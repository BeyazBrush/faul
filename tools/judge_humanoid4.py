import sys, os
sys.stdout.reclare = None
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

gos, comps, ctype = {}, {}, {}
for o in objs:
    ctype[(o.assets_file.name, o.path_id)] = o.type.name
    if o.type.name != "GameObject":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    k = (o.assets_file.name, o.path_id)
    gos[k] = t.get("m_Name", "")
    comps[k] = t.get("m_Component", [])

def transform_of(gk):
    for cid, ref in comps.get(gk, []):
        if cid == 4:
            return (gk[0], ref.get("m_PathID", 0))
    return None

ch = {}
def children_of(tk):
    if tk in ch:
        return ch[tk]
    tt = tree(tk); out = []
    if tt:
        for c in (tt.get("m_Children") or []):
            ct = tree(resolve(tk[0], c))
            if ct:
                gk = resolve(tk[0], ct.get("m_GameObject"))
                if gk:
                    out.append(gk)
    ch[tk] = out
    return out

anc = {}
def ancestors_of(gk):
    if gk in anc:
        return anc[gk]
    tk = transform_of(gk); out = []; cur = None
    if tk:
        tt = tree(tk)
        cur = resolve(tk[0], tt.get("m_Father")) if tt else None
    g = 0
    while cur and g < 80:
        g += 1
        ct = tree(cur)
        if not ct:
            break
        g2 = resolve(cur[0], ct.get("m_GameObject"))
        if g2:
            out.append(g2)
        cur = resolve(cur[0], ct.get("m_Father"))
    anc[gk] = out
    return out

sc = {}
def subtree(gk, d=0):
    if gk in sc:
        return sc[gk]
    acc = [gk]
    if d < 7:
        tk = transform_of(gk)
        if tk:
            for c in children_of(tk):
                acc.extend(subtree(c, d + 1))
    sc[gk] = acc
    return acc

def comp_types(gk):
    out = []
    for cid, ref in comps.get(gk, []):
        o = by_key.get((gk[0], ref.get("m_PathID", 0)))
        out.append(o.type.name if o else "?")
    return out

LIMBS = ("knee", "ankle", "shoulder", "hand_", "thigh", "thig_", "collar", "finger")

def evaluate(gk):
    sub = subtree(gk)
    low = [gos.get(x, "").lower() for x in sub]
    def b(k): return any(k in n for n in low)
    hips = b("hips") or b("pelvis")
    spine = b("spine") or b("chest") or b("upperbody")
    head = b("head")
    limbs = sum(1 for k in LIMBS if b(k))
    ct = []
    for x in sub:
        ct.extend(comp_types(x))
    return hips, spine, head, limbs, ct

def humanoid(gk, reject_wheels):
    hips, spine, head, limbs, ct = evaluate(gk)
    if not (hips and (head or spine) and limbs >= 2):
        return False
    if "SkinnedMeshRenderer" not in ct:
        return False
    if not (("Animation" in ct) or ("Animator" in ct)):
        return False
    if reject_wheels and "WheelCollider" in ct:
        return False      # INSANIN ICINDE TEKERLEK COLLIDER'I OLMAZ
    return True

VEH = ("boat", "car", "truck", "tractor", "trailer", "plane", "fork", "crane",
       "sail", "engine", "vehicle", "bus", "bike", "trains", "wagon", "caravan")

def veh_name(n):
    return any(w in n.lower() for w in VEH)

for reject in (False, True):
    acc = [gk for gk in gos
           if not veh_name(gos.get(gk, "")) and humanoid(gk, reject)]
    aset = set(acc)
    drop = set()
    for d in acc:
        for a in ancestors_of(d):
            drop.add(a)
    final = [gk for gk in acc if gk not in drop]
    print("\n### WheelCollider reddi=%s : ham=%d  nihai=%d" % (reject, len(acc), len(final)))
    print("    nihai icinde arac adli:",
          sorted(set(gos.get(g, "") for g in final if veh_name(gos.get(g, "")))))
    print("    ilk 30:", ", ".join(sorted(set(gos.get(g, "") for g in final))[:30]))

# oyuncu govdesi
print("\n" + "=" * 74)
print("OYUNCU / SURUCU GOVDE ARIAMASI")
print("=" * 74)
for kw in ("player", "driver", "man", "body"):
    hits = sorted(set(gos.get(g, "") for g in gos
                      if kw in gos.get(g, "").lower()))
    print("\n'%s' iceren adlar (%d): %s" % (kw, len(hits), ", ".join(hits[:40])))