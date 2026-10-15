using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// GODMODE — ölümleri TÜRÜNE BAKMAKSIZIN engeller.
	///
	/// Neden FSM'i devre dışı bırakmak
	/// --------------------------------
	/// MSC'de ölüm tek bir PlayMaker FSM'idir (Systems/Death, "Activate Dead
	/// Body"). Tüm ölüm sebepleri — WASP/DRIVEBEE (arı), CRASH, FIRE, DROWN,
	/// HUNGER, MURDER, RUNOVER... — aynı FSM'in transition'larıdır
	/// (DeathSync.Describe'da tam liste). Bu yüzden tek noktadan kesmek
	/// yeterli: FSM kapalıyken hiçbir ölüm event'i işlenemez, GameOverScreen
	/// açılmaz, "Delete saves" (permadeath) çalışmaz, kamera ölüm efekti
	/// uygulanmaz, ölü beden aktive olmaz.
	///
	/// ÖLÜM HİÇ TAMAMLANMAZ, oyuncu haritaya geri döner
	/// ----------------------------------------------
	/// FSM'i kapatmak tek başına yetmiyordu: (a) ölüm FSM'i çoğu zaman PASİF
	/// bir nesnede duruyor ve GameObject.Find pasif nesneleri bulmaz — canlı
	/// logdaki "'Systems/Death' ustunde 0 FSM" satırı tam olarak buydu;
	/// (b) FSM kapatılsa bile kan ekranı ve Game Over ekranı doğrudan nesne
	/// üzerinden açılabiliyor; (c) ölüm olayı FSM'e yine gönderilebiliyor.
	///
	/// Bu yüzden ölüm DÖRT sinyalden yakalanır ve hepsi tek bir yol
	/// (<see cref="HandleDeath"/>) üzerinden işlenir:
	///
	///   1. ölüm FSM'inin durumu başlangıçtan ayrıldıysa,
	///   2. ölüm FSM'ine bir OLAY gönderildiyse (Harmony öneki olayı
	///      YÜRÜTMEZ: sadece yakalar),
	///   3. Game Over ekranı açıldıysa,
	///   4. kan ekranı göründüyse ya da oyuncu haritanın altına düştüyse.
	///
	/// Tepki her durumda aynıdır: FSM temiz başlangıca çekilir, ekran
	/// bastırılır ve oyuncu HARİTADAKİ İLK GÜVENLİ KONUMA ışınlanır. Seçilen
	/// davranış gereği konum ve kan/ekran dışında hiçbir şey sıfırlanmaz:
	/// ihtiyaçlar, para ve araç durumu aynen korunur.
	///
	/// GC-free: keşif bir kez yapılır; kare başına
	/// tahsis yok. Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class GodMode {

		/// <summary>Açık mı? 'smp god on|off' ile değişir. Varsayılan AÇIK.</summary>
		public static bool Enabled = true;

		/// <summary>Engellenen ölüm state'i sayısı (teşhis).</summary>
		public static int Blocked;


		/// <summary>Son güvenli konum (yere basarken güncellenir) — geçmiş.</summary>
		static Vector3 lastSafe;
		static bool hasSafe;
		static float nextSample;
		static float lastFailsafe;
		static float lastReload;

		/// <summary>Son ölüm müdahalesinin zamanı (saniye).</summary>
		static float lastDeathHandled;

		/// <summary>
		/// Ölüm FSM'i en az bir kez BAŞLANGIÇ durumunda görüldü mü?
		///
		/// NEDEN GEREKLİ: "durum başlangıçtan farklı" tek başına ölüm
		/// kanıtı değildir. FSM'in normal dinlenme durumu başka bir ad
		/// olabilir (o zaman oyuncu her kare doğuş noktasına ışınlanırdı —
		/// yüklemede anında yankılanma). Doğru sıra: ÖNCE start state'i
		/// GÖR, SONRA oradan ayrılmayı ölüm say.
		///
		/// Sınır: bu kapı yanlış pozitifi sıfırlar; bedeli, start state
		/// hiç görülmezse durum temelli tespitin kapanmasıdır. Olay,
		/// Game Over ekranı ve kan ekranı katmanları o durumda da çalışır.
		/// </summary>
		static bool sawStartState;

		/// <summary>Yanlış dinlenme durumu uyarısı bir kez verilir.</summary>
		static bool warnedWrongStart;

		/// <summary>Kamera ölüm FSM'i bulunamadıysa uyarı BİR kez verilir.</summary>
		static bool loggedCameraDeathMissing;

		/// <summary>
		/// Ölüm müdahaleleri arasındaki en kısa süre (saniye).
		///
		/// NEDEN GEREKLİ: dört sinyal aynı ölümü ayrı ayrı haber verebilir
		/// (FSM durumu + olay + ekran + kan). Sınır olmasa tek bir ölüm
		/// saniyede onlarca ışınlanmaya ve log satırına dönüşürdü.
		/// </summary>
		const float DEATH_COOLDOWN = 1f;

		/// <summary>Haritanın üstündeki İLK iyi konum = "ilk spawn" noktası.
		/// Ölümde oyuncuyu buraya ışınlarız (map başlangıcı gibi).</summary>
		static Vector3 firstSpawn;
		static bool hasFirstSpawn;

		/// <summary>Kan eşiği: PlayerBlood aktif ve projektör boyutu buna yaklaşırsa "öldü" sayılır.</summary>
		const float BLOOD_THRESHOLD = 0.05f;
		static float nextBloodCheck;
		/// <summary>Önbellek boşken yeniden tarama zamanı (saniye).</summary>
		static float nextBloodScan;

		/// <summary>Kan efektinin bir project'örü ve ait olduğu nesne.</summary>
		struct BloodCone {
			public GameObject go;
			public Projector proj;
		}

		/// <summary>
		/// Kan efektinin project'örleri. Sahne başına BİR KEZ bulunur.
		///
		/// NEDEN ÖNBELLEK: eski kod her yoklamada tüm sahneyi
		/// (Resources.FindObjectsOfTypeAll) geziyor ve yeni bir dizi
		/// ayırıyordu; bu yüzden yoklama 1 Hz'e mecburdu ve ölüm ile ışınlanma
		/// arasında 1-2.5 saniye "ölü" ekran kalıyordu (canlı log: 00:11:32'de
		/// spawn yakalandı, 00:12:45'te ışınlanıldı). Önbellekle yoklama
		/// maliyeti project'ör sayısına iner ve 4 Hz'e çıkabilir.
		/// </summary>
		static readonly List<BloodCone> bloodCones = new List<BloodCone>();

		/// <summary>Kaç kez ışınlama yapıldı — TÜM sebepler (teşhis).</summary>
		public static int FailsafeRespawns;

		/// <summary>
		/// Kaç kez ÖLÜM yakalanıp oyuncu doğuş noktasına ışınlandı.
		///
		/// Sebepler: ölüm FSM'inin durum değişimi, ölüm FSM'ine gelen olay,
		/// Game Over ekranının açılması, kan ekranı. Yalnız haritadan düşme
		/// kurtarması buraya SAYILMAZ (o bir ölüm değil, konum kurtarması).
		/// </summary>
		public static int DeathRespawns;

		/// <summary>
		/// Ölüm FSM'inin başlangıç durumu.
		///
		/// DUMPER KANITI: Systems/Death [Activate Dead Body] -> start = "Permadeath 2".
		/// Önceden keşif sırasında öğreniliyordu; ölüm senkronu silindiği
		/// için artık sabit ve dökümle doğrulanmış bir değer.
		///
		/// Public: DEATH_OBJECT / DEATH_FSM ile aynı ailede duruyor ve
		/// tests\DeathProbe.cs bu üçlünün oyun dökümüyle aynı kaldığını
		/// doğruluyor. Biri sessizce değişirse GodMode.Discover FSM'i
		/// bulamaz ve godmode hiçbir ölümü engelleyemez.
		/// </summary>
		public const string START_STATE = "Permadeath 2";

		/// <summary>Ölüm senkronu v15'te kaldırıldı; bu bir HİLEDİR.</summary>
		/// <remarks>
		/// GodMode, sabit ihtiyaçlar/sabit para ile aynı aileden bilinçli
		/// bir cheat olduğu için KORUNDU. Silinen şey ölümün AĞ üzerinden
		/// senkronize edilmesiydi: ölüm paketleri zaten gönderilmiyordu
		/// (IsDead/IsUnconscious "PlayerState" adlı bir FSM'den okunuyordu;
		/// 6548 kayıtta böyle bir FSM yok) ve alıcı tarafı da simülasyona
		/// yazamıyordu. Aşağıdaki keşif ve ekran bastırma daha önce
		/// DeathSync'ten ödünç alınıyordu.
		/// </remarks>
		public const string DEATH_OBJECT = "Systems/Death";
		public const string DEATH_FSM = "Activate Dead Body";

		static PlayMakerFSM deathFsm;
		static GameObject gameOverRoot;
		static bool loggedDeathMissing;

		/// <summary>
		/// Ölüm SİSTEMİNİN kök nesnesi (FSM'in üstünde durduğu nesne).
		///
		/// Ölüm başlarken AÇILIR ve kan (PlayerBlood), GameOver ekranı ve
		/// ölüm FSM'leri onun ALTINDA durur. Yani "bu nesne açıldı" demek
		/// "ölüm gerçekten başladı" demektir ve kan ekranının görünmesinden
		/// DAHA ERKEN görülebilen bir sinyaldir. Sahne başında KAPALIDIR
		/// (keşif onu pasif nesneler arasında bulur), o yüzden geri kapatmak
		/// oyunun kendi başlangıç durumuna dönmektir.
		/// </summary>
		static GameObject deathRoot;

		/// <summary>
		/// YEREL oyuncunun kamera ölüm FSM'i ('Death' on the FPSCamera).
		///
		/// NEDEN AYRI BİR FSM: Systems/Death'i kapatmak ölümün SONUNU
		/// (ekran, ölü beden, menü) engelliyor ama kan ekranını ve kamerayı
		/// ele geçiren makine oyuncunun kamerasındadır. Bu FSM bulunamazsa
		/// oyuncu "engellenmiş ama ölü" hâlde kalır (canlı kanıt
		/// 2026-10-14 17:53). Yalnız oyuncu kökündeki 'Death' aranır:
		/// altında başka bir 'Death' yok, NPC/araç 'Death'leri başka
		/// köklerdedir.
		/// </summary>
		static PlayMakerFSM cameraDeathFsm;

		/// <summary>
		/// BİNDİĞİMİZ aracın sürücü ölüm FSM'leri ('Death' on DriverHeadPivot).
		///
		/// DUMP KANITI (tools/dump2/fsms.json): her araçta
		/// `.../DriverHeadPivot | Death` var; olayları DEATH/OFF/ON, eriştiği
		/// nesneler DeadBody + CameraPivot. DEATH gelirse ölü beden
		/// etkinleşir ve KAMERA PIVOT'u ona bağlanır: kamera düşen bedenle
		/// birlikte haritanın altına iner ve ışınlanma TUTMAZ (canlı kanıt
		/// 17:53:46 ışınlanma, 17:53:47 "fell below the map", 2 sn sonra
		/// yine). Bu yüzden aracın FSM'i de korunur.
		/// </summary>
		static readonly List<PlayMakerFSM> vehicleDeathFsms = new List<PlayMakerFSM>();
		static int vehicleDeathScanId;
		static float nextVehicleDeathScan;
		const int MAX_VEHICLE_DEATH_FSMS = 4;

		/// <summary>Engellenen ölüm OLAYI sayısı (teşhis).</summary>
		public static int BlockedEvents;
		static float lastBlockLog;

		/// <summary>Ölüm FSM'i (keşif edilmişse).</summary>
		public static PlayMakerFSM FSM { get { return deathFsm; } }

		/// <summary>
		/// Game Over ekranı kapalı kalır.
		///
		/// FSM'i devre dışı bırakmak yetmiyor: bazı ölüm yolları ekranı
		/// doğrudan nesne üzerinden aktifleştiriyor.
		/// </summary>
		/// <returns>Bu çağrıda ekran GERÇEKTEN açıktı ve kapatıldıysa true.
		/// Açık olması bir ölüm sinyalidir (bkz. HandleDeath).</returns>
		public static bool KeepScreenHidden() {
			try {
				if (gameOverRoot != null && gameOverRoot.activeSelf) {
					gameOverRoot.SetActive(false);
					return true;
				}
			} catch { }
			return false;
		}

		/// <summary>
		/// FSM adini guvenli sekilde okur.
		///
		/// PlayMakerFSM.FsmName BAZI bilesenlerde bos donuyor; gercek ad
		/// ancak Fsm.Name icinde durur. SatsumaFsmProbe.SafeName de ayni
		/// zinciri kullaniyor. GodMode once yalniz FsmName'e bakti ve
		/// bu yuzden oluM FSM'ini hic bulamadi (canli log 21:03, L727).
		/// </summary>
		static string FsmNameOf(PlayMakerFSM f) {
			try { if (f == null) return ""; } catch { return ""; }
			try { if (!string.IsNullOrEmpty(f.FsmName)) return f.FsmName; } catch { }
			try { if (!string.IsNullOrEmpty(f.name)) return f.name; } catch { }
			try {
				if (f.Fsm != null && !string.IsNullOrEmpty(f.Fsm.Name)) return f.Fsm.Name;
			} catch { }
			return "";
		}

		/// <summary>
		/// PASİF nesneler dahil ölüm sistemini arar.
		///
		/// NEDEN GEREKLİ: GameObject.Find ve GetComponentsInChildren yalnız
		/// yüklü/aktif hiyerarşiyi görür. Canlı log kanıtı:
		///
		///   "godmode: 'Systems/Death' ustunde 0 FSM, hedef 'Activate Dead Body' yok."
		///
		/// Bu bir isim uyuşmazlığı değil, aramanın dar olmasının sonucudur:
		/// ölüm makinesi ölüm gerçekleşene kadar kapalı durur.
		/// Resources.FindObjectsOfTypeAll pasif nesneleri de döndürür.
		///
		/// Süzgeç hideFlags'tır: varlık/prefab kopyaları kendilerini
		/// HideInHierarchy ile işaretler, sahne nesneleri temiz kalır. Aynı
		/// süzgeç IsBloody/ClearBlood içinde de kullanılıyor.
		///
		/// Unity 5.0 hedefi: GameObject.scene/SceneManager YOK, o yüzden
		/// sahne testi yapılmaz (bu API 5.3'te geldi).
		/// </summary>
		static void DiscoverInactive() {
			try {
				PlayMakerFSM[] fsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
				if (fsms != null) {
					for (int i = 0; i < fsms.Length; i++) {
						PlayMakerFSM f = fsms[i];
						if (f == null) continue;
						try { if (f.hideFlags != HideFlags.None) continue; } catch { continue; }
						if (!string.Equals(FsmNameOf(f), DEATH_FSM, StringComparison.Ordinal)) continue;
						deathFsm = f;
						SmpLog.Print("godmode: death FSM found among INACTIVE objects ('"
							+ DEATH_FSM + "').");
						break;
					}
				}
			} catch { }

			if (gameOverRoot == null) {
				try {
					GameObject[] gos = Resources.FindObjectsOfTypeAll<GameObject>();
					if (gos != null) {
						for (int i = 0; i < gos.Length; i++) {
							GameObject go = gos[i];
							if (go == null) continue;
							if (go.name != "GameOverScreen") continue;
							try { if (go.hideFlags != HideFlags.None) continue; } catch { continue; }
							gameOverRoot = go;
							break;
						}
					}
				} catch { }
			}
		}

		/// <summary>Sahne yüklendiğinde bir kez çağrılır.</summary>
		public static void Discover() {
			deathFsm = null;
			gameOverRoot = null;
			// v15: loggedDeathMissing burada ARTMAMALI. Canli log kaniti:
			// kesfi kare basina cagirmak 4479 satir uyari basmisti.
			// Uyarı bir kez verilir, sahne yeniden yuklenene kadar tekrar etmez.
			bool deathObjectSeen = false;
			try {
				GameObject death = GameObject.Find(DEATH_OBJECT);
				if (death == null) death = GameObject.Find("Death");
				if (death != null) {
					deathObjectSeen = true;
					PlayMakerFSM[] all = death.GetComponentsInChildren<PlayMakerFSM>(true);
					for (int i = 0; i < all.Length; i++) {
						if (all[i] == null) continue;
						if (string.Equals(FsmNameOf(all[i]), DEATH_FSM, StringComparison.Ordinal)) {
							deathFsm = all[i];
							break;
						}
					}
					if (deathFsm == null && all.Length == 1) deathFsm = all[0];

					// Canli kanit (21:25, L134): yol ile bulunan nesnenin
					// USTUNDE 0 FSM var:
					//   godmode: 'Systems/Death' ustunde 0 FSM,
					//   hedef 'Activate Dead Body' yok.
					// Yani GameObject.Find("Systems/Death") dökümdeki
					// nesneyi DEGIL, başka bir nesneyi döndürüyor.
					// Döküm kanıtı (Systems/Death [Activate Dead Body],
					// start "Permadeath 2") doğru; sadece arama yolu dar.
					// Bu yüzden 'Systems' altının TAMAMINI taranıyor.
					if (deathFsm == null) {
						GameObject systems = GameObject.Find("Systems");
						if (systems != null) {
							PlayMakerFSM[] wide = null;
							try { wide = systems.GetComponentsInChildren<PlayMakerFSM>(true); } catch { }
							if (wide != null) {
								for (int i = 0; i < wide.Length; i++) {
									if (wide[i] == null) continue;
									if (string.Equals(FsmNameOf(wide[i]), DEATH_FSM, StringComparison.Ordinal)) {
										deathFsm = wide[i];
										break;
									}
								}
							}
						}
					}
					if (deathFsm == null) {
						// Tam ad tutmadi. HANGI adlarin oldugunu yaz ki
						// sonraki log soruyu cevaplasin (canli 21:03 logunda
						// bu yol sessizce basarisiz kaliyordu).
						string names = "";
						for (int i = 0; i < all.Length && i < 12; i++) {
							if (all[i] == null) continue;
							if (i > 0) names += ", ";
							names += "'" + FsmNameOf(all[i]) + "'";
						}
						SmpLog.Print("godmode: '" + DEATH_OBJECT + "' ustunde "
							+ all.Length + " FSM, hedef '" + DEATH_FSM
							+ "' yok. Gorulenler: " + (names == "" ? "(yok)" : names));
					}
					Transform go = death.transform.Find("GameOverScreen");
					if (go != null) gameOverRoot = go.gameObject;
				}
				if (gameOverRoot == null) {
					GameObject direct = GameObject.Find("GameOverScreen");
					if (direct != null) gameOverRoot = direct;
				}
			}
			catch (Exception e) {
				SmpLog.Warning("godmode death discovery failed: " + e.GetType().Name);
			}
			// AKTİF OLMAYAN nesneler dahil ara. GameObject.Find yalnız aktif
			// nesneleri döndürür; oyun ölüm sistemini ölüm gerçekleşene kadar
			// KAPALI tuttuğu için yukarıdaki arama çoğu sahnede boş dönüyor.
			if (deathFsm == null || gameOverRoot == null) DiscoverInactive();

			if (deathFsm == null) {
				// Iki durum ayri:
				//   deathObjectSeen == false -> ana menudeyiz, Systems/Death
				//     hic yuklenmedi. Bu bir hata DEGIL, sessiz gec.
				//   deathObjectSeen == true  -> nesne var ama FSM adi tutmadi;
				//     gercek bir uyari, sadece BIR KERE.
				if (deathObjectSeen && !loggedDeathMissing) {
					loggedDeathMissing = true;
					SmpLog.Warning("godmode: death object '" + DEATH_OBJECT
						+ "' is present but FSM '" + DEATH_FSM + "' was not found;"
						+ " the death system's own events cannot be blocked. The player's camera"
						+ " death FSM, the Game Over screen, the blood screen and falling are still"
						+ " watched and still teleport the player to the spawn point.");
				}
			} else {
				SmpLog.Print("godmode: death FSM is '" + DEATH_FSM + "' on '" + DEATH_OBJECT + "'.");
			}

			// Ölüm NESNESİ: kan (PlayerBlood), GameOver ekranı ve ölüm
			// FSM'leri onun altındadır. Açılması = ölümün başlangıcı.
			deathRoot = (deathFsm != null) ? deathFsm.gameObject : null;

			// Oyuncunun kendi ölüm FSM'i (kamera). Systems/Death'i bulmak
			// yetmiyor: kan ekranını açan ve ölüm zincirini başlatan FSM
			// oyuncunun kamerasındadır.
			DiscoverPlayerDeath();
		}

		/// <summary>Yerel oyuncunun kök nesnesi ('PLAYER'), yoksa null.</summary>
		static GameObject PlayerRoot() {
			try {
				GameObject go = GameObject.Find("PLAYER");
				if (go != null) return go;
			} catch { }
			try {
				CharacterController cc = LocalPlayerState.Controller;
				if (cc == null) cc = PlayerMotion.Controller;
				if (cc == null) return null;
				Transform t = cc.transform;
				int guard = 0;
				while (t.parent != null && guard++ < 64) t = t.parent;
				return t.gameObject;
			} catch { return null; }
		}

		/// <summary>
		/// YEREL oyuncunun ölüm FSM'ini bulur: kamera nesnesindeki 'Death'.
		///
		/// DUMP KANITI (tools/dump2/fsms.json):
		///   PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera | Death | start= State 2
		///   durumlar: State 2 --DEATH--> State 1
		///   eylemler: SetProperty(ScreenOverlay.enabled), SendEvent('Activate Dead
		///             Body'), ActivateGameObject, SetFsmBool
		/// Yani kan ekranını AÇAN ve ölüm sistemini uyandıran makine budur.
		/// Oyuncu kökünün altındaki tek 'Death' FSM'i odur (NPC/araç 'Death'leri
		/// başka köklerde); bu yüzden ad yeterli, yol sabitlenmez.
		/// </summary>
		static void DiscoverPlayerDeath() {
			cameraDeathFsm = null;
			try {
				GameObject player = PlayerRoot();
				if (player != null) {
					PlayMakerFSM[] all = player.GetComponentsInChildren<PlayMakerFSM>(true);
					if (all != null) {
						for (int i = 0; i < all.Length; i++) {
							PlayMakerFSM f = all[i];
							if (f == null) continue;
							if (!string.Equals(FsmNameOf(f), "Death", StringComparison.Ordinal)) continue;
							cameraDeathFsm = f;
							break;
						}
					}
				}
			} catch { }

			if (cameraDeathFsm != null) {
				SmpLog.Print("godmode: player death FSM is 'Death' on '"
					+ SceneDump.PathOf(cameraDeathFsm.transform) + "'.");
			} else if (!loggedCameraDeathMissing) {
				loggedCameraDeathMissing = true;
				SmpLog.Warning("godmode: the player camera death FSM ('Death') was not found;"
					+ " the blood overlay and the death camera takeover cannot be blocked"
					+ " at the source. The death object, Game Over screen, blood screen and"
					+ " falling are still watched.");
			}
		}

		/// <summary>
		/// Bindiğimiz aracın sürücü ölüm FSM'lerini toplar.
		///
		/// DUMP KANITI: her araçta `.../DriverHeadPivot | Death` var (olayları
		/// DEATH/OFF/ON, eriştiği nesneler DeadBody + CameraPivot). DEATH
		/// ulaşırsa ölü beden etkinleşir ve KAMERA PIVOT'u ona bağlanır:
		/// kamera düşen bedenle haritanın altına iner ve ışınlanma tutmaz.
		/// Canlı kanıt (2026-10-14): ışınlanmadan 1.7 sn sonra
		/// "fell below the map" YENİDEN — ışınlanma bir düşen kamerayı
		/// kurtaramaz.
		///
		/// Hangi araç: sürücü VEYA yolcu koltuğu (VehicleRegistry.FindLocalOccupant).
		/// Tarama 0.5 sn'de bir YALNIZ araç kimliği değişmişse yapılır; aksi
		/// hâlde maliyet kimlik karşılaştırmasıdır.
		/// </summary>
		static void RefreshVehicleDeaths() {
			float now = Time.time;
			if (now < nextVehicleDeathScan) return;
			nextVehicleDeathScan = now + 0.5f;

			int id = 0;
			GameObject root = null;
			try {
				SyncedVehicle occ = VehicleRegistry.FindLocalOccupant();
				if (occ != null) {
					id = occ.GetInstanceID();
					root = occ.gameObject;
				}
			} catch { id = 0; root = null; }

			if (id == vehicleDeathScanId) return;
			vehicleDeathScanId = id;
			vehicleDeathFsms.Clear();
			if (root == null) return;

			try {
				PlayMakerFSM[] all = root.GetComponentsInChildren<PlayMakerFSM>(true);
				if (all == null) return;
				string found = "";
				for (int i = 0; i < all.Length && vehicleDeathFsms.Count < MAX_VEHICLE_DEATH_FSMS; i++) {
					PlayMakerFSM f = all[i];
					if (f == null) continue;
					if (!string.Equals(FsmNameOf(f), "Death", StringComparison.Ordinal)) continue;
					vehicleDeathFsms.Add(f);
					if (found.Length > 0) found += ", ";
					found += "'" + SceneDump.PathOf(f.transform) + "'";
				}
				if (vehicleDeathFsms.Count > 0) {
					SmpLog.Print("godmode: driver death FSM(s) of the vehicle you occupy,"
						+ " protected: " + found + ".");
				}
			} catch { }
		}

		/// <summary>
		/// DURUM GEÇİŞ KARARI — saf mantık, Unity'den bağımsız.
		///
		/// NEDEN AYRI METOT: bu özellikte en pahalı hata yanlış POZİTİFtir —
		/// oyuncu yükleme anında doğuş noktasına yankılanırsa mod oynanamaz
		/// hâle gelir. Kural tek bir saf fonksiyonda durunca
		/// tests\DeathProbe.cs onu Unity olmadan doğrulayabiliyor.
		///
		/// Kural:
		///   * durum boş/null      -> karar yok (FSM henüz hiç çalışmamış,
		///                            pasif nesnede ActiveStateName boş kalır)
		///   * durum = start state -> taban alındı
		///   * başka bir durum     -> YALNIZCA start state daha önce görülmüşse
		///                            ölüm; aksi hâlde FSM'in dinlenme durumu
		///                            başka bir ad demektir, ölüm DEĞİL.
		/// </summary>
		/// <returns>0 = karar yok, 1 = taban (start state), 2 = ÖLÜM.</returns>
		public static int ClassifyDeathState(string state, bool sawStart) {
			if (string.IsNullOrEmpty(state)) return 0;
			if (string.Equals(state, START_STATE, StringComparison.Ordinal)) return 1;
			return sawStart ? 2 : 0;
		}

		/// <summary>
		/// Her kare çağrılır. Ölümü ENGELLEYEN şey artık bu metot değil,
		/// olay kapısıdır (Fsm.ProcessEvent yaması -> ShouldBlockDeathFsmEvent).
		/// Buradaki iş, engellemeye rağmen ölüm BAŞLAMIŞSA onu yakalamaktır:
		///
		///   * ölüm sisteminin FSM'i start state'ten ayrıldı,
		///   * ölüm NESNESİ açıldı (kan ve Game Over ekranı onun içinde),
		///   * Game Over ekranı açıldı,
		///   * oyuncu haritanın altına düştü / kan ekranı göründü.
		///
		/// Her durumda tepki aynı: korunan FSM'ler dinlenme durumuna çekilir,
		/// kan/ekran bastırılır ve oyuncu doğuş noktasına ışınlanır.
		///
		/// NOT: ölüm FSM'ini KAPATMAK artık yapılmıyor. Eski sürüm onu ilk
		/// karede kapatıyordu ve bu yüzden aşağıdaki durum kontrolü HİÇ
		/// çalışmıyordu (canlı logda tek durum satırı yok) — üstelik oyunun
		/// kendi ölüm sistemini yarı ölü bir durumda bırakıyordu.
		/// </summary>
		public static void Tick() {
			if (!Enabled) return;
			try {
				// 1) Ölüm sisteminin KENDİ durumu. FSM artık KAPATILMIYOR
				//    (eski sürüm ilk karede kapatıyordu ve bu yüzden bu kontrol
				//    hiç çalışmıyordu — canlı logda tek bir durum satırı yok).
				PlayMakerFSM fsm = deathFsm;
				if (fsm != null && fsm.enabled) {
					string st = (fsm.Fsm != null) ? fsm.Fsm.ActiveStateName : null;
					int verdict = ClassifyDeathState(st, sawStartState);
					if (verdict == 1) {
						// Taban alindi: dinlenme durumu gerçekten start state.
						sawStartState = true;
					} else if (verdict == 2) {
						Blocked++;
						HandleDeath("death FSM entered '" + st + "'", true);
					} else if (!string.IsNullOrEmpty(st) && !warnedWrongStart) {
						// Start state HİÇ görülmedi: bu FSM'in dinlenme durumu
						// başka bir ad. Durum temelli tespit kapatılır.
						warnedWrongStart = true;
						SmpLog.Warning("godmode: death FSM rests in '" + st
							+ "', not '" + START_STATE + "'; state-based death detection is OFF"
							+ " (events, Game Over screen, blood and the death object still work).");
					}
				}

				// 2) Bindiğimiz aracın sürücü ölüm FSM'leri (araç değişince).
				RefreshVehicleDeaths();

				// 3) Ölüm NESNESİ açıldıysa ölüm gerçekten başlamıştır: kan onun
				//    içindedir ve kamera birazdan ölü bedene bağlanır. Işınla ve
				//    nesneyi geri kapat (kan da onunla birlikte kaybolur).
				//    KeepDeathOff cooldown'dan BAĞIMSIZ çalışır: ışınlama
				//    atlanmış olsa bile kan ekranda kalmaz.
				if (IsDeathObjectActive()) {
					HandleDeath("death system object was activated", true);
					KeepDeathOff();
				}

				// 4) Game Over ekranı bir şekilde açılmış olabilir: bastır.
				//    AÇIK olması da bir ölüm sinyalidir: ışınlan.
				if (KeepScreenHidden()) HandleDeath("Game Over screen was active", true);

				FailsafeTick();
			}
			catch (Exception e) {
				SmpLog.Warning("godmode tick failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// Ölüm sisteminin kök nesnesi AÇIK mı?
		///
		/// DUMP KANITI: kan (PlayerBlood) ve GameOver ekranı bu nesnenin
		/// ALTINDA. Yani bu bayrak, kan ekranından daha erken gelen bir ölüm
		/// sinyalidir. Sahne başında nesne kapalıdır (keşif onu pasif
		/// nesneler arasında bulur).
		/// </summary>
		static bool IsDeathObjectActive() {
			GameObject go = deathRoot;
			if (go == null) return false;
			try { return go.activeSelf; } catch { return false; }
		}

		/// <summary>Ölüm nesnesini kapatır: kan/ekran/ölü beden onunla birlikte gider.</summary>
		static void KeepDeathOff() {
			GameObject go = deathRoot;
			if (go == null) return;
			try { if (go.activeSelf) go.SetActive(false); } catch { }
		}

		/// <summary>
		/// Harita dışına düşme / kanlı ekran failsafe'i. Godmode ölüm FSM'ini
		/// kestiği için kaza sonrası kan efekti ve sonsuza düşme respawn'sız
		/// kalabiliyor: bunu burada yakalayıp son güvenli konuma ışınlarız.
		/// </summary>
		static void FailsafeTick() {
			float now = Time.time;
			CharacterController cc = LocalPlayerState.Controller;
			if (cc == null) cc = PlayerMotion.Controller;

			Camera cam = Camera.main;
			if (cc != null && cc.isGrounded && cam != null && cam.transform.position.y > 0f && cc.transform.position.y > 0f && now >= nextSample) {
				nextSample = now + 1.0f;
				lastSafe = cc.transform.position;
				hasSafe = true;
				if (!hasFirstSpawn) {
					firstSpawn = cc.transform.position;
					hasFirstSpawn = true;
					SmpLog.Print("godmode: first safe spawn captured at " + firstSpawn.ToString("F1"));
				}
			}			// Haritadan düşme: kan olayı varsa ilk spawn yerine dön, yoksa da her zaman güvenli.
			if (cam != null && cam.transform.position.y < -10f && hasSafe && now - lastFailsafe > 2f) {
				lastFailsafe = now;
				// v15: SpawnToCottage aramasi kaldirildi (boyle bir nesne yok).
				HandleDeath("fell below the map", false);
			}

			// Kan ekranı = öldü demek: ilk spawn noktasına ışınlayıp ekranı temizle.
			// (Not: 'GAME' sahne reload'u denenip ana menüye düştü. O yüzden teleport + kan temizliği.)
			// Kan ekranı = ölüm. Yoklama sıklığı önbelleğe bağlıdır:
			//   önbellek DOLU  -> 4 Hz (ışınlanma ~0.25 sn içinde),
			//   önbellek BOŞ   -> saniyede bir yeniden tara ve 1 Hz yokla
			//                     (PlayerBlood sonradan oluşturuluyorsa eski
							//                     davranış aynen korunur).
			if (now >= nextBloodCheck) {
				if (bloodCones.Count == 0 && now >= nextBloodScan) {
					nextBloodScan = now + 1.0f;
					FindBlood();
				}
				bool cached = bloodCones.Count > 0;
				nextBloodCheck = now + (cached ? 0.25f : 1.0f);
				if (cached && IsBloody() && now - lastReload > 0.75f) {
					lastReload = now;
					HandleDeath("blood screen", true);
				}
			}
		}

		/// <summary>
		/// ÖLÜM YAKALANDI: ölüm geri sarılır, oyuncu haritaya ışınlanır.
		///
		/// TEPKİ (seçilen tasarım): ölüm HİÇ TAMAMLANMAZ — kan ekranı, Game
		/// Over ve ölü beden bastırılır — ve oyuncu haritadaki İLK GÜVENLİ
		/// KONUMDA oynamaya devam eder. Yalnız konum ve kan/ekran sıfırlanır:
		/// ihtiyaçlar (açlık/sus/yorgunluk/mesane), para ve araç durumu AYNEN
		/// korunur.
		///
		/// NEDEN TEK YER: aynı ölümü dört ayrı sinyal haber verebiliyor (FSM
		/// durumu, FSM'e gelen olay, Game Over ekranı, kan/düşme). Hepsi
		/// buradan geçerse davranış tek olur ve sayaçlar tek yerde toplanır.
		///
		/// <param name="cause">Log'da görünen tetikleyici.</param>
		/// <param name="isDeath">Gerçek ölüm mü, haritadan düşme kurtarması mı?
		/// Yalnız sayaç ayrımı için (failsafe-respawns / death-respawns).</param>
		/// </summary>
		static void HandleDeath(string cause, bool isDeath) {
			float now = Time.time;
			if (now - lastDeathHandled < DEATH_COOLDOWN) return;
			lastDeathHandled = now;

			// Ölüm makinesini temiz başlangıca geri koy: aksi hâlde nesne
			// "engellenmiş ama ölü" hâlde kalır ve kan/ekran sürekli geri gelir.
			ForceRestState(deathFsm, START_STATE);
			// Oyuncunun kamera ölüm FSM'i ve bindiğimiz aracın sürücü ölüm
			// FSM'leri de yarıda kalmış olabilir: her biri kendi dinlenme
			// durumuna (start state) çekilir. Aksi hâlde kamera ölü kamerada,
			// araç ölü bedene bağlı kalır.
			ForceRestState(cameraDeathFsm, null);
			for (int i = 0; i < vehicleDeathFsms.Count; i++) ForceRestState(vehicleDeathFsms[i], null);
			// Kan ekranı ve Game Over ekranı ölüm nesnesinin içindedir.
			KeepDeathOff();
			KeepScreenHidden();

			// Doğuş noktası: kaydın yüklendiği ilk güvenli konum. Henüz
			// yakalanmadıysa son güvenli konuma, o da yoksa hiç ışınlanma
			// (0,0,0'a ışınlamak oyuncuyu haritanın dışına atardı).
			Vector3 target;
			if (hasFirstSpawn) target = firstSpawn;
			else if (hasSafe) target = lastSafe;
			else return;

			SmpLog.Print("godmode: death intercepted (" + cause + "); teleporting to the spawn point.");
			RespawnTo(target, isDeath);
		}

		/// <summary>
		/// Ölüm FSM'ine giden olay YÜRÜTÜLMESİN mi? (Harmony öneki)
		///
		/// Ölüm makinesinin tek işi ölümdür: bu FSM'e gelen her olay bir ölüm
		/// isteğidir (WASP/DRIVEBEE, CRASH, FIRE, DROWN, HUNGER, MURDER,
		/// RUNOVER ... hepsi aynı FSM'in geçişleri). Önek false döndürünce
		/// hedef metot HİÇ çalışmaz, yani olay FSM'e ulaşmaz: "ölüm hiç
		/// tamamlanmasın" şartının en sağlam hâli budur ve oyuncu ışınlanır.
		///
		/// Godmode KAPALIYKEN hiçbir şey engellenmez: 'smp god off' ölümü
		/// normale döndürür (FSM geri açılır, bkz. Tick).
		///
		/// NEDEN REFERANS (ad/isim değil): bu yol oyunun en sıcak olay
		/// damarıdır — kare başına yüzlerce kez geçer — ve deathFsm
		/// bulunamadıysa (null) maliyeti tek bir null kontrolüdür.
		/// </summary>
		public static bool ShouldBlockDeathEvent(PlayMakerFSM fsm, string eventName) {
			if (!Enabled || fsm == null) return false;
			PlayMakerFSM d = deathFsm;
			if (d != null && object.ReferenceEquals(d, fsm)) {
				// Ölüm sisteminin tek işi ölümdür: hangi olay gelirse gelsin
				// bir ölüm isteğidir.
				//
				// IŞINLAMA BURADA YAPILMAZ: engellemek ölümü zaten durdurur.
				// Ölüm gerçekten başladıysa onu açılan nesne / FSM durumu /
				// kan / düşme katmanları görür (bkz. Tick). Yoksa bir arının
				// ya da kaza tespitinin kare başına yolladığı istek oyuncuyu
				// saniyede bir doğuş noktasına yankılardı.
				NoteBlocked("event '" + (eventName == null ? "?" : eventName)
					+ "' on the death system FSM");
				return true;
			}
			// Sıcak yol: korunan oyuncu FSM'i YOKSA hiçbir olay engellenmez
			// ve ada bakmak bile gerekmez.
			if (cameraDeathFsm == null && vehicleDeathFsms.Count == 0) return false;
			if (!object.ReferenceEquals(fsm, cameraDeathFsm) && !IsVehicleDeathFsm(fsm)) return false;
			if (!IsDeathTriggerEvent(eventName)) return false;
			NoteBlocked("event '" + eventName + "' on the player death FSM");
			return true;
		}

		/// <summary>
		/// OLAY ADI ÖLÜMÜ BAŞLATIR MI? — saf mantık, Unity'den bağımsız.
		///
		/// DUMP KANITI: hem yerel oyuncunun kamera ölüm FSM'inin hem her
		/// aracın sürücü ölüm FSM'inin olay listesinde tek ölüm tetikleyicisi
		/// 'DEATH' (kamera FSM'i: State 2 -> State 1). 'DIE' ise
		/// FPSCamera/Blindness yolunun ölüm onayıdır; korunan FSM'lerde
		/// ikisi de ölüm isteğidir.
		///
		/// NEDEN AYRI METOT: bu kural bu özellikte en pahalı hatayı (yanlış
		/// engelleme = ölümsüz ama oynanamaz oyun) belirliyor, o yüzden
		/// tests\DeathProbe.cs onu Unity olmadan ölçebiliyor.
		/// </summary>
		public static bool IsDeathTriggerEvent(string name) {
			if (string.IsNullOrEmpty(name)) return false;
			return string.Equals(name, "DEATH", StringComparison.Ordinal)
				|| string.Equals(name, "DIE", StringComparison.Ordinal);
		}

		/// <summary>
		/// OLAY TESLİMİNİN TEK BOĞAZI — GodMode tarafı.
		///
		/// NEDEN YENİ BİR KAPI: PlayMaker bir olayı hedefe dört ayrı yoldan
		/// teslim ediyor (kendine, nesneye, FSM adına, tüm FSM'lere yayın) ve
		/// hepsi <c>Fsm.ProcessEvent</c> içinde birleşiyor. Eski yamalar
		/// (PlayMakerFSM.SendEvent, Fsm.Event aşırı yüklemeleri) yalnız
		/// GÖNDEREN tarafı görüyordu: "bir nesneye gönder" yolu hedef FSM'e
		/// doğrudan ProcessEvent ile yazıyor, yani gelen ölüm olayı hiçbir
		/// yamaya ve hiçbir log satırına girmiyordu.
		///
		/// CANLI KANIT (mscmp.txt, 2026-10-14 17:53): ölüm tam olarak bu
		/// yüzden başladı. İlk yakalanan sinyal kan ekranıydı (FSM durumu ve
		/// olay katmanlarından hiç satır yok); kan göründüğünde ölüm
		/// zinciri (ölü beden + kamera bağlama) çoktan çalışmıştı.
		///
		/// MALİYET: sıcak yol, bu yüzden önce REFERANS, sonra olay adı
		/// karşılaştırılır. Ölüm sistemi FSM'i için ad okunmaz (tüm olaylar
		/// ölümdür); korunan oyuncu FSM'lerinde ad okunur.
		/// </summary>
		public static bool ShouldBlockDeathFsmEvent(Fsm fsm, FsmEvent fsmEvent) {
			if (!Enabled || fsm == null) return false;

			PlayMakerFSM d = deathFsm;
			if (d != null) {
				Fsm target = FsmOf(d);
				if (target != null && object.ReferenceEquals(target, fsm)) {
					string n = null;
					try { n = (fsmEvent != null) ? fsmEvent.Name : null; } catch { }
					NoteBlocked("event '" + (n == null ? "?" : n) + "' delivered to the death system FSM");
					return true;
				}
			}

			// Sıcak yol: korunan oyuncu FSM'i YOKSA hiçbir olay engellenmez;
			// bu iki alan sahne boyunca sabit kalır, o yüzden ilk kapı ucuz.
			if (cameraDeathFsm == null && vehicleDeathFsms.Count == 0) return false;
			if (!IsPlayerDeathFsm(fsm)) return false;

			string name = null;
			try { name = (fsmEvent != null) ? fsmEvent.Name : null; } catch { }
			if (!IsDeathTriggerEvent(name)) return false;
			NoteBlocked("event '" + name + "' on the player death FSM");
			return true;
		}

		/// <summary>FSM'in kendisi (yoksa null). Kare başına çağrılmaz.</summary>
		static Fsm FsmOf(PlayMakerFSM f) {
			if (f == null) return null;
			try { return f.Fsm; } catch { return null; }
		}

		/// <summary>
		/// Bu FSM yerel oyuncuyu öldüren FSM'lerden mi? (kamera ölümü veya
		/// bindiğimiz aracın sürücü ölümü). Referans karşılaştırmasıdır.
		/// </summary>
		static bool IsPlayerDeathFsm(Fsm fsm) {
			if (fsm == null) return false;
			if (cameraDeathFsm != null) {
				Fsm cam = FsmOf(cameraDeathFsm);
				if (cam != null && object.ReferenceEquals(cam, fsm)) return true;
			}
			for (int i = 0; i < vehicleDeathFsms.Count; i++) {
				Fsm veh = FsmOf(vehicleDeathFsms[i]);
				if (veh != null && object.ReferenceEquals(veh, fsm)) return true;
			}
			return false;
		}

		/// <summary>Bu FSM bindigimiz aracin korunan surucu olum FSM'lerinden mi?</summary>
		static bool IsVehicleDeathFsm(PlayMakerFSM f) {
			if (f == null) return false;
			for (int i = 0; i < vehicleDeathFsms.Count; i++) {
				if (object.ReferenceEquals(vehicleDeathFsms[i], f)) return true;
			}
			return false;
		}

		/// <summary>
		/// Engellenen ölüm olayını sayar ve seyrek log'lar.
		///
		/// NEDEN SEYREK: ölüm isteği bir arı/kaza tespiti tarafından kare
		/// başına yollanabilir; satır başına log ölüm anında logu boğar.
		/// </summary>
		static void NoteBlocked(string what) {
			BlockedEvents++;
			float now = Time.time;
			if (now - lastBlockLog < 2f) return;
			lastBlockLog = now;
			SmpLog.Print("godmode: blocked " + what + " (total " + BlockedEvents
				+ "); no teleport, because the death never started.");
		}

		/// <summary>
		/// Bu Fsm ölüm makinesi mi? Fsm düzeyindeki olay kancaları için
		/// AYRI ve UCUZ bir kapıdır.
		///
		/// NEDEN AYRI: o kancalar olay adını argüman olarak almaz, adı
		/// çıkarmak her olayda ek maliyet olurdu — oysa o yol oyunun en
		/// sıcak damarıdır. Önce referansı karşılaştırıyoruz; yalnız ölüm
		/// FSM'i söz konusuysa ada bakılıyor (bkz. OnDeathEvent).
		/// </summary>
		public static bool IsDeathFsm(Fsm fsm) {
			if (!Enabled || fsm == null) return false;
			PlayMakerFSM d = deathFsm;
			if (d == null) return false;
			Fsm target = null;
			try { target = d.Fsm; } catch { }
			return target != null && object.ReferenceEquals(target, fsm);
		}

		/// <summary>
		/// Ölüm FSM'ine bir olay gönderildi: ölüm sayılır, oyuncu ışınlanır.
		/// (Olay adı yalnız teşhis satırı içindir.)
		/// </summary>
		public static void OnDeathEvent(string eventName) {
			HandleDeath("event '" + (eventName == null ? "?" : eventName) + "' on the death FSM", true);
		}

		/// <summary>
		/// Kan efektini bulur ve ÖNBELLEĞE alır.
		///
		/// Sahne başına bir kez çağrılır; bulunamazsa liste boş kalır ve
		/// çağıran taraf saniyede bir yeniden dener (PlayerBlood oyunda
		/// sonradan da oluşturulabiliyorsa davranış eskisiyle aynı kalır).
		/// </summary>
		static void FindBlood() {
			bloodCones.Clear();
			try {
				GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
				if (all == null) return;
				for (int i = 0; i < all.Length; i++) {
					GameObject go = all[i];
					if (go == null || go.hideFlags != HideFlags.None) continue;
					if (go.name != "PlayerBlood") continue;
					Component[] pcs = go.GetComponentsInChildren<Component>(true);
					for (int j = 0; j < pcs.Length; j++) {
						Projector p = pcs[j] as Projector;
						if (p == null) continue;
						BloodCone bc = new BloodCone();
						bc.go = go;
						bc.proj = p;
						bloodCones.Add(bc);
					}
				}
			} catch { }
		}

		/// <summary>
		/// KAN KARARI — saf mantık, Unity'den bağımsız.
		///
		/// NEDEN AYRI: IsBloody gövdesi Unity icall'u içerir (activeInHierarchy,
		/// Projector.enabled), yani masaüstü CLR'da JIT edilemez ve
		/// tests\DeathProbe.cs onu doğrulayamaz. Eşik/aktiflik kuralı buraya
		/// çıkınca oyun dışında ölçülebilir hâle geliyor.
		///
		/// Kural: nesne aktif VE project'ör açık VE boyut eşiği geçmiş.
		/// </summary>
		public static bool BloodIsDeath(bool goActive, bool projEnabled, float orthoSize) {
			return goActive && projEnabled && orthoSize > BLOOD_THRESHOLD;
		}

		/// <summary>Kan ekranı görünür mü? Önbellekteki project'örlere bakılır.</summary>
		static bool IsBloody() {
			for (int i = 0; i < bloodCones.Count; i++) {
				try {
					BloodCone bc = bloodCones[i];
					if (bc.go == null || bc.proj == null) continue;
					if (BloodIsDeath(bc.go.activeInHierarchy, bc.proj.enabled, bc.proj.orthographicSize)) return true;
				} catch { }
			}
			return false;
		}

		/// <summary>Oyuncuyu verilen noktaya ışınlar, kan ekranını temizler, oyuncu bayraklarını sıfırlar.</summary>
		static void RespawnTo(Vector3 point, bool isDeath) {
			FailsafeRespawns++;
			if (isDeath) DeathRespawns++;
			CharacterController cc = LocalPlayerState.Controller;
			if (cc == null) cc = PlayerMotion.Controller;
			if (cc != null) cc.enabled = false;
			Transform mover = cc != null ? cc.transform : null;
			if (mover == null) { Camera cam = Camera.main; if (cam != null) mover = cam.transform; }
			if (mover != null) mover.position = point;
			if (cc != null) cc.enabled = true;

			ClearBlood();
			KeepScreenHidden();
			SmpLog.Print("godmode: respawned at " + point.ToString("F1")
				+ (isDeath ? " (blood = death, full respawn)" : " (fell = failsafe)")
				+ " count=" + FailsafeRespawns + " deaths=" + DeathRespawns + ".");
		}

		/// <summary>
		/// Kan efektlerini kapatır: PlayerBlood project'örleri.
		///
		/// NOT: ölü beden nesnesine BİLEREK dokunulmaz. Dump kanıtı
		/// (tools/dump2/hierarchy.txt): 'DeadBody/CameraPivot' — ölüm kamerası
		/// ölü bedenin ALTINA bağlanıyor, yani o nesneyi kapatmak kamerayı da
		/// kapatırdı. Doğru çözüm nesneyi kapatmak değil, onu açan olayı
		/// engellemektir (bkz. ShouldBlockDeathFsmEvent).
		/// </summary>
		static void ClearBlood() {
			// Önbellek henüz kurulmadıysa (bu çağrı ilk ölümde gelebilir)
			// önce bul: aksi hâlde temizlenecek bir şey bilinmezdi.
			if (bloodCones.Count == 0) FindBlood();
			for (int i = 0; i < bloodCones.Count; i++) {
				try {
					BloodCone bc = bloodCones[i];
					if (bc.proj != null) bc.proj.enabled = false;
					if (bc.go != null) bc.go.SetActive(false);
				} catch { }
			}
		}

		/// <summary>
		/// Fsm'i verilen duruma zorlar. state boşsa FSM'in KENDİ start state'i
		/// kullanılır: her ölüm FSM'inin dinlenme durumu farklı bir addır
		/// (kamera 'State 2', SATSUMA 'State 2', HAYOSIKO 'State 3' — dump
		/// kanıtı), o yüzden ad sabitlenmez, FSM'den okunur.
		/// </summary>
		static void ForceRestState(PlayMakerFSM fsm, string state) {
			if (fsm == null) return;
			try {
				Fsm f = fsm.Fsm;
				if (f == null) return;
				string target = state;
				if (string.IsNullOrEmpty(target)) target = f.StartState;
				if (string.IsNullOrEmpty(target)) return;
				ForceState(f, target);
			} catch { }
		}

		/// <summary>Fsm'i başlangıç state'ine zorlar (ölüm sistemi).</summary>
		static void ForceStartState(PlayMakerFSM fsm) { ForceRestState(fsm, START_STATE); }

		/// <summary>API sürümden sürüme değiştiği için yansıma ile hem FsmState hem
		/// string aşırı yüklemesini dener.</summary>
		static void ForceState(Fsm fsm, string stateName) {
			try {
				Type ft = fsm.GetType();
				foreach (MethodInfo m in ft.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
					if (m.Name != "SetState") continue;
					ParameterInfo[] ps = m.GetParameters();
					if (ps.Length != 1) continue;
					if (ps[0].ParameterType == typeof(string)) {
						m.Invoke(fsm, new object[] { stateName });
						return;
					}
					object st = fsm.GetState(stateName);
					if (st != null && ps[0].ParameterType.IsInstanceOfType(st)) {
						m.Invoke(fsm, new object[] { st });
						return;
					}
				}
			} catch { }
		}

		/// <summary>
		/// Sahne değişiminde respawn noktasını unut (Enabled korunur).</summary>
		public static void ResetSafety() {
			hasSafe = false;
			lastSafe = Vector3.zero;
			nextSample = 0f;
			lastFailsafe = 0f;
			hasFirstSpawn = false;
			firstSpawn = Vector3.zero;
			nextBloodCheck = 0f;
			lastReload = 0f;
			lastDeathHandled = 0f;
			BlockedEvents = 0;
			// Kan önbelleği sahneye bağlıdır: yeni sahnede yeniden bulunur.
			bloodCones.Clear();
			nextBloodScan = 0f;
			// Taban sifirlanir: yeni sahnede FSM yeniden kesfediliyor ve
			// start state'i bir kez daha GORULMEDEN durum tespiti acilmaz.
			sawStartState = false;
			warnedWrongStart = false;
			// Ölüm nesnesi ve korunan FSM'ler sahneye bağlıdır: yeni sahnede
			// yeniden keşfedilirler (Discover). Araç listesi de boşaltılır,
			// çünkü yeni sahnede bindiğimiz araç yeniden çözülür.
			deathRoot = null;
			cameraDeathFsm = null;
			loggedCameraDeathMissing = false;
			vehicleDeathFsms.Clear();
			vehicleDeathScanId = 0;
			nextVehicleDeathScan = 0f;
			lastBlockLog = 0f;
		}

		/// <summary>Etiket değiştirme; log'a yazar.</summary>
		public static void Set(bool on) {
			if (Enabled == on) {
				SmpLog.Print("godmode is already " + (on ? "ON" : "off") + ".");
				return;
			}
			Enabled = on;
			if (on) SmpLog.Print("godmode ON - death events are blocked at the FSM seam "
				+ "(the player's camera 'Death' FSM, the driver death FSM of the vehicle you "
				+ "are in, and the death system itself); if a death still starts, the player "
				+ "is teleported to the first safe position of the save.");
			else SmpLog.Print("godmode OFF - the game handles deaths again (permadeath is back).");
		}

		/// <summary>'smp status' için tek satır.</summary>
		public static string Describe() {
			return "godmode: " + (Enabled ? "ON" : "off") + "  blocked=" + Blocked
				+ "  blocked-events=" + BlockedEvents
				+ "  failsafe-respawns=" + FailsafeRespawns
				+ "  death-respawns=" + DeathRespawns
				+ "  player-death-fsm=" + (cameraDeathFsm != null ? "found" : "MISSING")
				+ "  vehicle-death-fsms=" + vehicleDeathFsms.Count
				+ "  (death events are blocked at the FSM seam and the player is"
				+ " teleported to the spawn point when a death still starts)";
		}
	}
}
