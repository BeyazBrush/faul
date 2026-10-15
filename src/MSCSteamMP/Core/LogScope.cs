using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SteamMP {

	/// <summary>
	/// KAPSAM (scope): bir işlem boyunca yazan her satıra otomatik etiket.
	///
	/// SORUN
	/// -----
	/// Paket gönderimi 40 ayrı yerde oluşuyor. Hangisinin geciktiğini
	/// bulmak için "hangi fonksiyon çağırdı" bilgisi gerekiyor, o da pratikte
	/// yok: çağrı zinciri oyunun FSM'leri içine dağılıyor. Elle her satıra
	/// isim eklemek de 190 çağrı yerinde unutulacak bir şey.
	///
	/// ÇÖZÜM
	/// -----
	/// Çağıran bir blok açıyor, blok içindeki TÜM satırlar kapsam damgası
	/// alıyor:
	///
	///     using (LogScope.Push("send")) {
	///         LogEngine.Debug(Net, "packet", "bolt batch");
	///         // dosyada: 12:00:01.004 [debug] net [MSCSteamMP] send packet ...
	///     }
	///
	/// İç içe kapsamlar yol olarak birleşiyor ("init/send"). Bu, iki tarafın
	/// logunu satır satır eşleştirmede çok işe yarıyor: "o send, ben recv".
	///
	/// İŞ PARÇACIĞI
	/// -------------
	/// Kapsam yığını iş parçacığı başına tutulur. Aksi halde oyunun FSM iş
	/// parçacığından açılan kapsam, ana iş parçacığının satırlarına da
	/// bulaşırdı — yani "hangi işlem?" sorusunun cevabı yanlış olurdu.
	/// ThreadStatic bu yüzden şart; alan maliyeti (ThreadStatic erişimi
	/// görece pahalıdır) burada bir kez ödenir, okuma her kayıtta bir alan
	/// okumasıdır.
	/// </summary>
	public static class LogScope {

		[ThreadStatic]
		static List<string> stack;

		/// <summary>Etkin kapsam yolu ("a/b"), yoksa boş dize.</summary>
		public static string Current {
			get {
				List<string> s = stack;
				if (s == null || s.Count == 0) return "";
				StringBuilder sb = new StringBuilder(32);
				for (int i = 0; i < s.Count; i++) {
					if (i > 0) sb.Append('/');
					sb.Append(s[i]);
				}
				return sb.ToString();
			}
		}

		/// <summary>Etkin kapsam derinliği.</summary>
		public static int Depth {
			get { List<string> s = stack; return s == null ? 0 : s.Count; }
		}

		/// <summary>En fazla bu derinlik aşılır; fazlası yok sayılır.</summary>
		public const int MAX_DEPTH = 8;

		/// <summary>
		/// Kapsam açar. Boş veya çok uzun ad yok sayılır (isim kısıtı, hem
		/// dosya satırını okunur tutmak hem de alan adı kurallarıyla uyum için).
		/// </summary>
		public static IDisposable Push(string name) {
			if (string.IsNullOrEmpty(name)) return Scope.Empty;
			if (name.Length > 24) name = name.Substring(0, 24);
			List<string> s = stack;
			if (s == null) { s = new List<string>(4); stack = s; }
			if (s.Count >= MAX_DEPTH) return Scope.Empty;
			s.Add(name);
			return new Scope(s, name);
		}

		/// <summary>using için kapsam nesnesi.</summary>
		public sealed class Scope : IDisposable {

			/// <summary>Hiçbir şey yapmayan kapsam (geçersiz ad için).</summary>
			public static readonly Scope Empty = new Scope(null, null);

			readonly List<string> owner;
			readonly string name;
			bool disposed;

			internal Scope(List<string> owner, string name) {
				this.owner = owner;
				this.name = name;
			}

			/// <summary>
			/// Kapanma. Sıra dışı kapanmaya dayanıklı: en üstteki değil,
			/// kendi adımız aranıp ORAYA kadar olanlar atılır. Yanlış kapanma
			/// sırası (Fsm hook'larında erken dönüşler olağan) yığını bozmaz.
			/// </summary>
			public void Dispose() {
				if (disposed) return;
				disposed = true;
				if (owner == null || name == null) return;
				int at = owner.LastIndexOf(name);
				if (at < 0) return;                       // zaten kapanmış
				owner.RemoveRange(at, owner.Count - at);
			}
		}
	}

	/// <summary>
	/// İş parçacığı kimliği. Alanlara YALNIZCA iş parçacığı değiştiğinde yazılır:
	/// ana iş parçacığının kimliği 0'a çevrilir, böylece yüzlerce satıra aynı
	/// "tid=1" yazılmaz ve satırlar kısalır.
	///
	/// Neden gerekli: Unity'nin log dinleyicisi HER iş parçacığından gelir.
	/// Taşıma senkronu ana iş parçacığında, oyunun bazı çağrıları (Coroutine,
	/// Unity'nin kendi iş parçacıkları) başka yerde olabiliyor. "Hangi iş
	/// parçacığı yazdı" bilgisi teşhiste ayırt edici.
	/// </summary>
	public static class LogThread {

		static int main = -1;

		/// <summary>Ana iş parçacığının kimliği (genelde 1).</summary>
		public static int Main {
			get {
				if (main < 0) main = System.Threading.Thread.CurrentThread.ManagedThreadId;
				return main;
			}
		}

		/// <summary>Geçerli iş parçacığının kimliği; ana olan 0 döner.</summary>
		public static int Current {
			get {
				int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
				return id == main ? 0 : id;
			}
		}

		/// <summary>
		/// İş parçacığına okunabilir ad verir. Unity'nin kendi iş parçacıkları
		/// dışında bizimki adlandırılabilir; ad zaten varsa ya da bu çalışma
		/// zamanında mümkün değse sessizce geçilir (teşhis eklentisi, zorunlu
		/// adım değil).
		/// </summary>
		public static void NameCurrent(string name) {
			if (string.IsNullOrEmpty(name)) return;
			try { System.Threading.Thread.CurrentThread.Name = name; }
			catch { }
		}
	}
}
