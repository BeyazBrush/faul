using System;
using System.IO;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Host kopması TESPİTİ ve teşhisi.
    ///
    /// GERÇEK DURUM: bu mod iki oyunculu bir oturumdur (lobi "2 players max").
    /// Host ayrıldığında oturumda başka kimse kalmaz, dolayısıyla devralacak
    /// bir client YOKTUR. Steam P2P oturumunun sahipliği de taşınamaz: kablo
    /// ölü host'un oturumuna bağlıdır.
    ///
    /// ESKİ UYGULAMA NEDEN YANLIŞTI: client, host düştüğünde
    /// <c>SteamTransport.CurrentMode = Host</c> yazıyordu. Bu yalnız yerel
    /// bir bayrak değişikliği; gönderimler hâlâ ölü host'un oturumuna
    /// gidiyordu. Sonuç: mod kendini host sanır, hiçbir paket gitmez, oyuncu
    /// "dondu" izlenimi alır. Tespit bile çalışmıyordu, çünkü migration nabzı
    /// <c>Msg.Heartbeat</c> üzerinden gönderiliyordu; SteamTransport o
    /// mesajı asla <c>OnMessage</c>'e geçirmez, yani karşı taraftaki
    /// <c>OnHeartbeat</c> hiç çalışmıyordu.
    ///
    /// ŞU ANKİ DAVRANIŞ: host'un sessiz kaldığını 3 saniyede fark eder,
    /// kullanıcıya durumu ve kayıp olan şeyi açıkça söyler, oturumu
    /// kapatır. Yanlış bir "başarılı migration" göstermektense dürüst bir
    /// hata mesajı iyidir.
    /// </summary>
    public static class HostMigration
    {
        // --- Ayarlar ---
        /// <summary>Bu süre boyunca nabız gelmezse host kaybolmuş sayılır.</summary>
        public const float SILENCE_LIMIT = 3f;
        /// <summary>Aynı uyarının tekrarlanma aralığı (log kirliliği olmasın).</summary>
        public const float MIGRATION_COOLDOWN = 30f;

        // --- Durum ---
        enum MigrationState { Idle, Detecting, Failed }
        static MigrationState state = MigrationState.Idle;
        static float stateStartTime;
        static float lastHeartbeatSent;
        static float lastHeartbeatReceived;
        static float lastWarningTime;
        static CSteamID previousHost;
        static bool sessionStarted;
        static int lastSnapshotPartCount;
        static int lastSnapshotVehicleCount;

        // --- Public API ---

        public static void Initialize()
        {
            previousHost = SteamTransport.IsHost
                ? SteamUser.GetSteamID()
                : SteamTransport.RemoteId;
            lastHeartbeatReceived = Time.realtimeSinceStartup;
            lastHeartbeatSent = lastHeartbeatReceived;
            sessionStarted = true;
            state = MigrationState.Idle;
            SmpLog.Debug("migration", "init", "mevcut host: " + previousHost);
        }

        public static void Update()
        {
            // Oturum hiç kurulmadıysa (menü, tek başına) hiçbir şey yapma.
            if (!sessionStarted) return;
            if (!SteamTransport.SteamReady) return;

            float now = Time.realtimeSinceStartup;

            if (SteamTransport.IsHost)
            {
                if (now - lastHeartbeatSent > SILENCE_LIMIT / 3f)
                {
                    SendHeartbeat();
                    lastHeartbeatSent = now;
                }
                return;
            }

            // Nabız 3 saniye gelmediyse host kaybolmuş demektir.
            if (now - lastHeartbeatReceived > SILENCE_LIMIT)
            {
                if (state == MigrationState.Idle && now - lastWarningTime > MIGRATION_COOLDOWN)
                {
                    StartDetecting();
                }
            }
            else if (state == MigrationState.Detecting)
            {
                // Host geri döndü: her şey normal.
                state = MigrationState.Idle;
                SmpLog.Log("[HostMigration] host yanıt vermeye devam ediyor, durum normal.");
            }
        }

        static void StartDetecting()
        {
            state = MigrationState.Detecting;
            stateStartTime = Time.realtimeSinceStartup;
            lastWarningTime = Time.realtimeSinceStartup;

            SmpLog.Warning("[HostMigration] host'tan " + (int)SILENCE_LIMIT
                + " saniyedir yanıt yok.");
            SmpLog.Warning("[HostMigration] bu oturum 2 kişilik olduğu için "
                + "devralacak client yok; oturum kapanıyor.");
            SmpLog.Warning("[HostMigration] bilinen durum: " + lastSnapshotPartCount
                + " parça, " + lastSnapshotVehicleCount + " araç. "
                + "Devam etmek için yeni bir lobi aç ve davet et.");
            SmpLog.Debug("migration", "lost", "parça=" + lastSnapshotPartCount
                + " araç=" + lastSnapshotVehicleCount);

            state = MigrationState.Failed;
            sessionStarted = false;
        }

        /// <summary>
        /// Host'un "ben hâlâ buradayım" sinyali.
        ///
        /// <see cref="Protocol.Msg.Heartbeat"/> SteamTransport'ın kendi
        /// nabzıdır (ulong clientClock + bool hasWorld) ve ASLA
        /// OnMessage'e geçirilmez; onun yerine ayrı bir mesaj kullanılır.
        /// </summary>
        static void SendHeartbeat()
        {
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.MigrationHeartbeat);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(SteamTransport.NetClockMs());
                SteamTransport.SendUnreliableNoDelay(Protocol.Finish(s));
            }
            catch (Exception e)
            {
                SmpLog.Warning("[HostMigration] nabız gönderilemedi: " + e.Message);
            }
        }

        /// <summary>Gelen migration nabzı: "hâlâ oradayım" bilgisi.</summary>
        public static void OnHeartbeat(BinaryReader reader)
        {
            // Zaman damgasını okumak zorunlu değil; nabzın varlığı yeterli.
            // Okuma başarısız olursa yine de "host yaşıyor" sayılır: saat
            // alanı bozulsa bile kopma tespiti çalışmaya devam etmeli.
            lastHeartbeatReceived = Time.realtimeSinceStartup;
            try { reader.ReadUInt64(); }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[HostMigration] nabız paketi bozuk: " + e.Message);
            }
        }

        /// <summary>
        /// Durum özeti. Host kaybolduğunda kullanıcıya "ne kayboldu" bilgisini
        /// verebilmek için sayıları biriktirir.
        /// </summary>
        public static void NoteSessionState()
        {
            try
            {
                lastSnapshotPartCount = SatsumaRegistry.PartCount;
                lastSnapshotVehicleCount = VehicleRegistry.Vehicles.Count;
            }
            catch (Exception e)
            {
                SmpLog.Warning("[HostMigration] oturum özeti alınamadı: " + e.Message);
            }
        }

        public static void Clear()
        {
            state = MigrationState.Idle;
            sessionStarted = false;
            lastWarningTime = 0f;
            lastSnapshotPartCount = 0;
            lastSnapshotVehicleCount = 0;
            previousHost = new CSteamID(0);
        }

        public static bool IsMigrating { get { return state != MigrationState.Idle; } }

        public static string Describe()
        {
            return "host-kopma tespiti: state=" + state
                + ", son nabız " + (int)(Time.realtimeSinceStartup - lastHeartbeatReceived) + "s önce";
        }
    }
}