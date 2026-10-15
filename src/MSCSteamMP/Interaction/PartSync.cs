using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// PARÃ‡A TAKMA/SÃ–KME SENKRONÄ°ZASYONU (Install/Remove Sync)
    ///
    /// Temel AkÄ±ÅŸ:
    /// 1. Oyuncu parÃ§ayÄ± takar -> ASSEMBLE olayÄ± -> PartMountState (installed=true) gÃ¶nder
    /// 2. Oyuncu parÃ§ayÄ± sÃ¶ker -> REMOVE olayÄ± -> PartMountState (installed=false) gÃ¶nder
    ///
    /// Neden FSM Event Replay DEÄÄ°L, DoÄŸrudan Durum:
    ///   - FSM olayÄ±nÄ±n hedef FSM'si peer'de farklÄ± bir parent hiyerarÅŸisinde olabilir
    ///   - PartMountState doÄŸrudan Installed bool'unu ve parent/hiyerarÅŸiyi yazar
    ///   - Bu sayede FSM replay sorunu (parent mismatch) Ã¶nlenir
    ///
    /// HiyerarÅŸi YÃ¶netimi:
    ///   - TakÄ±lma: ParÃ§a araca parent edilir, localPos/localRot sÄ±fÄ±rlanÄ±r
    ///   - SÃ¶kme: ParÃ§a WORLD kÃ¶kÃ¼ne alÄ±nÄ±r, world pozisyonunda bÄ±rakÄ±lÄ±r
    ///
    /// Sahiplik:
    ///   - Takma/sÃ¶kme yapan oyuncu o anki Owner olur
    ///   - DiÄŸer oyuncu parÃ§a takmaya Ã§alÄ±ÅŸÄ±rsa AuthorityManager Ã§Ã¶zer
    /// </summary>
    public class PartSync
    {
        // Singleton
        public static PartSync Instance { get; private set; }

        // ---- Ayarlar ----
        public const float HEARTBEAT = 1f;

        /// <summary>
        /// AynÄ± parÃ§a iÃ§in iki `REMOVE` gÃ¶nderimi arasÄ±ndaki en az sÃ¼re (s).
        ///
        /// CANLI KANIT (host logu 13:05): `rearlight(leftx)` 8 kez arka arkaya
        /// "removed" oldu (13:05:06.550 / 07.419 / 07.942 / 08.919 / 09.227 /
        /// 09.863 / 11.610 / 11.869). Arada bir kez "fitted"
        /// (trigger_rearlight_left) â€” yani salinim.
        ///
        /// KULLANICI: "sÃ¶kÃ¼lebilir parÃ§alara aim alÄ±rsam parÃ§alar kendiliÄŸinden
        /// sÃ¶kÃ¼lÃ¼yor".
        ///
        /// NEDEN: `Removal` FSM'i zaten sÃ¶kÃ¼lmÃ¼ÅŸ parÃ§a iÃ§in de `REMOVE`
        /// Ã¼retiyor; ayrÄ±ca bizim yazdÄ±ÄŸÄ±mÄ±z `Installed=false` deÄŸerini oyunun
        /// kendi FSM'i geri okuyup yeni bir `REMOVE` Ã¼retiyor â†’ aynÄ± olay iki
        /// tarafta da sonsuz dÃ¶ngÃ¼ye giriyor, paketler birikiyor.
        ///
        /// GerÃ§ek bir sÃ¶kme iÅŸleminin ardÄ±ndan saniyeler iÃ§inde ikinci bir sÃ¶kme
        /// yapÄ±lamaz (vida gevÅŸetme + elle Ã§Ä±karma uzun sÃ¼rer), bu yÃ¼zden
        /// 1.5 sn gÃ¼venli.
        /// </summary>
        public const float REMOVE_DEDUP_WINDOW = 1.5f;

        /// <summary>Son gÃ¶nderilen REMOVE'un parÃ§a yolu (dedup iÃ§in).</summary>
        string lastRemovedId = "";
        /// <summary>Son REMOVE'un zamanÄ± (dedup iÃ§in).</summary>
        float lastRemovedAt = -999f;
        /// <summary>Yutulan tekrar REMOVE sayÄ±sÄ± (tani).</summary>
        public int DedupedRemoves;           // Durum kalp atÄ±ÅŸÄ±

        // ---- Durum ----
        Dictionary<string, PartState> partStates = new Dictionary<string, PartState>(StringComparer.Ordinal);
        float nextBeat;

        // TeÅŸhis
        public int SentStates;
        public int ReceivedStates;
        public int UnresolvedParts;

        class PartState
        {
            public bool Installed;
            public string CarrierPath;       // Kimin elinde/aracÄ±nda
            public string ParentVehiclePath; // Araca takÄ±lÄ±ysa aracÄ±n yolu
            public Vector3 LocalPos;
            public Quaternion LocalRot;
            public ulong OwnerId;
            public float LastChangeTime;
            /// <summary>
            /// Bu durumun SON gÃ¶nderildiÄŸi an. OnarÄ±m kalp atÄ±ÅŸÄ± bunu okur:
            /// yalnÄ±zca uzun sÃ¼redir gÃ¶nderilmemiÅŸ parÃ§alar tekrar yollanÄ±r.
            /// </summary>
            public float LastSentAt;
        }

        public void Discover()
        {
            Instance = this;
            partStates.Clear();
            nextBeat = 0f;

            // SatsumaRegistry'den takÄ±lÄ± parÃ§alarÄ± oku
            if (SatsumaRegistry.Root != null)
            {
                foreach (var part in SatsumaRegistry.AllParts)
                {
                    // Installable parÃ§alar VEYA InstalledVars olan vidasÄ±z parÃ§alar (buji vb.)
                    bool isInstallable = part.Installable;
                    bool hasInstalledVar = part.InstalledVars.Count > 0;
                    bool isBoltlessPart = part.Bolts.Count == 0 && (part.Assembly != null || part.Assemble != null);
                    
                    // Spark plug gibi parÃ§alar: Installable=true, Bolts=0, InstalledVars>0, Assembly=Data FSM
                    bool shouldTrack = isInstallable && (hasInstalledVar || isBoltlessPart);
                    
                    if (shouldTrack && SatsumaRegistry.InstalledOf(part))
                    {
                        partStates[part.Id] = new PartState
                        {
                            Installed = true,
                            CarrierPath = "",
                            ParentVehiclePath = part.Object != null ? GetParentVehiclePath(part.Object) : "",
                            LocalPos = Vector3.zero,
                            LocalRot = Quaternion.identity,
                            OwnerId = 0,
                            LastChangeTime = Time.time
                        };
                    }
                }
            }

            // Buji gibi vidasÄ±z parÃ§alar iÃ§in Installed bool deÄŸiÅŸiklik callback'i
            SatsumaRegistry.OnPartInstalledChangedCallback = OnPartInstalledChangedExternal;

            SmpLog.Log("PartSync: discovered " + partStates.Count + " part states (including boltless parts like spark plugs).");
        }

        void OnPartInstalledChangedExternal(string partId, bool installed, GameObject partObj)
        {
            if (string.IsNullOrEmpty(partId)) return;
            
            if (installed) OnPartInstalled(partId, partObj, SteamTransport.LocalSteamId);
            else OnPartRemoved(partId, partObj, SteamTransport.LocalSteamId);
        }

        public void Clear()
        {
            Instance = null;
            partStates.Clear();
            nextBeat = 0f;
            SatsumaRegistry.OnPartInstalledChangedCallback = null;
        }

        /// <summary>
        /// Peer baÄŸlandÄ±ÄŸÄ±nda (host tarafÄ±nda) tÃ¼m takÄ±lÄ± parÃ§alarÄ±n durumunu gÃ¶nderir.
        /// JIP snapshot ParMountState gÃ¶nderir AMA PartSync internal state'i
        /// (LastSentAt) gÃ¼ncellemez, bu yÃ¼zden heartbeat hiÃ§ atÄ±lmazdÄ±.
        /// Bu metod LastSentAt'i sÄ±fÄ±rlar ve SendHeartbeat'i zorlar.
        /// </summary>
        public void SendInitialState()
        {
            if (!SteamTransport.HasRemote) return;
            // TÃ¼m parÃ§alar iÃ§in LastSentAt = 0 yap -> heartbeat hepsini gÃ¶nderecek
            foreach (var kv in partStates)
            {
                if (kv.Value != null && kv.Value.Installed)
                {
                    kv.Value.LastSentAt = 0f;
                }
            }
            nextBeat = 0f; // hemen heartbeat at
            if (Telemetry.Verbose) SmpLog.Debug("parts", "init", "PartSync initial state sent for " + partStates.Count + " parts");
        }

        public void Update()
        {
            float now = Time.time;

            // KALP ATIÅI ARASI UZATILDI.
            //
            // CANLI KANIT (client oturum Ã¶zeti): net.partmount.send n=956
            // yani bu oturumda 956 AYRI paket gÃ¶nderildi. Sebep: kalp atÄ±ÅŸÄ±
            // 1 Hz'du ve her vuruÅŸta takÄ±lÄ± HER parÃ§a iÃ§in TEK baÅŸÄ±na bir
            // gÃ¼venilir paket yolluyordu (SendPartMountState -> SendReliable).
            //
            // Bu trafik TAMAMEN GEREKSIZDI:
            //   * ParÃ§alarÄ±n durumu zaten Satsuma katmanÄ±nda gidiyor
            //     (Msg.PartState / Msg.PartBatch, 160 parÃ§a, 10 Hz'e kadar),
            //   * vidalar Msg.BoltBatch ile gidiyor (301 vida),
            //   * JIP snapshot'Ä± da aynÄ± durumu taÅŸÄ±yor.
            //
            // Yani PartSync aynÄ± bilgiyi ÃœÃ‡ÃœNCÃœ kez, paket baÅŸÄ±na tek tek
            // gÃ¶nderiyordu. 956 gÃ¼venilir paket = bant geniÅŸliÄŸi, Steam kuyruk
            // baskÄ±sÄ± ve "donma/kasma" hissinin en somut kaynaÄŸÄ±.
            //
            // DÃœZELTME: kalp atÄ±ÅŸÄ± artÄ±k SADECE gerÃ§ekten kaybolmuÅŸ bir
            // durumu onarÄ±r. AÅŸaÄŸÄ±daki SendHeartbeat yalnÄ±zca, karÅŸÄ± taraftan
            // uzun sÃ¼redir onay alÄ±namayan parÃ§alarÄ± gÃ¶nderir.
            if (SteamTransport.HasRemote && now >= nextBeat)
            {
                nextBeat = now + HEARTBEAT;
                SendHeartbeat();
            }
        }

        // ---- Olay GiriÅŸleri (AssemblyPatch/ItemSync'ten Ã§aÄŸrÄ±lÄ±r) ----

        public void OnPartInstalled(string partId, GameObject partObj, ulong ownerId = 0)
        {
            if (string.IsNullOrEmpty(partId)) return;

            var state = GetOrCreateState(partId);
            state.Installed = true;
            state.CarrierPath = "";
            state.ParentVehiclePath = partObj != null ? GetParentVehiclePath(partObj) : "";
            state.LocalPos = Vector3.zero;
            state.LocalRot = Quaternion.identity;
            state.OwnerId = ownerId;
            state.LastChangeTime = Time.time;

            // HiyerarÅŸi: YERELDE ELLEME. GÃ¶nderenin oyunu kendi FSM'i ile
            // zaten doÄŸru montajladÄ±; tetiÄŸi/parent'i kurcalamak oyunu bozar
            // (canlÄ± kanÄ±t: tetikleyici adresine sÄ±fÄ±rlama). AlÄ±cÄ± taraf
            // Satsuma tekrarÄ± + asagidaki OnMessage daliyla monte eder.
            SendPartMountState(partId, true);
        }

        public void OnPartRemoved(string partId, GameObject partObj, ulong ownerId = 0)
        {
            if (string.IsNullOrEmpty(partId)) return;

            var state = GetOrCreateState(partId);

            // ------------------------------------------------------------------
            // SOKME DEDUP PENCERESI (canli hata: "parcalar kendiliginden sokuluyor")
            // ------------------------------------------------------------------
            // CANLI KANIT (host logu, 13:05):
            //   13:05:06.550 [ACT] PART removed  MiscParts/rearlight(leftx)  fsm='Removal'
            //   13:05:07.419 [ACT] PART removed  MiscParts/rearlight(leftx)  fsm='Removal'
            //   13:05:07.942 [ACT] PART removed  MiscParts/rearlight(leftx)  fsm='Removal'
            //   13:05:08.919 [ACT] PART removed  MiscParts/rearlight(leftx)  fsm='Removal'
            //   ... 8 kez arka arkaya, arada bir kez "fitted" (trigger_rearlight_left)
            //
            // KULLANICI: "sokulebilir parcalara aim alirsam parcalar kendiliginden
            // sokuluyor".
            //
            // NEDEN: `Removal` FSM'i parca zaten sokULMUS durumdayken bile
            // `REMOVE` olayini uretebiliyor; ayrica bizim yazdigimiz
            // `Installed=false` degerini oyunun kendi FSM'i geri okuyup yeni
            // bir `REMOVE` uretiyor -> AYNI parca icin sonsuz dongu. Dongu
            // iki tarafi da kirletiyor: paketler birikiyor, parca "titriyor".
            //
            // COZUM: ayni parcaya `REMOVE` gonderimi kisa bir sure icin
            // TEKRARLAMAZ. Gercek bir soke isleminin ardindan saniyeler
            // icinde ikinci bir soke yapilamaz (vida gevsetme suresi uzun).
            if (!state.Installed && partId == lastRemovedId
                && (Time.time - lastRemovedAt) < REMOVE_DEDUP_WINDOW) {
                DedupedRemoves++;
                if (Telemetry.Verbose)
                    SmpLog.Debug("item", "part", "REMOVE dedup: '" + partId + "' "
                        + (REMOVE_DEDUP_WINDOW - (Time.time - lastRemovedAt)).ToString("F1")
                        + "s icinde tekrar gonderilmedi");
                return;
            }

            state.Installed = false;
            state.CarrierPath = "";
            state.ParentVehiclePath = "";
            state.LocalPos = Vector3.zero;
            state.LocalRot = Quaternion.identity;
            state.OwnerId = ownerId;
            state.LastChangeTime = Time.time;
            lastRemovedId = partId;
            lastRemovedAt = Time.time;

            // HiyerarÅŸi: YERELDE ELLEME (yukarÄ±daki gerekÃ§e).
            SendPartMountState(partId, false);
        }

        // ---- Oyun olaylarÄ±ndan Ã¼retim ----

        /// <summary>
        /// Oyun bir parÃ§ayÄ± taktÄ± veya sÃ¶ktÃ¼. TaÅŸÄ±yÄ±cÄ± yol (araÃ§ iÃ§i mi) burada
        /// hesaplanÄ±r ve hem yerel duruma yazÄ±lÄ±r hem paket Ã¼retilir.
        ///
        /// YOLLAR: InteractionManager.OnFsmEvent, ASSEMBLE/REMOVE olaylarÄ±ndan
        /// gelir. Satsuma'nÄ±n kendi PartEvent yolu bunu Ã§aÄŸÄ±rmaz; o yol parÃ§ayÄ±
        /// hedefte FSM'i oynatÄ±r ve transform/sahiplik bilgisi taÅŸÄ±maz.
        /// </summary>
        public void OnPartInstalledOrRemoved(string partId, GameObject partObj, bool installed, ulong ownerId)
        {
            if (string.IsNullOrEmpty(partId)) return;

            if (installed) OnPartInstalled(partId, partObj, ownerId);
            else OnPartRemoved(partId, partObj, ownerId);
        }

        // ---- AÄŸ GÃ¶nderim ----

        void SendPartMountState(string partId, bool installed)
        {
            if (!SteamTransport.HasRemote || string.IsNullOrEmpty(partId)) return;

            var state = GetOrCreateState(partId);

            // GONDERILEN POZE GERCEK POZ OLMALI. Burada once Vector3.zero
            // yaziliyordu; alici tarafta "arac ici degil" dali bu degeri dunya
            // konumu saniyor ve parcayi (0,0,0) -> oyunun altina isinliyordu.
            // Aracin icindeysek araca gore local, degilsek dunya konumu.
            Vector3 localPos = state.LocalPos;
            Quaternion localRot = state.LocalRot;

            var partGo = FindByPath(partId);
            string vehiclePath = state.ParentVehiclePath ?? "";
            if (partGo != null)
            {
                if (!string.IsNullOrEmpty(vehiclePath))
                {
                    var vehicle = FindByPath(vehiclePath);
                    if (vehicle != null)
                    {
                        localPos = vehicle.transform.InverseTransformPoint(partGo.transform.position);
                        localRot = Quaternion.Inverse(vehicle.transform.rotation) * partGo.transform.rotation;
                    }
                }
                else
                {
                    localPos = partGo.transform.position;
                    localRot = partGo.transform.rotation;
                }
            }

            try
            {
                var s = Protocol.Begin(Protocol.Msg.PartMountState);
                var w = new BinaryWriter(s);
                Protocol.WritePartMountState(w, partId, installed, "", localPos, localRot, vehiclePath);
                // Kapsam: gonderen tarafta "send/PartMountState" yazan bir satÄ±r,
                // karsi tarafta "recv/PartMountState". Ayni dosyada yan yana
                // dururlar, yani "gonderdim mi" sorusu tek bakista cevaplanir.
                using (LogScope.Push("send/PartMountState"))
                using (LogSpan.Begin(LogChannels.Net, "partmount.send", 4f))
                {
                    LogEngine.Debug(LogChannels.Net, "send", "part mount state",
                        new LogFields().Add("part", partId).Add("installed", installed)
                            .Add3("x", localPos.x).Add3("y", localPos.y).Add3("z", localPos.z)
                            .Add("vehicle", vehiclePath.Length == 0 ? "-" : vehiclePath).Add("click", MenuMouse.ClickCtx()));
                    if (SteamTransport.SendReliable(Protocol.Finish(s))) {
                    SentStates++;
                    state.LastSentAt = Time.time;
                }
                }
            }
            catch (Exception e) { SmpLog.Warning("PartMountState send failed: " + e.Message); }
        }

        /// <summary>
        /// KALP ATIÅI: artÄ±k tÃ¼m parÃ§alarÄ± deÄŸil, YALNIZ onarÄ±m gerektirenleri
        /// gÃ¶nderir.
        ///
        /// KURAL: bir parÃ§a ancak (a) yerelde takÄ±lÄ±, (b) son gÃ¶nderimden
        /// bu yana BEAT_REPAIR_INTERVAL geÃ§ti VE (c) karÅŸÄ± taraf onayÄ±
        /// vermediyse tekrar gÃ¶nderilir. (c) iÃ§in son alÄ±nan paketin
        /// zamanÄ± kullanÄ±lÄ±r; onay yoksa onarÄ±m sÃ¼resi dolar.
        ///
        /// SÄ±nÄ±rlar: en fazla HEARTBEAT_BATCH parÃ§a/tur, bÃ¶ylece tek karede
        /// onlarca paket birikmesin. FazlasÄ± sonraki tura kalÄ±r.
        /// </summary>
        void SendHeartbeat()
        {
            float now = Time.time;
            int sent = 0;
            foreach (var kv in partStates)
            {
                if (sent >= HEARTBEAT_BATCH) break;
                PartState st = kv.Value;
                if (st == null || !st.Installed) continue;

                // Son gÃ¶nderimden beri doÄŸrulama geldiyse dokunma.
                if (st.LastSentAt > 0f && (now - st.LastSentAt) < BEAT_REPAIR_INTERVAL) continue;
                if (st.LastSentAt <= 0f) continue;   // daha hiÃ§ gÃ¶nderilmedi, zaten akÄ±ÅŸta

                SendPartMountState(kv.Key, true);
                st.LastSentAt = now;
                sent++;
            }
            if (sent > 0) HeartbeatRepairs += sent;
        }

        /// <summary>Kalp atÄ±ÅŸÄ±yla tekrar gÃ¶nderilen parÃ§a sayÄ±sÄ± (tani).</summary>
        public int HeartbeatRepairs;

        /// <summary>YalnÄ±zca bu aralÄ±kta onaysÄ±z kalan parÃ§alar tekrar gÃ¶nderilir (s).</summary>
        public const float BEAT_REPAIR_INTERVAL = 20f;

        /// <summary>Kalp atÄ±ÅŸÄ± turu baÅŸÄ±na azami paket (bant geniÅŸliÄŸi korumasÄ±).</summary>
        public const int HEARTBEAT_BATCH = 2;

        // ---- Snapshot (JIP) ----

        /// <summary>Takili parcalarin durumunu host tarafinin JIP snapshot'ina koyar.</summary>
        public void CollectStates(List<PartMountStateData> list)
        {
            if (list == null) return;
            foreach (var kv in partStates)
            {
                var s = kv.Value;
                if (!s.Installed) continue;
                list.Add(new PartMountStateData
                {
                    PartPath = kv.Key,
                    Installed = true,
                    CarrierPath = s.CarrierPath ?? "",
                    LocalPos = s.LocalPos,
                    LocalRot = s.LocalRot,
                    ParentVehiclePath = s.ParentVehiclePath ?? ""
                });
            }
        }

        /// <summary>Snapshot'tan gelen takma durumunu uygular.</summary>
        public void ApplySnapshotState(string partPath, bool installed, string carrierPath,
            Vector3 localPos, Quaternion localRot, string parentVehiclePath)
        {
            if (string.IsNullOrEmpty(partPath)) return;

            var state = GetOrCreateState(partPath);
            state.Installed = installed;
            state.CarrierPath = carrierPath ?? "";
            state.ParentVehiclePath = parentVehiclePath ?? "";
            state.LocalPos = localPos;
            state.LocalRot = localRot;
            state.LastChangeTime = Time.time;

            var partGo = FindByPath(partPath)?.gameObject;
            if (partGo == null) { UnresolvedParts++; return; }

            if (IsTriggerObject(partGo)) { ReceivedStates++; return; }

            var scope = EchoGuard.Enter();
            try
            {
                if (installed && !string.IsNullOrEmpty(state.ParentVehiclePath))
                {
                    var vehicle = FindByPath(state.ParentVehiclePath);
                    if (vehicle != null)
                    {
                                                // v15: MOUNT NOKTASI ARTÄ°K UYGULANIYOR.
                        // Gonderen taraf zaten araca gore YEREL pozi yaziyor
                        // (bkz. SendPartMountState, bu dosyada InverseTransformPoint/
                        // Inverse(rotation)*rotation). Burada o deger saklanip
                        // SIFIRLANIYORDU, yani takili her parca aracin PIVOTUNDA
                        // duruyordu; kullaniciya "parca dustu" gibi gorunuyordu.
                        // Deger (0,0,0) ise eski build gonderiyor demektir; o durumda
                        // da ayni sonucu verir, ayrica korumaya gerek yok.
                        partGo.transform.localPosition = state.LocalPos;
                        partGo.transform.localRotation = state.LocalRot;
                    }
                }
                else
                {
                    partGo.transform.SetParent(null, true);
                    if (!installed)
                    {
                        partGo.transform.position = state.LocalPos;
                        partGo.transform.rotation = state.LocalRot;
                    }
                }
            }
            finally { scope.Dispose(); }

            ReceivedStates++;
        }

        // ---- AlÄ±cÄ± TarafÄ± ----

        public void OnMessage(BinaryReader r)
        {
            try
            {
                string partPath;
                bool installed;
                string carrierPath;
                Vector3 localPos;
                Quaternion localRot;
                string parentVehiclePath;

                Protocol.ReadPartMountState(r, out partPath, out installed, out carrierPath, out localPos, out localRot, out parentVehiclePath);

                var state = GetOrCreateState(partPath);
                state.Installed = installed;
                state.ParentVehiclePath = parentVehiclePath ?? "";
                state.LocalPos = localPos;
                state.LocalRot = localRot;
                state.LastChangeTime = Time.time;
                // KarÅŸÄ± taraf durumu ALDI: onarÄ±m kalp atÄ±ÅŸÄ±na gerek yok.
                state.LastSentAt = 0f;

                var partGo = FindByPath(partPath)?.gameObject;
                if (partGo == null) { UnresolvedParts++; return; }

                if (IsTriggerObject(partGo)) { ReceivedStates++; return; }

                if (installed)
                {
                    // Tak: araca parent et
                    if (!string.IsNullOrEmpty(state.ParentVehiclePath))
                    {
                        var vehicle = FindByPath(state.ParentVehiclePath);
                        if (vehicle != null)
                        {
                            var scope = EchoGuard.Enter();
                            try
                            {
                                partGo.transform.SetParent(vehicle.transform, true);
                                // v15: MOUNT NOKTASI ARTIK UYGULANIYOR.
                                // Gonderen taraf zaten araca gore YEREL pozi yaziyor
                                // (SendPartMountState: InverseTransformPoint(position) /
                                // Inverse(rotation) * rotation). Burada o deger saklanip
                                // SIFIRLANIYORDU, yani takili her parca aracin PIVOTUNDA
                                // duruyordu; kullaniciya 'parca dustu' gibi gorunuyordu.
                                // Deger (0,0,0) ise eski build gonderiyor demektir; o durumda
                                // da ayni sonucu verir.
                                partGo.transform.localPosition = state.LocalPos;
                                partGo.transform.localRotation = state.LocalRot;
                            }
                            finally { scope.Dispose(); }
                        }
                    }
                    else
                    {
                        // World pozisyonuna bÄ±rak
                        var scope = EchoGuard.Enter();
                        try
                        {
                            partGo.transform.SetParent(null, true);
                            partGo.transform.position = state.LocalPos;
                            partGo.transform.rotation = state.LocalRot;
                        }
                        finally { scope.Dispose(); }
                    }
                }
                else
                {
                    // SÃ¶k: world kÃ¶kÃ¼ne al
                    var scope = EchoGuard.Enter();
                    try
                    {
                        partGo.transform.SetParent(null, true);
                    }
                    finally { scope.Dispose(); }
                }

                ReceivedStates++;
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("PartMountState receive failed: " + e.Message);
            }
        }

        /// <summary>
        /// Tetikleyici (trigger_*) nesne mi? ParÃ§a kayÄ±tlarÄ± tetikleyici
        /// adresinde tutulur (Assembly FSM'i tetikleyicidedir); ama hiyerarÅŸi
        /// iÅŸlemi (parent/sÄ±fÄ±rla) tetiÄŸe uygulanÄ±rsa gÃ¶nderenin oyunu da
        /// alÄ±cÄ±nÄ±n tetik mantÄ±ÄŸÄ± da bozulur. MontajÄ± oyunun FSM tekrarÄ± yapar.
        /// </summary>
        static bool IsTriggerObject(GameObject go) {
            if (go == null) return false;
            string n = null;
            try { n = go.name; } catch { return false; }
              // SMP_TRIGGERNAME_V28
              // v28: `trigger_` oneki TEK BASINA yeterli degildi.
              // DOKUM: Satsuma altinda onek `trigger_` ile BASLAMAYAN
              // ama gercekten tetikleyici olan nesneler var:
              //   TriggerWheel{FL,FR,RL,RR}_New
              //   Trigger_motor1..3
              //   FireTrigger, FireTriggerPlayer
              //   CapTrigger_{BrakeF,BrakeR,Clutch,FuelSatsuma}
              //
              // Bunlar bu filtreye girmedigi icin `installed=false`
              // geldiginde asagidaki uc noktada
              // `SetParent(null, true)` ile ARAC'TAN KOPYORLAR:
              //   :457 (genel), :531 (installed=false), :544 (sokme)
              // CANLI KANIT: `PartMountState 2 gonder / 82 alin`.
              //
              // v28: TEK KAYNAK. `SatsumaRegistry.IsTriggerName`
              // (`:4482`) v25'te dokum kanitiyla bu adeleri kapsayacak
              // sekilde genisletildi. Burada da ayni kural.
              if (SatsumaRegistry.IsTriggerName(go)) return true;
              if (string.IsNullOrEmpty(n)) return false;
              return n.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase);
        }

        PartState GetOrCreateState(string partId)
        {
            PartState state;
            if (!partStates.TryGetValue(partId, out state))
            {
                state = new PartState();
                partStates[partId] = state;
            }
            return state;
        }

        string GetParentVehiclePath(GameObject item)
        {
            if (item == null) return null;
            Transform t = item.transform;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                if (VehicleRegistry.IsVehicleRoot(t.gameObject))
                {
                    return SceneDump.PathOf(t);
                }
                t = t.parent;
            }
            return null;
        }

        GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string rootName, rest;
            SceneDump.SplitRoot(path, out rootName, out rest);

            GameObject[] roots;
            try { roots = SceneDump.AllSceneRoots(); }
            catch { return null; }
            if (roots == null) return null;

            for (int i = 0; i < roots.Length; i++)
            {
                GameObject r = roots[i];
                if (r == null) continue;
                if (!string.Equals(r.name, rootName, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(rest)) return r;
                Transform t;
                try { t = r.transform.Find(rest); }
                catch { continue; }
                if (t == null) continue;
                return t.gameObject;
            }
            return null;
        }

        public string Describe()
        {
            int installed = 0;
            foreach (var kv in partStates) if (kv.Value.Installed) installed++;
            return "parts: " + partStates.Count + " tracked, " + installed + " installed"
                + ", sent=" + SentStates + ", recv=" + ReceivedStates + ", unresolved=" + UnresolvedParts;
        }
    }
}
