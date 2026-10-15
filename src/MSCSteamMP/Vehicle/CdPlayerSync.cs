using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// CD PLAYER GÖRÜNÜRLÜK + DURUM SENKRONU.
	///
	/// KULLANICI İSTEĞİ: "cd playerı açtığımda kesin şekilde karşıdaki
	/// playerde de görünsün ve cd'nin de senkronize edilmesi lazım,
	/// şarkılar cd'nin içinde".
	///
	/// DUMPER KANITI (l2/fsms.json, 6034 tekil FSM):
	///
	/// 1) `Database/PartsStatus/Radio` [Status]  (start: "State 1")
	///    durumlar : Check CD, CD OK, CD OK 2, CD Out,
	///               Check Radio, Radio OK, Radio OK 2, Radio Out, State 1
	///    bool'lar : <b>CDPlayerOK</b>, <b>RadioOK</b>
	///    nesneler : <b>ButtonsCD</b>, <b>ButtonsRadio</b>  (FsmGameObject)
	///    olaylar  : FINISHED, ON
	///    aksiyon : Check Radio -> ActivateGameObject(ButtonsRadio)
	///              Check CD    -> ActivateGameObject(ButtonsCD)
	///    Yani dashboard YÜZEYİ değişimi TAM OLARAK bu FSM yapıyor.
	///    CDPlayerOK true ise CD düğmeleri görünür, RadioOK true ise
	///    radyo düğmeleri görünür. Satsuma'da CD player takılı DEGILSE
	///    CDPlayerOK false kalır ve karşı tarafta CD düğmeleri hiç
	///    belirmez. Bu, "karşıda görünmüyor" şikâyetinin tam kaynağı:
	///    bu bool hiçbir yerde senkronize edilmiyordu.
	///
	/// 2) `CARPARTS/PartsCar/dashboard meters(Clone)/TriggersRadio/trigger_cd`
	///    [Assembly]  -> bool <b>Setup</b>, GameObject Part,
	///    literal "cd player(Clone)". Per-arac "bu arabaya CD player
	///    takılı" bayrağı. `Database/DatabaseOrders/CD_player`[Data]
	///    state "Activate" bunu yazıyor.
	///
	/// 3) `STORE/.../cd player(Clone)/trigger_disc` [Assembly]
	///    -> bool <b>CDin</b>, bool Assemble, bool Ok, CD1/CD2/CD3, Distance
	///    durumlar: State 1, Check for Part, Find correct part,
	///              Wait for assembly, Assemble, "CD in", Eject, State 3
	///    olaylar : CDIN, CD1, CD2, CD3, EJECT, ASSEMBLE, DISASSEMBLE
	///    ÖNEMLİ: <b>CDin</b> bool'unu `ButtonsCD/TrackChannelSwitch`
	///    [ChangeTrack] "Check disc" durumunda GetFsmBool ile OKUYOR.
	///    CDin false ise ön panel "no cd" yazıyor. Yani CDin hem fiziksel
	///    kizak hem de EKRAN metninin kaynağı.
	///
	/// 4) `Radio/CD` [Playlist] -> FsmString <b>Path</b>
	///    ("NOT IMPORTED" -> "CD'S IMPORTED"), bool CD1/CD2/CD3, Import
	///    `Radio/CD/CD1..CD3` [LoadSongs] -> int <b>MaxTracks</b>, Track,
	///    Path, Name, FolderName
	///    -> "şarkılar cd'nin içinde": gönderen tarafta kaç şarkı
	///       yüklendiği. Alıcının kendi .ogg dosyaları farklıysa
	///       MaxTracks UYUŞMAZ ve parça numaraları kayar.
	///
	/// TEL: <see cref="Protocol.Msg.CdPlayerState"/>
	///   (string path, string fsm, string var, bool/int/string value)
	///   Kapsamı dar: yalnız yukarıdaki isimli bool'lar + MaxTracks.
	/// </summary>
	public static class CdPlayerSync {

		/// <summary>Izlenen tek bir sinyali tanimlar.</summary>
		public sealed class Signal {
			public string Path;      // sahne yolu (bos = ada gore bul)
			public string MatchName; // nesne adi (Path bos ise)
			public string FsmName;
			public string VarName;
			public bool IsInt;       // true ise FsmInt, false ise FsmBool

			public PlayMakerFSM Fsm;
			public bool LastBool;
			public int LastInt;
			public float LastSentAt = -99f;
		}

		static readonly List<Signal> signals = new List<Signal>(8);
		static bool discovered;

		// ------------------------------------------------------------------
		// G4: RADYO <-> CD MODU
		//
		// Dumper kaniti: `RadioCDSwitch`[Use] modu HICBIR degiskenle
		// tutmuyor. Modu yalnizca EnableFSM ile ifade ediyor:
		//     EnableFSM(ChangeTrack)   / EnableFSM(ChangeChannel)
		//     SendEventByName(Update, "STOP CD")
		//     SetFsmBool(RadioChannels/RadioOn)
		// Yani "on panel CD modunda mi" bilgisinin tek taşıyıcısı
		// `TrackChannelSwitch`[ChangeTrack] FSM'inin enabled durumu.
		//
		// Biz de tam olarak onu çoğaltıyoruz: ChangeTrack acik = CD modu.
		// ------------------------------------------------------------------
		static PlayMakerFSM changeTrackFsm;
		static bool lastCdMode;
		static float lastCdModeAt = -99f;
		const float MODE_COOLDOWN = 0.4f;
		const string VAR_CDMODE = "CDMODE";

		// ------------------------------------------------------------------
		// DUMPER'DAN GELEN ISIMLER
		// ------------------------------------------------------------------

		// `Database/PartsStatus/Radio`[Status] -- CDPlayerOK / RadioOK
		const string STATUS_OBJECT = "Radio";   // Database/PartsStatus altinda
		const string STATUS_FSM = "Status";
		const string VAR_CDPLAYER_OK = "CDPlayerOK";
		const string VAR_RADIO_OK = "RadioOK";

		// `trigger_cd`[Assembly].Setup  -- bu arabaya CD player takili mi
		const string TRIGGER_CD = "trigger_cd";
		// `trigger_disc`[Assembly].CDin -- kizakta disk var mi
		const string TRIGGER_DISC = "trigger_disc";
		// G4: `ButtonsCD/TrackChannelSwitch` -- ChangeTrack acik mi?
		const string TRACK_SWITCH = "TrackChannelSwitch";

		// `Radio/CD`[Playlist].Path -- CD listeleri yuklendi mi
		const string PLAYLIST_OBJECT = "CD";
		const string PLAYLIST_FSM = "Playlist";
		const string VAR_PATH = "Path";

		// `Radio/CD/CD1..3`[LoadSongs].MaxTracks -- kac sarki var
		const string LOADSONGS_FSM = "LoadSongs";
		const string VAR_MAXTRACKS = "MaxTracks";

		/// <summary>Gonderimler arasi minimum aralik (sn).</summary>
		const float SEND_COOLDOWN = 0.25f;

		// ==================================================================
		// KESIF
		// ==================================================================

		public static void Discover() {
			signals.Clear();
			discovered = true;
			try {
				GameObject[] all = null;
				try { all = Resources.FindObjectsOfTypeAll<GameObject>(); } catch { }
				if (all == null) return;

				PlayMakerFSM[] fsms = null;
				try { fsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>(); } catch { }

				// --- 1. Database/PartsStatus/Radio [Status] -----------------
				if (fsms != null) {
					for (int i = 0; i < fsms.Length; i++) {
						PlayMakerFSM f = fsms[i];
						if (f == null) continue;
						if (!string.Equals(SafeFsmName(f), STATUS_FSM, StringComparison.Ordinal)) continue;
						string p = null;
						try { p = Wire.PathOf(f.transform); } catch { }
						if (p == null) continue;
						if (p.IndexOf("Database/PartsStatus", StringComparison.OrdinalIgnoreCase) < 0) continue;
						if (p.IndexOf("/" + STATUS_OBJECT, StringComparison.OrdinalIgnoreCase) < 0) continue;
						Add(f, p, STATUS_FSM, VAR_CDPLAYER_OK, false);
						Add(f, p, STATUS_FSM, VAR_RADIO_OK, false);
						break;
					}
				}

				// --- 2 & 3. trigger_cd / trigger_disc -----------------------
				// Bu ikisi parçaya takili olduklari icin yol degisir
				// (dokum: STORE/Boxes/..., calisma aninda
				// .../dashboard meters(Clone)/pivot_radio/...). Bu yuzden
				// NESNE ADIYLA araniyor, yolla degil.
				if (all != null) {
					for (int i = 0; i < all.Length; i++) {
						GameObject go = all[i];
						if (go == null) continue;
						string n = null;
						try { n = go.name; } catch { }
						if (n == null) continue;

						if (string.Equals(n, TRIGGER_CD, StringComparison.Ordinal)) {
							PlayMakerFSM asm = FindFsmOn(go, "Assembly");
							if (asm != null) {
								string p = null;
								try { p = Wire.PathOf(asm.transform); } catch { }
								Add(asm, p, "Assembly", "Setup", false);
							}
						}
						else if (string.Equals(n, TRIGGER_DISC, StringComparison.Ordinal)) {
							PlayMakerFSM asm = FindFsmOn(go, "Assembly");
							if (asm != null) {
								string p = null;
								try { p = Wire.PathOf(asm.transform); } catch { }
								Add(asm, p, "Assembly", "CDin", false);
							}
						}
						else if (string.Equals(n, TRACK_SWITCH, StringComparison.Ordinal)) {
							// G4: CD modunun kaynagi.
							PlayMakerFSM ct = FindFsmOn(go, "ChangeTrack");
							if (ct != null) {
								changeTrackFsm = ct;
								try { lastCdMode = ct.enabled; } catch { }
							}
						}
						else if (string.Equals(n, PLAYLIST_OBJECT, StringComparison.Ordinal)) {
							// `Radio/CD`[Playlist] -- CD listeleri yuklendi mi
							PlayMakerFSM pl = FindFsmOn(go, PLAYLIST_FSM);
							if (pl != null) {
								string p = null;
								try { p = Wire.PathOf(pl.transform); } catch { }
								Add(pl, p, PLAYLIST_FSM, VAR_PATH, true);
							}
							// `Radio/CD/CD1..CD3`[LoadSongs].MaxTracks
							Transform cd = null;
							try { cd = go.transform; } catch { }
							if (cd != null) {
								for (int k = 0; k < cd.childCount; k++) {
									Transform c = null;
									try { c = cd.GetChild(k); } catch { continue; }
									if (c == null) continue;
									PlayMakerFSM ls = FindFsmOn(c.gameObject, LOADSONGS_FSM);
									if (ls == null) continue;
									string lp = null;
									try { lp = Wire.PathOf(ls.transform); } catch { }
									Add(ls, lp, LOADSONGS_FSM, VAR_MAXTRACKS, true);
								}
							}
						}
					}
				}
			}
			catch (Exception e) {
				SmpLog.Warning("[CdPlayerSync] kesif basarisiz: " + e.GetType().Name);
			}

			SmpLog.Print("cd player: " + signals.Count + " sinyal izleniyor"
				+ " (CDPlayerOK/RadioOK/Setup/CDin"
				+ " + CD listesi Path/MaxTracks). 'smp cd' ile liste.");
		}

		static void Add(PlayMakerFSM f, string path, string fsm, string v, bool isInt) {
			if (f == null) return;
			// Degisken gercekten cozuluyor mu? Yoksa sinyal OLUSTURMA.
			//
			// Canli gerekce: `trigger_cd` adi DORT nesnede birden var --
			//   CARPARTS/.../TriggersRadio/trigger_cd   (Assembly) -> Setup VAR
			//   ITEMS/CDs/cd case(item1|2|3)/trigger_cd (Assembly) -> Setup YOK
			// CD kutularinin tetikleyicileri de ayni adi tasiyor, ama onlarda
			// "Setup" degiskeni yok; FsmVars.Bool hep null doner ve sinyal
			// kalici olarak "(cozulmedi)" olarak birikir.
			if (isInt) {
				if (FsmVars.Int(f, v) == null) return;
			} else {
				if (FsmVars.Bool(f, v) == null) return;
			}
			Signal s = new Signal();
			s.Fsm = f;
			s.Path = path;
			s.FsmName = fsm;
			s.VarName = v;
			s.IsInt = isInt;
			signals.Add(s);
		}

		static PlayMakerFSM FindFsmOn(GameObject go, string fsmName) {
			try {
				PlayMakerFSM[] a = go.GetComponents<PlayMakerFSM>();
				if (a != null) {
					for (int i = 0; i < a.Length; i++) {
						if (a[i] == null) continue;
						if (string.Equals(SafeFsmName(a[i]), fsmName, StringComparison.Ordinal))
							return a[i];
					}
				}
			}
			catch { }
			return null;
		}

		static string SafeFsmName(PlayMakerFSM f) {
			try { if (f == null) return ""; } catch { return ""; }
			try { if (!string.IsNullOrEmpty(f.FsmName)) return f.FsmName; } catch { }
			try { if (!string.IsNullOrEmpty(f.name)) return f.name; } catch { }
			try { if (f.Fsm != null && !string.IsNullOrEmpty(f.Fsm.Name)) return f.Fsm.Name; } catch { }
			return "";
		}

		// ==================================================================
		// GONDERIM
		// ==================================================================

		static float now2() { try { return Time.time; } catch { return 0f; } }

		public static void Tick() {
			try {
				if (!discovered) return;
				if (signals.Count == 0) return;

				// G4: mod degisimi.
				if (changeTrackFsm != null) {
					bool cdMode = false;
					try { cdMode = changeTrackFsm.enabled; } catch { }
					if (cdMode != lastCdMode && now2() - lastCdModeAt >= MODE_COOLDOWN) {
						lastCdMode = cdMode;
						lastCdModeAt = now2();
						if (SteamTransport.HasRemote) {
							string mp = null;
							try { mp = Wire.PathOf(changeTrackFsm.transform); } catch { }
							Send(mp, "ChangeTrack", VAR_CDMODE, cdMode, false, 0);
						}
					}
				}

				for (int i = 0; i < signals.Count; i++) {
					Signal s = signals[i];
					if (s.Fsm == null) continue;

					if (s.IsInt) {
						FsmInt v = FsmVars.Int(s.Fsm, s.VarName);
						if (v == null) continue;
						int now = v.Value;
						if (now == s.LastInt) continue;
						s.LastInt = now;
						if (now < 0) continue;          // -1 = bilinmiyor, gonderme
						Send(s, true, now);
					}
					else {
						FsmBool v = FsmVars.Bool(s.Fsm, s.VarName);
						if (v == null) continue;
						bool now = v.Value;
						if (now == s.LastBool) continue;
						s.LastBool = now;
						Send(s, false, 0);
					}
				}
			}
			catch { }
		}

		static void Send(Signal s, bool isInt, int iVal) {
			try {
				if (!SteamTransport.HasRemote) return;
				float now = Time.time;
				if (now - s.LastSentAt < SEND_COOLDOWN) return;
				s.LastSentAt = now;
				Send(s.Path, s.FsmName, s.VarName, s.LastBool, isInt, iVal);
			}
			catch { }
		}

		static void Send(string path, string fsm, string v, bool b, bool isInt, int iVal) {
			try {
				MemoryStream ms = Protocol.Begin(Protocol.Msg.CdPlayerState);
				BinaryWriter w = new BinaryWriter(ms);
				w.Write(path ?? "");
				w.Write(fsm ?? "");
				w.Write(v ?? "");
				w.Write(isInt);
				if (isInt) w.Write(iVal); else w.Write(b);
				SteamTransport.SendReliable(Protocol.Finish(ms));
			}
			catch { }
		}

		// ==================================================================
		// ALIM
		// ==================================================================

		public static void OnMessage(BinaryReader r) {
			try {
				string path = r.ReadString();
				string fsm = r.ReadString();
				string v = r.ReadString();
				bool isInt = r.ReadBoolean();
				int iVal = 0; bool bVal = false;
				if (isInt) iVal = r.ReadInt32(); else bVal = r.ReadBoolean();

				if (string.IsNullOrEmpty(v)) return;

				// G4: mod paketi ozel bir yol izler (FsmBool degil,
				// PlayMakerFSM.enabled).
				if (string.Equals(v, VAR_CDMODE, StringComparison.Ordinal)) {
					if (changeTrackFsm != null) {
						try { changeTrackFsm.enabled = bVal; } catch { }
						lastCdMode = bVal;
						lastCdModeAt = now2();
						// Oyunun kendi zincirini de tetikle: CD moduna
						// gecerken ChangeChannel/ChangeTrack'i ve Update'i
						// birbirine baglayan olaylar yollanmali.
						try {
							if (changeTrackFsm.Fsm != null)
								changeTrackFsm.SendEvent(bVal ? "PLAY CD" : "STOP CD");
						} catch { }
					}
					return;
				}

				// Onceden bu degiskenin bizim taraftaki karsiligi var mi?
				Signal local = null;
				for (int i = 0; i < signals.Count; i++) {
					if (string.Equals(signals[i].VarName, v, StringComparison.Ordinal)
						&& signals[i].IsInt == isInt) {
						// Ayni isim birden fazla nesnede olabilir
						// (birden fazla arac). YOL eslesmesi varsa onu kullan.
						if (!string.IsNullOrEmpty(path)) {
							for (int k = 0; k < signals.Count; k++) {
								if (string.Equals(signals[k].VarName, v, StringComparison.Ordinal)
									&& string.Equals(signals[k].Path ?? "", path, StringComparison.Ordinal)) {
									local = signals[k];
									break;
								}
							}
						}
						if (local == null) local = signals[i];
						if (local != null) break;
					}
				}
				if (local == null || local.Fsm == null) return;

				// 1) Degeri yaz.
				if (isInt) {
					FsmInt iv = FsmVars.Int(local.Fsm, v);
					if (iv == null) return;
					if (iv.Value != iVal) iv.Value = iVal;
					local.LastInt = iVal;
				}
				else {
					FsmBool bv = FsmVars.Bool(local.Fsm, v);
					if (bv == null) return;
					if (bv.Value != bVal) bv.Value = bVal;
					local.LastBool = bVal;
				}

				// 2) SAHİBİNİN KENDİ AKSİYONLARINI TETİKLE.
				//
				// Dumper kanıtı: `Database/PartsStatus/Radio`[Status] in
				// "Check CD"/"Check Radio" durumlari CDPlayerOK/RadioOK
				// bool'unu okuyup ActivateGameObject(ButtonsCD)/(ButtonsRadio)
				// calistiriyor. Bool'u yazmak TEK BASINA yetmez; FSM
				// yeniden kosmali. Bildirilen tek olay FINISHED.
				//
				// `trigger_disc`[Assembly] icin de ayni mantik: "Check for Part"
				// durumu CDin'i okuyor.
				try {
					if (local.Fsm.Fsm != null) local.Fsm.SendEvent("FINISHED");
				}
				catch { }
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("[CdPlayerSync] paket hatasi: " + e.GetType().Name);
			}
		}

		// ==================================================================
		// TESHIS (smp cd)
		// ==================================================================

		public static string Describe() {
			System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
			sb.Append("cd player: ").Append(signals.Count).Append(" sinyal");
			sb.Append("\n   CDMODE (ChangeTrack acik) = ")
				.Append(changeTrackFsm != null ? (lastCdMode ? "CD" : "radyo") : "?");
			for (int i = 0; i < signals.Count; i++) {
				Signal s = signals[i];
				sb.Append("\n   ").Append(s.VarName).Append(" @ ").Append(s.Path ?? "?");
				if (s.Fsm == null) { sb.Append("  (cozulmedi)"); continue; }
				if (s.IsInt) {
					FsmInt v = FsmVars.Int(s.Fsm, s.VarName);
					sb.Append("  ").Append(v != null ? v.Value.ToString() : "?");
				}
				else {
					FsmBool v = FsmVars.Bool(s.Fsm, s.VarName);
					sb.Append("  ").Append(v != null ? (v.Value ? "ON" : "off") : "?");
				}
			}
			return sb.ToString();
		}

		public static void Reset() {
			signals.Clear();
			changeTrackFsm = null;
			discovered = false;
		}
	}
}