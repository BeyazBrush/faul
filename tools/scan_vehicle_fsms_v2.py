import sys, os, collections
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import UnityPy

DATA = r"D:\Steam\steamapps\common\My Summer Car\mysummercar_Data"
env = UnityPy.load(DATA)
objs = list(env.objects)

by_key = {}
for o in objs:
    by_key.setdefault((o.assets_file.name, o.path_id), o)

# GameObject adlarını topla
gos = {}
for o in objs:
    if o.type.name != "GameObject":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    k = (o.assets_file.name, o.path_id)
    gos[k] = t.get("m_Name", "")

# PlayMakerFSM'leri topla
fsm_data = []
for o in objs:
    if o.type.name != "PlayMakerFSM":
        continue
    try:
        t = o.read_typetree()
    except Exception:
        continue
    k = (o.assets_file.name, o.path_id)
    fsm_name = t.get("m_Name", "")
    fsm_fsm_name = t.get("m_FsmName", "")
    go_name = gos.get(k, "?")
    fsm_data.append({
        "path_id": k[1],
        "file": k[0],
        "fsm_name": fsm_name,
        "fsm_fsm_name": fsm_fsm_name,
        "go_name": go_name
    })

print(f"Toplam {len(fsm_data)} PlayMakerFSM bulundu")

# Gerçek araç köklerini bul (VehicleRegistry'de kullanılanlar)
# VehicleRegistry.Discover() kullanılan isimler:
# GIFU(750/450psi), HAYOSIKO(1500kg, 250), KEKMET(350-400psi), RCO_RUSCKO12(270)
# FERNDALE(1630kg), JONNEZ ES(Clone), SATSUMA(557kg, 248), COMBINE(350-400psi)

real_vehicles = {
    "GIFU(750/450psi)": None,
    "HAYOSIKO(1500kg, 250)": None,
    "KEKMET(350-400psi)": None,
    "RCO_RUSCKO12(270)": None,
    "FERNDALE(1630kg)": None,
    "JONNEZ ES(Clone)": None,
    "SATSUMA(557kg, 248)": None,
    "COMBINE(350-400psi)": None,
}

# GO isimleriyle eşleştir
for k, n in gos.items():
    if n in real_vehicles:
        real_vehicles[n] = k

print(f"\nBulunan gerçek araç kökleri: {sum(1 for v in real_vehicles.values() if v is not None)}/8\n")

# Her araç için FSM'leri listele
def resolve(fname, v):
    if isinstance(v, dict):
        fid, pid = v.get("m_FileID", 0), v.get("m_PathID", 0)
    elif isinstance(v, (list, tuple)) and len(v) >= 2 and isinstance(v[1], dict):
        fid, pid = v[0], v[1].get("m_PathID", 0)
    else:
        return None
    if fid == 0:
        return (fname, pid)
    return ("?" + str(fid), pid)

def tree(k):
    o = by_key.get(k)
    if o is None: return None
    try: return o.read_typetree()
    except Exception: return None

# by_key zaten var

for veh_name, veh_key in real_vehicles.items():
    if veh_key is None:
        print(f"\n{veh_name}: BULUNAMADI!")
        continue
    
    veh_name_short = veh_name.split("(")[0].strip()
    print(f"\n{'='*70}")
    print(f"ARAÇ: {veh_name} (path_id={veh_key[1]})")
    print(f"{'='*70}")
    
    # Transform hiyerarşisi ile alt nesneleri bul
    def get_children(tk):
        tt = tree(tk)
        if not tt: return []
        out = []
        for c in (tt.get("m_Children") or []):
            ck = resolve(tk[0], c)
            if ck:
                out.append(ck)
        return out
    
    def walk(tk, depth=0):
        t = tree(tk)
        if not t: return
        gid = resolve(tk[0], t.get("m_GameObject"))
        gname = gos.get(gid, "?") if gid else "?"
        # Bu nesnenin FSM'leri
        for cid, ref in (t.get("m_Component") or []):
            cpid = ref.get("m_PathID", 0)
            o = by_key.get((tk[0], cpid))
            if o and o.type.name == "PlayMakerFSM":
                ft = tree((tk[0], cpid))
                if ft:
                    fn = ft.get("m_Name", "")
                    fsmn = ft.get("m_FsmName", "")
                    print(f"  {'  '*depth}{gname:<30} | FSM: {fn:<25} | FsmName: {fsmn}")
        for ck in get_children(tk):
            walk(ck, depth+1)
    
    walk(veh_key)

print("\n\n" + "="*80)
print("TÜM BENZERSİZ FSM ADLARI (FsmName)")
print("="*80)
fsm_names = set()
for o in objs:
    if o.type.name == "PlayMakerFSM":
        try:
            t = o.read_typetree()
            n = t.get("m_FsmName", "")
            if n: fsm_names.add(n)
        except: pass
for n in sorted(fsm_names):
    print(f"  {n}")

print("\n\n" + "="*80)
print("BENZERSİZ FSM GO İSİMLERİ (m_Name)")
print("="*80)
go_names = set()
for o in objs:
    if o.type.name == "PlayMakerFSM":
        try:
            t = o.read_typetree()
            n = t.get("m_Name", "")
            if n: go_names.add(n)
        except: pass
for n in sorted(go_names):
    print(f"  {n}")