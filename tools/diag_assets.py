import UnityPy, os, glob
from collections import Counter
base = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
files = [os.path.join(base, n) for n in ("level0","level1","level2","level3","mainData")]
files += sorted(glob.glob(os.path.join(base, "sharedassets*.assets")))
files += [os.path.join(base, "resources.assets")]
for f in files:
    if not os.path.isfile(f):
        continue
    try:
        env = UnityPy.load(f)
    except Exception as e:
        print(os.path.basename(f), "LOAD FAIL", e); continue
    c = Counter(p.type.name for p in env.objects)
    print("%-24s smr=%-4d mesh=%-4d clip=%-4d go=%-5d tr=%-5d anim=%-3d" % (
        os.path.basename(f), c.get("SkinnedMeshRenderer",0), c.get("Mesh",0),
        c.get("AnimationClip",0), c.get("GameObject",0), c.get("Transform",0),
        c.get("Animation",0)))
