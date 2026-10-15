import sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"

# Tum klasoru yukle: MonoScript'ler ve PPtr hedefleri (sharedassets,
# resources.assets) birlikte cozulsun diye. level2 tek basina
# MonoScript sayisi 0 veriyordu.
env = UnityPy.load(DATA)
objs = list(env.objects)
by_id = {}
for o in objs:
    by_id.setdefault(o.path_id, o)

print("toplam nesne:", len(objs))

# ---- GameObject adlari ----
gos = {}
gtrees = {}
for o in objs:
    if o.type.name != "GameObject":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    pid = o.path_id
    gtrees[pid] = t
    gos[pid] = t.get("m_Name", "")

print("GameObject sayisi:", len(gos))

# ---- MonoScript -> sinif adi ----
script_name = {}
for o in objs:
    if o.type.name != "MonoScript":
        continue
    try:
        t = o.read_typetree()
        script_name[o.path_id] = t.get("m_ClassName", "") or t.get("m_Name", "")
    except Exception:
        pass
print("MonoScript sayisi:", len(script_name))

def tree(path_id):
    o = by_id.get(path_id)
    if o is None:
        return None
    try:
        return o.read_typetree()
    except Exception:
        return None

def ref_path(v):
    """m_Script gibi PPtr alanlarindan pathID cek."""
    if isinstance(v, dict):
        return v.get("m_PathID", 0)
    if isinstance(v, (list, tuple)) and len(v) >= 2 and isinstance(v[1], dict):
        return v[1].get("m_PathID", 0)
    return 0

def classify(path_id, class_id):
    """(etiket, aciklama) dondurur."""
    o = by_id.get(path_id)
    tname = o.type.name if o is not None else "?"
    if class_id == 114 or tname == "MonoBehaviour":
        t = tree(path_id) or {}
        sp = ref_path(t.get("m_Script"))
        cls = script_name.get(sp, "")
        label = cls or ("MonoBehaviour#" + str(path_id))
        keys = [k for k in t.keys()
                if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")]
        return (label, "alanlar=" + ",".join(sorted(keys)[:14]))
    return (tname, "")

def parent_chain(tpid):
    chain = []
    cur = tpid
    guard = 0
    seen = set()
    while cur and guard < 80:
        guard += 1
        if cur in seen:
            break
        seen.add(cur)
        t = tree(cur)
        if not t:
            break
        gid = ref_path(t.get("m_GameObject"))
        chain.append(gos.get(gid, "?"))
        fat = ref_path(t.get("m_Father"))
        cur = fat if fat else None
    return chain

def report(name_wanted):
    targets = [pid for pid, n in gos.items() if n.strip().lower() == name_wanted.lower()]
    print("\n=== TAM ESIT '%s' GameObject: %d adet ===" % (name_wanted, len(targets)))
    for pid in targets:
        g = gtrees[pid]
        print("\n" + "=" * 72)
        print("GameObject '%s'  pathID=%d  aktif=%s  layer=%s  tag=%s" % (
            gos[pid], pid, g.get("m_IsActive"), g.get("m_Layer"), repr(g.get("m_Tag"))))
        comps = g.get("m_Component", [])
        print("bilesen sayisi: %d" % len(comps))
        tpid = None
        for cid, ref in comps:
            cpid = ref.get("m_PathID", 0)
            label, extra = classify(cpid, cid)
            print("   - [%3d] %-28s %s" % (cid, label, extra))
            if label == "Transform":
                tpid = cpid
        if tpid:
            ch = parent_chain(tpid)
            print("\nHIYERARSI (kok -> kendisi):")
            for i, n in enumerate(ch):
                print("   " + "  " * i + n)
            tt = tree(tpid)
            lp = (tt.get("m_LocalPosition") or {})
            lr = (tt.get("m_LocalRotation") or {})
            ls = (tt.get("m_LocalScale") or {})
            print("\nYEREL KONUM : x=%.3f y=%.3f z=%.3f" % (lp.get("x", 0), lp.get("y", 0), lp.get("z", 0)))
            print("YEREL ROT   : x=%.3f y=%.3f z=%.3f w=%.3f" % (lr.get("x", 0), lr.get("y", 0), lr.get("z", 0), lr.get("w", 1)))
            print("YEREL OLCEK : x=%.3f y=%.3f z=%.3f" % (ls.get("x", 1), ls.get("y", 1), ls.get("z", 1)))
    return targets

report(sys.argv[1] if len(sys.argv) > 1 else "Julli")