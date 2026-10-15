using System;
using System.Collections.Generic;

namespace SteamMP {

	/// <summary>
	/// TEKRAR KATLAMA (deduplication): aynı mesajın arka arkaya gelmesini
	/// tek kayda indirger.
	///
	/// SORUN
	/// -----
	/// Log şişmesinin en yaygın sebebi tekrarlanan aynı mesajdır:
	///
	///     12:00:01 warn: part not found: strut fl(xxxxx)
	///     12:00:01 warn: part not found: strut fl(xxxxx)
	///     ... (20 Hz, 3 dakika = 3600 satır, hepsi aynı bilgi)
	///
	/// Bu satırları tekrarlamak teşhise hiçbir şey katmaz; hatta gerçek
	/// hatanın görülmesini engeller. "x3600" olarak yazılsaydı hem sıkışır hem
	/// de SAYI önemli bilgi olarak korunurdu.
	///
	/// KURAL
	/// -----
	///   * Hata (Error/Fatal) ASLA katlanmaz: her biri ayrı satırdır, çünkü
	///     zaman farkı ve yığın izi teşhis taşır.
	///   * Debug/Trace varsayılan olarak katlanır: bunlar teşhis satırlarıdır,
	///     "kaç kez oldu" sayısı yeterli bilgidir.
	///   * Info/Warn yalnızca <see cref="AlwaysFold"/> açıkken katlanır; kalıcı
	///     bilgi seviyesini varsayılan olarak katlamak, "bir şey oldu" kaydını
	///     görünmez yapardı.
	///
	/// GECİKME
	/// -------
	/// Katlanan kayıt HEMEN yazılmaz. Kanalın kendi satırı yazıldığında önce
	/// bekleyenler "xN" olarak boşaltılır, yani özet kaydın gerçekleştiği yerde
	/// görünür. O kanaldan bir daha hiçbir şey gelmezse
	/// <see cref="MaxIdleBeforeFlush"/> ms sonra boşaltılır; yani hiçbir bilgi
	/// kaybolmaz, yalnızca gecikir.
	/// </summary>
	public static class LogDedup {

		/// <summary>Pencere uzunluğu (ms). Yalnızca teşhis için.</summary>
		public const int WindowMs = 2000;

		/// <summary>
		/// Bir anahtarın "xN" özetine dönmesi için gereken sessizlik (ms).
		/// Olay bitince özet yazılır; kanal tamamen susarsa da yazılır.
		/// </summary>
		public const int MaxIdleBeforeFlush = 3000;

		/// <summary>İzlenen anahtar sayısı sınırı. Aşılırsa yeni anahtar izlenmez.</summary>
		public const int MaxKeys = 256;

		/// <summary>Info/Warn de katlansın mı? Varsayılan hayır.</summary>
		public static bool AlwaysFold;

		/// <summary>Toplam katlanan (bastırılan) kayıt sayısı.</summary>
		public static int Folded;

		/// <summary>Boşaltılan grup sayısı, yani yazılan "xN" satırı.</summary>
		public static int Groups;

		/// <summary>Anahtar tablosu taştığı için izlenemeyen anahtar sayısı.</summary>
		public static int Overflow;

		sealed class Entry {
			public int Count;
			public long LastAt;
			public LogSeverity Level;
			public string Channel;
			public string Scope;
			public string Event;
			public string Message;
			public string Fields;
		}

		static readonly Dictionary<string, Entry> pending
			= new Dictionary<string, Entry>(StringComparer.Ordinal);
		static long now;

		/// <summary>
		/// Zaman dışarıdan veriliyor: oyunda <see cref="LogEngine.Since"/>,
		/// testte elle. Böylece davranış saatin gerçek değerine bağlı olmadan
		/// doğrulanabiliyor.
		/// </summary>
		public static void SetClock(long ms) {
			now = ms;
			Drain();
		}

		/// <summary>Bu kayıt katlanabilir mi?</summary>
		static bool Foldable(LogSeverity level) {
			if (LogSeverities.IsCritical(level)) return false;      // hata: asla
			if (level >= LogSeverity.Debug) return true;            // teşhis: hep
			return AlwaysFold;                                    // info/warn: isteğe bağlı
		}

		/// <summary>
		/// Kayıt yazılacak mı? <c>false</c> dönse katlanmıştır; <paramref name="count"/>
		/// o anahtarda şu ana kadar kaç tekrarın birleştirildiğini bildirir
		/// (boşaltma sırasında kullanılır).
		/// </summary>
		public static bool ShouldWrite(LogSeverity level, string channel, string scope,
			string eventName, string message, out int count) {
			count = 0;
			if (!Foldable(level)) return true;

			string key = channel + "" + (int)level + "" + (message ?? "");
			lock (pending) {
				Entry e;
				if (!pending.TryGetValue(key, out e)) {
					if (pending.Count >= MaxKeys) { Overflow++; return true; }
					e = new Entry();
					e.Level = level;
					e.Channel = channel;
					e.Scope = scope;
					e.Event = eventName;
					e.Message = message;
					e.Count = 0;
					pending[key] = e;
				}
				e.LastAt = now;
				e.Count++;
				if (e.Count <= 1) return true;      // ilkini beklemeden yaz
				count = e.Count - 1;
				Folded++;
				return false;
			}
		}

		/// <summary>
		/// Sessizleşmiş anahtarları boşaltır. Kayıt yazımından ÖNCE çağrılır ki
		/// "xN" özeti, o kanaldaki son gerçek satırın hemen üstünde dursun.
		/// </summary>
		public static void Drain() {
			List<Entry> due = null;
			lock (pending) {
				if (pending.Count == 0) return;
				foreach (KeyValuePair<string, Entry> kv in pending) {
					Entry e = kv.Value;
					if (e.Count <= 1) continue;                              // yazıldı
					if ((now - e.LastAt) < MaxIdleBeforeFlush) continue;     // hâlâ tekrarlıyor
					if (due == null) due = new List<Entry>(8);
					due.Add(e);
					pending.Remove(kv.Key);
				}
			}
			if (due == null) return;
			for (int i = 0; i < due.Count; i++) {
				Entry e = due[i];
				Groups++;
				LogEngine.EmitFolded(e.Level, e.Channel, e.Scope, e.Event, e.Message,
					e.Fields, e.Count - 1);
			}
		}

		/// <summary>Bekleyen anahtar sayısı (teşhis çıktısı için).</summary>
		public static int PendingKeys {
			get { lock (pending) { return pending.Count; } }
		}

		/// <summary>Tabloyu ve sayaçları sıfırlar.</summary>
		public static void Reset() {
			lock (pending) { pending.Clear(); }
			Folded = 0;
			Groups = 0;
			Overflow = 0;
		}

		/// <summary>'smp log dedupe' çıktısı.</summary>
		public static string Describe() {
			return "dedupe: info/warn fold=" + (AlwaysFold ? "ON" : "off")
				+ " | folded=" + Folded
				+ " summaries=" + Groups
				+ " pending=" + PendingKeys
				+ (Overflow > 0 ? " untracked=" + Overflow : "")
				+ " (errors are never folded)";
		}
	}
}
