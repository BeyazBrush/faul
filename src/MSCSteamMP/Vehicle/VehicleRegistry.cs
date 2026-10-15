using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MSCLoader;
using Steamworks;
using UnityEngine;
using HutongGames.PlayMaker;

namespace SteamMP {

	/// <summary>
	/// Finds drivable vehicles, owns SyncedVehicle components and routes packets.
	/// Discovery mirrors WreckMPVehicleSync: AxisCarController components, not names.
	/// </summary>
	public static class VehicleRegistry {

		public static readonly Dictionary<int, SyncedVehicle> Vehicles = new Dictionary<int, SyncedVehicle>();
		static readonly HashSet<string> knownRoots = new HashSet<string>(StringComparer.Ordinal);

		static readonly string[] KnownVehicleNames = {
			"JONNEZ ES(Clone)", "HAYOSIKO(1500kg, 250)", "SATSUMA(557kg, 248)",
			"RCO_RUSCKO12(270)", "KEKMET(350-400psi)", "FLATBED",
			"FERNDALE(1630kg)", "GIFU(750/450psi)"
		};

		// ------------------------------------------------------------------
		// Discovery
		// ------------------------------------------------------------------

		public static void Install() {
			Scan();
		}

		public static void Scan() {
			float t0 = Time.realtimeSinceStartup;
			AxisCarController[] controllers = Resources.FindObjectsOfTypeAll<AxisCarController>();
			HashSet<GameObject> roots = new HashSet<GameObject>();
			foreach (AxisCarController c in controllers) {
				if (c != null && c.gameObject != null) roots.Add(c.gameObject);
			}
			foreach (GameObject root in roots) RegisterVehicleRoot(root.transform);
			if (Telemetry.Verbose) SmpLog.Log("registered " + Vehicles.Count + " drivable vehicle(s) from "
				+ roots.Count + " controller root(s) in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F1") + " ms");
		}

		public static void Rescan() {
			foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
				if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
			}
			Vehicles.Clear();
			knownRoots.Clear();
			Scan();
		}

		static void RegisterVehicleRoot(Transform root) {
			if (root == null) return;
			string path = Wire.PathOf(root);
			if (!knownRoots.Add(path)) return;
			string rootName = root.name;

			if (root.GetComponent<Drivetrain>() == null || root.GetComponent<Rigidbody>() == null) {
				SmpLog.Warning("'" + rootName + "' has no drivetrain/rigidbody, skipping.");
				return;
			}
			SyncedVehicle sv = root.gameObject.AddComponent<SyncedVehicle>();
			int id = Wire.StableHash(rootName);
			if (Vehicles.ContainsKey(id)) {
				int fallback = Wire.StableHash(path);
				SmpLog.Warning("duplicate vehicle name '" + rootName + "', path id " + fallback + ".");
				id = fallback;
			}
			if (Vehicles.ContainsKey(id)) {
				SmpLog.Error("no unique id for '" + rootName + "', skipping.");
				UnityEngine.Object.Destroy(sv);
				return;
			}
			sv.Initialise(id, rootName);
			Vehicles.Add(id, sv);
			if (Telemetry.Verbose) SmpLog.Log("vehicle '" + rootName + "' = " + id
				+ (sv.Binder != null ? " (fsm states: " + CountStates(sv.Binder) + ")" : ""));
		}

		static int CountStates(FsmBinder b) {
			int n = 0;
			foreach (TrackedFsm t in b.Tracked) n += t.States.Count;
			return n;
		}

		// ------------------------------------------------------------------
		// Outgoing
		// ------------------------------------------------------------------

		public static void SendTransforms() {
			if (!SteamTransport.HasRemote) return;
			foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
				SyncedVehicle sv = kv.Value;
				if (sv == null || !sv.LocalIsDriver) continue;
				try {
					MemoryStream s = Protocol.Begin(Protocol.Msg.Transform);
					sv.WriteTransform(new BinaryWriter(s));
					// Fizik konumu: noDelay. Bir kare gecikme interpolatörde
					// yumuşatılır, güveniliz kanal ise kuyruk biriktirip titreme
					// yapıyordu.
					if (SteamTransport.SendUnreliableNoDelay(Protocol.Finish(s))) Telemetry.SentTransforms++;
				}
				catch { }
			}
		}

		public static void SendSwitch(int vehicleId, VehicleSwitch sw, bool value, float floatValue) {
			if (!SteamTransport.HasRemote) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.Switch);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(vehicleId); w.Write((byte)sw); w.Write(value); w.Write(floatValue);
				if (SteamTransport.SendReliable(Protocol.Finish(s))) Telemetry.SentSwitches++;
			}
			catch { }
		}

		public static void SendState(int vehicleId, int fsmIndex, string stateName) {
			if (!SteamTransport.HasRemote) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.State);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(vehicleId); w.Write(fsmIndex); w.Write(stateName != null ? stateName : "");
				if (SteamTransport.SendReliable(Protocol.Finish(s))) Telemetry.SentStates++;
			}
			catch { }
		}

		public static void SendEngine(int vehicleId, int engine, int dash, float startTime) {
			if (!SteamTransport.HasRemote) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.Engine);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(vehicleId); w.Write((byte)engine); w.Write((byte)dash); w.Write(startTime);
				if (SteamTransport.SendReliable(Protocol.Finish(s))) Telemetry.SentEngines++;
			}
			catch { }
		}

		/// <summary>Direksiyon simidi acisi (surucu, 10 Hz, unreliable).</summary>
		/// <summary>Gosterge ibresi paketi (surucu, 10 Hz, unreliable).</summary>
		public static void SendDashNeedle(int vehicleId, int[] hashes, Quaternion[] rots, int count) {
			if (!SteamTransport.HasRemote) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.DashNeedle);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(vehicleId);
				w.Write((byte)count);
				for (int i = 0; i < count; i++) {
					w.Write(hashes[i]);
					Quaternion q = rots[i];
					w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
				}
				SteamTransport.SendUnreliable(Protocol.Finish(s));
			}
			catch { }
		}

		public static void SendSteerWheel(int vehicleId, Quaternion pivot, Quaternion wheel) {
			if (!SteamTransport.HasRemote) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.SteerWheel);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(vehicleId);
				w.Write(pivot.x); w.Write(pivot.y); w.Write(pivot.z); w.Write(pivot.w);
				w.Write(wheel.x); w.Write(wheel.y); w.Write(wheel.z); w.Write(wheel.w);
				SteamTransport.SendUnreliable(Protocol.Finish(s));
			}
			catch { }
		}

		/// <summary>Araca giriş. isDriver=false ise karşı taraf kuklayı yolcu
		/// koltuğuna bindirir (protokol v11: koltuk rolü artık taşınıyor).</summary>
		public static void SendEnter(int vehicleId, bool isDriver) {
			if (!SteamTransport.HasRemote) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.EnterVehicle);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(vehicleId);
				w.Write((byte)(isDriver ? 0 : 1));
				SteamTransport.SendReliable(Protocol.Finish(s));
			}
			catch { }
		}

		public static void SendLeave(int vehicleId, bool isDriver) {
			if (!SteamTransport.HasRemote) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.LeaveVehicle);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(vehicleId);
				w.Write((byte)(isDriver ? 0 : 1));
				SteamTransport.SendReliable(Protocol.Finish(s));
			}
			catch { }
		}

		public static void SendAllFullState() {
			if (!SteamTransport.HasRemote) return;
			foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
				if (kv.Value == null) continue;
				try {
					MemoryStream s = Protocol.Begin(Protocol.Msg.FullState);
					kv.Value.WriteFullState(new BinaryWriter(s));
					if (SteamTransport.SendReliable(Protocol.Finish(s))) Telemetry.SentFullStates++;
				}
				catch { }
			}
		}

		public static void OnPeerTimeout() {
			foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
				if (kv.Value != null) kv.Value.ReleaseRemote();
			}
		}

		// ------------------------------------------------------------------
		// Membership queries
		// ------------------------------------------------------------------

		/// <summary>
		/// Bir nesne arac kökü mü: yani kendisi kayıtlı bir arac, ya da
		/// kayıtlı bir aracin altinda mi.
		///
		/// Interaction katmani (CarrySync/PartSync) bir parcayi "arac icinde mi"
		/// diye sorarken bunu kullanir. Kontrol, transformun kayitli arac
		/// koklerinden birine kadar yukselip yukselmedigi yapilarak yapilir;
		/// boylece parcanin tam yolunu (her Transform) bilmek gerekmez.
		///
		/// Kayitli arac yoksa (menu, ya da henuz Scan calismadi) FALSE doner:
		/// "arac icinde degil" demek, hatali bir arac yolu gondermekten iyi.
		/// </summary>
		public static bool IsVehicleRoot(GameObject go) {
			if (go == null) return false;
			try {
				Transform t = go.transform;
				int guard = 0;
				while (t != null && guard++ < 64) {
					if (IsRegisteredRoot(t)) return true;
					t = t.parent;
				}
			}
			catch { }
			return false;
		}

		static bool IsRegisteredRoot(Transform t) {
			if (t == null) return false;
			foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
				SyncedVehicle sv = kv.Value;
				if (sv == null) continue;
				Transform root = null;
				try { root = sv.transform; }
				catch { continue; }
				if (root == t) return true;
			}
			return false;
		}

		// ------------------------------------------------------------------
		// Incoming
		// ------------------------------------------------------------------

		public static void OnMessage(Protocol.Msg id, CSteamID sender, BinaryReader r) {
			try {
				switch (id) {
					case Protocol.Msg.Transform: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 4, "transform data"); // vehicle id + transform
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							Telemetry.ReceivedTransforms++;
							Telemetry.Log("transform vehicle " + vid);
							sv.ApplyTransform(r);
						}
						else { Telemetry.ReceivedUnknownVehicle++; }
						break;
					}
					case Protocol.Msg.Switch: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 4, "switch data"); // vehicle id + switch byte + bool + float
						VehicleSwitch sw = (VehicleSwitch)r.ReadByte();
						bool v = r.ReadBoolean();
						float f = r.ReadSingle();
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							Telemetry.ReceivedSwitches++;
							Telemetry.Log("switch vehicle " + vid + " " + sw + "=" + v + "/" + f);
							sv.ApplySwitch(sw, v, f);
						}
						else { Telemetry.ReceivedUnknownVehicle++; }
						break;
					}
					case Protocol.Msg.State: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 4, "state data"); // vehicle id + fsm index + state name length
						int fsm = r.ReadInt32();
						string name = r.ReadString();
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							Telemetry.ReceivedStates++;
							Telemetry.Log("state vehicle " + vid + " fsm" + fsm + " -> '" + name + "'");
							sv.ApplyState(fsm, name);
						}
						else { Telemetry.ReceivedUnknownVehicle++; }
						break;
					}
					case Protocol.Msg.Engine: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 4, "engine data"); // vehicle id + engine + dash + startTime
						int engine = r.ReadByte();
						int dash = r.ReadByte();
						float st = r.ReadSingle();
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							Telemetry.ReceivedEngines++;
							Telemetry.Log("engine vehicle " + vid + " " + engine + "/" + dash);
							sv.ApplyEngine(engine, dash, st);
						}
						else { Telemetry.ReceivedUnknownVehicle++; }
						break;
					}
					case Protocol.Msg.FullState: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 4, "fullstate data"); // vehicle id + engine + dash + fuel
						int engine = r.ReadInt32();
						int dash = r.ReadInt32();
						float fuel = r.ReadSingle();
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							Telemetry.ReceivedFullStates++;
							Telemetry.Log("fullstate vehicle " + vid);
							sv.ApplyFullState(engine, dash, fuel);
						}
						else { Telemetry.ReceivedUnknownVehicle++; }
						break;
					}
						case Protocol.Msg.EnterVehicle: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 1, "entervehicle data"); // vehicle id + seat role byte
						// v11: koltuk rolü. 0 = sürücü, 1 = yolcu. Koltuğu
						// bulamayan eski paketlerde okuma taşar; sürüm
						// eşleşmesi bunu zaten eliyor.
						bool peerIsDriver = r.ReadByte() == 0;
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							sv.SetRemoteSeated(true, peerIsDriver);
							Telemetry.Log("peer " + (peerIsDriver ? "drives" : "rides in") + " vehicle " + vid);
							// İki taraf da aynı koltuğu talep ediyorsa (biri
							// sürücü, ikimiz de sürücüysek) çakışma olur.
							if (peerIsDriver && sv.LocalIsDriver)
								SmpLog.Warning("both of you drive '" + sv.VehicleName + "' - jitter expected.");
						}
						break;
					}
					case Protocol.Msg.LeaveVehicle: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 1, "leavevehicle data"); // vehicle id + seat role byte
						r.ReadByte();
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							sv.SetRemoteSeated(false);
							Telemetry.Log("peer left vehicle " + vid);
						}
						break;
					}
					case Protocol.Msg.DashNeedle: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 1, "dashtable data"); // vehicle id + count
						int n = r.ReadByte();
						if (n < 0 || n > 8) break;
						int[] hs = new int[n];
						Quaternion[] qs = new Quaternion[n];
						for (int i = 0; i < n; i++) {
							Protocol.AssertBytesRemaining(r, 8, "dash needle data"); // 4 bytes quat + 4 bytes per int
							hs[i] = r.ReadInt32();
							qs[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
						}
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							sv.ApplyDashNeedle(hs, qs);
						}
						else { Telemetry.ReceivedUnknownVehicle++; }
						break;
					}
					case Protocol.Msg.SteerWheel: {
						int vid = r.ReadInt32();
						Protocol.AssertBytesRemaining(r, 8, "steerwheel data"); // 2 quaternions = 8 floats
						Quaternion pq = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
						Quaternion wq = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
						SyncedVehicle sv;
						if (Vehicles.TryGetValue(vid, out sv) && sv != null) {
							sv.ApplySteerWheel(pq, wq);
						}
						else { Telemetry.ReceivedUnknownVehicle++; }
						break;
					}
				}
			}
			catch { Telemetry.BadPackets++; }
		}

		/// <summary>
		/// Uzaktaki govde yeni olustugunda cagrilir: onceden arac bosluklarinda
		/// RemoteDriven isaretli araclar varsa kukla hemen koltuga baglanir.
		///
		/// Neden gerekli: EnterVehicle paketi govdeden ONCE gelirse (ki normalde
		/// boyledir, cunku kukla ilk PlayerPos'ta dogar) AttachPeer govdesiz
		/// oldugu icin isini yapip cikiyordu. Govde dogduktan sonra kimse
		/// tekrar denemedigi icin kukla arac ici disinda asili kalirdi.
		/// </summary>
		/// <summary>
		/// Yerel oyuncunun bulundugu arac: surucu VEYA yolcu.
		///
		/// Neden ayri fonksiyon: FindLocalVehicle() yalnizca LocalIsDriver
		/// olan araci donduruyor. Hayosiko gibi araclarda yolcu koltuguna
		/// oturunca o arac null gorunuyordu ve Discord presence 'Yuruyor'de
		/// kaliyordu. Simdi her ikisini de kapsiyor.
		/// </summary>
		public static SyncedVehicle FindLocalOccupant() {
			try {
				foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
					SyncedVehicle sv = kv.Value;
					if (sv != null && (sv.LocalIsDriver || sv.LocalIsPassenger)) return sv;
				}
			}
			catch { }
			return null;
		}

		/// <summary>
		/// Yerel oyuncunun sürdüğü araç (yoksa null). Test botu, oyuncu
		/// araca binince yolcu koltuğuna oturmak için bunu sorar.
		/// </summary>
		public static SyncedVehicle FindLocalVehicle() {
			try {
				foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
					SyncedVehicle sv = kv.Value;
					if (sv != null && sv.LocalIsDriver) return sv;
				}
			}
			catch { }
			return null;
		}

		/// <summary>
		/// Peer'in sürdüğü TEK araç (yoksa ya da birden çoksa null).
		/// WheelState araç kimliği taşımaz; devir sesi yalnızca tek sürücü
		/// varken o araca yazılır (yoksa yanlış araba yanlış devirle öter).
		/// </summary>
		public static SyncedVehicle GetSingleRemoteDriven() {
			// Kare başına bir kez tara (her araç Update'ten sorar; sözlük
			// turu karede 8 kez olmasın).
			int frame = 0;
			try { frame = Time.frameCount; } catch { frame = 0; }
			if (frame == lastSingleFrame && frame != 0) return cachedSingle;
			lastSingleFrame = frame;
			cachedSingle = null;
			try {
				foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
					SyncedVehicle sv = kv.Value;
					if (sv == null || !sv.RemoteDriven || sv.LocalIsDriver) continue;
					if (cachedSingle != null) { cachedSingle = null; return null; }
					cachedSingle = sv;
				}
			}
			catch { cachedSingle = null; }
			return cachedSingle;
		}

		static int lastSingleFrame = -1;
		static SyncedVehicle cachedSingle;

		/// <summary>FSM bu durumu gerÃ§ekten tanÄ±mlÄ±yor mu?</summary>
		static bool HasState(PlayMakerFSM fsm, string state) { return FindState(fsm, state) != null; }

		/// <summary>
		/// FSM'i verilen duruma ZORLA al.
		///
		/// dnSpy kanÄ±tÄ± (PlayMaker.dll, HutongGames.PlayMaker.Fsm):
		///   public  void  UpdateStateChanges()        0x060001F8
		///   private void  SwitchState(FsmState)      0x060001FA   â† HEDEF
		///   private void  EnterState(FsmState)       0x060001FC
		///   public  string get_ActiveStateName()     0x06000185
		///   private void  set_ActiveState(...)       0x06000184  (private!)
		///
		/// Yani her ikisi de private; dÄ±ÅŸarÄ±dan Ã§aÄŸrÄ±labilir tek yol
		/// yansÄ±ma. `EnterState` de private olsa da `SwitchState` onu
		/// Ã§aÄŸÄ±rÄ±yor, dolayÄ±sÄ±yla giriÅŸ aksiyonlarÄ± (PlayAnimation,
		/// ActivateGameObject, SetParent) oyunun kendi koduyla Ã§alÄ±ÅŸÄ±r â€”
		/// yani CD player kapaÄŸÄ± AYNILAR.
		/// </summary>
		static System.Reflection.MethodInfo switchStateMi;

		internal static bool ForceState(PlayMakerFSM fsm, string state) {
			try {
				if (switchStateMi == null) {
					switchStateMi = typeof(Fsm).GetMethod("SwitchState",
						System.Reflection.BindingFlags.NonPublic
						| System.Reflection.BindingFlags.Instance);
				}
				if (switchStateMi == null) return false;
				FsmState target = FindState(fsm, state);
				if (target == null) return false;
				switchStateMi.Invoke(fsm.Fsm, new object[] { target });
				return true;
			}
			catch (Exception e) {
				if (Telemetry.Verbose)
					SmpLog.Warning("control state force failed: " + e.GetType().Name);
				return false;
			}
		}

		/// <summary>Adla durum nesnesi.</summary>
		static FsmState FindState(PlayMakerFSM fsm, string state) {
			try {
				FsmState[] all = fsm.Fsm.States;
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] != null && string.Equals(all[i].Name, state, StringComparison.Ordinal)) return all[i];
				}
			}
			catch { }
			return null;
		}

		public static void ReattachPeerBody() {
			foreach (KeyValuePair<int, SyncedVehicle> kv in Vehicles) {
				SyncedVehicle sv = kv.Value;
				if (sv == null || !sv.RemoteDriven) continue;
				sv.AttachPeer(true);
			}
		}

		/// <summary>
		/// Motor/kontak hattı BURAYA DEĞİL, `WorldObjectSync`'e aittir.
		///
		/// Canlı hat `Msg.CtrlState` ("Wait player &lt;FSM&gt; &lt;OLAY&gt;")'tir:
		/// `STATE_MIRROR_FSMS` (bkz. WorldObjectSync, `IgnitionSatsuma` /
		/// `Starter` / `Pushstart` dahil) olayı `Wait player` durumuna yazar,
		/// karşı taraf `OnCtrlStateMessage` ile kendi FSM'ine yeniden oynatır.
		/// `Msg.EngineState` (82) ise ALICI-ONLY bir kimliktir: hiçbir yer
		/// onu göndermez, dolayısıyla kullanılmıyor.
		///
		/// Buradaki eski kopyalar (OnEngineStateMessage / HasVehicle) bu
		/// yüzden silindi: aynı işi ikinci kez yapan ve çağrılmayan bir
		/// alıcı, ileride hangi yolun gerçekten çalıştığını gizliyordu.
		/// </summary>
	}
}
