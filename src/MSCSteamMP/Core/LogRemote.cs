using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;

namespace SteamMP {

	/// <summary>
	/// KARŞI TARAFIN LOGUNU AYNALAMA.
	///
	/// SORUN
	/// -----
	/// İki oyunculu oturumda teşhis tek taraflı olamaz. "Vida sıkmıyor" dediğinde
	/// soru şudur: ben gönderdim mi, o aldı mı, yoksa o aldı da benim
	/// taramam mı bulamadı? Her iki tarafın da kendi logu varsa bu üçü tek
	/// bakışta ayrılır. Ama iki oyuncu iki dosya paylaşıyor ve kullanıcı
	/// "hangi dosyaya bakayım?" diye soruyor.
	///
	/// ÇÖZÜM
	/// -----
	/// İsteğe bağlı aynalama: açık olan tarafın log satırları güvenilir
	/// kanaldan karşı tarafa gönderilir ve orada <c>peer</c> kanalıyla kendi
	/// dosyasına yazılır. İki tarafın logu tek bakışta birbirinin satırlarını
	/// içerir; satırın solunda kim gönderdi yazar.
	///
	/// 'send'/'recv' ÇİFTİ
	/// ------------------
	/// Asıl değer burada: paket gönderimi [ACT]-tarzı satırlarla değil,
	/// gönderen tarafta "send" etiketiyle, alan tarafta "recv" etiketiyle
	/// yazılır. Karşı tarafın logunda şu görünür:
	///
	///     12:00:04.118 [debug] peer   [MSCSteamMP] recv bolt batch  n=12 path=...
	///     12:00:04.118 [debug] net    [MSCSteamMP] send bolt batch  n=12 path=...
	///
	/// Yani "gönderdim ama ulaşmadı" ile "ulaştı ama işlemedim" birbirinden
	/// ayrılır. Bu, canlı logda en çok ihtiyaç duyulan ayrımdı.
	///
	/// SINIRLAR
	/// -------
	///   * Varsayılan KAPALI. Açıkken bile sadece Warn/Error/State ve isteğe
	///     bağlı Debug gider; Trace asla aynalanmaz.
	///   * Kuyruk sınırlıdır ve saniyede azami sayıda satır gönderilir; ayna
	///     modu, teşhis kanalını tıkamasın diye sınırlıdır.
	///   * Gönderim karşı tarafın P2P kuyruğunu doldurmamak için UNRELIABLE.
	///     Log satırı kaybolabilir; teşhis satırı kaybı kabul edilebilir,
	///     oyun durumunun kaybı kabul edilemez.
	///   * Yalnızca el sıkışma tamamlanmışsa gönderilir.
	/// </summary>
	public static class LogRemote {

		/// <summary>Kuyruk kapasitesi. Taşarsa en eski satır düşer.</summary>
		public const int QueueSize = 128;

		/// <summary>Saniyede azami gönderim.</summary>
		public const int MaxPerSecond = 20;

		/// <summary>Azami satır uzunluğu (bayt). Uzun yığın izleri kesilir.</summary>
		public const int MaxLineChars = 300;

		/// <summary>Aynalama açık mı?</summary>
		public static bool Enabled;

		/// <summary>Debug seviyesi de aynalansın mı? (Warn/Error her zaman gider.)</summary>
		public static bool MirrorDebug;

		/// <summary>Karşı taraftan gelen satır sayısı (bu oturum).</summary>
		public static int Received;

		/// <summary>Karşı tarafa gönderilen satır sayısı.</summary>
		public static int Sent;

		/// <summary>Kuyrukta taşıp düşen satır sayısı.</summary>
		public static int Dropped;

		sealed class Pending {
			public byte Level;
			public string Channel;
			public string Text;
		}

		static readonly Queue<Pending> queue = new Queue<Pending>(QueueSize);
		static readonly object gate = new object();
		static int thisSecond;
		static int thisSecondCount;
		static float nextSend;

		/// <summary>Kuyruğa satır ekler (gönderim sonraki Pump'ta yapılır).</summary>
		public static void Outbound(LogRecord rec) {
			if (!Enabled || rec.Level == LogSeverity.Trace) return;
			if (rec.Level < LogSeverity.Debug && !MirrorDebug) {
				// Info/Warn/Error/State her zaman aynalanır; burada sadece
				// "Info da aynalansın mı" sorusu kalıyor ki cevap hayır.
				if (rec.Level == LogSeverity.Info && rec.Kind == LogKind.Info) return;
			}
			if (!SteamTransport.HasRemote) return;

			string text = rec.Message;
			if (!string.IsNullOrEmpty(rec.Scope)) text = rec.Scope + "/ " + text;
			if (!string.IsNullOrEmpty(rec.Event)) text = rec.Event + " " + text;
			if (!string.IsNullOrEmpty(rec.Fields)) text = text + rec.Fields;
			if (text.Length > MaxLineChars) text = text.Substring(0, MaxLineChars) + "...";

			lock (gate) {
				while (queue.Count >= QueueSize) { queue.Dequeue(); Dropped++; }
				Pending p = new Pending();
				p.Level = (byte)rec.Level;
				p.Channel = rec.ChannelOrDefault();
				p.Text = text;
				queue.Enqueue(p);
			}
		}

		/// <summary>
		/// Kuyruğu boşaltır ve sınırlı sayıda satır gönderir. Kare başına bir
		/// kez <c>SteamMPBehaviour</c> tarafından çağrılır.
		/// </summary>
		public static void Pump() {
			if (!Enabled) return;
			if (!SteamTransport.HasRemote) { Clear(); return; }

			int budget = MaxPerSecond;
			int second = UnityEngine.Time.frameCount / 60;
			lock (gate) {
				if (second != thisSecond) { thisSecond = second; thisSecondCount = 0; }
				if (thisSecondCount >= budget) return;
			}

			float now = UnityEngine.Time.time;
			if (now < nextSend) return;
			nextSend = now + 0.05f;

			int sent = 0;
			while (sent < 4) {
				Pending p;
				lock (gate) {
					if (queue.Count == 0) break;
					p = queue.Dequeue();
					thisSecondCount++;
				}
				if (Send(p)) sent++;
				else break;      // bağlantı yok, geri kalanı sonraya
			}
		}

		static bool Send(Pending p) {
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.LogLine);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(p.Level);
				w.Write(p.Channel ?? "");
				w.Write(p.Text ?? "");
				if (!SteamTransport.SendUnreliable(Protocol.Finish(s))) return false;
				Sent++;
				return true;
			}
			catch { return false; }
		}

		/// <summary>Gelen satırı kendi loguna yazar (peer kanalı).</summary>
		public static void OnMessage(BinaryReader r) {
			try {
				byte level = r.ReadByte();
				string channel = r.ReadString();
				string text = r.ReadString();

				Received++;
				LogSeverity lv = LogSeverity.Info;
				if (level <= (byte)LogSeverity.Trace) lv = (LogSeverity)level;

				// Kanal adının başına "peer/" konur: hem dosyada aranabilir
				// hem de 'smp log level peer=off' ile tümünü susturulabilir.
				string chan = LogChannels.Peer + "/" + (string.IsNullOrEmpty(channel) ? "?" : channel);

				// Düz yazım: dedup, kapsam ve alan biçimlendirmesi UYGULANMAZ,
				// çünkü karşı tarafın biçimi bizimkine çevrilemez. Sadece
				// seviye eşiğine bakılır.
				if (!LogSeverities.AtMost(lv, LogChannels.LevelOf(LogChannels.Peer))) return;

				LogRecord rec = new LogRecord();
				rec.Since = LogEngine.Since();
				rec.Serial = 0;                 // PushPeer kendi numarasını verir
				rec.Level = lv;
				rec.Kind = LogSeverities.ToKind(lv);
				rec.Channel = chan;
				rec.Event = null;
				rec.Message = text;
				rec.Fields = null;
				rec.Scope = null;
				rec.Thread = 0;
				rec.Repeat = 0;

				LogEngine.PushPeer(rec);
			}
			catch (Exception e) {
				// Bozuk ayna paketi oyunu düşürmemeli.
				SmpLog.Warning("peer log line rejected: " + e.GetType().Name);
			}
		}

		/// <summary>Kuyruğu temizler (bağlantı kopunca).</summary>
		public static void Clear() {
			lock (gate) { queue.Clear(); }
		}

		/// <summary>Kuyrukta bekleyen satır sayısı.</summary>
		public static int Queued {
			get { lock (gate) { return queue.Count; } }
		}

		/// <summary>
		/// Gelen paket için "recv" kaydı. SteamTransport.RoutePacket bunu,
		/// mesaj gövdesi okunmadan hemen önce çağırır.
		///
		/// Neden burada ve neden bu biçimde: teşhisin en pahalı sorusu "gönderdim
		/// mi, karşı taraf aldı mı, aldı da işlemedi mi". Gönderen taraf bunu
		/// zaten "send" etiketiyle yazıyor; karşı taraf da aynı satırı "recv"
		/// olarak yazınca üçü tek dosyada ayrışıyor. Gövde okunmadan yazılır ki
		/// bozuk paket de kayda geçsin (okuma başarısız olsa bile gittiğini
		/// bilmek teşhise yeter).
		///
		/// Yalnızca aynalama açıkken yazar: kapalıyken ek yük, karşılığında hiç
		/// bilgi yok.
		/// </summary>
		internal static void NoteReceived(byte msgId, int bytes) {
			if (!Enabled) return;
			LogFields f = new LogFields().Add("id", (int)msgId).Add("bytes", bytes);
			LogEngine.Debug(LogChannels.Peer, "recv", "packet " + MsgName(msgId), f);
		}

		/// <summary>
		/// Gönderim için "send" kaydı. SendRaw, paket gerçekten gönderilmeden
		/// ÖNCE yazar; gönderilemeyen paketler "drop" olarak ayrı satır olur, yani
		/// "gönderdim" ile "gönderdim sanıyorum" birbirinden ayrılır.
		/// </summary>
		internal static void NoteSent(byte msgId, int bytes, bool sent) {
			if (!Enabled) return;
			LogFields f = new LogFields().Add("id", (int)msgId).Add("bytes", bytes)
				.Add("ok", sent);
			string name = MsgName(msgId);
			if (sent) LogEngine.Debug(LogChannels.Net, "send", "packet " + name, f);
			else LogEngine.Debug(LogChannels.Net, "drop", "packet " + name + " was not sent", f);
		}

		/// <summary>
		/// Mesaj kimliğini AD olarak verir (BoltBatch değil 1).
		///
		/// İki tarafın logu satır satır karşılaştırılacağı için sayı değil ad
		/// yazılır: "packet BoltBatch" okunur, "packet 1" okunmaz. Enum
		/// dönüşümü iki tarafta derleyiciye göre farklı olabilir (bilinmeyen
		/// değerde ToString bile değişebilir), bu yüzden bilinmeyen kimlik
		/// açıkça "?" olarak işaretlenir.
		/// </summary>
		static string MsgName(byte id) {
			switch ((Protocol.Msg)id) {
				case Protocol.Msg.BoltBatch: return "BoltBatch";
				case Protocol.Msg.PartBatch: return "PartBatch";
				// v20: `Database/*` part-flag kanali. `Database` satirlari
				// `Installed, Bolted, Damaged` ucunu bildiriyor ama hicbir
				// montaj FSM'i yok, bu yuzden PartBatch'in `Installable`
				// filtresine takiliyorlardi.
				case Protocol.Msg.DatabaseBatch: return "DatabaseBatch";
				// TA-01: kanca / kemer kilidi / sinyal bayraklari.
				case Protocol.Msg.InteriorState: return "InteriorState";
				case Protocol.Msg.PartEvent: return "PartEvent";
				case Protocol.Msg.SyncRequest: return "SyncRequest";
				case Protocol.Msg.SyncDone: return "SyncDone";
				case Protocol.Msg.PartState: return "PartState";
				case Protocol.Msg.PartTransform: return "PartTransform";
				case Protocol.Msg.EntityState: return "EntityState";
				case Protocol.Msg.Handshake: return "Handshake";
				case Protocol.Msg.Heartbeat: return "Heartbeat";
				case Protocol.Msg.HeartbeatResp: return "HeartbeatResp";
				case Protocol.Msg.Disconnect: return "Disconnect";
				case Protocol.Msg.Transform: return "Transform";
				case Protocol.Msg.Switch: return "Switch";
				case Protocol.Msg.State: return "State";
				case Protocol.Msg.Engine: return "Engine";
				case Protocol.Msg.EnterVehicle: return "EnterVehicle";
				case Protocol.Msg.LeaveVehicle: return "LeaveVehicle";
				case Protocol.Msg.FullState: return "FullState";
				case Protocol.Msg.WheelState: return "WheelState";
				case Protocol.Msg.PlayerPos: return "PlayerPos";
				case Protocol.Msg.PlayerInfo: return "PlayerInfo";
				case Protocol.Msg.ItemEvent: return "ItemEvent";
				case Protocol.Msg.WorldState: return "WorldState";
				case Protocol.Msg.ObjectState: return "ObjectState";
				case Protocol.Msg.RadioState: return "RadioState";
				case Protocol.Msg.PlayerState: return "PlayerState";
				case Protocol.Msg.FluidState: return "FluidState";
				case Protocol.Msg.EconomyState: return "EconomyState";
				case Protocol.Msg.JobState: return "JobState";
				case Protocol.Msg.HeldItem: return "HeldItem";
				case Protocol.Msg.DoorEvent: return "DoorEvent";
				case Protocol.Msg.CarryState: return "CarryState";
				case Protocol.Msg.PartMountState: return "PartMountState";
				case Protocol.Msg.BoltBatchV2: return "BoltBatchV2";
				case Protocol.Msg.AuthorityClaim: return "AuthorityClaim";
				case Protocol.Msg.FullStateSnapshot: return "FullStateSnapshot";
				case Protocol.Msg.PartRecovery: return "PartRecovery";
				case Protocol.Msg.AuthoritySnapshot: return "AuthoritySnapshot";
				case Protocol.Msg.LogLine: return "LogLine";
				case Protocol.Msg.CdDisc: return "CdDisc";
				case Protocol.Msg.CdCase: return "CdCase";
				default: return "?" + id;
			}
		}

		/// <summary>'smp log remote' ve sağlık çıktısı.</summary>
		public static string Describe() {
			return (Enabled ? "ON" : "off")
				+ (MirrorDebug ? " (+debug)" : "")
				+ " sent=" + Sent + " recv=" + Received + " queued=" + Queued
				+ (Dropped > 0 ? " dropped=" + Dropped : "")
				+ " | 'smp log remote on|off', 'smp log remote debug'";
		}

		/// <summary>Aç/kapat ve isteğe bağlı olarak sıfırla.</summary>
		public static void Set(bool enabled, bool debugToo) {
			Enabled = enabled;
			MirrorDebug = debugToo;
			if (!enabled) Clear();
			LogEngine.Info(LogChannels.Net, "log mirror "
				+ (enabled ? ("ON" + (debugToo ? " (debug included)" : "")) : "off"));
		}
	}
}
