# -*- coding: utf-8 -*-
"""
check_rig_space.py
bindpose'lari iki yorumla (satir/sutun major) alip inv(bindpose) farklarindan
iskeletin mesh uzayindaki seklini cikarir. Dogru yorumda iskelet mesh'in
uzun ekseniyle hizali, ayaklar dusuk, kafa yuksek olmalidir.
"""
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


def mmul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def minv(m):
    d = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
         - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
         + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))
    if abs(d) < 1e-14:
        d = 1e-14
    inv = [[0.0] * 4 for _ in range(4)]
    inv[0][0] = (m[1][1] * m[2][2] - m[1][2] * m[2][1]) / d
    inv[0][1] = -(m[0][1] * m[2][2] - m[0][2] * m[2][1]) / d
    inv[0][2] = (m[0][1] * m[1][2] - m[0][2] * m[1][1]) / d
    inv[1][0] = -(m[1][0] * m[2][2] - m[1][2] * m[2][0]) / d
    inv[1][1] = (m[0][0] * m[2][2] - m[0][2] * m[2][0]) / d
    inv[1][2] = -(m[0][0] * m[1][2] - m[0][2] * m[1][0]) / d
    inv[2][0] = (m[1][0] * m[2][1] - m[1][1] * m[2][0]) / d
    inv[2][1] = -(m[0][0] * m[2][1] - m[0][1] * m[2][0]) / d
    inv[2][2] = (m[0][0] * m[1][1] - m[0][1] * m[1][0]) / d
    t = [m[r][3] for r in range(3)]
    for r in range(3):
        inv[r][3] = -(inv[r][0] * t[0] + inv[r][1] * t[1] + inv[r][2] * t[2])
    inv[3] = [0.0, 0.0, 0.0, 1.0]
    return inv


f = open(sys.argv[1] if len(sys.argv) > 1 else r"D:\MSCSteamMP\extracted_npc\mpnpc.dat", "rb")
f.read(5); struct.unpack("<i", f.read(4))[0]; rstr(f)
vc, ic, smc = struct.unpack("<3i", f.read(12))
verts = struct.unpack("<%df" % (vc * 3), f.read(vc * 12))
f.read(vc * 12 + vc * 16 + vc * 8); f.read(ic * 4); f.read(smc * 4)
bc = struct.unpack("<i", f.read(4))[0]
bones = []
for _ in range(bc):
    nm = rstr(f); pth = rstr(f)
    par = struct.unpack("<i", f.read(4))[0]; issk = struct.unpack("<i", f.read(4))[0]
    pos = struct.unpack("<3f", f.read(12)); rot = struct.unpack("<4f", f.read(16))
    scl = struct.unpack("<3f", f.read(12)); bp = struct.unpack("<16f", f.read(64))
    bones.append({"n": nm, "p": pth, "par": par, "bp": bp})
root = struct.unpack("<i", f.read(4))[0]
sc = struct.unpack("<i", f.read(4))[0]
skin = struct.unpack("<%di" % sc, f.read(sc * 4))

mn = [min(verts[i::3]) for i in range(3)]
mx = [max(verts[i::3]) for i in range(3)]
print("mesh AABB  x[%.3f %.3f]  y[%.3f %.3f]  z[%.3f %.3f]"
      % (mn[0], mx[0], mn[1], mx[1], mn[2], mx[2]))
print("mesh uzunlugu: x=%.3f y=%.3f z=%.3f" % (mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]))
print()


def as_unity(v):
    """Unity Matrix4x4 duzeni: sutun major, ceviri 12..14"""
    return [[v[c * 4 + r] for c in range(4)] for r in range(4)]


def as_transposed(v):
    return [[v[r * 4 + c] for c in range(4)] for r in range(4)]


for label, conv in (("SUTUN-MAJOR (Unity native)", as_unity),
                    ("SATIR-MAJOR (transpose)", as_transposed)):
    pts = {}
    for k, bi in enumerate(skin):
        m = minv(conv(bones[bi]["bp"]))
        pts[bones[bi]["n"]] = (m[0][3], m[1][3], m[2][3])
    print("=" * 74)
    print(label)
    print("  %-16s %8s %8s %8s" % ("bone", "x", "y", "z"))
    for nm in ("pelvis", "spine_middle", "spine_upper", "head",
               "thig_left", "knee_left", "ankle_left",
               "thig_right", "knee_right", "ankle_right",
               "hand_left", "hand_right"):
        if nm in pts:
            print("  %-16s %8.3f %8.3f %8.3f" % (nm, pts[nm][0], pts[nm][1], pts[nm][2]))
    # pelvis'e gore z sıralaması: ayaklar en dusuk, kafa en yuksek olmali
    order = sorted(pts.items(), key=lambda kv: kv[1][2])
    print("  z sirasi (kucuk->buyuk): %s" % " < ".join(n for n, _ in order))
    order = sorted(pts.items(), key=lambda kv: kv[1][1])
    print("  y sirasi (kucuk->buyuk): %s" % " < ".join(n for n, _ in order))
    order = sorted(pts.items(), key=lambda kv: kv[1][0])
    print("  x sirasi (kucuk->buyuk): %s" % " < ".join(n for n, _ in order))
    print()