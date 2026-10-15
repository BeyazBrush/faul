"""
Confirm the scene name that ButtonContinue's LoadLevel uses.

The action's levelName parameter reads 'GAME'. 'GAME' has to be a build-settings
alias, because the scene files on disk are level0..level3. Confirm it by finding the
level -> name mapping, which Unity stores in globalgamemanagers (here mainData).
"""
import os
import re
import struct

GAME = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"

# Unity's scene list: m_Scenes array of {name, buildIndex, ...}. The names in this build
# are stored in a string table, so scan for the identifiers we expect.
for fname in ("mainData",):
    p = os.path.join(GAME, fname)
    b = open(p, "rb").read()
    print(f"{fname}: {len(b)} bytes")
    # every printable run of 3..40
    runs = []
    for m in re.finditer(rb"[\x20-\x7E]{3,40}", b):
        runs.append((m.start(), m.group().decode("ascii")))
    # dedupe preserving order
    seen = set()
    uniq = []
    for off, s in runs:
        if s not in seen:
            seen.add(s)
            uniq.append((off, s))
    print(f"  distinct tokens: {len(uniq)}")
    for off, s in uniq:
        low = s.lower()
        if any(k in low for k in ("game", "level", "scene", "yard", "start", "menu", "cont", "new")):
            print(f"    @{off:7d}  {s!r}")

print()
print("=== level file headers: first printable run after the version string ===")
for lvl in ("level0", "level1", "level2", "level3"):
    p = os.path.join(GAME, lvl)
    b = open(p, "rb").read(300)
    m = re.match(rb"([^\x00]{4,40})", b)
    print(f"  {lvl}: {m.group(1) if m else b[:40]!r}")
