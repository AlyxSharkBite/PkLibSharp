namespace PkLibSharp;

/// <summary>
/// The PKWARE "explode" (decompression) algorithm. One instance decompresses exactly one stream.
/// </summary>
/// <remarks>
/// This is a managed port of <c>explode.c</c> from Ladislav Zezula's PKLib. All of the state that the
/// C implementation kept in the <c>TDcmpStruct</c> work buffer lives in the fields of this class.
/// </remarks>
internal sealed class ExplodeEngine
{
    /// <summary>The size of the circular output buffer.</summary>
    /// <remarks>
    /// 0x0000-0x0FFF holds previously decompressed data kept for repetitions, 0x1000-0x1FFF holds the
    /// data being decompressed, and 0x2000-0x2203 is reserve space for the longest possible repetition.
    /// </remarks>
    private const int OutputBufferSize = 0x2204;

    /// <summary>The offset in the output buffer at which newly decompressed data starts.</summary>
    private const int OutputStart = 0x1000;

    /// <summary>The output position at which the buffer is flushed and wrapped around.</summary>
    private const int OutputFlushThreshold = 0x2000;

    private const int InputBufferSize = 0x800;

    /// <summary>The literal value that marks the end of the compressed stream.</summary>
    private const uint LiteralEndOfStream = 0x305;

    /// <summary>The literal value returned when the stream is truncated or malformed.</summary>
    private const uint LiteralError = 0x306;

    private readonly PkReadCallback _read;
    private readonly PkWriteCallback _write;

    private readonly byte[] _outputBuffer = new byte[OutputBufferSize];
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];

    private readonly byte[] _distPositionCodes = new byte[0x100];
    private readonly byte[] _lengthCodes = new byte[0x100];

    // Lookup tables used by ASCII mode. A literal is identified by the low 8 bits of the bit buffer;
    // when those 8 bits are ambiguous, 4, 6 or 8 bits are consumed and a wider table is consulted.
    private readonly byte[] _asciiLiteralsByLowByte = new byte[0x100];
    private readonly byte[] _asciiLiteralsAfter4Bits = new byte[0x100];
    private readonly byte[] _asciiLiteralsAfter6Bits = new byte[0x80];
    private readonly byte[] _asciiLiteralsAfter8Bits = new byte[0x100];

    // Working copies of the static tables. GenerateAsciiTables rewrites entries of _asciiLiteralBits,
    // so the shared table must not be modified in place.
    private readonly byte[] _asciiLiteralBits = new byte[0x100];
    private readonly byte[] _distanceBits = new byte[0x40];
    private readonly byte[] _lengthBits = new byte[0x10];
    private readonly byte[] _extraLengthBits = new byte[0x10];
    private readonly ushort[] _lengthBase = new ushort[0x10];

    private CompressionType _compressionType;
    private int _dictionaryBits;
    private uint _dictionaryMask;

    /// <summary>A rolling window of up to 16 bits of input, with the next bit to consume in bit 0.</summary>
    private uint _bitBuffer;

    /// <summary>The number of bits above the low 8 that are currently valid in <see cref="_bitBuffer"/>.</summary>
    private int _extraBits;

    private int _inputPosition;
    private int _inputBytes;
    private int _outputPosition;

    internal ExplodeEngine(PkReadCallback read, PkWriteCallback write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>
    /// Decompresses the whole stream, writing the result through the write callback.
    /// </summary>
    /// <returns><see cref="PkLibError.None"/> on success, otherwise the reason the stream was rejected.</returns>
    internal PkLibError Run()
    {
        // The header is three bytes: compression type, dictionary size and the first bits of data.
        _inputBytes = FillInputBuffer();
        if (_inputBytes <= 4)
        {
            return PkLibError.BadData;
        }

        _compressionType = (CompressionType)_inputBuffer[0];
        _dictionaryBits = _inputBuffer[1];
        _bitBuffer = _inputBuffer[2];
        _extraBits = 0;
        _inputPosition = 3;

        if (_dictionaryBits is < 4 or > 6)
        {
            return PkLibError.InvalidDictionarySize;
        }

        _dictionaryMask = 0xFFFFu >> (16 - _dictionaryBits);

        if (_compressionType != CompressionType.Binary)
        {
            if (_compressionType != CompressionType.Ascii)
            {
                return PkLibError.InvalidMode;
            }

            PkLibTables.ChBitsAsc.CopyTo(_asciiLiteralBits);
            GenerateAsciiTables();
        }

        PkLibTables.LenBits.CopyTo(_lengthBits);
        PkLibTables.ExLenBits.CopyTo(_extraLengthBits);
        PkLibTables.LenBase.CopyTo(_lengthBase);
        PkLibTables.DistBits.CopyTo(_distanceBits);

        GenerateDecodeTables(_lengthCodes, PkLibTables.LenCode, PkLibTables.LenBits);
        GenerateDecodeTables(_distPositionCodes, PkLibTables.DistCode, PkLibTables.DistBits);

        return Expand() != LiteralError ? PkLibError.None : PkLibError.Aborted;
    }

    /// <summary>
    /// Builds a table that maps the next 8 bits of input to the index of the code they begin with.
    /// </summary>
    /// <param name="positions">The table to fill.</param>
    /// <param name="startIndexes">The bit pattern of each code.</param>
    /// <param name="lengthBits">The number of bits in each code.</param>
    private static void GenerateDecodeTables(Span<byte> positions, ReadOnlySpan<byte> startIndexes, ReadOnlySpan<byte> lengthBits)
    {
        for (int i = 0; i < lengthBits.Length; i++)
        {
            // Every index whose low bits match the code maps back to this code.
            int step = 1 << lengthBits[i];

            for (int index = startIndexes[i]; index < 0x100; index += step)
            {
                positions[index] = (byte)i;
            }
        }
    }

    /// <summary>
    /// Builds the four ASCII decoding tables from the static ASCII code table. Codes longer than
    /// 8 bits cannot be resolved from a single byte, so their remaining bits are decoded from a
    /// secondary table and <see cref="_asciiLiteralBits"/> is reduced by the bits consumed to reach it.
    /// </summary>
    private void GenerateAsciiTables()
    {
        ReadOnlySpan<ushort> asciiCodes = PkLibTables.ChCodeAsc;

        for (int literal = 0xFF; literal >= 0; literal--)
        {
            ushort code = asciiCodes[literal];
            int bits = _asciiLiteralBits[literal];
            uint accumulator;
            uint step;

            if (bits <= 8)
            {
                // The code fits in one byte, so it can be resolved directly.
                step = 1u << bits;
                accumulator = code;

                do
                {
                    _asciiLiteralsByLowByte[accumulator] = (byte)literal;
                    accumulator += step;
                }
                while (accumulator < 0x100);
            }
            else if ((accumulator = (uint)(code & 0xFF)) != 0)
            {
                // Mark the low byte as ambiguous; the decoder will consume more bits and try again.
                _asciiLiteralsByLowByte[accumulator] = 0xFF;

                if ((code & 0x3F) != 0)
                {
                    bits -= 4;
                    _asciiLiteralBits[literal] = (byte)bits;

                    step = 1u << bits;
                    accumulator = (uint)(code >> 4);

                    do
                    {
                        _asciiLiteralsAfter4Bits[accumulator] = (byte)literal;
                        accumulator += step;
                    }
                    while (accumulator < 0x100);
                }
                else
                {
                    bits -= 6;
                    _asciiLiteralBits[literal] = (byte)bits;

                    step = 1u << bits;
                    accumulator = (uint)(code >> 6);

                    do
                    {
                        _asciiLiteralsAfter6Bits[accumulator] = (byte)literal;
                        accumulator += step;
                    }
                    while (accumulator < 0x80);
                }
            }
            else
            {
                // The low byte of the code is zero, so the literal is identified by the high bits alone.
                bits -= 8;
                _asciiLiteralBits[literal] = (byte)bits;

                step = 1u << bits;
                accumulator = (uint)(code >> 8);

                do
                {
                    _asciiLiteralsAfter8Bits[accumulator] = (byte)literal;
                    accumulator += step;
                }
                while (accumulator < 0x100);
            }
        }
    }

    /// <summary>
    /// Reads until the input buffer is full or the source is exhausted.
    /// </summary>
    /// <remarks>
    /// The C original called its read callback once and treated a short read as the whole of the
    /// remaining input, which made the "is this stream long enough to hold a header" check below
    /// depend on how much the caller happened to return. A <see cref="Stream"/> is entitled to
    /// return fewer bytes than asked for while more are still coming, so the read is looped here.
    /// For a caller that fills the buffer, which is what the C library assumed, nothing changes.
    /// </remarks>
    /// <returns>The number of bytes read, which is less than the buffer only at the end of the source.</returns>
    private int FillInputBuffer()
    {
        int totalRead = 0;

        while (totalRead < _inputBuffer.Length)
        {
            int bytesRead = _read(_inputBuffer.AsSpan(totalRead));

            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    /// <summary>
    /// Discards the given number of bits from the bit buffer, refilling it from the input when needed.
    /// </summary>
    /// <param name="count">The number of bits to discard.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> if the input is exhausted.</returns>
    private bool WasteBits(int count)
    {
        // Fast path: the buffer already holds enough bits above the low 8.
        if (count <= _extraBits)
        {
            _extraBits -= count;
            _bitBuffer >>= count;
            return true;
        }

        _bitBuffer >>= _extraBits;

        if (_inputPosition == _inputBytes)
        {
            _inputBytes = FillInputBuffer();
            if (_inputBytes == 0)
            {
                return false;
            }

            _inputPosition = 0;
        }

        _bitBuffer |= (uint)_inputBuffer[_inputPosition++] << 8;
        _bitBuffer >>= count - _extraBits;
        _extraBits = _extraBits - count + 8;
        return true;
    }

    /// <summary>
    /// Decodes the next literal from the compressed data.
    /// </summary>
    /// <returns>
    /// 0x000-0x0FF for a single uncompressed byte, 0x100-0x304 for a repetition of 2 to 0x206 bytes,
    /// <see cref="LiteralEndOfStream"/> for the end of the stream, or <see cref="LiteralError"/> on failure.
    /// </returns>
    private uint DecodeLiteral()
    {
        // A set low bit introduces a repetition; a clear low bit introduces an uncompressed byte.
        if ((_bitBuffer & 1) != 0)
        {
            if (!WasteBits(1))
            {
                return LiteralError;
            }

            // The next 8 bits hold the index into the length code table.
            uint lengthCode = _lengthCodes[_bitBuffer & 0xFF];

            if (!WasteBits(_lengthBits[lengthCode]))
            {
                return LiteralError;
            }

            int extraBitCount = _extraLengthBits[lengthCode];
            if (extraBitCount != 0)
            {
                uint extraLength = _bitBuffer & ((1u << extraBitCount) - 1);

                // Running out of input is tolerated for the very last (longest) code, which the
                // encoder may emit without any trailing padding.
                if (!WasteBits(extraBitCount) && lengthCode + extraLength != 0x10E)
                {
                    return LiteralError;
                }

                lengthCode = _lengthBase[lengthCode] + extraLength;
            }

            // Offset repetition lengths by 0x100 so they can be told apart from uncompressed bytes.
            return lengthCode + 0x100;
        }

        if (!WasteBits(1))
        {
            return LiteralError;
        }

        if (_compressionType == CompressionType.Binary)
        {
            uint uncompressedByte = _bitBuffer & 0xFF;
            return WasteBits(8) ? uncompressedByte : LiteralError;
        }

        uint literal;
        if ((_bitBuffer & 0xFF) != 0)
        {
            literal = _asciiLiteralsByLowByte[_bitBuffer & 0xFF];

            if (literal == 0xFF)
            {
                // The low byte was ambiguous, so consume the shared prefix and use a wider table.
                if ((_bitBuffer & 0x3F) != 0)
                {
                    if (!WasteBits(4))
                    {
                        return LiteralError;
                    }

                    literal = _asciiLiteralsAfter4Bits[_bitBuffer & 0xFF];
                }
                else
                {
                    if (!WasteBits(6))
                    {
                        return LiteralError;
                    }

                    literal = _asciiLiteralsAfter6Bits[_bitBuffer & 0x7F];
                }
            }
        }
        else
        {
            if (!WasteBits(8))
            {
                return LiteralError;
            }

            literal = _asciiLiteralsAfter8Bits[_bitBuffer & 0xFF];
        }

        return WasteBits(_asciiLiteralBits[literal]) ? literal : LiteralError;
    }

    /// <summary>
    /// Decodes how far back in the output buffer a repetition starts.
    /// </summary>
    /// <param name="repetitionLength">The already decoded length of the repetition.</param>
    /// <returns>The backward distance, or zero if the input is exhausted.</returns>
    private int DecodeDistance(int repetitionLength)
    {
        // The next 2-8 bits are the distance position code.
        int distPositionCode = _distPositionCodes[_bitBuffer & 0xFF];

        if (!WasteBits(_distanceBits[distPositionCode]))
        {
            return 0;
        }

        int distance;
        if (repetitionLength == 2)
        {
            // Two byte repetitions only ever reach 0x100 bytes back, so 2 extra bits suffice.
            distance = (distPositionCode << 2) | (int)(_bitBuffer & 0x03);

            if (!WasteBits(2))
            {
                return 0;
            }
        }
        else
        {
            distance = (distPositionCode << _dictionaryBits) | (int)(_bitBuffer & _dictionaryMask);

            if (!WasteBits(_dictionaryBits))
            {
                return 0;
            }
        }

        return distance + 1;
    }

    /// <summary>
    /// Decodes literals until the end of the stream is reached, flushing the output buffer as it fills.
    /// </summary>
    /// <returns>The last literal decoded, which is <see cref="LiteralError"/> if the stream was malformed.</returns>
    private uint Expand()
    {
        _outputPosition = OutputStart;

        uint literal;
        uint result;

        while ((result = literal = DecodeLiteral()) < LiteralEndOfStream)
        {
            if (literal >= 0x100)
            {
                // Literals from 0x100 up encode the length of a repeating sequence, where 0x100
                // means 2 bytes, 0x101 means 3 bytes, and so on.
                int repetitionLength = (int)literal - 0xFE;
                int backwardDistance = DecodeDistance(repetitionLength);

                if (backwardDistance == 0)
                {
                    result = LiteralError;
                    break;
                }

                int target = _outputPosition;
                int source = target - backwardDistance;
                _outputPosition += repetitionLength;

                // Copied one byte at a time on purpose: the source may overlap the target, in which
                // case the bytes written are meant to be re-read as the copy proceeds.
                while (repetitionLength-- > 0)
                {
                    _outputBuffer[target++] = _outputBuffer[source++];
                }
            }
            else
            {
                _outputBuffer[_outputPosition++] = (byte)literal;
            }

            if (_outputPosition >= OutputFlushThreshold)
            {
                _write(_outputBuffer.AsSpan(OutputStart, OutputStart));

                // Move the just-flushed data down so it stays available as a repetition source.
                // Anything decoded past the threshold moves with it.
                Array.Copy(_outputBuffer, OutputStart, _outputBuffer, 0, _outputPosition - OutputStart);
                _outputPosition -= OutputStart;
            }
        }

        _write(_outputBuffer.AsSpan(OutputStart, _outputPosition - OutputStart));
        return result;
    }
}
