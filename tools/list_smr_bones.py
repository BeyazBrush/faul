import UnityPy, os
base = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(os.path.join(base, "level2"))

tr = {}
for p in env.objects:
    if p.type.name == "Transform":
        try:
            t = p.read()
            tr[p.path_id] = getattr(t, "m_Name", "")
        except Exception:
            tr[p.path_id] = "?"

n = 0
for p in env.objects:
    if p.type.name != "SkinnedMeshRenderer":
        continue
    s = p.read()
    go = None
    try:
        go = getattr(s.m_GameObject.read(), "m_Name", "?")
    except Exception:
        go = "?"
    bnames = []
    try:
        for b in s.m_Bones:
            try:
                bt = b.read()
                bnames.append(getattr(bt, "m_Name", "?"))
            except Exception:
                bnames.append("<x>")
    except Exception as e:
        bnames.append("<bones ERR %s>" % e)
    try:
        mesh = getattr(s.m_Mesh.read(), "m_Name", "?")
    except Exception:
        mesh = "<mesh ERR>"
    print("%-30s mesh=%-22s bones=%d  %s" % (go[:30], mesh[:22], len(bnames), ", ".join(bnames[:8])))
    n += 1
    if n >= 15:
        break
