using System.Collections.Generic;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// EYLEM GÜNLÜĞÜ — teşhis edici.
	///
	/// AMAÇ
	/// ----
	/// İki oyunculu oturumda sorun çıktığında "ben ne yaptım, o ne
	/// gördü" eşleştirmesi elle yapılıyordu. Bu sınıf, oyuncunun YEREL
	/// eylemlerini (vida sıkma, eşya alma, kapı açma, motor çalıştırma)
	/// tek bir biçimde loga yazar; iki tarafın logu aynı satırları
	/// içermek zorunda.
	///
	/// NEDEN AYRI BİR MODÜL
	/// --------------------
	/// Dört farklı yerde (BoltSync, HeldItemSync, WorldObjectSync,
	/// SyncedVehicle) aynı biçimde yazılmasın diye tek yer toplandı. Biçim
	/// değişirse tek dosyadan değişir.
	///
	/// BİÇİM
	/// -----
	///   [ACT] &lt;kategori&gt; &lt;ne oldu&gt; &lt;ayrıntı&gt;
	/// Örnekler:
	///   [ACT] BOLT   tightened   .../strut fl(xxxxx)#2  stage 0 -&gt; 1
	///   [ACT] HAND   took         SUHTE/Beer can
	///   [ACT] DOOR   opened       WORKSHOP/DoorFront  Open=true
	///   [ACT] ENGINE started      SATSUMA(557kg, 248)  state=2 dash=1
	///
	/// Grep için: "[ACT]".
	///
	/// MALİYET
	/// ------
	/// Satır yazmak diske gider, o yüzden olay başına bir kez çağrılır,
	/// sıcak yolda değil. Sayaçlar şark etmeyi göstermek için var.
	/// </summary>
	public static class SmpTrace {

		/// <summary>Log satırı başlangıcı. Grep edilebilir olsun diye sabit.</summary>
		public const string PREFIX = "[ACT] ";

		/// <summary>Kapalıyken hiçbir şey yazılmaz (saat çok yüksekse).</summary>
		public static bool Enabled = true;

		/// <summary>
		/// Saniyede yazılabilecek azami satır. TAŞMA KORUMASI.
		///
		/// Canlı log kanıtı: bir regresyon (referanslar gönderimde
		/// güncellendiği için Changed() hep true döndü) 46 nesneyi 4 Hz'de
		/// yazdı ve log 891 KB'dan 2.47 MB'ye çıktı — 25.271 satır, saniyeler
		/// içinde. Sınır aşılırsa yazma durur ve ONCE bildirilir; sayaç
		/// artmaya devam eder, yani kaç satırın atlandığı bellidir.
		/// </summary>
		public const int MAX_PER_SECOND = 60;
		static int thisSecond;
		static int thisSecondCount;
		static bool overflowWarned;
		/// <summary>Sınır nedeniyle atlanan satır sayısı.</summary>
		public static int Dropped;

		// ------------------------------------------------------------------
		// Teşhis sayaçları (satır yazmaz)
		// ------------------------------------------------------------------
		// Canlı log kanıtı (23:03): kullanıcı vida sıktı/gevşetti ve eşya aldı,
		// [ACT] BOLT / [ACT] HAND yazılmadı. "Kanca tetiklenmiyor mu, yoksa
		// tespit mi boş dönüyor?" ayrımı ancak bu sayaçlarla yapılabilir:
		//   BoltsSeen  > 0  → kanca çalışıyor, Stage değişmiyor demektir
		//   BoltsSeen == 0  → FsmInt Postfix cıvata değişkenine hiç ulaşmıyor
		//   HandPolls/HeldFound → el tespiti çalışıyor mu, eşya bulunuyor mu
		/// <summary>Bolt kancasının gördüğü değişken yazımı (tek sayım).</summary>
		public static int BoltsSeen;
		/// <summary>El tespiti yapılan tur sayısı.</summary>
		public static int HandPolls;
		/// <summary>El altında takılabilir nesne BULUNDUĞU tur sayısı.</summary>
		public static int HandFound;
		/// <summary>İlk birkaç bolt sayımından sonra durur (sayaç şişmesin).</summary>
		public static bool BoltCounted;

		// Kategori sayaçları. Hepsi tek satırda raporlanır.
		public static int Bolts;
		public static int Hands;
		public static int Doors;
		public static int Engines;
		/// <summary>Parça takma/sökme. Bu sayaç loglanmaya başlandığında eklendi.</summary>
		public static int Parts;

		static readonly Dictionary<string, int> byCategory = new Dictionary<string, int>(System.StringComparer.Ordinal);

		/// <summary>Bir eylemi loglar. <paramref name="category"/> kısa ve sabit olsun.</summary>
		public static void Act(string category, string action, string detail) {
			if (!Enabled) return;
			if (string.IsNullOrEmpty(category)) category = "?";
			if (string.IsNullOrEmpty(action)) action = "?";
			// Sayaç her zaman artar (kaç eylem olduğu bilsin), yazma sınırlı.
			int n;
			byCategory.TryGetValue(category, out n);
			byCategory[category] = n + 1;
			try {
				int sec = Time.frameCount / 60;
				if (sec != thisSecond) { thisSecond = sec; thisSecondCount = 0; }
				if (thisSecondCount >= MAX_PER_SECOND) {
					Dropped++;
					if (!overflowWarned) {
						overflowWarned = true;
						SmpLog.Warning("action log: more than " + MAX_PER_SECOND
							+ " lines in one second; further lines are dropped. "
							+ "This means something is re-triggering every frame — "
							+ "send the surrounding log lines.");
					}
					return;
				}
				overflowWarned = false;
				thisSecondCount++;
				SmpLog.Log(PREFIX + category + " " + action + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
			}
			catch { }
		}

		/// <summary>Sayaç özeti ('smp trace' komutu).</summary>
		public static string Describe() {
			if (!Enabled) {
				return "action log: OFF (previous counts: bolt " + Bolts + ", part " + Parts
					+ ", hand " + Hands + ", door " + Doors + ", engine " + Engines + ")";
			}
			System.Text.StringBuilder sb = new System.Text.StringBuilder(160);
			sb.Append("action log: ON, grep for '").Append(PREFIX.Trim()).Append("'");
			foreach (KeyValuePair<string, int> kv in byCategory) {
				sb.Append(", ").Append(kv.Key).Append(' ').Append(kv.Value);
			}
			if (Dropped > 0) sb.Append(", DROPPED ").Append(Dropped);
			sb.Append(" | probe: bolt-seen ").Append(BoltsSeen)
			  .Append(", hand-poll ").Append(HandPolls)
			  .Append(", hand-found ").Append(HandFound);
			return sb.ToString();
		}

		/// <summary>
		/// Dünya yüklenirken bir kez yazılır: eylem günlüğünün açık olduğunu
		/// ve neyin yazılacağını duyurur. Kullanıcı "komut mu gerekli?"
		/// diye sormuştu: gerekmiyor, bu satır bunu her oturumda hatırlatır.
		/// </summary>
		public static void Announce() {
			if (!Enabled) {
				SmpLog.Log("action log is OFF (smp trace on to enable).");
				return;
			}
			SmpLog.Log("action log ON, grep '" + PREFIX.Trim()
				+ "' for: BOLT (any Stage/Tightness change under the Satsuma), "
				+ "PART (part fitted / removed), "
				+ "HAND (picked up / put down), DOOR (world + vehicle doors, switches, "
				+ "taps, shower), ENGINE (starter). No command needed.");
		}

		/// <summary>Aç/kapat ve sıfırla ('smp trace on|off|reset').</summary>
		public static void Set(bool enabled, bool reset) {
			if (reset) {
				Bolts = Parts = Hands = Doors = Engines = 0;
				Dropped = 0;
				BoltsSeen = HandPolls = HandFound = 0;
				BoltCounted = false;
				byCategory.Clear();
				thisSecondCount = 0;
				overflowWarned = false;
			}
			Enabled = enabled;
			SmpLog.Print(Describe());
		}
	}
}