# -*- coding: utf-8 -*-
"""Bundle icindeki prefab'i ac: hiyerarsi, SkinnedMeshRenderer, Animation."""
import sys
import UnityPy

path = sys.argv[1] if len(sys.argv) > 1 else r"D:\modmscMP\MSCSteamMP\unity\MPNPCBuilder\mpnpc"
env = UnityPy.load(path)

# Tip envanteri
kinds = {}
for o in env.objects:
    kinds[o.type.name] = kinds.get(o.type.name, 0) + 1
print("TIPLER:", ", ".join("%s=%d" % (k, v) for k, v in sorted(kinds.items())))
print()

# GameObject'lar
gos = {}
for o in env.objects:
    if o.type.name != "GameObject":
        continue
    try:
        gos[o.path_id] = o.read()
    except Exception:
        pass
print("GAMEOBJECT SAYISI:", len(gos))
for pid, g in gos.items():
    comps = []
    for c in (getattr(g, "m_Component", []) or []):
        ptr = getattr(c, "component", c)
        try:
            o = ptr.read()
            comps.append(getattr(o, "type", None))
        except Exception:
            comps.append("?")
    print("  %-22s (%3d) component: %s" % (
        getattr(g, "m_Name", "?"), pid, ", ".join(str(c) for c in comps)))

print()
for o in env.objects:
    if o.type.name == "SkinnedMeshRenderer":
        s = o.read()
        print("SKINNEDMESHRENDERER")
        print("  mesh      :", getattr(deref(s.m_Mesh), "m_Name", "?") if True else "?")
        print("  kemik     :", len(s.m_Bones or []))
        print("  rootBone  :", s.m_RootBone.m_PathID if s.m_RootBone else None)
        print("  materyaller:", [m for m in getattr(s, "m_Materials", [])])
        print("  AABB      :", s.m_AABB)
    if o.type.name == "Animation":
        a = o.read()
        clips = []
        for it in (getattr(a, "m_Animations", None) or []):
            if isinstance(it, (list, tuple)):
                it = it[0]
            try:
                c = it.read()
                clips.append(getattr(c, "m_Name", "?"))
            except Exception:
                pass
        print("ANIMATION")
        print("  clip sayisi:", len(clips))
        print("  clip'ler  :", ", ".join(sorted(clips)))
        print("  culling   :", getattr(a, "m_CullingMode", "?"))
    if o.type.name == "AnimationClip":
        c = o.read()
        print("  CLIP %-10s legacy=%s len=%.2f wrap=%s frameRate=%s" % (
            getattr(c, "m_Name", "?"), getattr(c, "m_Legacy", "?"),
            getattr(c, "m_Length", 0) or 0, getattr(c, "m_WrapMode", "?"),
            getattr(c, "m_SampleRate", "?")))
    if o.type.name == "Mesh":
        m = o.read()
        print("MESH %s  verts=%d" % (getattr(m, "m_Name", "?"),
                                     len(getattr(m, "m_VertexData").m_Channels) and
                                     getattr(m, "m_VertexData").m_VertexCount or 0))