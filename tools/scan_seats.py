import sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
exec(open(r'D:\modmscMP\MSCSteamMP\tools\judge_humanoid4.py').read().split('VEH =')[0])

TARGETS = ["GIFU(750/450psi)", "RCO_RUSCKO12(270)", "FERNDALE(1630kg)", "COMBINE(350-400psi)"]
KEYS = ("trigger", "seat", "drive", "driver", "enter", "player", "sit", "cockpit", "cabin")

for want in TARGETS:
    roots = [k for k in gos if gos.get(k) == want]
    print("=" * 74)
    print("ARAC: %s  kok=%d" % (want, len(roots)))
    for gk in roots:
        # tum alt agac adlari
        names = sorted(set(gos.get(x, "") for x in subtree(gk)))
        hits = [n for n in names if any(k in n.lower() for k in KEYS)]
        print("  aday (%d): %s" % (len(hits), ", ".join(hits)))
        # ayrica FSM tasiyabilecek supheliler: Use/Trigger iceren tum adlar
        alluse = sorted(set(n for n in names if "use" in n.lower() or "knob" in n.lower() or "button" in n.lower()))
        print("  use/knob/button (%d): %s" % (len(alluse), ", ".join(alluse[:40])))
