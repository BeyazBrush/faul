import UnityPy, os
base = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(os.path.join(base, "sharedassets3.assets"))
for p in env.objects:
    if p.type.name != "AnimationClip":
        continue
    c = p.read()
    n = getattr(c, "m_Name", "")
    if any(k in n.lower() for k in ("get_car","bus_driver","car_in","car_out","fat_","woman_","granny_")):
        print("%-24s path=%r legacy=%s rate=%s" % (n, getattr(c,"m_Path",None),
              getattr(c,"m_Legacy",None), getattr(c,"m_FrameRate",None)))
        cs = []
        for fc in getattr(c, "m_FloatCurves", []):
            cs.append("%s.%s" % (getattr(fc,"path",""), getattr(fc,"attribute","")))
        print("     curves:", len(cs), "->", cs[:6])
