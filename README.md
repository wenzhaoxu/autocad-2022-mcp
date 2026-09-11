# AutoCAD 2024 MCP

面向 AutoCAD 2024（R24.3）的本机 MCP 模块。大模型把自然语言意图转换为有类型的 MCP 工具调用，Node.js 服务通过 Windows 命名管道连接 AutoCAD 进程内 .NET 插件，最终由 ObjectARX .NET API 读取或修改 DWG。

当前版本：`0.0.3`。

## 已实现能力

| MCP 工具 | 能力 |
|---|---|
| `cad_instances` | 列出已注册的 AutoCAD 进程、PID 和活动图纸 |
| `cad_connect` | 明确绑定一个 AutoCAD PID，避免多实例误路由 |
| `cad_capabilities` | 返回精确能力矩阵、限制及已验证导出格式 |
| `cad_status` | 连接、版本、活动图纸和当前图层 |
| `cad_document` | 新建、打开、切换、保存、另存、保存副本和关闭 DWG |
| `cad_inspect` | 图纸、实体、图层、块、布局、样式、线型、系统变量 |
| `cad_draw` | 直线、多段线、圆、圆弧、椭圆、点、文字、块参照和尺寸 |
| `cad_solid` | 三维长方体、球体、圆柱、圆锥及布尔并集/差集/交集 |
| `cad_measure` | 包围盒、长度、面积、体积、质心及密度换算质量 |
| `cad_edit` | 属性、移动、复制、旋转、缩放、镜像和删除 |
| `cad_layer` | 图层创建、修改、改名、删除、当前层、隔离和恢复 |
| `cad_block` | 块定义、插入和从 DWG 导入 |
| `cad_layout` | 布局创建、改名、删除、切换和出图设置 |
| `cad_export` | 已验证 DWG、DXF、选定三维实体的 STL，以及队列式 PDF/DWF/SVG/PNG/JPEG |
| `cad_view` | 范围缩放、窗口缩放、视图恢复和重生成 |
| `cad_undo` | 撤销和重做 |
| `cad_command` | 受白名单保护的 AutoCAD 原生命令兜底 |

插件还提供 `CADMCPDIFFERENTIAL` 和 `CADMCPDIFFERENTIAL3D` 示例生成命令，分别生成差速器二维装配剖视图及可编辑的三维组件装配模型。

精确修改以 AutoCAD `Handle` 为稳定标识。建议先用 `cad_inspect` 查询，再把返回的 handle 交给 `cad_edit`。每个 AutoCAD 进程使用独立的 PID 管道；存在多个实例时必须先调用 `cad_instances` 和 `cad_connect`。写请求被串行执行，数据库修改使用文档锁和事务。保存和大型导出的默认超时为 120 秒，并以 AutoCAD 命令结束事件确认结果。

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

重启 AutoCAD 2024，命令行执行 `CADMCPSTATUS`。显示运行后，重启 Codex 使 MCP 配置生效。也可以把 [mcp.example.json](./mcp.example.json) 的内容合并到支持 JSON MCP 配置的客户端。

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

## 安全边界

- 仅监听本机命名管道，不开放 TCP 端口。
- 文件路径必须为绝对本机路径。
- 结构化修改使用事务；失败自动回滚。
- 保存通过异步原生 `QSAVE`/`SAVEAS` 执行，禁止从 Idle 回调直接调用 `AcDbDatabase.saveAs`。
- `saveCopy` 和 DWG 导出使用异步原生 `-WBLOCK`，不改变活动图纸的文件名。
- 插件 DLL 使用版本化文件名部署，不覆盖 AutoCAD 已加载的程序集。
- 正式 DLL 仅通过 `%APPDATA%\Autodesk\ApplicationPlugins` Bundle 自动加载；不要手动 `NETLOAD` 仓库 `plugin` 目录中的开发 DLL。
- 所有公开 AutoCAD 命令都有异常边界，API 错误写入命令行或 MCP 结构化响应，不弹出未处理异常框。
- `cad_command` 只允许运行插件中的固定白名单，避免任意命令和脚本执行。
- 默认不自动保存。只有明确调用 `cad_document save/saveAs` 或 `cad_export` 才写文件。
- 批量工具设有数量上限，实体读取支持分页。

## 已知边界

- PDF、DWF、SVG 和位图使用 AutoCAD 原生命令队列，返回 `queued=true` 代表已提交给 AutoCAD，不代表文件已经落盘；可在后续检查目标文件。
- 当前结构化几何覆盖常用二维制图。复杂三维实体、曲面、参数化约束、动态块和行业版对象应通过后续专用工具扩展，不能把本版本描述成全部 AutoCAD API 的完整封装。
- AutoCAD 必须处于运行状态。MCP 服务不会绕过 AutoCAD 授权，也不会在服务器端解析专有 DWG。

## 开发

```powershell
npm run dev
npm run check
```

协议是一条 UTF-8 JSON 请求和一条 JSON 响应，每个管道连接处理一次请求。插件的 `Application.Idle` 回调把后台请求切换到 AutoCAD 主线程，避免跨线程访问数据库。

卸载插件：

```powershell
.\scripts\uninstall-plugin.ps1
```
