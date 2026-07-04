// SnapshotReader.cs
//
// Main entry point: parses a Unity Memory Profiler .snap / .snapshot file into a
// <see cref="SnapshotData"/>. This is a straight port of the container/chapter layout
// verified against facebookexperimental/MemorySnapshotAnalyzer (UnityBackend/), which
// itself mirrors Unity's own MemorySnapshotFileReader.cs (UnityCsReference).
//
// Read order:
//   1. Header at 0           → must be 0xAEABCDCD
//   2. Footer at EOF-12      → DirectoryPosition + signature 0xABCDCDAE
//   3. Directory             → signature 0xCDCDAEAB, version 0x20170724, block section
//                              position, and N chapter positions
//   4. BlockSection          → version 0x20170724, M block positions
//   5. Blocks                → each a ChunkedBlock (chunked payload)
//   6. Chapters              → dispatch on ChapterFormat → Value/Const/Var array
//
// Then we pull the well-known chapters (ChapterType enum order) and decode them into
// the SnapshotData records.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SnapshotReader
{
    /// <summary>
    /// Reads Unity Memory Profiler snapshot files (.snap / .snapshot) and produces a
    /// strong-typed <see cref="SnapshotData"/>. Thread-safe per instance (open one
    /// reader per file); dispose when done.
    /// </summary>
    public sealed class SnapshotReader : IDisposable
    {
        SnapshotBinaryReader? m_reader;
        ChunkedBlock[]? m_blocks;
        SnapshotChapter[]? m_chapters;

        SnapshotReader() { }

        /// <summary>Open and fully parse a snapshot file. Returns the parsed data.</summary>
        public static SnapshotData Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException($"Snapshot file not found: {path}", path);

            using (var parser = new SnapshotReader())
            {
                return parser.LoadInternal(path);
            }
        }

        /// <summary>
        /// Load a snapshot from a byte array (e.g. loaded from memory, network, or
        /// embedded resources).
        /// </summary>
        /// <param name="data">Raw file bytes.</param>
        /// <param name="label">Optional display label (shown in SnapshotData.FilePath).</param>
        public static SnapshotData Load(byte[] data, string? label = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            using (var parser = new SnapshotReader())
            {
                return parser.LoadFromMemory(data, label ?? "memory");
            }
        }

        /// <summary>
        /// Asynchronously load a snapshot on a background thread. Returns a Task that
        /// completes when parsing is done. In the Unity Editor, await the result and
        /// the continuation resumes on the Unity main thread automatically.
        /// </summary>
        /// <example>
        /// <code>
        /// var data = await SnapshotReader.LoadAsync("path.snap");
        /// Debug.Log(data.ManagedTypeCount); // safe on main thread
        /// </code>
        /// </example>
        public static Task<SnapshotData> LoadAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            return Task.Run(() => Load(path));
        }

        /// <summary>
        /// Internal: parse from an in-memory byte array.
        /// </summary>
        SnapshotData LoadFromMemory(byte[] data, string label)
        {
            m_reader = SnapshotBinaryReader.Open(data);
            return Parse(label, data.Length);
        }

        SnapshotData LoadInternal(string path)
        {
            m_reader = SnapshotBinaryReader.Open(path);
            return Parse(path, m_reader.Length);
        }

        /// <summary>Shared parse pipeline used by both file and memory loads.</summary>
        SnapshotData Parse(string source, long fileSize)
        {
            var data = new SnapshotData { FilePath = source, FileSize = fileSize };

            // 1. Header
            Header header = m_reader.ReadStruct<Header>(0);
            if (header.Signature != SnapshotMagic.HeaderSignature)
                throw new InvalidDataException(
                    $"Not a Unity memory snapshot (bad header signature 0x{header.Signature:X8}); " +
                    $"expected 0x{SnapshotMagic.HeaderSignature:X8}.");

            // 2. Footer (last 12 bytes)
            long footerPos = m_reader.Length - Marshal.SizeOf(typeof(Footer));
            Footer footer = m_reader.ReadStruct<Footer>(footerPos);
            if (footer.Signature != SnapshotMagic.FooterSignature)
                throw new InvalidDataException(
                    $"Bad footer signature 0x{footer.Signature:X8}; expected 0x{SnapshotMagic.FooterSignature:X8}.");

            // 3. Directory
            Directory directory = m_reader.ReadStruct<Directory>(footer.DirectoryPosition);
            if (directory.Signature != SnapshotMagic.DirectorySignature)
                throw new InvalidDataException(
                    $"Bad directory signature 0x{directory.Signature:X8}; expected 0x{SnapshotMagic.DirectorySignature:X8}.");
            if (directory.Version != SnapshotMagic.FormatVersion)
                throw new InvalidDataException(
                    $"Unsupported snapshot format version 0x{directory.Version:X8}; expected 0x{SnapshotMagic.FormatVersion:X8}.");
            data.FormatVersion = directory.Version;

            long afterDirectory = footer.DirectoryPosition + Marshal.SizeOf(typeof(Directory));

            // 4. BlockSection
            BlockSection blockSection = m_reader.ReadStruct<BlockSection>(directory.BlockSectionPosition);
            if (blockSection.Version != SnapshotMagic.FormatVersion)
                throw new InvalidDataException(
                    $"Bad block section version 0x{blockSection.Version:X8}; expected 0x{SnapshotMagic.FormatVersion:X8}.");

            long afterBlockSection = directory.BlockSectionPosition + Marshal.SizeOf(typeof(BlockSection));

            // 5. Blocks
            m_blocks = new ChunkedBlock[blockSection.NumberOfBlocks];
            for (int i = 0; i < m_blocks.Length; i++)
            {
                long blockPos = m_reader.ReadStruct<long>(afterBlockSection + i * sizeof(long));
                m_blocks[i] = ChunkedBlock.Open(m_reader, blockPos);
            }

            // 6. Chapters
            m_chapters = new SnapshotChapter[directory.NumberOfChapters];
            for (int i = 0; i < m_chapters.Length; i++)
            {
                long chapterPos = m_reader.ReadStruct<long>(afterDirectory + i * sizeof(long));
                if (chapterPos == 0)
                {
                    m_chapters[i] = null!; // optional/absent chapter
                    continue;
                }
                m_chapters[i] = CreateChapterInstance(m_reader, chapterPos);
            }

            // Decode well-known chapters into the data model.
            DecodeMetadata(data);
            DecodeManagedTypes(data);
            DecodeNativeTypesAndObjects(data);
            DecodeGCHandles(data);
            DecodeManagedHeap(data);
            DecodeConnections(data);
            DecodeNativeRootReferences(data);
            DecodeNativeMemoryRegions(data);

            // Build the instance-id index for native-object reference resolution.
            var byId = data.NativeObjectIndexByInstanceId;
            for (int i = 0; i < data.NativeObjects.Length; i++)
            {
                int id = data.NativeObjects[i].InstanceId;
                if (!byId.ContainsKey(id)) byId[id] = i;
            }

            return data;
        }

        // ---- Metadata ----

        void DecodeMetadata(SnapshotData data)
        {
            if (TryGetValue(ChapterType.Metadata_Version, out var vc))
                data.SnapshotVersion = vc.Range.ReadStruct<uint>(0);
            if (TryGetValue(ChapterType.Metadata_RecordDate, out vc))
                data.RecordDate = vc.Range.ReadStruct<ulong>(0);
            if (TryGetValue(ChapterType.Metadata_CaptureFlags, out vc))
                data.CaptureFlags = vc.Range.ReadStruct<uint>(0);
            if (TryGetValue(ChapterType.Metadata_VirtualMachineInformation, out vc))
                data.VirtualMachineInformation = vc.Range.ReadStruct<VirtualMachineInformation>(0);
            if (TryGetValue(ChapterType.Metadata_UserMetadata, out vc))
                data.UserMetadata = DecodeUserMetadata(vc.Range.ToArray());
        }

        static Dictionary<string, string>? DecodeUserMetadata(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            var result = new Dictionary<string, string>();
            int i = 0;
            while (i < raw.Length)
            {
                // null-terminated key
                int keyEnd = Array.IndexOf(raw, (byte)0, i);
                if (keyEnd < 0 || keyEnd == i) break;
                string key = System.Text.Encoding.UTF8.GetString(raw, i, keyEnd - i);
                i = keyEnd + 1;
                if (i >= raw.Length) break;

                // null-terminated value
                int valEnd = Array.IndexOf(raw, (byte)0, i);
                if (valEnd < 0) break;
                string val = System.Text.Encoding.UTF8.GetString(raw, i, valEnd - i);
                i = valEnd + 1;

                result[key] = val;
            }
            return result;
        }

        // ---- Managed type system ----

        void DecodeManagedTypes(SnapshotData data)
        {
            if (!TryGetConstArray(ChapterType.TypeDescriptions_Flags, out var flagsCh))
                return;

            int count = (int)flagsCh.Count;
            data.ManagedTypeCount = count;
            if (count == 0) return;

            TryGetVarArray(ChapterType.TypeDescriptions_Name, count, out var names);
            TryGetVarArray(ChapterType.TypeDescriptions_Assembly, count, out var assemblies);
            TryGetVarArray(ChapterType.TypeDescriptions_FieldIndices, count, out var fieldIndices);
            TryGetVarArray(ChapterType.TypeDescriptions_StaticFieldBytes, count, out var staticFields);
            TryGetConstArray(ChapterType.TypeDescriptions_BaseOrElementTypeIndex, count, out var baseIndices);
            TryGetConstArray(ChapterType.TypeDescriptions_Size, count, out var sizes);
            TryGetConstArray(ChapterType.TypeDescriptions_TypeInfoAddress, count, out var typeInfoAddrs);
            TryGetConstArray(ChapterType.TypeDescriptions_TypeIndex, count, out var typeIndices);

            var types = new ManagedType[count];
            for (int i = 0; i < count; i++)
            {
                uint f = flagsCh[i].ReadStruct<uint>(0);
                string name = names != null ? names[i].ReadString() : string.Empty;
                string asm = assemblies != null ? assemblies[i].ReadString() : string.Empty;
                int[] fields = fieldIndices != null ? fieldIndices[i].ReadIntArray() : Array.Empty<int>();
                byte[]? staticBytes = staticFields != null ? staticFields[i].ToArray() : null;
                int baseIdx = baseIndices != null ? baseIndices[i].ReadStruct<int>(0) : -1;
                int size = sizes != null ? sizes[i].ReadStruct<int>(0) : 0;
                ulong tiAddr = typeInfoAddrs != null ? typeInfoAddrs[i].ReadInteger() : 0;
                int tIdx = typeIndices != null ? typeIndices[i].ReadStruct<int>(0) : -1;

                types[i] = new ManagedType(i, (TypeFlags)f, name, asm, baseIdx, size, tiAddr, tIdx, fields, staticBytes);
            }
            data.ManagedTypes = types;

            // Fields
            if (TryGetConstArray(ChapterType.FieldDescriptions_Offset, out var fieldOffsets))
            {
                int fcount = (int)fieldOffsets.Count;
                data.FieldCount = fcount;
                TryGetConstArray(ChapterType.FieldDescriptions_TypeIndex, fcount, out var fieldTypes);
                TryGetVarArray(ChapterType.FieldDescriptions_Name, fcount, out var fieldNames);
                TryGetConstArray(ChapterType.FieldDescriptions_IsStatic, fcount, out var fieldStatics);

                var fields = new ManagedField[fcount];
                for (int i = 0; i < fcount; i++)
                {
                    int off = fieldOffsets[i].ReadStruct<int>(0);
                    int ti = fieldTypes != null ? fieldTypes[i].ReadStruct<int>(0) : -1;
                    string nm = fieldNames != null ? fieldNames[i].ReadString() : string.Empty;
                    bool st = fieldStatics != null && fieldStatics[i].ReadStruct<byte>(0) != 0;
                    fields[i] = new ManagedField(i, off, ti, nm, st);
                }
                data.ManagedFields = fields;
            }
        }

        // ---- Native types & objects ----

        void DecodeNativeTypesAndObjects(SnapshotData data)
        {
            // Types
            if (TryGetVarArray(ChapterType.NativeTypes_Name, out var nNames))
            {
                int count = (int)nNames.Count;
                data.NativeTypeCount = count;
                TryGetConstArray(ChapterType.NativeTypes_NativeBaseTypeArrayIndex, count, out var nBaseIndices);

                var types = new NativeType[count];
                for (int i = 0; i < count; i++)
                {
                    string nm = nNames[i].ReadString();
                    int baseIdx = nBaseIndices != null ? nBaseIndices[i].ReadStruct<int>(0) : -1;
                    types[i] = new NativeType(i, nm, baseIdx);
                }
                data.NativeTypes = types;
            }

            // Objects
            if (TryGetConstArray(ChapterType.NativeObjects_NativeTypeArrayIndex, out var objTypes))
            {
                int count = (int)objTypes.Count;
                data.NativeObjectCount = count;
                if (count == 0) return;

                TryGetConstArray(ChapterType.NativeObjects_InstanceId, count, out var instIds);
                TryGetVarArray(ChapterType.NativeObjects_Name, count, out var objNames);
                TryGetConstArray(ChapterType.NativeObjects_NativeObjectAddress, count, out var objAddrs);
                TryGetConstArray(ChapterType.NativeObjects_Size, count, out var objSizes);
                TryGetConstArray(ChapterType.NativeObjects_RootReferenceId, count, out var objRoots);
                TryGetConstArray(ChapterType.NativeObjects_GCHandleIndex, count, out var objGCHandles);

                var objects = new NativeObject[count];
                long totalSize = 0;
                for (int i = 0; i < count; i++)
                {
                    int ti = objTypes[i].ReadStruct<int>(0);
                    int iid = instIds != null ? instIds[i].ReadStruct<int>(0) : 0;
                    string nm = objNames != null ? objNames[i].ReadString() : string.Empty;
                    ulong addr = objAddrs != null ? objAddrs[i].ReadInteger() : 0;
                    ulong sz = objSizes != null ? objSizes[i].ReadInteger() : 0;
                    ulong root = objRoots != null ? objRoots[i].ReadInteger() : 0;
                    int gcHandle = objGCHandles != null ? objGCHandles[i].ReadStruct<int>(0) : -1;
                    objects[i] = new NativeObject(i, ti, iid, nm, addr, sz, root, gcHandle);
                    totalSize += (long)sz;
                }
                data.NativeObjects = objects;
                data.NativeObjectsTotalBytes = totalSize;
            }
        }

        // ---- GC handles ----

        void DecodeGCHandles(SnapshotData data)
        {
            if (!TryGetConstArray(ChapterType.GCHandles_Target, out var targets)) return;
            int count = (int)targets.Count;
            data.GCHandleCount = count;
            var handles = new GCHandleInfo[count];
            for (int i = 0; i < count; i++)
                handles[i] = new GCHandleInfo(i, targets[i].ReadInteger());
            data.GCHandles = handles;
        }

        // ---- Managed heap ----

        void DecodeManagedHeap(SnapshotData data)
        {
            if (!TryGetConstArray(ChapterType.ManagedHeapSections_StartAddress, out var starts)) return;
            TryGetVarArray(ChapterType.ManagedHeapSections_Bytes, (int)starts.Count, out var contents);

            int count = (int)starts.Count;
            data.ManagedHeapSectionCount = count;
            var sections = new ManagedHeapSection[count];
            long totalBytes = 0;
            for (int i = 0; i < count; i++)
            {
                ulong raw = starts[i].ReadInteger();
                ulong startAddress = raw & 0x7FFFFFFFFFFFFFFFul;
                bool exec = (raw >> 63) != 0;
                byte[]? bytes = contents != null ? contents[i].ToArray() : null;
                sections[i] = new ManagedHeapSection(startAddress, exec, bytes ?? Array.Empty<byte>());
                totalBytes += sections[i].Size;
            }

            // Unity does not guarantee segments are sorted by address; sort for lookup.
            Array.Sort(sections, (a, b) => a.StartAddress.CompareTo(b.StartAddress));
            data.ManagedHeapSections = sections;
            data.ManagedHeapTotalBytes = totalBytes;
        }

        // ---- Connections (managed reference graph) ----

        void DecodeConnections(SnapshotData data)
        {
            if (!TryGetConstArray(ChapterType.Connections_From, out var fromCh)) return;
            TryGetConstArray(ChapterType.Connections_To, (int)fromCh.Count, out var toCh);

            int count = (int)fromCh.Count;
            data.ConnectionCount = count;
            var conns = data.Connections;
            for (int i = 0; i < count; i++)
            {
                int from = fromCh[i].ReadStruct<int>(0);
                int to = toCh != null ? toCh[i].ReadStruct<int>(0) : -1;
                if (conns.TryGetValue(from, out var list)) list.Add(to);
                else conns[from] = new List<int> { to };
            }
        }

        // ---- Native root references ----

        void DecodeNativeRootReferences(SnapshotData data)
        {
            if (!TryGetConstArray(ChapterType.NativeRootReferences_Id, out var ids)) return;
            int count = (int)ids.Count;
            data.NativeRootReferenceCount = count;

            TryGetVarArray(ChapterType.NativeRootReferences_AreaName, count, out var areaNames);
            TryGetVarArray(ChapterType.NativeRootReferences_ObjectName, count, out var objNames);
            TryGetConstArray(ChapterType.NativeRootReferences_AccumulatedSize, count, out var sizes);

            var roots = new NativeRootReference[count];
            for (int i = 0; i < count; i++)
            {
                ulong id = ids[i].ReadInteger();
                string area = areaNames != null ? areaNames[i].ReadString() : string.Empty;
                string nm = objNames != null ? objNames[i].ReadString() : string.Empty;
                ulong sz = sizes != null ? sizes[i].ReadInteger() : 0;
                roots[i] = new NativeRootReference(id, area, nm, sz);
            }
            data.NativeRootReferences = roots;
        }

        // ---- Native memory regions ----

        void DecodeNativeMemoryRegions(SnapshotData data)
        {
            if (!TryGetVarArray(ChapterType.NativeMemoryRegions_Name, out var names)) return;
            int count = (int)names.Count;
            data.NativeMemoryRegionCount = count;
            if (count == 0) return;

            TryGetConstArray(ChapterType.NativeMemoryRegions_ParentIndex, count, out var parents);
            TryGetConstArray(ChapterType.NativeMemoryRegions_AddressBase, count, out var bases);
            TryGetConstArray(ChapterType.NativeMemoryRegions_AddressSize, count, out var sizes);
            TryGetConstArray(ChapterType.NativeMemoryRegions_FirstAllocationIndex, count, out var firsts);
            TryGetConstArray(ChapterType.NativeMemoryRegions_NumAllocations, count, out var nums);

            var regions = new NativeMemoryRegion[count];
            for (int i = 0; i < count; i++)
            {
                string nm = names[i].ReadString();
                int parent = parents != null ? parents[i].ReadStruct<int>(0) : -1;
                ulong ab = bases != null ? bases[i].ReadInteger() : 0;
                ulong asz = sizes != null ? sizes[i].ReadInteger() : 0;
                int first = firsts != null ? firsts[i].ReadStruct<int>(0) : 0;
                int num = nums != null ? nums[i].ReadStruct<int>(0) : 0;
                regions[i] = new NativeMemoryRegion(i, nm, parent, ab, asz, first, num);
            }
            data.NativeMemoryRegions = regions;
        }

        // ---- Chapter accessors ----

        // ChapterType is an enum whose underlying order matches Unity's EnumType.
        // See the block comment in ChapterType.cs; we reproduce that order here.
        enum ChapterType : ushort
        {
            Metadata_Version,
            Metadata_RecordDate,
            Metadata_UserMetadata,
            Metadata_CaptureFlags,
            Metadata_VirtualMachineInformation,
            NativeTypes_Name,
            NativeTypes_NativeBaseTypeArrayIndex,
            NativeObjects_NativeTypeArrayIndex,
            NativeObjects_HideFlags,
            NativeObjects_Flags,
            NativeObjects_InstanceId,
            NativeObjects_Name,
            NativeObjects_NativeObjectAddress,
            NativeObjects_Size,
            NativeObjects_RootReferenceId,
            GCHandles_Target,
            Connections_From,
            Connections_To,
            ManagedHeapSections_StartAddress,
            ManagedHeapSections_Bytes,
            ManagedStacks_StartAddress,
            ManagedStacks_Bytes,
            TypeDescriptions_Flags,
            TypeDescriptions_Name,
            TypeDescriptions_Assembly,
            TypeDescriptions_FieldIndices,
            TypeDescriptions_StaticFieldBytes,
            TypeDescriptions_BaseOrElementTypeIndex,
            TypeDescriptions_Size,
            TypeDescriptions_TypeInfoAddress,
            TypeDescriptions_TypeIndex,
            FieldDescriptions_Offset,
            FieldDescriptions_TypeIndex,
            FieldDescriptions_Name,
            FieldDescriptions_IsStatic,
            NativeRootReferences_Id,
            NativeRootReferences_AreaName,
            NativeRootReferences_ObjectName,
            NativeRootReferences_AccumulatedSize,
            NativeAllocations_MemoryRegionIndex,
            NativeAllocations_RootReferenceId,
            NativeAllocations_AllocationSiteId,
            NativeAllocations_Address,
            NativeAllocations_Size,
            NativeAllocations_OverheadSize,
            NativeAllocations_PaddingSize,
            NativeMemoryRegions_Name,
            NativeMemoryRegions_ParentIndex,
            NativeMemoryRegions_AddressBase,
            NativeMemoryRegions_AddressSize,
            NativeMemoryRegions_FirstAllocationIndex,
            NativeMemoryRegions_NumAllocations,
            NativeMemoryLabels_Name,
            NativeAllocationSites_Id,
            NativeAllocationSites_MemoryLabelIndex,
            NativeAllocationSites_CallstackSymbols,
            NativeCallstackSymbol_Symbol,
            NativeCallstackSymbol_ReadableStackTrace,
            NativeObjects_GCHandleIndex,
        }

        bool TryGetChapter(ChapterType type, out SnapshotChapter chapter)
        {
            int idx = (int)type;
            if (m_chapters == null || idx >= m_chapters.Length || m_chapters[idx] == null)
            {
                chapter = null!;
                return false;
            }
            chapter = m_chapters[idx];
            return true;
        }

        bool TryGetValue(ChapterType type, out ValueChapter chapter)
        {
            if (TryGetChapter(type, out var c) && c is ValueChapter vc)
            {
                chapter = vc;
                return true;
            }
            chapter = null!;
            return false;
        }

        bool TryGetConstArray(ChapterType type, out ConstArrayChapter chapter)
        {
            if (TryGetChapter(type, out var c) && c is ConstArrayChapter cac)
            {
                chapter = cac;
                return true;
            }
            chapter = null!;
            return false;
        }

        bool TryGetConstArray(ChapterType type, int expectedLength, out ConstArrayChapter chapter)
        {
            if (TryGetConstArray(type, out chapter) && chapter.Count == expectedLength)
                return true;
            chapter = null!;
            return false;
        }

        bool TryGetVarArray(ChapterType type, out VarArrayChapter chapter)
        {
            if (TryGetChapter(type, out var c) && c is VarArrayChapter vac)
            {
                chapter = vac;
                return true;
            }
            chapter = null!;
            return false;
        }

        bool TryGetVarArray(ChapterType type, int expectedLength, out VarArrayChapter chapter)
        {
            if (TryGetVarArray(type, out chapter) && chapter.Count == expectedLength)
                return true;
            chapter = null!;
            return false;
        }

        // ---- Chapter factory (instance, so it can see m_blocks) ----

        SnapshotChapter CreateChapterInstance(SnapshotBinaryReader reader, long position)
        {
            ChapterHeader header = reader.ReadStruct<ChapterHeader>(position);
            switch (header.Format)
            {
                case ChapterFormat.Value:
                    return ValueChapter.Create(reader, position, m_blocks!);
                case ChapterFormat.ArrayOfConstantSizeElements:
                    return ConstArrayChapter.Create(reader, position, m_blocks!);
                case ChapterFormat.ArrayOfVariableSizeElements:
                    return VarArrayChapter.Create(reader, position, m_blocks!);
                default:
                    throw new InvalidDataException($"Unknown chapter format 0x{header.Format:X} at offset {position}");
            }
        }

        public void Dispose()
        {
            m_reader?.Dispose();
        }
    }

    /// <summary>
    /// Extension on ChunkedBlock.Range to read an int[] whose entire content is the range.
    /// Used for TypeDescriptions.FieldIndices (each type's field-index list).
    /// </summary>
    internal static class RangeExtensions
    {
        public static int[] ReadIntArray(this ChunkedBlock.Range range)
        {
            int count = (int)(range.Size / sizeof(int));
            if (count == 0) return Array.Empty<int>();
            byte[] bytes = range.ToArray();
            var arr = new int[count];
            Buffer.BlockCopy(bytes, 0, arr, 0, count * sizeof(int));
            return arr;
        }
    }
}
