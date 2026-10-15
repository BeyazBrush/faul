"""
MSC olum / respawn sinyallerini asset taramasiyla bulur.

Neden C# degil: Assembly-CSharp'te oyuncu olum kodu YOK. Tum saglik/olum
durumu PlayMaker FSM degiskenlerinde tutuluyor (SaveBool/SaveFloat ile
kaydedilen sey oyunun kalici durumu demektir). O yuzden sahne asset'ini
taramamiz gerekiyor.

Kullanim:
    python find_death.py [--level level0..3] [--strings-only]
"""
import collections
import os
import re
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import UnityPy

GAME = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"

# Oyunun gercekten kullandigi kelimeler. Genel kelimeler ("damage", "hit")
# carpma sesleri ve esya FSM'lerinde binlerce kez geciyor, o yuzden
# ayirt edici olm zorunlu.
KEYS = [
    # olum
    "you died", "die", "died", "death", "dead", "kill", "killed",
    "game over", "gameover", "game_over",
    # vucut / yaralanma (permadeath'i bunlar tetikler)
    "blood", "bleed", "wound", "broken", "fracture", "hospital", "ambulance",
    "corpse", "funeral", "coffin", "grave", "cemetery", "perma",
    # hayati destek -> olume yol acanlar
    "starv", "dehydrat", "hypotherm", "unconscious", "passed out", "blackout",
]

# Ters gosterilecek: butun sistemi degil, oyuncuyla ilgili oldugu
# kesin olanlar. "kill" oyuncu icin disinda objeyi yok etmek anlamina da
# geliyor.
PLAYER_HINTS = ["player", "drunk", "drunkenness", "piss", "shit", "thirst",
                "stomach", "fatigue", "urinat", "blood", "pain"]


def align4(n):
    return (n + 3) & ~3


def read_str(buf, off):
    if off + 4 > len(buf):
        return None, off
    n = struct.unpack_from("<i", buf, off)[0]
    if n < 0 or n > 4096 or off + 4 + n > len(buf):
        return None, off
    raw = buf[off + 4: off + 4 + n]
    if any(c < 9 or (13 < c < 32) for c in raw):
        return None, off
    off = align4(off + 4 + n)
    try:
        return raw.decode("utf-8"), off
    except Exception:
        return None, off


def all_strs(buf, minlen=3, maxlen=90):
    out = []
    i, n = 0, len(buf)
    while i + 4 <= n:
        ln = struct.unpack_from("<i", buf, i)[0]
        if minlen <= ln <= maxlen and i + 4 + ln <= n:
            raw = buf[i + 4: i + 4 + ln]
            if all(32 <= c < 127 for c in raw):
                out.append((i, raw.decode("ascii")))
                i = 4 + ln
                i = (-i) % 4 + i
                continue
        i += 4
    return out


def main():
    levels = ["level0", "level1", "level2", "level3"]
    strings_only = "--strings-only" in sys.argv
    if "--level" in sys.argv:
        levels = [sys.argv[sys.argv.index("--level") + 1]]

    env = UnityPy.load(GAME)
    objs = list(env.objects)

    # path_id -> GameObject, ve GameObject -> ad (FSM adini baglamak icin)
    go_name = {}
    parents = {}
    for o in objs:
        if o.type.name != "GameObject":
            continue
        try:
            t = o.read_typetree()
        except Exception:
            continue
        go_name[o.path_id] = t.get("m_Name", "")
        parents[o.path_id] = t.get("m_Father", 0) if hasattr(t.get("m_Father", 0), "__int__") else 0

    def full_path(pid):
        parts = []
        seen = set()
        cur = pid
        while cur in go_name and cur not in seen and len(parts) < 12:
            seen.add(cur)
            parts.append(go_name[cur])
            cur = parents.get(cur, 0)
            if not cur:
                break
        return "/".join(reversed(parts))

    # MonoBehaviour blob'larini tara: PlayMakerFSM tip sayilir
    hits = collections.defaultdict(set)   # keyword -> {path}
    fsm_events = collections.defaultdict(set)  # path -> {event names}
    fsm_names = collections.defaultdict(set)

    for o in objs:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            data = o.read()
            raw = data.m_Script  # placeholder, gercek okuma asagida
        except Exception:
            pass
        try:
            blob = o.get_raw_data()
        except Exception:
            continue

        strs = all_strs(blob)
        if not strs:
            continue
        low = [s.lower() for _, s in strs]

        matched = set()
        for k in KEYS:
            for l in low:
                if k in l:
                    matched.add(k)
        if not matched:
            continue

        gp = o.component.game_object.path_id if getattr(o, "component", None) else None
        path = full_path(gp) if gp else f"(path_id {o.path_id})"

        for k in matched:
            hits[k].add(path)

        # FSM olay adlarini topla (buyuk harf, kisa, FSM adayi)
        for _, s in strs:
            if 2 <= len(s) <= 24 and re.fullmatch(r"[A-Z][A-Z0-9 _]{1,23}", s):
                if any(k in s.lower() for k in KEYS):
                    fsm_events[path].add(s)
            if s.startswith("fsm") or s in ("Death", "Die", "Dead", "Respawn", "GameOver"):
                fsm_names[path].add(s)

    print("=" * 70)
    print("OLUM/OLUME-YOL-ACAN KELIMELER -> NESNE YOLU")
    print("=" * 70)
    for k in KEYS:
        paths = hits.get(k)
        if not paths:
            continue
        print(f"\n--- {k!r}  ({len(paths)} nesne) ---")
        for p in sorted(paths)[:40]:
            print("   ", p)
        if len(paths) > 40:
            print(f"    ... +{len(paths) - 40} daha")

    print()
    print("=" * 70)
    print("OLUMLA ILGISIZ GORUNEN PLAYMAKER OLAYLARI")
    print("=" * 70)
    for path, evs in sorted(fsm_events.items()):
        if len(evs) < 1:
            continue
        # yalnizca oyuncu ipuclari olanlari goster
        lowpath = path.lower()
        if not any(h in lowpath for h in PLAYER_HINTS):
            continue
        print(f"  {path}")
        for e in sorted(evs):
            print(f"       event: {e}")

    print()
    print("=" * 70)
    print("FSM ADLARI (Death/Die/Respawn/GameOver gecenler)")
    print("=" * 70)
    for path, ns in sorted(fsm_names.items()):
        print(f"  {path}: {sorted(ns)}")


if __name__ == "__main__":
    main()