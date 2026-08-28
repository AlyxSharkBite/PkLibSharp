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
        Array.Clear(_hashToIndex);

        // Step 1: count how often each byte pair hash occurs.
        for (int offset = bufferBegin; offset < bufferEnd; offset++)
        {
            _hashToIndex[BytePairHash(offset)]++;
        }

        // Step 2: turn the counts into a running total, so each entry holds the number of pairs
        // whose hash is less than or equal to its index.
        ushort totalSum = 0;
        for (int hash = 0; hash < _hashToIndex.Length; hash++)
        {
            totalSum = (ushort)(totalSum + _hashToIndex[hash]);
            _hashToIndex[hash] = totalSum;
        }

        // Step 3: walking backwards and decrementing turns the running totals into the index of the
        // first occurrence of each hash, and leaves each hash's offsets in ascending order.
        for (int offset = bufferEnd - 1; offset >= bufferBegin; offset--)
        {
            int hash = BytePairHash(offset);
            _hashOffsets[--_hashToIndex[hash]] = (ushort)offset;
        }
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
        // Codes can be up to 16 bits wide, but only 8 bits can cross into a new byte at a time.
        if (bitCount > 8)
        {
            OutputBits(8, bits);
            bits >>= 8;
            bitCount -= 8;
        }

        int bitsAlreadyUsed = _outputBits;
        _outputBuffer[_outputBytes] |= (byte)(bits << bitsAlreadyUsed);
        _outputBits += bitCount;

        if (_outputBits > 8)
        {
            _outputBytes++;
            bits >>= 8 - bitsAlreadyUsed;
            _outputBuffer[_outputBytes] = (byte)bits;
            _outputBits &= 7;
        }
        else
        {
            _outputBits &= 7;

            if (_outputBits == 0)
            {
                _outputBytes++;
            }
        }

        if (_outputBytes >= OutputFlushThreshold)
        {
            FlushOutputBuffer();
        }
    }

    /// <summary>
    /// Searches for the most recent earlier occurrence of the byte sequence at
    /// <paramref name="inputOffset"/> and stores its backward distance in <see cref="_distance"/>.
    /// </summary>
    /// <param name="inputOffset">The work buffer offset of the sequence to match.</param>
    /// <returns>The length of the repetition found, or zero if there is none worth encoding.</returns>
    private int FindRepetition(int inputOffset)
    {
        int hash = BytePairHash(inputOffset);
        int hashOffsetIndex = _hashToIndex[hash];

        // The lowest offset still reachable with the current dictionary size.
        ushort minimumHashOffset = (ushort)(inputOffset - _dictionarySizeBytes + 1);

        // Skip occurrences that have fallen out of the dictionary, and remember where to resume
        // next time this hash is looked up.
        if (_hashOffsets[hashOffsetIndex] < minimumHashOffset)
        {
            while (_hashOffsets[hashOffsetIndex] < minimumHashOffset)
            {
                hashOffsetIndex++;
            }

            _hashToIndex[hash] = (ushort)hashOffsetIndex;
        }

        // A repetition must start strictly before this offset.
        int repetitionLimit = inputOffset - 1;
        int previousRepetition = _hashOffsets[hashOffsetIndex];

        if (previousRepetition >= repetitionLimit)
        {
            return 0;
        }

        int repetitionLength = 1;
        int equalByteCount = 0;

        // A matching hash is not a matching byte pair, so compare the bytes and measure the match.
        while (true)
        {
            if (_workBuffer[inputOffset] == _workBuffer[previousRepetition] &&
                _workBuffer[inputOffset + repetitionLength - 1] == _workBuffer[previousRepetition + repetitionLength - 1])
            {
                int comparePosition = inputOffset + 1;
                previousRepetition++;
                equalByteCount = 2;

                while (equalByteCount < MaxRepetitionLength)
                {
                    previousRepetition++;
                    comparePosition++;

                    if (_workBuffer[previousRepetition] != _workBuffer[comparePosition])
                    {
                        break;
                    }

                    equalByteCount++;
                }

                // Take any match at least as long as the best so far. Because the occurrences are
                // enumerated in ascending order, that yields the most recent one, whose smaller
                // distance costs fewer bits to encode.
                if (equalByteCount >= repetitionLength)
                {
                    _distance = (uint)(inputOffset - previousRepetition + equalByteCount - 1);
                    repetitionLength = equalByteCount;

                    // Repetitions longer than 10 bytes are worth the extra search below.
                    if (repetitionLength > 10)
                    {
                        break;
                    }
                }
            }

            previousRepetition = _hashOffsets[++hashOffsetIndex];

            if (previousRepetition >= repetitionLimit)
            {
                // A repetition of a single byte is never smaller than the literal itself.
                return repetitionLength >= 2 ? repetitionLength : 0;
            }
        }

        // The match is already as long as the format can encode.
        if (equalByteCount == MaxRepetitionLength)
        {
            _distance--;
            return equalByteCount;
        }

        if (_hashOffsets[hashOffsetIndex + 1] >= repetitionLimit)
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
        _partialMatchTable[0] = NoMatch;
        _partialMatchTable[1] = 0;

        ushort prefixLength = 0;
        int matchedPrefix = 1;

        while (matchedPrefix < repetitionLength)
        {
            if (_workBuffer[inputOffset + matchedPrefix] != _workBuffer[inputOffset + prefixLength])
            {
                prefixLength = _partialMatchTable[prefixLength];

                if (prefixLength != NoMatch)
                {
                    continue;
                }
            }

            // Wraps from NoMatch back to zero, matching the unsigned short arithmetic of the original.
            prefixLength = (ushort)(prefixLength + 1);
            _partialMatchTable[++matchedPrefix] = prefixLength;
        }

        int previousRepetition = _hashOffsets[hashOffsetIndex];
        int previousRepetitionEnd = previousRepetition + repetitionLength;
        int candidateLength = repetitionLength;

        while (true)
        {
            candidateLength = _partialMatchTable[candidateLength];

            if (candidateLength == NoMatch)
            {
                candidateLength = 0;
            }

            // Skip occurrences too far back to reach the end of the match already found.
            do
            {
                previousRepetition = _hashOffsets[++hashOffsetIndex];

                if (previousRepetition >= repetitionLimit)
                {
                    return repetitionLength;
                }
            }
            while (previousRepetition + candidateLength < previousRepetitionEnd);

            byte preLastByte = _workBuffer[inputOffset + repetitionLength - 2];

            if (preLastByte == _workBuffer[previousRepetition + repetitionLength - 2])
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
                // Find an occurrence whose first and last but one bytes both match.
                do
                {
                    previousRepetition = _hashOffsets[++hashOffsetIndex];

                    if (previousRepetition >= repetitionLimit)
                    {
                        return repetitionLength;
                    }
                }
                while (_workBuffer[previousRepetition + repetitionLength - 2] != preLastByte ||
                       _workBuffer[previousRepetition] != _workBuffer[inputOffset]);

                previousRepetitionEnd = previousRepetition + 2;
                candidateLength = 2;
            }

            // Measure how far the candidate agrees with the input.
            while (_workBuffer[previousRepetitionEnd] == _workBuffer[inputOffset + candidateLength])
            {
                if (++candidateLength >= MaxRepetitionLength)
                {
                    break;
                }

                previousRepetitionEnd++;
            }

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
                if (_workBuffer[inputOffset + matchedPrefix] != _workBuffer[inputOffset + prefixLength])
                {
                    prefixLength = _partialMatchTable[prefixLength];

                    if (prefixLength != NoMatch)
                    {
                        continue;
                    }
                }

                prefixLength = (ushort)(prefixLength + 1);
                _partialMatchTable[++matchedPrefix] = prefixLength;
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
