using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// ELDE TUTULAN EŞYANIN SENKRONİZASYONU.
	///
	/// Belirti (canlı peer kanıtı): "player eline eşya alıyor görünmüyor".
	///
	/// EL NEREDE? (log kanıtı, çok önemli)
	/// ------------------------------------
	/// Oyuncu kökünün altında kemik değil, KAMERA altında bir el takımı var:
	///
	///   PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand
	///
	/// Bu yol logdan okundu (geç bağlama satırı: "late-tracked
	/// 'PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand'"), yani
	/// build'e göre doğrulanmış bir yol. ÖNCEKİ DENEME bu yolu kukla
	/// modelinde arıyordu; kukla bir oyuncu MODELİ, kamerası/el takımı yok,
	/// yani arama hep null döndü ve eşya hiç görünmedi. Bu yüzden artık
	/// parent'lama yok: eşyanın DÜNYA konumu doğrudan akışa gider.
	///
	/// NEDEN OLAY REPLAY EDİLMİYOR
	/// ---------------------------
	/// ItemSync olayı karşı tarafta oynatıyordu. Ama MSC'de alma işi eşyayı
	/// el takımına PARENT etmektir; hedef yerel oyuncunun kendi nesnesi.
	/// Replay eşyayı YEREL oyuncunun eline takar: kuklada eşya yok, kendi
	/// elimizde havada eşya var. Alma/bırakma olayları bu yüzden ItemSync'te
	/// gürültü sayılıyor.
	///
	/// AKIŞ
	/// ----
	///   1. Yayıncı 5 Hz'de el takımının altındaki yabancı nesneyi bulur
	///      (pasif de olabilir: oyun alınan eşyayı kapatabiliyor).
	///   2. "Tutuyor / bıraktı" değişimi GÜVENLİR gider (nadir olay).
	///   3. Tutulduğu sürece dünya konumu 10 Hz GÜVENSİZ gider: el hareket
	///      eder, konum kaybolsa bir sonraki pakette düzelir.
	///   4. Alıcı eşyayı fizikten çıkarır (kinematik, çarpışma kapalı) ve
	///      gelen konuma yumuşaklaştırılmış olarak gider. Bırakılınca
	///      fizik geri verilir, eşya düşmeye başlar.
	///
	/// Kayıt biçimi (sabit uzunluk, yazma/okuma eşleşmesi kolay):
	///   bool held | string path | 3x float konum | 4x float dönüş
	/// </summary>
	public static class HeldItemSync {

		/// <summary>Tespit aralığı (s).</summary>
		public const float POLL_INTERVAL = 0.2f;
		/// <summary>Tutulan eşyanın konum akışı (s).</summary>
		public const float POSE_INTERVAL = 0.1f;
		/// <summary>Boş el / elde eşya kalp atışı (s).</summary>
		public const float HEARTBEAT = 2f;
		/// <summary>Konum akışının yumuşatma hızı (1/s).</summary>
		public const float FOLLOW_RATE = 20f;

		/// <summary>Build'de doğrulanmış el yolu (PLAYER köküne göre).</summary>
		public const string HAND_PATH = "Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand";

		/// <summary>
		/// El takımı aranırken kullanılacak adlar.
		///
		/// CANLI KANIT (21:32:37, "held item: HAND NOT FOUND"): sabit yol bu
		/// build'de çözülmedi. Sebep: 1Hand_Assemble NESNESİ PASİF — üretimde
		/// kapalı, sadece oyun içinde açılıyor. Varsayılan arama yöntemleri
		/// (GetComponentsInChildren dahil) pasifleri dışarıda bırakıyor, yani
		/// 4 seviye aşağı inen ad araması da boş dönüyordu.
		/// </summary>
		/// <summary>
		/// El takımı adayları.
		///
		/// CANLI KANIT (22:26:03): "hand assembly is
		/// 'PLAYER/Pivot/AnimPivot/Camera/FPSCamera/Fuel/Pivot/hand'". Bu el
		/// DEĞİL, benzin kapağının tutamağı; adı "hand" olduğu için aday
		/// çıktı. Bu yüzden iki şart birlikte aranır:
		///   1) ad "Hand_Assemble" içermeli (çıplak "hand" yetmez),
		///   2) bir "FPSCamera" ebeveyni olmalı.
		/// Doğru nesnenin yolu logla doğrulandı:
		///   PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand
		/// </summary>
		static readonly string[] HAND_NAMES = {
			"1Hand_Assemble", "Hand_Assemble", "Hands_Assemble",
		};

		/// <summary>El takımının hemen üstündeki kamera nesnesi.</summary>
		public const string CAMERA_NODE = "FPSCamera";

		static Transform hand;
		static bool handSearched;
		static bool handWarned;
		static float nextHandRetry;
		/// <summary>El alt ağacı bir kez döküldü mü (teşhis, sonsuz tekrak için).</summary>
		static bool handContentsLogged;

		/// <summary>El bulunamazsa yeniden arama aralığı (s).</summary>
		public const float HAND_RETRY = 2f;

		// Yayıncı
		static string lastSentPath;
		static bool lastSentHeld;
		static bool lastLoggedHeld;
		static string lastLoggedPath;
		static float nextPoll;
		static float nextPose;
		static float nextBeat;

		// Alıcı
		static GameObject remoteItem;
		static bool remoteHeld;
		static Vector3 targetPos;
		static Quaternion targetRot = Quaternion.identity;
		static bool targetValid;

		/// <summary>Gönderilen kayıt sayısı.</summary>
		public static int Sent;
		/// <summary>Uygulanan kayıt sayısı.</summary>
		public static int Applied;
		/// <summary>Karşı tarafta bulunamadığı için atlanan eşya.</summary>
		public static int Unresolved;

		// ------------------------------------------------------------------
		// Yaşam döngüsü
		// ------------------------------------------------------------------

		/// <summary>Dünya yüklendi.</summary>
		public static void Discover() {
			hand = null;
			handSearched = false;
			handWarned = false;
			nextHandRetry = 0f;
			handContentsLogged = false;
			baselineTaken = false;
			activeAtLoad.Clear();
			cachedPath = null;
			cachedObject = null;
			lastSentPath = null;
			lastSentHeld = false;
			nextPoll = 0f;
			nextPose = 0f;
			nextBeat = 0f;
			baselineCamName = null;
			nextNameFallback = 0f;
			missingPaths.Clear();
			ReleaseRemote();
		}

		/// <summary>Dünya değişiminde bırakır.</summary>
		public static void Clear() {
			hand = null;
			handSearched = false;
			handWarned = false;
			nextHandRetry = 0f;
			baselineTaken = false;
			activeAtLoad.Clear();
			cachedPath = null;
			cachedObject = null;
			lastSentPath = null;
			lastSentHeld = false;
			// UZAK EŞYANIN FİZİĞİ GERİ VERİLİR.
			//
			// CANLI HATA (düzeltildi): burada sadece `remoteItem = null` ve
			// `remoteHeld = false` yazılıyordu. Ama karşı oyuncunun tuttuğu eşya
			// `ForceVisible(item, true)` ile kinematik yapılmış, collider'ları
			// kapatılmış ve `detectCollisions = false` bırakılmıştı. Onları geri
			// veren tek yer `ReleaseRemote()` (965-973) ve `Clear()` onu
			// atlıyordu.
			//
			// Sonuç: `Clear()` koştuğunda (dünya değişimi / bağlantı kopması)
			// eşya sahnede kalıyorsa fizik kapalı, havada donuk doğuyor ve
			// yerel oyuncu onu alamıyordu. Aynı hata `CarrySync.SetDropping`'de
			// de vardı ve orada `RestoreLocalPhysics` ile kapatıldı.
			ReleaseRemote();
			// ReleaseRemote remoteItem/remoteHeld/remotePath'i sıfırlar ama
			// `targetValid`'ı dokunmaz; izleme hedefinin geçersiz olduğunu
			// ayrıca söylemek gerekir, yoksa sonraki karede boş bir hedefe
			// doğru interpolasyon denenir.
			targetValid = false;
			Sent = 0;
			Applied = 0;
			Unresolved = 0;
		}

		/// <summary>'smp player' tanısı için tek satır.</summary>
		public static string Describe() {
			// v15: 'smp player' artik kamera agacini da ZORLA doker.
			// Otomatik akista bu döküm varsayilan olarak kapalı; sadece
			// Telemetry.Verbose'a acikken yaziliyor. Ama mesajin kendi
			// metni "run 'smp player' and send that line" diyor, yani
			// komut teşhis araci olarak kullanilmali. Komut bunu
			// istemeden yazmasin diye burada force=true ile cagriliyor.
			DumpHandTree(true);
			string where = hand == null
				? "HAND NOT FOUND (searched the whole scene, including inactive objects)"
				: "hand='" + SafePath(hand.gameObject) + "'";
			string mine = string.IsNullOrEmpty(lastSentPath) ? "-" : lastSentPath;
			string theirs = remoteItem == null ? "-" : SafePath(remoteItem);
			return "held item: " + where + ", mine='" + mine + (lastSentHeld ? "' (held)" : "' (empty)")
				+ ", peer='" + theirs + (remoteHeld ? "' (held)" : "'")
				+ ", sent " + Sent + ", applied " + Applied + ", unresolved " + Unresolved
				+ ", poll " + SmpTrace.HandPolls + ", found " + SmpTrace.HandFound
				+ ", base " + activeAtLoad.Count + "+" + visibleAtLoad.Count + " (" + (baselineCamName ?? "-") + ")";
		}

		static string SafePath(GameObject go) {
			if (go == null) return "?";
			try { return SceneDump.PathOf(go.transform); }
			catch { return "?"; }
		}

		// ------------------------------------------------------------------
		// El takımını bul
		// ------------------------------------------------------------------

		static bool ResolveHand() {
			hand = null;
			// DÜNYA YÜKLENMEDEN ARAMA YAPMA.
			// Canlı log kanıtı: 22:26:03 (PreLoad, kayıt henüz yüklenmemiş)
			// el takımı bulunamadı ve tarama yanlış nesneye (benzin kapağı)
			// yapıştı. Root hazır olunca aranır.
			// v15 DUZELTME: readiness icin `SatsumaRegistry.Root` KULLANILMAZDI.
			// O, "SATSUMA(557kg, 248)" nesnesidir ve el takimiyla HIC ilgisi
			// yoktur. Sonucu: Satsuma koku yokken / gec yuklenirken el takimi
			// hic aranmiyordu, `Update()` `:701`'de donuyordu ve `[ACT] HAND`
			// logu ile `Msg.HeldItem` paketi URETILMIYORDU. Belirti:
			// "karsi tarafin elinde bir sey gorunmuyor".
			//
			// Dogru sinyal: PLAYER koku. Adim 1 zaten onu ariyor, yani hazir
			// olma ani ile aramanin nesnesi ayni sey.
			GameObject playerRoot = null;
			try { playerRoot = GameObject.Find("PLAYER"); } catch { }
			if (playerRoot == null) return false;

			// 1) Logla doğrulanmış yol.
			try {
				GameObject root = GameObject.Find("PLAYER");
				if (root != null) {
					Transform t = root.transform.Find(HAND_PATH);
					if (t != null) { return Accept(t, "known path"); }
				}
			}
			catch { }

			// 2) Tüm sahne (pasif dahil): ad "Hand" İÇERMELİ VE bir FPSCamera
			//    ebeveyni olmalı. İkinci şart benzin kapağını eler.
			//
			// DÜZELTME (canlı log, client 12:08:47):
			//   "held item: no hand assembly found yet."
			// Bu uyarı sürekli tekrarlıyordu. İki koşul da yanlıştı:
			//
			// (a) `if (t.childCount == 0) continue;` — "boş kılıf değil" diye
			//     düşünülmüştü. Ama el takımı TUTULAN EŞYA YOKKEN boştur!
			//     Yani filtre, tam olarak aradığımız nesneyi elendiği anda
			//     eliyordu: oyuncu hiçbir şey tutmuyorken el bulunamıyor,
			//     dolayısıyla "el doldu" anı da asla yakalanamıyordu.
			//     -> Filtre KALDIRILDI. Ad + FPSCamera ebeveyni zaten yeterince
			//        seçici.
			//
			// (b) `HAND_NAMES` tam eşleşme: "1Hand_Assemble", "Hand_Assemble",
			//     "Hands_Assemble". Bu build'de farklıysa (örn. "1Hand_Assemble
			//     (Clone)" veya sadece "Hand") hiç eşleşmiyordu.
			//     -> "Hand" İÇEREN ad kabul edildi (benzin kapağı "Fuel/Pivot/hand"
			//        adıyla eleniyor çünkü FPSCamera ebeveyni YOK).
			Transform[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<Transform>(); }
			catch { }
			if (all != null) {
				Transform best = null;
				int bestDepth = -1;
				for (int i = 0; i < all.Length; i++) {
					Transform t = all[i];
					if (t == null) continue;
					if (!IsHandAssembly(t)) continue;
					// En DERİN eşleşmeyi seç: "…/1Hand_Assemble/Hand".
					int d = 0;
					Transform cur = t;
					while (cur != null && d < 32) { d++; cur = cur.parent; }
					if (d > bestDepth) { bestDepth = d; best = t; }
				}
				if (best != null) return Accept(best, "scene scan");
			}

			// 3) Son çare: "Hand" içeren her ad.
			if (all != null) {
				for (int i = 0; i < all.Length; i++) {
					Transform t = all[i];
					if (t == null) continue;
					if (t.name.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (!HasAncestorNamed(t, CAMERA_NODE)) continue;
					return Accept(t, "hand-name scan");
				}
			}

			handSearched = false;   // sahne biraz sonra kurulabilir, yeniden dene
			return false;
		}

		/// <summary>
		/// Gerçekten oyuncunun el takımı mı?
		///
		/// İki şart:
		///   1) ad "Hand" İÇERMELİ — çıplak eşleşme değil, "alt dizin".
		///      (benzin kapağı "Fuel/Pivot/hand" → 2. şart onu eler)
		///   2) bir "FPSCamera" EBEVEYNİ olmalı.
		/// </summary>
		static bool IsHandAssembly(Transform t) {
			if (t == null) return false;
			string n = t.name;
			bool nameOk = (n.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0);
			if (!nameOk) {
				for (int i = 0; i < HAND_NAMES.Length; i++) {
					if (string.Equals(n, HAND_NAMES[i], StringComparison.OrdinalIgnoreCase)) { nameOk = true; break; }
				}
			}
			if (!nameOk) return false;
			return HasAncestorNamed(t, CAMERA_NODE);
		}

		static bool HasAncestorNamed(Transform t, string name) {
			Transform cur = t != null ? t.parent : null;
			int guard = 0;
			while (cur != null && guard++ < 32) {
				if (string.Equals(cur.name, name, StringComparison.OrdinalIgnoreCase)) return true;
				cur = cur.parent;
			}
			return false;
		}

		static bool Accept(Transform t, string how) {
			hand = t;
			handSearched = true;
			handContentsLogged = false;
			if (Telemetry.Verbose) if (Telemetry.Verbose) if (Telemetry.Verbose) SmpLog.Log("held item: hand assembly is '" + SafePath(t.gameObject)
				+ "' (found by " + how + ").");
			return true;
		}

		// ------------------------------------------------------------------
		// Tespit
		// ------------------------------------------------------------------

		/// <summary>
		/// El takımının altındaki tutulan eşya.
		///
		/// TESPİT NEDEN BU KADAR KISITLI DEĞİL (canlı log kanıtı, 23:12):
		/// "probe: hand-poll 10475, hand-found 0". Yani on bin tur tarandı,
		/// bir kez bile eşya bulunamadı. İki olasılık vardı: (a) eşya elin
		/// altında değil, (b) eşya orada ama renderer/collider kuralı
		/// eliyor. Ayırt etmek için el takımının alt ağacı bir kez dökülür
		/// (DumpHandTree); kural ancak ondan sonra kesinleşir.
		///
		/// Şimdilik kural gevşek: "renderer'ı olan her şey" eşya sayılır
		/// (collider şartı kaldırıldı). El takımı boşken yanlış pozitif
		/// olmaz çünkü o zaman ortada hiçbir nesne yok.
		/// </summary>
		static GameObject FindHeld() {
			if (hand == null) return null;
			// Ağacı KOŞULSUZ bir kez dök: canlı log kanıtı (23:40) el ağacı
			// BOŞ çıktı, tutulan eşya orada değilmiş.
			if (!handContentsLogged) DumpHandTree(false);
			if (!baselineTaken) { CaptureBaseline(); return null; }
			return FindByDiff();
		}

		// ------------------------------------------------------------------
		// YÜKLEME ANI FARKI (canlı log kanıtı, 23:48)
		// ------------------------------------------------------------------
		// Hiyerarşide +HIER (aktifInHierarchy) işaretleri boşta bile şunları
		// gösteriyor: Drink, Fuel, Helmet, Stains, HandCamera, ThrowBottle.
		// Yani tutulan eşyanın KAPISI her zaman açık; sadece içindeki eşya
		// açılıyor ("-HIER BeerBottle", "-HIER BoozeBottle").
		//
		// Bu yüzden ne "aktif olan" ne de "ilk bulunan" işe yarıyor:
		//   * aktif olan → kalıcı kapı (yanlış pozitif: [ACT] HAND took Drink)
		//   * ilk bulunan → yine kalıcı kapı
		//
		// DOĞRU KURAL: dünya yüklenirken aktif olan düğüm kümesi referanstır;
		// elde tutulan eşya = şu an aktif olup referansta OLMAYAN düğüm.
		// İsim listesi, seçim kuralı, öncelik sırası gerekmez.

		static readonly HashSet<int> activeAtLoad = new HashSet<int>();
		static readonly HashSet<int> visibleAtLoad = new HashSet<int>();
		static string baselineCamName;
		static bool baselineTaken;

		static void CaptureBaseline() {
			activeAtLoad.Clear();
			visibleAtLoad.Clear();
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) return;
			Transform t;
			try { t = cam.transform; } catch { return; }
			if (t == null) return;
			try { baselineCamName = cam.name; } catch { baselineCamName = "?"; }
			CollectActive(t, activeAtLoad);
			CollectVisible(t, visibleAtLoad);
			baselineTaken = true;
			if (Telemetry.Verbose) if (Telemetry.Verbose) if (Telemetry.Verbose) SmpLog.Log("held item: baseline captured under '" + baselineCamName + "': "
				+ activeAtLoad.Count + " active, " + visibleAtLoad.Count
				+ " with enabled renderer. Held item = newly VISIBLE node.");
		}

		static void CollectVisible(Transform t, HashSet<int> into) {
			if (t == null) return;
			int n;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				bool live = false;
				try { live = c.gameObject.activeInHierarchy; } catch { }
				if (live && HasEnabledRenderer(c)) {
					int id = c.GetInstanceID();
					if (!into.Contains(id)) into.Add(id);
				}
				CollectVisible(c, into);
			}
		}

		static bool HasEnabledRenderer(Transform t) {
			Renderer[] rends = null;
			try { rends = t.GetComponentsInChildren<Renderer>(true); } catch { }
			if (rends == null) return false;
			for (int i = 0; i < rends.Length; i++) {
				Renderer r = rends[i];
				if (r == null || !r.enabled) continue;
				bool live = false;
				try { live = r.gameObject.activeInHierarchy; } catch { }
				if (live) return true;
			}
			return false;
		}

		static void CollectActive(Transform t, HashSet<int> into) {
			if (t == null) return;
			int n;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				bool live = false;
				try { live = c.gameObject.activeInHierarchy; } catch { }
				if (live) {
					int id = c.GetInstanceID();
					if (!into.Contains(id)) into.Add(id);
				}
				CollectActive(c, into);
			}
		}

		/// <summary>
		/// Referansta olmayan aktif düğümü bulur, sonra İÇİNDeki gerçek eşyaya iner.
		///
		/// Canlı log kanıtı (23:51): fark kuralı çalıştı —
		/// "[ACT] HAND took .../Drink/Hand/HandBottles" — ama yakalanan
		/// KAPSAYICI. Kapsayıcının altındaki şişeyi senkronlamak yanlış
		/// olur (kutudaki tüm şişeler taşınır). Bu yüzden bulunan düğümün
		/// altına inilir: iskelet adı olmayan (Hand/Pivot/mesh/Armature
		/// elenmiştir) ve renderer'ı olan EN DERİN düğüm gerçek eşyadır.
		/// Referans kümesi bu aşamada UYGULANMAZ; kapsayıcı yeni açıldığı için
		/// içindeki her şey yeni sayılır.
		/// </summary>
		static GameObject FindByDiff() {
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			Transform t = null;
			try { t = cam != null ? cam.transform : null; } catch { t = null; }
			GameObject found = null;
			int foundDepth = -1;
			if (t != null) DiffVisible(t, 0, ref found, ref foundDepth);
			if (found == null && hand != null) {
				try { DiffVisible(hand, 0, ref found, ref foundDepth); } catch { }
			}
			if (found == null && t != null) Diff(t, 0, ref found, ref foundDepth);
			if (found == null && hand != null) {
				try { Diff(hand, 0, ref found, ref foundDepth); } catch { }
			}
			if (found == null) return null;
			GameObject item = DeepestMeshUnder(found.transform, 0);
			if (item != null) return item;
			return HasEnabledRenderer(found.transform) ? found : null;
		}

		static void DiffVisible(Transform t, int depth, ref GameObject found, ref int foundDepth) {
			if (t == null || depth > 6) return;
			int n;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				bool live = false;
				try { live = c.gameObject.activeInHierarchy; } catch { }
				if (live && IsRigNode(c.name) == false && HasEnabledRenderer(c)
					&& !visibleAtLoad.Contains(c.GetInstanceID())) {
					if (depth + 1 > foundDepth) {
						GameObject go = null;
						try { go = c.gameObject; } catch { }
						if (go != null) { found = go; foundDepth = depth + 1; }
					}
				}
				DiffVisible(c, depth + 1, ref found, ref foundDepth);
			}
		}

		static GameObject DeepestMeshUnder(Transform t, int depth) {
			GameObject lit = DeepestMeshUnderEx(t, depth, true);
			if (lit != null) return lit;
			return DeepestMeshUnderEx(t, depth, false);
		}

		static GameObject DeepestMeshUnderEx(Transform t, int depth, bool enabledOnly) {
			if (t == null || depth > 6) return null;
			GameObject best = null;
			int n;
			try { n = t.childCount; } catch { return null; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				bool live = false;
				try { live = c.gameObject.activeInHierarchy; } catch { }
				if (!live) continue;
				bool ok = IsRigNode(c.name) == false
					&& (enabledOnly ? HasEnabledRenderer(c) : HasRenderer(c));
				if (ok) {
					GameObject go = null;
					try { go = c.gameObject; } catch { }
					if (go != null) best = go;
				}
				GameObject deeper = DeepestMeshUnderEx(c, depth + 1, enabledOnly);
				if (deeper != null) best = deeper;
			}
			return best;
		}

		static void Diff(Transform t, int depth, ref GameObject found, ref int foundDepth) {
			if (t == null || depth > 5) return;
			int n;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				bool live = false;
				try { live = c.gameObject.activeInHierarchy; } catch { }
				if (live && IsRigNode(c.name) == false && HasRenderer(c)
					&& !activeAtLoad.Contains(c.GetInstanceID())) {
					if (depth + 1 > foundDepth) {
						GameObject go = null;
						try { go = c.gameObject; } catch { }
						if (go != null) { found = go; foundDepth = depth + 1; }
					}
				}
				Diff(c, depth + 1, ref found, ref foundDepth);
			}
		}

		/// <summary>İskelet olmayan yardımcı düğüm adları (her zaman açık).</summary>
		static readonly string[] RIG_NODES = {
			"Armature", "hand_rigged", "Pivot", "mesh", "RayCast", "Raycast",
			"Camera", "Anim", "Animate", "Bar", "Up", "Down", "mask", "Watch",
			"TimeStamp", "CameraStamp", "BreathAnim", "Stain", "Stains",
			"Water", "Lift", "Rotate", "Sphere", "FatigueEyes", "SleepEyes",
			"HelmetPivot", "HandCamera", "AudioRain", "Collider", "RigidBody",
		};

		/// <summary>
		/// Tutulan eşya = kameranın altında ETKİLİ OLARAK AÇIK ve renderer'ı
		/// olan EN DERİN nesne.
		///
		/// CANLI LOG KANITI (23:40) — üç hatayı birden düzeltir:
		///   1) Eski kural el altını bakıyordu; oysa eşya kameranın altında.
		///   2) Eski kural activeSelf kullanıyordu; parent kapalıyken child
		///      "on" görünür ("Sledgehammer+off ... hand+on"). Doğrusu
		///      activeInHierarchy.
		///   3) Eski kural "ilk bulunan renderer" idi ve kalıcı bir düğümü
		///      seçiyordu: "[ACT] HAND took .../Fuel" (benzin kapağı) — yani
		///      EYLEM DEĞİL diye yanlış pozitif. En derin düğüm + iskelet
		///      dışı filtresi bunu eler.
		/// </summary>
		static GameObject FindDeepestActive() {
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) return null;
			Transform t;
			try { t = cam.transform; } catch { return null; }
			if (t == null) return null;
			GameObject found = null;
			int foundDepth = -1;
			Deepest(t, 0, ref found, ref foundDepth);
			return found;
		}

		static void Deepest(Transform t, int depth, ref GameObject found, ref int foundDepth) {
			if (t == null || depth > 5) return;
			int n;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				bool live = false;
				try { live = c.gameObject.activeInHierarchy; } catch { }
				if (live && IsRigNode(c.name) == false && HasRenderer(c)) {
					if (depth + 1 > foundDepth) {
						GameObject go = null;
						try { go = c.gameObject; } catch { }
						if (go != null) { found = go; foundDepth = depth + 1; }
					}
				}
				Deepest(c, depth + 1, ref found, ref foundDepth);
			}
		}

		static bool IsRigNode(string n) {
			if (string.IsNullOrEmpty(n)) return true;
			for (int i = 0; i < RIG_NODES.Length; i++) {
				if (string.Equals(n, RIG_NODES[i], StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		/// <summary>
		/// El takımının alt ağacını bir kez döker. Teşhisin tamamı buna
		/// bağlı: eşya nereye parent oluyor bilmeden tespit kuralı yazılamaz.
		/// </summary>
		/// <param name="force">
		/// true ise 'smp player' komutuyla elle istendi demektir ve her
		/// zaman yazilir. false ise otomatik çağrıdır ve yalnızca
		/// Telemetry.Verbose açıkken yazar.
		/// </param>
		static void DumpHandTree(bool force) {
			handContentsLogged = true;
			if (!Telemetry.Verbose && !force) return;
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) return;
			System.Text.StringBuilder sb = new System.Text.StringBuilder(320);
			string camName = "?";
			try { camName = cam != null ? cam.name : "?"; } catch { }
			sb.Append("held item: camera subtree under '").Append(camName).Append("' (name+mesh+body+HIER, HIER=aktif:");
			Transform ct;
			try { ct = cam.transform; } catch { return; }
			Walk(ct, 1, sb, 0);
			// v15: bu bir istege bagli arac ciktisiydi; rutin olarak
			// otomatik calisiyor ve WARNING seviyesinde tek satira
			// yuzlerce dugum yaziyordu (canli log L156, ~2500 karakter).
			// Artik varsayilan olarak hic yazmiyor.
			SmpLog.Log(sb.ToString());
		}

		static void Walk(Transform t, int depth, System.Text.StringBuilder sb, int budget) {
			// v15: 24x4 = yuzlerce dugum. Teshis icin ilk 40 yeterli ve
			// satiri okunur kilar.
			if (t == null || budget > 40 || depth > 4) return;
			int n;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				sb.Append(' ').Append('\'').Append(c.name).Append('\'');
				sb.Append(HasRenderer(c) ? "+mesh" : "-mesh");
				sb.Append(HasPhysics(c) ? "+body" : "-body");
				bool act = false;
				try { act = c.gameObject.activeInHierarchy; } catch { }
				sb.Append(act ? "+HIER" : "-HIER");
				Walk(c, depth + 1, sb, budget + 1);
			}
		}

		/// <summary>En üstteki renderer'ı olan nesneyi döndürür (collider şartı yok).</summary>
		static GameObject FindVisible(Transform t, int depth) {
			if (t == null || depth > 6) return null;
			Renderer[] rends = null;
			try { rends = t.GetComponentsInChildren<Renderer>(true); } catch { }
			if (rends != null && rends.Length > 0) {
				GameObject go = null;
				try { go = t.gameObject; } catch { }
				if (go != null) return go;
			}
			int n;
			try { n = t.childCount; } catch { return null; }
			for (int i = 0; i < n; i++) {
				GameObject go = FindVisible(t.GetChild(i), depth + 1);
				if (go != null) return go;
			}
			return null;
		}

		/// <summary>En üstteki renderer'ı VE collider'ı olan nesneyi döndürür.</summary>
		static GameObject FindPickable(Transform t, int depth) {
			if (t == null || depth > 6) return null;
			if (HasRenderer(t) && HasPhysics(t)) {
				GameObject go = null;
				try { go = t.gameObject; } catch { }
				if (go != null) return go;
			}
			int n;
			try { n = t.childCount; } catch { return null; }
			for (int i = 0; i < n; i++) {
				GameObject go = FindPickable(t.GetChild(i), depth + 1);
				if (go != null) return go;
			}
			return null;
		}

		static bool HasRenderer(Transform t) {
			Renderer[] rends = null;
			try { rends = t.GetComponentsInChildren<Renderer>(true); } catch { }
			return rends != null && rends.Length > 0;
		}

		static bool HasPhysics(Transform t) {
			try {
				if (t.GetComponent<Rigidbody>() != null) return true;
				Collider[] cols = t.GetComponentsInChildren<Collider>(true);
				return cols != null && cols.Length > 0;
			}
			catch { return false; }
		}

		// ------------------------------------------------------------------
		// Gönderim
		// ------------------------------------------------------------------

		/// <summary>Kare başına bir kez çağrılır.</summary>
		public static void Update() {
			float now = Time.time;

			// EL ARAMASI BAĞLANTIDAN BAĞIMSIZ.
			//
			// Canlı log kanıtı: "held item: HAND NOT FOUND" hiç görünmedi,
			// çünkü eski sürüm ilk satırda `if (!HasRemote) return` diyordu.
			// Tek başına test ederken el hiç aranmıyordu. Arama 2 saniyede bir
			// ve sadece bir kez bulunca biter, maliyeti ihmal edilebilir.
			if (hand == null && now >= nextHandRetry) {
				nextHandRetry = now + HAND_RETRY;
				if (ResolveHand()) handWarned = false;
				else if (!handWarned) {
					handWarned = true;
					// v15: bu satirlar ana menude atiliyordu (21:25:22), dünya
			// 14 saniye sonra yukleniyor. Yani "bulunamadi" degil, henuz
			// aranacak bir sey yok. Gercekten basarisiz olursa uyari
			// seviyesinde kalsin.
			SmpLog.Log("[held item] el zinciri henuz aranmadı (dünya yüklenmedi). "
						+ "Held items will not sync; run 'smp player' and send that line.");
				}
			}
			if (hand == null) return;

			// Alıcı taraf: gelen konuma yumuşaklaştırmalı takip.
			FollowRemote();

			// TESPİT, GÖNDERİMDEN ÖNCE: kullanıcı eline ne aldığını bağlantı
			// olmadan da görebilsin (test tek başına da yapılıyor).
			GameObject held = FindHeld();
			// TEŞHİS SAYACLARI: el altında bir şey bulunuyor mu? Canlı log
			// kanıtı (23:03): kullanıcı eşya aldı, [ACT] HAND yazılmadı;
			// tespit mi yoksa kayıt mı boş dönüyor ayrımı bu sayaçla yapılır.
			if (SmpTrace.HandPolls < 1000000) SmpTrace.HandPolls++;
			if (held != null && SmpTrace.HandFound < 1000000) SmpTrace.HandFound++;
			string path = held != null ? SafePath(held) : null;
			bool isHeld = held != null && !string.IsNullOrEmpty(path);

			// EYLEM GÜNLÜĞÜ: el doldu / boşaldı. Oyuncu eylemi olduğu için
			// karşı tarafın uyguladığı eşya buraya gelmez.
			if (isHeld != lastLoggedHeld
				|| !string.Equals(path, lastLoggedPath, StringComparison.Ordinal)) {
				SmpTrace.Hands++;
				SmpTrace.Act("HAND", isHeld ? "took" : "dropped",
					(isHeld ? path : (lastLoggedPath ?? "-"))
						+ "  hand='" + (hand != null ? hand.name : "-") + "'");
				lastLoggedHeld = isHeld;
				lastLoggedPath = path;
			}

			if (!SteamTransport.HasRemote) return;
			if (now < nextPoll) return;
			nextPoll = now + POLL_INTERVAL;

			bool changed = isHeld != lastSentHeld
				|| !string.Equals(path, lastSentPath, StringComparison.Ordinal);
			// Konum akışı: yalnızca tutarken, 10 Hz.
			bool poseDue = isHeld && now >= nextPose;
			// Kalp atışı: bayrak paketi kaybolursa el doluluk durumu kaybolmasın.
			bool beat = now >= nextBeat;

			if (!changed && !poseDue && !beat) return;

			if (poseDue) nextPose = now + POSE_INTERVAL;
			if (changed || beat) nextBeat = now + HEARTBEAT;

			lastSentHeld = isHeld;
			lastSentPath = path;

			// Değişim ve kalp atışı güvenilir; konum güvensiz (kaybolması
			// sorun değil, 100 ms sonra yeni paket geliyor).
			Send(path, held, changed || beat);
		}

		static void Send(string path, GameObject held, bool reliable) {
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.HeldItem);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(held != null);
				w.Write(path ?? "");
				Vector3 p = Vector3.zero;
				Quaternion q = Quaternion.identity;
				if (held != null) {
					Transform t = held.transform;
					p = t.position;
					q = t.rotation;
				}
				w.Write(p.x); w.Write(p.y); w.Write(p.z);
				w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
				w.Flush();
				bool ok = reliable
					? SteamTransport.SendReliable(Protocol.Finish(s))
					: SteamTransport.SendUnreliable(Protocol.Finish(s));
				if (ok) Sent++;
			}
			catch (Exception e) {
				SmpLog.Warning("held item send failed: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Uygulama
		// ------------------------------------------------------------------

		/// <summary>Aynı eksik yol için yalnızca bir kez uyar.</summary>
		static System.Collections.Generic.HashSet<string> missingPaths
			= new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

		static bool WarnedMissing(string path) {
			return missingPaths.Contains(path);
		}

		static void NoteMissing(string path) {
			if (string.IsNullOrEmpty(path)) return;
			if (missingPaths.Contains(path)) return;
			missingPaths.Add(path);
		}

		/// <summary>Karşı tarafın tutma kaydını uygular.</summary>
		/// <summary>Peer'in tuttugu esyanin bizdeki konumu (kol uzatma icin).</summary>
				/// <summary>Uzaktan gelen son held paketinin zamani (kol bayatlik kontrolu).</summary>
		public static float LastRemoteMsg = -999f;

		public static bool TryGetRemoteHeldPos(out Vector3 p) {
			p = Vector3.zero;
			try {
				if (!remoteHeld || remoteItem == null) return false;
				if (targetValid) { p = targetPos; return true; }
				p = remoteItem.transform.position;
				return true;
			}
			catch { return false; }
		}

		public static void OnMessage(BinaryReader r) {
			try {
				bool held = r.ReadBoolean();
				string path = r.ReadString();
				Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				Apply(held, path, p, q);
			try { LastRemoteMsg = Time.time; } catch { }
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("held item packet rejected: " + e.Message);
			}
		}

		/// <summary>Peer'in tuttugu esyanin yolu (diger hatlar sorsun).</summary>
		static string remotePath;

		/// <summary>Bu yolu peer tutuyor mu (bu hat uzerinden)?</summary>
		public static bool IsRemotePath(string path) {
			try {
				return remoteHeld && remoteItem != null && !string.IsNullOrEmpty(path)
					&& string.Equals(remotePath, path, StringComparison.Ordinal);
			}
			catch { return false; }
		}

		/// <summary>Peer biraktiysa bu hattaki takibi de birak.</summary>
		public static void ReleasePath(string path) {
			try {
				if (remoteHeld && !string.IsNullOrEmpty(path)
					&& string.Equals(remotePath, path, StringComparison.Ordinal))
					ReleaseRemote();
			}
			catch { }
		}

		static void Apply(bool held, string path, Vector3 p, Quaternion q) {
			if (!held) {
				if (remoteHeld) {
					ReleaseRemote();
					// Diger hatlar da biraksin (cift surucu savasi bitar).
					try {
						if (InteractionManager.Carry != null)
							InteractionManager.Carry.ReleaseRemoteIf(path);
					} catch { }
					try { GarageEntitySync.ForgetRemote(path); } catch { }
					SmpLog.Log("peer let go of their item.");
				}
				targetValid = false;
				return;
			}
			// CIFT ALMA: benim elimdeki esyayi karsi hat calamaz.
			// (CarrySync ile ayni kural.)
			try {
				if (InteractionManager.Carry != null
					&& InteractionManager.Carry.IsCarryingPath(path))
					return;
			} catch { }
			if (string.IsNullOrEmpty(path)) return;

			GameObject item = FindByPath(path);
			if (item == null) {
				Unresolved++;
				NoteMissing(path);
				// NEDEN GÖRÜNÜR HALE GELDİ: bu satır eski sessizce artıyordu,
				// yani "karşıda görünmüyor" belirtisinin taşıyıcı logu YOKTU.
				// Oysa iki ayrı sebep var ve ikisi de farklı çözüm ister:
				//   (a) eşya bu kayıtta HİÇ yok  → aynı kaydı oynamak lazım
				//   (b) eşya var ama yol farklı  → ItemSync yol tablosu
				// Aynı yol için bir kez uyar, sonra sessiz devam et.
				if (!WarnedMissing(path)) {
					SmpLog.Warning("held item: peer holds '" + path
						+ "' but this save has no object at that path — it will NOT appear"
						+ " (different save? run 'smp items' to compare). unresolved="
						+ Unresolved);
				}
				return;
			}

			targetPos = p;
			targetRot = q;
			targetValid = true;

			if (remoteItem != item) {
				ReleaseRemote();
				remoteItem = item;
				remoteHeld = true;
				remotePath = path;
				ForceVisible(item, true);
				SmpLog.Log("peer is holding '" + path + "'.");
			}
			Applied++;
		}

		/// <summary>
		/// Gelen dünya konumuna takıl. Enterpolasyon kare hızından bağımsız:
		/// sabit hız adımı yerine üstel yumuşatma, çünkü paketler 10 Hz gelir
		/// ve ekran 144 Hz olabilir.
		/// </summary>
		static void FollowRemote() {
			if (!targetValid || remoteItem == null) return;
			float dt = Time.deltaTime;
			if (dt <= 0f) return;
			float k = 1f - Mathf.Exp(-FOLLOW_RATE * dt);
			Transform t;
			try { t = remoteItem.transform; } catch { remoteItem = null; return; }
			// ECHOGUARD EKSIKTI: bu yazim yankisizdi, oyunun FSM'i geri okuyup
			// yeni olay uretebiliyordu (CarrySync hattinda vardi, burada yoktu).
			var scope = EchoGuard.Enter();
			try {
				t.position = Vector3.Lerp(t.position, targetPos, k);
				t.rotation = Quaternion.Slerp(t.rotation, targetRot, k);
			}
			finally { scope.Dispose(); }
		}

		/// <summary>Eşyayı bırak: fiziğini iade et.</summary>
		static void ReleaseRemote() {
			GameObject item = remoteItem;
			remoteItem = null;
			remoteHeld = false;
			remotePath = null;
			if (item == null) return;
			try { ForceVisible(item, false); }
			catch { }
		}

		/// <summary>
		/// Uzaktaki eşya: görünür ama fizik kapalı. Elden düşünce geri açılır.
		/// </summary>
		static void ForceVisible(GameObject item, bool held) {
			try {
				if (held && !item.activeSelf) item.SetActive(true);
				Rigidbody rb = item.GetComponent<Rigidbody>();
				if (rb != null) {
					rb.isKinematic = held;
					rb.detectCollisions = !held;
					if (held) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
				}
				if (held) {
					Collider[] cols = item.GetComponentsInChildren<Collider>();
					for (int i = 0; i < cols.Length; i++) {
						if (cols[i] != null) cols[i].enabled = false;
					}
				}
				else {
					// BIRAKMADA IADE EKSIKTI: collider'lar kapali kaliyor (esya
					// zeminden DUSUYOR), useGravity'ye dokunulmuyordu (havada
					// asili kalma). CarrySync.ReleaseRemote ile ayni iade.
					Collider[] cols = item.GetComponentsInChildren<Collider>();
					for (int i = 0; i < cols.Length; i++) {
						if (cols[i] != null) cols[i].enabled = true;
					}
					if (rb != null) rb.useGravity = true;
				}
				Renderer[] rends = item.GetComponentsInChildren<Renderer>();
				for (int i = 0; i < rends.Length; i++) {
					if (rends[i] == null) continue;
					rends[i].enabled = held ? true : rends[i].enabled;
				}
			}
			catch { }
		}

		/// <summary>
		/// Sahne kökünden göreli yoldan nesne bulur (ItemSync ile aynı kural).
		///
		/// ÖNBELLEK ZORUNLU: SceneDump.AllSceneRoots() Resources
		/// .FindObjectsOfTypeAll&lt;Transform&gt;() çağırır, yani TÜM sahne
		/// transform'larını tarayıp yeni dizi kurar. Konum akışı 10 Hz'de
		/// geldiği için bunu her paket için yapmak "oyun kasıyor" şikayetinin
		/// ikinci kaynağıydı. Aynı yol için tekrar çözüm yapılmıyor.
		/// </summary>
		static GameObject FindByPath(string path) {
			if (string.IsNullOrEmpty(path)) return null;
			if (string.Equals(path, cachedPath, StringComparison.Ordinal)) {
				if (cachedObject == null) cachedPath = null;   // nesne yok oldu
				else return cachedObject;
			}

			GameObject found = ResolveByPath(path);
			cachedPath = found != null ? path : null;
			cachedObject = found;
			return found;
		}

		static string cachedPath;
		static GameObject cachedObject;

		static GameObject ResolveByPath(string path) {
			string rootName, rest;
			SceneDump.SplitRoot(path, out rootName, out rest);

			GameObject[] roots;
			try { roots = SceneDump.AllSceneRoots(); }
			catch { return null; }
			if (roots == null) return null;
			for (int i = 0; i < roots.Length; i++) {
				GameObject r = roots[i];
				if (r == null) continue;
				if (!string.Equals(r.name, rootName, StringComparison.Ordinal)) continue;
				if (string.IsNullOrEmpty(rest)) return r;
				Transform t = null;
				try { t = r.transform.Find(rest); } catch { continue; }
				if (t == null) continue;
				return t.gameObject;
			}
			string leaf = path;
			int cut = path.LastIndexOf('/');
			if (cut >= 0 && cut + 1 < path.Length) leaf = path.Substring(cut + 1);
			if (!string.IsNullOrEmpty(leaf)) {
				float now = 0f;
				try { now = Time.time; } catch { }
				if (now >= nextNameFallback) {
					nextNameFallback = now + 3f;
					for (int i = 0; i < roots.Length; i++) {
						GameObject r2 = roots[i];
						if (r2 == null) continue;
						Transform f2 = null;
						try { f2 = FindByName(r2.transform, leaf); } catch { continue; }
						if (f2 == null) continue;
						if (Telemetry.Verbose) if (Telemetry.Verbose) if (Telemetry.Verbose) SmpLog.Log("held item: peer holds '" + path
							+ "' exact path missing, using same-named '" + SafePath(f2.gameObject) + "'.");
						return f2.gameObject;
					}
				}
			}
			return null;
		}

		static float nextNameFallback;

		static Transform FindByName(Transform t, string leaf) {
			if (t == null) return null;
			int n;
			try { n = t.childCount; } catch { return null; }
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				if (string.Equals(c.name, leaf, StringComparison.Ordinal) && HasRenderer(c)) return c;
			}
			for (int i = 0; i < n; i++) {
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				Transform f = FindByName(c, leaf);
				if (f != null) return f;
			}
			return null;
		}
	}
}
