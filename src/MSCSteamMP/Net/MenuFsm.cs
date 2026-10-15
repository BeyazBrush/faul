using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Bir butonun hangi FSM'i tıklamayı gerçekten işlediğini BULUR.
	///
	/// Neden ayrı dosya: bu saf (safkod) bir karar problemidir ve masadeste
	/// test edilebilir olmalidir. Sahne veya Unity gerektiren tek kisim
	/// <see cref="Scan"/> sarmalayicisidir.
	///
	/// SORUNUN KOKU
	/// ------------
	/// Onceki surum, buton nesnesindeki ILK <c>MousePickEvent</c> eylemine sahip
	/// FSM'i aryor ve onun <c>mouseDown</c> alanini gonderiyordu. level0'taki
	/// gercek veri bunu bozdu: ilk FSM <c>SetSize</c> idi ve olayi <c>DOWN</c>'du.
	/// SetSize bir buton ANIMASYON FSM'idir (olaylari FINISHED/DOWN/OFF/OVER,
	/// yani fare durumlari) ve tiklamayi isleyen sey degildir. Sonuc: gonderdigimiz
	/// olay bir butonu buyutmuyordu, hicbir sey olmuyordu.
	///
	/// COZUM: TAHMIN YOK, GEÇIŞ TABLOSU VAR
	/// ------------------------------------
	/// PlayMaker, gecis tablosunda olmayan bir olayi SESSIZCE YOK SAYAR. O
	/// yuzden "bu olay gercekten bir gecise bagli mi" sorusu sorulabilir ve
	/// cevabi kesindir. Biz yalnizca GERCEKTEN gecisi olan olayi gonderiyoruz:
	///
	///   1. Buton nesnesi + alt nesneleri + ust zincirindeki TUM FSM'ler taranir.
	///   2. Her biri icin gecis tablosundaki tum olay adlari cikarilir.
	///   3. <see cref="PickBest"/> puanlama yapar ve (FSM, olay) cifti secer.
	///
	/// Boylece yanlis olaya gonderme ihtimali kalmaz: secilen olay o FSM'in
	/// gecisinde GERCEKTEN var.
	/// </summary>
	public static class MenuFsm {

		/// <summary>
		/// Bir PlayMakerFSM'in okunmus hali. Sadece karar icin gereken alanlar.
		/// </summary>
		public class FsmInfo {
			/// <summary>FSM'in adi (ornegin "SetSize", "Button").</summary>
			public string Name = string.Empty;

			/// <summary>Nesne yolu, teşhis icin ("Interface/Buttons/ButtonNewgame").</summary>
			public string Path = string.Empty;

			/// <summary>FSM buton nesnesinin kendisinde mi?</summary>
			public bool OnButton;

			/// <summary>FSM etkin mi (ProcessEvent !Active'de erken donuyor).</summary>
			public bool Active = true;

			/// <summary>Gecis tablosundaki olay adlari, tekrarsiz.</summary>
			public readonly List<string> Transitions = new List<string>(8);

			/// <summary>MousePickEvent.mouseUp (fare birakildi = tiklama).</summary>
			public string PickUp;

			/// <summary>MousePickEvent.mouseDown (fare basildi = tiklamanin basi).</summary>
			public string PickDown;

			/// <summary>MousePickEvent.mouseOver (imlec butonun uzerinde).</summary>
			public string PickOver;

			/// <summary>MousePickEvent.mouseOff (imlec butondan cikti).</summary>
			public string PickOff;

			/// <summary>GetKeyDown.sendEvent (klavye yolu, ayni olayi gonderir).</summary>
			public string KeyDown;

			/// <summary>FSM baska nesneye olay gonderiyor mu (butonun gorevi bu).</summary>
			public bool NotifiesOther;

			/// <summary>
			/// Onay tercih listesindeki <paramref name="rank"/> sirasindaki
			/// olayi dondurur (gecis tablosunda oldugu icin kesinlikle vardir).
			/// </summary>
			public string BestPreference(int rank) {
				if (rank < 0 || rank >= ConfirmPreference.Length) return null;
				string want = ConfirmPreference[rank];
				for (int i = 0; i < Transitions.Count; i++) {
					if (string.Equals(Transitions[i], want,
						StringComparison.OrdinalIgnoreCase)) return Transitions[i];
				}
				return want;
			}

			/// <summary>Gecis tablosunda bu olay var mi?</summary>
			public bool Has(string eventName) {
				if (string.IsNullOrEmpty(eventName)) return false;
				for (int i = 0; i < Transitions.Count; i++) {
					if (string.Equals(Transitions[i], eventName,
						StringComparison.OrdinalIgnoreCase)) return true;
				}
				return false;
			}

			public override string ToString() {
				return "fsm '" + Name + "' on '" + Path + "'"
					+ (OnButton ? " [butonun kendisi]" : "")
					+ (Active ? "" : " [PASIF]")
					+ " gecisler=[" + string.Join(", ", Transitions.ToArray()) + "]"
					+ " pickUp=" + Show(PickUp) + " pickDown=" + Show(PickDown)
					+ " pickOver=" + Show(PickOver) + " pickOff=" + Show(PickOff)
					+ " key=" + Show(KeyDown)
					+ (NotifiesOther ? " digerNesneyeGonderir" : "");
			}

			static string Show(string s) { return string.IsNullOrEmpty(s) ? "-" : s; }
		}

		// ------------------------------------------------------------------
		// Siniflandirma: hangi olaylar tIKLAMA, hangileri sadece imleç?
		// ------------------------------------------------------------------

		/// <summary>
		/// Yalnizca imlecin buton UZERINDE olup olmamagini anlatan olaylar.
		///
		/// Bunlar bir butonu tiklamaz, yalnizca vurgular. Gecis tablosunda
		/// SADECE bunlar varsa o bir buton FSM'i degildir: buton goruntusunu
		/// buyuten bir animasyon ya da hover efektidir (gercek ornek: SetSize,
		/// olaylari FINISHED/DOWN/OFF/OVER).
		/// </summary>
		static readonly string[] HoverOnly = {
			"OVER", "MOUSE OVER", "OFF", "MOUSE OFF", "IN", "OUT", "MOUSE IN", "MOUSE OUT",
			"HOVER", "EXIT", "ENTERED", "LEFT", "SHOW", "HIDE",
		};

		/// <summary>
		/// Yalnizca fare basmasini anlatan olaylar. Tiklama tamamlanmamis olabilir;
		/// butonlar cogunlukla birakmayi bekler. Bu yuzden mouseUp'tan sonra
		/// tercih edilir.
		/// </summary>
		static readonly string[] PressOnly = {
			"DOWN", "MOUSEDOWN", "MOUSE DOWN", "PRESS", "PUSH",
		};

		/// <summary>
		/// Onay/ tiklama anlami tasiyan olay adlari, gucluden zayifa.
		///
		/// "ENTER" basa konur: level0'taki menulerde hem GetKeyDown hem
		/// MousePickEvent bunu gonderiyor, yani oyunun gercek onay olayi.
		/// </summary>
		/// "PROCEED" burada YOK: PlayMaker onu yerlesik bir olay olarak
		/// kullaniyor, yani gecis tablosunda gorunmesi oyunun tiklamasi
		/// degil durumun normal bitisi demek.
		public static readonly string[] ConfirmPreference = {
			"ENTER", "CLICK", "ONCLICK", "SUBMIT", "SELECT", "CONFIRM", "OKAY", "OK",
			"BEGIN", "START", "GO", "YES", "MOUSE UP", "MOUSEUP", "RELEASE",
		};

		/// <summary>FSM adi buton adina benzemiyorsa, animasyon olma olasiligi.</summary>
		static readonly string[] AnimationWords = {
			"SIZE", "SCALE", "ANIM", "COLOR", "COLOUR", "SOUND", "MOVE", "ROTATE",
			"WIRE", "SHADER", "MATERIAL", "LIGHT", "FADE", "TWEEN", "SPIN",
		};

		/// <summary>
		/// PlayMaker'in YERLESIK olaylari.
		///
		/// Bunlar gecis tablosunda gorunse bile tiklama anlami tasimaz:
		/// FINISHED bir durumun normal bitisidir. "Gecislerinde ne var"
		/// sorusunda sayilmazlar.
		///
		/// Gercek ornek: SetSize'in gecisleri FINISHED/DOWN/OFF/OVER. FINISHED
		/// sayilmazsa kalan ucu de imlec olayidir ve "bu bir buton degil"
		/// sonucu dogru cikar.
		/// </summary>
		static readonly string[] Reserved = {
			"FINISHED", "FINISHEDONHALT", "UPDATE", "CANCEL", "ABORT", "SUCCESS",
			"FAIL", "PROCEED", "STOP", "RESET", "REPEAT", "IGNORE", "DONE",
		};

		// ------------------------------------------------------------------
		// Saf karar
		// ------------------------------------------------------------------

		/// <summary>
		/// En iyi (FSM, olay) ciftini secer. GECIS TABLOSU OLMAYAN hicbir
		/// cift aday sayilmaz.
		///
		/// Puanlama gerekceleri tek tek:
		///   * <c>mouseUp</c> gecisi varsa guclu: fare birakildiginda gonderilen
		///     olay, yani tiklamanin TAMAMLANDIGI an.
		///   * onay adi tasiyan gecis: oyunun butonu "tiklaninca su is olur"
		///     diye isaretledigi an.
		///   * buton nesnesinin kendisinde olmasi: cogu menu butonu mantigini
		///     kendi nesnesinde toplar, cocuklarda animasyon vardir.
		///   * FSM adi buton adina benzerse: "ButtonNewgame" nesnesinde "Newgame"
		///     adi bir FSM var demektir.
		///   * animasyon kelimesi: hover/olcek efektleri.
		///   * yalnizca imlec gecisleri: kesinlikle degil.
		/// </summary>
		public static int PickBest(List<FsmInfo> infos, out string eventName) {
			eventName = null;
			if (infos == null) return -1;

			int bestScore = 0;
			FsmInfo best = null;

			for (int i = 0; i < infos.Count; i++) {
				FsmInfo info = infos[i];
				if (info == null) continue;

				// Gecis tablosu bos olan FSM'e gondermenin anlami yok: PlayMaker
				// olayi sessizce yok sayar, yani sifir etki.
				if (info.Transitions.Count == 0) continue;

				int score = Score(info);
				if (best == null || score > bestScore) {
					best = info;
					bestScore = score;
				}
			}

			if (best == null) return -1;
			eventName = EventFor(best);
			return bestScore;
		}

		/// <summary>Tek bir FSM'in puani. Saf: yalnizca okunmus veriye bakar.</summary>
		public static int Score(FsmInfo info) {
			if (info == null) return -1;
			if (info.Transitions.Count == 0) return -1;

			int score = 0;

			// Gecis tablosunda onay adi var mi, varsa guclu.
			int pref = PreferenceRank(info);
			if (pref == 0) score += 90;
			else if (pref == 1) score += 74;
			else if (pref == 2) score += 58;
			else if (pref == 3) score += 42;

			// mouseUp gecisi: tiklamanin tamamlandigi an. En guclu sinyal.
			if (!string.IsNullOrEmpty(info.PickUp) && info.Has(info.PickUp)) score += 60;
			// Yalnizca mouseDown: basma, tiklama degil. Zayif.
			else if (!string.IsNullOrEmpty(info.PickDown) && info.Has(info.PickDown)) score += 12;

			// Klavye yolu: GetKeyDown gonderdigi olay da geciste mi?
			if (!string.IsNullOrEmpty(info.KeyDown) && info.Has(info.KeyDown)) score += 20;

			// Buton nesnesinin kendisi.
			if (info.OnButton) score += 25;

			// FSM adi butona benziyor mu?
			if (ButtonNameMatches(info)) score += 35;

			// Baska nesneye olay gonderiyor mu (butonun isi menuyu uyarmak).
			if (info.NotifiesOther) score += 20;

			// Animasyon/efekt FSM'i cezasi.
			if (HasAnyWord(info.Name, AnimationWords)) score -= 120;

			// Yalnizca imlec olaylari: bu bir buton degil.
			if (OnlyHover(info)) score -= 150;

			// Pasif FSM: olay yutulacak.
			if (!info.Active) score -= 200;

			return score;
		}

		/// <summary>
		/// Secilen FSM icin gonderilecek olay.
		///
		/// Sira: gecis tablosunda gercekten olan onay olayi, yoksa mouseUp,
		/// yoksa herhangi bir gecis. Hicbiri geciste degilse null.
		/// </summary>
		public static string EventFor(FsmInfo info) {
			if (info == null) return null;

			int pref = PreferenceRank(info);
			if (pref >= 0) return info.BestPreference(pref);

			if (!string.IsNullOrEmpty(info.PickUp) && info.Has(info.PickUp)) return info.PickUp;
			if (!string.IsNullOrEmpty(info.KeyDown) && info.Has(info.KeyDown)) return info.KeyDown;
			if (!string.IsNullOrEmpty(info.PickDown) && info.Has(info.PickDown)) return info.PickDown;

			// Son care: imlec ve yerlesik olayi olmayan ilk gecis.
			for (int i = 0; i < info.Transitions.Count; i++) {
				string n = info.Transitions[i];
				if (IsReserved(n)) continue;
				if (!IsHoverOnly(n)) return n;
			}
			return null;
		}

		/// <summary>
		/// Onay tercih listesindeki sirayi dondurur; gecis tablosunda yoksa -1.
		/// </summary>
		public static int PreferenceRank(FsmInfo info) {
			if (info == null) return -1;
			for (int p = 0; p < ConfirmPreference.Length; p++) {
				if (info.Has(ConfirmPreference[p])) return p;
			}
			return -1;
		}

		/// <summary>
		/// Onay/TIKLAMA sinyalı taşıyan geçiş var mı?
		///
		/// false dönerse: FSM'in geçişleri yalnızca yerleşik olaylar (FINISHED
		/// vb. — durumun normal bitişi), imleç olayları (OVER/OFF vb. — sadece
		/// vurgu) veya basma olaylarından (DOWN vb. — tıklama henüz tamamlanmadı)
		/// oluşuyor. Yani bu bir buton değil, bir görsel yardımcıdır.
		///
		/// Gerçek örnek: SetSize'in geçişleri FINISHED/DOWN/OFF/OVER. Üçü de
		/// sayılmıyor, dolayısıyla "bu bir buton değil" sonucu doğru çıkar.
		/// </summary>
		public static bool OnlyHover(FsmInfo info) {
			if (info == null || info.Transitions.Count == 0) return true;
			for (int i = 0; i < info.Transitions.Count; i++) {
				string n = info.Transitions[i];
				if (IsReserved(n)) continue;
				if (IsHoverOnly(n)) continue;
				if (IsPressOnly(n)) continue;
				return false;
			}
			return true;
		}

		public static bool IsHoverOnly(string name) {
			return Contains(HoverOnly, name);
		}

		public static bool IsPressOnly(string name) {
			return Contains(PressOnly, name);
		}

		/// <summary>PlayMaker'in yerlesik (tiklamaya ait olmayan) olayi mi?</summary>
		public static bool IsReserved(string name) {
			return Contains(Reserved, name);
		}

		public static bool Contains(string[] list, string name) {
			if (string.IsNullOrEmpty(name)) return false;
			for (int i = 0; i < list.Length; i++) {
				if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		static bool HasAnyWord(string name, string[] words) {
			if (string.IsNullOrEmpty(name)) return false;
			for (int i = 0; i < words.Length; i++) {
				if (name.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			return false;
		}

		/// <summary>
		/// FSM adi buton adina benziyor mu?
		///
		/// Nesne "ButtonNewgame", FSM adi "Newgame" ya da "Button" ise eslesir.
		/// Nesne adindaki "Button" oneki ve bosluk/tire temizlenir.
		/// </summary>
		public static bool ButtonNameMatches(FsmInfo info) {
			if (info == null || string.IsNullOrEmpty(info.Name)) return false;
			string fsm = Normalize(info.Name);
			if (string.IsNullOrEmpty(fsm)) return false;
			// Genel bir ad her butonla eslesmemeli.
			if (fsm == "BUTTON" || fsm == "FSM") return false;
			string obj = Normalize(info.Path != null && info.Path.Length > 0
				? info.Path.Substring(info.Path.LastIndexOf('/') + 1)
				: string.Empty);
			if (string.IsNullOrEmpty(obj)) return false;
			return obj == fsm || obj.IndexOf(fsm, StringComparison.OrdinalIgnoreCase) >= 0
				|| fsm.IndexOf(obj, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static string Normalize(string s) {
			if (string.IsNullOrEmpty(s)) return string.Empty;
			string t = s.ToLowerInvariant();
			t = t.Replace("button", "");
			t = t.Replace(" ", "").Replace("-", "").Replace("_", "");
			return t;
		}

		// ------------------------------------------------------------------
		// Sahne taramasi (Unity gerektiren kisim)
		// ------------------------------------------------------------------

		/// <summary>
		/// Bir PlayMakerFSM'i <see cref="FsmInfo"/>'ya cevirir.
		///
		/// Gecis tablosu hem durum gecislerinden hem de global gecislerden
		/// toplanir. Global gecisler onemli: bir buton, "her nesne uzerinde"
		/// turen bir gecise bagli olabilir.
		/// </summary>
		public static FsmInfo Scan(PlayMakerFSM comp, bool onButton) {
			FsmInfo info = new FsmInfo();
			if (comp == null) return info;

			try { info.Name = comp.FsmName ?? string.Empty; } catch { }
			try { info.Path = PathOf(comp.gameObject); } catch { }
			try { info.Active = comp.Active; } catch { }
			info.OnButton = onButton;

			try {
				FsmState[] states = comp.FsmStates;
				if (states != null) {
					for (int s = 0; s < states.Length; s++) {
						// DURUM BASINA AYRI KORUMA.
						//
						// Canli log (her acilista, iki tarafta da):
						//   menu fsm scan failed on 'Init'/'Fetch'/'Scroll': NullReferenceException
						// Hepsi Interface/Credits/CreditsTV/creditsCAM/name uzerinde, yani PASIF
						// bir kredi kamerasi. Pasif nesneye erisen st.Transitions / st.Actions
						// okumasi patliyor ve istisna TUM donguyu dusuruyordu: FSM'in kalan
						// durumlari hic okunmuyor, yani gecis tablosu yarim ve tarama guvenilmez
						// oluyordu. Action basina koruma yetmiyor: patlama ReadAction'in degil,
						// durumun kendisinin okumasinda.
						try {
							FsmState st = states[s];
							if (st == null) continue;

							FsmTransition[] tr = st.Transitions;
							if (tr != null) {
								for (int t = 0; t < tr.Length; t++) {
									try { Add(info, TransitionName(tr[t])); }
									catch (Exception) { }
								}
							}

							FsmStateAction[] actions = st.Actions;
							if (actions == null) continue;
							for (int a2 = 0; a2 < actions.Length; a2++) {
								try { ReadAction(info, actions[a2]); }
								catch (Exception) { /* tek action atlanir, tarama surer */ }
							}
						}
						catch (Exception) { /* tek durum atlanir, tarama surer */ }
					}
				}
			}
			catch (Exception e) {
				SmpLog.Warning("menu fsm scan failed on '" + info.Name + "': "
					+ e.GetType().Name);
			}
			return info;
		}

		static string TransitionName(FsmTransition t) {
			if (t == null) return null;
			try {
				// EventName, zaten bos string donmek yerine olay nesnesinin adini
				// vermek icin var; yine de guvende tutuyoruz.
				string n = t.EventName;
				if (!string.IsNullOrEmpty(n)) return n;
				return EventName(t.FsmEvent);
			}
			catch { return null; }
		}

		static void ReadAction(FsmInfo info, FsmStateAction action) {
			if (action == null) return;

			MousePickEvent pick = action as MousePickEvent;
			if (pick != null) {
				// Sadece ilk (yani birincil) degeri al; birden fazla MousePickEvent
				// varsa en dolu olan wins.
				string up = EventName(pick.mouseUp);
				string down = EventName(pick.mouseDown);
				string over = EventName(pick.mouseOver);
				string off = EventName(pick.mouseOff);
				if (up == null) up = string.Empty;
				if (down == null) down = string.Empty;
				if (over == null) over = string.Empty;
				if (off == null) off = string.Empty;
				if (info.PickUp == null || (info.PickUp == "" && up != "")) info.PickUp = up;
				if (info.PickDown == null || (info.PickDown == "" && down != "")) info.PickDown = down;
				// mouseOver/mouseOff YALNIZCA "imlec" bilgisidir; tiklama
				// adayi olarak kullanilmaz. Ama teşhis ciktisinda gorunmesi
				// degerli: SetSize'in DOWN/OFF/OVER cikisi buradan geliyor.
				if (info.PickOver == null || (info.PickOver == "" && over != "")) info.PickOver = over;
				if (info.PickOff == null || (info.PickOff == "" && off != "")) info.PickOff = off;
				return;
			}

			GetKeyDown key = action as GetKeyDown;
			if (key != null) {
				string n = EventName(key.sendEvent);
				if (!string.IsNullOrEmpty(n) && string.IsNullOrEmpty(info.KeyDown)) info.KeyDown = n;
				return;
			}

			SendEvent se = action as SendEvent;
			if (se != null) {
				// Butonun isi, baska bir nesneyi uyarmak. eventTarget kendisi
				// degilse "menuyu bilgilendiriyor" sayilir.
				//
				// dnSpy: FsmStateAction.Owner bir GameObject'tir (Fsm degil),
				// FsmEventTarget.gameObject bir FsmOwnerDefault'tir (kucuk g) ve
				// onun GameObject'u bir FsmGameObject; gercek nesne Value'da.
				try {
					if (se.eventTarget != null
						&& se.eventTarget.gameObject != null
						&& se.eventTarget.gameObject.GameObject != null
						&& se.eventTarget.gameObject.GameObject.Value != null
						&& se.eventTarget.gameObject.GameObject.Value != action.Owner) {
						info.NotifiesOther = true;
					}
				}
				catch { }
				return;
			}
		}

		static void Add(FsmInfo info, string eventName) {
			if (string.IsNullOrEmpty(eventName)) return;
			if (info.Has(eventName)) return;
			info.Transitions.Add(eventName);
		}

		static string EventName(FsmEvent e) {
			if (e == null) return null;
			try {
				string n = e.Name;
				return string.IsNullOrEmpty(n) ? null : n;
			}
			catch { return null; }
		}

		/// <summary>Nesne yolu: "Interface/Buttons/ButtonNewgame".</summary>
		public static string PathOf(GameObject go) {
			if (go == null) return string.Empty;
			try {
				string path = go.name;
				Transform t = go.transform;
				int guard = 0;
				while (t != null && t.parent != null && guard < 32) {
					guard++;
					t = t.parent;
					if (t == null) break;
					path = t.name + "/" + path;
				}
				return path;
			}
			catch { return string.Empty; }
		}

		/// <summary>
		/// Butonun ve menusunun TUM aday FSM'lerini toplar.
		///
		/// Kapsam genisletildi; gercek kayit bunu zorunlu kildi:
		///
		///   NEW GAME button is 'Interface/Buttons/ButtonNewgame'.
		///      [-233] fsm 'SetSize'    on 'Interface/Buttons/ButtonNewgame' gecisler=[OVER, OFF, DOWN, FINISHED]
		///      [-115] fsm 'Check Save' on 'Interface/Buttons'              gecisler=[SUCCESS, FAIL]
		///
		/// Yani butonun kendisinde yalnizca bir ANIMASYON fsm'i var ve tiklamayi
		/// isleyen sey butonun altinda degil. Onceki surum ust zincirde yalnizca
		/// GetComponents&lt;PlayMakerFSM&gt;() cagirip NESNENIN KENDISINI
		/// taruyordu, cocuklarini degil. level0 string tablosu bunu dogruluyor:
		/// ButtonCancel, ButtonNewgame, wires, ButtonCredits ardisik, yani
		/// 'wires' butonun KARDESI ve tiklamayi isleyen fsm orada olabilir.
		///
		/// Taranan kume:
		///   * butonun alt agaci
		///   * her atasinin alt agaci (Interface/Buttons, Interface, ...)
		///   * 'Logic' nesnesi (MSCLoader bu nesneyi okuyor:
		///     GameObject.Find("Logic").GetPlayMaker("FSM").GetState("State 3")
		///     .GetAction&lt;ActivateGameObject&gt;(1))
		///
		/// Ayni FSM birden fazla kokten gelirse tekrar sayilmaz.
		/// </summary>
		public static List<FsmInfo> Collect(GameObject button) {
			List<FsmInfo> list = new List<FsmInfo>(32);
			if (button == null) return list;
			HashSet<PlayMakerFSM> seen = new HashSet<PlayMakerFSM>();
			string buttonPath = PathOf(button);

			// 1) Butonun alt agaci.
			AddSubtree(list, seen, button, buttonPath);

			// 2) Her atasinin alt agaci (cocuklar dahil).
			try {
				Transform t = button.transform;
				int guard = 0;
				while (t != null && t.parent != null && guard < 6) {
					guard++;
					t = t.parent;
					if (t == null) break;
					AddSubtree(list, seen, t.gameObject, buttonPath);
				}
			}
			catch (Exception e) {
				SmpLog.Warning("menu: ancestor FSM scan failed: " + e.GetType().Name);
			}

			// 3) 'Logic' koku: ana menu mantiginin durdugu yer olabilir.
			try {
				GameObject logic = GameObject.Find("Logic");
				if (logic != null) AddSubtree(list, seen, logic, buttonPath);
			}
			catch { }

			return list;
		}

		static void AddSubtree(List<FsmInfo> list, HashSet<PlayMakerFSM> seen,
			GameObject root, string buttonPath) {
			if (root == null) return;
			PlayMakerFSM[] fsms;
			try {
				fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
			}
			catch { return; }

			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM f = fsms[i];
				if (f == null) continue;
				if (!seen.Add(f)) continue;
				// Butonun kendisi mi? Yolu buton nesnesinin yoluyla ayni olan.
				bool onButton = string.Equals(PathOf(f.gameObject), buttonPath,
					StringComparison.Ordinal);
				list.Add(Scan(f, onButton));
			}
		}

		// ------------------------------------------------------------------
		// Gizleme karari (saf)
		// ------------------------------------------------------------------

		/// <summary>
		/// Bu nesne gizlenebilir mi?
		///
		/// Saf fonksiyon (nesne, kamera ya da Unity gerektirmez) ve masaüstünde
		/// test edilir: tests\MenuProbe.cs.
		///
		/// Neden kameraya bakıyoruz: ana menü butonları PlayMaker'ın
		/// MousePickEvent eylemiyle tıklanıyor ve o eylem kameradan ışın atıyor.
		/// Ana menü kamerasını SetActive(false) ile kapatırsak Camera.main null
		/// olur, MousePickEvent hiçbir şey yapmaz ve NEW GAME onay panosundaki
		/// BEGIN butonu TIKLANAMAZ. Hata sessizdir.
		///
		/// Gerçek kayıt: "menu: 'Car' carries camera 'motor'; NOT hidden".
		/// </summary>
		public static bool CanHide(string objectName, string cameraName) {
			return string.IsNullOrEmpty(cameraName);
		}

		// ------------------------------------------------------------------
		// NEW GAME onay panelini metninden bul
		// ------------------------------------------------------------------

		/// <summary>
		/// Onay panelinin kokunu bulur; bulunamazsa null.
		///
		/// Neden metin aramasi: panelin gorunur yuzu kendi metniyle belli
		/// oluyor. level0 kaniti:
		///
		///   NEW GAME WILL REPLACE ANY EXISTING SAVE!
		///   NEW GAME
		///   BEGIN
		///   CANCEL
		///
		/// Yani panelin bir TextMesh'i bu cumleyi tasiyor. O nesneden yukari
		/// cikilip ILK PASIF ataya kadar yurunuyoruz: panel simdi pasif
		/// oldugu icin ilk pasif ata tam olarak panelin koku (ya da onu
		/// saran bir konteyner; ikisi de ayni islevi gorur).
		///
		/// Neden oyun mantigini calistirmiyoruz: ilk adimda oyunun TIKLAMA
		/// mantigini calistirmamiz gerekiyordu ve iki deneme de yanlis cikti
		/// ('DOWN' -> SetSize). Paneli kendimiz etkinlestirmek o mantiga hic
		/// ihtiyac duymaz; BEGIN/CANCEL gercek butonlardir ve fare
		/// tiklamasiyla calisir.
		/// </summary>
		public static GameObject FindConfirmPanel() {
			try {
				GameObject root = SceneDump.ActiveSceneRoot();
				if (root == null) return null;

				TextMesh[] texts = root.GetComponentsInChildren<TextMesh>(true);
				for (int i = 0; i < texts.Length; i++) {
					TextMesh tm = texts[i];
					if (tm == null) continue;
					string s;
					try { s = tm.text; } catch { continue; }
					if (string.IsNullOrEmpty(s)) continue;
					if (s.IndexOf("REPLACE ANY", StringComparison.OrdinalIgnoreCase) < 0) continue;

					GameObject panel = PanelRootOf(tm.gameObject);
					if (panel != null) return panel;
				}
			}
			catch (Exception e) {
				SmpLog.Warning("menu: confirm panel search failed: " + e.GetType().Name);
			}
			return null;
		}

		/// <summary>
		/// Bir panel cogu zaman pasif durur. Metnin bulundugu nesneden yukari
		/// cikip ILK PASIF atayi donduruyoruz; aktif bir ataya cikinca duruyoruz.
		/// </summary>
		static GameObject PanelRootOf(GameObject from) {
			GameObject best = null;
			Transform t = from != null ? from.transform : null;
			int guard = 0;
			while (t != null && guard < 32) {
				guard++;
				if (!t.gameObject.activeSelf) best = t.gameObject;
				else if (best != null) break;      // aktif bir ataya cikti: dur
				t = t.parent;
			}
			if (best != null) return best;
			return from;
		}
	}
}
