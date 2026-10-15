"""
Modun bekledigi FSM olay/degisken adlarini level2 dump'uyla karsilastirir.

Neden gerekli: `Fsm.HasEvent` ve `FsmVariables.FindFsmBool` dnSpy ile
dogrulandi -- ikisi de `name == expected` ile karsilastiriyor, yani
BUYUK/KUCUK HARFE DUYARLI. Mod listelerinde bir adi yanlis harfle
yazmissa (ornegin "OPEN" yerine "Open") o FSM sessizce atlanir ve
senkron calismaz.

Kullanim:
    python check_case.py dump2/fsms.json
"""
import collections
import json
import sys

# Modun bekledigi adlar -> kaynak (hangi modulde tanimli)
EXPECTED = {
    # Interaction\PartSync / Satsuma (parca takma-sokme)
    "ASSEMBLE":   "PartSync / AssemblyPatch.IsPartEvent",
    "REMOVE":     "PartSync / AssemblyPatch.IsPartEvent",
    "DISASSEMBLE":"AssemblyPatch",
    "INSTALLED":  "SatsumaRegistry.OnTrackedEvent",
    # WorldObjectSync kapilari
    "OPEN":       "WorldObjectSync.DOOR_EVENTS",
    "CLOSE":      "WorldObjectSync.DOOR_EVENTS",
    "OPENDOOR":   "WorldObjectSync.DOOR_EVENTS",
    "DoorOpen":   "WorldObjectSync.DOOR_EVENTS",
    "USE":        "WorldObjectSync.DOOR_EVENTS",
    # Satsuma cıvataları
    "TIGHTEN":    "BoltSyncPatch (Screw events)",
    "UNTIGHTEN":  "BoltSyncPatch (Screw events)",
    "SAVEGAME":   "AssemblyPatch (elenen)",
    "OFF":        "AssemblyPatch (elenen)",
    # Parça degiskenleri
    "Installed":  "SatsumaPart.InstalledVars / Data",
    "Bolted":     "SatsumaPart.GroupBolted",
    "Damaged":    "SatsumaPart.DbDamaged",
    "Stage":      "SatsumaBolt.Stage",
    "Tightness":  "SatsumaBolt.Tightness / GroupTightness",
    "PART1":      "AssemblyPatch.PART_EVENTS",
    "PART2":      "AssemblyPatch.PART_EVENTS",
    # Door yonu olcumu
    "Open":       "WorldObjectSync.FindDoorOpenBool",
    "Rot":        "WorldObjectSync.ReadDoorDirection",
    "Rotation":   "WorldObjectSync.ReadDoorDirection",
}

# Hangi FSM ailelerinde hangi adlar aranacak (fsm adi filtreleri)
FAMILIES = {
    "door":   ("Use", "UseOld", "Knob"),
    "part":   ("Assembly", "Removal", "Assemble", "BoltCheck"),
    "bolt":   ("Screw", "Data"),
}

# Panel/bolum yolları aranirken kullanilan ipuclari
PATH_HINTS = ("door", "hood", "bootlid", "fuelfiller", "grille", "trunk",
              "body_fuel_hatch", "bathroom", "kitchen", "garage")


def load(path):
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def norm(s):
    return "".join(c for c in s if c.isalnum()).lower()


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    fsms = load(sys.argv[1])
    print(f"{len(fsms)} FSM yu klendi.")

    # 1) TUM FSM'lerde geçen her ad, harf varyantlarıyla birlikte
    #    Bir "ad" = SADECE harf/rakam/_ içeren ve makul uzunlukta olan string.
    variants = collections.defaultdict(collections.Counter)
    for f in fsms:
        for s in f.get("strings", []):
            if not s or len(s) > 40:
                continue
            if not all(c.isalnum() or c == "_" for c in s):
                continue
            variants[norm(s)][s] += 1

    print("\n=== BEKLENEN ADIN HARF VARYANTLARI ===")
    problems = []
    for want in sorted(EXPECTED):
        key = norm(want)
        found = variants.get(key)
        if not found:
            continue                      # bu oyunda hic yok: sessiz, rapor edilmez
        if want in found:
            status = "TAM"
        else:
            status = "HATALI HARF"
            problems.append((want, found, EXPECTED[want]))
        got = ", ".join(f"{k!r}x{v}" for k, v in found.most_common(6))
        print(f"  {status:12} {want:12} -> {got}")

    # 2) Sadece KAPI ailesinde: olay adları ne olmalı?
    print("\n=== KAPI AILESINDE (Use/UseOld/Knob) GECEN OLAY BENZERI ADLAR ===")
    door_fsms = [f for f in fsms if f["fsm"] in FAMILIES["door"]]
    door_variants = collections.defaultdict(collections.Counter)
    for f in door_fsms:
        for s in f.get("strings", []):
            if not s or len(s) > 40:
                continue
            if not all(c.isalnum() or c == "_" for c in s):
                continue
            door_variants[norm(s)][s] += 1
    for want in ("USE", "OPEN", "CLOSE", "OPENDOOR", "DoorOpen", "Mouse off",
                 "Mouse over", "Check position", "Check if open", "PlayAnimation"):
        key = norm(want)
        found = door_variants.get(key)
        if found:
            got = ", ".join(f"{k!r}x{v}" for k, v in found.most_common(6))
            flag = "" if want in found else "   <<< MOD BASKA HARF YAZIYOR"
            print(f"  {want:18} -> {got}{flag}")

    # 3) PARCA ailesinde: Assembly/Removal/Assemble olayları
    print("\n=== PARCA AILESINDE (Assembly/Removal/Assemble) GECEN OLAYLAR ===")
    part_fsms = [f for f in fsms if f["fsm"] in FAMILIES["part"]]
    print(f"  (incelenen FSM: {len(part_fsms)})")
    ev = collections.Counter()
    for f in part_fsms:
        for s in f.get("strings", []):
            if not s or len(s) > 40:
                continue
            if not all(c.isalnum() or c == "_" for c in s):
                continue
            # olaya benzer: hepsi buyuk harf, kisaveya bilinen olay adi
            if s == s.upper() and len(s) >= 4:
                ev[s] += 1
    print("  en cok gecen BUYUK HARFLI olay adlari:")
    for name, n in ev.most_common(20):
        mark = ""
        if name in ("ASSEMBLE", "REMOVE", "DISASSEMBLE"):
            mark = "   <- mod bunu kullaniyor"
        print(f"    {n:5d}  {name}{mark}")

    # 4) CIVATA ailesinde: Stage/Tightness degiskenleri ve olaylar
    print("\n=== CIVATA AILESINDE (Screw/Data) DEGISKEN VE OLAYLAR ===")
    bolt_fsms = [f for f in fsms if f["fsm"] in FAMILIES["bolt"]]
    names = collections.Counter()
    for f in bolt_fsms:
        for s in f.get("strings", []):
            if not s or len(s) > 40:
                continue
            if not all(c.isalnum() or c == "_" for c in s):
                continue
            names[s] += 1
    print(f"  (incelenen FSM: {len(bolt_fsms)})")
    for want in ("Stage", "Tightness", "TIGHTEN", "UNTIGHTEN", "SAVEGAME", "OFF",
                 "Installed", "Bolted", "Damage"):
        n = names.get(want, 0)
        print(f"    {want:12} tam-eslesme {n}")
        # harf varyanti var mi?
        alt = [k for k in names if norm(k) == norm(want) and k != want]
        if alt:
            print(f"      <<< baska varyantlar da geciyor: {alt}")

    # 5) OZET
    print("\n=== OZET ===")
    if problems:
        print("  HARF HATASI OLARAK GORULENLER:")
        for want, found, where in problems:
            print(f"    {want:12} (modde: {where})")
            print(f"                oyunda: {', '.join(repr(k) for k in found)}")
    else:
        print("  Harf hatasi bulunamadi.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
