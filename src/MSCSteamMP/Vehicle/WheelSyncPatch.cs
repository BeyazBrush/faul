using System;
using System.IO;
using System.Reflection;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Araç girdilerinin okunması: gaz, direksiyon, fren ve devir.
	///
	/// Neden transform kopyalamak yetmiyor
	/// ----------------------------------
	/// Uzak aracın Rigidbody'si kinematik yapılıp konumu doğrudan kopyalanır. Bu, aracı
	/// doğru yerde gösterir ama oyuncuya yanlış hissettirir: direksiyon tepkisi yoktur,
	/// kavrama kaybı yaşanmaz, motor sesi ve devir bilgisi yanlıştır. Sürücünün *girdisi*
	/// küçük bir veridir ve karşı tarafta aynı fiziği üretmek için kullanılabilir.
	///
	/// Neden HOOK değil POLLING, neden TEKERLEK değil KONTROLÖR
	/// --------------------------------------------------------
	/// İki ölü varsayım log + dnSpy kanıtıyla gömüldü:
	///
	/// 1) WheelCollider.set_* hook'ları: native (extern) metotlar, IL gövdesi
	///    YOK → Harmony 1.2 "has no body" diye reddetti. Tekerlek girdisi
	///    ağa HİÇ GİTMİYORDU.
	///
	/// 2) Tekerlek varsayımı: Satsuma dahil MSC araçlarında WheelCollider
	///    YOK (tüm asset'lerde 2 adet). Araçlar özel fizik kullanıyor:
	///    `AxisCarController : CarController` (dnSpy, Assembly-CSharp).
	///    Gerçek durum şurada:
	///      CarController.throttleInput / brakeInput / steerInput   (public float)
	///      CarController.throttle / brake / steering                (public float)
	///      Drivetrain.rpm (public float), Drivetrain.gear (public int)
	///    `drivetrain` alanı protected olduğu için yansıma (reflection) ile
	///    okunur; FieldInfo bir kez çözülüp saklanır.
	///
	/// Tekerlek yolu yedek olarak durur: CarController bulunamazsa ama
	/// WheelCollider varsa eski polling çalışır.
	///
	/// Sunucu/istemci ilişkisi
	/// -----------------------
	/// Bu mod P2P'tir ve kaynak oyuncudur: girdi yalnızca düğmeye basılan
	/// tarafta okunur. Karşı tarafta uzaktan sürülen araç kinematik olduğu için
	/// okunan değerin fiziksel bir etkisi yoktur; yine de ileride istemci
	/// tarafı tahmin (prediction) eklenirse aynı veri doğrudan kullanılabilir.
	/// </summary>
	public static class WheelSync {

		/// <summary>Bir araçta izlenen tekerlek sayısı üst sınırı (yedek yol).</summary>
		public const int MAX_WHEELS = 4;
		/// <summary>Gönderim aralığı, saniye. 20 Hz, araç dönüşümüyle aynı ritim.</summary>
		public const float SEND_INTERVAL = 0.05f;

		/// <summary>Değişim eşikleri: altında kalan titreşim gönderilmez.</summary>
		public const float INPUT_EPSILON = 0.02f;
		public const float RPM_EPSILON = 50f;
		public const float TORQUE_EPSILON = 5f;
		public const float STEER_EPSILON = 0.1f;

		/// <summary>Gaz girdisi (-1..1). Paket sırası 1/4 (eski adıyla tork).</summary>
		public static float TotalTorque;
		/// <summary>Direksiyon girdisi (-1..1). Paket sırası 2/4.</summary>
		public static float SteerAngle;
		/// <summary>Fren girdisi (0..1). Paket sırası 3/4.</summary>
		public static float TotalBrake;
		/// <summary>Motor devri. Paket sırası 4/4.</summary>
		public static float PeakRpm;
		/// <summary>Son WheelState paketinin yaşı: bayatsa ses yazılmaz.</summary>
		static float lastRecvTime = -100f;
		/// <summary>Gelen devir taze mi (2 sn)? Donmuş devirle ses ötmez.</summary>
		public static bool RpmFresh {
			get {
				try { return Time.time - lastRecvTime < 2f; }
				catch { return false; }
			}
		}
		/// <summary>Gelen fren taze mi (2 sn)? Bayat frenle lamba yanmaz.</summary>
		public static bool BrakeFresh {
			get {
				try { return Time.time - lastRecvTime < 2f; }
				catch { return false; }
			}
		}
		/// <summary>İzlenen tekerlek sayısı (yedek yolda; kontrolör yolunda 0).</summary>
		public static int WheelCount { get { return count; } }
		/// <summary>Veri içeren kare sayısı (telemetri).</summary>
		public static int Samples;
		/// <summary>Veri kaynağı: "controller", "wheels" ya da "none".</summary>
		public static string Source {
			get {
				if (controller != null) return "controller";
				if (count > 0) return "wheels";
				return "none";
			}
		}

		static CarController controller;
		static WheelCollider[] wheels = new WheelCollider[MAX_WHEELS];
		static int count;
		static float nextSend;
		static bool pending;
		static bool hasBaseline;

		static float lastSentTorque;
		static float lastSentSteer;
		static float lastSentBrake;
		static float lastSentRpm;

		// Yansıma önbelleği: her karede GetField yok.
		static FieldInfo drivetrainField;
		static FieldInfo rpmField;
		static bool reflectionProbed;

		/// <summary>
		/// Sürücün bindiği aracın kumandasını izlemeye başlar. Önce CarController
		/// (AxisCarController/Arcader dahil, taban sınıf polimorfizmi), yoksa
		/// WheelCollider yedek yolu. Araç değişince tekrar çağrılır.
		/// </summary>
		public static int Track(Transform vehicleRoot) {
			Clear();
			if (vehicleRoot == null) return 0;

			CarController[] controllers;
			try { controllers = vehicleRoot.GetComponentsInChildren<CarController>(true); }
			catch { controllers = null; }
			if (controllers != null) {
				for (int i = 0; i < controllers.Length; i++) {
					if (controllers[i] != null) {
						controller = controllers[i];
						break;
					}
				}
			}
			if (controller != null) {
				ProbeReflection();
				SmpLog.Log("tracking car controller ("
					+ controller.GetType().Name + ") on '" + vehicleRoot.name + "'.");
				// Gorsel mesh'leri de bul: kinematik gvede CarController fizigi
				// tekerlek mesh'ini dondurmuyor (bkz. ProbeVisuals notu).
				if (!visualProbed) { visualProbed = true; ProbeVisuals(vehicleRoot); }
				return 1;
			}

			WheelCollider[] found = vehicleRoot.GetComponentsInChildren<WheelCollider>(true);
			for (int i = 0; i < found.Length && count < MAX_WHEELS; i++) {
				if (found[i] == null) continue;
				wheels[count++] = found[i];
			}
			if (count == 0) {
				SmpLog.Warning("'" + vehicleRoot.name + "': no CarController and no "
					+ "WheelColliders; wheel input sync disabled on it.");
				return 0;
			}
			SmpLog.Log("tracking " + count + " wheel(s) on '" + vehicleRoot.name + "'.");
			// Gorsel mesh'leri de bul (kinematik gvede fizik dondurmuyor).
			if (!visualProbed) { visualProbed = true; ProbeVisuals(vehicleRoot); }
			return count;
		}

		// ------------------------------------------------------------------
		// TEKERLEK GORSELI (kinematik govede mesh donmez)
		// ------------------------------------------------------------------
		//
		// KULLANICI BELIRTISI: "direksiyonu cevirdim, karsi tarafta ic
		// direksiyon donuyor ama TEKERLER donmuyor".
		//
		// NEDEN: `ApplyToRemote` yalniz `CarController.steering` alanina
		// yaziyor ve "oyunun kodu mesh'i bundan donduruyor" varsayimi
		// UZAKTAN ARAC ICIN GECERSIZ: govde kinematik yapiliyor
		// (SyncedVehicle.ApplyTransform) ve arac transform paketleriyle
		// tasiniyor; kinematik govdede fizik adimi tekerlek mesh'ini
		// DONDURMEZ. Sonuc: dolgu doner, tekerlekler duz kalir.
		//
		// DUMPER KANITI (dump2/level2.json, SATSUMA teker hiyerarsisi):
		//   SATSUMA/FL/AckerFL/wheelFL/TireFL/...   fsm=SetGrip, SpindleFL
		//   SATSUMA/FR/AckerFR/wheelFR/TireFR/...
		//   SATSUMA/RL/wheelRL/TireRL/...            (arkada Acker YOK)
		//   SATSUMA/RR/wheelRR/TireRR/...
		// Yani  "Acker*" = direksiyon pivotu (Y),  "Tire*" = lastik mesh (X).
		// Adlar tahmin EDILMEDI, dumper'dan alindi.
		//
		// INDIS: her gorsel icin taban yerel donusu bir kez saklanir;
		// sonra `base * Euler(roll,0,0)` ve `base * Euler(0,steer,0)`
		// yazilir. Boylece parca degisiminde mesh kaymaz. Bulunamazsa
		// sessizce devre disi kalir; mevcut steering yolu degismez.

		const int MAX_VISUALS = 8;

		static Transform[] steerPivots = new Transform[MAX_VISUALS];
		static Transform[] rollMeshes = new Transform[MAX_VISUALS];
		static Quaternion[] steerBase = new Quaternion[MAX_VISUALS];
		static Quaternion[] rollBase = new Quaternion[MAX_VISUALS];
		static bool visualProbed;

		/// <summary>Toplam lastik devir acisi (derece). Gonderici biriktirir,
		/// alici mesh'e dogrudan yazar.</summary>
		public static float RollDeg;
		/// <summary>Gondericide biriken devir acisi (derece).</summary>
		static float rollDeg = float.NaN;

		/// <summary>Bulunan tekerlek gorseli sayisi (tani).</summary>
		public static int VisualCount;
		/// <summary>Son yazilan direksiyon acisi (derece).</summary>
		public static float WrittenSteer;
		/// <summary>Son yazilan devir acisi (derece).</summary>
		public static float WrittenRoll;
		/// <summary>Mesh'e yazma kare sayisi (yolun calistiginin kaniti).</summary>
		public static int WrittenFrames;

		/// <summary>
		/// Direksiyon pivotu + lastik mesh transformlarini bulur (araç başına
		/// bir kez). Bulamazsa sorun degildir.
		/// </summary>
		static void ProbeVisuals(Transform vehicleRoot) {
			VisualCount = 0; WrittenFrames = 0;
			for (int i = 0; i < MAX_VISUALS; i++) {
				steerPivots[i] = null; rollMeshes[i] = null;
				steerBase[i] = Quaternion.identity; rollBase[i] = Quaternion.identity;
			}
			if (vehicleRoot == null) return;
			try {
				Transform[] all = vehicleRoot.GetComponentsInChildren<Transform>(true);
				for (int i = 0; all != null && i < all.Length; i++) {
					Transform t = all[i];
					if (t == null || VisualCount >= MAX_VISUALS) continue;
					string n = t.name;
					if (n == null || !n.StartsWith("Tire", StringComparison.OrdinalIgnoreCase)) continue;
					// "TireFlatRollingSound" bir SESTIR, gorsel degil.
					if (n.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) >= 0) continue;

					rollMeshes[VisualCount] = t;
					rollBase[VisualCount] = t.localRotation;

					// Direksiyon pivotu: "AckerFL" (TireFL -> wheelFL -> AckerFL)
					Transform steer = null;
					Transform p = t.parent;
					int guard = 0;
					while (p != null && guard++ < 4) {
						string pn = p.name;
						if (pn != null && pn.StartsWith("Acker", StringComparison.OrdinalIgnoreCase)) {
							steer = p; break;
						}
						if (pn != null && pn.StartsWith("wheel", StringComparison.OrdinalIgnoreCase)
							&& p.parent != null && p.parent.name != null
							&& p.parent.name.StartsWith("Acker", StringComparison.OrdinalIgnoreCase)) {
							steer = p.parent; break;
						}
						p = p.parent;
					}
					if (steer != null) {
						steerPivots[VisualCount] = steer;
						steerBase[VisualCount] = steer.localRotation;
					}
					VisualCount++;
				}
			}
			catch { }

			SmpLog.Print("wheel visuals on '" + vehicleRoot.name + "': " + VisualCount
				+ " tire(s), " + CountSteerPivots() + " steering pivot(s). "
				+ (VisualCount > 0
					? "peer tekerlek donumu mesh'e dogrudan yazilacak."
					: "bulunamadi; yalniz CarController.steering yolu kullanilacak."));
		}

		static int CountSteerPivots() {
			int n = 0;
			for (int i = 0; i < MAX_VISUALS; i++) if (steerPivots[i] != null) n++;
			return n;
		}

		/// <summary>
		/// Kinematik gövedeki tekerlek mesh'lerine peer girdisini yazar.
		/// `steerNorm`: -1..1 kumanda girdisi. `rollDeg`: toplam devir acisi.
		/// </summary>
		public static void ApplyVisuals(float steerNorm, float rollAngle) {
			if (VisualCount == 0) return;
			float age = lastRecvTime > 0f ? Time.time - lastRecvTime : -1f;
			if (age < 0f || age > 1.5f) steerNorm = 0f;
			WrittenFrames++;

			// Direksiyon acisi: kumanda girdisi -1..1 -> en fazla 30 derece.
			// MSC'nin kendi Acker limiti de bu civarda (dumper: wheel_offset).
			float steerDeg = steerNorm * 30f;
			if (steerDeg > 30f) steerDeg = 30f;
			if (steerDeg < -30f) steerDeg = -30f;
			WrittenSteer = steerDeg;
			WrittenRoll = rollAngle;

			for (int i = 0; i < VisualCount; i++) {
				Transform roll = rollMeshes[i];
				if (roll == null) continue;
				try { roll.localRotation = rollBase[i] * Quaternion.Euler(rollAngle, 0f, 0f); }
				catch { }
			}
			for (int i = 0; i < VisualCount; i++) {
				Transform p = steerPivots[i];
				if (p == null) continue;
				try { p.localRotation = steerBase[i] * Quaternion.Euler(0f, steerDeg, 0f); }
				catch { }
			}
		}

		/// <summary>protected CarController.drivetrain + Drivetrain.rpm alanlarını bir kez çözer.</summary>
		static void ProbeReflection() {
			if (reflectionProbed) return;
			reflectionProbed = true;
			try {
				drivetrainField = typeof(CarController).GetField("drivetrain",
					BindingFlags.NonPublic | BindingFlags.Instance);
				if (drivetrainField != null) {
					rpmField = drivetrainField.FieldType.GetField("rpm",
						BindingFlags.Public | BindingFlags.Instance);
				}
			}
			catch {
				drivetrainField = null;
				rpmField = null;
			}
			if (drivetrainField == null || rpmField == null) {
				SmpLog.Warning("CarController.drivetrain.rpm reflection failed; "
					+ "rpm will read 0.");
			}
		}

		/// <summary>Dünya değişiminde referansları bırakır.</summary>
		public static void Clear() {
			for (int i = 0; i < MAX_WHEELS; i++) wheels[i] = null;
			count = 0;
			controller = null;
			// Gorsel indeksi de birak: arac degisti.
			for (int i = 0; i < MAX_VISUALS; i++) {
				steerPivots[i] = null; rollMeshes[i] = null;
				steerBase[i] = Quaternion.identity; rollBase[i] = Quaternion.identity;
			}
			VisualCount = 0; WrittenFrames = 0; visualProbed = false;
			RollDeg = 0f; rollDeg = float.NaN;
			TotalTorque = 0f;
			SteerAngle = 0f;
			TotalBrake = 0f;
			PeakRpm = 0f;
			pending = false;
			hasBaseline = false;
		}

		/// <summary>
		/// Kare başına bir kez: kumandadan okur, değiştiyse 20 Hz ritimde gönderir.
		///
		/// Ham GİRDİLER okunur (throttleInput/brakeInput/steerInput), işlenmiş
		/// değerler değil: karşı taraf neye basıldığını görür, fizik kendi
		/// tarafında aynı tepkiyi üretir.
		/// </summary>
		public static void Update() {
			float torque = 0f, brake = 0f, steer = 0f, rpm = 0f;
			bool haveData = false;

			if (controller != null) {
				try {
					torque = controller.throttleInput;
					brake = controller.brakeInput;
					steer = controller.steerInput;
					if (drivetrainField != null && rpmField != null) {
						object dt = drivetrainField.GetValue(controller);
						if (dt != null) rpm = (float)rpmField.GetValue(dt);
					}
					haveData = true;
				}
				catch (Exception e) {
					// Kumanda yok edildi (sahne değişimi): referansı bırak,
					// dünya olayları zaten Clear çağıracak.
					if (Telemetry.Verbose) SmpLog.Warning("controller read failed: " + e.Message);
					controller = null;
					return;
				}
			}
			else if (count > 0) {
				float steerSum = 0f;
				int steerN = 0;
				int valid = 0;
				for (int i = 0; i < count; i++) {
					WheelCollider w = wheels[i];
					if (w == null) continue;
					try {
						torque += w.motorTorque;
						brake += w.brakeTorque;
						float s = w.steerAngle;
						if (s > STEER_EPSILON || s < -STEER_EPSILON) {
							steerSum += s;
							steerN++;
						}
						float r = w.rpm;
						if (r < 0f) r = -r;
						if (r > rpm) rpm = r;
						valid++;
					}
					catch (Exception e) {
						if (Telemetry.Verbose) SmpLog.Warning("wheel poll failed: " + e.Message);
					}
				}
				if (valid == 0) return;
				steer = steerN > 0 ? steerSum / steerN : 0f;
				haveData = true;
			}
			else {
				return;
			}

			if (!haveData) return;
			Samples++;
			TotalTorque = torque;
			SteerAngle = steer;
			TotalBrake = brake;
			PeakRpm = rpm;

			if (!SteamTransport.HasRemote) return;

			// Eşikler yola göre: kontrolör girdileri -1..1 aralığında küçük
			// epsilon, tekerlek torkları yüzlü değerlerde büyük epsilon.
			bool byController = controller != null;
			float inEps = byController ? INPUT_EPSILON : TORQUE_EPSILON;
			float steerEps = byController ? INPUT_EPSILON : STEER_EPSILON;
			float rpmEps = byController ? RPM_EPSILON : 5f;

			if (!hasBaseline) {
				pending = true;
			}
			else if (Abs(torque - lastSentTorque) > inEps
				|| Abs(brake - lastSentBrake) > inEps
				|| Abs(steer - lastSentSteer) > steerEps
				|| Abs(rpm - lastSentRpm) > rpmEps) {
				pending = true;
			}
			if (!pending) return;
			if (Time.time < nextSend) return;
			nextSend = Time.time + SEND_INTERVAL;

			// DEVIR BIRIKTIRME: gorsel mesh donumu icin toplam aciyi
			// tutmamiz gerekiyor. Gonderici tarafta tekerlek kendi
			// fizigiyle doner; biz yalniz ACIKI kovalariz (rpm -> derece).
			// Satsuma lastigi 155/70R13 => yaricap ~0.32 m.
			if (!float.IsNaN(rollDeg)) {
				const float radius = 0.32f;
				float circumference = 2f * 3.14159265f * radius;
				float speed = rpm / 60f * circumference;   // m/s
				rollDeg += speed / radius * Mathf.Rad2Deg * Time.deltaTime;
				// 3600 derece = 10 tur: hassasiyet yeterli, sayi tasmaz.
				if (rollDeg > 3600f) rollDeg -= 3600f;
				if (rollDeg < -3600f) rollDeg += 3600f;
			}
			float rollOut = float.IsNaN(rollDeg) ? 0f : rollDeg;

			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.WheelState);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(torque);
				w.Write(steer);
				w.Write(brake);
				w.Write(rpm);
				w.Write(rollOut);
				if (SteamTransport.SendUnreliable(Protocol.Finish(s))) Telemetry.SentWheelStates++;
				lastSentTorque = torque;
				lastSentSteer = steer;
				lastSentBrake = brake;
				lastSentRpm = rpm;
				hasBaseline = true;
				pending = false;
			}
			catch (Exception e) {
				SmpLog.Warning("wheel send failed: " + e.Message);
			}
		}

		static float Abs(float v) { return v < 0f ? -v : v; }

		/// <summary>
		/// Karşı taraftan gelen girdiler. Kinematik bir araçta fiziksel etkisi yoktur, ama
		/// gösterge ve ses için saklanır; istemci tarafı tahmin eklendiğinde aynı değer
		/// doğrudan tekerleklere yazılabilir.
		/// </summary>
		public static void OnMessage(BinaryReader r) {
			try {
				float torque = r.ReadSingle();
				float steer = r.ReadSingle();
				float brake = r.ReadSingle();
				float rpm = r.ReadSingle();
				TotalTorque = torque;
				SteerAngle = steer;
				TotalBrake = brake;
				PeakRpm = rpm;
				// Sonraki paketlerde eski surumden kalsa diye SIFIR varsayimi:
				// okunamayan paket donus acisini bozmaz.
				float roll = 0f;
				try { roll = r.ReadSingle(); } catch { }
				RollDeg = roll;
				try { lastRecvTime = Time.time; } catch { }
				Telemetry.ReceivedWheelStates++;
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("wheel packet rejected: " + e.Message);
			}
		}

/// <summary>Son uygulanan peer direksiyonu (tani).</summary>
		public static float AppliedSteer;
		/// <summary>Son uygulanan peer direksiyonunun ZAMANI.</summary>
		public static float AppliedAt;
		/// <summary>
		/// Kaç kere peer direksiyonu bir araca YAZILDI.
		/// Uygulama yolu çalışıyor mu diye kanıt: "direksiyon çevirdim,
		/// arkadaşımda görünmüyor" belirtisinde tek soru budur. Sayaç 0
		/// ise ya paket gelmiyordur ya da hedef araç bulunamıyordur.
		/// </summary>
		public static int AppliedCount;

		/// <summary>
		/// Tek satır teşhis: peer direksiyonu gerçekten UYGULANDI mı?
		/// "Direksiyonu çevirdim, arkadaşımda görünmüyor" belirtisinde
		/// ayırt edilecek iki şey vardır: paket geliyor mu (sent/in) ve
		/// gelen değer araca yazılıyor mu (applied). İkisi de burada.
		/// </summary>
		public static string DescribeApplied() {
			float age = AppliedAt > 0f ? Time.time - AppliedAt : -1f;
			return "steer applied: " + AppliedCount + "x, last "
				+ AppliedSteer.ToString("F2")
				+ (age < 0f ? " (never)" : " (" + age.ToString("F1") + "s ago)")
				+ ", received=" + Telemetry.ReceivedWheelStates
				+ " | mesh: " + VisualCount + " tire(s), written " + WrittenFrames
				+ "x, steer=" + WrittenSteer.ToString("F1")
				+ "deg roll=" + WrittenRoll.ToString("F0") + "deg";
		}

		// ------------------------------------------------------------------
		// UZAKTAN SURULEN ARACA GIRDI UYGULAMA
		// ------------------------------------------------------------------

		/// <summary>
		/// Peer'in direksiyonunu yerel aracin kontrolorune yazar.
		///
		/// CANLI KANIT: "hayosikoyu surdum host olarak direksiyonu cevirdigimde
		/// arkadasimda gorunmuyor, sag sola ceviriyorum". Logda 2.5 dakika
		/// surus boyunca HICBIR tekerlek/girdi kaydi yok.
		///
		/// SEBEP: OnMessage gelen degerleri yalnizca SAKLIYORDU ("gosterge ve
		/// ses icin"), hicbir yere UYGULAMIYORDU. Uzaktan surulen arac
		/// kinematik oldugu icin fiziksel etkisi yok -- dogru -- ama gorsel
		/// tekerlek donusu de yoktu, yani araba duz gidiyordu.
		///
		/// UYGULAMA: CarController.steering (islenmis alan, public float).
		/// Oyunun kendi kodu da tekerlek mesh'lerini bu degerden donduruyor.
		/// Gaz/fren YAZILMAZ: kinematik govdede ise yaramaz ve yerel aracin
		/// motor sesini bozardi.
		/// </summary>
		public static void ApplyToRemote(CarController ctrl) {
			if (ctrl == null) return;
			try {
				if (Time.time - lastRecvTime > 1.5f) {
					// Peer artik girdi gondermiyor: tekerlekleri notrle.
					if (Mathf.Abs(ctrl.steering) > 0.001f) ctrl.steering = 0f;
					return;
				}
				if (Mathf.Abs(ctrl.steering - SteerAngle) > 0.001f) {
					ctrl.steering = SteerAngle;
					AppliedSteer = SteerAngle;
					AppliedAt = Time.time;
					AppliedCount++;
				}
				// GORSEL: kinematik govede `CarController.steering` tek
				// basina mesh'i dondurmuyor (fizik adimi calismiyor), o yuzden
				// tekerleri kendimiz yaziyoruz. `RollDeg` gondericinin
				// biriktirdigi toplam devir acisidir.
				ApplyVisuals(SteerAngle, RollDeg);
			}
			catch { }
		}

		/// <summary>Tek satırlık durum.</summary>
		public static string Describe() {
			if (controller == null && count == 0) return "wheels: none tracked";
			string what = controller != null
				? "controller (" + controller.GetType().Name + ")"
				: count + " wheel(s)";
			return "wheels: " + what
				+ (Samples > 0 ? ", " + Samples + " sample(s)" : "")
				+ ", t=" + TotalTorque.ToString("F2")
				+ ", steer=" + SteerAngle.ToString("F2")
				+ ", rpm=" + PeakRpm.ToString("F0");
		}
	}
}
