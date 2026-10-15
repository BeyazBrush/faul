using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// KAYIT DOSYASI AKTARIMI (JIP): host'un kaydını client'a gönderir.
	///
	/// NEDEN GEREKLİ: modun parça/cıvata tablosu YOL tabanlıdır ve iki tarafın
	/// AYNI kaydı yüklemeyi varsayar (README: "Both players must load the same
	/// save, otherwise the trees differ and the ids do not match"). Karşı taraf
	/// farklı bir kayıttan gelirse tablo hash'i uyuşmaz ve montaj/cıvatlar
	/// kayar. Bu modül o varsayımı elle kopyalama yerine ağdan karşılar.
	///
	/// NEDEN OTOMATİK DEĞİL, İSTEKLE ÇALIŞIR: `smp getsave` yazana kadar host
	/// HİÇBİR ŞEY göndermez. Sebep güvenlik: karşı tarafın kaydını sessizce
	/// ezmek, oyuncunun kendi ilerlemesini yok etmek demektir. İstek açıkça
	/// geldiğinde yedek alınır ve doğrulanır.
	///
	/// GÜVENLİK KATMANLARI (hepsi zorunlu):
	///   1. Veri bir GEÇİCİ dosyaya yazılır (<kayit>.incoming). Orijinal dosya
	///      taşınana kadar hiçbir zaman dokunulmaz.
	///   2. Tamamlanınca BOYUT ve CRC32 doğrulanır. Uyuşmazsa geçici dosya
	///      silinir, orijinale hiçbir şey olmaz.
	///   3. Yerine geçmeden ÖNCE orijinalin yedeği alınır:
	///      savegame.save.bak-yyyyMMdd-HHmmss
	///   4. Her adım loglanır; başarısızlıkta client host'a neden olduğunu
	///      bildirir (SaveGameComplete geri yönünde).
	///
	/// BU İD'LER v22'DE "ÖKSÜZ" BIRAKILMIŞTI: `SaveGameChunk` (60) ve
	/// `SaveGameComplete` (61) protokolde tanımlıydı, aktaran `SaveGameTransfer`
	/// sınıfı ise hiç çağrılmadığı için silinmişti. Artık gerçek bir uygulama
	/// var ve formatı tests\WireProbe.cs'ta doğrulanıyor.
	/// </summary>
	public static class SaveTransfer {

		/// <summary>Tek parçanın yük boyu. 32 KB paket sınırının çok altında
		/// (Protocol.MAX_PACKET_SIZE): 8 KB + 14 bayt başlık + 5 bayt protokol.</summary>
		public const int CHUNK_BYTES = 8 * 1024;

		/// <summary>Kare başına en fazla kaç parça gönderilir. P2P güvenilir
		/// kanalını boğmamak için: 8 KB × 2 ≈ 16 KB/kare, 60 fps'te ~1 MB/s.
		/// Bir 500 KB kayıt ~0.3 sn'de biter.</summary>
		public const int CHUNKS_PER_FRAME = 2;

		/// <summary>Aktarım zaman aşımı (s). Karşı taraf donarsa sonsuza dek
		/// beklemeden vazgeçeriz ve geçici dosyayı sileriz.</summary>
		public const float TIMEOUT = 120f;

		/// <summary>Dosya adı. Oyunun kendi kaydı (bkz. SaveTags).</summary>
		public const string SAVE_NAME = "savegame.save";

		// ---- Durum: GÖNDERİCİ (host) ----
		static byte[] sendBuffer;
		static uint sendCrc;
		static int sendNext;          // siradaki parça indeksi
		static int sendCount;         // toplam parça
		static bool sending;

		// ---- Durum: ALICI (client) ----
		static Stream incoming;       // geçici dosya akışı
		static uint recvCrc;
		static uint recvSize;
		static int recvNext;          // sıradan beklenen indeks
		static int recvCount;
		static bool[] recvGot;        // hangi parça geldi
		static float recvStartedAt = -1f;

		// ---- Teşhis ----
		/// <summary>Gönderilen parça sayısı (host).</summary>
		public static int SentChunks;
		/// <summary>Alınan parça sayısı (client).</summary>
		public static int ReceivedChunks;
		/// <summary>Bu oturumda tamamlanan aktarım sayısı.</summary>
		public static int Completed;
		/// <summary>Başarısız / iptal edilen aktarım sayısı.</summary>
		public static int Failed;
		/// <summary>Son işlemin özeti (smp status satırı).</summary>
		public static string LastResult = "-";

		/// <summary>Şu anda aktarım sürüyor mu?</summary>
		public static bool Busy { get { return sending || incoming != null; } }

		// ------------------------------------------------------------------
		// YOL
		// ------------------------------------------------------------------

		/// <summary>
		/// Kayıt dosyasının tam yolu.
		///
		/// `Application.dataPath` = ".../My Summer Car/My Summer Car_Data";
		/// kayıt dosyası onun bir üst klasöründedir (SaveTags ile aynı çözümleme).
		/// NoInlining: dataPath bir ECall'tir, inline edilirse istisna buradaki
		/// catch'i değil çağıranı vurur (SmpLog'daki ayni desen).
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static string SavePath() {
			try {
				string dataPath = UnityEngine.Application.dataPath;
				if (string.IsNullOrEmpty(dataPath)) return null;
				int i = dataPath.LastIndexOf('\\');
				if (i <= 0) return null;
				return Path.Combine(dataPath.Substring(0, i), SAVE_NAME);
			}
			catch { return null; }
		}

		static string TempPath(string savePath) { return savePath + ".incoming"; }
		static string BackupPath(string savePath) {
			return savePath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
		}

		// ------------------------------------------------------------------
		// CRC32 (taban halinde: .NET 3.5'te hazır CRC32 yok)
		// ------------------------------------------------------------------

		static readonly uint[] CrcTable = BuildCrcTable();
		static uint[] BuildCrcTable() {
			uint[] t = new uint[256];
			for (uint n = 0; n < 256; n++) {
				uint c = n;
				for (int k = 0; k < 8; k++)
					c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
				t[n] = c;
			}
			return t;
		}

		static uint Crc32(byte[] buf, int offset, int count) {
			uint c = 0xFFFFFFFFu;
			for (int i = 0; i < count; i++)
				c = CrcTable[(c ^ buf[offset + i]) & 0xFF] ^ (c >> 8);
			return c ^ 0xFFFFFFFFu;
		}

		// ------------------------------------------------------------------
		// GÖNDERİCİ TARAF (host)
		// ------------------------------------------------------------------

		/// <summary>
		/// Client kaydı istedi. Host dosyayı okur ve parçaları kuyruğa alır.
		/// HOST DEĞİLSE hiçbir şey yapmaz (istemci yanlışlıkla istemiş olabilir).
		/// </summary>
		public static bool BeginSend() {
			AbortSend("yeni istek geldi");
			if (!SteamTransport.IsHost || !SteamTransport.HasRemote) {
				LastResult = "not host / no peer";
				return false;
			}
			string path = SavePath();
			if (path == null || !File.Exists(path)) {
				LastResult = "no save file at " + (path ?? "?");
				SmpLog.Warning("save transfer: host has no '" + SAVE_NAME + "' to send.");
				// CLIENT'A SEBEBİ BİLDİR: yoksa istemci sessizce sonsuza dek
				// bekler ve kullanıcı "aktarım bozuk mu" sanır.
				SendFail("host has no " + SAVE_NAME);
				return false;
			}
			try {
				sendBuffer = File.ReadAllBytes(path);
				sendCrc = Crc32(sendBuffer, 0, sendBuffer.Length);
				sendCount = (sendBuffer.Length + CHUNK_BYTES - 1) / CHUNK_BYTES;
				if (sendCount <= 0) sendCount = 1;
				if (sendCount > 65535) {
					// ushort indeks: 65535 × 8 KB ≈ 512 MB. Üstü pratikte imkansız.
					LastResult = "save too large (" + sendCount + " chunks)";
					SendFail("save too large");
					AbortSend("dosya çok büyük");
					return false;
				}
				sendNext = 0;
				sending = true;
				SmpLog.Print("save transfer: sending " + sendBuffer.Length + " byte in "
					+ sendCount + " chunk(s) to the peer.");
				return true;
			}
			catch (Exception e) {
				LastResult = "read failed: " + e.Message;
				SendFail("read failed");
				AbortSend("okunamadı");
				return false;
			}
		}

		/// <summary>Host, aktaramayacağını client'a bildirir.</summary>
		static void SendFail(string why) {
			try {
				MemoryStream ms = Protocol.Begin(Protocol.Msg.SaveGameComplete);
				BinaryWriter w = new BinaryWriter(ms);
				Protocol.WriteSaveComplete(w, new Protocol.SaveCompleteData {
					FileSize = 0, Crc32 = 0, Ok = false, Message = why ?? "failed"
				});
				SteamTransport.SendReliable(Protocol.Finish(ms));
			}
			catch { }
		}

		/// <summary>Gönderimi iptal eder (yarım kalan hiçbir şey yazmaz).</summary>
		public static void AbortSend(string why) {
			if (sending) SmpLog.Warning("save transfer: send aborted (" + why + ").");
			sending = false;
			sendBuffer = null;
			sendNext = 0;
			sendCount = 0;
		}

		/// <summary>
		/// Kare başına CHUNKS_PER_FRAME parça gönderir. Host tarafından çağrılır.
		/// Parça başına TAM başlık gider (fileSize + crc32 + index + count), böylece
		/// ilk parça kaybolsa bile alıcı hâlâ kendini toparlayabilir.
		/// </summary>
		static void TickSend() {
			if (!sending || sendBuffer == null) return;
			float now = Time.realtimeSinceStartup;
			for (int i = 0; i < CHUNKS_PER_FRAME && sendNext < sendCount; i++) {
				int off = sendNext * CHUNK_BYTES;
				int len = sendBuffer.Length - off;
				if (len > CHUNK_BYTES) len = CHUNK_BYTES;
				if (len < 0) len = 0;
				byte[] packet;
				try {
					MemoryStream ms = Protocol.Begin(Protocol.Msg.SaveGameChunk);
					BinaryWriter w = new BinaryWriter(ms);
					Protocol.WriteSaveChunk(w, new Protocol.SaveChunkData {
						FileSize = (uint)sendBuffer.Length,
						Crc32 = sendCrc,
						Index = (ushort)sendNext,
						Count = (ushort)sendCount,
						Data = sendBuffer,
						Offset = off,
						Length = len
					});
					packet = Protocol.Finish(ms);
				}
				catch (Exception e) {
					AbortSend("paket üretilemedi: " + e.Message);
					Failed++;
					return;
				}
				if (!SteamTransport.SendReliable(packet)) {
					// Kanal kapandı: vazgeç. Client zaten hiçbir şey yazmadı.
					AbortSend("kanal reddetti");
					Failed++;
					return;
				}
				SentChunks++;
				sendNext++;
			}

			if (sendNext >= sendCount) {
				// Bitti: kapanış paketi. Client buna bakarak doğrulama yapar.
				try {
					MemoryStream ms = Protocol.Begin(Protocol.Msg.SaveGameComplete);
					BinaryWriter w = new BinaryWriter(ms);
					Protocol.WriteSaveComplete(w, new Protocol.SaveCompleteData {
						FileSize = (uint)sendBuffer.Length,
						Crc32 = sendCrc,
						Ok = true,
						Message = "sent"
					});
					SteamTransport.SendReliable(Protocol.Finish(ms));
				}
				catch { }
				SmpLog.Print("save transfer: all " + sendCount + " chunk(s) sent.");
				Completed++;
				AbortSend("tamamlandı");
			}
		}

		// ------------------------------------------------------------------
		// ALICI TARAF (client)
		// ------------------------------------------------------------------

		/// <summary>Karşı taraftan kaydı ister. Sadece client anlamlıdır.</summary>
		public static void RequestFromHost() {
			if (Busy) {
				SmpLog.Print("save transfer: already in progress.");
				return;
			}
			if (!SteamTransport.HasRemote) {
				SmpLog.Warning("save transfer: no peer connected.");
				return;
			}
			if (!SteamTransport.IsClient) {
				SmpLog.Warning("save transfer: only the joining side can request a save.");
				return;
			}
			// İstek: boş gövde. Host SaveGameChunk akışını bununla başlatır.
			try {
				MemoryStream ms = Protocol.Begin(Protocol.Msg.SaveGameComplete);
				BinaryWriter w = new BinaryWriter(ms);
				Protocol.WriteSaveComplete(w, new Protocol.SaveCompleteData {
					FileSize = 0, Crc32 = 0, Ok = false, Message = "request"
				});
				if (SteamTransport.SendReliable(Protocol.Finish(ms)))
					SmpLog.Print("save transfer: asked the host for its save.");
			}
			catch (Exception e) {
				SmpLog.Warning("save transfer: request failed: " + e.Message);
			}
		}

		static void AbortRecv(string why) {
			if (incoming != null) {
				try { incoming.Close(); } catch { }
				try { File.Delete(TempPath(SavePath() ?? "")); } catch { }
				incoming = null;
			}
			recvGot = null;
			recvNext = 0;
			recvCount = 0;
			if (recvStartedAt > 0f) {
				SmpLog.Warning("save transfer: receive aborted (" + why + ").");
				Failed++;
			}
			recvStartedAt = -1f;
			LastResult = "aborted: " + why;
		}

		/// <summary>Bir parça geldi. Host gövdeden okur.</summary>
		public static void OnChunk(BinaryReader r) {
			Protocol.SaveChunkData d;
			Protocol.ReadSaveChunk(r, out d);

			string savePath = SavePath();
			if (savePath == null) { LastResult = "no save path"; Failed++; return; }

			// Zaman aşımı: yarım kalmış aktarımı bırak.
			if (recvStartedAt > 0f && Time.realtimeSinceStartup - recvStartedAt > TIMEOUT) {
				AbortRecv("zaman aşımı");
				return;
			}

			// İlk parça: geçici dosyayı aç ve bekleme dizilerini kur.
			if (incoming == null) {
				try {
					if (File.Exists(TempPath(savePath))) File.Delete(TempPath(savePath));
					incoming = new FileStream(TempPath(savePath), FileMode.Create,
						FileAccess.Write, FileShare.None);
				}
				catch (Exception e) {
					LastResult = "temp open failed: " + e.Message;
					Failed++;
					return;
				}
				recvCrc = d.Crc32;
				recvSize = d.FileSize;
				recvCount = d.Count;
				recvNext = 0;
				recvGot = new bool[recvCount];
				recvStartedAt = Time.realtimeSinceStartup;
				SmpLog.Print("save transfer: receiving " + recvSize + " byte in "
					+ recvCount + " chunk(s).");
			}

			// Başlık uyuşmazsa bu akış başka bir dosyaya ait: vazgeç.
			if (d.Crc32 != recvCrc || d.FileSize != recvSize || d.Count != recvCount) {
				AbortRecv("parça başlığı uyuşmadı");
				return;
			}
			if (d.Index >= recvCount) { AbortRecv("parça indeksi taştu"); return; }

			// Parçayı kendi yerine yaz (yerinden yazma: sıra bozulsa da doğru olur).
			try {
				if (d.Data == null || d.Offset + d.Length > d.Data.Length) {
					AbortRecv("parça verisi bozuk");
					return;
				}
				incoming.Seek(d.Offset, SeekOrigin.Begin);
				incoming.Write(d.Data, d.Offset, d.Length);
				incoming.Flush();
			}
			catch (Exception e) {
				AbortRecv("yazılamadı: " + e.Message);
				return;
			}
			if (!recvGot[d.Index]) {
				recvGot[d.Index] = true;
				ReceivedChunks++;
			}
		}

		/// <summary>
		/// Kapanış paketi. Tüm parçalar geldiyse doğrula ve yerine geç.
		/// `ok == false` ve mesaj "request" ise BU BİR İSTEKTİR: host taraf
		/// yönlendirmesi burada başlar (BeginSend).
		/// </summary>
		public static void OnComplete(BinaryReader r, byte id) {
			Protocol.SaveCompleteData d;
			Protocol.ReadSaveComplete(r, out d);

			// ------------------------------------------------------------------
			// YÖN AYIRIMI: aynı mesaj kimliği iki yönde de kullanılıyor.
			//   host'a gelen  -> ya "istek" (ok=false,"request") ya da client'ın
			//                   yazma sonucu (ack). Host hiçbir zaman parça
			//                   toplamaz, o yüzden host tarafında "kapanışı
			//                   doğrula ve yaz" dalı ÇALIŞMAZ.
			//   client'a giden -> host'un kapanışı (ok=true) ya da "gönderemem"
			//                   yanıtı (ok=false).
			// ------------------------------------------------------------------
			if (SteamTransport.IsHost) {
				if (!d.Ok && string.Equals(d.Message, "request", StringComparison.Ordinal)) {
					BeginSend();
					return;
				}
				// Client'ın yazma sonucu. Bu tur istatistik olarak kaydedilir;
				// host'un kendi dosyasına hiç dokunulmaz.
				if (d.Ok) {
					LastResult = "peer wrote the save (" + (d.Message ?? "ok") + ")";
					SmpLog.Print("save transfer: peer reports the save was written ("
						+ (d.Message ?? "ok") + ").");
				}
				else {
					LastResult = "peer failed: " + (d.Message ?? "?");
					SmpLog.Warning("save transfer: peer could not write the save ("
						+ (d.Message ?? "?") + "). Its own save is untouched.");
				}
				return;
			}

			// ---- CLIENT tarafı ----
			if (!d.Ok) {
				// Host "gönderemem" dedi. İstemci sessizce bekler durumda
				// kalmamalı; sebep açıkça yazılır.
				LastResult = "host could not send: " + (d.Message ?? "?");
				SmpLog.Warning("save transfer: host could not send the save ("
					+ (d.Message ?? "?") + ").");
				recvStartedAt = -1f;
				recvGot = null;
				return;
			}

			// Host'un kapanışı: doğrula ve yaz.
			string savePath = SavePath();
			if (savePath == null) { LastResult = "no save path"; Failed++; return; }
			if (incoming == null || recvGot == null) {
				// Hiç parça gelmedi: kapanış tek başına bir şey ifade etmez.
				LastResult = "complete without chunks";
				return;
			}

			bool haveAll = true;
			for (int i = 0; i < recvGot.Length; i++) if (!recvGot[i]) { haveAll = false; break; }
			long written = incoming.Length;
			try { incoming.Close(); } catch { }
			incoming = null;

			if (!haveAll || written != (long)recvSize) {
				try { File.Delete(TempPath(savePath)); } catch { }
				AbortRecv("eksik parça: " + written + "/" + recvSize + " bayt");
				// Host'a sebebi bildir.
				SendAck(false, "incomplete");
				return;
			}

			// CRC32 doğrula: geçici dosyayı geri oku.
			uint actual = 0;
			try {
				byte[] buf = File.ReadAllBytes(TempPath(savePath));
				actual = (buf.Length == (int)recvSize) ? Crc32(buf, 0, buf.Length) : 0;
			}
			catch (Exception e) {
				AbortRecv("geçici dosya okunamadı: " + e.Message);
				SendAck(false, "temp unreadable");
				return;
			}
			if (actual != recvCrc) {
				try { File.Delete(TempPath(savePath)); } catch { }
				AbortRecv("CRC32 uyuşmadı");
				SendAck(false, "crc mismatch");
				return;
			}

			// HER ŞEY UYUŞTU: yedeği al ve yerine geç.
			string backup = null;
			try {
				if (File.Exists(savePath)) {
					backup = BackupPath(savePath);
					File.Copy(savePath, backup, false);
				}
				File.Delete(savePath);
				File.Move(TempPath(savePath), savePath);
			}
			catch (Exception e) {
				// Yedek varsa geri koy: orijinal hiç kaybolmasın.
				try {
					if (backup != null && File.Exists(backup) && !File.Exists(savePath))
						File.Move(backup, savePath);
				}
				catch { }
				AbortRecv("yerine geçirilemedi: " + e.Message);
				SendAck(false, "replace failed");
				return;
			}

			Completed++;
			LastResult = "saved " + recvSize + " byte"
				+ (backup != null ? " (backup: " + Path.GetFileName(backup) + ")" : "");
			SmpLog.Print("save transfer: COMPLETE. " + recvSize + " byte written to "
				+ SAVE_NAME + (backup != null ? ", backup: " + backup : "") + ".");
			SmpLog.Print("Load this save with CONTINUE to play the host's world.");
			SendAck(true, "ok");
			recvStartedAt = -1f;
			recvGot = null;
			recvNext = 0;
			recvCount = 0;
		}

		/// <summary>Host'a sonucu bildir (SaveGameComplete geri yönünde).</summary>
		static void SendAck(bool ok, string message) {
			try {
				MemoryStream ms = Protocol.Begin(Protocol.Msg.SaveGameComplete);
				BinaryWriter w = new BinaryWriter(ms);
				Protocol.WriteSaveComplete(w, new Protocol.SaveCompleteData {
					FileSize = recvSize,
					Crc32 = recvCrc,
					Ok = ok,
					Message = message ?? ""
				});
				SteamTransport.SendReliable(Protocol.Finish(ms));
			}
			catch { }
		}

		// ------------------------------------------------------------------
		// KARE
		// ------------------------------------------------------------------

		/// <summary>SteamMPBehaviour.Update içinden çağrılır.</summary>
		public static void Tick() {
			try {
				if (sending) TickSend();
				// Bağlantı koptuysa yarım kalan hiçbir şey yazmasın.
				if (!SteamTransport.HasRemote && (sending || incoming != null))
					AbortRecv("peer left");
			}
			catch (Exception e) {
				SmpLog.Warning("save transfer tick failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>'smp save' için tek satır.</summary>
		public static string Describe() {
			if (sending) return "sending " + sendNext + "/" + sendCount + " chunk(s)";
			if (incoming != null) {
				int got = 0;
				for (int i = 0; i < recvGot.Length; i++) if (recvGot[i]) got++;
				return "receiving " + got + "/" + recvCount + " chunk(s)";
			}
			return "idle, sent=" + SentChunks + " recv=" + ReceivedChunks
				+ " ok=" + Completed + " failed=" + Failed + " | " + LastResult;
		}
	}
}
