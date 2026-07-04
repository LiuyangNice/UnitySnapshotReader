// SnapshotFileFormat.cs
//
// On-disk binary format of Unity Memory Profiler snapshot files (.snap / .snapshot),
// produced by UnityEngine.Profiling.Memory.Experimental.MemorySnapshot and the
// com.unity.memoryprofiler package.
//
// Layout verified against the authoritative open-source parser
// (facebookexperimental/MemorySnapshotAnalyzer, UnityBackend/), whose ChapterType enum
// is kept in sync with Unity's own MemorySnapshotFileReader.cs (UnityCsReference,
// Modules/ProfilerEditor/MemoryProfiler/).
//
// Big picture:
//   [Header] ...payload... [Footer -> Directory -> BlockSection -> Blocks -> Chapters]
// The Directory at the tail points (via chapter positions) into a set of "blocks".
// Each block is a sequence of fixed-size chunks; chapters are logical views over
// those chunks. There are three chapter formats (Value / ConstArray / VarArray).

using System.Runtime.InteropServices;

namespace SnapshotReader
{
    // ---- Magic constants --------------------------------------------------

    internal static class SnapshotMagic
    {
        // Stored little-endian on disk, so the first 4 bytes of a valid file are
        // CD CD AB AE.
        public const uint HeaderSignature = 0xAEABCDCDu;

        // Footer is the last 12 bytes of the file.
        public const uint FooterSignature = 0xABCDCDAEu;

        // Directory sits at Footer.DirectoryPosition.
        public const uint DirectorySignature = 0xCDCDAEABu;

        // Both the Directory and the BlockSection carry this version tag.
        public const uint FormatVersion = 0x20170724u;
    }

    // ---- Container structs (all [Pack=1], little-endian) -----------------

    /// <summary>
    /// First 4 bytes of the file. Must equal <see cref="SnapshotMagic.HeaderSignature"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Header
    {
        public uint Signature;
    }

    /// <summary>Last 12 bytes of the file: a pointer back to the Directory + a signature.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Footer
    {
        public long DirectoryPosition;
        public uint Signature;
    }

    /// <summary>Sits at <see cref="Footer.DirectoryPosition"/>; lists chapter positions.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Directory
    {
        public uint Signature;
        public uint Version;
        public long BlockSectionPosition;
        public uint NumberOfChapters;
        // followed by NumberOfChapters * long  (chapter positions, absolute file offsets)
    }

    /// <summary>Sits at <see cref="Directory.BlockSectionPosition"/>; lists block positions.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlockSection
    {
        public uint Version;
        public uint NumberOfBlocks;
        // followed by NumberOfBlocks * long  (block positions, absolute file offsets)
    }

    /// <summary>
    /// A contiguous logical byte range made of fixed-size chunks. Unity chunks payload
    /// data so very large blocks can be read/streamed; each chunk is at most
    /// <see cref="ChunkSizeInBytes"/> and lives at its own file offset (stored in the
    /// array that immediately follows this struct).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Block
    {
        public long ChunkSizeInBytes;
        public long TotalSizeInBytes;
        // followed by ceil(TotalSizeInBytes / ChunkSizeInBytes) * long  (chunk positions)
    }

    // ---- Chapter header structs ------------------------------------------

    /// <summary>How a chapter's payload is laid out. Read as ushort.</summary>
    public enum ChapterFormat : ushort
    {
        Value = 1,
        ArrayOfConstantSizeElements = 2,
        ArrayOfVariableSizeElements = 3,
    }

    /// <summary>Common prefix of every chapter; just identifies the format.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ChapterHeader
    {
        public ChapterFormat Format;
    }

    /// <summary>Payload for a <see cref="ChapterFormat.Value"/> chapter: a single blob.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ValueChapterHeader
    {
        public int BlockIndex;
        public int ElementSizeInBytes;
        public long PositionInBlock;
    }

    /// <summary>
    /// Payload for a <see cref="ChapterFormat.ArrayOfConstantSizeElements"/> chapter:
    /// <see cref="ArrayLength"/> elements of <see cref="ElementSizeInBytes"/> each,
    /// stored back-to-back starting at offset 0 of the referenced block.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ConstArrayChapterHeader
    {
        public int BlockIndex;
        public int ElementSizeInBytes;
        public int ArrayLength;
    }

    /// <summary>
    /// Payload for a <see cref="ChapterFormat.ArrayOfVariableSizeElements"/> chapter:
    /// <see cref="ArrayLength"/> elements of varying size. Immediately after this struct
    /// comes (ArrayLength + 1) * long offsets into the referenced block; element i spans
    /// [offsets[i], offsets[i+1]). This is how strings/byte-blobs are stored — the raw
    /// bytes are bounded by the offsets, with NO separate length prefix.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VarArrayChapterHeader
    {
        public int BlockIndex;
        public int ArrayLength;
        // followed by (ArrayLength + 1) * long  (element positions within the block)
    }

    // ---- Metadata: VirtualMachineInformation -----------------------------

    /// <summary>
    /// 24 bytes describing the runtime that produced the snapshot. Typical 64-bit values
    /// (from a real snapshot): PointerSize=8, ObjectHeaderSize=16, ArrayHeaderSize=32,
    /// ArrayBoundsOffsetInHeader=16, ArraySizeOffsetInHeader=24, AllocationGranularity=16.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VirtualMachineInformation
    {
        public int PointerSize;
        public int ObjectHeaderSize;
        public int ArrayHeaderSize;
        public int ArrayBoundsOffsetInHeader;
        public int ArraySizeOffsetInHeader;
        public int AllocationGranularity;
    }

    // ---- Managed type system flags ---------------------------------------

    /// <summary>
    /// Per-type bitfield. Bit 0 = value type, bit 1 = array. The high 16 bits encode the
    /// array rank: rank = (flags &amp; 0xFFFF0000) &gt;&gt; 16.
    /// </summary>
    [System.Flags]
    public enum TypeFlags : uint
    {
        None = 0,
        ValueType = 1u << 0,
        Array = 1u << 1,
        ArrayRankMask = 0xFFFF0000u,
    }
}
