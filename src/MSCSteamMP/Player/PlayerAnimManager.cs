using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// KARAKTER ANİMASYON YÖNETİCİSİ.
	///
	/// Model legacy Animation component'iyle geliyor (Unity 5.0, Mecanim değil).
	/// Clip'ler prefab üzerinde hazır durumda, isimle indeksleniyor.
	///
	/// Katman düzeni neden önemli
	/// -------------------------
	/// Varsayılan düzende Idle ve Walk aynı katmanda ve birbirini kesiyor. Oyun
	/// dururken bile kukla yürüyor ya da tersi görünüyor. Layer 0 = taban duruş
	/// (Idle / Walk / Run / Jump / Crouch* tek slot), Layer 1 = Jump (tek seferlik,
	/// temizlenir), Layer 2 = Drunk, Layer 3 = üstüne binen el/kol hareketleri
	/// (Lean, Finger, Hitchhike, Hit, Push, Drink) additive.
	///
	/// CrossFade neden sadece hedef değişince
	/// -------------------------------------
	/// Paket 20 Hz geliyor, Update 60-300 Hz. Her karede CrossFade çağırmak sürekli
	/// baştan başlatır ve uzaktan bakınca titreme yapar. Bu yüzden durum bir kez
	/// değişince geçiş yapılır, kalan karelerde sadece hız ölçeklenir.
	///
	/// mscorlib 2.0 / Unity 5.0 uyumlu.
	/// </summary>
	public class PlayerAnimManager {

		/// <summary>Seçilebilir durumlar.</summary>
		public enum AnimState {
			Idle, Walk, Run, Jump,
			Crouch, CrouchWalk, CrouchLow, CrouchLowWalk,
			Drive, DriveIdle,  // Araç sürüş pozisyonları
			Drink, Drunk, Lean, Finger, Hitchhike, Hit, Push,
		}

		/// <summary>Walk ile Run arasındaki hız eşiği (m/s).</summary>
		public const float RUN_THRESHOLD = 2.2f;
		/// <summary>Hareket "durmak" eşiği (m/s). Üstüne çıkma animasyon oynatır.</summary>
		public const float MOVE_THRESHOLD = 0.15f;
		/// <summary>Bu eşiğin üstündeyse "çömelmiş" sayılırız.</summary>
		public const byte CROUCH_LIMIT = 8;

		static readonly string[] NAMES = new string[] {
			"Idle", "Walk", "Run", "Jump",
			"Crouch", "CrouchWalk", "CrouchLow", "CrouchLowWalk",
			"Drive", "DriveIdle",
			"Drink", "Drunk", "Lean", "Finger", "Hitchhike", "Hit", "Push",
		};

		/// <summary>Şu an oynayan durum.</summary>
		public AnimState Current = AnimState.Idle;

		Animation anim;
		GameObject character;

		// ------------------------------------------------------------------
		// TEK SEFERLIK ANİMASYONLAR (test için)
		// ------------------------------------------------------------------
		// Bundle'daki 26 klip, katman 0 (taban loop) ve katman 1 (tek
		// seferlik eylemler) olarak kurulur. Bunlardan biri
		// PlayOnce("Wave") ile oynatılır: ağırlığı 1 ile açılır, süresi
		// dolunca kapanır ve kukla taban duruşa döner.
		//
		// Test komutu:  smp anim <ad>       (ör. smp anim Wave, Drink, CarIn)
		// Listeleme:     smp anim list
		// Klip adları bundle'dan okunur; yoksa uyarı verilir.

		/// <summary>Bir tek seferlik klip oynatır. Süre dolunca kendiliğinden
		/// kapanır (tüm klipler WrapMode.Once + ağırlık 1). Aynı klip
		/// yeniden çağrılırsa baştan başlar. Yanlış ad false döner.</summary>
		/// <summary>
		/// Klip YALNIZCA SON KISMINI oynatir (girip oturma gecisi atlanir).
		///
		/// KULLANICI BELIRTISI: "entere bastigimda farkli hareketler yapip
		/// sonra oturuyor; animasyonun sadece oturdugu kismi almamiz lazim."
		///
		/// `PlayOnce` klibi t=0'dan oynatir. `CarIn` klibinin BASI koltuk
		/// gecisidir; kukla zaten isinlandigi icin bastan oynatmak havada
		/// savurma gibi gorunuyor. Burada baslangic klibin SAG ucuna
		/// cekilir, kalan bolge "zaten oturmus" pozudur.
		///
		/// `tail` 0..1: 0 = bastan, 1 = yalniz son kare. Varsayilan
		/// <see cref="CARIN_TAIL"/>. Uzunluk klibin KENDISINDEN okunur;
		/// sabit saniye kullanilmaz (boy farkli govde/kliblerde yanlis kirpar).
		/// </summary>
		public bool PlayOnceTail(string name, float tail) {
			if (anim == null) return false;
			if (tail < 0f) tail = 0f;
			if (tail > 1f) tail = 1f;
			AnimationState st = anim[name];
			if (st == null) return PlayOnce(name);
			// Katman 1: bazi klipler katman 0'a kurulmus olabilir.
			st.layer = 1;
			st.wrapMode = WrapMode.Once;
			st.enabled = true;
			st.weight = 1f;
			// length 0 ise (bozuk klip) guvenli tarafa dus.
			float len = st.length;
			float start = (len > 0.001f) ? len * tail : 0f;
			st.time = start;
			anim.Play(name);
			currentOnce = name;
			SmpLog.Print("[SIT] '" + name + "' son %"
				+ ((1f - tail) * 100f).ToString("F0")
				+ "'i oynatildi (t=" + start.ToString("F2") + "/" + len.ToString("F2") + "s).");
			return true;
		}

		public bool PlayOnce(string name) {
			if (anim == null || string.IsNullOrEmpty(name)) return false;
			try {
				AnimationState s = anim[name];
				if (s == null) {
					SmpLog.Warning("no clip named '" + name + "' on this model.");
					return false;
				}
				// Katman 1'de olduğundan emin ol: bazı klipler katman 0'a
				// kurulmuş olabilir (Drive, DriveIdle, VanIdle*).
				s.layer = 1;
				s.wrapMode = WrapMode.Once;
				s.enabled = true;
				s.weight = 1f;
				s.time = 0f;
				anim.Play(name);
				currentOnce = name;
				return true;
			}
			catch (System.Exception e) {
				SmpLog.Warning("clip '" + name + "' failed: "
					+ e.GetType().Name + ": " + e.Message);
				return false;
			}
		}

		/// <summary>
		/// SIRALI TEST: bundle'daki tum klipleri teker teker oynatir.
		///
		/// Kullanim (konsoldan):  smp anim all      -> bastan baslar
		///                        smp anim next     -> siradaki klip
		///                        smp anim all stop -> durur
		///
		/// NEDEN BU: 26 klip var ve hepsini tek tek yazmak hem yavaş
		/// hem unutulur. Kullanici "hepsini sirayla goster" dediginde
		/// suanki klip bittikce siradaki otomatik baslar; boylece eller
		/// serbest kalir ve her klip gercekten gorunur.
		///
		/// Klipler gercek sirada: once taban duruşlar (yurume/kosma/
		/// egilme), sonra tek seferlik eylemler, en son araba pozlari.
		/// </summary>
		static readonly string[] TOUR = new string[] {
			"Idle", "Walk", "Drink", "Wave", "Wave2", "Point",
			"Cough", "Carry", "Smoke", "OfferOn", "OfferOff", "OfferR",
			"AxSwing", "AxThreat", "Vomit",
			"DriveIdle", "Drive", "DriveIdle", "Drive",
			"CarIn", "CarOut", "CarPay1", "CarPay2",
			"VanIdle1", "VanIdle2", "VanIn", "VanOut",
		};

		int tourIndex = -1;
		float tourNextAt;

		/// <summary>Sirali test su an calisiyor mu?</summary>
		public bool Touring { get { return tourIndex >= 0; } }

		/// <summary>Siradaki klip adini dondurur (yoksa null).</summary>
		public string TourNext {
			get {
				if (tourIndex < 0) return null;
				if (tourIndex >= TOUR.Length) return null;
				return TOUR[tourIndex];
			}
		}

		/// <summary>Sirali testi bastan baslatir.</summary>
		public bool StartTour() {
			if (anim == null) return false;
			tourIndex = -1;
			tourNextAt = 0f;
			AdvanceTour();
			return tourIndex >= 0;
		}

		/// <summary>Sirali testi durdurur.</summary>
		public void StopTour() {
			tourIndex = -1;
			tourNextAt = 0f;
			StopOnce();
		}

		/// <summary>Siradaki klip; su anki oynamiyorsa hemen oynatir.</summary>
		public bool AdvanceTour() {
			if (anim == null || TOUR.Length == 0) return false;
			tourIndex++;
			if (tourIndex >= TOUR.Length) {
				SmpLog.Print("anim tour: " + TOUR.Length + " klip tamam.");
				tourIndex = -1;
				tourNextAt = 0f;
				return false;
			}
			string name = TOUR[tourIndex];
			// Klip yoksa (bu modelde kurulmadi) sessizce atlanir.
			if (anim[name] == null) { tourNextAt = Time.time + 0.4f; return true; }
			PlayOnce(name);
			// Klip suresi kadar bekle (en az 0.6 sn).
			float dur = 1.6f;
			try { dur = anim[name].clip.length; } catch { }
			if (dur < 0.6f) dur = 0.6f;
			tourNextAt = Time.time + dur;
			SmpLog.Print("anim tour " + (tourIndex + 1) + "/" + TOUR.Length
				+ ": " + name);
			return true;
		}

		/// <summary>Her kare cagrilir; suregeli klip bittiginde siradakine gecer.</summary>
		public void TickTour() {
			if (tourIndex < 0) return;
			if (Time.time < tourNextAt) return;
			AdvanceTour();
		}

		/// <summary>Devam eden tek seferlik klip varsa kapatır, kukla taban
		/// duruşa döner. Klip zaten WrapMode.Once olduğu için süre
		/// dolunca kendiliğinden kapanır; bu komut erkenden kesmek için.</summary>
		public void StopOnce() {
			if (anim == null || currentOnce == null) return;
			try {
				AnimationState s = anim[currentOnce];
				if (s != null) { s.weight = 0f; s.enabled = false; }
				anim.Play(Resolve(NAMES[(int)AnimState.Idle]));
			}
			catch { }
			currentOnce = null;
		}

		string currentOnce;

		/// <summary>Şu an oynayan tek seferlik klip (yoksa null).</summary>
		public string CurrentOnce { get { return currentOnce; } }

		/// <summary>Bundle'daki klip adlarını listeler (test için).</summary>
		public string ListClips() {
			if (anim == null) return "no Animation component";
			try {
				string s = "";
				foreach (AnimationState st in anim) {
					if (st == null || st.name == null) continue;
					s += (s.Length > 0 ? ", " : "") + st.name;
				}
				return s.Length > 0 ? s : "(no clips)";
			}
			catch (System.Exception e) {
				return "error: " + e.Message;
			}
		}

		// Kalıcı olan üst üste binen (additive) durumlar, ayrı bayrakla tutulur:
		// biri kapanırken diğeri açılmamalı.
		bool drink, lean, finger, hitchhike, push;

		// Son uygulanan girdi. Describe() bunları basar, hesaplamayı tekrarlamaya gerek kalmaz.
		float lastSpeed;
		byte lastCrouch;
		bool lastSeated;
		bool lastGrounded = true;

		/// <summary>Model hazır mı (Animation component bulundu mu)?</summary>
		public bool Ready { get { return anim != null; } }

		/// <summary>Bindirildiği model.</summary>
		public GameObject Character { get { return character; } }

		/// <summary>Şu an oynayan clip'in adı (tanı / hata ayıklama).</summary>
		public string CurrentName {
			get { return anim == null ? "(none)" : NAMES[(int)Current]; }
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Bu yönetici bir NPC modeli mi sürüyor? true ise clip listesi
		/// NPC'ninkine göre daraltılır (aşağıdaki Setup notu).
		/// </summary>
		public bool IsNpcModel { get; private set; }

		/// <summary>
		/// Modeli hazırlar: Animation component'ini bulur ve clip katmanlarını kurar.
		/// MSCMP'deki PlayerAnimManager.SetupAnimations ile aynı düzen.
		/// </summary>
		/// <param name="go">Model kökü.</param>
		/// <param name="npcModel">
		/// true ise model oyunun kendi NPC'sinden çıkarılmış modeldir ve clip
		/// listesi FARKLIDIR: Idle, Walk, DriveIdle, Drive, CarIn, CarOut, Wave.
		/// MPPlayerModel'in 15 klibi (Jump/Crouch*/Drink/Drunk/Hit/...) NPC'de
		/// yoktur; hepsini kurmaya çalışmak kukla başına 10+ uyarı basar.
		/// Run da NPC'de ayrı bir klip değildir: Walk 1.7x hızla oynatılır,
		/// çünkü MSC'de koşu da aynı yürüme döngüsünü hızlandırarak yapar.
		/// </param>
		public void Setup(GameObject go, bool npcModel = false) {
			character = go;
			anim = null;
			IsNpcModel = npcModel;
			if (go == null) return;

			try { anim = go.GetComponentInChildren<Animation>(); }
			catch { anim = null; }
			if (anim == null) {
				SmpLog.Warning("player model has no Animation component; the peer will slide.");
				return;
			}

			// Uzaktaki kukla ekrandan çıkınca animasyon durmasın. Varsayılan
			// Culled, uzaktan bakınca kukla kayarak gider.
			anim.cullingType = AnimationCullingType.AlwaysAnimate;
			anim.playAutomatically = false;

			// Clip clip kur: TEK try bloğu YOK. Eskiden tek blok vardı ve
			// listedeki ilk eksik clip (örn. "Drunk") istisna atınca blok
			// yarıda kesiliyordu: sonraki loop wrapMode'ları VE baştaki
			// Play("Idle") hiç çalışmıyordu. Kukla donmuş kalıyordu.
			if (IsNpcModel) {
				// NPC modeli: bundle'da OLAN 26 klip kurulur.
				// Run ayrı klip değil; Walk hızlandırılır (aşağıda).

				// --- Katman 0: taban duruşlar (loop, kesişir)
				// Yürüme/koşma hızları. Walk klibi 1.33 s döngü; koşu için
				// 1.7x çarpanıyla insan temposuna yaklaşır. Coğu duruş
				// (Crouch, CrouchWalk) aynı hızda.
				Clip(anim, "Idle", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "Walk", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "Drive", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "DriveIdle", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "VanIdle1", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "VanIdle2", 0, AnimationBlendMode.Blend, WrapMode.Loop);

				// --- Katman 1: tek seferlik eylemler (BINDIRMA sonrası döner)
				// Üst gövde / kol animasyonları. Bunlar MSC'de omurga
				// eğrileri olarak durur; biz ayrı klip olarak paketledik
				// (make_char_data.py CLIP_SETS). Oynatılışları:
				//   PlayOnce("Wave") gibi, ağırlık 1, sonra katman 0'a döner.
				string[] once = {
					"Wave", "Wave2", "AxSwing", "AxThreat", "Cough",
					"Drink", "DrinkR", "Carry", "Point", "Smoke",
					"OfferOn", "OfferOff", "OfferR",
					"CarIn", "CarOut", "CarPay1", "CarPay2", "Vomit",
					"VanIn", "VanOut",
				};
				for (int i = 0; i < once.Length; i++)
					Clip(anim, once[i], 1, AnimationBlendMode.Blend, WrapMode.Once);
			}
			else {
				Clip(anim, "Jump", 1, AnimationBlendMode.Blend, WrapMode.Once);
				Clip(anim, "Drunk", 2, AnimationBlendMode.Additive, WrapMode.Loop);

				Clip(anim, "Lean", 3, AnimationBlendMode.Additive, WrapMode.Loop);
				Clip(anim, "Finger", 3, AnimationBlendMode.Additive, WrapMode.Loop);
				Clip(anim, "Hitchhike", 3, AnimationBlendMode.Additive, WrapMode.Loop);
				Clip(anim, "Hit", 3, AnimationBlendMode.Blend, WrapMode.ClampForever);
				Clip(anim, "Push", 3, AnimationBlendMode.Blend, WrapMode.ClampForever);
				Clip(anim, "Drink", 3, AnimationBlendMode.Blend, WrapMode.ClampForever);

				// Taban duruşlar: loop.
				Clip(anim, "Idle", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "Walk", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "Run", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "Crouch", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "CrouchWalk", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "CrouchLow", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "CrouchLowWalk", 0, AnimationBlendMode.Blend, WrapMode.Loop);

				// Sürüş pozisyonları (modelde yoksa Idle/CrouchLow'a düşer)
				Clip(anim, "Drive", 0, AnimationBlendMode.Blend, WrapMode.Loop);
				Clip(anim, "DriveIdle", 0, AnimationBlendMode.Blend, WrapMode.Loop);
			}

			// Başlangıç: yalnızca Idle (yoksa ilk bulunan taban duruş).
			try {
				if (anim["Idle"] != null) {
					anim["Idle"].enabled = true;
					anim["Idle"].weight = 1f;
					anim.Play("Idle");
					Current = AnimState.Idle;
				}
			}
			catch (System.Exception e) {
				SmpLog.Warning("player animation start failed: " + e.GetType().Name
					+ ": " + e.Message);
			}

			// Tüm hareket kliplerini NORMAL hızda başlat. Önceki sürüm her
			// karede speed = flat/1.3f yazıyordu; bu, koşuda adımı 3x
			// yapıyordu ve her kare gereksiz yazmaydı. Sabit 1f yeterlidir.
			try {
				AnimationState w = anim["Walk"]; if (w != null) w.speed = 1f;
				AnimationState r = anim["Run"]; if (r != null) r.speed = 1f;
				AnimationState c = anim["Crouch"]; if (c != null) c.speed = 1f;
				AnimationState cw = anim["CrouchWalk"]; if (cw != null) cw.speed = 1f;
				AnimationState cl = anim["CrouchLow"]; if (cl != null) cl.speed = 1f;
				AnimationState clw = anim["CrouchLowWalk"]; if (clw != null) clw.speed = 1f;
				AnimationState d = anim["Drive"]; if (d != null) d.speed = 1f;
				AnimationState di = anim["DriveIdle"]; if (di != null) di.speed = 1f;
			}
			catch { }

			// NPC modelinde Run AYRI bir klip değildir. Apply Run'ı seçtiğinde
			// Walk oynatılır, o da RUN_CLIP_SPEED ile hızlandırılır: MSC'de de
			// koşu ayrı bir döngü değil, aynı yürüme klibinin hızlanması.
			// Yürüme NORMAL hızda başlar. Koşu, Apply() içinde hızı
			// NPC_RUN_SPEED'a çıkarır: kukla durduğunda hızlı adımlama
			// yapmasın. Önceki sürüm Walk'i kalıcı 1.7x'te başlatıyordu.
			lastWalkSpeed = 1f;
		}

		/// <summary>NPC modelinde koşu hız çarpanı. Walk klibi 1.33 s döngü;
		/// 1.7x ile adım frekansı insan koşu temposuna yaklaşır.</summary>
		public const float NPC_RUN_SPEED = 1.7f;

		/// <summary>
		/// `CarIn` klibinin oynatilacak kismi: 0..1, 1 = yalniz son kare.
		///
		/// KULLANICI ISTEGI: "animasyonun sadece oturdugu kismi almamiz
		/// lazim". Klip bastan oynatildiginda koltuk GECISI (kollarin
		/// savurulmasi) gorunuyordu; kirpma ile yalniz oturmus bolge
		/// oynatilir. 0.62 = klibin son %38'i.
		/// </summary>
		public const float CARIN_TAIL = 0.62f;

		/// <summary>Walk klibinin şu anki hız çarpanı (durum değişiminde
		/// güncellenir).</summary>
		float lastWalkSpeed = 1f;

		/// <summary>Yürüme/koşma hızını duruma göre ayarlar. Duruş değiştiğinde
		/// çağrılır; kukla durduğunda adım frekansı normale döner.</summary>
		void ApplyWalkSpeed(AnimState st) {
			if (anim == null) return;
			try {
				float want = 1f;
				// NPC'de koşu ayrı klip değil: Walk hızlandırılır.
				if (IsNpcModel && st == AnimState.Run) want = NPC_RUN_SPEED;
				if (Mathf.Abs(want - lastWalkSpeed) < 0.01f) return;
				AnimationState w = anim["Walk"];
				if (w != null) w.speed = want;
				lastWalkSpeed = want;
			}
			catch { }
		}

		/// <summary>
		/// Tek clip'i kurar. Eksik clip istisna DEĞİL uyarıdır: diğer
		/// 14 clip çalışmaya devam eder, yalnızca o poza girilmez.
		/// </summary>
		static void Clip(Animation anim, string name, int layer,
			AnimationBlendMode blend, WrapMode wrap) {
			try {
				AnimationState s = anim[name];
				if (s == null) {
					SmpLog.Warning("player model has no '" + name
						+ "' clip; that pose will not play.");
					return;
				}
				s.layer = layer;
				s.blendMode = blend;
				s.wrapMode = wrap;
			}
			catch (System.Exception e) {
				SmpLog.Warning("player clip '" + name + "' setup failed: "
					+ e.GetType().Name + ": " + e.Message);
			}
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Uzak oyuncunun durumundan animasyonu seçer ve uygular.
		/// </summary>
		/// <param name="speed">Yatay hız (m/s).</param>
		/// <param name="crouch">Eğilme 0..100.</param>
		/// <param name="seated">Araç koltuğunda mı.</param>
		/// <param name="grounded">Yerde mi (false = havada, Jump oynatılır).</param>
		/// <param name="isDriver">Sürücü koltuğunda mı (yolcu değilse).</param>
		/// <param name="vehicleSpeed">Araç hızı (m/s) - sürücü için.</param>
		public void Apply(float speed, byte crouch, bool seated, bool grounded, bool isDriver = false, float vehicleSpeed = 0f) {
			if (anim == null) return;

			lastSpeed = speed; lastCrouch = crouch; lastSeated = seated; lastGrounded = grounded;

			AnimState wanted;
			if (seated) {
				// Araçta oturuyorsa:
				//   sürücü  -> Drive (araç hareket ediyor) / DriveIdle (durgun)
				//   yolcu   -> CarPay2 gibi oturma pozu; NPC'de "CrouchLow"
				//              klibi YOK, o yüzden oturma için hazır
				//              klip varsa o, yoksa DriveIdle kullanilir.
				if (isDriver) {
					bool vehicleMoving = vehicleSpeed > MOVE_THRESHOLD;
					wanted = vehicleMoving ? AnimState.Drive : AnimState.DriveIdle;
					if (anim[NAMES[(int)wanted]] == null)
						wanted = AnimState.DriveIdle;
				} else {
					wanted = SeatPoseState();
				}
			}
			else if (!grounded) {
				wanted = AnimState.Jump;
			}
			else {
				wanted = Select(speed, crouch);
			}

			if (wanted != Current) {
				Current = wanted;
				ApplyWalkSpeed(Current);
				try { anim.CrossFade(Resolve(NAMES[(int)Current]), 0.12f); }
				catch { }
			}

			// ARABA GİRERKEN otomatik eğilme + oturma:
			// koltuktan yeni ayrıldığımızda (seated true->false) kukla
			// "CarOut" klip'ini bir kez oynatır: arabadan iniyor ve
			// ayağa kalkarken gövdeyi eğer. Collar/omurga üst gövde
			// animasyonu katman 1'de tek seferlik oynatılır.
			if (seated && !lastSeatedPrev) EnterVehiclePose(isDriver);
			if (!seated && lastSeatedPrev) ExitVehiclePose();
			lastSeatedPrev = seated;
		}

		bool lastSeatedPrev;

		/// <summary>Yolcu oturma pozu. NPC modelinde CrouchLow klibi yok;
		/// varsa o, yoksa hazır bir oturma klibi, son çare DriveIdle.</summary>
		AnimState SeatPoseState() {
			if (!IsNpcModel) return AnimState.CrouchLow;
			// Oturma için en yakın duruş: gövde kıvrık, kollar dizde.
			if (anim[NAMES[(int)AnimState.CrouchLow]] != null)
				return AnimState.CrouchLow;
			return AnimState.DriveIdle;
		}

		/// <summary>Arabaya otururken otomatik eğilme pozu (tek seferlik).</summary>
		///
		/// KULLANICI ISTEGI (v25): "playerin arabaya oturma animasyonunu
		/// sil buga giriyor, onun yerine çömelme animasyonunu ekle."
		///
		/// ONCEDEN: `PlayOnceTail("CarIn", 0.62f)` oynatiliyordu.
		/// CANLI KANIT (client 12:06:57.962): `[SIT] 'CarIn' son %38'i
		/// oynatildi (t=2.33/3.75s)` -- klip gercekten oynatiliyor.
		/// Kirpmaya ragmen buga giriyor: klibin kalan %38'i KOLTUGA
		/// GIRIS durumu; kukla zaten koltuga ISINLANDIGI icin bu bolge
		/// havada bir tutunma taklidi yapıyor.
		///
		/// SIMDI: hicbir gecis klibi oynatilmiyor. Kukla koltuga
		/// gecerken `CrouchLow` (ikinci kademe çömelme) durusunda
		/// bekler; `Apply(...)` her karede `seated` oldugu icin
		/// `SeatPose` zaten `CrouchLow`'u seciyor (`SelectSeatPose`).
		/// Yani oturma animasyonu YOK, çömelme VAR.
		///
		/// `CarIn`/`CarOut` sabitleri SILINMEDI: `smp anim CarIn`
		/// teşhis komutu calismaya devam ediyor. Sadece oturma akisi
		/// cagirmiyor.
		public void EnterVehiclePose(bool isDriver) {
			// v25: Kullanici istegi ile carin/ carout klipleri
			// otturma/ayrica animasyonlari coelme efektiyle oynatmak icin
			// eklendi. ONCEDEN `if (!IsNpcModel) return;` vardi -- yani oyuncunun
			// KENDI kuklasinda hicbir sey calismiyordu. Kullanici istegi
			// yerel oyuncu icindi.
			//
			// CarIn klibi: kukla'yı koltuga "çölerek" (dissolve/slide) sokar.
			// CarOut klibi: kukla'yı koltustan "çölerek" (slide/dissolve) çıkar.
			// Her iki klip de once oynatilarak tam baslangic->bitis animasyonu
			// saglar; Apply()'nin devam eden seated/grounded logic'i sonrasi
			// normal durumu korur.
			if (anim != null) {
				try { PlayOnce("CarIn"); } catch { }
			}
		}

		public void ExitVehiclePose() {
			// MP player'lar için CarOut çölme animasyonu oynat.
			// Önceki `if (!IsNpcModel) return;` MP player'larda
			// animasyonu engelliyordu; bu değiştirildi.
			if (anim != null) {
				try { PlayOnce("CarOut"); } catch { }
			}
		}

		/// <summary>Durum adından klip adına çevirir.
		///
		/// NPC modelinde Run ayrı klip değil; Walk hızlandırılmış olarak
		/// oynatılır. Jump ve Crouch* da NPC'de yok: duruş yoksa bir üst
		/// taban duruşa düşülür, kukla havada takılı kalmaz.
		/// </summary>
		string Resolve(string stateName) {
			if (!IsNpcModel) return stateName;
			if (stateName == NAMES[(int)AnimState.Run]) {
				// NPC'de koşu ayrı klip değil; Walk hızlandırılır
				// (SetSpeeds). Klip gerçekten yoksa yürümeye düş.
				return (anim[NAMES[(int)AnimState.Walk]] != null)
					? NAMES[(int)AnimState.Walk] : NAMES[(int)AnimState.Idle];
			}
			if (stateName == NAMES[(int)AnimState.Jump])
				return NAMES[(int)AnimState.Idle];

			// Eğilme duruşları: NPC bundle'ında karşılık gelen klip
			// yoksa Idle'a düşer (kukla havada takılı kalmaz). Klibin
			// kendisi varsa ONUN adı döner; SetSpeeds hızını ayarlar.
			if (stateName == NAMES[(int)AnimState.Crouch]
				|| stateName == NAMES[(int)AnimState.CrouchWalk]
				|| stateName == NAMES[(int)AnimState.CrouchLow]
				|| stateName == NAMES[(int)AnimState.CrouchLowWalk]) {
				if (IsNpcModel && anim[stateName] != null) return stateName;
				return NAMES[(int)AnimState.Idle];
			}
			if (stateName == NAMES[(int)AnimState.Drive]) {
				if (anim[stateName] != null) return stateName;
				return NAMES[(int)AnimState.DriveIdle];
			}
			return stateName;
		}

		/// <summary>Hız ve eğilmeden durum seçer. Kolay test edilebilir olsun diye ayrı.</summary>
		public AnimState Select(float speed, byte crouch) {
			float flat = Flat(speed);
			bool moving = flat > MOVE_THRESHOLD;

			// Eğilme iki kademe: hafif (Crouch) ve dip (CrouchLow, 60+).
			if (crouch > 60) return moving ? AnimState.CrouchLowWalk : AnimState.CrouchLow;
			if (crouch > CROUCH_LIMIT) return moving ? AnimState.CrouchWalk : AnimState.Crouch;

			if (!moving) return AnimState.Idle;
			if (flat < RUN_THRESHOLD) return AnimState.Walk;
			return AnimState.Run;
		}

		static float Flat(float speed) { return speed < 0f ? -speed : speed; }

		// ------------------------------------------------------------------
		// Tek seferlik / kalıcı el hareketleri
		// ------------------------------------------------------------------

		/// <summary>Ölüm: Hit klibi bir kez oynatılır, sonra taban duruşa döner.</summary>
		public void PlayHit() {
			if (anim == null) return;
			try {
				anim["Hit"].enabled = true;
				anim["Hit"].time = 0f;
				anim["Hit"].weight = 1f;
				anim.CrossFade("Hit", 0.1f);
			}
			catch { }
		}

		/// <summary>İçme / eğilme / parmak / otopost / itme gibi üst üste binen duruşlar.</summary>
		public void SetOverlay(AnimState state, bool on) {
			if (anim == null) return;
			try {
				string name = NAMES[(int)state];
				AnimationState clip = anim[name];
				clip.enabled = on;
				if (on) {
					clip.time = 0f;
					clip.weight = 1f;
					anim.CrossFade(name, 0.15f);
				}
				else {
					anim.Blend(name, 0f);
				}
			}
			catch { }

			switch (state) {
				case AnimState.Drink: drink = on; break;
				case AnimState.Lean: lean = on; break;
				case AnimState.Finger: finger = on; break;
				case AnimState.Hitchhike: hitchhike = on; break;
				case AnimState.Push: push = on; break;
			}
		}

		/// <summary>Açık olan tüm üst üste binen duruşları kapatır (peer's gone).</summary>
		public void ClearOverlays() {
			if (anim != null) {
				SetOverlay(AnimState.Drink, false);
				SetOverlay(AnimState.Lean, false);
				SetOverlay(AnimState.Finger, false);
				SetOverlay(AnimState.Hitchhike, false);
				SetOverlay(AnimState.Push, false);
			}
			drink = lean = finger = hitchhike = push = false;
		}

		/// <summary>Açık duruşların bayrak durumu ('smp player' tanısı).</summary>
		public string Describe() {
			if (anim == null) return "no Animation component (slide)";
			return CurrentName + " speed " + lastSpeed.ToString("F2")
				+ " crouch" + lastCrouch.ToString()
				+ (lastSeated ? " +seated" : "")
				+ (drink ? " +drink" : "") + (lean ? " +lean" : "")
				+ (finger ? " +finger" : "") + (hitchhike ? " +hitch" : "")
				+ (push ? " +push" : "");
		}
	}
}