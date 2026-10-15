import sys, os, re
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(DATA)
objs = list(env.objects)

by_key = {}
for o in objs:
    by_key.setdefault((o.assets_file.name, o.path_id), o)

# externalFiles cozumlemesi -> gercek dosya adi
ext = {}
for af in set(o.assets_file for o in objs):
    try:
        for i, e in enumerate(getattr(af, "externals", []) or []):
            ext[(af.name, i + 1)] = getattr(e, "path", None) or getattr(e, "name", None)
    except Exception:
        pass

def resolve(fname, v):
    if isinstance(v, dict):
        fid, pid = v.get("m_FileID", 0), v.get("m_PathID", 0)
    elif isinstance(v, (list, tuple)) and len(v) >= 2 and isinstance(v[1], dict):
        fid, pid = v[0], v[1].get("m_PathID", 0)
    else:
        return None
    if fid == 0:
        return (fname, pid)
    target = ext.get((fname, fid))
    if target is None:
        return ("?" + str(fid), pid)
    base = os.path.basename(str(target))
    return (base if base in ext_fallback else target, pid)

ext_fallback = set()
for o in objs:
    ext_fallback.add(o.assets_file.name)

script_name = {}
for o in objs:
    if o.type.name != "MonoScript":
        continue
    try:
        t = o.read_typetree()
        script_name[(o.assets_file.name, o.path_id)] = (t.get("m_ClassName", "") or t.get("m_Name", ""))
    except Exception:
        pass

def tree(k):
    o = by_key.get(k)
    if o is None:
        return None
    try:
        return o.read_typetree()
    except Exception:
        return None

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

# MonoBehaviour sinif adi (filenames duzeltilmis cozumleme)
def mono_label(fname, cpid):
    t = tree((fname, cpid)) or {}
    k = resolve(fname, t.get("m_Script"))
    cls = script_name.get(k, "") if k else ""
    if not cls:
        cls = script_name.get((fname, ref_pid(t.get("m_Script"))), "")
    return cls or ("MB#" + str(cpid))

def ref_pid(v):
    if isinstance(v, dict):
        return v.get("m_PathID", 0)
    if isinstance(v, (list, tuple)) and len(v) >= 2 and isinstance(v[1], dict):
        return v[1].get("m_PathID", 0)
    return 0

def find_gos(name):
    return [k for k, n in gos.items() if n.strip().lower() == name.strip().lower()]

def transform_of(gk):
    t = gtrees.get(gk)
    if not t:
        return None
    for cid, ref in t.get("m_Component", []):
        if cid == 4:
            return (gk[0], ref.get("m_PathID", 0))
    return None

def children_of(tk):
    tt = tree(tk)
    if not tt:
        return []
    out = []
    for c in (tt.get("m_Children") or []):
        ct = tree(resolve(tk[0], c))
        if ct:
            out.append(resolve(tk[0], ct.get("m_GameObject")))
    return out

# ---------------- 1) Walkers altindaki tum gezginler ----------------
print("=" * 74)
print("1) HUMANS/Randomizer/Walkers ALTINDAKI TUM GEZGINLER")
print("=" * 74)
walkers = None
for k, n in gos.items():
    if n == "Walkers":
        tk = transform_of(k)
        if tk:
            ch = children_of(tk)
            # Walkers'in parenti Randomizer mi?
            pt = tree(tk)
            par = resolve(tk[0], pt.get("m_Father")) if pt else None
            pt2 = tree(par) if par else None
            pname = gos.get(resolve(tk[0], pt2.get("m_GameObject"))) if pt2 else None
            if pname == "Randomizer":
                walkers = ch
                break
if walkers:
    print("   toplam %d gezgin:" % len(walkers))
    for gk in sorted(walkers, key=lambda x: gos.get(x, "")):
        labels = []
        for cid, ref in gtrees[gk].get("m_Component", []):
            if cid == 114:
                labels.append(mono_label(gk[0], ref.get("m_PathID", 0)))
            elif cid == 4:
                labels.append("Transform")
            else:
                o = by_key.get((gk[0], ref.get("m_PathID", 0)))
                labels.append(o.type.name if o else "?")
        print("     %-22s %s" % (gos.get(gk), ", ".join(labels)))
else:
    print("   (bulunamadi)")

# ---------------- 2) MonoBehaviour siniflari (Julli) ----------------
print("\n" + "=" * 74)
print("2) 'Julli' bilesenlerinin GERCEK sinif adlari")
print("=" * 74)
for gk in find_gos("Julli"):
    for cid, ref in gtrees[gk].get("m_Component", []):
        cpid = ref.get("m_PathID", 0)
        if cid == 114:
            print("     MonoBehaviour -> " + mono_label(gk[0], cpid))
        else:
            o = by_key.get((gk[0], cpid))
            print("     " + (o.type.name if o else "?"))

# ---------------- 3) IsHumanoid olcutunu TUM sahneye uygula ----------------
print("\n" + "=" * 74)
print("3) IsHumanoid olcutu (pelvis/hips + head VEYA spine) TUM GameObject'lere")
print("=" * 74)

def subtree_names(gk, out, depth=0):
    out.append(gos.get(gk, ""))
    if depth > 6:
        return
    tk = transform_of(gk)
    if not tk:
        return
    for ck in children_of(tk):
        subtree_names(ck, out, depth + 1)

passed = []
for gk in list(gos.keys()):
    names = []
    subtree_names(gk, names)
    low = [x.lower() for x in names if x]
    hips = sum(1 for x in low if "hips" in x or "pelvis" in x)
    head = sum(1 for x in low if "head" in x)
    spine = sum(1 for x in low if ("spine" in x or "chest" in x or "upperbody" in x))
    if (hips > 0 and head > 0) or (hips > 0 and spine > 0):
        passed.append((gos.get(gk), gk))

print("   Gecen GameObject sayisi: %d / %d" % (len(passed), len(gos)))
print("\n   GECENLER (ilk 60):")
for n, k in sorted(passed)[:60]:
    print("     " + n)

bad = [p for p in passed if any(w in p[0].lower() for w in
       ("boat", "car", "truck", "tractor", "trailer", "plane", "fork", "crane", "engine", "vehicle", "sail"))]
print("\n   ARAÇ/TEKNE İÇEREN GEÇENLER (olmamalı):", bad if bad else "(YOK - temiz)")