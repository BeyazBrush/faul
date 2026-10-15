using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Bir aracın PlayMaker FSM'lerini tarayıp adlandırır, sonra gerekli
	/// durumlara kanca (FsmInject) takar.
	///
	/// Neden isimle değil de çalışma anında keşifle
	/// -----------------------------------------------
	/// Asset'ler araç araç değişiyor: GIFU/RUSCKO/FERNDALE/COMBINE'da
	/// "DriveTrigger" NESNESİ var ama FSM'i "PlayerTrigger" DEĞİL. Sabit
	/// isim listesi bu araçlarda sürücü tespitini tamamen sustu. Bu yüzden
	/// adaylar toplanır, sonra "Player in car" durumunu taşıyan kazanan seçilir.
	/// </summary>
	public class FsmBinder {

		// --- Callbacks, wired up by SyncedVehicle ----------------------------------

		/// <summary>fsmIndex, stateName</summary>
		public Action<int, string> OnStateChange;

		/// <summary>switch, bool value, float value</summary>
		public Action<VehicleSwitch, bool, float> OnSwitchChange;

		/// <summary>engine state, dashboard state</summary>
		public Action<int, int> OnEngineChange;

		public Action OnDriverEnter;
		public Action OnDriverLeave;

		/// <summary>
		/// YOLCU koltuğuna oturma. Gerçek oyuncular için; bot yolculuğu
		/// AttachBotToPassenger ile ayrı yoldadır.
		/// </summary>
		public Action OnPassengerEnter;
		public Action OnPassengerLeave;

		/// <summary>
		/// Sürücü tetikleyicisi FSM'inin ETKİN durum adı (yoksa null).
		///
		/// dnSpy gerekçesi: AxisCarController, Arcader ve CarController
		/// hiçbirinde sürücü referansı yok; tek güvenilir "kim sürüyor"
		/// sinyali PlayMaker durum adı. Kanca (FsmInject) belirli isimlere
		/// bağlı olduğu için farklı isimli sürümlerde yanılabiliyor;
		/// SyncedVehicle bu yedek okumayla durumu yeniden değerlendiriyor.
		/// </summary>
		public string DriverTriggerState() {
			try {
				if (PlayerTrigger == null) return null;
				// dnSpy: PlayMakerFSM.ActiveStateName token 0x06000405,
				// PlayMakerFSM.get_FsmStates 0x06000406.
				return PlayerTrigger.ActiveStateName;
			}
			catch { return null; }
		}

		/// <summary>
		/// Yolcu koltuğu tetikleyicisi FSM'inin ETKİN durum adı (yoksa null).
		/// Yolcu koltuk FSM'i sürücüden ayrı olduğu için ayrı okuma gerekir.
		/// </summary>
		public string PassengerTriggerState() {
			try {
				if (PassengerTrigger == null) return null;
				return PassengerTrigger.ActiveStateName;
			}
			catch { return null; }
		}

		// --- Discovered FSMs, named as in the original mod -------------------------

		public PlayMakerFSM Starter;
		public PlayMakerFSM Ignition;
		public PlayMakerFSM Dashboard;
		public PlayMakerFSM FuelTank;
		public PlayMakerFSM Range;
		public PlayMakerFSM FuelTap;
		public PlayMakerFSM Lights;
		public PlayMakerFSM Wipers;
		public PlayMakerFSM InteriorLight;
		public PlayMakerFSM Indicators;
		public PlayMakerFSM Handbrake;
		public PlayMakerFSM Seatbelt;
		/// <summary>
		/// Pencere aç/kapa kumandası ("opener" Knob FSM'i).
		/// ASSET KANITI (fsms.json): her aracın sürücü kapısında
		///   HAYOSIKO(...)/DriverDoors/doorl/opener   FSM='Knob'
		///   durumlar: State 1 (kapalı), State 2 (açık),  olay: OPEN WINDOW
		/// Fare ile kumanda nodunu tutup tekerlek yukarı/aşağı çevrilir.
		/// </summary>
		public PlayMakerFSM WindowKnob;
		public PlayMakerFSM GearIndicator;
		public PlayMakerFSM Clutch;
		public PlayMakerFSM PlayerTrigger;
		public PlayMakerFSM PassengerTrigger;
		public PlayMakerFSM WaspHive;

		// Truck specific
		public PlayMakerFSM HydraulicPump;
		public PlayMakerFSM DiffLock;
		/// <summary>KEKMET: Test yerine Flip/Flip 2 kullanir (dumper kaniti).</summary>
		public bool DiffLockUsesFlip;
		public PlayMakerFSM AxleLift;
		public PlayMakerFSM SpillValve;
		public PlayMakerFSM Beacon;

		// Tractor specific
		public PlayMakerFSM FrontHydraulic;

		// --- Vehicle traits --------------------------------------------------------

		public bool HasRange;
		public bool HasPushParkingBrake;
		public bool HasLeverParkingBrake;
		public bool IsTruck;
		public bool IsTractor;

		/// <summary>Seat trigger transform, used to place the passenger seat.</summary>
		public Transform SeatTransform;

		/// <summary>True when the driver's seat GameObject is named "DriveTrigger".</summary>
		public bool HasDriverTrigger;

		/// <summary>
		/// FSMs whose state entry gets replicated, in a key-sorted order so that the index
		/// is identical on every client regardless of component iteration order.
		/// </summary>
		public readonly List<TrackedFsm> Tracked = new List<TrackedFsm>();

		readonly Dictionary<string, TrackedFsm> byKey = new Dictionary<string, TrackedFsm>(StringComparer.Ordinal);
		readonly HashSet<string> installedTransitions = new HashSet<string>(StringComparer.Ordinal);
		readonly List<PlayMakerFSM> ignitionCandidates = new List<PlayMakerFSM>();
		readonly List<PlayMakerFSM> playerTriggers = new List<PlayMakerFSM>();

		bool installed;

		// First-run guards: these FSMs fire once while the vehicle spawns, which would
		// otherwise broadcast a switch nobody touched.
		bool hydraulicPumpFirstRun = true;
		bool axleLiftFirstRun = true;
		bool diffLockFirstRun = true;

		float lastKnobPos = float.NaN;
		bool lastKnobBool;
		bool lastKnobBoolSet;
		float lastKnobSendAt = -1f;   // push el freni için hız sınırı

		// Pencere camı: entegrasyonu ölçme noktası. `opener` Knob FSM'inin
		// State1/2 replikasyonu yetersiz kaldığı için camın gerçek konumu
		// ayrı izlenir (VehicleSwitch.Window float olarak gider).
		Transform windowGlass;
		float lastWindowY = float.NaN;
		float lastWindowSendAt = -1f;
		/// <summary>Son uzakta uygulanan pencerenin zamanı (geri-besleme koruması).</summary>
		float lastWindowAppliedAt = -1f;
		FsmFloat cachedKnob;
		FsmBool cachedKnobBool;
		int knobKind = -1;   // 0=float KnobPos, 1=bool KnobPos, 2=bool Brake, -1=henüz çözülmedi
		bool knobProbeDone;

		// =======================================================================
		// Discovery
		// =======================================================================

		/// <summary>
		/// Walk a vehicle root and pick up every FSM this mod cares about.
		/// </summary>
		public void Discover(GameObject root) {
			// true ŞART: koltuk tetikleyicileri (DriveTrigger/PlayerTrigger)
			// yaklaşana kadar PASİF nesnelerdir. Parametresiz tarama onları
			// atlıyordu; GIFU/RUSCKO/FERNDALE/COMBINE bu yüzden "has no
			// PlayerTrigger FSM" diyordu. dnSpy: 6 overload var, (bool)
			// Unity 5.0'da mevcut.
			PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
			foreach (PlayMakerFSM fsm in fsms) {
				Classify(fsm);
			}
			ResolveIgnitionPair();
			ResolveDriverSeat();
			// Sürücü tetikleyicisi yoksa bu araca binme/inme ASLA tespit
			// edilemez: ne kanca ne PollDriverFallback çalışır, tekerlek
			// takibi başlamaz, FLAG_SITTING gitmez. Bu yüzden SESLİ
			// uyarı: aksi hâlde oyuncu neden bindiğini anlamaz.
			if (PlayerTrigger == null) {
				SmpLog.Warning("vehicle '" + (root != null ? root.name : "?") + "' has no PlayerTrigger FSM; "
					+ "driver enter/leave and wheel tracking will not work on it.");
			}
			BuildTrackedList();
			FindWindowGlass(root);
		}

		/// <summary>
		/// Pencere camının referansını bulur.
		///
		/// ASSET KANITI: HAYOSIKO/FERNDALE/GIFU/RCO_RUSCKO12 sürücü kapısında
		///   .../DriverDoors/doorl/Window/GlassLeft  (mesh)
		/// Pencere yavaş indikçe bu nesnenin yerel konumu değişir. State
		/// replikasyonu yerine bunu süzmek, camın "yavaş" hareketini
		/// karşı tarafta da görüntüler.
		/// </summary>
		void FindWindowGlass(GameObject root) {
			if (root == null) return;
			try {
				Transform[] all = root.GetComponentsInChildren<Transform>(true);
				Transform fallback = null;
				for (int i = 0; i < all.Length; i++) {
					string n = all[i].name;
					if (n != null && n.StartsWith("Glass", StringComparison.OrdinalIgnoreCase)) {
						// Öncelik: üstünde "Window" olan hareketli şoför camı.
						Transform p = all[i].parent;
						while (p != null && p != root.transform) {
							if (p.name != null && p.name.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0) {
								windowGlass = all[i];
								return;
							}
							p = p.parent;
						}
						if (fallback == null) fallback = all[i];
					}
				}
				// "Window" ebeveynli cam yoksa ilk Glass*'a düş.
				windowGlass = fallback;
			}
			catch { }
		}

		/// <summary>Uygulanacak pencere camını döner (yoksa null).</summary>
		public Transform WindowGlass { get { return windowGlass; } }

		/// <summary>
		/// Pencere camının yerel y konumunu süzer. Hız/delta sınırlıdır:
		/// el freni ile aynı desen — her karede değil, anlamlı değişimde.
		/// </summary>
		public void PollWindow() {
			if (windowGlass == null || OnSwitchChange == null) return;
			// YANSIMA KORUMASI: eger biz uzaktan gelen konumu su an camina
			// yazdiysak, bu ayni hareketi geri gondermez. Aksi halde:
			// uzak 'Window' ile camı yaziyor -> yerel PollWindow camı degisti
			// saniyor -> geri uzak tarafa 'Window' gerigini gönderiyor ->
			// uzak bunu tekrar yaziyor. Sozde bir dongu sifir gorunumde.
			if (Time.time - lastWindowAppliedAt < 0.2f) return;
			float y;
			try { y = windowGlass.localPosition.y; }
			catch { return; }
			if (float.IsNaN(y)) return;
			if (Time.time - lastWindowSendAt < 0.1f) return;
			if (!float.IsNaN(lastWindowY) && Mathf.Abs(y - lastWindowY) < 0.01f) return;
			lastWindowY = y;
			lastWindowSendAt = Time.time;
			OnSwitchChange(VehicleSwitch.Window, true, y);
		}

		/// <summary>
		/// Choose which "PlayerTrigger" is the driver's seat.
		///
		/// The seat the player actually occupies is the one on a GameObject named
		/// "DriveTrigger"; anything else in the vehicle may also carry a PlayerTrigger
		/// FSM. Picking the wrong one is what made the Ferndale come out with no passenger
		/// seat even though the original mod had a Ferndale offset ready for it.
		/// </summary>
		void ResolveDriverSeat() {
			if (playerTriggers.Count == 0) return;

			PlayMakerFSM seat = playerTriggers[0];
			foreach (PlayMakerFSM fsm in playerTriggers) {
				if (fsm.gameObject.name == "DriveTrigger") {
					seat = fsm;
					break;
				}
			}

			SeatTransform = seat.gameObject.transform;
			HasDriverTrigger = seat.gameObject.name == "DriveTrigger";

			// Driver enter/leave is signalled on the seat FSM that has those states.
			foreach (PlayMakerFSM fsm in playerTriggers) {
				if (fsm.Fsm.GetState("Player in car") != null) {
					PlayerTrigger = fsm;
					break;
				}
			}
			if (PlayerTrigger == null) {
				PlayerTrigger = seat;
			}

			ResolvePassengerSeat();
		}

		/// <summary>
		/// Yolcu koltuğunun FSM'ini seçer.
		///
		/// CANLI KANIT: logda "test bot: no passenger seat found" ve
		/// "you drive 'SATSUMA...'" var ama HAYOSIKO için HİÇ "you drive"
		/// satırı yok. ResolveDriverSeat sürücü koltuk FSM'ini seçip GERİ
		/// KALAN PlayerTrigger FSM'lerini attı; yolcu koltuğu hiç dinlenmiyordu.
		/// Minibüste ikinci bir "PlayerTrigger" FSM var ve o bizim yolcu
		/// koltuk FSM'imiz.
		///
		/// Seçim kuralı: sürücü olmayan ilk "PlayerTrigger" adayı. Adı
		/// "passenger" içeriyorsa öncelik verilir (sadece sıralama ipucu;
		/// bulunamazsa yine de diğer aday kullanılır).
		/// </summary>
		void ResolvePassengerSeat() {
			PlayMakerFSM fallback = null;
			foreach (PlayMakerFSM fsm in playerTriggers) {
				if (fsm == PlayerTrigger) continue;
				if (fallback == null) fallback = fsm;
				string go = fsm.gameObject.name.ToLowerInvariant();
				string fn = (fsm.FsmName ?? "").ToLowerInvariant();
				if (go.IndexOf("passenger", StringComparison.Ordinal) >= 0
					|| fn.IndexOf("passenger", StringComparison.Ordinal) >= 0) {
					PassengerTrigger = fsm;
					return;
				}
			}
			PassengerTrigger = fallback;
		}

		/// <summary>
		/// Nesnenin bir EBEVEYNinin adı verilen ad mı? (nesnenin KENDİSİ değil)
		///
		/// Gerekçe: oyun etkileşim FSM'lerini "Use" adlı bir çocuğa koyuyor.
		/// Kabin lambası: `InteriorLight` (boş kutu nesnesi) / `Use` (FSM).
		/// Nesne adı eşleşmesi bu yüzden yetmiyordu.
		/// </summary>
		static bool ParentNamed(GameObject go, string name) {
			if (go == null) return false;
			try {
				Transform p = go.transform.parent;
				int guard = 0;
				while (p != null && guard++ < 8) {
					if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) return true;
					p = p.parent;
				}
			}
			catch { }
			return false;
		}

		void Classify(PlayMakerFSM fsm) {
			GameObject go = fsm.gameObject;
			string goName = go != null ? go.name : "";
			string fsmName = fsm.FsmName;

			// --- Driver seat -------------------------------------------------------
			if (fsmName == "PlayerTrigger") {
				// A vehicle has several FSMs named "PlayerTrigger" (driver seat, passenger
				// area, and sometimes extras). Collect them all and pick the right ones
				// afterwards rather than letting the last one win.
				playerTriggers.Add(fsm);
			}
			else if (string.Equals(goName, "DriveTrigger", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(goName, "PlayerTrigger", StringComparison.OrdinalIgnoreCase)) {
				// Koltuk FSM'i başka adla. Asset taraması kanıtı: GIFU, RUSCKO12,
				// FERNDALE ve COMBINE'da "DriveTrigger" VE "PlayerTrigger"
				// nesneleri VAR ama hiçbirinin FSM adı "PlayerTrigger" değil;
				// o yüzden bu 4 araçta sürücü tespiti hiç çalışmıyordu
				// ("has no PlayerTrigger FSM" uyarısı). Doğru koltuk,
				// "Player in car" durumunu taşıyanla seçilir (ResolveDriverSeat),
				// o yüzden fazla aday eklemek güvenli.
				playerTriggers.Add(fsm);
			}

			// --- Starter -----------------------------------------------------------
			else if (fsmName == "Starter") {
				Starter = fsm;
			}

			// --- Ignition / dashboard ---------------------------------------------
			// Both live on an object called "Ignition" and both are named "Use", so they
			// are collected here and told apart by their states further down.
			// SATSUMA ISTISNASI (dumper kaniti: fsms.json, IgnitionSatsuma/UseNew):
			// Satsuma'nin kontak+gosterge FSM'i "Use" DEGIL "UseNew" adinda ve
			// nesne "IgnitionSatsuma". Eski filtre Satsuma'da IgnitionDashboard'u
			// hep null birakiyordu: kontak/lamba durumu hic gonderilmiyor,
			// CurrentDashboardState hep 8 donuyordu.
			// v15: "ButtonIgnition" kosulu kaldirildi -- oyle bir nesne
			// yok (dökümde 0). Satsuma "IgnitionSatsuma"[UseNew] ve
			// diger araclar "Ignition"[Use].
			else if ((fsmName == "Use" && goName == "Ignition")
				|| (fsmName == "UseNew" && (goName == "Ignition" || goName == "IgnitionSatsuma"))) {
				ignitionCandidates.Add(fsm);
			}

			// --- Handbrake ---------------------------------------------------------
			else if (goName == "ParkingBrake" && fsmName == "Use") {
				Handbrake = fsm;
				HasPushParkingBrake = true;
			}
			else if (goName == "Parking Brake" && fsmName == "Use") {
				Handbrake = fsm;
				HasLeverParkingBrake = true;
			}
			// v15: SATSUMA EL FRENI.
			//
			// Dumper kaniti:
			//   SATSUMA(557kg, 248)/MiscParts/HandBrake/
			//     handbrake(xxxxx)/handbrake lever   [Use]
			//       float KnobPos, bool Required,
			//       float SatsumaHandbrakePosition
			//       durumlar: State 1, State 2
			//
			// Yani NESNE ADI "ParkingBrake" DEGIL, "handbrake lever".
			// Yukaridaki iki kosul hicbirini saglamadigi icin Handbrake
			// null kaliyordu; HasPushParkingBrake/HasLeverParkingBrake
			// ikisi de false -> PollContinuous() :875'te hemen donuyordu
			// ve oyunun ANA aracinin el freni HIC senkronize olmuyordu.
			//
			// Tur tipi: KnobPos bir FLOAT (iterek cikan kol) ve baska hicbir
			// aracla ayni yuvada oldugu icin PULL (HasPushParkingBrake)
			// olarak ele alinir; ApplySwitch zaten KnobPos/Bool/Bool-Brake
			// sirasiyla dener.
			else if (fsmName == "Use" && goName == "handbrake lever" && Handbrake == null) {
				Handbrake = fsm;
				HasPushParkingBrake = true;
			}

			else if (goName == "Range" && fsmName == "Use") {
				Range = fsm;
				HasRange = true;
			}
			else if (goName == "FuelTank" && fsmName == "Data") {
				FuelTank = fsm;
			}
			else if (goName == "FuelTap" && fsmName == "Use") {
				FuelTap = fsm;
			}
			else if (fsmName == "Use" && (goName == "Lights" || goName == "ButtonLights")) {
				Lights = fsm;
			}
			// SATSUMA FAR DUGMESI (dumper kaniti: .../steering_column2/BeamMode
			// + Use; olaylar USE/ON/OFF/SHORTS/LONGS, durumlar Shorts/Longs).
			// Baska far dugmesi yoksa (Satsuma'da yok) Lights rolunu alir;
			// Replicate(Off/Shorts/Longs) + mevcut Lights anahtari aynen calisir.
			else if (fsmName == "Use" && goName == "BeamMode" && Lights == null) {
				Lights = fsm;
			}
			else if (fsmName == "Use" && (goName == "Wipers" || goName == "ButtonWipers")) {
				Wipers = fsm;
			}
				else if (fsmName == "Use" && (goName == "SeatbeltLock" || goName == "BuckleUp" || goName == "SeatbeltHandle")) {
					Seatbelt = fsm;
				}
			else if (fsmName == "Knob" && string.Equals(goName, "opener", StringComparison.OrdinalIgnoreCase)) {
				WindowKnob = fsm;
			}
			else if (fsmName == "Use" && (goName == "ButtonInteriorLight" || goName == "InteriorLight")) {
				// Dumper kaniti (dump2): SATSUMA/Interior/InteriorLight + Use.
				// Nesnenin KENDISI "InteriorLight"; ust kosul yalniz dugme
				// adina bakiyordu, alan docstring'i (satir ~325) dogru
				// yapiyi anlatmasina ragmen eslesmiyordu. Ebeveyn-kural
				// (HAYOSIKO .../InteriorLight/Use) aynen korunur.
				InteriorLight = fsm;
			}
			// ASSET KANITI: minibüste kabin lambasının FSM'i `InteriorLight`
			// NESNESİNDE değil, onun `Use` adlı ÇOCUĞUNDA:
			//   HAYOSIKO(1500kg, 250)/LOD/InteriorLight/Use   FSM='Use'
			//   durum/olaylar: Flip 2, ON, OFF   değişken: LightON
			// (fsms.json + hierarchy.txt taraması)
			//
			// Önceki koşul yalnızca `goName == "InteriorLight"` diyordu, yani
			// nesne adı `Use` olduğu için HAYOSIKO'DA EŞLEŞMEYORDU ve lamba
			// sessizce senkronlanmıyordu. Aynı desen Satsuma'da da var:
			//   SATSUMA(557kg, 248)/Interior/.../Use
			// Bu yüzden ebeveyn adına da bakıyoruz: bu bir tahmin değil,
			// tarama kanıtına dayanan bir genişletme.
			else if (fsmName == "Use" && ParentNamed(go, "InteriorLight")) {
				InteriorLight = fsm;
			}
			else if (fsmName == "GearIndicator") {
				GearIndicator = fsm;
			}
			else if (goName == "Clutch" && fsmName == "Use") {
				Clutch = fsm;
			}
			else if (fsmName == "Clutch") {
				Clutch = fsm;
			}
			// v15: "FrontHyd" kosulu kaldirildi -- oyle bir nesne yok.
			// Valmet (KEKMET) hidrolikleri SU nesne adlariyla geliyor:
			//   Dashboard/FrontHydLoader [Use]
			//   Dashboard/NewHydraulics/FrontHydArm [Use]
			//   LOD/Dashboard/RearHyd [Use]
			// Bu yolla atanmamis olurlar; ayri bir turda baglanacak.
			else if (goName == "RearHyd" && fsmName == "Use" && FrontHydraulic == null) {
				FrontHydraulic = fsm;
				IsTractor = true;
			}
			else if (goName == "TurnSignals" && fsmName == "Usage") {
				Indicators = fsm;
			}
			else if (goName == "WaspHive" && fsmName == "Data") {
				WaspHive = fsm;
			}
			else if (goName == "Hydraulics" && fsmName == "Use") {
				HydraulicPump = fsm;
				IsTruck = true;
			}
			// v15: KEKMET (Valmet) DIFF LOCK.
			//
			// Dumper kaniti:
			//   KEKMET(350-400psi)/LOD/Dashboard/DiffLock  [Use]
			//     float LeverPos, bool DiffLock
			//     durumlar: Flip, Flip 2
			//
			// GIFU'da nesne adi "Differential lock" (bosluklu) ve Test/Lock
			// kullanir; KEKMET'te "DiffLock" (birlestirilmis) ve Flip/Flip 2.
			// Onceki kosul yalniz GIFU'yu yakaliyordu.
			//
			// DIKKAT: KEKMET'in olay sözlüğü FARKLI (Test degil Flip/Flip 2),
			// ve bool'u "DiffLock". ApplySwitch KEKMET için bool yazma yolunu
			// kullanir; MirrorTrack listesine bu iki durum ekleniyor.
			else if (fsmName == "Use" && goName == "DiffLock" && DiffLock == null) {
				DiffLock = fsm;
				DiffLockUsesFlip = true;
			}
			else if (goName == "Liftaxle" && fsmName == "Use") {
				AxleLift = fsm;
			}
			else if (goName == "OpenSpill" && fsmName == "Use") {
				SpillValve = fsm;
			}
			else if (goName == "KnobBeacon" && fsmName == "Use") {
				Beacon = fsm;
			}
		}

		/// <summary>
		/// "Ignition" üzerinde İKİ FSM var (kontak + gösterge paneli) ve ikisi de
		/// "Use" adında. Durumlarına bakarak ayırıyoruz: "Wait player/Wait button"
		/// gösterge, "ACC on" ise kontak.
		/// </summary>
		void ResolveIgnitionPair() {
			foreach (PlayMakerFSM fsm in ignitionCandidates) {
				bool hasAcc = fsm.Fsm.GetState("ACC on") != null || fsm.Fsm.GetState("ACC on 2") != null;
				if ((fsm.Fsm.GetState("Wait player") != null || fsm.Fsm.GetState("Wait button") != null)
					&& Dashboard == null) {
					Dashboard = fsm;
				}
				if (hasAcc && Ignition == null) {
					Ignition = fsm;
				}
			}
			// Biri varsa diğeri de aynı FSM'dir: tek koltuklu araçlarda
			// kontak ve gösterge aynı nesnede birleşmiş olur.
			if (Ignition == null && Dashboard != null) Ignition = Dashboard;
			if (Dashboard == null && Ignition != null) Dashboard = Ignition;
		}

		/// <summary>
		/// Hangi FSM hangi durumlarının kopyalandığı tablo. Sıra sonradan
		/// anahtara göre sıralanır ki iki tarafta indeksler aynı olsun.
		/// </summary>
		void BuildTrackedList() {
			Tracked.Clear();
			byKey.Clear();

			Replicate(Ignition, "Wait button", "Wait player", "Motor starting", "Motor OFF", "Test", "Shut off", "ACC on", "ACC on 2");
			Replicate(Lights, "Off", "Shorts", "Longs");
			Replicate(Indicators, "Activate dash", "Activate dash 2", "On", "On 2", "Off", "Off 2", "State 3");
			Replicate(Beacon, "ON", "OFF");
			Replicate(WaspHive, "State 2");

			// Range FSM'i traktörde ve tirkada farklı olay adı kullanıyor.
			if (IsTruck) Replicate(Range, "Switch");
			else if (IsTractor) Replicate(Range, "Flip");

			Replicate(SpillValve, "Switch");
			Replicate(InteriorLight, IsTruck ? "Switch" : "Flip 2");
			// v15: SATSUMA kabin lambasi "Flip 2" DEGIL "State 2"/"State 3"
			// diyor (dumper: SATSUMA/Interior/InteriorLight [Use], bool
			// LightON, durumlar State 2 / State 3). Track() "Flip 2"yi
			// GetState() ile eliyordu, yani hicbir durum izlenmiyordu ve
			// SendEvent("MP_Flip 2") bosuna gidiyordu.
			// Kalan iki durum ekleniyor; Track() varligi yine dogrular.
			Replicate(InteriorLight, "State 2", "State 3");
			// Kolluluk ve silecek durumları. `Track` geçmeyen durum adlarını
			// kendiliğinden atar, yani burada geniş bir küme güvenli:
			// yalnızca FSM'in GERÇEKTEN bildirdiği durumlar izlenir.
			Replicate(Seatbelt, "Locked", "Wait player", "Wait player 2", "Roll back", "Wait lock", "Locking?", "Lock", "FASTEN SEATBELT", "TAKE SEATBELT", "Locked");
			Replicate(Wipers, "On", "Off", "Use", "Wait player 2", "Wait button 2", "State 2", "Function", "Stop", "TEST", "Test 2", "Check tune");
			// Pencere kumandası (WindowKnob) BİLEREK replike edilmez: State 1/2
			// geçişi camın yavaş inişini yansıtamadığı için konum süzülür
			// (VehicleSwitch.Window, bkz. PollWindow). Replikasyon eklenirse
			// iki yol birden camı ezer ve geri-besleme dalgası başlar.
			Replicate(HydraulicPump, "Test");
			Replicate(AxleLift, "Test");
			Replicate(DiffLock, "Test");
			Replicate(FrontHydraulic, "Test");

			// v15: YAKIT VANASI EKSİKTİ.
			//
			// Gönderim tarafı `InstallValueSwitchHooks` (bkz. FuelTap bloğu)
			// `Msg.Switch` + VehicleSwitch.FuelTap üretiyor, ve `ApplySwitch`
			// alıcıda `FuelTap.SendEvent("MP_Test")` oynatıyor. Ama burada
			// FuelTap için hiçbir Replicate/Track YOKTU; yani
			// AddGlobalTransition("MP_Test", "Test") hiç çağrılmamıştı.
			// Sonuç: `SendEvent("MP_Test")` hiçbir yere ulaşmıyordu, yani
			// YAKIT VANASI AÇIK/KAPALI hiç senkronize olmuyordu.
			//
			// "Test" varlığı Track() içinde GetState() ile doğrulanır; bu
			// build'de varsa (InstallValueSwitchHooks zaten GetState("Test")
			// ile koruyor) MP_Test kaydedilir, yoksa yine sessizce atlanır.
			Replicate(FuelTap, "Test");

			if (HasLeverParkingBrake) Replicate(Handbrake, "Flip");

			// Vites ve kavrama: durum listesi araçtan araca değiştiği için
			// elle listelemek yerine FSM'in TÜM durumları alınır.
			ReplicateAll(GearIndicator);
			ReplicateAll(Clutch);

			// Starter. ESKİ LİSTE: "Motor running", "Starting engine",
			// "ACC / Glowplug" -> Track() bunlari GetState() ile eler, cunku
			// FSM'de YOKLAR. O yuzden MP_Motor running / MP_Starting engine /
			// MP_ACC / Glowplug hicbir zaman UYULANMADI.
			// v15: gercek adlar eklendi (Track yine varligi dogruluyor,
			// yani olmayan biri yine sessizce atlanir).
			// Track() varligi GetState() ile dogruladigi icin HIC YANLIŞ
			// isim eklemek guvenli: olmayan sessizce atlanir. Satsuma adiyle
			// ("Running", "Crank up", "ACC off", ...) ve diger yedi aracin
			// adlariyla ("Motor running", "Starting engine", ...) birlikte.
			Track(NewTracked(Starter, false), "Wait for start", "Turn key", "Start engine",
				"Check clutch", "Start or not", "Running", "Motor running",
				"Crank up", "Starting engine", "ACC off", "ACC",
				"Stall engine", "Motor installed", "Electricity OK", "Broken starter",
				"Fuel Mixture", "Reset drivetrain", "State 1", "Wiring 2", "Wait");

			Tracked.Sort(delegate (TrackedFsm a, TrackedFsm b) {
				return string.CompareOrdinal(a.Key, b.Key);
			});
		}

		TrackedFsm NewTracked(PlayMakerFSM fsm, bool replicate) {
			if (fsm == null) return null;
			string key = KeyOf(fsm);
			TrackedFsm existing;
			if (byKey.TryGetValue(key, out existing)) return existing;
			TrackedFsm t = new TrackedFsm();
			t.Fsm = fsm;
			t.Key = key;
			t.Replicate = replicate;
			byKey.Add(key, t);
			Tracked.Add(t);
			return t;
		}

		void Replicate(PlayMakerFSM fsm, params string[] states) {
			Track(NewTracked(fsm, true), states);
		}

		/// <summary>
		/// FSM'in sahip olduğu TÜM durumları kopyalanacak listeye alır.
		/// CANLI KANIT: log "GearIndicator: replicating 12 state(s)" diyor,
		/// yani liste elle yazılmıyor, çalışma anında okunuyor.
		/// </summary>
		void ReplicateAll(PlayMakerFSM fsm) {
			if (fsm == null || fsm.Fsm == null) return;
			FsmState[] states = null;
			// Bozuk/serileştirilmiş FSM'lerde durum dizisi okunamayabiliyor;
			// o zaman bu FSM sessizce atlanır, keşif patlamaz.
			try { states = fsm.Fsm.States; }
			catch { return; }
			if (states == null || states.Length == 0) return;
			TrackedFsm t = NewTracked(fsm, true);
			if (t == null) return;
			int bound = 0;
			foreach (FsmState st in states) {
				if (st == null) continue;
				string name = null;
				try { name = st.Name; }
				catch { continue; }
				// GetState(name) hem varlığı doğrular hem de AddGlobalTransition'ın
				// var olmayan duruma event bağlamasını engeller.
				if (!string.IsNullOrEmpty(name) && fsm.Fsm.GetState(name) != null && t.States.Add(name)) bound++;
			}
			if (bound > 0) {
				if (Telemetry.Verbose) if (Telemetry.Verbose) SmpLog.Log("vehicle '" + KeyOf(fsm) + "': replicating " + bound + " state(s) ("
					+ string.Join(", ", ToArray(t.States)) + ").");
			}
		}

		static string[] ToArray(HashSet<string> set) {
			string[] a = new string[set.Count];
			set.CopyTo(a);
			Array.Sort(a, StringComparer.Ordinal);
			return a;
		}

		/// <summary>
		/// Register states to watch, skipping any the FSM does not actually have.
		///
		/// Validating up front matters for more than tidiness: <c>AddGlobalTransition</c>
		/// would otherwise be asked to point an <c>MP_</c> event at a state that does not
		/// exist, and <c>FsmInject</c> would then fail on every one of them.
		/// </summary>
		void Track(TrackedFsm t, params string[] states) {
			if (t == null) return;
			foreach (string s in states) {
				if (t.Fsm.Fsm.GetState(s) != null) t.States.Add(s);
			}
		}

		public static string KeyOf(PlayMakerFSM fsm) {
			return fsm.gameObject.name + "/" + fsm.FsmName;
		}

		// =======================================================================
		// Hook installation
		// =======================================================================

		/// <summary>
		/// Inject the hooks. Call once, after <see cref="Discover"/> and after the
		/// callbacks have been assigned.
		/// </summary>
		public void Install() {
			if (installed) return;
			installed = true;

			InstallDriverHooks();
			InstallPassengerHooks();

			for (int i = 0; i < Tracked.Count; i++) {
				TrackedFsm t = Tracked[i];
				if (t.Fsm == null || t.States.Count == 0) continue;
				foreach (string state in t.States) {
					HookState(i, t.Fsm, state);
				}
			}

			InstallValueSwitchHooks();
		}

		/// <summary>
		/// Kaydedilmis <c>MP_*</c> global gecisleri: "MP_" + GERCEK durum adi.
		///
		/// NEDEN VAR: AddGlobalTransition yalnizca GERCEKTEN var olan
		/// durumlara baglanir (Track() GetState() ile eler). Yani bir
		/// <c>MP_xxx</c> olayi, o durum FSM'de yoksa HIC kaydedilmez ve
		/// SendEvent("MP_xxx") sessizce kaybolur.
		///
		/// CANLI KANIT: eski harita "Motor running" / "Starting engine" /
		/// "ACC / Glowplug" ariyordu; Starter FSM'inde bu adlar YOK
		/// (gercek adlar "Running" / "Crank up" / "ACC off"). Sonuc motor
		/// calistiginda gonderilen paket bos gidiyordu, kimse nedenini
		/// gormuyordu. Bu kume olayi gondermeden once kontrol eder ve
		/// TEK KEZ uyarir.
		/// </summary>
		static readonly System.Collections.Generic.HashSet<string> mpEvents =
			new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
		static readonly System.Collections.Generic.HashSet<string> mpWarned =
			new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

		/// <summary>Bu FSM'de <paramref name="stateName"/> icin MP_ gecisi kuruldu mu?</summary>
		///
		/// v25 DUZELTME: anahtar UYUMSUZDU, fonksiyon DAIMA false donuyordu.
		///
		/// `HookState` anahtari `.../MP_<state>` olarak YAZIYOR:
		///     784:  string mpEvent = "MP_" + stateName;
		///     785:  string guard = name + "/" + FsmName + "/" + mpEvent;
		///     790:  mpEvents.Add(guard);
		/// ama burada `MP_` on eki ATILARAK araniyordu:
		///     761:  mpEvents.Contains(name + "/" + FsmName + "/" + stateName)
		///
		/// Iki yazi sekli farkli -> kume hicbir zaman eslesmez. Sonuc
		/// HER FSM icin `HasMpEvent == false`:
		///   * `MpAvailable` (765) her seferinde YANLIS uyari yaziyor:
		///       "FSM 'GearIndicator' icin durum 'Get gear' bulunamadi"
		///     Oysa `AddGlobalTransition("MP_Get gear", "Get gear")`
		///     kurulmus ve gecis CALISIYOR -- yalnizca `FindFsmHaving`
		///     yedegi sayesinde hedefi buluyor.
		///   * `ApplyEngine`'in motor kolu (`ResolveEngineEvent` -> ayni
		///     `HasMpEvent`) hic calismiyor.
		///   * `ApplyEngine`'in gosterge kolu `MpAvailable` yuzunden
		///     hicbir zaman uygulanmiyor.
		/// Yani `Msg.Engine` kanali veri tasiyor ama oyunu hic
		/// degistirmiyor; motor/gosterge senkronu yalniz `Msg.State`
		/// zinciriyle birakiyor ve log yanlis sebepler gosteriyor.
		///
		/// CANLI KANIT: client log'da her `UseNew`/`GearIndicator` durumu
		/// icin "durum 'X' bulunamadi" yaziyor; `X` degerleri FSM'in
		/// GECIS YAPILAN durumlari (dokum: `IgnitionSatsuma|UseNew`
		/// `start = 'Wait player'`).
		public static bool HasMpEvent(PlayMakerFSM fsm, string stateName) {
			if (fsm == null || string.IsNullOrEmpty(stateName)) return false;
			string k = fsm.gameObject.name + "/" + fsm.FsmName + "/";
			// v25: `HookState` `MP_` on EKIYLE yaziyor; burada da ayni
			// sekil kullanilir. Iki bicim de kabul edilir: gecisler
			// daha once kurulmus olabilir.
			return mpEvents.Contains(k + "MP_" + stateName)
				|| mpEvents.Contains(k + stateName);
		}

		/// <summary>Yoksa bir kez uyarir, sonra sessizce gecer.</summary>
		static bool MpAvailable(PlayMakerFSM fsm, string stateName, string what) {
			if (fsm == null || string.IsNullOrEmpty(stateName)) return false;
			if (HasMpEvent(fsm, stateName)) return true;
			string k = (fsm.gameObject.name + "/" + fsm.FsmName + "/" + stateName);
			if (mpWarned.Add(k)) {
				SmpLog.Warning("[" + what + "] FSM '" + fsm.FsmName + "' @ '"
					+ fsm.gameObject.name + "' icin durum '" + stateName
					+ "' bulunamadi; bu durum UZAKTA UYGULANAMAZ. "
					+ "Ekran goruntusu degisse de gonderim calismaz. "
					+ "(AddGlobalTransition yalniz var olan duruma baglanir.)");
			}
			return false;
		}

		/// <summary>
		/// Make <c>SendEvent("MP_" + name)</c> able to enter a state, and run a callback
		/// when the state is entered locally.
		/// </summary>
		void HookState(int index, PlayMakerFSM fsm, string stateName) {
			string mpEvent = "MP_" + stateName;
			string guard = fsm.gameObject.name + "/" + fsm.FsmName + "/" + mpEvent;

			if (installedTransitions.Add(guard)) {
				fsm.AddGlobalTransition(mpEvent, stateName);
			}
			mpEvents.Add(guard);

			// index is captured per call, so each closure reports its own FSM slot.
			int captured = index;
			string capturedState = stateName;
			PlayMakerFSM capturedFsm = fsm;

			bool ok = fsm.FsmInject(stateName, delegate {
				if (CameFromRemote(capturedFsm)) return;
				if (capturedFsm == Starter) {
					if (OnEngineChange != null) {
						OnEngineChange(MapEngineState(capturedState), CurrentDashboardState());
					}
				}
				else if (OnStateChange != null) {
					OnStateChange(captured, capturedState);
				}
			});

			if (!ok) {
				ModConsole.Warning("[MSCSteamMP] could not hook '" + fsm.gameObject.name + "/" + fsm.FsmName + "' state '" + stateName + "'");
			}
		}

		void InstallDriverHooks() {
			if (PlayerTrigger == null) return;
			PlayMakerFSM trigger = PlayerTrigger;
			if (trigger.Fsm.GetState("Player in car") != null) {
				trigger.FsmInject("Player in car", delegate {
					if (CameFromRemote(trigger)) return;
					if (OnDriverEnter != null) OnDriverEnter();
				});
			}
			if (trigger.Fsm.GetState("Wait for player") != null) {
				trigger.FsmInject("Wait for player", delegate {
					if (CameFromRemote(trigger)) return;
					if (OnDriverLeave != null) OnDriverLeave();
				});
			}
		}

		/// <summary>
		/// Yolcu koltuk FSM'ine kanca. Durum adları araçtan araca değişebildiği
		/// için birkaç aday denenir; hiçbiri yoksa bu kancalar takılmaz ve
		/// yolcu senkronu o araçta çalışmaz (logda görünür).
		///
		/// HAYOSIKO: ikinci "PlayerTrigger" FSM'i. CANLI KANIT: logda
		/// HAYOSIKO için hiç "you drive" satırı yokken SATSUMA için var;
		/// bunun tek açıklaması yolcu koltuk FSM'inin hiç dinlenmemesi.
		/// </summary>
		void InstallPassengerHooks() {
			if (PassengerTrigger == null || PassengerTrigger == PlayerTrigger) return;
			PlayMakerFSM trigger = PassengerTrigger;

			string[] enterStates = { "Player in car", "Player in passenger", "Player in seat" };
			foreach (string st in enterStates) {
				if (trigger.Fsm.GetState(st) == null) continue;
				trigger.FsmInject(st, delegate {
					if (CameFromRemote(trigger)) return;
					if (OnPassengerEnter != null) OnPassengerEnter();
				});
			}

			string[] leaveStates = { "Wait for player", "Wait for passenger", "Wait for seat" };
			foreach (string st in leaveStates) {
				if (trigger.Fsm.GetState(st) == null) continue;
				trigger.FsmInject(st, delegate {
					if (CameFromRemote(trigger)) return;
					if (OnPassengerLeave != null) OnPassengerLeave();
				});
			}
		}

		/// <summary>
		/// Switches that need to carry a value. The value is derived from the FSM variable
		/// at state-entry time, which is the moment the original mod read it too.
		/// </summary>
		void InstallValueSwitchHooks() {
			// Fuel tap: the FSM is about to flip its fuel valve flag, so send the inverted
			// value. "FuelOn" was the original name and no longer exists in the current
			// game assets, so the name is discovered rather than assumed.
			if (FuelTap != null && FuelTap.Fsm.GetState("Test") != null) {
				PlayMakerFSM fsm = FuelTap;
				fsm.FsmInject("Test", delegate {
					if (CameFromRemote(fsm) || OnSwitchChange == null) return;
					FsmBool valve = FsmVars.Bool(fsm, "FuelOn", "Fuel", "Tap", "On");
					if (valve == null) return;
					OnSwitchChange(VehicleSwitch.FuelTap, !valve.Value, -1f);
				});
			}

			// Truck hydraulics / axle lift / diff lock. Each fires "Test" once on spawn.
			HookInvertingBool(HydraulicPump, "Test", "On", VehicleSwitch.HydraulicPump,
				delegate { return hydraulicPumpFirstRun; }, delegate { hydraulicPumpFirstRun = false; });
			HookInvertingBool(AxleLift, "Test", "Up", VehicleSwitch.AxleLift,
				delegate { return axleLiftFirstRun; }, delegate { axleLiftFirstRun = false; });
			HookInvertingBool(DiffLock, "Test", "Lock", VehicleSwitch.DiffLock,
				delegate { return diffLockFirstRun; }, delegate { diffLockFirstRun = false; });

			// El freni (kollu): "Flip" durumunda Brake bool'u ters çevrilir.
			if (HasLeverParkingBrake && Handbrake != null && Handbrake.Fsm.GetState("Flip") != null) {
				PlayMakerFSM fsm = Handbrake;
				fsm.FsmInject("Flip", delegate {
					if (CameFromRemote(fsm) || OnSwitchChange == null) return;
					FsmBool brake = FsmVars.Bool(fsm, "Brake");
					if (brake == null) return;
					OnSwitchChange(VehicleSwitch.HandbrakeLever, !brake.Value, -1f);
				});
			}

			// Silgi: "Test 2" üç konumlu bir seçici döndürür, kayan değer gider.
			if (Wipers != null && Wipers.Fsm.GetState("Test 2") != null) {
				PlayMakerFSM fsm = Wipers;
				fsm.FsmInject("Test 2", delegate {
					if (CameFromRemote(fsm) || OnSwitchChange == null) return;
					FsmInt sel = FsmVars.Int(fsm, "Selection", "Select", "Pos");
					if (sel == null) return;
					int selection = sel.Value;
					// 2 -> 0 (silgi kapalı), diğerleri bir artır (0->1, 1->2)
					selection = selection == 2 ? 0 : selection + 1;
					OnSwitchChange(VehicleSwitch.Wipers, false, selection);
				});
			}
		}

		void HookInvertingBool(PlayMakerFSM fsm, string state, string variable, VehicleSwitch sw,
			Func<bool> isFirstRun, Action clearFirstRun) {
			if (fsm == null || fsm.Fsm.GetState(state) == null) return;
			PlayMakerFSM captured = fsm;
			captured.FsmInject(state, delegate {
				if (CameFromRemote(captured)) return;
				// Araç spawn olurken bir kez "Test" tetikleniyor; kimse
				// dokunmadı ama paket göndermemek için ilk koşuda yutuyoruz.
				if (isFirstRun()) { clearFirstRun(); return; }
				if (OnSwitchChange == null) return;
				FsmBool flag = FsmVars.Bool(captured, variable);
				if (flag == null) return;
				OnSwitchChange(sw, !flag.Value, -1f);
			});
		}

		/// <summary>
		/// Butonlu el frki konumu sürekli değişir (FSM olayı üretmez), bu
		/// yüzden her karede okunur.
		/// </summary>
		public void PollContinuous() {
			if (!HasPushParkingBrake || Handbrake == null || OnSwitchChange == null) return;

			// EL FRENİ DEĞİŞKEN TİPİ: float KnobPos olabilir, bool KnobPos
			// olabilir, ya da bool Brake. Önceden YALNIZCA float deniyordu;
			// bool ise FsmVars.Float null dönüyordu ve bu fonksiyon her
			// karede sessizce çıkıyordu. Sonuç: hiçbir zaman HİÇBİR araçta
			// HandbrakePull paketi üretilmemişti (log geçmişi: sıfır kayıt).
			//
			// Şimdi üçü de denenir ve bulunan tip BİR KEZ loglanır, böylece
			// sonraki teşhis "hangi araçta hangi tip" bilgisiyle doğrulanır.
			if (!knobProbeDone) {
				knobProbeDone = true;
				cachedKnob = FsmVars.Float(Handbrake, "KnobPos", "Knob");
				if (cachedKnob != null) { knobKind = 0; }
				else {
					cachedKnobBool = FsmVars.Bool(Handbrake, "KnobPos");
					if (cachedKnobBool != null) { knobKind = 1; }
					else {
						cachedKnobBool = FsmVars.Bool(Handbrake, "Brake");
						if (cachedKnobBool != null) { knobKind = 2; }
					}
				}
				SmpLog.Log("'" + (Handbrake != null && Handbrake.gameObject != null ? Handbrake.gameObject.name : "?")
					+ "': handbrake knob resolved as "
					+ (knobKind == 0 ? "FLOAT KnobPos"
						: knobKind == 1 ? "BOOL KnobPos"
						: knobKind == 2 ? "BOOL Brake" : "(none - WILL NOT SYNC)"));
			}

			if (knobKind == 0 && cachedKnob != null) {
				float pos = cachedKnob.Value;
				if (float.IsNaN(pos)) { lastKnobPos = pos; return; }

				// HIZ SINIRI + ANLAMLI DELTA: eski kod yalnızca
				// `Mathf.Approximately(.)` ile süzüyordu. Bu YANLIŞTI:
				//
				//   CANLI KANIT (19:37:56): el frenini çekerken
				//   `[ACT] VEHICLE HandbrakePull f=0.00 ... f=0.10` satırları
				//   4 ms aralıkla, AYNI saniyede yüzlerce kez yazıldı. Knob
				// analog olduğu için değer sürekli küçük adımlarla değişiyor;
				// Approximately (1e-6) hiçbir şeyi elemedi, yani her adım
				// hem ağa hem [ACT]'e gitti. 250/s'ye varan gönderim.
				//
				// Denklem: 100 ms'den seyrek, 0.02'den anlamlı değişim.
				// Hızlı çevirme gözükür (10 Hz), sabit durma sessiz.
				if (Time.time - lastKnobSendAt < 0.1f) return;
				if (!float.IsNaN(lastKnobPos) && Mathf.Abs(pos - lastKnobPos) < 0.02f) return;
				lastKnobPos = pos;
				lastKnobSendAt = Time.time;
				OnSwitchChange(VehicleSwitch.HandbrakePull, false, pos);
				return;
			}
			if (knobKind >= 1 && cachedKnobBool != null) {
				bool v = cachedKnobBool.Value;
				if (lastKnobBoolSet && v == lastKnobBool) return;
				lastKnobBool = v;
				lastKnobBoolSet = true;
				OnSwitchChange(VehicleSwitch.HandbrakePull, v, v ? 1f : 0f);
				return;
			}
		}

		/// <summary>
		/// Bu FSM'e girişim UZAKTAN mı geldi? "MP_" öneki bizim
		/// gönderdiğimiz olayın imzası; yerel oyuncuya geri yansımasın.
		/// </summary>
		public static bool CameFromRemote(PlayMakerFSM fsm) {
			if (fsm == null || fsm.Fsm == null || fsm.Fsm.LastTransition == null) return false;
			string ev = fsm.Fsm.LastTransition.EventName;
			return !string.IsNullOrEmpty(ev) && ev.StartsWith("MP_", StringComparison.Ordinal);
		}

		// =======================================================================
		// Incoming
		// =======================================================================

		public float Fuel {
			get {
				if (FuelTank == null) return 0f;
				FsmFloat level = FsmVars.Float(FuelTank, "FuelLevel", "Fuel", "Level");
				if (level != null) return level.Value;
				return 0f;
			}
			set {
				if (FuelTank == null) return;
				FsmFloat level = FsmVars.Float(FuelTank, "FuelLevel", "Fuel", "Level");
				if (level != null) level.Value = value;
			}
		}

		public void ApplyState(int fsmIndex, string stateName) {
			if (fsmIndex < 0 || fsmIndex >= Tracked.Count) {
				Telemetry.ReceivedRemoteFsmEvents++;
				return;
			}
			PlayMakerFSM fsm = Tracked[fsmIndex].Fsm;
			if (fsm == null) return;
			Telemetry.ReceivedRemoteFsmEvents++;
			// DUMPER UYUMSUZLUGU: iki tarafta ayni FSM/durum listesi yoksa
			// indeks kayar ve yanlis FSM'e uygulanir. Yedek cozum: ayni
			// durumu TASIYAN baska bir tracked FSM varsa onu kullan.
			if (!MpAvailable(fsm, stateName, "State"))
				fsm = FindFsmHaving(stateName) ?? fsm;
			fsm.SendEvent("MP_" + stateName);
		}

		/// <summary>
		/// Verilen durumu TASIYAN ilk tracked FSM. Yalniz <c>Replicate</c>
		/// olanlar taranir (fare durumlari cikarilmis, temiz liste).
		/// </summary>
		PlayMakerFSM FindFsmHaving(string stateName) {
			if (string.IsNullOrEmpty(stateName)) return null;
			for (int i = 0; i < Tracked.Count; i++) {
				TrackedFsm t = Tracked[i];
				if (t == null || !t.Replicate || t.Fsm == null) continue;
				if (t.States.Contains(stateName)) return t.Fsm;
			}
			return null;
		}

		public void ApplySwitch(VehicleSwitch state, bool newValue, float newValueFloat) {
			switch (state) {
				case VehicleSwitch.HandbrakePull:
					if (Handbrake != null) {
						// CANLI KANIT: push el freni send/receive yolu YALNIZCA float'a
						// bağlanmıştı; bool "Brake" kullanan araçlarda (canlı kanıt:
						// HAYOSIKO) karşı tarafta değer YAZILMIYORDU. Önce bool "Brake"/"KnobPos",
						// sonra float denenir — send tarafı (PollContinuous) ile simetrik.
						FsmBool brakeBool = FsmVars.Bool(Handbrake, "Brake");
						if (brakeBool != null) {
							bool v = newValue || newValueFloat > 0.5f;
							if (brakeBool.Value != v) brakeBool.Value = v;
							return;
						}
						FsmBool knobBool = FsmVars.Bool(Handbrake, "KnobPos");
						if (knobBool != null) {
							bool v = newValue || newValueFloat > 0.5f;
							if (knobBool.Value != v) knobBool.Value = v;
							return;
						}
						FsmFloat knob = FsmVars.Float(Handbrake, "KnobPos", "Knob");
						if (knob != null) knob.Value = newValueFloat;
						lastKnobPos = newValueFloat;
						return;
					}
					break;

				case VehicleSwitch.HandbrakeLever:
					if (Handbrake != null) {
						FsmBool brake = FsmVars.Bool(Handbrake, "Brake");
						// Değer zaten doğruysa olay GÖNDERME: el freni FSM'i
						// kendi bool'unu çevirdiği için tekrar tetiklemek
						// iki kez çevirip zıt sonuca yol açıyordu.
						if (brake != null && brake.Value != newValue) {
							Handbrake.SendEvent("MP_Flip");
							return;
						}
					}
					break;

				case VehicleSwitch.Lights:
					if (Lights != null) {
						FsmInt sel = FsmVars.Int(Lights, "Selection", "Select", "Pos");
						// Işıklar tek kollu: hedefe gelene kadar "Test" at.
						// 8 sefer sınırı: sonsuz döngü koruması, FSM hedefe
						// ulaşmazsa sonsuza kadar paket göndermemek için.
						int guard = 0;
						while (sel != null) {
							if (guard >= 8) return;
							if (sel.Value == (int)newValueFloat) break;
							Lights.SendEvent("MP_Test");
							guard++;
						}
					}
					break;

				case VehicleSwitch.Wipers:
					if (Wipers != null) {
						FsmInt sel = FsmVars.Int(Wipers, "Selection", "Select", "Pos");
						int guard = 0;
						while (sel != null) {
							if (guard >= 8) return;
							if (sel.Value == (int)newValueFloat) break;
							Wipers.SendEvent("MP_Test 2");
							guard++;
						}
					}
					break;

				case VehicleSwitch.HydraulicPump:
					if (HydraulicPump != null) {
						FsmBool on = FsmVars.Bool(HydraulicPump, "On", "Pump", "Running");
						if (on != null && on.Value != newValue) {
							HydraulicPump.SendEvent("MP_Test");
							return;
						}
					}
					break;

				case VehicleSwitch.DiffLock:
					if (DiffLock != null) {
						FsmBool lockState = FsmVars.Bool(DiffLock, "Lock", "Diff", "Differential");
						if (lockState != null && lockState.Value != newValue) {
							DiffLock.SendEvent("MP_Test");
						}
					}
					break;

				case VehicleSwitch.AxleLift:
					if (AxleLift != null) {
						FsmBool up = FsmVars.Bool(AxleLift, "Up", "Lift", "Axle");
						if (up != null && up.Value != newValue) {
							AxleLift.SendEvent("MP_Test");
							return;
						}
					}
					break;

				case VehicleSwitch.InteriorLight:
					if (InteriorLight != null) {
						// Tirkada anahtar "On" bool'u, diğer araçlarda
						// "LightON"; olay adları da farklı.
						if (IsTruck) {
							FsmBool on = FsmVars.Bool(InteriorLight, "On", "LightON", "Light");
							if (on != null && on.Value != newValue) {
								InteriorLight.SendEvent("MP_Switch");
								return;
							}
						}
						else {
							FsmBool lit = FsmVars.Bool(InteriorLight, "LightON", "Light", "On");
							if (lit != null && lit.Value != newValue) {
								InteriorLight.SendEvent("MP_Flip 2");
								return;
							}
						}
					}
					break;

				case VehicleSwitch.SpillValve:
					if (SpillValve != null) {
						FsmBool open = FsmVars.Bool(SpillValve, "Open", "Spill", "Valve");
						if (open != null && open.Value != newValue) {
							SpillValve.SendEvent("MP_Switch");
							return;
						}
					}
					break;

				case VehicleSwitch.FuelTap:
					if (FuelTap != null) {
						FsmBool valve = FsmVars.Bool(FuelTap, "FuelOn", "Fuel", "Tap", "On");
						if (valve != null && valve.Value != newValue) {
							FuelTap.SendEvent("MP_Test");
							return;
						}
					}
					break;

				case VehicleSwitch.Window:
					// Pencere camının yerel y konumunu geri yaz. Sender aynı
					// node hierarchical'i (Glass) kullandığı için doğrudan yerel
					// konum uygulanır. x/z değişmez (cam ray üstünde yukarı/aşağı
					// iner), yani yalnızca y yeter.
					if (windowGlass != null) {
						try {
							Vector3 p = windowGlass.localPosition;
							p.y = newValueFloat;
							windowGlass.localPosition = p;
						}
						catch { }
					}
					// YANSIMA KORUMASI: uzaktan yazılan bu konum, yerel PollWindow
					// tarafından "değişti" sayılıp geri gönderilmemeli. Önceden
					// lastWindowAppliedAt HİÇ yazılmıyordu; guard ölü koddı ve
					// camın uzaktaki hareketi kaynak tarafa geri yansıyordu.
					// lastWindowY'yi de güncelliyoruz: yoksa PollWindow, gönderen
					// değeri eski lastWindowY ile karşılaştırıp aradaki delta'yı
					// "yerel değişiklik" sanıp paketi geri yayıyordu.
					lastWindowAppliedAt = Time.time;
					lastWindowY = newValueFloat;
					lastWindowSendAt = Time.time;
					return;
				
				default:
					return;
			}
		}

		public void ApplyEngine(int engine, int dash, float startTime) {
			if (Starter != null) {
				if (startTime != -1f) {
					FsmFloat st = FsmVars.Float(Starter, "StartTime", "Start", "Time");
					if (st != null) st.Value = startTime;
				}
				// ADAY listesinden bu aracın Starter'ında KAYITLI olanı seç.
				// Satsuma "Running" der, diğer 7 araç "Motor running"; sabit
				// bir ad göndermek 7/8 aracı sessizce bozuyordu.
				string state = ResolveEngineEvent(Starter, (EngineState)engine);
				if (state != null) Starter.SendEvent("MP_" + state);
				else if (Telemetry.Verbose) SmpLog.Debug("vehicle", "engine",
					"kademe " + engine + " icin Starter'da uygun durum yok (MP_ gecisi kayitli degil)");
			}
			if (Dashboard != null) {
				string ev = DashEvent((DashboardState)dash);
				if (ev != null && MpAvailable(Dashboard, stateNameOf(ev), "Dashboard")) Dashboard.SendEvent(ev);
			}
		}

		/// <summary>"MP_X" -> "X"</summary>
		static string stateNameOf(string mpEvent) {
			if (string.IsNullOrEmpty(mpEvent)) return null;
			return mpEvent.StartsWith("MP_", StringComparison.Ordinal)
				? mpEvent.Substring(3) : mpEvent;
		}

		/// <summary>
		/// Bu kademe icin ADAY durum adlari, tercih sirasiyla.
		///
		/// NEDEN LISTE (v15 duzeltmesi): Satsuma "Running" diyor, diger
		/// yedi arac "Motor running" diyor. Dumper kaniti:
		///
		///   SATSUMA/CarSimulation/Car/Starter  -> Running, Crank up,
		///       ACC off, Stall engine, Motor installed, Electricity OK,
		///       Broken starter, Fuel Mixture, Reset drivetrain, ...
		///   GIFU|HAYOSIKO|FERNDALE|RUSKO|KEKMET|COMBINE|JONNEZ Starter ->
		///       Motor running, Starting engine, State 1, Wait for start,
		///       Turn key, Start engine, Check clutch, Start or not, Wait
		///
		/// Yani "Running" 1/8, "Motor running" 7/8. Hangisini dondurdugun
		/// onemli degil; HANGISININ o aracın Starter FSM'inde GERCEKTEN
		/// oldugu onemli. ApplyEngine ilk KAYITLI olani secer.
		/// </summary>
		static string[] EngineEventNames(EngineState s) {
			switch (s) {
				case EngineState.WaitForStart: return new string[] { "Wait for start" };
				case EngineState.ACC: return new string[] { "ACC" };
				case EngineState.Glowplug: return new string[] { "ACC / Glowplug", "Glowplug", "ACC" };
				case EngineState.TurnKey: return new string[] { "Turn key" };
				case EngineState.CheckClutch: return new string[] { "Check clutch" };
				case EngineState.StartingEngine: return new string[] { "Starting engine", "Crank up" };
				case EngineState.StartEngine: return new string[] { "Start engine" };
				case EngineState.StartOrNot: return new string[] { "Start or not" };
				// Satsuma "Running", digerleri "Motor running"
				case EngineState.MotorRunning: return new string[] { "Running", "Motor running" };
				case EngineState.Wait: return new string[] { "Wait" };
				case EngineState.Cranking: return new string[] { "Crank up", "Starting engine" };
				case EngineState.AccOff: return new string[] { "ACC off" };
				case EngineState.Stalled: return new string[] { "Stall engine" };
				case EngineState.MotorNotInstalled: return new string[] { "Motor installed" };
				case EngineState.ElectricityOk: return new string[] { "Electricity OK" };
				case EngineState.Broken: return new string[] { "Broken starter" };
				default: return null;
			}
		}

		/// <summary>
		/// <paramref name="fsm"/> üzerinde KAYITLI ilk MP_ gecisine karşılık
		/// gelen durum adı; hiçbiri yoksa null.
		///
		/// Kayıt `HookState` → `AddGlobalTransition` ile yapılır ve
		/// `mpEvents` kümesine yazılır; `Track()` varlığı `GetState()` ile
		/// doğruladığı için kümede olan ad o FSM'de GERÇEKTEN vardır.
		/// </summary>
		static string ResolveEngineEvent(PlayMakerFSM fsm, EngineState s) {
			string[] cands = EngineEventNames(s);
			if (fsm == null || cands == null) return null;
			for (int i = 0; i < cands.Length; i++) {
				if (HasMpEvent(fsm, cands[i])) return cands[i];
			}
			return null;
		}

static string DashEvent(DashboardState s) {
			switch (s) {
				case DashboardState.ACCon: return "MP_ACC on";
				case DashboardState.Test: return "MP_Test";
				case DashboardState.ACCon2: return "MP_ACC on 2";
				case DashboardState.MotorStarting: return "MP_Motor starting";
				case DashboardState.ShutOff: return "MP_Shut off";
				case DashboardState.MotorOff: return "MP_Motor OFF";
				case DashboardState.WaitButton: return "MP_Wait button";
				case DashboardState.WaitPlayer: return "MP_Wait player";
				default: return null;
			}
		}

		public static int MapEngineState(string stateName) {
			// v15: ONCE gercek Starter durum adlari. Bunlar eski haritada
			// hic yoktu; hepsi Null(10)'a duserdi ve EngineEvent(Null) null
			// dondugu icin motor calisirken/mars basilirken HICBIR paket
			// olusmuyordu. Adlar docs/fsm_detail/l2__Starter__Starter.txt'ten.
			// Satsuma "Running", diger yedi arac "Motor running" diyor.
			// IKISI DE MotorRunning. (Eski satirda yalniz "Running" vardi;
			// asagida "Motor running" zaten vardi, ikisi de duruyor.)
			if (stateName == "Running" || stateName == "Motor running")
				return (int)EngineState.MotorRunning;
			if (stateName == "Crank up") return (int)EngineState.Cranking;
			if (stateName == "ACC off") return (int)EngineState.AccOff;
			if (stateName == "Stall engine") return (int)EngineState.Stalled;
			if (stateName == "Motor installed") return (int)EngineState.MotorNotInstalled;
			if (stateName == "Electricity OK") return (int)EngineState.ElectricityOk;
			if (stateName == "Broken starter") return (int)EngineState.Broken;

			if (stateName == "Wait for start") return 0;
			if (stateName == "ACC") return 1;
			if (stateName == "Turn key") return 3;
			if (stateName == "Starting engine") return 5;
			if (stateName == "Start engine") return 6;
			if (stateName == "Motor running") return 8;
			if (stateName == "Wait") return 9;
			if (stateName == "Check clutch") return 4;
			if (stateName == "Start or not") return 7;
			if (stateName == "ACC / Glowplug") return 2;
			return 10;
		}

		public int CurrentDashboardState() {
			if (Dashboard == null) return 8;
			string s = Dashboard.Fsm.ActiveStateName;
			if (s == "ACC on") return 0;
			if (s == "Test") return 1;
			if (s == "ACC on 2") return 2;
			if (s == "Motor starting") return 3;
			if (s == "Shut off") return 4;
			if (s == "Motor OFF") return 5;
			if (s == "Wait button") return 6;
			if (s == "Wait player") return 7;
			return 8;
		}

		public int CurrentEngineState() {
			if (Starter == null) return 10;
			return MapEngineState(Starter.Fsm.ActiveStateName);
		}
	}

	/// <summary>
	/// FSM değişkeni okuma yardımcıları.
	///
	/// Neden ad listesi değil de üç katmanlı arama
	/// --------------------------------------------
	/// Asset'lerde değişken adları sürümden sürüme değişiyor ("FuelOn" yok
	/// oldu, "Fuel" oldu). Sırasıyla: tam ad → alternatif adlar → ada GÖRE
	/// BULUNAN ilk eşleşme. Üçü de tutmazsa null döner ve bir kez uyarı
	/// yazar; çağıran taraf zaten "bulunamadı" yolunu işleyebiliyor.
	/// </summary>
	public static class FsmVars {

		const string SWITCH_LOST = "that switch will not sync";

		static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);
		static readonly List<string> pending = new List<string>(256);

		public static int PendingCount { get { return pending.Count; } }
		public static List<string> PendingWarnings { get { return pending; } }

		static void WarnOnce(string fsmKey, string variable, string consequence) {
			if (string.IsNullOrEmpty(consequence)) consequence = SWITCH_LOST;
			string guard = fsmKey + "/" + variable;
			if (warned.Add(guard)) {
				pending.Add("FSM '" + fsmKey + "' has no variable matching '" + variable + "'; "
					+ consequence + " ('smp ls " + fsmKey + "' shows where it lives).");
			}
		}

		/// <summary>
		/// Tek satırlık özet: yüzlerce uyarıyı loga boğmadan "kaç tane
		/// bulunamadı" der. Tam liste 'smp warnlist' komutuyla.
		/// </summary>
		public static void FlushSummary() {
			if (pending.Count == 0) return;
			SmpLog.Log(pending.Count + " FSM variable(s) not found in this build; "
				+ "install/remove still syncs by replaying ASSEMBLE/REMOVE, bolt tightness follows the bolts. "
				+ "This is informational, not a failure. Full list: 'smp warnlist'.");
		}

		public static void ClearWarnings() {
			warned.Clear();
			pending.Clear();
		}

		public static FsmBool Bool(PlayMakerFSM fsm, string primary, params string[] alternatives) {
			return Bool(null, fsm, primary, alternatives);
		}

		public static FsmBool Bool(string consequence, PlayMakerFSM fsm, string primary, params string[] alternatives) {
			if (fsm == null) return null;
			FsmBool exact = FindBoolExact(fsm, primary);
			if (exact != null) return exact;
			if (alternatives != null) {
				foreach (string alt in alternatives) {
					exact = FindBoolExact(fsm, alt);
					if (exact != null) return exact;
				}
			}
			// SMP_NO_SUBSTRING_MATCH_V22
			// SON CARE ALT DIZE ARAMA KALDIRILDI.
			//
			// `IndexOf(primary, OrdinalIgnoreCase) >= 0` bir TAHMINDIR:
			// isim benzestigine dayanarak DEGISKEN degistirir.
			//
			// DOKUM KANITI (kaldirmaya gerek yok):
			//   * gercek bayraklar TAM AD olarak bildirilmis ve her zaman
			//     TAG'den ONCE geliyor -- `Database/*|Data` kayitlarinda
			//     181 `Bolted`, 97 `Damaged`, 203 `Installed` -> hepsi tam
			//     ad, hepsi once.
			//   * `UniqueTag*` birer `FsmString`; `BoolVariables` dizisinde
			//     YER ALMAZLAR, yani alt dize onlari zaten yakalayamaz.
			//
			// GERCEK TEHLIKE: `CylinderheadBolted` gibi baska bir bool
			// adi. 19 `Database/*|Data` kaydi bu adi iceriyor. Bugun
			// aksiyon verisi (kuyruk disi), ama ileride gercek bir
			// `FsmBool` olarak bildirilip gercek bayraktan ONCE gelirse
			// alt dize arama SILINDIR'in bayragini baglardi -- ve hicbir
			// sey yanlis gostermezdi.
			//
			// Artik yalniz: tam ad, sonra acikca yazilmis `alternatives`.
			if (alternatives != null) {
				FsmBool[] all = fsm.FsmVariables.BoolVariables;
				if (all != null) {
					foreach (FsmBool v in all) {
						if (v != null && ContainsAny(v.Name, alternatives)) return v;
					}
				}
			}
			WarnOnce(FsmBinder.KeyOf(fsm), primary, consequence);
			return null;
		}

		public static FsmFloat Float(PlayMakerFSM fsm, string primary, params string[] alternatives) {
			return Float(null, fsm, primary, alternatives);
		}

		public static FsmFloat Float(string consequence, PlayMakerFSM fsm, string primary, params string[] alternatives) {
			if (fsm == null) return null;
			FsmFloat[] all = fsm.FsmVariables.FloatVariables;
			if (all != null) {
				// Tam ad önce: "Selection" ile "WiperSelection" AYNI değişken
				// değil; kısmi eşleşme yanlış değişkeni seçerdi.
				foreach (FsmFloat v in all) {
					if (v != null && !string.IsNullOrEmpty(v.Name) && string.Equals(v.Name, primary, StringComparison.Ordinal)) return v;
				}
				if (alternatives != null) {
					foreach (string alt in alternatives) {
						foreach (FsmFloat v in all) {
							if (v != null && string.Equals(v.Name, alt, StringComparison.Ordinal)) return v;
						}
					}
				}
				// SMP_NO_SUBSTRING_MATCH_V22: alt dize arama kaldirildi.
				// Dokum: aranan `Tightness` float'inin 60 gercek Satsuma
				// `Screw` nesnesinin HICBIRINDE ayni adiyla bildirilmedigi
				// kanitlandi (60'i `Stage` int'i bildiriyor). Alt dize
				// arama `TightnessMax` gibi bir LIMIT degeri ya da
				// alakasiz bir FSM float'ini baglayabilirdi.
				if (alternatives != null) {
					foreach (FsmFloat v in all) {
						if (v != null && ContainsAny(v.Name, alternatives)) return v;
					}
				}
			}
			WarnOnce(FsmBinder.KeyOf(fsm), primary, consequence);
			return null;
		}

		public static FsmInt Int(PlayMakerFSM fsm, string primary, params string[] alternatives) {
			return Int(null, fsm, primary, alternatives);
		}

		public static FsmInt Int(string consequence, PlayMakerFSM fsm, string primary, params string[] alternatives) {
			if (fsm == null) return null;
			FsmInt[] all = fsm.FsmVariables.IntVariables;
			if (all != null) {
				foreach (FsmInt v in all) {
					if (v != null && string.Equals(v.Name, primary, StringComparison.Ordinal)) return v;
				}
				if (alternatives != null) {
					foreach (string alt in alternatives) {
						foreach (FsmInt v in all) {
							if (v != null && string.Equals(v.Name, alt, StringComparison.Ordinal)) return v;
						}
					}
				}
				// SMP_NO_SUBSTRING_MATCH_V22: alt dize arama kaldirildi.
				// Ayni gerekce: `Stage` iceren baska bir int (orn. bir
				// baska FSM'in `Stage2`/`SubStage` degiskeni) yanlis
				// yere baglanirdi.
				if (alternatives != null) {
					foreach (FsmInt v in all) {
						if (v != null && ContainsAny(v.Name, alternatives)) return v;
					}
				}
			}
			WarnOnce(FsmBinder.KeyOf(fsm), primary, consequence);
			return null;
		}

		static FsmBool FindBoolExact(PlayMakerFSM fsm, string name) {
			FsmBool[] all = fsm.FsmVariables.BoolVariables;
			if (all == null) return null;
			foreach (FsmBool v in all) {
				if (v != null && string.Equals(v.Name, name, StringComparison.Ordinal)) return v;
			}
			return null;
		}

		// SMP_NO_SUBSTRING_MATCH_V22
		/// <summary>
		/// `alternatives` listesinde TAM AD eslesmesi.
		///
		/// v22 ONCESI: `IndexOf(alternatives[i], OrdinalIgnoreCase) >= 0`
		/// yani ALT DIZE. O da bir tahmindi: acikca yazilmis bir isim
		/// ("Bolted") baska bir degiskenin ("CylinderheadBolted") icinde
		/// geciyorsa yanlis degisken baglanirdi. Artik yalniz TAM AD.
		/// </summary>
		static bool ContainsAny(string variableName, string[] alternatives) {
			if (alternatives == null || string.IsNullOrEmpty(variableName)) return false;
			for (int i = 0; i < alternatives.Length; i++) {
				if (string.IsNullOrEmpty(alternatives[i])) continue;
				if (string.Equals(variableName, alternatives[i], StringComparison.Ordinal))
					return true;
			}
			return false;
		}
	}

	/// <summary>
	/// Kopyalanan tek bir FSM + onun durum adları.
	/// <see cref="KeyOf"/> iki tarafta aynı indeksi verebilsin diye
	/// BuildTrackedList sonunda ada göre sıralanır.
	/// </summary>
	public class TrackedFsm {
		public PlayMakerFSM Fsm;
		public string Key;
		public bool Replicate;
		public readonly HashSet<string> States = new HashSet<string>(StringComparer.Ordinal);
	}
}
