// ChunkedBlock.cs
//
// A "block" in the Unity snapshot format is a logical byte range of TotalSizeInBytes,
// physically stored as ceil(Total/ChunkSize) fixed-size chunks scattered through the
// file. Each chunk has its own file offset (the long[] that follows the Block struct).
//
// This reader presents the block as a flat, contiguous address space: callers ask for
// a (offset, size) range, and we transparently fetch the right chunk(s). We cache the
// whole block in a single byte[] on first access — Unity snapshots are hundreds of MB
// at most, and a flat array is dramatically simpler and faster than the reference
// implementation's straddling-read logic. For truly huge files you can switch to the
// chunk-at-a-time path, but for Editor use the eager read is fine.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SnapshotReader
{
    /// <summary>
    /// Reads a Unity snapshot "block" — a virtual contiguous byte range backed by one or
    /// more fixed-size chunks elsewhere in the file. Provides range reads and primitive
    /// decoding over that virtual range.
    /// </summary>
    internal sealed class ChunkedBlock
    {
        readonly byte[] m_data;

        ChunkedBlock(byte[] data)
        {
            m_data = data;
        }

        /// <summary>
        /// Open the block whose <see cref="Block"/> header lives at <paramref name="blockPosition"/>
        /// in <paramref name="reader"/>, and eagerly read its full contents into memory.
        /// </summary>
        public static ChunkedBlock Open(SnapshotBinaryReader reader, long blockPosition)
        {
            Block block = reader.ReadStruct<Block>(blockPosition);

            int chunkCount = (int)(block.TotalSizeInBytes / block.ChunkSizeInBytes);
            if (block.TotalSizeInBytes % block.ChunkSizeInBytes != 0)
                chunkCount++;

            long[] chunkPositions = reader.ReadLongArray(
                blockPosition + Marshal.SizeOf(typeof(Block)), chunkCount);

            var data = new byte[block.TotalSizeInBytes];
            long remaining = block.TotalSizeInBytes;
            long dst = 0;
            for (int i = 0; i < chunkPositions.Length; i++)
            {
                int toRead = (int)Math.Min(block.ChunkSizeInBytes, remaining);
                reader.ReadBytes(chunkPositions[i], data, dst, toRead);
                dst += toRead;
                remaining -= toRead;
            }

            return new ChunkedBlock(data);
        }

        public long Size => m_data.Length;

        // ---- Range access --------------------------------------------------

        /// <summary>A window into the block's bytes, with bounds checking.</summary>
        public readonly struct Range
        {
            readonly ChunkedBlock m_block;
            readonly long m_offset;
            readonly long m_size;

            internal Range(ChunkedBlock block, long offset, long size)
            {
                m_block = block;
                m_offset = offset;
                m_size = size;
            }

            public long Size => m_size;

            public Range GetSubRange(long offset, long size)
            {
                if (offset < 0 || size < 0 || offset + size > m_size)
                    throw new InvalidDataException(
                        $"sub-range out of bounds: offset={offset}, size={size}, windowSize={m_size}");
                return new Range(m_block, m_offset + offset, size);
            }

            public byte[] ToArray()
            {
                var arr = new byte[m_size];
                Buffer.BlockCopy(m_block.m_data, (int)m_offset, arr, 0, (int)m_size);
                return arr;
            }

            // ---- Primitive decoders -------------------------------------

            public T ReadStruct<T>(long position) where T : struct
            {
                int size = Marshal.SizeOf(typeof(T));
                if (position < 0 || position + size > m_size)
                    throw new InvalidDataException($"struct read out of range: pos={position}, size={size}, windowSize={m_size}");
                return m_block.ReadStructInternal<T>(m_offset + position, size);
            }

            /// <summary>Read a 4- or 8-byte integer (pointer/word) at offset 0 of this range.</summary>
            public ulong ReadInteger()
            {
                if (m_size == 4) return ReadStruct<uint>(0);
                if (m_size == 8) return ReadStruct<ulong>(0);
                throw new InvalidDataException($"integer-sized range expected, got size {m_size}");
            }

            /// <summary>
            /// Decode the range as a string. Unity stores strings in variable-size chapters
            /// as raw bytes with NO length prefix — the byte range itself is the string.
            /// In practice names are ASCII / UTF-8.
            /// </summary>
            public string ReadString()
            {
                int len = (int)m_size;
                if (len <= 0) return string.Empty;
                // Trim trailing NULs if present (some padded entries).
                var arr = new byte[len];
                Buffer.BlockCopy(m_block.m_data, (int)m_offset, arr, 0, len);
                while (len > 0 && arr[len - 1] == 0) len--;
                return s_encoding.GetString(arr, 0, len);
            }

            static readonly System.Text.Encoding s_encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }

        /// <summary>Get a <see cref="Range"/> covering [offset, offset+size).</summary>
        public Range GetRange(long offset, long size)
        {
            if (offset < 0 || size < 0 || offset + size > m_data.Length)
                throw new InvalidDataException($"block range out of bounds: offset={offset}, size={size}, blockLen={m_data.Length}");
            return new Range(this, offset, size);
        }

        /// <summary>Read an int[] of <paramref name="count"/> starting at <paramref name="offset"/>.</summary>
        public int[] ReadIntArray(long offset, int count)
        {
            var arr = new int[count];
            if (count == 0) return arr;
            Buffer.BlockCopy(m_data, (int)offset, arr, 0, count * sizeof(int));
            return arr;
        }

        // ---- Low-level struct read with straddle handling -----------------

        T ReadStructInternal<T>(long pos, int size) where T : struct
        {
            // Structs in this format never cross chunk boundaries in practice (chunk size
            // is large and structs are tiny), but we handle the rare straddle safely by
            // copying into a local buffer. (Equality with the reference impl.)
            if (pos + size > m_data.Length)
                throw new InvalidDataException($"struct read past block end: pos={pos}, size={size}");

            // Fast path: pin and copy directly.
            unsafe
            {
                fixed (byte* p = &m_data[pos])
                {
                    T value;
                    // Marshal.PtrToStructure works on any packed struct without a GCHandle.
                    value = (T)Marshal.PtrToStructure(new IntPtr(p), typeof(T));
                    return value;
                }
            }
        }
    }
}
