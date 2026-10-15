using System;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// YEREL OYUNCUNUN DURUMU — oturma, eğilme, ölüm.
	///
	/// NEDEN AYRI PAKET (PlayerPos'un içine değil)
	/// ------------------------------------------
	/// PlayerPos 20 Hz ve UNRELIABLE: konum kaybolabilir, kaybolması
	/// sorun değil (bir sonraki karede düzelir). Durum bayrakları farklı:
	/// bayrak kaybolursa uzak oyuncu SİSTEMSİZ olarak ölü/oturuyor
	/// görünür ve bir daha düzelmez. Bu yüzden:
	///   * paket GÜVENLİ
	///   * yalnızca DEĞİŞİNDE gönderilir (+ 1 Hz kalp atışı, çünkü
	///     yeniden bağlanmada karşı tarafın bayrağı sıfırlanabilir)
	///   * hız DEĞİŞKENİ taşınır: uzak taraf hızı kendi interpolatöründen
	///     hesaplayabiliyor ama eklemek 4 byte ve titremeyi kesiyor
	///
	/// EĞİLME NASIL ÖLÇÜLÜR
	/// ---------------------
	/// Kamera ile CharacterController (ayak) arasındaki Y FARKI. Dünya Y'si
	/// değil: arazi eğimi ve yokuş referansı sürükler ve ayaktaki oyuncu
	/// "eğilmiş" gider (canlı peer kanıtı). Referans dik duruşun EN YÜKSEK
	/// ölçümü, artı 0.12/0.06 m histerezis. Araçta eğilme sıfırlanır.
	///
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class LocalPlayerState {

		/// <summary>Bayraklar.</summary>
		public const byte FLAG_SITTING = 1;
		public const byte FLAG_CROUCH = 2;
		public const byte FLAG_DEAD = 4;
		public const byte FLAG_FINGER = 8;
		public const byte FLAG_SMOKING = 16;

		const float HEARTBEAT = 1f;
		const float SEND_COOLDOWN = 0.2f;

		// Gönderilecek son durum
		static byte lastFlags;
		static byte lastCrouch;
		static float lastEyeOffset;
		static float nextSend;
		static float nextBeat;

		// Kalibrasyon
		static float referenceEyeHeight;
		static float referenceHeight;
		static int calibSamples;
		/// <summary>Referans yoksa kullanılacak örnek sayısı (yalnızca denetleyici bulunamazsa).</summary>
		const int CALIB_SAMPLES = 8;
		/// <summary>Histerezis: bu düşüşten büyükse eğilme BAŞLAR.</summary>
		public const float CROUCH_ENTER = 0.12f;
		/// <summary>Histerezis: bu düşüşten küçükse eğilme BİTER.</summary>
		public const float CROUCH_EXIT = 0.06f;
		/// <summary>Histerezis durumu (canlı peer kanıtı: salınımlı kukla).</summary>
		static bool crouching;
		static bool prevLoggedCrouching;
		static Transform fingerObj;
		static Transform smokeObj;
		static float nextGestureResolve;
		static bool lastFinger;
		static bool lastSmoking;
		/// <summary>Ayakta göz yüksekliğinin alt sınırı (m). Altı ölçüm geçersiz sayılır.</summary>
		public const float EYE_MIN = 1.0f;
		/// <summary>Ayakta göz yüksekliğinin üst sınırı (m). Üstü ışınlanma sayılır.</summary>
		public const float EYE_MAX = 2.4f;
		/// <summary>Tek karede bu kadar değişim (m) ışınlanma kabul edilir.</summary>
		public const float TELEPORT_JUMP = 1f;

		/// <summary>KarakterController'ı elle atamak için (keşif bulamazsa).</summary>
		public static CharacterController Controller;

		/// <summary>Son gönderilen bayraklar ('smp player' tanısı için).</summary>
		public static byte LastFlags { get { return lastFlags; } }

		/// <summary>Yerel oyuncu aracın içinde mi? VehicleRegistry yazar.</summary>
		public static bool InVehicle;
		/// <summary>Yerel oyuncu ölü mü? Oyun/oyuncu yazar.</summary>
		public static bool Dead;

		// ------------------------------------------------------------------
		// Ölçüm
		// ------------------------------------------------------------------

		/// <summary>
		/// Eğilme miktarını ölçer: 0 = dik, 1 = tam eğilme.
		///
		/// ÖLÇÜM NEDEN AYAK YÜKSEKLİĞİNE GÖRE (canlı peer kanıtı)
		/// ---------------------------------------------------
		/// Eski sürüm DÜNYA Y'sini referans alıyordu ve yavaşça (0.3/s)
		/// kamerayı izliyordu. Merdiven inişinde / yokuş aşağı yürüyünce
		/// kamera kalıcı olarak alçalıyor, referans geride kalıyor ve
		/// AYAKTA duran oyuncu "eğilmiş" gönderiliyordu: karşı tarafta
		/// kukla sürekli çömeliyordu (canlı şikayet: "karşıdan
		/// bakınca çömeliyo").
		///
		/// Şimdi referans AYAK YÜKSEKLİĞİ: `kameraY - denetleyiciY`.
		/// Arazi eğimi, yokuş, kat farkı ve zıplama bu farkı ETKİLEMEZ,
		/// çünkü ikisi de birlikte hareket eder. Sadece gerçek eğilme
		/// (kamera gövdeye göre iner) farkı değiştirir.
		///
		/// Histerezis: 0.12 m'den büyük düşüş eğilme sayılır,
		/// 0.06 m'nin altına inince bırakılır. Tek eşik olsaydı
		/// kameradaki 1-2 cm salınım kuklayı sürekli açıp kapardı.
		/// </summary>
		static byte MeasureCrouch(out float eyeOffset) {
			eyeOffset = 0f;
			try {
				Camera cam = Camera.main != null ? Camera.main : Camera.current;
				if (cam == null) return 0;

				CharacterController cc = Controller != null ? Controller : PlayerMotion.Controller;
				Transform feet = cc != null ? cc.transform : null;

				float y = cam.transform.position.y;
				float h;
				if (feet != null) {
					// AYAK YÜKSEKLİĞİ: arazi bağımsız.
					h = y - feet.position.y;
				} else {
					// Denetleyici yok: eski dúnya-Y yolu (yavaş uyarlama).
					if (calibSamples < CALIB_SAMPLES) {
						referenceEyeHeight += y;
						calibSamples++;
						if (calibSamples >= CALIB_SAMPLES) {
							referenceEyeHeight /= CALIB_SAMPLES;
							// Yeni referans = ölçülen ayak yüksekliğine de
							// çevrilir; ikisi de aynı birimi kullanıyor.
							referenceHeight = referenceEyeHeight;
						}
						return 0;
					}
					if (referenceEyeHeight <= 0.01f) return 0;
					h = y - referenceEyeHeight + referenceHeight;
				}

				// KALİBRASYON: dik duruş EN YÜKSEK ölçümdür, yani referans ORTALAMA
				// değil MAKSİMUM olmalı. Ortalama, spawn anında eğilmiş bir
				// oyuncuyu zehirlerdi.
				//
				// IŞINLANMA KORUMASI (canlı log kanıtı, 22:31:44):
				//   "crouch: ref=42.12 m measured=0.00"
				// Kayıt yüklenirken kamerayla denetleyici TEK KARE için 42 m
				// ayrı düşüyor (aynı olayın izi: "v=90.72 m/s").
				// max() ile alınan referans 42'ye sabitleniyor ve üstel geri çekilme
				// çok yavaş (0.4/s) olduğu için ~15 saniye boyunca drop>0
				// geliyor: kukla spawn olur olmaz "eğilmiş" görünüyordu.
				// şikayet birebir "hosta spawn olduktan sonra çömeliyo" idi.
				//
				// ÇÖZÜM 1: fiziksel olarak olası aralığı dşındaki ölçüm
				// atılır (ayakta göz yüksekliği 1.0-2.4 m).
				// ÇÖZÜM 2: tek karede 1 m'den fazla sıçrama = ışınlanma;
				// ölçüm yeni referans olarak alınır, anında toparlanır.
				if (h < EYE_MIN || h > EYE_MAX) {
					// Araçta kamerası koltuk hizasında iner, menüde yukarıda
					// bakar: bunlar eğilme değil, ölçülebilir göz yüksekliği de
					// değil. Yalnızca referansı bozmamak için çıkılıyoruz.
					if (h > EYE_MAX) referenceHeight = h;
					crouching = false;
					return 0;
				}
				if (referenceHeight <= 0.3f
					|| Mathf.Abs(referenceHeight - h) > TELEPORT_JUMP) {
					referenceHeight = h;
					crouching = false;
					return 0;
				}

				if (h > referenceHeight) { referenceHeight = h; crouching = false; }
				else if (!crouching) {
					// Dik duruştan biraz daha alçak: referansı yavaşça
					// yukarı çek (kamera sıçraması kalıcı olmasın).
					float dt = Time.deltaTime;
					if (dt <= 0f) dt = 0.02f;
					referenceHeight += (h - referenceHeight) * (1f - Mathf.Exp(-0.4f * dt));
				}
				if (referenceHeight <= 0.3f) return 0;

				float drop = referenceHeight - h;
				if (drop > CROUCH_ENTER) crouching = true;
				else if (drop < CROUCH_EXIT) crouching = false;
				if (!crouching) return 0;

				// 0.45 m düşüş = tam eğilme. Histerezis eşiğini
				// çıkarıp kalanı 0..1'e ölçekle.
				float t = (drop - CROUCH_EXIT) / 0.45f;
				if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
				eyeOffset = -drop;
				return (byte)(t * 100f);
			}
			catch { return 0; }
		}

		// ------------------------------------------------------------------
		// Gönderim
		// ------------------------------------------------------------------

		/// <summary>
		/// Yerel durumu ölçer (remote ŞART DEĞİL). Test botu taklidi de
		/// bunu kullanır: ağ bağlantısı yokken bile Tick erken döner ama
		/// Sample çalışır.
		/// </summary>
		public static void Sample(out byte flags, out byte crouch, out float eye) {
			flags = 0;
			crouch = 0;
			eye = 0f;
			// Menüde kalibrasyon YOK: menü kamerası yüksekten bakar, ilk
			// ölçümlerde zehirlenen referans dünyaya taşınır ve kukla
			// dakikalarca çömelik/yere yapışık görünür (canlı peer kanıtı).
			// Kalibrasyon yalnızca dünyada çalışır (PostLoad sıfırlar).
			if (SatsumaRegistry.Root == null) {
				referenceEyeHeight = 0f;
				referenceHeight = 0f;
				calibSamples = 0;
				crouching = false;
				return;
			}
			try {
				crouch = MeasureCrouch(out eye);
				// Araçta kamera koltuk hizasına iner; bu eğilme DEĞİL.
				// Oturma zaten ayrı bayrakla gidiyor, eğilme sıfırlanır
				// (yoksa kukla koltukta çömelmiş görünür).
				if (InVehicle) { crouch = 0; eye = 0f; crouching = false; }
				if (InVehicle) flags |= FLAG_SITTING;
				if (Dead) flags |= FLAG_DEAD;
				if (crouch > 8) flags |= FLAG_CROUCH;
				SampleGestures(ref flags);
			}
			catch { }
		}

		/// <summary>
		/// 'smp player' tanısı: eğilme ölçümü.
		///
		/// CANLI ÖLÇÜM YAPILIR. Gerekçe: Tick() bağlantı yokken erkenden
		/// dönüyor, yani tek başına test ederken referans hiç dolmuyordu ve
		/// satır "ref=0.00 amount=0" diye yanıltıcı biçimde sıfır görünüyordu.
		/// Ölçüm gönderim yapmaz; sadece referansı ve değeri tazeler, yani
		/// Tick'in yapacağı işin aynısı — güvenli.
		/// </summary>
		/// <summary>
		/// El hareketleri: MiddleFinger / Smoking nesneleri aktif mi?
		///
		/// Dumper kaniti (dump2): PLAYER/.../FPSCamera/FPSCamera/MiddleFinger
		/// (+RayCast) ve .../Smoking (+Start/Hand FSM'leri). Oyuncu parmak
		/// veya sigara hareketi yapinca oyun bu nesneleri acar; tus eslemeden
		/// bagimsiz oldugu icin yeniden esleme bozmaz.
		/// </summary>
		static void SampleGestures(ref byte flags) {
			try {
				float now = Time.realtimeSinceStartup;
				if ((fingerObj == null || smokeObj == null) && now >= nextGestureResolve) {
					nextGestureResolve = now + 2f;
					ResolveGestureObjects();
				}
				bool finger = false, smoking = false;
				try { finger = fingerObj != null && fingerObj.gameObject.activeInHierarchy; } catch { }
				try { smoking = smokeObj != null && smokeObj.gameObject.activeInHierarchy; } catch { }
				if (finger != lastFinger) {
					lastFinger = finger;
					SmpLog.Log("[ACT] GESTURE finger " + (finger ? "on" : "off"));
				}
				if (smoking != lastSmoking) {
					lastSmoking = smoking;
					SmpLog.Log("[ACT] GESTURE smoking " + (smoking ? "on" : "off"));
				}
				if (finger) flags |= FLAG_FINGER;
				if (smoking) flags |= FLAG_SMOKING;
			} catch { }
		}

		static void ResolveGestureObjects() {
			try {
				GameObject root = null;
				try { root = GameObject.Find("PLAYER"); } catch { }
				if (root == null) return;
				if (fingerObj == null) {
					Transform t = null;
					try { t = root.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/MiddleFinger"); } catch { }
					if (t == null) t = FindByName(root.transform, "MiddleFinger", 600);
					if (t != null) fingerObj = t;
				}
				if (smokeObj == null) {
					Transform t = null;
					try { t = root.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Smoking"); } catch { }
					if (t == null) t = FindByName(root.transform, "Smoking", 600);
					if (t != null) smokeObj = t;
				}
			} catch { }
		}

		static Transform FindByName(Transform root, string name, int budget) {
			if (root == null || budget <= 0) return null;
			int n = 0;
			try { n = root.childCount; } catch { return null; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = root.GetChild(i); } catch { continue; }
				if (c == null) continue;
				if (string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase)) return c;
			}
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = root.GetChild(i); } catch { continue; }
				if (c == null) continue;
				Transform f = FindByName(c, name, budget - n);
				if (f != null) return f;
			}
			return null;
		}

		public static string Describe() {
			byte liveFlags, liveCrouch;
			float liveEye;
			try { Sample(out liveFlags, out liveCrouch, out liveEye); }
			catch { liveFlags = 0; liveCrouch = 0; liveEye = 0f; }
			byte amount = lastCrouch != 0 ? lastCrouch : liveCrouch;
			return "crouch: ref=" + referenceHeight.ToString("F2")
				+ " m measured=" + (referenceHeight > 0f ? liveEye.ToString("F2") : "n/a")
				+ " amount=" + amount
				+ (crouching ? " CROUCHING" : " standing")
				+ " flags=" + lastFlags
				+ (InVehicle ? " in-vehicle" : "")
				+ (Dead ? " dead" : "");
		}

		/// <summary>Kare başına: değiştiyse veya kalp atışı zamanıysa gönder.</summary>
		public static void Tick() {
			try {
				if (!SteamTransport.HasRemote) return;
				float now = Time.time;
				if (now < nextSend) return;
				nextSend = now + SEND_COOLDOWN;

				byte flags;
				byte crouch;
				float eye;
				Sample(out flags, out crouch, out eye);

				if (crouching != prevLoggedCrouching) {
					prevLoggedCrouching = crouching;
					SmpLog.Log("[ACT] CROUCH " + (crouching ? "down amount=" + crouch : "up"));
				}

				bool changed = flags != lastFlags
					|| crouch != lastCrouch
					|| Mathf.Abs(eye - lastEyeOffset) > 0.02f;

				bool beat = now >= nextBeat;
				if (!changed && !beat) return;
				if (beat) nextBeat = now + HEARTBEAT;

				lastFlags = flags;
				lastCrouch = crouch;
				lastEyeOffset = eye;
				Send(flags, crouch, eye);
			}
			catch (System.Exception e) {
				SmpLog.Warning("player state send failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		static void Send(byte flags, byte crouch, float eye) {
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.PlayerState);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(flags);
				w.Write(crouch);
				w.Write(eye);
				w.Flush();
				SteamTransport.SendReliable(Protocol.Finish(s));
			}
			catch (System.Exception e) {
				SmpLog.Warning("player state write failed: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Alma
		// ------------------------------------------------------------------

		/// <summary>Karşı tarafın durum paketini uygular.</summary>
		public static void OnMessage(BinaryReader r) {
			try {
				byte flags = r.ReadByte();
				byte crouch = r.ReadByte();
				float eye = r.ReadSingle();

				// Bayraklari kukla modeline yaz. Oturma BAYRAGI burada istek
				// olarak kalir; koltuga baglama arac bilgisidir ve
				// SyncedVehicle.FindSeat -> AttachPeer zinciriyle olur.
				RemotePlayerBehaviour body = RemotePlayerSync.Body;
				if (body != null) {
					bool sitting = (flags & FLAG_SITTING) != 0;
					bool crouching = (flags & FLAG_CROUCH) != 0;
					bool dead = (flags & FLAG_DEAD) != 0;
					body.ApplyFlags(sitting, crouching ? crouch : (byte)0, dead);
					bool finger = (flags & FLAG_FINGER) != 0;
					bool smoking = (flags & FLAG_SMOKING) != 0;
					body.ApplyGestures(finger, smoking);
					if (crouching != prevPeerCrouch) {
						prevPeerCrouch = crouching;
						SmpLog.Log("[ACT] CROUCH peer " + (crouching ? "down" : "up"));
					}

					// Oturma istegi geldi ama kukla ayakta: koltuk bulunmamis
					// olabilir (arac kaydedilmemis). Klavuz mesaj degil, log.
					if (sitting && !body.Seated && !seatWarned) {
						seatWarned = true;
						SmpLog.Warning("peer is sitting but has no seat to attach to "
							+ "(the vehicle is not in the synced vehicle list).");
					}
				}
			}
			catch (System.Exception) {
				Telemetry.BadPackets++;
				SmpLog.Warning("player state packet rejected.");
			}
		}

		/// <summary>Oturma uyarisi bir kez yazilir, her pakette tekrar degil.</summary>
		static bool seatWarned;
		static bool prevPeerCrouch;

		// ------------------------------------------------------------------
		// Yaşam döngüsü
		// ------------------------------------------------------------------

		/// <summary>Dünya değişiminde kalibrasyonu sıfırlar.</summary>
		public static void Reset() {
			lastFlags = 0; lastCrouch = 0; lastEyeOffset = 0f;
			nextSend = 0f; nextBeat = 0f;
			referenceEyeHeight = 0f; referenceHeight = 0f; calibSamples = 0;
			crouching = false;
			fingerObj = null;
			smokeObj = null;
			nextGestureResolve = 0f;
			lastFinger = false;
			lastSmoking = false;
			InVehicle = false; Dead = false;
			Controller = null;
			seatWarned = false;
		}
	}
}