import sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
exec(open(r'D:\modmscMP\MSCSteamMP\tools\judge_humanoid4.py').read().split('VEH =')[0])

def skin_names(gk):
    """(GameObject adi, MeshRenderer adi) ciftleri"""
    out = []
    for x in subtree(gk):
        for cid, ref in comps.get(x, []):
            o = by_key.get((x[0], ref.get("m_PathID", 0)))
            if o is not None and o.type.name == "SkinnedMeshRenderer":
                out.append(gos.get(x, ""))
    return out

def rule_bodymesh(gk):
    """Iskelet + Animation + en az bir 'bodymesh' SkinnedMeshRenderer."""
    h, s, hd, lm, ct = evaluate(gk)
    if not (h and (hd or s) and lm >= 2):
        return False
    if not (("Animation" in ct) or ("Animator" in ct)):
        return False
    for n in skin_names(gk):
        if n.lower().startswith("bodymesh"):
            return True
    return False

VEH = ("boat", "car", "truck", "tractor", "trailer", "plane", "fork", "crane",
       "sail", "engine", "vehicle", "bus", "bike", "trains", "wagon", "caravan")
def veh_name(n):
    return any(w in n.lower() for w in VEH)

acc = [gk for gk in gos if not veh_name(gos.get(gk, "")) and rule_bodymesh(gk)]
aset = set(acc)
drop = set()
for d in acc:
    for a in ancestors_of(d):
        drop.add(a)
final = [gk for gk in acc if gk not in drop]
names = sorted(set(gos.get(g, "") for g in final))
print("### bodymesh kurali: ham=%d nihai=%d  benzersiz ad=%d" % (len(acc), len(final), len(names)))
print(", ".join(names))
print("\nAMIS2 kaldi mi:", "AMIS2" in names)
print("\n=== hedef kontrol ===")
for w in ("Cousin", "Char", "Julli", "Alpo", "Kale", "Kristian", "Rauno", "Unto",
          "Teimo", "Driver", "Passenger", "VICTRO", "Man", "Neighbour", "Cousin"):
    print("   %-11s -> %d" % (w, len([g for g in final if gos.get(g) == w])))

print("\n" + "=" * 74)
print("PLAYER RIG")
print("=" * 74)
for gk in [k for k in gos if gos.get(k) == "PLAYER"]:
    sub = subtree(gk)
    print("PLAYER altagac=%d dugum" % len(sub))
    skins = [(gos.get(x, ""), ) for x in sub
             for cid, ref in comps.get(x, [])
             if by_key.get((x[0], ref.get("m_PathID", 0)))
             and by_key[(x[0], ref.get("m_PathID", 0))].type.name == "SkinnedMeshRenderer"]
    print("SkinnedMeshRenderer sayisi:", len(skins))
    print("  adlar:", ", ".join(sorted(set(s[0] for s in skins))))
    anims = [gos.get(x, "") for x in sub
             for cid, ref in comps.get(x, [])
             if by_key.get((x[0], ref.get("m_PathID", 0)))
             and by_key[(x[0], ref.get("m_PathID", 0))].type.name == "Animation"]
    print("Animation bileseni olan nesneler:", ", ".join(sorted(set(anims))))
    ccs = [gos.get(x, "") for x in sub
           for cid, ref in comps.get(x, [])
           if by_key.get((x[0], ref.get("m_PathID", 0)))
           and by_key[(x[0], ref.get("m_PathID", 0))].type.name == "CharacterController"]
    print("CharacterController:", ccs)