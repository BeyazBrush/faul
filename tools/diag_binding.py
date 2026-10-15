import UnityPy, os
base = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(os.path.join(base, "sharedassets3.assets"))
for p in env.objects:
    if p.type.name != "AnimationClip":
        continue
    c = p.read()
    if getattr(c, "m_Name", "") != "fat_get_car_in":
        continue

    bc = c.m_ClipBindingConstant
    print("BINDING CONSTANT fields:")
    for k in [k for k in dir(bc) if k.startswith("m_")]:
        try:
            v = getattr(bc, k)
        except Exception as e:
            print("  ", k, "ERR", e); continue
        t = type(v).__name__
        extra = ""
        if hasattr(v, "__len__") and not isinstance(v, str):
            try: extra = " len=%d" % len(v)
            except Exception: pass
        print("  %-32s %s%s" % (k, t, extra))
        if extra and extra.endswith(("=%d" % len(v))) and len(v) <= 40:
            try: print("      ", list(v)[:40])
            except Exception: pass

    print("\nROTATION CURVES:")
    for rc in c.m_RotationCurves:
        print("   type=%s attr=%s path=%r" % (type(rc).__name__,
              getattr(rc, "attribute", None), getattr(rc, "path", None)))
    print("\nPOSITION CURVES:")
    for pc in c.m_PositionCurves:
        print("   type=%s attr=%s path=%r" % (type(pc).__name__,
              getattr(pc, "attribute", None), getattr(pc, "path", None)))
    break
