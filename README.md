# AutoCAD 2024 MCP

面向 AutoCAD 2024（R24.3）的本机 MCP 模块。大模型把自然语言意图转换为有类型的 MCP 工具调用，Node.js 服务通过 Windows 命名管道连接 AutoCAD 进程内 .NET 插件，最终由 ObjectARX .NET API 读取或修改 DWG。

当前版本：`0.1.1`。

## 本仓库：AutoCAD 2022 移植分支（R24.1）

本仓库 fork 自 [cafedaily/autocad-2024-mcp](https://github.com/cafedaily/autocad-2024-mcp)，目标是让同一套 MCP 在 **AutoCAD 2022（R24.1）** 上工作。

与原仓库的差异（全部为 2022 兼容性修正，不含功能删减）：

| 变更 | 文件 | 原因 |
|---|---|---|
| 插件清单 `SeriesMin/SeriesMax` 由 `R24.3` 改为 `R24.1` | `bundle/AutoCAD2024Mcp.bundle/PackageContents.xml` | AutoCAD 2022 默认**不会加载**标记为 2024 的 bundle |
| 空模板时显式解析默认模板路径 | `plugin/AutoCAD2024Mcp/CadDispatcher.cs` | `DocumentCollection.Add("")` 在 2022 上会**挂起**（超时），必须传具体 `.dwt` 路径 |
| `close` 移出命令上下文 | `plugin/AutoCAD2024Mcp/CadDispatcher.cs` | `Document.CloseAndDiscard()` 不允许在命令上下文内执行，否则抛异常且被 AutoCAD 吞掉 |
| 命令上下文回调显式捕获并重新抛出异常 | `plugin/AutoCAD2024Mcp/CadDispatcher.cs` | AutoCAD 会吞掉命令上下文回调中的异常，导致调用方只看到 `result: null`（静默"成功"） |
| 构建默认路径由 AutoCAD 2024 改为 2022 | `plugin/AutoCAD2024Mcp/AutoCAD2024Mcp.csproj` | 直接 `dotnet build` 即可对 2022 程序集编译 |

部署要点、已验证能力与已知限制见 [`docs/autocad-2022-port.md`](docs/autocad-2022-port.md)。
天正电气（T20 V9）符号库的自动化复用见 [`docs/tangent-integration.md`](docs/tangent-integration.md)。

## 额外命令白名单

`cad_command` 默认只放行内置安全命令。需要驱动第三方命令（例如天正电气的 `YJCR`/`BZSB`）时，
把命令名写进插件程序集同目录的 `allowed-commands.txt`（仓库内模板见
[`plugin/AutoCAD2024Mcp/allowed-commands.txt`](plugin/AutoCAD2024Mcp/allowed-commands.txt)），
或设置环境变量 `AUTOCAD_MCP_EXTRA_COMMANDS`。**无需重新编译，重启 AutoCAD 生效。**

> 注意：天正自身的命令只能在人工交互下完成（见 `docs/tangent-integration.md`）；
> 用 MCP 自动化复用天正符号，应走 `cad_block import` + `insert`。

> 程序集名、命名空间、bundle 目录名、`CADMCP*` 命令、命名管道前缀与实例注册目录**维持原样**（`AutoCAD2024Mcp` / `autocad-2024-mcp-<pid>`），以确保升级路径稳定、不破坏既有安装。字符串 `product: "AutoCAD 2024"` 为硬编码，`cad_status.release` 会如实返回实际版本（2022 为 `24.1.0.0`）。

## 已实现能力

| MCP 工具 | 能力 |
|---|---|
| `cad_instances` | 列出已注册的 AutoCAD 进程、PID 和活动图纸 |
| `cad_connect` | 明确绑定一个 AutoCAD PID，避免多实例误路由 |
| `cad_capabilities` | 返回精确能力矩阵、限制及已验证导出格式 |
| `cad_status` | 连接、版本、活动图纸和当前图层 |
| `cad_events` | 按游标读取命令、实体、文档、MCP 操作和屏幕呈现事件 |
| `cad_document` | 新建、打开、切换、保存、另存、保存副本和关闭 DWG；支持密码保护图纸 |
| `cad_inspect` | 图纸、实体、图层、块、布局、样式、线型、系统变量 |
| `cad_draw` | 二维/三维多段线、样条曲线、构造线、文字、表格、引线、块、填充和完整常用尺寸 |
| `cad_electrical_symbol` | 从内置电气符号目录插入可编辑的原生二维实体，支持缩放、旋转、标注和逐步显示 |
| `cad_electrical_sheet` | 生成 A0-A4 外框、图框、标题栏、会签栏和偶数图幅分区 |
| `cad_solid` | 七类三维基元、拉伸、旋转、扫掠及布尔并集/差集/交集 |
| `cad_model_build` | 用参数化配方批量构建或重建可编辑三维实体，并在失败时补偿回滚 |
| `cad_measure` | 包围盒、长度、面积、体积、质心及密度换算质量 |
| `cad_edit` | 属性、移动、复制、旋转、缩放、镜像和删除 |
| `cad_layer` | 图层创建、修改、改名、删除、当前层、隔离和恢复 |
| `cad_text_style` | 查询、创建、修改、设为当前或批量应用文字样式；提供中文/CJK 字体支持 |
| `cad_block` | 块定义、插入和从 DWG 导入 |
| `cad_layout` | 布局创建、改名、删除、切换和出图设置 |
| `cad_export` | 已验证 DWG、DXF、选定三维实体的 STL，以及队列式 PDF/DWF/SVG/PNG/JPEG |
| `cad_view` | 范围缩放、窗口缩放、视图恢复和重生成 |
| `cad_undo` | 撤销和重做 |
| `cad_command` | 受白名单保护的 AutoCAD 原生命令兜底 |

插件还提供 `CADMCPDIFFERENTIAL` 和 `CADMCPDIFFERENTIAL3D` 示例生成命令，分别生成差速器二维装配剖视图及可编辑的三维组件装配模型。

精确修改以 AutoCAD `Handle` 为稳定标识。建议先用 `cad_inspect` 查询，再把返回的 handle 交给 `cad_edit`。读取结果同时返回 `documentId` 和 `revision`；修改时可传入 `expectedDocumentId`、`expectedRevision`，若用户在 AutoCAD 中已经修改图纸，MCP 会返回冲突而不是覆盖新状态。每个 AutoCAD 进程使用独立的 PID 管道；存在多个实例时必须先调用 `cad_instances` 和 `cad_connect`。数据库写入仍在 AutoCAD 主线程串行执行，事件读取可在长命令运行期间并行完成。

## 实时绘制同步

结构化修改支持三种显示模式：

- `silent`：不主动重生成或改变当前选择集，适合后台批处理。
- `batch`：默认模式；完成一次 MCP 修改后立即重生成并在 AutoCAD 命令行确认。
- `step`：`cad_draw` 和三维基元创建会逐实体提交、刷新、高亮并显示步骤进度；中途失败会清理本次已经创建的实体。

示例：

```json
{
  "entities": [
    { "type": "line", "start": [0, 0], "end": [100, 0] },
    { "type": "circle", "center": [50, 30], "radius": 15 }
  ],
  "coordinateSystem": "UCS",
  "visualMode": "step",
  "highlightResults": true,
  "zoomToResult": true,
  "expectedDocumentId": "2A5F0C1",
  "expectedRevision": 18
}
```

调用 `cad_events` 并把上次返回的 `nextSequence` 作为新的 `afterSequence`，即可增量读取用户手工操作和 MCP 操作。事件缓冲区保存最近 4096 条记录；`overflowed=true` 表示游标过旧，需要重新执行 `cad_status` 和 `cad_inspect` 建立状态基线。

## 环境

- Windows 10/11 x64
- AutoCAD 2024，内部版本 R24.3
- Node.js 20 或更高版本
- 构建插件时需要 Visual Studio Build Tools 2022 或 .NET SDK（目标为 .NET Framework 4.8）

本机非标准 AutoCAD 安装目录可在构建时传入 `AutoCADDir`：

```powershell
dotnet build .\plugin\AutoCAD2024Mcp\AutoCAD2024Mcp.csproj -c Release -p:AutoCADDir="D:\Autodesk\AutoCAD 2024"
```

## 安装

在仓库根目录执行：

```powershell
npm install
npm run check
dotnet build .\plugin\AutoCAD2024Mcp\AutoCAD2024Mcp.csproj -c Release
.\scripts\install-plugin.ps1
.\scripts\register-codex.ps1
```

如果已经有编译后的 `plugin\AutoCAD2024Mcp\bin\Release\AutoCAD2024Mcp.dll`，可以跳过插件构建。安装脚本只复制到当前用户的 `%APPDATA%\Autodesk\ApplicationPlugins\AutoCAD2024Mcp.bundle`。

重启 AutoCAD 2024，命令行执行 `CADMCPSTATUS`。显示 0.1.1 后可执行 `CADMCPSELFTEST`；该命令临时创建并清理测试实体，用于验证结构化绘制、三维实体、检查和事件日志。随后重启 Codex 使 MCP 工具目录刷新。也可以把 [mcp.example.json](./mcp.example.json) 的内容合并到支持 JSON MCP 配置的客户端。

## 验收

AutoCAD 中至少打开一张图，然后执行：

```powershell
node .\scripts\smoke-test.mjs
```

随后可在支持 MCP 的对话中输入：

```text
读取当前图纸所有图层，找到名为 WALL 的闭合多段线，将它向右复制 3000 mm，
在 DIM 图层补一条对齐尺寸，检查结果后另存为 D:\output\revised.dwg。
```

推荐调用顺序是 `cad_status`、`cad_inspect`、`cad_edit/cad_draw`、再次 `cad_inspect`、`cad_export`。

## 中文文字与字体

MCP 与 AutoCAD 插件之间使用 UTF-8 JSON，`TEXT` 和 `MTEXT` 内容按 Unicode 原样传递。绘制包含中文、日文或韩文的文字且没有指定 `style` 时，插件会自动使用或创建 `MCP_CJK` 样式；也可以通过 `cad_text_style` 明确管理工程字体。

推荐先创建样式，再在 `cad_draw` 中引用：

```json
{
  "action": "create",
  "name": "MCP_CJK",
  "fontFile": "msyh.ttc",
  "bigFontFile": "",
  "xScale": 1
}
```

```json
{
  "entities": [{
    "type": "mtext",
    "position": [0, 0],
    "text": "车间工艺通风：一号风机运行反馈",
    "height": 5,
    "width": 120,
    "style": "MCP_CJK"
  }]
}
```

修复现有图纸中的问号或方框时，先调用 `cad_inspect`，确认返回的 `text` 字段仍是中文。如果内容仍在，仅需创建中文样式并使用 `cad_text_style` 的 `apply` 操作批量应用到 `TEXT`/`MTEXT`；如果返回内容已经是真实的 `?`，原字符已在更早的非 Unicode 流程中丢失，需要从源文件或备份恢复，字体替换无法还原。

字体文件必须已经安装在运行 AutoCAD 的 Windows 系统中。为保证其他工作站和 PDF 出图一致，项目交付时应统一字体版本；不要依赖接收方不存在的 SHX 大字体。DXF 请通过 `cad_export` 交给 AutoCAD 原生命令生成，避免用非 Unicode 文本工具重写 DXF。

## 电气制图资源

MCP 发布下列内置资源：

- `cad://standards/electrical-drawing-sheet`：A0-A4 幅面、图框、标题栏、会签栏、图号和图幅分区基线。
- `cad://symbols/electrical/catalog`：全部可调用电气符号的目录、类别、别名和端子信息。
- `cad://symbols/electrical/{symbolId}`：单个符号的规范化二维几何资源。

`cad_electrical_symbol` 将目录中的电阻、电容、电感、变压器、电源、接地、开关、按钮、保护、继电器、信号、仪表、电机、半导体和连接类符号展开为原生 `LINE`、`POLYLINE`、`CIRCLE`、`ARC`、`TEXT`，后续仍可通过 `cad_edit` 修改。符号语义按常用 IEC 60617 / GB/T 4728 表达方式对齐；正式出图前仍需按项目采用的标准版本复核。

`cad_electrical_sheet` 默认 A0-A3 为横向、A4 为纵向；装订边为左侧 25 mm，A0-A2 其他边距为 10 mm，A3-A4 为 5 mm。A3/A4 可通过 `extensionModules` 按短边模数增加长边。边距、方向、分区数量、图签字段、会签人员、图层和中文文字样式均可覆盖。横向数字与纵向大写字母从标题栏对角的左上角开始，横纵分区数仅接受偶数。

## 安全边界

- 仅监听本机命名管道，不开放 TCP 端口。
- 文件路径必须为绝对本机路径。
- 结构化修改使用事务；失败自动回滚。
- 保存通过异步原生 `QSAVE`/`SAVEAS` 执行；另存为期间临时禁用 `FILEDIA`，并在完成、失败或取消后恢复原设置；禁止从 Idle 回调直接调用 `AcDbDatabase.saveAs`。
- 新建、打开、激活及关闭图纸通过 AutoCAD 命令上下文执行，避免在 Idle 回调中切换 MDI 文档导致挂起。
- `saveCopy` 和 DWG 导出使用异步原生 `-WBLOCK`，不改变活动图纸的文件名。
- 插件 DLL 使用版本化文件名部署，不覆盖 AutoCAD 已加载的程序集。
- 正式 DLL 仅通过 `%APPDATA%\Autodesk\ApplicationPlugins` Bundle 自动加载；不要手动 `NETLOAD` 仓库 `plugin` 目录中的开发 DLL。
- 所有公开 AutoCAD 命令都有异常边界，API 错误写入命令行或 MCP 结构化响应，不弹出未处理异常框。
- `cad_command` 只允许运行插件中的固定白名单，避免任意命令和脚本执行。
- 默认不自动保存。只有明确调用 `cad_document save/saveAs` 或 `cad_export` 才写文件。
- 批量工具设有数量上限，实体读取支持分页。

## 已知边界

- PDF、DWF、SVG 和位图使用 AutoCAD 原生命令并等待命令结束及目标文件出现；交互式出图配置仍应预先保存在布局中。
- 当前结构化几何覆盖常用二维制图和实体三维建模。曲面、网格、参数化约束、动态块和行业版对象尚未封装，不能把本版本描述成全部 AutoCAD API 的完整映射。
- AutoCAD 必须处于运行状态。MCP 服务不会绕过 AutoCAD 授权，也不会在服务器端解析专有 DWG。

## 开发

```powershell
npm run dev
npm run check
npm run bench
```

协议是一条 UTF-8 JSON 请求和一条 JSON 响应，每个管道连接处理一次请求。插件的 `Application.Idle` 回调把后台请求切换到 AutoCAD 主线程，避免跨线程访问数据库。

卸载插件：

```powershell
.\scripts\uninstall-plugin.ps1
```
