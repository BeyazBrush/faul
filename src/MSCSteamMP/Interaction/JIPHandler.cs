using System;
using System.Collections.Generic;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// JOIN-IN-PROGRESS (JIP) YÖNETİCİSİ
    ///
    /// Yeni bir oyuncu lobiye katıldığında:
    /// 1. Client -> Host: FullStateSnapshot isteği gönderir
    /// 2. Host -> Client: Mevcut TÜM durumun snapshot'ını gönderir
    ///    - CarryState: Kimin elinde ne var (relative transform ile)
    ///    - PartMountState: Hangi parçalar takılı/sökülmüş
    ///    - BoltBatchV2: Tüm vidaların son Stage/Tightness değerleri
    ///    - AuthoritySnapshot: Tüm nesnelerin sahiplik durumu
    /// 3. Client bu veriyi uygular ve oyun başlar
    ///
    /// Host tarafında:
    ///   - SatsumaRegistry, CarrySync, PartSync, BoltBatcher, AuthorityManager'dan
    ///   güncel durum toplanır ve tek pakette gönderilir
    ///
    /// Client tarafında:
    ///   - Gelen veriler ilgili modüle dağıtılır (CarrySync, PartSync, BoltBatcher, AuthorityManager)
    ///   - EchoGuard ile yankı engellenir
    /// </summary>
    public class JIPHandler
    {
        /// <summary>
        /// Msg.FullStateSnapshot'ın yön baytı (v9).
        /// </summary>
        public enum SnapshotKind : byte
        {
            /// <summary>Client -> Host: "bana durumu gönder". Gövde yok.</summary>
            Request = 0,
            /// <summary>Host -> Client: tam durum. Gövde yazılır.</summary>
            Response = 1,
        }

        bool snapshotRequested;
        bool snapshotSent;
        float requestTime;

        public void Discover()
        {
            snapshotRequested = false;
            snapshotSent = false;
            requestTime = 0f;
            subsystemsMissing = false;
        }

        public void Clear()
        {
            snapshotRequested = false;
            snapshotSent = false;
            requestTime = 0f;
            subsystemsMissing = false;
        }

        public void Update()
        {
            // Client: snapshot isteği timeout kontrolü
            if (snapshotRequested && !snapshotSent && SteamTransport.IsClient)
            {
                if (Time.time - requestTime > 10f)
                {
                    SmpLog.Warning("JIP: Full state snapshot request timed out, retrying...");
                    RequestFullSnapshot();
                }
            }
        }

        // ---- Client: Snapshot İste ----

        public void RequestFullSnapshot()
        {
            if (!SteamTransport.HasRemote) return;

            snapshotRequested = true;
            requestTime = Time.time;

            try
            {
                var s = Protocol.Begin(Protocol.Msg.FullStateSnapshot);
                var w = new BinaryWriter(s);
                // YÖN baytı: 0 = istek. Bu mesaj iki yönlü olduğu için alıcı
                // (host) gövde bekleyip "Failed to read past end of stream"
                // veriyor ve cevabı hiç üretmiyordu.
                w.Write((byte)SnapshotKind.Request);
                w.Flush();
                if (SteamTransport.SendReliable(Protocol.Finish(s)))
                {
                    SmpLog.Log("JIP: Full state snapshot requested from host.");
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("JIP request failed: " + e.Message);
            }
        }

        // ---- Host: Snapshot Gönder ----

        public void SendFullSnapshot()
        {
            if (!SteamTransport.IsHost || !SteamTransport.HasRemote) return;

            try
            {
                // Tüm alt sistemlerden veriyi topla
                var carryStates = new List<CarryStateData>();
                var partMountStates = new List<PartMountStateData>();
                var boltStates = new List<BoltStateData>();
                var authorityStates = new List<AuthorityStateData>();

                // 1. CarrySync'den tutma durumu
                if (InteractionManager.Carry != null)
                {
                    // CarrySync internal state'e erişim gerekiyor - public metod ekleyelim
                    CollectCarryStates(carryStates);
                }

                // 2. PartSync'den takılma durumu
                if (InteractionManager.Parts != null)
                {
                    CollectPartStates(partMountStates);
                }

                // 3. BoltBatcher'dan vida durumu
                if (InteractionManager.Bolts != null)
                {
                    CollectBoltStates(boltStates);
                }

                // 4. AuthorityManager'den sahiplik durumu
                if (InteractionManager.Authority != null)
                {
                    CollectAuthorityStates(authorityStates);
                }

                // Gönder
                var s = Protocol.Begin(Protocol.Msg.FullStateSnapshot);
                var w = new BinaryWriter(s);
                // YÖN baytı: 1 = cevap. İstek ve cevap aynı id'de
                // taşındığı için ayrım zorunlu (v9).
                w.Write((byte)SnapshotKind.Response);
                Protocol.WriteFullStateSnapshot(w, carryStates.ToArray(), partMountStates.ToArray(),
                    boltStates.ToArray(), authorityStates.ToArray());

                if (SteamTransport.SendReliable(Protocol.Finish(s)))
                {
                    SmpLog.Log("JIP: Full state snapshot sent to client (" 
                        + carryStates.Count + " carry, " 
                        + partMountStates.Count + " parts, " 
                        + boltStates.Count + " bolts, " 
                        + authorityStates.Count + " authorities).");
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("JIP snapshot send failed: " + e.Message);
            }
        }

        // ---- Client: Snapshot Al ve Uygula ----

        public void OnFullSnapshot(BinaryReader r)
        {
            try
            {
                // YÖN AYRIK (v9): aynı id iki yönde kullanıldığı için ilk
                // bayt istek mi cevap mı ayırıyor. Önceden istek de cevap
                // gibi okunuyordu: gövde 1 bayt olduğu için "Failed to read
                // past end of stream" çıkıyor, host'un cevabı üretilmiyor ve
                // istemci 10 sn'de bir aynı isteği tekrarlıyordu.
                byte kind = r.ReadByte();
                if (kind != (byte)SnapshotKind.Response)
                {
                    // İSTEK: host ise cevabı üret, client ise yanlış yön.
                    if (SteamTransport.IsHost)
                    {
                        SendFullSnapshot();
                    }
                    else
                    {
                        SmpLog.Warning("JIP: ignoring a snapshot REQUEST sent to the client.");
                    }
                    return;
                }

                CarryStateData[] carryStates;
                PartMountStateData[] partMountStates;
                BoltStateData[] boltStates;
                AuthorityStateData[] authorityStates;

                Protocol.ReadFullStateSnapshot(r, out carryStates, out partMountStates, out boltStates, out authorityStates);

                // Alt sistemler hazır mı? Kayıtlar dünya kurulmadan gelirse
                // sayılar doğru okunur ama HİÇBİRİ uygulanamaz; bu, logda
                // "0 hata, tamam" gibi görünmesin diye açıkça raporlanır.
                subsystemsMissing = InteractionManager.Carry == null
                    && InteractionManager.Parts == null
                    && InteractionManager.Bolts == null
                    && InteractionManager.Authority == null;

                // EchoGuard ile yankıyı engelle
                var scope = EchoGuard.Enter();
                int failed = 0;
                try
                {
                    // KAYIT KAYIT KORUMA.
                    //
                    // CANLI KANIT (client logu 06.10, 14:25:23.039):
                    //   "JIP snapshot receive failed: Object reference not set"
                    // Tek bir istisna TÜM snapshot'ı çöptü: 6 parça ve 306
                    // cıvatanın hiçbiri uygulanmadı, üstelik istemci "alındı"
                    // işaretlemediği için 10 sn'de bir aynı isteği tekrarladı.
                    //
                    // Snapshot tek seferlik bir fırsattır (paket kaybolmaz,
                    // güvenilirdir), o yüzden bir kayıt patlarsa O KAYIT
                    // atılır, kalanı uygulanır. Hata sayısı loglanır.
                    //
                    // 1. CarryState
                    if (InteractionManager.Carry != null)
                    {
                        foreach (var c in carryStates)
                        {
                            try { ApplyCarryState(c.Path, c.Held, c.CarrierPath, c.LocalPos, c.LocalRot, c.ParentVehiclePath); }
                            catch (Exception e) { failed++; LogApplyFail("carry", c.Path, e); }
                        }
                    }

                    // 2. PartMountState
                    if (InteractionManager.Parts != null)
                    {
                        foreach (var p in partMountStates)
                        {
                            try { ApplyPartMountState(p.PartPath, p.Installed, p.CarrierPath, p.LocalPos, p.LocalRot, p.ParentVehiclePath); }
                            catch (Exception e) { failed++; LogApplyFail("part", p.PartPath, e); }
                        }
                    }

                    // 3. BoltBatchV2
                    if (InteractionManager.Bolts != null)
                    {
                        foreach (var b in boltStates)
                        {
                            try { InteractionManager.Bolts.ApplyBoltFromSnapshot(b.BoltPath, b.Stage, b.Tightness); }
                            catch (Exception e) { failed++; LogApplyFail("bolt", b.BoltPath, e); }
                        }
                    }

                    // 4. AuthoritySnapshot
                    if (InteractionManager.Authority != null)
                    {
                        foreach (var a in authorityStates)
                        {
                            try { InteractionManager.Authority.ApplyAuthoritySnapshot(a.ObjectPath, a.OwnerSteamId, a.Held, a.Installed, a.HolderPath); }
                            catch (Exception e) { failed++; LogApplyFail("authority", a.ObjectPath, e); }
                        }
                    }
                }
                finally { scope.Dispose(); }

                // Kısmi uygulama da BAŞARILI'dır: paket gitti, karşı tarafın
                // durumu büyük ölçüde aktarıldı. Yeniden istemek 306 kaydı
                // tekrar gönderip aynı hatayı tekrar tetiklerdi.
                snapshotSent = true;
                snapshotRequested = false;
                SmpLog.Log("JIP: Full state snapshot received and applied ("
                    + carryStates.Length + " carry, "
                    + partMountStates.Length + " parts, "
                    + boltStates.Length + " bolts, "
                    + authorityStates.Length + " authorities)"
                    + (failed > 0 ? " -- " + failed + " record(s) FAILED, see 'snapshot apply failed'" : "")
                    + (subsystemsMissing ? " -- snapshot arrived BEFORE discovery: counts read but nothing applied" : ""));
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("JIP snapshot receive failed: " + e.GetType().Name + ": " + e.Message
                    + " | " + (e.StackTrace ?? "-"));
            }
        }

        /// <summary>Snapshot dünya hazır olmadan geldiyse kayıt sayıları yanıltıcıdır.</summary>
        static bool subsystemsMissing;

        /// <summary>Tek bir snapshot kaydının uygulanamaması (yol + hata).</summary>
        static void LogApplyFail(string kind, string path, Exception e) {
            Telemetry.BadPackets++;
            SmpLog.Warning("snapshot apply failed (" + kind + ") '"
                + (string.IsNullOrEmpty(path) ? "?" : path) + "': "
                + e.GetType().Name + ": " + e.Message + " | " + (e.StackTrace ?? "-"));
        }

        // ---- Veri Toplama Yardımcıları ----

        void CollectCarryStates(List<CarryStateData> list)
        {
            if (InteractionManager.Carry == null) return;
            InteractionManager.Carry.CollectStates(list);
        }

        void CollectPartStates(List<PartMountStateData> list)
        {
            if (InteractionManager.Parts == null) return;
            InteractionManager.Parts.CollectStates(list);
        }

        void CollectBoltStates(List<BoltStateData> list)
        {
            if (InteractionManager.Bolts == null) return;

            int partCount = SatsumaRegistry.PartCount;
            for (int i = 0; i < partCount; i++)
            {
                var part = SatsumaRegistry.PartAt(i);
                if (part == null) continue;
                for (int b = 0; b < part.Bolts.Count; b++)
                {
                    var bolt = part.Bolts[b];
                    if (bolt != null)
                    {
                        list.Add(new BoltStateData
                        {
                            BoltPath = bolt.Path ?? "",
                            Stage = bolt.StageValue,
                            Tightness = bolt.TightnessValue
                        });
                    }
                }
            }
        }

        void CollectAuthorityStates(List<AuthorityStateData> list)
        {
            if (InteractionManager.Authority == null) return;
            InteractionManager.Authority.CollectStates(list);
        }

        // ---- Snapshot Uygulama Yardımcıları ----

        void ApplyCarryState(string path, bool held, string carrierPath, Vector3 localPos, Quaternion localRot, string parentVehiclePath)
        {
            if (InteractionManager.Carry == null) return;
            InteractionManager.Carry.ApplySnapshotState(path, held, carrierPath, localPos, localRot, parentVehiclePath);
        }

        void ApplyPartMountState(string partPath, bool installed, string carrierPath, Vector3 localPos, Quaternion localRot, string parentVehiclePath)
        {
            if (InteractionManager.Parts == null) return;
            InteractionManager.Parts.ApplySnapshotState(partPath, installed, carrierPath, localPos, localRot, parentVehiclePath);
        }

        public string Describe()
        {
            return "jip: requested=" + snapshotRequested + ", sent=" + snapshotSent;
        }
    }
}