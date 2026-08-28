using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PkLibSharp;

/// <summary>
/// The PKWARE "implode" (compression) algorithm. One instance compresses exactly one stream.
/// </summary>
/// <remarks>
/// This is a managed port of <c>implode.c</c> from Ladislav Zezula's PKLib. All of the state that the
/// C implementation kept in the <c>TCmpStruct</c> work buffer lives in the fields of this class.
/// </remarks>
internal sealed class ImplodeEngine
{
    /// <summary>The longest repetition the format can encode.</summary>
    private const int MaxRepetitionLength = 0x204;

    /// <summary>The number of uncompressed bytes read from the source per pass.</summary>
    private const int BlockSize = 0x1000;

    /// <summary>The logical size of the work buffer: the dictionary, one block and one repetition.</summary>
    private const int WorkBufferSize = 0x2204;

    /// <summary>
    /// Extra slack past the end of the work buffer. Byte pair hashing reads one byte of lookahead, and
    /// a repetition search near the end of the final block may compare past the last valid byte. The C
    /// original relied on adjacent fields of its work structure for this and required the caller to zero
    /// the whole structure; the padding here is always zero, which makes the outcome deterministic.
    /// </summary>
    private const int WorkBufferPadding = MaxRepetitionLength + 4;

    /// <summary>The number of compressed bytes buffered before a flush.</summary>
    private const int OutputFlushThreshold = 0x800;

    /// <summary>The literal that marks the end of the compressed stream.</summary>
    private const int EndOfStreamLiteral = 0x305;

    /// <summary>The number of distinct byte pair hashes.</summary>
    private const int HashTableSize = 0x900;

    /// <summary>Sentinel used by the partial match table, matching <c>USHRT_MAX</c> in the C original.</summary>
    private const ushort NoMatch = ushort.MaxValue;

    private readonly PkReadCallback _read;
    private readonly PkWriteCallback _write;

    private readonly CompressionType _compressionType;
    private readonly int _dictionarySizeBytes;
    private readonly int _dictionaryBits;
    private readonly uint _dictionaryMask;

    private readonly byte[] _distanceBits = new byte[0x40];
    private readonly byte[] _distanceCodes = new byte[0x40];

    /// <summary>Bit length of the code emitted for each literal and repetition length.</summary>
    private readonly byte[] _literalBits = new byte[0x306];

    /// <summary>Bit pattern of the code emitted for each literal and repetition length.</summary>
    private readonly ushort[] _literalCodes = new ushort[0x306];

    /// <summary>
    /// A Knuth-Morris-Pratt style failure function over the current repetition, used to decide how far a
    /// candidate at a later offset must reach before it can improve on the repetition already found.
    /// </summary>
    private readonly ushort[] _partialMatchTable = new ushort[MaxRepetitionLength];

    /// <summary>For each byte pair hash, the index in <see cref="_hashOffsets"/> of its first occurrence.</summary>
    private readonly ushort[] _hashToIndex = new ushort[HashTableSize];

    /// <summary>Work buffer offsets of every byte pair, grouped by hash and ascending within each group.</summary>
    private readonly ushort[] _hashOffsets = new ushort[WorkBufferSize];

    private readonly byte[] _outputBuffer = new byte[OutputFlushThreshold + 2];
    private readonly byte[] _workBuffer = new byte[WorkBufferSize + WorkBufferPadding];

    /// <summary>Backward distance of the repetition most recently found, decreased by one.</summary>
    private uint _distance;

    private int _outputBytes;
    private int _outputBits;

    internal ImplodeEngine(PkReadCallback read, PkWriteCallback write, CompressionType compressionType, DictionarySize dictionarySize)
    {
        _read = read;
        _write = write;
        _dictionarySizeBytes = (int)dictionarySize;

        _compressionType = compressionType switch
        {
            CompressionType.Binary or CompressionType.Ascii => compressionType,
            _ => throw new ArgumentOutOfRangeException(nameof(compressionType), compressionType, "Unsupported compression type."),
        };

        (_dictionaryBits, _dictionaryMask) = dictionarySize switch
        {
            DictionarySize.Size1024 => (4, 0x0Fu),
            DictionarySize.Size2048 => (5, 0x1Fu),
            DictionarySize.Size4096 => (6, 0x3Fu),
            _ => throw new ArgumentOutOfRangeException(nameof(dictionarySize), dictionarySize, "Unsupported dictionary size."),
        };

        BuildLiteralTables();

        PkLibTables.DistCode.CopyTo(_distanceCodes);
        PkLibTables.DistBits.CopyTo(_distanceBits);
    }

    /// <summary>
    /// Compresses the whole stream, writing the result through the write callback.
    /// </summary>
    internal void Run() => WriteCompressedData();

    /// <summary>
    /// Hashes the byte pair starting at the given work buffer offset. The exact pair would be
    /// <c>buffer[0] | buffer[1] &lt;&lt; 8</c>, but this cheaper hash still separates unequal pairs well
    /// while keeping the index table an order of magnitude smaller.
    /// </summary>
    private int BytePairHash(int offset) => (_workBuffer[offset] * 4) + (_workBuffer[offset + 1] * 5);

    /// <summary>
    /// Fills the literal code tables for the selected compression type, followed by the codes for the
    /// 0x206 encodable repetition lengths.
    /// </summary>
    private void BuildLiteralTables()
    {
        int index = 0;

        if (_compressionType == CompressionType.Binary)
        {
            // Every literal costs the same 9 bits: a clear marker bit followed by the byte itself.
            for (; index < 0x100; index++)
            {
                _literalBits[index] = 9;
                _literalCodes[index] = (ushort)(index * 2);
            }
        }
        else
        {
            // Literals are Huffman coded, again shifted up by one to make room for the marker bit.
            for (; index < 0x100; index++)
            {
                _literalBits[index] = (byte)(PkLibTables.ChBitsAsc[index] + 1);
                _literalCodes[index] = (ushort)(PkLibTables.ChCodeAsc[index] * 2);
            }
        }

        // Each length code covers a run of lengths distinguished by a few extra low bits.
        for (int lengthCode = 0; lengthCode < 0x10; lengthCode++)
        {
            int extraBits = PkLibTables.ExLenBits[lengthCode];
            int codeBits = PkLibTables.LenBits[lengthCode];

            for (int extraValue = 0; extraValue < 1 << extraBits; extraValue++)
            {
                _literalBits[index] = (byte)(extraBits + codeBits + 1);
                _literalCodes[index] = (ushort)((extraValue << (codeBits + 1)) | (PkLibTables.LenCode[lengthCode] << 1) | 1);
                index++;
            }
        }
    }

    /// <summary>
    /// Indexes every byte pair in the given range of the work buffer, so that
    /// <see cref="FindRepetition"/> can enumerate earlier occurrences of any pair in ascending order.
    /// </summary>
    /// <param name="bufferBegin">The first work buffer offset to index.</param>
    /// <param name="bufferEnd">One past the last work buffer offset to index.</param>
    private void BuildHashTables(int bufferBegin, int bufferEnd)
    {
        // Local copies keep the array references in registers through the hot loops.
        byte[] buffer = _workBuffer;
        ushort[] hashToIndex = _hashToIndex;
        ushort[] hashOffsets = _hashOffsets;

        Array.Clear(hashToIndex);

        // Step 1: count how often each byte pair hash occurs.
        for (int offset = bufferBegin; offset < bufferEnd; offset++)
        {
            hashToIndex[(buffer[offset] * 4) + (buffer[offset + 1] * 5)]++;
        }

        // Step 2: turn the counts into a running total, so each entry holds the number of pairs
        // whose hash is less than or equal to its index.
        ushort totalSum = 0;
        for (int hash = 0; hash < hashToIndex.Length; hash++)
        {
            totalSum = (ushort)(totalSum + hashToIndex[hash]);
            hashToIndex[hash] = totalSum;
        }

        // Step 3: walking backwards and decrementing turns the running totals into the index of the
        // first occurrence of each hash, and leaves each hash's offsets in ascending order.
        for (int offset = bufferEnd - 1; offset >= bufferBegin; offset--)
        {
            int hash = (buffer[offset] * 4) + (buffer[offset + 1] * 5);
            hashOffsets[--hashToIndex[hash]] = (ushort)offset;
        }
    }

    /// <summary>
    /// Counts how many bytes match between two positions in the work buffer, up to
    /// <paramref name="maxLength"/> — the index of the first mismatch, exactly as a byte-at-a-time
    /// loop would find it. Overlapping ranges are fine: both sides are only read.
    /// </summary>
    /// <remarks>
    /// Compares eight bytes per step: the XOR of two 64-bit loads is zero when all eight bytes
    /// agree, and the trailing-zero count of a non-zero XOR locates the first differing byte. On
    /// x86-64 the JIT compiles this to unaligned 64-bit loads and the TZCNT instruction. Match
    /// candidates are capped at 0x204 bytes, short enough that this beats both the byte loop it
    /// replaces and a 32-byte AVX2 loop, whose setup costs more than it saves on typical matches.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MatchLength(byte[] buffer, int first, int second, int maxLength)
    {
        // The work buffer's zeroed padding keeps all comparisons in range; the clamp is defensive.
        int length = Math.Min(maxLength, buffer.Length - Math.Max(first, second));
        int index = 0;

        if (BitConverter.IsLittleEndian)
        {
            ref byte start = ref MemoryMarshal.GetArrayDataReference(buffer);

            while (index + sizeof(ulong) <= length)
            {
                ulong difference =
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, first + index)) ^
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, second + index));

                if (difference != 0)
                {
                    // On little-endian, the lowest differing byte is the first differing byte.
                    return index + (BitOperations.TrailingZeroCount(difference) >> 3);
                }

                index += sizeof(ulong);
            }
        }

        while (index < length && buffer[first + index] == buffer[second + index])
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Writes the buffered compressed bytes to the output and restarts the buffer, preserving the
    /// partially filled byte that straddles the flush boundary.
    /// </summary>
    private void FlushOutputBuffer()
    {
        _write(_outputBuffer.AsSpan(0, OutputFlushThreshold));

        byte overflowByte = _outputBuffer[OutputFlushThreshold];
        byte partialByte = _outputBuffer[_outputBytes];
        _outputBytes -= OutputFlushThreshold;

        Array.Clear(_outputBuffer);

        if (_outputBytes != 0)
        {
            _outputBuffer[0] = overflowByte;
        }

        if (_outputBits != 0)
        {
            _outputBuffer[_outputBytes] = partialByte;
        }
    }

    /// <summary>
    /// Appends the low <paramref name="bitCount"/> bits of <paramref name="bits"/> to the output,
    /// least significant bit first.
    /// </summary>
    /// <param name="bitCount">The number of bits to write, at most 16.</param>
    /// <param name="bits">The value holding the bits to write.</param>
    private void OutputBits(int bitCount, uint bits)
    {
        byte[] outputBuffer = _outputBuffer;
        int outputBits = _outputBits;
        int outputBytes = _outputBytes;

        // Codes can be up to 16 bits wide, but only 8 bits can cross into a new byte at a time, so
        // wider codes take two rounds. This was a recursive call in the original; unrolled into a
        // loop with the cursor fields kept in locals, since this runs for every literal emitted.
        while (true)
        {
            outputBuffer[outputBytes] |= (byte)(bits << outputBits);
            int totalBits = outputBits + Math.Min(bitCount, 8);

            if (totalBits > 8)
            {
                // Deliberately unmasked, as in the original: on a two-round code the spilled byte
                // briefly carries bits belonging to the second round, which then ORs the same
                // values over themselves.
                outputBuffer[outputBytes + 1] = (byte)(bits >> (8 - outputBits));
                outputBytes++;
                outputBits = totalBits & 7;
            }
            else
            {
                outputBits = totalBits & 7;

                if (outputBits == 0)
                {
                    outputBytes++;
                }
            }

            if (outputBytes >= OutputFlushThreshold)
            {
                _outputBytes = outputBytes;
                _outputBits = outputBits;
                FlushOutputBuffer();
                outputBytes = _outputBytes;
                outputBits = _outputBits;
            }

            if (bitCount <= 8)
            {
                break;
            }

            bits >>= 8;
            bitCount -= 8;
        }

        _outputBytes = outputBytes;
        _outputBits = outputBits;
    }

    /// <summary>
    /// Searches for the most recent earlier occurrence of the byte sequence at
    /// <paramref name="inputOffset"/> and stores its backward distance in <see cref="_distance"/>.
    /// </summary>
    /// <param name="inputOffset">The work buffer offset of the sequence to match.</param>
    /// <returns>The length of the repetition found, or zero if there is none worth encoding.</returns>
    private int FindRepetition(int inputOffset)
    {
        byte[] buffer = _workBuffer;
        ushort[] hashOffsets = _hashOffsets;

        // The chain scan below runs a handful of instructions per entry, so the three bounds checks
        // it would otherwise carry are a measurable share of the whole compression. The unchecked
        // reads are safe because every chain is terminated by the entry for inputOffset itself
        // (indexed before this call, and >= the repetition limit), so hashOffsetIndex never leaves
        // the table, and because every stored offset is at most 0x2204 while the buffer extends
        // MaxRepetitionLength + 4 bytes further than that.
        ref byte bufferRef = ref MemoryMarshal.GetArrayDataReference(buffer);
        ref ushort hashOffsetsRef = ref MemoryMarshal.GetArrayDataReference(hashOffsets);

        int hash = BytePairHash(inputOffset);
        int hashOffsetIndex = _hashToIndex[hash];

        // The lowest offset still reachable with the current dictionary size.
        ushort minimumHashOffset = (ushort)(inputOffset - _dictionarySizeBytes + 1);

        // Skip occurrences that have fallen out of the dictionary, and remember where to resume
        // next time this hash is looked up.
        if (Unsafe.Add(ref hashOffsetsRef, hashOffsetIndex) < minimumHashOffset)
        {
            while (Unsafe.Add(ref hashOffsetsRef, hashOffsetIndex) < minimumHashOffset)
            {
                hashOffsetIndex++;
            }

            _hashToIndex[hash] = (ushort)hashOffsetIndex;
        }

        // A repetition must start strictly before this offset.
        int repetitionLimit = inputOffset - 1;
        int previousRepetition = Unsafe.Add(ref hashOffsetsRef, hashOffsetIndex);

        if (previousRepetition >= repetitionLimit)
        {
            return 0;
        }

        int repetitionLength = 1;
        int equalByteCount = 0;

        // Hoisted out of the scan: the first byte of the sequence, and the byte a candidate must
        // carry at position repetitionLength - 1 to have any chance of beating the current best.
        byte firstByte = buffer[inputOffset];
        byte lastByte = firstByte;
        int lastByteOffset = 0;

        // Every occurrence in this chain has the same byte pair hash (b0 * 4 + b1 * 5), so once the
        // first bytes match, the second bytes must match as well — which is why two bytes can be
        // counted before measuring on from the third, exactly as the original does.
        while (true)
        {
            // Both prechecks are folded into one branchless test: XOR is zero only on equality, so
            // the OR is zero only when both bytes match. Two hard-to-predict branches become one
            // rarely-taken branch, which matters on data where half the candidates match the first
            // byte but few survive both checks.
            if (((Unsafe.Add(ref bufferRef, previousRepetition) ^ firstByte) |
                 (Unsafe.Add(ref bufferRef, previousRepetition + lastByteOffset) ^ lastByte)) == 0)
            {
                equalByteCount = 2 + MatchLength(buffer, previousRepetition + 2, inputOffset + 2, MaxRepetitionLength - 2);

                // Take any match at least as long as the best so far. Because the occurrences are
                // enumerated in ascending order, that yields the most recent one, whose smaller
                // distance costs fewer bits to encode.
                if (equalByteCount >= repetitionLength)
                {
                    _distance = (uint)(inputOffset - previousRepetition - 1);
                    repetitionLength = equalByteCount;

                    // Repetitions longer than 10 bytes are worth the extra search below.
                    if (repetitionLength > 10)
                    {
                        break;
                    }

                    lastByteOffset = repetitionLength - 1;
                    lastByte = buffer[inputOffset + lastByteOffset];
                }
            }

            previousRepetition = Unsafe.Add(ref hashOffsetsRef, ++hashOffsetIndex);

            if (previousRepetition >= repetitionLimit)
            {
                // A repetition of a single byte is never smaller than the literal itself.
                return repetitionLength >= 2 ? repetitionLength : 0;
            }
        }

        // The match is already as long as the format can encode. The distance needs no adjustment
        // here: unlike the original's advancing pointers, the arithmetic above never moved the
        // start of the match.
        if (equalByteCount == MaxRepetitionLength)
        {
            return equalByteCount;
        }

        if (hashOffsets[hashOffsetIndex + 1] >= repetitionLimit)
        {
            return repetitionLength;
        }

        return ExtendRepetition(inputOffset, hashOffsetIndex, repetitionLimit, repetitionLength);
    }

    /// <summary>
    /// Looks for a longer repetition among the remaining occurrences of the byte pair. A later
    /// occurrence can be a better match even after a long one has been found, for example in
    /// <c>"EEEE...EEEEQQQQ" + "XYZ" + "EEEE...EEEEQQQQ"</c>, where every offset in the first run of
    /// <c>E</c> matches, but only the last one also carries the trailing run of <c>Q</c>.
    /// </summary>
    /// <param name="inputOffset">The work buffer offset of the sequence to match.</param>
    /// <param name="hashOffsetIndex">The index of the occurrence that produced the current match.</param>
    /// <param name="repetitionLimit">The offset before which a repetition must start.</param>
    /// <param name="repetitionLength">The length of the current match.</param>
    /// <returns>The length of the best repetition found.</returns>
    private int ExtendRepetition(int inputOffset, int hashOffsetIndex, int repetitionLimit, int repetitionLength)
    {
        byte[] buffer = _workBuffer;
        ushort[] hashOffsets = _hashOffsets;
        ushort[] partialMatchTable = _partialMatchTable;

        // Unchecked reads under the same guarantees as in FindRepetition: the chain terminator
        // bounds the index, and stored offsets sit well inside the padded buffer.
        ref byte bufferRef = ref MemoryMarshal.GetArrayDataReference(buffer);
        ref ushort hashOffsetsRef = ref MemoryMarshal.GetArrayDataReference(hashOffsets);

        partialMatchTable[0] = NoMatch;
        partialMatchTable[1] = 0;

        ushort prefixLength = 0;
        int matchedPrefix = 1;

        while (matchedPrefix < repetitionLength)
        {
            if (buffer[inputOffset + matchedPrefix] != buffer[inputOffset + prefixLength])
            {
                prefixLength = partialMatchTable[prefixLength];

                if (prefixLength != NoMatch)
                {
                    continue;
                }
            }

            // Wraps from NoMatch back to zero, matching the unsigned short arithmetic of the original.
            prefixLength = (ushort)(prefixLength + 1);
            partialMatchTable[++matchedPrefix] = prefixLength;
        }

        int previousRepetition = hashOffsets[hashOffsetIndex];
        int previousRepetitionEnd = previousRepetition + repetitionLength;
        int candidateLength = repetitionLength;

        while (true)
        {
            candidateLength = partialMatchTable[candidateLength];

            if (candidateLength == NoMatch)
            {
                candidateLength = 0;
            }

            // Skip occurrences too far back to reach the end of the match already found.
            do
            {
                previousRepetition = Unsafe.Add(ref hashOffsetsRef, ++hashOffsetIndex);

                if (previousRepetition >= repetitionLimit)
                {
                    return repetitionLength;
                }
            }
            while (previousRepetition + candidateLength < previousRepetitionEnd);

            int preLastByteOffset = repetitionLength - 2;
            byte preLastByte = buffer[inputOffset + preLastByteOffset];

            if (preLastByte == Unsafe.Add(ref bufferRef, previousRepetition + preLastByteOffset))
            {
                // The candidate reaches past the end of the previous match, so start measuring afresh.
                if (previousRepetition + candidateLength != previousRepetitionEnd)
                {
                    previousRepetitionEnd = previousRepetition;
                    candidateLength = 0;
                }
            }
            else
            {
                byte firstByte = buffer[inputOffset];

                // Find an occurrence whose first and last but one bytes both match.
                do
                {
                    previousRepetition = Unsafe.Add(ref hashOffsetsRef, ++hashOffsetIndex);

                    if (previousRepetition >= repetitionLimit)
                    {
                        return repetitionLength;
                    }
                }
                while (((Unsafe.Add(ref bufferRef, previousRepetition + preLastByteOffset) ^ preLastByte) |
                        (Unsafe.Add(ref bufferRef, previousRepetition) ^ firstByte)) != 0);

                previousRepetitionEnd = previousRepetition + 2;
                candidateLength = 2;
            }

            // Measure how far the candidate agrees with the input. The byte-at-a-time loop this
            // replaces advanced previousRepetitionEnd one step less when the cap was hit, but the
            // capped case returns below before previousRepetitionEnd is read again.
            int matched = MatchLength(buffer, previousRepetitionEnd, inputOffset + candidateLength, MaxRepetitionLength - candidateLength);
            candidateLength += matched;
            previousRepetitionEnd += matched;

            if (candidateLength < repetitionLength)
            {
                continue;
            }

            _distance = (uint)(inputOffset - previousRepetition - 1);
            repetitionLength = candidateLength;

            if (repetitionLength == MaxRepetitionLength)
            {
                return repetitionLength;
            }

            // Extend the failure function to cover the now longer match.
            while (matchedPrefix < candidateLength)
            {
                if (buffer[inputOffset + matchedPrefix] != buffer[inputOffset + prefixLength])
                {
                    prefixLength = partialMatchTable[prefixLength];

                    if (prefixLength != NoMatch)
                    {
                        continue;
                    }
                }

                prefixLength = (ushort)(prefixLength + 1);
                partialMatchTable[++matchedPrefix] = prefixLength;
            }
        }
    }

    /// <summary>
    /// Emits the code for a repetition of the given length at the current <see cref="_distance"/>.
    /// </summary>
    private void OutputRepetition(int repetitionLength)
    {
        OutputBits(_literalBits[repetitionLength + 0xFE], _literalCodes[repetitionLength + 0xFE]);

        if (repetitionLength == 2)
        {
            // Two byte repetitions reach at most 0x100 bytes back, so only 2 low bits are stored.
            OutputBits(_distanceBits[_distance >> 2], _distanceCodes[_distance >> 2]);
            OutputBits(2, _distance & 3);
        }
        else
        {
            OutputBits(_distanceBits[_distance >> _dictionaryBits], _distanceCodes[_distance >> _dictionaryBits]);
            OutputBits(_dictionaryBits, _distance & _dictionaryMask);
        }
    }

    /// <summary>
    /// Emits the code for a single uncompressed byte at the given work buffer offset.
    /// </summary>
    private void OutputLiteral(int inputOffset)
    {
        byte value = _workBuffer[inputOffset];
        OutputBits(_literalBits[value], _literalCodes[value]);
    }

    /// <summary>
    /// Reads the source one block at a time, compresses each block and writes the resulting stream.
    /// </summary>
    private void WriteCompressedData()
    {
        // The dictionary occupies the start of the work buffer, so incoming data starts behind it.
        int inputOffset = _dictionarySizeBytes + MaxRepetitionLength;

        // The header is the compression type and the dictionary size, both stored as whole bytes.
        _outputBuffer[0] = (byte)_compressionType;
        _outputBuffer[1] = (byte)_dictionaryBits;
        _outputBytes = 2;
        _outputBits = 0;

        bool inputEnded = false;
        int phase = 0;

        while (!inputEnded)
        {
            int bytesToLoad = BlockSize;
            int totalLoaded = 0;
            bool sourceWasEmpty = false;

            while (bytesToLoad != 0)
            {
                int bytesLoaded = _read(_workBuffer.AsSpan(_dictionarySizeBytes + MaxRepetitionLength + totalLoaded, bytesToLoad));

                if (bytesLoaded == 0)
                {
                    // Nothing at all was ever read, so there is nothing to compress.
                    sourceWasEmpty = totalLoaded == 0 && phase == 0;
                    inputEnded = true;
                    break;
                }

                bytesToLoad -= bytesLoaded;
                totalLoaded += bytesLoaded;
            }

            if (sourceWasEmpty)
            {
                break;
            }

            // Everything up to this offset can be compressed. On all but the last pass a whole
            // repetition is held back, because it may still grow into the next block.
            int inputEnd = _dictionarySizeBytes + totalLoaded;

            if (inputEnded)
            {
                inputEnd += MaxRepetitionLength;
            }

            // Index the new block along with as much already compressed data as the dictionary covers:
            // the first pass has no history, the second has at most one block of it, and from the third
            // pass on the whole dictionary is populated.
            int indexBegin;

            switch (phase)
            {
                case 0:
                    indexBegin = inputOffset;
                    phase = _dictionarySizeBytes == BlockSize ? 1 : 2;
                    break;

                case 1:
                    indexBegin = inputOffset - _dictionarySizeBytes + MaxRepetitionLength;
                    phase = 2;
                    break;

                default:
                    indexBegin = inputOffset - _dictionarySizeBytes;
                    break;
            }

            BuildHashTables(indexBegin, inputEnd + 1);
            CompressBlock(ref inputOffset, inputEnd, inputEnded);

            if (!inputEnded)
            {
                // Slide the window down by one block, keeping the dictionary and the held back tail.
                inputOffset -= BlockSize;
                Array.Copy(_workBuffer, BlockSize, _workBuffer, 0, _dictionarySizeBytes + MaxRepetitionLength);
            }
        }

        OutputBits(_literalBits[EndOfStreamLiteral], _literalCodes[EndOfStreamLiteral]);

        if (_outputBits != 0)
        {
            _outputBytes++;
        }

        _write(_outputBuffer.AsSpan(0, _outputBytes));
    }

    /// <summary>
    /// Compresses the work buffer from <paramref name="inputOffset"/> up to <paramref name="inputEnd"/>.
    /// </summary>
    /// <param name="inputOffset">On entry the first offset to compress; on return the first offset not compressed.</param>
    /// <param name="inputEnd">One past the last offset that may be compressed.</param>
    /// <param name="inputEnded">Whether the source is exhausted, so no repetition may cross <paramref name="inputEnd"/>.</param>
    private void CompressBlock(ref int inputOffset, int inputEnd, bool inputEnded)
    {
        while (inputOffset < inputEnd)
        {
            int repetitionLength = FindRepetition(inputOffset);
            bool repetitionEmitted = false;

            while (repetitionLength != 0)
            {
                // Encoding a distance of 0x100 or more takes more room than the two bytes themselves.
                if (repetitionLength == 2 && _distance >= 0x100)
                {
                    break;
                }

                bool emitNow;

                if (inputEnded && inputOffset + repetitionLength > inputEnd)
                {
                    // A repetition may not run past the end of the input, so shorten it to fit.
                    repetitionLength = inputEnd - inputOffset;

                    if (repetitionLength < 2 || (repetitionLength == 2 && _distance >= 0x100))
                    {
                        break;
                    }

                    emitNow = true;
                }
                else
                {
                    emitNow = repetitionLength >= 8 || inputOffset + 1 >= inputEnd;
                }

                if (!emitNow)
                {
                    // A repetition starting one byte later may be longer. For "ARROCKFORT" followed by
                    // "AROCKFORT", the match at the second string is "AR", but "ROCKFORT" starts one
                    // byte later and is far longer.
                    int savedLength = repetitionLength;
                    uint savedDistance = _distance;

                    repetitionLength = FindRepetition(inputOffset + 1);

                    // Only give up the current repetition if the later one gains more than the extra
                    // literal costs, or if the current one is far enough back to be expensive anyway.
                    if (repetitionLength > savedLength &&
                        (repetitionLength > savedLength + 1 || savedDistance > 0x80))
                    {
                        OutputLiteral(inputOffset);
                        inputOffset++;
                        continue;
                    }

                    repetitionLength = savedLength;
                    _distance = savedDistance;
                }

                OutputRepetition(repetitionLength);
                inputOffset += repetitionLength;
                repetitionEmitted = true;
                break;
            }

            if (!repetitionEmitted)
            {
                OutputLiteral(inputOffset);
                inputOffset++;
            }
        }
    }
}
