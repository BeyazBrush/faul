using System;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// SKİN MENÜSÜ İÇİN 3B ÖNİZLEME.
	///
	/// Ne yapıyor
	/// ---------
	/// Sahnede görünmez bir yerinde (y = -5000) bir kamera + iki ışık kurar,
	/// NPC modelini oraya yerleştirir ve RenderTexture'a çizer. Menü OnGUI'si
	/// o dokuyu bir dikdörtgene çizer. Model yavaşça kendi ekseninde döner.
	///
	/// Neden gizli bir sahne parçası
	/// ----------------------------
	/// Oyuncunun kendi kameraları bu modeli GÖRMEMELİ. Menü sırasında oyun
	/// kamerası aktif; model y = -5000'de ve oyun kamerasının uzak kesme
	/// mesafesi ~1000 olduğu için kadrajdan tamamen düşer. RenderTexture'a
	/// çizen kamera `enabled = false` olup yalnızca elle Render() alır.
	///
	/// Ortam ışığı
	/// -----------
	/// RenderSettings.ambientLight GLOBAL'dir; değiştirilirse oyunun
	/// ambient'ı bozulur. Bu yüzden önce eski değer saklanır, Render()
	/// çevresinde geçici olarak aydınlatma yapılır, hemen geri konur.
	///
	/// Animasyon
	/// ---------
	/// Model T-pozda durmasın diye "Idle" klibi oynatılır. Klip yoksa
	/// (NPC bundle'ı yüklenemedi) model yine gösterilir, sadece hareketsiz.
	///
	/// mscorlib 2.0: LINQ yok.
	/// </summary>
	public class SkinPreview {

		const float HIDDEN_Y = -5000f;
		const int RT_W = 288;
		const int RT_H = 432;
		// Kadraj: 1.60 m'lik model 32° FOV'ta rahat sığsın diye kamera
		// 3.15 m uzakta ve göğse bakıyor. 2.65 m + 1.60 m model kadrajı
		// tepeden tabağa dolduruyor, model kenarlardan taşıyordu.
		// Kadraj payi (3.60 m + 0.80 merkez): onceki 3.15/0.90 bandinda ayaklar cerceveye degiyordu.
		const float DIST = 3.6f;
		const float LOOK_Y = 0.80f;
		/// <summary>Modelin ölçeklendirileceği vitrin yüksekliği (m).</summary>
		const float STAGE_H = 1.60f;
		/// <summary>Kendi ekseninde dönüş hızı (derece/saniye). Vitrin
		/// hızı: yavaş, gövde okunur, 360°'de 18 saniye.</summary>
		const float SPIN_DEG_PER_SEC = 20f;

		GameObject rig;               // kamera + ışıkların altında olduğu boş kök
		Camera cam;
		Light key, fill;
		RenderTexture rt;
		GameObject model;
		Animation anim;
		/// <summary>Karakteri dik tutan düzeltme dönüşü (spin hariç).</summary>
		Quaternion upright = Quaternion.identity;
		float spin;                   // derece
		bool alive;
		int lastSkin = -1;
		/// <summary>Şu an kurulu olan gövdenin vertex sayısı (skin değişince
		/// model yeniden kurulur).</summary>
		int modelBody = -1;
		bool diagDone;
		bool diagAnimDone;
		float diagT;

		public Texture Preview { get { Ensure(); return rt; } }
		public bool Ready { get { Ensure(); return rt != null && model != null; } }

		// ------------------------------------------------------------------

		/// <summary>Altyapıyı kurar. Güvenle tekrar çağrılabilir; bir kez kurar.</summary>
		public void Ensure() {
			if (alive) return;
			alive = true;
			try {
				rt = new RenderTexture(RT_W, RT_H, 16, RenderTextureFormat.ARGB32);
				rt.name = "mpnpc_skin_preview_rt";
				rt.filterMode = FilterMode.Bilinear;
				rt.Create();

				rig = new GameObject("MPNPC_SkinPreview");
				rig.transform.position = new Vector3(0f, HIDDEN_Y, 0f);

				// Unity 5.0'da GameObject.light / .camera kisayollari YOK; AddComponent.
				key = new GameObject("key").AddComponent<Light>();
				key.transform.parent = rig.transform;
				key.transform.localRotation = Quaternion.Euler(28f, -145f, 0f);
				key.type = LightType.Directional;
				key.intensity = 1.05f;
				key.color = new Color(1f, 0.97f, 0.92f);
				key.shadows = LightShadows.None;

				fill = new GameObject("fill").AddComponent<Light>();
				fill.transform.parent = rig.transform;
				fill.transform.localRotation = Quaternion.Euler(14f, 130f, 0f);
				fill.type = LightType.Directional;
				fill.intensity = 0.45f;
				fill.color = new Color(0.72f, 0.80f, 1f);
				fill.shadows = LightShadows.None;

				cam = new GameObject("cam").AddComponent<Camera>();
				cam.transform.parent = rig.transform;
				cam.transform.localPosition = new Vector3(0f, LOOK_Y, -DIST);
				cam.transform.localRotation = Quaternion.identity;
				cam.fieldOfView = 32f;
				cam.nearClipPlane = 0.05f;
				cam.farClipPlane = 20f;
				cam.clearFlags = CameraClearFlags.SolidColor;
				cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // saydam
				cam.targetTexture = rt;
				cam.enabled = false;      // yalnizca elle Render()
				cam.depth = -100f;
			}
			catch (Exception e) {
				SmpLog.Warning("skin preview init failed: "
					+ e.GetType().Name + ": " + e.Message);
				Release();
			}
		}

		/// <summary>Modeli kurar (yoksa). NPC bundle'ı yüklü değilse false.</summary>
		public bool EnsureModel() {
			Ensure();
			if (rt == null) return false;

			// Gövde değiştiyse modeli yeniden kur.
			//
			// Kıyafet dokusu mesh'e göre tasarlandığı için skin'in kendi
			// gövdesi kullanılmalı; önceki sürüm daima 1712'ye sabitliydi
			// ve 70 skin'in 54'ü yanlış bedene giyiyordu.
			// EffectiveBody: elle secilen govde varsa o, yoksa skin'in
			// kendi govdesi, o da yoksa pakete en yakin olan.
			int wantBody = PlayerSkin.EffectiveBody;
			if (model != null && wantBody == modelBody) return true;
			if (model != null) {
				try { UnityEngine.Object.Destroy(model); } catch { }
				model = null;
			}

			if (!PlayerAssetNpc.SelectBody(wantBody)) wantBody = NpcSkinCatalog.DEFAULT_BODY;

			GameObject prefab = PlayerAssetNpc.Prefab;
			if (prefab == null) return false;
			try {
				model = (GameObject)UnityEngine.Object.Instantiate(prefab);
				model.name = "preview_body";
				model.transform.parent = rig.transform;
				// Prefab koke kendi donusu tasir (mesh Z ekseni boyunca
				// uzatilmis; bundle uretimi duzeltti). Instantiate bunu
				// EZMEZ, ama konum/olcek mutlaka sifirlanmali.
				model.transform.localPosition = Vector3.zero;
				model.transform.localRotation = PlayerAssetNpc.PrefabLocalRotation;
				model.transform.localScale = Vector3.one;

				// Modeli ölçekle ve DİK TUT.
				//
				// TABAN DÖNÜŞÜ = prefab kökünün kendi dönüşü. Bundle
				// üreticisi yönü mesh'ten ölçüp prefab köküne yazıyor
				// (unity/build_npc.log: "up=(0,0,1) -> (0,1,0), kutu
				// X 1.353 / Y 1.820 / Z 0.340, ayaklar Y=0") ve iskelet
				// bind pozundan kurulduğu için omurga bağ pozunda zaten dik.
				//
				// KRİTİK HATA (burada bir kez daha yazalım): upright
				// "kullanılacak dönüş" anlamına gelir ve Spin() onu
				// localRotation'a yazar. Onceki surumde upright yalnizca
				// DÜZELTME dönüşüydü ve dogru dönüş ComputeUpright'in
				// ICINDE saklaniyordu. Bundle duzeltilince ComputeUpright
				// "zaten dik" deyip KIMLIK dondurmeye basladi ve Spin()
				// prefab dönüşünü EZDI -> model mesh'in authored yonune
				// (govde Z ekseni) dondu, yani YANA YATTI. Bundle'i
				// duzeltmek bu hatayi ORTAK CIKARDI; kok neden burada.
				//
				// Cozum: upright DAIMA prefab dönüşünü icerir; olculebilen
				// egim varsa duzeltme onun USTUNE compose edilir.
				// (Yedek MPPlayerModel icin bu ag gecikmeli bir sigorta.)
				ScaleToFit(model);
				upright = ComputeUpright(model,
					PlayerAssetNpc.PrefabLocalRotation);
				Spin();

				// Yuzeyler cift tarafi olsun: mesh tek tarafliyse arka
				// yuzler gorunmez olur ve model delik gorunur.
				SkinnedMeshRenderer[] smrs
					= model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				for (int i = 0; smrs != null && i < smrs.Length; i++) {
					if (smrs[i] == null || smrs[i].sharedMesh == null) continue;
					smrs[i].updateWhenOffscreen = true;

					// localBounds'ı mesh'in gercek kutusuna esitle.
					//
					// NEDEN: Unity, SkinnedMeshRenderer'a localBounds'i
					// skinleme sirasinda HESAPLAR ve bu sırada mesh henüz
					// pozlanmadigi icin kutu bozuk kalir. Log kaniti:
					//     mesh.bounds = Center(0.0, 0.0, 0.9) Extents(0.2,0.7,0.9)
					//     localBounds = Center(0.0, 0.0, 0.0) Extents(0.8,0.2,0.7)
					// localBounds mesh uzayindadir ve renderer'in YEREL
					// uzayinda gecerlidir; kok donusumu bunu degistirmez.
					// Yanlis kutu, kameraya gore culling yaparak
					// govdenin bir kismini hic cizmiyor ve kiyfetler
					// "yarim giydirilmis" gorunuyor.
					//
					// Mesh'in kendi kutusu guvenilir kaynak (bundle'da
					// yazildi, ustune yazma yapmadik).
					try {
						Bounds b = smrs[i].sharedMesh.bounds;
						// birkac kat buyut: animasyonla hareket eden
						// uzuvlar da hicbir zaman kirpilmasin
						smrs[i].localBounds = new Bounds(
							b.center,
							new Vector3(b.extents.x * 2f,
							            b.extents.y * 2f,
							            b.extents.z * 2f));
					}
					catch { }
				}

				// Animasyon
				anim = null;
				try { anim = model.GetComponentInChildren<Animation>(); }
				catch { anim = null; }
				if (anim != null) {
					anim.cullingType = AnimationCullingType.AlwaysAnimate;
					anim.playAutomatically = false;
					try { if (anim["Idle"] != null) anim.Play("Idle"); }
					catch { }
				}
				lastSkin = -1;
				modelBody = wantBody;
				return true;
			}
			catch (Exception e) {
				SmpLog.Warning("skin preview model failed: "
					+ e.GetType().Name + ": " + e.Message);
				model = null;
				return false;
			}
		}

		/// <summary>Seçili skin'i modele uygular (önbellek varsayımıyla).</summary>
		public void RefreshSkin() {
			EnsureModel();
			if (model == null) return;
			PlayerSkin.Apply(model);
			lastSkin = PlayerSkin.Selected;
		}

		/// <summary>Her karede çağrılır: modeli yavaşça döndürür.</summary>
		public void Tick(float dt) {
			if (model == null || rig == null) return;
			spin += dt * SPIN_DEG_PER_SEC;
			if (spin > 360f) spin -= 360f;
			Spin();

			// Teşhis: klipler animasyonun İLK karede uygulanmasi gerekir
			// (Animation component Update'te hesaplar). 0.5 saniye sonra
			// ikinci kez dök, boylece "klip uygulandi mi" sorusu cevaplanir.
			if (diagDone) {
				diagT += dt;
				if (!diagAnimDone && diagT > 0.5f) {
					diagAnimDone = true;
					DumpAnim("0.5sn sonra");
				}
			}
		}

		/// <summary>
		/// Dönüşü uygular. Sıra ÖNEMLİ: spin ÖNCE, prefab dönüşü SONRA.
		///
		/// NEDEN: prefab kök dönüşü Y ekseninde değil; mesh'in Z eksenini
		/// dünya Y'sine çeviren bir dönüş (unity/build_npc.log:
		/// "up=(0,0,1) -> (0,1,0)"). Spin'i prefab dönüşünün SOLUNA
		/// yazarsak (upright * Euler(0,spin,0)) dönüş, eğik bir eksende
		/// uygulanır: model kendi Y ekseninde değil, dünya Y'siyle
		/// prefab ekseni arasında bir açıda döner ve dik durması bozulur.
		///
		/// Doğrusu: Euler(0,spin,0) * upright — önce modeli kendi
		/// dikey ekseninde çevir, sonra prefabın dünya dönüşünü uygula.
		/// Böylece spin daima dünya Y'si etrafında, model yerinde döner.
		/// </summary>
		void Spin() {
			if (model == null) return;
			try {
				model.transform.localRotation =
					Quaternion.Euler(0f, spin, 0f) * upright;
			}
			catch { }
		}

		/// <summary>
		/// Önizlemede kullanılacak TABAN dönüşü: <paramref name="baseRot"/>
		/// ve gerekiyorsa düzeltme.
		///
		/// pelvis→head yönü rig uzayında ölçülür ve +Y'ye çevrilir. Rig'in
		/// dönüşü kimlik olduğu için dünya yönü doğrudan rig yönüdür.
		/// Kemik bulunamazsa veya model zaten dike yakınsa DÜZELTME YAPILMAZ
		/// — ama <paramref name="baseRot"/> yine de DÖNDÜRÜLÜR. Dönmesi
		/// şart: <see cref="Spin"/> bu değeri localRotation'a yazar, dönen
		/// bir kimlik prefabın yön düzeltmesini siler ve model yana yatar.
		///
		/// Neden ölçüm gerekiyor: NPC bundle'ı düzgünse eğim ~1.6° çıkar
		/// ve düzeltme atlanır; MPPlayerModel yedeği düzgün değilse ölçüm
		/// onu kurtarır.
		/// </summary>
		static Quaternion ComputeUpright(GameObject model, Quaternion baseRot) {
			if (model == null) return baseRot;
			Transform pelvis = null, head = null;
			try {
				Transform[] all = model.GetComponentsInChildren<Transform>(true);
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] == null) continue;
					string n = all[i].name;
					if (n == "pelvis" && pelvis == null) pelvis = all[i];
					else if (n == "head" && head == null) head = all[i];
				}
			}
			catch { }
			if (pelvis == null || head == null) return baseRot;

			Vector3 up;
			try { up = head.position - pelvis.position; }
			catch { return baseRot; }
			if (up.sqrMagnitude < 1e-6f) return baseRot;
			up = up.normalized;
			// Neredeyse dik ise gereksiz düzeltme yapma (nicak etkisi)
			if (up.y > 0.985f) return baseRot;
			Quaternion corr = Quaternion.FromToRotation(up, Vector3.up);
			return corr * baseRot;
		}

		/// <summary>
		/// RenderTexture'i yeniler. OnGUI'nin Repaint olayında çağrılmalı.
		/// </summary>
		public void Render() {
			if (rt == null || cam == null || model == null) return;

			// Kamerayı modelin OTURDUĞU zeminе göre yerleştir.
			//
			// Sabit LOOK_Y yeterli değildi: ScaleToFit ölçümü yanlış
			// olduğunda model +0.80 m yukarıda duruyordu ve kamera
			// gövdenin ortasına değil beline bakıyordu. Model tabanı
			// ölçümden gelen `stageBase` ile burada tekrar hizalanır;
			// kameranın bakış yüksekliği gövde ortasının biraz üstünde
			// (LOOK_Y = STAGE_H * 0.50 + 0.10) kalır.
			if (rig != null) {
				float baseRel = model.transform.position.y - rig.transform.position.y;
				cam.transform.localPosition
					= new Vector3(0f, baseRel + LOOK_Y, -DIST);
			}

			// Skin degistiyse materyalleri tazele
			if (lastSkin != PlayerSkin.Selected) RefreshSkin();

			// Unity 5.0'da RenderSettings.ambientMode YOK; yalnizca ambientLight
			// degistirilebilir. Oyunun ambient'ini bozmamak icin eski
			// deger saklanir ve hemen geri konur.
			Color oldAmbient = RenderSettings.ambientLight;
			try {
				RenderSettings.ambientLight = new Color(0.30f, 0.31f, 0.36f);
			}
			catch { }

			try {
				// Model y = -5000'de ve oyun kamerasinin uzak kesme mesafesi
				// ~1000 oldugu icin zaten kadraj disi; bu yalnizca guvenlik.
				LayerMask prev = cam.cullingMask;
				cam.cullingMask = 1 << 0;
				cam.Render();
				cam.cullingMask = prev;
			}
			catch (Exception e) {
				SmpLog.Warning("skin preview render failed: "
					+ e.GetType().Name + ": " + e.Message);
			}

			try {
				RenderSettings.ambientLight = oldAmbient;
			}
			catch { }
		}

		/// <summary>
		/// TEŞHİS DÖKÜMÜ. Sadece bir kez (menüye girişte) çalışır.
		///
		/// NEDEN: bundle'ın tamamı çevrimdışı olarak DOĞRULANDI
		/// (tools\verify_runtime.py: bag poz sapmasi 1e-6, tum klipler CPU
		/// skinning ile render edilip dogru cikti; tools\render_npc.py).
		/// Yine de oyun ici gorsel bozuk. Demek ki sapma Unity motorunun
		/// icinde oluyor ve ancak ICERIDEN olcumle anlasilir.
		///
		/// Bu döküm ölçtüğü şeyler:
		///   - prefab kök dönüşü / ölçek / konum
		///   - ScaleToFit sonrası ölçek ve ölçülen ayak-kafa aralığı
		///   - kemiklerin DÜNYA konumu (pelvis→head eğimi: model yatıksa
		///     burada görünür)
		///   - animasyon OYNATILDIKTAN SONRA kemiklerin local rotasyonu
		///     (klip değişikliklerinin gerçekten uygulanıp uygulanmadığı)
		///   - SkinnedMeshRenderer.localBounds / sharedMesh.bounds
		///   - 3 submesh'in materyal + doku adı
		///   - kamera konum/dönüş/FOV ve RT boyutu
		/// </summary>
		public void DumpDiagnostics() {
			if (diagDone) return;
			diagDone = true;
			try {
				SmpLog.Print("--- gorsel onizleme TEHIS ---");
				if (model == null) {
					SmpLog.Print("  model YOK (bundle yuklenmemis)");
					return;
				}
				Transform mt = model.transform;
				SmpLog.Print("  kok rot=" + mt.localRotation.ToString("F4")
					+ " scl=" + mt.localScale.ToString("F3")
					+ " pos=" + mt.localPosition.ToString("F3"));
				SmpLog.Print("  prefab rot=" + PlayerAssetNpc.PrefabLocalRotation.ToString("F4"));

				Transform pelvis = FindTr(model, "pelvis");
				Transform head = FindTr(model, "head");
				Transform hl = FindTr(model, "hand_left");
				Transform hr = FindTr(model, "hand_right");
				Transform ankL = FindTr(model, "ankle_left");
				if (pelvis && head) {
					Vector3 up = head.position - pelvis.position;
					SmpLog.Print("  pelvis=" + pelvis.position.ToString("F3")
						+ " head=" + head.position.ToString("F3")
						+ " up=" + up.normalized.ToString("F3")
						+ " |up|=" + up.magnitude.ToString("F3"));
				}
				if (hl && hr)
					SmpLog.Print("  el Y ekseni=" + ((hl.position.y - hr.position.y)).ToString("F3")
						+ "  (eller ayni yukseklikte olmali)");
				if (ankL)
					SmpLog.Print("  ankle_left Y=" + ankL.position.y.ToString("F3")
						+ " (ayaklar model yerinde ~0 olmali)");

				SkinnedMeshRenderer[] smrs
					= model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				for (int i = 0; smrs != null && i < smrs.Length; i++) {
					SkinnedMeshRenderer s = smrs[i];
					if (s == null) continue;
					SmpLog.Print("  SMR GO=" + s.gameObject.name
						+ " subMesh=" + (s.sharedMesh != null ? s.sharedMesh.subMeshCount : -1)
						+ " mesh.bounds=" + (s.sharedMesh != null ? s.sharedMesh.bounds.ToString() : "-")
						+ " localBounds=" + s.localBounds.ToString()
						+ " rootBone=" + (s.rootBone != null ? s.rootBone.name : "-")
						+ " bones=" + (s.bones != null ? s.bones.Length : 0)
						+ " quality=" + s.quality);
					Material[] ms = s.sharedMaterials;
					for (int k = 0; ms != null && k < ms.Length; k++) {
						if (ms[k] == null) { SmpLog.Print("    sub" + k + ": <null>"); continue; }
						Texture t = ms[k].mainTexture;
						SmpLog.Print("    sub" + k + ": " + ms[k].name
							+ " shader=" + (ms[k].shader != null ? ms[k].shader.name : "-")
							+ " tex=" + (t != null ? t.name : "-")
							+ " " + (t != null ? t.width + "x" + t.height : ""));
					}
				}

				if (cam != null) {
					SmpLog.Print("  kamera pos=" + cam.transform.position.ToString("F3")
						+ " rot=" + cam.transform.localRotation.ToString("F3")
						+ " fov=" + cam.fieldOfView.ToString("F1")
						+ " aspect=" + cam.aspect.ToString("F3")
						+ " near/far=" + cam.nearClipPlane + "/" + cam.farClipPlane);
				}
				if (rt != null)
					SmpLog.Print("  RT " + rt.width + "x" + rt.height);

				DumpAnim("once");
			}
			catch (Exception e) {
				SmpLog.Warning("preview diagnostics failed: "
					+ e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>Animasyon OYNATILDIKTAN SONRA kemik local rotasyonlarını
		/// log'a döker. Klip eğrileriyle karşılaştırmak için.</summary>
		public void DumpAnim(string tag) {
			if (model == null) return;
			try {
				string[] names = { "pelvis", "spine_middle", "spine_upper",
					"collar_left", "shoulder_left", "arm_left",
					"collar_right", "shoulder_right", "arm_right",
					"thig_left", "knee_left", "ankle_left" };
				SmpLog.Print("  kemik local rot (" + tag + "):");
				for (int i = 0; i < names.Length; i++) {
					Transform t = FindTr(model, names[i]);
					if (t == null) continue;
					SmpLog.Print("    " + names[i].PadRight(14)
						+ t.localRotation.ToString("F4")
						+ " pos=" + t.localPosition.ToString("F3"));
				}
			}
			catch (Exception e) {
				SmpLog.Warning("preview anim dump failed: "
					+ e.GetType().Name + ": " + e.Message);
			}
		}

		static Transform FindTr(GameObject root, string name) {
			try {
				Transform[] all = root.GetComponentsInChildren<Transform>(true);
				for (int i = 0; all != null && i < all.Length; i++)
					if (all[i] != null && all[i].name == name) return all[i];
			}
			catch { }
			return null;
		}

		/// <summary>Önizlemeyi serbest bırakır. Menü kapanınca çağrılır.</summary>
		public void Release() {
			alive = false;
			try { if (model != null) UnityEngine.Object.Destroy(model); } catch { }
			try { if (rig != null) UnityEngine.Object.Destroy(rig); } catch { }
			try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } }
			catch { }
			model = null; rig = null; cam = null; rt = null;
			key = null; fill = null; anim = null;
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Modeli kadraja oturtur ve taban yerini raporlar.
		///
		/// ÖLÇÜM UZAYI — buradaki hata iki kez kadrajı bozdu:
		///
		///   mesh bounds'ı MESH-UZAYINDA saklanıyor: gövde Z ekseni
		///   boyunca (ayaklar z=0.007, kafa z=1.827), Y ise kolların
		///   açılma genişliği (±0.677). Ölçmemiz gereken Y, kök dönüşümünün
		///   (PrefabLocalRotation) mesh Z'sini dünya Y'sine çevirdiği
		///   uzaydaki Y'dir.
		///
		/// ÖNCEKİ HATALI YOL (iki deneme):
		///
		///   1) world = smr.TransformPoint(corner);
		///      local = model.InverseTransformPoint(world);
		///   Köşe önce kök dönüşümüyle dünyaya, sonra aynı dönüşümün
		///   TERSİYLE geri geliyor: ikisi birbirini İPTAL EDİYOR. Ölçülen
		///   mesh-uzayı Y'siydi = 1.353 m (kol açıklığı). Sonuç: k = 1.183,
		///   model 2.15 m, +0.80 m taban kayması — ekranda yalnız kafa ve
		///   omuzlar görünüyordu.
		///
		///   2) Yalnız SMR→kök göreli dönüşümü. Bu da YANLIŞ: mesh köşeleri
		///   kök dönüşümünden geçmez (SMR GameObject'i kimlikte), yine
		///   1.353 m çıktı.
		///
		/// DOĞRU YOL: köşeye mesh→SMR→kök→dünya zincirini elle uygula,
		///   Y'yi köke göre GÖRELİ oku:
		///
		///   y = (pos + rot * chain * corner * scale).y - root.position.y
		///
		///   InverseTransformPoint BİLEREK ÇAĞRILMAZ — o, kök dönüşümünü
		///   geri alıp mesh-uzayı Y'sini verir; iki hatayı da bu yüzden
		///   yaptık.
		///
		/// ÇEVRİMDIŞI DOĞRULAMA (UnityPy, kök rot 0.5,0.5,0.5,-0.5):
		///   genişlik(X) 1.3534   YÜKSEKLİK(Y) 1.8202   derinlik(Z) 0.3398
		///   lo=0.0067 hi=1.8270 → k=0.879, model 1.600 m, taban −0.006 m
		///   (ayaklar model tabanında ✓; kamera görünür yüksekliği 1.81 m,
		///   model kadrajın %89'unu doldurur).
		/// <returns>Model tabanının köke göre yüksekliği (m).</returns>
		static float ScaleToFit(GameObject model) {
			SkinnedMeshRenderer[] smrs
				= model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
			if (smrs == null || smrs.Length == 0) return 0f;

			Transform root = model.transform;
			Quaternion rot = root.localRotation;
			Vector3 pos = root.localPosition;
			float scale = root.localScale.x;
			if (scale <= 0.0001f) scale = 1f;

			// Ölçüm TAMAMEN YEREL: dünya konumu (root.position) kullanılmaz.
			//
			// NEDEN: önceki sürüm baseY = root.position.y alıyordu. Bu değer
			// rig'in dünya Y'sini (≈ -5000) ve modelin yerel kaymasını içerir.
			// ScaleToFit pos.y'yi GÜNCELLEYEN bir işlev olduğu için ikinci
			// çağrıda önceki kayma baseY'ye girer, lo kocaman çıkar ve
			// model yüzlerce metre uçar. Log kanıtı:
			//     pos=(0.000, -4394.856, 0.000)   (beklenen ≈ -0.006)
			// Menüye girip çıkınca gidip gelen birikimdi; her çağrı
			// üstüne ekliyordu.
			//
			// Yerel uzayda ölçmek bu sorunu tamamen ortadan kaldırır:
			// pozisyon ve ölçek bilerek hesaba katılır.
			float baseY = pos.y;

			// SMR -> kok zinciri (derinlik en fazla 64, nadiren 2-3).
			// mscorlib 2.0: sabit boyutlu dizi, List<> gerektirmez.
			Quaternion[] seg = new Quaternion[64];

			float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
			bool found = false;

			for (int i = 0; i < smrs.Length; i++) {
				SkinnedMeshRenderer smr = smrs[i];
				if (smr == null || smr.sharedMesh == null) continue;

				int n = 0;
				Transform t = smr.transform;
				while (t != null && t != root && n < seg.Length) {
					seg[n++] = t.localRotation;
					t = t.parent;
				}
				// seg[0] = SMR'nin kendi donusumu, seg[n-1] = kokun hemen alti
				// (cogu durumda hepsi kimliktir).

				Bounds b = smr.sharedMesh.bounds;
				for (int c = 0; c < 8; c++) {
					Vector3 corner = new Vector3(
						(c & 1) != 0 ? b.max.x : b.min.x,
						(c & 2) != 0 ? b.max.y : b.min.y,
						(c & 4) != 0 ? b.max.z : b.min.z);

					// mesh -> SMR zinciri (kok'ten disari = ters sira)
					Vector3 w = corner;
					for (int s = n - 1; s >= 0; s--) w = seg[s] * w;
					// kok donusumu: mesh Z'sini dunya Y'sine cevirir
					w = rot * w;
					w = pos + w * scale;

					// KOKE GORE GORELI yukseklik (tamamen yerel; baseY = pos.y)
					float y = w.y - baseY;
					if (y < lo) lo = y;
					if (y > hi) hi = y;
					found = true;
				}
			}
			if (!found || hi - lo < 0.05f || hi - lo > 50f) return 0f;

			// Vitrin yuksekligi: kamera 3.15 m, FOV 32° → gorunur yukseklik
			// 2*3.15*tan(16°) = 1.81 m. 1.60 m model bas ve ayakta pay birakir.
			float k = STAGE_H / (hi - lo);
			root.localScale = root.localScale * k;

			// En alt nokta (ayak) kokun tabaninda 0'a insin. lo koke gore
			// goreli oldugu icin olcekten bagimsizdir; once olcek, sonra
			// kayma uygulanir.
			pos.y -= k * lo;
			root.localPosition = pos;
			return pos.y;
		}
	}
}