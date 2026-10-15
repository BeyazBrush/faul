using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SteamMP;

namespace SteamMP.Interaction
{
    /// <summary>
    /// Vida senkronunun ETKILESIM katmanı.
    ///
    /// GERÇEK VIDA HATTI BURADA DEĞİL: `Satsuma/BoltSyncPatch.cs` yazar.
    ///   gönderme : SatsumaRegistry.FlushBolts -> BoltSync.Collect (20 Hz)
    ///             -> NetworkPacketManager.SendBoltBatch -> Msg.BoltBatch (id 1)
    ///   alma     : SteamMPBehaviour -> SatsumaRegistry.ApplyBoltBatch
    /// Gönderme sessizlik (BOLT_QUIET_PERIOD), hız (MIN_SEND_INTERVAL) ve
    /// uzlaştırma (ReconcileBolts) kurallarının hepsi orada; burada ikinci bir
    /// kuyruk tutmak onları ikinci kez uygular ve aynı cıvata için iki farklı
    /// gönderme yolu açardı.
    ///
    /// BU SINIF NEDEN VAR: `InteractionManager` tek bir etkileşim yüzeyi
    /// bekliyor ve iki çağıran yalnızca bu yüzeyi tanıyor:
    ///   - InteractionManager.RouteMessage, Msg.BoltBatchV2 için OnMessage
    ///   - JIPHandler snapshot uygularken ApplyBoltFromSnapshot
    ///
    /// YEREL cıvata eylemi BURADAN GİRMEZ. O yol `Satsuma/BoltSyncPatch.cs`
    /// içindedir: `FsmFloat.set_Value` Postfix'i (BoltFloatHook) her yazımı
    /// görür → BoltSync.OnTightnessWrite → SatsumaRegistry.OnBoltChanged →
    /// sessizlik + hız süzgeci → FlushBolts. FSM adına göre olay kancası
    /// kurmak yerine değer yazımını izlemek daha güvenilirdi: FSM TIGHTEN'i
    /// işleyip Stage'i IntClamp'te sınıra dayandırıp hiç değiştirmezse olay
    /// görülür ama değer değişmez (canlı log kanıtı, BoltSync dosya başı).
    ///
    /// NOT (BoltBatchV2 = id 52): bu mesajı GÖNDEREN YOK; canlı hat id 1'i
    /// kullanır. Yani OnMessage canlı yolda çağrılmaz, ama eski bir build'in
    /// paketi gelirse (ya da JIP snapshot'ı bu yüzü kullanırsa) hattı doğru
    /// okuyup SatsumaRegistry'nin tek uygulama yoluna yönlendirir.
    /// </summary>
    public class BoltBatcher
    {
        /// <summary>Bu oturumda karşı taraftan uygulanan cıvata kaydı sayısı (teşhis).</summary>
        public int AppliedFromPeer;

        /// <summary>Snapshot yüzünden bulunamayan yol sayısı (teşhis: farklı kayıt).</summary>
        public int Unresolved;

        // ------------------------------------------------------------------
        // InteractionManager'in beklediği yaşam döngüsü
        // ------------------------------------------------------------------

        /// <summary>
        /// Tablo SatsumaRegistry.Scan tarafından kurulur; burada yapılacak bir şey yok.
        /// Arayüz tutarlılığı için (InteractionManager.Discover hepsini çağırır) boş.
        /// </summary>
        public void Discover() { }

        /// <summary>
        /// Gönderme zamanlayıcısı SatsumaRegistry.Tick'in içinde (FlushBolts).
        /// Burada ikinci bir döngü olsaydı aynı cıvata iki kere fırlatılırdı.
        /// </summary>
        public void Update() { }

        /// <summary>Uzlaştırma da registry'de; ince yönlendirme.</summary>
        public void ReconcileBolts() { SatsumaRegistry.ReconcileBolts(); }

        public void Clear() {
            AppliedFromPeer = 0;
            Unresolved = 0;
        }

        // ------------------------------------------------------------------
        // YEREL OYUNCU EYLEMİ
        // ------------------------------------------------------------------

        /// <summary>
        /// Oyuncu bir cıvatayı çevirdiğinde çağrılabilir (dışarıdan bir olay
        /// yolundan). Canlı yolda çağıran yok — BoltSync'in değer-kancası
        /// aynı işi kendi başına yapıyor — ama yüzey doğru çalışır.
        ///
        /// BURADA HİÇBİR ŞEY UYGULANMAZ: oyunun kendi "Screw" FSM'i zaten
        /// Stage/Tightness'i yazdı, biz sadece "değişti" diyoruz. Değeri
        /// burada tekrar yazsaydık aynı karede iki yazım olur ve
        /// BoltSync.MarkLocalWrite sayacı ikinci yazımı "insan" sayardı.
        ///
        /// <paramref name="tightness"/> bilgi amaçlı: tween henüz
        /// hedefe ulaşmadığı için değeri göndermek YANLIŞ olurdu (bkz.
        /// BOLT_QUIET_PERIOD). Sadece dirty işaretliyoruz.
        /// </summary>
        public void OnLocalBoltAction(ulong actorSteamId, string boltPath, BoltAction action, float tightness) {
            if (string.IsNullOrEmpty(boltPath)) return;
            if (SatsumaRegistry.Suppressed) return;      // karşı tarafın kendi adımı
            if (!SteamTransport.HasRemote) return;

            SatsumaBolt bolt = FindBoltByPath(boltPath);
            if (bolt == null) {
                Unresolved++;
                if (Telemetry.Verbose)
                    SmpLog.Debug("bolts", "local", "yerel cıvata yolu tabloda yok: " + boltPath);
                return;
            }
            // Tek satır: değer zaten yazıldı, sadece "dirty" işaretle.
            SatsumaRegistry.OnBoltChanged(bolt);
        }

        // ------------------------------------------------------------------
        // GELEN PAKET
        // ------------------------------------------------------------------

        /// <summary>
        /// Msg.BoltBatchV2 gövdesi: ushort count, sonra count ×
        /// (boltPath, stage, tightness). Biçim tek kaynaktan okunur:
        /// <see cref="Protocol.ReadBoltBatchV2"/>.
        /// </summary>
        public void OnMessage(BinaryReader r) {
            BoltStateData[] bolts;
            try {
                Protocol.ReadBoltBatchV2(r, out bolts);
            }
            catch (Exception e) {
                Telemetry.BadPackets++;
                SmpLog.Warning("BoltBatchV2 okunamadı: " + e.Message);
                return;
            }
            for (int i = 0; i < bolts.Length; i++)
                Apply(bolts[i].BoltPath, bolts[i].Stage, bolts[i].Tightness);
        }

        /// <summary>
        /// JIP snapshot yüzü. Canlı hatla aynı uygulama yolunu kullanır; farkı
        /// karşı tarafa "ben de aynı durumdayım" demesidir (LastSent* eşitlenir),
        /// aksi halde snapshot sonrası cıvata aynı değer için tekrar gönderilir.
        /// </summary>
        public bool ApplyBoltFromSnapshot(string boltPath, int stage, float tightness) {
            return Apply(boltPath, stage, tightness);
        }

        // ------------------------------------------------------------------
        // UYGULAMA
        // ------------------------------------------------------------------

        /// <summary>
        /// Tek cıvata değerini uygular. Uygulamanın TEK yolu registry'dir
        /// (<see cref="SatsumaRegistry.ApplyBolt(string, byte, float, int, bool, string)"/>):
        /// sahiplik (AuthorityManager.ValidateBolt), echo bastırma ve
        /// uzlaştırma kuyruğu orada yazılı. Burada değerleri doğrudan
        /// FsmInt/FsmFloat'a yazsaydık o üçü atlanırdı — canlı logdaki
        /// "paket geldi ama montaj görünmüyor" belirtisinin tam kaynağı buydu.
        /// </summary>
        bool Apply(string boltPath, int stage, float tightness) {
            if (string.IsNullOrEmpty(boltPath)) return false;
            if (SatsumaRegistry.Suppressed) return false;

            SatsumaBolt bolt = FindBoltByPath(boltPath);
            if (bolt == null || bolt.Part == null) {
                Unresolved++;
                // Kayıt farkı (378/337 vs 369/332) yolu da çözemiyorsa
                // bu KAYIP DEĞİL, zamanlama: tablo henüz kurulmamış olabilir.
                if (SatsumaRegistry.TableNotReady && SteamTransport.IsClient)
                    SmpLog.Debug("bolts", "peer", "tablo hazır değil, bekliyorum: " + boltPath);
                return false;
            }

            // `bolted` yalnızca grup seviyesindeki yetki metni için; değeri
            // parçanın kendi durumundan okuyoruz, ağdan taşımıyoruz.
            bool bolted = bolt.Part.IsGroupBolted();

            EchoGuard.Scope scope = EchoGuard.Enter();
            try {
                SatsumaRegistry.ApplyBolt(bolt.Part.Id, bolt.Index, tightness, stage, bolted, bolt.Path);
            }
            finally { scope.Dispose(); }

            AppliedFromPeer++;
            return true;
        }

        // ------------------------------------------------------------------
        // TABLO SORGUSU / TEŞHİS
        // ------------------------------------------------------------------

        /// <summary>
        /// Cıvata yolunu tabloda arar.
        ///
        /// İNDEKS DEĞİL YOL: iki taraf farklı kayıtta olabiliyor (canlı log:
        /// host 378 parça / 337 vida, peer 369 / 332). Bir parçanın vida
        /// sayısı farklıysa indeks kayar ve gönderenin 3. vidası alıcının
        /// 2. vidasına düşer — belirti "parçaların vidaları kendiliğinden
        /// gevşiyor" idi. Yol kayıttan bağımsızdır.
        /// </summary>
        static SatsumaBolt FindBoltByPath(string boltPath) {
            if (string.IsNullOrEmpty(boltPath)) return null;
            int partCount = SatsumaRegistry.PartCount;
            for (int i = 0; i < partCount; i++) {
                SatsumaPart part = SatsumaRegistry.PartAt(i);
                if (part == null) continue;
                List<SatsumaBolt> list = part.Bolts;
                for (int b = 0; b < list.Count; b++) {
                    SatsumaBolt bolt = list[b];
                    if (bolt != null && string.Equals(bolt.Path, boltPath, StringComparison.Ordinal))
                        return bolt;
                }
            }
            return null;
        }

        public float GetBoltTightness(string boltPath) {
            SatsumaBolt bolt = FindBoltByPath(boltPath);
            return bolt != null ? bolt.TightnessValue : 0f;
        }

        public string Describe() {
            return "applied=" + AppliedFromPeer
                + " unresolved=" + Unresolved
                + " dirty=" + SatsumaRegistry.DirtyBolts
                + " table=" + SatsumaRegistry.PartCount;
        }
    }
}