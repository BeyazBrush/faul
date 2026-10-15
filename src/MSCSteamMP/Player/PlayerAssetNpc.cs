using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// NPC OYUNCU MODELİNİ YÜKLER.
	///
	/// Bu model, oyunun kendi verisinden çıkarıldı:
	///   mesh   : level2 › TRAFFIC/VehiclesHighway/VICTRO/LOD/Passengers/Driver/bodymesh
	///            (bodymesh, 1709 vertex, 2680 üçgen, 3 submesh)
	///   iskelet: 20 skin kemiği, kök Char/skeleton/pelvis
	///   doku   : char_shirt04 + char_pants01 + char_face02 (sürücü kıyafeti)
	///   animasyon: Idle, Walk, DriveIdle, Drive, CarIn, CarOut, Wave
	///
	/// Üretim hattı (tek seferlik):
	///   tools\extract_npc_dump.py    → mesh/iskelet/clip JSON
	///   tools\extract_textures.py    → 45 doku PNG
	///   tools\make_char_data.py      → mpnpc.dat (ikili)
	///   build_bundle.cmd             → bu bundle
	///
	/// Neden ayrı bir sınıf
	/// -------------------
	/// PlayerAsset MPPlayerModel'i (MSCMP karakteri) yükler ve orada kalmaya
	/// devam eder: bundle hazır değilse ya da bir şey ters giderse eski yol
	/// çalışmaya devam etsin. İki model birbirinin yedeği.
	///
	/// Unity 5.0 API TUZAKI
	/// --------------------
	/// AssetBundle.CreateFromMemory ASENKRON bir AssetBundleCreateRequest
	/// döndürür. Senkron açılım CreateFromMemoryImmediate'dir.
	///
	/// mscorlib 2.0
	/// -------------
	/// Stream.CopyTo yok; bayt kopyası elle yazıldı.
	/// </summary>
	public static class PlayerAssetNpc {

		/// <summary>Gömülü kaynak adı (build.cmd'daki /resource ile eşleşmeli).</summary>
		public const string RESOURCE = "SteamMP.npc.bundle";

		/// <summary>
		/// Bundle içindeki prefab yolu. KONTEYNER YOLLARI KÜÇÜK HARFE
		/// ÇEVRİLİR ve LoadAsset büyük/küçük harfe duyarlıdır; yanlış
		/// yazılırsa prefab null döner ve uzak oyuncu gövdesi hiç doğmaz.
		/// unity\MPNPCBuilder\Assets\MPNPC\mpnpc.prefab ile eşleşmeli.
		/// </summary>
		public const string MODEL_PATH = "assets/mpnpc/mpnpc.prefab";

		/// <summary>Bundle'daki clip adları (Animation component'inde bu adlarla).</summary>
		public const string CLIP_IDLE = "Idle";
		public const string CLIP_WALK = "Walk";
		public const string CLIP_DRIVE = "Drive";
		public const string CLIP_DRIVE_IDLE = "DriveIdle";
		public const string CLIP_CAR_IN = "CarIn";
		public const string CLIP_CAR_OUT = "CarOut";
		public const string CLIP_WAVE = "Wave";

		static bool attempted;
		static bool compiledIn;
		static AssetBundle bundle;
		static GameObject prefab;
		static string problem = "";
		static int resourceBytes;

		/// <summary>govde vertex sayisi -> prefab yolu. Bundle'da 6 govde var
		/// (960 cocuk, 1697 kadin, 1709/1712/1714 erkek, 1789 uzun).
		/// mscorlib 2.0: Dictionary KULLANILAMAZ (derlenmiyor); iki paralel
		/// liste tutulur.</summary>
		static readonly System.Collections.Generic.List<int> bodyVert
			= new System.Collections.Generic.List<int>();
		static readonly System.Collections.Generic.List<string> bodyPath
			= new System.Collections.Generic.List<string>();
		/// <summary>Yuklenmis prefab'lar (indirme bir kez yapilir).</summary>
		static readonly System.Collections.Generic.List<GameObject> bodyObj
			= new System.Collections.Generic.List<GameObject>();
		/// <summary>Prefab kok donusu. Instantiate bunu EZER, bu yuzden
		/// govde basina saklanir (hepsi ayni degil: mesh olculerek
		/// duzeltiliyor).</summary>
		static readonly System.Collections.Generic.List<Quaternion> bodyRot
			= new System.Collections.Generic.List<Quaternion>();

		/// <summary>Su an yuklenmis govdenin vertex sayisi.</summary>
		static int loadedVertexCount;

		/// <summary>Bundle'daki govde prefab'larinin vertex sayilari (artan).</summary>
		public static int[] BodyVertices {
			get {
				EnsureLoaded();
				int[] a = new int[bodyVert.Count];
				bodyVert.CopyTo(a);
				Array.Sort(a);
				return a;
			}
		}

		/// <summary>Su an yuklenmis govde (vertex sayisi).</summary>
		public static int LoadedBody { get { EnsureLoaded(); return loadedVertexCount; } }

		/// <summary>Verilen vertex sayisina sahip govde prefab'ini yukler ve
		/// aktif eder. Bulunamazsa mevcut prefab korunur (false doner).</summary>
		public static bool SelectBody(int vertices) {
			EnsureLoaded();
			if (bundle == null) return false;
			if (vertices <= 0 || vertices == loadedVertexCount) return true;
			GameObject p = LoadBody(vertices);
			if (p == null) return false;
			prefab = p;
			loadedVertexCount = vertices;
			ReadRootTransform(p);
			MeasureHeight(p);
			SmpLog.Print("npc body: " + vertices + " vertex ("
				+ ((prefab != null) ? prefab.name : "?") + ")");
			return true;
		}

		/// <summary>Verilen vertex sayisina ait prefabi cozer (yoksa null).</summary>
		static GameObject LoadBody(int vertices) {
			if (bundle == null) return null;
			int i = IndexOfBody(vertices);
			if (i >= 0 && bodyObj[i] != null) return bodyObj[i];
			string path = PathOfBody(vertices);
			if (path == null) path = "assets/mpnpc/mpnpc_" + vertices + ".prefab";
			GameObject g = bundle.LoadAsset<GameObject>(path);
			if (g != null) {
				// Klip yollari bu prefab'in kendi hiyerarsisine gore
				// eslesir; adlar hizalanmazsa egri sessizce tutmaz.
				int fixedNames = AlignBoneNames(g);
				if (fixedNames > 0) SmpLog.Print("body " + vertices
					+ ": " + fixedNames + " bone name(s) aligned to the clip rig.");
				if (i >= 0) {
					bodyObj[i] = g;
					try { bodyRot[i] = g.transform.localRotation; } catch { }
				}
			}
			if (g == null) {
				SmpLog.Warning("npc body " + vertices + " not found at '"
					+ path + "'; keeping " + loadedVertexCount + ".");
			}
			return g;
		}

		/// <summary>
		/// Ad sonundaki Unity " N" ekini atar: "HeadPivot 5" -> "HeadPivot".
		/// Ek yoksa null doner.
		/// </summary>
		static string StripDuplicateSuffix(string name) {
			if (string.IsNullOrEmpty(name)) return null;
			int i = name.Length;
			while (i > 0 && name[i - 1] >= '0' && name[i - 1] <= '9') i--;
			if (i >= name.Length || i < 2) return null;      // rakam yok
			if (name[i - 1] != ' ') return null;             // bosluk yok
			string bare = name.Substring(0, i - 1);
			return bare.Length == 0 ? null : bare;
		}

		/// <summary>
		/// Prefab'taki kemik adlarini klip yollariyla hizalar.
		///
		/// KOK NEDEN: Unity, ayni isimli ikinci bir dugumu "HeadPivot 5"
		/// diye yeniden adlandirir. Klip yollari referans rig'e (1712)
		/// gore cozulup TUM govde dosyalarina ayni .anim olarak yazildigi
		/// icin, ekli isimli govde o yollari cozemiyor. Unity eslesmeyen
		/// egriyi SESSIZCE birakir: omurga egirileri tutar, kafa
		/// egirileri tutmaz -> boyun uzar, omuzlar kabarir.
		/// (Ekranda gorulen bozuk govde tam olarak bu.)
		///
		/// Yalnizca o animasyon kurulduktan SONRA calisir: once prefab
		/// kurulur, cunku kurulum kodu kemikleri kokten itibaren
		/// olcer (Z duzeni, ayak tabani). Yeniden adlandirmak olculen
		/// hicbir seyi degistirmez; yalnizca ad degisir.
		///
		/// GUVENLI: Transform'un ADI degisir, referansi degil.
		/// SkinnedMeshRenderer kemikleri referansla baglar, yolla
		/// degil; agirliklar ve bindpose bozulmaz.
		///
		/// GUVENLIK: ayni ebeveyn altinda ek'siz karsilik ZATEN varsa
		/// dokunulmaz (gercekten iki ayri dugum demektir).
		/// </summary>
		static int AlignBoneNames(GameObject root) {
			if (root == null) return 0;
			Transform[] all = root.GetComponentsInChildren<Transform>(true);
			if (all == null) return 0;
			int renamed = 0;
			for (int i = 0; i < all.Length; i++) {
				if (all[i] == null) continue;
				string bare = StripDuplicateSuffix(all[i].name);
				if (bare == null) continue;

				Transform p = all[i].parent;
				bool collision = false;
				if (p != null) {
					for (int j = 0; j < p.childCount; j++) {
						Transform sib = p.GetChild(j);
						if (sib == null || sib == all[i]) continue;
						if (sib.name == bare) { collision = true; break; }
					}
				}
				if (collision) continue;

				all[i].name = bare;
				renamed++;
			}
			return renamed;
		}

		/// <summary>
		/// Bu vertex sayisina sahip bir govde bundle'da var mi?
		/// Gorunum sekmesindeki serit ve PlayerSkin.EffectiveBody
		/// bunu dogrular; sozluk kurulana kadar false doner.
		/// </summary>
		public static bool HasBody(int vertices) {
			if (vertices <= 0) return false;
			for (int i = 0; i < bodyVert.Count; i++)
				if (bodyVert[i] == vertices) return true;
			return false;
		}

		/// <summary>
		/// Verilen vertex sayisina EN YAKIN govdeyi dondurur (yoksa 0).
		///
		/// NEDEN: skin_mesh_map.json'daki govde her zaman pakette
		/// bulunmayabilir (cocuk/uzun varyantlar). O zaman 1712'ye
		/// dusmek yerine EN YAKIN olan secilir; boylece kiyafet
		/// yaka/cap uyumu en iyi ihtimalle korunur.
		/// </summary>
		public static int NearestBody(int vertices) {
			if (vertices <= 0) return 0;
			EnsureLoaded();
			if (bodyVert.Count == 0) return 0;
			int best = 0, bestD = int.MaxValue;
			for (int i = 0; i < bodyVert.Count; i++) {
				int d = bodyVert[i] - vertices;
				if (d < 0) d = -d;
				if (d < bestD) { bestD = d; best = bodyVert[i]; }
			}
			return best;
		}

		/// <summary>
		/// Verilen govdenin prefab'ini dondurur ve kok donusunu yazar.
		/// AKTIF prefabi DEGISTIRMEZ.
		///
		/// NEDEN AYRI YOL: RemotePlayerFactory her kuklayi dogururken
		/// o an secili olan govdeyi istiyor; Preview ise aktif olani
		/// degistiriyor. Tek bir statik "aktif prefab" ikisini birbirine
		/// karistiriyordu (menu acinca dunyadaki kuklarin govdesi
		/// degisiyordu).
		/// </summary>
		public static bool PrefabForBody(int vertices, out GameObject go,
				out Quaternion rootRotation) {
			go = null;
			rootRotation = Quaternion.identity;
			if (bundle == null) return false;
			int i = IndexOfBody(vertices);
			if (i < 0) i = IndexOfBody(NpcSkinCatalog.DEFAULT_BODY);
			if (i < 0) return false;
			if (bodyObj[i] == null) {
				bodyObj[i] = bundle.LoadAsset<GameObject>(bodyPath[i]);
				if (bodyObj[i] == null) {
					SmpLog.Warning("npc body " + bodyVert[i] + " missing at '"
						+ bodyPath[i] + "'.");
					return false;
				}
				// CANLI HATA (düzeltildi): kemik adları burada HİÇ
				// hizalanmıyordu; `AlignBoneNames` yalnızca `LoadBody`
				// içindeydi. `RemotePlayerFactory.Create` gövdeyi bu
				// yoldan çözüyor (PrefabForBody), yani karşı oyuncunun
				// seçtiği beden hiç menüden geçmemişse adlar Unity'nin
				// " N" ekli haliyle kalıyordu. Klip yolları
				// (`HeadPivot/head`) o zaman sessizce tutmuyor: boyun
				// uzaması, omuzlarda bozulma, kafanın yan durması.
				// Zaten hizalanmış bir nesnede `StripDuplicateSuffix`
				// null döndüğü için tekrar çalıştırmak no-op'tur.
				int fixedNames = AlignBoneNames(bodyObj[i]);
				if (fixedNames > 0) SmpLog.Print("body " + bodyVert[i] + ": "
					+ fixedNames + " bone name(s) aligned to the clip rig.");
				try {
					bodyRot[i] = bodyObj[i].transform.localRotation;
				} catch { }
			}
			go = bodyObj[i];
			rootRotation = bodyRot[i];
			return true;
		}

		/// <summary>Govde listesindeki indeksi dondurur (yoksa -1).</summary>
		static int IndexOfBody(int vertices) {
			for (int i = 0; i < bodyVert.Count; i++)
				if (bodyVert[i] == vertices) return i;
			return -1;
		}

		/// <summary>Verilen vertex sayisinin prefab yolunu dondurur (yoksa null).</summary>
		static string PathOfBody(int vertices) {
			for (int i = 0; i < bodyVert.Count; i++)
				if (bodyVert[i] == vertices) return bodyPath[i];
			return null;
		}

		/// <summary>Prefab'in kok transform'unu saklar (Instantiate bunlari
		/// ezer, bu yuzden yeniden gerekir).</summary>
		static void ReadRootTransform(GameObject g) {
			try {
				Transform t = g.transform;
				prefabLocalRotation = t.localRotation;
				prefabLocalPosition = t.localPosition;
				prefabLocalScale = t.localScale;
			}
			catch (System.Exception e) {
				SmpLog.Warning("npc model: could not read the prefab root "
					+ "transform: " + e.Message);
			}
		}

		/// <summary>Bundle'daki gercek yuksekligi olcer (log icin).</summary>
		static void MeasureHeight(GameObject g) {
			try {
				if (g == null) return;
				SkinnedMeshRenderer[] smrs =
					g.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
				for (int i = 0; smrs != null && i < smrs.Length; i++) {
					SkinnedMeshRenderer smr = smrs[i];
					if (smr == null || smr.sharedMesh == null) continue;
					Bounds b = smr.localBounds;
					if (b.min.y < lo) lo = b.min.y;
					if (b.max.y > hi) hi = b.max.y;
				}
				if (lo < hi && hi - lo < 100f) measuredHeight = hi - lo;
			}
			catch { }
		}

		/// <summary>Bir prefab'in govde mesh vertex sayisini bulur.
		///
		/// Prefab adi govde vertex sayisini TAHR (mpnpc_1712.prefab); bu
		/// en ucuz ve en guvenilir yol. GetDependencies Unity 5.0'da
		/// AssetBundle uzerinde YOK, bu yuzden prefab yuklenip
		/// SkinnedMeshRenderer'a bakilir.</summary>
		static int BodyVerticesOf(AssetBundle b, string path) {
			try {
				int us = path.LastIndexOf('_');
				int dot = path.LastIndexOf('.');
				if (us > 0 && dot > us) {
					string digits = path.Substring(us + 1, dot - us - 1);
					int v;
					if (int.TryParse(digits, out v)) return v;
				}
			}
			catch { }
			return 0;
		}

		/// <summary>
		/// Prefab KÖK'ÜNÜN kendi dönüşü. Instantiate(prefab, pos, rot)
		/// kökü dönüşü ÜSTÜNE YAZAR, yani bu kaybolur; RemotePlayerFactory
		/// gelen dönüşle bunu birleştirip geri koyar.
		///
		/// Neden gerekli: mesh Z ekseni boyunca uzatılmış olarak saklanıyor
		/// (ayaklar mesh z=0.007, kafa z=1.827) ama Unity'de dikey eksen Y.
		/// Bundle'i üreten Editor script'i köke ölçerek bir düzeltme
		/// döndürümü yazdı; logda "yon duzeltmesi" satırı bunu gösterir.
		/// </summary>
		static Quaternion prefabLocalRotation = Quaternion.identity;
		static Vector3 prefabLocalPosition = Vector3.zero;
		static Vector3 prefabLocalScale = Vector3.one;
		static float measuredHeight;

		static bool TryLoad() {
			try {
				compiledIn = (typeof(PlayerAssetNpc).Assembly
					.GetManifestResourceNames() != null);
				foreach (string n in typeof(PlayerAssetNpc).Assembly
					.GetManifestResourceNames()) {
					if (n == RESOURCE) { compiledIn = true; break; }
				}
			}
			catch { compiledIn = false; }
			return compiledIn;
		}

		/// <summary>Model hazır mı? İlk çağrıda yüklemeyi dener.</summary>
		public static bool Ready { get { EnsureLoaded(); return prefab != null; } }

		/// <summary>Yüklenemediyse neden. 'smp player' ve 'smp status' gösterir.</summary>
		public static string Problem { get { EnsureLoaded(); return problem; } }

		/// <summary>Model nesnesi. Spawn için kullanılır; çağırmadan önce Ready kontrol edin.</summary>
		public static GameObject Prefab { get { EnsureLoaded(); return prefab; } }

		/// <summary>Prefab kökünün kendi dönüşü (yön düzeltmesi).</summary>
		public static Quaternion PrefabLocalRotation {
			get { EnsureLoaded(); return prefabLocalRotation; }
		}

		/// <summary>Prefab kökünün yerel konumu (genelde sıfır).</summary>
		public static Vector3 PrefabLocalPosition {
			get { EnsureLoaded(); return prefabLocalPosition; }
		}

		/// <summary>Prefab kökünün yerel ölçeği (genelde 1).</summary>
		public static Vector3 PrefabLocalScale {
			get { EnsureLoaded(); return prefabLocalScale; }
		}

		/// <summary>Bundle ölçeğinde ölçülen karakter yüksekliği (m). Tanı bilgi için.</summary>
		public static float MeasuredHeight {
			get { EnsureLoaded(); return measuredHeight; }
		}

		/// <summary>Gömülü bundle'ın boyutu (bayt).</summary>
		public static int ResourceBytes { get { EnsureLoaded(); return resourceBytes; } }

		/// <summary>Bundle'dan doku yükler (skin menüsü için).
		/// <param name="atlasName">Atlas adı, .png'siz ("char_shirt01").</param>
		/// <returns>Doku, ya da yüklenemezse null.</returns>
		/// <remarks>Bundle yolları KÜÇÜK HARFLİDİR:
		/// assets/mpnctextures/&lt;ad&gt;.png. Büyük harf yazılırsa
		/// LoadAsset null döner ve skin beyaz görünür.</remarks>
		public static Texture2D LoadTexture(string atlasName) {
			if (string.IsNullOrEmpty(atlasName)) return null;
			EnsureLoaded();
			if (bundle == null) return null;
			try {
				string path = "assets/mpnctextures/" + atlasName.ToLowerInvariant() + ".png";
				Texture2D t = bundle.LoadAsset<Texture2D>(path);
				if (t == null) {
					SmpLog.Warning("skin texture not in bundle: " + path);
				}
				return t;
			}
			catch (System.Exception e) {
				SmpLog.Warning("skin texture load failed (" + atlasName + "): "
					+ e.GetType().Name + ": " + e.Message);
				return null;
			}
		}

		/// <summary>Tek satır durum.</summary>
		public static string Describe() {
			EnsureLoaded();
			if (prefab == null) return "npc asset: NOT LOADED (" + problem + ")";
			int clips = 0;
			try {
				Animation a = prefab.GetComponentInChildren<Animation>();
				if (a != null) clips = a.GetClipCount();
			}
			catch { }
			return "npc asset: " + MODEL_PATH + " (" + resourceBytes
				+ " byte bundle, " + prefab.transform.childCount + " child, "
				+ clips + " clip, bind " + measuredHeight.ToString("F2")
				+ " m) OK";
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Yüklemeyi bir kez dener.
		///
		/// "attempted" bayrağı: başarısızlık da kalıcıdır. Her karede 380 KB'lik
		/// bayt dizisini kopyalamak anlamsız; ayrıca neden değişmediği için yeni
		/// bilgi üretmez. Eksik kaynak (bundle build edilmemiş) durumunda hiç
		/// denememek daha doğru: çağıran taraf MPPlayerModel'e döner.
		/// </summary>
		static void EnsureLoaded() {
			if (attempted) return;
			attempted = true;

			if (!TryLoad()) {
				problem = "embedded resource '" + RESOURCE + "' not compiled in "
					+ "(run build_bundle.cmd, then build.cmd)";
				SmpLog.Log("npc model: " + problem + "; falling back to MPPlayerModel.");
				return;
			}

			try {
				Assembly self = typeof(PlayerAssetNpc).Assembly;
				using (Stream s = self.GetManifestResourceStream(RESOURCE)) {
					if (s == null) {
						problem = "embedded resource '" + RESOURCE + "' not found";
						SmpLog.Warning("npc model: " + problem
							+ "; falling back to MPPlayerModel.");
						return;
					}

					// mscorlib 2.0: Stream.CopyTo yok, elle kopyala.
					resourceBytes = (int)s.Length;
					byte[] data = new byte[resourceBytes];
					int offset = 0;
					while (offset < data.Length) {
						int read = s.Read(data, offset, data.Length - offset);
						if (read <= 0) break;
						offset += read;
					}
					if (offset != data.Length) {
						problem = "short read: " + offset + " of " + data.Length + " bytes";
						SmpLog.Warning("npc model: " + problem);
						return;
					}

					// Unity 5.0: Immediate senkron açılım döner.
					bundle = AssetBundle.CreateFromMemoryImmediate(data);
				}

				if (bundle == null) {
					problem = "AssetBundle.CreateFromMemoryImmediate returned null";
					SmpLog.Warning("npc model: " + problem);
					return;
				}

				// Bundle'da birden fazla GOVDE prefab'i var: oyunda "bodymesh" adi
			// altinda birden fazla mesh vardir ve NPC'ler farkli olanlari
			// kullaniyor (level2'de 186 SMR, 6 farkli mesh). Kıyafet
			// dokusu mesh'e gore tasarlandigi icin yanlis govde secmek
			// "kiyafetler yanlis giydirilmis" goruntusu uretir.
			//
			// Sozlugu kur: prefab adi -> vertex sayisi
			string[] names = bundle.GetAllAssetNames();
			for (int i = 0; i < names.Length; i++) {
				string n = names[i];
				if (n.IndexOf("mpnpc_", StringComparison.Ordinal) < 0) continue;
				if (!n.EndsWith(".prefab", StringComparison.Ordinal)) continue;
				int vc = BodyVerticesOf(bundle, n);
				if (vc <= 0) continue;
				bool dup = false;
				for (int k = 0; k < bodyVert.Count; k++)
					if (bodyVert[k] == vc) { dup = true; break; }
				if (dup) continue;
				bodyVert.Add(vc);
				bodyPath.Add(n);
				bodyObj.Add(null);
				bodyRot.Add(Quaternion.identity);
			}

			prefab = LoadBody(NpcSkinCatalog.DEFAULT_BODY);
			if (prefab == null) {
				problem = "no GameObject at '" + MODEL_PATH + "' in the bundle";
				SmpLog.Warning("npc model: " + problem);
				return;
			}
			loadedVertexCount = NpcSkinCatalog.DEFAULT_BODY;

			// KÖK dönüş/konum/ölçek bundle'da hazır; Instantiate bunları
			// EZER. Bu yüzden HER govde icin ayri ayri saklanir ve
			// govde degistiginde yeniden okunur.
			ReadRootTransform(prefab);

			if (bodyVert.Count > 1) {
				string s2 = "";
				for (int bi = 0; bi < bodyVert.Count; bi++) s2 += " " + bodyVert[bi] + "v";
				SmpLog.Print("npc bodies: " + bodyVert.Count + " (" + s2.Trim() + ")");
			}

				MeasureHeight(prefab);

				problem = "";
				SmpLog.Print("npc model loaded: " + MODEL_PATH + " ("
					+ resourceBytes + " byte bundle, root rotation "
					+ prefabLocalRotation.ToString("F3") + ").");
			}
			catch (Exception e) {
				bundle = null;
				prefab = null;
				problem = e.GetType().Name + ": " + e.Message;
				SmpLog.Warning("npc model load failed: " + problem
					+ "; falling back to MPPlayerModel.");
			}
		}
	}
}