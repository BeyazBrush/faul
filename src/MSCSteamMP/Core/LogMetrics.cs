using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SteamMP {

	/// <summary>
	/// SAYAÇ ve SÜRE toplama. Log satırından AYRI tutuluyor çünkü sayılar
	/// satır biçiminden daha güvenilir bir kaynaktır: bir satır kaybolsa bile
	/// (katlama, döndürme, ekran kaydırma) sayaç doğru kalır.
	///
	/// NE İÇİN
	/// ------
	/// "Paket kaçıyor mu?" sorusu loga bakarak cevaplanmıyordu; sayım gerekiyordu
	/// ama sayımlar dağıtıktı (Telemetry, SmpTrace, PerfHud, SatsumaCoop).
	/// Burada tek bir sözlük var ve hepsi buraya yazıyor, dolayısıyla
	/// 'smp log stats' TEK bir tutarlı tablo basar.
	///
	/// TÜRLER
	/// ------
	///   counter  : artan sayı (paket gönderildi, hata oldu)
	///   timer    : süre ölçümü (adet, toplam, en küçük, en büyük, son)
	///   gauge    : anlık değer (FPS, ping, sahne nesne sayısı)
	///
	/// SINIRLAR
	/// -------
	/// Ada sayısı sınırlıdır. Sınırsız büyüyen bir sayaç sözlüğü, adı nesne
	/// yolu olan bir teşhis döngüsünde dakikalar içinde bellek yiyebilir; sınır
	/// aşıldığında yeni adlar <see cref="Dropped"/> olarak sayılır, yani
	/// sessizce kaybolmaz.
	/// </summary>
	public static class LogMetrics {

		/// <summary>Azami farklı ad sayısı.</summary>
		public const int MAX_NAMES = 256;

		/// <summary>Sınıra takılıp sayılmayan ölçüm sayısı.</summary>
		public static int Dropped;

		sealed class Stat {
			public long Count;
			public double Total;
			public double Min;
			public double Max;
			public double Last;
			public bool Timer;
		}

		static readonly Dictionary<string, Stat> stats
			= new Dictionary<string, Stat>(StringComparer.Ordinal);
		static readonly object gate = new object();

		/// <summary>Sayaç artır (yeni ad oluşturur).</summary>
		public static void Count(string name, long by) {
			Stat s = Bump(name, 0, false);
			if (s == null) return;
			lock (gate) { s.Count += by; }
		}

		/// <summary>Gösterge ayarla (FPS, ping, ...).</summary>
		public static void Gauge(string name, double value) {
			Stat s = Bump(name, 0, false);
			if (s == null) return;
			lock (gate) {
				s.Last = value;
				if (value > s.Max) s.Max = value;
				if (value < s.Min) s.Min = value;
			}
		}

		/// <summary>Süre ölçümü (ms). İstatistik toplar, satır YAZMAZ.</summary>
		public static void Time(string name, double ms) {
			Stat s = Bump(name, 0, true);
			if (s == null) return;
			lock (gate) {
				if (s.Count == 0 || ms < s.Min) s.Min = ms;
				if (ms > s.Max) s.Max = ms;
				s.Count++;
				s.Total += ms;
				s.Last = ms;
			}
		}

		static Stat Bump(string name, int pad, bool timer) {
			if (string.IsNullOrEmpty(name)) return null;
			if (name.Length > 40) name = name.Substring(0, 40);
			lock (gate) {
				Stat s;
				if (!stats.TryGetValue(name, out s)) {
					if (stats.Count >= MAX_NAMES) { Dropped++; return null; }
					s = new Stat();
					s.Timer = timer;
					s.Min = double.MaxValue;
					s.Max = double.MinValue;
					stats[name] = s;
				}
				return s;
			}
		}

		/// <summary>Bir sayacın değeri (yoksa 0).</summary>
		public static long Value(string name) {
			lock (gate) {
				Stat s;
				return stats.TryGetValue(name, out s) ? s.Count : 0L;
			}
		}

		/// <summary>Bir sürenin son ölçümü (yoksa 0).</summary>
		public static double Last(string name) {
			lock (gate) {
				Stat s;
				return stats.TryGetValue(name, out s) ? s.Last : 0d;
			}
		}

		/// <summary>Sıralı ad listesi (tanılama çıktısı için).</summary>
		public static string[] Names() {
			lock (gate) {
				List<string> names = new List<string>(stats.Keys);
				names.Sort(StringComparer.Ordinal);
				return names.ToArray();
			}
		}

		/// <summary>
		/// Tek satırlık tablo: "ad değer". Sayaçlar düz, süreler
		/// "n=.. avg=.. min=.. max=..", göstergeler son değer.
		/// </summary>
		public static string Row(string name) {
			lock (gate) {
				Stat s;
				if (!stats.TryGetValue(name, out s)) return name + " 0";
				if (s.Timer && s.Count > 0) {
					return name + " n=" + s.Count.ToString(CultureInfo.InvariantCulture)
						+ " avg=" + Fmt(s.Total / s.Count)
						+ " min=" + Fmt(s.Min)
						+ " max=" + Fmt(s.Max)
						+ " last=" + Fmt(s.Last);
				}
				if (s.Timer) return name + " (timer, no samples)";
				// Sayaç: yalnızca son gösterge değeri varsa onu da göster.
				if (s.Max != double.MinValue && s.Max != 0d) {
					return name + " " + s.Count.ToString(CultureInfo.InvariantCulture)
						+ " (now " + Fmt(s.Last) + ")";
				}
				return name + " " + s.Count.ToString(CultureInfo.InvariantCulture);
			}
		}

		static string Fmt(double v) {
			if (v == double.MaxValue || v == double.MinValue) return "-";
			if (v == Math.Floor(v) && v < 1e9 && v > -1e9)
				return ((long)v).ToString(CultureInfo.InvariantCulture);
			return v.ToString("0.##", CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Çok satırlık döküm. 'smp log stats' bunu basar, oturum sonunda da
		/// dosyaya yazılır — çökme sonrası "kaç şey kaç kez oldu" bilgisi
		/// dosyada kalır.
		/// </summary>
		public static string Dump() {
			StringBuilder sb = new StringBuilder(512);
			sb.Append("metrics (counters/timers/gauges):");
			string[] names = Names();
			if (names.Length == 0) {
				sb.Append(" none recorded");
			}
			for (int i = 0; i < names.Length; i++) {
				sb.Append('\n').Append("  ").Append(Row(names[i]));
			}
			if (Dropped > 0) {
				sb.Append("\n  (").Append(Dropped).Append(" measurement(s) dropped: name limit ").Append(MAX_NAMES).Append(" reached)");
			}
			return sb.ToString();
		}

		/// <summary>Tüm ölçümleri siler.</summary>
		public static void Reset() {
			lock (gate) { stats.Clear(); }
			Dropped = 0;
		}
	}

	/// <summary>
	/// SÜRE ÖLÇÜMÜ. <see cref="LogMetrics.Time"/> biriktirir; bu sınıf ek olarak
	/// eşiği aşan ölçümleri uyarı olarak SATIR YAZAR, çünkü "flush 47ms sürdü"
	/// bilgisi ancak satır olarak görünürse birini ilgilendirir.
	///
	/// Kullanım:
	///
	///     using (LogSpan.Begin(LogChannels.Net, "flush", 16.7f)) {
	///         FlushAll();
	///     }
	///
	/// eşik 0 ise satır hiç yazılmaz, yalnızca metrik toplanır (sıcak yollar).
	/// </summary>
	public sealed class LogSpan : IDisposable {

		readonly string channel;
		readonly string name;
		readonly float warnMs;
		readonly long started;
		bool disposed;

		/// <summary>Ölçüm başlatır. Eşik 0: satır yazma, yalnızca metrik.</summary>
		public static LogSpan Begin(string channel, string name, float warnMs) {
			return new LogSpan(channel, name, warnMs);
		}

		LogSpan(string channel, string name, float warnMs) {
			this.channel = channel;
			this.name = name;
			this.warnMs = warnMs;
			started = LogEngine.Since();
		}

		/// <summary>Geçen süre (ms).</summary>
		public double Elapsed {
			get { return (double)(LogEngine.Since() - started); }
		}

		public void Dispose() {
			if (disposed) return;
			disposed = true;
			double ms = Elapsed;
			try {
				LogMetrics.Time(channel + "." + name, ms);
				if (warnMs <= 0f) return;
				if (ms < warnMs) return;
				LogFields f = new LogFields().AddMs("took", ms).Add3("limit", warnMs);
				LogEngine.Warn(channel, "slow", name + " took longer than the budget", f);
			}
			catch { }
		}
	}
}
