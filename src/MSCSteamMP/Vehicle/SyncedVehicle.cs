using System;
using System.IO;
using System.Reflection;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// One player-drivable vehicle. Owns its PlayMaker hooks (FsmBinder) and
	/// applies what arrives from the peer. Only the driver broadcasts.
	/// </summary>
	public class SyncedVehicle : MonoBehaviour {

		public int VehicleId;
		public string VehicleName;

		/// <summary>Eylem günlüğü için son görülen motor durumu (-1 = henüz yok).</summary>
		int LastTracedEngine = -1;
		public FsmBinder Binder;

		// Direksiyon simidi aynasi (Paket D): oyun surucude CarSteeringPivot
		// (kolon) ve/veya pivot_steering_wheel (simit) dondurur. Hangisinin
		// dondugu araca gore degisir; ikisi de okunur, degisen gonderilir,
		// peer aynen yazar. Eksen/acI tahmini yok, olcum var.
		Transform steerPivot;
		Transform steerWheel;
		bool steerWheelProbed;
		Quaternion lastSentPivot = Quaternion.identity;
		Quaternion lastSentWheel = Quaternion.identity;
		float nextSteerSend;
		string lastVehKey;
		float lastVehAt = -99f;

		/// <summary>Durum degistirme sayisi bu esigi asarsa FSM "dolasmis" sayilir.</summary>
		const int CHURN_STATES = 4;

		/// <summary>Degisim sayaci bu pencereyle sifirlanir.</summary>
		const float CHURN_WINDOW = 2f;

		static int chatFsmIndex = -1;
		static int chatCount;
		static float chatWindowStart;
		static float chatLastLogAt;
		string lastSwKey;
		float lastSwAt = -99f;
		float lastEngineDiagAt = -99f;
		public const float STEER_SEND_INTERVAL = 0.1f;
		public bool LocalIsDriver;
		public bool LocalIsPassenger;
		public bool RemoteDriven;
		/// <summary>Peer bir koltuğa bağlı (sürücü veya yolcu).</summary>
		public bool RemoteSeated;

		Rigidbody body;

		/// <summary>Yolcu koltuğu pivotu (bulunduğunda bir kez önbelleklenir).</summary>
		Transform passengerSeatPoint;

		public void Initialise(int id, string name) {
			VehicleId = id;
			VehicleName = name;
			body = gameObject.GetComponent<Rigidbody>();

			Binder = new FsmBinder();
			Binder.Discover(gameObject);
			Binder.OnStateChange = delegate (int fsmIndex, string stateName) {
				// EYLEM GÜNLÜĞÜ: kabin lambası, gösterge, vitez vb. gibi araç
				// içi durum değişimleri. Önceden yalnızca paket gönderiliyordu,
				// yani hiçbir şey [ACT]'e yazılmıyordu. Artık görülebiliyor.
				//
				// CANLI KANIT: kullanıcı "iç lambasını açtım, log yok" dedi.
				// Neden: InteriorLight'ın "Flip 2" durumu pakete gidiyordu ama
				// yerel logda satır yoktu.
				string key = "?";
				try {
					if (Binder != null && fsmIndex >= 0 && fsmIndex < Binder.Tracked.Count)
						key = Binder.Tracked[fsmIndex].Key;
				}
				catch { }
				// SEL DÜZELTME (canli kanit 15:03, iki log): ayni durum ~15 ms
				// arayla YUZLERCE kez ateslendi (`Wait player` IgnitionSatsuma)
				// ve her biri LOG + PAKET uretti -> kanal boguldu, rate limiter
				// teshis satirlarini yedi. Ayni (fsm,durum) 1.5 sn susturulur;
				// GERCEK degisim aninda gider.
				string vkey = fsmIndex + ":" + stateName;
				if (string.Equals(lastVehKey, vkey, StringComparison.Ordinal)
					&& Time.time - lastVehAt < 1.5f) return;

				// v15 -- DOLASIK FSM SUSTURMASI.
				//
				// Canli log kaniti (21:38 oturumu, 345 satirin 78'u = %23):
				//   21:39:18.749 [ACT] VEHICLE Get gear    .../GearIndicator
				//   21:39:18.749 [ACT] VEHICLE N           ...
				//   21:39:18.749 [ACT] VEHICLE Check range ...
				//   21:39:18.749 [ACT] VEHICLE Wait gear   ...
				//   ... ayni dongu birkac saniyede bir tekrarli
				//
				// Yukaridaki 1.5 sn kurali (fsm, durum) CIFTINE bakiyor.
				// Bu dongu Dort FARKLI durum uretiyor, bu yuzden kural hic
				// tetiklenmedi ve tek bir FSM 78 satir uretti.
				//
				// Cozum: bir FSM kisa surede CHURN_STATES kadar durum
				// degistirirse "dolasmis" sayilir ve yalnizca
				// CHURN_WINDOW boyunca 1 kez yazilir. Sakin bir FSM
				// (kontak, cam, far) hic kisitlamaya girmez.
				float nowT = Time.time;
				if (fsmIndex != chatFsmIndex) {
					chatFsmIndex = fsmIndex;
					chatCount = 0;
					chatWindowStart = nowT;
				}
				else if (nowT - chatWindowStart > CHURN_WINDOW) {
					chatWindowStart = nowT;
					chatCount = 0;
				}
				if (chatCount < CHURN_STATES) {
					chatCount++;
				}
				else if (nowT - chatLastLogAt < CHURN_WINDOW) {
					// Dongu suruyor: gonderimi yine yap, sadece logla.
					lastVehKey = vkey;
					lastVehAt = nowT;
					VehicleRegistry.SendState(VehicleId, fsmIndex, stateName);
					return;
				}
				chatLastLogAt = nowT;

				lastVehKey = vkey;
				lastVehAt = nowT;
				SmpTrace.Act("VEHICLE", stateName, key + "  @ " + VehicleName);
				VehicleRegistry.SendState(VehicleId, fsmIndex, stateName);
			};
			Binder.OnSwitchChange = delegate (VehicleSwitch sw, bool value, float floatValue) {
				// El freni (push) gibi konumumlu düğmeler: konum float olarak gider.
				// Ayni sel duzeltmesi (durum yolundakiyle ayni gerekce).
				string skey = sw.ToString() + "=" + value + "@" + floatValue.ToString("F2");
				if (string.Equals(lastSwKey, skey, StringComparison.Ordinal)
					&& Time.time - lastSwAt < 1.5f) return;
				lastSwKey = skey;
				lastSwAt = Time.time;
				SmpTrace.Act("VEHICLE", sw.ToString(), VehicleName + "  value=" + value + "  f=" + floatValue.ToString("F2"));
				VehicleRegistry.SendSwitch(VehicleId, sw, value, floatValue);
			};
			Binder.OnEngineChange = delegate (int engine, int dash) {
				// EYLEM GÜNLÜĞÜ: motor durumu değişti.
				//
				// CANLI KANIT: eski sınıflandırma "started" kelimesini yalnızca
				// 0 -> x geçişinde yazıyordu. Oysa MapEngineState'e göre
				// "çalışıyor" = 8 (Motor running) ve marş dizisi
				// 1->4->3->5->6->8 ile bitiyor, yani log "started" demiyor,
				// her adımda "changed" diyordu. Kullanıcı "araba çalıştı" deyip
				// logda 'started' arıyordu ve bulamıyordu.
				//
				// Doğru sınıflandırma: çalışma DURUMU 8'e girince "started",
				// oradan çıkınca "stopped". Diğer geçişler starter'ın
				// meşgul olduğunu gösterir ("cranking").
				try {
					const int MOTOR_RUNNING = 8;
					string action = null;
					if (engine != LastTracedEngine) {
						if (engine == MOTOR_RUNNING) action = "started";
						else if (LastTracedEngine == MOTOR_RUNNING) action = "stopped";
						else if (engine != 0 && LastTracedEngine != 0) action = "cranking";
					}
					if (action != null) {
						SmpTrace.Engines++;
						SmpTrace.Act("ENGINE", action, VehicleName
							+ "  state " + LastTracedEngine + " -> " + engine
							+ "  dash=" + dash);
					}
					LastTracedEngine = engine;
				}
				catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("ENGINE trace failed: " + e.Message); }
				// TANI (Paket C): mars denemesi koltuk + kontak on sarti ister.
				// "Client calistiramadi" sikayetinde koltukta kim var ve
				// ElectricsOK ne, bilinmeden oyuncu-hatasi ile mod-hatasi
				// ayrilamaz. Mars ve calisma durumlarinda tek satir.
				try {
					if ((engine >= 3 && engine <= 7) || engine == 8) {
						bool elecFound = false;
						bool elec = false;
						string stName = "?";
						try {
							if (Binder != null && Binder.Starter != null) {
								HutongGames.PlayMaker.FsmBool eb = FsmVars.Bool(Binder.Starter, "ElectricsOK");
								if (eb != null) { elec = eb.Value; elecFound = true; }
								try { stName = Binder.Starter.Fsm.ActiveStateName; } catch { }
							}
						} catch { }
						string ignName = "?";
						try { if (Binder != null && Binder.Dashboard != null) ignName = Binder.Dashboard.Fsm.ActiveStateName; } catch { }
						if (Time.time - lastEngineDiagAt >= 1f) {
							lastEngineDiagAt = Time.time;
							SmpLog.Log("[ENGINE-DIAG] " + VehicleName + " state=" + engine + " (" + stName + ")"
								+ " driver=" + LocalIsDriver + " passenger=" + LocalIsPassenger
								+ " electricsOK=" + (elecFound ? elec.ToString() : "?")
								+ " ign=" + ignName);
						}
					}
				} catch { }
				VehicleRegistry.SendEngine(VehicleId, engine, dash, -1f);
			};
			Binder.OnDriverEnter = OnDriverEnter;
			Binder.OnDriverLeave = OnDriverLeave;
			// HAYOSIKO: yolcu koltuğu ikinci bir "PlayerTrigger" FSM'i.
			// Bu kancalar olmadan yolcu koltuğu hiç dinlenmiyordu ve
			// araca binen ikinci oyuncu kukla olarak hiç görünmüyordu.
			Binder.OnPassengerEnter = OnPassengerEnter;
			Binder.OnPassengerLeave = OnPassengerLeave;
			Binder.Install();
			LogVehicleCapabilities();
		}

		/// <summary>
		/// Aracın senkronlanabilir yeteneklerini AÇIKÇA yazar (araç başına bir kez).
		///
		/// Neden: "tavandaki lamba senkron olmuyor", "el freni gitmiyor" gibi
		/// şikâyetlerde tahmin yürütmek yerine, aracın o düğmeye ait FSM'i
		/// gerçekten bulup BULMADIĞINI logda görmek gerekiyor. Classify()
		/// isim eşleşmesiyle çalışır (InteriorLight, ParkingBrake...) ve
		/// araçtan araca ad farklıysa sessizce düşer. Bu satır o düşüşü
		/// görünmez olmaktan çıkarır.
		///
		/// CANLI KANIT (seats satırı): "yolcu koltuğu yok" sanılan HAYOSIKO
		/// aslında ikinci bir PlayerTrigger FSM'ine sahipti; bu satır
		/// tahmin yerine ismi gösterdi.
		/// </summary>
		void LogVehicleCapabilities() {
			if (Binder == null) return;

			string driver = FsmLabel(Binder.PlayerTrigger);
			string passenger = (Binder.PassengerTrigger == null || Binder.PassengerTrigger == Binder.PlayerTrigger)
				? "(none)" : FsmLabel(Binder.PassengerTrigger);

			// El freni tipi ayrı: kollu (Flip olayı, doğrudan senkron) ve
			// basmalı (düğme konumu sürekli yoklanır). Kullanıcı "el freni
			// senkron olmuyor" dediğinde hangisinin bağlandığı buradan görünür.
			string handbrake = FsmLabel(Binder.Handbrake)
				+ (Binder.HasLeverParkingBrake ? " (lever/Flip)"
					: Binder.HasPushParkingBrake ? " (push/polled)" : " (NOT SYNCED)");

			if (Telemetry.Verbose) SmpLog.Log("'" + VehicleName + "' seats: driver=" + driver
				+ "  passenger=" + passenger
				+ " | cabin light=" + FsmLabel(Binder.InteriorLight)
				+ "  handbrake=" + handbrake
				+ "  lights=" + FsmLabel(Binder.Lights)
				+ "  wipers=" + FsmLabel(Binder.Wipers));

			if (passenger == "(none)") {
				SmpLog.Log("'" + VehicleName + "': single-seat vehicle - "
					+ "the peer cannot ride along here.");
			}
			// Kabin lambası / el freni yoksa TEK satırda özetle.
			//
			// NEDEN: Bu mesaj "bulamadım" diyor, "yoktur" demiyor. Döküm
			// kanıtı bazı araçlarda ikisinin de gerçekten olmadığını
			// gösteriyor -- `JONNEZ ES(Clone)` (46 FSM) ve `FLATBED`
			// (9 FSM) altında ne el freni ne kabin lambası geçiyor.
			// JONNEZ bir otomobil olduğu için el freninin olmaması
			// beklenen bir durum, keşif hatası değil. Bu yüzden eski
			// "NOT synced on this vehicle" cümleleri iki ayrı satır
			// halinde ve arac başına TEKRAR basılıyordu (keşif + her
			// `smp rescan`), yani gürültüydü.
			//
			// Şimdi: eksik olanlar tek satırda listelenir, gürültü
			// azalır, ama bilgi kaybolmaz. Ayrıntı (hangi isim denendi)
			// `smp verbose` altında kalır.
			string missing = null;
			if (Binder.InteriorLight == null)
				missing = missing == null ? "cabin lamp" : missing + ", cabin lamp";
			if (Binder.Handbrake == null)
				missing = missing == null ? "handbrake" : missing + ", handbrake";
			if (missing != null)
				SmpLog.Log("'" + VehicleName + "': no FSM for " + missing
					+ " on this vehicle (not an error if the car has none).");

			if (Telemetry.Verbose) {
				if (Binder.InteriorLight == null)
					SmpLog.Debug("vehicle", "cap",
						"'" + VehicleName + "' cabin lamp: tried InteriorLight/"
						+ "ButtonInteriorLight, found nothing.");
				if (Binder.Handbrake == null)
					SmpLog.Debug("vehicle", "cap",
						"'" + VehicleName + "' handbrake: tried ParkingBrake/"
						+ "Parking Brake, found nothing.");
			}
		}

		/// <summary>FSM'i "Nesne/FSM" biçiminde yazar, yoksa "(none)".</summary>
		static string FsmLabel(PlayMakerFSM fsm) {
			if (fsm == null) return "(none)";
			try { return fsm.gameObject.name + "/" + fsm.FsmName; }
			catch { return "(unreadable)"; }
		}

		void Update() {
			// Yedek sürücü tespiti: FSM kancası ("Player in car") ise
			// LocalIsDriver hiç true olmuyor ve araç hiç gönderilmiyor.
			// dnSpy: AxisCarController/Arcader/CarController'ın HİÇBİRİNDE
			// sürücü referansı yok (yalnızca eksen adları ve fizik
			// yardımcıları). Tek güvenilir sinyal PlayMaker durum adı.
			// O yüzden durum adı eşleşmesini genişletip, kanca çalışmasa
			// bile araştırıyoruz.
			if (!LocalIsDriver) PollDriverFallback();

			// Aynı mantık yolcu koltuğu için: yolcu koltuk FSM'inin
			// durum adı "Player in passenger seat" gibi değişebiliyor ve
			// kanca bu yüzden tutmayabiliyor.
			if (!LocalIsDriver && !LocalIsPassenger) PollPassengerFallback();

			// Peer paket göndermeyi bıraktıysa aracı yerel fizikse geri ver.
			// Aksi halde araç kinematic kalır ve kimse onu süremez.
			if (!LocalIsDriver && body != null && body.isKinematic
				&& lastRemotePacket > 0f && Time.time - lastRemotePacket > 1.5f) {
				body.isKinematic = false;
				lastRemotePacket = 0f;
				RemoteDriven = false;
				// Yolcuysa kukla koltukta takılı kalmasın: bağı da
				// bırakıyoruz, aksi hâlde görünmez bir çocuk koltuğa
				// yapışık durur.
				if (RemoteSeated) {
					RemoteSeated = false;
					try { AttachPeer(false); } catch { }
				}
				Interp.Clear();
				SmpLog.Print("'" + VehicleName + "': peer stopped sending; "
					+ "local physics resumed.");
			}

			// Uzaktan araç: transformu YUMUŞATILMIŞ konuma taşı.
			//
			// Sıralama önemli: "peer durdu" kontrolü önce çalışır, çünkü o
			// durumda kinematik gövdeyi yerel fizikse bırakıyoruz ve
			// interpolasyon devam ederse aracı iki fizik sistemi birden
			// sürüklerdi.
			if (!LocalIsDriver && Interp.HasData) RenderRemoteTransform();

			if (LocalIsDriver && Binder != null) {
				Binder.PollContinuous();
				Binder.PollWindow();
				PollSteerWheel();
				PollNeedles();
			}

			// Uzaktan ses: peer sürüyorsa onun devrini bizim kopyanın
			// aktarma organına yaz ki motor sesi gaz yesin. Kinematik
			// gövdede fizik etkisi YOK (kuvvetler işlemez), yalnızca ses
			// üreten devir okunur. Tek sürücüde çalışır (WheelState global).
			if (RemoteDriven && !LocalIsDriver) {
				WriteRemoteRpm();
				ApplyPeerSteering();
				PollBrakeLamps();
			}
		}

		Component rpmOwner;
		FieldInfo rpmField;
		bool rpmProbed;

		CarController steerCtrl;
		bool steerProbed;

		/// <summary>
		/// Peer'in direksiyonunu bu aracın görsel tekerleklerine uygular.
		///
		/// CANLI KANIT: "direksiyonu çevirdiğimde arkadaşımda görünmüyor".
		/// Gelen WheelState paketleri saklanıyordu ama hiçbir araca
		/// yazılmıyordu; araç düz gidiyordu.
		///
		/// Kinematik gövdede gaz/frenin fiziksel anlamı yok (aracı zaten
		/// transform paketleriyle taşınıyor), ama DİREKSİYONUN görsel
		/// karşılığı vardır: tekerlek mesh'leri döner. Oyunun kendi kodu
		/// da CarController.steering alanından döndürdüğü için aynı alana
		/// yazıyoruz.
		/// </summary>
		void ApplyPeerSteering() {
			if (!steerProbed) {
				steerProbed = true;
				try {
					CarController[] found = GetComponentsInChildren<CarController>(true);
					if (found != null) {
						for (int i = 0; i < found.Length; i++) {
							if (found[i] != null) { steerCtrl = found[i]; break; }
						}
					}
				}
				catch { steerCtrl = null; }
				if (steerCtrl == null && Telemetry.Verbose)
					SmpLog.Debug("vehicles", "steer", "'" + VehicleName
						+ "': no CarController, peer steering not shown.");
			}
			if (steerCtrl == null) return;
			WheelSync.ApplyToRemote(steerCtrl);
		}

		/// <summary>
		/// Peer'in devrini yerel aktarma organına yazar (ses için).
		/// Fizik kinematik gövdede etkilenmez; girdi (gaz/fren) ASLA
		/// yazılmaz — yazılsaydı peer'in gazı bizim marşımızı basardı.
		/// </summary>
		void WriteRemoteRpm() {
			try {
				if (!WheelSync.RpmFresh) return;
				if (VehicleRegistry.GetSingleRemoteDriven() != this) return;
				if (!rpmProbed) {
					rpmProbed = true;
					// Drivetrain MonoBehaviour'dir (dnSpy); CarController
					// altındaki korumalı alandan çözülür (okuma yoluyla aynı).
					Component[] all = null;
					try { all = GetComponentsInChildren<Component>(); }
					catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("GetComponentsInChildren failed: " + e.Message); all = null; }
					if (all != null) {
						for (int i = 0; i < all.Length; i++) {
							Component c = all[i];
							if (c == null || !(c is CarController)) continue;
							FieldInfo dtf = null;
							try {
								dtf = c.GetType().GetField("drivetrain",
									BindingFlags.NonPublic | BindingFlags.Instance);
							}
							catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("GetField drivetrain failed: " + e.Message); dtf = null; }
							if (dtf == null) continue;
							object dt = null;
							try { dt = dtf.GetValue(c); }
							catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("GetValue drivetrain failed: " + e.Message); dt = null; }
							Component owner = dt as Component;
							if (owner == null) continue;
							FieldInfo rf = null;
							try {
								rf = dt.GetType().GetField("rpm",
									BindingFlags.Public | BindingFlags.Instance);
							}
							catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("GetField rpm failed: " + e.Message); rf = null; }
							if (rf == null) continue;
							rpmOwner = owner;
							rpmField = rf;
							break;
						}
					}
					if (rpmField == null) {
						SmpLog.Warning("'" + VehicleName + "': remote rpm write disabled (no drivetrain).");
					}
				}
				if (rpmField == null || rpmOwner == null) return;
				float rpm = WheelSync.PeakRpm;
				if (rpm < 0f) rpm = 0f; else if (rpm > 8000f) rpm = 8000f;
				rpmField.SetValue(rpmOwner, rpm);
			}
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("WriteRemoteRpm failed: " + e.Message); }
		}

		/// <summary>Karşı taraftan son transform paketinin geldiği an.</summary>
		float lastRemotePacket;

		// ------------------------------------------------------------------
		// Uzaktan araç hareketi: yumuşatma
		// ------------------------------------------------------------------

		/// <summary>
		/// Peer'in aracı için konum/yönelim yumuşatıcısı.
		///
		/// Neden ayrı bir nesne: tampon SABİT boyutludur ve kare başına
		/// tahsis yapmaz; araç başına bir kez ayrılır, sonra hiç büyümez.
		/// GC-free olması bu yüzden bozulmaz.
		/// </summary>
		public readonly VehicleInterpolator Interp = new VehicleInterpolator();

		/// <summary>
		/// Uzaktan aracı bu karede yumuşatılmış duruma taşır.
		///
		/// Eskiden paket geldiği anda Transform'a doğrudan yazılıyordu:
		///
		///   gameObject.transform.position = p;
		///   gameObject.transform.rotation = q;
		///
		/// Yani peer her paket gönderdiğinde araç O NOKTAYA IŞINLANIYORDU;
		/// 20 Hz'de saniyede 20 sıçrama = görünür titreme. Kaynak kodun kendi
		/// uyarısı bunu teyit ediyordu: "expect jitter".
		///
		/// Şimdi paketler tampona yazılıyor (<see cref="ApplyTransform"/>) ve
		/// Transform her karede <see cref="VehicleInterpolator"/> tarafından
		/// hesaplanıyor: Vector3.Lerp + Quaternion.Slerp.
		///
		/// PlayMaker ile ilişkisi: burada FSM'e hiç dokunulmuyor. Yumuşatma
		/// SAF bir görsel katmandır; motor durumu, yakıt ve vites gibi oyun
		/// değişkenleri FsmBinder üzerinden ayrı yoldan (ApplyState /
		/// ApplySwitch) gider. Bu ayrım önemli: transform yumuşatılırken
		/// oyunun FSM'i anında tepki vermeye devam eder.
		/// </summary>
		void RenderRemoteTransform() {
			Vector3 p, v, av;
			Quaternion q;
			if (!Interp.Sample(Time.time, out p, out q, out v, out av)) return;

			Transform t = gameObject.transform;
			t.position = p;
			t.rotation = q;

			// Gövde kinematik olduğu için velocity fizik olarak etkisiz;
			// yine de tutarlı tutuyoruz çünkü yerel fiziğe devrederken
			// handoff bu değerden yapılıyor.
			if (body != null) {
				body.velocity = v;
				body.angularVelocity = av;
			}
		}

		// ------------------------------------------------------------------
		// Koltuk (peer kuklası buraya bağlanır)
		// ------------------------------------------------------------------

		/// <summary>
		/// Koltuk noktasını bulur.
		///
		/// ÖNCEKİ DENEME: {"Driver Seat", "Driver", "Seat", "CarSeat",
		/// "seat", "driver", "Drive Seat"} — hiçbiri yok. Asset taraması
		/// (level2 + sharedassets) gerçek adları verdi:
		///
		///   Seat_Driver        Seat_Passenger      Seat_Rear
		///   SeatPivot          PivotSeatR          Seat
		///   pivot_seat_driver  pivot_seat_passenger
		///   Seats              bucket seat driver(Clone)
		///
		/// Yani oyunda "Driver Seat" diye bir nesne YOK; adlandırma
		/// "Seat_Driver" / "pivot_seat_driver". Bu yüzden kukla araçta
		/// boşta duruyordu.
		///
		/// Öncelik sırası ölçüme göre: en önce gerçek koltuk pivot'u,
		/// sonra genel "Seat", en son araç kökü (koltuğu bulamazsak
		/// bile araçla birlikte taşınsın).
		/// </summary>
		Transform FindSeat() {
			if (seatPoint != null) return seatPoint;
			// "DriverDoors" KALDIRILDI.
			//
			// CANLI KANIT: logda HAYOSIKO'nun yolu
			// 'HAYOSIKO(1500kg, 250)/DriverDoors/doorl/Handle' — yani
			// "DriverDoors" bir KAPI grubu, koltuk değil. Ad listesinin son
			// adayı olarak kullanılıyordu, yani gerçek koltuk bulunamayınca
			// kukla KAPI MENTEŞESİNE bağlanıyordu. Belirti: "player aracı
			// sürmeye başladığında hala ayakta duruyor arabanın içinde" ve
			// kapının yerinden kopması.
			//
			// HAYOSIKO ÖZEL: minibüs koltuk isimleri (asset taramasıyla doğrulandı)
			//   Seat_Driver        pivot_seat_driver        SeatPivot
			//   Seat_Passenger     pivot_seat_passenger     Seat_Rear
			//   Seat              pivot_seat_passenger_bucket  seat passenger(Clone)
			string[] names = {
				"Seat_Driver", "pivot_seat_driver", "SeatPivot", "PivotSeatR",
				"Seat_Passenger", "pivot_seat_passenger", "pivot_seat_passenger_bucket", "seat passenger(Clone)",
				"Seat", "seat", "Seat_Passenger", "pivot_seat_passenger",
				"Seats",
				"DriveTrigger", "PlayerTrigger",  // Oyun içi tetikleyici nesneleri
			};
			Transform[] all = null;
			try { all = transform.GetComponentsInChildren<Transform>(true); }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("FindSeat GetComponentsInChildren failed: " + e.Message); return null; }
			if (all == null) return null;

			for (int i = 0; i < names.Length; i++) {
				for (int j = 0; j < all.Length; j++) {
					if (all[j] == null) continue;
					if (string.Equals(all[j].name, names[i],
							System.StringComparison.OrdinalIgnoreCase)) {
						seatPoint = all[j];
						return seatPoint;
					}
				}
			}
			return null;
		}

		Transform seatPoint;

		/// <summary>
		/// Test botunu yolcu koltuğuna oturtur. Oyuncu sürerken bot da
		/// arabaya biner ("pozisyonumu alsın"). Bulunamazsa false döner,
		/// bot olduğu yerde bekler.
		/// </summary>
		public bool AttachBotToPassenger(RemotePlayerBehaviour bot) {
			if (bot == null) return false;
			try {
				Transform[] all = transform.GetComponentsInChildren<Transform>(true);
				if (all == null) return false;
				string[] names = {
					"Seat_Passenger", "pivot_seat_passenger",
					"pivot_seat_passenger_bucket", "seat passenger(Clone)",
					"bucket seat passenger(Clone)", "Passenger",
				};
				for (int i = 0; i < names.Length; i++) {
					for (int j = 0; j < all.Length; j++) {
						if (all[j] == null) continue;
						if (string.Equals(all[j].name, names[i],
								System.StringComparison.OrdinalIgnoreCase)) {
							bot.AttachToSeat(all[j]);
							return true;
						}
					}
				}
			}
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("AttachBotToPassenger failed: " + e.Message); }
			return false;
		}

		/// <summary>Peer aracın koltuğunda: kuklayı bağla (veya bağdan çıkar).
	/// HAYOSIKO DESTEĞİ: sürücü koltuğu (FindSeat) ve yolcu koltuklarını
	/// (FindPassengerSeat) ayırır. Peer sürücüse FindSeat, yolcuysa
	/// FindPassengerSeat kullanılır.
	/// </summary>
	public void AttachPeer(bool seated, bool isDriver = true) {
		RemotePlayerBehaviour body = RemotePlayerSync.Body;
		if (body == null) {
			return;
		}

		if (!seated) {
			if (body.Seated) body.DetachFromSeat();
			return;
		}

		Transform seat = isDriver ? FindSeat() : FindPassengerSeat();
		if (seat == null) {
			if (!warnedNoSeat) {
				warnedNoSeat = true;
				SmpLog.Warning("'" + VehicleName + "' has no " + (isDriver ? "driver" : "passenger") + " seat pivot; "
					+ "the peer will stand in the car instead of sitting. "
					+ "Candidates named seat/driver/chair: " + SeatCandidates());
			}
			return;
		}
		if (!warnedSeatOk) {
			warnedSeatOk = true;
			SmpLog.Log("'" + VehicleName + "': peer seated at '" + SeatPath(seat) + "' (" + (isDriver ? "driver" : "passenger") + ").");
		}
		body.AttachToSeat(seat);
	}

	/// <summary>
	/// Yolcu koltuğunu bulur (sadece yolcu koltukları, sürücü hariç).
	/// </summary>
	Transform FindPassengerSeat() {
		if (passengerSeatPoint != null) return passengerSeatPoint;
		// Hayosiko yolcu koltukları (asset taramasıyla doğrulandı)
		// NOT: DriveTrigger / PlayerTrigger BILEREK DISLANDI.
		// Bunlar SÜRÜCÜ tetikleyicisidir, yolcu koltuk nesnesi değil.
		// Önceden listede olduğunda yolcu koltuğu bulunamazsa fallback
		// olarak sürücü tetikleyicisine bağlanıyordu → kukla yanlış konuma oturuyordu.
		string[] names = {
			"Seat_Passenger", "pivot_seat_passenger", "pivot_seat_passenger_bucket", "seat passenger(Clone)",
			"bucket seat passenger(Clone)", "Passenger", "Seat_Rear",
			"Seat", "seat", "Seats",
		};
		Transform[] all = null;
		try { all = transform.GetComponentsInChildren<Transform>(true); }
		catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("FindPassengerSeat GetComponentsInChildren failed: " + e.Message); return null; }
		if (all == null) return null;

		for (int i = 0; i < names.Length; i++) {
			for (int j = 0; j < all.Length; j++) {
				if (all[j] == null) continue;
				if (string.Equals(all[j].name, names[i], System.StringComparison.OrdinalIgnoreCase)) {
					passengerSeatPoint = all[j];
					return passengerSeatPoint;
				}
			}
		}
		return null;
	}

		bool warnedSeatOk;

		/// <summary>Koltuğa benzeyen aday nesne adları (tanı satırı, kare başına değil).</summary>
		string SeatCandidates() {
			try {
				Transform[] all = transform.GetComponentsInChildren<Transform>(true);
				if (all == null) return "(none)";
				string list = "";
				int n = 0;
				for (int i = 0; i < all.Length && n < 12; i++) {
					if (all[i] == null) continue;
					string nm = all[i].name;
					if (string.IsNullOrEmpty(nm)) continue;
					string low = nm.ToLowerInvariant();
					if (low.IndexOf("seat") < 0 && low.IndexOf("driver") < 0
						&& low.IndexOf("chair") < 0 && low.IndexOf("pilot") < 0) continue;
					if (list.Length > 0) list += ", ";
					list += nm;
					n++;
				}
				return list.Length == 0 ? "(none)" : list;
			}
			catch (Exception e) { return "scan failed: " + e.Message; }
		}

		string SeatPath(Transform t) {
			try {
				if (t == null) return "(null)";
				string p = t.name;
				Transform cur = t.parent;
				int guard = 0;
				while (cur != null && guard++ < 8) { p = cur.name + "/" + p; cur = cur.parent; }
				return p;
			}
			catch { return "(unknown)"; }
		}

		/// <summary>Koltuk yok uyarisi bir kez yazilir.</summary>
		bool warnedNoSeat;

		/// <summary>
		/// Sürücü olup olmadığını durum adından ikinci kez kontrol eder.
		///
		/// Tetikleyici FSM'in etkin durumu "araçta" anlamına gelen bir
		/// kalıpla eşleşiyorsa sürücüyüz. Bu, FsmBinder'daki kancanın
		/// yakalayamadığı isim farklılıklarını kapatır; kanca önce
		/// çalışıyorsa bu kod hiç devreye girmez.
		/// </summary>
		static readonly string[] OCCUPIED_STATES = {
			"player in car", "in car", "drive", "driving", "driving car",
			"player driving", "car", "wait for player",
		};

		float nextDriverPoll;

		void PollDriverFallback() {
			// 5 Hz yeterli: sürücüye girmek ani bir olaydır.
			if (Time.time < nextDriverPoll) return;
			nextDriverPoll = Time.time + 0.2f;

			if (Binder == null) return;
			string active = null;
			try { active = Binder.DriverTriggerState(); }
			catch { return; }
			if (string.IsNullOrEmpty(active)) return;

			string lower = active.ToLowerInvariant();
			bool occupied = false;
			for (int i = 0; i < OCCUPIED_STATES.Length; i++) {
				if (lower.IndexOf(OCCUPIED_STATES[i], StringComparison.Ordinal) >= 0) {
					occupied = true;
					break;
				}
			}
			// "Wait for player" = boş araç; sürücü DEĞİL.
			if (lower.IndexOf("wait for player", StringComparison.Ordinal) >= 0) occupied = false;

			if (occupied) OnDriverEnter();
			else if (lower.IndexOf("wait", StringComparison.Ordinal) >= 0) OnDriverLeave();
		}

		float nextPassengerPoll;

		/// <summary>
		/// Yolcu koltuğu için sürücü tespitinin aynası.
		///
		/// Neden gerekli: yolcu koltuk FSM'i araçtan araca farklı durum
		/// adları kullanıyor ("Player in passenger seat", "Player sitting"
		/// vb.). FsmInject yalnızca LİSTELENEN adlara takılabiliyor, o da
		/// tutmuyorsa yolcu koltuğu sessizce hiç işlenmiyordu.
		/// </summary>
		void PollPassengerFallback() {
			if (Time.time < nextPassengerPoll) return;
			nextPassengerPoll = Time.time + 0.2f;

			if (Binder == null) return;
			string active = null;
			try { active = Binder.PassengerTriggerState(); }
			catch { return; }
			// Koltuk FSM'i yok: HAYOSIKO gibi tek koltuklu araçlarda
			// sürekli null gelir ve her 0.2 sn'de uyarı yazmak yerine
			// sessizce çıkıyoruz (yolcu koltuğu o araçta yok demektir).
			if (string.IsNullOrEmpty(active)) return;

			string lower = active.ToLowerInvariant();

			// "Wait for player" = koltuk BOŞ, yolcu değil.
			bool occupied = lower.IndexOf("player in", StringComparison.Ordinal) >= 0
				|| lower.IndexOf("sitting", StringComparison.Ordinal) >= 0;
			if (lower.IndexOf("wait", StringComparison.Ordinal) >= 0) occupied = false;

			if (occupied) OnPassengerEnter();
			else if (lower.IndexOf("wait", StringComparison.Ordinal) >= 0) OnPassengerLeave();
		}

		// ------------------------------------------------------------------
		// Driver / ownership
		// ------------------------------------------------------------------

		void OnDriverEnter() {
			// Çift tetik koruması: kanca + PollDriverFallback aynı karede
			// ikisi de girebilir; ikinci giriş paket ve log tekrarı demek.
			if (LocalIsDriver) return;
			LocalIsDriver = true;
			// Artık yerel fizik sürüyor: uzaktan gelen örnekler geçersiz,
			// tamponu at. Yoksa hemen binip inince araç eski konuma
			// yumuşatarak geri dönerdi.
			Interp.Clear();
			if (body != null) body.isKinematic = false;
			// Araca binildi: yerel durum paketinde FLAG_SITTING bir sonraki
			// degisiklikte gider. (Daha once LocalPlayerState.InVehicle'i hicbir
			// sey yazmiyordu, yani bayrak hiç gonderilmiyordu.)
			LocalPlayerState.InVehicle = true;
			// Wheel inputs are captured only for the car this player is driving: the hook
			// fires for every WheelCollider in the game, and tracking them all would send
			// traffic nobody asked for.
			WheelSync.Track(transform);
			// Yolcu koltuktan kalkıp direksiyona geçerse iki bayrak da
			// sıfırlanmalı, yoksa "ayaktayım" sanıp bindeğimizi unuturuz.
			LocalIsPassenger = false;
			VehicleRegistry.SendEnter(VehicleId, true);
			// Binme/inme LOGLANIR. Eskiden sessizdi: sürücü tespiti çalışmasa
			// bile logda iz yoktu ve "sürdüm ama veri gitmedi" ayırt
			// edilemiyordu. Kaynak adi Track'in içinden gelir (controller /
			// wheels), burada tekrar sayılmaz.
			SmpLog.Print("you drive '" + VehicleName + "' (" + WheelSync.Source + " tracked).");
			if (RemoteDriven)
				SmpLog.Warning("'" + VehicleName + "' is also driven by your friend - "
					+ "both of you have physics on it, expect to fight for control.");
		}

		void OnDriverLeave() {
			if (!LocalIsDriver) return;
			LocalIsDriver = false;
			// Sürücü koltuğundan inince yolcu koltuk bayrağını da
			// temizle: "yolcuyum" sanıp yerel oturma bayrağını
			// yanlışlıkla açık bırakıyordu.
			LocalIsPassenger = false;
			LocalPlayerState.InVehicle = false;
			WheelSync.Clear();
			// Peer sürüyorsa tamponu at: içindeki örnekler bizim kalktığımız
			// ana ait, yeniden binince interpolasyon eski konumdan başlardı.
			if (!RemoteDriven) Interp.Clear();
			VehicleRegistry.SendLeave(VehicleId, true);
			SmpLog.Print("you left '" + VehicleName + "'.");
		}

		// ------------------------------------------------------------------
		// Passenger seat (HAYOSIKO and friends)
		// ------------------------------------------------------------------

		/// <summary>
		/// Yerel oyuncu YOLCU koltuğuna bindi.
		///
		/// Kritik ayrım: yolcu fizik devralmaz. Aracı sürdüğümüz için
		/// gövde bizimkinin kontrolünde kalır; sadece kukla konumu ve
		/// oturma bayrağı gider.
		/// </summary>
		void OnPassengerEnter() {
			if (LocalIsDriver || LocalIsPassenger) return;
			LocalIsPassenger = true;
			// Oturduğumuzu karşı tarafa da bildirmek ZORUNLU: aksi
			// hâlde peer kuklayı ayakta gösterir, koltuktan düşer.
			LocalPlayerState.InVehicle = true;
			// Tekerlek takibi YOK: yolcu gaz vermiyor.
			WheelSync.Clear();
			VehicleRegistry.SendEnter(VehicleId, false);
			SmpLog.Print("you ride in '" + VehicleName + "' as a passenger.");
		}

		void OnPassengerLeave() {
			if (!LocalIsPassenger) return;
			LocalIsPassenger = false;
			// Sürücü değilsek FLAG_SITTING'i düşür; sürücü isek yerel
			// sürücü bayrağı hâlâ açık, ona dokunmuyoruz.
			if (!LocalIsDriver) LocalPlayerState.InVehicle = false;
			VehicleRegistry.SendLeave(VehicleId, false);
			SmpLog.Print("you left the passenger seat of '" + VehicleName + "'.");
		}

		// ------------------------------------------------------------------
		// Outgoing
		// ------------------------------------------------------------------

		void FindSteerParts() {
			steerProbed = true;
			try {
				Transform[] all = GetComponentsInChildren<Transform>(true);
				if (all == null) return;
				for (int i = 0; i < all.Length; i++) {
					Transform t = all[i];
					if (t == null || string.IsNullOrEmpty(t.name)) continue;
					if (steerPivot == null
						&& (string.Equals(t.name, "CarSteeringPivot", StringComparison.OrdinalIgnoreCase)
							|| string.Equals(t.name, "TruckSteeringPivot", StringComparison.OrdinalIgnoreCase)
							|| string.Equals(t.name, "MuscleSteeringPivot", StringComparison.OrdinalIgnoreCase)
							|| t.name.IndexOf("SteeringPivot", StringComparison.OrdinalIgnoreCase) >= 0)) {
						steerPivot = t;
					}
					if (steerWheel == null
						&& string.Equals(t.name, "pivot_steering_wheel", StringComparison.OrdinalIgnoreCase)) {
						steerWheel = t;
					}
				}
				if (steerWheel == null && steerPivot != null) {
					for (int i = 0; i < all.Length; i++) {
						Transform t = all[i];
						if (t == null || t.parent != steerPivot) continue;
						if (t.name.IndexOf("steering_wheel", StringComparison.OrdinalIgnoreCase) >= 0
							&& t.name.IndexOf("trigger", StringComparison.OrdinalIgnoreCase) < 0) {
							steerWheel = t;
							break;
						}
					}
				}
			}
			catch { }
			if (steerPivot == null && steerWheel == null)
				SmpLog.Warning("steer: no wheel pivot found in '" + VehicleName + "'.");
		}

		void PollSteerWheel() {
			if (!SteamTransport.HasRemote || !LocalIsDriver) return;
			float now = Time.time;
			if (now < nextSteerSend) return;
			if (!steerWheelProbed) FindSteerParts();
			if (steerPivot == null && steerWheel == null) return;
			Quaternion pq = steerPivot != null ? steerPivot.localRotation : Quaternion.identity;
			Quaternion wq = steerWheel != null ? steerWheel.localRotation : Quaternion.identity;
			if (Quaternion.Angle(lastSentPivot, pq) < 1.5f && Quaternion.Angle(lastSentWheel, wq) < 1.5f) return;
			nextSteerSend = now + STEER_SEND_INTERVAL;
			lastSentPivot = pq;
			lastSentWheel = wq;
			VehicleRegistry.SendSteerWheel(VehicleId, pq, wq);
		}

		/// <summary>Peer'in simit acisini uygular (yalniz yolcu/kukla taraf).</summary>
		Quaternion lastRecvPivot = Quaternion.identity;
		Quaternion lastRecvWheel = Quaternion.identity;
		float lastRecvSteerAt = float.NegativeInfinity;
		bool steerHasData;

		// Gosterge ibre aynasi: surucu taraf kesif+takip, peer taraf yazma.
		// Kural: ibresi OYNAYAN gonderilir (otomatik kalibrasyon). Kural disi
		// muhafaza/lojik FSM'ler (yakit muslugu gostergesi vb.) oynamadigi
		// icin tele girmesin diye ilk gorulen aci referanstir.
		System.Collections.Generic.List<NeedleTrack> needles;
		bool needlesProbed;
		float nextNeedleSend;
		public const float NEEDLE_SEND_INTERVAL = 0.1f;
		public const int NEEDLE_MAX = 8;
		float lastRecvNeedleAt = float.NegativeInfinity;

		class NeedleTrack {
			public int Hash;
			public Transform Tr;
			public Quaternion First;
			public Quaternion LastSent;
			public bool Moved;
		}

		public void ApplySteerWheel(Quaternion pivot, Quaternion wheel) {
			if (LocalIsDriver) return;
			lastRecvPivot = pivot;
			lastRecvWheel = wheel;
			lastRecvSteerAt = Time.time;
			steerHasData = true;
			WriteSteerWheel();
		}

		void WriteSteerWheel() {
			if (!steerWheelProbed) FindSteerParts();
			try {
				if (steerPivot != null) steerPivot.localRotation = lastRecvPivot;
				if (steerWheel != null) steerWheel.localRotation = lastRecvWheel;
			}
			catch { }
		}

		static readonly string[] NEEDLE_FSMS = { "Speed", "Fuel", "Temp", "Rotation", "Tach", "RPM" };

		static readonly string[] PEDAL_NAMES = { "pedal_brake", "pedal_clutch", "pedal_throttle" };

		static bool IsPedalName(string n) {
			if (string.IsNullOrEmpty(n)) return false;
			for (int i = 0; i < PEDAL_NAMES.Length; i++) {
				if (string.Equals(n, PEDAL_NAMES[i], StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		static bool IsNeedleFsm(string fsmName) {
			if (string.IsNullOrEmpty(fsmName)) return false;
			for (int i = 0; i < NEEDLE_FSMS.Length; i++) {
				if (string.Equals(fsmName, NEEDLE_FSMS[i], StringComparison.Ordinal)) return true;
			}
			return false;
		}

		void FindNeedles() {
			needlesProbed = true;
			needles = new System.Collections.Generic.List<NeedleTrack>(8);
			try {
				PlayMakerFSM[] fsms = GetComponentsInChildren<PlayMakerFSM>(true);
				if (fsms == null) return;
				for (int i = 0; i < fsms.Length && needles.Count < 16; i++) {
					PlayMakerFSM f = fsms[i];
					if (f == null) continue;
					string fn = null;
					try { fn = f.FsmName; } catch { continue; }
					if (!IsNeedleFsm(fn)) continue;
					GameObject go = null;
					try { go = f.gameObject; } catch { continue; }
					if (go == null) continue;
					NeedleTrack nt = new NeedleTrack();
					nt.Tr = go.transform;
					nt.Hash = Wire.StableHash(SceneDump.PathOf(nt.Tr));
					nt.First = nt.Tr.localRotation;
					nt.LastSent = nt.First;
					nt.Moved = false;
					needles.Add(nt);
				}
			}
			catch { }
			try {
				Transform[] all = GetComponentsInChildren<Transform>(true);
				if (all != null) {
					for (int i = 0; i < all.Length && needles.Count < 16; i++) {
						Transform t = all[i];
						if (t == null || !IsPedalName(t.name)) continue;
						bool dup = false;
						for (int k = 0; k < needles.Count; k++) {
							if (needles[k] != null && needles[k].Tr == t) { dup = true; break; }
						}
						if (dup) continue;
						NeedleTrack nt = new NeedleTrack();
						nt.Tr = t;
						nt.Hash = Wire.StableHash(SceneDump.PathOf(t));
						nt.First = t.localRotation;
						nt.LastSent = nt.First;
						nt.Moved = false;
						needles.Add(nt);
					}
				}
			} catch { }
			if (needles.Count > 0)
				SmpLog.Log("needles: tracking " + needles.Count + " gauge/pedal(s) in '" + VehicleName + "'.");
		}

		void PollNeedles() {
			if (!SteamTransport.HasRemote || !LocalIsDriver) return;
			float now = Time.time;
			if (now < nextNeedleSend) return;
			if (!needlesProbed) FindNeedles();
			if (needles == null || needles.Count == 0) return;
			int[] hs = new int[NEEDLE_MAX];
			Quaternion[] qs = new Quaternion[NEEDLE_MAX];
			int n = 0;
			for (int i = 0; i < needles.Count && n < NEEDLE_MAX; i++) {
				NeedleTrack nt = needles[i];
				if (nt == null || nt.Tr == null) continue;
				Quaternion cur;
				try { cur = nt.Tr.localRotation; } catch { continue; }
				if (!nt.Moved) {
					if (Quaternion.Angle(nt.First, cur) < 2f) continue;
					nt.Moved = true;
				}
				if (Quaternion.Angle(nt.LastSent, cur) < 1.5f) continue;
				nt.LastSent = cur;
				hs[n] = nt.Hash;
				qs[n] = cur;
				n++;
			}
			if (n == 0) return;
			nextNeedleSend = now + NEEDLE_SEND_INTERVAL;
			VehicleRegistry.SendDashNeedle(VehicleId, hs, qs, n);
		}

		// Alici taraf yazma onbellegi (sifir tahsis: paralel diziler).
		readonly int[] needleKeys = new int[8];
		readonly Quaternion[] needleVals = new Quaternion[8];
		readonly Transform[] needleTrs = new Transform[8];
		int needleCount;

		Transform FindNeedleByHash(int hash) {
			try {
				PlayMakerFSM[] fsms = GetComponentsInChildren<PlayMakerFSM>(true);
				if (fsms == null) return null;
				for (int i = 0; i < fsms.Length; i++) {
					PlayMakerFSM f = fsms[i];
					if (f == null) continue;
					string fn = null;
					try { fn = f.FsmName; } catch { continue; }
					GameObject go = null;
					try { go = f.gameObject; } catch { continue; }
					if (go == null) continue;
					if (!IsNeedleFsm(fn) && !IsPedalName(go.name)) continue;
					if (Wire.StableHash(SceneDump.PathOf(go.transform)) == hash)
						return go.transform;
				}
			}
			catch { }
			return null;
		}

		/// <summary>Peer'in ibre acilarini uygular (yalniz yolcu/kukla taraf).</summary>
		public void ApplyDashNeedle(int[] hashes, Quaternion[] rots) {
			if (LocalIsDriver || hashes == null || rots == null) return;
			for (int i = 0; i < hashes.Length && i < rots.Length; i++) {
				int slot = -1;
				for (int k = 0; k < needleCount; k++) {
					if (needleKeys[k] == hashes[i]) { slot = k; break; }
				}
				if (slot < 0) {
					if (needleCount >= 8) continue;
					slot = needleCount++;
					needleKeys[slot] = hashes[i];
					try {
						Transform rt = FindNeedleByHash(hashes[i]);
						if (rt == null) rt = FindPedalByHash(hashes[i]);
						needleTrs[slot] = rt;
					} catch { needleTrs[slot] = null; }
				}
				needleVals[slot] = rots[i];
			}
			lastRecvNeedleAt = Time.time;
			WriteNeedles();
		}

		// Fren lambasi aynasi: oyuncu araclarinda fren lambasi FSM'i yok;
		// oyun kodu fren girdisinden direkt yakiyor. Akan WheelState fren
		// degeri (TotalBrake) peer'da bilerek yazilmiyordu. Arka stop
		// mesh'lerinin emisyonu fren basiliyken parlak yapilir, birakinca
		// eski haline doner (stop lambasiyla kavga etmez: sadece emisyon).
		System.Collections.Generic.List<Renderer> brakeLamps;
		System.Collections.Generic.List<Color> brakeOrigEmission;
		System.Collections.Generic.List<string> brakeEmissionProp;
		bool brakeProbed;
		bool brakeLit;

		void FindBrakeLamps() {
			brakeProbed = true;
			brakeLamps = new System.Collections.Generic.List<Renderer>(4);
			brakeOrigEmission = new System.Collections.Generic.List<Color>(4);
			brakeEmissionProp = new System.Collections.Generic.List<string>(4);
			try {
				Transform[] all = GetComponentsInChildren<Transform>(true);
				if (all == null) return;
				for (int i = 0; i < all.Length; i++) {
					Transform t = all[i];
					if (t == null) continue;
					Transform p = t;
					int g = 0;
					bool underRear = false;
					while (p != null && g++ < 6) {
						if (p.name.IndexOf("rearlight", StringComparison.OrdinalIgnoreCase) >= 0
							|| p.name.IndexOf("rear light", StringComparison.OrdinalIgnoreCase) >= 0) {
							underRear = true;
							break;
						}
						try { p = p.parent; } catch { break; }
					}
					if (!underRear) continue;
					Renderer r = null;
					try { r = t.GetComponent<Renderer>(); } catch { continue; }
					if (r == null || !r.enabled) continue;
					Material m = null;
					try { m = r.material; } catch { continue; }
					if (m == null) continue;
					string prop = null;
					try {
						if (m.HasProperty("_EmissionColor")) prop = "_EmissionColor";
						else if (m.HasProperty("_Emission")) prop = "_Emission";
					} catch { continue; }
					if (prop == null) continue;
					bool dup = false;
					for (int k = 0; k < brakeLamps.Count; k++) {
						if (brakeLamps[k] == r) { dup = true; break; }
					}
					if (dup) continue;
					Color orig = Color.black;
					try { orig = m.GetColor(prop); } catch { }
					brakeLamps.Add(r);
					brakeOrigEmission.Add(orig);
					brakeEmissionProp.Add(prop);
					if (brakeLamps.Count >= 8) break;
				}
			}
			catch { }
			if (brakeLamps.Count > 0)
				SmpLog.Log("brakelights: tracking " + brakeLamps.Count + " lens(es) in '" + VehicleName + "'.");
		}

		void PollBrakeLamps() {
			if (LocalIsDriver) return;
			if (!brakeProbed) FindBrakeLamps();
			if (brakeLamps == null || brakeLamps.Count == 0) return;
			bool want = false;
			try {
				want = WheelSync.BrakeFresh && WheelSync.TotalBrake > 0.15f
					&& VehicleRegistry.GetSingleRemoteDriven() == this;
			} catch { return; }
			if (want == brakeLit) return;
			brakeLit = want;
			Color hot = new Color(1f, 0.08f, 0.08f);
			for (int i = 0; i < brakeLamps.Count; i++) {
				try {
					Renderer r = brakeLamps[i];
					if (r == null) continue;
					Material m = r.material;
					if (m == null) continue;
					m.SetColor(brakeEmissionProp[i], want ? hot : brakeOrigEmission[i]);
				} catch { }
			}
		}

		Transform FindPedalByHash(int hash) {
			try {
				Transform[] all = GetComponentsInChildren<Transform>(true);
				if (all == null) return null;
				for (int i = 0; i < all.Length; i++) {
					Transform t = all[i];
					if (t == null || !IsPedalName(t.name)) continue;
					if (Wire.StableHash(SceneDump.PathOf(t)) == hash) return t;
				}
			}
			catch { }
			return null;
		}

		void WriteNeedles() {
			for (int k = 0; k < needleCount; k++) {
				Transform t = needleTrs[k];
				if (t == null) continue;
				try { t.localRotation = needleVals[k]; } catch { }
			}
		}

		/// <summary>
		/// Peer'in oyunu simit acisini her kare sifirdan yazar (yerel girdi
		/// yok); 10 Hz paket araya girse de cogu karede oyun kazanir ve simit
		/// oynamiyor gorunur. LateUpdate oyun yazimindan sonra calisir:
		/// taze paket varken her kare yeniden yazilir.
		/// </summary>
		void LateUpdate() {
			if (LocalIsDriver || !RemoteDriven) return;
			if (steerHasData && Time.time - lastRecvSteerAt <= 1.5f) {
				try { ApplyPeerSteering(); } catch { }
				try { WriteSteerWheel(); } catch { }
			}
			if (needleCount > 0 && Time.time - lastRecvNeedleAt <= 1.5f) {
				try { WriteNeedles(); } catch { }
			}
		}

		public void WriteTransform(BinaryWriter w) {
			Transform t = gameObject.transform;
			Vector3 p = t.position;
			Quaternion r = t.rotation;
			Vector3 v = body != null ? body.velocity : Vector3.zero;
			Vector3 av = body != null ? body.angularVelocity : Vector3.zero;
			w.Write(VehicleId);
			w.Write(p.x); w.Write(p.y); w.Write(p.z);
			w.Write(r.x); w.Write(r.y); w.Write(r.z); w.Write(r.w);
			w.Write(v.x); w.Write(v.y); w.Write(v.z);
			w.Write(av.x); w.Write(av.y); w.Write(av.z);
			w.Write(Binder != null ? Binder.Fuel : 0f);
		}

		public void WriteFullState(BinaryWriter w) {
			w.Write(VehicleId);
			w.Write(Binder != null ? Binder.CurrentEngineState() : (int)EngineState.Null);
			w.Write(Binder != null ? Binder.CurrentDashboardState() : (int)DashboardState.Null);
			w.Write(Binder != null ? Binder.Fuel : 0f);
		}

		// ------------------------------------------------------------------
		// Incoming
		// ------------------------------------------------------------------

		/// <summary>
		/// Peer'dan gelen transform paketi.
		///
		/// ÖNEMLİ: burada Transform'a BİR ŞEY YAZILMAZ. Sadece tampona girilir;
		/// hareket <see cref="Update"/> içinde her kare yumuşatılarak uygulanır.
		///
		/// Neden: doğrudan yazmak 20 Hz'de saniyede 20 ışınlanma demekti
		/// (görünür titreme). Yazma sorumluluğu Update'e taşındığı için paket
		/// işleme hızı ile görüntüleme hızı birbirinden bağımsız hale geldi.
		///
		/// PlayMaker notu: gövde kinematik yapılır ki yerel fizik uzaktan
		/// gelen aracı itmesin. Ancak FSM'e dokunulmaz: motor/vites/marş
		/// durumu <see cref="ApplyState"/> ve <see cref="ApplyEngine"/> ile
		/// ayrı yoldan gelir ve ANINDA uygulanır. Yumuşatma yalnızca görsel
		/// katmandır; oyun mantığı yumuşatılmaz.
		/// </summary>
		public void ApplyTransform(BinaryReader r) {
			try {
				Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				Vector3 v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				Vector3 av = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				float fuel = r.ReadSingle();
				if (LocalIsDriver) return; // ownership conflict: ignore peer, we drive

				// İlk paket: araç ışınlanmamalı, yerinden başlamalı.
				// Daha önce burada doğrudan atama yapılıyordu.
				if (!Interp.HasData) SnapTo(p, q);

				Interp.Push(p, q, v, av, Time.time);

				if (body != null && !body.isKinematic) body.isKinematic = true;
				if (Binder != null) Binder.Fuel = fuel;
				// Son alınan paketin yaşı: peer gittiğinde araç donup kalmasın.
				lastRemotePacket = Time.time;
			}
			catch { Telemetry.BadPackets++; }
		}

		/// <summary>
		/// Aracı anında bir konuma oturtur (interpolasyon BİTİŞİ).
		///
		/// İlk paket ve "peer uzaktaysak sürüşe geçtik" anında gerekir.
		/// Yumuşatma bu durumlarda yanlış olur: aracı haritanın bir ucundan
		/// diğerine süzmek yerine olduğu yere koymak gerekir.
		/// </summary>
		public void SnapTo(Vector3 p, Quaternion q) {
			Transform t = gameObject.transform;
			t.position = p;
			t.rotation = q;
		}

		public void ApplyState(int fsmIndex, string stateName) {
			// Sahiplik: biz sürüyorsak peer'in FSM olayları YOK SAYILIR.
			// Yoksa peer'in motor/marş durumu bizim kontağımızı tetikler
			// (canlı kanıt: Hayosiko'da marş döngüsü).
			if (LocalIsDriver) return;
			if (Binder != null) Binder.ApplyState(fsmIndex, stateName);
		}

		public void ApplySwitch(VehicleSwitch sw, bool value, float floatValue) {
			if (LocalIsDriver) return;
			if (Binder != null) Binder.ApplySwitch(sw, value, floatValue);
		}

		public void ApplyEngine(int engine, int dash, float startTime) {
			if (LocalIsDriver) return;
			if (Binder != null) Binder.ApplyEngine(engine, dash, startTime);
		}

		public void ApplyFullState(int engine, int dash, float fuel) {
			if (Binder == null) return;
			// Yakıt SADECE seviye bilgisidir (kontak/marş yok): sürücüde
			// bile uygulanır, yoksa park halindeki depo ıskalanır.
			Binder.Fuel = fuel;
			if (LocalIsDriver) return;
			Binder.ApplyEngine(engine, dash, -1f);
		}

		/// <summary>
		/// Peer araca bindi (veya indi). <paramref name="isDriver"/> yol
		/// koltuk seçimi yapar; ayrıca fizik sahipliğini de belirler.
		///
		/// Neden iki bayrak var (RemoteSeated / RemoteDriven)
		/// --------------------------------------------------
		/// Yolcu bindiğinde aracı kinematik YAPILMAMALI: o sadece
		/// koltuğunda oturuyor, aracı o sürüyor değil. Tek bayrak
		/// kullansaydık yolcu bindiği an yerel fizik kapanır ve
		/// kimse aracı süremezdi. Bu yüzden:
		///   RemoteSeated = kukla bir koltuğa bağlı
		///   RemoteDriven = fizik peer'a devredildi (SADECE sürücü için)
		/// </summary>
		public void SetRemoteSeated(bool seated, bool isDriver = true) {
			RemoteSeated = seated;
			RemoteDriven = seated && isDriver;
			// Peer sürücü değilse (veya ayrıldıysa) tampon temizlenir:
			// içindeki örnekler artık geçerli değil, eski konumdan
			// yumuşatarak başlamak aracı geriye sürükler.
			if (!RemoteDriven) Interp.Clear();
			try { AttachPeer(seated, isDriver); }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("AttachPeer failed: " + e.Message); }
		}

		/// <summary>Peer is gone: unfreeze so local physics resumes.</summary>
		public void ReleaseRemote() {
			RemoteDriven = false;
			RemoteSeated = false;
			Interp.Clear();
			if (!LocalIsDriver && body != null) body.isKinematic = false;
		}
	}
}
