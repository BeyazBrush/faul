using System;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// AKÜYİ DOLDUR — Satsuma'nın aküsü hiç bitmez, elektrik düşmez.
	///
	/// ASSET KANITI (l2/fsms.json -> docs/fsm_detail):
	///   SATSUMA(557kg, 248)/CarSimulation/Car/Electrics   FSM 'Electrics'
	///     seviye float'ları : Battery, Charge, BatteryLevel
	///     jeneratör float'ları: AlternatorEfficiency, AlternatorWear,
	///                          WearSetup, RPM, Volts, Math1..3
	///     veritabanı         : db_Battery, db_BatteryPlus, db_BatteryMinus
	///     bool'lar           : ElectricsOK, ElectricsON, Battery, Damaged,
	///                          Installed1..5, Charging
	///     aksiyonlar          : FloatOperator ×4, FloatClamp, FloatCompare,
	///                          AddFsmFloat, SetFloatValue, SetFsmFloat
	///     durumlar            : "Charge battery", LIGHT, PROCEED, FINISHED
	///
	///   SATSUMA/CarSimulation/Car/Starter                 FSM 'Starter'
	///     float: db_Battery, Charge, InitRPM, StarterPitch
	///
	/// İKİ AYRI KONU — ikisi de canlı logla kanıtlı:
	///
	/// (1) AKÜ SEVİYESİ: `Battery` bir FSM ÇIKTISIDIR; `Electrics` her kare
	///     `FloatOperator` + `FloatClamp` ile düşürüyor. Tek seferlik yazım
	///     bir sonraki karede ezilir (aynı tuzak: `ElectricsOK` 19 sn sonra
	///     silinmişti). → HER KARE 1.0 yazıyoruz.
	///
	/// (2) JENERATÖR VERİLERİ — paket seli:
	///       12:59:30  AlternatorEfficiency=2003.16  AlternatorWear=95.28  Charge=119.48
	///       12:59:47  AlternatorEfficiency=2015.64  AlternatorWear=95.24  Charge=123.36
	///       ↑ saniyede bir paket, değerler SAÇMA büyüyor (2003 → 2015)
	///     `Charge` sınırsız arttığı için değer HER KARE değişiyor; `Tick`
	///     her kare "dirty" bulup paket üretiyor. Sonuç: boşta bant +
	///     uzaktaki oyuncunun hesabının bozulması — yani "elektrik düşüyor"
	///     belirtisinin kaynağı olabilir.
	///     → Akü doluyken bu float'lar 1'e sabitlenir; akü zaten doluysa
	///       alternatörün şarj etmesine gerek yok.
	///
	/// YEREL İŞLEMDİR: paket gönderilmez. Zaten iki tarafta da hep dolu
	/// olacağı için senkronlanacak fark kalmaz.
	/// </summary>
	public static class BatteryKeepFull {

		/// <summary>Açık/kapalı (konsol: 'smp battery on|off').</summary>
		public static bool Enabled = true;

		/// <summary>Akü doluyken jeneratör float'ları 1'e sabitlensin mi?</summary>
		public static bool FreezeGenerator = true;

		/// <summary>
		/// Akü seviyesi ve VOLTAJI tutan değişkenler.
		///
		/// KULLANICI BELİRTİSİ: "akü voltajı hala düşüyor, akü voltajını hep
		/// full yap." Yani seviye (0..1) doğruymuş ama akımla çarpılan
		/// VOLTAJ okuması düşüyor. Asset'te `Volts` bir FSM ÇIKTISI:
		///   FloatOperator ×4 (akü × yük ÷ alternatör) → FloatClamp
		/// Akü 1.0 olsa bile "yük" (RPM, ışıklar, kaloratif) çarpımı
		/// sonucu 0.9'a düşürebiliyor. Biz onu da kilitliyoruz.
		/// </summary>
		static readonly string[] LEVEL_VARS = {
			"Battery", "db_Battery", "Charge", "BatteryLevel", "Volts",
		};

		/// <summary>Jeneratör/alternatör verileri — paket seli kaynağı.</summary>
		static readonly string[] GEN_VARS = {
			"Charge", "AlternatorWear", "AlternatorEfficiency", "WearSetup",
		};

		/// <summary>
		/// VOLTAJ hesabının girdileri — sabitlenmezse voltaj yine düşer.
		///
		/// ASSET (satır 544-545):
		///   Volts   ← komşu: RPM | Battery
		///   Battery ← komşu: Volts | Damaged
		///
		/// Yani `Volts` bir `FloatOperator(float1=RPM, float2=Battery)`
		/// çıktısı. Akü (Battery) bool'udur ve biz onu True yapıyoruz;
		/// kalan girdi `RPM`'dir. Motor çalışırken RPM yükselir ve
		/// FloatOperator sonucu SABİT bir değere oturmaz → göstergedeki
		/// voltaj salınıyor/düşüyor.
		///
		/// BURADA RPM'i 1'e sabitlemiyoruz: bu motor hızını da etkiler ve
		/// oyunun elektrik hesabını bozar. Bunun yerine sonuç değişkenini
		/// (`Volts`) kilitlemek DOĞRU olan — aşağıdaki VOLTS_PIN listesinde.
		/// </summary>
		static readonly string[] VOLTS_PIN = {
			"Volts", "Voltage",
		};

		/// <summary>
		/// Hedef voltaj (V). Kullanıcı isteği: "voltajı 15 yap."
		/// MSC'de dolu akü ≈12.x V, alternatör yüklüyken 13.8 V gösterir;
		/// burada daima tam dolu görünsün istendiği için 15.0 sabitleniyor.
		/// </summary>
		public const float VOLT_TARGET = 15f;

		/// <summary>
		/// Motor çalışırken aküyü hiç düşürme.
		///
		/// KULLANICI ÖNERİSİ (birebir): "motor marş bastığında akü bitiyor,
		/// onu hooklayabilirsin."
		///
		/// ASSET KANITI (docs/fsm_detail/l2__Electrics__Electrics.txt,
		/// FSM 'Electrics' durum akışı):
		///
		///   State 1
		///    ├ "Engine off"     → GetProperty(Drivetrain.rpm) → RPM
		///    │                    FloatOperator → FloatClamp
		///    │                    **SubtractFsmFloat(db_Battery -= yük, perSecond)**
		///    │                    FloatCompare
		///    ├ "Charge battery" → FloatOperator ×4 → FloatClamp
		///    │                    **AddFsmFloat(db_Battery += şarj, perSecond)**
		///    └ "Run on starter" → marş sırasındaki ek çekiş
		///
		/// Yani aküyü DÜŞÜREN tek eylem `SubtractFsmFloat`, hedef değişken
		/// `db_Battery`'dir. Alternatör sağlamsa `AddFsmFloat` telafi eder
		/// ama akü tam doluyken bile `FloatClamp(…, 1.0)` sonrası küçük bir
		/// kayıp kalır — motor marşı bu farkı belirginleştirir.
		///
		/// ÇÖZÜM: `SubtractFsmFloat` çalışırken `perSecond`'ı 0'a çekiyoruz
		/// → akü hiç düşmüyor. Alternatör/şarj yolu (`AddFsmFloat`)
		/// DOKUNULMAZ, yani oyunun elektrik mantığı bozulmaz.
		/// </summary>
		public static bool NoDrain = true;

		static PlayMakerFSM[] cached;
		static float nextSearch;
		static bool logged;
		/// <summary>
		/// Elektrik FSM'leri bir kez bağlandı mı?
		///
		/// `Tick` artık bağlandıktan sonra sahne taramasını TAMAMEN durdurur;
		/// yalnızca hiçbir şey bağlanamadığında düşük frekansla tekrar dener
		/// (teşhis `Diagnose()` orada). Ayrıntı: bkz. Tick.
		/// </summary>
		static bool boundDone;
		static int writes;
		static int frozen;
		static int hookWrites;

		/// <summary>
		/// Hook ile yapılan düzeltme sayısı.
		///
		/// KULLANICI İSTEĞİ: "her kare değilde direkt hook at fsm ile akü
		/// voltajı hep 15 olsun." Artık oyun `Volts`'u her yazdığında
		/// `FsmFloat.set_Value` Prefix'i değeri düzeltiyor; `Tick` içinde
		/// kare başına tarama YOK.
		/// </summary>
		public static int HookWrites { get { return hookWrites; } }

		internal static void NoteHookWrite() { hookWrites++; }

		/// <summary>Yazılan seviye sayısı (teşhis).</summary>
		public static int Writes { get { return writes; } }

		/// <summary>Sabitlenen jeneratör sayısı (teşhis).</summary>
		public static int Frozen { get { return frozen; } }

		/// <summary>Bağlı FSM sayısı (teşhis; 0 ise dünya yüklenmemiş).</summary>
		public static int Bound {
			get { return (cached == null) ? 0 : cached.Length; }
		}

		// ------------------------------------------------------------------
		// Ana iş parçığı
		// ------------------------------------------------------------------

		public static void Tick() {
			if (!Enabled) return;
			// Ba?Yland??ysa tek bir "ilk FSM hâlâ canlı mı?" karşılaştırmasıyla
			// döner. Dünya değişiminde ilk FSM yok olur (Unity'nin aşırı
			// yüklenmiş `==`'si yok olmuş bileşende true döner), bu yüzden
			// bağlantıyı düşürüp yeni dünyada yeniden bağlanılır.
			if (boundDone && BindingAlive()) return;
			if (boundDone) {
				// Bu modülde Reset() yok (hiçbir yerden çağrılmıyordu),
				// yani dünya değişimini ancak buradan fark edebiliriz:
				// bağlantıyı düşür, yeni dünyada yeniden bağlan.
				boundDone = false;
				cached = null;
				nextSearch = 0f;
				logged = false;   // yeni dünya: bağlama satırı tekrar basılabilsin
			}
			try {
				// KULLANICI İSTEĞİ: "her kare değilde direkt hook at."
				// Düzeltme artık `FsmFloat.set_Value` / `FsmBool.set_Value`
				// Prefix'inde yapılıyor (bkz. AssemblyPatch.ElectricFloatHook
				// ve InstalledBoolHook). Oyun değişkeni yazmadığı sürece
				// burada hiçbir iş yapmıyoruz.
				//
				// Burada yalnızca BAĞLAMA/TEŞHİS kalıyor: hangi FSM'ler
				// elektrik hesabı taşıyor, onu bir kez ölçüp yazıyoruz.
				//
				// TARAMA BİR KEZ (düzeltildi).
				// Eski koşul `if (cached == null || now >= nextSearch)` idi ve
				// bağlandıktan SONRA da her 10 saniyede bir `Bind()` koşturuyordu.
				// `Bind` tüm sahnenin her PlayMakerFSM'ini tarıyor
				// (`Resources.FindObjectsOfTypeAll<PlayMakerFSM>`, bu dünyada
				// ~7000 FSM) ve aday listesinde `hit.Contains` ile O(n²)
				// doğrusal arama yapıyor. O ise yalnızca `Bound` adedi ve
				// `if (!logged)` sayesinde BİR KEZ basılan log satırları içindi;
				// ölçülen değer sonradan hiç kullanılmıyordu. Yani 10 saniyede
				// bir, sonsuza dek boşa tekrarlanan bir tarama.
				//
				// Artık: hiçbir şey bağlanamadıysa (Bound == 0) 10 sn'de bir
				// tekrar denenir -- teşhis `Diagnose()` tam olarak bu yolu
				// kullanıyor ve bu bilinen zayıf noktaydı. Bağlandıysa bitiyor.
				float now = Time.time;
				if (Bound > 0) { boundDone = true; return; }
				if (now < nextSearch) return;
				nextSearch = now + 10f;
				Bind();
			}
			catch (Exception e) {
				if (!logged) {
					logged = true;
					SmpLog.Warning("battery keep-full failed: " + e.GetType().Name);
				}
			}
		}


		// ------------------------------------------------------------------
		// Keşif
		// ------------------------------------------------------------------

		/// <summary>
		/// Araç elektrik FSM'lerini bul.
		///
		/// CANLI KANIT (12:57:25): "bound to 1 car FSM(s)" — `Starter`
		/// bulunamamıştı. Nedeni: `Starter` nesnesi `CarSimulation/Car/`
		/// altındadır AMA yol filtresi bazı araçlarda (`FERNDALE/Starter`,
		/// `KEKMET/Simulation/Starter`) farklı. Bu yüzden önce
		/// `CarSimulation` yolunu arıyoruz, sonra yol bağımsız olarak
		/// `Battery`/`db_Battery` değişkeni taşıyan HER `Electrics`/`Starter`
		/// FSM'ini kabul ediyoruz — değişken varlığı en güvenilir işarettir.
		/// </summary>
		/// <summary>
		/// Bağlı FSM'lerin ilki hâlâ canlı mı? (dünya değişimi tespiti)
		///
		/// Tüm diziyi değil ilk elemanı sormak yeterli: bağlama tek seferde
		/// yapılır, yani hepsi aynı sahneden gelir; biri öldüyse diğerleri de
		/// ölmüştür. Kare maliyeti bir karşılaştırma.
		/// </summary>
		static bool BindingAlive() {
			if (cached == null || cached.Length == 0) return false;
			try { return cached[0] != null && cached[0].Fsm != null; }
			catch { return false; }
		}

		static void Bind() {
			cached = null;

			// DÜZELTME (13:40 oturumu): `Resources.FindObjectsOfTypeAll<GameObject>()`
			// Satsuma nesnelerini bulmuyordu (log: "NOTHING bound. Found 0
			// object(s) named Electrics/Starter."). Bu Unity sürümünde
			// FindObjectsOfTypeAll yalnızca AÇIK hiyerarşiyi döndürür;
			// Satsuma'nın `CarSimulation` dalı çoğunlukla pasif.
			//
			// GÜVENİLİR YOL: `SatsumaRegistry.Root` (GameObject.Find ile
			// bulunan SATSUMA kökü) + `GetComponentsInChildren` (pasif dahil).
			//
			// CANLI HATA: 42 kez "battery: NOTHING bound. Found 0 FSM(s)
			// named Electrics/Starter" yazdı. Demek ki arama hiçbir şey
			// bulamıyor. İki olası sebep vardı ve ikisi de burada kapatıldı:
			//   (a) `SatsumaRegistry.Root` null'du (Find aktif olmayan kökte
			//       tutar) → tüm arama boşa düşüyordu;
			//   (b) `CarSimulation` dalı PASİF ve ayrı bir kök altında →
			//       yalnızca Root'un çocuklarına bakmak yetmiyordu.
			// Çözüm: (1) bilinen kök, (2) Satsuma'ya benzeyen HER SAHNE
			// KÖKÜ, (3) en sonda tüm sahne nesneleri.
			PlayMakerFSM[] all = CollectFsm();

			if (all == null || all.Length == 0) all = new PlayMakerFSM[0];

			System.Collections.Generic.List<PlayMakerFSM> hit
				= new System.Collections.Generic.List<PlayMakerFSM>(8);

			for (int i = 0; i < all.Length; i++) {
				PlayMakerFSM fsm = all[i];
				if (fsm == null || fsm.Fsm == null) continue;
				if (!CarElectric(fsm)) continue;
				// Veritabanı kaydı yolunu ele.
				string path = null;
				try { if (fsm.gameObject != null) path = SceneDump.PathOf(fsm.gameObject.transform); } catch { }
				if (path != null && path.IndexOf("Database", StringComparison.OrdinalIgnoreCase) >= 0) continue;
				if (!hit.Contains(fsm)) hit.Add(fsm);
			}

			if (hit.Count > 0) cached = hit.ToArray();
			if (!logged) {
				logged = true;
				if (Bound > 0) {
					SmpLog.Log("battery: keep-full bound to " + Bound
						+ " car electric FSM(s); battery level pinned to 1.0, voltage to "
						+ VOLT_TARGET.ToString("F1") + " V (generator freeze="
						+ (FreezeGenerator ? "on" : "off") + ").");
				}
			}
			if (Bound == 0) Diagnose();
		}

		/// <summary>
		/// Hiçbir FSM bağlanamadıysa NEDENini yaz.
		///
		/// CANLI KANIT (13:03:39 ve 13:08:35):
		///   battery: keep-full bound to 0 car electric FSM(s)
		/// Yani modül HİÇ çalışmıyor ve akü/voltaj hiç kilitlenmiyor.
		/// Bu teşhis olmadan "voltaj düşüyor" belirtisinin kaynağı
		/// belirsiz kalıyordu.
		///
		/// Ne arıyoruz: adı `Electrics`/`Starter` olan her nesnenin
		/// float değişkenlerini döküyoruz. Böylece görürüz:
		///   (a) nesne hiç bulunamıyor mu,
		///   (b) bulunuyor ama float değişkeni `Battery` DEĞİLSE
		///       (belki `Battery` bir bool'dur — asset'te `Electrics`
		///       hem `Battery` bool hem `Charge` float taşıyor).
		/// </summary>
		static float lastDiagnose;

		static void Diagnose() {
			// Her çağrıda tam tarama spam yaratmasın (canlı log: saniyede 20+ kez).
			float now = Time.time;
			if (now - lastDiagnose < 15f) return;
			lastDiagnose = now;
			try {
				// AYNI aramayı kullan: Diagnose'un kendi taraması Bind()'inkinden
				// FARKLIydı ve bu yüzden "0 Electrics/Starter" yazıp asıl
				// sebebi gizliyordu.
				PlayMakerFSM[] all = CollectFsm();
				int objs = 0;
				int electric = 0;
				System.Text.StringBuilder sb = new System.Text.StringBuilder(1200);
				for (int i = 0; all != null && i < all.Length; i++) {
					PlayMakerFSM fsm = all[i];
					if (fsm == null || fsm.Fsm == null || fsm.gameObject == null) continue;
					string path = "?";
					try { path = SceneDump.PathOf(fsm.gameObject.transform); } catch { }
					// FSM'ye üç ayrı yerden bak: nesne adı, FSM adı, yol.
					// Eski sürüm YALNIZCA nesne adına bakıyordu; asset'te
					// nesne 'Starter' olsa bile FSM adi farkli olabiliyor.
					bool named = fsm.gameObject.name == "Electrics"
						|| fsm.gameObject.name == "Starter"
						|| (fsm.Fsm.Name != null && (fsm.Fsm.Name.IndexOf("Starter", StringComparison.OrdinalIgnoreCase) >= 0
							|| fsm.Fsm.Name.IndexOf("Electrics", StringComparison.OrdinalIgnoreCase) >= 0))
						|| (path != null && (path.IndexOf("Electrics", StringComparison.OrdinalIgnoreCase) >= 0
							|| path.IndexOf("Starter", StringComparison.OrdinalIgnoreCase) >= 0));
					if (CarElectric(fsm)) electric++;
					if (!named) continue;
					objs++;
					if (sb.Length > 1100) continue;
					sb.Append("\n        ").Append(fsm.gameObject.name).Append(": ").Append(path)
						.Append("  fsm='").Append(fsm.Fsm.Name).Append("'")
						.Append(CarElectric(fsm) ? "  <== ESLESTI" : "");
					FsmFloat[] fl = null;
					try { fl = fsm.FsmVariables.FloatVariables; } catch { }
					System.Text.StringBuilder vn = new System.Text.StringBuilder(120);
					for (int k = 0; fl != null && k < fl.Length && k < 24; k++) {
						if (fl[k] == null) continue;
						if (vn.Length > 0) vn.Append(",");
						vn.Append(fl[k].Name).Append("=").Append(fl[k].Value.ToString("F2"));
					}
					sb.Append("  floats: ").Append(vn.Length > 0 ? vn.ToString() : "(none)");
					FsmBool[] bl = null;
					try { bl = fsm.FsmVariables.BoolVariables; } catch { }
					System.Text.StringBuilder bn = new System.Text.StringBuilder(120);
					for (int k = 0; bl != null && k < bl.Length && k < 24; k++) {
						if (bl[k] == null) continue;
						if (bn.Length > 0) bn.Append(",");
						bn.Append(bl[k].Name).Append("=").Append(bl[k].Value);
					}
					if (bn.Length > 0) sb.Append("  bools: ").Append(bn.ToString());
				}
				SmpLog.Warning("battery: NOTHING bound."
					+ "\n        kok         : " + RootName()
					+ "\n        tarama      : " + (all != null ? all.Length : 0)
					+ " FSM, " + objs + " Electrics/Starter, " + electric
					+ " aku/voltaj degiskeni tasiyan"
					+ "\n        eslesenler  :" + sb.ToString()
					+ "\n        (IsLevelVar: 'Battery' | 'Volt' | 'Charge' iceren"
					+ " float VEYA bool. Eslesenler '<== ESLESTI' ile isaretlenir.)");
			}
			catch (Exception e) {
				SmpLog.Warning("battery diagnose failed: " + e.GetType().Name);
			}
		}

		/// <summary>
		/// Bu FSM gerçekten bir aracın elektrik hesabı mı?
		///
		/// İşaret: `Battery` veya `db_Battery` float değişkeni. `Database`
		/// altındaki kayıt FSM'lerinde bu değişkenler YOK, bu yüzden
		/// yanlışlıkla onları almıyoruz.
		/// </summary>
		/// <summary>
		/// Kayıtlı kök gerçekten var mıydı? Eski Diagnose "Found 0 ... under
		/// Satsuma" yazıyordu ama kökün adını hiç yazmıyordu; kök null'du
		/// mu yoksa yol başka mıydı belli olmuyordu.
		/// </summary>
		static string RootName() {
			try {
				if (SatsumaRegistry.Root != null)
					return SatsumaRegistry.Root.name + " (SatsumaRegistry.Root)";
			}
			catch { }
			GameObject g = SearchRoot_Brand();
			if (g != null) return g.name + " (sahne koku, kayitli yol null'du)";
			return "(HICBIR SATSUMA KOKU BULUNAMADI - tum sahne kokleri tarandi)";
		}

		/// <summary>
		/// Verilen köklerin altındaki TÜM PlayMakerFSM'leri toplar.
		/// Pasif nesneler dahil (CarSimulation dalı çoğunlukla pasiftir).
		/// Aynı FSM iki kökte de varsa tekrar sayılmaz.
		/// </summary>
		static PlayMakerFSM[] CollectFsm(GameObject[] roots) {
			System.Collections.Generic.List<PlayMakerFSM> acc
				= new System.Collections.Generic.List<PlayMakerFSM>(64);
			for (int r = 0; roots != null && r < roots.Length; r++) {
				GameObject root = roots[r];
				if (root == null) continue;
				PlayMakerFSM[] c = null;
				try { c = root.GetComponentsInChildren<PlayMakerFSM>(true); } catch { }
				for (int i = 0; c != null && i < c.Length; i++) {
					if (c[i] == null) continue;
					if (!acc.Contains(c[i])) acc.Add(c[i]);
				}
			}
			return acc.ToArray();
		}

		/// <summary>
		/// Kayıtlı Satsuma kökü; yoksa "SATSUMA" ile başlayan bir sahne
		/// kökü. Canlı hatada burası null dönüyordu.
		/// </summary>
		static GameObject SearchRoot_Brand() {
			try { if (SatsumaRegistry.Root != null) return SatsumaRegistry.Root; } catch { }
			GameObject[] roots = null;
			try { roots = SceneDump.AllSceneRoots(); } catch { }
			for (int i = 0; roots != null && i < roots.Length; i++) {
				GameObject g = roots[i];
				if (g == null || g.name == null) continue;
				if (g.name.StartsWith("SATSUMA", StringComparison.OrdinalIgnoreCase))
					return g;
			}
			return null;
		}

		/// <summary>
		/// Karşı çare: SAHNEDEKİ TÜM KÖKLER. `CarSimulation` dalı Satsuma
		/// kökünün altında değilse (araçta araçta değişiyor) önceki arama
		/// onu göremezdi.
		/// </summary>
		static GameObject[] SearchRoot_AllScene() {
			GameObject[] roots = null;
			try { roots = SceneDump.AllSceneRoots(); } catch { }
			if (roots == null || roots.Length == 0) return new GameObject[0];
			return roots;
		}

		/// <summary>
		/// Elektrik hesabı taşıyan FSM'leri bul.
		///
		/// MALİYET: Bind() her 10 saniyede bir çalışıyor. 700+ sahne kökünün
		/// hepsini her seferinde taramak pahalı olurdu. Bu yüzden önce
		/// UCUZ yol denenir (kayıtlı/SATSUMA kökü); ancak orada akü değişkeni
		/// taşıyan bir FSM BULUNMAZSA geniş tarama yapılır.
		/// </summary>
		/// <summary>
		/// Elektrik FSM'lerini bul — DOĞRUDAN FSM TARAMASI.
		///
		/// CANLI HATA (host logu, 12:29-12:30):
		///   battery: NOTHING bound.
		///   tarama: 67 FSM, 0 Electrics/Starter, 0 aku/voltaj degiskeni tasiyan
		///
		/// 67! Oysa aynı logda "world: 745 root(s), 6942 FSM(s)" yazıyor.
		/// Yani tarama dünyadaki FSM'lerin %1'ini görüyordu.
		///
		/// SEBEP: kök yürüyüşü. `AllSceneRoots()` yalnızca `parent == null`
		/// olanları kök sayıyor ve `GetComponentsInChildren` ile alt ağaç
		/// geziyor. Satsuma'nın `CarSimulation` dalı bu iki koşulu da
		/// karşılamıyor (kökü başka bir ağacın altında), dolayısıyla
		/// `Electrics`/`Starter` hiç görünmedi.
		///
		/// DÜZELTME: kök yürüyüşü tamamen kaldırıldı. Unity'nin kendi
		/// `Resources.FindObjectsOfTypeAll<PlayMakerFSM>()` çağrısı PASİF
		/// nesneleri de döndürür ve kökten bağımsızdır — aynı yolu
		/// `FluidWearSync.BindWear()` da kullanıyor ve o çalışıyor.
		///
		/// Prefab/asset FSM'leri elenir: `scene.IsValid()` onların
		/// sahnesi yoktur.
		/// </summary>
		/// Prefab/asset kopyaları elenir: `hideFlags` sahnede kullanılan
		/// bir nesneye ait değildir (Unity 5.0'da `GameObject.scene` yok,
		/// 5.3'de geldi — o yüzden `hideFlags` bakılıyor).
		static bool IsAssetCopy(PlayMakerFSM f) {
			try {
				HideFlags h = f.gameObject.hideFlags;
				if ((h & HideFlags.DontSave) != 0) return true;
				if ((h & HideFlags.HideAndDontSave) != 0) return true;
			}
			catch { }
			return false;
		}

		static PlayMakerFSM[] CollectFsm() {
			PlayMakerFSM[] fsms = null;
			try { fsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>(); }
			catch { }
			if (fsms == null || fsms.Length == 0) {
				// Çare: sahne köklerinden dene (eski yol, nadiren işe yarar).
				PlayMakerFSM[] wide = CollectFsm(SearchRoot_AllScene());
				return wide.Length > 0 ? wide : new PlayMakerFSM[0];
			}
			System.Collections.Generic.List<PlayMakerFSM> keep
				= new System.Collections.Generic.List<PlayMakerFSM>(16);
			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM f = fsms[i];
				if (f == null || f.Fsm == null) continue;
				if (IsAssetCopy(f)) continue;
				keep.Add(f);
			}
			return keep.ToArray();
		}

		static bool CarElectric(PlayMakerFSM fsm) {
			try {
				FsmFloat[] all = fsm.FsmVariables.FloatVariables;
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] == null) continue;
					if (IsLevelVar(all[i].Name)) return true;
				}
			}
			catch { }
			// BOOL SEVİYE — ASSET KANITI (docs/fsm_detail/l2__Electrics__Electrics.txt):
			//   satır 544:  Volts   ← komşu: RPM | Battery
			//   satır 545:  Battery ← komşu: Volts | Damaged
			//   satır  24:  Battery ← komşu: PROCEED | GetFsmBool
			//   satır 389:  Battery ← komşu: everyFrame | FINISHED
			//
			// `Battery` bir **FsmBool**'dur (FloatOperator float bekler ama
			// oyun bool→float çevirisi yapıyor). CANLI KANIT: eski
			// `CarElectric()` SADECE float'lara bakıyordu ve log'da
			//   13:03:39 battery: keep-full bound to 0 car electric FSM(s)
			//   13:08:35 battery: keep-full bound to 0 car electric FSM(s)
			// yani hiçbir şey bağlanmadı, akü/voltaj hiç kilitlenmedi.
			//
			// Düzeltme: bool seviye/voltaj değişkenlerini de kabul et.
			try {
				FsmBool[] bl = fsm.FsmVariables.BoolVariables;
				for (int i = 0; bl != null && i < bl.Length; i++) {
					if (bl[i] == null) continue;
					if (IsLevelVar(bl[i].Name)) return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>Akü seviyesi/voltajı değişkeni mi? (parça-parça eşleşme)</summary>
		static bool IsLevelVar(string n) {
			if (string.IsNullOrEmpty(n)) return false;
			string l = n.ToLowerInvariant();
			return l.IndexOf("battery", StringComparison.Ordinal) >= 0
				|| l.IndexOf("volt", StringComparison.Ordinal) >= 0
				|| l.IndexOf("charge", StringComparison.Ordinal) >= 0;
		}

		// ------------------------------------------------------------------
		// Tanı
		// ------------------------------------------------------------------

		/// <summary>'smp battery' çıktısı.</summary>
		public static string Describe() {
			return "battery keep-full: " + (Enabled ? "ON" : "off")
				+ "  HOOK (FsmFloat/FsmBool set_Value, no per-frame scan)"
				+ "  bound=" + Bound + " fsm(s)"
				+ "  hookWrites=" + hookWrites
				+ "  volt=" + VOLT_TARGET.ToString("F1") + " V"
				+ "  drainBlocked=" + BatteryDrainBlock.Blocked
				+ "/" + BatteryDrainBlock.Seen
				+ "  noDrain=" + (NoDrain ? "on" : "off")
				+ "  freezeGen=" + (FreezeGenerator ? "on" : "off");
		}
	}
}
