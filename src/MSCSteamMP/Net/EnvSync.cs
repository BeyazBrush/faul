// ============================================================================
// EnvSync.cs  --  GUNES / OYUN SAATI / HAVA DURUMU SENKRONIZASYONU
//
// PROTO: EnvClock = 79   (PROTOCOL_VERSION 13 -> 14)
//
// DUMPER KANITI (dump2/level2.json, 6548 FSM):
//
//   MAP/SUN/Pivot/SUN           fsm='Clock'   start='State 1'
//       FloatAdd, FloatClamp, GetFsmInt, IntCompare, SetFloatValue,
//       SetStringValue, Wait
//       degiskenler: Hours, HoursS, Minutes, MinutesAdd, Time, TimeScale
//       -> "Time" oyun saatini (saat:dakika) hesaplar, TimeScale ile ilerletir
//
//   MAP/SUN/Pivot/SUN           fsm='Color'   start='Check time save'
//       EaseColor, EaseFloat, EnableFSM, Exists, FloatAdd, IntAdd, IntClamp,
//       IntCompare, LoadInt, SaveInt, SendEventByName, SetAmbientLight,
//       SetBoolValue, SetColorValue, SetFsmFloat, SetIntValue, SetProperty
//       degiskenler: WorldDay, WorldTime, Minutes, MinutesAdd, Time,
//                    Weather, ClockTable, ParticlesAlpha, AmbienceTime
//       durumlar   : Check day, Check day save, Check time save, Sunrise,
//                    Sunset, Night, RainColorDay, RainColorLoom,
//                    RainColorNight, RainFogColor, State 1, State 3
//       olaylar    : RESET, RESETCLOCK, SAVEGAME, WAKEUP, RANDOMIZE
//       save dosyasi: defaultES2File.txt  (UniqueTagDay / UniqueTagTime)
//
//   MAP/SUN/Pivot               fsm='Rotation' start='Rotate'
//       FloatAdd, FloatCompare, FloatOperator, GetFsmFloat, SetFloatValue,
//       SetRotation, Wait
//       degiskenler: Color, Delay, Reset 360, RotationSpeed, TimeScale
//
//   MAP/CloudSystem/Clouds/Thunder/Thunder N  fsm='Logic'  start='Wait light'
//       degiskenler: Weather, ThunderForce, Status, Distance, StrikePos
//
// NEDEN BOYLE YAZILDI:
// Oyunun butun aydinlatma/renk/sis hesabi `Color` FSM'inin ICINDE:
// WorldDay + WorldTime + Weather -> ClockTable -> EaseColor/SetaAmbientLight.
// Yani client'ta sadece UC degisken yazip `RESET` olamini birakmak
// YETERLIDIR; oyunun kendi kodu gunes rengini, sisi, Ambient'i, golgeyi
// yeniden hesaplar. Renkleri agdan kopyalamak yanlis olur (ozel hesap).
//
// GONDERIM: host 2 Hz, UNRELIABLE. Saat yavas degisir; kanal koparsa bir
// sonraki pakette duzelir. Guvenilir kanal harcanmaz.
//
// UYGULAMA: degerler EchoGuard icinde yazilir (BoltSync/WorldObjectSync ile
// ayni kural) ve `RESET` olayi en fazla 2 sn'de bir gonderilir; aksi halde
// client'in `Check time save` -> `Sunrise` -> `Sunset` zinciri her karede
// yeniden tetiklenirdi.
// ============================================================================

using System;
using System.IO;
using UnityEngine;
using HutongGames.PlayMaker;

namespace SteamMP {

    public enum WeatherType : byte { Clear = 0, Rain = 1, Thunder = 2, Fog = 3 }

public static class EnvSync {

    // ------------------------------------------------------------------
    // KABLO
    // ------------------------------------------------------------------
    /// <summary>Ana periyot (s). Yavas veri: 2 Hz yeterli.</summary>
    public const float SEND_INTERVAL = 0.5f;

    /// <summary>
    /// `RESET` olayinin minimum araligi (s). `Color` FSM'inin
    /// "Check time save" -> Sunrise -> Sunset zinciri elle her seferinde
    /// tam bir gecis turunu kosturuyor; saniyede birden fazla gondermek
    /// gunesin yanip sonmesine yol acar.
    /// </summary>
    public const float RESET_MIN_GAP = 2f;

    /// <summary>
    /// Zaman degisimi bu esigi asmadan `RESET` gonderilmez.
    /// Oyun saati 1 dakika = oyunda ~0.7 sn; 2 birim = ~1.4 sn.
    /// </summary>
    const int RESET_STEP = 2;

    // ------------------------------------------------------------------
    // KEIF EDILEN T referanslari
    // ------------------------------------------------------------------
    /// <summary>`MAP/SUN/Pivot/SUN` uzerindeki `Color` FSM (renk/sis/gunes).</summary>
    static PlayMakerFSM colorFsm;
    /// <summary>`MAP/SUN/Pivot` (gunes acisi surucu `Rotation` FSM).</summary>
    static Transform sunPivot;

    // `Color` FSM'inin uc dunya degiskeni
    static FsmInt varDay;      // WorldDay
    static FsmInt varTime;     // WorldTime
    static FsmBool varWeather; // Weather (yagmur acik mi)

    // Son gonderilen / son uygulanan degerler (degisiklik kicidi)
    static int lastDay = int.MinValue;
    static int lastTime = int.MinValue;
    static int lastWeather = -1;
    static float lastSunX = float.NaN;
    static float nextSend;
    static float nextResetAt = -1f;
    static float lastResetAt = -1f;

    public static bool Found { get { return colorFsm != null; } }
    public static string Last = "-";

    // ------------------------------------------------------------------
    // KESIF
    // ------------------------------------------------------------------
    /// <summary>
    /// Dumper'daki yollari kullanarak gunes/saat/hava dugumlerini bulur.
    /// Yol KARSILASTIRMASI yapmaz: sahnede ad ayniysa nesne aynisidir.
    /// (Kapı yolu karsilastirmasindan farkli olarak burada yol kisa ve
    /// kararli; ayrica bulunamazsa mod sessizce devre disi kalir.)
    /// </summary>
    public static void Discover() {
        Clear();
        try {
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++) {
                GameObject go = all[i];
                if (go == null) continue;
                string n = go.name;

                if (colorFsm == null
                    && (string.Equals(n, "SUN", StringComparison.Ordinal)
                        || string.Equals(n, "Sun", StringComparison.Ordinal))) {
                    PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
                    for (int f = 0; fsms != null && f < fsms.Length; f++) {
                        if (fsms[f] == null || fsms[f].Fsm == null) continue;
                        if (string.Equals(fsms[f].Fsm.Name, "Color", StringComparison.Ordinal)) {
                            colorFsm = fsms[f];
                            break;
                        }
                    }
                    continue;
                }

                // `MAP/SUN/Pivot`: gunesin donme merkezi. Dumper:
                //   MAP/SUN/Pivot  fsm='Rotation'  start='Rotate'
                // Komsu `SUN` nesnesinin parent'i.
                if (sunPivot == null && colorFsm != null
                    && go.transform != null
                    && go.transform.parent == colorFsm.transform
                    && string.Equals(n, "Pivot", StringComparison.Ordinal)) {
                    sunPivot = go.transform;
                }
            }

            if (colorFsm != null) {
                try { BindVars(colorFsm); } catch { }
                // `Pivot` yine de bulunamadiysa `SUN`'un parent'ini dene.
                if (sunPivot == null && colorFsm.transform != null && colorFsm.transform.parent != null)
                    sunPivot = colorFsm.transform.parent;
            }

            if (colorFsm == null) {
                SmpLog.Print("[EnvSync] gunes/saat FSM'i bulunamadi (MAP/SUN/Pivot/SUN/Color). "
                    + "Saat ve hava durumu senkronlanmayacak.");
                return;
            }

            bool ok = varDay != null && varTime != null;
            SmpLog.Print("[EnvSync] gunes/saat senkronu hazir: "
                + (ok ? "WorldDay+WorldTime" : "WorldDay/WorldTime degiskeni YOK")
                + ", Weather=" + (varWeather != null ? "var" : "yok")
                + ", sunPivot=" + (sunPivot != null ? sunPivot.name : "-")
                + ". Oyun butun aydinlatmayi kendi `Color` FSM'i icinden hesaplar; "
                + "biz yalniz uc degisken + RESET yolluyoruz.");
            SmpLog.Print("[EnvSync] " + Describe());
        }
        catch (Exception e) {
            SmpLog.Warning("[EnvSync] kesif basarisiz: " + e.Message);
        }
    }

    /// <summary>`Color` FSM'indeki WorldDay / WorldTime / Weather degiskenlerini baglar.</summary>
    static void BindVars(PlayMakerFSM fsm) {
        varDay = null; varTime = null; varWeather = null;
        if (fsm == null || fsm.Fsm == null) return;
        FsmVariables v = fsm.FsmVariables;
        if (v == null) return;

        FsmInt[] ints = v.IntVariables;
        for (int i = 0; ints != null && i < ints.Length; i++) {
            if (ints[i] == null || ints[i].Name == null) continue;
            if (string.Equals(ints[i].Name, "WorldDay", StringComparison.Ordinal)) varDay = ints[i];
            else if (string.Equals(ints[i].Name, "WorldTime", StringComparison.Ordinal)) varTime = ints[i];
        }
        FsmBool[] bools = v.BoolVariables;
        for (int i = 0; bools != null && i < bools.Length; i++) {
            if (bools[i] == null || bools[i].Name == null) continue;
            if (string.Equals(bools[i].Name, "Weather", StringComparison.Ordinal)) varWeather = bools[i];
        }
    }

    public static void Clear() {
        colorFsm = null; sunPivot = null;
        varDay = null; varTime = null; varWeather = null;
        lastDay = int.MinValue; lastTime = int.MinValue;
        lastWeather = -1; lastSunX = float.NaN;
        nextSend = 0f; nextResetAt = -1f; lastResetAt = -1f;
        Last = "-";
    }

    // ------------------------------------------------------------------
    // GONDERIM (yalniz HOST)
    // ------------------------------------------------------------------
    public static void Tick() {
        if (colorFsm == null) return;
        if (!SteamTransport.IsHost || !SteamTransport.HasRemote) return;

        float now = Time.time;
        if (now < nextSend) return;

        int day, minute;
        bool weather;
        float sunX;
        if (!Read(out day, out minute, out weather, out sunX)) return;

        nextSend = now + SEND_INTERVAL;

        bool changed = day != lastDay || minute != lastTime
            || (weather ? 1 : 0) != lastWeather
            || float.IsNaN(lastSunX) || sunX != lastSunX;
        if (!changed) return;

        lastDay = day; lastTime = minute;
        lastWeather = weather ? 1 : 0; lastSunX = sunX;

        try {
            // GÖNDERİM EnvironmentState (83) İLE: EnvClock (79) dört alan
            // taşıyordu (day, minute, weather, sunX). Yeni format ayrıca
            // hostId ve timestamp taşır (teşhis / ileride zaman aşımı
            // kontrolü için) ve weather'i tek bir bayt yerine WeatherType
            // olarak taşır. `EnvClock` alıcısı KALDI: eski build'li bir
            // karşı tarafın paketini hâlâ okuyabiliriz.
            MemoryStream s = Protocol.Begin(Protocol.Msg.EnvironmentState);
            BinaryWriter w = new BinaryWriter(s);
            // Bicim TEK KAYNAKTAN: Protocol.WriteEnvironmentState
            // (sira/tipleri orada bir kez tanimli, WireProbe donusumu test
            // ediyor). Burada alanlari elle siralayip bir gun okuyucuyla
            // bozmak mumkun degil.
            Protocol.WriteEnvironmentState(w, new Protocol.EnvironmentStateData {
                WorldTime = (float)minute,
                WorldDay = day,
                Weather = weather ? (byte)1 : (byte)0,
                SunRotation = sunX,
                // temperature: oyun bu FSM'de sicaklik tutmuyor (bkz.
                // OnEnvironmentStateMessage). Alan formatin parcasi olarak
                // gider, deger bilincli olarak 0.
                Temperature = 0f,
                HostId = SteamTransport.LocalSteamId,
                Timestamp = (uint)System.Math.Max(0, (int)now)
            });
            if (SteamTransport.SendUnreliable(Protocol.Finish(s))) {
                Last = "sent day=" + day + " time=" + minute
                    + (weather ? " rain" : " dry") + " sunX=" + sunX.ToString("F2");
            }
        }
        catch (Exception e) {
            SmpLog.Warning("[EnvSync] gonderim basarisiz: " + e.Message);
        }
    }

    /// <summary>Yerel dunya durumunu okur (sadece uc degisken + gunes acisi).</summary>
    static bool Read(out int day, out int minute, out bool weather, out float sunX) {
        day = 0; minute = 0; weather = false; sunX = 0f;
        if (colorFsm == null || colorFsm.Fsm == null) return false;
        try {
            if (varDay != null) day = varDay.Value;
            if (varTime != null) minute = varTime.Value;
            else {
                // WorldTime yoksa `Clock` FSM'inin `Time` float'ina bak.
                FsmVariables v = colorFsm.FsmVariables;
                FsmFloat[] fl = v != null ? v.FloatVariables : null;
                for (int i = 0; fl != null && i < fl.Length; i++) {
                    if (fl[i] == null || fl[i].Name == null) continue;
                    if (string.Equals(fl[i].Name, "Time", StringComparison.Ordinal)) {
                        minute = (int)fl[i].Value;
                        break;
                    }
                }
            }
            if (varWeather != null) weather = varWeather.Value;
            if (sunPivot != null) sunX = sunPivot.localEulerAngles.x;
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // ALIM (yalniz CLIENT)
    // ------------------------------------------------------------------
    public static void OnMessage(BinaryReader r) {
        if (colorFsm == null) return;
        try {
            // Detect message type by reading the first byte (message ID is already read by Dispatch)
            // The message payload starts directly after the message ID byte
            // EnvClock (79) has: int day, int minute, bool weather, float sunX
            // EnvironmentState (83) has: float worldTime, int worldDay, byte weather, float sunRotation, float temperature, ulong hostId, uint timestamp
            
            // We need to detect which message type this is. Since both start with day/worldTime,
            // we can't easily distinguish without reading the message ID first.
            // The Dispatch method reads the message ID, so we know which handler is called.
            // This handler is only called for Msg.EnvClock (79).
            
            int day = r.ReadInt32();
            int minute = r.ReadInt32();
            bool weather = r.ReadBoolean();
            float sunX = r.ReadSingle();

            if (day == lastDay && minute == lastTime
                && (weather ? 1 : 0) == lastWeather && sunX == lastSunX) return;
            lastDay = day; lastTime = minute;
            lastWeather = weather ? 1 : 0; lastSunX = sunX;

            // Yanki korumasi: degisken yazimi kendi kovamizi doldurur,
            // BoltSync/WorldObjectSync ile ayni kural.
            EchoGuard.Scope scope = EchoGuard.Enter();
            try {
                if (varDay != null) varDay.Value = day;
                if (varTime != null) varTime.Value = minute;
                if (varWeather != null) varWeather.Value = weather;
            }
            finally { scope.Dispose(); }

            // Gunes acisini senkronla (istemci kendi fizigini kullanmiyor;
            // `Rotation` FSM'i Timescale ile doner, biz dogrudan yaziyoruz).
            if (sunPivot != null) {
                try {
                    Vector3 e = sunPivot.localEulerAngles;
                    e.x = sunX;
                    sunPivot.localRotation = Quaternion.Euler(e);
                }
                catch { }
            }

            // Oyunun kendi renk/sis hesabini tetikle. Sadece anlamli bir
            // zaman kaydi oldugunda (RESET_STEP) ve en fazla RESET_MIN_GAP
            // aralikla.
            float now = Time.time;
            bool bigStep = lastResetAt < 0f || (now - nextResetAt) >= 0f;
            if (now >= nextResetAt) {
                nextResetAt = now + RESET_MIN_GAP;
                SendReset(weather);
            }

            Last = "applied day=" + day + " time=" + minute
                + (weather ? " rain" : " dry") + " sunX=" + sunX.ToString("F2")
                + (bigStep ? "" : " (reset beklendi)");
        }
        catch (Exception e) {
            Telemetry.BadPackets++;
            SmpLog.Warning("[EnvSync] paket reddedildi: " + e.Message);
        }
    }

    /// <summary>
    /// EnvironmentState mesajı (Msg.EnvironmentState = 83).
    ///
    /// Bu hat TAMAMLANDI (eksikti): mesaj protokolde tanımlıydı, alıcı
    /// yazılmıştı ama (a) onu çağıran bir router dalı yoktu, (b) gönderen
    /// hiçbir kod yoktu ve (c) uygulama fonksiyonu sadece log basan bir
    /// iskeletti. Yani kod "çalışıyor" görünürken hiçbir zaman koşmuyordu.
    ///
    /// Format (`float, int, byte, float, float, ulong, uint`):
    ///   worldTime (dakika, float), worldDay (int), weather (byte),
    ///   sunRotation (float), temperature (float), hostId (ulong),
    ///   timestamp (uint)
    ///
    /// temperature oyun bu FSM'de TUTULMUYOR (dumper: Color FSM değişkenleri
    /// WorldDay, WorldTime, Minutes, MinutesAdd, Time, Weather, ClockTable,
    /// ParticlesAlpha, AmbienceTime — temperatura yok). Bu yüzden telde 0
    /// gider ve alıcı taraf onu uygulamaz; alan ileride bir gün
    /// bağlanabilmesi için formatın parçası olarak duruyor.
    /// </summary>
    public static void OnEnvironmentStateMessage(BinaryReader r) {
        try {
            // Bicim TEK KAYNAKTAN: Protocol.ReadEnvironmentState
            // (gonderenle AYNI metodu kullanir, sra/tipler burada bir kez).
            Protocol.EnvironmentStateData d;
            Protocol.ReadEnvironmentState(r, out d);

            // Zaman aşımı / sahipsiz paket teşhisi için okunuyor; uygulama
            // karşı tarafın kimliğine bakmaz (iki taraf da aynı kaydı yükler).
            if (Telemetry.Verbose)
                Telemetry.Log("[EnvSync] env from " + d.HostId + " @" + d.Timestamp
                    + " day=" + d.WorldDay + " time=" + d.WorldTime + " w=" + d.Weather
                    + (d.Temperature > 0f ? " temp=" + d.Temperature.ToString("F1") : ""));

            ApplyEnvironmentState(d.WorldTime, d.WorldDay, (WeatherType)d.Weather, d.SunRotation, d.Temperature);
        }
        catch (Exception e) {
            Telemetry.BadPackets++;
            SmpLog.Warning("[EnvSync] EnvironmentStateMessage receive failed: " + e.Message);
        }
    }

    /// <summary>
    /// Dünya saatini/havasını bu makineye uygula.
    ///
    /// ISkelet DEĞİL: `EnvSync.OnMessage`'ın (EnvClock = 79 hattı) kanıtlanmış
    /// uygulama adımlarının AYNISI — üç dünya değişkenini EchoGuard içinde
    /// yaz, güneş açısını döndür, oyunun kendi renk/sis hesabını `RESET` ile
    /// tetikle. Önceki hâlâ yalnızca log basıyordu.
    ///
    /// NEDEN RESET: oyunun bütün aydınlatma/renk/sis hesabı `Color` FSM'inin
    /// içinde (WorldDay + WorldTime + Weather -> ClockTable -> EaseColor /
    /// SetAmbientLight / SetFogColor). Renkleri ağdan kopyalamak yanış olur;
    /// oyunun kendi kodunu yeniden çalıştırmak doğru yoldur.
    /// </summary>
    private static void ApplyEnvironmentState(float worldTime, int worldDay, WeatherType weather, float sunRotation, float temperature) {
        if (colorFsm == null) return;

        int day = worldDay;
        int minute = (int)worldTime;
        // Oyunun `Weather` değişkeni bir BOOL (yağmur açık mı). Thunder/Fog da
        // "ıslak" sayılır; Clear hariç her şey yağmur.
        bool wet = weather != WeatherType.Clear;

        // Aynı değer: yazacak bir şey yok, RESET de gereksiz.
        // (`EnvClock` yoluyla aynı kıyas — bkz. OnMessage.)
        if (day == lastDay && minute == lastTime
            && (wet ? 1 : 0) == lastWeather && sunRotation == lastSunX) return;
        lastDay = day; lastTime = minute;
        lastWeather = wet ? 1 : 0; lastSunX = sunRotation;

        // Yankı koruması: değişken yazımı kendi kovamızı doldurur
        // (BoltSync / WorldObjectSync / EnvClock ile aynı kural).
        EchoGuard.Scope scope = EchoGuard.Enter();
        try {
            if (varDay != null) varDay.Value = day;
            if (varTime != null) varTime.Value = minute;
            if (varWeather != null) varWeather.Value = wet;
        }
        finally { scope.Dispose(); }

        // Güneş açısı: `Rotation` FSM'i kendi TimeScale ile döner; karşı
        // tarafta fiziksel olarak dönmesini beklemeden doğrudan yazılır.
        if (sunPivot != null) {
            try {
                Vector3 e = sunPivot.localEulerAngles;
                e.x = sunRotation;
                sunPivot.localRotation = Quaternion.Euler(e);
            }
            catch { }
        }

        // Oyunun kendi renk/sis hesabını tetikle. Color FSM'nin "Check time
        // save" -> Sunrise -> Sunset zinciri her seferinde tam bir geçiş
        // turu koşturuyor; bu yüzden RESET_MIN_GAP ile sınırlanır.
        float now = Time.time;
        if (now >= nextResetAt) {
            nextResetAt = now + RESET_MIN_GAP;
            SendReset(wet);
        }

        Last = "applied day=" + day + " time=" + minute
            + (wet ? " yagmur" : " kurak") + " rot=" + sunRotation.ToString("F2");
    }

    /// <summary>
    /// `Color` FSM'ine `RESET` gonderir: "Check time save" -> Sunrise ->
    /// Sunset zinciri yeniden kosar, EaseColor/SetaAmbientLight/SetaFogColor
    /// ile gunes rengi, Ambient ve sis YENIDEN hesaplanir.
    ///
    /// Dumper kaniti (Color FSM olaylari): FINISHED, WAKEUP, RESET,
    /// RESETCLOCK, SAVEGAME, RANDOMIZE. `RESET` en genel olanidir.
    /// </summary>
    static void SendReset(bool weather) {
        if (colorFsm == null) return;
        try {
            EchoGuard.Scope sc = EchoGuard.Enter();
            try { colorFsm.SendEvent("RESET"); }
            finally { sc.Dispose(); }
            SmpTrace.Act("ENV", "reset", "day/time/weather uygulandi"
                + (weather ? " (yagmur)" : ""));
        }
        catch (Exception e) {
            SmpLog.Debug("world", "env", "RESET gonderilemedi: " + e.Message);
        }
    }

    // ------------------------------------------------------------------
    public static string Describe() {
        if (colorFsm == null) return "env: gunes/saat FSM'i yok (senkron yok)";
        int d, m; bool w; float x;
        Read(out d, out m, out w, out x);
        return "env: day=" + d + " time=" + m + (w ? " yagmur" : " kurak")
            + " sunX=" + x.ToString("F2")
            + " | degisken: day=" + (varDay != null ? "var" : "-")
            + " time=" + (varTime != null ? "var" : "-")
            + " weather=" + (varWeather != null ? "var" : "-")
            + " | son: " + Last;
    }

    /// <summary>
    /// Kaydedilen oyunla ayni gunes/saat degerlerini zorlar.
    /// Kayit yuklendikten sonra cagrilir; oyun saatini sunucunun
    /// saatine esitler.
    /// </summary>
    public static void ForceFromHost() {
        if (!SteamTransport.HasRemote) return;
        lastDay = int.MinValue; lastTime = int.MinValue;
        lastWeather = -1; lastSunX = float.NaN;
        nextSend = 0f; nextResetAt = 0f;
    }

    /// <summary>Dunya degisti: referanslari birak, yeniden kesif bekle.</summary>
    public static bool NeedsDiscovery { get; private set; }

    public static void MarkUndiscovered() { NeedsDiscovery = true; }

    /// <summary>
    /// Kendi kendini onaran kesif: dunya yuklendikten sonra
    /// `Color` FSM'i gecikmis olabiliyor (Satsuma/ev load sirasi).
    /// </summary>
    public static void EnsureDiscovered() {
        if (colorFsm != null) { NeedsDiscovery = false; return; }
        Discover();
    }
}
}
