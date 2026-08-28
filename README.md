# PkLibSharp

A C# port of the **implode** and **explode** methods of the PKWARE Data Compression Library, from
Ladislav Zezula's PKLib.

The C sources it was translated from — `implode.c`, `explode.c`, `crc32.c` and `pklib.h` — ship as
[`src/pklib`](https://github.com/ladislav-zezula/StormLib/tree/master/src/pklib) inside
[StormLib](https://github.com/ladislav-zezula/StormLib). This is a line-by-line translation of that
logic into idiomatic C#, not a reimplementation: it produces and consumes the same byte streams as
the original, which is the format used by MPQ archives among others.

Targets `net10.0`. No dependencies, no unsafe code.

## Getting a codec

`IPkLibCodec` exposes both algorithms behind one interface. Ask the factory for one:

```csharp
using PkLibSharp;

IPkLibCodec codec = PkLibCodecFactory.Default.Create();
```

Compression settings are per call, so one codec handles whatever you throw at it:

```csharp
byte[] packedText   = codec.Compress(text, CompressionType.Ascii);
byte[] packedBinary = codec.Compress(image, CompressionType.Binary, DictionarySize.Size2048);
byte[] packed       = codec.Compress(data);                          // binary, 4096 byte dictionary

byte[] restored = codec.Decompress(packed);

codec.Compress(sourceStream, destinationStream, CompressionType.Ascii);
codec.Decompress(sourceStream, destinationStream);
```

Only compression takes settings. `Decompress` reads the compression type and dictionary size out of
the stream, so it needs nothing from you and never has to agree with how you compressed.

The codec holds no state at all, so one instance is safe to share across threads and to register as
a singleton:

```csharp
services.AddSingleton<IPkLibCodecFactory, PkLibCodecFactory>();
services.AddSingleton<IPkLibCodec>(sp => sp.GetRequiredService<IPkLibCodecFactory>().Create());
```

Inject `IPkLibCodec` to compress and decompress, or `IPkLibCodecFactory` if you would rather create
codecs on demand. Both are straightforward to substitute in tests.

The static `Imploder`, `Exploder` and `Crc32` classes below remain available and are what the codec
forwards to; use them directly when you would rather not carry an instance around.

## Compressing

```csharp
using PkLibSharp;

byte[] compressed = Imploder.Compress(data);
byte[] compressed = Imploder.Compress(data, CompressionType.Ascii, DictionarySize.Size2048);

Imploder.Compress(sourceStream, destinationStream);
```

`CompressionType.Binary` encodes every literal in a fixed 9 bits and suits arbitrary data.
`CompressionType.Ascii` uses a static Huffman table tuned for English text: smaller for text, larger
for binary. `DictionarySize` controls how far back a repetition may reach; a larger dictionary finds
more matches but spends more bits on each distance.

## Decompressing

```csharp
byte[] original = Exploder.Decompress(compressed);

Exploder.Decompress(sourceStream, destinationStream);
```

`Decompress` throws `PkLibException` on malformed input, with the reason in its `Error` property.
`Exploder.TryDecompress` returns the `PkLibError` instead of throwing.

The compression type and dictionary size are recorded in the stream, so decompression takes no options.

## Custom sources and destinations

Both algorithms are streaming; the memory and `Stream` overloads are thin wrappers over a pair of
callbacks that you can supply directly:

```csharp
Imploder.Compress(
    buffer => ReadUpTo(buffer),       // returns 0 once the source is exhausted
    buffer => Consume(buffer));       // called repeatedly with compressed blocks
```

Working memory is about 36 KB per compression and 12 KB per decompression regardless of stream size,
matching the fixed work buffers the C library required its callers to allocate.

## Checksums

```csharp
uint crc = Crc32.Compute(data);
uint crc = Crc32.Compute(moreData, crc);   // resumable
```

This is the CRC-32 routine that shipped with PKLib. It uses the standard polynomial table but is
neither pre- nor post-inverted, so it starts from zero and its results **do not** match zlib or
`System.IO.Hashing.Crc32`. It is here for compatibility with data produced by the original library.

## Notes on fidelity to the C original

- Output is byte for byte identical to the C library for the inputs tested, including the choice of
  repetition at each position.
- Compressing an empty input yields a four byte stream (header plus end of stream marker).
  `Exploder` rejects streams of four bytes or fewer, as the original does, so empty input does not
  round-trip. Special case it in calling code if you need to.
- The C compressor reads a byte or two past the end of valid data while hashing byte pairs and while
  measuring a repetition at the very end of the last block, and required callers to zero its work
  buffer to keep that deterministic. The port allocates that slack explicitly and it is always zero.
- `PkLibError` carries the original `CMP_*` result codes.
- One deliberate deviation: the decompressor loops its first read until the buffer is full or the
  source runs out. The C version called its read callback once and rejected the stream if that call
  returned four bytes or fewer, which made the check depend on how much the caller happened to hand
  over. A `Stream` may legitimately return a short read with more data still coming, so a stream
  arriving in small pieces would have been rejected as corrupt. Output is unaffected.

## License

MIT. See [LICENSE](../LICENSE).

- Copyright (c) 2026 Alyx Dallagiacomo
- Copyright (c) 1999-2013 Ladislav Zezula — PKLib, the original C implementation

The imploding and exploding algorithms are derived from PKLib, distributed as part of
[StormLib](https://github.com/ladislav-zezula/StormLib), which is also MIT licensed. Zezula's notice
is reproduced in full under **Third-party notices** in [LICENSE](../LICENSE), as the MIT terms
require.

## Tests

```bash
dotnet test
```

`PkLibSharp.Tests` mirrors the library's folder layout. The suite is 966 tests, most of them a
theory over 18 payloads crossed with all three dictionary sizes and both compression modes. Beyond
round-tripping, it pins the encoder and decoder to a stream assembled by hand from the format
definition, so a matching pair of bugs in the two halves cannot cancel out; checks the CRC against a
bitwise implementation written from the polynomial; fuzzes malformed input to confirm nothing but a
`PkLibError` ever comes back; and walks every input length across an internal block boundary.

## Layout

`PkLibCodecFactory` sits at the root because it is where you start; everything else is grouped by
what it does.

```
PkLibCodecFactory.cs      the entry point
Abstractions/             the contract: IPkLibCodec, IPkLibCodecFactory, the read and write delegates
Compression/              Imploder and the imploding engine
Decompression/            Exploder and the exploding engine
Checksums/                Crc32
Format/                   what the wire format is made of: CompressionType, DictionarySize, the code tables
Errors/                   PkLibError and PkLibException
Internal/                 implementation details: the codec, argument guards, memory adapters
```

Every type lives in the single `PkLibSharp` namespace regardless of folder, so one `using
PkLibSharp;` reaches the whole library. The folders organise the source, not the API.

Where each piece came from in the upstream PKLib sources:

| Folder | Ported from |
| --- | --- |
| `Compression/` | `implode.c` |
| `Decompression/` | `explode.c` |
| `Checksums/` | `crc32.c` |
| `Format/` | the enums in `pklib.h` and the shared code tables in `explode.c` |
| `Errors/` | the `CMP_*` result codes in `pklib.h` |
| `Abstractions/`, `Internal/`, `PkLibCodecFactory.cs` | no C equivalent |
