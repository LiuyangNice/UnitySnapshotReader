// SnapshotData.cs
//
// Strong-typed parsed view of a Unity Memory Profiler snapshot. The binary reader
// (SnapshotReader.cs) fills these records from the chapters; the Editor window binds to
// them for display.
//
// The names and semantics mirror Unity's own API (UnityEngine.Profiling.Memory.Experimental
// .MemorySnapshot / PackedMemorySnapshot): ManagedType, ManagedField, NativeObject,
// NativeType, GCHandle, etc. Keep these plain immutable structs/records so they are
// trivially serializable and thread-safe.

using System;
using System.Collections.Generic;

namespace SnapshotReader
{
    /// <summary>Parsed contents of a Unity Memory Profiler .snap file.</summary>
    public sealed class SnapshotData
    {
        /// <summary>Absolute path of the file this was parsed from.</summary>
        public string FilePath { get; internal set; } = string.Empty;

        /// <summary>File size in bytes.</summary>
        public long FileSize { get; internal set; }

        /// <summary>Snapshot format version (Unity's 0x20170724 etc.).</summary>
        public uint FormatVersion { get; internal set; }

        // ---- Metadata ----

        /// <summary>Snapshot version number as recorded by Unity.</summary>
        public uint SnapshotVersion { get; internal set; }
        /// <summary>DateTime.Ticks value when the snapshot was taken.</summary>
        public ulong RecordDate { get; internal set; }
        /// <summary>Capture flags bitfield.</summary>
        public uint CaptureFlags { get; internal set; }
        /// <summary>Runtime virtual-machine information (pointer size, header sizes, etc.).</summary>
        public VirtualMachineInformation VirtualMachineInformation { get; internal set; }
        /// <summary>Optional user-defined key-value metadata attached to this snapshot.</summary>
        public Dictionary<string, string>? UserMetadata { get; internal set; }

        // ---- Counts for the overview ----

        /// <summary>Number of managed (C#) types in the snapshot.</summary>
        public int ManagedTypeCount { get; internal set; }
        /// <summary>Number of managed field descriptors.</summary>
        public int FieldCount { get; internal set; }
        /// <summary>Number of native (C++) types tracked by Unity.</summary>
        public int NativeTypeCount { get; internal set; }
        /// <summary>Number of native object entries.</summary>
        public int NativeObjectCount { get; internal set; }
        /// <summary>Number of GC handles.</summary>
        public int GCHandleCount { get; internal set; }
        /// <summary>Number of managed-to-managed reference edges.</summary>
        public int ConnectionCount { get; internal set; }
        /// <summary>Number of managed heap memory sections.</summary>
        public int ManagedHeapSectionCount { get; internal set; }
        /// <summary>Number of native root references.</summary>
        public int NativeRootReferenceCount { get; internal set; }
        /// <summary>Number of native memory regions.</summary>
        public int NativeMemoryRegionCount { get; internal set; }
        /// <summary>Total bytes across all managed heap sections.</summary>
        public long ManagedHeapTotalBytes { get; internal set; }
        /// <summary>Sum of all native object sizes in bytes.</summary>
        public long NativeObjectsTotalBytes { get; internal set; }

        // ---- Raw chapter payload (lazily-indexed) ----
        // We keep references to the chapter objects so field/heap byte ranges can be read
        // on demand without re-parsing. The flat arrays below are eagerly decoded for
        // the common display path; heap bytes are left as Ranges.

        /// <summary>All managed (C#) types, indexed by type index.</summary>
        public ManagedType[] ManagedTypes { get; internal set; } = Array.Empty<ManagedType>();
        /// <summary>All managed field descriptors.</summary>
        public ManagedField[] ManagedFields { get; internal set; } = Array.Empty<ManagedField>();
        /// <summary>All native (C++) types, indexed by type index.</summary>
        public NativeType[] NativeTypes { get; internal set; } = Array.Empty<NativeType>();
        /// <summary>All native (Unity engine) objects.</summary>
        public NativeObject[] NativeObjects { get; internal set; } = Array.Empty<NativeObject>();
        /// <summary>GC handle table: each handle pins a managed object.</summary>
        public GCHandleInfo[] GCHandles { get; internal set; } = Array.Empty<GCHandleInfo>();
        /// <summary>Managed heap segments, sorted by <see cref="ManagedHeapSection.StartAddress"/>.</summary>
        public ManagedHeapSection[] ManagedHeapSections { get; internal set; } = Array.Empty<ManagedHeapSection>();
        /// <summary>Root references that keep native objects alive.</summary>
        public NativeRootReference[] NativeRootReferences { get; internal set; } = Array.Empty<NativeRootReference>();
        /// <summary>Named native memory regions (e.g. graphics allocators).</summary>
        public NativeMemoryRegion[] NativeMemoryRegions { get; internal set; } = Array.Empty<NativeMemoryRegion>();

        /// <summary>Adjacency list of managed-to-managed references. Key = from-index.</summary>
        public Dictionary<int, List<int>> Connections { get; internal set; } = new Dictionary<int, List<int>>();

        /// <summary>Lookup of native-object index by InstanceId, for resolving references.</summary>
        public Dictionary<int, int> NativeObjectIndexByInstanceId { get; internal set; } = new Dictionary<int, int>();
    }

    // ---- Managed type system ----

    /// <summary>A managed (C#) type, as described in the snapshot's type-description tables.</summary>
    public readonly struct ManagedType
    {
        /// <summary>Index in the type-description table.</summary>
        public readonly int Index;
        /// <summary>Flags: value-type, array, and array-rank mask.</summary>
        public readonly TypeFlags Flags;
        /// <summary>Full type name (e.g. "System.String").</summary>
        public readonly string Name;
        /// <summary>Assembly that contains this type.</summary>
        public readonly string Assembly;
        /// <summary>Index of the base type (or element type for arrays). -1 if none.</summary>
        public readonly int BaseOrElementTypeIndex;
        /// <summary>Instance size in bytes (0 for reference types).</summary>
        public readonly int Size;
        /// <summary>Runtime TypeInfo address (pointer value).</summary>
        public readonly ulong TypeInfoAddress;
        /// <summary>Type index within the runtime.</summary>
        public readonly int TypeIndex;
        /// <summary>Indices into the <c>ManagedFields</c> table for this type's fields.</summary>
        public readonly int[] FieldIndices;
        /// <summary>Raw bytes of static field data for this type, or null.</summary>
        public readonly byte[]? StaticFieldBytes;

        public ManagedType(int index, TypeFlags flags, string name, string assembly,
            int baseOrElementTypeIndex, int size, ulong typeInfoAddress, int typeIndex,
            int[] fieldIndices, byte[]? staticFieldBytes)
        {
            Index = index;
            Flags = flags;
            Name = name;
            Assembly = assembly;
            BaseOrElementTypeIndex = baseOrElementTypeIndex;
            Size = size;
            TypeInfoAddress = typeInfoAddress;
            TypeIndex = typeIndex;
            FieldIndices = fieldIndices;
            StaticFieldBytes = staticFieldBytes;
        }

        /// <summary>True if this is a value type (struct).</summary>
        public bool IsValueType => (Flags & TypeFlags.ValueType) != 0;
        /// <summary>True if this is an array type.</summary>
        public bool IsArray => (Flags & TypeFlags.Array) != 0;
        /// <summary>Array rank (1 = single-dimensional, 2 = jagged/rectangular, etc.).</summary>
        public int ArrayRank => (int)(((uint)Flags & (uint)TypeFlags.ArrayRankMask) >> 16);

        public override string ToString()
        {
            if (IsArray)
            {
                string brackets = new string('[', Math.Max(1, ArrayRank));
                // element type name is looked up via BaseOrElementTypeIndex
                return Name + brackets + "]";
            }
            return Name;
        }
    }

    /// <summary>A field of a managed type.</summary>
    public readonly struct ManagedField
    {
        /// <summary>Index in the field-descriptor table.</summary>
        public readonly int Index;
        /// <summary>Byte offset of this field within the type's layout.</summary>
        public readonly int Offset;
        /// <summary>Index of the field's type in <c>ManagedTypes</c>.</summary>
        public readonly int TypeIndex;
        /// <summary>Field name.</summary>
        public readonly string Name;
        /// <summary>True if this is a static field.</summary>
        public readonly bool IsStatic;

        public ManagedField(int index, int offset, int typeIndex, string name, bool isStatic)
        {
            Index = index;
            Offset = offset;
            TypeIndex = typeIndex;
            Name = name;
            IsStatic = isStatic;
        }

        public override string ToString() => $"{Name} @ +{Offset} (type {TypeIndex}{(IsStatic ? ", static" : "")})";
    }

    // ---- Native (Unity engine) objects ----

    /// <summary>A native (C++) object tracked by Unity's object system.</summary>
    public readonly struct NativeObject
    {
        /// <summary>Index in the native-object table.</summary>
        public readonly int Index;
        /// <summary>Index into <see cref="SnapshotData.NativeTypes"/>.</summary>
        public readonly int TypeIndex;
        /// <summary>Unity InstanceId (used to cross-reference with managed objects).</summary>
        public readonly int InstanceId;
        /// <summary>Object name (may be empty).</summary>
        public readonly string Name;
        /// <summary>Native memory address (pointer value).</summary>
        public readonly ulong ObjectAddress;
        /// <summary>Object size in bytes.</summary>
        public readonly ulong Size;
        /// <summary>Root reference ID if this object is a root, or 0.</summary>
        public readonly ulong RootReferenceId;
        /// <summary>Index of the GC handle pinning this object, or -1 if none.</summary>
        public readonly int GCHandleIndex;

        public NativeObject(int index, int typeIndex, int instanceId, string name,
            ulong objectAddress, ulong size, ulong rootReferenceId, int gcHandleIndex = -1)
        {
            Index = index;
            TypeIndex = typeIndex;
            InstanceId = instanceId;
            Name = name;
            ObjectAddress = objectAddress;
            Size = size;
            RootReferenceId = rootReferenceId;
            GCHandleIndex = gcHandleIndex;
        }

        public override string ToString() => $"{Name} ({nameof(NativeObject)} #{InstanceId})";
    }

    /// <summary>A native type (e.g. Texture2D, GameObject, MonoBehaviour).</summary>
    public readonly struct NativeType
    {
        /// <summary>Index in the native-type table.</summary>
        public readonly int Index;
        /// <summary>Type name (e.g. "Texture2D", "GameObject").</summary>
        public readonly string Name;
        /// <summary>Index of the base native type, or -1.</summary>
        public readonly int BaseTypeIndex;

        public NativeType(int index, string name, int baseTypeIndex)
        {
            Index = index;
            Name = name;
            BaseTypeIndex = baseTypeIndex;
        }

        public override string ToString() => Name;
    }

    // ---- GC handles ----

    /// <summary>A GC handle: a pin to a managed object, target is the managed heap address.</summary>
    public readonly struct GCHandleInfo
    {
        /// <summary>Index in the GC-handle table.</summary>
        public readonly int Index;
        /// <summary>Address of the pinned managed object on the GC heap.</summary>
        public readonly ulong TargetAddress;

        public GCHandleInfo(int index, ulong targetAddress)
        {
            Index = index;
            TargetAddress = targetAddress;
        }
    }

    // ---- Managed heap segments ----

    /// <summary>One contiguous segment of the managed (GC) heap.</summary>
    public readonly struct ManagedHeapSection
    {
        /// <summary>Start address of this heap segment.</summary>
        public readonly ulong StartAddress;
        /// <summary>True if the segment is marked executable (typically false).</summary>
        public readonly bool IsExecutable;
        /// <summary>Raw bytes of the heap segment.</summary>
        public readonly byte[] Bytes;
        /// <summary>Length of this segment in bytes.</summary>
        public readonly long Size;

        public ManagedHeapSection(ulong startAddress, bool isExecutable, byte[] bytes)
        {
            StartAddress = startAddress;
            IsExecutable = isExecutable;
            Bytes = bytes;
            Size = bytes?.Length ?? 0;
        }
    }

    // ---- Native roots & memory regions ----

    /// <summary>A native root reference (entry point keeping native objects alive).</summary>
    public readonly struct NativeRootReference
    {
        /// <summary>Root reference identifier.</summary>
        public readonly ulong Id;
        /// <summary>Name of the root area (e.g. "Native", "GCHandle").</summary>
        public readonly string AreaName;
        /// <summary>Name of the root object.</summary>
        public readonly string ObjectName;
        /// <summary>Accumulated size in bytes kept alive by this root.</summary>
        public readonly ulong AccumulatedSize;

        public NativeRootReference(ulong id, string areaName, string objectName, ulong accumulatedSize)
        {
            Id = id;
            AreaName = areaName;
            ObjectName = objectName;
            AccumulatedSize = accumulatedSize;
        }
    }

    /// <summary>A native memory region (e.g. a graphics/asset allocator region).</summary>
    public readonly struct NativeMemoryRegion
    {
        /// <summary>Index in the region table.</summary>
        public readonly int Index;
        /// <summary>Region display name.</summary>
        public readonly string Name;
        /// <summary>Index of the parent region, or -1.</summary>
        public readonly int ParentIndex;
        /// <summary>Base address of this memory region.</summary>
        public readonly ulong AddressBase;
        /// <summary>Total size of this memory region.</summary>
        public readonly ulong AddressSize;
        /// <summary>Index of the first allocation in this region.</summary>
        public readonly int FirstAllocationIndex;
        /// <summary>Number of allocations in this region.</summary>
        public readonly int NumAllocations;

        public NativeMemoryRegion(int index, string name, int parentIndex, ulong addressBase,
            ulong addressSize, int firstAllocationIndex, int numAllocations)
        {
            Index = index;
            Name = name;
            ParentIndex = parentIndex;
            AddressBase = addressBase;
            AddressSize = addressSize;
            FirstAllocationIndex = firstAllocationIndex;
            NumAllocations = numAllocations;
        }
    }
}
