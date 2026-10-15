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
print("Mesh:", len(mesh_name), " Material:", len(mat_name))

# Her SkinnedMeshRenderer icin (mesh, materyaller)
def skin_info(gk):
    out = []
    for x in subtree(gk):
        for cid, ref in comps.get(x, []):
            if cid != 137:      # 137 = SkinnedMeshRenderer
                continue
            st = tree((x[0], ref.get("m_PathID", 0)))
            if not st:
                continue
            mf, mp = ptr(st.get("m_Mesh"))
            mats = []
            mm = st.get("m_Materials") or []
            for m in mm:
                f, p = ptr(m)
                mats.append(mat_name.get(resolve(x[0], m), "?"))
            out.append((gos.get(x, ""), mesh_name.get(resolve(x[0], st.get("m_Mesh")), "?"), mats))
    return out

targets = ["Cousin", "Teimo", "VICTRO", "Char", "Man", "Neighbour", "Drummer", "Julli"]
for nm in targets:
    ks = [k for k in gos if gos.get(k) == nm]
    if not ks:
        continue
    print("\n--- %s (%d adet) ---" % (nm, len(ks)))
    seen = set()
    for gk in ks[:3]:
        for go, me, mats in skin_info(gk):
            key = (go, me, tuple(mats))
            if key in seen:
                continue
            seen.add(key)
            print("   %-12s mesh=%-26s mat=%s" % (go, me, mats))

# PLAYER'in mesh/materyalleri
print("\n" + "=" * 74)
print("PLAYER mesh + materyal")
print("=" * 74)
for gk in [k for k in gos if gos.get(k) == "PLAYER"]:
    for go, me, mats in skin_info(gk):
        print("   %-16s mesh=%-30s mat=%s" % (go, me, mats))