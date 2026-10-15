using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SteamMP {

	/// <summary>
	/// Kanal bazlı log yönetimi.
	///
	/// SORUN
	/// -----
	/// Tek bir global seviye iki işe yaramıyor. Oyuncu "ağ gecikmesi" sorununu
	/// ararken FSM gürültüsünü kapatmak istemez; "log çok şişti" derse paket
	/// sayaçlarını görmek ister. Global bir eşik ikisini de yapamaz.
	///
	/// ÇÖZÜM
	/// -----
	/// Her subsystem kendi kanalına sahip:
	///
	///   net       Steam taşıması, lobiler, el sıkışma, paket rotaları
	///   satsuma   parça/vida tabloları ve montaj senkronu
	///   vehicle   araç keşfi, transform, motor
	///   player    yerel ve uzak oyuncu durumu
	///   item      eşya alma/bırakma, elde tutulan
	///   world     saat, hava, dnya nesneleri (kapı, musluk)
	///   ui        menü, panel, komut satırı
	///   patch     Harmony yama uygulaması
	///   action    SmpTrace eylem günlüğü
	///   perf      kare süresi ölçümü
	///   peer      KARŞI TARAFTAN GELEN log satırları
	///   mod       varsayılan (kanal belirtmeyen çağrılar)
	///
	/// Kanal adı verilmemiş çağrılar <see cref="Default"/> kanalına düşer, yani
	/// mevcut 200+ çağrı yerinin hiçbiri değişmeden çalışmaya devam eder.
	///
	/// KALICI AYAR
	/// -------------
	/// Sevimeler dosyaya yazılır (mscmp.logcfg) ve bir sonraki açılışta okunur.
	/// Yani "smp log level net debug" yazıp kapatmak yerine, teşhis bittikten
	/// sonra da aynı ayar geçerli kalır; kullanıcı ayarı unutsa da mod hatırlamaz.
	/// </summary>
	public static class LogChannels {

		// ------------------------------------------------------------------
		// Sabit kanal adları
		// ------------------------------------------------------------------

		public const string Mod = "mod";
		public const string Net = "net";
		public const string Satsuma = "satsuma";
		public const string Vehicle = "vehicle";
		public const string Player = "player";
		public const string Item = "item";
		public const string World = "world";
		public const string Ui = "ui";
		public const string Patch = "patch";
		public const string Action = "action";
		public const string Perf = "perf";
		public const string Peer = "peer";
		public const string Game = "game";

		/// <summary>Kanal belirtilmemiş çağrıların düştüğü kanal.</summary>
		public const string Default = Mod;

		/// <summary>Ayar dosyasının adı (log dosyasının yanına).</summary>
		public const string CONFIG_NAME = "mscmp.logcfg";

		static readonly string[] Known = {
			Mod, Net, Satsuma, Vehicle, Player, Item, World,
			Ui, Patch, Action, Perf, Peer, Game,
		};

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		/// <summary>Bilinmeyen kanallar için eşik (bilinenler kendi eşiğini kullanır).</summary>
		public static LogSeverity Global = LogSeverities.DEFAULT;

		static readonly Dictionary<string, LogSeverity> overrides
			= new Dictionary<string, LogSeverity>(StringComparer.OrdinalIgnoreCase);

		static readonly Dictionary<string, int> counters
			= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		static readonly object gate = new object();

		/// <summary>
		/// Varsayılan eşikler. Kanalın kendine özgü bir eşiği yoksa
		/// <see cref="Global"/> kullanılır; buradaki değerler yalnızca başlangıç
		/// önerisidir ve dosyadan gelen ayar bunların üzerine yazar.
		/// </summary>
		public static LogSeverity DefaultFor(string channel) {
			if (string.IsNullOrEmpty(channel)) return Global;
			if (string.Equals(channel, Perf, StringComparison.OrdinalIgnoreCase))
				return LogSeverity.Off;          // kare süresi ölçümü opt-in
			if (string.Equals(channel, Peer, StringComparison.OrdinalIgnoreCase))
				return LogSeverity.Info;          // karşı tarafın logu her zaman ilgilidir
			if (string.Equals(channel, Action, StringComparison.OrdinalIgnoreCase))
				return LogSeverity.Info;          // SmpTrace kendi anahtarını yönetiyor
			return Global;
		}

		/// <summary>Kanalın etkin eşiği.</summary>
		public static LogSeverity LevelOf(string channel) {
			if (string.IsNullOrEmpty(channel)) return Global;
			lock (gate) {
				LogSeverity lvl;
				if (overrides.TryGetValue(channel, out lvl)) return lvl;
			}
			return DefaultFor(channel);
		}

		/// <summary>
		/// Kanal yeterince gürültülü mü? Kayıt yazılmadan ÖNCE sorulur; bu
		/// yüzden mesajı biçimlendirme maliyeti de elenir (sıcak yollarda
		/// string.Concat yapmamak önemli).
		/// </summary>
		public static bool Enabled(string channel, LogSeverity level) {
			if (level == LogSeverity.Off) return false;
			return LogSeverities.AtMost(level, LevelOf(channel));
		}

		/// <summary>Kanalın eşiğini değiştirir ve ayarı diske yazar.</summary>
		public static bool SetLevel(string channel, LogSeverity level, bool save) {
			if (string.IsNullOrEmpty(channel)) return false;
			lock (gate) {
				overrides[channel] = level;
			}
			if (save) Save();
			return true;
		}

		/// <summary>Global eşiği değiştirir (kanal özel eşiği olmayanlar için).</summary>
		public static void SetGlobal(LogSeverity level, bool save) {
			Global = level;
			if (save) Save();
		}

		/// <summary>Kanal sayacı: bu kanaldan kaç kayıt üretildi.</summary>
		public static void Count(string channel) {
			if (string.IsNullOrEmpty(channel)) return;
			lock (gate) {
				int n;
				counters.TryGetValue(channel, out n);
				counters[channel] = n + 1;
			}
		}

		/// <summary>Kanalın ürettiği kayıt sayısı (bu oturum).</summary>
		public static int CountOf(string channel) {
			lock (gate) {
				int n;
				return counters.TryGetValue(channel, out n) ? n : 0;
			}
		}

		/// <summary>Kanallar ve sayaçları, sıfırlanmadan önce okunur.</summary>
		public static string[] Names() {
			lock (gate) {
				List<string> names = new List<string>(counters.Keys);
				foreach (string k in overrides.Keys) {
					if (!names.Contains(k)) names.Add(k);
				}
				foreach (string k in Known) {
					if (!names.Contains(k)) names.Add(k);
				}
				names.Sort(StringComparer.Ordinal);
				return names.ToArray();
			}
		}

		// ------------------------------------------------------------------
		// Ayar metni: "net=debug,ui=off" / geri okuma
		// ------------------------------------------------------------------

		/// <summary>Ayarı tek satıra çevirir (kaydetme ve 'smp log level' çıktısı).</summary>
		public static string Spec() {
			lock (gate) {
				List<string> parts = new List<string>(overrides.Count);
				List<string> keys = new List<string>(overrides.Keys);
				keys.Sort(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < keys.Count; i++) {
					parts.Add(keys[i] + "=" + LogSeverities.Name(overrides[keys[i]]));
				}
				return parts.Count == 0 ? "" : string.Join(",", parts.ToArray());
			}
		}

		/// <summary>
		/// "net=debug, ui=warn, *=trace" biçimini uygular. "*" global eşiği,
		/// diğerleri kanal eşiğidir. Tanınmayan kanal sessizce eklenir: kullanıcı
		/// henüz yazılmamış bir modülü de açmak isteyebilir.
		/// </summary>
		public static bool ApplySpec(string spec) {
			if (string.IsNullOrEmpty(spec)) return false;
			string[] parts = spec.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
			int applied = 0;
			for (int i = 0; i < parts.Length; i++) {
				string p = parts[i].Trim();
				if (p.Length == 0) continue;
				int eq = p.IndexOf('=');
				if (eq < 0) {
					// "net debug" biçimi de kabul edilir; konsolda tırnak işi
					// gerekmesin diye.
					int sp = p.IndexOf(' ');
					if (sp <= 0) continue;
					eq = sp;
				}
				string name = p.Substring(0, eq).Trim();
				string value = p.Substring(eq + 1).Trim();
				if (name.Length == 0 || value.Length == 0) continue;
				if (name == "*") { Global = LogSeverities.Parse(value, Global); applied++; continue; }
				overrides[name] = LogSeverities.Parse(value, Global);
				applied++;
			}
			return applied > 0;
		}

		// ------------------------------------------------------------------
		// Kalıcılık
		// ------------------------------------------------------------------

		static string Directory() {
			try { return SmpLog.DllDirectory(); }
			catch { return ""; }
		}

		/// <summary>
		/// Ayarı diske yazar. Yazılamazsa sessiz geçilir: log ayarının
		/// kaydedilememesi oyunu etkilemez, sadece ayar hatırlanmaz.
		/// </summary>
		public static bool Save() {
			try {
				string dir = Directory();
				if (string.IsNullOrEmpty(dir)) return false;
				if (!System.IO.Directory.Exists(dir)) return false;
				System.IO.File.WriteAllText(System.IO.Path.Combine(dir, CONFIG_NAME),
					"global=" + LogSeverities.Name(Global) + "\n" + Spec() + "\n");
				return true;
			}
			catch { return false; }
		}

		/// <summary>Kayıtlı ayarı okur. Dosya yoksa sessizce varsayılan kalır.</summary>
		public static void Load() {
			try {
				string dir = Directory();
				if (string.IsNullOrEmpty(dir)) return;
				string path = System.IO.Path.Combine(dir, CONFIG_NAME);
				if (!System.IO.File.Exists(path)) return;
				string[] lines = System.IO.File.ReadAllLines(path);
				for (int i = 0; i < lines.Length; i++) {
					string line = lines[i];
					if (line == null) continue;
					line = line.Trim();
					if (line.Length == 0 || line[0] == '#') continue;
					int eq = line.IndexOf('=');
					if (eq <= 0) continue;
					string name = line.Substring(0, eq).Trim();
					string value = line.Substring(eq + 1).Trim();
					if (name == "global") Global = LogSeverities.Parse(value, Global);
					else overrides[name] = LogSeverities.Parse(value, Global);
				}
			}
			catch { }
		}

		// ------------------------------------------------------------------
		// Görünüm
		// ------------------------------------------------------------------

		/// <summary>'smp log level' çıktısı: kanal, eşik, bu oturumdaki kayıt sayısı.</summary>
		public static string Describe() {
			StringBuilder sb = new StringBuilder(256);
			sb.Append("log levels: global=").Append(LogSeverities.Name(Global))
			  .Append("  (a channel with its own setting overrides the global one)");
			string[] names = Names();
			for (int i = 0; i < names.Length; i++) {
				string n = names[i];
				int c = CountOf(n);
				if (c == 0 && !HasOverride(n)) continue;      // sessiz kanalları yorma
				sb.Append("\n  ").Append(Pad(n)).Append(' ')
				  .Append(LogSeverities.Padded(LevelOf(n))).Append(' ')
				  .Append(c.ToString(CultureInfo.InvariantCulture).PadLeft(6))
				  .Append(c == 1 ? " record" : " records");
			}
			return sb.ToString();
		}

		static bool HasOverride(string channel) {
			lock (gate) { return overrides.ContainsKey(channel); }
		}

		static string Pad(string s) {
			return s.Length >= 8 ? s : s.PadRight(8);
		}

		/// <summary>Tüm kanal sayaçlarını sıfırlar (ayar korunur).</summary>
		public static void ResetCounters() {
			lock (gate) { counters.Clear(); }
		}
	}
}
