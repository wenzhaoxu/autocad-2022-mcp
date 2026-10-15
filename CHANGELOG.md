# Changelog

## Unreleased

### AutoCAD 2022 (R24.1) 移植

- 插件清单 `SeriesMin/SeriesMax` 由 `R24.3` 改为 `R24.1`；AutoCAD 2022 现在会正常加载该 bundle。
- 修复 `cad_document new` 在 AutoCAD 2022 上挂起：`DocumentCollection.Add("")` 需要具体模板路径，空模板时改为按 `DEFAULT_TEMPLATE` → `LOCALROOTPREFIX\Template\acadiso.dwt` → `acad.dwt` → 目录内任一 `.dwt` 的顺序解析。
- 修复 `cad_document close` 在 AutoCAD 2022 上静默失败（返回 `result: null` 且文档未关闭）：`Document.CloseAndDiscard()` 不允许在命令上下文内执行，已将 `close` 移出 `RequiresCommandContext`，仅 `new`/`open`/`activate` 仍走命令上下文。
- 修复命令上下文回调吞异常导致 `result: null` 的静默"成功"：`DispatchInCommandContextAsync` 现在显式捕获并在上下文返回后重新抛出异常，调用方会收到真实错误码。
- 插件工程默认 `AutoCADDir` 由 `C:\Program Files\Autodesk\AutoCAD 2024` 改为 `...\AutoCAD 2022`，`dotnet build` 无需额外参数即可对 2022 程序集编译。
- 安装脚本输出提示由 "Restart AutoCAD 2024" 更正为 2022。
- 新增 `docs/autocad-2022-port.md`，记录部署前提（AutoCAD 必须以普通用户权限运行，否则命名管道受 Windows 强制完整性控制拦截）、已验证能力与已知限制。

## 0.1.1 - 2026-09-16

- 修复 `cad_document saveAs` 在 `FILEDIA=1` 时打开交互式文件窗口；保存完成、失败或取消后恢复原设置。
- `cad_document save` 在未命名图纸上返回 `UNTITLED_DOCUMENT`，避免 `QSAVE` 打开保存窗口。
- 新增电气图形符号目录、单符号 MCP 资源及 `cad_electrical_symbol`，可将常用标准对齐符号插入为可编辑原生实体。
- 新增电气图样幅面标准资源及 `cad_electrical_sheet`，生成 A0-A4 外框、图框、标题栏、会签栏和偶数图幅分区。

## 0.1.0 - 2026-09-16

- 新增 `cad_events` 双向变更日志，覆盖文档、命令、实体、MCP 操作和可视化步骤。
- `cad_status` 与检查结果增加图纸修订号；写工具支持文档及修订冲突保护。
- 新增 `silent`、`batch`、`step` 三种显示模式，逐实体绘制可在 AutoCAD 中实时刷新、高亮和回滚。
- 原生撤销、白名单命令、PDF/DWF/SVG/PNG/JPEG 导出等待命令完成，不再提前报告成功。
- 新增三维多段线、样条曲线、射线、构造线、引线、表格及半径、直径、角度尺寸。
- 三维能力增加圆环体、楔体、棱锥，以及拉伸、旋转和扫掠实体特征。
- 修正 UCS/WCS 绘制转换、块属性写入、标注样式和图层线型应用。
- AutoCAD 插件与 Node MCP 统一升级为 0.1.0，并保持零编译警告。
- 新增 `CADMCPSELFTEST`，可在不保存图纸的情况下验证核心绘制和同步链路。

## 0.0.3 - 2026-09-11

### Added

- `cad_solid` for WCS box, sphere, cylinder, cone, and transactional boolean operations.
- `cad_measure` for extents, curve length, planar area, 3D volume, centroid, and density-based mass.
- `cad_capabilities` for an exact machine-readable feature and limit matrix.
- Material dictionary inspection through `cad_inspect`.
- Verified asynchronous STL export for selected `3DSOLID` handles, replacing unsafe one-off export DLLs.

### Improved

- Preserve CJK text end to end over the UTF-8 pipe, honor explicit `TEXT`/`MTEXT` styles, and automatically select a Unicode-capable style for CJK content.
- Add `cad_text_style` to inspect, create, update, select, and batch-apply AutoCAD text styles so drawings with intact Chinese text can be repaired without re-entering labels.
- Run document open/new/activate/close in AutoCAD command context instead of switching MDI documents directly from Idle.
- Support password-protected DWG/DWT input and return `DECRYPTION_FAILED` without exposing the supplied password.
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
