// SnapshotBinaryReader.cs
//
// Thin wrapper over a file stream / byte array providing the few primitive reads the
// snapshot parser needs: read a struct at an absolute file offset, read a run of bytes,
// read a long[] (the chapter/block position arrays are stored as native long arrays
// directly in the file, not inside any block).

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SnapshotReader
{
    /// <summary>
    /// Random-access binary reader over a Unity snapshot file. Keeps the file open and
    /// reads on demand; structs are decoded little-endian (the snapshot is LE on every
    /// platform Unity ships on).
    /// </summary>
    internal sealed class SnapshotBinaryReader : IDisposable
    {
        readonly Stream m_stream;
        readonly BinaryReader m_reader;
        readonly long m_length;

        SnapshotBinaryReader(Stream stream)
        {
            m_stream = stream;
            m_reader = new BinaryReader(stream);
            m_length = stream.Length;
        }

        public static SnapshotBinaryReader Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
            return new SnapshotBinaryReader(fs);
        }

        /// <summary>
        /// Open a reader over an in-memory byte array. Useful for loading snapshots
        /// from embedded resources, network responses, or custom storage back-ends.
        /// </summary>
        public static SnapshotBinaryReader Open(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return new SnapshotBinaryReader(new MemoryStream(data));
        }

        public long Length => m_length;

        public void Dispose()
        {
            m_reader?.Dispose();
            m_stream?.Dispose();
        }

        // ---- Absolute-offset reads ----------------------------------------

        /// <summary>Read a struct T at absolute file offset <paramref name="position"/>.</summary>
        public T ReadStruct<T>(long position) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] buf = new byte[size];
            Seek(position);
            int read = m_reader.Read(buf, 0, size);
            if (read != size)
                throw new EndOfStreamException($"short read at {position}: got {read}/{size}");

            unsafe
            {
                fixed (byte* p = buf)
                {
                    return (T)Marshal.PtrToStructure(new IntPtr(p), typeof(T));
                }
            }
        }

        /// <summary>Read <paramref name="count"/> longs (8 bytes each) starting at <paramref name="position"/>.</summary>
        public long[] ReadLongArray(long position, int count)
        {
            var arr = new long[count];
            if (count == 0) return arr;
            Seek(position);
            int bytes = count * sizeof(long);
            byte[] buf = m_reader.ReadBytes(bytes);
            if (buf.Length != bytes)
                throw new EndOfStreamException($"short long[] read at {position}");
            Buffer.BlockCopy(buf, 0, arr, 0, bytes);
            return arr;
        }

        /// <summary>Read <paramref name="count"/> ints (4 bytes each) starting at <paramref name="position"/>.</summary>
        public int[] ReadIntArray(long position, int count)
        {
            var arr = new int[count];
            if (count == 0) return arr;
            Seek(position);
            int bytes = count * sizeof(int);
            byte[] buf = m_reader.ReadBytes(bytes);
            if (buf.Length != bytes)
                throw new EndOfStreamException($"short int[] read at {position}");
            Buffer.BlockCopy(buf, 0, arr, 0, bytes);
            return arr;
        }

        /// <summary>Read <paramref name="count"/> bytes at <paramref name="position"/> into <paramref name="buffer"/>.</summary>
        public void ReadBytes(long position, byte[] buffer, long dstOffset, int count)
        {
            Seek(position);
            int read = m_reader.Read(buffer, (int)dstOffset, count);
            if (read != count)
                throw new EndOfStreamException($"short byte read at {position}: got {read}/{count}");
        }

        /// <summary>Read <paramref name="count"/> bytes at <paramref name="position"/> as a new array.</summary>
        public byte[] ReadBytes(long position, int count)
        {
            var arr = new byte[count];
            ReadBytes(position, arr, 0, count);
            return arr;
        }

        void Seek(long position)
        {
            if (position < 0 || position > m_length)
                throw new InvalidDataException($"seek out of range: {position} (len {m_length})");
            m_stream.Position = position;
        }
    }
}
