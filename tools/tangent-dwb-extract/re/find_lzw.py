import pefile, os, re, glob

root = r"C:\Tangent\TElecT20V9"
files = glob.glob(os.path.join(root, "**", "*.arx"), recursive=True) + \
        glob.glob(os.path.join(root, "**", "*.dll"), recursive=True)

print("=== 1) 'AS LZW' / 'LZW' 字符串落在哪个模块 ===")
for f in files:
    try:
        b = open(f, "rb").read()
    except Exception:
        continue
    for needle in (b"AS LZW", b"LZW 1.0", b"LZW"):
        c = b.count(needle)
        if c:
            print(f"   {os.path.basename(f):<34} {needle.decode():<8} x{c}")

print("\n=== 2) 导出符号中含 压缩/存储/图库 相关关键词的 ===")
pat = re.compile(r"(?i)(lzw|compress|decompress|storage|slib|slide|dwb|tkw|loadlib|dwglib|dwg2|symb)")
for f in files:
    try:
        pe = pefile.PE(f, fast_load=True)
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_EXPORT']])
    except Exception:
        continue
    if not hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
        pe.close(); continue
    names = []
    for e in pe.DIRECTORY_ENTRY_EXPORT.symbols:
        if e.name:
            n = e.name.decode("ascii", "replace")
            if pat.search(n):
                names.append(n)
    if names:
        print(f"\n-- {os.path.basename(f)}  ({len(names)} 个)")
        for n in sorted(set(names))[:40]:
            print("   ", n)
    pe.close()
