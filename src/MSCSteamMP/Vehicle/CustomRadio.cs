using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// KULLANICI AYAĞI: Tundo'inRot değeri belirli aralığa gelince özel
    /// dosya sesini local çalar; aynı Rot değeri karşı tarafa gittiği için
    /// arkadaşın da aynı aralığa geldiğinde (dosyası varsa) aynı şarkı
    /// çalar.
    ///
    /// Kullanım:
    ///   - Ses dosyasını şu yola koy: Mods\custom_radio.ogg/.wav/.mp3
    ///   - Tuner'ı 1085-1095 derece aralığına getir.
    ///   - Arkadaşına aynı dosyayı ver; yoksa onun tarafında çalmaz.
    /// </summary>
    public static class CustomRadio
    {
        /// <summary>Tetikleyen tuner aralığı.</summary>
        public const float MIN_ROT = 1085f;
        public const float MAX_ROT = 1095f;

        /// <summary>Dosya uzantısı sırayla denenir.</summary>
        static readonly string[] MUTE_EXTS = { ".ogg", ".wav", ".mp3" };
        const string BASE_FILE = "custom_radio";

        static AudioSource audio;
        static WWW www;
        static AudioClip clip;
        static bool startedLoad;
        static bool loadCompleted;
        static bool warnedOnce;
        static FsmFloat[] rotors;
        static FsmFloat radioVolume;
        static string CurrentFile;

        static AudioType AudioTypeOf(string path)
        {
            string ext = Path.GetExtension(path) ?? "";
            switch (ext.ToLowerInvariant())
            {
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                case ".mp3": return AudioType.MPEG;
                default: return AudioType.UNKNOWN;
            }
        }

        static Transform SpeakerRoot;

        public static void Setup()
        {
            try
            {
                rotors = FindRotors();
                radioVolume = FindRadioVolume();

                // İkili ses çalır: car'ın hoparlöründen değil araç HoparlörRoot'undan
                // bağımsız GameObject kur. Dogrudan global 'RADIO' node'una bağla.
                GameObject radioRoot = GameObject.Find("RADIO");
                Transform parent = radioRoot != null ? radioRoot.transform : null;

                GameObject go = RecoveryObject("CsmRadioPlayer");
                if (go == null)
                {
                    go = new GameObject("CsmRadioPlayer");
                    // Araç nesnesinin altına koyma: RADIO koltuğu oyun
                    // tarafında inaktif olabiliyor ve inaktif GO'ya Play()
                    // sessiz kalın tekrarlıyordu (isPlaying her kare false
                    // kalıyordu). Sahnenin kökünde, DontDestroyOnLoad olarak
                    // kalsın; parent RADIO olsun dersek isPlaying kurtarılmaz.
                    UnityEngine.Object.DontDestroyOnLoad(go);
                }
                audio = go.GetComponent<AudioSource>();
                if (audio == null) audio = go.AddComponent<AudioSource>();
                audio.playOnAwake = false;
                audio.loop = true;
                audio.volume = 1f;
                audio.spatialBlend = 0f;
                SmpLog.Log("[CustomRadio] player ready. tuning Rot " + MIN_ROT + "-" + MAX_ROT + " plays '" + BASE_FILE + "'.");
            }
            catch (Exception e)
            {
                SmpLog.Warning("[CustomRadio] setup failed: " + e.Message);
            }
        }

        static Transform SpeakerPivotOf(Transform t) { return t; }

        static GameObject RecoveryObject(string name)
        {
            GameObject[] all = null;
            try { all = Resources.FindObjectsOfTypeAll<GameObject>(); } catch { return null; }
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name) return all[i];
            }
            return null;
        }

        static FsmFloat[] FindRotors()
        {
            List<FsmFloat> list = new List<FsmFloat>(8);
            PlayMakerFSM[] fsm = null;
            try { fsm = Resources.FindObjectsOfTypeAll<PlayMakerFSM>(); } catch { return list.ToArray(); }
            if (fsm == null) return list.ToArray();
            for (int i = 0; i < fsm.Length; i++)
            {
                PlayMakerFSM f = fsm[i];
                if (f == null) continue;
                // TUNA lingilikte: nesne adı 'RadioTuner' (her aracın)
                if (f.gameObject == null || f.gameObject.name.IndexOf("RadioTuner", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (f.FsmVariables == null || f.FsmVariables.FloatVariables == null) continue;
                FsmFloat[] arr = f.FsmVariables.FloatVariables;
                for (int v = 0; v < arr.Length; v++)
                {
                    if (arr[v] != null && arr[v].Name == "Rot") { list.Add(arr[v]); break; }
                }
            }
            return list.ToArray();
        }

        static FsmFloat FindRadioVolume()
        {
            GameObject radioChannels = GameObject.Find("RadioChannels");
            if (radioChannels == null)
            {
                GameObject[] all = null;
                try { all = Resources.FindObjectsOfTypeAll<GameObject>(); } catch { return null; }
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null && all[i].name == "RadioChannels") { radioChannels = all[i]; break; }
                    }
                }
            }
            if (radioChannels == null) return null;
            PlayMakerFSM[] fsms = radioChannels.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null) return null;
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && string.Equals(fsms[i].FsmName, "Fetch", StringComparison.OrdinalIgnoreCase))
                {
                    FsmFloat[] arr = fsms[i].FsmVariables != null ? fsms[i].FsmVariables.FloatVariables : null;
                    if (arr != null)
                    {
                        for (int v = 0; v < arr.Length; v++)
                        {
                            if (arr[v] != null && arr[v].Name == "Volume") return arr[v];
                        }
                    }
                }
            }
            return null;
        }

        public static void Tick()
        {
            try
            {
                if (audio == null) { Setup(); return; }

                EnsureRotors();

                bool inWindow = IsInTunerWindow();

                if (inWindow)
                {
                    EnsureFileLoading();

                    if (clip != null && audio.clip != clip) audio.clip = clip;
                    if (clip != null && clip.length > 0.1f && !audio.isPlaying)
                    {
                        audio.clip = clip;
                        audio.Play();
                        SmpLog.Log("[CustomRadio] tuner at station window, playing '" + BASE_FILE + "' (len " + clip.length.ToString("F1") + "s).");
                    }
                    if (radioVolume != null) audio.volume = Mathf.Clamp01(radioVolume.Value);
                    if (!loadCompleted && www != null && www.isDone)
                    {
                        loadCompleted = true;
                        if (www.error != null)
                        {
                            SmpLog.Warning("[CustomRadio] www error: " + www.error + " bytes=" + www.bytesDownloaded);
                        }
                        else
                        {
                            clip = www.GetAudioClip(false, false, AudioTypeOf(CurrentFile));
                            SmpLog.Log("[CustomRadio] retrieval: clip=" + (clip != null ? clip.name + " len=" + clip.length : "(null)")
                                + " bytes=" + www.bytesDownloaded);
                            if (clip == null || clip.length < 0.1f)
                                SmpLog.Warning("[CustomRadio] loaded but unusable clip; try a PCM .wav! (MP3/OGG decode desteği boş dönebiliyor)");
                        }
                    }
                    if (www != null && www.error != null && !warnedOnce)
                    {
                        warnedOnce = true;
                        SmpLog.Warning("[CustomRadio] load failed: " + www.error + " (put 'Mods/" + BASE_FILE + ".ogg' next to the dll)");
                    }
                }
                else
                {
                    if (audio != null && audio.isPlaying) audio.Stop();
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("[CustomRadio] tick failed: " + e.Message);
            }
        }

        static void EnsureRotors()
        {
            if (rotors == null || rotors.Length == 0) rotors = FindRotors();
        }

        static bool IsInTunerWindow()
        {
            if (rotors == null) return false;
            for (int i = 0; i < rotors.Length; i++)
            {
                if (rotors[i] == null) continue;
                float r = rotors[i].Value;
                //  |2\pi defaults zero değil, raw derece saklanıyor (950-980).
                if (r >= MIN_ROT && r <= MAX_ROT) return true;
            }
            return false;
        }

        static void EnsureFileLoading()
        {
            if (clip != null || startedLoad) return;
            startedLoad = true;

            string dir = GetAssemblyPath();
            string mods = !string.IsNullOrEmpty(dir) ? Path.GetDirectoryName(dir) : null;
            if (string.IsNullOrEmpty(mods)) mods = "Mods";
            foreach (string ext in MUTE_EXTS)
            {
                string candidate = Path.Combine(mods, BASE_FILE + ext);
                if (File.Exists(candidate))
                {
                    CurrentFile = candidate;
                    string uri = "file:///" + candidate.Replace('\\', '/');
                    www = new WWW(uri);
                    SmpLog.Log("[CustomRadio] loading " + candidate);
                    return;
                }
            }
            if (!warnedOnce)
            {
                warnedOnce = true;
                SmpLog.Warning("[CustomRadio] not found: " + Path.Combine(mods, BASE_FILE + ".ogg") + " (or .wav/.mp3). Put the file there.");
            }
        }

        static string GetAssemblyPath()
        {
            try { return typeof(CustomRadio).Assembly.Location; }
            catch { return null; }
        }
    }
}
