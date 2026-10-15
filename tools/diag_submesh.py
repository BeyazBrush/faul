# -*- coding: utf-8 -*-
"""
diag_submesh.py  -  Hangi submesh hangi vücut parçası?

PlayerSkin.Apply varsayımı: submesh 0 = gömlek, 1 = pantolon, 2 = yüz.
Bu varsayım build sırasında DOĞRULANMAMIŞTI; sıralama oyunun
m_Materials dizisinden alındı. Yanlışsa kıyafetler yanlış vücut
parçasına biner (gömlek yüze, pantolon kola vb).

NASIL ÖLÇÜLÜR:
mesh uzayında gövde Z ekseni boyunca uzanıyor (ayaklar z=0.048,
kafa z=1.569; önceden ölçüldü). Her submesh için:
  - vertex kutusu (x,y,z)
  - ağırlık merkezi
  - ortalama normal (yüzeyin hangi yöne baktığı)
  - doku adı
Böylece yüz (kafa, yüksek Z), gövde (gövde Z), pantolon (bacak, alçak Z)
ayırt edilir.

Ayrıca her submesh'in ÜÇGEN SAYISI ve alanı: yüz küçüktür, gövde
büyüktür. Tek üçgenli/yüz küçük => yüz.
"""
import os
import sys

import numpy as np
import UnityPy

BUNDLE = sys.argv[1] if len(sys.argv) > 1 else r"D:\modmscMP\MSCSteamMP\assets\mpnpc"


def q4(q):
    return (float(q.x), float(q.y), float(q.z), float(q.w))


def main():
    env = UnityPy.load(BUNDLE)
    mesh = None
    mat_tex = {}
    mats = None
    for o in env.objects:
        if o.type.name == "Mesh":
            mesh = o.read()
        elif o.type.name == "Material":
            m = o.read()
            for tf in (getattr(m.m_SavedProperties, "m_TexEnvs", None) or []):
                env2 = tf[1] if isinstance(tf, (list, tuple)) and len(tf) >= 2 else None
                ptr = getattr(env2, "m_Texture", None)
                if ptr is None:
                    continue
                try:
                    t = ptr.read()
                    t = t.read() if hasattr(t, "read") else t
                    mat_tex[getattr(m, "m_Name", "")] = getattr(t, "m_Name", None)
                except Exception:
                    pass
        elif o.type.name == "SkinnedMeshRenderer":
            mats = list(o.read().m_Materials or [])
    if mesh is None:
        print("mesh yok")
        return 1

    vd = mesh.m_VertexData
    n = int(vd.m_VertexCount)
    data = np.frombuffer(mesh.m_VertexData.m_DataSize, dtype="<f4")
    streams = {}
    for ch in vd.m_Channels:
        if int(ch.dimension) <= 0:
            continue
        streams.setdefault(int(ch.stream), []).append(
            (int(ch.offset), int(ch.dimension)))
    verts = norms = uvs = None
    base = 0
    for st in sorted(streams):
        items = sorted(streams[st])
        stride_f = sum(d for _, d in items)
        rows = np.arange(n) * stride_f + base // 4
        for off, dim in items:
            arr = data[rows[:, None] + np.arange(dim)[None, :] + off // 4]
            if dim == 3 and st == 0 and off == 0:
                verts = arr
            elif dim == 3 and st == 0 and off == 12:
                norms = arr
            elif dim == 2:
                uvs = arr
        base += stride_f * 4 * n

    ib = mesh.m_IndexBuffer
    want = sum(int(x.indexCount) for x in mesh.m_SubMeshes)
    if isinstance(ib, list) and len(ib) == want * 2 and max(ib) <= 255:
        idx = np.frombuffer(bytearray(ib), dtype="<u2")
    else:
        idx = np.array([int(i) for i in ib])

    print("mesh: %d vertex, %d ucgen" % (n, len(idx) // 3))
    print("mesh kutusu: x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f"
          % (verts[:, 0].min(), verts[:, 0].max(), verts[:, 1].min(),
             verts[:, 1].max(), verts[:, 2].min(), verts[:, 2].max()))
    print()
    print("Mesh uzayinda Z dikey eksen (ayak z=0.048, kafa z=1.569).")
    print()

    cursor = 0
    print("%-6s %-7s %-8s %-26s %-26s %-22s"
          % ("sub", "ucgen", "alan", "vertex kutusu (x/y/z)", "merkez", "ort.normal"))
    print("-" * 128)
    for si, sm in enumerate(mesh.m_SubMeshes):
        cnt = int(sm.indexCount)
        tris = idx[cursor:cursor + cnt].reshape(-1, 3)
        cursor += cnt
        # indeksler mesh genelinde; yerel degil. verts/norms ile dogrudan.
        a = verts[tris[:, 0]]; b = verts[tris[:, 1]]; c = verts[tris[:, 2]]
        area = 0.5 * np.linalg.norm(np.cross(b - a, c - a), axis=1).sum()
        nmean = norms[tris].reshape(-1, 3).mean(axis=0)
        v = verts[tris.reshape(-1)]
        box = ("x %.2f..%.2f y %.2f..%.2f z %.2f..%.2f"
               % (v[:, 0].min(), v[:, 0].max(), v[:, 1].min(),
                  v[:, 1].max(), v[:, 2].min(), v[:, 2].max()))
        print("sub%d   %-7d %-8.3f %-26s (%6.3f %6.3f %6.3f)  (%6.3f %6.3f %6.3f)"
              % (si, len(tris), area, box,
                 v[:, 0].mean(), v[:, 1].mean(), v[:, 2].mean(),
                 nmean[0], nmean[1], nmean[2]))

    print()
    print("materyal -> doku:")
    for i, ptr in enumerate(mats):
        try:
            o = ptr.read()
            m = o.read() if hasattr(o, "read") else o
            mn = getattr(m, "m_Name", "?")
        except Exception:
            mn = "?"
        print("  sub%d: %-18s -> %s" % (i, mn, mat_tex.get(mn, "?")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())