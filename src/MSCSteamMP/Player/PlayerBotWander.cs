using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// TEST BOTU: bundle'daki oyuncu modeliyle oyuncunun etrafında daire çizer.
	///
	/// NpcCommand ("/npc") ile üretilir. Ağ paketi YOKTUR: gövdeyi doğrudan
	/// RemotePlayerBehaviour.ApplyPose ile sürer; böylece interpolasyon,
	/// animasyon seçimi ve koltuk dışı tüm davranış ağ kuklasıyla BİREBİR
	/// aynı koddan geçer. Amaç P2P görünümünü tek başına test etmek.
	///
	/// Yükseklik sabit tutulur (hedef y = gövdenin o anki y'si): dikey hız
	/// sıfır kalır, grounded doğru okunur ve Walk klibi oynar.
	/// </summary>
	public class PlayerBotWander : MonoBehaviour {

		/// <summary>Daire yarıçapı (m).</summary>
		public float Radius = 6f;
		/// <summary>Teğetsel hız (m/s). Animasyon seçimi bunu kullanır.</summary>
		public float Speed = 1.7f;

		RemotePlayerBehaviour body;
		float angle;

		/// <summary>Gövdeye takar ve devri başlatır.</summary>
		public static PlayerBotWander Attach(RemotePlayerBehaviour b) {
			if (b == null) return null;
			PlayerBotWander w = b.gameObject.GetComponent<PlayerBotWander>();
			if (w == null) w = b.gameObject.AddComponent<PlayerBotWander>();
			w.body = b;
			w.angle = 0f;
			return w;
		}

		void Update() {
			if (body == null) {
				Object.Destroy(this);
				return;
			}
			try {
				// TAKLİT: yerel oyuncunun o anki durumu bota yazılır.
				// Eğilme/ölüm bayrakları animasyonu seçer; oturma ayrıca
				// koltuk bağını yönetir (aşağıda).
				byte flags;
				byte crouch;
				float eye;
				LocalPlayerState.Sample(out flags, out crouch, out eye);
				bool sitting = (flags & LocalPlayerState.FLAG_SITTING) != 0;
				bool dead = (flags & LocalPlayerState.FLAG_DEAD) != 0;
				body.ApplyFlags(sitting, crouch, dead);

				// DAİRE ÇİZME KAPALI: bot yerinde durur AMA SANA DÖNER ve
				// kafası da sana bakar.
				//
				// CANLI HATA (ekran: "sol kulağı bana bakıyor"): gövde
				// dönüşü yalnızca daire çizme AÇIKKEN hesaplanıyordu.
				// '/npc' varsayılan olarak WanderEnabled=false ile
				// doğuruyor, yani gövde hiç dönmüyor, doğduğu yöne bakıyor
				// kalıyordu; kafa 90° sınırına dayanıp kulağı gösteriyordu.
				if (!WanderEnabled) {
					ApplyStandingFacing();
					LookAtPlayer();
					return;
				}

				// Oyuncu araçtaysa bot da arabaya biner: sürülen aracın
				// yolcu koltuğuna oturur. Koltuk yoksa olduğu yerde bekler
				// (Idle), daire çizmeye devam etmez.
				if (sitting) {
					if (!body.Seated) {
						SyncedVehicle car = VehicleRegistry.FindLocalVehicle();
						bool ok = car != null && car.AttachBotToPassenger(body);
						if (!ok && !seatWarned) {
							seatWarned = true;
							SmpLog.Warning("test bot: no passenger seat found, waiting outside.");
						}
					}
					// Oturuyorken de baksın: koltuğun yönü aracın yönüdür,
					// oyuncu etrafa bakabiliyor, kafa onu izlesin.
					LookAtPlayer();
					return;
				}
				if (body.Seated) {
					body.DetachFromSeat();
					seatWarned = false;
				}

				Camera cam = Camera.main != null ? Camera.main : Camera.current;
				Vector3 anchor = cam != null ? cam.transform.position
					: body.transform.position;
				// ÇAPA AYAK HİZASINDA: kamera göz hizasındadır, botun
				// seviyesi 1.6 m altıdır. Eskiden çapa göz hizasındaydı ve
				// zemin kelepçesi ıskalayınca bot havada asılı kalıyordu.
				if (cam != null) anchor.y -= 1.6f;

				float dt = Time.deltaTime;
				if (dt <= 0f) dt = 0.02f;
				angle += (Speed / Radius) * dt;
				if (angle > Mathf.PI * 2f) angle -= Mathf.PI * 2f;

				Vector3 target = anchor + new Vector3(
					Mathf.Cos(angle) * Radius, 0f, Mathf.Sin(angle) * Radius);
				// ZEMİN KELEPÇESİ (her kare). Eğimli arazide sabit y ile
				// yürümek "havada yürüme" gibi görünüyordu. Hedefin 5 m
				// üstünden 30 m aşağı ışın atılır (yamaç/çukur kaçar diye
				// menzil geniş); yer 2.5 m yakınsa oraya oturulur, yoksa
				// AYAK SEVİYESİ kullanılır — asla eski yükseklik korunmaz.
				// 2.5 m sınırı tavana ışınlanmayı engeller (ev içi: çatı
				// her zaman daha yukarıdadır).
				target.y = GroundClamp(target, anchor.y, dt);

				Vector3 dir = target - body.transform.position;
				dir.y = 0f;
				Quaternion walkRot = dir.sqrMagnitude > 0.01f
					? Quaternion.LookRotation(dir)
					: body.transform.rotation;

				// GÖVDE SANA DÖNSÜN.
				//
				// NEDEN: bot daire çiziyordu, gövdesi YÜRÜME yönüne
				// (teğete) bakıyordu. Daire üzerindeki her noktada teğet
				// ile sana olan yön 90°'ye kadar ayrışıyor; kafa ±90°
				// sınırına dayanıp kulağını gösteriyordu.
				//
				// DOĞAL DAVRANIŞ: birinin etrafında yürüyen insan gövdesini
				// ona döndürür. Gövde sana dönünce kafa ofseti küçük kalır.
				Quaternion rot = FacingTowardPlayer(walkRot, dt);

				body.ApplyPose(target, rot, Speed);
				LogWalkDiag(target);
				LookAtPlayer();
			}
			catch { }
		}

		bool seatWarned;

		/// <summary>Bot, oyuncunun kafasına bakmaya devam etsin mi?</summary>
		public bool TrackPlayerHead = true;

		/// <summary>Gövde yönü uygulandı mı (LateUpdate'da bir kez çalışır)?</summary>
		bool facingApplied;

		/// <summary>
		/// Kare sonu: gövde dönüşünü ve kafa hedefini UYGULA.
		///
		/// NEDEN LATEUPDATE: Unity'de animasyon örneklemesi
		/// PreLateUpdate'te olur, yani Update()'ten SONRA. Update'te
		/// yazılan kafa dönüşünü animasyon üstüne ezdiği için kafa hep
		/// Idle klibinin pozunda kalıyordu (yana bakıyordu).
		/// </summary>
		void LateUpdate() {
			if (body == null) return;
			// Gövde dönüşü Update'te hesaplanmıştı (yürüme yönü + sana
			// bakma karışımı); burada sadece hedefe bakma uygulanır.
			LookAtPlayer();
		}

		/// <summary>
		/// Bot daire çizsin mi?
		///
		/// NEDEN AYRI BAYRAK (eskiden `enabled = false` ile kapatılıyordu):
		/// component kapatılınca Update() hiç çalışmıyor, dolayısıyla Kafa
		/// takibi de duruyordu. Halbuki '/npc' botu oyuncunun yanında
		/// DURMASINI istiyor, kafa takibi ise ayrı bir istek. Bu yüzden
		/// component hep açık; sadece daire çizme kapatılıyor.
		/// </summary>
		public bool WanderEnabled = true;

		/// <summary>Gövde dönüşünün yumuşama sertliği (1/s).</summary>
		const float TURN_HZ = 6f;

		/// <summary>
		/// Verilen dönüşü, sana bakan dönüşe YUMŞATIR.
		/// </summary>
		Quaternion FacingTowardPlayer(Quaternion from, float dt) {
			Vector3 look;
			if (body == null) return from;
			if (!PlayerHeadPosition(out look)) return from;
			Vector3 toPlayer = look - body.transform.position;
			toPlayer.y = 0f;                      // yalnız yatay: kafa eğimi ayrı
			if (toPlayer.sqrMagnitude < 0.04f) return from;
			Quaternion want = Quaternion.LookRotation(toPlayer);
			// Anında dönmek robotik durur; üstel yumuşatma.
			return Quaternion.Slerp(from, want, 1f - Mathf.Exp(-TURN_HZ * dt));
		}

		/// <summary>
		/// Bot DURURKEN gövdesini sana döndürür.
		///
		/// CANLI HATA (ekran: "sol kulağı bana bakıyor"): gövde dönüşü
		/// yalnızca daire çizme AÇIKKEN hesaplanıyordu. '/npc' varsayılan
		/// olarak WanderEnabled=false ile doğuruyor, yani gövde hiç dönmüyor
		/// ve doğduğu yöne bakıyor kalıyordu; kafa 90° sınırına dayanıp
		/// kulağı gösteriyordu.
		/// </summary>
		void ApplyStandingFacing() {
			if (body == null) return;
			float dt = Time.deltaTime;
			if (dt <= 0f || dt > 0.5f) dt = 1f / 60f;
			// YALNIZCA dönüş hedefi. Konuma dokunulmuyor.
			//
			// CANLI HATA (ekran: NPC havada uçuyor, ters gövde): burada
			// önce ApplyPose(body.transform.position, ...) çağrılıyordu ve
			// İKİ hata birden yapıyordu:
			//
			// 1) KONUMLAMA. ApplyPose gelen konumu GroundLift kadar
			//    KALDIRIR (ağ konumu ayak seviyesidir). Biz
			//    transform.position'ı veriyorduk, o da zaten kaldırılmış
			//    konumdu → her kare biraz daha yukarı, bot uçtu.
			//
			// 2) ÇİFT DÖNÜŞ. CurrentTargetRotation() model düzeltmesini
			//    İÇERİR, ama ApplyPose onu bir daha çarpıyor → model
			//    düzeltmesi iki kez uygulandı, gövde ters döndü.
			//
			// Çözüm: SetFacingOnly() yalnızca HAM dönüş hedefini yazar.
			body.SetFacingOnly(FacingTowardPlayer(body.RawTargetRotation(), dt));
		}

		/// <summary>Yerel oyuncunun GÖZ hizasındaki konumu.</summary>
		static bool PlayerHeadPosition(out Vector3 head) {
			Vector3 p;
			if (PlayerMotion.TryGetPosition(out p)) {
				// TryGetPosition AYAK konumunu verir; göz hizasına çıkar.
				head = new Vector3(p.x, p.y + 1.6f, p.z);
				return true;
			}
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) { head = Vector3.zero; return false; }
			head = cam.transform.position;
			return true;
		}

		/// <summary>
		/// Kafa bakış zincirini ölçer ('smp head'): kemik, dinlenme dönüşü,
		/// hesaplanan yön ve yazılan yerel dönüş. Teşhis için sayı verir.
		/// </summary>
		public string DescribeHead() {
			if (body == null) return "bot yok. /npc ile bir tane dogur.";
			Vector3 head;
			if (!PlayerHeadPosition(out head)) return "oyuncu konumu yok.";
			return body.DescribeHeadChain(head);
		}

		/// <summary>
		/// Botun kafasını oyuncunun KAFASINA çevirir.
		///
		/// NEDEN kafa noktası ve gövde değil: kameraya bakan bir kafa
		/// hedefi, mesh'in kafa kemiğinin dünya konumuna bakınca doğru
		/// hizalanıyor. Gövde hedefi alınsa kafa aşağı sapıyordu.
		///
		/// Görüş hattı kırpılır (LookAtWorld içinde): dikey açı
		/// HEAD_PITCH_MIN/MAX, yatay açı HEAD_YAW_LIMIT. Yani bot sana
		/// bakar ama kafayı toprağa sokmaz, kendi ekseninde tam tur da
		/// dönmez.
		/// </summary>
		void LookAtPlayer() {
			if (body == null || !TrackPlayerHead) return;
			Vector3 head;
			if (!PlayerHeadPosition(out head)) return;
			body.LookAtWorld(head);
		}

		// ------------------------------------------------------------------
		// YÜRÜRKEN TEŞHİS (1 Hz)
		// ------------------------------------------------------------------
		// BELIRTİ: kullanıcı "/npc wander" sonrası botun YERİN İÇİNDE
		// yürüdüğünü (yarım beden) bildirdi.
		//
		// Bu oturumda taban yüksekliğiyle ilgili 5 yanlış tahmin yapıldı;
		// hepsi "olmazsa görünen" idi. Bu yüzden sayı yazıyoruz.
		//
		// Üç sayı ayrı ayrı okunmalı:
		//   zemin  = ışının çarptığı yer (GroundClamp'ın hedefi)
		//   ayak   = ApplyPose'a verilen ayak konumu (= zemin + 0.02 olmalı)
		//   kok    = transform.position.y (= ayak + GroundLift olmalı)
		//   bosluk = mesh'in dünya en alçak noktası − zemin  ← GÖRÜLEN
		//
		// "BOSLUK" negatifse bot gömülü, pozitifse süzülüyor.
		float walkDiagAt;

		void LogWalkDiag(Vector3 target) {
			if (Time.time < walkDiagAt) return;
			walkDiagAt = Time.time + 1f;
			try {
				float gy = LastGroundY;
				float gap = RemotePlayerFactory.MeasureSteadyGap(
					body.gameObject, target);
				SmpLog.Log("[npc-walk] zemin=" + (float.IsNaN(gy) ? "NaN" : gy.ToString("F3"))
					+ " hedefAyak=" + target.y.ToString("F3")
					+ " kok=" + body.transform.position.y.ToString("F3")
					+ " lift=" + body.GroundLift.ToString("F3")
					+ " netAyak=" + body.NetPosition.y.ToString("F3")
					+ " | BOGLUK(mesh-zemin)=" + (float.IsNaN(gap) ? "NaN" : gap.ToString("F3")) + " m"
					+ (float.IsNaN(gap) ? "" : (gap > 0.02f ? "  -> SUZULUYOR" : (gap < -0.02f ? "  -> GOMULU" : "  -> tam yerde"))));
			}
			catch { }
		}

		/// <summary>
		/// Son zemin isabetinin Y'si (teşhis için; NaN = ışın tutmadı).
		/// </summary>
		public static float LastGroundY = float.NaN;

		/// <summary>
		/// Botun AYAK seviyesi (GroundLift UYGULANMAMIŞ), yumuşatılmış.
		/// NaN = henüz ölçülmedi.
		/// </summary>
		float smoothFeetY = float.NaN;

		/// <summary>
		/// Hedefi zemine oturtur; AYAK seviyesinde döner.
		///
		/// ------------------------------------------------------------------
		/// CANLI KÖK NEDEN (ölçüldü, tahmin edilmedi):
		///
		///   "bot yerin içinde yarım beden yürüyor".
		///   Log: zemin=-0.591  hedefAyak=-1.231  -> 0.64 m GÖMÜLÜ.
		///
		///   Eski kod tabanı `currentY` idi ve currentY = KÖK y'siydi:
		///
		///       return currentY + (aim - currentY) * (1 - e)
		///
		///   Ama `aim` bir AYAK seviyesidir ve kök = ayak + GroundLift.
		///   Yani hata, kendi çıktısını geri besliyordu. Birinci derece
		///   gecikmenin KALICI hatası:
		///
		///       hata = GroundLift / (1 - e),   e = exp(-k*dt)
		///
		///   Yüksek kare hızında (dt küçük) 1-e küçülür ve hata PATLAR.
		///
		///   ESKİDE BU GİZLİYDİ: GroundLift 0.689 iken hata o kadar
		///   büyüktü ki ±2.5f penceresine takılıyor ve `return aim`
		///   (ani düzeltme) devreye giriyordu. Yani yanlış denge
		///   hesabı YANLIŞ BİR KORUMA ARASIYLA maskeleniyordu.
		///   GroundLift -0.027 yapılınca hata pencerenin İÇİNDE kaldı
		///   ve asıl denge ortaya çıktı.
		///
		///   >>> Taban yüksekliğini düzeltmek, tabanı KULLANAN yumuşatmayı
		///   >>> da düzeltmeyi gerektiriyordu. İki hata birbirini saklıyordu.
		///
		/// ÇÖZÜM: yumuşatma kendi çıktısını geri beslemez. Sabit bir
		/// hedefe (`aim`) yaklaşan alan tutulur; kalıcı hata SIFIRDIR.
		/// Dönüş/ölçek/zemin ne olursa olsun yakınsar.
		/// ------------------------------------------------------------------
		/// </summary>
		float GroundClamp(Vector3 target, float feetY, float dt) {
			LastGroundY = float.NaN;

			// IŞIN: hedefin 10 m üstünden 20 m aşağı. Hedef zaten yerin
			// altındaysa bile zemini bulur (kayalar/çukur kaçar).
			float aim;
			try {
				RaycastHit hit;
				Vector3 origin = new Vector3(target.x, target.y + 10f, target.z);
				if (Physics.Raycast(origin, Vector3.down, out hit, 20f)) {
					LastGroundY = hit.point.y;
					// +0.02 telafi: mesh tabanı kökün 0.007 m ÜSTÜNDE ve
					// GroundLift -0.027; ikisi toplanınca ayak zeminde bitsin.
					aim = hit.point.y + 0.02f;
				}
				else {
					// Işın tutmadı: ayak seviyesine in. Bot asla havada asılı kalmaz.
					aim = feetY;
				}
			}
			catch { aim = feetY; }

			if (float.IsNaN(smoothFeetY)) smoothFeetY = aim;   // ilk kare: zıplama yok
			// Sabit hedefe üstel yaklaşım — kalıcı hata yok.
			// Merdiven eşiği gibi kademeler zıplama gibi görünmesin diye
			// yumuşak; 10/s oranı 0.3 sn'de kapanır.
			smoothFeetY += (aim - smoothFeetY) * (1f - Mathf.Exp(-10f * dt));
			return smoothFeetY;
		}

		/// <summary>Tek satır durum ('/npc list' bunu basar).</summary>
		public string Describe() {
			return "circle r=" + Radius.ToString("F1") + "m v=" + Speed.ToString("F1")
				+ "m/s angle=" + (angle * Mathf.Rad2Deg).ToString("F0") + "deg"
				+ " head=" + (TrackPlayerHead ? "follow" : "off")
				+ " (pitch " + RemotePlayerBehaviour.HEAD_PITCH_MIN.ToString("F0")
				+ ".." + RemotePlayerBehaviour.HEAD_PITCH_MAX.ToString("F0")
				+ " yaw +-" + RemotePlayerBehaviour.HEAD_YAW_LIMIT.ToString("F0") + ")"
				+ (body != null && body.Anim != null ? " " + body.Anim.Describe() : "");
		}
	}
}