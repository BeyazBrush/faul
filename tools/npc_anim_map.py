# -*- coding: utf-8 -*-
"""
npc_anim_map.py
Verilen NPC altindaki TUM Animation component'lerini + clip adlarini dondurur.
MSC'de legacy animasyon kkemik basina bolunmus: skeleton(bacaklar),
collar_left/collar_right(kollar), spine_upper(govde) ayni anda oynatilir.
"""
import UnityPy, os, sys
from collections import defaultdict

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
TARGET = sys.argv[1] if len(sys.argv) > 1 else "NPC_CARS/BUS/Latanen"


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


env = UnityPy.load(os.path.join(DATA, "level2"))

tr_name, tr_parent, tr_of_go = {}, {}, {}
for o in env.objects:
    if o.type.name != "Transform":
        continue
    try:
        t = o.read()
        gname = getattr(t.m_GameObject.read(), "m_Name", "")
    except Exception:
        continue
    tr_name[o.path_id] = gname
    tr_parent[o.path_id] = getattr(t.m_Father, "path_id", 0)
    try:
        tr_of_go[t.m_GameObject.path_id] = o.path_id
    except Exception:
        pass

kids = defaultdict(list)
for pid, par in tr_parent.items():
    kids[par].append(pid)


def path_of(pid):
    parts, g = [], 0
    while pid in tr_name and g < 128:
        parts.append(tr_name[pid])
        pid = tr_parent.get(pid, 0)
        g += 1
    return "/".join(reversed(parts))


# hedefi bul
root = None
for pid in tr_name:
    if path_of(pid) == TARGET:
        root = pid
        break
if root is None:
    print("BULUNAMADI:", TARGET)
    hit = [path_of(p) for p in tr_name if TARGET.split("/")[-1] == tr_name.get(p)]
    for h in sorted(set(hit))[:20]:
        print("   ", h)
    raise SystemExit(1)

subtree = set()
stack = [root]
while stack:
    p = stack.pop()
    subtree.add(p)
    stack.extend(kids.get(p, []))

print("=" * 96)
print("NPC:", TARGET)
print("=" * 96)
total = 0
rows = []
for o in env.objects:
    if o.type.name != "Animation":
        continue
    try:
        a = o.read()
    except Exception:
        continue
    pid = tr_of_go.get(getattr(a.m_GameObject, "m_PathID", -1))
    if pid not in subtree:
        continue
    clips = []
    for item in (getattr(a, "m_Animations", None) or []):
        if isinstance(item, (list, tuple)):
            item = item[0]
        c = deref(item)
        if c is not None:
            clips.append(getattr(c, "m_Name", "?"))
    if not clips:
        continue
    total += len(clips)
    rel = path_of(pid)[len(TARGET):].lstrip("/") or "(kok)"
    rows.append((rel, clips, getattr(a, "m_CullingMode", None),
                 getattr(a, "m_PlayAutomatically", None), getattr(a, "m_WrapMode", None)))

for rel, clips, cm, pa, wm in sorted(rows):
    print("\n  [%s]  clips=%d  culling=%s autoPlay=%s wrap=%s" % (rel, len(clips), cm, pa, wm))
    for c in sorted(clips):
        print("      -", c)

print()
print("=" * 96)
print("TOPLAM clip referansi:", total, " / Animation component:", len(rows))