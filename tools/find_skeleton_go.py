import UnityPy, os, glob
base = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
files = [os.path.join(base, n) for n in ("level0","level1","level2","level3","mainData")]
files += sorted(glob.glob(os.path.join(base, "sharedassets*.assets")))
files += [os.path.join(base, "resources.assets")]
targets = {"skeleton", "char", "thig_left", "thig_right", "pelvis"}
for f in files:
    if not os.path.isfile(f):
        continue
    env = UnityPy.load(f)
    found = []
    for p in env.objects:
        if p.type.name != "GameObject":
            continue
        try:
            g = p.read()
        except Exception:
            continue
        n = getattr(g, "m_Name", "")
        if n in targets:
            found.append(n)
    if found:
        print(os.path.basename(f), "->", sorted(set(found)), "count", len(found))
