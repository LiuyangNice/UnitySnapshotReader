// SnapshotChapter.cs
//
// A "chapter" is one logical data section of the snapshot. There are three formats,
// picked by the ChapterFormat ushort that opens the chapter:
//
//   Value                     — a single blob (e.g. VirtualMachineInformation)
//   ArrayOfConstantSizeElements — N fixed-size elements back-to-back (ints, ulongs...)
//   ArrayOfVariableSizeElements — N variable-size elements, bounded by (N+1) offsets
//                                 (this is how strings & byte-blobs are stored)
//
// Each chapter references a block (by index) and a position/size within that block.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SnapshotReader
{
    /// <summary>Base for the three chapter formats.</summary>
    internal abstract class SnapshotChapter
    {
        public abstract ChapterFormat Format { get; }
        public abstract long Count { get; }          // 1 for Value, N for arrays
        public abstract long ElementSize { get; }    // fixed for const arrays, -1 for variable
    }

    /// <summary>
    /// A single value of <see cref="ElementSize"/> bytes, read from a block at a given
    /// position. Exposes the bytes as a <see cref="ChunkedBlock.Range"/>.
    /// </summary>
    internal sealed class ValueChapter : SnapshotChapter
    {
        readonly ChunkedBlock.Range m_range;

        ValueChapter(ChunkedBlock.Range range) { m_range = range; }

        public static ValueChapter Create(SnapshotBinaryReader reader, long chapterPosition, ChunkedBlock[] blocks)
        {
            ChapterHeader header = reader.ReadStruct<ChapterHeader>(chapterPosition);
            if (header.Format != ChapterFormat.Value)
                throw new InvalidDataException($"expected Value chapter, got {header.Format}");

            ValueChapterHeader payload = reader.ReadStruct<ValueChapterHeader>(chapterPosition + Marshal.SizeOf(typeof(ChapterHeader)));
            var block = blocks[payload.BlockIndex];
            var range = block.GetRange(payload.PositionInBlock, payload.ElementSizeInBytes);
            return new ValueChapter(range);
        }

        public ChunkedBlock.Range Range => m_range;
        public override ChapterFormat Format => ChapterFormat.Value;
        public override long Count => 1;
        public override long ElementSize => m_range.Size;
    }

    /// <summary>
    /// An array of <see cref="Count"/> elements of exactly <see cref="ElementSize"/> bytes
    /// each, stored back-to-back from offset 0 of the referenced block. Index with
    /// <see cref="this[int]"/>.
    /// </summary>
    internal sealed class ConstArrayChapter : SnapshotChapter
    {
        readonly ChunkedBlock.Range m_contents;
        readonly int m_elementSize;
        readonly int m_length;

        ConstArrayChapter(ChunkedBlock.Range contents, int elementSize, int length)
        {
            m_contents = contents;
            m_elementSize = elementSize;
            m_length = length;
        }

        public static ConstArrayChapter Create(SnapshotBinaryReader reader, long chapterPosition, ChunkedBlock[] blocks)
        {
            ChapterHeader header = reader.ReadStruct<ChapterHeader>(chapterPosition);
            if (header.Format != ChapterFormat.ArrayOfConstantSizeElements)
                throw new InvalidDataException($"expected ConstArray chapter, got {header.Format}");

            ConstArrayChapterHeader payload = reader.ReadStruct<ConstArrayChapterHeader>(chapterPosition + Marshal.SizeOf(typeof(ChapterHeader)));
            var block = blocks[payload.BlockIndex];
            long totalBytes = (long)payload.ArrayLength * payload.ElementSizeInBytes;
            var contents = block.GetRange(0, totalBytes);
            return new ConstArrayChapter(contents, payload.ElementSizeInBytes, payload.ArrayLength);
        }

        public ChunkedBlock.Range this[int index]
        {
            get
            {
                if ((uint)index >= (uint)m_length)
                    throw new IndexOutOfRangeException($"const array index {index} / length {m_length}");
                return m_contents.GetSubRange(index * m_elementSize, m_elementSize);
            }
        }

        public override ChapterFormat Format => ChapterFormat.ArrayOfConstantSizeElements;
        public override long Count => m_length;
        public override long ElementSize => m_elementSize;
    }

    /// <summary>
    /// An array of <see cref="Count"/> variable-size elements. After the chapter header
    /// comes (N+1) long offsets into the referenced block; element i spans
    /// [offsets[i], offsets[i+1]). This is how strings and byte blobs are stored — the
    /// raw bytes are bounded by the offsets, with no separate length prefix.
    /// </summary>
    internal sealed class VarArrayChapter : SnapshotChapter
    {
        readonly ChunkedBlock.Range[] m_elements;

        VarArrayChapter(ChunkedBlock.Range[] elements) { m_elements = elements; }

        public static VarArrayChapter Create(SnapshotBinaryReader reader, long chapterPosition, ChunkedBlock[] blocks)
        {
            ChapterHeader header = reader.ReadStruct<ChapterHeader>(chapterPosition);
            if (header.Format != ChapterFormat.ArrayOfVariableSizeElements)
                throw new InvalidDataException($"expected VarArray chapter, got {header.Format}");

            VarArrayChapterHeader payload = reader.ReadStruct<VarArrayChapterHeader>(chapterPosition + Marshal.SizeOf(typeof(ChapterHeader)));
            long offsetsPos = chapterPosition + Marshal.SizeOf(typeof(ChapterHeader)) + Marshal.SizeOf(typeof(VarArrayChapterHeader));

            // (N + 1) offsets bound N elements.
            var block = blocks[payload.BlockIndex];
            var elements = new ChunkedBlock.Range[payload.ArrayLength];
            long prev = reader.ReadStruct<long>(offsetsPos);
            for (int i = 0; i < payload.ArrayLength; i++)
            {
                long next = reader.ReadStruct<long>(offsetsPos + (i + 1) * sizeof(long));
                long size = next - prev;
                if (size < 0) size = 0;
                elements[i] = block.GetRange(prev, size);
                prev = next;
            }

            return new VarArrayChapter(elements);
        }

        public ChunkedBlock.Range this[int index]
        {
            get
            {
                if ((uint)index >= (uint)m_elements.Length)
                    throw new IndexOutOfRangeException($"var array index {index} / length {m_elements.Length}");
                return m_elements[index];
            }
        }

        public override ChapterFormat Format => ChapterFormat.ArrayOfVariableSizeElements;
        public override long Count => m_elements.Length;
        public override long ElementSize => -1;
    }
}
