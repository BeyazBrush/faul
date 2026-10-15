using System;
using Harmony;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// BUJİ OLAY LİSTESİ: Bujiler (Sparkplug*) ASSEMBLE/REMOVE olaylarını
	/// klasik parça yolundan DOKUNDURMUYOR (aynı 'Data' FSM stili), yani
	/// PART/BOLT logunda görünmüyorlardı. Sparkplug adında bir nesnede
	/// geçen HER olay burada 'kesintisiz' loglanır.
	/// </summary>
	public static class SparkPlugLog {
		/// <summary>
		/// v15: varsayilan olarak KAPALI -- saf iz (trace).
		///
		/// Check, HER PlayMaker olayina takilan bir Harmony kancasidir ve
		/// yalnizca adi "sparkplug" iceren nesneleri gecer. Sahne yuklenirken
		/// cogunlukla PlayMaker BASLATMA gurultusunu yakaliyor:
		/// 'LEVEL LOADED' / 'FINISHED' (Database/PartsStatus/Sparkplug1..4,
		/// STORE/.../Sparkplugs) -- multiplayer ile ilgisi yok.
		///
		/// Canli log (21:25 oturumu): 299 satirin 23'u (%7.7) buydu.
		/// Buji olaylarini gercekten takip etmek icin 'smp verbose'.
		/// </summary>
		public static void Check(PlayMakerFSM fsm, string eventName) {
			if (!Telemetry.Verbose) return;
			if (fsm == null || string.IsNullOrEmpty(eventName)) return;
			if (fsm.gameObject == null || string.IsNullOrEmpty(fsm.gameObject.name)) return;
			// v22: alt dize -> SatsumaRegistry'nin TAM AD kurali.
			// Alt dize, 4 `trigger_sparkplugN` Assembly tetikleyicisi ve
			// `Sparkplugs` magaza rafini da yakalardi; bunlar buji degil.
			if (SatsumaRegistry.LooksLikeSparkPlug(fsm.gameObject)) {
				string path = "?";
				try { path = Wire.PathOf(fsm.transform); } catch { }
				SmpLog.Log("[SPARK] event='" + eventName + "' go='" + fsm.gameObject.name
					+ "' fsm='" + SafeFsmName(fsm) + "' @ " + path);
			}
		}

		static string SafeFsmName(PlayMakerFSM fsm) {
			try { return string.IsNullOrEmpty(fsm.FsmName) ? fsm.name : fsm.FsmName; } catch { return "?"; }
		}
	}

	/// <summary>
	/// dnSpy ile doğrulanan PlayMaker olay yolları ve doğru hook noktası.
	///
	/// Static analiz (PlayMaker.dll, Assembly-CSharp.dll):
	///
	///   HutongGames.PlayMaker.Actions.SendEvent.OnEnter()
	///     -> Fsm.Event(FsmEventTarget, FsmEvent)   token 0x060001ED   <-- OYUNUN ANA YOLU
	///   PlayMakerFSM.SendEvent(string)
	///     -> Fsm.Event(string)                     token 0x060001EE   <-- C# tarafının yolu
	///   PlayMakerFSM.ChangeState(string)  [Obsolete]
	///     -> Fsm.Event(string)                     token 0x060001EE
	///   PlayMakerFSM.SendRemoteFsmEvent(string) [RPC]
	///     -> Fsm.Event(string)                     token 0x060001EE
	///
	/// Neden önemli: PlayMaker'ın "Send Event" action'ı PlayMakerFSM.SendEvent'i
	/// ÇAĞIRMAZ, doğrudan Fsm.Event(FsmEventTarget, FsmEvent) çağırır. Yani yalnızca
	/// PlayMakerFSM.SendEvent(string) yamalıysak, Satsuma'nın ASSEMBLE/REMOVE olaylarının
	/// büyük kısmını kaçırırız ve sadece Installed bool yakalama yoluna kalırız.
	/// Bu yüzden üç giriş noktasının üçü de yamalıdır.
	///
	/// FsmEventTarget çözümlemesi (dnSpy ile doğrulandı):
	///   target          : Self | GameObject | GameObjectFSM | FSMComponent | BroadcastAll | HostFSM | SubFSMs
	///   excludeSelf     : FsmBool
	///   gameObject      : FsmOwnerDefault
	///   fsmName         : FsmString
	///   sendToChildren  : FsmBool
	///   fsmComponent    : PlayMakerFSM   <-- hedef burada çözülmüş olarak gelir
	///
	/// Maliyet: Fsm.Event her FSM'in her olayı için çağrılır, yani oyunun en sıcak
	/// noktalarından biri. Bu yüzden gövde yalnızca null kontrolü, bir enum karşılaştırması
	/// ve iki string karşılaştırması yapar; tahsis, sözlük veya LINQ yoktur. Filtre
	/// reddedilen olaylar burada çıkarken oluşmaz.
	/// </summary>
	public static class AssemblyPatch {

		/// <summary>
		/// Gerçekten "bu parça değişti" demek olan olaylar.
		///
		/// HOTINSTALL bilinçli olarak yok: o build'de hiçbir FSM onu bildirmiyor, ve
		/// bildirilmeyen bir olayı iletmek karşı tarafta sessizce yutulur; sadece
		/// filtreyi olduğundan kesin gösterir. Installed bool hook'u o boşluğu kapatır.
		/// </summary>
		// v16 (4): `INSTALLED`/`NOTINSTALLED` eklendi.
		// DOKUM KANITI: Satsuma'daki 52 `Assemble` (kablolama) nesnesinin
		// 51'i bu ikisini GLOBAL GECIS olarak bildiriyor ve hicbiri yerel
		// `Installed` bildirmiyor. Ornek
		// `Wiring/Triggers/HeadlightLeft/ConnectorLight|Assemble` dizisi:
		//   [3] ASSEMBLE   [63][71] CLOSELOOP   [131..136]
		//   ASSEMBLE, CLOSELOOP, INSTALLED, LOOP, NOTINSTALLED, RESETWIRING
		// Yani kablo takma karari `GetButtonDown(<KONEKTOR>)` -> `ASSEMBLE`
		// zinciridir ve FSM kendi bayragini
		//   SetFsmBool db_ThisPart, Data, Installed
		// ile yazar.
		//
		// BU LISTE IKI YERDE MIRRAZ OLMALIDIR: burasi OLAYI YAKALAR,
		// `SatsumaRegistry.PART_EVENTS` (ayni deger) isleme tarafinda
		// kullanilir. Ikisi ayrilirsa ya olay kaybolur ya yanlis yere gider.
		public static readonly string[] PART_EVENTS = {
			"ASSEMBLE", "REMOVE", "DISASSEMBLE", "INSTALLED", "NOTINSTALLED",
			// v17: cok yuvali parcalarin VARIANT secim olaylari (PART1..PART7).
			//
			// DOKUM KANITI (l2/fsms.json, kok "SATSUMA(557kg, 248)"): bu
			// adlar yalnizca alti trigger'in `Assembly` FSM'inde gorunuyor ve
			// global gecis blogunda ASSEMBLE/DISASSEMBLE/LOOP'un yaninda
			// duruyor:
			//   MiscParts/trigger_radiator   PART1, PART2
			//   Dashboard/trigger_gear_stick PART1, PART2
			//   Body/trigger_grille          PART1..PART7
			//   Body/trigger_spoiler         PART1..PART7
			//   Body/trigger_hood            PART1..PART5 (+HOOD1,HOOD2)
			//   .../trigger_steering_wheel   PART1..PART5
			// yani bunlar yuvalari (`db_Radiator1/2`, `db_WheelGT/Rally/
			// Sport/Stock`, `db_ThisPart1/2`) secen "find correct part"
			// durumlarinin GECIS HEDEFIleridir.
			//
			// CIBI `PART` KASITLI OLARAK YAZILMAZ: 88 `Assembly` FSM'i
			// yerel bir `TriggerEvent` (`sendEvent = "PART"` + `collideTag`)
			// uretiyor; bu bir GLOBAL GECIS DEGILDIR. Eklenirse yuzlerce
			// nesneden gelen yerel olaylar ag hattina birakilir.
			"PART1", "PART2", "PART3", "PART4", "PART5", "PART6", "PART7" };

		/// <summary>Parça davranışı tanımlayan FSM adları. BoltCheck bir denetleyicidir, aktör değil.</summary>
		public static bool IsPartFsm(string fsmName) {
			return fsmName == "Assembly" || fsmName == "Removal" || fsmName == "Assemble";
		}

		/// <summary>Küçük harfli "assemble" gibi varyantları da yakalar.</summary>
		///
		/// v17: `PART1..PART7` de yukaridaki diziden gelir, yani
		/// `TracePartEvent` onlari EK BIR KOSUL olmadan yakalar. CIBI
		/// `PART` dizide olmadigi icin kabul edilmez.
		public static bool IsPartEvent(string eventName) {
			if (string.IsNullOrEmpty(eventName)) return false;
			for (int i = 0; i < PART_EVENTS.Length; i++) {
				if (string.Equals(PART_EVENTS[i], eventName, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		// ------------------------------------------------------------------
		// Giriş noktası 1: PlayMakerFSM.SendEvent(string)  ->  Fsm.Event(string)
		// ------------------------------------------------------------------

		/// <summary>
		/// Parça takma/sökme ve eşya alma/bırakma olaylarını Interaction katmanına
		/// da bildirir.
		///
		/// Neden ayrı yol gerekiyor: Satsuma'nın kendi PartEvent akışı parçayı
		/// HEDEFTE FSM'i oynatarak monte eder. Interaction katmanı ise parçanın
		/// transform'ını ve sahipliğini de bilmek zorunda (PartMountState /
		/// AuthorityClaim), o yüzden aynı olayı PartSync'e ayrıca veriyoruz.
		///
		/// Aynı sebeple eşya olayları da akıtılıyor: CarrySync'in gönderim tarafı
		/// SetCarrying/SetDropping ile başlar, o da buradan besleniyor.
		///
		/// Yankı: EchoGuard.Suppressed iken çağrılmaz, yoksa karşı taraftan
		/// gelen olay yerelde yeniden üretilip geri gönderilirdi.
		/// </summary>
		static void NotifyInteraction(PlayMakerFSM fsm, string eventName) {
			if (SatsumaRegistry.Suppressed) return;
			try { InteractionManager.OnFsmEvent(fsm, eventName); } catch { }
		}

		/// <summary>C# tarafından ve ChangeState/RPC yollarından gelen olaylar.</summary>
		public static void OnSendEvent(PlayMakerFSM fsm, string eventName) {
			if (fsm == null || string.IsNullOrEmpty(eventName)) return;

			// Eşya olayları ayrı ve daha önce ele alınır: ASSEMBLE/REMOVE değildirler ve
			// parça filtresi onları elerdi.
			try { ItemSync.OnFsmEvent(fsm, eventName); } catch { }

			// Buji teşhisi: Sparkplug adında nesnede HER olay loglanır.
			try { SparkPlugLog.Check(fsm, eventName); } catch { }

			// Kapı olayları da buradan. OPEN/CLOSE ASSEMBLE/REMOVE değildir, yani
			// aşağıdaki parça filtresi onları elerdi. Kıyas, uzunluğa göre
			// yapılır (bkz. WorldObjectSync.OnFsmEvent): oyunun en sıcak yolu.
			if (!SatsumaRegistry.Suppressed) {
				try { WorldObjectSync.OnFsmEvent(fsm, eventName); } catch { }
				// Vida sıkma (TIGHTEN/UNTIGHTEN) gözlemcisi. YENİ YAMA YOK:
				// bu kanca zaten yamalı, yalnızca yönlendirme ekleniyor
				// ("patched count" 13'te kalır).
				try { BoltSync.OnScrewEvent(fsm, eventName); } catch { }
				// Radyo/CD OLAYLARI. Degisken senkronu yetmiyor:
				// oyun sesi AKSIYONLA kapatiyor - RADIO/RadioChannels'in
				// Off durumu AudioSource.mute + EnableFSM kullaniyor, yani
				// sessiz durumu HICBIR degiskene yazilmiyor. Kanal degisimi de
				// CDPlayer/Update'e NEXT/PREVIOUS/RADIO_UPDATE OLAYI olarak
				// gidiyor. Olayi da iletmezsek ses acma/kapatma ve kanal
				// degistirme karsi tarafa gitmez.
				try { RadioSync.OnFsmEvent(fsm, eventName); } catch { }
			}

			if (SatsumaRegistry.Suppressed) return;

			// PARÇA YOLU BAĞLANTIDAN BAĞIMSIZ.
			//
			// CANLI LOG KANITI: bu satırda "!SteamTransport.HasRemote"
			// vardı ve PART yolunun ÖNÜNDE duruyordu. Sonuç: tek başına
			// test ederken hiçbir parça takma/sökme loglanmıyordu
			// (1436 [ACT] satırın 0'ı PART idi) — oysa BOLT/DOOR/ENGINE
			// loglanıyordu, çünkü onların yolu bu şarttan ÖNCE çalışıyor.
			//
			// Doğru bölme: LOGLAMA bağlantı istemez, GÖNDERİM ister.
			// Gönderim şartı artık SatsumaRegistry.OnTrackedEvent'in
			// içinde (logladıktan sonra).
			TracePartEvent(fsm, eventName);

			if (!SteamTransport.HasRemote) return;

			NotifyInteraction(fsm, eventName);
		}

		/// <summary>
		/// ASSEMBLE/REMOVE olayını izlenen parçaya bağlar. FSM adı ve olay
		/// adı kontrolü dahil; bağlantı şartı YOK (yalnız loglama için de
		/// çalışır, gönderim OnTrackedEvent içinde ayrıca korunur).
		/// </summary>
		static void TracePartEvent(PlayMakerFSM fsm, string eventName) {
			if (!IsPartEvent(eventName)) return;

			string fsmName;
			try { fsmName = fsm.FsmName; } catch { return; }
			if (string.IsNullOrEmpty(fsmName) || !IsPartFsm(fsmName)) return;

			GameObject go;
			try { go = fsm.gameObject; } catch { return; }
			if (go == null) return;

			SatsumaRegistry.OnTrackedEvent(fsm, fsmName, eventName.ToUpperInvariant());
		}

		// ------------------------------------------------------------------
		// Giriş noktası 2: Fsm.Event(FsmEventTarget, FsmEvent)  [OYUNUN ANA YOLU]
		// ------------------------------------------------------------------

		/// <summary>
		/// PlayMaker "Send Event" action'ının gerçekten çağırdığı metot.
		///
		/// <paramref name="host"/> olayı gönderen FSM'dir (action'ın sahibi), hedef
		/// FsmEventTarget içinden gelir. Hedef bileşen null ise çözümlenmemiş demektir
		/// (GameObject+isim ile hedeflenmiş olabilir); bu durumda olay düşürülür, çünkü
		/// yanlış FSM'ye uygulamak part tablosunu bozar.
		/// </summary>
		public static void OnTargetedEvent(Fsm host, FsmEventTarget target, FsmEvent fsmEvent) {
			string eventName = null;
			try { eventName = fsmEvent != null ? fsmEvent.Name : null; } catch { return; }
			OnTargetedEventByName(host, target, eventName);
		}

		/// <summary>
		/// Giriş noktası 4: Fsm.Event(FsmEventTarget, string) token 0x060001EC.
		/// dnSpy taraması (Fsm üye listesi) bunu buldu: hedefli ama string-adlı
		/// olay yolu. Aynı gövde, ad doğrudan gelir.
		/// </summary>
		public static void OnTargetedEventByName(Fsm host, FsmEventTarget target, string eventName) {
			if (host == null || target == null) return;

			// En sıcak yol: en ucuz kontroller önce. Bu metot oyunun en sıcak noktası
			// (her FSM, her olay), bu yüzden bağlantı yokken ağ işi yapılmaz.
			//
			// ANCAK parça yolu (loglama) bağlantıdan bağımsızdır ve önce
			// çalıştırılır; aşağıdaki HasRemote şartı artık onu kesmiyor.
			if (string.IsNullOrEmpty(eventName)) return;

			PlayMakerFSM destination;
			try { destination = ResolveDestination(host, target); } catch { return; }
			if (destination == null) return;

			// Hedef çözüldükten sonra eşya yoluna bakılır: ItemSync olayın hangi FSM'den
			// geldiğine ihtiyaç duyar, yani hedefi önce çözmek zorundayız.
			if (!SatsumaRegistry.Suppressed) {
				try { ItemSync.OnFsmEvent(destination, eventName); } catch { }
				try { SparkPlugLog.Check(destination, eventName); } catch { }
				// Kapılar: hedef çözülmüş olduğu için doğrudan adreslenebilir.
				// Oyunun ana yolu budur ("Send Event" action'ı Fsm.Event'i çağırır),
				// yani kapıların büyük çoğunluğu YALNIZCA buradan yakalanır.
				try { WorldObjectSync.OnFsmEvent(destination, eventName); } catch { }
				try { BoltSync.OnScrewEvent(destination, eventName); } catch { }
			}

			if (SatsumaRegistry.Suppressed) return;

			// Parça yolu: loglama bağlantıdan bağımsız (bkz. OnSendEvent).
			TracePartEvent(destination, eventName);

			if (!SteamTransport.HasRemote) return;

			NotifyInteraction(destination, eventName);
		}

		/// <summary>
		/// FsmEventTarget'ın işaret ettiği FSM bileşeni.
		///
		/// dnSpy ile doğrulandı: FsmEventTarget hem bir fsmComponent (doğrudan çözülmüş
		/// hedef) hem de hedef türünü (Self / GameObjectFSM / BroadcastAll...) tutar.
		/// Yalnızca doğrudan çözülmüş veya "Self" olan hedefler ele alınır; GameObject
		/// üzerinden adla çözümleme, olay başına GameObject.GetComponentsInChildren
		/// demek olduğu için bilinçli olarak yapılmaz.
		/// </summary>
		static PlayMakerFSM ResolveDestination(Fsm host, FsmEventTarget target) {
			if (target.fsmComponent != null) return target.fsmComponent;

			if (target.target == FsmEventTarget.EventTarget.Self) {
				try {
					MonoBehaviour owner = host.Owner;
					return owner as PlayMakerFSM;
				}
				catch { return null; }
			}
			return null;
		}

		// ------------------------------------------------------------------
		// Giriş noktası 3: Fsm.Event(string)
		// ------------------------------------------------------------------

		/// <summary>
		/// Fsm.Event(string) tek argümanlı hâli. Değişken adıyla FSM içinde doğrudan
		/// gönderilen olaylar buradan geçer (self-send). Ayrı olarak yamalıdır çünkü
		/// hedefli hâlden ayrı bir metoddur; ikisi birlikte tüm yolları kapsar.
		/// </summary>
		public static void OnSelfEvent(Fsm host, string eventName) {
			if (host == null || string.IsNullOrEmpty(eventName)) return;

			PlayMakerFSM owner;
			try { owner = host.Owner as PlayMakerFSM; } catch { return; }
			if (owner == null) return;

			if (!SatsumaRegistry.Suppressed) {
				try { ItemSync.OnFsmEvent(owner, eventName); } catch { }
				try { SparkPlugLog.Check(owner, eventName); } catch { }
				// Kapılar: `Use` FSM'i OPEN/CLOSE'i çoğunlukla KENDİSİNE gönderir
				// (Fsm.Event(string) yolu), yani bu giriş noktası olmadan araç
				// kapıları görünmez.
				try { WorldObjectSync.OnFsmEvent(owner, eventName); } catch { }
				// Vida sıkma: "Screw" FSM'i TIGHTEN/UNTIGHTEN'i bu yoldan
				// kendine gönderir (Fsm.Event(string)). Yeni yama yok.
				try { BoltSync.OnScrewEvent(owner, eventName); } catch { }
			}

			if (SatsumaRegistry.Suppressed) return;

			// Parça yolu: loglama bağlantıdan bağımsız (bkz. OnSendEvent).
			TracePartEvent(owner, eventName);

			if (!SteamTransport.HasRemote) return;

			NotifyInteraction(owner, eventName);
		}

		// ------------------------------------------------------------------
		// Installed bool yakalama
		// ------------------------------------------------------------------

		/// <summary>
		/// FsmBool.set_Value hook'u. Montaj niyetini veren olay yakalanamasa bile
		/// kayıt durumu buradan okunur; wiring parçaları yalnızca bu yolla gelir çünkü
		/// onların Assemble FSM'i sadece Init state'ini çalıştırır.
		/// </summary>
		public static void OnInstalledWrite(FsmBool variable) {
			if (variable == null) return;

			// Saat ve hava da FsmBool/FsmFloat/FsmInt'tir ve aynı setter'ı kullanır,
			// bu yüzden WorldSync parça tabloları süzgeçinden önce yönlendirilir.
			try { WorldSync.OnBoolWrite(variable); } catch { }

			if (SatsumaRegistry.Suppressed) return;

			// Installed bool yolu da bağlantıdan bağımsız. ASSEMBLE/REMOVE
			// olayı yakalanamazsa (bazı parçalarda FSM yolu yoktur) parça
			// takma/sökme TEK güvenilir kaynaktır; bağlantı şartı burada
			// olsaydı tek başına testte hiçbir parça görünmezdi.
			// Gönderim şartı OnInstalledChanged'in içinde.
			SatsumaRegistry.OnInstalledChanged(SatsumaRegistry.LookupInstalled(variable));
		}

		// ------------------------------------------------------------------
		// AKÜ / VOLTAJ YAZMA HOOK'U
		// ------------------------------------------------------------------

		/// <summary>
		/// Oyun akü/voltaj değişkenine NE ZAMAN yazarsa yazsın, değeri
		/// hedefe sabitler.
		///
		/// KULLANICI İSTEĞİ: "her kare değilde direkt hook at fsm ile akü
		/// voltajı hep 15 olsun." Önceki çözüm `Tick` içinde her kare
		/// yazıyordu — bu doğru çalışıyordu ama (a) kare başına iş ve
		/// (b) oyunun yazdığı ile bizim yazdığımız arasında 1 kare fark
		/// (gösterge bir anlık düşüyordu).
		///
		/// ÇÖZÜM: `FsmFloat.set_Value` ve `FsmBool.set_Value` hook'ları.
		/// Zaten `FsmBool.set_Value` yaması vardı (OnInstalledWrite), aynı
		/// teknik. Oyun `FloatOperator` ile `Volts` hesapladığı AN setter
		/// çağrılır, biz Prefix'te değeri düzeltiriz → gösterge hiç
		/// düşmez, çünkü okuyan kod daha yazmamış değeri görür.
		///
		/// MALİYET: sıfır. Hook yalnızca oyun o değişkeni yazarken çalışır;
		/// kare başına tarama yok.
		/// </summary>
		public static void OnCarFloatWrite(FsmFloat variable) {
			if (variable == null || variable.Name == null) return;
			// RE-ENTRANCY KORUMASI.
			//
			// Postfix'te `variable.Value = target` yazınca setter TEKRAR
			// çağrılır ve Postfix yine girer. Değer artık hedefe yakın
			// olduğu için aşağıdaki tolerans kontrolü döner; ama yine de
			// iç içe çağrı zincirini açıkça kesiyoruz.
			if (inFloatHook) return;
			// Sadece elektrik değişkenlerini dinle; saat/hava değişkenleri
			// de FsmFloat kullanıyor, onlara dokunmuyoruz.
			if (!IsElectricVar(variable.Name)) return;
			float target = CarFloatTarget(variable.Name);
			if (float.IsNaN(target)) return;
			if (variable.Value >= target - 0.1f && variable.Value <= target + 0.1f) return;
			inFloatHook = true;
			try { variable.Value = target; }
			finally { inFloatHook = false; }
			BatteryKeepFull.NoteHookWrite();
		}

		/// <summary>Akü/voltaj float Postfix'i içinde miyiz?</summary>
		static bool inFloatHook;

		/// <summary>Akü/voltaj bool yazması (`Battery` bool).</summary>
		public static void OnCarBoolWrite(FsmBool variable) {
			if (variable == null || variable.Name == null) return;
			if (inBoolHook) return;
			if (!string.Equals(variable.Name, "Battery", StringComparison.Ordinal)) return;
			if (variable.Value) return;
			inBoolHook = true;
			try { variable.Value = true; }
			finally { inBoolHook = false; }
			BatteryKeepFull.NoteHookWrite();
		}

		/// <summary>Akü bool Postfix'i içinde miyiz?</summary>
		static bool inBoolHook;

		static bool IsElectricVar(string n) {
			return string.Equals(n, "Volts", StringComparison.Ordinal)
				|| string.Equals(n, "Voltage", StringComparison.Ordinal)
				|| string.Equals(n, "Battery", StringComparison.Ordinal)
				|| string.Equals(n, "db_Battery", StringComparison.Ordinal)
				|| string.Equals(n, "BatteryLevel", StringComparison.Ordinal)
				|| string.Equals(n, "Charge", StringComparison.Ordinal)
				|| string.Equals(n, "AlternatorWear", StringComparison.Ordinal)
				|| string.Equals(n, "AlternatorEfficiency", StringComparison.Ordinal);
		}

		static float CarFloatTarget(string n) {
			if (!BatteryKeepFull.Enabled) return float.NaN;
			if (string.Equals(n, "Volts", StringComparison.Ordinal)
				|| string.Equals(n, "Voltage", StringComparison.Ordinal)) {
				return BatteryKeepFull.VOLT_TARGET;
			}
			if (string.Equals(n, "Battery", StringComparison.Ordinal)
				|| string.Equals(n, "db_Battery", StringComparison.Ordinal)
				|| string.Equals(n, "BatteryLevel", StringComparison.Ordinal)
				|| string.Equals(n, "Charge", StringComparison.Ordinal)) {
				return 1f;
			}
			if (!BatteryKeepFull.FreezeGenerator) return float.NaN;
			if (string.Equals(n, "AlternatorWear", StringComparison.Ordinal)
				|| string.Equals(n, "AlternatorEfficiency", StringComparison.Ordinal)) {
				return 1f;
			}
			return float.NaN;
		}
	}

	/// <summary>PlayMakerFSM.SendEvent(string): C#, ChangeState ve [RPC] yolları.</summary>
	[HarmonyPatch(typeof(PlayMakerFSM), "SendEvent", new Type[] { typeof(string) })]
	public static class SendEventHook {
		/// <summary>
		/// bool döner: ÖLÜM FSM'ine giden olay YÜRÜTÜLMEZ (false = hedef
		/// metot atlanır, bkz. GodMode.ShouldBlockDeathEvent). Diğer bütün
		/// olaylarda davranış değişmez (true).
		/// </summary>
		static bool Prefix(PlayMakerFSM __instance, string eventName) {
			try {
				if (GodMode.ShouldBlockDeathEvent(__instance, eventName)) return false;
				AssemblyPatch.OnSendEvent(__instance, eventName);
			} catch { }
			return true;
		}
	}

	/// <summary>
	/// Fsm.ProcessEvent(FsmEvent, FsmEventData) — OLAY TESLİMİNİNİN TEK BOĞAZI.
	///
	/// NEDEN GEREKLİ (canlı kanıt: mscmp.txt, 2026-10-14 17:53):
	/// yukarıdaki yamalar yalnız GÖNDEREN tarafı görür. PlayMaker bir olayı
	/// hedefe "nesneye gönder" ya da "FSM adına gönder" yoluyla yolladığında
	/// (Send Event action -> Fsm.Event(FsmEventTarget, ...) ->
	/// BroadcastEventToGameObject / SendEventToFsmOnGameObject) hedef FSM'e
	/// doğrudan <c>ProcessEvent</c> ile yazılır; PlayMakerFSM.SendEvent
	/// çağrılmadığı için ne engelleme ne de log devreye girer. Sonuç: ölüm
	/// FSM'ine gelen olay hiçbir yamaya girmiyordu ve oyun ölümü normal
	/// işletiyordu.
	///
	/// DUMP KANITI (PlayMaker.dll decompile): hedefe giden her yol
	/// ProcessEvent'te birleşiyor — Self (satır 6374), GameObjectFSM
	/// (6391), HostFSM (6400), SubFSMs (6408), BroadcastEvent (6467),
	/// BroadcastEventToGameObject (6498), SendEventToFsmOnGameObject (6532),
	/// PlayMakerFSM.BroadcastEvent (11318).
	///
	/// MALİYET: bu yol oyunun EN sıcak damarıdır (her olay, her FSM).
	/// Karar GodMode tarafında ucuz tutulur: önce referans, sonra ad.
	/// Prefix yalnız GodMode açıkken ve korunan FSM'ler için false döner.
	/// </summary>
	[HarmonyPatch(typeof(Fsm), "ProcessEvent", new Type[] { typeof(FsmEvent), typeof(FsmEventData) })]
	public static class FsmProcessEventHook {
		/// <summary>
		/// false = hedef FSM olayı HİÇ görmez (ölüm engellenir).
		/// Diğer bütün FSM'ler ve olaylar için davranış değişmez (true).
		/// </summary>
		static bool Prefix(Fsm __instance, FsmEvent fsmEvent) {
			try {
				if (GodMode.ShouldBlockDeathFsmEvent(__instance, fsmEvent)) return false;
			} catch { }
			return true;
		}
	}

	/// <summary>
	/// Fsm.Event(FsmEventTarget, FsmEvent) token 0x060001ED.
	///
	/// dnSpy ile bulunan kritik yol: PlayMaker'ın "Send Event" action'ı olayı
	/// <c>base.Fsm.Event(this.eventTarget, this.sendEvent)</c> ile buraya yollar, yani
	/// oyunun olay trafiğinin ana damarı buradan geçer. SendEvent(string) yaması
	/// tek başına bu trafiği kaçırır.
	/// </summary>
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(FsmEventTarget), typeof(FsmEvent) })]
	public static class FsmTargetedEventHook {
		/// <summary>Ölüm FSM'ine giden olay yürütülmez (bkz. GodMode.IsDeathFsm).</summary>
		static bool Prefix(Fsm __instance, FsmEventTarget eventTarget, FsmEvent fsmEvent) {
			try {
				if (GodMode.IsDeathFsm(__instance)) {
					string n = null;
					try { n = fsmEvent != null ? fsmEvent.Name : null; } catch { }
					GodMode.OnDeathEvent(n);
					return false;
				}
				AssemblyPatch.OnTargetedEvent(__instance, eventTarget, fsmEvent);
			} catch { }
			return true;
		}
	}

	/// <summary>
	/// Fsm.Event(string) token 0x060001EE: FSM içine doğrudan self-send.
	/// </summary>
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(string) })]
	public static class FsmSelfEventHook {
		/// <summary>Ölüm FSM'ine giden olay yürütülmez (bkz. GodMode.IsDeathFsm).</summary>
		static bool Prefix(Fsm __instance, string fsmEventName) {
			try {
				if (GodMode.IsDeathFsm(__instance)) {
					GodMode.OnDeathEvent(fsmEventName);
					return false;
				}
				AssemblyPatch.OnSelfEvent(__instance, fsmEventName);
			} catch { }
			return true;
		}
	}

	/// <summary>
	/// Fsm.Event(FsmEventTarget, string) token 0x060001EC: hedefli ama
	/// string-adlı olay yolu (dnSpy Fsm üye taramasında bulundu).
	/// </summary>
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(FsmEventTarget), typeof(string) })]
	public static class FsmTargetedStringEventHook {
		/// <summary>Ölüm FSM'ine giden olay yürütülmez (bkz. GodMode.IsDeathFsm).</summary>
		static bool Prefix(Fsm __instance, FsmEventTarget eventTarget, string fsmEventName) {
			try {
				if (GodMode.IsDeathFsm(__instance)) {
					GodMode.OnDeathEvent(fsmEventName);
					return false;
				}
				AssemblyPatch.OnTargetedEventByName(__instance, eventTarget, fsmEventName);
			} catch { }
			return true;
		}
	}

	/// <summary>
	/// Fsm.Event(FsmEvent) token 0x060001EF: tekil self-send (dnSpy Fsm üye
	/// taramasında bulundu). Adı çıkarıp mevcut self-send gövdesine verilir.
	/// </summary>
	[HarmonyPatch(typeof(Fsm), "Event", new Type[] { typeof(FsmEvent) })]
	public static class FsmSingleEventHook {
		/// <summary>Ölüm FSM'ine giden olay yürütülmez (bkz. GodMode.IsDeathFsm).</summary>
		static bool Prefix(Fsm __instance, FsmEvent fsmEvent) {
			string n = null;
			try { n = fsmEvent != null ? fsmEvent.Name : null; } catch { return true; }
			try {
				if (GodMode.IsDeathFsm(__instance)) {
					GodMode.OnDeathEvent(n);
					return false;
				}
				AssemblyPatch.OnSelfEvent(__instance, n);
			} catch { }
			return true;
		}
	}

	/// <summary>
	/// FsmBool.set_Value: montaj/sökme durumunun kayıt tarafı gerçek doğrusu.
	///
	/// AYRICA akü `Battery` bool'u: oyun False yazarsa hemen düzeltilir
	/// (voltaj hesabının girdisi olduğu için gösterge düşmesin).
	///
	/// NOT: `OnInstalledWrite` **Prefix**'te kalıyor — montaj/sökme
	/// yakalaması yazma ÖNCESİ ("neden" bilgisi) lazım. Akü düzeltmesi
	/// ise **Postfix**'te: yazma SONRASI düzeltmek, geri besleme


	[HarmonyPatch(typeof(FsmBool), "set_Value")]
	public static class InstalledBoolHook {
		static void Prefix(FsmBool __instance) {
			try { AssemblyPatch.OnInstalledWrite(__instance); } catch { }
		}

		static void Postfix(FsmBool __instance) {
			try { AssemblyPatch.OnCarBoolWrite(__instance); } catch { }
		}

	/// FsmFloat.set_Value — AKÜ/VOLTAJ HOOK'U.
	///
	/// KULLANICI İSTEĞİ: "her kare değilde direkt hook at fsm ile akü
	/// voltajı hep 15 olsun."
	///
	/// ASSET (docs/fsm_detail/l2__Electrics__Electrics.txt, satır 544):
	///   Volts ← FloatOperator(float1=RPM, float2=Battery)
	///
	/// NEDEN POSTFIX, PREFIX DEĞİL — kritik:
	///
	/// Önceki sürüm Prefix kullanıyordu ve bu YANLIŞTI. Prefix setter'ın
	/// ÖNÜNDE çalışır: biz hedef değeri yazarız → setter çağrılır →
	/// Prefix TEKRAR çalışır → biz yine yazarız… oyun yazmayı bitirene
	/// kadar. Bu (a) sonsuz geri besleme riski, (b) yığın taşması ve
	/// (c) oyun hesabının ezilmesi demek.
	///
	/// Postfix'te akış şudur:
	///   oyun hesaplar → setter yazar → Postfix çalışır → biz HEDEFE
	///   DÜZELTİRİZ. Düzeltmemiz yeni bir setter çağrısı üretir; o çağrı
	///   da Postfix'e girer ama değer artık tolerans bandında olduğu için
	///   `OnCarFloatWrite` erken döner → zincir 2 adımda biter.
	///
	/// Ayrıca "değer şu an doğruysa hiç yazma" kontrolü sayesinde oyun
	/// doğru değeri yazdığında hiç müdahale etmiyoruz.
	/// </summary>
	[HarmonyPatch(typeof(FsmFloat), "set_Value")]
	public static class ElectricFloatHook {
		static void Postfix(FsmFloat __instance) {
			try { AssemblyPatch.OnCarFloatWrite(__instance); } catch { }
		}
	}

}
}
