using System;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// UZAK OYUNCUNUN GÖVDESİ: interpolasyon, animasyon, koltuk.
	///
	/// İnterpolasyon neden gerekiyor
	/// ----------------------------
	/// Paketler 20 Hz, ekran 60-300 Hz. Paketi doğrudan kopyalamak kuklayı 20 Hz'de
	/// sıçratır. Burada hedefe doğru sabit hızda yaklaşılır:
	///
	///     Vector3.Lerp / Quaternion.Slerp, 10/s
	///
	/// 10/s bir tween hızı: 100 ms'de hedefe %63, 300 ms'de %95. Paketler 50 ms
	/// arayla geldiği için asla belirgin biçimde geride kalmaz.
	///
	/// 8 m sıçrama kuralı
	/// ------------------
	/// Yeniden yüklenen sahne ya da oturum başındaki ilk paket, aradaki mesafeyi
	/// normal bir geçiş olsaydı harita boyunca "kayma" olarak görünürdü. 8 m'den
	/// büyük fark atlama sayılır ve kukla doğrudan hedefe konur.
	///
	/// Koltuk
	/// ------
	/// Kukla koltuğa PARENT olur, böylece araç dönerken kukla da döner ve
	/// süspansiyon hareketini izler. Bağlandığında konum interpolasyonu durur,
	/// çünkü artık konumu koltuk yazar. Aynı sebepten "koltuk" ayrı bir bayrakla
	/// tutulur: Unity, yok edilmiş bir Transform'u null'a eşit sayar, yani
	/// `seat == null` kontrolü "hiç bağlanmamış" ile "koltuk yok oldu" durumlarını
	/// ayıramaz.
	///
	/// Bu sınıf MonoBehaviour ve Update()'i doğrudan Unity çağırır: gövde bir sahne
	/// nesnesidir ve sahne değişiminde kendiliğinden yok olur.
	/// </summary>
	public class RemotePlayerBehaviour : MonoBehaviour {

		/// <summary>Hedefe yaklaşma hızı (1/s). Kare hızından BAĞIMSIZ.</summary>
		public const float LERP_RATE = 10f;
		/// <summary>Bu mesafeden büyük fark atlama sayılır.</summary>
		public const float SNAP_DISTANCE = 8f;

		/// <summary>
		/// Ağ 20 Hz, ekran 200-300 Hz. Hedefe düz yumuşatma "her paket gelene
		/// kadar dur, sonra hedefe doğru süzül" hareketi üretir; bu da
		/// SANİYEDE 20 KERE görünür sıçrama demektir ve yüksek kare hızında
		/// daha kötü görünür (200 Hz monitör = 200 kare, aynı 20 adım).
		///
		/// Çözüm: son iki paketten hız çıkarılır ve paketler arası bu hızla
		/// İLERİ KAYDIRILIR (extrapolation). Böylece kukla paket gelene
		/// kadar gerçek hızında hareket eder, duraklama olmaz.
		///
		/// TAVAN: en fazla 120 ms ileri bakılır. Oyuncu durunca ya da
		/// yön değiştirince kukla çok uzağa koşmasın diye fazlası
		/// yazılmaz; ayrıca yeni paket gelince hız yeniden ölçülür.
		/// </summary>
		public const float MAX_EXTRAPOLATE = 0.12f;
		/// <summary>Ölçülen hızın kabul edileceği en büyük değer (m/s).</summary>
		public const float MAX_PEER_SPEED = 12f;

		// ------------------------------------------------------------------
		// Gelen veri
		// ------------------------------------------------------------------

		Vector3 targetPosition;
		Quaternion targetRotation = Quaternion.identity;
		/// <summary>Prefab kökünün kendi dönüşü (mesh Z->Y düzeltmesi).
		/// Ham ağ dönüşünün SAĞINDA çarpılır. NPC modelinde identity
		/// değildir; MPPlayerModel yedeğinde kimliktir.</summary>
		Quaternion modelRotation = Quaternion.identity;
		Quaternion targetModelRotation = Quaternion.identity;

		/// <summary>Kök dönüşünü ham ağ dönüşü + prefab düzeltmesi olarak yazar.
		/// Bütün dönüş yazma noktaları BUNU çağırmalı; doğrudan
		/// transform.rotation = ... yazmak prefab düzeltmesini siler ve
		/// modeli yana yatırır.</summary>
		/// <summary>
		/// Ag kuaterniyonlari isaret degistirebilir (q ve -q ayni donus).
		/// Slerp isareti duzeltmez, uzun yoldan 360 dondurur ("arabaya
		/// binince 360 donuyor" sikayeti). Hedefi kisa yola cevirir.
		/// </summary>
		static Quaternion ShortestTarget(Quaternion from, Quaternion to) {
			try { if (Quaternion.Dot(from, to) < 0f) return new Quaternion(-to.x, -to.y, -to.z, -to.w); } catch { }
			return to;
		}

		void ApplyModelRotation(Quaternion netRotation) {
			transform.rotation = netRotation * modelRotation;
		}

		/// <summary>
		/// HAM dönüş hedefi (model düzeltmesi YOK).
		///
		/// NEDEN AYRI: <see cref="ApplyPose"/> gelen dönüşü
		/// ApplyModelRotation ile bir daha çarpıyor, yani beklediği ham
		/// dönüştür. Model düzeltmesi İÇEREN bir dönüş verilirse düzeltme
		/// iki kez uygulanır ve gövde ters döner (canlı hata).
		/// </summary>
		public Quaternion RawTargetRotation() {
			return targetRotation;
		}

		/// <summary>
		/// YALNIZCA gövde dönüşünü hedefler. Konum, hız ve dikey
		/// interpolasyona dokunulmaz.
		///
		/// Kullanım: duran bir kuklayı oyuncuya döndürmek için. Konumu
		/// transform.position verip ApplyPose çağırmak YANLIŞTIR — ApplyPose
		/// konumu GroundLift kadar kaldırır, yani kukla her kare biraz daha
		/// yukarı çıkar (canlı hata: bot havaya uçtu).
		/// </summary>
		public void SetFacingOnly(Quaternion netRotation) {
			targetRotation = netRotation;
		}

		/// <summary>Hedef kök dönüşü (Slerp hedefi).</summary>
		Quaternion TargetRotation {
			get { return targetRotation * targetModelRotation; }
		}
		float targetSpeed;

		// Extrapolasyon: son paket konumu + zamana göre hız.
		Vector3 extrapFrom;
		float extrapAt;
		Vector3 peerVelocity;

		/// <summary>Son konum paketinin geldiği an. Zaman aşımı tespiti için.</summary>
		float lastPacketTime;

		// Durum bayrakları (PlayerState paketinden).
		bool seated;
		bool dead;
		byte crouch;

		// Koltuk
		bool attached;
		bool lastFingerG;
		bool lastSmokingG;
		Transform seat;
		Vector3 seatLocalPosition;
		Quaternion seatLocalRotation;
		Transform pelvisBone;
			int seatCalibLeft;
			float nextSeatCalib;

		PlayerAnimManager anim;

		/// <summary>Animasyon yöneticisi (tanı ve komutlar için).</summary>
		public PlayerAnimManager Anim { get { return anim; } }

		// Dikey hız izleme: ağ paketi havada olup olmadığını söylemez, bu
		// yüzden enterpolasyonlu konumdan türetilir. Yürürken yokuşta oluşan
		// küçük dikey hızlar yumuşatma ile elenir; zıplama/düşme belli olur.
		float lastY;
		float smoothVy;
		bool yInit;
		/// <summary>Havada sayılma eşiği (m/s, yumuşatılmış dikey hız).</summary>
		public const float AIRBORNE_VY = 2.5f;

		/// <summary>Kukla şu an bir koltuğa mı bağlı?</summary>
		public bool Seated { get { return attached; } }

		/// <summary>Bayraklardan gelen oturma isteği (koltuk henüz bağlanmamış olabilir).</summary>
		public bool WantsSeat { get { return seated; } }

		/// <summary>Son konum paketinin yaşı (saniye).</summary>
		public float PacketAge { get { return Time.time - lastPacketTime; } }

		/// <summary>Gelen hız (m/s). Yalnızca animasyon seçimi için.</summary>
		public float Speed { get { return targetSpeed; } }

		/// <summary>Tek satır durum.</summary>
		public string Describe() {
			return (attached ? "seated on '" + seat.name + "'" : "on foot")
				+ " at " + targetPosition.ToString("F1")
				+ ", packet " + PacketAge.ToString("F2") + "s old"
				+ (dead ? " DEAD" : "")
				+ (anim != null ? ", " + anim.Describe() : "");
		}

		// ------------------------------------------------------------------

		/// <summary>Factory çağırır: animasyonu hazırlar ve ilk konumu sabitler.</summary>
		/// <param name="npcModel">Model oyunun kendi NPC'sinden mi çıkarıldı?
		/// true ise clip listesi NPC'ye göre daraltılır ve Run, hızlandırılmış
		/// Walk olarak oynatılır.</param>
		public void Setup(bool npcModel = false) {
			anim = new PlayerAnimManager();
			anim.Setup(gameObject, npcModel);

			// Prefab kök dönüşü HATASI.
			//
			// Doğurma anında RemotePlayerFactory ham ağ dönüşünü prefab'ın
			// kendi dönüşüyle birleştiriyordu:
			//     Instantiate(prefab, position, rotation * PrefabLocalRotation)
			// Ama HER paket geldiğinde ApplyState şunları YAZIYOR:
			//     transform.rotation = rotation;              (snap, 263-271)
			//     targetRotation  = rotation;                (297)
			//     Slerp(transform.rotation, targetRotation)  (180)
			// Hepsi ham ağ dönüşü — prefab'ın yön düzeltmesi BİR DAHA
			// KAYBOLUYOR. Yani doğru dönüş yalnızca ilk karede vardı,
			// ilk yer güncellemesinden sonra model mesh'in authored
			// yönüne (gövde Z ekseni) dönüyor ve HARİTADA YANA YATIYOR.
			//
			// Kök neden: "kök dönüşü = ham ağ dönüşü" varsayımı. Doğrusu
			// ayrı bir katmanda tutmak. Aşağıdaki ApplyModelRotation
			// HER yazma noktasında bunu uygular.
			modelRotation = npcModel
				? PlayerAssetNpc.PrefabLocalRotation
				: Quaternion.identity;
			targetModelRotation = modelRotation;
			CaptureHeadRest();
			ProbeArm();
			// Doğurma dönüşünü doğrulamak için: transform.rotation'ı
			// ham ağ dönüşünün hedefi yap. 'rotation' değişkeni ApplyState'in
			// parametresidir, Setup'ta yok; doğurma rotasyonu zaten
			// transform.rotation'a yazılmış durumdadır.
			ApplyModelRotation(transform.rotation * Quaternion.Inverse(modelRotation));

			lastPacketTime = Time.time;
			lastY = transform.position.y;
			smoothVy = 0f;
			yInit = true;
			// Doğurma konumu telafili; ham ağ konumu ilk paket gelene kadar
			// bilinmiyor, ama mesafe kontrolü sıfıra düşmesin diye kukla kökü
			// telafiden geri çevrilerek başlatılır.
			netPosition = transform.position - new Vector3(0f, GroundLift, 0f);
			// Extrapolasyon tabanı: ilk paket gelene kadar hedef = doğurma
			// konumu, hız sıfır. Aksi hâlde ilk karede hedef (0,0,0)'a
			// çekilirdi.
			extrapFrom = transform.position;
			extrapAt = Time.time;
			peerVelocity = Vector3.zero;

			// Durağan ikinci ölçüm zamanlayıcısı (bkz. MeasureSteadyGap).
			steadyGapAt = Time.time + 2f;
		}

		// ----- Durağan ikinci ölçüm -----------------------------------
		//
		// Doğurma anındaki ölçüm (RemotePlayerFactory) yeni eklenmiş
		// mesh'in BAYAT renderer bounds'ını kullanabiliyor. 2 saniye
		// sonra kukla yerleşmiş ve sakin duruyor; o anki ölçüm
		// kullanıcının EKRANDA GÖRDÜĞÜ boşluğu verir.
		float steadyGapAt;

		void CheckSteadyGap() {
			if (steadyGapAt <= 0f || Time.time < steadyGapAt) return;
			steadyGapAt = 0f; // tek seferlik
			try {
				Vector3 feet = netPosition;
				float gap = RemotePlayerFactory.MeasureSteadyGap(
					gameObject, feet);
				SmpLog.Log("player model: DURAĞAN ölçüm, ayak "
					+ (float.IsNaN(gap) ? "NaN (zemin yok)" : gap.ToString("F3") + " m")
					+ " | lift=" + GroundLift.ToString("F3")
					+ " ayakY=" + feet.y.ToString("F3")
					+ " kokY=" + transform.position.y.ToString("F3"));
			}
			catch { }
		}

		void Update() {
			CheckSteadyGap();
			if (attached) {
				// Koltuk yok edilmişse (sahne değişimi, araç uçtu): çöz ve
				// kalan karelerde yeniden interpolasyona başla.
				if (seat == null) {
					attached = false;
					seat = null;
					seatCalibLeft = 0;
				}
				else CalibrateSeat();
			}
			else {
				// HEDEF: paketler arası extrapolasyon.
				//
				// CANLI KANIT (belirti): "titreme". Ağ 20 Hz, ekran 200-300 Hz.
				// Eski kod Vector3.Lerp(pos, hedef, dt * LERP_RATE) idi; bu
				// KARE HIZINA BAĞLIDIR (saniyede kaç adım atıldığı kare
				// hızına göre değişir) ve paketler gelene kadar kukla HEDEFE
				// doğru yavaşlar, durur, sonra bir sonraki pakette tekrar
				// hareket eder. 200 Hz'de saniyede 20 kez görünen "takılma"
				// tam olarak "titreme" dediğiniz şey.
				//
				// DOĞRU: hedefi paketler arası ötelenmiş konuma çek.
				// 1-exp(-k*dt) kare hızından bağımsız üstel yumuşatmadir;
				// aynı dosyanın dikey hız satırları zaten bunu kullanıyor.
				float dt2 = Time.deltaTime;
				float t = Time.time - extrapAt;
				if (t < 0f) t = 0f;
				if (t > MAX_EXTRAPOLATE) t = MAX_EXTRAPOLATE;
				Vector3 goal = extrapFrom + peerVelocity * t;

				float k = 1f - Mathf.Exp(-LERP_RATE * dt2);
				transform.position = Vector3.Lerp(transform.position, goal, k);
				transform.rotation = Quaternion.Slerp(
					transform.rotation, ShortestTarget(transform.rotation, TargetRotation),
					1f - Mathf.Exp(-LERP_RATE * 2f * dt2));
			}

			// Dikey hızdan havada/yerde ayrımı. Koltuktayken koltuk yazar,
			// sıçrama yapmasın diye yumuşatma sıfıra çekilir; lastY her kare
			// güncellenir ki koltuktan inince sahte sıçrama olmasın.
			float dt = Time.deltaTime;
			if (!yInit) { lastY = transform.position.y; smoothVy = 0f; yInit = true; }
			else if (dt > 0.0001f) {
				if (attached) {
					smoothVy += (0f - smoothVy) * (1f - Mathf.Exp(-6f * dt));
				}
				else {
					float vy = (transform.position.y - lastY) / dt;
					if (vy > 20f) vy = 20f; else if (vy < -20f) vy = -20f;
					smoothVy += (vy - smoothVy) * (1f - Mathf.Exp(-6f * dt));
				}
			}
			lastY = transform.position.y;
			bool grounded = smoothVy > -AIRBORNE_VY && smoothVy < AIRBORNE_VY;

			if (anim != null) {
				// Sürücü tespiti: koltuk adı güvenilir DEĞİL. Oyunun
				// koltukları Seat_Driver, Driver, pivot_driver gibi
				// adlar kullanıyor ama tutarsız. Ek olarak koltuk bir
				// aracın ÇOCUĞUYSA o aracın Rigidbody'si vardır; hareket
				// eden tek koltuk sürücü koltuğudur (yolcu koltukları
				// genelde araç gövdesinin parçasıdır).
				bool isDriver = false;
				float vehicleSpeed = 0f;
				if (attached && seat != null) {
					string seatName = seat.name.ToLowerInvariant();
					isDriver = seatName.Contains("driver")
						|| seatName.Contains("drive")
						|| seatName.Contains("wheel");

					// Araç hızını parent hiyerarşisinden Rigidbody ile bul
					Transform vehicleRoot = seat;
					int guard = 0;
					while (vehicleRoot != null && guard++ < 12) {
						Rigidbody rb = vehicleRoot.GetComponent<Rigidbody>();
						if (rb != null) {
							float sp = rb.velocity.magnitude;
							if (sp > vehicleSpeed) vehicleSpeed = sp;
						}
						vehicleRoot = vehicleRoot.parent;
					}
				}
				anim.Apply(targetSpeed, crouch, attached, grounded,
					isDriver, vehicleSpeed);
				// Sirali animasyon testi (smp anim all) her kare ilerler.
				try { anim.TickTour(); } catch { }
			}

			// Kafa bakışı burada UYGULANMAZ, sadece hedef hazırlanır.
			// Uygulama LateUpdate()'te yapılıyor — nedeni orada yazılı.
		}

		/// <summary>
		/// Kare sonu: kafa bakışını uygula.
		///
		/// NEDEN LATEUPDATE VE UPDATE DEĞİL (canlı hata: kafa yana bakıyor):
		/// Unity'nin kare sırası şöyle:
		///
		///     Update()                                  <- MonoBehaviour.Update
		///       PreLateUpdate.DirectorUpdateAnimationBegin <- ANIMASYON ÖRNEKLENİR
		///       LateUpdate()                            <- MonoBehaviour.LateUpdate
		///
		/// Eski sürüm kafa dönüşünü Update()'te yazıyordu; animasyon
		/// PreLateUpdate'te gelip ÜSTÜNE YAZIYORDU. Yani hesabımız ne
		/// kadar doğru olursa olsun ekrana hiç ulaşmıyor, kafa Idle klibinin
		/// pozunda kalıyordu — ve Idle klibinde kafa hafifçe yana dönük.
		/// Üç denemede de hesapta hata aradık; asıl sebep sıralama oldu.
		/// </summary>
		void LateUpdate() {
			if (!headRestCaptured || headBone == null) return;
			float dt = Time.deltaTime;
			if (dt <= 0f || dt > 0.5f) dt = 1f / 60f;
			// Yuz ekseni ONCE olculmeli: butun bakis hesabi buna dayanir.
			CaptureHeadFaceAxis();
			StepHeadSmoothing(dt);
			StepHeadBias(dt);
			StepArmReach();
		}

		// ------------------------------------------------------------------
		// Gelen veri uygulaması
		// ------------------------------------------------------------------

		/// <summary>
		/// Konum paketi geldi.
		///
		/// Y TELEFASİ HER PAKETTE UYGULANIR — bu, canlı kanıtla düzeltilmiş
		/// bir hatadır. Önceki sürüm telafiyi yalnızca doğurma anında
		/// transform'a bir kez yazıyordu:
		///
		///     body.transform.position = position - lowest      // doğurmada
		///     targetPosition            = position             // PAKETTE YOK
		///
		/// Update() her karede transform.position'u targetPosition'a
		/// interpolasyonla götürdüğü için telafi ~0.3 saniyede tamamen
		/// geri alınıyordu: kukla gelen AYAK konumuna iniyor, model ise
		/// kalçada orijinli olduğu için topuktan itibaren yere gömülüyordu.
		/// Log bunu "snapped to the ground" diye yazıyordu, ekran görüntüsü
		/// ise yarım gövde gösteriyordu.
		///
		/// Doğrusu: telafi bir KATSA değil, modelin geometrisinden gelen
		/// kalıcı bir dönüşüm. Bu yüzden paket konumuna eklenir, hedefe de
		/// yazılır.
		/// </summary>
		public void ApplyPose(Vector3 position, Quaternion rotation, float speed) {
			// Ham ağ konumu saklanır (mesafe kontrolleri bunu okur).
			netPosition = position;
			// Ağ konumu AYAK seviyesidir; model kökü ise kalçada.
			Vector3 corrected = position + new Vector3(0f, GroundLift, 0f);

			// Büyük fark: atla. Harita boyunca kayma olmasın. Sıçramada
			// dikey hız izleme de sıfırlanır, yoksa ışınlanma "düşüyor"
			// gibi okunur ve kukla bir anlığına Jump oynatır.
			if ((corrected - transform.position).sqrMagnitude > SNAP_DISTANCE * SNAP_DISTANCE) {
				transform.position = corrected;
				ApplyModelRotation(rotation);
				lastY = corrected.y;
				smoothVy = 0f;
				// Sıçramada HIZ da sıfırlanır: eski konumdan ölçülen hız
				// yeni konuma uygulanırsa kukla haritayı boydan boya geçer.
				peerVelocity = Vector3.zero;
			}

			// HIZ ÖLÇÜMÜ: son iki paket arası fark / farkın süresi.
			//
			// Paket hızını kendi içinde göndermiyor; iki ardışık paket
			// arasındaki mesafe tam olarak ortalama hızdır. 20 Hz'de bu
			// kuklanın paketler arası düz çizgiyle ilerlemesini sağlar,
			// yani "20 adımda gel" yerine "sürekli akıyor" gibi görünür.
			//
			// SINIRLAR: 0'a yakın süre bölmesi patlamasın diye alınır; aşırı
			// hız (sıçrama, düşme, araç fırlatması) 12 m/s ile sınırlanır,
			// yoksa extrapolasyon kuklayı bir sonraki karede haritanın
			// dışına atardı.
			float now = Time.time;
			float dtPacket = now - extrapAt;
			if (dtPacket > 0.001f && dtPacket < 1f) {
				Vector3 v = (corrected - extrapFrom) / dtPacket;
				float sp = v.magnitude;
				if (sp > MAX_PEER_SPEED) v = v * (MAX_PEER_SPEED / sp);
				peerVelocity = v;
			}

			extrapFrom = corrected;
			extrapAt = now;

			targetPosition = corrected;
			targetRotation = ShortestTarget(transform.rotation, rotation);
			targetSpeed = speed;
			lastPacketTime = Time.time;
		}

		/// <summary>
		/// Bayrak paketi geldi.
		///
		/// Oturma bayrağı burada BAĞLANMAZ: koltuk pivot'unu bulmak araç
		/// bilgisidir, VehicleRegistry'in işi (SyncedVehicle.FindSeat). Burada
		/// yalnızca istek kaydedilir; bağlanma olmazsa kukla ayakta kalır, hata
		/// değildir.
		/// </summary>
		/// <summary>
		/// El hareketleri: ayni karede degismediyse dokunma (SetOverlay(true)
		/// saati sifirlar; kalp atisiyla pozu basa sarmamak icin degisim sart).
		///
		/// Smoke klibi mpplayer bundle'da YOK (yalnizca mpnpc'de var); ele
		/// agiz pozu olarak Drink kullanilir (paket rebuild'siz). Parmak icin
		/// Finger klibi modelde var.
		/// </summary>
		public void ApplyGestures(bool finger, bool smoking) {
			if (finger == lastFingerG && smoking == lastSmokingG) return;
			lastFingerG = finger;
			lastSmokingG = smoking;
			if (anim != null) {
				anim.SetOverlay(PlayerAnimManager.AnimState.Finger, finger);
				anim.SetOverlay(PlayerAnimManager.AnimState.Drink, smoking);
			}
			SmpLog.Log("[ACT] GESTURE peer finger " + (finger ? "on" : "off")
				+ " smoking " + (smoking ? "on" : "off"));
		}

		public void ApplyFlags(bool seatedFlag, byte crouchAmount, bool deadFlag) {
			bool wasDead = dead;
			seated = seatedFlag;
			crouch = crouchAmount;
			dead = deadFlag;

			// Ölüm anında Hit bir kez oynatılır; yeniden bağlanmada tekrarlanmaz.
			if (dead && !wasDead && anim != null) anim.PlayHit();
		}

		// ------------------------------------------------------------------
		// Koltuk
		// ------------------------------------------------------------------

		/// <summary>
		/// Kuklayı bir koltuğa bağlar. Konumu araç belirlersin diye parent olur.
		/// </summary>
		/// <summary>
		/// Koltuk oturus ofseti (koltuk pivotuna gore kukla koku).
		///
		/// CANLI HATA ("tavanda oturuyor"): eski surum baglanirken kuklanin
		/// O ANKI dunya konumunu koruyordu. O konum surucunun kafa hizasidir
		/// (+ GroundLift payi), yani koltuktan ~1 m yukarida; koltuga gore
		/// dondurulunce kukla tavana yapisiyordu. Kukla koku kalcada oldugu
		/// ve koltuk pivotu minder hizasinda oldugu icin dogru deger pivotun
		/// ta kendisidir (sifir ofset). Araca gore ince ayar gerekirse buradan.
		/// </summary>
		static readonly Vector3 SIT_OFFSET = new Vector3(0f, 0f, 0f);

		/// <summary>Minderin tabandan yuksekligi (Satsuma ~0.45 m). Kok
		/// ayaktadir, o yuzden kok minderin bu kadar altina konur.</summary>
		const float SIT_FLOOR_DROP = 0.45f;

		public void AttachToSeat(Transform seatPoint) {
			if (seatPoint == null) return;
			if (attached && seat == seatPoint) return;

			seat = seatPoint;
			attached = true;

			// DUSUS: kukla koku AYAKTADIR (fabrika olcumu: footOffset ~0).
			// Eski SIT_OFFSET=0 ayagi mindere koyuyor, kukla koltukta
			// AYAKTA duruyordu (basi tavandan cikiyordu). Dogru: kok,
			// ayaklar tabana degcek sekilde asagida: dusus = kokYuksekligi
			// - tabanDerinligi (Satsuma tabani minderin ~0.45 altinda).
			float rootAboveFeet = GroundLift - RemotePlayerFactory.GROUND_EPSILON;
			float drop = rootAboveFeet - SIT_FLOOR_DROP;
			Vector3 sitOffset = new Vector3(0f, drop, 0f);

			// DIKLIK: koltuk pivotu yatik modellenmis olabilir. Kukla
			// dunyada DIK durmali (yoksa inince "yatarak yuruyor").
			// Yerel donus, koltuk donusunu dik yaw konumuna telafi eder;
			// ayrilirken de ayni telafi upright verir (asagiya bak).
			Quaternion seatRot = Quaternion.identity;
			try { seatRot = seatPoint.rotation; } catch { }
			float yaw = 0f;
			try { yaw = seatRot.eulerAngles.y; } catch { }
			Quaternion upright = Quaternion.Euler(0f, yaw, 0f);
			Quaternion localRot = Quaternion.identity;
			try { localRot = Quaternion.Inverse(seatRot) * upright; } catch { }

			transform.parent = seatPoint;
			transform.localPosition = sitOffset;
			transform.localRotation = localRot;
			seatLocalPosition = sitOffset;
			seatLocalRotation = localRot;

			// Pelvis kalibrasyonu: 1 sn arayla 3 kez, leğen kemigini
			// pivotun ustune oturt (klip/govde/koltuk farkini kapatir).
			pelvisBone = FindChildRecursive(transform, "pelvis");
			seatCalibLeft = 3;
			try { nextSeatCalib = Time.time + 1f; } catch { nextSeatCalib = 0f; }

			if (anim != null) anim.SetOverlay(PlayerAnimManager.AnimState.Lean, false);
			try {
				SmpLog.Log("[SEAT] attach '" + seatPoint.name + "' seatY=" + seatPoint.position.y.ToString("F3")
					+ " drop=" + drop.ToString("F3") + " (rootAboveFeet=" + rootAboveFeet.ToString("F3") + ")"
					+ " seatEuler=" + seatRot.eulerAngles.ToString("F0"));
			} catch { }
		}

		/// <summary>
		/// Koltuğa bağlanmadan önce modelin ayağını zemine oturtmak için
		/// gereken yukarı kaydırma (m). RemotePlayerFactory ölçer ve yazar.
		/// </summary>
		public float GroundLift;

		/// <summary>
		/// Son gelen AĞ konumu (ayak seviyesi, GroundLift UYGULANMAMIŞ).
		/// Mesafe kontrolleri bunu kullanır: kukla kökü modelin kalçasındadır
		/// ve ~1 m yukarıdadır, hedef motor bayı gibi bir yerdeyse bu bir
		/// metre ölçüm hatası olurdu.
		/// </summary>
		public Vector3 NetPosition { get { return netPosition; } }
		Vector3 netPosition;

		/// <summary>
		/// Legen kemigini koltuk pivotuna hizalar (bagliyken 1 sn arayla,
		/// en fazla 3 kez). Klip govdeyi az one/ yana koymussa kalan fark
		/// buradan kapanir; tasit hareket ederken olusan mm titreme
		/// onemsizdir, 3 hak bitince birakilir.
		/// </summary>
		void CalibrateSeat() {
			if (seatCalibLeft <= 0 || pelvisBone == null) return;
			float now = 0f;
			try { now = Time.time; } catch { return; }
			if (now < nextSeatCalib) return;
			seatCalibLeft--;
			nextSeatCalib = now + 1f;
			try {
				Vector3 pp = pelvisBone.position;
				Vector3 local = seat.InverseTransformPoint(pp);
				if (local.sqrMagnitude > 0.0004f) {
					transform.localPosition = transform.localPosition - local;
					seatLocalPosition = transform.localPosition;
					SmpLog.Log("[SEAT] calib residual=" + local.ToString("F3")
						+ " left=" + seatCalibLeft);
				}
				else seatCalibLeft = 0;
			}
			catch { }
		}

		/// <summary>Koltuktan ayırır ve dünya konumunu korur.</summary>
		public void DetachFromSeat() {
			if (!attached) return;

			if (seat != null) {
				// Yerel koordinatı dünyaya çevir, sonra sahne köküne geri koy.
				Vector3 worldPosition = seat.TransformPoint(seatLocalPosition);
				// Koltuk dönüşü ham ağ dönüşünün YERİNE geçer; prefab
				// düzeltmesi yine sağdan çarpılmalı (ApplyModelRotation).
				Quaternion worldRotation = seat.rotation * seatLocalRotation;

				transform.parent = null;
				transform.position = worldPosition;
				ApplyModelRotation(worldRotation);
			}
			else {
				// Koltuk zaten yok: parent'i olduğu gibi bırak ama bayrağı düşür.
				if (transform.parent != null) transform.parent = null;
			}

			attached = false;
			seat = null;
			seatCalibLeft = 0;
		}

		/// <summary>
		/// Kafa bone'unun DINLENME (bind) dönüşünü yakalar.
		///
		/// NEDEN GEREKLI: gövde mesh'i Z ekseni boyunca saklanıyor, bu
		/// yüzden head bone'unun bind dönüşü 90°'lik bir dönüş içeriyor
		/// (bindpose satırları 0,0,-1 / 1,0,0 / 0,-1,0). Doğurma anında
		/// animasyon henüz oynamadığı için localRotation HENÜZ bu dinlenme
		/// değeridir; onu saklıyoruz.
		///
		/// CANLI HATA: `ApplyHeadRotation` mutlak
		/// `Quaternion.Euler(pitch, yaw, 0)` YAZIYORDU, yani bu 90°'lik
		/// bind dönüşünü siliyordu. Sonuç: kafa yanık/ters duruyordu.
		/// (Ayrıca her konum paketinde yazıldığı için sürekli eziliyordu.)
		/// </summary>
		void CaptureHeadRest() {
			headRestCaptured = false;
			headBone = null;
			Transform t = FindChildRecursive(transform, "head");
			if (t == null) t = FindChildRecursive(transform, "Head");
			if (t == null) t = FindChildRecursive(transform, "HeadBone");
			if (t == null) t = FindChildRecursive(transform, "mixamorig:Head");
			if (t == null) return;
			headBone = t;
			headRestLocal = t.localRotation;
			headRestCaptured = true;
			// Yeni kemi -> yuz ekseni yeniden olculmeli (model degismis olabilir).
			headFaceAxisMeasured = false;
		}

		// ==================================================================
		// KAFA KEMİĞİ — anatomisi, eksenleri ve iki bakış yolu
		// ==================================================================
		//
		// HİYERARŞİ (bundle içinde ölçüldü):
		//
		//   Char/skeleton/pelvis/spine_middle/spine_upper
		//     └── HeadPivot          ← eklem: kafayı omuz üstünde döndürür
		//          └── head          ← kafa kemiği (yüzü taşıyan)
		//
		// 960'lık çocuk gövdesinde HeadPivot YOKTU ve `head` doğrudan
		// spine_upper'a bağlıydı; klip yolları (HeadPivot/head) tutmadığı
		// için BuildNPCBundle kimlik bir HeadPivot ekliyor. 1714'te ise
		// Unity düğümü "HeadPivot 5" diye adlandırmıştı, o da düzeltildi.
		// Artık altı gövdede de yol birebir aynı ve 19/19 klip eğrisi
		// çözülüyor.
		//
		// KEMİĞİN YEREL EKSENLERİ — ÖLÇÜLDÜ, TAHMİN EDİLMEDİ
		//
		//   Oyun içinde otomatik yazılan `[head-axis]` ölçümü:
		//
		//     HEDEF      (-0.95, -0.22, -0.21)
		//     kemik +X  (0.12, -0.99, 0.03)   hedefden  84.1 derece
		//     kemik +Y  (-0.97, -0.12, -0.21)  hedefden   5.9 derece  ← YÜZ
		//     kemik +Z  (0.22, 0.00, -0.98)    hedefden  90.0 derece
		//
		//   yani  +Y = yüzün baktığı yön
		//        +X = dünya AŞAĞI
		//        +Z = yana (omuz hizası)
		//
		// Bu sıradışı bir eksen düzeni (normalde +Z ileri olur). Sebep:
		// mesh Z ekseni boyunca saklanıyor ve kökteki yön düzeltmesi onu
		// dikmiş oluyor; bind dönüşleri de buna göre yazılmış.
		//
		// ÖNEMLİ — DÖRT DENEME BOŞA GİTTİ, ÇÜNKÜ EKSENİ ADLANDIRDIM:
		//   1) "ileri = +Z" varsaydım                       → 90° kayma
		//   2) bind dönüşünü (headRestLocal) eklemeyi unuttum → 90° kayma
		//   3) ekledim ama hâlâ +Z kullanıyordum             → 90° kayma
		//   4) ölçtüm: +Y. Düzeldi.
		// Üçünün de ekranda görünen hali AYNIYDı (kafa yana dönük).
		// DERS: bir mesh'in anatomik eksenleri standart değildir; adını
		// tahmin etme, ölç. `ProbeHeadAxes()` hâlâ kodda duruyor.
		//
		// İKİ BAKIŞ YOLU (ikisi de aynı sınırları ve aynı yumuşatmayı
		// kullanır; sadece HEDEFİ farklı üretir):
		//
		//   OYUNCU KUKLASI  →  ApplyHeadRotation(pitch, yaw)
		//       Konum paketiyle gelir (~19 Hz). yaw = kameranın DÜNYA
		//       yönüdür, ofset değil; ofset burada hesaplanır ve
		//       SADECE koltuktayken uygulanır (yürürken kamera gövdeyi
		//       izlediği için zaten ~0'dır, ayrıca kapatılıyor).
		//
		//   TEST NPC'si    →  LookAtWorld(nokta)
		//       Bot için: kafayı doğrudan hedefe çevirir.
		//
		// İKİ ZORUNLU KURAL:
		//
		//   1) headRestLocal KORUNUR. Kafanın dünya dönüşü
		//      par.rotation * localRotation'dır. Mutlak bir Euler yazmak
		//      (local'a doğrudan Euler atamak) bind dönüşünü siler ve
		//      kafayı 90° kaydırır. Bu yüzden daima bind'in ÜSTÜNE
		//      çarpılır.
		//
		//   2) Uygulama LATEUPDATE'te yapılır. Unity'de animasyon
		//      örneklemesi PreLateUpdate'te, yani Update()'ten SONRA
		//      çalışır. Update'te yazılan dönüş animasyon tarafından
		//      üstüne yazılır ve hiç görünmez.
		// ==================================================================

		Transform headBone;
		Quaternion headRestLocal = Quaternion.identity;
		bool headRestCaptured;

		// ----- Kafa bakışı sınırları -----
		//
		// Kullanıcı isteği: yürürken yalnızca yukarı/aşağı, koltuktayken
		// ayrıca sağa/sola; ve SINIR koy, çünkü tam aşağı bakışta kafa
		// toprağa giriyor.
		//
		// Değerler: boyun anatomisi ~-50..+35 derece. -90'a varmasına
		// izin verilirse kafa mesh'i gövdeye/toprağa giriyor (canlı hata).
		public const float HEAD_PITCH_MIN = -50f;   // aşağı sınır
		public const float HEAD_PITCH_MAX = 35f;    // yukarı sınır
		/// <summary>
		/// Kafanın merkezden azami yatay dönüşü: ±90 derece.
		///
		/// Kullanıcı isteği: ne oyuncu ne NPC 90°'den fazla dönsün.
		/// İKİ YOL DA BUNU KULLANIR (biri çalışırken diğeri bozulursa
		/// sınırlar farklılaşırdı):
		///   * Oyuncu kuklası → ApplyHeadRotation (yalnızca koltuktayken)
		///   * Test NPC'si   → LookAtWorld
		/// </summary>
		public const float HEAD_YAW_LIMIT = 90f;

		/// <summary>
		/// Düz bakış ölü bölgesi (derece): bu açıdan küçük sapmalar
		/// tam SIFIR sayılır.
		///
		/// CANLI BELİRTİ: "kafa düz görünmüyor, hafif sağa yatık ve sağa
		/// bakıyor". Ağ gürültüsü + gövde yumuşatma gecikmesi hedefte
		/// sürekli 1-3 derecelik artık bırakıyordu; kukla hiç tam düz
		/// bakmıyordu. Ölü bölgeyle hedefe yapışıkken kafa tam düz olur.
		/// </summary>
		public const float HEAD_DEADZONE = 2.5f;

		/// <summary>
		/// Kafa kemiğinin YÜZ yönü = yerel eksen.
		///
		/// OLCÜLDÜ, varsayılmadı. Log (`[head-axis]`, otomatik):
		///   HEDEF     (-0.95, -0.22, -0.21)
		///   kemik +X  (0.12, -0.99, 0.03)   hedefden  84.1 derece
		///   kemik +Y  (-0.97,-0.12,-0.21)   hedefden   5.9 derece  ← yüz
		///   kemik +Z  (0.22, 0.00, -0.98)   hedefden  90.0 derece
		///
		/// Kemik +Y'ye bakan. Önce +Z varsayılmıştı ve tam 90° kayıyordu
		/// (ekranda "kafa sağa bakıyor"). Üç yanlış denemenin sonuncusuydu:
		/// ekseni ölçmeden adlandırmıştık.
		/// </summary>
		public static readonly Vector3 HEAD_FACE_AXIS = new Vector3(0f, 1f, 0f);

		/// <summary>
		/// Bu KUKLAYA OLCULMUS yuz ekseni. BASLANGICTA `headFaceAxis`
		/// degerini alir; `CaptureHeadFaceAxis()` ilk karede gercek olcer.
		///
		/// NEDEN SABIT DEGER YETMEZ (canli hata: "kafa hep sola donuk"):
		/// `headFaceAxis = (0,1,0)` yalnizca NPC govdesinden (mpnpc)
		/// OLCULMUS bir degerdi. Oyuncu govdesinde (mpplayer) kemigin
		/// bagli oldugu eksen FARKLI olabilir. Yanlis eksen secilirse:
		///   * `restFwd` yanlis yonu gosterir,
		///   * `FromToRotation(restFwd, want)` 90 derece kaydirir,
		///   * `StepHeadBias` OLCUMU YANLIS YAPAR ve hatayi ±30 derece
		///     kelepceye yazar -> kafa kalici olarak yana donuk kalir.
		/// Logdaki `[head-bias] spawn rest sapmasi yaw=0.0` bunu gosterir:
		/// sapma 0 cikiyor ama kullanici yine de sola bakiyor demektir ki
		/// bias kaynagi degil, EKSEN kaynagidir.
		///
		/// COZUM: dogustan olculur. Kemigin UCH yerel ekseni (bind durusunda)
		/// dunyaya cevrilip govde ileri yonuyle karsilastirilir; en cok
		/// hizalanan eksen yuz ekseni kabul edilir. Model degisse de
		/// calisir.
		/// </summary>
		Vector3 headFaceAxis = new Vector3(0f, 1f, 0f);

		/// <summary>Yuz ekseni olculdu mu?</summary>
		bool headFaceAxisMeasured;

		/// <summary>
		/// Kemigin yuz eksenini BIR KEZ olcer. Yanlis eksen tum hesabin
		/// temelidir, bu yuzden olcum olmadan hicbir bakis hedefi uretilmez.
		/// </summary>
		void CaptureHeadFaceAxis() {
			if (headFaceAxisMeasured) return;
			if (!headRestCaptured || headBone == null) return;
			Transform par = headBone.parent;
			if (par == null) return;
			headFaceAxisMeasured = true;

			Vector3 bodyFwd;
			try { bodyFwd = par.forward; } catch { bodyFwd = Vector3.forward; }
			if (bodyFwd.sqrMagnitude < 1e-6f) bodyFwd = Vector3.forward;
			bodyFwd = bodyFwd.normalized;

			Vector3 ax = (par.rotation * headRestLocal * Vector3.right).normalized;
			Vector3 ay = (par.rotation * headRestLocal * Vector3.up).normalized;
			Vector3 az = (par.rotation * headRestLocal * Vector3.forward).normalized;

			float dx = Vector3.Dot(ax, bodyFwd);
			float dy = Vector3.Dot(ay, bodyFwd);
			float dz = Vector3.Dot(az, bodyFwd);

			string best;
			if (dx >= dy && dx >= dz) { headFaceAxis = Vector3.right; best = "+X"; }
			else if (dy >= dz) { headFaceAxis = Vector3.up; best = "+Y"; }
			else { headFaceAxis = Vector3.forward; best = "+Z"; }

			SmpLog.Print("[head-bias] yuz ekseni OLCULDU: " + best
				+ " (ileri ile acilar: X=" + (dx * 90f).ToString("F1")
				+ " Y=" + (dy * 90f).ToString("F1")
				+ " Z=" + (dz * 90f).ToString("F1") + " derece)"
				+ (best == "+Y" ? "" : "  <== SABIT DEGERDEN FARKLI!"));
		}

		float targetHeadPitch, targetHeadYaw;
		Quaternion targetHeadLocal = Quaternion.identity;
		bool headLocalTargetSet;
		float smoothHeadPitch, smoothHeadYaw;
		// Kafa zinciri sabit ofseti (derece): dinlenme yakalama / ebeveyn /
		// govde farki ne olursa olsun duz bakista olculup hedefden dusulur.
		float headBiasYaw, headBiasPitch;
		bool headSeen;

		/// <summary>
		/// Kafayı DÜNYA NOKTASINA doğru çevirir (bot testi / NPC).
		///
		/// NEDEN YEREL DÖNÜŞ HEDEFİ: kafanın dünya dönüşü
		/// `ebeveynDünya * localRotation` olduğu için, hedefe bakan yerel
		/// dönüş `Inverse(ebeveynDünya) * hedefDünya` ile bulunur. Böylece
		/// mesh'in kendi eksen düzenini (gövde Z ekseni boyunca saklanıyor,
		/// bind dönüşü 90°) tahmin etmeye gerek kalmaz.
		///
		/// Aynı sınırlar geçerli: dikey açı HEAD_PITCH_MIN/MAX ile
		/// kırpılır (kafa toprağa girmesin), yatay açı gövdeye göreli
		/// HEAD_YAW_LIMIT ile sınırlanır (kendi ekseninde tam tur dönmesin).
		/// </summary>
		public void LookAtWorld(Vector3 worldPoint) {
			if (!headRestCaptured) CaptureHeadRest();
			if (headBone == null) return;

			Transform par = headBone.parent;
			if (par == null) return;

			Vector3 to = worldPoint - headBone.position;
			if (to.sqrMagnitude < 1e-6f) return;
			Vector3 want = to.normalized;

			// --- SINIR 1: dikey aci (kafa topraga girmesin) --------------
			float horiz = new Vector2(want.x, want.z).magnitude;
			if (horiz > 1e-5f) {
				float pitchDeg = Mathf.Atan2(want.y, horiz) * Mathf.Rad2Deg;
				float pc = Clamp(pitchDeg, HEAD_PITCH_MIN, HEAD_PITCH_MAX);
				if (Mathf.Abs(pc - pitchDeg) > 0.01f) {
					// Kirpildi: yatay bileseni koruyarak yeni yon kur.
					float h = Mathf.Cos(pc * Mathf.Deg2Rad);
					want = new Vector3(want.x / horiz * h,
						Mathf.Sin(pc * Mathf.Deg2Rad), want.z / horiz * h);
				}
			}

			// --- SINIR 2: yatay aci, govdeye goreli -------------------------
			float bodyYaw = transform.eulerAngles.y;
			float dirYaw = Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg;
			float off = Mathf.DeltaAngle(bodyYaw, dirYaw);
			float lo = Clamp(off, -HEAD_YAW_LIMIT, HEAD_YAW_LIMIT);
			if (Mathf.Abs(lo - off) > 0.01f && horiz > 1e-5f) {
				// Kirpildi: yatay yonu govde etrafinda geri al.
				float a = (bodyYaw + lo) * Mathf.Deg2Rad;
				want = new Vector3(Mathf.Sin(a) * horiz, want.y, Mathf.Cos(a) * horiz);
			}
			want = want.normalized;

			// --- KEMIGIN KENDI BAKIS YONU ---------------------------------
			// OLCULE BULUNDU, TAHMIN EDILMEDI. Kemigin local +Y ekseni
			// yuze bakiyor:
			//
			//   HEDEF     (-0.95, -0.22, -0.21)
			//   kemik +X  (0.12, -0.99, 0.03)   hedefden  84.1 derece
			//   kemik +Y  (-0.97,-0.12,-0.21)   hedefden   5.9 derece  <== yuz
			//   kemik +Z  (0.22, 0.00, -0.98)   hedefden  90.0 derece
			//
			// Once +Z varsayiliyordu ve HATALIYDI: tam 90 derece kayma,
			// ekranda "kafa saga bakiyor" olarak gorunuyordu. Uc kez
			// yanilmanin nedeni de buydu.
			Vector3 faceAxis = headFaceAxis;
			Vector3 restFwd = par.rotation * headRestLocal * faceAxis;
			if (restFwd.sqrMagnitude < 1e-6f) restFwd = par.rotation * faceAxis;
			restFwd = restFwd.normalized;

			if (Vector3.Dot(restFwd, want) > 0.99999f) {
				// Zaten dogru bakiyor: hicbir sey yazma.
				return;
			}

			// Dunya donusu: once hedefe cevir, sonra yerel uzaya indir.
			//
			//   dunya(son) = rot * par.rotation * headRestLocal
			//   yerel(son) = Inverse(par.rotation) * dunya(son)
			//
			// headRestLocal KORUNUR; atlanırsa 90° kayma olur.
			ProbeHeadAxes(want);
			// ROLL'SUZ (ApplyHeadRotation ile ayni yol).
			targetHeadLocal = ComposeHeadLocal(par, want);
			headLocalTargetSet = true;

			if (!headSeen) {
				smoothHeadLocal = targetHeadLocal;
				headSeen = true;
			}
		}

		/// <summary>
		/// Kafa bakışının doğru ekseni hangisi? TEK SEFERLİK otomatik ölçüm.
		///
		/// Bu hesapta üç kez yanıldık: önce "ileri ekseni +Z" varsaydık,
		/// sonra bind dönüşünü çarpmaya eklemeyi unuttuk, sonra da yanlış
		/// ekseni ölçtük. Kemiğin yerel +X/+Y/+Z eksenlerinin dünyada
		/// neye karşılık geldiğini bilmeden "hedefe baktır" diyemeyiz.
		///
		/// SONUÇ ÖLÇÜLDÜ: +Y (bkz. headFaceAxis ve dosyadaki büyük
		/// başlık). Bu metot SILINMEDİ çünkü mesh/rig değişirse (mod,
		/// gövde değişimi) eksen kayabilir ve sadece ölçüm bunu
		/// söyler.
		///
		/// Kullanıcı komut çalıştırmak zorunda değil: ilk bakışta bir
		/// kez otomatik yazılır.
		/// </summary>
		bool headProbed;

		void ProbeHeadAxes(Vector3 want) {
			if (headProbed) return;
			headProbed = true;
			try {
				Transform par = headBone.parent;
				if (par == null) return;
				Vector3 ax = (par.rotation * headRestLocal * Vector3.right).normalized;
				Vector3 ay = (par.rotation * headRestLocal * Vector3.up).normalized;
				Vector3 az = (par.rotation * headRestLocal * Vector3.forward).normalized;
				Vector3 bw = (par.rotation * Vector3.forward).normalized;
				float dx = Vector3.Angle(ax, want);
				float dy = Vector3.Angle(ay, want);
				float dz = Vector3.Angle(az, want);
				float dw = Vector3.Angle(bw, want);
				SmpLog.Print("[head-axis] HEDEF " + want.ToString("F2"));
				SmpLog.Print("[head-axis]   kemik +X = " + ax.ToString("F2")
					+ "   hedefden " + dx.ToString("F1") + " derece");
				SmpLog.Print("[head-axis]   kemik +Y = " + ay.ToString("F2")
					+ "   hedefden " + dy.ToString("F1") + " derece");
				SmpLog.Print("[head-axis]   kemik +Z = " + az.ToString("F2")
					+ "   hedefden " + dz.ToString("F1") + " derece");
				SmpLog.Print("[head-axis]   govde +Z = " + bw.ToString("F2")
					+ "   hedefden " + dw.ToString("F1") + " derece");
				SmpLog.Print("[head-axis]   EN YAKIN: " + NearestAxis(dx, dy, dz, dw));
				SmpLog.Print("[head-axis]   bone=" + headBone.name + " parent=" + par.name
					+ " bodyYaw=" + transform.eulerAngles.y.ToString("F1")
					+ " bodyTargetYaw=" + RawTargetRotation().eulerAngles.y.ToString("F1"));
			}
			catch (System.Exception e) {
				SmpLog.Warning("head-axis probe failed: " + e.Message);
			}
		}

		static string NearestAxis(float x, float y, float z, float w) {
			float m = x;
			string lbl = "kemik +X";
			if (y < m) { m = y; lbl = "kemik +Y"; }
			if (z < m) { m = z; lbl = "kemik +Z"; }
			if (w < m) { m = w; lbl = "govde +Z"; }
			return lbl + " (" + m.ToString("F1") + " derece)";
		}

		/// <summary>Hedef tabanlı bakışı bırakır (ağ kuklası zaten böyle).</summary>
		public void ClearLookAt() {
			headLocalTargetSet = false;
		}

		/// <summary>
		/// Kafa bakış zincirinin TAMAMINI ölçer ('smp headinfo').
		///
		/// NEDEN VAR: bu hesapta dört deneme yanlış gitti ve üçü de
		/// aynı belirtiyi verdi (kafa yana dönük). Kök neden ekseni adıyla
		/// tahmin etmemizdi. Bu metot sayıları verir; tahmin etmez.
		///
		/// Bakılacaklar:
		///   * kemik +Y'nin hedefe açısı küçükse eksen doğrudur.
		///     Değilse mesh/rig değişmiş demektir.
		///   * ANLIK local ile yazılan local farklıysa animasyon
		///     yazmamızı geçiyor demektir (LateUpdate sırası bozulmuş).
		///   * parent dönüşü her kare değişiyorsa LookAtWorld ile
		///     StepHeadSmoothing farklı parent dönüşü kullanıyor olabilir.
		/// </summary>
		public string DescribeHeadChain(Vector3 worldPoint) {
			System.Text.StringBuilder sb = new System.Text.StringBuilder(700);
			if (!headRestCaptured) CaptureHeadRest();
			if (headBone == null) {
				sb.Append("KAFASI BULUNAMADI (head/Head/HeadBone/mixamorig:Head)");
				return sb.ToString();
			}
			Transform par = headBone.parent;
			Func<Vector3, string> f = v => "(" + v.x.ToString("F2") + ", "
				+ v.y.ToString("F2") + ", " + v.z.ToString("F2") + ")";

			sb.Append("head bone      : ").Append(headBone.name);
			sb.Append("  parent=").Append(par != null ? par.name : "-").AppendLine();
			sb.Append("head dunya kon : ").Append(f(headBone.position)).AppendLine();
			sb.Append("headRestLocal  : euler(")
				.Append((headRestLocal.eulerAngles.x > 180f ? headRestLocal.eulerAngles.x - 360f : headRestLocal.eulerAngles.x).ToString("F1"))
				.Append(", ").Append(headRestLocal.eulerAngles.y.ToString("F1"))
				.Append(", ").Append(headRestLocal.eulerAngles.z.ToString("F1"))
				.Append(")").AppendLine();

			Vector3 restFwd = par != null
				? par.rotation * headRestLocal * Vector3.forward
				: Vector3.forward;
			restFwd = restFwd.normalized;
			sb.Append("restFwd (dunya): ").Append(f(restFwd)).AppendLine();

			if (par != null) {
				Vector3 e = par.rotation.eulerAngles;
				sb.Append("parent donus   : (").Append(e.x.ToString("F1")).Append(", ")
					.Append(e.y.ToString("F1")).Append(", ").Append(e.z.ToString("F1"))
					.Append(")").AppendLine();
			}

			Vector3 to = worldPoint - headBone.position;
			if (to.sqrMagnitude > 1e-6f) {
				Vector3 want = to.normalized;
				sb.Append("hedef          : ").Append(f(worldPoint)).AppendLine();
				sb.Append("want (dunya)   : ").Append(f(want)).AppendLine();
				float ang = Vector3.Angle(restFwd, want);
				sb.Append("ACI restFwd<>want: ").Append(ang.ToString("F1")).Append(" derece")
					.AppendLine();
				sb.Append("bodyYaw        : ").Append(transform.eulerAngles.y.ToString("F1")).AppendLine();
				sb.Append("dirYaw         : ")
					.Append((Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg).ToString("F1"))
					.AppendLine();
				float off = Mathf.DeltaAngle(transform.eulerAngles.y,
					Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg);
				sb.Append("yaw ofseti     : ").Append(off.ToString("F1"))
					.Append("  (sinir ").Append(Clamp(off, -HEAD_YAW_LIMIT, HEAD_YAW_LIMIT).ToString("F1"))
					.Append(")").AppendLine();
			}

			sb.Append("yazilan local  : euler(")
				.Append((targetHeadLocal.eulerAngles.x > 180f ? targetHeadLocal.eulerAngles.x - 360f : targetHeadLocal.eulerAngles.x).ToString("F1"))
				.Append(", ").Append(targetHeadLocal.eulerAngles.y.ToString("F1"))
				.Append(", ").Append(targetHeadLocal.eulerAngles.z.ToString("F1"))
				.Append(")").AppendLine();
			Vector3 le = headBone.localRotation.eulerAngles;
			sb.Append("ANLIK local    : euler(")
				.Append((le.x > 180f ? le.x - 360f : le.x).ToString("F1"))
				.Append(", ").Append(le.y.ToString("F1"))
				.Append(", ").Append(le.z.ToString("F1"))
				.Append(")").AppendLine();
			sb.Append("ANLIK head.forward: ").Append(f(headBone.forward));
			return sb.ToString();
		}

		Quaternion smoothHeadLocal = Quaternion.identity;

		/// <summary>
		/// Kafa bakışını HEDEF olarak kaydeder; <see cref="StepHeadSmoothing"/>
		/// her karede yumuşatarak uygular.
		///
		/// pitch: kamera eğimi (-90..+90), doğrudan kullanılır ama SINIRLANIR.
		/// yaw:   kamera DÜNYA yönü (0..360 mutlak) — kafa ofseti DEĞİL.
		///        Gövde yönüne göre fark alınır ve SADECE koltuktayken
		///        uygulanır; yürürken kamera gövdeyi izlediği için fark zaten
		///        ~0'dır, ayrıca kapatıyoruz ki yürüyüşte baş dönmesin.
		/// </summary>
		public void ApplyHeadRotation(float pitch, float yaw) {
			if (anim == null || anim.Character == null) return;

			if (pitch > 180f) pitch -= 360f;          // -90..90 aralığına indir
			// Ölü bölge: hedefe yapışıkken tam düz baksın (sürekli mikro
			// sapma "hafif sağa bakıyor" diye görünüyordu).
			if (pitch > -HEAD_DEADZONE && pitch < HEAD_DEADZONE) pitch = 0f;
			// SINIR: kesit (clamp) şart. Kameraya bakarken aşağı yönü
			// sınırsız göndermek kafayı toprağa sokuyordu.
			pitch = Clamp(pitch, HEAD_PITCH_MIN, HEAD_PITCH_MAX);

			if (attached && seat != null) {
				// Kafa ofseti = kameranın baktığı yön - gövdenin baktığı yön.
				float bodyYaw = transform.eulerAngles.y;
				float off = Mathf.DeltaAngle(bodyYaw, yaw);
				if (off > -HEAD_DEADZONE && off < HEAD_DEADZONE) off = 0f;
				targetHeadYaw = Clamp(off, -HEAD_YAW_LIMIT, HEAD_YAW_LIMIT);
			} else {
				// Yürürken sadece yukarı/aşağı.
				targetHeadYaw = 0f;
			}

			targetHeadPitch = pitch;
			// Yerel Euler YOK: kemik +Y yone bakar (+Z degil), o yuzden
			// headRestLocal * Euler(pitch, yaw, 0) eksenleri karistiriyordu:
			// pitch kemigin dikey eksenine (yaw etkisi), yaw yuz eksenine
			// (roll etkisi) biniyordu (yukari saga, asagi sola gidiyordu).
			// Cozum LookAtWorld ile ayni: istenen DUNYA bakis yonunu kur,
			// FromToRotation ile yerellige indir. pitch>0 = asagi (kamera euler.x).
			if (!headRestCaptured) CaptureHeadRest();
			Transform par = headBone != null ? headBone.parent : null;
			if (par != null) {
				// spawnBias YOK: restFwd zaten headRestLocal (bind pose) icerir,
				// yani spawn sapmasi restFwd'de mevcut. FromToRotation(restFwd, want)
				// otomatik karsilar. Sadece RUNTIME drift icin headBias kullan.
				float yawAbs = targetHeadYaw - headBiasYaw;
				try { yawAbs = transform.eulerAngles.y + targetHeadYaw - headBiasYaw; } catch { }
				float pitchEff = pitch - headBiasPitch;
			float pr = pitchEff * Mathf.Deg2Rad;
				float yr = yawAbs * Mathf.Deg2Rad;
				float cp = Mathf.Cos(pr);
				Vector3 want = new Vector3(Mathf.Sin(yr) * cp, -Mathf.Sin(pr), Mathf.Cos(yr) * cp);
				Vector3 restFwd = Vector3.zero;
				try { restFwd = par.rotation * headRestLocal * headFaceAxis; } catch { }
				if (restFwd.sqrMagnitude > 1e-6f) {
					try { targetHeadLocal = ComposeHeadLocal(par, want); } catch { }
					headLocalTargetSet = true;
				}
			}
			if (!headSeen) {              // ilk pakette zıplama olmasın
				smoothHeadPitch = targetHeadPitch;
				smoothHeadYaw = targetHeadYaw;
				smoothHeadLocal = targetHeadLocal;
				headSeen = true;
			}
		}

		/// <summary>Kare başına çağrılır: kafa bakışını hedefe doğru yumuşatır.</summary>
		void StepHeadSmoothing(float dt) {
			if (!headRestCaptured || headBone == null) return;
			// Paket ~19 Hz, kare 60+ Hz: doğrudan yazmak titrer.
			float k = dt <= 0f ? 1f : 1f - Mathf.Exp(-HEAD_SMOOTHING_HZ * dt);

			if (headLocalTargetSet) {
				// Hedef tabanlı bakış (bot/NPC): doğrudan yerel dönüş hedefi.
				smoothHeadLocal = Quaternion.Slerp(smoothHeadLocal, targetHeadLocal, k);
				headBone.localRotation = smoothHeadLocal;
				// Euler sayaçlarını da tut: Describe() bunları basıyor.
				Vector3 e = smoothHeadLocal.eulerAngles;
				smoothHeadPitch = e.x > 180f ? e.x - 360f : e.x;
				smoothHeadYaw = e.y;
				return;
			}

			// Euler tabanlı bakış (ağ kuklası, pitch/yaw paketinden).
			// YANLIŞ: headRestLocal * Euler(pitch, yaw, 0) — kemik +Y yüz,
			// +Z değil; bu eksenleri karıştırır (yukarı→sağa, aşağı→sola).
			// DOĞRU: ApplyHeadRotation / LookAtWorld ile aynı — dünya yönünden
			// FromToRotation ile yerel hedef kur.
			smoothHeadPitch = Mathf.Lerp(smoothHeadPitch, targetHeadPitch, k);
			smoothHeadYaw = Mathf.Lerp(smoothHeadYaw, targetHeadYaw, k);

			Transform par = headBone.parent;
			if (par != null) {
				// pitch>0 = aşağı (kamera euler.x); yaw = gövdeye göre ofset.
				// MUTLAK dünya yaw = gövde dünya yaw + gövdeye göreli ofset.
				float bodyYaw = transform.eulerAngles.y;
				// spawnBias YOK: restFwd zaten headRestLocal (bind pose) icerir.
				float yawAbs = bodyYaw + smoothHeadYaw - headBiasYaw;
				float pitchEff = smoothHeadPitch - headBiasPitch;
				float pr = pitchEff * Mathf.Deg2Rad;
				float yr = yawAbs * Mathf.Deg2Rad;
				float cp = Mathf.Cos(pr);
				Vector3 want = new Vector3(Mathf.Sin(yr) * cp, -Mathf.Sin(pr), Mathf.Cos(yr) * cp);
				Vector3 restFwd = par.rotation * headRestLocal * headFaceAxis;
				if (restFwd.sqrMagnitude > 1e-6f) {
					// ROLL'SUZ: yalnizca dunya dikey + yatay sag ekseni.
					headBone.localRotation = ComposeHeadLocal(par, want);
				}
				else {
					headBone.localRotation = headRestLocal;
				}
			}
			else {
				// Parent yoksa fallback (olmamalı).
				headBone.localRotation = headRestLocal
					* Quaternion.Euler(smoothHeadPitch, smoothHeadYaw, 0f);
			}
		}

		/// <summary>Yumuşatma sertliği (1/s). Yüksek = daha sıkı takip.</summary>
		const float HEAD_SMOOTHING_HZ = 12f;

		/// <summary>
		/// `from` vektorunden `to` vektorune, `axis` etrafindaki ISARETLI aci
		/// (derece).
		///
		/// Unity 5.0.0f4'te `Vector3.SignedAngle` YOK (5.1'de eklendi), bu
		/// yuzden elle hesaplanir. Eksene dik izdusum alir, acos ile aciyi,
		/// `axis x (from x to)` isaretiyle yonu bulur.
		/// </summary>
		static float SignedAngleAround(Vector3 from, Vector3 to, Vector3 axis) {
			Vector3 ax = axis.normalized;
			Vector3 a = Vector3.ProjectOnPlane(from, ax);
			Vector3 b = Vector3.ProjectOnPlane(to, ax);
			float ma = a.magnitude, mb = b.magnitude;
			if (ma < 1e-6f || mb < 1e-6f) return 0f;   // eksene dik durum
			a /= ma; b /= mb;
			float cos = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
			float ang = Mathf.Acos(cos) * Mathf.Rad2Deg;
			float sign = Mathf.Sign(Vector3.Dot(ax, Vector3.Cross(a, b)));
			if (sign == 0f) sign = 1f;
			return ang * sign;
		}

		/// <summary>
		/// Kafanin YEREL donusunu, istenen DUNYA yuz yonune gore ROLL'SUZ
		/// kurar.
		///
		/// Iki donus ve SADECE iki:
		///   1. dunya dikey ekseni (`Vector3.up`) etrafinda YAW,
		///   2. yaw sonrasi kalan yatay "sag" ekseni etrafinda PITCH.
		/// Birbirine dik olduklari icin hicbir roll bileseni olusamaz.
		///
		/// `Quaternion.FromToRotation` KULLANILMAZ: en kisa yay rotasyonu
		/// secer ve ara eksende egik oldugunda yatay olmayan bir eksen
		/// uretir -> kafa yana yatar. Canli belirti: "yukari bakinca saga,
		/// asagi bakinca sola yatiyor".
		/// </summary>
		Quaternion ComposeHeadLocal(Transform par, Vector3 want) {
			Vector3 restFwd = par.rotation * headRestLocal * headFaceAxis;
			if (restFwd.sqrMagnitude < 1e-6f) restFwd = par.forward;
			restFwd = restFwd.normalized;
			want = want.normalized;
			if (Vector3.Dot(restFwd, want) > 0.99999f) return headRestLocal;

			// 1) YAW: dunya dikey ekseni etrafinda, yatay izdusumlar arasinda.
			float yawDeg = SignedAngleAround(restFwd, want, Vector3.up);
			Quaternion yawQ = Quaternion.AngleAxis(yawDeg, Vector3.up);

			// 2) PITCH: yaw sonrasi kalan yonun "sag" ekseni etrafinda.
			//    right = up x afterYaw  ->  daima yatay ve `afterYaw`a dik.
			Vector3 afterYaw = yawQ * restFwd;
			Vector3 right = Vector3.Cross(Vector3.up, afterYaw);
			if (right.sqrMagnitude < 1e-6f) right = Vector3.right;   // dik durum
			right = right.normalized;
			float pitchDeg = SignedAngleAround(afterYaw, want, right);
			Quaternion pitchQ = Quaternion.AngleAxis(pitchDeg, right);

			// Unity'de `A * B` = once B, sonra A. Yani once yaw, sonra pitch.
			Quaternion worldNew = pitchQ * yawQ * par.rotation * headRestLocal;
			return Quaternion.Inverse(par.rotation) * worldNew;
		}

		static float Clamp(float v, float lo, float hi) {
			if (v < lo) return lo;
			if (v > hi) return hi;
			return v;
		}

		Transform shoulderR;
		Transform elbowR;
		bool armProbed;

		void ProbeArm() {
			armProbed = true;
			// Kemik adlari extracted_npc rig'inden dogrulanmistir
			// (rig_bone_names): shoulder_right -> arm_right -> hand_right.
			// Dirsek ayri kemik DEGIL, arm_right dirsek eklemidir.
			string[] shoulders = { "shoulder_right", "Shoulder_R", "shoulder_R", "ShoulderRight",
				"upperarm_right", "UpperArm_R", "upper_arm_right", "arm_right" };
			for (int i = 0; i < shoulders.Length; i++) {
				shoulderR = FindChildRecursive(transform, shoulders[i]);
				if (shoulderR != null) break;
			}
			if (shoulderR != null) {
				string[] elbows = { "arm_right", "Arm_R", "arm_R",
					"elbow_right", "Elbow_R", "elbow_R", "ElbowRight",
					"forearm_right", "ForeArm_R", "forearm_R", "lower_arm_right" };
				for (int i = 0; i < elbows.Length; i++) {
					elbowR = FindChildRecursive(shoulderR, elbows[i]);
					if (elbowR != null) break;
				}
				if (elbowR == null && shoulderR.childCount > 0) {
					try { elbowR = shoulderR.GetChild(0); } catch { elbowR = null; }
				}
			}
			SmpLog.Log("[ARM] right shoulder='" + (shoulderR != null ? shoulderR.name : "-")
				+ "' elbow='" + (elbowR != null ? elbowR.name : "-") + "'");
		}

		/// <summary>
		/// Sag kol esyaya uzanir (LateUpdate: animasyon resetinden sonra).
		///
		/// Yontem (kafa takibindeki eksen olcumu gibi kemik ekseni tahmini
		/// YOK): dirsek-sap arasi su ANKI kol yonu ile omuz-esya yonu
		/// arasindaki fark donusu omuza yazilir. Animasyon her kare sifirlar,
		/// birikme olmaz; 75 derece tavan dogal durur. Esya yoksa hice
		/// dokunulmaz (restore gerekmez). Otururken/oluyken calismaz.
		/// </summary>
		/// <summary>Kol hedefi tazelik penceresi (sn). Poz 10 Hz akar;
		/// bundan eski hedef bayattir (birakma paketi edge-case) ve kol
		/// havada takili kalirdi ("ben alinca onun eli kalkiyor").</summary>
		const float ARM_TARGET_FRESH = 2.5f;

		void StepHeadBias(float dt) {
			if (!headSeen || headBone == null) return;
			if (PacketAge > 2f) return;
			// Deadzone kullan: 0.01 cok kucuk, oyun gurgultusu bias ogrenmeyi engelliyordu.
			// HEAD_DEADZONE = 2.5 derece.
			if (Mathf.Abs(targetHeadPitch) > HEAD_DEADZONE) return;
			if (Mathf.Abs(targetHeadYaw) > HEAD_DEADZONE) return;
			// Yuz ekseni bu asamada kesinlesmis olmali.
			CaptureHeadFaceAxis();
			Vector3 fwd;
			try { fwd = headBone.rotation * headFaceAxis; } catch { return; }
			if (fwd.sqrMagnitude < 1e-6f) return;
			fwd = fwd.normalized;
			float bodyYaw = 0f;
			try { bodyYaw = transform.eulerAngles.y; } catch { return; }
			float measYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
			float measDown = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
			float eYaw = Mathf.DeltaAngle(bodyYaw, measYaw);
			float ePitch = measDown;
			if (Mathf.Abs(eYaw) > 45f || Mathf.Abs(ePitch) > 45f) return;
			float k = 1f - Mathf.Exp(-1f * dt);
			headBiasYaw = Clamp(headBiasYaw + eYaw * k, -30f, 30f);
			headBiasPitch = Clamp(headBiasPitch + ePitch * k, -30f, 30f);
		}

		void StepArmReach() {
			if (shoulderR == null || elbowR == null) return;
			if (attached || dead) return;
			Vector3 itemPos = Vector3.zero;
			bool hasItem = false;
			float nowA = 0f;
			try { nowA = Time.time; } catch { }
			bool freshCarry = false, freshHeld = false;
			try { freshCarry = nowA - CarrySync.LastRemoteMsg < ARM_TARGET_FRESH; } catch { }
			try { freshHeld = nowA - HeldItemSync.LastRemoteMsg < ARM_TARGET_FRESH; } catch { }
			try {
				if (freshCarry && InteractionManager.Carry != null && InteractionManager.Carry.TryGetRemoteCarriedPos(out itemPos)) hasItem = true;
				else if (freshHeld && HeldItemSync.TryGetRemoteHeldPos(out itemPos)) hasItem = true;
			}
			catch { hasItem = false; }
			if (!hasItem) return;
			Vector3 sp, ep;
			try { sp = shoulderR.position; ep = elbowR.position; } catch { return; }
			Vector3 cur = ep - sp;
			Vector3 want = itemPos - sp;
			if (cur.sqrMagnitude < 1e-8f || want.sqrMagnitude < 1e-8f) return;
			if (want.magnitude > 2.5f) return;
			Quaternion d;
			try { d = Quaternion.FromToRotation(cur.normalized, want.normalized); }
			catch { return; }
			float ang = 0f;
			Vector3 ax = Vector3.up;
			try { d.ToAngleAxis(out ang, out ax); } catch { return; }
			if (ang < 1f) return;
			const float MAX_REACH = 75f;
			if (ang > MAX_REACH) {
				try { d = Quaternion.AngleAxis(MAX_REACH, ax); } catch { return; }
			}
			try { shoulderR.rotation = d * shoulderR.rotation; } catch { }
		}

		Transform FindChildRecursive(Transform parent, string name) {
			foreach (Transform child in parent) {
				if (string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
					return child;
				Transform found = FindChildRecursive(child, name);
				if (found != null) return found;
			}
			return null;
		}
	}
}