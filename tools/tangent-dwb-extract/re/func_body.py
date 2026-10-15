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
exports = {base + e.address: e.name.decode("ascii", "replace") for e in pe.DIRECTORY_ENTRY_EXPORT.symbols if e.name}
iat = {}
for imp in pe.DIRECTORY_ENTRY_IMPORT:
    for e in imp.imports:
        if e.name:
            iat[e.address] = (imp.dll.decode(), e.name.decode("ascii", "replace"))

md = Cs(CS_ARCH_X86, CS_MODE_64)
START = 0x1C4FAAB0
ro = va2raw(START)
callees = []
print(f"=== 反汇编 0x{START:X}（AS LZW 块读取/解码入口）===")
n = 0
for ins in md.disasm(data[ro:ro+0x700], START):
    note = ""
    if ins.mnemonic in ("call", "jmp"):
        if ins.op_str.startswith("0x"):
            t = int(ins.op_str, 16)
            note = exports.get(t, "")
            if va2raw(t):
                callees.append(t)
        else:
            m = re.search(r"\[rip \+ (0x[0-9a-f]+)\]", ins.op_str)
            if m:
                slot = ins.address + ins.size + int(m.group(1), 16)
                note = "[IAT] " + str(iat.get(slot))
    print(f"  0x{ins.address:012X}  {ins.mnemonic:<7} {ins.op_str}{('   ; ' + note) if note else ''}")
    n += 1
    if n > 200:
        print("   ... 截断")
        break
print("\n=== 直接调用的内部函数 ===")
for c in dict.fromkeys(callees):
    print(f"  0x{c:012X}  {exports.get(c, '(internal)')}")
pe.close()
