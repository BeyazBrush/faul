# -*- coding: utf-8 -*-
"""
render_npc.py  -  Bundle'daki NPC modelini CPU ile RENDER EDER.

Neden: oyun calistirilamadan "kiyafetler bozuk" ve "dik durmuyor"
sorusuna kesin cevap veren tek yol. Mesh, iskelet, bindpose, bone weight
ve dokular hepsi bundle'da; hepsi CPU'da uygulanip goruntu yazilabilir.

Yapar:
  1) kemik world matrislerini kurar (prefab kok donusumu dahil)
  2) CPU skinning:  p' = sum_i w_i * (M_i * bindpose_i) * p
  3) ortografik projeksiyon + z-buffer
  4) submesh -> materyal -> doku eslemesiyle UV'lu rasterizasyon
  5) PNG yazar

Kullanim:
  python render_npc.py                       varsayilan skin
  python render_npc.py --skin 5              6. gorunum
  python render_npc.py --ortho-front         onden
"""
import argparse
import os
import struct
import sys

import numpy as np
from PIL import Image

BUNDLE = r"D:\modmscMP\MSCSteamMP\assets\mpnpc"
TEXDIR = r"D:\MSCSteamMP\extracted_npc\textures"
OUTDIR = r"C:\Users\aorus\AppData\Local\Temp\opencode\npcrender"

W, H = 620, 900
BG = (18, 18, 22)


# ------------------------------------------------------------------ okuma
def rstr(f):
    n, shift = 0, 0
    while True:
        x = f.read(1)[0]
        n |= (x & 0x7F) << shift
        shift += 7
        if not (x & 0x80):
            break
    return f.read(n).decode("utf-8", "replace")


def read_bundle(path):
    """Bundle'dan mesh + iskelet + materyal/doku bilgisini cikarir."""
    import UnityPy
    env = UnityPy.load(path)

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
            tr_local[pid] = (t.m_LocalPosition, t.m_LocalRotation,
                             getattr(t, "m_LocalScale", None))
            tr_go[pid] = t.m_GameObject.path_id

    roots = [p for p in tr_local if tr_parent.get(p, 0) not in tr_local]
    root = roots[0]

    mesh = None
    smr_bones = smr_root = smr_mats = None
    for o in env.objects:
        if o.type.name == "Mesh":
            mesh = o.read()
        elif o.type.name == "SkinnedMeshRenderer":
            s = o.read()
            smr_bones = [b.m_PathID for b in (s.m_Bones or [])]
            smr_root = s.m_RootBone.m_PathID if s.m_RootBone else 0
            smr_mats = list(s.m_Materials or [])
    if mesh is None:
        raise SystemExit("mesh bulunamadi")

    verts, norms, uvs, tans = read_vertex_data(mesh)
    subc = [int(getattr(x, "indexCount", 0) or 0) for x in mesh.m_SubMeshes]
    # m_IndexBuffer 16-bit indekslerin HAMI bayt listesi olarak gelir
    # (8040 indeks = 16080 byte). Elemanlar 0..255 ise mutlaka bayttir;
    # dogrudan kullanilirsa indeksler 0..255 olur, ucgenler rastgele
    # vertekslere baglanir ve model PATLAR.
    ib = mesh.m_IndexBuffer
    want = sum(subc)
    if isinstance(ib, (bytes, bytearray)):
        idx = [int(x) for x in np.frombuffer(bytes(ib), dtype="<u2")]
    elif isinstance(ib, list) and len(ib) == want * 2 and max(ib) <= 255:
        idx = [int(x) for x in np.frombuffer(bytearray(ib), dtype="<u2")]
    else:
        idx = [int(i) for i in ib]
    if len(idx) != want:
        raise SystemExit("indeks sayisi beklenen %d, gelen %d" % (want, len(idx)))

    # bone weight (m_Skin) ve bindpose
    skin = []
    for bi in mesh.m_Skin:
        skin.append((getattr(bi, "boneIndex_0_", 0), getattr(bi, "boneIndex_1_", 0),
                     getattr(bi, "boneIndex_2_", 0), getattr(bi, "boneIndex_3_", 0),
                     getattr(bi, "weight_0_", 0.0), getattr(bi, "weight_1_", 0.0),
                     getattr(bi, "weight_2_", 0.0), getattr(bi, "weight_3_", 0.0)))
    bind = []
    for bp in mesh.m_BindPose:
        bind.append([float(getattr(bp, "e%d%d" % (r, c))) for r in range(4)
                     for c in range(4)])

    # Transform path_id -> GameObject adi
    tr_name = {}
    for pid, gpid in tr_go.items():
        tr_name[pid] = go_name.get(gpid, "?")

    return dict(root=root, verts=verts, norms=norms, uvs=uvs, tans=tans,
                idx=idx, subc=subc, skin=skin, bind=bind,
                bones=smr_bones, rootbone=smr_root, mats=smr_mats,
                tr_local=tr_local, tr_parent=tr_parent, go_name=go_name,
                tr_name=tr_name, _bundle=path)


def read_vertex_data(mesh):
    """Unity 5 m_VertexData -> (verts, normals, uvs, tangents).

    DİKKAT: stream içinde bilek BAŞINA STRIDE vardır (stream 0 = 40 bayt).
    np.frombuffer(count=3n, offset=..) ARDIŞIK 3n float okur ve stride'ı
    yok sayar — pozisyon, normal ve tangent'i birbirine karıştırır. Doğru
    okuma: tüm tamponu float dizisine çevir, sonra her verteks için
    (i*stride + bileşen) indeksle.
    """
    verts, norms, uvs, tans = [], [], [], []
    vd = mesh.m_VertexData
    n = int(vd.m_VertexCount)
    data = np.frombuffer(mesh.m_VertexData.m_DataSize, dtype="<f4")
    streams = {}
    for ch in vd.m_Channels:
        if int(ch.dimension) <= 0:
            continue
        streams.setdefault(int(ch.stream), []).append(
            (int(ch.offset), int(ch.dimension)))
    base = 0
    for st in sorted(streams):
        items = sorted(streams[st])
        stride_f = sum(d for _, d in items)      # float cinsinden stride
        rows = np.arange(n) * stride_f + base // 4
        for off, dim in items:
            cols = rows[:, None] + np.arange(dim)[None, :] + off // 4
            arr = data[cols]
            if dim == 2:
                uvs.append(arr)
            elif dim == 3:
                (norms if (st == 0 and off == 12) else verts).append(arr)
            elif dim == 4:
                tans.append(arr)
        base += stride_f * 4 * n
    return (np.vstack(verts), np.vstack(norms), np.vstack(uvs),
            np.vstack(tans) if tans else np.zeros((n, 4)))


# ------------------------------------------------------------------ matris
def q_to_m(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0.0],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0.0],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0.0],
        [0.0, 0.0, 0.0, 1.0]])


def trs_m(pos, rot, scl):
    m = q_to_m(rot)
    m = m.copy()
    for c in range(3):
        m[:3, c] *= scl[c]
    m[:3, 3] = pos
    return m


def q4(q):
    try:
        return (float(q.x), float(q.y), float(q.z), float(q.w))
    except Exception:
        return tuple(float(v) for v in q)


def v3(p):
    try:
        return np.array([float(p.x), float(p.y), float(p.z)])
    except Exception:
        return np.array([float(v) for v in p])


# ------------------------------------------------------------------ render
def read_clips(path):
    """Bundle'daki AnimationClip'leri okur.

    Unity eski (legacy) clip'lerinde rotasyon egrileri MUTLAK local
    deger tasir; runtime'da Animation component.localRotation = egri(t)
    yapar. Tek anahtarli egri, kemigi sabit bir aciye KITAR ve bag pozunu
    tamamen ezer (Idle'da 4 kol/kol kemigi boyle)."""
    import UnityPy
    env = UnityPy.load(path)
    out = {}
    for o in env.objects:
        if o.type.name != "AnimationClip":
            continue
        c = o.read()
        rot, pos = {}, {}
        for k in (getattr(c, "m_RotationCurves", None) or []):
            ks = list(k.curve.m_Curve)
            rot[k.path] = [(float(f.time),
                            (float(f.value.x), float(f.value.y),
                             float(f.value.z), float(f.value.w)))
                           for f in ks]
        for k in (getattr(c, "m_PositionCurves", None) or []):
            ks = list(k.curve.m_Curve)
            pos[k.path] = [(float(f.time),
                            (float(f.value.x), float(f.value.y), float(f.value.z)))
                           for f in ks]
        out[getattr(c, "m_Name", "?")] = {"rot": rot, "pos": pos}
    return out


def sample(keys, t):
    """Egriden t anindaki degeri ornekler (lineer araliklama)."""
    if not keys:
        return None
    if t <= keys[0][0]:
        return keys[0][1]
    if t >= keys[-1][0]:
        return keys[-1][1]
    for i in range(1, len(keys)):
        if t <= keys[i][0]:
            t0, v0 = keys[i - 1]
            t1, v1 = keys[i]
            k = 0.0 if t1 <= t0 else (t - t0) / (t1 - t0)
            if len(v0) == 4:
                x0, y0, z0, w0 = v0
                x1, y1, z1, w1 = v1
                if x0 * x1 + y0 * y1 + z0 * z1 + w0 * w1 < 0:
                    x1, y1, z1, w1 = -x1, -y1, -z1, -w1
                return (x0 + (x1 - x0) * k, y0 + (y1 - y0) * k,
                        z0 + (z1 - z0) * k, w0 + (w1 - w0) * k)
            return tuple(a + (b - a) * k for a, b in zip(v0, v1))
    return keys[-1][1]


def apply_clip(d, clip_name, t):
    """Clip'i MUTLAK local deger olarak iskelete uygular; degisen
    kemiklerin path_id listesini dondurur."""
    clips = read_clips(d["_bundle"])
    if clip_name not in clips:
        raise SystemExit("clip yok: %s (var: %s)" % (clip_name, ", ".join(clips)))
    c = clips[clip_name]

    by_path = {}
    for pid in d["tr_local"]:
        parts, g = [], 0
        cur = pid
        while cur in d["tr_local"] and g < 128:
            parts.append(d["tr_name"].get(cur, "?"))
            cur = d["tr_parent"].get(cur, 0)
            g += 1
        full = "/".join(reversed(parts))
        by_path.setdefault(full, pid)
        # Clip egri yollari Animation component'inin GameObject'ine
        # GORELIDIR ("Char/skeleton/pelvis"), prefab kokune gore degil
        # ("mpnpc/Char/skeleton/pelvis"). parts dusende -> kokte dogru
        # siralidir, yani kok parts[-1]'dir; ONDAN SONRAKI halkayi at.
        rel = "/".join(reversed(parts[:-1]))
        if rel:
            by_path.setdefault(rel, pid)

    rot, pos, touched = {}, {}, []
    for path, keys in c["rot"].items():
        pid = by_path.get(path)
        if pid is None:
            continue
        v = sample(keys, t)
        rot[pid] = v
        touched.append((path, len(keys), "ROT", v))
    for path, keys in c["pos"].items():
        pid = by_path.get(path)
        if pid is None:
            continue
        v = sample(keys, t)
        pos[pid] = v
        touched.append((path, len(keys), "POS", v))
    return rot, pos, touched


def render(d, tex_of_submesh, title, out, local_rot=None, local_pos=None,
           spin_deg=0.0):
    verts = d["verts"]
    uvs = d["uvs"]
    skin = np.array(d["skin"], dtype=np.float64)
    bind = np.array(d["bind"], dtype=np.float64)
    tr_local, tr_parent = d["tr_local"], d["tr_parent"]

    # --- kemik world matrisleri (TUM transform'lar, once ebeveynler)
    world = {}
    depth = {}

    def get_depth(pid, guard=0):
        if pid in depth:
            return depth[pid]
        if pid not in tr_local or guard > 128:
            return 0
        p = tr_parent.get(pid, 0)
        d2 = 0 if p not in tr_local else get_depth(p, guard + 1) + 1
        depth[pid] = d2
        return d2

    for pid in tr_local:
        get_depth(pid)
    order = sorted(tr_local.keys(), key=lambda p: (depth.get(p, 0), p))
    for pid in order:
        lp, lq, ls = tr_local[pid]
        scale = (np.array([float(getattr(ls, "x", 1.0)), float(getattr(ls, "y", 1.0)),
                           float(getattr(ls, "z", 1.0))])
                 if ls is not None else np.array([1.0, 1.0, 1.0]))
        m = trs_m(v3(lp), q4(lq), scale)
        # animasyonla degisen kemikler
        if local_rot and pid in local_rot:
            m = trs_m(v3(lp), local_rot[pid], scale)
        if local_pos and pid in local_pos:
            m = trs_m(local_pos[pid], local_rot.get(pid, q4(lq)), scale)
        p = tr_parent.get(pid, 0)
        world[pid] = (world[p] @ m) if p in world else m
    if spin_deg:
        from math import cos, sin, radians
        a = radians(spin_deg)
        rot = np.array([[cos(a), 0, sin(a), 0], [0, 1, 0, 0],
                        [-sin(a), 0, cos(a), 0], [0, 0, 0, 1]], dtype=np.float64)
        for k in world:
            world[k] = rot @ world[k]

    # --- skinning
    nb = len(d["bones"])
    mats = np.zeros((nb, 4, 4))
    for k, pid in enumerate(d["bones"]):
        mats[k] = world[pid] @ bind[k].reshape(4, 4)

    outv = np.zeros_like(verts)
    for c in range(4):
        idx = skin[:, c].astype(int)
        w = skin[:, 4 + c]
        m = mats[idx]
        outv += (np.einsum("nij,nj->ni", m[:, :3, :3], verts)
                 + m[:, :3, 3]) * w[:, None]

    # --- kameraya oturt (ortografik, onden bakan: -Z'ye bakis yerine +Z)
    center = (outv.min(0) + outv.max(0)) * 0.5
    scale = (H - 60) / max(outv[:, 1].max() - outv[:, 1].min(),
                          outv[:, 2].max() - outv[:, 2].min())

    def project(p):
        x = p[0] - center[0]
        y = p[1] - center[1]
        return np.array([W * 0.5 + x * scale, H * 0.5 - y * scale])

    sp = np.array([project(p) for p in outv])
    depth = outv[:, 2].copy()

    img = np.zeros((H, W, 3), dtype=np.float64)
    img[:] = BG
    zbuf = np.full((H, W), 1e18)

    # --- submesh -> doku
    cursor = 0
    imgs = []
    for si, cnt in enumerate(d["subc"]):
        tri = np.array(d["idx"][cursor:cursor + cnt], dtype=int).reshape(-1, 3)
        cursor += cnt
        imgs.append((si, tri, tex_of_submesh(si)))

    order_tris = []
    for si, tri, tex in imgs:
        if tex is None:
            avg = np.array([200, 120, 120], dtype=np.float64)
            for t in tri:
                order_tris.append((si, t, None, avg))
            continue
        arr = np.asarray(tex, dtype=np.float64)
        th, tw = arr.shape[:2]
        for t in tri:
            order_tris.append((si, t, (arr, tw, th), None))

    # derinlige gore arkadan one
    for si, t, tex, avg in sorted(order_tris, key=lambda x: -depth[x[1][0]]):
        x0, y0 = sp[t[0]]
        x1, y1 = sp[t[1]]
        x2, y2 = sp[t[2]]
        minx = max(int(min(x0, x1, x2)), 0)
        maxx = min(int(max(x0, x1, x2)) + 1, W)
        miny = max(int(min(y0, y1, y2)), 0)
        maxy = min(int(max(y0, y1, y2)) + 1, H)
        if maxx <= minx or maxy <= miny:
            continue
        d00 = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
        if abs(d00) < 1e-9:
            continue
        xs = np.arange(minx, maxx) + 0.5
        ys = np.arange(miny, maxy) + 0.5
        gx, gy = np.meshgrid(xs, ys)
        # Alan formulu (HER TERIM ISARETI ONEMLI):
        #   d  = (x1-x0)(y2-y0) - (x2-x0)(y1-y0)
        #   l0 = ((x1-x)(y2-y) - (x2-x)(y1-y)) / d
        #   l1 = ((x2-x)(y0-y) - (x0-x)(y2-y)) / d
        #   l2 = 1 - l0 - l1
        # Onceki surumde l0'da a*(y1-y0) terimi, l1'de ise iki terimin
        # ISARETI ters yazilmisti; sonuc: ucgenler dogru yerde ama UV
        # arada tasiyor -> model "kirik cam" gibi gorunuyordu.
        l0 = ((x1 - gx) * (y2 - gy) - (x2 - gx) * (y1 - gy)) / d00
        l1 = ((x2 - gx) * (y0 - gy) - (x0 - gx) * (y2 - gy)) / d00
        l2 = 1.0 - l0 - l1
        inside = (l0 >= -1e-6) & (l1 >= -1e-6) & (l2 >= -1e-6)
        if not inside.any():
            continue
        b0, b1, b2 = l0, l1, l2
        z = depth[t[0]] * b0 + depth[t[1]] * b1 + depth[t[2]] * b2
        sub_z = zbuf[miny:maxy, minx:maxx]
        ok = inside & (z < sub_z)
        if not ok.any():
            continue
        if tex is None:
            col = np.tile(avg, (int(ok.sum()), 1))
        else:
            arr, tw, th = tex
            u = (uvs[t[0], 0] * b0 + uvs[t[1], 0] * b1 + uvs[t[2], 0] * b2)
            v = (uvs[t[0], 1] * b0 + uvs[t[1], 1] * b1 + uvs[t[2], 1] * b2)
            tu = np.clip((u * tw).astype(int), 0, tw - 1)
            tv = np.clip(((1.0 - v) * th).astype(int), 0, th - 1)
            col = arr[tv, tu][ok]
        block = img[miny:maxy, minx:maxx]
        block[ok] = col
        sub_z[ok] = z[ok]

    out_img = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    out_img.save(out)
    print("  yazildi: %s   (%s)" % (out, title))
    return out_img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=BUNDLE)
    ap.add_argument("--skin", type=int, default=-1)
    ap.add_argument("--out", default=OUTDIR)
    ap.add_argument("--pose", default=None,
                    help="klip adi (Idle/Walk/...) - MUTLAK local olarak uygular")
    ap.add_argument("--t", default=0.0, help="klip zamani (s)")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    d = read_bundle(args.bundle)
    print("mesh  : %d vertex, %d ucgen, submesh %s"
          % (len(d["verts"]), len(d["idx"]) // 3, d["subc"]))
    print("kemik : %d, rootBone pathID %d" % (len(d["bones"]), d["rootbone"]))

    import UnityPy
    env = UnityPy.load(args.bundle)
    mat_tex = {}
    for o in env.objects:
        if o.type.name == "Material":
            m = o.read()
            for tf in (getattr(m.m_SavedProperties, "m_TexEnvs", None) or []):
                env2 = tf[1] if isinstance(tf, (list, tuple)) and len(tf) >= 2 else None
                ptr = getattr(env2, "m_Texture", None)
                if ptr is None:
                    continue
                try:
                    t = ptr.read()
                    if hasattr(t, "read"):
                        t = t.read()
                    mat_tex[getattr(m, "m_Name", "")] = getattr(t, "m_Name", None)
                except Exception:
                    pass
    print("materyal->doku:", mat_tex)

    def deref_mat(i):
        ptr = d["mats"][i]
        try:
            o = ptr.read()
            m = o.read() if hasattr(o, "read") else o
            return getattr(m, "m_Name", None)
        except Exception:
            return None

    default = [mat_tex.get(deref_mat(i) or "", None) for i in range(len(d["mats"]))]
    print("varsayilan submesh -> doku:", default)

    cache = {}

    def tex(n):
        if not n:
            return None
        if n not in cache:
            p = os.path.join(TEXDIR, n + ".png")
            if not os.path.isfile(p):
                return None
            cache[n] = np.asarray(Image.open(p).convert("RGB"), dtype=np.float64)
        return cache[n]

    def make(skin_idx, tag):
        if skin_idx < 0:
            names = default
        else:
            sys.path.insert(0, r"D:\modmscMP\MSCSteamMP\src\MSCSteamMP\Player")
            names = None
        if skin_idx >= 0:
            # katalogdan oku
            import re
            src = open(r"D:\modmscMP\MSCSteamMP\src\MSCSteamMP\Player"
                       r"\NpcSkinCatalog.cs", encoding="utf-8").read()
            m = re.findall(r'new NpcSkin\("([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)"\)', src)
            nm, sh, pa, fa = m[skin_idx]
            names = [sh, pa, fa]
            print("  skin %d: %s = %s / %s / %s" % (skin_idx, nm, sh, pa, fa))
        return lambda i: tex(names[i] if i < len(names) else None)

    render(d, make(-1, "varsayilan"), "varsayilan (shirt04/pants01/face02)",
           os.path.join(args.out, "npc_default.png"))

    # secili skin ile de ciz (ekran goruntusuyle karsilastirma icin)
    import re as _re
    _src = open(r"D:\modmscMP\MSCSteamMP\src\MSCSteamMP\Player"
                r"\NpcSkinCatalog.cs", encoding="utf-8").read()
    _rows = _re.findall(r'new NpcSkin\("([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)"\)', _src)
    if args.skin >= 0 and args.skin < len(_rows):
        _nm, _sh, _pa, _fa = _rows[args.skin]
        print("  skin %d: %s = %s / %s / %s" % (args.skin, _nm, _sh, _pa, _fa))
        if args.pose:
            _rot, _pos, _ = apply_clip(d, args.pose, float(args.t))
            render(d, (lambda i: tex([_sh, _pa, _fa][i] if i < 3 else None)),
                   _nm, os.path.join(args.out, "npc_skin%d_%s.png"
                                     % (args.skin, args.pose)),
                   local_rot=_rot, local_pos=_pos)
        else:
            render(d, (lambda i: tex([_sh, _pa, _fa][i] if i < 3 else None)), _nm,
                   os.path.join(args.out, "npc_skin%d.png" % args.skin))

    # --- animasyonlu poz
    if args.pose:
        t = float(args.t)
        rot, pos, touched = apply_clip(d, args.pose, t)
        print()
        print("KLIP %s @ t=%.3f  -> %d kemik etkilendi"
              % (args.pose, t, len(rot) + len(pos)))
        for path, n, kind, v in touched:
            s = ("[%s]" % ", ".join("%.3f" % x for x in v)) if kind == "ROT" \
                else ("[%s]" % ", ".join("%.3f" % x for x in v))
            print("   %-64s n=%-3d %s %s" % (path[-64:], n, kind, s))
        print("   -> bag pozdaki degerle AYNI olan kemikler (tek anahtar = "
              "kemigi donduruyor):")
        for path, n, kind, v in touched:
            if kind != "ROT" or n > 1:
                continue
            pid = [p for p in rot if True]
            parts = path.split("/")
            # bu kemigin bag poz rotasyonunu bul
            cand = [p for p in d["tr_local"]
                    if d["tr_name"].get(p) == parts[-1]]
            if cand:
                lq = d["tr_local"][cand[0]][1]
                rest = (float(lq.x), float(lq.y), float(lq.z), float(lq.w))
                dot = abs(sum(a * b for a, b in zip(v, rest)))
                print("      %-22s bag=%s  fark=%.1f derece  %s"
                      % (parts[-1],
                         "(%.2f,%.2f,%.2f,%.2f)" % rest,
                         (1 - min(1.0, dot)) * 180.0,
                         "AYNI" if dot > 0.999 else "DEGISIK !!"))
        render(d, make(-1, "varsayilan"),
               "%s t=%.2f" % (args.pose, t),
               os.path.join(args.out, "npc_%s_t%03d.png" % (args.pose, int(t * 100))),
               local_rot=rot, local_pos=pos)
        return 0

    import re
    src = open(r"D:\modmscMP\MSCSteamMP\src\MSCSteamMP\Player"
               r"\NpcSkinCatalog.cs", encoding="utf-8").read()
    rows = re.findall(r'new NpcSkin\("([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)"\)', src)
    for idx in (int(args.skin) if args.skin >= 0 else 8,):
        if 0 <= idx < len(rows):
            nm, sh, pa, fa = rows[idx]
            print("  skin %d: %s = %s / %s / %s" % (idx, nm, sh, pa, fa))
            render(d, (lambda i: tex([sh, pa, fa][i] if i < 3 else None)), nm,
                   os.path.join(args.out, "npc_%d_%s.png" % (idx, nm)))


if __name__ == "__main__":
    main()