import sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(DATA)
objs = list(env.objects)
by_key = {}
for o in objs:
    by_key.setdefault((o.assets_file.name, o.path_id), o)

ext = {}
for af in set(o.assets_file for o in objs):
    try:
        for i, e in enumerate(getattr(af, "externals", []) or []):
            ext[(af.name, i + 1)] = getattr(e, "path", None) or getattr(e, "name", None)
    except Exception:
        pass
known = set(o.assets_file.name for o in objs)

def resolve(fname, v):
    if isinstance(v, dict):
        fid, pid = v.get("m_FileID", 0), v.get("m_PathID", 0)
    elif isinstance(v, (list, tuple)) and len(v) >= 2 and isinstance(v[1], dict):
        fid, pid = v[0], v[1].get("m_PathID", 0)
    else:
        return None
    if fid == 0:
        return (fname, pid)
    t = ext.get((fname, fid))
    if t is None:
        return ("?" + str(fid), pid)
    b = os.path.basename(str(t))
    return (b if b in known else t, pid)

def tree(k):
    o = by_key.get(k)
    if o is None:
        return None
    try:
        return o.read_typetree()
    except Exception:
        return None

gos, gtrees, comps_cache = {}, {}, {}
for o in objs:
    if o.type.name != "GameObject":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    k = (o.assets_file.name, o.path_id)
    gtrees[k] = t
    gos[k] = t.get("m_Name", "")
    comps_cache[k] = t.get("m_Component", [])

def transform_of(gk):
    for cid, ref in comps_cache.get(gk, []):
        if cid == 4:
            return (gk[0], ref.get("m_PathID", 0))
    return None

children_cache = {}
def children_of(tk):
    if tk in children_cache:
        return children_cache[tk]
    tt = tree(tk)
    out = []
    if tt:
        for c in (tt.get("m_Children") or []):
            ct = tree(resolve(tk[0], c))
            if ct:
                gk = resolve(tk[0], ct.get("m_GameObject"))
                if gk:
                    out.append(gk)
    children_cache[tk] = out
    return out

ancestor_cache = {}
def ancestors_of(gk):
    if gk in ancestor_cache:
        return ancestor_cache[gk]
    tk = transform_of(gk)
    out = []
    if tk:
        tt = tree(tk)
        cur = resolve(tk[0], tt.get("m_Father")) if tt else None
        guard = 0
        while cur and guard < 80:
            guard += 1
            ct = tree(cur)
            if not ct:
                break
            gk2 = resolve(cur[0], ct.get("m_GameObject"))
            if gk2:
                out.append(gk2)
            cur = resolve(cur[0], ct.get("m_Father"))
    ancestor_cache[gk] = out
    return out

subtree_cache = {}
def subtree(gk, depth=0):
    if gk in subtree_cache:
        return subtree_cache[gk]
    acc = [gos.get(gk, "")]
    if depth < 7:
        tk = transform_of(gk)
        if tk:
            for ck in children_of(tk):
                acc.extend(subtree(ck, depth + 1))
    subtree_cache[gk] = acc
    return acc

# ---- olcutler ----
def has_bone(names, key):
    return any(key in n.lower() for n in names if n)

LIMBS = ("knee", "ankle", "shoulder", "hand_", "thigh", "thig_", "collar", "finger")

def loose(names):            # simdiki C# olcutu
    hips = has_bone(names, "hips") or has_bone(names, "pelvis")
    head = has_bone(names, "head")
    spine = has_bone(names, "spine") or has_bone(names, "chest") or has_bone(names, "upperbody")
    return (hips and head) or (hips and spine)

def strict(names):           # yeni olcut
    hips = has_bone(names, "hips") or has_bone(names, "pelvis")
    head = has_bone(names, "head")
    spine = has_bone(names, "spine") or has_bone(names, "chest") or has_bone(names, "upperbody")
    limbs = sum(1 for k in LIMBS if has_bone(names, k))
    return hips and (head or spine) and limbs >= 2

def strict_no_ragdoll(names):
    """RagDoll altindaki kemikleri SAYMA - sadece canli iskelet."""
    hips = has_bone(names, "hips") or has_bone(names, "pelvis")
    head = has_bone(names, "head")
    spine = has_bone(names, "spine") or has_bone(names, "chest") or has_bone(names, "upperbody")
    limbs = sum(1 for k in LIMBS if has_bone(names, k))
    return hips and spine and limbs >= 3

VEH = ("boat", "car", "truck", "tractor", "trailer", "plane", "fork", "crane",
       "sail", "engine", "vehicle", "bus", "bike", "trains", "wagon", "caravan")

def is_veh(n):
    return any(w in n.lower() for w in VEH)

print("nesne sayisi:", len(gos))
for label, fn in (("LOOSE (mevcut C#)", loose), ("STRICT (yeni)", strict)):
    passed = []
    for gk in gos:
        n = gos.get(gk, "")
        if fn(subtree(gk)):
            passed.append(gk)
    veh = [gos[g] for g in passed if is_veh(gos.get(g, ""))]
    print("\n%-22s gecen: %5d   arac/teknе adli: %d" % (label, len(passed), len(veh)))
    if veh:
        print("      " + ", ".join(sorted(set(veh))[:18]))

# ---- konteyner elemesi: kabul edilenin ATASI olan adaylari ele ----
print("\n" + "=" * 74)
print("STRICT + konteyner elemeli sonuc")
print("=" * 74)
accepted = []
for gk in gos:
    if strict(subtree(gk)):
        accepted.append(gk)
acc_set = set(accepted)
dropped = [gk for gk in accepted if any(a in acc_set for a in ancestors_of(gk))]
final = [gk for gk in accepted if gk not in set(dropped)]
print("strict gecen         : %d" % len(accepted))
print("ic ice gecis (kapsayici, elendi): %d" % len(dropped))
print("kalan                : %d" % len(final))
fv = [gos[g] for g in final if is_veh(gos.get(g, ""))]
print("kalan icinde arac/teknе: %d  %s" % (len(fv), sorted(set(fv))[:12]))
print("\nKALANLISTE (alfabetik ilk 70):")
for n in sorted(set(gos.get(g, "") for g in final))[:70]:
    print("   " + n)

# yürüyüş NPC'leri geçiyor mu?
walkers = ["Alpo", "Julli", "Kale", "Kristian", "Rauno", "Unto"]
print("\n6 gezginin hepsi strict gecen mi:")
for w in walkers:
    hit = [g for g in gos if gos.get(g, "") == w]
    ok = any(strict(subtree(h)) for h in hit)
    print("   %-10s nesne=%d  strict=%s" % (w, len(hit), ok))