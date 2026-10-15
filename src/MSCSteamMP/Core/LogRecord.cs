using System;
using System.Globalization;
using System.Text;

namespace SteamMP {

	/// <summary>
	/// Tek bir log kaydı.
	///
	/// NEDEN YAPI İÇERİSİN
	/// ------------------
	/// Önceden halka tamponunda üç alan vardı: damga, seviye, metin. Bu, kayıt
	/// için yeterli değildi çünkü konsör artık "hangi kanaldan", "hangi
	/// kapsamda", "hangi iş parçacığından" sorularını da yanıtlamak zorunda ve
	/// dosya tarafında da zaman damgası milisaniye çözünürlüğü istiyor.
	///
	/// Ayrıca yükseklik önbelleği burada taşınır. Daha önce SmpLog'un Line
	/// yapısındaydı; ayrı tutulsaydı, tampon taşıp eski kayıtlar düştüğünde
	/// yükseklik yanlış metne yapışırdı (CoreProbe bu yüzden var).
	///
	/// BELLEK
	/// ------
	/// struct, sabit boyutlu, 800 kayıt = ~100 KB. struct yerine class kullanılsaydı
	/// her kayıt için ayrı tahsis (allocation) olurdu; konsör 60 Hz çizildiği
	/// için bu, GC baskısı demekti. <see cref="Height"/> konsörün ölçüm
	/// önbelleğidir, ilk çizimde doldurulur.
	/// </summary>
	public struct LogRecord {

		/// <summary>Oturum başından bu yana geçen ms. Sıralama ve süre hesabı için.</summary>
		public long Since;

		/// <summary>Seri numarası: yazılan kayıtların sırası, 1'den başlar.</summary>
		public int Serial;

		/// <summary>Filtre ekseni.</summary>
		public LogSeverity Level;

		/// <summary>Ekrandaki rozet (State ayrı bir görünüm kararıdır).</summary>
		public LogKind Kind;

		/// <summary>Kaynağın kanalı: "net", "satsuma", ...</summary>
		public string Channel;

		/// <summary>Olayın adı (varsa): "send", "recv", "apply".</summary>
		public string Event;

		/// <summary>Serbest metin.</summary>
		public string Message;

		/// <summary>Önceden biçimlenmiş alanlar: " k=v k2=\"v\"".</summary>
		public string Fields;

		/// <summary>Kapsam yolu: "sync/parts". İç içe scopelardan birleşir.</summary>
		public string Scope;

		/// <summary>Yazan iş parçacığı. Ana iş parçacığı 1'dir.</summary>
		public int Thread;

		/// <summary>Tekrarlanan kayıtlarda kaç kez bastırıldı (normalde 0).</summary>
		public int Repeat;

		/// <summary>Konsörün ölçtüğü yükseklik; 0 = henüz ölçülmedi.</summary>
		public float Height;

		/// <summary>Damga: HH:mm:ss.fff</summary>
		public static string TimeOf(long since) {
			DateTime t = LogEngine.Epoch.AddMilliseconds(since);
			return t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Dosya satırı. Geriye dönük uyum için biçim eskisiyle aynı kaldı:
		///   "SS:dd.mmm [info] kanal [MSCSteamMP] mesaj  alanlar"
		/// Kanal, mesajın hemen ÖNÜNE geliyor ki [MSCSteamMP] ile mesaj bitişik
		/// kalsın (birçok grep buna bağlı) ve alanlar mesajın arkasında dursun
		/// (gövde okunurken gürültü olmasın).
		/// </summary>
		public string FormatFile() {
			StringBuilder sb = new StringBuilder(Message == null ? 64 : Message.Length + 48);
			Stamp(sb);
			sb.Append('[').Append(TagText()).Append(']');
			sb.Append(' ').Append(ChannelOrDefault());
			sb.Append(" [MSCSteamMP] ");
			if (!string.IsNullOrEmpty(Scope)) {
				sb.Append(Scope);
				sb.Append('/');
			}
			if (!string.IsNullOrEmpty(Event)) {
				sb.Append(Event);
				sb.Append(' ');
			}
			sb.Append(Message == null ? "" : Message);
			if (Repeat > 1) {
				sb.Append("  (x").Append(Repeat.ToString(CultureInfo.InvariantCulture)).Append(')');
			}
			if (!string.IsNullOrEmpty(Fields)) sb.Append(Fields);
			if (Thread > 1) {
				sb.Append("  tid=").Append(Thread.ToString(CultureInfo.InvariantCulture));
			}
			return sb.ToString();
		}

		void Stamp(StringBuilder sb) {
			sb.Append(TimeOf(Since)).Append(' ');
		}

		/// <summary>
		/// Yazılacak etiket.
		///
		/// GÖRÜNÜM kararı olduğu için <see cref="LogKind"/>'den üretilir, ciddiyetten
		/// değil: aksi halde uyarı ve hata satırları "[info]" olarak yazılır ve
		/// iki tarafın logu karşılaştırılırken "burada hata var mı" sorusu
		/// cevapsız kalır. Teşhis satırları (debug/trace) etiketi ciddiyetten
		/// alır, çünkü rozet onları ayırt etmek zorunda.
		/// </summary>
		public string TagText() {
			if (Level == LogSeverity.Debug || Level == LogSeverity.Trace) {
				return LogSeverities.Name(Level);
			}
			return LogSeverities.Tag(Kind);
		}

		/// <summary>
		/// Konsör satırı. Damga ve rozet ayrı sütunlarda çizildiği için burada
		/// yalnızca gövde var: "[MSCSteamMP] mesaj alanlar".
		/// </summary>
		public string FormatConsole() {
			StringBuilder sb = new StringBuilder(Message == null ? 64 : Message.Length + 48);
			sb.Append("[MSCSteamMP] ");
			if (!string.IsNullOrEmpty(Scope)) {
				sb.Append(Scope);
				sb.Append('/');
			}
			if (!string.IsNullOrEmpty(Event)) {
				sb.Append(Event);
				sb.Append(' ');
			}
			sb.Append(Message == null ? "" : Message);
			if (Repeat > 1) {
				sb.Append("  (x").Append(Repeat.ToString(CultureInfo.InvariantCulture)).Append(')');
			}
			if (!string.IsNullOrEmpty(Fields)) sb.Append(Fields);
			return sb.ToString();
		}

		/// <summary>Uzun gövde metni, döküm dosyası için (biçimleme yok).</summary>
		public string FormatPlain() {
			StringBuilder sb = new StringBuilder(128);
			sb.Append(TimeOf(Since));
			sb.Append(' ').Append(LogSeverities.Padded(Level));
			sb.Append(' ').Append(ChannelOrDefault());
			sb.Append(" [MSCSteamMP] ");
			if (!string.IsNullOrEmpty(Scope)) { sb.Append(Scope); sb.Append('/'); }
			if (!string.IsNullOrEmpty(Event)) { sb.Append(Event); sb.Append(' '); }
			sb.Append(Message == null ? "" : Message);
			if (Repeat > 1) {
				sb.Append("  (x").Append(Repeat.ToString(CultureInfo.InvariantCulture)).Append(')');
			}
			if (!string.IsNullOrEmpty(Fields)) sb.Append(Fields);
			return sb.ToString();
		}

		public string ChannelOrDefault() {
			return string.IsNullOrEmpty(Channel) ? LogChannels.Default : Channel;
		}

		/// <summary>
		/// Tek satırda arama için gövde metni (alanlar dahil). Konsör filtre
		/// kutusu bunu karşılaştırır.
		/// </summary>
		public bool Matches(string needle) {
			if (string.IsNullOrEmpty(needle)) return true;
			if (Message != null && Message.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (Channel != null && Channel.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (Event != null && Event.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (Scope != null && Scope.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (Fields != null && Fields.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		public override string ToString() {
			return FormatFile();
		}
	}
}
