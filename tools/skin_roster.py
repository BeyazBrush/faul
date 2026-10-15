# -*- coding: utf-8 -*-
"""
skin_roster.py
Oyundaki TUM NPC'leri, giysi materyalleriyle birlikte cikarir.
"Player" listesinin gercek kaynagi bu: mesh hepsinde ayni (bodymesh),
gorunum materyalden gelir (shirt/pants/face).

Cikti:
    roster.json   -> [{ad, yol, shirt, pants, face, shader}, ...]
    roster.txt    -> okunabilir liste
"""
import json
import os
import re
from collections import defaultdict

import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
LEVELS = ["level0", "level1", "level2", "level3"]
OUT = r"D:\MSCSteamMP\extracted_npc\roster.json"

# NPC adini yoldan cikarmak icin bilinen etiketler (kucuk->buyuk esleme)
LABELS = [
    "teimo", "uncle", "granny", "grannyhiker", "cop", "police", "rallies",
    "rally", "spectator", "bus", "latanen", "signe", "signa", "amis",
    "kuski", "kylloniemi", "kyllajani", "drunk", "beer", "shitman", "shit",
    "berryman", "servant", "moose", "pig", "venttipig", "jokke", "hiker",
    "marks", "mara", "president", "neighbour", "guard", "drummer", "dancer",
    "copteimo", "kalle", "kristian", "alpo", "kippu", "mikko", "classmate",
    "truckdriver", "driver", "passenger", "boat", "man", "woman", "kid",
    "fighter", "assistent", "shop", "store", "theatre", "dancehall",
]


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


# ---------------------------------------------------------------- materyal -> doku
def mat_texture(material_name):
    """Material'in gercek _MainTex dokusunun adi.
    Doku adi materyal adiyle AYNI degil: cop_pants -> cop_pants01,
    rally_pants -> rally_pants01, curtain4 -> curtain4 (oyun dokusu),
    fabric_flower2 -> fabric_flower2. Yolu varsaymak yanlis cikiyor."""
    for _pass in range(2):
        m = MATS.get(material_name)
        if m is None:
            return None
        sp = getattr(m, "m_SavedProperties", None)
        if sp is None:
            return None
        for tf in (getattr(sp, "m_TexEnvs", None) or []):
            env = tf[1] if isinstance(tf, (list, tuple)) and len(tf) >= 2 else None
            if env is None:
                env = getattr(tf, "second", None)
            ptr = getattr(env, "m_Texture", None)
            if ptr is None:
                continue
            t = None
            try:
                t = ptr.read()
                if hasattr(t, "read"):
                    t = t.read()
            except Exception:
                t = None
            if t is not None and getattr(t, "m_Name", None):
                return t.m_Name
        return None
    return None


def clean(seg):
    seg = re.sub(r"\(x+\)", "", seg)
    seg = re.sub(r"\d+", " ", seg)
    return seg.strip(" _-")


def guess_name(path):
    """Yoldan okunabilir bir NPC adi cikar."""
    parts = [clean(p) for p in path.split("/")]
    parts = [p for p in parts if p]
    for p in reversed(parts):
        lp = p.lower()
        for lab in LABELS:
            if lab in lp and len(p) <= 18:
                return p
    # son anlamli segment
    for p in reversed(parts):
        if p.lower() not in ("pivot", "char", "skeleton", "bodymesh", "ragdoll",
                             "lod", "p", "driver"):
            return p
    return parts[-1] if parts else "?"


def scan(level_name, buckets):
    env = UnityPy.load(os.path.join(DATA, level_name))
    tr = {}
    for o in env.objects:
        if o.type.name != "Transform":
            continue
        try:
            t = o.read()
            nm = getattr(t.m_GameObject.read(), "m_Name", "")
        except Exception:
            continue
        tr[o.path_id] = {"n": nm, "p": getattr(t.m_Father, "path_id", 0),
                         "go": getattr(t.m_GameObject, "path_id", -1)}
    go_tr = {}
    for pid, i in tr.items():
        go_tr[i["go"]] = pid

    def path_of(pid):
        parts, g = [], 0
        while pid in tr and g < 96:
            parts.append(tr[pid]["n"])
            pid = tr[pid]["p"]
            g += 1
        return "/".join(reversed(parts))

    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        try:
            s = o.read()
            gpid = getattr(s.m_GameObject, "path_id", -1)
            mesh = deref(s.m_Mesh)
            mn = getattr(mesh, "m_Name", "")
        except Exception:
            continue
        if not str(mn).startswith("bodymesh"):
            continue
        pid = go_tr.get(gpid)
        if pid is None:
            continue
        full = path_of(pid)
        mats = []
        for mp in (s.m_Materials or []):
            m = deref(mp)
            mats.append(getattr(m, "m_Name", "?") if m else "?")
        if len(mats) < 3:
            continue
        # submesh sirasi = shirt, pants, face (dogrulandi: rally_shirt/rally_pants/char_face10)
        shirt, pants, face = mats[0], mats[1], mats[2]
        key = (shirt, pants, face)
        if key not in buckets:
            buckets[key] = {"shirt": shirt, "pants": pants, "face": face,
                            "samples": []}
        if len(buckets[key]["samples"]) < 4:
            buckets[key]["samples"].append(full)


buckets = {}

# ---- once tum Material'leri topla (doku cozumu icin).
# ---- DOGRUSU: Material'ler level dosyalarinda DEGIL, sharedassets*.assets
# ---- icinde. Level taramasi 0 material buluyor ve cozum yanlis zannediyor.
ALL_FILES = ([os.path.join(DATA, "sharedassets%d.assets" % i) for i in range(6)]
             + [os.path.join(DATA, "resources.assets")]
             + [os.path.join(DATA, n) for n in LEVELS])
MATS = {}
for p in ALL_FILES:
    if not os.path.isfile(p):
        continue
    _env = UnityPy.load(p)
    for o in _env.objects:
        if o.type.name != "Material":
            continue
        try:
            m = o.read()
        except Exception:
            continue
        n = getattr(m, "m_Name", None)
        if n and n not in MATS:
            MATS[n] = m
print("toplanan Material: %d" % len(MATS))

# dokularin gercekten var oldugunu dogrula
TEX = r"D:\MSCSteamMP\extracted_npc\textures"
have_tex = set(os.listdir(TEX)) if os.path.isdir(TEX) else set()

for lv in LEVELS:
    p = os.path.join(DATA, lv)
    if os.path.isfile(p):
        scan(lv, buckets)

# gercek doku adlarini coz ve olmayanlari ele
SKIP_MATS = ("char_ghost",)
for key in list(buckets.keys()):
    b = buckets[key]
    real = []
    for k in ("shirt", "pants", "face"):
        if any(k.startswith(s) or s.startswith(k) for s in SKIP_MATS):
            real.append(None)
            continue
        t = mat_texture(b[k])
        if t is None:
            # doku cozulemediyse materyal adinin kendisi olabilir
            t = b[k] if (b[k] + ".png") in have_tex else None
        real.append(t)
    b["tex"] = real
    if any(t is None for t in real):
        buckets.pop(key, None)

print("=" * 100)
print("BENZERSIZ GIYIM KUMESI: %d  (hepsi ayni mesh/rig uzerine)" % len(buckets))
print("=" * 100)

used_tex = set()
out = []
for key in sorted(buckets.keys()):
    b = buckets[key]
    name = guess_name(b["samples"][0]) if b["samples"] else "?"
    used_tex.update(t for t in b["tex"] if t)
    out.append({
        "name": name,
        "shirtMat": b["shirt"], "pantsMat": b["pants"], "faceMat": b["face"],
        "shirt": b["tex"][0], "pants": b["tex"][1], "face": b["tex"][2],
        "samples": b["samples"],
    })

print("%-16s %-18s %-18s %-18s" % ("AD", "GOMLEK", "PANTOLON", "YUZ"))
print("-" * 100)
for e in out:
    print("%-16s %-18s %-18s %-18s"
          % (e["name"][:16], str(e["shirt"])[:18], str(e["pants"])[:18],
             str(e["face"])[:18]))

print()
print("KULLANILAN DOKU SAYISI: %d" % len(used_tex))
missing = sorted(t for t in used_tex if (t + ".png") not in have_tex)
print("EKSIK DOKULAR (%d): %s" % (len(missing), ", ".join(missing) or "yok"))

with open(OUT, "w", encoding="utf-8") as f:
    json.dump({"skins": out, "textures": sorted(used_tex)}, f, indent=1,
              ensure_ascii=False)
print("YAZILDI: " + OUT)