import os, re, glob, csv, struct, hashlib

DWB_DIR = r"C:\Tangent\TElecT20V9\dwb"
OUT = r"C:\Users\wenzh\tools\tangent-lib-extract"
ENC = "gbk"

def names_for_library(lib_base):
    """库内 .TK 索引 -> {编号: 名称}（TK 与 DWB 同名，另含 Element/<->element 等大小写差异）"""
    cands = [p for p in glob.glob(os.path.join(DWB_DIR, "*.T*"))
             if os.path.splitext(os.path.basename(p))[0].lower() == lib_base.lower()
             and os.path.splitext(p)[1].lower() in (".tk",)]
    m = {}
    for tk in cands:
        text = open(tk, "rb").read().decode(ENC, "replace")
        for mm in re.finditer(r"([^\s\u00b7]{1,60}?)[\s\u00b7]{2,}(\d{8})", text):
            nm, code = mm.group(1).strip(" \u00b7"), mm.group(2)
            if nm and not nm.isdigit():
                m.setdefault(code, [])
                if nm not in m[code]:
                    m[code].append(nm)
    return m, [os.path.basename(c) for c in cands]

rows = []
for libdir in sorted(os.path.join(OUT, d) for d in os.listdir(OUT) if os.path.isdir(os.path.join(OUT, d))):
    base = os.path.basename(libdir)
    nmap, tks = names_for_library(base)
    for f in sorted(glob.glob(os.path.join(libdir, "*.dwg"))):
        code = os.path.splitext(os.path.basename(f))[0]
        size = os.path.getsize(f)
        rows.append({"library": base, "code": code, "name": " / ".join(nmap.get(code, ["(未命名)"])),
                     "tk": ",".join(tks), "size": size})

with open(os.path.join(OUT, "manifest.csv"), "w", newline="", encoding="utf-8-sig") as f:
    w = csv.DictWriter(f, fieldnames=["library", "code", "name", "tk", "size"])
    w.writeheader(); w.writerows(rows)

with open(os.path.join(OUT, "index-by-name.txt"), "w", encoding="utf-8") as f:
    f.write("# 天正电气图库符号索引：名称<TAB>库<TAB>编号<TAB>字节\n")
    f.write("# DWG 文件位于 <本目录>/<库>/<编号>.dwg\n")
    for r in sorted(rows, key=lambda x: (x["name"], x["library"], x["code"])):
        f.write(f"{r['name']}\t{r['library']}\t{r['code']}\t{r['size']}\n")

print(f"清单重建完成: {len(rows)} 条")
print("\n=== 按名称查询 ===")
for kw in ("断路器", "接地", "隔离开关", "熔断器", "接触器", "避雷器", "插座", "灯"):
    hit = [r for r in rows if kw in r["name"]][:3]
    if hit:
        print(f"  【{kw}】")
        for r in hit:
            print(f"     {r['name']:<24} {r['library']}/{r['code']}.dwg  {r['size']} 字节")
print("\n=== 各库命名覆盖率 ===")
for d in sorted(os.listdir(OUT)):
    p = os.path.join(OUT, d)
    if not os.path.isdir(p): continue
    sub = [r for r in rows if r["library"] == d]
    named = sum(1 for r in sub if r["name"] != "(未命名)")
    print(f"  {d:<12} {named:>5}/{len(sub):<5}")
