import struct, re

class BR:
    __slots__ = ("b","i","a","n")
    def __init__(s,b): s.b=b; s.i=0; s.a=0; s.n=0
    def get(s,w):
        while s.n < w:
            if s.i >= len(s.b): return None
            s.a |= s.b[s.i] << s.n; s.n += 8; s.i += 1
        c = s.a & ((1<<w)-1); s.a >>= w; s.n -= w; return c

def decode_debug(feed, declared, eoi_is_literal=False, clear_is_literal=False):
    br = BR(feed)
    pre=[0]*4200; suf=[0]*4200
    for i in range(256): pre[i]=-1; suf[i]=i
    nc,w,prev,fop = 258,9,-1,0
    out=bytearray(); codes=0; resets=0
    stop=None
    while True:
        if len(out) >= declared: stop="reached_declared"; break
        code = br.get(w)
        if code is None: stop="bits_exhausted"; break
        codes += 1
        if code == 256 and not clear_is_literal:
            nc,w,prev = 258,9,-1; resets += 1; continue
        if code == 257 and not eoi_is_literal:
            stop="hit_EOI(257)"; break
        stk=[]; c=code
        if c >= nc:
            if prev < 0: stop="invalid_first"; break
            stk.append(fop); c=prev
        g=0
        while c >= 258:
            stk.append(suf[c]); c=pre[c]; g+=1
            if g>4200: stop="chain_loop"; break
        stk.append(c & 0xFF); stk.reverse(); out.extend(stk)
        if prev >= 0 and nc < 4000:
            pre[nc]=prev; suf[nc]=stk[0]; nc+=1
            if nc >= (1<<w) and w < 12: w += 1
        prev=code; fop=stk[0]
    return bytes(out), stop, codes, w, nc, resets

def parse(data):
    r=[];o=0x20
    while o+36<=len(data) and re.fullmatch(rb"\d{8}", data[o:o+8]):
        r.append((data[o:o+8].decode(), struct.unpack_from("<I",data,o+32)[0])); o+=36
    return r

for lib, targets in (("element.dwb", ["00000757","00000760","00000776","00000799"]),
                     ("Equip.DWB", ["00002278","00002283","00002284"])):
    data = open(rf"C:\Tangent\TElecT20V9\dwb\{lib}","rb").read()
    ents = dict(parse(data))
    order = [c for c,_ in parse(data)]
    print(f"\n===== {lib} =====")
    for t in targets:
        if t not in ents: continue
        idx = order.index(t)
        off = ents[t]
        nxt = ents[order[idx+1]] if idx+1 < len(order) else len(data)
        blk = data[off:nxt]
        declared = struct.unpack_from("<I", blk, 11)[0]
        feed = data[off+40: min(len(data), off+40+max(16384,(nxt-off)*6))]
        out, stop, codes, w, nc, resets = decode_debug(feed, declared)
        out2, stop2, codes2, *_ = decode_debug(feed, declared, eoi_is_literal=True)
        out3, stop3, *_ = decode_debug(feed, declared, eoi_is_literal=True, clear_is_literal=True)
        print(f"  {t}: 声明={declared} 压缩块={nxt-off}")
        print(f"     常规   -> {len(out):<7} 停止={stop:<18} 码数={codes} 末宽={w} 末码表={nc} 重置={resets}")
        print(f"     257当字面量 -> {len(out2):<7} 停止={stop2}")
        print(f"     256/257当字面量 -> {len(out3):<7} 停止={stop3}")
