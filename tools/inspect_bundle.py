# -*- coding: utf-8 -*-
"""Bundle'i ayrintili ac: header, bloklar, nesneler."""
import struct
import sys
import UnityPy

path = sys.argv[1]
raw = open(path, "rb").read()
print("dosya:", path)
print("boyut:", len(raw))
print("imza:", raw[:20])

# Unity 5 bundle header
sig = raw[:8]
if sig != b"UnityWeb":
    print("BEKLENMEYEN IMZA")
ver = struct.unpack(">i", raw[8:12])[0]      # sürüm big-endian
print("unity versiyonu:", ver)
off = 12
# size tablosu: min veri, uncompressed size, compressed size (int32 BE)
total = struct.unpack(">i", raw[12:16])[0]
compressed = struct.unpack(">I", raw[16:20])[0]
flags = struct.unpack(">i", raw[20:24])[0]
print("toplam blok boyutu:", total, "sikistirilmis:", compressed, "flags:", hex(flags))

env = UnityPy.load(path)
print("env.container:", list(env.container.keys()))
print("env.files:", len(env.files))
print("env.objects:", len(list(env.objects)))
for f in env.files:
    print("  file:", type(f).__name__, getattr(f, "name", "?"))
    for attr in ("externals", "uncompressed", "compressed_size", "blockSize"):
        if hasattr(f, attr):
            print("     ", attr, "=", getattr(f, attr))