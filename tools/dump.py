"""
MSC asset dumper.

Usage:
    python dump.py <level-or-assets-file> [--fsm] [--out DIR] [--find TEXT]

Produces text/JSON that can be read directly, so a scene or FSM can be inspected
without launching the game.

Why a custom parser is needed for PlayMaker:
Unity stripped the type trees for MonoBehaviours in this build, so UnityPy cannot
read a PlayMakerFSM's fields. The serialized blob is still there, and PlayMaker's
own field order is known from PlayMaker.dll, so the FSM header is recovered by
skipping four strings and reading the name and start state.
"""
import collections
import json
import os
import re
import struct
import sys

import UnityPy

GAME = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
TRACKED_FSM_PREFIXES = ("Assembly", "Removal", "Assemble", "BoltCheck", "Screw", "Data")


def align4(n):
    return (n + 3) & ~3


def read_str(buf, off):
    """Length-prefixed string in Unity's binary format. Returns (str, next_off) or (None, off)."""
    if off + 4 > len(buf):
        return None, off
    n = struct.unpack_from("<i", buf, off)[0]
    if n < 0 or n > 4096 or off + 4 + n > len(buf):
        return None, off
    raw = buf[off + 4:off + 4 + n]
    if any(c < 9 or (13 < c < 32) for c in raw):
        return None, off
    off = align4(off + 4 + n)
    try:
        return raw.decode("utf-8"), off
    except Exception:
        return None, off


def all_strs(buf, minlen=2, maxlen=80):
    """Every length-prefixed string in the blob, in file order."""
    out = []
    i, n = 0, len(buf)
    while i + 4 <= n:
        ln = struct.unpack_from("<i", buf, i)[0]
        if minlen <= ln <= maxlen and i + 4 + ln <= n:
            raw = buf[i + 4:i + 4 + ln]
            if all(32 <= c < 127 for c in raw):
                out.append((i, raw.decode("ascii")))
                i += 4 + ln
                i = (-i) % 4 + i
                continue
        i += 4
    return out


def fsm_header(buf):
    """
    Recover (fsmName, startState) from a PlayMakerFSM blob.

    Layout after the MonoBehaviour header is: four strings, then the FSM name, then
    the start state. Verified against every FSM in level2.
    """
    off = 12
    off = align4(off)
    off += 1                       # m_Enabled
    off = align4(off)
    off += 12                      # m_Script PPtr
    off = align4(off)
    seq = []
    for _ in range(6):
        s, off = read_str(buf, off)
        if s is None:
            break
        seq.append(s)
    if len(seq) < 5:
        return None
    return seq[4], (seq[5] if len(seq) > 5 else ""), seq


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 1
    target = args[0]
    want_fsm = "--fsm" in args
    find = None
    if "--find" in args:
        find = args[args.index("--find") + 1]
    out = None
    if "--out" in args:
        out = args[args.index("--out") + 1]

    path = target if os.path.isabs(target) else os.path.join(GAME, target)
    if not os.path.isfile(path):
        print("not found:", path)
        return 1

    print("loading", path, f"({os.path.getsize(path) // 1_000_000} MB)", flush=True)
    env = UnityPy.load(path)

    names, tr = {}, {}
    for obj in env.objects:
        t = obj.type.name
        if t == "GameObject":
            names[obj.path_id] = obj.read().m_Name
        elif t == "Transform":
            d = obj.read()
            tr[obj.path_id] = (d.m_GameObject.path_id, d.m_Father.path_id)
    go_tr = collections.defaultdict(list)
    children = collections.defaultdict(list)
    for tid, (g, f) in tr.items():
        go_tr[g].append(tid)
        children[f].append(tid)

    def path_of(g):
        parts = []
        t = go_tr[g][0] if go_tr[g] else None
        guard = 0
        while t is not None and guard < 64:
            parts.append(names.get(tr[t][0], "?"))
            f = tr[t][1]
            if f == 0:
                break
            t = f
            guard += 1
        return "/".join(reversed(parts))

    # ---------- FSM inventory ----------
    fsms = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        buf = obj.get_raw_data()
        if b"HutongGames.PlayMaker.Actions" not in buf and not want_fsm:
            continue
        if b"HutongGames.PlayMaker.Actions" not in buf:
            continue
        hdr = fsm_header(buf)
        if not hdr:
            continue
        fsm_name, start, _ = hdr
        gid = struct.unpack_from("<iq", buf, 0)[1]
        fsms.append({
            "go": names.get(gid, "?"),
            "path": path_of(gid),
            "fsm": fsm_name,
            "start": start,
            "strings": [s for _, s in all_strs(buf)],
            "size": len(buf),
        })

    print(f"\nPlayMakerFSM objects: {len(fsms)}")
    c = collections.Counter(f["fsm"] for f in fsms)
    print("distinct FSM names:", len(c))
    for k, v in c.most_common(25):
        print(f"  {v:5d}  {k}")

    if find:
        needle = find.lower()
        print(f"\n=== FSMs matching {find!r} ===")
        for f in fsms:
            if needle in f["fsm"].lower() or needle in f["go"].lower() or needle in f["path"].lower():
                print(f"  {f['fsm']!r:22} start={f['start']!r:26} {f['path']}")

    if want_fsm and out:
        os.makedirs(out, exist_ok=True)
        p = os.path.join(out, "fsms.json")
        with open(p, "w", encoding="utf-8") as fh:
            json.dump(fsms, fh, indent=1)
        print("wrote", p, f"({len(fsms)} entries)")

    # ---------- scene / object inventory ----------
    if out:
        os.makedirs(out, exist_ok=True)
        rows = []
        for g, n in sorted(names.items(), key=lambda kv: path_of(kv[0])):
            rows.append(path_of(g))
        p = os.path.join(out, "hierarchy.txt")
        with open(p, "w", encoding="utf-8") as fh:
            fh.write("\n".join(rows))
        print("wrote", p, f"({len(rows)} objects)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
