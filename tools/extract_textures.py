# -*- coding: utf-8 -*-
"""
extract_textures.py
NPC materyallerinin dokularini PNG olarak disari aktarir.

Tek veri kaynagi roster.json'dir: skin_roster.py materyalin GERCEK _MainTex
dokusunu cozer. Doku adi materyal adiyle ayni DEGIL (cop_pants -> cop_pants01,
char_shirt01 -> char_shirt01), yani materyal adindan doku adi varsaymak
yanlis cikiyor.

    python extract_textures.py                     # roster.json'daki hepsi
    python extract_textures.py --prefix char_      # onek filtresi
"""
import json
import os
import sys

import UnityPy
from PIL import Image

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
ROOT = r"D:\MSCSteamMP\extracted_npc"
OUT = os.path.join(ROOT, "textures")
ROSTER = os.path.join(ROOT, "roster.json")

prefix = ""
for a in sys.argv[1:]:
    if a.startswith("--prefix"):
        prefix = ""

wanted = []
if os.path.isfile(ROSTER) and not prefix:
    with open(ROSTER, encoding="utf-8") as f:
        wanted = json.load(f).get("textures", [])
else:
    files = [os.path.join(DATA, "sharedassets%d.assets" % i) for i in range(6)]
    wanted = None
    wanted_set = set()

os.makedirs(OUT, exist_ok=True)

files = ([os.path.join(DATA, "sharedassets%d.assets" % i) for i in range(6)]
         + [os.path.join(DATA, "resources.assets")]
         + [os.path.join(DATA, n)
            for n in ("level0", "level1", "level2", "level3")])

index = {}
for f in files:
    if not os.path.isfile(f):
        continue
    env = UnityPy.load(f)
    for o in env.objects:
        if o.type.name != "Texture2D":
            continue
        try:
            nm = getattr(o.read(), "m_Name", "")
        except Exception:
            continue
        if not nm:
            continue
        if wanted is not None:
            if nm not in wanted:
                continue
        index.setdefault(nm, (f, o.path_id))

if wanted is not None:
    miss = [t for t in wanted if t not in index]
    if miss:
        print("UYARI: %d doku HIC BULUNAMADI: %s"
              % (len(miss), ", ".join(miss)))

print("=" * 84)
print("HEDEF DOKULAR: %d" % len(index))
print("=" * 84)

ok, fail = 0, 0
for nm in sorted(index):
    f, pid = index[nm]
    env = UnityPy.load(f)
    try:
        obj = next(o for o in env.objects
                   if o.type.name == "Texture2D" and o.path_id == pid)
        t = obj.read()
        img = t.image
        if img is None:
            raise ValueError("image None")
        w, h = img.size
        path = os.path.join(OUT, nm + ".png")
        img.save(path)
        print("  %-24s %-24s %4dx%-4d" % (nm, os.path.basename(f), w, h))
        ok += 1
    except Exception as e:
        print("  %-24s HATA: %s" % (nm, e))
        fail += 1

print("-" * 84)
print("basarili=%d  hata=%d  -> %s" % (ok, fail, OUT))