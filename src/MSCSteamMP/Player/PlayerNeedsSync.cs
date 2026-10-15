using System;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Oyuncu İhtiyaç Senkronizasyonu (açlık, susuzluk, yorgunluk, stres,
    /// kirlilik, idrar).
    ///
    /// DUMPER KANITI (6548 kayıtlık l2/fsms.json): ihtiyaç değişkenleri
    /// TEK bir FSM'de duruyor ve hepsi PlayMaker GLOBAL'i:
    ///
    ///   Database/PlayerDatabase  [Simulation]
    ///     PlayerHunger   PlayerThirst    PlayerStress   PlayerFatigue
    ///     PlayerDirtiness PlayerUrine    PlayerMoney    PlayerDrunk
    ///     PlayerCigarettes PlayerFines   PlayerWeight   PlayerTattoo
    ///     PlayerNetIncome PlayerPermaDeath
    ///
    /// v15'te silinenler ve NEDEN:
    ///   * Ölüm senkronu (IsDead / IsUnconscious / Permadeath): bunlar
    ///     "PlayerState" adlı bir FSM'den okunuyordu. Öyle bir FSM
    ///     YOK (tüm dökümde FSM adı olarak "PlayerState" geçmiyor).
    ///   * Health / MaxHealth: dökümde HİÇBİR can değişkeni yok.
    ///   * Energy: "PlayerEnergy" ve "Stamina" yok; yalnız PlayerEnergyRate
    ///     var, o da bir ORAN (enerji değil).
    ///
    /// Bladder alanı v15'e kadar hep null idi (:210) ama karşılığı olan
    /// PlayerUrine globali dökümde MEVCUT; artık bağlanıyor.
    ///
    /// NOT: <see cref="ForceLocalValues"/> bu değerleri yerelde her kare
    /// sabitler (bilinçli hile: açlık/susuzluk 0, PlayerKey* 1,
    /// PlayerMoney 9999999). Bu modül karşı tarafa gönderilen değeri
    /// üretir; hile katmanı ayrıdır.
    /// </summary>
    public static class PlayerNeedsSync
    {
        // --- Ayarlar ---
        public const float SEND_INTERVAL = 1f;        // 1 Hz periyodik
        public const float KEEPALIVE_INTERVAL = 1f;   // 1 Hz (değişim yoksa)

        /// <summary>
        /// Gerçek değişken adları. Bu build'de asset taramasıyla doğrulandı;
        /// eski yorumlardaki Bladder/Hunger/Thirst/Fatigue/Stress/Dirtiness
        /// adlarının HİÇBİRİ dosyalarda geçmiyor.
        /// </summary>
        static readonly string[] HUNGER_NAMES = { "PlayerHunger", "Hunger" };
        static readonly string[] THIRST_NAMES = { "PlayerThirst", "Thirst" };
        static readonly string[] STRESS_NAMES = { "PlayerStress", "Stress" };
        static readonly string[] FATIGUE_NAMES = { "PlayerFatigue", "Fatigue" };
        static readonly string[] DIRTINESS_NAMES = { "PlayerDirtiness", "Dirtiness" };
        /// <summary>Idrar/belder. v15'e kadar hiç bağlanmıyordu.</summary>
        static readonly string[] BLADDER_NAMES = { "PlayerUrine", "Urine" };

        // --- Veri Yapısı ---
        //
        // ALTı alan, altı gerçek global. Sıra TEL FORMATIDIR: gönderim
        // ve alım aynı sırayla yazıyor. Yeni alan EKLEME; ihtiyaç varsa
        // PROTOCOL_VERSION'ı artır (şu an 15).
        public struct NeedsData
        {
            public float Bladder;       // PlayerUrine
            public float Hunger;        // PlayerHunger
            public float Thirst;        // PlayerThirst
            public float Fatigue;       // PlayerFatigue
            public float Stress;        // PlayerStress
            public float Dirtiness;     // PlayerDirtiness
        }

        // --- Durum ---
        static NeedsData lastSentData;
        static NeedsData lastKnownData;
        static float nextSendTime;
        static float nextKeepAliveTime;
        static bool hasInitialized;
        static PlayMakerFSM needsFsm;
        /// <summary>Bağlanan FSM'lerin dünya yolu (teşhis için).</summary>
        static string needsPath = "";
        /// <summary>Tarama sırasında kaç FSM görüldü (teşhis için).</summary>
        static int scannedFsms;

        // --- Cache'li Variable Referansları ---
        static FsmFloat varBladder, varHunger, varThirst, varFatigue, varStress, varDirtiness;

        // --- Public API ---

        /// <summary>Sistemi başlatır (FSM referanslarını çözümler).</summary>
        public static void Initialize()
        {
            if (hasInitialized) return;

            // Player root'unu bul
            GameObject playerRoot = GameObject.Find("PLAYER");
            if (playerRoot == null)
            {
                SmpLog.Warning("[PlayerNeedsSync] PLAYER root bulunamadı");
                return;
            }

            // FSM'leri 'ICERIGE' gore bul, ama TUM SAHNE'de ara.
            //
            // CANLI LOG KANITI: once yalnizca GameObject.Find("PLAYER")
            // altinda arandi ve sonuc "(none)" oldu. Yani ihtiyac
            // degiskenleri PLAYER altinda degil, sahnenin baska bir
            // kolunda (asset'te "PlayerMisc" ile ayni FSM'de duruyor).
            // Bu yuzden kokleri geziyoruz.
            //
            // Maliyet: WorldSync.Discover() zaten ayni gezinmeyi yapiyor
            // (6942 FSM). Bir kez, dunya yuklendiginde.
            GameObject[] roots;
            try { roots = SceneDump.AllSceneRoots(); }
            catch (Exception e)
            {
                SmpLog.Warning("[PlayerNeedsSync] sahne kokleri alinamadi: " + e.Message);
                return;
            }

            int scanned = 0;
            for (int r = 0; r < roots.Length; r++)
            {
                GameObject root = roots[r];
                if (root == null) continue;
                PlayMakerFSM[] allFsms;
                try { allFsms = root.GetComponentsInChildren<PlayMakerFSM>(true); }
                catch { continue; }
                scanned += allFsms.Length;

                for (int i = 0; i < allFsms.Length; i++)
                {
                    PlayMakerFSM fsm = allFsms[i];
                    if (fsm == null) continue;

                    // Ihtiyac degiskenlerini gercekten tasiyan ilk FSM.
                    if (needsFsm == null && HasAnyFloat(fsm, HUNGER_NAMES, THIRST_NAMES))
                    {
                        needsFsm = fsm;
                        needsPath = Wire.PathOf(fsm.transform);
                    }

                }

                // Ihtiyac FSM'i bulunduysa tam sahne taramasina gerek yok.
                if (needsFsm != null) break;
            }

            scannedFsms = scanned;

            // Variable referanslarini cache'le
            CacheVariables();

            bool anyBound = varHunger != null || varThirst != null
                || varStress != null || varFatigue != null;

            // Ilk veriyi oku
            lastKnownData = ReadCurrentNeeds();
            lastSentData = lastKnownData;
            hasInitialized = true;

            // Baglanan degiskenleri ADLARIYLA VE YOLUyla logla. "Needs=True"
            // demek bir sey ifade etmiyordu; hangi degiskenin nerede
            // baglandigi belli olmali.
            SmpLog.Log("[PlayerNeedsSync] needs FSM = '"
                + (needsFsm != null ? needsFsm.FsmName : "(none)")
                + "' @ '" + (needsFsm != null ? needsPath : "-")
                + "' | bagli: acik"
                + (varHunger != null ? "Y" : "n") + " susuz"
                + (varThirst != null ? "Y" : "n") + " stres"
                + (varStress != null ? "Y" : "n") + " yorgunluk"
                + (varFatigue != null ? "Y" : "n") + " kir"
                + (varDirtiness != null ? "Y" : "n") + " idrar"
                + (varBladder != null ? "Y" : "n")
                + " | " + scannedFsms + " FSM tarandi");

            if (!anyBound)
            {
                // Hicbir sey baglanmadiysa 1 Hz'de bos paket gondermenin
                // anlami yok; kullaniciya da ne yapacagini soyleyelim.
                SmpLog.Warning("[PlayerNeedsSync] bu build'de ihtiyac degiskeni "
                    + "bulunamadi; ihtiyac senkronu KAPALI. "
                    + "Acik/susuzluk degisiklikleri karsi tarafa gitmez.");
            }
        }

        static void CacheVariables()
        {
            if (needsFsm != null)
            {
                varHunger = FloatByNames(needsFsm, HUNGER_NAMES);
                varThirst = FloatByNames(needsFsm, THIRST_NAMES);
                varStress = FloatByNames(needsFsm, STRESS_NAMES);
                varFatigue = FloatByNames(needsFsm, FATIGUE_NAMES);
                varDirtiness = FloatByNames(needsFsm, DIRTINESS_NAMES);
                varBladder = FloatByNames(needsFsm, BLADDER_NAMES);
            }

            // CANLI LOG KANITI (21:40): gercek isimler sahnenin FSM'inde
            // DEGIL, PlayMaker GLOBAL degiskenlerinde duruyor
            // ('smp globals Player' ile dogrulandi: global float PlayerHunger,
            // PlayerThirst, PlayerStress, PlayerFatigue, PlayerUrine...).
            // Buradaki FSM taramasi her zaman bos donuyordu, o yuzden
            // 'ihtiyac degiskeni bulunamadi' uyarisi geliyor ve senkron
            // KAPALI kalıyordu. Globalerden DOGRU kaynaktan bagla.
            BindFromGlobals();

        }

        /// <summary>Global değişkenlerden ihtiyaç referanslarını doldurur.</summary>
        static void BindFromGlobals()
        {
            try
            {
                FsmVariables g = FsmVariables.GlobalVariables;
                if (g == null || g.FloatVariables == null) return;
                FsmFloat[] all = g.FloatVariables;
                varHunger = varHunger ?? FloatOrNull(all, "PlayerHunger", "Hunger");
                varThirst = varThirst ?? FloatOrNull(all, "PlayerThirst", "Thirst");
                varStress = varStress ?? FloatOrNull(all, "PlayerStress", "Stress");
                varFatigue = varFatigue ?? FloatOrNull(all, "PlayerFatigue", "Fatigue");
                varDirtiness = varDirtiness ?? FloatOrNull(all, "PlayerDirtiness", "Dirtiness");
                // v15: idrar. Dumper'da PlayerUrine MEVCUT; eski kod
                // varBladder'i null birakip alani kalip deger olarak 0
                // gonderiyordu.
                varBladder = varBladder ?? FloatOrNull(all, "PlayerUrine", "Urine");
                SmpLog.Log("[PlayerNeedsSync] globals baglandi: acik"
                    + (varHunger != null ? "Y" : "n") + " susuz"
                    + (varThirst != null ? "Y" : "n") + " stres"
                    + (varStress != null ? "Y" : "n") + " yorgunluk"
                    + (varFatigue != null ? "Y" : "n") + " kir"
                    + (varDirtiness != null ? "Y" : "n") + " idrar"
                    + (varBladder != null ? "Y" : "n"));
            }
            catch (Exception e)
            {
                SmpLog.Warning("[PlayerNeedsSync] globals okunamadi: " + e.Message);
            }
        }

        /// <summary>FsmFloat dizisinden ada göre bulur (yoksa null).</summary>
        static FsmFloat FloatOrNull(FsmFloat[] all, params string[] names)
        {
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                for (int n = 0; n < names.Length; n++)
                {
                    if (string.Equals(all[i].Name, names[n], StringComparison.Ordinal))
                        return all[i];
                }
            }
            return null;
        }

        /// <summary>FSM'in float degiskenleri arasinda verilen adlardan biri var mi?</summary>
        /// <summary>
        /// Ad listesinden ilk eşleşen float değişkeni.
        ///
        /// FsmVars.Float imzası <c>(string primary, params string[]
        /// alternatives)</c>; diziyi olduğu gibi vermek primary'a
        /// string[] geçirmeye çalışmak demek (derleme hatası). Bu sarmalayıcı
        /// ad listesini doğru şekilde açar.
        /// </summary>
        static FsmFloat FloatByNames(PlayMakerFSM fsm, string[] names)
        {
            if (fsm == null || names == null || names.Length == 0) return null;
            return FsmVars.Float(fsm, names[0], names[1]);
        }

        /// <summary>FSM'in float değişkenleri arasında verilen adlardan biri var mı?</summary>
        static bool HasAnyFloat(PlayMakerFSM fsm, params string[][] nameGroups)
        {
            if (fsm == null) return false;
            try
            {
                FsmFloat[] all = fsm.FsmVariables.FloatVariables;
                if (all == null) return false;
                for (int g = 0; g < nameGroups.Length; g++)
                {
                    string[] names = nameGroups[g];
                    for (int n = 0; n < names.Length; n++)
                    {
                        for (int f = 0; f < all.Length; f++)
                        {
                            if (all[f] != null && all[f].Name == names[n]) return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("[PlayerNeedsSync] degisken taramasi basarisiz: " + e.Message);
            }
            return false;
        }

        /// <summary>
        /// SATIR OVERRIDE: 'smp globals' çıktısına göre tüm PlayerKey*
        /// değişkenleri sürekli 1, açlık/kirlilik/idrar/stres float'larını
        /// da sürekli 0 yazar. SteamTransport/HasRemote koşulu YOK; yerel
        /// oyunum için her kare zorlanır.
        /// </summary>
        public static void ForceLocalValues()
        {
            try
            {
                FsmVariables g = FsmVariables.GlobalVariables;
                if (g == null) return;

                FsmInt[] ints = g.IntVariables;
                if (ints != null)
                {
                    for (int i = 0; i < ints.Length; i++)
                    {
                        FsmInt v = ints[i];
                        if (v == null || v.Name == null) continue;
                        // PlayerKeyFerndale/Gifu/Hayosiko/Home/Ruscko/Satsuma...
                        if (v.Name.StartsWith("PlayerKey", StringComparison.Ordinal)
                            && v.Value != 1) v.Value = 1;
                    }
                }

                FsmFloat[] floats = g.FloatVariables;
                if (floats != null)
                {
                    for (int i = 0; i < floats.Length; i++)
                    {
                        FsmFloat v = floats[i];
                        if (v == null || v.Name == null) continue;
                        // v15: yalniz dökümde GERCEKTEN bulunan global'ler.
                        // Silinen PlayerAllergy / PlayerBerryPickSkill /
                        // PlayerBurns / PlayerMovementSpeed oyunda hic yok
                        // (6548 kayıtta 0 eslesme), dolayisiyla bu kollar
                        // hicbir zaman tetiklenmiyordu.
                        switch (v.Name)
                        {
                            case "PlayerHunger":
                            case "PlayerThirst":
                            case "PlayerStress":
                            case "PlayerFatigue":
                            case "PlayerDirtiness":
                            case "PlayerUrine":
                                if (v.Value != 0f) v.Value = 0f;
                                break;
                            case "PlayerMoney":
                                if (v.Value != 9999999f) v.Value = 9999999f;
                                break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("[PlayerNeedsSync] ForceLocalValues failed: " + e.Message);
            }
        }

        /// <summary>Her kare çağrılır - veri okur, değişim kontrolü yapar, gönderir.</summary>
        public static void Update()
        {
            if (!hasInitialized) Initialize();
            if (!hasInitialized || !SteamTransport.HasRemote) return;

            float now = Time.realtimeSinceStartup;

            // Mevcut veriyi oku
            NeedsData current = ReadCurrentNeeds();

            // Değişim kontrolü (epsilon ile)
            bool changed = HasSignificantChange(lastSentData, current);

            // Periyodik gönderim veya değişim varsa gönder
            if (changed || now >= nextSendTime)
            {
                // GÖNDERİM BAŞARISIZSA `lastSentData` GÜNCELLENMEZ.
                //
                // CANLI HATA (düzeltildi): `lastSentData = current` gönderimin
                // sonucuna bakılmadan yazılıyordu. `SendReliable` paketi
                // reddettiğinde (oturum yok, kanal kapalı) bir sonraki
                // `HasSignificantChange(lastSentData, current)` karşılaştırması
                // "değişiklik yok" der ve o değer bir sonraki değişime kadar
                // hiç iletilmez. SatsumaCoop.FlushPartsCapped'daki olay
                // kaybıyla aynı kalıp.
                //
                // Keepalive yolu aynı korumayı gerektirmez: orada zaten
                // `lastSentData`'ya dokunulmuyor.
                if (SendNeedsData(current)) lastSentData = current;
                nextSendTime = Time.realtimeSinceStartup + SEND_INTERVAL;
            }
            // Keepalive (değişim yoksa da 1 Hz'de bir gönder - packet loss'ta son bilinen değerin gitmesi için)
            else if (now >= nextKeepAliveTime)
            {
                SendNeedsData(current);
                nextKeepAliveTime = Time.realtimeSinceStartup + KEEPALIVE_INTERVAL;
            }

            lastKnownData = current;
        }

        static NeedsData ReadCurrentNeeds()
        {
            NeedsData data = new NeedsData();

            if (needsFsm != null)
            {
                data.Bladder = varBladder != null ? varBladder.Value : 0f;
                data.Hunger = varHunger != null ? varHunger.Value : 0f;
                data.Thirst = varThirst != null ? varThirst.Value : 0f;
                data.Fatigue = varFatigue != null ? varFatigue.Value : 0f;
                data.Stress = varStress != null ? varStress.Value : 0f;
                data.Dirtiness = varDirtiness != null ? varDirtiness.Value : 0f;
            }

            return data;
        }

        static bool HasSignificantChange(NeedsData a, NeedsData b)
        {
            const float EPSILON = 0.5f; // 0.5 birim değişim eşiği
            return Math.Abs(a.Bladder - b.Bladder) > EPSILON ||
                   Math.Abs(a.Hunger - b.Hunger) > EPSILON ||
                   Math.Abs(a.Thirst - b.Thirst) > EPSILON ||
                   Math.Abs(a.Fatigue - b.Fatigue) > EPSILON ||
                   Math.Abs(a.Stress - b.Stress) > EPSILON ||
                   Math.Abs(a.Dirtiness - b.Dirtiness) > EPSILON;
        }

        static bool SendNeedsData(NeedsData data)
        {
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.PlayerNeeds);
                BinaryWriter w = new BinaryWriter(s);

                w.Write(data.Bladder);
                w.Write(data.Hunger);
                w.Write(data.Thirst);
                w.Write(data.Fatigue);
                w.Write(data.Stress);
                w.Write(data.Dirtiness);

                // DÖNÜŞ DEĞERİ: `SteamTransport.SendReliable` Steam paketi
                // reddettiğinde (oturum yok / kanal kapalı) false döner.
                // Çağıran taraf bunu kullanır (bkz. Update).
                if (SteamTransport.SendReliable(Protocol.Finish(s)))
                {
                    Telemetry.SentPlayerNeeds++;
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                SmpLog.Warning("[PlayerNeedsSync] Gönderim hatası: " + e.Message);
                return false;
            }
        }

        /// <summary>Gelen PlayerNeeds paketini işler (diğer oyuncunun verisi).</summary>
        public static void OnMessage(BinaryReader r)
        {
            try
            {
                NeedsData data = new NeedsData
                {
                    Bladder = r.ReadSingle(),
                    Hunger = r.ReadSingle(),
                    Thirst = r.ReadSingle(),
                    Fatigue = r.ReadSingle(),
                    Stress = r.ReadSingle(),
                    Dirtiness = r.ReadSingle()
                };

                Telemetry.ReceivedPlayerNeeds++;
                ApplyToRemotePlayer(data);
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[PlayerNeedsSync] Paket işleme hatası: " + e.Message);
            }
        }

        /// <summary>
        /// Karşı tarafın ihtiyaç değerlerini SAKLAR.
        ///
        /// NEDEN FSM'e YAZILMIYOR: uzak oyuncu kuklası (RemotePlayerBehaviour)
        /// bir FBX asset bundle'ından yüklenen SAF BİR GÖRÜNÜŞ nesnesidir;
        /// üzerinde PlayMakerFSM YOKTUR, dolayısıyla "Değer"/"PlayerState"
        /// FSM'i bulunamaz. Eski kod her paket geldiğinde kukla altında
        /// FSM arıyor, bulamayıp sessizce çıkıyordu (kare başına
        /// GetComponentsInChildren = tam sahne maliyeti).
        ///
        /// Doğru yer HUD'dur: karşı tarafın açlığı/susuzluğu kendi
        /// ekranında görünür, bizim oyuncumuzun FSM'i değişmez.
        /// </summary>
        static NeedsData remoteNeeds;
        static bool hasRemoteNeeds;
        static ulong remoteNeedsSteamId;

        /// <summary>Karşı tarafın son bilinen ihtiyaç durumu.</summary>
        public static NeedsData RemoteNeeds { get { return remoteNeeds; } }
        public static bool HasRemoteNeeds { get { return hasRemoteNeeds; } }
        public static ulong RemoteSteamId { get { return remoteNeedsSteamId; } }

        static void ApplyToRemotePlayer(NeedsData data)
        {
            remoteNeeds = data;
            hasRemoteNeeds = true;
            remoteNeedsSteamId = SteamTransport.RemoteId.m_SteamID;
        }



        /// <summary>Sahne değişiminde temizle.</summary>
        public static void Clear()
        {
            hasInitialized = false;
            needsFsm = null;
            needsPath = "";
            scannedFsms = 0;
            varBladder = varHunger = varThirst = varFatigue = varStress = varDirtiness = null;
            remoteNeeds = new NeedsData();
            hasRemoteNeeds = false;
        }

        public static string Describe()
        {
            return "PlayerNeeds: initialized=" + hasInitialized
                + ", bound=" + (varHunger != null ? "Y" : "n")
                + (varThirst != null ? "Y" : "n")
                + (varStress != null ? "Y" : "n")
                + (varFatigue != null ? "Y" : "n")
                + (varDirtiness != null ? "Y" : "n")
                + (varBladder != null ? "Y" : "n");
        }
    }
}
