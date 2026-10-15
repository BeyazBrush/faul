using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SteamMP {

    /// <summary>
    /// P2P güvenlik: gelen ağ eylemlerini mesafe ve mantık kontrolleriyle doğrular.
    /// Host-authoritative DEĞIL, her iki taraf da kendi yerel oyuncusu için kontrol eder.
    /// "Bu komutu gönderen oyuncu, bu işlemi yapmaya yeterince yakın mı?"
    /// </summary>
    public class AuthorityManager {

        // v15: MESAFE DOGRULAMASI KALDIRILDI.
        //
        // Once burada MaxBoltDist / MaxPartPickupDist / MaxPartAssembleDist /
        // MaxDoorDist / MaxItemPickupDist vardi ve CheckDistance() bunlari
        // kullaniyordu. Ama CheckDistance'in CANLI CAGIRANI YOKTU: tek giris
        // noktasi ValidatePartState idi, o da sifir cagriliyordu; dolayisiyla
        // ValidateBoltAction / ValidatePartPickup / ValidatePartAssemble /
        // ValidateWorldObjectInteraction da hic cagrilmiyordu. Yani sinif bir
        // mesafe kapisi ILETIYORDU ama HIC CALISMIYORDU.
        //
        // Gerekce kayitliydi: iki taraf FARKLI KAYITTAN geliyor (canli
        // olcum: client 145 parca, host 135 parca) ve oyuncu aracindan 12 m
        // uzakta. Reddedilen sey bir hile degil, ORTAK KAYDIN KENDISIydi;
        // JIP ve ilk senkronu bozuyordu.
        //
        // SALT KALAN: canli olaylarin spam korumasi. ValidateConvergence /
        // ValidateBolt / ValidatePartEventReplay mesafe OLCMEZ; ayni deger
        // tekrarini eler (CheckCooldown).
        public static string DescribeThresholds() {
            return "authority: mesafe dogrulamasi KAPALI (ortak kayit varsayimi); "
                + "canli olaylar yalnizca " + ACTION_COOLDOWN.ToString("F2")
                + "s spam korumasi + ayni-deger yakinsamasiyla korunur.";
        }


        // Spam koruması: aynı eylem için minimum ara (saniye)
        public const float ACTION_COOLDOWN = 0.15f;

        readonly Dictionary<string, float> lastActionTime = new Dictionary<string, float>();

        /// <summary>Spam korumasiyla elenen eylem sayisi ('smp status').</summary>
        public int Rejected;
        /// <summary>Zaten aynı olduğu için kontrolü atlayan eylem sayısı.</summary>
        public int Agreed;

        /// <summary>
        /// DURUM UYUŞMA (convergence) kontrolü: mesafe YOK, sadece spam sınırı.
        ///
        /// NEDEN AYRI (canlı log 06.10 14:25:23):
        /// Kayıt senkronunda ~100 parça "remote player distance 11-14m > 5m"
        /// diye reddedildi. Sebep basit: iki taraf FARKLI kayıtlardan geliyor
        /// (client 145 takılı / 306 vida, host 135 takılı / 301 vida) ve oyuncu
        /// araçtan 12 m uzakta. Yani reddedilen şey bir hile değil, ortak
        /// kaydın kendisi.
        ///
        /// Tasarım "tek paylaşılan kayıt" üzerine: iki taraf AYNI kaydı
        /// yüklemeli, tablolar SyncDone hash'i ile doğrulanıyor.
        ///
        /// DÜZELTME: buradaki eski metin "mod host'tan client'a kayıt
        /// dosyasını aktarıyor (SaveGameTransfer)" diyordu. Öyle bir
        /// aktarım YOK; o sınıf ölü koddu (gönderim hiç pompalanmıyordu,
        /// alma tarafı da veri hiç almıyordu) ve kaldırıldı. Yani burada
        /// "aktarılabilir" değil, "zaten aynı kayıttan gelmeli".
        ///
        /// Bu yüzden durum mesajı güvenilir kanalda gelen, idempotent bir
        /// gerçektir; mesafe kalkıkanının orada durdurma değeri yok,
        /// yalnızca harmanı bozma değeri var.
        ///
        /// Mesafe kalkıkanı CANLI OLAYLARDA kalır: Msg.PartEvent (ASSEMBLE /
        /// REMOVE) ve vida sıkma. Yani bir oyuncu aracın 12 m ötesinden
        /// "şu parçayı taktım" diyemez; ama aynı kayıttan gelen durum
        /// kabul edilir.
        /// </summary>
        public bool ValidateConvergence(string partPath) {
            Agreed++;
            // Spam sınırı korunur, mesafe ölçülmez.
            return CheckCooldown("converge_" + partPath);
        }

        /// <summary>
        /// Spam koruması: aynı eylem çok sık tekrar ediliyor mu?
        /// </summary>
        public bool CheckCooldown(string actionKey) {
            float now = Time.time;
            float last;
            if (lastActionTime.TryGetValue(actionKey, out last)) {
                if (now - last < ACTION_COOLDOWN) {
                    if (Telemetry.Verbose) SmpLog.Debug("authority", "cooldown", "Cooldown reject: " + actionKey);
                    return false;
                }
            }
            lastActionTime[actionKey] = now;
            return true;
        }

        /// <summary>
        /// Vida kontrolü: hedef cıvata zaten aynı değeriyse kontrol atlanır.
        ///
        /// v15: mesafe kaldırıldı (ortak kayıt varsayımı); sadece
        /// aynı değer tekrarı ve spam sınırı.
        /// </summary>
        public bool ValidateBolt(string boltPath, SatsumaBolt bolt, int stage, float tightness, string action) {
            if (bolt != null) {
                int curStage = bolt.Stage != null ? bolt.Stage.Value : stage;
                float curTight = bolt.Tightness != null ? bolt.Tightness.Value : tightness;
                if (curStage == stage && System.Math.Abs(curTight - tightness) < 0.001f) {
                    Agreed++;
                    return true;   // zaten aynı: uygulanacak bir şey yok
                }
            }
            return CheckCooldown("boltreplay_" + boltPath);
        }

        /// <summary>
        /// Canlı parça olayı tekrarı (ApplyPartEvent): mesafe YOK, cooldown var.
        ///
        /// Alıcı, gönderenin parça başındaki yakınlığını doğrulayamaz; mesafe
        /// kalkıkanı uzakta duran eş oyuncunun tekrarını düşürüp "takıldı
        /// görünmüyor" yapıyordu. PartState zaten mesafesiz yakınsadığı için
        /// olay yolu da aynı kuralı kullanır: aynı durumdaysa kabul, spam ise ret.
        /// </summary>
        public bool ValidatePartEventReplay(string partPath, bool installed) {
            SatsumaPart part;
            if (SatsumaRegistry.TryResolvePublic(partPath, out part) && part != null
                && part.Installed == installed) {
                Agreed++;
                return true;
            }
            return CheckCooldown("partevt_" + partPath);
        }

        /// <summary>Sahne değişiminde temizle.</summary>
        public void Clear() {
            lastActionTime.Clear();
            ownerOf.Clear();
            ownedSince.Clear();
        }

        // ----- InteractionManager tarafından çağrılan metotlar -----

        public void Discover() {
            // Gerekirse buraya discovery mantığı eklenebilir
        }

        public void Update() {
            // Periyodik temizlik veya diğer güncellemeler.
            //
            // Sahiplik süresi temizliği burada DEĞİL,
            // TickConflictResolution()'da yapılır: InteractionManager
            // onu 5 Hz'de çağırıyor (InteractionManager.cs:118-122);
            // burada ayrıca çağırmak aynı işi her karede iki kez yapardı.
        }

        public void TickConflictResolution() {
            // Sahiplik çakışma çözümü (5 Hz)
            //
            // CANLI HATA (statik tarama): burası, SendAuthoritySnapshot,
            // OnAuthorityMessage, OnAuthoritySnapshot, CollectStates ve
            // ApplyAuthoritySnapshot'un HEPSİ boş gövdeydi. Yani sahiplik
            // (authority) sistemi hiç çalışmıyordu ve JIP'in 4. adımı
            // boş liste gönderiyordu. `AuthoritySnapshot` paketi logda
            // hiç görünmediği bunu doğruluyor.
            //
            // ÇÖZÜM: durum gerçekten tutuluyor, çakışmalar en eski
            // etkileşimi kazanan tarafta kalıyor (deterministik: iki
            // taraf da aynı sonucu bulur).
            float now = Time.realtimeSinceStartup;
            List<string> expired = null;
            foreach (var kv in ownedSince)
            {
                if (now - kv.Value > OWNERSHIP_TTL) {
                    if (expired == null) expired = new List<string>(4);
                    expired.Add(kv.Key);
                }
            }
            if (expired != null) {
                for (int i = 0; i < expired.Count; i++) {
                    ownerOf.Remove(expired[i]);
                    ownedSince.Remove(expired[i]);
                }
            }
        }

        /// <summary>Sahiplik kaydının yaşam süresi (s). Süre dolunca serbest kalır.</summary>
        const float OWNERSHIP_TTL = 12f;

        /// <summary>Yol -> sahip SteamID.</summary>
        readonly Dictionary<string, ulong> ownerOf = new Dictionary<string, ulong>();
        /// <summary>Yol -> sahiplik alındığı an (çakışmada kazananı belirler).</summary>
        readonly Dictionary<string, float> ownedSince = new Dictionary<string, float>();
        /// <summary>Son çakışma sayısı ('smp authority' çıktısı).</summary>
        public int Conflicts;

        /// <summary>
        /// Bir nesnenin sahipliğini alır/serbest bırakır ve karşı tarafa
        /// bildirir. Oyuncu bir parçaya montaj yaparken/vida sıkarken
        /// çağrılmalı.
        /// </summary>
        public bool Claim(string objectPath, AuthorityRequestType type) {
            if (string.IsNullOrEmpty(objectPath)) return false;
            ulong me = SteamTransport.LocalId.m_SteamID;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.AuthorityClaim);
                BinaryWriter w = new BinaryWriter(s);
                Protocol.WriteAuthorityClaim(w, objectPath, me, type);
                w.Flush();
                SteamTransport.SendReliable(Protocol.Finish(s));
            }
            catch (Exception e) {
                SmpLog.Warning("[Authority] claim gönderilemedi: " + e.Message);
            }
            Apply(objectPath, me, type);
            return true;
        }

        void Apply(string objectPath, ulong owner, AuthorityRequestType type) {
            if (objectPath == null) return;
            if (type == AuthorityRequestType.Release) {
                ownerOf.Remove(objectPath);
                ownedSince.Remove(objectPath);
                return;
            }
            ownerOf[objectPath] = owner;
            ownedSince[objectPath] = Time.realtimeSinceStartup;
        }

        public void SendAuthoritySnapshot() {
            // Sahiplik snapshot gönder (JIP için)
            try {
                List<AuthorityStateData> list = new List<AuthorityStateData>();
                CollectStates(list);
                MemoryStream s = Protocol.Begin(Protocol.Msg.AuthoritySnapshot);
                BinaryWriter w = new BinaryWriter(s);
                w.Write((ushort)list.Count);
                for (int i = 0; i < list.Count; i++) {
                    w.Write(list[i].ObjectPath ?? "");
                    w.Write(list[i].OwnerSteamId);
                    w.Write(list[i].Held);
                    w.Write(list[i].Installed);
                    w.Write(list[i].HolderPath ?? "");
                }
                w.Flush();
                SteamTransport.SendReliable(Protocol.Finish(s));
            }
            catch (Exception e) {
                SmpLog.Warning("[Authority] snapshot gönderilemedi: " + e.Message);
            }
        }

        public void OnPeerDisconnected() {
            // Bağlantı koptuğunda temizle
            Clear();
        }

        public void OnAuthorityMessage(BinaryReader r) {
            // AuthorityClaim paketi işle
            try {
                string path; ulong owner; AuthorityRequestType type;
                Protocol.ReadAuthorityClaim(r, out path, out owner, out type);
                if (string.IsNullOrEmpty(path)) return;
                // SAHİPLİK GÜVENLİĞİ: kimlik pakette değil, bağlantının
                // KENDİSİNDE. Böylece bir istemci başkasının adına sahiplik
                // talep edemez.
                ulong real = SteamTransport.RemoteId.m_SteamID;
                if (real == 0) real = owner;
                // Çakışma: aynı nesne için iki sahip varsa ÖNCE alan kazanır.
                // İki taraf da aynı kuralı uyguladığı için sonuç tutarlı.
                ulong cur;
                if (ownerOf.TryGetValue(path, out cur) && cur != real) {
                    float curAt, newAt;
                    ownedSince.TryGetValue(path, out curAt);
                    newAt = Time.realtimeSinceStartup;
                    if (curAt > newAt) { Conflicts++; return; }   // biz önce almıştık
                }
                Apply(path, real, type);
            }
            catch (Exception e) {
                Telemetry.BadPackets++;
                SmpLog.Warning("[Authority] claim okunamadı: " + e.Message);
            }
        }

        public void OnAuthoritySnapshot(BinaryReader r) {
            // AuthoritySnapshot paketi işle
            try {
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++) {
                    string path = r.ReadString();
                    ulong owner = r.ReadUInt64();
                    bool held = r.ReadBoolean();
                    bool installed = r.ReadBoolean();
                    string holder = r.ReadString();
                    // F04_AUTH_SNAPSHOT_ID: `owner` okunur (akis kaymamasi icin)
                    // ama `ApplyAuthoritySnapshot` icinde BAGLANTININ kimligiyle
                    // DEGISTIRILIR. Tel degisikligi yok.
                    ApplyAuthoritySnapshot(path, owner, held, installed, holder);
                }
            }
            catch (Exception e) {
                Telemetry.BadPackets++;
                SmpLog.Warning("[Authority] snapshot okunamadı: " + e.Message);
            }
        }

        // ----- JIP Snapshot desteği -----

        /// <summary>
        /// JIP snapshot için sahiplik durumlarını toplar.
        /// </summary>
        public void CollectStates(List<AuthorityStateData> list) {
            if (list == null) return;
            foreach (var kv in ownerOf) {
                AuthorityStateData d = new AuthorityStateData();
                d.ObjectPath = kv.Key;
                d.OwnerSteamId = kv.Value;
                d.Held = false;
                d.Installed = true;
                d.HolderPath = "";
                list.Add(d);
            }
        }

        /// <summary>
        /// JIP snapshot'tan gelen sahiplik durumunu uygular.
        /// </summary>
        public void ApplyAuthoritySnapshot(string objectPath, ulong ownerSteamId, bool held, bool installed, string holderPath) {
            if (string.IsNullOrEmpty(objectPath)) return;

            // F04_AUTH_SNAPSHOT_ID
            //
            // Bu metot JIP'ten gelen HER kaydi dogrudan tabloya yaziyordu
            // ve `ownerSteamId` PAKETTEN geliyordu. `OnAuthoritySnapshot`
            // (:293-297) degeri dogrulamadan geciyordu -> peer `ownerOf`
            // tablosunu istedigi kimlikle yazabiliyordu.
            //
            // `OnAuthorityMessage` (:265-269) ayni kurali zaten uyguluyor:
            //     ulong real = SteamTransport.RemoteId.m_SteamID;
            // Iki yol ayni kurali paylasmali; aksi halde JIP ile canli
            // akis farkli davranir.
            //
            // YEDEK KURAL: baglantinin kimligi 0 ise (lobby bilgisi yok)
            // paket degeri korunur. Reddetmek JIP'i tamamen calisamaz hale
            // getirirdi. Yedek yol yalniz `RemoteId` gecerli oldugu anda
            // devre disidir.
            //
            // TEL DEGISIKLIGI YOK: paket ayni sayida bayt, ayni sirada
            // okunuyor. Yalniz TABLONYA yazilan deger degisiyor.
            ulong real = 0;
            try { real = SteamTransport.RemoteId.m_SteamID; } catch { real = 0; }
            if (real != 0) {
                if (real != ownerSteamId && Telemetry.Verbose) {
                    SmpLog.Debug("authority", "snapshot-id",
                        "Snapshot owner (" + ownerSteamId + ") != connection ("
                        + real + ") for '" + objectPath + "'; using connection id.");
                }
                ownerSteamId = real;
            }

            ownerOf[objectPath] = ownerSteamId;
            ownedSince[objectPath] = Time.realtimeSinceStartup;
        }

        public string Describe() {
            return "authority: tracked=" + lastActionTime.Count + " action keys, rejected="
                + Rejected + ", agreed=" + Agreed;
        }
    }
}