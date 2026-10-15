"""
Modun NESNE ADI listelerini level2'nin gercek hiyerarsisine karsi denetler.

Onemli: `RadioVolume` gibi adlar bir FSM adi DEGIL, bir GameObject adidir.
Onceki denetleyiciler yalnizca FSM blob'undaki string'lere baktigi icin
bunlari "yok" dedi -- yanlis alarm. Gercek kaynak hierarchy.txt'dir.

Kullanim:
    python check_objects.py dump2/hierarchy.txt <src/MSCSteamMP>
"""
import collections
import os
import re
import sys

# Modun nesne adi listeleri (kaynaktan okunur ama burada acik yaziyoruz)
OBJECT_LISTS = {
    "KnownVehicleNames": ["JONNEZ ES(Clone)", "HAYOSIKO(1500kg, 250)",
                          "SATSUMA(557kg, 248)", "RCO_RUSCKO12(270)",
                          "KEKMET(350-400psi)", "FLATBED",
                          "FERNDALE(1630kg)", "GIFU(750/450psi)"],
    "RADIO_OBJECTS": ["RadioTuner", "RadioVolume", "RadioCDSwitch",
                      "RadioPivot", "trigger_cd", "cd player(Clone)"],
    "CAR_CONTROL_OBJECTS": ["IgnitionSatsuma", "trigger_disc",
                            "RadioCDSwitch", "TrackChannelSwitch",
                            "ButtonHorn", "ButtonLights", "ButtonWipers",
                            "ButtonInteriorLight"],
    "SWITCH_OBJECTS": ["MainSwitch", "Switch", "Light", "Lights",
                       "SetFire", "Cord"],
    "TAP_OBJECTS": ["WaterTap", "TapDrink", "FuelTap", "PivotWaterPress"],
    "PANEL_HINT_WORDS": ["door", "hood", "bootlid", "bonnet", "hatch",
                         "trunk", "fuelfiller", "body_fuel_hatch",
                         "hoodlocking", "glovbox"],
}

# Hangi arac hangi emareleri tasiyor (log/dump'tan dogrulanacak)
VEHICLE_MUST_HAVE = {
    "HAYOSIKO": ["DriverDoors", "RadioPivot", "Starter", "Dashboard",
                 "ButtonWipers", "ButtonHorn", "SideDoor", "RearDoor"],
    "SATSUMA": ["RadioPivot", "Starter", "Dashboard", "Body",
                "pivot_door_left", "pivot_hood", "pivot_bootlid"],
}


def leaf(path):
    return path.rsplit("/", 1)[-1]


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    paths = [l.rstrip("\n") for l in open(sys.argv[1], encoding="utf-8",
                                          errors="replace") if l.strip()]
    print(f"{len(paths)} nesne yolu okundu.\n")

    # Yaprak ad -> yollar (ilk 3)
    by_leaf = collections.defaultdict(list)
    for p in paths:
        by_leaf[leaf(p)].append(p)

    print("=== 1) MODUN NESNE ADI LISTELERI (gercek hiyerarside var mi?) ===")
    for label, items in OBJECT_LISTS.items():
        print(f"  {label}:")
        for it in items:
            hit = by_leaf.get(it)
            if hit:
                print(f"    OK    {it:24} ({len(hit)} nesne)  or: {hit[0][:70]}")
            else:
                print(f"    YOK   {it}")

    # 2) Araclar gercekten o emarelere sahip mi?
    print("\n=== 2) ARAC EMARELERI ===")
    for veh, musts in VEHICLE_MUST_HAVE.items():
        print(f"  {veh}:")
        for m in musts:
            hit = [p for p in paths if p.startswith(veh) and m in p]
            if hit:
                print(f"    OK    {m:22} {hit[0][:78]}")
            else:
                print(f"    YOK   {m}")

    # 3) HAYOSIKO kapilari: modun kapi filtrelerinden geciyor mu?
    print("\n=== 3) HAYOSIKO KAPILARI (door filtresi) ===")
    panel_words = OBJECT_LISTS["PANEL_HINT_WORDS"]
    doors = [p for p in paths if p.startswith("HAYOSIKO")
             and any(w in p.lower() for w in ("door", "hatch", "lid"))]
    for d in doors:
        low = d.lower()
        reasons = []
        if "fuelfiller" in low or "body_fuel_hatch" in low:
            reasons.append("tier2 fuel-hatch")
        if "hoodlocking" in low or "glovbox" in low:
            reasons.append("tier2 ozel")
        if any(w in low for w in panel_words):
            reasons.append("tier3 door-name")
        print(f"    {d[:88]}")
        print(f"      -> {', '.join(reasons) if reasons else '!!! UYUSAN KURAL YOK'}")

    # 4) Arac koku adlari: modun bildigi adlar gercekte var mi?
    print("\n=== 4) SAHNE KOKLERI (modun KnownVehicleNames ile) ===")
    roots = set(p.split("/", 1)[0] for p in paths)
    for r in sorted(roots):
        if any(k in r.upper() for k in ("GIFU", "HAYOSIKO", "SATSUMA", "KEKMET",
                                        "COMBINE", "FLATBED", "FERNDALE",
                                        "RUSCKO", "JONNEZ", "CARPARTS")):
            print(f"    {r}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
