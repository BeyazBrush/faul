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

gos, comps_cache = {}, {}
for o in objs:
    if o.type.name != "GameObject":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    k = (o.assets_file.name, o.path_id)
    gos[k] = t.get("m_Name", "")
    comps_cache[k] = t.get("m_Component", [])

def transform_of(gk):
    for cid, ref in comps_cache.get(gk, []):
        if cid == 4:
            return (gk[0], ref.get("m_PathID", 0))
    return None

ch_cache = {}
def children_of(tk):
    if tk in ch_cache:
        return ch_cache[tk]
    tt = tree(tk); out = []
    if tt:
        for c in (tt.get("m_Children") or []):
            ct = tree(resolve(tk[0], c))
            if ct:
                gk = resolve(tk[0], ct.get("m_GameObject"))
                if gk:
                    out.append(gk)
    ch_cache[tk] = out
    return out

anc_cache = {}
def ancestors_of(gk):
    if gk in anc_cache:
        return anc_cache[gk]
    tk = transform_of(gk); out = []; cur = None
    if tk:
        tt = tree(tk)
        cur = resolve(tk[0], tt.get("m_Father")) if tt else None
    guard = 0
    while cur and guard < 80:
        guard += 1
        ct = tree(cur)
        if not ct:
            break
        g2 = resolve(cur[0], ct.get("m_GameObject"))
        if g2:
            out.append(g2)
        cur = resolve(cur[0], ct.get("m_Father"))
    anc_cache[gk] = out
    return out

sub_cache = {}
def subtree(gk, depth=0):
    if gk in sub_cache:
        return sub_cache[gk]
    acc = [gk]
    if depth < 7:
        tk = transform_of(gk)
        if tk:
            for ck in children_of(tk):
                acc.extend(subtree(ck, depth + 1))
    sub_cache[gk] = acc
    return acc

classid_of = {}
for o in objs:
    classid_of[(o.assets_file.name, o.path_id)] = o.type.name

LIMBS = ("knee", "ankle", "shoulder", "hand_", "thigh", "thig_", "collar", "finger")

def evaluate(gk):
    sub = subtree(gk)
    names = [gos.get(x, "") for x in sub]
    low = [n.lower() for n in names]
    def bone(k): return any(k in n for n in low)
    hips = bone("hips") or bone("pelvis")
    spine = bone("spine") or bone("chest") or bone("upperbody")
    head = bone("head")
    limbs = sum(1 for k in LIMBS if bone(k))
    skin = any(classid_of.get(x) == "SkinnedMeshRenderer" for x in sub)
    anim = any(classid_of.get(x) in ("Animation", "Animator") for x in sub)
    return hips, spine, head, limbs, skin, anim

def humanoid(gk):
    hips, spine, head, limbs, skin, anim = evaluate(gk)
    return hips and (head or spine) and limbs >= 2 and skin and anim

print("nesne:", len(gos))
accepted = [gk for gk in gos if humanoid(gk)]
acc_set = set(accepted)
print("humanoid():", len(accepted))

# KAPSayici ELEME: C'yi dusur, kabul edilen bir D, C'nin ALTINDAYSA
drop = set()
for d in accepted:
    for a in ancestors_of(d):
        drop.add(a)
final = [gk for gk in accepted if gk not in drop]
print("kapsayici elenen:", len(drop & acc_set))
print("KALAN:", len(final))

VEH = ("boat", "car", "truck", "tractor", "trailer", "plane", "fork", "crane",
       "sail", "engine", "vehicle", "bus", "bike", "trains", "wagon", "caravan")
final_veh = [gos.get(g, "") for g in final if any(w in gos.get(g, "").lower() for w in VEH)]
print("\nkalan icinde arac/teknе adli: %d -> %s" % (len(final_veh), sorted(set(final_veh))))

print("\n===== KALAN TAM LISTE (%d) =====" % len(final))
rows = []
for gk in final:
    n = gos.get(gk, "")
    h, s, hd, lm, sk, an = evaluate(gk)
    root = ancestors_of(gk)[-1] if ancestors_of(gk) else "?"
    rows.append((n, gos.get(root, "?"), lm, h, s, hd, sk, an))
for r in sorted(rows):
    print("  %-26s kok=%-22s limbs=%d hips=%s spine=%s head=%s skin=%s anim=%s" % r)

print("\n===== 6 GEZGIN KONTROLU =====")
for w in ["Alpo", "Julli", "Kale", "Kristian", "Rauno", "Unto"]:
    hits = [g for g in gos if gos.get(g, "") == w]
    inf = any(g in final for g in hits)
    print("  %-10s nesne=%d  nihai_liste=%s" % (w, len(hits), inf))