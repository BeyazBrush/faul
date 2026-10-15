# -*- coding: utf-8 -*-
"""
verify_runtime.py  -  Bundle'in RUNTIME'DA calisip calismayacagini denetler.

Oyun testi yapilamadigi icin, calisma aninda en cok bozulabilecek sey
statik olarak denetleniyor:

  1) Animation component'inin clip'leri (adlar, sayi)
  2) Her legacy clip'in egri YOLLARI prefab'daki gercek Transform
     hiyerarsisiyle eslesiyor mu
       -> eslesmezse o kemik HIC ANIMASYON YAPMAZ: kollar oynar bacaklar
          doner gibi bozuk bir kukla cikar, ama hata LOGU DOKMEZ
  3) SkinnedMeshRenderer'in bones[] dizisi gercek Transform'lari gosteriyor mu
  4) bindposes sayisi == bones sayisi mi (eslesmezse skinleme bozulur)
  5) Mesh: vertex/index/boneWeight sayilari tutarli mi
  6) Materyal sayisi == submesh sayisi mi
"""
import sys
from collections import defaultdict

import UnityPy

PATH = sys.argv[1] if len(sys.argv) > 1 else r"D:\modmscMP\MSCSteamMP\assets\mpnpc"

fails = []
warns = []


def fail(msg):
    fails.append(msg)
    print("  [FAIL] " + msg)


def warn(msg):
    warns.append(msg)
    print("  [WARN] " + msg)


def ok(msg):
    print("  [ ok ] " + msg)


def deref(p):
    try:
        o = p.read()
        return o.read() if hasattr(o, "read") else o
    except Exception:
        return None


env = UnityPy.load(PATH)

# ---------------------------------------------------------------- GameObject/Transform
print("=" * 78)
print("1) HIYERARSI")
print("=" * 78)
go_name, go_tr, tr_go, tr_parent, tr_name = {}, {}, {}, {}, {}
tr_pos, tr_rot, tr_scl = {}, {}, {}
for o in env.objects:
    if o.type.name == "GameObject":
        try:
            go_name[o.path_id] = o.read().m_Name
        except Exception:
            go_name[o.path_id] = "?"
    elif o.type.name == "Transform":
        t = o.read()
        tr_name[o.path_id] = go_name.get(t.m_GameObject.path_id, "?")
        tr_parent[o.path_id] = t.m_Father.path_id
        tr_go[o.path_id] = t.m_GameObject.path_id
        go_tr[t.m_GameObject.path_id] = o.path_id
        tr_pos[o.path_id] = t.m_LocalPosition
        tr_rot[o.path_id] = t.m_LocalRotation
        tr_scl[o.path_id] = getattr(t, "m_LocalScale", None)


# ------------------------------------------------------------------ matris
def _f(v, d=0.0):
    # d = 0..2 ise x/y/z, d = 3 ise w. "xyz"[3] IndexError firlatir ve
    # o hata yutulunca w YANLISLIKLA 3 olur; kok quaternion'i bozuluyor
    # ve tum world matrisleri patliyor (sapma 547). O yuzden w ayri.
    try:
        return float(getattr(v, "w" if d == 3 else "xyz"[d]))
    except Exception:
        return d


def quat_m(q):
    x, y, z, w = _f(q, 0), _f(q, 1), _f(q, 2), _f(q, 3)
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0.0],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0.0],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0.0],
            [0.0, 0.0, 0.0, 1.0]]


def local_m(pid):
    m = quat_m(tr_rot[pid])
    s = tr_scl.get(pid)
    if s is not None:
        for c in range(3):
            k = _f(s, c)
            for r in range(3):
                m[r][c] *= k
    m[0][3], m[1][3], m[2][3] = _f(tr_pos[pid], 0), _f(tr_pos[pid], 1), _f(tr_pos[pid], 2)
    return m


def mm_mul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)]
            for r in range(4)]


def bp_mat(bp):
    """Bindpose: Unity matris duzeni, ceviri son SUTUNDA. Duz dizi
    satir-majordur (eleman(r,c) = v[r*4+c]), ceviri 12..14."""
    v = [float(getattr(bp, "e%d%d" % (r, c))) for r in range(4) for c in range(4)]
    return [v[r * 4:r * 4 + 4] for r in range(4)]


def unpack_u16(bytelist):
    b = bytearray(bytelist)
    return [b[i] | (b[i + 1] << 8) for i in range(0, len(b) - 1, 2)]


# Ebeveyn once islensin diye derinlige gore sirala
_depth = {}


def _gd(pid, g=0):
    if pid in _depth:
        return _depth[pid]
    p = tr_parent.get(pid, 0)
    d = 0 if p not in tr_name else _gd(p, g + 1) + 1
    _depth[pid] = d
    return d


for pid in tr_name:
    _gd(pid)

tr_world = {}
for pid in sorted(tr_name, key=lambda p: (_depth.get(p, 0), p)):
    m = local_m(pid)
    par = tr_parent.get(pid, 0)
    tr_world[pid] = mm_mul(tr_world[par], m) if par in tr_world else m


def tr_path(pid):
    parts, g = [], 0
    while pid in tr_name and g < 128:
        parts.append(tr_name[pid])
        pid = tr_parent.get(pid, 0)
        g += 1
    return "/".join(reversed(parts))


tr_by_path = {}
for pid in tr_name:
    tr_by_path.setdefault(tr_path(pid), pid)

roots = [p for p in tr_by_path if "/" not in p]
print("  Transform: %d   GameObject: %d   kok: %s"
      % (len(tr_name), len(go_name), ", ".join(sorted(roots)) or "-"))
if not roots:
    fail("transform hiyerarsisi bos")
if not any(r.lower() == "mpnpc" for r in roots):
    warn("kok 'MPNPC' degil: %s" % roots)

# ---------------------------------------------------------------- Animation
print()
print("=" * 78)
print("2) ANIMATION COMPONENT")
print("=" * 78)
anim_clips = []
anim_go = None
for o in env.objects:
    if o.type.name != "Animation":
        continue
    a = o.read()
    anim_go = a.m_GameObject.path_id
    for item in (getattr(a, "m_Animations", None) or []):
        if isinstance(item, (list, tuple)):
            item = item[0]
        c = deref(item)
        if c is not None:
            anim_clips.append(getattr(c, "m_Name", "?"))
    print("  component GameObject: %s" % go_name.get(anim_go, "?"))
anim_clips.sort()
print("  clip'ler (%d): %s" % (len(anim_clips), ", ".join(anim_clips)))
need = ["Idle", "Walk", "Drive", "DriveIdle"]
missing = [n for n in need if n not in anim_clips]
if missing:
    fail("Animation component'inde eksik klip: %s" % missing)
else:
    ok("zorunlu klipler mevcut: %s" % ", ".join(need))

# Clip egri YOLLARI, Animation component'inin GameObject'ine gorelidir
# ("Char/skeleton/pelvis"), prefab kokune gore DEGIL ("mpnpc/Char/...").
# Bu yuzden agac, anim_go'nun transform'undan ASAGI kurulur ve kok atilir.
print()
print("=" * 78)
print("2b) ANIMASYON YOLU REFERANSI")
print("=" * 78)
rel_by_path = {}
if anim_go is not None and anim_go in go_tr:
    anim_tid = go_tr[anim_go]
    base = tr_path(anim_tid) + "/"
    print("  Animation GameObject yolu: %s" % tr_path(anim_tid))
    for pid in tr_name:
        p = tr_path(pid)
        if p == tr_path(anim_tid):
            rel_by_path[""] = pid
        elif p.startswith(base):
            rel_by_path[p[len(base):]] = pid
    print("  Animation'dan goreli dugum: %d" % len(rel_by_path))
    if "" not in rel_by_path:
        fail("Animation GameObject'ine ait Transform yok")
    for probe in ("Char", "Char/skeleton", "Char/skeleton/pelvis",
                  "Char/skeleton/pelvis/spine_middle"):
        print("    %-40s %s" % (probe, "var" if probe in rel_by_path else "YOK"))
else:
    fail("Animation GameObject'i cozulemedi")

# ---------------------------------------------------------------- clip paths
print()
print("=" * 78)
print("3) CLIP EGRI YOLLARI  (en kritik kontrol)")
print("=" * 78)
all_curves = defaultdict(set)
for o in env.objects:
    if o.type.name != "AnimationClip":
        continue
    c = o.read()
    name = getattr(c, "m_Name", "?")
    paths, nattrs, keys = set(), set(), 0
    # Unity 5 legacy klip: rotasyon m_RotationCurves'te, konum
    # m_PositionCurves'te, olcek m_ScaleCurves'te, float ise m_FloatCurves'te.
    # (Oyunun kendi klip'leriyle ayni yer; m_FloatCurves BOS kalir.)
    for field in ("m_RotationCurves", "m_PositionCurves", "m_ScaleCurves",
                  "m_FloatCurves"):
        for el in (getattr(c, field, None) or []):
            p = getattr(el, "path", None)
            if p is None:
                continue
            paths.add(p)
            nattrs.add(getattr(el, "attribute", "") or field[2:-6])
            curve = getattr(el, "curve", None)
            if curve is not None:
                k = getattr(curve, "m_Curve", None)
                if k is None:
                    k = getattr(curve, "keys", None)
                if k:
                    keys += len(k)
            else:
                ks = getattr(el, "m_Curve", None)
                if ks is not None:
                    keys += len(ks)
    if paths:
        bad = sorted(p for p in paths if p and p not in rel_by_path)
        all_curves[name] = paths
        flag = "" if not bad else ("  COZULEMEYEN: %s" % bad)
        print("  %-10s egriler=%-3d yol=%-2d anahtar=%-4d%s"
              % (name, len(nattrs), len(paths), keys, flag))
        if bad:
            fail("%s: %d egrinin yolu prefab'da yok" % (name, len(bad)))
        if keys == 0:
            fail("%s: anahtar yok, animasyon oynamaz" % name)

union = set()
for p in all_curves.values():
    union |= p
print()
print("  toplam farkli yol: %d" % len(union))
if not all_curves:
    fail("hicbir clip okunamadi")

# ---------------------------------------------------------------- SMR / skin
print()
print("=" * 78)
print("4) SKINNING")
print("=" * 78)
for o in env.objects:
    if o.type.name != "SkinnedMeshRenderer":
        continue
    s = o.read()
    bones = list(s.m_Bones or [])
    rp = s.m_RootBone.path_id if s.m_RootBone else 0
    mesh = deref(s.m_Mesh)
    print("  GameObject : %s" % go_name.get(s.m_GameObject.path_id, "?"))
    print("  mesh       : %s" % getattr(mesh, "m_Name", "?"))
    print("  bones      : %d" % len(bones))
    print("  rootBone   : %s (pathID %d)" % (tr_name.get(rp, "?"), rp))
    bad = [b.m_PathID for b in bones if b.m_PathID not in tr_name]
    if bad:
        fail("bones[] icinde cozulemeyen Transform: %s" % bad)
    elif rp not in tr_name:
        fail("rootBone bir Transform degil")
    else:
        ok("tum kemikler ve rootBone gercek Transform")
    if rp not in [b.m_PathID for b in bones]:
        warn("rootBone, bones[] icinde degil (Unity bunu normalde kabul eder)")

    if mesh is None:
        fail("mesh cozulemedi")
        continue
    vd = getattr(mesh, "m_VertexData", None)
    vc = vd.m_VertexCount if vd else 0
    bp = list(getattr(mesh, "m_BindPose", []) or [])
    skin = list(getattr(mesh, "m_Skin", []) or [])
    idxbuf = list(getattr(mesh, "m_IndexBuffer", []) or [])
    print("  vertex     : %d" % vc)
    print("  bindposes  : %d" % len(bp))
    print("  boneWeight : %d" % len(skin))
    print("  indexBuffer: %d" % len(idxbuf))
    if len(bp) != len(bones):
        fail("bindposes (%d) != bones (%d)" % (len(bp), len(bones)))
    else:
        ok("bindposes sayisi bones ile esit")
    if skin and len(skin) != vc:
        fail("boneWeight (%d) != vertex (%d)" % (len(skin), vc))
    maxbi = 0
    for w in skin[:5]:
        maxbi = max(maxbi, getattr(w, "boneIndex_0_", 0))
    if maxbi >= len(bones):
        fail("boneIndex (%d) bones sayisini (%d) asiyor" % (maxbi, len(bones)))

    # --- BIND POZ TUTARLILIGI --------------------------------------------
    # Bag pozunda  M_i * bind_i  butun kemikler icin SABIT olmalidir.
    # Sapma varsa iskelet ile mesh birbirine bagli degildir: skinleme
    # ucgenleri savurur, kiyfetler bozuk gorunur, model parlar. Bu kontrol
    # olmadan bu hata "NPC'ler arabanin icinde oturuyordu" gibi gorunmez.
    # Bozun olustugu tarih: iskelet local TRS'leri bir NPC sahne pozundan
    # kopyalanmis, bindposeler T-pozdan geliyordu -> sapma 1.45.
    if bp and len(bp) == len(bones):
        worst = 0.0
        prod0 = None
        for k, b in enumerate(bones):
            pid = b.m_PathID
            wm = tr_world.get(pid)
            if wm is None:
                continue
            m = mm_mul(wm, bp_mat(bp[k]))
            if prod0 is None:
                prod0 = m
            worst = max(worst, max(abs(m[r][c] - prod0[r][c])
                                   for r in range(4) for c in range(4)))
        print("  bind poz   : en buyuk sapma %.6f" % worst)
        if worst < 1e-3:
            ok("M_i*bind_i sabit (iskelet bag pozla tutarli)")
        else:
            fail("M_i*bind_i SABIT DEGIL (sapma %.4f) -> skinleme bozuk, "
                 "kiyafetler/model patlar" % worst)

    sub = list(getattr(mesh, "m_SubMeshes", []) or [])
    sc = [getattr(x, "indexCount", 0) or 0 for x in sub]
    print("  submesh    : %d  %s" % (len(sub), sc))
    want = sum(sc)
    if len(idxbuf) == want * 2 and max(idxbuf) <= 255:
        idxbuf = unpack_u16(idxbuf)
    print("  index      : %d (ham bayt %d)"
          % (len(idxbuf), len(list(getattr(mesh, "m_IndexBuffer", []) or []))))
    if want and len(idxbuf) != want:
        fail("index sayisi beklenen %d, gelen %d (indeksler ham bayt "
             "olarak okunmus olabilir)" % (want, len(idxbuf)))
    elif idxbuf and max(idxbuf) >= vc:
        fail("indeks %d vertex sayisini (%d) asiyor" % (max(idxbuf), vc))
    else:
        ok("indeksler vertex sinirlari icinde")

    mats = list(s.m_Materials or [])
    mnames = []
    for mp in mats:
        m = deref(mp)
        mnames.append(getattr(m, "m_Name", "?") if m else "?")
    print("  materyaller: %d  %s" % (len(mats), ", ".join(mnames)))
    if len(sub) and len(mats) != len(sub):
        fail("materyal (%d) != submesh (%d)" % (len(mats), len(sub)))
    elif mats:
        ok("materyal/submesh esit")
    for mn in mnames:
        pass

# ---------------------------------------------------------------- materyal -> doku
print()
print("=" * 78)
print("5) MATERYAL DOKULARI")
print("=" * 78)
texnames = set()
for o in env.objects:
    if o.type.name == "Texture2D":
        try:
            texnames.add(o.read().m_Name)
        except Exception:
            pass
print("  Texture2D: %d" % len(texnames))
for o in env.objects:
    if o.type.name != "Material":
        continue
    m = o.read()
    sp = getattr(m, "m_SavedProperties", None)
    used = []
    if sp is not None:
        # UnityPy 1.25'te m_TexEnvs ogeleri TUPLE:
        #   (FastPropertyName('_MainTex'), UnityTexEnv(m_Texture=PPtr))
        for tf in (getattr(sp, "m_TexEnvs", None) or []):
            prop, env = None, None
            if isinstance(tf, (list, tuple)) and len(tf) >= 2:
                prop, env = tf[0], tf[1]
            else:
                prop = getattr(tf, "first", None)
                env = getattr(tf, "second", None)
            pname = getattr(prop, "name", str(prop) if prop else "?")
            ptr = getattr(env, "m_Texture", None)
            if ptr is None:
                continue
            # PPtr m_PathID, ayni SerializedFile icindeki Texture2D'ye gider
            t = None
            try:
                t = ptr.read()
                if hasattr(t, "read"):
                    t = t.read()
            except Exception:
                t = None
            tn = getattr(t, "m_Name", None)
            if tn is None:
                tn = "pathID=%s" % getattr(ptr, "m_PathID", "?")
            used.append("%s->%s" % (pname, tn))
    print("  %-18s -> %s" % (getattr(m, "m_Name", "?"),
                            ", ".join(used) if used else "(doku referansi yok)"))
    if not used:
        fail("%s: _MainTex referansi yok" % getattr(m, "m_Name", "?"))

print()
print("=" * 78)
if fails:
    print("SONUC: %d HATA, %d UYARI" % (len(fails), len(warns)))
    for f in fails:
        print("  - " + f)
    sys.exit(1)
print("SONUC: TEMIZ (%d uyari)" % len(warns))
for w in warns:
    print("  - " + w)