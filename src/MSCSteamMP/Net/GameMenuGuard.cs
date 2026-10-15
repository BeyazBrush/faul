using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// OYUNUN KENDİ ESC MENÜSÜNÜ TESPİT EDER (raporlama + yedek imleç düzeltmesi).
	///
	/// Belirti: dünyada ESC'ye basınca oyunun kendi SETTINGS MENU'su
	/// açılıyor (Btn_Resume / Btn_Graphics / Btn_Quit içeren panel) ama
	/// fare imleci görünmüyor, dolayısıyla tıklanamıyor.
	///
	/// ANA DÜZELTME ARTIK BURADA DEĞİL
	/// ---------------------------------
	/// dnSpy kanıtı: oyunun imleç kararı
	/// HutongGames.PlayMaker.Actions.SetMouseCursor.OnEnter (token 0x06000DC9)
	/// yalnızca PlayMakerGUI.HideCursor / LockCursor ALANLARINA yazıyor;
	/// gerçek Cursor.visible'ı hiç dokunmuyor. Bu yüzden asıl imleç
	/// düzeltmesi Core/CursorGuard.cs içindedir ve menü NESNESİNİ bulmaya
	/// ihtiyaç duymaz. Bu sınıf sadece 'smp status' için menü varlığını
	/// bildirir ve menü bulunursa imleci açık tutar.
	///
	/// PERFORMANS NOTU (önemli)
	/// ------------------------
	/// Bu sınıfın eski sürümü menüyü bulamayınca 0.5 saniyede bir, SINIRSIZ
	/// defa sahne köklerini tarıyordu; üstelik 9 aday nesne adı için tarama
	/// 9 KEZ TEKRARLANIYORDU. MSC'de sahne kökü sayısı ~295 ve her
	/// GetComponentsInChildren&lt;Transform&gt;(true) çağrısı yeni bir dizi
	/// tahsis eder. Sonuç: saniyede iki kez binlerce kısa ömürlü dizi,
	/// yani GC baskısı. Kullanıcı "300 FPS almama rağmen kasılıyor" diye
	/// bildirdi; bu tarama bunun bir parçasıydı.
	///
	/// Düzeltmeler:
	///   * Sahne kökleri TEK KEZ gezilir, transform'lar scanCache'e toplanır.
	///   * 9 aday ad bu TEK liste üzerinde aranır (9 tarama değil, 1).
	///   * Üstel geri çekilme: 2, 4, 8, 16, 32 sn.
	///   * 5 denemeden sonra VAZGEÇİLİR ve bir kez bildirilir.
	///
	/// Unity 5 / .NET 3.5 uyumlu; hiçbir istisna dışarı sızmaz.
	/// </summary>
	public static class GameMenuGuard {

		/// <summary>Tarama deneme sayısı. 5 denemeden sonra vazgeçilir.</summary>
		const int MAX_ATTEMPTS = 5;

		/// <summary>Üstel geri çekilmenin tabanı, saniye.</summary>
		const float BACKOFF_BASE = 2f;

		/// <summary>Tarama YAPILDIKTAN SONRA boşaltılan transform listesi.</summary>
		static readonly List<Transform> scanCache = new List<Transform>(512);

		/// <summary>Oyunun menü nesnesi aktif mi (bulunduğunda)?</summary>
		static GameObject menuRoot;
		/// <summary>Menü şu an görünür mü (bulunduğunda)?</summary>
		static bool menuVisible;

		/// <summary>Oyunun ESC menüsü bulundu ve açık mı? 'smp status' bunu gösterir.</summary>
		public static bool GameMenuOpen { get { return menuVisible; } }

		/// <summary>Tarama yapıldı mı? (Yoksa "aranıyor" anlamına gelir.)</summary>
		public static bool ScanFinished { get { return attempts >= MAX_ATTEMPTS; } }

		/// <summary>Menü nesnesinin adı (teşhis için), yoksa null.</summary>
		public static string MenuObjectName {
			get { return menuRoot != null ? menuRoot.name : (ScanFinished ? "(absent)" : "(searching)"); }
		}

		static float nextScan;
		static int attempts;
		static bool loggedFound;
		static bool loggedMissing;

		/// <summary>
		/// Kare başına bir kez çağrılır (SteamMPBehaviour.Update içinden).
		/// </summary>
		public static void Tick() {
			float t;
			try { t = Time.time; } catch { return; }

			if (menuRoot == null) {
				// Vazgeçildi: imleci artık CursorGuard yönetiyor; tarama
				// durur ve periyodik yük tamamen biter.
				if (attempts >= MAX_ATTEMPTS) return;
				if (t < nextScan) return;

				// Üstel geri çekilme: 2, 4, 8, 16, 32 sn. Sahne yüklenirken
				// nesneler henüz oluşmamış olabilir; ilk denemeler sık,
				// sonrakiler seyrek.
				if (attempts > 0) nextScan = t + BACKOFF_BASE * (1 << (attempts - 1));
				else nextScan = t;
				attempts++;

				FindMenu();
				if (menuRoot == null && !loggedMissing && attempts >= MAX_ATTEMPTS) {
					loggedMissing = true;
					SmpLog.Print("game ESC menu object not found after " + MAX_ATTEMPTS
						+ " attempts - stopping the scan. The cursor itself is handled "
						+ "by the cursor guard, so this is harmless.");
				}
				return;
			}

			menuVisible = IsMenuVisible(menuRoot);
			ApplyCursor();
		}

		/// <summary>
		/// Oyunun ESC menü nesnesini bulur.
		///
		/// Sahne kökleri TEK KEZ gezilir, tüm transform'lar scanCache'e
		/// toplanır; ardından hem "SETTINGS MENU" hem 9 aday kapsayıcı adı
		/// bu TEK liste üzerinde aranır.
		/// </summary>
		static void FindMenu() {
			scanCache.Clear();
			GameObject[] roots = SceneDump.AllSceneRoots();
			if (roots == null || roots.Length == 0) return;

			for (int r = 0; r < roots.Length; r++) {
				if (roots[r] == null) continue;
				Transform[] all;
				try { all = roots[r].GetComponentsInChildren<Transform>(true); }
				catch { continue; }
				if (all == null) continue;
				for (int i = 0; i < all.Length; i++) {
					if (all[i] != null) scanCache.Add(all[i]);
				}
			}

			// 1) level2 taramasında bulunan EN KESİN isaret: "SETTINGS MENU".
			for (int i = 0; i < scanCache.Count; i++) {
				Transform t = scanCache[i];
				if (t == null) continue;
				if (!string.Equals(t.name, "SETTINGS MENU", StringComparison.OrdinalIgnoreCase)) continue;
				Transform panel = t.parent;   // yazının bir üst seviyesi = panel
				if (panel == null) panel = t;
				Note(panel.gameObject);
				return;
			}

			// 2) Bilinen aday kapsayıcı adları (yedek) - artık TEK tarama.
			string[] candidates = {
				"MenuFSM", "Settings", "Settings 2", "Settings 3", "Settings 4",
				"Menu 2", "Menu 3", "Menu 4", "Menu 5",
			};
			for (int c = 0; c < candidates.Length; c++) {
				for (int i = 0; i < scanCache.Count; i++) {
					Transform t = scanCache[i];
					if (t == null || t.name != candidates[c]) continue;
					if (!HasSettingsPanel(t.gameObject)) continue;
					Note(t.gameObject);
					return;
				}
			}

			// 3) Son çare: Btn_* düğmeleri olan bir panel.
			for (int i = 0; i < scanCache.Count; i++) {
				Transform t = scanCache[i];
				if (t == null || t == t.root) continue;
				if (!HasSettingsPanel(t.gameObject)) continue;
				Note(t.gameObject);
				return;
			}
		}

		/// <summary>Menüyü kaydeder ve bir kez teşhis yazar.</summary>
		static void Note(GameObject go) {
			menuRoot = go;
			if (!loggedFound) {
				loggedFound = true;
				loggedMissing = false;
				SmpLog.Print("game ESC menu found: '" + go.name + "'.");
			}
		}

		/// <summary>
		/// Bu nesne ESC menüsünün kendisi mi? Altında en az iki Btn_ düğmesi olmalı
		/// (RESUME + en az bir tane daha).
		/// </summary>
		static bool HasSettingsPanel(GameObject go) {
			Transform[] all;
			try { all = go.GetComponentsInChildren<Transform>(true); }
			catch { return false; }
			if (all == null || all.Length == 0) return false;

			int buttons = 0;
			int children = 0;
			for (int i = 0; i < all.Length && children < 200; i++) {
				Transform t = all[i];
				if (t == null || t == go.transform) continue;
				children++;
				string n = t.name;
				if (string.IsNullOrEmpty(n)) continue;
				if (n.StartsWith("Btn_", StringComparison.OrdinalIgnoreCase)) buttons++;
			}
			return buttons >= 2;
		}

		/// <summary>
		/// Menü şu an görünür mü? Aktif olması yetmez: en az bir etkin çocuğu olmalı.
		/// </summary>
		static bool IsMenuVisible(GameObject go) {
			try {
				if (!go.activeInHierarchy) return false;
				Transform t = go.transform;
				for (int i = 0; i < t.childCount; i++) {
					Transform c = t.GetChild(i);
					if (c != null && c.gameObject.activeInHierarchy) return true;
				}
				return false;
			}
			catch { return false; }
		}

		/// <summary>
		/// Yedek imleç düzeltmesi: menü bulunduysa ve görünüyorsa imleci aç.
		/// (Asıl düzeltme CursorGuard'da; bu sadece ikinci emniyet.)
		/// </summary>
		static void ApplyCursor() {
			if (!menuVisible) return;
			try {
				if (!Cursor.visible) Cursor.visible = true;
				if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
			}
			catch { }
		}

		/// <summary>Sahne değişiminde referansları bırakır.</summary>
		public static void Reset() {
			menuRoot = null;
			menuVisible = false;
			loggedFound = false;
			loggedMissing = false;
			attempts = 0;
			nextScan = 0f;
			scanCache.Clear();
		}
	}
}