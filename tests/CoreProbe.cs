using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security;
using HutongGames.PlayMaker;
using SteamMP;

namespace CoreProbe {

	/// <summary>
	/// Exercises the capture, batching, echo-guard and inbound-apply logic against a fake
	/// Satsuma built from real PlayMaker variable types. No scene needed: nothing here
	/// touches GameObject.
	/// </summary>
	public static class Program {

		static int failures;

		static void Check(string what, object actual, object expected) {
			bool ok = Equals(actual, expected);
			if (!ok) failures++;
			Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + " = " + actual
				+ (ok ? "" : "   (expected " + expected + ")"));
		}

		public static int Main() {
			SatsumaRegistry.Clock = delegate { return fakeNow; };
			RunFilters();
			RunEchoGuard();
			RunLiveBoltCapture();
			RunInitialSyncPaging();
			RunInboundBoltApply();
			RunInboundWithNoTable();
			RunLobbyCode();
	RunPathSplit();
			RunLogging();
			RunLogLevels();
			RunLogChannels();
			RunLogFields();
			RunLogScope();
			RunLogDedup();
			RunLogMetrics();
			RunLogBuffering();
			RunCommandBridge();
			RunCommandParser();
			Console.WriteLine(failures == 0 ? "ALL CORE CHECKS PASSED" : failures + " CHECK(S) FAILED");
			return failures == 0 ? 0 : 1;
		}

		/// <summary>
		/// Seviye adlarının ayrıştırması. Tek sözlük paylaşıyorlar: komut
		/// satırındaki "debug", dosyadaki ayar ve el yazımı aynı sonucu vermeli.
		/// Ayrı ayrı ayrıştırılsaydı sessiz farklar oluşurdu.
		/// </summary>
		static void RunLogLevels() {
			Console.WriteLine("--- log levels ---");
			Check("parse debug", LogSeverities.Parse("debug", LogSeverity.Info), LogSeverity.Debug);
			Check("parse DEBUG upper", LogSeverities.Parse("DEBUG", LogSeverity.Info), LogSeverity.Debug);
			Check("parse d", LogSeverities.Parse("d", LogSeverity.Info), LogSeverity.Debug);
			Check("parse warn", LogSeverities.Parse("warn", LogSeverity.Info), LogSeverity.Warn);
			Check("parse w", LogSeverities.Parse("w", LogSeverity.Info), LogSeverity.Warn);
			Check("parse error/e", LogSeverities.Parse("e", LogSeverity.Info), LogSeverity.Error);
			Check("parse trace", LogSeverities.Parse("trace", LogSeverity.Info), LogSeverity.Trace);
			Check("parse off", LogSeverities.Parse("off", LogSeverity.Info), LogSeverity.Off);
			Check("parse on is off", LogSeverities.Parse("on", LogSeverity.Info), LogSeverity.Off);
			Check("garbage falls back", LogSeverities.Parse("zzz", LogSeverity.Warn), LogSeverity.Warn);
			Check("null falls back", LogSeverities.Parse(null, LogSeverity.Error), LogSeverity.Error);

			// Esik karsilastirmasi: "kayit bu esigin altinda mi" (AtMost).
			Check("debug is above the info threshold", LogSeverities.AtMost(LogSeverity.Debug, LogSeverity.Info), false);
			Check("error passes a debug threshold", LogSeverities.AtMost(LogSeverity.Error, LogSeverity.Debug), true);
			Check("info does NOT pass a warn threshold", LogSeverities.AtMost(LogSeverity.Info, LogSeverity.Warn), false);
			Check("warn passes a warn threshold", LogSeverities.AtMost(LogSeverity.Warn, LogSeverity.Warn), true);
			Check("trace passes a debug threshold", LogSeverities.AtMost(LogSeverity.Trace, LogSeverity.Debug), false);

			Check("error is critical", LogSeverities.IsCritical(LogSeverity.Error), true);
			Check("warn is critical", LogSeverities.IsCritical(LogSeverity.Warn), true);
			Check("info is not critical", LogSeverities.IsCritical(LogSeverity.Info), false);
			Check("debug is not critical", LogSeverities.IsCritical(LogSeverity.Debug), false);

			Check("warn maps to warn badge", LogSeverities.ToKind(LogSeverity.Warn), LogKind.Warn);
			Check("fatal maps to error badge", LogSeverities.ToKind(LogSeverity.Fatal), LogKind.Error);
			Check("debug maps to plain badge", LogSeverities.ToKind(LogSeverity.Debug), LogKind.Info);
			Check("padded keeps width", LogSeverities.Padded(LogSeverity.Warn).Length, 5);
		}

		/// <summary>
		/// Kanal bazlı filtre. Tek global eşiğin yetmediği yer burası: bir
		/// kanalı sustururken diğerini açık tutmak.
		/// </summary>
		static void RunLogChannels() {
			Console.WriteLine("--- log channels ---");
			LogSeverity global = LogChannels.Global;
			Check("global starts at info", global, LogSeverity.Info);

			// Kanal özel eşiği globalin üstüne çıkar.
			LogChannels.SetLevel("net", LogSeverity.Debug, false);
			Check("net override applies", LogChannels.LevelOf("net"), LogSeverity.Debug);
			Check("unknown channel uses global", LogChannels.LevelOf("nosuch"), LogSeverity.Info);

			Check("net accepts debug", LogChannels.Enabled("net", LogSeverity.Debug), true);
			Check("net rejects trace", LogChannels.Enabled("net", LogSeverity.Trace), false);
			Check("mod rejects debug", LogChannels.Enabled("mod", LogSeverity.Debug), false);
			Check("mod accepts info", LogChannels.Enabled("mod", LogSeverity.Info), true);
			Check("mod accepts warn", LogChannels.Enabled("mod", LogSeverity.Warn), true);
			Check("off never passes", LogChannels.Enabled("mod", LogSeverity.Off), false);

			// Toplu uygulama.
			Check("spec applies", LogChannels.ApplySpec("ui=off,satsuma=trace"), true);
			Check("ui is off", LogChannels.LevelOf("ui"), LogSeverity.Off);
			Check("satsuma is trace", LogChannels.LevelOf("satsuma"), LogSeverity.Trace);
			Check("spec round-trips", LogChannels.Spec().Length > 0, true);

			// "*" globali değiştirir, kanal özel eşikleri DURUR.
			LogChannels.SetGlobal(LogSeverity.Warn, false);
			Check("global changed", LogChannels.Global, LogSeverity.Warn);
			Check("channel override survives global change", LogChannels.LevelOf("net"), LogSeverity.Debug);
			Check("channel without override follows global", LogChannels.LevelOf("item"), LogSeverity.Warn);
			LogChannels.SetGlobal(global, false);
			LogChannels.ApplySpec("*=info");

			// Sayaçlar kanal bazında.
			int before = LogChannels.CountOf("net");
			LogChannels.Count("net");
			LogChannels.Count("net");
			Check("channel counter counts", LogChannels.CountOf("net") - before, 2);
			Check("describe lists the channel", LogChannels.Describe().Length > 0, true);
		}

		/// <summary>
		/// Yapılandırılmış alanlar. Log satırlarının makine tarafından
		/// okunabilir olmasının tek yolu buradan geçiyor; ayrıştırılabilirlik
		/// tırnak kaçışı ve kültürden bağımsız sayı biçimini gerektiriyor.
		/// </summary>
		static void RunLogFields() {
			Console.WriteLine("--- log fields ---");
			Check("empty renders nothing", new LogFields().Format(), "");

			LogFields f = new LogFields().Add("path", "SATSUMA/Chassis/strut fl")
				.Add("stage", 3).Add("tight", 0.75f).Add("held", true);
			string text = f.Format();
			Check("string field quoted", text.Contains("path=\"SATSUMA/Chassis/strut fl\""), true);
			Check("int field plain", text.Contains("stage=\"3\""), true);
			Check("bool is 1 or 0", text.Contains("held=\"1\""), true);
			// Ondalık ayracı nokta olmalı: alıcı ayrıştırıcı InvariantCulture
			// ile okuyor, yazarken de aynısı yazılıyor.
			Check("float uses a dot, not a comma", text.Contains("tight=\"0.75\""), true);
			Check("field count", f.Count, 4);
			Check("no fields dropped", f.Dropped, 0);

			// Tırnak ve satır sonu kaçışı: kaçmazsa tek satırlık kayıt çok
			// satıra bölünür ve satır bazlı ayrıştırıcıda kayıp oluşur.
			LogFields q = new LogFields().Add("msg", "he said \"hi\"\nthen left");
			string qt = q.Format();
			Check("quote escaped", qt.Contains("\\\""), true);
			Check("newline escaped", qt.Contains("\\n"), true);
			Check("rendered as one line", qt.IndexOf('\n') < 0, true);

			// Geçersiz anahtar alanı SESSİZCE düşürmemeli, sayılmalı.
			LogFields bad = new LogFields().Add("bad key!", "x").Add("ok", 1);
			Check("invalid key dropped", bad.Dropped, 1);
			Check("valid key survives", bad.Format().Contains("ok=\"1\""), true);

			// Sınır.
			LogFields many = new LogFields();
			for (int i = 0; i < LogFields.MAX_FIELDS + 4; i++) many.Add("k" + i, i);
			Check("field cap enforced", many.Count, LogFields.MAX_FIELDS);
			Check("overflow counted", many.Dropped, 4);

			// Özel biçimlendiriciler.
			Check("hex is padded", new LogFields().AddHex("h", 0x1234u).Format().Contains("0x00001234"), true);
			Check("milliseconds carry a unit", new LogFields().AddMs("t", 12.5).Format().Contains("12.5ms"), true);
			Check("nan is written", new LogFields().Add("v", float.NaN).Format().Contains("nan"), true);
		}

		/// <summary>
		/// Kapsam. Paket gönderiminin 40 ayrı yerden çağrıldığını düşününce
		/// "hangi işlem?" bilgisini elle yazmak sürdürülemez; kapsam yığını
		/// bunu otomatik taşır.
		/// </summary>
		static void RunLogScope() {
			Console.WriteLine("--- log scope ---");
			Check("starts empty", LogScope.Current, "");
			Check("starts at depth 0", LogScope.Depth, 0);

			using (LogScope.Push("sync")) {
				Check("one level", LogScope.Current, "sync");
				using (LogScope.Push("parts")) {
					Check("two levels join", LogScope.Current, "sync/parts");
					using (LogScope.Push("flush")) {
						Check("three levels join", LogScope.Current, "sync/parts/flush");
					}
					Check("innermost popped", LogScope.Current, "sync/parts");
				}
				Check("middle popped", LogScope.Current, "sync");
			}
			Check("fully released", LogScope.Current, "");
			Check("depth back to 0", LogScope.Depth, 0);

			// Çift dispose zararsız olmalı: Fsm hook'larında erken dönüşler olağan.
			IDisposable a = LogScope.Push("x");
			a.Dispose();
			a.Dispose();
			Check("double dispose is safe", LogScope.Current, "");

			// Boş ad kapsam açmaz.
			using (LogScope.Push("")) {
				Check("empty name opens nothing", LogScope.Current, "");
			}
			using (LogScope.Push(null)) {
				Check("null name opens nothing", LogScope.Current, "");
			}
			Check("still clear", LogScope.Depth, 0);

			// Derinlik sınırı: sonsuz iç içe using yığını belleği şişirmesin.
			List<IDisposable> scopes = new List<IDisposable>();
			for (int i = 0; i < LogScope.MAX_DEPTH + 5; i++) scopes.Add(LogScope.Push("d" + i));
			Check("depth capped", LogScope.Depth <= LogScope.MAX_DEPTH, true);
			for (int i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose();
			Check("all disposed", LogScope.Depth, 0);
		}

		/// <summary>
		/// Tekrar katlama. Log şişmesinin ana sebebi aynı satırın yüzlerce kez
		/// yazılması; sayısı korunmalı ama tekrarı değil.
		/// </summary>
		static void RunLogDedup() {
			Console.WriteLine("--- dedupe ---");
			LogDedup.Reset();
			LogDedup.SetClock(1000);

			int count;
			// Debug/Trace her zaman katlanır.
			Check("first debug writes", LogDedup.ShouldWrite(LogSeverity.Debug, "net", "", "send", "x", out count), true);
			Check("second debug folds", LogDedup.ShouldWrite(LogSeverity.Debug, "net", "", "send", "x", out count), false);
			Check("third debug folds", LogDedup.ShouldWrite(LogSeverity.Debug, "net", "", "send", "x", out count), false);
			Check("folded counter", LogDedup.Folded, 2);
			Check("pending keys", LogDedup.PendingKeys, 1);

			// Farklı mesajlar birbirine karışmaz.
			Check("different message writes", LogDedup.ShouldWrite(LogSeverity.Debug, "net", "", "send", "y", out count), true);
			Check("two keys now", LogDedup.PendingKeys, 2);

			// Farklı kanal aynı metin: ayrı sayılır, çünkü "hangi subsystem"
			// bilgisi kaybolmamalı.
			Check("other channel writes", LogDedup.ShouldWrite(LogSeverity.Debug, "ui", "", "send", "x", out count), true);

			// Hatalar ASLA katlanmaz.
			Check("first error writes", LogDedup.ShouldWrite(LogSeverity.Error, "net", "", "e", "boom", out count), true);
			Check("second error writes", LogDedup.ShouldWrite(LogSeverity.Error, "net", "", "e", "boom", out count), true);
			Check("third error writes", LogDedup.ShouldWrite(LogSeverity.Error, "net", "", "e", "boom", out count), true);

			// Kalıcı bilgi varsayılan olarak katlanmaz.
			Check("info writes by default", LogDedup.ShouldWrite(LogSeverity.Info, "net", "", "i", "note", out count), true);
			Check("info writes again", LogDedup.ShouldWrite(LogSeverity.Info, "net", "", "i", "note", out count), true);

			// Anahtar tablosu taşınca yeni anahtar izlenmez ama kayıt YAZILIR:
			// sessizce yutmak yanlış olur.
			for (int i = 0; i < LogDedup.MaxKeys + 10; i++) {
				LogDedup.ShouldWrite(LogSeverity.Debug, "flood", "", "send", "m" + i, out count);
			}
			Check("overflow counted", LogDedup.Overflow > 0, true);
			Check("describe mentions never-folded", LogDedup.Describe().Contains("never folded"), true);

			LogDedup.Reset();
			Check("reset clears counters", LogDedup.Folded, 0);
			Check("reset clears keys", LogDedup.PendingKeys, 0);
			Check("reset clears overflow", LogDedup.Overflow, 0);
		}

		/// <summary>
		/// Metrikler. Satır kaybolsa bile sayı doğru kalmalı; teşhis "kaç kez
		/// oldu" sorusunu satır sayarak değil buradan cevaplıyoruz.
		/// </summary>
		static void RunLogMetrics() {
			Console.WriteLine("--- metrics ---");
			LogMetrics.Reset();
			Check("starts empty", LogMetrics.Dump().Contains("none recorded"), true);

			LogMetrics.Count("packets.out", 3);
			LogMetrics.Count("packets.out", 2);
			Check("counter adds up", LogMetrics.Value("packets.out"), 5L);
			Check("unknown counter is 0", LogMetrics.Value("nope"), 0L);

			LogMetrics.Gauge("ping.ms", 42);
			Check("gauge readable", LogMetrics.Last("ping.ms"), 42.0);

			LogMetrics.Time("flush", 10);
			LogMetrics.Time("flush", 20);
			LogMetrics.Time("flush", 30);
			string row = LogMetrics.Row("flush");
			Check("timer count", row.Contains("n=3"), true);
			Check("timer average", row.Contains("avg=20"), true);
			Check("timer minimum", row.Contains("min=10"), true);
			Check("timer maximum", row.Contains("max=30"), true);

			// Ad sınırı: nesne yolu olan bir teşhis döngüsü sözlüğü
			// sınırsız büyütürdü.
			for (int i = 0; i < LogMetrics.MAX_NAMES + 20; i++) LogMetrics.Count("k" + i, 1);
			Check("name limit counted", LogMetrics.Dropped > 0, true);
			Check("dump reports the drop", LogMetrics.Dump().Contains("dropped"), true);

			LogMetrics.Reset();
			Check("reset clears", LogMetrics.Value("packets.out"), 0L);
		}

		/// <summary>
		/// Dosya tamponu. Satır başına disk yazma çağrısı yapmak kare süresine
		/// bulaşıyordu; artık kritik olmayan satırlar biriktirilip kare başına
		/// boşaltılıyor. Kritik satırlar yine de ANINDA inmeli, çünkü işlenmeden
		/// çöken oyunda tek kalacak bilgi onlar.
		///
		/// Okuma PAYLAŞIMLI açılışla yapılıyor: dosya yazıcı açıkken
		/// File.ReadAllText paylaşım kuralına takılır ve test, üretimde çalışan
		/// bir durumu ölçemez hale gelirdi.
		/// </summary>
		static void RunLogBuffering() {
			Console.WriteLine("--- file buffering ---");
			string dir = Path.Combine(Path.GetTempPath(), "mscmp-buftest");
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
			Directory.CreateDirectory(dir);

			string oldDir = SmpLog.Directory;
			SmpLog.Directory = dir;
			SmpLog.Restart();
			string path = SmpLog.Path;
			Check("directory override honoured", Path.GetDirectoryName(path), dir);
			Check("file open", LogFile.Enabled, true);
			Check("file exists", File.Exists(path), true);

			// Teşhis satırı tamponlanır: dosyada henüz görünmemeli.
			SmpLog.Debug("net", "send", "buffered line");
			Check("buffered, not on disk yet", ReadShared(path).Contains("buffered line"), false);
			Check("it is queued", LogFile.PendingLines > 0, true);

			// Boşaltma.
			SmpLog.Pump();
			Check("pumped to disk", ReadShared(path).Contains("buffered line"), true);
			Check("queue drained", LogFile.PendingLines, 0);

			// Kritik satır tampona GİRMEZ.
			int queuedBefore = LogFile.PendingLines;
			SmpLog.Error("real failure");
			Check("critical line skips the queue", LogFile.PendingLines, queuedBefore);
			Check("critical line is on disk at once",
				ReadShared(path).Contains("real failure"), true);

			// Dosya satırının biçimi geriye dönük uyumlu kalmalı: iki oyuncu
			// arasında karşılaştırılan logun ayrıştırılabilirliği buna bağlı.
			SmpLog.WriteChannel(LogSeverity.Warn, LogKind.Warn, "net", "send",
				"warned", new LogFields().Add("n", 2).Add("ok", true));
			SmpLog.Flush();
			string body = ReadShared(path);
			Check("level tag present", body.Contains("[warn]"), true);
			Check("error tag is not info", body.Contains("[error]"), true);
			Check("state tag is not info", body.Contains("[state]"), true);
			Check("debug tag is not info", body.Contains("[debug]"), true);
			Check("channel present", body.Contains("net [MSCSteamMP]"), true);
			// Etiketsiz mesajda [MSCSteamMP] hemen metne bitişik kalmalı: birçok
			// grep buna bağlı. Etiketli mesajda araya etiket giriyor, o ayrı.
			SmpLog.WriteChannel(LogSeverity.Info, LogKind.Info, "net", null, "plain", null);
			SmpLog.Flush();
			body = ReadShared(path);
			Check("mod prefix stays adjacent to the message",
				body.Contains("[MSCSteamMP] plain"), true);
			Check("event goes between prefix and message",
				body.Contains("[MSCSteamMP] send warned"), true);
			Check("event tag present", body.Contains("send warned"), true);
			Check("fields are quoted", body.Contains("n=\"2\""), true);
			Check("bool field", body.Contains("ok=\"1\""), true);

			// Oturum başlığı build ortamını yazmalı: "iki taraf aynı build mi?"
			// sorusu dosyanın en başına bakmayla cevaplanmalı.
			Check("header records the build", body.Contains("build"), true);
			Check("header records the machine", body.Contains("machine"), true);
			Check("header records the protocol", body.Contains("proto"), true);
			Check("header records the levels", body.Contains("log levels"), true);

			// Çok satırlı mesaj: konsör satır satır görmeli (kaydırma buna
			// dayanıyor), dosyada ise tek kayıt olarak kalmalı.
			int rowsBefore = SmpLog.LineCount;
			SmpLog.Print("first\nsecond");
			Check("multi-line becomes 2 console rows", SmpLog.LineCount - rowsBefore, 2);

			// Kapsam: "hangi işlem?" sorusunu otomatik taşır.
			using (LogScope.Push("sync")) {
				SmpLog.WriteChannel(LogSeverity.Info, LogKind.Info, "net", "send", "scoped", null);
			}
			SmpLog.Flush();
			Check("scope reaches the file", ReadShared(path).Contains("sync/send scoped"), true);

			// Kapanışta özet yazılır: "bu oturumda ne oldu" bilgisi dosyada kalır.
			SmpLog.Close();
			body = File.ReadAllText(path);
			Check("footer written on close", body.Contains("session summary"), true);
			Check("footer has the metrics", body.Contains("metrics"), true);
			Check("footer has the dedupe line", body.Contains("dedupe"), true);
			Check("file closed", LogFile.Enabled, false);

			// Dosya kapalıyken yazmak hata vermemeli (sessizce kaybolmak da
			// kabul edilemez ama konsör kaydı yine de tutulmalı).
			int rowsAfterClose = SmpLog.LineCount;
			SmpLog.Print("after close");
			SmpLog.Pump();
			Check("writing after close does not throw", true, true);
			Check("the console still records it", SmpLog.LineCount - rowsAfterClose, 1);

			// Sağlık kontrolü dosya kapalıyken sorunu bildirmeli.
			Check("closed file says so", LogFile.Describe().StartsWith("closed"), true);
			Check("closed file explains why", LogFile.Problem, "closed");
			string[] problems = LogEngine.Problems();
			Check("health flags the closed file", problems.Length > 0, true);
			Check("health explains why",
				string.Join(" ", problems).Contains("not open"), true);

			Directory.Delete(dir, true);
			SmpLog.Directory = oldDir;
		}

		/// <summary>Yazıcı açıkken okuma: paylaşım kuralına takılmaz.</summary>
		static string ReadShared(string path) {
			using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (StreamReader r = new StreamReader(fs)) {
				return r.ReadToEnd();
			}
		}

		/// <summary>
		/// Komut ayrıştırıcı: / öneki, tırnaklı argüman ve boş satır.
		///
		/// Kullanıcı bildirimi: "npc Ahmet" yazınca NPC gelmiyordu ve
		/// "komut çalışıyor mu bilmiyorum" deniyordu. Bu test ayrıştırma
		/// katmanının doğru olduğunu ve her komutun GÖRÜNÜR bir sonuç
		/// döndürdüğünü doğrular (SmpCommandParser.Execute artık string
		/// döndürüyor).
		/// </summary>
		static void RunCommandParser() {
			Console.WriteLine("--- command parser ---");

			// Execute artık void değil, sonuç döndürüyor.
			MethodInfo exec = typeof(SmpCommandParser).GetMethod("Execute",
				new Type[] { typeof(string) });
			Check("Execute(string) exists", exec != null, true);
			Check("Execute returns a result", exec != null && exec.ReturnType == typeof(string), true);

			if (exec != null) {
				// Boş girdi: çökmez, null döner.
				string r0 = exec.Invoke(null, new object[] { "" }) as string;
				Check("empty line returns null", r0, null);
				string r1 = exec.Invoke(null, new object[] { "   " }) as string;
				Check("blank line returns null", r1, null);
				string r2 = exec.Invoke(null, new object[] { "/" }) as string;
				Check("slash only returns null", r2, null);
				string r3 = exec.Invoke(null, new object[] { null }) as string;
				Check("null line returns null", r3, null);

				// Tanımsız komut istisna atmamalı.
				string r4 = null;
				bool threw = false;
				try { r4 = exec.Invoke(null, new object[] { "zzzqqq" }) as string; }
				catch (TargetInvocationException e) { threw = true; r4 = e.InnerException != null
					? e.InnerException.Message : "?"; }
				Check("unknown command survives", threw, false);
				// Menü açık değil, komut yürütücüsü yok: kullanıcıya
				// anlaşılır bir mesaj döner (null değil).
				Check("unknown command explains itself",
					!string.IsNullOrEmpty(r4), true);
			}
		}

		/// <summary>
		/// Log katmanı: dosya + ekran konsörü halka tamponu.
		///
		/// Masaüstü CLR'de çalışabildiği için gerçekten burada doğrulanabilen tek
		/// yeni parça bu. Doğrulanması gerekenler:
		///
		///   * Dosya gerçekten oluşuyor ve satırları içeriyor.
		///   * Çok satırlı mesaj konsörde satırlara bölünüyor (kaydırma satır
		///     bazında çalışsın diye).
		///   * Halka tampon SABİT boyutlu: kapasiteyi aşan satırlar eskileri
		///     düşürüyor, bellek şişmiyor. Konsör 800 satır tutuyor ve saniyede
		///     60 kez çiziliyor; sınırsız büyüyen bir liste oyunu kaç saatte
		///     yavaşlatırdı.
		///   * Tampon dolduğunda EN ESKİ satır düşüyor, en yeniler kalıyor. Bu
		///     yanlışsa kullanıcının ekranda gördüğü log yanlış olur.
		/// </summary>
		/// <summary>
	/// SceneDump.SplitRoot: path-to-object resolution.
	///
	/// LIVE BUG THIS COVERS (client session 22:56): the call sites did
	///     int slash = SceneDump.RootNameOf(path).Length;
	///     string rest = slash > 0 ? path.Substring(slash + 1) : null;
	/// RootNameOf returns the whole path when the path IS exactly a scene
	/// root name (the `path == n` branch), so .Length == path.Length and
	/// Substring(len + 1) threw ArgumentOutOfRangeException with
	/// Parameter name: startIndex -- about eight times a second, which
	/// silently dropped every carry packet and buried the real log lines.
	/// A path with no '/' at all hit it through the fallback branch too.
	/// SplitRoot does the bounds check in one place; this pins it down.
	/// No GameObject is touched: with no live scene the root list is empty
	/// and RootNameOf falls back to the first-slash rule.
	/// </summary>
	static void RunPathSplit() {
		Console.WriteLine("[path split]");
		string root, rest;

		// The regression itself: a bare scene-root name used to throw here.
		SceneDump.SplitRoot("PLAYER", "PLAYER", out root, out rest);
		Check("bare root name keeps the root", root, "PLAYER");
		Check("bare root name has no rest", rest, null);

		// A name with no slash at all: same crash through the fallback branch.
		SceneDump.SplitRoot("hand", "hand", out root, out rest);
		Check("slashless path keeps the root", root, "hand");
		Check("slashless path has no rest", rest, null);

		// Normal nested path still splits at the root boundary.
		SceneDump.SplitRoot("PLAYER/Pivot/hand", "PLAYER", out root, out rest);
		Check("nested root", root, "PLAYER");
		Check("nested rest", rest, "Pivot/hand");

		// A root whose own name contains '/' (the GIFU case): the root name
		// runs past a slash inside it and must survive intact.
		SceneDump.SplitRoot("GIFU(750/450psi)/LOD/door",
			"GIFU(750/450psi)", out root, out rest);
		Check("slash-in-root root", root, "GIFU(750/450psi)");
		Check("slash-in-root rest", rest, "LOD/door");

		// Root name that matches nothing: the first-slash fallback applies and
		// must still not throw on a slashless path.
		SceneDump.SplitRoot("PLAYER/Pivot/hand", null, out root, out rest);
		Check("unknown root falls back", root, "PLAYER");
		Check("unknown root rest", rest, "Pivot/hand");
		SceneDump.SplitRoot("wrench", null, out root, out rest);
		Check("unknown root slashless keeps all", root, "wrench");
		Check("unknown root slashless has no rest", rest, null);

		// A stale root list can name a root LONGER than the path. That must
		// be ignored, not trusted into an out-of-range Substring.
		SceneDump.SplitRoot("PLAYER", "SATSUMA(557kg, 248)", out root, out rest);
		Check("overlong root ignored", root, "PLAYER");
		Check("overlong root has no rest", rest, null);

		// Empty and null must not throw.
		SceneDump.SplitRoot("", "", out root, out rest);
		Check("empty path root", root, "");
		Check("empty path rest", rest, null);
		SceneDump.SplitRoot(null, null, out root, out rest);
		Check("null path root", root, null);
		Check("null path rest", rest, null);
	}

	static void RunLogging() {
			string dir = Path.Combine(Path.GetTempPath(), "mscmp-logtest");
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
			Directory.CreateDirectory(dir);

			string oldDir = SmpLog.Directory;
			SmpLog.Directory = dir;
			SmpLog.Restart();

			Check("log file is mscmp.txt", Path.GetFileName(SmpLog.Path), "mscmp.txt");
			Check("log file created", File.Exists(SmpLog.Path), true);

			SmpLog.Print("hello");
			SmpLog.Warning("careful");
			SmpLog.Error("broken");
			SmpLog.State("connected");
			// Close, not just Flush: an open StreamWriter keeps the file locked and
			// the read below would fail with a sharing violation.
			SmpLog.Close();

			string body = File.ReadAllText(SmpLog.Path);
			Check("file has info line", body.Contains("[info]") && body.Contains("hello"), true);
			Check("file has warn line", body.Contains("[warn]") && body.Contains("careful"), true);
			Check("file has error line", body.Contains("[error]") && body.Contains("broken"), true);
			Check("file has state line", body.Contains("[state]") && body.Contains("connected"), true);
			// The prefix is added by the logger, not by 190 call sites; a call site
			// that forgets it would otherwise be unlabelled in the shared file.
			Check("every line carries the mod prefix", body.Contains("[MSCSteamMP] hello"), true);

			// Multi-line messages must become separate console rows, otherwise the
			// console's fixed line height is wrong and scrolling skips lines.
			int before = SmpLog.LineCount;
			SmpLog.Print("one\ntwo\nthree");
			Check("multi-line splits into 3 rows", SmpLog.LineCount - before, 3);

			// Ring buffer overflow: capacity is fixed, the oldest goes first.
			int overflow = SmpLog.CAPACITY + 250;
			for (int i = 0; i < overflow; i++) SmpLog.Log("flood " + i);
			Check("buffer capped at CAPACITY", SmpLog.LineCount, SmpLog.CAPACITY);
			Check("total counter keeps counting", SmpLog.Total >= overflow, true);

			// The newest line must be the last row, not the first.
			string stamp, text;
			LogKind level;
			SmpLog.GetLine(SmpLog.LineCount - 1, out stamp, out level, out text);
			Check("newest row is last", text, "[MSCSteamMP] flood " + (overflow - 1));

			// The newest must not have been the one evicted.
			SmpLog.GetLine(0, out stamp, out level, out text);
			Check("oldest row was evicted, not newest", text != "[MSCSteamMP] flood 0", true);

			// Levels survive the round trip so the console can colour them.
			SmpLog.GetLine(SmpLog.LineCount - 1, out stamp, out level, out text);
			Check("level preserved", level, LogKind.Info);
			Check("stamp is a time, not empty", stamp.Length >= 8, true);

			RunRowHeights();
			RunGameLogCapture(dir);

			SmpLog.Close();
			Directory.Delete(dir, true);
			SmpLog.Directory = oldDir;
		}

		/// <summary>
		/// Konsörün satır yüksekliği önbelleği.
		///
		/// Çok satırlı kayıtlar 15 piksele sığmadığı için yükseklik metinden
		/// ölçülüyor ve kayda yazılıyor. Kritik nokta: ölçülen yüksekliğin metinle
		/// BİRLİKTE halkada taşınması. Taşınmazsa, tampon dolup en eski kayıtlar
		/// düştüğünde yanlış satırın yüksekliği yeni satıra yapışır ve konsörde
		/// kayıtlar üst üste binmeye başlar.
		///
		/// Burada iki kaydı farklı yükseklikle işaretleyip tamponu taşırıyoruz;
		/// taşma sonrası işaretlerin DOĞRU metinlerle birlikte geldiğini
		/// doğruluyoruz.
		/// </summary>
		static void RunRowHeights() {
			// Restart bir oturum başlığı satırı yazar, yani tampon boş değil.
			SmpLog.Restart();
			Check("restart leaves the session header in the buffer", SmpLog.LineCount >= 1, true);

			// Önce tamponu doldur ki işaretli kayıt halkanın SONUna gelsin:
			// halka başa sarmadıkça kayıt yerinde duruyor, yani "yükseklik metinle
			// birlikte taşındı mı" sorusu ancak kayıt yer değiştirdiğinde anlamlı.
			while (SmpLog.LineCount < SmpLog.CAPACITY - 1) SmpLog.Log("filler");

			SmpLog.Print("TALL");
			SmpLog.Print("short");
			SmpLog.Print("TALL2");

			// Ucu de YAZILDIKTAN SONRA araniyor. Yazdiktan hemen sonra ararsak
			// her biri en yeni satiri bulur (halka dolu oldugu icin daima son
			// indeks) ve ucu de ayni yeri dondurur.
			int tallAt = IndexOfRow("[MSCSteamMP] TALL");
			int shortAt = IndexOfRow("[MSCSteamMP] short");
			int tall2At = IndexOfRow("[MSCSteamMP] TALL2");
			Check("all three rows are findable", tallAt >= 0 && shortAt >= 0 && tall2At >= 0, true);
			Check("they are three rows apart", tall2At - tallAt, 2);
			if (tallAt < 0 || tall2At < 0 || shortAt < 0) return;

			SmpLog.SetHeight(tallAt, 40f);
			SmpLog.SetHeight(tall2At, 55f);
			Check("height round-trips", SmpLog.GetHeight(tallAt), 40f);
			Check("second height round-trips", SmpLog.GetHeight(tall2At), 55f);
			Check("unmeasured height is zero", SmpLog.GetHeight(shortAt), 0f);

			// TALL'in yasi su an 2 (short, TALL2). Tampon en eski CAPACITY-1 kaydi
			// tuttugu icin TALL'i 0. indekse (en eski) tasimak icin geriye
			// CAPACITY-3 kayit daha yazmamiz gerekiyor.
			for (int i = 0; i < SmpLog.CAPACITY - 3; i++) SmpLog.Log("wrap " + i);

			int movedTall = IndexOfRow("[MSCSteamMP] TALL");
			int movedTall2 = IndexOfRow("[MSCSteamMP] TALL2");
			Check("the wrapped row is now the oldest", movedTall, 0);
			Check("the second tall row is right after it", movedTall2, 2);
			if (movedTall2 < 0) return;

			// Asil iddia: yukseklik metinle BIRLIKTE tasindi, yanlis satira
			// yapismadi. Yapisaydi 0. siradaki "wrap" kaydi 40 piksel alirdi.
			Check("first height travelled with its row", SmpLog.GetHeight(movedTall), 40f);
			Check("second height travelled with its row", SmpLog.GetHeight(movedTall2), 55f);
			Check("heights did not swap", SmpLog.GetHeight(movedTall) != SmpLog.GetHeight(movedTall2), true);
			Check("a fresh row is still unmeasured", SmpLog.GetHeight(SmpLog.LineCount - 1), 0f);
		}

		/// <summary>Metnine göre satır indeksi, yoksa -1.</summary>
		static int IndexOfRow(string text) {
			for (int i = 0; i < SmpLog.LineCount; i++) {
				string stamp, line;
				LogKind kind;
				SmpLog.GetLine(i, out stamp, out kind, out line);
				if (line == text) return i;
			}
			return -1;
		}

		/// <summary>
		/// Oyun/MSCLoader loglarının Mods klasörüne yönlendirilmesi.
		///
		/// Konsol yazarı elle test edilebilir: GameLogCapture.TeeWriter iki hedefe
		/// birden yazar ve TAM SATIR yazmayı bekler. Yarım satır yazılırsa aynı
		/// satır iki kez dosyaya düşer ve konsol çıktısı arasına karışır; bu yüzden
		/// Write(char) yalnızca satır sonunda akıtıyor.
		///
		/// Doğrulanması gerekenler:
		///   * Console.WriteLine her iki hedefe de gidiyor
		///   * Write(char) tek tek çağrıldığında da satır bütün çıkıyor
		///   * System.IO.File.WriteLine, ki bu ikinci hedefe DOĞRUDAN yazıyor
		///     (yani Console.SetOut'a hiç uğramadan dosyaya iner)
		///   * Ayrı dosyalar: oyunun logu ile modun logu karışmıyor
		/// </summary>
		static void RunGameLogCapture(string dir) {
			string consolePath = System.IO.Path.Combine(dir, "mscmlog.txt");
			// FileShare.ReadWrite, çünkü üretimde de GameLogCapture dosyayı
			// paylaşımlı açıyor: hem output_log.txt'nin yazıcısı hem bizim
			// dosyamız aynı anda açık kalmalı.
			System.IO.TextWriter sink = new System.IO.StreamWriter(
				new System.IO.FileStream(consolePath, System.IO.FileMode.Create,
					System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite));
			sink.WriteLine("baseline");

			System.IO.TextWriter tee = new SteamMP.GameLogCapture.TeeWriter(sink, sink);
			tee.WriteLine("through the tee");
			tee.Write("abc");
			tee.Write("def");
			tee.Write("\n");
			tee.Write("no newline at the end");
			tee.Flush();
			sink.Flush();
			// Dosyayı okumadan önce kapat: Windows'ta açık bir yazıcı, paylaşım
			// biçimi ne olursa olsun File.ReadAllText'in açılmasını engelleyebilir.
			sink.Close();

			string body = System.IO.File.ReadAllText(consolePath);
			Check("tee writes a whole line", body.Contains("through the tee"), true);
			Check("tee assembles Write(char) calls into one line", body.Contains("abcdef"), true);
			Check("tee keeps the trailing partial line", body.Contains("no newline at the end"), true);
			Check("baseline line survives", body.Contains("baseline"), true);

			// Doğrudan dosyaya yazan yol: Unity'nin Debug.Log'u bu yoldan geçiyor
			// (GameLogCapture.OnLog -> kendi StreamWriter'ı).
			string unityPath = System.IO.Path.Combine(dir, "unitylog.txt");
			using (System.IO.TextWriter u = new System.IO.StreamWriter(unityPath, false)) {
				u.WriteLine("12:00:00.000 [Error] something broke");
				u.WriteLine("    at Some.Type.Method()");
			}
			string unity = System.IO.File.ReadAllText(unityPath);
			Check("unity log has the level", unity.Contains("[Error]"), true);
			Check("unity log has the message", unity.Contains("something broke"), true);
			Check("unity log has the stack trace", unity.Contains("Some.Type.Method"), true);
		}

		/// <summary>
		/// F3 konsörünün komut köprüsü.
		///
		/// Mod komutlarını çalıştırmak için MSCLoader'ın İKİ internal katmanına
		/// yansımayla uzanıyoruz:
		///
		///   ConsoleCommand.cc             internal static ConsoleController
		///   ConsoleController             internal sınıf
		///   ConsoleController.RunCommandString(string)
		///   ConsoleController.commands    internal Dictionary
		///
		/// Bu, oyunda test edilemeyen en kırılgan nokta: MSCLoader bir alanı
		/// yeniden adlandırırsa konsör sessizce hiçbir komut çalıştırmaz ve
		/// kullanıcı "/npc çalışmıyor" der. Yansımanın ADLARI doğru bulduğunu
		/// masaüstünde doğrulayabiliyoruz.
		///
		/// Alanın DEĞERİ masaüstünde null (MSCLoader menü yüklenirken atıyor), o
		/// yüzden yalnızca bulma doğrulanıyor, çalıştırma değil.
		/// </summary>
		static void RunCommandBridge() {
			Type commandType = typeof(MSCLoader.ConsoleCommand);

			FieldInfo cc = null;
			foreach (FieldInfo f in commandType.GetFields(
					BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)) {
				if (f.FieldType != null && f.FieldType.Name.EndsWith("ConsoleController", StringComparison.Ordinal)) {
					cc = f;
					break;
				}
			}
			Check("ConsoleCommand controller field found", cc != null, true);
			if (cc == null) return;

			Type controllerType = cc.FieldType;
			Check("controller type name is stable", controllerType.Name, "ConsoleController");

			MethodInfo run = controllerType.GetMethod("RunCommandString",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
				null, new Type[] { typeof(string) }, null);
			Check("RunCommandString found", run != null, true);
			if (run != null) Check("RunCommandString takes one string", run.GetParameters().Length, 1);

			FieldInfo commands = controllerType.GetField("commands",
				BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
			Check("commands dictionary field found", commands != null, true);
			if (commands != null) {
				Check("commands is a dictionary", commands.FieldType.Name.StartsWith("Dictionary"), true);
			}

			// The bridge's own probe must agree with this. It is what runs in game.
			Check("bridge sees the controller as unavailable here", SteamMP.ModCommands.Available, false);
			Check("bridge explains why", SteamMP.ModCommands.Unavailable.Length > 0, true);
			Check("bridge finds no commands before load", SteamMP.ModCommands.KnownCommands().Length, 0);

			// A leading slash is optional; both spellings must reach the same code.
			// Run() is called with no controller available, so this exercises the
			// guard path rather than dispatch: it must not throw.
			try {
				SteamMP.ModCommands.Run("/npc Ahmet");
				SteamMP.ModCommands.Run("npc Ahmet");
				SteamMP.ModCommands.Run("");
				SteamMP.ModCommands.Run(null);
				Check("bridge survives being called without a controller", true, true);
			}
			catch (Exception e) {
				Check("bridge survives being called without a controller", e.GetType().Name, "(no exception)");
			}

			// Registration must not throw even though it happens before the menu
			// exists; a throw here would take the whole mod down at load.
			try {
				SteamMP.ModCommands.RegisterAll();
				Check("RegisterAll does not throw at load time", true, true);
			}
			catch (Exception e) {
				Check("RegisterAll does not throw at load time", e.GetType().Name, "(no exception)");
			}
		}

		/// <summary>Manual clock so the rate limiter can be stepped deterministically.</summary>
		static float fakeNow = 100f;

		static SatsumaPart MakePart(string id, params int[] stages) {
			SatsumaPart p = new SatsumaPart();
			p.Id = id;
			p.Name = id;
			for (int i = 0; i < stages.Length; i++) p.Bolts.Add(MakeBolt(p, (byte)i, stages[i], stages[i]));
			return p;
		}

		static SatsumaBolt MakeBolt(SatsumaPart part, byte index, int stage, float tight) {
			SatsumaBolt b = new SatsumaBolt();
			b.Part = part;
			b.Index = index;
			b.Stage = new FsmInt();
			b.Stage.Name = "Stage";
			b.Stage.Value = stage;
			b.Tightness = new FsmFloat();
			b.Tightness.Name = "Tightness";
			b.Tightness.Value = tight;
			return b;
		}

		/// <summary>Resets a bolt to "never sent", which is what a freshly scanned bolt is.</summary>
		static void MarkUnsent(SatsumaBolt b, bool dirty) {
			b.LastSentStage = -1;
			b.LastSentTightness = float.NaN;
			b.LastSentAt = float.NegativeInfinity;
			b.Dirty = dirty;
		}

		/// <summary>
		/// Installs parts into the real registry through the same entry point Install() uses,
		/// then FinaliseTable(), so BoltSync walks the production table rather than a copy.
		/// The registry owns the entry objects, so the registered instances are returned.
		/// No GameObject is needed: the batching logic only touches the parts.
		/// </summary>
		static SatsumaPart[] InstallTable(params SatsumaPart[] wanted) {
			// Clear() reaches SatsumaRegistry.StopRunningSync, which holds the only
			// Unity internal call in the reset path (StopCoroutine). The desktop CLR
			// refuses to JIT a method containing an icall, so the SecurityException is
			// thrown before a single instruction runs -- an in-method try/catch cannot
			// reach it. Every other Unity-touching call in this probe is routed through
			// Call() for the same reason; Clear() was not.
			Call("clear", delegate { SatsumaRegistry.Clear(); });
			MethodInfo register = typeof(SatsumaRegistry).GetMethod("Register",
				BindingFlags.NonPublic | BindingFlags.Static);
			MethodInfo finalise = typeof(SatsumaRegistry).GetMethod("FinaliseTable",
				BindingFlags.NonPublic | BindingFlags.Static);
			if (register == null || finalise == null) throw new Exception("registry seam missing");
			SatsumaPart[] got = new SatsumaPart[wanted.Length];
			for (int i = 0; i < wanted.Length; i++) {
				got[i] = (SatsumaPart)register.Invoke(null, new object[] { wanted[i].Id, wanted[i].Name, null });
				// Carry the caller's bolts over, as Attach/BuildBolt would in the real scan.
				got[i].Bolts.AddRange(wanted[i].Bolts);
				for (int b = 0; b < wanted[i].Bolts.Count; b++) wanted[i].Bolts[b].Part = got[i];
			}
			finalise.Invoke(null, null);
			return got;
		}

		/// <summary>
		/// Inbound bolt apply with a populated table. The Assembly/Removal FSMs are null
		/// (no scene here), so this exercises the resolution and range checks, not the FSM
		/// writes. What it does prove: the right bolt object is targeted, and an out of range
		/// index is dropped instead of clamping onto another bolt.
		/// </summary>
		static void RunInboundBoltApply() {
			Console.WriteLine("--- inbound bolt apply ---");
			SatsumaPart p = InstallTable(MakePart("Chassis/FL/strut fl(xxxxx)", 0, 0, 0))[0];
			p.Installable = true;

			int mismatchedBefore = Telemetry.MismatchedBolts;
			int appliedBefore = Telemetry.AppliedBolts;

			Call("bolt 0", delegate { SatsumaRegistry.ApplyBolt(p.Id, 0, 5.5f, 5, false); });
			Check("bolt 0 stage applied", p.Bolts[0].StageValue, 5);
			Check("bolt 0 tightness applied", p.Bolts[0].TightnessValue, 5.5f);
			Check("bolt 0 counted", Telemetry.AppliedBolts - appliedBefore, 1);
			Check("bolt 0 marked sent", p.Bolts[0].LastSentStage, 5);

			Call("bolt 2", delegate { SatsumaRegistry.ApplyBolt(p.Id, 2, 8f, 8, true); });
			Check("bolt 2 stage applied", p.Bolts[2].StageValue, 8);

			// Out of range: must be dropped, not clamped onto bolt 2.
			Call("bolt 9 (out of range)", delegate { SatsumaRegistry.ApplyBolt(p.Id, 9, 3f, 3, false); });
			Check("out of range counted", Telemetry.MismatchedBolts - mismatchedBefore, 1);
			Check("out of range did not touch bolt 2", p.Bolts[2].StageValue, 8);
			Check("out of range not applied", Telemetry.AppliedBolts - appliedBefore, 2);

			// A bolt-less entry is a no-op rather than a crash.
			SatsumaPart empty = InstallTable(p, MakePart("Bolts"))[1];
			Check("bolt-less entry created", empty.Bolts.Count, 0);
			Call("bolt on empty entry", delegate { SatsumaRegistry.ApplyBolt(empty.Id, 0, 1f, 1, false); });
			Check("empty entry ignored", Telemetry.AppliedBolts - appliedBefore, 2);

			// Re-applying the same value must not mark anything for sending.
			Check("bolt 0 clean after apply", p.Bolts[0].Dirty, false);
			Check("bolt 0 not resendable", p.Bolts[0].LastSentTightness, 5.5f);
		}

		static int TableCount { get { return SatsumaRegistry.PartCount; } }

		// ------------------------------------------------------------------

		static void RunFilters() {
			Console.WriteLine("--- event filter ---");
			Check("ASSEMBLE", AssemblyPatch.IsPartEvent("ASSEMBLE"), true);
			Check("REMOVE", AssemblyPatch.IsPartEvent("REMOVE"), true);
			Check("assemble (lower)", AssemblyPatch.IsPartEvent("assemble"), true);
			Check("FINISHED dropped", AssemblyPatch.IsPartEvent("FINISHED"), false);
			Check("TIGHTEN dropped", AssemblyPatch.IsPartEvent("TIGHTEN"), false);
			Check("UNTIGHTEN dropped", AssemblyPatch.IsPartEvent("UNTIGHTEN"), false);
			Check("SAVEGAME dropped", AssemblyPatch.IsPartEvent("SAVEGAME"), false);
			Check("CarBuilding dropped", AssemblyPatch.IsPartEvent("CarBuilding"), false);
			Check("OFF dropped", AssemblyPatch.IsPartEvent("OFF"), false);
			// HOTINSTALL is absent from the current build's FSMs, so it is not replicated.
			Check("HOTINSTALL not sent", AssemblyPatch.IsPartEvent("HOTINSTALL"), false);
			Check("null dropped", AssemblyPatch.IsPartEvent(null), false);
			Check("empty dropped", AssemblyPatch.IsPartEvent(""), false);
			Check("Assembly fsm", AssemblyPatch.IsPartFsm("Assembly"), true);
			Check("Removal fsm", AssemblyPatch.IsPartFsm("Removal"), true);
			Check("Assemble fsm", AssemblyPatch.IsPartFsm("Assemble"), true);
			Check("BoltCheck not a part fsm", AssemblyPatch.IsPartFsm("BoltCheck"), false);
			Check("Screw not a part fsm", AssemblyPatch.IsPartFsm("Screw"), false);
		}

		static void RunEchoGuard() {
			Console.WriteLine("--- echo guard ---");
			Check("starts clear", SatsumaRegistry.Suppressed, false);
			SatsumaRegistry.SuppressScope a = new SatsumaRegistry.SuppressScope();
			Check("set inside scope", SatsumaRegistry.Suppressed, true);
			SatsumaRegistry.SuppressScope b = new SatsumaRegistry.SuppressScope();
			SatsumaRegistry.SuppressScope c = new SatsumaRegistry.SuppressScope();
			a.Dispose();
			Check("outer dispose does not clear inner", SatsumaRegistry.Suppressed, true);
			b.Dispose();
			Check("one inner left", SatsumaRegistry.Suppressed, true);
			c.Dispose();
			Check("fully released", SatsumaRegistry.Suppressed, false);
		}

		static void RunLiveBoltCapture() {
			Console.WriteLine("--- live bolt batching ---");
			SatsumaPart p = InstallTable(MakePart("part", 0, 3, 8))[0];
			Check("table has the part", TableCount, 1);
			List<SatsumaBolt> outp = new List<SatsumaBolt>();
			foreach (SatsumaBolt b in p.Bolts) MarkUnsent(b, true);

			Check("sends every dirty bolt once", BoltSync.Collect(outp, 32), 3);
			Check("list size", outp.Count, 3);
			Check("stage recorded", p.Bolts[1].LastSentStage, 3);
			Check("tightness recorded", p.Bolts[1].LastSentTightness, 3f);
			Check("dirty cleared", p.Bolts[1].Dirty, false);

			// Writing the same value again is FSM noise, not a change.
			p.Bolts[1].Stage.Value = 3;
			p.Bolts[1].Tightness.Value = 3f;
			p.Bolts[1].Dirty = true;
			outp.Clear();
			Check("same value suppressed", BoltSync.Collect(outp, 32), 0);
			Check("stays clean", p.Bolts[1].Dirty, false);

			// A real change is held back until the interval expires, then goes out. Driven by
			// the manual clock so the boundary is exact rather than timing dependent.
			p.Bolts[1].Stage.Value = 4;
			p.Bolts[1].Tightness.Value = 4f;
			p.Bolts[1].Dirty = true;
			float t0 = fakeNow;
			fakeNow = t0 + BoltSync.MIN_SEND_INTERVAL * 0.5f;
			outp.Clear();
			Check("held back by interval", BoltSync.Collect(outp, 32), 0);
			Check("still dirty while held", p.Bolts[1].Dirty, true);
			fakeNow = t0 + BoltSync.MIN_SEND_INTERVAL + 0.01f;
			outp.Clear();
			Check("sent once interval passes", BoltSync.Collect(outp, 32), 1);
			Check("newest value sent", p.Bolts[1].LastSentStage, 4);
			Check("send time recorded", p.Bolts[1].LastSentAt, fakeNow);

			// Burst behaviour: many writes between flushes collapse into one packet that
			// carries the final value, and the send rate stays at one per interval.
			float t1 = fakeNow;
			int final = 0;
			for (int i = 0; i < 40; i++) {
				final = 1 + (i % 8);
				p.Bolts[1].Stage.Value = final;
				p.Bolts[1].Tightness.Value = final;
				p.Bolts[1].Dirty = true;
			}
			fakeNow = t1 + BoltSync.MIN_SEND_INTERVAL + 0.01f;
			outp.Clear();
			Check("40 writes collapse to 1 send", BoltSync.Collect(outp, 32), 1);
			Check("burst carries the newest value", p.Bolts[1].LastSentStage, final);

			float t2 = fakeNow;
			p.Bolts[1].Stage.Value = 6;
			p.Bolts[1].Tightness.Value = 6f;
			p.Bolts[1].Dirty = true;
			fakeNow = t2 + BoltSync.MIN_SEND_INTERVAL * 0.5f;
			outp.Clear();
			Check("next change is rate limited", BoltSync.Collect(outp, 32), 0);
			Check("still dirty while rate limited", p.Bolts[1].Dirty, true);
			fakeNow = t2 + BoltSync.MIN_SEND_INTERVAL + 0.01f;
			outp.Clear();
			Check("goes out after the interval", BoltSync.Collect(outp, 32), 1);
			Check("carries its own value", p.Bolts[1].LastSentStage, 6);

			// Cap.
			SatsumaPart many = InstallTable(p, MakePart("many", 0, 0, 0, 0, 0))[1];
			Check("two parts in table", TableCount, 2);
			foreach (SatsumaBolt b in many.Bolts) MarkUnsent(b, true);
			outp.Clear();
			Check("honours cap", BoltSync.Collect(outp, 2), 2);
			Check("capped list", outp.Count, 2);
			Check("overflow stays dirty", many.Bolts[2].Dirty, true);
		}

		static void RunInitialSyncPaging() {
			Console.WriteLine("--- initial sync paging ---");
			SatsumaPart p = InstallTable(MakePart("page", 1, 2, 3, 4, 5), MakePart("q", 9))[0];
			foreach (SatsumaBolt b in p.Bolts) MarkUnsent(b, false);
			List<SatsumaBolt> page = new List<SatsumaBolt>();
			Check("page 1 has more", BoltSync.CollectAll(page, 2, 0), true);
			Check("page 1 size", page.Count, 2);
			Check("page 1 starts at 0", page[0].Index, (byte)0);
			page.Clear();
			bool more2 = BoltSync.CollectAll(page, 2, 2);
			Check("page 2 has more", more2, true);
			Check("page 2 starts at 2", page[0].Index, (byte)2);
			page.Clear();
			bool more3 = BoltSync.CollectAll(page, 2, 4);
			Check("page 3 has more", more3, true);
			Check("page 3 size", page.Count, 2);
			Check("page 3 index", page[0].Index, (byte)4);
			Check("paging marks values sent", page[0].LastSentStage, 5);
			page.Clear();
			bool more4 = BoltSync.CollectAll(page, 2, 6);
			Check("page 4 is the last", more4, false);
			Check("page 4 size", page.Count, 0);
			// Paging must not depend on the dirty flags.
			foreach (SatsumaBolt b in p.Bolts) b.Dirty = true;
			page.Clear();
			BoltSync.CollectAll(page, 10, 0);
			Check("full sweep ignores dirty", page.Count, 6);
		}

		static void RunInboundWithNoTable() {
			Console.WriteLine("--- inbound with an empty table ---");
			// Ayni neden InstallTable'daki gibi: Clear() -> StopRunningSync
			// icindeki StopCoroutine bir Unity icall'u ve metodun tamami
			// masaustu CLR'da JIT edilemiyor. try/catch buraya ulasamaz.
			Call("clear", delegate { SatsumaRegistry.Clear(); });
			Check("table empty", SatsumaRegistry.PartCount, 0);

			// Each of these logs a warning through ModConsole, which is a Unity ECall and
			// unavailable here. The counter is bumped before the log, so the assertions below
			// still verify the bookkeeping; a real throw from the sync logic would fail them.
			int before = Telemetry.UnresolvedParts;
			Call("ApplyPartState", delegate { SatsumaRegistry.ApplyPartState("nope/nope", true); });
			Check("part state counted", Telemetry.UnresolvedParts - before, 1);
			Call("ApplyBolt", delegate { SatsumaRegistry.ApplyBolt("nope/nope", 0, 5f, 5, true); });
			Check("bolt counted", Telemetry.UnresolvedParts - before, 2);
			Call("ApplyPartEvent", delegate { SatsumaRegistry.ApplyPartEvent("nope/nope", "Assembly", "ASSEMBLE"); });
			Check("part event counted", Telemetry.UnresolvedParts - before, 3);
			// The count byte is consumed by the caller (NetworkPacketManager.Handle), so the
			// reader starts at the first record, exactly as on the wire.
			MemoryStream payload = new MemoryStream();
			BinaryWriter pw = new BinaryWriter(payload);
			pw.Write("nope/nope");
			pw.Write((byte)0);
			pw.Write(1f);
			pw.Write((sbyte)1);
			pw.Write(false);
			pw.Flush();
			payload.Position = 0;
			Call("ApplyBoltBatch", delegate {
				SatsumaRegistry.ApplyBoltBatch(new BinaryReader(payload), 1);
			});
			Check("batch counted", Telemetry.UnresolvedParts - before, 4);
			Call("empty batch", delegate {
				SatsumaRegistry.ApplyPartBatch(new BinaryReader(new MemoryStream(new byte[0])), 0);
			});
			Check("empty batch is harmless", Telemetry.UnresolvedParts - before, 4);

			// Offline: asking for a sync must be a no-op, not a crash.
			// Routed through Call() for the same reason as everything else here: BeginInitialSync
			// contains a StartCoroutine call, which on the desktop CLR is a Unity internal call,
			// and the JIT refuses to compile such a method outside Unity ("ECall methods must be
			// packaged into a system module"). That SecurityException happens before a single
			// instruction of the method runs, so it says nothing about the mod's own logic.
			Call("BeginInitialSync offline", delegate {
				SatsumaRegistry.BeginInitialSync();
			});
		}

		/// <summary>Runs a call, tolerating the Unity log ECall that only fails outside the game.</summary>
		static void Call(string what, Action a) {
			try { a(); }
			catch (SecurityException) { }
			catch (Exception e) {
				failures++;
				Console.WriteLine("  FAIL " + what + " threw " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// Lobby code logic. Pure string work, so it is verified offline like the rest.
		/// What matters is that a wrong code is rejected and that the accepted set is
		/// exactly the characters the alphabet allows - a look-alike character in a code
		/// would fail on the user's own machine and read as a bug.
		/// </summary>
		static void RunLobbyCode() {
			Console.WriteLine("--- lobby code ---");
			Check("alphabet has no zero", SteamMP.LobbyCode.ALPHABET.IndexOf('0') < 0, true);
			Check("alphabet has no uppercase O", SteamMP.LobbyCode.ALPHABET.IndexOf('O') < 0, true);
			Check("alphabet has no lowercase l", SteamMP.LobbyCode.ALPHABET.IndexOf('l') < 0, true);
			Check("alphabet has no uppercase I", SteamMP.LobbyCode.ALPHABET.IndexOf('I') < 0, true);

			// A valid code built only from alphabet characters.
			string code = "ASfgw" + SteamMP.LobbyCode.ALPHABET[0];   // exactly LENGTH characters
			Check("code length", code.Length, SteamMP.LobbyCode.LENGTH);

			Check("exact match accepted", SteamMP.LobbyCode.Verify(code, code), true);
			Check("lowercase accepted", SteamMP.LobbyCode.Verify(code, code.ToLowerInvariant()), true);
			Check("spaces ignored", SteamMP.LobbyCode.Verify(code, code.Substring(0, 3) + " " + code.Substring(3)), true);

			// One character changed must fail, in every position.
			bool allRejected = true;
			for (int i = 0; i < code.Length && allRejected; i++) {
				char[] chars = code.ToCharArray();
				chars[i] = chars[i] == 'A' ? 'B' : 'A';
				allRejected = !SteamMP.LobbyCode.Verify(code, new string(chars));
			}
			Check("single-character change rejected", allRejected, true);

			Check("wrong length rejected", SteamMP.LobbyCode.Verify(code, code.Substring(1)), false);
			Check("empty rejected", SteamMP.LobbyCode.Verify(code, ""), false);
			Check("null host code rejects", SteamMP.LobbyCode.Verify(null, code), false);

			// A character outside the alphabet must be refused even at the right length,
			// otherwise typing "0" instead of "O" fails silently.
			string withZero = code.Substring(0, code.Length - 1) + "0";
			Check("character outside alphabet rejected", SteamMP.LobbyCode.Verify(code, withZero), false);

			Check("normalize trims spaces", SteamMP.LobbyCode.Normalize(" a b c "), "abc");
			Check("normalize lowercases", SteamMP.LobbyCode.Normalize("AbC"), "abc");
			Check("normalize handles null", SteamMP.LobbyCode.Normalize(null), "");

			// Oturum etiketi olarak yeni rolü. Kod artık kapı değil: oyuncu girmiyor,
			// sunucu rastgele üretiyor. Bu yüzden istemci ilk el sıkışmada kodu
			// BİLMEZ ve boş gönderir.
			//
			// Verify boş kodu reddediyor. Bu yüzden SteamTransport.VerifyHandshakeCode
			// boş gelen kodu ÖZEL olarak kabul etmek zorunda; bu iddia onun orada
			// özel durum olduğunu sabitler. Doğrulama buradan kaldırılırsa istemci
			// hiçbir lobya bağlanamaz ve bunu ancak iki oyuncuyla test ederek
			// anlarsın.
			Check("empty code is NOT accepted by Verify", SteamMP.LobbyCode.Verify(code, ""), false);
			Check("echoed code matches its own host", SteamMP.LobbyCode.Verify(code, code), true);
			Check("normalizing then verifying still works",
				  SteamMP.LobbyCode.Verify(code, SteamMP.LobbyCode.Normalize(code)), true);
		}
	}
}
