// EKONOMİ SENKRONİZASYONU - Para, Teimo market, yakıt pompası
//
// NEDEN AYRI MODÜL
// -------------------
// FluidWearSync pattern'i birebir aynen kullanılır: güvenilir kanal,
// 0.5 Hz polling, değişiklikte gönderim. Fark:
//   * Para: PlayMaker GLOBAL float (PlayerMoney) - @GLOBAL yolu
//   * Teimo/Market: stok/puan FSM'leri (satın alma sonrası değişir)
//   * Yakıt pompası: pompa depoları (fuel level FSM'leri)
//
// dnSpy kanıtı: Assembly-CSharp'te "Money" string'i C# tarafında YOK.
// Oyun para ve stokları PlayMaker float'larında tutar. Bu yüzden
// FluidWearSync'deki ObjectState pattern'i (path+fsm+var) tam uygulanır.
//
// Unity 5 / .NET 3.5 uyumlu.
using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP {

	public static class EconomySync {

		// ------------------------------------------------------------------
		// Ayarlar
		// ------------------------------------------------------------------

		/// <summary>Değişim tarama aralığı (saniye). Ekonomi yavaş değişir.</summary>
		public const float POLL_INTERVAL = 0.5f;

		/// <summary>Float hassasiyeti (para tam sayı, 0.5 yeterli).</summary>
		public const float EPSILON = 0.5f;

		/// <summary>Aynı kayıt için yankı susturma.</summary>
		public const float RESEND_COOLDOWN = 1.0f;

		/// <summary>Başlangıç senkronunda kayıt başına paket limiti.</summary>
		public const int MAX_PER_PACKET = 16;

		// ------------------------------------------------------------------
		// Kayıt yapısı
		// ------------------------------------------------------------------

		class Entry {
			public string Path;        // nesne yolu (@GLOBAL, STORE/..., PERAJARVI/...)
			public string Fsm;         // FSM adı
			public string Var;         // değişken adı
			public PlayMakerFSM FsmRef;
			public FsmFloat VarRef;
			public float Last;         // son gönderilen değer
			public float NextSend;     // bir sonraki gönderim zamanı
		}

		static readonly List<Entry> tracked = new List<Entry>(64);
		static readonly Dictionary<string, Entry> byKey = new Dictionary<string, Entry>(64);
		static float nextPoll;

		// ------------------------------------------------------------------
		// Anahtar üretimi
		// ------------------------------------------------------------------

		static string Key(string path, string fsm, string var) {
			return path + "|" + fsm + "|" + var;
		}

		// ------------------------------------------------------------------
		// Keşif
		// ------------------------------------------------------------------

		/// <summary>
		/// Ekonomi FSM'lerini keşfeder. Dünya hazır olduğunda bir kez çağrılır.
		/// </summary>
		public static void Discover() {
			tracked.Clear();
			byKey.Clear();
			nextPoll = 0f;

			// 1) Global para: PlayerMoney
			BindGlobalMoney();

			// 2) Teimo market stoğu / nakit
			BindTeimo();

			// 3) Yakıt pompaları (PERAJARVI, STORE, REPAIRSHOP)
			BindFuelPumps();

			SmpLog.Print("economy: discovered " + tracked.Count + " entries");
		}

		/// <summary>Global PlayerMoney değişkenini bağlar.</summary>
		static void BindGlobalMoney() {
			FsmVariables globals = null;
			try { globals = FsmVariables.GlobalVariables; }
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
		/// Teimo market: kasa fiyatı, stok sayacı, alım miktarları.
		///
		/// V15 DÜZELTMESİ -- eski adların üçü de uydurmaydı:
		///   Cash / Money  -> Register[Data] içinde YOK
		///                   (gerçek float'lar: Price, Fuel, Items,
		///                    Spraycans, float1, float2)
		///   Stock / ItemCount -> Inventory[Logic] içinde YOK
		///                   (gerçek: Quantity, QuantityBought,
		///                    RestockDay, PriceMultiplier, Inflationrate)
		///   STORE/LOD/ActivateStore[Data] -> o nesnede hiç FSM YOK;
		///                   altında 48 adet Buy/Use var. Hiçbir zaman
		///                   çözülemeyen bir bağlantıydı.
		/// </summary>
		static void BindTeimo() {
			// Kasa: satış fiyatı, yakıt, kalem sayacı, sprey sayaci
			BindVar("STORE/StoreCashRegister/Register", "Data", "Price");
			BindVar("STORE/StoreCashRegister/Register", "Data", "Fuel");
			BindVar("STORE/StoreCashRegister/Register", "Data", "Items");
			BindVar("STORE/StoreCashRegister/Register", "Data", "Spraycans");

			// Envanter: raflardaki miktar ve alınan miktar
			BindVar("STORE/Inventory", "Logic", "Quantity");
			BindVar("STORE/Inventory", "Logic", "QuantityBought");
			BindVar("STORE/Inventory", "Logic", "RestockDay");
			BindVar("STORE/Inventory", "Logic", "PriceMultiplier");
		}

		/// <summary>
		/// Yakıt pompası depoları. Asset'ten tespit edilen yollar:
		///   PERAJARVI/Building/PetrolStation/...
		///   STORE/LOD/FuelPump/...
		///   REPAIRSHOP/...
		/// Değişken adları: FuelLevel, Fuel, Level, Amount, Liters
		/// </summary>
		static void BindFuelPumps() {
			// v15: "PERAJARVI" çıkarıldı -- o kökte hiç pomp FSM'i yok.
			// Gerçek üç pompa:
			//   STORE/LOD/Pumps/GasolinePumpD / PumpPÖ / Pump98
			//     /Functions/TriggerD / TriggerPÖ / Trigger98   [Use]
			string[] pumpRoots = {
				"STORE",
				"REPAIRSHOP"
			};

			string[] fuelVarNames = { "FuelLevel", "Fuel", "Level", "Amount", "Liters", "CurrentFuel" };

			foreach (string root in pumpRoots) {
				GameObject go = GameObject.Find(root);
				if (go == null) continue;

				PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
				if (fsms == null) continue;

				for (int i = 0; i < fsms.Length; i++) {
					PlayMakerFSM fsm = fsms[i];
					if (fsm == null) continue;
					string fn = null;
					try { fn = fsm.FsmName; } catch { continue; }

					// Sadece ilgili FSM isimlerini al (Data, Use, Logic, Pump, FuelPump)
					if (!string.Equals(fn, "Data", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(fn, "Use", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(fn, "Logic", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(fn, "Pump", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(fn, "FuelPump", StringComparison.OrdinalIgnoreCase)) continue;

					foreach (string varName in fuelVarNames) {
						FsmFloat v = FsmVars.Float(fsm, varName);
						if (v == null) continue;
						if (!string.Equals(v.Name, varName, StringComparison.Ordinal)) continue;

						string path = GetPath(fsm.gameObject);
						if (string.IsNullOrEmpty(path)) continue;

						string k = Key(path, fn, varName);
						if (byKey.ContainsKey(k)) continue;

						Entry e = new Entry();
						e.Path = path;
						e.Fsm = fn;
						e.Var = varName;
						e.FsmRef = fsm;
						e.VarRef = v;
						try { e.Last = v.Value; } catch { e.Last = 0f; }
						tracked.Add(e);
						byKey[k] = e;
					}
				}
			}
		}

		/// <summary>Belirli bir nesne+FSM+değişken kombinasyonunu bağlar.</summary>
		static void BindVar(string path, string fsmName, string varName) {
			GameObject go = GameObject.Find(path);
			if (go == null) return;

			PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
			if (fsms == null) return;

			for (int i = 0; i < fsms.Length; i++) {
				if (fsms[i] == null) continue;
				string fn = null;
				try { fn = fsms[i].FsmName; } catch { continue; }
				if (!string.Equals(fn, fsmName, StringComparison.OrdinalIgnoreCase)) continue;

				FsmFloat v = FsmVars.Float(fsms[i], varName);
				if (v == null) continue;
				if (!string.Equals(v.Name, varName, StringComparison.Ordinal)) continue;

				string k = Key(path, fn, varName);
				if (byKey.ContainsKey(k)) continue;

				Entry e = new Entry();
				e.Path = path;
				e.Fsm = fn;
				e.Var = varName;
				e.FsmRef = fsms[i];
				e.VarRef = v;
				try { e.Last = v.Value; } catch { e.Last = 0f; }
				tracked.Add(e);
				byKey[k] = e;
			}
		}

		// ------------------------------------------------------------------
		// Yardımcılar
		// ------------------------------------------------------------------

		/// <summary>Nesnenin sahne yolunu hesaplar (Resources.FindObjectsOfTypeAll fallback).</summary>
		static string GetPath(GameObject go) {
			if (go == null) return null;
			List<string> parts = new List<string>(8);
			Transform t = go.transform;
			int guard = 0;
			while (t != null && guard < 16) {
				parts.Add(t.name);
				t = t.parent;
				guard++;
			}
			if (parts.Count == 0) return null;
			parts.Reverse();
			return string.Join("/", parts.ToArray());
		}

		// ------------------------------------------------------------------
		// Polling & Gönderim
		// ------------------------------------------------------------------

		/// <summary>Kare başına çağrılır. Değişen değerleri paketler.</summary>
		public static void Tick() {
			if (!SteamTransport.HasRemote) return;
			float now = Time.time;
			if (now < nextPoll) return;
			nextPoll = now + POLL_INTERVAL;

			List<Entry> dirty = null;
			for (int i = 0; i < tracked.Count; i++) {
				Entry e = tracked[i];
				if (e.VarRef == null) continue;
				float cur;
				try { cur = e.VarRef.Value; }
				catch { continue; }

				if (Math.Abs(cur - e.Last) > EPSILON && now >= e.NextSend) {
					if (dirty == null) dirty = new List<Entry>(8);
					dirty.Add(e);
					e.Last = cur;
					e.NextSend = now + RESEND_COOLDOWN;
				}
			}

			if (dirty != null && dirty.Count > 0) Send(dirty);
		}

		/// <summary>Değişen kayıtları paketler ve gönderir.</summary>
		static void Send(List<Entry> entries) {
			// v9: FluidState DEĞİL. FluidWearSync'in kayıtlarıyla aynı id'de
			// gönderiliyordu ama string kodlaması farklıydı (ushort uzunluk +
			// UTF8 bayt, vs FluidWearSync'in 7-bit önekli Write(string)'i),
			// yani alıcı taraf her pakette akışı kaydırıyordu.
			using (MemoryStream ms = Protocol.Begin(Protocol.Msg.EconomyState)) {
				BinaryWriter w = new BinaryWriter(ms);
				w.Write((byte)entries.Count);
				for (int i = 0; i < entries.Count; i++) {
					Entry e = entries[i];
					WriteString(w, e.Path);
					WriteString(w, e.Fsm);
					WriteString(w, e.Var);
					w.Write(e.Last);
				}
				SteamTransport.SendReliable(Protocol.Finish(ms));
			}
		}

		// ------------------------------------------------------------------
		// Alma (Diğer taraftan gelen paket)
		// ------------------------------------------------------------------

		/// <summary>Gelen FluidState paketini uygular (para, yakıt, stok).</summary>
		public static void OnMessage(BinaryReader r) {
			try {
				int count = r.ReadByte();
				for (int i = 0; i < count; i++) {
					string path = ReadString(r);
					string fsm = ReadString(r);
					string var = ReadString(r);
					float value = r.ReadSingle();

					Entry e;
					if (!byKey.TryGetValue(Key(path, fsm, var), out e)) {
						// Henüz keşfedilmemiş: dinamik ekle (JIP için)
						GameObject go = FindByPath(path);
						if (go == null) continue;
						PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
						if (fsms == null) continue;
						for (int j = 0; j < fsms.Length; j++) {
							if (fsms[j] == null) continue;
							string fn = null;
							try { fn = fsms[j].FsmName; } catch { continue; }
							if (!string.Equals(fn, fsm, StringComparison.OrdinalIgnoreCase)) continue;
							FsmFloat v = FsmVars.Float(fsms[j], var);
							if (v == null || !string.Equals(v.Name, var, StringComparison.Ordinal)) continue;
							e = new Entry();
							e.Path = path; e.Fsm = fn; e.Var = var;
							e.FsmRef = fsms[j]; e.VarRef = v;
							tracked.Add(e);
							byKey[Key(path, fsm, var)] = e;
							break;
						}
						if (e == null) continue;
					}

					// EchoGuard: uzaktan gelen değer yerel hook'ları tetiklemesin
					using (EchoGuard.Enter()) {
						if (e.VarRef != null) e.VarRef.Value = value;
						else if (string.Equals(e.Path, "@GLOBAL", StringComparison.Ordinal)) {
							// Global para: FsmVariables.GlobalVariables üzerinden yaz
							FsmVariables globals = null;
							try { globals = FsmVariables.GlobalVariables; } catch { }
							if (globals != null && globals.FloatVariables != null) {
								for (int j = 0; j < globals.FloatVariables.Length; j++) {
									FsmFloat gv = globals.FloatVariables[j];
									if (gv != null && string.Equals(gv.Name, var, StringComparison.Ordinal)) {
										gv.Value = value;
										break;
									}
								}
							}
						}
						e.Last = value;
					}
				}
			}
			catch (Exception ex) {
				SmpLog.Warning("economy receive failed: " + ex.Message);
			}
		}

		/// <summary>Sahne yolundan GameObject bulur.</summary>
		static GameObject FindByPath(string path) {
			if (string.IsNullOrEmpty(path)) return null;
			if (path == "@GLOBAL") return null;
			string[] parts = path.Split('/');
			GameObject cur = GameObject.Find(parts[0]);
			if (cur == null) return null;
			for (int i = 1; i < parts.Length; i++) {
				Transform t = cur.transform.Find(parts[i]);
				if (t == null) return null;
				cur = t.gameObject;
			}
			return cur;
		}

		// ------------------------------------------------------------------
		// String yardımcıları (FluidWearSync ile aynı)
		// ------------------------------------------------------------------

		static void WriteString(BinaryWriter w, string s) {
			if (s == null) s = string.Empty;
			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
			w.Write((ushort)bytes.Length);
			w.Write(bytes);
		}

		static string ReadString(BinaryReader r) {
			ushort len = r.ReadUInt16();
			if (len == 0) return string.Empty;
			byte[] bytes = r.ReadBytes(len);
			return System.Text.Encoding.UTF8.GetString(bytes);
		}

		// ------------------------------------------------------------------
		// Yaşam döngüsü
		// ------------------------------------------------------------------

		/// <summary>Dünya değişiminde sıfırlar.</summary>
		public static void Reset() {
			tracked.Clear();
			byKey.Clear();
			nextPoll = 0f;
		}

		/// <summary>Teşhis: 'smp status' ve 'smp globals money' için.</summary>
		public static string Describe() {
			int money = 0, fuel = 0, other = 0;
			for (int i = 0; i < tracked.Count; i++) {
				Entry e = tracked[i];
				if (e.Path == "@GLOBAL") money++;
				else if (e.Var != null && (e.Var.IndexOf("Fuel", StringComparison.OrdinalIgnoreCase) >= 0
					|| e.Var.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0)) fuel++;
				else other++;
			}
			return "economy: " + tracked.Count + " entries (money=" + money
				+ " fuel=" + fuel + " other=" + other + ")";
		}
	}
}