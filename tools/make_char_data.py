# -*- coding: utf-8 -*-
"""
make_char_data.py
NPC karakter verisini Unity Editor script'inin okuyacagi IKILI (.dat) formatina
yazar. Bu dosya su her seyi icerir: mesh, iskelet, bindpose, bone weight,
materyal/doku adlari ve animasyon clip'leri (yollari tek koke normalize).

Kullanim:
    python make_char_data.py
    python make_char_data.py --npc "TRAFFIC/VehiclesHighway/VICTRO/LOD/Driver" \
        --out "D:\MSCSteamMP\extracted_npc\mpnpc.dat"

IKILI FORMAT (little-endian, C# BinaryReader ile okunur)
---------------------------------------------------------
    magic   : "MPNPC" (5 bayt)
    int32   : version = 1
    string  : charName

    -- MESH --
    int32   : vertexCount
    int32   : indexCount
    int32   : submeshCount
    float32[3*vc] : vertices
    float32[3*vc] : normals
    float32[4*vc] : tangents
    float32[2*vc] : uvs
    int32[ic]     : indices
    int32[smc]    : submeshIndexCounts

    -- BONES --
    int32   : boneCount
    (boneCount kezi)
      string  name
      string  path
      int32   parentIndex   (-1 = yok)
      int32   isSkinBone
      float32[3]  localPosition
      float32[4]  localRotation
      float32[3]  localScale
      float32[16] bindpose
    int32   : rootBoneIndex

    -- WEIGHTS --
    int32   : weightCount  (= vertexCount)
    (weightCount kezi) int32[4] boneIndex, float32[4] weight

    -- MATERIALS --
    int32   : materialCount
    (materialCount kezi) string materialName, string textureFile

    -- CLIPS --
    int32   : clipCount
    (clipCount kezi)
      string  name
      float32 length
      int32   loop          (1 = loop)
      int32   curveCount
      (curveCount kezi)
        string  path
        int32   kind        0=position 1=rotation 2=scale
        int32   keyCount
        float32[keyCount*comp] values
        float32[keyCount]       times
"""
import argparse
import json
import os
import struct
import sys

import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
EXTRACT = r"D:\MSCSteamMP\extracted_npc"

# Bundle'da kullanilacak klip gruplari: ad -> (kaynak clip listesi, loop)
CLIP_SETS = [
    # --- temel duruşlar (birleştirilmiş: gövde + iki el kolu)
    ("Idle",      ["fat_standing", "fat_handsleft_idle", "fat_handsright_idle"], 1),
    ("Walk",      ["fat_walk", "fat_handsleft_walk", "fat_handsright_walk"], 1),

    # --- üst gövde / kol animasyonları (TEK SEFERLİK, katman 1)
    #
    # MSC'de bunlar ayrı klip olarak değil, omurga "additive" eğrileri
    # olarak durur. Biz ayrı klip olarak paketliyoruz; PlayerAnimManager
    # bunları katman 1'de tek seferlik oynatır.
    #
    # Not: eğri yolları gövdeye göre RELATİF ('spine_upper/...'), oyunda
    # ise mutlak ('pelvis/spine_middle/spine_upper/...'). anchor_for() ve
    # resolve_paths() bunu iskeletle eşleştirir.
    ("Wave",      ["fat_waving_hello"], 0),
    ("Wave2",     ["fat_waving_hello2"], 0),
    ("AxSwing",   ["fat_spine_ax_swing"], 0),
    ("AxThreat",  ["fat_spine_ax_threat"], 0),
    ("Cough",     ["fat_spine_upper_cough"], 0),
    ("Drink",     ["fat_handsleft_drink"], 0),
    ("DrinkR",    ["fat_handsright_drink"], 0),
    ("Carry",     ["fat_handsright_carry"], 0),
    ("Point",     ["fat_handsleft_point"], 0),
    ("Smoke",     ["fat_handsleft_smoke"], 0),
    ("OfferOn",   ["fat_handsleft_offer_on"], 0),
    ("OfferOff",  ["fat_handsleft_offer_off"], 0),
    ("OfferR",    ["fat_handsright_offer"], 0),

    # --- araç
    ("DriveIdle", ["fat_get_car_idle1"], 1),
    ("Drive",     ["fat_get_car_idle2"], 1),
    ("CarIn",     ["fat_get_car_in"], 0),
    ("CarOut",    ["fat_get_car_out"], 0),
    ("CarPay1",   ["fat_get_car_pay1"], 0),
    ("CarPay2",   ["fat_get_car_pay2"], 0),
    ("Vomit",     ["fat_get_car_vomit"], 0),
    ("VanIdle1",  ["fat_get_van_idle1"], 1),
    ("VanIdle2",  ["fat_get_van_idle2"], 1),
    ("VanIn",     ["fat_get_van_in"], 0),
    ("VanOut",    ["fat_get_van_out"], 0),
]

KIND_POS, KIND_ROT, KIND_SCALE = 0, 1, 2
COMPS = {KIND_POS: 3, KIND_ROT: 4, KIND_SCALE: 3}


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


def submesh_counts(mesh):
    """m_SubMeshes typetree alanlari UnityPy 1.25'te 'm_' oncesiz geliyor."""
    out, firsts = [], []
    try:
        for sm in mesh.m_SubMeshes:
            cnt = 0
            for a in ("indexCount", "m_IndexCount"):
                v = getattr(sm, a, None)
                if v is not None:
                    cnt = int(v)
                    break
            out.append(cnt)
            fb = 0
            for a in ("firstByte", "m_FirstByte"):
                v = getattr(sm, a, None)
                if v is not None:
                    fb = int(v)
                    break
            firsts.append(fb)
    except Exception as e:
        sys.stderr.write("submesh okunamadi: %s\n" % e)
    return out, firsts


def read_indices(mesh):
    """UnityPy 1.25 bazi mesh'lerde m_IndexBuffer'i ham bayt olarak donduruyor
    (degerler 0..255). 16080 bayt = 8040 adet uint16 indeks."""
    vals = list(mesh.m_IndexBuffer or [])
    if not vals:
        return []
    if max(vals) <= 255:
        buf = bytes(bytearray(vals))
        n = len(buf) // 2
        return list(struct.unpack("<%dH" % n, buf[:n * 2]))
    return [int(v) for v in vals]


def read_vertex_streams(mesh):
    """Unity 5 m_VertexData -> verts, normals, tangents, uvs"""
    verts, norms, tans, uvs = [], [], [], []
    vd = getattr(mesh, "m_VertexData", None)
    if vd is None:
        return (verts, norms, tans, uvs)
    n = int(vd.m_VertexCount)
    data = vd.m_DataSize
    streams = {}
    for ch in vd.m_Channels:
        if int(ch.dimension) <= 0:
            continue
        streams.setdefault(int(ch.stream), []).append((int(ch.offset), int(ch.dimension)))
    base = 0
    for st in sorted(streams):
        items = sorted(streams[st])
        stride = sum(d for _, d in items) * 4
        for off, dim in items:
            if dim == 2:
                for i in range(n):
                    uvs.append(list(struct.unpack_from("<2f", data, base + i * stride + off)))
            elif dim == 3:
                dst = norms if (st == 0 and off == 12) else verts
                for i in range(n):
                    dst.append(list(struct.unpack_from("<3f", data, base + i * stride + off)))
            elif dim == 4:
                for i in range(n):
                    tans.append(list(struct.unpack_from("<4f", data, base + i * stride + off)))
        base += stride * n
    while len(norms) < n:
        norms.append([0.0, 1.0, 0.0])
    return (verts, norms, tans, uvs)


class Writer(object):
    def __init__(self, f):
        self.f = f

    def i32(self, v):
        self.f.write(struct.pack("<i", int(v)))

    def f32(self, v):
        self.f.write(struct.pack("<f", float(v)))

    def f32a(self, vals):
        self.f.write(struct.pack("<%df" % len(vals),
                                 *[float(v) for v in vals]))

    def s(self, txt):
        b = (txt or "").encode("utf-8")
        n = len(b)
        out = bytearray()
        while True:
            x = n & 0x7F
            n >>= 7
            out.append(x | (0x80 if n else 0))
            if not n:
                break
        self.f.write(bytes(out))
        self.f.write(b)


# ----------------------------------------------------------------------
# BIND POZUNDAN ISKELET KURMA
# ----------------------------------------------------------------------
# NEDEN: Kemiklerin local TRS'i SAHNEDEN kopyalanamaz. `bodymesh` mesh'i TUM
# NPC'lerde paylasilir ama her NPC instance'inin iskeleti kendi pozundadir:
# secilen VICTRO surucusu arabasinda OTURUYORDU, yani pelvis rotasyonu
# ~100 degedi. Mesh'in bindposeleri ise T-pozdan gelir. Iki farkli poz
# oldugu icin
#
#     M_i * bind_i  !=  sabit
#
# ve skinleme PATLAR: ucgenler her yere savrulur, kiyafetler bozuk gorunur.
# tools/diag_skin.py olcumu: sapma 1.45.
#
# COZUM: iskelet bind pozundan kurulur. Bag pozunda M_i * bind_i = C (sabit).
# C = I secilirse  world_i = bind_i^-1  ve
#
#     local_i = world_parent^-1 * world_i = bind_parent * bind_i^-1
#
# C = I demek: mesh authored oldugu yerde durur, kemikler etrafinda T-pozda
# durur. Bu mesh'in ASIL bind pozudur ve animasyon clip'leri de bu poza gore
# yazildigi icin tutarli calisir. Poz verisi hicbir NPC instance'inden GELMEZ.
def mat_mul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)]
            for r in range(4)]


def mat_inv(m):
    d = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
         - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
         + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))
    if abs(d) < 1e-12:
        d = 1e-12
    inv = [[0.0] * 4 for _ in range(4)]
    inv[0][0] = (m[1][1] * m[2][2] - m[1][2] * m[2][1]) / d
    inv[0][1] = -(m[0][1] * m[2][2] - m[0][2] * m[2][1]) / d
    inv[0][2] = (m[0][1] * m[1][2] - m[0][2] * m[1][1]) / d
    inv[1][0] = -(m[1][0] * m[2][2] - m[1][2] * m[2][0]) / d
    inv[1][1] = (m[0][0] * m[2][2] - m[0][2] * m[2][0]) / d
    inv[1][2] = -(m[0][0] * m[1][2] - m[0][2] * m[1][0]) / d
    inv[2][0] = (m[1][0] * m[2][1] - m[1][1] * m[2][0]) / d
    inv[2][1] = -(m[0][0] * m[2][1] - m[0][1] * m[2][0]) / d
    inv[2][2] = (m[0][0] * m[1][1] - m[0][1] * m[1][0]) / d
    t = [m[r][3] for r in range(3)]
    for r in range(3):
        inv[r][3] = -(inv[r][0] * t[0] + inv[r][1] * t[1] + inv[r][2] * t[2])
    inv[3] = [0.0, 0.0, 0.0, 1.0]
    return inv


def decompose(m):
    """4x4 -> (pos, quat, scale). Unity duzeni: ceviri son SUTUNDA.

    Kolonlardan olcek, kalan rotasyon matrisinden quaternion (Shepperd).
    Determinant negatifse (ayna) en kucuk olcekli kolon ters cevrilir.
    """
    pos = [m[0][3], m[1][3], m[2][3]]

    cols = []
    for c in range(3):
        v = [m[0][c], m[1][c], m[2][c]]
        L = (v[0] ** 2 + v[1] ** 2 + v[2] ** 2) ** 0.5
        if L < 1e-9:
            cols.append(([1.0, 0.0, 0.0], 1.0))
        else:
            cols.append(([v[0] / L, v[1] / L, v[2] / L], L))
    axes = [c[0] for c in cols]
    scale = [c[1] for c in cols]

    def det3(a, b, c):
        return (a[0] * (b[1] * c[2] - b[2] * c[1])
                - a[1] * (b[0] * c[2] - b[2] * c[0])
                + a[2] * (b[0] * c[1] - b[1] * c[0]))

    if det3(axes[0], axes[1], axes[2]) < 0:
        k = scale.index(min(scale))
        axes[k] = [-v for v in axes[k]]
        scale[k] = -scale[k]

    mm = [[axes[c][r] for c in range(3)] for r in range(3)]
    tr = mm[0][0] + mm[1][1] + mm[2][2]
    if tr > 0:
        s = (tr + 1.0) ** 0.5 * 2
        q = [(mm[2][1] - mm[1][2]) / s, (mm[0][2] - mm[2][0]) / s,
             (mm[1][0] - mm[0][1]) / s, 0.25 * s]
    elif mm[0][0] > mm[1][1] and mm[0][0] > mm[2][2]:
        s = (1.0 + mm[0][0] - mm[1][1] - mm[2][2]) ** 0.5 * 2
        q = [0.25 * s, (mm[0][1] + mm[1][0]) / s, (mm[0][2] + mm[2][0]) / s,
             (mm[2][1] - mm[1][2]) / s]
    elif mm[1][1] > mm[2][2]:
        s = (1.0 + mm[1][1] - mm[0][0] - mm[2][2]) ** 0.5 * 2
        q = [(mm[0][1] + mm[1][0]) / s, 0.25 * s, (mm[1][2] + mm[2][1]) / s,
             (mm[0][2] - mm[2][0]) / s]
    else:
        s = (1.0 + mm[2][2] - mm[0][0] - mm[1][1]) ** 0.5 * 2
        q = [(mm[0][2] + mm[2][0]) / s, (mm[1][2] + mm[2][1]) / s, 0.25 * s,
             (mm[1][0] - mm[0][1]) / s]
    # quaternion normalize
    ql = (q[0] ** 2 + q[1] ** 2 + q[2] ** 2 + q[3] ** 2) ** 0.5 or 1.0
    q = [v / ql for v in q]
    if q[3] < 0:
        q = [-q[0], -q[1], -q[2], -q[3]]
    return pos, q, scale


def local_matrix(pos, rot, scl):
    x, y, z, w = rot
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    m = [
        [1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy), pos[0]],
        [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx), pos[1]],
        [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy), pos[2]],
        [0.0, 0.0, 0.0, 1.0],
    ]
    for c in range(3):
        for r in range(3):
            m[r][c] *= scl[c]
    return m


def resolve_paths(paths, rig_paths):
    """Her clip yolunu 'Char/skeleton/...' kacisina baglar.
    Doner: {kaynak_yol: tam_yol}. Cozulemeyenler None."""
    rig = set(rig_paths)
    norm = {}
    for p in paths:
        p = (p or "").strip()
        if not p:
            norm[p] = None
            continue
        if p in rig:
            norm[p] = p
            continue
        segs = p.split("/")
        found = None
        # '.../spine_upper/head' -> '.../spine_upper/HeadPivot/head'
        if len(segs) >= 2 and segs[-1] == "head":
            cand = "/".join(segs[:-1] + ["HeadPivot", "head"])
            if cand in rig:
                found = cand
        if found is None:
            norm[p] = None
        else:
            norm[p] = found
    return norm


def anchor_for(paths, rig_paths, rig_prefix_of):
    """Clip'in hangi rig dugumu uzerinden oynatildigini bulur.
    paths: clip'in ham yollari. Doner: anchor prefix ('' = kok)."""
    if not paths:
        return None
    rigset = set(rig_paths)
    best, best_score = None, -1
    anchors = [""] + list(rig_paths)
    for a in anchors:
        score = 0
        for p in paths:
            p = (p or "").strip()
            full = (a + "/" + p) if (a and p) else (a or p)
            if not p:
                ok = (a in rigset)
            else:
                ok = (full in rigset)
                if not ok and p.endswith("/head"):
                    ok = ((a + "/" + p[:-5] + "/HeadPivot/head") in rigset)
                # Klip yollari bazen koktan ABSOLUT gelir:
                #   'Char/skeleton/thig_left', 'Char', 'Char/skeleton/...'
                # Bu yollar zaten rig yolu oldugundan anchor BOS olmalidir.
                if not ok and p in rigset:
                    ok = True
                if not ok and "Char/" + p in rigset:
                    ok = True
            if ok:
                score += 1
        if score > best_score:
            best, best_score = a, score
    # Esik: cogunluk cozulmus olmali. Daha once "len(paths)-1" idi ve
    # VanIn gibi kliplerde (14 yolun 2'si ayni tekrarli yol) yanlis
    # basarisiz oluyordu; asil sorun esik degil, yol KARSILASTIRMASI.
    total = len(set(p for p in paths if (p or "").strip())) or 1
    return best if best_score >= max(1, total - 1) else None


def load_clips(anims_dir, rig_paths):
    out = []
    for bundle_name, sources, loop in CLIP_SETS:
        merged = {}       # tam_yol -> (kind, keys)
        length = 0.0
        for src in sources:
            p = os.path.join(anims_dir, src + ".json")
            if not os.path.isfile(p):
                sys.stderr.write("  ! clip yok: %s\n" % src)
                continue
            d = json.load(open(p, encoding="utf-8"))
            paths = [c["path"] for c in d["curves"] if c["keys"]]
            anchor = anchor_for(paths, rig_paths, None)
            if anchor is None:
                sys.stderr.write("  ! %s: anchor bulunamadi\n" % src)
                continue
            norm = resolve_paths(paths, rig_paths)
            for c in d["curves"]:
                if not c["keys"]:
                    continue
                rp = (c["path"] or "").strip()
                full = norm.get(rp)
                if full is None:
                    if not rp:
                        full = anchor
                    else:
                        full = (anchor + "/" + rp) if anchor else rp
                kind = {"quaternion": KIND_ROT, "vector": KIND_POS}.get(c["type"], KIND_POS)
                if rp and full not in rig_paths:
                    continue                      # cozulemeyen egriyi at
                merged.setdefault(full, (kind, c["keys"]))
            for c in d["curves"]:
                for k in c["keys"]:
                    length = max(length, float(k[0]))
        if not merged:
            sys.stderr.write("  ! %s: ege gecirilecek curve yok\n" % bundle_name)
            continue
        out.append({"name": bundle_name, "loop": loop, "curves": merged,
                    "length": max(0.1, round(length, 4)),
                    "sources": sources})
        print("  [clip] %-10s %d egri, %.2fs, loop=%d  <- %s"
              % (bundle_name, len(merged), max(0.1, length), loop, ", ".join(sources)))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--npc", default="TRAFFIC/VehiclesHighway/VICTRO/LOD/Driver",
                    help="bodymesh GameObject'inin yolu")
    ap.add_argument("--name", default="MPNPC")
    ap.add_argument("--anims", default=os.path.join(EXTRACT, "anims"))
    ap.add_argument("--textures", default=os.path.join(EXTRACT, "textures"))
    ap.add_argument("--out", default=os.path.join(EXTRACT, "mpnpc.dat"))
    args = ap.parse_args()

    level = os.path.join(DATA, "level2")
    print("=" * 84)
    print("NPC   :", args.npc)
    print("CIKTI :", args.out)
    print("=" * 84)

    env = UnityPy.load(level)
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

    # hedef SMR'i bul
    target = None
    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        try:
            s = o.read()
            gpid = getattr(s.m_GameObject, "path_id", -1)
        except Exception:
            continue
        tpid = go_tr.get(gpid)
        if tpid is None or path_of(tpid) != args.npc:
            continue
        mn = getattr(deref(s.m_Mesh), "m_Name", "")
        if not str(mn).startswith("bodymesh"):
            continue
        target = (o, s, tpid)
        break
    if target is None:
        sys.stderr.write("NPC bulunamadi: %s\n" % args.npc)
        return 1
    _o, smr, mesh_pid = target
    print("bulundu: mesh pid=%d" % mesh_pid)

    # ---- mesh
    mesh = deref(smr.m_Mesh)
    verts, norms, tans, uvs = read_vertex_streams(mesh)
    indices = read_indices(mesh)
    smc, firsts = submesh_counts(mesh)
    total_idx = len(indices)
    if not smc or sum(smc) != total_idx or not firsts:
        smc = [total_idx]
        firsts = [0]
    # submesh siralamasi zaten indekste sirali oldugu icin
    # sadece ilk ucgeni ve araligi dogrula
    if len(smc) > 1 and total_idx:
        step = 2 if (firsts[1] == firsts[0] + smc[0] * 2) else 4
        for i in range(min(3, total_idx // 3)):
            a, b, c = indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2]
            if not (0 <= a < len(verts) and 0 <= b < len(verts) and 0 <= c < len(verts)):
                sys.stderr.write("  ! indeksler bozuk (ilk ucgen %d,%d,%d)\n" % (a, b, c))
                break
    print("mesh  : %d vertex, %d index, %d submesh %s firstByte=%s"
          % (len(verts), total_idx, len(smc), smc, firsts))

    # ---- bone weights
    weights = []
    for bi in mesh.m_Skin:
        weights.append((
            int(getattr(bi, "boneIndex_0_", 0)), int(getattr(bi, "boneIndex_1_", 0)),
            int(getattr(bi, "boneIndex_2_", 0)), int(getattr(bi, "boneIndex_3_", 0)),
            float(getattr(bi, "weight_0_", 0.0)), float(getattr(bi, "weight_1_", 0.0)),
            float(getattr(bi, "weight_2_", 0.0)), float(getattr(bi, "weight_3_", 0.0))))
    print("weight: %d kayit" % len(weights))

    # ---- bone listesi + rebased hiyerarsi
    bone_pids = [getattr(b, "m_PathID", 0) for b in (smr.m_Bones or [])]
    root_bone_pid = getattr(smr.m_RootBone, "path_id", 0) if smr.m_RootBone else 0

    # 'skeleton' dugumunu bul: kemikler bu dugumun ALTINDA olmali,
    # uzerindeki sahne atalari (TRAFFIC/VICTRO/...) modele girmez.
    skel_pid = None
    for pid in list(bone_pids) + [root_bone_pid]:
        cur, guard = pid, 0
        while cur in tr and guard < 64:
            if tr[cur]["n"] == "skeleton":
                skel_pid = cur
                break
            cur = tr[cur]["p"]
            guard += 1
        if skel_pid is not None:
            break
    if skel_pid is None:
        sys.stderr.write("skeleton bulunamadi\n")
        return 1

    # skeleton + tum kemikler + kemiklerin skeleton'a kadar olan ATALARI
    # (HeadPivot gibi ara dugumler sadece atalar uzerinden bulunabilir;
    #  cocuk haritasi eksik olabilir cunku bazi transform'lar okunamayabilir)
    keep = set()
    for pid in list(bone_pids) + [root_bone_pid]:
        cur, guard = pid, 0
        while cur in tr and guard < 64:
            keep.add(cur)
            if cur == skel_pid:
                break
            cur = tr[cur]["p"]
            guard += 1
    keep.add(skel_pid)
    keep = set(x for x in keep if x in tr)

    def new_path(pid):
        rel = []
        cur = pid
        while cur in tr and cur != skel_pid:
            rel.append(tr[cur]["n"])
            cur = tr[cur]["p"]
        rel.append("skeleton")
        return "Char/" + "/".join(reversed(rel))

    rig_paths = set(["Char"] + [new_path(p) for p in keep])
    print("rig   : %d dugum (%s)" % (len(rig_paths), ", ".join(sorted(
        p for p in rig_paths if p.count("/") <= 1))))

    # bone index sirasi = m_Bones sirasi (boneweight'lar buna gore)
    bindposes = {}
    try:
        for i, bp in enumerate(mesh.m_BindPose):
            vals = []
            for r in range(4):
                for c in range(4):
                    vals.append(round(float(getattr(bp, "e%d%d" % (r, c))), 6))
            if not any(vals):
                continue
            bindposes[i] = vals
    except Exception as e:
        sys.stderr.write("bindpose: %s\n" % e)
    if not bindposes and len(mesh.m_BindPose):
        sys.stderr.write("bindpose BOS - skin bozulur\n")

    # bone kayitlari: once ana (Char), sonra derinlige gore dugumler
    def depth(pid):
        d, cur, guard = 0, pid, 0
        while cur in tr and guard < 64:
            cur = tr[cur]["p"]
            d += 1
            guard += 1
        return d

    bone_recs = []
    pid_to_idx = {}
    # Char (yeni kok dugum) once
    bone_recs.append({
        "name": "Char", "path": "Char", "parent_path": "",
        "pos": [0.0, 0.0, 0.0], "rot": [0.0, 0.0, 0.0, 1.0], "scl": [1.0, 1.0, 1.0],
        "bindpose": None, "skin_index": -1, "is_skin": False})
    pid_to_idx[0] = 0
    for pid in sorted(keep, key=lambda p: (depth(p), path_of(p))):
        info = tr[pid]
        parent = info["p"]
        ppath = "Char" if (parent == 0 or parent not in keep) else new_path(parent)
        pid_to_idx[pid] = len(bone_recs)
        skin_index = bone_pids.index(pid) if pid in bone_pids else -1
        bp = bindposes.get(skin_index) if skin_index >= 0 else None
        bone_recs.append({
            "name": info["n"], "path": new_path(pid), "parent_path": ppath,
            # POZ BIND'DEN TURELIR (asagida); sahneden KOPYALANMAZ.
            "pos": None, "rot": None, "scl": None,
            "bindpose": bp,
            "skin_index": skin_index,
            "is_skin": pid in bone_pids,
        })

    path_to_idx = {}
    for i, b in enumerate(bone_recs):
        path_to_idx[b["path"]] = i
    for b in bone_recs:
        b["parent"] = path_to_idx.get(b["parent_path"],
                                      0 if b["path"] != "Char" else -1)
    root_index = path_to_idx[new_path(root_bone_pid)]

    # ------------------------------------------------------------------
    # POZ: local_i = bind_parent * bind_i^-1   (world_i = bind_i^-1, C = I)
    #
    # Skin kemigi olan dugumler icin bindpose vardir. Ara dugumler
    # (Char, skeleton, HeadPivot) bindpose'sizdir ve kimlik birakilir;
    # boylece zincir "skin kemiginin parent'i ara dugumse" durumunda da
    # tutarli kalir (world_ara = I -> local = bind_i^-1).
    # ------------------------------------------------------------------
    ident = [[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0],
             [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]]

    # GECIS 1: her dugumun BIND POZUNDAKI world matrisi.
    #   skin kemigi  -> world = bind^-1
    #   ara dugum    -> kimlik (saf gecis: Char, skeleton, HeadPivot)
    # ONEMLI: ara dugumun world'u kimliktir ama local'i kimlik DEGILDIR;
    # ebeveyninin world'unu tasir. Asagida local = world_p^-1 * world_i
    # hesabinda bu world tablosu kullanilir, bindpose'dan turetilmis bir
    # deger degil. (Onceki surum burada bindpose'a bakti ve HeadPivot
    # kelepcesi hatasi yapti: M_i*bind_i sapmasi 1.30 idi.)
    world_bind = {}
    for i, b in enumerate(bone_recs):
        bp = b["bindpose"]
        world_bind[i] = (mat_inv([[bp[r * 4 + c] for c in range(4)]
                                  for r in range(4)])
                         if bp else ident)

    # GECIS 2: local = world_parent^-1 * world
    locals_m = {}
    for i, b in enumerate(bone_recs):
        p = b["parent"]
        world_p = world_bind[p] if p >= 0 else ident
        locals_m[i] = mat_mul(mat_inv(world_p), world_bind[i])

    # DOGRULAMA: once MATRIS uzayinda (quaternion round-trip'i karistirmamak icin)
    worst_m, rt = 0.0, 0.0
    for i, b in enumerate(bone_recs):
        if not b["bindpose"]:
            continue
        acc = ident
        chain, cur, guard = [], i, 0
        while cur >= 0 and guard < 64:
            chain.append(cur)
            cur = bone_recs[cur]["parent"]
            guard += 1
        for j in reversed(chain):
            acc = mat_mul(acc, locals_m[j])
        prod_i = mat_mul(acc, [[b["bindpose"][r * 4 + c] for c in range(4)]
                              for r in range(4)])
        worst_m = max(worst_m, max(abs(prod_i[r][c] - (1.0 if r == c else 0.0))
                                   for r in range(4) for c in range(4)))

    # decompose + geri donusum hatasi
    for i, b in enumerate(bone_recs):
        pos, rot, scl = decompose(locals_m[i])
        b["pos"], b["rot"], b["scl"] = pos, rot, scl
        back = local_matrix(pos, rot, scl)
        rt = max(rt, max(abs(back[r][c] - locals_m[i][r][c])
                         for r in range(4) for c in range(4)))

    print("bind poz (matris) sapma : %.6f  %s"
          % (worst_m, "(TAMAM)" if worst_m < 1e-3 else "(BOZUK)"))
    print("TRS round-trip hata    : %.6f  %s"
          % (rt, "(TAMAM)" if rt < 1e-3 else "(BOZUK)"))
    if worst_m >= 1e-3:
        sys.stderr.write("  ! M_i*bind_i sabit degil, skinleme bozuk\n")
    # m_Bones sirasi (boneWeights bu sirayla indekslenir)
    _bone_pid_paths = [new_path(p) for p in bone_pids]
    missing = [p for p in _bone_pid_paths if p not in path_to_idx]
    if missing:
        sys.stderr.write("  ! boneRecs'te olmayan kemik: %s\n" % missing)
        _bone_pid_paths = [p for p in _bone_pid_paths if p in path_to_idx]
    print("bone  : %d kayit, %d skin kemik, rootBone=%s (%s)"
          % (len(bone_recs), len(_bone_pid_paths),
             bone_recs[root_index]["path"], bone_recs[root_index]["name"]))

    # ---- materyaller
    mats = []
    for mp in (smr.m_Materials or []):
        m = deref(mp)
        nm = getattr(m, "m_Name", "mat") if m else "mat"
        tex = nm + ".png"
        if not os.path.isfile(os.path.join(args.textures, tex)):
            alt = None
            for f in os.listdir(args.textures):
                if f.lower().startswith(nm.lower()):
                    alt = f
                    break
            tex = alt or ("char_shirt01.png")
        mats.append({"name": nm, "tex": tex})
    print("mat   : %s" % ", ".join("%s->%s" % (m["name"], m["tex"]) for m in mats))

    # ---- clip'ler
    print("clip'ler:")
    clips = load_clips(args.anims, rig_paths)

    # ---- yaz
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    f = open(args.out, "wb")
    w = Writer(f)
    f.write(b"MPNPC")
    w.i32(1)
    w.s(args.name)

    w.i32(len(verts)); w.i32(len(indices)); w.i32(len(smc))
    w.f32a([c for v in verts for c in v])
    w.f32a([c for v in norms for c in v])
    w.f32a([c for v in tans for c in v])
    w.f32a([c for v in uvs for c in v])
    for i in indices:
        w.i32(i)
    for c in smc:
        w.i32(c)

    w.i32(len(bone_recs))
    for b in bone_recs:
        w.s(b["name"]); w.s(b["path"]); w.i32(b["parent"])
        w.i32(1 if b["is_skin"] else 0)
        w.f32a(b["pos"]); w.f32a(b["rot"]); w.f32a(b["scl"])
        bp = b["bindpose"]
        if bp is None:
            bp = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
        w.f32a(bp)
    w.i32(root_index)
    # boneWeights, m_Bones sirasiyla indekslenir. C# tarafinda bones[] dizisini
    # bu sirayla kurmasi icin: skinBoneCount + boneRecs indisleri
    skin_order = [path_to_idx[psk] for psk in _bone_pid_paths]
    w.i32(len(skin_order))
    for idx in skin_order:
        w.i32(idx)

    w.i32(len(weights))
    for wt in weights:
        for k in range(4):
            w.i32(wt[k])
        for k in range(4):
            w.f32(wt[4 + k])

    w.i32(len(mats))
    for m in mats:
        w.s(m["name"]); w.s(m["tex"])

    w.i32(len(clips))
    for c in clips:
        w.s(c["name"]); w.f32(c["length"]); w.i32(c["loop"])
        w.i32(len(c["curves"]))
        for path in sorted(c["curves"].keys()):
            kind, keys = c["curves"][path]
            comp = COMPS[kind]
            w.s(path); w.i32(kind); w.i32(len(keys))
            # ONCE tum degerler, SONRA tum zamanlar
            # (verify_char_data.py ve C# okuyucu bu sirayi bekliyor)
            for kk in keys:
                for ci in range(comp):
                    w.f32(kk[1][ci] if ci < len(kk[1]) else 0.0)
            for kk in keys:
                w.f32(kk[0])
    f.close()

    print("=" * 84)
    print("YAZILDI: %s  (%.1f KB)" % (args.out, os.path.getsize(args.out) / 1024.0))
    print("=" * 84)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())