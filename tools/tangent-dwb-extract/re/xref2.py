import pefile, struct, re
from capstone import *

PATH = r"C:\Tangent\TElecT20V9\sys22x64\tch_kernal.arx"
pe = pefile.PE(PATH, fast_load=True)
pe.parse_data_directories()
data = open(PATH, "rb").read()
base = pe.OPTIONAL_HEADER.ImageBase
secs = [(s.Name.rstrip(b"\0").decode(), s.VirtualAddress, s.PointerToRawData, s.SizeOfRawData, s.Characteristics) for s in pe.sections]
def raw2va(off):
    for n, rva, praw, size, ch in secs:
        if praw <= off < praw + size:
            return base + rva + (off - praw)
    return None
def va2raw(va):
    for n, rva, praw, size, ch in secs:
        if base + rva <= va < base + rva + size:
            return praw + (va - base - rva)
    return None

targets = {}
for needle in (b"AS LZW 1.0", b"AS LZW", b"LZW 1.0", b"DWB_TITLE", b"ArchStar"):
    for m in re.finditer(re.escape(needle), data):
        va = raw2va(m.start())
        if va:
            targets[va] = needle.decode()

tname, trva, tpraw, tsize, tch = next(s for s in secs if s[0] == ".text")
tb = data[tpraw:tpraw+tsize]; tva = base + trva

modrms = {0x05, 0x0D, 0x15, 0x1D, 0x25, 0x2D, 0x35, 0x3D}
rexes = {0x48, 0x49, 0x4C, 0x4D}
hits = []
n = len(tb)
for i in range(1, n - 6):
    # lea: [REX] 8D modrm(mod=00,rm=101) disp32
    if tb[i] == 0x8D and tb[i+1] in modrms:
        start = i - 1 if tb[i-1] in rexes else i
        size = (tva + i + 6) - (tva + start)   # 指令长度 = (i+1+1+4) - start
        length = (i + 6) - start
        disp = struct.unpack_from("<i", tb, i+2)[0]
        insn_va = tva + start
        tgt = insn_va + length + disp
        if tgt in targets:
            hits.append((insn_va, tgt, length))
    # mov reg, [rip+disp] (8B) 也一并看看
    if tb[i] in (0x8B, 0x89, 0x3B, 0x39, 0x8D) and tb[i+1] in modrms:
        start = i - 1 if tb[i-1] in rexes else i
        length = (i + 6) - start
        disp = struct.unpack_from("<i", tb, i+2)[0]
        insn_va = tva + start
        tgt = insn_va + length + disp
        if tgt in targets and (insn_va, tgt, length) not in hits:
            hits.append((insn_va, tgt, length))

print(f"命中 {len(hits)} 处引用图库字符串：")
for insn_va, tgt, ln in hits:
    print(f"  0x{insn_va:012X} -> 0x{tgt:012X} ({targets[tgt]!r})")

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
# 对每个命中点，回溯到函数起点（向前找 int3 填充区之后）
for insn_va, tgt, ln in hits[:6]:
    ro = va2raw(insn_va)
    # 向前扫描找函数起点
    fs = insn_va
    for back in range(0, 0x600):
        probe = ro - back
        if probe < tpraw:
            break
        if tb[probe - tpraw - 1:probe - tpraw + 3] == b"\xCC\xCC\xCC\xCC":
            fs = tva + (probe - tpraw)
            break
    print(f"\n{'='*95}\n函数(估计) 0x{fs:X}  引用 {targets[tgt]!r} 于 0x{insn_va:X}\n")
    ro2 = va2raw(fs)
    cnt = 0
    for ins in md.disasm(data[ro2:ro2+0x400], fs):
        mark = "  <<< 引用魔数" if ins.address == insn_va else ""
        if ins.mnemonic in ("call", "jmp") and "rip" in ins.op_str:
            m = re.search(r"\[rip \+ (0x[0-9a-f]+)\]", ins.op_str)
            if m:
                slot = ins.address + ins.size + int(m.group(1), 16)
                val = struct.unpack_from("<Q", data, va2raw(slot))[0] if va2raw(slot) else None
                mark += f"   ; slot=0x{slot:X} val=0x{val:X}"
        print(f"  0x{ins.address:012X}  {ins.mnemonic:<7} {ins.op_str}{mark}")
        cnt += 1
        if cnt > 70:
            print("   ... 截断")
            break
pe.close()
