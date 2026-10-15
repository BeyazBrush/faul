using System;
using System.Collections.Generic;
using Harmony;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Bolt tightness capture and outbound batching.
	///
	/// The bolt FSM ("Screw" on the GameObject "BoltPM") keeps the discrete level in
	/// <c>Stage</c> (FsmInt 0..8) and the drawn value in <c>Tightness</c> (FsmFloat 0..8).
	/// Turning a bolt with the wrench walks Stage up one step at a time while Tightness
	/// creeps towards it, which is dozens of variable writes per second per bolt.
	///
	/// Three things stop that from becoming dozens of packets per second:
	///
	/// 1. The hooks only set <see cref="SatsumaBolt.Dirty"/>. No packet is built here.
	/// 2. <see cref="Collect"/> runs once per flush (20 Hz) and sends the <em>last</em>
	///    value, not a delta, and at most once per <see cref="MIN_SEND_INTERVAL"/> per bolt.
	///    Everything written in between collapses into the next sample.
	/// 3. The values travel as one reliable <see cref="Protocol.Msg.BoltBatch"/> packet per
	///    flush instead of one packet per bolt.
	///
	/// Deltas were rejected deliberately: the remote FSM is a state machine over Stage, so
	/// a dropped or reordered delta desynchronises it permanently, whereas a repeated
	/// absolute value is idempotent.
	/// </summary>
	public static class BoltSync {

		/// <summary>Shortest interval between two packets for the same bolt.</summary>
		public const float MIN_SEND_INTERVAL = 0.1f;

		/// <summary>Changes below this are noise from the float tween.</summary>
		public const float EPSILON = 0.001f;

		/// <summary>
		/// Karşı tarafın bir cıvata için başlattığı uzlaştırma penceresi (s).
		///
		/// Gerekli çünkü TIGHTEN/UNTIGHTEN tetiklendiğinde "Screw" FSM'i
		/// Tightness'i birkaç kare boyunca AddFsmFloat ile tween ediyor. Tek bir
		/// SuppressScope yalnızca olayın tetiklendiği anı kapsar; tween sonraki
		/// karelerde yazıyor ve bu yazılar karşı tarafa geri gönderilirse iki
		/// oyuncu aynı anda cıvata çevirdiğinde değerler birbirini iter.
		/// WorldObjectSync'in RemoteOwned + RESEND_COOLDOWN deseniyle aynı fikir.
		/// </summary>
		public const float REMOTE_ECHO_WINDOW = 0.75f;

		/// <summary>
		/// SESSİZLİK EŞİĞİ (s): en son yerel yazımdan bu kadar süre geçmeden
		/// cıvata GÖNDERİLMEZ.
		///
		/// NEDEN GEREKLİ: "Screw" FSM'i `Tightness`'i `AddFsmFloat` ile
		/// kare kare tween ediyor. Yani bir fare tekerleği sürüklemesi
		/// boyunca değer HER KARE değişiyor ve eski sürüm 0.1 s'lik
		/// MIN_SEND_INTERVAL ile saniyede 10 ara değer paketi gönderiyordu.
		/// O ara değerlerin karşı tarafta hiçbir işi yok: ReconcileBolts
		/// hedef KADEME yürüyor, ara Tween'i değil.
		///
		/// Yani bu eşik sadece trafik azaltmıyor, yanlış da göndermiyor:
		/// tekerlek bırakıldığında tek bir paket, nihai değerle gider.
		///
		/// 0.12 s: tween kareleri (Wait + AddFsmFloat) bu sürede tamamlanıyor,
		/// ama bir oyuncu 0.12 s'den kısa aralıklarla iki kademe çevirip
		/// bırakırsa paketler birleşir — ki bu da doğru olan, çünkü hedef
		/// kademe nihai değerdir.
		/// </summary>
		public const float BOLT_QUIET_PERIOD = 0.12f;

		/// <summary>
		/// Sessizlik kuralının geçersiz kılındığı en uzun süre (s).
		///
		/// Bir FSM'in kaldığı sürece yazması teorik olarak mümkün ( takılı bir
		/// Wait, ya da tween döngüsü). O durumda LastLocalWriteAt sürekli
		/// tazelenir, cıvata hiç gönderilmez ve kullanıcı bunu "vida
		/// senkronu bozuk" sanar. 1.0 s sonunda kural geçersiz kılınır:
		/// BOLT_QUIET_PERIOD bir gecikmeyi azaltma yöntemidir, bir
		/// güvence değil.
		/// </summary>
		public const float BOLT_MAX_QUIET = 1.0f;

		// ------------------------------------------------------------------
		// TIGHTEN / UNTIGHTEN gözlemcisi (TANIM DIŞI)
		// ------------------------------------------------------------------
		//
		// SAHNE KANITI: "Screw" FSM'i TIGHTEN ve UNTIGHTEN bildiriyor ve fare
		// tekerleğinin her adımında kendine bu olayı gönderiyor.
		//
		// Bu yol AĞ YOLU DEĞİL. Ağ yolu zaten var ve çalışıyor: FsmInt.set_Value
		// (Stage) ve FsmFloat.set_Value (Tightness) Postfix'leri, ardından
		// Collect'in sessizlik + hız sınırları. Değer yazımı, olaydan daha
		// güvenilir bir sinyaldir: FSM TIGHTEN'i işleyip Stage'i IntClamp ile
		// sınıra dayandırıp hiç değiştirmezse olay görülür ama değer değişmez.
		//
		// O ZAMAN NEDEN BURADA? İki neden:
		//   1) Tıkışkanlık teşhisi. Canlı log (proto 5) "probe: bolt-seen 0"
		//      ve "[ACT] BOLT" hiç yazılmıyor diye raporluyordu; kancanın
		//      gerçekten tetiklenip tetiklenmediği ancak buradan okunuyor.
		//   2) Sürükleme ölçümü. Kaç olayın tek bir oyuncu hareketi olduğunu
		//      bilmek, BOLT_QUIET_PERIOD'un doğru seçilip seçilmediğini
		//      gösteriyor.
		public static int ScrewEvents;      // toplam TIGHTEN+UNTIGHTEN
		public static int ScrewBursts;      // 0.5 s'den uzun aralıkla ayrılan sürüklemeler
		public static int LastBurstEvents;  // son sürüklemedeki olay sayısı

		/// <summary>Bir sürüklemeyi yeni saymak için gereken boşluk (s).</summary>
		public const float BURST_GAP = 0.5f;
		static float lastScrewEventAt = float.NegativeInfinity;
		static int burstEvents;

		/// <summary>
		/// TIGHTEN / UNTIGHTEN sayacı. AssemblyPatch'in HÂLIHANZIRDA yamalı
		/// olduğu Fsm.Event kancalarından çağrılır; yeni Harmony yaması
		/// EKLENMEZ (patched count 13'te kalır).
		///
		/// SICAK YOL: oyunun en sıcak noktası. Sıralama: bağlantı, sonra
		/// uzunluk (TIGHTEN=7, UNTIGHTEN=9), sonra tam eşitlik, en son
		/// FSM adı. FSM adına bakmadan önce olay adını eliyoruz.
		/// </summary>
		public static void OnScrewEvent(PlayMakerFSM fsm, string eventName) {
			if (fsm == null) return;
			if (eventName == null) return;
			// CANLI LOG KANITI (12:17): buradaki "!SteamTransport.HasRemote"
			// şartı sayacı da öldürüyordu. Sayaç YALNIZ tanı içindir (tek
			// başına testte "kanca tetikleniyor mu" sorusunun cevabı), ağa
			// paket göndermiyor; bu yüzden bağlantı şartı olmamalı.
			// Kullanıcı tek başına "vidayı çevirdim, log yok" dediğinde
			// cevap göremez hale geliyordu.
			if (SteamMP.EchoGuard.Suppressed) return;   // karşı tarafın kendi adımı
			int len = eventName.Length;
			if (len < 7 || len > 9) return;
			bool tighten = string.Equals(eventName, "TIGHTEN", StringComparison.Ordinal);
			if (!tighten && !string.Equals(eventName, "UNTIGHTEN", StringComparison.Ordinal)) return;
			try {
				string fsmName;
				try { fsmName = fsm.FsmName; } catch { return; }

				// GERİ ALINAN GENİŞ FİLTRE. 12:27 log kanıtı: TIGHTEN /
				// UNTIGHTEN yalnız vidalara özgü değil. "Check" (Raycast
				// nesnesinde) ve "Rotate object" (Hand nesnesinde) de aynı
				// olayları gönderiyor; bunlar vida değil, kullanıcının
				// nesne taşıma/nişan alma hareketleri. Filtreyi
				// "Screw" dışına açınca [ACT] BOLT seli boğuldu:
				// oturumdaki 200+ satırın neredeyse tamamı
				// "NON-Screw fsm 'Rotate object' @ 'Hand'" idi.
				//
				// DOĞRU FİLTRE: FSM'in kendi adı Screw, ya da nesne adı
				// vida gibi (StartsWith("Bolt") / "sparkplug" içerir).
				// Bujiler "Data" FSM'idir ama nesnesi "SparkplugN" olduğu
				// için ikinci kapıdan geçer.
				bool looksLikeBolt = string.Equals(fsmName, "Screw", StringComparison.Ordinal)
					|| SatsumaRegistry.IsBoltLikeObject(fsm.gameObject);
				if (!looksLikeBolt) return;

				string fsPath = "?";
				try { fsPath = Wire.PathOf(fsm.transform); } catch { }
				SmpLog.Log("[SCREW] '" + eventName + "' on FSM '" + fsmName + "' @ " + fsPath);

				float now = SatsumaRegistry.Clock();
				ScrewEvents++;
				if (now - lastScrewEventAt > BURST_GAP) {
					if (lastScrewEventAt > float.NegativeInfinity) {
						ScrewBursts++;
						LastBurstEvents = burstEvents;
					}
					burstEvents = 0;
					lastScrewEventAt = now;
				}
				burstEvents++;

				// "Screw" FSM'i olmayan ama nesnesi vida olan yol (bujiler).
				// 'smp status' hangi yolun çalıştığını gösterebilsin diye
				// ayrı sayılır: hepsi sıfırken hangisinin eksik olduğu
				// belli olsun.
				if (!string.Equals(fsmName, "Screw", StringComparison.Ordinal)) {
					NonScrewScrewEvents++;
				}
			}
			catch { }
		}

		/// <summary>TIGHTEN/UNTIGHTEN gönderen "Screw" dışı FSM sayısı (teşhis).</summary>
		public static int NonScrewScrewEvents;

		public static void ResetNonScrew() { NonScrewScrewEvents = 0; }

		/// <summary>Sıfırlama (dünya çıkışı / rescan).</summary>
		public static void ResetCounters() {
			ScrewEvents = 0;
			ScrewBursts = 0;
			LastBurstEvents = 0;
			burstEvents = 0;
			lastScrewEventAt = float.NegativeInfinity;
		}

		/// <summary>Called from the FsmInt.Value hook.</summary>
		public static void OnStageWrite(FsmInt variable) {
			if (variable == null) return;
			// Clock variables are FsmInts too and share this hook; they are routed first so
			// the bolt table lookup below cannot shadow them.
			try { WorldSync.OnIntWrite(variable); } catch { }
			// Gözlemci: gerçek cıvata tablosunda olmayan her Stage/Tightness
			// değişkeni (bkz. SatsumaRegistry.WatchBoltLevels).
			try { SatsumaRegistry.TraceWatchedStage(variable); } catch { }
			if (SatsumaRegistry.Suppressed) return;
			SatsumaBolt bolt = SatsumaRegistry.LookupBoltStage(variable);
			if (bolt == null) return;
			// EYLEM GÜNLÜĞÜ: kademe değişimi = oyuncu bir cıvatayı çevirdi.
			// "Kullanıcı eylemi" olduğu için bağlantı aranmaz: tek başına
			// test ederken de görünür olsun diye Suppressed kontrolü
			// yeterli (karşı tarafın uyguladığı değer yazılmaz).
			TraceStage(bolt);
			MarkLocalWrite(bolt);
			if (!SteamTransport.HasRemote) return;
			SatsumaRegistry.OnBoltChanged(bolt);
		}

		/// <summary>
		/// İnsan kaynaklı yazımı işaretler: sessizlik sayacı buradan çalışıyor.
		///
		/// Yalnızca insan yazımları sayılır. Karşı tarafın kendi
		/// ReconcileBolts adımı SuppressScope içindedir, AMA `Screw` FSM'i
		/// Tightness'i tween etmeye birkaç kare daha devam eder; bu yazılar
		/// pencere dışında kalırdı. REMOTE_ECHO_WINDOW içindeki yazımlar
		/// sayılmaz, aksi halde karşı taraftaki cıvata sürekli "oyuncu
		/// sıkıyor" görünür ve hiçbir paket gönderilmez.
		/// </summary>
		static void MarkLocalWrite(SatsumaBolt bolt) {
			if (bolt == null) return;
			try {
				float now = SatsumaRegistry.Clock();
				if (now - bolt.RemoteSetAt < REMOTE_ECHO_WINDOW) return;
				bolt.LastLocalWriteAt = now;
			}
			catch { }
		}

		/// <summary>
		/// Kademe değişimini loglar. Sıcak yol: yalnızca tam sayı kademe
		/// değişimi yazılır, Tightness'in kademeler arası tween'i yazılmaz.
		/// </summary>
		static void TraceStage(SatsumaBolt bolt) {
			try {
				int stage = bolt.StageValue;
				// TEŞHİS SAYACI: kanca gerçekten tetikleniyor mu? Canlı log
				// kanıtı (23:03) bunu ayırt etmek için gerekli: [ACT] BOLT
				// yazılmıyordu ama bunun nedeni belirsizdi.
				if (!SmpTrace.BoltCounted) {
					SmpTrace.BoltsSeen++;
					if (SmpTrace.BoltsSeen >= 4) SmpTrace.BoltCounted = true;
				}
				if (stage == bolt.TracedStage) return;
				int from = bolt.TracedStage;
				bolt.TracedStage = stage;
				if (from < 0) return;   // ilk ölçüm, henüz eylem değil
				SmpTrace.Bolts++;

				// PARÇA SEVİYESİ LOG: Bu vida hangi parçaya ait? Parça logla.
				// Installable olmayan parçalar (sadece vida kabı) için de logla.
				if (bolt.Part != null && bolt.Part.Bolts.Count > 0) {
					TracePartBoltChange(bolt.Part, stage > from ? "tightened" : "loosened", from, stage, bolt.Index);
				}
			}
			catch { }
		}

		/// <summary>
		/// Parça seviyesinde vida değişikliğini loglar (bir parça için birden fazla vida değişirse tek log).
		/// </summary>
		static readonly Dictionary<string, float> lastPartBoltLog = new Dictionary<string, float>(StringComparer.Ordinal);
		static void TracePartBoltChange(SatsumaPart part, string action, int fromStage, int toStage, byte boltIndex) {
			if (part == null) return;
			float now = SatsumaRegistry.Clock();
			string key = part.Id;
			float last;
			if (lastPartBoltLog.TryGetValue(key, out last) && now - last < 0.5f) return; // 500ms debounce per part
			lastPartBoltLog[key] = now;

			int tightCount = 0;
			for (int i = 0; i < part.Bolts.Count; i++) {
				if (part.Bolts[i].StageValue >= 8) tightCount++;
			}

			// Tüm vidalar sıkıldıysa veya hepsi gevşetildiyse ek log
			if (tightCount == part.Bolts.Count && part.Bolts.Count > 0) {
			}
			else if (tightCount == 0 && part.Bolts.Count > 0) {
				// Sadece en az bir vida 0'dan büyükse (gevşetme eylemi)
				bool anyNonZero = false;
				for (int i = 0; i < part.Bolts.Count; i++) {
					if (part.Bolts[i].StageValue > 0) { anyNonZero = true; break; }
				}
				if (anyNonZero) {
				}
			}
		}

		/// <summary>Called from the FsmFloat.Value hook. Covers the bolt and the bolt group.</summary>
		/// <summary>Called from the FsmFloat.Value hook. Covers the bolt and the bolt group.</summary>
		public static void OnTightnessWrite(FsmFloat variable) {
			if (variable == null) return;
			try { WorldSync.OnFloatWrite(variable); } catch { }
			// Gözlemci (bkz. SatsumaRegistry.WatchBoltLevels).
			try { SatsumaRegistry.TraceWatchedTightness(variable); } catch { }
			if (SatsumaRegistry.Suppressed) return;
			SatsumaBolt bolt = SatsumaRegistry.LookupBoltTightness(variable);
			if (bolt != null) {
				// CANLI LOG KANITI (23:12): "probe: bolt-seen 0". Yani bu
				// build'de cıvataların FsmInt 'Stage' değişkeni YOK; tek
				// değişkenleri FsmFloat 'Tightness'. Kademe oradan türetilir.
				// Bkz. TraceTightness.
				TraceTightness(bolt);
				MarkLocalWrite(bolt);
				if (!SteamTransport.HasRemote) return;
				SatsumaRegistry.OnBoltChanged(bolt);
				return;
			}
			if (!SteamTransport.HasRemote) return;
			// Part level roll-up (FSM "BoltCheck").
			// v15: bu deger ONCEDEN HESAPLANIP ATILIYORDU. Simdi yaziliyor,
			// cunku "hepsi sikildi" karari oradan cikiyor:
			//   BoltCheck/Tightness -> Data/Bolted -> parca "sikili" sayilir
			// Data/Bolted de Removal FSM'inin REMOVE uretiminde
			// BoolAnyTrue(Installed, Bolted) girdisi (dumper kaniti).
			SatsumaRegistry.ConvergeGroupTightness(variable);
		}

		/// <summary>
		/// Tightness'ten türetilmiş kademe değişimi.
		///
		/// NEDEN AYRI FONKSİYON: Stage (FsmInt) bu build'de yok
		/// ("bolt-seen 0"), cıvata yalnızca Tightness ile temsil ediliyor.
		/// Değer 0..8 arası kademe olduğu için yuvarlanıp tam sayıya
		/// çevrilir; tween sırasında yarım kademeler eşiği geçmediği için
		/// günlüge girmez.
		/// </summary>
		static void TraceTightness(SatsumaBolt bolt) {
			try {
				float t = bolt.TightnessValue;
				int stage = (int)Mathf.Round(t);
				if (!SmpTrace.BoltCounted) {
					SmpTrace.BoltsSeen++;
					if (SmpTrace.BoltsSeen >= 4) SmpTrace.BoltCounted = true;
				}
				if (stage == bolt.TracedStage) return;
				int from = bolt.TracedStage;
				bolt.TracedStage = stage;
				if (from < 0) return;
				SmpTrace.Bolts++;

				// PARÇA SEVİYESİ LOG: Bu vida hangi parçaya ait? Parça logla.
				// Installable olmayan parçalar (sadece vida kabı) için de logla.
				if (bolt.Part != null && bolt.Part.Bolts.Count > 0) {
					TracePartBoltChange(bolt.Part, stage > from ? "tightened" : "loosened", from, stage, bolt.Index);
				}
			}
			catch { }
		}

		/// <summary>
		/// Fills <paramref name="into"/> with dirty bolts whose value actually changed and
		/// that are past their send interval. The last value wins: the bolt stays dirty
		/// until it can be sent, so nothing is ever lost.
		/// </summary>
		public static int Collect(List<SatsumaBolt> into, int max) {
			if (into == null || max <= 0) return 0;
			float now = SatsumaRegistry.Clock();
			int found = 0;
			int partCount = SatsumaRegistry.PartCount;
			for (int i = 0; i < partCount; i++) {
				SatsumaPart part = SatsumaRegistry.PartAt(i);
				for (int b = 0; b < part.Bolts.Count; b++) {
					SatsumaBolt bolt = part.Bolts[b];
					if (!bolt.Dirty) continue;
					// Karşı tarafın FSM'i şu anda bu cıvatayı kendi kademesine
					// yürütüyor. O tween'in yazıları gönderilmez; pencere bitince
					// değer zaten hedefe ulaşmış olur ve LastSent* ile eşleşir.
					if (now - bolt.RemoteSetAt < REMOTE_ECHO_WINDOW) continue;
					// SESSİZLİK: fare tekerleği hâlâ dönüyorsa bekle. Tekerlek
					// durduğunda TEK paket, nihai değerle gider. Ara tween
					// değerleri gönderilmez — ReconcileBolts onları zaten
					// kullanmıyor, yalnızca hedef kademeyi yürütüyor.
					//
					// KAÇIŞ YOLU: bir FSM sonsuza kadar yazarsa cıvata hiç
					// gönderilmez ve kullanıcı bunu kırık senkron sanar.
					// BOLT_MAX_QUIET aşılınca sessizlik kuralı geçersiz
					// kılınır, çünkü BOLT_QUIET_PERIOD bir İYİLEŞTİRMEDİR,
					// sınırsız gecikme değil.
					if (now - bolt.LastLocalWriteAt < BOLT_QUIET_PERIOD
					    && now - bolt.DirtySince < BOLT_MAX_QUIET) continue;
					int stage = bolt.StageValue;
					float tight = bolt.TightnessValue;
					if (stage == bolt.LastSentStage
					    && (float.IsNaN(bolt.LastSentTightness) || Math.Abs(tight - bolt.LastSentTightness) < EPSILON)) {
						bolt.Dirty = false; // the FSM wrote the value we already sent
						bolt.DirtySince = float.NegativeInfinity;
						continue;
					}
					if (now - bolt.LastSentAt < MIN_SEND_INTERVAL) continue; // keep dirty, send next flush
					bolt.LastSentAt = now;
					bolt.LastSentStage = stage;
					bolt.LastSentTightness = tight;
					bolt.Dirty = false;
					bolt.DirtySince = float.NegativeInfinity;
					into.Add(bolt);
					if (++found >= max) return found;
				}
			}
			return found;
		}

		/// <summary>
		/// Deterministic page through every bolt for the initial sync. Index based, so it is
		/// independent of what is dirty, and it marks the values as sent so the live poller
		/// does not immediately re-send them.
		/// </summary>
		/// <returns>false when the table is exhausted.</returns>
		public static bool CollectAll(List<SatsumaBolt> into, int max, int startIndex) {
			if (into == null || max <= 0) return false;
			float now = SatsumaRegistry.Clock();
			int index = 0;
			int partCount = SatsumaRegistry.PartCount;
			for (int i = 0; i < partCount; i++) {
				SatsumaPart part = SatsumaRegistry.PartAt(i);
				for (int b = 0; b < part.Bolts.Count; b++) {
					if (index++ < startIndex) continue;
					SatsumaBolt bolt = part.Bolts[b];
					bolt.LastSentAt = now;
					bolt.LastSentStage = bolt.StageValue;
					bolt.LastSentTightness = bolt.TightnessValue;
					bolt.Dirty = false;
					into.Add(bolt);
					if (into.Count >= max) return true;
				}
			}
			return false;
		}
	}

	/// <summary>
	/// Bolt level, the value the FSM's own state machine compares against 1..8.
	///
	/// POSTFIX, Prefix DEĞİL — canlı log kanıtı (22:55): "[ACT] BOLT" hiç
	/// yazılmıyordu. Prefix'te set_Value henüz çalınmadığı için
	/// `variable.Value` ESKİ kademeyi döndürüyor; TraceStage karşılaştırması
	/// her zaman eşit çıkıyor ve eylem günlüğü hiçbir zaman tetiklenmiyordu.
	/// Dirty işaretlemesi değer okumadığı için ağ davranışı değişmedi.
	/// </summary>
	[HarmonyPatch(typeof(FsmInt), "set_Value")]
	public static class BoltIntHook {
		static void Postfix(FsmInt __instance) {
			try { BoltSync.OnStageWrite(__instance); } catch { }
		}
	}

	/// <summary>
	/// Drawn tightness of a bolt, and the part level roll-up.
	///
	/// POSTFIX, Prefix DEĞİL — canlı log kanıtı (23:12): "probe: bolt-seen 0,
	/// hand-found 0". Prefix'te set_Value henüz çalınmadığı için
	/// Tightness'in ESKİ değeri okunuyor ve kademe değişimi hiç yakalanmıyordu.
	/// Zaten bu build'de cıvataların Stage (FsmInt) değişkeni yok, tek
	/// değişken Tightness; yani doğru değeri ancak Postfix ile görürüz.
	/// </summary>
	[HarmonyPatch(typeof(FsmFloat), "set_Value")]
	public static class BoltFloatHook {
		static void Postfix(FsmFloat __instance) {
			try { BoltSync.OnTightnessWrite(__instance); } catch { }
		}
	}
}