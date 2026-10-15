# -*- coding: utf-8 -*-
"""
npc_runtime_probe.py
Bir NPC'nin SAHNE HIYERARSISINI ve Animation component'ini cikarir.
Amac: runtime'da NPC'yi klonlayip multiplayer karakter olarak kullanmak.
"""
import UnityPy, os, sys
from collections import defaultdict

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
TARGET = sys.argv[1] if len(sys.argv) > 1 else "STORE/TeimoInShop/Pivot/Teimo"


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


env = UnityPy.load(os.path.join(DATA, "level2"))

# Transform + GameObject birlestir
tr_name = {}
tr_parent = {}
go_tr = {}          # go.path_id -> (tr.path_id, name)
for o in env.objects:
    if o.type.name != "Transform":
        continue
    t = o.read()
    gname = getattr(t.m_GameObject.read(), "m_Name", "")
    pid = o.path_id
    tr_name[pid] = gname
    tr_parent[pid] = getattr(t.m_Father, "path_id", 0)
    try:
        go_tr[t.m_GameObject.path_id] = pid
    except Exception:
        pass


def path_of(pid):
    parts, g = [], 0
    while pid in tr_name and g < 128:
        parts.append(tr_name[pid])
        pid = tr_parent.get(pid, 0)
        g += 1
    return "/".join(reversed(parts))


# hedef NPC'nin transform'ini bul
target_pid = None
for pid in tr_name:
    if path_of(pid) == TARGET:
        target_pid = pid
        break
if target_pid is None:
    print("BULUNAMADI:", TARGET)
    print("benzerleri:")
    for pid in tr_name:
        if TARGET.split("/")[-1] in path_of(pid):
            print("   ", path_of(pid))
    raise SystemExit(1)

print("HEDEF :", TARGET)
print("=" * 96)

# NPC kokunden yukariye: butun agaci yaz
def subtree(pid, depth=0):
    print("  " * depth + "- %-22s [%d]" % (tr_name.get(pid, "?"), pid))
    for o in env.objects:
        if o.type.name != "Transform":
            continue
        # cocuklar
    return


# cocuk listesi kur
kids = defaultdict(list)
for pid, par in tr_parent.items():
    kids[par].append(pid)

def walk(pid, depth=0):
    print("   " + "  " * depth + "%-24s pid=%-6d %s"
          % (tr_name.get(pid, "?"), pid, path_of(pid)))
    if depth > 6:
        return
    for c in sorted(kids.get(pid, [])):
        walk(c, depth + 1)

walk(target_pid)

# GameObject component'leri
print("=" * 96)
print("COMPONENT'LER")
for o in env.objects:
    if o.type.name != "GameObject":
        continue
    try:
        g = o.read()
    except Exception:
        continue
    pid = go_tr.get(o.path_id)
    if pid is None:
        continue
    p = path_of(pid)
    if not p.startswith(TARGET):
        continue
    comps = []
    for cp in (g.m_Component or []):
        c = deref(cp)
        if c is None:
            comps.append("<cozulemedi:%s>" % getattr(cp, "type", "?"))
            continue
        tn = type(c).__name__
        extra = ""
        if tn == "Animation":
            try:
                anims = c.m_Animations or []
                extra = "  clips=%d: %s" % (
                    len(anims), ", ".join(str(getattr(deref(a[0]), "m_Name", "?")) for a in anims[:14]))
                extra += "  | culling=%s playAutomatically=%s wrap=%s" % (
                    getattr(c, "m_CullingMode", "?"), getattr(c, "m_PlayAutomatically", "?"),
                    getattr(c, "m_WrapMode", "?"))
            except Exception as e:
                extra = "  <anim okunamadi %s>" % e
        if tn in ("Animator", "SkinnedMeshRenderer", "MeshRenderer", "MeshFilter",
                  "CharacterController", "AudioSource", "Rigidbody", "CapsuleCollider",
                  "BoxCollider", "SphereCollider", "Animation", "MonoBehaviour"):
            comps.append(tn + extra)
    if comps:
        print(" %-70s : %s" % (p, " | ".join(comps)))