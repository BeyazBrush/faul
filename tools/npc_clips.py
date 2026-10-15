# -*- coding: utf-8 -*-
"""
npc_clips.py
Her NPC kokundeki Animation component'in clip listesini cikarir.
"Idle + Walk + Run + car in/out" iceren en uygun NPC'yi bulur.
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


env = UnityPy.load(os.path.join(DATA, "level2"))

tr_name, tr_parent, go_of_tr, tr_of_go = {}, {}, {}, {}
for o in env.objects:
    if o.type.name != "Transform":
        continue
    try:
        t = o.read()
        gname = getattr(t.m_GameObject.read(), "m_Name", "")
    except Exception:
        continue
    pid = o.path_id
    tr_name[pid] = gname
    tr_parent[pid] = getattr(t.m_Father, "path_id", 0)
    try:
        tr_of_go[t.m_GameObject.path_id] = pid
        go_of_tr[pid] = t.m_GameObject.path_id
    except Exception:
        pass


def path_of(pid):
    parts, g = [], 0
    while pid in tr_name and g < 128:
        parts.append(tr_name[pid])
        pid = tr_parent.get(pid, 0)
        g += 1
    return "/".join(reversed(parts))


kids = defaultdict(list)
for pid, par in tr_parent.items():
    kids[par].append(pid)


def child_names(pid):
    return sorted(set(tr_name.get(c, "?") for c in kids.get(pid, [])))


# Animation component'leri tara
rows = []
for o in env.objects:
    if o.type.name != "Animation":
        continue
    try:
        a = o.read()
    except Exception:
        continue
    try:
        goname = getattr(a.m_GameObject.read(), "m_Name", "")
    except Exception:
        continue
    pid = tr_of_go.get(o.path_id and a.m_GameObject.path_id)
    if pid is None:
        # path_id'yi dogrudan ara
        pid = None
        for gid, tpid in tr_of_go.items():
            if gid == getattr(a.m_GameObject, "m_PathID", -1):
                pid = tpid
                break
    clips = []
    try:
        for item in (a.m_Animations or []):
            if isinstance(item, (list, tuple)):
                item = item[0]
            c = deref(item)
            if c is not None:
                clips.append(getattr(c, "m_Name", "?"))
    except Exception as e:
        print("anim okunamadi:", e)
    if not clips:
        continue
    rows.append((goname, path_of(pid) if pid else "?", child_names(pid) if pid else [], clips))

print("=" * 100)
print("ANIMATION COMPONENT'I OLAN NPC'LER  (%d adet)" % len(rows))
print("=" * 100)

WANT = ["idle", "stand", "walk", "run", "get_car", "car_in", "car_out", "sit", "drive"]
best = []
for goname, p, ch, clips in sorted(rows, key=lambda r: r[1]):
    low = [c.lower() for c in clips]
    has = [w for w in WANT if any(w in c for c in low)]
    score = len(has)
    # skeleton/bodymesh tasiyanlar NPC'dir
    is_npc = ("skeleton" in [c.lower() for c in ch]) or ("bodymesh" in [c.lower() for c in ch])
    if is_npc and score >= 3:
        best.append((score, goname, p, len(clips), has))
    print("%-14s %-58s clips=%-3d isNPC=%-5s [%s]"
          % (goname[:14], p[-58:], len(clips), is_npc, ",".join(has)[:40]))

print()
print("=" * 100)
print("EN UYGUN NPC ADAYLARI (skeleton/bodymesh + en cok locomotion clip'i)")
print("=" * 100)
for score, goname, p, n, has in sorted(best, reverse=True)[:20]:
    print("  %2d  %-56s clips=%-3d %s" % (score, p[-56:], n, ",".join(has)))

if best:
    print()
    print("=" * 100)
    print("DETAY: en iyi aday")
    print("=" * 100)
    top = sorted(best, reverse=True)[0]
    for goname, p, ch, clips in rows:
        if p == top[1]:
            print("  kok        :", p)
            print("  cocuklar   :", ch)
            print("  clip sayisi:", len(clips))
            print("  clip'ler   :")
            for c in sorted(clips):
                print("      ", c)