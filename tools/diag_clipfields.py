import UnityPy, os
base = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(os.path.join(base, "sharedassets3.assets"))
for p in env.objects:
    if p.type.name != "AnimationClip":
        continue
    c = p.read()
    if getattr(c, "m_Name", "") != "fat_get_car_in":
        continue
    keys = [k for k in dir(c) if not k.startswith("_")]
    print("FIELDS:", keys)
    for k in keys:
        if k.startswith("m_"):
            try:
                v = getattr(c, k)
            except Exception as e:
                print("  ", k, "ERR", e); continue
            t = type(v).__name__
            extra = ""
            if hasattr(v, "__len__") and not isinstance(v, str):
                extra = " len=%d" % len(v)
            print("  %-34s %s%s" % (k, t, extra))
    break
