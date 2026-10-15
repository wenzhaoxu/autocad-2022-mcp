# 天正电气（Tangent TElec / T20）集成说明

在 AutoCAD 2022 + 天正电气 T20 V9 环境下，用本 MCP 自动化复用天正电气符号库的**可行路径**与**边界**。

结论先行：

> 天正自身的命令**无法被程序化驱动完成**（一律在命令上下文中内部回滚），但天正插入的元件本质上就是普通块参照
> （`$element$<编号>` / `$equip$<编号>`）。因此可行路径是
> **先让块定义进过一次图（种子），之后用 `cad_block import` + `insert` 全自动复用**。

## 一、可直接使用的自动化流程（已验证）

```jsonc
// 1) 从「种子图库」把天正符号块定义导入当前图纸（只需该图库存在同名块）
{ "action": "import", "name": "$element$00000576",
  "sourcePath": "C:\\Users\\wenzh\\tools\\tangent-symbols\\tangent-electrical-symbols.dwg" }

// 2) 插入（任意位置 / 数量 / 旋转 / 比例）
{ "action": "insert", "name": "$element$00000576",
  "insertionPoint": [10000, 5000, 0], "rotation": 1.5707963, "scale": [2, 2, 1] }

// 3) 修正图层到天正约定的 ELEMENT（cad_block 的 insert 不接收图层参数）
//    cad_edit { target: { handles: ["308"] }, operations: [{ op: "setProperties", layer: "ELEMENT" }] }
```

实测记录（全新图纸、无任何天正 UI 操作）：

| 步骤 | 结果 |
|---|---|
| `cad_block import` `$element$00000576`（断路器） | 块表新增 `$element$00000576` |
| 3 次 `cad_block insert` | handle `308`/`309`/`30A`，含旋转 90° 与放大 2 倍 |
| `cad_edit setProperties layer=ELEMENT` | 3 个均落到 `ELEMENT` |

### 种子图库

`C:\Users\wenzh\tools\tangent-symbols\tangent-electrical-symbols.dwg`

制作方式（一次性）：用天正界面插入所需符号 → 另存为该文件。之后任何图纸都能通过
`cad_block import` 复用，**不再需要天正**。

## 二、为什么不能直接驱动天正命令

| 尝试 | 结果 | 原因 |
|---|---|---|
| MCP `cad_command "YJCR"`（`waitForCompletion=false`） | `command.started YJCR` → 内部 `UNDO` → `command.completed`，无插入 | 见下 |
| COM `ActiveDocument.SendCommand("YJCR ")` / `("_.YJCR ")` | 同上 | 与 `_.` 前缀、调用方无关 |
| `rybz`（任意布置） | 启动后自行结束，无插入 | 等不到拾取输入 |
| `yltk`（原理图库） | 被派发为 `EXECUTEFUNCTION` 后结束 | 天正 UI 走 `EXECUTEFUNCTION` 派发，裸命令名无效 |

`tchdata.dbf` 中 `YJCR` 的记录：

```
TYPE=系统元件  CHACMD=元件插入  ENGCMD=yjcr  ICON=TEL_16_INSELEMENT
EXPLAIN=在系统中将元件图块插入导线中: YJCR
```

即 `YJCR` 的设计前提是**已存在导线**，且符号需交互选取。程序化调用缺少交互输入 →
天正在命令内部回滚（表现为 `UNDO`），并在 AutoCAD 吞掉异常后返回 `result: null`。

天正也没有暴露参数化插入接口：全部 375 条命令中仅两条 LISP 入口，且都只是打开图库浏览器：

```
(ITgDwgLib "03d201")        # 国标图集
(ITgDwgLib "FireProSys")    # 消防图库
```

## 三、天正符号库的事实记录

| 项目 | 内容 |
|---|---|
| 库文件 | `<天正>\dwb\*.dwb`（几何）+ `*.slb`（幻灯片）+ `*.TK`（索引） |
| 容器格式 | `ArchStar DWG Library 1.0`，自有封装；内嵌 DWG **已压缩无 `AC10` 签名**，无法直接提取 |
| 索引格式 | `<分类>\<名称>` + 8 位十进制编号，例如 `断路器 00000840` |
| 插入后的块名 | Element 库 → `$element$<编号>`；Equip 库 → `$equip$<编号>` |
| 天正约定图层 | 元件 → `ELEMENT`；设备 → 通常为 `EQUIP` 类图层 |
| 常用编号 | `00000840/00000845/00000576` 断路器 · `00000595` 带漏电流保护的断路器 · `00000531/00000499` 熔断器 · `00000529/00000530` 熔断器式隔离开关 · `00000277` 接地 · `00000116` 等设备见 `Equip.tk` |

查询编号的方法：在 `<天正>\dwb\*.TK` 中按 GBK 编码检索符号名，取紧随其后的 8 位编号。

## 四、探测天正环境（本次使用过的定位手段）

| 目标 | 位置 |
|---|---|
| 命令名 ↔ 中文名 ↔ 说明 ↔ 图标 | `<天正>\sys\tchdata.dbf`（DBF；字段 TYPE/CHACMD/ENGCMD/ICON/EXPLAIN/FLAG，375 条） |
| 命令清单（简表） | `<天正>\sys\tchcmd.txt`（描述 + 命令名，214 行，GBK） |
| 同名命令改名表 | `<天正>\sys\Tel_RenameCommand.txt` |
| 专业词库（非符号库） | `<天正>\sys\dict.xml` |
| 启动脚本 / 加载 ARX | `<天正>\sys22x64\acad.rx`、`Tel.tmn` / `Tch.tmn`（仅右键菜单定义） |

> 注意 `tchcmd.txt` / `tchdata.dbf` / `dict.xml` / `.TK` 均为 **GBK**，用 PowerShell 读取时需
> `[System.Text.Encoding]::GetEncoding(936)`，否则中文变问号。

## 五、本仓库为此所做的修正

1. `cad_command` 白名单可由**运行时配置文件**扩充：插件程序集同目录的 `allowed-commands.txt`
   （或环境变量 `AUTOCAD_MCP_EXTRA_COMMANDS`）。修改后**无需重编译**，但需重启 AutoCAD。
   两条校验路径（`Command()` 与 `QueueInteractiveOperationAsync()`）已统一。
2. `saveCopy` / `cad_export format=dwg` / `format=dxf` / `format=stl`：命令执行期间强制
   `FILEDIA=0` 并在结束后恢复。**天正接管了 `-WBLOCK`/`DXFOUT` 等原生命令**，`FILEDIA=1` 时会弹出
   「创建图形文件」模态对话框，阻塞命令队列并让桥接 119 秒超时。
3. `cad_block import`：源 DWG 中存在同名块定义时，只克隆该块（含依赖对象）而不是把整个文件当成一个块；
   顺带消除了同名时 `Database.Insert` 抛出的 `eSelfReference`。

## 六、仍未解决

* **无法在全新图纸中凭空生成天正符号**：`element.dwb` 容器未逆向，符号几何必须经由天正本体
  至少入库一次（种子流程）。
* **天正命令的交互提示无法喂入**：本仓库的 `cad_command` 始终带 `_.` 前缀、且只接受命令名，
  不能下发裸命令行文本。若将来需要驱动 `rybz` 这类"等待拾取点"的命令，需要新增一个
  `cad_command_raw`（下发原始文本）工具——但遇到模态对话框仍然无效。
* **天正元件的附加语义**：`cad_block insert` 完成的是几何与图层；天正的元件标号、统计等
  依赖其自身 XDATA 的功能不保证生效。需要这些语义时仍应走天正界面插入。
