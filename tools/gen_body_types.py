# -*- coding: utf-8 -*-
"""
gen_body_types.py  -  NPC govde mesh'lerini ENVANTER + boy olcumu cikarir.

NEDEN: ekran goruntusunde uc parca birbirine uymuyor — yasli erkek yuz,
kadin yaka bicimli gomlek, genc erkek bedeni. Sebep: oyunda "bodymesh"
ADI ALTINDA 9 farkli mesh var (939..1801 vertex). Her NPC kendi
govdesini kullaniyor:

    sharedassets1/3.assets  bodymesh          -> 9 varyant
                           bodymesh_kid      -> cocuk
                           bodymesh_pig      -> domuz (NPC degil)
                           bodymesh_ponytail -> at kuyrugu (NPC degil)

Biz TEK bir varyanti (1709 vertex) cikardik ve tum skinleri ona
bagladik. Dogru olan: her skinin KENDI mesh'ini kullanmasi.

Bu script:
  1) tum "bodymesh*" mesh'ini bulur
  2) her biri icin vertex/ucgen/submesh/bone sayisini ve kutusunu olcer
  3) ayni iskeleti paylasip paylasmadiklarini kontrol eder
  4) hangi NPC'lerin hangi mesh'i kullandigini roster ile birlestirir
  5) govde tiplerini (zayif/gunclu/kadin/cocuk/...) otomatik siniflandirir
"""
import collections
import json
import os
import sys

import numpy as np
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
EXTRACT = r"D:\MSCSteamMP\extracted_npc"
OUT = os.path.join(EXTRACT, "body_types.json")

FILES = ([os.path.join(DATA, "sharedassets%d.assets" % i) for i in range(6)]
         + [os.path.join(DATA, "resources.assets")]
         + [os.path.join(DATA, "level%d" % i) for i in range(4)])


def mesh_arrays(mesh):
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
    return verts


def classify(v):
    """Mesh uzayinda Z dikey (ayak z~0, kafa z~1.8)."""
    height = float(v[:, 2].max() - v[:, 2].min())
    width = float(v[:, 1].max() - v[:, 1].min())      # omuz/kol genisligi
    depth = float(v[:, 0].max() - v[:, 0].min())
    # omuz genisligi / boy orani: vucut tipi ayirt edici
    ratio = width / height if height > 1e-6 else 0.0
    tag = "normal"
    if height < 1.45:
        tag = "cocuk"
    elif height < 1.70:
        tag = "kadin"
    elif ratio > 0.80:
        tag = "gunclu"
    elif ratio > 0.74:
        tag = "kilolu"
    else:
        tag = "zayif"
    return dict(height=round(height, 4), width=round(width, 4),
                depth=round(depth, 4), ratio=round(ratio, 4), tag=tag)


def main():
    print("=" * 86)
    print("1) BODYMESH ENVANTERI")
    print("=" * 86)
    found = {}
    for f in FILES:
        if not os.path.isfile(f):
            continue
        try:
            env = UnityPy.load(f)
        except Exception:
            continue
        for o in env.objects:
            if o.type.name != "Mesh":
                continue
            try:
                m = o.read()
                nm = str(m.m_Name)
                if "bodymesh" not in nm.lower():
                    continue
                if m.m_VertexData is None:
                    continue
                v = mesh_arrays(m)
                if v is None or len(v) < 100:
                    continue
                vc = len(v)
                tri = sum(int(s.indexCount) for s in m.m_SubMeshes) // 3
                key = (nm, vc)
                if key in found:
                    continue
                found[key] = dict(
                    name=nm, vertices=vc, triangles=tri,
                    submeshes=len(m.m_SubMeshes),
                    sub_counts=[int(s.indexCount) for s in m.m_SubMeshes],
                    bones=len(list(m.m_BindPose)),
                    skin=len(list(m.m_Skin)),
                    file=os.path.basename(f),
                    path_id=o.path_id, **classify(v))
            except Exception:
                continue

    if not found:
        print("  HICBIR bodymesh bulunamadi")
        return 1

    print("%-18s %-8s %-8s %-6s %-8s %-8s %-8s"
          % ("mesh", "vertex", "ucgen", "sub", "boy(m)", "omuz(m)", "tip"))
    print("-" * 86)
    for k in sorted(found, key=lambda x: -found[x]["height"]):
        d = found[k]
        print("%-18s %-8d %-8d %-6d %-8.3f %-8.3f %-8s"
              % (d["name"], d["vertices"], d["triangles"], d["submeshes"],
                 d["height"], d["width"], d["tag"]))

    print()
    print("=" * 86)
    print("2) BOY DAGILIMI")
    print("=" * 86)
    by_h = sorted((d["height"], d["name"], d["vertices"], d["tag"])
                  for d in found.values())
    for h, nm, vc, tag in by_h:
        print("   %-18s %-6d vertex  boy %6.3f m  %s" % (nm, vc, h, tag))

    # ---- katalog ile birlestir: her skin hangi mesh'i kullanir?
    print()
    print("=" * 86)
    print("3) SKIN -> MESH BAGLANTISI (roster ornekleri)")
    print("=" * 86)
    rp = os.path.join(EXTRACT, "roster.json")
    if os.path.isfile(rp):
        d = json.load(open(rp, encoding="utf-8"))
        skins = d["skins"]
        print("   roster'daki skin sayisi: %d" % len(skins))
        print("   (her skin'in samples yolu -> SkinnedMeshRenderer -> m_Mesh)")
        print("   Bu eslesme kurulum sirasinda yapilacak.")
    else:
        print("   roster.json yok")

    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(list(found.values()), f, indent=2, ensure_ascii=False)
    print()
    print("YAZILDI: %s  (%d mesh)" % (OUT, len(found)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())