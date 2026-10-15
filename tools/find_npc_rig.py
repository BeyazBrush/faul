import UnityPy, os
base = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(os.path.join(base, "level2"))

tr = {}
for p in env.objects:
    if p.type.name != "Transform":
        continue
    t = p.read()
    tr[p.path_id] = {"name": getattr(t, "m_Name", ""), "parent": t.m_Father.path_id}

def path_of(pid):
    parts, g = [], 0
    while pid in tr and g < 64:
        parts.append(tr[pid]["name"]); pid = tr[pid]["parent"]; g += 1
    return "/".join(reversed(parts))

hits = 0
for p in env.objects:
    if p.type.name != "SkinnedMeshRenderer":
        continue
    s = p.read()
    names = []
    try:
        for b in s.m_Bones:
            bt = b.read()
            names.append(getattr(bt, "m_Name", "?"))
    except Exception:
        pass
    if not names:
        continue
    low = [n.lower() for n in names]
    if ("thig_left" in low) or ("thig_right" in low) or sum(1 for n in low if "skeleton" in n) > 0:
        go = s.m_GameObject.read()
        print("HIT go=%r  bones=%d  root=%s" % (getattr(go, "m_Name", "?"), len(names), names[0]))
        for b in p and []: pass
        try:
            rp = s.m_RootBone.path_id
            print("    path:", path_of(rp))
            print("    bones:", ", ".join(names[:14]))
        except Exception as e:
            print("    ", e)
        hits += 1
        if hits >= 6:
            break
print("total hits", hits)
