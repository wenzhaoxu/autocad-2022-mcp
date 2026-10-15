import struct, re, os, glob, hashlib, csv, time, sys

DWB_DIR = r"C:\Tangent\TElecT20V9\dwb"
OUT = r"C:\Users\wenzh\tools\tangent-lib-extract"
ENC = "gbk"

class BR:
    __slots__ = ("b","i","a","n")
    def __init__(s,b): s.b=b; s.i=0; s.a=0; s.n=0
    def get(s,w):
        while s.n < w:
            if s.i >= len(s.b): return None
            s.a |= s.b[s.i] << s.n; s.n += 8; s.i += 1
        c = s.a & ((1<<w)-1); s.a >>= w; s.n -= w; return c

def decode(feed, declared, maxcodes=4000, maxwidth=12):
    """AS LZW 1.0 (天正 ArchStar)：
       LSB 优先，9 位起；0..255 字面量；256 = 字典重置；257 = 字面量 0x01（非 EOI）；
       首新码 258；字典满 4000 停止增长；宽度在 next_code 达 1<<width 时 +1（上限 12）；
       解码按「输出达到声明大小」终止。"""
    br = BR(feed)
    pre = [0]*4200; suf = [0]*4200
    for i in range(256): pre[i] = -1; suf[i] = i
    nc, w, prev, fop = 258, 9, -1, 0
    out = bytearray()
    while len(out) < declared:
        code = br.get(w)
        if code is None: break
        if code == 256:
            nc, w, prev = 258, 9, -1
            continue
        stk = []; c = code
        if c >= nc:
            if prev < 0: break
            stk.append(fop); c = prev
        g = 0
        while c >= 258:
            stk.append(suf[c]); c = pre[c]; g += 1
            if g > 4200: break
        stk.append(c & 0xFF)
        stk.reverse(); out.extend(stk)
        if prev >= 0 and nc < maxcodes:
            pre[nc] = prev; suf[nc] = stk[0]; nc += 1
            if nc >= (1 << w) and w < maxwidth: w += 1
        prev = code; fop = stk[0]
    return bytes(out)

def parse_index(data):
    if not data.startswith(b"ArchStar DWG  Library 1.0"): return None
    r = []; o = 0x20
    while o + 36 <= len(data) and re.fullmatch(rb"\d{8}", data[o:o+8]):
        r.append((data[o:o+8].decode(), struct.unpack_from("<I", data, o+32)[0])); o += 36
    return r

def load_names():
    names = {}
    for tk in glob.glob(os.path.join(DWB_DIR, "*.TK")) + glob.glob(os.path.join(DWB_DIR, "*.tk")):
        lib = os.path.splitext(os.path.basename(tk))[0]
        text = open(tk, "rb").read().decode(ENC, "replace")
        for m in re.finditer(r"([^\s\u00b7]{1,40}?)[\s\u00b7]{2,}(\d{8})", text):
            nm, code = m.group(1).strip(" \u00b7"), m.group(2)
            if nm and not nm.isdigit():
                names.setdefault(code, [])
                k = f"{lib}:{nm}"
                if k not in names[code]: names[code].append(k)
    return names

def main():
    names = load_names()
    os.makedirs(OUT, exist_ok=True)
    manifest, summary = [], []
    libs = sys.argv[1:] or [os.path.basename(p) for p in
                            sorted(glob.glob(os.path.join(DWB_DIR, "*.DWB")) + glob.glob(os.path.join(DWB_DIR, "*.dwb")))]
    for lib in libs:
        p = os.path.join(DWB_DIR, lib)
        if not os.path.exists(p): continue
        data = open(p, "rb").read()
        entries = parse_index(data)
        base = os.path.splitext(lib)[0]
        if entries is None:
            print(f"[跳过] {lib} 非 ArchStar 容器", flush=True); continue
        dest = os.path.join(OUT, base); os.makedirs(dest, exist_ok=True)
        ok = fail = 0; t0 = time.time()
        for i, (code, off) in enumerate(entries):
            nxt = entries[i+1][1] if i+1 < len(entries) else len(data)
            if not (0 < off < len(data)): fail += 1; continue
            blk = data[off:nxt]
            if len(blk) < 19 or blk[:11] != b"AS LZW 1.0 ": fail += 1; continue
            declared = struct.unpack_from("<I", blk, 11)[0]
            feed = data[off+40: min(len(data), off+40+max(16384, (nxt-off)*6))]
            out = decode(feed, declared)
            if len(out) < declared or not re.match(rb"AC10\d\d", out[:6]):
                fail += 1; continue
            out = out[:declared]
            with open(os.path.join(dest, f"{code}.dwg"), "wb") as f: f.write(out)
            ok += 1
            manifest.append({"library": base, "code": code, "name": " / ".join(names.get(code, [])),
                             "size": len(out), "dwg": out[4:6].decode("ascii","replace"),
                             "sha1": hashlib.sha1(out).hexdigest()[:12]})
        summary.append((base, len(entries), ok, fail))
        print(f"[完成] {base}: 条目 {len(entries):<6} 成功 {ok:<6} 失败 {fail:<5} {time.time()-t0:.1f}s", flush=True)
    with open(os.path.join(OUT, "manifest.csv"), "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=["library","code","name","size","dwg","sha1"])
        w.writeheader(); w.writerows(manifest)
    print(f"\n总计提取 {len(manifest)} 个符号 DWG", flush=True)
    with open(os.path.join(OUT, "index-by-name.txt"), "w", encoding="utf-8") as f:
        for r in manifest:
            if r["name"]:
                f.write(f"{r['name']}\t{r['library']}\t{r['code']}\t{r['size']}\n")

if __name__ == "__main__":
    main()
