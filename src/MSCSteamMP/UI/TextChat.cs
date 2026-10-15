using System;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MSCLoader;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace SteamMP
{
    /// <summary>
    /// Metin Sohbeti (Text Chat) Sistemi
    ///
    /// Özellikler:
    /// - Global / Team / Whisper kanalları
    /// - Steam Rich Presence entegrasyonu (opsiyonel)
    /// - Mesaj geçmişi (scrollable UI)
    /// - Basit komut desteği (/w, /me, /roll)
    /// - Anti-spam (rate limiting)
    /// - UTF-8 desteği (Türkçe karakterler)
    /// </summary>
    public static class TextChat
    {
        // --- Ayarlar ---
        public const int MAX_HISTORY = 200;           // Geçmişte tutulacak mesaj sayısı
        public const float SEND_COOLDOWN = 1.5f;      // Mesaj gönderme aralığı (saniye)
        public const int MAX_MESSAGE_LENGTH = 256;    // Maksimum mesaj uzunluğu

        // OTOMATİK KAPANMA KALDIRILDI. Eski UI_FADE_TIME = 10 s idi ve
        // menü açıkken yazmaya başlamadan önce kayboluyordu; kullanıcı
        // "çok sorunlu" dedi. Artık menü YALNIZCA T / Enter / Escape ile
        // kapanır — bunun dışında hiçbir koşulda kaybolmaz.
        //
        // ŞEFFAFLIK: kullanıcı isteği. 0.7/0.9 opaklık arkasındaki oyunu
        // tamamen kesiyordu; oyun görünür kalmalı.
        public const float PANEL_ALPHA = 0.32f;       // Sohbet paneli zemin opaklığı
        public const float INPUT_ALPHA = 0.55f;       // Yazı kutusu / kanal düğmesi

        // --- Kanallar ---
        public enum ChatChannel : byte
        {
            Global = 0,       // Herkes görür
            Team = 1,         // Takım/araç arkadaşları
            Whisper = 2,      // Özel mesaj
            System = 3        // Sistem mesajları (join/leave, Ölüm, vb.)
        }

        // --- Veri Yapıları ---
        public struct ChatMessage
        {
            public ChatChannel Channel;
            public ulong SenderSteamId;
            public string SenderName;
            public string Message;
            /// <summary>Saat etiketi ("21:48:05"). KABUL EDİLMEZ çünkü
            /// <c>Time.realtimeSinceStartup</c> iki makinede farklı bir başlangıca
            /// sahip; ekranda gösterilecek saat için duvar saati gerekir.</summary>
            public string Time;
            public ulong TargetSteamId; // Whisper için hedef
        }

        // --- Durum ---
        static readonly List<ChatMessage> history = new List<ChatMessage>(MAX_HISTORY);
        static float lastSendTime = -999f;
        static float uiVisibleTime = -999f;
        static bool uiVisible = false;
        static int unreadCount = 0;
        static Text unreadText;
        static bool uiDirty = true;
        static bool wasStuck = true;
        /// <summary>Tek seferde cizilen en fazla satir (duzen maliyeti sinirli).</summary>
        public const int RENDER_LINES = 60;
        static string pendingInput = "";
        static ChatChannel activeChannel = ChatChannel.Global;
        static ulong whisperTarget = 0;

        // UI Referansları
        static GameObject chatPanel;
        static Text chatText;
        static InputField inputField;
        static ScrollRect scrollRect;
        static Button channelButton;
        static Text channelLabel;

        // --- Public API ---

        /// <summary>Chat sistemini başlatır (UI oluşturur).</summary>
        public static void Initialize()
        {
            CreateUI();
            SmpLog.Log("[TextChat] Başlatıldı");
        }

        /// <summary>
        /// Bir panelin etrafına 1px kenarlık şeritleri çizer.
        ///
        /// NEDEN ELLE: Unity 5.0.0f4'te UnityEngine.UI.Outline YOK (dnSpy ile
        /// teyit edildi — bu sürümde yalnızca Shadow var). Kenarlığı dört ince
        /// Image şeriditle çizmek tek yürüyülebilir yol; böylece loş kutunun
        /// kenarı oyunun arka planında kaybolmuyor.
        /// </summary>
        static void DrawBorder(Transform parent, Color color, float thickness)
        {
            // Üst, alt, sol, sağ. Her şerit 1px ve dış kenara yapışık.
            Strip(parent, "BorderTop", new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -thickness), new Vector2(0f, 0f), color);
            Strip(parent, "BorderBottom", new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(0f, thickness), color);
            Strip(parent, "BorderLeft", new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(thickness, 0f), color);
            Strip(parent, "BorderRight", new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(-thickness, 0f), new Vector2(0f, 0f), color);
        }

        /// <summary>Tek kenarlık şeridi. Kenar çapaları ve offset'leri verilir.</summary>
        static void Strip(Transform parent, string name, Vector2 aMin, Vector2 aMax,
            Vector2 offMin, Vector2 offMax, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
            var img = go.AddComponent<Image>();
            img.color = color;
        }

        static void CreateUI()
        {
            // KENDİ canvas'ımızı kuruyoruz; oyunun canvas'ının altına
            // eklenmiyor. Sebep: oyun canvas'ının CanvasScaler ayarı farklıysa
            // metinler farklı boyutta çıkar ve oyun sahne değiştirince
            // sohbet paneli yok olurken bizim nesnelerimiz kalıyor ya da
            // tersi. Ayrı canvas iki yerde de aynı sorunu çözer.
            GameObject canvasObj = new GameObject("ChatCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            Canvas canvas = canvasObj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            UnityEngine.Object.DontDestroyOnLoad(canvasObj);

            // Chat Panel
            chatPanel = new GameObject("ChatPanel", typeof(RectTransform));
            chatPanel.transform.SetParent(canvas.transform, false);
            // RectTransform nesne oluşturulurken eklendi; tekrar eklemek
            // Unity 5'te "Transform zaten var" hatası veriyor.
            RectTransform panelRect = chatPanel.GetComponent<RectTransform>();

            // BOYUT: sabit piksel, çözünürlükten bağımsısa yakın.
            //
            // ESKİ HÂL (%40 x %35): 1920x1080'de 768x378 piksel — ekranın
            // beşte birini kaplayan, oyunun görüş alanının ortasına yayılan
            // devasa bir kutu. Kullanıcı "çok berbat görünüyor" dediği tam
            // olarak buydu. Bir sohbet kutusu okunabilir bir GENİŞLİK ve
            // birkaç satırlık YÜKSEKLİK ister; ekranı kapatmaz.
            //
            // Artık: genişlik ekranın ~%27'si (320-540 px arası), yükseklik
            // ~%22'si (130-260 px arası). Alt-solda sabit konum.
            float pw = Mathf.Clamp(Screen.width * 0.27f, 320f, 540f);
            float ph = Mathf.Clamp(Screen.height * 0.22f, 130f, 260f);
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 0f);
            panelRect.pivot = new Vector2(0f, 0f);
            panelRect.sizeDelta = new Vector2(pw, ph);
            panelRect.anchoredPosition = new Vector2(12f, 12f);

            // ŞEFFAF TASARIM: kullanıcı isteği. Arkasındaki oyun görünür
            // kalmalı; kutu yalnızca okunabilirlik için loş bir zemin.
            var bg = chatPanel.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, PANEL_ALPHA);

            // 1px KENARLIK ŞERİTLERİ. Unity 5.0.0f4'te UnityEngine.UI.Outline
            // YOK (dnSpy ile teyit edildi: yalnızca Shadow var), o yüzden
            // kenarlık dört ince Image şeridiyle çiziliyor. Böyle kutu
            // "yapışkan bir yamâ" gibi değil, bilinçli bir panel gibi duruyor.
            DrawBorder(chatPanel.transform, new Color(1f, 1f, 1f, 0.22f), 1f);

            // Chat Text (ScrollView içine)
            GameObject scrollObj = new GameObject("ScrollView", typeof(RectTransform));
            scrollObj.transform.SetParent(chatPanel.transform, false);
            RectTransform scrollRectT = scrollObj.GetComponent<RectTransform>();
            scrollRectT.anchorMin = Vector2.zero;
            scrollRectT.anchorMax = Vector2.one;
            scrollRectT.offsetMin = new Vector2(4, 29);
            scrollRectT.offsetMax = new Vector2(-4, -29);

            scrollRect = scrollObj.AddComponent<ScrollRect>();

            GameObject contentObj = new GameObject("Content", typeof(RectTransform));
            contentObj.transform.SetParent(scrollObj.transform, false);
            RectTransform contentRect = contentObj.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);

            var verticalLayout = contentObj.AddComponent<VerticalLayoutGroup>();
            verticalLayout.childAlignment = TextAnchor.UpperLeft;
            verticalLayout.spacing = 2;
            verticalLayout.padding = new RectOffset(5, 5, 5, 5);

            var contentSizeFitter = contentObj.AddComponent<ContentSizeFitter>();
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = contentRect;
            scrollRect.vertical = true;
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            // Chat Text (Prefab olarak mesaj objeleri oluşturulacak)
            chatText = contentObj.AddComponent<Text>();
            chatText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            chatText.fontSize = 14;
            chatText.color = Color.white;
            chatText.supportRichText = true;
            chatText.horizontalOverflow = HorizontalWrapMode.Wrap;
            chatText.verticalOverflow = VerticalWrapMode.Overflow;

            // Input Field
            GameObject inputObj = new GameObject("InputField", typeof(RectTransform));
            inputObj.transform.SetParent(chatPanel.transform, false);
            RectTransform inputRect = inputObj.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0, 0);
            inputRect.anchorMax = new Vector2(1, 0);
            inputRect.sizeDelta = new Vector2(0, 26);
            inputRect.anchoredPosition = new Vector2(2, 3);

            var inputBg = inputObj.AddComponent<Image>();
            inputBg.color = new Color(0.08f, 0.09f, 0.11f, INPUT_ALPHA);

            inputField = inputObj.AddComponent<InputField>();

// Unity 5.0.0f4'te textComponent ve placeholder ÇOCUK nesne olmak
            // zorunda (Graphic tek kök üzerinde). Aynı GameObject'e ikinci
            // Graphic eklemek "Graphic is already on this object" hatası verir.
            GameObject textObj = new GameObject("Text", typeof(RectTransform));
            textObj.transform.SetParent(inputObj.transform, false);
            RectTransform tRect = textObj.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.offsetMin = new Vector2(6, 2);
            tRect.offsetMax = new Vector2(-6, -2);
            Text inputText = textObj.AddComponent<Text>();
            inputText.font = chatText.font;
            inputText.fontSize = 14;
            inputText.color = Color.white;
            inputText.alignment = TextAnchor.MiddleLeft;
            inputField.textComponent = inputText;

GameObject phObj = new GameObject("Placeholder", typeof(RectTransform));
            phObj.transform.SetParent(inputObj.transform, false);
            RectTransform phRect = phObj.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(6, 2);
            phRect.offsetMax = new Vector2(-6, -2);
            Text phText = phObj.AddComponent<Text>();
            phText.text = "Mesaj yazın... (Enter gönderir)";
            phText.font = chatText.font;
            phText.fontSize = 14;
            phText.color = Color.gray;
            phText.alignment = TextAnchor.MiddleLeft;
            inputField.placeholder = phText;

inputField.characterLimit = MAX_MESSAGE_LENGTH;
            inputField.onEndEdit.AddListener(OnInputSubmitted);

            // Baslik seridi (26px): kanal dugmesi solda, okunmadi rozeti sagda.
            // Ustu 2px aksan seridiyle sillenir: panelin nerede bittigi
            // arkasindaki oyunda kayismasin.
            GameObject headerObj = new GameObject("Header", typeof(RectTransform));
            headerObj.transform.SetParent(chatPanel.transform, false);
            RectTransform headerRect = headerObj.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.offsetMin = new Vector2(1, -27);
            headerRect.offsetMax = new Vector2(-1, -1);
            var headerImg = headerObj.AddComponent<Image>();
            headerImg.color = new Color(0.13f, 0.14f, 0.16f, 0.80f);

            // 2px aksan seridi: basligin alt kenarinda, modun vurgu rengi.
            GameObject accentObj = new GameObject("Accent", typeof(RectTransform));
            accentObj.transform.SetParent(headerObj.transform, false);
            RectTransform accentRect = accentObj.GetComponent<RectTransform>();
            accentRect.anchorMin = new Vector2(0, 0);
            accentRect.anchorMax = new Vector2(1, 0);
            accentRect.offsetMin = new Vector2(0, 0);
            accentRect.offsetMax = new Vector2(0, 2);
            var accentImg = accentObj.AddComponent<Image>();
            accentImg.color = new Color(0.36f, 0.69f, 0.94f, 0.95f);

            GameObject unreadObj = new GameObject("Unread", typeof(RectTransform));
            unreadObj.transform.SetParent(headerObj.transform, false);
            RectTransform unRect = unreadObj.GetComponent<RectTransform>();
            unRect.anchorMin = new Vector2(1, 0);
            unRect.anchorMax = new Vector2(1, 1);
            unRect.offsetMin = new Vector2(-104, 2);
            unRect.offsetMax = new Vector2(-4, -2);
            unreadText = unreadObj.AddComponent<Text>();
            unreadText.font = chatText.font;
            unreadText.fontSize = 12;
            unreadText.alignment = TextAnchor.MiddleRight;
            unreadText.color = Color.yellow;
            unreadText.supportRichText = false;
            unreadText.text = "";

            // Channel Dropdown
            GameObject dropdownObj = new GameObject("ChannelButton", typeof(RectTransform));
            dropdownObj.transform.SetParent(headerObj.transform, false);
            RectTransform ddRect = dropdownObj.GetComponent<RectTransform>();
            ddRect.anchorMin = new Vector2(0, 0);
            ddRect.anchorMax = new Vector2(0, 1);
            ddRect.offsetMin = new Vector2(4, 2);
            ddRect.offsetMax = new Vector2(114, -2);

            // Unity 5.0.0f4'te UnityEngine.UI'da Dropdown YOK (5.1 ile geldi).
            // Onun yerine kanalı döngüleyen bir Button: her tik bir sonraki kanal.
            var ddImg = dropdownObj.AddComponent<Image>();
            ddImg.color = new Color(0.15f, 0.16f, 0.18f, INPUT_ALPHA);
            channelButton = dropdownObj.AddComponent<Button>();
            channelButton.targetGraphic = ddImg;

            GameObject labelObj = new GameObject("Label", typeof(RectTransform));
            labelObj.transform.SetParent(dropdownObj.transform, false);
            RectTransform lRect = labelObj.GetComponent<RectTransform>();
            lRect.anchorMin = Vector2.zero;
            lRect.anchorMax = Vector2.one;
            lRect.offsetMin = new Vector2(4, 0);
            lRect.offsetMax = new Vector2(-4, 0);
            channelLabel = labelObj.AddComponent<Text>();
            channelLabel.font = chatText.font;
            channelLabel.fontSize = 14;
            channelLabel.alignment = TextAnchor.MiddleCenter;
            channelLabel.color = Color.white;
            UpdateChannelLabel();

            channelButton.onClick.AddListener(CycleChannel);

            // Başlangıçta gizle
            chatPanel.SetActive(false);
        }

        /// <summary>Ekanda gösterilecek duvar saati etiketi.</summary>
static string ClockStamp()
        {
            return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Unity 5.0.0f4'te <c>ColorUtility</c> yok. Beş sabit rengin adını
        /// elle veriyoruz; yeni renk eklenirse buraya da eklenmeli.
        /// </summary>
        static string HtmlColor(Color c)
        {
            if (c == Color.white) return "#FFFFFF";
            if (c == Color.yellow) return "#FFFF00";
            if (c == Color.cyan) return "#00FFFF";
            if (c == Color.magenta) return "#FF00FF";
            return "#FFFFFF";
        }

        static string typingName = "";
        static float typingUntil;
        static float lastTypingPing;

        /// <summary>Yaziyor gostergesini kanal etiketine isler.</summary>
        static void RefreshTypingLabel()
        {
            if (channelLabel == null) return;
            UpdateChannelLabelBase();
            if (!string.IsNullOrEmpty(typingName)
                && Time.realtimeSinceStartup < typingUntil) {
                string tn = typingName.Length > 12 ? typingName.Substring(0, 12) + "…" : typingName;
                channelLabel.text += "  •  " + tn + " yazıyor...";
            }
        }

        static void UpdateChannelLabelBase()
        {
            if (channelLabel == null) return;
            switch (activeChannel)
            {
                case ChatChannel.Team: channelLabel.text = "Team"; break;
                case ChatChannel.Whisper: channelLabel.text = "Whisper"; break;
                default: channelLabel.text = "Global"; break;
            }
        }

        static void UpdateChannelLabel()
        {
            UpdateChannelLabelBase();
            RefreshTypingLabel();
        }

        /// <summary>Yaziyor pingsel (karsi taraf).</summary>
        public static void OnTyping(BinaryReader reader)
        {
            try
            {
                ulong senderId = reader.ReadUInt64();
                string senderName = reader.ReadString();
                if (senderId == SteamUser.GetSteamID().m_SteamID) return;
                if (string.IsNullOrEmpty(senderName)) senderName = "Peer";
                typingName = senderName;
                typingUntil = Time.realtimeSinceStartup + 5f;
                RefreshTypingLabel();
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[TextChat] Typing paketi hatası: " + e.Message);
            }
        }

        static void ClearTyping()
        {
            if (typingName.Length == 0) return;
            typingName = "";
            RefreshTypingLabel();
        }

        /// <summary>Kanalı Global -> Team -> Whisper -> Global şeklinde döngüler.</summary>
        static void CycleChannel()
        {
            switch (activeChannel)
            {
                case ChatChannel.Global: activeChannel = ChatChannel.Team; break;
                case ChatChannel.Team: activeChannel = ChatChannel.Whisper; break;
                default: activeChannel = ChatChannel.Global; break;
            }
            UpdateChannelLabel();
        }

        // --- Input Handling ---

        static void OnInputSubmitted(string text)
        {
            // Kullanıcı isteği:
            //   Enter -> mesaj GİTSİN ve menü KAPANSIN.
            //   Kutu boşsa bile Enter KAPANSIN (eskiden boş metinde
            //   `return` edip menüyü açık bırakıyordu).
            //
            // Unity 5.0 legacy InputField'da onEndEdit yalnızca Enter'a
            // değil, odak KAYBINA da tetiklenir. Bu yüzden mesaj
            // gönderimi burada, KAPATMA da burada yapılır: odak kaybı
            // da menüyü kapatır (beklenen davranış).
            if (!string.IsNullOrEmpty(text) && text.Trim().Length > 0)
                SendMessage(text);
            SubmitAndClose();
        }

        /// <summary>Metni temizler, menüyü kapatır ve oyun girdisini geri verir.</summary>
        static void SubmitAndClose()
        {
            if (inputField != null) inputField.text = "";
            HideUI();
        }

        /// <summary>Menüyü kapatır; yazma odakını bırakır (klavye oyuna döner).</summary>
        static void CloseAndRelease()
        {
            if (inputField != null)
            {
                inputField.text = "";
                // Odak bırakılmazsa WASD harfleri yazmaya devam eder.
                inputField.DeactivateInputField();
            }
            HideUI();
        }

        static void OnChannelChanged(int value)
        {
            activeChannel = (ChatChannel)value;
            UpdateChannelLabel();
        }

        /// <summary>
        /// Chat mesajı gönderir.
        /// </summary>
        public static void SendMessage(string message, ChatChannel channel = ChatChannel.Global, ulong targetId = 0)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastSendTime < SEND_COOLDOWN)
            {
                AddSystemMessage("Çok hızlı yazıyorsunuz! " + (SEND_COOLDOWN - (now - lastSendTime)).ToString("F1") + " sn bekleyin.");
                return;
            }

            if (message.Length > MAX_MESSAGE_LENGTH)
                message = message.Substring(0, MAX_MESSAGE_LENGTH);

            // Komut işle
            if (message.StartsWith("/"))
            {
                ProcessCommand(message);
                return;
            }

            // Whisper hedef kontrolü
            if (channel == ChatChannel.Whisper && targetId == 0)
            {
                AddSystemMessage("Whisper için hedef belirtilmeli: /w <isim> <mesaj>");
                return;
            }

            lastSendTime = now;

            // Mesajı paketle ve gönder
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.TextChat);
                BinaryWriter w = new BinaryWriter(s);

                w.Write((byte)channel);
                w.Write(SteamUser.GetSteamID().m_SteamID);
                w.Write(SteamFriends.GetPersonaName() ?? "Unknown");
                w.Write(message);
                w.Write(targetId);
                // GÖNDERENİN saati (v10).
                //
                // NEDEN: alıcı taraf kendi ClockStamp()'ini koyuyordu, yani
                // ekranda görünen zaman "mesaj ne zaman VARDI" değil
                // "ne zaman GELDİ" idi. Aynı mesaj için gönderen "14:30:05",
                // alıcı "14:30:11" görüyordu; ping yüksekse fark büyür ve
                // sohbet kronolojisi tutmazdı.
                //
                // ClockStamp() duvar saati (DateTime.Now) üretir, yani iki
                // makine arasında doğrudan karşılaştırılabilir; saat
                // senkronizasyonu gerekmez.
                w.Write(ClockStamp());

                if (SteamTransport.SendReliable(Protocol.Finish(s)))
                {
                    Telemetry.SentChatMessages++;
                    // Kendi mesajımızı hemen local'e ekle (echo)
                    AddMessage(new ChatMessage
                    {
                        Channel = channel,
                        SenderSteamId = SteamUser.GetSteamID().m_SteamID,
                        SenderName = SteamFriends.GetPersonaName() ?? "You",
                        Message = message,
                        Time = ClockStamp(),
                        TargetSteamId = targetId
                    });
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("[TextChat] Gönderim hatası: " + e.Message);
            }
        }

        static void ProcessCommand(string cmd)
        {
            string[] parts = cmd.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLowerInvariant();

            switch (command)
            {
                case "/w":
                case "/whisper":
                    if (parts.Length >= 3)
                    {
                        string targetName = parts[1];
                        string msg = parts[2];
                        ulong targetId = FindPlayerByName(targetName);
                        if (targetId != 0)
                        {
                            whisperTarget = targetId;
                            activeChannel = ChatChannel.Whisper;
                            UpdateChannelLabel();
                            SendMessage(msg, ChatChannel.Whisper, targetId);
                            AddSystemMessage("(To " + targetName + ") " + msg);
                        }
                        else
                        {
                            AddSystemMessage("Oyuncu bulunamadı: " + targetName);
                        }
                    }
                    else if (parts.Length == 2)
                    {
                        ulong targetId = FindPlayerByName(parts[1]);
                        if (targetId != 0)
                        {
                            whisperTarget = targetId;
                            activeChannel = ChatChannel.Whisper;
                            UpdateChannelLabel();
                            AddSystemMessage("Whisper hedefi: " + parts[1]);
                        }
                        else
                        {
                            AddSystemMessage("Oyuncu bulunamadı: " + parts[1]);
                        }
                    }
                    else
                    {
                        AddSystemMessage("Kullanım: /w <oyuncu_adı> [mesaj]");
                    }
                    break;

                case "/me":
                    if (parts.Length >= 2)
                    {
                        SendMessage("* " + SteamFriends.GetPersonaName() + " " + parts[1]);
                    }
                    else
                    {
                        AddSystemMessage("Kullanım: /me <eylem>");
                    }
                    break;

                case "/roll":
                    int roll = UnityEngine.Random.Range(1, 101);
                    SendMessage("zar attı: " + roll + "/100");
                    break;

                case "/clear":
                    history.Clear();
                    RefreshUI();
                    break;

                case "/help":
                    AddSystemMessage("Komutlar: /w <isim> <mesaj>, /me <eylem>, /roll, /clear, /help");
                    break;

                default:
                    AddSystemMessage("Bilinmeyen komut: " + command + " (/help için yardım)");
                    break;
            }
        }

        static ulong FindPlayerByName(string name)
        {
            // RemotePlayerSync veya SteamFriends üzerinden bul
            // Basit implementasyon: RemotePlayerSync.PeerName kontrolü
            if (RemotePlayerSync.PeerName != null && 
                RemotePlayerSync.PeerName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return SteamTransport.RemoteId.m_SteamID;
            }
            return 0;
        }

        /// <summary>
        /// Gelen chat mesajını işler.
        /// </summary>
        public static void OnMessage(BinaryReader reader)
        {
            try
            {
                ChatChannel channel = (ChatChannel)reader.ReadByte();
                ulong senderId = reader.ReadUInt64();
                string senderName = reader.ReadString();
                string message = reader.ReadString();
                ulong targetId = reader.ReadUInt64();
                // Gönderenin saati (v10). Eski build'de bu alan yoktur; o
                // durumda akış zaten burada biter, o yüzden ayrı koruma
                // gerekmez (PROTOCOL_VERSION el sıkışmada bunu zaten
                // reddeder). Yine de boş/bozuk gelirse yerel saat düşer.
                string stamp = reader.ReadString();

                Telemetry.ReceivedChatMessages++;

                // Kendi mesajımız mı? (Echo kontrolü)
                if (senderId == SteamUser.GetSteamID().m_SteamID) return;

                if (string.IsNullOrEmpty(stamp)) stamp = ClockStamp();

                var msg = new ChatMessage
                {
                    Channel = channel,
                    SenderSteamId = senderId,
                    SenderName = senderName,
                    Message = message,
                    // GÖNDERENİN saati, alıcının değil. Artık iki taraf aynı
                    // mesaj için aynı zamanı görüyor.
                    Time = stamp,
                    TargetSteamId = targetId
                };

                ClearTyping();
                AddMessage(msg);

                // Whisper ise sesli bildirim
                if (channel == ChatChannel.Whisper && targetId == SteamUser.GetSteamID().m_SteamID)
                {
                    // Bildirim sesi çal (opsiyonel)
                }
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[TextChat] Paket hatası: " + e.Message);
            }
        }

        static void AddMessage(ChatMessage msg)
        {
            history.Add(msg);
            if (history.Count > MAX_HISTORY)
                history.RemoveAt(0);

            if (!uiVisible || !wasStuck)
                unreadCount++;

            MarkDirty();
            uiVisibleTime = Time.realtimeSinceStartup;
            ShowUI();
        }

        /// <summary>Sistem kanalına yerel mesaj ekler (ağa gitmez).</summary>
        public static void AddSystemMessage(string text)
        {
            var msg = new ChatMessage
            {
                Channel = ChatChannel.System,
                SenderSteamId = 0,
                SenderName = "[Sistem]",
                Message = text,
                Time = ClockStamp(),
            };
            AddMessage(msg);
        }

        static void MarkDirty() { uiDirty = true; }

        /// <summary>
        /// 40+ harflik bölünmez kelimeler (link, yol) panelden taşıyordu:
        /// Unity 5 Text yalnızca boşluktan böler. Görüntüye boşluk eklenir
        /// (kayıtlı mesaj aynen kalır).
        /// </summary>
        static string BreakLongWords(string text) {
            if (string.IsNullOrEmpty(text) || text.Length < 40) return text;
            StringBuilder w = new StringBuilder(text.Length + 16);
            int run = 0;
            for (int i = 0; i < text.Length; i++) {
                char ch = text[i];
                if (ch == ' ' || ch == '\n') { run = 0; w.Append(ch); continue; }
                run++;
                w.Append(ch);
                if (run >= 40) { w.Append(' '); run = 0; }
            }
            return w.ToString();
        }

        static void RefreshUI()
        {
            if (chatText == null) return;

            bool stick = scrollRect == null || scrollRect.verticalNormalizedPosition <= 0.02f;
            StringBuilder sb = new StringBuilder(2048);
            ulong localId = SteamTransport.SteamReady ? SteamUser.GetSteamID().m_SteamID : 0UL;
            int start = history.Count > RENDER_LINES ? history.Count - RENDER_LINES : 0;
            for (int i = start; i < history.Count; i++)
            {
                var m = history[i];
                string prefix = "";
                Color color = Color.white;

                switch (m.Channel)
                {
                    case ChatChannel.Global: prefix = ""; color = Color.white; break;
                    case ChatChannel.Team: prefix = "[Team] "; color = Color.cyan; break;
                    case ChatChannel.Whisper:
                        prefix = m.SenderSteamId == localId ? "[Kisisel] " : "[Kimden " + m.SenderName + "] ";
                        color = Color.magenta; break;
                    case ChatChannel.System: prefix = ""; color = Color.yellow; break;
                }

                // Unity 5.0.0f4'te ColorUtility YOK. Renk adı elle yazılıyor:
                // zaten sadece 5 sabit renk var.
                sb.Append("<color=#888888>").Append("[").Append(m.Time).Append("]</color> ");
                sb.Append("<color=").Append(HtmlColor(color)).Append(">");
                sb.Append(prefix).Append("<b>").Append(m.SenderName).Append("</b>: ").Append(BreakLongWords(m.Message));
                sb.Append("</color>\n");
            }

            chatText.text = sb.ToString();

            // Yukari kaydirilmissa cekme; okunmadi rozeti soyler.
            Canvas.ForceUpdateCanvases();
            if (scrollRect != null && stick) scrollRect.verticalNormalizedPosition = 0f;
            if (unreadText != null)
                unreadText.text = unreadCount > 0 ? unreadCount + " yeni" : "";
        }

        static void ShowUI()
        {
            if (!uiVisible)
            {
                uiVisible = true;
                chatPanel.SetActive(true);
                unreadCount = 0;
                wasStuck = true;
                MarkDirty();
            }
        }

        static void HideUI()
        {
            if (uiVisible)
            {
                uiVisible = false;
                chatPanel.SetActive(false);
            }
        }

        /// <summary>Chat UI'ını toggle eder (T tuşu ile çağrılabilir).</summary>
        public static void ToggleUI()
        {
            if (uiVisible) HideUI(); else ShowUI();
        }

        public static bool IsVisible() { return uiVisible; }

        public static Rect ScreenRectTopLeft()
        {
            // uGUI panelin gerçek ekran dikdörtgeni. IMGUI/olay noktaları
            // sol-üst kökenli, uGUI sol-ALT kökenli; bu yüzden Y çevrilir.
            // Bu alan chat açıkken dünya etkileşimini susturur (MenuMouse).
            //
            // BOYUT DrawBorder/CreateUI'daki panel boyutunun AYNISI olmalı,
            // yoksa susturma alanı ile görünen panel birbirinden kayar.
            try {
                float w = Screen.width > 0 ? Screen.width : 1280f;
                float h = Screen.height > 0 ? Screen.height : 720f;
                float pw = Mathf.Clamp(w * 0.27f, 320f, 540f);
                float ph = Mathf.Clamp(h * 0.22f, 130f, 260f);
                return new Rect(12f, h - 12f - ph, pw, ph);
            }
            catch { return new Rect(0f, 0f, 0f, 0f); }
        }

        /// <summary>
        /// MSCLoader'ın kendi konsolu (F3) açık mı?
        ///
        /// NEDEN VAR: MSC'nin F3 konsolu ve modun kendi LogConsole'u İKİ
        /// AYRI nesnedir. Sadece kendi konsolumuza bakınca, kullanıcı
        /// MSCLoader konsolunda yazarken T harfi sohbeti açıyordu (harf
        /// iki yere birden gidiyordu).
        ///
        /// MSCLoader.ConsoleView bir MonoBehaviour; public "Visible"
        /// özelliği YOK, durum `viewContainer.activeSelf` alanından
        /// okunuyor. Referans bir kez alınıp saklanıyor çünkü
        /// FindObjectOfType her karede tüm sahneyi tarar (ağır).
        ///
        /// TYPING ayrıca kontrol ediliyor: konsol görünür olmasa bile
        /// input alanı odaktaysa T oraya yazılmalı.
        /// </summary>
        static Type mscConsoleType;
        static Component mscConsole;

        static bool MscLoaderConsoleOpen()
        {
            try
            {
                if (mscConsole == null)
                {
                    if (mscConsoleType == null)
                    {
                        Assembly asm = typeof(ConsoleCommand).Assembly;
                        mscConsoleType = asm.GetType("MSCLoader.ConsoleView");
                    }
                    if (mscConsoleType == null) return false;
                    mscConsole = UnityEngine.Object.FindObjectOfType(mscConsoleType) as Component;
                }
                if (mscConsole == null) return false;

                FieldInfo fContainer = mscConsoleType.GetField("viewContainer");
                if (fContainer != null)
                {
                    GameObject go = fContainer.GetValue(mscConsole) as GameObject;
                    if (go != null && go.activeSelf) return true;
                }

                FieldInfo fInput = mscConsoleType.GetField("inputField");
                if (fInput != null)
                {
                    InputField f = fInput.GetValue(mscConsole) as InputField;
                    if (f != null && f.isFocused) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Update: T ile aç/kapat, Enter ile gönder ve kapat.
        ///
        /// DÜZELTİLEN HATALAR (hepsi kullanıcı raporundan):
        ///
        /// 1) T YAZMAYI BAŞLATMIYORDU. ShowUI() yalnızca paneli açar,
        ///    ActivateInputField() çağrılmazdı; alana tıklamak gerekiyordu.
        ///    Artık T hem açar hem odaklar.
        ///
        /// 2) T İKİNCİ KEZ İŞE YARAMIYORDU. `&& !inputField.isFocused`
        ///    koşulu yüzünden odaklıyken T hiçbir şey yapmıyordu, yani
        ///    kapatmak için fareye uzanmak gerekiyordu. Artık T açıkken
        ///    kapatır.
        ///
        /// 3) KENDİ KENDİNE KAPANIYORDU. Panel açıkken 10 sn sonra
        ///    otomatik gizleniyordu (`uiVisibleTime` + UI_FADE_TIME);
        ///    yazmaya başlamadan önce düşününce sohbet kayboluyordu.
        ///    Bu otomatik gizleme KALDIRILDI: menü sadece T/Enter/Escape
        ///    ile kapanır.
        ///
        /// 4) Enter MENÜYÜ KAPATMIYORDU. Boş metinde `return` ediyor,
        ///    dolu metinde ise alanı yeniden odaklıyordu.
        ///    Artık Enter her zaman kapatır.
        ///
        /// 5) ODAG BIRAKILMIYORDU. Kapatınca InputField hâlâ odaktaydı;
        ///    WASD harfleri yazmaya devam ediyordu. CloseAndRelease()
        ///    DeactivateInputField() çağırıyor.
        /// </summary>
        public static void Update()
        {
            if (chatPanel == null || inputField == null) return;
            try { MenuMouse.SetChatRect(ScreenRectTopLeft(), uiVisible); } catch { }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        // ----- KONSOL ACIK MI? (IKI AYRI KONSOL VAR) -----
        //
        // CANLI HATA: yalniz kendi LogConsole'umuza bakiyorduk. Log'da
        // "log console open" HIC BIR ZAMAN gorunmuyor, yani kullanici
        // F3'te MSCLoader'in KENDI konsolunu aciyor. O acikken
        // `logOpen` hep false kaliyor ve T harfi hem konsola yaziliyor
        // HEM DE sohbeti aciyordu: iki yazma hedefi, belirsizlik.
        //
        // MSCLoader.ConsoleView'in public "Visible" ozelligi yok;
        // gorunurluk `viewContainer.activeSelf` alanindan okunuyor.
        // Nesne sahnede bir kez bulunup saklaniyor (her kare arama
        // yapmak pahali; FindObjectOfType tum sahneyi tarar).
        bool logOpen = SteamMPBehaviour.Instance != null
            && SteamMPBehaviour.Instance.Console != null
            && SteamMPBehaviour.Instance.Console.Visible;

        bool mscOpen = MscLoaderConsoleOpen();
        bool anyConsoleOpen = logOpen || mscOpen;

            if (!ctrl && !anyConsoleOpen && Input.GetKeyDown(KeyCode.T))
            {
                if (uiVisible) {
                    CloseAndRelease();
                } else {
                    ShowUI();
                    // 1) T ile yazmaya BAŞLA.
                    inputField.text = "";
                    inputField.ActivateInputField();
                }
                return;
            }

            // 4) Enter: odak alanın dışındaysa yine de gönder+kapat.
            if (uiVisible && !inputField.isFocused
                && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                SubmitAndClose();
                return;
            }

            if (uiVisible && !UiFocus.ConsoleVisible() && Input.GetKeyDown(KeyCode.Escape))
            {
                CloseAndRelease();
                return;
            }

            if (uiVisible && inputField != null && inputField.isFocused
                && inputField.text.Length > 0 && SteamTransport.HasRemote)
            {
                float tr = Time.realtimeSinceStartup;
                if (tr - lastTypingPing > 2.5f)
                {
                    lastTypingPing = tr;
                    try
                    {
                        MemoryStream ms = Protocol.Begin(Protocol.Msg.ChatTyping);
                        BinaryWriter w = new BinaryWriter(ms);
                        w.Write(SteamUser.GetSteamID().m_SteamID);
                        w.Write(SteamFriends.GetPersonaName() ?? "Unknown");
                        w.Flush();
                        SteamTransport.SendUnreliable(Protocol.Finish(ms));
                    }
                    catch { }
                }
            }

            if (typingName.Length > 0 && Time.realtimeSinceStartup >= typingUntil)
                ClearTyping();

            if (uiDirty) { uiDirty = false; RefreshUI(); }
            if (chatPanel != null && uiVisible && scrollRect != null)
                wasStuck = scrollRect.verticalNormalizedPosition <= 0.02f;
        }

        public static void Clear()
        {
            history.Clear();
            unreadCount = 0;
            lastSendTime = -999f;
            whisperTarget = 0;
            if (chatText != null) chatText.text = "";
            // Canvas kalıcı (DontDestroyOnLoad) ama sahne değişiminde yeniden
            // kurulmalı; Clear() yalnız referansları bırakırsa bir sonraki
            // Initialize() ölü panelin üstüne ikinci bir panel eklerdi.
            if (chatPanel != null)
            {
                UnityEngine.Object.Destroy(chatPanel);
                chatPanel = null;
            }
            chatText = null;
            inputField = null;
            scrollRect = null;
            channelButton = null;
            channelLabel = null;
            uiVisible = false;
            uiVisibleTime = -999f;
        }

        public static string Describe()
        {
            return "TextChat: history=" + history.Count + ", unread=" + unreadCount + ", visible=" + uiVisible;
        }
    }
}
