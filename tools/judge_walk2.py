import sys, os, json
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
exec(open(r'D:\modmscMP\MSCSteamMP\tools\judge_humanoid4.py').read().split('VEH =')[0])

meshname, clipname = {}, {}
for o in objs:
    if o.type.name == "Mesh":
        try: meshname[(o.assets_file.name, o.path_id)] = o.read_typetree().get("m_Name", "")
        except Exception: pass
    elif o.type.name == "AnimationClip":
        try: clipname[(o.assets_file.name, o.path_id)] = o.read_typetree().get("m_Name", "")
        except Exception: pass
print("Mesh: %d  AnimationClip: %d" % (len(meshname), len(clipname)))

LIMBS = ("knee", "ankle", "shoulder", "hand_", "thigh", "thig_", "collar", "finger")

def comp_types(gk):
    out = []
    for cid, ref in comps.get(gk, []):
        o = by_key.get((gk[0], ref.get("m_PathID", 0)))
        out.append(o.type.name if o else "?")
    return out

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

def count_bodymesh(gk):
    n = 0
    for x in subtree(gk):
        for cid, ref in comps.get(x, []):
            o = by_key.get((x[0], ref.get("m_PathID", 0)))
            if o is not None and o.type.name == "SkinnedMeshRenderer":
                st = tree((x[0], ref.get("m_PathID", 0)))
                if st and meshname.get(resolve(x[0], st.get("m_Mesh")), "") == "bodymesh":
                    n += 1
    return n

def clips_of(gk):
    total, mv = 0, []
    for x in subtree(gk):
        for cid, ref in comps.get(x, []):
            o = by_key.get((x[0], ref.get("m_PathID", 0)))
            if o is None or o.type.name != "Animation":
                continue
            owner = gos.get(x, "")
            if owner in ("NavigationAI", "Ray", "NavigationDrag", "Car", "Vehicle"):
                pass
            at = tree((x[0], ref.get("m_PathID", 0))) or {}
            for a in (at.get("m_Animations") or []):
                nm = clipname.get(resolve(x[0], a), "")
                if not nm:
                    continue
                total += 1
                l = nm.lower()
                if "walk" in l or "run" in l or "move" in l:
                    mv.append(nm)
    return total, mv

def ok(gk):
    h, s, hd, lm, ct = evaluate(gk)
    if not (h and (hd or s) and lm >= 2):
        return False
    if "SkinnedMeshRenderer" not in ct:
        return False
    return ("Animation" in ct) or ("Animator" in ct)

VEH = ("boat","car","truck","tractor","trailer","plane","fork","crane","sail",
       "engine","vehicle","bus","bike","trains","wagon","caravan")
def veh(n): return any(w in n.lower() for w in VEH)

acc = [gk for gk in gos if not veh(gos.get(gk, "")) and ok(gk)]
drop = set()
for d in acc:
    for a in ancestors_of(d):
        drop.add(a)
final = [gk for gk in acc if gk not in drop]
one = [gk for gk in final if count_bodymesh(gk) == 1]
print("\nkapsayici elemesi sonrasi: %d" % len(final))
print("  + bodymesh sayisi == 1  : %d" % len(one))

for label, pool in (("TUM nihai", final), ("bodymesh==1", one)):
    good, none_ = 0, 0
    samples = []
    for gk in pool:
        tot, mv = clips_of(gk)
        if mv:
            good += 1
            if len(samples) < 14:
                samples.append((gos.get(gk, ""), tot, sorted(set(mv))[:5]))
        else:
            none_ += 1
    print("\n--- %s : yurume klipi OLAN %d / YOK %d ---" % (label, good, none_))
    for n, t, ex in samples:
        print("   %-18s klip=%2d  %s" % (n, t, ", ".join(ex)))

print("\n=== Amikset ===")
for gk in [k for k in gos if gos.get(k) == "Amikset"]:
    tot, mv = clips_of(gk)
    print("  bodymesh=%d klip=%d yurume=%s" % (count_bodymesh(gk), tot, sorted(set(mv))[:8] or "YOK"))