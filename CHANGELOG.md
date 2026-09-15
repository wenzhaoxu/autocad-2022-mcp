# Changelog

## 0.0.3 - 2026-09-11

### Added

- `cad_solid` for WCS box, sphere, cylinder, cone, and transactional boolean operations.
- `cad_measure` for extents, curve length, planar area, 3D volume, centroid, and density-based mass.
- `cad_capabilities` for an exact machine-readable feature and limit matrix.
- Material dictionary inspection through `cad_inspect`.
- Verified asynchronous STL export for selected `3DSOLID` handles, replacing unsafe one-off export DLLs.

### Improved

- Added parameterized model recipes with preflight validation, batched primitive creation, boolean feature tracking, rebuild cleanup, and compensating rollback.
- Reduced a 500-primitive recipe from 500 named-pipe round trips to 2 bounded CAD transactions.
- Throttled instance-registration heartbeats to eliminate continuous disk writes from AutoCAD Idle events.
- Allowed concurrent pipe clients to queue safely while retaining single-threaded CAD database execution.
- Made bridge and native-command task continuations asynchronous to avoid re-entrant UI-thread continuations.
- Made bridge start/stop idempotent and prevented a stopped bridge from being re-registered by its Idle hook.
- Correct box-origin semantics and arbitrary-axis primitive orientation.
- Health output now reports version, normal state, and an empty warning list when no actionable condition exists.
- Default command timeout increased to 120 seconds for large DWG operations.
- Removed remaining direct `Editor.Command` calls from MCP draw and view paths.
- Native save, SaveAs, WBLOCK, and DXFOUT sequences are verified against AutoCAD 2024 Core Engine.

### Alert cleanup

- Eliminated the re-entrant command and direct Idle-save paths that generated CER dialogs.
- Historical CER evidence is preserved; security warnings and `SECURELOAD` are not suppressed.

## 0.0.2 - 2026-09-11

### Fixed

- Execute `save` and `saveAs` through asynchronous native `QSAVE`/`SAVEAS` commands instead of calling `AcDbDatabase.saveAs` from Idle.
- Avoid recursive `DocumentLock` acquisition inside AutoCAD command callbacks.
- Export DWG through AutoCAD's asynchronous native `-WBLOCK` command so the active document is not renamed.
- Give each AutoCAD process a PID-specific named pipe and publish an atomic instance registration.
- Reject ambiguous routing when several AutoCAD processes are running; add `cad_instances` and `cad_connect`.
- Serialize bridge requests so database operations cannot overlap.
- Deploy versioned plugin DLLs instead of overwriting an assembly loaded by AutoCAD.

### Safety

- `saveCopy` refuses to overwrite an existing file unless `overwrite=true`.
- Untitled drawings must be explicitly saved before a non-discarding close.
- Stale instance registrations are ignored when their owning process no longer exists.
