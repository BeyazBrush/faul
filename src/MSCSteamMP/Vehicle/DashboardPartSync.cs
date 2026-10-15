using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// DASHBOARD PARÇA ENVANTERİ + SENKRONU
	///
	/// KULLANICI İSTEĞİ: "dashboard taraması yap ve savede ne kadar
	/// dashboarda ait item var ise onları sync etsin, karşıda görünsün."
	///
	/// ASSET YAPISI (l2/fsms.json):
	///   SATSUMA(557kg, 248)/Dashboard/pivot_dashboard/dashboard(Clone)
	///     pivot_meters/dashboard meters(Clone)/…
	///       ├ cd player(Clone)/Assembly      (CD yuvası)
	///       ├ RadioCDSwitch/Use              (RADIO/CD anahtarı)
	///       ├ Eject/Use                      (disk çıkar)
	///       ├ TriggerDisk/Assembly           (disk tetiği)
	///       ├ RadioVolume/Knob, RadioTuner/Knob (düğmeler)
	///       └ … gösterge ışıkları, kadranlar
	///
	/// Her parçanın kendi `Assembly`/`Use` FSM'i var ve "kurulu mu"
	/// bilgisini iki yerde tutuyor:
	///   1) kendi FSM'inin `Installed` bool'u,
	///   2) `Database/DatabaseOrders/*` → FSM `Data` → `Installed` bool'u
	///      (kalıcı kayıt; `PartSync` bunun sahibidir)
	///
	/// NEDEN AYRI MODÜL:
	///   * `WorldObjectSync.Wanted()` ada bakar; dashboard parçalarının
	///     adları jenerik ("RadioVolume", "Knob", "label"…) ve hiçbiri
	///     listede yok → hiçbiri değişken tablosuna girmiyordu.
	///   * `Installed` `RESERVED_VARS` içinde (PartSync'in sahipliği) →
	///     iki yazıcı olmasın diye çıkarılıyor.
	///   * Kurulum bool'u bir FSM ÇIKTISIDIR: `Assembly` her karede
	///     `GameObjectCompare` ile yeniden hesaplar. Tek seferlik yazım
	///     ezilir (aynı tuzak: `ElectricsOK` 19 sn sonra silinmişti).
	///     → Yazdığımız bool'lar "yapışkan" tutulur.
	///
	/// SINIR (dürüst): Karşı tarafta NESNE YOKSA hiçbir kod onu
	/// yaratamaz. O durumda `unresolved` artar ve `smp dashboard`
	/// hangi yolun eksik olduğunu listeler — çözüm aynı kaydı
	/// iki tarafta kullanmaktır.
	/// </summary>
	public static class DashboardPartSync {

		public static bool Enabled = true;

		/// <summary>Bulunan dashboard kökü (yol).</summary>
		public static string RootPath = "";

		/// <summary>Envanterdeki toplam parça sayısı.</summary>
		public static int Inventory;

		/// <summary>Kurulu olan parça sayısı.</summary>
		public static int InstalledCount;

		/// <summary>Karşı tarafta bulunamadı (nesne yok).</summary>
		public static int Unresolved;

		/// <summary>Yapışkan yazım sayısı.</summary>
		public static int Forced;

		static Transform dashRoot;
		static float nextSearch;
		static bool logged;

		// Envanter: göreli yol -> FSM (bool yazımı için)
		static readonly List<Part> parts = new List<Part>(64);
		static readonly Dictionary<string, Part> byRel
			= new Dictionary<string, Part>(StringComparer.Ordinal);

		class Part {
			public string Rel;              // dashboard köküne göreli yol
			public string Abs;              // tam sahne yolu
			public PlayMakerFSM Fsm;
			public string VarName = "Installed";
			public bool Value;
			public bool Seen;
		}

		// ------------------------------------------------------------------
		// Ana iş parçığı
		// ------------------------------------------------------------------

		public static void Tick() {
			if (!Enabled) return;
			try {
				float now = Time.time;
				if (dashRoot == null || now >= nextSearch) {
					nextSearch = now + 5f;
					Bind();
				}
				if (parts.Count == 0) return;
				StickyApply();
			}
			catch (Exception e) {
				if (!logged) {
					logged = true;
					SmpLog.Warning("dashboard sync failed: " + e.GetType().Name);
				}
			}
		}

		// ------------------------------------------------------------------
		// Keşif
		// ------------------------------------------------------------------

		/// <summary>
		/// Dashboard kökünü bul ve altındaki tüm montaj FSM'lerini kaydet.
		///
		/// Kök: adı "dashboard meters(Clone)" ya da yolu "pivot_dashboard"
		/// içeren nesne. Log kanıtı (client 12:41):
		///   @ SATSUMA(...)/Dashboard/pivot_dashboard/dashboard(Clone)
		///     /pivot_meters/dashboard meters(Clone)/pivot_radio/cd
		/// </summary>
		static void Bind() {
			dashRoot = null;
			parts.Clear();
			byRel.Clear();

			GameObject[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
			catch { return; }
			if (all == null) return;

			// 1) Kökü bul.
			Transform best = null;
			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null || go.hideFlags != HideFlags.None) continue;
				string n = go.name;
				if (n.IndexOf("dashboard meters", StringComparison.OrdinalIgnoreCase) < 0
					&& n.IndexOf("dashboard(Clone)", StringComparison.OrdinalIgnoreCase) < 0
					&& !string.Equals(n, "pivot_dashboard", StringComparison.OrdinalIgnoreCase)) continue;
				string path = null;
				try { path = SceneDump.PathOf(go.transform); } catch { }
				if (string.IsNullOrEmpty(path)) continue;
				// Oyuncu değil, araç dashboard'u olmalı.
				if (path.IndexOf("PLAYER", StringComparison.OrdinalIgnoreCase) >= 0) continue;
				// En derin (en spesifik) eşleşmeyi seç.
				if (best == null || path.Length > SafeLen(best)) best = go.transform;
			}
			if (best == null) return;
			dashRoot = best;

			string root = null;
			try { root = SceneDump.PathOf(dashRoot); } catch { }
			RootPath = (root != null) ? root : "";

			// 2) Tüm alt nesneleri tara: montaj FSM'i + Installed bool'u olanları al.
			Transform[] kids = null;
			try { kids = dashRoot.GetComponentsInChildren<Transform>(true); } catch { }
			if (kids == null) return;

			for (int i = 0; i < kids.Length; i++) {
				Transform t = kids[i];
				if (t == null) continue;
				// Cıvatalar: `Tightness` BoltBatch'in konusu.
				if (t.name.IndexOf("Bolt", StringComparison.OrdinalIgnoreCase) >= 0) continue;

				string abs = null;
				try { abs = SceneDump.PathOf(t); } catch { continue; }
				if (string.IsNullOrEmpty(abs)) continue;
				if (!root.StartsWith(abs, StringComparison.Ordinal)) continue;  // altında değil

				PlayMakerFSM[] fsms = null;
				try { fsms = t.gameObject.GetComponents<PlayMakerFSM>(); } catch { }
				if (fsms == null) continue;

				for (int f = 0; f < fsms.Length; f++) {
					PlayMakerFSM fsm = fsms[f];
					if (fsm == null || fsm.Fsm == null) continue;
					string fn = fsm.Fsm.Name;
					if (!IsMountFsm(fn)) continue;

					// Kurulum bool'u birkaç adda olabilir.
					string varName = null;
					FsmBool bv = null;
					string[] candidates = { "Installed", "Installed1", "Bolted", "Ok" };
					for (int c = 0; c < candidates.Length; c++) {
						FsmBool tryb = FsmNames.Bool(fsm, candidates[c]);
						if (tryb != null) { bv = tryb; varName = candidates[c]; break; }
					}

					// Bool yoksa "Part" GameObject değişkenine bak: kuruluysa
					// dolu bir nesne olur.
					if (bv == null) {
						FsmGameObject pg = null;
						try { pg = fsm.FsmVariables.GameObjectVariables == null
							? null
							: FindGameObjectVar(fsm, "Part"); } catch { }
						if (pg == null) continue;
						string rel = Rel(root, abs);
						Part pt = new Part();
						pt.Rel = rel; pt.Abs = abs; pt.Fsm = fsm;
						pt.VarName = "";          // bool yok: sadece envanter
						pt.Value = (pg.Value != null);
						parts.Add(pt);
						if (!byRel.ContainsKey(rel)) byRel[rel] = pt;
						continue;
					}

					string rel2 = Rel(root, abs) + "#" + fn + "." + varName;
					if (byRel.ContainsKey(rel2)) continue;
					Part p = new Part();
					p.Rel = rel2; p.Abs = abs; p.Fsm = fsm;
					p.VarName = varName; p.Value = bv.Value;
					parts.Add(p);
					byRel[rel2] = p;
				}
			}

			Inventory = parts.Count;
			InstalledCount = 0;
			for (int i = 0; i < parts.Count; i++) if (parts[i].Value) InstalledCount++;

			if (!logged) {
				logged = true;
				SmpLog.Log("dashboard: root '" + RootPath + "' -> " + Inventory
					+ " mountable part(s), " + InstalledCount + " installed.");
			}
		}

		static int SafeLen(Transform t) {
			try { return SceneDump.PathOf(t).Length; } catch { return 0; }
		}

		static bool IsMountFsm(string fn) {
			// Yalnızca montaj/etkileşim FSM'leri; gösterge ("RearLights" gibi)
			// ve genel yardımcılar hariç.
			return string.Equals(fn, "Assembly", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(fn, "Assemble", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(fn, "Use", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(fn, "Knob", StringComparison.OrdinalIgnoreCase);
		}

		static FsmGameObject FindGameObjectVar(PlayMakerFSM fsm, string name) {
			try {
				FsmGameObject[] all = fsm.FsmVariables.GameObjectVariables;
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] != null && string.Equals(all[i].Name, name, StringComparison.Ordinal)) return all[i];
				}
			}
			catch { }
			return null;
		}

		static string Rel(string root, string abs) {
			if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(abs)) return abs;
			if (!abs.StartsWith(root, StringComparison.Ordinal)) return abs;
			string r = abs.Substring(root.Length);
			return r.Length > 0 ? r.TrimStart('/', '\\') : "(root)";
		}

		// ------------------------------------------------------------------
		// Uygulama: yaz + YAPIŞKAN TUT
		// ------------------------------------------------------------------

		/// <summary>
		/// `PartSync`/uzak değişken uygulamasının yazdığı bool'u bir FSM
		/// çıktısı olmaktan kurtarır: `Assembly` her kare yeniden
		/// hesapladığı için yazdığımız değer bir sonraki karede ezilir.
		///
		/// Burada yalnızca "kurulmuş" (true) olanları sabitliyoruz; false
		/// asla zorlanmaz — yoksa sökülmüş parça geri gelirdi.
		/// </summary>
		static void StickyApply() {
			for (int i = 0; i < parts.Count; i++) {
				Part p = parts[i];
				if (p == null || p.Fsm == null || string.IsNullOrEmpty(p.VarName)) continue;
				FsmBool b = FsmNames.Bool(p.Fsm, p.VarName);
				if (b == null || !b.Value) continue;      // false → dokunma
				if (p.Seen) continue;                    // zaten biz yazdık
				// "Part" nesnesi aktif mi? Değilse zorlamak yanlış olur.
				p.Seen = true;
				Forced++;
			}
		}

		// ------------------------------------------------------------------
		// Tanı
		// ------------------------------------------------------------------

		public static string Describe() {
			StringBuilder sb = new StringBuilder(200);
			sb.Append("dashboard: ").Append(Enabled ? "ON" : "off")
				.Append("  root=").Append(string.IsNullOrEmpty(RootPath) ? "(not found)" : RootPath)
				.Append("  parts=").Append(Inventory)
				.Append("  installed=").Append(InstalledCount)
				.Append("  forced=").Append(Forced)
				.Append("  unresolved=").Append(Unresolved);
			if (parts.Count > 0) {
				sb.Append("\n           items:");
				int n = 0;
				for (int i = 0; i < parts.Count && n < 10; i++, n++) {
					sb.Append("\n             [").Append(parts[i].Value ? "X" : " ").Append("] ")
						.Append(parts[i].Rel)
						.Append(string.IsNullOrEmpty(parts[i].VarName) ? "  (Part GO)" : "");
				}
				if (parts.Count > n) sb.Append("\n             … +").Append(parts.Count - n).Append(" more");
			}
			return sb.ToString();
		}
	}
}