# Changelog

All notable changes to the Snapshot Reader package will be documented in this file.

## [1.0.0] - 2025-06-18

### Added
- Initial release of Snapshot Reader
- Full binary parser for Unity Memory Profiler .snap / .snapshot files
- Editor window with 6 tabs: Overview, Native Objects, Types, Heap, Roots, Regions
- Search + pagination for large data sets (500 items/page)
- Strongly typed C# API: `SnapshotReader.Load(path)`, `SnapshotReader.LoadAsync(path)`, `SnapshotReader.Load(byte[])`
- Support for all well-known chapter types (Value, ConstArray, VarArray)
- Decodes: metadata, managed types/fields, native types/objects, GC handles, managed heap, connections, root references, memory regions, user metadata
- Public API fully documented with XML comments
- Comprehensive README with installation and usage examples
