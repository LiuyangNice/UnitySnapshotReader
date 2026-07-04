// SnapshotReader.Cli — standalone console tool to dump a .snap / .snapshot file summary.
//
// Usage:
//   dotnet run --project Cli -- <path-to-snap-file>
//   dotnet run --project Cli -- --json <path-to-snap-file>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SnapshotReader;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: SnapshotReader.Cli [--json] <path-to-snap-file>");
            return 1;
        }

        bool jsonMode = false;
        string path;

        if (args[0] == "--json")
        {
            jsonMode = true;
            path = args.Length > 1 ? args[1] : "";
        }
        else
        {
            path = args[0];
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        try
        {
            Console.Error.WriteLine($"Loading: {path} ({new FileInfo(path).Length:N0} bytes)");
            var data = SnapshotReader.SnapshotReader.Load(path);
            Console.Error.WriteLine("OK — parsed successfully.\n");

            if (jsonMode)
                PrintJson(data);
            else
                PrintSummary(data);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static void PrintSummary(SnapshotData data)
    {
        var vm = data.VirtualMachineInformation;

        Console.WriteLine("╔══════════════════════════════════════════════╗");
        Console.WriteLine("║        Unity Snapshot Summary                ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"  File      : {data.FilePath}");
        Console.WriteLine($"  Size      : {FormatBytes(data.FileSize)} ({data.FileSize:N0} bytes)");
        Console.WriteLine($"  Version   : 0x{data.FormatVersion:X8}");
        Console.WriteLine($"  SnapVer   : {data.SnapshotVersion}");
        Console.WriteLine($"  RecordDate: {data.RecordDate}");
        Console.WriteLine();
        Console.WriteLine($"  ── VM Info ──");
        Console.WriteLine($"  PointerSize          : {vm.PointerSize}");
        Console.WriteLine($"  ObjectHeaderSize     : {vm.ObjectHeaderSize}");
        Console.WriteLine($"  ArrayHeaderSize      : {vm.ArrayHeaderSize}");
        Console.WriteLine($"  AllocationGranularity: {vm.AllocationGranularity}");
        Console.WriteLine();

        if (data.UserMetadata is { Count: > 0 })
        {
            Console.WriteLine($"  ── User Metadata ({data.UserMetadata.Count}) ──");
            foreach (var kv in data.UserMetadata)
                Console.WriteLine($"    {kv.Key} = {kv.Value}");
            Console.WriteLine();
        }

        static void Row(string label, string val) =>
            Console.WriteLine($"  {label,-30} {val}");

        Console.WriteLine("  ── Counts ──");
        Row("Managed types",           data.ManagedTypeCount.ToString("N0"));
        Row("Managed fields",          data.FieldCount.ToString("N0"));
        Row("Native types",            data.NativeTypeCount.ToString("N0"));
        Row("Native objects",          data.NativeObjectCount.ToString("N0"));
        Row("GC handles",              data.GCHandleCount.ToString("N0"));
        Row("Connections (edges)",     data.ConnectionCount.ToString("N0"));
        Row("Managed heap sections",   data.ManagedHeapSectionCount.ToString("N0"));
        Row("Native root references",  data.NativeRootReferenceCount.ToString("N0"));
        Row("Native memory regions",   data.NativeMemoryRegionCount.ToString("N0"));
        Console.WriteLine();

        Console.WriteLine("  ── Totals ──");
        Row("Managed heap bytes",      FormatBytes(data.ManagedHeapTotalBytes));
        Row("Native object bytes",     FormatBytes(data.NativeObjectsTotalBytes));
        Console.WriteLine();

        // Top 10 native types by total size
        Console.WriteLine("  ── Top 10 Native Types by Size ──");
        var totals = new Dictionary<string, (long bytes, int count)>();
        for (int i = 0; i < data.NativeObjects.Length; i++)
        {
            var obj = data.NativeObjects[i];
            string name = obj.TypeIndex >= 0 && obj.TypeIndex < data.NativeTypes.Length
                ? data.NativeTypes[obj.TypeIndex].Name : "<unknown>";
            if (totals.TryGetValue(name, out var v))
                totals[name] = (v.bytes + (long)obj.Size, v.count + 1);
            else
                totals[name] = ((long)obj.Size, 1);
        }
        var sorted = totals.OrderByDescending(kv => kv.Value.bytes).Take(10);
        foreach (var kv in sorted)
            Console.WriteLine($"  {kv.Key,-35} {kv.Value.count,8} objects  {FormatBytes(kv.Value.bytes),12}");
        Console.WriteLine();

        // Top 10 native objects by size
        Console.WriteLine("  ── Top 10 Native Objects by Size ──");
        var topObjs = data.NativeObjects
            .OrderByDescending(o => o.Size)
            .Take(10);
        foreach (var obj in topObjs)
        {
            string typeName = obj.TypeIndex >= 0 && obj.TypeIndex < data.NativeTypes.Length
                ? data.NativeTypes[obj.TypeIndex].Name : "?";
            Console.WriteLine($"  {FormatBytes((long)obj.Size),10}  [{typeName}] {obj.Name} (id={obj.InstanceId})");
        }
        Console.WriteLine();

        // Managed heap layout
        if (data.ManagedHeapSections.Length > 0)
        {
            Console.WriteLine("  ── Managed Heap Sections ──");
            foreach (var section in data.ManagedHeapSections)
                Console.WriteLine($"  0x{section.StartAddress:X16}  {FormatBytes(section.Size),10}  {(section.IsExecutable ? "exec" : "    ")}");
            Console.WriteLine();
        }
    }

    static void PrintJson(SnapshotData data)
    {
        var json = new System.Text.StringBuilder();
        json.AppendLine("{");
        json.AppendLine($"  \"filePath\": \"{EscapeJson(data.FilePath)}\",");
        json.AppendLine($"  \"fileSize\": {data.FileSize},");
        json.AppendLine($"  \"formatVersion\": \"0x{data.FormatVersion:X8}\",");
        json.AppendLine($"  \"snapshotVersion\": {data.SnapshotVersion},");
        json.AppendLine($"  \"recordDate\": {data.RecordDate},");
        json.AppendLine($"  \"pointerSize\": {data.VirtualMachineInformation.PointerSize},");
        json.AppendLine($"  \"managedTypeCount\": {data.ManagedTypeCount},");
        json.AppendLine($"  \"fieldCount\": {data.FieldCount},");
        json.AppendLine($"  \"nativeTypeCount\": {data.NativeTypeCount},");
        json.AppendLine($"  \"nativeObjectCount\": {data.NativeObjectCount},");
        json.AppendLine($"  \"gcHandleCount\": {data.GCHandleCount},");
        json.AppendLine($"  \"connectionCount\": {data.ConnectionCount},");
        json.AppendLine($"  \"managedHeapBytes\": {data.ManagedHeapTotalBytes},");
        json.AppendLine($"  \"nativeObjectsBytes\": {data.NativeObjectsTotalBytes}");
        json.Append("}");
        Console.WriteLine(json.ToString());
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
