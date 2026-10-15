using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Oyunun ve MSCLoader'ın loglarını Mods klasörüne yönlendirir.
	///
	/// Niye gerekli
	/// ------------
	/// Üç ayrı kanal var ve varsayılan olarak hepsi dağıtık:
	///
	///   1. Debug.Log / Debug.LogError  -> Unity'nin Player.log'u. Windows'ta
	///      %USERPROFILE%\AppData\Local\Low\Amistech\My Summer Car\Player.log
	///      altında, oyuncu için görünmez bir yerde.
	///   2. Console.WriteLine             -> oyunun output_log.txt'si, oyun
	///      klasörünün kökünde. MSCLoader bütün mesajlarını buraya da yazıyor.
	///   3. MSCLoader'ın kendi konsol ekranı (F1). Kullanıcı bunu açmak zorunda,
	///      ve Warning/Error çağrılarında zorla açılıyor.
	///
	/// Kullanıcı isteği: hepsi Mods klasörüne gitsin ve MSCLoader'ın konsol ekranı
	/// kullanılmasın.
	///
	/// Nasıl
	/// -----
	///   * Console.WriteLine -> Console.SetOut ile Mods\mscmlog.txt'ye yönlendirilir.
	///     Bu, MSCLoader'ın ve oyunun mesajlarının TAMAMINI yakalar; ayrı ayrı
	///     ModConsole'u çağırmaya gerek kalmaz.
	///   * Debug.Log        -> Application.logMessageReceivedThreaded ile
	///     Mods\unitylog.txt'ye yazılır. PlayMaker'ın FSM logları da Debug.Log
	///     kullandığı için aynı dosyaya düşer.
	///
	/// Neden threaded dinleyici
	/// ------------------------
	/// Application.logMessageReceived yalnızca ana iş parçacığından gelen
	/// logları verir, logMessageReceivedThreaded ise HER iş parçacığından
	/// verir (ana iş parçacığı dahil). İkisine birden abone olsaydık ana iş
	/// parçacığındaki her mesaj iki kez yazılırdı. Sadece threaded'e abone
	/// oluyoruz: tek abonelik, tam kapsam.
	///
	/// Recursion tuzağı
	/// ----------------
	/// Bu sınıf dosyaya yazarken ASLA Debug.Log kullanmaz ve SmpLog'a gitmez;
	/// sadece kendi StreamWriter'ını kullanır. Aksi hâlde yazma işlemi yeni bir log
	/// üretir, o log tekrar yazılır ve sonsuza kadar büyüyen bir döngü oluşurur.
	///
	/// Disk
	/// ----
	/// unitylog.txt oyunun hata ayıklama açıkken çok büyüyebilir (her durum
	/// geçişi). İki dosya da 8 MB'ı aşınca .old' adıyla döndürülüyor.
	/// </summary>
	public static class GameLogCapture {

		/// <summary>Mods klasörü. SmpLog ile aynı yer.</summary>
		public static string Directory = @"D:\Steam\steamapps\common\My Summer Car\Mods";

		/// <summary>Console.WriteLine çıktısı (MSCLoader + oyun).</summary>
		public const string CONSOLE_FILE = "mscmlog.txt";

		/// <summary>Debug.Log çıktısı (Unity + PlayMaker FSM).</summary>
		public const string UNITY_FILE = "unitylog.txt";

		const long MAX_BYTES = 8L * 1024L * 1024L;

		static readonly object gate = new object();
		static TextWriter consoleFile;
		static TextWriter unityFile;
		static TextWriter originalOut;
		static bool installed;
		static bool broken;

		/// <summary>Nereye yazılıyor? Konsör başlığında ve 'smp log path' çıktısında.</summary>
		public static string Describe() {
			if (broken) return "disabled (files are not writable)";
			return System.IO.Path.Combine(Directory, CONSOLE_FILE)
				+ " + " + System.IO.Path.Combine(Directory, UNITY_FILE);
		}

		// ------------------------------------------------------------------
		// Kurulum
		// ------------------------------------------------------------------

		public static void Install() {
			if (installed) return;
			installed = true;

			lock (gate) {
				try {
					// SmpLog ile ayni yer: sabit D: yolu yoksa DLL'nin yani.
					// SmpLog.Init henuz calismamis olabilir; cozumu dogrudan
					// cagir, SmpLog.Directory'e guvenme.
					if (string.IsNullOrEmpty(Directory) || !System.IO.Directory.Exists(Directory)) {
						string dllDir = SmpLog.DllDirectory();
						Directory = !string.IsNullOrEmpty(dllDir) ? dllDir : SmpLog.Directory;
					}
					if (!System.IO.Directory.Exists(Directory)) System.IO.Directory.CreateDirectory(Directory);

					// output_log.txt çalışmaya devam etsin: yazarken asıl akış da
					// açılıyor. Aksi hâlde oyunun kendi hata ayıklaması körleşirdi.
					try { originalOut = Console.Out; } catch { originalOut = null; }

					consoleFile = OpenForAppend(CONSOLE_FILE);
					unityFile = OpenForAppend(UNITY_FILE);

					Header(consoleFile, "Console.WriteLine (MSCLoader + game) capture");
					Header(unityFile, "Debug.Log capture (Unity + PlayMaker FSM)");

					if (consoleFile != null && originalOut != null) {
						Console.SetOut(new TeeWriter(originalOut, consoleFile));
					}
					else if (originalOut == null) {
						// Mono'da Console.Out yerleşik olmayabilir; yine de dosyayı
						// kurup Unity kanalını çalıştırıyoruz.
						Console.SetOut(new TeeWriter(TextWriter.Null, consoleFile));
					}

					// Tek abonelik, tam kapsam: bkz. sınıf yorumundaki gerekçe.
					Application.logMessageReceivedThreaded += OnLog;

					broken = false;
				}
				catch (Exception) {
					broken = true;
					consoleFile = null;
					unityFile = null;
				}
			}

			if (broken) {
				// Buraya SmpLog yazmıyoruz: SmpLog bu dosyalara değil kendi
				// dosyasına yazıyor, ama yine de bir döngü ihtimali istemiyoruz.
				Console.WriteLine("[MSCSteamMP] game log capture could not be installed.");
			}
		}

		static TextWriter OpenForAppend(string name) {
			try {
				string path = System.IO.Path.Combine(Directory, name);
				Roll(path);
				return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) {
					AutoFlush = true
				};
			}
			catch (Exception) {
				return null;
			}
		}

		static void Roll(string path) {
			try {
				if (!File.Exists(path)) return;
				if (new FileInfo(path).Length < MAX_BYTES) return;
				string old = path + ".old";
				if (File.Exists(old)) File.Delete(old);
				File.Move(path, old);
			}
			catch (Exception) { }
		}

		static void Header(TextWriter w, string what) {
			if (w == null) return;
			w.WriteLine();
			w.WriteLine("=== " + what + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
				CultureInfo.InvariantCulture) + " ===");
		}

		// ------------------------------------------------------------------
		// Yakalama
		// ------------------------------------------------------------------

		static void OnLog(string condition, string stackTrace, LogType type) {
			TextWriter w;
			lock (gate) {
				if (broken) return;
				w = unityFile;
			}
			if (w == null) return;

			try {
				string stamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
				w.WriteLine(stamp + " [" + type + "] " + condition);

				// Yığın izi yalnızca gerçek hatalarda: normal logları yazmak dosyayı
				// kullanılmaz hâle getiriyor.
				if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) {
					if (!string.IsNullOrEmpty(stackTrace)) w.WriteLine("    " + stackTrace.Replace("\n", "\n    "));
				}
			}
			catch (Exception) {
				// Yazma başarısız olursa sus; bir log hatası oyunu düşürmemeli.
				broken = true;
			}
		}

		/// <summary>Konsol ve Unity loglarının yazıldığı dosyaların tam yolu.</summary>
		public static string[] Files() {
			return new string[] {
				System.IO.Path.Combine(Directory, CONSOLE_FILE),
				System.IO.Path.Combine(Directory, UNITY_FILE),
			};
		}

		/// <summary>
		/// PlayMaker'ın kendi FSM loglamasını açar/kapatır.
		///
		/// Varsayılan KAPALI. Açıkken her durum geçişi yazılır ve dosya dakikalar
		/// içinde büyüyebilir; teşhis gerektiğinde açıp kapatmak daha doğru.
		/// dnSpy: FsmLog.LoggingEnabled herkese açık bir statik özellik.
		/// </summary>
		public static bool SetFsmLogging(bool on) {
			try {
				HutongGames.PlayMaker.FsmLog.LoggingEnabled = on;
				if (on) {
					// MirrorDebugLog, FSM logunu Debug.Log'a da yazar; o da
					// unitylog.txt'ye düşer, yani tek dosyada görünür.
					HutongGames.PlayMaker.FsmLog.MirrorDebugLog = true;
				}
				return HutongGames.PlayMaker.FsmLog.LoggingEnabled;
			}
			catch (Exception) {
				return false;
			}
		}

		/// <summary>Dosyaları kapat (oyun kapanırken).</summary>
		public static void Close() {
			lock (gate) {
				try { if (consoleFile != null) consoleFile.Flush(); } catch { }
				try { if (unityFile != null) unityFile.Flush(); } catch { }
			}
		}

		// ------------------------------------------------------------------
		// Çift yazıcı
		// ------------------------------------------------------------------

		/// <summary>
		/// Bir yazıcıyı iki hedefe yazar: asıl akış (output_log.txt) ve bizim dosya.
		/// Asıl akış kapatılmıyor çünkü oyunun kendi hata ayıklaması oraya bakıyor.
		///
		/// Satır TAMPONLU yazılır. Console.WriteLine zaten tam satır verir, ama
		/// Console.Write("...") parça parça gelir; parçaları doğrudan yazarsak aynı
		/// satır dosyaya defalarca yazılır ve konsol çıktısı arasına karışır.
		/// tests/CoreProbe bu sınıfı doğrudan sınar.
		/// </summary>
		public class TeeWriter : TextWriter {
			readonly TextWriter first, second;
			readonly StringBuilder buffer = new StringBuilder(128);
			readonly object writeLock = new object();

			public TeeWriter(TextWriter first, TextWriter second) {
				this.first = first;
				this.second = second;
				CoreNewLine = new[] { '\n' };
			}

			public override Encoding Encoding {
				get { return first != null ? first.Encoding : Encoding.UTF8; }
			}

			public override void Write(char value) {
				// Satır tamamlanmadan yazmıyoruz: yarım satır iki kez yazılırsa
				// konsol çıktısı arasına karışır ve dosya okunmaz hale gelir.
				lock (writeLock) {
					if (value == '\n') FlushLine();
					else buffer.Append(value);
				}
			}

			public override void Write(string value) {
				if (string.IsNullOrEmpty(value)) return;
				for (int i = 0; i < value.Length; i++) Write(value[i]);
			}

			public override void WriteLine(string value) {
				lock (writeLock) {
					if (!string.IsNullOrEmpty(value)) buffer.Append(value);
					FlushLine();
				}
			}

			public override void WriteLine() { WriteLine(string.Empty); }

			void FlushLine() {
				string line = buffer.ToString();
				buffer.Length = 0;
				try { if (first != null) first.WriteLine(line); } catch { }
				try { if (second != null) second.WriteLine(line); } catch { }
			}

			/// <summary>
			/// Tamponlanmış satırı da akıtır, sonra hedefleri flush eder.
			///
			/// Satır sonu gelmemiş bir yazma varsa onu da yazıyoruz; yoksa
			/// Console.Write("...") ile başlayıp yarım kalan her mesaj dosyada
			/// kaybolurdu. tests/CoreProbe bunu doğruluyor.
			/// </summary>
			public override void Flush() {
				lock (writeLock) {
					if (buffer.Length > 0) FlushLine();
					try { if (first != null) first.Flush(); } catch { }
					try { if (second != null) second.Flush(); } catch { }
				}
			}
		}
	}
}