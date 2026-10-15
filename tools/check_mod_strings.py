"""
Modun C# kaynagindaki FSM olay/degisken adi string'lerini cikarir ve
level2 dump'unda bulunup bulunmadigini kontrol eder.

Neden: mod bir FSM olay/degisken adini YANLIS HARFLE veya HIC OLMAYAN bir
adla ararsa, PlayMaker `FindFsmBool`/`HasEvent` (ikisi de `==` ile
karsilastirir, dnSpy ile dogrulandi) sessizce null dondurur ve o
senkron yolu HIC calismaz -- hata da gorunmez.

Kullanim:
    python check_mod_strings.py <dump2/fsms.json> <src/MSCSteamMP>
"""
import collections
import json
import os
import re
import sys

# Modun kendi tutarsizlik riski olan listeleri (dogrudan kaynaktan da
# okunabiliyor; burada elle listeliyoruz ki "neyi ariyoruz" acik olsun).
MOD_LISTS = {
    "AssemblyPatch.PART_EVENTS": ["ASSEMBLE", "REMOVE", "PART1", "PART2", "PART3",
                                  "PART4", "PART5", "PART6", "PART7"],
    "WorldObjectSync.DOOR_EVENTS": ["OPEN", "CLOSE", "OPENDOOR", "DoorOpen", "USE"],
    "WorldObjectSync.DOOR_FSMS": ["Use", "UseOld", "Knob"],
    "WorldObjectSync.CONTROL_EVENTS": ["OFF", "ON", "ACC", "USE", "START", "STOP"],
}

# Kaynaktan tarayacagimiz kalipli string literalleri (olay/degisken adaylari)
NAMEISH = re.compile(r'^[A-Za-z][A-Za-z0-9_]{2,31}$')


def norm(s):
    return "".join(c for c in s if c.isalnum()).lower()


def scan_source(src):
    """Kaynak agactan FSM adi olabilecek string literalleri topla."""
    out = collections.defaultdict(list)   # name -> [(file, line), ...]
    for root, _dirs, files in os.walk(src):
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            p = os.path.join(root, fn)
            try:
                with open(p, "r", encoding="utf-8", errors="replace") as fh:
                    for i, line in enumerate(fh, 1):
                        for m in re.finditer(r'"((?:[^"\\]|\\.){3,40})"', line):
                            s = m.group(1)
                            if not NAMEISH.match(s):
                                continue
                            # Yorum satirini atla: yalnizca kod satirlari
                            stripped = line.lstrip()
                            if stripped.startswith("//") or stripped.startswith("*"):
                                continue
                            out[s].append((os.path.basename(p), i))
            except Exception:
                pass
    return out


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    with open(sys.argv[1], "r", encoding="utf-8") as fh:
        fsms = json.load(fh)

    # dump'daki TUM string'ler (kucuk harfe indirgemis normal form -> varyantlar)
    variants = collections.defaultdict(collections.Counter)
    for f in fsms:
        for s in f.get("strings", []):
            if not s or len(s) > 40:
                continue
            if not all(c.isalnum() or c == "_" for c in s):
                continue
            variants[norm(s)][s] += 1

    mod = scan_source(sys.argv[2])
    print(f"{len(mod)} farkli ad modda gecmis; dump'ta karsilastiriliyor.\n")

    missing, wrongcase = [], []
    for name in sorted(mod):
        key = norm(name)
        found = variants.get(key)
        if not found:
            missing.append((name, mod[name]))
        elif name not in found:
            wrongcase.append((name, found))

    print("=== OYUNDA HIC OLMAYAN ADLAR (mod arıyor, oyun bilmiyor) ===")
    if not missing:
        print("  yok")
    for name, where in missing:
        files = ", ".join(f"{f}:{l}" for f, l in where[:3])
        print(f"  {name:24} {files}")

    print("\n=== FARKLI HARFLE GECEN ADLAR ===")
    if not wrongcase:
        print("  yok")
    for name, found in wrongcase:
        got = ", ".join(f"{k!r}x{v}" for k, v in found.most_common(4))
        files = ", ".join(f"{f}:{l}" for f, l in mod[name][:2])
        print(f"  {name:24} oyunda {got}")
        print(f"  {'':24} modde {files}")

    # Modun kendi listeleri: her oge dump'ta var mi?
    print("\n=== MODUN KENDI LISTELERI ===")
    for label, items in MOD_LISTS.items():
        bad = []
        for it in items:
            if it not in variants.get(norm(it), {}):
                bad.append(it)
        print(f"  {label:34} {len(items) - len(bad)}/{len(items)} tam"
              + (f"   EKSIK/YANLIS: {bad}" if bad else ""))

    return 0


if __name__ == "__main__":
    sys.exit(main())
