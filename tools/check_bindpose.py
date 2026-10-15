# -*- coding: utf-8 -*-
"""
check_bindpose.py
bindpose matrisinin satir-mi sutun-mu duzeni belirler.
Teori: bindpose = inverse(boneWorld) * rootBoneWorld
Iki yorumu da hesaplayip kayitla karsilastirir.
"""
import struct
import sys

KIND_POS, KIND_ROT, KIND_SCALE = 0, 1, 2
COMP = {0: 3, 1: 4, 2: 3}


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


def trs(pos, rot, scl):
    x, y, z, w = rot
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    m = [[1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy)],
         [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx)],
         [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)]]
    for c in range(3):
        for r in range(3):
            m[r][c] *= scl[c]
    for r in range(3):
        m[r].append(pos[r])
    m.append([0.0, 0.0, 0.0, 1.0])
    return m


def mmul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def minv(m):
    # sadece TRS matrisleri icin yeterli (tam ters matris)
    d = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
         - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
         + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))
    if abs(d) < 1e-12:
        d = 1e-12
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


def row_major(v):      # v = e00,e01,...,e33 -> [r][c]
    return [[v[r * 4 + c] for c in range(4)] for r in range(4)]


def col_major(v):      # v dizisini sutun major yorumla
    return [[v[c * 4 + r] for c in range(4)] for r in range(4)]


def diff(a, b):
    return max(abs(a[r][c] - b[r][c]) for r in range(4) for c in range(4))


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else r"D:\MSCSteamMP\extracted_npc\mpnpc.dat"
    f = open(path, "rb")
    assert f.read(5) == b"MPNPC"
    struct.unpack("<i", f.read(4))[0]
    rstr(f)
    vc, ic, smc = struct.unpack("<3i", f.read(12))
    f.read(vc * 12 * 2 + vc * 16 + vc * 8)
    f.read(ic * 4)
    f.read(smc * 4)
    bc = struct.unpack("<i", f.read(4))[0]
    bones = []
    for _ in range(bc):
        nm = rstr(f); pth = rstr(f)
        par = struct.unpack("<i", f.read(4))[0]
        issk = struct.unpack("<i", f.read(4))[0]
        pos = struct.unpack("<3f", f.read(12))
        rot = struct.unpack("<4f", f.read(16))
        scl = struct.unpack("<3f", f.read(12))
        bp = struct.unpack("<16f", f.read(64))
        bones.append({"n": nm, "p": pth, "par": par, "sk": issk,
                      "pos": pos, "rot": rot, "scl": scl, "bp": bp})
    root = struct.unpack("<i", f.read(4))[0]
    sc = struct.unpack("<i", f.read(4))[0]
    skin_order = struct.unpack("<%di" % sc, f.read(sc * 4))

    # world matriseri (Char = kok)
    world = {}
    for i in range(bc):
        b = bones[i]
        m = trs(list(b["pos"]), list(b["rot"]), list(b["scl"]))
        if b["par"] >= 0:
            m = mmul(world[b["par"]], m)
        world[i] = m

    print("rootBone = %s" % bones[root]["p"])
    print("%-4s %-52s %10s %10s" % ("idx", "bone", "satirHata", "sutunHata"))
    print("-" * 82)
    tot_r = tot_c = 0.0
    for k, bi in enumerate(skin_order):
        expect = mmul(minv(world[bi]), world[root])
        d_r = diff(row_major(bones[bi]["bp"]), expect)
        d_c = diff(col_major(bones[bi]["bp"]), expect)
        tot_r = max(tot_r, d_r)
        tot_c = max(tot_c, d_c)
        print("%-4d %-52s %10.6f %10.6f"
              % (k, bones[bi]["p"].split("skeleton/")[-1], d_r, d_c))
    print("-" * 82)
    print("EN BUYUK HATA  satir-major: %.6f    sutun-major: %.6f" % (tot_r, tot_c))
    print("KARAR: %s" % ("SATIR-MAJOR (e{r}{c} = satir r, sutun c)"
                          if tot_r < tot_c else "SUTUN-MAJOR"))


if __name__ == "__main__":
    main()