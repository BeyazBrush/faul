# -*- coding: utf-8 -*-
"""
gen_skin_mesh_map.py  -  Her skin'in KENDI govde mesh'ini bulur.

SORUN: "bodymesh" adi altinda birden fazla mesh var ve NPC'ler FARKLI
olanlari kullaniyor. level2'de olculdu (186 SMR):

    bodymesh       1712 vertex   115 SMR
    bodymesh       1709 vertex    35 SMR     <-- bizim cikardigimiz
    bodymesh       1714 vertex    14 SMR
    bodymesh       1697 vertex     8 SMR
    bodymesh_kid   1714 vertex     5 SMR
    bodymesh       1789 vertex     5 SMR
    bodymesh        960 vertex     2 SMR
    bodymesh_pig   1627 vertex     2 SMR

Biz tek varyanti (1709) cikardik ve 70 skinin tamamini ona bagladik.
Sonuc: yasli yuz + kadin yaka gomlek + genc erkek bedeni ayni
modelde birlesiyor.

COZUM: her skin'in ornek NPC'sine (roster.json samples) git, o NPC'nin
SkinnedMeshRenderer'ini coz ve mesh'in vertex sayisini kaydet. Sonra
katalog skin -> mesh eslesmesi tasir ve runtime'da dogru mesh secilir.

ONEMLI: PPtr cozumlemesi sadece m_Mesh.read() ile calisiyor; PPtr
path_id'si tek basina anlamsiz (dis PPtr'lar dosyaya gore cozulur).
"""
import collections
import json
import os

import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
EXTRACT = r"D:\MSCSteamMP\extracted_npc"
OUT = os.path.join(EXTRACT, "skin_mesh_map.json")
LEVELS = ["level0", "level1", "level2", "level3"]


def main():
    roster = json.load(open(os.path.join(EXTRACT, "roster.json"),
                            encoding="utf-8"))
    skins = roster["skins"]

    # Her path icin mesh vertex sayisi: path -> vertex
    path_mesh = {}
    print("level dosyalari taraniyor (SMR -> mesh)...")
    for lv in LEVELS:
        p = os.path.join(DATA, lv)
        if not os.path.isfile(p):
            continue
        env = UnityPy.load(p)
        names = {}
        go_tr = {}
        tr_parent = {}
        for o in env.objects:
            if o.type.name == "GameObject":
                try:
                    names[o.path_id] = o.read().m_Name
                except Exception:
                    pass
            elif o.type.name == "Transform":
                t = o.read()
                tr_parent[o.path_id] = getattr(t.m_Father, "path_id", 0)
                go_tr[getattr(t.m_GameObject, "path_id", -1)] = o.path_id

        def path_of(pid):
            parts = []
            g = 0
            while pid in tr_parent and g < 96:
                gp = [k for k, v in go_tr.items() if v == pid]
                parts.append(names.get(gp[0], "?") if gp else "?")
                pid = tr_parent.get(pid, 0)
                g += 1
            return "/".join(reversed(parts))

        for o in env.objects:
            if o.type.name != "SkinnedMeshRenderer":
                continue
            try:
                s = o.read()
                m = s.m_Mesh.read()
                nm = str(m.m_Name)
                if "bodymesh" not in nm.lower():
                    continue
                vd = getattr(m, "m_VertexData", None)
                if vd is None:
                    continue
                vc = int(vd.m_VertexCount)
                if vc < 100:
                    continue
                gpid = getattr(s.m_GameObject, "path_id", -1)
                tpid = go_tr.get(gpid)
                if tpid is None:
                    continue
                path_mesh[path_of(tpid)] = (nm, vc)
            except Exception:
                continue
        print("   %-8s -> toplam %d yol" % (lv, len(path_mesh)))

    print()
    print("cozulen NPC yolu: %d" % len(path_mesh))

    # skin -> mesh
    result = {}
    for s in skins:
        key = "|".join([s["shirtMat"], s["pantsMat"], s["faceMat"]])
        if key in result:
            continue
        hit = None
        for path in s.get("samples", []):
            if path in path_mesh:
                hit = path_mesh[path]
                break
        # yol bulunamadiysa mesh adiyla dene (bodymesh/<...>/Char/bodymesh)
        if hit is None:
            for path, v in path_mesh.items():
                if path.endswith("/" + s.get("samples", [""])[0].split("/")[-1]):
                    hit = v
                    break
        result[key] = dict(name=hit[0], vertices=hit[1]) if hit else None

    ok = sum(1 for v in result.values() if v)
    print("skin -> mesh eslesmesi: %d/%d cozuldu" % (ok, len(result)))

    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)
    print("YAZILDI: %s" % OUT)

    c = collections.Counter(
        (v["name"], v["vertices"]) for v in result.values() if v)
    print()
    print("skinlarin mesh dagilimi:")
    for (nm, vc), k in c.most_common():
        print("   %-14s %-6d vertex : %d skin" % (nm, vc, k))
    unknown = [k for k, v in result.items() if not v]
    if unknown:
        print()
        print("cozulemeyen skin sayisi: %d" % len(unknown))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())