using System.Collections.Generic;
using System.Linq;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// '/npc' — bundle'daki oyuncu modeliyle test botu üretir.
	///
	///   /npc         oyuncunun önünde bot doğar, etrafında dönmeye başlar
	///   /npc here    ilk botu oyuncunun 2 m önüne, ayak seviyesine ışınlar
	///   /npc list    botları listele (konum + animasyon durumu)
	///   /npc kill    hepsini kaldır
	///
	/// Bot, P2P'deki uzak oyuncu gövdesinin BİREBİR aynısıdır
	/// (RemotePlayerFactory.Create + RemotePlayerBehaviour); tek farkı
	/// konum paketleri yerine PlayerBotWander'in daire çizmesidir. Böylece
	/// model, klip, interpolasyon ve animasyon seçimi ağ olmadan test edilir.
	/// </summary>
	public class NpcCommand : ConsoleCommand {

		public override string Name => "npc";

		public override string Help =>
			"Test: '/npc' spawns a bot with your player model circling you. "
			+ "'/npc here' teleports it to your feet, '/npc seat' puts it in Satsuma passenger seat, "
			+ "'/npc list' lists bots, '/npc kill' removes them.";

		/// <summary>Üretilen bot gövdeleri. Sahne değişiminde Unity siler.</summary>
		static readonly List<GameObject> spawned = new List<GameObject>();

		public override void Run(string[] args) {
			string what = args != null && args.Length > 0 && args[0] != null
				? args[0].ToLowerInvariant() : "";
			if (what == "list") { List(); return; }
			if (what == "kill" || what == "remove" || what == "clear") { Kill(); return; }
			if (what == "here" || what == "tp" || what == "yanima") { TeleportHere(); return; }
			if (what == "seat" || what == "passenger" || what == "satsuma") { SeatInSatsuma(); return; }
			if (what == "wander" || what == "circle" || what == "walk") { ToggleWander(); return; }
		// /npc stop -> YÜRÜMEYİ DURDUR. ÖNEMLİ: bu komut YOKTU ve
		// bilinmeyen bir alt komut Run()'un sonuna düşüp Spawn()'a
		// gidiyordu, yani "/npc stop" YENİ BİR BOT DOĞURUYORDU.
		// Kullanıcı "stop yazdım, yürümeye devam ediyor" dedi; doğru
		// davranış buydu çünkü eski bot hâlâ yürüyordu.
		if (what == "stop" || what == "dur" || what == "halt" || what == "bekle") { StopWander(); return; }
		// /npc head  -> botun sana bakmasini ac/kapat (varsayilan ACIK)
		if (what == "head" || what == "look" || what == "bak") { ToggleHeadTracking(); return; }
		// /smp headinfo -> kafa bakis zincirini OLCER (tanimaz, sadece yazar)
		if (what == "headinfo" || what == "olc" || what == "hesap") {
			PlayerBotWander w = FirstBot();
			SmpLog.Print(w == null ? "bot yok. /npc ile bir tane dogur." : w.DescribeHead());
			return;
		}
			Spawn();
		}

		/// <summary>
		/// Botun etrafında daire çizme (wander) davranışını açar/kapatır.
		/// Varsayılan: kapalı, bot yerinde durur.
		/// </summary>
		static void ToggleWander() {
			if (spawned.Count == 0) {
				SmpLog.Error("no bot. Spawn one first: /npc");
				return;
			}
			int toggled = 0;
			for (int i = 0; i < spawned.Count; i++) {
				if (spawned[i] == null) continue;
				var w = spawned[i].GetComponent<PlayerBotWander>();
				if (w != null) {
					// DIARE cizmeyi ac/kapa. Component KAPATILMAZ: kapansaydi
					// Update() durur ve kafa takibi de kesilirdi.
					w.WanderEnabled = !w.WanderEnabled;
					toggled++;
				}
			}
			if (toggled > 0) {
				var first = spawned[0].GetComponent<PlayerBotWander>();
				SmpLog.Print("Wander " + (first.WanderEnabled ? "ENABLED - bot will circle" : "DISABLED - bot stands still"));
			}
		}

		/// <summary>
		/// TÜM botların yürümesini durdurur ('/npc stop').
		///
		/// '/npc wander' ile AYNI şeyi yapmaz: o bir TOGGLE'dır, yani
		/// zaten kapalıysa AÇAR. Kullanıcı "stop" yazınca "durdur"
		/// demektir; koşul soran komut yanıltır.
		///
		/// Component KAPATILMAZ: kapsama girince Update() durur ve
		/// kafa takibi de kesilirdi (bkz. ToggleWander).
		/// </summary>
		static void StopWander() {
			if (spawned.Count == 0) {
				SmpLog.Error("no bot. Spawn one first: /npc");
				return;
			}
			int stopped = 0;
			int already = 0;
			for (int i = 0; i < spawned.Count; i++) {
				if (spawned[i] == null) continue;
				var w = spawned[i].GetComponent<PlayerBotWander>();
				if (w == null) continue;
				if (!w.WanderEnabled) { already++; continue; }
				w.WanderEnabled = false;
				stopped++;
			}
			if (stopped > 0) {
				SmpLog.Print("Wander DISABLED - " + stopped + " bot stopped walking.");
			}
			else if (already > 0) {
				SmpLog.Print("Wander already DISABLED - " + already + " bot standing still.");
			}
			else {
				SmpLog.Print("no bot had PlayerBotWander; nothing to stop.");
			}
		}

		/// <summary>
		/// Botun kafa takibini ac/kapat.
		///
		/// Varsayilan ACIK: /npc ile doguran bot, nerede durursa dursun
		/// oyuncunun kafasina bakar. Kafa takibi icin botun etrafinda
		/// dolanmasi GEREKMEZ; bu yuzden wander bayragindan bagimsiz.
		///</summary>
		static void ToggleHeadTracking() {
				if (spawned.Count == 0) {
						SmpLog.Error("no bot. Spawn one first: /npc");
				}
				bool anyOn = false;
				for (int i = 0; i < spawned.Count; i++) {
						if (spawned[i] == null) continue;
						var w = spawned[i].GetComponent<PlayerBotWander>();
						if (w == null) continue;
						w.TrackPlayerHead = !w.TrackPlayerHead;
						if (w.TrackPlayerHead) anyOn = true;
				}
				SmpLog.Print("Head tracking " + (anyOn
						? "ON - bot looks at your head"
						: "OFF - head follows camera only"));
				SmpLog.Print("  limits: pitch "
						+ RemotePlayerBehaviour.HEAD_PITCH_MIN.ToString("F0") + ".."
						+ RemotePlayerBehaviour.HEAD_PITCH_MAX.ToString("F0")
						+ " deg, yaw +-"
						+ RemotePlayerBehaviour.HEAD_YAW_LIMIT.ToString("F0") + " deg");
		}

		/// <summary>Ilk botun gezgini (temis komutlari icin), ya da null.</summary>
		static PlayerBotWander FirstBot() {
			for (int i = 0; i < spawned.Count; i++) {
				if (spawned[i] == null) continue;
				PlayerBotWander w = spawned[i].GetComponent<PlayerBotWander>();
				if (w != null) return w;
			}
			return null;
		}

		static void Spawn() {
			if (!PlayerAsset.Ready) {
				SmpLog.Error("player asset NOT loaded: " + PlayerAsset.Problem);
				return;
			}

			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) {
				SmpLog.Error("no camera; cannot work out where to put the bot.");
				return;
			}
			// YANINDA DOĞAR: oyuncunun 2 m sağı, AYAK SEVİYESİNDE.
			// Kamera pozisyonu kafada (~1.7m), ayak seviyesini hesapla.
			Vector3 right = cam.transform.right;
			right.y = 0f;
			if (right.sqrMagnitude < 0.01f) right = Vector3.right;
			right.Normalize();

			// Oyuncu ayak pozisyonu: kamera pozisyonundan ~1.6m aşağı
			Vector3 playerFeet = cam.transform.position - Vector3.up * 1.6f;
			Vector3 where = playerFeet + right * 2f;

			// Zemin yüksekliğini bul (where noktasının üzerinden raycast)
			where.y = GroundY(where);

			// Bot oyuncunun YÖNÜNE bakmalı: kameranın yaw'ı + 180
			float yaw = cam.transform.eulerAngles.y + 180f;
			RemotePlayerBehaviour body = RemotePlayerFactory.Create(
				where, Quaternion.Euler(0f, yaw, 0f));
			if (body == null) {
				SmpLog.Error("could not spawn the test bot.");
				return;
			}

			body.gameObject.name = "TestBot";
			NameTag.Attach(body.gameObject, "test_player");

			// PlayerBotWander ekle ama BAŞLANG��TAKAPAT: bot oyuncunun yanında
			// yerinde dursun, etrafında dönmesin. Dönmesini istiyorsan
			// '/npc wander' yaz.
			PlayerBotWander wander = PlayerBotWander.Attach(body);
			if (wander != null) wander.WanderEnabled = false;

			spawned.Add(body.gameObject);
			SmpLog.Print("'TestBot' spawned at " + where.ToString("F1")
				+ " (" + spawned.Count + " bot(s)). '/npc kill' removes them."
				+ (wander != null ? " (wander disabled - use '/npc wander' to enable)" : ""));
		}

		/// <summary>
		/// Verilen X,Z pozisyonundaki zemin yüksekliğini bulur.
		/// Raycast yukarıdan aşağıya atılır; isabet etmezse oyuncu ayak seviyesi kullanılır.
		/// </summary>
		static float GroundY(Vector3 where) {
			try {
				RaycastHit hit;
				// Raycast başlangıcı: where noktasının 10m üzeri
				Vector3 origin = where + Vector3.up * 10f;
				if (Physics.Raycast(origin, Vector3.down, out hit, 20f)) {
					return hit.point.y + 0.02f; // Zeminin biraz üstünde
				}
			}
			catch { }
			// Fallback: oyuncu seviyesi (kamera - 1.6m)
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			return cam != null ? cam.transform.position.y - 1.6f : where.y;
		}

		/// <summary>
		/// İlk botu oyuncunun 2 m önüne, ayak seviyesine ışınlar.
		/// Bot havada kaldıysa anında yanına iner.
		/// </summary>
		static void TeleportHere() {
			GameObject bot = null;
			for (int i = spawned.Count - 1; i >= 0; i--) {
				if (spawned[i] == null) { spawned.RemoveAt(i); continue; }
				bot = spawned[i];
				break;
			}
			if (bot == null) {
				SmpLog.Error("no bot. Spawn one first: /npc");
				return;
			}
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) {
				SmpLog.Error("no camera.");
				return;
			}
			Vector3 flat = cam.transform.forward;
			flat.y = 0f;
			if (flat.sqrMagnitude < 0.01f) flat = cam.transform.right;
			flat.Normalize();
			Vector3 feet = cam.transform.position - Vector3.up * 1.6f;
			Vector3 dest = feet + flat * 2f;
			dest.y = GroundY(dest);

			RemotePlayerBehaviour body = bot.GetComponent<RemotePlayerBehaviour>();
			if (body != null) {
				// Dikey hız izleme sıfırlansın, yoksa ışınlanma "düşüyor"
				// gibi okunur ve bot bir anlığına Jump oynatır.
				body.ApplyPose(dest, Quaternion.Euler(0f,
					cam.transform.eulerAngles.y + 180f, 0f), 0f);
			}
			else {
				bot.transform.position = dest;
			}

			// Koltuktaysa ayır ve wander'i KAPAT (yerinde dursun)
			if (body != null && body.Seated) body.DetachFromSeat();
			var wander = bot.GetComponent<PlayerBotWander>();
			if (wander != null) wander.WanderEnabled = false;

			SmpLog.Print("'TestBot' moved to your feet at " + dest.ToString("F1") + ".");
		}

		static void List() {
			int alive = 0;
			for (int i = spawned.Count - 1; i >= 0; i--) {
				if (spawned[i] == null) { spawned.RemoveAt(i); continue; }
				alive++;
				PlayerBotWander w = spawned[i].GetComponent<PlayerBotWander>();
				SmpLog.Print("  " + spawned[i].name
					+ " at " + spawned[i].transform.position.ToString("F1")
					+ (w != null ? " " + w.Describe() : ""));
			}
			if (alive == 0) SmpLog.Print("no bots. Spawn one with: /npc");
		}

		static void Kill() {
			int n = 0;
			for (int i = spawned.Count - 1; i >= 0; i--) {
				if (spawned[i] == null) { spawned.RemoveAt(i); continue; }
				Object.Destroy(spawned[i]);
				n++;
			}
			spawned.Clear();
			SmpLog.Print("removed " + n + " bot(s).");
		}

		/// <summary>
		/// Botu Satsuma yolcu koltuğuna oturtur.
		/// Bot yoksa önce spawn eder, sonra VehicleRegistry'den Satsuma'yi bulup
		/// SyncedVehicle.AttachBotToPassenger ile koltuğa bağlar.
		/// </summary>
		static void SeatInSatsuma() {
			GameObject bot = null;
			for (int i = spawned.Count - 1; i >= 0; i--) {
				if (spawned[i] == null) { spawned.RemoveAt(i); continue; }
				bot = spawned[i];
				break;
			}
			if (bot == null) {
				SmpLog.Print("No bot found. Spawning one first...");
				Spawn();
				// Spawn sonrası listede son ekleneni al
				for (int i = spawned.Count - 1; i >= 0; i--) {
					if (spawned[i] != null) { bot = spawned[i]; break; }
				}
				if (bot == null) {
					SmpLog.Error("Failed to spawn bot.");
					return;
				}
			}

			// Satsuma'yi bul
			var satsuma = VehicleRegistry.Vehicles.Values
				.FirstOrDefault(v => v != null && v.VehicleName != null && v.VehicleName.StartsWith("SATSUMA"));
			if (satsuma == null) {
				SmpLog.Error("Satsuma not found in VehicleRegistry. Is the car spawned?");
				return;
			}

			var body = bot.GetComponent<RemotePlayerBehaviour>();
			if (body == null) {
				SmpLog.Error("Bot has no RemotePlayerBehaviour component.");
				return;
			}

			// Bot zaten bir yere bağlıysa ayır
			if (body.Seated) body.DetachFromSeat();

			// PlayerBotWander'i devre dışı bırak: yoksa her framede botu
			// yerinden oynatmaya devam eder, koltuk bağını bozar.
			var wander = bot.GetComponent<PlayerBotWander>();
			if (wander != null) wander.WanderEnabled = false;

						// Yolcu koltuğuna bağla
			if (satsuma.AttachBotToPassenger(body)) {
				SmpLog.Print("Bot seated in Satsuma passenger seat. (Wander disabled.)");
				// Animasyon durumu güncellensin
				body.ApplyFlags(true, 0, false); // seated=true
			} else {
				SmpLog.Error("Failed to attach bot to Satsuma passenger seat. Seat not found?");
			}
		}
	}
}