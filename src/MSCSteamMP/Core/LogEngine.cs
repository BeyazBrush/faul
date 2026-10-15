using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SteamMP {

	/// <summary>
	/// Log MOTORU. SmpLog bunun ince bir yüzüdür; bütün kararlar burada.
	///
	/// AKIŞ
	/// ----
	///
	///   çağıran
	///     │  Write(level, channel, event, message, fields)
	///     ▼
	///   LogDedup.ShouldWrite ── katlanabilir mi? ── hayır ─┐
	///     │ evet (ilk)                                      │
	///     ▼                                                 │
	///   LogDedup.Drain  ("xN" özetlerini yaz)              │
	///     │                                                 │
	///     ▼                                                 ▼
	///   kayıt biçimlendir (dosya satırı + konsör gövdesi)
	///     │
	///     ├──► halka tampon (konsör, sorgu, döküm)
	///     ├──► LogFile (kritik satırlar doğrudan, diğerleri tamponlu)
	///     └──► LogRemote (isteğe bağlı, karşı tarafa aynalama)
	///
	/// BÖLÜMLER
	/// --------
	/// Her modül tek başına deneyebilir olsun diye ayrıldı:
	///   * <see cref="LogChannels"/>  — kanal başına eşik ve sayaç
	///   * <see cref="LogDedup"/>     — tekrar katlama
	///   * <see cref="LogFile"/>      — döndürmeli, tamponlu dosya
	///   * <see cref="LogMetrics"/>   — sayaç/süre/gösterge
	///   * <see cref="LogScope"/>     — kapsam damgası
	///
	/// KAPSAM (scope) burada okunur ve kayda yazılır: kapsam yığını iş
	/// parçacığına ait olduğu için kayıt, o iş parçacığında yazılmış olmalıdır.
	///
	/// ÇOK PARÇACIK
	/// -------------
	/// Unity'nin log dinleyicisi HER iş parçacığından gelir, yani Write'a ana
	/// iş parçacığı dışından da girebilir. Kilit yalnızca tampona alma
	/// sırasında tutulur; biçimlendirme (string tahsisi) kilit DIŞINDA yapılır,
	/// çünkü en pahalı kısım odur ve kilidi uzatmak her şeyi yavaşlatır.
	/// </summary>
	public static class LogEngine {

		/// <summary>Konsör halka tamponunun kapasitesi (satır).</summary>
		public const int Capacity = 800;

		/// <summary>Oturumun başladığı an (sabit referans; süre hesabı için).</summary>
		public static readonly DateTime Epoch = DateTime.Now;

		static readonly LogRecord[] ring = new LogRecord[Capacity];
		static readonly object gate = new object();
		static int head;
		static int count;
		static int serial;

		/// <summary>Kritik kayıtlar (Warn/Error/Fatal/State) diske gecikmez.</summary>
		public static bool ImmediateFlush = true;

		/// <summary>Kapsam damgası kayda yazılsın mı?</summary>
		public static bool ScopeInFile = true;

		static bool started;

		// ------------------------------------------------------------------
		// Zaman
		// ------------------------------------------------------------------

		/// <summary>Oturum başından bu yana geçen süre (ms).</summary>
		public static long Since() {
			return (long)(DateTime.Now - Epoch).TotalMilliseconds;
		}

		/// <summary>Şu anki duvar saati damgası (HH:mm:ss.fff).</summary>
		public static string Stamp() {
			return DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
		}

		// ------------------------------------------------------------------
		// Kurulum
		// ------------------------------------------------------------------

		/// <summary>
		/// Motoru başlatır: kanal ayarlarını okur, dosyayı açar, başlığı yazar.
		/// SmpLog.Init bunu çağırır; ayrıca çağırmak zararlı değil.
		/// </summary>
		public static void Init() {
			lock (gate) { started = true; }
			try { LogChannels.Load(); } catch { }
			// Klasör SmpLog.Directory'den geliyor, doğrudan DLL konumundan
			// değil: SmpLog.Directory testler tarafından (ve ileride başka bir
			// konuma yönlendirmek isteyenler tarafından) değiştirilebilir ve bu
			// değer yok sayılırsa dosya her zaman DLL'nin yanına gider.
			LogFile.Open(SmpLog.Directory);
			try { LogFile.WriteHeader(); }
			catch (Exception e) {
				// Başlığın yazılamaması sessizce geçilemez: dosyada build bilgisi
				// yoksa "iki taraf aynı build mi koşuyor?" sorusu cevapsız kalır.
				// SmpLog'a GİDİLMEZ (yazı sırasında olurdu); doğrudan konsola.
				try { Console.WriteLine("[MSCSteamMP] log header could not be written: "
					+ e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); } catch { }
			}
		}

		/// <summary>Motor kuruldu mu?</summary>
		public static bool Started {
			get { lock (gate) { return started; } }
		}

		/// <summary>Oturumu kapatır: boşalt, özet yaz, dosyayı kapat.</summary>
		public static void Shutdown() {
			try { LogDedup.Drain(); } catch { }
			try { LogFile.WriteFooter(); } catch { }
			try { LogFile.Close(); } catch { }
		}

		// ------------------------------------------------------------------
		// Yazma
		// ------------------------------------------------------------------

		/// <summary>
		/// Ana yazma yolu.
		///
		/// <paramref name="fields"/> null olabilir; o zaman alan biçimlendirmesi
		/// hiç yapılmaz (sıcak yollarda önemli). <paramref name="eventName"/>
		/// null olabilir; o zaman etiket yazılmaz.
		/// </summary>
		public static void Write(LogSeverity level, LogKind kind, string channel, string eventName,
			string message, LogFields fields) {

			if (message == null) return;
			if (!LogChannels.Enabled(channel, level)) { FilteredCount++; return; }
			WriteCount++;

			string chan = string.IsNullOrEmpty(channel) ? LogChannels.Default : channel;
			string scope = LogScope.Current;

			// Katlanabilir seviyelerde tekrar sayacı.
			if (level >= LogSeverity.Debug || LogDedup.AlwaysFold) {
				int folded;
				if (!LogDedup.ShouldWrite(level, chan, scope, eventName, message, out folded)) {
					LogChannels.Count(chan);
					return;
				}
			}
			// Saat her kayıtta ilerletilir; SetClock aynı zamanda sessizleşmiş
			// tekrarları da boşaltır, yani "xN" özeti yeni satırın üstünde belirir.
			LogDedup.SetClock(Since());

			// Seri numarası mesaj başına BİR kez artar. Çok satırlı mesajın
			// satırları aynı numarayı paylaşır; konsör "toplam" değerini
			// karşılaştırdığı için her satırda artması kaydırma tetikleyicisini
			// bozuyordu.
			int serialNow = NextSerial();

			// Biçimlendirme: en pahalı kısım, kasıtlı olarak kilit dışında.
			string fieldText = fields == null ? null : fields.Format();
			long since = Since();
			string useScope = ScopeInFile ? scope : null;

			LogRecord rec = new LogRecord();
			rec.Since = since;
			rec.Serial = serialNow;
			rec.Level = level;
			rec.Kind = kind;
			rec.Channel = chan;
			rec.Event = eventName;
			rec.Message = message;
			rec.Fields = fieldText;
			rec.Scope = useScope;
			rec.Thread = LogThread.Current;
			rec.Repeat = 0;

			// Halka + dosya + uzak.
			//
			// ÇOK SATIRLI MESAJLAR konsörde SATIR SATIR görünmeli. Tek satır
			// olarak itilirse konsörün değişken yükseklik hesabı bozulur ve
			// kaydırma satır başına çalışmaz. Dosyada ise mesaj olduğu gibi
			// (gömülü satır sonlarıyla) yazılır, çünkü dosyanın ayrıştırıcısı
			// zaten satır bazlı ve bir istisna zinciri tek kayıtta kalmalı.
			PushRows(rec);

			string fileLine = rec.FormatFile();
			bool critical = ImmediateFlush && LogSeverities.IsCritical(level);
			LogFile.Write(fileLine, critical);
			if (critical) LogFile.Flush();
			LogChannels.Count(chan);
			try { LogRemote.Outbound(rec); } catch { }
		}

		/// <summary>
		/// Kaydı halkaya iter. Mesajda satır sonu varsa her satır AYRI kayıt
		/// olur; hepsi aynı seri numarasını paylaşır, yani "yeni mesaj geldi"
		/// tespiti bir kez tetiklenir.
		/// </summary>
		static void PushRows(LogRecord rec) {
			if (rec.Message == null || rec.Message.IndexOf('\n') < 0) {
				Push(rec);
				return;
			}
			string[] lines = rec.Message.Split('\n');
			for (int i = 0; i < lines.Length; i++) {
				LogRecord row = rec;
				row.Message = lines[i].TrimEnd('\r');
				// Alanlar ve tekrarlar yalnızca son satırda: ara satırlar
				// gövdenin devamı, onlarda alan tekrarı yanıltıcı olur.
				if (i != lines.Length - 1) { row.Fields = null; row.Repeat = 0; }
				Push(row);
			}
		}

		/// <summary>Seri numarası (kilit altında; sıralama için şart).</summary>
		static int NextSerial() {
			lock (gate) { return ++serial; }
		}

		/// <summary>
		/// Karşı taraftan gelen kaydı olduğu gibi yazar: kapsam, alan biçimi ve
		/// tekrar katlama UYGULANMAZ. Uygulanırsa karşı tarafın gönderdiği
		/// biçim bozulur ve "peer kanalında kendi satırlarımızı arıyoruz"
		/// karışıklığı çıkar.
		/// </summary>
		internal static void PushPeer(LogRecord rec) {
			rec.Serial = NextSerial();
			Push(rec);
			LogFile.Write(rec.FormatFile(), LogSeverities.IsCritical(rec.Level));
			LogChannels.Count(rec.Channel);
		}

		/// <summary>
		/// Katlanmış kayıt özeti. Dedup modülünden gelir ve dedup'u YİNE
		/// uygulamaz; doğrudan halka + dosyaya yazar.
		/// </summary>
		internal static void EmitFolded(LogSeverity level, string channel, string scope,
			string eventName, string message, string fields, int repeat) {

			LogRecord rec = new LogRecord();
			rec.Since = Since();
			rec.Serial = NextSerial();
			rec.Level = level;
			rec.Kind = LogSeverities.ToKind(level);
			rec.Channel = channel;
			rec.Event = eventName;
			rec.Message = message;
			rec.Fields = fields;
			rec.Scope = ScopeInFile ? scope : null;
			rec.Thread = 0;
			rec.Repeat = repeat + 1;

			Push(rec);
			bool critical = ImmediateFlush && LogSeverities.IsCritical(level);
			LogFile.Write(rec.FormatFile(), critical);
			if (critical) LogFile.Flush();
			LogChannels.Count(channel);
		}

		// ------------------------------------------------------------------
		// Kolay katmanlar
		// ------------------------------------------------------------------

		/// <summary>Teşhis satırı (sıcak yollarda; öntanımlı kapalı değil, kanal eşiğine bakar).</summary>
		public static void Debug(string channel, string eventName, string message) {
			Write(LogSeverity.Debug, LogKind.Info, channel, eventName, message, null);
		}

		public static void Debug(string channel, string eventName, string message, LogFields f) {
			Write(LogSeverity.Debug, LogKind.Info, channel, eventName, message, f);
		}

		/// <summary>En gürültülü seviye. Sadece açıkça istendiğinde yazılır.</summary>
		public static void Trace(string channel, string eventName, string message) {
			Write(LogSeverity.Trace, LogKind.Info, channel, eventName, message, null);
		}

		public static void Info(string channel, string message) {
			Write(LogSeverity.Info, LogKind.Info, channel, null, message, null);
		}

		public static void Info(string channel, string eventName, string message, LogFields f) {
			Write(LogSeverity.Info, LogKind.Info, channel, eventName, message, f);
		}

		public static void Warn(string channel, string message) {
			Write(LogSeverity.Warn, LogKind.Warn, channel, null, message, null);
		}

		public static void Warn(string channel, string eventName, string message, LogFields f) {
			Write(LogSeverity.Warn, LogKind.Warn, channel, eventName, message, f);
		}

		public static void Error(string channel, string message) {
			Write(LogSeverity.Error, LogKind.Error, channel, null, message, null);
		}

		public static void Error(string channel, string eventName, string message, LogFields f) {
			Write(LogSeverity.Error, LogKind.Error, channel, eventName, message, f);
		}

		/// <summary>Oturumu bitiren hata: ekran konsörü açılır.</summary>
		public static void Fatal(string channel, string message) {
			Write(LogSeverity.Fatal, LogKind.Error, channel, null, message, null);
		}

		/// <summary>Durum değişimi: bağlandı, ayrıldı, dünya geldi/gitti.</summary>
		public static void State(string channel, string message) {
			Write(LogSeverity.Info, LogKind.State, channel, null, message, null);
		}

		// ------------------------------------------------------------------
		// Hata yakalama
		// ------------------------------------------------------------------

		/// <summary>
		/// İstisnayı loglar: tür, mesaj, İÇ istisna zinciri ve yığın izi.
		///
		/// Neden özel: eski kod neredeyse her yerde <c>e.Message</c> yazıyordu.
		/// Harmony 1.2 bir istisnayı sarmalayıp attığında mesaj "Exception from
		/// HarmonyInstance" olur ve ASIL sebep kaybolur; canlı log kanıtı bu
		/// yüzden "hangi metot bulunamadı" sorusu hep cevapsızdı. Burada zincir
		/// kök nedene kadar açılır.
		///
		/// Zincir derinliği sınırlıdır: bir hata döngüsü (A -> B -> A) sonsuza
		/// kadar yazılmasın diye 6 seviyeye kırpılır, kırpıldığı belirtilir.
		/// </summary>
		public static void Exception(string channel, string context, Exception e) {
			if (e == null) {
				Warn(channel, context + ": null exception");
				return;
			}
			StringBuilder sb = new StringBuilder(512);
			sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
			int depth = 0;
			Exception inner = e.InnerException;
			while (inner != null && depth < 6) {
				sb.Append(" <- ").Append(inner.GetType().Name)
				  .Append(": ").Append(inner.Message);
				inner = inner.InnerException;
				depth++;
			}
			if (inner != null) sb.Append(" <- ... (chain truncated)");

			LogFields f = new LogFields().Add("ctx", context == null ? "?" : context);
			if (e.StackTrace != null) {
				// Yığın izi gövdeye değil alana değil: gövde tek satır kalmalı,
				// konsör yüksekliği ve dosya okunabilirliği bozulmasın.
				f.Add("at", FirstFrame(e.StackTrace));
			}
			Write(LogSeverity.Error, LogKind.Error, channel, "exception",
				context + ": " + sb.ToString(), f);

			if (e.StackTrace != null) {
				// Tam iz ayrı satırlarda, girintili. Konsörde de görünür.
				foreach (string line in e.StackTrace.Split('\n')) {
					string t = line.TrimEnd('\r').Trim();
					if (t.Length > 0) Write(LogSeverity.Error, LogKind.Error, channel, "at", t, null);
				}
			}
		}

		static string FirstFrame(string stack) {
			int nl = stack.IndexOf('\n');
			string first = nl > 0 ? stack.Substring(0, nl) : stack;
			return first.Trim().Length > 120 ? first.Trim().Substring(0, 120) : first.Trim();
		}

		// ------------------------------------------------------------------
		// Halka tampon (konsör)
		// ------------------------------------------------------------------

		static void Push(LogRecord rec) {
			lock (gate) {
				ring[head] = rec;
				head++;
				if (head >= Capacity) head = 0;
				if (count < Capacity) count++;
			}
		}

		/// <summary>Ekranda tutulan satır sayısı.</summary>
		public static int LineCount {
			get { lock (gate) { return count; } }
		}

		/// <summary>Toplam yazılan kayıt (taşmadan önceki toplam).</summary>
		public static int Total {
			get { lock (gate) { return serial; } }
		}

		/// <summary>Mantıksal indeksi halka dilimine çevirir (0 = en eski tutulan).</summary>
		static int SlotOf(int index) {
			int start = count < Capacity ? 0 : head;
			int i = start + index;
			if (i >= Capacity) i -= Capacity;
			return i;
		}

		/// <summary>Kaydın tamamı (konsör satır çizimi ve sorgular için).</summary>
		public static bool TryGet(int index, out LogRecord record) {
			lock (gate) {
				if (index < 0 || index >= count) { record = new LogRecord(); return false; }
				record = ring[SlotOf(index)];
				return true;
			}
		}

		/// <summary>Kaydın yüksekliği (konsör ölçüm önbelleği).</summary>
		public static float Height(int index) {
			lock (gate) {
				if (index < 0 || index >= count) return 0f;
				return ring[SlotOf(index)].Height;
			}
		}

		/// <summary>Konsörün ölçtüğü yüksekliği kayda yazar (metinle birlikte taşınır).</summary>
		public static void SetHeight(int index, float height) {
			lock (gate) {
				if (index < 0 || index >= count) return;
				ring[SlotOf(index)].Height = height;
			}
		}

		// ------------------------------------------------------------------
		// Sorgu ve döküm
		// ------------------------------------------------------------------

		/// <summary>
		/// Filtreli kayıt listesi. Konsör filtre kutusu ve 'smp log find' bunu
		/// kullanır. <paramref name="limit"/> 0 ise sınırsız.
		/// </summary>
		public static List<LogRecord> Query(string needle, LogSeverity minLevel, string channel,
			bool errorsOnly, int limit) {

			List<LogRecord> result = new List<LogRecord>(64);
			lock (gate) {
				for (int i = 0; i < count; i++) {
					LogRecord r = ring[SlotOf(i)];
					if (minLevel != LogSeverity.Off && !LogSeverities.AtMost(r.Level, minLevel)) continue;
					if (errorsOnly && !LogSeverities.IsNoteworthy(r.Level)) continue;
					if (!string.IsNullOrEmpty(channel)
						&& !string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase)) continue;
					if (!r.Matches(needle)) continue;
					result.Add(r);
				}
			}
			if (limit > 0 && result.Count > limit) {
				result.RemoveRange(0, result.Count - limit);
			}
			return result;
		}

		/// <summary>
		/// Halkadaki her şeyi dosyaya yazar (bug raporu paketi). 'smp log dump'
		/// bunu çağırır: kullanıcı ekran görüntüsü almak yerine tek dosya
		/// gönderir.
		/// </summary>
		public static string Dump(string fileName) {
			try {
				string dir = SmpLog.DllDirectory();
				if (string.IsNullOrEmpty(dir)) dir = ".";
				if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
				string path = System.IO.Path.Combine(dir,
					string.IsNullOrEmpty(fileName) ? "mscmp-dump.txt" : fileName);

				StringBuilder sb = new StringBuilder(64 * 1024);
				sb.Append("# MSCSteamMP log dump  ").Append(DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
				sb.Append("  buffer=").Append(count).Append(" of ").Append(serial)
				  .Append(" record(s) written this session").Append('\n');
				sb.Append(LogChannels.Describe()).Append('\n');
				sb.Append(LogDedup.Describe()).Append('\n');
				sb.Append(LogMetrics.Dump()).Append('\n');
				sb.Append('\n');

				List<LogRecord> all = Query(null, LogSeverity.Off, null, false, 0);
				for (int i = 0; i < all.Count; i++) sb.Append(all[i].FormatPlain()).Append('\n');

				System.IO.File.WriteAllText(path, sb.ToString());
				return path;
			}
			catch (Exception e) {
				return "FAILED: " + e.GetType().Name + ": " + e.Message;
			}
		}

		// ------------------------------------------------------------------
		// Sağlık kontrolü
		// ------------------------------------------------------------------

		/// <summary>
		/// Log sisteminin kendi durumu. 'smp log health' bunu basar.
		///
		/// Neden gerekli: "log yazmıyor" belirtisi iki farklı yerde olabilir —
		/// dosya açılamıyor (izin, disk dolu) ya da filtre her şeyi eliyor
		/// (seviyan yanlış). Bu çıktı ikisini birbirinden ayırır.
		/// </summary>
		public static string Health() {
			StringBuilder sb = new StringBuilder(768);
			sb.Append("log health:");
			sb.Append("\n  engine      : ").Append(Started ? "started" : "not started");
			sb.Append("\n  file        : ").Append(LogFile.Describe());
			sb.Append("\n  file path   : ").Append(LogFile.Path);
			sb.Append("\n  written     : ").Append(LogFile.Written).Append(" byte(s)");
			sb.Append("\n  buffered    : ").Append(LogFile.PendingLines).Append(" line(s) waiting for the file");
			sb.Append("\n  buffer      : ").Append(count).Append('/').Append(Capacity)
			  .Append(" line(s), ").Append(serial).Append(" written total");
			sb.Append("\n  global level: ").Append(LogSeverities.Name(LogChannels.Global));
			sb.Append("\n  overrides   : ").Append(LogChannels.Spec().Length == 0 ? "(none)" : LogChannels.Spec());
			sb.Append("\n  dedupe      : ").Append(LogDedup.Describe());
			sb.Append("\n  scope depth : ").Append(LogScope.Depth);
			sb.Append("\n  remote      : ").Append(LogRemote.Describe());
			sb.Append("\n  writes      : ").Append(WriteCount)
			  .Append(" accepted, ").Append(FilteredCount).Append(" filtered out");

			string[] problems = Problems();
			sb.Append("\n  verdict     : ").Append(problems.Length == 0 ? "OK" : string.Join("; ", problems));
			return sb.ToString();
		}

		/// <summary>Tespit edilen sorunlar (boşsa sorun yok).</summary>
		public static string[] Problems() {
			List<string> list = new List<string>(3);
			if (!LogFile.Enabled) list.Add("the log file is not open (" + LogFile.Problem + ")");
			if (LogChannels.Global == LogSeverity.Off && LogChannels.Spec().Length == 0)
				list.Add("every channel is off, nothing is written");
			if (!Started) list.Add("the engine was never started");
			return list.ToArray();
		}

		/// <summary>Yazma çağrısı sayısı (kabul edilenler; teşhis için).</summary>
		public static int WriteCount;
		/// <summary>Filtre tarafından elenen çağrı sayısı.</summary>
		public static int FilteredCount;

		/// <summary>Sayaçları sıfırlar (oturum sonu / konsör temizleme).</summary>
		public static void ResetCounters() {
			lock (gate) {
				head = 0;
				count = 0;
				serial = 0;
				WriteCount = 0;
				FilteredCount = 0;
			}
			LogChannels.ResetCounters();
			LogDedup.Reset();
			LogMetrics.Reset();
		}
	}
}
