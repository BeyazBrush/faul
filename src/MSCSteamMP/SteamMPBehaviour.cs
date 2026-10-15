using System;
using System.IO;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// The mod's only runtime object: Steam callbacks, packet routing, the per-frame
    /// flush and the on-screen panel.
    ///
    /// It exists because MSCLoader's SetupFunction hooks are plain delegates, not
    /// MonoBehaviours, so they cannot run a coroutine. The Satsuma initial sync is
    /// deliberately spread over frames (218 parts at 8 per frame is 28 frames), and that
    /// needs StartCoroutine - hence this object, and hence the static Instance the
    /// Satsuma code reaches for.
    ///
    /// Ordering per frame is deliberate:
    ///   1. SteamTransport.Update()  - run callbacks, read packets, heartbeat, timeout
    ///   2. AutoLoad.Update()        - menu/world divergence warnings and auto-enter
    ///   3. SatsumaRegistry.Update() - one flush of parts and bolts
    ///   4. vehicle transforms       - 20 Hz, unreliable
    ///   5. player positions         - 20 Hz, unreliable
    /// Reads first, writes last: state applied this frame never leaves again this frame.
    /// </summary>
    public class SteamMPBehaviour : MonoBehaviour
    {
        public static SteamMPBehaviour Instance;

        /// <summary>
        /// Oyun penceresi odakta mi? Unity 5.0.0f4'te Application.isFocused
        /// YOK, o yuzden OnApplicationFocus ile kendimiz tutuyoruz.
        /// Odak bizde degilken (Alt-Tab) imlec/ses gibi OS-paylasimli
        /// kaynaklara dokunulmaz.
        /// </summary>
        public static bool AppFocused = true;

        void OnApplicationFocus(bool focus) { AppFocused = focus; }

        /// <summary>Vehicle transform streaming rate. 20 Hz is plenty for another car.</summary>
        const float VEHICLE_SEND_INTERVAL = 0.05f;
        /// <summary>How often the world is checked for having appeared or vanished.</summary>
        const float WORLD_CHECK_INTERVAL = 1f;

        ConnectionPanel panel = new ConnectionPanel();
        LogConsole console = new LogConsole();
        bool worldInstalled;
        float nextVehicleSend;
        float nextWorldCheck;

        /// <summary>The " key log window. Public so 'smp log' can reach it.</summary>
        public LogConsole Console { get { return console; } }

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Sıra önemli. Önce oyunun ve MSCLoader'ın loglarını yönlendir, sonra
            // kendi logumuzu aç: aksi hâlde modun kendi yazdığı ilk satırlar
            // yakalanmadan geçer.
            //
            // GameLogCapture  : oyunun + MSCLoader'ın mesajları  -> Mods\mscmlog.txt
            //                    Debug.Log (Unity + PlayMaker FSM) -> Mods\unitylog.txt
            // SmpLog          : modun kendi mesajları            -> Mods\mscmp.txt
            // LogConsole (F3) : ekranda canlı görüntü
            //
            // MSCLoader'ın kendi konsol EKRANI artık hiç kullanılmıyor; onun
            // yazdığı her şey Mods klasörüne gidiyor.
            GameLogCapture.Install();
            SmpLog.Init();
            // BUILD ETIKETI: logdan hangi DLL'nin calistigi kanitlanir.
            // KANIT IHTIYACI: client eski DLL ile teste devam edip "duzelmedi"
            // diyebiliyor (kafa/door eski kod). Etiket her tur degistirilir.
            SmpLog.Print("[MSCSteamMP] build TAG-DOORJOB-2 (door job surucu + vehicle sel + retry)");
            SmpLog.Print("log destinations: mod=" + SmpLog.Path
                + " | game+mscloader=" + GameLogCapture.Describe());

            // Steam must be running; if it is not, the mod stays dormant and logs once.
            SteamTransport.Init();
            // Without this subscription nothing would be routed: SteamTransport handles the
            // protocol level messages itself and hands the rest here.
            SteamTransport.OnMessage = Dispatch;
        }

        void Update()
        {
            // Kare başlangıcı: toplam modül maliyeti için.
            frameStartTicks = PerfHud.Now();

            // F8: performans paneli (300 FPS ama kasılıyor hissinin ölçümü).
            if (PerfHud.TogglePressed()) PerfHud.Toggle();
            PerfHud.Sample();

            // Log motoru: dosya tamponunu boşaltır, sessizleşmiş tekrarları
            // "xN" olarak özetler, ayna kuyruğunu gönderir. Kare başına TEK
            // yazma sistem çağrısı demek; önceden her satır kendi çağrısını
            // yapıyordu.
            Guard("LogPump", A_LogPump);

            // HER MODUL AYRI AYRI KORUNUR.
            //
            // Neden: output_log.txt kaniti. LogConsole.ReadKeys -> MaxScroll ->
            // HeightOf zinciri bir noktada NullReferenceException atti; istisna
            // Update'den disari tasip Unity tarafindan yakalandi ve Update'in
            // GERISI CALISMADI. MenuController.Tick hic cagrilmadigi icin menu
            // donuyordu ve "PLAY ONLINE tikliyorum bir sey olmuyor" belirtisi
            // vardi. Tek bir modulun hatasi artik digerlerini etkilemez.
            Guard("SteamTransport", A_SteamTransport);
            Guard("AutoLoad", A_AutoLoad);

            // Satsuma gonderimi tek noktadan: 100ms rate-limit + batching + echo-guard
            // (SatsumaCoop.Tick). Registry.Update ile birlikte CALISTIRMAYIN:
            // ikisi de flush eder ve ayni degisiklik iki kez gider.
            Guard("SatsumaCoop", A_SatsumaCoop);

            if (Time.time >= nextVehicleSend) {
                nextVehicleSend = Time.time + VEHICLE_SEND_INTERVAL;
                Guard("Vehicles", A_Vehicles);
            }

            // Oyunun kendi ESC menüsü (SETTINGS MENU) açıldığında fare
            // imlecini düzeltir: menü görünür ama imleç görünmüyordu.
            Guard("GameMenu", A_GameMenu);

            // Oyunun imleç isteğini gerçek Cursor'a uygula ve kısa süre
            // tazele. ESC menüsü açıldığında "imleci göster" diyen action
            // burada yakalanıyor (Core/CursorGuard).
            Guard("Cursor", A_Cursor);

            // Parça dünya transform'u: client'a parçaların araçta
            // durmasını sağlar ve elde tutulan parçayı karşı tarafa
            // gösterir. 20 Hz, paketler toplu gider.
            Guard("PartTransform", A_PartTransform);

            // Garajdaki boş parça / eşya: yetki (kim tutuyor) + konum
            // senkronu, yerelde interpolasyon.
            Guard("GarageEntities", A_GarageEntities);

            // Dunya nesneleri (kapi/musluk/dus) ve radyo-CD: yalnizca
            // SteamTransport.HasRemote oldugunda calisir, tek yonlu gonderir.
            Guard("WorldObjects", A_WorldObjects);
            // Gunes/saat/hava: gonderim host'ta, alim client'ta.
            // Yavas veri oldugu icin EnvSync.Tick kendi 0.5 sn
            // oranini kendi tutar (ayri zamanlayici yok).
            Guard("EnvSync", A_Env);
            Guard("Fluids", A_Fluids);
            Guard("Interior", A_Interior);   // TA-01
            Guard("Radio", A_Radio);

            // CD SES AKIŞI: karşı tarafta .ogg yoksa çalan parça PCM ile
            // aktarılır. Gönderen 22050 Hz mono dilimler yollar, alan taraf
            // AudioClip.Create + SetData ile aynı sesi üretir (350 ms jitter
            // buffer → ping/lag birikmez). Ayrı modül: hata burada diğer
            // modülleri ETKİLEMEZ (bkz. yukarıdaki Guard açıklaması).
            Guard("CdAudio", A_CdAudio);
			Guard("CdPlayer", A_CdPlayer);

            // AKÜ DOLDURMA: Satsuma'nın aküsü hiç bitmez. `Electrics` FSM'i
            // `Battery`/`db_Battery` float'ını HER KARE FloatOperator +
            // FloatClamp ile düşürüyor; değişken senkronu bunu çözemez
            // (FSM çıktısı yeniden hesaplanır). Biz her kare 1.0 yazıyoruz.
            // Yerel işlem — paket gönderilmez.
            Guard("BatteryKeepFull", A_BatteryKeepFull);

            // DASHBOARD ENVANTERİ: araç dashboard'undaki tüm montaj
            // parçalarını tarar (Assembly/Use/Knob + Installed bool),
            // hangilerinin kurulu olduğunu raporlar ve kurulu olanların
            // bool'unu FSM'in yeniden hesaplamasına rağmen sabit tutar.
            Guard("Dashboard", A_Dashboard);

            // SATIR OVERRIDE: anahtarlar her zaman 1, aciklik/kirlilik/idrar/
            // stres her zaman 0. Kullanıcı isteği üzerine; her kare zorlanır.
            Guard("StatOverride", A_StatOverride);

            // ÖZEL İSTASYON: Rot 1085-1095 aralığına gelince özel dosya çalar.
            Guard("CustomRadio", A_CustomRadio);

            // SATSUMA multi-FSM inspector: tüm durum değişimleri loglanır.
            Guard("SatsumaFsmProbe", A_SatsumaFsmProbe);
            // Ekonomi: para, Teimo stogu, yakıt pompaları.
            Guard("Economy", A_EconomySync);
            // İşler: kilju, odun, tamirhane.
            Guard("Jobs", A_JobSync);

            // Yerel oyuncunun durumu (oturma/egilme/olum): yalnizca
            // DEGISTIGINDE gonderir, 20 Hz PlayerPos'a dahil degil.
            Guard("PlayerState", A_PlayerState);

            // Godmode: olum FSM'ini devre disi birakir, Game Over ekranini
            // bastirir ve haritadan dusmeyi onlar. GodMode.Tick() bunun
            // TAMAMINI yapiyor.
            //
            // v15 NOTU: once burada ikinci bir slot ("GodModeDeath") vardi ve
            // o slot GodMode.Discover() cagiryordu. Discover kare basina
            // calistirilirsa sahne yuklenmeden once "death FSM not found"
            // uyarisini her kare basar -> canli logda 4479 satir (%58) spam.
            // Kesfi yalnizca sahne yukleme/reset noktalarinda yapilir
            // (asagida GodMode.Discover ve ResetSafety cagrilari).
            Guard("GodMode", A_GodMode);
            // Discord Rich Presence: durum metni uretilir, gonderim kendi
            // is parcaciginda olur (ana is parcigi ASLA bloklanmaz).
            Guard("Discord", A_Discord);
            // Arı/Wasp'leri ayrı engelleyici ile sustur (FSM + nesne).
            Guard("WaspBlocker", A_WaspBlocker);
            // Otomatik muzik importu: CD1/CD2/CD3 klasorlerindeki .ogg
            // dosyalari. Ana menusundeki IMPORT butonunu beklemeden her
            // oyun acilisinda yukler.
            Guard("CdImport", A_CdImport);

            // Uzak oyuncunun konumu: 20 Hz UNRELIABLE gonderim, gelen paketler
            // ise her birinde uygulanir.
            Guard("RemotePlayer", A_RemotePlayer);

            // Log konsolu: F3, komut satiri, kaydirma. Once cagrilir cunku
            // MenuController onun BlocksInput degerini okur.
            Guard("LogConsole", delegate { console.Tick(Time.deltaTime); });

            // Menu: sahne izleme, metin girdisi ve oyunun kendi menu
            // nesnelerini gizleme/geri getirme.
            bool blocks = console.BlocksInput;
            Guard("Menu", delegate { MenuController.Tick(blocks); });

            // Yeni modullerin TEK giriş noktasi. Tek bir Guard icinde birlesir:
            // ayri ayri Guard'lar koyulsaydi ve iclerinden biri cokse
            // digerleri calismazdi, yani tek bir hata tum yeni sistemleri
            // sustururdu.
            Guard("ModIntegration", A_ModIntegration);
            Guard("SaveTransfer", A_SaveTransfer);

            // Hook tabanli moduller: tekerlek 20 Hz, esyalar kuyruktan,
            // saat/hava 1 Hz.
            Guard("PlayerMotion", A_PlayerMotion);
            Guard("WheelSync", A_WheelSync);
            Guard("ItemSync", A_ItemSync);
            Guard("HeldItem", A_HeldItem);
            Guard("WorldSync", A_WorldSync);

            // Interaction subsystems: carry, parts, bolts, authority, JIP, recovery
            // Kapsam acik: bu modulun urettigi her satir "interaction/" ile
            // isaretlenir, yani 'smp log find interaction' ile tum etkilesim
            // trafigi tek listede gorunur.
            Guard("Interaction", delegate {
                using (LogScope.Push("interaction")) InteractionManager.Update();
            });

            if (Time.time >= nextWorldCheck) {
                nextWorldCheck = Time.time + WORLD_CHECK_INTERVAL;
                Guard("CheckWorld", delegate { CheckWorld(); });
            }

            // Toplam modül maliyeti (kare başına bir kez).
            PerfHud.FrameTotal((float)((PerfHud.Now() - frameStartTicks)
                / System.Diagnostics.Stopwatch.Frequency * 1000.0));
        }

        /// <summary>
        /// Bir modulun istisnasini izole eder.
        ///
        /// Unity MonoBehaviour.Update icindeki istisna metodu yarida keser ve
        /// sonraki satirlar hic calismaz. Bu yuzden her modul kendi
        /// try/catch icinde: biri cokse digerleri calismaya devam eder.
        ///
        /// Ayni modul icin ikinci kez hata olursa bir daha yazmaz (log
        /// kirlilmasin), ama sayac artar; 'smp status' bunu gosterir.
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<string, int> moduleErrors =
            new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);

        static void Guard(string name, System.Action action) {
            long t0 = PerfHud.Now();
            try { action(); }
            catch (System.Exception e) {
                int n = 0;
                moduleErrors.TryGetValue(name, out n);
                n++;
                moduleErrors[name] = n;
                if (n <= 2) {
                    SmpLog.Error("module '" + name + "' failed: "
                        + e.GetType().Name + ": " + e.Message);
                }
                if (n == 3) {
                    SmpLog.Error("module '" + name + "' still failing; further errors are counted only.");
                }
            }
            finally {
                // Kronometre burada: istisna yutucusunun İÇİNDE, yani
                // hata durumunda da modül süresi doğru ölçülür.
                PerfHud.Mark(name, t0);
            }
        }

        // ------------------------------------------------------------------
        // Modül eylemleri — ÖNCEDEN AYRILMIŞ DELEGATELER
        // ------------------------------------------------------------------
        //
        // PERFORMANS: Update() her karede Guard("X", delegate { X(); }) yazıyordu.
        // Bu her kare 15 adet KAPALI DELEGATE tahsisi demek. 300 FPS'te saniyede
        // 4500 kısa ömürlü nesne -> Unity her collections fazında duruyor ->
        // "300 FPS ama kasılıyor" hissinin bir parçası. Delegates tek sefer
        // yaratılıp saklanıyor; kare başına tahsis sıfır.

        static readonly System.Action A_SteamTransport = delegate { SteamTransport.Update(); };
        static readonly System.Action A_LogPump = delegate { SmpLog.Pump(); };
        static readonly System.Action A_AutoLoad = delegate { AutoLoad.Update(); };
        static readonly System.Action A_SatsumaCoop = delegate { SatsumaCoop.Tick(); };
        static readonly System.Action A_Vehicles = delegate { VehicleRegistry.SendTransforms(); };
        static readonly System.Action A_GameMenu = delegate { GameMenuGuard.Tick(); };
        static readonly System.Action A_Cursor = delegate { CursorGuard.Tick(); };
        static readonly System.Action A_PartTransform = delegate { SatsumaPartTransform.Tick(); };
        static readonly System.Action A_GarageEntities = delegate { GarageEntitySync.Tick(); };
        /// <summary>EnvSync.Tick: gunes/saat/hava (2 Hz; host gonderir).</summary>
        static readonly System.Action A_Env = delegate { EnvSync.Tick(); };

        static readonly System.Action A_WorldObjects = delegate { WorldObjectSync.Tick(); };
        static readonly System.Action A_Fluids = delegate { FluidWearSync.Tick(); };
        // TA-01: kanca / kemer kilidi / sinyal bayraklari.
        // Bunlar SATSUMA parca tablosunda degil (Assembly/Removal
        // FSM'i olmayan nesneler), ayri bir kanalla tasiniyor.
        static readonly System.Action A_Interior = delegate { InteriorStateSync.Tick(); };
        static readonly System.Action A_Radio = delegate { RadioSync.Tick(); };
        static readonly System.Action A_CdAudio = delegate { CdAudioStream.Tick(); };
		// CD player gorunurlugu + CD/sarki durumu (CdPlayerOK, RadioOK,
		// trigger_cd.Setup, trigger_disc.CDin, Path, MaxTracks).
		static readonly System.Action A_CdPlayer = delegate { CdPlayerSync.Tick(); };
        static readonly System.Action A_BatteryKeepFull = delegate { BatteryKeepFull.Tick(); };
        static readonly System.Action A_Dashboard = delegate { DashboardPartSync.Tick(); };
        static readonly System.Action A_StatOverride = delegate { PlayerNeedsSync.ForceLocalValues(); };
        static readonly System.Action A_CustomRadio = delegate { CustomRadio.Tick(); };
        static readonly System.Action A_SatsumaFsmProbe = delegate { SatsumaFsmProbe.Tick(); };
        static readonly System.Action A_PlayerState = delegate { LocalPlayerState.Tick(); };
        static readonly System.Action A_CdImport = delegate { CdMusicImport.Tick(); };
        static readonly System.Action A_GodMode = delegate { GodMode.Tick(); };
        static readonly System.Action A_Discord = delegate { DiscordPresence.Tick(); };
        static readonly System.Action A_WaspBlocker = delegate { WaspBlocker.Tick(); };
        static readonly System.Action A_RemotePlayer = delegate { RemotePlayerSync.Update(); };
        static readonly System.Action A_PlayerMotion = delegate { PlayerMotion.Update(); };
        static readonly System.Action A_WheelSync = delegate { WheelSync.Update(); };
        static readonly System.Action A_ItemSync = delegate { ItemSync.Update(); };
        static readonly System.Action A_HeldItem = delegate { HeldItemSync.Update(); };
        static readonly System.Action A_WorldSync = delegate { WorldSync.Update(); };
        static readonly System.Action A_EconomySync = delegate { EconomySync.Tick(); };
        static readonly System.Action A_JobSync = delegate { JobSync.Tick(); };
        static readonly System.Action A_ModIntegration = delegate { ModIntegration.Update(); };
        static readonly System.Action A_SaveTransfer = delegate { SaveTransfer.Tick(); };

        /// <summary>Update'in toplam modül maliyetini ölçmek için kare başlangıcı.</summary>
        static long frameStartTicks;

        void OnGUI()
        {
            // OnGUI de izole: bir panelin hatasi digerini gizlemesin.
            try { MenuController.Draw(); }
            catch (System.Exception e) {
                if (!menuDrawFailed) {
                    menuDrawFailed = true;
                    SmpLog.Error("menu draw failed: " + e.GetType().Name + ": " + e.Message);
                }
            }

            try { if (panel != null) panel.Draw(); }
            catch { }

            // Performans paneli (F8 / 'smp perf). Sol üstte: ekranın geri
            // kalanı oyun içi kalır, teşhis paneli kullanıcıyı kapatmaz.
            try { PerfHud.Draw(); }
            catch { }

            // The log window is drawn last so it sits on top of the menu and of the
            // connection panel: it is the thing you open when something is wrong.
            try { console.Draw(); }
            catch { }
        }

        /// <summary>Menü çiziminde bir hata oldu mu (bir kez loglanır)?</summary>
        bool menuDrawFailed;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            // Oturum sonu: kalan satırlar boşaltılır, özet (satır sayısı,
            // kanal sayaçları, metrikler, katlananlar) dosyaya yazılır, dosya
            // kapatılır. Önceden yalnızca Flush vardı; dosyada "bu oturumda ne
            // oldu" özeti hiç kalmıyordu.
            SmpLog.Close();
            Protocol.NetStats.Clear();
            GameLogCapture.Close();
        }

        /// <summary>Unity kapanırken son çağrı (otomatik çalışmayan platformlar için).</summary>
        void OnApplicationQuit()
        {
            try { SmpLog.Close(); } catch { }
            try { GameLogCapture.Close(); } catch { }
        }

        // ------------------------------------------------------------------
        // World lifetime
        // ------------------------------------------------------------------

        /// <summary>Called from the MSCLoader PostLoad hook, and self-healing every second.</summary>
        public void OnWorldReady()
        {
            if (worldInstalled) return;
            worldInstalled = true;

            // Yeni dünya = eski uyarı listesi geçersiz. TARAMA ÖNCESİ
            // temizlenir; temizlik sonda olursa biriken mesajlar silinir.
            FsmVars.ClearWarnings();

            SatsumaRegistry.Install();
            VehicleRegistry.Install();
            // Discovery-based modules: what the world contains is not knowable from the
            // assemblies, so it is scanned once per world, in the same place as the
            // Satsuma table.
            WorldSync.Discover();
            ItemSync.Discover();
            // Elde tutulan eşya: iskelet düğümleri bu dünyada fotoğraflanır.
            HeldItemSync.Discover();
            // Kapi / musluk / dus ve radyo-CD: asset'ten cikarilmis isimlerle
            // (DoorFront, GarageDoors, KitchenWaterTap, CDPlayer, RadioTuner ...)
            // PlayMaker FSM degiskenlerine baglanir.
            WorldObjectSync.Discover();
            // Gunes/saat/hava. Dumper kaniti: MAP/SUN/Pivot/SUN
            // (Clock: Hours/Minutes/Time/TimeScale) + (Color:
            // WorldDay/WorldTime/Weather/ClockTable, durumlar
            // "Check time save"/Sunrise/Sunset/Night/RainColorDay).
            try { EnvSync.Discover(); } catch { }
            FluidWearSync.Discover();
            // TA-01: kanca / kemer kilidi / sinyal bayraklari.
            // Bunlar SATSUMA parca tablosunda DEGIL (Assembly/Removal
            // FSM'i olmayan nesneler), ayri bir kanalla tasiniyor.
            InteriorStateSync.Discover();
            RadioSync.Discover();
            // CD PLAYER gorunurlugu: Database/PartsStatus/Radio[Status]
            // (CDPlayerOK/RadioOK -> ActivateGameObject(ButtonsCD/ButtonsRadio)),
            // trigger_cd[Assembly].Setup, trigger_disc[Assembly].CDin ve
            // Radio/CD[Playlist].Path + CDn[LoadSongs].MaxTracks.
            try { CdPlayerSync.Discover(); } catch { }
            // Otomatik muzik importu. ANA MENUDE calisir (level0 Radio/CD
            // nesnesi), bu yuzden dunya hazirligindan sonra bir kez
            // daha tetiklenir; ana menusune donuldugunde de kendi
            // zamanlayicisi devralir.
            try { CdMusicImport.Discover(); } catch { }
            // Ekonomi: para (global), Teimo, yakıt pompaları.
            try { EconomySync.Discover(); } catch { }
            // İşler: kilju, odun, tamirhane.
            try { JobSync.Discover(); } catch { }
            // Olum / yeniden dogma: "Activate Dead Body" FSM'i ve Game Over
            // ekrani bu dunyada. Bulunamazsa mod sessizce devre disi kalir
            // ve permadeath normal calisir (SmpLog uyarir).
            try { GodMode.Discover(); } catch { }
            // Garajdaki taşınabilir nesneler (parça, alet, eşya): yetki ve
            // konum senkronu burada. ItemSync olay bazlıdır (alma/bırakma),
            // bu ise sürekli fizik konumu takip eder.
            GarageEntitySync.Discover();
			// Eylem günlüğü: komut gerekmez, her oturumda kendini duyurur.
			SmpTrace.Announce();
            // Yeni moduller dunyaya baglanir: AI arac kesfi, oyuncu ihtiyaclari,
            // sohbet, HUD, host kopma tespiti. SatsumaCoop.Tick() zaten
            // Update icinden calisiyor, orada bir daha cagrilmaz.
            //
            // ARASI ONEMLI: bu, VehicleRegistry.Install() SONRASINDA gelir;
            // AI arac kestifinin "bu arac zaten VehicleRegistry'de mi?"
            // sorusunu cevaplamasi icin tablonun dolu olmasi gerekir.
            try { ModIntegration.OnWorldReady(); }
            catch (Exception e) {
                SmpLog.Error("ModIntegration.OnWorldReady failed: "
                    + e.GetType().Name + ": " + e.Message);
            }
            // Uzak oyuncunun govdesi: once konum kaynagini bul (CharacterController
            // varsa o, yoksa kamera) ve onceki dunyadan kalmis bir kukla kalmasin.
            // PlayerMotion.TrackFromCamera daha once hic cagrilmamiyordu, yani
            // Position hep (0,0,0) kalirdi.
            RemotePlayerSync.Reset();
            SmpLog.Print(RemotePlayerSync.Describe());
            // Egilme kalibrasyonu da dunya ile baslar: menudeki kamera
            // yuksekligi referansi zehirler, kukla cokelek gorunurdu.
            try { LocalPlayerState.Reset(); } catch { }
            // A client that loaded its save after the handshake still has to ask again.
            SatsumaRegistry.OnWorldReady();
            WorldSync.SendAll();
            WorldObjectSync.SendAll();
            FluidWearSync.SendAll();
            // TA-01: JIP/ilk senkron -- tum bayraklarin baslangic
            // degerini gonder.
            InteriorStateSync.SendAll();
            RadioSync.SendAll();
            // Interaction.FSMEventHook ve Interaction.WorldSync KALDIRILDI.
            //
            // İkisi de WorldObjectSync'in (kapı/anahtar/kontak) YANINDA ikinci
            // bir kanal kuruyordu ve ikisi de bozuktu:
            //
            //  1) FSMEventHook.Update() `_fsmCache`'i geziyordu, ama o kova
            //     HİÇ doldurulmuyordu (ScanAndHook yalnız `_hookedFsmKeys`
            //     ekliyor, AddSyncAction boş bir metod). Yani olay zinciri
            //     HİÇ tetiklenmiyordu.
            //  2) Tesadüfen dolarsa gönderdiği gövdeler yanlıştı:
            //       SendSwitchState -> Msg.ObjectState'a (string,bool) yazıyordu;
            //       oysa WorldObjectSync.Flush() (byte count, path, boolCount,
            //       name,val..., floatCount, name,val..., ping) yazıyor.
            //       Yani şalter paketi ObjectState akışını KAYDIRIRDI ve
            //       sonraki her float/bool kaydı yanlış nesneye düşerdi.
            //       SendDoorState -> Msg.DoorEvent'e (string,bool);
            //       okuyan taraf (path, byte action, ulong, uint) bekliyor.
            //       SendEngineState -> Msg.Engine'e (string,bool).
            //
            // Canlı hatlar duruyor:
            //   kapı    : ObjectState değişken aynası + TickDoorDrives işi
            //   şalter  : Msg.CtrlState ("Wait player <FSM> <OLAY>")
            //   kontak  : STATE_MIRROR_FSMS -> Msg.CtrlState
            //   vida    : Satsuma/BoltSyncPatch (FsmInt/FsmFloat Postfix)
            //   ortam   : EnvSync -> Msg.EnvClock(79)
            //
            // InteractionManager.Bolts.OnLocalBoltAction yolunu da
            // FSMEventHook'tan basan bu yüzden artık çağıran yok; BoltSync'in
            // FsmFloat.set_Value Postfix'i aynı işi yapıyor ve DOĞRU yolu
            // kullanıyor (SatsumaRegistry.OnBoltChanged -> sessizlik + hız
            // + MIN_SEND_INTERVAL, yani ara tween değerleri gitmiyor).
            // Tüm keşifler bitti: biriken "FSM değişkeni yok" uyarılarını
            // 238 satır yerine TEK özet olarak bas. (Log kanıtı: 12 oturumda
            // 2856 satır; tamamı aynı 238 mesajın kopyasıydı.)
            FsmVars.FlushSummary();
        }

        /// <summary>Called before a save load and when the world went away.</summary>
        public void OnWorldUnloading()
        {
            if (!worldInstalled) {
                SatsumaRegistry.Clear();
                return;
            }
            worldInstalled = false;
            SmpLog.Print("dunyadan cikiliyor: tablolar birakiliyor.");
            // EchoGuard first: any scope left open by a half-applied packet must not
            // silence capture in the next world.
            EchoGuard.Reset();
            VehicleRegistry.Rescan();   // destroys the SyncedVehicle components, finds none here
            // Dropped with the scene: every one of these holds scene object references.
            // F07_ROOT_CACHE: kok nesne onbellegi sahne nesnesi tutar;
            // temizlenmezse yeni sahne eski koklerle cozulmeye calisir.
            try { SceneDump.Clear(); } catch { }
            PlayerMotion.Clear();
            WheelSync.Clear();
            ItemSync.Clear();
            HeldItemSync.Clear();
            WorldSync.Clear();
            LocalPlayerState.Reset();
            GameMenuGuard.Reset();
            // Olum referanslari da sahne nesnesi; birakilirsa Destroy edilmis
            // nesne karsilastirmasi null'a esit oldugu icin FSM bulunamaz.
            try { GodMode.KeepScreenHidden(); } catch { }
            try { GodMode.ResetSafety(); } catch { }
            SatsumaPartTransform.Clear();
            GarageEntitySync.Clear();
            WorldObjectSync.Clear();
            EnvSync.Clear();
            FluidWearSync.Clear();
            InteriorStateSync.Clear();   // TA-01
            RadioSync.Clear();
            // Ekonomi ve işler de sahne referansları tutar.
            try { EconomySync.Reset(); } catch { }
            try { JobSync.Reset(); } catch { }
            // Yeni moduller de sahne referansi tutuyor (eşya kuklalari, AI arac
            // gölgeleri, sohbet/HUD nesneleri).
            try { ModIntegration.OnSceneChange(); } catch { }
            // Kukla sahne nesnesidir; sahne zaten yok ediyor ama referansi birakmamak
            // icin burada birakiliyor (yok edilmis Unity nesnesi null'a esittir).
            RemotePlayerSync.Clear();
        }

        /// <summary>
        /// The scene can change without MSCLoader telling us (returning to the menu, a
        /// second Continue). A destroyed Unity object compares equal to null, so a Root that
        /// has been thrown away by the scene unload shows up here as null.
        /// </summary>
        void CheckWorld()
        {
            bool alive = SatsumaRegistry.Root != null;
            if (alive && !worldInstalled) {
                OnWorldReady();
                return;
            }
            if (!alive && worldInstalled) OnWorldUnloading();

            // Müzik importu ANA MENÜDE yaşar (level0 `Radio/CD`), dünyada
            // değil. Dünya kapanınca menüye dönüldüğü için keşfi orada
            // yenilemek gerekir; aksi halde "eski sahne nesnesi" kalır ve
            // IMPORT gönderilmez. Yalnızca keşif bozuksa tetiklenir.
            if (!alive && !CdMusicImport.Found) CdMusicImport.Discover();
        }

        // ------------------------------------------------------------------
        // Inbound routing
        // ------------------------------------------------------------------

        /// <summary>
        /// SteamTransport hands us everything that is not handshake/heartbeat/disconnect.
        /// Vehicle messages carry a vehicle id and belong to VehicleRegistry; player messages
        /// belong to RemotePlayerSync; the rest is Satsuma state.
        /// </summary>
        void Dispatch(Protocol.Msg id, CSteamID sender, BinaryReader r)
        {
            // SAYIM: ALINAN her paket once buradan gecer. Gonderim
            // Protocol.Finish'te sayiliyor; ikisi birlikte 'smp netstat'
            // tablosunu olusturur.
            //
            // ONEMLI: byte sayisi BURADA hesaplanir cunku r.BaseStream
            // Position her modul okudukça ilerler. Konum, okuma oncesi
            // alinir.
            try {
                Protocol.AssertBytesRemaining(r, 4, "packet length");
                int pktLen = (int)(r.BaseStream.Length - r.BaseStream.Position);
                Protocol.NetStats.CountRecv(id, pktLen);
            } catch { }
            try {
                // OYUNCU MESAJLARI BURADA. Protocol.IsVehicleOrPlayer() PlayerPos
                // ve PlayerInfo'yu da true sayiyor, yani aşağıdaki blok onları
                // VehicleRegistry'ye yollardi. Oyuncu konumu arac degil; bu yuzden
                // once burada yakalaniyor.
                if (id == Protocol.Msg.PlayerPos) {
                    RemotePlayerSync.OnMessage(r);
                    return;
                }
                if (id == Protocol.Msg.PlayerInfo) {
                    RemotePlayerSync.OnInfo(r);
                    return;
                }

                if (Protocol.IsVehicleOrPlayer(id)) {
                    switch (id) {
                        case Protocol.Msg.WheelState:
                            WheelSync.OnMessage(r);
                            return;
                        default:
                            VehicleRegistry.OnMessage(id, sender, r);
                            return;
                    }
                }

                // Yeni modullerin mesajlari. AYRI switch case'leriyle degil, tek
                // bir kontrol: bu blok "bilinmeyen mesaj" olarak
                // yorumlanip dusmesin diye butun yeni mesaj tiplerini
                // kapsar ve iclerinden biri true donerse islenmistir.
                if (ModIntegration.TryHandle(id, r))
                    return;

                // Items and the clock are not vehicle traffic but they also are not
                // Satsuma parts; they get their own handlers below.

                switch (id) {
                    case Protocol.Msg.BoltBatch: {
                        byte count = r.ReadByte();
                        Telemetry.ReceivedBoltSyncs++;
                        SatsumaRegistry.ApplyBoltBatch(r, count);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("BoltBatch stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.PartBatch: {
                        byte count = r.ReadByte();
                        Telemetry.ReceivedBatches++;
                        SatsumaRegistry.ApplyPartBatch(r, count);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("PartBatch stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.DatabaseBatch: {
                        // `Database/*` part-flag page (v20). Layout is
                        // (id, installed, bolted, damaged) -- FOUR fields, no slot.
                        // Read order is the exact inverse of
                        // NetworkPacketManager.WriteDatabaseBatch:
                        //   w.Write(Id) / w.Write(InstalledOf) /
                        //   w.Write(IsGroupBolted) / w.Write(DamagedValue)
                        // apply      -> r.ReadString() / ReadBoolean x3
                        //
                        // A Database row has no mount slot, so slot = 0 goes to
                        // ApplyPartState. That is NOT a no-op inside it --
                        // ApplySlot(part, installed, 0) clears every slot of a
                        // HasSlots part -- so the value is safe only because a
                        // Database row is never a slotted mount trigger (see
                        // WriteDatabaseBatch: the filter is the Database marker,
                        // not HasSlots). Same assumption WriteStorePartBatch
                        // already makes for its slot byte.
                        byte dbCount = r.ReadByte();
                        Telemetry.ReceivedBatches++;
                        int dbRead = 0;
                        for (int i = 0; i < dbCount; i++) {
                            // Same early-out style as ApplyBoltBatch: the record
                            // floor here is 1 (7-bit length) + 0 (empty id) + 3
                            // bools = 4 bytes. Less than that means the stream is
                            // already desynchronised and reading on would throw
                            // on a random string length.
                            if (r.BaseStream.Position >= r.BaseStream.Length) {
                                SmpLog.Warning("database batch ended early at record "
                                    + i + "/" + dbCount + " ("
                                    + (r.BaseStream.Length - r.BaseStream.Position)
                                    + " byte(s) left).");
                                break;
                            }
                            string dbId = r.ReadString();
                            bool dbInstalled = r.ReadBoolean();
                            // The two trailing bools are read defensively, in the
                            // ApplyPartBatch style: an older or shorter peer stops
                            // writing right before them and the flag is left at the
                            // default rather than throwing out of the switch.
                            bool dbBolted = true;
                            try {
                                if (r.BaseStream.Position < r.BaseStream.Length) dbBolted = r.ReadBoolean();
                            }
                            catch (System.IO.EndOfStreamException) { dbBolted = true; }
                            bool dbDamaged = false;
                            try {
                                if (r.BaseStream.Position < r.BaseStream.Length) dbDamaged = r.ReadBoolean();
                            }
                            catch (System.IO.EndOfStreamException) { dbDamaged = false; }
                            // F09_VERBOSE_GUARD: birlestirme yalniz
                            // `Verbose` acikken. `Telemetry.Log` yalniz
                            // `Verbose` oldugunda SmpLog.Log yapsa da
                            // C# argumenteyi ONCE degerlendirir; 225
                            // kayit x 4 birlestirme her zaman oluyordu.
                            if (Telemetry.Verbose) {
                                Telemetry.Log("database part " + dbId + "=" + dbInstalled
                                    + " bolted=" + dbBolted + " damaged=" + dbDamaged);
                            } else {
                                // `LastReceived` yine guncellenir.
                                Telemetry.LastReceived = dbId;
                            }
                            SatsumaRegistry.ApplyDatabasePartState(dbId, dbInstalled, dbBolted, dbDamaged);
                            dbRead++;
                        }
                        if (dbRead != dbCount && Telemetry.Verbose)
                            SmpLog.Warning("DatabaseBatch read " + dbRead + "/" + dbCount + " record(s)");
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("DatabaseBatch stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.PartEvent: {
                        string partId = r.ReadString();
                        string fsm = r.ReadString();
                        string ev = r.ReadString();
                        Telemetry.ReceivedPartEvents++;
                        // F09_VERBOSE_GUARD: birlestirme yalniz `Verbose`
                        // acikken; `LastReceived` her zaman guncellenir.
                        if (Telemetry.Verbose) {
                            Telemetry.Log("part event " + partId + " " + fsm + "." + ev);
                        } else {
                            Telemetry.LastReceived = partId;
                        }
                        SatsumaRegistry.ApplyPartEvent(partId, fsm, ev);
                        break;
                    }
                    case Protocol.Msg.PartState: {
                        string partId = r.ReadString();
                        bool installed = r.ReadBoolean();
                        // v15: kayitta `bolted` de var. Geriye donuk
                        // uyum icin akis bitmisse varsayilan true.
                        bool pBolted = true;
                        try {
                            if (r.BaseStream.Position < r.BaseStream.Length) pBolted = r.ReadBoolean();
                        }
                        catch (System.IO.EndOfStreamException) { pBolted = true; }
                        // v16 (b): dolu yuvanin indeksi.
                        byte pSlot = 0;
                        try {
                            if (r.BaseStream.Position < r.BaseStream.Length) pSlot = r.ReadByte();
                        }
                        catch (System.IO.EndOfStreamException) { pSlot = 0; }
                        // v18 (a): parca hasari.
                        bool pDamaged = false;
                        try {
                            if (r.BaseStream.Position < r.BaseStream.Length) pDamaged = r.ReadBoolean();
                        }
                        catch (System.IO.EndOfStreamException) { pDamaged = false; }
                        Telemetry.ReceivedPartStates++;
                        // F09_VERBOSE_GUARD: birlestirme yalniz `Verbose` acikken.
                        if (Telemetry.Verbose) {
                            Telemetry.Log("part state " + partId + "=" + installed
                        + " bolted=" + pBolted);
                        } else {
                            Telemetry.LastReceived = partId;
                        }
                        SatsumaRegistry.ApplyPartState(partId, installed, pBolted, pSlot, pDamaged);
                        break;
                    }
                    case Protocol.Msg.PartTransform: {
                        // Parçanın dünya konumu. "client'ta parçalar araçta
                        // değil, garajda duruyor" probleminin çözümü: konum
                        // doğrudan kopyalanır, FSM'e güvenilmez.
                        byte tcount = r.ReadByte();
                        SatsumaPartTransform.OnMessage(r, tcount);
                        break;
                    }
                    case Protocol.Msg.EntityState: {
                        // Garajdaki boş parça / eşya: konum + kim tutuyor.
                        byte ecount = r.ReadByte();
                        GarageEntitySync.OnMessage(r, ecount);
                        break;
                    }
                    case Protocol.Msg.SyncRequest: {
                        // Payload is the requester's own table, logged so a mismatch with the
                        // host is visible in the log rather than only guessed at.
                        uint hash = r.ReadUInt32();
                        int parts = r.ReadInt32();
                        int bolts = r.ReadInt32();
                        SmpLog.Log("sync requested by peer: " + parts + " part(s), "
							+ bolts + " bolt(s), hash 0x" + hash.ToString("x8"));
                        SatsumaRegistry.OnSyncRequested();
                        break;
                    }
                    case Protocol.Msg.SyncDone: {
                        int parts = r.ReadInt32();
                        int bolts = r.ReadInt32();
                        uint hash = r.ReadUInt32();
                        SatsumaRegistry.ApplySyncDone(parts, bolts, hash);
                        // Stream should be fully consumed
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("SyncDone stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.ItemEvent: {
                        ItemSync.OnMessage(r);
                        break;
                    }
                    case Protocol.Msg.HeldItem: {
                        // Elde tutulan eşya: kuklanın eline parent edilir.
                        HeldItemSync.OnMessage(r);
                        break;
                    }
                    case Protocol.Msg.PlayerState: {
                        // Oturma / egilme / olum bayraklari.
                        LocalPlayerState.OnMessage(r);
                        break;
                    }
                    case Protocol.Msg.WorldState: {
                        WorldSync.OnMessage(r);
                        break;
                    }
                    case Protocol.Msg.ObjectState: {
                        // Musluk / dus / anahtar / ev isiklari: degisken durumu.
                        byte ocount = r.ReadByte();
                        WorldObjectSync.OnMessage(r, ocount);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("ObjectState stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.DoorEvent: {
                        // Kapi acma/kapama: FSM OLAYI, degisken degil.
                        // Bu build'de kapinin okunabilir bir durumu yok; mesh'i
                        // oyunun kendi Use FSM'i OPEN/CLOSE ile döndürüyor.
                        WorldObjectSync.OnDoorMessage(r);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("DoorEvent stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.RadioEvent: {
                        // Radyo/CD OLAYI: ses acma-kapatma, kanal degistirme,
                        // CD parca degistirme. Degisken yolundan ayri, cunku
                        // oyun sesi degiskene yazmadan (AudioSource.mute +
                        // EnableFSM) kapatiyor.
                        RadioSync.OnEventMessage(r);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("RadioEvent stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.RadioPlay: {
                        // Radyo/CD ÇALMA DURUMU. Değişkenler tek başına yetmez:
                        // karşı tarafta sesin duyulması için oyunun FSM'i
                        // yürümeli (AudioPlay). Bu paket `CurrentCD`+`Index`
                        // ve kanal bool'larını yazıp oyunun kendi PLAY CD /
                        // RADIO_UPDATE olayını yerelde yeniden üretir.
                        RadioSync.OnPlayMessage(r);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("RadioPlay stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.CdAudioHeader: {
                        // CD SES AKIŞI — BAŞLIK. Karşı tarafta .ogg yoksa
                        // çalan parça PCM olarak aktarılır; alan taraf
                        // AudioClip.Create ile aynı uzunlukta clip üretip
                        // parçaları içine yazar (350 ms jitter buffer).
                        CdAudioStream.OnHeader(r);
                        break;
                    }
                    case Protocol.Msg.CdAudioData: {
                        // CD SES AKIŞI — VERİ (güvenilmez/no-delay).
                        // Kayıp paket 80 ms sessizlik; birikmiş gecikme
                        // olmaması için bilerek güvenilmez.
                        CdAudioStream.OnData(r);
                        break;
                    }
                    case Protocol.Msg.CtrlState: {
                        // ARAÇ KONTROL DURUMU AYNASI (CD player kapağı,
                        // motor). Olay yolu yetmiyor: kapağı açan şey bir FSM
                        // DURUMU ve uzakta `Part` değişkeni çözülmediği için
                        // ASSEMBLE göndermek işe yaramıyor. Alan taraf
                        // `Fsm.SetState` ile durumu doğrudan uygular.
                        // Fare durumları ("Wait player"/"Mouse over")
                        // bilerek taşınmaz — yoksa düğmeler kilitlenirdi.
                        WorldObjectSync.OnCtrlStateMessage(r);
                        break;
                    }
                    case Protocol.Msg.CdDisc: {
                        // CD DISK (fiziksel kizak + disk nesnesi): slot yolu +
                        // disk kimligi. CtrlState durumu aynalar ama `Part`
                        // cozulemez; kimlik bu pakette gider.
                        CdDiscSync.OnMessage(r);
                        break;
                    }
                    case Protocol.Msg.EnvClock: {
                        // Gunes/saat/hava (WorldDay, WorldTime, Weather,
                        // sunX). Yalniz client'ta ise yarar; host bu
                        // paketi gondermez ama dogrusal akis icin
                        // bos gecirilir.
                        //
                        // BU HAT GERIYE DONUK: karsi taraf eski build'liyse
                        // (yalniz EnvClock gonderen) paket buradan okunur.
                        EnvSync.OnMessage(r);
                        break;
                    }
                    case Protocol.Msg.SaveGameChunk: {
                        // Kayıt dosyası parçası (host -> client). Client bunu
                        // geçici dosyaya yazar; orijinaline DOKUNMAZ (yedek +
                        // CRC32 doğrulaması bkz. SaveTransfer).
                        SaveTransfer.OnChunk(r);
                        break;
                    }
                    case Protocol.Msg.SaveGameComplete: {
                        // İki yönlü: client istemesi (ok=false, "request"),
                        // host'un kapanışı (ok=true) ve client'ın yazma sonucu.
                        SaveTransfer.OnComplete(r, (byte)Protocol.Msg.SaveGameComplete);
                        break;
                    }
                    case Protocol.Msg.EnvironmentState: {
                        // Zengin ortam durumu: worldTime, worldDay, weather,
                        // sunRotation, temperature, hostId, timestamp.
                        //
                        // BU DAL EKSIKTI: `EnvSync.OnEnvironmentStateMessage`
                        // yazilmisti ama hicbir yerden cagrilmiyordu, bu yuzden
                        // mesaj her zamanki gibi `default` dalina dusup
                        // bilinmeyen paket sayiliyordu. Artik gecerli.
                        EnvSync.OnEnvironmentStateMessage(r);
                        break;
                    }
                    case Protocol.Msg.CdCase: {
                        // CD KUTUSU (kapak + icindeki disk).
                        CdDiscSync.OnCaseMessage(r);
                        break;
                    }
                    case Protocol.Msg.CdPlayerState: {
                        // CD PLAYER DURUMU: CDPlayerOK/RadioOK (dashboard
                        // yuzeyi degisimi), trigger_cd.Setup, trigger_disc.CDin,
                        // CD listesi Path/MaxTracks.
                        CdPlayerSync.OnMessage(r);
                        break;
                    }
                    case Protocol.Msg.FluidState: {
                        // Sivi / aşınma / park yakıtı: yol + fsm + ad + float.
                        //
                        // v9: BU BLOK ARTIK TEK OKUYUCU. Önceden FluidWearSync,
                        // EconomySync ve JobSync'in üç okuyucusu da aynı paketi
                        // okumak için stream'i geri sarıyordu; ama kayıt
                        // düzenleri birbirinden farklıydı, dolayısıyla üçünden
                        // ikisi HER PAKETTE "Failed to read past end of stream"
                        // veriyordu (canlı log: ekonomi 17x, işler 17x). Ekonomi
                        // ve işler artık 71/72 numaralı kendi mesajlarında.
                        byte fcount = r.ReadByte();
                        FluidWearSync.OnMessage(r, fcount);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("FluidState stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    // TA-01 (SMP_INTERIOR_WIRE2): kanca / kemer kilidi /
                    // sinyal. Kayit: byte sayim, sonra (byte index, bool).
                    case Protocol.Msg.InteriorState: {
                        byte icount = r.ReadByte();
                        InteriorStateSync.OnMessage(r, icount);
                        break;
                    }
                    case Protocol.Msg.EconomyState: {
                        // Ekonomi: yol + fsm + ad + float. v9'da ayrıldı.
                        EconomySync.OnMessage(r);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("EconomyState stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.JobState: {
                        // İşler: yol + fsm + ad + tip + değer. v9'da ayrıldı.
                        JobSync.OnMessage(r);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("JobState stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.RadioState: {
                        // CD/radyo: track, playing, volume.
                        byte rcount = r.ReadByte();
                        RadioSync.OnMessage(r, rcount);
                        if (r.BaseStream.Position != r.BaseStream.Length && Telemetry.Verbose)
                            SmpLog.Warning("RadioState stream not fully consumed: " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes remaining");
                        break;
                    }
                    case Protocol.Msg.CarryState: {
                        // Elinde tutma/taşınma: yol, held, carrier, localPos, localRot, parentVehiclePath.
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.PartMountState: {
                        // Parça takma/sökme: partPath, installed, carrierPath, localPos, localRot, parentVehiclePath.
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.BoltBatchV2: {
                        // Vida sıkma toplu: (boltPath, stage, tightness) listesi.
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.AuthorityClaim: {
                        // Sahiplik talep/transfer.
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.FullStateSnapshot: {
                        // JIP tam durum snapshot.
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.PartRecovery: {
                        // Harita altından düşen parça kurtarma.
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.AuthoritySnapshot: {
                        // Sahiplik snapshot (JIP).
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.FireworkLight: {
                        // Havai fisek atesleme: canta yolu.
                        InteractionManager.RouteMessage(id, r);
                        break;
                    }
                    case Protocol.Msg.LogLine: {
                        // Karşı tarafın log satırı: kendi dosyamıza yazılır.
                        LogRemote.OnMessage(r);
                        break;
                    }
                    default:
                        SmpLog.Warning("unhandled message " + (int)id + ".");
                        break;
                }
            }
            catch (Exception e) {
                // A truncated or foreign packet must not take the frame down.
                Telemetry.BadPackets++;
                SmpLog.Warning("bad packet (" + id + "): " + e.Message);
            }
        }
    }
}
