using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MSCLoader;

namespace SteamMP {

	/// <summary>Log seviyeleri. Konsörün rengi ve dosyadaki etiket bunlardan gelir.</summary>
	public enum LogKind { Info, State, Warn, Error }

	/// <summary>
	/// Modun tek log kapısı.
	///
	/// Bu sınıf ARTIK ince bir yüzdür: bütün kararlar <see cref="LogEngine"/> ve
	/// onun altındaki modüllerdedir (LogChannels, LogDedup, LogFile, LogMetrics,
	/// LogScope, LogRemote). Buradaki her metot eski imzayı koruyor, yani 200'den
	/// fazla çağrı yerinin hiçbiri değişmeden çalışır; yeni yetenekler kanal /
	/// alan / kapsam taşıyan YENİ metotlarla açılır.
	///
	/// Neden kendi sınıfımız
	/// -------------------
	/// dnSpy ile MSCLoader.ModConsole açıldı ve görüldü ki Print/Log şunu yapıyor:
	///
	///     if (ModConsole.console != null) ModConsole.console.controller.AppendLogLine(str);
	///     Console.WriteLine("MSCLoader Message: " + Regex.Replace(str, "&lt;.*?&gt;", ""));
	///
	/// Yani Debug.Log çağrısı YOK. Application.logMessageReceived dinleyerek logları
	/// yakalamak mümkün değil; yakalansaydı bile o noktaya kadar olan her şey
	/// MSCLoader'ın konsolüne gittiği için istediğimiz dosyaya düşmezdi. Warning ve
	/// Error ayrıca ModConsole.console.SetVisibility(true) çağırdığı için kullanıcının
	/// konsolü sürekli zorla açılıyordu.
	///
	/// ÇIKIŞ NOKTALARI
	/// ---------------
	///   1. mscmp.txt          kalıcı, iki oyuncu arasında paylaşılabilir,
	///                         N nesil döndürmeli, satır başına disk yazmıyor
	///                         (kare başına boşaltma), oturum başlığı/sonu içerir
	///   2. ekran konsörü       " tuşu, filtre kutusu, kanal rozeti, ölçüm satırı
	///   3. karşı taraf aynası  isteğe bağlı, peer kanalı (LogRemote)
	///
	/// Dosya neden ekleniyordu
	/// ----------------------
	/// Ekran konsörü bir anda kaybolur: "oturum takıldı" gibi bir sorunu karşı
	/// taraftan teşhis etmek için ekran görüntüsü yetmiyor. Ortak bir dosya, iki
	/// oyuncunun da aynı hatayı dosyadan okumasını sağlar.
	///
	/// Dosya kuralı
	/// ------------
	/// Oturum başına üstüne yazılmıyor, ekleniyor: oyun bir kere çökerse bir
	/// sonraki açılışta önceki çökme kaydı kaybolmamalı. 2 MB aşılınca
	/// mscmp.1.txt'ye, o da aşılınca .2'ye kaydırılır (LogFile.Generations).
	/// </summary>
	public static class SmpLog {

		/// <summary>Hedef dosya adı.</summary>
		public const string FILE_NAME = "mscmp.txt";

		/// <summary>
		/// Dönüş dosyası artık numaralandırılmış (mscmp.1.txt ...). Bu ad geriye
		/// dönük uyum için duruyor; <see cref="Files"/> tam listeyi verir.
		/// </summary>
		public const string OLD_NAME = "mscmp.1.txt";

		/// <summary>Log klasörü. Oyunun Mods klasörü.</summary>
		public static string Directory = @"D:\Steam\steamapps\common\My Summer Car\Mods";

		/// <summary>Dosya 2 MB'ı aşarsa döndürülür.</summary>
		public const long MAX_BYTES = LogFile.MaxBytes;

		/// <summary>Ekran konsöründe tutulan satır sayısı.</summary>
		public const int CAPACITY = LogEngine.Capacity;

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		static readonly object gate = new object();

		/// <summary>
		/// Dosya yazımı anahtarı. Kapatıldığında hiçbir şey diske inmez ama
		/// konsör çalışmaya devam eder ("sadece ekranda görmek istiyorum").
		/// </summary>
		static bool enabled = true;

		/// <summary>Init tamamlandı mı (dosya açıldı)?</summary>
		static bool initDone;

		/// <summary>
		/// Init öncesi dosya satırları.
		///
		/// Neden gerekiyor: ModSetup, SmpLog.Init'ten ÖNCE yazıyor ("yukleniyor",
		/// yama sonuçları). Dosya henüz açılmadığı için bu satırlar biriktirilir
		/// ve Init'te dosyaya yazılır. Canlı kanıt: soğuk açılışta yama raporu
		/// dosyada görünmüyordu.
		/// </summary>
		static readonly List<string> backlog = new List<string>(64);

		/// <summary>Init öncesi en fazla bu kadar satır tutulur.</summary>
		const int MAX_BACKLOG = 200;

		/// <summary>Dosya yazılamıyorsa neden? Konsör başlığında gösterilir.</summary>
		public static string Problem { get { return LogFile.Problem; } }

		/// <summary>Logların yazıldığı dosyanın tam yolu, ya da neden yazılamadığı.</summary>
		public static string Path { get { return LogFile.Path; } }

		/// <summary>Toplam yazılan satır (buffer taşmadan önceki toplam).</summary>
		public static int Total { get { return LogEngine.Total; } }

		// ------------------------------------------------------------------
		// Kurulum
		// ------------------------------------------------------------------

		/// <summary>Klasörü hazırlar, dosyayı açar, oturum başlığını yazar.</summary>
		public static void Init() {
			lock (gate) {
				if (initDone) return;
				string dir = Directory;
				// Sabit D: yolu bu makinede yoksa (başka disk/kurulum) DLL'nin
				// bulunduğu klasöre düşülür: log HER ZAMAN dll'nin yanına yazılır
				// (canlı kanıt: D: olmayan PC'de dosya hiç oluşmuyordu, F3
				// konsörü çalışıyordu).
				if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) {
					dir = DllDirectory();
				}
				if (string.IsNullOrEmpty(dir)) dir = FallbackDirectory();
				Directory = dir;
				initDone = true;
			}

			LogEngine.Init();

			// Init öncesi biriken satırlar (örn. ModSetup'un yama raporu) dosyaya
			// yazılır. Konsöre ZATEN yazıldılar, o yüzden burada yalnızca dosya
			// yazılır.
			lock (gate) {
				for (int i = 0; i < backlog.Count; i++) {
					LogFile.Write(backlog[i], true);
				}
				backlog.Clear();
			}

			// Header must go through Write so it reaches the file and the console too.
			Write(LogKind.State, ModEntry.MOD_VERSION + " baslatildi. Log: " + Path);
		}

		/// <summary>
		/// DLL'nin bulunduğu klasör (oyunun Mods klasörü). Boş dönerse "".
		/// GameLogCapture SmpLog.Init'ten ÖNCE çalıştığı için herkese açık:
		/// o da aynı çözümü kullanır.
		/// </summary>
		public static string DllDirectory() {
			try {
				string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
				if (string.IsNullOrEmpty(loc)) return "";
				string dir = System.IO.Path.GetDirectoryName(loc);
				return dir ?? "";
			}
			catch { return ""; }
		}

		/// <summary>
		/// Oyunun veri klasörü. Belirtilen Mods yolu bu makinede yoksa oraya düşer.
		///
		/// NoInlining zorunlu: <c>Application.persistentDataPath</c> bir ECall'dir
		/// ve inline edilirse istisna buradaki try/catch'i değil çağıranı vurur
		/// (bkz. LogFile.UnityDescription).
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static string FallbackDirectory() {
			try {
				string data = UnityEngine.Application.persistentDataPath;
				if (!string.IsNullOrEmpty(data)) return System.IO.Path.Combine(data, "Mods");
			}
			catch { }
			return ".";
		}

		static string Now() {
			return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
		}

		static string Stamp() {
			return LogEngine.Stamp();
		}

		// ------------------------------------------------------------------
		// Yazma — eski imza (kanal yok, alan yok)
		// ------------------------------------------------------------------

		public static void Print(string message) { Write(LogKind.Info, message); }

		public static void Log(string message) { Write(LogKind.Info, message); }

		public static void Warn(string message) { Write(LogKind.Warn, message); }

		public static void Warning(string message) { Write(LogKind.Warn, message); }

		public static void Error(string message) { Write(LogKind.Error, message); }

		/// <summary>Durum değişimi: bağlandı, ayrıldı, sahne değişti.</summary>
		public static void State(string message) { Write(LogKind.State, message); }

		/// <summary>
		/// Ham mesaj. Varsayılan kanala (<c>mod</c>) yazılır.
		///
		/// [MSCSteamMP] öneki motor tarafından eklenir, çağrı yerlerinde
		/// yazılmaz; böylece 200 çağrı yerinden biri unutsa bile log etiketsiz
		/// kalmaz.
		/// </summary>
		public static void Write(LogKind level, string message) {
			if (!enabled) return;
			if (message == null) return;
			// Ciddiyet rozetten türetiliyor. Sabit "info" yazılırsa hata ve
			// uyarı satırları tamponlanır ve kritik oldukları için gerektiği gibi
			// anında diske de inmezler.
			WriteChannel(LogSeverities.FromKind(level), level, LogChannels.Default, null, message, null);
		}

		// ------------------------------------------------------------------
		// Yazma — kanal / alan / olay etiketli (yeni yetenekler)
		// ------------------------------------------------------------------

		/// <summary>Belirli bir kanala yaz.</summary>
		public static void WriteChannel(LogSeverity level, LogKind kind, string channel,
			string eventName, string message, LogFields fields) {
			if (!enabled) return;
			if (message == null) return;
			// State bir görünüm kararı, ciddiyet değil: hep Info eşiğinde
			// durur (ama kritik sayılır, yani dosyaya gecikmez).
			if (kind == LogKind.State) level = LogSeverity.Info;
			LogEngine.Write(level, kind, channel, eventName, message, fields);
		}

		/// <summary>Kanal + olay etiketi + alanlar (yeni biçimin en çok kullanılan hali).</summary>
		public static void Write(string channel, string eventName, string message, LogFields fields) {
			WriteChannel(LogSeverity.Info, LogKind.Info, channel, eventName, message, fields);
		}

		/// <summary>Teşhis satırı (Debug). Kanalın eşiği kapalıysa hiç çalışmaz.</summary>
		public static void Debug(string channel, string eventName, string message) {
			WriteChannel(LogSeverity.Debug, LogKind.Info, channel, eventName, message, null);
		}

		public static void Debug(string channel, string eventName, string message, LogFields fields) {
			WriteChannel(LogSeverity.Debug, LogKind.Info, channel, eventName, message, fields);
		}

		/// <summary>En ayrıntılı seviye. Kare başına yüzlerce satır üretebilir.</summary>
		public static void Trace(string channel, string eventName, string message) {
			WriteChannel(LogSeverity.Trace, LogKind.Info, channel, eventName, message, null);
		}

		/// <summary>Kanal etiketli uyarı.</summary>
		public static void WarnChannel(string channel, string message) {
			WriteChannel(LogSeverity.Warn, LogKind.Warn, channel, null, message, null);
		}

		public static void WarnChannel(string channel, string eventName, string message, LogFields fields) {
			WriteChannel(LogSeverity.Warn, LogKind.Warn, channel, eventName, message, fields);
		}

		/// <summary>Kanal etiketli hata.</summary>
		public static void ErrorChannel(string channel, string message) {
			WriteChannel(LogSeverity.Error, LogKind.Error, channel, null, message, null);
		}

		public static void ErrorChannel(string channel, string eventName, string message, LogFields fields) {
			WriteChannel(LogSeverity.Error, LogKind.Error, channel, eventName, message, fields);
		}

		/// <summary>Durum değişimi (kanal etiketli).</summary>
		public static void StateChannel(string channel, string message) {
			WriteChannel(LogSeverity.Info, LogKind.State, channel, null, message, null);
		}

		/// <summary>
		/// İstisnayı kök nedene kadar açıp loglar (yığın izi dahil).
		/// Harmony'nin sarmaladığı istisnalarda asıl sebebi gösterir.
		/// </summary>
		public static void Exception(string channel, string context, Exception e) {
			LogEngine.Exception(channel, context, e);
		}

		// ------------------------------------------------------------------
		// Okuma (konsör)
		// ------------------------------------------------------------------

		/// <summary>Ekranda tutulan satır sayısı.</summary>
		public static int LineCount { get { return LogEngine.LineCount; } }

		/// <summary>
		/// 0 = en eski tutulan satır. Kapasite taştıysa en eskiler düşmüş olur.
		/// </summary>
		public static void GetLine(int index, out string stamp, out LogKind level, out string text) {
			LogRecord rec;
			if (!LogEngine.TryGet(index, out rec)) {
				stamp = ""; level = LogKind.Info; text = ""; return;
			}
			stamp = LogRecord.TimeOf(rec.Since);
			level = rec.Kind;
			text = rec.FormatConsole();
		}

		/// <summary>Kaydın tamamı (konsör satır çizimi ve sorgular için).</summary>
		public static bool TryGetRecord(int index, out LogRecord record) {
			return LogEngine.TryGet(index, out record);
		}

		/// <summary>
		/// Kaydın ölçülmüş yüksekliği. 0 dönerse konsör henüz ölçmemiştir.
		///
		/// Satır BİR KEZ ölçülür ve sonucu kayda yazılır: her kare 800 kez
		/// CalcHeight çağırmak, sarma hesabı yapması ve 80 metrelik bir hata
		/// mesajının her karede yeniden ölçülmesi anlamına gelirdi.
		/// </summary>
		public static float GetHeight(int index) { return LogEngine.Height(index); }

		/// <summary>Konsörün ölçtüğü yüksekliği kaydeder (bkz. GetHeight).</summary>
		public static void SetHeight(int index, float height) { LogEngine.SetHeight(index, height); }

		/// <summary>Konsörde kısa etiket (rozet).</summary>
		public static string LevelName(LogKind level) {
			switch (level) {
				case LogKind.Warn: return "WARN";
				case LogKind.Error: return "ERR";
				case LogKind.State: return "STATE";
				default: return "LOG";
			}
		}

		// ------------------------------------------------------------------
		// Dosya yaşam döngüsü
		// ------------------------------------------------------------------

		/// <summary>
		/// Kare başına bir kez: dosya tamponunu boşaltır, sessizleşmiş
		/// tekrarları özetler, ayna kuyruğunu gönderir.
		/// </summary>
		public static void Pump() {
			try { LogFile.Pump(); } catch { }
			try { LogDedup.SetClock(LogEngine.Since()); } catch { }
			try { LogRemote.Pump(); } catch { }
		}

		/// <summary>Dosyayı şimdi boşaltır (kritik olay öncesi).</summary>
		public static void Flush() {
			try { LogFile.Flush(); } catch { }
		}

		/// <summary>
		/// Dosyayı kapatır. Sonraki Write çağrıları yeniden açmaz; Init
		/// çağrılmalıdır (Restart bunu yapar).
		///
		/// Neden gerekli: açık bir yazıcı dosyayı tutmaya devam eder, bu yüzden
		/// log dosyası "başka bir programda açık" olmadan okunamıyor ya da
		/// silinemiyor. Oyun kapanırken ve testlerde çağrılır.
		/// </summary>
		public static void Close() {
			lock (gate) { initDone = false; }
			try { LogEngine.Shutdown(); } catch { }
		}

		/// <summary>Konsörü ve dosyayı sıfırdan başlatır ('smp log clear').</summary>
		public static void Restart() {
			lock (gate) {
				initDone = false;
				backlog.Clear();
			}
			LogEngine.ResetCounters();
			Init();
		}

		/// <summary>
		/// Dosya yazımını aç/kapat. Kapatıldığında konsör ve sayaçlar çalışır,
		/// kalıcı dosya yazılmaz.
		/// </summary>
		public static void SetEnabled(bool value) {
			enabled = value;
			if (!value) { try { LogFile.Close(); } catch { } return; }
			if (!initDone) Init();
		}

		public static bool Enabled { get { return enabled; } }

		/// <summary>Dosya yazımı gerçekten açık mı (izin/disk sorunu olabilir)?</summary>
		public static bool FileOpen { get { return LogFile.Enabled; } }

		// ------------------------------------------------------------------
		// Teşhis
		// ------------------------------------------------------------------

		/// <summary>Log sisteminin sağlık özeti ('smp log health').</summary>
		public static string Health() { return LogEngine.Health(); }

		/// <summary>Tüm log dosyalarının yolları (döndürme nesilleri dahil).</summary>
		public static string[] Files() { return LogFile.Files(); }

		/// <summary>
		/// Artık MSCLoader konsolüne GİRİŞ YOK.
		///
		/// Kullanıcı isteği: "tüm loglar MSCLoader'ın konsoluna yazılıyor, engelle."
		///
		/// dnSpy ile açılan MSCLoader kaynağındaki durum:
		///
		///     public static void Print(string str) {
		///         if (ModConsole.console != null)
		///             ModConsole.console.controller.AppendLogLine(str);   // konsol ekranı
		///         Console.WriteLine("MSCLoader Message: " + ...);          // output_log.txt
		///     }
		///
		/// Yani Print bile konsol EKRANINA yazıyor; Warning ve Error ayrıca
		/// SetVisibility(true) çağırdığı için oyuncunun konsolünü zorla açıyordu.
		/// Mod artık ModConsole'u hiç çağırmıyor.
		///
		/// Peki oyunun ve MSCLoader'ın kendi mesajları ne olacak? Onlar bizim
		/// dosyalarımıza GameLogCapture ile gidiyor (Console.SetOut ve
		/// Application.logMessageReceivedThreaded). Yani hiçbir mesaj kaybolmuyor;
		/// yalnızca ekranı kaplayan konsoldan çıkıyor.
		/// </summary>
		static void MirrorToLoader(LogKind level, string full) { }
	}
}
