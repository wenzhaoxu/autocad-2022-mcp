import struct, re, os

data = open(r"C:\Tangent\TElecT20V9\dwb\element.dwb", "rb").read()
entries = []
o = 0x20
while o + 36 <= len(data) and data[o:o+8].isdigit():
    entries.append((data[o:o+8].decode(), struct.unpack_from("<I", data, o+32)[0]))
    o += 36
entries.append(("END", len(data)))

def decode(payload, declared, reset_at_width, clear256, maxcodes=4000):
    v = int.from_bytes(payload, "little")
    tb = len(payload) * 8
    prefix = [0]*(maxcodes+8); suffix = [0]*(maxcodes+8)
    for i in range(256): prefix[i] = -1; suffix[i] = i
    next_code, width, pos, prev, fop = 258, 9, 0, -1, 0
    out = bytearray()
    def reset():
        nonlocal next_code, width, prev
        next_code, width, prev = 258, 9, -1
    while pos + width <= tb and len(out) < declared:
        code = (v >> pos) & ((1 << width) - 1)
        pos += width
        if clear256 and code == 256:
            reset(); continue
        if code == 257:
            break
        if reset_at_width and code == (1 << width):
            reset(); continue
        stack = []
        c = code
        if c >= next_code:
            if prev < 0: break
            stack.append(fop); c = prev
        steps = 0
        while c >= 258:
            stack.append(suffix[c]); c = prefix[c]; steps += 1
            if steps > maxcodes+8: break
        stack.append(c & 0xFF)
        stack.reverse()
        out.extend(stack)
        if prev >= 0 and next_code < maxcodes:
            prefix[next_code] = prev; suffix[next_code] = stack[0]
            next_code += 1
            if next_code >= (1 << width) and width < 12: width += 1
        prev = code; fop = stack[0]
    return bytes(out)

variants = [("仅 clear256", True, False), ("仅 1<<width 重置", False, True), ("两者都", True, True), ("都不", False, False)]
for name, c256, raw in variants:
    ok = bad = 0
    fails = []
    for i in range(len(entries)-1):
        code, off = entries[i]
        nxt = entries[i+1][1]
        if not (0 < off < len(data)): bad += 1; continue
        blk = data[off:nxt]
        if blk[:11] != b"AS LZW 1.0 " or len(blk) < 19: bad += 1; continue
        declared = struct.unpack_from("<I", blk, 11)[0]
        feed = data[off+40: min(len(data), off+40+max(16384, (nxt-off)*6))]
        try:
            out = decode(feed, declared, raw, c256)
        except Exception:
            bad += 1; continue
        if len(out) == declared and re.match(rb"AC10\d\d", out[:6]):
            ok += 1
        else:
            bad += 1
            fails.append((code, nxt-off, declared, len(out)))
    print(f"{name:<20} 成功 {ok:>4} / {len(entries)-1}   失败 {bad}")
    print("    失败样本(编号, 压缩块, 声明, 实际):", fails[:5])
