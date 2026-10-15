using System;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// CD SES AKIŞI — karşı tarafta .ogg dosyası yoksa.
	///
	/// TASARIM (kullanıcı isteği: "canlı ses akışı ama ping ve lag sorunu olmadan")
	/// =========================================================================
	/// Gönderen, çalan `AudioClip`'in PCM'ini 22050 Hz mono'ya indirip
	/// gerçek zamanlı dilimler (80 ms) hâlinde gönderir. Alan taraf
	/// `AudioClip.Create` ile AYNI UZUNLUKTA bir clip üretir ve parçaları
	/// sırayla içine yazar; clip'i bir kez başlatır. Yani ses motorunun
	/// zamanlaması AudioSource'a bırakılır — her paket için ayrı planlama
	/// yok, dolayısıyla jitter birikmez.
	///
	/// LAG/PING NASIL YÖNETİLİYOR (her satır canlı gerekçe):
	///
	/// 1) `SendUnreliableNoDelay` — paket kaybı 80 ms sessizlik olur, ama
	///    KALICI KAYMA olmaz. Güvenilir kanalda gecikme birikir
	///    (head-of-line blocking); Steam P2P'de kuyruk şişer ve ses
	///    geride kalır. Bu yüzden bilerek güvenilmez seçildi.
	///
	/// 2) `PREBUFFER = 350 ms` — alan taraf 350 ms dolmadan başlatmıyor.
	///    Ani 1-2 kare gecikmesi (30-60 ms) emilir. RTT 100 ms olan
	///    bir bağlantıda bile akıcı.
	///
	/// 3) Gerçek zamanlı pacing — gönderen hedefi `oyuncunun gerçekten
	///    oynattığı konum + 350 ms` olarak hesaplar, saniyede en fazla 10
	///    parça yollar. "Olabildiğince hızlı" gönderim yapmıyoruz; bu
	///    uzun oturumlarda şişme ve zaman kayması üretirdi.
	///
	/// 4) GEÇ GELEN PARÇA ATILIR — `frameIndex + frames <= timeSamples`
	///    ise o paket zaten oynanmış, yazılması bir işe yaramaz; yazılırsa
	///    sesin içine "tekrarlanan örnek" olarak girer. Atılır.
	///
	/// 5) Boşluk = sessizlik, takılma değil — veri gelmeyen bölge sıfır
	///    kalır, AudioSource devam eder. Playhead ASLA geri alınmaz, yani
	///    senkron kayması birikmez.
	///
	/// 6) Ses seviyesi oyunun kendi `CDPlayer/Fetch` FSM'inden okunur
	///    (`Volume`, `db_Required`) → alanda da aynı ayar uygulanır. Yani
	///    uzaktaki oyuncu da sesi kısabilir/kapatabilir.
	///
	/// YALNIZCA CD: radyo sesleri oyun klasörü içinde gömülü
	/// (`Radio/Folk`, `Folk/Ads` listeleri her iki tarafta da var), dosya
	/// aktarımına gerek yok. Radyo için sadece kanal/ses senkronu yeter.
	///
	/// CD PLAYER EKRANI: alan taraf ayrıca `RadioPlay` (47) paketini alıp
	/// oyunun kendi `PLAY CD` olayını yeniden üretir; böylece CD player
	/// arayüzü, parça numarası ve "CD modunda" göstergesi aynı görünür.
	/// Akış için ayrı bir AudioSource kullanılır ki FSM'in `SetAudioClip`
	/// çağrısıyla çakışmasın; nesne CDPlayer'ın ALTINDA olduğu için
	/// konum/uzaklık sesleri doğru uygulanır.
	/// </summary>
	public static class CdAudioStream {

		// ------------------------------------------------------------------
		// Ayarlar
		// ------------------------------------------------------------------

		/// <summary>Hedef örnekleme hızı. 22050 mono = 44.1 KB/sn ham.</summary>
		const int RATE = 22050;

		/// <summary>Bir parçadaki kare sayısı (80 ms).</summary>
		const int CHUNK_FRAMES = 1764;

		/// <summary>Güvenilmez paket güvenli üst sınırı (bayt).</summary>
		const int MAX_PACKET = 1100;

		/// <summary>
		/// Alanda başlatmadan önce doldurulacak süre. RTT + paket jitter'ı
		/// karşılamak için gerekir.
		/// </summary>
		const float PREBUFFER = 0.35f;

		/// <summary>Gönderim pacing aralığı (saniye).</summary>
		const float TICK = 0.04f;

		/// <summary>Saniyede en fazla kaç parça (24 → daha hızlı doluş).</summary>
		const int MAX_CHUNKS_PER_SEC = 24;

		/// <summary>Alan tarafta ayrılacak en uzun akış (dakika). 8 dk =
		/// 22050*480 float32 ≈ 42 MB; üstü oyunu gereksiz yorar.</summary>
		const int MAX_MINUTES = 8;

		// ------------------------------------------------------------------
		// Telemetri
		// ------------------------------------------------------------------

		public static int HeadersSent;
		public static int ChunksSent;
		public static int FramesSent;
		public static int HeadersApplied;
		public static int ChunksApplied;
		public static int LateDropped;
		public static int HeadersIgnored;
		public static int Underruns;
		public static bool Sending;
		public static bool Receiving;
		public static string LastError = "";

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		static float nextTick;

		// gönderen
		static AudioClip sendClip;
		static string sendCd = "";
		static int sendNext;
		static int sendStart;
		static float sendStartTime;
		static float[] readBuf;

		// alan
		static AudioClip playClip;
		static GameObject playGo;
		static AudioSource playSrc;
		static int playWrite;        // bir sonraki yazılacak kare
		static int playStarted;      // timeSamples
		static bool playRunning;
		static float playLastProgress;
		static string playCd = "";
		static int playTotal;

		/// <summary>Ana iş parçığından her kare çağrılır.</summary>
		public static void Tick() {
			try {
				if (!SteamTransport.HasRemote) {
					Reset();
					return;
				}
				float now = Time.time;
				if (now < nextTick) return;
				nextTick = now + TICK;

				AudioSource src = LocalPlayingSource();
				if (src != null && src.clip != null) SendTick(src);
				else StopSending();

				ApplyVolume();
			}
			catch (Exception e) {
				if (LastError != e.GetType().Name) {
					LastError = e.GetType().Name;
					SmpLog.Warning("cd audio stream failed: " + e.GetType().Name + ": " + e.Message);
				}
			}
		}

		// ------------------------------------------------------------------
		// Ortak: CD player'ın çalan AudioSource'u
		// ------------------------------------------------------------------

		static PlayMakerFSM CdUpdateFsm() {
			return RadioSync.FindFsm("CDPlayer", "Update");
		}

		static GameObject CdGo() {
			PlayMakerFSM f = CdUpdateFsm();
			return (f != null) ? f.gameObject : null;
		}

		/// <summary>
		/// CDPlayer altında çalmakta olan AudioSource. FSM bize
		/// `Audio` (AudioClip) değişkenini verir; yoksa nesnedeki
		/// AudioSource'ları tararız.
		/// </summary>
		static AudioSource LocalPlayingSource() {
			try {
				PlayMakerFSM f = CdUpdateFsm();
				if (f == null) return null;
				AudioClip want = null;
				FsmObject o = FsmNames.Obj(f, "Audio");
				if (o != null && o.Value != null) want = o.Value as AudioClip;
				if (want == null) {
					FsmString cs = FsmNames.Str(f, "CurrentCD");
					if (cs == null || string.IsNullOrEmpty(cs.Value)) return null;
				}

				AudioSource[] all = f.gameObject.GetComponentsInChildren<AudioSource>(true);
				for (int i = 0; all != null && i < all.Length; i++) {
					AudioSource a = all[i];
					if (a == null || !a.isPlaying || a.clip == null) continue;
					if (want != null && a.clip != want) continue;
					return a;
				}
			}
			catch { }
			return null;
		}

		// ------------------------------------------------------------------
		// GÖNDEREN
		// ------------------------------------------------------------------

		static void StopSending() {
			if (sendClip == null) return;
			sendClip = null;
			Sending = false;
			SmpTrace.Act("RADIO", "audiostream", "stopped sending");
		}

		static void SendTick(AudioSource src) {
			AudioClip clip = src.clip;

			// Parça değişti / akış yeni başlıyor → başlık paketi.
			if (clip != sendClip) {
				sendClip = clip;
				sendCd = CurrentCdName();
				sendStart = src.timeSamples;
				sendStartTime = Time.time;
				// Alan tarafın hazır olması için önbellek kadar İLERİDEN başla.
				sendNext = sendStart;
				if (readBuf == null || readBuf.Length < CHUNK_FRAMES) readBuf = new float[CHUNK_FRAMES];
				Sending = true;

				MemoryStream hs = Protocol.Begin(Protocol.Msg.CdAudioHeader);
				BinaryWriter hw = new BinaryWriter(hs);
				hw.Write(sendCd);
				hw.Write(clip.name != null ? clip.name : "cd");
				hw.Write(RATE);
				hw.Write(Mathf.Min(clip.samples, RATE * 60 * MAX_MINUTES));
				hw.Write(CHUNK_FRAMES);
				hw.Flush();
				if (SteamTransport.SendReliable(Protocol.Finish(hs))) {
					HeadersSent++;
					SmpTrace.Act("RADIO", "audiostream", "header " + sendCd + " '"
						+ clip.name + "' " + clip.frequency + "Hz->" + RATE
						+ "Hz  " + clip.samples + " frames");
				}
				return;
			}

			int played = src.timeSamples;

			// Atlama / geri sarma: oyuncu parçayı değiştirdiyse veya
			// başa sarıyorsa akışı yeni konuma yapıştır.
			if (played > sendNext + RATE) sendNext = played;
			else if (played < sendNext - RATE * 3) sendNext = played;

			// Gerçek zamanlı pacing: hedef = oynanan konum + PREBUFFER.
			float ahead = (Time.time - sendStartTime) + PREBUFFER;
			int target = sendStart + (int)(ahead * RATE);
			int budget = MAX_CHUNKS_PER_SEC * 2;
			int sent = 0;
			while (sendNext < target && sent < budget) {
				if (!SendChunk(clip, sendNext)) break;
				sendNext += CHUNK_FRAMES;
				FramesSent += CHUNK_FRAMES;
				sent++;
			}
			ChunksSent += sent;
		}

		static string CurrentCdName() {
			PlayMakerFSM f = CdUpdateFsm();
			if (f == null) return "CD1";
			FsmString s = FsmNames.Str(f, "CurrentCD");
			if (s != null && !string.IsNullOrEmpty(s.Value)) return s.Value;
			return "CD1";
		}

		/// <summary>
		/// Bir dilimi oku, 22050 mono'ya indir, parçala ve gönder.
		/// `GetData` başarısızsa (sıkıştırılmış clip) false döner ve akış
		/// başlamaz — oyun durumunu bozmamak için sessizce vazgeçilir.
		/// </summary>
		static bool SendChunk(AudioClip clip, int atFrame) {
			try {
				int frames = CHUNK_FRAMES;
				if (atFrame < 0) return false;
				if (atFrame + frames > clip.samples) frames = clip.samples - atFrame;
				if (frames <= 0) return false;

				int step = clip.frequency / RATE;         // genelde 2 (44.1k -> 22.05k)
				if (step < 1) step = 1;
				int srcNeed = frames * step;
				float[] tmp = new float[srcNeed];
				if (!clip.GetData(tmp, atFrame * step)) {
					// Bazı platformlarda ses verisi bellekte değil; indirmeyi dene.
					if (!clip.LoadAudioData()) return false;
					if (!clip.GetData(tmp, atFrame * step)) return false;
				}

				// Mono'ya indir + int16'ya çevir.
				byte[] pcm = new byte[frames * 2];
				for (int i = 0; i < frames; i++) {
					float v = 0f;
					for (int k = 0; k < step; k++) {
						int j = i * step + k;
						if (j < srcNeed) v += tmp[j];
					}
					v /= step;
					if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
					short s16 = (short)(v * 32767f);
					pcm[i * 2] = (byte)(s16 & 0xFF);
					pcm[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
				}

				// 1100 bayt sınırına göre parçala (header + frameIndex + veri).
				int maxData = MAX_PACKET - 6;
				int off = 0;
				bool firstFrag = true;
				while (off < pcm.Length) {
					int n = Math.Min(maxData, pcm.Length - off);
					MemoryStream ms = Protocol.Begin(Protocol.Msg.CdAudioData);
					BinaryWriter w = new BinaryWriter(ms);
					w.Write(atFrame + off / 2);   // frameIndex (mono: 2 bayt=1 kare)
					w.Write((byte)(pcm.Length / 2));       // toplam kare
					w.Write((byte)(off / 2));              // parça içi başlangıç karesi
					w.Write((byte)0);
					w.Write(pcm, off, n);
					w.Flush();
					if (!SteamTransport.SendUnreliableNoDelay(Protocol.Finish(ms))) return firstFrag;
					off += n;
					firstFrag = false;
				}
				return true;
			}
			catch (Exception e) {
				LastError = e.GetType().Name;
				return false;
			}
		}

		// ------------------------------------------------------------------
		// ALAN
		// ------------------------------------------------------------------

		/// <summary>Başlık paketi: clip'i oluştur, durumu sıfırla.</summary>
		public static void OnHeader(BinaryReader r) {
			try {
				string cd = r.ReadString();
				string clipName = r.ReadString();
				int rate = r.ReadInt32();
				int total = r.ReadInt32();
				int chunk = r.ReadInt32();
				if (rate < 4000 || rate > 48000) return;
				if (chunk <= 0 || chunk > 20000) return;
				if (total <= 0) total = rate * 60;
				if (total > rate * 60 * MAX_MINUTES) total = rate * 60 * MAX_MINUTES;

				StopReceiving();

				GameObject go = CdGo();
				if (go == null) return;

				// CIMRILIK ONLEME: bu CD karsida yukluyse yerel calar
				// (RadioPlay PLAY CD tekrari), akis cift ses yapardi.
				// Yalnizca dosyasi olmayana akar.
				if (CdMusicImport.SongCount(cd) > 0) {
					HeadersIgnored++;
					SmpTrace.Act("RADIO", "audiostream", "header ignored " + cd
						+ " (local copy plays it)");
					return;
				}

				// CDPlayer'ın ALTINDA ayrı bir ses kaynağı: FSM'in
				// SetAudioClip çağrısıyla çakışmasın ama konum/uzaklık doğru
				// uygulansın.
				string cname = "SMP_CdAudio_" + clipName;
				playGo = new GameObject(cname);
				playGo.transform.parent = go.transform;
				playGo.transform.localPosition = Vector3.zero;

				playSrc = playGo.AddComponent<AudioSource>();
				playSrc.playOnAwake = false;
				playSrc.loop = false;
				playSrc.spatialBlend = 1f;      // araç sesi: 3D
				playSrc.rolloffMode = AudioRolloffMode.Linear;
				playSrc.minDistance = 1f;
				playSrc.maxDistance = 25f;

				playClip = AudioClip.Create("stream_" + clipName, total, 1, rate, false);
				playSrc.clip = playClip;

				playWrite = 0;
				playStarted = 0;
				playRunning = false;
				playLastProgress = Time.time;
				playCd = cd;
				playTotal = total;
				Receiving = true;
				HeadersApplied++;

				SmpTrace.Act("RADIO", "audiostream", "header applied " + cd
					+ " '" + clipName + "' " + total + " frames @ " + rate + "Hz"
					+ " (prebuffer " + (PREBUFFER * 1000f).ToString("F0") + "ms)");
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("cd audio header failed: " + e.GetType().Name);
			}
		}

		/// <summary>Veri parçası: clip'in içine yaz, gerekirse başlat.</summary>
		public static void OnData(BinaryReader r) {
			try {
				if (!Receiving || playClip == null || playSrc == null) return;
				int atFrame = r.ReadInt32();
				int frames = r.ReadByte();
				int partFrame = r.ReadByte();
				r.ReadByte();               // yastık
				if (frames <= 0 || partFrame < 0) return;

				int count = Math.Min(frames, playTotal - atFrame);
				if (count <= 0) return;

				float[] f = new float[count];
				for (int i = 0; i < count; i++) {
					short s16 = 0;
					try {
						s16 = (short)(r.ReadByte() | (r.ReadByte() << 8));
					}
					catch { return; }
					f[i] = s16 / 32767f;
				}

				int start = atFrame + partFrame;
				if (start >= playTotal) return;
				if (start + count > playTotal) count = playTotal - start;
				if (count <= 0) return;

				if (!playRunning) {
					// ÖNBELLEK DOLANA KADAR BEKLE — jitter emilir.
					if (start + count < (int)(PREBUFFER * RATE)) return;
					if (!playClip.SetData(f, start)) return;
					playSrc.Play();
					playStarted = 0;
					playRunning = true;
					ChunksApplied++;
					return;
				}

				// GEÇ GELEN PARÇAYI AT: oynanmış bölgeye yazmak sesi bozar.
				if (start + count <= playSrc.timeSamples) {
					LateDropped++;
					return;
				}

				if (playClip.SetData(f, start)) {
					ChunksApplied++;
					playLastProgress = Time.time;
				}
			}
			catch (Exception e) {
				// Bozuk paket: Telemetry zaten sayıyor. Uzun süre tekrarlanırsa
				// akışı durdurmak daha doğru (yarım kalmış veri işe yaramaz).
				Telemetry.BadPackets++;
				if (LastError != "data:" + e.GetType().Name) {
					LastError = "data:" + e.GetType().Name;
					SmpTrace.Act("RADIO", "audiostream", "bad data packet: " + e.GetType().Name);
				}
				if (Receiving && Time.time - playLastProgress > 5f) StopReceiving();
			}
		}

		/// <summary>Alan tarafta oyunun ses ayarını uygular.</summary>
		//
		// HANGİ DEĞİŞKENDEN? ÖNCE `RadioChannels/Fetch` → `Volume`.
		//
		// CANLI LOG KANITI (11:48:18.860):
		//   [SatsumaFSM] 'Use' -> 'Wait player' @ .../pivot_radio/cd player(Clone)
		//   [ACT] RADIO float Volume 0.00 -> 0.10  @ RADIO/RadioChannels
		//
		// Yani CD player'ın kendi ses düğmesi `CDPlayer/Fetch.Volume`'a
		// DEĞİL, global `RADIO/RadioChannels.Volume`'a yazıyor. Önce
		// `CDPlayer/Fetch.Volume`'u okuyorduk (hiç değişmediği için alanda
		// ses hep 1.0'da kalıyordu — kısınca karşıda kısılmıyordu).
		//
		// Volume == 0 oyunun "radyo kapalı" anlamı; Clamp01(0)=0 zaten
		// susturur, ayrı bool gerekmiyor.
		static void ApplyVolume() {
			try {
				if (!Receiving || playSrc == null) return;

				float vol = 1f;
				PlayMakerFSM rc = RadioSync.FindFsm("RadioChannels", "Fetch");
				if (rc != null) {
					FsmFloat fv = FsmNames.Float(rc, "Volume");
					if (fv != null) vol = fv.Value;
				}
				else {
					// CDPlayer/Fetch yedeği: oyunun "No sound" durumu
					// db_Required'i okuyup Volume'u 0'lıyor.
					PlayMakerFSM fetch = RadioSync.FindFsm("CDPlayer", "Fetch");
					if (fetch != null) {
						FsmFloat fv = FsmNames.Float(fetch, "Volume");
						if (fv != null) vol = fv.Value;
						FsmBool req = FsmNames.Bool(fetch, "db_Required");
						if (req != null && !req.Value) vol = 0f;
					}
				}
				playSrc.volume = Mathf.Clamp01(vol);
				playSrc.mute = (vol <= 0.001f);

				// Akış kesildiyse (60 sn sessizlik) durumu temizle ki
				// sonraki parçada yeni başlık çalışsın.
				if (playRunning && Time.time - playLastProgress > 60f) {
					SmpLog.Warning("cd audio stream stalled at " + playSrc.timeSamples
						+ "/" + playTotal + " — dropping");
					StopReceiving();
				}
			}
			catch { }
		}

		public static void StopReceiving() {
			Receiving = false;
			playRunning = false;
			playClip = null;
			playSrc = null;
			playGo = null;
			playTotal = 0;
			playWrite = 0;
		}

		/// <summary>Sahne değişimi / bağlantı kopması.</summary>
		public static void Reset() {
			StopSending();
			StopReceiving();
		}

		// ------------------------------------------------------------------
		// Tanı
		// ------------------------------------------------------------------

		public static string Describe() {
			string mode = Sending ? "SENDING" : (Receiving ? "RECEIVING" : "idle");
			string extra = Sending
				? ("  send hdr=" + HeadersSent + " chunks=" + ChunksSent)
				: ("  recv hdr=" + HeadersApplied + " chunks=" + ChunksApplied
					+ " late=" + LateDropped);
			return "cd audio: " + mode + extra
				+ "  prebuffer=" + (PREBUFFER * 1000f).ToString("F0") + "ms"
				+ "  rate=" + RATE + "Hz mono"
				+ (Receiving ? ("  buffered=" + playSrc.timeSamples + "/" + playTotal) : "");
		}
	}
}