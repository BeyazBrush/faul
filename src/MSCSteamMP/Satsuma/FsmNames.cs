using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// dnSpy ile doğrulanan isim tabanlı FSM değişken çözümlemesi.
	///
	/// Bulgu: PlayMaker, değişkenleri ada göre çekmek için public yardımcılar sunuyor.
	///   Fsm.GetFsmBool(String)    token 0x0600020C
	///   Fsm.GetFsmInt(String)     token 0x0600020B
	///   Fsm.GetFsmFloat(String)   token 0x0600020A
	///   Fsm.GetFsmString(String)  token 0x0600020D
	///   Fsm.GetFsmVector3(String) token 0x0600020F
	///   Fsm.HasEvent(String)      token 0x060001E7
	///
	/// Neden önemli: elle yazılmış "Installed" / "Tightness" gibi isimler build'e göre
	/// değişebiliyor ve bazı FSM'lerde hiç yok. Oyun kendi çözümleyicisini kullanmak,
	/// (a) adı bilmemiz gerekmiyor, (b) aradığımız isim yoksa kesin olarak öğreniyoruz.
	///
	/// HasEvent neden gerekli: bir olayı karşı tarafa göndermeden önce hedef FSM'in o
	/// olayı gerçekten tanımladığını doğrulayabiliyoruz. Bildirilmemiş bir olay göndermek
	/// karşı tarafta hiçbir şey yapmaz ve hatanın kaynağı görünmez olur; şimdi paket daha
	/// gönderilmeden sayıya dönüşüyor.
	/// </summary>
	public static class FsmNames {

		/// <summary>
		/// Bir FSM'nin GERÇEK adı: `FsmName` -> `name` -> `Fsm.Name`.
		///
		/// NEDEN ÜÇ KADEME: `PlayMakerFSM.FsmName` bazı bileşenlerde BOŞ döner
		/// (canlı kanıt: ölüm FSM'i hiç bulunamadı, log 21:03 L727; CD player
		/// için aynı tuzak). Oysa gerçek ad `Fsm.Name` içinde duruyor. Tek
		/// kademe kullanan kod o FSM'leri sessizce atlar.
		///
		/// Kapı senkronunda bu tam bir çalışmama olarak görünüyor: kapı
		/// keşfinde `FsmName` boş dönerse kapı tabloya hiç girmez, olay
		/// kuyruğa alınmaz ve karşı tarafta kapı hiç açılmaz — hata da
		/// görünmez (yalnızca `UnresolvedDoors` sayacı artar).
		///
		/// Sıra önemli: `FsmName` doğruysa onu koru (en yaygın yol), boşsa
		/// bileşen adına, o da yoksa FSM'in kendi adına düş.
		/// </summary>
		public static string RealName(PlayMakerFSM fsm) {
			if (fsm == null) return "";
			try { if (!string.IsNullOrEmpty(fsm.FsmName)) return fsm.FsmName; } catch { }
			try { if (!string.IsNullOrEmpty(fsm.name)) return fsm.name; } catch { }
			try {
				if (fsm.Fsm != null && !string.IsNullOrEmpty(fsm.Fsm.Name)) return fsm.Fsm.Name;
			} catch { }
			return "";
		}

		/// <summary>
		/// DİKKAT: Bu yardımcılar yalnızca oyun İÇİNDE çağrılmalıdır.
		///
		/// Fsm.GetFsmBool ve Fsm.HasEvent, PlayMakerGlobals.Instance'a uzanır; bu bir Unity
		/// internal call olduğu için masaüstü CLR'de SecurityException atar. tests/PatchProbe.cs
		/// bu yüzden sadece hook hedeflerinin doğru çözüldüğünü yansıtır, bu metotları çağırmaz.
		/// </summary>

		/// <summary>
		/// Olay hedef FSM'in bildirdiği listede var mı?
		///
		/// DNSPY İLE DOĞRULANDI (bu düzeltmenin sebebi): `Fsm.HasEvent`
		/// (token 0x060001E7) karşılaştırmayı `fsmEvent.Name == eventName`
		/// ile yapıyor — yani BÜYÜK/KÜÇÜK HARFE DUYARLI, ordinal.
		///
		///   public bool HasEvent(string eventName) {
		///       foreach (FsmEvent fsmEvent in this.events)
		///           if (fsmEvent.Name == eventName) return true;   // <- ==
		///       return false;
		///   }
		///
		/// CANLI HATA: kapı keşfi `DOOR_EVENTS = { "OPEN", "CLOSE", "USE" }`
		/// dizisiyle bu kontrole soruyordu. FSM adları listesi
		/// (`DOOR_FSMS = { "Use", "UseOld", "Knob" }`) doğru harfi
		/// kullanıyordu ama olay adları hepsi BÜYÜK HARFTİ. PlayMaker'ın
		/// bildirimi "Open"/"Close"/"Use" ise (MSC'nin tümü bu yazımı
		/// kullanıyor) `HasEvent` FALSE döner ve kapı `declared == 0`
		/// yolundan SESSİZCE atlanır → kapı hiç senkronlanmaz.
		/// "Kapıyı açtım, karşı tarafta görünmüyor" şikayetinin
		/// sessiz bir kaynağı buydu.
		///
		/// ÇÖZÜM: önce tam eşleşme (hızlı yol), olmazsa büyük/küçük harf
		/// duyarsız ikinci geçiş. `Fsm.events` listesi kısa olduğu için
		/// ikinci geçiş ucuz. Tel formatı DEĞİŞMEZ: gönderilen olay adı
		/// zaten büyük harfe normalize ediliyor.
		/// </summary>
		public static bool HasEvent(PlayMakerFSM fsm, string eventName) {
			if (fsm == null || string.IsNullOrEmpty(eventName)) return false;
			try {
				Fsm f = fsm.Fsm;
				if (f == null) return false;
				// Tam eşleşme: oyunun kendi yolu (en yaygın durum).
				if (f.HasEvent(eventName)) return true;
			}
			catch { return false; }

			// İkinci geçiş: aynı ad, farklı harf. `FsmEvents` toplama
			// özelliğini kullanıyoruz; elle yürümek `Fsm.events` alanının
			// private olması nedeniyle mümkün değil.
			try {
				FsmEvent[] all = fsm.FsmEvents;
				if (all == null) return false;
				for (int i = 0; i < all.Length; i++) {
					if (all[i] == null || all[i].Name == null) continue;
					if (string.Equals(all[i].Name, eventName, StringComparison.OrdinalIgnoreCase))
						return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>Ada göre bool değişken (GetFsmBool). Yoksa null.</summary>
		/// <remarks>
		/// DNSPY: `FsmVariables.GetFsmBool` da `FindFsmBool` üzerinden
		/// `fsmBool.Name == name` karşılaştırması yapıyor — BÜYÜK/KÜÇÜK HARFE
		/// DUYARLI. Bu yüzden aşağıda tam eşleşme denenir, olmazsa büyük/küçük
		/// harf duyarsız ikinci geçiş yapılır. Aksi hâlde oyun "rot" olarak
		/// bildirdiğinde bizim "Rot" aramamız sessizce null dönerdi (kapı yönü
		/// ölçülemez, gelen olay yanlış uygulanırdı).
		/// </remarks>
		public static FsmBool Bool(PlayMakerFSM fsm, string name) {
			FsmBool v = BoolExact(fsm, name);
			if (v != null) return v;
			return BoolFuzzy(fsm, name);
		}

		static FsmBool BoolExact(PlayMakerFSM fsm, string name) {
			if (fsm == null || string.IsNullOrEmpty(name)) return null;
			try { return fsm.Fsm.GetFsmBool(name); } catch { return null; }
		}

		static FsmBool BoolFuzzy(PlayMakerFSM fsm, string name) {
			try {
				FsmBool[] all = fsm.FsmVariables.BoolVariables;
				if (all == null) return null;
				for (int i = 0; i < all.Length; i++)
					if (all[i] != null && all[i].Name != null
						&& string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase))
						return all[i];
			}
			catch { }
			return null;
		}

		public static FsmInt Int(PlayMakerFSM fsm, string name) { return IntExact(fsm, name) ?? IntFuzzy(fsm, name); }

		static FsmInt IntExact(PlayMakerFSM fsm, string name) {
			if (fsm == null || string.IsNullOrEmpty(name)) return null;
			try { return fsm.Fsm.GetFsmInt(name); } catch { return null; }
		}

		static FsmInt IntFuzzy(PlayMakerFSM fsm, string name) {
			try {
				FsmInt[] all = fsm.FsmVariables.IntVariables;
				if (all == null) return null;
				for (int i = 0; i < all.Length; i++)
					if (all[i] != null && all[i].Name != null
						&& string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase))
						return all[i];
			}
			catch { }
			return null;
		}

		public static FsmFloat Float(PlayMakerFSM fsm, string name) { return FloatExact(fsm, name) ?? FloatFuzzy(fsm, name); }

		static FsmFloat FloatExact(PlayMakerFSM fsm, string name) {
			if (fsm == null || string.IsNullOrEmpty(name)) return null;
			try { return fsm.Fsm.GetFsmFloat(name); } catch { return null; }
		}

		static FsmFloat FloatFuzzy(PlayMakerFSM fsm, string name) {
			try {
				FsmFloat[] all = fsm.FsmVariables.FloatVariables;
				if (all == null) return null;
				for (int i = 0; i < all.Length; i++)
					if (all[i] != null && all[i].Name != null
						&& string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase))
						return all[i];
			}
			catch { }
			return null;
		}

		public static FsmString Str(PlayMakerFSM fsm, string name) { return StrExact(fsm, name) ?? StrFuzzy(fsm, name); }

		static FsmString StrExact(PlayMakerFSM fsm, string name) {
			if (fsm == null || string.IsNullOrEmpty(name)) return null;
			try { return fsm.Fsm.GetFsmString(name); } catch { return null; }
		}

		static FsmString StrFuzzy(PlayMakerFSM fsm, string name) {
			try {
				FsmString[] all = fsm.FsmVariables.StringVariables;
				if (all == null) return null;
				for (int i = 0; i < all.Length; i++)
					if (all[i] != null && all[i].Name != null
						&& string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase))
						return all[i];
			}
			catch { }
			return null;
		}

		/// <summary>
		/// Object (AudioClip vb.) değişkenini döndürür (yoksa null).
		///
		/// NOT: ArrayList'ler burada DEĞİLDİR. dnSpy ile doğrulandı:
		/// `FsmVariables` yalnızca 13 dizi tutuyor (bool/int/float/string/
		/// vector2/vector3/color/rect/quaternion/gameObject/object/material/
		/// texture) — `ArrayLists` alanı YOK. ArrayMaker listeleri ayrı bir
		/// `PlayMakerArrayListProxy` bileşeninde yaşıyor (bkz.
		/// `ArrayListAdd.OnEnter` -> SetUpArrayListProxyProxy(reference)).
		/// </summary>
		public static FsmObject Obj(PlayMakerFSM fsm, string name) {
			if (fsm == null || fsm.Fsm == null || string.IsNullOrEmpty(name)) return null;
			try {
				FsmObject[] all = fsm.FsmVariables.ObjectVariables;
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] == null) continue;
					if (all[i].Name == name) return all[i];
				}
			}
			catch { }
			return null;
		}

		/// <summary>
		/// İsmi bilinen bir değişkenin, bilinen alternatiflerden hangisinin gerçekten
		/// bildirildiğini döndürür.
		///
		/// Elle taramak yerine oyunun kendi çözümleyicisine soruyoruz; bu yüzden
		/// "Installed" gibi isimleri hardcode etmeye gerek kalmıyor, sadece aday listesini
		/// veriyoruz.
		/// </summary>
		public static string FirstDeclared(PlayMakerFSM fsm, params string[] candidates) {
			if (fsm == null || candidates == null) return null;
			for (int i = 0; i < candidates.Length; i++) {
				string n = candidates[i];
				if (string.IsNullOrEmpty(n)) continue;
				if (Bool(fsm, n) != null || Int(fsm, n) != null || Float(fsm, n) != null
					|| Str(fsm, n) != null || fsm.Fsm.GetFsmGameObject(n) != null) {
					return n;
				}
			}
			return null;
		}

		/// <summary>
		/// FSM'in bildirdiği tüm değişken adları (sıralı).
		///
		/// Clock/hava/eşya keşfi bununla çalışır: aday isimleri denenir, bulunamayanların
		/// ne bildirdiği bu listeden görülür. 'smp scan' komutu da aynı listeyi döker.
		/// </summary>
		public static List<string> DeclaredNames(PlayMakerFSM fsm) {
			List<string> names = new List<string>();
			if (fsm == null) return names;
			try {
				FsmVariables v = fsm.FsmVariables;
				if (v == null) return names;

				FsmBool[] bools = v.BoolVariables;
				if (bools != null) {
					for (int i = 0; i < bools.Length; i++) Add(names, bools[i] == null ? null : bools[i].Name);
				}
				FsmInt[] ints = v.IntVariables;
				if (ints != null) {
					for (int i = 0; i < ints.Length; i++) Add(names, ints[i] == null ? null : ints[i].Name);
				}
				FsmFloat[] floats = v.FloatVariables;
				if (floats != null) {
					for (int i = 0; i < floats.Length; i++) Add(names, floats[i] == null ? null : floats[i].Name);
				}
				FsmString[] strings = v.StringVariables;
				if (strings != null) {
					for (int i = 0; i < strings.Length; i++) Add(names, strings[i] == null ? null : strings[i].Name);
				}
			}
			catch { }
			return names;
		}

		/// <summary>
		/// FSM'i adıyla bulur.
		///
		/// dnSpy: PlayMakerFSM.FindFsmOnGameObject(GameObject, string) token 0x060003D7,
		/// ayrıca PlayMakerFSM.FsmList (token 0x060003D6) etkin tüm FSM bileşenlerini
		/// tutuyor. İkisi de elle taramaktan ucuz ve doğru.
		/// </summary>
		public static PlayMakerFSM FindOn(GameObject go, string fsmName) {
			if (go == null || string.IsNullOrEmpty(fsmName)) return null;
			try { return PlayMakerFSM.FindFsmOnGameObject(go, fsmName); } catch { return null; }
		}

		/// <summary>
		/// FSM'in gerçekten dinlediği olay adları.
		///
		/// Neden gerekli (dnSpy kanıtı): Fsm.ProcessEvent (token 0x060001E8) olayı
		/// üç yerden kabul eder, sırayla:
		///
		///   1. ActiveState.OnEvent(event)
		///   2. GlobalTransitions içindeki eşleşmeler
		///   3. ActiveState.Transitions içindeki eşleşmeler
		///
		/// Yani bir olayın işe yaraması için ya etkin durumun ya da global geçişlerin
		/// adına sahip olması gerekir; aksi hâlde ProcessEvent sessizce çıkar. Bu
		/// metot o üç kaynağı da okuyup adları toplar; böylece "butona hangi olayı
		/// göndereyim" sorusu yanıtlanabilir hâle gelir. Elle ad tahmin etmek
		/// (CLICK, MOUSEDOWN, ...) oyun sürümünden bağımsız çalışmaz.
		/// </summary>
		public static List<string> DeclaredEvents(PlayMakerFSM fsm) {
			List<string> names = new List<string>();
			if (fsm == null) return names;
			try {
				Fsm f = fsm.Fsm;
				if (f == null) return names;

				// Bildirilen olay havuzu: FsmHasEvent'in baktığı liste de budur.
				FsmEvent[] declared = f.Events;
				if (declared != null) {
					for (int i = 0; i < declared.Length; i++) {
						if (declared[i] != null) Add(names, declared[i].Name);
					}
				}

				// Global geçişler: FSM'in hangi olaya neye gittiğini doğrudan söyler.
				FsmTransition[] globals = f.GlobalTransitions;
				if (globals != null) {
					for (int i = 0; i < globals.Length; i++) {
						if (globals[i] != null) Add(names, globals[i].EventName);
					}
				}

				// Tüm durumların geçişleri.
				FsmState[] states = f.States;
				if (states != null) {
					for (int i = 0; i < states.Length; i++) {
						FsmState st = states[i];
						if (st == null) continue;
						FsmTransition[] tr = st.Transitions;
						if (tr == null) continue;
						for (int j = 0; j < tr.Length; j++) {
							if (tr[j] != null) Add(names, tr[j].EventName);
						}
					}
				}
			}
			catch { }
			return names;
		}

		/// <summary>Etkin tüm FSM bileşenleri (PlayMakerFSM.FsmList).</summary>
		public static List<PlayMakerFSM> Active() {
			try { return PlayMakerFSM.FsmList; }
			catch { return new List<PlayMakerFSM>(); }
		}

		// ------------------------------------------------------------------
		// Nesne/FSM adıyla DEĞİŞKEN çözümleme
		// ------------------------------------------------------------------
		//
		// Kullanıcı talebi: "şarkıyı klasöre ekledim, otomatik import et".
		// Oyunun import zinciri `SongDatabaseCD/Playlist/Path` gibi bir
		// NESNE referansı kullanıyor, ama hiyerarşi dökümünde
		// `SongDatabase` diye bir nesne YOK (referans Loadself ile çözülüyor).
		// Bu yüzden nesne ADIYLA bulunup üzerindeki FSM'ler taranıyor.

		/// <summary>Nesne adı + FSM adı + değişken adı ile string okur. Yoksa null.</summary>
		public static string StrByName(string objectName, string fsmName, string varName) {
			PlayMakerFSM fsm = FindByNames(objectName, fsmName);
			if (fsm == null) return null;
			FsmString v = Str(fsm, varName);
			return v == null ? null : v.Value;
		}

		/// <summary>Nesne adı + FSM adı + değişken adı ile int okur. Yoksa -1.</summary>
		public static int IntByName(string objectName, string fsmName, string varName) {
			PlayMakerFSM fsm = FindByNames(objectName, fsmName);
			if (fsm == null) return -1;
			FsmInt v = Int(fsm, varName);
			return v == null ? -1 : v.Value;
		}

		/// <summary>
		/// Nesne adıyla FSM bulur. fsmName boşsa nesnedeki İLK FSM döner.
		/// Yanlış FSM dönse bile hedef değişkeni orada yoksa çağıran taraf
		/// "bulunamadı" olarak ele alır, yani güvenli.
		/// </summary>
		public static PlayMakerFSM FindByNames(string objectName, string fsmName) {
			if (string.IsNullOrEmpty(objectName)) return null;
			try {
				GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
				for (int i = 0; all != null && i < all.Length; i++) {
					GameObject go = all[i];
					if (go == null || go.hideFlags != HideFlags.None) continue;
					if (go.name != objectName) continue;

					PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
					if (fsms == null || fsms.Length == 0) continue;
					if (string.IsNullOrEmpty(fsmName)) return fsms[0];
					PlayMakerFSM named = FindOn(go, fsmName);
					if (named != null) return named;
				}
			}
			catch { }
			return null;
		}

		static void Add(List<string> names, string name) {
			if (string.IsNullOrEmpty(name)) return;
			if (!names.Contains(name)) names.Add(name);
		}
	}
}