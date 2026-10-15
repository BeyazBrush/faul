# -*- coding: utf-8 -*-
"""
extract_npc_dump.py  (v3 - UnityPy 1.25.x, My Summer Car / Unity 5.0.0f4)

NPC mesh / iskelet / animasyon dumper.

KULLANIM
--------
    pip install UnityPy
    python extract_npc_dump.py
    python extract_npc_dump.py --only bodymesh --limit 3
    python extract_npc_dump.py --clips-only --clip get_car

KAYNAKLAR
---------
    sharedassets3.assets -> AnimationClip'ler (350 adet, compressed curve)
    level2              -> NPC GameObject'leri, SkinnedMeshRenderer, iskelet

OLCULENMIS FORMAT BILGISI (Unity 5.0 + UnityPy 1.25.3 ile dogrulandi)
---------------------------------------------------------------------
Mesh (Unity 5'te m_Vertices yok, hepsi m_VertexData icinde):
    m_VertexData.m_Channels  -> ChannelInfo(stream, offset, dimension, format)
                               dimension==0 olanlar yer tutucu
    stream 0 : stride = (3 pos + 3 normal + 4 tangent) * 4 = 40 byte
    stream 1 : stride = (2 uv) * 4 = 8 byte
    stream'lar m_DataSize icinde sirayla (once stream 0) yigilir
    dogrulama: 1709 vertex, pozisyonlar Mesh.m_LocalAABB ile %100 birebir

    indices      -> m_IndexBuffer (dunya dizisi, 16080 int)
    bone weights -> m_Skin        (1709 adet BoneInfluence:
                                    boneIndex_0_..3_, weight_0_..3_)
    bindposes    -> m_BindPose    (Matrix4x4f, alanlar e00..e33 = satir-major)
    kemik sayisi -> m_BoneNameHashes (20 adet uint32)

SkinnedMeshRenderer:
    m_Bones    -> 20 adet Transform PPtr (ad cozumlemesi m_GameObject uzerinden)
    m_RootBone -> kok Transform
    m_BindPose YOK (mesh'te)

AnimationClip (m_Compressed = True, legacy = True):
    m_RotationCurves : QuaternionCurve(path, curve)
    m_PositionCurves : Vector3Curve(path, curve)
    curve.m_Curve    -> anahtar listesi, her anahtarda m_Time + m_Value
    curve path'leri  -> "Char/skeleton/thig_left" gibi RIG YOLLARI
                        (yani NPC rig'i: Char/skeleton/{thig_left,pelvis,
                        spine_middle,spine_upper,collar_*,shoulder_*,arm_*})

CIKTI
-----
    manifest.json              ozet + NPC envanteri + clip<->rig eslesmesi
    meshes/<ad>__<mesh>.obj    geometri (v/vn/vt/f)
    meshes/<ad>__<mesh>.skin.json  bone weights + bindposes + bone listesi
    rigs/<ad>.json             iskelet (local + world TRS, parent, path)
    anims/<clip>.json          curve'ler (path, tip, anahtarlar)
    poses/<clip>@<t>s.json     orneklenmis oturma/poz
"""

import argparse
import json
import os
import struct
import sys
from pathlib import Path

try:
    import UnityPy
except ImportError:
    sys.stderr.write("pip install UnityPy\n")
    raise

DEFAULT_DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
DEFAULT_OUT = r"D:\MSCSteamMP\extracted_npc"

CAR_CLIP_HINTS = ["get_car", "car_in", "car_out", "car_enter", "car_exit",
                  "sit", "sitting", "drive", "driving", "bus_driver"]


# ======================================================================
# kucuk yardimcilar
# ======================================================================
def jdump(path, obj):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=1, ensure_ascii=False)


def v3(t, default=(0.0, 0.0, 0.0)):
    try:
        return [round(float(t.x), 6), round(float(t.y), 6), round(float(t.z), 6)]
    except Exception:
        return list(default)


def v4q(t):
    try:
        return [round(float(t.x), 6), round(float(t.y), 6),
                round(float(t.z), 6), round(float(t.w), 6)]
    except Exception:
        return [0.0, 0.0, 0.0, 1.0]


def mat16(m):
    """Matrix4x4f (e00..e33) / Matrix4x4x4 (m00..m33) / dict -> 16 float."""
    for keys in (["e%02d" % k for k in range(16)],
                 ["m%02d" % k for k in range(16)],
                 ["m%d%d" % (i, j) for i in range(4) for j in range(4)]):
        try:
            vals = [round(float(getattr(m, k)), 6) for k in keys]
            if any(v != 0.0 for v in vals):
                return vals
        except Exception:
            pass
    try:
        d = dict(m)
        for keys in (["e%02d" % k for k in range(16)],
                     ["m%02d" % k for k in range(16)]):
            vals = [round(float(d.get(k, 0.0)), 6) for k in keys]
            if any(v != 0.0 for v in vals):
                return vals
    except Exception:
        pass
    return [1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0]


def deref(ptr):
    """PPtr -> cozulmus obje (PPtr veya ObjectInfo olabilir), hata -> None."""
    try:
        o = ptr.read()
    except Exception:
        return None
    try:
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return o


def safe_name(s):
    return "".join(c if (c.isalnum() or c in "._-") else "_" for c in (s or "unnamed"))


def local_matrix(pos, rot, scl):
    x, y, z, w = rot
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    m = [[1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy), pos[0]],
         [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx), pos[1]],
         [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy), pos[2]],
         [0.0, 0.0, 0.0, 1.0]]
    for c in range(3):
        for r in range(3):
            m[r][c] *= scl[c]
    return m


def mat_mul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def trs_from_matrix(m):
    pos = [round(m[0][3], 6), round(m[1][3], 6), round(m[2][3], 6)]

    def col(c):
        v = [m[0][c], m[1][c], m[2][c]]
        L = (v[0] ** 2 + v[1] ** 2 + v[2] ** 2) ** 0.5 or 1.0
        return [round(x / L, 6) for x in v]

    sx, sy, sz = col(0), col(1), col(2)
    tr = m[0][0] + m[1][1] + m[2][2]
    if tr > 0:
        s = (tr + 1.0) ** 0.5
        qw = s * 0.5
        s = 0.5 / s
        qx, qy, qz = ((m[1][2] - m[2][1]) * s, (m[2][0] - m[0][2]) * s, (m[0][1] - m[1][0]) * s)
    elif m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = (1.0 + m[0][0] - m[1][1] - m[2][2]) ** 0.5
        qx = 0.5 * s
        s = 0.5 / s
        qw, qy, qz = ((m[1][2] - m[2][1]) * s, (m[1][0] + m[0][1]) * s, (m[2][0] + m[0][2]) * s)
    elif m[1][1] > m[2][2]:
        s = (1.0 + m[1][1] - m[0][0] - m[2][2]) ** 0.5
        qy = 0.5 * s
        s = 0.5 / s
        qx, qw, qz = ((m[1][0] + m[0][1]) * s, (m[1][2] - m[2][1]) * s, (m[2][1] + m[1][2]) * s)
    else:
        s = (1.0 + m[2][2] - m[0][0] - m[1][1]) ** 0.5
        qz = 0.5 * s
        s = 0.5 / s
        qx, qy, qw = ((m[2][0] + m[0][2]) * s, (m[2][1] + m[1][2]) * s, (m[0][1] - m[1][0]) * s)
    return pos, [round(qx, 6), round(qy, 6), round(qz, 6), round(qw, 6)], [sx, sy, sz]


# ======================================================================
# Transform tablosu  (level2'de 36045 transform var, hepsini biriktir)
# ======================================================================
class TransformIndex(object):
    def __init__(self, env):
        self.info = {}
        for obj in env.objects:
            if obj.type.name != "Transform":
                continue
            try:
                t = obj.read()
            except Exception:
                continue
            pid = obj.path_id
            try:
                parent = t.m_Father.path_id
            except Exception:
                parent = 0
            try:
                gname = getattr(t.m_GameObject.read(), "m_Name", "")
            except Exception:
                gname = ""
            self.info[pid] = {
                "path_id": pid,
                "name": gname or "",
                "parent": parent,
                "children": [],
                "pos": v3(getattr(t, "m_LocalPosition", None)),
                "rot": v4q(getattr(t, "m_LocalRotation", None)),
                "scale": v3(getattr(t, "m_LocalScale", None), (1.0, 1.0, 1.0)),
            }
        for pid, i in self.info.items():
            if i["parent"] in self.info:
                self.info[i["parent"]]["children"].append(pid)

    def path(self, pid):
        parts, guard = [], 0
        while pid in self.info and guard < 512:
            parts.append(self.info[pid]["name"])
            pid = self.info[pid]["parent"]
            guard += 1
        return "/".join(reversed(parts))

    def world(self, pid):
        i = self.info.get(pid)
        if i is None:
            return [1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0]
        m = local_matrix(i["pos"], i["rot"], i["scale"])
        cur, guard = i["parent"], 0
        while cur in self.info and guard < 512:
            p = self.info[cur]
            m = mat_mul(local_matrix(p["pos"], p["rot"], p["scale"]), m)
            cur = p["parent"]
            guard += 1
        return m

    def bone_chain(self, bone_pids, root_pid):
        """kemik + tum atalarini topla (sadece iskelet agaci, 36045 hepsi degil)"""
        keep = set()
        for bp in list(bone_pids) + ([root_pid] if root_pid else []):
            cur, guard = bp, 0
            while cur in self.info and cur not in keep and guard < 512:
                keep.add(cur)
                cur = self.info[cur]["parent"]
                guard += 1
        return keep


# ======================================================================
# Mesh okuma (Unity 5 m_VertexData)
# ======================================================================
def read_mesh(mesh):
    """-> dict(verts, normals, tangents, uvs, indices, weights, bindposes, hashes)"""
    out = {"verts": [], "normals": [], "tangents": [], "uvs": [],
           "indices": [], "weights": [], "bindposes": [], "hashes": [],
           "submesh_count": 0, "aabb": None}
    try:
        out["submesh_count"] = len(mesh.m_SubMeshes)
    except Exception:
        pass

    # ---------- vertex stream'leri
    vd = getattr(mesh, "m_VertexData", None)
    if vd is not None:
        try:
            n = int(vd.m_VertexCount)
            data = vd.m_DataSize
        except Exception:
            n = 0
            data = b""
        streams = {}
        for ch in vd.m_Channels:
            if int(ch.dimension) <= 0:
                continue
            streams.setdefault(int(ch.stream), []).append(
                (int(ch.offset), int(ch.dimension)))
        base = 0
        for st in sorted(streams):
            items = sorted(streams[st])
            stride = sum(dim for _, dim in items) * 4
            b = base
            nbytes = stride * n
            for off, dim in items:
                # pos/normal/tangent float32 (format 0), uv float32
                if b + off + dim * 4 > len(data):
                    continue
                if dim == 2:
                    for i in range(n):
                        out["uvs"].append(list(struct.unpack_from("<2f", data, b + i * stride + off)))
                elif dim == 3:
                    dst = out["normals"] if (st == 0 and off == 12) else out["verts"]
                    for i in range(n):
                        dst.append(list(struct.unpack_from("<3f", data, b + i * stride + off)))
                elif dim == 4:
                    for i in range(n):
                        out["tangents"].append(list(struct.unpack_from("<4f", data, b + i * stride + off)))
                else:
                    for i in range(n):
                        out["uvs"].append(list(struct.unpack_from(
                            "<%df" % dim, data, b + i * stride + off)))
            base += nbytes
    else:
        n = 0
        try:
            out["verts"] = [v3(v) for v in mesh.m_Vertices]
            n = len(out["verts"])
            out["normals"] = [v3(v) for v in mesh.m_Normals]
            out["uvs"] = [v3(v)[:2] for v in mesh.m_UV0]
        except Exception:
            pass

    # normalize: uvs dim 2 channel'i varsa verts ile ayni sira
    if len(out["uvs"]) != n and out["uvs"]:
        out["uvs"] = out["uvs"][:n]
    while len(out["normals"]) < n:
        out["normals"].append([0.0, 1.0, 0.0])

    # ---------- index buffer
    try:
        idx = mesh.m_IndexBuffer
        out["indices"] = [int(i) for i in idx]
    except Exception:
        try:
            out["indices"] = [int(i) for i in mesh.m_Indices]
        except Exception:
            pass

    # ---------- skin
    try:
        for bi in mesh.m_Skin:
            out["weights"].append([
                int(getattr(bi, "boneIndex_0_", 0)), int(getattr(bi, "boneIndex_1_", 0)),
                int(getattr(bi, "boneIndex_2_", 0)), int(getattr(bi, "boneIndex_3_", 0)),
                round(float(getattr(bi, "weight_0_", 0.0)), 6),
                round(float(getattr(bi, "weight_1_", 0.0)), 6),
                round(float(getattr(bi, "weight_2_", 0.0)), 6),
                round(float(getattr(bi, "weight_3_", 0.0)), 6)])
    except Exception:
        pass
    try:
        out["bindposes"] = [mat16(b) for b in mesh.m_BindPose]
    except Exception:
        pass
    try:
        out["hashes"] = [int(h) & 0xFFFFFFFF for h in mesh.m_BoneNameHashes]
    except Exception:
        pass
    try:
        c = mesh.m_LocalAABB.m_Center
        e = mesh.m_LocalAABB.m_Extent
        out["aabb"] = {"min": [round(c.x - e.x, 5), round(c.y - e.y, 5), round(c.z - e.z, 5)],
                       "max": [round(c.x + e.x, 5), round(c.y + e.y, 5), round(c.z + e.z, 5)]}
    except Exception:
        pass
    return out


def write_obj(path, name, md):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    verts = md["verts"]
    norms = md["normals"] if len(md["normals"]) == len(verts) else []
    uvs = md["uvs"] if len(md["uvs"]) == len(verts) else []
    with open(path, "w", encoding="utf-8") as f:
        f.write("# MSC NPC dump - %s\n" % name)
        f.write("o %s\n" % name)
        for v in verts:
            f.write("v %.6f %.6f %.6f\n" % (v[0], v[1], v[2]))
        for n in norms:
            f.write("vn %.6f %.6f %.6f\n" % (n[0], n[1], n[2]))
        for u in uvs:
            f.write("vt %.6f %.6f\n" % (u[0], u[1]))
        f.write("s 1\n")
        idx = md["indices"]
        if norms and uvs:
            for i in range(0, len(idx) - 2, 3):
                a, b, c = idx[i] + 1, idx[i + 1] + 1, idx[i + 2] + 1
                f.write("f %d/%d/%d %d/%d/%d %d/%d/%d\n" % (a, a, a, b, b, b, c, c, c))
        else:
            for i in range(0, len(idx) - 2, 3):
                f.write("f %d %d %d\n" % (idx[i] + 1, idx[i + 1] + 1, idx[i + 2] + 1))


# ======================================================================
# AnimationClip
# ======================================================================
def read_curve_element(el, kind):
    path = getattr(el, "path", "") or ""
    curve = getattr(el, "curve", None)
    keys = []
    raw = None
    if curve is not None:
        raw = getattr(curve, "m_Curve", None)
        if raw is None:
            raw = getattr(curve, "keys", None)
    for k in (raw or []):
        t = getattr(k, "m_Time", getattr(k, "time", 0.0))
        v = getattr(k, "m_Value", getattr(k, "value", None))
        if kind == "quaternion":
            keys.append([round(float(t), 5), v4q(v)])
        elif kind == "vector":
            keys.append([round(float(t), 5), v3(v)])
        else:
            try:
                keys.append([round(float(t), 5), round(float(v), 5)])
            except Exception:
                keys.append([round(float(t), 5), 0.0])
    return {"path": path, "type": kind, "key_count": len(keys), "keys": keys}


def dump_clip(clip):
    d = {
        "name": getattr(clip, "m_Name", ""),
        "legacy": bool(getattr(clip, "m_Legacy", False)),
        "compressed": bool(getattr(clip, "m_Compressed", False)),
        "sample_rate": round(float(getattr(clip, "m_SampleRate", 0.0) or 0.0), 3),
        "wrap_mode": int(getattr(clip, "m_WrapMode", 0) or 0),
        "curves": [],
    }
    for attr, kind in (("m_RotationCurves", "quaternion"),
                       ("m_PositionCurves", "vector"),
                       ("m_ScaleCurves", "vector"),
                       ("m_FloatCurves", "float")):
        for el in (getattr(clip, attr, None) or []):
            try:
                d["curves"].append(read_curve_element(el, kind))
            except Exception:
                pass
    for el in (getattr(clip, "m_PPtrCurves", None) or []):
        d["curves"].append({"path": getattr(el, "path", "") or "", "type": "pptr",
                            "key_count": 0, "keys": []})
    return d


def clip_bones(clip):
    out = set()
    for c in clip["curves"]:
        for seg in (c.get("path") or "").replace("\\", "/").split("/"):
            if seg.strip():
                out.add(seg.strip())
    return out


def clip_duration(clip):
    d = 0.0
    for c in clip["curves"]:
        for k in c["keys"]:
            d = max(d, float(k[0]))
    return round(d, 4)


def sample(clip, t):
    pose = {}
    for c in clip["curves"]:
        keys = c["keys"]
        if not keys:
            continue
        val = keys[-1][1]
        for i in range(len(keys) - 1):
            t0, v0 = keys[i][0], keys[i][1]
            t1, v1 = keys[i + 1][0], keys[i + 1][1]
            if t0 <= t <= t1:
                f = (t - t0) / ((t1 - t0) or 1e-6)
                val = v0 if not isinstance(v0, list) else \
                    [a + (b - a) * f for a, b in zip(v0, v1)]
                break
        pose.setdefault(c["path"], {})[c["type"]] = val
    return pose


# ======================================================================
# SkinnedMeshRenderer -> mesh + rig
# ======================================================================
def dump_renderer(smr, tindex, out, mesh_pool):
    go = deref(smr.m_GameObject)
    goname = getattr(go, "m_Name", "unnamed") if go else "unnamed"
    t_go = None
    for pid, i in tindex.info.items():
        if i["name"] == goname:
            t_go = pid
            break

    mesh = deref(smr.m_Mesh)
    if mesh is None:
        mesh = mesh_pool.get(getattr(smr.m_Mesh, "m_PathID", -1))
    if mesh is None:
        return None
    mname = getattr(mesh, "m_Name", "?")
    md = read_mesh(mesh)
    if not md["verts"]:
        return None

    # ---- kemikler
    bone_pids = []
    for b in (getattr(smr, "m_Bones", None) or []):
        pid = getattr(b, "m_PathID", 0)
        bone_pids.append(pid)
    root_pid = getattr(smr.m_RootBone, "m_PathID", 0) if smr.m_RootBone else 0

    bones = []
    for k, pid in enumerate(bone_pids):
        inf = tindex.info.get(pid)
        bones.append({
            "index": k,
            "path_id": pid,
            "name": inf["name"] if inf else "?",
            "path": tindex.path(pid) if inf else "?",
            "bindpose": md["bindposes"][k] if k < len(md["bindposes"]) else None,
        })

    base = "%s__%s" % (safe_name(goname), safe_name(mname))
    write_obj(out / "meshes" / (base + ".obj"), goname, md)
    jdump(out / "meshes" / (base + ".skin.json"), {
        "gameobject": goname, "mesh": mname,
        "vertex_count": len(md["verts"]), "triangle_count": len(md["indices"]) // 3,
        "submesh_count": md["submesh_count"], "aabb": md["aabb"],
        "bones": bones, "bone_name_hashes": md["hashes"],
        "bone_weights": md["weights"],
        "root_bone": tindex.info[root_pid]["name"] if root_pid in tindex.info else None,
        "root_bone_path": tindex.path(root_pid) if root_pid in tindex.info else None,
        "parent_gameobject_path": tindex.path(t_go) if t_go else None,
    })

    # ---- rig (sadece iskelet agaci)
    keep = tindex.bone_chain(bone_pids, root_pid)
    rig = []
    for pid in sorted(keep):
        inf = tindex.info[pid]
        wp, wr, ws = trs_from_matrix(tindex.world(pid))
        rig.append({
            "path_id": pid, "name": inf["name"], "path": tindex.path(pid),
            "parent": tindex.info[inf["parent"]]["name"] if inf["parent"] in tindex.info else None,
            "parent_path_id": inf["parent"] if inf["parent"] in tindex.info else None,
            "local_pos": inf["pos"], "local_rot": inf["rot"], "local_scale": inf["scale"],
            "world_pos": wp, "world_rot": wr, "world_scale": ws,
            "is_root_bone": (pid == root_pid),
            "is_skin_bone": (pid in set(bone_pids)),
        })
    jdump(out / "rigs" / (safe_name(goname) + ".json"), {
        "gameobject": goname, "mesh": mname,
        "root_bone": rig[0]["name"] if rig else None,
        "parent_gameobject_path": tindex.path(t_go) if t_go else None,
        "skin_bone_count": len(bone_pids), "node_count": len(rig),
        "nodes": rig,
    })

    print("  [SMR] %-14s mesh=%-12s v=%-6d tri=%-6d bone=%-3d node=%-4d  %s"
          % (goname, mname, len(md["verts"]), len(md["indices"]) // 3,
             len(bone_pids), len(rig), tindex.path(t_go) if t_go else ""))
    return {"gameobject": goname, "mesh": mname,
            "vertices": len(md["verts"]), "triangles": len(md["indices"]) // 3,
            "bone_names": [b["name"] for b in bones],
            "bone_paths": [b["path"] for b in bones],
            "parent_path": tindex.path(t_go) if t_go else None,
            "aabb": md["aabb"]}


# ======================================================================
# main
# ======================================================================
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=DEFAULT_DATA)
    ap.add_argument("--clips", default="", help="clip dosyasi (bos = sharedassets3.assets)")
    ap.add_argument("--level", default="", help="rig dosyasi (bos = level2)")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--only", default="", help="sadece bu GameObject adi (or. bodymesh)")
    ap.add_argument("--clip", default="", help="sadece bu clip adi (or. get_car)")
    ap.add_argument("--limit", type=int, default=0, help="en fazla N renderer")
    ap.add_argument("--clips-only", action="store_true")
    ap.add_argument("--no-poses", action="store_true")
    args = ap.parse_args()

    data = args.data
    clips_file = args.clips or os.path.join(data, "sharedassets3.assets")
    level_file = args.level or os.path.join(data, "level2")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    print("=" * 74)
    print("clip kaynagi : %s" % clips_file)
    print("rig kaynagi  : %s" % level_file)
    print("cikti        : %s" % out)
    print("=" * 74)

    # ---------------- 1) AnimationClip'ler
    print("[1] AnimationClip okunuyor...")
    cenv = UnityPy.load(clips_file)
    clips = {}
    for o in cenv.objects:
        if o.type.name != "AnimationClip":
            continue
        try:
            d = dump_clip(o.read())
        except Exception:
            continue
        if d["name"]:
            clips[d["name"]] = d
    sel = [n for n in clips if (args.clip in n)] if args.clip else list(clips.keys())
    for n in sel:
        jdump(out / "anims" / (safe_name(n) + ".json"), clips[n])
    print("    %d clip bulundu, %d yazildi" % (len(clips), len(sel)))

    if args.clips_only:
        return 0

    # ---------------- 2) iskelet + mesh
    print("[2] SkinnedMeshRenderer + iskelet okunuyor...")
    lenv = UnityPy.load(level_file)
    tindex = TransformIndex(lenv)
    print("    %d Transform" % len(tindex.info))

    mesh_pool = {}
    try:
        for o in cenv.objects:
            if o.type.name == "Mesh":
                mesh_pool[o.path_id] = o
    except Exception:
        pass

    renderers, seen = [], {}
    for o in lenv.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        try:
            smr = o.read()
        except Exception:
            continue
        if args.only:
            g = deref(smr.m_GameObject)
            nm = getattr(g, "m_Name", "") if g else ""
            if args.only.lower() not in nm.lower():
                continue
        key = (getattr(deref(smr.m_GameObject), "m_Name", ""),
               getattr(deref(smr.m_Mesh), "m_Name", ""))
        if key in seen:
            continue
        info = dump_renderer(smr, tindex, out, mesh_pool)
        if info:
            renderers.append(info)
            seen[key] = True
            if args.limit and len(renderers) >= args.limit:
                break
    print("    %d benzersiz renderer" % len(renderers))

    # ---------------- 3) clip <-> rig eslestirme
    print("[3] clip <-> rig eslestirme...")
    matches = []
    all_rig_bones = set()
    for r in renderers:
        for p in r["bone_paths"]:
            for seg in (p or "").split("/"):
                if seg:
                    all_rig_bones.add(seg)
    for n in sorted(clips.keys()):
        cb = clip_bones(clips[n])
        if len(cb) < 3:
            continue
        inter = cb & all_rig_bones
        if len(inter) < 3:
            continue
        score = len(inter) / float(len(cb))
        best = max(renderers,
                   key=lambda r: len(cb & set(b for p in r["bone_paths"]
                                             for b in (p or "").split("/") if b)))
        bs = len(cb & set(b for p in best["bone_paths"] for b in (p or "").split("/") if b))
        matches.append({"clip": n, "renderer": best["gameobject"],
                        "score": round(score, 3), "shared_bones": bs,
                        "clip_bones": len(cb), "duration": clip_duration(clips[n])})
        print("    %-28s -> %-12s %.3f (%d/%d kemik, %.2fs)"
              % (n, best["gameobject"], score, bs, len(cb), clip_duration(clips[n])))

    # ---------------- 4) poz ornekleme
    if not args.no_poses:
        print("[4] oturma/poz ornekleri...")
        pc = 0
        names = [n for n in clips if any(h in n.lower() for h in CAR_CLIP_HINTS)]
        for n in sorted(names):
            d = clips[n]
            dur = clip_duration(d)
            for t in sorted({round(dur, 3), round(dur * 0.75, 3), round(dur * 0.5, 3)}):
                jdump(out / "poses" / ("%s@t%ss.json" % (safe_name(n), str(t).replace(".", "p"))),
                      {"clip": n, "duration": dur, "time": t, "pose": sample(d, t)})
                pc += 1
        print("    %d poz / %d clip" % (pc, len(names)))

    jdump(out / "manifest.json", {
        "clips_file": clips_file, "level_file": level_file,
        "clip_count": len(clips), "transform_count": len(tindex.info),
        "renderer_count": len(renderers),
        "renderers": [{k: v for k, v in r.items()
                       if k not in ("bone_names", "bone_paths")} for r in renderers],
        "clip_to_renderer": matches,
        "rig_bone_names": sorted(all_rig_bones),
        "car_clips": sorted(n for n in clips if any(h in n.lower() for h in CAR_CLIP_HINTS)),
    })
    print("=" * 74)
    print("TAMAM")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())