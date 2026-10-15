using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// SEÇİLİ GÖRÜNÜM (SKIN).
	///
	/// Oyunun tüm NPC'leri AYNI mesh ve iskeleti paylaşıyor
	/// (bodymesh, 20 kemik, 1709 vertex). Görünüm yalnızca üç dokuyla
	/// belirleniyor: gömlek, pantolon, yüz. Bu yüzden bir "skin" üç doku
	/// adından ibaret — 70 farklı NPC görünümü tek model üzerinde.
	///
	/// Katalog
	/// -------
	/// <see cref="NpcSkinCatalog"/> oyunun verisinden üretilir
	/// (tools\skin_roster.py → roster.json → gen_skin_catalog.py). Elle
	/// yazılmış bir liste oyun güncellemesinde sessizce yanlış kalırdı.
	///
	/// Nereye uygulanır
	/// ----------------
	/// Uzaktaki oyuncunun gövdesine. <see cref="RemotePlayerFactory"/> her
	/// doğurmada <see cref="Apply"/> çağırır, yani menüde seçim bir sonraki
	/// spawn'dan itibaren geçerlidir; açık gövde varsa hemen de uygulanır.
	///
	/// KAPSAM: seçim şu an YERELDİR — karşı taraf, seni seçtiğin
	/// görünümle görür. Ağ üzerinden göndermek protokol değişikliği ister.
	///
	/// mscorlib 2.0: LINQ yok, Dictionary&lt;string,Texture2D&gt; elle kullanılıyor.
	/// </summary>
	public static class PlayerSkin {

		/// <summary>PlayerPrefs anahtarı. Sürüm eklenirse eski kayıt geçersiz.</summary>
		public const string PREFS_KEY = "MSCSteamMP.skin.v1";

		/// <summary>Elle secilen govde icin ayri kayit anahtari.</summary>
		public const string BODY_PREFS_KEY = "MSCSteamMP.body.v1";

		static int selected = -1;
		static bool loaded;

		/// <summary>Doğrulanmamış ham indeks (kayıttan gelirken).</summary>
		static int rawIndex = -1;

		/// <summary>
		/// Kullanicinin gorunum sekmesinden elle sectigi govde vertex
		/// sayisi. 0 ise skin'in kendi govdesi kullanilir.
		///
		/// GEREKCE: skin_mesh_map.json her gorunumun dogru govdesini
		/// verir, ama kullanici istedigi govdeyi denemek isteyebilir
		/// ("kadin govdesi + unis kirmasi" gibi). Secim kalicidir.
		/// </summary>
		public static int bodyOverride = 0;

		/// <summary>Son bilinen dogru govde (Select/SetBodyOverride yazar).</summary>
		public static int selectedBody = NpcSkinCatalog.DEFAULT_BODY;

		// atlas adi -> Texture2D. 47 doku x 512x512 bellekte durmasın diye
		// yalnızca GERÇEKTEN kullanılanlar tutulur.
		static readonly Dictionary<string, Texture2D> cache
			= new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Toplam skin sayısı.</summary>
		public static int Count { get { return NpcSkinCatalog.Count; } }

		/// <summary>Seçili skin'in indeksi (0..Count-1). Katalog yoksa -1.</summary>
		public static int Selected {
			get {
				EnsureLoaded();
				return selected;
			}
		}

		/// <summary>Seçili skin. Geçersizse null.</summary>
		public static NpcSkin? Current {
			get {
				EnsureLoaded();
				int i = selected;
				if (i < 0 || i >= NpcSkinCatalog.All.Length) return null;
				return NpcSkinCatalog.All[i];
			}
		}

		/// <summary>Seçili skin'in adı (log/menü için). Yoksa "—".</summary>
		public static string CurrentName {
			get {
				NpcSkin? s = Current;
				return s.HasValue ? s.Value.name : "—";
			}
		}

		/// <summary>
		/// Skin seç. Geçersiz indeks yok sayılır.
		/// Açık bir gövde varsa ona hemen uygulanır.
		/// </summary>
		public static void Select(int index) {
			EnsureLoaded();
			if (index < 0 || index >= NpcSkinCatalog.All.Length) return;
			selected = index;
			Save();
			SmpLog.Print("skin selected: " + NpcSkinCatalog.All[index].ToString());

			// Govde degisimi: skin'in kendi govdesi bundle prefab'inin
			// kaynagidir. Otomatik modda (bodyOverride yok) secim degisince
			// aktif prefab da degismeli; aksi halde 3B onizleme yeni
			// gorunumu ESKI govde uzerinde gosterir ve kiyafetler yaka/
			// cap uyusmazligi cikar.
			if (bodyOverride <= 0) {
				int want = NpcSkinCatalog.All[index].bodyVertices > 0
					? NpcSkinCatalog.All[index].bodyVertices
					: NpcSkinCatalog.DEFAULT_BODY;
				if (PlayerAssetNpc.SelectBody(want)) selectedBody = want;
			}

			// Menüde seçim yaparken ekranda bir kukla duruyorsa anında
			// değişsin; kullanıcı "seçtim ama bir şey olmadı" demesin.
			//
			// DÜZELTME: bu döngü UZAKTAKI oyuncunun kuklasını da
			// yeniliyor ve yerel skin'i ona uyguluyordu. Canlı hata:
			// "menüden seçtiğim player görünümü karşıdaki playerde de
			// görünüyor". Artık kuklanın görünümü KARŞININ seçiminden
			// gelir; bizim seçimimiz yalnızca bizi etkiler ve peer'a
			// PlayerInfo ile bildirilir.
			try {
				RemotePlayerSync.ResendAppearance();
			}
			catch (Exception e) {
				if (Telemetry.Verbose)
					SmpLog.Debug("skin", "peer", "appearance not sent: " + e.Message);
			}
		}

		/// <summary>Varsayılan görünüme dön (sıfırıncı skin).</summary>
		public static void Reset() { Select(0); }

		/// <summary>
		/// Kullanilacak govdenin vertex sayisi.
		///
		/// Oncelik: elle secim (bodyOverride) -> skin'in kendi govdesi
		/// -> paket varsayilani. Bulunamayan bir deger bundle'daki YAKIN
		/// govdeye duser; hicbiri yoksa DEFAULT_BODY.
		/// </summary>
		public static int EffectiveBody {
			get {
				EnsureLoaded();
				if (bodyOverride > 0 && PlayerAssetNpc.HasBody(bodyOverride))
					return bodyOverride;
				NpcSkin? sk = Current;
				if (sk.HasValue && sk.Value.bodyVertices > 0) {
					if (PlayerAssetNpc.HasBody(sk.Value.bodyVertices))
						return sk.Value.bodyVertices;
					int near = PlayerAssetNpc.NearestBody(sk.Value.bodyVertices);
					if (near > 0) return near;
				}
				return NpcSkinCatalog.DEFAULT_BODY;
			}
		}

		/// <summary>
		/// KARSININ gorunumu icin govde: onun bodyOverride'i varsa o,
		/// yoksa skin'inin kendi govdesi.
		///
		/// NEDEN EffectiveBody DEGIL: EffectiveBody YEREL bodyOverride'i
		/// okur. Uzaktaki kukla yerel secimle giydirildigi icin menude
		/// gorunum degistirince karsi tarafin kuklasi da degisiyordu.
		/// Burada girdi tamamen KARSINININ: PeerInfo ile gelen indeks
		/// ve govde secimi.
		///
		/// Karsi tarafin bodyOverride'i bizim bundle'da yoksa (farkli
		/// build) NearestBody'ye duser; hicbiri yoksa DEFAULT_BODY.
		/// </summary>
		public static int BodyForRemote(int skinIndex, int remoteOverride) {
			EnsureLoaded();
			if (remoteOverride > 0 && PlayerAssetNpc.HasBody(remoteOverride))
				return remoteOverride;
			if (NpcSkinCatalog.All != null && NpcSkinCatalog.All.Length > 0) {
				if (skinIndex < 0 || skinIndex >= NpcSkinCatalog.All.Length) skinIndex = 0;
				int want = NpcSkinCatalog.All[skinIndex].bodyVertices;
				if (want > 0) {
					if (PlayerAssetNpc.HasBody(want)) return want;
					int near = PlayerAssetNpc.NearestBody(want);
					if (near > 0) return near;
				}
			}
			return NpcSkinCatalog.DEFAULT_BODY;
		}

		/// <summary>
		/// Govdeyi elle sec (gorunum sekmesindeki seritten). 0 = otomatik.
		/// Secim hem 3B onizlemeye hem dunyadaki kuklalara uygulanir.
		/// </summary>
		public static bool SetBodyOverride(int vertices) {
			EnsureLoaded();
			if (vertices > 0 && !PlayerAssetNpc.HasBody(vertices)) return false;
			bodyOverride = vertices;
			try {
				PlayerPrefs.SetInt(BODY_PREFS_KEY, vertices);
				PlayerPrefs.Save();
			}
			catch (Exception) { }

			int want = EffectiveBody;
			if (PlayerAssetNpc.SelectBody(want)) selectedBody = want;
			SmpLog.Print("body: "
				+ (vertices > 0 ? vertices + " vertex (elle)" : "otomatik")
				+ "  -> aktif " + want);

			// DÜZELTME: burada RespawnLive() çağrılıyordu, yani YEREL
			// gövde değişince UZAKTAKI oyuncunun kuklası yok edilip
			// yeniden doğuruluyordu. Ama kukla artık bizim gövdemizi
			// taşımıyor; karsininkini taşıyor. Yerel değişiklik yalnızca
			// bizi etkiler, peer'a bildirilir. Peer'in gövdesi değişirse
			// onun PlayerInfo'sı gelir ve o tarafta kukla yeniden doğar.
			try {
				RemotePlayerSync.ResendAppearance();
			}
			catch (Exception e) {
				if (Telemetry.Verbose)
					SmpLog.Debug("skin", "peer", "body not sent: " + e.Message);
			}
			return true;
		}

		/// <summary>Bir skin'i adıyla seç. Bulunamazsa false.</summary>
		public static bool SelectByName(string name) {
			EnsureLoaded();
			if (string.IsNullOrEmpty(name)) return false;
			for (int i = 0; i < NpcSkinCatalog.All.Length; i++) {
				if (string.Equals(NpcSkinCatalog.All[i].name, name,
						StringComparison.OrdinalIgnoreCase)) {
					Select(i);
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Atlas dokusunu getirir (bundle'dan, önbellekli).
		/// Doğuşta materyaller zaten oluştuğu için bu yol yalnızca MENÜ
		/// önizlemesinde ve skin değişiminde kullanılır.
		/// </summary>
		public static Texture2D Texture(string atlasName) {
			if (string.IsNullOrEmpty(atlasName)) return null;
			Texture2D t;
			if (cache.TryGetValue(atlasName, out t) && t != null) return t;
			t = PlayerAssetNpc.LoadTexture(atlasName);
			if (t == null) return null;
			cache[atlasName] = t;
			return t;
		}

		/// <summary>Önbelleği boşalt (bundle düşerse bellek geri döner).</summary>
		public static void ClearCache() { cache.Clear(); }

		/// <summary>
		/// Seçili görünümü bir gövdeye uygular.
		///
		/// Mesh'in 3 submesh'i sırasıyla gömlek / pantolon / yuz
		/// (oyundaki SkinnedMeshRenderer.m_Materials sırası ile birebir aynı,
		/// build sırasında doğrulandı). Her materyal KOPYALANIR: bundle'daki
		/// asset'i doğrudan değiştirmek bütün kuklaları aynı kıyafete
		/// bağlar.
		/// </summary>
		public static void Apply(GameObject body) {
			ApplyIndex(body, Selected);
		}

		/// <summary>
		/// VERILEN indeksli skin'i bir govdeye uygular.
		///
		/// NEDEN AYRI FONKSIYON: <see cref="Apply"/> yerel secimi
		/// uygular. Ama uzaktaki oyuncunun kuklasina KARSINININ
		/// gorunumu giydirilir; yerel secimle giydirilirsa menude
		/// gorunum degistirdigimizde karsi tarafin kuklasi da
		/// degisiyordu (canli hata: "menuden sectigim player gorunumu
		/// karsidaki playerde de gorunuyor").
		///
		/// Endeks karsi tarafin katalogunda olmayabilir (farkli build,
		/// farkli doku sayisi); o durumda 0'a dusurulur.
		/// </summary>
		public static void ApplyIndex(GameObject body, int skinIndex) {
			if (body == null) return;
			EnsureLoaded();
			if (NpcSkinCatalog.All == null || NpcSkinCatalog.All.Length == 0) return;
			if (skinIndex < 0 || skinIndex >= NpcSkinCatalog.All.Length) skinIndex = 0;
			NpcSkin? s = NpcSkinCatalog.All[skinIndex];
			if (!s.HasValue) return;      // NPC modeli yoksa dokunma

			try {
				SkinnedMeshRenderer[] smrs
					= body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				if (smrs == null) return;

				for (int i = 0; i < smrs.Length; i++) {
					SkinnedMeshRenderer smr = smrs[i];
					if (smr == null) continue;
					Mesh m = smr.sharedMesh;
					if (m == null) continue;
					// Sadece karakter mesh'i (3 submesh). El, pelvin vb.
					// parcalarin farkli submesh sayisi olur.
					if (m.subMeshCount < 3) continue;

					Material[] src = smr.sharedMaterials;
					if (src == null || src.Length < 3) continue;

					Material[] outMats = new Material[src.Length];
					Texture2D tShirt = Texture(s.Value.shirt);
					Texture2D tPants = Texture(s.Value.pants);
					Texture2D tFace  = Texture(s.Value.face);

					for (int k = 0; k < src.Length; k++) {
						Material srcMat = src[k];
						if (srcMat == null) { outMats[k] = null; continue; }
						Material dst = new Material(srcMat);
					// UV ATLASI KONTROLU (runtime olcumu).
						//
						// char_*.png dokulari 0..1 araliginda tek atlas. Mesh in
						// UV si bu araligin DISINDAYSA doku tile edilerek tekrar
						// eder ve baska atlas takmak kiyafeti bozar. Olcum:
						//   1697 Granny  X[-3.176..4.120]  -> tile
						//   1789 Suski   X[-0.360..0.989]  -> kismi tasma
						//   960/1709/1712/1714  0.006..0.989  -> uygun
						//
						// Uyumsuz govdeye doku TAKILMAZ; kendi kiyafeti korunur.
						bool uvOk = AtlasCompatible(m);
						// submesh sirasi gomlek / pantolon / yuz
						Texture2D t;
						if (k == 0) t = tShirt;
						else if (k == 1) t = tPants;
						else t = tFace;
						if (!uvOk) t = null;   // atlas uyumsuz
						if (t != null && dst.HasProperty("_MainTex")) {
							dst.mainTexture = t;
						}
						outMats[k] = dst;
					}
					smr.materials = outMats;
				}
			}
			catch (Exception e) {
				SmpLog.Warning("skin apply failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>Bu govde doku atlasiyla uyumlu mu?
		/// Govde 0 veya prefab yuklenemezse 'bilinmiyor' sayilir
		/// ve uygun kabul edilir (menuyu bloklamamak icin).</summary>
		static bool BodyAtlasOk(int bodyVertices) {
			try {
				if (bodyVertices <= 0) return true;
				GameObject p = PlayerAssetNpc.Prefab;
				if (p == null) return true;
				SkinnedMeshRenderer[] smrs =
					p.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				if (smrs == null) return true;
				for (int i = 0; i < smrs.Length; i++) {
					if (smrs[i] == null) continue;
					Mesh m = smrs[i].sharedMesh;
					if (m == null || m.subMeshCount < 3) continue;
					return AtlasCompatible(m);
				}
				return true;
			}
			catch { return true; }
		}
		/// <summary>
		/// Bu mesh, 0..1 araligindaki tek bir doku atlasina mi uygun?
		///
		/// char_*.png dosyalari 0..1 araliginda tek atlas. Mesh in UV si
		/// bu araligin disindaysa doku tile edilerek tekrar eder ve baska
		/// atlas takmak kiyafeti bozar (yaka/cap kayar).
		///
		/// Sonuc govde basina ONCEDEN olculdu (diag_uv5.py):
		/// 1697 ve 1789 uyumsuz, 960/1709/1712/1714 uygun. Yine de
		/// her Apply cagrisinda mesh ten olculur: referans mesh
		/// degisse bir daha yanlmasin.
		/// </summary>
		const float UV_TOL = 0.02f;
		const int UV_MAX_SAMPLE = 256;

		static bool AtlasCompatible(Mesh m) {
			try {
				if (m == null) return false;
				Vector2[] uv = m.uv;
				if (uv == null || uv.Length == 0) return false;
				// Orneklemeli: tum vertex i taramak pahali, 256 yeter.
				int step = uv.Length / UV_MAX_SAMPLE;
				if (step < 1) step = 1;
				for (int i = 0; i < uv.Length; i += step) {
					Vector2 t = uv[i];
					if (t.x < -UV_TOL || t.x > 1f + UV_TOL) return false;
					if (t.y < -UV_TOL || t.y > 1f + UV_TOL) return false;
				}
				return true;
			}
			catch { return false; }
		}
		/// <summary>Tek satır durum ('smp skin').</summary>
		public static string Describe() {
			EnsureLoaded();
			// NOT: NpcSkinCatalog.Count bir const; "== 0" denetimi derleyici
			// tarafindan katlanir ve sonraki satirlar "unreachable" uyarisi
			// uretir. Katalog uretim ile gelir, bos olmaz.
			NpcSkin? s = Current;
			if (!s.HasValue) return "skin: none selected";
			string body = s.Value.bodyVertices > 0
				? s.Value.bodyVertices.ToString() : "?";
			string ovr = bodyOverride > 0
				? "  elle:" + bodyOverride : "";
			return "skin: " + s.Value.name + " [" + (Selected + 1) + "/" + Count + "]"
				+ "  " + s.Value.shirt + " / " + s.Value.pants + " / " + s.Value.face
				+ "  govde " + body + ovr + "  aktif " + EffectiveBody
				+ (s.Value.bodyVertices != NpcSkinCatalog.DEFAULT_BODY
					? " (bundle " + NpcSkinCatalog.DEFAULT_BODY + ")"
					: "")
				+ (BodyAtlasOk(s.Value.bodyVertices)
					? "" : "  [UV atlas disi: kiyafet sabit]")
				+ "  (" + cache.Count + " doku onbellekte)";
		}

		/// <summary>
		/// Seçili skin'in govde mesh'i bundle'dakinden farkli mi?
		///
		/// Oyun "bodymesh" adi altinda birden fazla mesh kullanir
		/// (level2'de 186 SMR, 6 farkli mesh). Kıyafet dokusu mesh'e
		/// gore tasarlanmistir: kadin govdesinin UV'si erkek omuzuna
		/// oturmaz, yaka/cap uyumsuzlugu ve "kıyafetler yanlış
		/// giydirilmiş" goruntusu dogar.
		///
		/// Bundle'da su an TEK govde var (en yaygin olan). Skin'in
		/// govdesi baska biriyse bu sesli uyari verir; dogru cozum
		/// o mesh'i de bundle'a almak.
		/// </summary>
		public static bool BodyMatchesBundle {
			get {
				EnsureLoaded();
				NpcSkin? s = Current;
				if (!s.HasValue) return true;
				// Karsilastirma sabit 1712 ile degil, KULLANILACAK govde
				// ile yapilir: elle secim varsa o, yoksa skin'in kendi
				// govdesi (paket yoksa en yakini).
				if (PlayerAssetNpc.HasBody(s.Value.bodyVertices)) return true;
				return EffectiveBody == PlayerAssetNpc.NearestBody(s.Value.bodyVertices);
			}
		}

		// ------------------------------------------------------------------

		static void EnsureLoaded() {
			if (loaded) return;
			loaded = true;

			int fallback = 0;
			for (int i = 1; i < NpcSkinCatalog.All.Length; i++) {
				if (NpcSkinCatalog.All[i].shirt == "char_shirt04") { fallback = i; break; }
			}
			selected = fallback;

			try {
				rawIndex = PlayerPrefs.GetInt(PREFS_KEY, -1);
			}
			catch (Exception) {
				// Bazı ortamlarda PlayerPrefs erişimi yok (kayıt yok). Sessizce
				// varsayılana kal; skin seçimi çalışmaya devam eder.
				rawIndex = -1;
			}
			if (rawIndex >= 0 && rawIndex < NpcSkinCatalog.All.Length) selected = rawIndex;

			try { bodyOverride = PlayerPrefs.GetInt(BODY_PREFS_KEY, 0); }
			catch (Exception) { bodyOverride = 0; }

			SmpLog.Print("skin catalog: " + NpcSkinCatalog.Count + " gorunum, "
				+ NpcSkinCatalog.Textures.Length + " doku; secili: "
				+ CurrentName + " (kayit: " + rawIndex + ")");
		}

		/// <summary>
		/// Dunyadaki kuklalari temizler; bir sonraki konum paketinde
		/// secili govdeyle yeniden dogarlar.
		///
		/// NEDEN YENIDEN DOGURMA: mesh degistiginde mevcut kuklanin
		/// SkinnedMeshRenderer'i eski mesh'i tasir ve bones/bindposes
		/// yeni mesh'e ait oldugu icin yerine gecirmek temiz bir yol
		/// degil. Dogurma tek ve dogru cozumdur.
		/// </summary>
		static void RespawnLive() {
			try {
				RemotePlayerBehaviour[] all
					= UnityEngine.Object.FindObjectsOfType(typeof(RemotePlayerBehaviour))
						as RemotePlayerBehaviour[];
				if (all == null || all.Length == 0) return;
				// Isim korunur: yerel oyuncunun govdesi degisti, karsidakilerin
				// adi degismedi. Clear(false) butun kuklalari "peer" yapardi
				// ve PlayerInfo oturum basina bir kez gittigi icin oturumun
				// geri kalaninda duzelmezdi.
				RemotePlayerSync.Clear(true);
				SmpLog.Print("body changed; remote players respawn on the next "
					+ "position packet.");
			}
			catch (Exception e) {
				SmpLog.Warning("body respawn failed: "
					+ e.GetType().Name + ": " + e.Message);
			}
		}

		static void Save() {
			try { PlayerPrefs.SetInt(PREFS_KEY, selected); PlayerPrefs.Save(); }
			catch (Exception) { /* kalici depolama yoksa oturum ici gecerli */ }
		}
	}
}