using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Sahne ağacını gezen dört küçük yardımcı.
	///
	/// Burada önce 410 satırlık bir "runtime sahne müfettişi" vardı: nesne arama,
	/// FSM dökümü, değişken arama, sahne özeti. Hepsi konsol komutlarına
	/// (smp ls / go / fsm / var / scan / scene) besleniyordu.
	///
	/// O komutlar kaldırıldı çünkü varlık sebebi ortadan kalktı: kullanıcı artık
	/// sahneyi kendisi dökmek zorunda değil, mod ne yaptığını hem ekran konsöründe
	/// hem de mscmp.txt dosyasında kendisi anlatıyor. Sahne dökümü gerektiğinde
	/// oyunu durdurmadan bakmak için " tuşu açılır.
	///
	/// Kalan dört metot gerçekten kullanılıyor:
	///
	///   SceneRoots / ActiveSceneRoot   - ItemSync, WorldSync, MenuController
	///   ActiveSceneName                - log satırlarına sahne adı yazmak için
	///   PathOf                         - nesneleri okunur yolda adlandırmak için
	///
	/// Neden Unity 5.0'a özgü bir numara var: SceneManager 5.3'te geldi. Aktif
	/// sahne, aktif bir kameradan alınıp transform yukarı yürütülerek bulunuyor.
	/// Menüde de kameralar aktif olduğu için bu yöntem ana menüde de çalışıyor,
	/// ki genelde arızan oturumun durduğu yer menüdür.
	/// </summary>
	public static class SceneDump {

		/// <summary>Aktif sahnenin kök nesnesi, ya da hiçbir şey yüklü değilse null.</summary>
		public static GameObject ActiveSceneRoot() {
			Camera[] cams = Camera.allCameras;
			for (int i = 0; i < cams.Length; i++) {
				Camera c = cams[i];
				if (c == null || !c.isActiveAndEnabled) continue;
				Transform t = c.transform;
				int guard = 0;
				while (t != null && t.parent != null && guard++ < 64) t = t.parent;
				if (t != null) return t.gameObject;
			}
			return null;
		}

		/// <summary>Aktif sahnenin kökleri. Sahne yoksa boş dizi.</summary>
		public static GameObject[] SceneRoots() {
			GameObject root = ActiveSceneRoot();
			if (root == null) return EmptyRoots;
			return new GameObject[] { root };
		}

		/// <summary>
		/// Sahne icindeki TUM kok nesneler (kameranin zinciri degil).
		///
		/// Neden: ActiveSceneRoot kameranin en ust ebeveynini doner; kamera tek
		/// basina kok ise tarama yalnizca kamerayi kapsar, kamera bir dunya
		/// kokunun altindaysa yalnizca o alt agaci. Log kaniti: items taramasi
		/// 86 FSM bulurken Satsuma tek basina 500+ FSM tasiyordu; world 0,
		/// items 0 baglaniyordu. Dunya capinda kesif (WorldSync/ItemSync) buradan
		/// yapilmalidir. Unity 5.0'da SceneManager yok (5.3'te geldi), bu yuzden
		/// Resources.FindObjectsOfTypeAll kullanilir; prefab/asset elenir.
		/// </summary>
		// F07_ROOT_CACHE
		//
		// `AllSceneRoots` HER CAGIRIDA Unity'nin tum yuklu nesne
		// havuzunu donduruyordu:
		//     Resources.FindObjectsOfTypeAll<Transform>()
		// Canli olcum: ~37.000 nesne (WorldObjectSync.cs:156 yorumu).
		// List<GameObject> + ToArray() = her cagrida 2 tahllis.
		//
		// 21 cagri noktasi bunu dogrudan cagliyordu; en kotusu
		// `CarrySync.ResolveByPath` (CarrySync.cs:1001) -- HER KARE,
		// tasima varken. Dosyanin kendi notu bunu zaten
		// belgeliyor (HeldItemSync.cs:1015-1016): "ONBELLEK ZORUNLU:
		// SceneDump.AllSceneRoots() Resources.FindObjectsOfTypeAll
		// cagirir, yani TUM sahne..."
		//
		// Cache BURAYA konur, cagri noktalarina degil: 21 cagiranin
		// 21'i birden kazanir.
		//
		// GECERSIZLESTIRME iki katmanli:
		//  (a) `Clear()` -- sahne degisiminde AÇIKÇA cagrilir.
		//  (b) kok NESNE SAYISI degisirse otomatik dusurulur.
		//      Sahne yuklendiginde sayim bir kare sonra otomatik
		//      duzelir; bu, yeni sahneye GECIKMIS bir kare demek.
		//      Yanlis nesne cozmektense bir kare gecikmeli dogru
		//      cozmek yeğdir.
		static GameObject[] cachedRoots;
		static int cachedRootCount = -1;

		/// <summary>
		/// F07_ROOT_CACHE: sahne degisimi. Kok nesne onbellegini dusurur.
		/// </summary>
		public static void Clear() {
			cachedRoots = null;
			cachedRootCount = -1;
		}

		public static GameObject[] AllSceneRoots() {
			try {
				Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
				if (all == null) return SceneRoots();
				List<GameObject> roots = new List<GameObject>(32);
				for (int i = 0; i < all.Length; i++) {
					Transform t = all[i];
					if (t == null || t.parent != null) continue;
					GameObject go = null;
					try { go = t.gameObject; } catch { continue; }
					if (go == null) continue;
					HideFlags f;
					try { f = go.hideFlags; } catch { continue; }
					if (f != HideFlags.None && f != HideFlags.HideInHierarchy) continue;
					roots.Add(go);
				}
				if (roots.Count == 0) return SceneRoots();
				// F07_ROOT_CACHE: ayni kok sayisi ise onbellek gecerli.
				// Sahne degisimi kok SAYISINI degistirir (eski koklar
				// Destroy edilir, yenileri gelir); bu yuzden sayim
				// degismisse onbellek DUSURULUR.
				if (cachedRoots != null && cachedRootCount == roots.Count)
					return cachedRoots;
				GameObject[] arr = roots.ToArray();
				cachedRoots = arr;
				cachedRootCount = arr.Length;
				return arr;
			}
			catch {
				return SceneRoots();
			}
		}

		static readonly GameObject[] EmptyRoots = new GameObject[0];

		/// <summary>Aktif sahnenin adı, kök nesneden türetilir.</summary>
		public static string ActiveSceneName() {
			GameObject root = ActiveSceneRoot();
			return root != null ? root.name : "(none)";
		}

		/// <summary>
		/// Bir yol ifadesindeki KÖK nesnenin adı.
		///
		/// NEDEN GEREKLİ (dump'ta doğrulandı): oyunun bazı kök adları "/"
		/// içeriyor. Ölçüm: `GIFU(750/450psi)`. `PathOf` adları "/" ile
		/// birleştirdiği için o aracın bir parçasının yolu
		/// `GIFU(750/450psi)/LOD/...` olur — ilk "/" den bölmek kökü
		/// `GIFU(750` sanar ve arama sessizce başarısız olur.
		///
		/// Eskiden 13 yerde `path.IndexOf('/')` ile "ilk kesme" yapılıyordu
		/// ve bu araç için hepsi yanlış kök üretiyordu.
		///
		/// ÇÖZÜM: önce sahnenin GERÇEK kök adlarına bak, yolun hangi kökle
		/// başladığını en uzun eşleşmeyle bul. Hiç eşleşmezse (ya da kök
		/// listesi alınamazsa) eski davranışa dön: ilk "/" den öncesi.
		/// </summary>
		public static string RootNameOf(string path) {
			if (string.IsNullOrEmpty(path)) return path;

			GameObject[] roots = null;
			try { roots = SceneRoots(); } catch { roots = null; }
			if (roots != null) {
				string best = null;
				for (int i = 0; i < roots.Length; i++) {
					GameObject r = roots[i];
					if (r == null) continue;
					string n = r.name;
					if (string.IsNullOrEmpty(n)) continue;
					// Yol ya tam bu köktür ya da bu kök + "/" ile devam eder.
					if (path == n || path.StartsWith(n + "/", StringComparison.Ordinal)) {
						if (best == null || n.Length > best.Length) best = n;
					}
				}
				if (best != null) return best;
			}

			// Yedek: eski davranış (ilk "/"). "/" içeren kök adı için
			// yanlıştır ama kök listesi boşsa tek şansımız budur.
			int slash = path.IndexOf('/');
			return slash > 0 ? path.Substring(0, slash) : path;
		}

		/// <summary>
		/// Yolu "kok adi" + "kalan yol" olarak guvenli boler. ASLA istisna atmaz.
		///
		/// NEDEN GEREKLI (canli kaza, 22:56 oturumu):
		///   `RootNameOf` yolun TAMAMI kok oldugunda (`path == n` dali) yolun
		///   kendisini dondurur, yani `.Length == path.Length` olur. Alti ayri
		///   yerde su kaliyordu:
		///
		///       int slash = SceneDump.RootNameOf(path).Length;
		///       string rest = slash > 0 ? path.Substring(slash + 1) : null;
		///
		///   `slash + 1 == path.Length + 1` oldugu icin
		///   `String.Substring(int startIndex)` "Argument is out of range.
		///   Parameter name: startIndex" atiyordu -- saniyede ~8 kez. Oyuncu
		///   tasima paketlerinin tamami islenemedigi icindir, log da gercek
		///   tanilari baskiya uzerdi.
		///
		/// "/" hic icermeyen yollarda da ayni sekilde patlardi (`RootNameOf`
		/// yedege dustugu icin `slash == path.Length`).
		///
		/// COZUM: tek noktada boleriz, uzunluk ve '/' kontrolunu iceride yapariz.
		/// </summary>
		public static void SplitRoot(string path, out string rootName, out string rest) {
			// Sahne kokleri oyun motorundan gelir. Mono bu cagriyi JIT aninda
			// dogrular ve test kosulunda SecurityException firlatir -- ust
			// methoddaki try/catch YETMEZ. Bu yuzden asil is yapan saf
			// surum ayri bir overload'da; oyun ici yol yalnizca onu cagirir.
			SplitRoot(path, RootNameOf(path), out rootName, out rest);
		}

		/// <summary>
		/// SAF surum: kok adi BILINEN yolu guvenle boler.
		///
		/// Unity API'sine dokunmaz, bu yuzden probe testlerinden cagrilabilir
		/// (bkz. CoreProbe.RunPathSplit). Butunluk denetimi buradadir:
		/// hicbir girdi degeri istisna firlatamaz.
		/// </summary>
		public static void SplitRoot(string path, string sceneRootName, out string rootName, out string rest) {
			rootName = path; rest = null;
			if (string.IsNullOrEmpty(path)) return;

			int len = path.Length;
			int n = string.IsNullOrEmpty(sceneRootName) ? 0 : sceneRootName.Length;
			// Kok adi yoldan uzunsa (bozuk/eskiden kalan kok listesi) yoksay.
			if (n > len) n = 0;

			if (n <= 0) {
				// Kok bilinmiyor: eski "ilk /" yedegi. IndexOf hicbir zaman
				// istisna firlatmaz, yalniz bolmez.
				int slash = path.IndexOf('/');
				if (slash > 0) { rootName = path.Substring(0, slash); rest = path.Substring(slash + 1); }
				return;
			}

			rootName = path.Substring(0, n);
			// '/' gercekten orada mi? Yoksa "kalan" yok demektir -- ve boyle
			// kalindiiginda Substring(Length + 1) CAGRILMAZ. Asil hata buydu.
			rest = (n < len && path[n] == '/') ? path.Substring(n + 1) : null;
		}

		/// <summary>
		/// Kökten nesneye giden tam yol, örnek:
		/// <c>SATSUMA(557kg, 248)/Chassis/FL/strut</c>.
		///
		/// Bu yol, parça kimliklerinin anahtarıdır; iki tarafın aynı isimleri
		/// üretmesi gerekir, bu yüzden nesne adı + hiyerarşi birlikte kullanılır.
		/// </summary>
		public static string PathOf(Transform t) {
			if (t == null) return string.Empty;
			Stack<string> parts = new Stack<string>();
			Transform cur = t;
			int guard = 0;
			while (cur != null && guard++ < 64) {
				parts.Push(cur.name);
				cur = cur.parent;
			}
			StringBuilder sb = new StringBuilder(96);
			while (parts.Count > 0) {
				if (sb.Length > 0) sb.Append('/');
				sb.Append(parts.Pop());
			}
			return sb.ToString();
		}
	}
}