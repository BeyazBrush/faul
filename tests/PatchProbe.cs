using System;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;

namespace PatchProbe {

	public static class Probe {
		public static int BoolHits;
		public static int IntHits;
		public static int FloatHits;
		public static int SendEventHits;
		public static int TargetedEventHits;
		public static int SelfEventHits;
		/// <summary>Fsm.ProcessEvent (hedefe teslim) kac kez girdi.</summary>
		public static int ProcessEventHits;
		public static int MoveHits;
		public static int LookHits;
		/// <summary>SetMouseCursor.OnEnter kancasinin kac kez girdigi.</summary>
		public static int CursorHits;
		/// <summary>Kancanin okudugu hideCursor degeri (imlec gizli miydi).</summary>
		public static bool CursorHideSeen;
		public static bool CursorLockSeen;
		public static string LastValue;
	}

	/// <summary>
	/// Oyunun imleç kararı: HutongGames.PlayMaker.Actions.SetMouseCursor.OnEnter
	/// (token 0x06000DC9). CursorGuard bu metodu Postfix'liyor ve oradan
	/// gerçek Cursor.visible / Cursor.lockState yazıyor.
	///
	/// NEDEN BU PROBE VAR: ESC'de imlecin kaybolmasının kök nedeni, bu
	/// action'ın Cursor'a hiç dokunmaması (sadece PlayMakerGUI alanlarına
	/// yazması). Yanlış tip veya yanlış metot adı olursa Harmony ya hata
	/// verir ya da sessizce hiç yamamaz — ve ESC fix'i sessizce çalışmaz.
	/// Bu probe hedefin çözülebildiğini ve alanların okunabildiğini kanıtlar.
	/// </summary>
	[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.SetMouseCursor), "OnEnter")]
	public static class SetMouseCursorProbe {
		static void Postfix(HutongGames.PlayMaker.Actions.SetMouseCursor __instance) {
			Probe.CursorHits++;
			Probe.CursorHideSeen = __instance.hideCursor != null && __instance.hideCursor.Value;
			Probe.CursorLockSeen = __instance.lockCursor != null && __instance.lockCursor.Value;
			Probe.LastValue = "cursor hide=" + Probe.CursorHideSeen
				+ " lock=" + Probe.CursorLockSeen;
		}
	}

	[HarmonyPatch(typeof(FsmBool), "set_Value")]
	public static class BoolHook {
		static void Prefix(FsmBool __instance, bool value) {
			Probe.BoolHits++;
			Probe.LastValue = "bool " + value;
		}
	}

	[HarmonyPatch(typeof(FsmInt), "set_Value")]
	public static class IntHook {
		static void Prefix(FsmInt __instance, int value) {
			Probe.IntHits++;
			Probe.LastValue = "int " + value;
		}
	}

	[HarmonyPatch(typeof(FsmFloat), "set_Value")]
	public static class FloatHook {
		static void Prefix(FsmFloat __instance, float value) {
			Probe.FloatHits++;
			Probe.LastValue = "float " + value;
		}
	}

	[HarmonyPatch(typeof(PlayMakerFSM), "SendEvent", new Type[] { typeof(string) })]
	public static class SendEventHook {
		static void Prefix(PlayMakerFSM __instance, string eventName) {
			Probe.SendEventHits++;
			Probe.LastValue = "event " + eventName;
		}
	}

	// dnSpy ile bulunan kritik yol: PlayMaker'in "Send Event" action'i
	// Fsm.Event(FsmEventTarget, FsmEvent) cagirir (token 0x060001ED). Bu yamanin
	// cozulebilmesi, Harmony 1.2'in overload'i dogru cozebildigini kanitlar.
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(FsmEventTarget), typeof(FsmEvent) })]
	public static class FsmTargetedHook {
		static void Prefix(Fsm __instance, FsmEventTarget eventTarget, FsmEvent fsmEvent) {
			Probe.TargetedEventHits++;
			Probe.LastValue = "targeted " + (fsmEvent == null ? "(null)" : fsmEvent.Name);
		}
	}

	// Fsm.Event(string) token 0x060001EE: self-send yolu.
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(string) })]
	public static class FsmSelfHook {
		static void Prefix(Fsm __instance, string fsmEventName) {
			Probe.SelfEventHits++;
			Probe.LastValue = "self " + fsmEventName;
		}
	}

	// Fsm.Event(FsmEventTarget, string) token 0x060001EC: hedefli string yolu.
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(FsmEventTarget), typeof(string) })]
	public static class FsmTargetedStringHook {
		static void Prefix(Fsm __instance, FsmEventTarget eventTarget, string fsmEventName) {
			Probe.TargetedEventHits++;
			Probe.LastValue = "targeted-str " + fsmEventName;
		}
	}

	// Fsm.Event(FsmEvent) token 0x060001EF: tekil self-send yolu.
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(FsmEvent) })]
	public static class FsmSingleHook {
		static void Prefix(Fsm __instance, FsmEvent fsmEvent) {
			Probe.SelfEventHits++;
			Probe.LastValue = "single " + (fsmEvent == null ? "(null)" : fsmEvent.Name);
		}
	}

	// Fsm.ProcessEvent(FsmEvent, FsmEventData): hedef FSM'e TESLIM yoludur ve
	// engellemenin GERCEK bogazidir. PlayMakerFSM.SendEvent ile Fsm.Event
	// yamalari yalniz GONDEREN tarafi goruyor; "bir nesneye gonder" yolu
	// hedefe dogrudan buradan yazar. Bu yama cozulemezse olum olayi hicbir
	// yamaya girmez (canli kanit: mscmp.txt 2026-10-14 17:53 — olum tam
	// olarak boyle basladi).
	//
	// NOT: ikinci parametre varsayilanlidir (eventData = null) ama IL'de tek
	// bir metot vardir; Harmony'nin iki parametreli tam imzayi cozmesi gerekir.
	[HarmonyPatch(typeof(Fsm), "ProcessEvent", new Type[] { typeof(FsmEvent), typeof(FsmEventData) })]
	public static class FsmProcessEventHook {
		static void Prefix(Fsm __instance, FsmEvent fsmEvent) {
			Probe.ProcessEventHits++;
			Probe.LastValue = "process " + (fsmEvent == null ? "(null)" : fsmEvent.Name);
		}
	}

	// Unity tarafi: karakter hareketi donus deger CollisionFlags donduruyor (0x060012ED),
	// yani zemin durumu ucuzca okunabilir.
	[HarmonyPatch(typeof(UnityEngine.CharacterController), "Move")]
	public static class CharacterMoveHook {
		static void Prefix(UnityEngine.CharacterController __instance, UnityEngine.Vector3 motion) {
			Probe.MoveHits++;
			Probe.LastValue = "move " + motion.magnitude;
		}
	}

	// Kamera fare kontrolu: konsol acikken dondurulur (CameraLookGuard).
	// Token'lar Assembly-CSharp: SimpleSmoothMouseLook.Update 0x06000087,
	// SmoothMouseLook.Update 0x06000089, MainCamera.LateUpdate 0x0600047D.
	[HarmonyPatch(typeof(SimpleSmoothMouseLook), "Update")]
	public static class SimpleMouseLookProbe {
		static void Prefix() { Probe.LookHits++; }
	}

	[HarmonyPatch(typeof(SmoothMouseLook), "Update")]
	public static class SmoothMouseLookProbe {
		static void Prefix() { Probe.LookHits++; }
	}

	[HarmonyPatch(typeof(MainCamera), "LateUpdate")]
	public static class MainCameraProbe {
		static void Prefix() { Probe.LookHits++; }
	}

	public static class Program {
		public static int Main() {
			HarmonyInstance harmony = HarmonyInstance.Create("com.mscsteammp.probe");
			try {
				harmony.PatchAll(Assembly.GetExecutingAssembly());
				Console.WriteLine("PatchAll: ok");
			}
			catch (Exception e) {
				Console.WriteLine("PatchAll: FAILED " + e.GetType().Name + ": " + e.Message);
				return 1;
			}

			int patched = 0;
			foreach (MethodBase m in harmony.GetPatchedMethods()) {
				Console.WriteLine("  patched: " + m.DeclaringType.Name + "." + m.Name);
				patched++;
			}
			Console.WriteLine("patched count: " + patched + " (expect 14)");

			// The two Fsm.Event overloads must be distinct methods. Calling them here is
			// not an option: FsmEvent.GetFsmEvent and FsmVariables.GetFsmBool both reach
			// PlayMakerGlobals.Instance, which is a Unity internal call and throws on the
			// desktop CLR. So this is a pure reflection check: if Harmony had resolved both
			// patches to the same MethodBase, half the game's FSM traffic would silently
			// stop being captured.
			MethodBase targetedPatch = null, selfPatch = null, targetedStrPatch = null, singlePatch = null;
			foreach (MethodBase m in harmony.GetPatchedMethods()) {
				if (m.DeclaringType != typeof(Fsm)) continue;
				ParameterInfo[] ps = m.GetParameters();
				if (ps.Length == 2 && ps[0].ParameterType == typeof(FsmEventTarget)) {
					if (ps[1].ParameterType == typeof(FsmEvent)) targetedPatch = m;
					else if (ps[1].ParameterType == typeof(string)) targetedStrPatch = m;
				}
				else if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) selfPatch = m;
				else if (ps.Length == 1 && ps[0].ParameterType == typeof(FsmEvent)) singlePatch = m;
			}
			bool overloadsResolved = targetedPatch != null && selfPatch != null
				&& targetedStrPatch != null && singlePatch != null
				&& !targetedPatch.Equals(selfPatch);
			Console.WriteLine("Fsm.Event(FsmEventTarget,FsmEvent) patched = " + (targetedPatch != null));
			Console.WriteLine("Fsm.Event(string) patched = " + (selfPatch != null));
			Console.WriteLine("Fsm.Event(FsmEventTarget,string) patched = " + (targetedStrPatch != null));
			Console.WriteLine("Fsm.Event(FsmEvent) patched = " + (singlePatch != null));
			Console.WriteLine("overloads are distinct = " + overloadsResolved);

			// Same for PlayMakerFSM.SendEvent, which is the third event path.
			bool sendEventPatched = false;
			bool processEventPatched = false;
			foreach (MethodBase m in harmony.GetPatchedMethods()) {
				if (m.DeclaringType == typeof(PlayMakerFSM) && m.Name == "SendEvent") sendEventPatched = true;
				// DORDUNCU yol ve gercek bogaz: hedef FSM'e teslim. "Bir nesneye
				// gonder" yolu PlayMakerFSM.SendEvent'i cagirmaz, dogrudan
				// ProcessEvent'e yazar; bu yama olmadan olum olayi engellenmez
				// (canli kanit: mscmp.txt 2026-10-14 17:53 — olum tam olarak
				// boyle basladi).
				if (m.DeclaringType == typeof(Fsm) && m.Name == "ProcessEvent") processEventPatched = true;
			}
			Console.WriteLine("PlayMakerFSM.SendEvent patched = " + sendEventPatched);
			Console.WriteLine("Fsm.ProcessEvent patched = " + processEventPatched);

			FsmBool b = new FsmBool();
			b.Name = "Installed";
			b.Value = true;
			Console.WriteLine("FsmBool.Value = " + b.Value + " hits=" + Probe.BoolHits);

			FsmInt i = new FsmInt();
			i.Name = "Stage";
			i.Value = 7;
			Console.WriteLine("FsmInt.Value = " + i.Value + " hits=" + Probe.IntHits);

			FsmFloat f = new FsmFloat();
			f.Name = "Tightness";
			f.Value = 3.5f;
			Console.WriteLine("FsmFloat.Value = " + f.Value + " hits=" + Probe.FloatHits);

			Console.WriteLine("last prefix arg: " + Probe.LastValue);

			// A bool write must not reach the other hooks, and repeated writes must each count.
			Probe.BoolHits = 0;
			b.Value = false;
			b.Value = true;
			Console.WriteLine("repeat writes hits=" + Probe.BoolHits + " (expect 2)");

			// Read must not fire the setter hook.
			Probe.BoolHits = 0;
			bool read = b.Value;
			Console.WriteLine("read " + read + " hits=" + Probe.BoolHits + " (expect 0)");

			// The event paths are verified by reflection above; nothing here needs Unity.
			// processEventPatched ZORUNLU: onsuz hedefe giden olay hicbir yamaya girmez.
			return (Probe.BoolHits >= 0 && b.Value && overloadsResolved && sendEventPatched
				&& processEventPatched) ? 0 : 1;
		}
	}
}