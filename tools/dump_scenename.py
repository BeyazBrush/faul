"""
Recover the scene name ButtonContinue passes to LoadLevel.

The action's parameters live in the FSM blob, but Unity stripped the type tree so the
field layout is unknown. The scene name is a plain string in the blob, so: read the
actionData for the LoadLevel entry, print every string in the surrounding region, and
look for a token that names a scene. The build settings tell us the real names, so the
candidate is cross-checked against what actually exists in the game folder.
"""
import collections
import os
import re
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


def all_strs(buf, minlen=2, maxlen=120):
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


env = UnityPy.load(os.path.join(GAME, "level0"))

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


target = None
for obj in env.objects:
    if obj.type.name != "MonoBehaviour":
        continue
    buf = obj.get_raw_data()
    if b"HutongGames.PlayMaker.Actions.LoadLevel" not in buf:
        continue
    gid = struct.unpack_from("<iq", buf, 0)[1]
    if path_of(gid).endswith("Interface/Buttons/ButtonContinue"):
        target = (gid, buf)
        break

if not target:
    print("ButtonContinue LoadLevel not found")
    sys.exit(1)

gid, buf = target
print("ButtonContinue blob:", len(buf), "bytes")

ss = all_strs(buf)
i_load = None
for idx, (off, s) in enumerate(ss):
    if s == "HutongGames.PlayMaker.Actions.LoadLevel":
        i_load = idx
        break
print("LoadLevel string index:", i_load)

# Everything in the blob, with the LoadLevel neighbourhood marked.
print("\n=== strings around the LoadLevel entry ===")
lo = max(0, i_load - 12)
hi = min(len(ss), i_load + 40)
for k in range(lo, hi):
    off, s = ss[k]
    mark = "  <== LoadLevel" if k == i_load else ""
    print(f"  {k:4d} @{off:6d}  {s!r}{mark}")

# Scene names actually present in the build, as a cross-check.
print("\n=== scene files that exist ===")
for f in sorted(os.listdir(GAME)):
    if f.startswith("level"):
        print("   ", f)

# And the strings that look like scene names at all.
print("\n=== every non-action, non-field token (scene-name candidates) ===")
skip = set("""gameObject everyFrame lateUpdate vector space quaternion xAngle yAngle zAngle
intVariable boolVariable floatVariable variableName fsmName isTrue isFalse equal
lessThan greaterThan tolerance storeResult integer1 integer2 active true false
value name events transitions actions states""".split())
for off, s in ss:
    if s in skip or s.startswith("HutongGames") or s.startswith("UnityEngine"):
        continue
    if not s or s[0].islower():
        continue
    if "." in s or " " in s:
        continue
    if len(s) > 3:
        print(f"   @{off:6d}  {s!r}")
