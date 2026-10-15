# -*- coding: utf-8 -*-
"""
verify_char_data.py
mpnpc.dat dosyasini C# ile ayni mantikla geri okur ve dogrular.
"""
import struct
import sys

KIND = {0: "position", 1: "rotation", 2: "scale"}
COMP = {0: 3, 1: 4, 2: 3}


def rstr(f):
    n = 0
    shift = 0
    while True:
        b = f.read(1)
        if not b:
            raise EOFError
        x = b[0]
        n |= (x & 0x7F) << shift
        shift += 7
        if not (x & 0x80):
            break
    return f.read(n).decode("utf-8", "replace")


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else r"D:\MSCSteamMP\extracted_npc\mpnpc.dat"
    f = open(path, "rb")
    magic = f.read(5)
    assert magic == b"MPNPC", "magic hatasi: %r" % magic
    ver = struct.unpack("<i", f.read(4))[0]
    name = rstr(f)
    print("magic=MPNPC version=%d name=%s" % (ver, name))

    vc, ic, smc = struct.unpack("<3i", f.read(12))
    verts = struct.unpack("<%df" % (vc * 3), f.read(vc * 12))
    norms = struct.unpack("<%df" % (vc * 3), f.read(vc * 12))
    tans = struct.unpack("<%df" % (vc * 4), f.read(vc * 16))
    uvs = struct.unpack("<%df" % (vc * 2), f.read(vc * 8))
    idx = struct.unpack("<%di" % ic, f.read(ic * 4))
    subs = struct.unpack("<%di" % smc, f.read(smc * 4))

    print("MESH  : %d vertex, %d index, %d submesh %s" % (vc, ic, smc, list(subs)))
    assert sum(subs) == ic, "submesh toplami indeks ile eslesmiyor"
    print("  v[0]  = %s" % [round(verts[i], 4) for i in range(3)])
    print("  n[0]  = %s" % [round(norms[i], 4) for i in range(3)])
    print("  t[0]  = %s" % [round(tans[i], 4) for i in range(4)])
    print("  uv[0] = %s" % [round(uvs[i], 4) for i in range(2)])
    print("  i[0:9]= %s" % list(idx[:9]))
    bad = 0
    for i in range(0, ic, 3):
        if not (0 <= idx[i] < vc and 0 <= idx[i + 1] < vc and 0 <= idx[i + 2] < vc):
            bad += 1
    print("  bozuk ucgen: %d / %d" % (bad, ic // 3))
    nl = [(norms[i] ** 2 + norms[i + 1] ** 2 + norms[i + 2] ** 2) ** 0.5
          for i in range(0, vc * 3, 3)]
    print("  normal uzunlugu %.4f..%.4f" % (min(nl), max(nl)))

    bc = struct.unpack("<i", f.read(4))[0]
    bones = []
    for _ in range(bc):
        bn = rstr(f)
        bp = rstr(f)
        par = struct.unpack("<i", f.read(4))[0]
        issk = struct.unpack("<i", f.read(4))[0]
        pos = struct.unpack("<3f", f.read(12))
        rot = struct.unpack("<4f", f.read(16))
        scl = struct.unpack("<3f", f.read(12))
        bpm = struct.unpack("<16f", f.read(64))
        bones.append({"n": bn, "p": bp, "par": par, "sk": issk,
                      "pos": pos, "rot": rot, "scl": scl, "bp": bpm})
    root = struct.unpack("<i", f.read(4))[0]
    print("BONES : %d kayit, rootBone=%d (%s)" % (bc, root, bones[root]["p"]))
    for i, b in enumerate(bones):
        mark = "S" if b["sk"] else " "
        print("  %2d %s %-26s parent=%-34s pos=(%.3f,%.3f,%.3f) scl=(%.2f,%.2f,%.2f)"
              % (i, mark, b["p"], bones[b["par"]]["p"] if b["par"] >= 0 else "-",
                 b["pos"][0], b["pos"][1], b["pos"][2],
                 b["scl"][0], b["scl"][1], b["scl"][2]))
    # yol butunlugu
    paths = set(b["p"] for b in bones)
    miss = []
    for b in bones:
        if b["par"] < 0:
            continue
        want = bones[b["par"]]["p"] + "/" + b["n"] if bones[b["par"]]["p"] else b["n"]
        if b["p"] != want:
            miss.append((b["p"], want))
    print("  yol tutarliligi: %s" % ("TAMAM" if not miss else "BOZUK %s" % miss[:4]))
    # bindpose kontrolu (son satir 0,0,0,1 olmali)
    for i, b in enumerate(bones):
        if b["bp"][12:16] != (0.0, 0.0, 0.0, 1.0):
            print("  ! bindpose[%d] son satir = %s" % (i, b["bp"][12:16]))
            break

    sc = struct.unpack("<i", f.read(4))[0]
    skin_order = struct.unpack("<%di" % sc, f.read(sc * 4))
    print("SKINORDER: %d kayit" % sc)
    for k, bi in enumerate(skin_order):
        b = bones[bi]
        print("   bones[%2d] = rec %-2d  %-58s bindpose_t3=%s"
              % (k, bi, b["p"], [round(x, 3) for x in b["bp"][12:16]]))
    print("  tum kayitlar skin mi: %s" % all(bones[i]["sk"] for i in skin_order))
    print("  ilk 3: %s" % [bones[i]["n"] for i in skin_order[:3]])

    wc = struct.unpack("<i", f.read(4))[0]
    wsum_ok = True
    maxidx = 0
    for i in range(wc):
        bi = struct.unpack("<4i", f.read(16))
        wt = struct.unpack("<4f", f.read(16))
        s = sum(wt)
        if abs(s - 1.0) > 0.01:
            wsum_ok = False
        maxidx = max(maxidx, max(bi))
    print("WEIGHT: %d kayit, toplam~1.0=%s, maxBoneIndex=%d" % (wc, wsum_ok, maxidx))
    skin_idx = [i for i, b in enumerate(bones) if b["sk"]]
    print("  skinBone listesi: %s" % [bones[i]["n"] for i in skin_idx])
    print("  maxBoneIndex skinBone icinde mi: %s" % (maxidx < len(skin_idx)))

    mc = struct.unpack("<i", f.read(4))[0]
    mats = []
    for _ in range(mc):
        mats.append((rstr(f), rstr(f)))
    print("MAT   : %d" % mc)
    for m in mats:
        print("      %s -> %s" % m)
    print("  submesh sayisi == materyal sayisi: %s" % (smc == mc))

    cc = struct.unpack("<i", f.read(4))[0]
    print("CLIPS : %d" % cc)
    for _ in range(cc):
        cn = rstr(f)
        ln = struct.unpack("<f", f.read(4))[0]
        loop = struct.unpack("<i", f.read(4))[0]
        ncu = struct.unpack("<i", f.read(4))[0]
        print("  %-10s len=%.3fs loop=%d curves=%d" % (cn, ln, loop, ncu))
        for _ in range(ncu):
            cp = rstr(f)
            kind = struct.unpack("<i", f.read(4))[0]
            kc = struct.unpack("<i", f.read(4))[0]
            comp = COMP[kind]
            vals = struct.unpack("<%df" % (kc * comp), f.read(kc * comp * 4))
            times = struct.unpack("<%df" % kc, f.read(kc * 4))
            if cp not in paths:
                print("     ! YOL YOK: %s" % cp)
            if kc and times[0] > 0.001:
                print("     ! ilk anahtar t=%.3f (0 olmali)" % times[0])
            print("     %-58s %-9s keys=%-4d t=%.3f..%.3f"
                  % (cp, KIND[kind], kc, times[0] if kc else 0,
                     times[-1] if kc else 0))

    rest = f.read()
    print("-" * 70)
    print("dosya sonrasi kalan bayt: %d (0 olmali)" % len(rest))
    f.close()
    print("DOGRULAMA TAMAM")


if __name__ == "__main__":
    main()