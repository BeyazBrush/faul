using System;
using System.Collections.Generic;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// SATSUMA PARÇA POZİSYON SENKRONİZASYONU.
	///
	/// BELİRTİ (kullanıcı): "Satsuma parçaları araçta takılı değil,
	/// sadece garajda duruyor." Yani montaj iki tarafta da görünüyor ama
	/// parça client'ta ARAÇTA DEĞİL, eski yerinde (garajda) duruyor.
	///
	/// KÖK NEDEN: mevcut sistem yalnızca iki şey gönderiyordu —
	///   1) "Installed" bool (parça takılı mı)
	///   2) "ASSEMBLE" / "REMOVE" FSM olayı
	/// Host'ta Assembly FSM'i parçayı ARAÇTA konumlandırıyor (SetParent +
	/// yer değiştirme). Client'ta aynı FSM çalıştırıldığında bile, client'ın
	/// kendi fizik/transform durumu farklı olduğu için parça hedefe oturmuyor;
	/// üstelik FSM çalışmadan önce 1 kare eski konumda görünüyor.
	///
	/// ÇÖZÜM: parçaların DÜNYA TRANSFORM'U (pozisyon + rotasyon) açıkça
	/// senkronize edilir. Yani:
	///   * Client bağlandığında: host her parçanın transform'unu bir kez
	///     gönderir (initial sync), client parçaları AYNILAR.
	///   * Parça taşındığında (takıldı/söküldü/elde tutuldu): host yeni
	///     transform'u gönderir, client parçayı oraya taşır.
	///   * Elde tutulan parça: eldeki oyuncu görünür, karşı tarafta da
	///     aynı oyuncunun elinde görünür (oyuncu transform'u zaten 20 Hz
	///     gidiyor; parça yerel transform'u takip eder).
	///
	/// neden ayrı modül: SatsumaRegistry tabloyu ve bool/olay ağını yönetiyor;
	/// bu modül sadece TRANSFORM'u taşıyor ve tek bir paket tipi kullanıyor.
	///
	/// dnSpy notu: parça sistemi C# değil, PlayMaker FSM (Assembly-CSharp'te
	/// "Assembly" dizgisi yok). Bu yüzden doğru yer burada değil, senkron
	/// katmanıdır: FSM'i dinle + dünya transform'unu kopyala.
	///
	/// Unity 5 / .NET 3.5 uyumlu; hiçbir istisna dışarı sızmaz.
	/// </summary>
	public static class SatsumaPartTransform {

		// ------------------------------------------------------------------
		// Paket ayarları
		// ------------------------------------------------------------------

		/// <summary>Transform gönderim aralığı (saniye). 20 Hz yeterli.</summary>
		public const float SEND_INTERVAL = 0.05f;

		/// <summary>Flush başına parça sayısı (bant genişliği koruması).</summary>
		public const int MAX_PARTS_PER_PACKET = 24;

		/// <summary>
		/// Transform uygulama eşiği. FSM'in kendi yerleştirmesi 0.05 m'den
		/// küçük farkı "zaten yerinde" sayar; biz de aynı ölçüyü kullanıyoruz
		/// ki sürekli küçük kaymalar ağa gitmesin.
		/// </summary>
		public const float POS_EPSILON = 0.02f;
		/// <summary>Rotasyon farkı eşiği (derece cinsinden karekökü tahmini).</summary>
		public const float ROT_EPSILON = 0.01f;

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		/// <summary>Son gönderilen transform (kıyaslama için). NaN = hiç gönderilmedi.</summary>
		class Sent {
			public Vector3 Pos;
			public Quaternion Rot;
			public bool Has;
		}

		static readonly Dictionary<string, Sent> lastSent = new Dictionary<string, Sent>(StringComparer.Ordinal);
		static readonly List<string> dirtyOrder = new List<string>(64);
		static readonly HashSet<string> dirty = new HashSet<string>(StringComparer.Ordinal);

		static float nextSend;
		static bool transformSyncDone;

		/// <summary>Başlangıç senkronu bitti mi? (tüm parçalar bir kez gönderildi)</summary>
		public static bool InitialSyncDone { get { return transformSyncDone; } }

		/// <summary>Şu an kaç parça gönderim bekliyor?</summary>
		public static int PendingCount { get { return dirty.Count; } }

		/// <summary>Telemetri: gönderilen ve uygulanan transform sayısı.</summary>
		public static int SentTransforms;
		public static int AppliedTransforms;
		/// <summary>Eksik/hatalı parça kimliği için atlanan paketler.</summary>
		public static int UnresolvedParts;

		// ------------------------------------------------------------------
		// Yakalama: parça hareket etti mi?
		// ------------------------------------------------------------------

		/// <summary>
		/// Kare başına bir kez çağrılır. Parçaların transform'unu izler;
		/// değişenleri işaretler ve işaretlenenleri gönderir.
		/// </summary>
		public static void Tick() {
			try {
				if (SatsumaRegistry.PartCount == 0) return;
				if (!SteamTransport.HasRemote) return;

				float now = Time.time;
				if (now < nextSend) return;
				nextSend = now + SEND_INTERVAL;

				// TARAMA GÖNDERİM HIZINDA, HER KARE DEĞİL.
				//
				// CANLI KANIT: "client tarafında çok fazla kasma donma".
				// DetectMoves() 216 tablo girdisinin transform'unu okuyor,
				// yani kare başına ~1300 özellik erişimi ve 216 sahne
				// nesnesine dokunma. Ama sonucu SADECE her SEND_INTERVAL
				// (50 ms) gönderiliyor — yani kare başına iş yapıp sonucun
				// 19 kare boyunca çöpe atıyorduk.
				//
				// Tarama gönderimle aynı ritme alındı: 144 Hz'de 144 yerine
				// 20 tarama. Doğruluk kaybı YOK: iki tarama arasında hareket
				// eden parça bir sonraki taramada zaten yakalanır (ölçüm
				// son konuma karşı yapılır, art arda örnekleme değil).
				DetectMoves();
				SendDirty();

				// Başlangıç senkronu bitti mi? Tek seferlik bildirim.
				if (initialPending > 0 && dirtyOrder.Count == 0) {
					SmpLog.Log("part transform sync complete: " + SentTransforms + " part(s) sent.");
					initialPending = 0;
					transformSyncDone = true;
				}
			}
			catch (Exception e) {
				SmpLog.Warning("part transform tick failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// Tablodaki her parçanın transform'unu okuyup, son gönderilenden
		/// farklıysa işaretler.
		///
		/// Maliyet: 218 parça * (position + rotation okuma) = kare başına ~1300
		/// özellik okuması. Bu ucuzdur; asıl maliyet gönderimdedir ve o
		/// 100ms'de bir yapılır. "Her kare tümünü gönder" YANLIŞTIR: yüzlerce
		/// parça * 20 Hz ağ tıkar ve oyunu kasar.
		/// </summary>
		static void DetectMoves() {
			IList<SatsumaPart> table = SatsumaRegistry.Table;
			if (table == null) return;
			for (int i = 0; i < table.Count; i++) {
				SatsumaPart part = table[i];
				if (part == null || part.Object == null) continue;
				Transform t;
				try { t = part.Object.transform; } catch { continue; }
				Vector3 p = t.position;
				Quaternion r = t.rotation;

				Sent s;
				lastSent.TryGetValue(part.Id, out s);
				if (s != null && s.Has
					&& (p - s.Pos).sqrMagnitude < POS_EPSILON * POS_EPSILON
					&& QuaternionAngle(s.Rot, r) < ROT_EPSILON) {
					continue;   // değişmemiş
				}
				if (dirty.Add(part.Id)) dirtyOrder.Add(part.Id);
			}
		}

		/// <summary>
		/// İşaretlenen parçaları tek pakette gönderir (MAX sınırına kadar).
		/// Gönderilenler dirty listesinden düşer, kalanlar sonraki çağrıya kalır.
		///
		/// GERÇEK BELLEK/BÜYÜME HATASI (düzeltildi):
		///
		/// dirtyOrder'ın "temizlenmediği" doğru değil — finally bloğu
		/// RemoveRange ile tüketilen kayıtları siliyordu. Asıl hata şuydu:
		///
		///   Gönderim BAŞARISIZ olunca kod, tüketilen kayıtları dirty'ye geri
		///   koyuyordu; ancak finally bloğu onları dirtyOrder'dan SİLİYORDU.
		///
		///   dirty = { A, B, C }      dirtyOrder = { A }        (B, C yok)
		///
		/// DetectMoves her karede `if (dirty.Add(id)) dirtyOrder.Add(id)`
		/// kullanıyor. A ve B dirty'de ZATEN olduğu için Add false döner,
		/// yani dirtyOrder'a ASLA geri giremezler. Sonuç: A ve B o parçalar
		/// için sonsuza kadar takılı kalır, gönderilmez, üstelik dirty kümesi
		/// büyümeye devam eder (gerçek sızıntı).
		///
		/// DÜZELTME: başarısızlıkta kayıtlar dirtyOrder'da TUTULUR (yalnızca
		/// dirty üyeliği geri konur). Böylece bir sonraki çağrı onları yeniden
		/// dener. Ek olarak dirtyOrder'da dirty olmayı bırakmış kayıtlar
		/// (çözülemeyen parça, iptal) ayrıca temizlenir.
		/// </summary>
		static int SendDirty() {
			if (dirtyOrder.Count == 0) return 0;
			if (!SteamTransport.HasRemote) return 0;

			int sent = 0;
			MemoryStream s = null;
			int tCountPos = -1;
			int consumed = 0;
			bool keepForRetry = false;
			try {
				for (int i = 0; i < dirtyOrder.Count && sent < MAX_PARTS_PER_PACKET; i++) {
					string id = dirtyOrder[i];
					consumed = i + 1;
					SatsumaPart part;
					if (!SatsumaRegistry.TryGetById(id, out part) || part == null || part.Object == null) {
						dirty.Remove(id);
						continue;
					}
					Transform t = part.Object.transform;

					if (s == null) {
						s = Protocol.Begin(Protocol.Msg.PartTransform);
						// F01_PATCH_OFFSET: sayim baytinin OFSETI burada
						// yakalanir. `PatchCountByte` daha once `6`
						// diye SABIT yaziyordu; o indeks ilk kaydin
						// string uzunluk onegi -- sayim bayti degil.
						// `Protocol.Begin` 4+1 = 5 bayt yazar
						// (HEADER_SIZE, Protocol.cs:112), yani sayim
						// bayti indeksi 5.
						tCountPos = (int)s.Position;
						new BinaryWriter(s).Write((byte)0);   // sayim yer tutucusu
					}
					BinaryWriter w = new BinaryWriter(s);
					w.Write(id);
					w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
					w.Write(t.rotation.x); w.Write(t.rotation.y);
					w.Write(t.rotation.z); w.Write(t.rotation.w);

					Sent rec;
					if (!lastSent.TryGetValue(id, out rec) || rec == null) {
						rec = new Sent();
						lastSent[id] = rec;
					}
					rec.Pos = t.position;
					rec.Rot = t.rotation;
					rec.Has = true;

					dirty.Remove(id);
					sent++;
				}
				if (sent == 0) return 0;

				// F01_PATCH_OFFSET: gercek ofset gonderilir.
				PatchCountByte(s, tCountPos, sent);
				tCountPos = -1;
				if (SteamTransport.SendReliable(Protocol.Finish(s))) {
					SentTransforms += sent;
				}
				else {
					// Gönderilemedi: bu sayfa dirtyOrder'DA KALSIN, bir sonraki
					// çağrı yeniden denesin. dirty'ye geri koymak YETMEZ; kayıt
					// dirtyOrder'dan da düşerdi ve bir daha hiç denenmezdi.
					keepForRetry = true;
					return 0;
				}
				return sent;
			}
			catch (Exception e) {
				SmpLog.Warning("part transform send failed: " + e.Message);
				keepForRetry = true;
				return 0;
			}
			finally {
				if (s != null) { try { s.Dispose(); } catch { } }
				if (!keepForRetry && consumed > 0) {
					// Tüketilen kayıtları listeden çıkar, kalanlar durur.
					dirtyOrder.RemoveRange(0, Math.Min(consumed, dirtyOrder.Count));
				} else {
					// Yeniden denenecekler listede kalıyor; dirty üyeliği de
					// yerinde olmalı ki DetectMoves onları atlamasın.
					for (int k = 0; k < consumed && k < dirtyOrder.Count; k++)
						dirty.Add(dirtyOrder[k]);
				}
				// BÜYÜME EMNİYETİ: dirtyOrder'da artık dirty olmayan kayıtlar
				// (çözülemeyen parça, sahne değişimi) sonsuza kadar birikir.
				// dirty zaten sende değilse bir işi yok, atılır.
				PruneOrder();
			}
		}

		/// <summary>
		/// dirtyOrder'da artık dirty olmayan kayıtları siler.
		///
		/// NEDEN: dirtyOrder bir LİST, dirty bir HASHSET. Bir parça dirty'ye
		/// eklenirken listeye de giriyor, ama listeden yalnızca gönderim
		/// başarısı olduğunda çıkıyor. Başarısız gönderim artık listeyi
		/// koruduğu için, çözülemeyen/cancel edilen kayıtlar listede kalıp
		/// liste sonsuza kadar büyüyebilir. Bu, her çağrıda listeyi gezip
		/// yalnızca dirty üyeliği kalmışları tutan tek geçişli bir tarama.
		/// </summary>
		static void PruneOrder()
		{
			if (dirtyOrder.Count == 0) return;
			int w = 0;
			for (int r = 0; r < dirtyOrder.Count; r++) {
				string id = dirtyOrder[r];
				if (id != null && dirty.Contains(id)) {
					dirtyOrder[w++] = id;
				}
			}
			if (w < dirtyOrder.Count) dirtyOrder.RemoveRange(w, dirtyOrder.Count - w);
		}

		/// <summary>
		/// Başlangıç senkronu: client bağlandığında TÜM parçaların transform'u
		/// işaretlenir ve kare başına bir paketle yayılır.
		///
		/// Neden tek seferde değil: 218 parça / 24 = 10 güvenilir paket TEK
		/// karede gönderilirse Steam kuyruğu dolar, kare uzar ve oyun
		/// takılır. Aynı sebeple parça/çivata başlangıç senkronu da
		/// coroutine ile sayfalıyor. Burada aynı desen Tick() üzerinden
		/// sürülüyor: işaretle, sonra her 100 ms'de bir sayfa boşalt.
		/// </summary>
		public static void SendAll() {
			try {
				if (SatsumaRegistry.PartCount == 0 || !SteamTransport.HasRemote) return;
				lastSent.Clear();
				dirty.Clear();
				dirtyOrder.Clear();

				DetectMoves();          // hepsini kirli işaretle
				initialPending = dirtyOrder.Count;
				transformSyncDone = false;
				SmpLog.Log("part transform sync queued: " + initialPending
					+ " part(s), " + MaxPerPacket() + " per packet (spread over frames).");
			}
			catch (Exception e) {
				SmpLog.Warning("part transform initial sync failed: " + e.Message);
			}
		}

		static int initialPending;

		static int MaxPerPacket() { return MAX_PARTS_PER_PACKET; }

		// ------------------------------------------------------------------
		// Uygulama (karşı tarafta)
		// ------------------------------------------------------------------

		/// <summary>
		/// Gelen transform paketini uygular.
		///
		/// ECHO GUARD: uygulama sırasında yerel hook'lar (AssemblyPatch)
		/// transform'u değil bool/olay dinlediği için normalde yankı olmaz;
		/// yine de kapsam açıyoruz, çünkü FSM yan etkisi olarak Install FSM'i
		/// tetikleyip bool yazabilir.
		/// </summary>
		public static void OnMessage(BinaryReader r, byte count) {
			for (int i = 0; i < count; i++) {
				try {
					string id = r.ReadString();
					Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(),
													r.ReadSingle(), r.ReadSingle());

					SatsumaPart part;
					if (!SatsumaRegistry.TryGetById(id, out part) || part == null || part.Object == null) {
						UnresolvedParts++;
						continue;
					}
					Transform t = part.Object.transform;
					if ((t.position - p).sqrMagnitude < POS_EPSILON * POS_EPSILON
						&& QuaternionAngle(t.rotation, q) < ROT_EPSILON) {
						continue;   // zaten yerinde
					}

					EchoGuard.Scope scope = EchoGuard.Enter();
					try {
						t.position = p;
						t.rotation = q;
						// Parent değişmiş olabilir (araçta takılı). Eğer parça
						// host'ta araçtaysa, client'ta da araçta olmalı; parent'ı
						// senkronlamak FSM'i zorlamadan konumu sabitler.
						// Parent'ı değiştirmek tehlikeli olabilir; bu yüzden
						// sadece world konumu uygulanır. Araçla ilişkiyi FSM
						// (ASSEMBLE) yönetir.
					}
					finally { scope.Dispose(); }

					Sent s;
					if (!lastSent.TryGetValue(id, out s) || s == null) {
						s = new Sent();
						lastSent[id] = s;
					}
					s.Pos = p;
					s.Rot = q;
					s.Has = true;
					AppliedTransforms++;
				}
				catch (Exception e) {
					Telemetry.BadPackets++;
					SmpLog.Warning("part transform packet rejected: " + e.Message);
					return;
				}
			}
		}

		/// <summary>Dünya çıkışında temizle.</summary>
		public static void Clear() {
			lastSent.Clear();
			dirty.Clear();
			dirtyOrder.Clear();
			transformSyncDone = false;
			initialPending = 0;
		}

		// ------------------------------------------------------------------
		// Yardımcılar
		// ------------------------------------------------------------------

		/// <summary>İki rotasyon arasındaki açı farkı (derece), karekökü.</summary>
		static float QuaternionAngle(Quaternion a, Quaternion b) {
			float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
			if (dot < 0f) dot = -dot;
			if (dot > 1f) dot = 1f;
			return (float)(2.0 * Math.Acos(dot));
		}

		/// <summary>
		/// F01_PATCH_OFFSET: sayim yer tutucusunu gercek sayiyla degistirir.
		///
		/// <paramref name="countPos"/> GONDERICI tarafta <c>Begin</c>'den
		/// sonraki konumdur -- yani <c>Write((byte)0)</c> yazilmadan ONCE
		/// yakalanan <c>s.Position</c>.
		///
		/// ONCEDEN SABIT <c>6</c> yaziyordu ve BU HATALIYDI:
		/// <c>Protocol.Begin</c> 4 + 1 = 5 bayt yazar
		/// (<c>HEADER_SIZE</c>, Protocol.cs:112), yer tutucu indeks 5'e,
		/// ilk kayit indeks 6'dan baslar. <c>6</c> yazmak sayim baytini
		/// degil ilk kaydin 7-bit STRING UZUNLUK ONEKININI eziyordu;
		/// alici tarafta <c>ReadByte()</c> her zaman 0 okuyup hicbir
		/// kaydi uygulamiyordu.
		/// </summary>
		static void PatchCountByte(MemoryStream s, int countPos, int written) {
			if (s == null || written <= 0) return;
			if (countPos < 0) return;
			long end = s.Position;
			s.Position = countPos;
			new BinaryWriter(s).Write((byte)Math.Min(written, 255));
			s.Position = end;
		}
	}
}
