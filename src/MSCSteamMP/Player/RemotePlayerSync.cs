using System.IO;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// UZAK OYUNCUNUN KONUM AKIŞI.
	///
	/// Protokol
	/// --------
	///   Msg.PlayerPos  (30)  UNRELIABLE, 20 Hz, 32 bayt
	///       float x, y, z
	///       float qx, qy, qz, qw
	///       float speed          (yalnızca animasyon seçimi için)
	///
	///   Msg.PlayerInfo (31)  RELIABLE, oturum başına bir kez
	///       string peerName
	///
	/// Neden PlayerPos güvenilmez
	/// -----------------------
	/// 20 Hz konum akışıdır; düşen bir paket bir sonraki 50 ms'de kendiliğinden
	/// düzelir. Güvenilir kanal 50 ms'lik bir gecikme eklerdi ve kukla takılırdı.
	/// Bu, Satsuma/araç tarafındaki "mutlak değer, güvenilir, değişince" kuralının
	/// tam tersidir ve bilinçlidir: durum bayrakları (PlayerState) güvenlidir,
	/// konum değildir.
	///
	/// Neden hız gönderiliyor
	/// ---------------------
	/// Uzak taraf hızı konum farkından hesaplayabilirdi ama paket 50 ms arayla
	/// gelir; durduğunda fark sıfırdan başlar ve interpolatör gereksiz yere
	/// "yavaşlıyormuş" gibi davranır. 4 byte kesinlik.
	///
	/// İki taraf aynı build'i çalıştırmak zorunda: PROTOCOL_VERSION el sıkışmada
	/// kontrol edilir. Bu dosyadaki tel sırası iki tarafta da aynı olmak zorunda.
	/// </summary>
	public static class RemotePlayerSync {

		/// <summary>Gönderim aralığı (saniye). 20 Hz.</summary>
		public const float SEND_INTERVAL = 0.05f;

		/// <summary>
		/// Konum paketi boyutu: 3 konum + 4 yonelme + hiz + kafa pitch/yaw
		/// = 10 float = 40 bayt.
		///
		/// CANLI HATA (duzeltildi): burasi 32 diyordu ve gerekcesi "3+4+1
		/// float" idi. Bas pitch/yaw alanlari SendPos'a eklendiginde sabit
		/// guncellenmemis. Sonuc: `tests\WireProbe.cs` kendi elle yazdigi
		/// 32 baytlik paketi bu sabitle karsilastirip "geciyor" diyordu,
		/// yani test GERCEK gondericinin duzenini hic olcmuyordu. Sabit ile
		/// tel gercekligi ayrisirsa bunu gorebilecek tek yer o testti.
		/// </summary>
		public const int POS_BYTES = 40;

		// ------------------------------------------------------------------
		// Durum
		// ------------------------------------------------------------------

		static RemotePlayerBehaviour body;

		static float nextSend;
		static bool infoSent;
		static ulong lastPeer;

		/// <summary>Karşı tarafın gördüğü isim (PlayerInfo'dan).</summary>
		static string peerName = "";

		/// <summary>Kaç saniye sonra peer sessiz sayılır (gövde donmasın).</summary>
		public const float SILENCE_TIMEOUT = 5f;

		// ------------------------------------------------------------------

		/// <summary>Kukla var mı?</summary>
		public static bool Spawned { get { return body != null; } }

		/// <summary>Gövde (yoksa null). VehicleRegistry koltuğa bağlarken kullanır.</summary>
		public static RemotePlayerBehaviour Body { get { return body; } }

		/// <summary>Karşı tarafın gönderdiği isim.</summary>
		public static string PeerName { get { return peerName; } }

		/// <summary>Tek satır durum.</summary>
		public static string Describe() {
			if (!PlayerAsset.Ready) return "player asset NOT loaded: " + PlayerAsset.Problem;
			if (body == null) return "player asset OK, no body yet (waiting for PlayerPos)";
			return "body " + RemotePlayerFactory.ROOT_NAME + ": " + body.Describe()
				+ " | info out" + infoSent + " peer '" + (peerName == "" ? "?" : peerName) + "'";
		}

		// ------------------------------------------------------------------
		// Yaşam döngüsü
		// ------------------------------------------------------------------

		/// <summary>Dünya hazır: konum kaynağını bul, gövdeyi temizle.</summary>
		public static void Reset() {
			Clear();

			// PlayerMotion.TryGetPosition, denetleyiciyi burada arıyordu ve
			// TrackFromCamera hiç çağrılmıyordu: Position hep zero kalıyordu.
			// Kamera üzerinden denetleyiciyi bul, yoksa mod kamera yoluna düşer.
			try { PlayerMotion.TrackFromCamera(); }
			catch { }

			if (!PlayerMotion.Available) {
				SmpLog.Print("no CharacterController on the player; remote position "
					+ "will follow the camera instead.");
			}
		}

		/// <summary>
		/// Sahne değişimi: gövdeyi bırak.
		///
		/// CANLI HATA (düzeltildi): `peerName` burada koşulsuz siliniyordu.
		/// `Clear()` yalnızca sahne/oturum sınırında değil, karşı oyuncunun
		/// GÖVDE değiştirdiği anlarda da çağrılıyor (aşağıdaki `OnInfo` ve
		/// `PlayerSkin.RespawnLive`). O anda isim silinir, kukla yeniden
		/// doğar ve etiket "peer" olur. `PlayerInfo` paketi ise oturum
		/// başına BİR kez gönderilir (`infoSent`), karşı taraf gövde
		/// değiştirdiğinde tekrar göndermez; `lastPeer` de değişmediği için
		/// "peer changed" dalı tetiklenmez. Sonuç: isim bağlantı kopup
		/// yeniden kurulana kadar kalıcı olarak kaybolur.
		///
		/// `keepPeerName`: yalnızca gövde yeniden doğuşunda true. Sahne
		/// yüklenmesi, peer değişimi ve `smp player kill` için false —
		/// orada ismin gerçekten değişmiş olması gerekir.
		/// </summary>
		public static void Clear(bool keepPeerName = false) {
			if (body != null) {
				RemotePlayerFactory.Destroy(body);
				body = null;
			}
			nextSend = 0f;
			infoSent = false;
			lastPeer = 0;
			if (!keepPeerName) peerName = "";
		}

		// ------------------------------------------------------------------
		// Gönderim
		// ------------------------------------------------------------------

		/// <summary>
		/// Kare başına bir kez çağrılır: 20 Hz konum + oturum başına bir kez PlayerInfo.
		/// </summary>
		public static void Update() {
			// Peer yoksa gövdeyi kapat.
			if (!SteamTransport.HasRemote) {
				if (body != null) Clear();
				return;
			}

			// Peer değiştiyse (yeni oturum): her şeyi sıfırla ve ismi yeniden iste.
			// SIRA ÖNEMLİ: Clear() lastPeer'i sıfırlar, o yüzden atama
			// SONRA yapılır. Yoksa her kare "peer changed" + yeniden doğuş
			// olur (canlı kanıt: ilk peer'de 2 sn'de 4 reset).
			ulong peer = SteamTransport.RemoteId.m_SteamID;
			if (peer != lastPeer) {
				infoSent = false;
				if (body != null) Clear();
				lastPeer = peer;
				SmpLog.Log("peer changed, remote player reset for " + peer);
			}

			if (!infoSent) SendInfo();

			if (Time.time < nextSend) return;
			nextSend = Time.time + SEND_INTERVAL;

			SendPos();
		}

		/// <summary>Yerel oyuncunun Steam adı (etiket + PlayerInfo için).</summary>
		public static string LocalName {
			get {
				try {
					string n = Steamworks.SteamFriends.GetPersonaName();
					if (!string.IsNullOrEmpty(n)) return n;
				}
				catch { }
				return "peer";
			}
		}

		/// <summary>
		/// Gorunumu peer'a bildirir: skin indeksi + elle secilen govde.
		///
		/// NEDEN PlayerInfo'YA EKLENDI: ayri bir mesaj acmaya gerek
		/// yok; PlayerInfo zaten guvenilir kanalda gidiyor. Sonraki
		/// alanlar ESKI build'li peer'larda YOKTUR, o yuzden okuyucu
		/// "kalan bayt var mi" diye bakıyor (ApplyBoltBatch'teki path
		/// okumasiyla ayni savunma).
		/// </summary>
		public static void SendInfo() {
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.PlayerInfo);
				BinaryWriter w = new BinaryWriter(s);
				// Karsi tarafin kafa ustu etiketinde gorunecek isim.
				w.Write(LocalName);
				// Karsi tarafin bizi GORDUGU gorunum.
				w.Write(PlayerSkin.Selected);
				w.Write(PlayerSkin.bodyOverride);
				w.Flush();
				SteamTransport.SendReliable(Protocol.Finish(s));
				infoSent = true;
			}
			catch (System.Exception e) {
				SmpLog.Warning("player info send failed: " + e.Message);
			}
		}

		/// <summary>
		/// Gorunum degisti: peer'a yenisini bildir.
		///
		/// NEDEN infoSent BAYRAGI ONCE DUSURULUYOR: o bayrak "ilk
		/// bilgi gitti mi" demek, "son bilgi gitti mi" degil.
		/// </summary>
		public static void ResendAppearance() {
			if (!SteamTransport.HasRemote) return;
			infoSent = false;
			SendInfo();
		}

		/// <summary>Peer'in bize bildirdigi gorunum (uzaktaki kukla icin).</summary>
		public static int PeerSkinIndex { get { return peerSkinIndex; } }
		public static int PeerBodyOverride { get { return peerBodyOverride; } }
		static int peerSkinIndex;
		static int peerBodyOverride;

		/// <summary>
		/// Peer yalnızca KIYAFETINI degistirdi (govdesi ayni kaldi):
		/// mesh degismedi, kukla yerinde, sadece materyaller yenilenir.
		/// </summary>
		static void RefreshBodyAppearance() {
			if (body == null) return;
			try {
				RemotePlayerFactory.RefreshAppearance(body.gameObject);
			}
			catch (System.Exception e) {
				SmpLog.Warning("peer appearance refresh failed: " + e.Message);
			}
		}

		static void SendPos() {
			Vector3 p;

			// Önce denetleyici (gerçek karakter konumu), yoksa kamera.
			if (!PlayerMotion.TryGetPosition(out p)) {
				Camera cam = Camera.main != null ? Camera.main : Camera.current;
				if (cam == null) return;
				p = cam.transform.position;
			}

			// Yatay hız: eğilme ve yokuş yanlış animasyon seçtirmesin.
			Vector3 v = PlayerMotion.Velocity;
			float speed = new Vector3(v.x, 0f, v.z).magnitude;

			Quaternion r = transformRotationOf();
			
			// Kafa bakışı: kamera eğimi (pitch) ve kameranın DÜNYA yönü.
			//
			// DİKKAT: headYaw kafa ofseti DEĞİLDİR — kameranın mutlak yönüdür.
			// Ofseti karşı taraf hesaplar (kendi gördüğü gövde yönüne göre
			// fark alarak) ve SADECE koltuktayken uygular. Yürürken kamera
			// gövdeyi izlediği için fark zaten ~0'dır.
			//
			// Bu düzeltme, önceki "headYaw = kamera yönü" gönderiminin
			// karşı tarafta kafayı kendi ekseninde tam tur döndürmesine
			// yol açtığını ortadan kaldırır.
			float headPitch = 0f, headYaw = 0f;
			Camera camHead = Camera.main != null ? Camera.main : Camera.current;
			if (camHead != null) {
				Vector3 euler = camHead.transform.eulerAngles;
				headPitch = euler.x;  // -90..90 aralığına normalize edilir
				headYaw = euler.y;    // 0..360, mutlak dünya yönü
				// Pitch'i -90..90 aralığına normalize et
				if (headPitch > 180f) headPitch -= 360f;
			}

			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.PlayerPos);
				BinaryWriter w = new BinaryWriter(s);
				w.Write(p.x); w.Write(p.y); w.Write(p.z);
				w.Write(r.x); w.Write(r.y); w.Write(r.z); w.Write(r.w);
				w.Write(speed);
				w.Write(headPitch);
				w.Write(headYaw);
				w.Flush();
				SteamTransport.SendUnreliable(Protocol.Finish(s));
				Telemetry.SentPlayerPositions++;
			}
			catch (System.Exception e) {
				SmpLog.Warning("player position write failed: " + e.Message);
			}
		}

		/// <summary>Yayınlanan dönüş: denetleyici varsa o, yoksa kameranın bakış düzlemi.</summary>
		static Quaternion transformRotationOf() {
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) return Quaternion.identity;
			// Kamera pitch'i (eğilme) gönderilmez: kukla tepeden aşağı bakmasın.
			return Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f);
		}

		// ------------------------------------------------------------------
		// Alma
		// ------------------------------------------------------------------

		/// <summary>PlayerPos paketi geldi (30).</summary>
		public static void OnMessage(BinaryReader r) {
			try {
				// Uzunluk kapisi: POS_BYTES'i TEL SOZLESMESINE baglar.
				//
				// Onceden burada hicbir kontrol yoktu; eksik/kisa paket
				// okumanin ORTASINDA EndOfStream atiyor ve teshis, hangi
				// alanin eksik oldugunu soylemeyen genel bir
				// "read past end of stream" satiri oluyordu. Bu kod
				// tabanindaki ayni desen VehicleRegistry ve
				// WorldObjectSync'te zaten kullaniliyor.
				//
				// Ikinci faydasi: sabit artik URETIMDE de okunuyor, yani
				// bas pitch/yaw eklendiginde POS_BYTES'i guncellemeyi
				// unutmak sessiz kalmaz -- paket reddedilir ve log sebebi
				// sayiyla soyler.
				Protocol.AssertBytesRemaining(r, POS_BYTES, "player pos");
				Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(),
					r.ReadSingle(), r.ReadSingle());
				float speed = r.ReadSingle();
				float headPitch = r.ReadSingle();
				float headYaw = r.ReadSingle();

				Telemetry.ReceivedPlayerPositions++;

				// GOVDE HENUZ YOK OLABILIR: NULL DONUSU KONTROL ET.
				//
				// `EnsureBody` sozlesmesi bunu acikca yaziyor: "PlayerAsset
				// yuklenemediyse NULL doner; her paket bunu tekrar dener".
				// Ama cagiran taraf donusu KONTROL ETMIYORDU ve dogrudan
				// body.ApplyPose cagiriyordu. Asset hazir olana kadar gelen
				// HER konum paketi (20/sn) NullReferenceException atiyor,
				// catch bunu "player position packet rejected" diye yazip
				// BadPackets'i sisiriyordu.
				//
				// DAHA KOTUSU: `ReceivedPlayerPositions` bu satirin USTUNDE
				// artiyor. Yani alicinin kendi sayaci "20/sn aldim" derken
				// hicbir sey uygulanmiyordu; teslim teshisi icin tutulan
				// sayaclar birbirini yalanliyordu.
				//
				// NULL bir HATA DEGILDIR (asset henuz yuklenmedi), o yuzden
				// BadPackets artmaz ve log spam'i olmaz; sonraki paket
				// yeniden dener. Asset'in neden yuklenmedigi 'smp status'
				// ciktisinda PlayerAsset.Problem olarak zaten gorunur.
				RemotePlayerBehaviour body = EnsureBody(p, q);
				if (body == null) return;
				body.ApplyPose(p, q, speed);
				body.ApplyHeadRotation(headPitch, headYaw);
			}
			catch (System.Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("player position packet rejected: " + e.Message);
			}
		}

		/// <summary>
		/// PlayerInfo paketi geldi (31): karşı tarafın Steam adı. Gövde
		/// varsa etiketine yazılır, yoksa saklanır ve doğumda yazılır.
		/// </summary>
		public static void OnInfo(BinaryReader r) {
			try {
				peerName = r.ReadString();
				if (string.IsNullOrEmpty(peerName)) peerName = "peer";
				SmpLog.Log("peer name: " + peerName);

				// Gorunum alanlari: eski build'li peer yazmaz, o yuzden
				// SAVUNMALI. Kalan bayt yoksa peer'in gorunumu bilinmiyor
				// demektir ve kukla varsayilanla giydirilir.
				int newSkin = peerSkinIndex;
				int newBody = peerBodyOverride;
				long left = r.BaseStream.Length - r.BaseStream.Position;
				if (left >= 8) {
					try {
						newSkin = r.ReadInt32();
						newBody = r.ReadInt32();
					}
					catch (System.IO.EndOfStreamException) {
						newSkin = peerSkinIndex;
						newBody = peerBodyOverride;
					}
				} else {
					SmpLog.Log("peer build has no appearance field; using defaults.");
				}

				bool changed = (newSkin != peerSkinIndex) || (newBody != peerBodyOverride);
				// Gövde, mesh'i belirler. Peer'in gövdesi değiştiyse
				// mesh de değişir; eski SkinnedMeshRenderer yeni mesh'in
				// boneWeights/bindpose'unu taşıyamaz, tek doğru yol kuklayı
				// sıfırlamak (bir sonraki konum paketinde yeniden doğar).
				int bodyBefore = PlayerSkin.BodyForRemote(peerSkinIndex, peerBodyOverride);
				peerSkinIndex = newSkin;
				peerBodyOverride = newBody;
				int bodyAfter = PlayerSkin.BodyForRemote(peerSkinIndex, peerBodyOverride);

				if (body != null) {
					NameTag tag = body.gameObject.GetComponent<NameTag>();
					if (tag != null) tag.SetText(peerName);
					else NameTag.Attach(body.gameObject, peerName);
				}
				if (changed) {
					SmpLog.Print("peer appearance: skin " + peerSkinIndex
						+ ", body " + (peerBodyOverride > 0
							? peerBodyOverride.ToString() : "auto")
						+ (bodyAfter != bodyBefore
							? "  (mesh changed, respawning)" : ""));
					if (bodyAfter != bodyBefore) Clear(true);          // mesh degisti: isim KORUNUR
					else RefreshBodyAppearance();                   // sadece kiyafet
				}
			}
			catch (System.Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("player info packet rejected: " + e.Message);
			}
		}

		/// <summary>
		/// İlk pakette gövdeyi doğurur.
		///
		/// PlayerAsset yüklenemediyse NULL döner; her paket bunu tekrar dener,
		/// çünkü bundle'ın yüklenmesi sahne ortaya çıktıktan sonra da mümkün
		/// olabilir (ilk Update'te asset henüz yoksa).
		/// </summary>
		static RemotePlayerBehaviour EnsureBody(Vector3 p, Quaternion q) {
			if (body != null) return body;
			if (!PlayerAsset.Ready) return null;

			body = RemotePlayerFactory.Create(p, q);
			if (body != null) {
				NameTag.Attach(body.gameObject,
					string.IsNullOrEmpty(peerName) ? "peer" : peerName);
				SmpLog.Print("remote player body spawned (" + RemotePlayerFactory.ROOT_NAME + ").");
				// Peer bu anda bir aracın içindeyse (EnterVehicle paketi ilk
				// konum paketinden ONCE geldi) kukla hemen koltuğa bağlanmalı.
				// Bu olmazsa kukla aracın dışında asılı kalır.
				try { VehicleRegistry.ReattachPeerBody(); }
				catch (System.Exception e) {
					SmpLog.Warning("could not attach the new body to the vehicle: " + e.Message);
				}
			}
			return body;
		}
	}
}