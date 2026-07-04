# Snapshot Reader

Unity Editor 插件，用于读取和浏览 Unity Memory Profiler 生成的 `.snap` / `.snapshot` 快照文件。

## 安装

### 方式一：通过 UPM 添加本地包
将本仓库克隆或复制到项目的 `Packages/` 目录下：
```
YourProject/Packages/com.snapshotreader.unity/
```

### 方式二：通过 Git URL（需有仓库权限）
在 `Packages/manifest.json` 中添加：
```json
{
  "dependencies": {
    "com.snapshotreader.unity": "https://github.com/your/repo.git"
  }
}
```

### 方式三：通过磁盘路径
在 `Packages/manifest.json` 中添加：
```json
{
  "dependencies": {
    "com.snapshotreader.unity": "file:../path/to/SnapshotReader"
  }
}
```

## 快速开始

### 在 Editor 中浏览快照

菜单栏 **Tools → Snapshot Reader** 打开浏览窗口，点击 **Open Snapshot…** 选择一个 `.snap` 或 `.snapshot` 文件。

窗口提供 6 个标签页：
| 标签页 | 内容 |
|--------|------|
| **Overview** | 文件元数据、VM 信息、各表计数、Top 15 类型内存排名 |
| **Native Objects** | 所有原生对象（搜索/分页），显示名称、类型、InstanceId、大小 |
| **Types** | 托管类型表（搜索/分页），显示名称、程序集、大小、字段数 |
| **Heap** | 托管堆各段的内存地址、大小、可执行标记 |
| **Roots** | 原生根引用（搜索/分页） |
| **Regions** | 原生内存区域 |

### 在代码中使用

```csharp
using SnapshotReader;

// 同步加载快照文件
SnapshotData data = SnapshotReader.Load(@"C:\path\to\snapshot.snap");

// 或使用异步加载（不阻塞 Editor）
SnapshotReader.LoadAsync(@"C:\path\to\snapshot.snap", data =>
{
    Debug.Log($"Loaded: {data.FilePath}");
    Debug.Log($"Managed types: {data.ManagedTypeCount}");
    Debug.Log($"Native objects: {data.NativeObjectCount}");
    Debug.Log($"Heap total: {data.ManagedHeapTotalBytes} bytes");
});

// 检查元数据
Debug.Log($"Snapshot version: {data.SnapshotVersion}");
Debug.Log($"Pointer size: {data.VirtualMachineInformation.PointerSize} bytes");

// 遍历托管类型
foreach (var type in data.ManagedTypes)
{
    Debug.Log($"[{type.Index}] {type.Name} ({type.Assembly}) size={type.Size}");
}

// 遍历原生对象
foreach (var obj in data.NativeObjects)
{
    string typeName = obj.TypeIndex >= 0 && obj.TypeIndex < data.NativeTypes.Length
        ? data.NativeTypes[obj.TypeIndex].Name : "?";
    Debug.Log($"{obj.Name} : {typeName} ({obj.Size} bytes)");
}

// 访问托管堆原始字节
foreach (var section in data.ManagedHeapSections)
{
    Debug.Log($"Heap section 0x{section.StartAddress:X} - {section.Bytes.Length} bytes");
}

// 查找原生对象
int instanceId = 42;
if (data.NativeObjectIndexByInstanceId.TryGetValue(instanceId, out int index))
{
    var obj = data.NativeObjects[index];
    Debug.Log($"Found: {obj.Name}");
}
```

## 公共 API 参考

### `SnapshotReader.SnapshotReader`

主入口类（一次性使用，调用 `Load` 后自动释放）。

| 方法 | 说明 |
|------|------|
| `SnapshotData Load(string path)` | 同步加载并解析快照文件（静态方法） |
| `void LoadAsync(string path, Action<SnapshotData> onComplete, Action<Exception>? onError)` | 异步加载，在后台线程解析，完成后在主线程回调 |

### `SnapshotReader.SnapshotData`

解析后的强类型数据容器。

| 属性 | 类型 | 说明 |
|------|------|------|
| `FilePath` | `string` | 文件路径 |
| `FileSize` | `long` | 文件大小 |
| `FormatVersion` | `uint` | 格式版本 (0x20170724) |
| `SnapshotVersion` | `uint` | 快照版本号 |
| `RecordDate` | `ulong` | 录制时间戳 |
| `VirtualMachineInformation` | `VirtualMachineInformation` | VM 信息（指针大小、对象头大小等） |
| `UserMetadata` | `Dictionary<string, string>?` | 用户自定义元数据 |
| `ManagedTypes` | `ManagedType[]` | 所有托管类型 |
| `ManagedFields` | `ManagedField[]` | 所有托管字段 |
| `NativeTypes` | `NativeType[]` | 所有原生类型 |
| `NativeObjects` | `NativeObject[]` | 所有原生对象 |
| `GCHandles` | `GCHandleInfo[]` | GC 句柄表 |
| `ManagedHeapSections` | `ManagedHeapSection[]` | 托管堆内存段（已按地址排序） |
| `NativeRootReferences` | `NativeRootReference[]` | 原生根引用 |
| `NativeMemoryRegions` | `NativeMemoryRegion[]` | 原生内存区域 |
| `Connections` | `Dictionary<int, List<int>>` | 托管对象引用图（邻接表） |
| `NativeObjectIndexByInstanceId` | `Dictionary<int, int>` | InstanceId → 数组索引 的映射 |

### 数据记录类型

所有记录类型均为 `readonly struct`，不可变且线程安全。

- `ManagedType` — 托管类型（Flags, Name, Assembly, Size, FieldIndices, StaticFieldBytes）
- `ManagedField` — 托管字段（Offset, TypeIndex, Name, IsStatic）
- `NativeObject` — 原生对象（TypeIndex, InstanceId, Name, ObjectAddress, Size, RootReferenceId, GCHandleIndex）
- `NativeType` — 原生类型（Name, BaseTypeIndex）
- `GCHandleInfo` — GC 句柄（TargetAddress）
- `ManagedHeapSection` — 堆段（StartAddress, IsExecutable, Bytes, Size）
- `NativeRootReference` — 根引用（Id, AreaName, ObjectName, AccumulatedSize）
- `NativeMemoryRegion` — 内存区域（Name, ParentIndex, AddressBase, AddressSize, NumAllocations）

## 文件格式说明

插件解析的是 Unity Memory Profiler 的二进制快照格式，布局如下：

```
[Header: 4B] ...payload... [Footer: 12B → Directory → BlockSection → Blocks → Chapters]
```

- **Header**: 魔术字 `0xAEABCDCD`
- **Footer**: 末尾 12 字节，指向 Directory + 签名 `0xABCDCDAE`
- **Directory**: 签名 `0xCDCDAEAB` + 版本 `0x20170724` + BlockSection 位置 + N 个 Chapter 位置
- **BlockSection**: 版本 + M 个 Block 位置
- **Blocks**: 每个 Block 是一组固定大小的 Chunk，逻辑上构成连续字节范围
- **Chapters**: 三种格式 — Value（单值）/ ConstArray（定长数组）/ VarArray（变长数组，用于字符串/字节块）

## 依赖

- Unity 2021.3+（Editor 窗口需要 UnityEditor API）
- 运行时部分可在纯 .NET 环境中编译使用（移除外 Editor 相关代码）

## 许可

MIT License
