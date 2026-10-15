using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Eşya alma / bırakma senkronizasyonu.
	///
	/// Neden C# yaması değil
	/// --------------------
	/// MSC'de eşya sistemi bir C# sınıfı değil: Assembly-CSharp içinde Item, Pickup veya
	/// Inventory tipi yok. Her eşya, üzerindeki PlayMaker FSM'lerle yönetiliyor (alma,
	/// bırakma, yok etme, sahip değiştirme olayları). Bu yüzden "Item.Pickup() metodunu
	/// yamalayalım" gibi bir yaklaşım derleme zamanında patlar.
	///
	/// Yaklaşım
	/// --------
	/// 1. Keşif: dünya yüklendiğinde eşya gibi görünen FSM'ler taranır (isim kalıbı + FSM
	///    üzerinde hareket/animasyon olması). Bulunanlar tabloya alınır ve konsola yazılır.
	/// 2. Yakalama: zaten global olan <c>PlayMakerFSM.SendEvent</c> hook'u, izlenen bir
	///    eşya FSM'i olay gönderdiğinde buraya düşer.
	/// 3. Gönderim: (yol, olay, konum) üçlüsü gönderilir. Aynı olayın kısa aralıkla
	///    tekrarlanması bastırılır, çünkü FSM'ler "UPDATE" gibi iç olayları saniyede onlarca
	///    kez tetikler ve bunlar oyunun kendi işidir, senkronize edilecek bir değişim değildir.
	/// 4. Uygulama: karşı tarafta aynı yoldaki FSM bulunur ve olay kendi FSM'inde oynatılır.
	///    Yankı koruması (EchoGuard) açık olduğu için karşı tarafın kendi olayı geri
	///    gönderilmez.
	///
	/// Tablo uyuşmazlığı
	/// ----------------
	/// İki taraf aynı kaydı ve aynı haritayı yüklemiş olmalıdır. Aksi hâlde yol eşleşmez;
	/// bunun yerine tahmin etmektense paket düşürülür ve sayac artar, 'smp status' bunu
	/// gösterir. Satsuma tablosuyla aynı kural.
	/// </summary>
	public static class ItemSync {

		/// <summary>Tablodaki azami eşya sayısı. Karşı tarafta olmayan bir eşyayı izlemek oyunu yavaşlatır.</summary>
		public const int MAX_TRACKED = 64;
		/// <summary>Aynı (eşya, olay) çifti bu süreden kısa aralıkla tekrarlanırsa bastırılır.</summary>
		public const float DEDUPE_WINDOW = 0.5f;
		/// <summary>Bir pakette gönderilebilecek olay sayısı üst sınırı.</summary>
		public const int MAX_EVENTS_PER_PACKET = 8;

		/// <summary>Keşifte aranan isim parçaları. Oyun sürümüne göre değişebilir.</summary>
		public static readonly string[] NAME_HINTS = {
			"ITEM", "PICKUP", "DESTROY", "HELD", "PICKABLE", "LOOT"
		};

		/// <summary>Senkronize edilmeyen oyun içi olaylar (gürültü).</summary>
		static readonly string[] IGNORED_EVENTS = {
			"FINISHED", "WAIT", "UPDATE", "IGNORE", "DONE", "NONE", "START", "INIT"
		};

		/// <summary>İzlenen tek bir eşya FSM'i.</summary>
		public class Entry {
			public PlayMakerFSM Fsm;
			public string Path;          // sahne kökünden göreli yol: kimlik bu
			public float LastSentAt;     // bastırma penceresi
			public string LastEvent;     // bastırma için son olay adı
			public GameObject Object;    // hızlı null kontrolü için
		}

		static readonly Dictionary<string, Entry> byPath = new Dictionary<string, Entry>(StringComparer.Ordinal);
		static readonly List<Entry> ordered = new List<Entry>(MAX_TRACKED);
		static readonly List<Entry> pending = new List<Entry>(MAX_EVENTS_PER_PACKET);
		static readonly Dictionary<string, float> recentEvents = new Dictionary<string, float>(StringComparer.Ordinal);

		/// <summary>Kaç eşya izleniyor.</summary>
		public static int TrackedCount { get { return ordered.Count; } }
		/// <summary>Gönderilen olay sayısı.</summary>
		public static int SentEvents;
		/// <summary>Uygulanan olay sayısı.</summary>
		public static int AppliedEvents;
		/// <summary>Yol bulunamadığı için düşürülen olay sayısı.</summary>
		public static int UnresolvedEvents;
		/// <summary>
		/// Uygulama sırasında atılan (hatalı kayıt) olay sayısı.
		///
		/// Kayıt başına koruma eklendiğinde (bkz. OnMessage) tek bir bozuk
		/// kayıt paketin geri kalanını düşürmez; bu sayaç o bozuk kayıtları
		/// görünür kılar, yoksa sessizce kaybolurlardı.
		/// </summary>
		public static int DroppedEvents;

		// ------------------------------------------------------------------
		// Keşif
		// ------------------------------------------------------------------

		/// <summary>
		/// Dünya yüklendiğinde çağrılır. Eşya gibi görünen FSM'leri bulur ve tabloya alır.
		/// Hiçbir şey bulunamazsa mod sessizce devre dışı kalır: oyun sürümünde isimler
		/// farklıysa kullanıcı 'smp item &lt;ad&gt;' ile elle ekleyebilir.
		/// </summary>
		public static int Discover() {
			Clear();
			GameObject[] roots = SceneDump.AllSceneRoots();
			int scanned = 0;
			for (int r = 0; r < roots.Length && ordered.Count < MAX_TRACKED; r++) {
				PlayMakerFSM[] fsms = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
				scanned += fsms.Length;
				for (int i = 0; i < fsms.Length && ordered.Count < MAX_TRACKED; i++) {
					PlayMakerFSM fsm = fsms[i];
					if (fsm == null || fsm.gameObject == null) continue;
					bool looksLikeItem = LooksLikeItem(fsm);
				string nameLower = fsm.gameObject.name;
				bool spannerSet = nameLower != null
					&& nameLower.IndexOf("spanner set", StringComparison.OrdinalIgnoreCase) >= 0;
				if (looksLikeItem || spannerSet) {
					Track(fsm);
					if (spannerSet) {
						try {
							SmpLog.Log("items: TRACK spanner set at '" + Wire.PathOf(fsm.transform) + "'.");
						} catch { }
					}
				}
				}
			}
			SmpLog.Log("items: scanned " + scanned + " FSM(s), tracked " + ordered.Count + ".");
			if (ordered.Count == 0) {
				SmpLog.Warning("no item FSMs found by name. Use 'smp item <objectName>' to track one manually.");
			}
			return ordered.Count;
		}

		/// <summary>
		/// "Eşya gibi görünüyor" testi: isim ipucu + oyuncuya bakan bir trigger ya da
		/// mesh/animasyon. Yalnızca isme bakmak yanlış pozitif üretir (her "trigger_"
		/// nesnesi isim ipucuyla eşleşmez ama yine de FSM'siz olanları eleriz).
		/// </summary>
		static bool LooksLikeItem(PlayMakerFSM fsm) {
			string objectName = fsm.gameObject.name;
			string fsmName;
			try { fsmName = fsm.FsmName; } catch { return false; }

			// v31: once kendi adi + FSM adi, sonra EBEVEYN zinciri
			// (8 cocuk-only `(itemx)` nesnesi icin -- asil ad ustte).
			bool hint = ContainsHint(objectName) || ContainsHint(fsmName)
				|| ObjectOrParentLooksLikeItem(fsm);
			if (!hint) return false;

			// Bir FSM'in kendi başına yeterli kanıtı: hareket/animasyon ya da bir "holder" nesnesi.
			bool hasMotion = fsm.gameObject.GetComponentInChildren<Animation>() != null
				|| fsm.gameObject.GetComponentInChildren<Animator>() != null
				|| fsm.gameObject.GetComponent<Collider>() != null;
			return hasMotion;
		}

		static bool ContainsHint(string value) {
			if (string.IsNullOrEmpty(value)) return false;
			for (int i = 0; i < NAME_HINTS.Length; i++) {
				if (value.IndexOf(NAME_HINTS[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			return false;
		}

		/// <summary>
		/// SMP_ITEM_PARENT_HINT_V31
		/// Asıl eşya adını nesnenin ve EBEVEYNlerinin adında ara.
		///
		/// DOKUM KANITI: `(itemx)` nesnelerinden 8 tanesinin FSM'i
		/// KENDİ nesnesinde değil, ÇOCUĞUNDA:
		///   ITEMS/floor jack(itemx)      -> cocuk `Trigger|Use`
		///   ITEMS/fish trap(itemx)       -> cocuk
		///   ITEMS/garbage barrel(itemx)  -> cocuk
		///   ITEMS/motor hoist(itemx)     -> cocuk `Hoist|Hook`
		///   ITEMS/sofa(itemx)            -> cocuk
		///   ITEMS/spanner set(itemx)     -> cocuk
		///   ITEMS/water bucket(itemx)    -> cocuk
		///   ITEMS/wood carrier(itemx)    -> cocuk
		///
		/// `LooksLikeItem` yalnız `fsm.gameObject.name` ve `fsm.FsmName`
		/// üzerinden `ContainsHint` çağırır. `Trigger` / `Use` / `Hoist`
		/// `NAME_HINTS` içindeki
		///   ITEM, PICKUP, DESTROY, HELD, PICKABLE, LOOT
		/// hiçbiriyle eşleşmez -> bu 8 eşya %0 kapsamda.
		///
		/// Bu tahmin DEĞİL: oyun eşyanın adını ÜST nesneye koyup FSM'i
		/// ALTINA yerleştiriyor. Kok adı aramak oyunun kendi düzenini
		/// okumaktır.
		///
		/// Unity 5 notu: `Transform.parent` vardır; `firstChild` /
		/// `nextSibling` YOK (v21/v24'te aynı şekilde düzeltildi).
		/// Zincir 8 seviye ile sınırlı: `motor hoist(itemx)` kökte,
		/// `Hoist` bir altta.
		/// </summary>
		static bool ObjectOrParentLooksLikeItem(PlayMakerFSM fsm) {
			if (fsm == null || fsm.gameObject == null) return false;
			Transform t = null;
			try { t = fsm.gameObject.transform; } catch { return false; }
			int guard = 0;
			while (t != null && guard++ < 8) {
				string n = null;
				try { n = t.name; } catch { }
				if (ContainsHint(n)) return true;
				try { t = t.parent; } catch { break; }
			}
			return false;
		}

		/// <summary>Tek bir FSM'i tabloya ekler (keşif ya da 'smp item' komutu).</summary>
		public static bool Track(PlayMakerFSM fsm) {
			if (fsm == null || fsm.gameObject == null) return false;
			string path = SceneDump.PathOf(fsm.gameObject.transform);
			if (string.IsNullOrEmpty(path) || byPath.ContainsKey(path)) return false;
			if (ordered.Count >= MAX_TRACKED) return false;

			Entry e = new Entry();
			e.Fsm = fsm;
			e.Object = fsm.gameObject;
			e.Path = path;
			byPath[path] = e;
			ordered.Add(e);
			return true;
		}

		/// <summary>Ada göre FSM bulup ekler: 'smp item beer_can' gibi.</summary>
		public static bool TrackByName(string name) {
			if (string.IsNullOrEmpty(name)) return false;
			GameObject[] roots = SceneDump.AllSceneRoots();
			for (int r = 0; r < roots.Length; r++) {
				PlayMakerFSM[] fsms = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
				for (int i = 0; i < fsms.Length; i++) {
					PlayMakerFSM fsm = fsms[i];
					if (fsm == null || fsm.gameObject == null) continue;
					if (fsm.gameObject.name != name && SafeFsmName(fsm) != name) continue;
					if (Track(fsm)) {
						SmpLog.Log("item tracked: '" + SceneDump.PathOf(fsm.gameObject.transform) + "'");
						return true;
					}
				}
			}
			return false;
		}

		/// <summary>Dünya değişiminde tabloyu bırakır.</summary>
		public static void Clear() {
			byPath.Clear();
			ordered.Clear();
			pending.Clear();
			recentEvents.Clear();
		}

		static string SafeFsmName(PlayMakerFSM fsm) {
			try { return fsm.FsmName; } catch { return null; }
		}

		// ------------------------------------------------------------------
		// Yakalama (SendEvent hook'undan)
		// ------------------------------------------------------------------

		/// <summary>
		/// Global SendEvent hook'undan gelir. Satsuma filtresinden *önce* çağrılır, çünkü
		/// eşya olayları "ASSEMBLE" değildir ve Satsuma filtresi onları elerdi.
		/// </summary>
		public static void OnFsmEvent(PlayMakerFSM fsm, string eventName) {
			if (fsm == null || fsm.gameObject == null || string.IsNullOrEmpty(eventName)) return;

			// TEŞHİS: eşya alma/bırakma olayının adını bulmak için.
			//
			// Canlı kanıt: eşya kameranın altındaki "Drink/Hand/HandBottles"
			// kapsamına parent oluyor, tespit artık çalışıyor — ama olayın
			// adı bilinmiyor ("PICKUP"? "TAKE"?). EchoGuard/HasRemote
			// kontrolünden ÖNCE yazılır ki tek başına testte de görünsün.
			// Sessiz tutan tek şey kelime filtresidir.
			if (LooksLikeItemEvent(eventName) && Telemetry.Verbose) {
				SmpLog.Log("event probe: '" + fsm.gameObject.name + "'  fsm='"
					+ SafeFsmName(fsm) + "'  event='" + eventName + "'");
			}

			// Yankı koruması: karşı tarafta biz uyguladığımız olayı yeniden göndermeyelim.
			if (EchoGuard.Suppressed || !SteamTransport.HasRemote) return;

			// Alma/bırakma olayı: eşya keşifte yoksa (uzak/ıskarta) konum
			// akışı için tabloya al. Sıralama ÖNEMLİ: IsNoise bunu eler,
			// yani geç bağlama gürültü süzgecinden ÖNCE yapılmalı.
			if (IsGrabEvent(eventName)) {
				try { GarageEntitySync.EnsureTracked(fsm.gameObject); } catch { }
			}
			else if (string.Equals(eventName, "USE", StringComparison.OrdinalIgnoreCase)) {
				try {
					if (CarrySync.IsCarryableUse(fsm, fsm.gameObject))
						GarageEntitySync.EnsureTracked(fsm.gameObject);
				} catch { }
			}

			if (IsNoise(eventName)) return;

			Entry entry = Find(fsm);
			if (entry == null) return; // izlenmiyor

			// Aynı olayın tekrarı: FSM'ler "UPDATE"/"DONE" dışında bazı olayları da her
			// karede tekrarlar. Pencere içinde aynı (yol, olay) ikinci kez gelirse bastır.
			string key = entry.Path + "/" + eventName;
			float now = Time.realtimeSinceStartup;
			float last;
			if (recentEvents.TryGetValue(key, out last) && now - last < DEDUPE_WINDOW) return;
			recentEvents[key] = now;

			// Paket başına sınır: patlamayı engeller, kalanı sonraki kareye bırakılır.
			if (pending.Count >= MAX_EVENTS_PER_PACKET) {
				Flush();
				if (pending.Count >= MAX_EVENTS_PER_PACKET) return;
			}
			pending.Add(entry);
			entry.LastEvent = eventName;
		}

		static readonly string[] GRAB_EVENTS = {
			"PICKUP", "PICK_UP", "TAKE", "TOOK", "GRAB", "HOLD", "HELD",
			"CARRY", "DROP", "THROW", "RELEASE",
		};

		/// <summary>
		/// Alma/bırakma olayı mı? Geç bağlama tetikleyicisi.
		///
		/// DİKKAT: bu olaylar karşı tarafta OYNATILMAZ. MSC'de alma işi
		/// eşyayı oyuncunun elindeki bone parent etmektir; hedef oyuncunun
		/// KENDİ nesnesi olduğu için replay, eşyayı yerel oyuncunun eline
		/// takar (kuklada hiç görünmez). HeldItemSync sonucu doğrudan
		/// kopyalıyor; buradaki rolü sadece eşyayı konum tablosuna almak.
		/// </summary>
		static bool IsGrabEvent(string eventName) {
			if (string.IsNullOrEmpty(eventName)) return false;
			for (int i = 0; i < GRAB_EVENTS.Length; i++) {
				if (eventName.IndexOf(GRAB_EVENTS[i], StringComparison.OrdinalIgnoreCase) >= 0) {
					return true;
				}
			}
			return false;
		}

		/// <summary>Alma/bırakma olayları karşı tarafta yeniden oynatılmaz.</summary>
		static bool IsHoldEvent(string eventName) { return IsGrabEvent(eventName); }

		/// <summary>
		/// Olay adı eşya eylemini çağıştırıyor mu? (probe filtresi)
		/// Geniş tutuldu: kesin ad bilinmiyor, tüm varyantları yakala.
		/// </summary>
		static bool LooksLikeItemEvent(string eventName) {
			if (string.IsNullOrEmpty(eventName)) return false;
			string l = eventName.ToLowerInvariant();
			return l.IndexOf("pick", StringComparison.Ordinal) >= 0
				|| l.IndexOf("take", StringComparison.Ordinal) >= 0
				|| l.IndexOf("took", StringComparison.Ordinal) >= 0
				|| l.IndexOf("grab", StringComparison.Ordinal) >= 0
				|| l.IndexOf("hold", StringComparison.Ordinal) >= 0
				|| l.IndexOf("drop", StringComparison.Ordinal) >= 0
				|| l.IndexOf("carry", StringComparison.Ordinal) >= 0
				|| l.IndexOf("equip", StringComparison.Ordinal) >= 0
				|| l.IndexOf("invent", StringComparison.Ordinal) >= 0
				|| l.IndexOf("loot", StringComparison.Ordinal) >= 0;
		}

		static bool IsNoise(string eventName) {
			// Alma/bırakma: konum akışına giriyor ama OLAY olarak gitmiyor.
			if (IsHoldEvent(eventName)) return true;
			for (int i = 0; i < IGNORED_EVENTS.Length; i++) {
				if (string.Equals(IGNORED_EVENTS[i], eventName, StringComparison.OrdinalIgnoreCase)) return true;
			}
			// PlayMaker'ın iç olayları: "FSM_INIT", "FSM_EVENT" gibi.
			return eventName.StartsWith("FSM", StringComparison.OrdinalIgnoreCase);
		}

		static Entry Find(PlayMakerFSM fsm) {
			if (fsm == null || fsm.gameObject == null) return null;
			// Sıralı liste küçük (en fazla 64); referans eşitliği yeterli ve tahsissiz.
			for (int i = 0; i < ordered.Count; i++) {
				if (ReferenceEquals(ordered[i].Fsm, fsm)) return ordered[i];
			}
			return null;
		}

		// ------------------------------------------------------------------
		// Gönderim / uygulama
		// ------------------------------------------------------------------

		/// <summary>Kare başına bir kez çağrılır.</summary>
		public static void Update() {
			if (pending.Count > 0) Flush();
		}

		static void Flush() {
			if (!SteamTransport.HasRemote || pending.Count == 0) return;
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.ItemEvent);
				BinaryWriter w = new BinaryWriter(s);
				w.Write((byte)pending.Count);
				for (int i = 0; i < pending.Count; i++) {
					Entry e = pending[i];
					string eventName = e.LastEvent;
					Vector3 pos = e.Object != null ? e.Object.transform.position : Vector3.zero;
					w.Write(e.Path);
					w.Write(eventName);
					w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
				}
				if (SteamTransport.SendReliable(Protocol.Finish(s))) SentEvents += pending.Count;
			}
			catch (Exception ex) {
				SmpLog.Warning("item send failed: " + ex.Message);
			}
			pending.Clear();
		}

		/// <summary>Karşı taraftan gelen olay paketini uygular.</summary>
		public static void OnMessage(BinaryReader r) {
			try {
				byte count = r.ReadByte();
				for (int i = 0; i < count; i++) {
					string path = r.ReadString();
					string eventName = r.ReadString();
					r.ReadSingle(); // konum: ileride reddedilmiş eşya kullanımı için saklanır
					r.ReadSingle();
					r.ReadSingle();

					Entry entry;
					if (!byPath.TryGetValue(path, out entry) || entry == null) {
						UnresolvedEvents++;
						continue;
					}
					// KAYIT BAŞINA KENDİ KORUMASI.
					//
					// CANLI HATA (düzeltildi): `SendEvent` eskiden tek bir
					// try/finally ile sarılıydı ve istisna dıştaki yakalayıcıya
					// kaçıyordu. O da paketin GERİ KALANINI tamamen bırakıyordu:
					// bir kapıyı + aynı karede 3 eşya olayını taşıyan pakette
					// ilk kayıttaki nesne yok olmuşsa (MissingReferenceException)
					// kapı karşı tarafta hiç açılmıyordu ve konsolda yalnızca
					// "item packet rejected" görünüyordu. `finally` yalnızca
					// kapsamı serbest bırakır, istisnayı tutmaz.
					//
					// Şimdi her kayıt kendi try/catch'inde: bozuk bir kayıt
					// sayılır ve döngü sorunsuz devam eder.
					try {
						EchoGuard.Scope scope = EchoGuard.Enter();
						try { entry.Fsm.SendEvent(eventName); }
						finally { scope.Dispose(); }
						AppliedEvents++;
					}
					catch (Exception ex) {
						// Fizik olayı kaybolur; sessiz değil, ama paketin
						// geri kalanı gider.
						Telemetry.BadPackets++;
						if (DroppedEvents < 256) DroppedEvents++;
						SmpLog.Warning("item event dropped (" + path + " -> '"
							+ eventName + "'): " + ex.GetType().Name);
					}
				}
			}
			catch (Exception ex) {
				Telemetry.BadPackets++;
				SmpLog.Warning("item packet rejected: " + ex.Message);
			}
		}

		/// <summary>
		/// Bağlantı kurulduğunda tüm eşyaların mevcut durumunu isteyen çağrı için
		/// isim listesi. Alınan/bırakılan eşya "durum" FSM üzerinden tutulduğu için,
		/// karşı tarafın tabloyu kendi kaydından kurması yeterlidir.
		/// </summary>
		public static string Describe() {
			if (ordered.Count == 0) return "items: none tracked ('smp item <name>' to add one)";
			return "items: " + ordered.Count + " tracked, sent " + SentEvents
				+ ", applied " + AppliedEvents
				+ ", unresolved " + UnresolvedEvents;
		}

		/// <summary>'smp items' komutu için satırlar.</summary>
		public static List<string> Dump() {
			List<string> lines = new List<string>(ordered.Count);
			for (int i = 0; i < ordered.Count; i++) {
				Entry e = ordered[i];
				lines.Add(e.Path + "   fsm: " + SafeFsmName(e.Fsm) + "   last: " + (e.LastEvent ?? "-"));
			}
			return lines;
		}
	}
}