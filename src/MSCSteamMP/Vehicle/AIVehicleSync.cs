using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Yapay Zeka Araç Senkronizasyonu (otobüs, tren, iş makinesi gibi
    /// oyun tarafından sürülen, oyuncunun binemediği araçlar).
    ///
    /// NEDEN AYRI BİR TABLO: <see cref="VehicleRegistry"/> oyuncu tarafından
    /// sürülebilen ARAÇLARI takip eder ve 20 Hz Transform gönderir. Bu
    /// dosyadaki tablo, VehicleRegistry'nin bilmediği araçları taşır.
    ///
    /// İKİ KURAL (birlikte uygulanır, ikisi de şart):
    ///   1. VehicleRegistry.Vehicles'ta ZATEN kayıtlı bir kök AI sayılmaz.
    ///      Aksi hâlde aynı araç iki sistem tarafından birden gönderilir
    ///      (biri drivetrain, diğeri rigidbody okur) ve iki tarafta iki
    ///      farklı fizik kaynağı çalışır -> araç titrer/kayar.
    ///   2. Yalnız HOST keşfeder ve gönderir. Client hiçbir şey yaratmaz,
    ///      yalnız gelen veriyi uygular. Böylece araç kopyaları iki tarafta
    ///      birden oluşmaz.
    ///
    /// Mavi araçlar (Satsuma vb.) bu tabloya GİRMEZ; onları VehicleRegistry
    /// zaten 20 Hz gönderiyor.
    /// </summary>
    public static class AIVehicleSync
    {
        // --- Ayarlar ---
        public const float SEND_INTERVAL = 0.05f;   // 20 Hz, araç transformu ile aynı
        public const float INTERP_DELAY = 0.1f;     // 100 ms interpolasyon gecikmesi
        public const float MAX_EXTRAPOLATION = 0.5f; // en fazla 500 ms ileri sar
        public const float SNAP_DISTANCE = 50f;     // bu mesafeden büyük sıçramada yeniden başlat
        public const float RECEIVE_TIMEOUT = 5f;     // bu kadar süre veri gelmezse araç "ölü"

        class AIVehicleEntry
        {
            public int VehicleId;
            public string VehicleName;
            public GameObject Root;
            public Rigidbody Body;
            public AxisCarController Controller;
            public VehicleInterpolator Interp = new VehicleInterpolator();
            public bool IsHostAuthority;
            public float LastRecvTime;
            /// <summary>Client tarafında kinematik yapıldı mı? (sahne değişiminde sıfırlanır)</summary>
            public bool MadeKinematic;
            public bool DisabledOnTimeout;

            // Karşı tarafın girdileri; yalnız gözlemek/efekt için tutulur.
            public float LastThrottle, LastBrake, LastSteer, LastRpm;
            public int LastGear;
            public bool LastLights, LastHorn;
        }

        static readonly Dictionary<int, AIVehicleEntry> aiVehicles = new Dictionary<int, AIVehicleEntry>();
        static readonly List<AIVehicleEntry> sendList = new List<AIVehicleEntry>();
        static float nextSendTime;
        static bool discovered;

        // --- Host: keşif ve gönderim ---

        /// <summary>AI araçları bir kez keşfeder. Yalnız host anlamlı.</summary>
        public static void Discover()
        {
            if (discovered) return;
            discovered = true;

            // Client keşfetmez: gönderen taraf host'tur. Yine de client'ın
            // uygulaması için tabloyu boş bırakmak yerine çıkıyoruz; gelen
            // paket ilk kaydı yaratacak (aşağıdaki ResolveEntry).
            if (!SteamTransport.IsHost) return;

            AxisCarController[] allControllers;
            try { allControllers = Resources.FindObjectsOfTypeAll<AxisCarController>(); }
            catch (Exception e)
            {
                SmpLog.Warning("[AIVehicleSync] araç taraması başarısız: " + e.Message);
                return;
            }

            int count = 0;
            for (int i = 0; i < allControllers.Length; i++)
            {
                AxisCarController controller = allControllers[i];
                if (controller == null || controller.gameObject == null) continue;

                GameObject root = controller.gameObject;

                // KURAL 1: oyuncu aracı zaten VehicleRegistry'de.
                if (IsAlreadySyncedByVehicleRegistry(root)) continue;

                // Oyuncu controller'ı taşıyan kök oyuncunun aracıdır.
                if (HasPlayerController(root)) continue;

                Rigidbody rb;
                try { rb = root.GetComponent<Rigidbody>(); }
                catch { continue; }
                if (rb == null) continue;

                AIVehicleEntry entry = CreateEntry(root, rb, controller, true);
                if (entry == null) continue;
                if (aiVehicles.ContainsKey(entry.VehicleId)) continue;

                aiVehicles.Add(entry.VehicleId, entry);
                sendList.Add(entry);
                count++;
            }

            if (count > 0) {
                SmpLog.Log("[AIVehicleSync] " + count + " AI aracı host tarafında takip ediliyor.");
            } else {
                SmpLog.Log("[AIVehicleSync] AI araç bulunamadı; tüm araçlar oyuncu araçları "
                    + "(VehicleRegistry hâlihazırda gönderiyor).");
            }
        }

        /// <summary>
        /// Bu kökü VehicleRegistry zaten gönderiyor mu? Öyleyse AI tablosuna
        /// girmemeli; iki tablo aynı aracı gönderirse iki tarafta iki fizik
        /// kaynağı çalışır ve araç titrer.
        /// </summary>
        static bool IsAlreadySyncedByVehicleRegistry(GameObject root)
        {
            Transform t = root.transform;
            foreach (KeyValuePair<int, SyncedVehicle> kv in VehicleRegistry.Vehicles)
            {
                SyncedVehicle sv = kv.Value;
                if (sv == null) continue;
                Transform vt = null;
                try { vt = sv.transform; } catch { continue; }
                if (vt == t) return true;
            }
            return false;
        }

        /// <summary>
        /// Oyuncu controller'ı kökte veya bir üstünde mi? MSC'de
        /// LocalPlayerController aracın kendi GameObject'inde değil üst
        /// bir düğümde olabiliyor; bu yüzden yukarı doğru da bakılır.
        /// </summary>
        static bool HasPlayerController(GameObject root)
        {
            Transform t = root.transform;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                if (t.GetComponent("LocalPlayerController") != null) return true;
                if (t.GetComponent("PlayerController") != null) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Kaydı kurar. İD kökün tam yolundan türetilir, çünkü AYNI ADLI iki
        /// AI aracı olabilir (ör. klonlar); addanın hash'i çakışırdı.
        /// </summary>
        static AIVehicleEntry CreateEntry(GameObject root, Rigidbody rb, AxisCarController controller, bool hostAuthority)
        {
            string path;
            int id;
            try
            {
                path = Wire.PathOf(root.transform);
                id = Wire.StableHash(path);
            }
            catch (Exception e)
            {
                SmpLog.Warning("[AIVehicleSync] araç yolu okunamadı (" + root.name + "): " + e.Message);
                return null;
            }

            return new AIVehicleEntry
            {
                VehicleId = id,
                VehicleName = path,
                Root = root,
                Body = rb,
                Controller = controller,
                IsHostAuthority = hostAuthority
            };
        }

        /// <summary>Host tarafı periyodik gönderim (20 Hz).</summary>
        public static void HostUpdate()
        {
            if (!SteamTransport.IsHost || !SteamTransport.HasRemote) return;
            if (Time.realtimeSinceStartup < nextSendTime) return;
            nextSendTime = Time.realtimeSinceStartup + SEND_INTERVAL;

            for (int i = 0; i < sendList.Count; i++)
            {
                AIVehicleEntry entry = sendList[i];
                if (entry == null || entry.Root == null || entry.Body == null) continue;
                try
                {
                    SendAIVehicleState(entry);
                }
                catch (Exception e)
                {
                    SmpLog.Warning("[AIVehicleSync] host gönderim hatası ("
                        + entry.VehicleName + "): " + e.Message);
                }
            }
        }

        static void SendAIVehicleState(AIVehicleEntry entry)
        {
            MemoryStream s = Protocol.Begin(Protocol.Msg.AIVehicleState);
            BinaryWriter w = new BinaryWriter(s);

            w.Write(entry.VehicleId);

            Transform t = entry.Root.transform;
            Vector3 pos = t.position;
            Quaternion rot = t.rotation;
            Vector3 vel = entry.Body.velocity;
            Vector3 angVel = entry.Body.angularVelocity;

            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
            w.Write(vel.x); w.Write(vel.y); w.Write(vel.z);
            w.Write(angVel.x); w.Write(angVel.y); w.Write(angVel.z);

            // Girdi bilgisi gönderilmez: AI aracın girdisi oyunun kendi
            // FSM'i tarafından hesaplanıyor ve client'ta o girdiyi
            // uygulamak (throttle yazmak) yerel fizikle çakışır. Sadece
            // gözlem/hud için okunur, ağa gönderilmez.

            if (SteamTransport.SendUnreliableNoDelay(Protocol.Finish(s)))
                Telemetry.SentAIVehicleStates++;
        }

        // --- Client: alma ve uygulama ---

        /// <summary>Gelen AI araç durumu paketi.</summary>
        public static void OnMessage(BinaryReader r)
        {
            try
            {
                int vehicleId = r.ReadInt32();
                Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Quaternion rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Vector3 vel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Vector3 angVel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                AIVehicleEntry entry;
                if (!aiVehicles.TryGetValue(vehicleId, out entry))
                {
                    entry = ResolveEntry(vehicleId);
                    if (entry == null) return;
                }

                entry.LastRecvTime = Time.time;
                Telemetry.ReceivedAIVehicleStates++;
                entry.Interp.Push(pos, rot, vel, angVel, Time.time);
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[AIVehicleSync] paket işleme hatası: " + e.Message);
            }
        }

        /// <summary>
        /// Gelen araç kimliğini sahnedeki nesneye bağlar. YENİ NESNE
        /// YARATILMAZ: oyuncu tarafındaki sahne zaten tamdır, biz sadece
        /// uzaktaki konumu uygularız. Yaratmak yanlış kopyalar üretirdi.
        /// </summary>
        static AIVehicleEntry ResolveEntry(int vehicleId)
        {
            // İlk arama bir kez yapılır ve sonucu saklanır. Resources
            // .FindObjectsOfTypeAll her çağrıda TÜM sahneyi tarar; bunu
            // 20 Hz'lik bir paketin içinde yapmak kare başına binlerce
            // nesne taraması demek.
            if (!aiLookupBuilt)
            {
                ScanAiVehicles();
            }

            GameObject found;
            if (!aiLookup.TryGetValue(vehicleId, out found) || found == null)
            {
                // BULUNAMADI — bu, aracın YENİ DOĞMUŞ olması demektir.
                //
                // CANLI KANIT / DÜZELTME: tablo bir kez kurulup bir daha
                // taranmadığı için, oyunda SONRADAN spawn olan trafik
                // aracının kimliği tabloda hiç yoktu ve ResolveEntry her
                // pakette null dönüp ReceivedUnknownVehicle'i artırıyordu;
                // araç ağda tamamen görünmez kalıyordu. Tablo yeniden
                // taranır (rate-limited).
                if (found == null && TryRescanOnMiss()) { }

                if (!aiLookup.TryGetValue(vehicleId, out found) || found == null)
                {
                    Telemetry.ReceivedUnknownVehicle++;
                    return null;
                }
            }

            AxisCarController ctrl = found.GetComponent<AxisCarController>();
            if (ctrl == null)
            {
                Telemetry.ReceivedUnknownVehicle++;
                return null;
            }

            Rigidbody rb = found.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Telemetry.ReceivedUnknownVehicle++;
                return null;
            }

            AIVehicleEntry entry = CreateEntry(found, rb, ctrl, false);
            if (entry == null) return null;
            aiVehicles[vehicleId] = entry;
            return entry;
        }

        static bool aiLookupBuilt;
        static readonly Dictionary<int, GameObject> aiLookup = new Dictionary<int, GameObject>();

        /// <summary>
        /// Bulamadıktan sonra tabloyu yeniden taramaya izin verilen en kısa aralık (s).
        ///
        /// NEDEN GEREKLİ: Resources.FindObjectsOfTypeAll TÜM sahneyi tarar ve
        /// trafik her karede spawn olabilir. 20 Hz'lik bir paket içinde
        /// her bulunamayan kimlikte tam tarama yapmak kare başına yüzlerce
        /// nesne taraması demek (log kanıtı: "300 FPS ama kasılıyor").
        /// 2 saniyelik bekleme, doğan bir aracın en geç 2 sn içinde ağa
        /// girmesini sağlar — oyuncu farkı hissetmez.
        /// </summary>
        public const float RESCAN_COOLDOWN = 2f;

        static float nextRescanAllowed;

        /// <summary>
        /// AI araç kimliği -> sahne kökü tablosunu (yeniden) kurar.
        ///
        /// ÖNEMLİ: yalnız EKLER, silmez. Sahneden ayrılan araçların kökleri
        /// Unity tarafından yok edilmiş olur; Dictionary'de kalan ölü
        /// referanslar biriktirmemesi için ayrıca PruneLookup() çağrılır.
        /// </summary>
        static void ScanAiVehicles()
        {
            aiLookupBuilt = true;
            nextRescanAllowed = Time.time + RESCAN_COOLDOWN;
            try
            {
                AxisCarController[] all = Resources.FindObjectsOfTypeAll<AxisCarController>();
                for (int i = 0; i < all.Length; i++)
                {
                    AxisCarController c = all[i];
                    if (c == null || c.gameObject == null) continue;
                    GameObject root = c.gameObject;
                    if (IsAlreadySyncedByVehicleRegistry(root)) continue;
                    if (HasPlayerController(root)) continue;

                    int id = Wire.StableHash(Wire.PathOf(root.transform));
                    if (!aiLookup.ContainsKey(id)) aiLookup.Add(id, root);
                }
                PruneLookup();
            }
            catch (Exception e)
            {
                SmpLog.Warning("[AIVehicleSync] araç eşleme hatası: " + e.Message);
            }
        }

        /// <summary>Ölü (yok edilmiş) kök referanslarını tablodan siler.</summary>
        static void PruneLookup()
        {
            if (aiLookup.Count == 0) return;
            List<int> dead = null;
            foreach (var kvp in aiLookup)
            {
                // Unity, yok edilmiş nesneyi null'a EŞİT sayar.
                if (kvp.Value == null)
                {
                    if (dead == null) dead = new List<int>(8);
                    dead.Add(kvp.Key);
                }
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) aiLookup.Remove(dead[i]);
        }

        /// <summary>
        /// Bulunamayan kimlik için yeniden tarama denenir (rate-limited).
        /// Gerçekten yeni bir araç eklendiyse döner.
        /// </summary>
        static bool TryRescanOnMiss()
        {
            float now = Time.time;
            if (now < nextRescanAllowed) return false;
            int before = aiLookup.Count;
            ScanAiVehicles();
            Rescans++;
            if (aiLookup.Count > before)
            {
                SmpLog.Log("[AIVehicleSync] yeni AI araç doğdu: tablo "
                    + before + " -> " + aiLookup.Count + " (yeniden tarama).");
                return true;
            }
            return false;
        }

        /// <summary>Kaç kez yeniden tarama yapıldı (tani satırı).</summary>
        public static int Rescans;

        // --- Client: interpolasyon ---

        /// <summary>Client tarafında her kare interpolasyonu uygular.</summary>
        public static void ClientInterpolate()
        {
            if (SteamTransport.IsHost) return;
            if (aiVehicles.Count == 0) return;

            float now = Time.time;

            // sendList yalnız host'ta doludur; client'ta kayıtlar gelen
            // paketlerle oluşur, bu yüzden sözlük üzerinden yürünür.
            foreach (KeyValuePair<int, AIVehicleEntry> kv in aiVehicles)
            {
                AIVehicleEntry entry = kv.Value;
                if (entry == null || entry.IsHostAuthority) continue;
                if (entry.Root == null || entry.Body == null) continue;

                // Veri akmıyorsa aracı bir daha uygulama; eski interpolasyon
                // değeriyle sonsuza kadar kaymasın.
                if (now - entry.LastRecvTime > RECEIVE_TIMEOUT)
                {
                    if (!entry.DisabledOnTimeout)
                    {
                        entry.DisabledOnTimeout = true;
                        SmpLog.Log("[AIVehicleSync] veri akışı kesildi, araç donduruldu: "
                            + entry.VehicleName);
                    }
                    continue;
                }
                entry.DisabledOnTimeout = false;

                Vector3 p, v, av;
                Quaternion q;
                if (!entry.Interp.Sample(now, out p, out q, out v, out av)) continue;

                // Kinematik yapma: oyunun kendi FSM'i araç için dinamik
                // gövde kullanıyor, kinematik yapmak yerel fiziği kapatır
                // ve FSM hâlâ konum yazınca araç zıplar. Bunun yerine
                // MovePosition/MoveRotation kullanılır, gövde dinamik kalır.
                try
                {
                    entry.Body.MovePosition(p);
                    entry.Body.MoveRotation(q);
                }
                catch (Exception e)
                {
                    SmpLog.Warning("[AIVehicleSync] hareket uygulamadı: " + e.Message);
                }
            }
        }

        // --- Temizlik ---

        public static void Clear()
        {
            // Host tarafında hiçbir şey yok edilmez: kayıtlar sahneye ait.
            // Yalnız kinematik yapılmış gövdeler (yok edilmiş sahne
            // nesneleri) ve gönderim listesi temizlenir.
            sendList.Clear();
            aiVehicles.Clear();
            aiLookup.Clear();
            aiLookupBuilt = false;
            discovered = false;
            nextSendTime = 0f;
        }

        // --- Telemetri ---

        public static int VehicleCount { get { return aiVehicles.Count; } }

        /// <summary>Client tarafında uzaktan sürülen araç sayısı (HUD için).</summary>
        public static int RemoteCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<int, AIVehicleEntry> kv in aiVehicles)
                    if (kv.Value != null && !kv.Value.IsHostAuthority) n++;
                return n;
            }
        }

        public static string Describe()
        {
            return "AIVehicleSync: tracked=" + aiVehicles.Count
                + " host-sending=" + sendList.Count;
        }
    }
}