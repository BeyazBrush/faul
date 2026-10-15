"""
modun ciktilari level2 dump'uyla karsilastirir.

Modun kendi logu (canli oturum): "267 bolt(s)" (host) / "272" (peer).
README'nin olctugu sayi: 541 (SATSUMA 137 + CARPARTS 217 + STORE 169 +
REPAIRSHOP 8 + YARD 7 + ITEMS 3).

Iki sayi tutmuyorsa tarama bir alt kumeyle sinirli demektir -- ve
karsı tarafta olmayan cıvatalar icin DEXIS kaymasi, yani gelen TIGHTEN
baska bir cıvataya duser. Canli izdeki `tightened -1 -> 8` /
`loosened 8 -> 8` belirtisi tam olarak bu.

Kullanim:
    python check_bolts.py dump2/fsms.json
"""
import collections
import json
import sys

# README'nin olctugu agaclar -> kac civata (Stage/Tightness tasiyan)
EXPECTED = {
    "SATSUMA": 137, "CARPARTS": 217, "STORE": 169,
    "REPAIRSHOP": 8, "YARD": 7, "ITEMS": 3,
}


def main():
    fsms = json.load(open(sys.argv[1], encoding="utf-8"))
    print(f"{len(fsms)} FSM okundu.\n")

    # 1) CIVATA: `Screw` FSM'i ve Stage/Tightness tasiyip tasimadigi
    screw = [f for f in fsms if f["fsm"] == "Screw"]
    with_vars, without = [], []
    for f in screw:
        ss = set(f.get("strings", []))
        has = ("Stage" in ss) or ("Tightness" in ss)
        (with_vars if has else without).append(f)
    print(f"=== CIVATA (Screw FSM) ===")
    print(f"  toplam Screw FSM : {len(screw)}")
    print(f"  Stage/Tightness tasiyan: {len(with_vars)}")
    print(f"  tasimayan              : {len(without)}")

    # Aggaca gore dagilim
    by_tree = collections.Counter()
    for f in with_vars:
        top = (f.get("path") or "?").split("/", 1)[0]
        by_tree[top] += 1
    print("\n  agac bazinda (Stage/Tightness tasiyanlar):")
    for k, v in by_tree.most_common(12):
        exp = EXPECTED.get(k.upper())
        mark = ""
        if exp is not None:
            mark = f"   README: {exp}" + ("  <<< FARK" if v != exp else "  (uyuyor)")
        print(f"    {k:22} {v:5d}{mark}")
    total = len(with_vars)
    print(f"\n  TOPLAM: {total}   README'nin dedigi: {sum(EXPECTED.values())}")
    if total != sum(EXPECTED.values()):
        print("  <<<< TARAMA EKSIK: modun buldugu sayi gercek sayidan az.")

    # 2) PARCA: Assembly/Removal/Assemble/BoltCheck FSM'i
    print("\n=== PARCA AILESI ===")
    fam = collections.Counter(f["fsm"] for f in fsms
                              if f["fsm"] in ("Assembly", "Removal", "Assemble", "BoltCheck"))
    for k in ("Assembly", "Removal", "Assemble", "BoltCheck"):
        print(f"  {k:12} {fam.get(k, 0):5d}")

    # Kurulabilir = Assembly FSM'i tasiyan nesne (README: 102 Assembly)
    asm = [f for f in fsms if f["fsm"] == "Assembly"]
    declares = sum(1 for f in asm if "ASSEMBLE" in set(f.get("strings", [])))
    print(f"  ASSEMBLE olayi bildiren Assembly FSM: {declares}/{len(asm)}")
    rem = [f for f in fsms if f["fsm"] == "Removal"]
    r_decl = sum(1 for f in rem if "ASSEMBLE" in set(f.get("strings", []))
                 and "REMOVE" in set(f.get("strings", [])))
    print(f"  ASSEMBLE+REMOVE bildiren Removal FSM : {r_decl}/{len(rem)}")

    # 3) KAPI: Use/UseOld ve OPEN/CLOSE bildirenler
    print("\n=== KAPI AILESI ===")
    use = [f for f in fsms if f["fsm"] in ("Use", "UseOld")]
    panels = [f for f in use
              if "OPEN" in set(f.get("strings", [])) or "CLOSE" in set(f.get("strings", []))]
    print(f"  Use/UseOld FSM            : {len(use)}")
    print(f"  OPEN veya CLOSE bildiren  : {len(panels)}   (README: 142)")

    # 4) DURUM ADLARI: gercek yazim
    print("\n=== GERCEK DURUM ADLARI (Fs`m.GetState` birebir arar) ===")
    st = collections.Counter()
    for f in fsms:
        for s in f.get("strings", []):
            if s and len(s) < 26 and ("air" in s or "ait" in s.lower()) and " " in s:
                st[s] += 1
    for k, v in st.most_common(8):
        print(f"  {v:5d}  {k!r}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
