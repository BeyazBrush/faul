"""
Sadece GERCEKTEN FSM adi olan string'leri kontrol eder:
  1) `static readonly string[] NAME = { ... }` dizilerinin her ogesi
  2) `FsmNames.X(...)`, `GetFsmX(...)`, `HasEvent(...)` cagrilarindaki
     string literalleri

Amac: modun bir FSM olay/degisken adini yanlis harfle veya hic
olmayan bir adla aramasini bulmak. PlayMaker'in `FindFsmBool` ve
`HasEvent` metotlari `==` ile karsilastirir (dnSpy ile dogrulandi),
yani yanlis harf = sessiz "yok".

Kullanim:
    python check_fsm_names.py <dump2/fsms.json> <src/MSCSteamMP>
"""
import collections
import json
import os
import re
import sys

# FSM adi arayan moduller (digerleri arayuz/oyun-ici isim, FSM degil)
FSM_FILES = (
    "AssemblyPatch.cs", "BoltSyncPatch.cs", "SatsumaRegistry.cs",
    "WorldObjectSync.cs", "EconomySync.cs", "FluidWearSync.cs",
    "EnvSync.cs", "JobSync.cs", "CdPlayerSync.cs", "CdDiscSync.cs",
    "RadioSync.cs", "FsmBinder.cs", "DashboardPartSync.cs",
    "BatteryKeepFull.cs", "BatteryDrainHook.cs", "SyncedVehicle.cs",
    "PartSync.cs", "CarrySync.cs", "ItemSync.cs", "RecoveryManager.cs",
    "HeldItemSync.cs", "GarageEntitySync.cs", "AIVehicleSync.cs",
    "SatsumaPartTransform.cs", "SaveTags.cs", "SatsumaFsmProbe.cs",
    "CustomRadio.cs", "CdMusicImport.cs", "FsmInspector.cs",
)

# Dizi baslaticisi: static readonly string[] X = { "a", "b", ... };
ARRAY_RE = re.compile(
    r'(?:static\s+)?(?:readonly\s+)?(?:public\s+|private\s+|internal\s+)?'
    r'string\s*\[\s*\]\s*(\w+)\s*=\s*\{([^}]*)\}')

# FsmNames.X(fsm, "ad")  /  fsm.Fsm.HasEvent("ad")  /  GetFsmFloat("ad")
CALL_RE = re.compile(
    r'\b(?:FsmNames|FsmVars)\s*\.\s*(HasEvent|Bool|Int|Float|Str|Obj|Globals)\s*\([^,()]*,\s*"([^"]{3,40})"'
    r'|\bGetFsm(Float|Int|Bool|String|GameObject)\s*\(\s*"([^"]{3,40})"'
    r'|\.Fsm\s*\.\s*HasEvent\s*\(\s*"([^"]{3,40})"')

NAMEISH = re.compile(r'^[A-Za-z_][A-Za-z0-9_ ]{2,31}$')


def norm(s):
    return "".join(c for c in s if c.isalnum()).lower()


def scan(src):
    arrays = collections.defaultdict(list)   # (dizi adi) -> [(ad, dosya, satir)]
    calls = collections.defaultdict(list)    # ad -> [(dosya, satir)]
    for root, _dirs, files in os.walk(src):
        for fn in files:
            if fn not in FSM_FILES:
                continue
            p = os.path.join(root, fn)
            with open(p, "r", encoding="utf-8", errors="replace") as fh:
                text = fh.read()
            # yorumlari at
            text = re.sub(r'//[^\n]*', '', text)
            text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
            lines = text.split("\n")

            for m in ARRAY_RE.finditer(text):
                arr = m.group(1)
                for item in re.finditer(r'"((?:[^"\\]|\\.){3,40})"', m.group(2)):
                    s = item.group(1)
                    if NAMEISH.match(s):
                        # kacinci satirda tahmini olarak bul
                        ln = text[:m.start()].count("\n") + 1
                        arrays[arr].append((s, fn, ln))

            for i, line in enumerate(lines, 1):
                for m in CALL_RE.finditer(line):
                    s = next((g for g in m.groups() if g and not g.islower()
                              or (g and g[0].isupper())), None)
                    s = None
                    for g in m.groups():
                        if g and NAMEISH.match(g):
                            s = g
                            break
                    if s:
                        calls[s].append((fn, i))
    return arrays, calls


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    with open(sys.argv[1], "r", encoding="utf-8") as fh:
        fsms = json.load(fh)

    variants = collections.defaultdict(collections.Counter)
    for f in fsms:
        for s in f.get("strings", []):
            if not s or len(s) > 40:
                continue
            if not all(c.isalnum() or c == "_" or c == " " for c in s):
                continue
            variants[norm(s)][s] += 1

    arrays, calls = scan(sys.argv[2])

    print("=== 1) MODUN DIZILERI (her oge dump'ta var mi?) ===")
    total_bad = 0
    for arr in sorted(arrays):
        bad, tot = [], 0
        for s, fn, ln in arrays[arr]:
            tot += 1
            if s not in variants.get(norm(s), {}):
                bad.append((s, fn))
        if tot == 0:
            continue
        flag = ""
        if bad:
            flag = "   <<< " + ", ".join(f"{s!r} ({fn})" for s, fn in bad)
            total_bad += len(bad)
        print(f"  {arr:26} {tot - len(bad):3d}/{tot:3d} tam{flag}")

    print("\n=== 2) DOGRUDAN FsmNames/HasEvent CAGRI (string literalleri) ===")
    if not calls:
        print("  (bulunamadi)")
    bad_calls = []
    for s in sorted(calls):
        if s in variants.get(norm(s), {}):
            continue
        # Kapali cocuklar (icerinde bosluk/ozel) olarak gercek adlari goster
        where = ", ".join(f"{f}:{l}" for f, l in calls[s][:3])
        bad_calls.append((s, where, variants.get(norm(s))))
    if not bad_calls:
        print("  TÜM literaller dump'ta mevcut.")
    for s, where, found in bad_calls:
        print(f"  {s:26} {where}")
        if found:
            print(f"  {'':26} oyunda geceN varyantlar: "
                  + ", ".join(f"{k!r}x{v}" for k, v in found.most_common(4)))

    print(f"\n=== OZET: {total_bad} dizi ogesi + {len(bad_calls)} cagri literali eslesmedi ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
