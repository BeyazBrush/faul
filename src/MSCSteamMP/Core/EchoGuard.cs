using System;
using MSCLoader;

namespace SteamMP {

	/// <summary>
	/// Yankı koruması (echo guard): ağdan gelen bir değeri oyunun kendi FSM'ine yazdığımızda,
	/// o yazma kendi karşılığını yeniden göndermesin diye yapay zekâyı susturan sayaç.
	///
	/// Neden sayaç, neden bool değil
	/// ----------------------------
	/// ASSEMBLE olayını oynatmak, karşı tarafta kendi FSM'ini çalıştırır ve o FSM
	/// Installed, Bolted ve Tightness değerlerini sırayla yazar. Her biri için ayrı bir
	/// " bastım, şimdi bastım" bayrağı koysak, ilk iç içe yazma bayrağı kaldırır ve
	/// geri kalan yazmalar ağa geri gönderilirdi. Sayaç bunu çözer: her giriş artırır,
	/// en içteki Dispose azaltır, susturma ancak en dışta biter.
	///
	/// Kapsam
	/// ------
	/// Tek bir global sayaçtır: bütün modüller (Satsuma, araç, eşya, saat/hava) aynı yolu
	/// kullanır. Aksi hâlde bir modülün uyguladığı değer diğerini tetiklerdi.
	///
	/// struct Scope
	/// ------------
	/// Scope bir struct'tir, yani başka bir metoda değer olarak geçirilip iki kez Dispose
	/// edilirse sayaç yanlışlıkla eksiye gider. Bu yüzden:
	///   * kapsam yalnızca yerel değişken olarak kullanılır (using / try-finally),
	///   * her kopyanın kendi "aktif" bayrağı vardır, Dispose ikinci kez çağrılsa da
	///     sayacı bir kez azaltır,
	///   * sayaç yine de eksiye düşerse 0'a sabitlenir ve bir kez uyarı basılır: sessiz
	///     bir kayma, sonsuza kadar açık kalan bir susturmadan çok daha kötüdür.
	///
	/// Performans: Enter/Exit yalnızca tam sayı artırma/azaltmadır. Update içinde
	/// yalnızca <see cref="Suppressed"/> okunur ve bu da ilk bool alan okumasıdır.
	/// </summary>
	public static class EchoGuard {

		/// <summary>Kaç kapsal şu anda açık. Hata ayıklama ve testler için açık tutulur.</summary>
		public static int Depth { get { return depth; } }

		/// <summary>
		/// Harmony hook'larının ilk okuduğu şey. Yazma yakalandığında "bu bizim mi uyguladığımız
		/// bir değer?" sorusunun cevabıdır: modüller (Satsuma, araç, eşya, saat) hepsi aynı
		/// sayacı okur, böylece birinin uyguladığı değer diğerinin yakalayıcısını tetiklemez.
		/// </summary>
		public static bool Suppressed { get { return depth > 0; } }

		static int depth;
		static bool underflowWarned;

		/// <summary>Susturmayı başlatır. Çağıran, Scope'u Dispose etmek zorundadır.</summary>
		public static Scope Enter() {
			depth++;
			return new Scope();
		}

		/// <summary>Susturmayı bitirir. Kapsam dışı çağrılarda sayacı 0'a sabitler.</summary>
		public static void Exit() {
			if (depth <= 0) {
				depth = 0;
				if (underflowWarned) return;
				underflowWarned = true;
				SmpLog.Warning("echo guard underflow: a scope was disposed twice. "
					+ "Capture is disabled until the next scene load.");
				return;
			}
			depth--;
		}

		/// <summary>Sayacı sıfırlar. Dünya değişiminde çağrılır.</summary>
		public static void Reset() {
			depth = 0;
			underflowWarned = false;
		}

		/// <summary>try/finally ile kullanılacak kapsayıcı. Kopyalanmamalıdır.</summary>
		public struct Scope : IDisposable {
			bool active;

			/// <summary>Kapsamı açar (EchoGuard.Enter tarafından çağrılır).</summary>
			public Scope() {
				active = true;
			}

			/// <summary>Kapsamı kapatır. Aynı kopya üzerinde ikinci çağrı etkisizdir.</summary>
			public void Dispose() {
				if (!active) return;
				active = false;
				EchoGuard.Exit();
			}
		}
	}
}