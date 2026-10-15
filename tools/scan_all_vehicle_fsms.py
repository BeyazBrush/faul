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
    go_k = k
    fsm_data.append({
        "path_id": k[1],
        "file": k[0],
        "fsm_name": fsm_name,
        "fsm_fsm_name": fsm_fsm_name,
        "go_name": gos.get(go_k, "?")
    })

# Araç köklerini bul (Root nesneler)
vehicle_roots = {}
for k, n in gos.items():
    # Araç isimlerini tanı
    name = n.lower()
    if any(kw in name for kw in ["satsuma", "gifu", "hayosi", "ruscko", "ferndale", "kekm", "jonnez", "combine", "tractor", "truck", "bus", "van", "ruscko12", "rco_ruscko", "hayosiko"]):
        vehicle_roots[k] = n

print(f"Toplam {len(fsm_data)} PlayMakerFSM")
print(f"Tespit edilen {len(vehicle_roots)} araç kökü\n")

# Her araç kökü için FSM'leri listele
for k, name in sorted(vehicle_roots.items(), key=lambda x: x[1]):
    print(f"\n{'='*60}")
    print(f"ARAÇ: {name} (path_id={k[1]})")
    print(f"{'='*60}")
    
    # Bu kökün altındaki tüm FSM'leri bul
    tk = k
    # Transform hiyerarşisi ile alt nesneleri bul
    for f in fsm_data:
        # FSM'in GameObject'i bu kökün altındamı?
        # Basit yöntem: GO adı içinde araç adı geçiyorsa
        go_name = f["go_name"].lower()
        veh_name_lower = name.lower()
        if veh_name_lower in go_name or go_name in veh_name_lower:
            print(f"  FSM: {f['fsm_name']:<25} | FsmName: {f['fsm_fsm_name']:<20} | GO: {f['go_name']}")
        # Veya FSM adı araçla alakalıysa
        elif any(kw in f['fsm_name'].lower() for kw in ["starter", "ignition", "dashboard", "fuel", "light", "wiper", "handbrake", "gear", "range", "clutch", "fuel", "wiper", "trigger", "player", "seat", "brake", "light", "wiper", "indicator", "indicator", "hydraul", "diff", "axle", "spill", "beacon", "pump", "lock", "lift", "valve", "steer", "wheel", "brake", "handbrake", "clutch", "engine", "engine", "engine"]):
            # Daha geniş eşleşme
            pass

# Alternatif: Tüm FSM'leri araç ismine göre grupla
print("\n\n" + "="*80)
print("TÜM FSM'LER (araç kelimeleri içerenler)")
print("="*80)

vehicle_keywords = ["satsuma", "gifu", "hayosi", "ruscko", "ferndale", "kekm", "jonnez", "combine", "tractor", "truck", "bus", "van", "ruscko12", "rco", "hayosiko", "satsuma", "kekm", "hayos", "rusck", "fernd", "jonn", "combi", "tractor"]

grouped = collections.defaultdict(list)
for f in fsm_data:
    go_l = f["go_name"].lower()
    fsm_l = f["fsm_name"].lower()
    fsmfn_l = f["fsm_fsm_name"].lower()
    
    matched_veh = None
    for kw in vehicle_keywords:
        if kw in go_l or kw in fsm_l or kw in fsmfn_l:
            matched_veh = kw
            break
    if matched_veh:
        grouped[matched_veh].append(f)

for veh, fsms in sorted(grouped.items()):
    print(f"\n--- {veh.upper()} ({len(fsms)} FSM) ---")
    for f in fsms:
        print(f"  {f['fsm_name']:<25} | {f['fsm_fsm_name']:<20} | GO: {f['go_name']}")

# Ayrıca FSM isimlerinin benzersiz listesi
print("\n\n" + "="*80)
print("BENZERSİZ FSM ADLARI (FsmName)")
print("="*80)
unique_fsmnames = sorted(set(f["fsm_fsm_name"] for f in fsm_data if f["fsm_fsm_name"]))
for n in unique_fsmnames:
    print(f"  {n}")

print("\n\n" + "="*80)
print("BENZERSİZ FSM GO İSİMLERİ")
print("="*80)
unique_go = sorted(set(f["go_name"] for f in fsm_data))
for n in unique_go:
    print(f"  {n}")