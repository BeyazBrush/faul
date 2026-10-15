using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// TÜM SATSUMA FSM'LERİNİ DÖKÜCÜ: SATSUMA kökünün altındaki her
    /// PlayMakerFSM'in aktif durum değişimini loglar.
    ///
    /// Neden:
    ///   - Hangi fsm'de hangi duruma girildiği bilinmeden eksik senkronu
    ///     "gözle" hata bulamıyorduk.
    ///   - BoltSync/SatsumaCoop sadece faiz ve montaj olaylarını kapsar;
    ///     KitchenWaterTap/kapı/yakıt kapağı/FarSinyal gibi async FSMs
    ///     yoktu. Bu probe tüm SATSUMA FSM'lerini loglar, böylece hangi
    ///     bilgilerin peer'a alınması gerektiğini veride görürüz.
    ///
    /// Maliyet: 0.25 s aralıkla SATSUMA altındaki FSM'ler taranır.
    /// Gerçek bir state değişimi yoksa yazılmaz, yani log azdır.
    /// </summary>
    public static class SatsumaFsmProbe
    {
        const float INTERVAL = 0.25f;

        static readonly Dictionary<PlayMakerFSM, string> lastState =
            new Dictionary<PlayMakerFSM, string>(256);
        /// <summary>
        /// Ayni FSM'in ayni durumunu en son ne zaman logladik? Atesleme
        /// kaskadi + Blink dongusu saniyede 60+ satir uretip hizli loglayiciyi
        /// tetikliyor, CEVRE satirlar dusuyordu. 10 sn susturma.
        /// </summary>
        static readonly Dictionary<PlayMakerFSM, float> lastLogAt =
            new Dictionary<PlayMakerFSM, float>(256);
        const float LOG_COOLDOWN = 10f;
        static PlayMakerFSM[] fsms = new PlayMakerFSM[0];
        static bool discovered;
        static float next;
        static int lastPathCount;

        public static void Tick()
        {
            // v15: varsayilan olarak KAPALI.
            //
            // Canli log kaniti (21:03 oturumu, 3529 satir): bu probu logun
            // %25'ini (879 satir) tutuyordu -- SATSUMA koku altindaki HER
            // FSM'in her durum gecisi. Uzerinde 10 sn'lik bastirma var ama
            // oyun doluysa yine saniyede onlarca satir uretiyor.
            //
            // Probe saf bir TESHIS araci: lastState / lastLogAt / fsms
            // alanlarini baska hicbir kod okumuyor. Bu yuzden sadece
            // Telemetry.Verbose acikken tarama ve loglama yapilir; boylece
            // gereksiz polling de ortadan kalkiyor.
            if (!Telemetry.Verbose) return;

            try
            {
                if (!discovered || Time.time >= next)
                {
                    if (!discovered) { discovered = true; Scan(); }
                    next = Time.time + INTERVAL;
                }

                for (int i = 0; i < fsms.Length; i++)
                {
                    PlayMakerFSM fsm = fsms[i];
                    if (fsm == null) continue;
                    string state;
                    try { state = fsm.ActiveStateName; } catch { continue; }
                    if (string.IsNullOrEmpty(state)) continue;
                    string old;
                    if (lastState.TryGetValue(fsm, out old) && string.Equals(old, state, StringComparison.Ordinal))
                        continue;
                    lastState[fsm] = state;
                    float at;
                    float now = Time.time;
                    if (lastLogAt.TryGetValue(fsm, out at) && now - at < LOG_COOLDOWN) continue;
                    lastLogAt[fsm] = now;
                    string path = "?";
                    try { path = Wire.PathOf(fsm.transform); } catch { }
                    SmpLog.Log("[SatsumaFSM] '" + SafeName(fsm) + "' -> '" + state + "'  @ " + path);
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("[SatsumaFSM] probe failed: " + e.Message);
            }
        }

        static string SafeName(PlayMakerFSM fsm)
        {
            try
            {
                if (!string.IsNullOrEmpty(fsm.FsmName)) return fsm.FsmName;
                return fsm.name;
            }
            catch
            {
                try { return fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name) ? fsm.Fsm.Name : fsm.name; }
                catch { return "(unnamed)"; }
            }
        }

        /// <summary>SATSUMA kökünün altındaki tüm FSM'leri toplar.</summary>
        static void Scan()
        {
            GameObject root = null;
            try { root = GameObject.Find("SATSUMA"); } catch { }
            if (root == null)
            {
                try
                {
                    if (SatsumaRegistry.Root != null) root = SatsumaRegistry.Root.gameObject;
                }
                catch { }
            }
            if (root == null)
            {
                // SATSUMA yokken ilk taramayı atlıyoruz; root bulununca tekrar denenir.
                discovered = false;
                return;
            }
            PlayMakerFSM[] all = null;
            try { all = root.GetComponentsInChildren<PlayMakerFSM>(true); } catch { }
            if (all == null) { fsms = new PlayMakerFSM[0]; return; }
            fsms = all;
            lastPathCount = all.Length;
            SmpLog.Log("[SatsumaFSM] probe: " + all.Length + " FSM found at SATSUMA root; state changes will be logged.");
        }
    }
}
