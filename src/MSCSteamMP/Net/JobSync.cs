// İŞLER (JOBS) SENKRONİZASYONU - Kilju, odun kesme, tamirhane işleri
//
// NEDEN AYRI MODÜL
// -------------------
// FluidWearSync / EconomySync pattern'i aynen kullanılır:
//   * Güvenilir kanal (ObjectState benzeri FluidState=43)
//   * 0.5 Hz polling, değişiklikte gönderim
//   * Yankı koruması (EchoGuard)
//
// FSM VERİSİ (level2 asset'ten okundu):
//   KILJU (JOBS/HouseDrunk/BeerCamp...):
//     - ITEMS/bucket(itemx): Level (State 1/2), Fill, Empty, Mass, Scale
//     - JOBS/HouseDrunk/BeerCamp/KiljuBuyer: EmptyBucket, Logic (CanTrigger)
//   ODUN (JOBS/HouseWood1):
//     - Logic (Check data), Use (PayMoney), LOD
//   TAMİRHANE (REPAIRSHOP/Jobs/*):
//     - Work FSM'leri (State 1/2/Wait): EngineFix, TireJob*, Brakes,
//       SuspensionRepair, Bodyfix, Paintjob, Windshield, RimPolish,
//       Rollcage, GearLinkage, GearRatios, EngineAdjust, EngineTune,
//       ToeAdjust, N2OBottleFill
//
// Bu FSM'lerin ortak özelliği: işin "durumu" (progress/complete/paid)
// bir FSM değişkeninde (int/float/bool) tutulur. State adları da
// işin hangi aşamasında olduğunu belirtir (Wait, Check data, State 1/2).
//
// Unity 5 / .NET 3.5 uyumlu.

using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP {

	public static class JobSync {

		// ------------------------------------------------------------------
		// Ayarlar
		// ------------------------------------------------------------------

		/// <summary>Değişim tarama aralığı (saniye). İşler yavaş ilerler.</summary>
		public const float POLL_INTERVAL = 0.5f;

		/// <summary>Float/Int hassasiyeti (tam sayılar için 0.5).</summary>
		public const float EPSILON = 0.5f;

		/// <summary>Aynı kayıt için yankı susturma.</summary>
		public const float RESEND_COOLDOWN = 1.0f;

		/// <summary>Başlangıç senkronunda kayıt başına paket limiti.</summary>
		public const int MAX_PER_PACKET = 16;

		// ------------------------------------------------------------------
		// Kayıt yapısı - hem float hem int hem bool destekler
		// ------------------------------------------------------------------

		enum VarType { Float, Int, Bool }

		class Entry {
			public string Path;
			public string Fsm;
			public string Var;
			public VarType Type;
			public PlayMakerFSM FsmRef;
			public FsmFloat FloatRef;
			public FsmInt IntRef;
			public FsmBool BoolRef;
			public float LastFloat;
			public int LastInt;
			public bool LastBool;
			public float NextSend;
		}

		static readonly List<Entry> tracked = new List<Entry>(128);
		static readonly Dictionary<string, Entry> byKey = new Dictionary<string, Entry>(128);
		static float nextPoll;

		/// <summary>Çözülemeyen kayıt ('smp status' bunu gösterir).</summary>
		public static int Unresolved;
		/// <summary>Uygulanan kayıt.</summary>
		public static int Applied;

		// ------------------------------------------------------------------
		// Anahtar üretimi
		// ------------------------------------------------------------------

		static string Key(string path, string fsm, string var) {
			return path + "|" + fsm + "|" + var;
		}

		// ------------------------------------------------------------------
		// Keşif
		// ------------------------------------------------------------------

		/// <summary>İş FSM'lerini keşfeder. Dünya hazır olduğunda bir kez çağrılır.</summary>
		public static void Discover() {
			tracked.Clear();
			byKey.Clear();
			nextPoll = 0f;

			// 1) KILJU:_bucket FSM'leri (malzemeler, fermentasyon seviyesi)
			BindKiljuBuckets();

			// 2) KILJU: Satıcı/trigger FSM'leri
			BindKiljuTriggers();

			// 3) ODUN KESME: HouseWood1

			// 4) TAMİRHANE: Tüm Work FSM'leri
			BindRepairShopJobs();

			SmpLog.Print("jobs: discovered " + tracked.Count + " entries");
		}

		/// <summary>Kilju kovası FSM'leri: Level, Fill, Empty, Mass, Scale</summary>
		static void BindKiljuBuckets() {
			// ITEMS/bucket(itemx) altındaki tüm FSM'ler
			GameObject go = GameObject.Find("ITEMS");
			if (go == null) return;

			PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
			if (fsms == null) return;

			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null) continue;

				// Sadece bucket FSM'leri
				Transform t = fsm.transform;
				bool isBucket = false;
				while (t != null) {
					if (t.name.IndexOf("bucket", StringComparison.OrdinalIgnoreCase) >= 0) {
						isBucket = true;
						break;
					}
					t = t.parent;
				}
				if (!isBucket) continue;

				string fn = null;
				try { fn = fsm.FsmName; } catch { continue; }

			// Kilju bucket değişkenleri (asset'ten tespit edildi)
			string[] vars = { "Level", "Fill", "Empty", "Mass", "Scale" };
				foreach (string varName in vars) {
					BindVarOfType(fsm, fn, varName);
				}
			}
		}

		/// <summary>Kilju satıcı ve trigger FSM'leri</summary>
		static void BindKiljuTriggers() {
			// v15: "KiljuBuyer"[Logic] diye bir FSM YOK. Dökümde KiljuBuyer
			// üzerinde EmptyBucket / Look / Move / Angry var; "Logic" FSM'i
			// bir alt nesne olan CanTrigger'da.
			BindVar("JOBS/HouseDrunk/BeerCampOld/BeerCamp/KiljuBuyer", "EmptyBucket", "Idle");
			BindVar("JOBS/HouseDrunk/BeerCampOld/BeerCamp/KiljuBuyer/CanTrigger", "Logic", "Money");

			// CanTrigger[PlayerDrink] bir "State" DEGISKENI tasimaz; "State 1",
			// "State 2", "State 3" DURUM adidir. Bu bag hicbir zaman bos
			// gecmisti, kaldirildi.

			// BeerCamp LOD
			BindVar("JOBS/HouseDrunk/BeerCampOld/BeerCamp", "OpeningHours", "Activate");
		}

		// v15: BindWoodJob SİLİNDİ.
		//
		// Hem null FSM geçiriyordu (FsmVars.* null'da null döner -> hiçbir
		// şey bağlanmazdı) hem de aradığı State/Progress/Paid/Complete
		// adları dökümde yok: JOBS/HouseWood1[Logic] yalnız UniqueTagOrder /
		// JOBDONE / ORDER / Is day / Night taşıyor, ve HouseWood1'de
		// "Use" FSM'i hiç yok.

		/// <summary>Tamirhane işleri: REPAIRSHOP/Jobs/* altındaki Work FSM'leri</summary>
		static void BindRepairShopJobs() {
			GameObject go = GameObject.Find("REPAIRSHOP");
			if (go == null) return;

			PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
			if (fsms == null) return;

			// İş FSM isimleri (asset'ten tespit edildi)
			HashSet<string> jobFsmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
				"Work", "BoltCheck", "Assembly", "Removal", "Screw"
			};

			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null) continue;
				string fn = null;
				try { fn = fsm.FsmName; } catch { continue; }

				if (!jobFsmNames.Contains(fn)) continue;

				// Jobs klasörü altında mı?
				Transform t = fsm.transform;
				bool underJobs = false;
				while (t != null) {
					if (t.name.IndexOf("Jobs", StringComparison.OrdinalIgnoreCase) >= 0) {
						underJobs = true;
						break;
					}
					t = t.parent;
				}
				if (!underJobs) continue;

				// v15: bu yedinin HİÇBİRİ 19 adet REPAIRSHOP/Jobs/*[Work]
				// FSM'inin içinde geçmiyor (durumları Wait/Disable/Fix2/
				// WORK/Fix; gerçek veri REPAIRSHOP/Order[Data]'da).
				// Gerçek sipariş bayrakları:
				//   MetalBody, MetalBootlid, MetalBumperFront/Rear,
				//   MetalDoorLeft/Right, MetalFenderLeft/Right,
				//   MetalGrille, MetalHood
				string[] jobVars = { "Money", "Price", "Time", "Amount" };
				foreach (string varName in jobVars) {
					BindVarOfType(fsm, fn, varName);
				}
			}
		}

		/// <summary>Belirli bir FSM'deki değişkeni türüne göre bağlar.</summary>
		static void BindVarOfType(PlayMakerFSM fsm, string fsmName, string varName) {
			// Float
			FsmFloat fv = FsmVars.Float(fsm, varName);
			if (fv != null && string.Equals(fv.Name, varName, StringComparison.Ordinal)) {
				AddEntry(fsm, fsmName, varName, VarType.Float, fv, null, null, fv.Value, 0, false);
				return;
			}

			// Int
			FsmInt iv = FsmVars.Int(fsm, varName);
			if (iv != null && string.Equals(iv.Name, varName, StringComparison.Ordinal)) {
				AddEntry(fsm, fsmName, varName, VarType.Int, null, iv, null, 0f, iv.Value, false);
				return;
			}

			// Bool
			FsmBool bv = FsmVars.Bool(fsm, varName);
			if (bv != null && string.Equals(bv.Name, varName, StringComparison.Ordinal)) {
				AddEntry(fsm, fsmName, varName, VarType.Bool, null, null, bv, 0f, 0, bv.Value);
				return;
			}
		}

		/// <summary>Belirli bir nesne+FSM+değişken kombinasyonunu türüne göre bağlar.</summary>
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

				BindVarOfType(fsms[i], fn, varName);
			}
		}

		/// <summary>Kayıt ekler (türüne göre).</summary>
		static void AddEntry(PlayMakerFSM fsm, string fsmName, string varName, VarType type,
			FsmFloat fv = null, FsmInt iv = null, FsmBool bv = null, float fval = 0f, int ival = 0, bool bval = false) {

			string path = GetPath(fsm.gameObject);
			if (string.IsNullOrEmpty(path)) return;

			string k = Key(path, fsmName, varName);
			if (byKey.ContainsKey(k)) return;

			Entry e = new Entry();
			e.Path = path;
			e.Fsm = fsmName;
			e.Var = varName;
			e.Type = type;
			e.FsmRef = fsm;
			e.FloatRef = fv;
			e.IntRef = iv;
			e.BoolRef = bv;
			e.LastFloat = fval;
			e.LastInt = ival;
			e.LastBool = bval;
			tracked.Add(e);
			byKey[k] = e;
		}

		// ------------------------------------------------------------------
		// Yardımcılar
		// ------------------------------------------------------------------

		/// <summary>Nesnenin sahne yolunu hesaplar.</summary>
		static string GetPath(GameObject go) {
			if (go == null) return null;
			List<string> parts = new List<string>(12);
			Transform t = go.transform;
			int guard = 0;
			while (t != null && guard < 20) {
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
				bool changed = false;

				try {
					switch (e.Type) {
						case VarType.Float:
							if (e.FloatRef != null) {
								float cur = e.FloatRef.Value;
								if (Math.Abs(cur - e.LastFloat) > EPSILON && now >= e.NextSend) changed = true;
							}
							break;
						case VarType.Int:
							if (e.IntRef != null) {
								int cur = e.IntRef.Value;
								if (Math.Abs(cur - e.LastInt) > EPSILON && now >= e.NextSend) changed = true;
							}
							break;
						case VarType.Bool:
							if (e.BoolRef != null) {
								bool cur = e.BoolRef.Value;
								if (cur != e.LastBool && now >= e.NextSend) changed = true;
							}
							break;
					}
				}
				catch { continue; }

				if (changed) {
					if (dirty == null) dirty = new List<Entry>(16);
					dirty.Add(e);
					e.NextSend = now + RESEND_COOLDOWN;

					// Değeri güncelle
					switch (e.Type) {
						case VarType.Float: e.LastFloat = e.FloatRef.Value; break;
						case VarType.Int: e.LastInt = e.IntRef.Value; break;
						case VarType.Bool: e.LastBool = e.BoolRef.Value; break;
					}
				}
			}

			if (dirty != null && dirty.Count > 0) Send(dirty);
		}

		/// <summary>Değişen kayıtları paketler ve gönderir (v9: kendi JobState mesajı).</summary>
		static void Send(List<Entry> entries) {
			// v9: FluidState DEĞİL. Kayıtlar FluidWearSync'inkinden iki yönde
			// ayrışıyordu: string kodlaması farklı (ushort + UTF8 vs 7-bit önek)
			// ve arada bir tip baytı var. Aynı id'de göndermek, alıcı tarafın
			// FluidWearSync/EconomySync okuyucularını bozuyordu.
			using (MemoryStream ms = Protocol.Begin(Protocol.Msg.JobState)) {
				BinaryWriter w = new BinaryWriter(ms);
				w.Write((byte)entries.Count);
				for (int i = 0; i < entries.Count; i++) {
					Entry e = entries[i];
					WriteString(w, e.Path);
					WriteString(w, e.Fsm);
					WriteString(w, e.Var);
					w.Write((byte)e.Type);
					switch (e.Type) {
						case VarType.Float: w.Write(e.LastFloat); break;
						case VarType.Int: w.Write(e.LastInt); break;
						case VarType.Bool: w.Write(e.LastBool); break;
					}
				}
				SteamTransport.SendReliable(Protocol.Finish(ms));
			}
		}

		// ------------------------------------------------------------------
		// Alma
		// ------------------------------------------------------------------

		/// <summary>Gelen paketi uygular (v9: JobState mesajı formatında).</summary>
		public static void OnMessage(BinaryReader r) {
			try {
				int count = r.ReadByte();
				for (int i = 0; i < count; i++) {
					string path = ReadString(r);
					string fsm = ReadString(r);
					string var = ReadString(r);
					byte typeByte = r.ReadByte();
					VarType type = (VarType)typeByte;

					// DEĞER, KAYIT ÇÖZÜLMEDEN ÖNCE OKUNUR.
					//
					// Canlı log kanıtı: değer en sonda, yani "e çözülemedi"
					// durumunda okunmadan geçiliyordu. O kaydın DEĞER baytları
					// akışta kalıyor ve bir sonraki kaydın YOLU olarak okunuyordu:
					// paketin geri kalanı çöp. 17 iş kaydı olan bir pakette tek bir
					// çözülemeyen kayıt, "Failed to read past end of stream" ile
					// 17 değişikliği birden kaybettiriyordu.
					float fValue = 0f;
					int iValue = 0;
					bool bValue = false;
					switch (type) {
						case VarType.Float: fValue = r.ReadSingle(); break;
						case VarType.Int: iValue = r.ReadInt32(); break;
						case VarType.Bool: bValue = r.ReadBoolean(); break;
						// Bilinmeyen tip: kaydın kaç bayt tuttuğu bilinemez,
						// akışı kurtarmanın yolu yok. Düşüp paketi bitirmek,
						// sonraki kayıtları yanlış uygulamaktan iyidir.
						default:
							SmpLog.Warning("jobs: unknown value type " + (int)typeByte
								+ " on '" + path + "/" + fsm + "/" + var + "'.");
							return;
					}

					Entry e;
					if (!byKey.TryGetValue(Key(path, fsm, var), out e)) {
						// Dinamik ekle (JIP)
						GameObject go = FindByPath(path);
						if (go == null) { Unresolved++; continue; }
						PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
						if (fsms == null) { Unresolved++; continue; }
						for (int j = 0; j < fsms.Length; j++) {
							if (fsms[j] == null) continue;
							string fn = null;
							try { fn = fsms[j].FsmName; } catch { continue; }
							if (!string.Equals(fn, fsm, StringComparison.OrdinalIgnoreCase)) continue;

							FsmFloat fv = FsmVars.Float(fsms[j], var);
							if (fv != null && string.Equals(fv.Name, var, StringComparison.Ordinal)) {
								e = new Entry();
								e.Path = path; e.Fsm = fn; e.Var = var;
								e.Type = VarType.Float; e.FsmRef = fsms[j]; e.FloatRef = fv;
								e.LastFloat = fv.Value;
								tracked.Add(e); byKey[Key(path, fsm, var)] = e;
								break;
							}

							FsmInt iv = FsmVars.Int(fsms[j], var);
							if (iv != null && string.Equals(iv.Name, var, StringComparison.Ordinal)) {
								e = new Entry();
								e.Path = path; e.Fsm = fn; e.Var = var;
								e.Type = VarType.Int; e.FsmRef = fsms[j]; e.IntRef = iv;
								e.LastInt = iv.Value;
								tracked.Add(e); byKey[Key(path, fsm, var)] = e;
								break;
							}

							FsmBool bv = FsmVars.Bool(fsms[j], var);
							if (bv != null && string.Equals(bv.Name, var, StringComparison.Ordinal)) {
								e = new Entry();
								e.Path = path; e.Fsm = fn; e.Var = var;
								e.Type = VarType.Bool; e.FsmRef = fsms[j]; e.BoolRef = bv;
								e.LastBool = bv.Value;
								tracked.Add(e); byKey[Key(path, fsm, var)] = e;
								break;
							}
						}
						if (e == null) { Unresolved++; continue; }
					}

					if (e == null) { Unresolved++; continue; }

					using (EchoGuard.Enter()) {
						switch (type) {
							case VarType.Float:
								if (e.FloatRef != null) { e.FloatRef.Value = fValue; e.LastFloat = fValue; }
								break;
							case VarType.Int:
								if (e.IntRef != null) { e.IntRef.Value = iValue; e.LastInt = iValue; }
								break;
							case VarType.Bool:
								if (e.BoolRef != null) { e.BoolRef.Value = bValue; e.LastBool = bValue; }
								break;
						}
					}
					Applied++;
				}
			}
			catch (Exception ex) {
				SmpLog.Warning("jobs receive failed: " + ex.Message);
			}
		}

		/// <summary>Sahne yolundan GameObject bulur.</summary>
		static GameObject FindByPath(string path) {
			if (string.IsNullOrEmpty(path)) return null;
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
		// String yardımcıları
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

		public static void Reset() {
			tracked.Clear();
			byKey.Clear();
			nextPoll = 0f;
		}

		public static string Describe() {
			int f = 0, in_ = 0, b = 0;
			for (int i = 0; i < tracked.Count; i++) {
				switch (tracked[i].Type) {
					case VarType.Float: f++; break;
					case VarType.Int: in_++; break;
					case VarType.Bool: b++; break;
				}
			}
			return "jobs: " + tracked.Count + " entries (float=" + f + " int=" + in_
				+ " bool=" + b + "), applied " + Applied + ", unresolved " + Unresolved;
		}
	}
}