import struct, os, re, glob, sys

REV = bytes(int(f"{b:08b}"[::-1], 2) for b in range(256))

def lzw_decode(payload, declared, maxcodes=4000, maxwidth=12):
    """AS LZW 1.0：LSB 优先、9 位起、CLEAR=256/EOI=257、首码 258、宽度无 early change"""
    v = int.from_bytes(payload, "little")   # LSB 优先，无需位反转
    total_bits = len(payload) * 8
    prefix = [0] * (maxcodes + 8)
    suffix = [0] * (maxcodes + 8)
    for i in range(256):
        prefix[i] = -1
        suffix[i] = i
    next_code = 258
    width = 9
    pos = 0
    prev = -1
    first_of_prev = 0
    out = bytearray()
    while pos + width <= total_bits and len(out) < declared:
        code = (v >> pos) & ((1 << width) - 1)
        pos += width
        if code == 256:                       # CLEAR
            next_code = 258; width = 9; prev = -1; continue
        if code == 257:                       # EOI
            break
        stack = []
        c = code
        if c >= next_code:
            if prev < 0:
                break
            stack.append(first_of_prev)
            c = prev
        steps = 0
        while c >= 258:
            stack.append(suffix[c])
            c = prefix[c]
            steps += 1
            if steps > maxcodes + 8:
                break
        stack.append(c & 0xFF)
        stack.reverse()
        out.extend(stack)
        if prev >= 0 and next_code < maxcodes:
            prefix[next_code] = prev
            suffix[next_code] = stack[0]
            next_code += 1
            if next_code >= (1 << width) and width < maxwidth:
                width += 1
        prev = code
        first_of_prev = stack[0]
    return bytes(out)

def parse_index(data, verbose=False):
    if not data.startswith(b"ArchStar DWG  Library 1.0"):
        return None
    entries = []
    off = 0x20
    while off + 36 <= len(data):
        code = data[off:off+8]
        if not re.fullmatch(rb"\d{8}", code):
            break
        val = struct.unpack_from("<I", data, off + 32)[0]
        entries.append((code.decode(), val))
        off += 36
    return entries

def extract(dwb_path, outdir, limit=None):
    data = open(dwb_path, "rb").read()
    entries = parse_index(data)
    if entries is None:
        print(f"[跳过] {os.path.basename(dwb_path)} 不是 ArchStar 容器")
        return 0, 0, 0
    os.makedirs(outdir, exist_ok=True)
    ok = bad = empty = 0
    vers = {}
    total = len(entries)
    for i, (code, off) in enumerate(entries):
        if i + 1 < total:
            nxt = entries[i+1][1]
        else:
            nxt = len(data)
        if not (0 < off < len(data)):
            bad += 1; continue
        blk = data[off:nxt]
        if len(blk) < 19 or blk[:11] != b"AS LZW 1.0 ":
            bad += 1; continue
        declared = struct.unpack_from("<I", blk, 11)[0]
        feed = data[off+40: min(len(data), off + 40 + max(16384, (nxt - off) * 6))]
        try:
            out = lzw_decode(feed, declared)
        except Exception as e:
            bad += 1; continue
        if len(out) != declared or not re.match(rb"AC10\d\d", out[:6]):
            bad += 1; continue
        vers[out[4:6].decode("ascii", "replace")] = vers.get(out[4:6].decode("ascii", "replace"), 0) + 1
        name = f"{code}.dwg"
        with open(os.path.join(outdir, name), "wb") as f:
            f.write(out)
        ok += 1
        if limit and ok >= limit:
            break
    print(f"[完成] {os.path.basename(dwb_path)}: 条目 {total}, 成功 {ok}, 失败 {bad}, DWG版本 {vers}")
    return total, ok, bad

if __name__ == "__main__":
    root = r"C:\Tangent\TElecT20V9\dwb"
    out_root = r"C:\Users\wenzh\tools\tangent-lib-extract"
    targets = sys.argv[1:] if len(sys.argv) > 1 else ["element.dwb"]
    for t in targets:
        p = os.path.join(root, t)
        if os.path.exists(p):
            extract(p, os.path.join(out_root, os.path.splitext(t)[0]))
        else:
            print("不存在:", p)
