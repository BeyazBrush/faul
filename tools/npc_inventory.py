# -*- coding: utf-8 -*-
"""
npc_inventory.py  - MSC tum NPC/govde modellerini envanterler.
Hangi GameObject hangi mesh kullaniyor, hangi clip'ler var, kok yolu ne.
"""
import UnityPy, os, sys
from collections import defaultdict

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


def main():
    level = sys.argv[1] if len(sys.argv) > 1 else os.path.join(DATA, "level2")
    env = UnityPy.load(level)

    tr = {}
    go_tr = {}
    for o in env.objects:
        if o.type.name == "Transform":
            try:
                t = o.read()
                nm = getattr(t.m_GameObject.read(), "m_Name", "")
            except Exception:
                continue
            tr[o.path_id] = {"name": nm, "parent": getattr(t.m_Father, "path_id", 0)}
    for pid, i in tr.items():
        go_tr.setdefault(i["name"], []).append(pid)

    def path_of(pid):
        parts, g = [], 0
        while pid in tr and g < 128:
            parts.append(tr[pid]["name"])
            pid = tr[pid]["parent"]
            g += 1
        return "/".join(reversed(parts))

    rows = []
    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        try:
            s = o.read()
        except Exception:
            continue
        g = deref(s.m_GameObject)
        gn = getattr(g, "m_Name", "") if g else ""
        m = deref(s.m_Mesh)
        mn = getattr(m, "m_Name", "") if m else ""
        rb = getattr(s.m_RootBone, "path_id", 0) if s.m_RootBone else 0
        rpath = path_of(rb)
        nb = len(s.m_Bones or [])
        rows.append((gn, mn, nb, rpath))

    # mesh -> kullanan GameObject'ler
    bymesh = defaultdict(set)
    for gn, mn, nb, rpath in rows:
        bymesh[mn].add(gn)

    print("=" * 100)
    print("SKINNED MESH RENDERER'LAR  (%d adet)" % len(rows))
    print("=" * 100)
    print("%-22s %-22s %5s  %s" % ("GAMEOBJECT", "MESH", "BONE", "KOK(IK KEMIK) YOLU"))
    seen = set()
    for gn, mn, nb, rpath in sorted(rows, key=lambda r: (r[0], r[1])):
        key = (gn, mn, rpath)
        if key in seen:
            continue
        seen.add(key)
        print("%-22s %-22s %5d  %s" % (gn[:22], mn[:22], nb, rpath))

    print()
    print("=" * 100)
    print("MESH ADLARI  (%d benzersiz)" % len(bymesh))
    print("=" * 100)
    for mn in sorted(bymesh):
        if not mn:
            continue
        print("  %-26s GameObject: %s" % (mn, ", ".join(sorted(bymesh[mn])[:6])))


if __name__ == "__main__":
    main()