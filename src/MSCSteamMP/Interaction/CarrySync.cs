using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// ELDE/TAÅIMA SENKRONÄ°ZASYONU - Relative Transform DesteÄŸi
    ///
    /// Temel AkÄ±ÅŸ:
    /// 1. Oyuncu parÃ§ayÄ± alÄ±r -> PICKUP olayÄ± -> CarryState (held=true) gÃ¶nder
    /// 2. ParÃ§a tutuluyorken -> 10 Hz POSE_INTERVAL ile local transform gÃ¶nder
    /// 3. Oyuncu bÄ±rakÄ±r -> DROP olayÄ± -> CarryState (held=false) gÃ¶nder
    ///
    /// Relative Transform:
    ///   - ParÃ§a bir araÃ§ iÃ§indeyse (parentVehiclePath dolu), transform araca gÃ¶re local.
    ///   - Aksi halde dÃ¼nya koordinatÄ± (world space).
    ///   - Bu sayede yÃ¼ksek hÄ±zlarda paket gecikmesi "uÃ§ma" yaratmaz.
    ///
    /// Fizik YÃ¶netimi:
    ///   - Tutulurken: isKinematic=true, detectCollisions=false, velocity=0
    ///   - BÄ±rakÄ±lÄ±rken: isKinematic=false, detectCollisions=true, gravity=true
    ///
    /// Sahiplik (Authority):
    ///   - ParÃ§ayÄ± alan oyuncu o anki sahiptir (Owner).
    ///   - BaÅŸka oyuncu aynÄ± parÃ§ayÄ± almaya Ã§alÄ±ÅŸÄ±rsa AuthorityManager Ã§akÄ±ÅŸmayÄ± Ã§Ã¶zer.
    ///   - Sahip olan oyuncu positional update gÃ¶nderir; diÄŸerleri interpolasyonla takip eder.
    /// </summary>
    public class CarrySync
    {
        // Singleton
        public static CarrySync Instance { get; private set; }

        // ---- Ayarlar ----
        public const float POSE_INTERVAL = 0.1f;     // 10 Hz pozisyon
        public const float HEARTBEAT = 2f;           // Tutma bayraÄŸÄ± kalp atÄ±ÅŸÄ±
        public const float FOLLOW_RATE = 20f;        // AlÄ±cÄ± interpolasyon hÄ±zÄ± (1/s)
        public const float MAX_FOLLOW_SPEED = 30f;   // Maksimum takip hÄ±zÄ± (m/s)
        public const float TELEPORT_DIST = 5f;       // Bu mesafeden bÃ¼yÃ¼kse teleport

        // ---- Durum (YayÄ±ncÄ±) ----
        string carriedPath;          // Åu an tutulan parÃ§a yolu
        GameObject carriedItem;      // Tutulan nesne referansÄ± (reparent sonrasÄ± yol Ã§Ã¶zÃ¼mÃ¼ bozulur)
        bool isCarrying;             // Yerel oyuncu taÅŸÄ±yor mu?
        ulong carriedByMeId;         // SteamID (yerel iÃ§in 0)
        string carrierVehiclePath;   // TaÅŸÄ±yÄ±cÄ± araÃ§ yolu (iÃ§indeyse)
        Transform carryBaseParent;     // Alma anindaki parent (birakma tespiti)
        float carryGraceUntil;         // Alma reparent'i icin hosgoru penceresi
        float nextPose;
        float nextBeat;
        string lastSentPath;
        bool lastSentHeld;

        // ---- Durum (AlÄ±cÄ±) ----
        GameObject remoteItem;
        string remotePath;
        ulong remoteOwnerId;
        bool remoteHeld;
        Vector3 targetPos;
        Quaternion targetRot;
        string targetVehiclePath;
        bool targetValid;
        Transform targetVehicleTransform;

        // Ã–nbellek
        GameObject cachedItem;
        static float nextCarryNameFallback;
        string cachedPath;

        /// <summary>Ben bu yolu tutuyor muyum? (diger hatlar sorsun diye)</summary>
        public bool IsCarryingPath(string path) {
            try { return isCarrying && !string.IsNullOrEmpty(path)
                && string.Equals(carriedPath, path, StringComparison.Ordinal); }
            catch { return false; }
        }

        /// <summary>Peer bu yolu tutuyor mu (bu hat uzerinden)?</summary>
        public bool IsRemotePath(string path) {
            try { return remoteHeld && remoteItem != null && !string.IsNullOrEmpty(path)
                && string.Equals(remotePath, path, StringComparison.Ordinal); }
            catch { return false; }
        }

        /// <summary>Peer biraktiysa bu hattaki takibi de birak.</summary>
        public void ReleaseRemoteIf(string path) {
            try {
                if (remoteHeld && !string.IsNullOrEmpty(path)
                    && string.Equals(remotePath, path, StringComparison.Ordinal))
                    ReleaseRemote();
            } catch { }
        }

        // TeÅŸhis
        public int SentStates;
        public int ReceivedStates;
        public int UnresolvedItems;

        public void Discover()
        {
            Instance = this;
            Reset();
        }

        // ---- Oyun olaylarÄ±ndan Ã¼retim ----

        /// <summary>
        /// Alma/bÄ±rakma olayÄ± adÄ± mÄ±? ItemSync'in GRAB_EVENTS listesiyle aynÄ±
        /// kriter: olay adÄ± ALMA veya BIRAKMA kelimelerinden birini iÃ§eriyorsa.
        ///
        /// InteractionManager buradan geÃ§iyor; filtre ItemSync'in iÃ§inde PRIVATE
        /// kaldÄ±ÄŸÄ± iÃ§in aynÄ± liste burada tekrarlanÄ±yor. Ä°ki taraf farklÄ±
        /// filtreler kullanÄ±rsa taÅŸÄ±ma akÄ±ÅŸÄ± sessizce tutarsÄ±zlaÅŸÄ±r.
        /// </summary>
        /// ALMA olaylari -- ALT DIZE (dokumde toplu gecer).
        ///
        /// DOKUM KANITI (v23, bu turun denetimi):
        /// Onceki liste `PICKUP/PICK_UP/TAKE/TOOK/GRAB/HOLD/HELD/
        /// CARRY/DROP/THROW/RELEASE` idi ve HICBIR Satsuma parcasini
        /// yakalamiyordu. CANLI KANIT (10:23 oturumu):
        ///     `[ACT] CARRY ...` satiri 0 adet
        ///     `CarryState` 0 gonderilmis / 345 alinmis
        /// Yani tasima hic baslamiyordu.
        ///
        /// NEDEN: `PART` listede YOK. DOKUM:
        ///     `PLAYER/.../1Hand_Assemble/Hand|PickUp`  -> `PART` uretiyor
        ///     `PLAYER/.../1Hand_Assemble/Hand|PickUp`  -> `DROP_PART`
        ///     `PLAYER/.../1Hand_Assemble/Hand|PickUp`  -> `DROP_TOOL`
        /// Alma tarafi `PART`, birakma tarafi `DROP_PART`/`DROP_TOOL`.
        /// `DROP_PART` zaten `DROP` alt dizesiyle YAKALANIYORDU, ama
        /// karsi tarafta tutacak nesne hic gelmedigi icin birakma da
        /// ise yaramiyordu.
        ///
        /// DOKUMDA `PART` TAM AD olarak 489 kayitta gecer; SATSUMA
        /// altinda 99. Bunlarin cogu `Assembly`/`Removal` FSM'idir
        /// (parcanin takili/sokulu tetikleyicisi) -- yani tasiyan
        /// nesne `trigger_*` yoludur, parcanin kendisi degil. Bu
        /// dogru: oyun tetikleyiciyi eline alir, parca GIZLI olarak
        /// gelir.
        /// </summary>
        static readonly string[] GRAB_EVENTS = {
            "PICKUP", "PICK_UP", "TAKE", "TOOK", "GRAB", "HOLD", "HELD",
            "CARRY", "DROP", "THROW", "RELEASE",
            // v23: dokumde dogrulanmis, onceki listede eksikti.
            "ITEM_AX",   // `SelectItem|Selection` ve `2Spanner|PickUp`
            "EQUIP",     // `1Hand_Assemble/Hand|PickUp`
            //
            // SMP_GRAB_V32
            //
            // v32: dokumde `sendEvent` ALANINDA dogrulanmis,
            // once yakalanmayan ve tasima ile ilgili olaylar.
            //
            // "Assembly"  4 gonderen:
                        //   "Assembly" GERI ALINDI (v33): CD kutusu KAPAGI --
            //   tasima degil. Ayrica ALT DIZE riski: "Assembly"
            //   kelimesi gecen "AssemblyDone"/"Disassembly" gibi
            //   token'lar da yakalanirdi.
            //
            // "POURON" / "POUROFF"  ITEMS/coffee cup(itemx)|Use
            //   -> kahve dokum/doldurma.
            //
            // "Fill"  ITEMS/bucket(itemx)|Empty
            //   -> kova doldurma.
            //
            // "ITEM"  3 gonderen:
            //   ITEMS/bucket(itemx)/TriggerFill|Fill
            //   ITEMS/garbage barrel(itemx)/Fire/GarbageTrigger|Logic
            //   SATSUMA/.../trigger_extinguisher|Assembly
            //   -> genel esya bildirimi. `ITEM` TAM AD olarak eklenir;
            //   alt dize olarak `ITEM_AX`i yakalar ama `ITEM`i yakalamaz.
            //
            // EKLENMEYENLER -- dokumde `sendEvent` DEGERI degil, NESNE/FSM
            // adi olanlar:
            //   ItemPivot(9) Untagged(68) Target(44) Part(21)
            //   Trigger(21) BoltCheck(6) Bolted(6) INSTALL(41) PART(0)
            //
            // TASINABILIR OLMAYANLAR -- oyun ici efekt/tetik olaylari:
            //   FIRE Key Knob Scale INWATER Charcoal Grill SLEEP Untagged
            "POURON",
            "POUROFF",
            "Fill",
        };

        /// <summary>
        /// TAM AD olarak eslesen alma olaylari.
        ///
        /// v23 KURALI (onceki turun alt dize yasagiyla ayni):
        /// `PART` ALT DIZE olarak aranamaz. Dokumde `PART` kelimesini
        /// iceren ama baska bir sey olan 12 kayit var:
        ///     PART1(9) PART2(9) PART3(8) PART4(8)   -> TriggerWheel yuvalari
        ///     PARTY_TIME(7) PARTY(2)                -> dans partisi
        ///     APART(3) HUDPARTIAL(2) BROWSE PARTS CATALOG(1)
        /// Bunlar tasima degil; `PART1..4` donanim varyanti yuvasidir
        /// (v16b'de cozuldu) ve asla tasima tetiklemez.
        ///
        /// Oyleyse `PART`u TAM AD olarak yakalayan tek yer burasi.
        /// </summary>
        // SMP_PART_NOT_CARRY_V30
        //
        // v30: `PART` BURAYA YAZILMAZ. v23'te yazilmisti; gerekce
        // yanlisti ("`Hand|PickUp` uretir").
        //
        // DOKUM OLCUMU (`sendEvent` ALANINA atanan degerler):
        //   PART    -> 177 kayit, 72'si SATSUMA, HEPSI `Assembly`
        //   TOOL    ->   0 kayit
        //   RAGDOLL ->   0 kayit
        // Ayrica `tag` ALANINDA: PART 23, ITEM 2, TOOL 4, RAGDOLL 2.
        //
        // YANI `PART` iki ayri yerde geciyor:
        //  (a) 72 Satsuma `Assembly` FSM'i
        //      `TriggerEvent{sendEvent="PART", collideTag=...}` ile
        //      GONDERIR. Bu EL ILE ALMA DEGIL -- fiziksel carpisma
        //      tetikleyicisidir.
        //  (b) `Hand|PickUp` nesnenin `PART` ETIKETLI olup olmadigini
        //      `GameObjectCompareTag` ile KARSILASTIRIR (token 139:
        //      onceki `['GameObjectCompareTag','PickedObject']`, sonraki
        //      `['gameObject','tag','trueEvent','falseEvent',
        //      'storeResult','everyFrame']`). Bu da OLAY DEGIL.
        //
        // `AssemblyPatch.PART_EVENTS` (`:123-127`) `PART`'i bilincli
        // olarak dislamisti: "yerel bir `TriggerEvent` ... GLOBAL GECIS
        // DEGILDIR. Eklenirse yuzlerce nesneden gelen yerel olaylar
        // ag hattina birakilir." v23 bu karari geri aldi.
        //
        // ZATEN ISLENIYOR: `InteractionManager.cs:288-301`
        //   `AssemblyPatch.IsPartEvent` -> `Parts.OnPartInstalledOrRemoved`
        // 72 gonderen `Assembly` FSM'i bu yoldan tasiniyor.
        //
        // EL ILE ALMANIN GERCEK YOLU (dokum + canli log):
        //   `ITEMS/*|Use` -> `USE` (28 nesne)
        //   `InteractionManager.cs:323-328` -> `IsCarryableUse`
        //   canli: `13:35:10.221 [ACT] CTRL OFF
        //          ITEMS/flashlight(itemx)  Use`
        // SMP_GRAB_V33
        //
        // v33: `ITEM` BURAYA. Alt dize olamaz -- `ITEMS`, `ITEM1`,
        // `ITEM_AX` gibi onlarca token'i de yakalardi. Tam ad
        // yalnizca `ITEM`i yakalar.
        //
        // DOKUM: `sendEvent` ALANINDA `ITEM` gonderen 3 kayit:
        //   ITEMS/bucket(itemx)/TriggerFill|Fill
        //   ITEMS/garbage barrel(itemx)/Fire/GarbageTrigger|Logic
        //   SATSUMA(557kg, 248)/.../trigger_extinguisher|Assembly
        static readonly string[] GRAB_EVENTS_EXACT = {
            "ITEM",
        };

        public static bool IsGrabEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            // ONCE tam ad: en dar kriter, en az yanlis pozitif.
            for (int i = 0; i < GRAB_EVENTS_EXACT.Length; i++)
            {
                if (string.Equals(eventName, GRAB_EVENTS_EXACT[i], StringComparison.Ordinal))
                    return true;
            }
            for (int i = 0; i < GRAB_EVENTS.Length; i++)
            {
                if (eventName.IndexOf(GRAB_EVENTS[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// USE olayi gercekten esya alma mi? Dumper kaniti: dunya esyalari
        /// Use FSM'i + USE ile alinir. Kapilar (FSM olay yolunun),
        /// kontroller (CtrlState yolunun), aracin tamami, oyuncunun el kiti
        /// ve NPC iskeleti dislanir.
        ///
        /// SMP_CARRYPARTS_V31: Satsuma ve CARPARTS parcasi ELENMEZ.
        /// Oyun `Hand|PickUp`te `PART` ETIKETI sorar (token 139,
        /// `GameObjectCompareTag`); burada da ayni soru sorulur ve
        /// `CarryableOfTrigger` cagrilir. Cozulemezse tasima yapilmaz.
        /// </summary>
        public static bool IsCarryableUse(PlayMakerFSM fsm, GameObject go)
        {
            if (fsm == null || go == null) return false;
            // Havai fisek cantasi tasinmaz, ATESLENIR (USE = fitil).
            // Tasiyonda karsi tarafta poz spam olur, atesleme olmaz.
            try {
                string nn = go.name;
                if (!string.IsNullOrEmpty(nn)
                    && nn.IndexOf("firework", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            } catch { }
            string fsmName = null;
            try { fsmName = fsm.FsmName; } catch { return false; }

            // SMP_CARRYPARTS_V31
            //
            // ESKIDIR:
            //     if (AssemblyPatch.IsPartFsm(fsmName)) return false;
            // Montaj FSM'i tasiyan HER nesneyi eliyordu:
            // `trigger_bootlid`, `CARPARTS/PartsCar/bootlid(Clone)`
            // dahil. Yani kaput/camurluk/motor eline ALINAMIYORDU.
            //
            // SIMDI: elenmek yerine TASICILABILIR GORUNUR KARDES
            // cozulur.
            //
            // OYUNUN KENDI KURALLARI (dokum, token 139):
            //   PLAYER/.../1Hand_Assemble/Hand|PickUp
            //   GameObjectCompareTag { gameObject=PickedObject, tag=PART }
            // Bu bir OLAY DEGIL, etiket KARSILASTIRMASI -- oyun
            // "alinan nesne `PART` etiketli mi?" diye soruyor.
            //
            // BURADA DA AYNI SORU SORULUR. `CarryableOfTrigger`
            // (SatsumaRegistry:1227):
            //   1) nesnenin kendisi `PART` tag'li + `Renderer` -> o
            //   2) ayni ust nesnenin `PART` tag'li + `Renderer'li kardesi
            //   3) bulunamazsa `null` -- TAHMIN YAPILMAZ
            // `FindTaggedSibling` `CompareTag` kullandigi icin etiket
            // API'DEN okunuyor; dokumde bulunmasi gerekmiyor.
            //
            // COZULEMEZSE TASIMA YAPILMAZ. Yanlis nesneyi tasimak,
            // hic tasimamaktan kotudur.
            //
            // v23 NOTU: `PART`u `GRAB_EVENTS_EXACT`e koymak YANLISTI
            // ve v30'da kaldirildi -- `PART` gonderilen bir OLAY degil,
            // bir ETIKETTIR. Dogrusu bu degisikliktir: olay degil,
            // cozumleyici.
            if (AssemblyPatch.IsPartFsm(fsmName)) {
            	// SMP_CARRY_INSTALLED_V34
            	//
            	// ONCEDEN sadece etikete bakiyordu:
            	//     return SatsumaRegistry.CarryableOfTrigger(go) != null;
            	// Yani ARACA MONTE EDILMIS bir parca da "tasir" diye
            	// isaretleniyordu. Kullanici sorusu: "takilmamis parcalar
            	// icin" -- montajli parca tasinMAMALI.
            	//
            	// KURAL:
            	//   Installed == true   -> montajli, tasima YAPILMAZ
            	//   Installed == false  -> sokulmus, tasinabilir
            	//   okunamaz (bilinmiyor)     -> tasinir: eldeki takilmamis parca garantisi
            	//
            	// `InstalledOf` "bilinmiyor" ile "hayir" ayni degeri
            	// donuyor (bilinen API tuzagi), bu yuzden
            	// `HasKnownInstallState` ile birlikte okunur.
            	try {
            		GameObject real = SatsumaRegistry.CarryableOfTrigger(go);
            		if (real == null) return false;
            		SatsumaPart part;
            		try { part = SatsumaRegistry.PartOf(real); } catch { part = null; }
            		// Tabloda yoksa etiket VE gorunurluk yeter kanit;
            		// `Database/*` satirlari buraya girmez ve onlar da
            		// elle alinmiyor.
            		if (part == null) return true;
            		if (SatsumaRegistry.HasKnownInstallState(part) && SatsumaRegistry.InstalledOf(part)) return false;
            		return true;
            	}
            	catch { return false; }
            }
            try { if (WorldObjectSync.IsTrackedDoor(go)) return false; } catch { }
            try { if (WorldObjectSync.IsKnownControl(fsm)) return false; } catch { }
            try { if (VehicleRegistry.IsVehicleRoot(go)) return false; } catch { }
            try
            {
                Transform t = go.transform;
                int guard = 0;
                while (t != null && guard++ < 12)
                {
                    string n = t.name;
                    if (string.Equals(n, "1Hand_Assemble", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, "FPSCamera", StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.Equals(n, "Char", StringComparison.Ordinal)) return false;
                    t = t.parent;
                }
            }
            catch { }
            // GUI/menÃ¼ kokleri: envanter ikonlari (GUI/Icons) USE bildirir
            // ama tasinir esya degildir. Tasiyonda karsi tarafin arayuzu
            // yerinden oynar ("kayboluyor" sikayetinin sebebi).
            try {
                Transform r = go.transform;
                int g = 0;
                while (r.parent != null && g++ < 16) r = r.parent;
                string rn = r.name;
                if (string.Equals(rn, "GUI", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rn, "Interface", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            return true;
        }

        static bool IsDropEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            string[] dropEvents = { "DROP", "THROW", "RELEASE" };
            for (int i = 0; i < dropEvents.Length; i++)
            {
                if (eventName.IndexOf(dropEvents[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Oyun bir eÅŸya alma/bÄ±rakma olayÄ± gÃ¶nderdi. YalnÄ±zca FSM'in kendi
        /// nesnesi taÅŸÄ±nabilir kabul edilir; taÅŸÄ±ma yolu buradan baÅŸlar
        /// (SetCarrying / SetDropping).
        /// </summary>
        public void OnGrabEvent(string path, GameObject item, string eventName)
        {
            if (string.IsNullOrEmpty(path) || item == null) return;

            if (IsDropEvent(eventName))
            {
                SetDropping(path);
                return;
            }

            // v30: `PART` OLAYI BURAYA GELMEZ.
            //
            // v23 bu blogu "tetikleyiciye gelen PART, gorunur parcayi
            // bulur" gerekcesiyle yazdi ve `CarryableOfTrigger` cagirdi.
            // Blok KALDIRILDI.
            //
            // DOKUM: `PART` iki ayri yerde gecer.
            //   (a) 72 Satsuma `Assembly` FSM'i
            //       `TriggerEvent{sendEvent="PART", collideTag=...}` ile
            //       GONDERIR. Bu EL ILE ALMA DEGIL; carpisma olayidir
            //       ve zaten `InteractionManager.cs:288-301` ->
            //       `Parts.OnPartInstalledOrRemoved` ile isleniyor.
            //   (b) `Hand|PickUp` `PART` etiketini `GameObjectCompareTag`
            //       ile karsilastirir (token 139). Olay degil.
            //
            // Yani bu blok ya hic calismiyordu ya da bir carpismayi
            // "parca alindi" sanirdi. Ikisi de istenen degil.
            //
            // Buraya gelenler: `IsCarryableUse` ile onaylanmis `USE`
            // (28 `ITEMS/*` nesnesi) ve `GRAB_EVENTS` alt dize eslesen
            // gercek al/birak olaylari.

            SetCarrying(path, item, SteamTransport.LocalSteamId);
        }

        public void Clear()
        {
            Instance = null;
            Reset();
            ReleaseRemote();
        }

        /// <summary>
        /// Peer baÄŸlandÄ±ÄŸÄ±nda (host tarafÄ±nda) tutma durumunu gÃ¶nderir.
        /// JIP snapshot CarryState boÅŸ gider (peer henÃ¼z eÅŸya tutmuyor).
        /// Host tarafÄ±nda yerel durumu gÃ¶nder ki peer senkron olsun.
        /// </summary>
        public void SendInitialState()
        {
            if (!SteamTransport.HasRemote) return;
            // EÄŸer yerelde bir eÅŸya tutuyorsak, durumu gÃ¶nder
            if (isCarrying && !string.IsNullOrEmpty(carriedPath))
            {
                // Kalp atÄ±ÅŸÄ±nÄ± hemen tetikle
                nextBeat = 0f;
                SendCarryState(true);
                if (Telemetry.Verbose) SmpLog.Debug("carry", "init", "CarrySync initial state sent for " + carriedPath);
            }
        }

        void Reset()
        {
            carriedPath = null;
            carriedItem = null;
            isCarrying = false;
            carriedByMeId = 0;
            carrierVehiclePath = null;
            nextPose = 0f;
            nextBeat = 0f;
            lastSentPath = null;
            lastSentHeld = false;
            ReleaseRemote();
        }

        public void Update()
        {
            float now = Time.time;

            // 1. Yerel tutma/bÄ±rakma tespiti (ItemSync olaylarÄ±ndan gelir)
            // Bu kÄ±sÄ±m ItemSync.OnFsmEvent tarafÄ±ndan tetiklenen SetCarrying/SetDropping Ã§aÄŸrÄ±lÄ±r

            // 2. Pozisyon gÃ¶nderimi (tutarken)
            if (isCarrying && now >= nextPose)
            {
                nextPose = now + POSE_INTERVAL;
                SendPose();
            }

            // 3. Kalp atÄ±ÅŸÄ± (tutma bayraÄŸÄ±)
            if (isCarrying && now >= nextBeat)
            {
                nextBeat = now + HEARTBEAT;
                SendCarryState(true);
            }

            // 3b. Birakma tespiti: esya yok olduysa (tuketildi/yok edildi)
            // ya da parent hosgoru disinda degistiyse (elden dustu) birak.
            if (isCarrying)
            {
                GameObject live = null;
                try { live = carriedItem != null ? carriedItem : (string.IsNullOrEmpty(carriedPath) ? null : FindByPath(carriedPath)); }
                catch { live = null; }
                if (live == null)
                {
                    SetDropping(carriedPath ?? "");
                }
                else
                {
                    Transform p = null;
                    try { p = live.transform.parent; } catch { p = carryBaseParent; }
                    if (now < carryGraceUntil) carryBaseParent = p;
                    else if (p != carryBaseParent) SetDropping(carriedPath);
                }
            }

            // 4. AlÄ±cÄ± interpolasyonu
            FollowRemote(now);
        }

        // ---- Yerel Kontrol (ItemSync'ten Ã§aÄŸrÄ±lÄ±r) ----

        public void SetCarrying(string path, GameObject item, ulong ownerId = 0)
        {
            if (isCarrying && path == carriedPath) return;

            carriedPath = path;
            carriedItem = item;
            isCarrying = true;
            carriedByMeId = ownerId;

            // AraÃ§ iÃ§i mi kontrol et
            carrierVehiclePath = GetParentVehiclePath(item);
            // Birakma tespiti: alma sirasindaki reparent 1.5 sn hosgoruyle
            // taban yapilir; sonrasinda parent degisirse esya birakilmistir.
            try { carryBaseParent = item != null ? item.transform.parent : null; } catch { carryBaseParent = null; }
            carryGraceUntil = Time.time + 1.5f;

            // Fizik: kinematik, Ã§arpÄ±ÅŸma kapalÄ±
            var rb = item.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            var cols = item.GetComponentsInChildren<Collider>();
            foreach (var c in cols) if (c != null) c.enabled = false;

            // GÃ¶rÃ¼nÃ¼rlÃ¼k
            var rends = item.GetComponentsInChildren<Renderer>();
            foreach (var r in rends) if (r != null) r.enabled = true;

            try { SmpTrace.Act("CARRY", "took", path + " owner=" + ownerId); } catch { }
            SendCarryState(true);

            // v15: SAHIPLIK TALEBI.
            // AuthorityManager.Claim'in HIC CAGIRANI YOKTU; dolayisiyla
            // ownerOf her zaman bos kaldi, Msg.AuthorityClaim (53) hic
            // gonderilmedi ve JIP sahiplik snapshot'u (Msg 56) her zaman
            // 0 kayit tasiyordu. Iki oyuncu ayni esyayi ayni anda
            // aldiginda da "kim once almis" sorusu sorulamiyordu.
            // Alma aninda talep et, birakma aninda birak.
            ClaimAuthority(path, true);
        }

        /// <summary>
        /// Esya uzerinde sahiplik talebi (alirken) / birakma (birakirken).
        ///
        /// NEDEN AYRI FONKSIYON: Claim hem de Release burada kullanilir;
        /// hata olursa tasima akisi KIRILMAMALI, bu yuzden her sey kendi
        /// try blogunda.
        /// </summary>
        static void ClaimAuthority(string path, bool take) {
            try {
                if (string.IsNullOrEmpty(path)) return;
                if (!SteamTransport.HasRemote) return;   // tek basina test: gereksiz paket
                var auth = InteractionManager.Authority;
                if (auth == null) return;
                auth.Claim(path, take ? AuthorityRequestType.Request
                                    : AuthorityRequestType.Release);
            } catch (Exception e) {
                SmpLog.Debug("carry", "authority",
                    "sahiplik istegi basarisiz (" + path + "): " + e.Message);
            }
        }

        public void SetDropping(string path)
        {
            if (!isCarrying || path != carriedPath) return;

            // YOL Ã–NCE OKUNMALI: SendCarryState, bÄ±rakma iÃ§in de nesnenin YERINI
            // yazmak zorunda, Ã§Ã¼nkÃ¼ alÄ±cÄ± tarafta bÄ±rakma paketi yolda ne
            // olduÄŸunu sÃ¶ylemek zorunda. carriedPath'i Ã¶nce null'larsak paket
            // BOÅ yol gider ve karÅŸÄ± taraf hangi eÅŸyayÄ± bÄ±raktÄ±ÄŸÄ±nÄ± bilemez.
            string droppedPath = carriedPath;
            string droppedVehiclePath = carrierVehiclePath;
            // Nesne DE ONCE okunmali: asagida carriedItem null'lanacak, geri
            // verilecek fizik o nesnede duruyor.
            GameObject droppedItem = carriedItem;

            isCarrying = false;
            carriedPath = null;
            carriedItem = null;
            carrierVehiclePath = null;

            try { SmpTrace.Act("CARRY", "dropped", droppedPath); } catch { }

            // YEREL FIZIGI GERI VER.
            //
            // CANLI HATA (dÃ¼zeltildi): `SetCarrying` eÅŸyayÄ± alÄ±rken
            // `isKinematic=true`, `detectCollisions=false` ve bÃ¼tÃ¼n
            // collider'larÄ± kapatÄ±yordu (541-550). BÄ±rakmanÄ±n tek yolu olan
            // `SetDropping` bunlarÄ±n HÄ°Ã‡BÄ°RÄ°NÄ° geri vermiyordu; dosyanÄ±n kendi
            // baÅŸlÄ±ÄŸÄ±ndaki sÃ¶zleÅŸme ("BÄ±rakÄ±lÄ±rken: isKinematic=false,
            // detectCollisions=true, gravity=true", satÄ±r 25) yerel tarafta hiÃ§
            // uygulanmamÄ±ÅŸ. Geri veren tek kod `ReleaseRemote` (1168-1192) ve o
            // yalnÄ±zca KARSI taraftan gelen eÅŸya iÃ§in Ã§alÄ±ÅŸÄ±yordu.
            //
            // SonuÃ§: elden Ã§Ä±kan / yeniden parentalanan eÅŸya yerelde kinematik
            // kalÄ±yordu â€” yer Ã§ekimi Ã§alÄ±ÅŸmÄ±yor, collider'larÄ± kapalÄ±, yani
            // yerde donuk duruyor ve fiziksel olarak alÄ±namÄ±yor. KarÅŸÄ± tarafta
            // aynÄ± eÅŸya doÄŸru biÃ§imde dÃ¼ÅŸÃ¼yordu: iki taraf farklÄ± durumda.
            //
            // `ReleaseRemote` ile AYNI Ã¼Ã§ bayrak yazÄ±lÄ±r. EÅŸya yok olduysa
            // (tÃ¼ketildi) geri verme sessizce atlanÄ±r.
            RestoreLocalPhysics(droppedItem);

            SendCarryState(false, droppedPath, droppedVehiclePath);
            ClaimAuthority(droppedPath, false);   // v15: esya artik serbest
        }

        /// <summary>
        /// BÄ±rakÄ±lan (yerel) eÅŸyanÄ±n fiziÄŸini geri verir.
        ///
        /// `ReleaseRemote` ile AYNI Ã¼Ã§ bayraÄŸÄ± yazar; tek fark bu yerel
        /// `carriedItem` iÃ§in Ã§alÄ±ÅŸÄ±yor. Kendi try blogunda: eÅŸya o anda yok
        /// olmuÅŸsa (tÃ¼ketildi / yok edildi) bÄ±rakma akÄ±ÅŸÄ± kÄ±rÄ±lmamalÄ±.
        /// </summary>
        void RestoreLocalPhysics(GameObject item) {
            try {
                if (item == null) return;
                var rb = item.GetComponent<Rigidbody>();
                if (rb != null) {
                    rb.isKinematic = false;
                    rb.detectCollisions = true;
                    rb.useGravity = true;
                }
                var cols = item.GetComponentsInChildren<Collider>();
                foreach (var c in cols) if (c != null) c.enabled = true;
            } catch { }
        }

        // ---- AÄŸ GÃ¶nderim ----

        void SendCarryState(bool held)
        {
            SendCarryState(held, carriedPath, carrierVehiclePath);
        }

        void SendCarryState(bool held, string path, string vehiclePath)
        {
            if (!SteamTransport.HasRemote) return;
            if (held && string.IsNullOrEmpty(path)) return;

            try
            {
                GameObject item = null;
                try { if (held && !string.IsNullOrEmpty(path) && string.Equals(path, carriedPath, StringComparison.Ordinal) && carriedItem != null) item = carriedItem; } catch { item = null; }
                if (item == null) { try { item = FindByPath(path); } catch { item = null; } }
                Vector3 localPos = Vector3.zero;
                Quaternion localRot = Quaternion.identity;

                if (item != null)
                {
                    var t = item.transform;
                    if (!string.IsNullOrEmpty(vehiclePath))
                    {
                        var vehicle = FindByPath(vehiclePath);
                        if (vehicle != null)
                        {
                            localPos = vehicle.transform.InverseTransformPoint(t.position);
                            localRot = Quaternion.Inverse(vehicle.transform.rotation) * t.rotation;
                        }
                        else
                        {
                            localPos = t.position;
                            localRot = t.rotation;
                        }
                    }
                    else
                    {
                        localPos = t.position;
                        localRot = t.rotation;
                    }
                }

                var s = Protocol.Begin(Protocol.Msg.CarryState);
                var w = new BinaryWriter(s);
                Protocol.WriteCarryState(w, path ?? "", held, SteamTransport.LocalSteamId.ToString(), localPos, localRot, vehiclePath ?? "");
                // Kapsam: gonderen tarafta "send/CarryState", karsi tarafta
                // "recv/CarryState".
                //
                // v23 YORUM DUZELTMESI: burada once "held=false paketinde
                // konum anlamsizdir, yazilmaz" yaziyordu. YANLISTI.
                // `WriteCarryState` (`InteractionProtocol.cs:80`) konumu
                // HER ZAMAN yaziyor -- `held` bayragi ayri bir alan.
                // Dokum kaniti gosteriyor: alici taraf `held == false`
                // dalinda paketin konumunu OKUYUP nesneyi isinliyor
                // (`CarrySync.OnMessage`, "Birakma." blogu) ve fizige
                // birakiyor. Yani konumun yazilmamasi, birakma
                // senkronunu TAMAMEN bozardi. Yorum duzeltildi; wire
                // format DEGISTIRILMEDI (PROTOCOL_VERSION 20'de kalir).
                using (LogScope.Push("send/CarryState"))
                {
                    LogFields f = new LogFields()
                        .Add("item", path ?? "")
                        .Add("held", held)
                        .Add("vehicle", string.IsNullOrEmpty(vehiclePath) ? "-" : vehiclePath);
                    if (held) f.Add3("x", localPos.x).Add3("y", localPos.y).Add3("z", localPos.z);
                    LogEngine.Debug(LogChannels.Net, "send", "carry state", f);
                    if (SteamTransport.SendReliable(Protocol.Finish(s))) SentStates++;
                }
            }
            catch (Exception e) { SmpLog.Warning("CarryState send failed: " + e.Message); }
        }

        void SendPose()
        {
            if (!SteamTransport.HasRemote || !isCarrying || string.IsNullOrEmpty(carriedPath)) return;

            GameObject item = carriedItem;
            if (item == null) { try { item = FindByPath(carriedPath); } catch { item = null; } }
            if (item == null) return;

            var t = item.transform;
            Vector3 localPos = Vector3.zero;
            Quaternion localRot = Quaternion.identity;

            if (!string.IsNullOrEmpty(carrierVehiclePath))
            {
                var vehicle = FindByPath(carrierVehiclePath);
                if (vehicle != null)
                {
                    localPos = vehicle.transform.InverseTransformPoint(t.position);
                    localRot = Quaternion.Inverse(vehicle.transform.rotation) * t.rotation;
                }
                else
                {
                    localPos = t.position;
                    localRot = t.rotation;
                }
            }
            else
            {
                localPos = t.position;
                localRot = t.rotation;
            }

            try
            {
                var s = Protocol.Begin(Protocol.Msg.CarryState);
                var w = new BinaryWriter(s);
                Protocol.WriteCarryState(w, carriedPath, true, SteamTransport.LocalSteamId.ToString(), localPos, localRot, carrierVehiclePath ?? "");
                if (SteamTransport.SendUnreliable(Protocol.Finish(s))) SentStates++;
            }
            catch (Exception e) { SmpLog.Warning("Carry pose send failed: " + e.Message); }
        }

        // ---- Snapshot (JIP) ----

        /// <summary>
        /// Host tarafinin JIP snapshot'ina koyacagi durum. Yalnizca ELDE TUTULAN
        /// nesne gonderilir; bÄ±rakÄ±lmÄ±ÅŸ nesnenin durumu zaten iki tarafta da aynÄ±.
        /// </summary>
        public void CollectStates(List<CarryStateData> list)
        {
            if (list == null || !isCarrying || string.IsNullOrEmpty(carriedPath)) return;

            GameObject item = carriedItem;
            if (item == null) { try { item = FindByPath(carriedPath); } catch { item = null; } }
            Vector3 localPos = Vector3.zero;
            Quaternion localRot = Quaternion.identity;

            if (item != null)
            {
                var t = item.transform;
                if (!string.IsNullOrEmpty(carrierVehiclePath))
                {
                    var vehicle = FindByPath(carrierVehiclePath);
                    if (vehicle != null)
                    {
                        localPos = vehicle.transform.InverseTransformPoint(t.position);
                        localRot = Quaternion.Inverse(vehicle.transform.rotation) * t.rotation;
                    }
                    else
                    {
                        localPos = t.position;
                        localRot = t.rotation;
                    }
                }
                else
                {
                    localPos = t.position;
                    localRot = t.rotation;
                }
            }

            list.Add(new CarryStateData
            {
                Path = carriedPath,
                Held = true,
                CarrierPath = carriedByMeId.ToString(),
                LocalPos = localPos,
                LocalRot = localRot,
                ParentVehiclePath = carrierVehiclePath ?? ""
            });
        }

        /// <summary>Snapshot'tan gelen tutma durumunu uygular (canli paket yoluyla ayni).</summary>
        public void ApplySnapshotState(string path, bool held, string carrierPath,
            Vector3 localPos, Quaternion localRot, string parentVehiclePath)
        {
            if (string.IsNullOrEmpty(path)) return;

            var item = FindByPath(path);
            if (item == null) { UnresolvedItems++; return; }

            ulong ownerId;
            if (!ulong.TryParse(carrierPath, out ownerId)) ownerId = 0;

            if (!held)
            {
                if (remoteHeld) ReleaseRemote();
                return;
            }

            remotePath = path;
            remoteOwnerId = ownerId;
            remoteHeld = true;
            targetVehiclePath = parentVehiclePath;

            if (remoteItem != item)
            {
                ReleaseRemote();
                remoteItem = item;
                ForceVisible(item, true);
            }

            if (!string.IsNullOrEmpty(parentVehiclePath))
            {
                var vehicle = FindByPath(parentVehiclePath);
                if (vehicle != null)
                {
                    targetVehicleTransform = vehicle.transform;
                    targetPos = vehicle.transform.TransformPoint(localPos);
                    targetRot = vehicle.transform.rotation * localRot;
                }
                else
                {
                    targetVehicleTransform = null;
                    targetPos = localPos;
                    targetRot = localRot;
                }
            }
            else
            {
                targetVehicleTransform = null;
                targetPos = localPos;
                targetRot = localRot;
            }

            targetValid = true;
            ReceivedStates++;
        }

        // ---- AlÄ±cÄ± TarafÄ± ----

        /// <summary>Peer'in tasidigi esyanin bizdeki konumu (kol uzatma icin).</summary>
        		/// <summary>Uzaktan gelen son carry paketinin zamani (kol bayatlik kontrolu).</summary>
		public static float LastRemoteMsg = -999f;
		/// <summary>Ben tutarken gelen yabanci tutma paketi (yok sayildi).</summary>
		public int CarryConflicts;

		public bool TryGetRemoteCarriedPos(out Vector3 p)
        {
            p = Vector3.zero;
            try
            {
                if (!remoteHeld || remoteItem == null) return false;
                if (targetValid) { p = targetPos; return true; }
                p = remoteItem.transform.position;
                return true;
            }
            catch { return false; }
        }

        public void OnMessage(BinaryReader r)
        {
            try
            {
                string path;
                bool held;
                string carrierId;
                Vector3 localPos;
                Quaternion localRot;
                string parentVehiclePath;

                Protocol.ReadCarryState(r, out path, out held, out carrierId, out localPos, out localRot, out parentVehiclePath);
				try { LastRemoteMsg = Time.time; } catch { }

                if (held)
                {
                    ulong ownerId;
                    if (!ulong.TryParse(carrierId, out ownerId)) ownerId = 0;

                    // CIFT ALMA: ben tutuyorsam benim elim gecerli. Karsi taraf
                    // elindekini birakmadan ayni esyayi alamaz (oyunda da
                    // oyledir: kinematik + carpismasiz ele yapisilamaz).
                    // Benim kalp atisim karsiyi duzeltir; kavga edilmez.
                    try {
                        if (isCarrying && !string.IsNullOrEmpty(path)
                            && string.Equals(carriedPath, path, StringComparison.Ordinal)) {
                            CarryConflicts++;
                            return;
                        }
                    } catch { }

                    var item = FindByPath(path);
                    if (item == null) { UnresolvedItems++; return; }

                    // Sahiplik ata
                    remotePath = path;
                    remoteOwnerId = ownerId;
                    remoteHeld = true;
                    targetVehiclePath = parentVehiclePath;

                    if (remoteItem != item)
                    {
                        ReleaseRemote();
                        remoteItem = item;
                        ForceVisible(item, true);
                    }

                    // Hedef pozisyon hesapla
                    if (!string.IsNullOrEmpty(parentVehiclePath))
                    {
                        var vehicle = FindByPath(parentVehiclePath);
                        if (vehicle != null)
                        {
                            targetVehicleTransform = vehicle.transform;
                            targetPos = vehicle.transform.TransformPoint(localPos);
                            targetRot = vehicle.transform.rotation * localRot;
                        }
                        else
                        {
                            targetPos = localPos;
                            targetRot = localRot;
                            targetVehicleTransform = null;
                        }
                    }
                    else
                    {
                        targetPos = localPos;
                        targetRot = localRot;
                        targetVehicleTransform = null;
                    }

                    targetValid = true;
                    ReceivedStates++;
                }
                else
                {
                    // Birakma.
                    //
                    // KANIT (kullanici): "biraktigimda havada asili kaliyor,
                    // ucarak gidiyor". Eski kod paketteki konumu COP'E ATIP
                    // yalniz fizik bayragi ceviriyordu: esya interpolatorun
                    // biraktigi STALE noktada kaliyordu (elde/havada).
                    //
                    // Yeni: birakma noktasina ISINLAN, hizi sifirla, fizige ver
                    // (yercekimiyle yere duser). Diger iki hat da ayni esyayi
                    // birakir (cift surucu savasi bitar).
                    if (remoteHeld)
                    {
                        try {
                            GameObject dropItem = remoteItem != null ? remoteItem : FindByPath(path);
                            if (dropItem != null) {
                                var scope = EchoGuard.Enter();
                                try {
                                    Vector3 dp = localPos;
                                    Quaternion dr = localRot;
                                    if (!string.IsNullOrEmpty(parentVehiclePath)) {
                                        var veh = FindByPath(parentVehiclePath);
                                        if (veh != null) {
                                            dp = veh.transform.TransformPoint(localPos);
                                            dr = veh.transform.rotation * localRot;
                                        }
                                    }
                                    dropItem.transform.position = dp;
                                    dropItem.transform.rotation = dr;
                                    Rigidbody drb = null;
                                    try { drb = dropItem.GetComponent<Rigidbody>(); } catch { }
                                    if (drb != null) {
                                        drb.velocity = Vector3.zero;
                                        drb.angularVelocity = Vector3.zero;
                                    }
                                }
                                finally { scope.Dispose(); }
                            }
                        } catch { }
                        ReleaseRemote();
                        // NOT: firlatma hizi TASIINMAZ (pakette yok). Esya
                        // birakildigi noktadan sÃ¼zÃ¼lerek duser; havada asili
                        // kalma ve ucup gitme biter.
                        try { HeldItemSync.ReleasePath(path); } catch { }
                        try { GarageEntitySync.ForgetRemote(path); } catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("CarryState receive failed: " + e.Message);
            }
        }

        // ---- Interpolasyon ----

        void FollowRemote(float now)
        {
            if (!targetValid || remoteItem == null) return;
            if (!remoteHeld) { ReleaseRemote(); return; }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Hedef pozisyonu gÃ¼ncelle (taÅŸÄ±yÄ±cÄ± araÃ§ hareket edebilir)
            Vector3 currentTargetPos = targetPos;
            Quaternion currentTargetRot = targetRot;

            if (targetVehicleTransform != null)
            {
                // Relative transform: aracÄ±n hareketini takip et
                // Not: localPos/localRot son paketten, targetVehicleTransform gÃ¼ncel
            }

            float dist = Vector3.Distance(remoteItem.transform.position, currentTargetPos);

            if (dist > TELEPORT_DIST)
            {
                // Teleport
                var scope = EchoGuard.Enter();
                try
                {
                    remoteItem.transform.position = currentTargetPos;
                    remoteItem.transform.rotation = currentTargetRot;
                }
                finally { scope.Dispose(); }
            }
            else
            {
                // YumuÅŸak takip
                float k = 1f - Mathf.Exp(-FOLLOW_RATE * dt);
                float maxStep = MAX_FOLLOW_SPEED * dt;
                float step = Mathf.Min(dist, maxStep);
                if (dist > 1e-4f)
                {
                    var scope = EchoGuard.Enter();
                    try
                    {
                        remoteItem.transform.position = Vector3.Lerp(remoteItem.transform.position, currentTargetPos, k);
                        remoteItem.transform.rotation = Quaternion.Slerp(remoteItem.transform.rotation, currentTargetRot, k);
                    }
                    finally { scope.Dispose(); }
                }
            }
        }

        // ---- YardÄ±mcÄ± Metotlar ----

        string GetParentVehiclePath(GameObject item)
        {
            if (item == null) return null;
            Transform t = item.transform;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                // AraÃ§ kÃ¶kleri: Satsuma, veya VehicleRegistry'de kayÄ±tlÄ± araÃ§lar
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
            if (string.Equals(path, cachedPath, StringComparison.Ordinal))
            {
                if (cachedItem == null) cachedPath = null;
                else return cachedItem;
            }

            var found = ResolveByPath(path);
            cachedPath = found != null ? path : null;
            cachedItem = found;
            return found;
        }

        static GameObject ResolveByPath(string path)
        {
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
            // Yaprak-adi yedegi (HeldItemSync ile ayni kural): klon
            // numarasi az farkli kayitta tam yol tutmaz. Renderer'li eslesme ara.
            string leaf = path;
            int cut = path.LastIndexOf('/');
            if (cut >= 0 && cut + 1 < path.Length) leaf = path.Substring(cut + 1);
            if (!string.IsNullOrEmpty(leaf)) {
                float nowF = 0f;
                try { nowF = Time.time; } catch { }
                if (nowF >= nextCarryNameFallback) {
                    nextCarryNameFallback = nowF + 3f;
                    for (int r2i = 0; r2i < roots.Length; r2i++) {
                        GameObject r2 = roots[r2i];
                        if (r2 == null) continue;
                        Transform f2 = null;
                        try { f2 = FindByNameLeaf(r2.transform, leaf); } catch { continue; }
                        if (f2 != null) return f2.gameObject;
                    }
                }
            }
            return null;
        }

        static Transform FindByNameLeaf(Transform t, string leaf) {
            if (t == null || string.IsNullOrEmpty(leaf)) return null;
            int n = 0;
            try { n = t.childCount; } catch { return null; }
            for (int i = 0; i < n; i++) {
                Transform c = null;
                try { c = t.GetChild(i); } catch { continue; }
                if (c == null) continue;
                if (string.Equals(c.name, leaf, StringComparison.Ordinal)) {
                    Renderer[] rr = null;
                    try { rr = c.GetComponentsInChildren<Renderer>(true); } catch { }
                    if (rr != null && rr.Length > 0) return c;
                }
            }
            for (int i = 0; i < n; i++) {
                Transform c2 = null;
                try { c2 = t.GetChild(i); } catch { continue; }
                if (c2 == null) continue;
                Transform f = FindByNameLeaf(c2, leaf);
                if (f != null) return f;
            }
            return null;
        }

        void ForceVisible(GameObject item, bool held)
        {
            try
            {
                if (held && !item.activeSelf) item.SetActive(true);
                var rb = item.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = held;
                    rb.detectCollisions = !held;
                    if (held) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                }
                if (held)
                {
                    var cols = item.GetComponentsInChildren<Collider>();
                    foreach (var c in cols) if (c != null) c.enabled = false;
                }
                else
                {
                    var cols = item.GetComponentsInChildren<Collider>();
                    foreach (var c in cols) if (c != null) c.enabled = true;
                }
                var rends = item.GetComponentsInChildren<Renderer>();
                foreach (var r in rends) if (r != null) r.enabled = true;
            }
            catch { }
        }

        void ReleaseRemote()
        {
            if (remoteItem != null)
            {
                try
                {
                    var rb = remoteItem.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                        rb.detectCollisions = true;
                        rb.useGravity = true;
                    }
                    var cols = remoteItem.GetComponentsInChildren<Collider>();
                    foreach (var c in cols) if (c != null) c.enabled = true;
                }
                catch { }
            }
            remoteItem = null;
            remotePath = null;
            remoteOwnerId = 0;
            remoteHeld = false;
            targetValid = false;
            targetVehicleTransform = null;
        }

        public string Describe()
        {
            return "carry: " + (isCarrying ? carriedPath : "none") + " held=" + isCarrying
                + ", remote=" + (remoteHeld ? remotePath : "none")
                + ", sent=" + SentStates + ", recv=" + ReceivedStates + ", unresolved=" + UnresolvedItems;
        }
    }
}
