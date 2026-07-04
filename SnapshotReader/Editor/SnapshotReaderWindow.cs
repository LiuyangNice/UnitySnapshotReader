// SnapshotReaderWindow.cs
//
// Editor window for loading and inspecting Unity Memory Profiler .snap / .snapshot files.
// Menu: Tools > Snapshot Reader
//
// Tabs:
//   Overview    — file info, metadata, VM info, per-table counts & totals
//   Native Objs — searchable list of native (Unity) objects with type/size/instance-id
//   Types       — managed (C#) type table with flags, base type, size, field count
//   Heap        — managed heap segments (start address, size, exec flag)
//   Roots       — native root references keeping objects alive
//   Regions     — native memory regions

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SnapshotReader.Editor
{
    public class SnapshotReaderWindow : EditorWindow
    {
        SnapshotData? m_data;
        Vector2 m_scroll;
        int m_tab;
        string m_filter = string.Empty;

        // Search-paged view state (native objects / types can be huge; render only the visible page).
        const int kPageSize = 500;
        int m_page;

        static readonly string[] s_tabs = { "Overview", "Native Objects", "Types", "Heap", "Roots", "Regions" };

        [MenuItem("Tools/Snapshot Reader")]
        public static void Open()
        {
            var w = GetWindow<SnapshotReaderWindow>(title: "Snapshot Reader", focus: true);
            w.minSize = new Vector2(700, 400);
        }

        void OnGUI()
        {
            DrawToolbar();

            if (m_data == null)
            {
                DrawEmptyState();
                return;
            }

            m_tab = GUILayout.Toolbar(m_tab, s_tabs, EditorStyles.toolbarButton, GUILayout.Height(24));
            EditorGUILayout.Space(2);

            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            try
            {
                switch (m_tab)
                {
                    case 0: DrawOverview(); break;
                    case 1: DrawNativeObjects(); break;
                    case 2: DrawManagedTypes(); break;
                    case 3: DrawManagedHeap(); break;
                    case 4: DrawRootReferences(); break;
                    case 5: DrawMemoryRegions(); break;
                }
            }
            catch (Exception e)
            {
                EditorGUILayout.HelpBox($"Error rendering tab: {e.Message}", MessageType.Error);
            }
            EditorGUILayout.EndScrollView();
        }

        // ---- Toolbar ----

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Open Snapshot…", EditorStyles.toolbarButton))
                    OpenSnapshot();

                if (m_data != null && GUILayout.Button("Reload", EditorStyles.toolbarButton))
                    LoadFile(m_data.FilePath);

                GUILayout.FlexibleSpace();

                if (m_data != null)
                {
                    var fi = new FileInfo(m_data.FilePath);
                    GUILayout.Label($"{fi.Name}  ({FormatBytes(m_data.FileSize)})", EditorStyles.miniLabel);
                }
            }
        }

        void DrawEmptyState()
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Label("No snapshot loaded", EditorStyles.boldLabel);
                EditorGUILayout.Space();
                if (GUILayout.Button("Open .snap / .snapshot file…", GUILayout.Height(30)))
                    OpenSnapshot();
                EditorGUILayout.HelpBox(
                    "This tool reads Unity Memory Profiler snapshot files (.snap / .snapshot),\n" +
                    "produced by com.unity.memoryprofiler / UnityEngine.Profiling.Memory.Experimental.MemorySnapshot.\n\n" +
                    "Take a snapshot in your project: Window > Analysis > Memory Profiler > Take Snapshot, then \"Save\".",
                    MessageType.Info);
            }
            GUILayout.FlexibleSpace();
        }

        void OpenSnapshot()
        {
            string path = EditorUtility.OpenFilePanelWithFilters(
                "Open Unity Memory Snapshot", string.Empty, new[] { "Unity Memory Snapshot", "snap,snapshot", "All files", "*" });
            if (!string.IsNullOrEmpty(path))
                LoadFile(path);
        }

        void LoadFile(string path)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Snapshot Reader", "Parsing snapshot…", 0.3f);
                m_data = SnapshotReader.Load(path);
                m_page = 0;
                m_filter = string.Empty;
                m_tab = 0;
            }
            catch (Exception e)
            {
                m_data = null;
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Snapshot Reader", $"Failed to load snapshot:\n{e.Message}", "OK");
                Debug.LogError($"[SnapshotReader] Load failed: {e}");
                return;
            }
            EditorUtility.ClearProgressBar();
        }

        // ---- Tab: Overview ----

        void DrawOverview()
        {
            var d = m_data!;
            var vm = d.VirtualMachineInformation;
            Section("File");
            Label("Path", d.FilePath);
            Label("File size", FormatBytes(d.FileSize));
            Label("Format version", $"0x{d.FormatVersion:X8}");

            Section("Metadata");
            Label("Snapshot version", d.SnapshotVersion.ToString());
            Label("Record date (raw)", d.RecordDate.ToString());
            Label("Capture flags", $"0x{d.CaptureFlags:X}");
            Label("Pointer size", $"{vm.PointerSize} bytes");
            Label("Object header size", $"{vm.ObjectHeaderSize} bytes");
            Label("Array header size", $"{vm.ArrayHeaderSize} bytes");
            Label("Array size offset", $"{vm.ArraySizeOffsetInHeader} bytes");
            Label("Allocation granularity", $"{vm.AllocationGranularity} bytes");

            Section("Counts");
            Label("Managed types", d.ManagedTypeCount.ToString("N0"));
            Label("Managed fields", d.FieldCount.ToString("N0"));
            Label("Native types", d.NativeTypeCount.ToString("N0"));
            Label("Native objects", d.NativeObjectCount.ToString("N0"));
            Label("GC handles", d.GCHandleCount.ToString("N0"));
            Label("Connections", d.ConnectionCount.ToString("N0"));
            Label("Managed heap sections", d.ManagedHeapSectionCount.ToString("N0"));
            Label("Native root references", d.NativeRootReferenceCount.ToString("N0"));
            Label("Native memory regions", d.NativeMemoryRegionCount.ToString("N0"));

            Section("Totals");
            Label("Managed heap bytes", FormatBytes(d.ManagedHeapTotalBytes));
            Label("Native object bytes", FormatBytes(d.NativeObjectsTotalBytes));

            if (d.UserMetadata != null && d.UserMetadata.Count > 0)
            {
                Section("User Metadata");
                foreach (var kv in d.UserMetadata)
                    Label(kv.Key, kv.Value);
            }

            // Top native types by total size — handy at-a-glance.
            Section("Top native types by size");
            DrawTopNativeTypesBySize(15);
        }

        void DrawTopNativeTypesBySize(int top)
        {
            var d = m_data!;
            var totals = new Dictionary<string, (long bytes, int count)>();
            for (int i = 0; i < d.NativeObjects.Length; i++)
            {
                var obj = d.NativeObjects[i];
                string name = obj.TypeIndex >= 0 && obj.TypeIndex < d.NativeTypes.Length
                    ? d.NativeTypes[obj.TypeIndex].Name : "<unknown>";
                if (totals.TryGetValue(name, out var v))
                    totals[name] = (v.bytes + (long)obj.Size, v.count + 1);
                else
                    totals[name] = ((long)obj.Size, 1);
            }

            var list = new List<(string name, long bytes, int count)>(totals.Count);
            foreach (var kv in totals)
                list.Add((kv.Key, kv.Value.bytes, kv.Value.count));
            list.Sort((a, b) => b.bytes.CompareTo(a.bytes));

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Type", EditorStyles.boldLabel, GUILayout.Width(220));
                EditorGUILayout.LabelField("Count", EditorStyles.boldLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField("Total size", EditorStyles.boldLabel, GUILayout.Width(120));
            }
            int n = Math.Min(top, list.Count);
            for (int i = 0; i < n; i++)
            {
                var e = list[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(e.name, GUILayout.Width(220));
                    EditorGUILayout.LabelField(e.count.ToString("N0"), GUILayout.Width(70));
                    EditorGUILayout.LabelField(FormatBytes(e.bytes), GUILayout.Width(120));
                }
            }
        }

        // ---- Tab: Native objects ----

        void DrawNativeObjects()
        {
            var d = m_data!;
            DrawSearchBar(ref m_filter, ref m_page, d.NativeObjects.Length);

            int total = d.NativeObjects.Length;
            var filtered = FilterIndices(total, idx =>
            {
                var obj = d.NativeObjects[idx];
                string typeName = obj.TypeIndex >= 0 && obj.TypeIndex < d.NativeTypes.Length
                    ? d.NativeTypes[obj.TypeIndex].Name : "?";
                return (obj.Name + " " + typeName).IndexOf(m_filter, StringComparison.OrdinalIgnoreCase) >= 0;
            });

            DrawPager(filtered.Length, ref m_page);
            var page = PageOf(filtered, m_page);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("#", EditorStyles.boldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("Name", EditorStyles.boldLabel, GUILayout.Width(240));
                EditorGUILayout.LabelField("Type", EditorStyles.boldLabel, GUILayout.Width(140));
                EditorGUILayout.LabelField("InstID", EditorStyles.boldLabel, GUILayout.Width(80));
                EditorGUILayout.LabelField("Size", EditorStyles.boldLabel, GUILayout.Width(90));
                EditorGUILayout.LabelField("Address", EditorStyles.boldLabel);
            }
            foreach (int i in page)
            {
                var obj = d.NativeObjects[i];
                string typeName = obj.TypeIndex >= 0 && obj.TypeIndex < d.NativeTypes.Length
                    ? d.NativeTypes[obj.TypeIndex].Name : "?";
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(50));
                    EditorGUILayout.LabelField(Truncate(obj.Name, 40), GUILayout.Width(240));
                    EditorGUILayout.LabelField(typeName, GUILayout.Width(140));
                    EditorGUILayout.LabelField(obj.InstanceId.ToString(), GUILayout.Width(80));
                    EditorGUILayout.LabelField(FormatBytes((long)obj.Size), GUILayout.Width(90));
                    EditorGUILayout.LabelField($"0x{obj.ObjectAddress:X}");
                }
            }
        }

        // ---- Tab: Managed types ----

        void DrawManagedTypes()
        {
            var d = m_data!;
            DrawSearchBar(ref m_filter, ref m_page, d.ManagedTypes.Length);

            var filtered = FilterIndices(d.ManagedTypes.Length, idx =>
                d.ManagedTypes[idx].Name.IndexOf(m_filter, StringComparison.OrdinalIgnoreCase) >= 0);

            DrawPager(filtered.Length, ref m_page);
            var page = PageOf(filtered, m_page);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("#", EditorStyles.boldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("Name", EditorStyles.boldLabel, GUILayout.Width(220));
                EditorGUILayout.LabelField("Assembly", EditorStyles.boldLabel, GUILayout.Width(200));
                EditorGUILayout.LabelField("Flags", EditorStyles.boldLabel, GUILayout.Width(90));
                EditorGUILayout.LabelField("Size", EditorStyles.boldLabel, GUILayout.Width(60));
                EditorGUILayout.LabelField("Fields", EditorStyles.boldLabel);
            }
            foreach (int i in page)
            {
                var t = d.ManagedTypes[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(50));
                    EditorGUILayout.LabelField(Truncate(t.Name, 45), GUILayout.Width(220));
                    EditorGUILayout.LabelField(Truncate(t.Assembly, 40), GUILayout.Width(200));
                    EditorGUILayout.LabelField(TypeFlagString(t), GUILayout.Width(90));
                    EditorGUILayout.LabelField(t.Size.ToString(), GUILayout.Width(60));
                    EditorGUILayout.LabelField((t.FieldIndices?.Length ?? 0).ToString());
                }
            }
        }

        static string TypeFlagString(in ManagedType t)
        {
            if (t.IsArray) return $"Array[{t.ArrayRank}]";
            if (t.IsValueType) return "ValueType";
            return "Class";
        }

        // ---- Tab: Heap ----

        void DrawManagedHeap()
        {
            var d = m_data!;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("#", EditorStyles.boldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("Start address", EditorStyles.boldLabel, GUILayout.Width(140));
                EditorGUILayout.LabelField("Size", EditorStyles.boldLabel, GUILayout.Width(120));
                EditorGUILayout.LabelField("Exec", EditorStyles.boldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("End address", EditorStyles.boldLabel);
            }
            for (int i = 0; i < d.ManagedHeapSections.Length; i++)
            {
                var s = d.ManagedHeapSections[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(50));
                    EditorGUILayout.LabelField($"0x{s.StartAddress:X}", GUILayout.Width(140));
                    EditorGUILayout.LabelField(FormatBytes(s.Size), GUILayout.Width(120));
                    EditorGUILayout.LabelField(s.IsExecutable ? "yes" : "no", GUILayout.Width(50));
                    EditorGUILayout.LabelField($"0x{s.StartAddress + (ulong)s.Size:X}");
                }
            }
        }

        // ---- Tab: Roots ----

        void DrawRootReferences()
        {
            var d = m_data!;
            DrawSearchBar(ref m_filter, ref m_page, d.NativeRootReferences.Length);
            var filtered = FilterIndices(d.NativeRootReferences.Length, idx =>
            {
                var r = d.NativeRootReferences[idx];
                return (r.AreaName + " " + r.ObjectName).IndexOf(m_filter, StringComparison.OrdinalIgnoreCase) >= 0;
            });
            DrawPager(filtered.Length, ref m_page);
            var page = PageOf(filtered, m_page);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Id", EditorStyles.boldLabel, GUILayout.Width(140));
                EditorGUILayout.LabelField("Area", EditorStyles.boldLabel, GUILayout.Width(180));
                EditorGUILayout.LabelField("Object", EditorStyles.boldLabel, GUILayout.Width(220));
                EditorGUILayout.LabelField("Accum. size", EditorStyles.boldLabel);
            }
            foreach (int i in page)
            {
                var r = d.NativeRootReferences[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"0x{r.Id:X}", GUILayout.Width(140));
                    EditorGUILayout.LabelField(Truncate(r.AreaName, 35), GUILayout.Width(180));
                    EditorGUILayout.LabelField(Truncate(r.ObjectName, 40), GUILayout.Width(220));
                    EditorGUILayout.LabelField(FormatBytes((long)r.AccumulatedSize));
                }
            }
        }

        // ---- Tab: Regions ----

        void DrawMemoryRegions()
        {
            var d = m_data!;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("#", EditorStyles.boldLabel, GUILayout.Width(40));
                EditorGUILayout.LabelField("Name", EditorStyles.boldLabel, GUILayout.Width(200));
                EditorGUILayout.LabelField("Base", EditorStyles.boldLabel, GUILayout.Width(140));
                EditorGUILayout.LabelField("Size", EditorStyles.boldLabel, GUILayout.Width(120));
                EditorGUILayout.LabelField("Allocs", EditorStyles.boldLabel);
            }
            for (int i = 0; i < d.NativeMemoryRegions.Length; i++)
            {
                var r = d.NativeMemoryRegions[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(40));
                    EditorGUILayout.LabelField(Truncate(r.Name, 40), GUILayout.Width(200));
                    EditorGUILayout.LabelField($"0x{r.AddressBase:X}", GUILayout.Width(140));
                    EditorGUILayout.LabelField(FormatBytes((long)r.AddressSize), GUILayout.Width(120));
                    EditorGUILayout.LabelField(r.NumAllocations.ToString("N0"));
                }
            }
        }

        // ---- Shared UI helpers ----

        static void DrawSearchBar(ref string filter, ref int page, int total)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Filter", GUILayout.Width(40));
                var newFilter = GUILayout.TextField(filter, GUILayout.Width(300));
                if (newFilter != filter) { filter = newFilter; page = 0; }
                GUILayout.Label($"{total:N0} items", EditorStyles.miniLabel);
            }
            EditorGUILayout.Space(2);
        }

        static void DrawPager(int filteredCount, ref int page)
        {
            int pages = Mathf.Max(1, Mathf.CeilToInt((float)filteredCount / kPageSize));
            page = Mathf.Clamp(page, 0, pages - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"{filteredCount:N0} matches", EditorStyles.miniLabel, GUILayout.Width(120));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("◀", EditorStyles.miniButtonLeft)) page = Mathf.Max(0, page - 1);
                GUILayout.Label($"{page + 1}/{pages}", EditorStyles.miniButtonMid, GUILayout.Width(60));
                if (GUILayout.Button("▶", EditorStyles.miniButtonRight)) page = Mathf.Min(pages - 1, page + 1);
            }
            EditorGUILayout.Space(2);
        }

        int[] FilterIndices(int total, Func<int, bool> predicate)
        {
            if (string.IsNullOrEmpty(m_filter)) return AllIndices(total);
            var list = new List<int>();
            for (int i = 0; i < total; i++)
                if (predicate(i)) list.Add(i);
            return list.ToArray();
        }

        static int[] AllIndices(int total)
        {
            var arr = new int[total];
            for (int i = 0; i < total; i++) arr[i] = i;
            return arr;
        }

        int[] PageOf(int[] filtered, int page)
        {
            int start = page * kPageSize;
            int count = Mathf.Min(kPageSize, filtered.Length - start);
            if (count <= 0) return Array.Empty<int>();
            var result = new int[count];
            Array.Copy(filtered, start, result, 0, count);
            return result;
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.Separator();
        }

        static void Label(string key, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(key, GUILayout.Width(180));
                EditorGUILayout.SelectableLabel(value, EditorStyles.textField, GUILayout.Height(18));
            }
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
