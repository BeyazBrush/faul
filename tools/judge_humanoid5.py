import sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
src = open(r'D:\modmscMP\MSCSteamMP\tools\judge_humanoid4.py').read().split('VEH =')[0]
exec(src)

# INSAN OLMAYAN: makine parcasi adi tasiyan alt ogeler
MACHINE = ("wheel", "tire", "tyre", "rim", "axle", "differential", "spindle",
           "engine", "clutch", "gearbox", "exhaust", "spoiler", "bumper", "fender",
           "chassis", "windscreen", "windshield", "wiper", "headlight", "taillight",
           "brakelight", "interior", "dashboard", "steering", "pedal", "radiator",
           "propeller", "rudder", "sail", "keel", "hull", "seats_driver")

def machine_inside(gk):
    for x in subtree(gk):
        n = gos.get(x, "").lower()
        for w in MACHINE:
            if w in n:
                return gos.get(x, "")
    return None

def humanoid2(gk):
    h, s, hd, lm, ct = evaluate(gk)
    if not (h and (hd or s) and lm >= 2):
        return False
    if "SkinnedMeshRenderer" not in ct:
        return False
    if not (("Animation" in ct) or ("Animator" in ct)):
        return False
    return machine_inside(gk) is None

VEH = ("boat", "car", "truck", "tractor", "trailer", "plane", "fork", "crane",
       "sail", "engine", "vehicle", "bus", "bike", "trains", "wagon", "caravan")
def veh_name(n):
    return any(w in n.lower() for w in VEH)

acc = [gk for gk in gos if not veh_name(gos.get(gk, "")) and humanoid2(gk)]
aset = set(acc)
drop = set()
for d in acc:
    for a in ancestors_of(d):
        drop.add(a)
final = [gk for gk in acc if gk not in drop]
print("### makine-adi elemeli: ham=%d nihai=%d" % (len(acc), len(final)))
print("AMIS2 kaldi mi :", any(gos.get(gk) == "AMIS2" for gk in final))
print("nihai adlar (%d):" % len(set(gos.get(g, "") for g in final)))
print(", ".join(sorted(set(gos.get(g, "") for g in final))))

print("\n=== kontrol: secilen isimler gecer mi? ===")
for w in ("Cousin", "Char", "Julli", "Alpo", "Kale", "Kristian", "Rauno", "Unto",
          "Teimo", "Driver", "Passenger", "VICTRO", "Man", "Neighbour"):
    n = len([g for g in final if gos.get(g) == w])
    print("   %-11s -> %d" % (w, n))

print("\n" + "=" * 74)
print("OYUNCU GOVDESI: CharacterController + SkinnedMeshRenderer tasiyan koklar")
print("=" * 74)
found = []
for gk in gos:
    ct = []
    for x in subtree(gk):
        for cid, ref in comps.get(x, []):
            o = by_key.get((x[0], ref.get("m_PathID", 0)))
            ct.append(o.type.name if o else "?")
    if "CharacterController" in ct and "SkinnedMeshRenderer" in ct:
        found.append((len(subtree(gk)), gos.get(gk, "")))
found.sort()
print("aday sayisi:", len(found))
for n, nm in found[:25]:
    print("   %-28s altagac=%d" % (nm, n))