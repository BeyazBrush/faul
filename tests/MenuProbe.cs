using System;
using System.Collections.Generic;
using System.Reflection;
using SteamMP;

namespace MenuProbe {

	/// <summary>
	/// Ana menü mantığının masaüstünde doğrulanabilen kısmı.
	///
	/// Sahne olmadan test edilebilenler:
	///
	///   * NEW GAME olayının TERCİH SIRASI. Oyunun butonları PlayMaker'ın
	///     MousePickEvent EYLEMİ ile tıklanıyor (level0 kanıtı: "Mouse over" /
	///     "Mouse off" / "ENTER" olayları). Olay tahmin etmek çalışmıyordu:
	///     eski sürüm "CLICK"/"BEGIN" gibi isimleri önceliklendiriyor ve oyunun
	///     gerçek onay olayı olan ENTER'a hiç bakmıyordu. Burada level0'tan
	///     çıkarılan GERÇEK olay listesiyle tercih sırası sınanıyor.
	///
	///   * Lobi kutusunun 17 haneli kimliği kabul etmesi. Önceden kutu 6
	///     karakterle sınırlıydı, yani hiçbir lobi kimliği yazılamıyor ve
	///     "lobi kodu ile giriş" yolu hiç çalışmıyordu.
	///
	/// Test edilemeyenler (oyun gerektiriyor): MousePickEvent eyleminin
	/// mouseUp alanının okunması, butonun gerçekten görünür olması, fare
	/// tıklaması, Steam lobisi oluşturma. Bunlar MenuController.ClickEventOf
	/// içinde; sahne olmadan sahte bir FSM kurmak da mümkün değil çünkü
	/// new GameObject() bir Unity ECall'idir ve masaüstü CLR'sinde derlenmez.
	/// </summary>
	public static class Program {

		static int failures;

		static void Check(string what, object actual, object expected) {
			bool ok = Equals(actual, expected);
			if (!ok) failures++;
			Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + " = " + actual
				+ (ok ? "" : "   (expected " + expected + ")"));
		}

		public static int Main() {
			Console.WriteLine("--- NEW GAME event choice ---");
			RunEventChoice();
			Console.WriteLine("--- transition table pick (regression) ---");
			RunTransitionPick();
			Console.WriteLine("--- NEW GAME panel route ---");
			RunNewGamePanelRoute();
			Console.WriteLine("--- NEW GAME dialog must stay alive ---");
			RunNewGameDialogAlive();
			Console.WriteLine("--- lobby code input ---");
			RunLobbyInput();
			Console.WriteLine(failures == 0 ? "ALL MENU CHECKS PASSED" : failures + " CHECK(S) FAILED");
			return failures == 0 ? 0 : 1;
		}

		static MethodInfo PickMethod { get { return typeof(MenuController).GetMethod("PickEvent",
			BindingFlags.NonPublic | BindingFlags.Static); } }
		static MethodInfo Reserved { get { return typeof(MenuController).GetMethod("IsPlayMakerReserved",
			BindingFlags.NonPublic | BindingFlags.Static); } }

		static string Pick(List<string> declared) {
			MethodInfo m = PickMethod;
			return m == null ? null : m.Invoke(null, new object[] { declared }) as string;
		}

		static bool IsReserved(string name) {
			MethodInfo m = Reserved;
			return m != null && (bool)m.Invoke(null, new object[] { name });
		}

		static void RunEventChoice() {
			Check("PickEvent exists", Pick != null, true);
			Check("IsPlayMakerReserved exists", Reserved != null, true);
			if (Pick == null) return;

			// level0'taki gerçek buton olayları: FSM'in bildirdiği liste.
			List<string> real = new List<string>(new string[] {
				"FINISHED", "Mouse over", "Mouse off", "ENTER", "CANCEL",
			});
			Check("real button list picks ENTER", Pick(real), "ENTER");

			// Klavye/fare aynı olayı gönderiyor: BUYUK/küçük harf fark etmemeli.
			Check("case insensitive", Pick(new List<string>(new string[] {
				"FINISHED", "enter", "CANCEL" })), "enter");

			// ENTER yoksa tıklamaya benzeyen isimler sırayla denenir.
			Check("MOUSE UP before MOUSE DOWN", Pick(new List<string>(new string[] {
				"FINISHED", "MOUSE DOWN", "MOUSE UP" })), "MOUSE UP");
			Check("CLICK when nothing better", Pick(new List<string>(new string[] {
				"FINISHED", "CLICK", "MOUSEDOWN" })), "CLICK");

			// Hiçbir bilinen isim yoksa ilk kullanılabilir olay. Sıralamanın
			// kendisi önemsiz, yerleşik olayların atlanması önemli.
			Check("falls back to the first usable event",
				Pick(new List<string>(new string[] {
					"FINISHED", "UPDATE", "CANCEL", "START" })), "START");

			// Yalnızca yerleşik olaylar varsa hiçbir şey seçilmez: yanlış olay
			// göndermek, hiç göndermemekten iyi değil (butonun çizimi geri
			// açılır ve oyuncu oyunun kendi menüsünü kullanır).
			Check("no usable event returns null",
				Pick(new List<string>(new string[] { "FINISHED", "UPDATE", "CANCEL" })), null);
			Check("empty list returns null", Pick(new List<string>()), null);
			Check("null list returns null", Pick(null), null);

			// Yerleşik olay listesi: CANCEL bir butonun etiketi olabilir ama
			// onay değildir.
			Check("FINISHED reserved", IsReserved("FINISHED"), true);
			Check("UPDATE reserved", IsReserved("UPDATE"), true);
			Check("CANCEL reserved", IsReserved("CANCEL"), true);
			Check("ABORT reserved", IsReserved("ABORT"), true);
			Check("ENTER not reserved", IsReserved("ENTER"), false);
			Check("Mouse over not reserved", IsReserved("Mouse over"), false);

			// Buton adı level0'tan doğrulandı: nesne "ButtonNewgame" (küçük g).
			Check("button name matches the scene", MenuController.NewGameButtonName, "ButtonNewgame");
		}

		/// <summary>
		/// NEW GAME onay panelinin butonları gizlenmemeli.
		///
		/// Regresyon koruması. Bu iki adge (ButtonBegin, ButtonCancel) bir ara
		/// HiddenNames içindeydi ve Hide() onlara SetActive(false) uyguluyordu.
		/// dnSpy kanıtı: PlayMakerFSM.OnDisable -> Fsm.Stop() -> Active = false,
		/// ve Fsm.ProcessEvent !Active'de erken dönüyor. Yani oyun onay panelini
		/// açıyor ama BEGIN butonunun FSM'i ölü olduğu için hiçbir tıklama
		/// işlemiyordu: "NEW GAME çalışmıyor".
		///
		/// Liste sessizce değiştirilip bu hata yeniden eklenirse test kırmızı
		/// olur.
		/// </summary>
		static void RunTransitionPick() {
			// Kayıttaki animasyon FSM'i (buton nesnesinin kendisinde).
			MenuFsm.FsmInfo setSize = new MenuFsm.FsmInfo();
			setSize.Name = "SetSize";
			setSize.Path = "Interface/Buttons/ButtonNewgame";
			setSize.OnButton = true;
			setSize.Active = true;
			setSize.PickDown = "DOWN";
			setSize.PickOver = "OVER";
			setSize.PickOff = "OFF";
			setSize.Transitions.Add("FINISHED");
			setSize.Transitions.Add("DOWN");
			setSize.Transitions.Add("OFF");
			setSize.Transitions.Add("OVER");

			// Gerçek tıklayan buton FSM'i (bir alt nesnede, ENTER geçişi olan).
			MenuFsm.FsmInfo button = new MenuFsm.FsmInfo();
			button.Name = "Newgame";
			button.Path = "Interface/Buttons/ButtonNewgame/wires";
			button.OnButton = false;
			button.Active = true;
			button.PickUp = "ENTER";
			button.NotifiesOther = true;
			button.Transitions.Add("FINISHED");
			button.Transitions.Add("ENTER");
			button.Transitions.Add("Mouse over");
			button.Transitions.Add("Mouse off");

			List<MenuFsm.FsmInfo> infos = new List<MenuFsm.FsmInfo>();
			infos.Add(setSize);
			infos.Add(button);

			string chosen;
			int score = MenuFsm.PickBest(infos, out chosen);
			Check("picks the clickable FSM", chosen, "ENTER");

			// Ters sırada da aynı sonuç: seçim sıraya değil PUANA bakar.
			List<MenuFsm.FsmInfo> flipped = new List<MenuFsm.FsmInfo>();
			flipped.Add(button);
			flipped.Add(setSize);
			Check("order does not matter", MenuFsm.PickBest(flipped, out chosen), score);

			// Geçiş tablosu boş olan FSM asla aday olmamalı: PlayMaker olayı
			// sessizce yok sayar, yani göndermenin anlamı yok.
			MenuFsm.FsmInfo empty = new MenuFsm.FsmInfo();
			empty.Name = "Wires";
			empty.Path = "Interface/Buttons/ButtonNewgame/wires";
			empty.OnButton = true;
			Check("empty transition table scores -1", MenuFsm.Score(empty), -1);
			Check("empty transition table has no event",
				MenuFsm.EventFor(empty), null);

			// Pasif FSM elenir: dnSpy, ProcessEvent !Active'de erken dönüyor.
			MenuFsm.FsmInfo inactive = new MenuFsm.FsmInfo();
			inactive.Name = "Newgame";
			inactive.Path = "Interface/Buttons/ButtonNewgame";
			inactive.OnButton = true;
			inactive.Active = false;
			inactive.PickUp = "ENTER";
			inactive.Transitions.Add("ENTER");
			List<MenuFsm.FsmInfo> withInactive = new List<MenuFsm.FsmInfo>();
			withInactive.Add(inactive);
			withInactive.Add(button);
			Check("inactive FSM loses", MenuFsm.PickBest(withInactive, out chosen), score);

			// Hiçbir FSM geçiş tablosu taşımadıysa sonuç yok.
			Check("no candidates -> -1",
				MenuFsm.PickBest(new List<MenuFsm.FsmInfo>(), out chosen), -1);
			Check("null list -> -1", MenuFsm.PickBest(null, out chosen), -1);

			// Sınıflandırma.
			Check("DOWN is press only", MenuFsm.IsPressOnly("DOWN"), true);
			Check("OVER is hover only", MenuFsm.IsHoverOnly("OVER"), true);
			Check("ENTER is not hover", MenuFsm.IsHoverOnly("ENTER"), false);
			Check("ENTER first", MenuFsm.ConfirmPreference[0], "ENTER");

			// Nesne/FSM adı eşleşmesi: "ButtonNewgame" nesnesinde "Newgame".
			MenuFsm.FsmInfo match = new MenuFsm.FsmInfo();
			match.Name = "Newgame";
			match.Path = "Interface/Buttons/ButtonNewgame";
			Check("button name matches FSM name", MenuFsm.ButtonNameMatches(match), true);
			MenuFsm.FsmInfo generic = new MenuFsm.FsmInfo();
			generic.Name = "Button";
			generic.Path = "Interface/Buttons/ButtonNewgame";
			Check("generic 'Button' matches nothing", MenuFsm.ButtonNameMatches(generic), false);

			// Yalnızca imleç geçişleri olan FSM "buton değildir".
			Check("SetSize is hover only", MenuFsm.OnlyHover(setSize), true);
			Check("button is not hover only", MenuFsm.OnlyHover(button), false);

			// Animasyon adı ceza alır, gerçek buton almaz.
			Check("SetSize is penalised", MenuFsm.Score(setSize) < 0, true);
			Check("button is not penalised", MenuFsm.Score(button) > 0, true);

			// Yalnızca animasyon varsa EventFor imleç dışı bir şey bulmaz.
			string onlyAnimEvent = MenuFsm.EventFor(setSize);
			Check("animation-only FSM has no click event",
				onlyAnimEvent == null || MenuFsm.IsHoverOnly(onlyAnimEvent)
					|| MenuFsm.IsPressOnly(onlyAnimEvent), true);
		}

		/// <summary>
		/// NEW GAME onay panelinin butonları gizlenmemeli.
		///
		/// Regresyon koruması. Bu iki adge (ButtonBegin, ButtonCancel) bir ara
		/// HiddenNames içindeydi ve Hide() onlara SetActive(false) uyguluyordu.
		/// dnSpy kanıtı: PlayMakerFSM.OnDisable -> Fsm.Stop() -> Active = false,
		/// ve Fsm.ProcessEvent !Active'de erken dönüyor. Yani oyun onay panelini
		/// açıyor ama BEGIN butonunun FSM'i ölü olduğu için hiçbir tıklama
		/// işlemiyordu: "NEW GAME çalışmıyor".
		///
		/// Liste sessizce değiştirilip bu hata yeniden eklenirse test kırmızı
		/// olur.
		/// </summary>
		/// <summary>
		/// NEW GAME artık butona olay göndermiyor; onay panelini kendisi
		/// etkinleştiriyor.
		///
		/// Gerekçe, gerçek çalışma kaydı: buton nesnesinde yalnızca bir
		/// animasyon FSM'i var ve tıklamayı işleyen şey butonun altında değil.
		/// İki ayrı deneme (isim tahmini, MousePickEvent okuma) ikisi de aynı
		/// sonucu verdi: 'SetSize' + 'DOWN', yani hiçbir şey.
		///
		/// Panel ise metniyle bulunabiliyor: level0'da
		/// "NEW GAME WILL REPLACE ANY EXISTING SAVE!" var. Paneli etkinleştirmek
		/// oyunun ilk adımını birebir yapar, sonrası kullanıcının tıklamasına
		/// kalır (BEGIN/CANCEL gerçek butonlar).
		/// </summary>
		static void RunNewGamePanelRoute() {
			// Sahne gerektiren FindConfirmPanel masaüstünde çağrılamaz
			// (TextMesh bir Unity bileşeni), ama varlığı ve imzası doğrulanır.
			MethodInfo find = typeof(MenuFsm).GetMethod("FindConfirmPanel");
			Check("FindConfirmPanel exists", find != null, true);
			Check("FindConfirmPanel returns GameObject",
				find == null || find.ReturnType == typeof(UnityEngine.GameObject), true);

			// Panel açıkken BEGIN/CANCEL butonları gizlenmemeli: onlar oyunun
			// gerçek butonları ve fare tıklamasıyla çalışıyor. HiddenNames'te
			// olurlarsa FSM'leri ölür ve panel işlemez.
			Check("ButtonBegin is NOT hidden",
				MenuController.HiddenNames.Contains("ButtonBegin"), false);
			Check("ButtonCancel is NOT hidden",
				MenuController.HiddenNames.Contains("ButtonCancel"), false);

			// Menü kamerası gizlenmemeli: BEGIN tıklaması PlayMaker'ın
			// MousePickEvent eylemiyle kameradan ışın atarak geliyor; kamera
			// giderse Camera.main null olur ve tıklama sessizce yutulur.
			//
			// 'Car' listede DURUYOR ama gizleme kararı onu eliyor. Gerçek kayıt:
			//   menu: 'Car' carries camera 'motor'; NOT hidden (the game's
			//         buttons need it).
			// Yani listenin içeriği değil, KARAR önemli; o da saf bir
			// fonksiyon (MenuFsm.CanHide) ve burada doğrulanıyor.
			Check("no camera -> can hide", MenuFsm.CanHide("ButtonQuit", null), true);
			Check("empty camera name -> can hide", MenuFsm.CanHide("ButtonQuit", ""), true);
			Check("camera present -> must NOT hide",
				MenuFsm.CanHide("Car", "motor"), false);
			Check("camera present on a model -> must NOT hide",
				MenuFsm.CanHide("MenuCar", "menu"), false);

			// NEW GAME butonu ARTIK TAMAMEN GİZLENİYOR.
			//
			// NEDEN DEĞİŞTİ: canlı log (11.10) o yaklaşımın çalışmadığını
			// gösterdi — "NEW GAME: no FSM has a usable transition; the
			// game's own button stays visible." Olay adı keşfi başarısız
			// olunca buton görünür kalıyordu. Modun kendi menüsü PLAY
			// ONLINE akışını karşılıyor; oyunun New Game butonuna gerek yok.
			Check("ButtonNewgame IS hidden",
				MenuController.HiddenNames.Contains(MenuController.NewGameButtonName), true);
			// Nesne yalnızca BİR listede durmalı: iki kez gizlemek hatadır.
			Check("ButtonNewgame is NOT a model root",
				MenuController.HiddenModelNames.Contains(MenuController.NewGameButtonName), false);
		}

		static void RunNewGameDialogAlive() {
			Check("NewGameDialogNames exists", MenuController.NewGameDialogNames != null, true);
			if (MenuController.NewGameDialogNames == null) return;

			// Onay panosu görünümü kendi ekranımızı kapatıp yer açıyor. Bu
			// görünüm olmadan bizim merkezdeki panelimiz oyunun BEGIN butonunu
			// kapatıyor ve kullanıcı fareyi havada gezdiriyor.
			Check("GameDialog view exists",
				Enum.IsDefined(typeof(MenuUi.View), "GameDialog"), true);

			string[] dialog = MenuController.NewGameDialogNames.ToArray();

			Check("dialog lists ButtonBegin",
				IndexOf(dialog, "ButtonBegin") >= 0, true);
			Check("dialog lists ButtonCancel",
				IndexOf(dialog, "ButtonCancel") >= 0, true);

			Check("ButtonBegin is NOT hidden",
				MenuController.HiddenNames.Contains("ButtonBegin"), false);
			Check("ButtonCancel is NOT hidden",
				MenuController.HiddenNames.Contains("ButtonCancel"), false);

			// NEW GAME butonu tamamen gizleniyor (yukardaki gerekçe): FSM'sini
			// canlı tutmaya gerek kalmadı çünkü ona olay göndermiyoruz.
			Check("ButtonNewgame IS hidden",
				MenuController.HiddenNames.Contains(MenuController.NewGameButtonName), true);
			Check("ButtonNewgame is NOT a model root",
				MenuController.HiddenModelNames.Contains(MenuController.NewGameButtonName), false);

			// Bir nesne iki listede birden olamaz: HiddenNames uygulanırken
			// SetActive(false) çalışır, NewGameDialogNames uygulanmaz; çakışma
			// hangisinin kazandığını belirsizleştirir.
			int overlap = 0;
			for (int i = 0; i < dialog.Length; i++) {
				if (MenuController.HiddenNames.Contains(dialog[i])
					|| MenuController.HiddenModelNames.Contains(dialog[i])) overlap++;
			}
			Check("no overlap with the hidden lists", overlap, 0);
		}

		static int IndexOf(string[] a, string s) {
			for (int i = 0; i < a.Length; i++) if (a[i] == s) return i;
			return -1;
		}

		static void RunLobbyInput() {
			MenuUi ui = new MenuUi();

			// 6 haneli oturum kodu: eski davranış, hâlâ çalışmalı.
			ui.DebugSetCode("abc123", true);
			Check("six character code accepted", ui.CodeComplete, true);
			Check("six characters is not a lobby id", ui.LooksLikeLobbyId, false);

			// 17 haneli lobi kimliği: ÖNCE YAZILAMAZDI (kutu 6 karakterle
			// sınırlıydı), yani "lobi kodu ile giriş" yolu hiç çalışmıyordu.
			ui.DebugSetCode("10977524123456789", true);
			Check("lobby id accepted", ui.CodeComplete, true);
			Check("lobby id recognised", ui.LooksLikeLobbyId, true);
			Check("lobby id keeps all digits", ui.TypedLength, 17);
			Check("lobby id text intact", ui.TypedCode, "10977524123456789");

			// Karışık karakterli 17 hane lobi kimliği DEĞİLDİR.
			ui.DebugSetCode("10977524a23456789", true);
			Check("mixed input is not a lobby id", ui.LooksLikeLobbyId, false);

			// 6 haneden kısa kod tamamlanmış sayılmaz (JOIN reddedilmeli).
			ui.DebugSetCode("abc12", true);
			Check("short code refused", ui.CodeComplete, false);

			// Yazma üst sınırı artık 20 karakter: 17 haneli kimlik sığar.
			ui.DebugSetCode(string.Empty, true);
			for (int i = 0; i < 30; i++) ui.Type('1');
			Check("input capped at MaxCodeChars", ui.TypedLength, MenuUi.MaxCodeChars);
			Check("cap is at least 17", MenuUi.MaxCodeChars >= 17, true);

			// Yapıştırma: boşluk ve satır sonları temizlenir.
			ui.DebugSetCode(string.Empty, true);
			ui.Paste("1097 7524\r\n123456789");
			Check("paste strips whitespace", ui.TypedCode, "10977524123456789");
			Check("paste yields a lobby id", ui.LooksLikeLobbyId, true);

			// Odak yoksa yazma sessizce yok sayılır.
			ui.DebugSetCode(string.Empty, false);
			ui.Type('x');
			Check("typing without focus is ignored", ui.TypedLength, 0);

			// Backspace odak yoksa çalışmaz.
			ui.DebugSetCode("abc123", false);
			ui.Backspace();
			Check("backspace without focus ignored", ui.TypedLength, 6);
			ui.DebugSetCode("abc123", true);
			ui.Backspace();
			Check("backspace removes one", ui.TypedLength, 5);
		}
	}
}
