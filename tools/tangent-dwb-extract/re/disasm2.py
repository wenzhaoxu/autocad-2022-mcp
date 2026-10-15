import pefile, struct, re
from capstone import *

PATH = r"C:\Tangent\TElecT20V9\sys22x64\tch_kernal.arx"
pe = pefile.PE(PATH, fast_load=True)
pe.parse_data_directories()
data = open(PATH, "rb").read()
base = pe.OPTIONAL_HEADER.ImageBase
secs = [(s.Name.rstrip(b"\0").decode(), s.VirtualAddress, s.PointerToRawData, s.SizeOfRawData) for s in pe.sections]

def va2raw(va):
    for n, rva, praw, size in secs:
        if base + rva <= va < base + rva + size:
            return praw + (va - base - rva)
    return None

def read_u64(va):
    ro = va2raw(va)
    return struct.unpack_from("<Q", data, ro)[0] if ro is not None else None

exports = {base + e.address: e.name.decode("ascii", "replace") for e in pe.DIRECTORY_ENTRY_EXPORT.symbols if e.name}

# IAT: 槽位 VA -> (dll, 名字)
iat = {}
print("=== tch_kernal.arx 的导入模块 ===")
for imp in pe.DIRECTORY_ENTRY_IMPORT:
    dll = imp.dll.decode()
    print("  ", dll)
    for e in imp.imports:
        if e.name:
            iat[e.address] = (dll, e.name.decode("ascii", "replace"))

md = Cs(CS_ARCH_X86, CS_MODE_64)

def resolve_indirect(insn):
    """解析 call/jmp qword ptr [rip+disp] -> 槽位 VA -> 真实目标"""
    m = re.search(r"\[rip \+ (0x[0-9a-f]+)\]", insn.op_str)
    if not m:
        return None
    disp = int(m.group(1), 16)
    slot = insn.address + insn.size + disp
    val = read_u64(slot)
    return slot, val, iat.get(slot)

def show_func(va, limit=60, label=""):
    ro = va2raw(va)
    if ro is None:
        print(f"   (0x{va:X} 无法映射)")
        return []
    print(f"\n----- 0x{va:012X} {label} -----")
    calls = []
    n = 0
    for ins in md.disasm(data[ro:ro+0x600], va):
        extra = ""
        if ins.mnemonic in ("call", "jmp") and "rip" in ins.op_str:
            r = resolve_indirect(ins)
            if r:
                slot, val, imp = r
                extra = f"   ; -> slot 0x{slot:X} value 0x{val:X} " + (f"[{imp[0]}!{imp[1]}]" if imp else ("= local" if val and va2raw(val) else ""))
                if val and va2raw(val):
                    calls.append(val)
        if ins.mnemonic == "call" and ins.op_str.startswith("0x"):
            t = int(ins.op_str, 16)
            extra = f"   ; {exports.get(t,'')}" if t in exports else ""
            calls.append(t)
        print(f"  0x{ins.address:012X}  {ins.mnemonic:<7} {ins.op_str}{extra}")
        n += 1
        if n >= limit:
            print("   ... 截断")
            break
    return calls

print("\n" + "=" * 100)
c1 = show_func(0x1C4FB240, 95, "?Append@TGGiCompressStorage (压缩写入)")
print("  => CALL 目标:", ", ".join(f"0x{c:X}" for c in dict.fromkeys(c1)))
c2 = show_func(0x1C4FB480, 95, "?Copy@TGGiCompressStorage (解压导出)")
print("  => CALL 目标:", ", ".join(f"0x{c:X}" for c in dict.fromkeys(c2)))
pe.close()
