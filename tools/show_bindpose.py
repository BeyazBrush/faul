# -*- coding: utf-8 -*-
"""bindpose matrislerini goster + hangi yorumun tutarli oldugunu sinirla."""
import struct
import sys


def rstr(f):
    n = 0
    shift = 0
    while True:
        x = f.read(1)[0]
        n |= (x & 0x7F) << shift
        shift += 7
        if not (x & 0x80):
            break
    return f.read(n).decode("utf-8", "replace")


f = open(sys.argv[1] if len(sys.argv) > 1 else r"D:\MSCSteamMP\extracted_npc\mpnpc.dat", "rb")
f.read(5); struct.unpack("<i", f.read(4))[0]; rstr(f)
vc, ic, smc = struct.unpack("<3i", f.read(12))
f.read(vc * 12 * 2 + vc * 16 + vc * 8); f.read(ic * 4); f.read(smc * 4)
bc = struct.unpack("<i", f.read(4))[0]
bones = []
for _ in range(bc):
    nm = rstr(f); pth = rstr(f)
    par = struct.unpack("<i", f.read(4))[0]; issk = struct.unpack("<i", f.read(4))[0]
    pos = struct.unpack("<3f", f.read(12)); rot = struct.unpack("<4f", f.read(16))
    scl = struct.unpack("<3f", f.read(12)); bp = struct.unpack("<16f", f.read(64))
    bones.append({"n": nm, "p": pth, "par": par, "sk": issk,
                  "pos": pos, "rot": rot, "scl": scl, "bp": bp})
root = struct.unpack("<i", f.read(4))[0]
sc = struct.unpack("<i", f.read(4))[0]
skin = struct.unpack("<%di" % sc, f.read(sc * 4))

print("rootBone:", bones[root]["p"], "\n")
for k in (0, 1, 3, 8, 16, 19):
    bi = skin[k]
    b = bones[bi]
    v = b["bp"]
    print("bones[%2d] %s" % (k, b["p"].split("skeleton/")[-1]))
    print("   row-major:")
    for r in range(4):
        print("      [%8.4f %8.4f %8.4f %8.4f]" % tuple(v[r * 4 + i] for i in range(4)))
    print("   col-major (v[r*4+c] = satir r, sutun c -> degil, sutun major):")
    for r in range(4):
        print("      [%8.4f %8.4f %8.4f %8.4f]" % tuple(v[c * 4 + r] for c in range(4)))
    print("   local pos = %s  rot = %s" % ([round(x, 4) for x in b["pos"]],
                                          [round(x, 4) for x in b["rot"]]))
    print()