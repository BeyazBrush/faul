# -*- coding: utf-8 -*-
"""
diag_skin.py
Bag pozunda  M_i * bind_i  butun kemikler icin SABIT olmalidir.
Sapma varsa: bindpose sirasi yanlis, bone transform'lari yanlis ya da
bindpose yorumu (satir/sutun major) yanlistir.
"""
import sys

import numpy as np

import UnityPy

env = UnityPy.load(sys.argv[1] if len(sys.argv) > 1
                   else r"D:\modmscMP\MSCSteamMP\assets\mpnpc")

tr_local, tr_parent, go_name = {}, {}, {}
for o in env.objects:
    if o.type.name == "GameObject":
        go_name[o.path_id] = o.read().m_Name
    elif o.type.name == "Transform":
        t = o.read()
        tr_local[o.path_id] = (t.m_LocalPosition, t.m_LocalRotation,
                               t.m_LocalScale)
        tr_parent[o.path_id] = t.m_Father.path_id

bones = bind = None
for o in env.objects:
    if o.type.name == "SkinnedMeshRenderer":
        s = o.read()
        bones = [b.m_PathID for b in (s.m_Bones or [])]
    elif o.type.name == "Mesh":
        m = o.read()
        bind = []
        for bp in m.m_BindPose:
            bind.append([float(getattr(bp, "e%d%d" % (r, c)))
                         for r in range(4) for c in range(4)])


def q4(q):
    return (float(q.x), float(q.y), float(q.z), float(q.w))


def v3(p):
    return np.array([float(p.x), float(p.y), float(p.z)])


def q_to_m(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0.0],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0.0],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0.0],
        [0.0, 0.0, 0.0, 1.0]])


depth = {}


def gd(pid, g=0):
    if pid in depth:
        return depth[pid]
    p = tr_parent.get(pid, 0)
    d = 0 if p not in tr_local else gd(p, g + 1) + 1
    depth[pid] = d
    return d


for p in tr_local:
    gd(p)
world = {}
for pid in sorted(tr_local.keys(), key=lambda p: (depth.get(p, 0), p)):
    lp, lq, ls = tr_local[pid]
    mm = q_to_m(q4(lq))
    for c in range(3):
        mm[:3, c] *= float(getattr(ls, "xyz"[c]))
    mm[:3, 3] = v3(lp)
    p = tr_parent.get(pid, 0)
    world[pid] = world[p] @ mm if p in world else mm

print("kemik sayisi: %d   bindpose sayisi: %d" % (len(bones), len(bind)))
print()
print("%-4s %-14s %10s" % ("idx", "kemik", "sapma"))
print("-" * 40)
prod = []
for k, pid in enumerate(bones):
    m = world[pid] @ np.array(bind[k]).reshape(4, 4)
    prod.append(m)
ref = prod[0]
worst = 0.0
for k, pid in enumerate(bones):
    dev = float(np.abs(prod[k] - ref).max())
    worst = max(worst, dev)
    print("%-4d %-14s %10.6f" % (k, go_name.get(pid, "?")[:14], dev))
print("-" * 40)
print("EN BUYUK SAPMA: %.6f" % worst)
print("SONUC:", "TAMAM (bind pose tutarli)" if worst < 1e-3
      else "BOZUK - bindpose/bone siralamasi ya da yorumu hatali")

# ayrica: bindpose satiri kendi icinde tutarli mi (det=1, son satir 0,0,0,1)
bad = 0
for k in range(len(bind)):
    b = np.array(bind[k]).reshape(4, 4)
    if abs(b[3, 3] - 1.0) > 1e-5 or abs(b[3, 0]) > 1e-5:
        bad += 1
print()
print("bindpose son-satir hatasiz: %d/%d" % (len(bind) - bad, len(bind)))
print("  (Unity'de ceviri SON SUTUN'dadir; bizim duz dizide 12..14 = son sutun)")
b0 = np.array(bind[0]).reshape(4, 4)
print("bind[0] (satir-major okunuş):")
for r in range(4):
    print("   [%8.4f %8.4f %8.4f %8.4f]" % tuple(b0[r]))
print("  son sutun (ceviri) = (%.4f, %.4f, %.4f)" % (b0[0, 3], b0[1, 3], b0[2, 3]))
print("  son satir          = (%.4f, %.4f, %.4f, %.4f)" % tuple(b0[3]))