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

clip_name = {}
for o in objs:
    if o.type.name == "AnimationClip":
        try:
            clip_name[(o.assets_file.name, o.path_id)] = o.read_typetree().get("m_Name", "")
        except Exception:
            pass

gos, comps = {}, {}
for o in objs:
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

def subtree(gk, d=0):
    acc = [gk]
    if d < 7:
        tk = transform_of(gk)
        if tk:
            for ck in children_of(tk):
                acc.extend(subtree(ck, d + 1))
    return acc

def report(root_name):
    print("=" * 74)
    print("ANIMASYON KATMANLARI: %s" % root_name)
    print("=" * 74)
    for gk in [k for k, n in gos.items() if n == root_name]:
        print("\nalt agac: %s" % " / ".join(sorted(set(gos.get(x, "") for x in subtree(gk)))))
        for x in subtree(gk):
            for cid, ref in comps.get(x, []):
                o = by_key.get((x[0], ref.get("m_PathID", 0)))
                if o is None or o.type.name != "Animation":
                    continue
                at = tree((x[0], ref.get("m_PathID", 0))) or {}
                an = at.get("m_Animations") or []
                print("\n  Animation bileseni GameObject='%s'  klip sayisi=%d"
                      % (gos.get(x, ""), len(an)))
                for a in an:
                    ck = resolve(x[0], a.get("m_Clip"))
                    cn = clip_name.get(ck, "?")
                    print("     katman=%s  ad=%-32s loop=%s" % (
                        a.get("m_Level", "?"), cn, a.get("m_LoopTime", "?")))
        break

for r in (sys.argv[1:] or ["Julli", "AMIS2"]):
    report(r)