using System;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// AKÜ DÖKÜMÜ ENGELİ — `SubtractFsmFloat` hook'u.
	///
	/// KULLANICI ÖNERİSİ (birebir): "motor marş bastığında akü bitiyor,
	/// onu hooklayabilirsin." Bu yaklaşım hedefli ve temiz: aküyü düşüren
	/// TEK eylemi devre dışı bırakıyoruz.
	///
	/// ASSET KANITI (docs/fsm_detail/l2__Electrics__Electrics.txt,
	/// FSM 'Electrics' durum akışı):
	///
	///   State 1
	///    ├ "Engine off"     GetProperty(Drivetrain.rpm) → RPM
	///    │                   FloatOperator → FloatClamp
	///    │                   **SubtractFsmFloat(Data, db_Battery, -= yük, perSecond)**
	///    │                   FloatCompare
	///    ├ "Charge battery" FloatOperator ×4 → FloatClamp
	///    │                   **AddFsmFloat(Data, db_Battery, += şarj, perSecond)**
	///    └ "Run on starter" marş anındaki ek çekiş
	///
	/// Aküyü DÜŞÜREN tek eylem `SubtractFsmFloat`'tir. Alternatörün ŞARJ
	/// yolu (`AddFsmFloat`) dokunulmaz — yani oyunun elektrik mantığı,
	/// şarj ışığı ve akü göstergesi bozulmaz; sadece düşüş engellenir.
	///
	/// dnSpy kanıtı (PlayMaker.dll, HutongGames.PlayMaker.Actions.SubtractFsmFloat):
	///   fields : gameObject 0x04001232, fsmName 0x04001233,
	///            variableName 0x04001234, subtractValue 0x04001235,
	///            everyFrame 0x04001236, perSecond 0x04001237
	///   methods: OnEnter 0x06000E70, DoSubtractFsmFloat 0x06000E71 (private),
	///            OnUpdate 0x06000E72   ← HOOK EDİLEN
	///
	/// NEDEN sadece `db_Battery`: `SubtractFsmFloat` sahnede çok kullanılır
	/// (alternatör aşınması `WearSetup/Data/WearAlternator` dahil). Yanlış
	/// değişkeni sıfırlamak oyun mekaniğini bozardı.
	/// </summary>
	public static class BatteryDrainBlock {

		/// <summary>Engellenen döküm sayısı (teşhis).</summary>
		public static int Blocked;

		/// <summary>Kaç farklı SubtractFsmFloat eylemi görüldü (teşhis).</summary>
		public static int Seen;

		/// <summary>AKÜ DÖKÜMÜ HOOK'U — Postfix: işlem yapıldıktan SONRA.</summary>
		public static void OnSubtract(object action) {
			try {
				if (action == null) return;
				if (!BatteryKeepFull.Enabled || !BatteryKeepFull.NoDrain) return;

				Type t = action.GetType();
				if (!string.Equals(t.Name, "SubtractFsmFloat", StringComparison.Ordinal)) return;

				// Hedef değişken ADI eşleşmeli.
				string varName = Str(action, "variableName");
				if (!IsBatteryVar(varName)) return;

				Seen++;

				// Dökümü hiç yapma: `subtractValue` 0 olursa işlem etkisiz.
				FsmFloat sv = Flt(action, "subtractValue");
				if (sv != null) {
					sv.Value = 0f;
					Blocked++;
				}
			}
			catch { }
		}

		/// <summary>Akü değişkeni mi? (esnek: Battery/Volts/Charge/Voltaj)</summary>
		/// <summary>
	/// AKÜ ARTIŞ/AZALIŞINI ENGELLE: `AddFsmFloat` ve `SubtractFsmFloat`
	/// ile `db_Battery`'ye yazılan hem + hem − değerleri döküm/şarj sayılır.
	///
	/// CANLI KANIT: `Crank up` durumunda asset
	///   AddFsmFloat(db_Battery, -= yük)   (asset 475)
	/// SATIRLARI DÜŞÜŞ YARATIYORDU. `subtractValue`'yu sıfırlamak işe
	/// yaramaz çünkü alan negatif olabilir; doğrusu hedef değişkeni
	/// KORUMAK. Burada basitleştiriyoruz: `addValue`/`subtractValue`
	/// 0'a çekilir → işlem etkisiz kalır. Pozitif şarj yolu (alternatör,
	/// motor çalışırken) ise ayrı: onu YALNIZCA değeri düşürüyorsa
	/// engelliyoruz, şarj yolunu açıkmı koruyoruz.
	/// </summary>
	public static void OnAdd(object action) {
		try {
			if (action == null) return;
			if (!BatteryKeepFull.Enabled || !BatteryKeepFull.NoDrain) return;
			Type t = action.GetType();
			if (!string.Equals(t.Name, "AddFsmFloat", StringComparison.Ordinal)) return;
			string varName = Str(action, "variableName");
			if (!IsBatteryVar(varName)) return;
			FsmFloat av = Flt(action, "addValue");
			if (av == null) return;
			// Yalnızca DÜŞÜŞ ise engelle (negatif addValue).
			if (av.Value < 0f) {
				av.Value = 0f;
				Blocked++;
			}
			Seen++;
		}
		catch { }
	}

	static bool IsBatteryVar(string n) {
			if (string.IsNullOrEmpty(n)) return false;
			if (string.Equals(n, "db_Battery", StringComparison.Ordinal)) return true;
			if (string.Equals(n, "Battery", StringComparison.Ordinal)) return true;
			if (string.Equals(n, "Volts", StringComparison.Ordinal)) return true;
			if (string.Equals(n, "Voltage", StringComparison.Ordinal)) return true;
			if (string.Equals(n, "Charge", StringComparison.Ordinal)) return true;
			return false;
		}

		static FieldInfo Fi(object o, string name, bool pub) {
			try {
				BindingFlags bf = BindingFlags.Instance
					| (pub ? BindingFlags.Public : BindingFlags.NonPublic);
				return o.GetType().GetField(name, bf);
			}
			catch { return null; }
		}

		static string Str(object o, string name) {
			try {
				FieldInfo fi = Fi(o, name, true);
				if (fi == null) return null;
				object v = fi.GetValue(o);
				if (v == null) return null;
				// DÜZELTME: bu alan BİR `FsmString`'dir, düz string DEĞİLDİR.
				// `v as string` her zaman null döndüğü için CANLI KANIT:
				//   - OnSubtract → Str == null → IsBatteryVar(null) → return
				//   - yani akü dökümü HİÇ ENGELLENMEDİ.
				// Doğrusu: FsmString'in `.Value` property'sini okumak.
				if (v is string) return (string)v;
				PropertyInfo p = v.GetType().GetProperty("Value",
					BindingFlags.Public | BindingFlags.Instance);
				if (p == null) return null;
				object inner = p.GetValue(v, null);
				return inner as string;
			}
			catch { return null; }
		}

		static FsmFloat Flt(object o, string name) {
			try {
				FieldInfo fi = Fi(o, name, true);
				if (fi == null) return null;
				return fi.GetValue(o) as FsmFloat;
			}
			catch { return null; }
		}
	}

	/// <summary>
	/// Harmony yaması: `SubtractFsmFloat.OnUpdate` → akü düşüşünü engelle.
	///
	/// NEDEN Postfix: işlem zaten yapılmıştır; `subtractValue`'yu sıfırlamak
	/// sonraki çalıştırmayı etkisizleştirir. Prefix'te yapsaydık işlem yine
	/// gerçekleşirdi.
	/// </summary>
	[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.SubtractFsmFloat), "OnUpdate")]
	public static class BatteryDrainHook {
		static void Postfix(object __instance) {
			try { BatteryDrainBlock.OnSubtract(__instance); } catch { }
		}
	}

	/// <summary>
	/// Aynı engelleme OnEnter'da da: tek seferlik döküm (everyFrame=false)
	/// OnUpdate hook'unu hiç görmez, OnEnter içinde DoSubtractFsmFloat'i
	/// çalıştırır. Prefix'te likini sıfırlıyoruz ki işlem hiç olmasın.
	/// </summary>
	[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.SubtractFsmFloat), "OnEnter")]
	public static class BatteryDrainHookEnter {
		static void Prefix(object __instance) {
			try { BatteryDrainBlock.OnSubtract(__instance); } catch { }
		}
	}

	/// <summary>
	/// Harmony yaması: `AddFsmFloat.OnUpdate` → akü artış/azalışını engelle.
	///
	/// NEDEN GEREKLİ: `Crank up` durumunda asset
	///   12:50:24.102 …/Starter  Charge=111.89  StarterPitch=0.93
	///   12:50:26.047 …/Starter  Charge=111.31
	/// satırlarındaki düşüş `AddFsmFloat(db_Battery, -= yük)`'ten geliyor
	/// (asset 465-485). Yani marş ANINDA aküyü düşüren yol
	/// `AddFsmFloat`'tir, `SubtractFsmFloat` değil.
	///
	/// `addValue`'yu 0'a çekiyoruz; `AddFsmFloat` o zaman hiçbir şey
	/// eklemiyor. Pozitif `addValue` (alternatör şarjı) YALNIZCA motor
	/// çalışıyorken çalışır ve aküyü doldurur — ama `addValue=0` yapmak
	/// şarjı DA engeller. Bu yüzden yalnızca DÖKÜM ile eşleşen hedef
	/// değişkeninde engelliyoruz ve yalnızca değer DÜŞÜYORSA (addValue < 0).
	/// </summary>
	[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.AddFsmFloat), "OnUpdate")]
	public static class BatteryAddDrainHook {
		static void Postfix(object __instance) {
			try { BatteryDrainBlock.OnAdd(__instance); } catch { }
		}
	}

	/// <summary>Tek seferlik döküm OnEnter'dan gelirse OnUpdate hook'u
	/// hiç çalışmaz; prefix ile addValue'yi sıfırlayarak engelleriz.</summary>
	[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.AddFsmFloat), "OnEnter")]
	public static class BatteryAddDrainHookEnter {
		static void Prefix(object __instance) {
			try { BatteryDrainBlock.OnAdd(__instance); } catch { }
		}
	}
}