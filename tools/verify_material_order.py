# -*- coding: utf-8 -*-
"""
verify_material_order.py  -  submesh -> doku siralamasi GERCEKTEN
shirt/pants/face mu? Her NPC icin OLCEK.

SORUN: skin_roster.py:147-148 siralamayi VARSAYIYOR:
    shirt, pants, face = mats[0], mats[1], mats[2]   # "dogrulandi" yorumu
Yorum yanlisti. diag_uv.py olcumu (bodymesh, 35 UV adacigi):
    adacik 0,1   pantolon  z 0.119..1.010  -> sub0
    adacik 2,6   govde     z 1.004..1.578  -> sub0
    adacik 3,4   kollar    z 0.991..1.524  -> sub0
    adacik 5,9   yuz       z 1.551..1.827  -> sub1
    adacik 10,11 ayaklar   z 0.007..0.074  -> sub1
Yani sub0 pantolonu da iceriyor, yuz sub1'de.

NASIL OLCULUR: her NPC'nin SkinnedMeshRenderer'i icin
  - submesh basina geometri kutusu (mesh uzayinda Z dikey)
  - o submesh'e atan malzemenin _MainTex adi
Malzeme adi kendi rolunu ele verir: char_shirt* = gomlek,
char_pants*/fabric*/cop_pants* = pantolon, char_face* = yuz.
Boylece "hangi submesh gomlek dokusu aliyor" dogrudan gorulur.
"""
import collections
import os
import re
import sys

import numpy as np
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
ALL_FILES = ([os.path.join(DATA, "sharedassets%d.assets" % i) for i in range(6)]
             + [os.path.join(DATA, "resources.assets")]
             + [os.path.join(DATA, n) for n in ("level0", "level1", "level2", "level3")])
ALL_FILES = [f for f in ALL_FILES if os.path.isfile(f)]
print("taranacak dosya: %d" % len(ALL_FILES))


def mat_tex_cache():
    cache = {}
    for f in ALL_FILES:
        if not os.path.isfile(f):
            continue
        try:
            env = UnityPy.load(f)
        except Exception:
            continue
        for o in env.objects:
            if o.type.name != "Material":
                continue
            try:
                m = o.read()
                nm = getattr(m, "m_Name", None)
                if not nm or nm in cache:
                    continue
                for tf in (getattr(m.m_SavedProperties, "m_TexEnvs", None) or []):
                    e = tf[1] if isinstance(tf, (list, tuple)) and len(tf) >= 2 else None
                    p = getattr(e, "m_Texture", None)
                    if p is None:
                        continue
                    t = p.read()
                    t = t.read() if hasattr(t, "read") else t
                    tn = getattr(t, "m_Name", None)
                    if tn:
                        cache[nm] = tn
                        break
            except Exception:
                pass
    return cache


def role_of_tex(name):
    n = (name or "").lower()
    if n.startswith("char_face"):
        return "FACE"
    if "pants" in n or n.startswith("fabric"):
        return "PANTS"
    if "shirt" in n:
        return "SHIRT"
    return "?"


def read_mesh_arrays(mesh):
    vd = mesh.m_VertexData
    n = int(vd.m_VertexCount)
    data = np.frombuffer(mesh.m_VertexData.m_DataSize, dtype="<f4")
    streams = {}
    for ch in vd.m_Channels:
        if int(ch.dimension) <= 0:
            continue
        streams.setdefault(int(ch.stream), []).append(
            (int(ch.offset), int(ch.dimension)))
    verts = None
    base = 0
    for st in sorted(streams):
        items = sorted(streams[st])
        stride_f = sum(d for _, d in items)
        rows = np.arange(n) * stride_f + base // 4
        for off, dim in items:
            if dim == 3 and st == 0 and off == 0:
                verts = data[rows[:, None] + np.arange(dim)[None, :] + off // 4]
        base += stride_f * 4 * n
    ib = mesh.m_IndexBuffer
    subc = [int(x.indexCount) for x in mesh.m_SubMeshes]
    want = sum(subc)
    if isinstance(ib, list) and len(ib) == want * 2 and max(ib) <= 255:
        idx = np.frombuffer(bytearray(ib), dtype="<u2")
    else:
        idx = np.array([int(i) for i in ib])
    return verts, idx, subc


def main():
    print("Material -> doku cozumleniyor (sharedassets + resources)...")
    MT = mat_tex_cache()
    print("  %d material cozuldu" % len(MT))

    print()
    print("=" * 88)
    print("NPC bazinda: submesh -> doku -> geometri (Z dikey: ayak 0.007, kafa 1.827)")
    print("=" * 88)

    votes = collections.Counter()
    seen = set()
    total = 0

    for f in ALL_FILES:
        if not os.path.isfile(f):
            continue
        try:
            env = UnityPy.load(f)
        except Exception:
            continue
        for o in env.objects:
            if o.type.name != "SkinnedMeshRenderer":
                continue
            try:
                s = o.read()
                mesh = s.m_Mesh.read()
                mn = getattr(mesh, "m_Name", "")
            except Exception:
                continue
            if not str(mn).startswith("bodymesh"):
                continue
            try:
                verts, idx, subc = read_mesh_arrays(mesh)
            except Exception:
                continue
            mats = []
            for mp in (s.m_Materials or []):
                try:
                    mm = mp.read()
                    mm = mm.read() if hasattr(mm, "read") else mm
                    mats.append(getattr(mm, "m_Name", "?"))
                except Exception:
                    mats.append("?")
            if len(mats) != len(subc):
                continue
            key = tuple(mats)
            if key in seen:
                continue
            seen.add(key)
            total += 1

            cursor = 0
            desc = []
            for si, cnt in enumerate(subc):
                tri = idx[cursor:cursor + cnt].reshape(-1, 3)
                cursor += cnt
                v = verts[tri.reshape(-1)]
                zmed = float(np.median(v[:, 2]))
                tex = MT.get(mats[si], mats[si])
                role = role_of_tex(tex)
                votes[(si, role)] += 1
                desc.append("sub%d[%s %s z%.2f-%.2f]"
                            % (si, mats[si][:16], role, v[:, 2].min(), v[:, 2].max()))
            if total <= 14:
                print("  " + "  ".join(desc))

    print()
    print("toplam farkli malzeme sirasi: %d" % total)
    print()
    print("OY SAYILARI (hangi submesh hangi rolde doku aliyor):")
    for si in sorted(set(k[0] for k in votes)):
        row = [(r, votes[(si, r)]) for r in ("SHIRT", "PANTS", "FACE", "?")
               if votes.get((si, r))]
        print("  sub%d: %s" % (si, ", ".join("%s=%d" % r for r in row)))

    print()
    dom = {}
    for si in sorted(set(k[0] for k in votes)):
        best = max(("SHIRT", "PANTS", "FACE"), key=lambda r: votes.get((si, r), 0))
        dom[si] = best
        print("  sub%d -> %s (%d/%d oy)"
              % (si, best, votes[(si, best)],
                 sum(votes.get((si, r), 0) for r in ("SHIRT", "PANTS", "FACE"))))
    print()
    print("SONUC: esleme " + ", ".join("sub%d=%s" % (k, v) for k, v in sorted(dom.items())))
    expected = ["SHIRT", "PANTS", "FACE"]
    if [dom.get(i) for i in range(3)] == expected:
        print("  -> '0=gomlek, 1=pantolon, 2=yuz' DOGRU")
    else:
        print("  -> '0=gomlek, 1=pantolon, 2=yuz' YANLIS, gercek: %s"
              % [dom.get(i) for i in range(3)])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())