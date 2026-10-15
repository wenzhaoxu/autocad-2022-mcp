# Changelog

## Unreleased

### 天正符号插入的比例与基点（补正）

- **比例**：天正插入元件带图纸比例，直接用 `scale=1` 会得到小几百倍的符号。
  实测本图纸（出图比例 1:100）为 **1500**，关系式为 **插入比例 = 15 × DIMSCALE**；
  最可靠的取值方式是读取图中任一已有天正元件 INSERT 的 `scale` 因子。
  校验：提取出的 `element/00000277.dwg`（接地）原始几何 0.5 × 0.625，×1500 = 750 × 937.5，
  与天正手工插入实例的实测包围盒完全一致。
- **基点**：天正把符号插入基点写进 DWG 的 `INSBASE`，`cad_block import` 会把它作为块基点，
  插入点与几何落点一致，无需额外平移。
- `docs/tangent-integration.md` 的自动化流程示例已按上述结论补上 `scale` 与基点说明。

### 天正电气图库（ArchStar）格式逆向 + 离线提取器

- 新增 `docs/tangent-dwb-format.md`：完整逆向报告——`.DWB/.SLB/.TK/.TKW` 文件族职责、
  模块/导出符号调用链、`ArchStar DWG  Library 1.0` 容器布局、`AS LZW` 编解码参数
  （LSB 优先 / 9 位起 / `256` 为字典重置 / **`257` 是字面量而非 EOI** / 字典上限 4000 /
  位流起点 `块内 +0x28`）、名词索引方法与踩坑记录。
- 新增 `tools/tangent-dwb-extract/`：无第三方依赖的离线提取器。
  实测在本机天正 T20 V9 上解出 **2380 个符号 DWG**（各库成功率 76%~100%），
  提取文件可在 AutoCAD 2022 直接打开（`AC1015`，含 `LWPOLYLINE` 与 `ATTDEF`），
  并提供按名称检索的 `index-by-name.txt`。
- 该成果使"复用天正符号库"不再依赖天正界面入库：把提取出的 DWG 交给 `cad_block import`
  即可在任意图纸中自动插入（见 `docs/tangent-integration.md`）。

### 可配置的命令白名单（支持第三方命令）

- `cad_command` 的白名单现在可由运行时配置扩充：插件程序集同目录的 `allowed-commands.txt`，
  或环境变量 `AUTOCAD_MCP_EXTRA_COMMANDS`（逗号/分号/换行分隔，`#` 后可写注释）。
  无需重新编译插件，重启 AutoCAD 即生效。
- 统一了两条校验路径（`Command()` 与 `QueueInteractiveOperationAsync()`），此前只放开其中一条会导致
  `waitForCompletion` 默认值下第三方命令仍被拦截。
- `install-plugin.ps1` 会在存在时一并部署 `allowed-commands.txt`。

### 修复 FILEDIA=1 时导出命令弹出文件对话框并阻塞队列

- `saveCopy`、`cad_export format=dwg|dxf|stl` 现在与 `saveAs` 一致：命令执行期间强制 `FILEDIA=0`，
  结束/失败/取消后恢复。
- 在天正电气等**接管了 `-WBLOCK` / `DXFOUT` 原生命令**的环境中，`FILEDIA=1` 会弹出
  「创建图形文件」模态对话框，阻塞命令队列并让桥接 119 秒超时。修复后 `saveCopy` 立即返回。

### `cad_block import` 支持导入源 DWG 中的指定块

- 源 DWG 中存在与 `name` 同名的块定义时，只克隆该块（含依赖对象），而不是把整个文件当成一个块。
- 顺带消除同名时 `Database.Insert` 抛出的 `eSelfReference`。
- 使「一次性入库 → 之后全自动复用」的第三方符号库流程成为可能（见 `docs/tangent-integration.md`）。

### 文档

- 新增 `docs/tangent-integration.md`：天正电气 T20 符号库的自动化路径、边界、符号编号检索方法，
  以及天正环境的探测手段（`tchdata.dbf` / `tchcmd.txt` / `.TK` 索引，均为 GBK 编码）。

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
