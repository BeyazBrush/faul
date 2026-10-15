import sys, os, collections
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
exec(open(r'D:\modmscMP\MSCSteamMP\tools\judge_humanoid4.py').read().split('VEH =')[0])

def ptr(v):
    if isinstance(v, dict):
        return (v.get("m_FileID", 0), v.get("m_PathID", 0))
    if isinstance(v, (list, tuple)) and len(v) >= 2 and isinstance(v[1], dict):
        return (v[0], v[1].get("m_PathID", 0))
    return (0, 0)

mesh_name, mat_name = {}, {}
for o in objs:
    if o.type.name == "Mesh":
        try: mesh_name[(o.assets_file.name, o.path_id)] = o.read_typetree().get("m_Name", "")
        except Exception: pass
    elif o.type.name == "Material":
        try: mat_name[(o.assets_file.name, o.path_id)] = o.read_typetree().get("m_Name", "")
        except Exception: pass

def node_info(gk):
    """(ad, component turleri, MeshFilter meshi, MeshRenderer materyalleri)"""
    ct, mf, mr, skin = [], "?", [], "?"
    for cid, ref in comps.get(gk, []):
        t = tree((gk[0], ref.get("m_PathID", 0)))
        if t is None:
            continue
        if "m_Mesh" in t:
            mf = mesh_name.get(resolve(gk[0], t["m_Mesh"]), "?")
        if "m_Materials" in t:
            ms = [mat_name.get(resolve(gk[0], m), "?") for m in (t.get("m_Materials") or [])]
            o = by_key.get((gk[0], ref.get("m_PathID", 0)))
            if o is not None and o.type.name == "SkinnedMeshRenderer":
                skin = ms
            else:
                mr = ms
        for c2, r2 in comps.get(gk, []):
            pass
        o = by_key.get((gk[0], ref.get("m_PathID", 0)))
        ct.append(o.type.name if o else "?")
    return ct, mf, mr, skin

def comp_names(gk):
    out = []
    for cid, ref in comps.get(gk, []):
        o = by_key.get((gk[0], ref.get("m_PathID", 0)))
        out.append(o.type.name if o else "?")
    return out

print("=" * 74)
print("PLAYER ALT AGACI: mesh + materyal tasiyor olan dugumler")
print("=" * 74)
for gk in [k for k in gos if gos.get(k) == "PLAYER"]:
    for x in subtree(gk):
        ct = comp_names(x)
        if not any(t in ("MeshRenderer", "SkinnedMeshRenderer") for t in ct):
            continue
        t = tree(x)
        mf = skin = mr = "?"
        for cid, ref in comps.get(x, []):
            st = tree((x[0], ref.get("m_PathID", 0)))
            if not st:
                continue
            if "m_Mesh" in st:
                mf = mesh_name.get(resolve(x[0], st["m_Mesh"]), "?")
            if "m_Materials" in st:
                ms = [mat_name.get(resolve(x[0], m), "?") for m in (st.get("m_Materials") or [])]
                o = by_key.get((x[0], ref.get("m_PathID", 0)))
                if o is not None and o.type.name == "SkinnedMeshRenderer":
                    skin = ms
                else:
                    mr = ms
        if mf == "?" and skin == "?" and mr == "?":
            continue
        print("   %-18s mesh=%-22s rend=%s skin=%s" % (gos.get(x, ""), mf, mr, skin))

print("\n" + "=" * 74)
print("char_* MATERYAL KUMESI")
print("=" * 74)
groups = collections.defaultdict(list)
for (f, p), n in mat_name.items():
    if n.startswith("char_"):
        key = n.split("_")[1] if "_" in n else "?"
        groups[key].append(n)
for k in sorted(groups):
    v = sorted(set(groups[k]))
    print("  %-8s (%2d): %s" % (k, len(v), ", ".join(v)))

print("\n" + "=" * 74)
print("bodymesh MESH'INI KULLANAN TUM NPC'LER (ad -> materyaller)")
print("=" * 74)
rows = []
for gk in gos:
    for x in subtree(gk):
        for cid, ref in comps.get(x, []):
            o = by_key.get((x[0], ref.get("m_PathID", 0)))
            if o is None or o.type.name != "SkinnedMeshRenderer":
                continue
            st = tree((x[0], ref.get("m_PathID", 0)))
            if not st:
                continue
            if mesh_name.get(resolve(x[0], st.get("m_Mesh")), "") != "bodymesh":
                continue
            ms = [mat_name.get(resolve(x[0], m), "?") for m in (st.get("m_Materials") or [])]
            rows.append((gos.get(gk, ""), tuple(ms)))
seen = set()
uniq = []
for n, ms in sorted(rows):
    if (n, ms) in seen:
        continue
    seen.add((n, ms))
    uniq.append((n, ms))
print("benzersiz (ad, materyal) : %d" % len(uniq))
for n, ms in uniq[:70]:
    print("   %-20s %s" % (n, list(ms)))