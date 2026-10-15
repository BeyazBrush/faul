import sys, os, collections
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
exec(open(r'D:\modmscMP\MSCSteamMP\tools\judge_humanoid4.py').read().split('VEH =')[0])

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

meshname = {}
for o in objs:
    if o.type.name == "Mesh":
        try: meshname[(o.assets_file.name, o.path_id)] = o.read_typetree().get("m_Name", "")
        except Exception: pass

clipname = {}
for o in objs:
    if o.type.name == "AnimationClip":
        try: clipname[(o.assets_file.name, o.path_id)] = o.read_typetree().get("m_Name", "")
        except Exception: pass

def clips_of(gk):
    """(toplam klip, walk/run adlari)"""
    total, mv = 0, []
    for x in subtree(gk):
        for cid, ref in comps.get(x, []):
            o = by_key.get((x[0], ref.get("m_PathID", 0)))
            if o is None or o.type.name != "Animation":
                continue
            at = tree((x[0], ref.get("m_PathID", 0))) or {}
            for a in (at.get("m_Animations") or []):
                nm = clipname.get(resolve(x[0], a.get("m_Clip")), "")
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

print("=== bodymesh SAYISI ile kapsayici elemesi ===")
print("strict gecen: %d" % len(acc))
one = [gk for gk in acc if count_bodymesh(gk) == 1]
print("bodymesh sayisi == 1 olan: %d" % len(one))
multi = [gk for gk in acc if count_bodymesh(gk) > 1]
print("bodymesh sayisi > 1 olan: %d  (bunlar konteyner)" % len(multi))
print("  ornekler:", ", ".join(sorted(set(gos.get(g, "") for g in multi))[:25]))

# kesin aday: 1 bodymesh + kapsayici degil
final1 = [gk for gk in one if gk not in drop]
print("\n=== nihai (bodymesh==1 VE kapsayici degil): %d ===" % len(final1))

print("\n=== yurume/kosma klipi olan adaylar ===")
good = []
for gk in final1:
    tot, mv = clips_of(gk)
    if mv:
        good.append((gos.get(gk, ""), len(mv), sorted(set(mv))[:6]))
print("yurume klipi olan: %d / %d" % (len(good), len(final1)))
for n, c, ex in sorted(good)[:30]:
    print("   %-16s %2d klip  %s" % (n, c, ", ".join(ex)))

print("\n=== Amikset kontrolu ===")
for gk in [k for k in gos if gos.get(k) == "Amikset"]:
    tot, mv = clips_of(gk)
    print("  bodymesh=%d  klip=%d  yurume=%s" % (count_bodymesh(gk), tot, mv[:6] or "YOK"))