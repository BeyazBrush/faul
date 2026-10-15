import sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(DATA)
objs = list(env.objects)

names = {}
for o in objs:
    if o.type.name != "AnimationClip":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    n = t.get("m_Name", "")
    names.setdefault(n, []).append((o.assets_file.name, o.path_id))

print("AnimationClip turu:", len(names))

# Yurus/kosu adayi olan klipler
KEYS = ("walk", "run", "jog", "idle", "step", "sprint", "move", "stand", "talk", "drive")
hits = sorted(n for n in names if any(k in n.lower() for k in KEYS))
print("\n--- hareket/animasyon adli klipler (%d) ---" % len(hits))
for n in hits:
    print("   %-40s x%d  [%s]" % (n, len(names[n]), names[n][0][0]))

print("\n--- toplam benzersiz klip adi: %d ---" % len(names))
print("\n--- ilk 60 klip adi (alfabetik) ---")
for n in sorted(names)[:60]:
    print("   " + n)