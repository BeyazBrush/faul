using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SteamMP {

	/// <summary>
	/// Yapılandırılmış log alanları: "anahtar=değer" çiftleri.
	///
	/// SORUN
	/// -----
	/// Serbest metin logları makine tarafından okunamıyor. "carry: p 0->1" ve
	/// "carry: 0 -> 1" aynı bilgiyi veriyor ama ikisi de bir grep'e yakalanmıyor.
	/// İki oyunculu oturumda "ben ne gönderdim, o ne aldı" karşılaştırması
	/// ancak alanlar sabit isimliyse otomatik yapılabiliyor.
	///
	/// SÖZLEŞME
	/// --------
	///   * Alan adı küçük harf, [_a-z0-9] karakterleriyle. Boşluk ve '=' yasak,
	///     çünkü ayrıştırıcı bunları güvenilir biçimde buluyor olmalı.
	///   * Değer her zaman tırnak içinde yazılır; boş değer bile ("" ) görünür,
	///     yani "alan yok" ile "alan boş" karışmaz.
	///   * Sayılar kültürden bağımsız yazılır. Oyuncu Türkçe sistemde
	///     1,5 görür ama dosyadaki bayt sırası 1.5'tir; logu ayrıştıran araç
	///     InvariantCulture ile okumalı, biz de yazarken aynısını kullanıyoruz.
	///
	/// MALİYET
	/// ------
	/// Alan kullanılmayan çağrılar (modun çoğu) hiç tahsis yapmaz: sınıf
	/// ancak ilk Add'de liste açar. Biçimlendirme de tek sefer, kayıt anında
	/// yapılır (bkz. LogRecord.Format).
	/// </summary>
	public sealed class LogFields {

		sealed class Pair {
			public string Key;
			public string Value;
			public Pair(string k, string v) { Key = k; Value = v; }
		}

		List<Pair> pairs;

		/// <summary>Alan sayısı sınırı. Aşan alanlar sayılır ama yazılmaz.</summary>
		public const int MAX_FIELDS = 12;

		/// <summary>Sınıra takılıp atılan alan sayısı (raporlanır, sessizce değil).</summary>
		public int Dropped { get; private set; }

		public LogFields() { pairs = null; }

		public bool Empty { get { return pairs == null || pairs.Count == 0; } }

		public int Count { get { return pairs == null ? 0 : pairs.Count; } }

		/// <summary>
		/// Alan ekler. Anahtar geçersizse ALAN YOK SAYILIR ve Dropped artar:
		/// geçersiz anahtar mesajı gizlemek için bir sebep değil.
		/// </summary>
		public LogFields Add(string key, string value) {
			if (!ValidKey(key)) { Dropped = Dropped + 1; return this; }
			if (pairs == null) pairs = new List<Pair>(6);
			if (pairs.Count >= MAX_FIELDS) { Dropped = Dropped + 1; return this; }
			pairs.Add(new Pair(key, value ?? ""));
			return this;
		}

		public LogFields Add(string key, int value) {
			return Add(key, value.ToString(CultureInfo.InvariantCulture));
		}

		public LogFields Add(string key, long value) {
			return Add(key, value.ToString(CultureInfo.InvariantCulture));
		}

		public LogFields Add(string key, bool value) {
			return Add(key, value ? "1" : "0");
		}

		/// <summary>Ondalık sayı: nokta ayracı, kültürden bağımsız.</summary>
		public LogFields Add(string key, float value) {
			if (float.IsNaN(value)) return Add(key, "nan");
			if (float.IsInfinity(value)) return Add(key, value > 0 ? "+inf" : "-inf");
			return Add(key, value.ToString("0.###", CultureInfo.InvariantCulture));
		}

		/// <summary>Ondalık sayı, sabit 3 basamak (konum/hız için).</summary>
		public LogFields Add3(string key, float value) {
			if (float.IsNaN(value)) return Add(key, "nan");
			return Add(key, value.ToString("0.000", CultureInfo.InvariantCulture));
		}

		/// <summary>Süre: milisaniye, tek basamak.</summary>
		public LogFields AddMs(string key, double ms) {
			return Add(key, ms.ToString("0.#", CultureInfo.InvariantCulture) + "ms");
		}

		public LogFields AddMs(string key, long ms) {
			return Add(key, ms.ToString(CultureInfo.InvariantCulture) + "ms");
		}

		/// <summary>Onaltılık sayı (tablo hash'i, mesaj kimliği).</summary>
		public LogFields AddHex(string key, uint value) {
			return Add(key, "0x" + value.ToString("x8", CultureInfo.InvariantCulture));
		}

		public LogFields AddHex(string key, ulong value) {
			return Add(key, "0x" + value.ToString("x16", CultureInfo.InvariantCulture));
		}

		/// <summary>
		/// Alan adı kuralları. Üst harf de kabul edilir ama küçük harfe
		/// çevrilir, böylece Add("Id") ve Add("id") aynı alan olur.
		/// </summary>
		static bool ValidKey(string key) {
			if (string.IsNullOrEmpty(key) || key.Length > 24) return false;
			for (int i = 0; i < key.Length; i++) {
				char c = key[i];
				if (c >= 'A' && c <= 'Z') continue;
				if (c >= 'a' && c <= 'z') continue;
				if (c >= '0' && c <= '9') continue;
				if (c == '_' || c == '.' || c == '-') continue;
				return false;
			}
			return true;
		}

		static string Normalise(string key) {
			return key.ToLowerInvariant();
		}

		/// <summary>
		/// Kayda yazılacak biçim: boşsa "" döner, değilse " k=v k2=\"v 2\"".
		/// Tırnak içeriğindeki tırnak ve satır sonu kaçırılır; aksi halde tek
		/// satırlık log çok satıra bölünür ve ayrıştırıcı satır bazında çalıştığı
		/// için kayıp oluşur.
		/// </summary>
		public string Format() {
			if (pairs == null || pairs.Count == 0) return "";
			StringBuilder sb = new StringBuilder(64);
			for (int i = 0; i < pairs.Count; i++) {
				Pair p = pairs[i];
				sb.Append(' ').Append(Normalise(p.Key)).Append('=').Append('"');
				AppendQuoted(sb, p.Value);
				sb.Append('"');
			}
			return sb.ToString();
		}

		internal static void AppendQuoted(StringBuilder sb, string value) {
			if (string.IsNullOrEmpty(value)) return;
			for (int i = 0; i < value.Length; i++) {
				char c = value[i];
				switch (c) {
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						// Kontrol karakterleri ayrıştırıcıyı bozuyor, görünmez
						// karakterler de logu görsel olarak bozuyor; ikisi de atlanır.
						if (c < 0x20 || c == 0x7f) sb.Append('?');
						else sb.Append(c);
						break;
				}
			}
		}

		/// <summary>Tek satırlık insan okuma biçimi (konsör / 'smp log stats').</summary>
		public override string ToString() {
			return Format();
		}
	}
}
