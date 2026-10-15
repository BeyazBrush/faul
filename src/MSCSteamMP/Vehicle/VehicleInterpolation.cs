using System;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Uzaktan sürülen bir aracın konum/yönelimini yumuşatır.
	///
	/// SORUN
	/// ------
	/// Ağdan gelen her paket doğrudan Transform'a yazılıyordu:
	///
	///   gameObject.transform.position = p;
	///   gameObject.transform.rotation = q;
	///
	/// Sonuç: peer her paket gönderdiğinde araç O NOKTAYA IŞINLANIYOR.
	/// 20 Hz gönderimde saniyede 20 kez sıçrama = gözle görülür titreme
	/// (jitter). Kaynak kod bile bunu biliyordu: "expect jitter" uyarısı.
	///
	/// ÇÖZÜM
	/// ------
	/// Klasik "interpolasyon gecikmesi" (jitter buffer):
	///
	///   1. Gelen paketler ZAMAN DAMGALARIYLE bir halka tampona yazılır.
	///   2. Her kare, "gecikmiş" bir zamanda örnek alınır:
	///        renderTime = simdi - INTERP_DELAY
	///   3. renderTime'i saran iki an arasında Vector3.Lerp / Quaternion.Slerp.
	///
	/// Gecikme (varsayılan 100 ms) neden gerekli: paketler kaybolabilir ya da
	/// gecikmeyle gelir. İki örnek arasında kalırsak asla "geleceğe bakmak"
	/// zorunda kalmayız, bu yüzden takılma olmaz. 100 ms, 20 Hz gönderimde
	/// yaklaşık 2-3 paket demektir: hem yeterli yedek hem fark edilmez gecikme.
	///
	/// GC-FREE
	/// -------
	/// Kare başına HİÇBİR tahsis yok:
	///   * diziler kurucuda bir kez ayrılır, asla büyümez,
	///   * halka indeksi sayaçla yürür, LINQ/iteratör yok,
	///   * Vector3/Quaternion Unity'nin saf C# struct'larıdır (boxed değil),
	///   * Sample() dışarıya değil dönüşür (out struct = stack).
	/// Kuyruk boşaldığında Clear() sadece sayaçları sıfırlar, bellek serbest bırakmaz.
	///
	/// SINIRLAR VE GEREKLER
	/// -------------------
	///   * TELEPORT: İki ardışık örnek arasındaki mesafe SNAP_DISTANCE'ı aşarsa
	///     tampon temizlenir. Aksi hâlde araç haritanın bir ucundan diğerine
	///     "süzülür" (sahne değişimi, peer ölümü/resp, ızgara düzeltmesi).
	///   * DIŞA DOĞRU TAHMİN (extrapolation): Peer paket göndermeyi keserse
	///     tampon tükenir. O zaman son HIZLA ilerleriz, ama en fazla
	///     MAX_EXTRAPOLATION_MS boyunca. Sınırsız tahmin aracı duvara sokar.
	///   * HIZ: Ağdan gelen velocity doğrudan gövdeye yazılmaz; interpolasyon
	///     adımının SONUCU olarak yeniden hesaplanır. Böylece kinematik
	///     gövde ile görsel hareket çelişmez ve yerel fizik devralındığında
	///     handoff tutarlı olur.
	/// </summary>
	public class VehicleInterpolator {

		/// <summary>
		/// Blend'in kullandığı Slerp'in değiştirilebilir kopyası.
		///
		/// Üretimde NULL kalır (normal durum): çağıran taraf yalnızca null'ı
		/// kontrol edip Unity'nin native Quaternion.Slerp'ine düşer.
		/// tests\InterpProbe.cs bu alana yönetilen (managed) bir Slerp yazar,
		/// çünkü Unity 5.0.0f4'te Quaternion.Slerp bir internal call ve oyun
		/// dışındaki CLR'da çağrılamıyor. Testin "slerp en kısa yolu alır"
		/// iddiasını gerçekten ölçebilmesi için gereklidir.
		/// </summary>
		internal static Func<Quaternion, Quaternion, float, Quaternion> SlerpOverride;

		/// <summary>
		/// Tampon kapasitesi. 20 Hz'de 1 saniye = 20 örnek; 32 fazlasıyla yeter.
		/// Sabit bir sayı: çalışma anında büyüyen List YOK.
		/// </summary>
		public const int CAPACITY = 32;

		/// <summary>
		/// Kaç saniye geriden örnek alıyoruz.
		///
		/// 0.10 s = 100 ms. Paketler kaybolsa bile iki örnek arasında kalırız.
		/// Çok büyük değer (0.3+) aracın gözle gecikmesi olur; çok küçük
		/// değer (<0.05) paket kaybında takılma yapar.
		/// </summary>
		public const float INTERP_DELAY = 0.10f;

		/// <summary>
		/// Tamponun tek örnekte tutacağı en fazla zaman aralığı (saniye).
		///
		/// Peer'in gönderimi durursa tampon "eskiyor"; 0.5 sn'den sonra artık
		/// yeni örnek gelmiyor demektir ve interpolasyonun anlamı kalmaz,
		/// en son değere kilitlenir. Bu olmadan araç, yeni veri gelene kadar
		/// eski bir kareye doğru yavaşça geriler (sonsuz yavaşlama).
		/// </summary>
		public const float MAX_SAMPLE_AGE = 0.5f;

		/// <summary>
		/// Bu mesafenin üstündeki iki örnek "teleport" sayılır.
		///
		/// 25 m/saniye üstü bir sıçrama normal fizik değildir: sahne
		/// değişimi, peer resp'i veya ızgara düzeltmesi. Böyle bir adımda
		/// tampon boşaltılır, aracın harita boyunca süzülmesi engellenir.
		/// </summary>
		public const float SNAP_DISTANCE = 25f;

		/// <summary>
		/// Peer durduğunda en fazla bu kadar tahmin ederiz (saniye).
		///
		/// Son hızla ilerlemek, aracın bir duvara girmesindense iyidir; ama
		/// sınırsız olursa takılı kalan bir peer aracı saniyelerce
		/// kayıp götürür. 0.35 sn sonra durur ve hata sayılır.
		/// </summary>
		public const float MAX_EXTRAPOLATION = 0.35f;

		// ------------------------------------------------------------------
		// Sabit tampon (asla yeniden ayrılmaz)
		// ------------------------------------------------------------------

		readonly Vector3[] pos = new Vector3[CAPACITY];
		readonly Quaternion[] rot = new Quaternion[CAPACITY];
		readonly Vector3[] vel = new Vector3[CAPACITY];
		readonly Vector3[] angVel = new Vector3[CAPACITY];
		readonly float[] time = new float[CAPACITY];

		/// <summary>Doludur: kaç geçerli örnek var (CAPACITY'den büyük değil).</summary>
		public int Count { get { return count; } }

		/// <summary>En az bir örnek var mı?</summary>
		public bool HasData { get { return count > 0; } }

		int count;
		int head;      // bir sonraki yazılacak halka indeksi

		// ------------------------------------------------------------------
		// Yazma
		// ------------------------------------------------------------------

		/// <summary>
		/// Yeni bir ağ örneği ekler.
		///
		/// </summary>
		/// <param name="p">Konum.</param>
		/// <param name="q">Yönelim.</param>
		/// <param name="v">Doğrusal hız (ileride handoff için saklanır).</param>
		/// <param name="av">Açısal hız (aynı).</param>
		/// <param name="now">Yerel alış zamanı (Time.time).</param>
		public void Push(Vector3 p, Quaternion q, Vector3 v, Vector3 av, float now) {
			// Teleport tespiti: ardışık iki örnek arası makul sınırların dışında.
			if (count > 0) {
				int newest = (head - 1 + CAPACITY) % CAPACITY;
				if ((p - pos[newest]).sqrMagnitude > SNAP_DISTANCE * SNAP_DISTANCE) {
					// Harita boyunca süzülmesin diye geçmişi at.
					Clear();
				}
			}

			pos[head] = p;
			rot[head] = q;
			vel[head] = v;
			angVel[head] = av;
			time[head] = now;
			head = (head + 1) % CAPACITY;
			if (count < CAPACITY) count++;
		}

		/// <summary>Tamponu boşaltır (belleği serbest bırakmaz).</summary>
		public void Clear() {
			count = 0;
			head = 0;
		}

		/// <summary>
		/// Halkada kronolojik sırayla i. örneğin dizin indeksi.
		/// i = 0 en eski, i = Count-1 en yeni.
		/// </summary>
		int IndexAt(int i) {
			int start = head - count;
			if (start < 0) start += CAPACITY;
			return (start + i) % CAPACITY;
		}

		// ------------------------------------------------------------------
		// Okuma
		// ------------------------------------------------------------------

		/// <summary>
		/// Şimdiki zamana göre yumuşatılmış durumu hesaplar.
		///
		/// renderTime = now - INTERP_DELAY. İki örnek arasında kaldığımız için
		/// asla geleceğe bakmaya zorlanmayız; yalnızca tampon tükenirse son
		/// hızla sınırlı bir tahmin yapılır.
		///
		/// </summary>
		/// <param name="now">Time.time (yerel saat).</param>
		/// <param name="p">Sonuç konum.</param>
		/// <param name="q">Sonuç yönelim.</param>
		/// <param name="v">Sonuç hız (interpolasyon adımından türetilir).</param>
		/// <param name="av">Sonuç açısal hız.</param>
		/// <returns>Örnek alınabildiyse true.</returns>
		public bool Sample(float now, out Vector3 p, out Quaternion q,
			out Vector3 v, out Vector3 av) {
			p = Vector3.zero;
			q = Quaternion.identity;
			v = Vector3.zero;
			av = Vector3.zero;
			if (count == 0) return false;

			int newest = IndexAt(count - 1);
			float tNewest = time[newest];

			// Tek örnek: sadece onu döndür.
			if (count == 1) {
				p = pos[newest]; q = rot[newest]; v = vel[newest]; av = angVel[newest];
				return true;
			}

			int oldest = IndexAt(0);
			float tOldest = time[oldest];
			float renderTime = now - INTERP_DELAY;

			// (a) Peer gönderimi durmuş: en son örnek çok eski.
			//     Son değerde DONUYORUZ. Neden: sınırsız tahmin aracı bir
			//     duvara sokar, hatta ekran dışına uçurur. Dondurmak güvenlidir.
			if (now - tNewest > MAX_SAMPLE_AGE) {
				p = pos[newest]; q = rot[newest]; v = Vector3.zero; av = Vector3.zero;
				return true;
			}

			// (b) renderTime en yeni örnekten sonraya düşüyor: tampon yetersiz.
			//     Sınırlı dışa doğru tahmin (son hızla).
			if (renderTime >= tNewest) {
				float beyond = renderTime - tNewest;
				if (beyond > MAX_EXTRAPOLATION) beyond = MAX_EXTRAPOLATION;
				p = pos[newest] + vel[newest] * beyond;
				q = rot[newest];
				v = vel[newest];
				av = angVel[newest];
				return true;
			}

			// (c) renderTime tamponun başından önce: en eski örneği ver.
			if (renderTime <= tOldest) {
				p = pos[oldest]; q = rot[oldest]; v = vel[oldest]; av = angVel[oldest];
				return true;
			}

			// (d) Normal durum: renderTime'ı saran iki örneği bul.
			//     Kronolojik sırada ilerliyoruz: t0 <= renderTime <= t1.
			for (int i = 0; i < count - 1; i++) {
				int a = IndexAt(i);
				int b = IndexAt(i + 1);
				float ta = time[a], tb = time[b];
				if (renderTime >= ta && renderTime <= tb) {
					return Blend(a, b, ta, tb, renderTime, out p, out q, out v, out av);
				}
			}

			// Beklenmeyen: son savunma. En yeni örneği ver ki araç donmasın.
			p = pos[newest]; q = rot[newest]; v = vel[newest]; av = angVel[newest];
			return true;
		}

		/// <summary>
		/// Unity'nin native Slerp'i. Oyun içinde kullanılan yol budur.
		///
		/// NoInlining ZORUNLU: Unity 5.0.0f4'te Quaternion.Slerp bir internal
		/// call. Onu çağıran bir metod JIT tarafından derlenemez ve
		/// SecurityException fırlatır — çağrı yolu hiç çalıştırılmasa bile.
		/// Bu yüzden ECall referansı Blend'in gövdesinden ayrılmak zorunda;
		/// aynı sebepten yöntem inline da edilmemeli, aksi halde çağıran
		/// metoda geri gömülür ve aynı hata orada patlar.
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static Quaternion NativeSlerp(Quaternion a, Quaternion b, float t) {
			return Quaternion.Slerp(a, b, t);
		}

		/// <summary>İki örnek arasında karıştırma (Lerp + Slerp).</summary>
		bool Blend(int a, int b, float ta, float tb, float renderTime,
			out Vector3 p, out Quaternion q, out Vector3 v, out Vector3 av) {
			Vector3 p0 = pos[a], p1 = pos[b];
			Quaternion q0 = rot[a], q1 = rot[b];

			float span = tb - ta;
			// Aynı zaman damgası (aynı karede iki paket): bölme yerine son
			// değeri kullan.
			float t = span > 0.0001f ? (renderTime - ta) / span : 1f;
			if (t < 0f) t = 0f;
			else if (t > 1f) t = 1f;

			p = Vector3.Lerp(p0, p1, t);
			// Yönelimde SLERP şart: Quaternion.Lerp normalize etmez ve araç
			// iki nokta arasında şişip çöker.
			//
			// SlerpOverride: YALNIZCA testlerde dolar (tests\InterpProbe.cs).
			// Neden gerekli: Unity 5.0.0f4'te Quaternion'un statik uyeleri
			// (Slerp/Lerp/Euler/AngleAxis/Inverse) internal call, yani Unity
			// runtime'i yokken çağrıldıklarında SecurityException firlatıyorlar
			// ("ECall metotlarının bir sistem modülüne paketlenmesi gerekir").
			// Oyun içinde native oldukları için hızlı ve doğru çalışıyorlar.
			//
			// NEDEN AYRI METOD (native Slerp'e değil doğrudan burada değil):
			// internal call'a doğrudan referans veren bir metod, JIT tarafından
			// DERLENEMEZ; dal çalıştırılmasa bile SecurityException verir.
			// Yani test override'ı devreye girse bile aynı metodta ECall
			// referansı kaldığı için test yine patlar. Referansı ayrı, NoInlining
			// bir metoda alıyoruz: Blend testte derlenebiliyor, oyunda ise
			// çağrı yine doğrudan native Slerp'e gidiyor.
			Func<Quaternion, Quaternion, float, Quaternion> slerp = SlerpOverride;
			q = slerp != null ? slerp(q0, q1, t) : NativeSlerp(q0, q1, t);

			// Hız interpolasyon adımından türetilir; ağdan gelen velocity
			// doğrudan yazılmaz, çünkü kinematik gövdeyle çelişir ve
			// yerel fiziğe devrederken tutarsız handoff çıkar.
			float dt = span > 0.0001f ? span : 0.0001f;
			v = (p1 - p0) / dt;
			av = AngularPerSecond(q0, q1, dt);
			return true;
		}

		/// <summary>
		/// İki yönelim arasındaki açısal hız (Unity birimi/sn).
		///
		/// Quaternion'lar arasında Vector3.Cross ÇALIŞMAZ (iki Vector3 ister),
		/// bu yüzden göreli dönüş üzerinden eksen çıkarılıyor:
		///
		///   rel = to * Inverse(from)      -> from'dan to'ya olan dönüş
		///   yarı açı = atan2(|xyz|, w)
		///   eksen    = rel.xyz / |xyz|
		///
		/// w negatifse açı 2*PI'ye eklenir: daima EN KISA yol seçilir, yoksa araç
		/// 359 derece dolanıp 1 derece ilerlerdi.
		///
		/// Yalnızca yerel fiziğe devrederken (handoff) kullanılır; günlük
		/// interpolasyonda görsel sonuç etkisizdir çünkü gövde kinematiktir.
		/// </summary>
		static Vector3 AngularPerSecond(Quaternion from, Quaternion to, float dt) {
			if (dt <= 0.0001f) return Vector3.zero;
			// Neden try/catch yetmiyor, NoInlining gerekiyor:
			// Unity 5.0.0f4'te Quaternion.Inverse bir internal call. Onu
			// çağıran metod JIT tarafında DERLENIRKEN SecurityException
			// firlatir ("ECall metotlarının bir sistem modülüne paketlenmesi
			// gerekir") — try/catch bunu yakalayamaz, çünkü istisna çalışma
			// anında değil derleme anında oluşur. Bu yüzden ECall'i içeren
			// kısım ayrı bir NoInlining metoda alındı: Unity içinde çalışır,
			// Unity dışındaki test anacında o metoda hiç girilmez.
			Quaternion rel;
			try { rel = RelativeRotation(from, to); }
			catch { return Vector3.zero; }
			if (rel.w == 0f && rel.x == 0f && rel.y == 0f && rel.z == 0f) return Vector3.zero;
			float mag = Mathf.Sqrt(
				rel.x * rel.x + rel.y * rel.y + rel.z * rel.z);
			if (mag < 0.00001f) return Vector3.zero;
			float w = Mathf.Clamp(rel.w, -1f, 1f);
			float half = Mathf.Atan2(mag, w);
			if (half < 0f) half += 2f * Mathf.PI;   // en kisa yol
			Vector3 axis = new Vector3(rel.x, rel.y, rel.z) / mag;
			return axis * (2f * half / dt);
		}

		/// <summary>
		/// rel = to * Inverse(from)  -> from'dan to'ya olan dönüş.
		///
		/// Bu metodun TEK görevi Quaternion.Inverse çağrısını izole etmektir.
		/// Unity 5.0.0f4'te Quaternion.Inverse bir internal call; içeren metot
		/// JIT'te derlenemez (SecurityException). NoInlining ile ayrıldığı için
		/// AngularPerSecond derlenebilir, çağrı yalnızca oyun içinde yapılır.
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static Quaternion RelativeRotation(Quaternion from, Quaternion to) {
			return to * Quaternion.Inverse(from);
		}

		/// <summary>
		/// Teşhis: kaç örnek, ne kadar eski.
		///
		/// Log'a dökülür ('smp vehicle'), çünkü interpolasyonun doğru
		/// çalıştığını gözlemek zor: sorun görünmez, "araç biraz yumuşak
		/// gidiyor" gibi görünür. Sayılar yanlışsa en azından görünür.
		/// </summary>
		public string Describe(float now) {
			if (count == 0) return "buffer empty";
			int newest = IndexAt(count - 1);
			int oldest = IndexAt(0);
			return count + "/" + CAPACITY + " samples, oldest "
				+ (now - time[oldest]).ToString("0.000") + "s, newest "
				+ (now - time[newest]).ToString("0.000") + "s, delay "
				+ INTERP_DELAY.ToString("0.00") + "s";
		}
	}
}
