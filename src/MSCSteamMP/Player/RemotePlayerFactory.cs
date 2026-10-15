using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// UZAK OYUNCUNUN GÖVDESİNİ ÜRETİR.
	///
	/// Önceki yöntem: oyuncu modeli yok, o yüzden bir NPC klonlanıyordu
	/// (Kiosk_Teimo). Klonun PlayMaker'ı, NavMeshAgent'ı, Rigidbody'si
	/// Destroy ediliyordu. Artık gerek yok: model bundle'dan geliyor ve içinde
	/// temiz bir iskelet var. Bu yüzden burada TEK bir şey yapılıyor: prefab'ı
	/// Instantiate etmek ve render/animasyon ayarlarını sabitlemek.
	///
	/// Ayarların gerekçesi
	/// -------------------
	///   updateWhenOffscreen  Render mesh'in kameraya göre küllenmesini kapatır.
	///                       Aksi halde kukla ekrana girerken bir kare invisible olur.
	///   layer                Oyunun kendi katmanı. Işık/çarpışma davranışı bu katmana
	///                       bağlı; 0 (Default) güvenli seçim.
	///   collider             Modelde yok; yine de bir Instantiate'a sızmışsa
	///                       kukla fiziksel engel olmasın.
	/// </summary>
	public static class RemotePlayerFactory {

		/// <summary>Kuklanın GameObject adı. Sahne dökümünde aranabilir olsun diye sabit.</summary>
		public const string ROOT_NAME = "RemotePlayerBody";

		/// <summary>
		/// Ayak telafisi (m) — YALNIZCA dünya ölçümü de başarısız olursa
		/// kullanılan son çare.
		///
		/// Bundle ölçümü (UnityPy): kök "MPPlayerModel" y=+0.867, yani
		/// orijin KALÇADA — ama canlı ölçüm bunu yanlış çıkardı: gerçek
		/// mesh tabanı kökün 0.007 m ÜSTÜNDE, yani kök ayak hizasında.
		/// Fark PrefabLocalRotation ile dönen kökte ölçüldüğü için
		/// görünüyordu; bu yüzden değer DÜNYA ölçümüyle (bak
		/// MeasureMeshWorldY) belirlenir, bu sabite güvenilmez.
		/// </summary>
		public const float FOOT_OFFSET = 0.867f;

		/// <summary>
		/// Taban ile zemin arasında bırakılan pay (m). Negatif = bastır.
		///
		/// ÖNCEKİ iki tur bunu suçladı: +0.02 sonra -0.01. Yanlıştı, çünkü
		/// telafi değil KAYNAK KONUM yanlıştı. Yayınlanan konum
		/// CharacterController'ın transform'ıydı, yani kapsülün ORTASI
		/// (~0.9 m yukarısı), ayağın kendisi değil. PlayerMotion artık
		/// kapsülün alt ucunu yayınlıyor (bkz. PlayerPhysicsHook.FeetPosition),
		/// yani ölçüm artık doğru tabana oturuyor.
		///
		/// ------------------------------------------------------------------
		/// ÖLÇÜM SONUCU (ışınla, GroundGap) — varsayım değil:
		///
		///   footOffset = -0.007   ← dünya ölçümüyle (güvenilir yol)
		///   lift       = -0.027   ← footOffset + GROUND_EPSILON
		///   mesh tabanı = -0.564
		///   zemin       = -0.564
		///   SONUÇ: ZEMIN ustunde 0.000 m   ← ayaklar birebir zeminde
		///
		///   Doğrulama iki bağımsız noktada aynı sayıyı verdi:
		///     doğurma karesi 0.000, 2 saniye sonraki durağan ölçüm 0.000.
		///
		/// AŞAĞIDAKİ HİKÂYE — dört tur yanlış yerde düzeltme yapıldı:
		///   1. "havada duruyor" → GROUND_EPSILON +0.02 → model 17 cm
		///      DAHA YUKARI çıktı, kullanıcı "daha çok yukarı" dedi
		///   2. -0.02 → doğru yön, ama ölçüm 0.027'de kilitli kaldı
		///   3. ölçümün YERLEŞTİRMEDEN ÖNCE çalıştığı anlaşıldı
		///      (gap = wLow - zeminY; iki terim de aynı hatalı durumdan
		///       ölçülüyor → fark lift'ten BAĞIMSIZ)
		///   4. kök neden: `lowest` KÖK-UZAYINDA ölçülüyordu, kök
		///      DÖNDÜRÜLMÜŞ (PrefabLocalRotation) olduğu için dünya-dikey
		///      mesafe kök-uzayı Y'sine yansımıyor. Sapma tam |lowest|
		///      = 0.676 m idi; model 65.6 cm fazla yukarıdaydı.
		///
		/// DERS (kalıcı): dünya konumu soruları DÜNYA uzayında ölçülür.
		/// Kök dönmesi/ölçeği varsa kök-uzayı ölçümü sessizce saçma
		/// bir sayı verir — ve verilen sayı makul görünür, bu yüzden
		/// iki ayrı ölçümün ÇELİŞMESİ gerçek hatayı ele verdi.
		///
		/// DEĞER: -0.02. Mesh'in tabanı kökün 0.007 m ÜSTÜNDE olduğu
		/// için bu, 0.007'lik kök farkını da götürür; sonuç tam oturma
		/// (0.000). Daha önceki -0.15 / -0.05 / +0.02 denemelerinin
		/// hiçbiri doğru tabanı kullanmıyordu, bu yüzden hiçbiri tutmadı.
		/// </summary>
		public const float GROUND_EPSILON = -0.02f;

		/// <summary>
		/// Modelin köküne göre en alçak ve en yüksek noktası (m).
		/// Renderer sınırlarının dünya AABB'si köke geri çevrilir, böylece
		/// dönüşten bağımsız ölçüm yapılır.
		///
		/// İKİ DEĞER NEDEN: kullanıcı hem "player biraz fazla büyük" hem de
		/// "birkaç cm havada" diyordu. Kökün nereden geçtiğini bilmiyoruz
		/// (eski yorum "orijin KALÇADA" diyordu ama bu ölçümle doğrulanamaz).
		/// O yüzden varsayıma güvenmek yerine modelin GERÇEK boyunu ölçüp
		/// insan boyuna ölçekliyoruz: scale = HEDEF / (enYuksek - enAlçak).
		/// Hem boy hem zemin oturumu tek ölçümden doğru çıkar.
		/// </summary>
		/// <summary>
		/// DÜNYA uzayında en alçak/en yüksek nokta (dönüşüm YOK).
		///
		/// Ölçek DOĞRULAMA geçişi için: `InverseTransformPoint` kök-uzayına
		/// ölçer ve iç içe ölçekli düğümlerde ilişkiyi bozar; burada renderer
		/// bounds'ı doğrudan dünya koordinatından okunur.
		/// </summary>
		/// <summary>
		/// Model GERÇEKTE zeminden ne kadar yukarıda? (ışınla ölçer)
		///
		/// Döner: mesh'in dünya en alçak noktası ile zemin arası (m).
		/// 0 = zeminde, pozitif = süzülüyor, negatif = gömülü.
		///
		/// NEDEN AYRI FONKSİYON: `footOffset = -lowest * appliedScale`
		/// KÖK-UZAY ölçümü ve mesh'in görünmeyen kısımlarını da
		/// kapsıyor. Kullanıcı "havada duruyor" dedi ama hesapla yazınca
		/// model daha çok yükseldi (0.519 -> 0.689). Bu ölçüm ışınla
		/// GERÇEK zemini bulup farkı verir; tek sabit değişiklikle
		/// düzeltilebilir.
		/// </summary>
		static float GroundGap(GameObject body, Vector3 feet, out float groundY) {
			groundY = float.NaN;
			try {
				float wLow;
				float wHigh;
				MeasureMeshWorldY(body, out wLow, out wHigh);
				if (float.IsNaN(wLow)) return float.NaN;

				// CANLI HATA: ilk ölçüm 0.5 m yukarıdan ve 10 m menzille
				// denendi, NaN döndü ("zemin bulunamadi") — model y=40'ta
				// doğuyordu, zemin 10 m'nin altındaydı. Yüksekten ve geniş
				// menzille denemek zorunlu.
				Vector3 hi = new Vector3(feet.x, wLow + 5f, feet.z);
				Vector3 lo = new Vector3(feet.x, wLow + 0.5f, feet.z);
				RaycastHit hit;
				if (Physics.Raycast(hi, Vector3.down, out hit, 60f)) {
					groundY = hit.point.y;
					return wLow - hit.point.y;
				}
				if (Physics.Raycast(lo, Vector3.down, out hit, 60f)) {
					groundY = hit.point.y;
					return wLow - hit.point.y;
				}
				return float.NaN;
			}
			catch { return float.NaN; }
		}

		/// <summary>
		/// DURAĞAN ikinci ölçüm: kukla doğalıktan 2 saniye sonra gerçekte
		/// ne kadar süzülüyor?
		///
		/// NEDEN DOĞRMA ANININKI TEK BAŞINA YETMEZ: doğurma karesinde
		/// SkinnedMeshRenderer'ın bounds'ı yeni eklenmiş bir mesh için hâlâ
		/// BAYAT olabiliyor. Kullanıcı ekranda gördüğü boşluğu soruyor;
		/// o, doğurma karesi değil, 2 saniye sonraki durağan hâldir.
		/// İki ölçüm farklıysa sorun bounds bayatlığıdır.
		/// </summary>
		public static float MeasureSteadyGap(GameObject body, Vector3 feet) {
			float groundY;
			return GroundGap(body, feet, out groundY);
		}

		static void MeasureMeshWorldY(GameObject body, out float lowest, out float highest) {
			lowest = float.NaN; highest = float.NaN;
			if (body == null) return;
			Transform root = null;
			try { root = body.transform; } catch { return; }
			if (root == null) return;

			float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
			bool found = false;

			try {
				SkinnedMeshRenderer[] smrs = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				for (int i = 0; smrs != null && i < smrs.Length; i++) {
					SkinnedMeshRenderer smr = smrs[i];
					if (smr == null || smr.sharedMesh == null) continue;
					Bounds b = smr.sharedMesh.bounds;
					for (int c = 0; c < 8; c++) {
						Vector3 corner = new Vector3(
							(c & 1) != 0 ? b.max.x : b.min.x,
							(c & 2) != 0 ? b.max.y : b.min.y,
							(c & 4) != 0 ? b.max.z : b.min.z);
						Vector3 wp;
						try { wp = smr.transform.TransformPoint(corner); }
						catch { continue; }
						if (wp.y < lo) lo = wp.y;
						if (wp.y > hi) hi = wp.y;
						found = true;
					}
				}
			}
			catch { }

			try {
				MeshFilter[] mfs = body.GetComponentsInChildren<MeshFilter>(true);
				for (int i = 0; mfs != null && i < mfs.Length; i++) {
					MeshFilter mf = mfs[i];
					if (mf == null || mf.sharedMesh == null) continue;
					Bounds b = mf.sharedMesh.bounds;
					for (int c = 0; c < 8; c++) {
						Vector3 corner = new Vector3(
							(c & 1) != 0 ? b.max.x : b.min.x,
							(c & 2) != 0 ? b.max.y : b.min.y,
							(c & 4) != 0 ? b.max.z : b.min.z);
						Vector3 wp;
						try { wp = mf.transform.TransformPoint(corner); }
						catch { continue; }
						if (wp.y < lo) lo = wp.y;
						if (wp.y > hi) hi = wp.y;
						found = true;
					}
				}
			}
			catch { }

			if (!found || lo == float.PositiveInfinity) return;
			lowest = lo; highest = hi;
		}

		static void MeasureVerticalExtent(GameObject body, out float lowest, out float highest) {
			lowest = float.NaN; highest = float.NaN;
			if (body == null) return;
			Transform root = null;
			try { root = body.transform; } catch { return; }
			if (root == null) return;

			float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
			bool found = false;

			try {
				SkinnedMeshRenderer[] smrs = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				for (int i = 0; smrs != null && i < smrs.Length; i++) {
					SkinnedMeshRenderer smr = smrs[i];
					if (smr == null || smr.sharedMesh == null) continue;
					Bounds b = smr.sharedMesh.bounds;
					for (int c = 0; c < 8; c++) {
						Vector3 corner = new Vector3(
							(c & 1) != 0 ? b.max.x : b.min.x,
							(c & 2) != 0 ? b.max.y : b.min.y,
							(c & 4) != 0 ? b.max.z : b.min.z);
						Vector3 wp;
						try { wp = smr.transform.TransformPoint(corner); }
						catch { continue; }
						Vector3 lp;
						try { lp = root.InverseTransformPoint(wp); } catch { continue; }
						if (lp.y < lo) lo = lp.y;
						if (lp.y > hi) hi = lp.y;
						found = true;
					}
				}
			}
			catch { }

			try {
				MeshFilter[] mfs = body.GetComponentsInChildren<MeshFilter>(true);
				for (int i = 0; mfs != null && i < mfs.Length; i++) {
					MeshFilter mf = mfs[i];
					if (mf == null || mf.sharedMesh == null) continue;
					Bounds b = mf.sharedMesh.bounds;
					for (int c = 0; c < 8; c++) {
						Vector3 corner = new Vector3(
							(c & 1) != 0 ? b.max.x : b.min.x,
							(c & 2) != 0 ? b.max.y : b.min.y,
							(c & 4) != 0 ? b.max.z : b.min.z);
						Vector3 wp;
						try { wp = mf.transform.TransformPoint(corner); }
						catch { continue; }
						Vector3 lp;
						try { lp = root.InverseTransformPoint(wp); } catch { continue; }
						if (lp.y < lo) lo = lp.y;
						if (lp.y > hi) hi = lp.y;
						found = true;
					}
				}
			}
			catch { }

			if (!found || lo == float.PositiveInfinity) return;
			lowest = lo; highest = hi;
		}

		/// <summary>
		/// Uzaktaki oyuncunun hedef boyu (m).
		///
		/// MSC karakteri ~1.80 m. Bundle ölçümü 1.006 m (kök→ayak) ve
		/// kullanıcının "biraz fazla büyük" şikâyetiyle uyumlu: model
		/// gerçekte ~1.9 m civarında ve ölçekleniyor.
		/// </summary>
		public const float TARGET_HEIGHT = 1.80f;

		/// <summary>
		/// Modeli dünyaya koyar.
		/// </summary>
		/// <param name="position">Başlangıç konumu (son paketten gelen).</param>
		/// <param name="rotation">Başlangıç dönüşü.</param>
		/// <returns>Kuklanın üzerine takılacak davranış, ya da null.</returns>
		public static RemotePlayerBehaviour Create(Vector3 position, Quaternion rotation) {
			// Model seçimi: önce NPC (oyunun kendi sürücüsünden çıkarıldı),
			// yoksa MPPlayerModel. İkisi de yüklenemezse null.
			//
			// NPC modeli mesh/iskelet/dokuları oyunun kendi verisinden geldiği
			// için gerçek NPC gibi görünür VE gerçek sürüş animasyonlarını
			// taşır (fat_get_car_idle1/2 -> DriveIdle/Drive). MPPlayerModel'de
			// oturma pozu yok, CrouchLow'a düşüyordu.
			// Govde secimi: secili SKIN'in kendi govdesi.
			//
			// ONCEKI SURUM: PlayerAssetNpc.Prefab (TEK aktif prefab) kullaniliyordu.
			// Bu iki hatayi birden yapiyordu:
			//   1) Govde secimi menude yapilinca AKTIF prefab degisiyor ve
			//      dunyadaki kuklar bir sonraki dogurmada o govdeye giyiyordu;
			//      menu kapaninca geri donmuyordu.
			//   2) Kullanicinin sectigi skin'in govdesi hic dikkate alinmiyordu;
			//      her zaman 1712 kullaniliyordu (70 skinin 54'unun kiyafeti
			//      yanlis bedene oturuyordu).
			//
			//   3) KARSININ gorunumu icin yerel secim KULLANILMAZDI.
			//      Canli hata: "menuden sectigim player gorunumu karsidaki
			//      playerde de gorunuyor". Cocuk, yerel PlayerSkin'in
			//      EffectiveBody'su ve Apply'i ile kuruluyordu; yani
			//      iki taraf ayni gorunumu tasiyordu.
			//
			// Artik govde KARSININDAN gelir: RemotePlayerSync, PlayerInfo
			// ile peer'in skin indeksini ve govde secimini ogrenir.
			// Peer bildirmediyse (eski build) varsayilana dusulur.
			//
			// PlayerAssetNpc.PrefabForBody aktif prefabi DEGISTIRMEZ ve o
			// govdenin kok donusunu de verir (her mesh ayri olculerek
			// duzeltildigi icin govdeden govdeye degisebilir).
			int wantBody = PlayerSkin.BodyForRemote(
				RemotePlayerSync.PeerSkinIndex, RemotePlayerSync.PeerBodyOverride);
			GameObject prefab;
			Quaternion prefabRot;
			bool npcModel = PlayerAssetNpc.PrefabForBody(wantBody, out prefab,
				out prefabRot);
			if (!npcModel) {
				prefab = PlayerAssetNpc.Prefab;
				prefabRot = PlayerAssetNpc.PrefabLocalRotation;
				npcModel = (prefab != null);
			}
			if (!npcModel) prefab = PlayerAsset.Prefab;
			if (prefab == null) return null;   // nedenler zaten loglandi.

			// Prefab kokunun KENDI donusu (NPC'de mesh Z ekseni boyunca
			// uzatilmis saklandigi icin olculup duzeltilmis bir dondurme var).
			// Instantiate(prefab, pos, rot) kok donusunu USTUNE YAZAR; gelen
			// ag donusu ile prefab'in kendi donusunu birlestirip geri koyuyoruz.
			Quaternion spawnRotation = rotation;
			if (npcModel) {
				try { spawnRotation = rotation * prefabRot; }
				catch { spawnRotation = rotation; }
			}

			// Gelen konum AYAK seviyesidir. Önce düz konumda doğuruyoruz, sonra
			// modelin en alçak noktasını ölçüp yukarı kaydırıyoruz: kukla tam
			// yere oturur. Canlı kanıt: tahmini 0.867 sabiti kuklayı "yerden
			// bir tık" havada bırakıyordu.
			//
			// ÖLÇÜLEN TELAFİ DAVRANIŞA DA YAZILIR (aşağıdaki not).
			GameObject body = null;
			try {
				body = (GameObject)Object.Instantiate(prefab, position, spawnRotation);
			}
			catch (System.Exception e) {
				SmpLog.Error("player model instantiate failed: "
					+ e.GetType().Name + ": " + e.Message);
				return null;
			}
			if (body == null) return null;

			body.name = ROOT_NAME;

			float lowest, highest;
			MeasureVerticalExtent(body, out lowest, out highest);
			float lift;
			if (float.IsNaN(lowest) || float.IsNaN(highest)) {
				// Renderer yok (bundle bozuk?): eski tahmini değere düş.
				lift = FOOT_OFFSET;
				SmpLog.Warning("player model: no renderer bounds, using the "
					+ FOOT_OFFSET.ToString("F3") + " m estimate.");
			}
			else {
				// ÖLÇEK HESABI — DÜNYA YÜKSEKLİĞİNE GÖRE.
				//
				// KULLANICI KANITI: "player çok küçük görünüyor" (ekran
				// görüntüsü: uzaktaki oyuncu normal insan boyunun ~1/4'ü).
				//
				// Eski kod ve YORUMU çelişiyordu. Yorum "bundle ölçeğini
				// EZBERLEME, sadece targetScale yaz" diyordu; kod da öyle
				// yapıyordu — ama bu HATALI:
				//
				// `MeasureVerticalExtent` sınırları `root.InverseTransformPoint`
				// ile alıyor, yani KÖK-UZAYI. Bu uzay kökün KENDİ
				// localScale'ini İÇİNDE barındırır: logdaki "bundle scale 3.714"
				// çarpanı zaten localHeight'in İÇİNDEDİR, ayrıca çarpılmaz:
				//
				//   localHeight = 2.112 * 3.714 = 7.844   (2.112 DEĞİL)
				//   targetScale = 1.80 / 7.844 = 0.2295
				//   localScale  = 0.2295                  // 3.714 SİLİNDİ
				//   dünya boyu   = 7.844 * 0.2295 = 1.80 m ← doğru görünüyor
				//
				// …ama yalnızca model TEK katmanlıysa. Oyuncu kökü bir ebeveyne
				// (PlayerHolder vb.) bağlıysa ve/veya model içinde ikinci bir
				// ölçek varsa, ölçülen "kök-uzayı boyu" artık DÜNYA boyu
				// değildir; çarpan yanlış büyüklüğe bölünür ve model küçük
				// kalır. Ölçtüğümüz şey DÜNYA boyu olmak zorunda:
				//
				// DOĞRU:
				//   worldHeight = localHeight * lossyScale.y   ← dünya metre
				//   k           = TARGET_HEIGHT / worldHeight
				//   localScale *= k                            ← çarpan, üstüne
				//
				// Bundle ölçeği ne olursa olsun sonuç 1.80 m olur.
				Vector3 ls = body.transform.localScale;
				float localHeight = highest - lowest;
				float lossyY = 1f;
				try { lossyY = Mathf.Abs(body.transform.lossyScale.y); } catch { }
				if (lossyY < 0.0001f || float.IsNaN(lossyY) || float.IsInfinity(lossyY)) lossyY = 1f;

				float worldHeight = localHeight * lossyY;
				float k = 1f;
				if (worldHeight > 0.05f && !float.IsNaN(worldHeight)) {
					k = TARGET_HEIGHT / worldHeight;
					body.transform.localScale = ls * k;
				}
				// Uygulanan kök dünya ölçeği (Y ekseni) — yer hesabı için.
				float appliedScale = lossyY * k;

				// ------------------------------------------------------------------
				// DOĞRULAMA GEÇİŞİ — ölçüme GÜVENME, SONUCU ÖLÇ.
				//
				// KULLANICI KANITI: ilk geçiş "final 1.80 m" yazmasına rağmen
				// ekran görüntüsünde oyuncu ~0.8 m görünüyordu (tuvaletle
				// karşılaştırıldı). Yani ilk ölçüm yanlış.
				//
				// Neden yanlış olabilir:
				//   * `Renderer.bounds`, Instantiate'ten SONRA geçersizdir;
				//     SkinnedMeshRenderer'ın bounds'ı bir güncelleme geçişi
				//     ister. Asset'ten gelen bayat bounds 2.112 gibi "inandırıcı"
				//     ama yanlış bir değer verebilir.
				//   * `InverseTransformPoint` kök-uzayına ölçer; iç içe ölçekli
				//     bir düğüm varsa (FBX'teki Armature/mixamorig gibi)
				//     ilişki doğrusal değildir.
				//
				// ÇÖZÜM (öz-düzeltici): ölçek uygulandıktan SONRA dünya
				// uzayında yeniden ölç. Fark %5'ten büyükse ikinci çarpanı
				// uygula. Böylece hangi ölçüm yanlış olursa olsun sonuç 1.80 m'ye
				// yakınsar; ikinci ölçüm artık gerçek dünya uzayıdır.
				// ------------------------------------------------------------------
				float wLow, wHigh;
				MeasureMeshWorldY(body, out wLow, out wHigh);
				float worldNow = wHigh - wLow;
				if (worldNow > 0.05f && !float.IsNaN(worldNow)) {
					float k2 = TARGET_HEIGHT / worldNow;
					if (k2 > 0.05f && k2 < 20f && (k2 < 0.95f || k2 > 1.05f)) {
						body.transform.localScale = body.transform.localScale * k2;
						appliedScale *= k2;
						MeasureMeshWorldY(body, out wLow, out wHigh);
						worldNow = wHigh - wLow;
						k *= k2;
						SmpLog.Log("player model: first pass was off by "
							+ (TARGET_HEIGHT / worldNow / k).ToString("F3")
							+ "x -> second correction k2=" + k2.ToString("F4"));
					}
				}

			// ------------------------------------------------------------------
			// KÖK YÜKSEKLİĞİ — DÜNYA ÖLÇÜMÜYLE (dönüş/ölçekten bağımsız)
			// ------------------------------------------------------------------
			// CANLI KÖK NEDEN (ölçüldü, tahmin edilmedi):
			//
			//   `lowest` KÖK-UZAYINDA ölçülüyordu (root.InverseTransformPoint).
			//   Ama kök DÖNDÜRÜLMÜŞ (PrefabLocalRotation) olduğu için
			//   dünya-dikey mesafe kök-uzayı Y'sine yansımıyor.
			//
			//   Sayılarla çelişki:
			//     yerleşik kok        =  0.078
			//     GERÇEK mesh tabanı  =  0.085  → kokun 0.007 ÜSTÜNDE
			//     lowest = -0.676, appliedScale = 0.989
			//       → -lowest*appliedScale = 0.669 → kokun 0.669 ALTINDA
			//
			//   Fark tam 0.676 = |lowest|. Yani işaret/eksen çevirme
			//   hatası birebir. Sonuç: model 0.656 m FAZLA yukarıda,
			//   ölçülen gerçek boşluk 0.675 m — ekranda gördüğün.
			//
			//   Önceki sürüm düzeltmeleri hep yanlış yeri hedefledi
			//   (modeli yükseltti), çünkü elimde DOĞRU bir sayı yoktu.
			//
			// DOĞRU YOL: kökü `position`'a (lift'siz) koy, mesh'in DÜNYA
			// en alçak noktasını oku, farkı al. Dönüş ve ölçek ne olursa
			// olsun doğru; kök-uzayı aracılığına gerek yok.
			body.transform.position = position;   // temiz ölçüm için lift'siz

			float rawLow, rawHigh;
			MeasureMeshWorldY(body, out rawLow, out rawHigh);

			float footOffset;
			string footSrc;
			if (!float.IsNaN(rawLow) && !float.IsNaN(position.y)) {
				footOffset = position.y - rawLow;   // dünya ölçümü
				footSrc = "dunya (guvenilir)";
			}
			else if (!float.IsNaN(lowest) && !float.IsNaN(appliedScale)) {
				footOffset = -lowest * appliedScale;  // eski yol (donmeye duyarsiz)
				footSrc = "kok-uzay (donmus kok - SORUNLU)";
			}
			else {
				footOffset = FOOT_OFFSET;
				footSrc = "sabit yedek";
			}
			if (float.IsNaN(footOffset) || float.IsInfinity(footOffset))
				footOffset = FOOT_OFFSET;

			lift = footOffset + GROUND_EPSILON;

			// NEGATİF lift GEÇERLİDİR: mesh'in tabanı kökün ÜSTÜNDEYSE
			// doğru yerleştirme kökü ayak konumunun altındadır.
			// ÖNCEKİ "if (lift < 0f) lift = 0f;" bunu bozuyordu.
			// Sadece sayısal bozulmayı ele.
			if (float.IsNaN(lift) || float.IsInfinity(lift)) lift = 0f;

			// ------------------------------------------------------------------
			// ÖNCE YERLEŞTİR, SONRA ÖLÇ.  (SIRA ÖNEMLİ)
			// ------------------------------------------------------------------
			// CANLI HATA: ölçüm satır 482'de, yerleştirme satır 498'de
			// yazıyordu — yani 16 satır SONRA. Sonuç: `lift` 0.689 ->
			// 0.649 yapıldı (model 4 cm aşağı gitti) ama "ZEMIN ustunde"
			// 0.027'de KİLİTLİ kaldı, İKİ KEZ DEĞİŞMEDİ.
			//
			// NEDEN: gap = wLow - zeminY. İki terim de yerleştirilMEMİŞ
			// durumdan ölçülüyor; ikisi de aynı ölçümü paylaştığı için
			// fark lift'ten BAĞIMSIZ çıkıyor.
			//
			// DERS: bir düzeltmeyi ölçtüğünü iddia etmeden önce
			// "ölçüm, değişikliği YANSITTI MI?" diye sor. Değişiklik
			// ölçüme GİRMİYORSA ölçüm yanlış yerde.
			// ------------------------------------------------------------------
			body.transform.position = position + new Vector3(0f, lift, 0f);

			// ÖLÇÜM: mesh'in DÜNYA en alçak noktası ile ZEMİN arasındaki
			// fark. Sıfıra yakınsa model zeminde, pozitifse süzülüyor.
			// Artık yerleşik hali ölçtüğü için GROUND_EPSILON'a duyarlı.
			float groundY;
			float floatAbove = GroundGap(body, position, out groundY);
			// NOT: buradaki degerler TEK SATIR olarak yazilir; asagida
			// ayri bir dogrulama satiri var.
			SmpLog.Log("player model: footOffset=" + footOffset.ToString("F3") + " (" + footSrc + ") lift=" + lift.ToString("F3")
				+ ", rawLow=" + (float.IsNaN(rawLow) ? "NaN" : rawLow.ToString("F3")) + ", kok-uzay lowest=" + lowest.ToString("F3") + " x scale " + appliedScale.ToString("F3") + " = " + (-lowest * appliedScale).ToString("F3") + ", posY=" + position.y.ToString("F3")
				+ " | ZEMIN ustunde " + floatAbove.ToString("F3") + " m"
				+ (float.IsNaN(groundY) ? " (zemin bulunamadi)"
					: " (zemin y=" + groundY.ToString("F2") + ")"));
				SmpLog.Log("player model: VERIFIED world height " + worldNow.ToString("F3")
					+ " m (target " + TARGET_HEIGHT.ToString("F2") + " m)"
					+ "  root-local " + localHeight.ToString("F3")
					+ " x rootScale " + appliedScale.ToString("F3")
					+ " = " + (localHeight * appliedScale).ToString("F3")
					+ "; lift " + lift.ToString("F3") + " m (footOffset "
					+ footOffset.ToString("F3") + ", wLow=" + wLow.ToString("F3")
					+ ", posY=" + position.y.ToString("F3") + ").");
			}

			try {
				// Model kökünde SkinnedMeshRenderer var; alt nesnelerde başka
				// renderer'lar da olabilir, hepsine bakıyoruz.
				Renderer[] renderers = body.GetComponentsInChildren<Renderer>(true);
				for (int i = 0; i < renderers.Length; i++) {
					Renderer r = renderers[i];
					if (r == null) continue;
					r.enabled = true;

					// Ekrana girerken bir kare invisible olmasın.
					SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
					if (smr != null) smr.updateWhenOffscreen = true;

					// Kukla oyuncuyla çarpışmasın.
					r.gameObject.layer = 0;
				}

				// Modelde collider yoktur ama bir Instantiate sızdıysa kaldır:
				// görsel bir gövde fiziksel engel olmamalı.
				Collider[] colliders = body.GetComponentsInChildren<Collider>(true);
				for (int i = 0; i < colliders.Length; i++) {
					if (colliders[i] != null) Object.Destroy(colliders[i]);
				}

				Rigidbody[] bodies = body.GetComponentsInChildren<Rigidbody>(true);
				for (int i = 0; i < bodies.Length; i++) {
					if (bodies[i] == null) continue;
					bodies[i].isKinematic = true;
					bodies[i].detectCollisions = false;
				}
			}
			catch (System.Exception e) {
				// Model yine de durur; sadece görsel ayarlar eksik kalır.
				SmpLog.Warning("player model setup incomplete: "
					+ e.GetType().Name + ": " + e.Message);
			}

			RemotePlayerBehaviour behaviour = body.AddComponent<RemotePlayerBehaviour>();
			// Telafi SADECE doğurma anında konuma bir kez yazılmakla kalmamalı;
			// her konum paketinde de uygulanmalı (aşağıdaki not).
			behaviour.GroundLift = lift;
			behaviour.Setup(npcModel);

			// Görünüm: KARSININ skin'i bu gövdeye uygulanır, yerelinin değil.
			// (Canlı hata: yerel skin uygulanınca menüdeki seçim karşı
			// tarafın kuklasını da değiştiriyordu.)
			// NPC modeli yoksa ApplyIndex zaten dokunmuyor (3 submesh
			// arar, MPPlayerModel'de yok).
			if (npcModel)
				PlayerSkin.ApplyIndex(body, RemotePlayerSync.PeerSkinIndex);

			return behaviour;
		}

		/// <summary>
		/// Peer görünümü değişti: kuklanın kıyafetini yeniden giydirir.
		///
		/// GÖVDE değişmişse mesh de değişir; mesh boneWeights/bindpose
		/// taşıdığı için yeniden doğurmak gerekir. O yüzden yalnızca
		/// skin indeksi değiştiyse kumaş yenilenir; gövde değiştiyse
		/// çağıran taraf kuklayı sıfırlamalı.
		/// </summary>
		public static void RefreshAppearance(GameObject body) {
			if (body == null) return;
			PlayerSkin.ApplyIndex(body, RemotePlayerSync.PeerSkinIndex);
		}

		/// <summary>Kuklayı yok eder. Sahne değişiminde çağrılır.</summary>
		public static void Destroy(RemotePlayerBehaviour body) {
			if (body == null) return;
			GameObject go = body.gameObject;
			if (go != null) Object.Destroy(go);
		}
	}
}
