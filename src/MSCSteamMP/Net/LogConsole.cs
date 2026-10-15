using System;
using System.Collections.Generic;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Ekran konsörü: F3 ile açılır, logları gösterir ve komut ÇALIŞTIRIR.
	///
	/// Neden kendi komut satırı var
	/// --------------------------
	/// Komutlar MSCLoader'ın konsolundan (BackQuote) yazılıyordu. O ekranı
	/// kullanılmaz hâle getirdiğimiz için ortada komut yazılacak yer kalmadı ve
	/// "/npc çalışmıyor" belirtisi doğdu. Şimdi konsörün altında kendi giriş
	/// satırı var; yürütücü yine MSCLoader'ınki (ModCommands), yani tüm
	/// komutlar — hem bizimkiler hem MSCLoader'ınkiler — aynı yerden çalışıyor.
	///
	/// Çok satırlı kayıtlar
	/// --------------------
	/// Önceden her satır SABİT yükseklikte (15 piksel) çiziliyordu ve yazı tipi
	/// satır kaydırmayı açıksa uzun kayıtlar 2-3 satıra bölünüp altı kesiliyordu:
	/// kullanıcı mesajın başını görüyor, gövdesini görmüyordu.
	///
	/// Çözüm: yükseklik metinden ÖLÇÜLÜR ve kayda yazılır. Ölçüm bir kez yapılır
	/// (GUIStyle.CalcHeight), sonuç SmpLog'un halka kaydında saklanır; yani
	/// yükseklik metinle birlikte taşınır ve tampon taşıp eski kayıtlar düştüğünde
	/// yanlış yükseklikler kaymaz. Kaydırma aritmetiği (toplam yükseklik, ilk
	/// görünen kayıt) sabit yükseklik varsaymıyor.
	///
	/// 800 kaydın yüksekliklerini her karede toplamak 800 basit toplama, yani
	/// ölçüm değil; ölçüm yalnızca ilk göründüğünde bir kez yapılıyor.
	///
	/// Tuşlar
	/// ------
	///   F3 / ESC   pencereyi kapat
	///   yaz        komut satırına yazar
	///   ENTER      komutu çalıştır
	///   BACKSPACE  sil
	///   UP/DOWN    komut geçmişi
	///   TAB        komut adı tamamlama
	///   PGUP/PGDN  sayfa kaydır
	///   HOME/END   en üst / en alt
	///   fare tekerleği  kaydır
	/// </summary>
	public class LogConsole {

		/// <summary>Konsörü açan tuş. F3'ün oyunda kullanılmadığı doğrulandı.</summary>
		public const KeyCode TOGGLE = KeyCode.F3;

		/// <summary>
		/// EKRAN KONSOLÜ KAPALI.
		///
		/// Kullanıcı isteği: F3 log konsolu ve ana menüdeki komut satırı
		/// kaldırıldı. Konsol kodu burada duruyor çünkü `smp log ...`
		/// komutları hâlâ onu kullanıyor ve yeniden yazmak gerekmiyor;
		/// yalnızca EKRANDAN açılması kapatıldı.
		///
		/// Not: `MenuController` konsolü açıkken ESC'yi menüye döndürmez;
		/// bu yüzden konsol bir kez açılırsa ESC dışında çıkmak zor
		/// olurdu. Kapalı olması bu tuzakları da kaldırıyor.
		///
		/// `static readonly`, `const` DEĞİL: const olsaydı derleyici
		/// değeri katlayıp metodun geri kalanını "unreachable" ilan ediyor
		/// ve Tick'in gövdesi uyarı üretiyordu. Davranış aynı, uyarı yok.
		/// </summary>
		public static readonly bool ENABLED = false;

		const int FONT_SIZE = 13;
		const int STAMP_SIZE = 11;
		const float PAD = 10f;
		const float HEADER_H = 28f;
		const float FOOTER_H = 22f;
		const float INPUT_H = 26f;
		const float FILTER_H = 20f;
		const float SCROLLBAR_W = 8f;

		/// <summary>Sütun genişlikleri. Metin alanı bundan sonra başlar.</summary>
		const float STAMP_W = 58f;
		const float BADGE_W = 46f;
		const float CHAN_W = 54f;
		const float TEXT_X = STAMP_W + 4f + BADGE_W + 4f + CHAN_W + 4f;

		/// <summary>Filtre satırını açan tuş. F4: oyunda kullanılmadığı doğrulandı.</summary>
		public const KeyCode FILTER_TOGGLE = KeyCode.F4;

		/// <summary>Ölçülmemiş kayıtlar için en az yükseklik (tek satır).</summary>
		float minRowHeight = FONT_SIZE + 4f;

		bool visible;
		float scroll;
		bool tail = true;
		int lastSerial;

		// ------------------------------------------------------------------
		// Filtre
		// ------------------------------------------------------------------
		//
		// 800 satırlık bir tampon içinde arama yapmanın tek yolu kaydırmak.
		// Canlı log kanıtı: "hata nerede?" sorusu 5 dakika kaydırmayla
		// cevaplanıyordu. Filtre kutusu metin, seviye ve kanalı birlikte
		// uygular ve kaç kaydın tutulduğunu başlıkta yazar.
		//
		// Filtreleme indeks TABANLIDIR: gövde sırası korunur, eşleşmeyen
		// kayıtlar atlanır. Böylece "hataları görünce aralarındaki bağlamı
		// kaybetmezsin", kullanıcı filtreyi kapatınca yerine geri döner.

		string filterText = string.Empty;
		bool filterErrorsOnly;
		LogSeverity filterMinLevel = LogSeverity.Off;
		string filterChannel = string.Empty;
		/// <summary>Filtre uygulanıyor mu (metin/seviye/kanal)?</summary>
		bool Filtering {
			get {
				return !string.IsNullOrEmpty(filterText) || filterErrorsOnly
					|| filterMinLevel != LogSeverity.Off || !string.IsNullOrEmpty(filterChannel);
			}
		}

		// Komut satırı
		string input = string.Empty;
		static readonly GUIContent InputContent = new GUIContent(string.Empty);
		readonly List<string> history = new List<string>();
		int historyIndex = -1;      // -1 = geçmişin dışında, serbest yazım
		float caretPhase;

		/// <summary>TextField kontrol adi (odak icin). Sabit, cakisma yok.</summary>
		const string InputControlName = "SmpConsoleInput";
		bool inputFocused;

		/// <summary>Kaydirma cubugu surukleniyor mu? Surukleme bitmeden kuyruk (tail) takip etmez.</summary>
		bool scrollDragging;
		float dragGrabOffset;

		/// <summary>F3 bir onceki karede basiliydi mi? Kenar algilama icin.</summary>
		bool f3WasDown;
		/// <summary>Toggle isteginin son islendigi kare. Cift islemeyi onler.</summary>
		int lastToggleFrame = -1;
		/// <summary>ReadKeys istisnasi bir kez loglandi mi? (her kare yazmamak icin)</summary>
		bool keyReadFailed;

		/// <summary>Filtre alanı odakta mı? Odaktayken yazılan her şey filtreye gider.</summary>
		bool filterFocused;
		/// <summary>Filtre alanının Unity kontrol adı (odak takibi için).</summary>
		const string FilterControlName = "SmpConsoleFilter";
		/// <summary>F4 ile açıldı mı (kapatınca filtre uygulanmaya devam eder mi)?</summary>
		bool filterBarVisible = true;
		/// <summary>
		/// ENTER'den sonra metin alanini temizlemek icin 1 kare bekle.
		/// TextField'in ic TextEditor tamponu odak birakilinca sifirlanir;
		/// hemen ayni karede yazmak eski metni geri getirir.
		/// </summary>
		bool clearNextFrame;
		/// <summary>Cizim istisnasi bir kez loglandi mi?</summary>
		bool drawFailed;
		/// <summary>
		/// Pencere sürükleme ofseti, EKRANIN ORANINA GÖRE (-0.5..0.5).
		/// Mutlak piksel değil: 2560x1440'te 300px ile 1280x720'de 300px
		/// farklı yerde durur ve pencere ekran dışına taşabilir.
		/// </summary>
		static Vector2 windowOffset;
		/// <summary>Sürükleme sırasında fare ile pencere arasındaki fark (piksel).</summary>
		static Vector2 dragGrab;
		static bool draggingWindow;

		/// <summary>
		/// Input.GetKey'in guvenli sarmalayicisi.
		///
		/// Unity 5'te Input.GetKey normalde istisna atmaz, ancak pencere
		/// odagi kayboldugunde bazi suruculerde false doner. Burada yalnizca
		/// false donerse "basili degil" yorumlanir; istisna olursa da
		/// konsol acilir kapandinmaz.
		/// </summary>
		static bool SafeKey(KeyCode key) {
			try { return Input.GetKey(key); }
			catch { return false; }
		}

		Texture2D pixel;
		GUIStyle stampStyle, infoStyle, stateStyle, warnStyle, errorStyle,
			chanStyle, headerStyle, footerStyle, inputStyle, emptyStyle;
		GUISkin skinBefore;
		bool stylesReady;

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		/// <summary>Pencere açık mı?</summary>
		public bool Visible { get { return visible; } }

		/// <summary>
		/// Açıkken oyunun klavye girdisi durmalı mı?
		///
		/// Menü denetleyicisi bunu okuyup şifre kutusuna tuş geçirmez. Konsör
		/// açıkken zaten klavyeyi o yazıyor; iki yazma hedefi olursa hangisine
		/// gittiği belirsizleşiyor.
		/// </summary>
		public bool BlocksInput { get { return visible; } }

		public void Open() { if (!visible) Toggle(); }
		public void Close() { if (visible) Toggle(); }

		public void Toggle() {
			visible = !visible;
			if (visible) {
				tail = true;
				lastSerial = SmpLog.Total;
				caretPhase = 0f;
				inputFocused = false;
				scrollDragging = false;
				ShowCursor();
				SmpLog.Print("log console open (F3 to close). Commands: type and press ENTER, "
					+ "TAB completes, UP/DOWN walks the history.");
				if (!ModCommands.Available) {
					SmpLog.Warning("commands are not available yet: " + ModCommands.Unavailable);
				}
			}
			else {
				FlushInput();
				scrollDragging = false;
				draggingWindow = false;
				CameraLookGuard.SetBlocked(false);
				RestoreCursor();
				SmpLog.Print("log console closed.");
			}
		}

		/// <summary>
		/// Konsol acikken fare imleci her zaman gorunur ve serbesttir: loglar
		/// tekerlekle kaydirilir, kaydirma cubugu suruklenir, giris alanina
		/// tiklanir. Oyun surus sirasinda imleci kilitler; kapatinca iade edilir.
		/// </summary>
		static void ShowCursor() {
			try {
				Cursor.visible = true;
				Cursor.lockState = CursorLockMode.None;
			}
			catch { }
		}

		/// <summary>
		/// Kapanma: imleci duzelt. Dunya durumu MenuController'in kuralina
		/// birakilir; bu yuzden burada yalnizca menudeysek serbest birakiliyor.
		/// </summary>
		static void RestoreCursor() {
			// Kapatildiktan SONRA dunyada imleci serbest birakmak YANLIS:
			// MenuController her karede yeniden kilitler. Burada yalnizca
			// menudeysek serbest birakilir.
			//
			// Istisna: oyunun ESC menusu acikse dokunulur. Orada fare
			// kilitli kalirsa menu TIKLANAMAZ olur ve kullanici cikamiyor.
			try {
				if (MenuController.InMenu) {
					Cursor.visible = true;
					Cursor.lockState = CursorLockMode.None;
				}
				else if (GameMenuGuard.GameMenuOpen) {
					Cursor.visible = true;
					Cursor.lockState = CursorLockMode.None;
				}
			}
			catch { }
		}

		// ------------------------------------------------------------------
		// Kare başına girdi
		// ------------------------------------------------------------------

		/// <summary>
		/// Tuşlar ve kuyruk takibi.
		///
		/// Update'ten çağrılır, OnGUI'den değil: OnGUI kare başına iki kez
		/// (Layout + Repaint) çalışır ve orada tuş okumak işi iki kez yapmak
		/// demektir.
		/// </summary>
		public void Tick(float dt) {
			// F3 iki yoldan dinlenir ve KARE KORUMASIYLA bir kez islenir:
			//   1) burada Input.GetKey (kenar algilama) - Unity polling
			//   2) Draw() icinde Event.current - IMGUI olayi
			// Ikisi de ayni karede gelirse Toggle() iki kez calisip
			// konsolu tekrar kapatirdi; lastToggleFrame bunu engeller.
			// ONEMLI: bu blok istisna birakmaz. Asagidaki ReadKeys bir
			// noktada patlarsa bile F3 bir sonraki karede yine calisir.
			// (output_log.txt kaniti: HeightOf istisnasi Update'i yarida
			// kesiyordu ve konsol kapanmaz hale geliyordu.)
			// F3 dinlemesi ve tum konsol isi kaldirildi (ENABLED = false).
			// Burada degisikligi tek bir bayrakta topluyoruz: ekrandan
			// acma yolu kapali, `smp log ...` bilgilendirici komutlari
			// dosyaya yazmaya devam ediyor.
			if (ENABLED) {
				try {
					bool f3Down = SafeKey(TOGGLE);
					if (f3Down && !f3WasDown) RequestToggle();
					f3WasDown = f3Down;
				}
				catch { }
			}
			else {
				// Konsol hicbir yoldan acik kalamaz: F3 kapali oldugu icin
				// kapatmak icin de tus yok, acik kalirsa ekranda asili kalirdi.
				f3WasDown = false;
				if (visible) visible = false;
				try { CameraLookGuard.SetBlocked(false); } catch { }
				return;
			}

			try {
				bool f3Down = SafeKey(TOGGLE);
				if (f3Down && !f3WasDown) RequestToggle();
				f3WasDown = f3Down;
			}
			catch { }

			if (!visible) {
				try { CameraLookGuard.SetBlocked(false); } catch { }
				return;
			}

			// Oyunun fare bakisini durdur.
			//
			// dnSpy kaniti: SimpleSmoothMouseLook.Update her karede
			// Screen.lockCursor'i ZORLUYOR ve GetAxisRaw("Mouse X") okuyor.
			// Yani imleci serbest birakmak (CursorLockMode.None) ise yaramaz;
			// kamera fare hareketiyle donmeye devam ediyor. Dogru cozum
			// CameraLookGuard: konsol acikken o Update'ler atlanir.
			try { CameraLookGuard.SetBlocked(true); } catch { }

			// Tus okuma HICBIR ZAMAN istisna disari tasirmaz. Onceden
			// ReadKeys -> MaxScroll -> HeightOf zinciri null GUIStyle'a
			// dokunuyordu (stiller sadece OnGUI'de olusuyor) ve istisna
			// SteamMPBehaviour.Update'in gerisini kesiyordu: menu oluyor,
			// konsol kapanmiyordu.
			try { ReadKeys(); }
			catch (Exception e) {
				// Bir kez yaz; sonra sus (her kare ayni hata olur).
				if (!keyReadFailed) {
					keyReadFailed = true;
					SmpLog.Warning("console key handling failed: "
						+ e.GetType().Name + ": " + e.Message);
				}
			}

			// Imlec yanip sonmesi.
			caretPhase += dt;
			if (caretPhase >= 1f) caretPhase -= 1f;

			int total = SmpLog.Total;
			if (total == lastSerial) return;
			lastSerial = total;

			// Kuyruk: yeni satir geldiyse en alta in. Kullanici zaten en altta
			// degilse takip etmiyoruz, yoksa yukarida okumak imkansiz olurdu.
			try { if (tail) scroll = MaxScroll(); } catch { }
		}

		/// <summary>
		/// Ac/kapa istegini kare basina EN FAZLA bir kez uygular.
		/// Hem Input.GetKey (Update) hem Event.current (OnGUI) yolundan
		/// gelen istekler cift sayilmasin diye.
		/// </summary>
		public void RequestToggle() {
			int frame = Time.frameCount;
			if (frame == lastToggleFrame) return;
			lastToggleFrame = frame;
			try { Toggle(); }
			catch (Exception e) {
				SmpLog.Warning("console toggle failed: "
					+ e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>OnGUI'den (Event.current) F3 yakalama; cagiran Use() yapar.</summary>
		public bool WantsToggleFromEvent(Event e) {
			if (e == null) return false;
			if (e.type != EventType.KeyDown) return false;
			if (e.keyCode != TOGGLE) return false;
			return true;
		}

		/// <summary>
		/// Tuslar: yalnizca ozel tuslar. METIN TextField'indir, tek yazici o.
		///
		/// Cift-yazi bug'i: onceki surum hem GUI.TextField ciziyor hem de
		/// Input.inputString anketiyle ayni harfi tekrar ekliyordu
		/// ("npc" -> "nnppcc"); Backspace de iki kez siliyordu. Kural: yazi
		/// yalnizca TextField'dan gelir (DrawInput), burada harf dokunulmaz.
		/// ENTER yedegi zararsizdir (ikinci cagri bos satirla doner).
		/// </summary>
		void ReadKeys() {
			if (Input.GetKeyDown(KeyCode.Escape)) {
				// Filtre alanı odaktaysa önce filtreyi bırak: konsörü kapatmak
				// için iki kez ESC basmak zorunda olmamak.
				if (filterFocused) { filterFocused = false; try { GUI.FocusControl(string.Empty); } catch { } return; }
				Close(); return;
			}

			// Filtre kısayolları. Metin alanı odaktayken bunlar YAZI olarak
			// geçmeli, yani odak kontrolü buradan ÖNCE yapılır.
			if (!filterFocused) {
				if (Input.GetKeyDown(FILTER_TOGGLE) || Input.GetKeyDown(KeyCode.Slash)) {
					filterFocused = true;
					try { GUI.FocusControl(FilterControlName); } catch { }
					return;
				}
				if (Input.GetKeyDown(KeyCode.E)) { filterErrorsOnly = !filterErrorsOnly; scroll = MaxScroll(); return; }
				if (Input.GetKeyDown(KeyCode.C)) { CycleChannel(); return; }
			}

			if (filterFocused) {
				// Filtre odaktayken yalnızca kaydırma geçerli; yazım TextField'da.
			}
			else {
				if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) {
					RunTyped();
					return;
				}
				if (Input.GetKeyDown(KeyCode.UpArrow)) { WalkHistory(-1); return; }
				if (Input.GetKeyDown(KeyCode.DownArrow)) { WalkHistory(1); return; }
				if (Input.GetKeyDown(KeyCode.Tab)) { Complete(); return; }
			}

			// Kaydırma: ok tuşları geçmişe ayrıldı, sayfa tuşları kaldı.
			float page = VisibleRowsOnScreen() * minRowHeight;
			float max = MaxScroll();
			if (Input.GetKeyDown(KeyCode.PageDown)) scroll += page;
			if (Input.GetKeyDown(KeyCode.PageUp)) scroll -= page;
			if (Input.GetKeyDown(KeyCode.Home)) scroll = 0f;
			if (Input.GetKeyDown(KeyCode.End)) scroll = max;
			if (scroll < 0f) scroll = 0f;
			if (scroll > max) scroll = max;
			tail = scroll >= max - 1f;
		}

		void RunTyped() {
			string line = input == null ? string.Empty : input.Trim();

			// METIN ALANI HER ZAMAN TEMIZLENIR (kullanici istegi).
			// clearNextFrame, DrawInput'in TextEditor tamponunu sifirlamasini
			// bekler; aksi halde Unity eski metni geri yaziyor.
			input = string.Empty;
			inputFocused = false;
			historyIndex = -1;
			clearNextFrame = true;

			if (line.Length == 0) return;

			if (history.Count == 0 || history[history.Count - 1] != line) {
				history.Add(line);
				if (history.Count > 50) history.RemoveAt(0);
			}

			SmpLog.Print("> " + line);

			// Yurutucu: once bizim ayristirici (/npc), sonra MSCLoader.
			// Sonuc KULLANICIYA GORUNUR sekilde yazilir; onceki surumde
			// calisip calismadigi belirsizdi ("calisiyor mu bilmiyorum").
			string result;
			try {
				result = SmpCommandParser.Execute(line);
			}
			catch (Exception e) {
				result = "ERROR: " + e.GetType().Name + ": " + e.Message;
				SmpLog.Error("console execute failed: " + result);
			}
			if (!string.IsNullOrEmpty(result)) SmpLog.Print("  " + result);
		}

		void FlushInput() {
			input = string.Empty;
			historyIndex = -1;
		}

		void WalkHistory(int delta) {
			if (history.Count == 0) return;
			if (historyIndex == -1) {
				if (delta > 0) return;               // aşağı: zaten en yenide
				historyIndex = history.Count - 1;
			}
			else {
				int next = historyIndex + delta;
				if (next < 0) next = 0;
				if (next >= history.Count) {          // geçmişin altına indik
					historyIndex = -1;
					input = string.Empty;
					return;
				}
				historyIndex = next;
			}
			input = history[historyIndex];
		}

		/// <summary>
		/// TAB tamamlama: yazılan ön eke uyan ilk komut adı.
		///
		/// Komut listesi MSCLoader'ın kendi kaydından okunur (ModCommands), yani
		/// MSCLoader'ın komutları da tamamlanır.
		/// </summary>
		void Complete() {
			string[] names = ModCommands.KnownCommands();
			if (names.Length == 0) {
				SmpLog.Warning("no command list available yet.");
				return;
			}

			string word = CurrentWord();
			string hit = null;
			int hits = 0;
			for (int i = 0; i < names.Length; i++) {
				if (!names[i].StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
				hits++;
				if (hit == null) hit = names[i];
			}

			if (hits == 0) return;

			if (hits == 1) {
				ReplaceCurrentWord(hit + " ");
				return;
			}

			// Birden fazla eşleşme: hepsini göster, yazmayı bozma.
			SmpLog.Print(hits + " commands start with '" + word + "': " + string.Join(" ", names));
		}

		/// <summary>İmlecin solundaki kelime (yalnızca harf/rakam içeren).</summary>
		string CurrentWord() {
			int end = input.Length;
			int start = end;
			while (start > 0 && IsWordChar(input[start - 1])) start--;
			return input.Substring(start, end - start);
		}

		void ReplaceCurrentWord(string replacement) {
			int end = input.Length;
			int start = end;
			while (start > 0 && IsWordChar(input[start - 1])) start--;
			input = input.Substring(0, start) + replacement;
		}

		static bool IsWordChar(char c) {
			return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
		}

		// ------------------------------------------------------------------
		// Ölçüler
		// ------------------------------------------------------------------

		static Rect WindowRect() {
			float w = Mathf.Min(Screen.width * 0.88f, 1240f);
			float h = Mathf.Min(Screen.height * 0.78f, 700f);
			// Sürükleme: pencerenin sol-üst köşesi, 0..1 oranında saklanır ki
			// çözünürlük değişince de aynı yere otursun (ekran kenarlarına
			// yapışmasın diye oran 0..1 aralığına kelepçelenir).
			float x = (Screen.width - w) * 0.5f + windowOffset.x * Screen.width;
			float y = (Screen.height - h) * 0.5f + windowOffset.y * Screen.height;
			return new Rect(x, y, w, h);
		}

		/// <summary>
		/// Başlık çubuğu: sürükleme tutamağı. Fare basılı tutup
		/// taşındığında pencereyi taşır.
		/// </summary>
		static Rect TitleBar(Rect win) {
			return new Rect(win.x, win.y, win.width, 5f + HEADER_H);
		}

		static Rect BodyRect(Rect win) {
			float top = win.y + 5f + HEADER_H + 4f + FILTER_H + 4f;
			float bottom = win.yMax - FOOTER_H - INPUT_H - PAD;
			return new Rect(win.x + PAD, top, win.width - PAD * 2f - SCROLLBAR_W, bottom - top);
		}

		/// <summary>Filtre satırının dikdörtgeni (komut satırının hemen üstü).</summary>
		static Rect FilterRect(Rect win) {
			float y = win.yMax - FOOTER_H - INPUT_H - FILTER_H - PAD * 0.5f;
			return new Rect(win.x + PAD, y, win.width - PAD * 2f, FILTER_H);
		}

		float VisibleRowsOnScreen() {
			return BodyRect(WindowRect()).height / minRowHeight;
		}

		/// <summary>
		/// Bir kaydın yüksekliği, gerekirse şimdi ölçüp kayda yazıyor.
		///
		/// Ölçüm GUIStyle.CalcHeight ile yapılır ve bir kez saklanır; her kare
		/// yeniden ölçmek 80 satırlık bir Harmony hata zinciri için kare başına
		/// 800 sarma hesabı demek.
		///
		/// ÖNEMLİ (output_log.txt kanıtı): bu metot Update'ten de çağrılıyor
		/// (ReadKeys -> MaxScroll), ama GUIStyle alanları YALNIZCA OnGUI'de,
		/// yani Draw() içindeki EnsureStyles() ile oluşuyor. Konsol kapalıyken
		/// hiç Draw() çağrılmadığı için stiller null'dı ve CalcHeight
		/// NullReferenceException atıyordu. İstisna Update'in gerisini kesiyor,
		/// MenuController.Tick hiç çalışmıyor ve konsol kapanmıyordu.
		///
		/// Çözüm: stil null ise ÖLÇÜM YAPILMAZ, tek satır yüksekliği döner.
		/// Böylece Update güvenli olur; gerçek ölçüm yine Draw'da olur.
		/// </summary>
		float HeightOf(int index) {
			float h = SmpLog.GetHeight(index);
			if (h > 0f) return h;

			GUIStyle style = StyleForStored();
			if (style == null) return minRowHeight;      // OnGUI disi: olcume calisma

			string stamp, text;
			LogKind kind;
			SmpLog.GetLine(index, out stamp, out kind, out text);

			Rect body = BodyRect(WindowRect());
			h = style.CalcHeight(new GUIContent(text), body.width - TEXT_X);
			if (h < minRowHeight) h = minRowHeight;
			SmpLog.SetHeight(index, h);
			return h;
		}

		/// <summary>Ölçüm için kullanılacak stil; henüz oluşmadıysa null.</summary>
		GUIStyle StyleForStored() {
			return infoStyle;
		}

		/// <summary>
		/// Filtre uygulanmış kayıt SAYISI (gövdede çizilecek satır).
		/// </summary>
		int VisibleRowCount() {
			int count = SmpLog.LineCount;
			if (!Filtering) return count;
			int n = 0;
			for (int i = 0; i < count; i++) if (Matches(i)) n++;
			return n;
		}

		/// <summary>Bu ham kayıt indeksi filtreyi geçiyor mu?</summary>
		bool Matches(int rawIndex) {
			if (!Filtering) return true;
			LogRecord rec;
			if (!SmpLog.TryGetRecord(rawIndex, out rec)) return false;
			if (filterErrorsOnly && !LogSeverities.IsNoteworthy(rec.Level)) return false;
			if (filterMinLevel != LogSeverity.Off && !LogSeverities.AtMost(rec.Level, filterMinLevel)) return false;
			if (!string.IsNullOrEmpty(filterChannel)
				&& !string.Equals(rec.ChannelOrDefault(), filterChannel, StringComparison.OrdinalIgnoreCase)
				&& rec.ChannelOrDefault().IndexOf(filterChannel, StringComparison.OrdinalIgnoreCase) < 0) return false;
			if (!string.IsNullOrEmpty(filterText) && !rec.Matches(filterText)) return false;
			return true;
		}

		/// <summary>
		/// Filtreli sıradaki görünür kaydın ham indeksi.
		/// </summary>
		int RawIndexAt(int visibleIndex) {
			int count = SmpLog.LineCount;
			int seen = 0;
			for (int i = 0; i < count; i++) {
				if (!Matches(i)) continue;
				if (seen == visibleIndex) return i;
				seen++;
			}
			return -1;
		}

		float MaxScroll() {
			int count = VisibleRowCount();
			if (count <= 0) return 0f;
			float total = 0f;
			for (int i = 0; i < count; i++) {
				int raw = RawIndexAt(i);
				if (raw < 0) break;
				total += HeightOf(raw);
			}
			float bodyH = BodyRect(WindowRect()).height;
			float max = total - bodyH;
			return max > 0f ? max : 0f;
		}

		// ------------------------------------------------------------------
		// Çizim
		// ------------------------------------------------------------------

		public void Draw() {
			// Konsör kapatildi (ENABLED = false). Fare yonlendirmesi icin
			// dikdortgen temizlenir, boylece menu cizilmeyen bir panel icin
			// yer ayirmaz. Bayrak bir gun geri acilirsa normal yol ve istisna
			// korumasi calisir: bayrak, ReadKeys ve Tick'in de kullandigi tek
			// anahtar olarak kalir.
			//
			// ONCEKI HALI: `MenuMouse.SetConsoleRect(...); return;` kosulsuz
			// duruyordu. Uygulama davranisi ayniydi ama ENABLED = true
			// yapmak konsolu HIC cizmiyordu (gizli bir tuzak) ve geri kalan
			// her sey "unreachable" uyarisi veriyordu -- `static readonly`
			// seciminin (bkz. ENABLED belgesi) bastirmak icin secildigi tam
			// olarak o uyari.
			if (!ENABLED) { MenuMouse.SetConsoleRect(new Rect(0f, 0f, 0f, 0f), false); return; }

			// Cizim de istisna guvenlidir: bir hata olsa bile OnGUI'nin
			// gerisi (menu paneli) calisir ve konsol bir sonraki karede
			// toparlanir.
			try { DrawUnsafe(); }
			catch (Exception e) {
				if (!drawFailed) {
					drawFailed = true;
					SmpLog.Warning("console draw failed: "
						+ e.GetType().Name + ": " + e.Message);
				}
			}
		}

		/// <summary>
		/// Başlık çubuğundan pencereyi sürükle.
		///
		/// Fare basılıyken MouseDrag olayında pencereyi taşır. Olay
		/// tüketilir ki oyun aynı sürüklemeyi görmesin. Bırakınca ofset
		/// ekranın %50'sine kelepçelenir: pencere hiçbir çözünürlükte
		/// tamamen ekran dışına kayamaz.
		/// </summary>
		void DrawDrag() {
			try {
				Event e = Event.current;
				if (e == null) return;

				Rect win = WindowRect();
				try { MenuMouse.SetConsoleRect(win, true); } catch { }
				Rect bar = TitleBar(win);

				if (e.type == EventType.MouseDown && e.button == 0 && bar.Contains(e.mousePosition) && !MenuMouse.EventInChatRect(e.mousePosition)) {
					draggingWindow = true;
					dragGrab = e.mousePosition - new Vector2(win.x, win.y);
					try { e.Use(); } catch { }
				}
				else if (e.type == EventType.MouseDrag && draggingWindow && e.button == 0) {
					// ÖNEMLİ: windowOffset, pencerenin MERKEZE göre
					// kaymasıdır (WindowRect merkezi konuma ofset ekler).
					// Önceki sürüm mutlak fare x'ini ofset sanıyordu; sonuç
					// pencere imlecin ALTINDA değil, tamamen başka yerde
					// duruyordu ("farklı yerden sürüklüyor").
					// Doğrusu: fare ile pencerenin SOL-ÜST köşesi arasındaki
					// FARKI, o da merkeze göre ofset olmalı.
					float dx = e.mousePosition.x - dragGrab.x - win.x;
					float dy = e.mousePosition.y - dragGrab.y - win.y;
					float maxX = Screen.width * 0.5f;
					float maxY = Screen.height * 0.5f;
					if (dx < -maxX) dx = -maxX;
					if (dy < -maxY) dy = -maxY;
					if (dx > maxX) dx = maxX;
					if (dy > maxY) dy = maxY;
					windowOffset = new Vector2(
						Screen.width > 0 ? dx / Screen.width : 0f,
						Screen.height > 0 ? dy / Screen.height : 0f);
					try { e.Use(); } catch { }
				}
				else if (e.type == EventType.MouseUp && draggingWindow && e.button == 0) {
					draggingWindow = false;
					try { e.Use(); } catch { }
				}
			}
			catch { draggingWindow = false; }
		}

		void DrawUnsafe() {
			EnsureStyles();
			EnsurePixel();

			Rect win = WindowRect();
			try { MenuMouse.SetConsoleRect(win, true); } catch { }

			Fill(new Color(0f, 0f, 0f, 0.94f), win);
			Fill(new Color(1f, 0.82f, 0.25f, 0.9f), new Rect(win.x, win.y, win.width, 2f));
			Fill(new Color(1f, 1f, 1f, 0.10f), new Rect(win.x + 1f, win.y + 2f, win.width - 2f, 1f));

			Rect header = new Rect(win.x + PAD, win.y + 5f, win.width - PAD * 2f, HEADER_H);
			GUI.Label(header, HeaderText(), headerStyle);
			Fill(new Color(1f, 1f, 1f, 0.10f), new Rect(win.x + PAD, header.yMax, win.width - PAD * 2f, 1f));

			// Başlık çubuğunu sürükle.
			DrawDrag();

			Rect footer = new Rect(win.x + PAD, win.yMax - FOOTER_H - INPUT_H - 2f,
				win.width - PAD * 2f, FOOTER_H);
			Fill(new Color(1f, 1f, 1f, 0.10f), new Rect(win.x + PAD, footer.y, win.width - PAD * 2f, 1f));
			GUI.Label(footer, FooterText(), footerStyle);

			// Fare tekerlegi: imlec pencere ustundeyse loglari kaydir.
			// OnGUI baglaminda Event.current gecerlidir. Oyun tekerlegi
			// gormesin diye olay tuketilir.
			try {
				Event wheel = Event.current;
				if (wheel != null && wheel.type == EventType.ScrollWheel && win.Contains(wheel.mousePosition) && !MenuMouse.EventInChatRect(wheel.mousePosition)) {
					float max = MaxScroll();
					scroll += wheel.delta.y * 24f;
					if (scroll < 0f) scroll = 0f;
					if (scroll > max) scroll = max;
					tail = scroll >= max - 1f;
					try { wheel.Use(); } catch { }
				}
			}
			catch { }

			DrawBody(win, BodyRect(win));
			DrawFilter(win);
			DrawInput(win);
		}

		/// <summary>
		/// Log gövdesi. Kayıtlar DEĞİŞKEN yükseklikte çizilir.
		/// </summary>
		void DrawBody(Rect win, Rect body) {
			int total = SmpLog.LineCount;
			int count = VisibleRowCount();

			GUILayout.BeginArea(body);
			if (total == 0) {
				GUI.Label(new Rect(0f, 4f, body.width, 20f), "(no log lines yet)", emptyStyle);
			}
			else if (count == 0) {
				GUI.Label(new Rect(0f, 4f, body.width, 20f),
					Filtering ? "(nothing matches the filter - F4 clears it)"
					          : "(no log lines yet)", emptyStyle);
			}
			else {
				float y = -scroll;
				for (int i = 0; i < count; i++) {
					int raw = RawIndexAt(i);
					if (raw < 0) break;
					float h = HeightOf(raw);

					// Görünür alanı atlayan kayıtlar çizilmiyor. Bu, sabit
					// yükseklik varsayımına göre "ilk satır" hesabından daha
					// basit ve değişken yükseklikte doğru.
					if (y + h >= 0f && y <= body.height) DrawRow(body, raw, y, h);
					y += h;
				}
			}
			GUILayout.EndArea();

			DrawScrollbar(win, body);
		}

		void DrawRow(Rect body, int index, float y, float height) {
			LogRecord rec;
			if (!SmpLog.TryGetRecord(index, out rec)) return;
			string stamp = LogRecord.TimeOf(rec.Since);

			// Dikkat çeken kayıtların arkasına çok soluk zemin. Metinden ÖNCE
			// çiziliyor: sonra çizilirse satırı okunamaz hâle getiriyor.
			if (LogSeverities.IsNoteworthy(rec.Level)) {
				Fill(new Color(1f, 0.4f, 0.2f, 0.10f), new Rect(0f, y, body.width, height));
			}
			// Karşı taraftan gelen satırlar ayrı bir tonda: aynalama açıkken
			// iki tarafın logu tek dosyada durur, hangisinin yerel olduğu
			// bir bakışta belli olmalı.
			else if (rec.Channel != null
				&& rec.Channel.StartsWith(LogChannels.Peer, StringComparison.Ordinal)) {
				Fill(new Color(0.30f, 0.60f, 1f, 0.10f), new Rect(0f, y, body.width, height));
			}

			// Damga, rozet ve kanal TEK satır; asıl metin kendi yüksekliği kadar alan alır.
			GUI.Label(new Rect(0f, y, STAMP_W, minRowHeight), stamp, stampStyle);
			GUI.Label(new Rect(STAMP_W + 4f, y, BADGE_W, minRowHeight),
				SmpLog.LevelName(rec.Kind), StyleFor(rec.Level));
			GUI.Label(new Rect(STAMP_W + BADGE_W + 8f, y, CHAN_W, minRowHeight),
				rec.ChannelOrDefault(), chanStyle);
			GUI.Label(new Rect(TEXT_X, y, body.width - TEXT_X, height),
				rec.FormatConsole(), StyleFor(rec.Level));
		}

		/// <summary>
		/// Kendi kaydırma çubuğumuz: tekerlek + surukleme ile calisir.
		///
		/// GUI.BeginScrollView kullanmıyoruz çünkü o tekerleği kendi de ele
		/// alıyor; elle de ele alırsak aynı olay iki kez işlenir ve kaydırma iki
		/// kat hızlı olur.
		/// </summary>
		void DrawScrollbar(Rect win, Rect body) {
			int count = VisibleRowCount();
			if (count <= 0) { scrollDragging = false; return; }

			float total = 0f;
			for (int i = 0; i < count; i++) {
				int raw = RawIndexAt(i);
				if (raw < 0) break;
				total += HeightOf(raw);
			}
			float maxScroll = total - body.height;
			if (maxScroll <= 0f) { scroll = 0f; tail = true; scrollDragging = false; return; }

			// Baştaki çubuk yüksekliği gövdeyle aynı oranda olmalı, yoksa
			// "ne kadar aşağı var" bilgisi yanlış olur.
			float thumbH = Mathf.Max(24f, body.height * (body.height / total));
			float travel = body.height - thumbH;
			float t = scroll / maxScroll;

			float x = body.xMax + 2f;
			Rect track = new Rect(x, body.y, SCROLLBAR_W - 2f, body.height);
			Rect thumb = new Rect(x, body.y + travel * t, SCROLLBAR_W - 2f, thumbH);
			Fill(new Color(1f, 1f, 1f, 0.06f), track);
			Fill(new Color(1f, 0.82f, 0.25f, 0.75f), thumb);

			// Surukleme: basili tut, asagi-yukari gezdir. Olaylar OnGUI
			// baglaminda gelir (Draw), Tick'te degil.
			try {
				Event e = Event.current;
				if (e == null) return;
				if (e.type == EventType.MouseDown && e.button == 0 && track.Contains(e.mousePosition)) {
					scrollDragging = true;
					float grab = e.mousePosition.y - thumb.yMin;
					if (grab < 0f) grab = 0f;
					if (grab > thumbH) grab = thumbH;
					dragGrabOffset = grab;
					if (!thumb.Contains(e.mousePosition)) {
						// Piste tiklama: cubugu oraya ziplat.
						float nt = (e.mousePosition.y - track.yMin - thumbH * 0.5f) / travel;
						if (nt < 0f) nt = 0f;
						if (nt > 1f) nt = 1f;
						scroll = nt * maxScroll;
						dragGrabOffset = thumbH * 0.5f;
					}
					try { e.Use(); } catch { }
				}
				else if (e.type == EventType.MouseDrag && scrollDragging && e.button == 0) {
					float nt2 = (e.mousePosition.y - track.yMin - dragGrabOffset) / travel;
					if (nt2 < 0f) nt2 = 0f;
					if (nt2 > 1f) nt2 = 1f;
					scroll = nt2 * maxScroll;
					try { e.Use(); } catch { }
				}
				else if (e.type == EventType.MouseUp && scrollDragging && e.button == 0) {
					scrollDragging = false;
				}
			}
			catch { scrollDragging = false; }

			if (scroll < 0f) scroll = 0f;
			if (scroll > maxScroll) scroll = maxScroll;
			tail = scroll >= maxScroll - 1f;
		}

		// ------------------------------------------------------------------
		// Filtre satırı
		// ------------------------------------------------------------------

		/// <summary>
		/// Filtre satırı: metin araması, seviye eşiği ve kanal.
		///
		/// Neden ayrı bir satır: 800 kayıt içinde "hata nerede" sorusunu
		/// kaydırmakla cevaplamak dakikalar sürüyordu. Arama kutusu tek tuşla
		/// (F4) odak alır; ENTER uygular, ESC temizler.
		///
		/// Kısayollar filtre alanı ODAKTA DEĞİLKEN de çalışır, yani konsör
		/// açıkken filtreyi değiştirmek için önce odak almak zorunlu değil:
		///   "/"  odak al / bırak
		///   "E"  yalnız hatalar
		///   "C"  kanal süzgecini sırayla gez (off -> net -> satsuma -> ...)
		/// </summary>
		void DrawFilter(Rect win) {
			Rect r = FilterRect(win);
			Fill(new Color(0.06f, 0.06f, 0.07f, 0.95f), r);
			bool active = Filtering || filterFocused;
			Fill(active ? new Color(0.30f, 0.60f, 1f, 0.85f) : new Color(1f, 1f, 1f, 0.15f),
				new Rect(r.x, r.y, 2f, r.height));

			try {
				Event cur = Event.current;

				if (filterFocused) {
					GUI.SetNextControlName(FilterControlName);
					string next = GUI.TextField(new Rect(r.x + 6f, r.y + 1f, r.width * 0.55f, r.height - 2f),
						filterText, inputStyle);
					if (next != filterText) { filterText = next; scroll = MaxScroll(); }

					if (cur != null && cur.type == EventType.KeyDown) {
						if (cur.keyCode == KeyCode.Return || cur.keyCode == KeyCode.KeypadEnter) {
							try { cur.Use(); } catch { }
							filterFocused = false;
							try { GUI.FocusControl(string.Empty); } catch { }
							tail = true; scroll = MaxScroll();
						}
						else if (cur.keyCode == KeyCode.Escape) {
							try { cur.Use(); } catch { }
							ClearFilter();
						}
					}
				}
				else {
					string summary = FilterSummary();
					GUI.Label(new Rect(r.x + 6f, r.y, r.width - 12f, r.height),
						summary, active ? headerStyle : footerStyle);
				}
			}
			catch { }
		}

		/// <summary>Filtre satırında görünen özet.</summary>
		string FilterSummary() {
			if (!Filtering) return "F4 or / : filter   (text, or E for errors only, or C to cycle the channel)";
			int shown = VisibleRowCount();
			System.Text.StringBuilder sb = new System.Text.StringBuilder(96);
			sb.Append(shown).Append('/').Append(SmpLog.LineCount).Append(" shown  ");
			if (filterErrorsOnly) sb.Append("[errors] ");
			if (filterMinLevel != LogSeverity.Off) sb.Append('[').Append(LogSeverities.Name(filterMinLevel)).Append("+] ");
			if (!string.IsNullOrEmpty(filterChannel)) sb.Append("[").Append(filterChannel).Append("] ");
			if (!string.IsNullOrEmpty(filterText)) sb.Append('"').Append(filterText).Append('"');
			sb.Append("   ESC clears");
			return sb.ToString();
		}

		/// <summary>Filtreyi sıfırlar ve gövdeye döner.</summary>
		public void ClearFilter() {
			filterText = string.Empty;
			filterErrorsOnly = false;
			filterMinLevel = LogSeverity.Off;
			filterChannel = string.Empty;
			filterFocused = false;
			scroll = MaxScroll();
		}

		/// <summary>
		/// Kanal süzgecini sırayla gezer: kapalı -&gt; net -&gt; satsuma -&gt;
		/// vehicle -&gt; player -&gt; item -&gt; world -&gt; ui -&gt; peer.
		///
		/// Elle kanal adı yazmaya gerek kalmazsın; "ağ mı araç mı" sorusu
		/// tuşla cevaplanır.
		/// </summary>
		static readonly string[] CycleChannels = {
			"", LogChannels.Net, LogChannels.Satsuma, LogChannels.Vehicle,
			LogChannels.Player, LogChannels.Item, LogChannels.World, LogChannels.Ui,
			LogChannels.Peer,
		};

		void CycleChannel() {
			int at = 0;
			for (int i = 0; i < CycleChannels.Length; i++) {
				if (string.Equals(CycleChannels[i], filterChannel, StringComparison.OrdinalIgnoreCase)) {
					at = i; break;
				}
			}
			at = (at + 1) % CycleChannels.Length;
			filterChannel = CycleChannels[at];
			scroll = MaxScroll();
		}

		/// <summary>
		/// F4: filtre alanına odaklan / odaktan çık. Dosya dışından da
		/// çağrılabilir ('smp log filter').
		/// </summary>
		public void FocusFilter() {
			filterFocused = true;
			filterBarVisible = true;
			try { GUI.FocusControl(FilterControlName); } catch { }
		}

		/// <summary>Filtre metnini ayarlar ('smp log find &lt;metin&gt;').</summary>
		public void SetFilterText(string text) {
			filterText = text ?? string.Empty;
			scroll = MaxScroll();
		}

		public void SetFilterErrors(bool only) { filterErrorsOnly = only; scroll = MaxScroll(); }
		public void SetFilterMinLevel(LogSeverity level) { filterMinLevel = level; scroll = MaxScroll(); }
		public void SetFilterChannel(string channel) { filterChannel = channel ?? string.Empty; scroll = MaxScroll(); }
		public string FilterText { get { return filterText; } }

		// ------------------------------------------------------------------
		// Komut satırı
		// ------------------------------------------------------------------

		/// <summary>
		/// Alt taraftaki komut satiri: gercek Unity giris alani (GUI.TextField).
		///
		/// ENTER SONRASI TEMIZLEME (kullanici bildirimi):
		/// Unity TextField kendi ic TextEditor tamponunu tutuyor. Disaridan
		/// input="" yazmak yetmiyor; alan odaktayken Unity bir sonraki
		/// cizimde eski metni geri yaziyor ve yazi ekranda kaliyordu.
		/// Cozum: calistirmadan once odagi BIRAK (TextEditor tamponu
		/// sifirlanir), sonra bos metni yaz ve odagi 1 sonraki cizimde
		/// geri al. "clearNextFrame" bayragi bunu yapar.
		///
		/// AYRICA F3 burada da dinlenir: IMGUI olayi polling'den daha
		/// guvenilir, cunku Unity metin alani odaktayken GetKeyDown'i
		/// yutabiliyor. Kare korumasi (RequestToggle) cift islemeyi onler.
		/// </summary>
		void DrawInput(Rect win) {
			Rect line = new Rect(win.x + PAD, win.yMax - INPUT_H - PAD * 0.5f,
				win.width - PAD * 2f, INPUT_H);

			Fill(new Color(0.06f, 0.06f, 0.07f, 0.95f), line);
			Fill(new Color(1f, 0.82f, 0.25f, 0.8f), new Rect(line.x, line.y, 2f, line.height));

			Rect field = new Rect(line.x + 6f, line.y + 2f, line.width - 12f, line.height - 4f);
			try {
				Event cur = Event.current;

				// F3: OnGUI yolu. Konsol kapaliysa hicbir sey dinlenmez.
				if (ENABLED && WantsToggleFromEvent(cur)) {
					try { cur.Use(); } catch { }
					RequestToggle();
					return;
				}

				// TAB TextField'in odak-degistirmesini engelle: tamamlamayi
				// Complete() yapar, bu tus alana asla ulasmamali.
				if (cur != null && cur.type == EventType.KeyDown && cur.keyCode == KeyCode.Tab) {
					try { cur.Use(); } catch { }
				}

				// Temizlik sonrasi odak 1 kare sonra geri alinir; TextEditor
				// tamponunun bos degeri gormesi icin bu kare odak birakilir.
				if (clearNextFrame) {
					clearNextFrame = false;
					input = string.Empty;
					try { GUI.FocusControl(string.Empty); } catch { }
				}

				GUI.SetNextControlName(InputControlName);
				string next = input;
				bool isFocused = GUI.GetNameOfFocusedControl() == InputControlName;
				if (isFocused || input.Length > 0 || inputFocused) {
					try { next = GUI.TextField(field, input, inputStyle); }
					catch { next = input; }
					if (next != input) { input = next; historyIndex = -1; }
				}
				else {
					// Odak yok, metin yok: alani hic cizmiyoruz (odaklaninca
					// cizilmeye baslayacak). Bos Label ile ipucu gosterilir.
					GUI.Label(field, EmptyInputContent, inputStyle);
				}

				// Odak teyidi: odak kacirilirsa (TAB/ESC/fare) geri al.
				if (inputFocused) {
					try {
						if (GUI.GetNameOfFocusedControl() != InputControlName)
							GUI.FocusControl(InputControlName);
					}
					catch { }
				}

				// ENTER: Once odagi birak (metni temizlemek icin), sonra
				// calistir, sonra odagi 1 kare sonra geri al.
				Event e = Event.current;
				if (e != null && e.type == EventType.KeyDown
					&& (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) {
					try { e.Use(); } catch { }
					try { GUI.FocusControl(string.Empty); } catch { }
					RunTyped();
					clearNextFrame = true;
				}
			}
			catch {
				// TextField herhangi bir sebeple cizilemezse salt-gorunur yedek.
				try {
					InputContent.text = "> " + input + (caretPhase < 0.5f ? "_" : " ");
					GUI.Label(line, InputContent, inputStyle);
				}
				catch { }
			}
		}

		static readonly GUIContent EmptyInputContent = new GUIContent("> ");

		/// <summary>
		/// Başlık: ne yazılıyor, nereye yazılıyor, kaç satır var.
		///
		/// Filtre etkinse "gösterilen/buffer" sayısı da yazılır; kullanıcı
		/// filtreyi açtığını ve kaç kayda indirdiğini görür.
		/// </summary>
		string HeaderText() {
			int shown = VisibleRowCount();
			string head = "MSCSteamMP LOG   " + shown + "/" + SmpLog.LineCount
				+ " line(s), " + LogEngine.Total + " this session";
			if (LogSeverities.IsNoteworthy(LogSeverity.Warn)) { /* stil zaten ayrı */ }
			string chan = Filtering ? FilterSummary() : FilterIdleHint();
			return head
				+ "   |   " + SmpLog.Path
				+ "   |   " + chan
				+ "   |   game: " + System.IO.Path.GetFileName(GameLogCapture.CONSOLE_FILE)
				+ " + " + System.IO.Path.GetFileName(GameLogCapture.UNITY_FILE);
		}

		string FilterIdleHint() {
			return "F4 filter, E errors, C channel, level " + LogSeverities.Name(LogChannels.Global);
		}

		string FooterText() {
			return "ENTER run   |   TAB complete   |   UP/DOWN history   |   "
				+ "wheel or PGUP/PGDN scroll   |   HOME/END jump   |   "
				+ "F4 filter   |   ESC close"
				+ "   |   mirror: " + LogRemote.Describe();
		}

		GUIStyle StyleFor(LogSeverity level) {
			if (level == LogSeverity.Warn) return warnStyle;
			if (level == LogSeverity.Fatal || level == LogSeverity.Error) return errorStyle;
			return infoStyle;
		}

		void Fill(Color c, Rect r) {
			Color keep = GUI.color;
			GUI.color = c;
			GUI.DrawTexture(r, pixel);
			GUI.color = keep;
		}

		void EnsurePixel() {
			if (pixel != null) return;
			pixel = new Texture2D(1, 1, TextureFormat.RGB24, false);
			pixel.SetPixel(0, 0, Color.white);
			pixel.Apply();
			pixel.hideFlags = HideFlags.HideAndDontSave;
		}

		void EnsureStyles() {
			if (stylesReady && skinBefore == GUI.skin) return;
			skinBefore = GUI.skin;

			GUIStyle root = GUI.skin.label;

			stampStyle = new GUIStyle(root);
			stampStyle.fontSize = STAMP_SIZE;
			stampStyle.alignment = TextAnchor.UpperLeft;
			stampStyle.normal.textColor = new Color(0.55f, 0.58f, 0.62f);
			stampStyle.wordWrap = false;
			stampStyle.clipping = TextClipping.Clip;

			// Metin stillerinde satır kaydırma AÇIK: uzun kayıtlar tek satıra
			// sıkışmaz, kendi yüksekliği kadar yer kaplar. Önceden devraldığımız
			// skin'in ayarı neyse onu zorlamıyorduk ve çok satırlı kayıtların
			// altı kesiliyordu.
			infoStyle = MakeMessage(root, new Color(0.90f, 0.92f, 0.94f));
			stateStyle = MakeMessage(root, new Color(0.55f, 0.85f, 1f));
			warnStyle = MakeMessage(root, new Color(1f, 0.85f, 0.40f));
			errorStyle = MakeMessage(root, new Color(1f, 0.50f, 0.42f));

			// Kanal sütunu: küçük ve soluk, ama satırın hangi subsystemden
			// geldiğini tek bakışta okunur kılar. Metin değil etiket olduğu
			// için kırpılır.
			chanStyle = new GUIStyle(root);
			chanStyle.fontSize = STAMP_SIZE;
			chanStyle.alignment = TextAnchor.UpperLeft;
			chanStyle.normal.textColor = new Color(0.62f, 0.66f, 0.72f);
			chanStyle.wordWrap = false;
			chanStyle.clipping = TextClipping.Clip;

			headerStyle = new GUIStyle(root);
			headerStyle.fontSize = 12;
			headerStyle.alignment = TextAnchor.MiddleLeft;
			headerStyle.normal.textColor = new Color(1f, 0.85f, 0.35f);
			headerStyle.clipping = TextClipping.Clip;
			headerStyle.wordWrap = false;

			footerStyle = new GUIStyle(root);
			footerStyle.fontSize = 11;
			footerStyle.alignment = TextAnchor.MiddleLeft;
			footerStyle.normal.textColor = new Color(0.55f, 0.60f, 0.65f);
			footerStyle.clipping = TextClipping.Clip;
			footerStyle.wordWrap = false;

			inputStyle = new GUIStyle(root);
			inputStyle.fontSize = FONT_SIZE + 1;
			inputStyle.alignment = TextAnchor.MiddleLeft;
			inputStyle.normal.textColor = Color.white;
			inputStyle.clipping = TextClipping.Clip;
			inputStyle.wordWrap = false;

			emptyStyle = new GUIStyle(footerStyle);

			stylesReady = true;
		}

		static GUIStyle MakeMessage(GUIStyle root, Color color) {
			GUIStyle s = new GUIStyle(root);
			s.fontSize = FONT_SIZE;
			s.alignment = TextAnchor.UpperLeft;
			s.normal.textColor = color;
			s.wordWrap = true;             // uzun kayıtlar sığmıyorsa sarılsın
			s.clipping = TextClipping.Clip;
			s.padding = new RectOffset(0, 4, 1, 1);
			return s;
		}
	}
}
