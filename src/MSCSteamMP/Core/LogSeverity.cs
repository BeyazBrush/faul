using System;

namespace SteamMP {

	/// <summary>
	/// Log CİDDİYET DERECESİ. Konsördeki <see cref="LogKind"/> rozetinden AYRI
	/// bir eksen.
	///
	/// Neden iki tane
	/// --------------
	/// Önceden dört seviye vardı (Info/State/Warn/Error) ve aynı ikisi hem filtre
	/// hem de görünüm için kullanılıyordu. Bunun iki somut zararı var:
	///
	///   1. "State" (bağlandı/ayrıldı) bir görünüm biçimi; oyuncu teşhis
	///      seviyesini açtığında da state satırlarının KAYBOLMAMASI gerekir,
	///      sadece daha çok satır gelmesi gerekir. Tek eksende bu mümkün değil.
	///   2. Teşhis (Debug/Trace) hiç yoktu. Olay başına tek satır yazmak isteyen
	///      yerler ya hiçbir şey yazmıyor ya da kalıcı bilgi seviyesine yazıp
	///      dosyayı şişiriyordu.
	///
	/// İKİ EKSENİN İŞ BÖLÜMÜ
	/// ---------------------
	///   <see cref="LogSeverity"/>  FİLTRE kararı: "bu kayıt yazılsın mı?"
	///   <see cref="LogKind"/>      GÖRÜNÜM kararı: "rozet ne renk, zemin var mı?"
	///
	/// Karıştırmanın canlı kanıtı: dosya etiketi ciddiyetten üretildiğinde
	/// uyarı ve hata satırları "[info]" olarak yazıldı. Etiket KIND'dan
	/// üretilir; ciddiyet yalnızca filtreyi besler.
	///
	/// İSİM
	/// ----
	/// "LogLevel" değil de "LogSeverity" çünkü PlayMaker'ın da bir
	/// LogLevel'i var (HutongGames.PlayMaker.LogLevel). Aynı isim, aynı
	/// dosyada iki using ile belirsizliğe düşüyordu; isim çakışması ileride
	/// başka birinin yazdığı hatayı sessizleştireceği için baştan önlendi.
	/// </summary>
	public enum LogSeverity {
		/// <summary>Kanal tamamen kapalı.</summary>
		Off = 0,
		/// <summary>Modun çalışamayacağı, oturumun bittiği hata.</summary>
		Fatal = 1,
		/// <summary>Hata. Eylem başarısız oldu, istisna atıldı.</summary>
		Error = 2,
		/// <summary>Beklenmeyen ama zararsız durum. Düzeltilmezse birikir.</summary>
		Warn = 3,
		/// <summary>Normal çalışma bilgisi. Varsayılan eşik bu.</summary>
		Info = 4,
		/// <summary>Teşhis. Paket gönderildi/alındı, sayaç değişti.</summary>
		Debug = 5,
		/// <summary>Her şey. Kare başına yüzlerce satır üretebilir.</summary>
		Trace = 6,
	}

	/// <summary>
	/// <see cref="LogSeverity"/> ile <see cref="LogKind"/> arasındaki çevirim ve
	/// metin ayrıştırma.
	///
	/// Dışarıdan yazılan metin ("debug", "TRACE", "warn") burada tek noktada
	/// çözülüyor; komut satırı, dosyadaki ayar ve el yazımı aynı sözlüğü
	/// paylaşıyor. Ayrı ayrı ayrıştırılsaydı "d" yazan biri "debug" bulurken
	/// "D" yazan bulamaz gibi sessiz farklar oluşurdu.
	/// </summary>
	public static class LogSeverities {

		/// <summary>Varsayılan eşik: Info ve üstü kalıcı dosyaya gider.</summary>
		public const LogSeverity DEFAULT = LogSeverity.Info;

		/// <summary>Ad çözümlemesi. Bilinmeyen ad <paramref name="fallback"/> döner.</summary>
		public static LogSeverity Parse(string text, LogSeverity fallback) {
			if (string.IsNullOrEmpty(text)) return fallback;
			string t = text.Trim().ToLowerInvariant();
			if (t.Length == 0) return fallback;
			switch (t[0]) {
				case 'o': return LogSeverity.Off;      // off / on (ikisi de "kapalı")
				case 'f': return LogSeverity.Fatal;
				case 'e': return LogSeverity.Error;
				case 'w': return LogSeverity.Warn;
				case 'i': return LogSeverity.Info;
				case 'd': return LogSeverity.Debug;
				case 't': return LogSeverity.Trace;
			}
			return fallback;
		}

		/// <summary>Dosyada ve konsörde görünen kısa ad.</summary>
		public static string Name(LogSeverity level) {
			switch (level) {
				case LogSeverity.Off: return "off";
				case LogSeverity.Fatal: return "fatal";
				case LogSeverity.Error: return "error";
				case LogSeverity.Warn: return "warn";
				case LogSeverity.Info: return "info";
				case LogSeverity.Debug: return "debug";
				case LogSeverity.Trace: return "trace";
			}
			return "info";
		}

		/// <summary>Sabit genişlikli ad (döküm sütunlarının hizalanması için).</summary>
		public static string Padded(LogSeverity level) {
			string n = Name(level);
			return n.Length >= 5 ? n : n.PadRight(5);
		}

		/// <summary>
		/// "a bu eşiğin altında mı?" — filtrenin TAMAMI buradan geçiyor.
		///
		/// Adı bilerek "daha gürültülü mü" değil: uygulamada kullanılan
		/// karşılaştırma bu, yani ismi de bu olmalı. Daha önce "Less" adıyla
		/// yazılmıştı ama çağrı yerleri "gürültülü" anlamında okunuyordu; ters
		/// yönde yazılmış bir test onu "her şey geçiyor" gibi gösterebilirdi.
		/// </summary>
		public static bool AtMost(LogSeverity level, LogSeverity threshold) {
			return (int)level <= (int)threshold;
		}

		/// <summary>Bu seviye dosyaya GEÇİKMEDEN yazılır mı?</summary>
		public static bool IsCritical(LogSeverity level) {
			return level == LogSeverity.Fatal || level == LogSeverity.Error
				|| level == LogSeverity.Warn;
		}

		/// <summary>Bu seviye konsörde dikkat çeker mi (renk/zemin)?</summary>
		public static bool IsNoteworthy(LogSeverity level) {
			return level == LogSeverity.Fatal || level == LogSeverity.Error
				|| level == LogSeverity.Warn;
		}

		/// <summary>
		/// Ciddiyetten rozet türüne. Debug/Trace ekranda "LOG" görünür, Fatal
		/// ise Error ile aynı kırmızı.
		/// </summary>
		public static LogKind ToKind(LogSeverity level) {
			switch (level) {
				case LogSeverity.Fatal:
				case LogSeverity.Error:
					return LogKind.Error;
				case LogSeverity.Warn:
					return LogKind.Warn;
				default:
					return LogKind.Info;
			}
		}

		/// <summary>
		/// Rozet türünden ciddiyet. ESKİ API yolu bunu kullanıyor:
		/// <c>SmpLog.Error("x")</c> yalnızca LogKind.Error bildirdiği için
		/// ciddiyet buradan türetilir. Türetilmezse hata "info" ciddiyetiyle
		/// yazılır, yani tamponlanır ve kritik olduğu için anında diske
		/// inmesi de gerektiği halde inmez — hata ancak kapanışta görünür.
		/// </summary>
		public static LogSeverity FromKind(LogKind kind) {
			switch (kind) {
				case LogKind.Warn: return LogSeverity.Warn;
				case LogKind.Error: return LogSeverity.Error;
				default: return LogSeverity.Info;      // Info ve State
			}
		}

		/// <summary>
		/// Dosyada ve konsörde yazılan ETİKET.
		///
		/// Neden <see cref="LogKind"/>'e bakıyor: etiket bir GÖRÜNÜM kararıdır
		/// (warn kırmızı, state mavi). Ciddiyetten üretilirse uyarı ve hata
		/// satırları "info" olarak yazılır — iki tarafın logu karşılaştırılırken
		/// "burada hata var mı" sorusu cevapsız kalır.
		/// </summary>
		public static string Tag(LogKind kind) {
			switch (kind) {
				case LogKind.Warn: return "warn";
				case LogKind.Error: return "error";
				case LogKind.State: return "state";
				default: return "info";
			}
		}
	}
}
