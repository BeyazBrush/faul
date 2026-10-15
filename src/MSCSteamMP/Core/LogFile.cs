using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SteamMP {

	/// <summary>
	/// Kalıcı log dosyası: döndürme, tampon, oturum başlığı/sonu, disk koruması.
	///
	/// ESKİ DAVRANIŞ VE NEDEN DEĞİŞTİ
	/// ------------------------------
	/// Önceden her satır doğrudan <c>StreamWriter</c>'a gidiyordu ve
	/// <c>AutoFlush = true</c> idi. Bu, log SATIR BAŞINA bir disk yazma
	/// sistem çağrısı demek. Oyun 300 FPS'ye çıktığında saniyede yüzlerce
	/// satır yazan modda bu, kare süresine ölçülebilir biçimde bulaşıyordu.
	///
	/// Şimdi:
	///   * <see cref="Debug"/> ve <see cref="Trace"/> satırları tamponlanır ve
	///     kare başına en fazla <see cref="MaxBytesPerPump"/> kadar boşaltılır.
	///   * Warn/Error/Fatal/State YAZILMAZDAN GEÇMEZ: bunlar diskte olmalı,
	///     çünkü işlenmeden çöken bir oyunda son bilgi bunlardır.
	///   * Kritik olmayan satırların kaybı, tamponun kendisi zaten dosyaya
	///     yazılmış olduğu için yalnızca "son kare"de görülür: çökme tam
	///     kare sınırında olursa o kare teşhis satırlarını kaybeder, hata
	///     satırlarını kaybetmez.
	///
	/// DÖNDÜRME
	/// --------
	/// Eski sürümde tek yedek vardı (mscmp.old.txt) ve yedek de büyüyebiliyordu.
	/// Şimdi N nesil tutulur: mscmp.txt, mscmp.1.txt, ... mscmp.{N-1}.txt.
	/// Nesil sayısı <see cref="Generations"/>; en eskisi silinir. Bu, "dünkü
	/// oturumun logu" sorusunun cevabını 3-4 gün geriye kadar verir.
	///
	/// DİSK KORUMASI
	/// -------------
	/// Log yazmak diski doldurabilir. Açılışta boş alan kontrol edilir;
	/// eşiğin altındaysa dosya yazımı KAPATILIR ve konsöre neden yazıldığı
	/// bildirilir. Kullanıcı "log dosyası neden boş" diye sormak yerine
	/// ekranda görüyor.
	/// </summary>
	public static class LogFile {

		/// <summary>Ana dosya adı.</summary>
		public const string Name = "mscmp.txt";

		/// <summary>Dosya başına azami boyut (byte) — döndürme eşiği.</summary>
		public const long MaxBytes = 2L * 1024L * 1024L;

		/// <summary>Kaç nesil tutulur (1 = sadece ana dosya).</summary>
		public const int Generations = 4;

		/// <summary>Kare başına boşaltılacak azami byte.</summary>
		public const int MaxBytesPerPump = 32 * 1024;

		/// <summary>Boş alan bu altındaysa dosya yazımı kapanır.</summary>
		public const long MinFreeBytes = 8L * 1024L * 1024L;

		static readonly object gate = new object();
		static string directory;
		static string path;
		static StreamWriter writer;
		static bool broken;
		static string problem = "";
		static long written;

		// Tampon: sıraya alınmış, henüz diske inmemiş satırlar.
		static readonly Queue<string> pending = new Queue<string>(256);
		static int pendingBytes;

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		/// <summary>
		/// Dosyanın tam yolu.
		///
		/// BİLEREK DÜZ YOL: bu değer dosyayı açmak için kullanılıyor, o yüzden
		/// durum eki karıştırılmıyor. Durumu ayrı alanlarda durur
		/// (<see cref="Enabled"/>, <see cref="Problem"/>, <see cref="Describe"/>).
		/// </summary>
		public static string Path {
			get {
				lock (gate) {
					if (broken) return "(not written: " + problem + ")";
					return path ?? "(not started)";
				}
			}
		}

		/// <summary>
		/// Yolu durumuyla birlikte verir: konsör başlığı ve 'smp log path'
		/// bunu gösterir, çünkü "dosya neden boş" sorusunun cevabı burada.
		/// </summary>
		public static string Describe() {
			lock (gate) {
				string where = path ?? "(not started)";
				if (broken) return "NOT WRITTEN (" + problem + ") " + where;
				if (writer == null) return "closed " + where;
				return "open " + where + "  (" + written + " byte)";
			}
		}

		/// <summary>
		/// Neden yazılmıyor. Dosya kapalıysa da dolu bir metin döner; aksi
		/// halde konsör başlığı sessizce yalan söyler ve kullanıcı "log
		/// dosyam neden boş" diye sormak zorunda kalır.
		/// </summary>
		public static string Problem {
			get {
				lock (gate) {
					if (broken) return problem;
					if (writer == null && !string.IsNullOrEmpty(path)) return "closed";
					return "";
				}
			}
		}

		/// <summary>Bu oturumda diske inen byte sayısı.</summary>
		public static long Written { get { lock (gate) { return written; } } }

		/// <summary>Tamponda bekleyen satır sayısı.</summary>
		public static int PendingLines {
			get { lock (gate) { return pending.Count; } }
		}

		/// <summary>Dosya yazımı açık mı (konsör açık olsa da kapalı olabilir)?</summary>
		public static bool Enabled {
			get { lock (gate) { return writer != null; } }
		}

		// ------------------------------------------------------------------
		// Açma / kapatma
		// ------------------------------------------------------------------

		/// <summary>
		/// Dosyayı açar ve oturum başlığını yazar. Klasör verilmezse
		/// <see cref="SmpLog.DllDirectory"/> kullanılır (log daima DLL'nin yanında).
		/// </summary>
		public static bool Open(string dir) {
			lock (gate) {
				if (writer != null) return true;
				try {
					directory = !string.IsNullOrEmpty(dir) ? dir : SmpLog.DllDirectory();
					if (string.IsNullOrEmpty(directory)) {
						broken = true;
						problem = "no writable directory found";
						return false;
					}
					if (!System.IO.Directory.Exists(directory))
						System.IO.Directory.CreateDirectory(directory);

					if (!EnoughDisk(directory)) {
						broken = true;
						problem = "less than " + (MinFreeBytes / (1024 * 1024)) + " MB free on that drive";
						path = System.IO.Path.Combine(directory, Name);
						return false;
					}

					path = System.IO.Path.Combine(directory, Name);
					Rotate(path);

					writer = new StreamWriter(new FileStream(path, FileMode.Append,
						FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
					broken = false;
					problem = "";
					written = 0;
					return true;
				}
				catch (Exception e) {
					broken = true;
					problem = e.GetType().Name + ": " + e.Message;
					writer = null;
					return false;
				}
			}
		}

		/// <summary>
		/// Boş alan kontrolü. DriveInfo o sürücüde çalışmıyorsa (ağ sürücüsü,
		/// USB'de takılı değil) kontrol atlanır — yoksa "yazamıyorum" der ve
		/// yazma da engellenirdi.
		/// </summary>
		static bool EnoughDisk(string dir) {
			try {
				string root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dir));
				if (string.IsNullOrEmpty(root)) return true;
				DriveInfo d = new DriveInfo(root);
				if (!d.IsReady) return true;
				return d.AvailableFreeSpace >= MinFreeBytes;
			}
			catch { return true; }
		}

		/// <summary>
		/// Nesil kaydırma. Ana dosya eşiği aştıysa:
		///   mscmp.{N-1}.txt silinir, geriye doğru bir kaydırılır, ana dosya
		///   en eski yere taşınır. Dosya adları numaralandığı için tarih
		///   sırası kaydırmada bozulmaz.
		/// </summary>
		static void Rotate(string current) {
			try {
				FileInfo fi = new FileInfo(current);
				if (!fi.Exists || fi.Length < MaxBytes) return;

				string oldest = current + "." + (Generations - 1);
				if (File.Exists(oldest)) File.Delete(oldest);
				for (int i = Generations - 2; i >= 1; i--) {
					string from = current + "." + i;
					if (!File.Exists(from)) continue;
					string to = current + "." + (i + 1);
					if (File.Exists(to)) File.Delete(to);
					File.Move(from, to);
				}
				File.Move(current, current + ".1");
			}
			catch { }
		}

		/// <summary>Dosyayı kapatır (tampon boşaltılarak).</summary>
		public static void Close() {
			lock (gate) {
				FlushLocked();
				if (writer == null) return;
				try { writer.Flush(); writer.Close(); } catch { }
				writer = null;
			}
		}

		// ------------------------------------------------------------------
		// Yazma
		// ------------------------------------------------------------------

		/// <summary>
		/// Satır ekler. <paramref name="critical"/> satırı doğrudan diske yazar;
		/// aksi halde tampona alınır.
		/// </summary>
		public static void Write(string line, bool critical) {
			lock (gate) {
				if (writer == null) {
					// Dosya açılmadan önceki satırlar: kaybolmamaları için
					// SmpLog tarafında tutuluyor, buraya gelmezler.
					return;
				}
				if (critical) {
					FlushLocked();
					WriteLocked(line);
					return;
				}
				pending.Enqueue(line);
				pendingBytes += line.Length + 2;
				// Tampon sınırsız büyürse bellek şişer; biriken veriyi zorla
				// yazmak, sınırı aşmaktan iyidir.
				if (pendingBytes > MaxBytesPerPump * 4) FlushLocked();
			}
		}

		/// <summary>Tamponu diske boşaltır (kare başına bir kez).</summary>
		public static void Pump() {
			lock (gate) {
				if (writer == null || pending.Count == 0) return;
				int budget = MaxBytesPerPump;
				while (pending.Count > 0 && budget > 0) {
					string line = pending.Peek();
					if (line.Length + 2 > budget && budget < MaxBytesPerPump) break;
					pending.Dequeue();
					budget -= line.Length + 2;
					WriteLocked(line);
				}
				try { writer.Flush(); } catch { }
			}
		}

		/// <summary>Yazıcıyı zorla boşaltır (kritik olay öncesi, kapanışta).</summary>
		public static void Flush() {
			lock (gate) { FlushLocked(); }
		}

		static void FlushLocked() {
			if (writer == null) return;
			while (pending.Count > 0) {
				string line = pending.Dequeue();
				WriteLocked(line);
			}
			pendingBytes = 0;
			try { writer.Flush(); } catch { }
		}

		static void WriteLocked(string line) {
			try {
				writer.WriteLine(line);
				written += line.Length + 2;
			}
			catch (Exception e) {
				broken = true;
				problem = e.GetType().Name;
				writer = null;
			}
		}

		// ------------------------------------------------------------------
		// Başlık / sonu
		// ------------------------------------------------------------------

		/// <summary>
		/// Oturum başlığı: sürüm, protokol, ortam. "Hangi build, hangi makinede"
		/// sorusunun cevabı iki oyuncu arasında log karşılaştırırken gerekli;
		/// canlı log kanıtı: "iki taraf farklı build mi koşuyor?" sorusu hep
		/// dosyanın en başına bakmayı gerektiriyordu.
		///
		/// HER ALAN KENDİ try/catch'İ İÇİNDE: Unity'nin ECall'ları (bkz.
		/// UnityDescription) masaüstü test JIT'i tarafından derlenemiyor ve
		/// istisna metot GİRİŞİNDE atılıyor, yani o metodun kendi try/catch'i
		/// işe yaramıyor. Yakalayıcı burada, çağıran tarafta olmalı; tek bir
		/// alanın düşmesi başlığın tamamını düşürmüyor.
		/// </summary>
		public static void WriteHeader() {
			StringBuilder sb = new StringBuilder(1024);
			sb.AppendLine();
			sb.AppendLine("=== MSCSteamMP " + ModEntry.MOD_VERSION
				+ "  proto " + Protocol.PROTOCOL_VERSION
				+ "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
			Line(sb, "build", BuildStamp);
			Line(sb, "machine", delegate { return Environment.MachineName; });
			Line(sb, "user", delegate { return Environment.UserName; });
			Line(sb, "os", delegate { return Environment.OSVersion.ToString(); });
			Line(sb, "clr", ClrDescription);
			Line(sb, "unity", UnityDescription);
			Line(sb, "screen", ScreenDescription);
			Line(sb, "path", delegate { return path ?? "?"; });
			Line(sb, "log levels", delegate {
				return LogSeverities.Name(LogChannels.Global) + " global, " + ChannelSpec();
			});
			WriteBlock(sb.ToString(), true);
			Flush();
		}

		/// <summary>
		/// Başlığa tek satır ekler. Alan üreticisi patlarsa "?" yazılır ve
		/// başlık devam eder.
		/// </summary>
		static void Line(StringBuilder sb, string label, System.Func<string> make) {
			string value = "?";
			try { value = make(); }
			catch { }
			if (string.IsNullOrEmpty(value)) value = "?";
			sb.Append("    ").Append(label).Append(" : ").AppendLine(value);
		}

		/// <summary>
		/// Oturum sonu: satır sayısı, kanal sayaçları, metrikler, katlananlar.
		/// Çökme olmasa bile "bu oturumda ne oldu" özeti dosyada kalır.
		/// </summary>
		public static void WriteFooter() {
			try {
				StringBuilder sb = new StringBuilder(512);
				sb.AppendLine();
				sb.AppendLine("=== session summary " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
				Line(sb, "written", delegate { return written + " byte(s)"; });
				Line(sb, "buffered", delegate { return pending.Count + " line(s)"; });
				Line(sb, "dedupe", LogDedup.Describe);
				sb.AppendLine(LogMetrics.Dump());
				// AG SAYACLARI - otomatik, komut girmeye gerek yok.
				//
				// NEDEN: log'da paket basina iz yoktu. Kapi/kontak/motor/
				// vida icin "gonderiliyor mu" ayirt edilemiyordu; [ACT]
				// satirlari yerel eylem gunlugu, ag kaniti degil. Oturum
				// sonunda tablo yazilirsa kullanici sadece oynayip kapatir;
				// teshis icin ekstra bir sey yapmaz.
				//
				// CANLI KANIT: 6 parti cikarma olayi ile net.partmount.send
				// n=6 birebir eslesti -> parca yolu calisiyor. Kapi/kontak
				// icin hicbir ag sayaci yoktu, yani "gonderilmiyor" ile
				// "uygulanmiyor" ayrilamiyordu.
				//
				// 2026-10-08: buraya UYGULAMA HUNISI de eklendi. Tasinma
				// katmani tek basina yetersiz: client logunda BoltBatch 78
				// ALINMIS ve teslim %100 gorunuyordu, ama "paket geldi"
				// ile "paket uygulandi" ayrimi yoktu. Oturum sonunda
				// bakilacak tek yer burasi oldugu icin huni de yaziliyor.
				try {
					sb.AppendLine();
					sb.AppendLine(Protocol.NetStats.Describe());
				}
				catch { }
				try {
					sb.AppendLine();
					sb.AppendLine(Telemetry.ApplyFunnel());
				}
				catch { }
				WriteBlock(sb.ToString(), true);
				Flush();
			}
			catch { }
		}

		static string BuildStamp() {
			try {
				System.Reflection.AssemblyName n =
					new System.Reflection.AssemblyName(System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
				return n.Version.ToString();
			}
			catch { return "?"; }
		}

		static string ClrDescription() {
			try {
				return "Mono " + (typeof(LogFile).Assembly.GetName().Version) + " on " + Environment.Version;
			}
			catch { return Environment.Version.ToString(); }
		}

		/// <summary>
		/// Unity sürümü.
		///
		/// NoInlining ZORUNLU. Unity'nin <c>Application.unityVersion</c>'ı bir
		/// ECall'dir; JIT bu metodu çağıran yere inline ederse, ECall'i
		/// taşıyan metot artık çağıranın kendisi olur ve "ECall metotlarının bir
		/// sistem modülüne paketlenmesi gerekir" SecurityException'ı, buradaki
		/// try/catch'te değil WriteHeader'ın gövdesinde patlar. Canlı kanıt:
		/// tests\verify.cmd /optimize- ile derliyor, masaüstü CLR'sinde
		/// oturum başlığı yazılamıyordu.
		///
		/// Oyun içinde ECall normal çalışır; try/catch yalnızca masaüstü testinde
		/// (ve olası bir OOM'da) devreye girer.
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static string UnityDescription() {
			try { return UnityEngine.Application.unityVersion; }
			catch { return "?"; }
		}

		/// <summary>Ekran çözünürlüğü. NoInlining açıklaması UnityDescription'a bak.</summary>
		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static string ScreenDescription() {
			try {
				return UnityEngine.Screen.width + "x" + UnityEngine.Screen.height
					+ (UnityEngine.Screen.fullScreen ? " fullscreen" : " windowed");
			}
			catch { return "?"; }
		}

		static string ChannelSpec() {
			string s = LogChannels.Spec();
			return s.Length == 0 ? "(all channels at the global level)" : s;
		}

		/// <summary>Ham metin yazar (başlık/sonu blokları için, kritik sayılır).</summary>
		public static void WriteBlock(string text, bool critical) {
			if (string.IsNullOrEmpty(text)) return;
			Write(text.TrimEnd('\n', '\r'), critical);
		}

		/// <summary>Dosya adları (döküm/hata mesajları için).</summary>
		public static string[] Files() {
			List<string> list = new List<string>();
			lock (gate) {
				if (string.IsNullOrEmpty(path)) return list.ToArray();
				list.Add(path);
				for (int i = 1; i < Generations; i++) list.Add(path + "." + i);
			}
			return list.ToArray();
		}
	}
}
