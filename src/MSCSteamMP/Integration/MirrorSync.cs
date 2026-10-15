using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// MWCoop senkronizasyon/aynalama YÖNTEMİNİN MSC uyarlaması.
    ///
    /// Kaynak: MWCoop (My Winter Car) `Sync/` + `Net/Session.cs` + `Net/Codec.cs`.
    /// Kopyalanan şey oyuna özel içerik DEĞİL, yalnızca yöntem:
    ///
    ///   1. Sürekli fizik = GÜVENİLMEZ snapshot, son-kazanır (last-wins).
    ///      Hız kademeleri MWCoop ile aynı: sürülen 20 Hz, elde-tutulan/motorlu
    ///      10 Hz, host parked recale 0.5 Hz, sim 1 Hz, periyodik full 5/10/20 sn.
    ///   2. Tekil aksiyon = GÜVENİLİR olay-tekrarı (event replay): yapan gönderir,
    ///      alan kendi FSM'inde aynı olayı oynatır. EchoGuard + Claim ile
    ///      yankı ve çift-kanca engellenir (MWCoop `Replay.Depth/Claim`).
    ///   3. Yıldız topoloji + host-relay: guest host'a gönderir, host diğerine
    ///      iletir (`Session.SendAll/Broadcast`). 2 oyuncuda relay = yansıma.
    ///   4. Yetki (authority): sürücü &gt; motor-bırakan &gt; çeken &gt; host;
    ///      tutan/taşıyan &gt; host. Bırakınca son-poz + sim GÜVENİLİR gider.
    ///   5. Kopya kinematik + interpolasyon: uzaktaki gövde kinematic yapılır,
    ///      Drivetrain/kontrol kesilir, `VehicleInterpolator` ile yumuşatılır.
    ///   6. Tablo: host numaralandırır, guest anahtarla bağlar (homonym #rank).
    ///      JIP + periyodik full snapshot kaybı kapatır.
    ///
    /// KAPSAM GÜVENLİĞİ: bu katman varsayılan olarak HİÇBİR nesneyi kendiliğinden
    /// göndermez. `Track()` ile kaydedilen anahtarlar gönderilir; VehicleRegistry /
    /// SatsumaRegistry / GarageEntitySync kapsadıkları kökleri zaten gönderdiği
    /// için `Discover()` onları atlar (çift-gönderim titremesi olmasın).
    /// Yeni sistemler (NPC/trafik/prop tarzı) bu API'yi kullanır.
    /// </summary>
    public static class MirrorSync
    {
        // ------------------------------------------------------------------
        // Hız kademeleri (MWCoop VehicleSync/PlayerSync/Traffic/Npcs tick'leri)
        // ------------------------------------------------------------------

        /// <summary>Sürülen / hızlı hareket: 20 Hz (MWCoop PlayerSync/VehicleSync fast).</summary>
        public const float FAST_INTERVAL = 0.05f;
        /// <summary>Elde-tutulan / motor-bırakan / römork: 10 Hz (MWCoop owned tick).</summary>
        public const float OWNED_INTERVAL = 0.10f;
        /// <summary>Sim (akü/ısı/yakıt): 1 Hz (MWCoop SendSim).</summary>
        public const float SIM_INTERVAL = 1.0f;
        /// <summary>Host parked recale: 0.5 Hz / 2 sn (MWCoop nextSlow).</summary>
        public const float HOST_RECALE_INTERVAL = 2.0f;
        /// <summary>Host parked sim: 10 sn (MWCoop nextParkedSim).</summary>
        public const float HOST_SIM_INTERVAL = 10.0f;
        /// <summary>Tablo taraması: 20 sn (MWCoop nextScan).</summary>
        public const float TABLE_RESCAN_INTERVAL = 20.0f;
        /// <summary>Periyodik full snapshot: 5 sn (MWCoop Events/Parked/Knobs full).</summary>
        public const float FULL_INTERVAL = 5.0f;

        /// <summary>Uzak pose zaman aşımı: 1.5 sn (MWCoop Remote() eşiği).</summary>
        public const float REMOTE_TIMEOUT = 1.5f;
        /// <summary>Bırakma koruma: 5 sn host anlık-görüntüsü ezmesin (MWCoop AuthorityUntil).</summary>
        public const float RELEASE_GUARD = 5.0f;

        /// <summary>Snapshot kipleri (MWCoop VehicleSync mode: 0 parked, 1 driven, 2 kept).</summary>
        public const byte MODE_PARKED = 0;
        /// <summary>Sürülen: 20 Hz unreliable-no-delay.</summary>
        public const byte MODE_DRIVEN = 1;
        /// <summary>Motor-bırakan / römork / tutulan: 10 Hz unreliable.</summary>
        public const byte MODE_KEPT = 2;

        // ------------------------------------------------------------------
        // Replay sahipliği (MWCoop Replay.Claim/Owner/ClaimedByOther birebir)
        // ------------------------------------------------------------------

        static readonly Dictionary<PlayMakerFSM, string> hookOwners =
            new Dictionary<PlayMakerFSM, string>();

        /// <summary>
        /// Bir FSM'i bir modüle bağlar. MWCoop `Replay.Claim` ile aynı:
        /// boşsa alır, doluysa yalnızca sahibi true alır. Çift-kanca = çift
        /// gönderim + yankı-tekrarı demek, bu yüzden ikinci modül susar.
        /// </summary>
        public static bool Claim(PlayMakerFSM fsm, string module)
        {
            if (fsm == null || string.IsNullOrEmpty(module)) return false;
            string owner;
            if (hookOwners.TryGetValue(fsm, out owner)) return owner == module;
            hookOwners[fsm] = module;
            return true;
        }

        /// <summary>FSM'in sahibi modül (yoksa null).</summary>
        public static string Owner(PlayMakerFSM fsm)
        {
            if (fsm == null) return null;
            string owner;
            return hookOwners.TryGetValue(fsm, out owner) ? owner : null;
        }

        /// <summary>Bu FSM başka bir modülün mü?</summary>
        public static bool ClaimedByOther(PlayMakerFSM fsm, string module)
        {
            if (fsm == null) return false;
            string owner;
            return hookOwners.TryGetValue(fsm, out owner) && owner != module;
        }

        // ------------------------------------------------------------------
        // Yetki (MWCoop VehicleSync.Authority/SimAuthority + Props holder>host)
        // ------------------------------------------------------------------

        /// <summary>Yetki türü: sayı büyüdükçe öncelik artar (host en düşük).</summary>
        public enum AuthorityKind : byte
        {
            Host = 0,
            Kept = 1,
            Tower = 2,
            Holder = 3,
            Driver = 4
        }

        class AuthorityRec
        {
            public ulong OwnerSteamId;
            public AuthorityKind Kind;
            public float Since;
        }

        static readonly Dictionary<string, AuthorityRec> authorityOf =
            new Dictionary<string, AuthorityRec>(StringComparer.Ordinal);
        // F05_MIRROR_BANDWIDTH
        //
        // "Bu anahtar bir zamanlar yerel yetkiye alindi mi?"
        //
        // `Tick` icindeki host recale blogu `!IsMine && !IsRemote`
        // kosuluyla calisiyor. `IsMine` `authorityOf`u arar; anahtar
        // yoksa false doner. `Track`/`ClaimAuthority` HICBIR YERDEN
        // cagrilmadigi icin `authorityOf` HICBIR ZAMAN dolmuyor ->
        // recale, sahiplenilmemis TUM girdileri kapsiyor ve
        // `Discover`in tabloya aldigi tum sahne kok rigidbody'leri
        // icin PARKED snapshot gonderiyor.
        //
        // CANLI KANIT (mscmp.txt NETSTAT):
        //   MirrorState 54096 gonder / 3989089 gB / 0 aB
        //   toplam gonderimin %56'si, hicbiri teslim edilmiyor.
        //
        // Recale'nin anlami "sahibi BIRAKTI, son halini tekrar yolla"
        // demektir; hic sahiplenilmemis bir anahtarin birakilacak
        // bir sahibi yoktur.
        static readonly HashSet<string> everOwned =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Yerel SteamID (oturum yoksa 0).</summary>
        static ulong LocalId()
        {
            try { return SteamTransport.LocalId.m_SteamID; }
            catch { return 0; }
        }

        /// <summary>
        /// Anahtarın yetkisini yerelde üstlenir ve karşı tarafa duyurur.
        /// MWCoop'ta sürücü/utan implicitly üstlenir; burada açık çağrı var
        /// çünkü MSC tarafında tutma/sürme olayları farklı modüllerde.
        /// </summary>
        public static void ClaimAuthority(string key, AuthorityKind kind)
        {
            if (string.IsNullOrEmpty(key)) return;
            AuthorityRec rec = new AuthorityRec();
            rec.OwnerSteamId = LocalId();
            rec.Kind = kind;
            rec.Since = Time.realtimeSinceStartup;
            authorityOf[key] = rec;
            // F05_MIRROR_BANDWIDTH: ilk sahiplenmede bir kez isaretle.
            everOwned.Add(key);
        }

        /// <summary>Yetkiyi bırakır: son-poz + sim GÜVENİLİR gider (MWCoop Release).</summary>
        public static void ReleaseAuthority(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            authorityOf.Remove(key);
            Entry e;
            if (entries.TryGetValue(key, out e) && e.Body != null)
            {
                SendSnapshot(e.Key, MODE_PARKED, true);
                SendSimEntry(e, true);
                e.AuthorityUntil = Time.realtimeSinceStartup + RELEASE_GUARD;
            }
        }

        /// <summary>Bu anahtarı yerel taraf mı sürüyor/tutuyor?</summary>
        public static bool IsMine(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            AuthorityRec rec;
            if (!authorityOf.TryGetValue(key, out rec)) return false;
            return rec.OwnerSteamId == LocalId() && rec.OwnerSteamId != 0;
        }

        /// <summary>
        /// Uzak taraf bu anahtarda yetki iddia ediyor mu? (taze kayıt varsa evet)
        /// </summary>
        public static bool IsRemote(string key)
        {
            Entry e;
            if (!entries.TryGetValue(key, out e)) return false;
            return Time.realtimeSinceStartup - e.LastRemote < REMOTE_TIMEOUT
                && e.RemoteBy != 0 && e.RemoteBy != LocalId();
        }

        // ------------------------------------------------------------------
        // Tablo (MWCoop VehicleSync netKeys/byNet + SendTable/SendKeys)
        // ------------------------------------------------------------------

        class Entry
        {
            public string Key;
            public GameObject Root;
            public Rigidbody Body;
            public Behaviour Drive;      // AxisCarController / Drivetrain benzeri, kopyada kesilir
            public bool DriveWas;
            public VehicleInterpolator Interp = new VehicleInterpolator();
            public Vector3 LastPos = Vector3.zero;
            public Quaternion LastRot = Quaternion.identity;
            public Vector3 LastVel = Vector3.zero;
            public Vector3 LastAngVel = Vector3.zero;
            public ulong RemoteBy;
            public float LastRemote = -100f;
            public float AuthorityUntil;
            public float NextFull = -1f;
            public bool MadeKinematic;
            public bool KinematicWas;
            public RigidbodyInterpolation InterpWas;
            public Dictionary<string, float> Sim = new Dictionary<string, float>(StringComparer.Ordinal);
        }

        static readonly Dictionary<string, Entry> entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        static readonly List<string> netKeys = new List<string>();
        static bool tableSeen;
        static float nextFast;
        static float nextSim;
        static float nextSlow;
        static float nextHostSim;
        static float nextRescan;
        static float nextKeys;
        static int ownedTick;
        static bool discovered;

        /// <summary>Kayıtlı anahtar sayısı (teşhis).</summary>
        public static int Count { get { return entries.Count; } }

        static string KeyOf(GameObject go, int rank)
        {
            if (go == null) return null;
            return rank <= 0 ? go.name : go.name + "#" + rank;
        }

        /// <summary>
        /// Dünya taraması: tabloyu kurar, mevcut sistemlerin kapsadığı kökleri
        /// ATlar (çift-gönderim yok). Gönderim yalnızca `Track()` ile başlar.
        /// </summary>
        public static void Discover()
        {
            if (discovered) return;
            discovered = true;
            try
            {
                UnityEngine.Object[] found;
                try { found = Resources.FindObjectsOfTypeAll(typeof(Rigidbody)); }
                catch { found = new UnityEngine.Object[0]; }
                if (found == null) found = new UnityEngine.Object[0];

                Dictionary<string, int> ranks = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < found.Length; i++)
                {
                    Rigidbody rb = found[i] as Rigidbody;
                    if (rb == null || rb.gameObject == null) continue;
                    // Yalnız sahne kökleri: MWCoop IsCarBody karşılığı (genel: parent'sız rigidbody).
                    if (rb.transform.parent != null) continue;
                    // Mevcut sistemin gönderdiği araçla çakışma: VehicleRegistry biliyorsa atla.
                    if (IsCoveredByVehicleRegistry(rb.gameObject)) continue;
                    int rank = 0;
                    if (!ranks.TryGetValue(rb.name, out rank)) rank = 0;
                    ranks[rb.name] = rank + 1;
                    string key = KeyOf(rb.gameObject, rank);
                    if (entries.ContainsKey(key)) continue;
                    Entry e = new Entry();
                    e.Key = key;
                    e.Root = rb.gameObject;
                    e.Body = rb;
                    e.Drive = FindDrive(rb.gameObject);
                    entries[key] = e;
                    if (SteamTransport.IsHost && !netKeys.Contains(key)) netKeys.Add(key);
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("[Mirror] discover failed: " + e.Message);
            }
            float now = Time.realtimeSinceStartup;
            nextFast = now + FAST_INTERVAL;
            nextSim = now + SIM_INTERVAL;
            nextSlow = now + HOST_RECALE_INTERVAL;
            nextHostSim = now + HOST_SIM_INTERVAL;
            nextRescan = now + TABLE_RESCAN_INTERVAL;
            nextKeys = now + FULL_INTERVAL;
        }

        static bool IsCoveredByVehicleRegistry(GameObject go)
        {
            try
            {
                // VehicleRegistry zaten AxisCarController köklerini gönderiyor.
                AxisCarController acc = go.GetComponent<AxisCarController>();
                if (acc != null) return true;
            }
            catch { }
            return false;
        }

        static Behaviour FindDrive(GameObject go)
        {
            try
            {
                Behaviour b = go.GetComponent<AxisCarController>() as Behaviour;
                if (b != null) return b;
                Component dt = go.GetComponent("Drivetrain") as Behaviour;
                return dt as Behaviour;
            }
            catch { return null; }
        }

        /// <summary>
        /// Açık takip: bu anahtar artık bu katmandan snapshot alır/gönderir.
        /// MWCoop'ta sürüş/taşma implicit başlatır; burada modüller çağırır.
        /// </summary>
        public static void Track(string key, GameObject root)
        {
            if (string.IsNullOrEmpty(key) || root == null) return;
            Entry e;
            if (!entries.TryGetValue(key, out e))
            {
                e = new Entry();
                e.Key = key;
                entries[key] = e;
                if (SteamTransport.IsHost && !netKeys.Contains(key)) netKeys.Add(key);
            }
            e.Root = root;
            try { e.Body = root.GetComponent<Rigidbody>(); }
            catch { e.Body = null; }
            if (e.Drive == null) e.Drive = FindDrive(root);
        }

        /// <summary>Takibi bırakır (sahne nesnesi tutulmaz).</summary>
        public static void Untrack(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            entries.Remove(key);
            authorityOf.Remove(key);
            // F05_MIRROR_BANDWIDTH: anahtar tabladan cikiyor; birakilmis
            // sayimi da birakilir, yoksa yeniden ayni isimle eklenen
            // bir nesne "eski" birakma sayfasini alir.
            everOwned.Remove(key);
        }

        /// <summary>Sahne değişimi: referans bırak, sayaç sıfırla.</summary>
        public static void Clear()
        {
            entries.Clear();
            authorityOf.Clear();
            // F05_MIRROR_BANDWIDTH: sahne degisti; "bir zamanlar
            // sahiplendi" bilgisi de eski sahneye ait.
            everOwned.Clear();
            hookOwners.Clear();
            netKeys.Clear();
            tableSeen = false;
            discovered = false;
        }

        // ------------------------------------------------------------------
        // Gönderim (MWCoop Send/SendSim: unreliable snapshot + reliable final)
        // ------------------------------------------------------------------

        static bool ShouldSend(float now, ref float next, float interval)
        {
            if (now < next) return false;
            next = now + interval;
            return true;
        }

        /// <summary>
        /// Snapshot gönderir. Sürekli veri GÜVENİLMEZ (son-kazanır); bırakma/
        /// final anı GÜVENİLİR (`reliable=true`, MWCoop Release/SimFinal).
        /// Host değilse host'a gider, host relay eder.
        /// </summary>
        public static void SendSnapshot(string key, byte mode, bool reliable)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!SteamTransport.HasRemote) return;
            if (EchoGuard.Suppressed) return;
            Entry e;
            if (!entries.TryGetValue(key, out e) || e.Body == null) return;
            try
            {
                Vector3 p = e.Body.position;
                Quaternion q = e.Body.rotation;
                Vector3 v = Vector3.zero;
                Vector3 av = Vector3.zero;
                try { v = e.Body.velocity; av = e.Body.angularVelocity; }
                catch { }
                MemoryStream s = Protocol.Begin(Protocol.Msg.MirrorState);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(mode);
                w.Write(key);
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                w.Write(v.x); w.Write(v.y); w.Write(v.z);
                w.Write(av.x); w.Write(av.y); w.Write(av.z);
                w.Flush();
                byte[] data = Protocol.Finish(s);
                bool ok;
                if (reliable) ok = SteamTransport.SendReliable(data);
                else if (mode == MODE_DRIVEN) ok = SteamTransport.SendUnreliableNoDelay(data);
                else ok = SteamTransport.SendUnreliable(data);
                if (!ok) Telemetry.BadPackets++;
            }
            catch (Exception ex)
            {
                SmpLog.Warning("[Mirror] snapshot send failed (" + key + "): " + ex.Message);
            }
        }

        /// <summary>
        /// Sim gönderir: 1 Hz + bırakma-anı reliable (MWCoop SendSim/SimFinal).
        /// Sözlük boşsa gönderilmez.
        /// </summary>
        public static void SendSim(string key, bool reliable)
        {
            Entry e;
            if (string.IsNullOrEmpty(key) || !entries.TryGetValue(key, out e)) return;
            SendSimEntry(e, reliable);
        }

        static void SendSimEntry(Entry e, bool reliable)
        {
            if (e == null || string.IsNullOrEmpty(e.Key)) return;
            if (!SteamTransport.HasRemote) return;
            if (EchoGuard.Suppressed) return;
            if (e.Sim.Count == 0 && !reliable) return;
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.MirrorSim);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(e.Key);
                w.Write((byte)(reliable ? 1 : 0));
                w.Write((ushort)e.Sim.Count);
                foreach (KeyValuePair<string, float> kv in e.Sim)
                {
                    w.Write(kv.Key ?? string.Empty);
                    w.Write(kv.Value);
                }
                w.Flush();
                SteamTransport.SendReliable(Protocol.Finish(s));
            }
            catch (Exception ex)
            {
                SmpLog.Warning("[Mirror] sim send failed (" + e.Key + "): " + ex.Message);
            }
        }

        /// <summary>
        /// Olay-tekrarı gönderir: GÜVENİLİR, değişimde tek paket (MWCoop event replay).
        /// Yankı (EchoGuard) veya başkasının kancası (Claim) varsa gönderilmez.
        /// </summary>
        public static void SendEvent(string key, string fsm, string evt, PlayMakerFSM hook)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(evt)) return;
            if (!SteamTransport.HasRemote) return;
            if (EchoGuard.Suppressed) return;
            if (hook != null && ClaimedByOther(hook, "Mirror")) return;
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.MirrorEvent);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(key);
                w.Write(fsm ?? string.Empty);
                w.Write(evt);
                w.Flush();
                SteamTransport.SendReliable(Protocol.Finish(s));
            }
            catch (Exception ex)
            {
                SmpLog.Warning("[Mirror] event send failed (" + key + "." + evt + "): " + ex.Message);
            }
        }

        /// <summary>Sim değişkenini yerelde yazar (boşsa gönderim atlanır).</summary>
        public static void SetSim(string key, string name, float value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name)) return;
            Entry e;
            if (!entries.TryGetValue(key, out e)) return;
            e.Sim[name] = value;
        }

        // ------------------------------------------------------------------
        // Tick (MWCoop Update kademeleri)
        // ------------------------------------------------------------------

        /// <summary>Her kare ModIntegration.Update içinden çağrılır.</summary>
        public static void Tick()
        {
            if (!discovered) return;
            float now = Time.realtimeSinceStartup;

            if (now >= nextRescan)
            {
                nextRescan = now + TABLE_RESCAN_INTERVAL;
                // Yeni kökler (sonradan aktifleşen araç/NPC): tabloya ekle.
                discovered = false;
                List<string> keepMine = new List<string>(authorityOf.Keys);
                Discover();
                for (int i = 0; i < keepMine.Count; i++)
                {
                    if (!authorityOf.ContainsKey(keepMine[i]))
                    {
                        AuthorityRec rec = new AuthorityRec();
                        rec.OwnerSteamId = LocalId();
                        rec.Kind = AuthorityKind.Kept;
                        rec.Since = now;
                        authorityOf[keepMine[i]] = rec;
                    }
                }
            }

            // Hızlı kademe: sürülen 20 Hz, elde-tutulan 10 Hz.
            if (ShouldSend(now, ref nextFast, FAST_INTERVAL))
            {
                foreach (KeyValuePair<string, Entry> kv in entries)
                {
                    Entry e = kv.Value;
                    if (e.Body == null) continue;
                    if (IsMine(e.Key))
                    {
                        AuthorityRec rec;
                        bool kept = authorityOf.TryGetValue(e.Key, out rec)
                            && rec.Kind != AuthorityKind.Driver;
                        if (!kept && rec != null && rec.Kind == AuthorityKind.Driver)
                            SendSnapshot(e.Key, MODE_DRIVEN, false);
                        else if (kept && (ownedTick & 1) == 0)
                            SendSnapshot(e.Key, MODE_KEPT, false);
                    }
                }
                ownedTick++;
            }

            // Sim: yetkimizdekiler 1 Hz.
            if (ShouldSend(now, ref nextSim, SIM_INTERVAL))
            {
                foreach (KeyValuePair<string, Entry> kv in entries)
                {
                    if (IsMine(kv.Key) && kv.Value.Sim.Count > 0) SendSimEntry(kv.Value, false);
                }
            }

            // Host recale: parked 2 sn, parked-sim 10 sn (MWCoop nextSlow/ParkedSim).
            if (SteamTransport.IsHost && ShouldSend(now, ref nextSlow, HOST_RECALE_INTERVAL))
            {
                foreach (KeyValuePair<string, Entry> kv in entries)
                {
                    Entry e = kv.Value;
                    if (e.Body == null) continue;
                    if (IsMine(e.Key) || IsRemote(e.Key)) continue;
                    if (now < e.AuthorityUntil) continue;
                    // F05_MIRROR_BANDWIDTH: recale yalniz GERCEKTEN BIRAKILMIS
                    // anahtarlara gider. `everOwned` false ise bu anahtar
                    // hic sahiplenilmedi -> birakilacak bir sahibi yok ->
                    // gonderilecek bir "son hal" de yok.
                    //
                    // ONCEKI HALI: bu kosul YOKTU. `IsMine` `authorityOf`u
                    // arar ve anahtar yoksa false doner; `Track`/
                    // `ClaimAuthority` hic cagrilmadigi icin `authorityOf`
                    // hep bos kalir -> kosul `!IsMine && !IsRemote` oluyor,
                    // yani sahiplenilmemis TUM girdiler kapsaniyor.
                    if (!everOwned.Contains(e.Key)) continue;
                    SendSnapshot(e.Key, MODE_PARKED, false);
                }
            }
            if (SteamTransport.IsHost && ShouldSend(now, ref nextHostSim, HOST_SIM_INTERVAL))
            {
                foreach (KeyValuePair<string, Entry> kv in entries)
                {
                    Entry e = kv.Value;
                    if (e.Body == null || e.Sim.Count == 0) continue;
                    if (IsMine(e.Key) || IsRemote(e.Key)) continue;
                    // F05_MIRROR_BANDWIDTH: parked-sim de recale ile ayni
                    // seyi gonderiyor; hic sahiplenilmemis anahtarin
                    // "bırakılmış sim" tarafi da yoktur.
                    if (!everOwned.Contains(e.Key)) continue;
                    SendSimEntry(e, false);
                }
            }

            // Periyodik full: JIP/kayıp kapatma (MWCoop 5 sn full'leri).
            foreach (KeyValuePair<string, Entry> kv in entries)
            {
                Entry e = kv.Value;
                if (e.Body == null) continue;
                if (e.NextFull < 0f) e.NextFull = now + FULL_INTERVAL;
                if (IsMine(e.Key) && now >= e.NextFull)
                {
                    e.NextFull = now + FULL_INTERVAL;
                    SendSnapshot(e.Key, MODE_PARKED, false);
                }
            }

            // Kopyaları uygula: kinematik + interpolasyon.
            foreach (KeyValuePair<string, Entry> kv in entries)
            {
                Entry e = kv.Value;
                if (e.Body == null) continue;
                bool remote = IsRemote(e.Key);
                if (!remote && e.RemoteBy != 0 && now - e.LastRemote >= REMOTE_TIMEOUT)
                {
                    e.RemoteBy = 0;
                    RestoreBody(e);
                    continue;
                }
                SetKinematic(e, remote);
                if (remote) Follow(e, now);
            }
        }

        // ------------------------------------------------------------------
        // Alma (host relay + uygula)
        // ------------------------------------------------------------------

        /// <summary>Gelen Mirror paketleri. Tek okuyucu: ModIntegration yönlendirir.</summary>
        public static void OnMessage(Protocol.Msg id, BinaryReader r)
        {
            if (id == Protocol.Msg.MirrorState) OnState(r);
            else if (id == Protocol.Msg.MirrorEvent) OnEvent(r);
            else if (id == Protocol.Msg.MirrorSim) OnSim(r);
        }

        static Entry Resolve(string key)
        {
            Entry e;
            if (entries.TryGetValue(key, out e)) return e;
            e = new Entry();
            e.Key = key;
            entries[key] = e;
            return e;
        }

        static void OnState(BinaryReader r)
        {
            byte mode = r.ReadByte();
            string key = r.ReadString();
            Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Vector3 v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Vector3 av = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            // Host-relay (MWCoop Session.Broadcast): guest'ten geleni diğerine yansıt.
            if (SteamTransport.IsHost) RelayMirrorState(mode, key, p, q, v, av);

            // Yetkimizdeyse ezme: sürücü/tutan kazanır (MWCoop DrivenHere/Authority).
            if (IsMine(key)) return;

            Entry e = Resolve(key);
            if (e.Body == null) TryBind(key, e);
            e.LastPos = p;
            e.LastRot = q;
            e.LastVel = v;
            e.LastAngVel = av;
            e.RemoteBy = SteamTransport.RemoteId.m_SteamID;
            e.LastRemote = Time.realtimeSinceStartup;
            e.Interp.Push(p, q, v, av, Time.time);
        }

        static void RelayMirrorState(byte mode, string key, Vector3 p, Quaternion q, Vector3 v, Vector3 av)
        {
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.MirrorState);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(mode);
                w.Write(key ?? string.Empty);
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                w.Write(v.x); w.Write(v.y); w.Write(v.z);
                w.Write(av.x); w.Write(av.y); w.Write(av.z);
                w.Flush();
                SteamTransport.SendUnreliable(Protocol.Finish(s));
            }
            catch { }
        }

        static void OnEvent(BinaryReader r)
        {
            string key = r.ReadString();
            string fsm = r.ReadString();
            string evt = r.ReadString();

            if (SteamTransport.IsHost) RelayMirrorEvent(key, fsm, evt);

            Entry e = Resolve(key);
            if (e.Body == null) TryBind(key, e);
            if (e.Root == null) return;
            using (EchoGuard.Enter())
            {
                try
                {
                    PlayMakerFSM target = null;
                    try
                    {
                        PlayMakerFSM[] fsms = e.Root.GetComponentsInChildren<PlayMakerFSM>(true);
                        for (int i = 0; i < fsms.Length; i++)
                        {
                            if (fsms[i] == null) continue;
                            if (string.IsNullOrEmpty(fsm) || fsms[i].FsmName == fsm) { target = fsms[i]; break; }
                        }
                    }
                    catch { }
                    if (target != null) target.SendEvent(evt);
                    else SmpLog.Warning("[Mirror] event target missing: " + key + "." + fsm + "." + evt);
                }
                catch (Exception ex)
                {
                    SmpLog.Warning("[Mirror] event apply failed (" + key + "): " + ex.Message);
                }
            }
        }

        static void RelayMirrorEvent(string key, string fsm, string evt)
        {
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.MirrorEvent);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(key ?? string.Empty);
                w.Write(fsm ?? string.Empty);
                w.Write(evt ?? string.Empty);
                w.Flush();
                SteamTransport.SendReliable(Protocol.Finish(s));
            }
            catch { }
        }

        static void OnSim(BinaryReader r)
        {
            string key = r.ReadString();
            byte final = r.ReadByte();
            ushort n = r.ReadUInt16();
            Entry e = Resolve(key);
            using (EchoGuard.Enter())
            {
                for (int i = 0; i < n; i++)
                {
                    string name = r.ReadString();
                    float value = r.ReadSingle();
                    e.Sim[name] = value;
                }
            }
            if (final != 0)
            {
                e.AuthorityUntil = Time.realtimeSinceStartup + RELEASE_GUARD;
            }
        }

        static void TryBind(string key, Entry e)
        {
            try
            {
                GameObject go = GameObject.Find(key);
                if (go == null && key.Contains("#"))
                    go = GameObject.Find(key.Substring(0, key.IndexOf('#')));
                if (go == null) return;
                e.Root = go;
                e.Body = go.GetComponent<Rigidbody>();
                if (e.Drive == null) e.Drive = FindDrive(go);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Kopya gövde (MWCoop SetKinematic/Follow: kinematic + interp)
        // ------------------------------------------------------------------

        static void SetKinematic(Entry e, bool on)
        {
            if (e.Body == null) return;
            try
            {
                if (e.Body.isKinematic == on && e.MadeKinematic == on) return;
                if (on)
                {
                    e.KinematicWas = e.Body.isKinematic;
                    e.Body.isKinematic = true;
                    e.MadeKinematic = true;
                    try
                    {
                        e.InterpWas = e.Body.interpolation;
                        e.Body.interpolation = RigidbodyInterpolation.Interpolate;
                    }
                    catch { }
                    if (e.Drive != null)
                    {
                        try { e.DriveWas = e.Drive.enabled; e.Drive.enabled = false; }
                        catch { }
                    }
                }
                else if (e.MadeKinematic)
                {
                    e.MadeKinematic = false;
                    try { e.Body.isKinematic = e.KinematicWas; }
                    catch { }
                    try { e.Body.interpolation = e.InterpWas; }
                    catch { }
                    if (e.Drive != null)
                    {
                        try { e.Drive.enabled = e.DriveWas; }
                        catch { }
                    }
                    e.Interp.Clear();
                }
            }
            catch { }
        }

        static void RestoreBody(Entry e)
        {
            SetKinematic(e, false);
        }

        static void Follow(Entry e, float now)
        {
            Vector3 p;
            Quaternion q;
            Vector3 v;
            Vector3 av;
            if (!e.Interp.Sample(Time.time, out p, out q, out v, out av))
            {
                p = e.LastPos;
                q = e.LastRot;
            }
            try
            {
                if (e.Body != null)
                {
                    e.Body.MovePosition(p);
                    e.Body.MoveRotation(q);
                }
                else if (e.Root != null)
                {
                    e.Root.transform.position = p;
                    e.Root.transform.rotation = q;
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // JIP / peer (MWCoop ScheduleSnapshot + SendTable/SendKeys)
        // ------------------------------------------------------------------

        /// <summary>Peer bağlanınca host full iter, guest ister (MWCoop OnHello/SaveTransfer.Queue).</summary>
        public static void OnPeerConnected()
        {
            if (SteamTransport.IsHost) SendAllFull();
        }

        /// <summary>
        /// Peer ayrılınca uzak kopyalar fiziğe döner (kinematik kilit kalmasın).
        /// Tablo/yetki korunur: aynı oturumda geri gelirse kaldığı yerden sürer.
        /// </summary>
        public static void OnPeerDisconnected()
        {
            foreach (KeyValuePair<string, Entry> kv in entries)
            {
                Entry e = kv.Value;
                e.RemoteBy = 0;
                e.LastRemote = -100f;
                RestoreBody(e);
            }
        }

        /// <summary>Tüm yetkimizdekilerin son hali (JIP + sahne girişi).</summary>
        public static void SendAllFull()
        {
            if (!SteamTransport.HasRemote) return;
            foreach (KeyValuePair<string, Entry> kv in entries)
            {
                if (IsMine(kv.Key))
                {
                    SendSnapshot(kv.Key, MODE_PARKED, true);
                    if (kv.Value.Sim.Count > 0) SendSimEntry(kv.Value, true);
                }
            }
        }

        /// <summary>Teşhis satırı (`smp status` / log).</summary>
        public static string Describe()
        {
            int mine = 0;
            int remote = 0;
            foreach (KeyValuePair<string, Entry> kv in entries)
            {
                if (IsMine(kv.Key)) mine++;
                else if (IsRemote(kv.Key)) remote++;
            }
            return "mirror: " + entries.Count + " key, " + mine + " yerel, " + remote + " uzak";
        }
    }
}
