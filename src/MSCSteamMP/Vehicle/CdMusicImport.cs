using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Otomatik müzik importu — CD1/CD2/CD3 klasörlerindeki .ogg dosyaları.
	///
	/// OYUNUN IMPORT SİSTEMİ (level0 asset'inden okundu, tahmin değil)
	/// ------------------------------------------------------------------
	/// Ana menüde `Radio/CD` diye bir nesne var. Üzerindeki FSM'in olay
	/// listesinde `IMPORT` bulunuyor ve durumları şöyle:
	///
	///   Radio/CD (FSM)
	///     Import → CD1/CD2/CD3 nesnelerine `LOAD` gönderir
	///     olaylar: IMPORT, LOAD, PLAY, JINGLE, NEXT SONG, ...
	///
	///   Radio/CD/CD1 (FSM adı "LoadSongs")
	///     Load durumu yolu KURAR:
	///       GetAppPath()                       -> ".../My Summer Car/"
	///       BuildString: "file:///" + FolderName + "CD1/" + "track"
	///                     + Name + ".ogg"
	///     sonra WWWPOST(storeAudio: Song) ile indirir ve `Playlist`
	///     nesnesindeki `Ref` ArrayList'ine ekler.
	///     MaxTracks / Track sayacı dosya sayısını belirler.
	///
	/// Yani oyun `D:\Steam\...\My Summer Car\CD1\track1.ogg` yolunu KENDİ
	/// kuruyor — senin eklediğin klasör yapısı (track1.ogg + coverart.png)
	/// doğru. Tek eksik olan şey `IMPORT` olayının tetiklenmesi.
	///
	/// NEDEN "import music" BUTONU GEREKMİYOR
	/// -------------------------------------
	/// Ana menüdeki buton sadece `IMPORT` olayını gönderiyor. Bu mod o
	/// butona dokunmadan olayı doğrudan FSM'e yolluyor, yani oyun her
	/// başladığında tüm CD'ler otomatik yükleniyor.
	///
	/// ZAMANLAMA
	/// ---------
	/// `IMPORT` bir kez gönderilir; WWWPOST asenkron olduğu için yükleme
	/// saniyeler sürebilir. Durum `SongDatabaseCD/Playlist/Path` okunarak
	/// izlenir. 20 saniye içinde "CD'S IMPORTED" olmazsa 3 kez yeniden
	/// denenir (bazen menü sahnesi hazır olmadan gönderiliyor).
	///
	/// AĞ YOK
	/// ------
	/// Müzik dosyaları KENDİ makinende okunur, karşı tarafa paket
	/// gönderilmez. Senkronlanan şey zaten parça numarası
	/// (`CDPlayer/Update: Index, CurrentTrack`) — RadioSync'te mevcut.
	///
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class CdMusicImport {

		/// <summary>
		/// Import FSM'inin olayı.
		///
		/// DÜZELTME — asset'i yeniden okudum ve İLK HALİM YANLIŞTI:
		///   `Radio/CD` → GO "CD", FSM adı "Playlist"
		///   bildirilen olaylar: ADVERT1, DESTROY, FAIL, JINGLE, LOAD, LOOP,
		///                      NEXT SONG, PLAY, PREVIOUS SONG, RADIO_FOLK,
		///                      WAKEUP, CD1, CD2, CD3, **Import**, Path,
		///                      NOT IMPORTED
		///
		/// Yani olay "IMPORT" değil, **"Import"** (küçük i). PlayMaker olay
		/// adları büyük/küçük harf DUYARLIDIR; "IMPORT" göndermek sessizce
		/// düşüyordu. Canlı log kanıtı:
		///   cd import: no FSM with an IMPORT event found   (her saniye)
		/// </summary>
		const string IMPORT_EVENT = "Import";

		/// <summary>Eski (yanlış) yazım; FSM buna yanıt vermiyor ama log
		/// karışıklığını önlemek için ayrıca denemiyoruz.</summary>
		const string IMPORT_EVENT_ALT = "IMPORT";

		/// <summary>Yüklemenin bittiğini bildiren metin.</summary>
		const string DONE_TEXT = "CD'S IMPORTED";

		/// <summary>
		/// Yeniden deneme arası bekleme.
		///
		/// Ölçüm: `Import` gönderimi ile `Path = "CD'S IMPORTED"` arası
		/// **79 ms** (log 11:28:41.973 -> 11:28:41.695). Yani import
		/// anlıktır; 20 sn beklemenin tek işlevi ilk denemeyi geciktirmekti.
		/// </summary>
		const float RETRY_SECONDS = 3f;

		/// <summary>
		/// İlk deneme sahne hazır olmadan gidiyordu, sonrakiler 3 sn'de bir
		/// tekrarlanıyor. 6 deneme = ~15 saniyelik toplam pencere, sonra
		/// oyunun kendi "import music" butonu devreye giriyor.
		/// </summary>
		const int MAX_ATTEMPTS = 6;

		static PlayMakerFSM importFsm;
		static bool discovered;
		static int attempts;
		static float nextAttempt;
		static float nextProbe;
		static string lastPath = "";
		static string lastError = "";
		/// <summary>Yükleyici (CD1/CD2/CD3) son görülen FSM state'i.</summary>
		static readonly string[] cdLastState = new string[] { "", "", "" };

		/// <summary>Yükleyicinin son gördüğü URL ve `Name` sayacı.</summary>
		static readonly string[] cdLastUrl = new string[] { "", "", "" };
		static readonly string[] cdLastName = new string[] { "", "", "" };

		/// <summary>Yükleyicinin son gördüğü `Error` metni.</summary>
		static readonly string[] cdErr = new string[] { "", "", "" };

		/// <summary>Sahnedeki ArrayList proxy'leri bir kez listelendi mi?</summary>
		static bool proxyLogged;

		/// <summary>Playlist `Ref` ArrayList'inde bulunan clip sayısı.</summary>
		static int refCount = -1;

		/// <summary>CD1/CD2/CD3 `Ref` listelerindeki clip sayıları.</summary>
		static readonly int[] cdClips = new int[] { -1, -1, -1 };
		static readonly bool[] cdClipSeen = new bool[] { false, false, false };
		static string lastSong = "";
		static string lastUrl = "";

		/// <summary>Ebeveyn zinciri teşhisi bir kez yazılsın.</summary>
		static bool chainLogged;

		static bool loggedMissing;
		static int countReached = -1;

		/// <summary>Her şeyi sıfırlar ve yeniden dener ('smp songs reset').</summary>
		public static void ResetAndRetry() {
			lastPath = "";
			lastUrl = "";
			lastSong = "";
			chainLogged = false;
			for (int i = 0; i < cdLastState.Length; i++) cdLastState[i] = "";
			countReached = -1;
			Imported = false;
			Discover();
			Send();
		}

		/// <summary>Import tamamlandı mı?</summary>
		public static bool Imported { get; private set; }

		/// <summary>Import FSM'i bulundu mu? (Menüye dönüldüğünde yeniden aranır.)</summary>
		public static bool Found { get { return importFsm != null; } }

		/// <summary>Kaç deneme yapıldı (teşhis).</summary>
		public static int Attempts { get { return attempts; } }

		/// <summary>Import edilen şarkı sayısı (teşhis; -1 = bilinmiyor).</summary>
		public static int TrackCount { get { return countReached; } }

		/// <summary>Son görülen durum metni.</summary>
		public static string Status { get { return lastPath; } }

		// ------------------------------------------------------------------
		// Keşif
		// ------------------------------------------------------------------

		/// <summary>
		/// `IMPORT` olayını tanımlayan FSM'i bulur. İsme göre arama yapmıyor:
		/// oyun "Radio/CD" yolunu kullanıyor ama nesne adı ileride değişebilir;
		/// olayı tanımlayan FSM'i aramak daha dayanıklı.
		/// </summary>
		public static void Discover() {
			importFsm = null;
			discovered = false;
			// DÜZELTME: sayaçlar BURADA sıfırlanmıyordu. CheckWorld dünyadan
			// çıkışta keşfi tazelediği için "attempt 1" tekrar başlıyordu ve
			// deneme bütçesi (3) hiç dolmuyordu. Canlı log:
			//   sent IMPORT (attempt 1) / (attempt 2) / (attempt 1)
			// Sayaçlar yalnızca 'smp songs reset' ile sıfırlanmalı.
			// DÜZELTME — bu blok `lastPath.Length == 0` koşuluna bağlıydı ve
			// oturumlar arasında sızmaya yol açıyordu. Canlı log (11:35):
			//
			//   11:33:01.973 sent 'Import' (attempt 1) and LOAD to 3 CD loader(s)
			//   11:35:04.615 found FSM 'Playlist' on 'CD'   ← yeni oturum
			//   (… "sent 'Import'" satırı YOK)             ← Send() hiç çağrılmadı
			//
			// Sebep: `Path` = "NOT IMPORTED" olduğu için `lastPath.Length != 0`
			// ve `attempts` sıfırlanmıyordu; önceki oturumun `nextAttempt`
			// değeri de `Time.time` ile karşılaştırılınca `Send()` eşiğe
			// takılıp kalıyordu. Sayaçlar oturum başına olduğu için
			// KOŞULSUZ sıfırlanmalı.
			attempts = 0;
			nextAttempt = 0f;
			loadersBusy = false;
			lastLoaderActivity = 0f;
			nextProbe = 0f;
			lastPath = "";
			lastUrl = "";
			lastSong = "";
			chainLogged = false;
			proxyLogged = false;
			refCount = -1;
			countReached = -1;
			Imported = false;

			try {
				// 1) YOL İLE: asset'te `Radio/CD` -> GO "CD", FSM "Playlist".
				//    Bu en kesin yol; olay adına bakmadan FSM'i alıyoruz.
				GameObject radio = FindPath();
				if (radio != null) {
					PlayMakerFSM named = FsmNames.FindOn(radio, "Playlist");
					if (named != null && HasImportEvent(named)) importFsm = named;
					if (importFsm == null) {
						PlayMakerFSM[] fsms = radio.GetComponents<PlayMakerFSM>();
						for (int i = 0; i < fsms.Length && importFsm == null; i++) {
							if (fsms[i] != null && HasImportEvent(fsms[i])) importFsm = fsms[i];
						}
					}
					// Olay tanınmadıysa nesnedeki ilk FSM'i yine de dene:
					// build farkında olay listesi değişmiş olabilir.
					if (importFsm == null) {
						PlayMakerFSM[] fsms = radio.GetComponents<PlayMakerFSM>();
						if (fsms != null && fsms.Length > 0) importFsm = fsms[0];
					}
				}

				// 2) YEDEK: sahnede "Import" olayı tanımlı FSM'i ara.
				if (importFsm == null) {
					PlayMakerFSM[] all = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
					for (int i = 0; all != null && i < all.Length; i++) {
						if (all[i] == null) continue;
						if (!HasImportEvent(all[i])) continue;
						importFsm = all[i];
						break;
					}
				}
			}
			catch (Exception e) {
				SmpLog.Warning("cd import discovery failed: " + e.GetType().Name);
			}

			discovered = importFsm != null;
			if (discovered) {
				// Uyarı BURAYA taşındı: bulunduğunda bir kez yaz, sonrasında
				// sus. Önceden başarısızlık her Discover'da yazılıyordu ve
				// CheckWorld saniyede bir keşif yaptığı için log SANIYEDE BİR
				// "not found" ile doluyordu (log kanıtı).
				if (!loggedMissing) {
					loggedMissing = true;
					if (Telemetry.Verbose) SmpLog.Print("cd import: found FSM '" + (importFsm.Fsm != null ? importFsm.Fsm.Name : "?")
						+ "' on '" + (importFsm.gameObject != null ? importFsm.gameObject.name : "?")
						+ "', event '" + IMPORT_EVENT + "' (auto-importing CD1/CD2/CD3).");
				}
			}
			else {
				// Yalnızca seyrek uyar: kullanıcı menüde değilken bulunamaz.
				if (Time.time - lastMissingLog > 60f) {
					lastMissingLog = Time.time;
					if (Telemetry.Verbose) SmpLog.Log("cd import: no import FSM found (this is normal while the "
						+ "world is loaded - the importer lives in the main menu).");
				}
			}
		}

		static float lastMissingLog = -999f;
		static bool cdLoggedOnce;

		/// <summary>FSM "Import" (ya da "IMPORT") olayını tanımlıyor mu?</summary>
		static bool HasImportEvent(PlayMakerFSM fsm) {
			if (fsm == null) return false;
			if (FsmNames.HasEvent(fsm, IMPORT_EVENT)) return true;
			// Buyuk/kucuk harf duyarsiz: gecerli listeyi okuyup kiyasla.
			try {
				List<string> ev = FsmNames.DeclaredEvents(fsm);
				for (int i = 0; i < ev.Count; i++) {
					if (string.Equals(ev[i], IMPORT_EVENT, StringComparison.OrdinalIgnoreCase)) return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>
		/// `Radio/CD` nesnesini bulur: adı "CD" olan ve ebeveyni "Radio" olan
		/// nesne (asset: Radio → CD → CD1/CD2/CD3).
		/// </summary>
		static GameObject FindPath() {
			try {
				GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
				if (all == null) return null;
				for (int i = 0; i < all.Length; i++) {
					GameObject go = all[i];
					if (go == null || go.hideFlags != HideFlags.None) continue;
					if (go.name != "CD") continue;
					Transform t = go.transform.parent;
					if (t != null && t.name == "Radio") return go;
				}
			}
			catch { }
			return null;
		}

		/// <summary>
		/// Ana menüdeki `Radio/CD` nesnesi (= Playlist FSM'inin
		/// `SongDatabaseCD` değişkeni). CD1/CD2/CD3 playlist listeleri
		/// (`PlayMakerArrayListProxy`, referenceName CD1/CD2/CD3) bunun
		/// ÜZERİNDE yaşar.
		///
		/// RadioSync bunu kullanıyor: gelen "şu parça çalıyor" paketinde
		/// listeden AudioClip'i bulup yerel AudioSource'a koymak için.
		/// </summary>
		public static GameObject CdRoot() { return FindPath(); }

		/// <summary>Bu CD adinda kac sarki yuklu? (yerel cozunurluk testi).
		/// dnSpy: listeler CdRoot uzerindeki PlayMakerArrayListProxy
		/// bilesenlerinde durur; eslesme base PlayMakerCollectionProxy.
		/// referenceName iledir (CollectionsActions.GetArrayListProxyPointer).
		/// PCM yedegi bunu sorar: liste doluysa karsi taraf da kendi
		/// dosyasiyla calar, akis cift ses yapardi.</summary>
		public static int SongCount(string cdName) {
			try {
				if (string.IsNullOrEmpty(cdName)) return 0;
				GameObject root = CdRoot();
				if (root == null) return 0;
				PlayMakerArrayListProxy[] all = root.GetComponents<PlayMakerArrayListProxy>();
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] == null) continue;
					string rn = null;
					try { rn = all[i].referenceName; } catch { }
					if (!string.Equals(rn, cdName, StringComparison.OrdinalIgnoreCase)) continue;
					System.Collections.ArrayList list = null;
					try { list = all[i].arrayList; } catch { }
					if (list == null) return 0;
					return list.Count;
				}
			}
			catch { }
			return 0;
		}



		// ------------------------------------------------------------------
		// Otomatik tetikleme
		// ------------------------------------------------------------------

		/// <summary>
		/// Ana iş parçığından her kare çağrılır. Kendi içinde seyrek çalışır:
		/// `IMPORT` yalnızca gerekirse gönderilir.
		/// </summary>
		public static void Tick() {
			float now = Time.time;

			// Keşif kendi zamanlayıcısıyla: menü ve dünya sahnelerinin
			// ikisinde de nesne aranır (DontDestroyOnLoad sayesinde dünyada
			// da yaşıyor, ama sahne değişiminde referans eskir).
			if (!discovered && now >= nextDiscover) {
				nextDiscover = now + 5f;
				Discover();
			}
			if (!discovered) return;

			// Durumu oku (seyrek): bitti mi, kaç şarkı?
			//
			// DÜZELTME: Import bitince teşhis döngüsü DURDURULUR. Artık
			// "Couldn't open file .../track15.ogg" gibi (zaten 16 slottan
			// sadece 1'i dolu olduğu için normal olan) hataları sonsuza
			// kadar basıyordu. Canlı kanıt: import 11:28:41'de bitti,
			// log 11:29:15'e kadar aynı 3 satırı tekrarladı.
			if (!Imported && now >= nextProbe) {
				nextProbe = now + 2f;
				ProbeStatus();
			}

			if (Imported) return;
			if (attempts >= MAX_ATTEMPTS) return;
			if (now < nextAttempt) return;

			// Yükleyici hâlâ WWWPOST döngüsündeyse araya girme: yeni LOAD
			// `Name` sayacını 1'e çekiyor ve hiçbir şey bitmiyor.
			//
			// CANLI KANIT (11:10):
			//   loader CD1 url: .../CD1/track16.ogg
			//   loader CD1 error: Couldn't open file .../track15.ogg
			//   11:10:26 sent 'Import' (attempt 2)      ← 20 sn sonra yeniden
			if (loadersBusy && now - lastLoaderActivity < 60f) return;

			// OYUN AÇILIR AÇILMAZ (kullanıcı isteği): keşif bittiği anda
			// beklemeden gönder. Eski akış `RETRY_SECONDS` beklediği için
			// `Playlist` FSM'i 11:36:41'de bulunmasına rağmen `sent 'Import'`
			// satırı ancak 20 sn sonra yazılabiliyordu.
			Send();
		}

		/// <summary>Yükleyiciler şu anda WWWPOST döngüsünde mi?</summary>
		static bool loadersBusy;

		/// <summary>Yükleyicide son hareket (URL/Error/state değişimi).</summary>
		static float lastLoaderActivity;

		static float nextDiscover;

		/// <summary>
		/// `Import` olayını gönderir VE CD1/CD2/CD3 yükleyicilerine `LOAD`
		/// gönderir.
		///
		/// NEDEN İKİ YOL — CANLI KANIT:
		///   sent IMPORT (attempt 1)
		///   progress - NOT IMPORTED          ← import olayı yetmiyor
		///
		/// Asset zinciri (`l0/fsms.json`):
		///   Radio/CD      GO "CD"  FSM "Playlist"  olay: Import, Path, CD1/2/3...
		///   Radio/CD/CD1  GO "CD1" FSM "LoadSongs" olay: LOAD, FAIL, PLAY...
		///
		/// Playlist olayı CD yükleyicilerine LOAD iletir, ama zincirin
		/// ara durumuna (`Check` → `SongDatabaseCD` karşılaştırması →
		/// `DestroySelf`) takılınca iletim olmuyor: `Path` NOT IMPORTED'de
		/// kalıyor. Gerçek işi yapan `LoadSongs` FSM'i, o yüzden ona
		/// doğrudan gidiyoruz — hâlâ oyunun kendi WWWPOST yolu, mod
		/// hiçbir ses dosyasını kendisi yüklemiyor.
		/// </summary>
		public static void Send() {
			if (importFsm == null) return;
			attempts++;
			nextAttempt = Time.time + RETRY_SECONDS;

			int loaders = 0;
			try {
				// GO/gizlenmis ise olaylar havaya gider. Canli log kaniti:
				//   found 'CD1' hideFlags=None activeInHierarchy=False
				EnsureRadioCdActive();

				if (importFsm.enabled) {
					importFsm.SendEvent(IMPORT_EVENT);
					// Playlist FSM'i LOAD olayını da tanımlıyor (asset:
					// ADVERT1, DESTROY, FAIL, JINGLE, LOAD, LOOP, ...).
					// Import zinciri takılırsa doğrudan LOAD da işe yarayabilir.
					if (FsmNames.HasEvent(importFsm, "LOAD")) importFsm.SendEvent("LOAD");
				}

				// CD1/CD2/CD3 yükleyicilerine doğrudan LOAD.
				// DİKKAT: tüm sahnelerde "adı CD1/CD2/CD3 olan her şey" değil,
				// SADECE ana menüdeki `Radio/CD` ağacının çocukları. (Canlı
				// log 11:47: "arraylist proxies -> shoppingbagx63/Items=0"
				// → dünya sahnesindeki alışveriş çantalarına da dokunmuştuk.)
				GameObject[] loaders3 = Loaders();
				for (int i = 0; loaders3 != null && i < loaders3.Length; i++) {
					GameObject go = loaders3[i];
					PlayMakerFSM fsm = FindLoader(go);
					if (fsm == null) continue;

					if (!fsm.enabled) fsm.enabled = true;
					if (!go.activeSelf) go.SetActive(true);
					if (FsmNames.HasEvent(fsm, "LOAD")) {
						fsm.SendEvent("LOAD");
						loaders++;
					}
					if (!go.activeInHierarchy) {
						SmpLog.Warning("cd import: LOAD sent to " + Full(go)
							+ " but it is INACTIVE: " + Chain(go));
					}
				}
			}
			catch (Exception e) {
				SmpLog.Warning("cd import: IMPORT failed: " + e.GetType().Name + ": " + e.Message);
				return;
			}

			if (Telemetry.Verbose) SmpLog.Print("cd import: sent '" + IMPORT_EVENT + "' (attempt " + attempts
				+ ") and LOAD to " + loaders + " CD loader(s).");
		}

/// <summary>
		/// Radio/CD ağacını aktif yapar — SADECE ana menüdeki ağaç.
		///
		/// YANLIŞ HALİ (11:44–11:47 canlı kanıt):
		///   Resources.FindObjectsOfTypeAll TÜM sahneleri tarar; "adı Radio/CD/
		///   CD1/CD2/CD3 olan her şeyi" aktifleştirince ARAÇTAKİ radyo/CD
		///   parçalarına da dokunuyorduk. Sonuç:
		///     [SatsumaFSM] 'Assembly' -> 'CD in'   ← oyuncu CD koymamıştı
		///   ayrıca 4 kademe yukarı yürüyüp kapatılmış bütün ebeveynleri de
		///   açıyorduk (log: "arraylist proxies -> shoppingbagx63/Items=0"
		///   dünya sahnesindeki alışveriş çantalarını görüyordu).
		///
		/// DOĞRUSU: sadece `FindPath()` ile bulunan ağaca (ebeveyn adı "Radio"
		/// olan "CD" nesnesi) ve onun CD1/CD2/CD3 çocuklarına dokun; yukarı
		/// doğru en fazla "Radio" seviyesine kadar çık.
		///
		/// CD1'e `SetActive(true)` yetmiyor: `activeInHierarchy` yalnızca TÜM
		/// ebeveynler aktifse true olur. `Radio` ya da `CD` kapalıysa
		/// `LoadSongs` hiç başlamıyor (WWWPOST çalışmıyor, "NOT IMPORTED"
		/// kalıyor). Canlı log: "activated 'CD1/CD2/CD3'" var ama hiç
		/// "loader ... state" satırı yok.
		/// </summary>
		static void EnsureRadioCdActive() {
			try {
				GameObject cd = FindPath();
				if (cd == null) return;

				// 1) CD ağacının kendisi.
				if (!cd.activeSelf) {
					cd.SetActive(true);
					if (Telemetry.Verbose) SmpLog.Log("cd import: activated " + Full(cd));
				}

				// 2) CD1/CD2/CD3 çocukları (yalnızca bu ağacın).
				Transform ct = cd.transform;
				for (int i = 0; ct != null && i < ct.childCount; i++) {
					GameObject child = ct.GetChild(i).gameObject;
					if (child == null) continue;
					if (CdIndex(child.name) < 0) continue;
					if (child.activeSelf) continue;
					child.SetActive(true);
					if (Telemetry.Verbose) SmpLog.Log("cd import: activated " + Full(child));
				}

				// 3) "Radio" seviyesine kadar (en fazla 2 halka) yukarı çık.
				//    Daha yukarısı oyunun sahne kökü — karışmayalım.
				Transform t = cd.transform.parent;
				int guard = 0;
				while (t != null && guard++ < 2) {
					if (!t.gameObject.activeSelf) {
						t.gameObject.SetActive(true);
						if (Telemetry.Verbose) SmpLog.Log("cd import: activated parent " + Full(t.gameObject));
					}
					if (t.gameObject.activeInHierarchy) break;
					t = t.parent;
				}

				// Doğrulama: hâlâ kapalı mı?
				if (!cd.activeInHierarchy) {
					SmpLog.Warning("cd import: " + Full(cd)
						+ " still inactive: " + Chain(cd));
				}
			}
			catch (Exception e) {
				SmpLog.Warning("cd import: activation pass failed: " + e.GetType().Name);
			}
		}

		/// <summary>Nesnenin kökten alta tam yolu (log için).</summary>
		static string Full(GameObject go) {
			if (go == null) return "(null)";
			string p = go.name;
			Transform t = go.transform.parent;
			int guard = 0;
			while (t != null && guard++ < 12) {
				p = t.name + "/" + p;
				t = t.parent;
			}
			return p;
		}

		/// <summary>
		/// Ana menüdeki `Radio/CD` ağacının CD1/CD2/CD3 yükleyicileri.
		///
		/// SADECE bu ağacın çocukları. Tüm sahnelerde isim taraması yapmak
		/// araçtaki CD player parçalarını da kapsıyor ve oyuncu CD koymamışken
		/// "CD in" durumuna geçiriyordu (canlı log 11:44/11:47).
		/// </summary>
		static GameObject[] Loaders() {
			try {
				GameObject cd = FindPath();
				if (cd == null) return new GameObject[0];
				Transform ct = cd.transform;
				GameObject[] res = new GameObject[3];
				int n = 0;
				for (int i = 0; ct != null && i < ct.childCount; i++) {
					GameObject child = ct.GetChild(i).gameObject;
					if (child == null) continue;
					int ci = CdIndex(child.name);
					if (ci < 0 || res[ci] != null) continue;
					res[ci] = child;
					n++;
				}
				if (n == 0) return new GameObject[0];
				GameObject[] outp = new GameObject[n];
				int k = 0;
				for (int i = 0; i < 3; i++) if (res[i] != null) outp[k++] = res[i];
				return outp;
			}
			catch { }
			return new GameObject[0];
		}

		/// <summary>CD1/CD2/CD3 -> 0/1/2; diğer adlar -1.</summary>
		static int CdIndex(string name) {
			if (name == "CD1") return 0;
			if (name == "CD2") return 1;
			if (name == "CD3") return 2;
			return -1;
		}

		/// <summary>
		/// CD1/CD2/CD3 nesnesindeki "LoadSongs" FSM'ini bulur.
		///
		/// Önce `FsmNames.FindOn` (PlayMakerFSM.FindFsmOnGameObject -> FsmList)
		/// denenir. FsmList yalnızca KAYIT OLMUŞ (aktif) FSM'leri tutar;
		/// CD1 ilk kez bizim SetActive çağrımızdan sonra listeye girer.
		/// O güne kadar null döndüğü için bileşenler doğrudan taranır.
		/// Bulunamazsa null.
		/// </summary>
		static PlayMakerFSM FindLoader(GameObject go) {
			if (go == null) return null;
			PlayMakerFSM fsm = FsmNames.FindOn(go, "LoadSongs");
			if (fsm != null) return fsm;
			PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
			for (int i = 0; fsms != null && i < fsms.Length; i++) {
				PlayMakerFSM fx = fsms[i];
				if (fx == null) continue;
				string n = null;
				try { n = (fx.Fsm != null) ? fx.Fsm.Name : null; } catch { }
				if (n == "LoadSongs") return fx;
			}
			return null;
		}

		/// <summary>
		/// Nesnenin ebeveyn zincirini aktiflik bilgisiyle yazar:
		///   CD1[self=T,h=F] &lt;- CD[self=F,h=F] &lt;- Radio[self=T,h=T]
		/// </summary>
		static string Chain(GameObject go) {
			string s = "";
			Transform t = (go != null) ? go.transform : null;
			int guard = 0;
			while (t != null && guard++ < 8) {
				s += (s.Length > 0 ? " <- " : "") + t.name
					+ "[self=" + (t.gameObject.activeSelf ? "T" : "F")
					+ ",h=" + (t.gameObject.activeInHierarchy ? "T" : "F") + "]";
				t = t.parent;
			}
			return s;
		}

		/// <summary>Probe sayacı (her 8 turda ~16 sn'de bir zorunlu log).</summary>
		static int probeTick;

/// <summary>
		/// CD playlist durumunu okur.
		///
		/// ASSET GERÇEKLERİ (l0/fsms.json'ten doğrulandı, tahmin değil):
		///
		///   Radio/CD      GO "CD"  FSM "Playlist"
		///     Check -> Init -> State 4 (DestroySelf) -> State 3
		///     State 3: ActivateGameObject x3  +  BoolAllTrue  -> State 1
		///     State 1: SetStringValue  Path = "NOT IMPORTED" / "CD'S IMPORTED"
		///
		///   Radio/CD/CD1  GO "CD1" FSM "LoadSongs"  (start: Init)
		///     Init        ArrayListClear("Ref") + Wait + SetFsmString
		///     Get file name  GetAppPath -> BuildString("file:///" + appPath
		///                     + FolderName + "track" + Name + ".ogg" -> Path)
		///                   IntCompare(Name, MaxTracks)
		///     Load        WWWPOST(url=Path, storeAudio=Song, errorString=Error)
		///     Add List    ArrayListAdd("Ref", Song)      <-- ASIL IMPORT
		///     Off         SetFsmBool + SetIntValue + EnableFSM
		///
		/// Yani `Path` metni yükleme sırasında URL ile sürekli değişir ve
		/// "CD'S IMPORTED" yalnızca `BoolAllTrue` üç yükleyiciyi de bitmiş
		/// bulduğunda yazılır. Güvenilir gösterge: `Ref` ArrayList'inin
		/// uzunluğu (AudioClip sayısı).
		/// </summary>
		static void ProbeStatus() {
			probeTick++;
			bool forced = (probeTick % 8) == 0;   // ~16 sn'de bir tam rapor
			try {
				string path = null;
				int tracks = -1;
				// CD1/CD2/CD3 liste sayaçları bu turun başında sıfırlanır.
				string clipNames = "";
				for (int i = 0; i < 3; i++) { cdClips[i] = -1; cdClipSeen[i] = false; }

				// 1) `Path` — "Playlist" FSM'inin kendi değişkeni.
				if (importFsm != null) {
					FsmString s = FsmNames.Str(importFsm, "Path");
					if (s != null) path = s.Value;
				}
				// 2) Path yoksa CD nesnesi üzerindeki diğer FSM'lere bak.
				if (string.IsNullOrEmpty(path)) {
					GameObject cd = FindPath();
					if (cd != null) {
						PlayMakerFSM[] fsms = cd.GetComponents<PlayMakerFSM>();
						for (int i = 0; fsms != null && i < fsms.Length; i++) {
							if (fsms[i] == null) continue;
							FsmString s = FsmNames.Str(fsms[i], "Path");
							if (s != null && !string.IsNullOrEmpty(s.Value)) { path = s.Value; break; }
						}
					}
				}

				// 3) CD1/CD2/CD3 yükleyicileri (yalnızca `Radio/CD` ağacının çocukları —
				//    dünya sahnesindeki benzer adlı nesnelere dokunmadan).
				GameObject[] all = Loaders();
				for (int i = 0; all != null && i < all.Length; i++) {
					GameObject go = all[i];
					if (go == null || go.hideFlags != HideFlags.None) continue;
					int ci = CdIndex(go.name);
					if (ci < 0) continue;

					PlayMakerFSM fsm = FindLoader(go);
					if (fsm == null) {
						if (!cdLoggedOnce) {
							cdLoggedOnce = true;
							SmpLog.Warning("cd import: no LoadSongs FSM on '" + go.name + "'");
						}
						continue;
					}

					FsmInt mt = FsmNames.Int(fsm, "MaxTracks");
					if (mt != null && mt.Value > tracks) tracks = mt.Value;

					if (!chainLogged) {
						chainLogged = true;
						if (Telemetry.Verbose) SmpLog.Log("cd import: loader '" + go.name + "' fsm='"
							+ (fsm.Fsm != null ? fsm.Fsm.Name : "?")
							+ "' enabled=" + fsm.enabled + " chain " + Chain(go));
					}

					try {
						string st = "(not started)";
						if (fsm.Fsm != null) {
							string n = fsm.Fsm.ActiveStateName;
							if (!string.IsNullOrEmpty(n)) st = n;
						}

						FsmString errV = FsmNames.Str(fsm, "Error");
						string err = (errV != null) ? errV.Value : "";
						FsmString urlV = FsmNames.Str(fsm, "Path");
						string url = (urlV != null) ? urlV.Value : "";
						FsmString nameV = FsmNames.Str(fsm, "Name");
						string nv = (nameV != null) ? nameV.Value : "";
						FsmString folderV = FsmNames.Str(fsm, "FolderName");
						string folder = (folderV != null) ? folderV.Value : "";

						// Yükleyici gerçekten ÇALIŞIYOR mu?
						//
						// DÜZELTME (11:36) — `st` değişimini de "hareket" sayıyorduk,
						// ama hiç başlamamış bir FSM'de `ActiveStateName` hep boş
						// kalıyor; `""` → `"(not started)"` geçişi İLK probda
						// tetiklenip `loadersBusy=true` yapıyor ve Tick 60 sn
						// boyunca `return` ediyordu. CANLI KANIT:
						//   11:36:41.906 playlist clips = 0
						//   11:36:55.976 CD1 state='(not started)' track=?/16
						//   (… "sent 'Import'" satırı YOK — Send() hiç çağrılmadı)
						//
						// Gerçek çalışma işareti: `Name` sayacının DİZİYE GİRMESİ
						// ya da WWWPOST'un bir URL üretmesi. State tek başına
						// yeterli değil — "daha başlamadı" demektir.
						bool moved = (!string.IsNullOrEmpty(nv) && nv != cdLastName[ci])
							|| (!string.IsNullOrEmpty(url) && url != "NOT IMPORTED" && url != cdLastUrl[ci]);
						if (moved) {
							cdLastName[ci] = nv;
							cdLastUrl[ci] = url;
							cdErr[ci] = err;
							lastLoaderActivity = Time.time;
							loadersBusy = true;
						}
						cdLastState[ci] = st;
						cdErr[ci] = err;
						cdLastUrl[ci] = url;
						CheckErrAgainstDisk(go.name, err);

						if (forced) {
							if (Telemetry.Verbose) SmpLog.Log("cd import: " + go.name
								+ "  state='" + st + "'"
								+ "  track=" + (string.IsNullOrEmpty(nv) ? "?" : nv)
								+ "/" + (mt != null ? mt.Value.ToString() : "?")
								+ "  folder='" + folder + "'"
								+ "  hier=" + (go.activeInHierarchy ? "T" : "F")
								+ "  clips=" + ClipCount(go.name)
								+ ((err.Length > 0) ? "  err=" + err : "")
								+ "  url=" + url);
						}
					}
					catch { }
				}

				// 4) Playlist `Ref` ArrayList'i — ASIL "kaç şarkı yüklendi" göstergesi.
				//    ArrayMaker listesi bir `PlayMakerArrayListProxy` bileşeninde
				//    yaşar (ArrayListAdd: SetUpArrayListProxyProxy(reference="Ref")),
				//    FsmVariables içinde DEĞİLDİR — bu yüzden proxy'yi arıyoruz.
				//
				// CANLI KANIT (11:31:53) — proxy'ler CD1/CD2/CD3 NESNELERİNDE
				// DEĞİL, hepsi "Radio/CD" nesnesinin ÜZERİNDE ve referenceName
				// ile ayrılmış:
				//   arraylist proxies -> CD/CD3=0, CD/CD1=0, CD/CD2=0, Folk/Ads=17
				// Bu yüzden nesne adına göre filtrelemek yanlıştı; referenceName
				// ("CD1"/"CD2"/"CD3") üzerinden eşleştiriyoruz.
				try {
					// Yalnızca `Radio/CD` üzerindeki proxy'ler — dünya sahnesindeki
					// alışveriş çantası/folk listelerini saymıyoruz. Canlı log:
					// "arraylist proxies -> shoppingbagx63/Spraycans=0, …"
					GameObject cdRoot = FindPath();
					PlayMakerArrayListProxy[] proxies =
						(cdRoot != null) ? cdRoot.GetComponents<PlayMakerArrayListProxy>()
										 : new PlayMakerArrayListProxy[0];
					string allProxies = "";
					for (int i = 0; proxies != null && i < proxies.Length; i++) {
						PlayMakerArrayListProxy px = proxies[i];
						if (px == null) continue;
						int n = (px.arrayList != null) ? px.arrayList.Count : -1;

						if (allProxies.Length < 300) {
							allProxies += (allProxies.Length > 0 ? ", " : "")
								+ px.referenceName + "=" + n;
						}

						int ri = CdIndex(px.referenceName);
						if (ri < 0) continue;
						cdClips[ri] = n;
						cdClipSeen[ri] = true;
						clipNames += (clipNames.Length > 0 ? ", " : "")
							+ px.referenceName + "=" + n;
					}
					// Bir kez: CD ağacında hangi ArrayList proxy'leri var?
					if (!proxyLogged) {
						proxyLogged = true;
						if (Telemetry.Verbose) SmpLog.Log("cd import: " + (cdRoot != null ? Full(cdRoot) : "(no CD)")
							+ " proxies -> " + (allProxies.Length > 0 ? allProxies : "(none)"));
					}
				}
				catch { }

				// DÜZELTME: `int clips = -1` ile `clips += n` yazmıştım; -1 + 0
				// hâlâ -1 olduğu için toplam hep yanlış çıkıyordu. Canlı log:
				//   CD1 clips=1  ama  playlist clips = 0  [CD=0, CD=1, CD=0]
				// Artık "bulundu mu" ayrı bir bayrakla tutuluyor.
				int total = 0;
				bool anyClip = false;
				for (int i = 0; i < 3; i++) {
					if (!cdClipSeen[i]) continue;
					anyClip = true;
					if (cdClips[i] > 0) total += cdClips[i];
				}
				int clips = anyClip ? total : -1;

				if (clips != refCount) {
					refCount = clips;
					if (Telemetry.Verbose) SmpLog.Log("cd import: playlist clips = " + clips
						+ (clipNames.Length > 0 ? "  [" + clipNames + "]" : ""));
					// Path metni yazılmayabilir; Ref dolduysa import tamam.
					if (clips > 0 && !Imported) {
						Imported = true;
						if (Telemetry.Verbose) SmpLog.Print("cd import: DONE - " + clips
							+ " clip(s) loaded  [" + clipNames + "]"
							+ ", files on disk=" + CountDiskTracks()
							+ ", Path='" + (path != null ? path : "?") + "'");
					}
				}

				if (path != null && path != lastPath) {
					lastPath = path;
					if (path.IndexOf(DONE_TEXT, StringComparison.OrdinalIgnoreCase) >= 0) {
						Imported = true;
						if (Telemetry.Verbose) SmpLog.Print("cd import: DONE - " + path
							+ (tracks >= 0 ? "  (slots=" + tracks : "")
							+ (refCount >= 0 ? ", clips=" + refCount : "")
							+ ", files on disk=" + CountDiskTracks() + ")");
					} else if (forced || lastPath.IndexOf("NOT IMPORTED", StringComparison.OrdinalIgnoreCase) < 0) {
						if (Telemetry.Verbose) SmpLog.Log("cd import: Path - " + path);
					}
				}
				countReached = tracks;
			}
			catch { }
		}

		/// <summary>
		/// Diskteki gerçek şarkı sayısı (CD1/CD2/CD3 altındaki *.ogg).
		///
		/// Neden önemli: oyunun `MaxTracks` değişkeni 16'dır ve bu bir SLOT
		/// sayısıdır, yüklenen şarkı sayısı DEĞİLDİR. `LoadSongs` FSM'i
		/// Name=1..MaxTracks arasında gezip olmayan dosyalarda
		/// "Couldn't open file" hatası üretir. Kullanıcı 16'yı "16 şarkı
		/// yüklendi" sanabiliyordu.
		/// </summary>
		static int CountDiskTracks() {
			int n = 0;
			try {
				string root = GameRoot();
				if (root == null) return -1;
				string[] discs = new string[] { "CD1", "CD2", "CD3" };
				for (int d = 0; d < discs.Length; d++) {
					string dir = Path.Combine(root, discs[d]);
					if (!Directory.Exists(dir)) continue;
					string[] files = Directory.GetFiles(dir, "*.ogg");
					for (int i = 0; i < files.Length; i++) n++;
				}
			}
			catch { }
			return n;
		}

		/// <summary>
		/// `Couldn't open file` hatası, halbuki dosya diskte VARSA sorun
		/// yol biçimindedir (`file:///` + ters eğik çizgi). Böyle bir
		/// durumu bir kez uyarı olarak yazar.
		/// </summary>
		static bool warnedPath;

		static void CheckErrAgainstDisk(string loaderName, string err) {
			if (warnedPath || string.IsNullOrEmpty(err)) return;
			try {
				string root = GameRoot();
				if (root == null) return;
				// "Couldn't open file /D:\...\CD1\track2.ogg"
				int i = err.IndexOf("Couldn't open file");
				if (i < 0) return;
				string p = err.Substring(i + "Couldn't open file".Length).Trim();
				p = p.TrimStart('/');
				if (p.Length == 0 || !File.Exists(p)) return;
				warnedPath = true;
				SmpLog.Warning("cd import: " + loaderName + " could not open an EXISTING file: " + p
					+ " -> the game's file:/// URL format is the problem (backslashes).");
			}
			catch { }
		}

		/// <summary>Oyun klasörü kökü (…\My Summer Car).</summary>
		static string GameRoot() {
			try {
				string dp = Application.dataPath;   // …\My Summer Car\mysummercar_Data
				if (string.IsNullOrEmpty(dp)) return null;
				string root = Path.GetDirectoryName(dp.TrimEnd('/', '\\'));
				return root;
			}
			catch { }
			return null;
		}

		/// <summary>
		/// `referenceName` verilen ArrayList proxy'sinin eleman sayısı (-1 = yok).
		///
		/// CANLI KANIT (11:31:53): üç `Ref` listesi de `Radio/CD` nesnesinin
		/// ÜSTÜNDE ve `referenceName` = "CD1"/"CD2"/"CD3" ile ayrılmış:
		///   arraylist proxies -> CD/CD1=0, CD/CD2=0, CD/CD3=0
		/// Yani `CD1` nesnesinin kendi bileşeninde proxy YOK; nesne adıyla
		/// eşleştirmek her zaman boş dönerdi.
		/// </summary>
		static int ClipCount(string referenceName) {
			try {
				// Yalnızca `Radio/CD` (= Playlist'in `SongDatabaseCD` nesnesi)
				// üzerindeki proxy'ler. Canlı log kanıtı:
				//   arraylist proxies -> CD/CD1=0, CD/CD2=0, CD/CD3=0
				GameObject cd = FindPath();
				if (cd == null) return -1;
				PlayMakerArrayListProxy[] px =
					cd.GetComponents<PlayMakerArrayListProxy>();
				for (int i = 0; px != null && i < px.Length; i++) {
					if (px[i] == null || px[i].arrayList == null) continue;
					if (px[i].referenceName != referenceName) continue;
					return px[i].arrayList.Count;
				}
			}
			catch { }
			return -1;
		}

		/// <summary>
		/// CD yuvasının durumu — ÇALMA için şart, import yetmez.
		///
		/// ASSET (l2 tarama, `STORE/Boxes/cd player(Clone)/cd player(Clone)`):
		///   trigger_disc  FSM "Assembly"
		///     değişkenler : Distance, CDin, Ok, CD1, CD2, CD3, Part
		///     olaylar     : TRIGGER ENTER, ASSEMBLE, CD1, CD2, CD3, CDIN,
		///                   DISASSEMBLE, EJECT, GLOBALEVENT, LOOP, PROCEED
		///     GetFsmBool(Status/CDPlayerOK), SendEventByName("CD1"/"CD2"/"CD3")
		///
		///   cd player(Clone) FSM "Assembly"  -> "CD in" / "State 1"
		///
		/// NEDEN ÖNEMLİ: oyun 3 ayrı CD yuvası tutar (CD1/CD2/CD3) ve
		/// `CDPlayer/Update` sesleri `ArrayListGet(SongDatabaseCD, CurrentCD)`
		/// ile okur. Yani CD2 takılıysa CD2 listesi çalar; bizim
		/// `track1.ogg` CD1'e yüklendiği için CD2 boş gelir.
		///
		/// CD takılı değilse `CDPlayer/Fetch` -> GetFsmBool(Data/Installed)
		/// -> BoolTest(db_Required) -> "No sound" -> ses çıkmaz.
		/// </summary>
		static string CdPlayerState() {
			try {
				GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
				string slotState = "cd player(Clone) not found (world not loaded?)";
				string which = "";

				for (int i = 0; all != null && i < all.Length; i++) {
					GameObject go = all[i];
					if (go == null || go.hideFlags != HideFlags.None) continue;

					// --- 1) Satsuma yuvasının FSM'i (araç; mağaza kutusu değil)
					if (go.name == "cd player(Clone)" && InSatsuma(go)) {
						PlayMakerFSM slotFsm = FsmNames.FindOn(go, "Assembly");
						if (slotFsm != null) {
							string st = (slotFsm.Fsm != null) ? slotFsm.Fsm.ActiveStateName : "";
							if (string.IsNullOrEmpty(st)) st = "(idle)";
							slotState = "slot='" + st + "'"
								+ (st.IndexOf("CD in", StringComparison.OrdinalIgnoreCase) >= 0
									? "  [CD INSTALLED]" : "  [no disc in slot]");
						}
					}

					// --- 2) trigger_disc: hangi CD takılı (CD1/CD2/CD3 bool)
					if (go.name != "trigger_disc") continue;
					PlayMakerFSM fsm = FsmNames.FindOn(go, "Assembly");
					if (fsm == null) continue;

					string b1 = B(fsm, "CD1"), b2 = B(fsm, "CD2"), b3 = B(fsm, "CD3");
					string cdin = B(fsm, "CDin");
					if (b1 == "?" && b2 == "?" && b3 == "?") continue;

					// Satsuma yoksa diğer araçları da yaz (KEKMET/FERNDALE/…)
					which += (which.Length > 0 ? "  |  " : "") + Root(go)
						+ ": CD1=" + b1 + " CD2=" + b2 + " CD3=" + b3
						+ " CDin=" + cdin
						+ " -> plays " + Active(b1, b2, b3);

					// Status/CDPlayerOK: CD player çalışabilir mi?
					FsmBool ok = FsmNames.Bool(fsm, "CDPlayerOK");
					if (ok != null) which += " ok=" + ok.Value;
				}

				return slotState + (which.Length > 0 ? "\n           disc: " + which : "");
			}
			catch { }
			return "cd slot: probe failed";
		}

		static string B(PlayMakerFSM fsm, string name) {
			FsmBool b = FsmNames.Bool(fsm, name);
			if (b == null) return "?";
			return b.Value ? "T" : "F";
		}

		static string Active(string c1, string c2, string c3) {
			if (c1 == "T") return "CD1";
			if (c2 == "T") return "CD2";
			if (c3 == "T") return "CD3";
			return "nothing";
		}

		/// <summary>Nesnenin en üst kök ebeveyninin adı (aracı belirtir).</summary>
		static string Root(GameObject go) {
			Transform t = (go != null) ? go.transform : null;
			Transform best = t;
			int guard = 0;
			while (t != null && guard++ < 14) { best = t; t = t.parent; }
			return (best != null) ? best.name : "?";
		}

		/// <summary>Satsuma dashboard'undaki CD yuvası mı? (mağaza kutusu değil)</summary>
		static bool InSatsuma(GameObject go) {
			try {
				Transform t = (go != null) ? go.transform : null;
				int guard = 0;
				while (t != null && guard++ < 14) {
					if (t.parent != null
						&& t.parent.name.IndexOf("pivot_radio", StringComparison.OrdinalIgnoreCase) >= 0) {
						return Root(go).IndexOf("SATSUMA", StringComparison.OrdinalIgnoreCase) >= 0;
					}
					t = t.parent;
				}
			}
			catch { }
			return false;
		}

		// ------------------------------------------------------------------
		// Tanı
		// ------------------------------------------------------------------

		/// <summary>'smp songs' çıktısı.</summary>
		public static string Describe() {
			if (!discovered) return "cd import: FSM NOT FOUND (IMPORT event) - music will not load";
			string st = string.IsNullOrEmpty(lastPath) ? "(no status yet)" : lastPath;
			return "cd import: " + (Imported ? "IMPORTED" : "pending")
				+ "  attempts=" + attempts + "/" + MAX_ATTEMPTS
				+ "  slots=" + (countReached < 0 ? "?" : countReached.ToString())
				+ "  clips=" + (refCount < 0 ? "?" : refCount.ToString())
				+ "  files=" + CountDiskTracks()
				+ "  status='" + st + "'"
				+ "\n           slot: " + CdPlayerState();
		}
	}
}
