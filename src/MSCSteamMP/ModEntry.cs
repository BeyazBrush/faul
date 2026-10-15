using Harmony;
using MSCLoader;
using System.Reflection;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// MSCLoader entry point.
    ///
    /// MSCLoader has no OnLoad/OnUnload overrides: hooks are registered in ModSetup with
    /// SetupFunction(Setup.X, action). This mod needs three of them:
    ///
    ///   PreLoad  - a save is about to be loaded, so any table from the previous world is stale.
    ///   PostLoad - the world and the save exist; build the Satsuma table and the vehicle list.
    ///   OnGUI    - nothing, the panel is drawn by SteamMPBehaviour's own OnGUI.
    ///
    /// Everything else (Steam callbacks, packet reading, the per-frame flush, coroutines)
    /// lives in SteamMPBehaviour, because it is the only object that can run a coroutine:
    /// SatsumaRegistry's initial sync is spread over frames and needs StartCoroutine.
    /// </summary>
    public class ModEntry : Mod
    {
        /// <summary>Shown in the panel and the log file. Must match AssemblyInfo.</summary>
        public const string MOD_VERSION = "0.1";

        /// <summary>
        /// How many methods Harmony actually patched. Printed on load and by 'smp status':
        /// if a hook silently failed to apply, this number is the first place it shows.
        /// Compare it against ExpectedPatches, which is counted from the [HarmonyPatch]
        /// attributes at load time rather than written down by hand.
        /// </summary>
        public static int PatchCount;

        /// <summary>
        /// KaÃ§ metotun yamanmasÄ± BEKLENÄ°YOR.
        ///
        /// Niteliklerden sayÄ±lÄ±r, elle yazÄ±lmaz. Daha Ã¶nce burada "10" yazÄ±yordu ve
        /// yanlÄ±ÅŸtÄ±: mod 14 hedef metot yamalÄ±yor
        ///
        ///   3  FsmBool/FsmInt/FsmFloat set_Value
        ///   1  PlayMakerFSM.SendEvent
        ///   2  Fsm.Event iki aÅŸÄ±rÄ± yÃ¼kleme
        ///   1  CharacterController.Move (prefix + postfix; __args YOK, acik
        ///      Vector3 â€” Harmony 1.2 __args'i reddediyordu)
        ///   1  SetMouseCursor.OnEnter      (CursorGuard)
        ///   3  SimpleSmoothMouseLook.Update, SmoothMouseLook.Update, MainCamera.LateUpdate
        ///
        /// Elle yazÄ±lan bir sayÄ±, yeni bir [HarmonyPatch] eklenince sessizce yanlÄ±ÅŸ
        /// kalÄ±yor ve tam olarak bu sayÄ±ya bakarak "yama uygulanmadÄ± mÄ±" sorusunu
        /// soran kiÅŸiyi yanÄ±ltÄ±yordu. Åimdi tek doÄŸruluk kaynaÄŸÄ± kendisi.
        ///
        /// TEKERLEK HOOK'LARI KALDIRILDI. WheelCollider set_motorTorque /
        /// set_steerAngle / set_brakeTorque native (extern) metotlardir, IL
        /// govdeleri yoktur; Harmony 1.2 bunlari "has no body" diye
        /// reddediyor (log kaniti). Tekerlek girdisi artik WheelSync.Update
        /// icinde getter'lardan okunuyor (polling) â€” yamaya gerek yok.
        ///
        /// Dikkat: bu, tests\PatchProbe.cs'in 11'i DEÄÄ°LDÄ°R. Probun kendi 11 test
        /// yamasÄ± vardÄ±r; o modun yamalarÄ±nÄ± saymaz.
        /// </summary>
        public static int ExpectedPatches;

        public override string ID => "MSCSteamMP";
        public override string Name => "MSC Steam Multiplayer";
        public override string Version => MOD_VERSION;
        public override string Author => "konig";
        public override Game SupportedGames => Game.MySummerCar;

        /// <summary>
        /// Harmony yamalarÄ±nÄ± tip tip uygular; bir tip Ã§Ã¶kerse diÄŸerleri yaÅŸar.
        ///
        /// SayÄ± dinamiktir (PatchAllResilient niteliklerden sayar); buradaki
        /// sabitlere gÃ¼venmeyin.
        /// Kamera yamalarÄ± (SimpleSmoothMouseLook.Update 0x06000087,
        /// SmoothMouseLook.Update 0x06000089, MainCamera.LateUpdate 0x0600047D)
        /// konsol aÃ§Ä±kken fare bakÄ±ÅŸÄ±nÄ± dondurur.
        ///
        /// Neden kendi dÃ¶ngÃ¼mÃ¼z (dnSpy kanÄ±tÄ±, HarmonyInstance.PatchAll token 0x0600006A):
        ///
        ///     assembly.GetTypes().Do&lt;Type&gt;(delegate(Type type) {
        ///         ... new PatchProcessor(this, type, ...).Patch();
        ///     });
        ///
        /// Tek bir Do Ã§aÄŸrÄ±sÄ±, istisna yÃ¼kseldiÄŸinde tÃ¼m enumerasyonu kesiyor. Yani
        /// onaylÄ± (valid) ama oyunda Ã§Ã¶zÃ¼lemeyen tek bir yama, sonraki on yamanÄ±n
        /// hiÃ§ uygulanmamasÄ±na yol aÃ§Ä±yor ve oyuncu yalnÄ±zca tek satÄ±rlÄ±k
        /// "Exception from HarmonyInstance" mesajÄ± gÃ¶rÃ¼yor. AynÄ± dÃ¶ngÃ¼yÃ¼ tekrar
        /// yazÄ±p tip baÅŸÄ±na try/catch koymak iki ÅŸeyi birden kazandÄ±rÄ±yor:
        ///
        ///   * Ã‡Ã¶kme bir yamayla sÄ±nÄ±rlanÄ±r, kalanlar uygulanÄ±r.
        ///   * Hangi tipin neden Ã§Ã¶ktÃ¼ÄŸÃ¼ tam olarak bilinir. Ã–nceden yalnÄ±zca
        ///     e.Message yazÄ±lÄ±yordu; Harmony istisnayÄ± sarmaladÄ±ÄŸÄ± iÃ§in bu mesaj
        ///     hedefteki metodun adÄ±nÄ± iÃ§ermiyordu.
        ///
        /// DÃ¶nÃ¼ÅŸ: baÅŸarÄ±sÄ±z tip sayÄ±sÄ±.
        /// </summary>
        static int PatchAllResilient(HarmonyInstance harmony, Assembly assembly)
        {
            int failures = 0;
            int declared = 0;
            System.Type[] types;
            try { types = assembly.GetTypes(); }
            catch (System.Exception e)
            {
                SmpLog.Error("assembly.GetTypes() failed: " + Describe(e));
                ExpectedPatches = 0;
                return 1;
            }

            for (int i = 0; i < types.Length; i++)
            {
                System.Type type = types[i];
                try
                {
                    System.Collections.Generic.List<HarmonyMethod> methods = type.GetHarmonyMethods();
                    if (methods == null || methods.Count == 0) continue;

                    // Beklenen sayi burada, uygulama yapilirken dogrudan sayiliyor:
                    // ikinci bir GetTypes() turu ve elle yazilmis bir sabit gerekmiyor.
                    declared += methods.Count;

                    HarmonyMethod merged = HarmonyMethod.Merge(methods);
                    new PatchProcessor(harmony, type, merged).Patch();
                }
                catch (System.Exception e)
                {
                    failures++;
                    SmpLog.Error("patch failed on " + type.FullName + ": " + Describe(e));
                }
            }

            ExpectedPatches = declared;
            return failures;
        }

        /// <summary>
        /// Ä°stisna zincirini kÃ¶k nedene kadar aÃ§Ä±k yazar.
        ///
        /// Harmony 1.2 hedefi bulamadÄ±ÄŸÄ±nda iÃ§ istisnayÄ± sarmalayÄ±p atÄ±yor; sadece
        /// e.Message yazÄ±ldÄ±ÄŸÄ±nda "Exception from HarmonyInstance" gÃ¶rÃ¼nÃ¼yor ve
        /// asÄ±l neden (hangi metod bulunamadÄ±) kayboluyor.
        /// </summary>
        static string Describe(System.Exception e)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int depth = 0;
            while (e != null && depth < 6)
            {
                if (depth > 0) sb.Append(" <- ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ModSetup iki kez koÅŸarsa ikinci tur atlanÄ±r.
        ///
        /// Log kanÄ±tÄ±: 12:57:01'de ModSetup aynÄ± process iÃ§inde Ä°KÄ°NCÄ° kez
        /// koÅŸtu ("yukleniyor..." + yama raporu oturum ortasÄ±nda tekrar
        /// belirdi). SonuÃ§lar:
        ///   * PatchAll iki kez â†’ Harmony 1.2 Ã§ift prefix ekler ("16/14
        ///     metot" sayÄ±mÄ±) ve her hook iki kez ateÅŸlenir (Ã§ift aÄŸ
        ///     paketi, Ã§ift log satÄ±rÄ±).
        ///   * RegisterAll iki kez â†’ komutlar Ã§ift kaydolur.
        ///   * MenuController.Install iki kez â†’ menÃ¼ event'leri Ã§ift
        ///     tetiklenir (Ã§ift OpenLobby, Ã§ift log).
        /// PreLoad/PostLoad abonelikleri MSCLoader tarafÄ±nda tutulur, o
        /// yÃ¼zden koruma yalnÄ±zca modun kendi iÅŸlerini kapsar.
        /// </summary>
        static bool setupDone;

        public override void ModSetup()
        {
            SetupFunction(Setup.PreLoad, OnPreLoad);
            SetupFunction(Setup.PostLoad, OnPostLoad);
            if (setupDone) {
                SmpLog.Print(MOD_VERSION + " already set up; skipping re-patch "
                    + "(MSCLoader re-entry).");
                EnsureBehaviour();
                return;
            }
            setupDone = true;
            SmpLog.Print(" "+ MOD_VERSION + " yukleniyor...");

            // Build damgasi: iki tarafin AYNI dll'i kosturdugu logdan
            // dogrulanabilsin (canli kanit: karsilikli reddedilen radyo
            // paketleri + senkron farklari hep "karsi taraf eski mi?"
            // sorusuyla basliyor). MSCLoader Mod.Version zaten gosterir
            // ama log dosyalar arasi karsilastirmada bu satir esastir.
            try {
                System.Reflection.Assembly self = Assembly.GetExecutingAssembly();
                System.IO.FileInfo fi = new System.IO.FileInfo(self.Location);
                SmpLog.Print("build: " + self.GetName().Version
                    + ", " + fi.Length + " byte, "
                    + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }

            // Komutlar modun KENDÄ°SÄ° tarafÄ±ndan kaydedilmeli. dnSpy ile
            // MSCLoader.dll aÃ§Ä±ldÄ±: mod komutlarÄ±nÄ± otomatik keÅŸfeden bir tarama
            // yok; ConsoleCommand.Add(cmd) yalnÄ±zca verilen Ã¶rneÄŸi kaydediyor.
            // Bu Ã§aÄŸrÄ± olmadan 'smp' ve 'npc' hiÃ§ var olmuyor ve F3 konsÃ¶rÃ¼nde
            // Ã§alÄ±ÅŸtÄ±rÄ±lacak komut kalmÄ±yor.
            ModCommands.RegisterAll();

            // Discord Rich Presence: Mods\mscdiscord.txt okunur. clientId
            // bos ise hicbir sey denenmez (Discord yoksa oyun etkilenmez).
            // Ayarlar (dosya yoksa gomulu DEFAULT_CLIENT_ID kullanilir) ve
            // BAGLANTI DERHAL: kullanici istegi "oyunu actigim an baslasin,
            // oyun kapanana kadar devam etsin". Start() handshake'i kendi
            // worker thread'inde yapar; ana is parcigi hic bloklanmaz.
            try {
                DiscordPresence.LoadSettings(null);
                DiscordPresence.Start();
            } catch { }

            HarmonyInstance harmony = HarmonyInstance.Create("com.mscsteammp.mod");

            // SADECE KENDÄ° yamalarÄ±mÄ±zÄ± say. dnSpy kanÄ±tÄ± (0Harmony.dll):
            // HarmonyInstance.GetPatchedMethods() -> HarmonySharedState.
            // GetPatchedMethods() argÃ¼mansÄ±zdÄ±r ve GLOBAL durumu dÃ¶ner
            // (GetState().Keys): process'teki TÃœM modlarÄ±n yamalarÄ±.
            // AdvancedKBMControls de Harmony kullanÄ±yor; sayÄ±m "15/11"
            // Ã§Ä±kÄ±yordu (11 bizim + 4 onun). O yÃ¼zden Ã¶nce fotoÄŸraf alÄ±nÄ±p
            // fark sayÄ±lÄ±r.
            System.Collections.Generic.HashSet<string> before = PatchedKeys(harmony);

            int failures = PatchAllResilient(harmony, Assembly.GetExecutingAssembly());

            // Reported rather than assumed. A hook that silently did not apply looks
            // exactly like a hook that found nothing to capture, so the number is
            // logged and 'smp status' shows it against the declared count.
            int patched = 0;
            System.Collections.Generic.List<string> patchedNames =
                new System.Collections.Generic.List<string>(24);
            foreach (System.Reflection.MethodBase m in harmony.GetPatchedMethods()) {
                string key;
                string show;
                try {
                    key = (m.DeclaringType != null ? m.DeclaringType.FullName : "?")
                        + "::" + m.Name;
                    show = (m.DeclaringType != null ? m.DeclaringType.Name : "?")
                        + "." + m.Name;
                }
                catch { continue; }
                if (before.Contains(key)) continue;   // baÅŸkasÄ±nÄ±n yamasÄ±
                patched++;
                patchedNames.Add(show);
            }
            PatchCount = patched;
            // Yama listesi: sayaÃ§ tutmazsa (Ã¶rn. "15/11") hangi metodun iki
            // kez sayÄ±ldÄ±ÄŸÄ±nÄ± buradan gÃ¶rÃ¼yoruz. Oturum baÅŸÄ±na bir kez.
            patchedNames.Sort(System.StringComparer.Ordinal);
            for (int i = 0; i < patchedNames.Count; i++) {
                SmpLog.Print("  patched: " + patchedNames[i]);
            }

            int missing = ExpectedPatches - patched;
            SmpLog.Print("Harmony yamalarÄ± uygulandÄ±: " + patched + "/" + ExpectedPatches
                + " metot" + (failures > 0 ? ", " + failures + " tip BAÅARISIZ" : "") + ".");
            if (missing > 0) {
                SmpLog.Error(missing + " yama UYGULANMADI. TÃ¼m mod davranÄ±ÅŸÄ± "
                    + "buna dayandÄ±ÄŸÄ± iÃ§in oturumda veri kaybÄ± olur. "
                    + "Mod sÃ¼rÃ¼mleri eÅŸleÅŸmiyor olabilir ya da oyun gÃ¼ncellenmiÅŸ olabilir.");
            }

            // Steam ve aÄŸ iÅŸlemlerini yÃ¼rÃ¼tecek kalÄ±cÄ± nesne. DontDestroyOnLoad, yoksa sahne
            // deÄŸiÅŸiminde Steam baÄŸlantÄ±sÄ± kopar.
            EnsureBehaviour();

            // Custom main menu. Installed here rather than in Awake so the UI events are
            // wired before the first OnGUI can fire.
            MenuController.Install();
        }

        /// <summary>
        /// O an yamalÄ± metotlarÄ±n kÃ¼mesi (sayaÃ§ farkÄ± iÃ§in fotoÄŸraf).
        /// </summary>
        static System.Collections.Generic.HashSet<string> PatchedKeys(HarmonyInstance harmony) {
            System.Collections.Generic.HashSet<string> set =
                new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            try {
                foreach (System.Reflection.MethodBase m in harmony.GetPatchedMethods()) {
                    try {
                        set.Add((m.DeclaringType != null ? m.DeclaringType.FullName : "?")
                            + "::" + m.Name);
                    }
                    catch { }
                }
            }
            catch { }
            return set;
        }

        /// <summary>KalÄ±cÄ± davranÄ±ÅŸ nesnesi yoksa kurar (ilk kurulum + re-entry).</summary>
        static void EnsureBehaviour() {
            if (SteamMPBehaviour.Instance == null) {
                GameObject host = new GameObject("SteamMPBehaviour");
                host.AddComponent<SteamMPBehaviour>();
            }
        }

        /// <summary>About to load a save: drop everything derived from the previous world.</summary>
        void OnPreLoad()
        {
            SmpLog.Print("PreLoad: eski tablo temizleniyor.");
            SteamMPBehaviour.Instance.OnWorldUnloading();
            InteractionManager.Clear();
        }

        /// <summary>Save loaded: the Satsuma and the vehicles exist now.</summary>
        void OnPostLoad()
        {
            SmpLog.Print("PostLoad: tablo kuruluyor.");
            SteamMPBehaviour.Instance.OnWorldReady();

            // Initialize interaction subsystems (carry, parts, bolts, authority, JIP, recovery)
            InteractionManager.Initialize();
            InteractionManager.Discover();
        }
    }
}