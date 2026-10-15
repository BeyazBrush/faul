// Satsuma sivilari + parca yipranmasi + park halindeki yakit.
// Hepsi PlayMaker float'idir (dnSpy kaniti: C# tarafinda "Money"/"Wear"
// string'i yok, sahne verisinde de UnityPy ile okunamiyorlar; adlar
// `smp fsm` canli sorgusundan cikarildi):
//
//   motor oil(itemx)  Use/Fluid + Data/Fluid  (sisedeki yag)
//   MotorOilTrigger   Data/Fluid              (yag doldurma tetik aynasi)
//   brake fluid(itemx) Use/Fluid + Data/Fluid (sisedeki fren hidroliği)
//   BrakeFluidTrigger Data/Fluid
//   Combustion        Cylinders/Oil           (motordaki yag; 42 degiskenli
//                                            FSM'de SADECE Oil baglanir,
//                                            devir/sicaklik spam olur)
//   Cooling           Cooling/WaterLevel      (sogutma suyu; CoolantTemp ve
//                                            basinc/sicaklik SADECE yerel
//                                            simulasyondur, baglanmaz)
//   SATSUMA/FuelTank  Data/FuelLevel|Fuel|Level (park halindeki yakit; surucu
//                                            varken Transform paketi zaten
//                                            tasiyor, bu kapak acigi kapatir)
//   Use.Wear + Data.Wear (buji, kayis, ampul...): isim listesi hardcode
//                                            DEGIL, kesifte otomatik bulunur.
//
// Tasarim WorldObjectSync ile ayni (guvenilir kanal, degisince gonder,
// ilk tikta taban cizgisi): ama AYRI mesaj (FluidState=43) ve AYRI tablo,
// cunku yankı sahipligi tabloya ozeldir. Ayni ObjectState paketini
// paylassaydik uygulanan deger karsi tabloda isaretlenir, bizim tarama
// yine degisiklik gorup geri gonderirdi.
using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP {

	public static class FluidWearSync {

		/// <summary>Degisim taramasi araligi (saniye). Sivi/asinma yavastir.</summary>
		public const float POLL_INTERVAL = 0.5f;

		/// <summary>Float hassasiyeti. Yag/su 0-1 (ya da 0-100) olcegindedir.</summary>
		public const float EPSILON = 0.005f;

		/// <summary>Ayni kayit icin yankı susturma (uzaktan uygulanan geri gitmez).</summary>
		public const float RESEND_COOLDOWN = 1.0f;

		/// <summary>Baslangic senkronunda kayit siniri.</summary>
		public const int MAX_PER_PACKET = 24;

		class Entry {
			public string Path;
			public string Fsm;
			public string Var;
			public PlayMakerFSM FsmRef;
			public FsmFloat VarRef;
			/// <summary>
			/// PlayMaker degil C# kaynagi: Satsuma deposu (dnSpy kaniti:
			/// FuelTank.currentFuel; tank kapasiteye kelepcelenir, devirle
			/// bosalir). VarRef bosken bu okunur/yazilir.
			/// </summary>
			public FuelTank Tank;
			public float Last;
			public bool HasBaseline;
			public bool RemoteOwned;
			public float RemoteSetAt;
		}

		static readonly List<Entry> tracked = new List<Entry>(48);
		static readonly Dictionary<string, Entry> byKey =
			new Dictionary<string, Entry>(StringComparer.Ordinal);
		static readonly List<Entry> outbox = new List<Entry>(MAX_PER_PACKET);
		static float nextPoll;
		static bool discovered;

		/// <summary>Tekrar deneme: kayitli esyalar (sise) sahne yuklenisinden
		/// SONRA dogar; ilk taramada yoksa 30 sn'de bir sise turu atilir.</summary>
		public const float BOTTLE_RETRY = 30f;

		static bool bottlesBound;
		static float nextBottleRetry;

		/// <summary>
		/// Sise turu: ada gore sise bulup baglar. Bulunursa true (bir daha
		/// denenmez). Kayitli esya gec dogdugu icin ayri tur.
		/// </summary>
		static bool BottlePass() {
			GameObject[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
			catch { return bottlesBound; }
			if (all == null) return bottlesBound;
			int before = tracked.Count;
			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null) continue;
				string n = null;
				try { n = go.name; } catch { continue; }
				if (string.IsNullOrEmpty(n)) continue;
				if (n.IndexOf("MotorOil", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("BrakeFluid", StringComparison.OrdinalIgnoreCase) >= 0) {
					bottleHits++;
					BindVar(go, "Data", "Fluid");
					BindVar(go, "Use", "Fluid");
				}
			}
			if (tracked.Count > before) {
				bottlesBound = true;
				SmpLog.Log("fluid bottles appeared late, now tracking "
					+ tracked.Count + " variable(s).");
			}
			return bottlesBound;
		}

		/// <summary>Tanı sayaçları: ada göre kaç nesne eşleşti.</summary>
		static int bottleHits;
		static int triggerHits;
		static int engineHits;

		public static int TrackedCount { get { return tracked.Count; } }
		public static int SentUpdates;
		public static int AppliedUpdates;
		public static int Unresolved;

		static string Key(string path, string fsm, string var) {
			return path + "|" + fsm + "|" + var;
		}

		// ------------------------------------------------------------------
		// Kesif
		// ------------------------------------------------------------------

		/// <summary>
		/// Sivi sise/tetikleri ada gore, motor ici degerleri Satsuma alti
		/// kisitiyla, asinmayi degisken adiyla bulur. Dunya basina bir kez.
		/// </summary>
		public static void Discover() {
			Clear();
			if (SatsumaRegistry.Root == null) return;

			GameObject[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
			catch { }
			if (all == null) return;

			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null) continue;
				string n = null;
				try { n = go.name; } catch { continue; }
				if (string.IsNullOrEmpty(n)) continue;

				// Sise ve tetikler: sahnede birden cok olabilir (raf stogu +
				// satin alinan), hepsi ayri nesnedir, hepsi baglanir.
				// Siseler kayitli esya olarak GEC dogabilir; ayri tur +
				// tekrar deneme (BottlePass) onlar icin.
				if (n.IndexOf("MotorOil", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("BrakeFluid", StringComparison.OrdinalIgnoreCase) >= 0) {
					bottleHits++;
					BindVar(go, "Data", "Fluid");
					BindVar(go, "Use", "Fluid");
					continue;
				}
				// v15: "MotorOilTrigger"/"BrakeFluidTrigger" UYDURMAYDI,
				// dökümde 0 eşleşme. Gerçek kapak tetikleyicileri
				// (SATSUMA/.../OpenCap/CapTrigger_BrakeF gibi) yapı olarak
				// buraya girmez; bu dal artık mağaza ürünlerine bakıyor.
				if (string.Equals(n, "CapTrigger_MotorOil", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(n, "CapTrigger_BrakeF", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(n, "CapTrigger_BrakeR", StringComparison.OrdinalIgnoreCase)) {
					triggerHits++;
					BindVar(go, "Data", "Fluid");
					continue;
				}

				// Motor ici degerler: SADECE Satsuma altindakiler. Diger
				// araclarin yakiti zaten surucu paketleriyle akiyor; karisirsak
				// Ferndale'in deposu Satsuma'yi bozar.
				//
				// Yakit deposu NOTU (sahne taramasi kaniti): Satsuma altinda
				// adi "FuelTank" olan nesne YOK (o ad GIFU'ya ait). Depo
				// seviyesi, adi bilinmeyen bir Data FSM'indeki FuelLevel'dir;
				// o yuzden nesne adiyla degil, kapsam+degisken adiyla baglanir.
				if (string.Equals(n, "Combustion", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(n, "Cooling", StringComparison.OrdinalIgnoreCase)) {
					if (!IsUnderSatsuma(go)) continue;
					engineHits++;
					if (string.Equals(n, "Combustion", StringComparison.OrdinalIgnoreCase))
						BindVar(go, "Cylinders", "Oil");
					else
						BindVar(go, "Cooling", "WaterLevel");
					continue;
				}
			}

			BindSatsumaFuel();
			BindSatsumaFuelHunt();
			BindSatsumaTanks();
			BindGlobals();
			BindWear();

			// Siseler taramada baglanamadiysa kayitli esya henuz dogmamis
			// demektir; Tick 30 sn'de bir sise turu atar. Eslesme degil,
			// GERCEK baglanti aranir (adi tutan ama baglanamayan sise
			// tekrar denemeyi durdurmaz).
			bottlesBound = false;
			for (int i = 0; i < tracked.Count; i++) {
				string p = tracked[i] != null ? tracked[i].Path : null;
				if (string.IsNullOrEmpty(p)) continue;
				if (p.IndexOf("motor oil", StringComparison.OrdinalIgnoreCase) >= 0
					|| p.IndexOf("brake fluid", StringComparison.OrdinalIgnoreCase) >= 0) {
					bottlesBound = true;
					break;
				}
			}
			if (!bottlesBound) nextBottleRetry = Time.time + BOTTLE_RETRY;

			discovered = true;
			SmpLog.Log("fluids+wear: tracking " + tracked.Count + " variable(s)."
				+ " name hits: bottles=" + bottleHits + " triggers=" + triggerHits
				+ " engine=" + engineHits);
		}

		/// <summary>
		/// Satsuma yakit deposu: Satsuma kokunun altindaki, Data FSM'inde
		/// yakit degiskeni olan nesne. "FuelLevel"/"Fuel" her Data'da aranir
		/// (ayirt edici adlar); "Level" cok genel oldugu icin yalnizca adi
		/// yakit/depo cagristiran nesnelerde aranir. TAM eslesme sart.
		/// Nesne adi tek basina aranmaz (sahnede "FuelTank" Satsuma'da yok).
		/// </summary>
		static readonly string[] FUEL_NAMES = { "FuelLevel", "Fuel" };

		static void BindSatsumaFuel() {
			PlayMakerFSM[] fsms = null;
			try { fsms = SatsumaRegistry.Root.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch { return; }
			if (fsms == null) return;
			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null) continue;
				string fn = null;
				try { fn = fsm.FsmName; } catch { continue; }
				if (!string.Equals(fn, "Data", StringComparison.Ordinal)) continue;
				GameObject go = null;
				try { go = fsm.gameObject; } catch { continue; }
				if (go == null) continue;
				for (int k = 0; k < FUEL_NAMES.Length; k++) {
					if (TryAddNamed(go, fsm, FUEL_NAMES[k])) break;
				}
				// "Level": yalnizca yakit/depo adli nesnelerde.
				string n = null;
				try { n = go.name; } catch { continue; }
				if (n != null && (n.IndexOf("fuel", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("tank", StringComparison.OrdinalIgnoreCase) >= 0)) {
					TryAddNamed(go, fsm, "Level");
				}
			}
		}

		/// <summary>Degiskeni tam adiyla baglar (bulunamazsa false).</summary>
		static bool TryAddNamed(GameObject go, PlayMakerFSM fsm, string varName) {
			FsmFloat v = FsmVars.Float(fsm, varName);
			if (v == null) return false;
			string vn = null;
			try { vn = v.Name; } catch { return false; }
			if (!string.Equals(vn, varName, StringComparison.Ordinal)) return false;
			Add(go, fsm, v);
			return true;
		}

		/// <summary>
		/// Satsuma depo bileşenleri: FuelTank.currentFuel (C# MonoBehaviour,
		/// PlayMaker degiskeni degil). Ada bakilmaz, kok altindaki tum
		/// FuelTank'lar baglanir. Setter 0-kapasite arasi kelepceler.
		/// </summary>
		static void BindSatsumaTanks() {
			FuelTank[] tanks = null;
			try { tanks = SatsumaRegistry.Root.GetComponentsInChildren<FuelTank>(true); }
			catch { return; }
			if (tanks == null) return;
			for (int i = 0; i < tanks.Length; i++) {
				FuelTank t = tanks[i];
				if (t == null) continue;
				GameObject go = null;
				try { go = t.gameObject; } catch { continue; }
				if (go == null) continue;
				string path = null;
				try { path = SceneDump.PathOf(go.transform); } catch { continue; }
				if (string.IsNullOrEmpty(path)) continue;
				string k = Key(path, "FuelTank", "currentFuel");
				if (byKey.ContainsKey(k)) continue;
				Entry e = new Entry();
				e.Path = path;
				e.Fsm = "FuelTank";
				e.Var = "currentFuel";
				e.Tank = t;
				try { e.Last = t.currentFuel; } catch { e.Last = 0f; }
				tracked.Add(e);
				byKey[k] = e;
			}
		}

		/// <summary>Kaynaktan o anki deger (tank ya da FSM).</summary>
		static float ReadOf(Entry e, out bool ok) {
			ok = false;
			try {
				if (e.Tank != null) { ok = true; return e.Tank.currentFuel; }
				if (e.VarRef != null) { ok = true; return e.VarRef.Value; }
			}
			catch { }
			return 0f;
		}

		/// <summary>Kaynaga yazar (tank setter'i kelepceler).</summary>
		static void WriteOf(Entry e, float value) {
			try {
				if (e.Tank != null) { e.Tank.currentFuel = value; return; }
				if (e.VarRef != null) e.VarRef.Value = value;
			}
			catch { }
		}

		/// <summary>
		/// PlayMaker GLOBAL cüzdanı: float PlayerMoney (canlı kanıt:
		/// `smp globals money` gerçek parayı gösterdi). Yol "@GLOBAL" ile
		/// ayni pakette tasinir; alici ayni yoldan globale yazar.
		/// </summary>
		static void BindGlobals() {
			HutongGames.PlayMaker.FsmVariables globals = null;
			try { globals = HutongGames.PlayMaker.FsmVariables.GlobalVariables; }
			catch { return; }
			if (globals == null || globals.FloatVariables == null) return;
			for (int i = 0; i < globals.FloatVariables.Length; i++) {
				FsmFloat v = globals.FloatVariables[i];
				if (v == null) continue;
				string vn = null;
				try { vn = v.Name; } catch { continue; }
				if (!string.Equals(vn, "PlayerMoney", StringComparison.Ordinal)) continue;
				string k = Key("@GLOBAL", "", vn);
				if (byKey.ContainsKey(k)) continue;
				Entry e = new Entry();
				e.Path = "@GLOBAL";
				e.Fsm = "";
				e.Var = vn;
				e.VarRef = v;
				try { e.Last = v.Value; } catch { e.Last = 0f; }
				tracked.Add(e);
				byKey[k] = e;
			}
		}

		/// <summary>
		/// Yıpranma otomatik keşfi: adi Use/Data olan her FSM'de tam "Wear"
		/// lastik...): desen tutan her sey baglanir, yeni parca da gelir.
		/// </summary>
		static void BindWear() {
			PlayMakerFSM[] fsms = null;
			try { fsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>(); }
			catch { return; }
			if (fsms == null) return;
			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null) continue;
				string fn = null;
				try { fn = fsm.FsmName; } catch { continue; }
				if (!string.Equals(fn, "Use", StringComparison.Ordinal)
					&& !string.Equals(fn, "Data", StringComparison.Ordinal)) continue;
				FsmFloat w = FsmVars.Float(fsm, "Wear");
				// TAM ad sart: "RandomWear" (dukkan) gibi benzer adlar
				// asinma degildir, tabloya girmez.
				if (w == null || !string.Equals(w.Name, "Wear", StringComparison.Ordinal)) continue;
				GameObject go = null;
				try { go = fsm.gameObject; } catch { continue; }
				if (go == null) continue;
				Add(go, fsm, w);
			}
		}

		static void BindVar(GameObject go, string fsmName, string varName) {
			PlayMakerFSM[] fsms = null;
			try { fsms = go.GetComponents<PlayMakerFSM>(); } catch { return; }
			if (fsms == null) return;
			for (int i = 0; i < fsms.Length; i++) {
				if (fsms[i] == null) continue;
				string fn = null;
				try { fn = fsms[i].FsmName; } catch { continue; }
				if (!string.Equals(fn, fsmName, StringComparison.OrdinalIgnoreCase)) continue;
				FsmFloat v = FsmVars.Float(fsms[i], varName);
				// FsmVars yedek adla eslesme yapar ("Wear" -> "RandomWear"
				// gibi); burada TAM ad sart — yoksa dukkan RNG'si gibi
				// alakasiz degiskenler tabloya girer (log kaniti).
				if (v != null && !string.Equals(v.Name, varName, StringComparison.Ordinal)) continue;
				if (v != null) Add(go, fsms[i], v);
			}
		}

		/// <summary>
		/// Satsuma yakit deposu AVCI: Satsuma altindaki HER FSM'de (adi ne
		/// olursa olsun) adinda "fuel" gecen float. Depo Data/FuelLevel
		/// degilmis (warnlist: Satsuma'daki tum Data'lar iskarta); ibre,
		/// boru, pompa gibi adaylar degerleriyle Report'ta gorunur, dogru
		/// olan degerden anlasilir (depo ~X litre).
		/// </summary>
		static void BindSatsumaFuelHunt() {
			PlayMakerFSM[] fsms = null;
			try { fsms = SatsumaRegistry.Root.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch { return; }
			if (fsms == null) return;
			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null) continue;
				HutongGames.PlayMaker.FsmVariables vars = null;
				try { vars = fsm.FsmVariables; } catch { continue; }
				if (vars == null || vars.FloatVariables == null) continue;
				GameObject go = null;
				try { go = fsm.gameObject; } catch { continue; }
				if (go == null) continue;
				for (int k = 0; k < vars.FloatVariables.Length; k++) {
					FsmFloat v = vars.FloatVariables[k];
					if (v == null) continue;
					string vn = null;
					try { vn = v.Name; } catch { continue; }
					if (string.IsNullOrEmpty(vn)) continue;
					if (vn.IndexOf("fuel", StringComparison.OrdinalIgnoreCase) < 0) continue;
					Add(go, fsm, v);
				}
			}
		}

		static void Add(GameObject go, PlayMakerFSM fsm, FsmFloat v) {
			string path = null;
			string fn = null;
			string vn = null;
			try {
				path = SceneDump.PathOf(go.transform);
				fn = fsm.FsmName;
				vn = v.Name;
			}
			catch { return; }
			if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(vn)) return;
			string k = Key(path, fn ?? "", vn);
			if (byKey.ContainsKey(k)) return;
			Entry e = new Entry();
			e.Path = path;
			e.Fsm = fn ?? "";
			e.Var = vn;
			e.FsmRef = fsm;
			e.VarRef = v;
			try { e.Last = v.Value; } catch { e.Last = 0f; }
			tracked.Add(e);
			byKey[k] = e;
		}

		/// <summary>Nesne Satsuma kokunun altinda mi (yakit/motor/su icin).</summary>
		static bool IsUnderSatsuma(GameObject go) {
			try {
				Transform root = SatsumaRegistry.Root.transform;
				Transform t = go.transform;
				int guard = 0;
				while (t != null && guard++ < 64) {
					if (t == root) return true;
					t = t.parent;
				}
			}
			catch { }
			return false;
		}

		public static void Clear() {
			tracked.Clear();
			byKey.Clear();
			outbox.Clear();
			discovered = false;
			bottleHits = 0;
			triggerHits = 0;
			engineHits = 0;
			bottlesBound = false;
			nextBottleRetry = 0f;
		}

		// ------------------------------------------------------------------
		// Kare basina
		// ------------------------------------------------------------------

		/// <summary>Degisiklikleri tarar ve gonderir (0.5 sn'de bir).</summary>
		public static void Tick() {
			try {
				if (!discovered || tracked.Count == 0) return;
				if (!SteamTransport.HasRemote) return;

				float now = Time.time;
				if (now < nextPoll) return;
				nextPoll = now + POLL_INTERVAL;

				// Gec dogan siseler: baglanana kadar 30 sn'de bir sise turu.
				if (!bottlesBound && now >= nextBottleRetry) {
					nextBottleRetry = now + BOTTLE_RETRY;
					try { BottlePass(); } catch { }
				}

				outbox.Clear();
				for (int i = 0; i < tracked.Count && outbox.Count < MAX_PER_PACKET; i++) {
					Entry e = tracked[i];
					if (e == null) continue;
					if (e.Tank == null && e.VarRef == null) continue;
					if (e.RemoteOwned && now - e.RemoteSetAt < RESEND_COOLDOWN) continue;
					bool ok;
					float v = ReadOf(e, out ok);
					if (!ok) continue;
					if (!e.HasBaseline || Math.Abs(v - e.Last) > EPSILON) {
						e.Last = v;
						e.HasBaseline = true;
						e.RemoteOwned = false;
						outbox.Add(e);
					}
				}
				if (outbox.Count > 0) Flush();
			}
			catch (Exception ex) {
				SmpLog.Warning("fluid tick failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		static void Flush() {
			try {
				// Gonderim aninda olen kayit sayiya dahil edilmez (sayi once
				// yazildigi icin atlama paketi bozardi; o yuzden once suzulur).
				for (int i = outbox.Count - 1; i >= 0; i--) {
					Entry e = outbox[i];
					if (e == null) { outbox.RemoveAt(i); continue; }
					bool ok;
					ReadOf(e, out ok);
					if (!ok) outbox.RemoveAt(i);
				}
				if (outbox.Count == 0) return;
				MemoryStream s = Protocol.Begin(Protocol.Msg.FluidState);
				BinaryWriter w = new BinaryWriter(s);
				w.Write((byte)outbox.Count);
				for (int i = 0; i < outbox.Count; i++) {
					Entry e = outbox[i];
					w.Write(e.Path);
					w.Write(e.Fsm);
					w.Write(e.Var);
					bool ok;
					float v = ReadOf(e, out ok);
					w.Write(ok ? v : e.Last);
				}
				w.Flush();
				if (SteamTransport.SendReliable(Protocol.Finish(s))) SentUpdates += outbox.Count;
			}
			catch (Exception e) {
				SmpLog.Warning("fluid send failed: " + e.Message);
			}
			finally { outbox.Clear(); }
		}

		// ------------------------------------------------------------------
		// Alma
		// ------------------------------------------------------------------

		/// <summary>Gelen sivi/asinma paketini uygular (yol + fsm + ad ile).</summary>
		public static void OnMessage(BinaryReader r, byte count) {
			for (int n = 0; n < count; n++) {
				try {
					string path = r.ReadString();
					string fsm = r.ReadString();
					string var = r.ReadString();
					float value = r.ReadSingle();

					Entry e;
					if (!byKey.TryGetValue(Key(path, fsm, var), out e) || e == null) {
						Unresolved++;
						continue;
					}
					EchoGuard.Scope scope = EchoGuard.Enter();
					try {
						WriteOf(e, value);
						e.Last = value;
						e.HasBaseline = true;
					}
					finally { scope.Dispose(); }

					e.RemoteOwned = true;
					e.RemoteSetAt = Time.time;
					AppliedUpdates++;
				}
				catch (Exception) {
					Telemetry.BadPackets++;
					SmpLog.Warning("fluid packet rejected.");
					return;
				}
			}
		}

		/// <summary>Baslangic: tum kayitlari bir kez gonderir.</summary>
		public static void SendAll() {
			try {
				if (!discovered || tracked.Count == 0 || !SteamTransport.HasRemote) return;
				outbox.Clear();
				for (int i = 0; i < tracked.Count && outbox.Count < MAX_PER_PACKET; i++) {
					Entry e = tracked[i];
					if (e == null) continue;
					if (e.Tank == null && e.VarRef == null) continue;
					bool ok;
					float v = ReadOf(e, out ok);
					if (!ok) continue;
					e.Last = v;
					e.HasBaseline = true;
					e.RemoteOwned = false;
					outbox.Add(e);
				}
				if (outbox.Count > 0) Flush();
			}
			catch (Exception e) {
				SmpLog.Warning("fluid initial sync failed: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Tani
		// ------------------------------------------------------------------

		public static string Describe() {
			if (!discovered) return "fluids+wear: not scanned";
			return "fluids+wear: " + tracked.Count + " tracked, sent " + SentUpdates
				+ ", applied " + AppliedUpdates + ", unresolved " + Unresolved;
		}

		/// <summary>'smp fluids': bagli degiskenleri degerleriyle doker.</summary>
		public static void Report() {
			if (!discovered) { SmpLog.Print("fluids+wear: not scanned. Open a save first."); return; }
			SmpLog.Print(Describe());
			int shown = 0;
			for (int i = 0; i < tracked.Count && shown < 40; i++) {
				Entry e = tracked[i];
				if (e == null) continue;
				bool ok;
				float v = ReadOf(e, out ok);
				if (!ok) v = float.NaN;
				string shortPath = e.Path;
				int slash = shortPath.LastIndexOf('/');
				if (slash >= 0) shortPath = shortPath.Substring(slash + 1);
				SmpLog.Print("  " + shortPath + "/" + e.Fsm + "." + e.Var
					+ " = " + v.ToString("F3"));
				shown++;
			}
			if (tracked.Count > shown) SmpLog.Print("  ... " + (tracked.Count - shown) + " more.");
		}
	}
}
