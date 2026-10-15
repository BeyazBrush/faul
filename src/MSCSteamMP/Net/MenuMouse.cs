using System;
using Harmony;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// MENÜNÜN FARE GİRDİSİNİN TEK SAHİBİ.
	///
	/// BELİRTİ (kullanıcı, tekrarlayan): "menü buga giriyor, bazen
	/// tıklanmıyor, oyunun kendi menüsüyle çakışıyor".
	///
	/// ÜÇ AYRI KÖK NEDEN — hepsi doğrulandı
	/// -----------------------------------
	///
	/// 1) OYUN İMLECİ SON ANDA EZİYOR (asıl neden)
	///    dnSpy kanıtı, PlayMakerGUI.OnGUI (token 0x0600042F):
	///
	///        if (this.controlMouseCursor) {
	///            if (Screen.lockCursor != PlayMakerGUI.LockCursor)
	///                Screen.lockCursor = PlayMakerGUI.LockCursor;
	///            if (Cursor.visible == PlayMakerGUI.HideCursor)
	///                Cursor.visible = !PlayMakerGUI.HideCursor;
	///        }
	///
	///    Bu blok EventType.Repaint içinde ve HER karede çalışır. Yani
	///    sıralama şudur:
	///
	///        Update  -> biz ApplyCursorState() ile imleci açıyoruz
	///        OnGUI   -> oyun PlayMakerGUI.OnGUI'de geri kapatıyor
	///
	///    CursorGuard yalnızca SetMouseCursor.OnEnter'ı yamalıyor
	///    (Core/CursorGuard.cs); o action FSM'de bir kez çalıştığı için
	///    "göster" kararını bir kez yazıp bırakıyor. Oyun ise o kararı
	///    KARE BAŞINA yeniden uyguluyor ve KAZANIYOR. Sonuç: menü
	///    görünürken imleç kayboluyor ve fare "görünmez ama serbest"
	///    durumda kalıyor — yani tıklanıyor ama hedefi bulamıyor.
	///    DÜZELTME: PlayMakerGUI.OnGUI'ya prefix konur; menümüz
	///    ekranı tutuyorsa orijinal gövde HİÇ çalıştırılmaz
	///    (Prefix false döner). Böylece oyun ne fare konumunu ne
	///    imleci ne de GUI.matrix'i değiştirebilir.
	///
	/// 2) Event sırası bize karşı
	///    Unity bütün OnGUI'leri belirsiz sırada çağırır. Oyunun
	///    menü düğmeleri PlayMaker FsmStateAction.OnGUI() ile
	///    (PlayMakerGUI.CallOnGUI, token 0x06000430) çizilir ve
	///    GUI.Button kullanır; bunlar Event'i tüketip hotControl'ü
	///    kapabilir. Önce onlar çalışırsa bizim görünmez
	///    GUI.Button'ımız MouseDown'u hiç görmez. Üstelik game
	///    menüsü düğmesi hotControl'ü tutup MouseUp'i görmezse
	///    "ölü" kalır ve SONRAKİ TÜM GUI.Button'lar tıklanamaz
	///    olur — "bazen tıklanmıyor" belirtisinin klasik nedeni.
	///    DÜZELTME: (a) yukarıdaki prefix oyunun GUI'sini susturur,
	///    (b) tıklama artık Event'e değil Input'a bakar (aşağıda),
	///    (c) MenuUi her kare başında ölü hotControl'ü temizler.
	///
	/// 3) GUI.matrix kaçağı
	///    MenuUi mantıksal 1280x720 uzayı kullanır ve V() ile
	///    Screen.width/height'ten hesaplanan dikdörtgenleri ham
	///    ekran uzayına çevirir. Bu İKİSİ de "GUI.matrix == identity"
	///    varsayar. Ama PlayMakerGUI.OnGUI (IL 0x0124 / 0x01B5),
	///    PlayMakerGUI.ResetGUIMatrix.OnGUI (IL 0x0007) ve
	///    DashBoard.OnGUI (IL 0x031A / 0x050C) GUI.matrix'i yazıyor.
	///    Oyun OnGUI'si bizim OnGUI'mizden ÖNCE çalışıp ölçekli bir
	///    matris bırakırsa: fare konumu GUI uzayında gelir, biz onu
	///    kendi V() ölçeğimizle böleriz → imlecin göründüğü yer ile
	///    tıklamanın düştüğü yer AYRIŞIR. ConnectionPanel.cs:72 ve
	///    PerfHud.cs:188 bu yüzden already "GUI.matrix'i kimlik yap,
	///    sonra geri koy" kalıbını kullanıyor; MenuUi'de bu kalıp
	///    EKSİKTİ. DÜZELTME: MenuUi.Draw artık aynı kalıbı kullanır.
	///
	/// GİRDİ NEDEN Input ÜZERİNDEN?
	/// ----------------------
	/// GUI.Button yerine fare durumu Update'te (Tick) okunur:
	///
	///   * Input.mousePosition ve Input.GetMouseButton IMGUI olay
	///     akışından BAĞIMSIZDIR; ondan önce kim çalışırsa çalışsın
	///     aynı değeri verir.
	///   * GUI.matrix'ten etkilenmez (ekran pikseli verir).
	///   * Oyunun GUI'si olayı tüketse de tıklama bize gelir.
	///
	/// Tek seferliklik: Clicked() ilk eşleşen dikdörtgende
	/// tüketilir, böylece bir tıklama iki satıra birden gitmez.
	///
	/// Unity 5.0 / mscorlib 2.0 uyumlu. Hiçbir istisna dışarı sızmaz.
	/// </summary>
	public static class MenuMouse {

		// --- mantıksal <-> ekran dönüşümü --------------------------
		// MenuUi her çizimde ComputeView() sonrası SetView() çağırır.
		// (Menü görünmüyorken de yazılır; 0 ölçek bölme yapmaz.)
		static float viewScale = 1f;
		static Vector2 viewOffset = Vector2.zero;

		// --- ham durum (Tick ile dolar) ----------------------------
		static Vector2 screenPos;      // sol-üst köşe, ekran pikseli
		static bool clickPending;      // basıldı, henüz bir kutuya verilmedi
		static Vector2 clickPos;       // basıldığı andaki mantıksal konum
		static float scrollDelta;
		static bool enabled;

		// ------------------------------------------------------------------

		/// <summary>Menü fareyi sahipleniyor mu?</summary>
		public static bool Enabled { get { return enabled; } }

		/// <summary>
		/// DUNYA ETKILESIMI uretimi susturulsun mu? (UI fareyi kucakliyorsa)
		///
		/// CANLI HATA: "sokulebilir parcalara aim alirsam parcalar kendiliginden
		/// sokuluyor." Mekanizma: chat/konsol acilinca kamera donuyor
		/// (`CameraLookGuard`), nişangah sabit kaldigi yerde kaliyor ve UI'ye
		/// yapilan tiklama `Input.GetMouseButtonDown` sayesinde PlayMaker'a da
		/// ulasiyor. Dumper kaniti (`rearlight(leftx)` fsm='Removal'):
		///   Mouse over -> GetMouseButtonDown -> sendEvent "Remove part"
		/// Yani UI tiklamasi dunya tiklamasina donusuyor.
		///
		/// Bu bayrak yalniz GONDERIMI susturur; oyunun kendi FSM'i normal
		/// calisir, yani tek oyunculu davranis bozulmaz.
		/// </summary>
		public static bool BlockWorldInteraction {
			get {
				if (hasChatRect || hasConsoleRect) return true;
				float now;
				try { now = Time.unscaledTime; } catch { return false; }
				return now < uiBlockUntil;
			}
		}

		/// <summary>
		/// UI kapandiktan sonra etkilesiminin susmaya devam edecegi sure (s).
		/// Kapanma tiklamasi da bir sonraki karede PlayMaker'a ulastigi icin
		/// kisa bir tolerans birakiyoruz.
		/// </summary>
		const float UI_BLOCK_GRACE = 0.30f;
		static float uiBlockUntil = -1f;

		/// <summary>Fare basılı mı? (basma geri bildirimi için)</summary>
		public static bool Held {
			get {
				try { return enabled && Input.GetMouseButton(0); }
				catch { return false; }
			}
		}

		/// <summary>Fare mantıksal (1280x720) uzayda.</summary>
		public static Vector2 Logical {
			get {
				if (viewScale <= 0.0001f) return screenPos;
				return new Vector2((screenPos.x - viewOffset.x) / viewScale,
								   (screenPos.y - viewOffset.y) / viewScale);
			}
		}

		/// <summary>MenuUi çizim başında çağırır.</summary>
		public static void SetView(float scale, Vector2 offset) {
			if (scale <= 0.0001f) scale = 0.0001f;
			viewScale = scale;
			viewOffset = offset;
		}

		/// <summary>
		/// Kare başına (Update'ten, yani OnGUI'den ÖNCE) okuma.
		/// </summary>
		/// <param name="active">Menü/panel fareyi sahipleniyor mu.</param>
		public static void Tick(bool active) {
			enabled = active;
			// UI acikken ve kapandiktan kisa sure daha dunya etkilesimi
			// susturulur. `active` false olsa da buraya giriyoruz: kapali
			// olan chat'in `hasChatRect` bayragi buTick'te guncellenmis olur.
			if (hasChatRect || hasConsoleRect) {
				float now;
				try { now = Time.unscaledTime; } catch { now = 0f; }
				uiBlockUntil = now + UI_BLOCK_GRACE;
			}
			try {
				// Input.mousePosition SOL-ALT köşedir; GUI.sol-üst.
				Vector3 m = Input.mousePosition;
				screenPos = new Vector2(m.x, Screen.height - m.y);

				float s = 0f;
				try { s = Input.mouseScrollDelta.y; } catch { }
				scrollDelta = s;

				if (!active) { clickPending = false; return; }
				if (Input.GetMouseButtonDown(0)) {
					clickPending = true;
					clickPos = Logical;
					clickScreen = screenPos;
					try { lastClickTime = Time.realtimeSinceStartup; } catch { }
				}
			}
			catch {
				// Input erişilemezse (odak pencerede değil) eski durumu koru.
			}
		}

		/// <summary>Tekerlek kaydırma miktarı (okunduktan sonra sıfırlanır).</summary>
		public static float TakeScroll() {
			float s = scrollDelta;
			scrollDelta = 0f;
			return s;
		}

		// ---- Z-SIRASI: ustteki modal pencerelerin ekran alanlari ----
		// Alttaki pencere usttekinden ONCE cizilir ve olayi once gorur.
		// Ustteki alanini her kare yayinlar, alttaki tuketmeden once sorar.
		// Ham ekran pikseli, sol-ust koken (Event.mousePosition uzayi).
		static Vector2 clickScreen;
		static float lastClickTime = -999f;
		const float CLICK_RECENT_WINDOW = 0.5f;
		static Rect topConsoleRect;
		static bool hasConsoleRect;
		static Rect topChatRect;
		static bool hasChatRect;

		public static void SetConsoleRect(Rect r, bool on) {
			hasConsoleRect = on;
			topConsoleRect = r;
		}

		public static void SetChatRect(Rect r, bool on) {
			hasChatRect = on;
			topChatRect = r;
		}

		public static bool PressInTopModal() {
			if (!clickPending) return false;
			if (hasConsoleRect && topConsoleRect.Contains(clickScreen)) return true;
			if (hasChatRect && topChatRect.Contains(clickScreen)) return true;
			return false;
		}

		public static bool EventInChatRect(Vector2 p) {
			if (!hasChatRect) return false;
			return topChatRect.Contains(p);
		}

		public static bool EventInTopModal(Vector2 p) {
			if (hasConsoleRect && topConsoleRect.Contains(p)) return true;
			if (hasChatRect && topChatRect.Contains(p)) return true;
			return false;
		}

		public static bool GuiBlockedAtCurrentEvent() {
			try {
				Event e = Event.current;
				if (e == null) return false;
				return EventInTopModal(e.mousePosition);
			}
			catch { return false; }
		}

		/// <summary>Yerel uretim teshisi: bu karede fiziksel tiklama var mi,
		/// hangi arayuz acik? Kapak/parca "kendi kendine" sikayetinde tiklama
			/// mi (yanlis-yonlendirme) yoksa paketsiz uretim mi ayirt eder.</summary>
		/// <summary>
		/// Yakinda fareye basildi mi? (`ClickCtx` ile ayni 0.5 sn pencere)
		///
		/// Parca sokme kapisi bunu kullanir: `Removal`/`Assembly` FSM'i bool
		/// testlerinden TIKLAMASIZ da `REMOVE` uretebiliyor (dumper kaniti:
		/// `Mouse off` -> `BoolAnyTrue(Installed, Bolted)` -> `REMOVE`).
		/// Tiklamali yol ayri: `Mouse over` -> `GetMouseButtonDown` ->
		/// `"Remove part"`. Ayrim buradan yapilir.
		/// </summary>
		public static bool ClickRecent() {
			try { return Time.realtimeSinceStartup - lastClickTime < CLICK_RECENT_WINDOW; }
			catch { return false; }
		}

		public static string ClickCtx() {
			bool held = false, down = false;
			try { held = Input.GetMouseButton(0); } catch { }
			try { down = Input.GetMouseButtonDown(0); } catch { }
			string ui = "";
			try {
				if (UiFocus.ConsoleVisible()) ui += "console";
				if (UiFocus.ChatVisible()) ui += (ui.Length > 0 ? "+" : "") + "chat";
				if (UiFocus.MenuVisible()) ui += (ui.Length > 0 ? "+" : "") + "menu";
				if (GameMenuGuard.GameMenuOpen) ui += (ui.Length > 0 ? "+" : "") + "game";
			} catch { }
			if (ui.Length == 0) ui = "-";
			bool recent = false;
			try { recent = !down && Time.realtimeSinceStartup - lastClickTime < CLICK_RECENT_WINDOW; } catch { }
			return "click=" + (down ? "DOWN" : (recent ? "recent" : (held ? "held" : "-"))) + " ui=" + ui;
		}

		/// <summary>Fare bu mantıksal dikdörtgenin üstünde mi?</summary>
		public static bool Hover(Rect logical) {
			if (!enabled) return false;
			return logical.Contains(Logical);
		}

		/// <summary>
		/// Tıklama bu dikdörtgenin içinde mi? EVET ise tıklamayı
		/// TÜKETİR (bir tıklama yalnızca bir kutuya gider).
		/// </summary>
		public static bool Clicked(Rect logical) {
			if (!enabled || !clickPending) return false;
			if (!logical.Contains(clickPos)) return false;
			// Z-SIRASI: alttaki pencere ustteki modalin alanina dusen basmayi calmaz.
			// Basma beklemede kalir, sonra cizilen ustteki pencere onu alir.
			if (PressInTopModal()) return false;
			clickPending = false;
			return true;
		}

		/// <summary>
		/// Çizim karesi bitti: hiçbir öge tüketmemişse bekleyen
		/// tıklamayı düşür.
		///
		/// NEDEN: Clicked() yalnızca İÇİNDE kalan kutularda
		/// tüketir. Boşluğa tıklanırsa tıklama birikiyor ve
		/// sonraki karede, imleç başka yere gitmiş olsa bile,
		/// o eski noktaya denk gelen bir kutu onu tetikliyordu.
		/// </summary>
		public static void EndFrame() {
			clickPending = false;
		}

		/// <summary>Bekleyen tıklamayı düşür (ekran değişince).</summary>
		public static void Clear() {
			clickPending = false;
			scrollDelta = 0f;
		}

		/// <summary>Menü görünürken oyunun imleç kararını geçersiz kıl.</summary>
		/// <remarks>
		/// PlayMakerGUI.HideCursor/LockCursor statik alanlar; OnGUI
		/// bunları doğrudan Cursor'a yazıyor. CursorGuard bu alanları
		/// doğru değerde bırakmış olsa bile oyun "gizli" isteği
		/// yapmışsa yeniden kilitler. Menümüz açıkken ikisini de
		/// "göster / serbest" yapıyoruz.
		/// </remarks>
		public static void ReleaseGameCursor() {
			try {
				PlayMakerGUI.HideCursor = false;
				PlayMakerGUI.LockCursor = false;
				try { CursorGuard.KillSoftwareCursor(); } catch { }
			}
			catch { }
		}
	}

	/// <summary>
	/// PlayMakerGUI.OnGUI prefix'i: menümüz ekranı tutuyorken oyunun
	/// IMGUI'sini tamamen susturur.
	///
	/// NEDEN SADECE "menü açıkken"
	/// --------------------------
	/// PlayMakerGUI.OnGUI iki işi birden yapıyor:
	///   1) FSM GUI eylemlerini çizdiriyor (CallOnGUI),
	///   2) her Repaint'te Screen.lockCursor / Cursor.visible yazıyor.
	///
	/// (2) bizim için tehlike. (1) oyunun HUD/etiketleri için gerekli.
	/// Bu yüzden susturma yalnızca MenuUi görünürken ve onay
	/// panosu açıkken DEĞİLken yapılır — onay panosunda oyunun kendi
	/// BEGIN/CANCEL düğmeleri tıklanabilir olmalıdır (View.GameDialog).
	/// </summary>
	public enum WinLayer { None, Game, Connection, Session, Invite, Menu, Chat, Console }

	/// <summary>Pencere odak hakemi: konsol en usttedir; ESC yalnizca en ustu kapatir.</summary>
	public static class UiFocus {
		public static bool ConsoleVisible() {
			try {
				return SteamMPBehaviour.Instance != null
					&& SteamMPBehaviour.Instance.Console != null
					&& SteamMPBehaviour.Instance.Console.Visible;
			}
			catch { return false; }
		}
		public static bool ChatVisible() {
			try { return TextChat.IsVisible(); }
			catch { return false; }
		}
		public static bool MenuVisible() {
			try {
				MenuUi ui = MenuController.Ui;
				return ui != null && ui.IsVisible;
			}
			catch { return false; }
		}
	}

	[HarmonyPatch(typeof(PlayMakerGUI), "OnGUI")]
	public static class PlayMakerGuiPatch {

		static bool Prefix() {
			try {
				if (MenuController.Ui == null) return true;
				MenuUi ui = MenuController.Ui;
				if (ui.Target == MenuUi.View.GameDialog) return true;
				// Log konsolü F3 ile açılıyor ve oyunun menüsüyle
				// değil, bizim her şeyimizle çakışıyor; o da bizim.
				if (ui.IsVisible) {
					MenuMouse.ReleaseGameCursor();
					return false;
				}
				if (SteamMPBehaviour.Instance != null
					&& SteamMPBehaviour.Instance.Console != null
					&& SteamMPBehaviour.Instance.Console.Visible) {
					MenuMouse.ReleaseGameCursor();
					return false;
				}
				// Sohbet uGUI canvasidir; menu/konsol kosuluna girmez. Acikken oyunun
				// IMGUI susar, yoksa alttaki oyun penceresi ustteki sohbeti engeller.
				try {
					if (TextChat.IsVisible()) {
						MenuMouse.ReleaseGameCursor();
						return false;
					}
				}
				catch { }
			}
			catch { }
			return true;
		}

		/// <summary>
		/// Oyunun OnGUI'si bittikten SONRA imleci bizim kararimiza geri alir.
		///
		/// ZORUNLULUK: PlayMakerGUI.OnGUI'nin Repaint blogu her karede
		/// <c>Screen.lockCursor = PlayMakerGUI.LockCursor</c> ve
		/// <c>Cursor.visible = !PlayMakerGUI.HideCursor</c> yaziyor.
		/// CursorGuard yalnizca <c>SetMouseCursor.OnEnter</c>'i yamaladigi
		/// icin kararini FSM'de bir kez yaziyor ve oyun SONRA geri
		/// aliyor. Yani ESC menusu acildiginda kamera/imlec durumu
		/// "gorsunmez ama serbest"de kaliyor ve kullanici tiklayamiyor.
		///
		/// Bu postfix oyunun yazimindan sonra calisir. Kosullar: bizim
		/// menusumuz veya konsolumuz acik ya da oyunun kendi ESC
		/// menusu (GameMenuGuard) acik.
		/// </summary>
		static void Postfix() {
			try {
				bool ours = false;
				MenuUi ui = MenuController.Ui;
				if (ui != null && ui.IsVisible) ours = true;
				if (SteamMPBehaviour.Instance != null
					&& SteamMPBehaviour.Instance.Console != null
					&& SteamMPBehaviour.Instance.Console.Visible) ours = true;
				if (!ours && !GameMenuGuard.GameMenuOpen) return;

				// MenuMouse statigi: PlayMakerGUI.HideCursor/LockCursor
				// alanlarini "goster / serbest" yapar, sonra gercek
				// Cursor'a da yazar (oyun bir sonraki Repaint'te
				// yeniden yazacaksa en azindan dogru degerle baslar).
				if (!SteamMPBehaviour.AppFocused) return;
				MenuMouse.ReleaseGameCursor();
				if (!Cursor.visible) Cursor.visible = true;
				if (Cursor.lockState != CursorLockMode.None)
					Cursor.lockState = CursorLockMode.None;
				CursorGuard.ApplyCustomCursor();
			}
			catch { }
		}
	}
}