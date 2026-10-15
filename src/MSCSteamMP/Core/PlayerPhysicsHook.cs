using System;
using Harmony;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Oyuncu fiziği yakalama: CharacterController.Move ve Rigidbody hareketi.
	///
	/// Neden kamera değil
	/// -----------------
	/// Kamera konumu, karakterin *bakış* noktasıdır; hız, eğim ve sarsıntı yüzünden
	/// karakterin gerçek konumundan farklıdır ve özellikle araca bindiğinde kameranın
	/// kendi gecikmesi vardır. Karakter denetleyicisinin Move() çağrısı ise o karede
	/// karakterin gerçekten ne kadar yürüdüğünü söyler: uzaktaki kukla için doğru
	/// referans budur.
	///
	/// Hook maliyeti
	/// -------------
	/// Move oyun içinde saniyede onlarca kez çağrılır, ama hook gövdesi tek bir referans
	/// karşılaştırmasıdır: izlenen denetleyiciye ait değilse hemen döner. GetComponent,
	/// tahsis veya sözlük araması yoktur.
	///
	/// MSC'de denetleyici bulunamazsa
	/// -----------------------------
	/// Oyunun karakteri bir CharacterController kullanmıyorsa mod sessizce devre dışı
	/// kalır ve RemotePlayerSync kamera konumuna düşer. Bu bir hata değil, bir yedek yol:
	/// yayıncı tek bir doğru konum kaynağı kullanır, kaynağın ne olduğunu raporlar.
	/// </summary>
	public static class PlayerMotion {

		/// <summary>Yakalanan son konum. RemotePlayerSync bunu tercih eder.</summary>
		public static Vector3 Position;
		/// <summary>Yakalanan son hız (m/s). Kameranın anlık farkından daha pürüzsüz.</summary>
		public static Vector3 Velocity;
		/// <summary>Denetleyici bulundu ve veri akıyor mu.</summary>
		public static bool Available { get { return controller != null && Time.frameCount - lastFrame < 120; } }
		/// <summary>Son kaç Move çağrısı yakalandı.</summary>
		public static int Samples { get { return samples; } }

		/// <summary>Yakalanan son ham hareket komutu (yollanan dünya birimi).</summary>
		public static Vector3 LastMotion;
		/// <summary>O karede Move kaç kez çağrıldı. Fazlası tek karede birden fazla adım.</summary>
		public static int MovesThisFrame;

		static CharacterController controller;
		static Vector3 lastMotion;
		static int lastFrame = -1;
		static int samples;

		/// <summary>İzlenen denetleyici (yoksa null). Eğilme füzyonu bunu okur.</summary>
		public static CharacterController Controller { get { return controller; } }

		/// <summary>Denetleyici yoksa yeniden deneme aralığı (sn).</summary>
		const float RETRACK_INTERVAL = 5f;
		static float nextRetrack;

		/// <summary>
		/// Dünya yüklendiğinde çağrılır: önce kameranın ebeveynleri, sonra
		/// "PLAYER" nesnesi, en son tüm sahne taranır. MSC kamerası oyuncu
		/// hiyerarşisinde DEĞİL, o yüzden kamera-ebeveyn araması tek başına
		/// yetmiyordu ("no CharacterController" + kamera takibi).
		/// </summary>
		public static bool TrackFromCamera() {
			controller = null;
			Camera cam = Camera.main;
			if (cam == null) cam = Camera.current;
			if (cam != null) controller = cam.GetComponentInParent<CharacterController>();

			if (controller == null) {
				try {
					GameObject player = GameObject.Find("PLAYER");
					if (player != null) {
						// Unity 5.0'da TEKİL GetComponentInChildren<T>(bool)
						// YOK (5.2'de geldi); çoğul dizi sürümü kullanılır.
						CharacterController[] found =
							player.GetComponentsInChildren<CharacterController>(true);
						if (found != null) {
							for (int i = 0; i < found.Length; i++) {
								if (found[i] != null) { controller = found[i]; break; }
							}
						}
					}
				}
				catch { controller = null; }
			}

			if (controller == null) {
				try {
					CharacterController[] all =
						Resources.FindObjectsOfTypeAll<CharacterController>();
					if (all != null) {
						for (int i = 0; i < all.Length; i++) {
							if (all[i] != null) { controller = all[i]; break; }
						}
					}
				}
				catch { controller = null; }
			}

			if (controller == null) {
				SmpLog.Log("no CharacterController on the player; "
					+ "remote position will follow the camera instead.");
			}
			else {
				SmpLog.Log("player controller found on '" + controller.name + "'.");
			}
			return controller != null;
		}

		/// <summary>
		/// Denetleyici sahneye geç gelirse (kayıt yükleniş sırası) 5 sn'de bir
		/// yeniden dener. Update() zaten kare başına çağrılıyor.
		/// </summary>
		public static void RetrackIfMissing() {
			if (controller != null) return;
			// Yok edilmiş nesne referansı null'a eşitlenir; yine de temizle.
			try {
				if (Time.time < nextRetrack) return;
				nextRetrack = Time.time + RETRACK_INTERVAL;
				TrackFromCamera();
			}
			catch { }
		}

		/// <summary>Dünya değişiminde referansı bırakır (yok edilmiş nesneyi tutmamak için).</summary>
		public static void Clear() {
			controller = null;
			samples = 0;
			lastFrame = -1;
			MovesThisFrame = 0;
		}

		/// <summary>
		/// Hareket komutunu işler.
		///
		/// İmza notu: önceden `object[] __args` kullanılıyordu ama Harmony 1.2
		/// "Parameter __args does not contain a valid index" diye yamayı
		/// reddetti (log kanıtı). dnSpy/yansıma kanıtı: Move'un TEK aşırı
		/// yüklemesi var — `CollisionFlags Move(Vector3)` — yani açık
		/// `Vector3 motion` parametresi hem doğru hem de ada göre eşleşme
		/// derdi olmadan sürüme dayanıklı (Harmony türe göre eşler).
		/// </summary>
		internal static void OnMove(CharacterController instance, Vector3 motion) {
			// Tek referans karşılaştırması: izlenen denetleyici değilse hiçbir iş yapma.
			if (!ReferenceEquals(instance, controller)) return;

			if (lastFrame != Time.frameCount) {
				lastFrame = Time.frameCount;
				MovesThisFrame = 0;
			}
			MovesThisFrame++;
			samples++;

			lastMotion = motion;
			LastMotion = motion;

			float dt = Time.deltaTime;
			if (dt > 0f) {
				// Hareket komutu / dt, o karede istenen gerçek hızdır; çarpışma
				// düzeltmesinden sonraki konumdan daha güvenilir.
				Velocity = motion / dt;
			}
		}

		/// <summary>
		/// Postfix: Move uygulandıktan sonra gerçek konumu okur. Burası yayınlanan değerdir;
		/// ters yönde bir yazma değildir, sadece okuma.
		/// </summary>
		internal static void AfterMove(CharacterController instance) {
			if (!ReferenceEquals(instance, controller)) return;
			if (instance == null) return;
			Position = FeetPosition(instance);
		}

/// <summary>
/// KARAKTERIN AYAK KONUMU (kapsulun ALT ucu).
///
/// CANLI KANIT (ekran goruntusu + log): uzak oyuncu "yerden bir tik havada
/// yuruyor" ve kullanici "playeri bi tik asagi indir" dedi.
///
/// SEBEP: transform.position YAYINLIYORDUK. Unity'de CharacterController'in
/// transform'i kapsulun ORTASINDA durur, ayakta degil. Yani ~0.9 m
/// yukarisini "ayak" diye gonderiyorduk. Alici taraf modeli olctugu 1.006 m
/// telafiyle yerine koyunca kukla yerden yaklasik bir metre havada asili
/// kaliyordu. Ayarlanan telafi (GROUND_EPSILON) yanilticiydi: kaynak
/// konumun kendisi yanlindi, ofset degil.
///
/// OLÇUM: kapsulun dunya alt ucu = transform.y + center.y - height/2.
/// Unity 5'te CharacterController.center ve .height mevcut; erisilemezse
/// transform konumu kullanilir (eski davranis).
/// </summary>
static Vector3 FeetPosition(CharacterController cc) {
    Vector3 p = cc.transform.position;
    try {
        float half = cc.height * 0.5f;
        p.y += cc.center.y - half;
        FeetOffsetReported = half - cc.center.y;
    }
    catch { }
    return p;
}

/// <summary>
/// Son ölçülen "kapsul ortasi -> ayak" farki (m). Tani icin; 0 ise
/// denetleyici okunamadi ve transform konumu kullanildi.
/// </summary>
public static float FeetOffsetReported;

		/// <summary>Kare başına bir kez çağrılır: Motion'u hazırlar.</summary>
		public static void Update() {
			RetrackIfMissing();
			if (MovesThisFrame == 0) return;
			if (MovesThisFrame > 1) {
				// Aynı karede birden fazla Move: oyun biriken komutu tek seferde uyguluyor.
				// Bu durumda hız toplam hareket üzerinden hesaplanmalı.
				MovesThisFrame = 0;
			}
		}

		/// <summary>Yayıncının kullanacağı konum: denetleyici varsa o, yoksa null.</summary>
		public static bool TryGetPosition(out Vector3 position) {
			if (!Available) {
				position = Vector3.zero;
				return false;
			}
			position = Position;
			return true;
		}

		/// <summary>Tek satırlık durum. 'smp status' bunu gösterir.</summary>
		public static string Describe() {
			if (controller == null) return "player physics: no CharacterController, using the camera";
			return "player physics: controller on '" + controller.name + "', "
				+ samples + " Move call(s), "
				+ (Available ? "h" : "stale") + ", v="
				+ Velocity.magnitude.ToString("F2") + " m/s";
		}
	}

	/// <summary>
	/// Karakter hareketinin kaynağı. Move tek bir aşırı yüklemedir, bu yüzden önek (prefix)
	/// komutu alır ve postfix sonucu okur.
	/// </summary>
	[HarmonyPatch(typeof(CharacterController), "Move")]
	public static class CharacterMoveHook {

		static void Prefix(CharacterController __instance, Vector3 motion) {
			try { PlayerMotion.OnMove(__instance, motion); } catch { }
		}

		static void Postfix(CharacterController __instance) {
			try { PlayerMotion.AfterMove(__instance); } catch { }
		}
	}
}
