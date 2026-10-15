using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MSCLoader;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace SteamMP
{
    /// <summary>
    /// HUD Geliştirmeleri: Name Tags + Ping Göstergesi + Bağlantı Kalitesi
    ///
    /// Özellikler:
    /// - Oyuncu üzerindeki isim etiketi (Name Tag) - mesafeye göre ölçeklenir
    /// - Ping göstergesi (ms) - renk kodlu (yeşil/sarı/kırmızı)
    /// - Bağlantı kalitesi çubuğu (packet loss %'si)
    /// - Araç içindeyken sürücü/yolcu göstergesi
    /// - Mesafeye göre fade out (uzakta küçük/gizli)
    ///
    /// İŞ PARÇACIĞI: BU DOSYA TAMAMEN ANA İŞ PARÇACIĞINDA ÇALIŞIR.
    ///
    /// "lock (gate)" KALDIRILDI — inceleme notu doğru sonuca vardı, yanlış
    /// sebebe. Not, Unity API çağrılarının "ağ thread'inden" geldiğini
    /// varsayıyordu; bu modda ÖYLE BİR THREAD YOK:
    ///
    ///   SteamMPBehaviour.Update()            (ana iş parçacığı)
    ///     -> SteamTransport.Update()
    ///          -> SteamAPI.RunCallbacks()    (Steam iş çağrıları)
    ///          -> RoutePacket -> OnHandshake -> OnPeerConnected event'i
    ///               -> HUDEnhancements.RegisterPlayer
    ///          -> Dispatch -> ModIntegration.TryHandle -> TextChat.OnMessage
    ///     -> ModIntegration.Update() -> HUDEnhancements.Update()
    ///
    /// `new Thread`, `Task.Run`, `ThreadPool.QueueUserWorkItem` veya
    /// benzeri HİÇBİR şey yok (tüm kaynaklarda arandı). Yani lock burada
    /// hiçbir yarışı önlemiyordu; yalnızca yanlış bir güvenlik izlenimi
    /// üretiyor ve Update() boyunca kare başına boşuna Monitor
    /// maliyeti ödüyordu.
    ///
    /// YERİNE KONAN: lock kaldırıldı ve ana iş parçacığı garantisi
    /// assert ile KODDA sabitlendi. İleride bir worker thread eklenirse
    /// Unity API'si sessizce bozulmayı bırakır, ilk çağrıda net bir
    /// hata verir.
    /// </summary>
    public static class HUDEnhancements
    {
        // --- Ayarlar ---
        public const float NAME_TAG_MAX_DISTANCE = 100f;      // Maksimum görünme mesafesi
        public const float NAME_TAG_FADE_START = 50f;         // Fade başlangıç mesafesi
        public const float NAME_TAG_HEIGHT = 2.5f;            // Oyuncu başı üzerindeki yükseklik
        public const float PING_UPDATE_INTERVAL = 1f;         // Ping güncelleme aralığı
        public const int PING_HISTORY_SIZE = 10;              // Ortalama için geçmiş boyutu

        // --- Ping Sınıflandırma ---
        public enum PingQuality : byte
        {
            Excellent = 0,    // < 50ms
            Good = 1,         // 50-100ms
            Fair = 2,         // 100-200ms
            Poor = 3,         // 200-500ms
            Critical = 4      // > 500ms
        }

        // --- Veri Yapıları ---
        public class PlayerHUDData
        {
            public ulong SteamId;
            public string Name;
            public GameObject TagObject;
            public Text NameText;
            public Text PingText;
            public Image PingBar;
            public Image ConnectionBar;
            public float LastPingUpdate;
            public readonly Queue<float> PingHistory = new Queue<float>(PING_HISTORY_SIZE);
            public float AveragePing = 0f;
            public PingQuality Quality = PingQuality.Excellent;
            public float PacketLoss = 0f;
            public bool IsInVehicle = false;
            public string VehicleName = "";
            public bool IsDriver = false;
        }

        // --- Durum ---
        static readonly Dictionary<ulong, PlayerHUDData> hudData = new Dictionary<ulong, PlayerHUDData>();
        static GameObject hudCanvas;
        static Camera mainCamera;
        static float nextPingRequest;

        /// <summary>
        /// Ana iş parçacığı doğrulaması.
        ///
        /// Unity API'leri (new GameObject, AddComponent, Object.Destroy)
        /// YALNIZCA ana iş parçacığından çağrılabilir. Bu modda her şey
        /// SteamMPBehaviour.Update() içinden akıyor, yani ana iş parçacığı;
        /// ama bu bir KURAL olarak burada sabitleniyor, varsayım olarak
        /// değil. Kural bozulursa (ileride worker thread) ilk çağrı net
        /// bir hata verir, sessizce bozulan UI olmaz.
        ///
        /// Maliyet: sadece bir int okuma + karşılaştırma, kare başına birkaç
        /// kez. Unity 5'te Application.isPlayingThreadIsMainThread YOK
        /// (5.3'te geliyor), bu yüzden referans thread sayılıyor.
        /// </summary>
        static readonly int mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        static bool mainThreadWarned;

        static void AssertMainThread(string who)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == mainThreadId) return;
            if (mainThreadWarned) return;
            mainThreadWarned = true;
            SmpLog.Error("[HUDEnhancements] " + who + " ANA İŞ PARÇACIĞINDAN DEĞİL. "
                + "Unity API'leri (GameObject/AddComponent/Destroy) yalnızca ana "
                + "iş parçacığından çağrılabilir; HUD burada bozulur. "
                + "Ağ işini ayrı bir thread'e taşıma, paketi kuyruğa al.");
        }

        // --- Public API ---

        public static void Initialize()
        {
            CreateHUDCanvas();
            mainCamera = Camera.main;
            SmpLog.Log("[HUDEnhancements] Başlatıldı");
        }

        static void CreateHUDCanvas()
        {
            // Oyunun canvas'ının altına eklenmiyor; TextChat de kendi
            // canvas'ını kuruyor. Sebep: oyun canvas'ı sahne değişiminde
            // yok oluyor, bizim etiketlerimiz kalıyor ya da tersi; ayrıca
            // CanvasScaler ayarı farklıysa isim etiketleri farklı boyutta
            // çıkar. sortingOrder sohbetin altında kalır.
            GameObject canvasObj = new GameObject("NameTagCanvas",
                typeof(RectTransform), typeof(Canvas));
            Canvas canvas = canvasObj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            UnityEngine.Object.DontDestroyOnLoad(canvasObj);

            hudCanvas = new GameObject("NameTags", typeof(RectTransform));
            hudCanvas.transform.SetParent(canvas.transform, false);
            RectTransform rect = hudCanvas.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;

            SmpLog.Debug("hud", "canvas", "isim etiketi canvas'ı hazır");
        }

        /// <summary>
        /// Oyuncu için HUD verisi oluşturur/günceller.
        /// </summary>
        public static void RegisterPlayer(ulong steamId, string name)
        {
            AssertMainThread("RegisterPlayer");
            PlayerHUDData data;
            if (!hudData.TryGetValue(steamId, out data))
            {
                data = new PlayerHUDData { SteamId = steamId, Name = name };
                CreateHUDElements(data);
                hudData[steamId] = data;
            }
            else
            {
                data.Name = name;
            }
        }

        static void CreateHUDElements(PlayerHUDData data)
        {
            if (hudCanvas == null) return;

            data.TagObject = new GameObject("NameTag_" + data.SteamId, typeof(RectTransform));
            data.TagObject.transform.SetParent(hudCanvas.transform, false);

            RectTransform rect = data.TagObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 60);

            // Background panel
            // Unity 5.0.0f4'te Graphic.raycastTarget YOK; fare NameTag'i
            // yemesin diye panelin collider'i hiç eklenmiyor (raycast yalnızca
            // raycastTarget=true graphic'lerde çalışır, yani burada kapalı).
            var bg = data.TagObject.AddComponent<Image>();
            bg.color = new Color(0, 0, 0, 0.6f);

            // Layout
            var layout = data.TagObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 4;
            layout.padding = new RectOffset(8, 8, 4, 4);
            layout.childForceExpandWidth = false;

            // Name Text
            GameObject nameObj = new GameObject("Name", typeof(RectTransform));
            nameObj.transform.SetParent(data.TagObject.transform, false);
            data.NameText = nameObj.AddComponent<Text>();
            data.NameText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            data.NameText.fontSize = 16;
            data.NameText.fontStyle = FontStyle.Bold;
            data.NameText.color = Color.white;
            data.NameText.alignment = TextAnchor.MiddleCenter;
            data.NameText.text = data.Name;
            data.NameText.resizeTextForBestFit = true;
            data.NameText.resizeTextMinSize = 10;
            data.NameText.resizeTextMaxSize = 18;

            // Ping Text
            GameObject pingObj = new GameObject("Ping", typeof(RectTransform));
            pingObj.transform.SetParent(data.TagObject.transform, false);
            data.PingText = pingObj.AddComponent<Text>();
            data.PingText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            data.PingText.fontSize = 12;
            data.PingText.color = Color.green;
            data.PingText.alignment = TextAnchor.MiddleCenter;
            data.PingText.text = "0ms";

            // Ping Bar (kalite göstergesi)
            GameObject pingBarObj = new GameObject("PingBar", typeof(RectTransform));
            pingBarObj.transform.SetParent(data.TagObject.transform, false);
            data.PingBar = pingBarObj.AddComponent<Image>();
            data.PingBar.color = Color.green;
            data.PingBar.type = Image.Type.Filled;
            data.PingBar.fillMethod = Image.FillMethod.Horizontal;
            data.PingBar.fillAmount = 1f;
            var pingBarRect = pingBarObj.GetComponent<RectTransform>();
            pingBarRect.sizeDelta = new Vector2(80, 4);

            // Connection Quality Bar (packet loss)
            GameObject connBarObj = new GameObject("ConnectionBar", typeof(RectTransform));
            connBarObj.transform.SetParent(data.TagObject.transform, false);
            data.ConnectionBar = connBarObj.AddComponent<Image>();
            data.ConnectionBar.color = Color.green;
            data.ConnectionBar.type = Image.Type.Filled;
            data.ConnectionBar.fillMethod = Image.FillMethod.Horizontal;
            data.ConnectionBar.fillAmount = 1f;
            var connBarRect = connBarObj.GetComponent<RectTransform>();
            connBarRect.sizeDelta = new Vector2(80, 4);
        }

        /// <summary>
        /// Oyuncu HUD verilerini günceller (ping, packet loss, araç durumu).
        /// </summary>
        public static void UpdatePlayerData(ulong steamId, float pingMs, float packetLoss, bool inVehicle = false, string vehicleName = "", bool isDriver = false)
        {
            PlayerHUDData data;
            if (!hudData.TryGetValue(steamId, out data)) return;

            // Ping geçmişini güncelle
            data.PingHistory.Enqueue(pingMs);
            if (data.PingHistory.Count > PING_HISTORY_SIZE)
                data.PingHistory.Dequeue();

            // Ortalama hesapla
            float sum = 0f;
            foreach (float p in data.PingHistory) sum += p;
            data.AveragePing = sum / data.PingHistory.Count;

            data.Quality = ClassifyPing(data.AveragePing);
            data.PacketLoss = packetLoss;
            data.IsInVehicle = inVehicle;
            data.VehicleName = vehicleName;
            data.IsDriver = isDriver;
            data.LastPingUpdate = Time.realtimeSinceStartup;
        }

        static PingQuality ClassifyPing(float ping)
        {
            if (ping < 50) return PingQuality.Excellent;
            if (ping < 100) return PingQuality.Good;
            if (ping < 200) return PingQuality.Fair;
            if (ping < 500) return PingQuality.Poor;
            return PingQuality.Critical;
        }

        /// <summary>
        /// Her kare çağrılır - NameTag pozisyonunu günceller, mesafeye göre fade.
        /// </summary>
        public static void Update()
        {
            // Ping isteğinin TEK zamanlayıcısı burada: HUD hazır olmadan da
            // bağlantı ölçülür, çünkü oyuncu listesi boşken bile ping gerekir.
            if (SteamTransport.HasRemote && Time.realtimeSinceStartup >= nextPingRequest)
                RequestPing(SteamTransport.RemoteId.m_SteamID);

            if (mainCamera == null) mainCamera = Camera.main;
            if (mainCamera == null) return;

            Vector3 localPos = GetLocalPlayerPosition();
            float now = Time.realtimeSinceStartup;

            // Eski verileri temizle (5 sn güncellenmediyse)
            staleHud.Clear();
            foreach (var kvp in hudData)
            {
                if (now - kvp.Value.LastPingUpdate > 5f)
                    staleHud.Add(kvp.Key);
            }
            // Dikkat: hudData içinde Delete edilip Iterate EDİLMEZ.
            // Anahtarlar önce toplanır, döngü bittikten sonra uygulanır;
            // aksi hâlde InvalidOperationException.
            for (int i = 0; i < staleHud.Count; i++)
            {
                ulong id = staleHud[i];
                if (hudData[id].TagObject != null)
                    UnityEngine.Object.Destroy(hudData[id].TagObject);
                hudData.Remove(id);
            }

            // Aktif HUD'ları güncelle
            foreach (var kvp in hudData)
            {
                UpdateHUDPosition(kvp.Value, localPos);
            }
        }

        /// <summary>Update içinde doldurulur, döngü sonunda uygulanır (kare başına tahsis yok).</summary>
        static readonly List<ulong> staleHud = new List<ulong>(4);

        static void UpdateHUDPosition(PlayerHUDData data, Vector3 localPos)
        {
            if (data.TagObject == null || data.TagObject.transform == null) return;

            // Oyuncu nesnesini bul (RemotePlayerBehaviour veya Local Player)
            Transform targetTransform = FindPlayerTransform(data.SteamId);
            if (targetTransform == null)
            {
                data.TagObject.SetActive(false);
                return;
            }

            // Dünya pozisyonu -> Screen pozisyonu
            Vector3 worldPos = targetTransform.position + Vector3.up * NAME_TAG_HEIGHT;
            Vector3 screenPos = mainCamera.WorldToScreenPoint(worldPos);

            // Kamera arkasında mı?
            if (screenPos.z < 0)
            {
                data.TagObject.SetActive(false);
                return;
            }

            // Mesafe hesapla
            float dist = Vector3.Distance(localPos, targetTransform.position);

            // Maksimum mesafe kontrolü
            if (dist > NAME_TAG_MAX_DISTANCE)
            {
                data.TagObject.SetActive(false);
                return;
            }

            data.TagObject.SetActive(true);

            // Screen koordinatlarını Canvas koordinatlarına çevir
            RectTransform canvasRect = hudCanvas.GetComponent<RectTransform>();
            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect, screenPos, null, out localPoint);

            data.TagObject.GetComponent<RectTransform>().anchoredPosition = localPoint;

            // Mesafeye göre alpha (fade)
            float alpha = 1f;
            if (dist > NAME_TAG_FADE_START)
            {
                alpha = Mathf.Lerp(1f, 0f, (dist - NAME_TAG_FADE_START) / (NAME_TAG_MAX_DISTANCE - NAME_TAG_FADE_START));
            }

            // UI elemanlarının alpha'sını ayarla
            var canvasGroup = data.TagObject.GetComponent<CanvasGroup>();
            if (canvasGroup == null) canvasGroup = data.TagObject.AddComponent<CanvasGroup>();
            canvasGroup.alpha = alpha;

            // Ping/Connection UI güncelle
            UpdateHUDVisuals(data);
        }

        static void UpdateHUDVisuals(PlayerHUDData data)
        {
            if (data.NameText != null)
            {
                // İsim + araç durumu
                string displayName = data.Name;
                if (data.IsInVehicle)
                {
                    displayName += data.IsDriver ? " 🚗" : " 🚙";
                    if (!string.IsNullOrEmpty(data.VehicleName))
                        displayName += " (" + data.VehicleName + ")";
                }
                data.NameText.text = displayName;
            }

            if (data.PingText != null)
            {
                data.PingText.text = data.AveragePing.ToString("F0") + "ms";
                data.PingText.color = GetPingColor(data.Quality);
            }

            if (data.PingBar != null)
            {
                data.PingBar.fillAmount = GetPingFillAmount(data.Quality);
                data.PingBar.color = GetPingColor(data.Quality);
            }

            if (data.ConnectionBar != null)
            {
                data.ConnectionBar.fillAmount = 1f - data.PacketLoss;
                data.ConnectionBar.color = data.PacketLoss < 0.05f ? Color.green : 
                                           data.PacketLoss < 0.2f ? Color.yellow : Color.red;
            }
        }

        static Color GetPingColor(PingQuality q)
        {
            switch (q)
            {
                case PingQuality.Excellent: return Color.green;
                case PingQuality.Good: return new Color(0.5f, 1f, 0f); // Light green
                case PingQuality.Fair: return Color.yellow;
                case PingQuality.Poor: return new Color(1f, 0.5f, 0f); // Orange
                case PingQuality.Critical: return Color.red;
                default: return Color.white;
            }
        }

        static float GetPingFillAmount(PingQuality q)
        {
            switch (q)
            {
                case PingQuality.Excellent: return 1f;
                case PingQuality.Good: return 0.8f;
                case PingQuality.Fair: return 0.6f;
                case PingQuality.Poor: return 0.4f;
                case PingQuality.Critical: return 0.2f;
                default: return 1f;
            }
        }

        // --- Yardımcı Metotlar ---

        static Transform FindPlayerTransform(ulong steamId)
        {
            // Local player
            if (steamId == SteamUser.GetSteamID().m_SteamID)
            {
                GameObject player = GameObject.Find("PLAYER");
                if (player != null) return player.transform;
                return null;
            }

            // Remote player
            var remoteBody = RemotePlayerSync.Body;
            if (remoteBody != null && RemotePlayerSync.Body != null)
            {
                // RemotePlayerSync'te steamId kontrolü yok, sadece bir remote player varsa o
                // Çoklu oyuncu desteği eklenirse burası genişletilecek
                return remoteBody.transform;
            }

            return null;
        }

        static Vector3 GetLocalPlayerPosition()
        {
            Vector3 pos;
            if (PlayerMotion.TryGetPosition(out pos)) return pos;
            
            Camera cam = Camera.main != null ? Camera.main : Camera.current;
            if (cam != null) return cam.transform.position;
            
            return Vector3.zero;
        }

        /// <summary>
        /// Ping isteği gönderir (Ping message ile).
        /// </summary>
        public static void RequestPing(ulong targetId)
        {
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.Ping);
                BinaryWriter w = new BinaryWriter(s);
                // Karşı tarafın saatimizi kendi saatine çevirebilmesi için
                // AYNI referansı kullanıyoruz: SteamTransport'in kendi saati.
                // Time.realtimeSinceStartup iki makinede aynı değeri vermez,
                // RTT hesabı bu yüzden Negatif çıkardı.
                w.Write(SteamTransport.NetClockMs());
                // Tek peer'lı oturumda hedef zaten bağlı peer; SendTo
                // yok, giden kanallar yeterli (Steam P2P zaten tek hedefe gider).
                SteamTransport.SendUnreliableNoDelay(Protocol.Finish(s));
                Telemetry.SentPings++;
                nextPingRequest = Time.realtimeSinceStartup + PING_UPDATE_INTERVAL;
            }
            catch (Exception e)
            {
                SmpLog.Warning("[HUD] ping isteği gönderilemedi: " + e.Message);
            }
        }

        /// <summary>
        /// Gelen Ping isteğine yanıt verir.
        /// </summary>
        public static void OnPingRequest(ulong stamp)
        {
            try
            {
                // İsteği olduğu gibi geri yansıt: isteği gönderen taraf kendi
                // saatinden farkı alıp RTT olarak kullanıyor.
                MemoryStream s = Protocol.Begin(Protocol.Msg.Ping);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(stamp);
                SteamTransport.SendUnreliableNoDelay(Protocol.Finish(s));
                Telemetry.ReceivedPings++;
            }
            catch (Exception e)
            {
                SmpLog.Warning("[HUD] ping yanıtı gönderilemedi: " + e.Message);
            }
        }

        /// <summary>
        /// Ping yanıtı alır, RTT hesaplar.
        /// </summary>
        public static void OnPingResponse(ulong sentAt)
        {
            try
            {
                ulong now = SteamTransport.NetClockMs();

                // Saat farkı çıkabilir (özellikle eşitsiz iki makinede); RRT
                // olarak yalnızca pozitif ve makul olanı kabul et, aksi hâlde
                // HUD'da binlerce ms'lık saçma değerler görünürdü.
                if (now <= sentAt) return;
                float rttMs = (float)(now - sentAt);
                if (rttMs > 10000f) return;

                ulong senderId = SteamTransport.RemoteId.m_SteamID;
                UpdatePlayerData(senderId, rttMs, 0f);
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[HUD] ping yanıtı okunamadı: " + e.Message);
            }
        }

        /// <summary>
        /// Oyuncu ayrıldığında HUD'ı temizle.
        /// </summary>
        public static void UnregisterPlayer(ulong steamId)
        {
            AssertMainThread("UnregisterPlayer");
            PlayerHUDData data;
            if (hudData.TryGetValue(steamId, out data))
            {
                if (data.TagObject != null)
                    UnityEngine.Object.Destroy(data.TagObject);
                hudData.Remove(steamId);
            }
        }

        public static void Clear()
        {
            AssertMainThread("Clear");
            foreach (var kvp in hudData)
            {
                if (kvp.Value.TagObject != null)
                    UnityEngine.Object.Destroy(kvp.Value.TagObject);
            }
            hudData.Clear();

            // Canvas kalıcı (DontDestroyOnLoad) ama sahne değişiminde yeniden
            // kurulması gerekiyor; yoksa Initialize tekrar çağrıldığında
            // etiketler eski (ölü) canvas'a eklenirdi.
            if (hudCanvas != null)
            {
                UnityEngine.Object.Destroy(hudCanvas);
                hudCanvas = null;
            }
            mainCamera = null;
        }

        public static string Describe()
        {
            return "HUD: tracked=" + hudData.Count;
        }
    }
}