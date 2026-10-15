using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Oyun içi zaman ve hava durumu senkronizasyonu.
	///
	/// Neden FSM değişkenleri
	/// --------------------
	/// MSC'de saat ve hava bir C# singleton'ı değil; saat "GAME" sahnesindeki bir
	/// PlayMaker FSM'nin saat değişkenleriyle, hava ise ayrı bir FSM zinciriyle yürütülüyor
	/// (Assembly-CSharp içinde Clock/Weather tipi yok). Bu yüzden bir "GameClock.SetHour()"
	/// yaması yazılamaz; yazılabilir olan şey, o değişkenlere olan yazmaları izlemektir.
	///
	/// Yaklaşım
	/// --------
	/// 1. Bağlama (bind): dünya yüklendiğinde, adı saat/hava kalıplarına uyan FSM
	///    değişkenleri taranır ve bir tabloya alınır. Bu tablo "ne senkronize ediliyor"
	///    sorusunun cevabıdır ve konsola yazılır; kullanıcı isterse 'smp world bind <ad>'
	///    ile ad ekleyebilir.
	/// 2. Yakalama: FsmInt/FsmFloat/FsmBool için zaten global olan setter hook'ları bu
	///    modüle yönlendirilir. Bir bağlı değişken yazıldığında kirlenir.
	/// 3. Gönderim: 1 Hz'de bir paket. Saat sürekli değiştiği için yüksek frekans anlamsız;
	///    hava zaten dakikalar arayla değişir.
	/// 4. Uygulama: karşı tarafta değerler EchoGuard kapsamında yazılır, böylece karşı
	///    tarafın kendi saatinin değişimi geri gönderilmez.
	///
	/// Tablo eşleşmesi
	/// --------------
	/// Değişkenler isimle değil, tablodaki *sırayla* gönderilir ve paket başında tablo
	/// imzası (hash) taşınır. İmza tutmuyorsa iki taraf farklı nesneleri bağlamış demektir:
	/// bu durumda değerler uygulanmaz, çünkü yanlış değişkeni yanlış değerle ezmek oyunu
	/// bozar. Kullanıcıya hangi tarafın ne bağladığı yazdırılır.
	/// </summary>
	public static class WorldSync {

		/// <summary>Gönderim aralığı, saniye. Saat dakikalar, hava saatler ölçeğinde değişir.</summary>
		public const float SEND_INTERVAL = 1f;

		/// <summary>
		/// Varsayılan bağlanan değişken adları. Küçük harfe karşılaştırılır.
		/// Bilinmeyen bir sürümde isim değişmiş olabilir; o zaman 'smp world scan <metin>'
		/// ile adayları görüp 'smp world bind <ad>' ile eklenir.
		/// </summary>
		public static readonly string[] DEFAULT_NAMES = {
			"hour", "day", "minute", "minuteofday", "timeofday",
			"weatherselect", "weathertype", "rain", "rainyes", "thunderforce", "israining", "cloudy"
		};

		static readonly List<string> names = new List<string>(DEFAULT_NAMES);
		static readonly List<Binding> bindings = new List<Binding>(16);
		static readonly Dictionary<FsmInt, Binding> intBindings = new Dictionary<FsmInt, Binding>(RefComparer<FsmInt>.Instance);
		static readonly Dictionary<FsmFloat, Binding> floatBindings = new Dictionary<FsmFloat, Binding>(RefComparer<FsmFloat>.Instance);
		static readonly Dictionary<FsmBool, Binding> boolBindings = new Dictionary<FsmBool, Binding>(RefComparer<FsmBool>.Instance);

		static readonly List<Binding> dirty = new List<Binding>(16);
		static float nextSend;
		static uint tableHash;
		static bool scanned;
		static bool bindingsDirty = true; // Track if bindings need resorting

		/// <summary>Bağlanan değişken sayısı.</summary>
		public static int BoundCount { get { return bindings.Count; } }
		/// <summary>Gönderilen güncelleme sayısı.</summary>
		public static int SentUpdates;
		/// <summary>Uygulanan güncelleme sayısı.</summary>
		public static int AppliedUpdates;
		/// <summary>İmza tutmadığı için reddedilen paket sayısı.</summary>
		public static int TableMismatches;
		static uint lastMismatchHash = 0xFFFFFFFF;

		public enum Kind { Int, Float, Bool }

		/// <summary>Bağlanmış tek bir değişken.</summary>
		public class Binding {
			public Kind Type;
			public FsmInt Int;
			public FsmFloat Float;
			public FsmBool Bool;
			public string Name;       // değişken adı
			public string Path;       // sahne kökünden göreli yol (tanı için)
			public float LastSent = float.NaN;
			public bool Dirty;
			public float Current { get { return Value(); } }

			float Value() {
				switch (Type) {
					case Kind.Int: return Int != null ? Int.Value : 0f;
					case Kind.Float: return Float != null ? Float.Value : 0f;
					default: return (Bool != null && Bool.Value) ? 1f : 0f;
				}
			}
		}

		// ------------------------------------------------------------------
		// Bağlama
		// ------------------------------------------------------------------

		/// <summary>Dünya yüklendiğinde çağrılır.</summary>
		public static void Discover() {
			Clear();
			GameObject[] roots = SceneDump.AllSceneRoots();
			int fsmCount = 0;
			for (int r = 0; r < roots.Length; r++) {
				PlayMakerFSM[] fsms = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
				fsmCount += fsms.Length;
				for (int i = 0; i < fsms.Length; i++) {
					if (fsms[i] == null) continue;
					TryBind(fsms[i]);
				}
			}
			// Global saat: tum duvar/masa/saat gostergeleri PlayMaker
			// globallerini okur (Hours24X/Minutes24X, dumper kaniti:
			// STORE/LOD/Clock Time FSM). Sahne taramasi globalleri
			// gormez; ayrica baglanir. Yol iki tarafta sabit "GLOBALS"
			// oldugu icin imza ayni hesaplanir.
			try {
				HutongGames.PlayMaker.FsmVariables globals = HutongGames.PlayMaker.FsmVariables.GlobalVariables;
				if (globals != null) {
					BindGlobalInt(globals, "Hours24X");
					BindGlobalInt(globals, "Minutes24X");
				}
			} catch { }
			// Sıra iki tarafta aynı olmalı: yola göre sırala, sonra imzayı hesapla.
			// Sadece bindingsDirty ise tekrar sırala (Performance: tablo değişmedi
			// ancak Discover tekrar çağrıldıysa évreli sıralamayı kaçının).
			if (bindingsDirty) {
				bindings.Sort(delegate (Binding a, Binding b) {
					return string.CompareOrdinal(a.Path + "/" + a.Name, b.Path + "/" + b.Name);
				});
				bindingsDirty = false;
			}
			tableHash = ComputeHash();
			scanned = true;

			SmpLog.Log("world: " + roots.Length + " root(s), " + fsmCount + " FSM(s), bound "
				+ bindings.Count + " variable(s), hash 0x"
				+ tableHash.ToString("x8") + (bindings.Count == 0
					? " (nothing matched; run 'smp world scan hour' to find the real names)" : ""));
		}

		/// <summary>
		/// 'smp world scan &lt;metin&gt;': adi verilen metni iceren FSM degiskenlerini
		/// listeler (tum degisken tipleri). dnSpy notu: saat/hava adlari DLL'de
		/// yoktur (sahne verisidir), bu yuzden gercek adlar ancak boyle bulunur.
		/// En fazla 30 satir basar.
		/// </summary>
		public static void Scan(string fragment) {
			if (string.IsNullOrEmpty(fragment)) fragment = "";
			int shown = 0;
			GameObject[] roots = SceneDump.AllSceneRoots();
			for (int r = 0; r < roots.Length && shown < 30; r++) {
				PlayMakerFSM[] fsms = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
				for (int i = 0; i < fsms.Length && shown < 30; i++) {
					PlayMakerFSM fsm = fsms[i];
					if (fsm == null || fsm.gameObject == null) continue;
					string fsmName;
					try { fsmName = fsm.FsmName; } catch { continue; }
					FsmVariables v;
					try { v = fsm.FsmVariables; } catch { continue; }
					if (v == null) continue;
					shown = DumpVars(v.IntVariables, fsm, fsmName, fragment, shown);
					if (shown >= 30) break;
					shown = DumpVars(v.FloatVariables, fsm, fsmName, fragment, shown);
					if (shown >= 30) break;
					shown = DumpVars(v.BoolVariables, fsm, fsmName, fragment, shown);
				}
			}
			if (shown == 0) SmpLog.Print("world scan '" + fragment + "': no match.");
			else SmpLog.Print("world scan '" + fragment + "': " + shown + " shown. Bind with 'smp world bind <name>'.");
		}

		static int DumpVars(NamedVariable[] vars, PlayMakerFSM fsm, string fsmName, string fragment, int shown) {
			if (vars == null) return shown;
			for (int k = 0; k < vars.Length && shown < 30; k++) {
				NamedVariable nv = vars[k];
				if (nv == null || string.IsNullOrEmpty(nv.Name)) continue;
				if (fragment.Length > 0
					&& nv.Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0
					&& fsmName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
				SmpLog.Print("  " + SceneDump.PathOf(fsm.gameObject.transform)
					+ " [" + fsmName + "] " + nv.GetType().Name + " " + nv.Name);
				shown++;
			}
			return shown;
		}

		/// <summary>Yeni bir değişken adı ekler ve taramayı tekrarlar.</summary>
		public static bool AddName(string name) {
			if (string.IsNullOrEmpty(name)) return false;
			for (int i = 0; i < names.Count; i++) {
				if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return false;
			}
			names.Add(name);
			Discover();
			return true;
		}

		static void BindGlobalInt(HutongGames.PlayMaker.FsmVariables globals, string name) {
			if (globals == null || string.IsNullOrEmpty(name)) return;
			HutongGames.PlayMaker.FsmInt v = null;
			try { v = globals.GetFsmInt(name); } catch { return; }
			if (v == null) return;
			Binding b = new Binding();
			b.Type = Kind.Int;
			b.Int = v;
			b.Name = v.Name;
			b.Path = "GLOBALS";
			bindings.Add(b);
			bindingsDirty = true;
			intBindings[v] = b;
		}

		static void TryBind(PlayMakerFSM fsm) {
			string path = SceneDump.PathOf(fsm.gameObject.transform);

			FsmInt[] ints = fsm.FsmVariables.IntVariables;
			if (ints != null) {
				for (int i = 0; i < ints.Length; i++) BindInt(ints[i], path);
			}
			FsmFloat[] floats = fsm.FsmVariables.FloatVariables;
			if (floats != null) {
				for (int i = 0; i < floats.Length; i++) BindFloat(floats[i], path);
			}
			FsmBool[] bools = fsm.FsmVariables.BoolVariables;
			if (bools != null) {
				for (int i = 0; i < bools.Length; i++) BindBool(bools[i], path);
			}
		}

		static bool Matches(string variableName) {
			if (string.IsNullOrEmpty(variableName)) return false;
			for (int i = 0; i < names.Count; i++) {
				if (string.Equals(names[i], variableName, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		static void BindInt(FsmInt v, string path) {
			if (v == null || intBindings.ContainsKey(v) || !Matches(v.Name)) return;
			Binding b = new Binding();
			b.Type = Kind.Int;
			b.Int = v;
			b.Name = v.Name;
			b.Path = path;
			bindings.Add(b);
			bindingsDirty = true;
			intBindings[v] = b;
		}

		static void BindFloat(FsmFloat v, string path) {
			if (v == null || floatBindings.ContainsKey(v) || !Matches(v.Name)) return;
			Binding b = new Binding();
			b.Type = Kind.Float;
			b.Float = v;
			b.Name = v.Name;
			b.Path = path;
			bindings.Add(b);
			bindingsDirty = true;
			floatBindings[v] = b;
		}

		static void BindBool(FsmBool v, string path) {
			if (v == null || boolBindings.ContainsKey(v) || !Matches(v.Name)) return;
			Binding b = new Binding();
			b.Type = Kind.Bool;
			b.Bool = v;
			b.Name = v.Name;
			b.Path = path;
			bindings.Add(b);
			bindingsDirty = true;
			boolBindings[v] = b;
		}

		/// <summary>Dünya değişiminde tabloyu bırakır.</summary>
		public static void Clear() {
			bindings.Clear();
			intBindings.Clear();
			floatBindings.Clear();
			boolBindings.Clear();
			dirty.Clear();
			scanned = false;
			tableHash = 0;
			bindingsDirty = true;
		}

		static uint ComputeHash() {
			// FNV-1a: (yol + ad) çiftlerinden. Sıra zaten normalize edildi.
			uint hash = 2166136261;
			for (int i = 0; i < bindings.Count; i++) {
				string s = bindings[i].Path + "/" + bindings[i].Name;
				for (int c = 0; c < s.Length; c++) {
					hash ^= s[c];
					hash *= 16777619;
				}
			}
			return hash;
		}

		// ------------------------------------------------------------------
		// Yakalama (global setter hook'larından)
		// ------------------------------------------------------------------

		/// <summary>FsmInt.set_Value hook'undan. Her yazma buraya düşer; kısa yol bir anahtar araması.</summary>
		public static void OnIntWrite(FsmInt variable) {
			if (EchoGuard.Suppressed || variable == null || !SteamTransport.HasRemote || !SteamTransport.IsHost) return;
			Binding b;
			if (!intBindings.TryGetValue(variable, out b)) return;
			MarkDirty(b);
		}

		public static void OnFloatWrite(FsmFloat variable) {
			if (EchoGuard.Suppressed || variable == null || !SteamTransport.HasRemote || !SteamTransport.IsHost) return;
			Binding b;
			if (!floatBindings.TryGetValue(variable, out b)) return;
			MarkDirty(b);
		}

		public static void OnBoolWrite(FsmBool variable) {
			if (EchoGuard.Suppressed || variable == null || !SteamTransport.HasRemote || !SteamTransport.IsHost) return;
			Binding b;
			if (!boolBindings.TryGetValue(variable, out b)) return;
			MarkDirty(b);
		}

		static void MarkDirty(Binding b) {
			if (b.Dirty) return;
			b.Dirty = true;
			if (dirty.Count < 64) dirty.Add(b); // taşma durumunda tam gönderim zaten yetiyor
		}

		// ------------------------------------------------------------------
		// Gönderim / uygulama
		// ------------------------------------------------------------------

		/// <summary>Kare başına bir kez çağrılır.</summary>
		public static void Update() {
			// HOST OTORITER (Faz 6 duzeltmesi): iki taraf da gonderirse
			// saatler birbirini ezer (client kendi saatini hosta geri yazar).
			// Host 1 Hz yayinlar, client yalnizca uygular (EchoGuard yankisiz).
			if (!SteamTransport.HasRemote || !scanned || !SteamTransport.IsHost) return;
			float now = Time.time;
			// Saat değişimi sürekli olduğu için kirli değişken varsa bekleme.
			if (dirty.Count == 0 && now < nextSend) return;
			if (now < nextSend) return;
			nextSend = now + SEND_INTERVAL;

			SendState();
		}

		/// <summary>Bağlantı kurulduğunda tam durumu bir kez gönderir.</summary>
		public static void SendAll() {
			if (!SteamTransport.HasRemote || !scanned || !SteamTransport.IsHost) return;
			SendState();
		}

		static void SendState() {
			if (bindings.Count == 0) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.WorldState);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(tableHash);
				w.Write((byte)bindings.Count);
				for (int i = 0; i < bindings.Count; i++) {
					Binding b = bindings[i];
					float v = b.Current;
					w.Write(v);
					b.LastSent = v;
					b.Dirty = false;
				}
				if (SteamTransport.SendReliable(Protocol.Finish(s))) SentUpdates++;
			}
			catch (Exception e) {
				SmpLog.Warning("world send failed: " + e.Message);
			}
			dirty.Clear();
		}

		public static void OnMessage(BinaryReader r) {
			try {
				Protocol.AssertBytesRemaining(r, 5, "world state header"); // 4 bytes hash + 1 byte count
				uint hash = r.ReadUInt32();
				byte count = r.ReadByte();
				if (hash != tableHash) {
					TableMismatches++;
					if (hash != lastMismatchHash) {
						lastMismatchHash = hash;
						SmpLog.Warning("world state ignored: the two sides bound different "
							+ "variables (peer 0x" + hash.ToString("x8") + ", us 0x" + tableHash.ToString("x8")
							+ "). Run 'smp world scan' on both sides and use the same names.");
					}
					return;
				}
				lastMismatchHash = hash;
				int n = count < bindings.Count ? count : bindings.Count;
				EchoGuard.Scope scope = EchoGuard.Enter();
				try {
					for (int i = 0; i < n; i++) {
						float v = r.ReadSingle();
						Binding b = bindings[i];
						switch (b.Type) {
							case Kind.Int:
								if (b.Int != null) b.Int.Value = (int)v;
								break;
							case Kind.Float:
								if (b.Float != null) b.Float.Value = v;
								break;
							default:
								if (b.Bool != null) b.Bool.Value = v != 0f;
								break;
						}
						AppliedUpdates++;
					}
				}
				finally { scope.Dispose(); }
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("world packet rejected: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Tanılama
		// ------------------------------------------------------------------

		/// <summary>'smp world' çıktısı: her bağlanan değişken, yolu ve değeri.</summary>
		public static List<string> Dump() {
			List<string> lines = new List<string>(bindings.Count);
			for (int i = 0; i < bindings.Count; i++) {
				Binding b = bindings[i];
				lines.Add(b.Path + "  [" + b.Type + "] " + b.Name + " = " + b.Current
					+ (b.Dirty ? "  (pending)" : ""));
			}
			return lines;
		}

		public static string Describe() {
			if (!scanned) return "world: not scanned";
			return "world: " + bindings.Count + " bound variable(s), sent " + SentUpdates
				+ ", applied " + AppliedUpdates
				+ (TableMismatches > 0 ? ", " + TableMismatches + " table mismatch(es)" : "");
		}
	}
}