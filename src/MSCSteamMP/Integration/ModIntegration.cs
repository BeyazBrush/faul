using System;
using System.Collections.Generic;
using System.IO;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Tüm yeni sistemlerin entegrasyon noktası.
    /// Bu sınıf SteamMPBehaviour.Update içinden çağrılır.
    /// </summary>
    public static class ModIntegration
    {
        static bool initialized = false;
        static float lastLogTime;

        // --- Initialize ---

        /// <summary>
        /// Oyun başlangıcında (veya save yüklendiğinde) çağrılır.
        /// </summary>
        public static void OnWorldReady()
        {
            if (initialized) return;
            initialized = true;

            SmpLog.Log("[ModIntegration] Sistemler başlatılıyor...");

            // 1. Senkronizasyon sistemleri
            AIVehicleSync.Discover();
            PlayerNeedsSync.Initialize();
            ItemSpawnSync.Clear();
            TextChat.Initialize();
            HUDEnhancements.Initialize();
            HostMigration.Initialize();
            // MWCoop yöntemi: genel ayna katmanı (tablo + relay + replay + yetki).
            // Varsayılan gönderimsizdir (Track ile açılır), mevcut sistemlerle
            // çakışmaz; Discover yalnızca tabloyu kurar.
            MirrorSync.Discover();

            // 2. Event abonelikleri
            SubscribeToEvents();

            SmpLog.Debug("integration", "ready", "yeni modüller hazır");
        }

        static void SubscribeToEvents()
        {
            // SteamTransport event'leri
            SteamTransport.OnPeerConnected += OnPeerConnected;
            SteamTransport.OnPeerDisconnected += OnPeerDisconnected;
            
            // Yeni mesajlar SteamMPBehaviour.Dispatch içinden buraya yönlendirilir
            // (Dispatch'te açık bir çağrı var; event aboneliği yerine bu, tek
            //  giriş noktası olduğu anlamına gelir ve kaçırılma riski taşımaz).
        }

        // --- Update Loop ---

        /// <summary>
        /// Her kare SteamMPBehaviour.Update içinden çağrılır.
        /// </summary>
        public static void Update()
        {
            if (!initialized) return;

            // 1. AI araçlar: host gönderir, client interpolasyon uygular.
            if (SteamTransport.IsHost) AIVehicleSync.HostUpdate();
            AIVehicleSync.ClientInterpolate();

            // 2. Oyuncu ihtiyaçları (1 Hz, yalnız değişince ek olarak)
            PlayerNeedsSync.Update();

            // 3. Host kopma tespiti (host kaybolduğunda kullanıcıya dürüstçe bildirir)
            HostMigration.Update();

            // 4. HUD (ping + isim etiketleri)
            HUDEnhancements.Update();

            // 5. Sohbet
            TextChat.Update();

            // 5b. MWCoop yöntemi: genel ayna tick'i (20/10/1 Hz kademeler +
            // host recale + periyodik full + kopya interpolasyonu).
            MirrorSync.Tick();

            // 6. GC istatistikleri (10 sn'de bir; yalnız verbose iken yazar)
            if (Time.realtimeSinceStartup - lastLogTime > 10f)
            {
                GCOptimizer.UpdateGCStats();
                lastLogTime = Time.realtimeSinceStartup;
            }

            // NOT: SatsumaCoop.Tick() BURADA ÇAĞRILMAZ. SteamMPBehaviour
            // onu zaten her karede kendi Guard'ı içinde çağırıyor; iki kez
            // çağırmak aynı değişikliği iki kez gönderirdi.
        }

        // --- Protocol Message Routing ---

        /// <summary>
        /// Gelen pakette bu modülün mesajı mı diye bakar.
        ///
        /// SteamMPBehaviour.Dispatch bunu switch'ten ÖNCE çağırır ve
        /// <c>true</c> dönerse mesaj işlenmiş sayılır. Ayrı switch case'leri
        /// kullanmamamızın nedeni: yeni mesajlar burada unutulup
        /// "unhandled message" olarak düşüyordu.
        /// </summary>
        public static bool TryHandle(Protocol.Msg msgType, BinaryReader reader)
        {
            if (!IsHandled(msgType)) return false;
            OnProtocolMessage(msgType, SteamTransport.RemoteId, reader);
            return true;
        }

        /// <summary>Bu modülün sahip olduğu mesaj tipleri.</summary>
        static bool IsHandled(Protocol.Msg id)
        {
            // v15: SaveGameChunk / SaveGameComplete ARTIK SAHİPLENMİYOR.
            // Önceden IsHandled bunları iddia ediyordu ama gövde bilinçli
            // bir no-op'tu: TryHandle true dönüp paketi SESSİZCE yutuyordu,
            // yani akış "işlendi" görünüyordu. Artık IsHandled false dönüyor
            // ve SteamMPBehaviour `default:` koluna düşüp gerçeği logluyor.
            return id == Protocol.Msg.AIVehicleState
                || id == Protocol.Msg.PlayerNeeds
                || id == Protocol.Msg.ItemSpawn
                || id == Protocol.Msg.TextChat
                || id == Protocol.Msg.ChatTyping
                || id == Protocol.Msg.HostMigration
                || id == Protocol.Msg.MigrationHeartbeat
                || id == Protocol.Msg.Ping
                // MWCoop yöntemi: genel ayna mesajları (84/85/86). IsVehicleOrPlayer
                // bunları sahiplenmez, IsInteraction'a da girmezler; tek okuyucu
                // burasıdır (çift-okuyucu akış kaydırır).
                || id == Protocol.Msg.MirrorState
                || id == Protocol.Msg.MirrorEvent
                || id == Protocol.Msg.MirrorSim;
        }

        static void OnProtocolMessage(Protocol.Msg msgType, CSteamID sender, BinaryReader reader)
        {
            try
            {
                switch (msgType)
                {
                    // SaveGameChunk / SaveGameComplete BILINCLI OLARAK BOS.
                    // Gerekce: aktarim bir zamanlar "82546 byte gonderiliyor"
                    // diye basliyordu ama HostUpdate() HICBIR YERDEN
                    // POMPALANMIYORDU, yani hicbir parca gitmiyordu; buna
                    // karsilik alma tarafi da hicbir sey almiyordu. Log
                    // "gonderimi basladi" yazip sessizce oluyordu ve iki
                    // taraf farkli kayitlarla birbirine sokuluyordu.
                    // Aktarim kaldirildi; uyusmazlik ApplySyncDone icinde
                    // ACIKÇA ve KULLANILABILIR sekilde bildiriliyor.

                    // --- AI Vehicle ---
                    case Protocol.Msg.AIVehicleState:
                        AIVehicleSync.OnMessage(reader);
                        break;

                    // --- Player Needs ---
                    case Protocol.Msg.PlayerNeeds:
                        PlayerNeedsSync.OnMessage(reader);
                        break;

                    // --- Item Spawn ---
                    case Protocol.Msg.ItemSpawn:
                        ItemSpawnSync.OnMessage(reader);
                        break;

                    // --- Text Chat ---
                    case Protocol.Msg.TextChat:
                        TextChat.OnMessage(reader);
                        break;
                    case Protocol.Msg.ChatTyping:
                        TextChat.OnTyping(reader);
                        break;

                    // --- Host Migration ---
                    case Protocol.Msg.HostMigration:
                        SmpLog.Warning("[ModIntegration] eşzamanlı host migration "
                            + "bildirimi alındı; Steam P2P oturum devri yapılamaz.");
                        break;
                    case Protocol.Msg.MigrationHeartbeat:
                        HostMigration.OnHeartbeat(reader);
                        break;

                    

                    // --- Ping ---
                    // Tek paket iki işe yarar: içeriği kendi gönderdiğimiz
                    //  NetClockMs ise karşı tarafın bize gönderdiği bir
                    //  yankıdır. Nasıl ayırt edileceği paketin tek başına
                    //  mümkün değil; bu yüzden karşı tarafın gönderdiği
                    //  saat hedefe yakınsa yankı, değilse istek sayılır.
                    case Protocol.Msg.Ping:
                        OnPing(reader);
                        break;

                    // --- MWCoop yöntemi: genel ayna (tek okuyucu) ---
                    case Protocol.Msg.MirrorState:
                    case Protocol.Msg.MirrorEvent:
                    case Protocol.Msg.MirrorSim:
                        MirrorSync.OnMessage(msgType, reader);
                        break;
                }
            }
            catch (Exception e)
            {
                SmpLog.Error("[ModIntegration] Mesaj işleme hatası (" + msgType + "): " + e.Message);
                Telemetry.BadPackets++;
            }
        }

        /// <summary>
        /// Ping paketi: istek mi yankı mı ayrımı yapıp doğru işleyiciye gider.
        ///
        /// Paket tek bir <c>ulong</c> taşır. Değer kendi saatimize çok yakınsa
        /// bu bir YANKI'dır (karşı taraf bizim isteğimizi geri yansıttı); değilse
        /// karşı taraf kendi saatini gönderiyor demektir, yani İSTEK'tir.
        /// Eşik: 30 saniye. Saat kayması bu kadarına kadar normaldir.
        /// </summary>
        static void OnPing(BinaryReader reader)
        {
            ulong stamp = reader.ReadUInt64();
            ulong now = SteamTransport.NetClockMs();

            ulong diff = now > stamp ? now - stamp : stamp - now;
            if (diff < PING_ECHO_WINDOW_MS)
                HUDEnhancements.OnPingResponse(stamp);
            else
                HUDEnhancements.OnPingRequest(stamp);
        }

        /// <summary>30 sn: bu pencereden küçük fark = kendi saatimizin yankısı.</summary>
        const ulong PING_ECHO_WINDOW_MS = 30000;

        // --- Event Handlers ---

        static void OnPeerConnected()
        {
            SmpLog.Log("[ModIntegration] Peer bağlandı: " + SteamTransport.RemoteId);

            // 1. Kayit uyusmazligi.
            //
            // ONCEDEN BURADA "host kaydini 82546 byte olarak gonderiyor"
            // yaziliyordu. O GONDERIM HICBIR ZAMAN GERCEKLESMIYORDU:
            // SaveGameTransfer.HostUpdate() hicbir yerden cagrilmiyordu,
            // yani ilk chunk bile gonderilmiyordu. Log yine de "gonderimi
            // basladi" yaziyor, sonra hicbir sey olmuyordu. Sonuc: iki taraf
            // KENDI KAYITLARINDA kalip parca/vida tablolari farkli kaliyor
            // (canli olcum: host 218 girdi / 297 vida, client 216 / 301) ve
            // civalar yolla eslesemedigi icin yanlis civata sikiliyor.
            //
            // Simdilik aktarim YOK; yerine ne yapilmasi gerektigini soyle.
            SmpLog.Log("[ModIntegration] " + (SteamTransport.IsHost ? "host" : "client")
                + ": iki taraf AYNI KAYDI yuklemeli. Kayit aktarimi yapilmaz"
                + " (canli bir dunyayi oturum ortasinda degistirmek oyunu"
                + " kararsizlastirir). Farkli kayit + farkli bolt sayisi"
                + " = yanlis vidaya mudahale.");

            // 2. Host kopma tespitini başlat
            HostMigration.Initialize();

            // 3. JIP Full State Snapshot isteği (yalnız client)
            if (!SteamTransport.IsHost && InteractionManager.JIP != null)
                InteractionManager.JIP.RequestFullSnapshot();

            // 3. HUD Enhancements - peer kaydet
            HUDEnhancements.RegisterPlayer(SteamTransport.RemoteId.m_SteamID, RemotePlayerSync.PeerName);

            // 3b. MWCoop yöntemi: host JIP full iter (yetkisindekilerin son hali).
            try { MirrorSync.OnPeerConnected(); }
            catch (Exception e) { SmpLog.Warning("[Mirror] peer-join full failed: " + e.Message); }
            
            // 4. Text Chat - oturuma katılma bildirimi (kanal: sistem)
            TextChat.AddSystemMessage("Peer bağlandı: " + RemotePlayerSync.PeerName);
        }

        static void OnPeerDisconnected()
        {
            SmpLog.Log("[ModIntegration] Peer ayrıldı: " + SteamTransport.RemoteId);

            ulong peerId = SteamTransport.RemoteId.m_SteamID;

            // Cleanup
            AIVehicleSync.Clear();
            HUDEnhancements.UnregisterPlayer(peerId);
            ItemSpawnSync.Clear();
            HostMigration.Clear();
            // MWCoop yöntemi: uzak kopyalar fiziğe döner (kilitli kinematik kalmasın).
            try { MirrorSync.OnPeerDisconnected(); } catch { }
            // Peer gitti: karşı tarafın ihtiyaç/health verisi artık geçersiz,
            // HUD'da yanlış değerde kalmasın.
            PlayerNeedsSync.Clear();
        }

        

        // --- Cleanup ---

        public static void OnSceneChange()
        {
            SmpLog.Log("[ModIntegration] Sahne değişimi - sistemler temizleniyor");

            // Event abonelikleri ONCE kaldırılır: Clear() içindeki geri
            // çağrılar yeniden sahne keşfi tetikleyip yeni bir abonelik
            // zinciri kurmasın.
            UnsubscribeFromEvents();

            AIVehicleSync.Clear();
            ItemSpawnSync.Clear();
            TextChat.Clear();
            HUDEnhancements.Clear();
            HostMigration.Clear();
            // MWCoop yöntemi: ayna tablosu/yetki/kanca sahipliği sahneyle ölür.
            try { MirrorSync.Clear(); } catch { }
            // PlayerNeedsSync sahne nesnesi tutuyor (PLAYER altındaki FSM'ler).
            PlayerNeedsSync.Clear();

            initialized = false;
        }

        static void UnsubscribeFromEvents()
        {
            SteamTransport.OnPeerConnected -= OnPeerConnected;
            SteamTransport.OnPeerDisconnected -= OnPeerDisconnected;
        }

        public static void Clear()
        {
            OnSceneChange();
        }

        public static string Describe()
        {
            return "yeni modüller: " + (initialized ? "aktif" : "kapalı")
                + " | " + MirrorSync.Describe();
        }
    }
}