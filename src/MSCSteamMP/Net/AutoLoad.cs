using System;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Brings the client into the world once the peer is already there.
	///
	/// The scene name is not a guess. Continue is a PlayMaker FSM on
	/// <c>Interface/Buttons/ButtonContinue</c> in level0, its LoadLevel action's
	/// <c>levelName</c> parameter reads <c>GAME</c>, and the build settings in mainData
	/// list <c>Assets/_Scenes/GAME.unity</c> at build index 2. So "GAME" is the exact name
	/// the game itself uses, and loading it is the same transition as pressing Continue.
	///
	/// What this does not do is read or write a save. Pressing Continue in-game is
	/// equivalent and is left to the player, because the save itself belongs to MSC.
	/// </summary>
	public static class AutoLoad {

		/// <summary>
		/// The scene Continue loads. Recovered from the button's own LoadLevel action and
		/// cross-checked against the build settings scene list.
		/// </summary>
		public const string WORLD_SCENE = "GAME";

		/// <summary>
		/// Whether a client still in the menu is taken into the world once the host has
		/// one.
		///
		/// Varsayılan AÇIK. Kullanıcı isteği: "join lobby olduğunda arkadaşımın oyunu
		/// açılsın ve spawn olsun." Kapalıyken istemci menüde bekler ve CONTINUE'ye
		/// basmayı bekler, ki bu tam olarak istenmeyen durumdu.
		///
		/// Kapatmak için: 'smp autoload off'.
		/// </summary>
		public static bool Enabled = true;

		static bool warnedPeer;
		static bool warnedSelf;
		static bool loading;

		/// <summary>Called every frame from the mod's Update.</summary>
		public static void Update() {
			// If the host has signaled return to main menu, do it now
			if (MenuController.RequestReturnToMainMenu) {
				MenuController.RequestReturnToMainMenu = false;
				SmpLog.Log("host left - returning to main menu.");
				try { Application.LoadLevel("MainMenu"); } catch { }
				return;
			}
			if (!SteamTransport.HasRemote) {
				Reset();
				return;
			}
			if (!Enabled) {
				Reset();
				return;
			}

			bool weAreInWorld = SatsumaRegistry.Root != null;
			bool theyAreInWorld = PeerInWorld;

			if (weAreInWorld && !theyAreInWorld) {
				if (!warnedSelf) {
					warnedSelf = true;
					SmpLog.Warning("you have loaded a save but your friend is still in the main menu.");
					SmpLog.Warning("nothing will appear on their side until they press CONTINUE too.");
				}
			}
			else {
				warnedSelf = false;
			}

			// A client waiting for a world that the host already has.
			if (!weAreInWorld && theyAreInWorld && SteamTransport.IsClient) {
				if (!warnedPeer) {
					warnedPeer = true;
					SmpLog.Log("the host is in the world. "
						+ (Enabled ? "Joining you automatically." : "Press CONTINUE to load your save."));
				}
				if (Enabled) EnterWorld();
			}
			else {
				warnedPeer = false;
			}
		}

		/// <summary>
		/// Load the game world. The same transition the Continue button performs, so the
		/// save prompt and the rest of MSC's own startup sequence run normally.
		/// </summary>
		public static void EnterWorld() {
			if (loading || SatsumaRegistry.Root != null) return;
			loading = true;
			try {
				SmpLog.Log("loading '" + WORLD_SCENE + "'...");
				Application.LoadLevel(WORLD_SCENE);
			}
			catch (Exception e) {
				SmpLog.Error("could not load '" + WORLD_SCENE + "': " + e.Message);
				SmpLog.Error("press CONTINUE yourself; the rest of the mod works either way.");
				loading = false;
			}
		}

		/// <summary>
		/// Whether the peer has a world: a client that received a SyncDone, or a host that
		/// saw the flag in a heartbeat.
		/// </summary>
		public static bool PeerInWorld {
			get {
				if (!SteamTransport.HasRemote) return false;
				return SteamTransport.IsHost
					? PeerReportedWorld.Load
					: Telemetry.ReceivedSyncDone > 0;
			}
		}

		static void Reset() {
			warnedSelf = false;
			warnedPeer = false;
		}

		/// <summary>Set from the heartbeat, so a host knows the client actually has a world.</summary>
		public static class PeerReportedWorld {
			public static bool Load;
			public static float LastSeen;
		}
	}
}
