using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// ESC menüsünde davet paneli: host, dünyada, ESC açık ve peer yokken
	/// ekranın sağında küçük bir liste çıkar. listedeki arkadaşa basılınca
	/// Steam üzerinden lobi daveti gider (Shift+Tab'a gerek kalmaz).
	///
	/// Koşul sürümlü (durum tutulmaz): ESC kapanınca panel kendiliğinden
	/// gider. Arkadaş listesi 5 sn'de bir tazelenir (her kare Steam sorgusu
	/// yok). Çizim ham GUI'dir; MenuUi stillerine dokunmaz, hata dışarı
	/// sızmaz (crash-free).
	/// </summary>
	public static class InvitePanel {

		/// <summary>Arkadaş listesi tazeleme aralığı (sn).</summary>
		const float REFRESH_INTERVAL = 5f;

		static List<SteamTransport.FriendEntry> friends = new List<SteamTransport.FriendEntry>(16);
		static float nextRefresh;
		static string note = string.Empty;
		static float noteUntil;

		// ------------------------------------------------------------------
		// Oturum kartı: host'un paylaşacak şeyi DÜNYADA da görünmeli
		// ------------------------------------------------------------------

		static string sessionId = string.Empty;
		static string sessionCode = string.Empty;

		/// <summary>
		/// Lobi kimliğini ve kodu kaydeder. Dünyada gösterilecek.
		///
		/// Neden gerekli: OPEN LOBBY artık dünyayı HEMEN açıyor (kullanıcı
		/// beklediği buydu ve eski sürümde de böyleydi). Menüdeki online
		/// paneli saniyeden kısa göründüğü için host'un arkadaşına söyleyeceği
		/// tek gerçek adres (lobi kimliği) ekranda kalmıyordu. Davet panosu da
		/// yalnızca ESC menüsü açıkken çizildiği için yine görünmüyordu.
		/// </summary>
		public static void SetSession(string code, ulong lobbyId) {
			sessionId = lobbyId.ToString();
			sessionCode = code ?? string.Empty;
		}

		public static void ClearSession() {
			sessionId = string.Empty;
			sessionCode = string.Empty;
		}

		/// <summary>
		/// Kart çizilmeli mi?
		///
		/// Koşul bilerek dar: yalnızca DÜNYADA, host olarak, karşı taraf
		/// bağlanmamışken. Bağlantı kurulunca kart kaybolur; ekranı
		/// kalabalıklaştırmaya gerek yok.
		/// </summary>
		static bool SessionVisible() {
			try {
				if (MenuController.InMenu) return false;
				if (!SteamTransport.IsHost) return false;
				if (SteamTransport.HasRemote) return false;
				return !string.IsNullOrEmpty(sessionId);
			}
			catch { return false; }
		}

		/// <summary>
		/// Sol üstte küçük oturum kartı.
		///
		/// Tıklanınca LOBBY ID panoya kopyalanır (6 haneli kod ekranda zaten
		/// duruyor; arkadaşın gerçekten ihtiyacı olan şey 17 haneli kimliktir).
		/// </summary>
		static void DrawSessionCard() {
			float w = 268f;
			float h = 74f;
			float x = 16f;
			float y = 16f;

			GUI.Box(new Rect(x, y, w, h), string.Empty);
			GUI.Label(new Rect(x + 8f, y + 4f, w - 16f, 16f),
				"MULTIPLAYER - WAITING FOR FRIEND");
			GUI.Label(new Rect(x + 8f, y + 22f, w - 16f, 16f), "LOBBY ID  " + sessionId);

			Rect idRow = new Rect(x + 8f, y + 38f, w - 16f, 18f);
			string idLabel = sessionId + (sessionCopyNote ? "   (copied)" : "   (click to copy)");
			// Z-SIRASI: ustte modal varsa dugme kutu olur, olay ustte kalir.
			bool idBlocked = MenuMouse.GuiBlockedAtCurrentEvent();
			bool idHit = false;
			if (idBlocked) { GUI.Box(idRow, idLabel); }
			else { idHit = GUI.Button(idRow, idLabel); }
			if (idHit) {
				// GUIUtility.systemCopyBuffer internal (dnSpy kaniti), tek
				// erisilebilir yol MenuUi.CopyToClipboard (TextEditor.Copy).
				MenuUi.CopyToClipboard(sessionId);
				sessionCopyNote = true;
				noteUntil = Time.time + 3f;
				SmpLog.Print("lobby id copied to the clipboard: " + sessionId);
			}

			if (!string.IsNullOrEmpty(sessionCode)) {
				GUI.Label(new Rect(x + 8f, y + 56f, w - 16f, 16f), "CODE  " + sessionCode);
			}
		}

		static bool sessionCopyNote;

		/// <summary>Panel şu an çizilmeli mi?</summary>
		static bool Visible() {
			try {
				if (MenuController.InMenu) return false;
				if (!GameMenuGuard.GameMenuOpen) return false;
				if (!SteamTransport.IsHost || SteamTransport.HasRemote) return false;
				return true;
			}
			catch { return false; }
		}

		public static void Draw() {
			try {
				if (Time.time >= noteUntil) sessionCopyNote = false;

				// Kart, ESC menüsünden BAGIMSIZ: her zaman, host olarak ve
				// arkadas baglanmadikca gorunur.
				if (SessionVisible()) DrawSessionCard();

				if (!Visible()) return;
				float now = Time.time;
				if (now >= nextRefresh) {
					nextRefresh = now + REFRESH_INTERVAL;
					Refresh();
				}
				DrawBox();
			}
			catch (Exception e) {
				SmpLog.Warning("invite panel failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		static void Refresh() {
			friends.Clear();
			try {
				List<SteamTransport.FriendEntry> fresh = SteamTransport.GetOnlineFriends();
				if (fresh != null) {
					for (int i = 0; i < fresh.Count && friends.Count < 12; i++) {
						friends.Add(fresh[i]);
					}
				}
			}
			catch { }
		}

		static void DrawBox() {
			float w = 300f;
			float rowH = 26f;
			float pad = 10f;
			float headH = 30f;
			float footH = 22f;
			int n = friends.Count;
			float h = pad * 2f + headH + footH + Math.Max(n, 1) * (rowH + 4f);
			float x = Screen.width - w - 16f;
			float y = (Screen.height - h) * 0.5f;

			GUI.Box(new Rect(x, y, w, h), "MSCSteamMP - INVITE FRIEND");
			float cy = y + pad + headH;

			if (n == 0) {
				GUI.Label(new Rect(x + pad, cy, w - pad * 2f, rowH),
					"no online friends. Shift+Tab works too.");
			}
			for (int i = 0; i < n; i++) {
				SteamTransport.FriendEntry f = friends[i];
				string label = null;
				try { label = f.name; } catch { label = null; }
				if (string.IsNullOrEmpty(label)) label = "(unknown)";
				if (label.Length > 22) label = label.Substring(0, 22);
				GUI.Label(new Rect(x + pad, cy, w - pad * 2f - 80f, rowH), label);
				Rect inviteBox = new Rect(x + w - pad - 72f, cy, 72f, rowH - 4f);
				bool inviteBlocked = MenuMouse.GuiBlockedAtCurrentEvent();
				bool inviteHit = false;
				if (inviteBlocked) { GUI.Box(inviteBox, "Invite"); }
				else { inviteHit = GUI.Button(inviteBox, "Invite"); }
				if (inviteHit) {
					SendInvite(f);
				}
				cy += rowH + 4f;
			}

			string foot = Time.time < noteUntil && !string.IsNullOrEmpty(note)
				? note
				: "lobby " + sessionId
					+ "   code " + (LobbyCode.Current.Length > 0 ? LobbyCode.Current : "-");
			GUI.Label(new Rect(x + pad, cy, w - pad * 2f, footH), foot);
		}

		static void SendInvite(SteamTransport.FriendEntry f) {
			bool ok = false;
			string who = null;
			try { who = f.name; } catch { who = null; }
			try { ok = SteamTransport.Invite(f.id); } catch { ok = false; }
			note = ok ? ("invited " + (who ?? "friend") + ".")
				: "invite failed. Shift+Tab -> invite manually.";
			try { noteUntil = Time.time + 4f; } catch { }
			SmpLog.Print(note + " (lobby " + SteamTransport.LobbyId.m_SteamID + ")");
		}
	}
}
