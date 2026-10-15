# -*- coding: utf-8 -*-
"""
make_bodies.py  -  Birden fazla GOVDE mesh'i .dat olarak uretir.

NEDEN: oyunda "bodymesh" adi altinda birden fazla mesh var ve NPC'ler
farkli olanlari kullaniyor. level2'de olculdu (20 kemik, insan):

    960 vertex   MAP/Buildings/DINGONBIISI/.../bodymesh
    1697 vertex  Granny, Dancer            (ince / kadin)
    1709 vertex  RALLY ... /Driver         (slim erkek)
    1712 vertex  BeerCamp, Teimo, Uncle   (en yaygin, 39/70 skin)
    1714 vertex  NPC_CARS/KUSKI CrashEvent RagDoll
    1789 vertex  SuskiHiker                (uzun)

Once tek mesh uretip 70 skinin hepsine bagliyorduk; boylece 54 skin
kendi govdesi yerine 1712'ye giyiyordu ve "kiyafetler yanlis
giydirilmis" goruntusu cikiyordu.

YONTEM: make_char_data.py her calistirilmasinda TEK mesh yaziyor.
Refactor etmek riskli; bu script onu BUL KEZ, farkli --npc ve --out
ile cagirir. make_char_data.py'ye dokunulmaz.

 Cikti:   bodies/mpnpc_<vertex>.dat   (her biri v1 formatinda)
 MANIFEST: bodies/manifest.json      {"bodies":[{"vertices":1712,"dat":"mpnpc_1712.dat",...}]}
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUTDIR = r"D:\MSCSteamMP\extracted_npc\bodies"

# (vertex, aciklama, ornek NPC yolu, katalog etiketi)
# Etiketler menude gosterilecek.
BODIES = [
    (1712, "kilolu erkek (en yaygin)",
     r"STORE\TeimoInShop\Pivot\Teimo\bodymesh"),
    (1709, "ince erkek",
     r"RALLY\RallyCars\RALLYCAR2\LOD\Driver\Driver\bodymesh"),
    (1714, "genc erkek",
     r"HUMANS\Randomizer\Walkers\Alpo\Pivot\Char\bodymesh"),
    (1697, "kadin / ince",
     r"JOBS\Mummola\LOD\GrannyTalking\Granny\Char\bodymesh"),
    (1789, "uzun erkek",
     r"JOBS\Suski\SuskiPivot\SuskiHiker\Char\bodymesh"),
    (960, "cocuk",
     r"MAP\Buildings\DINGONBIISI\Misc\Thing3\Mover\mesh\Turn\Pivot\bodymesh"),
]

# Oyun yollari '/' ile gelir; UnityPy bu biçimi bekler.
SLASH = [(v, d, p.replace("\\", "/")) for (v, d, p) in BODIES]


def main():
    os.makedirs(OUTDIR, exist_ok=True)
    py = sys.executable or "python"
    script = os.path.join(HERE, "make_char_data.py")
    made = []

    for vc, desc, path in SLASH:
        out = os.path.join(OUTDIR, "mpnpc_%d.dat" % vc)
        print()
        print("=" * 84)
        print("GOVDE %d  (%s)" % (vc, desc))
        print("  NPC : %s" % path)
        print("=" * 84)
        r = subprocess.run([py, script, "--npc", path, "--out", out],
                           capture_output=True, text=True, cwd=HERE)
        # make_char_data sadece bitti/basarisiz yaziyor; ayrintili cikti onun
        if not os.path.isfile(out):
            print("  BASARISIZ: %s" % (r.stderr or r.stdout)[-600:])
            continue
        size = os.path.getsize(out)
        made.append({"vertices": vc, "desc": desc, "npc": path,
                     "dat": os.path.basename(out), "bytes": size})
        print("  OK: %s (%.1f KB)" % (os.path.basename(out), size / 1024.0))

    # varsayilan govdeyi de eski yerine yaz (tek mesh kullanan yollar)
    if made:
        # Varsayilan govde: katalogdaki EN YAYGIN govde (1712, 39/70
        # skin). En buyuk vertex sayisi DEGIL: cocuk (960) ve uzun
        # (1789) mesh'leri daha buyuktur ama nadir kullanilir ve
        # varsayilan olmamalari tum gorumleri delik gosterirdi.
        PREFERRED = 1712
        main_dat = next((b for b in made if b["vertices"] == PREFERRED),
                        None)
        if main_dat is None:
            main_dat = made[0]
            print("UYARI: %d vertex'lik govde bulunamadi; %d kullaniliyor"
                  % (PREFERRED, main_dat["vertices"]))
        src = os.path.join(OUTDIR, main_dat["dat"])
        dst = r"D:\MSCSteamMP\extracted_npc\mpnpc.dat"
        with open(src, "rb") as f:
            data = f.read()
        with open(dst, "wb") as f:
            f.write(data)
        print()
        print("varsayilan mpnpc.dat = %s (%d vertex)"
              % (main_dat["dat"], main_dat["vertices"]))

    mf = {"bodies": made, "default": made[0]["vertices"] if made else 0}
    with open(os.path.join(OUTDIR, "manifest.json"), "w",
              encoding="utf-8") as f:
        json.dump(mf, f, indent=2, ensure_ascii=False)
    print()
    print("MANIFEST: %s  (%d govde)" % (
        os.path.join(OUTDIR, "manifest.json"), len(made)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())