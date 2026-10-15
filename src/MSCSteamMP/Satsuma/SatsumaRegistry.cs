using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Satsuma part table, outbound flush, initial sync and remote application.
	///
	/// Discovery is one pass over the SATSUMA subtree after the save is loaded: every
	/// PlayMakerFSM is classified by FSM name into a part entry (Assembly / Removal /
	/// Assemble / BoltCheck) or a bolt (Screw). Nothing is resolved lazily on the hot path,
	/// so each Harmony hook costs a single dictionary lookup.
	///
	/// Identity: an entry id is the object's path relative to the SATSUMA root, captured
	/// once at scan time. Installing a part re-parents it in the scene (PlayMaker
	/// SetParent in the Assembly FSM's End state), so re-deriving the path later would
	/// break the mapping. The id is fixed and the object reference stays alive, so the
	/// table survives any amount of installation and removal.
	/// </summary>
	public static class SatsumaRegistry {

		/// <summary>Scene root of the Satsuma. Verified name in the current build.</summary>
		public const string ROOT_NAME = "SATSUMA(557kg, 248)";

		/// <summary>
		/// Clock indirection. <c>Time.realtimeSinceStartup</c> is a Unity ECall, so the
		/// rate limiter cannot be exercised outside the game process. The default is the
		/// real clock; the verification harness swaps in a manual one.
		/// </summary>
		public static Func<float> Clock = DefaultClock;

		static float DefaultClock() { return Time.realtimeSinceStartup; }

		/// <summary>Parts replicated per frame during the initial sync.</summary>
		public const int INITIAL_PARTS_PER_FRAME = 8;

		/// <summary>Bolts replicated per frame during the initial sync.</summary>
		public const int INITIAL_BOLTS_PER_FRAME = 12;

		/// <summary>Live changes leave the mod this often.</summary>
		public const float FLUSH_INTERVAL = 0.05f;
		/// <summary>
		/// Non-Satsuma (store / boxed) parts replicated per frame during the
		/// initial sync. Same budget as the Satsuma pages on purpose: a record
		/// is a 7-bit-length-prefixed id plus 4 bytes, so eight rows stay well
		/// under a kilobyte and MAX_PACKET_SIZE (32 KB) never comes into play.
		/// </summary>
		public const int STORE_PARTS_PER_FRAME = 8;

		/// <summary>v20: `Database/*` part-flag sayfa boyutu. Ayni olcu
		/// StorePartBatch icin kullanilir: sayfa basina 8 kayit, tipik
		/// `Database/wheel` id'si (~20 karakter) icin kayit 1+20+3 = 24
		/// bayt, sayfa ~198 bayt -- MAX_PACKET_SIZE'in %0.6'si.</summary>
		public const int DATABASE_PARTS_PER_FRAME = 8;

		public static GameObject Root;

		static readonly List<SatsumaPart> parts = new List<SatsumaPart>();

		static readonly Dictionary<string, SatsumaPart> byId = new Dictionary<string, SatsumaPart>(StringComparer.Ordinal);
		static readonly Dictionary<string, List<SatsumaPart>> byName = new Dictionary<string, List<SatsumaPart>>(StringComparer.Ordinal);

		// Value-hook bindings. Keyed on the variable object itself so the hooks do not have
		// to find the owning FSM again.
		static readonly Dictionary<FsmBool, SatsumaPart> installedBinding = new Dictionary<FsmBool, SatsumaPart>(RefComparer<FsmBool>.Instance);
		static readonly Dictionary<FsmFloat, SatsumaBolt> boltTightnessBinding = new Dictionary<FsmFloat, SatsumaBolt>(RefComparer<FsmFloat>.Instance);
		static readonly Dictionary<FsmInt, SatsumaBolt> boltStageBinding = new Dictionary<FsmInt, SatsumaBolt>(RefComparer<FsmInt>.Instance);
		static readonly Dictionary<FsmFloat, SatsumaPart> groupTightnessBinding = new Dictionary<FsmFloat, SatsumaPart>(RefComparer<FsmFloat>.Instance);

		static readonly List<SatsumaBolt> flushBuffer = new List<SatsumaBolt>(32);
		static readonly HashSet<string> warnedIds = new HashSet<string>(StringComparer.Ordinal);

		static float nextFlush;
		static bool syncRunning;
		// F06_POLL_HZ: izlenen degisken taramasinin periyodu (sn).
		// `SatsumaRateLimiter.FLUSH_INTERVAL` (0.10f) ile ayni:
		// veri gonderimi 10 Hz, tarama da 10 Hz.
		const float WATCHED_POLL_INTERVAL = 0.10f;
		static float nextWatchedPoll;
		// F02_SYNC_COROUTINE: calisan SendAll coroutine'i TUTULUR.
		//
		// `syncRunning` bayragi coroutine'i DURDURMAZ; yalnizca bir
		// sonraki `BeginInitialSync` cagrisinin `if (syncRunning) return;`
		// kontrolunu etkiler. `OnSyncRequested` bu bayragi sifirladigi icin
		// (`:2990`) IKINCI bir `StartCoroutine(SendAll())` basliyordu ve
		// iki coroutine ayni tabloyu iki kez sayfaliyordu -- `offset` /
		// `dbOffset` METOT YERELI oldugu icin iki coroutine iki kopyasini
		// tutuyor, ikisi de 0'dan basliyor.
		//
		// CANLI KANIT: 3 SyncRequest -> 4 "initial sync" baslangici,
		// `DatabaseBatch 116 gonder` (beklenen 29 = 225/8), `SyncDone 4`.
		static UnityEngine.Coroutine runningSync;
		static bool syncPending;

		/// <summary>
		/// Dünyamız taranıp tablo kuruldu mu? (client için)
		///
		/// CANLI ÖLÇÜM: client dünya hazır OLMADAN SyncRequest gönderiyordu.
		/// Host bunu alıp tam bir tur gönderiyor (301 vida + 160 parça) ve
		/// client'ın tablosu boş olduğu için HİÇBİRİ uygulanmıyordu.
		///
		///   host logu: 3 kez "initial sync: 160 installable part(s) + 301 bolt(s)"
		///   client huni: 903 cıvata kaydı = 301 × 3, 602'si "tablo-hazir-degil"
		///
		/// 3 turun 2'si boşa gitti. Kendiliğinden iyileşiyordu (3. tur
		/// tuttu), ama 2 tam tur = gereksiz bant genişliği.
		/// </summary>
		static bool worldReady;

		/// <summary>
		/// Bu tablo için SyncRequest ZATEN gönderildi mi?
		///
		/// OnPeerConnected ve OnWorldReady ikisi de istiyor; ikisi de
		/// aynı tablo için çalıştığında tekrar göndermemek için.
		/// </summary>
		static bool syncRequestedForThisTable;
		/// <summary>
		/// Tablo kurulmadan gelen kayitlar dustu (katilma yarisi). Tablo
		/// kurulunca BIR KEZ yeniden istenir; bayrak o zaman temizlenir.
		/// KANIT: client funnel `tablo-hazir-degil 160` (parca) + `301`
		/// (civata) -- dusenler desired'a girmedigi icin verify de gormedi,
		/// kalici eksik kaliyordu.
		/// </summary>
		static bool syncRetryNeeded;

		/// <summary>
		/// Erken-dusen retry surucusu (SatsumaCoop.Tick her kare cagirir).
		/// Tablo kurulmadan dustuyse ve simdi kurulduysa, tam durumu bir kez
		/// yeniden ister. Yalniz client ister; host push tarafidir.
		/// </summary>
		public static void TickSyncRetry() {
			if (!syncRetryNeeded) return;
			if (TableNotReady) return;
			if (!SteamTransport.HasRemote || !SteamTransport.IsClient) {
				syncRetryNeeded = false;
				return;
			}
			syncRetryNeeded = false;
			syncRequestedForThisTable = false;
			SmpLog.Log("registry: tablo kuruldu, erken dusen kayitlar icin tam durum yeniden isteniyor.");
			try { NetworkPacketManager.SendSyncRequest(); } catch { }
		}
		static uint tableHash;
		static bool syncCompleted;
		static bool syncRequested;
		static float syncDoneAt;

		/// <summary>
		/// Tablo uyuşmazlığı yönlendirmesi TEK sefer yazılsın. Aynı oturumda
		/// SyncDone birden çok kez gelebiliyor (JIP + sonraki istekler) ve
		/// kullanıcı aynı 12 satırlık metni üç kez görmemeli.
		/// Sahne değişiminde sıfırlanır.
		/// </summary>
		static bool mismatchReported;

		/// <summary>Kendi taramamızdaki takılı parça sayısı (yönlendirme metni için).</summary>
		static int tableInstalledCount;

		/// <summary>Karşı tarafın istediği takılı durumu (SyncDone sonrası doğrulama için).</summary>
		static readonly Dictionary<string, bool> desiredInstalled = new Dictionary<string, bool>(StringComparer.Ordinal);
		/// <summary>Kaçıncı doğrulama turu yapıldı (0 = henüz yok).</summary>
		static int verifyRound;
		/// <summary>Doğrulamanın zamanlanacağı an (s).</summary>
		static float verifyNextAt;
		/// <summary>SyncDone'dan sonraki ilk doğrulama gecikmesi (s).</summary>
		public const float VERIFY_DELAY = 0.75f;

		/// <summary>
		/// One word describing where the initial sync stands, for the on-screen HUD.
		///
		/// "connected" on its own is misleading: the link can be up while neither side has
		/// exchanged a single part, which is exactly the state a player cannot tell apart
		/// from a working session. This distinguishes the three real cases.
		/// </summary>
		public static string SyncLabel {
			get {
				if (Root == null) return "no-save";
				if (!SteamTransport.HasRemote) return "no-peer";
				if (syncCompleted) return "ok";
				if (syncRunning) return "sending";
				if (syncRequested) return "requested";
				if (SteamTransport.IsClient) return "awaiting-host";
				return "pending";
			}
		}

		// ------------------------------------------------------------------
		// Echo guard
		// ------------------------------------------------------------------

		/// <summary>
		/// True while a remote change is being written into any local FSM. The counter lives
		/// in EchoGuard because the vehicle, item and clock applies share it.
		/// </summary>
		public static bool Suppressed { get { return SteamMP.EchoGuard.Suppressed; } }

		/// <summary>
		/// Re-entrant guard for remote applies. A plain flag would be wrong: firing
		/// ASSEMBLE makes the local FSM write Installed, Bolted and Tightness in turn, and
		/// every one of those writes must also be ignored.
		///
		/// The counter itself lives in EchoGuard because the Satsuma path is only one of
		/// several: the vehicle, item and clock applies all write into the same FSMs, and
		/// one module's apply must not be mistaken for another module's capture.
		/// </summary>
		public struct SuppressScope : IDisposable {
			readonly SteamMP.EchoGuard.Scope inner;
			public SuppressScope() { inner = SteamMP.EchoGuard.Enter(); }
			public void Dispose() { inner.Dispose(); }
		}

		// ------------------------------------------------------------------
		// Discovery
		// ------------------------------------------------------------------

		/// <summary>Every table entry, including pure bolt containers.</summary>
		// ------------------------------------------------------------------
		// CANLI SENKRON KANITI
		// ------------------------------------------------------------------
		// CANLI HATA: log'da yalniz YEREL eylemler gorunuyordu
		// ("[ACT] BOLT loosened ..." = client'in kendi islemi). Uzaktan
		// gelen eylem HICBIR YERE YAZILMIYORDU, yani su soru logdan
		// cevaplanamiyordu:
		//
		//   "karsi taraf vida gevsetti, bizim tarafta ne oldu?"
		//
		// Yol buraya [ACT-REMOTE] olarak yazilir. Amac tek degil:
		// kullanici canli senkronu gozle dogruladiginda, gorsdugunu
		// buradaki kayitla karsilastirabilsin.
		static readonly System.Collections.Generic.Dictionary<string, float>
			lastRemoteTrace = new System.Collections.Generic.Dictionary<string, float>(
				StringComparer.Ordinal);

		static void TraceRemote(string kind, string id, byte index, string what) {
			try {
				Telemetry.RemoteBoltActions++;
				string key = kind + "|" + id + "|" + index.ToString();
				float now = Clock();
				float prev;
				if (lastRemoteTrace.TryGetValue(key, out prev) && now - prev < 0.5f)
					return; // 500 ms debounce
				lastRemoteTrace[key] = now;
				SmpLog.Print("[ACT-REMOTE] " + kind + " " + id
					+ (index != 255 ? "#" + index.ToString() : "")
					+ "  " + what + "   <- karsi taraftan");
			}
			catch { }
		}

		/// <summary>
		/// Tablo henüz kurulmadı mı? (dünya taraması bitmedi)
		///
		/// NEDEN: client, SyncDone'u dünyası taranmadan alıyor ve o turdaki
		/// her kayıt "kimlik çözülemedi" diye düşüyordu. Canlı ölçüm:
		/// 903 cıvata kaydının 602'si bu yüzden "parça-yok" sayılıyordu —
		/// oysa bunlar bozuk kayıt değil, ZAMANLAMA kaybıydı.
		/// </summary>
		internal static bool TableNotReady {
			get { return parts.Count == 0 && BoltCount == 0; }
		}


		/// <summary>
		/// Bu nesne `Database/...` bayrak nesnesi mi?
		///
		/// Kural: herhangi bir atasi tam adi "Database" ile eslesiyor. Bu
		/// bayraklar parcanin kendisi DEGIL, kaydin ayri bir kolu; o yuzden
		/// "Database" tam adi en guvenilir sinyal (isim eslesmesiyle
		/// parca baglamaya calisinca WiringBatteryPlus != trigger_battery
		/// gibi sahte eslesmeler cikiyor).
		/// </summary>
		static bool IsDatabaseFlagObject(GameObject go) {
			if (go == null) return false;
			try {
				Transform t = go.transform;
				int guard = 0;
				while (t != null && guard++ < 16) {
					if (string.Equals(t.name, "Database", StringComparison.Ordinal)) return true;
					t = t.parent;
				}
			} catch { }
			return false;
		}

		/// <summary>
		/// `Database/.../Data` FSM'indeki "Bolted" bool'unu da bagla.
		///
		/// "Installed" zaten Attach() icinde baglaniyor. "Bolted" ise parca
		/// seviyesinde "hepsi sikildi mi" bilgisi ve IsGroupBolted() /
		/// BoltCheck tarafindan kullaniliyor; baglanmazsa karsi tarafta
		/// parca takili kalir ama "sikili" isaretlenmez, yani ikinci bir
		/// montaj/sokme turu tetiklenir.
		///
		/// BILINCLI OLARAK InstalledVars'A EKLENMEZ: InstalledOf() oradan
		/// "kurulu mu" okuyor, "hepsi sikildi mi" degil.
		/// </summary>
		static void BindDatabaseFlag(SatsumaPart part, PlayMakerFSM fsm) {
			if (part == null || fsm == null) return;
			if (seenDatabaseFlags.Add(part.Id)) databaseFlagCount++;
			// v18 (b): the `Data` FSM itself. Install/LateEntry reach this
			// from one branch only ("Data" + IsDatabaseFlagObject), so this
			// assignment is what "this row has a Database Data FSM" means
			// everywhere else in the file. First one wins, like the two
			// bindings below.
			if (part.Data == null) part.Data = fsm;
			try {
				FsmBool bolted = FsmVars.Bool("database flag", fsm, "Bolted");
				if (bolted != null && part.GroupBolted == null) part.GroupBolted = bolted;
				// v18 (a): parca hasari.
				// DOKUM KANITI: `Database/*|Data` satirlari `Usage, Water,
				// Bolted, Damaged, Installed, UniqueTagBolted,
				// UniqueTagDamaged, ...` bildiriyor. `Damaged` AYRI bir
				// bayrak, `Bolted`dan bagimsiz: kirmilan vida
				// `Bolted=false` yazar ama parca `Damaged` DEGISMEZ.
				// Satsuma icinde `Damaged` YAZAN bir FSM yok (okuyan 12
				// nokta var); yazan tek yer
				// `REPAIRSHOP/Jobs/EngineFix|Work`. Yani hasar kaydi
				// `Database` satirinda duruyor, oradan okunmali.
				FsmBool dmg = FsmVars.Bool("database damage flag", fsm, "Damaged");
				if (dmg != null && part.DbDamaged == null) part.DbDamaged = dmg;
			} catch { }
		}

		/// <summary>
		/// Tabloya giren Database bayrak parcasi sayisi (teshis / `smp ls`).
		/// </summary>
		public static int DatabaseFlagParts { get { return databaseFlagCount; } }
		static int databaseFlagCount;
		static readonly HashSet<string> seenDatabaseFlags = new HashSet<string>(StringComparer.Ordinal);

		// ------------------------------------------------------------------
		// NON-SATSUMA (STORE / BOXED) PART STATE -- SECOND TABLE MARKER
		// ------------------------------------------------------------------
		//
		// `Installable` means exactly one thing: "this row is a Satsuma
		// mount slot" (Install(): `if (IsUnderSatsuma(go)) part.Installable =
		// true`). Install() scans the WHOLE scene, so the CARPARTS / STORE /
		// YARD / ITEMS / REPAIRSHOP rows are already in the table and their
		// BOLTS already replicate (BoltSync.Collect walks every row, there is
		// no Installable filter there). Their `installed` BOOL does not:
		// WritePartBatch drops every `!Installable` row, so a part taken out
		// of its box never converges on the peer.
		//
		// `Installable` IS DELIBERATELY NOT REUSED. It is read by OwnerOf's
		// precedence, by StepVerify's skip, by InstalledOfObject, by
		// CountInstallable and by WritePartBatch. Flipping it for store rows
		// would move all five at once and none of it would be reviewable.
		// This is a separate marker over the same ids.
		//
		// MEMBERSHIP (all of them, see BuildStorePartTable):
		//   1) not an installable slot -> a store/boxed row, not a car mount
		//   2) Assembly or Removal FSM  -> a REAL part identity. The rows
		//      OwnerOf invents from its immediate-parent fallback are
		//      Installable == false AND HasPartFsm == false, so this drops
		//      them and the bare store bolt containers with them.
		//   3) a WRITABLE Installed flag -> InstalledVars.Count > 0, or
		//      DatabaseInstalledFlag(part) != null. Without one,
		//      InstalledOf() reads the FSM STATE NAME heuristically ("Mouse
		//      over" counts as installed), so there is no ground truth to
		//      put on the wire at all.
		static readonly HashSet<string> storePartIds = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>
		/// Is this row a non-Satsuma part whose state travels on the store
		/// batch?
		///
		/// The `!p.Installable` term is re-checked HERE and not only at build
		/// time: OnTrackedEvent promotes a store row to a mount slot the moment
		/// it is fitted to the Satsuma, and from that moment the Satsuma
		/// PartBatch carries it. Without the re-check the same id would leave
		/// twice on two paths.
		/// </summary>
		public static bool IsSyncableStorePart(SatsumaPart p) {
			return p != null && !p.Installable && !string.IsNullOrEmpty(p.Id)
				&& storePartIds.Contains(p.Id);
		}

		/// <summary>
		/// Store rows in the current table. Live, not cached, exactly like
		/// <see cref="InstallableCount"/>: a row can leave this set at runtime
		/// (the promotion above) and the page loop has to agree with the
		/// writer's filter, or it would resend row 0 forever.
		/// </summary>
		public static int StorePartCount {
			get {
				int n = 0;
				for (int i = 0; i < parts.Count; i++) {
					if (IsSyncableStorePart(parts[i])) n++;
				}
				return n;
			}
		}

		/// <summary>
		/// Fills <see cref="storePartIds"/>. Called from <see cref="Install"/>
		/// after <see cref="FinaliseTable"/>, so `parts` is in id order and the
		/// set is built in one deterministic pass.
		///
		/// DatabaseInstalledFlag() is called HERE, which is the same call
		/// InstalledOf() makes on the first read of that row. The store writer
		/// calls InstalledOf() on every row it writes, so the resolution
		/// happens either way; doing it at scan time only moves the timing.
		/// </summary>
		static void BuildStorePartTable() {
			storePartIds.Clear();
			int added = 0;
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				if (p == null || p.Installable) continue;
				if (string.IsNullOrEmpty(p.Id)) continue;
				// (2) real part identity: an install/removal FSM of its own.
				// `Data` flag rows (Database/*) and the synthetic bolt
				// containers OwnerOf creates both fail here.
				if (p.Assembly == null && p.Removal == null) continue;
				// (1) outside the car. Installable == false alone is not
				// enough: the Database/* Data rows are also non-installable
				// rows, and they are scene-rooted rather than car-rooted.
				if (IsUnderSatsuma(p.Object)) continue;
				// (3) writable ground truth.
				if (p.InstalledVars.Count == 0 && DatabaseInstalledFlag(p) == null) continue;
				if (storePartIds.Add(p.Id)) added++;
			}
			SmpLog.Log("store part table: " + added + " non-Satsuma part(s) with a "
				+ "writable Installed flag, of " + parts.Count + " table entries ("
				+ InstallableCount + " Satsuma slot(s)).");
		}

		/// <summary>
		/// A part registered AFTER the scan (LateEntry) can be a store row too.
		/// Same test as BuildStorePartTable, without the walk.
		/// </summary>
		static bool RegisterStorePart(SatsumaPart part) {
			if (part == null || part.Installable || string.IsNullOrEmpty(part.Id)) return false;
			if (part.Assembly == null && part.Removal == null) return false;
			if (IsUnderSatsuma(part.Object)) return false;
			if (part.InstalledVars.Count == 0 && DatabaseInstalledFlag(part) == null) return false;
			return storePartIds.Add(part.Id);
		}

		// ------------------------------------------------------------------
		// v18 (b): DATABASE/* FLAG ROWS -- THIRD TABLE MARKER
		// ------------------------------------------------------------------
		//
		// THE GAP. Install() has always put the `Database/*` `Data` rows in
		// the table and BindDatabaseFlag has always bound GroupBolted and
		// DbDamaged on them, but nothing carried those two bits out:
		//   WritePartBatch      skips `!Installable`, and a Database row is
		//                       not under SATSUMA(557kg, 248)
		//   WriteStorePartBatch needs an Assembly or Removal FSM, and a
		//                       Database row has only `Data`
		// So the damage bit and the bolted bit existed locally and stopped
		// at the wire. This list is the discovery half of the channel that
		// carries them.
		//
		// A SEPARATE LIST, NOT A NEW `Installable`. Installable means exactly
		// one thing, "this row is a Satsuma mount slot", and it is read by
		// OwnerOf's precedence, StepVerify's skip, InstalledOfObject,
		// CountInstallable and WritePartBatch. Flipping it for flag rows
		// would move all five at once and none of it would be reviewable --
		// the same argument that produced storePartIds above.
		//
		// MEMBERSHIP (all four, see BuildDatabaseFlagPartTable):
		//   1) `Data != null`       -> a `Data` FSM was seen on this object
		//   2) IsDatabaseFlagObject -> it sits under a `Database` ancestor
		//   3) DbFlagBits != 0      -> at least one of Installed / Bolted /
		//                              Damaged is really declared. The 119
		//                              rows that declare none are exactly
		//                              what this test excludes, and it is
		//                              what keeps the batch small.
		//   4) !IsSyncableStorePart -> not already on the store batch, so
		//                              one id cannot leave on two paths
		//
		// ORDER. Built after FinaliseTable() AND after
		// BuildStorePartTable(), in one pass over an already sorted `parts`,
		// so it inherits the id order FinaliseTable() establishes
		// (StringComparer.Ordinal) and paging is as deterministic as Table.
		// Nothing appends to it afterwards: a row born after the scan cannot
		// become a flag row, because Installable is only raised for objects
		// under the car and a `Database` row is not under the car.
		static readonly List<SatsumaPart> databaseFlagParts = new List<SatsumaPart>();
		static readonly System.Collections.ObjectModel.ReadOnlyCollection<SatsumaPart>
			databaseFlagPartView = databaseFlagParts.AsReadOnly();

		/// <summary>
		/// The Database flag rows, read-only, in the same id order as
		/// <see cref="Table"/>. This is the writer's page source.
		/// </summary>
		public static IList<SatsumaPart> DatabaseFlagPartTable {
			get { return databaseFlagPartView; }
		}

		/// <summary>How many rows the database-flag batch has to send.</summary>
		public static int DatabaseFlagPartCount { get { return databaseFlagParts.Count; } }

		/// <summary>Row at <paramref name="index"/> in id order; null past the end.</summary>
		public static SatsumaPart DatabaseFlagPartAt(int index) {
			return index >= 0 && index < databaseFlagParts.Count ? databaseFlagParts[index] : null;
		}

		/// <summary>
		/// Is this row one of the Database flag rows?
		///
		/// The `Installable` term mirrors IsSyncableStorePart: it is the
		/// "must not sit on two batches at once" re-check. The list itself
		/// never changes after Install(), so this predicate and
		/// <see cref="DatabaseFlagPartCount"/> always agree -- a writer may
		/// page on the count and filter on this without the page loop
		/// resending row 0.
		/// </summary>
		public static bool IsSyncableDatabaseFlagPart(SatsumaPart p) {
			return p != null && !p.Installable && databaseFlagParts.Contains(p);
		}

		/// <summary>
		/// Fills <see cref="databaseFlagParts"/>. Called from Install() right
		/// after BuildStorePartTable(), so the store set is complete (test 4)
		/// and `parts` is in id order (ORDER above).
		/// </summary>
		static void BuildDatabaseFlagPartTable() {
			databaseFlagParts.Clear();
			int admitted = 0, allThree = 0, dataRows = 0;
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				if (p == null) continue;
				// (1) a `Data` FSM on this object
				if (p.Data == null) continue;
				// (2) under a `Database` ancestor. Re-checked here instead of
				// trusted from scan time: the list outlives the scan and the
				// object is live, so the cheap walk is worth it.
				if (!IsDatabaseFlagObject(p.Object)) continue;
				dataRows++;
				// (3) something to write
				if (p.DbFlagBits == 0) continue;
				// (4) not already on the store batch
				if (IsSyncableStorePart(p)) continue;
				databaseFlagParts.Add(p);
				admitted++;
				if (p.HasAllDbFlags) allThree++;
			}
			int dbObjects = CountDatabaseObjects();
			// Scene walk unavailable (no `Database` root found): fall back to
			// the rows, so the ratio still means something instead of "of 0".
			if (dbObjects <= 0) dbObjects = dataRows;
			SmpLog.Log("database flags: " + admitted + " row(s) with a writable flag, of "
				+ dbObjects + " Database object(s) (" + allThree + " with all three).");
		}

		/// <summary>
		/// How many GameObjects live under a `Database` ancestor, counted in
		/// the SCENE and not in the table.
		///
		/// WHY: the table only holds objects an FSM was found on, so it cannot
		/// report the ones that declare nothing -- the dump has 9 such
		/// objects under `Database/` and they never become rows. A
		/// denominator taken from the table would silently read as "the ones
		/// we happen to know about".
		///
		/// Same rule as IsDatabaseFlagObject (an ancestor whose name is
		/// exactly "Database"), counted downwards from that root; the root
		/// itself is not counted, so the number reads as "objects under
		/// Database/".
		/// </summary>
		static int CountDatabaseObjects() {
			int n = 0;
			GameObject[] roots = null;
			try { roots = SceneDump.AllSceneRoots(); } catch { }
			if (roots == null) return 0;
			for (int i = 0; i < roots.Length; i++) {
				GameObject r = roots[i];
				if (r == null || !string.Equals(r.name, "Database", StringComparison.Ordinal)) continue;
				Transform[] kids = null;
				try { kids = r.GetComponentsInChildren<Transform>(true); } catch { continue; }
				if (kids == null) continue;
				for (int k = 0; k < kids.Length; k++) {
					if (kids[k] == null || kids[k] == r.transform) continue;
					n++;
				}
			}
			return n;
		}

		public static int PartCount { get { return parts.Count; } }

		/// <summary>
		/// F03_PAGE_TOTAL: sayfalama icin tablo referansi.
		///
		/// `parts` `static readonly` ve private; `NetworkPacketManager`
		/// `RetryOffset` ile "bu sayfa bos mu, yoksa gonderilemedi mi"
		/// ayrimini yapabilmek icin tabloyu OKUMAK zorunda (WritePartBatch
		/// saf bir yazici -- tasma olmadan denemek gerekiyor).
		///
		/// DONDURULMEZ: liste `readonly` ve `Clear()` sadece `Clear()`
		/// cagirildiginda degisir, o an sayfalama da durdurulur.
		/// </summary>
		public static List<SatsumaPart> PartsForPaging { get { return parts; } }


		/// <summary>
		/// F03_PAGE_TOTAL: sayfalanabilir parca sayisi.
		///
		/// `InstallableCount` yalniz `Installable` bayragini sayar.
		/// `WritePartBatch` (NetworkPacketManager.cs:78-96) ise AYNI
		/// zamanda `HasKnownInstallState` filtresi uygular ve `seen`
		/// sayacini YALNIZ o filtreden gecen satirda artirir:
		///
		///     if (!table[i].Installable) continue;
		///     if (!HasKnownInstallState(table[i])) continue;
		///     if (seen++ &lt; startIndex) continue;
		///
		/// `SendAll` `InstallableCount`'u `total` olarak kullaniyordu.
		/// Iki sayac farkli oldugu icin `offset` hicbir zaman `total`'a
		/// ulasamiyor, son sayfa bos donuyor ve dongu `break` ile
		/// KALICI olarak o satirlari atliyordu.
		///
		/// LOG KANITI (mscmp.txt):
		///   "initial sync: part paging stopped at 157/160 -- 3 installable
		///    row(s) have no readable state"
		///
		/// DIKKAT: `InstallableCount` BILEREK DEGISTIRILMEZ. `ApplySyncDone`
		/// (`:4064`, `:4080`) ve `StepVerify` (`:4029`) tablo
		/// karsilastirmasi icin onu kullanir; yeni bir filtre buraya
		/// sizarsa "tablolar farkli" yanlisi cikar. Yeni sayac YALNIZCA
		/// sayfalama dongusunde kullanilir.
		/// </summary>
		public static int CountWritableInstallable() {
			int c = 0;
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				if (p == null || !p.Installable) continue;
				if (!HasKnownInstallState(p)) continue;
				c++;
			}
			return c;
		}

		/// <summary>Entries that actually carry a part FSM, i.e. the ones that can be installed.</summary>
		public static int InstallableCount {
			get {
				int n = 0;
				for (int i = 0; i < parts.Count; i++) {
					if (parts[i].Installable) n++;
				}
				return n;
			}
		}

		public static int BoltCount {
			get {
				int n = 0;
				for (int i = 0; i < parts.Count; i++) n += parts[i].Bolts.Count;
				return n;
			}
		}

		public static uint TableHash { get { return tableHash; } }

		/// <summary>Entry at <paramref name="index"/> in id order. BoltSync walks the table with this.</summary>
		internal static SatsumaPart PartAt(int index) {
			return index >= 0 && index < parts.Count ? parts[index] : null;
		}

		/// <summary>
		/// Sorts and fingerprints the table. <see cref="Install"/> calls it after building the
		/// table; it is separate so the ordering rule has exactly one definition.
		/// </summary>
		internal static void FinaliseTable() {
			parts.Sort(delegate (SatsumaPart a, SatsumaPart b) { return string.CompareOrdinal(a.Id, b.Id); });
			tableHash = ComputeTableHash();
		}

		/// <summary>The whole table, in id order. Read only for the packet writer.</summary>
		internal static IList<SatsumaPart> Table { get { return parts; } }

		/// <summary>
		/// The table, read-only. Transform sync walks it every frame to spot
		/// moved parts, so it needs the list without the internal modifier.
		/// </summary>
		public static IList<SatsumaPart> AllParts { get { return parts; } }

		/// <summary>
		/// Looks a part up by its wire id, without the name fallback.
		/// Transform sync needs the exact entry: a fallback hit would move a
		/// different part to a wrong place, which is worse than skipping.
		/// </summary>
		public static bool TryGetById(string id, out SatsumaPart part) {
			part = null;
			if (string.IsNullOrEmpty(id)) return false;
			return byId.TryGetValue(id, out part) && part != null;
		}

		/// <summary>
		/// SMP_PART_OF_V34: bir <c>GameObject</c>'in tablo kaydini dondurur.
		///
		/// KULLANIM: <c>CarrySync.IsCarryableUse</c> (CP-04) tasinacak
		/// gorunur parcayi etiketten cozuyor, sonra "takili mi?" diye
		/// soruyor. <c>InstalledOf</c> bir <c>SatsumaPart</c> aliyor.
		///
		/// YALNIZ YOL COZUMLEMESI -- AD DONDURUSU YOK. <c>TryGetById</c>
		/// de ayni seyi yapar ve o zaten dokunulmamis. Yerel bir
		/// nesne adla eslesirse kayit yanlislar; o zaman <c>null</c>
		/// donmek, yanlis parcaya baglanmaktan iyidir.
		///
		/// Cozulemezse <c>null</c> doner ve cagiran taraf etiket +
		/// gorunurluk kanitini yeterli sayar.
		/// </summary>
		public static SatsumaPart PartOf(GameObject go) {
			if (go == null) return null;
			string id = null;
			try { id = IdOf(go); } catch { return null; }
			if (string.IsNullOrEmpty(id)) return null;
			SatsumaPart p;
			return TryGetById(id, out p) ? p : null;
		}

		public static void Install() {
			Clear();
			Root = GameObject.Find(ROOT_NAME);

			float t0 = Time.realtimeSinceStartup;

			// TEK KAYNAK: bütün sahne.
			//
			// NEDEN: cıvataların çoğu Satsuma altında DEĞİL (aşağıdaki kanıt),
			// dolayısıyla parça tablosu ve cıvata tablosu aynı nesne kümesinden
			// beslenmek zorunda. İki ayrı tarama, tutarsız sahiplik üretirdi.
			PlayMakerFSM[] fsms = AllSceneFsms();
			if (fsms == null || fsms.Length == 0) {
				SmpLog.Error("satsuma scan failed: no scene FSMs.");
				return;
			}
			if (Root == null)
				SmpLog.Warning("'" + ROOT_NAME + "' not found: no installable slots, "
					+ "but scene bolts are still replicated.");

			// Pass 1: every part entry. All entries must exist before any bolt is assigned,
			// because a bolt is attributed to its nearest part *ancestor* and the traversal
			// order of GetComponentsInChildren does not guarantee the part is seen first.
			List<KeyValuePair<string, PlayMakerFSM>> screws = new List<KeyValuePair<string, PlayMakerFSM>>(768);
			foreach (PlayMakerFSM fsm in fsms) {
				if (fsm == null) continue;
				string fsmName = SafeFsmName(fsm);
				if (string.IsNullOrEmpty(fsmName)) continue;
				GameObject go;
				try { go = fsm.gameObject; } catch { continue; }
				if (go == null) continue;

				if (fsmName == "Assembly" || fsmName == "Removal" || fsmName == "Assemble" || fsmName == "BoltCheck") {
					SatsumaPart part = EntryFor(go);
					// Montaj yuvası (installable) YALNIZCA araç altındaki parçalardır.
					// CARPARTS/STORE altındaki aynı FSM'ler cıvata sahipliği için
					// kayıt altına alınır ama montaj yuvası DEĞİLDİR: parça orada
					// takılı değildir, kutusunda duruyordur. WritePartBatch ve
					// InstallableCount zaten installable olanları taşıyor, yani
					// parça teli bu ayrımla değişmez.
					if (IsUnderSatsuma(go)) part.Installable = true;
					Attach(part, fsm, fsmName);
				}
				// v15: OYUNUN GERCEK KURULUM BAYRAKLARI.
				//
				// Dumper kaniti: Database/ altindaki `Data` FSM'leri parcanin
				// KURULU/KIRIK bilgisini TASIYAN asil yer:
				//   Database/PartsStatus/Battery
				//   Database/DatabaseMotor/Starter
				//   Database/DatabaseWiring/Wiring{BatteryHarness,BatteryMinus,
				//        BatteryPlus,Starter,Radio,Speakers}
				//   Database/DatabaseBody/Discbrake_{FL,FR}
				//   Database/DatabaseComputer/Speakers
				//   Database/DatabaseMechanics/Radio
				//   Database/DatabaseOrders/{Starter,N2O Bottle,
				//        N2O Bottle Holder,CD_player}
				//
				// ESKI KURAL: bu dal yalnizca LooksLikeSparkPlug(go) icin
				// acilirdi, yani yukaridakilerin TAMAMI eleniyordu. Sonuc:
				// hicbir parcada zemin bool yoktu, InstalledOf() FSM durum
				// adindan sezgisel okumaya dustu ("Mouse over" /
				// "Requirements" gecici durumlarini "takili" sayiyordu) ve
				// dogrulama dongusu ayni karari VERIFY_ROUNDS tur
				// tekrar gonderiyordu.
				//
				// YENI KURAL: bir `Data` FSM'i "Installed" VEYA "Bolted"
				// bool'u TASIYORSA ve Database/ altindaysa tabloya girer.
				// Montaj yuvasi DEGILDIR (Installable=false kalsin): bunlar
				// yuva degil bayrak; WritePartBatch zaten installable olanlari
				// tasiyor, yani PartBatch teli degismez.
				//
				// Baglanma tek satirlik: Attach() "Installed" bool'unu
				// InstalledVars + installedBinding'e koyuyor ve mevcut
				// FsmBool.set_Value hook'u (AssemblyPatch.OnInstalledWrite)
				// LookupInstalled(variable) ile zaten yakaliyor.
				else if (fsmName == "Data" && IsDatabaseFlagObject(go)) {
					SatsumaPart part = EntryFor(go);
					Attach(part, fsm, "Data");
					BindDatabaseFlag(part, fsm);
				}
				else if (fsmName == "Data" && LooksLikeSparkPlug(go)) {
					// BUJİ (SPARK PLUG) ÖZEL DURUMU:
					// Bujilerin FSM adı "Data"dır, "Assembly"/"Removal" YOK.
					// Montaj durumu "Data" FSM'inin "Installed" bool'unda yaşar.
					// Vida sayısı 0, dolayısıyla IsBoltlessPart() = true.
					// Bu yüzden onları Installable parça olarak kaydetmeliyiz.
					SatsumaPart part = EntryFor(go);
					if (IsUnderSatsuma(go)) part.Installable = true;
					// "Data" FSM'ini Assembly/Removal yerine kaydet (Installed bool bağlama için)
					AttachDataFsmForSparkPlug(part, fsm);
					SmpLog.Log("spark plug registered as installable part: " + part.Id);
				}
				else if (fsmName == "Screw" || LooksLikeBoltObject(go)) {
					// Buji tespiti yeni eklendi (11:30 canlı logu): bujiler
					// "Sparkplug" adında, ne "Screw" FSM ne "Bolt*" eşleşmesi
					// tutuyordu.
					//
					// CANLI LOG (12:10): buji artık tabloda (301 -> 305 bolt,
					// Tightness 301 -> 305) AMA kullanıcı bujiyi sıktığında
					// hiçbir değişken yazılmadı, yani bağladığımız "Tightness"
					// bujinin sıkma değeri DEĞİL. Bujinin gerçek
					// değişkenlerini bir kez basıyoruz; adı görünce
					// BuildBolt'a yazarız.
					if (!fsmName.Equals("Screw", StringComparison.Ordinal)
						&& LooksLikeSparkPlug(go))
					{
						DescribeBoltLikeVariables(fsm);
					}

					screws.Add(new KeyValuePair<string, PlayMakerFSM>(IdOf(go), fsm));
				}
			}

			// Pass 2: her cıvatayı en yakın parça atasına bağla (tüm sahne).
			screws.Sort(delegate (KeyValuePair<string, PlayMakerFSM> a, KeyValuePair<string, PlayMakerFSM> b) {
				int c = string.CompareOrdinal(a.Key, b.Key);
				return c != 0 ? c : string.CompareOrdinal(IdOf(a.Value.gameObject), IdOf(b.Value.gameObject));
			});
			foreach (KeyValuePair<string, PlayMakerFSM> kv in screws) {
				SatsumaPart part = EntryFor(OwnerOf(kv.Value.gameObject));
				SatsumaBolt bolt = BuildBolt(part, kv.Value);
				if (bolt != null) {
					part.Bolts.Add(bolt);

					// Bujilerde montaj durumu "Data" FSM'inin Installed
					// bool'unda yaşar; Assembly/Removal FSM'inde değil.
					// Attach() yalnız o üç adı gözden geçirdiği için bu bool
					// bağlanmıyordu, yani buji takılıp söküldüğünde
					// senkronlanmıyordu.
					//
					// CANLI LOG KANITI (12:17):
					//   bolt vars Sparkplug1 (fsm 'Data'): bool: Installed=False
					// Değişken mevcut, bağ yok.
					BindBoltInstalled(part, kv.Value);
				} else {
					// Vida gibi görünüp de Stage/Tightness'i olmayan FSM.
					// BuildBolt ikisi de yoksa null döner, yani bu nesne
					// ne loglanır ne senkronlanır. Kullanıcı "bujiyi sıktım,
					// logda yok" dediğinde cevap buradadır.
					NoteBoltLikeRejected(kv.Value);
				}
			}

			// A deterministic order makes the table hash meaningful and the diagnostics stable.
			FinaliseTable();
			BuildStorePartTable();
			// v18 (b): needs the store set (test 4) and the sorted table
			// (ORDER), so it runs after both.
			BuildDatabaseFlagPartTable();

			WatchBoltLevels();
			WatchDatabaseLevels();
			LogBoltBindingHealth();
			// v21: gercek vida sayisi `Bolt` dizisinin calisma zamani
			// uzunlugudur; dokum bir STRING degildir, yani yalnizca
			// canli okunebilir. Bu OLCER sonraki adimin tabanidir.
			LogBoltArrayCapacity();

			SmpLog.Log("satsuma: " + InstallableCount + " installable part(s) in "
				+ parts.Count + " table entries, " + BoltCount + " bolt(s), " + InstalledCount()
				+ " installed, hash 0x" + tableHash.ToString("x8") + ", scanned in "
				+ ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms");
			// ApplySyncDone'daki yönlendirme metni kendi taramamizin
			// rakamlarini gosteriyor; burada bir kez aliniyor.
			tableInstalledCount = InstalledCount();
			SmpLog.Log("bolt watcher: " + (watchedInt.Count + watchedFloat.Count)
				+ " Stage/Tightness variable(s) traced (" + watchedFloatCount
				+ " float, " + watchedIntCount + " int). The log names the real owner.");
		}

		/// <summary>
		/// Sahnedeki TÜM aktif PlayMakerFSM bileşenleri.
		///
		/// PlayMakerFSM.FsmList (token 0x060003D6) oyunun bütün aktif FSM'lerini
		/// tutuyor: kök kök gezmekten hem ucuz hem eksiksiz. Yedek yol Satsuma
		/// altıdır ki FsmList düşerse en azından eski kapsam korunsun.
		/// </summary>
		static PlayMakerFSM[] AllSceneFsms() {
			try {
				List<PlayMakerFSM> all = PlayMakerFSM.FsmList;
				if (all != null && all.Count > 0) return all.ToArray();
			}
			catch { }
			if (Root == null) return new PlayMakerFSM[0];
			try { return Root.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch { return new PlayMakerFSM[0]; }
		}

		/// <summary>Nesne Satsuma'nın altında mı? (montaj yuvası testi)</summary>
		static bool IsUnderSatsuma(GameObject go) {
			if (go == null || Root == null) return false;
			try { return go == Root || go.transform.IsChildOf(Root.transform); }
			catch { return false; }
		}

		// ------------------------------------------------------------------
		// Vida seviyesi gözlemcisi (TANIM DIŞI)
		// ------------------------------------------------------------------
		//
		// Canlı log kanıtı (23:12 ve sonrası): "probe: bolt-seen 0". Yani
		// "Screw" adlı FSM'e sahip cıvatalar bu build'de yok; kapı sökerken
		// gözlenen "pivot_door_left Tightness 31 → 0" bir TOPLAM sayaçtır.
		//
		// Burada varsayım bırakıldı: Satsuma ağacındaki HER "Stage" (int) ve
		// "Tightness" (float) değişkeni izlenir. Doğru olanlar cıvata
		// kademeleridir, olmayanlar (kapı menteşesi Moment/Tightness gibi)
		// günlüğe adıyla yazılır — yani günlük bize doğru yapıyı söyler.
		// Bu bir tanı yolu, ağ yolu değildir: BoltBatch hâlâ yalnızca
		// tablodaki gerçek cıvataları taşır.

		class Watched {
			public string Path;
			public string Var;
			public int LastStage = -1;
			public bool Counted;
		}

		static readonly Dictionary<FsmInt, Watched> watchedInt = new Dictionary<FsmInt, Watched>(RefComparer<FsmInt>.Instance);
		static readonly Dictionary<FsmFloat, Watched> watchedFloat = new Dictionary<FsmFloat, Watched>(RefComparer<FsmFloat>.Instance);

		/// <summary>İzlenecek değişken adları (küçük harf).</summary>
		static readonly string[] WATCH_INT = { "stage", "boltstage", "tightness" };
		static readonly string[] WATCH_FLOAT = { "tightness", "tight", "boltstage" };

		/// <summary>
		/// Kayıt veritabanı ağacındaki cıvata değişkenleri.
		///
		/// CANLI LOG KANITI (23:40): Satsuma ağacında 133 Stage/Tightness
		/// değişkeni bulundu ama motorun vidaları sökülürken HİÇBİRİ
		/// yazılmadı. Demek ki cıvata kademesi Satsuma altında değil. Aynı
		/// logda "Database/DatabaseBody/Door_Left Bolted=False" görünüyor:
		/// oyun per-part durumu (Installed/Bolted) ayrı bir Database ağacında
		/// tutuyor. O ağaç da taranır.
		/// </summary>
		static void WatchDatabaseLevels() {
			if (Root == null) return;
			GameObject db = null;
			try { db = GameObject.Find("Database"); } catch { }
			if (db == null) return;
			PlayMakerFSM[] fsms = null;
			try { fsms = db.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch { }
			if (fsms == null) return;

			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null || fsm.FsmVariables == null) continue;
				GameObject go = null;
				try { go = fsm.gameObject; } catch { continue; }
				if (go == null) continue;
				string path = SceneDump.PathOf(go.transform);
				if (string.IsNullOrEmpty(path)) continue;

				FsmFloat[] flts = fsm.FsmVariables.FloatVariables;
				if (flts != null) {
					for (int v = 0; v < flts.Length; v++) {
						FsmFloat x = flts[v];
						if (x == null) continue;
						string n = null;
						try { n = x.Name; } catch { }
						if (!BoltishName(n)) continue;
						if (watchedFloat.ContainsKey(x)) continue;
						watchedFloat[x] = new Watched { Path = path, Var = n };
						watchedFloatCount++;
					}
				}
				FsmInt[] ints = fsm.FsmVariables.IntVariables;
				if (ints != null) {
					for (int v = 0; v < ints.Length; v++) {
						FsmInt x = ints[v];
						if (x == null) continue;
						string n = null;
						try { n = x.Name; } catch { }
						if (!BoltishName(n)) continue;
						if (watchedInt.ContainsKey(x)) continue;
						watchedInt[x] = new Watched { Path = path, Var = n };
						watchedIntCount++;
					}
				}
			}
		}

		/// <summary>Değişken adı cıvatayı çağıştırıyor mu? (alt dize)</summary>
		static bool BoltishName(string n) {
			if (string.IsNullOrEmpty(n)) return false;
			string l = n.ToLowerInvariant();
			return l.IndexOf("tight", StringComparison.Ordinal) >= 0
				|| l.IndexOf("bolt", StringComparison.Ordinal) >= 0
				|| l.IndexOf("stage", StringComparison.Ordinal) >= 0
				|| l.IndexOf("loose", StringComparison.Ordinal) >= 0;
		}

		static int watchedFloatCount;
		static int watchedIntCount;

		static void WatchBoltLevels() {
			watchedInt.Clear();
			watchedFloat.Clear();
			watchedFloatCount = 0;
			watchedIntCount = 0;
			if (Root == null) return;
			PlayMakerFSM[] fsms = null;
			try { fsms = Root.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch { }
			if (fsms == null) return;

			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null || fsm.FsmVariables == null) continue;
				GameObject go = null;
				try { go = fsm.gameObject; } catch { continue; }
				if (go == null) continue;
				string path = IdOf(go);
				if (string.IsNullOrEmpty(path)) continue;

				FsmInt[] ints = fsm.FsmVariables.IntVariables;
				if (ints != null) {
					for (int v = 0; v < ints.Length; v++) {
						FsmInt x = ints[v];
						if (x == null) continue;
						string n = null;
						try { n = x.Name; } catch { }
						if (!Wanted(n, WATCH_INT)) continue;
						if (boltStageBinding.ContainsKey(x)) continue;   // gerçek cıvata
						if (watchedInt.ContainsKey(x)) continue;
						watchedInt[x] = new Watched { Path = path, Var = n };
						watchedIntCount++;
					}
				}
				FsmFloat[] flts = fsm.FsmVariables.FloatVariables;
				if (flts != null) {
					for (int v = 0; v < flts.Length; v++) {
						FsmFloat x = flts[v];
						if (x == null) continue;
						string n = null;
						try { n = x.Name; } catch { }
						if (!Wanted(n, WATCH_FLOAT)) continue;
						if (boltTightnessBinding.ContainsKey(x)) continue;   // gerçek cıvata
						if (watchedFloat.ContainsKey(x)) continue;
						watchedFloat[x] = new Watched { Path = path, Var = n };
						watchedFloatCount++;
					}
				}
			}
		}

		static bool Wanted(string name, string[] list) {
			if (string.IsNullOrEmpty(name)) return false;
			for (int i = 0; i < list.Length; i++) {
				if (string.Equals(name, list[i], StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		/// <summary>Gerçek cıvata değilse gözlemciye düş.</summary>
		public static bool TraceWatchedStage(FsmInt v) {
			Watched w;
			if (v == null || !watchedInt.TryGetValue(v, out w) || w == null) return false;
			try {
				int stage = v.Value;
				if (!w.Counted) { w.Counted = true; SmpTrace.BoltsSeen++; }
				if (stage == w.LastStage) return true;
				int from = w.LastStage;
				w.LastStage = stage;
				if (from < 0) return true;
				SmpTrace.Bolts++;
			}
			catch { }
			return true;
		}

		public static bool TraceWatchedTightness(FsmFloat v) {
			Watched w;
			if (v == null || !watchedFloat.TryGetValue(v, out w) || w == null) return false;
			try {
				float t = v.Value;
				int stage = (int)Mathf.Round(t);
				if (!w.Counted) { w.Counted = true; SmpTrace.BoltsSeen++; }
				if (stage == w.LastStage) return true;
				int from = w.LastStage;
				w.LastStage = stage;
				if (from < 0) return true;
				SmpTrace.Bolts++;
			}
			catch { }
			return true;
		}

		/// <summary>
		/// Reset the table. Called by Install and, importantly, when the world unloads:
		/// leaving stale object references behind across a scene change would make every
		/// id resolve to a destroyed object.
		/// </summary>
		public static void Clear() {
			parts.Clear();
			byId.Clear();
			byName.Clear();
			installedBinding.Clear();
			seenDatabaseFlags.Clear();
			databaseFlagCount = 0;
			partPathReported = false;
			boltTightnessBinding.Clear();
			boltStageBinding.Clear();
			groupTightnessBinding.Clear();
			watchedInt.Clear();
			watchedFloat.Clear();
			warnedIds.Clear();
			warnedNoEvent.Clear();
			boltQueue.Clear();
			nextReconcile = 0f;
			desiredInstalled.Clear();
			storePartIds.Clear();
			databaseFlagParts.Clear();
			verifyRound = 0;
			verifyNextAt = 0f;
			verifyRunning = false;
			verifyIndex = 0;
			Root = null;
			tableHash = 0;
			// F02_SYNC_COROUTINE: sahne degisti; eldeki sayfalama
			// coroutine'i eski tabloyu gonderiyor olabilir.
			StopRunningSync();
			syncCompleted = false;
			syncRequested = false;
			// Yeni sahne = yeni tablo: bu tablo için henüz istek
			// gönderilmedi sayılmalı, dünya da artık hazır DEĞİL.
			syncRequestedForThisTable = false;
			worldReady = false;
			// Yeni sahne = yeni oturum: yönlendirme metni tekrar yazılabilsin.
			mismatchReported = false;
			tableInstalledCount = 0;
			try { BoltSync.ResetCounters(); } catch { }
		}

		/// <summary>
		/// Nesne bir cıvata mı? (ismi "Bolt" ile başlıyor)
		///
		/// FSM adı "Screw" olmak zorunda değil: canlı log kanıtı (23:12) bu
		/// build'de "bolt-seen 0" gösterdi, yani o ad hiç eşleşmiyor.
		/// BuildBolt, "Stage" ya da "Tightness" değişkeni yoksa null döndüğü
		/// için yanlış pozitif yine de tabloya girmez.
		/// </summary>
		static readonly List<string> rejectedBoltLike = new List<string>(8);
		static int rejectedBoltLikeCount;

		/// <summary>
		/// Vida gibi görünen ama Stage/Tightness'i bulunamayan FSM'leri biriktirir.
		///
		/// NEDEN: BuildBolt her iki değişkeni de bulamazsa null döner ve nesne
		/// sessizce tablodan düşer. Kullanıcının "bujileri sıktım, logda yok"
		/// raporunun olası açıklaması budur. Sayı ve isimler tek seferde
		/// yazılır; hangi değişken adlarının aranması gerektiği ancak
		/// böyle anlaşılır.
		/// </summary>
		static void NoteBoltLikeRejected(PlayMakerFSM fsm) {
			rejectedBoltLikeCount++;
			if (rejectedBoltLike.Count >= 12) return;
			string label = "?";
			try { label = (fsm.FsmName ?? "?") + " @ '" + (fsm.gameObject != null ? fsm.gameObject.name : "?") + "'"; }
			catch { }
			if (!rejectedBoltLike.Contains(label)) rejectedBoltLike.Add(label);
		}

		/// <summary>
		/// Buji (spark plug) gibi "Data" FSM'li, Assembly/Removal FSM'siz parçalar için.
		/// Bu parçaların montaj durumu "Data" FSM'inin "Installed" bool'unda yaşar.
		/// Vida sayısı 0'dır (IsBoltlessPart = true).
		/// </summary>
		static void AttachDataFsmForSparkPlug(SatsumaPart part, PlayMakerFSM fsm) {
			if (part == null || fsm == null) return;
			
			// "Data" FSM'ini part.Assembly alanına kaydet ki Installed bool bağlansın
			// (Attach metodu Assembly/Removal/Assemble/BoltCheck için bakar)
			part.Assembly = fsm;
			
			// Installed bool'unu bağla
			FsmBool installed = FsmVars.Bool("spark plug installed bool", fsm, "Installed");
			if (installed != null && !part.InstalledVars.Contains(installed)) {
				part.InstalledVars.Add(installed);
				installedBinding[installed] = part;
			}
		}

		/// <summary>
		// ------------------------------------------------------------------
		// v23: `PART` OLAYI -> TASIANABILIR GORUNUR PARCA
		// ------------------------------------------------------------------

		/// <summary>
		/// `Assembly`/`Removal` FSM'lerinin parcayi buldugu TAG.
		///
		/// DOKUM KANITI (v23, bu turun denetimi):
		/// 99 SATSUMA `Assembly`/`Removal` kaydinin `GetChild` blogu
		/// TAM OLARAK su sekilde:
		///     'racing radiator(Clone)', 'PART', 'PART',
		///     'gameObject', 'childName', 'withTag', 'storeResult', ...
		/// Yani `childName` bir COCUK ADI DEGIL, bir TAG adidir
		/// (`withTag` = true). Etiket `PART`.
		///
		/// Bu, oyununun gizli kuralini verir: montaj tetikleyicisi
		/// (`trigger_*`) `PART` TAG'li kardesini parca sayar. Elle
		/// kurulmus bir kural DEGIL, dokumden okundu.
		/// </summary>
		public const string PART_TAG = "PART";

		/// <summary>
		/// v23: bir `trigger_*` tetikleyicisinden TASIANABILIR gorunur
		/// parcayi cozer.
		///
		/// NEDEN GEREKIYOR: `Hand|PickUp` FSM'i `PART` olayini
		/// `MousePick` ile TETIKLEYICIYE yolluyor (dokum: 99 SATSUMA
		/// `PART` alan kaydinin 99'u da `trigger_*` nesnesinde). Oyun
		/// tetikleyiciyi alir ve parcayi gizli olarak tasir.
		/// Tasima senkronu tetikleyicinin yolunu gordugu icin
		/// KULLANICI PARCAYI GORMEZ -- orada sadece bos bir kare vardir.
		///
		/// DOKUM KANITI (cozumun gecerliligi):
		///   * 95 `trigger_*` nesnesi var; 26/40'inin ayni ust nesne
		///     altinda gorunur bir kardesi var
		///     (`trigger_halfshaft_fl` -> `strut fl(xxxxx)` vb.)
		///   * `trigger_bootlid` gibi gereksiz durumlarda kardes
		///     `car body(xxxxx)` gibi GOVDE kabuklaridir; bunlar
		///     `Renderer` TASIYANLARIYLA ayirt edilir (asagida).
		///
		/// KURAL (tahmin degil, etiket):
		///   1. Nesnenin kendisi `PART` tag'liyse -> o (en dogru).
		///   2. `GetChild(i)` ile ayni ust nesnenin cocuklarini tara
		///      (`nextSibling` Unity 5'te YOK), `PART` tag'li ve
		///      `Renderer` TASIYAN ilk cocugu seç.
		///   3. Bulunamazsa `null` -> tasima YAPILMAZ (tahmin yok).
		/// </summary>
		public static GameObject CarryableOfTrigger(GameObject trigger) {
			// SMP_CARRY_ITEMPIVOT_V32: `HasRenderer` artik derin
			// arama yapiyor ve `FindPartTaggedSibling` hem cocuk
			// hem kardes tarıyor; sirа degismedi.
			if (trigger == null) return null;
			// 1) Nesnenin kendisi zaten taşınabilir mi?
			if (IsPartTagged(trigger) && HasRenderer(trigger)) return trigger;
			// 2) `PART` tag'li, gorunur cocuk/kardes ara.
			GameObject sib = FindPartTaggedSibling(trigger);
			if (sib != null) return sib;
			// 3) yok -> HICBIR SEY UYDURMA.
			return null;
		}

		/// <summary>
		/// Nesne `PART` etiketli mi?
		///
		/// ETIKET NEDEN DOKUMDE DEGIL: Unity tag'leri bir MonoBehaviour
		/// degil, proje ayaridir (`TagManager.asset`). FSM dökümü onlari
		/// icermez. DOKUMDA OLAN kanit dolaylidir ve cok guc ludur:
		/// 219 SATSUMA kaydinda token dizilimi tam olarak
		///     <parca adi>, 'PART', 'gameObject', 'childName', 'withTag'
		/// yani `childName` ALANININ DEGERI `PART` ve `withTag = true`.
		/// `GetChild` + `withTag` == "bu etiketli cocuk".
		///
		/// DOGRULAMA (canli): `smp rescan` sonrasi `[CARRY-TAG]` satirlari
		/// etiketin gercekten tanimli oldugunu ve kac nesne tasiyan
		/// sayisini yazar. `CompareTag` TANIMSIZ etikette exception
		/// attigi icin burada `try` ile yutulur -- bu yuzden sayac
		/// olmadan "etiket yok" ile "etiket var ama kimse tasimiyor"
		/// ayirt edilemez.
		/// </summary>
		static int partTagHits;
		static int partTagProbes;

		static bool IsPartTagged(GameObject go) {
			if (go == null) return false;
			try {
				partTagProbes++;
				bool r = go.CompareTag(PART_TAG);
				if (r) partTagHits++;
				return r;
			}
			catch { return false; }
		}

		/// <summary>v23: etiket cozumlemesi olcum sayaclari (canli dogrulama).</summary>
		public static int PartTagHits { get { return partTagHits; } }
		/// <summary>v23: kac nesne etiket kontrolunden gecti.</summary>
		public static int PartTagProbes { get { return partTagProbes; } }

		/// <summary>Gorunur mu? (etiketli ama mesh'i olmayan govde kabugu tasinmaz)</summary>
		// SMP_CARRY_ITEMPIVOT_V32
		//
		// ESKIDIR: `go.GetComponent<Renderer>()` -- YALNIZ kendi
		// Renderer'ina bakiyor.
		//
		// SORUN: tasinan parca bir KABUK olabilir ve
		// Renderer'i alt cocugunda. `trigger_hood` icin kardes
		// `car body(xxxxx)` gibi bir GOVDE kabugu; v23 notu "bunlar
		// `Renderer` TASIYANLARIYLA ayirt edilir" diyordu ama
		// `GetComponent` DERIN ARAMA YAPMAZ -> hep `false` ->
		// parca eleniyordu.
		//
		// DERIN ARAMA: Unity 5.0'da `GetComponentInChildren<T>(bool)`
		// YOK (derleme hatasi CS1501). Parametresiz
		// `GetComponentInChildren<T>()` VAR ve yalniz AKTIF cocuklari
		// arar. Tasinabilir parca arac altinda AKTIF durur (takili
		// parca `Activation` ile gizlenmez), o yuzden parametresiz
		// overload yeterli; yine de pasif durum icin agac elle
		// gezilir.
		static bool HasRenderer(GameObject go)
		{
			if (go == null) return false;
			try
			{
				// Unity 5.0'da parametresiz overload.
				Renderer r = go.GetComponentInChildren<Renderer>();
				if (r != null) return true;
			}
			catch { }
			try
			{
				Renderer r2 = go.GetComponent<Renderer>();
				if (r2 != null) return true;
			}
			catch { }
			// Pasif cocuklar: agaci elle gez (Unity 5 API'si).
			try
			{
				Transform[] kids = go.GetComponentsInChildren<Transform>(true);
				if (kids != null)
				{
					for (int i = 0; i < kids.Length; i++)
					{
						Transform k = kids[i];
						if (k == null) continue;
						Renderer r = k.gameObject.GetComponent<Renderer>();
						if (r != null) return true;
					}
				}
			}
			catch { }
			return false;
		}

		/// <summary>
		/// `PART` tag'li ve `Renderer` tasiyan kardes.
		///
		/// v23 NOTU: Unity 5 / Mono build'inde `Transform.firstChild`
		/// ve `nextSibling` YOK. Yalniz `GetChild(i)` + `childCount`
		/// vardir (v21'de ayni sekilde duzeltildi).
		/// </summary>
		// SMP_CARRY_ITEMPIVOT_V32
		//
		// ESKIDIR: yalniz `go.transform.parent` cocuklarini
		// ariyordu.
		//
		// DOKUM: `SATSUMA/Body/trigger_bootlid|Assembly`
		//     GetChild { gameObject=Part, childName=ItemPivot,
		//                withTag=HandChild }
		// `Part` bir UST NESNE ADIDIR; tetikleyici onun COCUGU.
		// Yani tasinan sey TETIKLEYICININ KENDI ALTINDA
		// degil, TETIKLEYICIYLE KARDEStir.
		//
		// 89 Satsuma kaydi `HandChild` iceriyor, 99 kayit
		// `ItemPivot` -- hepsi ayni desen. Cocuk olabilen de
		// var (`windows black wrap(Clone)` tetikleyicinin altinda
		// gorunuyor), bu yuzden IKISI de aranir.
		//
		// KURAL yine etiket + gorunurluk; bulunamazsa `null`.
		static GameObject FindPartTaggedSibling(GameObject go) {
			if (go == null) return null;
			// KENDI COCUKLARI (duzeltme): tetikleyicinin altinda duran
			// PART etiketli + renderer'li parca. Eski kod yalniz ust
			// nesnenin cocuklarini (kardesleri) tarıyordu.
			try {
				Transform selfT = null;
				try { selfT = go.transform; } catch { selfT = null; }
				if (selfT != null) {
					int m = 0;
					try { m = selfT.childCount; } catch { m = 0; }
					for (int k = 0; k < m; k++) {
						Transform cc = null;
						try { cc = selfT.GetChild(k); } catch { continue; }
						if (cc == null || cc.gameObject == go) continue;
						if (!IsPartTagged(cc.gameObject)) continue;
						if (!HasRenderer(cc.gameObject)) continue;
						return cc.gameObject;
					}
				}
			} catch { }
			if (go == null) return null;
			// NOT: asagidaki ilk dongu kardesleri tarar (ust nesnenin cocuklari);
			// kendi cocuklari yukaridaki blokta aranir.
			Transform parent = null;
			try { parent = go.transform.parent; } catch { parent = null; }
			int n;
			try { n = parent != null ? parent.childCount : 0; } catch { n = 0; }
			for (int i = 0; i < n; i++) {
				Transform c;
				try { c = parent.GetChild(i); } catch { continue; }
				if (c == null || c.gameObject == go) continue;
				if (!IsPartTagged(c.gameObject)) continue;
				if (!HasRenderer(c.gameObject)) continue;
				return c.gameObject;
			}
			// 2) KARDEStER (ust nesnenin diger cocuklari)
			if (parent == null) return null;
			try { n = parent.childCount; } catch { return null; }
			for (int i = 0; i < n; i++) {
				Transform c;
				try { c = parent.GetChild(i); } catch { continue; }
				if (c == null || c.gameObject == go) continue;
				// Kendisi ve diger `trigger_*` kardesler TETIKLEYICIDIR;
				// onlar `PART` etiketli olmadigi icin zaten elenir,
				// ama acikca yaziliyor: etiket degisirse yanlis nesne
				// tasinmasin.
				if (!IsPartTagged(c.gameObject)) continue;
				if (!HasRenderer(c.gameObject)) continue;
				return c.gameObject;
			}
			return null;
		}

		/// Nesne bir buji (spark plug) mı? (ismi "sparkplug" içeriyor)
		/// </summary>
		public static bool LooksLikeSparkPlug(GameObject go) {
			if (go == null) return false;
			string n = go.name;
			if (string.IsNullOrEmpty(n)) return false;
			// v22: ALT DIZE "sparkplug" -> TAM AD.
			//
			// DOKUM KANITI (kanitlanmis 5 YANLIS POZITIF):
			// alt dize su nesneleri de yakalardi ve hicbiri buji degil:
			//   CARPARTS/PartsMotor/cylinder head(Clone)/trigger_sparkplug1
			//   .../trigger_sparkplug2, 3, 4   (FSM `Assembly` = tetikleyici)
			//   STORE/LOD/ActivateStore/CarProducts/Sparkplugs  (FSM `Buy`)
			// Bunlar `screws` listesine girip `BuildBolt`'tan null donuyordu
			// ve "vida benzeri FSM tabloda yok" uyarisinda KULLANICIYA
			// YANLIS NESNELERI GOSTERIYORDU.
			//
			// Gercek bujiler `Database/PartsStatus/Sparkplug1..4` nesneleridir
			// ve ADLARI tam olarak `Sparkplug` + rakamdir. Not: bu dort
			// nesne DOKUMDE YOKTUR (canli logla kanitli) -- bu yuzden kural
			// "dokumda gorulmus" degil "canli logda gorulmus" kanitina
			// dayanir.
			string l = n.ToLowerInvariant();
			if (!l.StartsWith("sparkplug", StringComparison.Ordinal)) return false;
			string tail = n.Substring(9);
			if (tail.Length == 0) return true;            // "Sparkplug"
			for (int c = 0; c < tail.Length; c++) {
				char ch = tail[c];
				if (ch < '0' || ch > '9') return false;   // "Sparkplugs" -> hayir
			}
			return true;
		}

		/// <summary>
		/// Vida nesnesinin kendi "Installed" bool'unu sahibi parçaya bağlar.
		///
		/// Attach() yalnız Assembly / Removal / Assemble / BoltCheck FSM'lerini
		/// gezdiği için, montaj durumu başka bir FSM'de tutulan parçaların
		/// Installed bool'u bağlanmıyordu. Buji bunun canlı kanıtı: FSM adı
		/// "Data", Installed=False, ama bağ yok — yani takıp sökme ağa hiç
		/// gitmiyordu.
		/// </summary>
		static void BindBoltInstalled(SatsumaPart part, PlayMakerFSM fsm) {
			if (part == null || fsm == null) return;
			FsmBool installed;
			try { installed = FsmVars.Bool(fsm, "Installed"); }
			catch { return; }
			if (installed == null) return;
			if (!part.InstalledVars.Contains(installed)) part.InstalledVars.Add(installed);
			installedBinding[installed] = part;
		}

		static readonly List<string> boltLikeVarReports = new List<string>(4);

		/// <summary>
		/// Vida gibi görünen ama "Screw" olmayan bir FSM'in değişken
		/// adlarını bir kez basar.
		///
		/// NEDEN: "hangi değişkene bağlanmalı" sorusu ancak adı görünerek
		/// cevaplanır. Kullanıcı 12:10'da bujiyi sıktı, hiçbir yazım
		/// olmadı; demek ki nesnedeki "Tightness" sıkma değeri değil. Adı
		/// bilmeden tahmin yazmak yanlış değişkeni bağlar (daha kötü: yanlış
		/// yeri ağa gönderir).
		/// </summary>
		static void DescribeBoltLikeVariables(PlayMakerFSM fsm) {
			if (fsm == null) return;
			string objectName = "?";
			try { objectName = fsm.gameObject != null ? fsm.gameObject.name : "?"; } catch { }
			string head = objectName + " (fsm '" + (fsm.FsmName ?? "?") + "'): ";
			if (boltLikeVarReports.Contains(head)) return;
			if (boltLikeVarReports.Count >= 4) return;

			string floats = "-", ints = "-", bools = "-";
			try {
				FsmFloat[] fv = fsm.FsmVariables.FloatVariables;
				if (fv != null && fv.Length > 0) {
					System.Text.StringBuilder sb = new System.Text.StringBuilder();
					for (int i = 0; i < fv.Length && i < 10; i++) {
						if (fv[i] == null) continue;
						if (sb.Length > 0) sb.Append(", ");
						sb.Append(fv[i].Name).Append('=').Append(fv[i].Value.ToString("F2"));
					}
					if (sb.Length > 0) floats = sb.ToString();
				}
				FsmInt[] iv = fsm.FsmVariables.IntVariables;
				if (iv != null && iv.Length > 0) {
					System.Text.StringBuilder sb = new System.Text.StringBuilder();
					for (int i = 0; i < iv.Length && i < 10; i++) {
						if (iv[i] == null) continue;
						if (sb.Length > 0) sb.Append(", ");
						sb.Append(iv[i].Name).Append('=').Append(iv[i].Value.ToString());
					}
					if (sb.Length > 0) ints = sb.ToString();
				}
				FsmBool[] bv = fsm.FsmVariables.BoolVariables;
				if (bv != null && bv.Length > 0) {
					System.Text.StringBuilder sb = new System.Text.StringBuilder();
					for (int i = 0; i < bv.Length && i < 8; i++) {
						if (bv[i] == null) continue;
						if (sb.Length > 0) sb.Append(", ");
						sb.Append(bv[i].Name).Append('=').Append(bv[i].Value);
					}
					if (sb.Length > 0) bools = sb.ToString();
				}
			}
			catch (Exception e) {
				SmpLog.Warning("bolt var dump failed for '" + objectName + "': " + e.Message);
				return;
			}

			boltLikeVarReports.Add(head);
			SmpLog.Log("bolt vars " + head + "float: " + floats);
			SmpLog.Log("bolt vars " + head + "int: " + ints);
			SmpLog.Log("bolt vars " + head + "bool: " + bools);
		}

		/// <summary>
		/// Nesne adı vida gibi mi? (FSM adından bağımsız)
		///
		/// AssemblyPatch/BoltSync bu olay yolunu da kullanıyor: bujilerin
		/// FSM'i "Data" olduğu için "Screw" filtresinden geçmiyorlar, adları
		/// "SparkplugN" olduğu için buradan geçiyorlar.
		/// </summary>
		public static bool IsBoltLikeObject(GameObject go) { return LooksLikeBoltObject(go); }

		static bool LooksLikeBoltObject(GameObject go) {
			if (go == null) return false;
			string n = go.name;
			if (string.IsNullOrEmpty(n)) return false;
			if (n.StartsWith("Bolt", StringComparison.OrdinalIgnoreCase)) return true;
			// YANLIS YORUM DUZELTMESI: onceki yorum "BoltPM HICBIR
			// gecmiyor, StartsWith("Bolt") dali olu kod" diyordu. DOKUM
			// BUNU CELDI: sahnede 182 adet `BoltPM` nesnesi var ve hepsi
			// `Screw` FSM'i tasiyor. Bu dal YUKUNLUDUR, silinmemelidir.

			// BUJİLER. CANLI LOG KANITI (11:30 oturumu): kullanıcı bujileri
			// sparkplug wrench ile sıktı, [ACT] BOLT hiç yazılmadı.
			// Gercek buji nesnesi `Sparkplug1..4` (Database/PartsStatus).
			//
			// v22: alt dize -> TAM AD + rakam sonu. Alt dize, doğrulanmış
			// 5 yanlış pozitifi de yakalıyordu (4 `trigger_sparkplugN`
			// Assembly tetikleyicisi + `Sparkplugs` magaza rafı).
			if (LooksLikeSparkPlug(go)) return true;

			return false;
		}

		static SatsumaPart EntryFor(GameObject go) {
			return Register(IdOf(go), go != null ? go.name : "(null)", go);
		}

		/// <summary>
		/// Gec giris: kesiften SONRA dogan parca (magazadan alinip takilan).
		/// Ilk dokunusta (montaj olayi) kaydedilir + alt agac vidalari baglanir,
		/// ayni olay bu cagrida akmaya devam eder. Nadir yol: maliyet onemsiz.
		/// </summary>
		static SatsumaPart LateEntry(PlayMakerFSM fsm, string fsmName, GameObject go) {
			if (fsm == null || go == null || string.IsNullOrEmpty(fsmName)) return null;
			SatsumaPart part = null;
			try {
				if (fsmName == "Assembly" || fsmName == "Removal" || fsmName == "Assemble" || fsmName == "BoltCheck") {
					part = EntryFor(go);
					if (IsUnderSatsuma(go)) part.Installable = true;
					Attach(part, fsm, fsmName);
					// Late scan miss: a store row born after Install() still belongs
					// in the store batch. Attach() runs first so the FSM fields the
					// test reads are already set.
					if (!part.Installable && RegisterStorePart(part) && Telemetry.Verbose)
					SmpLog.Debug("satsuma", "store", "late store part: " + part.Id);
				}
				else if (fsmName == "Data" && IsDatabaseFlagObject(go)) {
					part = EntryFor(go);
					Attach(part, fsm, "Data");
					BindDatabaseFlag(part, fsm);
				}
				else if (fsmName == "Data" && LooksLikeSparkPlug(go)) {
					part = EntryFor(go);
					if (IsUnderSatsuma(go)) part.Installable = true;
					AttachDataFsmForSparkPlug(part, fsm);
				}
				if (part != null) {
					SmpLog.Log("late part entry: " + part.Id + " installable=" + part.Installable);
					BindSubtreeBolts(part);
				}
			} catch { return null; }
			return part;
		}

		static void BindSubtreeBolts(SatsumaPart part) {
			if (part == null || part.Object == null) return;
			PlayMakerFSM[] fsms = null;
			try { fsms = part.Object.GetComponentsInChildren<PlayMakerFSM>(true); } catch { return; }
			if (fsms == null) return;
			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM f = fsms[i];
				if (f == null) continue;
				string fn = SafeFsmName(f);
				if (string.IsNullOrEmpty(fn)) continue;
				GameObject go2 = null;
				try { go2 = f.gameObject; } catch { continue; }
				if (go2 == null) continue;
				if (!(fn == "Screw" || LooksLikeBoltObject(go2))) continue;
				SatsumaBolt b = BuildBolt(part, f);
				if (b == null) continue;
				bool dup = false;
				for (int k = 0; k < part.Bolts.Count; k++) {
					if (part.Bolts[k] != null && part.Bolts[k].Screw == f) { dup = true; break; }
				}
				if (dup) continue;
				part.Bolts.Add(b);
				try { BindBoltInstalled(part, f); } catch { }
			}
		}

		/// <summary>Marks an entry as carrying part semantics, which is what makes it installable.</summary>
		internal static void MarkInstallable(SatsumaPart part) {
			if (part != null) part.Installable = true;
		}

		/// <summary>
		/// Adds an entry to the table. Internal so the only caller is <see cref="Install"/>,
		/// which is the single place that knows how a part was discovered.
		/// </summary>
		internal static SatsumaPart Register(string id, string name, GameObject go) {
			SatsumaPart p;
			if (byId.TryGetValue(id, out p) && p != null) return p;
			p = new SatsumaPart();
			p.Id = id;
			p.Name = name;
			p.Object = go;
			byId[id] = p;
			parts.Add(p);
			List<SatsumaPart> same;
			if (!byName.TryGetValue(name, out same)) {
				same = new List<SatsumaPart>(1);
				byName[name] = same;
			}
			same.Add(p);
			return p;
		}

		/// <summary>
		/// The part a bolt belongs to: the nearest ancestor that is already a part entry.
		///
		/// İki turlu arama. Birinci tur installable (montaj yuvası) bir giriş arar,
		/// çünkü bir cıvata başka bir cıvata kabına bağlanmamalı. İkinci tur, Satsuma
		/// DIŞINDAKİ parçalar için gerekir: mağazadaki `door left(Clone)` gibi bir
		/// parça tabloda var ama installable DEĞİL (kutusunda duruyor), ve dört
		/// cıvatası da onun adresine bağlanmalı. Sadece installable arayan eski
		/// sürüm bu cıvataların sahibini `Bolts` kabına düşürüyordu; bu da doğru
		/// çalışır ama parça bazında gruplama ve BoltCheck toplamı bozuluyordu.
		///
		/// Hiçbir giriş bulunamazsa doğrudan ebeveyn sahip olur: tekerlek ve
		/// süspansiyon gruplarının çıplak `Bolts` kabı böyle adreslenebilir kalır.
		/// </summary>
		static GameObject OwnerOf(GameObject go) {
			GameObject fallback = null;
			Transform cur = go != null ? go.transform.parent : null;
			int guard = 0;
			while (cur != null && guard++ < 64) {
				string id = IdOf(cur.gameObject);
				SatsumaPart candidate;
				if (byId.TryGetValue(id, out candidate) && candidate != null) {
					if (candidate.Installable) return cur.gameObject;
					if (fallback == null && candidate.HasPartFsm) fallback = cur.gameObject;
				}
				cur = cur.parent;
			}
			if (fallback != null) return fallback;

			// ------------------------------------------------------------------
			// YATAY KARDES (v21). Dikey yürüyüş bulamadıysa SON ÇARE.
			// ------------------------------------------------------------------
			// DOKUM KANITI (SATSUMA altında 22 `Screw` nesnesi):
			//
			// Bu vidaların parça nesnesi COĞU DEĞİL, KARDEŞİ. Dikey
			// yürüyüş yukarı çıkıp `IK_wishbone_fl`, `halfshaft_fl`,
			// `OFFSET` gibi ara kollarda hiçbir part FSM bulamadığı için
			// son satıra düşüyor ve cıvatanın EBEVEYNİNİ sentetik bir
			// tablo satırı olarak üretiyordu:
			//
			//   Chassis/FL/Wishbone/Arm/IK_wishbone_fl/BoltPM
			//     kardeş: Chassis/FL/Wishbone/Arm/IK_wishbone_fl/wishbone fl(xxxxx)
			//   Chassis/FL/halfshaft_fl/BoltPM
			//     kardeş: Chassis/FL/halfshaft_fl/halfshaft(xxxxx)
			//   FL/AckerFL/wheelFL/SpindleFL/OFFSET/BoltPM
			//     kardeş: FL/AckerFL/wheelFL/SpindleFL/OFFSET/spindle fl(xxxxx)
			//
			// DÜZELTME: ebeveynin diğer çocuklarına bak.
			//
			// SEÇİM KURALI NEDEN `BoltCheck`:
			// 10 vidanın 2 kardeşi var ve bu karışıklık GERÇEK:
			//   spindle fl(xxxxx)        -> [Removal, BoltCheck]  = PARÇA
			//   trigger_spindle_fl      -> [Assembly]             = TRIGGER
			// `BoltCheck` taşıyan kardeş parça nesnesidir (vida listesini
			// o yönetir); yalnız `Assembly` taşıyan kardeş montaj
			// tetikleyicisidir (yuvası temsil eder). Dökümde 14/14
			// çözülen vidanın `BoltCheck` taşıyan bir kardeşi VAR.
			//
			// ÖNEMLİ: bu SADECE SAHİPLİK değiştirir. Cıvata sayısı, tel
			// kimliği (`Path`), `Index` ve gönderim yolu DEĞİŞMEZ — sadece
			// cıvata artık doğru parçanın `Bolts` listesine girer, yanlış
			// bir sentetik satıra değil.
			GameObject sibling = SiblingOwnerOf(go);
			if (sibling != null) return sibling;

			return go != null && go.transform.parent != null ? go.transform.parent.gameObject : go;
		}

		/// <summary>
		/// v21: cıvatanın ebeveyni seviyesinde, `BoltCheck` (yani parça)
		/// taşıyan kardeşini bul. Yoksa `null`.
		///
		/// Kural gerekçesi için <see cref="OwnerOf"/> yorumuna bak.
		/// </summary>
		static GameObject SiblingOwnerOf(GameObject bolt) {
			try {
				if (bolt == null) return null;
				Transform parent = bolt.transform.parent;
				if (parent == null) return null;
				// Kardeş taraması: ebeveynin doğrudan çocukları. Daha derine
				// inmek yanlış olur -- `spindle fl(xxxxx)/Bolts/BoltPM`
				// gibi normal (dikey) vidalarda yanlış sahip bulurdu.
				//
				// API: Unity 5 / Mono'da `Transform.firstChild` ve
				// `nextSibling` YOK; `GetChild(i)` + `childCount` kullanilir.
				int count = 0;
				try { count = parent.childCount; } catch { return null; }
				for (int i = 0; i < count; i++) {
					Transform sib;
					try { sib = parent.GetChild(i); } catch { continue; }
					if (sib == null || sib.gameObject == bolt) continue;
					GameObject cand = sib.gameObject;
					SatsumaPart p;
					if (byId.TryGetValue(IdOf(cand), out p) && p != null
						&& p.BoltCheck != null)
						return cand;
				}
			}
			catch { }
			return null;
		}

		/// <summary>
		/// Highest `DetachN` index declared by a BoltCheck FSM; 0 when none.
		///
		/// SMEBMENIN KAYNAGI (50 Satsuma BoltCheck FSM'i, dokum kaniti): `DetachN`
		/// bir civanin SLOT INDEKSI. `Bolts OFF` durumu `GetChild` ile tag `Untagged`
		/// + isim `DetachN` cocugunu bulur, `SetFsmBool` ile o civa icin bir bool
		/// yazar ve sonra `Detach` olayini `Removal` FSM'ine gonderir. Yani oyunun
		/// kendi yuvasi; bizim `Bolt`/`BoltPM` sayimizla ayni sey DEGIL.
		///
		/// NEDEN `FsmVars.Bool` DEGIL: onun son care ad aramasi case-insensitive
		/// ALT DIZE arar; `DetachPart`, `Detach`, `Detach10` gibi adlari ve uyari
		/// kuyrugunu da karistirir. Burada TAM AD, ordinal, rakam sonu ile sayilir.
		/// Duz `Detach` (Removal'in tek anahtari) ve `DetachPart` sayilmaz.
		///
		/// UST SINIR 99: gercek aralik 1..9 (BoltPM 0..8 asameli). Bozuk bir ad
		/// (orn. `Detach2048`) sayimi kirletmesin diye kirilir.
		/// </summary>
		static int DetachSlotsOf(PlayMakerFSM fsm) {
			if (fsm == null) return 0;
			int max = 0;
			// (1) FSM'in KENDI bool'lari.
			FsmBool[] bools = null;
			try { bools = fsm.FsmVariables.BoolVariables; } catch { bools = null; }
			max = MaxDetachIndex(bools, max);
			if (max > 0) return max;

			// (2) GLOBAL bool'lar.
			//
			// DNSPY KANITI: FsmVariables::GetFsmBool once kendi
			// BoolVariables'inda TAM AD ile arar, bulamazsa
			// FsmVariables.GlobalVariables.BoolVariables'a da bakar.
			// Yani bir FSM, KENDI bildirmedigi bir bool'u global
			// degiskenden okuyabilir. Ilk surum yalniz (1)'e bakti ve
			// canli oturumda 0 dondu; dokum ise 10 BoltCheck'te
			// `Detach1..N` gosteriyor -- demek ki bunlar global.
			FsmBool[] glob = null;
			try {
				FsmVariables g = FsmVariables.GlobalVariables;
				if (g != null) glob = g.BoolVariables;
			}
			catch { glob = null; }
			if (glob != null) max = MaxDetachIndex(glob, max);
			if (max > 0) return max;

			// (3) v29: `Detach1..N` bir `FsmGameObject`, bool DEGIL.
			//
			// DOKUM KANITI: `BoltCheck` bildirilen kuyrugu (orn.
			// `Chassis/RL/TrailArm/Arm/trail arm rl(xxxxx)`, nstr=340):
			//   Chance, Tightness, Part, UniqueTagArray, UniqueTagFloat,
			//   Detach1, Detach2, Detach3, Detach4, Detach5, Detach6,
			//   db_BoltThis1, db_BoltThis2, db_BoltThis3, db_ThisPart
			// Sinif siralamasi: `BoltedNO/YES` = FsmEvent,
			// `Chance/Tightness` = FsmFloat, `Part/UniqueTag*` = FsmString,
			// `Detach1..N` + `db_*` = FsmGameObject. `db_ThisPart` kesin
			// FsmGameObject oldugu icin, `DetachN` de ayni blokta oldugu
			// icin FsmGameObject'tir.
			//
			// DOGRULAMA (ayni kaydin `Bolts OFF` durumu):
			//   GetChild(gameObject=Detach3, childName=Detach3,
			//            withTag=Untagged, storeResult=<local>)
			//   SetFsmBool(gameObject=<storeResult>, fsmName='Removal',
			//              variableName='Detach', setValue=<Removal>)
			// Yani `DetachN` bir VIDANIN `Removal` FSM'idir.
			//
			// ETKI: onceki kod yalniz `FsmBool[]` okudugu icin 53 yuvayi
			// HIC gormuyordu; canli log her seferinde `0 yuva` diyordu.
			// SMP_DETACH_GO_V29
			FsmGameObject[] goss = null;
			try { goss = fsm.FsmVariables.GameObjectVariables; }
			catch { goss = null; }
			if (goss != null) max = MaxDetachIndexGo(goss, max);
			if (max > 0) return max;
			try {
				FsmVariables g2 = FsmVariables.GlobalVariables;
				if (g2 != null) max = MaxDetachIndexGo(g2.GameObjectVariables, max);
			}
			catch { }
			return max;
		}

		/// <summary>
		/// v29: `FsmGameObject[]` icindeki en buyuk `DetachN` indeksi.
		///
		/// `DetachN` adinin SONUNDA rakam olmali ve bastan `Detach`
		/// gelmeli; `Detach` (rakamsiz) bir OLAY/Bool adidir, yuva degil.
		/// </summary>
		static int MaxDetachIndexGo(FsmGameObject[] gos, int start) {
			if (gos == null) return start;
			int max = start;
			for (int i = 0; i < gos.Length; i++) {
				if (gos[i] == null) continue;
				string n = null;
				try { n = gos[i].Name; } catch { }
				if (string.IsNullOrEmpty(n)) continue;
				if (!n.StartsWith("Detach", StringComparison.Ordinal)) continue;
				if (n.Length <= 6) continue;           // "Detach" -> rakam yok
				int k = 0;
				bool allDigits = true;
				for (int c = 6; c < n.Length; c++) {
					char ch = n[c];
					if (ch < '0' || ch > '9') { allDigits = false; break; }
					k = k * 10 + (ch - '0');
				}
				if (!allDigits || k <= 0) continue;
				if (k > max) max = k;
			}
			return max;
		}

		/// <summary>
		/// v21: bir FsmBool dizisindeki en buyuk `DetachN` indeksi.
		/// </summary>
		static int MaxDetachIndex(FsmBool[] bools, int start) {
			if (bools == null) return start;
			int max = start;
			for (int i = 0; i < bools.Length; i++) {
				FsmBool v = bools[i];
				if (v == null) continue;
				string n = null;
				try { n = v.Name; } catch { continue; }
				if (string.IsNullOrEmpty(n)) continue;
				if (!n.StartsWith("Detach", StringComparison.Ordinal)) continue;
				string tail = n.Substring(6);
				if (tail.Length == 0) continue;
				int idx = 0;
				for (int c = 0; c < tail.Length; c++) {
					char ch = tail[c];
					if (ch < '0' || ch > '9') { idx = -1; break; }
					idx = idx * 10 + (ch - '0');
					if (idx > 99) { idx = -1; break; }
				}
				if (idx > max) max = idx;
			}
			return max;
		}

		/// <summary>
		/// v21: `Bolt` DEGISKENININ tipini kur.
		///
		/// HEDEF: bir `Screw` FSM'inin kac civata yonettigini olcmek.
		/// `Screw` govdesi `GetChild(..., 'Bolt', ThisBolt)` + `ArrayIndex`
		/// kullaniyor, yani `Bolt` bir toplama/indis degiskeni.
		///
		/// ANCAK BU API'DE DIZI DEGISKENI YOK. Dogrulama (dnSpy, bu
		/// build'in `PlayMaker.dll`'i):
		///   `HutongGames.PlayMaker.FsmVariables` -> 14 alan:
		///     floatVariables, intVariables, boolVariables, stringVariables,
		///     vector2/3Variables, colorVariables, rectVariables,
		///     quaternionVariables, gameObjectVariables, objectVariables,
		///     materialVariables, textureVariables
		///   HICBIRISI dizi degil.
		///   `HutongGames.PlayMaker.FsmFloat` -> TEK `private float value`,
		///     `Value` ozelligi; dizi uyesi YOK.
		///   `HutongGames.PlayMaker.FsmVar` -> yalniz `floatValue`,
		///     `intValue`, `boolValue`, `stringValue`, `vector4Value`,
		///     `objectReference`; dizi uyesi YOK.
		/// Yani bu PlayMaker surumunde dizi degiskeni YOKTUR; `Bolt`
		/// ya duz bir `FsmFloat` ya da harici bir `ArrayList`'tir.
		///
		/// SONUÇ: GERÇEK VIDA SAYISI bu build'in public API'siyle
		/// okunamaz. Uydurma sayi URETMIYORUZ; `Detach1..N` (asagida)
		/// tek guvenilir yuvadir ve o da 40/50 parçada YOKTUR.
		/// </summary>
		static int BoltArrayLengthOf(PlayMakerFSM screw) {
			// BILEREK BOS DONER: yukaridaki dogrulama bu olcumu
			// desteklemiyor. Bir `float` degerini "dizi uzunlugu" gibi
			// sunmak, dogru olmayan bir cift sayi uretirdi.
			return 0;
		}

		/// <summary>
		/// v21: `Bolt` dizisi kapasitesini OLCE ve tek satirlik ozet yaz.
		///
		/// Bu SATIR bir sonraki adimin (yuvadan vidaya baglama) tabanidir.
		/// Ucm sayi:
		///   nWithArray  : `Bolt` dizisi okunabilen BoltPM sayisi
		///   nMulti      : dizisi 1'den UZUN olan BoltPM sayisi
		///                  (1 BoltPM birden fazla civata yonetiyor demektir)
		///   totalSlots  : bu BoltPM'lerin yonettigi yuva toplami
		///                  -> gercek vida sayisi ile tablodaki arasindaki fark
		///
		/// Uydurma kayit YAPILMAZ. Yalnizca olcer.
/// SATIR bir sonraki adimin (yuvadan vidaya baglama) tabanidir.
		///
		/// YALNIZCA `Detach1..N` sayilir, cunku `Bolt` dizisi bu
		/// build'in API'siyle okunamaz (`BoltArrayLengthOf` yorumuna bak:
		/// `FsmVariables` dizi degiskeni TASIYAMIOR).
		///
		///   nDetach    : `DetachN` bildiren BoltCheck sayisi
		///   nOver      : DetachSlots > Bolts.Count olan parca sayisi
		///   totalSlots : Σ(DetachSlots) -- bilinen en iyi YUVA tahmini
		///   gap        : totalSlots - BoltCount
		///
		/// gap > 0 ise tabloda bilinen yuvadan az civata baglanmis
		/// demektir; bu bir ACIK'tir ve uydurma kayitla kapatilmaz
		/// (bir BoltPM tek bir Stage/Tightness yaziyor).
		/// </summary>
		static void LogBoltArrayCapacity() {
			try {
				int nDetach = 0, nOver = 0, totalSlots = 0;
				for (int i = 0; i < parts.Count; i++) {
					SatsumaPart p = parts[i];
					if (p == null || p.DetachSlots <= 0) continue;
					nDetach++;
					totalSlots += p.DetachSlots;
					if (p.DetachSlots > p.Bolts.Count) nOver++;
				}
				SmpLog.Log("bolt yuva (Detach1..N, canli): " + nDetach
					+ " parca Detach bildiriyor, " + nOver
					+ " parcada tablodan fazla yuva var, toplam " + totalSlots
					+ " yuva | tablo: " + BoltCount + " civata -> fark: "
					+ (totalSlots - BoltCount)
					+ "  (`Bolt` dizisi bu build'de API ile OKUNAMAZ:"
					+ " FsmVariables dizi degiskeni icermiyor)");
			}
			catch (Exception e) {
				SmpLog.Debug("satsuma", "bolt", "bolt yuva olcumu basarisiz: "
					+ e.GetType().Name);
			}
		}

		static void Attach(SatsumaPart part, PlayMakerFSM fsm, string fsmName) {
			if (part.Assembly == null && fsmName == "Assembly") part.Assembly = fsm;
			else if (part.Removal == null && fsmName == "Removal") part.Removal = fsm;
			else if (part.Assemble == null && fsmName == "Assemble") part.Assemble = fsm;
			else if (part.BoltCheck == null && fsmName == "BoltCheck") part.BoltCheck = fsm;

			if (fsmName == "BoltCheck") {
				// Only 2 of the 50 BoltCheck FSMs in the shipped build declare these two, so a
				// missing one is normal and not a failure: the per-bolt Stage/Tightness still
				// sync, only the part level roll-up the game derives from them does not.
				FsmFloat t = FsmVars.Float("the part roll-up will follow the bolts instead", fsm, "Tightness");
				if (t != null) {
					if (part.GroupTightness == null) part.GroupTightness = t;
					groupTightnessBinding[t] = part;
				}
				if (part.GroupBolted == null) {
					part.GroupBolted = FsmVars.Bool("the part roll-up will follow the bolts instead", fsm, "Bolted");
				}
				// // SMP_DETACH_SLOTS_V1
				// Kac yuva bildirildi (SatsumaPart.DetachSlots). Sadece
				// SAYIM: bu degerden HICBIR SatsumaBolt uretilmez. Tek bir
				// BoltPM tek `Stage` + tek `Tightness` yaziyor, yani N kayit
				// uydurmak telde N-1 var olmayan degisken demek. Bkz.
				// SatsumaPart.DetachSlots ve SatsumaPart.BoltCapacity.
				part.DetachSlots = DetachSlotsOf(fsm);
				return;
			}

			// A missing Installed bool is also normal on the Assembly/Removal FSMs: the part
			// is still replicated, because the event is what the peer replays. The bool is
			// the ground truth only when the FSM has one.
			FsmBool installed = FsmVars.Bool("install/remove still syncs by replaying ASSEMBLE/REMOVE", fsm, "Installed");
			if (installed != null && !part.InstalledVars.Contains(installed)) {
				part.InstalledVars.Add(installed);
				installedBinding[installed] = part;
			}
		}

		static SatsumaBolt BuildBolt(SatsumaPart part, PlayMakerFSM screw) {
			FsmInt stage = FsmVars.Int("this bolt will sync by whatever values the FSM does declare", screw, "Stage");
			FsmFloat tight = FsmVars.Float("this bolt will sync by whatever values the FSM does declare", screw, "Tightness");

			// İsim çözümlemesi: önce tam ad, sonra büyük/küçük harf duyarsız,
			// sonra alt dize. Bu build'de adlar "Stage" ve "Tightness" (level2
			// kanıtı), ama tek bir sabit ad yazmak sürüm değişince sessizce
			// bozulur: BuildBolt null döner ve cıvata tabloda görünmez.
			if (stage == null) stage = BoltStageFallback(screw);
			if (tight == null) tight = BoltTightnessFallback(screw);

			if (stage == null && tight == null) return null; // an FSM called Screw that is not a bolt
			SatsumaBolt b = new SatsumaBolt();
			b.Part = part;
			b.Screw = screw;
			b.Stage = stage;
			b.Tightness = tight;
			b.Index = (byte)part.Bolts.Count;
			// Cıvatanın kendi yolu: tel kimliği bu. Kayıtlar farklı olduğunda
			// indeks kayar, yol kaymaz (canlı log kanıtı: 378/337 ↔ 369/332).
			try { b.Path = screw.gameObject != null ? IdOf(screw.gameObject) : null; }
			catch { b.Path = null; }
			if (stage != null) boltStageBinding[stage] = b;
			if (tight != null) boltTightnessBinding[tight] = b;

			// TracedStage'i TABLO KURULURKEN doldur.
			//
			// CANLI LOG KANITI (12:17): kullanıcı bujiyi gevşetti ve sıktı,
			// [ACT] BOLT sıfır. Değişken adı doğruydu (Tightness=0.00,
			// tablo 305'e çıkmıştı), yani sorun bağlamada değil.
			//
			// Sebep: TracedStage -1 ile başlıyor ve TraceStage/TraceTightness
			// "ilk ölçüm" diye ilk değişikliği yutuyor. Vida FSM'i
			// Tightness'i kademe kademe (tween) yazdığı için 0..8 arası çok
			// sayıda yazım olur, ilki yutulur, sonrakileri loglanır (koltuğu
			// 32 kademe görmemizin sebebi bu). Buji ise TEK seferde 0 -> 8
			// yazıyor, yani tek yazım oluyor ve o da yutuluyor: hiçbir şey
			// görünmüyor.
			//
			// Düzeltme: başlangıç değeri kurulurken okunur, böylece ilk
			// GERÇEK değişiklik loglanır.
			try {
				if (stage != null) b.TracedStage = stage.Value;
				else if (tight != null) b.TracedStage = (int)Mathf.Round(tight.Value);
			}
			catch { }
			return b;
		}

		/// <summary>
		/// Vida bağlama tablosunun sağlığını bir kez yazar.
		///
		/// CANLI LOG KANITI: 11:23 oturumunda 138 [ACT] BOLT satırı vardı
		/// ama BİR TANESİ bile "partId#index" biçiminde değildi; hepsi grup
		/// (BoltCheck) değişkeniydi. Yani 4 vidası olan sürücü koltuğunda
		/// kullanıcı 32 kademe gördü ama hiçbir GERÇEK vida kaydı yoktu.
		///
		/// Bu, loglama boşluğu değil, SENKRON boşluğudur: bağlama
		/// tabloları boşsa OnBoltChanged hiç çalışmaz, yani o vida ağa
		/// hiç gitmez. Bu yüzden sayılar burada açıkça yazılıyor:
		///   stageBindings = boltStageBinding.Count      (FsmInt kancası)
		///   tightBindings = boltTightnessBinding.Count  (FsmFloat kancası)
		///   boltsWithStage / boltsWithTightness = kaçı gerçekten bağlandı
		/// </summary>
		static void LogBoltBindingHealth() {
			int withStage = 0, withTight = 0, total = 0;
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				if (p == null) continue;
				for (int b = 0; b < p.Bolts.Count; b++) {
					SatsumaBolt bolt = p.Bolts[b];
					if (bolt == null) continue;
					total++;
					if (bolt.Stage != null) withStage++;
					if (bolt.Tightness != null) withTight++;
				}
			}

			SmpLog.Log("bolt bindings: " + total + " bolt(s) | Stage(FsmInt) "
				+ withStage + " | Tightness(FsmFloat) " + withTight
				+ " | tablo: stage " + boltStageBinding.Count + ", tight "
				+ boltTightnessBinding.Count
				+ " | izleyen (grup) degisken: int " + watchedInt.Count
				+ ", float " + watchedFloat.Count
				+ (total > 0 && (withStage == 0 && withTight == 0)
					? "  <== HICBIR VIDA KANCAYA BAGLANMADI, vida senkronu CALISMAZ"
					: ""));

			// SMP_DETACH_SLOTS_V1_HEALTH
			// YuvA SAYIMI: BoltCheck'in `Detach1..N` bildirimi ile
			// tablodaki vida sayisi arasindaki fark. TEK satir, parca
			// basina spam yok. Hicbir civa KAYDI bu sayidan turemez.
			{
				int detDecl = 0, detGapParts = 0, detGapSlots = 0;
				for (int i = 0; i < parts.Count; i++) {
					SatsumaPart dp = parts[i];
					if (dp == null || dp.DetachSlots <= 0) continue;
					detDecl++;
					int gap = dp.DetachSlots - dp.Bolts.Count;
					if (gap > 0) { detGapParts++; detGapSlots += gap; }
				}
				SmpLog.Log("bolt slots (Detach1..N, tani): " + detDecl
					+ " parca Detach bildiriyor, " + detGapParts
					+ " parcada tabla vida sayisindan fazla, " + detGapSlots
					+ " yuvanin hesabi yok (BU KADAR CIVA UYDURULMAZ; "
					+ "gercek sayi Bolt dizisinin calisma zamani uzunlugu)");
			}

			if (withStage == 0 && withTight == 0 && total > 0)
				SmpLog.Error("bolt bindings: hicbir vidanin Stage/Tightness degiskeni "
					+ "bulunamadi; vida senkronu devre disi. 'smp warnlist' ile "
					+ "Screw FSM'lerinin gercek degisken adlarini gorun.");

			// Vida gibi görünüp de tablodan düşenler. Bunlar loglanmaz VE
			// senkronlanmaz; kullanıcının "sıktım ama görünmedi" dediği
			// nesneler büyük olasılıkla buradadır.
			if (rejectedBoltLikeCount > 0) {
				SmpLog.Warning("bolt bindings: " + rejectedBoltLikeCount
					+ " vida benzeri FSM tabloda yok (Stage/Tightness bulunamadi) -> "
					+ "NE LOGLANIR NE SENKRONLANIR. ilk birkisi: "
					+ string.Join(", ", rejectedBoltLike.ToArray()));
				SmpLog.Warning("bolt bindings: bu nesneler icin 'smp world scan "
					+ "tightness' ile gercek degisken adlarini bul.");
			}
		}

		/// <summary>
		/// "Stage" dışında bir FsmInt arar (buyuk/kucuk harf, sonra "stage"/"bolt"
		/// alt dizesi). Donanimin degistirilmis bir build'inde tek bir sabit adin
		/// kaybolmasi civatanin tamamen tablodan cikmasi demek.
		/// </summary>
		static FsmInt BoltStageFallback(PlayMakerFSM fsm) {
			if (fsm == null || fsm.FsmVariables == null) return null;
			FsmInt[] ints;
			try { ints = fsm.FsmVariables.IntVariables; } catch { return null; }
			if (ints == null) return null;
			for (int i = 0; i < ints.Length; i++) {
				if (ints[i] == null) continue;
				string n = null;
				try { n = ints[i].Name; } catch { }
				if (string.IsNullOrEmpty(n)) continue;
				if (string.Equals(n, "Stage", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(n, "BoltStage", StringComparison.OrdinalIgnoreCase))
					return ints[i];
			}
			// v22: ALT DIZE "stage" aramasi KALDIRILDI.
			// Dokum: 71 Satsuma `Screw` nesnesinden 60'i TAM AD `Stage`
			// int'i bildiriyor ve hicbiri "stage" iceren baska bir ad
			// kullanmiyor. Alt dize, ileride `SubStage`/`Stage2` gibi bir
			// degiskene yanlis baglanirdi. Yukaridaki tam-ad turu zaten
			// gercek ismi bulur; burada baska bir sey bulunmuyor.
			return null;
		}

		/// <summary>
		/// "Tightness" disinda bir FsmFloat arar. Capraz kontrol: ayni FsmFloat
		/// BoltCheck'in part toplami da olabilir ("pivot_door_left Tightness 31 ->
		/// 0" bir toplam sayaciydi). Bu yuzden Once "tight" iceren ADLAR denenir,
		/// yalnizca hicbiri yoksa herhangi bir FsmFloat'a dusulur.
		/// </summary>
		static FsmFloat BoltTightnessFallback(PlayMakerFSM fsm) {
			if (fsm == null || fsm.FsmVariables == null) return null;
			FsmFloat[] flts;
			try { flts = fsm.FsmVariables.FloatVariables; } catch { return null; }
			if (flts == null || flts.Length == 0) return null;
			// v22: ALT DIZE "tight" aramasi KALDIRILDI.
			//
			// DOKUM KANITI (bu turun denetimi): 71 Satsuma `Screw`
			// nesnesinden HICBIRI `Tightness` float'i bildirmiyor; 60'i
			// `Stage` int'i bildiriyor. `Screw` kayitlarindaki `Tightness`
			// metni bir `AddFsmFloat { fsmName: 'BoltCheck',
			// variableName: 'Tightness' }` HEDEFidir -- yani o deger
			// parcanin BoltCheck'ine ait, civenin degil.
			//
			// Gercek vida `Stage` int'iyle senkronlanir; `Tightness` telde
			// hic gonderilmez. Onceden alt dize, `TightnessMax` (bir
			// LIMIT, 3 BoltCheck'te), `CrankTightness`, `OilpanTightness`,
			// `LiningTightness` gibi ALAKASIZ degerleri yakalayabilirdi.
			for (int i = 0; i < flts.Length; i++) {
				if (flts[i] == null) continue;
				string n = null;
				try { n = flts[i].Name; } catch { }
				if (string.IsNullOrEmpty(n)) continue;
				if (string.Equals(n, "Stage", StringComparison.OrdinalIgnoreCase)) return flts[i];
			}
			// v15: `return flts[0];` KALDIRILDI.
			//
			// Canli döküm kanıtı: Satsuma altında `Stage` ÇÖZÜLMEYEN 11
			// Screw FSM'i var ve hiçbiri vidayı temsil etmiyor:
			//   MiscParts/{brake,clutch} master cylinder(xxxxx)/OpenCap
			//   MiscParts/{,racing }radiator(xxxxx)/OpenCap
			//   MiscParts/fuel tank pipe(xxxxx)/FuelFiller/OpenCap
			//       -> float'lari Rot / ScrewAmount: KAPAGIN AÇILMA AÇISI
			//   4x .../DamperAdjustment/BoltPM  -> AdjustmentStep
			//   2x .../pivot_steering_arm_{fl,fr}/BoltPM -> Alignment/BoltSize
			//
			// Bunlar flts[0] ile "tightness" olarak telde gidiyordu ve
			// alıcı tarafta ApplyBolt `bolt.Tightness.Value = ...` yazıp
			// kapagin kendi animasyonuyla SAVASIYORDU. Senkronlanmamaktan
			// daha kotu.
			//
			// Artık null donuyoruz; BuildBolt (`:1099`) stage ve tightness
			// ikisi de null ise vidayi REDDEDIYOR. Yani bu 11 nesne
			// tablodan cikar ve kotu sayfa gitmez.
			// (Dogrulama: Satsuma vidalari 71 -> 60 ve 60'inin hepsinde
			//  gercek bir `Stage` var.)
			return null;
		}

		// ------------------------------------------------------------------
		// Identity helpers
		// ------------------------------------------------------------------

		/// <summary>Path of a GameObject relative to the Satsuma root, e.g. <c>Chassis/FL/strut fl(xxxxx)</c>.</summary>
		public static string IdOf(GameObject go) {
			if (go == null) return string.Empty;
			if (go == Root) return Root.name;

			// Satsuma DIŞINDAKİ nesneler (mağazadaki kutuda yatan parça, yaldırımdaki
			// araç, Database ağacı). Aşağıdaki yürüyüş bunlarda kök bulamaz ve
			// "SATSUMA(557kg, 248)/<sahne kökü>/CARPARTS/..." gibi HİÇBİR ŞEYİN
			// ÇÖZEMEYECEĞİ bir id üretir. Bu yüzden Satsuma'ya ait değilsek tam
			// sahne yolunu kullanıyoruz.
			//
			// İki taraf da aynı fonksiyonu, kendi Root'u ile çağırdığı için tel
			// kimliği yine eşleşir; fark sadece "ne anlama geldiği".
			if (Root != null) {
				try {
					if (!go.transform.IsChildOf(Root.transform))
						return SceneDump.PathOf(go.transform);
				}
				catch { return SceneDump.PathOf(go.transform); }
			}

			Stack<string> stack = new Stack<string>();
			Transform cur = go.transform;
			int guard = 0;
			while (cur != null && cur.gameObject != Root && guard++ < 64) {
				stack.Push(cur.name);
				cur = cur.parent;
			}
			stack.Push(Root != null ? Root.name : ROOT_NAME);
			System.Text.StringBuilder sb = new System.Text.StringBuilder(96);
			while (stack.Count > 0) {
				if (sb.Length > 0) sb.Append('/');
				sb.Append(stack.Pop());
			}
			return sb.ToString();
		}

		static string SafeFsmName(PlayMakerFSM fsm) {
			try { return fsm.FsmName; } catch { return null; }
		}

		static uint ComputeTableHash() {
			List<string> keys = new List<string>(parts.Count);
			for (int i = 0; i < parts.Count; i++) keys.Add(parts[i].Id + "#" + parts[i].Bolts.Count);
			keys.Sort(StringComparer.Ordinal);
			uint hash = 2166136261;
			for (int i = 0; i < keys.Count; i++) {
				string s = keys[i];
				for (int j = 0; j < s.Length; j++) {
					hash ^= s[j];
					hash *= 16777619;
				}
			}
			return hash;
		}

		// ------------------------------------------------------------------
		// Hook callbacks (see AssemblyPatch / BoltSyncPatch)
		// ------------------------------------------------------------------

		/// <summary>Callback for when a part's installed state changes (for boltless parts like spark plugs).</summary>
		public static Action<string, bool, GameObject> OnPartInstalledChangedCallback;

		/// <summary>A tracked FSM fired an event. Only part events are forwarded verbatim.</summary>
		public static void OnTrackedEvent(PlayMakerFSM fsm, string fsmName, string eventName) {
			if (Suppressed || parts.Count == 0) return;
			WarnIfPartPathBlindOnce();
			GameObject go;
			try { go = fsm.gameObject; } catch { return; }
			if (go == null) return;
			SatsumaPart part;
			if (!byId.TryGetValue(IdOf(go), out part) || part == null) {
				List<SatsumaPart> same;
				if (byName.TryGetValue(go.name, out same) && same != null && same.Count == 1) part = same[0];
				else part = LateEntry(fsm, fsmName, go);
				if (part == null) return;
			}
			// MAGAZA YUKSELTME: kesifte kutuda gorulen parca (Installable=false)
			// arabaya takilinca yuva olur. Bu bayrak hic yukseltilmiyordu;
			// magazadan alinan racing/GT parcalar sessizce senkron disi kaliyordu.
			if (!part.Installable) {
				try {
					if (IsUnderSatsuma(go)) {
						part.Installable = true;
						SmpLog.Log("part now installable (mounted into Satsuma): " + part.Id);
					}
				} catch { }
			}

			// LOG: parça takma/sökme. Bağlantı şartı YOK.
			//
			// CANLI LOG KANITI: buradaki eski "!SteamTransport.HasRemote"
			// şartı yüzünden tek başına test ederken hiçbir parça eylemi
			// görünmüyordu; 3634 [ACT] satırın hiçbirinde PART yoktu.
			// Teşhis, ağdan bağımsız bir konudur: kullanıcı "vida/parça
			// senkronu çalışıyor mu" diye tek başına bakmak istediğinde
			// görebilmelidir.
			if (Wanted(eventName, PART_EVENTS)) {
				// AYNI OLAY İKİ KEZ GELİYOR. Canlı log kanıtı: 7 zaman
				// damgasında tamamen aynı PART satırı iki kez yazılmış
				// (11:25:17.800, 11:26:09.790, ...).
				//
				// Sebep: PlayMaker aynı olayı iki farklı giriş noktasından
				// geçiriyor (Fsm.Event(FsmEventTarget, FsmEvent) ve
				// Fsm.Event(FsmEventTarget, string)). Yamanın ikisine de
				// bağlı olduğu için olay iki kez görülüyor.
				//
				// Bu yalnız LOG için elenir; ağa gönderim aşağıda hâlâ
				// her iki çağrıda da işlenir (PendingEvent aynı değeri
				// yazmakla eşdeğer, zararsız).
				if (!IsDuplicatePartTrace(part, eventName)) {
					SmpTrace.Parts++;
				}
			}

			// AĞA GÖNDERME bağlantı gerektirir.
			if (!SteamTransport.HasRemote) return;

			// ------------------------------------------------------------------
			// TIKLAMASIZ PARCA OLAYI AGA GIDERMEZ
			// ------------------------------------------------------------------
			// CANLI KANIT (host logu 13:48):
			//   13:48:43.327 [ACT] PART removed MiscParts/rearlight(leftx)
			//                 fsm='Removal'  click=- ui=-
			// `click=-` = oyuncu fareye basmadi.
			//
			// DUMPER KANITI (`rearlight(leftx)` fsm='Removal'):
			//   Mouse off: SetBoolValue, BoolTest, MousePickEvent
			//              GetFsmBool(Data.Installed), GetFsmBool(Data.Bolted)
			//              BoolNoneTrue(db_RemoveReq1) -> Requirements
			//              BoolAnyTrue(Installed, Bolted) -> REMOVE
			// `Removal` FSM'i `Mouse off` durumunda bool testlerinden dogrudan
			// `REMOVE` uretebiliyor -- yani oyunun KENDI otomatik durumu.
			// (Tikmali yol ayri: `Mouse over` -> `GetMouseButtonDown` ->
			//  `sendEvent "Remove part"`.)
			//
			// Bu satirlari aga gondermek karsi tarafta oyuncunun YAPMADIGI bir
			// sokme olusturuyor ve "sokmedigim halde sokuluyor" belirtisini
			// yasiyor. Tiklamasiz olayi sayip GONDERMIYORUZ; oyunun kendi
			// yerel davranisi degistirilmez.
			// Tiklamasiz olay: ANLIK tekrar yok, DURUM yakinsamasi var.
			// `Removal` FSM'i `Mouse off` + `BoolAnyTrue` ile tiklamasiz da
			// REMOVE uretir ve oyun parcayi GERCEKTEN soker; cop'e atmak
			// host sokuk / peer takili ayismasi demekti (bool'suz parcalar
			// hicbir yoldan gidemiyordu). Olay tekrarina girmeden StateWant
			// + StateDirty kurulur; flush DURUM paketi gonderir, verify
			// denetler.
			// v17: `PARTn` cok yuvali parcanin VARIANT secim olayidir,
			// yani montaj (ASSEMBLE) yonundedir. REMOVE/DISASSEMBLE ile
			// karistirilirsa `desiredInstalled` yanlis yazilir ve verify
			// turu parcayi sokmeye calisir.
			//
			// `part.HasSlots` SARTI: yuvali OLMAYAN parca icin bu yol
			// hic acilmaz, yani onun davranisi degismemis olur. (Dokumda
			// `PARTn` bildiren alti trigger nesnesinin altisi da cok
			// yuvalidir.)
			bool slotEvent = part.HasSlots && IsSlotEvent(eventName);
			bool nowInstalled = slotEvent
				? true
				: string.Equals(eventName, "ASSEMBLE",
					StringComparison.OrdinalIgnoreCase);

			// v15: GERCEK ZEMIN VARSA istege yaz.
			// InstalledVars (gercek bool) veya vida varsa oyunun kendi
			// otomatik REMOVE'i (Mouse off -> BoolAnyTrue) dogrudur ve
			// istege yazilmalidir.
			bool groundTruth = part.InstalledVars.Count > 0 || part.Bolts.Count > 0;
			if (groundTruth && desiredInstalled.Count < 1024)
				desiredInstalled[part.Id] = nowInstalled;

			if (!HasPlayerClick()) {
				AutoPartEvents++;
				// v16 (4): kablolama ayrimi yok (bakis SendWiringEvent).
				// `INSTALLED`/`ASSEMBLE` tiklamasiz gelirse de gonderilir.
				if (SendWiringEvent(part, fsm, eventName, nowInstalled)) {
					// Kablolama: OLAY da gonderildi (PendingEvent), ama alttaki
					// bool yolu da kurulmali. `PendingEvent` flush'ta
					// onceliklidir; `StateDirty` sonraki turda bool'u
					// tekrar gonderir. Ikisi birlikte calisir: olay FSM'i
					// yurutur, bool kayit durumunu zorlar.
					part.StateWant = nowInstalled;
					part.StateDirty = true;
					return;
				}

				// Zemini olmayanda istege YAZMA. InstalledOf() o durumda
				// FSM durum adindan sezgisel okuyor ("Mouse over",
				// "Requirements" gibi gecici durumlari "takili" sanir), bu
				// yuzden verify dongusu her turda ayni REMOVE'i tekrar
				// gonderiyor ve parca saliniyordu. StateWant/StateDirty
				// asagida yine kuruluyor: durum paketi GIDER, sadece
				// dogrulama sezgisine emanet edilmez.
				if (Telemetry.Verbose)
					SmpLog.Debug("satsuma", "part",
						"tiklamasiz parca olayi duruma dustu: " + eventName
						+ " @ " + part.Id + "  " + MenuMouse.ClickCtx());
				part.StateWant = nowInstalled;
				part.StateDirty = true;
				return;
			}

			// v17: `PARTn` yerelde dusunce yuvayi COZ ve parca uzerinde
			// sakla (`OutgoingPartEvent` okur). CozULEMEZSE olay
			// GONDERILMEZ: yalniz `StateWant`/`StateDirty` kurulur ve
			// `smp verbose` altinda uyarilir. Guvenilmeyen bir indeks
			// gondermek, hic gondermemekten kotudur: karsi tarafa YANLIS
			// variant kurulur ve oyuncu elindekini duzeltemez.
			//
			// TIKLAMA KAPISI VE HIZ SINIRLAYICI DEGISMEDI: bu blok
			// ustteki `HasPlayerClick()` kapisindan GECIS yapan bir yolda
			// calisir; gonderim yine tek 100 ms flush penceresinden gecer.
			if (slotEvent) {
				byte slot = ResolveSlotFromEvent(part, fsm, eventName);
				if (slot != 0) {
					part.SlotIndex = slot;
					if (Telemetry.Verbose)
						SmpLog.Debug("registry", "slot", "'" + part.Id + "' "
							+ eventName + " -> yuva " + slot + " cozuldu.");
				}
				else {
					if (Telemetry.Verbose)
						SmpLog.Debug("registry", "slot", "'" + part.Id + "' "
							+ eventName + " yuvasi cozulemedi (Installed<n> yok, "
							+ "db_* yuvasinda dolu olan yok); OLAY gonderilmedi.");
					part.StateWant = nowInstalled;
					part.StateDirty = true;
					return;
				}
			}

			part.PendingFsm = fsmName;
			part.PendingEvent = eventName;
			// BOOL'U DA KIRLIK YAP: KOOPERATIF MONTAJIN ANA ENGELİ BUYDU.
			//
			// Bu dal (gerçek montaj/sökme: oyuncu fareye bastı) yalnızca
			// `PendingEvent` kuruyordu; `StateDirty` HİÇ kurulmuyordu.
			// `SatsumaCoop.FlushPartsCapped` ise olay varken sadece OLAYI
			// gönderiyor (`if (hasEvent) {...} else {durum}`).
			//
			// Sonuç: karşı tarafa yalnızca `ASSEMBLE`/`REMOVE` olayı gidiyor,
			// zorlayan bool yazımı asla gitmiyordu. Oysa karşı tarafın kendi
			// `Assembly` FSM'i montajı CIVATA KONTROLÜNE BAKARAK kabul eder
			// ve cıvatalar henüz uyuşmadıysa reddeder (bkz. 3570-3578'in
			// kendi gerekçesi). Cıvatalar 20 Hz'de aktığı için oyuncu vidayı
			// sıktıktan hemen sonra parçayı taktığında karşı tarafın cıvata
			// kademeleri neredeyse her zaman eski olur → montaj karşı tarafta
			// SESSİZCE reddedilir ve parça orada takılı görünmez.
			//
			// Bu yüzden olay ile birlikte bool da gider: olay FSM'i yürütür,
			// bool (`ApplyPartState` -> `Installed` yazımı, 3646-3647) kayıt
			// durumunu zorlar. `FlushPartsCapped` olayı önce yollar, `StateDirty`
			// kaldığı için bir sonraki 100 ms penceresinde bool'u tekrar
			// yollar — yani ikisi birlikte çalışır (aynı gerekçe: 2425-2431).
			// Durum paketi idempotent'tir; tekrarı zararsızdır.
			part.StateWant = nowInstalled;
			part.StateDirty = true;
			// Do not clear StateDirty: the event is intent and the bool is ground truth, and
			// the two can disagree within one flush. The flush prefers the event, and the
			// next flush re-sends the bool if it is still off.
		}

		/// <summary>
		/// v16 (4): kablolama (`Assemble`) parcalarinda `ASSEMBLE`/`INSTALLED`
		/// OLAYINI gonder.
		///
		/// NEDEN GEREKLI: ustteki tiklama kapisi montaj/sokme ayrimi icin.
		/// Kablolamada o ayrim YOKTUR: dokum kaniti
		/// `Wiring/Triggers/HeadlightLeft/ConnectorLight|Assemble` dizisi
		///   [28] 'Assemble'  (state adi)
		///   [37] GetButtonDown  [40] 'FRONT LIGHTS CONNECTOR'  [41] 'Use'
		///   [63][71] CLOSELOOP
		///   [67] SendEventByName  [68] '_OtherEnd'  [69] 'Assemble'
		///   [84] SetFsmBool  [88] 'db_ThisPart' ... [93] 'Data' [94] 'Installed'
		///   [86] SendEvent -> 'INSTALLED'
		/// Yani "kablo takildi" karari kullanicinin `Use` butonuna
		/// BAGLANMIS: `Mouse over` -> `GetButtonDown(FRONT LIGHTS
		/// CONNECTOR)` -> `ASSEMBLE`. TIKLAMA vardir, ama `HasPlayerClick()`
		/// o ani yakalayamayabilir (0.5 s pencere; `SendEventByName` ile
		/// karsi uca gonderilen `CLOSELOOP` turu fareye bagli DEGILDIR).
		///
		/// SONUC: tiklamasiz dusen `ASSEMBLE` sadece bool'a dusuyordu.
		/// Bool yolu calisiyor (v16a `db_ThisPart` yazimi), ama OLAY
		/// gitmedigi icin karsi tarafta FSM hic yurumuyor: kablo
		/// gorseli/etkilesimi olusmuyor. Burada olayi da gonderiyoruz.
		///
		/// Kapsam siniri: YALNIZCA `Assemble` FSM'i olan parcalar. Montaj
		/// parcalari ustteki tiklama ayrimina dokunulmaz -- orada
		/// ayrimin sebebi BASLIKCA farkli (dumper kaniti yorumu satiri
		/// 1428-1431'de).
		/// </summary>
		static bool SendWiringEvent(SatsumaPart part, PlayMakerFSM fsm,
				string eventName, bool nowInstalled) {
			try {
				if (part == null || part.Installable) return false;      // montaj parcasi: dokunma
				if (part.Assemble == null) return false;                  // kablolama degil
				if (fsm != null && fsm != part.Assemble) return false;    // Assembly/Removal olayi
				// v16 (4): dört olay kabul. `NOTINSTALLED` kablo SOKME'dir ve
				// karsi tarafinin FSM'ini calistirmalidir, bu yuzden
				// gonderilen olay adi `ASSEMBLE` DEGIL kendisidir --
				// alici tarafta `EffectiveEvent` `DISASSEMBLE` varsa
				// onu kullanir (ayni islev, soke zinciri).
				if (!string.Equals(eventName, "ASSEMBLE", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(eventName, "INSTALLED", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(eventName, "NOTINSTALLED", StringComparison.OrdinalIgnoreCase))
					return false;
				// Kablolamanin yazilabilir bayragi yoksa (v16a: TriggerWheel
				// gibi yalniz onkosulu olanlar) bool da tasinamaz; olayi
				// tek basina gondermek yetersiz ve yaniltici olur.
				if (part.InstalledVars.Count == 0 && DatabaseInstalledFlag(part) == null)
					return false;
				part.PendingFsm = "Assemble";
				// `INSTALLED` -> `ASSEMBLE` (oyunun kendi zinciri:
				// ASSEMBLE -> ... -> INSTALLED). `NOTINSTALLED` kendi
				// adiyle gider.
				part.PendingEvent = string.Equals(eventName, "INSTALLED",
					StringComparison.OrdinalIgnoreCase) ? "ASSEMBLE" : eventName;
				part.StateWant = nowInstalled;
				part.StateDirty = true;
				if (Telemetry.Verbose)
					SmpLog.Debug("satsuma", "wire", "kablolama olayi gonderildi: "
						+ eventName + " @ " + part.Id);
				return true;
			}
			catch { return false; }
		}

		/// <summary>ASSEMBLE/REMOVE gibi montaj olayları.</summary>
		static readonly string[] PART_EVENTS = {
			"ASSEMBLE", "REMOVE", "DISASSEMBLE",
			// v16 (4): kablolama (`Assemble`) FSM'leri bu ikisini GLOBAL
			// GECIS olarak bildiriyor (dokum: 52 `Assemble` nesnesinin
			// 51'i `INSTALLED` + `NOTINSTALLED` bildiriyor, hicbiri yerel
			// `Installed` bildirmiyor). Yalniz kablolama icin ele alinir;
			// montaj parcalarinda bu iki olay hic tanimli degil, o yuzden
			// eklenmesi onlari degil, yalniz `IsPartFsm` filtresinden
			// gecmeyi etkiler -- `SendWiringEvent` bunu siniflandirir.
			"INSTALLED", "NOTINSTALLED",
			// v17: `AssemblyPatch.PART_EVENTS` ile BIRE BIR AYNI olmali.
			// `PART1..PART7` cok yuvali parcalarin variant secim olayi
			// (dokum kaniti ve `AssemblyPatch.PART_EVENTS` notu). Yalniz
			// sayili olan kabul edilir; CIBI `PART` (88 nesnenin yerel
			// `TriggerEvent` olayi) buraya da girmiyor.
			"PART1", "PART2", "PART3", "PART4", "PART5", "PART6", "PART7" };

		// ------------------------------------------------------------------
		// v17: PART1..PART7 -- cok yuvali parcalarin variant secim olayi
		// ------------------------------------------------------------------

		/// <summary>En buyuk kabul edilen yuva indeksi (dokumda en buyuk PART7).</summary>
		const int MAX_SLOT_EVENT = 7;

		/// <summary>
		/// `PART1..PART7` mi? SONRA RAKAM ZORUNLU: cipl `PART` DEGIL.
		///
		/// NEDEN: 88 `Assembly` FSM'i yerel bir `TriggerEvent`
		/// (`sendEvent = "PART"`, `collideTag` ile) uretiyor. Bu olay bir
		/// GLOBAL GECIS DEGIL, FSM'in kendi durumundan kendine gidiyor;
		/// kabul edilirse yuzlerce nesneden gelen yerel olaylar ag
		/// hattina birakilir. Yalniz `PART` + tek rakam (1..7) global
		/// gecis adidir.
		///
		/// Tahsis yapmaz; `eventName` her zaman zaten bir referans.
		/// </summary>
		public static bool IsSlotEvent(string eventName) {
			if (eventName == null || eventName.Length != 5) return false;
			if (eventName[0] != 'P' && eventName[0] != 'p') return false;
			if (eventName[1] != 'A' && eventName[1] != 'a') return false;
			if (eventName[2] != 'R' && eventName[2] != 'r') return false;
			if (eventName[3] != 'T' && eventName[3] != 't') return false;
			char c = eventName[4];
			return c >= '1' && c <= '7';
		}

		/// <summary>`PARTn` olayindan yuva indeksi (1..7). Diger her sey icin 0.</summary>
		public static byte SlotEventIndex(string eventName) {
			if (!IsSlotEvent(eventName)) return 0;
			return (byte)(eventName[4] - '0');
		}

		/// <summary>
		/// `PARTn` YERELDE dusunce hangi yuvayi kastettiğini cozer (1..7),
		/// cozulemezse 0 doner.
		///
		/// ONCELIK (dokum kaniti):
		///   `trigger_radiator` `Assembly` FSM'i `Installed` + `Installed2`
		///   bool'larini VE `ActivateThis1/2` nesnelerini bildiriyor
		///   (string dizisinin sonu: "Installed, Installed2, HandChild").
		///   Yani 1. yuvanin bool'u ADI `Installed` (rakam YOK), 2. yuvanin
		///   ki `Installed2`. Bu yuzden n==1 icin once `Installed1`, sonra
		///   `Installed` denenir.
		///   `trigger_gear_stick` yalniz `ActivateThis1/2` bildiriyor,
		///   `trigger_steering_wheel` `Part1..Part4` nesnelerini
		///   bildiriyor -- ikisinde de `Installed<n>` bool'u YOK, onlar
		///   yedeke duser.
		///
		/// NEDEN bool yalniz TRUE ise guvenilir: `PARTn` bir durum
		/// yonlendiricisidir; `SetFsmBool db_RadiatorN / Data / Installed`
		/// yazimi dokumde "Wait for assembly" -> "End" zincirinin
		/// SONUNDA gorunuyor, yani `PARTn` aninda henuz yazilmamistir.
		/// Bu yuzden `false` goruldugunde tahmin degil, yedek yol kullanilir.
		/// </summary>
		static byte ResolveSlotFromEvent(SatsumaPart part, PlayMakerFSM fsm, string eventName) {
			byte n = SlotEventIndex(eventName);
			if (n == 0) return 0;
			if (fsm != null) {
				FsmBool flag = FsmNames.Bool(fsm, "Installed" + n);
				if (flag == null && n == 1) flag = FsmNames.Bool(fsm, "Installed");
				if (flag != null) {
					try { if (flag.Value) return n; } catch { }
				}
			}
			if (part != null) {
				byte filled = part.FilledSlotIndex();
				if (filled >= 1 && filled <= MAX_SLOT_EVENT) return filled;
			}
			return 0;
		}

		/// <summary>
		/// GONDERILECEK parca olayinin adi. Yuvali parcalarda genel
		/// `ASSEMBLE` yerine `PARTn` tercih edilir.
		///
		/// NEDEN: `PARTn` karsi tarafinin kendi "find correct part N"
		/// durumlarini calistirir, yani dogru VARIANT kurulur. Duz
		/// `ASSEMBLE` gonderilirse o durumlar hic girmez.
		///
		/// DEGISMEYENLER (her durumda girdi AYNEN doner):
		///   - yuvali olmayan parca (`HasSlots == false`)
		///   - `PARTn` disindaki olaylar (REMOVE/DISASSEMBLE anlam
		///     degistirmemeli)
		///   - yuva cozulemiyorsa (0) -- tahmin GONDERILMEZ
		///   - hedef FSM `PARTn`'yi bildirmiyorsa (`ASSEMBLE` kalir)
		/// </summary>
		public static string OutgoingPartEvent(SatsumaPart p, string eventName) {
			try {
				if (p == null || !p.HasSlots) return eventName;
				if (string.IsNullOrEmpty(eventName)) return eventName;
				if (IsSlotEvent(eventName)) return eventName;   // zaten PARTn
				if (!string.Equals(eventName, "ASSEMBLE", StringComparison.OrdinalIgnoreCase))
					return eventName;
				// Canli dogruluk (yuva bool'lari) once; hafizadaki indeks
				// yalniz yuva bool'lari henuz yazilmadiysa devreye girer.
				byte slot = p.FilledSlotIndex();
				if (slot == 0) slot = p.SlotIndex;
				if (slot < 1 || slot > MAX_SLOT_EVENT) return eventName;
				string ev = "PART" + slot;
				if (!Declares(p.Assembly, ev)) {
					if (Telemetry.Verbose)
						SmpLog.Debug("registry", "slot", "'" + p.Id + "' " + ev
							+ " Assembly FSM'inde bildirilmiyor; ASSEMBLE gonderildi.");
					return eventName;
				}
				if (Telemetry.Verbose)
					SmpLog.Debug("registry", "slot", "'" + p.Id + "' ASSEMBLE -> "
						+ ev + " (yuvali parca) gonderiliyor.");
				return ev;
			}
			catch { return eventName; }
		}

		/// <summary>
		/// Oyuncu gercekten fareye basti mi? (parca sokme kapisi)
		///
		/// `Removal`/`Assembly` FSM'i bool testlerinden TIKLAMASIZ da `REMOVE`
		/// uretebiliyor (dumper kaniti: `Mouse off` -> `BoolAnyTrue(Installed,
		/// Bolted)` -> `REMOVE`). Tiklamali yol ayri: `Mouse over` ->
		/// `GetMouseButtonDown` -> `"Remove part"`. Bu yuzden ayrimi tiklama
		/// baglamindan yapiyoruz.
		/// </summary>
		static bool HasPlayerClick() {
			try {
				if (Input.GetMouseButtonDown(0)) return true;
				if (Input.GetMouseButton(0)) return true;
			}
			catch { return true; }   // girdi okunamazysa GELENEYI reddetme
			return MenuMouse.ClickRecent();
		}

		/// <summary>Tiklamasiz (otomatik) parca olayi sayisi.</summary>
		public static int AutoPartEvents;

		/// <summary>
		/// Parça yolu ilk kez çalıştığında bağlama sayılarını BİR KEZ yazar.
		///
		/// NEDEN: [ACT] PART yine de 0 çıkarsa iki olasılık kalır ve
		/// ayırt etmek zorundaydık:
		///   a) olay/FSM filtresi hedefi kaçırıyor (yol boş),
		///   b) hiçbir parçanın Installed bool'u bağlanmamış (yine yol boş).
		/// Bu iki sayı olmadan ikisi de "log yok" görünüyordu.
		/// </summary>
		static void WarnIfPartPathBlindOnce() {
			if (partPathReported) return;
			partPathReported = true;
			SmpLog.Debug("satsuma", "partpath", installedBinding.Count
				+ " Installed bool bagli, " + watchedInt.Count + "/" + watchedFloat.Count
				+ " izlenen Stage/Tightness");
		}

		static bool partPathReported;

		/// <summary>
		/// Aynı olayın iki farklı yama noktasından gelmesini eler.
		///
		/// Kural: aynı parça için aynı olay, kısa bir zaman içinde ikinci kez
		/// gelirse "yankı" sayılır ve loglanmaz. Gerçek bir kullanıcı eylemi
		/// (parçayı çıkar → geri tak) saniyeler sonra olur, yani pencere
		/// geniş tutulabilir. 250 ms: iki yama çağrısı aynı kare içinde
		/// yapıldığı için milisaniyeler yeter.
		/// </summary>
		static bool IsDuplicatePartTrace(SatsumaPart part, string eventName) {
			if (part == null) return false;
			float now = Clock();
			// Aynı olay, aynı zaman damgası tabanlı kısa anahtar.
			string key = part.Id + "|" + eventName;
			if (lastPartTraceKey == key && now - lastPartTraceAt < 0.25f) return true;
			lastPartTraceKey = key;
			lastPartTraceAt = now;
			return false;
		}

		static string lastPartTraceKey;
		static float lastPartTraceAt = float.NegativeInfinity;

		/// <summary>Olay adını okunur bir fiile çevirir.</summary>
		static string DescribePartEvent(string eventName) {
			return string.Equals(eventName, "ASSEMBLE", StringComparison.OrdinalIgnoreCase)
				? "fitted" : "removed";
		}

		/// <summary>An "Installed" bool of a known part changed.</summary>
		public static void OnInstalledChanged(SatsumaPart part) {
			if (part == null || Suppressed) return;
			WarnIfPartPathBlindOnce();

			// LOG: bağlantıdan bağımsız. ASSEMBLE/REMOVE olayı zaten
			// geldiyse tekrar yazmıyoruz (olay daha zengin bilgi veriyor).
			if (string.IsNullOrEmpty(part.PendingEvent)) {
				SmpTrace.Parts++;
			}

			// Callback for boltless parts (spark plugs, etc.) that don't have ASSEMBLE/REMOVE events
			part.StateWant = part.Installed;
			bool isBoltlessPart = part.Bolts.Count == 0 && (part.Assembly != null || part.Assemble != null);
			if (isBoltlessPart && part.InstalledVars.Count > 0 && OnPartInstalledChangedCallback != null) {
				try {
					OnPartInstalledChangedCallback(part.Id, part.Installed, part.Object);
				}
				catch { }
			}

			if (!SteamTransport.HasRemote) return;
			part.StateDirty = true;
		}

		public static void OnBoltChanged(SatsumaBolt bolt) {
			if (bolt == null || Suppressed || !SteamTransport.HasRemote) return;
			// DirtySince yalnızca ilk geçişte kurulur: sürükleme boyunca
			// her yazımda yenilemek, sessizlik kaçış yolunu hep erteler.
			if (!bolt.Dirty) bolt.DirtySince = Clock();
			bolt.Dirty = true;
		}

		internal static SatsumaPart LookupInstalled(FsmBool v) {
			SatsumaPart p;
			return installedBinding.TryGetValue(v, out p) ? p : null;
		}

		internal static SatsumaBolt LookupBoltTightness(FsmFloat v) {
			SatsumaBolt b;
			return boltTightnessBinding.TryGetValue(v, out b) ? b : null;
		}

		internal static SatsumaBolt LookupBoltStage(FsmInt v) {
			SatsumaBolt b;
			return boltStageBinding.TryGetValue(v, out b) ? b : null;
		}

		/// <summary>
		/// Parça seviyesindeki `BoltCheck/Tightness` toplamını HESAPLA ve yaz.
		///
		/// v15: bu değer daha önce hesaplanıp ATILIYORDU
		/// (BoltSyncPatch.OnTightnessWrite yalnızca LookupGroupTightness
		/// çağırıyor, sonucu kullanmıyordu). Oysa oyunun "hepsi sıkıldı"
		/// kararı buradan çıkıyor:
		///
		///   BoltCheck/Tightness -> Data/Bolted -> parça "sıkılı" sayılır
		///
		/// ve `Data/Bolted` de `Removal` FSM'inin REMOVE üretiminde
		/// BoolAnyTrue(Installed, Bolted) girdisi olarak kullanılıyor
		/// (dumper: l2__n2o_bottle_holder_xxxxx___Removal.txt). Yani
		/// toplam yakınsamazsa parça karşı tarafta "takılı ama sıkılmamış"
		/// kalır ve ikinci bir montaj turu tetiklenir.
		///
		/// KARSILASTIRMA: zaten aynı değer ise yazmıyoruz, çünkü her yazım
		/// `FsmFloat.set_Value` Postfix'ini tetikler ve gereksiz yankı
		/// paketi doğururdu.
		/// </summary>
		internal static void ConvergeGroupTightness(FsmFloat v) {
			if (v == null) return;
			SatsumaPart p;
			if (!groupTightnessBinding.TryGetValue(v, out p) || p == null) return;
			int total = p.Bolts.Count;
			if (total == 0) return;
			float sum = 0f;
			for (int i = 0; i < total; i++) {
				float t = p.Bolts[i].TightnessValue;
				if (t < 0f) t = 0f;
				if (t > 8f) t = 8f;
				sum += t;
			}
			float avg = sum / total;
			try {
				if (Mathf.Abs(v.Value - avg) > 0.001f) v.Value = avg;
			} catch { }
			// "Bolted" bayrağı da aynı eşikten türer.
			try {
				if (p.GroupBolted != null) {
					bool bolted = avg >= 7.999f;
					if (p.GroupBolted.Value != bolted) p.GroupBolted.Value = bolted;
				}
			} catch { }
		}

		internal static SatsumaPart LookupGroupTightness(FsmFloat v) {
			SatsumaPart p;
			return groupTightnessBinding.TryGetValue(v, out p) ? p : null;
		}

		// ------------------------------------------------------------------
		// Outbound flush
		// ------------------------------------------------------------------

		/// <summary>
		/// Ships accumulated changes once per <see cref="FLUSH_INTERVAL"/>: BOLTS FIRST, so
		/// the peer has bolt stages ready before it hears about part events (ASSEMBLE).
		/// Driven from the mod's Update.
		/// </summary>
		public static void Update() {
			if (parts.Count == 0) return;
			float now = Clock();
			if (now < nextFlush) return;
			nextFlush = now + FLUSH_INTERVAL;
			PollWatchedLevels();
			ReconcileBolts();
			if (!SteamTransport.HasRemote) return;
			
			// SIRALAMA DEĞİŞTİ: ÖNCE VİDALAR, SONRA PARÇALAR
			FlushBolts();
			FlushParts();
			TickVerify(now);
		}

		/// <summary>
		/// İzlenen Stage/Tightness değişkenlerini okur ve [ACT] BOLT yazar.
		///
		/// Flush'tan ayrı ve bağlantıdan bağımsız: gözlemci ağa paket
		/// GÖNDERMEZ, sadece yerel değişimi loglar. Bu yüzden bağlı olmadan
		/// da (tek başına test) anlamlıdır ve SatsumaCoop.Tick'ten HER KARE
		/// çağrılmalıdır.
		///
		/// CANLI LOG KANITI: bu metot yalnızca Update() içinden çağrılıyordu
		/// ve Update() hiçbir yerden çağrılmıyordu; sonuç BOLT sayacının
		/// sıfırda kalmasıydı. Dışarıdan açık bir giriş noktası verildi.
		/// </summary>
		public static void PollWatched() {
			// F06_POLL_HZ: tarama 10 Hz.
			//
			// ONCEDEN HER KARE calisiyordu:
			// `SteamMPBehaviour.Update` -> `SatsumaCoop.Tick()` ->
			// `PollWatched()` (SatsumaCoop.cs:69). `PollWatchedLevels` her
			// cagrida watchedFloat (133) + watchedInt (74) = 207 Fsm deger
			// okuyordu; 144 Hz'de saniyede ~29.800 property erisimi.
			//
			// GONDERIM zaten 10 Hz (`SatsumaRateLimiter.FLUSH_INTERVAL =
			// 0.10f`), yani tarama 14 KAT fazla calisiyor ve HICBIR BILGI
			// KAZANDIRMIYORDU. Ayni ritim buraya da konunca hicbir sey
			// kaybolmaz.
			float nowPoll = Clock();
			if (nowPoll < nextWatchedPoll) return;
			nextWatchedPoll = nowPoll + WATCHED_POLL_INTERVAL;
			try { PollWatchedLevels(); }
			catch (Exception e) {
				SmpLog.Warning("bolt watcher failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// İzlenen Stage/Tightness değişkenlerinde keşif farkı.
		/// Flush'tan ayrı; bağlantı yokken de (tek kendine test) çalışır.
		/// </summary>
		static readonly Dictionary<string, float> lastWatchedPartBoltLog = new Dictionary<string, float>(StringComparer.Ordinal);

		static void PollWatchedLevels() {
			if (watchedFloat.Count == 0 && watchedInt.Count == 0) return;
			foreach (KeyValuePair<FsmFloat, Watched> kv in watchedFloat) {
				FsmFloat f = kv.Key;
				Watched w = kv.Value;
				if (f == null || w == null) continue;
				float t;
				try { t = f.Value; } catch { continue; }
				int stage = (int)Mathf.Round(t);
				if (!w.Counted) { w.Counted = true; SmpTrace.BoltsSeen++; }
				if (stage == w.LastStage) continue;
				int from = w.LastStage;
				w.LastStage = stage;
				if (from < 0) continue;
				SmpTrace.Bolts++;

				// PART SEVİYESİ LOG (watched variables)
				TraceWatchedPartBoltChange(w.Path, stage > from ? "tightened" : "loosened", from, stage);
			}
			foreach (KeyValuePair<FsmInt, Watched> kv in watchedInt) {
				FsmInt v = kv.Key;
				Watched w = kv.Value;
				if (v == null || w == null) continue;
				int stage;
				try { stage = v.Value; } catch { continue; }
				if (!w.Counted) { w.Counted = true; SmpTrace.BoltsSeen++; }
				if (stage == w.LastStage) continue;
				int from = w.LastStage;
				w.LastStage = stage;
				if (from < 0) continue;
				SmpTrace.Bolts++;

				// PART SEVİYESİ LOG (watched variables)
				TraceWatchedPartBoltChange(w.Path, stage > from ? "tightened" : "loosened", from, stage);
			}
		}

		static void TraceWatchedPartBoltChange(string varPath, string action, int fromStage, int toStage) {
			if (string.IsNullOrEmpty(varPath)) return;

			// F06_PREFIX_FIX
			//
			// `varPath` nesnenin KENDI yoludur (`WatchBoltLevels` ->
			// `IdOf(go)`), yani "SATSUMA(...)/Chassis/FL/screw". `p.Id`
			// ise parca yoludur ("SATSUMA(...)/Chassis/FL"). Dogru
			// eslesme `varPath.StartsWith(p.Id + "/")`.
			//
			// One-hash KOK SEGMENTLE calismaz: kok segment
			// ("SATSUMA(557kg, 248)") hicbir zaman bir `p.Id` degildir.
			// Once yazilan `byRootId` kokusu hep bos donuyor ve
			// `[ACT] BOLT` kayitlari kesiliyordu.
			//
			// COZUM: `varPath` once TAM OLARAK denenir, sonra her
			// kesme noktasindan kisaltilarak. Deneme sayisi yol
			// derinligi kadardir (Satsuma'da tipik 3-6). Eski dongunun
			// 445 satirlik `parts` taramasi ve her iterasyondaki
			// `p.Id + "/"` string tahssisi yok.
			SatsumaPart part = null;
			if (!byId.TryGetValue(varPath, out part) || part == null) {
				part = null;
				for (int cut = varPath.Length - 1; cut > 0; cut--) {
					if (varPath[cut] != '/' && cut != 0) continue;
					if (byId.TryGetValue(varPath.Substring(0, cut), out part) && part != null) break;
					part = null;
				}
			}
if (part == null || part.Bolts.Count == 0) return;

			float now = Clock();
			string key = part.Id;
			float last;
			if (lastWatchedPartBoltLog.TryGetValue(key, out last) && now - last < 0.5f) return;
			lastWatchedPartBoltLog[key] = now;

			int tightCount = 0;
			for (int i = 0; i < part.Bolts.Count; i++) {
				if (part.Bolts[i].StageValue >= 8) tightCount++;
			}

			if (tightCount == part.Bolts.Count && part.Bolts.Count > 0) {
			}
			else if (tightCount == 0 && part.Bolts.Count > 0) {
				bool anyNonZero = false;
				for (int i = 0; i < part.Bolts.Count; i++) {
					if (part.Bolts[i].StageValue > 0) { anyNonZero = true; break; }
				}
				if (anyNonZero) {
				}
			}
		}

		/// <summary>
		/// Doğrulama turlarını tetikler. Yalnızca host DEĞİL, yani yalnızca
		/// push ALAN tarafta çalışır (karşı taraftan gelen durumla
		/// karşılaştıracak veri onda vardır).
		/// </summary>
		/// <summary>
		/// Dogrulama surucusu. SatsumaRegistry.Update() OLU KOD oldugu icin
		/// (hicbir yerden cagrilmiyor) SatsumaCoop.Tick buradan surer.
		/// Yalniz SyncDone ALAN tarafta is yapar (desiredInstalled onda dolar).
		/// </summary>
		public static void TickVerify(float now) {
			if (verifyRunning) {
				try { StepVerify(); }
				catch (Exception e) { verifyRunning = false; SmpLog.Warning("verify pass failed: " + e.Message); }
				return;
			}
			if (verifyRound >= VERIFY_ROUNDS) return;
			if (now < verifyNextAt) return;
			verifyRound++;
			verifyNextAt = now + VERIFY_DELAY * 3f;
			try { RunVerifyPass(); } catch (Exception e) {
				SmpLog.Warning("verify pass failed: " + e.Message);
			}
		}

		/// <summary>SyncDone sonrası kaç doğrulama turu yapılır.</summary>
		// Dogrulama turu 2 -> 4: civata paketleri ayni guvenilir kanaldan
		// once gitse de peer o sirada kendi vidasini sikiyor olabilir ya da
		// tekrar istisna atar; ilk 2 tur yetmezse parca gorunmez kalirdi.
		// Turlar seyrek (0.75s + 3x2.25s) ve yalniz uyusmazlik varsa calisir.
		public const int VERIFY_ROUNDS = 4;

		// Doğrulama turunun karelik ilerlemesi (bkz. StepVerify).
		static bool verifyRunning;
		static int verifyIndex;
		static int verifyFixed;
		static int verifyStillWrong;

		/// <summary>Doğrulama şu anda çalışıyor mu ('smp status' için).</summary>
		public static bool VerifyRunning { get { return verifyRunning; } }

		/// <summary>
		/// Sends pending part changes. An event wins over the bool for the same part in the
		/// same flush, because replaying the event is what makes the peer's own FSM do the
		/// work. The bool is only sent on its own, which is the wiring-part path and the
		/// recovery path when an event arrives without one.
		/// </summary>
		static void FlushParts() {
			if (!SteamTransport.HasRemote) return;

			// 1. Önce: ASSEMBLE olayı BEKLEYEN parçaların vidalarını topla ve gönder
			List<SatsumaBolt> assemblyBolts = new List<SatsumaBolt>(32);
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				if (p == null) continue;
				
				// ASSEMBLE olayı varsa (PendingEvent == "ASSEMBLE")
				if (string.Equals(p.PendingEvent, "ASSEMBLE", StringComparison.OrdinalIgnoreCase)) {
					for (int b = 0; b < p.Bolts.Count; b++) {
						SatsumaBolt bolt = p.Bolts[b];
						if (bolt != null && bolt.Dirty) {
							// F08_BOLT_CAP: sayim bayti `byte`.
							// `PatchCountByte` `Math.Min(written, 255)` ile
							// kirpiyor, ama asagida temizleme TUM listeyi
							// `Dirty = false` isaretliyor -- yani 255 ustu
							// kayit telde KAYBOLMASINA ragmen bir daha
							// ASLA toplanmiyordu. Cap burada: fazlalik
							// `Dirty` kalir, sonraki 100 ms penceresinde
							// gider.
							if (assemblyBolts.Count >= SatsumaRateLimiter.MAX_BOLTS_PER_FLUSH) break;
							assemblyBolts.Add(bolt);
						}
					}
				}
			}
			
			// Bu vidaları hemen gönder (parçadan önce)
			if (assemblyBolts.Count > 0) {
				try {
					// F08_BOLT_CAP_SEND: gonderim sonucu ONCEDEN yok sayiliyordu.
					// Basarisiz gonderimde de tum liste temizleniyordu -> o
					// vidalar bir daha ASLA toplanmiyor (kalici kayip).
					if (NetworkPacketManager.SendBoltBatch(assemblyBolts)) {
						for (int i = 0; i < assemblyBolts.Count; i++) {
						SatsumaBolt bolt = assemblyBolts[i];
						bolt.Dirty = false;
						bolt.DirtySince = float.NegativeInfinity;
						bolt.LastSentAt = Clock();
						bolt.LastSentStage = bolt.StageValue;
						bolt.LastSentTightness = bolt.TightnessValue;
						}
					}
				}
				catch (Exception e) {
					SmpLog.Error("pre-assembly bolt flush failed: " + e.Message);
				}
			}

			// 2. Sonra: Tüm parça olaylarını/durumlarını gönder (normal flush)
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				if (p == null) continue;
				
				if (string.IsNullOrEmpty(p.PendingEvent)) {
					if (!p.StateDirty) continue;
					p.StateDirty = false;
					NetworkPacketManager.SendPartState(p.Id, InstalledOf(p),
					p.IsGroupBolted(),
					p.HasSlots ? p.FilledSlotIndex() : (byte)0, p.DamagedValue);
					continue;
				}
				
				string fsmName = p.PendingFsm;
				// v17: canli yol `SatsumaCoop.FlushPartsCapped` ile ayni
				// degisikligi paylasir (bu yol `Update()` ile birlikte olu
				// kod; biri degisip digeri degismesin diye).
				string eventName = OutgoingPartEvent(p, p.PendingEvent);
				p.PendingFsm = null;
				p.PendingEvent = null;
				NetworkPacketManager.SendPartEvent(p.Id, fsmName, eventName);
			}
		}

		static void FlushBolts() {
			flushBuffer.Clear();
			BoltSync.Collect(flushBuffer, 32);
			if (flushBuffer.Count > 0) NetworkPacketManager.SendBoltBatch(flushBuffer);
		}

		// ------------------------------------------------------------------
		// Initial sync (host -> client, spread over frames)
		// ------------------------------------------------------------------

		/// <summary>
		/// Queues a full state push. Refuses to start before the world is loaded and
		/// remembers the request, so the push happens as soon as PostLoad runs.
		/// </summary>
		public static void BeginInitialSync() {
			if (!SteamTransport.IsHost) return;
			if (Root == null || parts.Count == 0 || !SteamTransport.HasRemote || SteamMPBehaviour.Instance == null) {
				syncPending = true;
				return;
			}
			if (syncRunning) return;
			// F02_SYNC_COROUTINE: yeni turun baslamadan once ESKISINI
			// DURDUR. `syncRunning = false` yetmez; Unity coroutine'i
			// yalniz `StopCoroutine` ile iptal edilir.
			StopRunningSync();
			runningSync = SteamMPBehaviour.Instance.StartCoroutine(SendAll());
		}

		/// <summary>
		/// The Steam peer just became reachable.
		///
		/// A client asks for the state here because the save may have finished loading
		/// before the connection existed: the OnWorldReady request would then have been
		/// dropped by the HasRemote check, and nothing would ever retry it.
		/// </summary>
		public static void OnPeerConnected() {
			if (SteamTransport.IsClient) {
				syncRequested = true;
				syncCompleted = false;
				verifyRound = 0;
				verifyNextAt = 0f;
				// Dünya taranmamışsa İSTEK GÖNDERME. Yoksa host tam bir
				// tur yollar (301 vida + 160 parça) ve client'ın boş
				// tablosuna yığılır — canlı ölçümde 3 turun 2'si buydu.
				// OnWorldReady zaten isteği gönderecek.
				if (!worldReady) {
					SmpLog.Debug("registry", "sync", "peer connected before our "
						+ "world was scanned; waiting for OnWorldReady to ask.");
					return;
				}
				if (syncRequestedForThisTable) {
					SmpLog.Debug("registry", "sync", "peer connected after we "
						+ "already asked for this table; not asking twice.");
					return;
				}
				syncRequestedForThisTable = true;
				NetworkPacketManager.SendSyncRequest();
				return;
			}
			// A host that already has a table can push straight away.
			BeginInitialSync();

			// Parçaların DÜNYA KONUMU ayrıca gönderilir. Bool/olay
			// senkronu "takılı mı" bilgisini taşır ama konumu taşımaz;
			// olmadan client'ta parçalar garajda kalır (kullanıcı belirtisi).
			SatsumaPartTransform.SendAll();
		}

		/// <summary>
		/// Called by ModEntry once the save is loaded.
		///
		/// A host pushes immediately. A client asks the host to push again, because the
		/// host may have already sent its state while this client still had no table: the
		/// ids resolved against an empty dictionary and were dropped. Asking is cheap and
		/// idempotent, and the host's table hash check proves the retry landed.
		/// </summary>
		public static void OnWorldReady() {
			worldReady = true;
			if (SteamTransport.IsClient) {
				syncRequested = true;
				// OnPeerConnected zaten bu tablo için istediyse (dünya
				// bağlantıdan ÖNCE yüklenmişse) tekrar gönderme: aynı
				// durumu iki kez yollamak host'ta iki tam tur demek.
				if (syncRequestedForThisTable) {
					SmpLog.Debug("registry", "sync", "world ready; SyncRequest "
						+ "already sent for this table, skipping duplicate.");
					return;
				}
				syncRequestedForThisTable = true;
				NetworkPacketManager.SendSyncRequest();
				return;
			}
			if (!syncPending) return;
			syncPending = false;
			BeginInitialSync();
		}

		/// <summary>Host side of <see cref="OnWorldReady"/>: push the whole state again.</summary>
		public static void OnSyncRequested() {
			Telemetry.ReceivedSyncRequests++;
			if (!SteamTransport.IsHost) return;
			// F02_SYNC_COROUTINE: "a retry supersedes an initial push
			// still in flight" dogruydu ama UYGULAMASI YANLISTI:
			// `syncRunning = false` coroutine'i durdurmaz, sadece bir
			// sonraki `BeginInitialSync` cagrisinin guard'ini acar --
			// boylece IKINCI `StartCoroutine(SendAll())` baslar.
			// Dogru olan: eldeki coroutine'i DURDUR, sonra yeniden baslat.
			StopRunningSync();
			BeginInitialSync();
		}

		/// <summary>
		/// F02_SYNC_COROUTINE: calisan `SendAll` coroutine'ini durdurur.
		///
		/// `SendAll` TUM tabloyu sayfalayarak gonderiyor; iki tanesi ayni
		/// anda kosarsa ayni satirlar iki kez gider. Metot yereli
		/// (`offset`, `dbOffset`) oldugu icin iki coroutine birbirinin
		/// ilerlemesini gormez.
		///
		/// `syncRunning` bayragi burada DA sifirlanir; aksi halde
		/// `BeginInitialSync` `if (syncRunning) return;` ile yeni turunu
		/// baslatmazdi.
		/// </summary>
		static void StopRunningSync() {
			if (runningSync == null) { syncRunning = false; return; }
			try {
				if (SteamMPBehaviour.Instance != null)
					SteamMPBehaviour.Instance.StopCoroutine(runningSync);
			} catch { }
			runningSync = null;
			syncRunning = false;
		}

		static IEnumerator SendAll() {
			syncRunning = true;
			float t0 = Clock();
			int sentParts = 0, sentBolts = 0;
			// F03_PAGE_TOTAL: `InstallableCount` DEGIL.
			// `WritePartBatch` `HasKnownInstallState` filtresini de
			// uyguluyor; yazilabilir satirlarin sayisi bu.
			int installableTotal = InstallableCount;
			int total = CountWritableInstallable();
			SmpLog.Log("initial sync: " + total + " installable part(s) (of "
				+ installableTotal + ") + "
				+ BoltCount + " bolt(s), " + INITIAL_PARTS_PER_FRAME + "/" + INITIAL_BOLTS_PER_FRAME + " per frame");

			// SIRA KR\u0130T\u0130K: \u00c7VATALAR \u00d6NCE, PAR\u00c7ALAR SONRA.
			//
			// Canl\u0131 kan\u0131t: "araba par\u00e7alar\u0131 vidal\u0131 de\u011fil". Assembly FSM'i
			// ASSEMBLE olay\u0131n\u0131 al\u0131rken kendi c\u0131vata kademelerine bakar ve
			// tutmuyorsa montaj\u0131 REDDEDER. Eski s\u00fcr\u00fcm par\u00e7alar\u0131 \u00f6nce
			// g\u00f6nderiyordu, yani client'in c\u0131vatalar\u0131 h\u00e2l\u00e2 s\u0131f\u0131rd\u0131 ve
			// FSM montaj\u0131 yapmadan \u00e7\u0131k\u0131yordu (bool zorla yaz\u0131lsa bile fiziksel
			// takma olay\u0131 ger\u00e7ekle\u015fmemi\u015fti). Ayn\u0131 sebeple i\u00e7 s\u0131ra:
			// c\u0131vata g\u00f6nderilmeden Assembly tetiklenmiyor.
			List<SatsumaBolt> batch = new List<SatsumaBolt>(INITIAL_BOLTS_PER_FRAME);
			int index = 0;
			while (true) {
				batch.Clear();
				// CollectAll sayfa TAM dolunca true doner; son KISMI sayfa
				// false ile doner ve eski kod onu GONDERMEDEN atardi
				// (canli kanit: 332 civatanin 324'u gitti, son 8 kayip).
				// Once gonder, sonra bitise bak.
				bool more = BoltSync.CollectAll(batch, INITIAL_BOLTS_PER_FRAME, index);
				if (batch.Count > 0) {
					try {
						NetworkPacketManager.SendBoltBatch(batch);
						sentBolts += batch.Count;
					}
					catch (Exception e) {
						SmpLog.Error("initial sync bolt batch failed: " + e.Message);
					}
					index += batch.Count;
				}
				if (!more) break;
				yield return null;
			}

			// Par\u00e7a sayfalar\u0131 i\u00e7in c\u0131vatalar\u0131 oturdu; bir kare bekle, ikisinin de
			// uygulanmas\u0131 i\u00e7in zaman tan\u0131.
			yield return null;

			//
			// v25 DUZELTME: `offset += n` YANLISTI.
			//
			// `SendPartBatch(offset, n)` `offset` kadar girdi ATLAR,
			// sonra en fazla `n` TANI yazar. Ama `WritePartBatch`
			// (NetworkPacketManager.cs:78-96) `seen` sayacini yalniz
			// `HasKnownInstallState` Gecen satirda artiriyor:
			//
			//     if (!table[i].Installable) continue;
			//     if (!HasKnownInstallState(table[i])) continue;   // <== 7 kayit burada elenir
			//     if (seen++ < startIndex) continue;
			//     if (written >= limit) break;
			//
			// CANLI KANIT: host `160 installable` buluyor ama
			// `initial sync done: 153 part(s)` yaziyor. Aradaki 7 kayit
			// hic gonderilmiyor; `offset` yine de 160'a kadar sayiyor,
			// dolayisiyla son sayfalar BOS gidiyor (`written == 0` ->
			// `SendPartBatch` paketi hic uretmiyor) ve o satirlar hic
			// bir zaman gonderilemiyor.
			//
			// DOGRU Ilerleme: yazilan kayda gore ilerle, `n` degil.
			// Yazilacak bir sey kalmadiginda dongu kendiliginden biter.
			int offset = 0;
			int guardPages = 0;
			// F03_PAGE_TOTAL: gonderilemeyen sayfa sayaci (bitince sifirlanir).
			int sendFail = 0;
			while (offset < total && guardPages++ < total + 16) {
				int n = Math.Min(INITIAL_PARTS_PER_FRAME, total - offset);
				int written = 0;
				try {
					written = NetworkPacketManager.SendPartBatch(parts, offset, n);
					sentParts += written;
				}
				catch (Exception e) {
					SmpLog.Error("initial sync part batch failed: " + e.Message);
				}
				// v25: ilerleme YAZILAN kayit sayisina gore. `written`
				// 0 ise dongu KIRILIR: sayfa bos demektir, atlanan
				// girdiler filtreliyor ve daha fazla denemek ayni
				// sonucu verir -- yalnizca kare harcar.
				if (written <= 0) {
					// F03_PAGE_TOTAL: `written == 0` artik IKI anlam tasiyor:
					//  (a) sayfa bos  -> yazilacak satir kalmadi, dogal bitis
					//  (b) gonderilemedi -> ag boslugu, sayfa KAYBOLMASIN
					// Ayirt etmek icin `SendPartBatch` gonderilemeyen
					// sayfada 0 doner ve buraya kadar gelir; o durumda
					// birkac kare bekleyip AYNI sayfayi tekrar deneriz.
					// Sonsuz dongu olmaz: `guardPages` ust sinirdir.
					int g = offset;
					if (NetworkPacketManager.RetryOffset(g, INITIAL_PARTS_PER_FRAME)) {
						sendFail++;
						if (Telemetry.Verbose) {
							SmpLog.Debug("satsuma", "sync",
								"part page at offset " + offset
								+ " not sent (attempt " + (sendFail + 1)
								+ "); retrying");
						}
						if (sendFail > 12) {
							SmpLog.Warning("initial sync: part page at offset "
								+ offset + " failed " + sendFail
								+ " time(s); giving up (channel is down).");
							break;
						}
						yield return null;
						continue;
					}
					if (Telemetry.Verbose) {
						SmpLog.Debug("satsuma", "sync",
							"part page at offset " + offset + " wrote 0; stopping");
					}
					break;
				}
				offset += written;
				sendFail = 0;
				yield return null;
			}
			if (offset < total) {
				// v25: sessizce kesmek yaniltici. Kac kayit gitmedi ve
				// NEDEN (filtre) log'a yazilir.
				// F03_PAGE_TOTAL: `total` artik YAZILABILIR satir sayisi.
				// Aradaki fark = `Installable` ama durumu OKUNAMAZ satir;
				// bu satirlar `WritePartBatch`'in `HasKnownInstallState`
				// filtresinde eleniyor ve gonderilmiyorlar. Bilerek
				// gonderilmiyorlar: `false` gondermek "bu parca yok"
				// demek olurdu ve karsi tarafta takili parca KAYBOLURDU.
				if (total < installableTotal) {
					SmpLog.Warning("initial sync: part paging stopped at " + offset
						+ "/" + total + " -- " + (installableTotal - total)
						+ " installable row(s) have no readable state"
						+ " (HasKnownInstallState=false; deliberately NOT sent).");
				}
			}

			// ------------------------------------------------------------------
			// NON-SATSUMA (STORE / BOXED) PARTS -- SECOND, CONSERVATIVE PATH.
			// ------------------------------------------------------------------
			// The Satsuma pages above are finished; the store table goes out
			// now with the SAME paging (one page per frame, PatchCountByte in
			// NetworkPacketManager.SendStorePartBatch).
			//
			// `total` and `sentParts` ARE DELIBERATELY UNTOUCHED. SyncDone
			// carries them and ApplySyncDone compares partCount against
			// InstallableCount; a store row counted there would raise a false
			// "tables differ" verdict on every join.
			int storeTotal = StorePartCount;
			if (storeTotal > 0) {
				SmpLog.Log("initial sync: " + storeTotal + " non-Satsuma part(s) "
					+ "in " + STORE_PARTS_PER_FRAME + "/frame pages");
				int storeOffset = 0;
				int sentStore = 0;
				while (storeOffset < storeTotal) {
					int n = Math.Min(STORE_PARTS_PER_FRAME, storeTotal - storeOffset);
					try {
						sentStore += NetworkPacketManager.SendStorePartBatch(parts, storeOffset, n);
					}
					catch (Exception e) {
						SmpLog.Error("initial sync store part batch failed: " + e.Message);
					}
					// Always advance: a failed batch must not spin this coroutine forever.
					storeOffset += n;
					yield return null;
				}
				SmpLog.Log("initial sync: non-Satsuma page(s) done, " + sentStore
					+ " record(s) sent.");
			}

			// ------------------------------------------------------------------
			// DATABASE/* PART-FLAG ROWS -- THIRD PATH (v20).
			// ------------------------------------------------------------------
			// NEDEN AYRI BIR KANAL:
			//
			// `REPAIRSHOP/Jobs/EngineFix|Work` `Damaged` YAZIYOR
			// (`db_Block, db_Crankshaft, db_Headgasket, db_Piston1..4, ...`)
			// ama okuyan taraf AYNI ISIMLERI KULLANMIYOR: Satsuma
			// simülasyonu `db_Required5, db_Required7, db_Required8,
			// db_Required1, db_Required3, db_Required4, db_Required11`
			// okuyor. İki tarafın kesişimi YALNIZCA `db_Alternator`.
			// Dokum değişken DEĞERLERİNİ taşımadığı için
			// `db_Required5`'in hangi `Database` satırını gösterdiği
			// çözülemez -- isimle eşleştirme yapmak yanlış parçaya hasar
			// yazardı.
			//
			// ÇÖZÜM: satırın KENDİ yolunu (`Database/DatabaseMechanics/
			// Radiator`) kimlik olarak gönderiyoruz. Eşleştirme gerekmiyor.
			//
			// 232 `Data` FSM'inden 113'ü en az bir bayrak bildiriyor
			// (45 üçü birden, 59 ikisi, 9 tek) -> tablo `BuildDatabaseFlagPartTable`
			// ile kurulur ve `Clear()` ile temizlenir.
			//
			// `total`/`sentParts` yine BILINCLI OLARAK dokunulmaz (SyncDone).
			int dbTotal = DatabaseFlagPartCount;
			if (dbTotal > 0) {
				SmpLog.Log("initial sync: " + dbTotal + " Database/* flag row(s) "
					+ "in " + DATABASE_PARTS_PER_FRAME + "/frame pages");
				int dbOffset = 0;
				int sentDb = 0;
				while (dbOffset < dbTotal) {
					int n = Math.Min(DATABASE_PARTS_PER_FRAME, dbTotal - dbOffset);
					try {
						sentDb += NetworkPacketManager.SendDatabaseBatch(
							databaseFlagParts, dbOffset, n);
					}
					catch (Exception e) {
						SmpLog.Error("initial sync database batch failed: " + e.Message);
					}
					// Always advance: a failed batch must not spin this coroutine forever.
					dbOffset += n;
					yield return null;
				}
				SmpLog.Log("initial sync: Database/* page(s) done, " + sentDb
					+ " record(s) sent.");
			}

			NetworkPacketManager.SendSyncDone(sentParts, BoltCount, tableHash);
			// F02_SYNC_COROUTINE: bu turun bitti; tutamagi birak.
			// (Kendi kendini durdurmaya calisip bir sonraki turu
			//  oldurmemek icin tutamak ONCE temizlenir.)
			runningSync = null;
			syncRunning = false;
			syncCompleted = true;
			syncDoneAt = Clock();
			SmpLog.Log("initial sync done: " + sentParts + " part(s), " + sentBolts
				+ " bolt(s) in " + ((Clock() - t0) * 1000f).ToString("F0") + " ms");
		}

		// ------------------------------------------------------------------
		// Incoming
		// ------------------------------------------------------------------

		/// <summary>Replay a live event (ASSEMBLE / REMOVE) on the peer's part.</summary>
		public static void ApplyPartEvent(string id, string fsmName, string eventName) {
			SatsumaPart part;
			if (!TryResolvePublic(id, out part) || part == null) return;

			// Verify kapsami: canli olayla gelen montaj da kaydedilir, yoksa
			// sonraki verify turu YENIDEN TAKILAN parcayi eski `desired` ile
			// SOKUP atardi.
			// v17: `PARTn` de montaj yonudur (variant secimi), REMOVE
			// DEGILDIR. Yanlis `false` yazmak `desiredInstalled`'i bozar
			// ve verify turu parcayi sokmeye calisir.
			try {
				bool evInstalled = IsSlotEvent(eventName)
					|| string.Equals(eventName, "ASSEMBLE",
						StringComparison.OrdinalIgnoreCase);
				if (desiredInstalled.Count < 1024) desiredInstalled[id] = evInstalled;
			} catch { }
			
			// --- GÜVENLİK KONTROLÜ (TEK SEFER) ---
			//
			// SatsumaCoop.ApplyPartEvent de aynı kontrolü yapıyordu. İkinci
			// çağrı CheckCooldown'a takılıp eylemi sessizce düşürüyordu, yani
			// gelen her montaj olayı iptal ediliyordu. Buraya bir kez.
			// ValidatePartState: kayıttan gelen ve zaten aynı olan durum için
			// mesafe kontrolü yapılmaz (bkz. AuthorityManager).
			// v17: `PARTn` de `isAssemble` sayilir. Aksi halde yetki/
			// cooldown kapisi (`ValidatePartEventReplay`) BYPASS edilir ve
			// yeni bir sinirsiz gonderim yolu acilmis olur.
			bool isAssemble = IsSlotEvent(eventName)
				|| string.Equals(eventName, "ASSEMBLE", StringComparison.OrdinalIgnoreCase);
			bool isRemove = string.Equals(eventName, "REMOVE", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(eventName, "DISASSEMBLE", StringComparison.OrdinalIgnoreCase);
			var authority = InteractionManager.Authority;
			if ((isAssemble || isRemove) && authority != null && !authority.ValidatePartEventReplay(id, isAssemble)) {
				if (Telemetry.Verbose) SmpLog.Debug("registry", "authority", "Part event rejected for " + id + " (" + eventName + ")");
				return;
			}
			// ----------------------------------

			PlayMakerFSM target = FsmForEvent(part, fsmName, eventName);
			if (target == null) {
				SmpLog.Warning("'" + id + "' has no FSM for event '" + eventName + "'.");
				return;
			}
			// v17: `PARTn` KIRILMAZ -- bildirilmis oldugu icin aynen
			// gider ve karsi tarafinin "find correct part N" zinciri
			// calisir. BildirilMEMIS bir `PARTn` ise diger
			// bildirilmemis olaylar gibi dusurulur (null + uyari); HICBIR
			// BASKA OLAYA CEVIRILMEZ.
			if (IsSlotEvent(eventName) && !Declares(target, eventName)) {
				PartEventsNotDeclared++;
				if (Telemetry.Verbose)
					SmpLog.Debug("registry", "part", "'" + id + "' '" + eventName
						+ "' bildirilmiyor; gonderilmedi.");
				if (warnedNoEvent.Add(id + "|" + eventName))
					SmpLog.Warning("'" + id + "': '" + eventName
						+ "' gonderilemedi; hedef FSM bu olayi bildirmiyor.");
				return;
			}
			SuppressScope scope = new SuppressScope();
			try { target.SendEvent(eventName); }
			catch (Exception e) { SmpLog.Warning("'" + id + "' '" + eventName + "' failed: " + e.Message); }
			finally { scope.Dispose(); }
			Telemetry.AppliedPartEvents++;
		}

		/// <summary>
		/// Converge a part's installed state. The event does the visual work, then the bool is
		/// forced.
		///
		/// The forced write is not redundant: the event runs the real Assembly FSM, which
		/// refuses to install while this peer's own bolt stages disagree with the sender's.
		/// Since bolt packets may not have arrived yet (or the peer is mid-tightening the same
		/// part), the bool is the authoritative save state and has to land regardless.
		/// </summary>
		/// <summary>
		/// v15: grup "sıkı kaldı mı" bayrağıyla birlikte uygular.
		///
		/// NEDEN GEREKLİ: `bolted` daha önce YALNIZCA vida kaydının bir
		/// alanı olarak taşınıyordu (`WriteBolt`) ve uygulaması
		/// `ApplyBolt` içindeydi. Canlı döküm kanıtı: Satsuma altındaki
		/// 50 `BoltCheck` nesnesinin **38'inin** altında `Screw` FSM'i
		/// YOK — yani o parçalar için hiç vida kaydı üretilmiyor ve grup
		/// bayrağı hiçbir kanaldan geçmiyordu.
		///
		/// BU ÖNEMLİ: trigger'ın Assembly FSM'i montajı kabul edip
		/// etmediğini o bağlantıya bakar —
		///   trigger_shock_fl[Assembly], "Check bolts":
		///     GetFsmBool, BoolTest, db_PartRequired1, Data, Bolted
		/// Karşı tarafta `Data/Bolted` bayat kalırsa parça "takılı ama
		/// sıkmamış" görünür ve ikinci bir montaj turu tetiklenir.
		/// </summary>
		/// <summary>
		/// v20: `Database/*` part-flag kaydi uygula.
		///
		/// NEDEN AYRI YOL: `ApplyPartState`in 2 argumanli hali montaj
		/// semasidir ve `:2828` sezgisel korumasiyla gelir:
		///   if (!installed &amp;&amp; part.InstalledVars.Count == 0
		///       &amp;&amp; part.Bolts.Count == 0) return;
		/// Bir `Database/*` satiri SADECE `Data` FSM'e sahiptir (dokum:
		/// 232 satirin hicbirinde Assembly/Removal/BoltCheck yok), dolayisiyla
		/// `InstalledVars` bos ve `Bolts` bos. `installed == false` olan
		/// saglam bir parcada guard devreye girip KAYDI TUMDEN DUSURURDU --
		/// yani "bu parca saglam" bilgisi hic gonderilmez ve karsi taraf
		/// hasarli bir parcayi saglam sanabilir (motor calisirken cokertir).
		///
		/// Bu yol:
		///   - `InstalledOf` sezgisine DOKUNMAZ (Database satiri sezgisel
		///     okunamaz; `InstalledOf` FSM durum adindan tahmin eder),
		///   - yalniz bagli olan bayraklari yazar,
		///   - yok sayilmayan bir kaydi sessizce gecirmez: sayac artar.
		/// </summary>
		public static void ApplyDatabasePartState(string id, bool installed, bool bolted, bool damaged) {
			try {
				Telemetry.PartRecords++;
				SatsumaPart part;
				if (!TryResolvePublic(id, out part) || part == null) {
					if (TableNotReady) {
						Telemetry.PartNotReady++;
						if (SteamTransport.IsClient) syncRetryNeeded = true;
					}
					else Telemetry.PartMissing++;
					return;
				}
				byte bits = part.DbFlagBits;
				if (bits == 0) { DatabaseFlagsSkipped++; return; }

				// `installed`: Database satiri SADECE `Data` FSM'e sahip
				// oldugu icin `Installed` bayragi `BindDatabaseFlag`
				// yerine `Attach(part, fsm, "Data")` tarafindan
				// `InstalledVars`'a eklenir (Attach, BoltCheck disi her
				// fsmName icin `Installed` arar). Bu yuzden burada
				// `InstalledVars[0]` dogru hedef.
				//
				// ONCEKI SURUM BU ALANI ATIYORDU; yalniz `bolted` ve
				// `damaged` yaziliyordu. Dokum: 232 Data FSM'inin
				// 205'i en az bir bayrak bildiriyor ve cogu ucunu birden
				// -- `Installed` en yaygin olan. Atlaninca hasar dogru
				// giderken montaj durumu eski kalirdi.
				if ((bits & SatsumaPart.DBFLAG_INSTALLED) != 0
					&& part.InstalledVars.Count > 0) {
					try {
						FsmBool iv = part.InstalledVars[0];
						if (iv != null && iv.Value != installed) iv.Value = installed;
					}
					catch { }
				}

				// `bolted`: YALNIZ `GroupBolted` GERCEKTEN bagliysa.
				// `IsGroupBolted()` vida yokken SABIT true doner; bir
				// Database satiri icin bu "hepsi sikildi" UYDURMASI olur.
				if ((bits & SatsumaPart.DBFLAG_BOLTED) != 0 && part.GroupBolted != null) {
					if (part.GroupBolted.Value != bolted) part.GroupBolted.Value = bolted;
				}
				// `damaged`: `DbDamaged` yalniz `Data/Damaged` bildiren
				// satirlarda baglanir (BindDatabaseFlag).
				if ((bits & SatsumaPart.DBFLAG_DAMAGED) != 0 && part.DbDamaged != null) {
					if (part.DbDamaged.Value != damaged) part.DbDamaged.Value = damaged;
				}
				Telemetry.AppliedPartStates++;
				Telemetry.PartApplied++;
				Telemetry.RemotePartActions++;
			}
			catch (Exception e) {
				Telemetry.PartFailed++;
				SmpLog.Warning("database state '" + id + "' failed: " + e.Message);
			}
		}

		public static void ApplyPartState(string id, bool installed, bool bolted, byte slot, bool damaged) {
			ApplyPartState(id, installed);
			try {
				SatsumaPart part;
				if (!TryResolvePublic(id, out part) || part == null) return;
				if (part.GroupBolted != null && part.GroupBolted.Value != bolted)
					part.GroupBolted.Value = bolted;
				ApplySlot(part, installed, slot);
				ApplyDamage(part, damaged);
			}
			catch { }
		}

		/// <summary>
		/// v16 (b): yuvayi zorla.
		///
		/// NEDEN: 6 trigger tek yuva degil. Dokum kaniti:
		///   MiscParts/trigger_radiator   PART1,PART2  db_Radiator1,db_Radiator2
		///   Body/trigger_grille          PART1..7     db_Part1,db_Part2
		///   .../trigger_steering_wheel   PART1..5     db_WheelGT,Rally,Sport,Stock
		/// `PARTn` GLOBAL GECIS olaylari (trigger_radiator dizisinde
		/// 226/227; `DISASSEMBLE`/`LOOP` yaninda) ve yuvalar `db_*`
		/// referanslariyla eslesiyor. Yani "hangi parca takili" bilgisi
		/// `Installed` bool'unda TEK BASINA degil.
		///
		/// DOGRULAMA YAPMADAN YAZMIYORUZ: gelen `slot` degeri bu
		/// makinedeki cozumlenmis yuva sayisinin disindaysa UYGULANMAZ
		/// (tahlis taban farki = eski build, ya da farkli tablo). Yanlis
		/// yuvaya yazmak, hic yazmamaktan kotudur.
		/// </summary>
		static void ApplySlot(SatsumaPart part, bool installed, byte slot) {
			if (part == null || !part.HasSlots) return;   // tek yuvali: zaten yazildi
			int n = part.SlotFlags.Count;
			if (slot == 0) {
				// "Hicbiri dolu degil" -- tumunu kapat.
				for (int i = 0; i < n; i++) {
					try {
						FsmBool v = part.SlotFlags[i];
						if (v != null && v.Value) v.Value = false;
					}
					catch { }
				}
				if (installed && n > 0) SlotMismatches++;
				return;
			}
			if (slot > n) { SlotMismatches++; return; }   // karsi tarafta daha fazla yuva
			for (int i = 0; i < n; i++) {
				try {
					FsmBool v = part.SlotFlags[i];
					if (v == null) continue;
					bool want = ((byte)(i + 1) == slot);
					if (v.Value != want) v.Value = want;
				}
				catch { }
			}
		}

		public static void ApplyPartState(string id, bool installed) {
			Telemetry.PartRecords++;
			SatsumaPart part;
			if (!TryResolvePublic(id, out part) || part == null) {
				if (TableNotReady) {
					Telemetry.PartNotReady++;
					if (SteamTransport.IsClient) syncRetryNeeded = true;
				}
				else Telemetry.PartMissing++;
				return;
			}
			// SEZGISEL PARCA KORUMASI (Paket D): civatasi ve Installed
			// bool'u olmayan girdide InstalledOf FSM durus sezgisidir.
			// Ilk-senkron/JIP tablosundaki "takili degil" yuzunden peer'in
			// takili camurluk/egzozunu sokmek yasaktir (baglanti sonrasi
			// kopme). Canli ASSEMBLE/REMOVE/DISASSEMBLE olaylari
			// (ApplyPartEvent) aynen uygulanir; yalniz tablo kopyasi
			// sokumu engellenir.
			if (!installed && part.InstalledVars.Count == 0 && part.Bolts.Count == 0) {
				if (Telemetry.Verbose)
					SmpLog.Debug("satsuma", "part", "heuristic '" + id + "' keep (no auto-remove).");
				return;
			}
			
			// --- GÜVENLİK KONTROLÜ (TEK SEFER) ---
			// SatsumaCoop.ApplyPartState de kontrol ediyordu; ikinci çağrı
			// CheckCooldown'a takılıp durumu düşürüyordu (bkz. yukarıdaki not).
			//
			// BURADA MESAFE ÖLÇÜLMEZ. Canlı log (06.10 14:25:23) ~100 parçayı
			// "distance 11-14m > 5m" diye reddetti: iki taraf farklı kayıtlardan
			// geldiği için değerler gerçekten farklıydı, yani reddedilen ortak
			// kaydın kendisiydi. Msg.PartState/PartBatch bir EYLEM değil, ortak
			// kaydın uzaktan kopyasıdır. Canlı eylemler (Msg.PartEvent ve
			// vida sıkma) mesafe kalkıkanıyla korunmaya devam eder.
			// Ayni degerin tekrari cooldown'a girer; DEGISEN deger her
			// zaman uygulanir (KANIT: client funnel `yetki-reddi 149` --
			// katilma patlamasinda tekrarlar ayiklanmali ama gercek degisim
			// dusmemeli).
			bool prevKnown = false;
			bool prevWant = false;
			try { prevKnown = desiredInstalled.TryGetValue(id, out prevWant); } catch { }
			var authority = InteractionManager.Authority;
			if (prevKnown && prevWant == installed) {
				if (authority != null && !authority.ValidateConvergence(id)) {
					Telemetry.PartAuthorityRejected++;
					if (Telemetry.Verbose) SmpLog.Debug("registry", "authority", "Part state dropped (spam guard) for " + id);
					return;
				}
			}
			// ----------------------------------

			// Karşı tarafın istediği durum saklanır: SyncDone sonrası
			// doğrulama geçişi bununla karşılaştırır (aşağıda).
			if (desiredInstalled.Count < 1024) desiredInstalled[id] = installed;
			// Degisen degerin damgasi: sonraki AYNI deger tekrarlari
			// cooldown'a girsin. Damgasiz birakilirsa ilk tekrarda zaman
			// asimi bos gorunur ve spam gecer.
			if (!prevKnown || prevWant != installed) {
				try { if (authority != null) authority.ValidateConvergence(id); } catch { }
			}
			string eventName = installed ? "ASSEMBLE" : "REMOVE";
			PlayMakerFSM target = FsmForEvent(part, null, eventName);
			// ------------------------------------------------------------------
			// İKİ AYRI TRY — CANLI HATA DÜZELTMESİ
			// ------------------------------------------------------------------
			// Canlı ölçüm: 8 parça hiç uygulanamıyordu (trigger_sparkplug1..4,
			// trigger_alternator belt, trigger_oil filter, headlight
			// left/right trigger_bulb1/2) ve hepsi aynı hatayı veriyordu:
			//   "Object reference not set to an instance of an object"
			//
			// SEBEP: SendEvent ile durum yazımı AYNI try bloğundaydı.
			// Oyunun kendi FSM'i istisna attığında blok YARIDA kesiliyor
			// ve InstalledVars hiç yazılmıyordu. Sonuç: parça kayıtta
			// takılı kalmıyor; üstelik hata "tam uygulanamadı" diye
			// sayılıyordu ve gerçek kayıp sayıları tutmuyordu.
			//
			// SendEvent SENKRON çalışır ve istisna dışarı taşar.
			//
			// DÜZELTME: iki ayrı try. FSM olayı atsa bile kayıt durumu
			// yazılır — görsel eksik kalır ama kayıt doğru olur, yani
			// bir sonraki kayıt yüklemesinde doğru duruma gelir.
			// ------------------------------------------------------------------
			SuppressScope scope = new SuppressScope();
			bool wroteState = false;
			// Durum yazimi gercekten istisna atti mi? Asagidaki "yazilabilir
			// bool yok" sayacinin ayni kaydi IKIZCE saymamasi icin gerekli.
			bool stateWriteThrew = false;
			try {
				try {
					// v15: bildirilmeyen olay gonderme; ayni islevi tasiyan
					// adi kullan (REMOVE -> DISASSEMBLE, bkz. EffectiveEvent).
					if (target != null) {
						string ev = EffectiveEvent(target, eventName);
						if (ev == null) {
							// Anlam koruyan HICBIR olay yok. Gondermek yanlis
							// olurdu (ASSEMBLE istiyoruz, DISASSEMBLE gondermek
							// parcayi sokardi), bosuna gondermek de anlamsiz.
							// Telemetry.PartEventFailed: FSM olayi KISALDI.
							// Kayit durumu asagida yine yazildigi icin bu
							// "dustu" sayilmaz (ayni PartEventFailed sozu).
							Telemetry.PartEventFailed++;
							PartEventsNotDeclared++;
							if (Telemetry.Verbose)
								SmpLog.Debug("registry", "part", "'" + id + "' '" + eventName
									+ "' bildirilmiyor; ayni islevli ad yok, gonderilmedi.");
							if (warnedNoEvent.Add(id + "|" + eventName))
								SmpLog.Warning("'" + id + "': '" + eventName
									+ "' gonderilemedi; hedef FSM bu olayi bildirmiyor.");
						}
						else {
							if (Telemetry.Verbose && !string.Equals(ev, eventName, StringComparison.Ordinal))
								SmpLog.Debug("registry", "part", "'" + id + "' '" + eventName
									+ "' bildirilmiyor; '" + ev + "' kullanildi.");
							target.SendEvent(ev);
						}
					}
				}
				catch (Exception fe) {
					// Oyunun FSM'i patladı. Bu BİZİM hatamız değil; ama
					// görsel montaj yapılmamış olabilir.
					Telemetry.PartEventFailed++;
					if (Telemetry.Verbose)
						SmpLog.Debug("registry", "part", "'" + id + "' "
							+ eventName + " threw in the game's FSM: " + fe.Message);
				}
				// ------------------------------------------------------------------
				// DURUM YAZIMI -- OKUMA TARAFIYLA AYNI BAYRAGA.
				//
				// `InstalledOf` (okuma) onceligi:
				//   1) InstalledVars doluysa  -> p.Installed (kendi gercek bool'u)
				//   2) InstalledVars BOSS ise  -> DatabaseInstalledFlag(p)
				//      (`db_*` -> Database/*/Data/Installed)
				//   3) hicbiri yoksa          -> sezgisel (Removal/Assembly durum adi)
				//
				// v15 HATA: yazma tarafi yalniz (1)'i yaziyordu. Satsuma'daki
				// 102 `Assembly` tetikleyicisinin 83'u kendi `Installed`
				// bool'unu bildirmiyor; oyunda o bayrak, tetikleyicinin
				// `db_*` FsmGameObject degiskeninin tutugu Database nesnesinde
				// durur. Yani bu 83 parca icin yazma HICBIR SEY yapmiyordu,
				// ama `wroteState` yine true idi -> "uygulandi" deniyordu.
				//
				// DUZELTME: yazma da ayni onceligi kullanir. InstalledVars
				// bossa `DatabaseInstalledFlag(part)` ile -- InstalledOf ile
				// BIRE BIR AYNI metot -- ayni FsmBool cozulup yazilir.
				// Cozum telifs (`DbInstalledChecked`/`DbInstalled`) oldugu
				// icin okuma ve yazma her zaman ayni bayragi gosterir.
				// ------------------------------------------------------------------
				try {
					// wroteAny = "en az bir bool YAZILDI" (yazilabilir ve su
					// an istenen degeri tasiyor). Bos InstalledVars'ta bu
					// KESINLIKLE false kalir; boylece `wroteState` artik
					// "hicbir sey yazmadan basarili" diyemez.
					bool wroteAny = false;
					for (int i = 0; i < part.InstalledVars.Count; i++) {
						FsmBool v = part.InstalledVars[i];
						if (v == null) continue;
						if (v.Value != installed) v.Value = installed;
						wroteAny = true;
					}
					// Okuma tarafiyla ayni oncelik: yalniz InstalledVars
					// bossa Database/* bayragina bakilir.
					if (!wroteAny && part.InstalledVars.Count == 0) {
						FsmBool dbFlag = DatabaseInstalledFlag(part);
						if (dbFlag != null) {
							if (dbFlag.Value != installed) dbFlag.Value = installed;
							wroteAny = true;
						}
					}
					wroteState = wroteAny;
				}
				catch (Exception ve) {
					stateWriteThrew = true;
					Telemetry.PartFailed++;
					SmpLog.Warning("'" + id + "' state write failed: " + ve.Message);
				}
				// HICBIR yazilabilir bool yoksa bu kaydin durumu hedefte
				// GONDERILENDEN FARKLI kalir; "uygulandi" DENMEZ. Varis
				// sinyali gonderilmistir, yazilacak bir bayrak yoktur:
				// bu bir kayiptir, PartFailed kovasinda sayilir
				// (PartRecords = PartApplied + dustu dengesi bozulmaz) ve
				// ayrica `PartsNoWritableFlag` ile ayirt edilir. Ilk 12
				// kayit uyarilir; gerisi sessizce sayilir.
				if (!stateWriteThrew && !wroteState) {
					Telemetry.PartFailed++;
					PartsNoWritableFlag++;
					if (PartsNoWritableFlag <= 12)
						SmpLog.Warning("'" + id + "' has no writable Installed flag "
							+ "(InstalledVars empty, db_* unresolved) - state NOT applied.");
				}
			}
			catch (Exception e) {
				Telemetry.PartFailed++;
				SmpLog.Warning("'" + id + "' state failed: " + e.Message);
			}
			finally { scope.Dispose(); }
			Telemetry.AppliedPartStates++;
			if (wroteState) Telemetry.PartApplied++;
			Telemetry.RemotePartActions++;
			if (Telemetry.Verbose)
				SmpLog.Print("[ACT-REMOTE] PART " + id + "  "
					+ (installed ? "ASSEMBLE (takili)" : "REMOVE (sokuldu)")
					+ "   <- karsi taraftan");
		}

		/// <summary>Apply a batch of part states.</summary>
		public static void ApplyPartBatch(BinaryReader r, byte count) {
			for (int i = 0; i < count; i++) {
				string id = r.ReadString();
				bool installed = r.ReadBoolean();
				// v15: kayitta `bolted` de var. Geriye donuk uyum: eski
				// build'da akis burada bitmis olur, o durumda bayragi
				// dokunmadan yoksay ve cik.
				bool bolted = true;
				try {
					if (r.BaseStream.Position < r.BaseStream.Length) bolted = r.ReadBoolean();
				}
				catch (System.IO.EndOfStreamException) { bolted = true; }
				// v16 (b): yuva indeksi. Geriye donuk: akis bitmisse 0
				// (tek yuvali / yuva yok) kabul edilir.
				byte slot = 0;
				try {
					if (r.BaseStream.Position < r.BaseStream.Length) slot = r.ReadByte();
				}
				catch (System.IO.EndOfStreamException) { slot = 0; }
				// v18 (a): hasar.
				bool dmg = false;
				try {
					if (r.BaseStream.Position < r.BaseStream.Length) dmg = r.ReadBoolean();
				}
				catch (System.IO.EndOfStreamException) { dmg = false; }
				ApplyPartState(id, installed, bolted, slot, dmg);
			}
			Telemetry.AppliedBatches++;
		}

		/// <summary>Apply a batch of bolt values.</summary>
		public static void ApplyBoltBatch(BinaryReader r, byte count) {
			long remaining = r.BaseStream.Length - r.BaseStream.Position;
			for (int i = 0; i < count; i++) {
				// A record without the path is 4 + 1 + 4 + 1 + 1 bytes plus the two
				// strings, so the smallest possible record is 11 bytes. Anything
				// shorter means the stream is already desynchronised and reading on
				// would throw on a random string length.
				if (r.BaseStream.Position >= r.BaseStream.Length) {
					SmpLog.Warning("bolt batch ended early at record " + i + "/" + count
						+ " (" + remaining + " byte(s) left).");
					break;
				}
				string id = r.ReadString();
				Telemetry.BoltRecords++;
				byte index = r.ReadByte();
				float tightness = r.ReadSingle();
				int stage = (sbyte)r.ReadByte();
				bool bolted = r.ReadBoolean();
				// The path is the last field, so it is read defensively: a peer on
				// the previous build stops writing right before it.
				string path = null;
				try {
					if (r.BaseStream.Position < r.BaseStream.Length) path = r.ReadString();
				}
				catch (System.IO.EndOfStreamException) { path = null; }
				ApplyBolt(id, index, tightness, stage, bolted, path);
			}
			Telemetry.AppliedBatches++;
		}

		/// <summary>
		/// One remote bolt value.
		///
		/// Resolution order is path first, index second. The path exists because the
		/// two sides were on different saves (live log: host 378 parts / 337 bolts,
		/// peer 369 / 332). With different bolt counts per part the index shifts and
		/// the value lands on the wrong bolt, which reads as "the parts' bolts come
		/// loose". An unknown path falls back to the index so a peer on the
		/// previous build still works, and an out of range index is dropped rather
		/// than clamped: clamping would silently tighten the wrong bolt.
		/// </summary>
		public static void ApplyBolt(string id, byte index, float tightness, int stage, bool bolted) {
			ApplyBolt(id, index, tightness, stage, bolted, null);
		}

		public static void ApplyBolt(string id, byte index, float tightness, int stage, bool bolted, string boltPath) {
			// GELEN KADEME SAĞLAM MI?
			//
			// CANLI İZ (11.10-12.10 logları):
			//   [ACT-REMOTE] BOLT SATSUMA(...)/door left(Clone)#0
			//     tightened -1 -> 8 (hedef), uzlastırmaya alındı
			//     loosened 8 -> 8 (hedef)
			// Kademe -1 GELİYORDU. -1 bu projede "uzlaştırma yok" anlamına
			// giren bir GÖNDERGE değeri (SatsumaBolt.PendingStage = -1), bir
			// kademe değil. Eski kod onu olduğu gibi yazıyordu: `Stage.Value`
			// -1 oluyor, `HasEvent(Screw, "TIGHTEN")` kararı ters davranıyor ve
			// [ACT] satırı "tightened -1 -> 8" gibi okunamaz bir şey basıyor.
			//
			// Ayrıca kademe 0..8 aralığının dışındaysa (boltsz veya bozuk paket)
			// tutarlı bir anlamı yoktur: kabul etmeyiz.
			if (stage < 0 || stage > 8) {
				Telemetry.BoltStageOOR++;
				if (Telemetry.Verbose)
					SmpLog.Debug("registry", "bolt", "geçersiz kademe reddedildi: "
						+ id + " stage=" + stage);
				return;
			}

			// UYGULAMA HUNISI: her cikis kendi kovasina yazilir, hicbiri
			// sayacsiz kalmasin. Once sayacsizdi: "paket geldi ama
			// senkron yok" durumunda NEDEN bilinmiyordu.
			SatsumaPart part;
			if (!TryResolvePublic(id, out part) || part == null) {
				// Tablo hazır değilse bu KAYIP DEĞİL, zamanlama.
				if (TableNotReady) {
					Telemetry.BoltNotReady++;
					if (SteamTransport.IsClient) syncRetryNeeded = true;
				}
				else Telemetry.BoltPartMissing++;
				return;
			}
			if (part.Bolts.Count == 0) {
				Telemetry.BoltNoBolts++;
				return;
			}

			// Cıvata ÖNCE çözülür: güvenlik kontrolü "zaten aynı değerde mi"
			// karşılaştırması yapabilmek için hedefi bilmeli. Kontrolün ikinci
			// kez çağrılması da bu yüzden kalktı (aşağıdaki not).
			SatsumaBolt bolt = null;
			if (!string.IsNullOrEmpty(boltPath)) {
				for (int i = 0; i < part.Bolts.Count; i++) {
					if (string.Equals(part.Bolts[i].Path, boltPath, StringComparison.Ordinal)) {
						bolt = part.Bolts[i];
						break;
					}
				}
			}
			bool byPath = bolt != null;
			if (!byPath) {
				if (index >= part.Bolts.Count) {
					Telemetry.MismatchedBolts++;
					Telemetry.BoltIndexOOR++;
					return;
				}
				bolt = part.Bolts[index];
			}

			// --- GÜVENLİK KONTROLÜ (TEK SEFER) ---
			//
			// DİKKAT: bu satır ikinci kez çalıştırılmamalı. SatsumaCoop
			// ApplyBoltBatch zaten ValidateBoltAction çağırıyordu ve
			// AuthorityManager.CheckCooldown aynı eylem anahtarını 0.15 sn
			// boyunca ikinci kez reddediyordu: yani GELEN HER CIVATA sessizce
			// düşüyordu. Canlı log, montajın neden hiç görünmediğini açıklıyor.
			string fullBoltPath = !string.IsNullOrEmpty(boltPath) ? boltPath : (id + "/Bolt" + index);
			var authority = InteractionManager.Authority;
			if (authority != null && !authority.ValidateBolt(fullBoltPath, bolt, stage, tightness,
					bolted ? "Tighten" : "Loosen")) {
				Telemetry.BoltAuthorityRejected++;
				if (Telemetry.Verbose) SmpLog.Debug("registry", "authority", "Bolt action rejected for " + fullBoltPath);
				return;
			}
			// ----------------------------------

			// ÖNCEKİ KADEME, DEĞERLER YAZILMADAN ÖNCE YAKALANIR:
			// aşağıda bolt.LastSentStage = stage ile EZİLİYOR. Canlı iz
			// "32 -> 0" gibi yön bilgisini bu değerden alıyor.
			int prevStage = bolt.LastSentStage;

			SuppressScope scope = new SuppressScope();
			try {
				// ÖNCE DEĞER, SONRA FSM. Sıralama önemli: Assembly FSM'i ASSEMBLE'i
				// cıvata kademelerine bakarak kabul/reddediyor, yani Stage karşı
				// tarafta hedefe gelmeden montaj olayı gönderilmemeli.
				// MESHI OYNAT: Stage dogrudan yazilirsa Screw FSM'i yurumez,
				// somun gorseli donmez ("vidasiz" sikayeti). Yurume mumkunse
				// Stage'i oyuna birak (TIGHTEN/UNTIGHTEN adimlari somunu
				// dondurur ve Stage'i kendisi yazar); degilse dogrudan yaz.
				bool walks = bolt.Screw != null && FsmNames.HasEvent(bolt.Screw,
					stage > bolt.StageValue ? "TIGHTEN" : "UNTIGHTEN");
				if (bolt.Stage != null && !walks) bolt.Stage.Value = stage;
			// v15: parca seviyesi toplam da yakinsasin. Sadece Stage
			// yazmak yetmez: karsi tarafin kendi `Screw` FSM'i yuruyemeyebilir
			// (QueueReconcile, TIGHTEN/UNTIGHTEN bildirilmemisse erken
			// cikiyor), o zaman BoltCheck/Tightness hic kipirdamaz ve
			// Data/Bolted flip olmaz -- parca "takili ama sikilmamis"
			// kalir ve ikinci bir montaj turu tetiklenir.
			// ConvergeGroupTightness ayni degerde yazmadigi icin
			// idempotent.
				if (bolt.Tightness != null) bolt.Tightness.Value = tightness;
				bolt.LastSentStage = stage;
				bolt.LastSentTightness = tightness;
				bolt.Dirty = false;
				bolt.DirtySince = float.NegativeInfinity;
				bolt.RemoteSetAt = Clock();
				// GroupTightness BURAYA YAZILMAZ. Değer partin tüm cıvatalarının
				// türevi (BoltCheck FSM'i ArrayList + FloatOperator ile hesaplıyor:
				// "pivot_door_left Tightness 31 -> 0" bir toplam sayacı). Tek bir
				// cıvatanın sıkılığını yazmak o toplamı bozuyor ve oyunun kendi
				// hesabıyla sürekli çakışıyordu. Doğru toplam, o cıvatalar
				// uzlaştırıldıktan sonra BoltCheck'in kendi işidir.
				if (part.GroupBolted != null) part.GroupBolted.Value = bolted;
				QueueReconcile(bolt, stage);
			}
			catch (Exception e) {
				// CANLI HATA: burada da Telemetry.AppliedBolts++ vardi,
				// yani PATLAYAN civata "uygulandi" diye sayiliyordu.
				// Uygulama hunisi ancak bunu duzeltince dogru calisir.
				Telemetry.BoltFailed++;
				SmpLog.Warning("bolt '" + id + "'#" + index + " failed: " + e.Message);
			}
			finally { scope.Dispose(); }
			Telemetry.AppliedBolts++;
			Telemetry.BoltApplied++;
			// CANLI KANIT: karsi taraf bize vida gonderdi. Bakacak yeri
			// bilelim (vidanin gorseli donup donmedigi bu kayittan
			// SONRA oluyor, zincir sayaclari da onu olcer).
			// SyncDone gelmeden gelenler BAŞLANGIÇ senkronudur; "canlı"
			// saymak yanıltıcı (299'un tamamı başlangıç senkronuydu).
			if (syncCompleted) Telemetry.LiveBolts++;
			else Telemetry.InitialSyncBolts++;

			TraceRemote("BOLT", id, index,
				(stage > prevStage ? "tightened " : "loosened ")
				+ prevStage + " -> " + stage + " (hedef), uzlastirmeye alindi");
			// Boltlar canlıda oyun tarafından da sıkılıkla değiştirilir; hangi
			// yoldan çözüldüğünü görmek için seyrek sayac (smp status).
			try { ConvergeGroupTightness(bolt.Part != null ? bolt.Part.GroupTightness : null); } catch { }
			if (byPath) Telemetry.BoltsByPath++;
			else Telemetry.BoltByIndex++;
		}

		// ------------------------------------------------------------------
		// Bolt reconciliation: the peer's FSM has to actually RUN
		// ------------------------------------------------------------------
		//
		// SAHNE KANITI (level2, "Screw" FSM): cıvata mesh'i oyunun kendi
		// `HutongGames.PlayMaker.Actions.SetRotation` action'ı ile, FSM'in
		// "Screw 2" / "Unscrew 2" durumlarında döndürülüyor. Bu durumlar yalnızca
		// TIGHTEN / UNTIGHTEN olaylarıyla giriliyor (aynı FSM'de ayrıca Idle,
		// Setup 2, Save data 2 durumları var).
		//
		// YANİ: Stage ve Tightness'i yazmak kayıt durumunu ve Assembly'nin
		// "cıvatalar sıkılı mı" kontrolünü düzeltir, ama mesh'i yerinde bırakır.
		// Kapılarla aynı sınıf bir sorun, bir kat daha aşağıda.
		//
		// ÇÖZÜM oyunun kendi geçişi: bir TIGHTEN ya da UNTIGHTEN Stage'i tam bir
		// adım oynatır ve FSM yolda hem Tightness'i hem rotasyonu kendisi
		// türetir. Adım sayısı 0..8 arasında ve kuyruk kendini Stage eşleştiği
		// anda boşaltır; yani kayıp ya da yeniden sıralanmış bir paket hedefi
		// aşamaz. Her iki tarafta da oyuncu aynı anda cıvata çeviyorsa son
		// yazan kazanır, sonraki paket yine uzlaştırır.
		static readonly List<SatsumaBolt> boltQueue = new List<SatsumaBolt>(32);
		static float nextReconcile;

		/// <summary>Bir karede uzlaştırılacak en fazla cıvata (donmayı önler).</summary>
		public const int RECONCILE_BUDGET = 8;

		/// <summary>İki uzlaştırma adımı arası en kısa süre (s).</summary>
		public const float RECONCILE_INTERVAL = 0.05f;

		/// <summary>Uzlaştırma kuyruğunda bekleyen cıvata sayısı ('smp status').</summary>
		public static int PendingReconcile { get { return boltQueue.Count; } }

		/// <summary>
		/// Hedefi kaydeder. FSM yoksa ya da kademe değişkeni yoksa hiçbir şey
		/// yapılmaz: yalnızca değer yazılır, ki bu durum zaten doğru durumdur.
		/// </summary>
		static void QueueReconcile(SatsumaBolt bolt, int stage) {
			if (bolt == null) return;
			if (bolt.Screw == null || bolt.Stage == null) {
				bolt.PendingStage = -1;
				Telemetry.SkipNoFsm++;
				return;
			}
			bolt.PendingStage = stage;
			bolt.ReconcileTries = 0;
			if (bolt.StageValue == stage) {
				// En sık dal: cıvata ZATEN doğru kademede.
				// Beklenen davranış; kuyruğa girmemesi doğru.
				bolt.PendingStage = -1;
				Telemetry.SkipAtTarget++;
				return;
			}

			// FSM'in TIGHTEN/UNTIGHTEN olaylarını tanımlayıp tanımlamadığını ÖNCE kontrol et
			string testEvent = stage > bolt.StageValue ? "TIGHTEN" : "UNTIGHTEN";
			if (!FsmNames.HasEvent(bolt.Screw, testEvent))
			{
				// FSM olayı tanımlamıyorsa, uzlaştırmayı atla (değerler zaten yazıldı)
				// BU DAL KÖK NEDEN OLABİLİR: değerler yazıldı ama oyunun
				// FSM'i olayı tanımıyor, yani vidanın GÖRSELİ dönmez.
				Telemetry.SkipNoEvent++;
				if (Telemetry.Verbose) SmpLog.Debug("registry", "reconcile", "Bolt FSM missing " + testEvent + " event: " + bolt.Path);
				return;
			}

			// InQueue bayrağı Contains'in O(n) taramasını gereksiz kılar: başlangıç
			// senkronu ~540 cıvatayı tek seferde kuyruğa alıyor, Contains ile bu
			// karesel olurdu.
			if (bolt.InQueue) { Telemetry.SkipAlreadyQueued++; return; }
			if (boltQueue.Count >= RECONCILE_QUEUE_LIMIT) {
				Telemetry.SkipQueueFull++;
				return;
			}
			bolt.InQueue = true;
			Telemetry.Queued++;
			boltQueue.Add(bolt);
		}

		/// <summary>
		/// Kuyruk üst sınırı. Sahne genişliğine göre: 541 cıvata. Bellek önemsiz
		/// (yalnızca referans), kare süresi ise RECONCILE_BUDGET ile sınırlı.
		/// </summary>
		public const int RECONCILE_QUEUE_LIMIT = 1024;

		/// <summary>
		/// Bir cıvataya verilecek en fazla uzlaştırma denemesi.
		///
		/// Kuyruğun sonsuza kadar dolu kalmasını engeller. TIGHTEN'in işe
		/// yaramadığı bir durumda (FSM pasif, cıvata görünmez, `Stages 2` dizisi
		/// hedefi kabul etmiyor) kuyruk her flush'ta yer kaplar ve diğer
		/// cıvataların sırasını yiyip kare süresini uzatırdı. 12 deneme 0..8
		/// aralığı için fazlasıyla yeterli; sonra kayıt düşürülür ve bir sonraki
		/// paket yeniden dener.
		/// </summary>
		public const int RECONCILE_MAX_TRIES = 12;

		/// <summary>
		/// Kuyruktaki cıvataları karşı tarafın bildirdiği kademeye yürütür.
		/// Kare başına çağrılır; bağlantı gerektirmez çünkü kuyruk yalnızca
		/// gelen paketlerle dolar.
		/// </summary>
		public static void ReconcileBolts() {
			if (boltQueue.Count == 0) return;
			float now = Clock();
			if (now < nextReconcile) return;
			nextReconcile = now + RECONCILE_INTERVAL;

			int budget = RECONCILE_BUDGET;
			for (int i = boltQueue.Count - 1; i >= 0 && budget > 0; i--) {
				SatsumaBolt b = boltQueue[i];
				if (b == null) { boltQueue.RemoveAt(i); continue; }
				b.InQueue = false;
				string ev = null;
				// CANLI HATA: bu bes daldan DORDU civatayi sessizce
				// dusuruyordu ve hicbiri sayilmiyordu. "Vida donmedi"
				// ile "vida zaten hedefteydi" logda AYNI gorunuyordu.
				if (b.Stage == null || b.Screw == null || b.PendingStage < 0) {
					// hedef yok: düşür
					Telemetry.ReconcileNoTarget++;
				}
				else {
					int have = b.StageValue;
					if (have == b.PendingStage) {
						// hedefe ulaştı
						Telemetry.ReconcileAtTarget++;
					}
					else if (have > b.PendingStage && have - b.PendingStage > 8) {
						// hedef çok uzak: FSM'i zorlamayı bırak
						Telemetry.ReconcileTooFar++;
					}
					else if (++b.ReconcileTries > RECONCILE_MAX_TRIES) {
						// FSM ilerlemiyor: bırak, sonraki paket dener
						Telemetry.ReconcileGaveUp++;
					}
					else {
						ev = have < b.PendingStage ? "TIGHTEN" : "UNTIGHTEN";
						// EN KRITIK DAL: FSM bu olayi TANIYORSA
						// vidanin gorseli donmez. Canli testte bu olmadi,
						// ama sayisini bilmek sart: "olay yok" ile "olay
						// gonderildi ama islenmedi" farkli hatalardir.
						if (!FsmNames.HasEvent(b.Screw, ev)) {
							ev = null;
							Telemetry.ReconcileNoEvent++;
						}
					}
				}
				// Her durumda ya hedefe varıldı ya da vazgeçildi: kuyruktan düş.
				// Kalan tek durum, bir adım daha atılacak olan.
				if (ev == null) { boltQueue.RemoveAt(i); continue; }
				SuppressScope scope = new SuppressScope();
				try { b.Screw.SendEvent(ev); }
				catch { boltQueue.RemoveAt(i); continue; }
				finally { scope.Dispose(); }
				Telemetry.ReconcileEventSent++;
				budget--;
				// Hâlâ hedefe ulaşmadıysa kuyrukta kalır. b ZATEN i indisinde
				// duruyor, bu yüzden tekrar eklenmez; yalnızca bayrak açık kalır,
				// yoksa QueueReconcile bu cıvatayı ikinci kez kuyruğa alırdı.
				// (List.Insert(i, b) bir kopya daha üretirdi: kayıt hem i'de hem
				// i+1'de durur, cıvata iki adım atar ve kopya birikir.)
				if (b.StageValue != b.PendingStage && b.ReconcileTries <= RECONCILE_MAX_TRIES)
					b.InQueue = true;
			}
		}

		/// <summary>Host finished the initial sync. Reports a mismatch instead of failing silently.</summary>
		public static void ApplySyncDone(int partCount, int boltCount, uint hash) {
			Telemetry.ReceivedSyncDone++;
			syncCompleted = true;
			syncDoneAt = Clock();
			// DO\u011eRULAMA GE\u00c7\u0130\u015e\u0130: paketler bitti, \u015fimdi ger\u00e7ek durumu
			// kar\u015f\u0131la\u015ft\u0131r. Montaj\u0131 reddedilen par\u00e7alar\u0131 bir tur daha
			// denenir (ASSEMBLE yeniden), sonra 3 sn sonra ikinci tur.
			verifyRound = 0;
			verifyNextAt = Clock() + VERIFY_DELAY;
			if (hash == tableHash) {
				SmpLog.Log("initial sync complete: " + partCount + " part(s), "
					+ boltCount + " bolt(s), tables match.");
				return;
			}

			// ------------------------------------------------------------------
			// 1) YEREL TABLO HENÜZ TARANMADI — KARŞILAŞTIRMA YAPMA
			// ------------------------------------------------------------------
			// CANLI ÖLÇÜM: client menüdeyken host'un SyncDone'u geliyor ve
			//   "Us: 0 / 0 / 0x00000000" yazıyordu. Kullanıcıya "aynı kaydı
			// yükle" deniyordu, oysa kayıt sorunu yoktu; sadece bizim
			// dünyamız henüz yüklenmemişti. Karşılaştırma anlamsız.
			//
			// ÇÖZÜM: bekle. OnWorldReady() bir SyncRequest daha gönderir,
			// o zaman anlamlı bir SyncDone gelir ve aşağıdaki blok doğru
			// sayılarla çalışır.
			if (parts.Count == 0 && BoltCount == 0) {
				SmpLog.Log("sync done arrived before our world was scanned; "
					+ "table comparison deferred (we will resync when loaded).");
				return;
			}

			// ------------------------------------------------------------------
			// 2) FARKLI SAHNE Mİ, FARKLI KAYIT MI?
			// ------------------------------------------------------------------
			// CANLI ÖLÇÜM: client STORE sahnesindeyken
			//   biz   121 parça /  28 vida / 303 root / 6615 FSM
			//   host 160 parça / 297 vida / ~740 root / 6942 FSM
			// Bu FARKLI KAYIT değil, FARKLI SAHNE. "Aynı kaydı yükle"
			// demek yanlış olurdu; doğrusu "sürüş dünyasına geç".
			// Ayırt edici: hayvan sayısı iki tarafta da çok farklıysa
			// (bizimki yarısından az) bu kayıt değil, sahne farkıdır.
			int dBolt = boltCount - BoltCount;
			bool differentScene = (boltCount > 0 && BoltCount * 2 < boltCount)
				|| (partCount > 0 && InstallableCount * 2 < partCount);

			if (differentScene) {
				SmpLog.Warning("=== FARKLI SAHNE — karsilastirma gecersiz ===");
				SmpLog.Warning("  host (surus dunyasi): " + partCount + " parca / "
					+ boltCount + " vida");
				SmpLog.Warning("  biz               : " + InstallableCount + " parca / "
					+ BoltCount + " vida");
				SmpLog.Warning("  Bu bir KAYIT farki degil, SAHNE farki: bizdeki tablo"
					+ " surus dunyasindan cok daha kucuk (ornegin STORE sahnesi).");
				SmpLog.Warning("  COZUM: iki taraf da ARABIN/ SURUS DUNYASINDA olsun;"
					+ " CONTINUE ile kaydi yukleyip gidin.");
				SmpLog.Warning("=== BITTI ===");
				return;
			}

			// ------------------------------------------------------------------
			// CANLI HATA: BURADA ELMA ILE ARMUT KARSILASTIRILIYORDU.
			//
			//   eski : ". Us: " + parts.Count
			//          parts.Count  = TABLO GIRDISI   (216)
			//   host : SendAll() -> total = InstallableCount
			//          partCount    = KURULABILIR PARCA (160)
			//
			//   Canli olcum (iki taraf AYNI kayit, ayni surus dunyasi):
			//     kurulabilir parca : 160 (host) / 160 (biz)   AYNI
			//     vida              : 301 (host) / 301 (biz)   AYNI
			//     tablo girdisi     : 217 (host) / 216 (biz)   1 fark
			//   Eski ozet "Host: 160 ... Us: 216" yaziyordu, yani
			//   kayitlar BIREBIR ayni olmasina ragmen "tables differ"
			//   uyarisi cikiyordu. Kullanici ilk satiri okuyor.
			//
			// DERS: karsilastirma yapmadan once iki tarafin AYNI olcumu
			// gonderdiginden emin ol. Eski kod bunu iki yerde dogru
			// yapiyordu (InstallableCount) ve bir yerde yanlis.
			bool materialMatch = (partCount == InstallableCount) && (dBolt == 0);

			// SEVİYE: her iki ölçüm de tutuyorsa bu bir UYARI DEĞİL.
			// Canlı ölçümde kayıtlar birebir aynıydı (160/160, 301/301) ve
			// yine de turuncu "UYARI" basıyordu; kullanıcı gerçek bir hata
			// arıyordu ve bulamayacaktı.
			string verdict = materialMatch
				? "initial sync OK. "
				: "initial sync done. ";
			if (materialMatch) SmpLog.Log(verdict
				+ "kurulabilir parca: " + partCount + " (host) / " + InstallableCount + " (biz) AYNI"
				+ " | vida: " + boltCount + " / " + BoltCount + " AYNI"
				+ " | hash farki TABLO GIRDISI sayisindan (senkronu bozmaz; hedefler yolla eslesiyor)");
			else SmpLog.Warning(verdict
				+ "kurulabilir parca: " + partCount + " (host) / " + InstallableCount + " (biz)"
				+ (partCount == InstallableCount ? " AYNI" : " FARKLI")
				+ " | vida: " + boltCount + " / " + BoltCount
				+ (dBolt == 0 ? " AYNI" : " FARKLI")
				+ " | hash: 0x" + hash.ToString("x8") + " / 0x" + tableHash.ToString("x8")
				+ " -> SENKRON BOZUK OLABILIR");
			// AYNI OYUN SÜRÜMÜ İSE PARÇA ID'LERİ GEÇERLİDİR: kayıtta olan parça
			// yine de aynı yolda durur, yani montaj durumu senkronlanabilir.
			// Sadece BOLT SAYILARI farklıysa indeks kayar; bu yüzden cıvatalar
			// artık yolla eşleşiyor. Ayrıntılı yönlendirme aşağıda.
			//
			// (Eski sürüm burada ayrıca "part COUNT differs ... your two saves
			//  hold different parts" yazıyordu; canlı ölçümde bunun FARKLI
			//  SAHNE olduğu görüldü, o yüzden yukarıya taşındı ve burası
			//  sadeleştirildi.)

			// ------------------------------------------------------------------
			// 3) GERÇEK UYUŞMAZLIK — TEK SEFERLİK YÖNLENDİRME
			// ------------------------------------------------------------------
			// Buraya yalnızca ikisi de aynı sahnede olduğunda düşüyoruz.
			if (mismatchReported) return;
			mismatchReported = true;

			if (materialMatch) SmpLog.Log("=== TABLO FARKİ (ZARARSIZ) ===");
			else SmpLog.Warning("=== TABLO UYUŞMAZLIĞI ===");
			if (materialMatch) SmpLog.Log("  kurulabilir parça : " + partCount + " (host) / "
				+ InstallableCount + " (biz) AYNI");
			else SmpLog.Warning("  kurulabilir parça : " + partCount + " (host) / "
				+ InstallableCount + " (biz)  "
				+ (partCount == InstallableCount ? "AYNI" : "FARKLI"));
			SmpLog.Log("  bizim tablo       : " + parts.Count + " girdi / "
				+ BoltCount + " vida / " + tableInstalledCount + " takılı");
			SmpLog.Log("  vida farkı        : " + boltCount + " (host) vs " + BoltCount
				+ " (biz) = " + (dBolt >= 0 ? "+" : "") + dBolt);
			// Host tablo girdisi SAYISINI GONDERMIYOR (SyncDone yalnizca
			// kurulabilir parca + vida + hash tasiyor). O yuzden burada
			// KARSILASTIRMA YAPILAMAZ, sadece yerel sayi ve hash'in neyi
			// kapsadigi yazilir. Canli olcum: host 217, biz 216.
			SmpLog.Log("  tablo girdisi (biz): " + parts.Count
				+ "   <- host bu sayiyi GONDERMIYOR; hash ise TABLO girdisi"
				+ " + takili durumu uzerinden hesaplaniyor");
			SmpLog.Log("  hash              : 0x" + hash.ToString("x8") + " (host) / "
				+ "0x" + tableHash.ToString("x8") + " (biz)");

			if (dBolt == 0 && partCount == InstallableCount) {
				// HER İKİ ÖLÇÜM DE TUTUYOR (canlı ölçüm: 160=160, 301=301).
				// Hash farkı tablo GİRDİSİ sayısından geliyor (217 vs 216),
				// kurulabilir parça veya vida sayısından değil. Hedefler
				// yolla eşleştiği için bu fark senkronu bozmaz.
				SmpLog.Log("  VIDA SAYISI TUTUYOR. Hash farkı takılı parça "
					+ "durumundan; hedefler YOLLA eşleştiği için "
					+ "vida sıkma ve parça takma senkronlanmalı.");
				SmpLog.Warning("=== BITTI ===");
				return;
			}

			SmpLog.Warning("  <== VIDA SAYISI FARKLI: İNDİS KAYAR, hedef "
				+ "yanlış çözülür.");
			SmpLog.Warning("  Paketler teslim ediliyor (ölçüldü: BoltBatch 75/75, "
				+ "PartBatch 60/60); sorun gönderim değil, hedef çözümü.");
			SmpLog.Warning("  ÇÖZÜM: iki taraf da AYNI kaydı yüklesin.");
			SmpLog.Warning("   1) Host'ta oyunu kaydet, oyundan çık, oyunu kapat.");
			SmpLog.Warning("   2) Client'ta aynı kaydı CONTINUE ile yükle.");
			SmpLog.Warning("   3) Aynı kayıt olmadan vida sıkma/parça takma "
				+ "güvenilir değildir.");
			SmpLog.Warning("=== BITTI ===");
		}

		/// <summary>
		/// SyncDone sonras\u0131 "ger\u00e7ekten do\u011frula" turu.
		///
		/// SORUN: Assembly FSM'i ASSEMBLE'i c\u0131vata kontrol\u00fcyle reddedebiliyor.
		/// Paketlerin gelme SIRASI \u00e7\u00f6z\u00fcm\u00fc gider (c\u0131vatalar \u00f6nce), ama
		/// client kendi kay\u0131t\u0131n\u0131 y\u00fckleyene kadar bir montaj ka\u00e7abilir veya
		/// ba\u011flant\u0131 bo\u015fma an\u0131nda bir paket d\u00f6nebilir.
		///
		/// \u00c7\u00d6Z\u00dcM: ald\u0131klar\u0131m\u0131z durumla kendi \u00f6l\u00e7t\u00fcm\u00fcz kar\u015f\u0131la\u015ft\u0131r.
		/// Uyu\u015fmayan her par\u00e7a i\u00e7in olay yeniden oynat\u0131l\u0131r. \u0130ki tur
		/// yeter: FSM'in kendi gecikmeli montaj\u0131 (vin\u00e7, ask\u0131) ikinci turda
		/// tamamlanm\u0131\u015ft\u0131r. Sonu\u00e7 loglan\u0131r \u2014 "ka\u00e7 par\u00e7a almad\u0131" art\u0131k
		/// tahmin de\u011fil, say\u0131d\u0131r.
		/// </summary>
		public static void RunVerifyPass() {
			if (parts.Count == 0 || desiredInstalled.Count == 0) return;
			verifyFixed = 0;
			verifyStillWrong = 0;
			verifyIndex = 0;
			verifyRunning = true;
			verifyNextAt = Clock();
		}

		/// <summary>Kare başına işlenecek parça sayısı (donmayı önler).</summary>
		public const int VERIFY_BUDGET = 3;

		/// <summary>Doğrulama adımları arası bekleme (s).</summary>
		public const float VERIFY_STEP_INTERVAL = 0.05f;

		/// <summary>
		/// Doğrulama turunun bir karelik dilimi.
		///
		/// KARE BÜTÇESİ: canlı kanıt, oyun "kastı".
		///
		/// Eski sürüm 378 parçayı TEK KAREDE yeniden ASSEMBLE ediyordu.
		/// Her çağrı FsmForEvent → GetComponents&lt;PlayMakerFSM&gt;() + Fsm.Event()
		/// demek; 378 kez = yüzlerce FSM'nin aynı karede yeniden
		/// başlaması = saniyelerce donma. şikayet tam olarak buydu:
		/// "oyun kasıyor parçalardan kaynaklı".
		///
		/// Doğrulama bir KARE HIZI işi değil, birkaç saniyelik toparlamadır:
		/// kare başına VERIFY_BUDGET parça, kalanı sonraki kareye.
		/// </summary>
		static void StepVerify() {
			if (!verifyRunning) return;
			float now = Clock();
			if (now < verifyNextAt) return;
			verifyNextAt = now + VERIFY_STEP_INTERVAL;

			int budget = VERIFY_BUDGET;
			while (budget-- > 0 && verifyIndex < parts.Count) {
				SatsumaPart p = parts[verifyIndex++];
				if (p == null || !p.Installable) continue;
				bool want;
				if (!desiredInstalled.TryGetValue(p.Id, out want)) continue;
				// Guvenilir sinyal yoksa dogrulama YAPILAMAZ: `Removal` FSM'i
				// olup Installed bool'u olmayan parcalarda InstalledOf her
				// zaman ~true doner (FSM 'Mouse off' gibi adli bir state'te
				// takilidir), sokuk parca "takili" gorunur ve verify her turde
				// REMOVE yagdirip sesi/gorseli bozar. Olay tekrari (canli yol)
				// zaten uyguladi; dogrulanamayan atlanir.
				if (p.InstalledVars.Count == 0 && p.Removal != null) continue;
				if (InstalledOf(p) == want) continue;
				// Montajı reddedilmiş: olayı bir kez daha oynat.
				ApplyPartState(p.Id, want, p.IsGroupBolted(),
					p.HasSlots ? p.FilledSlotIndex() : (byte)0, p.DamagedValue);
				if (InstalledOf(p) == want) verifyFixed++; else verifyStillWrong++;
			}

			if (verifyIndex < parts.Count) return;

			verifyRunning = false;
			SmpLog.Log("verify pass " + verifyRound + ": re-sent "
				+ (verifyFixed + verifyStillWrong) + " part(s), fixed " + verifyFixed
				+ ", still wrong " + verifyStillWrong + ".");
		}

		static PlayMakerFSM FsmForEvent(SatsumaPart part, string preferredFsm, string eventName) {
			if (part == null || string.IsNullOrEmpty(eventName)) return null;
			if (preferredFsm != null) {
				PlayMakerFSM pref = null;
				if (preferredFsm == "Assembly") pref = part.Assembly;
				else if (preferredFsm == "Removal") pref = part.Removal;
				else if (preferredFsm == "Assemble") pref = part.Assemble;
				// v15: tercih edilen FSM olayi BILMIYORSA gonderme.
				// PlayMaker bildirilmemis bir olayi sessizce yok sayar
				// (bkz. Satsuma/FsmNames.cs), yani "uygulandi" sayilip gorsel
				// hicbir sey olmazdi. Once ilan eden baska bir FSM ara.
				if (pref != null && Declares(pref, eventName)) return pref;
			}
			// Wiring parts and bolt-only parts do not carry all three FSMs: pick whichever of
			// our FSMs actually declares the event.
			PlayMakerFSM declared = FindFsmWithEvent(part, eventName);
			if (declared != null) return declared;
			if (eventName == "ASSEMBLE" && part.Assembly != null) return part.Assembly;
			if (eventName == "REMOVE" && part.Removal != null) return part.Removal;
			return part.Assembly != null ? part.Assembly : (part.Removal != null ? part.Removal : part.Assemble);
		}

		/// <summary>FSM bu olayi BILIR mi? (bilmiyorsa SendEvent sessizce yutulur)</summary>
		static bool Declares(PlayMakerFSM fsm, string eventName) {
			if (fsm == null || string.IsNullOrEmpty(eventName)) return false;
			try {
				FsmEvent[] ev = fsm.FsmEvents;
				if (ev == null) return false;
				for (int i = 0; i < ev.Length; i++) {
					if (ev[i] != null && string.Equals(ev[i].Name, eventName, StringComparison.Ordinal))
						return true;
				}
			} catch { }
			return false;
		}

		/// <summary>
		/// Istenen islevi TASIYAN olay adi, ya da TASIYAN YOKSA null.
		///
		/// OYUNUN SEKLI (dumper kaniti): parca takma/soKme trigger NESNESI
		/// uzerinde bir <c>Assembly</c> FSM'i ile olur; parcanin kendisi
		/// (battery0, starter(Clone), ...) ayri bir <c>Removal</c> FSM'i tasiyor.
		///   docs/fsm_detail/l2__trigger_battery__Assembly.txt  -> TRIGGER ENTER,
		///     ASSEMBLE, DISASSEMBLE, GLOBALEVENT, LOOP, SPAWNITEM   (REMOVE YOK)
		///   docs/fsm_detail/l2__battery0__Removal.txt            -> REMOVE var
		///
		/// Yani trigger satiri bir tablo girdisi oldugunda eski yol
		/// "REMOVE"u <c>Assembly</c>'e gonderiyordu; bildirilmedigi icin
		/// HICBIR sey olmuyordu ve hicbir log da yazilmardi (belirti:
		/// "sokulmus gorunuyor ama gonderdigimde bir sey olmuyor").
		///
		/// Cozum: ayni islevi tasiyan adi tercih et. Assembly FSM'i
		/// DISASSEMBLE bildiriyorsa REMOVE yerine o gonderilir; boylece
		/// PlayMaker'in kendi sokme zinciri calisir.
		///
		/// DIKKAT (v15 duzeltme): `DISASSEMBLE` bir SOKME olayidir ve
		/// YALNIZ `REMOVE` icin kullanilir. Bir `ASSEMBLE` (takma) istegi
		/// ASLA `DISASSEMBLE`'e cevrilmez; bu anlam degistirir (takma
		/// istegi sokmeye doner). Hedef hicbir yerde `ASSEMBLE`
		/// bildirmiyorsa null doner ve cagiran olayi GONDERMEZ; kayit
		/// durumu yine de `InstalledOf`/InstalledVars tarafindan yakalanir.
		/// </summary>
		static string EffectiveEvent(PlayMakerFSM target, string eventName) {
			if (target == null || string.IsNullOrEmpty(eventName)) return eventName;
			if (Declares(target, eventName)) return eventName;
			// v17: bildirilmemis `PARTn` HICBIR SEYE CEVIRILMEZ. `ASSEMBLE`
			// gibi null doner: cagilan gondermez ve uyarir. Yanlis bir yuvaya
			// (`PARTm`/`ASSEMBLE`) cevirmek karsi tarafa YANLIS variant
			// kurar ve oyuncu elindekini duzeltemez.
			if (IsSlotEvent(eventName)) return null;
			// REMOVE -> DISASSEMBLE: AYNI islev (sokme zinciri). YASAL.
			if (eventName == "REMOVE" && Declares(target, "DISASSEMBLE")) return "DISASSEMBLE";
			// ASSEMBLE -> DISASSEMBLE: TERS islev. YAPILMAZ. Hedef
			// ASSEMBLE'i bildirmiyorsa hicbir islev tasinmayacak: null
			// doner, cagiran gondermez ve uyarir. Kayit durumu yine de
			// ApplyPartState'in yazma yolundan (InstalledVars veya
			// Database/*/Installed) yakalanir.
			if (eventName == "ASSEMBLE") return null;
			return eventName;
		}

		static PlayMakerFSM FindFsmWithEvent(SatsumaPart part, string eventName) {
			if (part.Object == null || string.IsNullOrEmpty(eventName)) return null;
			PlayMakerFSM[] all;
			// v15: yalniz o nesnenin USTUNDEKI bilesenler araniyordu.
			// Oyun cogu zaman montaj/sokme FSM'ini bir COCUK nesneye
			// koyuyor; o zaman hep null donuyordu ve kod bir sonraki
			// (bildirmeyen) varsayilana dustugu icin yanlis FSM'e gidiyordu.
			try { all = part.Object.GetComponentsInChildren<PlayMakerFSM>(true); } catch { return null; }
			if (all == null) return null;
			// v22: ONCE parcanin KENDI ust component'i.
			//
			// DOKUM KANITI: `ASSEMBLE` olayini birden fazla FSM
			// bildiriyor. Ornegin
			// `Chassis/FL/strut fl(xxxxx)` altinda hem `Removal` hem de
			// `BoltPM/Screw` bu olayi TANIMLIYOR. `GetComponentsInChildren`
			// SIRASI garanti DEGIL (Unity kardeş sırasında derinlik-önce
			// geziyor, dokum bunu kaydetmiyor), yani "ilk bulan" kurali
			// bir cıvata FSM'ine monte edilen `ASSEMBLE`'i `Screw`'e
			// yönlendirebilirdi.
			//
			// Parcanın kendi component'i her zaman önce gelir (Unity
			// `GetComponentsInChildren` nesnenin kendisiyle başlar), ve
			// montaj FSM'i (`Removal`/`Assembly`/`Assemble`/`BoltCheck`)
			// parça nesnesinin kendisindedir.
			//
			// KALAN TEHLIKE: `Wiring/Parts/battery_terminal_minus` gibi
			// 3 parçada parça nesnesinin kendisinde HIC montaj FSM'i yok;
			// altındaki tek `Screw` bu olayı bildiriyor. `GetComponent`
			// null doner, sonra alt taramaya düşeriz -- ve orada yine
			// `Screw` bulunur. O yüzden alt taramada FSM ADI kontrolü de
			// ekleniyor: `Screw` bir VIDA, montaj olayinin hedefi olamaz.
			try {
				PlayMakerFSM own = part.Object.GetComponent<PlayMakerFSM>();
				if (own != null && Declares(own, eventName)) return own;
			}
			catch { }
			for (int i = 0; i < all.Length; i++) {
				if (all[i] == null) continue;
				// v22: vida FSM'i montaj olayinin hedefi olamaz.
				string nm = null;
				try { nm = all[i].FsmName; } catch { }
				if (string.Equals(nm, "Screw", StringComparison.Ordinal)) continue;
				if (all[i] == null) continue;
				FsmEvent[] events;
				try { events = all[i].FsmEvents; } catch { continue; }
				if (events == null) continue;
				for (int e = 0; e < events.Length; e++) {
					if (events[e] != null && string.Equals(events[e].Name, eventName, StringComparison.Ordinal))
						return all[i];
				}
			}
			return null;
		}

		/// <summary>Part ID ile tablo girdisini çözer (fallback ile). Dışarıdan çağrılabilir.</summary>
		public static bool TryResolvePublic(string id, out SatsumaPart part) {
			return TryResolve(id, out part);
		}

		static bool TryResolve(string id, out SatsumaPart part) {
			part = null;
			if (string.IsNullOrEmpty(id)) return false;
			if (byId.TryGetValue(id, out part) && part != null) return true;

			// Re-parented since our scan, or a slightly different tree: fall back to the
			// object name when exactly one installable entry carries it.
			int slash = id.LastIndexOf('/');
			string leaf = slash >= 0 ? id.Substring(slash + 1) : id;
			List<SatsumaPart> same;
			if (byName.TryGetValue(leaf, out same) && same != null) {
				SatsumaPart only = null;
				for (int i = 0; i < same.Count; i++) {
					if (!same[i].Installable) continue;
					if (only != null) { only = null; break; } // ambiguous
					only = same[i];
				}
				if (only != null) {
					part = only;
					Telemetry.FallbackLookups++;
					return true;
				}
			}
			Telemetry.UnresolvedParts++;
			// Tablomuz yoksa (menudeyken gelen push) uyari YOK: dunya
			// yuklenince hepsi cozulecek. Ayrica her dunyada liste
			// sifirlanir, yoksa menu-donemi id'ler sonraki gercek
			// hatalari susturur (canli kanit: 385 satir menu spami).
			if (parts.Count == 0) return false;
			if (warnedIds.Add(id)) SmpLog.Warning("no part '" + id + "' (different save?).");
			return false;
		}

		// ------------------------------------------------------------------
		// Diagnostics
		// ------------------------------------------------------------------

		/// <summary>
		/// Parçanın GERÇEK takılı durumu. 'Installed' bool'u 338 parçada yok,
		/// o yüzden bool tek başına okunamaz (hepsi false dönerdi).
		///
		/// CANLI DENEY KANITI (sürücü kapısı sökülüp `smp parts door`):
		///   takılı kapı:   rem='Mouse off', parent='pivot_door_left', bolts=4/4,
		///                   trigger kapalı (go=0/0, asm='?empty')
		///   sökük kapı:     rem='?empty', parent='(root)', bolts=0/4,
		///                   trigger açık (go=1/1, asm='Check for Part collision')
		///
		/// TAZE-SAVE DÜZELTMESİ: ?empty her zaman takılı DEMEZ. Seçenek
		/// parça trigger'ları (vinç motoru, yarış kemeri...) montaj noktası
		/// kapalıyken hiç çalışmaz: go=1/0 (kendisi açık, ebeveyn kapalı) +
		/// ?empty = TAKILI DEĞİL (parça arabada yok). Gerçek takılıda trigger
		/// KENDİSİ kapanır (go=0/x). Taze save'de 8 yanlış T buradan geldi.
		///
		/// Kurallar:
		///   1) Removal FSM'i varsa (parçanın kendisi): gerçek bir aktif durum
		///      adı = takılı. Sökülünce oyun FSM'i durdurur ('?empty').
		///   2) Assembly FSM'i varsa (trigger): "Check" ile başlayan durumlar
		///      beklemek demektir; KENDİSİ açık ama ebeveyni kapalı (?empty)
		///      trigger hiç çalışmamıştır = takılı değil. Kapalı trigger
		///      (?empty), 'Show bolts' ve diğer her durum = takılı.
		///   3) İkisi de yoksa (kablolama): bool ne diyorsa (eski davranış).
		/// </summary>
		public static bool InstalledOf(SatsumaPart p) {
			if (p == null) return false;

			// v15: EN GUCLU KAYNAK -- trigger'in kendi `db_*` nesne
			// degiskeni uzerindeki Database/Data bool'u.
			//
			// Dumper kaniti (trigger_shock_fl[Assembly], 258 string):
			//   GetFsmBool, BoolTest,
			//       db_PartRequired1, Data, Installed, gameObject, fsmName
			//   SetFsmBool ... Data, Installed ...
			//   bildirilen degiskenler: Required1, HandChild, PartName,
			//       UniqueTagArray, UniqueTagFloat, ActivateSpindleBolts,
			//       ActivateThis, DetachPart, Part, db_PartRequired1,
			//       db_ThisPart
			//
			// Yani trigger'in KENDI `Installed` bool'u YOK. Oyun, gercek
			// durumu `db_*` degiskeninin tuttugu Database nesnesindeki
			// `Data/Installed` uzerine yaziyor/okuyor. `db_*` bir
			// FsmGameObject DEGISKENI oldugu icin calisma aninda cozulur
			// (döküm hangi Database satirina baktigini soyleyemez, ama
			// degisken degeri soyler).
			//
			// Satsuma'daki 102 Assembly tetikleyicisinin 99'u en az bir
			// `db_*` nesne degiskeni bildiriyor. Bu, daha once durum
			// ADINDAN sezgisel okunan 90 kaydin ve 56 kaydin (sabit false)
			// cogunu gercek veriye bagliyor.
			if (p.InstalledVars.Count == 0) {
				FsmBool dbFlag = DatabaseInstalledFlag(p);
				if (dbFlag != null) { try { return dbFlag.Value; } catch { } }
			}
			// v15: GERCEK BOOL ONCE.
			//
			// Eski sira Removal -> Assembly -> bool idi, yani sezgisel okuma
			// her zaman kazaniyordu. Ama `Removal` FSM'i gecici durumlarda
			// (Mouse over, Requirements) durur ve bunlar `?` ile baslamaz,
			// yani sezgisel okuma "takili" diyor. Dogrulama dongusu de
			// hedefi "sokuldu" gorup 4 tur ayni REMOVE'i tekrar gonderiyordu.
			//
			// Artik cok sayida parcada gercek `Installed` bool'u var (Tur 3:
			// Database/* Data bayraklari baglandi), o yuzden once o okunur.
			// Bool yoksa eski sira aynen devreder.
			if (p.InstalledVars.Count > 0) {
				try { return p.Installed; } catch { }
			}

			// ------------------------------------------------------------------
			// v22: FSM DURUM ADINDAN OKUMA KALDIRILDI.
			//
			// Onceki kod, gercek bir bool bulamadiginda `Removal`/`Assembly`
			// FSM'inin AKTIF DURUM ADINI okuyordu:
			//     rem[0] != '?'            -> "Removal FSM'i calisiyor"
			//     asm.StartsWith("Check")   -> "Assembly FSM'i 'Check*' degil"
			//
			// Ikisi de TAHMIN. DOKUM KANITI:
			//
			// 1) `rem[0] != '?'` -- '?' bir OYUN degeri degil, `ActiveOf`'in
			//    (`:4759`) "aktif durum yok" icin URETtigi sentayeldir.
			//    Dokumde 60 `Removal` FSM'inin BASLANGIC durumlari:
			//       44 'Disable trigger', 16 'Add mass'
			//    ve gecis durumlari: 'Mouse off'(60), 'Requirements'(55),
			//    'Mouse over'(5).
			//    HICBIR durum adi '?' ile baslamaz. Yani kural
			//    "FSM calisiyor" demek, "parca takili" demek DEGIL.
			//    Parca sokuldugu halde 'Requirements' durumunda duran bir
			//    FSM "takili" okunurdu.
			//
			// 2) `asm.StartsWith("Check")` -- 102 `Assembly` FSM'inin
			//    baslangic durumlari: 80 'Check for Part collision',
			//    22 'Check Assembly'. Ikisi de 'Check' ile basliyor, yani
			//    BU AN icin dogru. Ama 'Requirements'(32 FSM) ve 'Ready'(3)
			//    'Check' ile BASLAMIYOR -> "takili" sayiliyor. 'Requirements'
			//    ontamlanmamis onkosul dalidir; orada duran bir tetikleyici
			//    "takili" mi degil mi dokumdan BELIRLENEMEZ.
			//
			// SONUC: gercek bir `Installed` bool'u olmayan bir parcanin
			// durumu bu build'de DOKUMEDEN OKUNAMAZ. Guvenli tek yol:
			// BILINMIYOR de. `InstalledOf` yalniz gercek bool okur; yoksa
			// `HasKnownInstallState == false` doner ve o parca gonderilmez.
			// Tahminle "takili" demek, karsi tarafa YANLIS durum
			// gondermekten kotudur.
			// ---------------------------------------------------------------
			// v24: 7 PARCA ICIN "BILINMIYOR" DEGIL -- ALTERNATIF KAYNAK VAR.
			//
			// CANLI KANIT (host 12:15:50): 7 parcada `Data FSM'i yok`.
			//   1 cd player(Clone)/trigger_disc
			//   4 wheel_offset (FL/FR/RL/RR)
			//   1 fire extinguisher holder/trigger_extinguisher
			//   1 MiscParts/trigger_battery
			// Host 160 bulup 153 gonderiyor; kullanici "parcalar koptu" dedi.
			//
			// DOKUM KANITI: bu parcalarin montaj durumu BOOL ILE DEGIL
			// yonetilmiyor. `trigger_battery|Assembly` bildirilen:
			//     SPAWNITEM, Part, HandChild, `battery(Clone)`
			// `trigger_extinguisher|Assembly`:
			//     `GetChild` + `childName` + `withTag` + `ITEM`
			//     -> `fire extinguisher(itemx)`, `HandChild`, `ItemPivot`
			//
			// `Assembly` FSM'i 102 kayitta, `Removal` 60 kayitta
			// `ActivateGameObject` kullanir. Yani oyun montaj durumunu
			// NESNENIN AKTIFLIGI ve ARACTA OLUP OLMADIGI ile yonetiyor.
			//
			// Bu bir TAHMIN degil: Transform.parent zinciri ve
			// GameObject.activeSelf dogrudan okunabilir GERCEK veridir.
			// Onceki turda FSM durum ADI okunuyordu (tahmin); burada
			// nesnenin kendisi okunuyor (olcum).
			// ---------------------------------------------------------------
			if (PlacementInstalledOf(p)) return true;
			// Aracin altinda degilse parca SOKULMUS demektir -- ama bu
			// karar ancak nesnenin YOLU cozulduyse kesindir.
			if (PlacementReadable(p)) return false;

			// ---------------------------------------------------------------
			// v26: `PART` ETIKETLI KARDESIN `Data` FSM'I.
			//
			// KULLANICI ISTEGI: "kalan tüm triggerleri derin bir araştırma
			// yap herşeyi senkronize et".
			//
			// DOKUM KANITI (3 tetikleyicinin tamamı icin):
			//   `MiscParts/trigger_battery|Assembly`   idx 121-127:
			//     'battery(Clone)', 'PART', 'PART',
			//     'gameObject', 'childName', 'withTag', 'storeResult'
			//   `Chassis/trigger_subframe|Assembly`   idx 27-33:
			//     'sub frame(Clone)', 'PART', 'PART',
			//     'gameObject', 'childName', 'withTag', 'storeResult'
			//   `.../trigger_extinguisher|Assembly`   idx 107-113:
			//     'fire extinguisher(itemx)', 'ITEM', 'ITEM', ...
			//
			// Yani `GetChild` + `childName` + `withTag` = "bu etiketli
			// cocugu bul". `PART` etiketi 104 tetikleyicide, `ITEM`
			// YALNIZCA `trigger_extinguisher`'da (dokumda tek istisna).
			//
			// Oyun parcayi bu etiketli kardes buluyor, sonra DURUMU
			// PARCANIN `Data` FSM'ine yaziyor -- tetikleyicinin kendi
			// `db_*` degiskeni olmadan. DOKUM:
			//   `trigger_battery|Assembly` idx 19-25:
			//     'Part', 'Part', 'Data', 'Installed', 'Use', 'Installed'
			//   `trigger_subframe|Assembly` 'End' durumu:
			//     'Part', 'CarBuilding', 'assemble', 'Data', 'Installed'
			//
			// -> `SetFsmBool{ gameObject='Part', fsmName='Data',
			//                  variableName='Installed' }`
			//
			// HEDEF SATIR DOKUMDE MEV CUT:
			//   `Database/PartsStatus/Battery|Data` bildiriyor:
			//     Bolt1, Bolt2, Charge, Force, MaxCharge, MaxVoltage,
			//     Installed, Joint
			// Yani `Installed` ORADA var. `DatabaseInstalledFlag` onu
			// goremiyordu cunku yalniz `db_*` adli GameObject
			// DEGISKENLERINI ariyor.
			//
			// NOT: `Database/PartsStatus/Battery` zaten `Database` kanalinda
			// senkronize (225 satirlik kanaldadır). Buradaki ek, TETIKLEYICI
			// satirinin kendisini de gonderilebilir yapmak -- boylece
			// `installed` bayragi tetikleyici uzerinden de tasinir ve
			// 160/160 tam olur.
			// ---------------------------------------------------------------
			if (TryTaggedSiblingDataFlag(p, out FsmBool tagged)) {
				try { return tagged.Value; } catch { return false; }
			}

			if (p.DbDamaged != null && p.DbInstalled == null && p.DbInstalledChecked) {
				// `Data` FSM'i var ama `Installed` bildirmiyor -- tanimli
				// olarak "okunamaz".
				return false;
			}
			return false;
		}

		/// <summary>
		/// v26: `PART`/`ITEM` etiketli kardesin `Data|Installed` bayragi.
		///
		/// ONCE v23'te yazilan `CarryableOfTrigger` ayni etiket kuralini
		/// kullanir; burada onun TERSI yapilir: etiketli kardes bulunur ve
		/// USTUNDEKI `Data` FSM'inden `Installed` okunur.
		///
		/// DOKUM: `GetChild{ childName='PART', withTag=true }` -> ardidan
		/// `SetFsmBool{ gameObject='Part', fsmName='Data',
		/// variableName='Installed' }`. Yani oyunun KENDI yolu budur.
		///
		/// GUARD: etiketli kardes bulunamazsa `false` doner, TAHMIN yapilmaz.
		/// </summary>
		static bool TryTaggedSiblingDataFlag(SatsumaPart p, out FsmBool flag) {
			flag = null;
			if (p == null || p.Object == null) return false;
			// Sadece TETIKLEYICILER icin: normal parcanin zaten `db_*`
			// yolu var; tetikleyici olmayanda bu yol yanlis nesneyi
			// okurdu.
			if (!IsTriggerName(p.Object)) return false;

			string[] tags = { PART_TAG, ITEM_TAG };
			for (int t = 0; t < tags.Length; t++) {
				GameObject sib = FindTaggedSibling(p.Object, tags[t]);
				if (sib == null) continue;
				PlayMakerFSM data = null;
				try { data = sib.GetComponent<PlayMakerFSM>(); } catch { }
				if (data == null) continue;
				FsmBool b = FsmNames.Bool(data, "Installed");
				if (b == null) continue;
				// KANIT: `Data` FSM'i `Installed` bildiriyor ve `Data`
				// FSM'ine sahip oldugu dogrulandi.
				if (!string.Equals(SafeFsmName(data), "Data", StringComparison.Ordinal))
					continue;
				flag = b;
				return true;
			}
			return false;
		}

		/// <summary>v26: `ITEM` etiketi (dokumda YALNIZ `trigger_extinguisher`).</summary>
		const string ITEM_TAG = "ITEM";

		/// <summary>
		/// v26: verilen etiketi tasiyan, `Renderer` sahip kardes.
		///
		/// `GetChild` + `withTag` oyunun kurali; Unity 5 bu build'de
		/// `GetChild(i)` + `childCount` ile gezilir (`firstChild`/
		/// `nextSibling` YOK -- v21/v24'te ayni sekilde duzeltildi).
		/// </summary>
		static GameObject FindTaggedSibling(GameObject go, string tag) {
			Transform parent = null;
			try { parent = go.transform.parent; } catch { return null; }
			if (parent == null) return null;
			int n;
			try { n = parent.childCount; } catch { return null; }
			for (int i = 0; i < n; i++) {
				Transform c;
				try { c = parent.GetChild(i); } catch { continue; }
				if (c == null || c.gameObject == go) continue;
				bool tagged;
				try { tagged = c.gameObject.CompareTag(tag); } catch { continue; }
				if (!tagged) continue;
				if (!HasRenderer(c.gameObject)) continue;
				return c.gameObject;
			}
			return null;
		}

		/// <summary>
		/// v24: montaj durumu NESNENIN KONUMUNDAN okunabiliyor mu?
		///
		/// DIKKAT -- BURADA BİR TUZAK VAR VE KAPANMASI GEREKİYOR:
		/// 7 okunamayan kaydin cogu bir `trigger_*` TETIKLEYICISI
		/// (`trigger_battery`, `trigger_extinguisher`, `trigger_disc`,
		/// `wheel_offset`). Tetikleyici aracin altinda `HER ZAMAN`
		/// durur; `IsUnderSatsuma(trigger)` daima `true` doner. Yani
		/// tetikleyicinin konumu MONTAJ DURUMU DEGILDIR ve kanit
		/// sayilmaz.
		///
		/// DOKUM: tetikleyicinin parcasi ayri bir nesnedir --
		/// `trigger_battery|Assembly` `Part` + `HandChild` +
		/// `battery(Clone)` bildiriyor. `p.Object` bu cogu zaman
		/// TETIKLEYICININ KENDISIDIR, parcasi degil.
		///
		/// Bu yuzden konum yalnizca `p.Object` gercekten bir TETIKLEYICI
		/// DEGILSE okunur. Tetikleyici adlari dokumde `trigger_` on eki
		/// tasiyor ve hicbir parcada bu on eki yok (`BoltPM`, `wheel`,
		/// `battery(Clone)`).
		/// </summary>
		static bool PlacementReadable(SatsumaPart p) {
			if (p == null || p.Object == null) return false;
			// Tetikleyici konumu montaj durumu DEGILDIR.
			if (IsTriggerName(p.Object)) return false;
			try {
				return IsUnderSatsuma(p.Object);
			} catch { return false; }
		}

		/// <summary>
		/// v24: nesne bir montaj tetikleyicisi mi?
		///
		/// DOKUM KANITI: `trigger_*` oneki 98 nesneyi kapsiyor
		/// (`trigger_bootlid`, `trigger_hood`, `trigger_battery`...).
		///
		/// v25 DUZELTME: onek TEK BASINA yeterli degildi. Dokumda 19
		/// nesne `trigger_` ile BASLAMIYOR ama yine de tetikleyici:
		///     `TriggerWheelFL_New` / `TriggerWheelFR_New`
		///     `TriggerWheelRL_New` / `TriggerWheelRR_New`
		///     `Trigger_motor1..3`
		///     `FireTrigger`, `FireTriggerPlayer`, `CapTrigger_*` (5 adet)
		///
		/// ETKI: `TriggerWheel*_New` v24'te "gercek parca" sayilip
		/// `activeSelf` ile okundu. Bir TETIKLEYICI tanim geregi
		/// aktiftir, dolayisiyla bu dort nesne HEP "takili" gonderdi --
		/// hangi tekerlek takili olursa olsun. Bu bir YALAN'dir.
		///
		/// DOKUM (bu dort nesnenin bildirdikleri):
		///     Part, Pivot1, Pivot2, ThisWheel, db_PartRequired1,
		///     WheelScript, Wheel
		/// `db_PartRequired1` -> `Prereq` ailesi -> `PRIORITY`'de
		/// BILEREK ATLANIYOR (baska parcanin bayragi). Yani bunlarin
		/// `Installed` kaynagi YOK; montaj noktasi olarak kalirlar.
		/// </summary>
		// v28: `internal` -- `PartSync.IsTriggerObject` bu kurala
		// DELEGASYON yapiyor (kopma duzeltmesi). Dokum kanitli TEK
		// KAYNAK olmasi icin: `PartSync` kendi `trigger_` kontrolunu
		// TUTMASIN.
		internal static bool IsTriggerName(GameObject go) {
			if (go == null) return false;
			string n;
			try { n = go.name; } catch { return false; }
			if (string.IsNullOrEmpty(n)) return false;
			if (n.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)) return true;
			// v25: `TriggerWheel*_New` -- dokumda dogrulanmis 4 adet.
			if (n.StartsWith("TriggerWheel", StringComparison.OrdinalIgnoreCase)
				&& n.EndsWith("_New", StringComparison.Ordinal)) return true;
			// `Trigger_motor1..3` -- `OrdinalIgnoreCase` ile de yakalanir
			// ama acikca yazmak dokum kanitini kodda gosterir.
			if (n.StartsWith("Trigger_motor", StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>
		/// v24: parca aracin altinda VE gorunur mu? (takili demek icin)
		///
		/// DOKUM KANITI: `Assembly` 102/102 ve `Removal` 60/60 kayitta
		/// `ActivateGameObject` kullanir. Sokulen parca pasiflestirilir.
		/// Bu, `Installed` bool'unun YOK oldugu yerde tek okunabilir
		/// montaj sinyalidir.
		///
		/// YALNIZ tetikleyici OLMAYAN nesne icin gecerlidir
		/// (`PlacementReadable` ayni filtresi uygular).
		/// </summary>
		static bool PlacementInstalledOf(SatsumaPart p) {
			if (p == null || p.Object == null) return false;
			if (IsTriggerName(p.Object)) return false;
			try {
				if (!IsUnderSatsuma(p.Object)) return false;
				return p.Object.activeSelf;
			} catch { return false; }
		}

		/// <summary>
		/// v22: bu parcanin montaj durumu GERCEK bir bool'dan okunabiliyor mu?
		///
		/// `InstalledOf` artik yalniz gercek veri okur (FSM durum adi
		/// tahmini kaldirildi). Arayanlar once bunu sorar; `false` ise parca
		/// telde GONDERILMEZ, cunku gonderilecek bir dogru deger yok.
		///
		/// v24 CANLI KANIT (host 12:04 / client 12:05 oturumu):
		///   Host : `160 installable` buluyor ama `initial sync done:
		///          153 part(s)` yaziyor -- 7 PARCA HIC GONDERILMEDI.
		///   Client: `kurulabilir parca: 153 (host) / 160 (biz) FARKLI`
		///           ve `SENKRON BOZUK OLABILIR` uyarisi.
		///
		/// Bu filtre onceki turun (v22) DUZELTMESIYDI; bedeli bu oldu:
		/// okunamayan durum "sokulmus" demek DEGIL, "BILINMIYOR" demek,
		/// ama telde hic gonderilmemesi karsi tarafada parcanin yerinde
		/// KALMASI demek. Kullanici bunu "parcalar koptu" olarak yasadi.
		///
		/// DIKKAT: bu bir KOPMA DEGIL. Log kaniti:
		///   `PartBatch 60/60` TESLIM EDILMIS,
		///   `zaten-hedefte 780`,
		///   `verify pass 1/2/3: re-sent 0, fixed 0, still wrong 0`.
		/// Yani ag hat saglam. Eksik olan 7 kaydin KENDISI.
		///
		/// v24 TEŞHİS: hangi parcalar okunamiyor, ADLARIyla yazilir.
		/// Tahminle cozmek yerine once olcuyoruz.
		/// </summary>
		public static bool HasKnownInstallState(SatsumaPart p) {
			if (p == null) return false;
			if (p.InstalledVars.Count > 0) return true;
			if (!p.DbInstalledChecked) DatabaseInstalledFlag(p);
			if (p.DbInstalled != null) return true;
			// v24: bool yok ama NESNE konumu okunabiliyorsa bu da
			// gecerli bir kaynaktir (7 parcada boyle).
			if (PlacementReadable(p)) return true;
			// v26: `db_*` yoksa `PART`/`ITEM` etiketli kardesin `Data`
			// FSM'inden okunur (dokum: `trigger_battery`,
			// `trigger_subframe` bu yolla cozuluyor).
			FsmBool taggedFlag;
			if (TryTaggedSiblingDataFlag(p, out taggedFlag)) return true;
			// v24: okunamayan parcayi bir kez yaz (tekrarlama).
			ReportUnreadable(p);
			return false;
		}

		// ------------------------------------------------------------------
		// v24: okunamayan montaj durumu TEŞHİSİ
		// ------------------------------------------------------------------

		static readonly HashSet<string> unreadableReported = new HashSet<string>(
			StringComparer.Ordinal);
		static int unreadableCount;

		/// <summary>
		/// Montaj durumu okunamayan parcayi TEK SEFER loglar.
		///
		/// NEDEN AYRI FONKSIYON: `HasKnownInstallState` `WritePartBatch`
		/// dongusunde 160 parcaya cagrilir; log her seferinde yazilirsa
		/// gonderim dongusunu bogar. Bir kere yaz, sonra sus.
		/// </summary>
		static void ReportUnreadable(SatsumaPart p) {
			try {
				unreadableCount++;
				string id = p.Id;
				if (string.IsNullOrEmpty(id)) id = p.Object != null ? p.Object.name : "?";
				if (!unreadableReported.Add(id)) return;
				string why;
				if (p.InstalledVars.Count == 0 && p.DbInstalled == null && p.DbDamaged == null)
					why = "Data FSM'i yok";
				else if (p.DbDamaged != null)
					why = "Data FSM'i var ama `Installed` bildirmiyor";
				else
					why = "`Installed` bool'u ve `db_*` bayragi yok";
				SmpLog.Warning("montaj durumu OKUNAMAZ, telde gonderilmiyor: '"
					+ id + "' (" + why + ")");
			} catch { }
		}

		/// <summary>v24: okunamayan parca sayisi (snapshot icin).</summary>
		public static int UnreadableCount { get { return unreadableCount; } }

		// ------------------------------------------------------------------
		// v15 sayac / uyari altyapisi (yalnizca bu dosyada)
		// ------------------------------------------------------------------

		/// <summary>
		/// Gelen parca kaydinda HICBIR yazilabilir Installed bool'u
		/// bulunamayan parcayi sayar. Bu kayit "uygulandi" sayilmaz:
		/// `Telemetry.PartFailed` kovasinda tutulur (funnel dengesi:
		/// PartRecords = PartApplied + dustu) ve `smp parts` ozetinde gorunur.
		/// </summary>
		public static int PartsNoWritableFlag;

		/// <summary>
		/// v16: `db_*` cozumlemesinin sectigi AILE. Dokum kaniti:
		/// `db_ThisPart` BoltCheck'te 32/32 ve Removal'da 19/19 vakada
		/// EN SONDA bildiriliyor, yani "ilk cozulen" kurali sistemli
		/// olarak yanlis parcayi seciyordu. Artik oncelik sirasi var ve
		/// secilen aile burada sayilir.
		/// </summary>
		public enum DbFamily { None, Self, Other, Bolt, Guard, Prereq }

		/// <summary>PREREQ/GUARD'a dusmus cozumler. Kendi bayragimiz
		/// olmadigi halde bir onkosulun bayragi secildi.</summary>
		/// <summary>v16 (a) sonrasi BU SAYAC ARTMAZ. Onkosul/guard aileleri
		/// cozum listesinden cikarildi; yalnizca geriye donuk kalir.</summary>
		public static int DbResolvedPrereq;
		public static int DbResolvedBolt;
		public static int DbResolvedOther;
		/// <summary>v16 (a): yazilabilir dogru bayrak bulamadigi icin
		/// cozumun null dondugu parca sayisi. Bunlar TELDE TASINMAZ;
		/// `InstalledOf` sezgisel okumaya duser. `TriggerWheel*_New`
		/// (4 adet) bu kapsamdadir: tek `db_*` degiskenleri
		/// `db_PartRequired1` yani bir onkosul.</summary>
		public static int DbUnresolvedNoSelfFlag;

		/// <summary>v16 (b): gelen yuva indeksi bu makinedeki cozumlenmis
		/// yuva sayisini asiyor. Eski build ya da farkli tablo demektir;
		/// yanlis yuvaya yazmamak icin sayilir.</summary>
		public static int SlotMismatches;

		/// <summary>v18 (a): gelen hasar degeri bu makinede yazilabilir
		/// bir `Damaged` bayragi bulamadigi icin UYGULANAMADI. Yeni build
		/// ya da farkli tablo demektir; yanlis parca hasarlanmamali.</summary>
		public static int DamageUnapplied;

		/// <summary>v20: gelen Database kaydinda hicbir bayrak bagli
		/// degilse (`DbFlagBits == 0`) sayilir. Sessizce gecmek yerine
		/// gorunur kalsin ki kapsam hatasi yakalansin.</summary>
		public static int DatabaseFlagsSkipped;

		/// <summary>
		/// v18 (a): hasari zorla.
		///
		/// DOKUM: `Database/*|Data` `Damaged` bildiriyor; Satsuma'da onu
		/// YAZAN FSM yok, okuyan 12 nokta var. Kayit `Database` satirinda
		/// durdugu icin yalniz oradaki bool'a yaziyoruz.
		///
		/// KAPSAM: bu (a) adimi YALNIZCA `Database/*` satirlarindaki
		/// hasari tasir. Motor simülasyonunun okudugu hasarlar (b) ve
		/// tum `Damaged` satirlari (c) adimlarinda ayrica ele alinir.
		/// Simdilik yazilabilir bayrak yoksa UYGULANMAZ -- yanlis
		/// parcayi hasarlandirmak, hic uygulamamaktan kotudur.
		/// </summary>
		static void ApplyDamage(SatsumaPart part, bool damaged) {
			if (part == null) return;
			if (part.DbDamaged == null) { DamageUnapplied++; return; }
			try { if (part.DbDamaged.Value != damaged) part.DbDamaged.Value = damaged; }
			catch { DamageUnapplied++; }
		}

		/// <summary>
		/// Hedef FSM'in hicbir yerde bildirmedigi olay icin
		/// gonderilemeyen istek sayisi (ASSEMBLE -> DISASSEMBLE cevirisi
		/// kaldirildiktan sonra). Kayit durumu yine yazildigi icin bu bir
		/// kayip degil; gorsel montaj yapilmamis olabilir.
		/// </summary>
		public static int PartEventsNotDeclared;

		/// <summary>"bu olayi bildirmiyor" uyarisini parca basina bir kez ver.</summary>
		static readonly HashSet<string> warnedNoEvent = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>
		/// v15: `db_*` FsmGameObject degiskeni -> `&lt;nesne&gt;/Data`[Data]
		/// -> `Installed` / `Bolted` cozumu.
		///
		/// Neden `InstalledVars`'a degil de buraya bakiliyor: bazi
		/// tetikleyiciler KENDI `Installed` bool'unu bildirmiyor
		/// (orn. trigger_shock_fl). Oyunda `GetFsmBool(db_PartRequired1,
		/// "Data", "Installed")` yaziyorlar. Bu yuzden gercek bayrak
		/// tetikleyicinin DEGISKENINDE degil, onun tuttugu Database
		/// nesnesinde duruyor.
		///
		/// Guvenlik: yalniz FsmGameObject turundeki `db_*` degiskenleri
		/// kabul edilir (`db_ThisPart` cogu yerde FsmString'dir, o yuzden
		/// otomatik elenir) ve hedefin `Data` FSM'inden `Installed`
		/// cozulemediginde sessizce vazgecilir -- sezgisel okumaya duser.
		///
		/// Cozum bir kez yapilir ve `p`'ye yazilir; `InstalledOf` her
		/// part icin her flush'ta cagrildigi icin her seferinde taranmaz.
		/// </summary>
		/// <summary>
		/// v16 (a): `db_*` cozumleme ONCELIK SIRASI.
		///
		/// NEDEN BU SIRa: dokum kaniti. Bildirilen `db_*` degiskenleri
		/// daima aile sirasiyla gelir ve KENDI referansimiz (`db_ThisPart`)
		/// daima EN SONDA:
		///   BoltCheck 32/32   BOLT,BOLT,BOLT,SELF
		///   Removal   19/19   PREREQ,[PREREQ],SELF
		///   Assembly  30      PREREQ->SELF / GUARD,GUARD,PREREQ,SELF ...
		/// Yani "ilk cozulen" kurali sistematik olarak YANLIS parcayi
		/// seciyordu (229 FSM'nin 75'inde).
		///
		/// GUARD ve PREREQ BILEREK LISTEDE YOK. Onlar baska parcanin
		/// bayragi -- `db_PartRequired1` (17 Assembly), `db_RemoveReq1`
		/// (17 Removal), `db_NotInstalled` (11 Assembly). Onceki surumde
		/// bunlar "son care" olarak secilebiliyordu; (a) duzeltmesi bunu
		/// kaldiriyor.
		///
		/// (a) DUZELTMESININ KANITI: `TriggerWheel{F,L,R,R}_New` adli 4
		/// trigger'in hic kendi yuva referansi yok. Tek `db_*` degiskenleri
		/// `db_PartRequired1`, yani tamamen bir ONKOSUL. Dokumda bildirilen
		/// bloklari: `Required1, Setup, Corner, FL, Name, Bolts,
		/// NoTireCollider, Owner, Part, Pivot1, Pivot2, ThisWheel,
		/// db_PartRequired1, WheelScript, Wheel` -- yerel `Installed`
		/// YOK. `PART1`/`PART2` olaylari `Pivot1`/`Pivot2` ile eslesiyor,
		/// yani donanim varyanti secimi, yuva degil.
		///
		/// Bu 4 trigger icin yazilabilir dogru bir bayrak YOKTUR. Onlara
		/// bir onkosulun bayragini yazmak, o parcanin montaj durumunu
		/// bozmak demek. Cozum `null` doner; `InstalledOf` sezgisel
		/// okumaya duser ve `ApplyPartState` "yazilabilir bayrak yok"
		/// diye sayar (PartsNoWritableFlag).
		/// </summary>
		static readonly DbFamily[] PRIORITY = {
			DbFamily.Self,      // db_ThisPart, db_ThisPart1, db_ThisPart2
			DbFamily.Other,     // db_Radiator1/2, db_Part1/2, db_WheelGT/...
			DbFamily.Bolt,      // db_BoltThis1..3 (yalniz BoltCheck'te)
			// GUARD ve PREREQ KASITLI OLARAK ATLANDI: baska parcanin
			// bayragi, kendimizinki degil. Yazmak yanlis.
		};

		/// <summary>v16 (b): cozumleyen FSM'in adi. Kablolama (`Assemble`)
		/// ile montaj (`Assembly`) ayrimi icin gerekli: kablolama
		/// ucunda iki `db_ThisPart*` = kablo ucu, yuva degil.</summary>
		static string fsmNameOf(SatsumaPart p) {
			try {
				if (p == null) return null;
				if (p.Assembly != null) return p.Assembly.FsmName;
				if (p.Removal != null) return p.Removal.FsmName;
				if (p.Assemble != null) return p.Assemble.FsmName;
				if (p.BoltCheck != null) return p.BoltCheck.FsmName;
			}
			catch { }
			return null;
		}

		/// <summary>
		/// v25: parcanin `db_*` bildirebilecegi TUM FSM'ler, oncelik sirasiyla.
		///
		/// NEDEN AYRI FONKSIYON: `DatabaseInstalledFlag` ic ice iki gecis
		/// tasiyor (aile -> cozum). Holder listesini uretmek icin ayri
		/// fonksiyon, siranin NEDEN boyle oldugunu tek yerde belgeler.
		///
		/// SIRALAMA KANITI (dokum):
		///   1. `Assembly`/`Removal` -- `db_ThisPart` (`Self`) burada.
		///      102 `Assembly`, 60 `Removal`; `db_ThisPart` 256 kez.
		///   2. `BoltCheck` -- `db_BoltThis1..3` (`Bolt`) burada, 32 FSM.
		///      Onceki `else if` zinciri bunu HICBIR ZAMAN okumuyordu.
		///   3. `Assemble` -- kablolama; `db_ThisPart2` kablo UCU demek
		///      (v16 karari), yuva degil. Onceden bu her zaman sona
		///      birakilirdi; simdi de onceligi dusuk.
		///
		/// DONDUGU ICIN `Data` FSM'i DIŞARIDA: dokumde Satsuma altindaki
		/// 7 `Data` FSM'inin HICBIRI `db_` bildirmiyor (dogrulandi), ve
		/// bayrak (`Installed/Bolted/Damaged`) da bildirmiyor. Eklemek
		/// bos bir tarama olurdu.
		/// </summary>
		static PlayMakerFSM[] HolderFsms(SatsumaPart p) {
			if (p == null) return null;
			int n = 0;
			if (p.Assembly != null) n++;
			if (p.Removal != null) n++;
			if (p.BoltCheck != null) n++;
			if (p.Assemble != null) n++;
			if (n == 0) return null;
			PlayMakerFSM[] r = new PlayMakerFSM[n];
			int i = 0;
			if (p.Assembly != null) r[i++] = p.Assembly;
			if (p.Removal != null) r[i++] = p.Removal;
			if (p.BoltCheck != null) r[i++] = p.BoltCheck;
			if (p.Assemble != null) r[i++] = p.Assemble;
			return r;
		}

		static FsmBool DatabaseInstalledFlag(SatsumaPart p) {
			if (p == null) return null;
			if (p.DbInstalledChecked) return p.DbInstalled;
			p.DbInstalledChecked = true;

			// v25: ART ARDA DEGIL -- TUM HOLDERS taranir.
			//
			// KULLANICI ISTEGI: "tüm db_ ile başlayan satsuma parçalarını,
			// triggerlerini herşeyi senkronizeye dahil et".
			//
			// ESKI KOD `if / else if` idi: `Assembly` varsa `BoltCheck`
			// HICBIR ZAMAN bakilmazdi. Dokum bunun bedelini gosteriyor:
			//   * 32 `BoltCheck` `db_BoltThis1/2/3` bildiriyor
			//   * 32 `db_*` bildiren `db_BoltThis3` DUNYA GENELINDE
			//     0 kez KULLANILIYOR (onceki turun tespiti)
			// yani `Bolt` ailesi `PRIORITY`'de olmali, `Assembly` onu
			// gizliyordu.
			//
			// ONCELIK KORUNUYOR: `db_ThisPart` (`Self`) her zaman bir
			// `db_BoltThis*` gecisinden ONCE bakilir. `Self` bir parcanin
			// KENDI durumu, `Bolt` grubun durumu -- ikisi de yazilabilir
			// ama biri dogruysa o secilir.
			PlayMakerFSM[] holders = HolderFsms(p);
			if (holders == null) return null;

			for (int h = 0; h < holders.Length; h++) {
				PlayMakerFSM fsm = holders[h];
				if (fsm == null || fsm.FsmVariables == null) continue;
				FsmGameObject[] gos;
				try { gos = fsm.FsmVariables.GameObjectVariables; } catch { continue; }
				if (gos == null) continue;
				// Once tum adaylari topla, sonra ONCELIK SIRASI ile sec.
				// (Ic ice iki gecis: 1) aile siniflandirmasi 2) ilk gecerli
				// hedefi cozme.)
				FsmBool best = null;
				string bestName = null;
				GameObject bestTarget = null;
				DbFamily bestFam = DbFamily.None;

				for (int pass = 0; pass < PRIORITY.Length; pass++) {
					DbFamily want = PRIORITY[pass];

					// v16 (b): cok yuvali parcalarda TUM yuva adlarini topla.
					//
					// HANGI AILE yuva adayi olabilir? IKISI DE:
					//   Other : db_Radiator1/2, db_Part1/2,
					//           db_WheelGT/Rally/Sport/Stock
					//   Self  : db_ThisPart + db_ThisPart2 (trigger_hood),
					//           db_ThisPart1 + db_ThisPart2 (trigger_gear_stick)
					// Ikisi de yuva LISTESIDIR. Onceki surum yuvayi yalniz
					// `Other` icinde ariyordu, bu yuzden gear_stick ve hood
					// TEK yuvali sayiliyordu ve ikinci yuva hic tasinmiyordu.
					//
					// Kural: ayni ailede 2+ cozulebilir ad varsa hepsi
					// yuva sayilir. Ilki `db_` adinin PART sirasina gore
					// gelir (dokumda bildirim sirasi da boyle).
					if ((want == DbFamily.Other || want == DbFamily.Self)
						&& p.SlotFlags.Count == 0) {
						List<FsmBool> slots = new List<FsmBool>(4);
						List<string> slotNames = new List<string>(4);
						for (int i = 0; i < gos.Length; i++) {
							FsmGameObject g = gos[i];
							if (g == null) continue;
							string n = null;
							try { n = g.Name; } catch { }
							if (string.IsNullOrEmpty(n)) continue;
							if (!n.StartsWith("db_", StringComparison.Ordinal)) continue;
							if (DbFamilyOf(n) != want) continue;
							GameObject t = null;
							try { t = g.Value; } catch { }
							if (t == null) continue;
							PlayMakerFSM d = null;
							try { d = t.GetComponent<PlayMakerFSM>(); } catch { }
							if (d == null) continue;
							FsmBool b = FsmNames.Bool(d, "Installed");
							if (b == null) continue;
							slots.Add(b);
							slotNames.Add(n);
						}
						if (slots.Count > 0) {
							// Onceki donusum: tek ad -> ana bayrak (eski
							// davranis). Cok ad -> yuva listesi.
							p.DbInstalled = slots[0];
							p.DbResolvedFamily = want;
							if (want == DbFamily.Other) DbResolvedOther++;
							if (slots.Count > 1) {
								// v16 (b): KABLO UCU mu, YUVA mi?
								//
								// Dokum kaniti: `Assemble` (kablolama) FSM'leri
								// de `db_ThisPart` + `db_ThisPart2` bildiriyor, ama
								// bildirilen bloktaki komsular SU:
								//   BatteryPlusHarness|Assemble:
								//     Init, Distance, Tolerance, Owner,
								//     ThisWire1, ThisWire2, _OtherEnd,
								//     db_ThisPart, db_ThisPart2
								// Bunlar birbirinin YERINE gecen iki parca
								// degil, TEK kablonun IKI ucu (`ThisWire1`/
								// `ThisWire2`, `_OtherEnd` = karsi uc).
								// `Assembly` FSM'lerinde ise komsular:
								//   trigger_gear_stick: HandChild1, gear stick,
								//     HandChild2, center console gt, Name,
								//     ActivateThis1, ActivateThis2, Part, ...
								//   trigger_radiator: HandChild, radiator,
								//     HandChild2, racing radiator, ...
								// -> `HandChild`+`ActivateThis*` = yuva.
								//
								// Kablolama ucunda ikisini birden yazmak
								// HICBIR SEYI DUZELTMEZ, sadece iki ucu da
								// zorlar (biri zaten dogruysa digeri bozulur).
								// Bu yuzden yuva sayilmaz: yalnizca ilki
								// yazilir (eski davranis).
								bool isWiring = fsmNameOf(p) == "Assemble";
								if (!isWiring) {
									p.SlotFlags = slots;
									p.SlotNames = slotNames;
									if (Telemetry.Verbose)
										SmpLog.Debug("satsuma", "slot", "'" + p.Id + "' "
											+ slots.Count + " yuva: "
											+ string.Join(", ", slotNames.ToArray()));
								}
								else if (Telemetry.Verbose) {
									SmpLog.Debug("satsuma", "slot", "'" + p.Id + "' "
										+ slots.Count + " kablo ucu (Assemble FSM) -- YUVA DEGIL, "
										+ "yalniz ilki yazilir: " + slotNames[0]);
								}
							}
							return p.DbInstalled;
						}
					}

					for (int i = 0; i < gos.Length; i++) {
						FsmGameObject g = gos[i];
						if (g == null) continue;
						string n = null;
						try { n = g.Name; } catch { }
						if (string.IsNullOrEmpty(n)) continue;
						if (!n.StartsWith("db_", StringComparison.Ordinal)) continue;
						if (DbFamilyOf(n) != want) continue;
					// "NotInstalled" gibi ada sahip olanlar hedef DEGIL:
					// oyun "bu parca kurulu degil" durumu icin ayri
					// referanslar tutuyor. Yalniz icinde gercek bir
					// Data/Installed bulunanlari kabul ediyoruz.
						GameObject target = null;
						try { target = g.Value; } catch { }
						if (target == null) continue;
						PlayMakerFSM data = null;
						try { data = target.GetComponent<PlayMakerFSM>(); } catch { }
						if (data == null) continue;
						FsmBool inst = FsmNames.Bool(data, "Installed");
						if (inst == null) continue;

						best = inst;
						bestName = n;
						bestTarget = target;
						bestFam = want;
						break;   // bu gecisin en iyi adayi
					}
					if (best != null) break;
				}

				if (best != null) {
					p.DbInstalled = best;
					p.DbResolvedFamily = bestFam;
					if (bestFam == DbFamily.Bolt) DbResolvedBolt++;
					else if (bestFam == DbFamily.Other) DbResolvedOther++;
					else if (bestFam == DbFamily.Guard || bestFam == DbFamily.Prereq)
						DbResolvedPrereq++;
					// DENETIM SATIRI (v15/v16). Cozum BIR kez yapilir
					// (DbInstalledChecked) yani parca basina bir kez.
					if (Telemetry.Verbose)
						SmpLog.Debug("satsuma", "dbflag", "'" + p.Id + "' Installed -> "
							+ bestName + " [" + bestFam + "] = '"
							+ (bestTarget != null ? bestTarget.name : "?")
							+ "':Data[Installed]");
					return best;
				}
				// v16 (a): SELF/OTHER/BOLT hicbiri cozulmedi. Onkosul ve
				// guard aileleri KASITLI OLARAK denenmiyor (baska
				// parcanin bayragi). Sayilir ki bu durum sessizce
				// gecmesin -- `TriggerWheel*_New` (4) buraya duser.
				DbUnresolvedNoSelfFlag++;
				if (Telemetry.Verbose) {
					string[] names = null;
					int nn = 0;
					try {
						if (fsm != null && fsm.FsmVariables != null
							&& fsm.FsmVariables.GameObjectVariables != null) {
							FsmGameObject[] gg = fsm.FsmVariables.GameObjectVariables;
							int cnt = 0;
							for (int q = 0; q < gg.Length; q++) {
								if (gg[q] == null) continue;
								string qn = null;
								try { qn = gg[q].Name; } catch { }
								if (qn != null && qn.StartsWith("db_", StringComparison.Ordinal)) cnt++;
							}
							names = new string[cnt];
							for (int q = 0, w = 0; q < gg.Length; q++) {
								if (gg[q] == null) continue;
								string qn = null;
								try { qn = gg[q].Name; } catch { }
								if (qn == null || !qn.StartsWith("db_", StringComparison.Ordinal)) continue;
								if (w < cnt) names[w++] = qn;
							}
							nn = cnt;
						}
					}
					catch { }
					SmpLog.Debug("satsuma", "dbflag", "'" + p.Id + "' Installed -> YOK. "
						+ "kendi db_* referansi bulunamadi (istenen aile: Self/Other/Bolt). "
						+ "bildirilen db_*: " + (names == null ? "(okunamadi)"
							: string.Join(", ", names))
						+ (nn > 0 ? " [hepsi onkosul/bolts turunden, YAZILMAZ]" : ""));
				}
			}
			return null;
		}

		/// <summary>
		/// v16: `db_*` adini aileye cevirir. Dokumdeki bildirim sirasi
		/// bu sirayi zorunlu kiliyor:
		///   BoltCheck 32/32  BOLT,BOLT,BOLT,SELF
		///   Removal   19/19  PREREQ,[PREREQ],SELF
		/// Yani kendi referansimiz (`db_ThisPart*`) daima son sirdadir.
		/// </summary>
		static DbFamily DbFamilyOf(string name) {
			if (string.IsNullOrEmpty(name)) return DbFamily.None;
			if (name.StartsWith("db_ThisPart", StringComparison.Ordinal))
				return DbFamily.Self;
			if (name.StartsWith("db_BoltThis", StringComparison.Ordinal))
				return DbFamily.Bolt;
			if (name.IndexOf("NotInstalled", StringComparison.OrdinalIgnoreCase) >= 0)
				return DbFamily.Guard;
			if (name.StartsWith("db_PartRequired", StringComparison.Ordinal)
				|| name.StartsWith("db_RemoveReq", StringComparison.Ordinal)
				|| name.StartsWith("db_Required", StringComparison.Ordinal))
				return DbFamily.Prereq;
			return DbFamily.Other;
		}

		/// <summary>
		/// Trigger kendisi açık ama hiyerarşide kapalı (ebeveyn kapalı):
		/// FSM hiç çalışmadı. Parça arabada değil, önkoşul noktasında bekliyor.
		/// </summary>
		static bool IsNeverRan(SatsumaPart p) {
			try {
				if (p.Object == null) return false;
				return p.Object.activeSelf && !p.Object.activeInHierarchy;
			}
			catch { return false; }
		}

		/// <summary>
		/// Verilen nesne takılı mı? Nesnenin kendisi ya da en yakın installable
		/// ebeveyni için bakılır.
		///
		/// KULLANIM: Satsuma'nın CD/radyo parçaları. Kullanıcı bilgisi: "cdplayer
		/// ve diğer satsuma radyosu araca takılınca aktif oluyor, garajda duruyor".
		/// Yani ikisi de sahnede ama yalnızca ARAYA TAKILAN radyo gerçekten
		/// çalıyor; yedek olanın değişkenleri kabloya gönderilmemeli.
		/// </summary>
		public static bool InstalledOfObject(GameObject go) {
			if (go == null) return false;
			SatsumaPart p;
			string id = IdOf(go);
			if (byId.TryGetValue(id, out p) && p != null && p.Installable) return InstalledOf(p);
			Transform cur = null;
			try { cur = go.transform != null ? go.transform.parent : null; } catch { }
			int guard = 0;
			while (cur != null && guard++ < 8) {
				if (cur.gameObject == Root) break;
				string pid = IdOf(cur.gameObject);
				if (byId.TryGetValue(pid, out p) && p != null && p.Installable) return InstalledOf(p);
				cur = cur.parent;
			}
			return false;
		}

		/// <summary>Installed parts, counted over installable entries only.</summary>
		public static int InstalledCount() {
			int n = 0;
			for (int i = 0; i < parts.Count; i++) {
				if (parts[i].Installable && InstalledOf(parts[i])) n++;
			}
			return n;
		}

		public static int PendingParts {
			get {
				int n = 0;
				for (int i = 0; i < parts.Count; i++) {
					if (!string.IsNullOrEmpty(parts[i].PendingEvent) || parts[i].StateDirty) n++;
				}
				return n;
			}
		}

		public static int DirtyBolts {
			get {
				int n = 0;
				for (int i = 0; i < parts.Count; i++) {
					for (int b = 0; b < parts[i].Bolts.Count; b++) {
						if (parts[i].Bolts[b].Dirty) n++;
					}
				}
				return n;
			}
		}

		/// <summary>
		/// Tanı: takılı-sayımının neden 0 okuduğunu bulmak için gerçek veriyi döker.
		/// Her installable parça için InstalledVars değerleri + Assembly/Removal aktif
		/// durum adları gezilir; filtre yoksa özet + durum histogramı basılır (391 satır
		/// konsolu doldurmasın), filtre varsa eşleşenler tek tek dökülür (en çok 40).
		/// Histogram, gerçek "takılı" durum adını TAHMİN etmeden ÖĞRENMEK içindir:
		/// full toplu arabada hangi state adları çoğunlukta, ona bakılır.
		/// </summary>
		public static void DumpPartStates(string filter) {
			if (parts.Count == 0) {
				SmpLog.Print("satsuma: no table yet (load a save, then 'smp rescan').");
				return;
			}
			// Liste anahtarları: 'smp parts installed' takılıları,
			// 'smp parts missing' sökükleri döker (en çok 400 satır).
			bool wantInstalled = string.Equals(filter, "installed", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(filter, "takili", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(filter, "on", StringComparison.OrdinalIgnoreCase);
			bool wantMissing = string.Equals(filter, "missing", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(filter, "removed", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(filter, "sokuk", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(filter, "off", StringComparison.OrdinalIgnoreCase);
			// Tam dokum: 'smp parts all' tablodaki HER girdiyi tek satir
			// yazar (391 satir; konsol kaydirir, log dosyasi tutar).
			bool wantAll = string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(filter, "komple", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(filter, "hepsi", StringComparison.OrdinalIgnoreCase);
			bool listMode = wantInstalled || wantMissing || wantAll;
			bool hasFilter = !string.IsNullOrEmpty(filter) && !listMode;
			Dictionary<string, int> asmStates = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, int> remStates = new Dictionary<string, int>(StringComparer.Ordinal);
			int installable = 0, withVars = 0, installedTrue = 0, matched = 0, shown = 0;
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				if (p == null || (!p.Installable && !wantAll)) continue;
				if (wantAll && !p.Installable) {
					// Civata kabi: durum sayimina girmez, listede (c) isaretiyle.
					matched++;
					if (shown < 500) {
						shown++;
						SmpLog.Print("  (c) " + p.Id
							+ " bolts=" + p.TightBoltCount + "/" + p.Bolts.Count);
					}
					continue;
				}
				installable++;
				string asm = ActiveOf(p.Assembly);
				string rem = ActiveOf(p.Removal);
				Bump(asmStates, asm);
				Bump(remStates, rem);
				if (p.InstalledVars.Count > 0) withVars++;
				bool inst = InstalledOf(p);
				if (inst) installedTrue++;
				if (listMode) {
					if (!wantAll && ((wantInstalled && !inst) || (wantMissing && inst))) continue;
					matched++;
					if (shown >= 500) continue;
					shown++;
					SmpLog.Print("  " + (inst ? "[x] " : "[ ] ") + p.Id
						+ " bolts=" + p.TightBoltCount + "/" + p.Bolts.Count);
					continue;
				}
				if (hasFilter && (p.Id == null
					|| p.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
				matched++;
				if (!hasFilter || shown >= 40) continue;
				shown++;
				SmpLog.Print("  " + p.Id + " installed=" + (inst ? "T" : "F")
					+ " vars=" + VarsOf(p)
					+ " asm='" + asm + "' rem='" + rem + "'"
					+ " bolts=" + p.TightBoltCount + "/" + p.Bolts.Count
					+ " go=" + GoFlags(p)
					+ " parent='" + ParentOf(p) + "'");
			}
			SmpLog.Print("satsuma parts: " + installable + " installable, "
				+ installedTrue + " installed, "
				+ withVars + " declare 'Installed', "
				+ databaseFlagCount + " from Database/* flags, "
				+ PartsNoWritableFlag + " remote state(s) with no writable flag, "
				+ StorePartCount + " non-Satsuma part(s) on the store batch, "
				+ DatabaseFlagPartCount + " Database/* flag row(s) on the database batch.");
			// v16: db_* cozumleme dagilimi. PREREQ/GUARD dusenler bizim
			// bayragimiz DEGIL demektir -- yuksek sayi sorun isareti.
			SmpLog.Print("  db_* cozumleme: Self=" + (databaseFlagCount
					- DbResolvedPrereq - DbResolvedBolt - DbResolvedOther)
				+ " Other=" + DbResolvedOther
				+ " Bolt=" + DbResolvedBolt
				+ " | ONKOSUL'A DUSEN=" + DbResolvedPrereq
				+ " (yazilmaz, hep 0 kalmali)"
				+ " | KENDI BAYRAGI YOK=" + DbUnresolvedNoSelfFlag
				+ " (TriggerWheel*_New dahil, telde tasinmiyor)");
			SmpLog.Print("  assembly states: " + HistOf(asmStates));
			SmpLog.Print("  removal states: " + HistOf(remStates));
			if (listMode) {
				SmpLog.Print("  " + (wantInstalled ? "installed" : (wantMissing ? "missing" : "all"))
					+ ": " + matched + " part(s)"
					+ (matched > shown ? ", first " + shown + " shown." : "."));
				return;
			}
			if (hasFilter) {
				SmpLog.Print("  filter '" + filter + "': " + matched + " match(es)"
					+ (matched > shown ? ", first " + shown + " shown." : "."));
				return;
			}
			// Filtresiz modda 'Installed' deklare edenler tek tek dökülür
			// (31 adet): bool'u olan ama false okunan parçanın asm/rem durumu
			// ve parent'ı, ikinci tur düzeltmenin verisidir.
			SmpLog.Print("  parts declaring 'Installed':");
			int listed = 0;
			for (int i = 0; i < parts.Count && listed < 40; i++) {
				SatsumaPart p = parts[i];
				if (p == null || !p.Installable || p.InstalledVars.Count == 0) continue;
				listed++;
				bool inst = InstalledOf(p);
				SmpLog.Print("  " + p.Id + " installed=" + (inst ? "T" : "F")
					+ " vars=" + VarsOf(p)
					+ " asm='" + ActiveOf(p.Assembly) + "' rem='" + ActiveOf(p.Removal) + "'"
					+ " bolts=" + p.TightBoltCount + "/" + p.Bolts.Count
					+ " go=" + GoFlags(p)
					+ " parent='" + ParentOf(p) + "'");
			}
		}

		static string ActiveOf(PlayMakerFSM fsm) {
			if (fsm == null) return "-";
			try {
				HutongGames.PlayMaker.Fsm inner = fsm.Fsm;
				if (inner == null) return "?innerNull";
				string n = inner.ActiveStateName;
				return string.IsNullOrEmpty(n) ? "?empty" : n;
			}
			catch (System.Exception e) { return "?" + e.GetType().Name; }
		}

		/// <summary>
		/// Objenin aktiflik bayrakları: "1/1" = açık. Kuram: takılan parçanın
		/// trigger'ı oyun tarafından KAPATILIR (Assembly FSM'i hiç başlamaz →
		/// ActiveState null → okuma hatası '?'). Yani asm='?' + go=0/0 büyük
		/// olasılıkla TAKILI demektir; go=1/1 + "Check for Part collision" ise
		/// takılmayı bekleyen demektir. Bu sütun kuramı doğrulamak içindir.
		/// </summary>
		static string GoFlags(SatsumaPart p) {
			try {
				if (p.Object == null) return "?/?";
				GameObject go = p.Object;
				return (go.activeSelf ? "1" : "0") + "/"
					+ (go.activeInHierarchy ? "1" : "0");
			}
			catch { return "?/?"; }
		}

		static string VarsOf(SatsumaPart p) {
			if (p.InstalledVars.Count == 0) return "[]";
			System.Text.StringBuilder sb = new System.Text.StringBuilder(16);
			sb.Append('[');
			for (int i = 0; i < p.InstalledVars.Count; i++) {
				if (i > 0) sb.Append(',');
				bool v = false;
				try { v = p.InstalledVars[i] != null && p.InstalledVars[i].Value; }
				catch { v = false; }
				sb.Append(v ? 'T' : 'F');
			}
			sb.Append(']');
			return sb.ToString();
		}

		static string ParentOf(SatsumaPart p) {
			try {
				if (p.Object == null) return "?";
				Transform t = p.Object.transform;
				if (t == null || t.parent == null) return "(root)";
				return t.parent.gameObject != null ? t.parent.gameObject.name : "?";
			}
			catch { return "?"; }
		}

		static void Bump(Dictionary<string, int> hist, string key) {
			if (string.IsNullOrEmpty(key)) key = "?";
			int n = 0;
			hist.TryGetValue(key, out n);
			hist[key] = n + 1;
		}

		static string HistOf(Dictionary<string, int> hist) {
			if (hist.Count == 0) return "(none)";
			System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
			bool first = true;
			foreach (KeyValuePair<string, int> kv in hist) {
				if (!first) sb.Append(", ");
				first = false;
				sb.Append(kv.Key).Append('=').Append(kv.Value);
			}
			return sb.ToString();
		}

		public static void DumpParts() {			if (parts.Count == 0) {
				SmpLog.Log("no part table: load a save first.");
				return;
			}
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				SmpLog.Log(" "+ p.Id + "  installed=" + p.Installed
					+ " bolts=" + p.Bolts.Count + "/" + p.TightBoltCount + " tight"
					+ (p.Installable ? "" : " [bolt container]")
					+ (p.Assembly != null ? " [Assembly]" : "")
					+ (p.Removal != null ? " [Removal]" : "")
					+ (p.Assemble != null ? " [Assemble]" : "")
					+ (p.BoltCheck != null ? " [BoltCheck]" : ""));
			}
		}

		public static void DumpBolts() {
			int n = 0;
			for (int i = 0; i < parts.Count; i++) {
				SatsumaPart p = parts[i];
				for (int b = 0; b < p.Bolts.Count; b++) {
					SatsumaBolt bolt = p.Bolts[b];
					SmpLog.Log(" "+ p.Id + " #" + bolt.Index
						+ " stage=" + bolt.StageValue + " tightness=" + bolt.TightnessValue.ToString("F2")
						+ (bolt.Dirty ? " dirty" : ""));
					n++;
				}
			}
			if (n == 0) SmpLog.Log("no bolts in the table.");
		}
	}

	/// <summary>
	/// Reference-identity comparer. PlayMaker variables are mutable objects, so value
	/// equality would be wrong if the type ever grew one.
	/// </summary>
	public class RefComparer<T> : IEqualityComparer<T> where T : class {
		public static readonly RefComparer<T> Instance = new RefComparer<T>();
		public bool Equals(T a, T b) { return ReferenceEquals(a, b); }
		public int GetHashCode(T obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
	}
}