// TA-01: ic yapi bayraklarinin karsili tarafa tasinmasi.
//
// KAYNAK (dokum olcumu, bu dosya yazilmadan once yapildi):
//
//   `Attached`    SATSUMA(557kg, 248)/HookFront|Logic
//                 SATSUMA(557kg, 248)/HookRear|Logic
//       2 kayit, 2/2 `GetMouseButtonDown` iceriyor.
//       Yazan eylem: `SetFsmBool Attached`.
//       Kanca takili/cik -> arikan baglanir. Iki taraf farkli
//       dusunce biri kancaya bagli digeri baglamaz.
//
//   `Locked`      SATSUMA(557kg, 248)/Interior/Seatbelts/SeatbeltLock|Use
//                 .../HandleUpPivot/SeatbeltHandle|Use
//                 SATSUMA(557kg, 248)/PlayerTrigger/DriveTrigger|PlayerTrigger
//       3 kayit, 3/3 girdi iceriyor.
//       Yazan eylem: `SetFsmBool Locked`.
//
//   `BlinkerLeft` SATSUMA(557kg, 248)/Dashboard/TurnSignals|Usage
//       Yazan eylem: `SetFsmBool BlinkerLeft`.
//
// NEDEN AYRI BIR KANAL:
// Bu ucu `Assembly`/`Removal` FSM'i olmayan nesneler, yani
// `SatsumaPart` tablosunda YOK. `WritePartBatch` onlari tablonun
// disinda birakir. `Database` tablosu da dogru degil: o
// `Database/*` altindaki bayraklari tasir, bunlar orada degil.
//
// TEL: PROTOCOL_VERSION 20 -> 21, `Msg.InteriorState = 89`.
// KAYIT: byte index, bool deger -> 2 bayt/kayit.
//
// DEGISKEN ADI KURALI (v22 ile ayni):
// Alt dize arama YOK. Yalniz tam ad, artiq acikca yazilmis
// alternatifler. `Attached` kelimesi iceren baska degiskenler
// olabilirdi; `SMP_NO_SUBSTRING_MATCH_V22` geregi tam ad zorunlu.
using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Kanca / kemer kilidi / sinyal bayraklarinin komsu makineye
    /// tasinmasi. Tum degerler mutlak (bool); son-kazanir gecerli,
    /// bir kaybolan paket bir sonraki degisiklikte duzelir.
    /// </summary>
    public static class InteriorStateSync
    {
        // Kayit indeksi -> tanim. INDEKS TELE KABIDIR; yeni kayit
        // eklenirken sona eklenir, ara degerler yer degistirmez.
        public const byte IDX_ATTACHED_FRONT = 0;
        public const byte IDX_ATTACHED_REAR = 1;
        public const byte IDX_SEATBELT_LOCKED = 2;
        public const byte IDX_BLINKER_LEFT = 3;
        public const byte IDX_COUNT = 4;

        class Slot
        {
            public PlayMakerFSM Fsm;
            public FsmBool Var;
            public string Path;   // nesne adi (yol degil)
            public bool Last;
            public bool Known;
            public bool Dirty;
            public bool Suppressed;
        }

        static readonly Slot[] slots = new Slot[IDX_COUNT];
        static bool bound;
        static EchoGuard.Scope echo;

        /// <summary>Baglanti sayaci (teshis icin).</summary>
        public static int BoundCount {
            get { int n = 0; for (int i = 0; i < slots.Length; i++) if (slots[i] != null && slots[i].Var != null) n++; return n; }
        }

        public static int SentCount { get; private set; }
        public static int AppliedCount { get; private set; }
        public static int UnresolvedCount { get; private set; }

        // ------------------------------------------------------------------
        // Baglama
        // ------------------------------------------------------------------

        // SMP_INTERIOR_DEADCODE
        //
        // Yol ile cozumleme (`Resolve`) KALDIRILDI. Baglama artik
        // nesne ADI + FSM adi ile yapiliyor (`FindFsm`), cunku hedef
        // yollar (`SATSUMA/Rope`, `Dashboard/TurnSignalLever`,
        // `Seatbelts/SeatbeltHandle`) DOKUMDE YOK. Ayrica oyun
        // bu referanslari `SendEventByName`/`SetFsmGameObject` ile
        // FSM'e bagliyor; kok yol degil NESNE ADI sabittir.

        /// <summary>
        /// SMP_INTERIOR_LOOKUP
        ///
        /// Nesne adI + FSM adI ile bulur; YOL kullanmaz.
        ///
        /// SATSUMA koku her seferde yeniden cozulur (`AllSceneRoots`
        /// zaten onbellekli -- F07). `true` sart: pasif nesne
        /// altinda kalan FSM'ler var (FsmBinder.cs:173-177).
        ///
        /// Alt dize eslesmesi YOK (SMP_NO_SUBSTRING_MATCH_V22).
        /// `Use` gibi kisa bir FSM adi icin bu onemlidir.
        /// </summary>
        static PlayMakerFSM FindFsm(string objectName, string fsmName)
        {
            GameObject root;
            try { root = SatsumaRegistry.Root; } catch { root = null; }
            if (root == null) return null;
            PlayMakerFSM[] all;
            try { all = root.GetComponentsInChildren<PlayMakerFSM>(true); }
            catch { return null; }
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                PlayMakerFSM f = all[i];
                if (f == null) continue;
                try
                {
                    if (!string.Equals(f.gameObject.name, objectName, StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(f.FsmName, fsmName, StringComparison.Ordinal))
                        continue;
                    return f;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// TAM AD eslesen FsmBool bulur. Alt dize YOK: `Locked`
        /// kelimesi iceren baska bir bool olabilir.
        /// </summary>
        static FsmBool FindBoolExact(PlayMakerFSM fsm, string name)
        {
            if (fsm == null) return null;
            FsmBool[] all;
            try { all = fsm.FsmVariables.BoolVariables; } catch { return null; }
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                string n = null;
                try { n = all[i].Name; } catch { continue; }
                if (string.Equals(n, name, StringComparison.Ordinal)) return all[i];
            }
            return null;
        }

        /// <summary>
        /// Bir bayragi baglar. <paramref name="path"/> kok nesne yoludur
        /// (`SATSUMA(557kg, 248)/HookFront` gibi); FSM tam adi verilir.
        /// </summary>
        static void Bind(byte idx, string objectName, string fsmName, string primary)
        {
            Slot s = new Slot();
            s.Path = objectName;
            s.Fsm = FindFsm(objectName, fsmName);
            if (s.Fsm != null) s.Var = FindBoolExact(s.Fsm, primary);
            slots[idx] = s;
            if (s.Var != null) bound = true;
        }

                // SMP_INTERIOR_LOOKUP
        //
        // YOLLA BAGLANMAZ -- nesne ADI + FSM adi ile baglanir.
        //
        // DOKUM EKSIGI (bu dosya yazilmadan once olculdu):
        //   SATSUMA(557kg, 248)/Rope                       -> HENUZ YOK
        //   SATSUMA(557kg, 248)/Dashboard/TurnSignalLever -> HENUZ YOK
        //   SATSUMA(557kg, 248)/Interior/Seatbelts/SeatbeltHandle -> HENUZ YOK
        // Bu oturumda dokumun eksikligi 5 kez kanitlandi
        // (`Database/PartsStatus/Sparkplug1..4`, tum `wheel_offset`
        // yollari, `trigger_disc`, `pivot_battery/battery(Clone)`
        // canlida var, dokumde yok). Yine de yol TAHMINI yazmaz.
        //
        // YERINE: SATSUMA kokunden tum FSM'ler taranir, sonra
        // (nesne adi, FsmName) ikilisiyle eslesme yapilir.
        // Nesne adlari dokumde kesin:
        //   HookFront|Logic [215]  `SetFsmBool Rope Status Attached`
        //   HookRear|Logic  [215]  ayni
        //   TurnSignals|Usage[107] `... TurnSignalLever Blinkers BlinkerLeft`
        //   SeatbeltLock|Use [67]  `SetFsmBool SeatbeltHandle Use Locked`
        //
        // AYRICA: degisken bulmada `FsmVars.Bool` yerine
        // `fsm.FsmVariables.BoolVariables` icinden TAM AD seçimi
        // kullanilir -- `Locked` kelimesi iceren baska bir bool
        // olabilirdi (v22 alt dize yasagi).
        public static void Discover()
        {
            Clear();
            Bind(IDX_ATTACHED_FRONT, "HookFront", "Logic", "Attached");
            Bind(IDX_ATTACHED_REAR, "HookRear", "Logic", "Attached");
            Bind(IDX_SEATBELT_LOCKED, "SeatbeltLock", "Use", "Locked");
            Bind(IDX_BLINKER_LEFT, "TurnSignalLever", "Blinkers", "BlinkerLeft");
        }

        public static void Clear()
        {
            for (int i = 0; i < slots.Length; i++) slots[i] = null;
            bound = false;
            SentCount = 0;
            AppliedCount = 0;
            UnresolvedCount = 0;
        }

        public static string Describe()
        {
            return "interior: " + BoundCount + "/" + IDX_COUNT + " bound";
        }

        // ------------------------------------------------------------------
        // Gonderim
        // ------------------------------------------------------------------

        /// <summary>
        /// Degismis bayraklari tek pakette gonderir.
        ///
        /// Yalniz `Dirty` olanlar: 4 bayragin tamamini her 100 ms
        /// gondermek bant israfi; cogu kare degismiyor.
        /// </summary>
        public static void Flush()
        {
            if (!bound || !SteamTransport.HasRemote) return;
            // NOT: önceden burada `if (!SteamTransport.IsHost && !SteamTransport.HasRemote) return;`
            // satırı vardı. HasRemote zaten yukarıda kontrol edildi; bu satır gereksizdi.

            List<KeyValuePair<byte, bool>> dirty = null;
            for (int i = 0; i < slots.Length; i++)
            {
                Slot s = slots[i];
                if (s == null || s.Var == null) continue;
                bool v;
                try { v = s.Var.Value; } catch { continue; }
                if (!s.Known) { s.Known = true; s.Last = v; continue; }
                if (v == s.Last) { s.Dirty = false; continue; }
                s.Last = v;
                s.Dirty = true;
                if (dirty == null) dirty = new List<KeyValuePair<byte, bool>>(IDX_COUNT);
                dirty.Add(new KeyValuePair<byte, bool>((byte)i, v));
            }
            if (dirty == null || dirty.Count == 0) return;

            try
            {
                MemoryStream st = Protocol.Begin(Protocol.Msg.InteriorState);
                BinaryWriter w = new BinaryWriter(st);
                w.Write((byte)dirty.Count);
                for (int i = 0; i < dirty.Count; i++)
                {
                    w.Write(dirty[i].Key);
                    w.Write(dirty[i].Value);
                }
                w.Flush();
                if (!SteamTransport.SendReliable(Protocol.Finish(st))) return;
                SentCount += dirty.Count;
            }
            catch (Exception e)
            {
                SmpLog.Warning("interior state send failed: " + e.Message);
            }
        }

        /// <summary>Tum bayraklari gonderir (JIP / ilk senkron).</summary>
        public static void SendAll()
        {
            if (!bound || !SteamTransport.HasRemote) return;
            try
            {
                List<KeyValuePair<byte, bool>> all = new List<KeyValuePair<byte, bool>>(IDX_COUNT);
                for (int i = 0; i < slots.Length; i++)
                {
                    Slot s = slots[i];
                    if (s == null || s.Var == null) continue;
                    bool v;
                    try { v = s.Var.Value; } catch { continue; }
                    s.Known = true;
                    s.Last = v;
                    all.Add(new KeyValuePair<byte, bool>((byte)i, v));
                }
                if (all.Count == 0) return;
                MemoryStream st = Protocol.Begin(Protocol.Msg.InteriorState);
                BinaryWriter w = new BinaryWriter(st);
                w.Write((byte)all.Count);
                for (int i = 0; i < all.Count; i++)
                {
                    w.Write(all[i].Key);
                    w.Write(all[i].Value);
                }
                w.Flush();
                if (!SteamTransport.SendReliable(Protocol.Finish(st))) return;
                SentCount += all.Count;
            }
            catch (Exception e)
            {
                SmpLog.Warning("interior state send-all failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // Alma
        // ------------------------------------------------------------------

        public static void OnMessage(BinaryReader r, byte count)
        {
            for (int i = 0; i < count; i++)
            {
                byte idx;
                bool v;
                try
                {
                    idx = r.ReadByte();
                    v = r.ReadBoolean();
                }
                catch (System.IO.EndOfStreamException)
                {
                    UnresolvedCount++;
                    return;
                }
                if (idx >= slots.Length) { UnresolvedCount++; continue; }
                Slot s = slots[idx];
                if (s == null || s.Var == null) { UnresolvedCount++; continue; }
                // Yanki guardi: yazdigimiz degeri tekrar uygulama.
                // Değer mutlak, ama EchoGuard olmadan yerel kirpma
                // dongusu bir sonraki dinlemede ayni bayragi tekrar
                // gonderir.
                echo = EchoGuard.Enter();
                try { s.Var.Value = v; } catch { }
                finally { echo.Dispose(); }
                s.Known = true;
                s.Last = v;
                AppliedCount++;
            }
        }

        // ------------------------------------------------------------------
        // Uzak degisiklik -> Flush
        // ------------------------------------------------------------------

        static float nextFlush;

        /// <summary>100 ms'de bir cagrilir; kirpma icin gereksinim duymaz.</summary>
        public static void Tick()
        {
            if (!bound) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextFlush) return;
            nextFlush = now + 0.1f;
            if (!SteamTransport.HasRemote) return;
            Flush();
        }
    }
}
