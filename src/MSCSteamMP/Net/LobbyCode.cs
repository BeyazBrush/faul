using System;
using System.Text;
using MSCLoader;

namespace SteamMP {

	/// <summary>
	/// Lobi katılım şifresi: üretimi, karşılaştırması ve hız sınırlama.
	///
	/// Ne olduğunu açıkça söylemek gerekiyor: bu kod bir kriptografik sır değil, bir
	/// oturum kapısıdır. Steam P2P trafiği düz metindir ve Steam lobisi Steam
	/// arkadaşlarına listelenir; yani şifre "yanlışlıkla giren biri" ile "kasten giren
	/// biri"yi ayırmak içindir, saldırganı durdurmak için değil. Buna karşılık:
	///
	///   * Doğrulama el sıkışmada yapılır, yani oturum kurulmadan önce. Kabul
	///     edilmeyen oturum hiç açılmaz.
	///   * Kod 3 kez yanlış girilirse oturum düşürülür; bu, çevrimiçi tahmin
	///     hızını saniyede on binlerden saniyede üçe indirir.
	///   * Karşılaştırma sabit zamanlıdır (constant time). Klasik string karşılaştırma
	///     ilk farklı karakterde çıkar ve zamanlama farkıyla karakterler tek tek
	///     tahmin edilebilir. Buradaki döngü kodun tamamı boyunca çalışır ve karar
	///     yalnızca sonunda verilir.
	///
	/// Alfabeye karışabilir karakterler (0/O, 1/l/I) konulmadı: kod ekranda ve sesli
	/// okunduğunda bunları ayırmak imkansız ve kullanıcı hatası kaynağıdır.
	/// </summary>
	public static class LobbyCode {

		/// <summary>Üretilen kodun uzunluğu. 6 karakter, 55+ alfabe ile ~5.7e10 olasılık.</summary>
		public const int LENGTH = 6;

		/// <summary>Kaç yanlış denemeden sonra oturum düşürülür.</summary>
		public const int MAX_FAILURES = 3;

		/// <summary>
		/// Karışması kolay karakterler çıkarıldı. 32 karakter: A-Z (I ve O hariç) + a-z
		/// (l ve o hariç) + 2-9 (0 ve 1 hariç). Fazla büyük harf, rakam ya da işaret yok.
		/// </summary>
		public const string ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ"   // 24
										  + "abcdefghijkmnpqrstuvwxyz"    // 22
										  + "23456789";                     //  8  = 54

		/// <summary>Bu oturumun şifresi. Host'ta üretilir, Client'ta kullanıcı girer.</summary>
		public static string Current { get { return current; } }
		static string current = string.Empty;

		/// <summary>Yanlış deneme sayısı. Sıfırlandığında oturum reddedilir.</summary>
		public static int Failures { get { return failures; } }
		static int failures;

		/// <summary>Reddedilen oturumlarda sıfırlanır.</summary>
		public static void ResetFailures() { failures = 0; }

		/// <summary>Reddedilen oturumda sayacı artırır, sınırı aşarsa oturumu düşürür.</summary>
		public static void RegisterFailure() {
			failures++;
			if (failures >= MAX_FAILURES) {
				SmpLog.Error("lobby code rejected " + failures + " times, dropping the session.");
				SteamTransport.Leave();
				ResetFailures();
			}
		}

		/// <summary>
		/// Rastgele kod üretir.
		///
		/// UnityEngine.Random yerine System.Security.Cryptography.RandomNumberGenerator
		/// kullanılıyor: Unity'nin RNG'si tahmin edilebilir bir tohumdan (seed) gelir ve
		/// aynı tohum iki oyuncuya aynı kodu verirse kimse fark etmez. Burada tek bir
		/// statik RNG örneği tutulur; çağrı başına yeni oluşturmak GC baskısı yaratırdı.
		/// </summary>
		public static string Generate() {
			EnsureRng();
			StringBuilder sb = new StringBuilder(LENGTH);
			for (int i = 0; i < LENGTH; i++) {
				sb.Append(ALPHABET[NextIndex(ALPHABET.Length)]);
			}
			current = sb.ToString();
			ResetFailures();
			SmpLog.Print("lobby code: " + current);
			return current;
		}

		/// <summary>Kullanıcının girdiği kodu normalize eder (küçük harf, boşluk kesme).</summary>
		public static string Normalize(string input) {
			if (string.IsNullOrEmpty(input)) return string.Empty;
			StringBuilder sb = new StringBuilder(input.Length);
			for (int i = 0; i < input.Length; i++) {
				char c = input[i];
				if (c == ' ') continue;
				sb.Append(char.ToLowerInvariant(c));
			}
			return sb.ToString();
		}

		/// <summary>
		/// Girilen kod geçerli mi?
		///
		/// Uzunluk ve karakter kümesi önce denetlenir: geçersiz karakterli bir giriş
		/// karşılaştırma yapılmadan reddedilir, çünkü normalize edilmiş kod alfabe
		/// dışındaysa zaten geçemez. Karşılaştırma sabit zamanlıdır (aşağıda).
		/// </summary>
		public static bool Verify(string hostCode, string entered) {
			if (string.IsNullOrEmpty(hostCode)) return false;

			// İki taraf da normalize edilir. Bu, kodu büyük/küçük harf duyarsız yapar
			// ve aynı zamanda bir tuş hatasının sessizce başarısız olmasını engeller:
			// üretilen kod büyük harf içerebiliyor, kullanıcı ise küçük harf yazıyor;
			// yalnızca girilen taraf normalize edilirse geçerli kod reddedilirdi.
			string expected = Normalize(hostCode);
			string candidate = Normalize(entered);

			if (candidate.Length != LENGTH) return false;
			for (int i = 0; i < candidate.Length; i++) {
				if (ALPHABET.IndexOf(candidate[i]) < 0) return false;
			}
			for (int i = 0; i < expected.Length; i++) {
				if (ALPHABET.IndexOf(expected[i]) < 0) return false;
			}
			return FixedTimeEquals(expected, candidate);
		}

		/// <summary>
		/// Sabit zamanlı karşılaştırma.
		///
		/// string.Equals erken çıkış yapar ve bu, kod uzunluğunu değil ama hangi
		/// karakterin ilk kez tuttuğunu zamanlama üzerinden ele verir. Burada fark her
		/// adımda bir bool'a toplanır, karar ancak tüm karakterler işlendikten sonra
		/// verilir. İki kod farklı uzunluktaysa sonuç zaten yanlıştır ama yine de
		/// sonuna kadar gezilir.
		/// </summary>
		static bool FixedTimeEquals(string a, string b) {
			int n = Math.Max(a == null ? 0 : a.Length, b == null ? 0 : b.Length);
			int diff = (a == null ? 0 : a.Length) ^ (b == null ? 0 : b.Length);
			for (int i = 0; i < n; i++) {
				char ca = (a != null && i < a.Length) ? a[i] : '\0';
				char cb = (b != null && i < b.Length) ? b[i] : '\0';
				diff |= ca ^ cb;
			}
			return diff == 0;
		}

		// ------------------------------------------------------------------
		// RNG: tek örnek, çağrı başına tahsis yok
		// ------------------------------------------------------------------

		static System.Security.Cryptography.RandomNumberGenerator rng;

		static void EnsureRng() {
			// Unity 5 / Mono 2.5 profilinde RandomNumberGenerator.Create() mevcuttur.
			// Yine de oluşturulamazsa (bazı Mono build'leri) alttaki yedek yola düşülür.
			if (rng != null) return;
			try { rng = System.Security.Cryptography.RandomNumberGenerator.Create(); }
			catch { rng = null; }
		}

		/// <summary>
		/// [0, max) aralığında tek bir tam sayı.
		///
		/// Modülo yanılgısını (hane seçimi) önlemek için reddederek seçilir:
		/// 54 bir alfabe 256'ya bölümünde 4.7 hanelik pay oluşur; yeniden denemek
		/// yerine tam sayı atıp baştan başlamak doğru ve tek seferde biten bir yoldur.
		///
		/// CANLI HATA (düzeltildi): kod dört baytı tek bir 32 bit tamsayıya
		/// birleştirip `value < limit` ile karşılaştırıyordu, ama `limit`
		/// BAYT aralığı için hesaplanmıştı (`256 - 256 % max`, max=54 için 216).
		/// Kabul olasılığı 216 / 2^32 ≈ 5e-8 idi, yani karakter başına ~20
		/// milyon `GetBytes` çağrısı; altı karakterlik bir kod için ~120 milyon.
		/// Kripto RNG bulunduğunda (Mono'da normal yol bu) `Generate()` ana
		/// thread'de pratikte SONSUZ döngüye giriyordu ve davet kodu üretimi
		/// oyunu donduruyordu. `int.MinValue` koruması da bu yüzden anlamsızdı.
		///
		/// Düzeltme: örnekleme `limit`'in tanımlı olduğu aralıkta yapılır,
		/// yani BİR bayt. Oran max=54 için ~%84, beklenen ~1.19 tur.
		/// </summary>
		static int NextIndex(int max) {
			if (max <= 0) return 0;
			EnsureRng();
			if (rng == null) return FallbackIndex(max);

			byte[] buffer = new byte[4];
			int limit = 256 - (256 % max);   // 256'nın max'a bölümünden küçük kısmını ele
			while (true) {
				rng.GetBytes(buffer);
				int value = buffer[0];
				if (value < limit) return value % max;
			}
		}

		/// <summary>
		/// RNG oluşturulamadığında yedek yol.
		///
		/// Güvenlik açısından zayıf olduğu bilinir ve ModConsole ile bildirilir: oyun
		/// yine de çalışır, ama kod tahmin edilebilir hâle gelir. Sessizce düşmemek,
		/// kullanıcıyı yanlış güvenlik izleniminde bırakmaktan iyidir.
		/// </summary>
		static int FallbackIndex(int max) {
			if (!warnedAboutFallback) {
				warnedAboutFallback = true;
				SmpLog.Warning("cryptographic RNG unavailable; the lobby code "
					+ "is generated from a weaker source and is not safe against guessing.");
			}
			return UnityEngine.Random.Range(0, max);
		}

		static bool warnedAboutFallback;
	}
}