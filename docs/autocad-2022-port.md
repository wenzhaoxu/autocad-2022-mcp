# AutoCAD 2022（R24.1）移植与部署说明

本仓库 fork 自 [cafedaily/autocad-2024-mcp](https://github.com/cafedaily/autocad-2024-mcp)，
把同一套 MCP 桥接移植到 **AutoCAD 2022（R24.1）**。

上游面向 AutoCAD 2024（R24.3）。两者的 ObjectARX .NET 程序集版本不同
（2022 为 `24.1.0.0`，2024 为 `24.3.0.0`），且部分 `DocumentCollection` /
`Document` 行为存在差异，因此需要下列修正。**功能范围没有删减。**

## 一、代码修正清单

### 1. 插件清单版本范围

`bundle/AutoCAD2024Mcp.bundle/PackageContents.xml`

```diff
-<RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R24.3" SeriesMax="R24.3" />
+<RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R24.1" SeriesMax="R24.1" />
```

AutoCAD 在启动时按 `SeriesMin/SeriesMax` 过滤 bundle。标记为 `R24.3` 的包在
2022 上会被**静默跳过**，不会报错，插件也就永远不会加载。

### 2. `cad_document new` 不能使用空模板

`plugin/AutoCAD2024Mcp/CadDispatcher.cs`

上游实现是 `docs.Add((string)p["template"] ?? "")`。在 AutoCAD 2022 上，
空模板字符串会让该调用**挂起**（实测 119 秒超时后由 MCP 侧报 `COMMAND_TIMEOUT`），
且不会创建任何文档。

修正：模板为空时按顺序解析一个真实存在的 `.dwt`：

1. 系统变量 `DEFAULT_TEMPLATE`
2. `LOCALROOTPREFIX\Template\acadiso.dwt`
3. `LOCALROOTPREFIX\Template\acad.dwt`
4. 同目录下任意 `*.dwt`

（对应 `ResolveDefaultTemplate()`。）

### 3. `cad_document close` 不能在命令上下文中执行

`plugin/AutoCAD2024Mcp/CadDispatcher.cs`

`Document.CloseAndDiscard()` / `CloseAndSave()` 不允许在命令上下文内调用。
上游把 `close` 归入 `RequiresCommandContext`，于是：

* 命令上下文回调抛出异常；
* AutoCAD **吞掉**该异常，`ExecuteInCommandContextAsync` 正常完成；
* 调用方收到 `{"ok":true,"result":null}` —— 既不报错，文档也没关闭。

修正：`RequiresCommandContext` 只保留 `new` / `open` / `activate`，
`close` 改为直接调度。

### 4. 命令上下文回调吞异常（静默"成功"）

`plugin/AutoCAD2024Mcp/CadDispatcher.cs`

```csharp
// 之前：异常被 AutoCAD 吞掉，result 保持 null，调用方看到"成功"
await AcApp.DocumentManager.ExecuteInCommandContextAsync(async ignored =>
{
    result = Dispatch(method, p);
    await Task.CompletedTask;
}, null);
return result;

// 之后：显式捕获并在上下文返回后重新抛出
```

这条修正让任何在命令上下文内抛异常的操作都能返回真实错误码，
而不是伪装成 `result: null`。

### 5. 构建默认路径

`plugin/AutoCAD2024Mcp/AutoCAD2024Mcp.csproj`

`AutoCADDir` 默认值由 `C:\Program Files\Autodesk\AutoCAD 2024` 改为 `...\AutoCAD 2022`，
因此直接 `dotnet build` 即可对 2022 程序集编译。装在非标准目录时仍可覆盖：

```powershell
dotnet build .\plugin\AutoCAD2024Mcp\AutoCAD2024Mcp.csproj -c Release -p:AutoCADDir="D:\Autodesk\AutoCAD 2022"
```

## 二、部署前提（关键）

### AutoCAD 必须与 MCP 服务进程处于同一完整性级别

**AutoCAD 不能以管理员身份运行。** 命名管道由 AutoCAD 进程创建；若 AutoCAD 处于高完整性级别
（Elevated）而 OpenCode / Node 的 MCP 服务是普通用户（中完整性），Windows 的强制完整性控制
（MIC）会拦截，表现为：

| 现象 | 原因 |
|---|---|
| `cad_instances` 恒为空 | 服务端用 `process.kill(pid, 0)` 探测存活，跨完整性级别返回 `EPERM`，被当作"进程已死" |
| `cad_status` 返回 `BRIDGE_UNAVAILABLE` | 实例列表为空 |
| 即使绕过存活检查，`\.\pipe\autocad-2024-mcp-<pid>` 连接也会 `EPERM` / `Access denied` | MIC 禁止低完整性进程写入高完整性对象 |

常见诱因是注册表里的兼容性标记：

```
HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers
  C:\Program Files\Autodesk\AutoCAD 2022\acad.exe  =>  RUNASADMIN
```

移除方式（用户级设置，无需管理员权限）：

```powershell
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers" `
  -Name "C:\Program Files\Autodesk\AutoCAD 2022\acad.exe"
```

移除后需重启 AutoCAD 使其以普通权限运行。若该标记是为授权/网络盘等问题特意设置的，
请自行权衡，必要时可恢复。

### 安全提示对话框

每次插件 DLL 内容变化（重新编译）后，AutoCAD 首次加载会弹出
「安全 - 未签名的可执行文件」对话框，需选择「始终加载」或「仅加载一次」。
该对话框是**模态**的，会阻塞 AutoCAD 的 Idle 回调，从而让插件一直不注册
（表现为没有管道、`cad_instances` 为空）。

### 至少打开一张图纸

`new` / `open` / `activate` 依赖 `ExecuteInCommandContextAsync`，该方法需要存在活动文档
的命令上下文。停在「开始」页（0 个文档）时这些操作无法工作。
先手动打开任意一张图，再使用 MCP。

## 三、安装步骤

```powershell
npm install
npm run build
dotnet build .\plugin\AutoCAD2024Mcp\AutoCAD2024Mcp.csproj -c Release
.\scripts\install-plugin.ps1
```

重启 AutoCAD 2022，命令行执行 `CADMCPSTATUS`，应显示 `0.1.1`。
随后可用 `CADMCPSELFTEST` 做自检（会临时创建并清理测试实体）。

注册到 OpenCode（本仓库的 `register-codex.ps1` 是给 Codex 用的）：

```powershell
opencode mcp add autocad-2024 --global --env AUTOCAD_MCP_TIMEOUT_MS=120000 -- node "<repo>\dist\src\index.js"
```

## 四、已在 AutoCAD 2022 R24.1 上验证的能力

| 验证项 | 结果 |
|---|---|
| `cad_status` | `release: "24.1.0.0"`、`connected: true`、`trustedDeployment: true` |
| `cad_instances` / `cad_connect` | 正常（PID 级管道） |
| `cad_inspect`（layers / entities） | 正常 |
| `cad_draw`（LINE + CIRCLE） | 正常，返回 handle |
| `cad_measure`（length / area / extents） | 正常（CIRCLE 面积 = πr²） |
| `cad_undo` | 正常，实体被撤销 |
| `cad_document new`（不传模板） | 正常（修正后） |
| `cad_document close` | 正常（修正后） |

## 五、已知限制（未修正）

* **未命名图纸无法通过 API 关闭。** `CloseAndDiscard()`（插件）与 COM
  `Document.Close(false)` 对 `DWGTITLED=0` 的图纸都会失败。
  对已命名（已保存过）的图纸关闭正常。变通：先 `cad_document saveAs` 落盘，再关闭。
* **`saveAs` / `close` 忽略 `documentId`，作用于活动文档。**
  这是上游既有行为，未在本次移植中改动；多文档场景请先用 `cad_document activate`
  切到目标文档，再执行 `saveAs` / `close`。
* `cad_command` 白名单不包含 `CADMCPSELFTEST`，自检需在 AutoCAD 命令行手动执行。
* `cad_status` 的 `product` 字段硬编码为 `"AutoCAD 2024"`（仅显示用，不影响功能）。
* 曲面、网格、参数化约束、动态块等未封装，与上游一致。
