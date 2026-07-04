# Snapshot Reader

> 一款 Unity 插件和独立 CLI 工具，用于读取和浏览 Unity Memory Profiler 生成的 `.snap` / `.snapshot` 快照文件。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Unity](https://img.shields.io/badge/Unity-2021.3+-brightgreen)](#)

## 📦 快速开始

### Unity Editor 插件

将 `SnapshotReader/` 目录放入项目的 `Packages/` 下，或通过 UPM 安装：

```json
// Packages/manifest.json
{
  "dependencies": {
    "com.snapshotreader.unity": "https://github.com/LiuyangNice/UnitySnapshotReader.git?path=SnapshotReader"
  }
}
```

菜单栏 **Tools → Snapshot Reader** 打开浏览窗口。

### CLI 命令行工具

```bash
cd SnapshotReader
dotnet run --project Cli -- "path/to/snapshot.snap"
```

## 📊 功能

- **Unity Editor 窗口** — 6 个标签页浏览快照数据
- **C# API** — `SnapshotReader.Load()`, `LoadAsync()`, `Load(byte[])`
- **CLI 工具** — 终端输出内存摘要
- **全数据覆盖** — 托管类型/字段、原生对象/类型、GC 句柄、托管堆、引用图、内存区域

## 📖 详细文档

详见 [`SnapshotReader/README.md`](SnapshotReader/README.md)。

## 许可证

[MIT](LICENSE)
