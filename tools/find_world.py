import sys, os, collections
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
exec(open(r'D:\modmscMP\MSCSteamMP\tools\judge_humanoid4.py').read().split('VEH =')[0])

# FSM adlari ve oyuncu-nesne adlari: tum GameObject adlari + bunlarin
# PlayMakerFSM/diger MonoBehaviour sayilari degil, sadece ADLAR.
names = sorted(set(n for n in gos.values() if n))

def show(label, pred, limit=70):
    hits = [n for n in names if pred(n)]
    print("\n--- %s (%d) ---" % (label, len(hits)))
    for n in hits[:limit]:
        print("   " + n)
    return hits

# KAPILAR
doors = show("KAPI (door)", lambda n: "door" in n.lower() or "ovi" in n.lower(), 80)

# MUSLUK / SU
show("MUSLUK/SU (tap/water/shower/khana)", lambda n: any(k in n.lower() for k in
     ("tap", "water", "shower", "khana", "bath", "sink", "toilet", "wc", "pisu")),
     70)

# RADYO / CD
radio = show("RADYO/CD", lambda n: any(k in n.lower() for k in
     ("radio", "cd", "tape", "cassette", "music", "stereo", "speaker")), 80)

# NPC onay prefablari
show("NPC ONERI (kiosk/fighter/teimo)", lambda n: any(k in n.lower() for k in
     ("kiosk", "fighter", "teimo", "npc", "husband", "wife", "homeless")), 60)

# Arac parcalari / isiklar
show("SATSUMA ISIK/KAPUT/KAPI", lambda n: any(k in n.lower() for k in
     ("bootlid", "bootlid", "bonnet", "light", "indicator", "headl", "exhaust", "wiper")), 60)

# FSM olay/state adlari (buyuk harf + FINISHED/PLAY gibi)
print("\n--- OLAY ADLARI (FINISHED/PLAY/DONE) ---")
ev = [n for n in names if n in ("FINISHED", "PLAY", "DONE", "ON", "OFF", "OPEN", "CLOSE",
                                "START", "STOP", "TURNON", "TURNOFF", "LOCK", "UNLOCK")]
print("   " + ", ".join(sorted(ev)))