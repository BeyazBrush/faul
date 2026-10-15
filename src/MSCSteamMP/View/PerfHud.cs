using System.Text;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// PERFORMANS PANELİ — "300 FPS alıyorum ama kasılıyor" sorusunun ölçümü.
	///
	/// Belirti (kullanıcı): "kamera optimizasyonu ekle 300 fps almama rağmen
	/// kasıyor hissi var".
	///
	/// FPS tek başına yeterli bir ölçüt DEĞİL. 300 FPS, kare süresinin 3.3 ms
	/// olduğu anlamına gelir; ama o 3.3 ms'nin 2.5 ms'si tek bir karede
	/// (GC) olursa grafik akıcı görünürken kontrol tepkisi "donuk" hissedilir.
	/// Kasma hissi çoğunlukla iki şeyden gelir:
	///
	///   1. KARE SÜRESİ DALGASI (spike). Ortalama 3 ms ama her saniye bir
	///      kare 25 ms'ye çıkıyorsa göz "takılma" olarak algılar.
	///   2. GC BASKISI. Kısa ömürlü tahsis (delegate, List, string) saniyede
	///      on binlerce kez olursa Unity her collections fazında durur.
	///
	/// Bu panel üç şeyi gösterir:
	///   * anlık ve ortalama FPS + kare süresi (ms)
	///   * MSCSteamMP'nin kendi kare başına maliyeti ve EN AĞIR modül
	///   * yönetilen yığın (managed heap) boyutu — tahsis görünür olur
	///
	/// MODÜL BAŞINA ÖLÇÜM
	/// ------------------
	/// SteamMPBehaviour.Guard zaten her modülü ayrı çalıştırıyor. Kronometreyi
	/// ORADA başlatıp bitiriyoruz: yani ölçüm, istisna yutan korumanın
	/// İÇİNDEDİR; Guard'a eklenen tek maliyet iki Stopwatch.GetTimestamp()
	/// çağrısıdır (tahsis yok, ~20 ns).
	///
	/// GC-FREE: sabit boyutlu diziler, kare başına yeni tahsis yok.
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class PerfHud {

		/// <summary>Panel açık mı? F8 veya 'smp perf' ile açılır.</summary>
		public static bool Visible;

		/// <summary>Ölçülen modül sayısı. Guard 15 modül çağırıyor; 24 yeterli.</summary>
		const int SLOTS = 24;

		static readonly string[] slotName = new string[SLOTS];
		/// <summary>Yumuşatılmış milisaniye (neden? tek kare yanıltıcıdır).</summary>
		static readonly float[] slotMs = new float[SLOTS];
		/// <summary>Anlık milisaniye (ani yavaşlamaları görmek için).</summary>
		static readonly float[] slotNow = new float[SLOTS];
		static int slotCount;

		// Kare düzeyi toplamlar
		static float modMsSmooth;
		static float frameMsSmooth;
		static float frameMsPeak;
		static float frameMsWorstSlow;
		static long lastHeap;

		// Çizim için yeniden kullanılan tampon (kare başına tahsis yok)
		static readonly StringBuilder sb = new StringBuilder(512);
		static Texture2D pixel;

		// ------------------------------------------------------------------
		// Ölçüm
		// ------------------------------------------------------------------

		/// <summary>
		/// Guard'ın çağırdığı zamanlayıcı.
		/// Stopwatch.GetTimestamp sabit frekanslı bir sayaç döndürür ve
		/// TAHİS YAPMAZ (new Stopwatch() her karede ayrım nesnesi üretirdi).
		/// </summary>
		public static long Now() { return System.Diagnostics.Stopwatch.GetTimestamp(); }

		/// <summary>Frekansı bir kez hesapla (saniye başına tik).</summary>
		static double ticksPerSecond = 0d;
		static float ToMs(long ticks) {
			if (ticksPerSecond == 0d) {
				ticksPerSecond = (double)System.Diagnostics.Stopwatch.Frequency;
				if (ticksPerSecond <= 0d) ticksPerSecond = 10000000d;
			}
			return (float)(ticks / ticksPerSecond * 1000.0);
		}

		/// <summary>Guard: modül bitti, süreyi yaz.</summary>
		public static void Mark(string name, long startTicks) {
			// HER ZAMAN ölçülür: maliyet iki Stopwatch.GetTimestamp (~20 ns).
			// 15 modül = 300 ns/kare; 3 ms'lik karede %0.01. Panel kapalıyken
			// de ölçmek, 'smp perf' komutunun panel açılmadan da doğru
			// veri vermesini sağlar.
			float ms = ToMs(System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);

			int idx = -1;
			for (int i = 0; i < slotCount; i++) {
				if (slotName[i] == name) { idx = i; break; }
			}
			if (idx < 0) {
				if (slotCount >= SLOTS) {
					// En eski yuvası geri dönüşür (modül kaldırıldıysa).
					idx = 0;
				}
				else {
					idx = slotCount;
					slotCount++;
				}
				slotName[idx] = name;
				slotMs[idx] = 0f;
			}
			slotNow[idx] = ms;
			// Üstel yumuşatma: 0.2 -> yaklaşık 5 karelik ortalama.
			slotMs[idx] += (ms - slotMs[idx]) * 0.2f;
		}

		/// <summary>Kare başına toplam modül maliyeti.</summary>
		public static void FrameTotal(float ms) {
			modMsSmooth += (ms - modMsSmooth) * 0.1f;
		}

		/// <summary>
		/// Kare başına bir kez: gerçek kare süresi, tepe ve yığın örneği.
		///
		/// Yığın ölçümü: UnityEngine.Profiling.Profiler bu build'de
		/// (Unity 5.0 referansında) YOK, bu yüzden System.GC.GetTotalMemory
		/// kullanılıyor. false = çöp toplama tetikleme; sadece okuma.
		/// Değer anlık kullanılan değil, ayakta olan yığımdır: sürekli
		/// büyüyorsa kare başına tahsis demektir (GC baskısı).
		/// </summary>
		public static void Sample() {
			try {
				float ms = Time.unscaledDeltaTime * 1000f;
				frameMsSmooth += (ms - frameMsSmooth) * 0.1f;
				if (ms > frameMsPeak) frameMsPeak = ms;
				// 2 saniyede bir tepe sıfırlanır; aksi halde tek bir kare
				// sonsuza dek "en kötü" görünür.
				frameMsWorstSlow -= Time.unscaledDeltaTime;
				if (frameMsWorstSlow <= 0f) {
					frameMsWorstSlow = 2f;
					frameMsPeak = ms;
				}
				lastHeap = (long)System.GC.GetTotalMemory(false);
			}
			catch { }
		}

		// ------------------------------------------------------------------
		// Çizim
		// ------------------------------------------------------------------

		const float PAD = 8f;
		const float ROW = 15f;
		const float MARGIN = 8f;

		static GUIStyle header, row, warn;

		static void EnsureStyles() {
			if (header != null) return;
			header = new GUIStyle(GUI.skin.label);
			header.fontSize = 13;
			header.fontStyle = FontStyle.Bold;
			header.wordWrap = false;          // varsayılan true: satırı böler/kırpar
			header.alignment = TextAnchor.MiddleLeft;
			header.normal.textColor = new Color(1f, 0.85f, 0.2f);

			row = new GUIStyle(GUI.skin.label);
			row.fontSize = 12;
			row.wordWrap = false;
			row.alignment = TextAnchor.MiddleLeft;
			row.normal.textColor = new Color(0.92f, 0.92f, 0.92f);

			warn = new GUIStyle(row);
			warn.normal.textColor = new Color(1f, 0.5f, 0.35f);
		}

		/// <summary>
		/// OnGUI içinden çağrılır. Oyunun GUI.matrix'i miras kalmasın diye
		/// kimlik matrise geçilir (ConnectionPanel ile aynı sebepten).
		/// </summary>
		public static void Draw() {
			if (!Visible) return;
			try { DrawPanel(); }
			catch (System.Exception) { Visible = false; }
		}

		static void DrawPanel() {
			EnsureStyles();
			if (pixel == null) pixel = Solid();

			Matrix4x4 keep = GUI.matrix;
			Color keepColor = GUI.color;
			GUI.matrix = Matrix4x4.identity;

			float fps = frameMsSmooth > 0.01f ? 1000f / frameMsSmooth : 0f;
			float heapMb = lastHeap / (1024f * 1024f);

			// Satırları hazırla. wordWrap=false olduğu için kırpma olmaz.
			sb.Length = 0;
			sb.Append("MSCSteamMP  perf\n");
			sb.Append("frame  ").Append(frameMsSmooth.ToString("F2")).Append(" ms   ")
			  .Append(fps.ToString("F0")).Append(" fps\n");
			sb.Append("peak   ").Append(frameMsPeak.ToString("F2")).Append(" ms (last 2 s)\n");
			sb.Append("mod    ").Append(modMsSmooth.ToString("F2")).Append(" ms/frame\n");
			sb.Append("heap   ").Append(heapMb.ToString("F1")).Append(" MB mono\n");
			sb.Append("vSync  ").Append(QualitySettings.vSyncCount)
			  .Append("   target ").Append(Application.targetFrameRate).Append('\n');

			// Modül sıralaması: en ağır 5. Küçük dizi, seçim sıralaması —
			// GC için tahsis yapmadan bubble sort.
			SortSlots();
			sb.Append("worst modules\n");
			int shown = 0;
			for (int i = 0; i < slotCount && shown < 5; i++) {
				if (slotMs[i] < 0.01f) continue;
				sb.Append("  ").Append(slotName[i].PadRight(14))
				  .Append(slotMs[i].ToString("F3")).Append(" ms\n");
				shown++;
			}

			float w = 250f;
			float h = PAD * 2f + ROW * 7f + ROW * Mathf.Max(shown, 1) + ROW;
			Rect box = new Rect(MARGIN, MARGIN, w, h);

			GUI.color = new Color(0f, 0f, 0f, 0.80f);
			GUI.DrawTexture(box, pixel);
			// Kare süresi dalgalıysa başlık çizgisi kırmızı: "kasıyor"
			// hissinin en somut göstergesi budur.
			GUI.color = frameMsPeak > frameMsSmooth * 2f && frameMsSmooth > 0.1f
				? new Color(1f, 0.35f, 0.25f, 0.95f)
				: new Color(0.4f, 1f, 0.4f, 0.9f);
			GUI.DrawTexture(new Rect(box.x, box.y, box.width, 2f), pixel);
			GUI.color = keepColor;

			// Başlık ve satırlar ayrı GUI.Label ile çizilir ki stiller
			// (wordWrap=false) ve renkler doğru uygulansın.
			GUI.Label(new Rect(box.x + PAD, box.y + PAD, w - PAD * 2f, ROW * 1.4f),
				"MSCSteamMP  perf", header);
			float y = box.y + PAD + ROW * 1.4f;
			string[] parts = sb.ToString().Split('\n');
			for (int i = 1; i < parts.Length; i++) {
				string line = parts[i];
				if (string.IsNullOrEmpty(line)) continue;
				// "worst modules" başlığı ve altındaki modül satırları.
				bool isModule = line.StartsWith("  ");
				GUI.Label(new Rect(box.x + PAD, y, w - PAD * 2f, ROW), line,
					line.StartsWith("peak") && frameMsPeak > frameMsSmooth * 2f ? warn : row);
				y += ROW;
			}

			GUI.matrix = keep;
			GUI.color = keepColor;
		}

		/// <summary>slotName'ı slotMs'e göre azalan sıralar (küçük dizi, seçim).</summary>
		static void SortSlots() {
			for (int i = 0; i < slotCount - 1; i++) {
				int worst = i;
				for (int j = i + 1; j < slotCount; j++) {
					if (slotMs[j] > slotMs[worst]) worst = j;
				}
				if (worst == i) continue;
				string n = slotName[i]; slotName[i] = slotName[worst]; slotName[worst] = n;
				float a = slotMs[i]; slotMs[i] = slotMs[worst]; slotMs[worst] = a;
				a = slotNow[i]; slotNow[i] = slotNow[worst]; slotNow[worst] = a;
			}
		}

		/// <summary>1x1 beyaz doku (GUI.DrawTexture için).</summary>
		static Texture2D Solid() {
			Texture2D t = new Texture2D(1, 1);
			t.SetPixel(0, 0, Color.white);
			t.Apply();
			t.hideFlags = HideFlags.HideAndDontSave;
			return t;
		}

		// ------------------------------------------------------------------
		// Kontrol
		// ------------------------------------------------------------------

		/// <summary>F8 / 'smp perf' ile aç/kapa.</summary>
		public static void Toggle() {
			Visible = !Visible;
			// Kapalıyken ölçüm de dursun: kullanıcı sorunu çözene kadar
			// maliyeti görmek ister. Mark() yalnızca ilk açılışta ölçmeye başlar.
			SmpLog.Print("performance panel " + (Visible ? "on (F8)" : "off"));
			if (Visible) SmpLog.Print(Describe());
		}

		/// <summary>Ölçümü sıfırlar.</summary>
		public static void Reset() {
			slotCount = 0;
			modMsSmooth = 0f;
			frameMsSmooth = 0f;
			frameMsPeak = 0f;
			frameMsWorstSlow = 0f;
			lastHeap = 0;
		}

		/// <summary>Konsol için tek satırlık özet.</summary>
		public static string Describe() {
			sb.Length = 0;
			sb.Append("frame ").Append(frameMsSmooth.ToString("F2")).Append(" ms (")
			  .Append(frameMsSmooth > 0.01f ? (1000f / frameMsSmooth).ToString("F0") : "0")
			  .Append(" fps), peak ").Append(frameMsPeak.ToString("F2")).Append(" ms, mod ")
			  .Append(modMsSmooth.ToString("F2")).Append(" ms, heap ")
			  .Append((lastHeap / (1024f * 1024f)).ToString("F1")).Append(" MB");
			return sb.ToString();
		}

		/// <summary>F8'e basıldı mı? SteamMPBehaviour.Update içinden sorulur.</summary>
		public static bool TogglePressed() {
			try { return Input.GetKeyDown(KeyCode.F8); }
			catch { return false; }
		}
	}
}