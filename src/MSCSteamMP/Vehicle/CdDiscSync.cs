using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// CD PLAYER DISK SENKRONU (fiziksel: kizak + disk nesnesi).
	///
	/// DUMPER KANITI (dump2/level2.json):
	///   trigger_disc / Assembly:
	///     "Wait for assembly" --ASSEMBLE--> Assemble --FINISHED--> "CD in"
	///     "CD in": SetBoolValue + EnableFSM + SetParent + PlayAnimation(cd_sled_in)
	///     "CD in" --EJECT--> Eject: SetFsmInt(Update/Index) + SetBoolValue +
	///       ActivateGameObject x2 --FINISHED--> "State 3":
	///       ActivateGameObject + SetBoolValue + SendEventByName(Part/ChangeTrack, NO CD)
	///       + PlayAnimation(cd_sled_out) --FINISHED--> "Check for Part"
	///     olaylar: TRIGGER ENTER, ASSEMBLE, CD1, CD2, CD3, CDIN, DISASSEMBLE,
	///       EJECT, GLOBALEVENT, LOOP, PROCEED
	///   Eject / Use: USE --> Switch: SendEventByName(Update, STOP CD) +
	///     SendEventByName(Assembly, EJECT) + MasterAudioPlaySound(CarFoley/cd_button)
	///   cd(itemN) / Player: "Remove part" SetFsmString(Update/CurrentCD, ThisCD)
	///
	/// NEDEN AYRI MESAJ (CtrlState/olay yolu yetmiyor):
	///   * CtrlState yalnizca (yol, fsm, durum) tasir: "CD in" aynalanir ama
	///     HANGI diskin (CD1/2/3) takildigi gitmez; karsida `Part` degiskeni
	///     bos kalir, SetParent/ActivateGameObject bosa calisir, disk kutuda kalir.
	///   * Olay yolu (ASSEMBLE/EJECT) ayni sebeple yetmez: karsi tarafta `Part`
	///     cozulemezse FSM "Check for Part"ta kalir (WorldObjectSync:2626 notu).
	///
	/// COZUM: (slotYolu, diskAdi) gider. Bos diskAdi = cikarma. Alan taraf:
	///   takma  -> diski bul, Assembly `Part` degiskenine koy, Update/CurrentCD
	///             yaz, oyunun kendi CDIN olayini gonder (kayit-yukleme yolu;
	///             yoksa ForceState "CD in"). Giris aksiyonlari (SetParent +
	///             cd_sled_in + EnableFSM) oyunun kendi koduyla calisir.
	///   cikarma -> Assembly "CD in"deyse oyunun kendi EJECT olayini gonder
	///             (sled_out + NO CD + Index sifirlama aynen calisir).
	///
	/// Yetki: simetrik ilk-sahip + 3 sn bekleme (kapilarla ayni ilke). JIP:
	/// Tick, baglanti yukselirken dolu slotlari yeniden gonderir (JIP
	/// anlik goruntusunde `Part`/disk kimligi yoktur).
	/// </summary>
	public static class CdDiscSync {

		/// <summary>Gonderen tablosu: dolu slotlar (slotYolu -> "CD1"/"CD2"/"CD3").</summary>
		static readonly Dictionary<string, string> sentSlots =
			new Dictionary<string, string>(StringComparer.Ordinal);

		/// <summary>Son gonderim zamani (simetrik bekleme).</summary>
		static readonly Dictionary<string, float> sentAt =
			new Dictionary<string, float>(StringComparer.Ordinal);

		const float COOLDOWN = 3f;

		static bool wasRemote;

		// Kutu gonderen tablolari: kapak (biliniyorsa) + icindeki disk (biliniyorsa).
		// Kapak mantigi yuklemede daima kapali baslar (kayit kapak boolunu
		// saklamaz) -> tablo false baslar, her USE cevirir.
		static readonly Dictionary<string, bool> sentCaseLid =
			new Dictionary<string, bool>(StringComparer.Ordinal);
		static readonly Dictionary<string, string> sentCaseDisc =
			new Dictionary<string, string>(StringComparer.Ordinal);
		static readonly Dictionary<string, float> sentAtCase =
			new Dictionary<string, float>(StringComparer.Ordinal);
		// Alici hafizasi: "lid|disc" (lid: 1/0/?, disc: ?/id/"").
		static readonly Dictionary<string, string> appliedCase =
			new Dictionary<string, string>(StringComparer.Ordinal);
		struct PendingCaseDisc {
			public string CasePath;
			public string DiscId;
			public float Deadline;
		}
		static readonly System.Collections.Generic.List<PendingCaseDisc> pendingCase =
			new System.Collections.Generic.List<PendingCaseDisc>(4);

		public static int SentDisc;
		public static int AppliedDisc;
		public static int UnresolvedDisc;

		// ------------------------------------------------------------------
		// Gonderen kancalari (InteractionManager.OnFsmEvent'ten)
		// ------------------------------------------------------------------

		/// <summary>trigger_disc / Assembly olayi (ASSEMBLE, CDIN, EJECT, DISASSEMBLE).</summary>
		public static void OnSlotEvent(string slotPath, PlayMakerFSM asmFsm, string eventName) {
			try {
				if (!SteamTransport.HasRemote) return;
				if (string.IsNullOrEmpty(slotPath) || asmFsm == null) return;
				if (string.Equals(eventName, "ASSEMBLE", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(eventName, "CDIN", StringComparison.OrdinalIgnoreCase)) {
					string disc = ReadInsertedDiscId(asmFsm);
					if (string.IsNullOrEmpty(disc)) return;
					string old;
					if (sentSlots.TryGetValue(slotPath, out old)
						&& string.Equals(old, disc, StringComparison.Ordinal)) return;
					sentSlots[slotPath] = disc;
					sentAt[slotPath] = Time.time;
					SendState(slotPath, disc);
				}
				else if (string.Equals(eventName, "EJECT", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(eventName, "DISASSEMBLE", StringComparison.OrdinalIgnoreCase)) {
					if (!sentSlots.ContainsKey(slotPath)) return;
					sentSlots.Remove(slotPath);
					sentAt[slotPath] = Time.time;
					SendState(slotPath, "");
				}
			}
			catch { }
		}

		/// <summary>Eject dugmesine basildi (Use). Slot adresi dugmeden turetilir.</summary>
		public static void OnEjectPressed(GameObject ejectBtn) {
			try {
				if (!SteamTransport.HasRemote || ejectBtn == null) return;
				string slotPath = null;
				PlayMakerFSM asmFsm = null;
				try {
					Transform buttons = ejectBtn.transform.parent;
					Transform unit = buttons != null ? buttons.parent : null;
					Transform slot = unit != null ? unit.Find("trigger_disc") : null;
					if (slot != null) {
						slotPath = SceneDump.PathOf(slot);
						PlayMakerFSM[] fsms = slot.GetComponents<PlayMakerFSM>();
						for (int i = 0; fsms != null && i < fsms.Length; i++) {
							string fn = null;
							try { fn = fsms[i].FsmName; } catch { }
							if (string.Equals(fn, "Assembly", StringComparison.OrdinalIgnoreCase)) {
								asmFsm = fsms[i];
								break;
							}
						}
					}
				}
				catch { }
				if (!string.IsNullOrEmpty(slotPath)) {
					// Dogrudan cikarma gonder; Assembly EJECT gozlemi de
					// gelirse tablo bostur ve ikinci paket uretilmez.
					if (!sentSlots.ContainsKey(slotPath)) return;
					sentSlots.Remove(slotPath);
					sentAt[slotPath] = Time.time;
					SendState(slotPath, "");
				}
				else if (sentSlots.Count > 0) {
					// Adres cozulemezse bilinen dolu slotlari bosalt.
					string[] keys = new string[sentSlots.Count];
					sentSlots.Keys.CopyTo(keys, 0);
					for (int i = 0; i < keys.Length; i++) {
						sentSlots.Remove(keys[i]);
						sentAt[keys[i]] = Time.time;
						SendState(keys[i], "");
					}
				}
			}
			catch { }
		}

		/// <summary>Disk elle cikarildi (cd(itemN) REMOVE). Takili diskse cikarma gider.</summary>
		public static void OnDiscRemoved(GameObject discGo) {
			try {
				if (!SteamTransport.HasRemote || discGo == null) return;
				if (sentSlots.Count == 0 && sentCaseDisc.Count == 0) return;
				string thisCd = ReadThisCd(discGo);
				if (string.IsNullOrEmpty(thisCd)) return;
				string hit = null;
				foreach (KeyValuePair<string, string> kv in sentSlots) {
					if (string.Equals(kv.Value, thisCd, StringComparison.Ordinal)) {
						hit = kv.Key;
						break;
					}
				}
				if (hit == null) {
					// Kutudaki disklerden biri mi? (thisCd yukarida okundu.)
					if (!string.IsNullOrEmpty(thisCd)) {
						foreach (KeyValuePair<string, string> kv in sentCaseDisc) {
							if (string.Equals(kv.Value, thisCd, StringComparison.Ordinal)) {
								sentCaseDisc[kv.Key] = "";
								sentAtCase[kv.Key] = Time.time;
								bool lid = false;
								bool lk = sentCaseLid.TryGetValue(kv.Key, out lid);
								SendCaseState(kv.Key, lk, lid, true, "");
								return;
							}
						}
					}
					return;
				}
				sentSlots.Remove(hit);
				sentAt[hit] = Time.time;
				SendState(hit, "");
			}
			catch { }
		}

		/// <summary>Takilan diskin kimligi: Assembly `Part` -> Player/ThisCD, yedek CurrentCD.</summary>
		static string ReadInsertedDiscId(PlayMakerFSM asmFsm) {
			try {
				FsmGameObject part = null;
				try { part = asmFsm.Fsm.GetFsmGameObject("Part"); } catch { }
				GameObject disc = part != null ? part.Value : null;
				if (disc != null) {
					string t = ReadThisCd(disc);
					if (!string.IsNullOrEmpty(t)) return t;
				}
				PlayMakerFSM upd = RadioSync.FindFsm("CDPlayer", "Update");
				FsmString c = upd != null ? FsmNames.Str(upd, "CurrentCD") : null;
				if (c != null && !string.IsNullOrEmpty(c.Value)) return c.Value.Trim();
			}
			catch { }
			return null;
		}

		static string ReadThisCd(GameObject discGo) {
			try {
				PlayMakerFSM[] fsms = discGo.GetComponents<PlayMakerFSM>();
				for (int i = 0; fsms != null && i < fsms.Length; i++) {
					string fn = null;
					try { fn = fsms[i].FsmName; } catch { }
					if (!string.Equals(fn, "Player", StringComparison.OrdinalIgnoreCase)) continue;
					FsmString t = FsmNames.Str(fsms[i], "ThisCD");
					if (t != null && !string.IsNullOrEmpty(t.Value)) return t.Value.Trim();
				}
			}
			catch { }
			return null;
		}

		static void SendState(string slotPath, string discId) {
			try {
				MemoryStream ms = Protocol.Begin(Protocol.Msg.CdDisc);
				BinaryWriter w = new BinaryWriter(ms);
				w.Write(slotPath);
				w.Write(discId != null ? discId : "");
				w.Flush();
				if (SteamTransport.SendReliable(Protocol.Finish(ms))) {
					SentDisc++;
					SmpTrace.Act("CD", string.IsNullOrEmpty(discId) ? "eject" : "insert",
						slotPath + " disc=" + (string.IsNullOrEmpty(discId) ? "(empty)" : discId));
				}
			}
			catch (Exception e) {
				SmpLog.Warning("cd disc send failed: " + e.GetType().Name);
			}
		}

		// ------------------------------------------------------------------
		// JIP: baglanti yukselirken dolu slotlari yeniden gonder
		// ------------------------------------------------------------------

		public static void Tick() {
			try {
				bool remote = SteamTransport.HasRemote;
				if (remote && !wasRemote) {
					if (sentSlots.Count > 0) {
						string[] keys = new string[sentSlots.Count];
						sentSlots.Keys.CopyTo(keys, 0);
						for (int i = 0; i < keys.Length; i++) {
							string d;
							if (!sentSlots.TryGetValue(keys[i], out d)) continue;
							SendState(keys[i], d);
						}
					}
					if (sentCaseLid.Count > 0 || sentCaseDisc.Count > 0) {
						System.Collections.Generic.List<string> ck =
							new System.Collections.Generic.List<string>(8);
						foreach (string k in sentCaseLid.Keys) if (!ck.Contains(k)) ck.Add(k);
						foreach (string k in sentCaseDisc.Keys) if (!ck.Contains(k)) ck.Add(k);
						for (int i = 0; i < ck.Count; i++) {
							bool lid = false;
							bool lk = sentCaseLid.TryGetValue(ck[i], out lid);
							string dd = "";
							bool dk = sentCaseDisc.TryGetValue(ck[i], out dd);
							SendCaseState(ck[i], lk, lid, dk, dd);
						}
					}
				}
				wasRemote = remote;
				VerifyPendingCase();
			}
			catch { }
		}

		static void VerifyPendingCase() {
			if (pendingCase.Count == 0) return;
			float now = Time.time;
			for (int i = pendingCase.Count - 1; i >= 0; i--) {
				PendingCaseDisc p = pendingCase[i];
				if (now < p.Deadline) continue;
				pendingCase.RemoveAt(i);
				try {
					GameObject cgo = FindByPath(p.CasePath);
					PlayMakerFSM useFsm = cgo != null ? FindFsmOn(cgo, "Use") : null;
					FsmBool din = useFsm != null ? FsmNames.Bool(useFsm, "DiscIn") : null;
					if (din != null && din.Value) continue;
					GameObject dgo = FindDisc(p.DiscId);
					if (din != null) { try { din.Value = true; } catch { } }
					if (dgo == null || cgo == null) continue;
					try { dgo.transform.SetParent(cgo.transform, true); } catch { }
					try { dgo.transform.localPosition = Vector3.zero; } catch { }
					try {
						PlayMakerFSM rem = FindFsmOn(dgo, "Removal");
						if (rem != null) rem.enabled = true;
					} catch { }
					try { if (!dgo.activeSelf) dgo.SetActive(true); } catch { }
					SmpTrace.Act("CD", "casedisc", "scripted fallback " + p.CasePath);
				}
				catch { }
				}
			}

		static void EnqueueCasePending(string casePath, string discId) {
			try {
				for (int i = 0; i < pendingCase.Count; i++) {
					if (string.Equals(pendingCase[i].CasePath, casePath, StringComparison.Ordinal)) {
						pendingCase.RemoveAt(i);
						break;
					}
				}
				if (pendingCase.Count >= 4) pendingCase.RemoveAt(0);
				PendingCaseDisc p = new PendingCaseDisc();
				p.CasePath = casePath;
				p.DiscId = discId;
				p.Deadline = Time.time + 1.2f;
				pendingCase.Add(p);
			}
			catch { }
		}

		// ------------------------------------------------------------------
		// Alan taraf
		// ------------------------------------------------------------------

		public static void OnMessage(BinaryReader r) {
			string slotPath, discId;
			try {
				slotPath = r.ReadString();
				discId = r.ReadString();
			}
			catch {
				return;
			}
			if (string.IsNullOrEmpty(slotPath)) return;
			try {
				if (EchoGuard.Suppressed) return;
				// Simetrik bekleme: bu slotu az once biz surduysek geleni yok say.
				float at;
				if (sentAt.TryGetValue(slotPath, out at) && Time.time - at < COOLDOWN) return;
				EchoGuard.Scope sc = EchoGuard.Enter();
				try {
					if (string.IsNullOrEmpty(discId)) ApplyEject(slotPath);
					else ApplyInsert(slotPath, discId);
				}
				finally { sc.Dispose(); }
			}
			catch (Exception e) {
				SmpLog.Warning("cd disc apply failed: " + e.GetType().Name);
			}
		}

		static void ApplyInsert(string slotPath, string discId) {
			GameObject slot = FindByPath(slotPath);
			if (slot == null) { UnresolvedDisc++; return; }
			PlayMakerFSM asm = FindFsmOn(slot, "Assembly");
			if (asm == null || asm.Fsm == null) { UnresolvedDisc++; return; }
			GameObject disc = FindDisc(discId);
			// DISK NESNESI YOKSA DA DEVAM: kizak + yuva durumu aynalanir,
			// "varmis gibi" gorunur; ses PCM akisindan gelir.
			if (disc == null) SmpTrace.Act("CD", "insert", slotPath + " disc=" + discId + " (no object, visual only)");

			string cur = null;
			try { cur = asm.Fsm.ActiveStateName; } catch { }
			FsmGameObject partVar = GetGameObjectVar(asm, "Part");
			bool samePart = disc != null && partVar != null && partVar.Value == disc;
			if (string.Equals(cur, "CD in", StringComparison.Ordinal) && samePart) {
				SetCurrentCd(discId);
				AppliedDisc++;
				return;
			}

			try {
				if (partVar != null && disc != null) partVar.Value = disc;
				SetCurrentCd(discId);
				bool done = false;
				if (!string.Equals(cur, "CD in", StringComparison.Ordinal)
					&& FsmNames.HasEvent(asm, "CDIN")) {
					asm.SendEvent("CDIN");
					string now = null;
					try { now = asm.Fsm.ActiveStateName; } catch { }
					done = string.Equals(now, "CD in", StringComparison.Ordinal);
				}
				if (!done) {
					// CDIN yoksa ya da gecis olmadiysa: dogrudan "CD in".
					// Giris aksiyonlari (SetParent + cd_sled_in + EnableFSM)
					// oyunun kendi koduyla calisir.
					if (!WorldObjectSync.ForceState(asm, "CD in")) {
						UnresolvedDisc++;
						return;
					}
				}
				AppliedDisc++;
				SmpTrace.Act("CD", "insert", slotPath + " disc=" + discId);
			}
			catch (Exception e) {
				SmpLog.Warning("cd insert apply failed: " + e.GetType().Name);
			}
		}

		static void ApplyEject(string slotPath) {
			GameObject slot = FindByPath(slotPath);
			if (slot == null) { UnresolvedDisc++; return; }
			PlayMakerFSM asm = FindFsmOn(slot, "Assembly");
			if (asm == null || asm.Fsm == null) { UnresolvedDisc++; return; }
			string cur = null;
			try { cur = asm.Fsm.ActiveStateName; } catch { }
			if (!string.Equals(cur, "CD in", StringComparison.Ordinal)) return;
			try {
				// Oyunun kendi cikarma zinciri: Eject (Index + gosterge) ->
				// State 3 (NO CD + cd_sled_out) -> Check for Part.
				if (FsmNames.HasEvent(asm, "EJECT")) asm.SendEvent("EJECT");
				else WorldObjectSync.ForceState(asm, "Eject");
				AppliedDisc++;
				SmpTrace.Act("CD", "eject", slotPath);
			}
			catch (Exception e) {
				SmpLog.Warning("cd eject apply failed: " + e.GetType().Name);
			}
		}

		static void SetCurrentCd(string discId) {
			try {
				PlayMakerFSM upd = RadioSync.FindFsm("CDPlayer", "Update");
				FsmString c = upd != null ? FsmNames.Str(upd, "CurrentCD") : null;
				if (c != null) c.Value = discId != null ? discId : "";
			}
			catch { }
		}

		// ------------------------------------------------------------------
		// CD KUTUSU uygulamalari
		// ------------------------------------------------------------------

		public static bool HasAncestorLike(GameObject go, string frag) {
			try {
				Transform t = go != null ? go.transform.parent : null;
				while (t != null) {
					if (t.name.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) return true;
					t = t.parent;
				}
			}
			catch { }
			return false;
		}

		public static void OnCaseLidToggle(GameObject caseGo) {
			try {
				if (!SteamTransport.HasRemote || caseGo == null) return;
				string cp = null;
				try { cp = SceneDump.PathOf(caseGo.transform); } catch { }
				if (string.IsNullOrEmpty(cp)) return;
				bool lid = true;
				bool old;
				if (sentCaseLid.TryGetValue(cp, out old)) lid = !old;
				sentCaseLid[cp] = lid;
				sentAtCase[cp] = Time.time;
				bool dk = sentCaseDisc.ContainsKey(cp);
				string dv = "";
				if (dk) sentCaseDisc.TryGetValue(cp, out dv);
				SendCaseState(cp, true, lid, dk, dv);
			}
			catch { }
		}

		public static void OnCaseDiscEvent(GameObject triggerGo, PlayMakerFSM asmFsm, string eventName) {
			try {
				if (!SteamTransport.HasRemote || triggerGo == null) return;
				Transform caseT = triggerGo.transform.parent;
				if (caseT == null) return;
				string cp = null;
				try { cp = SceneDump.PathOf(caseT); } catch { }
				if (string.IsNullOrEmpty(cp)) return;
				if (string.Equals(eventName, "ASSEMBLE", StringComparison.OrdinalIgnoreCase)) {
					string disc = ReadInsertedDiscId(asmFsm);
					if (string.IsNullOrEmpty(disc)) return;
					string old;
					if (sentCaseDisc.TryGetValue(cp, out old)
						&& string.Equals(old, disc, StringComparison.Ordinal)) return;
					sentCaseDisc[cp] = disc;
					sentAtCase[cp] = Time.time;
					bool lid = false;
					bool lk = sentCaseLid.TryGetValue(cp, out lid);
					SendCaseState(cp, lk, lid, true, disc);
				}
				else if (string.Equals(eventName, "DISASSEMBLE", StringComparison.OrdinalIgnoreCase)) {
					string old;
					if (!sentCaseDisc.TryGetValue(cp, out old)) return;
					sentCaseDisc[cp] = "";
					sentAtCase[cp] = Time.time;
					bool lid = false;
					bool lk = sentCaseLid.TryGetValue(cp, out lid);
					SendCaseState(cp, lk, lid, true, "");
				}
			}
			catch { }
		}

		static void SendCaseState(string casePath, bool lidKnown, bool lidOpen, bool discKnown, string discId) {
			try {
				MemoryStream ms = Protocol.Begin(Protocol.Msg.CdCase);
				BinaryWriter w = new BinaryWriter(ms);
				w.Write(casePath);
				w.Write(lidKnown);
				w.Write(lidOpen);
				w.Write(discKnown);
				w.Write(discKnown && discId != null ? discId : "");
				w.Flush();
				if (SteamTransport.SendReliable(Protocol.Finish(ms))) {
					SentDisc++;
					SmpTrace.Act("CD", "case", casePath + " lid=" + (lidKnown ? (lidOpen ? "open" : "closed") : "?")
						+ " disc=" + (discKnown ? (string.IsNullOrEmpty(discId) ? "(empty)" : discId) : "?"));
				}
			}
			catch (Exception e) { SmpLog.Warning("cd case send failed: " + e.GetType().Name); }
		}

		public static void OnCaseMessage(BinaryReader r) {
			string cp;
			bool lidKnown, lidOpen, discKnown;
			string discId;
			try {
				cp = r.ReadString();
				lidKnown = r.ReadBoolean();
				lidOpen = r.ReadBoolean();
				discKnown = r.ReadBoolean();
				discId = r.ReadString();
			}
			catch { return; }
			if (string.IsNullOrEmpty(cp)) return;
			try {
				if (EchoGuard.Suppressed) return;
				float at;
				if (sentAtCase.TryGetValue(cp, out at) && Time.time - at < COOLDOWN) return;
				EchoGuard.Scope sc = EchoGuard.Enter();
				try { ApplyCase(cp, lidKnown, lidOpen, discKnown, discId); }
				finally { sc.Dispose(); }
			}
			catch (Exception e) { SmpLog.Warning("cd case apply failed: " + e.GetType().Name); }
		}

		static void ParseCaseMem(string mem, out bool lidKnown, out bool lid, out bool discKnown, out string disc) {
			lidKnown = false; lid = false; discKnown = false; disc = "";
			try {
				if (string.IsNullOrEmpty(mem)) return;
				int bar = mem.IndexOf("|");
				if (bar < 0) return;
				string l = mem.Substring(0, bar);
				string d = mem.Substring(bar + 1);
				if (l == "1") { lidKnown = true; lid = true; }
				else if (l == "0") { lidKnown = true; lid = false; }
				if (d != "?") { discKnown = true; disc = d; }
			}
			catch { }
		}

		static void ApplyCase(string cp, bool lidKnown, bool lidOpen, bool discKnown, string discId) {
			GameObject caseGo = FindByPath(cp);
			if (caseGo == null) { UnresolvedDisc++; return; }
			string mem = null;
			appliedCase.TryGetValue(cp, out mem);
			bool mLidKnown, mLid, mDiscKnown;
			string mDisc;
			ParseCaseMem(mem, out mLidKnown, out mLid, out mDiscKnown, out mDisc);
			bool newLidKnown = mLidKnown, newLid = mLid, newDiscKnown = mDiscKnown;
			string newDisc = mDisc;
			if (lidKnown && (!mLidKnown || mLid != lidOpen)) {
				PlayMakerFSM open = FindFsmOn(caseGo, "Open");
				if (open != null && open.Fsm != null) {
					string goal = lidOpen ? "Open" : "Close";
					if (!string.Equals(ActiveName(open), goal, StringComparison.Ordinal)) {
						if (WorldObjectSync.ForceState(open, goal)) {
							AppliedDisc++;
							SmpTrace.Act("CD", "caselid", cp + " -> " + goal);
							newLidKnown = true; newLid = lidOpen;
						}
						else UnresolvedDisc++;
					}
					else { newLidKnown = true; newLid = lidOpen; }
					// G9: `cd case(itemN)[Open]` hem "Open" ADLI bir durum
					// hem de ayni adi bir bool tasiyor. ForceState durumu
					// yaziyor; bool da esitleniyor ki geç katılan oyuncu
					// kutu kapağının ACIK olduğunu bool'dan görsün.
					SetCaseLidBool(caseGo, lidOpen);
				}
				else UnresolvedDisc++;
			}
			if (discKnown && (!mDiscKnown || !string.Equals(mDisc ?? "", discId ?? "", StringComparison.Ordinal))) {
				if (string.IsNullOrEmpty(discId)) ApplyCaseDiscTaken(caseGo, mDisc);
				else ApplyCaseDiscInsert(cp, caseGo, discId);
				newDiscKnown = true; newDisc = discId ?? "";
			}
			appliedCase[cp] = (newLidKnown ? (newLid ? "1" : "0") : "?") + "|" + (newDiscKnown ? newDisc : "?");
		}

		static string ActiveName(PlayMakerFSM f) {
			try { return (f != null && f.Fsm != null) ? f.Fsm.ActiveStateName : null; }
			catch { return null; }
		}

		static void ApplyCaseDiscInsert(string cp, GameObject caseGo, string discId) {
			try {
				Transform trigT = caseGo.transform.Find("trigger_cd");
				PlayMakerFSM asm = trigT != null ? FindFsmOn(trigT.gameObject, "Assembly") : null;
				if (asm == null || asm.Fsm == null) { UnresolvedDisc++; return; }
				PlayMakerFSM useFsm = FindFsmOn(caseGo, "Use");
				FsmBool din = useFsm != null ? FsmNames.Bool(useFsm, "DiscIn") : null;
				GameObject disc = FindDisc(discId);
				if (din != null && din.Value) {
					FsmGameObject pv = GetGameObjectVar(asm, "Part");
					if (disc == null || (pv != null && pv.Value == disc)) { AppliedDisc++; return; }
				}
				if (disc == null) {
					if (din != null) { try { din.Value = true; } catch { } }
					SetCaseDiscFlags(useFsm, false, true);
					AppliedDisc++;
					SmpTrace.Act("CD", "casedisc", cp + " flag-only (no object)");
					return;
				}
				FsmGameObject partVar = GetGameObjectVar(asm, "Part");
				if (partVar != null) partVar.Value = disc;
				if (FsmNames.HasEvent(asm, "ASSEMBLE")) asm.SendEvent("ASSEMBLE");
				// G9/G10: kutunun KENDI tarafi.
				//   DiscInPlayer = disk kazakta degil, CD player'da
				//   DiscIn        = disk kutuda degil
				// Dumper: cd case(itemN)[Use] bool DiscIn / DiscInPlayer
				SetCaseDiscFlags(useFsm, false, true);
				EnqueueCasePending(cp, discId);
				AppliedDisc++;
				SmpTrace.Act("CD", "casedisc", cp + " insert " + discId);
			}
			catch (Exception e) { SmpLog.Warning("cd case insert failed: " + e.GetType().Name); }
		}

		/// <summary>
		/// G9 -- CD kutusunun kendi bayraklari + ES2 kaydi.
		///
		/// Dumper kaniti (`ITEMS/CDs/cd case(item1)`[Use], 501 string):
		///   bool <b>DiscIn</b>       disk kutuda mi
		///   bool <b>DiscInPlayer</b> disk CD player kazaginda mi
		///   int  Count,  bool Save
		///   ES2 anahtarlari: CD1InCase, CD1InPlayer, CD1CaseTransform,
		///                   CD1Transform  (dosya: defaultES2File.txt)
		///
		/// ES2 dosyasina KENDIMIZ yazmiyoruz. Bayraklari yazip ardindan
		/// `SAVEGAME` gonderiyoruz; oyunun kendi FSM'i kendi kayit
		/// dosyasini guncelliyor. Tek kod yolu -> iki tarafa yazilmis
		/// bir dosya olmaz, kayit ayrismasi olmaz.
		///
		/// Ayni sebeple gec katilan oyuncu da dogrulenir: kayit dosyasi
		/// zaten dogruysa hicbir sey yazilmaz.
		/// </summary>
		static void SetCaseDiscFlags(PlayMakerFSM useFsm, bool discIn, bool discInPlayer) {
			try {
				if (useFsm == null) return;
				FsmBool a = FsmNames.Bool(useFsm, "DiscIn");
				if (a != null && a.Value != discIn) a.Value = discIn;
				FsmBool b = FsmNames.Bool(useFsm, "DiscInPlayer");
				if (b != null && b.Value != discInPlayer) b.Value = discInPlayer;
				// Oyunun kendi kayit yolunu calistir.
				if (useFsm.Fsm != null) useFsm.SendEvent("SAVEGAME");
			}
			catch { }
		}

		/// <summary>
		/// G9 -- kutu kapaginin bool'u. `cd case(itemN)[Open]` hem "Open"
		/// ADLI bir bool hem de ayni adi bir durum tasiyor; ForceState
		/// durumu yaziyor, bool ayrica burada esitleniyor.
		/// </summary>
		static void SetCaseLidBool(GameObject caseGo, bool open) {
			try {
				PlayMakerFSM of = FindFsmOn(caseGo, "Open");
				if (of == null) return;
				FsmBool v = FsmNames.Bool(of, "Open");
				if (v != null && v.Value != open) v.Value = open;
			}
			catch { }
		}

		static void ApplyCaseDiscTaken(GameObject caseGo, string lastDiscId) {
			try {
				// G9/G10: disk kazaktan cikti -> ne kutuda ne player'da.
				// Once bayraklari temizle ki oyunun kendi zinciri
				// (Removal -> Use/DiscIn) calissin.
				SetCaseDiscFlags(FindFsmOn(caseGo, "Use"), false, false);
				GameObject disc = null;
				if (!string.IsNullOrEmpty(lastDiscId)) disc = FindDisc(lastDiscId);
				if (disc == null) disc = FindDiscUnder(caseGo);
				if (disc == null) { AppliedDisc++; return; }
				PlayMakerFSM rem = FindFsmOn(disc, "Removal");
				if (rem != null && rem.Fsm != null && FsmNames.HasEvent(rem, "REMOVE")) rem.SendEvent("REMOVE");
				else {
					try {
						disc.transform.SetParent(null, true);
						disc.transform.position = caseGo.transform.position + new Vector3(0f, 0.3f, 0f);
						if (!disc.activeSelf) disc.SetActive(true);
					} catch { }
				}
				try {
					PlayMakerFSM useFsm = FindFsmOn(caseGo, "Use");
					FsmBool din = useFsm != null ? FsmNames.Bool(useFsm, "DiscIn") : null;
					if (din != null) din.Value = false;
				} catch { }
				AppliedDisc++;
				SmpTrace.Act("CD", "casedisc", "taken");
			}
			catch (Exception e) { SmpLog.Warning("cd case taken failed: " + e.GetType().Name); }
		}

		static GameObject FindDiscUnder(GameObject root) {
			try {
				if (root == null) return null;
				for (int i = 0; i < root.transform.childCount; i++) {
					Transform c = root.transform.GetChild(i);
					if (c == null) continue;
					if (c.name.StartsWith("cd(item", StringComparison.OrdinalIgnoreCase)) return c.gameObject;
				}
			}
			catch { }
			return null;
		}

		// ------------------------------------------------------------------
		// Bulma yardimcilari
		// ------------------------------------------------------------------

		static GameObject FindByPath(string path) {
			try {
				if (string.IsNullOrEmpty(path)) return null;
				string[] parts = path.Split('/');
				GameObject[] roots = SceneDump.AllSceneRoots();
				if (roots == null) return null;
				for (int i = 0; i < roots.Length; i++) {
					if (roots[i] == null) continue;
					int at = 0;
					if (string.Equals(roots[i].name, parts[0], StringComparison.Ordinal)) at = 1;
					Transform t = roots[i].transform;
					bool ok = true;
					for (int k = at; k < parts.Length; k++) {
						Transform c = null;
						try { c = t.Find(parts[k]); } catch { }
						if (c == null) { ok = false; break; }
						t = c;
					}
					if (ok) return t.gameObject;
				}
			}
			catch { }
			return null;
		}

		static PlayMakerFSM FindFsmOn(GameObject go, string fsmName) {
			try {
				PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
				for (int i = 0; fsms != null && i < fsms.Length; i++) {
					string fn = null;
					try { fn = fsms[i].FsmName; } catch { }
					if (string.Equals(fn, fsmName, StringComparison.OrdinalIgnoreCase)) return fsms[i];
				}
			}
			catch { }
			return null;
		}

		static FsmGameObject GetGameObjectVar(PlayMakerFSM fsm, string name) {
			try {
				FsmGameObject[] all = fsm.FsmVariables.GameObjectVariables;
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] != null && string.Equals(all[i].Name, name, StringComparison.Ordinal)) return all[i];
				}
			}
			catch { }
			return null;
		}

		/// <summary>ITEMS/CDs altinda Player/ThisCD eslesen disk.</summary>
		static GameObject FindDisc(string discId) {
			try {
				GameObject[] roots = SceneDump.AllSceneRoots();
				if (roots == null) return null;
				for (int i = 0; i < roots.Length; i++) {
					if (roots[i] == null) continue;
					GameObject hit = WalkDisc(roots[i].transform, discId);
					if (hit != null) return hit;
				}
			}
			catch { }
			return null;
		}

		static GameObject WalkDisc(Transform t, string discId) {
			try {
				if (t.name.StartsWith("cd(item", StringComparison.OrdinalIgnoreCase)) {
					string thisCd = ReadThisCd(t.gameObject);
					if (string.Equals(thisCd, discId, StringComparison.Ordinal)) return t.gameObject;
				}
				for (int i = 0; i < t.childCount; i++) {
					Transform c = t.GetChild(i);
					if (c == null) continue;
					GameObject hit = WalkDisc(c, discId);
					if (hit != null) return hit;
				}
			}
			catch { }
			return null;
		}
	}
}
