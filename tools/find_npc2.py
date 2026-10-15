import sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(DATA)
objs = list(env.objects)

# pathID her dosyada 1'den baslar -> ANAHTAR (dosya, pathID) olmali
by_key = {}
for o in objs:
    by_key.setdefault((o.assets_file.name, o.path_id), o)

script_name = {}
for o in objs:
    if o.type.name != "MonoScript":
        continue
    try:
        t = o.read_typetree()
        script_name[(o.assets_file.name, o.path_id)] = (
            t.get("m_ClassName", "") or t.get("m_Name", ""))
    except Exception:
        pass

gos, gtrees = {}, {}
for o in objs:
    if o.type.name != "GameObject":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    gtrees[(o.assets_file.name, o.path_id)] = t
    gos[(o.assets_file.name, o.path_id)] = t.get("m_Name", "")

def tree(k):
    o = by_key.get(k)
    if o is None:
        return None
    try:
        return o.read_typetree()
    except Exception:
        return None

def ref_ptr(v):
    """(fileID, pathID) dondurur."""
    if isinstance(v, dict):
        return (v.get("m_FileID", 0), v.get("m_PathID", 0))
    if isinstance(v, (list, tuple)) and len(v) >= 2 and isinstance(v[1], dict):
        return (v[0], v[1].get("m_PathID", 0))
    return (0, 0)

FILEMAP = {}
for fn in os.listdir(DATA):
    FILEMAP[fn] = fn
def resolve(fname, file_id, path_id):
    """fileID 0 = ayni dosya; >0 = externalFiles sirasi."""
    if file_id == 0:
        return (fname, path_id)
    return ("?" + str(file_id), path_id)

def mono_label(fname, cpid):
    t = tree((fname, cpid)) or {}
    f, p = ref_ptr(t.get("m_Script"))
    key = resolve(fname, f, p)
    cls = script_name.get(key, "")
    if not cls and f == 0:
        cls = script_name.get((fname, p), "")
    return cls or ("MonoBehaviour#" + str(cpid))

def comps(gk):
    t = gtrees.get(gk)
    if not t:
        return []
    return t.get("m_Component", [])

def find_gos(name):
    return [k for k, n in gos.items() if n.strip().lower() == name.strip().lower()]

def transform_of(gk):
    fname = gk[0]
    for cid, ref in comps(gk):
        if cid == 4:
            return (fname, ref.get("m_PathID", 0))
    return None

def children_of(tk):
    tt = tree(tk)
    if not tt:
        return []
    out = []
    ch = tt.get("m_Children") or []
    for c in ch:
        f, p = ref_ptr(c)
        ctree = tree(resolve(tk[0], f, p))
        if ctree:
            gid = ref_ptr(ctree.get("m_GameObject"))
            out.append(resolve(tk[0], *gid))
    return out

def walk(gk, depth=0, maxdepth=3, stats=None):
    tk = transform_of(gk)
    indent = "   " + "  " * depth
    labels = []
    for cid, ref in comps(gk):
        cpid = ref.get("m_PathID", 0)
        if cid == 114:
            labels.append(mono_label(gk[0], cpid))
        elif cid == 4:
            labels.append("Transform")
        else:
            o = by_key.get((gk[0], cpid))
            labels.append(o.type.name if o else "?")
    print("%s- %-26s [%s]" % (indent, gos[gk], ", ".join(labels)))
    if stats is not None:
        stats.append(gos[gk])
        stats.extend(labels)
    if depth >= maxdepth:
        return
    if tk is None:
        return
    for ck in children_of(tk):
        walk(ck, depth + 1, maxdepth, stats)

print("=" * 74)
print("JULLI ALT AGACI")
print("=" * 74)
for gk in find_gos(sys.argv[1] if len(sys.argv) > 1 else "Julli"):
    stats = []
    walk(gk, 0, 4, stats)

print("\n" + "=" * 74)
print("BONE ADLARI (Julli alt agacinda gecen 'hips/pelvis/spine/head/bone')")
print("=" * 74)
for gk in find_gos(sys.argv[1] if len(sys.argv) > 1 else "Julli"):
    st = []
    walk(gk, 0, 5, st)
    low = [x for x in st if any(k in x.lower() for k in
            ("hips", "pelvis", "spine", "head", "bone_", "shoulder", "hand_", "ankle"))]
    print("  " + (", ".join(sorted(set(low))) if low else "(HUMAN BONE ADI YOK)"))