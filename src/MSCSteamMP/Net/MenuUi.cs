//
// DURUM NOTU (kurtarma, 2026-10-14)
// ---------------------------------
// Bu dosya 2026-10-14 00:16'da tamamen bosaltilmisti (5 bayt: BOM + satir
// sonu); hicbir yedegi yoktu ve proje surum kontrolunde DEGIL. Yorumlariyla
// birlikte tek saglam kopya, 2026-10-09 tarihli MSCSteamMP.7z arsivindeydi
// ve icerik oradan geri getirildi.
//
// Arşiv sürümü 09 Ekim'deki hâldir; 14 Ekim 00:05'teki SON BASARILI
// derlemede ana menüde ucuncu satir (NEW GAME) YOKTU ve OnNewGame olayi
// kaldirilmisti. Kaybolan dosya o sürümdü (MenuTheme.ROW_COUNT = 2 bunu
// dogrular), bu yuzden delta yeniden uygulandi:
//
//   * `RowNewGame` GUIContent ve `OnNewGame` olayi kaldirildi
//   * satirlar yeniden numaralandi (0 = PLAY ONLINE, 1 = GORUNUM)
//   * OnRow() ayni numaralara gore dallaniyor
//
// DOGRULAMA: bu kaynakla derlenen DLL decompile edilip 14 Ekim DLL'inin
// decompile ciktisiyla karsilastirildi; iki tip BIREBIR ayni (248/248 uye).
//
// NOT: NEW GAME satirini geri isteyen, bu notu okuyup satirlari geri
// eklemeli -- MenuTheme.ROW_COUNT da 2'den 3'e cikmalidir, yoksa yigin
// dikeyde ortalanmaz.
//
using System;
using System.Collections.Generic;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Ana menu ve online giris paneli. Butonlar fare ile secilir.
	///
	/// DUZEN: bu dosya bastan yazildi. Eski surumde uc gercek hata vardi:
	///
	///   1. Menu cizimi sanal 1280x720 uzayinda yapiliyordu ama arka plan
	///      HAM ekran uzayinda ciziliyordu; iki uzayin merkezi ayni degildi
	///      (esken 16:9 disi ekranlarda panel kayiyordu). Artik TEK bir
	///      olcek/ornek sistemi var: <see cref="VirtualScale"/> her cizimde
	///      Screen.width/height'ten hesaplanir, icerik ve cerceve ayni Rect
	///      uzerinden turetilir, sonunda ekrana kirpilir. Hicbir ogge ekran
	///      disinda kalamaz.
	///   2. Kutu icerigi iki ayri yerde hesaplaniyordu (PanelHeight ve
	///      DrawOnline'in satir toplamlari). Uyuşmadiginda son satir BACK
	///      butonu panelin disina tasiyordu. Artik icerik ONCE olculuyor
	///      (<see cref="Measure"/>), panel boyutu o olcümden turetiliyor ve
	///      cizim ayni olcumden yuruyor. Tasma imkansiz.
	///   3. Kod kutusu hostta "goruntulenir kutu", istemcide "yazilabilir
	///      kutu" idi; iki dal farkli yukseklikteydi ve ikisinde de tıklama
	///      alani ile gorunur alan ust uste biniyordu. Artik her iki dal da
	///      ayni <see cref="CodeRow"/> yuksekligini ve ayni cizim yolunu
	///      kullanir; yazma odagi tek alanda.
	///
	/// GORSEL
	/// ------
	/// MSC menusu duz kutular kullanir; oyun dilinden kopmamak icin iskelet
	/// duz kutu + sarı vurgu olarak kaldi. "Gorsel solenli" kisim efektlerden
	/// geliyor:
	///
	///   * Arka plan: dikey degrade (tek dokuda, 1 cizim cagrisi) + vignette
	///     + yavasca kayan isik huzmesi. Degre uretilir, her kare hesaplanmaz.
	///   * Buton hover: 2px yukselme, buyuyen sol vurgu cubugu, dis parlam
	///     (3 katmanli alfa halka) ve kayan parlaklik seridi.
	///   * Panel girisi: asagi kayma + olcek + soluk (300ms).
	///   * Baslik altininin genisligi animasyonla dolar.
	///   * Durum satiri nabiz gibi soluklanir.
	///
	/// PERFORMANS
	/// ----------
	/// OnGUI kare basina iki kez (Layout + Repaint) cagrilir. Bu yuzden:
	///   * dokular bir kez uretilir (gradient, vignette, pixel, vignetteMask),
	///   * her cizimde yalnizca DrawTexture/Box cagrisi vardir,
	///   * string birlestirme yerine GUIContent.text sadece degisince yazilir,
	///   * kirpma hesabi sabit sayi (8 kirpma).
	/// Hicbir noktada LINQ, LINQ-like extension veya yeni dil ozelligi yok.
	///
	/// UYUMLULUK: Unity 5.0 / mscorlib 2.0. Kullanilan API'ler yalnizca
	/// UnityEngine.IMGUI, Texture2D ve System.Drawing yerine elle cizim.
	/// </summary>
	/// <summary>Skin gorunum menusu MenuUiSkins.cs icindeki partial bolumde.</summary>
	public partial class MenuUi {

		public enum View { Hidden, Main, Online, Loading, GameDialog, Skins }

		// ------------------------------------------------------------------
		// Olcek: tek kaynak
		// ------------------------------------------------------------------

		/// <summary>
		/// Mantiksal cizim alani. Icerik bu alanda taslanir, sonra ekrana
		/// kirpilir. Oyunun menusu de sabit mantiksal boyut kullanir; ayni
		/// yontem 800x600 ile 2560x1440 arasinda ayni gorunum verir.
		/// </summary>
		public const float REF_W = MenuTheme.REF_W;
		public const float REF_H = MenuTheme.REF_H;

		/// <summary>Ekran 16:9 degilse siyah bant olusur; icerik yine ortali.</summary>
		static float viewScale;
		static Vector2 viewOffset;

		/// <summary>
		/// Sanal uzaydan ekran uzayina cevir. Rect'i dondurur: cizim
		/// boylece icerik ve cerceve ayni koordinatta kalir.
		/// </summary>
		static Rect V(Rect r) {
			return new Rect(r.x * viewScale + viewOffset.x,
							r.y * viewScale + viewOffset.y,
							r.width * viewScale, r.height * viewScale);
		}

		/// <summary>Tam ekran dikdortgeni (ham ekran uzayi).</summary>
		static Rect ScreenRect() {
			return new Rect(0f, 0f, Screen.width, Screen.height);
		}

		// ------------------------------------------------------------------
		// Olcum sabitleri (mantiksal piksel)
		// ------------------------------------------------------------------

		const float PAD = MenuTheme.PAD;
		const float TITLE_H = MenuTheme.TITLE_H;
		const float SUB_H = MenuTheme.SUB_H;
		const float RULE_H = MenuTheme.RULE_H;
		const float GAP = MenuTheme.GAP;

		const float PANEL_W = MenuTheme.PANEL_W;
		const float BTN_H = MenuTheme.BTN_H;
		const float BTN_GAP = MenuTheme.BTN_GAP;
		const float CODE_LABEL_H = MenuTheme.CODE_LABEL_H;
		const float CODE_H = MenuTheme.CODE_H;
		const float STATUS_H = MenuTheme.STATUS_H;
		const float BACK_H = MenuTheme.BACK_H;
		const float PANEL_PAD = MenuTheme.PANEL_PAD;

		// Ana menu
		const float BRAND_H = MenuTheme.BRAND_H;
		const float BRAND_SUB_H = MenuTheme.BRAND_SUB_H;
		// Loading ekrani
		const float LOAD_TITLE_H = MenuTheme.LOAD_TITLE_H;
		const float LOAD_SUB_H = MenuTheme.LOAD_SUB_H;
		const float LOAD_BAR_H = MenuTheme.LOAD_BAR_H;
		const float LOAD_BAR_W = MenuTheme.LOAD_BAR_W;

		const float ROW_H = MenuTheme.ROW_H;
		const float ROW_W = MenuTheme.ROW_W;
		const float ROW_GAP = MenuTheme.ROW_GAP;
		const float FOOT_H = MenuTheme.FOOT_H;

		// Menu komut satiri (alt bilgi altinda, tam genislikte)
		const float CMD_H = MenuTheme.CMD_H;
		const float CMD_GAP = MenuTheme.CMD_GAP;
		const string CmdControlName = "SmpMenuCmd";

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		public View Target { get { return target; } }
		View target = View.Hidden;

		/// <summary>0 kapali, 1 acik. Panel acilisi bu degerle surer.</summary>
		public float Fade { get { return fade; } }
		float fade;

		/// <summary>Uzerinde durulan satir: -1 hicbiri. Yalnizca gorsel.</summary>
		public int Hover { get { return hover; } }
		int hover = -1;

		/// <summary>Son tiklanan girdi: 0 NEW GAME, 1 PLAY ONLINE.</summary>
		public int Active { get { return active; } }
		int active = -1;

		/// <summary>Panel acilis ilerlemesi 0..1 (kayma + olcek icin).</summary>
		float slide;

		/// <summary>Buton basma geri bildirimi (0..1, kisa sure).</summary>
		float press;

		/// <summary>Surekli zaman; huzme ve nabiz icin.</summary>
		float clock;

		// ------------------------------------------------------------------
		// Menu komut satiri
		// ------------------------------------------------------------------
		//
		// Ana menude de komut calistirilabilir; yurutucu MSCLoader'in kendi
		// olani (ModCommands), yani "smp", "npc", "help" ve MSCLoader'in
		// kayitli tum komutlari tek yerden, tutarli calisir.
		//
		// OPTIMIZASYON (her kare maliyeti sifir):
		//   * metin yalnizca TextField'dan gelir; ikinci bir yazici yok
		//     (cift yazi / "nnppcc" bu yuzden mumkun degil)
		//   * komut listesi (TAB tamamlama) ONCE bir kez alinip onbellege
		//     girilir; her tus basmasinda sozluk taranmaz
		//   * gecmis 40 girdiyle sinirli, halka tampon
		//   * calistirma yalnizca ENTER'de bir kez olur

		string cmdInput = string.Empty;
		string cmdInputKey = "\0";
		/// <summary>Komut kutusu odakta mi? Yalnizca odakliyken ok tuslari gecmisi gezdirir.</summary>
		bool cmdFocused;

		/// <summary>
		/// Komut kutusu odakta mi? MenuController bunu okur: odaktayken
		/// yazi kutuya gider, menü gezinme tuslari devre disi kalir.
		/// </summary>
		public bool CommandFocused { get { return cmdFocused; } }

		/// <summary>Komut kutusunu odaktan cikarir (ESC).</summary>
		public void BlurCommand() {
			cmdFocused = false;
			cmdInput = string.Empty;
			try { GUI.FocusControl(string.Empty); } catch { }
		}
		readonly List<string> cmdHistory = new List<string>();
		int cmdHistoryIndex = -1;
		/// <summary>Onbelleklenmis komut adlari (TAB). Bos ise ilk kullanimda doldurulur.</summary>
		string[] cmdNames;
		float cmdCaret;

		// ------------------------------------------------------------------
		// Host kodu
		// ------------------------------------------------------------------

		public string HostCode { get { return CodeContent.text; } }
		static readonly GUIContent CodeContent = new GUIContent(string.Empty);
		string codeKey = "\0";

		/// <summary>Baslik altinin dolanim ilerlemesi 0..1.</summary>
		float ruleT;

		/// <summary>
		/// Host'un LOBBY ID'si (17 hane, Steam'in veridigi gercek adres).
		///
		/// Neden ayri bir alan: 6 haneli oturum kodu oturumu KAPATIR ama bir
		/// lobiyi ADRESLEMEZ. Tek bir lobiye ulaşabilen şey Steam'in lobby
		/// kimliğidir. Kullanıcı "lobi kodu ile giris" dediğinde kastettiği şey
		/// tam olarak budur, önceki sürümde bu yazılamıyordu (kutu 6 karakterle
		/// sınırlıydı) ve keşfe düşülüyordu.
		/// </summary>
		public string HostLobbyId { get { return hostLobbyId; } }
		string hostLobbyId = string.Empty;

		public void SetHostCode(string code) {
			string next = code ?? string.Empty;
			if (next == codeKey) return;
			codeKey = next;
			CodeContent.text = next;
		}

		/// <summary>
		/// Lobinin adresini gösterir (lobi oluşturulduğunda çağrılır).
		/// </summary>
		public void SetHostLobbyId(ulong id) {
			hostLobbyId = id.ToString();
		}

		public bool HasHostCode { get { return CodeContent.text.Length > 0; } }

		public bool HasBackgroundImage { get { return background != null; } }

		// ------------------------------------------------------------------
		// Lobi kodu yazma
		// ------------------------------------------------------------------

		string typedCode = string.Empty;
		string typedKey = "\0";
		static readonly GUIContent TypedContent = new GUIContent(string.Empty);

		/// <summary>Kod kutusu odakta mi? Odaktayken klavye oraya gider.</summary>
		public bool FieldFocused { get { return fieldFocused; } }
		bool fieldFocused;

		/// <summary>
		/// Yazilabilecek en fazla karakter.
		///
		/// ESKISI Ise LobbyCode.LENGTH (6) idi. Bu, girilen sey her zaman bir
		/// "lobi kodu" varsayiyordu; ama oyuncuya gosterilen 6 haneli kod bir
		/// lobiyi ADRESLEMEZ, sadece oturumun kapisidir. Tek adresleyebilir
		/// sey Steam'in 17 haneli lobi kimligidir ve o yazilamiyordu, yani
		/// kullanici "lobi kodu ile giris" yapamiyordu.
		///
		/// Simdi iki tur sekli de kabul edilir: 6 haneli oturum kodu ya da 17
		/// haneli lobi kimligi. MenuController hangi tur oldugunu ayirt edip
		/// dogrudan Join(id) cagirir (kesif gerektirmez) ya da keşif + el
		/// sikisma dogrulamasi yolunu kullanir.
		/// </summary>
		public const int MaxCodeChars = MenuTheme.MaxCodeChars;

		/// <summary>Girilen sey tam bir lobi kimligi mi (en az 10 hane)?</summary>
		public bool LooksLikeLobbyId {
			get { return typedCode != null && typedCode.Length >= 10 && AllDigits(typedCode); }
		}

		static bool AllDigits(string s) {
			for (int i = 0; i < s.Length; i++) {
				if (s[i] < '0' || s[i] > '9') return false;
			}
			return true;
		}

		/// <summary>Kod tam uzunlukta mi? (JOIN on kontrolu)</summary>
		public bool CodeComplete {
			get { return LooksLikeLobbyId || typedCode.Length == LobbyCode.LENGTH; }
		}

		/// <summary>Yazilan kod, normalize edilmeden.</summary>
		public string TypedCode { get { return typedCode; } }

		/// <summary>
		/// Test ve teşhis için: kutu odaktaymış gibi davranır ve metni yazar.
		/// Oyun içinde yalnızca <see cref="Type"/> ve <see cref="Paste"/> kullanılır.
		/// </summary>
		public void DebugSetCode(string text, bool focused) {
			typedCode = text ?? string.Empty;
			if (typedCode.Length > MaxCodeChars) typedCode = typedCode.Substring(0, MaxCodeChars);
			fieldFocused = focused;
			RefreshTyped();
		}

		/// <summary>Yazilan karakter sayisi.</summary>
		public int TypedLength { get { return typedCode.Length; } }

		/// <summary>Tek karakter ekle. Odak yoksa yok sayilir.</summary>
		public void Type(char c) {
			if (!fieldFocused) return;
			// Bosluk reddediliyor: Normalize bosluklari siliyor, yazilabilseydi
			// kullanici karakter harcamadan yer ilerletirdi.
			if (c == ' ') return;
			if (c < '!' || c > '~') return;
			if (typedCode.Length >= MaxCodeChars) return;
			typedCode = typedCode + c;
			RefreshTyped();
		}

		/// <summary>
		/// Panodan yapistirma (Ctrl+V).
		///
		/// 17 haneli lobi kimligini elle yazmak hataya açık ve yavaş. Yapıştırma
		/// hem hizli hem hatasız; alinan metindeki bosluklar ve satir sonlari
		/// temizlenir, uzunluk sinirina kirpilir.
		/// </summary>
		public void Paste(string text) {
			if (!fieldFocused || string.IsNullOrEmpty(text)) return;
			stringBuilder.Length = 0;
			for (int i = 0; i < text.Length && stringBuilder.Length < MaxCodeChars; i++) {
				char c = text[i];
				if (c == ' ' || c == '\r' || c == '\n' || c == '\t') continue;
				if (c < '!' || c > '~') continue;
				stringBuilder.Append(c);
			}
			if (stringBuilder.Length == 0) return;
			typedCode = stringBuilder.ToString();
			RefreshTyped();
		}

		readonly System.Text.StringBuilder stringBuilder = new System.Text.StringBuilder(MaxCodeChars);

		public void Backspace() {
			if (!fieldFocused || typedCode.Length == 0) return;
			typedCode = typedCode.Substring(0, typedCode.Length - 1);
			RefreshTyped();
		}

		public void ClearCode() {
			typedCode = string.Empty;
			fieldFocused = false;
			RefreshTyped();
		}

		public void UnfocusField() { fieldFocused = false; }

		void RefreshTyped() {
			if (typedCode == typedKey) return;
			typedKey = typedCode;
			TypedContent.text = typedCode;
		}

		/// <summary>Durum satiri.</summary>
		static readonly GUIContent StatusContent = new GUIContent(string.Empty);
		string statusKey = "\0";
		/// <summary>Durum rengi: basari yesil, uyari turuncu, hata kirmizi.</summary>
		Color statusColor = new Color(1f, 0.78f, 0.35f);
		Color statusColorNext = new Color(1f, 0.78f, 0.35f);

		/// <summary>Gorunur mu (solma suresi icin de).</summary>
		public bool IsVisible { get { return target != View.Hidden || fade > 0.002f; } }

		// ------------------------------------------------------------------
		// Olaylar
		// ------------------------------------------------------------------

		public event Action OnPlayOnline;
		public event Action OnHost;
		public event Action OnJoin;
		public event Action OnBack;

		// ------------------------------------------------------------------
		// Gezinme
		// ------------------------------------------------------------------

		public void ShowMain() {
			if (target == View.Main && fade > 0.99f) return;
			target = View.Main;
			hover = -1;
			fieldFocused = false;
			slide = 0f;
			ruleT = 0f;
		}

		public void ShowOnline() {
			if (target == View.Online && fade > 0.99f) return;
			target = View.Online;
			hover = -1;
			fieldFocused = false;
			slide = 0f;
			ruleT = 0f;
		}

		/// <summary>Skin (gorunum) menusunu acar. MenuUiSkins.cs.</summary>
		public void ShowSkins() {
			if (target == View.Skins && fade > 0.99f) return;
			target = View.Skins;
			hover = -1;
			fieldFocused = false;
			slide = 0f;
			ruleT = 0f;
			SkinsEnter();
		}

		// ------------------------------------------------------------------
		// Loading ekranı
		// ------------------------------------------------------------------

		/// <summary>
		/// Siyah "LOADING MULTIPLAYER" ekranı.
		///
		/// OPEN LOBBY / JOIN LOBBY'ye basıldığında çağrılır: ekran
		/// kararır, ortada MSC fontuyla başlık belirir, dünya yüklenince
		/// animasyonla kapanır.
		/// </summary>
		public void ShowLoading(string reason) {
			target = View.Loading;
			hover = -1;
			fieldFocused = false;
			loadReason = reason ?? string.Empty;
			loadFade = 0f;
			loadProgress = 0f;
			slide = 0f;
		}

		/// <summary>Loading ekranını kapatır (dünya hazır olduğunda).</summary>
		public void HideLoading() {
			if (target != View.Loading) return;
			loadingOut = true;
		}

		/// <summary>Bekleme nedeni metnini tazeler (animasyonu sıfırlamaz).</summary>
		public void SetLoadReason(string reason) {
			loadReason = reason ?? string.Empty;
		}

		string loadReason = string.Empty;
		float loadFade;
		float loadProgress;
		bool loadingOut;
		float loadOutFade;

		/// <summary>Loading ekranı açık mı? (fare kilidi buna bakar)</summary>
		public bool LoadingVisible {
			get { return target == View.Loading && loadOutFade < 0.99f; }
		}

		public void Hide() { target = View.Hidden; }

		/// <summary>
		/// Oyunun kendi NEW GAME onay panosuna yer aç.
		///
		/// Neden ayrı bir görünüm: panelimiz ekranın ORTASINI kaplıyor. Oyunun
		/// "NEW GAME WILL REPLACE ANY EXISTING SAVE!" onay panosu da ortada
		/// beliriyor (3B nesne, PlayMaker ile açılıyor). İkimiz üst üste
		/// binerdi ve oyunun BEGIN butonu GÖRÜNMEZ olurdu; kullanıcı fareyi
		/// havada gezdirip boş yere tıklar, "NEW GAME çalışmıyor" sanır.
		///
		/// Çözüm: onay panosu açılır açılmaz kendi ekranımızı kapatıp ekranın
		/// altında tek satır ipucu bırakıyoruz. Onayı kullanan yine oyundur;
		/// yalnızca ekranı paylaşıyoruz.
		/// </summary>
		public void ShowGameDialog() {
			target = View.GameDialog;
			hover = -1;
			fieldFocused = false;
		}

		/// <summary>
		/// Durum satirini gunceller. Renk anahtara gore secilir: boylece
		/// "Steam yok" ve "bitti" mesaji ayni renkte gorunmez.
		/// </summary>
		public void SetStatus(string key, string message) {
			if (key != statusKey) {
				statusKey = key;
				statusColorNext = ColorFor(key);
			}
			StatusContent.text = message ?? string.Empty;
		}

		static Color ColorFor(string key) {
			if (string.IsNullOrEmpty(key)) return new Color(1f, 0.78f, 0.35f);
			if (key == "hosting" || key == "joining") return new Color(0.55f, 0.85f, 1f);
			if (key == "nosteam" || key == "short" || key == "newgame")
				return new Color(1f, 0.45f, 0.40f);
			return new Color(1f, 0.78f, 0.35f);
		}

		// ------------------------------------------------------------------
		// Kare basina
		// ------------------------------------------------------------------

		/// <summary>
		/// Animasyon zamanlayici. Kare hizindan bagimsiz: dt ile ilerler,
		/// sabit kare sayimi yok.
		/// </summary>
		public void Tick(float dt) {
			if (dt < 0f) dt = 0f;
			if (dt > 0.25f) dt = 0.25f;      // pencere odaklaninca ani sıçramayı keser

			clock += dt;
			ClockValue = clock;   // statik cizim yardimcilari icin

			float goal = target == View.Hidden ? 0f : 1f;
			float k = 14f * dt;
			float t = 1f - 1f / (1f + k + 0.5f * k * k);
			fade += (goal - fade) * t;
			if (goal == 0f && fade < 0.01f) fade = 0f;
			if (goal == 1f && fade > 0.99f) fade = 1f;

			slide += ((goal > 0f ? 1f : 0f) - slide) * t;
			ruleT += ((goal > 0f ? 1f : 0f) - ruleT) * t;

			// Skin menusu: modeli dondur ve yalnizca acikken maliyet odele.
			SkinsTick(dt);

			cmdCaret += dt;
			if (cmdCaret > 1f) cmdCaret -= 1f;

			// Loading giriş/çıkış animasyonu.
			if (target == View.Loading) {
				// İçeri gir: hızla karar.
				loadFade += (1f - loadFade) * Mathf.Clamp01(dt * 5f);
				// İlerleme çubuğu hedefe yaklaşır ama asla tam dolmaz;
				// "bitmemiş gibi görünür" çünkü dünya gerçekten yükleniyor.
				loadProgress += (0.94f - loadProgress) * Mathf.Clamp01(dt * 0.6f);
				if (loadingOut) {
					loadOutFade += (1f - loadOutFade) * Mathf.Clamp01(dt * 4f);
					if (loadOutFade >= 0.99f) { Hide(); loadingOut = false; loadOutFade = 0f; }
				}
			}
			else {
				loadOutFade = 0f;
			}
			// NOT: 'press' burada azaltilmaz; Row()/Button() kendi kuraliyla
			// yonetir (fare basiliyken artar, birakilinca kare basina solarak
			// duser). Burada azaltmak cift hiz azalma olurdu.
		}

		// ------------------------------------------------------------------
		// Cizim
		// ------------------------------------------------------------------

		public void Draw() {
			if (fade <= 0.002f) return;

			// ---- IMGUI DURUM İZOLASYONU --------------------------------
			// dnSpy kanıtı: PlayMakerGUI.OnGUI (IL 0x0124 / 0x01B5),
			// ResetGUIMatrix.OnGUI (IL 0x0007) ve DashBoard.OnGUI
			// (IL 0x031A / 0x050C) GUI.matrix'i yazıyor; PlayMakerGUI
			// ayrıca GUI.skin'i ve GUI.color'u da değiştiriyor.
			//
			// MenuUi mantıksal 1280x720 uzayı kullanıyor ve V() ile
			// Screen.width/height'ten hesaplanan dikdörtgenleri ham ekran
			// uzayına çeviriyor. İKİSİ DE "GUI.matrix == identity"
			// varsayıyor. Oyunun OnGUI'si bizden ÖNCE çalışıp ölçekli bir
			// matris bırakırsa imlecin göründüğü yer ile tıklamanın
			// düştüğü yer AYRIŞIR — "bazen tıklanmıyor" belirtisinin
			// bir parçası budur.
			//
			// ConnectionPanel.cs:72 ve PerfHud.cs:188 aynı kalıbı zaten
			// kullanıyor; burada EKSİKTİ.
			Matrix4x4 keepMatrix = GUI.matrix;
			int keepDepth = GUI.depth;
			bool keepEnabled = GUI.enabled;
			Color keepColor = GUI.color;
			Color keepContent = GUI.contentColor;
			Color keepBack = GUI.backgroundColor;
			try { GUI.matrix = Matrix4x4.identity; } catch { }
			try { GUI.depth = 0; } catch { }
			// Oyun bir yerde GUI.enabled=false bırakmışsa bizim
			// görünmez düğmelerimiz de tıklanamaz hale gelir.
			try { GUI.enabled = true; } catch { }

			// ÖLÜ hotControl TEMİZLİĞİ. Oyunun bir düğmesi MouseDown'u
			// alıp MouseUp'i görmezse (panel kapandı, ekran değişti)
			// hotControl o denetimi tutmaya devam eder ve Unity'de
			// O KONTROL AİT OLDUĞU SÜRE TÜM GUI.Button'LAR ÖLÜ OLUR.
			// "Menüde hiçbir şeye tıklanamıyor" belirtisinin klasik
			// nedeni budur. Fare basılı değilken sıfırlamak güvenlidir
			// (sürükleme sırasında dokunmuyoruz).
			ReleaseStaleHotControl();

			// Tum cizim savunmacidir: bir istisna oyunu dusurmemeli
			// (crash-free). Ilk hatada menu bir sonraki karede tekrar dener.
			try {
				EnsureStyles();
				EnsureTextures();
				EnsureBackground();

				ComputeView();
				// MenuMouse'a ölçeği bildir: fare konumu artık OnGUI'den
				// değil, Update'te okunan Input'tan geliyor.
				MenuMouse.SetView(viewScale, viewOffset);

				// Onay panosu görünümünde arka plan ÇİZİLMEZ. DrawBackdrop
				// ekranın tamamına opak degrade çiziyor; çizilirse oyunun 3B
				// NEW GAME onay panosu tamamen görünmez olur, yani paylaşma
				// yerine tam gizleme yapmış oluruz.
				if (target != View.GameDialog) DrawBackdrop();

				if (target == View.Loading) DrawLoading();
				else if (target == View.Online) DrawOnline();
				else if (target == View.GameDialog) DrawGameHint();
				else if (target == View.Skins) DrawSkins();
				else DrawMain();
			}
			catch (Exception e) {
				SmpLog.Warning("menu draw failed: " + e.GetType().Name + ": " + e.Message);
			}

			// ---- IMGUI DURUMUNU GERİ YÜKLE ------------------------------
			// Oyunun diğer OnGUI'leri (PlayMakerGUI, DashBoard) kendi
			// çizimlerine bu değerlerle devam etmeli; bıraktığımız ölçek
			// onların düğmelerini ekranın köşesine kaydırırdı.
			try { GUI.matrix = keepMatrix; } catch { }
			try { GUI.depth = keepDepth; } catch { }
			try { GUI.enabled = keepEnabled; } catch { }
			try { GUI.color = keepColor; } catch { }
			try { GUI.contentColor = keepContent; } catch { }
			try { GUI.backgroundColor = keepBack; } catch { }

			// Bu karede HICBIR oge turetmemisse tiklamayi dusur.
			// Aksi halde bekleyen tiklama kareler boyunca birikir ve
			// fare baska bir yere gelse bile o eski noktadaki bir ogeyi
			// tetikler. (smp anim all gibi test komutlarinda sinir bozucu)
			MenuMouse.EndFrame();
		}

		/// <summary>
		/// Kimseye ait olmayan hotControl'ü bırakır.
		///
		/// Unity'de GUI.Button "basıldı" durumunu <c>GUIUtility.hotControl</c>
		/// ile tutar ve TIKLAMAYI yalnızca hotControl kendisine aitken kabul
		/// eder. Bir düğme basıldıktan sonra çizilmez olursa (menü kapandı,
		/// oyun ekranı değişti) hotControl o denetime takılı kalır ve
		/// SONRAKİ TÜM GUI.Button'LAR tıklanamaz olur. Kullanıcının
		/// "bazen tıklanmıyor" dediği durum budur.
		///
		/// Fare basılıyken dokunmuyoruz: sürükleme hâlâ sürüyorsa
		/// hotControl'ün kaldırılması tıklamayı iptal ederdi.
		/// </summary>
		static void ReleaseStaleHotControl() {
			try {
				Event e = Event.current;
				if (e != null && (e.type == EventType.MouseDown
								|| e.type == EventType.MouseDrag
								|| e.type == EventType.MouseUp)) return;
				if (Input.GetMouseButton(0) || Input.GetMouseButton(1)) return;
				// YALNIZCA hotControl. keyboardControl klavyedeki
				// odaktir (komut kutusu); onu sifirlamak yazma
				// odagini kirdi ve komut kutusu kullanilamaz
				// hale geliyordu.
				if (GUIUtility.hotControl != 0) GUIUtility.hotControl = 0;
			}
			catch { }
		}

		static readonly GUIContent GameHintContent = new GUIContent(
			"The game is asking to confirm. Press BEGIN to start a new game, "
			+ "CANCEL to go back.  ESC also goes back.");

		/// <summary>
		/// Onay panosu açıkken ekranın ALTINDA tek satır ipucu.
		///
		/// Ekranın ORTASI BOŞ BIRAKILIR: oyunun BEGIN/CANCEL butonları orada
		/// duruyor ve tıklanabilir olmalılar. Arka plan da çizilmiyor
		/// (bkz. Draw), yani panosunun önünde hiçbir şey yok.
		///
		/// Yer seçimi neden alt: oyunun menüsü ekranın ortasına yerleşiyor.
		/// Alt şerit tek satır ve dar; üstüne bir şey gelmiyor.
		/// </summary>
		void DrawGameHint() {
			float a = fade;
			const float h = 30f;
			const float w = 820f;
			Rect box = new Rect(REF_W * 0.5f - w * 0.5f, REF_H - h - 28f, w, h);
			Fill(new Color(0.03f, 0.03f, 0.04f, 0.86f * a), box);
			Fill(new Color(Accent.r, Accent.g, Accent.b, 0.55f * a),
				 new Rect(box.x, box.y, 3f, box.height));
			Label(new Rect(box.x + 14f, box.y, box.width - 28f, box.height),
				  GameHintContent, styleHint, Dim, a);
		}

		/// <summary>
		/// Olcegi ekrandan turetir. Tum icerik bundan sonra V() ile cizilir.
		/// </summary>
		void ComputeView() {
			float sw = Screen.width > 0 ? Screen.width : 1280f;
			float sh = Screen.height > 0 ? Screen.height : 720f;
			// "fit" yerine "contain": hicbir ogne hicbir cozunurlukte
			// kirpilmis oge olusmaz.
			viewScale = Mathf.Min(sw / REF_W, sh / REF_H);
			if (viewScale <= 0.001f) viewScale = 0.001f;
			viewOffset = new Vector2((sw - REF_W * viewScale) * 0.5f,
									(sh - REF_H * viewScale) * 0.5f);
		}

		/// <summary>
		/// Arka plan: degrade + hareketli isik huzmesi + vignette.
		/// Uc dokunun ucu de tek DrawTexture cagrisi; piksel basina is yok.
		/// </summary>
		void DrawBackdrop() {
			float a = fade;

			// 1) Dikey degrade (siyah -> lacivertimsi). Oyunun menusu karanlik
			//    foto uzerinde durur; duz siyah yerine hafif renkli degrade
			//    daha derinlik verir ve sarı vurguyu tasir.
			Color k = GUI.color;
			GUI.color = new Color(1f, 1f, 1f, a);
			GUI.DrawTexture(ScreenRect(), gradient, ScaleMode.StretchToFill, true);
			GUI.color = k;

			// 2) Kayan isik huzmesi. Ucgen esleme yok (shader derlenmesi
			//    Unity 5'te riskli); bunun yerine genis, cok soluk dikey
			//    bir ışık bandı yatayda hareket eder.
			float sweep = Mathf.Repeat(clock * 0.11f, 1f);
			float bandW = Screen.width * 0.42f;
			float bandX = (sweep - 0.2f) * Screen.width;
			Color s = GUI.color;
			GUI.color = new Color(1f, 0.92f, 0.72f, 0.05f * a);
			GUI.DrawTexture(new Rect(bandX, 0f, bandW, Screen.height),
							beam, ScaleMode.StretchToFill, true);
			GUI.color = s;

			// 3) Arka plan fotografi varsa uzerine, hafif karartma ile.
			if (background != null) {
				Color b = GUI.color;
				GUI.color = new Color(1f, 1f, 1f, a * 0.55f);
				GUI.DrawTextureWithTexCoords(ScreenRect(), background, SourceCrop(), true);
				GUI.color = b;
			}

			// 4) Kenar karartma (vignette): 4 kirpma, ucuz. HAM ekran uzayi.
			FillScreen(new Color(0f, 0f, 0f, 0.55f * a), new Rect(0f, 0f, Screen.width, 4f));
			FillScreen(new Color(0f, 0f, 0f, 0.55f * a), new Rect(0f, Screen.height - 4f, Screen.width, 4f));
			FillScreen(new Color(0f, 0f, 0f, 0.55f * a), new Rect(0f, 0f, 4f, Screen.height));
			FillScreen(new Color(0f, 0f, 0f, 0.55f * a), new Rect(Screen.width - 4f, 0f, 4f, Screen.height));

			// 5) Ustte ince sarı çizgi: MSC seçgi dilinin izi. HAM ekran uzayi.
			FillScreen(new Color(Accent.r, Accent.g, Accent.b, 0.85f * a),
					   new Rect(0f, 0f, Screen.width, 2f));
		}

		/// <summary>Fotografi ekrani tamamen kaplar (CSS "background-size: cover").</summary>
		Rect SourceCrop() {
			float sw = Screen.width, sh = Screen.height;
			int tw = background.width, th = background.height;
			if (sw <= 0f || sh <= 0f || tw <= 0 || th <= 0) return new Rect(0f, 0f, 1f, 1f);
			float screenAspect = sw / sh;
			float texAspect = (float)tw / th;
			float x = 0f, y = 0f, w = 1f, h = 1f;
			if (texAspect > screenAspect) { w = screenAspect / texAspect; x = (1f - w) * 0.5f; }
			else { h = texAspect / screenAspect; y = (1f - h) * 0.5f; }
			return new Rect(x, y, w, h);
		}

		// ------------------------------------------------------------------
		// Ana menu
		// ------------------------------------------------------------------

		void DrawMain() {
			float a = fade;
			Vector2 mouse = MouseLocal();

			// Dikey yigin tam orta: baslik, kural, UC satir, alt bilgi.
			// Yiginin toplam yuksekligi hesaplanir, sonra ortalanir.
			//
			// SATIR SAYISI: ROW_COUNT kadar satir var (PLAY ONLINE, GORUNUM).
			// Satir eklenip buradaki carpana guncellenmezse yigin REF_H'yi
			// asar; MenuTheme.ROW_COUNT ile BURASI ayni olmak zorundadir.
			//
			// KOMUT SATIRI GERİ GELDI. Önceki sürümde metot (DrawCommandBar)
			// yazılıydı ama HİÇ ÇAĞRILMIYORDU: komut kutusu ekranda
			// görünmüyor, yalnızca altındaki ipucu satırı vardı. Kullanıcı
			// ipucu satırını kaldırmamı, kutu görünsün diye.
			const int ROW_COUNT = MenuTheme.ROW_COUNT;
			float stackH = BRAND_H + BRAND_SUB_H + RULE_H + GAP * 2f
						 + ROW_H * ROW_COUNT + ROW_GAP * (ROW_COUNT - 1)
						 + CMD_H;
			float y = (REF_H - stackH) * 0.5f;

			float cx = REF_W * 0.5f;
			float brandW = 300f;
			float rowX = cx - ROW_W * 0.5f;

			// ---- Marka basligi ------------------------------------------------
			GUIContent brand = BrandText();
			Label(new Rect(cx - brandW * 0.5f, y, brandW, BRAND_H), brand, styleBrand, Accent, a);
			y += BRAND_H;

			Label(new Rect(cx - brandW * 0.5f, y, brandW, BRAND_SUB_H),
					BrandSub, styleBrandSub, Dim, a);
			y += BRAND_SUB_H;

			// ---- Animasyonlu sarı kural -------------------------------------
			// Genislik 0'dan tam gider: acilis hissi verir, sabit cizgi gibi durmaz.
			float fullW = 190f;
			float ruleW = fullW * Ease(ruleT);
			Fill(new Color(Accent.r, Accent.g, Accent.b, 0.9f * a),
				 new Rect(cx - ruleW * 0.5f, y, ruleW, RULE_H));
			// Kuralin ucunda parlak nokta.
			if (ruleW > 4f) {
				Fill(new Color(1f, 1f, 1f, 0.85f * a),
					 new Rect(cx - ruleW * 0.5f - 1.5f, y - 1f, 3f, RULE_H + 2f));
			}
			y += RULE_H + GAP;

			// ---- Iki ana satir ------------------------------------------------
			// Butonlar icerikten olculur; Row() ic cerceveyi cizer, tiklama
			// gorunmez GUI.Button ile alinir.
			// NOT: 14 Ekim sürümünde burada `hover = -1;` YOKTU; sıfırlama
			// menü görünümüne girişlerde (ShowOnline/ShowSkins/ShowLoading)
			// yapılıyor. Aynısı geri getirildi: DrawMain içinde tekrar
			// sıfırlamak fazladan bir atamaydı ve 09 Ekim sürümünden kalmaydı.
			Rect play = new Rect(rowX, y, ROW_W, ROW_H);
			Row(play, RowPlay, 0, a);
			if (play.Contains(mouse)) hover = 0;
			y += ROW_H + ROW_GAP;

			// ---- Skin (gorunum) satiri ------------------------------------
			// Sol etiket "SKIN", sag etiket secili gorunumun adi.
			// Tiklaninca ShowSkins(): 3B onizlemeli tum NPC kadrosu.
			Rect skin = new Rect(rowX, y, ROW_W, ROW_H);
			RowSkin(skin, a);
			if (hover < 0 && skin.Contains(mouse)) hover = 1;
			y += ROW_H + GAP;

			// ---- Komut satiri -------------------------------------------------
			// DrawCommandBar() HIC CAGRILMIYORDU: metot yaziliydi, komut
			// kutusu ekrana hic cizilmiyordu. Statik tarama buldu.
			//
			// Alt bilgi satiri ("smp · komutlar icin konsola yaz") kullanici
			// istegiyle kaldirildi; yerine komut satiri geliyor. Artik hem
			// bilgi satiri yok hem de komut kutusu EKRANDA — daha once
			// ikisi de yoktu (satir ciziliyordu, kutu cizilmiyordu).
			Rect cmdBar = new Rect(rowX, y, ROW_W, CMD_H);
			DrawCommandBar(cmdBar, a);
		}

		/// <summary>
		/// Menu komut satiri. Ayni yurutucu: MSCLoader.
		///
		/// "smp status", "npc Ahmet", "help" ve MSCLoader'in kendi komutlari
		/// buradan da calisir. Odak yalnizca kutuya tiklandiginda alinir;
		/// butonlari kullanirken odak kutuya kilitlenmez.
		/// </summary>
		void DrawCommandBar(Rect r, float a) {
			bool hot = r.Contains(MouseLocal());

			Fill(new Color(0.04f, 0.04f, 0.05f, 0.72f * a), r);
			Fill(new Color(1f, 1f, 1f, 0.04f * a), new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 1f));
			Frame(cmdFocused
				? new Color(Accent.r, Accent.g, Accent.b, a)
				: (hot ? new Color(0.55f, 0.57f, 0.62f, a) : new Color(0.34f, 0.36f, 0.40f, a)),
				r, 1f);

			Rect field = new Rect(r.x + 10f, r.y, r.width - 20f, r.height);
			try {
				// TAB odak kaydirmasin: tamamlama bizim (CompleteCmd).
				Event pre = Event.current;
				if (pre != null && pre.type == EventType.KeyDown && pre.keyCode == KeyCode.Tab) {
					try { pre.Use(); } catch { }
				}

				GUI.SetNextControlName(CmdControlName);
				string next = cmdInput;
				// V() şart: TextField da ham ekran koordinatı ister.
				try { next = GUI.TextField(V(field), cmdInput, styleCmd); }
				catch { }
				if (next != cmdInput) { cmdInput = next; cmdHistoryIndex = -1; }

				// Odak sadece tiklandiginda; her karede zorlamiyoruz cunku
				// bu durumda ok tuslari (UP/DOWN) gecmisi gezdirir ve
				// butonlara odaklanmayi engeller.
				if (GUI.GetNameOfFocusedControl() == CmdControlName) cmdFocused = true;

				// Kutuya tiklama = odaklan. TextField'in kendisi odagi alir,
				// bu yalnizca ipucu/isaret mantigini tetikler. V() ile,
				// aksi halde tiklama alani yanlis yerde kalir.
				bool cmdClick = MenuMouse.Clicked(r);
			if (GUI.Button(V(r), GUIContent.none, styleHit)) cmdClick = true;
			if (!cmdFocused && cmdClick) {
					cmdFocused = true;
					try { GUI.FocusControl(CmdControlName); } catch { }
				}

				Event e = Event.current;
				if (e == null) return;

				if (cmdFocused && e.type == EventType.KeyDown) {
					if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) {
						try { e.Use(); } catch { }
						RunMenuCommand();
					}
					else if (e.keyCode == KeyCode.Tab) {
						CompleteCmd();
					}
					else if (e.keyCode == KeyCode.UpArrow) {
						try { e.Use(); } catch { }
						WalkCmdHistory(-1);
					}
					else if (e.keyCode == KeyCode.DownArrow) {
						try { e.Use(); } catch { }
						WalkCmdHistory(1);
					}
					else if (e.keyCode == KeyCode.Escape) {
						try { e.Use(); } catch { }
						GUI.FocusControl(string.Empty);
						cmdFocused = false;
						cmdInput = string.Empty;
					}
				}
			}
			catch { }

			// Odak yoksa ve kutu bos ise ipucu goster.
			//
			// KALDIRILDI: kullanici istegiyle info/ipucu satirlari gitti.
			// Bos kutu artik bos gorunuyor; komut satiri zaten ne yapilacagini
			// basligindan anlasiliyor. Odaklanmak icin kutuya tiklamak yeterli.
		}

		/// <summary>
		/// Komutu calistirir. Yurutucu MSCLoader'in kendi ayristiricisi
		/// (ModCommands); /npc gibi bize ozel komutlar da ayni yerden gecer.
		/// </summary>
		void RunMenuCommand() {
			string line = cmdInput == null ? string.Empty : cmdInput.Trim();
			// Gecmis
			if (line.Length > 0) {
				if (cmdHistory.Count == 0 || cmdHistory[cmdHistory.Count - 1] != line) {
					cmdHistory.Add(line);
					if (cmdHistory.Count > 40) cmdHistory.RemoveAt(0);
				}
			}
			cmdInput = string.Empty;
			cmdInputKey = "\0";
			cmdHistoryIndex = -1;
			if (line.Length == 0) return;

			try { SmpCommandParser.Execute(line); }
			catch (Exception ex) {
				SmpLog.Error("menu command failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		/// <summary>
		/// TAB tamamlama. Komut listesi ONCE bir kez onbellege alinir;
		/// her tus basmasinda ModCommands'e gitmek gereksiz maliyetti.
		/// </summary>
		void CompleteCmd() {
			if (cmdNames == null || cmdNames.Length == 0) {
				cmdNames = ModCommands.KnownCommands();
				if (cmdNames == null || cmdNames.Length == 0) {
					SmpLog.Warning("no command list yet.");
					return;
				}
			}
			string word = CurrentCmdWord();
			string hit = null;
			int hits = 0;
			for (int i = 0; i < cmdNames.Length; i++) {
				if (!cmdNames[i].StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
				hits++;
				if (hit == null) hit = cmdNames[i];
			}
			if (hits == 0) return;
			if (hits == 1) { ReplaceCmdWord(hit + " "); return; }
			SmpLog.Print(hits + " commands start with '" + word + "'.");
		}

		string CurrentCmdWord() {
			int end = cmdInput.Length, start = end;
			while (start > 0 && IsCmdWord(cmdInput[start - 1])) start--;
			return cmdInput.Substring(start, end - start);
		}

		void ReplaceCmdWord(string replacement) {
			int end = cmdInput.Length, start = end;
			while (start > 0 && IsCmdWord(cmdInput[start - 1])) start--;
			cmdInput = cmdInput.Substring(0, start) + replacement;
		}

		static bool IsCmdWord(char c) {
			return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
				|| (c >= '0' && c <= '9') || c == '_';
		}

		void WalkCmdHistory(int delta) {
			if (cmdHistory.Count == 0) return;
			if (cmdHistoryIndex == -1) {
				if (delta > 0) return;
				cmdHistoryIndex = cmdHistory.Count - 1;
			}
			else {
				int next = cmdHistoryIndex + delta;
				if (next < 0) next = 0;
				if (next >= cmdHistory.Count) { cmdHistoryIndex = -1; cmdInput = string.Empty; return; }
				cmdHistoryIndex = next;
			}
			cmdInput = cmdHistory[cmdHistoryIndex];
		}

		/// <summary>
		/// Tek ana girdi: duz kutu + sol vurgu cubugu + hover parlamasi.
		///
		/// Efektler uc katmanda: (1) ic cerceve hafif acilir, (2) sol cubuk
		/// uzar, (3) dis halka belirir. Hepsi ayni Rect uzerinden; icerik
		/// kaymaz, sadece cerceve buyur.
		/// </summary>
		void Row(Rect r, GUIContent content, int index, float a) {
			bool hot = r.Contains(MouseLocal());

			// Basma geri bildirimi yalnizca fare butonu basili VEYA yeni
			// tiklandiginda. OnGUI iki kez cagrildigi icin sadece
			// MouseDown/Repaint aninda guncellenir; her iki cagrida da
			// "basili" durumu ayni okunur.
			if (hot) {
				Event e = Event.current;
				bool down = (e != null && e.type == EventType.MouseDown && e.button == 0);
				if (!down) down = (Event.current.type == EventType.Layout && MenuMouse.Held);
				if (down) press = 1f;
				else if (e != null && e.type == EventType.Repaint) {
					if (MenuMouse.Held) press = Mathf.Max(press, 0.6f);
					else press = Mathf.Max(0f, press - 0.08f);
				}
			}
			else {
				press = Mathf.Max(0f, press - 0.08f);
			}

			float lift = hot ? 2f : 0f;
			float glow = hot ? 1f : 0f;
			float pressK = press;

			Rect box = new Rect(r.x, r.y - lift + pressK * 1.5f, r.width, r.height);

			// Dış parlam: 3 katmanli, alfa düşük (gerçek blur yerine).
			if (glow > 0.01f) {
				float g = glow * (0.35f - pressK * 0.15f);
				Frame(new Color(Accent.r, Accent.g, Accent.b, 0.10f * g * a), Grow(box, 3f), 1f);
				Frame(new Color(Accent.r, Accent.g, Accent.b, 0.13f * g * a), Grow(box, 6f), 1f);
			}

			// Gövde: koyu cam. Üstte hafif aydınlık, altta koyu.
			Fill(new Color(0.07f, 0.07f, 0.09f, MenuTheme.PanelAlpha * a), box);
			Fill(new Color(1f, 1f, 1f, 0.05f * a), new Rect(box.x + 1f, box.y + 1f, box.width - 2f, 1f));

			// Sol vurgu çubuğu: seçiliyse uzar.
			float bar = hot ? 4f : 2f;
			float barH = (hot ? box.height - 8f : box.height - 18f) * Ease(glow * 0.35f + 0.65f);
			barH = Mathf.Max(barH, 6f);
			Fill(new Color(Accent.r, Accent.g, Accent.b, (hot ? 1f : 0.7f) * a),
				 new Rect(box.x + 1f, box.y + (box.height - barH) * 0.5f, bar, barH));

			// Çerçeve
			Frame(hot
				? new Color(Accent.r, Accent.g, Accent.b, 0.95f * a)
				: new Color(0.45f, 0.47f, 0.52f, 0.85f * a), box, 1f);

			// Kayan parlaklık şeridi (sadece üzerindeyken).
			if (hot) DrawSheen(box, a, index * 0.37f);

			// Yazı: ortalanmış, seçiliyse sarı.
			Color c = hot ? Accent : Normal;
			Label(box, content, styleRow, c, a);

			// Sağ ok ipucu: sadece seçiliyken, hafif kayarak.
			if (hot) {
				float nudge = Mathf.Sin(clock * 5f) * 1.5f;
				Label(new Rect(box.xMax - 26f + nudge, box.y, 20f, box.height),
						ArrowContent, styleRow, Accent, a);
			}

			// Tıklanabilir ama görünmez: asıl kutu biz çiziyoruz.
			//
			// V() ŞARTH: GUI.Button ham ekran koordinatı ister. Fill/Frame
			// mantıksal koordinatı V() ile çeviriyor; Button'a çevrilmeden
			// verilirse tıklama alanı görsel butonun ALTINDA kalır ve
			// "tıklıyorum ama hiçbir şey olmuyor" belirtisi olur.
			// IKI YOL BIRLIKTE. GUI.Button normal IMGUI yoludur; MenuMouse
			// ise Update'te okunan Input'a bakar. Oyunun OnGUI'si olayi
			// tuketirse veya hotControl'u kaparsa GUI.Button susar, MenuMouse
			// devreye girer. Ikisi de ayni tiklamayi isleyebilir ama
			// OnRow yalnizca index'e gore dallanir, yan etkisi yoktur.
			// MenuMouse.Clicked DAIMA cagrilir (kisac devresi olsa da):
			// cagrilmazsa bekleyen tiklama TUKETILMEZ ve bir sonraki
			// satira sizar. Once tuket, sonra iki yolu birlestir.
			bool mmClick = MenuMouse.Clicked(box);
			bool guiClick = GUI.Button(V(box), GUIContent.none, styleHit);
			if (guiClick || mmClick) OnRow(index);
		}

		bool OnRow(int index) {
			active = index;
			press = 0f;
			// Satır 1 = GÖRÜNÜM (skin seçimi).
			//
			// ÖNCEKİ HATA: burada index != 0 olan HER ŞEY "PLAY ONLINE"
			// sayılıyordu. RowSkin() satırı ayrı bir index ile Row()'a
			// devrediyor, dolayısıyla GÖRÜNÜM'e tıklamak multiplayer
			// ekranını açıyordu ("görünüm sekmesine tıklıyorum,
			// multiplayer geliyor"). Satır numarası değişince buraya da
			// karşılığı GÜNCELLENMELİ.
			if (index == 1) { ShowSkins(); return true; }
			if (OnPlayOnline != null) OnPlayOnline();
			return true;
		}

		// ------------------------------------------------------------------
		// Online paneli
		// ------------------------------------------------------------------

		/// <summary>
		/// LOADING MULTIPLAYER ekranı.
		///
		/// Tasarım: ekran TAMAMEN siyah (o menü görüntüsü altında kalmasın),
		/// ortada MSC fontuyla (GUI.skin.font = Unity'nin yerleşik Arial,
		/// oyunun menü yazı tipi) sarı başlık, altında ince kural, onun
		/// altında ilerleme çubuğu ve durum satırı.
		///
		/// Dünya yüklenince <see cref="HideLoading"/> çağrılır ve ekran
		/// yumuşakça solar (loadOutFade 0->1), sonra menü tamamen kapanır.
		///
		/// Ekran uzayı: ham (FillScreen) arka plan + mantıksal içerik.
		/// </summary>
		void DrawLoading() {
			float a = loadFade;
			float outA = 1f - loadOutFade;      // kapanırken solar

			// Zemin. Eskiden TAM SİYAHTI (outA) ve oyunu tamamen kesiyordu;
			// kullanıcı "şeffaf tasarım" istedi. Oyun arkada görünür, metin
			// okunabilir kalsın diye 0.82 ile sınırlı.
			FillScreen(new Color(0f, 0f, 0f, outA * 0.82f), ScreenRect());

			float cx = REF_W * 0.5f;
			float totalH = LOAD_TITLE_H + 10f + LOAD_BAR_H + 14f + LOAD_SUB_H;
			float y = (REF_H - totalH) * 0.5f;

			// Başlık: "LOADING MULTIPLAYER" (MSC fontu, büyük, sarı).
			// Harf harf belirme efekti: clock'tan türeyen kayan bir maske.
			float reveal = Mathf.Clamp01(loadFade * 1.4f);
			Label(new Rect(cx - 400f, y, 800f, LOAD_TITLE_H),
				  LoadTitleContent(reveal), styleLoadTitle, Accent, outA);
			y += LOAD_TITLE_H + 10f;

			// İlerleme çubuğu: dolu kısım büyür, ucunda parlak nokta.
			float barW = LOAD_BAR_W;
			Rect track = new Rect(cx - barW * 0.5f, y, barW, LOAD_BAR_H);
			Fill(new Color(1f, 1f, 1f, 0.10f * outA), track);
			float fillW = barW * loadProgress;
			if (fillW > 1f) {
				Fill(new Color(Accent.r, Accent.g, Accent.b, 0.9f * outA),
					 new Rect(track.x, track.y, fillW, LOAD_BAR_H));
				// Parlak uç.
				Fill(new Color(1f, 1f, 1f, 0.9f * outA),
					 new Rect(track.x + fillW - 2f, track.y - 1f, 3f, LOAD_BAR_H + 2f));
			}
			y += LOAD_BAR_H + 14f;

			// Durum satırı: neden yüklüyoruz.
			Label(new Rect(cx - 400f, y, 800f, LOAD_SUB_H),
				  LoadStatusContent(), styleLoadSub, Dim, outA);
		}

		string loadTitleKey = "\0";
		GUIContent loadTitle = new GUIContent(string.Empty);
		GUIContent LoadTitleContent(float reveal) {
			// Harf harf belirme: ilk reveal kadar karakteri göster.
			string full = "LOADING MULTIPLAYER";
			int n = Mathf.Clamp(Mathf.CeilToInt(full.Length * reveal), 0, full.Length);
			string text = full.Substring(0, n);
			if (text != loadTitleKey) {
				loadTitleKey = text;
				loadTitle.text = text;
			}
			return loadTitle;
		}

		GUIContent LoadStatusContent() {
			string s = string.IsNullOrEmpty(loadReason) ? "Connecting to your friend..." : loadReason;
			if (loadStatus.text != s) loadStatus.text = s;
			return loadStatus;
		}
		GUIContent loadStatus = new GUIContent(string.Empty);

		void DrawOnline() {
			float a = fade;

			// Panel icerigi ONCE olculur. Panel boyutu bu olcumden cikar;
			// cizim de ayni olcumden yurur. Iki ayri hesap olmadigi icin
			// tasma olusamaz.
			float inner = PANEL_W - PANEL_PAD * 2f;
			float btns = BTN_H + BTN_GAP;
			float code = CODE_LABEL_H + CODE_H;
			// SUB_H (alt başlık) ÖLÇÜYE DAHİL DEĞİL: kullanıcı isteğiyle
			// "Two players, one save" satırı kaldırıldı ve arkasında boş
			// şerit kalmasın diye yükseklik de düşürüldü.
			float h = PANEL_PAD + TITLE_H + GAP + btns + GAP
					+ code + GAP + STATUS_H + GAP + BACK_H + PANEL_PAD;

			// Kayma + hafif buyume: panel aşağıdan geliyor gibi.
			float s = Ease(slide);
			Rect panel = new Rect((REF_W - PANEL_W) * 0.5f,
								  (REF_H - h) * 0.5f + (1f - s) * 26f,
								  PANEL_W, h);

			// Ekran karartma: sadece online panelde, icerik odaklansin. HAM ekran uzayi.
			FillScreen(new Color(0f, 0f, 0f, 0.25f * a), ScreenRect());

			// Panel gövdesi + dış parlam (parlama sabit, panel kaydıkça yerinde).
			// 0.93 -> MenuTheme.PanelAlpha: kullanıcı "şeffaf tasarım" istedi.
			Fill(new Color(0.06f, 0.06f, 0.08f, MenuTheme.PanelAlpha * a), panel);
			Frame(new Color(0.42f, 0.44f, 0.50f, 0.9f * a), panel, 1f);
			Frame(new Color(Accent.r, Accent.g, Accent.b, 0.10f * a), Grow(panel, 4f), 1f);

			// Üst vurgu çizgisi: iki uçta sönen sarı bant.
			Fill(new Color(Accent.r, Accent.g, Accent.b, 0.95f * a),
				 new Rect(panel.x, panel.y, panel.width, 2f));
			float edge = panel.width * 0.22f * Ease(ruleT);
			Fill(new Color(1f, 1f, 1f, 0.8f * a),
				 new Rect(panel.x, panel.y - 1f, edge, 1f));
			Fill(new Color(1f, 1f, 1f, 0.8f * a),
				 new Rect(panel.xMax - edge, panel.y - 1f, edge, 1f));

			float x = panel.x + PANEL_PAD;
			float y = panel.y + PANEL_PAD;

			// ---- Baslik ------------------------------------------------------
			// Alt başlık ("Two players, one save") kaldırıldı: kullanıcı
			// isteği, ve yukarıdaki yükseklik hesabı zaten SUB_H yok sayıyor.
			Label(new Rect(x, y, inner, TITLE_H), OnlineTitle, styleTitle, Accent, a);
			y += TITLE_H + GAP;

			// ---- OPEN LOBBY / JOIN LOBBY ------------------------------------
			float bw = (inner - BTN_GAP) * 0.5f;
			Rect host = new Rect(x, y, bw, BTN_H);
			Rect join = new Rect(x + bw + BTN_GAP, y, bw, BTN_H);
			if (Button(host, HostContent, a, 0.0f)) { if (OnHost != null) OnHost(); }
			if (Button(join, JoinContent, a, 0.5f)) { if (OnJoin != null) OnJoin(); }
			y += btns + GAP;

			// ---- Kod satiri (iki dal ayni olcu ve cizim yolunu kullanir) ----
			bool hostView = HasHostCode;
			Label(new Rect(x, y, inner, CODE_LABEL_H),
					hostView ? HostLabelContent : JoinLabelContent, styleSub, Dim, a);
			y += CODE_LABEL_H;

			Rect codeRow = new Rect(x, y, inner, CODE_H);
			CodeBox(codeRow, hostView, a);
			y += CODE_H + GAP;

			// ---- Durum (nabiz) ------------------------------------------------
			Label(new Rect(x, y, inner, STATUS_H), StatusContent, styleStatus,
				  Pulse(statusColor), a);
			y += STATUS_H + GAP;

			// ---- BACK (panelden turetilmis konum: asla disari tasmaz) --------
			Rect back = new Rect(panel.x + (panel.width - 160f) * 0.5f, y, 160f, BACK_H);
			if (Button(back, BackContent, a, 1f)) { if (OnBack != null) OnBack(); }
		}

		/// <summary>
		/// Kod satiri. Iki farkli sey GOSTERILIR:
		///
		///   * 6 haneli OTURUM KODU  - arkadasina soyleyecegin kisa kod. Oturumu
		///     acar (el sikismada dogrulanir) ama lobiyi adreslemez.
		///   * 17 haneli LOBBY ID    - Steam'in veridigi tek gercek adres.
		///     Bunu yazan istemci KESIF BEKLEMEZ, dogrudan girer.
		///
		/// Eskiden sadece 6 haneli kod gosteriliyordu ve yazma kutusu da 6
		/// karakterle sinirliydi; yani "lobi kodu ile giris" yolu hicbir zaman
		/// calismiyor, kullanici 60 saniye bekleyip hata goruyordu.
		/// </summary>
		void CodeBox(Rect r, bool hostView, float a) {
			bool hot = r.Contains(MouseLocal());

			// Kutu.
			Fill(new Color(0.03f, 0.03f, 0.04f, 0.85f * a), r);
			Fill(new Color(1f, 1f, 1f, 0.04f * a), new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 1f));

			Color border;
			float borderW = 1f;
			if (hostView) border = hot ? new Color(1f, 1f, 1f, 0.7f * a) : new Color(0.35f, 0.37f, 0.42f, a);
			else if (fieldFocused) border = new Color(Accent.r, Accent.g, Accent.b, a);
			else if (hot) border = new Color(0.55f, 0.57f, 0.62f, a);
			else border = new Color(0.32f, 0.34f, 0.38f, a);
			Frame(border, r, borderW);

			if (fieldFocused && !hostView) {
				// Odak halkasi: nabiz.
				Frame(new Color(Accent.r, Accent.g, Accent.b, 0.25f * a * Pulse01()), Grow(r, 3f), 1f);
			}

			if (hostView) {
				// Ust satir: kisa oturum kodu (buyuk, kutucuklu).
				float topH = r.height * 0.5f;
				DrawCodeCells(new Rect(r.x + 14f, r.y + 3f, r.width - 28f, topH),
					CodeContent.text, a, true);
				// Alt satir: lobi id (duz metin, ayni kutuda tikla kopyalanir).
				float subY = r.y + 3f + topH + 2f;
				float subH = r.height - (subY - r.y) - 3f;
				Rect sub = new Rect(r.x + 14f, subY, r.width - 28f, subH);
				if (!string.IsNullOrEmpty(hostLobbyId)) {
					Label(sub, HostLobbyIdContent(hostLobbyId, hot), styleCodeHint,
						hot ? Accent : Dim, a);
				}
				else {
					Label(sub, LobbyIdPendingContent, styleCodeHint, Dim, a * 0.8f);
				}
			}
			else {
				DrawTypedCode(new Rect(r.x + 14f, r.y, r.width - 28f, r.height), a);
			}

			// Tiklama: tek gorunmez buton, iki dalin da ustunde. V() sart, bak
			// Row()'daki aciklama. MenuMouse yedegi: oyunun OnGUI'si
			// olayi tuketirse yalnizca bu yol calisir.
			bool codeClick = MenuMouse.Clicked(r);
			if (GUI.Button(V(r), GUIContent.none, styleHit)) codeClick = true;
			if (codeClick) {
				if (hostView) {
					// Once lobi kimligi kopyalanir: arkadasinin GERCEKTEN
					// ihtiyaci olan sey bu. Kisa kod zaten ekranda gorunuyor.
					bool haveId = !string.IsNullOrEmpty(hostLobbyId);
					string what = haveId ? hostLobbyId : CodeContent.text;
					CopyToClipboard(what);
					SmpLog.Print((haveId ? "lobby id" : "lobby code")
						+ " copied to the clipboard: " + what);
				}
				else {
					fieldFocused = true;
				}
			}
		}

		/// <summary>
		/// Istemcide yazilan sey.
		///
		/// 6 haneli kod kutucuklarla, lobi kimligi duz metin olarak gosterilir:
		/// kutucuk 17 haneye sigmaz ve her karakter icin ayri cerceve cizmek
		/// yerine duz yazi hem okunur hem yer kaplar. Hangisinin yazildigi
		/// metinden belli.
		/// </summary>
		void DrawTypedCode(Rect r, float a) {
			if (LooksLikeLobbyId) {
				Label(r, TypedLobbyIdContent(typedCode), styleCode, Accent, a);
				return;
			}
			DrawCodeCells(r, typedCode, a, fieldFocused);
			if (typedCode.Length == 0 && !fieldFocused) {
				Label(new Rect(r.x + 2f, r.y, r.width, r.height),
					PlaceholderContent, styleCodeHint, Dim, a);
			}
		}

		static readonly GUIContent LobbyIdPendingContent
			= new GUIContent("lobby id pending...");

		string hostLobbyKey = "\0";
		GUIContent hostLobbyContent = new GUIContent(string.Empty);
		GUIContent HostLobbyIdContent(string id, bool hot) {
			// Metin her kare yeniden birlestirilmesin.
			string key = id + (hot ? "1" : "0");
			if (key != hostLobbyKey) {
				hostLobbyKey = key;
				hostLobbyContent.text = "LOBBY ID  " + id + (hot ? "   (click to copy)" : "");
			}
			return hostLobbyContent;
		}

		string typedLobbyKey = "\0";
		GUIContent typedLobbyContent = new GUIContent(string.Empty);
		GUIContent TypedLobbyIdContent(string id) {
			if (id != typedLobbyKey) {
				typedLobbyKey = id;
				typedLobbyContent.text = id + "   (lobby id)";
			}
			return typedLobbyContent;
		}

		/// <summary>
		/// Kod karakterlerini kutucuklara boler. Yazilan kisim sari, bos
		/// yerler soluk cerceve. Ilerleme gorsel olarak okunur.
		/// </summary>
		void DrawCodeCells(Rect r, string code, float a, bool filled) {
			int n = LobbyCode.LENGTH;
			if (n <= 0) return;
			float gap = 6f;
			float cellW = (r.width - gap * (n - 1)) / n;
			if (cellW < 6f) cellW = 6f;
			for (int i = 0; i < n; i++) {
				Rect cell = new Rect(r.x + i * (cellW + gap), r.y + 6f, cellW, r.height - 12f);
				bool has = code != null && i < code.Length;
				Color c = has ? Accent : new Color(0.55f, 0.57f, 0.62f);

				if (has) Fill(new Color(c.r, c.g, c.b, 0.16f * a), cell);
				Frame(new Color(c.r, c.g, c.b, (has ? 0.85f : 0.35f) * a), cell, 1f);

				if (has) {
					string ch = code.Substring(i, 1);
					Label(cell, CharContent(ch), styleCode, filled ? Accent : c, a);
				}
				else if (filled && i == (code == null ? 0 : code.Length)) {
					// Imlec kutusu: yazma sirasinda odak halkasi.
					float pulse = Pulse01();
					Frame(new Color(1f, 1f, 1f, 0.5f * pulse * a), Grow(cell, 1f), 1f);
				}
			}
		}

		/// <summary>
		/// MSC düğmesi: düz dolgu, çerçeve, hover'da kayan parlaklık.
		/// Tıklama yine görünmez GUI.Button ile.
		/// </summary>
		bool Button(Rect r, GUIContent content, float a, float phase) {
			bool hot = r.Contains(MouseLocal());
			float lift = hot ? 1.5f : 0f;
			bool down = false;
			Event cur = Event.current;
			if (hot && cur != null && cur.type == EventType.MouseDown && cur.button == 0) down = true;
			// MenuMouse basili bilgisi de verir: OnGUI olayi tuketilmis
			// olsa bile dugmenin gorunumu basili duruma gecer.
			if (hot && MenuMouse.Held) down = true;

			Rect box = new Rect(r.x, r.y - lift + (down ? 1f : 0f), r.width, r.height);

			if (hot) {
				Frame(new Color(Accent.r, Accent.g, Accent.b, 0.12f * a), Grow(box, 3f), 1f);
			}

			Fill(new Color(hot ? 0.20f : 0.13f, hot ? 0.19f : 0.13f, hot ? 0.16f : 0.15f, 0.95f * a), box);
			Fill(new Color(1f, 1f, 1f, 0.06f * a), new Rect(box.x + 1f, box.y + 1f, box.width - 2f, 1f));
			Frame(hot
				? new Color(Accent.r, Accent.g, Accent.b, a)
				: new Color(0.44f, 0.46f, 0.50f, 0.9f * a), box, 1f);

			if (hot) DrawSheen(box, a, phase);
			Label(box, content, styleButton, hot ? Accent : Normal, a);

			// IKI YOL: GUI.Button (normal IMGUI) + MenuMouse (Input).
			// Ikisi de ayni tiklamayi OnGUI'de iki kez bildirebilir ama
			// cagiran taraf MenuMouse.Clicked'in TUKETTIGI tiklamayi
			// onun yerine gorur; cift etki olmaz.
			bool mmClick = MenuMouse.Clicked(box);
			bool guiClick = GUI.Button(V(box), GUIContent.none, styleHit);
			return guiClick || mmClick;
		}

		/// <summary>
		/// Kutunun icinden gecen dar parlaklik seridi. Gercek gradient
		/// shader yerine: once yatay konum belirlenir, sonra yalnizca o
		/// bolgede acik dikdortgen cizilir. Kutu genisligi kadar ucuz.
		/// </summary>
		void DrawSheen(Rect r, float a, float phase) {
			float t = Mathf.Repeat(clock * 0.55f + phase, 1f);
			float w = r.width * 0.34f;
			float x = r.x + (t - 0.2f) * r.width;
			// Sadece kutu icinde kalan bolgeyi ciz.
			float left = Mathf.Max(x, r.x);
			float right = Mathf.Min(x + w, r.xMax);
			if (right <= left) return;
			Fill(new Color(1f, 1f, 1f, 0.07f * a), new Rect(left, r.y, right - left, r.height));
		}

		/// <summary>
		/// Metni panoya kopyalar.
		///
		/// Neden TextEditor: dnSpy kanitı, Unity 5'te
		/// <c>GUIUtility.systemCopyBuffer</c> INTERNAL (WrapperlessIcall), yani
		/// moddan dogrudan erisilemiyor. TextEditor.Copy() tek erisilebilir yol.
		///
		/// Burada sadece YAZMA var; SelectAll yaptigimiz icin Copy() gercekten
		/// panoyu yazar. (Okuma tarafi MenuController.ClipboardText'te ve
		/// OnGUI'de yapilmak zorunda.)
		/// </summary>
		public static void CopyToClipboard(string text) {
			try {
				if (string.IsNullOrEmpty(text)) return;
				TextEditor te = new TextEditor();
				te.content = new GUIContent(text);
				te.SelectAll();
				te.Copy();
			}
			catch (Exception) { }
		}

		// ------------------------------------------------------------------
		// Cizim yardimcilari
		// ------------------------------------------------------------------

		/// <summary>
		/// Fareyi mantiksal uzaya cevirir.
		///
		/// KAYNAK: MenuMouse (Input, Update icinde okunur).
		///
		/// NEDEN Event.current DEGIL -- uc somut hata:
		///   1) Event.current.mousePosition GUI.matrix'ten ETKILENIR.
		///      Oyunun OnGUI'si (PlayMakerGUI IL 0x0124, DashBoard IL
		///      0x031A) bizi OnGUI'den once cagirip olcekli bir matris
		///      birakirsa konum yanlis gelir; imlecin gorundugu yer ile
		///      tiklamanin dustugu yer AYRISIR.
		///   2) OnGUI olayini oyun tuketirse (PlayMaker
		///      FsmStateAction.OnGUI -> GUI.Button) Event bize hic ulasmaz.
		///   3) Onceki surum, Layout/Repaint disindaki olaylarda CACHE'lenmis
		///      degeri donduruyordu; fare MouseDown olayi arasinda
		///      kipirdadiginda tiklama yanlis satira dustu.
		///
		/// MenuMouse yalnizca Update'te okudugu ham ekran pikselini
		/// (GUI.matrix'ten bagimsiz) ayni olcekle geri cevirir; sonuc her
		/// zaman EKRANLA UYUMLUDUR.
		/// </summary>
		Vector2 MouseLocal() {
			if (MenuMouse.Enabled) return MenuMouse.Logical;
			// Yedek yol: MenuMouse henuz calismadiysa (menu ilk karede
			// aciliyor olabilir) eski hesap.
			Event e = Event.current;
			if (e == null) return new Vector2(-9999f, -9999f);
			if (viewScale <= 0.0001f) return e.mousePosition;
			return new Vector2((e.mousePosition.x - viewOffset.x) / viewScale,
								   (e.mousePosition.y - viewOffset.y) / viewScale);
		}

		static readonly Color Accent = MenuTheme.Accent;   // MSC secgi sarisi
		static readonly Color Normal = MenuTheme.Normal;
		static readonly Color Dim = MenuTheme.Dim;
		/// <summary>Uyari rengi (gövde uyuşmazlığı, hata).</summary>
		static readonly Color Warn = MenuTheme.Warn;

		static Rect Grow(Rect r, float d) {
			return new Rect(r.x - d, r.y - d, r.width + d * 2f, r.height + d * 2f);
		}

		/// <summary>
		/// Duz renk dikdortgen. MANTIKSAL koordinat alir.
		///
		/// KRITIK: burada V() ile ekrana kirpilir. Onceki surumde Fill/Frame
		/// donusumu yapmiyordu, Label() yapiyordu; sonuc olarak kutu mantiksal
		/// koordinati EKRAN koordinati sanilip ekranin sol ustune ciziliyor,
		/// yazi ise ortali ciziliyordu. Artik her cizim ayni donusumden gecer.
		/// </summary>
		void Fill(Color c, Rect r) {
			if (r.width <= 0f || r.height <= 0f) return;
			Rect s = V(r);
			if (s.width <= 0f || s.height <= 0f) return;
			Color keep = GUI.color;
			GUI.color = c;
			GUI.DrawTexture(s, pixel);
			GUI.color = keep;
		}

		/// <summary>
		/// HAM ekran uzayinda dolgu (arka plan, karartma, kenar bandi).
		///
		/// Fill() mantiksal koordinat bekler; bunlar zaten ekran
		/// koordinatidir, o yuzden ayri bir yol gerekir. Ic ve dis
		/// cizim karismasin diye isim ayri tutuldu.
		/// </summary>
		void FillScreen(Color c, Rect s) {
			if (s.width <= 0f || s.height <= 0f) return;
			Color keep = GUI.color;
			GUI.color = c;
			GUI.DrawTexture(s, pixel);
			GUI.color = keep;
		}

		/// <summary>
		/// Cerceve. MANTIKSAL koordinat ve mantiksal kalinlik alir; kalinlik
		/// de ekrana kirpilir, boylece 1920x1080'de cerceve 1 mantiksal = 1.5
		/// gercek piksel olur ve her cozunurlukte ayni gorunur.
		/// </summary>
		void Frame(Color c, Rect r, float t) {
			if (r.width <= 0f || r.height <= 0f || t <= 0f) return;
			// Kalinlik mantiksal birimde verilir ve Fill() tarafindan
			// V() ile kirpilir; boylece cerceve her cozunurlukte ayni
			// gorunur (1920x1080'de 1 mantiksal = 1.5 gercek piksel).
			Fill(c, new Rect(r.x, r.y, r.width, t));
			Fill(c, new Rect(r.x, r.yMax - t, r.width, t));
			Fill(c, new Rect(r.x, r.y + t, t, r.height - t * 2f));
			Fill(c, new Rect(r.xMax - t, r.y + t, t, r.height - t * 2f));
		}

		/// <summary>
		/// Siyah cevreli yazi: dort kosede kaydirip siyah ciz, sonra asil
		/// rengi ustune ciz. Oyunun menu yazilari fotograf ustunde okunur
		/// kalmasi icin boyle; duz metin fotografta kayboluyor.
		///
		/// Stillerin textColor'i hep beyaz; renk GUI.color ile veriliyor,
		/// cunku GUI.color metin rengini carpar.
		/// </summary>
		static readonly Vector2[] Outline = {
			new Vector2(-1f, -1f), new Vector2(1f, -1f),
			new Vector2(-1f,  1f), new Vector2(1f,  1f),
		};

		/// <summary>Mantiksal Rect'i alir, ekrana kirpar, cevreli yazar.</summary>
		void Label(Rect logical, GUIContent content, GUIStyle style, Color color, float a) {
			if (content == null) return;
			Rect r = V(logical);
			Color keep = GUI.color;
			GUI.color = new Color(0f, 0f, 0f, 0.85f * a);
			for (int i = 0; i < Outline.Length; i++) {
				GUI.Label(new Rect(r.x + Outline[i].x * viewScale,
								   r.y + Outline[i].y * viewScale, r.width, r.height),
						  content, style);
			}
			GUI.color = new Color(color.r, color.g, color.b, a);
			GUI.Label(r, content, style);
			GUI.color = keep;
		}

		// ------------------------------------------------------------------
		// Metinler
		// ------------------------------------------------------------------

		static readonly GUIContent RowPlay = new GUIContent("PLAY ONLINE");
		static readonly GUIContent ArrowContent = new GUIContent(">");
		static readonly GUIContent OnlineTitle = new GUIContent("ONLINE MULTIPLAYER");
		// OnlineSubtitle kaldirildi (kullanici istegi: info satirlari gereksiz).
		static readonly GUIContent HostContent = new GUIContent("OPEN LOBBY");
		static readonly GUIContent JoinContent = new GUIContent("JOIN LOBBY");
		static readonly GUIContent BackContent = new GUIContent("BACK");
		static readonly GUIContent HostLabelContent = new GUIContent("LOBBY CODE  -  click to copy");
		static readonly GUIContent JoinLabelContent = new GUIContent("ENTER THE LOBBY CODE");
		static readonly GUIContent PlaceholderContent = new GUIContent("click here, then type");
		static readonly GUIContent CopyContent = new GUIContent("copy");

		// Marka metinleri
		string brandKey = "\0";
		GUIContent brandContent = new GUIContent(string.Empty);
		GUIContent BrandText() {
			if (brandKey == "\0") {
				brandContent.text = "MY SUMMER CAR";
				brandKey = brandContent.text;
			}
			return brandContent;
		}

		// FooterText() KALDIRILDI: alt bilgi satırı ("smp · komutlar icin
		// konsola yaz") kullanıcı isteğiyle menüden kaldırıldı; komut kutusu
		// artık ekranda, ipucu satırına gerek yok.

		static readonly GUIContent BrandSub = new GUIContent("S T E A M   M U L T I P L A Y E R");

		/// <summary>Kod karakteri icin paylasilan GUIContent (kare basina yeni ayirma yok).</summary>
		static GUIContent charContent = new GUIContent(string.Empty);
		static GUIContent CharContent(string ch) {
			charContent.text = ch;
			return charContent;
		}

		// ------------------------------------------------------------------
		// Stiller
		// ------------------------------------------------------------------

		GUIStyle styleBrand, styleBrandSub, styleRow, styleTitle, styleSub,
				 styleButton, styleStatus, styleCode, styleCodeHint, styleFoot, styleHint,
				 styleCmd, styleCmdHint, styleHit, styleLoadTitle, styleLoadSub;
		bool stylesReady;
		GUISkin skinBefore;

		void EnsureStyles() {
			if (stylesReady && skinBefore == GUI.skin) return;
			skinBefore = GUI.skin;

			// Oyunun menu yazi tipi. Oyun .ttf tasimiyor, dolayisiyla MSC de
			// Unity'nin yerlesik Arial'ini kullaniyor; GUI.skin.font tam olarak o.
			GUIStyle root = GUI.skin.label;

			// Font boyutlari MANTIKSAL uzayda. 1920x1080'de olcek 1.5 oldugu
			// icin 30 mantiksal piksel = 45 gercek piksel.
			styleBrand = Make(root, 34, TextAnchor.MiddleCenter);
			styleBrandSub = Make(root, 12, TextAnchor.MiddleCenter);
			styleRow = Make(root, 21, TextAnchor.MiddleCenter);
			styleTitle = Make(root, 22, TextAnchor.MiddleLeft);
			styleSub = Make(root, 12, TextAnchor.MiddleLeft);
			styleButton = Make(root, 15, TextAnchor.MiddleCenter);
			styleStatus = Make(root, 13, TextAnchor.MiddleCenter);
			styleCode = Make(root, 20, TextAnchor.MiddleCenter);
			styleCodeHint = Make(root, 12, TextAnchor.MiddleLeft);
			styleFoot = Make(root, 11, TextAnchor.MiddleCenter);
			styleHint = Make(root, 10, TextAnchor.MiddleCenter);
			styleCmd = Make(root, 14, TextAnchor.MiddleLeft);
			styleCmdHint = Make(root, 12, TextAnchor.MiddleLeft);
			// Loading basligi buyuk ve MSC fontunda; alt satiri kucuk.
			styleLoadTitle = Make(root, 44, TextAnchor.MiddleCenter);
			styleLoadSub = Make(root, 13, TextAnchor.MiddleCenter);

			// Yalnizca tiklama alani: gorunmez.
			styleHit = new GUIStyle(GUI.skin.button);
			styleHit.normal.background = null;
			styleHit.hover.background = null;
			styleHit.active.background = null;
			styleHit.focused.background = null;
			styleHit.normal.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.hover.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.active.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.focused.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.padding = new RectOffset(0, 0, 0, 0);
			styleHit.margin = new RectOffset(0, 0, 0, 0);

			stylesReady = true;
		}

		static GUIStyle Make(GUIStyle root, int size, TextAnchor anchor) {
			GUIStyle s = new GUIStyle(root);
			s.font = root.font;             // yerlesik Arial
			s.fontSize = size;
			s.alignment = anchor;
			s.normal.textColor = Color.white;   // renk GUI.color ile veriliyor
			s.clipping = TextClipping.Clip;
			s.wordWrap = false;
			return s;
		}

		// ------------------------------------------------------------------
		// Dokular
		// ------------------------------------------------------------------

		Texture2D pixel;
		Texture2D gradient;
		Texture2D beam;

		/// <summary>
		/// Dokular bir kez uretilir. Degrade 1x128 dikey olur; cizimde tek
		/// DrawTexture ile ekrana gerilir, boylece piksel basina is yok.
		/// </summary>
		void EnsureTextures() {
			if (pixel != null) return;

			pixel = new Texture2D(1, 1, TextureFormat.RGB24, false);
			pixel.SetPixel(0, 0, Color.white);
			pixel.Apply();
			pixel.hideFlags = HideFlags.HideAndDontSave;
			pixel.wrapMode = TextureWrapMode.Clamp;

			const int H = 128;
			gradient = new Texture2D(1, H, TextureFormat.RGB24, false);
			for (int y = 0; y < H; y++) {
				float t = y / (float)(H - 1);
				// Ust: derin lacivertimsi. Alt: neredeyse siyah. Ortada hafif sicak.
				Color top = new Color(0.055f, 0.060f, 0.085f);
				Color mid = new Color(0.085f, 0.082f, 0.080f);
				Color bot = new Color(0.030f, 0.030f, 0.038f);
				Color c = t < 0.5f
					? Color.Lerp(top, mid, t * 2f)
					: Color.Lerp(mid, bot, (t - 0.5f) * 2f);
				gradient.SetPixel(0, y, c);
			}
			gradient.Apply();
			gradient.hideFlags = HideFlags.HideAndDontSave;
			gradient.wrapMode = TextureWrapMode.Clamp;

			const int B = 64;
			beam = new Texture2D(1, B, TextureFormat.RGB24, false);
			for (int y = 0; y < B; y++) {
				float t = y / (float)(B - 1);
				beam.SetPixel(0, y, new Color(1f, 1f, 1f, Mathf.Sin(t * Mathf.PI)));
			}
			beam.Apply();
			beam.hideFlags = HideFlags.HideAndDontSave;
			beam.wrapMode = TextureWrapMode.Clamp;
		}

		// ------------------------------------------------------------------
		// Arka plan fotografi
		// ------------------------------------------------------------------

		Texture2D background;
		bool backgroundTried;

		/// <summary>Gömülü arka plan kaynağı (build.cmd'daki /resource ile eşleşmeli).</summary>
		const string EMBEDDED_BG = "SteamMP.menu_bg.jpg";

		/// <summary>
		/// Arka plan goruntusunu bulur. Önce DLL'in İÇİNDEKİ gömülü resim
		/// denenir (tek DLL dağıtımı; kurulum klasöründe dosya yok), sonra
		/// kullanıcının kendi resmi (Mods/masaüstü) onu ezer. Hiçbiri yoksa
		/// oyunun kendi art'i kalir.
		/// </summary>
		void EnsureBackground() {
			if (backgroundTried) return;
			backgroundTried = true;

			string[] names = { "menu_bg.jpg", "menu_bg.png", "mysummercar.jpg", "mysummercar.png" };
			string[] folders = { GameModsFolder(), DesktopFolder() };

			for (int f = 0; f < folders.Length && background == null; f++) {
				for (int i = 0; i < names.Length && background == null; i++) {
					string path;
					try { path = System.IO.Path.Combine(folders[f], names[i]); }
					catch { continue; }
					if (!FileExistsSafe(path)) continue;
					if (TryLoad(path)) SmpLog.Print("menu background: " + path);
				}
			}
			// Kullanıcı resmi yoksa gömülü varsayılan devreye girer.
			if (background == null && TryLoadEmbedded()) {
				SmpLog.Print("menu background: embedded (" + EMBEDDED_BG + ")");
			}
			if (background == null) {
				SmpLog.Print("no menu background found; the gradient stays visible.");
			}
		}

		/// <summary>
		/// DLL'e gömülü resmi okur. mscorlib 2.0: Stream.CopyTo yok, elle kopya.
		/// Dosya yoksa sessizce false (klasör taraması devam eder).
		/// </summary>
		bool TryLoadEmbedded() {
			if (background != null) return true;
			try {
				System.Reflection.Assembly self = GetType().Assembly;
				using (System.IO.Stream s = self.GetManifestResourceStream(EMBEDDED_BG)) {
					if (s == null) return false;
					long len = s.Length;
					if (len <= 0 || len > 8 * 1024 * 1024) return false;
					byte[] data = new byte[(int)len];
					int offset = 0;
					while (offset < data.Length) {
						int read = s.Read(data, offset, data.Length - offset);
						if (read <= 0) break;
						offset += read;
					}
					if (offset != data.Length) return false;
					return TryLoadBytes(data);
				}
			}
			catch { return false; }
		}

		static string GameModsFolder() {
			try {
				return System.IO.Path.GetDirectoryName(
					System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;
			}
			catch { return string.Empty; }
		}

		static string DesktopFolder() {
			try { return Environment.GetFolderPath(Environment.SpecialFolder.Desktop); }
			catch { return string.Empty; }
		}

		static bool FileExistsSafe(string path) {
			try { return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path); }
			catch { return false; }
		}

		bool TryLoad(string path) {
			try {
				byte[] bytes = System.IO.File.ReadAllBytes(path);
				return TryLoadBytes(bytes);
			}
			catch (Exception e) {
				SmpLog.Warning("background '" + path + "' unreadable: " + e.Message);
				background = null;
				return false;
			}
		}

		/// <summary>Ham resim baytından doku kurar (dosya + gömülü kaynak ortak yolu).</summary>
		bool TryLoadBytes(byte[] bytes) {
			try {
				if (bytes == null || bytes.Length == 0) return false;
				Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
				if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); return false; }
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.filterMode = FilterMode.Bilinear;
				background = tex;
				return true;
			}
			catch {
				background = null;
				return false;
			}
		}

		// ------------------------------------------------------------------
		// Animasyon yardimcilari
		// ------------------------------------------------------------------

		/// <summary>
		/// Yumuşatma: t*(2-t). Kare hizindan bagimsiz, ucuz, tek islem.
		/// Unity 5'te Mathf.SmoothStep de var ama bunun tek parametreli
		/// surumu yeterli ve daha okunakli.
		/// </summary>
		static float Ease(float t) {
			if (t <= 0f) return 0f;
			if (t >= 1f) return 1f;
			return t * (2f - t);
		}

		/// <summary>0..1 arasinda salinim. Nabiz icin.</summary>
		static float Pulse01() {
			return 0.5f + 0.5f * Mathf.Sin(ClockValue * 3.2f);
		}

		/// <summary>Nabiz + taban alpha: durum satiri hafifçe soluklar.</summary>
		Color Pulse(Color c) {
			Color keep = c;
			keep.a = 0.82f + 0.18f * Pulse01();
			return keep;
		}

		/// <summary>Statik saat: Tick doldurur, statik cizim icin erisim kolayligi.</summary>
		static float ClockValue;

		// ------------------------------------------------------------------
		// Kucuk araclar
		// ------------------------------------------------------------------

			}
}
