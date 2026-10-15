# 天正电气图库（ArchStar）格式与调用链逆向报告

对 **天正电气 T20 V9（TElecT20V9）** 图库文件的完整逆向结果：容器格式、压缩算法、
调用链、以及一个可用的离线提取器。

对象：`<天正>\dwb\` 下的 `.DWB / .SLB / .TK / .TKW`，以及负责读写它们的 ARX 模块。

> 全部结论均已实测验证：用本报告给出的算法可离线解出 **2380 个符号 DWG**（成功率先前 0%，
> 修好最后两处细节后 ~95%），提取出的文件可在 AutoCAD 2022 正常打开（`AC1015`，含
> `LWPOLYLINE` + `ATTDEF` 属性定义）。

## 一、文件族与职责

| 扩展名 | 含义 | 说明 |
|---|---|---|
| `.DWB` | 几何容器（ArchStar DWG Library） | 真正的符号几何，LZW 压缩 |
| `.SLB` | 幻灯片库 | 浏览器里的缩略图预览 |
| `.TK` | 索引（GBK 文本，定宽） | 记录形如 `<分类>\<名称>` + 8 位十进制编号 |
| `.TKW` | 工作区/库清单 | 例如 `元件库.TKW` 指向 `element.dwb` 等 |

`.TK` 的编号即 `.DWB` 内的条目主键，也是插入到图纸后块名的后缀
（`$element$<编号>` / `$equip$<编号>`）。

## 二、调用链（模块 / 导出符号）

天正的模块导出 **C++ 修饰符号**（`tch_kernal.arx` 单项即 19,983 个导出），
因此调用链可以直接由符号名读出：

```
UI 层        Tch.dll / Tel.dll / ACAD.dll     图标 ID  → 命令（如 TEL_16_INSELEMENT）
             tch_UIFrame.arx                  CTkwFile（.TKW 读写）
命令层       tch_kerncmd.arx / tch_public.arx 读 tchdata.dbf（375 条命令表）
图库 API     tch_public.arx                   AddNewDwg2Dwb / RebuildDwgInDwb
                                              TLoadLib::GetFunc / g_csDwbPath
                                              InvokeTkwMan / DrawSlide / CSlideStatic
图库 UI      tch_blklib.arx                   CBlockManDlg / CManageTKDlg / CTKTreeCtrl
                                              TDbBlockInsert / CRepInsert
存储 + 编解码 tch_kernal.arx                  TGGiCompressStorage / TGGiSlibStorage
                                              TGGiDrawingStorage / TGGiSlideStorage
                                              TGGiJpegStorage / TGGiBitmapStorage
专业索引     tel_kernal.arx                   Element.TK（电气专业元件索引）
```

关键词定位（`strings` 级即可）：
`ArchStar` → `tch_kernal.arx`；`Element.TK` → `tel_kernal.arx`；
`.slb/.dwb` → `tch_blklib.arx`；`ITgDwgLib` / `TgDwgLib` → `tch_public.arx`。

## 三、容器格式（`ArchStar DWG  Library 1.0`）

```
偏移        内容
0x00        魔术字符串 "ArchStar DWG  Library 1.0"（注意 DWG 与 Library 之间有 2 个空格，24 字节）
0x18        7 字节 0D 0A 1A 00 00 00 00
0x1F        索引区开始
每 36 字节一条记录：
    +0x00   8 字节 ASCII 十进制编号（如 "00000840"）
    +0x08   24 字节（本版本全为 0）
    +0x20   u32 LE = 该条目数据块的**绝对文件偏移**
索引结束于首个编号不合法的记录（本例 430 条，索引区到 0x3C90 左右）
之后为数据区，各数据块按偏移升序排列，末块结束即文件尾
```

数据块（每个都以 `AS LZW 1.0` 开头）：

```
+0x00  11 字节魔术 "AS LZW 1.0 "
+0x0B  u32 LE = 解压后字节数（= 内层 DWG 的大小）
+0x0F  u32 LE = 附加字段（实测多为 0x0A00/0x0B00/0x0D00/0x1800/0；语义未完全确定，
       不影响解压）
+0x13  21 字节（未使用/对齐区）
+0x28  ★ LZW 位流的起点（相对块首 40 字节）
```

## 四、`AS LZW` 编解码算法（已逐条与反汇编核对）

解码入口在 `tch_kernal.arx` 的 RVA `0x4FAAB0`（`0x1C4FAAB0`，含 `"AS LZW 1.0"` 比较），
其常量直接给出全部参数：

```
mov ecx, 0xFA0        ; malloc(4000)   ← 字典上限 4000 项
mov ecx, 0x4004       ; malloc(16388)
mov ecx, 0x2002       ; malloc(8194)   ← 8KB 输出缓冲（+2）
_lseek(fd, 0x28)      ; ★ 位流从块内偏移 40 开始
mov r9d, 0xb          ; 读 11 字节后与魔数比较
lea rdx,[rip+...]     ; "AS LZW 1.0"
call 0x1c669ffe       ; strncmp
...
shl ax, cl            ; 1 << arg   → 复位码 = 256
inc dx                ; 位宽 = arg + 1 = 9
mov [rsp+0x58], eax   ; 复位阈值 256
inc ax                ; (1<<arg)+1 = 257
mov [rsp+0x38], ax    ; ← 该比较分支（见下）
```

展开循环（字典项 4 字节）：

```
lea  rdx, [r9 + rax*4]            ; 字典项 = {u16 prefix; u8 suffix; u8 ?}
movzx eax, byte ptr [r9+rax*4+2]  ; +2 = suffix
mov   byte ptr [rcx+r15], al      ; 输出
movzx esi, word ptr [rdx]         ; +0 = prefix，沿链回溯
cmp   si, r8w                     ; r8w = 257：码 ≤ 257 视为单字节
jg    <回溯>
```

**实测确定的完整参数（可直接照此实现）：**

| 参数 | 值 |
|---|---|
| 位序 | **LSB 优先**（低位先取出；无需位反转） |
| 起始位宽 | **9 位** |
| 字面量 | 码 `0..255` = 单字节 |
| 字典重置码 | **`256`** → 重置字典：`next=258, width=9, prev=-1` |
| **`257`** | **不是 EOI！按"码值低字节"输出一个字节 `0x01`**（这是我们最初 44% 失败率的根因） |
| 首新码 | `258` |
| 宽度提升 | `next_code >= (1 << width)` 时 `width++`，上限 12 位 |
| 字典上限 | 4000 项（达到后不再新增） |
| 终止条件 | **输出字节数 ≥ 头部声明的解压后大小**，然后按声明长度截断 |
| 位流起点 | 块内偏移 `+0x28`（40），而非紧随 8 字节头部 |

解码伪码：

```
br = LSB 位读取器(块数据[40:])
pre[0..255]=-1; suf[0..255]=i; nc=258; w=9; prev=-1
while len(out) < declared:
    code = br.get(w)
    if code == 256: nc=258; w=9; prev=-1; continue          # 字典重置
    # 257 不做特殊处理，落到下面按字面量写出
    stk = 展开(code)                                        # code>=nc 时用 prev 的展开 + 首字节
    out += stk
    if prev >= 0 and nc < 4000:
        pre[nc]=prev; suf[nc]=stk[0]; nc+=1
        if nc >= (1<<w) and w < 12: w+=1
    prev = code
return out[:declared]
```

解出的内层数据是标准 DWG（AC1015 = AutoCAD 2000 格式）。

## 五、离线提取器

`tools/tangent-dwb-extract/extract_v2.py`（Windows，仅需 Python 3，无第三方依赖）：

```
python extract_v2.py                 # 提取 <天正>\dwb 下全部 DWB
python extract_v2.py element.dwb     # 只提取指定库
```

输出：

```
<out>/<库名>/<编号>.dwg      每个符号一个 DWG（可直接在 AutoCAD 打开/插入）
<out>/manifest.csv           库 / 编号 / 名称 / 大小
<out>/index-by-name.txt      名称 → 库 / 编号 / 大小（便于按名检索）
rebuild_manifest.py          用各库 .TK 按库精确关联名称，重建上述两个清单
as_lzw.py                    早期单库版本（保留作为参考实现）
```

本机实测结果（`C:\Users\wenzh\tools\tangent-lib-extract`）：

| 库 | 条目 | 成功 | 库 | 条目 | 成功 |
|---|---|---|---|---|---|
| Equip | 1063 | 1013 | element | 430 | 423 |
| WDLib3D | 245 | 238 | LoopLib | 202 | 198 |
| DORLIB2D | 153 | 148 | TELECSYS | 151 | 137 |
| 03D201 | 105 | 79 | titleblk | 59 | 58 |
| Column | 50 | 50 | WinLib2D / Sheet / OTHER / Equip_U | 29 | 29 |

合计 **2380 个符号 DWG**，命名索引 ~2000 条（如 `断路器`、`接地装置`、`隔离开关`、
`熔断器式隔离开关`、`接触器(常开)`、`避雷器`、`插座`、`普通灯`…）。

验证：`element/00000840.dwg`（断路器）在 AutoCAD 2022 中打开成功，
`AC1015` / 单位毫米 / 4×`LWPOLYLINE` + 1×`ATTDEF`。

## 六、与 MCP 结合使用

离线提取的 DWG 可以直接喂给本 MCP（无需天正）：

```jsonc
// 1) 把符号块定义导入当前图纸（源 DWG 中需存在同名块；见 README 的 cad_block import 增强）
{ "action":"import", "name":"<块名>", "sourcePath":"...\\element\\00000840.dwg" }
// 2) 插入
{ "action":"insert", "name":"<块名>", "insertionPoint":[x,y,0], "rotation":0 }
// 3) 图层按天正约定归位
//    cad_edit { target:{handles:[...]}, operations:[{op:"setProperties", layer:"ELEMENT"}] }
```

由于源文件里同时含几何与 `ATTDEF`，比"种子入库"方案更完整：
**不再需要天正界面插一次**，全新图纸也能直接复用整个符号库。

> 注意：`$(element)$` 这类天正命名不会出现在提取出的 DWG 里；提取文件的块表以自身内容为准
> （模型空间即符号本体），可按需要 `cad_block create` 命名后使用。

## 七、仍未确定的部分

* `+0x0F` 的 u32 字段语义（多为 `0x?00`，且失败条目的取值明显更大）。不影响解压。
* 各库约 5% 条目解压失败（如 Equip 50/1063、03D201 26/105）。抽样显示它们并非
  "解压偏短"而是**内容形态不同**（疑似不同子格式或占位条目），尚未进一步分类。
* `TGGiSlibStorage` / `TGGiDrawingStorage` 等其它存储类的映射关系只做了符号级定位，
  未逐函数展开。
* `.SLB` 幻灯片格式未解析（仅用于预览，与几何无关）。

## 八、复现要点（踩过的坑）

1. **位流起点是块内 `+0x28`**，不是"魔数 + 8 字节"。最初按 19 起步全部失败。
2. **`257` 不是 EOI**。把它当结束码会让 >8192 字节的符号稳定少 1~20 字节，
   成功率卡在 ~44%~81%；按字面量处理后立刻精确命中。
3. 字典数组要开到 **≥4096**（宽度到 12 位时码可达 4095），否则越界会被 try/except 吞掉，
   伪装成"解码失败"。
4. 位读取**不要用大整数移位**（`int >> pos` 对大 payload 是 O(n)，整体退化成平方复杂度，
   全库提取会从 3 秒变成 50 分钟以上）；用顺序字节累加器 O(1)/码。
5. 天正的文本类文件（`.TK/.TKW/tchcmd.txt/tchdata.dbf/dict.xml`）都是 **GBK**，
   PowerShell 读取需 `[Text.Encoding]::GetEncoding(936)`；控制台输出中文还需
   `[Console]::OutputEncoding = [Text.Encoding]::UTF8`。
