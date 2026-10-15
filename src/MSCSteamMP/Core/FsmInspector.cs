using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// OYUNUN FSM DEĞİŞKENLERİNİ ÖĞRENME ARACI.
	///
	/// NEDEN VAR
	/// ---------
	/// Bu oyunun bütün mantığı C# değil, PlayMaker FSM'idir ve
	/// değişken adları sahne verisinde saklanır. dnSpy'de
	/// `search_strings("Jul")` boş döner, `/dump` komutu yoktur,
	/// Base64/obfuscation katmanı yoktur. Yani değişken adlarına
	/// çevrimdışı ulaşmanın TEK yolu çalışma zamanında okumaktır.
	///
	/// Bu sınıf o işi yapar: verilen nesnenin altındaki HER PlayMakerFSM'in
	/// HER değişkenini isim + tip + değer olarak döker.
	///
	/// NASIL KULLANILIR
	/// -----------------
	///   smp fsm FrontDoor          -> o nesnedeki tüm FSM değişkenleri
	///   smp fsm CDPlayer          -> CD çalarların değişkenleri
	///   smp fsm KitchenWaterTap    -> musluğun değişkenleri
	///   smp fsm FrontDoor Open    -> yalnızca adı 'Open' ile GEÇEN değişkenler
	///
	/// Bu, kapı/musluk/radyo senkronunun değişken adlarını elle bulmak
	/// yerine okuyup tabloya yazmak içindir. Yanlış ad denemek yerine
	/// ismi GÖRÜRÜZ.
	///
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class FsmInspector {

		/// <summary>Tek seferde en fazla bu kadar satır yazılır.</summary>
		const int MAX_LINES = 400;

		// Yeniden kullanılan tampon: komut çağrısı seyrek olduğu için
		// burada tahsis sorun değil, ama tutarlılık için saklıyoruz.
		static readonly StringBuilder sb = new StringBuilder(1024);
		static readonly List<string> lines = new List<string>(128);

		/// <summary>
		/// <paramref name="objectName"/> nesnesini bulur ve tüm FSM
		/// değişkenlerini yazar. <paramref name="filter"/> boş değilse
		/// yalnızca adında geçen değişkenler yazılır.
		/// </summary>
		public static void Dump(string objectName, string filter) {
			lines.Clear();
			if (string.IsNullOrEmpty(objectName)) {
				SmpLog.Error("usage: smp fsm <objectName> [variableNameFilter]");
				return;
			}

			GameObject go = Find(objectName);
			if (go == null) {
				SmpLog.Error("no object named '" + objectName + "' in this scene.");
				SmpLog.Print("list the candidates with: smp doors | smp radio | /npc scan");
				return;
			}

			PlayMakerFSM[] fsms;
			try { fsms = go.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch { fsms = null; }
			if (fsms == null || fsms.Length == 0) {
				SmpLog.Print("'" + objectName + "' has no PlayMakerFSM component.");
				return;
			}

			bool hasFilter = !string.IsNullOrEmpty(filter);
			int total = 0, shown = 0;

			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null || fsm.FsmVariables == null) continue;
				string fsmName = SafeFsmName(fsm);
				string owner = fsm.gameObject != null ? fsm.gameObject.name : "?";

				int before = total;
				total += CollectBool(fsmName, owner, fsm, filter, hasFilter);
				total += CollectInt(fsmName, owner, fsm, filter, hasFilter);
				total += CollectFloat(fsmName, owner, fsm, filter, hasFilter);
				total += CollectString(fsmName, owner, fsm, filter, hasFilter);
				// Durum adları: replikasyon tablosunu (FsmBinder) doldurmak
				// için değişken adları YETMEZ. dnSpy kanıtı (PlayMaker.dll):
				// Fsm.States -> FsmState[], FsmState.Name, Fsm.GetState(string).
				CollectStates(fsmName, owner, fsm, filter, hasFilter);

				if (total > before && shown < 4) {
					SmpLog.Print("-- FSM '" + fsmName + "' on '" + owner + "' ("
						+ (total - before) + " variable(s))");
					shown++;
				}
			}

			if (lines.Count == 0) {
				SmpLog.Print(total == 0
					? "'" + objectName + "' has no matching FSM variable(s)."
					: "'" + objectName + "': " + total + " variable(s) matched.");
				if (hasFilter) SmpLog.Print("filter '" + filter + "' matched nothing; run without a filter to see all.");
				return;
			}

			SmpLog.Print("'" + objectName + "': " + total + " variable(s), "
				+ fsms.Length + " FSM(s)."
				+ (hasFilter ? " filter='" + filter + "'" : ""));
			for (int i = 0; i < lines.Count && i < MAX_LINES; i++) {
				SmpLog.Print("   " + lines[i]);
			}
			if (lines.Count > MAX_LINES) {
				SmpLog.Print("   ... " + (lines.Count - MAX_LINES) + " more (raise MAX_LINES or filter).");
			}
			SmpLog.Print("copy these names into WorldObjectSync/RadioSync to bind them.");
		}

		static string SafeFsmName(PlayMakerFSM fsm) {
			try {
				if (fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name)) return fsm.Fsm.Name;
				return fsm.name;
			}
			catch { return fsm != null && !string.IsNullOrEmpty(fsm.name) ? fsm.name : "(unnamed)"; }
		}

		static bool Keep(string fsmName, string owner, string varName, string filter, bool hasFilter) {
			if (!hasFilter) return true;
			// Süzgeç nesne adında da FSM adında da aranır: kullanıcı
			// 'smp fsm RadioTuner Volume' gibi kullanabilir.
			return NameOf(varName, filter)
				|| (fsmName != null && fsmName.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
				|| (owner != null && owner.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0);
		}

		static bool NameOf(string hay, string needle) {
			return hay != null && hay.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static void Row(string fsmName, string kind, string varName, string value) {
			lines.Add(kind.PadRight(6) + " " + varName + " = " + value
				+ "    [" + fsmName + "]");
		}

		static int CollectBool(string fsmName, string owner, PlayMakerFSM fsm,
			string filter, bool hasFilter) {
			FsmBool[] a = fsm.FsmVariables.BoolVariables;
			if (a == null) return 0;
			int n = 0;
			for (int i = 0; i < a.Length; i++) {
				if (a[i] == null) continue;
				string vn = Safe(a[i].Name);
				if (!Keep(fsmName, owner, vn, filter, hasFilter)) continue;
				Row(fsmName, "bool", vn, a[i].Value.ToString());
				n++;
			}
			return n;
		}

		static int CollectInt(string fsmName, string owner, PlayMakerFSM fsm,
			string filter, bool hasFilter) {
			FsmInt[] a = fsm.FsmVariables.IntVariables;
			if (a == null) return 0;
			int n = 0;
			for (int i = 0; i < a.Length; i++) {
				if (a[i] == null) continue;
				string vn = Safe(a[i].Name);
				if (!Keep(fsmName, owner, vn, filter, hasFilter)) continue;
				Row(fsmName, "int", vn, a[i].Value.ToString());
				n++;
			}
			return n;
		}

		static int CollectFloat(string fsmName, string owner, PlayMakerFSM fsm,
			string filter, bool hasFilter) {
			FsmFloat[] a = fsm.FsmVariables.FloatVariables;
			if (a == null) return 0;
			int n = 0;
			for (int i = 0; i < a.Length; i++) {
				if (a[i] == null) continue;
				string vn = Safe(a[i].Name);
				if (!Keep(fsmName, owner, vn, filter, hasFilter)) continue;
				Row(fsmName, "float", vn, a[i].Value.ToString("F3"));
				n++;
			}
			return n;
		}

		static int CollectString(string fsmName, string owner, PlayMakerFSM fsm,
			string filter, bool hasFilter) {
			FsmString[] a = fsm.FsmVariables.StringVariables;
			if (a == null) return 0;
			int n = 0;
			for (int i = 0; i < a.Length; i++) {
				if (a[i] == null) continue;
				string vn = Safe(a[i].Name);
				if (!Keep(fsmName, owner, vn, filter, hasFilter)) continue;
				Row(fsmName, "string", vn, "\"" + a[i].Value + "\"");
				n++;
			}
			return n;
		}

		/// <summary>
		/// FSM'in TÜM durum adlarını yazar (aktif durum * ile işaretli).
		///
		/// NEDEN: `smp fsm` değişkenleri döküyordu ama durum adlarını
		/// DÖKMÜYORDU. Oysa FsmBinder replikasyon tablosu durum adlarıyla
		/// çalışıyor (Replicate(fsm, "1","2",...) gibi). Yanlış durum adı
		/// sessizce atlanıyordu (Track içinde GetState null dönüyordu), bu
		/// yüzden GearIndicator/Clutch gibi FSM'lerin gerçek adları
		/// bilinmeden tablo dolduruluyordu. Artık durum adları da
		/// logda görünüyor; tabloyu doğrulamak için `smp fsm GearIndicator`
		/// yeterli.
		///
		/// dnSpy (PlayMaker.dll, 98 tip): Fsm.States (FsmState[]),
		/// FsmState.Name, Fsm.GetState(string), Fsm.ActiveStateName.
		/// </summary>
		static void CollectStates(string fsmName, string owner, PlayMakerFSM fsm,
			string filter, bool hasFilter) {
			HutongGames.PlayMaker.Fsm inner = null;
			try { inner = fsm.Fsm; } catch { return; }
			if (inner == null) return;

			string active = null;
			try { active = inner.ActiveStateName; } catch { }

			HutongGames.PlayMaker.FsmState[] states = null;
			try { states = inner.States; } catch { return; }
			if (states == null || states.Length == 0) return;

			System.Text.StringBuilder sbStates = new System.Text.StringBuilder(256);
			int shown = 0;
			for (int i = 0; i < states.Length; i++) {
				HutongGames.PlayMaker.FsmState st = states[i];
				if (st == null) continue;
				string n = null;
				try { n = st.Name; } catch { continue; }
				if (string.IsNullOrEmpty(n)) continue;
				if (hasFilter && !NameOf(n, filter)
					&& !NameOf(fsmName, filter) && !NameOf(owner, filter)) continue;
				if (shown > 0) sbStates.Append(", ");
				bool isActive = !string.IsNullOrEmpty(active)
					&& string.Equals(n, active, System.StringComparison.Ordinal);
				if (isActive) sbStates.Append('*');
				sbStates.Append(n);
				shown++;
			}
			if (shown == 0) return;
			lines.Add("states (" + shown + "): " + sbStates.ToString()
				+ "    [" + fsmName + "]");
		}

		static string Safe(string s) { return string.IsNullOrEmpty(s) ? "(unnamed)" : s; }

		// ------------------------------------------------------------------
		// Bulma
		// ------------------------------------------------------------------

		/// <summary>Ada göre nesne bulur (pasif olanlar dahil).</summary>
		public static GameObject Find(string name) {
			if (string.IsNullOrEmpty(name)) return null;
			GameObject direct = GameObject.Find(name);
			if (direct != null) return direct;
			GameObject[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
			catch { return null; }
			if (all == null) return null;
			GameObject loose = null;
			for (int i = 0; i < all.Length; i++) {
				GameObject c = all[i];
				if (c == null) continue;
				if (c.name == name) return c;
				if (loose == null && string.Equals(c.name, name,
						System.StringComparison.OrdinalIgnoreCase)) loose = c;
			}
			return loose;
		}

		/// <summary>
		/// Sahnedeki TÜM FSM değişkenlerini toplu listeler (süzgeçsiz tarama).
		/// "Sınırlı tahminle kısıtlı alan arıyor" diye düşüp elle denemek
		/// yerine oyunun sözlüğünü bir kez görmek için.
		/// </summary>
		public static void DumpAllWithName(string filter, int maxObjects) {
			GameObject[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
			catch { return; }
			if (all == null) return;

			bool hasFilter = !string.IsNullOrEmpty(filter);
			int objects = 0;
			for (int i = 0; i < all.Length && objects < maxObjects; i++) {
				GameObject go = all[i];
				if (go == null) continue;
				PlayMakerFSM[] fsms;
				try { fsms = go.GetComponents<PlayMakerFSM>(); }
				catch { continue; }
				if (fsms == null) continue;
				for (int f = 0; f < fsms.Length; f++) {
					if (fsms[f] == null || fsms[f].FsmVariables == null) continue;
					if (HasVar(fsms[f], filter, hasFilter)) {
						Dump(go.name, filter);
						objects++;
						break;
					}
				}
			}
			if (objects == 0) {
				SmpLog.Print("no FSM variable" + (hasFilter ? " matching '" + filter + "'" : "")
					+ " was found. Try: smp fsm FrontDoor");
			}
		}

		static bool HasVar(PlayMakerFSM fsm, string filter, bool hasFilter) {			if (!hasFilter) return true;
			FsmBool[] b = fsm.FsmVariables.BoolVariables;
			if (b != null) for (int i = 0; i < b.Length; i++)
				if (b[i] != null && NameOf(b[i].Name, filter)) return true;
			FsmInt[] n = fsm.FsmVariables.IntVariables;
			if (n != null) for (int i = 0; i < n.Length; i++)
				if (n[i] != null && NameOf(n[i].Name, filter)) return true;
			FsmFloat[] f = fsm.FsmVariables.FloatVariables;
			if (f != null) for (int i = 0; i < f.Length; i++)
				if (f[i] != null && NameOf(f[i].Name, filter)) return true;
			FsmString[] s = fsm.FsmVariables.StringVariables;
			if (s != null) for (int i = 0; i < s.Length; i++)
				if (s[i] != null && NameOf(s[i].Name, filter)) return true;
			return false;
		}

		/// <summary>
		/// PlayMaker GLOBAL degiskenleri doker. Sahne FSM'lerinde bulunamayan
		/// yakit/cuzdan gibi capraz-FSM durumlari globalde olabilir (Satsuma
		/// deposu sahnede yok: ne Data/FuelLevel ne igne). Filtre yoksa ilk
		/// 40 global basilir.
		/// </summary>
		public static void DumpGlobals(string filter) {
			bool hasFilter = !string.IsNullOrEmpty(filter);
			FsmVariables globals = null;
			try { globals = FsmVariables.GlobalVariables; } catch { globals = null; }
			if (globals == null) {
				SmpLog.Print("no PlayMaker globals accessible.");
				return;
			}
			int shown = 0;
			shown += DumpGlobalList("bool", globals.BoolVariables, filter, hasFilter);
			shown += DumpGlobalList("int", globals.IntVariables, filter, hasFilter);
			shown += DumpGlobalList("float", globals.FloatVariables, filter, hasFilter);
			shown += DumpGlobalList("string", globals.StringVariables, filter, hasFilter);
			if (shown == 0) {
				SmpLog.Print("no global variable"
					+ (hasFilter ? " matching '" + filter + "'" : "") + " found.");
			}
			else if (!hasFilter) {
				SmpLog.Print("(first " + shown + " globals shown; use 'smp globals <filter>'.)");
			}
		}

		static int DumpGlobalList(string kind, System.Array vars, string filter, bool hasFilter) {
			if (vars == null) return 0;
			int shown = 0;
			for (int i = 0; i < vars.Length && shown < 40; i++) {
				object v = null;
				try { v = vars.GetValue(i); } catch { continue; }
				if (v == null) continue;
				string name = null;
				string value = null;
				try {
					FsmBool b = v as FsmBool;
					if (b != null) { name = b.Name; value = b.Value.ToString(); }
					else {
						FsmInt ni = v as FsmInt;
						if (ni != null) { name = ni.Name; value = ni.Value.ToString(); }
						else {
							FsmFloat f = v as FsmFloat;
							if (f != null) { name = f.Name; value = f.Value.ToString("F3"); }
							else {
								FsmString s = v as FsmString;
								if (s != null) { name = s.Name; value = s.Value ?? ""; }
							}
						}
					}
				}
				catch { continue; }
				if (string.IsNullOrEmpty(name)) continue;
				if (hasFilter && !NameOf(name, filter)) continue;
				SmpLog.Print("   global " + kind + " " + name + " = " + value);
				shown++;
			}
			return shown;
		}
	}
}