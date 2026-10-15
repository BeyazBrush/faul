# -*- coding: utf-8 -*-
"""NPC materyal/doku envanteri: bodymesh SMR'larinin materyalleri + texture'leri."""
import UnityPy, os
DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


env = UnityPy.load(os.path.join(DATA, "level2"))
cs_env = UnityPy.load(os.path.join(DATA, "sharedassets3.assets"))

# Texture2D'leri topla (tum dosyalardan)
tex = {}
for e in (cs_env, env):
    for o in e.objects:
        if o.type.name in ("Texture2D", "Texture"):
            try:
                t = o.read()
                tex[(o.type.name, getattr(t, "m_Name", ""), getattr(o.assets_file, "name", ""))] = t
            except Exception:
                pass
print("Texture nesnesi:", len(tex))
tex_by_name = {}
for (k, n, f), t in tex.items():
    tex_by_name.setdefault(n, []).append(t)

mats = {}
for e in (cs_env, env):
    for o in e.objects:
        if o.type.name == "Material":
            try:
                m = o.read()
            except Exception:
                continue
            mats.setdefault(getattr(m, "m_Name", ""), m)
print("Material nesnesi:", len(mats))

shown = 0
for o in env.objects:
    if o.type.name != "SkinnedMeshRenderer":
        continue
    try:
        s = o.read()
        g = deref(s.m_GameObject)
        gn = getattr(g, "m_Name", "")
        mn = getattr(deref(s.m_Mesh), "m_Name", "")
    except Exception:
        continue
    if not str(mn).startswith("bodymesh"):
        continue
    print("=" * 90)
    try:
        sm = len(deref(s.m_Mesh).m_SubMeshes)
    except Exception:
        sm = 0
    print("%s / mesh=%s  submesh=%d" % (gn, mn, sm))
    for mp in (s.m_Materials or []):
        m = deref(mp)
        if m is None:
            print("   material <cozulemedi> fileID=%s" % getattr(mp, "m_FileID", "?"))
            continue
        sh = deref(getattr(m, "m_Shader", None))
        shname = getattr(sh, "m_Path", "") if sh else "?"
        stex = ""
        try:
            sp = m.m_SavedProperties
            for tf in sp.m_TexEnvs:
                tid = getattr(tf, "second", None)
                pt = deref(tid) if tid is not None else None
                if pt is not None:
                    stex += "  %s -> %s" % (getattr(tf, "first", "?"),
                                           getattr(pt, "m_Name", "?"))
        except Exception as e:
            stex = "  <okunamadi %s>" % e
        print("   MAT %-28s shader=%-34s%s" % (getattr(m, "m_Name", "?"), str(shname)[:34], stex))
    shown += 1
    if shown >= 4:
        break

print("=" * 90)
print("Texture2D adlari (ilk 80):")
names = sorted(tex_by_name.keys())
for n in names[:80]:
    print("   ", n)
print("   ... toplam", len(names))