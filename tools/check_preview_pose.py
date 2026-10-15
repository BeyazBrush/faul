# -*- coding: utf-8 -*-
"""
check_preview_pose.py
Bundle'daki prefab'in kok donusumunu okuyup KARAKTERIN GERCEKTE hangi
eksende durdugunu hesaplar.

Onizleme kamerasi kimlig dondurumle bakar ve +Z'ye dogru bakar. Karakterin
"yuk" ekseni +Y degilse onizleme yatmis gorunur (ekran goruntusundeki
bozuk kadin).

Hesap:
  kok_rot = prefab kokunun localRotation
  bone yerel TRS'leri prefabdeki gibi
  head kemiginin DUNYA konumu = kok_rot * ... * head.localPosition
  up = normalize(headDunya - pelvisDunya)
"""
import sys

import UnityPy

env = UnityPy.load(sys.argv[1] if len(sys.argv) > 1 else r"D:\modmscMP\MSCSteamMP\assets\mpnpc")

go_name, tr_go, tr_parent, tr_local = {}, {}, {}, {}
for o in env.objects:
    if o.type.name == "GameObject":
        try:
            go_name[o.path_id] = o.read().m_Name
        except Exception:
            go_name[o.path_id] = "?"
    elif o.type.name == "Transform":
        t = o.read()
        pid = o.path_id
        tr_parent[pid] = t.m_Father.path_id
        tr_local[pid] = (t.m_LocalPosition, t.m_LocalRotation)
        tr_go[pid] = t.m_GameObject.path_id
        go_tr = getattr(t.m_GameObject, "path_id", -1)


def tr_path(pid):
    parts, g = [], 0
    while pid in tr_local and g < 64:
        parts.append(go_name.get(tr_go[pid], "?"))
        pid = tr_parent.get(pid, 0)
        g += 1
    return "/".join(reversed(parts))


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx = 2 * (y * vz - z * vy)
    ty = 2 * (z * vx - x * vz)
    tz = 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


roots = [p for p in tr_local if tr_parent.get(p, 0) not in tr_local]
print("kok(ler):", [(go_name.get(tr_go[p], "?"), tr_path(p)) for p in roots])
root = roots[0]


def q4(q):
    """Bundle'da m_LocalRotation bir Quaternionf NESNESI; duz tuple degil."""
    try:
        return (float(q.x), float(q.y), float(q.z), float(q.w))
    except Exception:
        return tuple(float(v) for v in q)


def v3(p):
    try:
        return (float(p.x), float(p.y), float(p.z))
    except Exception:
        return tuple(float(v) for v in p)


rq = q4(tr_local[root][1])
print()
print("PREFAB KOK ROTASYONU: (%.4f, %.4f, %.4f, %.4f)" % rq)
print("  aci (derece): %.1f" % (2 * 57.29578 *
      __import__("math").acos(max(-1.0, min(1.0, rq[3])))))

# pelvis -> head yolu
pelvis = head = None
for p in tr_local:
    n = go_name.get(tr_go[p], "")
    if n == "pelvis":
        pelvis = p
    if n == "head":
        head = p
if pelvis is None or head is None:
    print("pelvis/head bulunamadi")
    sys.exit(1)


def world_rot_and_pos(pid):
    """pid'ye kokten itibaren biriken donusum ve konum."""
    chain, cur, g = [], pid, 0
    while cur in tr_local and g < 64:
        chain.append(cur)
        cur = tr_parent.get(cur, 0)
        g += 1
    rot = (0, 0, 0, 1)
    pos = (0.0, 0.0, 0.0)
    for t in reversed(chain):
        lp, lq = tr_local[t]
        rot = qmul(rot, q4(lq))
        p = qrot(rot, v3(lp))
        pos = (pos[0] + p[0], pos[1] + p[1], pos[2] + p[2])
    return rot, pos


_, wp = world_rot_and_pos(pelvis)
_, wh = world_rot_and_pos(head)
up = (wh[0] - wp[0], wh[1] - wp[1], wh[2] - wp[2])
L = (up[0] ** 2 + up[1] ** 2 + up[2] ** 2) ** 0.5 or 1.0
up = (up[0] / L, up[1] / L, up[2] / L)
print()
print("prefab uzayinda:")
print("  pelvis -> (%.3f, %.3f, %.3f)" % wp)
print("  head   -> (%.3f, %.3f, %.3f)" % wh)
print("  up     = (%.3f, %.3f, %.3f)   uzunluk %.3f" % (up + (L,)))

# Mesh: AABB hangi eksende uzun
print()
print("MESH AABB (mesh uzayi):")
for o in env.objects:
    if o.type.name != "Mesh":
        continue
    m = o.read()
    vd = m.m_VertexData
    c = m.m_LocalAABB.m_Center
    e = m.m_LocalAABB.m_Extent
    print("  x %6.3f  (genislik %5.3f)" % (c.x, 2 * e.x))
    print("  y %6.3f  (genislik %5.3f)" % (c.y, 2 * e.y))
    print("  z %6.3f  (genislik %5.3f)" % (c.z, 2 * e.z))
    break

print()
print("KARAR")
if up[1] > 0.9:
    print("  prefab KOK donusumu Z eksenini yuk eksenine ceviriyor -> onizleme DIK.")
else:
    print("  prefab koku KARAKTERI DIK TUTMUYOR (up.y=%.2f)." % up[1])
    print("  Editor betigi 'yon duzeltmesi' yazdiysa bu bir yazma/okuma")
    print("  hatasidir: PlayerAssetNpc.PrefabLocalRotation degil, prefab'in")
    print("  kendi localRotation'i uygulanmalidir.")