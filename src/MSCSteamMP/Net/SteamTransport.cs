using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Steam lobby + P2P transport. 2 players max, FriendsOnly lobby, channel 7.
	/// Ported from MSCMP-OLD NetManager, stripped to lobby/handshake/heartbeat/P2P only.
	/// All Steam calls are guarded: if Steam is unavailable the mod stays dormant.
	/// </summary>
	public static class SteamTransport {
		public static readonly AppId_t GAME_APP_ID = new AppId_t(516750);

		/// <summary>
		/// Lobby metadata keys. The version is published so a client can skip a lobby that
		/// speaks a different protocol revision instead of connecting and failing the
		/// handshake later.
		/// </summary>
		public const string LOBBY_KEY = "mscsmp";
		public const string LOBBY_KEY_VERSION = "mscsmpver";

		public enum Mode { None, Host, Client }

		public static Mode CurrentMode = Mode.None;
		public static bool SteamReady;
		public static CSteamID LocalId;
		public static CSteamID LobbyId;
		public static CSteamID RemoteId;
		public static bool RemoteHandshake;
		public static ulong RemoteClock;
		public static uint PingMs;

		public static bool IsOnline { get { return SteamReady && CurrentMode != Mode.None; } }
		public static bool IsHost { get { return CurrentMode == Mode.Host; } }
		public static bool IsClient { get { return CurrentMode == Mode.Client; } }
		public static bool HasRemote { get { return IsOnline && RemoteId.IsValid() && RemoteHandshake; } }

		/// <summary>Yerel oyuncunun SteamID'si (ulong).</summary>
		public static ulong LocalSteamId { get { return LocalId.m_SteamID; } }

		/// <summary>Belirtilen SteamID'nin bağlı olup olmadığını kontrol eder.</summary>
		public static bool IsPeerConnected(ulong steamId)
		{
			if (!HasRemote) return false;
			return RemoteId.m_SteamID == steamId;
		}

		public static Action<Protocol.Msg, CSteamID, BinaryReader> OnMessage;

		/// <summary>
		/// Ilk kez el sikisma tamamlandiginda bir kez atilir: JIP snapshot'i ve
		/// sahiplik snapshot'i yalnizca buradan cikar, cunku oturumun basinda
		/// bir kez gonderilmeleri gerekir.
		///
		/// Ayrica oturum bitince (Leave) OnPeerDisconnected atilir; sahiplik
		/// kayitlari temizlenmezse yeni oturumda eski sahip kaliyordu.
		/// </summary>
		public static event Action OnPeerConnected;
		public static event Action OnPeerDisconnected;

		static Callback<P2PSessionRequest_t> cbSessionRequest;
		static Callback<P2PSessionConnectFail_t> cbConnectFail;
		static Callback<GameLobbyJoinRequested_t> cbJoinRequested;
		static CallResult<LobbyCreated_t> crLobbyCreated;
		static CallResult<LobbyEnter_t> crLobbyEnter;
		static CallResult<LobbyMatchList_t> crLobbyList;
		static bool lobbyListBusy;
		static bool autoJoinPending;
		static float joinAttemptAt;
		static float lastDiscoverAt;

		/// <summary>Otomatik katilma suruyor mu (menu bekleme ekrani buna bakar)?</summary>
		public static bool AutoJoinPending { get { return autoJoinPending; } }

		/// <summary>Katılma zaman aşımı (sn). Bu sürede lobi yoksa vazgeçilir.</summary>
		public const float JOIN_TIMEOUT = 60f;

		/// <summary>Katılma aramaları arası bekleme (sn). Tek seferlik arama,
		/// host aramadan sonra kurarsa lobiyi hiç görmüyordu.</summary>
		public const float JOIN_RETRY_INTERVAL = 5f;

		/// <summary>Menu bekleme ekraninda gosterilecek durum metni.</summary>
		public static string JoinStatus { get { return joinStatus; } }
		static string joinStatus = string.Empty;

		/// <summary>Katılma başarısız bitti (menu beklemeden ciksin diye).</summary>
		public static bool JoinFailed { get { return joinFailed; } }
		static bool joinFailed;

		/// <summary>Menu bekleme sonucunu tuketti (bir kez gosterilir).</summary>
		public static void ClearJoinResult() {
			joinFailed = false;
			joinStatus = string.Empty;
		}

		/// <summary>
		/// Bekleyen katılma girişimini iptal eder. <c>true</c> dönerse gerçekten
		/// beklenen bir girişim vardı ve iptal edildi.
		///
		/// KURULMUŞ OTURUMA DOKUNMAZ. Sadece "henüz bir lobye girilmedi, keşif
		/// sürüyor" durumunu temizler. Gerekçe: LOADING ekranı 60 saniye boyunca
		/// hiçbir çıkış sunmuyordu ve hata ancak süre dolunca yazılıyordu; kullanıcı
		/// oyunu kapatmak zorunda kalıyordu. Oturum düşürmek kullanıcının
		/// isteği olmadığı için yalnızca bekleme iptal ediliyor; bağlantı kurulmuşsa
		/// oyun normal şekilde devam ediyor.
		/// </summary>
		public static bool CancelPendingJoin() {
			if (!autoJoinPending) return false;
			autoJoinPending = false;
			// joinFailed BILerek false birakiliyor: iptal bir hata degil, ekranda
			// "cancelled" yazacak. true birakilirsa menu hata olarak gosterirdi.
			joinStatus = "cancelled";
			SmpLog.Warning("join cancelled by the player.");
			return true;
		}

		const float HEARTBEAT_INTERVAL = 5f;
		const float TIMEOUT_TIME = 60f;

		/// <summary>
		/// Packets handled per frame. The initial sync plus live traffic is a few dozen, so
		/// this is generous; it exists so a flood cannot stall the frame.
		/// </summary>
		const int MAX_PACKETS_PER_FRAME = 64;
		static float timeToHeartbeat;
		static float timeSinceHeartbeat;
		static DateTime bootTime = DateTime.UtcNow;

		public struct FriendEntry {
			public CSteamID id;
			public string name;
			public bool playingMSC;
		}

		public static void Init() {
			if (SteamReady) return;

			// OYUN KENDI STEAM'INI ZATEN KURMU OLABILIR (canlı log kanıtı)
			// ------------------------------------------------------------------
			// Oyunun kendi SteamManager.Awake()'i şunu çağırıyor:
			//
			//   if (SteamAPI.RestartAppIfNecessary(AppId_t.Invalid)) {
			//       Application.Quit();
			//   }
			//   m_bInitialized = SteamAPI.Init();
			//
			// Yani oyun kendi Steamworks oturumunu kendisi kurar. Steamworks
			// yalnızca BİR kez Init kabul eder: aynı süreçte ikinci bir
			// SteamAPI.Init() BAŞARISIZ döner. Oyun Steam istemcisi
			// üzerinden değil de exe'den başlatıldığında (logdaki
			// "Launch arguments" satırı) RestartAppIfNecessary "hayır"
			// dediği için oyun yine de kendi Init'ini yapıyor — ve bizim
			// çağrımız ikinci oluyor.
			//
			// Belirti tam olarak bu: Steam açık, oyun çalışıyor, lobi
			// kurulamıyor, log "Steam must be running" diyor.
			//
			// ÇÖZÜM: oyunun SteamManager'ı zaten kurduysa onu kullanıyoruz.
			if (GameSteamAlreadyInitialised()) {
				SteamReady = true;
				RegisterCallbacks();
				string who = "?";
				try { who = SteamFriends.GetPersonaName() + " (" + LocalId.m_SteamID + ")"; }
				catch { }
				SmpLog.Print("Steam ready (reusing the game's own Steamworks session) as " + who);
				return;
			}

			try {
				if (!SteamAPI.Init()) {
					SmpLog.Error("SteamAPI.Init failed. The game itself could not reach "
						+ "Steam: restart Steam, then start the game from the Steam library "
						+ "(not by running the exe).");
					return;
				}
			}
			catch (Exception e) {
				SmpLog.Error("Steam init exception: " + e.Message);
				return;
			}
			SteamReady = true;
			RegisterCallbacks();
			string who2 = "?";
			try { who2 = SteamFriends.GetPersonaName() + " (" + LocalId.m_SteamID + ")"; } catch { }
			SmpLog.Print("Steam ready (initialised by the mod) as " + who2);
		}

		/// <summary>
		/// Oyunun kendi SteamManager'ı Steamworks'i kurmuş mu?
		///
		/// Oyunun SteamManager'ı INTERNAL (Assembly-CSharp, namespace'siz), bu
		/// yüzden derleme zamanında referans verilemez. Bu mod oyunun
		/// tiplerine derleme zamanında bağlanmaz (build.cmd yalnızca MSCLoader,
		/// PlayMaker, 0Harmony referansı verir; Assembly-CSharp'i Assembly-CSharp
		/// dll'i kırılgan bir referans olarak görür). O yüzden çalışma
		/// zamanında, ad ve üye imzasıyla aranır.
		///
		/// BULMA YÖNTEMİ
		/// ------------
		/// Resources.FindObjectsOfTypeAll() ile tüm MonoBehaviour'ler gezilir.
		/// MonoBehaviour'ler birbirinden TÜREMEZ (hepsi doğrudan
		/// MonoBehaviour'ten), bu yüzden "SteamManager" aranırken
		/// GetType().Name == "SteamManager" demek YETERLİDİR; oyunun
		/// kendi SteamManager'ını bulur, MSCLoader'ınki değil.
		///
		/// Başarısız olursa false döner ve mod eski yola düşer (kendi
		/// SteamAPI.Init'ini dener). Yani bu bir optimizasyon değil,
		/// hata toleransı olan bir yol.
		/// </summary>
		static bool GameSteamAlreadyInitialised() {
			try {
				UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour));
				if (all == null) return false;
				for (int i = 0; i < all.Length; i++) {
					UnityEngine.Object o = all[i];
					if (o == null) continue;
					if (o.GetType().Name != "SteamManager") continue;
					PropertyInfo initialized = o.GetType().GetProperty("Initialized",
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					if (initialized == null) continue;
					object value = initialized.GetValue(null, null);
					if (value is bool && (bool)value) return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>
		/// Steam callback'lerini kurar.
		///
		/// Oyunun kendi SteamAPI.Init'ini kullandığımız durumda callback'ler
		/// AYRI kaydedilmelidir: Steam bir callback'i yalnızca bir kez
		/// tescil eder ve oyunun SteamManager'ı kendi ihtiyaçları olanları
		/// kurmuş olabilir. Bizimkiler farklı (P2P oturumu, lobi).
		/// </summary>
		static void RegisterCallbacks() {
			try { LocalId = SteamUser.GetSteamID(); } catch { LocalId = new CSteamID(); }

			cbSessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
			cbConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
			cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
			crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
			crLobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
			crLobbyList = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
		}

		public static ulong NetClockMs() {
			return (ulong)(DateTime.UtcNow - bootTime).TotalMilliseconds;
		}

		// ------------------------------------------------------------------
		// Lobby
		// ------------------------------------------------------------------

		public static void Host() {
			if (!SteamReady) { SmpLog.Error("Steam not ready."); return; }
			if (CurrentMode != Mode.None) { SmpLog.Log("already online."); return; }
			// Onceki bir katilma denemesinin artiklarini temizle. Join() bunu
			// yapiyordu, Host() yapmiyordu: basarisiz bir JOIN'den sonra OPEN
			// LOBBY'ye donuldugunde autoJoinPending/joinFailed bayraklari yanmis
			// kaliyordu ve MenuController.Tick bir sonraki framede
			// ui.HideLoading() + ShowOnline() yaparak host'un LOADING ekranini
			// kapatiyor, yerine eski hatayi yaziyordu. Host olmanin bir tek
			// onemi var: onceki deneme sonucu artik gecersiz.
			autoJoinPending = false;
			joinFailed = false;
			joinStatus = string.Empty;
			try {
				SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
				if (call == SteamAPICall_t.Invalid) {
					SmpLog.Error("CreateLobby call failed.");
					return;
				}
				crLobbyCreated.Set(call);
				SmpLog.Log("creating lobby...");
			}
			catch (Exception e) { SmpLog.Error("host failed: " + e.Message); }
		}

		public static void Join(ulong lobbyId) {
			if (!SteamReady) { SmpLog.Error("Steam not ready."); return; }
			if (CurrentMode != Mode.None) { SmpLog.Log("already online."); return; }
			autoJoinPending = false;
			joinFailed = false;
			joinStatus = "joining lobby " + lobbyId + "...";
			try {
				CSteamID lobby = new CSteamID(lobbyId);
				RemoteId = SteamMatchmaking.GetLobbyOwner(lobby);
				timeSinceHeartbeat = 0f;
				SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobby);
				if (call == SteamAPICall_t.Invalid) {
					SmpLog.Error("JoinLobby call failed.");
					RemoteId = CSteamID.Nil;
					return;
				}
				crLobbyEnter.Set(call);
				SmpLog.Log("joining lobby " + lobbyId + "...");
			}
			catch (Exception e) { SmpLog.Error("join failed: " + e.Message); }
		}

		/// <summary>
		/// Ask Steam for the game's lobby list and keep only the ones that are ours and
		/// speak the same protocol revision. This is what makes 'smp join' work without the
		/// player having to read a 17 digit lobby id off the host's screen.
		/// </summary>
		public static void DiscoverLobbies() {
			if (!SteamReady) { SmpLog.Error("Steam not ready."); return; }
			if (lobbyListBusy) { SmpLog.Log("already searching."); return; }
			try {
				SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
				if (call == SteamAPICall_t.Invalid) {
					SmpLog.Error("lobby search call failed.");
					return;
				}
				lobbyListBusy = true;
				lastDiscoverAt = Time.time;
				crLobbyList.Set(call);
				SmpLog.Log("searching for MSCSteamMP lobbies...");
			}
			catch (Exception e) {
				lobbyListBusy = false;
				SmpLog.Error("lobby search failed: " + e.Message);
			}
		}

		/// <summary>Lobbies found by the last <see cref="DiscoverLobbies"/>, ours only.</summary>
		public static readonly List<CSteamID> FoundLobbies = new List<CSteamID>();

		static void OnLobbyList(LobbyMatchList_t r, bool ioFail) {
			lobbyListBusy = false;
			FoundLobbies.Clear();
			if (ioFail) {
				SmpLog.Error("lobby search failed (io).");
				return;
			}
			for (int i = 0; i < (int)r.m_nLobbiesMatching; i++) {
				CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
				if (!lobby.IsValid()) continue;
				string tag = null;
				try { tag = SteamMatchmaking.GetLobbyData(lobby, LOBBY_KEY); } catch { }
				if (tag != "1") continue; // not ours
				string ver = null;
				try { ver = SteamMatchmaking.GetLobbyData(lobby, LOBBY_KEY_VERSION); } catch { }
				int parsed;
				if (!int.TryParse(ver, out parsed) || parsed != Protocol.PROTOCOL_VERSION) {
					SmpLog.Log("skipping lobby " + lobby.m_SteamID + " (version " + (ver ?? "?") + ")");
					continue;
				}
				FoundLobbies.Add(lobby);
			}
			SmpLog.Log("found " + FoundLobbies.Count + " compatible lobby/lobbies.");
			// Only auto-join if the player asked for it; 'smp find' just lists.
			if (!autoJoinPending || FoundLobbies.Count == 0) return;
			Join(FoundLobbies[0].m_SteamID);
		}

		/// <summary>
		/// Join by discovery when no id was given. Picks the first compatible lobby, or
		/// reports how many were found so the player can use 'smp join &lt;id&gt;'.
		/// </summary>
		public static void JoinAuto() {
			if (!SteamReady) { SmpLog.Error("Steam not ready."); return; }
			if (CurrentMode != Mode.None) { SmpLog.Log("already online."); return; }
			autoJoinPending = true;
			joinFailed = false;
			joinAttemptAt = Time.time;
			lastDiscoverAt = 0f;
			joinStatus = "searching for lobbies...";
			DiscoverLobbies();
		}

		/// <summary>
		/// Katılma bekleyişi: 5 sn'de bir yeniden ara, 60 sn'de vazgeç.
		/// Tek seferlik arama, host aramadan sonra kurarsa lobiyi kaçırıyordu
		/// ve menu sonsuz "LOADING MULTIPLAYER"da kalıyordu (canlı kanıt).
		/// Mode==None iken Update'in erken dönüşünden ÖNCE çağrılır.
		/// </summary>
		static void TickJoinRetry() {
			if (!autoJoinPending || CurrentMode != Mode.None) return;
			float now = Time.time;
			if (now - joinAttemptAt >= JOIN_TIMEOUT) {
				autoJoinPending = false;
				joinFailed = true;
				joinStatus = "no lobby found in " + (int)JOIN_TIMEOUT
					+ "s. Is your friend hosting right now, with the same mod version?";
				SmpLog.Error("join timed out: " + joinStatus);
				return;
			}
			if (lobbyListBusy) {
				// Yanıt hiç dönmezse kapi kilitli kalirdi; 15 sn'yi aşan
				// aramayı ölü sayıp kapıyı aç.
				if (now - lastDiscoverAt > 15f) lobbyListBusy = false;
				else return;
			}
			if (now - lastDiscoverAt >= JOIN_RETRY_INTERVAL) {
				joinStatus = "searching for lobbies... ("
					+ (int)(now - joinAttemptAt) + "s)";
				DiscoverLobbies();
			}
		}

		public static void Leave() {
			if (!SteamReady || CurrentMode == Mode.None) return;
			try {
				if (HasRemote) SendDisconnect();
				if (RemoteId.IsValid()) {
					try { SteamNetworking.CloseP2PSessionWithUser(RemoteId); } catch { }
				}
				if (LobbyId.IsValid()) {
					try { SteamMatchmaking.LeaveLobby(LobbyId); } catch { }
				}
			}
			catch { }
			// Aboneyi OTURUM KAPANMADAN ONCE bilgilendir: AuthorityManager
			// IsPeerConnected soruyor, o da HasRemote'a bakıyor. Once
			// CurrentMode sifirlansaydi kopan oyuncunun sahiplikleri hic
			// serbest birakilmazdi.
			Action dropped = OnPeerDisconnected;
			OnPeerDisconnected = null;
			if (dropped != null) dropped();

			// Host leaving: reset session state
			CurrentMode = Mode.None;
			LobbyId = CSteamID.Nil;
			RemoteId = CSteamID.Nil;
			RemoteHandshake = false;
			autoJoinPending = false;
			joinFailed = false;
			joinStatus = string.Empty;
			PeerCode = string.Empty;
			MenuController.RequestReturnToMainMenu = true;
			SmpLog.Log("left session (offline).");
		}

		public static bool Invite(CSteamID friend) {
			if (!IsHost || !LobbyId.IsValid()) return false;
			try { return SteamMatchmaking.InviteUserToLobby(LobbyId, friend); }
			catch { return false; }
		}

		public static List<FriendEntry> GetOnlineFriends() {
			List<FriendEntry> list = new List<FriendEntry>();
			if (!SteamReady) return list;
			try {
				int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
				for (int i = 0; i < n; i++) {
					CSteamID f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
					if (SteamFriends.GetFriendPersonaState(f) == EPersonaState.k_EPersonaStateOffline)
						continue;
					FriendEntry e = new FriendEntry();
					e.id = f;
					try { e.name = SteamFriends.GetFriendPersonaName(f); }
					catch { e.name = f.m_SteamID.ToString(); }
					try {
						FriendGameInfo_t gi;
						if (SteamFriends.GetFriendGamePlayed(f, out gi))
							e.playingMSC = gi.m_gameID.AppID() == GAME_APP_ID;
					}
					catch { }
					if (e.playingMSC) list.Insert(0, e); else list.Add(e);
				}
			}
			catch { }
			return list;
		}

		// ------------------------------------------------------------------
		// Callbacks
		// ------------------------------------------------------------------

		static void OnSessionRequest(P2PSessionRequest_t r) {
			try {
				if (SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote))
					SmpLog.Log("accepted P2P session with " + r.m_steamIDRemote.m_SteamID);
			}
			catch (Exception e) { SmpLog.Error("accept P2P failed: " + e.Message); }
		}

		static void OnConnectFail(P2PSessionConnectFail_t r) {
			SmpLog.Error("P2P failed (" + (EP2PSessionError)r.m_eP2PSessionError
				+ ") with " + r.m_steamIDRemote.m_SteamID);
		}

		static void OnJoinRequested(GameLobbyJoinRequested_t r) {
			SmpLog.Log("invite accepted, joining " + r.m_steamIDLobby.m_SteamID);
			if (CurrentMode != Mode.None) Leave();
			autoJoinPending = false;
			joinFailed = false;
			joinStatus = "joining lobby " + r.m_steamIDLobby.m_SteamID + "...";
			RemoteId = r.m_steamIDFriend;
			timeSinceHeartbeat = 0f;
			try {
				SteamAPICall_t call = SteamMatchmaking.JoinLobby(r.m_steamIDLobby);
				if (call == SteamAPICall_t.Invalid) {
					SmpLog.Error("JoinLobby call failed.");
					RemoteId = CSteamID.Nil;
					return;
				}
				crLobbyEnter.Set(call);
			}
			catch (Exception e) { SmpLog.Error("join failed: " + e.Message); }
		}

		static void OnLobbyCreated(LobbyCreated_t r, bool ioFail) {
			if (ioFail || r.m_eResult != EResult.k_EResultOK) {
				SmpLog.Error("lobby create failed: " + r.m_eResult + " io:" + ioFail);
				return;
			}
			CurrentMode = Mode.Host;
			LobbyId = new CSteamID(r.m_ulSteamIDLobby);
			RemoteId = CSteamID.Nil;
			RemoteHandshake = false;
			PeerCode = string.Empty;
			// Publish the protocol revision so a client can skip a mismatched lobby during
			// discovery instead of connecting and failing the handshake.
			try {
				SteamMatchmaking.SetLobbyData(LobbyId, LOBBY_KEY, "1");
				SteamMatchmaking.SetLobbyData(LobbyId, LOBBY_KEY_VERSION, Protocol.PROTOCOL_VERSION.ToString());
			}
			catch { }
			// The lobby id is the only thing that can actually ADDRESS this lobby.
			// The six character code is a session gate, not an address, so the menu
			// has to show it or "join by code" cannot work at all.
			try { MenuController.OnLobbyCreated(); } catch { }
			SmpLog.Log("HOSTING. Lobby ID: " + r.m_ulSteamIDLobby);
			SmpLog.Log("Friend: press Shift+Tab -> invite, or run 'smp join' with no arguments.");
		}

		static void OnLobbyEnter(LobbyEnter_t r, bool ioFail) {
			if (ioFail || r.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
				SmpLog.Error("lobby join failed (" + r.m_EChatRoomEnterResponse + ").");
				RemoteId = CSteamID.Nil;
				autoJoinPending = false;
				joinFailed = true;
				joinStatus = "lobby join failed (" + r.m_EChatRoomEnterResponse
					+ "). Wrong version, full lobby, or invite expired?";
				return;
			}
			CurrentMode = Mode.Client;
			LobbyId = new CSteamID(r.m_ulSteamIDLobby);
			autoJoinPending = false;
			joinFailed = false;
			joinStatus = string.Empty;
			try { RemoteId = SteamMatchmaking.GetLobbyOwner(LobbyId); } catch { }
			SmpLog.Log("joined lobby " + r.m_ulSteamIDLobby + ", host is " + RemoteId.m_SteamID);
			SendHandshake();
		}

		/// <summary>
		/// Check if the host has left the lobby. If the lobby owner is no longer valid
		/// or has changed, kick the client back to the menu.
		/// </summary>
		static void CheckHostState() {
			if (CurrentMode != Mode.Client) return;
			if (!LobbyId.IsValid()) return;

			// Check if our known host is still the lobby owner
			try {
				CSteamID currentOwner = SteamMatchmaking.GetLobbyOwner(LobbyId);
				if (!currentOwner.IsValid()) {
					// Lobby owner left - kick the client
					SmpLog.Log("host left the lobby, kicking client.");
					Leave();
					return;
				}
				if (currentOwner.m_SteamID != RemoteId.m_SteamID) {
					// Host changed - this shouldn't normally happen in a 2-player game,
					// but handle it gracefully
					SmpLog.Log("host changed from " + RemoteId.m_SteamID + " to " + currentOwner.m_SteamID);
					Leave();
					return;
				}
			}
			catch { /* If we can't check, just continue; timeout will handle it */ }
		}

		// ------------------------------------------------------------------
		// Send
		// ------------------------------------------------------------------

		/// <summary>
		/// Gönderim sınırı. Burası logun EN DEĞERLİ noktası: "gönderdim mi"
		/// sorusunun cevabı. Etiket <c>send</c> ve mesaj kimliği alan olarak
		/// yazılır, karşı tarafta aynı satır <c>recv</c> olarak görünür; iki
		/// tarafın logu tek bakışta eşleştirilir.
		///
		/// Seviye Debug: kanal eşiği kapalıyken maliyeti yalnızca iki string
		/// karşılaştırması ve bir bayt okuması — mesajın tamamı biçimlenmez.
		/// </summary>
		static bool SendRaw(byte[] data, EP2PSend type) {
			bool handshake = HasRemoteAllowHandshake(data);
			if (!handshake) {
				// Sessizce atılan paketler teşhisin EN SIK gelen sebebi: oturum
				// kurulmadan gönderim denenmesi, ya da kanal eşiği kapalıyken
				// bir modülün göndermeyi unutması. Sessizce düşmüyoruz.
				if (LogChannels.Enabled(LogChannels.Net, LogSeverity.Debug) && data != null && data.Length > 4) {
					LogEngine.Debug(LogChannels.Net, "drop",
						"packet dropped before send",
						new LogFields().Add("id", (int)data[4]).Add("bytes", data.Length)
							.Add("why", "no session yet"));
				}
				return false;
			}
			bool sent;
			try {
				sent = SteamNetworking.SendP2PPacket(RemoteId, data, (uint)data.Length, type, Protocol.P2P_CHANNEL);
			}
			catch { return false; }
			if (data.Length > 4) LogRemote.NoteSent(data[4], data.Length, sent);

			// SAYAC BURADA, cunku BURADA gercek sonucu goruyoruz.
			// Onceki konum (Protocol.Finish) yanlitti: paket olusturuldu
			// diye sayiyordu, ama oturum yoksa veya Steam guvenilir
			// kanali reddettiyse paket HICBIR ZAMAN ulasmiyordu.
			//
			// KANIT (iki logun karsilastirmasi): host WorldState
			// 127 gonderdi / client 74 aldi. Paylasimli yon 61/61 TAM.
			// Yani kayip tek yonlu ve sayimin yerine degil, GONDERIM
			// basarisizligindan.
			try {
				Protocol.NetStats.CountSend((Protocol.Msg)data[4], data.Length, sent);
			} catch { }
			if (!sent && LogChannels.Enabled(LogChannels.Net, LogSeverity.Debug) && data.Length > 4) {
				LogEngine.Debug(LogChannels.Net, "drop", "Steam refused the packet",
					new LogFields().Add("id", (int)data[4]).Add("bytes", data.Length)
						.Add("mode", type.ToString()));
			}
			return sent;
		}

		/// <summary>Handshake may go out before RemoteHandshake is set; everything else needs it.</summary>
		static bool HasRemoteAllowHandshake(byte[] data) {
			if (!SteamReady || CurrentMode == Mode.None) return false;
			if (!RemoteId.IsValid()) return false;
			if (RemoteHandshake) return true;
			if (data != null && data.Length > 5 && data[4] == (byte)Protocol.Msg.Handshake) return true;
			return false;
		}

		public static bool SendReliable(byte[] data) {
			return SendRaw(data, EP2PSend.k_EP2PSendReliable);
		}

		public static bool SendUnreliable(byte[] data) {
			return SendRaw(data, EP2PSend.k_EP2PSendUnreliable);
		}

		/// <summary>
		/// Gecikmeyi en aza indiren güvenilmez gönderim.
		///
		/// Araç ve oyuncu KONUMU için kullanılır: 20 Hz fizik akışı. Bir kare
		/// gecikmesi interpolatörde yumuşatıldığı için kayıp paket zararsız,
		/// ama güveniliz kanalın kuyruğu biriktiği için titreme yapıyor.
		/// k_EP2PSendUnreliable vs UnreliableNoDelay farkı: noDelay paketi
		/// Steam'in iç kuyruğuna girmeden gönderilir, yani bir sonraki paketi
		/// bekletmez.
		/// </summary>
		public static bool SendUnreliableNoDelay(byte[] data) {
			return SendRaw(data, EP2PSend.k_EP2PSendUnreliableNoDelay);
		}


		/// <summary>
		/// �stemcinin bildirece�i kod: panelde yazd��� lobi kodu.
		///
		/// Kod KULLANICI SE�M�YOR; sunucu her a��l��ta rastgele �retiyor. Ama
		/// kat�lmak i�in yaz�lmas� gerekiyor: do�rulama el s�k��mada yap�l�r, yani
		/// oturum kurulmadan. Alan protokold� oldu�u i�in duruyor; b�rak�l�rsa
		/// el s�k��man�n ikinci ad�m� desync olur.
		/// </summary>
		public static string PendingCode = string.Empty;

		/// <summary>
		/// Host'un kodunu istemci taraf�nda ��renilmi� h�li.
		///
		/// �lk pakette istemci bo� g�nderebilir (konsoldan 'smp join' ile
		/// kat�l�rsa); kar��l���nda host'un kodu gelir ve buraya yaz�l�r. Sonraki
		/// el s�k��malarda istemci bu kodu geri yollar, host kar��la�t�r�r. B�ylece
		/// iki taraf�n ayn� oturumda oldu�u da do�rulan�r.
		/// </summary>
		public static string PeerCode = string.Empty;

		public static void SendHandshake() {
			MemoryStream s = Protocol.Begin(Protocol.Msg.Handshake);
			BinaryWriter w = new BinaryWriter(s);
			w.Write(Protocol.PROTOCOL_VERSION);
			w.Write(NetClockMs());

			// �kinci ad�m: lobi kodu.
			//   Host    -> kendi �retti�i kod.
			//   �stemci -> ilk pakette yazd��� kod (yoksa bo�), sonrakilerde
			//              host'tan ��rendi�i kod.
			string code = (CurrentMode == Mode.Client)
				? (string.IsNullOrEmpty(PeerCode) ? (PendingCode ?? string.Empty) : PeerCode)
				: LobbyCode.Current;
			w.Write(code != null ? code : string.Empty);

			SendReliable(Protocol.Finish(s));
		}

		/// <summary>
		/// El s�k��madaki lobi kodunu do�rular. Bu bir KAPI: kabul edilmeden
		/// oturum kurulmaz (RemoteHandshake set edilmez) ve 3 hatal� denemede
		/// ba�lant� tamamen d��er (LobbyCode.RegisterFailure).
		///
		///   Host    - gelen kod kendi �retti�i kodla kar��la�t�r�l�r.
		///   �stemci - host'un kodu gelir; kaydedilir ve sonrakilerde
		///              kar��la�t�r�l�r. B�ylece istemci yanl�� lobiye ba�lan�p
		///              bunu fark etmezse sessizce yanl�� oyuna girmez.
		///
		/// Bo� kod �zel olarak ele al�n�yor: ilk el s�k��mada istemci hen�z host'un
		/// kodunu bilmiyor ve konsoldan kat�l�rken bo� g�nderebilir.
		/// LobbyCode.Verify bo� kodu reddetti�i i�in (uzunluk e�le�miyor) bu dal
		/// olmadan konsoldan kat�l�m hi� �al��maz.
		/// </summary>
		static bool VerifyHandshakeCode(string received) {
			if (CurrentMode == Mode.Host) {
				string mine = LobbyCode.Current;
				if (string.IsNullOrEmpty(mine)) return true;      // kod �retilmemi�

				if (string.IsNullOrEmpty(received)) {
					SmpLog.State("peer joined without a lobby code (console join). "
						+ "Session " + mine + ".");
					return true;
				}
				if (LobbyCode.Verify(mine, received)) return true;

				LobbyCode.RegisterFailure();
				SmpLog.Error("wrong lobby code from "
					+ (RemoteId.IsValid() ? RemoteId.m_SteamID.ToString() : "peer") + ".");
				Leave();
				return false;
			}

			// �stemci: host'un kodunu ��ren, sonra kar��la�t�r.
			if (string.IsNullOrEmpty(PeerCode)) {
				PeerCode = received ?? string.Empty;
				SmpLog.State("lobby code learned from the host: "
					+ (PeerCode.Length > 0 ? PeerCode : "(none)"));
				return true;
			}
			if (string.IsNullOrEmpty(received)) return true;       // host kodsuz
			if (LobbyCode.Verify(PeerCode, received)) return true;

			SmpLog.Error("this lobby expects a different code ('" + PeerCode
				+ "' vs '" + received + "').");
			Leave();
			return false;
		}

		static void SendHeartbeat() {
			MemoryStream s = Protocol.Begin(Protocol.Msg.Heartbeat);
			BinaryWriter w = new BinaryWriter(s);
			w.Write(NetClockMs());
			// Whether this side has a save loaded. Without it the host cannot tell "the client
			// is still in the menu" from "the client is here but has no world", which is the
			// difference between a working session and one that looks broken.
			w.Write(SatsumaRegistry.Root != null);
			SendReliable(Protocol.Finish(s));
		}

		/// <summary>
		/// Tells the peer we are going away, so it can drop the dummy immediately instead
		/// of waiting for the 60 s timeout.
		/// </summary>
		static void SendDisconnect() {
			try { SendReliable(Protocol.Finish(Protocol.Begin(Protocol.Msg.Disconnect))); }
			catch { }
		}

		/// <summary>
		/// Per-frame Steam pump. Order matters and is deliberate:
		///
		///   1. RunCallbacks  - dispatch lobby/P2P results. Nothing else may run before
		///      this, or a join callback is a frame late.
		///   2. ProcessMessages - read and route whatever arrived.
		///   3. Timeout then heartbeat - so a packet that arrived this frame has already
		///      reset the idle timer and the peer is not dropped while it is talking.
		/// </summary>
		public static void Update() {
			if (!SteamReady) return;
			try { SteamAPI.RunCallbacks(); } catch { }

			// Katılma bekleyişi çevrimdışıyken de ilerler (lobiye girmeden
			// CurrentMode None'dır; aşağıdaki erken dönüş onu atlardı).
			try { TickJoinRetry(); } catch { }

			if (CurrentMode == Mode.None) return;

			// Check if host has left - this allows immediate client ejection
			// when the host leaves, rather than waiting 60s for timeout.
			CheckHostState();

			ProcessMessages();

			if (RemoteId.IsValid() && RemoteHandshake) {
				timeSinceHeartbeat += Time.deltaTime;
				if (timeSinceHeartbeat >= TIMEOUT_TIME) {
					SmpLog.Error("connection timed out after "
						+ (int)TIMEOUT_TIME + "s with no traffic.");
					try { VehicleRegistry.OnPeerTimeout(); } catch { }
					Leave();
					return;
				}

				timeToHeartbeat -= Time.deltaTime;
				if (timeToHeartbeat <= 0f) {
					timeToHeartbeat = HEARTBEAT_INTERVAL;
					SendHeartbeat();
				}
			}
		}

		/// <summary>
		/// Drains the P2P queue and routes each packet.
		///
		/// Capped at MAX_PACKETS_PER_FRAME per frame on purpose: a flood must cost a
		/// bounded amount of work, not stall the frame for as long as the queue is long.
		/// The Steam API calls inside are wrapped individually, because a Steam failure
		/// must drop this frame's traffic rather than the whole loop.
		/// </summary>
		static void ProcessMessages() {
			int budget = MAX_PACKETS_PER_FRAME;
			while (budget-- > 0) {
				uint size;
				bool available;
				try { available = SteamNetworking.IsP2PPacketAvailable(out size, Protocol.P2P_CHANNEL); }
				catch { return; }
				if (!available || size == 0) return;

				if (size > Protocol.MAX_PACKET_SIZE) {
					DiscardOversized();
					continue;
				}

				byte[] data = new byte[size];
				CSteamID sender = CSteamID.Nil;
				uint read;
				bool ok;
				try { ok = SteamNetworking.ReadP2PPacket(data, size, out read, out sender, Protocol.P2P_CHANNEL); }
				catch { return; }
				if (!ok || read == 0) { Telemetry.BadPackets++; continue; }

				// Steam can hand back more bytes than it reserved if a packet was queued
				// larger than the space asked for; shrink rather than read past the end.
				if (read < size) Array.Resize(ref data, (int)read);

				RoutePacket(sender, data);
			}
		}

		/// <summary>
		/// Decodes and dispatches one packet.
		///
		/// Everything is inside a try: a truncated or foreign packet must not take the
		/// frame down, so a bad body is counted and dropped rather than thrown. Counting
		/// matters too - a stream of bad packets has to be visible in 'smp status'
		/// instead of looking like silence.
		/// </summary>
		static void RoutePacket(CSteamID sender, byte[] data) {
			try {
				// Anything not from the peer we think we have is dropped, so a late packet
				// from a previous session cannot corrupt the tables of the new one.
				if (RemoteId.IsValid() && sender != RemoteId) {
					Telemetry.BadPackets++;
					if (LogChannels.Enabled(LogChannels.Net, LogSeverity.Debug)) {
						LogEngine.Debug(LogChannels.Net, "drop", "packet from an unexpected peer",
							new LogFields().AddHex("from", (ulong)sender.m_SteamID)
								.AddHex("expected", (ulong)RemoteId.m_SteamID));
					}
					return;
				}

				MemoryStream ms = new MemoryStream(data, false);
				BinaryReader r = new BinaryReader(ms);
				Protocol.Msg id;
				if (!Protocol.TryReadHeader(r, out id)) {
					Telemetry.BadPackets++;
					if (LogChannels.Enabled(LogChannels.Net, LogSeverity.Debug)) {
						LogEngine.Debug(LogChannels.Net, "drop", "header did not match the protocol",
							new LogFields().Add("bytes", data.Length));
					}
					return;
				}

				// Gelen her paket. Aynalanmış log açıksa bu satır gönderen
				// taraftaki "send" satırının karşılığıdır: iki tarafın logunda
				// aynı satırlar yan yana durur.
				LogRemote.NoteReceived((byte)id, data.Length);

				// Protocol-level messages are handled here; everything else goes to the
				// mod's router, which owns the per-subsystem handlers.
				if (id == Protocol.Msg.Handshake) {
					OnHandshake(sender, r);
				}
				else if (id == Protocol.Msg.Heartbeat) {
					ulong clientClock = 0;
					bool hasWorld = false;
					try {
						clientClock = r.ReadUInt64();
						hasWorld = r.ReadBoolean();
					}
					catch { }

					// Only trust it once a handshake has happened, so a stray packet from
					// someone else cannot set our view of the peer.
					if (RemoteHandshake) {
						AutoLoad.PeerReportedWorld.Load = hasWorld;
						AutoLoad.PeerReportedWorld.LastSeen = NetClockMs();
					}

					MemoryStream s = Protocol.Begin(Protocol.Msg.HeartbeatResp);
					BinaryWriter w = new BinaryWriter(s);
					w.Write(clientClock);
					w.Write(NetClockMs());
					SendReliable(Protocol.Finish(s));
				}
				else if (id == Protocol.Msg.HeartbeatResp) {
					try {
						ulong clientClock = r.ReadUInt64();
						RemoteClock = r.ReadUInt64();
						PingMs = (uint)NetClockMs() - (uint)clientClock;
					}
					catch { }
					timeSinceHeartbeat = 0f;
				}
				else if (id == Protocol.Msg.Disconnect) {
					SmpLog.Log("peer disconnected.");
					try { VehicleRegistry.OnPeerTimeout(); } catch { }
					Leave();
				}
				else {
					timeSinceHeartbeat = 0f;
					if (OnMessage != null) {
						try { OnMessage(id, sender, r); }
						catch (Exception e) { SmpLog.Error("msg handler: " + e.Message); }
					}
				}
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("bad packet dropped: " + e.Message);
			}
		}

		/// <summary>
		/// Drops an oversized packet.
		///
		/// The buffer is capped at what the API will accept, so the packet is read
		/// out and thrown away rather than parsed: it is corrupt either way, and
		/// without draining it the queue would keep handing back the same packet and
		/// the frame budget would be spent forever on it.
		/// </summary>
		static void DiscardOversized() {
			Telemetry.BadPackets++;
			try {
				byte[] scratch = new byte[Protocol.MAX_PACKET_SIZE];
				CSteamID sender = CSteamID.Nil;
				uint read;
				SteamNetworking.ReadP2PPacket(scratch, (uint)scratch.Length, out read, out sender,
					Protocol.P2P_CHANNEL);
			}
			catch { }
		}

		/// <summary>
		/// The handshake. This is where a session is accepted or refused.
		///
		/// Three things have to line up before RemoteHandshake is set: the protocol
		/// revision, the lobby code, and the peer identity. Until then nothing else is
		/// routed, which is what makes "the session never starts" a state you can
		/// actually see in the log instead of a session that silently shares nothing.
		/// </summary>
		static void OnHandshake(CSteamID sender, BinaryReader r) {
			byte version = 0;
			ulong clock = 0;
			string code = string.Empty;

			try {
				version = r.ReadByte();
				clock = r.ReadUInt64();
				// Tolerate a peer that sent a shorter body: the code is optional in the
				// stream and a missing tail must not read past the end.
				code = (r.BaseStream.Position + 1L <= r.BaseStream.Length) ? r.ReadString() : string.Empty;
			}
			catch {
				Telemetry.BadPackets++;
				return;
			}

			if (version != Protocol.PROTOCOL_VERSION) {
				SmpLog.Error("protocol mismatch: peer speaks v" + version
					+ ", this build speaks v" + Protocol.PROTOCOL_VERSION
					+ ". Both sides need the same mod version.");
				return;
			}

			if (!VerifyHandshakeCode(code)) return;

			RemoteClock = clock;
			bool firstTime = !RemoteHandshake;
			RemoteId = sender;
			RemoteHandshake = true;
			timeSinceHeartbeat = 0f;

			SmpLog.State("connected with " + sender.m_SteamID.ToString()
				+ (CurrentMode == Mode.Host ? " (client)" : " (host)"));

			if (firstTime && CurrentMode == Mode.Host) {
				// Reply so the client learns our code, then push the world: the client
				// cannot ask for state it does not know exists.
				SendHandshake();
				try { VehicleRegistry.SendAllFullState(); } catch { }
				try { SatsumaRegistry.BeginInitialSync(); } catch { }
			}
			if (firstTime && CurrentMode == Mode.Client) {
				try { VehicleRegistry.SendAllFullState(); } catch { }
				try { SatsumaRegistry.OnPeerConnected(); } catch { }
			}

			// Etkilesim katmani: JIP snapshot'i ve sahiplik snapshot'i.
			// Sadece ilk el sikismada, cunku ikisi de oturum BASINDA bir kez
			// gonderilmeli; her el sikismada tekrarlansaydi gereksiz trafik
			// olurdu.
			if (firstTime) {
				Action connected = OnPeerConnected;
				if (connected != null) {
					try { connected(); }
					catch (Exception e) { SmpLog.Error("peer connected handler: " + e.Message); }
				}
			}
		}
	}
}
