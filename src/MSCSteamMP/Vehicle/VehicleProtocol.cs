using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Switches that can be toggled from inside a vehicle.
	///
	/// Values are persisted in save files and travel over the wire, so existing entries
	/// must keep their numeric value. Append new ones at the end only.
	/// </summary>
	public enum VehicleSwitch {
		HandbrakePull = 0,
		HandbrakeLever = 1,
		Lights = 2,
		Wipers = 3,
		HydraulicPump = 4,
		DiffLock = 5,
		AxleLift = 6,
		InteriorLight = 7,
		SpillValve = 8,
		FuelTap = 9,
		TractorHydraulics = 10,
		DestroyWaspNest = 11,
		FlatbedHatch = 12,
		/// <summary>Gear range selector (low / high).</summary>
		Range = 13,
		/// <summary>
		/// Pencere (sürücü kapısı camı) — taşınan tek değer, camın ilgili
		/// kapı pivotuna göre yerel y konumu. Hayosiko'da pencere kumandayı
		/// çevirdikçe YAVAŞÇA iniyor; yani durum 1/2 değil, analog konum.
		/// </summary>
		Window = 14,
	}

	/// <summary>
	/// Starter FSM states, mirrored from the old MSCMP <c>PlayerVehicle.EngineStates</c>.
	/// Numeric values are wire format.
	/// </summary>
	public enum EngineState {
		WaitForStart = 0,
		ACC = 1,
		Glowplug = 2,
		TurnKey = 3,
		CheckClutch = 4,
		StartingEngine = 5,
		StartEngine = 6,
		StartOrNot = 7,
		MotorRunning = 8,
		Wait = 9,
		Null = 10,
		// --- v15: gercek Starter durum adlari (yalniz SONA eklenir) ---
		/// <summary>"Crank up" - mars sirasinda.</summary>
		Cranking = 11,
		/// <summary>"ACC off" - anahtar cikarildi.</summary>
		AccOff = 12,
		/// <summary>"Stall engine".</summary>
		Stalled = 13,
		/// <summary>"Motor installed" - motor takili degil.</summary>
		MotorNotInstalled = 14,
		/// <summary>"Electricity OK" - elektrik on kontrolu.</summary>
		ElectricityOk = 15,
		/// <summary>"Broken starter".</summary>
		Broken = 16,
	}

	/// <summary>
	/// Dashboard / ignition FSM states, mirrored from the old
	/// <c>PlayerVehicle.DashboardStates</c>. Numeric values are wire format.
	/// </summary>
	public enum DashboardState {
		ACCon = 0,
		Test = 1,
		ACCon2 = 2,
		MotorStarting = 3,
		ShutOff = 4,
		MotorOff = 5,
		WaitButton = 6,
		WaitPlayer = 7,
		Null = 8,
	}

	/// <summary>
	/// Wire event names. Prefixed so they cannot collide with another mod's events.
	/// </summary>
	public static class EventNames {
		/// <summary>
		/// One packet per frame carrying every state, switch and engine change made in
		/// that frame. Replaces the previous one-event-per-change scheme.
		/// </summary>
		public const string Batch = "wmpvs_batch";

		/// <summary>Host to joining player: vehicleId, engine, dash, fuel.</summary>
		public const string FullState = "wmpvs_fullstate";
	}

	public static class Wire {

		/// <summary>
		/// Deterministic FNV-1a hash.
		///
		/// Deliberately not <see cref="string.GetHashCode"/>: vehicle identity has to be
		/// identical on every client, and the BCL string hash is not contractually stable
		/// across runtimes (randomised on .NET Core, differs between Mono builds). If the
		/// host and a client disagree on a vehicle id, every switch event lands on the
		/// wrong car.
		/// </summary>
		public static int StableHash(string value) {
			if (string.IsNullOrEmpty(value)) {
				return 0;
			}
			unchecked {
				uint hash = 2166136261;
				for (int i = 0; i < value.Length; i++) {
					hash ^= value[i];
					hash *= 16777619;
				}
				return (int)hash;
			}
		}

		/// <summary>
		/// Full transform path of a vehicle root, used to build a stable id.
		/// </summary>
		public static string PathOf(UnityEngine.Transform t) {
			if (t == null) {
				return string.Empty;
			}
			StringBuilder sb = new StringBuilder();
			Stack<string> parts = new Stack<string>();
			Transform cur = t;
			while (cur != null) {
				parts.Push(cur.name);
				cur = cur.parent;
			}
			while (parts.Count > 0) {
				if (sb.Length > 0) {
					sb.Append('/');
				}
				sb.Append(parts.Pop());
			}
			return sb.ToString();
		}
	}
}

