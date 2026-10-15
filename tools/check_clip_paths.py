# -*- coding: utf-8 -*-
"""Clip'lerin curve path kokleri: hangi clip 'Char/skeleton/...' hangisi 'pelvis/...'"""
import json, io, os

ANIMS = r"D:\MSCSteamMP\extracted_npc\anims"
targets = ["fat_standing", "fat_walk", "fat_get_car_in", "fat_get_car_out",
           "fat_get_car_idle1", "fat_get_car_idle2", "fat_spine_upper_breath",
           "fat_handsleft_idle", "fat_handsright_idle", "woman_get_car_in",
           "granny_get_car_in", "bus_driver_door", "bus_driver_cash",
           "fat_walk_run", "fat_run", "fat_walking", "fat_jog"]

for t in targets:
    p = os.path.join(ANIMS, t + ".json")
    if not os.path.isfile(p):
        continue
    d = json.load(io.open(p, encoding="utf-8"))
    print("=" * 92)
    print("%-24s legacy=%-5s compressed=%-5s rate=%-6s wrap=%s curves=%d"
          % (d["name"], d["legacy"], d["compressed"], d["sample_rate"],
             d["wrap_mode"], len(d["curves"])))
    for c in sorted(d["curves"], key=lambda x: x["path"]):
        ks = c["keys"]
        if ks:
            print("   %-64s %-10s keys=%-4d t=%.3f..%.3f"
                  % (c["path"], c["type"], len(ks), ks[0][0], ks[-1][0]))
        else:
            print("   %-64s %-10s keys=0" % (c["path"], c["type"]))

print()
print("=" * 92)
print("TUM CLIP'LERDE KULLANILAN EN BASKIN PATH ONEKI (ilk segment)")
print("=" * 92)
from collections import Counter
pref = Counter()
full = Counter()
for f in os.listdir(ANIMS):
    if not f.endswith(".json"):
        continue
    d = json.load(io.open(os.path.join(ANIMS, f), encoding="utf-8"))
    for c in d["curves"]:
        pth = (c.get("path") or "").strip()
        if not pth:
            continue
        pref[pth.split("/")[0]] += 1
        full["/".join(pth.split("/")[:2])] += 1
for k, v in pref.most_common(12):
    print("   %-30s %d" % (k, v))
print("   --- ilk iki segment ---")
for k, v in full.most_common(12):
    print("   %-30s %d" % (k, v))