# -*- coding: utf-8 -*-
"""
driver_assets.py
Araba suren NPC'leri bulur, materyal setlerini ve kullandiklari
dokularin adlarini cikarir.
"""
import UnityPy, os, sys, glob
from collections import defaultdict

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
LEVELS = ["level0", "level1", "level2", "level3"]


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


def load_all():
    envs = []
    for n in LEVELS:
        p = os.path.join(DATA, n)
        if os.path.isfile(p):
            envs.append((n, UnityPy.load(p)))
    return envs


print("=" * 100)
print("SURUCU NPC'LERI (bodymesh + 'driver' yolu)")
print("=" * 100)

drivers = []
for lvl, env in load_all():
    tr = {}
    for o in env.objects:
        if o.type.name == "Transform":
            try:
                t = o.read()
                nm = getattr(t.m_GameObject.read(), "m_Name", "")
            except Exception:
                continue
            tr[o.path_id] = {"n": nm, "p": getattr(t.m_Father, "path_id", 0),
                            "go": getattr(t.m_GameObject, "path_id", -1)}
    go_tr = {v["go"]: k for k, v in tr.items()}

    def path_of(pid):
        parts, g = [], 0
        while pid in tr and g < 96:
            parts.append(tr[pid]["n"])
            pid = tr[pid]["p"]
            g += 1
        return "/".join(reversed(parts))

    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        try:
            s = o.read()
            mn = getattr(deref(s.m_Mesh), "m_Name", "")
        except Exception:
            continue
        if not str(mn).startswith("bodymesh"):
            continue
        pid = go_tr.get(getattr(s.m_GameObject, "path_id", -1))
        p = path_of(pid) if pid else ""
        low = p.lower()
        if "driver" not in low:
            continue
        mats = []
        for mp in (s.m_Materials or []):
            m = deref(mp)
            mats.append(getattr(m, "m_Name", "?") if m else "?")
        drivers.append((lvl, p, mn, mats))

for lvl, p, mn, mats in sorted(set((a, b, c, tuple(d)) for a, b, c, d in drivers)):
    print("%-7s %-64s %-14s %s" % (lvl, p[-64:], mn, ", ".join(mats)))

print()
print("=" * 100)
print("MATERYAL ADLARI (bodymesh'e ait olanlar)")
print("=" * 100)
mats = set()
for _l, _p, _m, ms in drivers:
    for x in ms:
        mats.add(x)
print("  ", ", ".join(sorted(mats)))

print()
print("=" * 100)
print("DOKU ADLARI (shirt / pants / face icerenler)")
print("=" * 100)
found = defaultdict(list)
files = [os.path.join(DATA, "sharedassets%d.assets" % i) for i in range(6)] + \
        [os.path.join(DATA, "resources.assets")] + \
        [os.path.join(DATA, n) for n in LEVELS]
for f in files:
    if not os.path.isfile(f):
        continue
    env = UnityPy.load(f)
    for o in env.objects:
        if o.type.name != "Texture2D":
            continue
        try:
            t = o.read()
            n = getattr(t, "m_Name", "")
        except Exception:
            continue
        ln = n.lower()
        if any(k in ln for k in ("shirt", "pants", "face", "cloth", "body")):
            found[n].append((os.path.basename(f), o.path_id,
                             getattr(o.assets_file, "name", "?")))
for n in sorted(found):
    print("  %-40s %s" % (n, found[n][0][0]))
print("  toplam:", len(found))