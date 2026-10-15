using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SteamMP
{
	// Token: 0x02000036 RID: 54
	public static class MenuController
	{
		// Token: 0x1700003D RID: 61
		// (get) Token: 0x0600022C RID: 556 RVA: 0x00015BC1 File Offset: 0x00013DC1
		public static MenuUi Ui
		{
			get
			{
				return MenuController.ui;
			}
		}

		// Token: 0x1700003E RID: 62
		// (get) Token: 0x0600022D RID: 557 RVA: 0x00015BC8 File Offset: 0x00013DC8
		public static string NewGameEvent
		{
			get
			{
				return MenuController.newGameEvent;
			}
		}

		// Token: 0x1700003F RID: 63
		// (get) Token: 0x0600022E RID: 558 RVA: 0x00015BCF File Offset: 0x00013DCF
		public static List<string> NewGameCandidates
		{
			get
			{
				return MenuController.candidates;
			}
		}

		// Token: 0x17000040 RID: 64
		// (get) Token: 0x0600022F RID: 559 RVA: 0x00015BD6 File Offset: 0x00013DD6
		public static bool InMenu
		{
			get
			{
				return MenuController.inMenu;
			}
		}

		// Token: 0x17000041 RID: 65
		// (get) Token: 0x06000230 RID: 560 RVA: 0x00015BE2 File Offset: 0x00013DE2
		public static bool RequestReturnToMainMenu { get; set; }

		// Token: 0x17000041 RID: 65
		// (get) Token: 0x06000230 RID: 560 RVA: 0x00015BDD File Offset: 0x00015DDD
		public static int HiddenCount
		{
			get
			{
				return MenuController.hiddenCount;
			}
		}

		// Token: 0x17000042 RID: 66
		// (get) Token: 0x06000231 RID: 561 RVA: 0x00015BE4 File Offset: 0x00013DE4
		public static bool HasBackground
		{
			get
			{
				return MenuController.ui.HasBackgroundImage;
			}
		}

		// Token: 0x06000232 RID: 562 RVA: 0x00015BF0 File Offset: 0x00013DF0
		public static void Install()
		{
			MenuController.ui.OnPlayOnline += delegate
			{
				MenuController.ui.ShowOnline();
				SmpLog.Print("menu: PLAY ONLINE");
			};
			// NEW GAME kaldirildi: modun menusu yalnizca PLAY ONLINE ve GORUNUM
			// satirlarini tasiyor. Oyunun kendi New Game butonu 3B sahnede
			// tamamen gizleniyor (HiddenNames icinde "ButtonNewgame").
			MenuController.ui.OnHost += MenuController.OpenLobby;
			MenuController.ui.OnJoin += MenuController.JoinLobby;
			MenuController.ui.OnBack += delegate
			{
				if (MenuController.waitingForPeer || SteamTransport.IsHost)
				{
					MenuController.waitingForPeer = false;
					SteamTransport.Leave();
					MenuController.ui.SetHostLobbyId(0UL);
					MenuController.ui.ClearCode();
					SmpLog.Print("menu: left the session.");
				}
				MenuController.ui.ShowMain();
				SmpLog.Print("menu: back to the main list.");
			};
		}

		// Token: 0x06000233 RID: 563 RVA: 0x00015C9C File Offset: 0x00013E9C
		public static void Tick(bool inputBlocked)
		{
			MenuController.TickMouse();
			float dt = Time.deltaTime;
			if (dt <= 0f)
			{
				return;
			}
			MenuController.ui.Tick(dt);
			MenuController.ApplyCursorState();
			if (MenuController.inMenu || SatsumaRegistry.Root == null)
			{
				if (SteamTransport.JoinFailed)
				{
					if (MenuController.ui.Target == MenuUi.View.Loading)
					{
						MenuController.ui.HideLoading();
					}
					MenuController.ui.ShowOnline();
					MenuController.ui.SetStatus("notfound", SteamTransport.JoinStatus);
					SteamTransport.ClearJoinResult();
				}
				else if (SteamTransport.AutoJoinPending && MenuController.ui.Target == MenuUi.View.Loading && !string.IsNullOrEmpty(SteamTransport.JoinStatus))
				{
					MenuController.ui.SetLoadReason(SteamTransport.JoinStatus);
				}
				if (MenuController.waitingForPeer && SteamTransport.HasRemote)
				{
					MenuController.waitingForPeer = false;
					if (MenuController.inMenu)
					{
						MenuController.ui.SetStatus("joining", "Your friend joined. Loading the world...");
						MenuController.ui.ShowLoading("Your friend joined. Loading the world...");
					}
					SmpLog.Print("peer joined; entering the world.");
					AutoLoad.EnterWorld();
				}
				if (MenuController.waitingForPeer && Time.time >= MenuController.hostDeadline)
				{
					MenuController.waitingForPeer = false;
					SteamTransport.Leave();
					InvitePanel.ClearSession();
					if (MenuController.ui.Target == MenuUi.View.Loading)
					{
						MenuController.ui.HideLoading();
					}
					MenuController.ui.ShowOnline();
					MenuController.ui.SetStatus("notfound", "Steam did not create the lobby. Try again.");
					SmpLog.Error("lobby creation timed out after " + 15f.ToString() + "s.");
				}
			}
			if (Time.time >= MenuController.lastSceneCheck)
			{
				MenuController.lastSceneCheck = Time.time + 1f;
				MenuController.inMenu = !(SatsumaRegistry.Root != null);
				if (MenuController.inMenu && MenuController.ui.Target == MenuUi.View.Hidden)
				{
					MenuController.ui.ShowMain();
					MenuController.ApplyHide();
					SmpLog.Print("main menu: custom list active (" + MenuController.hiddenCount + " object(s) hidden).");
				}
				else if (!MenuController.inMenu && MenuController.ui.IsVisible)
				{
					if (MenuController.ui.Target == MenuUi.View.Loading)
					{
						MenuController.ui.HideLoading();
					}
					else
					{
						MenuController.ui.Hide();
						MenuController.Restore();
						SmpLog.Print("world loaded; menu restored.");
					}
				}
			}
			MenuController.ReadInput(inputBlocked);
		}

		// Token: 0x06000234 RID: 564 RVA: 0x00015EF8 File Offset: 0x000140F8
		private static void TickMouse()
		{
			bool uiVisible = false;
			try
			{
				uiVisible = MenuController.ui.IsVisible;
			}
			catch
			{
			}
			bool consoleUp = false;
			try
			{
				consoleUp = SteamMPBehaviour.Instance != null && SteamMPBehaviour.Instance.Console != null && SteamMPBehaviour.Instance.Console.Visible;
			}
			catch
			{
			}
			MenuMouse.Tick(uiVisible || consoleUp);
		}

		// Token: 0x06000235 RID: 565 RVA: 0x00015F70 File Offset: 0x00014170
		private static void ApplyCursorState()
		{
			try
			{
				if (SteamMPBehaviour.Instance != null && SteamMPBehaviour.Instance.Console.Visible)
				{
					Cursor.visible = true;
					if (Cursor.lockState != CursorLockMode.None)
					{
						Cursor.lockState = CursorLockMode.None;
					}
				}
				else if (GameMenuGuard.GameMenuOpen)
				{
					Cursor.visible = true;
					if (Cursor.lockState != CursorLockMode.None)
					{
						Cursor.lockState = CursorLockMode.None;
					}
				}
				else if (MenuController.inMenu && MenuController.Ui.IsVisible)
				{
					Cursor.visible = true;
					if (Cursor.lockState != CursorLockMode.None)
					{
						Cursor.lockState = CursorLockMode.None;
					}
				}
				else if (!MenuController.inMenu && !MenuController.MouseOverUI())
				{
					Cursor.visible = false;
					if (Cursor.lockState != CursorLockMode.Locked)
					{
						Cursor.lockState = CursorLockMode.Locked;
					}
				}
			}
			catch
			{
			}
		}

		// Token: 0x06000236 RID: 566 RVA: 0x0001602C File Offset: 0x0001422C
		private static bool MouseOverUI()
		{
			try
			{
				if (SteamMPBehaviour.Instance != null && SteamMPBehaviour.Instance.Console != null && SteamMPBehaviour.Instance.Console.Visible)
				{
					return true;
				}
			}
			catch
			{
			}
			try
			{
				if (MenuController.inMenu && MenuController.Ui.IsVisible)
				{
					return true;
				}
			}
			catch
			{
			}
			try
			{
				if (GameMenuGuard.GameMenuOpen)
				{
					return true;
				}
			}
			catch
			{
			}
			return false;
		}

		// Token: 0x06000237 RID: 567 RVA: 0x000160C8 File Offset: 0x000142C8
		private static void ReadInput(bool inputBlocked)
		{
			if (!MenuController.ui.IsVisible || !MenuController.inMenu)
			{
				return;
			}
			if (UiFocus.ConsoleVisible() || UiFocus.ChatVisible())
			{
				return;
			}
			if (MenuController.ui.CommandFocused)
			{
				if (Input.GetKeyDown(KeyCode.Escape))
				{
					MenuController.ui.BlurCommand();
				}
				return;
			}
			if (MenuController.ui.Target == MenuUi.View.GameDialog)
			{
				if (Input.GetKeyDown(KeyCode.Escape))
				{
					MenuController.confirmState = 3;
					MenuController.ui.ShowMain();
					MenuController.ui.SetStatus("newgame", "Cancelled. Use BEGIN or CANCEL in the game window.");
					SmpLog.Print("NEW GAME: back to the main list via ESC.");
				}
				return;
			}
			if (MenuController.ui.Target == MenuUi.View.Loading)
			{
				if (Input.GetKeyDown(KeyCode.Escape))
				{
					MenuController.CancelLoading();
				}
				return;
			}
			if (MenuController.ui.Target != MenuUi.View.Online)
			{
				return;
			}
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				if (MenuController.ui.FieldFocused)
				{
					MenuController.ui.UnfocusField();
					return;
				}
				MenuController.ui.ShowMain();
				return;
			}
			if (inputBlocked)
			{
				return;
			}
			if (!MenuController.ui.FieldFocused)
			{
				return;
			}
			if (Input.GetKeyDown(KeyCode.Backspace))
			{
				MenuController.ui.Backspace();
				return;
			}
			string typed = Input.inputString;
			if (typed.Length > 0)
			{
				MenuController.ui.Type(typed[typed.Length - 1]);
				return;
			}
			for (int i = 48; i <= 57; i++)
			{
				if (Input.GetKeyDown((KeyCode)i))
				{
					MenuController.ui.Type((char)(48 + (i - 48)));
					return;
				}
			}
			for (int j = 256; j <= 265; j++)
			{
				if (Input.GetKeyDown((KeyCode)j))
				{
					MenuController.ui.Type((char)(48 + (j - 256)));
					return;
				}
			}
		}

		// Token: 0x06000238 RID: 568 RVA: 0x0001625C File Offset: 0x0001445C
		private static void CancelLoading()
		{
			bool wasPending = SteamTransport.CancelPendingJoin();
			AutoLoad.Enabled = false;
			if (SteamTransport.IsHost)
			{
				MenuController.waitingForPeer = false;
				SteamTransport.Leave();
				MenuController.ui.SetHostLobbyId(0UL);
				MenuController.ui.ClearCode();
			}
			if (MenuController.ui.Target == MenuUi.View.Loading)
			{
				MenuController.ui.HideLoading();
			}
			MenuController.ui.ShowOnline();
			MenuController.ui.SetStatus("cancelled", wasPending ? "Cancelled. The lobby was not reached." : "Cancelled.");
			SmpLog.Warning("menu: cancelled while loading (wasPending=" + wasPending.ToString() + ").");
		}

		// Token: 0x06000239 RID: 569 RVA: 0x000162F8 File Offset: 0x000144F8
		[MethodImpl(MethodImplOptions.NoInlining)]
		private static string ClipboardText()
		{
			string text;
			try
			{
				TextEditor te = new TextEditor();
				te.content = new GUIContent(string.Empty);
				te.style = ((GUI.skin != null) ? GUI.skin.textField : GUI.skin.label);
				if (te.style == null)
				{
					text = string.Empty;
				}
				else if (!te.Paste())
				{
					text = string.Empty;
				}
				else
				{
					text = ((te.content == null) ? string.Empty : te.content.text);
				}
			}
			catch (Exception e)
			{
				SmpLog.Warning("clipboard read failed: " + e.GetType().Name);
				text = string.Empty;
			}
			return text;
		}

		// Token: 0x0600023A RID: 570 RVA: 0x000163B8 File Offset: 0x000145B8
		private static void HandlePasteHotkey()
		{
			if (!MenuController.ui.IsVisible || MenuController.ui.Target != MenuUi.View.Online)
			{
				return;
			}
			if (!MenuController.ui.FieldFocused)
			{
				return;
			}
			Event ev = Event.current;
			if (ev == null || ev.type != EventType.KeyDown)
			{
				return;
			}
			if (ev.keyCode != KeyCode.V || !ev.control)
			{
				return;
			}
			ev.Use();
			string text = MenuController.ClipboardText();
			if (text.Length == 0)
			{
				MenuController.ui.SetStatus("paste", "The clipboard is empty. Type the LOBBY ID by hand instead.");
				SmpLog.Warning("paste: nothing usable on the clipboard.");
				return;
			}
			MenuController.ui.Paste(text);
		}

		// Token: 0x0600023B RID: 571 RVA: 0x00016450 File Offset: 0x00014650
		public static void Draw()
		{
			MenuController.HandlePasteHotkey();
			MenuController.ui.Draw();
			try
			{
				InvitePanel.Draw();
			}
			catch (Exception e)
			{
				SmpLog.Warning("invite draw failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		// NEW GAME kaldirildi: bu metot artik yalnizca gizleme listesini
		// tazeler. Oyunun kendi butonlariyla ilgili bir kesif yapilmaz.
		public static void Rescan()
		{
			SmpLog.Print("menu: rescanning the scene and re-applying the hide list.");
			MenuController.applied = false;
			MenuController.ApplyHide();
		}

		// Token: 0x0600023F RID: 575 RVA: 0x000167E4 File Offset: 0x000149E4
		internal static string PickEvent(List<string> declared)
		{
			if (declared == null)
			{
				return null;
			}
			int at = MenuController.IndexOfIgnoreCase(declared, "ENTER");
			if (at >= 0)
			{
				return declared[at];
			}
			string[] preferred = new string[]
			{
				"MOUSE UP", "MOUSEUP", "CLICK", "ONCLICK", "SUBMIT", "SELECT", "CONFIRM", "OKAY", "PROCEED", "GO",
				"YES", "PRESS", "MOUSE DOWN", "MOUSEDOWN"
			};
			for (int i = 0; i < preferred.Length; i++)
			{
				at = MenuController.IndexOfIgnoreCase(declared, preferred[i]);
				if (at >= 0)
				{
					return declared[at];
				}
			}
			for (int j = 0; j < declared.Count; j++)
			{
				if (!MenuController.IsPlayMakerReserved(declared[j]))
				{
					return declared[j];
				}
			}
			return null;
		}

		// Token: 0x06000240 RID: 576 RVA: 0x000168D8 File Offset: 0x00014AD8
		internal static bool IsPlayMakerReserved(string name)
		{
			if (name != null)
			{
				switch (name.Length)
				{
				case 4:
				{
					char c = name[0];
					if (c != 'F')
					{
						if (c != 'S')
						{
							return false;
						}
						if (!(name == "STOP"))
						{
							return false;
						}
					}
					else if (!(name == "FAIL"))
					{
						return false;
					}
					break;
				}
				case 5:
				{
					char c2 = name[0];
					if (c2 != 'A')
					{
						if (c2 != 'R')
						{
							return false;
						}
						if (!(name == "RESET"))
						{
							return false;
						}
					}
					else if (!(name == "ABORT"))
					{
						return false;
					}
					break;
				}
				case 6:
				{
					char c3 = name[0];
					if (c3 != 'C')
					{
						if (c3 != 'R')
						{
							if (c3 != 'U')
							{
								return false;
							}
							if (!(name == "UPDATE"))
							{
								return false;
							}
						}
						else if (!(name == "REPEAT"))
						{
							return false;
						}
					}
					else if (!(name == "CANCEL"))
					{
						return false;
					}
					break;
				}
				case 7:
				{
					char c4 = name[0];
					if (c4 != 'P')
					{
						if (c4 != 'S')
						{
							return false;
						}
						if (!(name == "SUCCESS"))
						{
							return false;
						}
					}
					else if (!(name == "PROCEED"))
					{
						return false;
					}
					break;
				}
				case 8:
					if (!(name == "FINISHED"))
					{
						return false;
					}
					break;
				case 9:
				case 10:
				case 11:
				case 12:
				case 13:
					return false;
				case 14:
					if (!(name == "FINISHEDONHALT"))
					{
						return false;
					}
					break;
				default:
					return false;
				}
				return true;
			}
			return false;
		}

		// Token: 0x06000241 RID: 577 RVA: 0x00016A56 File Offset: 0x00014C56
		internal static bool ContainsIgnoreCase(List<string> list, string name)
		{
			return MenuController.IndexOfIgnoreCase(list, name) >= 0;
		}

		// Token: 0x06000242 RID: 578 RVA: 0x00016A68 File Offset: 0x00014C68
		internal static int IndexOfIgnoreCase(List<string> list, string name)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}
			return -1;
		}

		// Token: 0x06000249 RID: 585 RVA: 0x00016EC8 File Offset: 0x000150C8
		private static void ApplyHide()
		{
			if (MenuController.applied)
			{
				return;
			}
			MenuController.applied = true;
			MenuController.hiddenObjects.Clear();
			MenuController.hiddenRenderers.Clear();
			MenuController.hiddenColliders.Clear();
			MenuController.hiddenCount = 0;
			// NEW GAME artik HiddenNames icinde: HideByName onu da kapatir.
			MenuController.HideByName(MenuController.HiddenNames);
			MenuController.HideByName(MenuController.HiddenModelNames);
			SmpLog.Print(string.Concat(new string[]
			{
				"menu: hid ",
				MenuController.hiddenCount.ToString(),
				" object(s), ",
				MenuController.hiddenRenderers.Count.ToString(),
				" renderer(s)."
			}));
		}

		// Token: 0x0600024A RID: 586 RVA: 0x00016F98 File Offset: 0x00015198
		private static void HideByName(List<string> names)
		{
			for (int i = 0; i < names.Count; i++)
			{
				GameObject go = MenuController.FindInScene(names[i]);
				if (go != null)
				{
					MenuController.Hide(go);
				}
			}
		}

		// Token: 0x0600024B RID: 587 RVA: 0x00016FD4 File Offset: 0x000151D4
		private static void Hide(GameObject go)
		{
			if (go == null || !go.activeSelf)
			{
				return;
			}
			string cameraName = MenuController.FirstActiveCameraName(go);
			if (!MenuFsm.CanHide(go.name, cameraName))
			{
				if (Telemetry.Verbose)
				{
					SmpLog.Log(string.Concat(new string[] { "menu: '", go.name, "' carries camera '", cameraName, "'; NOT hidden (the game's buttons need it)." }));
				}
				return;
			}
			MenuController.hiddenObjects.Add(go);
			go.SetActive(false);
			MenuController.hiddenCount++;
		}

		// Token: 0x0600024C RID: 588 RVA: 0x00017064 File Offset: 0x00015264
		private static string FirstActiveCameraName(GameObject go)
		{
			try
			{
				Camera[] cams = go.GetComponentsInChildren<Camera>(true);
				for (int i = 0; i < cams.Length; i++)
				{
					if (!(cams[i] == null) && cams[i].enabled)
					{
						string j = cams[i].name;
						return string.IsNullOrEmpty(j) ? "(unnamed)" : j;
					}
				}
			}
			catch
			{
			}
			return null;
		}

		// Token: 0x0600024D RID: 589 RVA: 0x000170D0 File Offset: 0x000152D0
		private static void HideVisually(GameObject go)
		{
			if (go == null)
			{
				return;
			}
			foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
			{
				if (!(r == null) && r.enabled)
				{
					r.enabled = false;
					MenuController.hiddenRenderers.Add(r);
				}
			}
			foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
			{
				if (!(c == null) && c.enabled)
				{
					c.enabled = false;
					MenuController.hiddenColliders.Add(c);
				}
			}
			MenuController.hiddenCount++;
		}

		// Token: 0x0600024E RID: 590 RVA: 0x00017178 File Offset: 0x00015378
		private static void Restore()
		{
			for (int i = 0; i < MenuController.hiddenObjects.Count; i++)
			{
				GameObject go = MenuController.hiddenObjects[i];
				if (go != null)
				{
					go.SetActive(true);
				}
			}
			for (int j = 0; j < MenuController.hiddenRenderers.Count; j++)
			{
				Renderer r = MenuController.hiddenRenderers[j];
				if (r != null)
				{
					r.enabled = true;
				}
			}
			for (int k = 0; k < MenuController.hiddenColliders.Count; k++)
			{
				Collider c = MenuController.hiddenColliders[k];
				if (c != null)
				{
					c.enabled = true;
				}
			}
			MenuController.hiddenObjects.Clear();
			MenuController.hiddenRenderers.Clear();
			MenuController.hiddenColliders.Clear();
			MenuController.hiddenCount = 0;
			MenuController.newGameObject = null;
			MenuController.newGameFsm = null;
			MenuController.newGameEvent = null;
			MenuController.confirmState = 0;
			MenuController.confirmButtons.Clear();
			MenuController.confirmPanel = null;
			MenuController.applied = false;
		}

		// Token: 0x06000250 RID: 592 RVA: 0x00017300 File Offset: 0x00015500
		public static GameObject FindInScene(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return null;
			}
			try
			{
				GameObject direct = GameObject.Find(name);
				if (direct != null)
				{
					return direct;
				}
			}
			catch
			{
			}
			GameObject root = SceneDump.ActiveSceneRoot();
			if (root == null)
			{
				return null;
			}
			Transform[] all = root.GetComponentsInChildren<Transform>(true);
			for (int i = 0; i < all.Length; i++)
			{
				if (all[i].name == name)
				{
					return all[i].gameObject;
				}
			}
			return null;
		}

		// Token: 0x06000251 RID: 593 RVA: 0x0001738C File Offset: 0x0001558C
		public static GameObject FindContaining(string fragment)
		{
			GameObject root = SceneDump.ActiveSceneRoot();
			if (root == null)
			{
				return null;
			}
			Transform[] all = root.GetComponentsInChildren<Transform>(true);
			for (int i = 0; i < all.Length; i++)
			{
				if (all[i].name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return all[i].gameObject;
				}
			}
			return null;
		}

		// Token: 0x06000252 RID: 594 RVA: 0x000173DC File Offset: 0x000155DC
		public static void OpenLobby()
		{
			if (!SteamTransport.SteamReady)
			{
				MenuController.ui.SetStatus("nosteam", "Steam is not running. Start Steam, then restart the game.");
				SmpLog.Error("cannot host: Steam is not initialised.");
				return;
			}
			string code = LobbyCode.Generate();
			MenuController.ui.SetHostCode(code);
			MenuController.ui.SetStatus("hosting", "Opening the lobby...");
			MenuController.ui.ShowLoading("Opening the lobby...");
			MenuController.waitingForPeer = true;
			MenuController.hostDeadline = Time.time + 15f;
			SteamTransport.Host();
			SmpLog.Print("HOSTING. Session code: " + code);
		}

		// Token: 0x06000253 RID: 595 RVA: 0x00017470 File Offset: 0x00015670
		public static void OnLobbyCreated()
		{
			try
			{
				if (SteamTransport.LobbyId.IsValid())
				{
					ulong id = SteamTransport.LobbyId.m_SteamID;
					MenuController.ui.SetHostLobbyId(id);
					InvitePanel.SetSession(LobbyCode.Current, id);
					if (MenuController.waitingForPeer)
					{
						MenuController.waitingForPeer = false;
						SmpLog.Print("peer wait skipped; entering the world with the lobby up.");
						AutoLoad.EnterWorld();
					}
				}
			}
			catch (Exception e)
			{
				SmpLog.Warning("lobby id could not be shown: " + e.Message);
			}
		}

		// Token: 0x06000254 RID: 596 RVA: 0x000174F4 File Offset: 0x000156F4
		public static void JoinLobby()
		{
			if (!SteamTransport.SteamReady)
			{
				MenuController.ui.SetStatus("nosteam", "Steam is not running. Start Steam, then restart the game.");
				SmpLog.Error("cannot join: Steam is not initialised.");
				return;
			}
			if (!MenuController.ui.CodeComplete)
			{
				MenuController.ui.SetStatus("short", "Paste the host's LOBBY ID, or type the " + 6.ToString() + " character lobby code.");
				SmpLog.Warning(string.Concat(new string[]
				{
					"join refused: ",
					MenuController.ui.TypedLength.ToString(),
					" character(s) typed; need a lobby id or ",
					6.ToString(),
					" code characters."
				}));
				return;
			}
			MenuController.ui.UnfocusField();
			AutoLoad.Enabled = true;
			if (!MenuController.ui.LooksLikeLobbyId)
			{
				SteamTransport.PendingCode = LobbyCode.Normalize(MenuController.ui.TypedCode);
				MenuController.ui.SetStatus("joining", "Looking for the host's lobby...");
				MenuController.ui.ShowLoading("Looking for the host's lobby...");
				SteamTransport.JoinAuto();
				SmpLog.Print("JOINING by discovery. Lobby code '" + LobbyCode.Normalize(MenuController.ui.TypedCode) + "' | auto world entry ON.");
				return;
			}
			ulong lobbyId;
			if (!ulong.TryParse(MenuController.ui.TypedCode, out lobbyId) || lobbyId == 0UL)
			{
				MenuController.ui.SetStatus("short", "That lobby id is not a valid number.");
				SmpLog.Error("join refused: '" + MenuController.ui.TypedCode + "' is not a lobby id.");
				return;
			}
			MenuController.ui.SetStatus("joining", "Connecting to lobby " + lobbyId.ToString() + "...");
			MenuController.ui.ShowLoading("Connecting to lobby " + lobbyId.ToString() + "...");
			SteamTransport.Join(lobbyId);
			SmpLog.Print("JOINING by lobby id " + lobbyId.ToString() + " | auto world entry ON.");
		}

		// Token: 0x040001B6 RID: 438
		private static MenuUi ui = new MenuUi();

		// Token: 0x040001B7 RID: 439
		public const string NewGameButtonName = "ButtonNewgame";

		// Token: 0x040001B8 RID: 440
		public static readonly List<string> HiddenNames = new List<string>(new string[]
		{
			"ButtonContinue", "ButtonLoad", "ButtonDelete", "ButtonCopy", "ButtonRename", "ButtonSettings", "ButtonExit", "ButtonCredits", "ButtonBack", "ButtonOptions",
			"ButtonQuit", "ButtonChangelog", "ContinueButton", "LoadButton", "DeleteButton", "SettingsButton", "ExitButton", "DeathButton", "LoadingRadio", "LoadingCD",
			"Loading", "CreditsTV", "creditsCAM", "LogGFX", "ColorButtonsNew", "OptionsMainMenuMute", "ButtonNewgame", "ButtonNewGame"
		});

		// Token: 0x040001B9 RID: 441
		public static readonly List<string> NewGameDialogNames = new List<string>(new string[] { "ButtonBegin", "ButtonCancel" });

		// Token: 0x040001BA RID: 442
		public static readonly List<string> HiddenModelNames = new List<string>(new string[]
		{
			"MenuCar", "Car", "GFX", "GFXShadow", "BG 1", "BG 2", "BG 3", "BG 4", "BG", "SHOWROOM",
			"Showroom", "GARAGE", "Garage", "MENU_3D", "Menu3D", "CameraMenu", "MenuCamera", "Rotator", "Spin", "Turntable"
		});

		// Token: 0x040001BB RID: 443
		private static PlayMakerFSM newGameFsm;

		// Token: 0x040001BC RID: 444
		private static GameObject newGameObject;

		// Token: 0x040001BD RID: 445
		private static string newGameEvent;

		// Token: 0x040001BE RID: 446
		private static List<string> candidates = new List<string>();

		// Token: 0x040001BF RID: 447
		private static bool inMenu;

		// Token: 0x040001C0 RID: 448
		private static int hiddenCount;

		// Token: 0x040001C1 RID: 449
		private static readonly List<GameObject> hiddenObjects = new List<GameObject>(16);

		// Token: 0x040001C2 RID: 450
		private static readonly List<Renderer> hiddenRenderers = new List<Renderer>(16);

		// Token: 0x040001C3 RID: 451
		private static readonly List<Collider> hiddenColliders = new List<Collider>(16);

		// Token: 0x040001C4 RID: 452
		private static bool applied;

		// Token: 0x040001C5 RID: 453
		private static float lastSceneCheck;

		// Token: 0x040001C6 RID: 454
		private static GameObject confirmPanel;

		// Token: 0x040001C7 RID: 455
		private static readonly List<GameObject> confirmButtons = new List<GameObject>(4);

		// Token: 0x040001C8 RID: 456
		private static int confirmState;

		// Token: 0x040001C9 RID: 457
		private static float confirmDeadline;

		// Token: 0x040001CA RID: 458
		private const float CONFIRM_TIMEOUT = 45f;

		// Token: 0x040001CB RID: 459
		private const float LOBBY_TIMEOUT = 15f;

		// Token: 0x040001CC RID: 460
		private static float hostDeadline;

		// Token: 0x040001CD RID: 461
		private static bool waitingForPeer;

	}
}
