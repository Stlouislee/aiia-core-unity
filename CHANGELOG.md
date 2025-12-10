# Changelog

All notable changes to Unity LiveLink will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2024-12-10

### Added

- Initial release of Unity LiveLink
- WebSocket server for bidirectional communication
- Scene hierarchy serialization and synchronization
- Delta sync for efficient updates
- Command handling for spawn, transform, delete, rename, set_parent, set_active
- Custom editor inspector with status display and controls
- MainThreadDispatcher for thread-safe Unity API access
- Support for configurable sync scope (WholeScene / TargetObjectOnly)
- Spawnable prefabs registry
- Python and Node.js client examples in documentation
