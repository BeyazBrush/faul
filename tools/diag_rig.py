# -*- coding: utf-8 -*-
"""
diag_rig.py  -  NPC rig'inin HANGI uzayda oldugunu olcer.

SORU: bundle'a yazdigim iskelet, animasyon clip'lerinin yazildigi uzayla
ayni mi? Degilse clip'ler mutlak local rotasyon tasidigi icin animasyon
modeli parcalar.

OLCUM:
  A) Sahnedeki (prefab'daki) kemik local rotasyonlari -> bunlar oyunun
     gercek rig'i olmali.
  B) bindpozdan turetilen local rotasyonlar (C = I) -> simdi kullandigimiz.
  C) Idle clip'inin ilk anahtari (t=0) -> clip'in yazildigi uzay.
  D) M_i * bind_i sapmasi: A icin ve B icin.

B ile C uyumlu degilse C = I secimi yanlistir ve kemikleri
C_game ile carpman gerekir.
"""
import json
import os
import sys

import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_char_data as M

DATA = M.DATA
EXTRACT = M.EXTRACT
NPC = "TRAFFIC/VehiclesHighway/VICTRO/LOD/Passengers/Driver/bodymesh"


def q4(q):
    return (float(q.x), float(q.y), float(q.z), float(q.w))


def v3(p):
    return [float(p.x), float(p.y), float(p.z)]


def matmul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)]
            for r in range(4)]


def main():
    env = UnityPy.load(os.path.join(DATA, "level2"))
    tr, go_tr = {}, {}
    for o in env.objects:
        if o.type.name == "Transform":
            try:
                t = o.read()
                nm = getattr(t.m_GameObject.read(), "m_Name", "")
            except Exception:
                continue
            tr[o.path_id] = {"n": nm, "p": getattr(t.m_Father, "path_id", 0),
                             "pos": t.m_LocalPosition, "rot": t.m_LocalRotation,
                             "scl": t.m_LocalScale}
            go_tr[getattr(t.m_GameObject, "path_id", -1)] = o.path_id

    def path_of(pid):
        parts, g = [], 0
        while pid in tr and g < 96:
            parts.append(tr[pid]["n"])
            pid = tr[pid]["p"]
            g += 1
        return "/".join(reversed(parts))

    target = None
    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        s = o.read()
        tpid = go_tr.get(getattr(s.m_GameObject, "path_id", -1))
        if tpid is None or path_of(tpid) != NPC:
            continue
        if not str(getattr(M.deref(s.m_Mesh), "m_Name", "")).startswith("bodymesh"):
            continue
        target = (s, tpid)
        break
    if not target:
        print("SMR bulunamadi")
        return 1
    smr, mesh_tpid = target

    bone_pids = [b.m_PathID for b in (smr.m_Bones or [])]
    mesh = M.deref(smr.m_Mesh)
    bindposes = {}
    for i, b in enumerate(bone_pids):
        bp = mesh.m_BindPose[i]
        bindposes[b] = [float(getattr(bp, "e%d%d" % (r, c)))
                        for r in range(4) for c in range(4)]

    # ---------- A) sahnedeki world matrisleri
    depth = {}

    def gd(pid, g=0):
        if pid in depth:
            return depth[pid]
        p = tr.get(pid, {}).get("p", 0)
        d = 0 if p not in tr else gd(p, g + 1) + 1
        depth[pid] = d
        return d

    for pid in tr:
        gd(pid)
    worldA = {}
    for pid in sorted(tr, key=lambda p: (depth.get(p, 0), p)):
        m = M.local_matrix(v3(tr[pid]["pos"]), q4(tr[pid]["rot"]),
                           v3(tr[pid]["scl"]))
        p = tr[pid]["p"]
        worldA[pid] = matmul(worldA[p], m) if p in worldA else m

    # ---------- B) bindpozdan (C = I)
    worldB = {pid: M.mat_inv([bindposes[pid][r * 4:r * 4 + 4] for r in range(4)])
              for pid in bone_pids}

    # ---------- D) sapma
    def dev(world):
        prod = []
        for pid in bone_pids:
            prod.append(matmul(world[pid],
                               [bindposes[pid][r * 4:r * 4 + 4] for r in range(4)]))
        w = 0.0
        for i in range(1, len(prod)):
            w = max(w, max(abs(prod[i][r][c] - prod[0][r][c])
                           for r in range(4) for c in range(4)))
        return w, prod[0]

    dA, CA = dev(worldA)
    dB, CB = dev(worldB)
    print("A) SAHNEDEKİ world (prefab rig):  M_i*bind_i sapma = %.6f" % dA)
    print("   C = ")
    for r in range(4):
        print("     [%9.4f %9.4f %9.4f %9.4f]" % tuple(CA[r]))
    print()
    print("B) BINDPOZDAN (C = I):            sapma = %.6f" % dB)
    print("   C = ")
    for r in range(4):
        print("     [%9.4f %9.4f %9.4f %9.4f]" % tuple(CB[r]))
    print()
    print("C_A = C_B ?", "evet" if dA < 1e-3 else "HAYIR - uzaylar farkli")

    # ---------- C) Idle clip t=0
    rig_paths = set()
    for pid in bone_pids:
        rig_paths.add(path_of(pid))
    d = json.load(open(os.path.join(EXTRACT, "anims",
                                    "fat_standing.json"), encoding="utf-8"))
    print()
    print("Idle (fat_standing) t=0 degerleri:")
    clip0 = {}
    for c in d["curves"]:
        if not c["keys"]:
            continue
        rp = (c["path"] or "").strip()
        clip0[rp] = (c["type"], c["keys"][0])

    print()
    print("%-22s %-34s %-34s" % ("kemik", "A) sahne local rot", "B) bind local rot"))
    print("-" * 92)
    for pid in bone_pids:
        nm = path_of(pid).split("/")[-1]
        rel = path_of(pid).split("bodymesh/")[-1]
        # A) sahnedeki local rot
        la = q4(tr[pid]["rot"])
        # B) bind local rot: local = world_parent^-1 * world
        par = tr[pid]["p"]
        worldBp = (worldA[par] if par in worldA
                   else [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]])
        lb = M.mat_mul(M.mat_inv([worldBp[r][r] for r in range(4)]
                                 if False else worldBp), worldB[pid])
        pos, rot, scl = M.decompose(lb)
        c = clip0.get(rel)
        cs = ""
        if c:
            if c[0] == "quaternion":
                cs = "clip=%s" % ("[" + ", ".join("%.3f" % x for x in c[1][1:5]) + "]")
            else:
                cs = "clipPOS=%s" % ("[" + ", ".join("%.3f" % x for x in c[1][1:4]) + "]")
        else:
            cs = "clip=---"
        print("%-22s %-34s %-34s %s"
              % (nm,
                 "(%.3f,%.3f,%.3f,%.3f)" % la,
                 "(%.3f,%.3f,%.3f,%.3f)" % tuple(rot), cs))


if __name__ == "__main__":
    raise SystemExit(main())