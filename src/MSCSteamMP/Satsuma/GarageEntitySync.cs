using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// GAR AJDAKİ BOŞ PARÇA / EŞYA SENKRONİZASYONU + AĞ YETKİSİ (AUTHORITY).
	///
	/// Belirti: garajda yerde duran Satsuma parçaları ve aletler iki oyuncuda
	/// farklı yerde duruyor; biri alıp taşıdığında diğeri eski yerinde
	/// kalıyor.
	///
	/// YETKİ MODELİ
	/// -----------
	/// İki oyunculu oyunda nesne üzerinde "sahibi" vardır:
	///   * Hiç kimse tutmuyorsa nesne SERBESTtir (isKinematic = true,
	///     yerçekimi kapalı değil ama kimse itmiyor) ve İÇİN BULUNAN
	///     taraf gönderir.
	///   * Bir oyuncu ALDIĞI AN yetki ona geçer: isKinematic = false,
	///     yerçekimi açılır, o taraf gönderir.
	///   * Bıraktığında yetki karşı tarafa döner.
	/// Tek sahibi olmayan nesneler için "içinde olan kazanır" kuralı,
	/// iki kişilik oturumda çakışma üretmez.
	///
	/// dnSpy / runtime bulgusu
	/// ---------------------
	/// Parça ve eşya sistemi C# değil: hepsi PlayMaker FSM (Assembly-CSharp'te
	/// "Assembly", "Pickup", "Item" dizgileri YOK). Bu yüzden "Pick up"
	/// olayını isimle dinlemek kırılgan; bunun yerine yetkiyi FİZİKTEN
	/// türetiyoruz: Rigidbody.isKinematic == false olan nesne tutuluyor
	/// demektir. Bu tek satırlık test her build'de doğru çalışır.
	///
	/// İNTERPOLASYON
	/// --------------
	/// Ağ 20 Hz, ekran 60-144 Hz. Aradaki farkı RemotePlayerBehaviour ile
	/// aynı desen kapatıyor: hedefe doğru kare süresinden BAĞIMSIZ ilerleme
	/// (MaxFollowSpeed * dt) + üstel rotasyon yumuşatma (1 - e^(-k·dt)).
	/// Düz Vector3.Lerp(a,b,0.1f) kullanmıyoruz çünkü 144 Hz'de çok hızlı,
	/// 30 Hz'de çok yavaş çalışır (kare hızına bağlı).
	///
	/// GC-free: her nesne için tek bir referans; gönderim tek pakette
	/// birleştirilir; kare başına yeni tahsis yok (List/Dictionary
	/// önceden ayrılmıştır).
	///
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class GarageEntitySync {

		// ------------------------------------------------------------------
		// Ayarlar
		// ------------------------------------------------------------------

		/// <summary>Gönderim aralığı (saniye). 20 Hz.</summary>
		public const float SEND_INTERVAL = 0.05f;

		/// <summary>Paket başına azami nesne sayısı (bant genişliği koruması).</summary>
		public const int MAX_PER_PACKET = 20;

		/// <summary>
		/// Hareket eşiği (metre). Bundan az değişim gönderilmez: altı kademeli
		/// bir aletin titreşimi ağa paket üretmesin.
		/// </summary>
		public const float MOVE_EPSILON = 0.02f;

		/// <summary>Rotasyon eşiği (derece).</summary>
		public const float ROT_EPSILON = 1.5f;

		/// <summary>Hedefe yaklaşma hızı üst sınırı (m/s).</summary>
		public const float MAX_FOLLOW_SPEED = 14f;

		/// <summary>
		/// Pozisyon üstel yumuşatma katsayısı (1/s). Rotasyonla aynı desen.
		///
		/// NEDEN BU KATSAYI: eski yol sabit hızla hedefe yürüyordu
		/// (MAX_FOLLOW_SPEED * dt) ve HEDEFE VARINCA ANIDEN DURUYORDU. Üstel
		/// yumuşatma sona erişken yumuşak iniş veriyor; hız sabit olduğu için
		/// "yürüyerek durmak" hissi bittiği için nesne hedefin 10 cm'i
		/// üzerinde asılı kalıp son karede zıplıyordu.
		///
		/// ÖNEMLİ: saf üstel yumuşatma TEK BAŞINA kullanılmıyor. Hızlı
		/// hareket eden hedefte nesne kalıcı olarak geride kalır ve
		/// TELEPORT_DISTANCE'ı tetikleyip sürekli ışınlanma üretir. Bu
		/// yüzden aşağıda üstel yumuşatma HIZ SINIRI ile birlikte
		/// uygulanıyor: yumuşatma şekli verir, sınır güvenliği korur.
		/// </summary>
		public const float POSITION_SHARPNESS = 14f;

		/// <summary>Bu mesafeden büyük sıçrama teleport sayılır.</summary>
		/// <summary>
		/// KAPI MENTEŞESİNE SON YAZAN KİMDİR?
		///
		/// CANLI ÖLÇÜM (13:39 client, zaman serisi):
		///     0.2s:10, 0.4s:23, 0.6s:34, sonra 0.0 × 15
		/// Kapı düzgünce açılıyor (0.6 sn'de 34 derece) ve sonra BİR KAREDE
		/// sıfıra düşüyor. Animasyon değil: kapı yavaş kapanmaz, anında
		/// sıfırlanır. Bu, dışarıdan bir `rotation` yazımıdır.
		///
		/// Sıfır çıkarsa bu mod kapıya dokunmuyor demektir ve sorun
		/// oyunun kendi FSM mantığındadır (`Test cops`).
		/// </summary>
		public static int DoorWrites;
		public static string LastDoorWriter = "(yok)";

		/// <summary>Kapı dışındaki bir nesneye yazıldı mı? (sadece teşhis)</summary>
		static bool IsDoorTransform(Transform t) {
			if (t == null) return false;
			try { return WorldObjectSync.IsTrackedDoor(t.gameObject); }
			catch { return false; }
		}

public const float TELEPORT_DISTANCE = 4f;

		/// <summary>Rotasyon yumuşatma katsayısı (1/s).</summary>
		public const float ROTATION_SHARPNESS = 12f;

		/// <summary>Keşifte en fazla izlenecek nesne (garajda 200+ nesne olabiliyor).</summary>
		public const int MAX_TRACKED = 120;

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		/// <summary>İzlenen bir nesne.</summary>
		public class Entry {
			public Rigidbody Body;
			public Transform Tr;
			/// <summary>Sahne kökünden göreli yol = kimlik (ağ üstündeki ad).</summary>
			public string Path;

			// Yerel gönderim durumu
			public Vector3 LastSentPos;
			public Quaternion LastSentRot;
			public bool LastKinematic;
			public bool HasSent;

			// Karşı tarafta: hedef + interpolasyon durumu
			public bool RemoteOwned;        // karşı taraf tutuyor
			public Vector3 TargetPos;
			public Quaternion TargetRot;
			public bool HasTarget;

			/// <summary>Son hareket zamanı (telemetri için).</summary>
			public float LastMoveAt;
		}

		static readonly Dictionary<string, Entry> byPath = new Dictionary<string, Entry>(StringComparer.Ordinal);
		static readonly List<Entry> ordered = new List<Entry>(MAX_TRACKED);

		/// <summary>Ben bu esyayi elimde tutuyor muyum? (carry hatti bilir)</summary>
		static bool SelfCarrying(string path) {
			try {
				if (string.IsNullOrEmpty(path)) return false;
				if (InteractionManager.Carry != null
					&& InteractionManager.Carry.IsCarryingPath(path)) return true;
			} catch { }
			return false;
		}

		/// <summary>Peer bu esyayi tutuyor mu (carry/held hatti bilir)?</summary>
		static bool PeerCarrying(string path) {
			try {
				if (string.IsNullOrEmpty(path)) return false;
				if (InteractionManager.Carry != null
					&& InteractionManager.Carry.IsRemotePath(path)) return true;
				if (HeldItemSync.IsRemotePath(path)) return true;
			} catch { }
			return false;
		}

		/// <summary>
		/// Birakma baska hattan geldi: bu hattaki uzaktan hedefi unut, fizige
		/// karisma (carry hatti birakma noktasina isinlayip fizige verdi).
		/// </summary>
		public static void ForgetRemote(string path) {
			try {
				if (string.IsNullOrEmpty(path)) return;
				Entry e;
				if (!byPath.TryGetValue(path, out e) || e == null) return;
				e.RemoteOwned = false;
				e.HasTarget = false;
				e.TargetPos = Vector3.zero;
			} catch { }
		}

		// Gönderim tamponu (kare başına ayırma yok)
		static readonly List<Entry> outbox = new List<Entry>(MAX_PER_PACKET);

		// Keşif tamponları: yeniden kullanılır, keşif arka arkaya çalışsa
		// bile kare başına yeni tahsis olmaz.
		static readonly List<Rigidbody> candidates = new List<Rigidbody>(256);
		static readonly List<float> dist = new List<float>(256);

		static float nextSend;
		static bool discoverDone;

		// ------------------------------------------------------------------
		// Telemetri
		// ------------------------------------------------------------------

		public static int TrackedCount { get { return ordered.Count; } }
		public static int SentUpdates;
		public static int AppliedUpdates;
		public static int UnresolvedEntities;

		// ------------------------------------------------------------------
		// Keşif
		// ------------------------------------------------------------------

		/// <summary>
		/// Garajdaki hareket edebilir nesneleri bulur.
		///
		/// Ölçüt: Rigidbody taşıyan nesne. Satsuma parçaları, aletler, eşyalar
		/// Rigidbody ile düşer; duvar/zemin taşımaz. Yani "Rigidbody var" =
		/// "taşınabilir nesne" — oyun sahnede böyle işaretliyor.
		///
		/// Tracked sınırı: 218 parça + 137 cıvata + aletler olabiliyor. Hepsini
		/// 20 Hz göndermek ağa boğucu çıkarır, bu yüzden en yakın N tanesi
		/// izlenir (oyuncunun ilgilendiği yerdeki nesneler).
		/// </summary>
		public static void Discover() {
			Clear();

			GameObject[] roots = SceneDump.AllSceneRoots();
			if (roots == null) return;

			// Adayları topla (Rigidbody olanlar).
			//
			// PERFORMANS: log kanıtı "tracked 120 rigidbody object(s) from
			// 2028 candidate(s)". 2028 adayı yükleme anında mesafeye göre
			// sıralamak, karşılaştırma başına ~11 kez transform.position
			// okuması demek (22 bin native çağrı) ve yükleme sıçraması
			// yaratıyordu. Artık mesafe TEK KEZ hesaplanıp saklanıyor,
			// sıralama yalnızca float karşılaştırıyor.
			candidates.Clear();
			dist.Clear();
			int skippedDoors = 0;
			Vector3 me = LocalPlayerPosition();
			for (int r = 0; r < roots.Length; r++) {
				if (roots[r] == null) continue;
				Rigidbody[] bodies = roots[r].GetComponentsInChildren<Rigidbody>(true);
				for (int i = 0; i < bodies.Length; i++) {
					if (bodies[i] == null) continue;
					if (IsSkyboxShape(bodies[i])) continue;   // post-effect kutusu vs
					if (IsPlayerOwned(bodies[i])) continue;    // el/kol/kamera ASLA
					// KAPILAR DIŞARI. CANLI KANIT: "kapı yerinden kopuyor,
					// hayosiko kapısı geriden geliyor".
					// Garaj senkronu "Rigidbody olan her şeyi" izliyordu ve
					// kapılar da dahildi; aynı transform'u hem bu sistem
					// (20 Hz uzaktan konum) hem de WorldObjectSync'in FSM
					// animasyonu yazıyordu. Kapı taşınabilir bir eşya değil,
					// bir FSM olayıdır: tek sahibi olay yolu olmalı.
					if (WorldObjectSync.IsTrackedDoor(bodies[i].gameObject)) {
						skippedDoors++;
						continue;
					}
					candidates.Add(bodies[i]);
					dist.Add(SquaredDistanceTo(bodies[i], me));
				}
			}

			// Yakınlık sırası: en yakın MAX_TRACKED tane.
			int total = candidates.Count;
			if (total > MAX_TRACKED) {
				SortByDistance();
				candidates.RemoveRange(MAX_TRACKED, total - MAX_TRACKED);
				dist.RemoveRange(MAX_TRACKED, total - MAX_TRACKED);
			}

			for (int i = 0; i < candidates.Count && ordered.Count < MAX_TRACKED; i++) {
				Register(candidates[i]);
			}

			discoverDone = true;
			SmpLog.Log("garage entities: tracked " + ordered.Count + " rigidbody object(s) from "
				+ total + " candidate(s)"
				+ (skippedDoors > 0 ? "; " + skippedDoors + " door rigidbody/bodies skipped "
					+ "(doors are driven by the FSM event path, not by transform streaming)"
					: "") + ".");
		}

		/// <summary>candidates/dist çiftini mesafeye göre sıralar (önbellekli).</summary>
		static void SortByDistance() {
			// Kayma sıralaması yerine: her elemanı kendi yerine oturtan tek
			// geçiş + tekrarlama. 2028 eleman için bile <1 ms; ölçülebilir bir
			// kazanç için karmaşık bir quickselect yazmaya değmez.
			for (int i = 1; i < candidates.Count; i++) {
				Rigidbody b = candidates[i];
				float d = dist[i];
				int j = i - 1;
				while (j >= 0 && dist[j] > d) {
					candidates[j + 1] = candidates[j];
					dist[j + 1] = dist[j];
					j--;
				}
				candidates[j + 1] = b;
				dist[j + 1] = d;
			}
		}

		static void Register(Rigidbody body) {
			if (body == null || body.gameObject == null) return;
			Transform t;
			try { t = body.transform; } catch (Exception ex) { if (Telemetry.Verbose) SmpLog.Warning("transform access failed: " + ex.Message); return; }
			string path = SceneDump.PathOf(t);
			if (string.IsNullOrEmpty(path)) return;
			if (byPath.ContainsKey(path)) return;

			Entry e = new Entry();
			e.Body = body;
			e.Tr = t;
			e.Path = path;
			byPath[path] = e;
			ordered.Add(e);
		}

		/// <summary>
		/// Alma/tutma anında geç bağlama: keşifte yoktu (uzaktaydı ya da
		/// sonradan doğdu), ama oyuncu eline aldı. Konum akışı için tabloya
		/// alınır; yoksa peer eşyayı elinde göremez (canlı kanıt).
		/// Kap aşımına +16 tolerans: tutulan eşya uzak hurdadan önemlidir.
		/// </summary>
		public static bool EnsureTracked(GameObject go) {
			if (go == null) return false;
			// OYUNCUNUN KENDİ HİYERARŞİSİ ASLA TAKİP EDİLMEZ.
			//
			// Canlı log kanıtı: "late-tracked 'PLAYER/Pivot/AnimPivot/Camera/
			// FPSCamera/1Hand_Assemble/Hand'". El takımının Rigidbody'si var ve
			// alma olayı elin kendi FSM'inden geldiği için geç bağlama onu
			// aldı. Sonuç: karşı taraf bu yolu Aynen kendi elinde de bulur ve
			// eliMİZİ onun konumuna çekiyordu — hem tutulan eşya görünmüyor
			// hem de yerel oyuncunun eli bozuluyordu.
			if (IsPlayerOwned(go)) return false;
			Rigidbody rb = null;
			try { rb = go.GetComponent<Rigidbody>(); } catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("GetComponent<Rigidbody> failed: " + e.Message); return false; }
			if (rb == null) return false;
			Transform t = null;
			try { t = go.transform; } catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("go.transform failed: " + e.Message); return false; }
			if (t == null) return false;
			string path = null;
			try { path = SceneDump.PathOf(t); } catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("SceneDump.PathOf failed: " + e.Message); return false; }
			if (string.IsNullOrEmpty(path) || byPath.ContainsKey(path)) {
				return !string.IsNullOrEmpty(path) && byPath.ContainsKey(path);
			}
			if (ordered.Count >= MAX_TRACKED + 16) return false;
			Register(rb);
			if (byPath.ContainsKey(path)) {
				SmpLog.Log("late-tracked '" + path + "' (grabbed after discovery).");
				return true;
			}
			return false;
		}

		/// <summary>
		/// Yola göre geç bağlama (peer'in olayı çözülemediğinde).
		/// Transform.Find köke göre arar; ilk kesit kök adıdır.
		/// </summary>
		public static bool EnsureTrackedByPath(string path) {
			if (string.IsNullOrEmpty(path)) return false;
			if (byPath.ContainsKey(path)) return true;
			string rootName, rest;
			SceneDump.SplitRoot(path, out rootName, out rest);
			if (string.IsNullOrEmpty(rest)) return false;
			GameObject[] roots = null;
			try { roots = SceneDump.AllSceneRoots(); } catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("AllSceneRoots failed: " + e.Message); return false; }
			if (roots == null) return false;
			for (int i = 0; i < roots.Length; i++) {
				GameObject r = roots[i];
				if (r == null) continue;
				bool same = false;
				try { same = string.Equals(r.name, rootName, StringComparison.Ordinal); }
				catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("string.Equals failed: " + e.Message); continue; }
				if (!same) continue;
				Transform found = null;
				try { found = r.transform.Find(rest); }
				catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("transform.Find failed: " + e.Message); continue; }
				if (found == null) continue;
				GameObject go = null;
				try { go = found.gameObject; }
				catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("found.gameObject failed: " + e.Message); continue; }
				if (EnsureTracked(go)) return true;
			}
			return false;
		}

		/// <summary>
		/// Ölü gövdeyi tazele: kapma anında oyun Rigidbody'yi söküp yeniden
		/// takabilir; eski referansla gönderim sonsuza dek dururdu.
		/// </summary>
		static void RefreshBody(Entry e) {
			if (e == null || e.Body != null || e.Tr == null) return;
			try { e.Body = e.Tr.GetComponent<Rigidbody>(); }
			catch (Exception ex) { if (Telemetry.Verbose) SmpLog.Warning("RefreshBody GetComponent failed: " + ex.Message); e.Body = null; }
		}

		static bool IsSkyboxShape(Rigidbody body) {
			// Oyun post-effect için Rigidbody'li kutu nesneleri koyuyor; onlar
			// taşınabilir değil. Boyutları çok büyükse ele.
			Collider c = null;
			try { c = body.GetComponent<Collider>(); }
			catch (Exception ex) { if (Telemetry.Verbose) SmpLog.Warning("GetComponent<Collider> failed: " + ex.Message); }
			if (c == null) return true;
			Bounds b = c.bounds;
			if (b.size.x > 12f && b.size.y > 12f && b.size.z > 12f) return true;
			return false;
		}

		static float DistanceTo(Rigidbody body, Vector3 p) {
			try { return Vector3.Distance(body.transform.position, p); }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("Vector3.Distance failed: " + e.Message); return float.MaxValue; }
		}

		/// <summary>
		/// Karesel mesafe. Sıralama için kare kök ALMAMIZ GEREKMEZ: aynı
		/// sıralamayı verir ama bir kare kök çağrısı tasarruf eder.
		/// </summary>
		static float SquaredDistanceTo(Rigidbody body, Vector3 p) {
			try { return (body.transform.position - p).sqrMagnitude; }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("sqrMagnitude failed: " + e.Message); return float.MaxValue; }
		}

		static Vector3 LocalPlayerPosition() {
			try {
				Vector3 p;
				if (PlayerMotion.TryGetPosition(out p)) return p;
				Camera cam = Camera.main != null ? Camera.main : Camera.current;
				if (cam != null) return cam.transform.position;
				return Vector3.zero;
			}
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("LocalPlayerPosition failed: " + e.Message); return Vector3.zero; }
		}

		/// <summary>
		/// Nesne yerel oyuncunun hiyerarşisinde mi? ( el, kol, kamera, kukla )
		///
		/// Canlı log kanıtı: takip tablosuna
		/// 'PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand' girdi.
		/// Bu yol karşı tarafta da BİREBİR AYNI olduğu için uzaktan gelen
		/// konum paketi yerel oyuncunun kendi elini kaydırıyordu.
		///
		/// Maliyet: en fazla 32 ebeveyn, yalnızca referans karşılaştırması.
		/// Kök referansları 1 saniyede bir tazelenir; sahne değişince
		/// geçersizleşen nesne null'a eşitlenir ve yenisi bulunur.
		/// </summary>
		static bool IsPlayerOwned(GameObject go) {
			if (go == null) return false;
			return IsPlayerTransform(TransformOf(go));
		}

		static Transform TransformOf(GameObject go) {
			try { return go != null ? go.transform : null; }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("TransformOf failed: " + e.Message); return null; }
		}

		static bool IsPlayerOwned(Rigidbody body) {
			return body != null && IsPlayerTransform(TransformOf(body.gameObject));
		}

		static bool IsPlayerTransform(Transform t) {
			if (t == null) return false;
			RefreshPlayerRoots();
			int guard = 0;
			while (t != null && guard++ < 32) {
				if (t.GetInstanceID() == playerRootId) return true;
				if (t.GetInstanceID() == cameraId) return true;
				t = t.parent;
			}
			return false;
		}

		static int playerRootId;
		static int cameraId;
		static float nextRootRefresh;

		static void RefreshPlayerRoots() {
			float now = Time.time;
			if (now < nextRootRefresh) return;
			nextRootRefresh = now + 1f;
			playerRootId = 0;
			cameraId = 0;
			try {
				GameObject root = GameObject.Find("PLAYER");
				if (root != null) playerRootId = root.GetInstanceID();
			}
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("GameObject.Find PLAYER failed: " + e.Message); }
			try {
				Camera cam = Camera.main != null ? Camera.main : Camera.current;
				if (cam != null) cameraId = cam.GetInstanceID();
			}
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("Camera.main find failed: " + e.Message); }
		}

		/// <summary>Dünya çıkışında temizle.</summary>
		public static void Clear() {
			playerRootId = 0;
			cameraId = 0;
			nextRootRefresh = 0f;
			byPath.Clear();
			ordered.Clear();
			outbox.Clear();
			discoverDone = false;
		}

		// ------------------------------------------------------------------
		// Kare başına
		// ------------------------------------------------------------------

		/// <summary>
		/// Gönderim + yerel interpolasyon. SteamMPBehaviour.Update içinden.
		/// </summary>
		public static void Tick() {
			try {
				if (!discoverDone) return;

				if (SteamTransport.HasRemote) {
					float now = Time.time;
					if (now >= nextSend) {
						nextSend = now + SEND_INTERVAL;
						CollectAndSend();
					}
				}
				InterpolateLocals();
			}
			catch (Exception e) {
				SmpLog.Warning("garage entity tick failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Gönderim
		// ------------------------------------------------------------------

		static void CollectAndSend() {
			outbox.Clear();
			if (!SteamTransport.HasRemote) return;

			for (int i = 0; i < ordered.Count && outbox.Count < MAX_PER_PACKET; i++) {
				Entry e = ordered[i];
				if (e == null || e.Tr == null) continue;
				RefreshBody(e);
				if (e.Body == null) continue;

				// TUTULAN ESYA BU HATTAN GITMEZ: carry hatti (10 Hz + kalp
				// atisi) konumun tek sahibidir. Cift gonderim, karsida cift
				// yazim demekti (ucma/titreme).
				if (SelfCarrying(e.Path)) continue;

				// YETKI (duzeltildi): `!isKinematic = tutuluyor` sezgisi TERS
				// calisiyordu. Birakilan esya dinamik DUSUYOR; eski kod onu
				// "tutuluyor" sanip karsida DONDURUYORDU -> havada asili kalma
				// ("biraktigimda havada asili kaliyor" belirtisi). Tutma
				// bilgisi carry hattindan gelir; serbest fizik yerelde yasanir.
				bool kinematic = e.Body.isKinematic;

				Vector3 p = e.Tr.position;
				Quaternion r = e.Tr.rotation;

				bool moved = !e.HasSent
					|| (p - e.LastSentPos).sqrMagnitude > MOVE_EPSILON * MOVE_EPSILON
					|| AngleBetween(e.LastSentRot, r) > ROT_EPSILON
					|| kinematic != e.LastKinematic;
				if (!moved) continue;

				e.LastSentPos = p;
				e.LastSentRot = r;
				e.LastKinematic = kinematic;
				e.HasSent = true;
				outbox.Add(e);
			}

			if (outbox.Count == 0) return;
			SendOutbox();
		}

		static void SendOutbox() {
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.EntityState);
				BinaryWriter w = new BinaryWriter(s);
				w.Write((byte)outbox.Count);
				for (int i = 0; i < outbox.Count; i++) {
					Entry e = outbox[i];
					w.Write(e.Path);
					w.Write(e.Tr.position.x);
					w.Write(e.Tr.position.y);
					w.Write(e.Tr.position.z);
					w.Write(e.Tr.rotation.x);
					w.Write(e.Tr.rotation.y);
					w.Write(e.Tr.rotation.z);
					w.Write(e.Tr.rotation.w);
					// Tutma biti carry hattindan: BEN tutuyorsam karsi
					// dondursun; degilse (dusen/bos esya) karsi yerelde
					// simule etsin. Eski `!isKinematic` dusen esyayi da
					// "tutuluyor" saniyordu.
					w.Write(SelfCarrying(e.Path));
				}
				w.Flush();

				// Konum sürekli değişen veri: noDelay. Bir kare gecikme
				// interpolatörde yumuşatılır; güveniliz kuyruk titretirdi.
				if (SteamTransport.SendUnreliableNoDelay(Protocol.Finish(s))) {
					SentUpdates += outbox.Count;
				}
			}
			catch (Exception e) {
				SmpLog.Warning("garage entity send failed: " + e.Message);
			}
			finally {
				outbox.Clear();
			}
		}

		// ------------------------------------------------------------------
		// Uygulama (karşı taraf)
		// ------------------------------------------------------------------

		/// <summary>Gelen nesne durumu paketini uygular.</summary>
		public static void OnMessage(BinaryReader r, byte count) {
			for (int i = 0; i < count; i++) {
				try {
					string path = r.ReadString();
					Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(),
													r.ReadSingle(), r.ReadSingle());
					bool peerHolding = r.ReadBoolean();

					Entry e;
					if (!byPath.TryGetValue(path, out e) || e == null || e.Body == null) {
						UnresolvedEntities++;
						continue;
					}

					// CARRY HATTI VARSA BU HAT KARIŞMAZ: ayni esyayi iki
					// hat yaziyordu (ucma/titreme). Carry/held izliyorsa bu
					// paket yalniz goz ardi edilir.
					if (PeerCarrying(path)) {
						e.RemoteOwned = false;
						e.HasTarget = false;
						continue;
					}

					e.RemoteOwned = peerHolding;
					e.TargetPos = p;
					e.TargetRot = q;
					e.HasTarget = true;
					e.LastMoveAt = Time.time;

					// YETKİ UYGULA: karşı taraf tutuyorsa yerelde
					// kinematik yap (yerçekimi çekmesin, biz de itmeyelim).
					// Tutmuyorsa kinematik bırak ki fizik kendi işini yapsın.
					if (peerHolding) {
						if (!e.Body.isKinematic) { e.Body.isKinematic = true; e.Body.useGravity = false; }
					}
					else {
						if (e.Body.isKinematic) {
							// Serbest bırak: karşı taraf bırakmış, biz de
							// fizik devralalım.
							e.Body.isKinematic = false;
							e.Body.useGravity = true;
						}
					}
					AppliedUpdates++;
				}
				catch (Exception) {
					Telemetry.BadPackets++;
					SmpLog.Warning("garage entity packet rejected.");
					return;
				}
			}
		}

		// ------------------------------------------------------------------
		// Yerel interpolasyon (karşı taraf elinde olan nesneler)
		// ------------------------------------------------------------------

		/// <summary>
		/// Karşı tarafta tutulan nesneleri hedefe doğru taşır.
		///
		/// Düz Lerp(a,b,0.1f) YOK: kare hızına bağlıdır (144 Hz'de çok
		/// hızlı, 30 Hz'de çok yavaş). Bunun yerine kare süresinden bağımsız
		/// ÜSTEL YUMUŞATMA (1 - exp(-k*dt)) kullanılır — pozisyon ve rotasyon
		/// aynı formülü paylaşır.
		///
		/// DÜZeltME NOTU: önceki sürümde pozisyon sabit hızla
		/// (MAX_FOLLOW_SPEED * dt) ilerliyordu. Bu da kare hızından bağımsız
		///ydı (yani "144 Hz sarsıntısı" bu koddan gelmiyordu); asıl sorun
		/// HEDEFA VARINCA ANIDEN DURMASINDI. Yukarıdaki hibrid (üstel şekil +
		/// hız sınırı) ikisini de çözer.
		/// </summary>
		static void InterpolateLocals() {
			float dt = Time.deltaTime;
			if (dt <= 0f) return;

			for (int i = 0; i < ordered.Count; i++) {
				Entry e = ordered[i];
				if (e == null || e.Tr == null) continue;
				RefreshBody(e);
				if (e.Body == null || !e.HasTarget) continue;

				// Karşı taraf bıraktıysa artık biz yönetiyoruz: hedefi unut,
				// fiziğe geri ver.
				if (!e.RemoteOwned) {
					if (!e.Body.isKinematic) { e.HasTarget = false; }
					else e.HasTarget = false;
					continue;
				}

				// Kinematik olmayan nesne yerelde tutuluyor demektir; karşı
				// taraf da tutuyorsa çakışma var, yerel fizik öncelikli.
				if (!e.Body.isKinematic) { e.HasTarget = false; continue; }

				Vector3 current = e.Tr.position;
				Vector3 toTarget = e.TargetPos - current;
				float dist = toTarget.magnitude;

				// KAPILARIN KORUNMASI: Kapı transformları asla GarageEntitySync tarafından
				// ezilmemelidir. Kapılar menteşeli (HingeJoint/FSM) nesnelerdir; fiziksel
				// konum senkronu ile zorlanırlarsa menteşelerinden koparlar ve titrerler.
				if (IsDoorTransform(e.Tr)) {
					DoorWrites++;
					LastDoorWriter = "skipped @ " + e.Tr.name;
					continue;
				}

				if (dist > TELEPORT_DISTANCE) {
					// Büyük sıçrama (başlangıç senkronu gibi): teleport.
					EchoGuard.Scope scope = EchoGuard.Enter();
					try {
						e.Tr.position = e.TargetPos;
						e.Tr.rotation = e.TargetRot;
					}
					finally { scope.Dispose(); }
					continue;
				}

				EchoGuard.Scope scope2 = EchoGuard.Enter();
				try {
					// ÜSTEL YUMUŞATMA (rotasyonla aynı formül):
					//     alpha = 1 - exp(-k * dt)
					// Kare süresinden BAĞIMSIZdır ve hedefe yumuşak iner.
					//
					// HIZ SINIRI korunur: saf üstel yumuşatma hızlı hedefte
					// nesneyi kalıcı geride bırakır ("takip eden gölge") ve
					// TELEPORT_DISTANCE'ı tetikleyip ışınlanma döngüsü
					// üretir. Sınır, üstel adımın taşıabileceği mesafeyi
					// MAX_FOLLOW_SPEED * dt ile kısar.
					float alpha = 1f - Mathf.Exp(-POSITION_SHARPNESS * dt);
					Vector3 next = Vector3.Lerp(current, e.TargetPos, alpha);
					Vector3 delta = next - current;
					float maxStep = MAX_FOLLOW_SPEED * dt;
					float sq = delta.sqrMagnitude;
					if (sq > maxStep * maxStep) {
						delta = delta * (maxStep / Mathf.Sqrt(sq));
					}
					if (delta.sqrMagnitude > 1e-10f) {
						e.Tr.position = current + delta;
					}
					e.Tr.rotation = Quaternion.Slerp(e.Tr.rotation, e.TargetRot,
						1f - Mathf.Exp(-ROTATION_SHARPNESS * dt));
				}
				finally { scope2.Dispose(); }
			}
		}

		/// <summary>İki rotasyon arasındaki açı farkı (derece).</summary>
		static float AngleBetween(Quaternion a, Quaternion b) {
			float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
			if (dot < 0f) dot = -dot;
			if (dot > 1f) dot = 1f;
			return (float)(2.0 * Math.Acos(dot)) * 57.29578f;
		}

		// ------------------------------------------------------------------
		// Tanı
		// ------------------------------------------------------------------

		public static string Describe() {
			if (!discoverDone) return "garage entities: not scanned";
			return "garage entities: " + ordered.Count + " tracked, sent " + SentUpdates
				+ ", applied " + AppliedUpdates
				+ ", unresolved " + UnresolvedEntities;
		}
	}
}
