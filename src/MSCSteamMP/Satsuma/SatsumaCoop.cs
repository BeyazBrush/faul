using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Satsuma Full Co-op senkronizasyonu: 2 kisi ortak montaj (takma/sokme,
	/// civata sikma/gevsetme). Host ve Client arasinda iki yonlu.
	///
	/// Parca hiyerarsisi (dnSpy + runtime taramasi, SatsumaRegistry.Install):
	///   Kok: "SATSUMA(557kg, 248)" (sabit ad, GameObject.Find ile bulunur)
	///   Parca FSM'leri: "Assembly" (tetikleyici), "Removal" (sokme),
	///                   "Assemble" (kablolama, sadece Init calisir),
	///                   "BoltCheck" (grup roll-up, aksiyon degil)
	///   Civata FSM'i: "Screw" (BoltPM objelerinde), degiskenler:
	///                   Stage (FsmInt 0..8), Tightness (FsmFloat 0..8)
	///   Olaylar: "ASSEMBLE" / "REMOVE" (buyuk harf normalize edilir)
	///   Not: ASSEMBLE/REMOVE/Screw/Tightness/Stage/Installed/BoltCheck
	///   dizgileri Assembly-CSharp.dll'de YOKTUR (dnSpy search_strings bos
	///   dondu). Bunlar sahne/sablon verisidir, kodda degil. Bu yuzden
	///   kesif runtime'da PlayMakerFSM taramasiyla yapilir (Install), DLL'de
	///   aramak dogru yontem degildir. Metot imzalari ise DLL'dedir ve
	///   yukaridaki token'larla dogrulandi.
	///
	/// Cokme ve lag korumasi:
	///   - Hook'lar sadece dirty isaretler (AssemblyPatch/BoltSyncPatch).
	///   - Gonderim SatsumaRateLimiter ile 100ms pencerede, parcalar once
	///     (peer once parcayi taksin, sonra civatasini duysun), civatalar
	///     tek BoltBatch paketinde biriktirilerek (batching) gider.
	///   - Tum apply'lar EchoGuard.SuppressScope icindedir (yanki korumasi):
	///     agdan gelen degisiklik oyuna yazilirken yerel hook'larin bunu yeni
	///     degisiklik sanip tekrar aga gondermesi kesin olarak engellenir.
	///     Sayac re-entrant'tir (ASSEMBLE -> Installed+Bolted+Tightness
	///     yazimlarinin tamami susturulur).
	///
	/// Modul: mevcut SatsumaRegistry + NetworkPacketManager + BoltSync
	/// uzerinde ince cephedir; kopya tablo tutmaz, durumu bozmaz.
	/// </summary>
	public static class SatsumaCoop {

		static readonly List<SatsumaBolt> boltBatch =
			new List<SatsumaBolt>(SatsumaRateLimiter.MAX_BOLTS_PER_FLUSH);

		static int droppedParts;
		static int droppedBolts;

		/// <summary>
		/// Her kare SteamMPBehaviour.Update icinden cagrilir.
		/// VİDALAR ÖNCE, PARÇALAR SONRA sıralamasıyla flush yapar.
		/// </summary>
		public static void Tick() {
			try {
				if (SatsumaRegistry.PartCount == 0) return;

				// Gözlemci her kare: izlenen Stage/Tightness değişkenlerini
				// okur ve "[ACT] BOLT" yazar.
				//
				// CANLI LOG KANITI: bu çağrı HİÇ YERDEN YAPILMIYORDU.
				// SatsumaRegistry.Update() yalnızca SteamMPBehaviour'in
				// BAŞLIK YORUMUNDA anılıyordu, kodda tek bir referansı
				// yoktu. Sonuç: gözlüğün ürettiği 3634 [ACT] satırın
				// 1204'ü DOOR, 80'i ENGINE, 2'si HAND idi; BOLT sıfırdı.
				// Yani vida sökme/takma HİÇ loglanmıyordu.
				//
				// Aynı çağrı bağlantı olmasa da (tek başına test) çalışır:
				// gözlemcinin ağa ihtiyacı yoktur.
				SatsumaRegistry.PollWatched();

				// DOGRULAMA (olu kod diriltme): Registry.Update() hic
				// cagrilmadigi icin TickVerify/RunVerifyPass/StepVerify HIC
				// calismiyordu -> reddedilen montaj tekrar denenmiyor,
				// parcalar eksik kaliyordu. Icinde tur/bütce korumasi var.
				try { SatsumaRegistry.TickVerify(Time.time); } catch { }

				// ERKEN-DUSEN RETRY: katilma yarısında tablo kurulmadan gelen
				// parca/vida kayitlari dustu (`tablo-hazir-degil`). Tablo
				// kurulur kurulmaz BIR KEZ tam durum yeniden istenir; host
				// butun tabloyu bastan yollar. Bayrak tek seferliktir (tablo
				// kurulduktan sonra yeni erken-dusme olamaz).
				try { SatsumaRegistry.TickSyncRetry(); } catch { }

				// Eski protokol (BoltBatch) için uzlaştırma - QueueReconcile SatsumaRegistry.ApplyBolt'tan çağrılır
				SatsumaRegistry.ReconcileBolts();

				if (!SatsumaRateLimiter.ShouldFlush()) return;
				SatsumaRateLimiter.MarkFlushed();
				if (!SteamTransport.HasRemote) return;
				
				// SIRALAMA DEĞİŞTİ: ÖNCE VİDALAR, SONRA PARÇALAR
				FlushBoltsCapped();
				FlushPartsCapped();
			}
			catch (Exception e) {
				SmpLog.Warning("satsuma coop tick failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// Parça olay/state gonderimi: ASSEMBLE olayı bekleyen VIDALI parçaların vidalarını
		/// önce gönderir, sonra parçayı gönderir. VIDASIZ parçalar vida beklemez.
		/// </summary>
		static void FlushPartsCapped() {
			int sent = 0;
			try {
				IList<SatsumaPart> table = SatsumaRegistry.Table;
				if (table == null) return;
				
				// 1. ASSEMBLE/REMOVE bekleyen VIDALI parcalarin vidalarini topla (boltless haric)
				List<SatsumaBolt> assemblyBolts = new List<SatsumaBolt>(32);
				for (int i = 0; i < table.Count; i++) {
					SatsumaPart p = table[i];
					if (p == null) continue;
					
					// VIDASIZ parça mı kontrol et - vidalarını bekleme
					if (p.IsBoltlessPart()) continue;
					
					// REMOVE DA EKLENDI (onceden yalniz ASSEMBLE idi).
					//
					// NEDEN: SIRALAMA. Assembly FSM'i karsi tarafta montaj kararini
					// civata asamelerine bakarak veriyor; ayni mantik sokmede de
					// gecerli: parca SOKULMEDEN ONCE vidalarinin gonderilmesi
					// gerekiyor. Yoksa karsi taraf parcayi sokup kendi eski
					// (sifir) asameli durumunda kalir, sonra gelen civata paketi
					// artik baglanacak bir parcaya isaret edemez ve vidalar
					// ASKIYA KALIR.
					//
					// Yalniz DIRTY olanlar toplanir: her flush zaten 160 parcayi
					// geziyor, 300+ civatanin hepsini gondermek bant genisligini
					// bosa harcar.
					bool assembling = string.Equals(p.PendingEvent, "ASSEMBLE", StringComparison.OrdinalIgnoreCase);
					bool removing = string.Equals(p.PendingEvent, "REMOVE", StringComparison.OrdinalIgnoreCase);
					if (!assembling && !removing) continue;

					for (int b = 0; b < p.Bolts.Count; b++) {
							SatsumaBolt bolt = p.Bolts[b];
							if (bolt != null && bolt.Dirty) {
							// F08_BOLT_CAP: sayim bayti `byte` -- 255'i asan bir liste
							// KALICI KAYBOLUR (asagida). Cap burada; fazlalik `Dirty`
							// kalir, bir sonraki 100 ms penceresinde gider.
							if (assemblyBolts.Count >= SatsumaRateLimiter.MAX_BOLTS_PER_FLUSH)
								break;
							assemblyBolts.Add(bolt);
							}
						}
				}
				
				// Bu vidaları hemen gönder
				if (assemblyBolts.Count > 0) {
					try {
						// F08_BOLT_CAP_SEND: gonderim sonucu ONCEDEN yok sayiliyordu.
						// Basarisiz gonderimde de tum liste temizleniyordu ->
						// o vidalar bir daha ASLA toplanmiyor.
						if (NetworkPacketManager.SendBoltBatch(assemblyBolts)) {
							for (int i = 0; i < assemblyBolts.Count; i++) {
							SatsumaBolt bolt = assemblyBolts[i];
							bolt.Dirty = false;
							bolt.DirtySince = float.NegativeInfinity;
							bolt.LastSentAt = SatsumaRegistry.Clock();
							bolt.LastSentStage = bolt.StageValue;
							bolt.LastSentTightness = bolt.TightnessValue;
							}
						}
					}
					catch (Exception e) {
						SmpLog.Warning("coop pre-assembly bolt flush failed: " + e.Message);
					}
				}

				// 2. Normal parça flush'ı
				for (int i = 0; i < table.Count; i++) {
					SatsumaPart p = table[i];
					if (p == null) continue;
					bool hasEvent = !string.IsNullOrEmpty(p.PendingEvent);
					if (!hasEvent && !p.StateDirty) continue;
					if (sent >= SatsumaRateLimiter.MAX_PARTS_PER_FLUSH) {
						droppedParts++;
						continue;
					}
					if (hasEvent) {
						string fsmName = p.PendingFsm;
						// v17: yuvali parcalarda genel ASSEMBLE yerine PARTn
						// gider; karsi taraf kendi "find correct part N"
						// durumlarini calistirip dogru VARIANT'i kurar.
						// Yuvali olmayan parca ve PARTn disindaki her olay
						// DEGISTIRILMEZ (bkz. OutgoingPartEvent).
						// Gonderim yine tek 100 ms flush penceresinden, en
						// fazla MAX_PARTS_PER_FLUSH parcayla gecer.
						string eventName = SatsumaRegistry.OutgoingPartEvent(p, p.PendingEvent);
						// ASIMETRI (düzeltildi): olay burada gönderimden ÖNCE
						// siliniyordu (`PendingFsm = null; PendingEvent = null;`
						// satırları `SendPartEvent`'in ÜSTÜNDEYDİ). Oysa hemen
						// alttaki durum dalı (196-201) aynı durumda bayrağı
						// GERİ yüklüyordu. `SendPartEvent` false döndüğünde
						// (Steam reddetti, oturum yok) olay kalıcı olarak
						// kayboluyordu ve karşı taraf montajı hiç öğrenemiyordu.
						// `OutgoingPartEvent` saf bir fonksiyon (2660-2685),
						// tekrar çağrılması güvenli.
						if (NetworkPacketManager.SendPartEvent(p.Id, fsmName, eventName)) {
							p.PendingFsm = null;
							p.PendingEvent = null;
							sent++;
						} else {
							SmpLog.Warning("satsuma: part event for '" + p.Id
								+ "' was not sent; retrying next window.");
						}
					}
					else {
						p.StateDirty = false;
						// StateWant: olayin/bool'un soyledigi GERCEK hedef.
						// `p.Installed` ham bool'dur, bool'suz parcalarda hep
						// false doner ve takili parcayi "sokuk" gonderirdi.
						if (NetworkPacketManager.SendPartState(p.Id, p.StateWant, p.IsGroupBolted(), p.HasSlots ? p.FilledSlotIndex() : (byte)0, p.DamagedValue)) sent++;
						else p.StateDirty = true;
					}
				}
			}
			catch (Exception e) {
				SmpLog.Warning("satsuma part flush failed: " + e.Message);
			}
			if (droppedParts > 0) {
				SmpLog.Warning("satsuma: part cap hit, " + droppedParts
					+ " part(s) deferred to next 100ms window.");
				droppedParts = 0;
			}
		}

		static void FlushBoltsCapped() {
			try {
				boltBatch.Clear();
				// NOT: FlushPartsCapped() ASSEMBLE/REMOVE parçaların dirty vidalarını
				// önce gönderir ve Dirty = false yapar. BoltSync.Collect aşağıda
				// yalnız Dirty==true olanları toplar, bu yüzden çift gönderim olmaz.
				// Ama FlushPartsCapped başarısız olursa (SendBoltBatch false dönerse)
				// vidalar Dirty kalmaya devam eder ve burada da toplanır — bu kasıtlı.
				BoltSync.Collect(boltBatch, SatsumaRateLimiter.MAX_BOLTS_PER_FLUSH);
				if (boltBatch.Count > 0) {
					NetworkPacketManager.SendBoltBatch(boltBatch);
				}
			}
			catch (Exception e) {
				SmpLog.Warning("satsuma bolt flush failed: " + e.Message);
			}
			finally {
				try { boltBatch.Clear(); } catch { }
			}
		}

		// ------------------------------------------------------------------
		// Incoming (uzaktan geleni oyuna uygula) - yankı korumalı + authority check
		// ------------------------------------------------------------------

		/// <summary>Canli olayi karsi tarafta oynat (ASSEMBLE/REMOVE).</summary>
		public static void ApplyPartEvent(string id, string fsmName, string eventName) {
			try {
				// GÜVENLİK KONTROLÜ BURADA DEĞİL, SatsumaRegistry.ApplyPartEvent
				// içinde ve TAM OLARAK BİR KEZ.
				//
				// Önceden burada da kontrol vardı ve ikinci çağrı
				// AuthorityManager.CheckCooldown'a takılıp eylemi sessizce
				// düşürüyordu: aynı eylem anahtarı 0.15 sn içinde ikinci kez
				// geldiği için HER gelen montaj olayı iptal ediliyordu. Canlı
				// logdaki "Satsuma ile etkileşime girince hiçbir şey görünmüyor"
				// belirtisinin asıl kaynağı buydu.
				SatsumaRegistry.ApplyPartEvent(id, fsmName, eventName);
			}
			catch (Exception e) {
				SmpLog.Warning("coop apply event '" + id + "' failed: " + e.Message);
			}
		}

		/// <summary>Parca durumunu yakin-sat (olay + bool zorlama).</summary>
		public static void ApplyPartState(string id, bool installed) {
			try {
				// Kontrol tek yerde: SatsumaRegistry.ApplyPartState.
				SatsumaRegistry.ApplyPartState(id, installed);
			}
			catch (Exception e) {
				SmpLog.Warning("coop apply state '" + id + "' failed: " + e.Message);
			}
		}

		/// <summary>Toplu civata uygular. Kontrol SatsumaRegistry.ApplyBolt içinde.</summary>
		public static void ApplyBoltBatch(BinaryReader r, byte count) {
			try {
				for (int i = 0; i < count; i++) {
					string id = r.ReadString();
					byte index = r.ReadByte();
					float tightness = r.ReadSingle();
					int stage = (sbyte)r.ReadByte();
					bool bolted = r.ReadBoolean();
					string boltPath = null;
					try {
						if (r.BaseStream.Position < r.BaseStream.Length) boltPath = r.ReadString();
					}
					catch (System.IO.EndOfStreamException) { boltPath = null; }

					// Mesafe/gereklilik kontrolü ApplyBolt'un İÇİNDE ve bir kez.
					// Burada ikinci kez çağrılıyordu; CheckCooldown aynı anahtarı
					// 0.15 sn blokladığı için paketin TÜM cıvataları sessizce
					// düşüyordu (301 cıvatalık ilk senkron dahil).
					SatsumaRegistry.ApplyBolt(id, index, tightness, stage, bolted, boltPath);
				}
			}
			catch (Exception e) {
				SmpLog.Warning("coop apply bolt batch failed: " + e.Message);
			}
		}

		/// <summary>Tek civata uygular (test / toparlama yolu).</summary>
		public static void ApplyBolt(string id, byte index, float tightness, int stage, bool bolted) {
			try {
				SatsumaRegistry.ApplyBolt(id, index, tightness, stage, bolted);
			}
			catch (Exception e) {
				SmpLog.Warning("coop apply bolt '" + id + "'#" + index + " failed: " + e.Message);
			}
		}

		/// <summary>Durum ozeti (smp status / F3 konsol icin).</summary>
		public static string Describe() {
			try {
				return "coop: " + SatsumaRegistry.PartCount + " entries, "
					+ SatsumaRegistry.BoltCount + " bolts, "
					+ SatsumaRegistry.InstalledCount() + " installed, "
					+ "pending " + SatsumaRegistry.PendingParts + " part(s)/"
					+ SatsumaRegistry.DirtyBolts + " bolt(s) +"
					+ SatsumaRegistry.PendingReconcile + " walking, "
					+ "screw " + BoltSync.ScrewEvents + " evt/"
					+ BoltSync.ScrewBursts + " burst, "
					+ "flush 100ms, echo-guard depth " + EchoGuard.Depth;
			}
			catch {
				return "coop: n/a";
			}
		}
	}
}
