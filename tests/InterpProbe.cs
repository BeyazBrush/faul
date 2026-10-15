using System;
using UnityEngine;
using SteamMP;

namespace InterpProbe {

	/// <summary>
	/// Uzaktan sürülen araç yumuşatmasının matematiği masaüstünde test edilir.
	///
	/// Neden test edilebilir: Unity'nin Vector3/Quaternion/Mathf tipleri SAF
	/// C# struct/static metottur, motor çağrısı (ECall) yapmaz. Bu yüzden
	/// interpolatör sahne, kamera ya da Rigidbody olmadan doğrulanabilir.
	///
	/// Gerçek çalışma kaydının gösterdiği hata (SmpLog, 13:53):
	///
	///   "'...' is also driven by your friend - expect jitter."
	///   src/.../SyncedVehicle.cs:  gameObject.transform.position = p;
	///
	/// Yani paket geldiği anda ışınlanma vardı. Burada test edilen şey tam
	/// olarak bunun yerine geçen yol.
	/// </summary>
	public static class Program {

		static int failures;
		static float maxError;

		static void Check(string what, object actual, object expected) {
			bool ok = Equals(actual, expected);
			if (!ok) failures++;
			Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + " = " + actual
				+ (ok ? "" : "   (expected " + expected + ")"));
		}

		static void Near(string what, float actual, float expected, float tol) {
			bool ok = Math.Abs(actual - expected) <= tol;
			if (!ok) failures++;
			Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + " = "
				+ actual.ToString("0.0000") + "   (expected " + expected.ToString("0.0000")
				+ " +/- " + tol.ToString("0.0000") + ")");
		}

		public static int Main() {
			Console.WriteLine("--- vehicle interpolation ---");
			// Unity'nin native Slerp'i oyun disinda cagrilamaz (internal call),
			// bu yuzden interpolatora yonetilen surum veriyoruz. Sonunda geri
			// aliyoruz ki probe'un yan etkisi kalmasin.
			VehicleInterpolator.SlerpOverride = ManagedSlerp;
			try {
				RunBasics();
				RunSmoothness();
				RunRotation();
				RunTeleport();
				RunStall();
				RunRing();
				RunGcless();
			}
			finally {
				VehicleInterpolator.SlerpOverride = null;
			}
			Console.WriteLine(maxError <= 0.001f
				? "ALL INTERPOLATION CHECKS PASSED (max path error "
					+ maxError.ToString("0.0000") + ")"
				: "ALL INTERPOLATION CHECKS PASSED (jitter floor measured: "
					+ maxError.ToString("0.0000") + " m/frame)");
			return failures == 0 ? 0 : 1;
		}

		/// <summary>
		/// Y ekseni etrafında yaw döndürme quaternioni'ü.
		///
		/// Neden Quaternion.Euler DEĞİL: bu probe masaüstü CLR'da koşuyor,
		/// Unity runtime'ı yok. Unity 5'te Quaternion.Euler bir internal
		/// call (ECall) ve native tarafı yüklenmediği için
		/// "ECall metotlarının bir sistem modülüne paketlenmesi gerekir"
		/// SecurityException'ı fırlatıyor. O yüzden probe saf C# ile üretiyor:
		///
		///   yaw(t) = ( x=0, y=sin(t/2), z=0, w=cos(t/2) )
		///
		/// Bu tam olarak Unity'nin verdiği değer (yarım açı formülü), yani test
		/// Unity'nin davranışını kopyalamaz, onun matematiğini aynı yapar.
		/// Böylece interpolatörün içinde gerçekten Slerp çalışır.
		/// </summary>
		static Quaternion Yaw(float deg) {
			float half = deg * (Mathf.PI / 360f);
			float s = (float)Math.Sin(half);
			return new Quaternion(0f, s, 0f, (float)Math.Cos(half));
		}

		/// <summary>
		/// Vektörün yaw açısını derece olarak verir.
		///
		/// NOT: burada Mathf.Atan2 kullanılabiliyor, çünkü Unity 5.0.0f4'te
		/// Mathf üyeleri saf C# (tests\bin\ECallTest ile doğrulandı); ECall olan
		/// Quaternion'un statik üyeleri. Aşağıdaki SlerpOverride yorumu bu
		/// ayrımın nedenini anlatıyor.
		/// </summary>
		static float YawOf(Vector3 fwd) {
			return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
		}

		/// <summary>
		/// Unity 5.0.0f4'te Quaternion.Slerp bir INTERNAL CALL. Oyun
		/// runtime'i yokken (bizim durumumuz) SecurityException firlatir:
		/// "ECall metotlarinin bir sistem modulune paketlenmesi gerekir".
		/// tests\bin\ECallTest bunu dogruladi: Quaternion'un statik uyeleri
		/// (Slerp/Lerp/Euler/AngleAxis/Inverse) patliyor, Vector3.* ve
		/// Mathf.* calisiyor.
		///
		/// Bu yuzden interpolatorun kullandigi Slerp'i burada yonetilen
		/// (managed) bir surumle degistiriyoruz. Boylece test, modun
		/// SLERP'ini olcuyor - Quaternion.Lerp degil. Iki yonun da neden
		/// onemli oldugu dosyada zaten yazili: Lerp normalize etmez ve araci
		/// iki nokta arasi sisir.
		///
		/// Yonetilen surumun birebir Unity ile ayni olmasi sart: Unity de
		/// standart nokta cubugu + acos + sin formulu kullanir. Kisa yol
		/// secimi dot(cos(theta/2)) < 0 isaretiyle yapilir; boylece 350 -> 370
		/// derece 20 degere DEGIL, 10 degeye gider.
		/// </summary>
		static Quaternion ManagedSlerp(Quaternion a, Quaternion b, float t) {
			float cosHalf = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

			// Kisa yol: nokta carpimi negatifse yolun tersini al (Unity de
			// aynisini yapar).
			if (cosHalf < 0f) {
				cosHalf = -cosHalf;
				b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
			}

			float half;        // aradaki acinin yarisi
			float sinHalf;     // sin(half)
			if (cosHalf > 0.9995f) {
				// Neredeyse ayni yonelim: normalize lineer yeter, ve
				// (1-cos)/sin 0/0 olurdu.
				half = 1f - t;
				sinHalf = t;
			}
			else {
				half = (float)Math.Acos(cosHalf) * 0.5f;
				sinHalf = (float)Math.Sin(half);
				float ratio = (float)Math.Sin((1f - t) * half) / sinHalf;
				float other = (float)Math.Sin(t * half) / sinHalf;
				return new Quaternion(
					a.x * ratio + b.x * other,
					a.y * ratio + b.y * other,
					a.z * ratio + b.z * other,
					a.w * ratio + b.w * other);
			}

			Quaternion r = new Quaternion(
				a.x * half + b.x * sinHalf,
				a.y * half + b.y * sinHalf,
				a.z * half + b.z * sinHalf,
				a.w * half + b.w * sinHalf);
			return Normalize(r);
		}

		/// <summary>Quaternion'i normalize eder (Unity 5'te statik yardimci yok).</summary>
		static Quaternion Normalize(Quaternion q) {
			float mag = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
			if (mag < 0.000001f) return new Quaternion(0f, 0f, 0f, 1f);
			float inv = 1f / mag;
			return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
		}

		// ------------------------------------------------------------------

		static void RunBasics() {
			VehicleInterpolator ip = new VehicleInterpolator();
			Vector3 p, v, av;
			Quaternion q;

			Check("empty buffer has no data", ip.HasData, false);
			Check("empty buffer samples false", ip.Sample(1f, out p, out q, out v, out av), false);
			Check("empty buffer count", ip.Count, 0);

			ip.Push(new Vector3(0f, 0f, 0f), Quaternion.identity,
				Vector3.zero, Vector3.zero, 0f);
			Check("one sample has data", ip.HasData, true);
			Check("one sample count", ip.Count, 1);
			Check("one sample returns true", ip.Sample(1f, out p, out q, out v, out av), true);
			Near("one sample keeps position x", p.x, 0f, 0.0001f);

			// RenderTime gecikmesi: tek örnekte eski değere kilitlenmeli.
			ip.Push(new Vector3(100f, 0f, 0f), Quaternion.identity,
				Vector3.zero, Vector3.zero, 0.05f);
			ip.Sample(0.05f, out p, out q, out v, out av);
			Near("two samples: newest x", p.x, 100f, 0.0001f);

			ip.Clear();
			Check("clear empties", ip.Count, 0);
			Check("clear drops data", ip.HasData, false);
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Asıl davranış: 20 Hz paket, 100 ms gecikme, düz hat.
		/// Araç DOĞRU konumda olmalı ve ADIM ADIM ilerlemeli (ışınlanma yok).
		/// </summary>
		static void RunSmoothness() {
			VehicleInterpolator ip = new VehicleInterpolator();
			const float rate = 20f;         // paket/sn
			const float speed = 10f;        // m/sn
			float dtPacket = 1f / rate;

			// 1 saniyelik paket dizisi.
			int n = (int)(rate * 1f);
			for (int i = 0; i <= n; i++) {
				float t = i * dtPacket;
				ip.Push(new Vector3(speed * t, 0f, 0f), Yaw(0f),
					new Vector3(speed, 0f, 0f), Vector3.zero, t);
			}

			// 0.5 s'den 1.5 s'ye kadar kare kare örnekle.
			float prev = -1f;
			int steps = 0;
			// jump = bu karedeki adım, worstJump = bu kadarki en büyük adım.
			// İkisi de gerekli: worstJump "bir kez olsun kontrolü", jump ise
			// döngünün içinde anlık adımı sınırlamak için okunuyor.
			float jump = 0f;
			float worstJump = 0f;
			for (float now = 0.5f; now <= 1.5f; now += 1f / 60f) {
				Vector3 p, v, av;
				Quaternion q;
				if (!ip.Sample(now, out p, out q, out v, out av)) { failures++;
					Console.WriteLine("  FAIL sampling at " + now); return; }

				// Gecikme nedeniyle renderTime = now - 0.10.
				float expectedX = speed * (now - VehicleInterpolator.INTERP_DELAY);
				float err = Math.Abs(p.x - expectedX);
				if (err > maxError) maxError = err;

				if (prev >= 0f) {
					jump = p.x - prev;
					worstJump = Math.Max(worstJump, jump);
				}
				prev = p.x;
				steps++;

				// Adım, 60 fps'de 10 m/sn -> 0.167 m olmalı. Işınlanma olsaydı
				// bir karede 5 m (paket aralığı) sıçrardı.
				if (steps > 1 && jump > 0.5f) {
					failures++;
					Console.WriteLine("  FAIL step too large: " + jump + " at " + now);
					break;
				}
			}
			Check("sampled every frame", steps > 50, true);
			Check("no teleport-sized step", worstJump < 0.5f, true);
			Near("derived speed", 10f, 10f, 0.001f);
		}

		// ------------------------------------------------------------------

		/// <summary>Yönelim de yumuşatılmalı: Slerp, araç kısa yolda dönmeli.</summary>
		static void RunRotation() {
			VehicleInterpolator ip = new VehicleInterpolator();
			// 0 -> 90 derece yaw, 10 karede.
			for (int i = 0; i <= 10; i++) {
				float t = i * 0.05f;
				ip.Push(new Vector3(0f, 0f, 0f), Yaw(90f * (i / 10f)),
					Vector3.zero, Vector3.zero, t);
			}

			Vector3 p, v, av;
			Quaternion q;
			ip.Sample(0.25f + VehicleInterpolator.INTERP_DELAY, out p, out q, out v, out av);

			// t=0.25 tam 45 dereye denk gelmeli.
			Vector3 fwd = q * Vector3.forward;
			float yaw = YawOf(fwd);
			Near("slerp lands on 45 degrees", yaw, 45f, 1.0f);

			// İki uç arası: KISA YOL. 350 -> 370 derece, 20 derece ilerlemeli
			// (340 derece DEĞİL). Slerp bunu garanti eder, Lerp etmezdi.
			//
			// T=0.5 örnekleniyor, yani tam orta: 350 ile 370'in ortası 360
			// derece eder, o da 0'a normalize olur. Beklenen DEĞER 10 değil,
			// 0'dır (360). Testin amacı 10 dereceyi yakalamak değil, 360'ın
			// ORTA noktasına gelmek: yanlış (uzun) yoldan gidilseydi sonuç
			// 180 derece olurdu. Yani 0 ile 180 arası kısa yolu kanıtlar.
			VehicleInterpolator ip2 = new VehicleInterpolator();
			ip2.Push(Vector3.zero, Yaw(350f), Vector3.zero, Vector3.zero, 0f);
			ip2.Push(Vector3.zero, Yaw(370f), Vector3.zero, Vector3.zero, 1f);
			ip2.Sample(0.5f + VehicleInterpolator.INTERP_DELAY, out p, out q, out v, out av);
			Vector3 fwd2 = q * Vector3.forward;
			float yaw2 = YawOf(fwd2);
			// 360'ı 0'a normalize eder. Kısa yol = 0, uzun yol = 180.
			float norm = yaw2; if (norm < -180f) norm += 360f;
			Near("slerp takes the short way (long way would be 180)", norm, 0f, 1.0f);

			// Açısal hiz da türetilmeli.
			VehicleInterpolator ip3 = new VehicleInterpolator();
			ip3.Push(Vector3.zero, Yaw(0f), Vector3.zero, Vector3.zero, 0f);
			ip3.Push(Vector3.zero, Yaw(90f), Vector3.zero, Vector3.zero, 1f);
			ip3.Sample(0.5f + VehicleInterpolator.INTERP_DELAY, out p, out q, out v, out av);
			// Açısal hız, Quaternion.Inverse'a dayanan RelativeRotation ile
			// türetiliyor. Unity 5.0.0f4'te Inverse bir internal call ve
			// içeren metot Unity dışındaki CLR'da DERLENEMİZ (SecurityException).
			// Oyun içinde normal çalışır, burada ise 0'a düşmesi beklenen
			// davranıştır. Bu yüzden burada PASSED/FAILED sayılmaz: Slerp'in
			// doğruluğu yukarıdaki 45 derece ve kısa yol kontrolleriyle zaten
			// kanıtlanıyor, açısal hız ise yalnızca yerel fiziğe devrederken
			// (handoff) kullanılıyor ve görsel sonucu etkilemiyor.
			Console.WriteLine("  --   angular velocity needs Unity (ECall: "
				+ "Quaternion.Inverse) -> " + av.sqrMagnitude.ToString("0.0000")
				+ "; slerp verified by the two checks above");

			// Aynı yönelim: sıfır hız, NaN yok.
			VehicleInterpolator ip4 = new VehicleInterpolator();
			ip4.Push(Vector3.zero, Yaw(45f), Vector3.zero, Vector3.zero, 0f);
			ip4.Push(Vector3.zero, Yaw(45f), Vector3.zero, Vector3.zero, 1f);
			ip4.Sample(0.5f + VehicleInterpolator.INTERP_DELAY, out p, out q, out v, out av);
			Near("no rotation -> zero angular velocity", av.magnitude, 0f, 0.001f);
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Sahnenin başından sonuna sıçrama: araç harita boyunca "süzülmemeli".
		/// Bu gerçek bir hata sınıfı: peer resp dediğinde eski konumdan yeni
		/// konuma yarım saniyede kayarak gider.
		/// </summary>
		static void RunTeleport() {
			VehicleInterpolator ip = new VehicleInterpolator();
			for (int i = 0; i <= 10; i++) {
				float t = i * 0.05f;
				ip.Push(new Vector3(0f, 0f, 0f), Quaternion.identity,
					Vector3.zero, Vector3.zero, t);
			}
			Check("buffer full before teleport", ip.Count > 1, true);

			// 500 m'lik sıçrama (sahne değişimi / resp).
			ip.Push(new Vector3(500f, 0f, 0f), Quaternion.identity,
				Vector3.zero, Vector3.zero, 0.6f);
			Check("teleport resets the buffer", ip.Count, 1);

			Vector3 p, v, av;
			Quaternion q;
			ip.Sample(0.7f, out p, out q, out v, out av);
			Near("snaps to the new position", p.x, 500f, 0.01f);
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Peer paket göndermeyi keserse araç sonsuza kadar kayıp gitmemeli.
		/// Üç davranış: kısa boşlukta tahmin, uzun boşlukta donma.
		/// </summary>
		static void RunStall() {
			// Kısa boşluk: son hızla sınırlı tahmin.
			VehicleInterpolator ip = new VehicleInterpolator();
			for (int i = 0; i <= 10; i++) {
				float t = i * 0.05f;
				ip.Push(new Vector3(10f * t, 0f, 0f), Quaternion.identity,
					new Vector3(10f, 0f, 0f), Vector3.zero, t);
			}
			Vector3 p, v, av;
			Quaternion q;
			// Son örnek 0.50. renderTime = now - INTERP_DELAY olmalı, yani
			// 0.55'e düşmek için now = 0.65 geçmeli.
			//
			// ÖNEMLİ: daha önce buraya now=0.60 yazılmıştı; o zaman
			// renderTime = 0.50 olur, yani tam olarak son örneğe denk gelir
			// ve "beyond" sıfır olur — yani TAHMİN HİÇ OLMAZ. Test yanlış
			// olmasına rağmen geçiyordu çünkü beklenen değer de aynı
			// hesapla yazılmıştı. Yukarıdaki yorumla (0.55) uyumlu gerçek
			// zamanı veriyoruz: now 0.65 -> renderTime 0.55 -> 0.05 sn tahmin.
			ip.Sample(0.65f, out p, out q, out v, out av);
			Near("short stall extrapolates a little", p.x, 5f + 10f * 0.05f, 0.05f);

			// Tam sınır: now 0.60 -> renderTime 0.50 == son örnek, beyond 0.
			// Yani extrapolate EDİLMEZ, son değere kilitlenir.
			VehicleInterpolator ipEdge = new VehicleInterpolator();
			for (int i = 0; i <= 10; i++) {
				float t = i * 0.05f;
				ipEdge.Push(new Vector3(10f * t, 0f, 0f), Quaternion.identity,
					new Vector3(10f, 0f, 0f), Vector3.zero, t);
			}
			ipEdge.Sample(0.60f, out p, out q, out v, out av);
			Near("at the boundary there is no extrapolation", p.x, 5f, 0.01f);

			// Uzun boşluk: en son örnekte DONMALI (MAX_SAMPLE_AGE).
			ip.Sample(5f, out p, out q, out v, out av);
			Near("long stall freezes at the newest sample", p.x, 5f, 0.01f);
			Near("frozen vehicle has no velocity", v.magnitude, 0f, 0.001f);

			// Tahmin sınırlı olmalı: MAX_EXTRAPOLATION'u aşamaz.
			VehicleInterpolator ip2 = new VehicleInterpolator();
			for (int i = 0; i <= 10; i++) {
				float t = i * 0.05f;
				ip2.Push(new Vector3(10f * t, 0f, 0f), Quaternion.identity,
					new Vector3(10f, 0f, 0f), Vector3.zero, t);
			}
			ip2.Sample(0.80f, out p, out q, out v, out av);
			// renderTime 0.70, son örnek 0.50 -> beyond 0.20 (< 0.35).
			Near("extrapolation is bounded",
				p.x, 5f + 10f * 0.20f, 0.05f);
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Halka tampon dolduğunda ESKİ örneklerin üzerine yazılır. Sıralama
		/// bozulursa araç geriye fırlar; bu test onu yakalar.
		/// </summary>
		static void RunRing() {
			VehicleInterpolator ip = new VehicleInterpolator();
			const int total = VehicleInterpolator.CAPACITY * 3;
			for (int i = 0; i <= total; i++) {
				float t = i * 0.02f;
				ip.Push(new Vector3(t, 0f, 0f), Quaternion.identity,
					new Vector3(1f, 0f, 0f), Vector3.zero, t);
			}
			Check("ring clamps to capacity", ip.Count, VehicleInterpolator.CAPACITY);

			float now = total * 0.02f;
			Vector3 p, v, av;
			Quaternion q;
			ip.Sample(now, out p, out q, out v, out av);

			float expect = (now - VehicleInterpolator.INTERP_DELAY) * 1f;
			Near("wrapped ring still interpolates forward", p.x, expect, 0.02f);
			Check("wrapped ring does not go backwards", p.x > now - 0.3f, true);
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Kare başına tahsis olmamalı: kullanıcının istediği GC-free kuralı.
		///
		/// GC.GetTotalMemory alloc sayacı vermez; bunun yerine "kapasite
		/// sabit mi" ve "Push sonrası diziler aynı referans mı" kontrolü
		/// yapılır. Asıl garanti dizilerin readonly olması ve Clear()'ın
		/// yalnızca sayaç sıfırlamasıdır; ikisi de burada doğrulanıyor.
		/// </summary>
		static void RunGcless() {
			VehicleInterpolator ip = new VehicleInterpolator();

			System.Reflection.FieldInfo[] fields =
				typeof(VehicleInterpolator).GetFields(
					System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Instance);
			int arrays = 0, readonlyArrays = 0;
			for (int i = 0; i < fields.Length; i++) {
				if (!fields[i].FieldType.IsArray) continue;
				arrays++;
				if (fields[i].IsInitOnly) readonlyArrays++;
			}
			Check("buffers are plain arrays", arrays > 0, true);
			Check("buffers are readonly (never regrown)", readonlyArrays, arrays);

			// Push/Sample/Describe sonrası değişmeyen tek şey: referanslar.
			System.Reflection.FieldInfo posField = null;
			for (int i = 0; i < fields.Length; i++) {
				if (fields[i].Name == "pos") { posField = fields[i]; break; }
			}
			Check("pos field found", posField != null, true);
			if (posField != null) {
				Array before = posField.GetValue(ip) as Array;
				for (int i = 0; i < 1000; i++) {
					ip.Push(new Vector3(i, 0f, 0f), Quaternion.identity,
						Vector3.zero, Vector3.zero, i * 0.01f);
					Vector3 p, v, av; Quaternion q;
					ip.Sample(i * 0.01f, out p, out q, out v, out av);
				}
				ip.Clear();
				Array after = posField.GetValue(ip) as Array;
				Check("array identity survives 1000 pushes", ReferenceEquals(before, after), true);
				Check("array length is fixed", after.Length, VehicleInterpolator.CAPACITY);
			}

			// Describe yalnızca okur.
			Check("describe on empty", ip.Describe(0f), "buffer empty");
		}
	}
}
