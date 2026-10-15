import binascii, struct, math, re
from collections import Counter

path = r"C:\Tangent\TElecT20V9\dwb\element.dwb"
data = open(path, "rb").read()
print("文件大小:", len(data))

# ---- 解析索引：从 0x20 开始，步长 36 ----
start = 0x20
step = 36
entries = []
off = start
while True:
    if off + step > len(data):
        break
    code = data[off:off+8]
    if not re.fullmatch(rb"\d{8}", code):
        break
    val = struct.unpack_from("<I", data, off + 32)[0]
    # 其余 24 字节是否有非零内容？
    mid = data[off+8:off+32]
    entries.append((code.decode(), val, mid))
    off += step

print("索引条目数:", len(entries), " 索引结束偏移:", off, hex(off))
print("前 12 条:")
for c, v, m in entries[:12]:
    print(f"   {c}  val={v:<8}  mid_nonzero={any(m)}")
print("后 5 条:")
for c, v, m in entries[-5:]:
    print(f"   {c}  val={v:<8}  mid_nonzero={any(m)}")

vals = [v for _, v, _ in entries]
increasing = sum(1 for i in range(len(vals)-1) if vals[i+1] >= vals[i])
print(f"\n值单调不减的相邻对: {increasing}/{len(vals)-1}")
nz = sum(1 for _, _, m in entries if any(m))
print(f"mid 24 字节含非零的条目: {nz}/{len(entries)}")

# ---- 把 val 当偏移：看块大小分布 ----
if increasing > len(vals) - 5:
    blocks = []
    for i in range(len(vals)-1):
        blocks.append(vals[i+1]-vals[i])
    blocks.append(len(data)-vals[-1])
    print("\n块大小: min=%d max=%d 前12=%s" % (min(blocks), max(blocks), blocks[:12]))
    print("末块大小:", blocks[-1], " 文件末尾校验:", vals[-1]+blocks[-1], "==", len(data))

# ---- 第一个数据块的内容与熵 ----
def entropy(buf):
    if not buf: return 0
    c = Counter(buf); n = len(buf)
    return -sum((v/n) * math.log2(v/n) for v in c.values())

v0 = vals[0]
print(f"\n--- 第一个数据块 @ {v0} (0x{v0:X}) ---")
blk = data[v0:v0+128]
for i in range(0, 128, 16):
    chunk = blk[i:i+16]
    print(f"{v0+i:08X}  " + " ".join(f"{b:02X}" for b in chunk) + "  " + "".join(chr(b) if 32 <= b < 127 else "." for b in chunk))
print("首 4 字节:", binascii.hexlify(blk[:4]).decode(), " 是否 'AC10':", blk[:4].startswith(b"AC1"))

big = data[v0:v0+4096]
print("该块前 4KB 熵:", round(entropy(big), 3), "（8 为完全随机/压缩，<6 多为结构化数据）")
print("索引区前 4KB 熵:", round(entropy(data[0x20:0x20+4096]), 3))

# ---- 在整个数据区找结构线索 ----
print("\n--- 数据区特征扫描 ---")
region = data[off:]
print("数据区大小:", len(region), " 熵:", round(entropy(region[:200000]), 3))
for sig, name in ((b"AC10","DWG签名"), (b"\x78\x9c","zlib"), (b"\x1f\x8b","gzip"), (b"Arch","Arch"), (b"DWG","DWG字面")):
    print(f"   {name}({sig!r}) 出现次数:", region.count(sig))
