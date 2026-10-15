using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SteamMP {

	/// <summary>
	/// Discord Rich Presence — harici DLL YOK.
	///
	/// NASIL
	/// -----
	/// Discord masaustu istemcisi calisirken Windows'ta
	/// `\\.\pipe\discord-ipc-0` adli bir named pipe acar ve oradan
	/// "handshake -> SET_ACTIVITY frame" konusmasi bekler. Protokol
	/// duz TCP degil, basit bir cerceveleme:
	///
	///   frame := int32 opcode | int32 length | byte[length] payload
	///   opcode 0 = HANDSHAKE   payload = UTF8 JSON
	///   opcode 1 = FRAME       payload = UTF8 JSON
	///   opcode 2 = CLOSE       (Discord baglantiyi keser)
	///
	/// Bu, `discord-rpc.dll` + native'inin yaptigi isin TAMAMIDIR. Farki
	/// managed tarafin derlenmis olmasi. Oysa bu oyun Mono 2.5 profili
	/// uzerinde calisiyor (mscorlib 2.0.5.0) ve mod tek dosya olarak
	/// dagitiliyor; yanina native kutuphane koymak hem kurulumu zorlastirir
	/// hem surum/platform eslesmesi gerektirir. Pipe'a yazmak ikisini de
	/// gereksiz kilar.
	///
	/// HANDSHAKE JSON OLMALI — CANLI KANIT
	/// -----------------------------------
	/// Eski dokuman handshake'i binary struct olarak tarif eder
	/// (int32 v + char[] client_id). Guncel Discord bunu REDDEDIYOR:
	///   -> opcode=0 len=24   (binary struct)
	///   <- opcode=2 {"code":1003,"message":"Unexpected token ... is not valid JSON"}
	/// Baglanti kapaniyor. Dogru bicim JSON:
	///   -> opcode=0 {"v":1,"client_id":"..."}
	///   <- opcode=1 {"cmd":"DISPATCH","evt":"READY","data":{...}}
	///
	/// IS PARCACIGI
	/// ------------
	/// Pipe okuma/yazma zaman bloklayabilir; Unity ana is parciginda
	/// bloke olursa donma olur. Bu yuzden TUM socket isi kendi
	/// `Thread` uzerinde; ana is parcigi sadece `Tick()` ile durumu bir
	/// kuyruga birakir. Kuyruk `Queue<string>` + lock ile korunur
	/// (ConcurrentQueue .NET 3.5'te yok).
	///
	/// ZAMANLAYICI
	/// ----------
	/// Discord istemcisi baglantiyi ~15 saniye sonra dusurur; bu yuzden
	/// 15 saniyede bir PING atilir. Discord yoksa pipe acilmaz; 20 saniyede
	/// bir yeniden denenir.
	///
	/// Unity 5 / .NET 3.5 uyumlu: async/await, Task, ConcurrentQueue yok.
	/// </summary>
	public static class DiscordPresence {

		// ------------------------------------------------------------------
		// Ayarlar
		// ------------------------------------------------------------------

		/// <summary>
		/// Discord Application ID — derlemeye GOMULU, dosya gerekmez.
		///
		/// Kullanici talebi: "RPC oyunu actigim an baslasin, oyun kapanana
		/// kadar devam etsin." Yani ilk acilista Mods\mscdiscord.txt yazili
		/// olmayabilir; bu yuzden varsayilan burada sabit. Dosya varsa
		/// icindeki deger BUNU EZER (baska bir uygulamaya gecmek isteyen
		/// icin), ama dosyada BOS clientId varsayilani EZMEZ.
		/// </summary>
		public const string DEFAULT_CLIENT_ID = "1557283685433221161";

		/// <summary>Discord Application ID (bos olabilir, o zaman hicbir sey yapilmaz).</summary>
		public static string ClientId = DEFAULT_CLIENT_ID;

		/// <summary>Istendiginde kapatilabilir.</summary>
		public static bool Enabled = true;

		/// <summary>Discord'da gosterilen ana baslik (uygulama adi degil, presence satiri).</summary>
		public const string STATE_LABEL = "My Summer Car";

		/// <summary>Rich Presence buyuk gorselinin asset anahtari (Discord uygulamasinda tanimli olmali).</summary>
		public const string LARGE_IMAGE = "mysummercar";

		/// <summary>Kucuk gorselin asset anahtari.</summary>
		public const string SMALL_IMAGE = "satsuma";

		/// <summary>Ayarlarin tutuldugu dosya (Mods klasoru altinda).</summary>
		public static string SettingsPath = "mscdiscord.txt";

		static readonly object ioLock = new object();
		static readonly Queue<string> outgoing = new Queue<string>();
		static string pendingDetails;
		static string pendingState;
		static bool pendingForce;

		static Thread worker;
		static volatile bool stopRequested;
		static volatile bool connected;
		static volatile bool warnedMissing;
		static volatile string lastError = "";
		static string lastSentDetails = " ";
		static string lastSentState = " ";
		static int connectAttempts;
		static int reconnects;
		static int lastPingSent;
		static float nextCheck;

		// AYRI TAMPONLAR. Tek bir StringBuilder'i paylasmak hataliydi:
		// WriteHandshake worker thread'inde, BuildActivity ana thread'de
		// (Update -> Report) ayni tampona yaziyordu -> bozuk JSON ->
		// Discord baglantiyi kapatiyordu. Iki tampon, lock gerektirmez.
		static readonly StringBuilder handshakeBuf = new StringBuilder(96);
		static readonly StringBuilder activityBuf = new StringBuilder(512);

		// ------------------------------------------------------------------
		// Genel API
		// ------------------------------------------------------------------

		/// <summary>Discord baglantisi kuruldu mu?</summary>
		public static bool Connected { get { return connected; } }

		/// <summary>Son hata (teshis icin; bos ise hata yok).</summary>
		public static string LastError { get { return lastError ?? ""; } }

		/// <summary>
		/// Oyun ACILIR ACILMAZ baglantiyi kurar. Tick()'e birakilirsa ilk
		/// kare gelene kadar presence gecikir; ModEntry'den dogrudan
		/// cagrilir. Kuyruk/handshake tamamen worker thread'indedir.
		/// </summary>
		public static void Start() {
			if (!Enabled) return;
			if (string.IsNullOrEmpty(ClientId)) return;
			if (worker != null && worker.IsAlive) return;
			stopRequested = false;
			StartWorker();
		}

		/// <summary>Ana is parcigi: baglantiyi kur, durumu gonder.</summary>
		public static void Tick() {
			if (!Enabled) return;
			if (string.IsNullOrEmpty(ClientId)) return;

			Report();
			// Baglanti gozetimi: worker olduyse (Discord kapandi, pipe dustu,
			// sahne yeniden yuklendi) yeniden baslat. Oyun kapanana kadar
			// bu boyle devam eder.
			Start();
		}

		/// <summary>Durumu okur ve Discord'a yazar (kendi icinde 2 sn'de bir).</summary>
		public static void Report() {
			if (!Enabled) return;
			if (string.IsNullOrEmpty(ClientId)) return;

			float now = UnityEngine.Time.time;
			if (now < nextCheck) return;
			nextCheck = now + 2.0f;

			Update(BuildDetails(), BuildState());
		}

		/// <summary>
		/// Durum metnini kuyruga yazar. Gonderim yapmaz; gonderim worker
		/// thread'indedir.
		/// </summary>
		public static void Update(string details, string state) {
			if (!Enabled) return;
			if (string.IsNullOrEmpty(ClientId)) return;

			// Degismemisse hic paket uretme: Discord tarafinda her olayda
			// presence yenilenir, gereksiz trafik olur.
			if (!pendingForce
				&& string.Equals(details, lastSentDetails, StringComparison.Ordinal)
				&& string.Equals(state, lastSentState, StringComparison.Ordinal))
				return;

			lastSentDetails = details ?? "";
			lastSentState = state ?? "";
			pendingDetails = lastSentDetails;
			pendingState = lastSentState;
			pendingForce = false;

			lock (ioLock) {
				outgoing.Enqueue(BuildActivity(pendingDetails, pendingState, false));
			}
		}

		/// <summary>Yeniden gonderimi zorla (menuye donus vb.).</summary>
		public static void ForceUpdate() {
			if (!Enabled) return;
			if (string.IsNullOrEmpty(ClientId)) return;
			pendingForce = true;
		}

		/// <summary>Discord'u kapat: bos activity gonder, baglantiyi birak.</summary>
		public static void Disable() {
			Enabled = false;
			try {
				lock (ioLock) {
					outgoing.Clear();
					outgoing.Enqueue(BuildActivity("", "", true));
				}
			}
			catch { }
		}

		// ------------------------------------------------------------------
		// Durum metni
		// ------------------------------------------------------------------

		/// <summary>Satir 1: ne yapiyor (INGILIZCE — Discord dunyasi Ingilizce,
		/// Turkce karmasi tutarsiz gorunuyor).</summary>
		static string BuildDetails() {
			if (SatsumaRegistry.Root == null) return "In main menu";
			// v15: "Dead" satiri kaldirildi. Oyunu olup olmedigini bilmenin
			// yolu yok: olum senkronu silindi (paket gonderilmiyordu ve
			// IsDead bayragi "PlayerState" adli, dump'ta OLMAYAN bir FSM'den
			// okunuyordu). GodMode zaten olumleri engelliyor.

			// FindLocalOccupant = surucu VEYA yolcu. Once yalnizca surucu
			// bakiliyordu (FindLocalVehicle); yolcu koltugunda presence
			// "Walking"de kaliyordu.
			SyncedVehicle v = VehicleRegistry.FindLocalOccupant();
			if (v != null && LocalPlayerState.InVehicle) {
				string name = FriendlyVehicleName(v.VehicleName);
				if (name == null) return "Driving a vehicle";
				return "Driving the " + name;
			}
			return "Walking around";
		}

		/// <summary>
		/// Sahne kok adini insanin okuyacagi ada cevirir:
		///   "SATSUMA(557kg, 248)"   -&gt; "Satsuma"
		///   "HAYOSIKO(1500kg, 250)" -&gt; "Hayosiko"
		///   "RCO_RUSCKO12(270)"     -&gt; "Ruscko"
		///   "JONNEZ ES(Clone)"      -&gt; "Jonnez ES"
		/// Parantez ici kule/sayi Discord'da yalnizca yer kaplar.
		/// </summary>
		static string FriendlyVehicleName(string sceneName) {
			if (string.IsNullOrEmpty(sceneName)) return null;
			string name = sceneName;

			int paren = name.IndexOf('(');
			if (paren >= 0) name = name.Substring(0, paren);
			name = name.Trim();
			if (name.Length == 0) return null;

			// "RCO_RUSCKO12" -&gt; "RUSCKO": on ek kirpilir, sondaki numara atilir.
			if (name.StartsWith("RCO_", StringComparison.OrdinalIgnoreCase))
				name = name.Substring(4);
			name = TrimTrailingDigits(name);
			if (name.Length == 0) return null;

			return Capitalize(name);
		}

		/// <summary>Sondaki rakamlari ve ayiricilari atar: "RUSCKO12" -&gt; "RUSCKO".</summary>
		static string TrimTrailingDigits(string s) {
			int end = s.Length;
			while (end > 0 && s[end - 1] >= '0' && s[end - 1] <= '9') end--;
			while (end > 0 && (s[end - 1] == '/' || s[end - 1] == '-' || s[end - 1] == '_')) end--;
			return s.Substring(0, end);
		}

		/// <summary>Her kelimenin bas harfini buyutur: "satsuma" -&gt; "Satsuma".</summary>
		static string Capitalize(string s) {
			StringBuilder sb = new StringBuilder(s.Length);
			bool atStart = true;
			for (int i = 0; i < s.Length; i++) {
				char c = s[i];
				if (c == '_') { c = ' '; }
				if (c == ' ') { atStart = true; sb.Append(c); continue; }
				sb.Append(atStart ? char.ToUpperInvariant(c) : c);
				atStart = false;
			}
			return sb.ToString().Trim();
		}

		/// <summary>Satir 2: oyun baglami (Ingilizce).</summary>
		static string BuildState() {
			if (SatsumaRegistry.Root == null) return "Main menu";
			if (SteamTransport.HasRemote)
				return SteamTransport.IsHost ? "MSCSteamMP - hosting" : "MSCSteamMP - joined";
			return "Singleplayer";
		}

		// ------------------------------------------------------------------
		// Ayarlar dosyasi
		// ------------------------------------------------------------------

		/// <summary>
		/// Mods klasorundeki mscdiscord.txt okur. Format:
		///   clientId=123456789012345678
		///   enabled=1
		/// Yol verilmezse tahmin edilir: "...\My Summer Car\Mods".
		/// </summary>
		public static void LoadSettings(string modFolder) {
			try {
				if (string.IsNullOrEmpty(modFolder)) modFolder = GuessModFolder();
				if (string.IsNullOrEmpty(modFolder)) return;
				SettingsPath = Path.Combine(modFolder, "mscdiscord.txt");
				if (!File.Exists(SettingsPath)) return;
				string[] lines = File.ReadAllLines(SettingsPath);
				for (int i = 0; i < lines.Length; i++) {
					string line = lines[i];
					if (string.IsNullOrEmpty(line)) continue;
					line = line.Trim();
					if (line.Length == 0 || line[0] == '#') continue;
					int eq = line.IndexOf('=');
					if (eq <= 0) continue;
					string key = line.Substring(0, eq).Trim();
					string val = line.Substring(eq + 1).Trim();
					if (string.Equals(key, "clientId", StringComparison.OrdinalIgnoreCase)) {
						// Bos deger varsayilani EZMEZ: kullanici dosyayi
						// sifirladiginda presence tamamen kapanirdi.
						if (val.Length > 0) ClientId = val;
					}
					else if (string.Equals(key, "enabled", StringComparison.OrdinalIgnoreCase)) {
						Enabled = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
					}
				}
			}
			catch (Exception e) {
				SmpLog.Warning("discord settings could not be read: " + e.Message);
			}
		}

		/// <summary>Ayarlari diske yazar (clientId degistiginde).</summary>
		public static void SaveSettings() {
			try {
				File.WriteAllText(SettingsPath,
					"# MSCSteamMP - Discord Rich Presence\n"
					+ "clientId=" + (ClientId ?? "") + "\n"
					+ "enabled=" + (Enabled ? "1" : "0") + "\n");
			}
			catch (Exception e) {
				SmpLog.Warning("discord settings could not be written: " + e.Message);
			}
		}

		/// <summary>
		/// Mod klasorunu tahmin eder: Unity'de CurrentDirectory oyun kokudur.
		/// </summary>
		static string GuessModFolder() {
			try {
				string baseDir = AppDomain.CurrentDomain.BaseDirectory;
				if (string.IsNullOrEmpty(baseDir)) return null;
				string guess = Path.Combine(baseDir, "Mods");
				return Directory.Exists(guess) ? guess : null;
			}
			catch { return null; }
		}

		/// <summary>Tek satir tani.</summary>
		public static string Describe() {
			string id = string.IsNullOrEmpty(ClientId) ? "no clientId" : ClientId;
			return "discord: " + (Enabled ? "enabled" : "OFF") + "  clientId=" + id
				+ "  link=" + (connected ? "connected" : "not connected")
				+ "  attempts=" + connectAttempts + "  reconnects=" + reconnects
				+ (string.IsNullOrEmpty(lastError) ? "" : "  last-error=" + lastError);
		}

		// ------------------------------------------------------------------
		// Is parcacigi
		// ------------------------------------------------------------------

		static void StartWorker() {
			try {
				stopRequested = false;
				worker = new Thread(WorkerLoop);
				worker.IsBackground = true;   // oyun kapanirken surec asili kalmasin
				worker.Start();
			}
			catch (Exception e) {
				worker = null;
				SmpLog.Warning("discord worker could not start: " + e.Message);
			}
		}

		static void WorkerLoop() {
			while (!stopRequested) {
				connectAttempts++;
				try {
					RunSession();
					lastError = "session returned (pipe closed by Discord)";
				}
				catch (Exception e) {
					// Discord kapali olmak normal; sebebi yine de yaz ki
					// "connected -> 100 ms sonra not connected" belirtisi
					// teşhis edilebilsin.
					lastError = e.GetType().Name + ": " + e.Message;
					SmpLog.Log("discord: session ended: " + lastError);
				}
				connected = false;
				lastSentDetails = " ";
				lastSentState = " ";
				if (!warnedMissing) {
					warnedMissing = true;
					SmpLog.Log("discord: not connected (" + lastError
						+ "). Discord desktop client should be running; retrying every 20 s.");
				}
				for (int i = 0; i < 40 && !stopRequested; i++) Thread.Sleep(500);
			}
		}

		/// <summary>
		/// Tek bir baglanti oturumu: handshake, sonra frame alisverisi.
		///
		/// PIPE ACILISI NEDEN Win32: oyunun Mono profili (mscorlib 2.0.5)
		/// `System.IO.Pipes` ICERMEZ — derleme bile hata veriyordu. Bu yuzden
		/// `kernel32!CreateFileW` dogrudan cagriliyor.
		/// </summary>
		static void RunSession() {
			IntPtr pipe = InvalidHandleValue;
			for (int idx = 0; idx < 4; idx++) {
				string path = "\\\\.\\pipe\\discord-ipc-" + idx.ToString(CultureInfo.InvariantCulture);
				pipe = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
				if (pipe != InvalidHandleValue) break;
				if (stopRequested) return;
			}
			if (pipe == InvalidHandleValue) return;

			try {
				WriteHandshake(pipe);
				ReadFrame(pipe);   // cevap: opcode 1, DISPATCH/READY

				connected = true;
				warnedMissing = false;
				lastError = "";
				reconnects++;
				SmpLog.Print("discord: connected. Presence is live."
					+ (reconnects > 1 ? " (reconnect #" + reconnects + ")" : ""));

				// Ilk activity'yi handshake'ten SONRA hemen gonder: Discord
				// READY'yi gorene kadar SET_ACTIVITY kabul etmiyor.
				EnqueueFreshActivity();

				while (!stopRequested) {
					string queued = null;
					lock (ioLock) {
						if (outgoing.Count > 0) queued = outgoing.Dequeue();
					}
					if (queued != null) {
						WriteFrame(pipe, 1, queued);
					}
					else if ((Environment.TickCount - lastPingSent) > 15000) {
						lastPingSent = Environment.TickCount;
						try { WriteFrame(pipe, 1, "{\"cmd\":\"PING\",\"nonce\":\"p\"}"); }
						catch { break; }
					}

					// Discord her seyi event olarak yollar; okumamiz gerekmez
					// ama buffer dolmasin diye varsa bir kere okuyup atiyoruz.
					TryReadFrame(pipe);

					Thread.Sleep(100);
				}
			}
			finally {
				CloseHandle(pipe);
			}
		}

		/// <summary>Baglanti kurulur kurulmaz mevcut durumu bir kez yollar.</summary>
		static void EnqueueFreshActivity() {
			try {
				string details = BuildDetails();
				string state = BuildState();
				lastSentDetails = details;
				lastSentState = state;
				lock (ioLock) {
					outgoing.Clear();
					outgoing.Enqueue(BuildActivity(details, state, false));
				}
			}
			catch { }
		}

		/// <summary>
		/// Handshake (opcode 0). Payload JSON OLMALI — binary struct kabul
		/// edilmiyor (kod 1003 "Invalid JSON"). Kanit yukarida.
		/// </summary>
		static void WriteHandshake(IntPtr pipe) {
			handshakeBuf.Length = 0;
			handshakeBuf.Append("{\"v\":1,\"client_id\":\"").Append(Escape(ClientId)).Append("\"}");
			WriteFrame(pipe, 0, handshakeBuf.ToString());
		}

		static void WriteFrame(IntPtr pipe, int opcode, string json) {
			WriteFrame(pipe, opcode, Encoding.UTF8.GetBytes(json));
		}

		static void WriteFrame(IntPtr pipe, int opcode, byte[] payload) {
			byte[] header = new byte[8];
			WriteInt(header, 0, opcode);
			WriteInt(header, 4, payload.Length);
			WriteAll(pipe, header, 8);
			if (payload.Length > 0) WriteAll(pipe, payload, payload.Length);
		}

		// NOT: WriteFile/ReadFile imzalari (hFile, lpBuffer, nSize, out n,
		// lpOverlapped) -> 5 parametre.
		static void WriteAll(IntPtr pipe, byte[] buffer, int count) {
			int written = 0;
			while (written < count) {
				int n;
				if (!WriteFile(pipe, buffer, count - written, out n, IntPtr.Zero) || n <= 0)
					throw new IOException("discord pipe write failed");
				written += n;
			}
		}

		/// <summary>Bloklayan okuma: handshake cevabi icin.</summary>
		static byte[] ReadFrame(IntPtr pipe) {
			byte[] header = new byte[8];
			ReadExact(pipe, header, 8);
			int len = ReadInt(header, 4);
			if (len < 0 || len > 4 * 1024 * 1024) throw new IOException("discord frame too large");
			byte[] payload = new byte[len];
			if (len > 0) ReadExact(pipe, payload, len);
			return payload;
		}

		/// <summary>
		/// Bloklamadan dene: PeekNamedPipe ile "veri var mi" sorar, yoksa
		/// hic dokunmaz. Boylece Discord'un event'leri donguyu bloklamaz.
		/// </summary>
		static void TryReadFrame(IntPtr pipe) {
			// PeekNamedPipe imzasi:
			//   (handle, buffer, size, out lpBytesRead,
			//                   out lpTotalBytesAvail, out lpBytesLeftThisMessage)
			// "Veri var mi" sorusunu lpTotalBytesRead DEGIL, IKINCI out
			// (lpTotalBytesAvail) yanitlar; ilk out her zaman 0'dir.
			int peeked, avail, bytesLeft;
			if (!PeekNamedPipe(pipe, IntPtr.Zero, 0, out peeked, out avail, out bytesLeft))
				throw new IOException("discord pipe closed");
			if (avail <= 0) return;
			try { ReadFrame(pipe); } catch { /* yarim frame: sonrakini bekle */ }
		}

		static void ReadExact(IntPtr pipe, byte[] buffer, int count) {
			int read = 0;
			while (read < count) {
				int n;
				if (!ReadFile(pipe, buffer, count - read, out n, IntPtr.Zero) || n <= 0)
					throw new EndOfStreamException("discord pipe closed");
				read += n;
			}
		}

		// ------------------------------------------------------------------
		// Win32 (System.IO.Pipes bu Mono profilinde YOK)
		// ------------------------------------------------------------------

		static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
		const uint GENERIC_READ = 0x80000000;
		const uint GENERIC_WRITE = 0x40000000;
		const uint OPEN_EXISTING = 3;

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
			IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes,
			IntPtr templateFile);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool ReadFile(IntPtr handle, byte[] buffer, int bytesToRead,
			out int bytesRead, IntPtr overlapped);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool WriteFile(IntPtr handle, byte[] buffer, int bytesToWrite,
			out int bytesWritten, IntPtr overlapped);

		// buffer parametresi IntPtr: PeekNamedPipe'a byte[] vermek
		// marshalling hatasi veriyordu.
		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool PeekNamedPipe(IntPtr handle, IntPtr buffer, int bufferSize,
			out int bytesRead, out int totalBytesRead, out int bytesLeftThisMessage);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool CloseHandle(IntPtr handle);

		static void WriteInt(byte[] b, int offset, int value) {
			b[offset] = (byte)(value & 0xFF);
			b[offset + 1] = (byte)((value >> 8) & 0xFF);
			b[offset + 2] = (byte)((value >> 16) & 0xFF);
			b[offset + 3] = (byte)((value >> 24) & 0xFF);
		}

		static int ReadInt(byte[] b, int offset) {
			return b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);
		}

		// ------------------------------------------------------------------
		// JSON (elle yaziliyor: Newtonsoft bagimliligi istemiyoruz)
		// ------------------------------------------------------------------

		static int nonce;

		static string BuildActivity(string details, string state, bool clear) {
			activityBuf.Length = 0;
			activityBuf.Append("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":").Append(ProcessIdCurrent())
				.Append(",\"activity\":{");
			if (clear) {
				activityBuf.Append("}},\"nonce\":\"").Append(NextNonce()).Append("\"}");
				return activityBuf.ToString();
			}

			if (!string.IsNullOrEmpty(details))
				activityBuf.Append("\"details\":\"").Append(Escape(details)).Append("\",");
			activityBuf.Append("\"state\":\"").Append(Escape(state)).Append("\",");
			activityBuf.Append("\"timestamps\":{\"start\":").Append(startedAtSeconds()).Append("},");
			if (!string.IsNullOrEmpty(LARGE_IMAGE))
				activityBuf.Append("\"large_image\":\"").Append(Escape(LARGE_IMAGE)).Append("\",");
			if (!string.IsNullOrEmpty(SMALL_IMAGE))
				activityBuf.Append("\"small_image\":\"").Append(Escape(SMALL_IMAGE)).Append("\",");
			// Sondaki virgulu temizle.
			int n = activityBuf.Length;
			if (n > 0 && activityBuf[n - 1] == ',') activityBuf.Length = n - 1;

			activityBuf.Append("}},\"nonce\":\"").Append(NextNonce()).Append("\"}");
			return activityBuf.ToString();
		}

		static string NextNonce() {
			nonce++;
			return nonce.ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Discord "start" olarak Unix zamani (saniye) ister.
		/// FILETIME 1601-01-01 baslangicidir; Unix'e cevirmek icin
		/// 11 644 473 847 saniye dusulur.
		/// </summary>
		static long startTicks;

		static long startedAtSeconds() {
			if (startTicks == 0) {
				try { startTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.Ticks; }
				catch { startTicks = DateTime.UtcNow.Ticks; }
			}
			long unixTicks = startTicks - TimeSpan.TicksPerSecond * 11644473647L;
			return unixTicks / TimeSpan.TicksPerSecond;
		}

		static int pid = -1;

		static int ProcessIdCurrent() {
			if (pid < 0) {
				try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
				catch { pid = 0; }
			}
			return pid;
		}

		/// <summary>JSON kacisi: tirnak, backslash, satir sonu.</summary>
		static string Escape(string s) {
			if (string.IsNullOrEmpty(s)) return "";
			StringBuilder sb = new StringBuilder(s.Length + 8);
			for (int i = 0; i < s.Length; i++) {
				char c = s[i];
				switch (c) {
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < 32) sb.Append(' ');
						else sb.Append(c);
						break;
				}
			}
			return sb.ToString();
		}
	}
}
