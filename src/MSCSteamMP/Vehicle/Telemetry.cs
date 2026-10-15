using MSCLoader;

namespace SteamMP {

	/// <summary>
	/// Send/receive counters, shown by 'smp status'. They are the first thing to look at
	/// when a sync looks wrong: an exploding send count means the flush rate is too high or
	/// something is looping locally, a zero receive count means the packets never arrive.
	/// </summary>
	public static class Telemetry {
		// vehicle side
		public static int SentTransforms;
		public static int SentSwitches;
		public static int SentStates;
		public static int SentEngines;
		public static int SentFullStates;
		public static int ReceivedTransforms;
		public static int ReceivedSwitches;
		public static int ReceivedStates;
		public static int ReceivedEngines;
		public static int ReceivedFullStates;
		public static int ReceivedUnknownVehicle;
		public static int ReceivedRemoteFsmEvents;

		// satsuma side
		// remote player side
		public static int SentPlayerPositions;
		public static int ReceivedPlayerPositions;

		// vehicle input side
		public static int SentWheelStates;
		public static int ReceivedWheelStates;

		public static int SentPartEvents;
		public static int SentPartStates;
		public static int SentBoltSyncs;
		public static int SentSyncDone;
		public static int SentSyncRequests;
		public static int ReceivedPartEvents;
		public static int ReceivedPartStates;
		public static int ReceivedBoltSyncs;
		public static int ReceivedBatches;
		public static int ReceivedSyncDone;
		public static int ReceivedSyncRequests;
		public static int AppliedPartEvents;
		public static int AppliedPartStates;
		public static int AppliedBolts;
		public static int AppliedBatches;

		/// <summary>Incoming ids that had no exact match but resolved by unique object name.</summary>
		public static int FallbackLookups;

		/// <summary>Incoming ids that matched nothing at all, usually a different save.</summary>
		public static int UnresolvedParts;

		/// <summary>Bolt indices past the end of a part's bolt list, i.e. disagreeing tables.</summary>
		public static int MismatchedBolts;

		/// <summary>
		/// Bolts resolved by their own path instead of the index. This is the normal
		/// case now: the two sides had different saves (live log: host 378 parts /
		/// 337 bolts, peer 369 / 332), so the indices shifted and the wrong bolt got
		/// unscrewed. Close to AppliedBolts means the save-independent path works.
		/// </summary>
		public static int BoltsByPath;

		// --- UYGULAMA KATMANI HUNISI (SatsumaRegistry) ---
		//
		// NEDEN: NETSTAT yalnız TAŞIMA katmanını ölçüyor (gönderilen /
		// alınan / reddedilen). Teslim %100 çıkmasına rağmen parçalar
		// "senkron olmuyor" deniyordu; çünkü paket GELİYOR ama
		// UYGULANMIYORDU ve nedeni hiçbir yerde yazmıyordu.
		//
		// Bu oturumda aynı hata iki kez tekrarlandı (ölçüm yanlış
		// katmandaydı: yer payı ve kafa ekseni). Burada KAYIP YOLUN
		// TAMAMINI ayrı ayrı sayıyoruz.
		//
		// Her cıvata TAM OLARAK BİR KOVAYA düşer:
		//   BoltRecords  = toplam okunan kayıt
		//   uygulandı     = BoltApplied
		//   düştü: BoltPartMissing | BoltNoBolts | BoltIndexOOR
		//          | BoltAuthorityRejected | BoltFailed
		//
		// Kovaya düşmeyen tek yol kalmamalı; kalan fark != 0 ise
		// sayac eksiktir.

		/// <summary>Batch'lerden okunan toplam cıvata kaydı (uygulama ÖNCESİ).</summary>
		public static int BoltRecords;
		/// <summary>Başarıyla dünyaya yazılan cıvata.</summary>
		public static int BoltApplied;
		/// <summary>Parça kimliği çözülemedi (genelde FARKLI KAYIT).</summary>
		public static int BoltPartMissing;
		/// <summary>Parça bulundu ama cıvata listesi boş.</summary>
		public static int BoltNoBolts;
		/// <summary>Index, parçanın cıvata sayısını aşıyor (kayıt uyuşmazlığı).</summary>
		public static int BoltIndexOOR;
		/// <summary>
		/// Gelen KADEME geçersiz (0..8 dışında, özellikle -1).
		///
		/// -1 bu projede "uzlaştırma yok" GÖNDERGESİDİR (SatsumaBolt.PendingStage),
		/// bir kademe değil. Canlı izde `tightened -1 -> 8` gibi okunamaz
		/// satırlar bu yüzden oluşuyordu. Artık ApplyBolt bunları reddediyor.
		/// </summary>
		public static int BoltStageOOR;
		/// <summary>Yetki/mesafe kontrolü reddetti.</summary>
		public static int BoltAuthorityRejected;
		/// <summary>Yazma sırasında istisna.</summary>
		public static int BoltFailed;
		/// <summary>Yol eşleşmesi tutmadı, index ile çözüldü.</summary>
		public static int BoltByIndex;

		/// <summary>Batch'lerden okunan toplam parça kaydı.</summary>
		public static int PartRecords;
		/// <summary>Başarıyla uygulanan parça durumu.</summary>
		public static int PartApplied;
		/// <summary>Parça kimliği çözülemedi.</summary>
		public static int PartMissing;
		/// <summary>Yetki/koruma reddetti.</summary>
		public static int PartAuthorityRejected;
		/// <summary>Yazma sırasında istisna.</summary>
		public static int PartFailed;

		/// <summary>
		/// FSM olayı gönderilirken oyunun kendi kodu istisna attı.
		///
		/// CANLI ÖLÇÜM: 8 parça hiç uygulanamıyordu —
		///   trigger_sparkplug1..4, trigger_alternator belt,
		///   trigger_oil filter, headlight left/right trigger_bulb1/2
		/// hepsi "Object reference not set to an instance of an object".
		///
		/// ESKİ DAVRANIŞ: SendEvent ile durum yazımı AYNI try
		/// bloğundaydı. Oyunun FSM'i attığında blok yarıda kesiliyor
		/// ve InstalledVars HİÇ yazılmıyordu: parça kayıtta takılı
		/// kalmıyor, üstelik hata "başarısız" diye sayılıyordu.
		///
		/// YENİ: iki ayrı try. FSM atsa bile durum yazılır; oyunun
		/// görseli eksik kalır ama KAYIT doğru olur (kayıttan yeniden
		/// yüklendiğinde doğru duruma gelir).
		/// </summary>
		public static int PartEventFailed;

		/// <summary>
		/// "Yerel oyuncunun niyeti kazandı" uygulamaları.
		///
		/// CANLI ÖLÇÜM: evin kapısı açıldıktan 1.5 sn sonra OYUNUN KENDİ
		/// FSM'i kapattı (DOOR-SRC CLOSE, aralarında DOOR-RX yok). Yani
		/// geri besleme döngüsü değil, oyunun kararı. Bu sayaç o kararın
		/// üstüne yazdığımızı sayar.
		/// </summary>
		public static int DoorIntentOverrides;

		/// <summary>
		/// Tablo HENÜZ KURULMADIĞI için düşen kayıt.
		///
		/// NEDEN AYRI KOVA: canlı ölçümde huni "602 parça-yok" gösteriyordu
		/// ama bunların hepsi BOZUK KAYIT değildi — client dünyası taranmadan
		/// önce gelen iki tam turdu (host 3 kez gönderiyor, ilk ikisi boşa).
		/// Gerçek kimlik çözümleme hataları bu gürültünün içinde kayboluyordu.
		///
		/// Artık ayrı sayılıyor: "henüz hazır değildim" ile "bu parçayı
		/// tanımıyorum" farkı hunide görünür.
		/// </summary>
		//
		// CANLI HATA (kendi olcum hatam): tek sayac hem civata hem parca
		// kaydini sayiyordu. Canli olcumde ikisi de 461 yazdi (301 + 160)
		// ve "SAYILAMAYAN -160 / -301" cikti — yani sayilar DOGRUYDU,
		// sadece HANGI satira yazilacagi bilinmiyordu. Ikiye bolundu.
		public static int BoltNotReady;

		// --- GÖRSEL/FSM KATMANI ---
		//
		// NEDEN: "paket geldi, deger yazildi" ile "oyunun olayi isledi"
		// AYRI sey. Canli olcumde baslangic senkronunda FSM tepki verdi
		// ("'Assembly' -> 'Assemble'"), ama CANLI degisiklikte hicbir iz
		// yoktu — cunku uzaktan gelen eylem hicbir yere yazilmaz.
		//
		// RECONCILEBOLTS'in bes daldan DORDU civatayi sessizce dusuruyor
		// ve hicbiri loglanmiyordu. Yani "vida donmedi" ile "vida
		// hedefe zaten ulasti" ayni gorunuyordu.
		//
		// zincir: kuyruga alindi -> olay gonderildi -> hedefe ulasti
		//         | hedef cok uzak | FSM olayi TANI MIYOR | vazgecildi
		public static int ReconcileQueued;
		public static int ReconcileEventSent;
		public static int ReconcileAtTarget;
		public static int ReconcileTooFar;
		public static int ReconcileNoEvent;   // FSM'de TIGHTEN/UNTIGHTEN YOK
		public static int ReconcileGaveUp;    // deneme hakki bitti
		public static int ReconcileNoTarget;

		// QueueReconcile'ın beş ERKEN ÇIKIŞI. Olumsuz sonuç değil çoğu —
		// ama hangisi olduğu bilinmezse "fsm: 0 ..." satırı YANLIŞ
		// OKUNUR: sıfır, "hiçbir şey denenmedi" ile "hepsi gereksizdi"
		// arasındaki farkı göstermiyor.
		//
		// Canlı ölçüm: 299 uzak cıvata geldi, kuyruğa 0 girdi.
		// Nedeni: çoğu zaten hedef kademedeydi (8 -> 8, -1 -> 0).
		public static int Queued;
		public static int SkipNoFsm;
		public static int SkipAtTarget;      // en sık dal, beklenen davranış
		public static int SkipNoEvent;       // FSM'de olay YOK  ← KÖK NEDEN
		public static int SkipAlreadyQueued;
		public static int SkipQueueFull;
		public static int InitialSyncBolts;
		public static int LiveBolts;  // Stage/Screw referansi yok
		/// <summary>Uzaktan gelen cıvata eylemi (canlı senkron kanıtı).</summary>
		public static int RemoteBoltActions;
		/// <summary>Uzaktan gelen parça olayı (canlı senkron kanıtı).</summary>
		public static int RemotePartActions;
		/// <summary>Parça kaydının tablo hazır olmadığı için düşmesi.</summary>
		public static int PartNotReady;

		// --- Yeni sistemler için telemetri ---
		public static int SentPlayerNeeds;
		public static int ReceivedPlayerNeeds;
		public static int SentChatMessages;
		public static int ReceivedChatMessages;
		public static int SentAIVehicleStates;
		public static int ReceivedAIVehicleStates;
		public static int SentItemSpawnRequests;
		public static int ReceivedItemSpawnRequests;
		public static int SentItemSpawns;
		public static int ReceivedItemSpawns;
		// SentSaveGameChunks KALDIRILDI: kayit aktarimi olü koddu ve
		// kaldirildi; sayac her zaman 0 gosterip durumu yaniltiyordu.

		// Düşen paketler (ağ/kanal katmanı)
		public static int DroppedOversized;

		// HUD ping
		public static int SentPings;
		public static int ReceivedPings;

		public static int BadPackets;
		public static string LastReceived = "(none yet)";
		public static bool Verbose;

		public static void Reset() {
			SentTransforms = SentSwitches = SentStates = SentEngines = SentFullStates = 0;
			ReceivedTransforms = ReceivedSwitches = ReceivedStates = ReceivedEngines = 0;
			ReceivedFullStates = ReceivedUnknownVehicle = ReceivedRemoteFsmEvents = 0;
			SentPartEvents = SentPartStates = SentBoltSyncs = SentSyncDone = SentSyncRequests = 0;
			SentPlayerPositions = ReceivedPlayerPositions = 0;
			SentWheelStates = ReceivedWheelStates = 0;
			ReceivedPartEvents = ReceivedPartStates = ReceivedBoltSyncs = ReceivedBatches = 0;
			ReceivedSyncDone = ReceivedSyncRequests = 0;
			AppliedPartEvents = AppliedPartStates = AppliedBolts = AppliedBatches = 0;
			FallbackLookups = UnresolvedParts = MismatchedBolts = BoltsByPath = BadPackets = 0;

			// Uygulama hunisi (her sayaç YALNIZCA bir kez)
			BoltRecords = BoltApplied = BoltPartMissing = BoltNoBolts = 0;
			BoltIndexOOR = BoltAuthorityRejected = BoltFailed = BoltByIndex = 0;
			BoltStageOOR = 0;
			PartRecords = PartApplied = PartMissing = 0;
			PartAuthorityRejected = PartFailed = PartEventFailed = 0;
			BoltNotReady = PartNotReady = 0;
			ReconcileQueued = ReconcileEventSent = ReconcileAtTarget = 0;
			ReconcileTooFar = ReconcileNoEvent = ReconcileGaveUp = 0;
			ReconcileNoTarget = RemoteBoltActions = RemotePartActions = 0;
			Queued = SkipNoFsm = SkipAtTarget = SkipNoEvent = 0;
			SkipAlreadyQueued = SkipQueueFull = InitialSyncBolts = LiveBolts = 0;

			// Yeni alanlar (her biri YALNIZCA bir kez sıfırlanır: önceki
			// sürümde aynı sayaçlar üç kez yazılmıştı, bu gürültüydü)
			SentPlayerNeeds = ReceivedPlayerNeeds = 0;
			SentChatMessages = ReceivedChatMessages = 0;
			SentAIVehicleStates = ReceivedAIVehicleStates = 0;
			SentItemSpawnRequests = ReceivedItemSpawnRequests = 0;
			SentItemSpawns = ReceivedItemSpawns = 0;
			DroppedOversized = 0;
			SentPings = ReceivedPings = 0;

			LastReceived = "(none yet)";
		}

		public static void Log(string message) {
			LastReceived = message;
			if (Verbose) SmpLog.Log("rx " + message);
		}

		public static string Summary() {
			return "sent T" + SentTransforms + " sw" + SentSwitches + " st" + SentStates
				+ " e" + SentEngines + " f" + SentFullStates
				+ " | parts ev" + SentPartEvents + " st" + SentPartStates
				+ " bolts" + SentBoltSyncs
				+ " | player out" + SentPlayerPositions + " in" + ReceivedPlayerPositions
				+ " | wheel out" + SentWheelStates + " in" + ReceivedWheelStates
				+ " | recv T" + ReceivedTransforms + " sw" + ReceivedSwitches + " st" + ReceivedStates
				+ " e" + ReceivedEngines + " f" + ReceivedFullStates
				+ " | parts ev" + ReceivedPartEvents + " st" + ReceivedPartStates
				+ " bolts" + ReceivedBoltSyncs + " batch" + ReceivedBatches
				+ " | unresolved " + UnresolvedParts + " mismatch " + MismatchedBolts
				+ " bad " + BadPackets;
		}

		/// <summary>
		/// Yeni modüllerin sayaçları. 'smp status' bunu ayrı satırda gösterir:
		/// hepsi sıfırsa o modüller çalışmıyor demektir (bağlanmamış ya da
		/// mesaj yönlendirmesi eksik).
		/// </summary>
		public static string ExtendedSummary() {
			return "needs " + SentPlayerNeeds + "/" + ReceivedPlayerNeeds
				+ " | chat " + SentChatMessages + "/" + ReceivedChatMessages
				+ " | ai-veh " + SentAIVehicleStates + "/" + ReceivedAIVehicleStates
				+ " | spawn " + SentItemSpawns + "/" + ReceivedItemSpawns
				+ " spawn-req " + SentItemSpawnRequests + "/" + ReceivedItemSpawnRequests
				+ " | ping " + SentPings + "/" + ReceivedPings
				+ " | oversized " + DroppedOversized;
		}

		public static string SatsumaSummary() {
			return "applied ev" + AppliedPartEvents + " st" + AppliedPartStates
				+ " bolts" + AppliedBolts + " batches" + AppliedBatches
				+ " | name-fallback " + FallbackLookups + " | sync-done " + ReceivedSyncDone
				+ " sync-req out" + SentSyncRequests + " in" + ReceivedSyncRequests
				+ " | bolt-index-mismatch " + MismatchedBolts
				+ " | bolt-by-path " + BoltsByPath + "/" + AppliedBolts;
		}

		/// <summary>
		/// UYGULAMA HUNISI — gelen cıvatanın nereye düştüğü.
		///
		/// 'smp netstat' bunu da basar. Okuma kuralı:
		///
		///   okunan   BoltRecords      ← kaç kayıt GELDİ
		///   uygulandı BoltApplied      ← kaçı gerçekten YAZILDI
		///
		///   ikisi eşitse sorun YOK'TA (aktarım sağlam).
		///   okunan &gt; uygulandı ise kayıp AŞAĞIDAKİ kovalardadır:
		///
		///     BoltPartMissing         parça bulunamadı  → FARKLI KAYIT
		///     BoltNoBolts             cıvata listesi boş → kayıt eksik
		///     BoltIndexOOR            index taşıyor       → cıvata sayısı farklı
		///     BoltAuthorityRejected   yetki/mesafe reddi
		///     BoltFailed              istisna
		///
		///   toplam ~= okunan değilse bir yol sayılmamış demektir.
		/// </summary>
		public static string ApplyFunnel() {
			int dropped = BoltPartMissing + BoltNoBolts + BoltIndexOOR
				+ BoltAuthorityRejected + BoltFailed + BoltStageOOR;
			int realDropped = dropped + BoltNotReady;
			string boltTail = "uygulandi " + BoltApplied + "/" + BoltRecords
				+ " | tablo-hazir-degil " + BoltNotReady
				+ " | dustu: parca-yok " + BoltPartMissing
				+ " civata-yok " + BoltNoBolts
				+ " index-tasma " + BoltIndexOOR
				+ " kademe-gecersiz " + BoltStageOOR
				+ " yetki-reddi " + BoltAuthorityRejected
				+ " istisna " + BoltFailed
				+ " (toplam " + dropped + ")";
			// Kalan fark: kayıt sayısı ile uygulanan+düşen toplamı.
			int unaccounted = BoltRecords - BoltApplied - realDropped;
			if (unaccounted != 0)
				boltTail += " !! SAYILAMAYAN " + unaccounted;
			boltTail += " | yolla " + BoltsByPath + " indexle " + BoltByIndex;

			// DİKKAT: PartEventFailed DÜŞÜş DEĞİL. FSM olayı patladı ama
			// durum değişkeni YAZILDI (iki ayrı try), yani parça
			// uygulanmış sayılır. Bunu "düşen"e koymak PartApplied'ı iki
			// kez saydırıyor ve "SAYILAMAYAN -N" üretiyordu — canlı
			// ölçümde tam olarak bu oldu (-6).
			int partDropped = PartMissing + PartAuthorityRejected + PartFailed
				+ PartNotReady;
			string partTail = "uygulandi " + PartApplied + "/" + PartRecords
				+ " | tablo-hazir-degil " + PartNotReady
				+ " | dustu: parca-yok " + PartMissing
				+ " yetki-reddi " + PartAuthorityRejected
				+ " istisna " + PartFailed
				+ (PartEventFailed > 0
					? " (ayrica oyunun FSM'i " + PartEventFailed
					  + " olayda patladi, kayit durumu YINE yazildi)"
					: "");
			int partUnaccounted = PartRecords - PartApplied - partDropped;
			if (partUnaccounted != 0)
				partTail += " !! SAYILAMAYAN " + partUnaccounted;

			// Zincir toplami kuyruga alinan sayisina esit olmali.
			int reconOut = ReconcileAtTarget + ReconcileTooFar
				+ ReconcileNoEvent + ReconcileGaveUp + ReconcileNoTarget;
			string recon = ReconcileQueued + " civata uzlastirmeye alindi -> "
				+ "hedefe ulasti " + ReconcileAtTarget
				+ " | olay GONDERILDI " + ReconcileEventSent
				+ " | hedef cok uzak " + ReconcileTooFar
				+ " | FSM OLAYI YOK " + ReconcileNoEvent
				+ " | vazgecildi " + ReconcileGaveUp
				+ " | hedef referansi yok " + ReconcileNoTarget
				+ (reconOut + ReconcileEventSent != ReconcileQueued
					? " !! HESAP TUTMUYOR (" + (ReconcileQueued - reconOut - ReconcileEventSent) + ")"
					: "");
			string live = "CANLI (SyncDone sonrasi): " + LiveBolts + " civa, "
				+ RemotePartActions + " parca olayi | baslangic senkronu: "
				+ InitialSyncBolts + " civa | toplam uzaktan: " + RemoteBoltActions;
			int qSkip = SkipNoFsm + SkipAtTarget + SkipNoEvent
				+ SkipAlreadyQueued + SkipQueueFull;
			string qLine = Queued + " kuyruga alindi | zaten-hedefte "
				+ SkipAtTarget + " | FSM-OLAYI-YOK " + SkipNoEvent
				+ " | fsm-yok " + SkipNoFsm + " | zaten-kuyrukta "
				+ SkipAlreadyQueued + " | kuyruk-dolu " + SkipQueueFull
				+ (Queued + qSkip != RemoteBoltActions
					? " !! HESAP TUTMUYOR (" + (RemoteBoltActions - Queued - qSkip) + ")"
					: "");

			return "APPLY-FUNNEL  civata: " + boltTail
				+ "\nAPPLY-FUNNEL  parca: " + partTail
				+ "\nAPPLY-FUNNEL  fsm  : " + recon
				+ "\nAPPLY-FUNNEL  kuyruk: " + qLine
				+ "\nAPPLY-FUNNEL  canli: " + live;
		}
	}
}