using System;
using System.Collections.Generic;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// KAYIP PARÃ‡A KURTARMA (Fell Through Map Recovery)
    ///
    /// Sorun: Senkronizasyon hatasÄ±, fizik glitch'i veya aÄŸ gecikmesi
    /// sonucu bir parÃ§a haritanÄ±n altÄ±na dÃ¼ÅŸebilir (Y < -100).
    ///
    /// Ã‡Ã¶zÃ¼m:
    /// 1. Her kare (veya 10 Hz) izlenen parÃ§alarÄ±n Y koordinatÄ±nÄ± kontrol et
    /// 2. Y < RECOVERY_THRESHOLD (-100) ise parÃ§ayÄ± gÃ¼venli bÃ¶lgeye Ä±ÅŸÄ±nla
    /// 4. Kurtarma olayÄ±nÄ± PartRecovery paketi ile diÄŸer oyunculara bildir
    ///
    /// GÃ¼venli BÃ¶lge SeÃ§imi:
    ///   - Ã–ncelik 1: Oyuncunun garajÄ± (garaj merkezi)
    ///   - Ã–ncelik 2: Oyuncunun Ã¶nÃ¼ (oyuncu pozisyonu + 3m ileri)
    ///   - Ã–ncelik 3: DÃ¼nya kÃ¶keni (0, 50, 0)
    ///
    /// AÄŸ Paketi: PartRecovery (path, newPosition) - Reliable
    /// </summary>
    public class RecoveryManager
    {
        // ---- Ayarlar ----
        public const float RECOVERY_THRESHOLD = -100f;  // Y koordinatÄ± bu altÄ±na inerse kurtar
        public const float CHECK_INTERVAL = 0.5f;       // 2 Hz kontrol
        public const float RECOVERY_COOLDOWN = 10f;     // AynÄ± parÃ§a iÃ§in minimum aralÄ±k

        /// <summary>
        /// MAKUL OLMAYAN KONUM REDDÄ°.
        ///
        /// CANLI KAZA (11.10 oturumu): parÃ§alar Y=7310672000.0 ve Y=-3145728.0
        /// deÄŸerlerine "kurtarÄ±ldÄ±" â€” yani kurtarma mekanizmasÄ± kendisi parÃ§ayÄ±
        /// uzaya gÃ¶nderdi. BÃ¶yle bir konumu telde taÅŸÄ±mak ve karÅŸÄ± tarafa
        /// uygulamak hatayÄ± Ä°KÄ° makineye yayÄ±yor.
        ///
        /// Harita birkaÃ§ kilometre; 10.000 birim kadar bir Y deÄŸeri zaten
        /// fiziksel olamaz. Bu sÄ±nÄ±rÄ± aÅŸan her konum sessizce reddedilir.
        /// </summary>
        public const float SANITY_LIMIT = 10000f;

        /// <summary>
        /// AlÄ±cÄ± tarafÄ±nda aynÄ± parÃ§a iÃ§in minimum bekleme (s).
        ///
        /// NEDEN: gÃ¶nderici tarafÄ±nda RECOVERY_COOLDOWN vardÄ± ama ALICI
        /// tarafÄ±nda yoktu. GÃ¶nderici bir seferde ~150 parÃ§ayÄ± kurtarÄ±p
        /// 150 paket basÄ±nca alÄ±cÄ± hepsini ARKA ARKA uyguladÄ±; kurtarma
        /// konumu zaten karÅŸÄ± tarafÄ±n yerel konumuydu, yani parÃ§alar
        /// dur durak bilmeyen bir Ä±ÅŸÄ±nlama dÃ¶ngÃ¼sÃ¼ne girdi.
        /// </summary>
        public const float APPLY_COOLDOWN = 1f;

        /// <summary>Tek kontrolde en fazla kaÃ§ kurtarma paketi GÃ–NDERÄ°LÄ°R.
        /// FazlasÄ± bir sonraki kontrole kalÄ±r; gÃ¶nderim kaybolmaz, yalnÄ±z
        /// gecikir (kirli kalma deseni).</summary>
        public const int MAX_SEND_PER_CHECK = 8;

        // ---- Durum ----
        float nextCheck;
        /// <summary>Bu kontrolde gÃ¶nderilen kurtarma paketi sayÄ±sÄ± (sÄ±nÄ±r iÃ§in).</summary>
        int sentThisCheck;
        Dictionary<string, float> lastRecovery = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>AlÄ±nan kurtarmanÄ±n son uygulanma anÄ± (yol -> zaman).</summary>
        Dictionary<string, float> lastApplied = new Dictionary<string, float>(StringComparer.Ordinal);

        // TeÅŸhis
        public int RecoveriesPerformed;
        public int PartsChecked;
        public int PartsBelowThreshold;
        /// <summary>Makul olmayan konum nedeniyle reddedilen paket sayÄ±sÄ±.</summary>
        public int RejectedInsane;
        /// <summary>KarÅŸÄ± tarafÄ±n parÃ§asÄ± zaten saÄŸlam olduÄŸu iÃ§in atlanan paket.</summary>
        public int SkippedHealthy;
        /// <summary>Cooldown nedeniyle ertelenen paket.</summary>
        public int Deferred;

        public void Discover()
        {
            lastRecovery.Clear();
            lastApplied.Clear();
            nextCheck = 0f;
            sentThisCheck = 0;
        }

        public void Clear()
        {
            lastRecovery.Clear();
            lastApplied.Clear();
            nextCheck = 0f;
            sentThisCheck = 0;
        }

        public string Describe()
        {
            return "recovery: performed=" + RecoveriesPerformed
                + " below=" + PartsBelowThreshold
                + " rejected-insane=" + RejectedInsane
                + " skipped-healthy=" + SkippedHealthy
                + " deferred=" + Deferred;
        }

        public void Update()
        {
            float now = Time.time;
            if (now < nextCheck) return;
            nextCheck = now + CHECK_INTERVAL;

            if (!SteamTransport.HasRemote) return; // Sadece multiplayer'da

            CheckParts(now);
        }

        void CheckParts(float now)
        {
            PartsChecked = 0;
            PartsBelowThreshold = 0;
            sentThisCheck = 0;

            // SatsumaRegistry'deki tÃ¼m parÃ§alarÄ± kontrol et
            int partCount = SatsumaRegistry.PartCount;
            for (int i = 0; i < partCount; i++)
            {
                var part = SatsumaRegistry.PartAt(i);
                if (part == null || part.Object == null) continue;
                PartsChecked++;

                var t = part.Object.transform;
                if (t.position.y < RECOVERY_THRESHOLD)
                {
                    PartsBelowThreshold++;

                    // Cooldown kontrolÃ¼
                    float lastRec;
                    if (lastRecovery.TryGetValue(part.Id, out lastRec))
                    {
                        if (now - lastRec < RECOVERY_COOLDOWN) continue;
                    }

                    // GÃ–NDERÄ°M SINIRI: bir kontrolde en fazla MAX_SEND_PER_CHECK
                    // kurtarma paketi Ã§Ä±kar. CanlÄ± oturumda ilk kontrol 150'den
                    // fazla parÃ§ayÄ± aynÄ± anda kurtarÄ±p 150 paket basmÄ±ÅŸtÄ±; alÄ±cÄ±
                    // taraf fiziksel olarak toparlanamadÄ±. FazlasÄ± bir sonraki
                    // kontrole kalÄ±r â€” paket kaybolmaz, yalnÄ±zca gecikir.
                    if (sentThisCheck >= MAX_SEND_PER_CHECK) { Deferred++; continue; }

                    // Kurtar!
                    RecoverPart(part, now);
                    lastRecovery[part.Id] = now;
                    sentThisCheck++;
                }
            }

            // ItemSync/GarageEntitySync'teki nesneleri de kontrol et
            if (InteractionManager.Carry != null)
            {
                // CarrySync'te remote item varsa kontrol et
            }
        }

        void RecoverPart(SatsumaPart part, float now)
        {
            var t = part.Object.transform;

            // LOG DÃœZELTMESÄ° (canlÄ± karÄ±ÅŸÄ±klÄ±ÄŸÄ±n kaynaÄŸÄ±):
            // Eski kod `t.position = safePos` yazdÄ±ktan SONRA
            // `t.position.y`'yi okuyordu, yani "recovered from Y=3.6 to 3.6"
            // basÄ±yordu â€” "eski konum" hiÃ§bir zaman gÃ¶rÃ¼nmÃ¼yordu. ParÃ§alarÄ±n
            // nereden dÃ¼ÅŸtÃ¼ÄŸÃ¼ (ve meÅŸhur Y=7310672000 deÄŸerinin gerÃ§ekten
            // eski konum mu yoksa yazÄ±mÄ±n tutmadÄ±ÄŸÄ± anlamÄ±na mÄ± geldiÄŸi)
            // bu yÃ¼zden hiÃ§ ayÄ±rt edilemedi.
            float originalY = t.position.y;

            // CANLI HATA DUZELTMESI: konum artik parcanin KENDI hiyerarsisindan
            // turetiliyor (bkz. FindSafePosition). Eski kod dogrudan garaj
            // merkezine isinlardi; Satsuma parcalari arac hiyerarsisinin
            // cocuklari oldugu icin hepsi aracdan KOPARILIYORDU --
            // "kapiyi actim, butun parcalar koptu" belirtisinin kaynagi.
            Vector3 safePos = FindSafePosition(t.position, t);

            // GÄ°DEN SAVUNMA: Ã¼rettiÄŸimiz gÃ¼venli konum da saÄŸlamsÄ±z olabilir
            // (garaj nesnesi Ä±ÅŸÄ±nlanmÄ±ÅŸsa, oyuncu kÃ¶kÃ¼ bozuksa). BÃ¶yle bir
            // konumu ne kendimize uygularÄ±z ne de telde taÅŸÄ±rÄ±z.
            if (float.IsNaN(safePos.x) || float.IsNaN(safePos.y) || float.IsNaN(safePos.z)
                || Mathf.Abs(safePos.x) > SANITY_LIMIT || Mathf.Abs(safePos.y) > SANITY_LIMIT
                || Mathf.Abs(safePos.z) > SANITY_LIMIT)
            {
                RejectedInsane++;
                if (Telemetry.Verbose)
                    SmpLog.Debug("world", "recovery", "Ã¼retilen gÃ¼venli konum saÄŸlamsÄ±z, atlandÄ±: "
                        + safePos.ToString("F1") + " @ " + part.Id);
                return;
            }

            var scope = EchoGuard.Enter();
            try
            {
                t.position = safePos;
                // EÄŸer kinematikse (taÅŸÄ±yorsa) velocity sÄ±fÄ±rla
                var rb = part.Object.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            finally { scope.Dispose(); }

            // AÄŸ bildirimi
            SendRecovery(part.Id, safePos);

            RecoveriesPerformed++;
            SmpLog.Log("Recovery: Part '" + part.Id + "' recovered from Y="
                + originalY.ToString("F1") + " to " + safePos.y.ToString("F1"));
        }

        /// <summary>
        /// Parca icin guvenli konum. ONCE parcanin kendi agacindaki saglam
        /// ust (arac govdesi, bina, garaj...) aranir; bulunamazsa eski yol
        /// (garaj / oyuncu onu / dunya koku) devreye girer.
        ///
        /// NEDEN (canli kaza): eski kod dogrudan GARAJ MERKEZINE isinlardi.
        /// Satsuma parcalari arac hiyerarsisinin cocuklari (yol ornegi:
        /// SATSUMA(557kg, 248)/MiscParts/radiator hose3(xxxxx)), dolayisiyla
        /// "kayip" sayilan her parca aracdan KOPARILIP garaja tasiniyordu.
        /// Kullanici belirtisi: "satsuma aracinin kapisini actigim an butun
        /// parcalar koptu." Log kaniti: ayni anda 45+ parca icin
        /// "recovered from Y=-32768.0 to 3.8" ve "Y=491520.0 to 3.8".
        /// </summary>
        Vector3 FindSafePosition(Vector3 currentPos, Transform self)
        {
            // 0) Parcanin kendi agacinda saglam bir ust varsa oraya koy.
            Vector3 anchor;
            if (TryFindSaneAncestor(self, out anchor)) return anchor + Vector3.up * 1f;

            // 1. Oyuncunun garajÄ± varsa oraya
            var garage = FindGarageCenter();
            if (garage != Vector3.zero)
            {
                return garage + Vector3.up * 2f;
            }

            // 2. Yerel oyuncunun Ã¶nÃ¼
            var localPlayer = FindLocalPlayer();
            if (localPlayer != null)
            {
                Vector3 forward = localPlayer.transform.forward;
                Vector3 pos = localPlayer.transform.position + forward * 3f + Vector3.up * 2f;
                // NavMesh/zemin kontrolÃ¼ basitÃ§e
                return SnapToGround(pos);
            }

            // 3. DÃ¼nya kÃ¶keni yukarÄ±
            return new Vector3(0f, 50f, 0f);
        }

        /// <summary>
        /// Parcanin ustundeki ilk saglam (makul konumlu) atayi bulur.
        ///
        /// "Saglam" = NaN yok, |koordinat| &lt;= SANITY_LIMIT ve harita
        /// altinda degil. Boylece:
        ///   * arac govdesi sagsa -> parca aracin uzerine geri konar;
        ///   * arabin tamami kotudeyse hicbir ust saglam degildir -> false,
        ///     ciftci eski yola duser.
        /// </summary>
        static bool TryFindSaneAncestor(Transform self, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (self == null) return false;
            Transform cur = self.parent;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                Vector3 p;
                try { p = cur.position; } catch { break; }
                if (!float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)
                    && Mathf.Abs(p.x) <= SANITY_LIMIT
                    && Mathf.Abs(p.y) <= SANITY_LIMIT
                    && Mathf.Abs(p.z) <= SANITY_LIMIT
                    && p.y > RECOVERY_THRESHOLD)
                {
                    pos = p;
                    return true;
                }
                cur = cur.parent;
            }
            return false;
        }

        Vector3 FindGarageCenter()
        {
            // Garaj nesnelerini bul: "Garage" veya "garage" iÃ§eren
            GameObject[] roots;
            try { roots = SceneDump.AllSceneRoots(); }
            catch { return Vector3.zero; }
            if (roots == null) return Vector3.zero;

            foreach (var r in roots)
            {
                if (r == null) continue;
                string name = r.name.ToLowerInvariant();
                if (name.Contains("garage") || name.Contains("garaj"))
                {
                    // Garaj kÃ¶kÃ¼nÃ¼n pozisyonu + hafif yukarÄ±
                    return r.transform.position + Vector3.up * 2f;
                }
            }
            return Vector3.zero;
        }

        GameObject FindLocalPlayer()
        {
            try
            {
                var root = GameObject.Find("PLAYER");
                return root;
            }
            catch { return null; }
        }

        Vector3 SnapToGround(Vector3 pos)
        {
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 10f, Vector3.down, out hit, 50f))
            {
                return hit.point + Vector3.up * 0.5f;
            }
            return pos;
        }

        void SendRecovery(string partPath, Vector3 newPosition)
        {
            if (!SteamTransport.HasRemote) return;

            try
            {
                var s = Protocol.Begin(Protocol.Msg.PartRecovery);
                var w = new BinaryWriter(s);
                Protocol.WritePartRecovery(w, partPath, newPosition);
                SteamTransport.SendReliable(Protocol.Finish(s));
            }
            catch (Exception e)
            {
                SmpLog.Warning("PartRecovery send failed: " + e.Message);
            }
        }

        public void OnRecoveryMessage(BinaryReader r)
        {
            try
            {
                string partPath;
                Vector3 newPosition;
                Protocol.ReadPartRecovery(r, out partPath, out newPosition);

                // ---- KORUMA 1: MAKUL OLMAYAN KONUM ----
                // CanlÄ± kazanÄ±n tetikleyicisi buydu: konumlar Y=7310672000.0
                // ve Y=-3145728.0 deÄŸerlerine ulaÅŸmÄ±ÅŸtÄ± ve karÅŸÄ± tarafÄ±n
                // parÃ§asÄ± o koordinata Ä±ÅŸÄ±nlanÄ±yordu. BÃ¶yle bir paket
                // uygulanmaz; sayÄ±cÄ±ya dÃ¼ÅŸer ki sessizce kaybolmasÄ±n.
                if (float.IsNaN(newPosition.x) || float.IsNaN(newPosition.y)
                    || float.IsNaN(newPosition.z)
                    || Mathf.Abs(newPosition.x) > SANITY_LIMIT
                    || Mathf.Abs(newPosition.y) > SANITY_LIMIT
                    || Mathf.Abs(newPosition.z) > SANITY_LIMIT)
                {
                    RejectedInsane++;
                    if (WarnRecoveryPath(partPath))
                        SmpLog.Warning("Recovery: absurd position rejected for "
                            + partPath + " -> " + newPosition.ToString("F1"));
                    return;
                }

                GameObject partGo = null;
                // Cozum sirasi: once TABLO (gonderen `part.Id`, yani tablo
                // kimligi gonderir), sonra tam yol, sonra yaprak-adi yedegi.
                // Eski kod yalniz tam yola bakiyordu: karsi save'de parca
                // farkli ebeveyndeyse/klon numarasi farkliysa 114 satir
                // "Part not found" spam'i uretiyordu.
                try {
                    SatsumaPart rp;
                    if (SatsumaRegistry.TryGetById(partPath, out rp) && rp != null) partGo = rp.Object;
                } catch { partGo = null; }
                if (partGo == null) { try { partGo = FindByPath(partPath)?.gameObject; } catch { partGo = null; } }
                if (partGo == null) { try { partGo = FindByLeafName(partPath); } catch { partGo = null; } }
                if (partGo == null)
                {
                    // Ayni yol icin bir kez uyar (farkli save'te parca yoksa
                    // her pakette bagirmak logu bogar).
                    if (WarnRecoveryPath(partPath))
                        SmpLog.Warning("Recovery: Part not found for path: " + partPath + " (different save?)");
                    return;
                }

                // ---- KORUMA 2: YALNIZCA GERÃ‡EKTEN DIÅARIDAYSA UYGULA ----
                // BU, dÃ¶ngÃ¼yÃ¼ kÄ±ran ana koruma.
                //
                // Eski kod gelen konumu KOÅULSUZ uyguluyordu. KarÅŸÄ± tarafÄ±n
                // kurtarma konumu KENDÄ° yerel garajÄ±/oyuncusudur; onu kendi
                // saÄŸlam parÃ§amÄ±za zorlamak parÃ§ayÄ± haritadan dÃ¼ÅŸÃ¼rÃ¼yordu,
                // o zaman BÄ°ZÄ°M kurtarmamÄ±z ateÅŸliyor, o da geri gÃ¶nderiyordu
                // â†’ iki makine birbirinin parÃ§alarÄ±nÄ± dur durak bilmeyen bir
                // Ä±ÅŸÄ±nlama dÃ¶ngÃ¼sÃ¼ne sokuyordu ("araba uÃ§tu havaya").
                //
                // Åimdi: karÅŸÄ± tarafÄ±n parÃ§asÄ± zaten dÃ¼nyede saÄŸlamsa paket
                // TAMAMEN yok sayÄ±lÄ±r. Kurtarma yalnÄ±zca iki tarafta da parÃ§a
                // gerÃ§ekten dÃ¼ÅŸtÃ¼ÄŸÃ¼nde anlamlÄ±dÄ±r.
                Vector3 localPos = partGo.transform.position;
                if (!(localPos.y < RECOVERY_THRESHOLD))
                {
                    SkippedHealthy++;
                    if (Telemetry.Verbose)
                        SmpLog.Debug("world", "recovery",
                            "karÅŸÄ± tarafÄ±n kurtarmasÄ± atlandÄ± (bu tarafta saÄŸlam): "
                            + partPath + " y=" + localPos.y.ToString("F1"));
                    return;
                }

                // ---- KORUMA 3: UYGULAMA BEKLEMESÄ° ----
                // GÃ¶ndericinin RECOVERY_COOLDOWN'u vardÄ±, alÄ±cÄ±nÄ±n yoktu.
                // Bir seferde yÃ¼zlerce parÃ§a kurtarÄ±lÄ±rsa alÄ±cÄ± hepsini
                // arka arka uygular ve fizik hiÃ§ toparlanamazdÄ±.
                float lastAt;
                if (lastApplied.TryGetValue(partPath, out lastAt)
                    && Time.time - lastAt < APPLY_COOLDOWN)
                {
                    Deferred++;
                    return;
                }
                lastApplied[partPath] = Time.time;

                var scope = EchoGuard.Enter();
                try
                {
                    partGo.transform.position = newPosition;
                    var rb = partGo.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                }
                finally { scope.Dispose(); }

                SmpLog.Log("Recovery received: " + partPath + " -> " + newPosition);
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("PartRecovery receive failed: " + e.Message);
            }
        }

        GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string rootName, rest;
            SceneDump.SplitRoot(path, out rootName, out rest);

            GameObject[] roots;
            try { roots = SceneDump.AllSceneRoots(); }
            catch { return null; }
            if (roots == null) return null;

            for (int i = 0; i < roots.Length; i++)
            {
                GameObject r = roots[i];
                if (r == null) continue;
                if (!string.Equals(r.name, rootName, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(rest)) return r;
                Transform t;
                try { t = r.transform.Find(rest); }
                catch { continue; }
                if (t == null) continue;
                return t.gameObject;
            }
            return null;
        }

        static readonly HashSet<string> warnedRecoveryPaths =
            new HashSet<string>(StringComparer.Ordinal);

        static bool WarnRecoveryPath(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            try { return warnedRecoveryPaths.Add(path); }
            catch { return true; }
        }

        static GameObject FindByLeafName(string path) {
            if (string.IsNullOrEmpty(path)) return null;
            string leaf = path;
            int cut = path.LastIndexOf('/');
            if (cut >= 0 && cut + 1 < path.Length) leaf = path.Substring(cut + 1);
            if (string.IsNullOrEmpty(leaf)) return null;
            GameObject[] roots;
            try { roots = SceneDump.AllSceneRoots(); }
            catch { return null; }
            if (roots == null) return null;
            for (int i = 0; i < roots.Length; i++) {
                GameObject r = roots[i];
                if (r == null) continue;
                Transform f = null;
                try { f = FindByNameLeaf(r.transform, leaf); } catch { continue; }
                if (f != null) return f.gameObject;
            }
            return null;
        }

        static Transform FindByNameLeaf(Transform t, string leaf) {
            if (t == null || string.IsNullOrEmpty(leaf)) return null;
            int n = 0;
            try { n = t.childCount; } catch { return null; }
            for (int i = 0; i < n; i++) {
                Transform c = null;
                try { c = t.GetChild(i); } catch { continue; }
                if (c == null) continue;
                if (string.Equals(c.name, leaf, StringComparison.Ordinal)) {
                    Renderer[] rr = null;
                    try { rr = c.GetComponentsInChildren<Renderer>(true); } catch { }
                    if (rr != null && rr.Length > 0) return c;
                }
            }
            for (int i = 0; i < n; i++) {
                Transform c2 = null;
                try { c2 = t.GetChild(i); } catch { continue; }
                if (c2 == null) continue;
                Transform f = FindByNameLeaf(c2, leaf);
                if (f != null) return f;
            }
            return null;
        }
    }
}
