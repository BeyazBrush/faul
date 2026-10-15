# -*- coding: utf-8 -*-
"""
gen_skin_catalog.py
roster.json -> src/MSCSteamMP/Player/NpcSkinCatalog.cs

C# tarafinda JSON ayristirmaya gerek kalmasin diye katalog DERLENMIS
kod olarak uretilir. Elle yazmak 70 satir degil, uretim zincirinin parcasidir:
oyun guncellenince roster degisir, katalog da degisir.

Uretilen tip:
    public struct NpcSkin { public string name, shirt, pants, face; }
    public static class NpcSkinCatalog {
        public static readonly NpcSkin[] All = { ... };
        public static readonly TextureName[] Textures = { ... };   // bundle'a girenler
        public const int Count = ...;
    }
"""
import io
import json
import os
import re

ROSTER = r"D:\MSCSteamMP\extracted_npc\roster.json"
MESHMAP_FILE = r"D:\MSCSteamMP\extracted_npc\skin_mesh_map.json"
OUT = r"D:\modmscMP\MSCSteamMP\src\MSCSteamMP\Player\NpcSkinCatalog.cs"

# Bundle'da hangi govde mesh var? make_char_data.py'in cikardigi.
# unity/build_npc.log: "[MPNPC] mesh: 1712 vertex" -> 1712 (en yaygin, 39/70 skin)
DEFAULT_BODY = 1712

# Yoldan ad cikarirken ATLANACAK genel segmentler
SKIP = {
    "char", "pivot", "p", "mesh", "skeleton", "offset", "lod", "open", "play",
    "functions", "turn", "actors", "disable", "0", "1", "2", "3", "4", "5",
    "startareaobjects", "startarea", "sunday", "saturday",
}


def is_skip(seg):
    s = seg.strip().lower()
    s = re.sub(r"\(x+\)", "", s).strip()
    if not s:
        return True
    if s in SKIP:
        return True
    if s.startswith("bodymesh"):
        return True
    if s.startswith("ragdoll"):
        return True
    if s.isdigit():
        return True
    if re.match(r"^(passengers?|drivers?|seats?|lodb|passenger|driver)\s*\d*$", s):
        return True
    return False


def nice_name(path):
    segs = [re.sub(r"\(x+\)", "", s).strip() for s in path.split("/")]
    segs = [s for s in segs if s]
    for s in reversed(segs):
        if is_skip(s):
            continue
        n = re.sub(r"\s*\d+\s*$", "", s).strip(" _-")
        if len(n) < 2:
            continue
        return n[0].upper() + n[1:]
    return "Skin"


def main():
    with open(ROSTER, encoding="utf-8") as f:
        data = json.load(f)
    skins = data["skins"]
    textures = data["textures"]

    # skin -> govde mesh eslesmesi (gen_skin_mesh_map.py ciktisi).
    # Anahtar: "shirtMat|pantsMat|faceMat" — roster'daki MALZEME adlariyla.
    MESHMAP = {}
    if os.path.isfile(MESHMAP_FILE):
        with open(MESHMAP_FILE, encoding="utf-8") as f:
            MESHMAP = json.load(f)
        print("skin_mesh_map: %d kayit" % len(MESHMAP))
    else:
        print("UYARI: %s yok, govde bilgisi 0 yazilacak" % MESHMAP_FILE)

    used = set()
    rows = []
    ROWMAT = {}
    n_mesh_ok = 0
    for e in skins:
        nm = nice_name(e["samples"][0] if e["samples"] else e["name"])
        base = nm
        i = 2
        while nm in used:
            nm = "%s %d" % (base, i)
            i += 1
        used.add(nm)
        rows.append((nm, e["shirt"], e["pants"], e["face"]))
        # govde vertex sayisi: skin_mesh_map MALZEME adiyla anahtarlanir
        # (shirtMat|pantsMat|faceMat), katalog ise DOKU adlarini tasir.
        k = "|".join([e["shirtMat"], e["pantsMat"], e["faceMat"]])
        if MESHMAP.get(k):
            n_mesh_ok += 1
        ROWMAT[(nm, e["shirt"], e["pants"], e["face"])] = k
    print("regoven govde bilgisi: %d/%d skin" % (n_mesh_ok, len(rows)))

    tex = sorted(set(textures) | set(t for r in rows for t in r[1:]))

    o = io.open(OUT, "w", encoding="utf-8", newline="\r\n")
    o.write("// ---------------------------------------------------------------------------\n")
    o.write("// NpcSkinCatalog.cs  -  OTOMATIK URETILIR, ELLE DUZENLEMEYIN.\n")
    o.write("//\n")
    o.write("// Uretici  : tools\\gen_skin_catalog.py\n")
    o.write("// Kaynak    : tools\\skin_roster.py -> extracted_npc\\roster.json\n")
    o.write("//\n")
    o.write("// Oyunun kendi NPC kadrosu.\n")
    o.write("//\n")
    o.write("// GORUNUM = uc doku + GOVDE TIPI.\n")
    o.write("// 'TUM NPC'ler ayni mesh'i paylasir' onceki varsayimi YANLISTI.\n")
    o.write("// Oyunda \"bodymesh\" adi altinda birden fazla mesh var ve NPC'ler\n")
    o.write("// farkli olanlari kullaniyor. level2'de olculdu (tools\\gen_skin_mesh_map.py):\n")
    o.write("//\n")
    o.write("//   bodymesh      1712 vertex   115 SMR\n")
    o.write("//   bodymesh      1709 vertex    35 SMR\n")
    o.write("//   bodymesh      1714 vertex    14 SMR\n")
    o.write("//   bodymesh      1697 vertex     8 SMR   (ince / kadin)\n")
    o.write("//   bodymesh_kid  1714 vertex     5 SMR\n")
    o.write("//   bodymesh      1789 vertex     5 SMR   (uzun)\n")
    o.write("//\n")
    o.write("// 70 skin'in 54'u 1712-vertex govdeyi kullaniyordu ama biz yalnizca\n")
    o.write("// 1709-lu varyanti cikarmistik. Sonuc: yasli yuz + kadin yaka gomlek\n")
    o.write("// + genc erkek bedeni ayni modelde birlesiyor (kullanicinin ekran\n")
    o.write("// goruntusundeki 'kiyafetler yanlis giydirilmis' tablosu).\n")
    o.write("//\n")
    o.write("// Bu dosya tools\\gen_skin_catalog.py ile URETILIR. govde vertex sayisi\n")
    o.write("// tools\\gen_skin_mesh_map.py ciktisi olan skin_mesh_map.json'dan gelir.\n")
    o.write("// ---------------------------------------------------------------------------\n")
    o.write("\n")
    o.write("namespace SteamMP {\n\n")
    o.write("\t/// <summary>Bir NPC gorunumu: uc doku + govde tipi.</summary>\n")
    o.write("\tpublic struct NpcSkin {\n")
    o.write("\t\t/// <summary>Menu ve logda gorunen ad (oyundaki NPC adi).</summary>\n")
    o.write("\t\tpublic string name;\n")
    o.write("\t\t/// <summary>Gomlek dokusu (atlas adi, .png'siz).</summary>\n")
    o.write("\t\tpublic string shirt;\n")
    o.write("\t\t/// <summary>Pantolon dokusu.</summary>\n")
    o.write("\t\tpublic string pants;\n")
    o.write("\t\t/// <summary>Yuz dokusu.</summary>\n")
    o.write("\t\tpublic string face;\n")
    o.write("\t\t/// <summary>Govde mesh'inin vertex sayisi. Bundle'daki govdeyle\n")
    o.write("\t\t/// eslesmezse kiyafetler yanlis bedene oturur (yaka/cap uyumsuzlugu,\n")
    o.write("\t\t/// kollarin/kadin govdesinin erkek omuzuna girmesi).</summary>\n")
    o.write("\t\t/// <remarks>0 = katalogda cozulemedi, varsayilan govde kullanilir.</remarks>\n")
    o.write("\t\tpublic int bodyVertices;\n")
    o.write("\n")
    o.write("\t\tpublic NpcSkin(string n, string s, string p, string f, int bv) {\n")
    o.write("\t\t\tname = n; shirt = s; pants = p; face = f; bodyVertices = bv;\n")
    o.write("\t\t}\n")
    o.write("\n")
    o.write("\t\tpublic override string ToString() {\n")
    o.write("\t\t\treturn name + \" (\" + shirt + \", \" + pants + \", \" + face\n")
    o.write("\t\t\t\t+ (bodyVertices > 0 ? \", govde \" + bodyVertices : \"\") + \")\";\n")
    o.write("\t\t}\n")
    o.write("\t}\n\n")
    o.write("\t/// <summary>Oyunun NPC kadrosu ve skin'lerin kullandigi dokular.</summary>\n")
    o.write("\tpublic static class NpcSkinCatalog {\n\n")
    o.write("\t\t/// <summary>Toplam skin sayisi.</summary>\n")
    o.write("\t\tpublic const int Count = %d;\n\n" % len(rows))
    o.write("\t\t/// <summary>Bundle'daki varsayilan govdenin vertex sayisi.</summary>\n")
    o.write("\t\tpublic const int DEFAULT_BODY = %d;\n\n" % DEFAULT_BODY)
    o.write("\t\tpublic static readonly NpcSkin[] All = {\n")
    for nm, s, p, f in rows:
        # katalog DOKU adi tasir; skin_mesh_map MALZEME adiyla anahtarlanir.
        # roster.json'da ikisi de var; katalog satiri icin malzeme
        # adlarini o satira ait veriden aliyoruz (asagida ROWMAT).
        key = ROWMAT.get((nm, s, p, f))
        bv = 0
        if key and MESHMAP.get(key):
            bv = MESHMAP[key]["vertices"]
        if bv == 0:
            # Cozulemeyen skin: ornek NPC yolu level0/1/3'te kalmis
            # olabilir. Guvenli varsayilan en yaygin govde (1712,
            # 39/70 skin). 0 birakmak runtime'da "bilinmiyor" demek
            # ve ozellikle 1697/1789 (kadin/uzun) bedenler yanlis
            # gomleklendirilirdi.
            bv = DEFAULT_BODY
        o.write('\t\t\tnew NpcSkin("%s", "%s", "%s", "%s", %d),\n'
                % (nm, s, p, f, bv))
    o.write("\t\t};\n\n")
    o.write("\t\t/// <summary>Bundle'a gomulen doku adlari. %d adet.</summary>\n" % len(tex))
    o.write("\t\t/// <remarks>Bundle yolu: assets/mpnctextures/&lt;ad&gt;.png (kucuk harf).</remarks>\n")
    o.write("\t\tpublic static readonly string[] Textures = {\n")
    for t in tex:
        o.write('\t\t\t"%s",\n' % t)
    o.write("\t\t};\n")
    o.write("\t}\n")
    o.write("}\n")
    o.close()

    print("skin sayisi   : %d" % len(rows))
    print("doku sayisi   : %d" % len(tex))
    print("yazildi       : %s" % OUT)
    print()
    print("%-20s %-20s %-20s %-20s" % ("AD", "GOMLEK", "PANTOLON", "YUZ"))
    print("-" * 84)
    for nm, s, p, f in rows[:12]:
        print("%-20s %-20s %-20s %-20s" % (nm, s, p, f))
    print("... (%d daha)" % max(0, len(rows) - 12))


if __name__ == "__main__":
    main()