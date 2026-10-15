using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;
using SteamMP.Interaction;

namespace SteamMP
{
    /// <summary>
    /// ETKÄ°LEÅÄ°M YÃ–NETÄ°CÄ°SÄ° - TÃ¼m etkileÅŸim alt sistemlerini koordine eder.
    ///
    /// Sorumluluklar:
    ///   - CarrySync: El/taÅŸÄ±ma senkronizasyonu (relative transform desteÄŸi)
    ///   - PartSync: ParÃ§a takma/sÃ¶kme senkronizasyonu
    ///   - BoltBatcher: Vida sÄ±kma toplu gÃ¶nderim
    ///   - AuthorityManager: Sahiplik Ã§akÄ±ÅŸma Ã§Ã¶zÃ¼mÃ¼
    ///   - JIPHandler: Sonradan katÄ±lan oyuncuya tam durum gÃ¶nderimi
    ///   - Recovery: Harita altÄ±ndan dÃ¼ÅŸen parÃ§alarÄ± kurtarma
    ///
    /// Mimari:
    ///   Her alt sistem kendi Update/Discover/Clear/OnMessage'Ä±nÄ± yÃ¶netir.
    ///   InteractionManager yalnÄ±zca yaÅŸam dÃ¶ngÃ¼sÃ¼nÃ¼ koordine eder ve
    ///   Ã§apraz kesim concern'leri (sahiplik, JIP, recovery) merkeziyen yÃ¶netir.
    /// </summary>
    public static class InteractionManager
    {
        // Alt sistemler
        public static CarrySync Carry { get; private set; }
        public static PartSync Parts { get; private set; }
        public static BoltBatcher Bolts { get; private set; }
        public static AuthorityManager Authority { get; private set; }
        public static JIPHandler JIP { get; private set; }
        public static RecoveryManager Recovery { get; private set; }

        static bool initialized;
        static float nextAuthorityTick;

        /// <summary>
        /// InteractionManager kuruldu mu? FSMEventHook her karede buna bakar:
        /// taÅŸÄ±yÄ±cÄ±/sahiplik tablolarÄ± hazÄ±r olmadan gelen olaylar
        /// null referans yerine sessizce dÃ¼ÅŸmesin.
        /// </summary>
        public static bool IsInitialized { get { return initialized; } }

        /// <summary>
        /// Chat/konsol acikken susturulan etkilesim gonderimi.
        ///
        /// CANLI BELIRTI: "sokulebilir parcalara aim alirsam parcalar
        /// kendiliginden sokuluyor." UI acikken kamera dondugu icin
        /// niÅŸangah sabit kaliyor ve UI tiklamasi `Input.GetMouseButtonDown`
        /// ile PlayMaker'a ulasip `Removal` FSM'inde "Remove part" uretiyor.
        /// </summary>
        public static int UiBlockedEvents;

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;

            Carry = new CarrySync();
            Parts = new PartSync();
            Bolts = new BoltBatcher();
            Authority = new AuthorityManager();
            JIP = new JIPHandler();
            Recovery = new RecoveryManager();

            // Olay abonelikleri
            SteamTransport.OnPeerConnected += OnPeerConnected;
            SteamTransport.OnPeerDisconnected += OnPeerDisconnected;
        }

        public static void Discover()
        {
            if (!initialized) Initialize();

            // SatsumaRegistry'nin hazÄ±r olmasÄ± gerekir (parÃ§a tablosu)
            if (SatsumaRegistry.Root == null)
            {
                SmpLog.Warning("InteractionManager: SatsumaRegistry.Root null, part sync limited.");
            }

            Carry.Discover();
            Parts.Discover();
            Bolts.Discover();
            Authority.Discover();
            Recovery.Discover();

            // Kaydetme, oturumdan SONRA yuklenmis olabilir (menu'de baglanip
            // sonra Continue). O durumda SteamTransport.OnPeerConnected daha
            // once atilmisti ve JIP snapshot'i hic gitmemisti; tablo hazir
            // oldugu icin istegi simdi tekrarliyoruz.
            if (SteamTransport.HasRemote) OnPeerConnected();

            SmpLog.Log("InteractionManager: all subsystems discovered.");
        }

        public static void Clear()
        {
            if (!initialized) return;

            Carry.Clear();
            Parts.Clear();
            Bolts.Clear();
            Authority.Clear();
            JIP.Clear();
            Recovery.Clear();

            initialized = false;
            warnedNotReady = false;
            SmpLog.Log("InteractionManager: cleared.");
        }

        public static void Update()
        {
            if (!initialized) return;

            float now = Time.time;

            Carry.Update();
            Parts.Update();
            Bolts.Update();
            Authority.Update();
            Recovery.Update();
            Bolts.ReconcileBolts();

            // JIP de periyodik calismali: kendi kare guncellestirmesi snapshot
            // isteginin zaman asimini izliyor ve gecikmis istegi yeniliyor.
            // Bu cagri olmadan istemci, host'un cevabini hic gormezse bir daha
            // sormuyordu.
            JIP.Update();

            // Sahiplik Ã§akÄ±ÅŸma kontrolÃ¼ (5 Hz)
            if (now >= nextAuthorityTick)
            {
                nextAuthorityTick = now + 0.2f;
                Authority.TickConflictResolution();
            }
        }

        // ---- Oyun olaylarÄ±ndan Ã¼retim (AssemblyPatch'ten gelir) ----

        /// <summary>
        /// ParÃ§a takma/sÃ¶kme ve eÅŸya alma/bÄ±rakma olaylarÄ±nÄ± ilgili alt sisteme
        /// daÄŸÄ±tÄ±r.
        ///
        /// Bu giriÅŸ noktasÄ± olmadan CarrySync ve PartSync'in gÃ¶nderim tarafÄ± hiÃ§
        /// Ã§alÄ±ÅŸmÄ±yordu: SetCarrying / OnPartInstalled / OnPartRemoved
        /// hiÃ§bir yerden Ã§aÄŸrÄ±lmadÄ±ÄŸÄ± iÃ§in paket Ã¼retilmiyor, alÄ±cÄ± taraf da
        /// bekleyecek bir ÅŸey bulamÄ±yordu.
        ///
        /// YankÄ± korumasÄ± Ã§aÄŸÄ±ran tarafta yapÄ±lÄ±r (EchoGuard.Suppressed), bÃ¶ylece
        /// karÅŸÄ± taraftan gelen olay yerelde yeniden Ã¼retilip geri gÃ¶nderilmez.
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<string, float> probeCooldown = 
    new System.Collections.Generic.Dictionary<string, float>();

    public static void OnFsmEvent(PlayMakerFSM fsm, string eventName)
        {
            if (fsm == null || string.IsNullOrEmpty(eventName)) return;
            if (EchoGuard.Suppressed) return;
            // PROB (Paket B): salter + CD kapagi tiklamasi FSM'ye ulasiyor mu?
            // Baglanti/sahne sarti YOK: tek basina testte de gorunur. Yalniz
            // Switch/MainSwitch/cd adli nesnelerde calisir, spam yapmaz.
            // try-catch burada ISTISNA yutucu; alt sistemler birbirini etkilemez.
            try {
                GameObject pgo = fsm.gameObject;
                if (pgo != null) {
                    string pgn = pgo.name;
                    if (string.Equals(pgn, "Switch", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pgn, "MainSwitch", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pgn, "cd", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pgn, "TrackChannelSwitch", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pgn, "Eject", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pgn, "RadioCDSwitch", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pgn, "RadioVolume", StringComparison.OrdinalIgnoreCase)) {
                        string pfn = null;
                        try { pfn = fsm.FsmName; } catch { }
                        string probeKey = SceneDump.PathOf(pgo.transform) + "|" + (pfn ?? "?") + "|" + eventName;
                        float now = Time.time;
                        float last;
                        if (probeCooldown.TryGetValue(probeKey, out last) && now - last < 10f) {}
                        else {
                            probeCooldown[probeKey] = now;
                            SmpLog.Log("[PROBE] switch '" + SceneDump.PathOf(pgo.transform)
                                + "' fsm='" + (pfn ?? "?") + "' ev='" + eventName + "'" + "  " + MenuMouse.ClickCtx());
                        }
                    }
                }
            } catch { } // probe logging errors yalniz bu bloğa düşürülür; diğer hatalar yayilir

            GameObject go0;
            try { go0 = fsm.gameObject; } catch { return; }

            // UI SIZINTISI KAPISI (DARALTILDI): ESYA ALMA/BIRAKMA HARIC.
            //
            // KANIT (kullanici): "esyayi aliyorum ama oldugu yerde kaliyor
            // BAZEN". Mekanizma: UI acikken alinan esyanin grab olagi burada
            // susturuluyordu; yerel alma oluyor ama peer'e paket gitmiyor,
            // UI kapaninca da tekrar gonderilmiyordu (kalp atisi yalniz
            // isCarrying bayragi kuruluyken calisir, o bayrak hic kurulmadi)
            // -> KALICI ayrisma. "Bazen" = UI acikken alininca.
            //
            // Kapi yalniz PARCA/VIDA/FIREWORK yolunu susturur (orada sizinti
            // gercek: `Removal` FSM'i `Mouse off` + `BoolAnyTrue` ile tiklamasiz
            // REMOVE uretiyor + UI tiklamasi PlayMaker'a ulasiyor). Eldeki
            // esyanin karsida gorunmemesi, UI'ye yanlis tiklamaktan kotudur.
            // icin (`CameraLookGuard`) niÅŸangah sabit kalir ve UI'ye yapilan
            // tiklama `Input.GetMouseButtonDown` ile PlayMaker'a ulasir.
            // Dumper kaniti (`rearlight(leftx)` fsm='Removal'):
            //   Mouse over -> GetMouseButtonDown -> sendEvent "Remove part"
            // Yani sohbete yazarken parca sokuluyordu. Gonderimi susturuyoruz;
            // oyunun kendi FSM'i normal calismaya devam eder.
            bool carryEvt = false;
            try {
                if (CarrySync.IsGrabEvent(eventName)) carryEvt = true;
                else if (string.Equals(eventName, "USE", StringComparison.OrdinalIgnoreCase)
                    && go0 != null && CarrySync.IsCarryableUse(fsm, go0)) carryEvt = true;
            } catch { }
            try {
                if (MenuMouse.BlockWorldInteraction && !carryEvt) {
                    UiBlockedEvents++;
                    // UI tiklamasi dunya tiklamasina donusuyordu; sayac
                    // teÅŸhis icin (smp netstat / APPLY-FUNNEL ciktisi).
                    if (Telemetry.Verbose)
                        SmpLog.Debug("interaction", "ui", "UI acik, etkilesim susturuldu: "
                            + eventName);
                    return;
                }
            }
            catch { }

            // Tablo yoksa (kaydetme yuklenmedi) alt sistemler null; oyun olaylari
            // yine de geliyor ve uretim yapilamaz.
            if (!initialized) return;
            // Ãœretim yalnÄ±zca oturum varken anlamlÄ±: aksi halde sahiplik
            // tablosu oyuncu etkileÅŸimi yokken dolar.
            if (!SteamTransport.HasRemote) return;

            GameObject go;
            try { go = fsm.gameObject; } catch { return; }
            if (go == null) return;
			// CD DISK (fiziksel kizak + disk): dumper kaniti dump2/level2.json
			// (trigger_disc/Assembly: "CD in" + cd_sled_in, Eject + cd_sled_out).
			// CtrlState durumu aynalar ama disk kimligini (CD1/2/3) tasimaz;
			// kimlik CdDisc paketiyle gider (CdDiscSync). Yankisiz: bu noktaya
			// yalnizca yankisiz olaylar ulasir (fonksiyon basi EchoGuard).

            // 1a. CD disk: slot olayi + Eject dugmesi + disk cikarma.
            // Parca yolundan ONCE: slot ASSEMBLE ayni zamanda parca
            // olayidir, ikisi de uretilir (farkli paketler).
            try {
                string ifn = null;
                try { ifn = fsm.FsmName; } catch { }
                if (!string.IsNullOrEmpty(ifn)) {
                    if (string.Equals(go.name, "trigger_disc", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ifn, "Assembly", StringComparison.OrdinalIgnoreCase)) {
                        string slot = null;
                        try { slot = SceneDump.PathOf(go.transform); } catch { }
                        if (!string.IsNullOrEmpty(slot)) CdDiscSync.OnSlotEvent(slot, fsm, eventName);
                    }
                    else if (string.Equals(go.name, "Eject", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(eventName, "USE", StringComparison.OrdinalIgnoreCase)) {
                        CdDiscSync.OnEjectPressed(go);
                    }
                    else if (go.name.StartsWith("cd(item", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(eventName, "REMOVE", StringComparison.OrdinalIgnoreCase)) {
                        CdDiscSync.OnDiscRemoved(go);
                    }
                    else if (go.name.StartsWith("cd case(item", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ifn, "Open", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(eventName, "USE", StringComparison.OrdinalIgnoreCase)) {
                        // Kutu kapagi: USE her tiklamada acar/kapatir (cd_caseopen/close).
                        CdDiscSync.OnCaseLidToggle(go);
                    }
                    else if (string.Equals(go.name, "trigger_cd", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ifn, "Assembly", StringComparison.OrdinalIgnoreCase)
                        && CdDiscSync.HasAncestorLike(go, "cd case(item")) {
                        // Kutuya disk iade: ASSEMBLE (cd_cdin) / DISASSEMBLE.
                        // Panel tetikleyicisi (dashboard TriggersRadio) elenir.
                        CdDiscSync.OnCaseDiscEvent(go, fsm, eventName);
                    }
                }
            }
            catch { }

            // 1. ParÃ§a takma/sÃ¶kme.
            if (AssemblyPatch.IsPartEvent(eventName))
            {
                string fsmName;
                try { fsmName = fsm.FsmName; } catch { fsmName = null; }
                if (!string.IsNullOrEmpty(fsmName) && AssemblyPatch.IsPartFsm(fsmName))
                {
                    // PartSync parÃ§ayÄ± Satsuma tablosunda da arar; bilinmeyen
                    // nesne (kablo baÄŸlantÄ±sÄ± gibi) sessizce atlanÄ±r.
                    bool install = string.Equals(eventName, "ASSEMBLE", StringComparison.OrdinalIgnoreCase);
                    try { Parts.OnPartInstalledOrRemoved(SceneDump.PathOf(go.transform), go, install, SteamTransport.LocalSteamId); }
                    catch { }
                }
                return;
            }

            // 2. EÅŸya alma/bÄ±rakma: taÅŸÄ±ma (Carry) akÄ±ÅŸÄ±.
            if (Carry != null && CarrySync.IsGrabEvent(eventName))
            {
                // SMP_CARRY_RESOLVE_V33: 2. dal icin ayni cozum.
                // `PICKUP`/`GRAB`/`TAKE` bir tetikleyiciye gelirse
                // gonderilecek nesne ONUN gorunur kardesidir.
                try {
                    GameObject carry = go;
                    string fsn = null;
                    try { fsn = fsm.FsmName; } catch { fsn = null; }
                    if (!string.IsNullOrEmpty(fsn) && AssemblyPatch.IsPartFsm(fsn)) {
                        GameObject resolved = SatsumaRegistry.CarryableOfTrigger(go);
                        if (resolved == null) return;
                        carry = resolved;
                        SmpTrace.Act("CARRY", "resolved-grab",
                            SceneDump.PathOf(carry.transform) + " (tetikleyici: "
                            + SceneDump.PathOf(go.transform) + ")");
                    }
                    Carry.OnGrabEvent(SceneDump.PathOf(carry.transform), carry, eventName);
                }
                catch { }
            }
            // 2a. Havai fisek atesleme: canta Open+USE ile yanar (fitil
            // ayri nesne degil). Tasma YOK (CarrySync dislar); tek reliable
            // paket karsida ayni USE'yi oynatir.
            if (string.Equals(eventName, "USE", StringComparison.OrdinalIgnoreCase)
                && IsFirework(go)) {
                try { SendFireworkLight(SceneDump.PathOf(go.transform)); }
                catch { }
                return;
            }
            // 2b. USE ile alma: MSC dunya esyalari (ITEMS/beercase0,
            // beer case(itemx), magazin urunleri) Use FSM'i + USE olayiyla
            // alinir; PICKUP/GRAB gibi bir olay bildirilmez (dumper kaniti:
            // docs/fsm_detail/l2__beercase0__Use.txt). Kapilar, kontroller,
            // parcalar, araclar, el kiti ve NPC iskeleti dislanir.
            else if (Carry != null && string.Equals(eventName, "USE", StringComparison.OrdinalIgnoreCase)
                && CarrySync.IsCarryableUse(fsm, go))
            {
                // SMP_CARRY_RESOLVE_V33
                //
                // ONCEDEN: `PathOf(go.transform)` + `go` gonderiliyordu,
                // yani `trigger_bootlid` gibi bir TETIKLEYICI.
                // Arkadasinda gorunecek nesne o TETIKLEYICI olurdu --
                // oyun bos bir kare gosterir, parca degil.
                //
                // COZUM: montaj FSM'i tasiyan bir nesne icin once
                // TASICILABILIR gorunur kardes cozulur
                // (`SatsumaRegistry.CarryableOfTrigger`, `PART` etiketi
                // + `Renderer`). Cozulemezse HICBIR SEY gonderilmez --
                // yanlis nesne tasimak, hic tasimamaktan kotudur.
                //
                // Dunya esyalari (`ITEMS/*`) bu yoldan degisir: onlarin
                // `CarryableOfTrigger` sonucu KENDILERI doner, cunku
                // `PART` etiketli degillerdir ama nesne zaten
                // tasinabilir.
                try {
                    GameObject carry = go;
                    if (AssemblyPatch.IsPartFsm(fsm.FsmName)) {
                        GameObject resolved = SatsumaRegistry.CarryableOfTrigger(go);
                        if (resolved == null) return;
                        carry = resolved;
                        SmpTrace.Act("CARRY", "resolved",
                            SceneDump.PathOf(carry.transform) + " (tetikleyici: "
                            + SceneDump.PathOf(go.transform) + ")");
                    }
                    Carry.OnGrabEvent(SceneDump.PathOf(carry.transform), carry, eventName);
                }
                catch { }
            }
        }

        // ---- Peer baÄŸlantÄ±/kesilme ----

        static void OnPeerConnected()
        {
            // Host: JIP snapshot gÃ¶nder
            if (SteamTransport.IsHost)
            {
                JIP.SendFullSnapshot();
            }
            // Client: snapshot iste
            else
            {
                JIP.RequestFullSnapshot();
            }

            // Sahiplik snapshot paylaÅŸ
            Authority.SendAuthoritySnapshot();

            // YENÄ°: PartSync ve CarrySync baÅŸlangÄ±Ã§ durumu gÃ¶nder
            // JIP snapshot ParMountState iÃ§eriyor AMA PartSync kendi internal state'ini
            // (lastSentAt vb.) gÃ¼ncellemediÄŸi iÃ§in heartbeat atÄ±lmÄ±yordu.
            // CarrySync iÃ§in: peer'in elindeki eÅŸya JIP'de boÅŸ gider (peer henÃ¼z eÅŸya tutmuyor).
            // Host tarafÄ±nda yerel durumu gÃ¶nder ki peer senkron olsun.
            if (SteamTransport.IsHost)
            {
                PartSync.Instance?.SendInitialState();
                CarrySync.Instance?.SendInitialState();
            }
        }

        static void OnPeerDisconnected()
        {
            // BaÄŸlantÄ± koptuÄŸunda sahiplik temizle
            Authority.OnPeerDisconnected();
        }

        // ---- Mesaj yÃ¶nlendirme ----

        public static bool RouteMessage(Protocol.Msg id, BinaryReader r)
        {
            // Gelen etkilesim paketi. Kapsam "recv/<mesaj>" oldugu icin dosyada
            // "recv/PartMountState" gibi bir etiket olusur; gonderen taraftaki
            // "send" satiriyla ayni dosyada karsilastirilabilir.
            //
            // ONCE OLUSTURMA KONTROLU.
            //
            // CANLI KANIT (client logu 06.10, 14:25:22.168):
            //   "recv interaction packet failed: PartMountState
            //    err=NullReferenceException"
            //   "recv interaction packet failed: FullStateSnapshot
            //    err=NullReferenceException"
            //
            // Sebep: alt sistemler DUNYA YUKLENMEDEN once null. Host, peer
            // baglandigi anda (menudeyken bile) JIP snapshot'i ve PartSync
            // kalp atisi gonderebiliyor; o paketler kayit yuklenmeden once
            // geliyor ve `Parts.OnMessage(r)` null bir uygunluk (instance)
            // Ã¼zerinde Ã§aÄŸrÄ±lÄ±yordu. NullReferenceException, paketin
            // KAYIP olduÄŸu anlamÄ±na gelmiyordu ama iÅŸlem de yapÄ±lmÄ±yordu.
            //
            // DoÄŸrusu: paket dÃ¼ÅŸÃ¼rÃ¼lÃ¼r ve SEBEP bir kez yazÄ±lÄ±r. UyarÄ± bir
            // kez yazÄ±lÄ±yor, yoksa 1 Hz kalp atÄ±ÅŸÄ± logu dolduruyordu.
            if (!initialized) {
                if (!warnedNotReady) {
                    warnedNotReady = true;
                    SmpLog.Warning("interaction packet '" + id + "' arrived before the world "
                        + "was ready; dropped. The peer will resync once it loads its save.");
                }
                return false;
            }
            try
            {
                using (LogScope.Push("recv/" + id))
                {
                    switch (id)
                    {
                        case Protocol.Msg.CarryState:
                            if (Carry == null) return false;
                            Carry.OnMessage(r);
                            return true;

                        case Protocol.Msg.PartMountState:
                            if (Parts == null) return false;
                            Parts.OnMessage(r);
                            return true;

                        case Protocol.Msg.BoltBatchV2:
                            if (Bolts == null) return false;
                            Bolts.OnMessage(r);
                            return true;

                        case Protocol.Msg.AuthorityClaim:
                            if (Authority == null) return false;
                            Authority.OnAuthorityMessage(r);
                            return true;

                        case Protocol.Msg.FullStateSnapshot:
                            if (JIP == null) return false;
                            JIP.OnFullSnapshot(r);
                            return true;

                        case Protocol.Msg.PartRecovery:
                            if (Recovery == null) return false;
                            Recovery.OnRecoveryMessage(r);
                            return true;

                        case Protocol.Msg.AuthoritySnapshot:
                            if (Authority == null) return false;
                            Authority.OnAuthoritySnapshot(r);
                            return true;

                        case Protocol.Msg.FireworkLight:
                            OnFireworkLight(r);
                            return true;

                        // DoorState(80) / SwitchState(81) / EngineState(82) /
                        // EnvironmentState(83) BURAYA DÃœÅMEZ.
                        //
                        // SteamMPBehaviour'in switch'i bunlarÄ± kendisi
                        // tÃ¼ketir (DoorState/SwitchState/EngineState ->
                        // WorldObjectSync, EnvironmentState -> EnvSync) ve
                        // `break` ile dÃ¼ÅŸer. Yani buradaki case'ler ERÄ°ÅÄ°LEMEZ.
                        //
                        // Neden silindi: proje kuralÄ± "her paketin TEK
                        // okuyucusu vardÄ±r" (bkz. FluidWearSync v9 notu:
                        // Ã¼Ã§ okuyucu birden varken paketin 2/3'Ã¼
                        // "Failed to read past end of stream" veriyordu).
                        // Ä°kinci bir okuyucu â€” Ã§aÄŸrÄ±lsa bile â€” aynÄ± streami
                        // ikinci kez tÃ¼ketecekti.
                        //
                        // Bu kimliklerin gÃ¶ndericisi de yok; canlÄ± hatlar:
                        //   kapÄ±/anahtar/kontak : Msg.CtrlState, Msg.ObjectState
                        //   ortam                : Msg.EnvClock(79)
                        // yani 80-83 yalnÄ±zca ALICI-ONLY ve boÅŸ kimlikler.
                    }
                }
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.WarnChannel(LogChannels.Net, "recv", "interaction packet failed: " + id,
                    new LogFields().Add("err", e.GetType().Name).Add("msg", e.Message)
                        .Add("where", e.StackTrace ?? "-"));
            }
            return false;
        }

        /// <summary>"DÃ¼nya hazÄ±r deÄŸilken paket geldi" uyarÄ±sÄ± bir kez yazÄ±ldÄ± mÄ±?</summary>
        static bool warnedNotReady;

        // ---- TanÄ± ----

        static bool IsFirework(GameObject go) {
            if (go == null) return false;
            try {
                string n = go.name;
                return !string.IsNullOrEmpty(n)
                    && n.IndexOf("firework", StringComparison.OrdinalIgnoreCase) >= 0;
            } catch { return false; }
        }

        public static void SendFireworkLight(string path) {
            if (!SteamTransport.HasRemote || string.IsNullOrEmpty(path)) return;
            try {
                MemoryStream ms = Protocol.Begin(Protocol.Msg.FireworkLight);
                BinaryWriter w = new BinaryWriter(ms);
                w.Write(path);
                w.Flush();
                if (SteamTransport.SendReliable(Protocol.Finish(ms))) {
                    SmpTrace.Act("FIREWORK", "lit", path);
                }
            }
            catch (Exception e) { SmpLog.Warning("FireworkLight send failed: " + e.Message); }
        }

        static GameObject ResolveFirework(string path) {
            if (string.IsNullOrEmpty(path)) return null;
            string rootName, rest;
            SceneDump.SplitRoot(path, out rootName, out rest);
            GameObject[] roots = null;
            try { roots = SceneDump.AllSceneRoots(); } catch { return null; }
            if (roots == null) return null;
            for (int i = 0; i < roots.Length; i++) {
                GameObject r = roots[i];
                if (r == null) continue;
                if (!string.Equals(r.name, rootName, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(rest)) return r;
                Transform t = null;
                try { t = r.transform.Find(rest); } catch { continue; }
                if (t == null) continue;
                return t.gameObject;
            }
            return null;
        }

        static void OnFireworkLight(BinaryReader r) {
            string path = null;
            try { path = r.ReadString(); } catch { return; }
            if (string.IsNullOrEmpty(path)) return;
            GameObject go = ResolveFirework(path);
            if (go == null) {
                SmpLog.Warning("firework '" + path + "' not found here; skipped.");
                return;
            }
            // Hedef: Open FSM, yoksa USE bildiren ilk FSM.
            PlayMakerFSM target = null;
            PlayMakerFSM[] fsms = null;
            try { fsms = go.GetComponents<PlayMakerFSM>(); } catch { return; }
            if (fsms != null) {
                for (int i = 0; i < fsms.Length; i++) {
                    if (fsms[i] == null) continue;
                    string fn = null;
                    try { fn = fsms[i].FsmName; } catch { continue; }
                    if (string.Equals(fn, "Open", StringComparison.OrdinalIgnoreCase)) { target = fsms[i]; break; }
                }
                if (target == null) {
                    for (int i = 0; i < fsms.Length; i++) {
                        if (fsms[i] == null) continue;
                        try {
                            List<string> ev = FsmNames.DeclaredEvents(fsms[i]);
                            if (ev == null) continue;
                            for (int k = 0; k < ev.Count; k++) {
                                if (string.Equals(ev[k], "USE", StringComparison.OrdinalIgnoreCase)) {
                                    target = fsms[i];
                                    break;
                                }
                            }
                        } catch { continue; }
                        if (target != null) break;
                    }
                }
            }
            if (target == null) {
                SmpLog.Warning("firework '" + path + "' has no usable FSM; skipped.");
                return;
            }
            EchoGuard.Scope scope = EchoGuard.Enter();
            try {
                target.SendEvent("USE");
                SmpTrace.Act("FIREWORK", "lit", path + "  (remote)");
            }
            catch (Exception e) { SmpLog.Warning("firework replay failed: " + e.Message); }
            finally { scope.Dispose(); }
        }

        public static string Describe()
        {
            if (!initialized) return "Interaction: not initialised (no save loaded yet)";
            return "Interaction: carry=" + Carry.Describe()
                + ", parts=" + Parts.Describe()
                + ", bolts=" + Bolts.Describe()
                + ", authority=" + Authority.Describe()
                + ", jip=" + JIP.Describe()
                + ", recovery=" + Recovery.Describe();
        }
    }
}
