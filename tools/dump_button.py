"""
What does ButtonContinue actually do?

Finds the FSM on the Continue button and dumps every PlayMaker action and event it
declares, so the scene load can be identified instead of guessed.
"""
import collections
import os
import struct
import sys

import UnityPy

GAME = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"


def align4(n):
    return (n + 3) & ~3


def read_str(buf, off):
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


def all_strs(buf, minlen=2, maxlen=90):
    out = []
    i, n = 0, len(buf)
    while i + 4 <= n:
        ln = struct.unpack_from("<i", buf, i)[0]
        if minlen <= ln <= maxlen and i + 4 + ln <= n:
            raw = buf[i + 4:i + 4 + ln]
            if all(32 <= c < 127 for c in raw):
                out.append((i, raw.decode("ascii")))
                i += 4 + ln
                i = align4(i)
                continue
        i += 4
    return out


path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(GAME, "level0")
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
for tid, (g, f) in tr.items():
    go_tr[g].append(tid)


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


def fsm_header(buf):
    off = 12
    off = align4(off)
    off += 1
    off = align4(off)
    off += 12
    off = align4(off)
    seq = []
    for _ in range(6):
        s, off = read_str(buf, off)
        if s is None:
            break
        seq.append(s)
    return (seq[4], seq[5] if len(seq) > 5 else "") if len(seq) >= 5 else None


INTEREST = ("ButtonContinue", "ButtonNewGame", "ButtonQuit", "Continue", "NewGame")

print("=== every FSM on the menu buttons ===")
for obj in env.objects:
    if obj.type.name != "MonoBehaviour":
        continue
    buf = obj.get_raw_data()
    hdr = fsm_header(buf)
    if not hdr:
        continue
    gid = struct.unpack_from("<iq", buf, 0)[1]
    p = path_of(gid)
    if not any(k in p for k in INTEREST):
        continue
    fsm, start = hdr
    ss = all_strs(buf)
    actions = sorted(set(s for _, s in ss if s.startswith("HutongGames.PlayMaker.Actions.")))
    # the FSM's own event and state names are the non-action, non-field tokens
    fields = set()
    for _, s in ss:
        fields.add(s)
    print(f"\n--- {p}")
    print(f"    FSM {fsm!r}  start={start!r}  size={len(buf)}")
    print("    actions:")
    for a in actions:
        print("       ", a.split(".")[-1])
    # tokens that look like scene names
    cand = [s for _, s in ss
            if s and s[0].isupper() and " " not in s and "." not in s
            and not s.startswith("HutongGames")
            and len(s) > 2 and len(s) < 40]
    interesting = [s for s in cand
                   if any(k in s.lower() for k in
                          ("load", "level", "scene", "new", "game", "continue", "yard", "save", "world", "start"))]
    if interesting:
        print("    scene-ish tokens:", sorted(set(interesting)))

print()
print("=== does ANY FSM in this scene reference a scene load? ===")
for obj in env.objects:
    if obj.type.name != "MonoBehaviour":
        continue
    buf = obj.get_raw_data()
    for a in (b"LoadScene", b"LoadLevel", b"SceneManager", b"LoadSceneAsync"):
        if a in buf:
            gid = struct.unpack_from("<iq", buf, 0)[1]
            hdr = fsm_header(buf)
            print(f"  {a.decode():16} in {path_of(gid)}  fsm={hdr[0] if hdr else '?'}")
