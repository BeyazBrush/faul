// Artik baglantilarin hepsi asagida DOOR_EVENTS / DOOR_FSMS olarak tanimli
using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// DUNYA NESNELERI SENKRONIZASYONU - kapilar, musluklar, dus, anahtar,
	/// dolap kapagi gibi tek bir FSM degiskeniyle anlatilabilecek her sey.
	///
	/// NEDEN AYRI BIR MODUL
	/// -------------------
	/// Parca (Satsuma) senkronu var olan bir sistemi klonlar; burada is
	/// bambaaska: oyuncu bir NESNEYE ETKI EDIYOR (kapiyi aciyor) ve
	/// karsi tarafin ayni nesneyi ayni duruma sokmasi gerekiyor. Bu
	/// "olay" degil "durum" senkronudur: paket kaybolursa kapi bir daha
	/// acilmaz, o yuzden GUVENLI (reliable) kanal kullanilir.
	///
	/// FSM, C# DEGILDIR
	/// ----------------
	/// dnSpy kaniti: Assembly-CSharp'te "Door" tipi YOK, radyo/CD ile
	/// ilgili tek string bile yok. Tum bu mantik PlayMaker FSMidir ve
	/// sahne verisinde durur.
	///
	/// KAPILAR: OLAY, DEGISKEN DEGIL
	/// -----------------------------
	/// level2'den okundu (mysummercar_Data, bu build):
	///
	///   CARPARTS/PartsCar/door left(Clone)                FSM Use (start: Close door)
	///       events : OPEN, CLOSE, "Mouse off", "Mouse over 1/2", LOOP
	///       states : Close door -> Open door -> Open door 2
	///       actions: SetRotation + SetHingeJointLimits (Joint)
	///       bool   : Open
	///
	///   YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle     FSM Use (start: Get pivot)
	///       events : USE, OPEN, CLOSE, OPENDOOR, LOCKED, DoorOpen
	///   YARD/UNCLE/Building/DoorFront/Pivot/Door            FSM Use (start: Wait player)
	///       events : USE, OPEN, CLOSE, LOCKED, KNOCK ON DOOR
	///   YARD/UNCLE/Building/DoorFront/Pivot/UncleDoorHandle  FSM UseOld (start: Get pivot)
	///       events : USE, OPEN, CLOSE, OPENDOOR, Door locked
	///       bool   : DoorLocked     action: PlayAnimation door_open/door_close
	///
	/// YANI "kapi acik mi" diye okunabilir bir degisken YOK; `Open` bool'u tek
	/// istisna (araba kapisi) ve onu yazmak durum makinesini ilerletmiyor.
	/// Eski surum tam da bunu yapiyordu: bool'u yaziyor, kapi kipirdamiyordu.
	/// BU YUZDEN kapilar iki yoldan senkronize edilir:
	///
	///   1) OLAY (birincil): OPEN / CLOSE yakalanir ve karsi tarafta ayni
	///      FSM'e ayni olay gonderilir. Kapinin kendi animasyonu, sesi,
	///      menteÅŸe sinirlari ve kilit kontrolu oyunun kendi kodundan akar.
	///   2) DEGISKEN (yedek): yapisal durumu olan nesneler (musluk, dus,
	///      anahtar, ev isiklari) mevcut polling yoluyla senkronlenir.
	///
	/// BU NEDEN DOGRU YOL
	/// -------------------
	/// Kapiyi kendi koduna donmeye calismak (rotation yazmak) oyunun
	/// kilit/mentese/kilit dili mantigini atlar ve oyuncu kapiyi
	/// kapatamaz. FSM olayini tekrar oynatmak oyunun kendi yoludur.
	///
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class WorldObjectSync {

		// ------------------------------------------------------------------
		// Ayarlar
		// ------------------------------------------------------------------

		/// <summary>
		/// DeÄŸiÅŸim taramasÄ± aralÄ±ÄŸÄ± (saniye).
		///
		/// 0.1f (10 Hz) â†’ 0.25f (4 Hz) olarak dÃ¼ÅŸÃ¼rÃ¼ldÃ¼.
		///
		/// KULLANICI BELÄ°RTÄ°SÄ°: "kasma sorunu var, FPS 50'ye dÃ¼ÅŸtÃ¼."
		/// Tarama BAÄLANTIDAN BAÄIMSIZ Ã§alÄ±ÅŸÄ±r (tek baÅŸÄ±na testte de
		/// Ã§alÄ±ÅŸsÄ±n diye), yani 10 Hz'de saniyede 10 kez TÃœM izlenen
		/// nesneler ve tÃ¼m bool/float'larÄ± karÅŸÄ±laÅŸtÄ±rÄ±lÄ±yordu. Nesne
		/// sayÄ±sÄ± arttÄ±kÃ§a (dashboard + kontak + CD player eklendikÃ§e)
		/// bu maliyet kare baÅŸÄ±na daÄŸÄ±tÄ±lÄ±yor ve FPS dÃ¼ÅŸÃ¼yordu.
		///
		/// 4 Hz yeterli: bu yol gÃ¶rsel/etkileÅŸim durumu taÅŸÄ±r (musluk,
		/// Ä±ÅŸÄ±k, kapÄ±), saniyede 4 kez hÃ¢lÃ¢ fark edilmez. Durum aynasÄ±
		/// (`CtrlState`) zaten 0.2 sn'de bir Ã§alÄ±ÅŸÄ±yor.
		/// </summary>
		public const float POLL_INTERVAL = 0.25f;

		/// <summary>Ayni nesne icin en kisa tekrar araligi (anti-spam).</summary>
		public const float RESEND_COOLDOWN = 0.25f;

		/// <summary>Baslangic senkronunda nesne basina gonderim siniri.</summary>
		public const int MAX_PER_PACKET = 24;

		/// <summary>Birikmis kapÄ± olaylarÄ±nÄ±n gÃ¶nderim aralÄ±ÄŸÄ± (s).</summary>
		public const float DOOR_FLUSH_INTERVAL = 0.05f;

		/// <summary>Bekleyen kapÄ± olayÄ± sayÄ±sÄ± Ã¼st sÄ±nÄ±rÄ±.</summary>
		public const int DOOR_QUEUE_LIMIT = 64;

		// ------------------------------------------------------------------
		// Izlenecek nesneler (asset'ten cikarilmis adlar)
		// ------------------------------------------------------------------

		/// <summary>
		/// KAPILAR: oyunun gercekten kullandigi OLAYLAR.
		/// Ã–lÃ§Ã¼m: bÃ¼tÃ¼n ev/araÃ§ kapÄ±larÄ±nda OPEN ve CLOSE bildiriliyor;
		/// OPENDOOR ve DoorOpen bazÄ± varyantlarda ayrÄ± bir giriÅŸ noktasÄ±.
		/// </summary>
		static readonly string[] DOOR_EVENTS = { "OPEN", "CLOSE", "OPENDOOR", "DoorOpen", "USE" };

		/// <summary>
		/// KapÄ±nÄ±n etkileÅŸim FSM adÄ±. Ã–lÃ§Ã¼m: hepsi `Use`, biri `UseOld`
		/// (amcalarÄ±n evinin Ã¶n kapÄ± tutamaÄŸÄ±).
		/// </summary>
		static readonly string[] DOOR_FSMS = { "Use", "UseOld", "Knob" };

		/// <summary>
		/// YOLUN BÄ°R DÄ°ZÄ°NÄ°NDE geÃ§mesi gereken parÃ§a (kapÄ±).
		///
		/// KapÄ± adÄ± Ã§oÄŸu zaman YAPRAKTA deÄŸil, EBEVEYNDE:
		///   YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle
		///   YARD/Building/Garage/GarageDoors/DoorLeft/Coll
		///   CABIN/Shed/Doors/DoorLeft/Mesh
		///   DANCEHALL/Doors/DoorRight/dancehut_doorright
		/// Bu yÃ¼zden eÅŸleÅŸme YAPRAKTA deÄŸil YOLDA yapÄ±lÄ±r, ve alt dize olarak.
		///
		/// DÄ°ZÄ°N BAÅINA EÅLEÅME ÅART: "lid" kelimesi `MISC/holiday
		/// present(Clon2)/Parts/Money` iÃ§inde geÃ§iyor. Alt dizin olsaydÄ± o
		/// nesne bir kapÄ± sanÄ±lÄ±rdÄ±.
		/// </summary>
		static readonly string[] DOOR_WORDS = { "door", "hoodlocking", "glovbox" };

		/// <summary>
		/// YakÄ±t kapaÄŸÄ± / dolabÄ± (Tier 2). Ã–lÃ§Ã¼m: Ã¼Ã§ adet, hepsi ÅŸu biÃ§imde
		///   FERNDALE(1630kg)/LOD/FuelFiller 1/Pivot/body_fuel_hatch/Trigger
		///   HAYOSIKO(1500kg, 250)/LOD/FuelFiller/Pivot/body_fuel_hatch/Trigger
		///   SATSUMA(557kg, 248)/MiscParts/fuel tank pipe(xxxxx)/FuelFiller/...
		/// Bunlar menteÅŸeli deÄŸil: yalnÄ±zca SetRotation + ActivateGameObject
		/// kullanÄ±yorlar, yani Tier 1 onlarÄ± kaÃ§Ä±rÄ±r. AÅŸaÄŸÄ±daki sÃ¶zcÃ¼kler
		/// Ã¶lÃ§Ã¼lmÃ¼ÅŸ olarak bu Ã¼Ã§ nesneyi seÃ§iyor.
		/// </summary>
		static readonly string[] FUEL_WORDS = { "fuelfiller", "body_fuel_hatch" };

		/// <summary>
		/// KEÅÄ°F Ã–N FÄ°LTRESÄ°: adÄ±nda geÃ§erse nesne "panel adayÄ±" sayÄ±lÄ±r ve
		/// yalnÄ±zca o zaman bileÅŸenleri okunur.
		///
		/// Bu listenin Ã¼Ã§ kademenin de ÃœST KÃœMESÄ° olmasÄ± gerekir (kapÄ±, kaput,
		/// Ã¶n kapak, yakÄ±t kapaÄŸÄ±) â€” ve oluyor. Bilerek geniÅŸ: Ã¶n filtrede geÃ§en
		/// bir fazladan nesne yalnÄ±zca bir GetComponents maliyeti demektir,
		/// Ã§Ã¼nkÃ¼ ASIL kararÄ± veren PanelTier'dÄ±r ve o Ã¶lÃ§Ã¼lmÃ¼ÅŸtÃ¼r. Burada
		/// "lid"/"holiday" gibi bir Ã§akÄ±ÅŸma zararsÄ±zdÄ±r.
		///
		/// NEDEN GEREKLÄ°: Resources.FindObjectsOfTypeAll 37.000 nesne dÃ¶ndÃ¼rÃ¼r.
		/// Filtresiz her birine GetComponents<PlayMakerFSM>() Ã§aÄŸÄ±rmak yÃ¼kleme
		/// anÄ±nda gÃ¶zle gÃ¶rÃ¼lÃ¼r bir gecikme olurdu. Bu filtre onu ~yÃ¼ze indiriyor.
		/// </summary>
		static readonly string[] PANEL_HINT_WORDS = {
			"door", "hood", "bootlid", "bonnet", "hatch", "trunk",
			"fuelfiller", "body_fuel_hatch", "hoodlocking",
			"radio", "glovbox",
		};

		/// <summary>
		/// Nesne ya da bir ebeveyni panel adayÄ± mÄ±? YalnÄ±zca ad zincirine bakar,
		/// hiÃ§bir bileÅŸen okumaz, tahsis yapmaz.
		/// </summary>
		static bool PanelCandidate(GameObject go) {
			if (go == null) return false;
			Transform cur;
			try { cur = go.transform; } catch { return false; }
			int guard = 0;
			while (cur != null && guard++ < 64) {
				string n = cur.name;
				if (!string.IsNullOrEmpty(n)) {
					for (int w = 0; w < PANEL_HINT_WORDS.Length; w++) {
						if (n.IndexOf(PANEL_HINT_WORDS[w], StringComparison.OrdinalIgnoreCase) >= 0)
							return true;
					}
				}
				cur = cur.parent;
			}
			// v27: ad tutmadiysa `DoorOpen` bool'una bak.
			// SMP_DOOROPEN_MARKER_V27
			return HasDoorOpenMarker(go);
		}

		/// <summary>
		/// v27: nesne uzerindeki (veya cocuklarindaki) bir FSM `DoorOpen`
		/// bool'u bildiriyor mu? Oyunun panel isareti.
		///
		/// DOKUM: 25 kayit `DoorOpen` bildiriyor; 21'i YARD/Building
		/// altinda ve hepsi `Use`/`UseOld` FSM'idir. Bilesik yol
		/// (`GetComponentsInChildren`) kullanilir cunku kapi kolu
		/// (`Pivot/Handle`) govdeden ayri bir cocuktur.
		/// </summary>
		static bool HasDoorOpenMarker(GameObject go) {
			if (go == null) return false;
			PlayMakerFSM[] fsms = null;
			try { fsms = go.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch { return false; }
			if (fsms == null || fsms.Length == 0) return false;
			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM f = fsms[i];
				if (f == null || f.FsmVariables == null) continue;
				FsmBool[] bs;
				try { bs = f.FsmVariables.BoolVariables; } catch { continue; }
				if (bs == null) continue;
				for (int b = 0; b < bs.Length; b++) {
					if (bs[b] == null) continue;
					string n = null;
					try { n = bs[b].Name; } catch { }
					if (string.Equals(n, DOOR_OPEN_VAR, StringComparison.Ordinal)) return true;
				}
			}
			return false;
		}

		/// <summary>
		/// v27: oyunun panel (acılır-kapanır) isareti. DOKUM: 25 kayit
		/// bildiriyor, 21'i YARD/Building altinda — kapilar, buzdolabi,
		/// sauna kapisi.
		/// </summary>
		public const string DOOR_OPEN_VAR = "DoorOpen";

		/// <summary>
		/// Tier adlarÄ±, yalnÄ±zca 'smp doors' raporu ve teÅŸhis iÃ§in.
		/// </summary>
		const string TIER_DOOR = "door-name";
		const string TIER_HINGE = "hinged";
		const string TIER_FUEL = "fuel-hatch";


		/// <summary>Musluklar ve su sistemleri.</summary>
		static readonly string[] TAP_OBJECTS = {
			"TapDrink", "PivotWaterPress", "FuelTap", "BuyWaterpump",
			// v27: dokumda dogrulanmis musluk/su nesneleri (1'er kayit):
			//   YARD/Building/BATHROOM/Shower/Valve|Switch
			//   YARD/Building/KITCHEN/KitchenWaterTap/Trigger|Use
			//   YARD/Building/KITCHEN/KitchenWaterTap/ParticleDrink|Distance
			// SMP_HOUSE_WANTED_V27
			"Valve", "Trigger", "ParticleDrink",
};

		/// <summary>Dus ve banyo.</summary>
		static readonly string[] SHOWER_OBJECTS = {
			"Shower",
			// v27: `YARD/Building/SAUNA/Sauna/Kiuas/StoveTrigger|Steam`
			"StoveTrigger",
};

		/// <summary>
		/// Ev isik anahtarlari (sahne taramasi kaniti). Tusa basan tarafta
		/// FSM degisir, karsiya ObjectState ile gider (kapi/muslukla ayni yol).
		/// </summary>
		static readonly string[] SWITCH_OBJECTS = {
			"MainSwitch", "Switch", "Light", "Lights",
			// v27: dokumda dogrulanmis ev dugmeleri:
			//   LIVINGROOM/Fireplace/SetFire|Use  (OPEN/CLOSE)
			//   LIVINGROOM/Telephone/Cord|Use    (USE)
			// SMP_HOUSE_WANTED_V27
			"SetFire", "Cord",
		};

		/// <summary>
		/// v27: `Knob*` ocak dugmeleri BILINCLI OLARAK DISARIDA.
		///
		/// DOKUM: `KITCHEN/OvenStove/KnobPower1..4`, `KnobTempOven`,
		/// `KnobModeOven` -- hepsi FSM adi `Screw`, olaylari
		/// `TIGHTEN/UNTIGHTEN/ASSEMBLE`, eylemleri `SetBoolValue`.
		/// Yani bunlar `Use` dugmesi DEGIL, vidalama topu gibi
		/// donen dugmeler; `SCREW` bolt yoluna aittir.
		///
		/// `Screw` FSM'ini `SWITCH_OBJECTS`'a koymak, oyunun
		/// `TIGHTEN` olayini `DoorEvent`/`CtrlState` ile gondermeye
		/// calisir -- oysa bu olaylar BoltBatch hattinin konusudur.
		/// Karistirmak cift gonderim ve yanlis uygulama yaratir.
		///
		/// Ayrica `Knob` YAPRAK ADI olarak dokumde HICBIR YERDE
		/// gecmiyor (yalniz `KnobPower1` gibi birlestirilmis adlar var),
		/// yani `Knob`u listeye koymak da bir sey kazandirmazdi.
		/// </summary>
		static readonly string[] EXCLUDED_KNOBS = {
			"KnobPower1", "KnobPower2", "KnobPower3", "KnobPower4",
			"KnobTempOven", "KnobModeOven",
		};


		// ------------------------------------------------------------------
		// ARAC KONTROLLERI  (v11 â€” canlÄ± log kanÄ±tÄ±)
		//
		// KULLANICI BILGISI: "Satsuma'nÄ±n kapÄ±sÄ± aÃ§Ä±ktÄ± gÃ¶rÃ¼nmedi, kontak
		// aÃ§Ä±ktÄ± gÃ¶rmedi, CD player'i aÃ§tÄ±m gÃ¶rmedi." ArkadaÅŸ logunda
		//   12:09:41.390 'UseNew' -> 'Wait button' @ .../IgnitionSatsuma
		//   12:09:54.096 'UseNew' -> 'ACC on'       @ .../IgnitionSatsuma
		//   12:09:54.264 'Starter' -> 'ACC'          @ .../Car/Starter
		//   12:09:56.417 'Assembly' -> 'Wait'        @ .../cd player(Clone)
		// yazÄ±lÄ±yor ama HÄ°Ã‡BÄ°R `[ACT]` satÄ±rÄ± yok â€” yani bu nesneler ne
		// deÄŸiÅŸken tablosunda ne olay tablosundaydÄ±.
		//
		// ASSET (l2/fsms.json -> docs/fsm_detail):
		//   SATSUMA/.../steering_column2/IgnitionSatsuma  FSM 'UseNew'
		//     bool : MotorOn, CarKey, Electrics, Keys, ACC
		//     float: DashVolume, MouseDelay
		//     olay : ACC, START, OFF, ON, IGNITION, KEY, NOKEY
		//   SATSUMA/CarSimulation/Car/Starter            FSM 'Starter'
		//     bool : PowerON, Starting, ShutOff, ACC, Electrics, ElectricsOK,
		//            FuelOK, MotorInstalled, Installed1..5, StarterSound,
		//            IndicatorOil, db_Battery, db_Required1..8
		//     olay : "START ENGINE", OFF, PROCEED, STALL, PUSHSTART
		//   .../pivot_radio/cd player(Clone)             FSM 'Assembly'
		//   .../cd player(Clone)/trigger_disc            FSM 'Assembly'
		//     bool : CDin, Ok, CD1, CD2, CD3, Distance
		//     olay : ASSEMBLE, DISASSEMBLE, EJECT, CDIN, CD1, CD2, CD3
		//   .../ButtonsCD/RadioCDSwitch                 FSM 'Use'
		//     bool : ON, On, OFF, Off
		//     olay : ON, OFF, USE, SUSKI
		// ------------------------------------------------------------------

		static readonly string[] CAR_CONTROL_OBJECTS = {
    "IgnitionSatsuma", "Starter", "Electrics", "Keys", "cd player(Clone)",
    "trigger_disc", "RadioCDSwitch", "Eject", "TrackChannelSwitch",
};

		/// <summary>
		/// YALNIZCA OLAY YOLUNDA, DEÄÄ°ÅKEN TABLOSUNDA OLMAYAN kontrol
		/// nesneleri.
		///
		/// CANLI KANIT (host logu 12:59:30 â†’ 12:59:47):
		///   DOOR changed â€¦/Car/Electrics  AlternatorEfficiency=2003.16
		///                                 AlternatorWear=95.28  Charge=119.48
		///   â€¦ saniyede bir paket, 18 saniyede 18 paket â€¦
		///
		/// NEDEN SORUN: `Electrics` FSM'i altÄ± tane ham float taÅŸÄ±yor
		/// (Charge, AlternatorWear, AlternatorEfficiency, RPM, Volts,
		/// Math1..3). `Charge` sÄ±nÄ±rsÄ±z bÃ¼yÃ¼dÃ¼ÄŸÃ¼ iÃ§in DEÄER HER KARE
		/// deÄŸiÅŸiyor â†’ `Tick` her kare "dirty" buluyor â†’ paket Ã¼retiyor.
		/// SonuÃ§: boÅŸta bant + uzaktaki hesabÄ±n bozulmasÄ± (kullanÄ±cÄ±
		/// belirtisi: "elektrik dÃ¼ÅŸÃ¼yor").
		///
		/// Ã‡Ã–ZÃœM: `Electrics`/`Starter` yalnÄ±zca bool Ã¼reten FSM'ler
		/// (`ElectricsOK`, `ElectricsON`, `PowerON`, `ACC`, `Installed1..5`)
		/// â€” bunlar PartSync'in kayÄ±t sahipliÄŸiyle uyumlu ve az deÄŸiÅŸir.
		/// Ham float'lar senkronlanmaz; akÃ¼/jeneratÃ¶r deÄŸerleri
		/// `BatteryKeepFull` tarafÄ±ndan YERELDE kilitlenir.
		/// </summary>
		static readonly string[] EVENT_ONLY_CONTROLS = {
			"Electrics", "Starter",
		};

		static bool EventOnly(string n) {
			for (int i = 0; i < EVENT_ONLY_CONTROLS.Length; i++)
				if (string.Equals(n, EVENT_ONLY_CONTROLS[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>
		/// Ham/sÃ¼rekli deÄŸiÅŸen float'lar â€” DEÄÄ°ÅKEN TABLOSUNA ALINMAZ.
		///
		/// KULLANICI BELÄ°RTÄ°SÄ°: "kasma sorunu var, FPS 50'ye dÃ¼ÅŸtÃ¼."
		///
		/// CANLI KANIT (host logu 13:04:09):
		///   DOOR changed â€¦/Car/Electrics  Battery=True ElectricsOK=True
		/// saniyede bir "Electrics" paketi; `Charge`/`AlternatorEfficiency`
		/// her kare deÄŸiÅŸtiÄŸi iÃ§in nesne ASLA "temiz" olmuyor.
		///
		/// `Electrics` FSM'i altÄ± tane float taÅŸÄ±yor ve bunlarÄ±n Ã§oÄŸu
		/// OYUNUN Ä°Ã‡ HESABI (Math1..3 ara deÄŸerler, RPM, Wear). HiÃ§biri
		/// oyuncuya gÃ¶rÃ¼nmez, hepsi her kare deÄŸiÅŸir. Tabloya girerse:
		///   * 4 Hz'de karÅŸÄ±laÅŸtÄ±rÄ±lÄ±r (POLL_INTERVAL 0.1 â†’ 10 Hz tarama),
		///   * her seferinde "deÄŸiÅŸmiÅŸ" bulunur â†’ paket Ã¼retir,
		///   * uzakta oyuncu hesabÄ± bozulur.
		///
		/// Bunlar yalnÄ±zca YERELDE anlamlÄ±; oyun zaten hesaplÄ±yor.
		/// </summary>
		static readonly string[] LOCAL_ONLY_VARS = {
			// araÃ§ elektrik hesabÄ±
			"Charge", "AlternatorEfficiency", "AlternatorWear", "WearSetup",
			"Math1", "Math2", "Math3", "RPM", "Volts", "Battery",
			"BatteryLevel", "FuelLevel", "Charge1", "Charge2",
			// genel sÃ¼rekli deÄŸerler
			"Wear", "Rot", "Scroll", "Tension", "Distance", "Speed",
			"Progress", "Timer", "Time", "Life", "Opacity", "Fill",
			"Angle", "Pitch", "Yaw", "Value", "Amount", "Slider",
		};

		static bool LocalOnlyVar(string name) {
			if (string.IsNullOrEmpty(name)) return false;
			for (int i = 0; i < LOCAL_ONLY_VARS.Length; i++)
				if (string.Equals(name, LOCAL_ONLY_VARS[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>
		/// PartSync / BoltSync'in SAHÄ°BÄ° olduÄŸu deÄŸiÅŸkenler.
		///
		/// NEDEN GEREKLÄ°: yeni kontrol nesneleri (`Starter`, `trigger_disc`,
		/// `cd player(Clone)`) de `Installed` / `Bolted` / `Tightness`
		/// taÅŸÄ±yor. AynÄ± deÄŸiÅŸkeni iki yol yazarsa kapÄ±daki gibi GERÄ° BESLEME
		/// DÃ–NGÃœSÃœ kurulur (bkz. Register iÃ§indeki kapÄ± notu).
		/// </summary>
		static readonly string[] RESERVED_VARS = {
			"Installed", "Bolted", "Tightness", "Removed", "Wear", "Condition",
		};

		/// <summary>
		/// Kontrol olaylarÄ± (olay yolu â€” deÄŸiÅŸken tek baÅŸÄ±na yetmez).
		///
		/// DÃœZELTME (canlÄ± log, client 12:50:22-12:50:38):
		///   CTRL PROCEED  â€¦/pivot_radio/cd player(Clone)â€¦     â”
		///   CTRL CD1      â€¦/pivot_radio/cd player(Clone)â€¦     â”˜ saniyede 2 paket
		///   â€¦ 20+ kez arka arkaya, kesintisiz
		///
		/// `PROCEED`, `CD1`, `CD2`, `CD3` ve `LOOP` `Assembly` FSM'inin
		/// **LOOP** olaylarÄ±dÄ±r: kullanÄ±cÄ± eylemi DEÄÄ°L, "yine dene"
		/// sinyalidir. Bunlar gÃ¶nderilince saniyede onlarca paket Ã¼retilir
		/// ve aÄŸ gereksiz dolar. KapaÄŸÄ±n gÃ¶rÃ¼nÃ¼mÃ¼ zaten DURUM AYNASI ile
		/// taÅŸÄ±nÄ±yor (`Assembly -> 'CD in'`), yani bu olaylara gerek yok.
		///
		/// Kalanlar gerÃ§ek kullanÄ±cÄ± eylemleri:
		///   ASSEMBLE / DISASSEMBLE / EJECT / CDIN  â€” CD player & disk
		///   "START ENGINE" / OFF / ON / ACC / START / IGNITION â€” kontak
		///   PUSHSTART â€” itmeli Ã§alÄ±ÅŸtÄ±rma
		///   USE â€” nesneye tÄ±klama (kapÄ±/kontrol Ã¶nculÃ¼)
		/// </summary>
		static readonly string[] CONTROL_EVENTS = {
			"START ENGINE", "OFF", "ON", "ACC", "START", "IGNITION",
			"STALL", "PUSHSTART",
			"ASSEMBLE", "DISASSEMBLE", "EJECT", "CDIN",
			"USE",
			// CD PARCA TUSLARI (dumper kaniti: dump2, TrackChannelSwitch
			// ChangeTrack/ChangeChannel). Sarki/kanal degistirme NEXT, CD,
			// FOLKRADIO, CHANNEL2 ile olur; listede yoktu, tus karsida
			// olu gidiyordu (USE/ON/OFF zaten akiyordu). Yalniz kayitli
			// kontrollerin bildirdigi olaylar kuyruga girer; CD1/CD2/CD3,
			// PROCEED, LOOP, SUSKI, GLOBALEVENT, BATTERY bilerek DISARIDA
			// (dongu/internal, canli log + dumper karsilastirmasi).
			"NEXT", "CD", "FOLKRADIO", "CHANNEL2",
			// NOT: OPEN/CLOSE buraya EKLEMMEZ (geri alindi). TV guc
			// dugmesi (TV/Switch) kendi dongusunde her saniye OPEN+CLOSE
			// bildiriyor; listeye girince ag her saniye 2 paket + karsi
			// tarafta sonsuz tekrar uretiyordu. CD kapagi icin prob
			// ([PROBE] switch '.../cd') gercek olayi gosterecek; hedefli
			// cozum o zaman yazilir.
		};

		/// <summary>Bir kontrol hedefi (nesne + FSM + kabul edilen olaylar).</summary>
		class Control {
			public string Path;
			public string FsmName;
			public PlayMakerFSM Fsm;
			public string LastState = "";
			public string[] Events;
			public string PendingState = "";
			/// <summary>
			/// Ev salterinin son gonderilen ACIK/KAPALI degeri (bilinmiyorsa -1).
			/// Salter `USE` bir TOGGLE oldugu icin sayim yanlis karsi tarafi
			/// SONUK birakiyordu; mutlak durum gondermek icin tutulur.
			/// </summary>
			public int LastSwitchOn = -1;
		}

		static readonly List<Control> controls = new List<Control>(16);
		static readonly Dictionary<string, Control> controlByKey
			= new Dictionary<string, Control>(StringComparer.Ordinal);
		static readonly Dictionary<PlayMakerFSM, Control> controlByFsm
			= new Dictionary<PlayMakerFSM, Control>(16);

		public static string ControlKey(string path, string fsmName) {
			return (path ?? "") + "\u0001" + (fsmName ?? "");
		}

		/// <summary>
		/// Bu FSM bir kontrol (lamba/kontak tusu vb.) mi? Carry gibi tuketici
		/// sistemler USE olayini kapmadan once sorar; kontroller tasma degil,
		/// CtrlState yolundan senkronize olur.
		/// </summary>
		public static bool IsKnownControl(PlayMakerFSM fsm) {
			if (fsm == null) return false;
			try { return controlByFsm.ContainsKey(fsm); } catch { return false; }
		}

		public static int SentControlEvents;
		public static int AppliedControlEvents;
		public static int UnresolvedControls;

		// --- KAPI YONU TANIISI -------------------------------------------
		// Yon olay adindan okunmasi yerine kacinin gercek durumundan
		// okunuyor. Bu sayaclar olcumu gosterir: `DirectionMeasured` sifir
		// ise hicbir kapida bool/aci okunamamis demektir (FSM tablosu
		// degismis olabilir).
		/// <summary>Kapi yonu `Open` bool'u / menteve acisindan okundu.</summary>
		public static int DirectionMeasured;
		/// <summary>Kapi yonu yalnizca olay adindan okunabildi (fallback).</summary>
		public static int DirectionFromEvent;
		/// <summary>Ayni yon 0.3 sn icinde tekrar uretildi, bastirildi.</summary>
		public static int SameDirectionDrops;

		/// <summary>
		/// UI (chat/konsol) acikken susturulan gonderim sayisi.
		/// Sifirdan buyukse kamera donukken UI tiklamalari dunya tiklamasina
		/// donusuyordu (canli belirti: "parcalar kendiliginden sokuluyor").
		/// </summary>
		public static int UiBlockedEvents;

		/// <summary>Kontrol nesnesini olay tablosuna kaydet.</summary>
		/// <summary>
		/// Ev/tesisat kontrol nesneleri (isik salterleri, sigorta tablosu).
		///
		/// Dumper kaniti (switch_scan_out.txt: 178 isik/salter nesnesi;
		/// fsms.json: YARD/Building/.../Switch + Use FSM'i): ev salterleri
		/// "Switch"/"MainSwitch" adinda, Use FSM'li etkilesimli dugmelerdir.
		/// Eski filtre yalnizca CAR_CONTROL_OBJECTS'a bakiyordu, bunlar
		/// kayda girmiyor, USE/ON/OFF olaylari ag'a hic cikmiyordu.
		/// </summary>
		static readonly string[] HOUSE_CONTROL_OBJECTS = {
			"Switch", "MainSwitch",
		};

		static bool IsHouseControl(GameObject go) {
			if (go == null) return false;
			string n = null;
			try { n = go.name; } catch { return false; }
			if (string.IsNullOrEmpty(n)) return false;
			bool hit = false;
			for (int i = 0; i < HOUSE_CONTROL_OBJECTS.Length; i++) {
				if (string.Equals(n, HOUSE_CONTROL_OBJECTS[i], StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
			}
			// switch_wc gibi isimli isik salterleri: "Switch" esitligi yetmez.
			if (!hit && n.StartsWith("switch", StringComparison.OrdinalIgnoreCase)) hit = true;
			if (!hit) return false;
			// Kapi dislama: ayni adli kapi parcalari (orn. switch_door) kapi
			// yolunda kalmali; kontrol tablosu FSM olay yolunu calar.
			try { if (IsTrackedDoor(go)) return false; } catch { }
			return true;
		}

		static void TryRegisterControl(GameObject go) {
			try {
				bool houseSwitch = IsHouseControl(go);
				if (!IsCarControl(go.name) && !houseSwitch && !IsFlashlight(go)) return;
				string path = SceneDump.PathOf(go.transform);
				if (string.IsNullOrEmpty(path)) return;
				PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
				for (int f = 0; fsms != null && f < fsms.Length; f++) {
					PlayMakerFSM fsm = fsms[f];
					if (fsm == null || fsm.Fsm == null) continue;
					string fn = fsm.Fsm.Name;
					if (string.IsNullOrEmpty(fn)) continue;
					// Bu FSM gerÃ§ekten bir kontrol olayÄ± bildiriyor mu?
					// EV SALTERLERINDE `mouseDown` da kabul edilir: PlayMaker
					// `MousePickEvent` tiklamayi `mouseDown` olarak uretir, `USE`
					// yalnizca global gecis kosuludur. Bu olmadan hicbir ev
					// salteri tabloya giremez (bkz. DeclaredControlEvents notu).
					string[] ev = DeclaredControlEvents(fsm, houseSwitch);
					if (ev == null) continue;

					string key = path + "" + fn;
					if (controlByKey.ContainsKey(key)) continue;
					Control c = new Control();
					c.Path = path;
					c.FsmName = fn;
					c.Fsm = fsm;
					c.Events = ev;
					controls.Add(c);
					controlByKey[key] = c;
					controlByFsm[fsm] = c;
				}
			}
			catch { }
		}

		static bool IsCarControl(string n) {
			for (int i = 0; i < CAR_CONTROL_OBJECTS.Length; i++)
				if (string.Equals(n, CAR_CONTROL_OBJECTS[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>FSM'in bildirdiÄŸi kontrol olaylarÄ± (yoksa null).</summary>
		static string[] DeclaredControlEvents(PlayMakerFSM fsm) {
			return DeclaredControlEvents(fsm, false);
		}

		/// <summary>
		/// SALTER TIKLAMASI (`mouseDown`) de kontrol olayı sayılır.
		///
		/// CANLI KANIT (client logu): `switch_kitchen` / `switch_bedroomBoy`
		/// vs. HİÇBİR `[ACT] CTRL` satırı üretmiyor — yani hiç kaydedilmemişler.
		///
		/// NEDEN: PlayMaker `MousePickEvent` action'ı tıklamayı `mouseDown`
		/// olarak ÜRETİR. `USE`/`ON`/`OFF` yalnızca FSM'in GLOBAL GEÇİŞ
		/// koşullarıdır, dışarıdan gelmezler. `CONTROL_EVENTS` içinde
		/// `mouseDown` olmadığı için `DeclaredControlEvents` null dönüyor,
		/// `TryRegisterControl` `continue` ediyor ve anahtar hiç tabloya
		/// girmiyor → ışık senkronu hiç başlamıyor.
		///
		/// Yalnız EV SALTERLERİ için genişletilir: `mouseDown` her
		/// MousePickEvent'li nesnede vardır, genel tabloya girseydi bütün
		/// tıklanabilir nesneler kontrol olurdu.
		/// </summary>
		static string[] DeclaredControlEvents(PlayMakerFSM fsm, bool allowClickEvents) {
			try {
				List<string> ev = FsmNames.DeclaredEvents(fsm);
				if (ev == null || ev.Count == 0) return null;
				List<string> hit = new List<string>(4);
				for (int i = 0; i < ev.Count; i++) {
					for (int k = 0; k < CONTROL_EVENTS.Length; k++) {
						if (string.Equals(ev[i], CONTROL_EVENTS[k], StringComparison.OrdinalIgnoreCase)) {
							hit.Add(ev[i]);
							break;
						}
					}
					if (hit.Count > 0) continue;
					if (!allowClickEvents) continue;
					if (IsSwitchClickEvent(ev[i]) && !ContainsEvent(hit, ev[i])) hit.Add(ev[i]);
				}
				return (hit.Count > 0) ? hit.ToArray() : null;
			}
			catch { }
			return null;
		}

		/// <summary>
		/// Ev salterinin ACIK/KAPALI durumunu `Lights` nesnesinden okur.
		/// Dumper kaniti (LightSwitches/switch_wc fsm='Use'):
		///   Wait button -> ActivateGameObject(Lights)
		/// Deger bulunamazsa -1 doner (cagiran eski yola duser).
		/// </summary>
		static int ReadSwitchOn(PlayMakerFSM fsm) {
			if (fsm == null || fsm.Fsm == null) return -1;
			try {
				FsmVariables v = fsm.FsmVariables;
				if (v == null) return -1;
				FsmGameObject[] gos = v.GameObjectVariables;
				for (int i = 0; gos != null && i < gos.Length; i++) {
					if (gos[i] == null || gos[i].Name == null) continue;
					// v27: sabit "Lights" yerine DOKUMDA DOGRULENMIS isimler.
					// SMP_SWITCHON_V27
					if (!IsSwitchTargetName(gos[i].Name)) continue;
					GameObject lit = gos[i].Value;
					if (lit == null) continue;
					return lit.activeSelf ? 1 : 0;
				}
			} catch { }
			return -1;
		}

		/// <summary>
		/// v27: anahtar durumu okunacak nesne degiskenleri.
		///
		/// DOKUM KANITI (her biri bildirilen kuyrukta birebir var):
		///   `Lights`        8 `switch_*|Use` (LightSwitches)  -- CALISIYOR
		///   `ShowerSwitch`  `BATHROOM/Shower/Switch|Use`
		///   `TapOn`         `BATHROOM/Shower/Valve|Switch`
		///   `ShowerOn`      `BATHROOM/Shower/Valve|Switch`
		///   `Tap`/`Shower`  `BATHROOM/Shower/Valve|Switch` (ActivateGameObject)
		///   `Water`         `KITCHEN/KitchenWaterTap/Trigger|Use`
		///   `SwitchOn`      `KITCHEN/KitchenWaterTap/Trigger|Use`
		///
		/// ESKI KOD yalniz `"Lights"` ariyordu; digerleri `-1` donup
		/// `ResolveSwitchDeferred`de `e.Event = ""` yapip olayi SESSIZCE
		/// dusuruyordu. Log kaniti: shower switch icin tek satir var
		/// (`ev='LEVEL LOADED'`), hicbir `[ACT] CTRL USE:1` degil.
		/// </summary>
		static readonly string[] SWITCH_TARGET_NAMES = {
			"Lights",       // 8 isik anahtari (calisan yol)
			"ShowerSwitch", // BATHROOM/Shower/Switch|Use
			"TapOn",        // BATHROOM/Shower/Valve|Switch
			"ShowerOn",     // BATHROOM/Shower/Valve|Switch
			"Tap",          // BATHROOM/Shower/Valve|Switch
			"Shower",       // BATHROOM/Shower/Valve|Switch
			"Water",        // KITCHEN/KitchenWaterTap/Trigger|Use
			"SwitchOn",     // KITCHEN/KitchenWaterTap/Trigger|Use
		};

		/// <summary>Anahtar durumu okunacak GameObject degiskeni mi?</summary>
		static bool IsSwitchTargetName(string n) {
			if (string.IsNullOrEmpty(n)) return false;
			for (int i = 0; i < SWITCH_TARGET_NAMES.Length; i++) {
				if (string.Equals(n, SWITCH_TARGET_NAMES[i], StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		/// <summary>Salter olayini kuyruga al (QueueControlEvent'in son bolumu).</summary>
		static void QueueControlEventNow(Control c, string eventName) {
			for (int i = 0; i < doorOutbox.Count; i++) {
				DoorEvent q = doorOutbox[i];
				if (string.Equals(q.Path, c.Path, StringComparison.Ordinal)
					&& string.Equals(q.FsmName, c.FsmName, StringComparison.Ordinal)
					&& string.Equals(q.Event, eventName, StringComparison.Ordinal)) return;
			}
			// Ayni kontrolun ESKI durumu kuyruktaysa yerini al (birlestirme):
			// karsi taraf iki zit durum görmesin.
			for (int i = doorOutbox.Count - 1; i >= 0; i--) {
				DoorEvent q = doorOutbox[i];
				if (string.Equals(q.Path, c.Path, StringComparison.Ordinal)
					&& string.Equals(q.FsmName, c.FsmName, StringComparison.Ordinal)
					&& q.Event.StartsWith("USE:", StringComparison.Ordinal))
					doorOutbox.RemoveAt(i);
			}
			if (doorOutbox.Count >= DOOR_QUEUE_LIMIT) doorOutbox.RemoveAt(0);
			DoorEvent e = new DoorEvent();
			e.Path = c.Path;
			e.FsmName = c.FsmName;
			e.Event = eventName;
			doorOutbox.Add(e);
		}

		/// <summary>Salter durumu degismedi, gonderilmedi (teshis sayaci).</summary>
		public static int SwitchStateNoop;
		/// <summary>Salter icin ertelenmis (cozulmemis) kuyruk isareti.</summary>
		public const string SWITCH_PENDING = "USE?";
		/// <summary>Flush aninda cozulen salter sayisi (teshis).</summary>
		public static int SwitchResolved;

		/// <summary>
		/// Ev salterinin tiklamasini kuyruga COZULMEMIS olarak koyar.
		/// Durum flush aninda okunur (toggle sonrasi deger).
		/// </summary>
		static void QueueSwitchDeferred(Control c) {
			for (int i = doorOutbox.Count - 1; i >= 0; i--) {
				DoorEvent q = doorOutbox[i];
				if (string.Equals(q.Path, c.Path, StringComparison.Ordinal)
					&& string.Equals(q.FsmName, c.FsmName, StringComparison.Ordinal)) {
					// Zaten bekleyen bir salter kaydi var: yeni tiklama onu
					// TAZELER (son durum gonderilir), ikinci kayit acilmaz.
					q.Event = SWITCH_PENDING;
					SwitchCoalesced++;
					return;
				}
			}
			if (doorOutbox.Count >= DOOR_QUEUE_LIMIT) doorOutbox.RemoveAt(0);
			DoorEvent e = new DoorEvent();
			e.Path = c.Path;
			e.FsmName = c.FsmName;
			e.Event = SWITCH_PENDING;
			doorOutbox.Add(e);
		}

		/// <summary>Bekleyen salter isaretini cozup gercek olaya cevirir.</summary>
		static void ResolveSwitchDeferred(ref DoorEvent e) {
			if (!string.Equals(e.Event, SWITCH_PENDING, StringComparison.Ordinal)) return;
			Control c;
			// BUG FIX: Kayıt sırasında key `path + "\u0001" + fn` olarak yapılıyor (satır 556).
			// Önceden burada `e.Path + "" + e.FsmName` (boş string ayraç) kullanılıyordu,
			// bu yüzden key hiçbir zaman eşleşmiyordu → ev ışıkları hiç senkronize olmuyordu.
			if (!controlByKey.TryGetValue(e.Path + "\u0001" + e.FsmName, out c) || c == null
				|| c.Fsm == null) {
				e.Event = "";   // cozulemiyor -> dusur
				return;
			}
			int on = ReadSwitchOn(c.Fsm);
			if (on < 0) { e.Event = ""; return; }
			// Yon degismediyse gonderme: karsi taraf zaten ayni durumda ve
			// `USE` bir TOGGLE oldugu icin gereksiz gonderim yanlis yone sapar.
			if (c.LastSwitchOn == on) {
				SwitchStateNoop++;
				e.Event = "";
				return;
			}
			c.LastSwitchOn = on;
			e.Event = (on != 0) ? "USE:1" : "USE:0";
			SwitchResolved++;
			SentControlEvents++;
			SmpTrace.Act("CTRL", e.Event, e.Path + "  " + e.FsmName + "  (mutlak durum)");
		}

		/// <summary>Arka arkaya tiklamalarin birlestirildigi sayi.</summary>
		public static int SwitchCoalesced;

		static bool ContainsEvent(List<string> list, string name) {
			for (int i = 0; i < list.Count; i++)
				if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>Kapinin `Open` bool degiskenini bulur (yoksa null).</summary>
		static FsmBool FindDoorOpenBool(PlayMakerFSM fsm) {
			if (fsm == null || fsm.Fsm == null) return null;
			FsmVariables v;
			try { v = fsm.FsmVariables; } catch { return null; }
			if (v == null) return null;
			FsmBool[] bools = v.BoolVariables;
			for (int i = 0; bools != null && i < bools.Length; i++) {
				if (bools[i] == null || bools[i].Name == null) continue;
				if (string.Equals(bools[i].Name, "Open", StringComparison.Ordinal)) return bools[i];
			}
			string[] alt = { "DoorOpen", "IsOpen", "Opened" };
			for (int a = 0; a < alt.Length; a++) {
				for (int i = 0; bools != null && i < bools.Length; i++) {
					if (bools[i] == null || bools[i].Name == null) continue;
					if (string.Equals(bools[i].Name, alt[a], StringComparison.OrdinalIgnoreCase))
						return bools[i];
				}
			}
			return null;
		}

		/// <summary>
		/// Bu kapi EV KAPISI mi (animasyonlu) yoksa ARAÇ KAPISI mi (menteveli)?
		///
		/// AYRI YOL GEREKÇESI (dumper kaniti, dump2/level2.json):
		///
		///  EV KAPISI -- COTTAGE/LOD/Doors/DoorFront1/Pivot/Handle, fsm=Use
		///    Wait player --USE--> Check position --BoolTest(DoorOpen)-->
		///      Open door : PlayAnimation(store_door_open)
		///      Close door: PlayAnimation(store_door_close)
		///    `Wait player` YALNIZCA `USE` kabul eder; `OPEN`/`CLOSE` yalniz
		///    `Check position`dan cikar. Kanit (client logu):
		///      "olay ilerletmedi (Wait player), duruma zorlandi: Close door"
		///    -> ev kapisina `USE` gonderilmeli.
		///
		///  ARAÇ KAPISI -- CARPARTS/PartsCar/door left(Clone), fsm=Use
		///    Mouse off --Door(tiklama)--> Check position --BoolTest(Open)-->
		///      Open door / Open door 2 (SetHingeJointLimits + AddTorque)
		///    `OPEN`/`CLOSE` GLOBAL olaylardir; `USE` bildirilmez.
		///
		/// AYIRMA KURALI: `USE` bildiren kapilar ev kapisidir (animasyonlu),
		/// bildirmeyenler arac kapisidir (menteveli). `USE` gonderilmedigi
		/// icin ev kapisinda animasyon hic oynamaz -- canli belirti.
		/// </summary>
		static bool IsAnimDoor(Door d) {
			if (d == null) return false;
			// Ogrenilmis sonucu sakla (siniflandirma kapi basina bir kez).
			if (d.KindKnown) return d.IsAnim;
			// `EventIndex` global `DOOR_EVENTS` dizisinde arar; `d.Events`
			// o dizinin kapinin BILDIRDIKLERI maskesidir (bool[]).
			int useIdx = EventIndex("USE");
			d.IsAnim = (useIdx >= 0 && d.Events != null && useIdx < d.Events.Length
				&& d.Events[useIdx]);
			d.KindKnown = true;
			return d.IsAnim;
		}

		static string ActiveDoorState(Door d) {
			try { return (d != null && d.Fsm != null && d.Fsm.Fsm != null)
				? d.Fsm.Fsm.ActiveStateName : null; }
			catch { return null; }
		}

		/// <summary>Aktif kapi surus job'lari (kapi basina en fazla bir).</summary>
		static readonly List<Door> driveJobs = new List<Door>(4);

		/// <summary>Job baslat/degistir (uzaktan kapi mesaji gelince).</summary>
		static void StartDoorDrive(Door d, bool wantOpen, string fromState) {
			if (d == null) return;
			d.DriveActive = true;
			d.DriveWantOpen = wantOpen;
			d.DriveDeadline = Time.time + 2.5f;
			d.DriveSent = 0;
			d.DriveStory = "basla:" + (fromState ?? "?") + " ";
			d.DriveLockState = null;
			d.DriveLockEvent = null;
			d.DriveWait = 0;
			d.DriveSendState = null;
			d.DriveStall = 0;
			// Yanki penceresi job boyunca taze tutulur (TickDoorDrives icinde
			// her gonderimde yenilenir); zincirin urettigi ara olaylar aga
			// geri gitmez.
			d.AppliedAt = Time.time;
			d.AppliedOpen = wantOpen;
			d.SendDampedAt = Time.time;
			bool found = false;
			for (int i = 0; i < driveJobs.Count; i++)
				if (driveJobs[i] == d) { found = true; break; }
			if (!found) driveJobs.Add(d);
		}

		/// <summary>Job surucu Tick (karede bir; Tick() basindan cagrilir).</summary>
		static void TickDoorDrives() {
			for (int i = driveJobs.Count - 1; i >= 0; i--) {
				Door d = driveJobs[i];
				if (d == null || d.Fsm == null || !d.DriveActive) {
					driveJobs.RemoveAt(i);
					continue;
				}
				bool done = false;
				try { done = StepDoorDrive(d); }
				catch { done = true; }
				if (done || Time.time > d.DriveDeadline) {
					if (Time.time > d.DriveDeadline && !done)
						d.DriveStory += "SURE ";
					FinishDoorDrive(d);
					driveJobs.RemoveAt(i);
				}
			}
		}

		static void FinishDoorDrive(Door d) {
			try {
				d.DriveActive = false;
				SmpLog.Print("[DOOR-CHAIN] '" + d.Path + "' ("
					+ (d.DriveWantOpen ? "AC" : "KAPAT") + ") " + d.DriveSent
					+ " gonderim: " + d.DriveStory);
			} catch { }
		}

		/// <summary>
		/// <summary>Bir kapi surus isinde EN FAZLA bu kadar olay gonderilir.</summary>
		public const int DOOR_DRIVE_MAX_SENDS = 8;

		/// <summary>
		/// Ayni durumda bu kadar ardisik gonderimden sonra is birakilir.
		/// FSM ilerlemiyorsa daha fazla gonderim zararlidir (bkz. donme korumasi).
		/// </summary>
		public const int DOOR_DRIVE_STALL_LIMIT = 3;

		/// Tek adim: bir karede EN FAZLA bir olay. true = job bitti.
		///
		/// DUMPER KANITI (explore, offset cozumu):
		///  EV : Wait player -FINISHED-> Wait button -USE-> Check position
		///       -BoolTest-> Open/Close door.
		///  ARAC: Mouse off -FINISHED-> Check position -BoolTest-> Mouse over
		///       1/2 -FINISHED-> Open door/Open door 2.
		///  BOOTLID: Mouse over -OPEN-> Open hood (ac);
		///       Mouse over 2 -OPEN-> Drop -CLOSE-> Sound (kapat).
		///  HOOD: Mouse over 3 -OPEN-> Open hood 2 (ac);
		///       Mouse over 4 -OPEN-> State 2 -CLOSE-> Sound (kapat).
		/// </summary>
		static bool StepDoorDrive(Door d) {
			string st = ActiveDoorState(d);
			if (string.IsNullOrEmpty(st)) return true;
			bool wantOpen = d.DriveWantOpen;
			string send = null;

			if (IsAnimDoor(d)) {
				if (string.Equals(st, "Wait player", StringComparison.Ordinal))
					send = "FINISHED";
				else if (string.Equals(st, "Wait button", StringComparison.Ordinal))
					send = "USE";
				else return true;   // karar/animasyon: oyun surdurur
			}
			else {
				if (string.Equals(st, "Mouse off", StringComparison.Ordinal)
					|| string.Equals(st, "Mouse off 2", StringComparison.Ordinal)) {
					send = "FINISHED";
				}
				else if (string.Equals(st, "Check position", StringComparison.Ordinal)
					|| string.Equals(st, "Check if open", StringComparison.Ordinal)
					|| string.Equals(st, "Check if open 2", StringComparison.Ordinal)) {
					// Otomatik karara bir kare muhlet (BoolTest ateslensin).
					if (d.DriveWait >= 2) {
						send = wantOpen ? "OPEN" : "CLOSE";
						d.DriveWait = 0;
					}
					else { d.DriveWait++; return false; }
				}
				else if (string.Equals(st, "Mouse over 1", StringComparison.Ordinal)) {
					if (!wantOpen) { d.DriveStory += "YON-UYUSMAZLIK "; return true; }
					send = "FINISHED";
				}
				else if (string.Equals(st, "Mouse over", StringComparison.Ordinal)) {
					if (!wantOpen) { d.DriveStory += "YON-UYUSMAZLIK "; return true; }
					send = "OPEN";
				}
				else if (string.Equals(st, "Mouse over 2", StringComparison.Ordinal)) {
					if (wantOpen) { d.DriveStory += "YON-UYUSMAZLIK "; return true; }
					send = HasState(d.Fsm, "Drop") ? "OPEN" : "FINISHED";
				}
				else if (string.Equals(st, "Mouse over 3", StringComparison.Ordinal)) {
					if (!wantOpen) { d.DriveStory += "YON-UYUSMAZLIK "; return true; }
					send = "OPEN";
				}
				else if (string.Equals(st, "Mouse over 4", StringComparison.Ordinal)) {
					if (wantOpen) { d.DriveStory += "YON-UYUSMAZLIK "; return true; }
					send = "OPEN";
				}
				else if (string.Equals(st, "State 2", StringComparison.Ordinal)) {
					if (wantOpen) { d.DriveStory += "YON-UYUSMAZLIK "; return true; }
					send = "CLOSE";
				}
				else if (string.Equals(st, "Drop", StringComparison.Ordinal)) {
					if (wantOpen) { d.DriveStory += "YON-UYUSMAZLIK "; return true; }
					send = "CLOSE";
				}
				else return true;   // Open door/hood, Close, Sound, Idle...: yerlesti
			}

			if (send == null) return false;
			// KILIT: ayni (durum,olay) ikinci kezse varyant uymuyor.
			// Mouse-off ozel: FINISHED kilitlenirse tel yonu bir kez dene.
			if (string.Equals(d.DriveLockState, st, StringComparison.Ordinal)
				&& string.Equals(d.DriveLockEvent, send, StringComparison.Ordinal)) {
				if ((string.Equals(st, "Mouse off", StringComparison.Ordinal)
					|| string.Equals(st, "Mouse off 2", StringComparison.Ordinal))
					&& string.Equals(send, "FINISHED", StringComparison.Ordinal)) {
					send = wantOpen ? "OPEN" : "CLOSE";
					d.DriveStory += "(tel-yon) ";
				}
				else { d.DriveStory += "KILIT:" + st + " "; return true; }
			}
			// -------------------------------------------------------------
			// DONME KORUMASI -- "kapiyi actim, butun parcalar koptu"
			// -------------------------------------------------------------
			// CANLI KANIT (client logu):
			//   [DOOR-CHAIN] 'SATSUMA(557kg, 248)/Body/pivot_door_left/
			//               door left(Clone)' (KAPAT) 532 gonderim:
			//     basla:Mouse off FINISHED (tel-yon) CLOSE FINISHED (tel-yon) CLOSE ...
			//   Ayni desen garaj kapilarinda 590 gonderim. FSM `Mouse off`
			//   durumunda ASLA ilerlemiyor; 2.5 saniyede yuzlerce olay
			//   enjekte ediliyor ve is `SURE` (sure doldu) ile bitiyor.
			//
			// ARAC KAPISI `Open door 2` durumunda `SetHingeJointLimits` +
			// `AddTorque` calistirir. Ayni durumda yuzlerce kez calismasi
			// menteve/arabeliri patlatir, araba sarsilir ve Satsuma'daki
			// gevsek parcalar (yolunda `(xxxxx)` olanlar) fizik bosluguna
			// firlatilir. RecoveryManager loglarindaki
			//   "recovered from Y=-32768.0 to 3.8"
			//   "recovered from Y=491520.0 to 3.8"
			// satirlari BU etkinin sonucudur.
			//
			// COZUM: (1) is basina gonderim siniri, (2) ayni durumda
			// ardisik gonderim sayaci. Bozuk varyant en kotu halde birkac
			// olay alir ve orada kalir; parcalari kirmaz. Duz varyant
			// zinciri durum degistirdigi icin sayac sifir kalir ve normal
			// calisir.
			if (d.DriveSent >= DOOR_DRIVE_MAX_SENDS) {
				d.DriveStory += "GONDERIM-SINIRI ";
				return true;
			}
			if (string.Equals(d.DriveSendState, st, StringComparison.Ordinal)) d.DriveStall++;
			else { d.DriveSendState = st; d.DriveStall = 0; }
			if (d.DriveStall >= DOOR_DRIVE_STALL_LIMIT) {
				d.DriveStory += "DURUM-ILERLEMEDI(" + st + ") ";
				SmpLog.Debug("world", "door", "uzaktan kapi surusu durdu: '" + d.Path
					+ "' " + d.DriveSent + " gonderim sonrasi hala '" + st + "' durumunda.");
				return true;
			}

			d.DriveLockState = st;
			d.DriveLockEvent = send;
			EchoGuard.Scope scope = EchoGuard.Enter();
			try {
				d.Fsm.SendEvent(send);
				d.DriveSent++;
				d.AppliedAt = Time.time;
				d.DriveStory += send + " ";
			}
			catch { d.DriveStory += send + "! "; }
			finally { scope.Dispose(); }
			return false;
		}


		/// <summary>Olay adi yon tasiyor mu?</summary>
		static bool EventIsOpen(string eventName) {
			if (string.IsNullOrEmpty(eventName)) return false;
			return string.Equals(eventName, "OPEN", StringComparison.Ordinal)
				|| string.Equals(eventName, "OPENDOOR", StringComparison.Ordinal)
				|| string.Equals(eventName, "DoorOpen", StringComparison.Ordinal);
		}

		/// <summary>
		/// Kacinin GERCEK durumunu okur ve aga gonderilecek yonu belirler.
		///
		/// CANLI KANIT (host logu 13:22): Satsuma `Use` FSM'i kapama yonunda
		/// da `OPEN` uretiyor. Dort tiklama -> dort `OPEN`, hic `CLOSE`; ama
		/// `[DOOR-VAR]` kaci 0 -> 289 -> 0 -> 289 donduruyor. Yani OLAY ADI
		/// YON TASIYAMAZ, kacinin durumu tasir.
		///
		/// OKUMA SIRASI (guvenilirlik azalan):
		///  1. `Open` bool'u  -- dumper kaniti: `door left(Clone)`/`bootlid`
		///     `Use` FSM'inde `Open`, `LockStrenght`, `Rot` degiskenleri var.
		///     `Check if open` durumu bu bool'a bakar.
		///  2. `Rot` / `Rotation` menteve acisi -- `[DOOR-VAR]` kaniti:
		///     kapaliyken `0.00`, acikken `289.82`. 45 derece esik.
		///  3. Olay adi (fallback).
		///
		/// `source` cikisi: "bool" | "angle" | "event".
		/// </summary>
		static void ReadDoorDirection(Door d, string eventName, out bool open, out string source) {
			open = false; source = "event";
			if (d == null || d.Fsm == null) { open = EventIsOpen(eventName); return; }
			try {
				FsmBool b = FindDoorOpenBool(d.Fsm);
				if (b != null) { open = b.Value; source = "bool"; return; }
				FsmFloat f = FsmNames.Float(d.Fsm, "Rot");
				if (f == null) f = FsmNames.Float(d.Fsm, "Rotation");
				if (f != null) {
					float deg = f.Value;
					while (deg > 180f) deg -= 360f;
					while (deg < -180f) deg += 360f;
					open = (deg > 45f || deg < -45f);
					source = "angle";
					return;
				}
			} catch { }
			open = EventIsOpen(eventName);
		}

		/// <summary>
		/// Kapi FSM'inin `Open` bool'unu hedef yone gore yazar.
		///
		/// BU, Satsuma/ev kapilarinin calismasinin anahtaridir. Dumper
		/// kaniti (dump2/level2.json, `door left(Clone)` fsm=`Use`):
		///   states: Mouse off / Mouse over 2 / Check if open / Open door /
		///           Close door
		///   bool  : Open, LockStrenght, OpenLockJointTorque, RayDistance
		/// "Check if open" durumu `Open` bool'une bakar ve dogru yonu
		/// uretir. Biz yalnizca durumu zorladigimiz icin bool degismiyor,
		/// FSM her dongu yanlis yonu uretiyor ( sonsuz OPEN dongusu ->
		/// "kapilar kendiliginden aciliyor" + "kapama hic gormuyorum").
		///
		/// `Open` bool'u yazilmadan ONCE hangi bool'un aranacagi bilinmez;
		/// bu yuzden FSM'in tum `FsmBool` degiskenleri taranir ve adi tam
		/// "Open" olan (varsa) yazilir. Bulunamazsa hicbir sey yapilmaz.
		/// </summary>
		static void WriteOpenBool(Door d, bool open) {
			if (d == null || d.Fsm == null || d.Fsm.Fsm == null) return;
			FsmBool b = FindDoorOpenBool(d.Fsm);
			if (b == null) return;
			b.Value = open;
			SmpLog.Print("[DOOR-OPENBOOL] '" + d.Path + "' " + b.Name
				+ "=" + (open ? "True" : "False") + " (yazildi)");
		}

		/// <summary>Kontrol olayÄ±nÄ± kuyruÄŸa al (kapÄ± olayÄ±yla AYNI kabloyu kullanÄ±r).</summary>
		/// <summary>Ev salteri kontrolu mu? (USE-disi ic olaylar elenir.)</summary>
		/// <summary>
		/// El feneri ailesi mi? Kok Use (acma/kapama) + ChangeBatteries/Remove
		/// (pil cikarma) dumper kanitli (dump2: ON/OFF/USE + Insert/Assembly).
		/// Pil TAKMA Insert/Assembly'den parca yolunda akar; burada yalniz
		/// acma/kapama (ON/OFF) kuyruga girer. USE tasmaindir (Carry tasir);
		/// karsida USE oynatmak oyunun alma mantigini tetikler.
		/// </summary>
		static bool IsFlashlight(GameObject go) {
			if (go == null) return false;
			try {
				Transform t = go.transform;
				int g = 0;
				while (t != null && g++ < 5) {
					if (t.name.IndexOf("flashlight", StringComparison.OrdinalIgnoreCase) >= 0) return true;
					t = t.parent;
				}
			} catch { }
			return false;
		}

		static bool IsFlashlightControl(Control c) {
			if (c == null || string.IsNullOrEmpty(c.Path)) return false;
			return c.Path.IndexOf("flashlight", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool IsHouseSwitchControl(Control c) {
			if (c == null || string.IsNullOrEmpty(c.Path)) return false;
			int cut = c.Path.LastIndexOf('/');
			string leaf = cut >= 0 ? c.Path.Substring(cut + 1) : c.Path;
			if (string.Equals(leaf, "Switch", StringComparison.OrdinalIgnoreCase)) return true;
			if (string.Equals(leaf, "MainSwitch", StringComparison.OrdinalIgnoreCase)) return true;
			if (leaf.StartsWith("switch", StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>Kontrol olayını kuyruğa al (kapı olayıyla AYNI kabloyu kullanır).</summary>
		/// <summary>
		/// Ev salterinde disaridan (fare) gelen tiklama olayi mi?
		///
		/// Dumper kaniti: PlayMaker `MousePickEvent` action'i tiklamayi
		/// `mouseDown` / `mouseUp` olarak URETIR; salterin `Use` FSM'i
		/// `USE`/`Switch` yalnizca global gecis olarak tanimlar.
		/// Bu yuzden `USE` dinleyerek salteri yakalamak mumkun degil.
		/// </summary>
		static bool IsSwitchClickEvent(string name) {
			if (string.IsNullOrEmpty(name)) return false;
			return string.Equals(name, "mouseDown", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(name, "DOWN", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(name, "CLICK", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(name, "Switch", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(name, "Toggle", StringComparison.OrdinalIgnoreCase);
		}

		static void QueueControlEvent(PlayMakerFSM fsm, string eventName) {
			Control c;
			if (!controlByFsm.TryGetValue(fsm, out c) || c == null) {
				// Gec klon (kurulum ortasinda takilan CD dugmeleri vb.):
				// o an kaydet, tekrar dene. Kayda girmeyen sahte istek
				// sayaci sismirmez.
				try {
					GameObject cgo = fsm != null ? fsm.gameObject : null;
					if (cgo != null) TryRegisterControl(cgo);
				} catch { }
				if (!controlByFsm.TryGetValue(fsm, out c) || c == null) {
					UnresolvedControls++;
					return;
				}
			}
			bool declared = false;
			for (int i = 0; c.Events != null && i < c.Events.Length; i++) {
				if (string.Equals(c.Events[i], eventName, StringComparison.OrdinalIgnoreCase)) {
					declared = true;
					break;
				}
			}
			if (!declared) return;

			// EV SALTERI KURALI (duzeltildi: "isik dugmeleri senkron degil")
			//
			// SALTERIN IC OPEN/CLOSE olaylari oyunun kendi guc dongusudur
			// (TV her saniye OPEN+CLOSE uretir); aga cikarsa hem spam hem
			// karsi tarafta tekrar dongusu olur.
			//
			// ANCAK disaridan gelen TIKLAMA `USE` DEGILDIR. Dumper kaniti
			// (dump2/level2.json, YARD/.../LightSwitches/switch_kitchen fsm='Use'):
			//   MousePickEvent  mouseDown mouseUp mouseOff
			//   USE  Switch  ON  OFF  ActivateGameObject(Lights)  SaveBool
			// PlayMaker `MousePickEvent` tiklamayi `mouseDown` OLARAK URETIR;
			// `USE` sadece FSM'in global gecis kosulu, tiklamayla gelmez.
			// Eski "yalniz USE cikar" kurali bu yuzden HICBIR sey gecirmiyordu
			// ve ev isiklari hic senkron olmuyordu.
			//
			// COZUM: salterde tiklama olaylari kabul edilir ve aga NORMALIZE
			// edilmis `USE` olarak gonderilir; karsi taraf da `USE` oynatir
			// ve `Lights` nesnesini oyunun kendi koduyla acar/kapatir.
			if (IsHouseSwitchControl(c)) {
				bool isUse = string.Equals(eventName, "USE", StringComparison.OrdinalIgnoreCase);
				bool isClick = IsSwitchClickEvent(eventName);
				// ic dongu (ON/OFF/OPEN/CLOSE) ve guc dongusu (TV) disari
				if (!isUse && !isClick) return;

				// ------------------------------------------------------------------
				// MUTLAK DURUM, TOGGLE DEGIL (canli hata: "isik clientte yaniyor")
				// ------------------------------------------------------------------
				// CANLI KANIT (host logu 13:20): `switch_wc` 10 saniyede 11 kez
				// `USE` gonderdi. `Use` FSM'i her `USE`da `BoolFlip` yaptigi icin
				// karsi taraf 11 kez toggle ediyor; TEK sayida sonucta SONUK
				// kaliyor. Kullanici "isigi actim, clientte gorunmuyor" diyor.
				//
				// Telde `USE:1` (acik) / `USE:0` (kapali) gonderiyoruz. Alici
				// taraf hedef durumda zanaysa oynatmaz -> tekrar sayisi onemsiz.
				// ------------------------------------------------------------------
				// MUTLAK DURUM, GONDERIM ANINDA DEGIL FLUSH ANINDA COZULUR
				// ------------------------------------------------------------------
				// CANLI KANIT (host logu 13:47) -- gonderim aninda okuyunca TERS
				// ve SPAM oldu:
				//   13:47:56.297 [ACT] CTRL USE:1 switch_wc
				//   13:47:56.391 [ACT] CTRL USE:0 switch_wc
				//   13:47:56.489 [ACT] CTRL USE:1 switch_wc
				//   13:47:56.580 [ACT] CTRL USE:0 switch_wc
				// 0.3 saniyede 4 kez yon degisti, yani `ActivateGameObject(Lights)`
				// HENUZ CALISMEMISTI: okudugumuz deger TOGGLE ONCESI idi (ayni
				// hata kapida da goruldu).
				//
				// Dumper kaniti (LightSwitches/switch_wc fsm='Use'):
				//   Wait button: ActivateGameObject(Lights) + SetBoolValue
				//   Switch     : MasterAudioPlaySound + BoolFlip
				// Yani mutlak durum `Lights` nesnesinde ve FSM bir sonraki
				// karede yaziyor.
				//
				// COZUM: kuyruga cozulmemis bir ISARET (`USE?`) konur; durum
				// FLUSH aninda (DOOR_FLUSH_INTERVAL = 0.05 sn sonra) okunur.
				// O arada birden fazla tiklama olsa bile kuyruk TEK kayda
				// birlestigi icin yalniz SON durum gider -- ne spam ne ters.
				QueueSwitchDeferred(c);
				return;
			}
			if (IsFlashlightControl(c)
				&& !string.Equals(eventName, "ON", StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(eventName, "OFF", StringComparison.OrdinalIgnoreCase)) return;

			for (int i = 0; i < doorOutbox.Count; i++) {
				DoorEvent q = doorOutbox[i];
				if (string.Equals(q.Path, c.Path, StringComparison.Ordinal)
					&& string.Equals(q.FsmName, c.FsmName, StringComparison.Ordinal)
					&& string.Equals(q.Event, eventName, StringComparison.Ordinal)) return;
			}
			if (doorOutbox.Count >= DOOR_QUEUE_LIMIT) doorOutbox.RemoveAt(0);

			DoorEvent e = new DoorEvent();
			e.Path = c.Path;
			e.FsmName = c.FsmName;
			e.Event = eventName;
			doorOutbox.Add(e);

			SentControlEvents++;   // kuyruÄŸa alÄ±ndÄ± = gÃ¶nderilecek
			SmpTrace.Act("CTRL", eventName, c.Path + "  " + c.FsmName);
		}

		// ------------------------------------------------------------------
		// Kapi tablosu
		// ------------------------------------------------------------------
		//
		// Ã–NCEKÄ° "TARGET_VARS" TABLOSU SÄ°LÄ°NDÄ°.
		//
		// O tablo elle yazÄ±lmÄ±ÅŸ bir (nesne, fsm, deÄŸiÅŸken) listesiydi ve HÄ°Ã‡BÄ°R
		// YERDEN OKUNMUYORDU. Yani hem Ã¶lÃ¼ koddu hem de varlÄ±ÄŸÄ± yanlÄ±ÅŸ gÃ¼ven
		// veriyordu: "46 nesne takip ediliyor" logu, gerÃ§ekte hiÃ§bir kapÄ±nÄ±n
		// yakalanmadÄ±ÄŸÄ± anlamÄ±na geliyordu. Zaten adlarÄ± da bu build'de yoktu
		// (log kanÄ±tÄ±: FrontDoor listede vardÄ±, sahnede yok).

		/// <summary>Tel Ã¼zerinde adreslenebilen bir kapÄ±: nesne + FSM.</summary>
		public class Door {
			/// <summary>Nesnenin tam sahne yolu. Tel kimligi budur.</summary>
			public string Path;
			/// <summary>Yaprak nesnenin adi (rapor icin).</summary>
			public string Name;
			/// <summary>FSM adi ("Use" / "UseOld"). Tel kimliginin parcasidir.</summary>
			public string FsmName;
		public string LastSentEvent = "";
			/// <summary>
			/// Telde gonderilen SON yon ("OPEN" / "CLOSE").
			///
			/// CANLI KANIT (host logu 13:22): Satsuma `Use` FSM'i kapama
			/// yonunda da `OPEN` uretiyor -- dort tiklamada dort `OPEN`,
			/// hic `CLOSE` yok, ama `[DOOR-VAR]` kacinin 0 -> 289 -> 0
			/// dondugunu gosteriyor. Olay adi yon TASIYAMAZ.
			/// </summary>
			public string LastSentWire = "";

			// ------------------------------------------------------------------
			// KAPI AILESI (ev / arac) -- AYRI UYGULAMA YOLU
			// ------------------------------------------------------------------
			// Dumper kaniti: iki aile de `BoolTest` ile karar verir ama GIRIS
			// OLAYI farklidir.
			//   EV  (DoorFront1/Pivot/Handle): `Wait player` YALNIZCA `USE`
			//       kabul eder -> `Check position` -> `PlayAnimation`.
			//       `USE` gonderilmezse animasyon HIC oynamaz.
			//   ARAÇ (PartsCar/door left(Clone)): `Mouse off` --tiklama-->
			//       `Check position` -> `SetHingeJointLimits` + `AddTorque`;
			//       `OPEN`/`CLOSE` global olaylardir.
			/// <summary>Ev kapi mi (animasyonlu `PlayAnimation`)?</summary>
			public bool IsAnim;
			/// <summary>Siniflandirma yapildi mi (sahne degisiminde sifirlanir)?</summary>
			public bool KindKnown;

			// --- KARE-ADIMLI SURUS (DoorDrive job) ---
			/// <summary>Aktif surus job'u var mi?</summary>
			public bool DriveActive;
			/// <summary>Job'un hedef yonu.</summary>
			public bool DriveWantOpen;
			/// <summary>Job bitis zamani (takilmasin).</summary>
			public float DriveDeadline;
			/// <summary>Job boyunca gonderilen olay sayisi.</summary>
			public int DriveSent;
			/// <summary>Job oykusu (tani).</summary>
			public string DriveStory = "";
			/// <summary>Son gonderilen (durum,olay) cifti (KILIT icin).</summary>
			public string DriveLockState;
			/// <summary>Son gonderilen olay (KILIT icin).</summary>
			public string DriveLockEvent;
			/// <summary>Otomatik karar bekleme sayaci (Check durumlari).</summary>
			public int DriveWait;
			/// <summary>
			/// SON gonderimde okunan durum. Gonderim aninda degismiyorsa FSM
			/// ilerlemiyor demektir (asagidaki donme korumasi).
			/// </summary>
			public string DriveSendState;
			/// <summary>Ayni durumda yapilan ARDISIK gonderim sayisi.</summary>
			public int DriveStall;
			/// <summary>Canli FSM. Basit tutmak icin nesne referansi degil.</summary>
			public PlayMakerFSM Fsm;
			/// <summary>
			/// Hangi Ã¶lÃ§Ã¼tle tanÄ±ndÄ±: TIER_HINGE (menteÅŸeli panel), TIER_FUEL
			/// (yakÄ±t kapaÄŸÄ±) ya da TIER_DOOR (yolunda "door"). YalnÄ±zca teÅŸhis
			/// iÃ§in; tel biÃ§imini etkilemez.
			/// </summary>
			public string Tier;
			/// <summary>Bu FSM'in hangi kapÄ± olaylarini bildirdigi.</summary>
			public bool[] Events;

			// ECHO KORUMA: uzaktan uygulanan kapÄ± olayÄ± sonrasÄ±nda bu FSM'in
			// kendisi kÄ±sa sÃ¼re iÃ§inde ters yÃ¶nde bir olay Ã¼retebiliyor (menteÅŸe
			// limiti/Ã§alÄ± kapanma). Bu yalancÄ± olay geri gÃ¶nderilirse kapÄ± aÃ§-kapa
			// dÃ¶ngÃ¼sÃ¼ baÅŸlar. Uygulanan olayÄ± ve zamanÄ±nÄ± burada tutuyoruz;
			// karÅŸÄ± yÃ¶ndeki olaylarÄ± kÄ±sa sÃ¼re bastÄ±rÄ±yoruz.
			public float AppliedAt = -1f;
			public bool AppliedOpen;

			// ------------------------------------------------------------------
			// OYUNCU NÄ°YETÄ° (canlÄ± Ã¶lÃ§Ã¼mle eklendi)
			// ------------------------------------------------------------------
			// BELÄ°RTÄ°: "kapÄ±yÄ± aÃ§maya Ã§alÄ±ÅŸÄ±yorum kapÄ± aÃ§Ä±lÄ±rken tekrar
			// kapanÄ±yor, evden dÄ±ÅŸarÄ± Ã§Ä±kamÄ±yorum."
			//
			// KANIT (client logu, DOOR-SRC + DOOR-RX izleriyle):
			//   12:19:30.893  SRC OPEN   DoorFront  durum=Check position
			//   12:19:32.407  SRC CLOSE  DoorFront  durum=Test cops
			//   â†‘ 1.5 sn arayla, aralarÄ±nda DOOR-RX YOK
			//
			// YANÄ°: CLOSE'u KARÅI TARAF deÄŸil, OYUNUN KENDÄ° FSM'i
			// Ã¼retiyor. Modun geri besleme dÃ¶ngÃ¼sÃ¼ DEÄÄ°L.
			//
			// Kural (MP'de standart): YEREL OYUNCUNUN NÄ°YETÄ° KAZANIR.
			// Oyun bir kapÄ±yÄ± kÄ±sa sÃ¼re iÃ§inde kendi kapatÄ±yorsa ve
			// karÅŸÄ± taraf bir ÅŸey istemiyorsa, niyeti yeniden uygularÄ±z.
			//
			// Yan etki BILINEN: oyun bir kapÄ±yÄ± bilerek kapatÄ±yorsa
			// (rÃ¼zgÃ¢r, zamanlayÄ±cÄ±) biz onu aÃ§Ä±k tutarÄ±z. SÃ¼re 2 sn ile
			// sÄ±nÄ±rlÄ±; sonrasÄ±nda oyunun kararÄ± geÃ§erli sayÄ±lÄ±r.
			public float IntentOpenAt = -1f;
			public bool IntentOpen;
			/// <summary>NiÃ§at son kez yeniden uygulandÄ± (spam korumasÄ±).</summary>
			public float IntentReassertAt = -1f;
			/// <summary>`USE` yedeÄŸi denendi mi (kapÄ± baÅŸÄ±na bir kez)?</summary>
			public bool UseTried;
			/// <summary>
			/// KapÄ±ya bir olay GÃ–NDERDÄ°ÄÄ°MÄ°Z an (yerel veya uzak).
			/// GÃ¶nderimden sonraki karelerde bu FSM'in kendi geÃ§iÅŸ
			/// olaylarÄ± bastÄ±rÄ±lÄ±r.
			/// </summary>
			public float SendDampedAt = -1f;
		/// <summary>
		/// Ogrenilmis bitis durumlari: yerel acma/kapamada olcumle gorulen
		/// durum adlari. Uzaktan olayda bekleyen FSM'ye olay islemez
		/// (kanit: uzaktan-USE 0.0 derece, son-durum=Wait player); hedef
		/// biliniyorsa SwitchState ile surulur.
		/// </summary>
		public string OpenState;
		public string ClosedState;
	}

		/// <summary>Bekleyen kapÄ± olayi (gÃ¶nderilene kadar).</summary>

		/// <summary>
		/// Bu nesne (ya da bir alt parcasi) bizim kapi tablomuzda mi?
		///
		/// ÃœÃ‡ AYRI SÄ°STEM BÄ°R NESNEYE DOKUNABÄ°LÄ°YOR; TEK SAHÄ°P OLMALI:
		///
		///   1) WorldObjectSync â€” kapÄ±nÄ±n `Use` FSM'ine OPEN/CLOSE oynatÄ±r.
		///   2) GarageEntitySync â€” "Rigidbody olan HER ÅEY" izler, 20 Hz'de
		///      dÃ¼nya konumu yazar.
		///   3) DeÄŸiÅŸken tablosu â€” `Open`/`Rot` bool/float'larÄ±nÄ± yollar.
		///
		/// ÃœÃ§Ã¼ birden aynÄ± transform'u yazÄ±nca kapÄ± yerinden kopuyor
		/// (canlÄ± kanÄ±t: "kapÄ± yerinden kopuyor", "hayosiko aracÄ±n kapÄ±sÄ±
		/// ileri doÄŸru giderken geriden geliyor").
		///
		/// Ã‡Ã–ZÃœM: bir nesnenin TEK senkron sahibi olmalÄ±. KapÄ±nÄ±n sahibi
		/// OLAY yoludur, Ã§Ã¼nkÃ¼ olay oyunun kendi FSM'ini Ã§alÄ±ÅŸtÄ±rÄ±r;
		/// kilit kontrolÃ¼, menteÅŸe sÄ±nÄ±rlarÄ± ve ses oradan gelir.
		/// </summary>
		/// KapÄ± benzeri bir nesne mi? (HingeJoint veya kapÄ± yolu eÅŸleÅŸmesi)
		///
		/// MenteÅŸeli nesneler (kapÄ±lar, kapaklar) FSM animasyonuyla kontrol edilir.
		/// GarageEntitySync'in fiziksel konum senkronizasyonu ile DEÄÄ°L.
		/// Bu yÃ¼zden GarageEntitySync'in takip listesinden KESINLIKLE Ã§Ä±karÄ±lmalÄ±dÄ±r.
		/// </summary>
			/// <summary>
		/// v15: bu bir SAF PREDICATE; artik log yazmaz.
		///
		/// Canli log kaniti (21:03 oturumu, 3529 satir): eski haliyle
		/// [HINGE-JOINT] 1139 + [IS-TRACKED-DOOR] 1139 = logun %64'u
		/// sadece joint aramasiydi. Bunun 1500 satiri (%43) kapI DEGIL:
		/// BoltPM (125), ragdoll iskeleti (head/shoulders/arm/leg/thigh,
		/// ~450), RagDoll (48), Hose (20).
		///
		/// Sebep: IsTrackedDoor kesif sirasinda HER nesne icin, ayrica
		/// CarrySync.IsCarryableUse her tasima denemesinde cagriliyor;
		/// ayni ragdoll jointi defalarca yeniden yaziliyordu.
		///
		/// Bulgu sayacla korunur, ayrinti yalniz Telemetry.Verbose ile.
		/// </summary>
		static bool IsHingedDoor(GameObject go) {
		if (go == null) return false;
		// HingeJoint: araç kapılarının standart menteşe bileşeni
		var joints = go.GetComponentsInChildren<HingeJoint>(true);
		if (joints != null && joints.Length > 0) {
			hingeHits++;
			if (Telemetry.Verbose) SmpLog.Log("[HINGE-JOINT] HingeJoint (children): " + go.name);
			return true;
		}
		var parentJoints = go.GetComponentsInParent<HingeJoint>(true);
		if (parentJoints != null && parentJoints.Length > 0) {
			hingeHits++;
			if (Telemetry.Verbose) SmpLog.Log("[HINGE-JOINT] HingeJoint (parent): " + go.name);
			return true;
		}
		// ConfigurableJoint: bazı araç kapaklarında (bagaj, kaput) kullanılır
		var configJoints = go.GetComponentsInChildren<ConfigurableJoint>(true);
		if (configJoints != null && configJoints.Length > 0) {
			hingeHits++;
			if (Telemetry.Verbose) SmpLog.Log("[HINGE-JOINT] ConfigurableJoint (children): " + go.name);
			return true;
		}
		var parentConfigJoints = go.GetComponentsInParent<ConfigurableJoint>(true);
		if (parentConfigJoints != null && parentConfigJoints.Length > 0) {
			hingeHits++;
			if (Telemetry.Verbose) SmpLog.Log("[HINGE-JOINT] ConfigurableJoint (parent): " + go.name);
			return true;
		}
		// CharacterJoint BILEREK DISLANDI:
		// CharacterJoint yalnızca RAGDOLL iskeletinde bulunur (kol/bacak/omuz).
		// Önceden bu kontrol vardı ve NPC ragdoll eklemlerini "kapı" sanıyordu
		// → GarageEntitySync'den çıkarıyordu → parçalar yanlış senkronlanıyordu.
		// Araç kapılarında CharacterJoint kullanılmaz; HingeJoint veya ConfigurableJoint kullanılır.
		//
		// İsim/yol tabanlı fallback da KALDIRIDI:
		// "door" içeren her nesneyi kapı saymak çok geniş bir eşleşme.
		// FSM tabanlı kapı listesi (doorList) zaten doğru kapıları içeriyor;
		// bu fallback false pozitif üretiyordu (Database/…/door left gibi parça yolları).
		return false;
	}

		/// <summary>Gorulen durum listesini okunur tek satira cevirir.</summary>
		static string SeenList(List<string> seen) {
			if (seen == null || seen.Count == 0) return "(yok)";
			string r = "";
			for (int i = 0; i < seen.Count; i++) {
				if (i > 0) r += " > ";
				r += seen[i];
			}
			return r;
		}

		/// </summary>
		public static bool IsTrackedDoor(GameObject go) {
			// HingeJoint/ConfigurableJoint varsa KESINLIKLE kapÄ±dÄ±r - GarageEntitySync'den Ã§Ä±kar
			bool hinged = IsHingedDoor(go);
			if (hinged) {
				skippedByJoint++;
				if (Telemetry.Verbose)
					SmpLog.Log("[IS-TRACKED-DOOR] Joint tespit edildi, atlanıyor: "
						+ (go != null ? go.name : "null"));
				return true;
			}

			if (go == null) return false;
			string path = null;
			try { path = SceneDump.PathOf(go.transform); } catch { return false; }
			if (string.IsNullOrEmpty(path)) return false;
			for (int i = 0; i < doorList.Count; i++) {
				Door d = doorList[i];
				if (d == null || string.IsNullOrEmpty(d.Path)) continue;
				// birebir
				if (string.Equals(path, d.Path, StringComparison.Ordinal)) return true;
				// izlenen yolun ALTINDAKÄ° her parÃ§a (pivot, Handle, Coll)
				if (path.Length > d.Path.Length
					&& path.StartsWith(d.Path, StringComparison.Ordinal)
					&& path[d.Path.Length] == '/') return true;
				// ------------------------------------------------------------------
				// YUKARIDA OLAN HER ÅEY â€” ASIL DÃœZELTME
				//
				// CANLI BELÄ°RTÄ°: "kapÄ± koluna tÄ±klÄ±yorum aÃ§Ä±lÄ±p gibi olup
				// aÃ§Ä±lmÄ±yor; hÄ±zlÄ± koÅŸunca kapÄ±nÄ±n Ä°Ã‡Ä°NDEN geÃ§ebiliyorum,
				// sonra tekrar geÃ§emiyorum."
				//
				// Bu imza tam olarak "gÃ¶rsel aÃ§Ä±lÄ±yor, collider aÃ§Ä±lmÄ±yor"
				// demektir: FSM `Pivot`'Ä± dÃ¶ndÃ¼rÃ¼yor, collider'Ä± ise baÅŸka
				// bir sistem (GarageEntitySync, 20 Hz) konumla zorluyor.
				//
				// SEBEP: izlenen yol EN DERÄ°N nesneye ait â€”
				//   YARD/.../DoorFront/Pivot/Handle   (FSM'nin nesnesi)
				//                                  ^^^^^^  ^^^^^^
				//                                   Pivot  Handle
				// eski kontrol yalnÄ±z "birebir" ve "altÄ±ndakiler" diyordu;
				// YUKARIDA kalan Pivot/DoorFront eÅŸleÅŸmiyordu. Rigidbody
				// `Pivot` Ã¼zerindeyse kapÄ± akÄ±ÅŸtan Ã§Ä±karÄ±lmÄ±yordu.
				//
				// Ã–LÃ‡ÃœM: log "58 openable panel FSM" diyordu ama yalnÄ±zca
				// "25 door rigidbody/bodies skipped" yazÄ±yordu â€” yani
				// kapÄ±larÄ±n bir kÄ±smÄ± hÃ¢lÃ¢ GarageEntitySync'in 20 Hz
				// konum akÄ±ÅŸÄ±ndaydÄ±.
				// ------------------------------------------------------------------
				if (d.Path.Length > path.Length
					&& d.Path.StartsWith(path, StringComparison.Ordinal)
					&& d.Path[path.Length] == '/') return true;
				// KARDEÅ (SIBLING) DURUMU â€” Collider/Rigidbody Handle'Ä±n kardeÅŸi
				// Ã–rn: `.../DoorFront/Pivot/Handle` (FSM) vs `.../DoorFront/Pivot/Collider` (Rigidbody)
				// Ortak ata `DoorFront` -> "Door" iÃ§eriyor -> AYNI KAPI.
				if (SameDoorRoot(path, d.Path)) return true;
			}
			return false;
		}

		/// <summary>
		/// Ä°ki yol aynÄ± kapÄ± kÃ¶kÃ¼ne mi ait?
		/// KapÄ± kÃ¶kÃ¼: yol bileÅŸenlerinden birinde "door" (bÃ¼yÃ¼k/kÃ¼Ã§Ã¼k harf gÃ¶zetmez) geÃ§en en yakÄ±n ortak ata.
		/// Ã–rn: `.../DoorFront/Pivot/Handle` ve `.../DoorFront/Pivot/Collider`
		//       -> ortak prefix `.../DoorFront/Pivot`, parent `DoorFront` -> "Door" iÃ§eriyor -> TRUE
		/// </summary>
		static bool SameDoorRoot(string pathA, string pathB) {
			if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB)) return false;
			int i = 0, lenA = pathA.Length, lenB = pathB.Length;
			while (i < lenA && i < lenB && pathA[i] == pathB[i]) i++;
			if (i == 0) return false;
			// Ortak prefix'in son '/' anÄ±nÄ± bul
			int lastSlash = pathA.LastIndexOf('/', i - 1);
			if (lastSlash < 0) lastSlash = i;
			// Ortak prefix'in son bileÅŸenini al
			int compStart = pathA.LastIndexOf('/', lastSlash - 1);
			if (compStart < 0) compStart = 0;
			else compStart++;
			int compLen = lastSlash - compStart;
			if (compLen <= 0) return false;
			string lastComp = pathA.Substring(compStart, compLen);
			// Ortak prefix'in son bileÅŸeni "Door" iÃ§eriyor mu?
			if (lastComp.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			// Parent bileÅŸenine bak (Ã¶rn: Pivot -> DoorFront)
			if (compStart > 0) {
				int parentSlash = pathA.LastIndexOf('/', compStart - 2);
				if (parentSlash >= 0) {
					int pCompStart = pathA.LastIndexOf('/', parentSlash - 1);
					if (pCompStart < 0) pCompStart = 0;
					else pCompStart++;
					int pCompLen = parentSlash - pCompStart;
					if (pCompLen > 0) {
						string parentComp = pathA.Substring(pCompStart, pCompLen);
						if (parentComp.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0) return true;
					}
				}
			}
			return false;
		}

/// <summary>
/// KAPI AÃ‡ILIP AÃ‡ILMIYOR? (menteÅŸe aÃ§Ä±sÄ± Ã¶lÃ§Ã¼mÃ¼)
///
/// GEREK: FSM "OPEN aldÄ±m" diyor ama kapÄ± GÃ–RSEL olarak aÃ§Ä±lmÄ±yor.
/// Olay katmanÄ± ile gÃ¶rsel katman arasÄ±ndaki farkÄ± hiÃ§bir kayÄ±t
/// Ã¶lÃ§mÃ¼yordu. CanlÄ± belirti: "kapÄ± aÃ§Ä±lÄ±rken tekrar kapanÄ±yor".
///
/// Ã–LÃ‡ÃœM: menteÅŸenin yerel Euler aÃ§Ä±sÄ±. KapÄ± aÃ§Ä±lÄ±nca bu 0'dan
/// uzaklaÅŸmalÄ±. 3 dereceden az uzaklaÅŸÄ±rsa FSM olayÄ± almÄ±ÅŸ ama
/// ANÄ°MASYON Ã§alÄ±ÅŸmÄ±yor demektir (oyuncu kancaya ulaÅŸmÄ±yor,
/// esya kilitli, FSM koÅŸulu saÄŸlanmÄ±yor).
///
/// KapÄ± "dokunulduktan" sonra 3 saniye boyunca Ã¶rneklenir.
/// </summary>
static System.Collections.IEnumerator ProbeDoorAngle(Door d, string reason) {
	// NOT: catch'li try iÃ§inde yield OLAMAZ (C# kuralÄ±). Korumalar
	// if + yield break ile yapÄ±lÄ±yor.
	//
	// Ã–LÃ‡ÃœM HATASI (dÃ¼zeltildi): eski sÃ¼rÃ¼m `d.Fsm.transform`Ã¼n
	// aÃ§Ä±sÄ±nÄ± Ã¶lÃ§Ã¼yordu. Oysa kapÄ± yolu
	//   .../DoorFront/Pivot/Handle   fsm=Use
	// ve HANDLE sabit duran nesnedir; DÃ–NEN ÅŸey PIVOT'tur. Handle'in
	// aÃ§Ä±sÄ± her zaman 0 dÃ¶ndÃ¼ÄŸÃ¼ iÃ§in "MENTESE KIMILADI" hÃ¼kmÃ¼nÃ¼
	// YANLIÅLIKLA her seferinde Ã¼retirdi.
	//
	// Ã‡Ã–ZÃœM: kapÄ±nÄ±n TÃœM alt dÃ¶nÃ¼ÅŸÃ¼mleri Ã¶rneklenir, en Ã§ok deÄŸiÅŸen
	// kazanÄ±r. Hangi nesnenin dÃ¶ndÃ¼ÄŸÃ¼ de yazÄ±lÄ±r.
	if (d == null || d.Fsm == null || d.Fsm.gameObject == null) yield break;

	// ------------------------------------------------------------------
	// HANGÄ° NESNE DÃ–NÃœYOR? (Ã¶lÃ§Ã¼m hatasÄ± #2 â€” dÃ¼zeltildi)
	// ------------------------------------------------------------------
	// ESKÄ°: yalnÄ±z GetComponentsInChildren. KapÄ± yolu
	//     .../DoorFront/Pivot/Handle   fsm=Use
	// ve Handle'in ALTINDA hiÃ§ nesne yok. CanlÄ± Ã¶lÃ§Ã¼m bunu doÄŸruladÄ±:
	//     ornekleme=15 nesne=1  en-cok-degisen='' 0.0 derece
	// Yani menteÅŸe dÃ¶nen Pivot (Handle'in ÃœSTÃœNDE) hiÃ§ Ã¶lÃ§Ã¼me giriyordu
	// ve hÃ¼kÃ¼m HER ZAMAN "kÄ±pÄ±rdamadÄ±" Ã§Ä±kÄ±yordu.
	//
	// Ã‡Ã–ZÃœM: 4 seviye YUKARI + kendisi + tÃ¼m Ã§ocuklarÄ±. KapÄ± 6 nesne
	// ise artÄ±k ~10 nesne Ã¶lÃ§Ã¼lÃ¼yor ve gerÃ§ekten dÃ¶nen bulunuyor.
	List<Transform> list = new List<Transform>(16);
	Transform up = d.Fsm.transform;
	for (int k = 0; k < 4 && up != null; k++) {
		list.Add(up);
		up = up.parent;
	}
	try {
		Transform[] kids = d.Fsm.GetComponentsInChildren<Transform>(true);
		if (kids != null) {
			for (int k = 0; k < kids.Length; k++) {
				if (kids[k] == null) continue;
				if (!list.Contains(kids[k])) list.Add(kids[k]);
			}
		}
	}
	catch { }
	Transform[] ts = list.ToArray();
	if (ts == null || ts.Length == 0) yield break;

	Vector3[] first = new Vector3[ts.Length];
	float[] firstY = new float[ts.Length];
	for (int i = 0; i < ts.Length; i++) {
		first[i] = ts[i].localEulerAngles;
		firstY[i] = first[i].y;
	}

	// v15: probu baslayan anki durum. Kapanis animasyonu GECICI bir
	// durumdur ("Close door"); 3 saniyelik olcum penceresi kapaninca
	// FSM coktan "Wait player" durusuna donmus olur. Son durumu tek
	// kez okuyorduk, bu yuzden kapinin kapanis durumu HIC OGRENILEMIYORDU
	// ve uzaktaki kapi kapatma komutu bos bir duruma gidiyordu.
	string restState = null;
	try { if (d.Fsm.Fsm != null) restState = d.Fsm.Fsm.ActiveStateName; } catch { }
	// probu basindan farkli gorulen EN SON durum -> animasyon durumu
	string motionState = null;
	// v15: PENCERE ICINDE GORULEN BUTUN farkli durumlar, sirali.
	//
	// Canli kanit (21:03, L3188): DoorFront kapanisinda motionState yine
	// "Wait player" cikti. Sebep: prob 0.2 sn'de bir örnek aliyor ve FSM
	// "Close door" durumundan "Wait player"a iki örnek arasinda geciyor;
	// sadece SON farkli durumu tutmak gecici durumu göremez.
	//
	// Dumper kaniti ayni FSM icin kapanis animasyonunun "Close door"
	// durumunda oldugunu gosteriyor:
	//   YARD/.../DoorFront/Pivot/Handle [Use]
	//     durumlar: Wait player, Check position, Open door, Close door,
	//               Test cops, Get pivot, Check key, Cop 1 anim
	List<string> seenStates = null;

	float t0 = Time.time;
	int samples = 0;
	string winner = "";
	float best = 0f;
	// HER ORILEM KAYDEDILIR: kopma ancak zaman serisiyle gorunur.
	float[] traceVal = new float[20];
	float[] traceAt = new float[20];
	float peakBefore = 0f;   // ilk yarida gorulen en fazla kayma
	float peakAfter = 0f;    // sonra gorulen en fazla kayma
	Vector3 bestFrom = Vector3.zero, bestTo = Vector3.zero;
	// Sadece kapÄ±nÄ±n altÄ±ndaki nesnelere bak; oyuncu kamerasÄ± vs gerekmez.
	while (Time.time - t0 < 3f && samples < 20) {
		yield return new WaitForSeconds(0.2f);
		samples++;
		float nowOff = 0f;
		string nowName = "";
		for (int i = 0; i < ts.Length; i++) {
			if (ts[i] == null) continue;
			Vector3 cur = ts[i].localEulerAngles;
			float dAng = Mathf.Abs(Mathf.DeltaAngle(cur.y, first[i].y))
				+ Mathf.Abs(Mathf.DeltaAngle(cur.x, first[i].x))
				+ Mathf.Abs(Mathf.DeltaAngle(cur.z, first[i].z));
			if (dAng > best) {
				best = dAng;
				winner = ts[i].name;
				bestFrom = first[i];
				bestTo = cur;
			}
			if (dAng > nowOff) { nowOff = dAng; nowName = ts[i].name; }
		}
		// Zaman serisi: bu olmadan kopma imzasi gorunmez.
		if (samples - 1 < traceVal.Length) {
			traceVal[samples - 1] = nowOff;
			traceAt[samples - 1] = Time.time - t0;
		}
		// Durumu da ornekle: baslangictan farkli olan son durum animasyon
		// durumudur (or. "Close door"). Dosya sonundaki tek seferlik
		// okuma bunu yakalayamaz.
		try {
			if (d.Fsm.Fsm != null) {
				string cur = d.Fsm.Fsm.ActiveStateName;
				if (!string.IsNullOrEmpty(cur) && !string.Equals(cur, restState, StringComparison.Ordinal)) {
					motionState = cur;
					if (seenStates == null) seenStates = new List<string>(8);
					if (!seenStates.Contains(cur)) seenStates.Add(cur);
				}
			}
		} catch { }
		if (samples <= 7) { if (nowOff > peakBefore) peakBefore = nowOff; }
		else if (nowOff > peakAfter) peakAfter = nowOff;
		// ART ARDA ACIK KALDI MI? (kopma tespiti)
		//
		// KULLANICI BELÄ°RTÄ°SÄ°: "kapÄ± aÃ§Ä±lÄ±r gibi olup aÃ§Ä±lmÄ±yor. tam
		// aÃ§tÄ±ÄŸÄ±m sÄ±rada kapÄ± yerinden kopuyor sonra tekrar eski
		// haline geliyor kapalÄ± ÅŸekilde."
		//
		// Ã–NCEKÄ° Ã–LÃ‡ÃœM BUNU GÃ–REMEZDÄ°: dÃ¶ngÃ¼ `best > 3f` olunca
		// `break` ile bitiyordu, yani kapÄ± 0.2 saniyede "aÃ§Ä±ldÄ±" sayÄ±lÄ±p
		// Ã¶lÃ§Ã¼m DURUYORDU. Kopma sonraki saniyelerde olduÄŸu iÃ§in
		// hiÃ§ Ã¶rneklenmiyordu. CanlÄ± kanÄ±t: `ornekleme=1`.
		//
		// DÃœZELTME: erken Ã§Ä±kÄ±ÅŸ YOK. 3 saniye boyunca 5 Hz Ã¶rneklenir,
		// her Ã¶rnekte kayma miktarÄ± kaydedilir. BÃ¶ylece "aÃ§Ä±ldÄ± â†’ 0'a
		// dÃ¶ndÃ¼" ya da "0 â†’ 60 â†’ 0" gibi kopma imzalarÄ± logda gÃ¶rÃ¼nÃ¼r.
		//
		// YalnÄ±z kesin bir aÃ§Ä±lma (kayma 25 derece) hÃ¢lÃ¢ erken Ã§Ä±karÄ±r:
		// tam aÃ§Ä±lan kapÄ±da 3 saniye beklemenin anlamÄ± yok.
		if (best > 25f) break;
		if (samples >= 15) break;
	}

	// Ã–RNEK ZAMAN Ã‡Ä°ZELGESÄ° â€” kopyanÄ±n imzasÄ± burada.
	string trace = "";
	for (int k = 0; k < traceAt.Length && k < traceVal.Length; k++) {
		if (k > 0) trace += ",";
		trace += traceAt[k].ToString("F1") + "s:" + traceVal[k].ToString("F0");
	}
	string kopma = "";
	if (peakAfter > peakBefore + 5f) {
		kopma = "  !! KOPMA: once " + peakBefore.ToString("F0")
			+ " derece acildi, sonra " + peakAfter.ToString("F0")
			+ " dereceye GERI DONDU";
	}

	string st = null;
	try { if (d.Fsm.Fsm != null) st = d.Fsm.Fsm.ActiveStateName; } catch { }
	// Ogrenme hedefi (v15 duzeltmesi).
	//
	// Kural: FSM'in DURDURDUGU durum (`st`) dogru hedeftir -- animasyonun
	// icinde kalinan durum odur. Canli kanit (21:25, gorulen= sutunu):
	//
	//   bootlid(Clone)   gorulen=Mouse off > Mouse over > Open hood
	//                    -> dogru hedef "Open hood" (SADECE son durum)
	//   GarageDoorRight  gorulen=Open                     -> "Open"
	//   DoorWC           gorulen=Open door                -> "Open door"
	//
	// Yani listedeki ILK farkli durum HATALI: "Mouse off" bir vurgu
	// (hover) durumu. Onceki surumun ilk-kadayi kurali bu yuzden
	// regresyondu ve "Mouse off" ogreniyordu.
	//
	// Oncelik sirasi:
	//   1) st            -- durus durumundan farkliysa
	//   2) motionState   -- st durus durumuna donduyse
	//   3) hicbiri yoksa OGRENME YAPILMAZ. Durus durumu ("Wait player",
	//      "Mouse over 2") asla hedef olmaz: kapidan uzakta durmak
	//      animasyonu tetiklemez, bos bir duruma gider.
	string learnSt = null;
	if (!string.IsNullOrEmpty(st)
		&& !string.Equals(st, restState, StringComparison.Ordinal)) {
		learnSt = st;
	}
	else if (!string.IsNullOrEmpty(motionState)
		&& !string.Equals(motionState, restState, StringComparison.Ordinal)) {
		learnSt = motionState;
	}

	// ------------------------------------------------------------------
	// MUTLAK AÃ‡I â€” Ã–LÃ‡ÃœM HATASI #3 ve #5 (dÃ¼zeltildi)
	// ------------------------------------------------------------------
	// #3: sadece DEÄÄ°ÅÄ°M Ã¶lÃ§Ã¼lÃ¼yordu. 0.0 deÄŸiÅŸim "geri dÃ¶nmedi" ile
	//     "hiÃ§ aÃ§Ä±lmadÄ±" arasÄ±nda ayrÄ±m yapamÄ±yordu.
	//
	// #5 (ASIL HATA, kullanÄ±cÄ± "kapÄ±lar aÃ§Ä±k, hiÃ§ kapanmadÄ±" deyip
	//     yakaladÄ±): "ÅŸu an aÃ§Ä±k mÄ±" kontrolÃ¼ YALNIZCA Y EKSENÄ°NÄ°
	//     bakÄ±yordu:
	//         if (|DeltaAngle(y, 0)| > 3) openNow = true;
	//     Oysa menteÅŸe birÃ§ok kapÄ±da X veya Z ekseninde dÃ¶nÃ¼yor.
	//     Ã–lÃ§Ã¼len deÄŸiÅŸimin tamamÄ± 3 eksenin toplamÄ±ydÄ± (satÄ±r 691'de
	//     Ã¶yle hesaplanÄ±yor), ama AÃ‡IKLIK kontrolÃ¼ tek eksene bakÄ±yordu.
	//     SonuÃ§: kapÄ±lar aÃ§Ä±k olduÄŸu halde "simdi-acik-mi=HAYIR" yazdÄ±
	//     ve "kapi KAPANDI" hÃ¼kmÃ¼ Ã§Ä±ktÄ±. KullanÄ±cÄ±nÄ±n gÃ¶zÃ¼ doÄŸruydu,
	//     Ã¶lÃ§Ã¼m yanlÄ±ÅŸtÄ±.
	//
	// DÃœZELTME: aynÄ± 3 eksenli Ã¶lÃ§Ã¼m her iki yerde de kullanÄ±lÄ±yor.
	// ------------------------------------------------------------------
	string angleInfo = "";
	if (winner != "") {
		for (int i = 0; i < ts.Length; i++) {
			if (ts[i] == null) continue;
			if (!string.Equals(ts[i].name, winner, StringComparison.Ordinal)) continue;
			Vector3 b0 = first[i], b1 = ts[i].localEulerAngles;
			angleInfo = " '" + winner + "' mutlak: baslangic=("
				+ b0.x.ToString("F0") + "," + b0.y.ToString("F0") + "," + b0.z.ToString("F0")
				+ ") bitis=("
				+ b1.x.ToString("F0") + "," + b1.y.ToString("F0") + "," + b1.z.ToString("F0")
				+ ") derece";
			break;
		}
	}
	if (angleInfo == "") angleInfo = " (donen nesne yok, mutlak aci alinamadi)";

	// ACIK MI? â€” deÄŸiÅŸim Ã¶lÃ§Ã¼mÃ¼yle AYNI 3 eksen kullanÄ±lÄ±yor.
	float maxOffset = 0f;
	string maxName = "";
	for (int i = 0; i < ts.Length; i++) {
		if (ts[i] == null) continue;
		Vector3 cur = ts[i].localEulerAngles;
		float off = Mathf.Abs(Mathf.DeltaAngle(cur.y, first[i].y))
			+ Mathf.Abs(Mathf.DeltaAngle(cur.x, first[i].x))
			+ Mathf.Abs(Mathf.DeltaAngle(cur.z, first[i].z));
		if (off > maxOffset) { maxOffset = off; maxName = ts[i].name; }
	}
	bool openNow = maxOffset > 3f;
	if (openNow) angleInfo += " | su-an-en-fazla-kayma='" + maxName + "' " + maxOffset.ToString("F1") + " derece";

	string verdict;
	if (best > 3f && openNow) {
		verdict = "MENTESE DONDU ve ACIK KALDI -> GORSEL ACIK" + angleInfo;
	}
	else if (best > 3f) {
		verdict = "MENTESE ONCE DONDU sonra GERI DONDU -> kapi KAPANDI" + angleInfo;
	}
	else {
		verdict = "MENTESE KIMILADI: en cok " + best.ToString("F1")
			+ " derece (" + (winner == "" ? "hicbir alt nesne donmedi" : winner)
			+ ") -> FSM olayi aldi ama ANIMASYON CALISMIYOR" + angleInfo;
	}

	// OGRENME: yerel olayin bitis durumu, uzaktan surus icin hedef olur.
	// Yalniz ilk saglam olcum saklanir; ayni kapi icin tekrar yazilmaz.
	if (!string.IsNullOrEmpty(learnSt) && d != null) {
		bool yerelOpen = reason != null && reason.StartsWith("yerel-OPEN");
		bool yerelClose = reason != null && reason.StartsWith("yerel-CLOSE");
		if (yerelOpen && openNow && string.IsNullOrEmpty(d.OpenState)) {
			d.OpenState = learnSt;
			SmpLog.Print("[DOOR-LEARN] " + d.Path + " open-state='" + learnSt
				+ "' (" + (motionState != null && learnSt == motionState ? "animasyon durumu" : "bitis durumu")
				+ ")");
		}
		else if (yerelClose && !string.IsNullOrEmpty(learnSt) && !string.Equals(learnSt, d.OpenState, StringComparison.Ordinal)
			&& string.IsNullOrEmpty(d.ClosedState)) {
			// NOT: !openNow sarti YANLISTI. Kapanis animasyonu hareket
			// uretir (38 derece), bitis ofseti buyuk cikar -> openNow=true
			// olur ve ogrenme hic calismazdi. Dogru test: bitis durumu
			// acilis durumundan farkliysa kapanmistir.
			d.ClosedState = learnSt;
			SmpLog.Print("[DOOR-LEARN] " + d.Path + " closed-state='" + learnSt
				+ "' (" + (motionState != null && learnSt == motionState ? "animasyon durumu" : "bitis durumu")
				+ ")");
		}
	}

	if (Telemetry.Verbose) SmpLog.Print("[DOOR-ANGLE] " + d.Path + "  " + reason
		+ "  ornekleme=" + samples + " olculen-nesne=" + ts.Length
		+ "  en-cok-degisen='" + (winner == "" ? "-" : winner) + "' "
		+ best.ToString("F1") + " derece"
		+ "  son-durum=" + (st ?? "?")
		+ "  durus=" + (restState ?? "?")
		+ "  animasyon-durumu=" + (motionState ?? "(yok)")
		+ "  gorulen=" + SeenList(seenStates));
	if (Telemetry.Verbose) SmpLog.Print("[DOOR-ANGLE] -> " + verdict
		+ " | simdi-acik-mi=" + (openNow ? "EVET" : "HAYIR"));
		// Zaman serisi ve [DOOR-LEARN] asagida; ikisi de kendi
		// kosuluna sahip.
	// v15: zaman serisi SADECE temiz bir basari disinda yazilir.
	// Satirin kendi yorumu bunun icin: "kopma imzasi" yani acik -> geri
	// donme gibi BIR HATA imzasi. Canli logda (21:25) her kapi
	// etkilesiminde 15 sayilik seri basiliyordu; 5 tanesi tamamen
	// basarili acilislerdi.
	if (kopma.Length > 0 || best <= 3f)
		SmpLog.Print("[DOOR-ANGLE] zaman-serisi: " + (trace.Length > 0 ? trace : "(yok)")
			+ kopma);
}

/// <summary>
/// `OPEN` iÅŸe yaramadÄ±ysa `USE` dene (oyunun kendi tuÅŸ yolu).
///
/// GerekÃ§e: canlÄ± Ã¶lÃ§Ã¼mde ev kapÄ±sÄ± `OPEN` aldÄ±ktan sonra da bekleme
/// durumunda kalÄ±yor, yani o olay onu aÃ§mÄ±yor. `USE` bir TOGGLE'dÄ±r
/// ve koÅŸulu kendi iÃ§inde dener.
///
/// ZararsÄ±z olmasÄ± iÃ§in: yalnÄ±z bekleme durumundayken, en fazla bir kez
/// (kapÄ± baÅŸÄ±na) deneniyor ve `USE` FSM'i tanÄ±mÄ±yorsa hiÃ§ gÃ¶nderilmiyor.
/// </summary>
static void InvokeUseFallback(string path, string fsmName) {
	try {
		if (SteamMPBehaviour.Instance == null) return;
		SteamMPBehaviour.Instance.StartCoroutine(UseFallback(path, fsmName, 0f));
	}
	catch { }
}

static System.Collections.IEnumerator UseFallback(string path, string fsmName, float age) {
	if (age > 1.5f) yield break;
	Door d = null;
	for (int q = 0; q < doorList.Count; q++) {
		if (doorList[q] != null
			&& string.Equals(doorList[q].Path, path, StringComparison.Ordinal)) {
			d = doorList[q];
			break;
		}
	}
	if (d == null || d.Fsm == null || d.UseTried) yield break;
	if (d.Fsm.Fsm == null) yield break;

	// FSM'in `USE` olayini taniyor mu? Tanimiyorsa hicbir sey gonderme.
	//
	// CANLI HATA (13:14 client): `USE` yedegi HIC TETIKLENMEDI (log'da
	// `[DOOR-USE]` yok). Sebep su olabilir:
	//   (a) `FsmEvents` yalniz FSM'e KAYITLI olaylari dondurur; `USE`
	//       cogu kapida GLOBAL bir olaydir ve listede gorunmez;
	//   (b) `FsmNames.HasEvent` baska bir liste okuyor.
	// Hangisi oldugunu OLCUYORUZ: yedegi denemeden once butun olay
	// adlarini yaziyoruz. Kapida `USE` yoksa bile `SendEvent("USE")`
	// calisir (PlayMaker global olaylari kabul eder).
	bool hasUse = FsmNames.HasEvent(d.Fsm, "USE");
	if (!hasUse) {
		try {
			FsmEvent[] evs = d.Fsm.FsmEvents;
			System.Text.StringBuilder sb = new System.Text.StringBuilder(300);
			if (evs != null) {
				for (int i = 0; i < evs.Length && sb.Length < 280; i++) {
					if (evs[i] == null) continue;
					if (sb.Length > 0) sb.Append(",");
					sb.Append(evs[i].Name);
				}
			}
			SmpLog.Print("[DOOR-USE] " + path
				+ "  FSM `USE` TANIYOR: hayir; olaylar: "
				+ (sb.Length > 0 ? sb.ToString() : "(bos)") + " -> yine de denenecek");
		}
		catch { }
	}

	yield return new WaitForSeconds(0.3f);

	d.UseTried = true;
	// YARIS KORUMASI: 0.3 sn beklerken kapi kapanmis (yerel tiklama,
	// baska paket, oyun mantigi) olabilir. Bayat USE toggle olur ve
	// kapiyi KENDI KENDINE acar ("ustune gelince aciliyor" sikayeti).
	// Hala aciksa kapatmak icin atesle, degilse atla.
	string curNow = null;
	try { if (d.Fsm != null && d.Fsm.Fsm != null) curNow = d.Fsm.Fsm.ActiveStateName; } catch { }
	bool stillOpen = curNow != null
		&& curNow.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0
		&& curNow.IndexOf("Close", StringComparison.OrdinalIgnoreCase) < 0;
	if (!stillOpen) {
		SmpLog.Print("[DOOR-USE] atlandi (durum degisti: '" + (curNow ?? "?") + "')  " + path);
		yield break;
	}
	try {
		EchoGuard.Scope sc = EchoGuard.Enter();
		try {
			d.Fsm.SendEvent("USE");
			SmpLog.Print("[DOOR-USE] OPEN ise yaramadi, `USE` gonderildi  " + path
				+ "  (FSM taniyor=" + hasUse + ")");
		}
		finally { sc.Dispose(); }
	}
	catch (Exception e) {
		SmpLog.Debug("world", "door", "USE fallback failed on " + path + ": " + e.Message);
	}
}

/// <summary>Bekleyen kapÄ± olayÄ± (gÃ¶nderilene kadar).</summary>
struct DoorEvent {
			public string Path;
			public string FsmName;
			public string Event;
		}

		static readonly List<Door> doorList = new List<Door>(64);
		static readonly Dictionary<string, Door> doorByKey
			= new Dictionary<string, Door>(StringComparer.Ordinal);
		/// <summary>
		/// SÄ±cak yol Ã¶nbelleÄŸi: PlayMakerFSM -> Door. KeÅŸifte bir kez
		/// doldurulur, QueueDoorEvent her Ã§aÄŸrÄ±da tek sÃ¶zlÃ¼k bakÄ±ÅŸÄ± yapar.
		/// SÄ±fÄ±r string tahsisi. DeÄŸer null ise "bu FSM bir kapÄ± deÄŸil"
		/// demektir (olumsuz sonuÃ§ da Ã¶nbelleklenir).
		/// </summary>
		static readonly Dictionary<PlayMakerFSM, Door> doorByFsm
			= new Dictionary<PlayMakerFSM, Door>(64);
		static readonly List<DoorEvent> doorOutbox = new List<DoorEvent>(8);

		/// <summary>
		/// Oyun kapÄ±yÄ± kendi kapattÄ±ysa kÄ±sa sÃ¼re sonra yeniden aÃ§Ä±lacaklar.
		/// "AÃ§Ä±k kal" yan etkisini 1 saniyeyle sÄ±nÄ±rlar: oyun bir kapÄ±yÄ±
		/// bilerek kapatÄ±yorsa ikinci turda artÄ±k mÃ¼dahale etmiyoruz.
		/// </summary>
		static readonly List<Door> PendingIntentReassert = new List<Door>(4);
		static float nextIntentReassert;

		/// <summary>Oyuncunun aÃ§ma niyeti bu sÃ¼re iÃ§inde geÃ§erlidir (s).</summary>
		public const float INTENT_WINDOW = 2f;

		/// <summary>AynÄ± kapÄ± iÃ§in iki yeniden uygulama arasÄ± en az sÃ¼re (s).</summary>
		public const float INTENT_REASSERT_GAP = 0.75f;

		/// <summary>"Oyuncunun niyeti kazandÄ±" sayÄ±sÄ± ('smp status').</summary>
		public static int IntentOverrides = 0;
		static float nextDoorFlush;

		/// <summary>Izlenecek bir nesne.</summary>
		public class Tracked {
			public string Name;
			public string Path;
			public PlayMakerFSM[] Fsms;

			/// <summary>Senkronlanan FsmBool'lar: (fsm, degisken) -> deger.</summary>
			public FsmBool[] BoolVars;
			public FsmFloat[] FloatVars;

			public bool[] BoolLast;
			public float[] FloatLast;
			/// <summary>
			/// Eylem gÃ¼nlÃ¼ÄŸÃ¼ iÃ§in son YAZILAN deÄŸerler. BoolLast'tan ayrÄ±dÄ±r:
			/// oyun bazÄ± bool'larÄ± her tur yeniden yazÄ±yor (araÃ§ kapÄ±sÄ± Open),
			/// "son gÃ¶zlenen" ile kÄ±yaslamak gÃ¼nlÃ¼ÄŸÃ¼ spam'e Ã§eviriyordu (canlÄ±
			/// log kanÄ±tÄ± 23:12).
			/// </summary>
			public bool[] BoolLogged;
			public float[] FloatLogged;
			public bool HasBaseline;

			// v15 -- ANIMASYON TESPITI.
			//
			// Canli log kaniti (21:38 oturumu, 345 satirin 63'u = %18):
			//   [ACT] DOOR changed TRAFFIC/CrashSite/LOD/Candle/light
			//          Intensity=0.41 / 0.43 / 0.50 / 0.46 / 0.49 ...
			// Tek bir titreyen mum. Float karsilastirmasi 0.001 toleransla
			// yapildigi icin alevin her oynemesi "degisim" sayiliyordu.
			// Bu bir DURUM degisimi degil, bir ANIMASYON.
			//
			// ChurnPoll: ardisik kac poll'dur bu nesne float bildiriyor.
			// CHURN_LIMIT'i asarsa nesne "animasyonlu" sayilir, bir kez
			// bildirilir ve CHURN_COOLDOWN boyunca float izlemeye alinmaz.
			public int ChurnPoll;
			public float ChurnQuietUntil;

			public float LastSentAt;
			public bool RemoteOwned;      // karsi taraf son degisikligi yapti
			public float RemoteSetAt;
		}

		static readonly List<Tracked> tracked = new List<Tracked>(128);
		static readonly Dictionary<string, Tracked> byPath
			= new Dictionary<string, Tracked>(StringComparer.Ordinal);
		static readonly List<Tracked> outbox = new List<Tracked>(MAX_PER_PACKET);

		static float nextPoll;
		static bool discovered;

		// v15: menteşe taraması saf predicate olduğu için artık log yazmaz;
		// bulunan sayılar burada toplanır ve keşif sonunda TEK satırda
		// özetlenir. (Canlı log: 1139+1139 satır, %64.)
		static int hingeHits;      // joint bulunan nesne
		static int nameDoorHits;   // isim/yoldan "kapı" sayılan nesne
		static int skippedByJoint; // IsTrackedDoor -> joint nedeniyle elenen

		// ------------------------------------------------------------------
		// Telemetri
		// ------------------------------------------------------------------

		public static int TrackedCount { get { return tracked.Count; } }
		public static int SentUpdates;
		public static int AppliedUpdates;
		public static int Unresolved;

		/// <summary>KeÅŸfedilen kapÄ± sayÄ±sÄ± ('smp status' / 'smp doors').</summary>
		public static int DoorCount { get { return doorList.Count; } }

		/// <summary>
		/// DeÄŸiÅŸken tablosuna ALINMAYAN kapÄ± sayÄ±sÄ± (kapÄ± = yalnÄ±z olay yolu).
		/// KeÅŸif Ã¶zetinde yazÄ±lÄ±r: sÄ±fÄ±rdan bÃ¼yÃ¼kse kapÄ±lar Ã§ift yoldan
		/// senkron edilmiyor demektir.
		/// </summary>
		static int skippedDoorVars;
		/// <summary>GÃ¶nderilen kapÄ± olayÄ±.</summary>
		public static int SentDoorEvents;

		/// <summary>Bayat USE tekrarlari (toggle kirlenmesin diye dusuruldu).</summary>
		public static int DroppedStaleUse;
		/// <summary>Uygulanan kapÄ± olayÄ±.</summary>
		public static int AppliedDoorEvents;
		/// <summary>Ã‡Ã¶zÃ¼lemeyen kapÄ± yolu.</summary>
		public static int UnresolvedDoors;
		/// <summary>Bekleyen kapÄ± olayÄ±.</summary>
		public static int PendingDoorEvents { get { return doorOutbox.Count; } }

		// ------------------------------------------------------------------
		// Kesif
		// ------------------------------------------------------------------

		/// <summary>
		/// Oyundaki kapi/musluk/dus nesnelerini bulur ve senkronlanacak
		/// FSM degiskenlerini toplar.
		///
		/// Ä°ki asamali:
		///   1. Ada gore nesne bul (Resources.FindObjectsOfTypeAll, pasif
		///      olanlar dahil - kapi FSM'i kapaliyken nesne pasif olabilir).
		///   2. Nesne ustundeki HER PlayMakerFSM'i tara ve bool/float
		///      degiskenlerini topla. Hangi degiskenin "durum" oldugu
		///      onceden bilinmiyor; hepsi toplanir ve yalnizca DEGISEN
		///      olanlar gonderilir.
		///
		/// AYRICA kapÄ±lar iÃ§in olay tablosu kurulur (TryRegisterDoor).
		///
		/// Satsuma'ya BAÄIMLI DEÄÄ°LDÄ°R: eski sÃ¼rÃ¼m burada
		/// `if (SatsumaRegistry.Root == null) return;` ile baÅŸlÄ±yordu, yani
		/// araÃ§ kayÄ±tta yokken bÃ¼tÃ¼n kapÄ± senkronu sessizce Ã¶lÃ¼yordu. KapÄ±
		/// senkronunun arabaya hiÃ§bir baÄŸÄ±mlÄ±lÄ±ÄŸÄ± yok.
		/// </summary>
		public static void Discover() {
			Clear();
			skippedDoorVars = 0;

			GameObject[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
			catch { }
			if (all == null) return;

			// Ä°KÄ° GEÃ‡Ä°ÅLÄ° KEÅÄ°F (SIRA Ã–NEMLÄ°).
			//
			// Ã–nceden tek geÃ§iÅŸ vardÄ± ve nesne sÄ±rasÄ±
			// Resources.FindObjectsOfTypeAll'a baÄŸlÄ±ydÄ± â€” yani bir kapÄ±nÄ±n
			// Ã‡OCUÄU (pivot / Handle / Coll) ana nesnesinden Ã–NCE
			// iÅŸlenebiliyordu. O zaman IsTrackedDoor(Ã§ocuk) yanlÄ±ÅŸlÄ±kla
			// false dÃ¶nÃ¼yor, Ã§ocuk deÄŸiÅŸken tablosuna giriyor ve geri
			// besleme dÃ¶ngÃ¼sÃ¼ kuruluyordu.
			//
			// 1. geÃ§iÅŸ: TÃœM kapÄ±lar olay tablosuna (parent dahil).
			// 2. geÃ§iÅŸ: deÄŸiÅŸken tablosu â€” artÄ±k kapÄ± tablosu tam olduÄŸu
			//             iÃ§in IsTrackedDoor her zaman doÄŸru cevap verir.
			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null) continue;
				// CÄ±vatalar dÄ±ÅŸlanÄ±r: BoltPM nesnesinin YOLU da "door" iÃ§erir
				// (Ã¶rn. CARPARTS/PartsCar/door left(Clone)/Bolts/BoltPM) ve
				// Ã¼zerindeki `Tightness` float'u BoltBatch'in konusudur. Ä°ki yol
				// aynÄ± deÄŸiÅŸkeni yazarsa birbirlerini ezer.
				TryRegisterDoor(go);
			}

			// Kontrol nesneleri (kontak, CD player kapaÄŸÄ±, radyo/CD anahtarÄ±).
			// AYRI tablo: hem DEÄÄ°ÅKEN (Readonly durum) hem OLAY yolu.
			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null) continue;
				TryRegisterControl(go);
			}

			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null) continue;
				string n = go.name;
				if (string.IsNullOrEmpty(n)) continue;

				// Degisken senkronu (musluk, dus, anahtar, ev isiklari ve
				// hala yapÄ±sal durumu olan nesneler). KapÄ±lar Register
				// iÃ§inde elenir.
				if (!Wanted(n)) continue;
				Register(go);
			}

			discovered = true;

			// v15: menteşe taraması özeti (tek satır). Önceden her nesne
			// için [HINGE-JOINT] + [IS-TRACKED-DOOR] yazılıyordu:
			// canlı logda 2278 satır (%64), bunun 1500'i BoltPM / ragdoll
			// iskeleti / Hose üzerinde -- yani kapı olmayan nesneler.
			// Ayrıntı istenirse Telemetry.Verbose (smp verbose) ile açılır.
			SmpLog.Print("world: joint taramasi -> joint'li nesne " + hingeHits
				+ ", isimden kapi " + nameDoorHits
				+ ", joint nedeniyle elenen " + skippedByJoint + ".");

			// Client joiner: initial sync may have arrived before discovery.
			// Request a resync so host re-sends door/control states.
			try {
				if (SteamTransport.HasRemote && !SteamTransport.IsHost) {
					NetworkPacketManager.SendSyncRequest();
					SmpLog.Log("[WorldObjectSync] Discovery complete, requested state resync from host.");
				}
			}
			catch { }

			// FPS TEÅHÄ°SÄ°: kaÃ§ deÄŸiÅŸken kare baÅŸÄ±na karÅŸÄ±laÅŸtÄ±rÄ±lÄ±yor?
			// KULLANICI BELÄ°RTÄ°SÄ°: "kasma, FPS 50'ye dÃ¼ÅŸtÃ¼." â€” 10 Hz'de
			// bu toplam her kare okunuyor. SayÄ±yÄ± logla ki artmadÄ±ÄŸÄ±nÄ± gÃ¶relim.
			int db = 0, df = 0;
			for (int i = 0; i < tracked.Count; i++) {
				if (tracked[i] == null) continue;
				db += tracked[i].BoolVars.Length;
				df += tracked[i].FloatVars.Length;
			}
			SmpLog.Log("world objects: " + tracked.Count + " object(s), "
				+ db + " bool + " + df + " float = " + (db + df)
				+ " deÄŸiÅŸken @ " + (1f / POLL_INTERVAL).ToString("F0")
				+ " Hz tarama.");
			SmpLog.Log("world objects: tracking " + tracked.Count
				+ " door/tap/shower object(s) with syncable FSM variables, "
				+ doorList.Count + " openable panel FSM(s)" + TierTally()
				+ " on the OPEN/CLOSE event path."
				+ (controls.Count > 0
					? "; " + controls.Count + " car-control FSM(s) on the event path (ignition,"
						+ " CD player lid, RADIO/CD switch) vars reserved for PartSync"
					: "; NO car-control FSM found (ignition / CD player lid will NOT sync)")
				+ (skippedDoorVars > 0
					? "; " + skippedDoorVars + " door object(s) kept OUT of the variable "
						+ "table (doors sync by event only - two paths fed each other back)"
					: "") + ".");
		}

		/// <summary>
		/// Bu nesne bir cÄ±vata mÄ±? CÄ±vatalarÄ±n Tightness'i BoltBatch'in
		/// konusudur; kapÄ± yolu onlara dokunmamalÄ±.
		/// </summary>
		static bool IsBoltObject(GameObject go) {
			if (go == null) return false;
			if (go.name.StartsWith("Bolt", StringComparison.OrdinalIgnoreCase)) return true;
			try {
				PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
				if (fsms == null) return false;
				for (int i = 0; i < fsms.Length; i++) {
					if (fsms[i] == null) continue;
					if (string.Equals(fsms[i].FsmName, "Screw", StringComparison.Ordinal))
						return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>
		/// YOLUN BÄ°R DÄ°ZÄ°NÄ°, verilen sÃ¶zcÃ¼klerden birini iÃ§eriyor mu?
		///
		/// Dizin baÅŸÄ±na eÅŸleÅŸme ÅŸart. "lid" kelimesi `MISC/holiday
		/// present(Clon2)/Parts/Money` iÃ§inde geÃ§tiÄŸi iÃ§in alt dize eÅŸleÅŸmesi
		/// bir hediye Ã§antasÄ±nÄ± kapa sanardÄ±. Tahsis yoktur: yol '/' ile
		/// taranÄ±r.
		/// </summary>
		static bool SegmentHit(string path, string[] words) {
			if (string.IsNullOrEmpty(path) || words == null) return false;
			int start = 0;
			int n = path.Length;
			while (start <= n) {
				int slash = path.IndexOf('/', start);
				int end = slash >= 0 ? slash : n;
				for (int w = 0; w < words.Length; w++) {
					if (path.IndexOf(words[w], start, end - start, StringComparison.OrdinalIgnoreCase) >= 0)
						return true;
				}
				if (slash < 0) break;
				start = slash + 1;
			}
			return false;
		}

		/// <summary>
		/// Bu FSM bir menteÅŸeli panel mi?
		///
		/// Ã–LÃ‡ÃœM (level2): `SetHingeJointLimits` yalnÄ±zca GERÃ‡EK menteÅŸeli
		/// panellerde var â€” 21 nesne, tamamÄ± araÃ§ kapÄ±sÄ± / kaput / Ã¶n kapak ve
		/// sÄ±fÄ±r yanlÄ±ÅŸ pozitif:
		///
		///   CARPARTS/PartsCar/bootlid(Clone)/Handles   SetHingeLimits+AddTorque
		///   CARPARTS/PartsCar/hood(Clone)             SetHingeLimits+SetRotation
		///   FERNDALE(1630kg)/Bootlid/Bootlid          SetHingeLimits+SetRotation
		///   ... 8 aracÄ±n sÃ¼rÃ¼cÃ¼ kapÄ±larÄ± ...
		///
		/// BU YÃœZDEN TEST `PlayAnimation` DEÄÄ°L. PlayAnimation'Ä± PayMoney
		/// (17 NPC), buzdolabÄ±, mikrodalga, gÃ¼neÅŸlik, akaryakÄ±t pompasÄ± da
		/// kullanÄ±yor; Ã¶lÃ§Ã¼mde 36 yanlÄ±ÅŸ pozitif veriyordu.
		///
		/// `SetHingeJointLimits` Assembly-CSharp.dll iÃ§inde PUBLIC bir tip
		/// (dnSpy ile doÄŸrulandÄ±) ve mod o dosyayÄ± zaten referanslÄ±yor, bu
		/// yÃ¼zden typeof kullanÄ±lÄ±yor: yansÄ±ma maliyeti ve isim karÅŸÄ±laÅŸtÄ±rmasÄ±
		/// yok, tek bir tip testi var.
		/// </summary>
		static bool UsesHingeAction(PlayMakerFSM fsm) {
			FsmState[] states;
			try { states = fsm.Fsm.States; } catch { return false; }
			if (states == null) return false;
			for (int i = 0; i < states.Length; i++) {
				FsmState st = states[i];
				if (st == null) continue;
				FsmStateAction[] actions;
				try { actions = st.Actions; } catch { continue; }
				if (actions == null) continue;
				for (int a = 0; a < actions.Length; a++) {
					if (actions[a] is HutongGames.PlayMaker.Actions.SetHingeJointLimits)
						return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Bu nesne bir aÃ§Ä±lÄ±r panel mi, ve hangi yolla tanÄ±ndÄ±?
		///
		/// ÃœÃ‡ KADEMELÄ° Ã–LÃ‡ÃœM. Tek baÅŸÄ±na "OPEN/CLOSE bildiriyor" testi
		/// YANLIÅ: TV dÃ¼ÄŸmesi, buzdolabÄ±, mikrodalga, el freni, gÃ¼neÅŸlik, kÄ±rÄ±labilir
		/// pencere ve 17 adet PayMoney hepsi OPEN+CLOSE bildiriyor. Ã–lÃ§Ã¼mde 139
		/// Use/UseOld FSM'inin yalnÄ±zca 51'i gerÃ§ek kapÄ±ydÄ±.
		///
		///   TIER 1  SetHingeJointLimits  -> 21 nesne, hepsi araÃ§ kapÄ±sÄ±/kaput/
		///           Ã¶n kapak. SÄ±fÄ±r yanlÄ±ÅŸ pozitif. (KullanÄ±cÄ± isteÄŸi: kaput ve
		///           Ã¶n kapaklar buradan geliyor.)
		///   TIER 2  yakÄ±t kapaÄŸÄ± adÄ±    -> 3 nesne, tam olarak Ã¶lÃ§Ã¼len Ã¼Ã§
		///           yakÄ±t kapaÄŸÄ±. MenteÅŸeleri yok, Tier 1'in kaÃ§Ä±rdÄ±ÄŸÄ± bu
		///           grup bu yÃ¼zden ada dayanÄ±yor.
		///   TIER 3  yolda "door"         -> 33 nesne, ev ve bina kapÄ±larÄ±.
		///
		/// Toplam 57, reddedilen 85. Ret listesi de doÄŸrulandÄ±: hiÃ§biri kapÄ±,
		/// kaput ya da kapa deÄŸil.
		/// </summary>
		static readonly string[] RADIO_LID_WORDS = { "pivot_radio" };

		static string PanelTier(GameObject go, PlayMakerFSM fsm, string path) {
			if (UsesHingeAction(fsm)) return TIER_HINGE;
			if (SegmentHit(path, RADIO_LID_WORDS)) return TIER_DOOR;
			if (SegmentHit(path, FUEL_WORDS)) return TIER_FUEL;
			if (SegmentHit(path, DOOR_WORDS)) return TIER_DOOR;
			return null;
		}

		/// <summary>
		/// Nesne bir aÃ§Ä±lÄ±r panel mi, ve hangi kapÄ± olaylarÄ±nÄ± bildiriyor?
		///
		/// ÃœÃ§ Ã¶lÃ§Ã¼t birlikte: Use/UseOld olmalÄ±, en az bir kapÄ± olayÄ±
		/// bildirmeli ve PanelTier onu tanÄ±malÄ±.
		/// </summary>
		static void TryRegisterDoor(GameObject go) {
			if (go == null) return;
			// Ã–n filtre: 37.000 nesneye GetComponents Ã§aÄŸÄ±rmamak iÃ§in.
			if (!PanelCandidate(go)) return;
			// CÄ±vatalar dÄ±ÅŸlanÄ±r: `CARPARTS/.../door left(Clone)/Bolts/BoltPM`
			// yolu "door" iÃ§erdiÄŸi iÃ§in Ã¶n filtreden geÃ§er, ama Ã¼zerindeki
			// Tightness float'u BoltBatch'in konusudur. Ä°ki yol aynÄ± deÄŸiÅŸkeni
			// yazarsa birbirini ezer.
			if (IsBoltObject(go)) return;
			PlayMakerFSM[] fsms;
			try { fsms = go.GetComponents<PlayMakerFSM>(); }
			catch { return; }
			if (fsms == null || fsms.Length == 0) return;

			string path = null;
			for (int i = 0; i < fsms.Length; i++) {
				PlayMakerFSM fsm = fsms[i];
				if (fsm == null) continue;
				// CANLI HATA (düzeltildi): burada çiplak `fsm.FsmName` vardı.
				// `FsmName` bazı bileşenlerde BOŞ döner (bkz. FsmNames.RealName),
				// yani o kapı `IsNullOrEmpty` kapısından sessizce düşer ve tabloya
				// hiç girmez → karşı tarafta asla açılmaz. Artık üç kademeli
				// çözümleme kullanılıyor; kod tabanının geri kalanı (GodMode,
				// CdPlayerSync) zaten bunu yapıyordu, kapı yolu yapmıyordu.
				string fsmName = FsmNames.RealName(fsm);
				if (string.IsNullOrEmpty(fsmName)) continue;
				bool interaction = false;
				for (int k = 0; k < DOOR_FSMS.Length; k++) {
					if (string.Equals(fsmName, DOOR_FSMS[k], StringComparison.Ordinal)) {
						interaction = true;
						break;
					}
				}
				if (!interaction) continue;

				// Bu FSM hangi kapÄ± olaylarÄ±nÄ± BÄ°LDÄ°RÄ°YOR? YalnÄ±zca bildirilen
				// olay gÃ¶nderilir; bildirilmemiÅŸ bir olay karÅŸÄ± tarafta
				// Fsm.ProcessEvent'te sessizce kaybolur ve hata gÃ¶rÃ¼nmez olur.
				bool[] ev = new bool[DOOR_EVENTS.Length];
				int declared = 0;
				for (int e = 0; e < DOOR_EVENTS.Length; e++) {
					if (!FsmNames.HasEvent(fsm, DOOR_EVENTS[e])) continue;
					ev[e] = true;
					declared++;
				}
				if (declared == 0) continue;

				if (path == null) {
					try { path = SceneDump.PathOf(go.transform); }
					catch { return; }
					if (string.IsNullOrEmpty(path)) return;
				}
				string tier = PanelTier(go, fsm, path);
				if (tier == null) continue;

				string key = DoorKey(path, fsmName);
				if (doorByKey.ContainsKey(key)) continue;
				Door d = new Door();
				d.Path = path;
				d.Name = go.name;
				d.FsmName = fsmName;
				d.Fsm = fsm;
				d.Events = ev;
				d.Tier = tier;
				doorByKey[key] = d;
				// SÄ±cak yol Ã¶nbelleÄŸi: keÅŸifte doldur, bÃ¶ylece her kapÄ±
				// olayÄ±nda hiyerarÅŸi yÃ¼rÃ¼yÃ¼p string Ã¼retmiyoruz.
				doorByFsm[fsm] = d;
				doorList.Add(d);
			}
		}

		static string DoorKey(string path, string fsmName) {
			return (path ?? "") + "|" + (fsmName ?? "");
		}

		/// <summary>
		/// YavaÅŸ yol: hiyerarÅŸiden yol + FSM adÄ± ile tabloyu ara.
		/// YalnÄ±zca Ã¶nbellek boÅŸaldÄ±ÄŸÄ±nda Ã§alÄ±ÅŸÄ±r (keÅŸifte bir kez, ya da
		/// nesne yeniden doÄŸarsa).
		/// </summary>
		static Door ResolveDoorSlow(PlayMakerFSM fsm) {
			GameObject go;
			try { go = fsm.gameObject; } catch { return null; }
			if (go == null) return null;
			// Keşifle AYNI çözümleme: FsmName boş olabilir (bkz. TryRegisterDoor
			// içindeki düzeltme). Burada da çıplak `fsm.FsmName` kullanılırsa
			// boş adlı kapı burada bir daha bulunamaz.
			string fsmName = FsmNames.RealName(fsm);
			string path = SceneDump.PathOf(go.transform);
			Door d;
			if (!doorByKey.TryGetValue(DoorKey(path, fsmName), out d)) {
				// Geç klon: keşif dünyası yüklerken çalıştı, bu nesne
				// sonradan doğdu (kurulum/kutu). O an kaydet, tekrar dene.
				try { TryRegisterDoor(go); } catch { }
				if (!doorByKey.TryGetValue(DoorKey(path, fsmName), out d)) return null;
			}
			return d;
		}

		static bool Wanted(string n) {
			for (int i = 0; i < TAP_OBJECTS.Length; i++)
				if (string.Equals(n, TAP_OBJECTS[i], StringComparison.OrdinalIgnoreCase)) return true;
			for (int i = 0; i < SHOWER_OBJECTS.Length; i++)
				if (string.Equals(n, SHOWER_OBJECTS[i], StringComparison.OrdinalIgnoreCase)) return true;
			for (int i = 0; i < SWITCH_OBJECTS.Length; i++)
				if (string.Equals(n, SWITCH_OBJECTS[i], StringComparison.OrdinalIgnoreCase)) return true;
			// Yaprak adÄ± "door" iÃ§eriyorsa deÄŸiÅŸken senkronuna da al: araÃ§ kapÄ±sÄ±
			// `Open` bool'u taÅŸÄ±yan tek yer, o deÄŸer de kayÄ±t durumunun parÃ§asÄ±.
			//
			// DÄ°KKAT: kapsam BÄ°LEREK geniÅŸletilmedi. Kaput/Ã¶n kapak/yakÄ±t kapaÄŸÄ±
			// nesneleri `hood(Clone)`, `bootlid(Clone)/Handles` gibi adlar taÅŸÄ±r
			// ve `Removal` + `BoltCheck` FSM'leri Ã¼zerler; onlarÄ± deÄŸiÅŸken
			// tablosuna almak parÃ§anÄ±n Tightness/Bolted toplamÄ±nÄ± uzaktan
			// yazmak anlamÄ±na gelirdi, yani BoltCheck ile Ã§akÄ±ÅŸÄ±rdÄ±. Panel
			// senkronu DoorEvent Ã¼zerinden, Ã§akÄ±ÅŸma yaratmadan yÃ¼rÃ¼yor.
			if (n.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			// SIRA Ã–NEMLÄ° â€” DÃœZELTME (canlÄ± log 13:04:09):
			//   DOOR changed â€¦/Car/Electrics  Battery=True ElectricsOK=True
			// hÃ¢lÃ¢ yazÄ±yor, yani `Electrics` DEÄÄ°ÅKEN TABLOSUNDA.
			// Sebep: `IsCarControl(n)` kontrolÃ¼ `EventOnly(n)` kontrolÃ¼nden
			// Ã–NCE geliyordu ve `true` dÃ¶nerek erken Ã§Ä±kÄ±yordu.
			// DoÄŸrusu: Ã¶nce hariÃ§ tut, sonra kabul et.
			//
			// Elektrik/starter FSM'leri altÄ± tane ham float taÅŸÄ±yor
			// (Charge, AlternatorWear, AlternatorEfficiency, RPM, Volts,
			// Math1..3). `Charge` sÄ±nÄ±rsÄ±z bÃ¼yÃ¼dÃ¼ÄŸÃ¼ iÃ§in deÄŸer HER KARE
			// deÄŸiÅŸiyor â†’ saniyede bir paket + kare baÅŸÄ±na karÅŸÄ±laÅŸtÄ±rma
			// â†’ FPS dÃ¼ÅŸÃ¼ÅŸÃ¼. Onlar yalnÄ±zca olay ve durum aynasÄ±yla taÅŸÄ±nÄ±r.
			if (EventOnly(n)) return false;

			// ARAC KONTROLLERI: kontak, CD player kapaÄŸÄ±, radyo/CD anahtarÄ±.
			// KullanÄ±cÄ± belirtisi: "kontak aÃ§Ä±ktÄ± gÃ¶rmedi, CD player'i aÃ§tÄ±m
			// gÃ¶rmedi" â†’ bu nesneler ne deÄŸiÅŸken ne olay tablosundaydÄ±.
			if (IsCarControl(n)) return true;
			return false;
		}

		/// <summary>
		/// PartSync / BoltSync'in sahibi olduÄŸu deÄŸiÅŸken mi? Ã–yleyse
		/// deÄŸiÅŸken tablosuna ALMA: iki yazÄ±cÄ± geri besleme dÃ¶ngÃ¼sÃ¼ kurar
		/// (kapÄ±daki canlÄ± kanÄ±tÄ±n aynÄ±sÄ± â€” bkz. Register notu).
		/// </summary>
		static bool ReservedVar(string name) {
			if (string.IsNullOrEmpty(name)) return false;
			for (int i = 0; i < RESERVED_VARS.Length; i++)
				if (string.Equals(name, RESERVED_VARS[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>
		/// Nesneyi degisken tablosuna al.
		///
		/// YALNIZCA KENDÄ° FSM'LERÄ° (canlÄ± log kanÄ±tÄ±, 23:20): Ã¶nceden
		/// GetComponentsInChildren kullanÄ±lÄ±yordu, yani bir kapÄ± kaydÄ± hem
		/// kendi FSM'lerini hem de ALT NESNESÄ°NÄ°NKÄ°LERÄ° (door(Clone)) kendine
		/// baÄŸlÄ±yordu. Alt nesne de ayrÄ±ca kaydolduÄŸu iÃ§in her deÄŸiÅŸiklik Ä°KÄ°
		/// KEZ hem yazÄ±lÄ±yor hem de kabloya gidiyordu: logda her satÄ±r
		/// "pivot_door_left" ve "door left(Clone)" olarak tekrarlaniyordu.
		///
		/// Alt nesneler zaten ayrÄ± kayÄ±t olduÄŸu iÃ§in (adlarÄ± "door" iÃ§eriyor)
		/// burada yalnÄ±zca kendi bileÅŸenleri yeter.
		/// </summary>
		static void Register(GameObject go) {
			string path = SceneDump.PathOf(go.transform);
			if (string.IsNullOrEmpty(path)) return;
			if (byPath.ContainsKey(path)) return;

			// KAPI Ä°SE DEÄÄ°ÅKEN TABLOSUNA GÄ°RMEZ.
			//
			// CANLI KANIT (client logu 15:08:41-45), Satsuma sol kapÄ±sÄ±:
			//   15:08:41.135 DOOR opened ... Use OPEN     <- OLAY yolu
			//   15:08:41.658 DOOR opened ... Open=True   <- DEÄÄ°ÅKEN yolu
			//   15:08:41.963 DOOR closed ... Use CLOSE   <- OLAY yolu
			//   15:08:42.125 DOOR closed ... Open=False  <- DEÄÄ°ÅKEN yolu
			//   ... saniyede birkaÃ§ kez, 5 saniye boyunca
			//
			// SEBEP: GERÄ° BESLEME DÃ–NGÃœSÃœ. KapÄ± iki tabloda birdenydi:
			//   * DoorEvent yolu `Use` FSM'ine OPEN/CLOSE oynatÄ±yor,
			//   * ObjectState yolu aynÄ± kapÄ±nÄ±n `Open` bool'unu yazÄ±yor.
			// KarÅŸÄ± taraf `Open=False` uygulayÄ±nca oyunun kendi FSM'i
			// CLOSE'a geÃ§iyor, biz o deÄŸiÅŸikliÄŸi "yerel" sanÄ±p geri
			// gÃ¶nderiyoruz, Ã¶te yandan o da yapÄ±yor... kapÄ± saniyede
			// birkaÃ§ kez aÃ§Ä±lÄ±p kapanÄ±yor. GÃ¶rÃ¼ntÃ¼: kapÄ± yerinden
			// kopyuyor.
			//
			// Ã‡Ã–ZÃœM: bir nesnenin TEK senkron sahibi olmalÄ±. KapÄ±nÄ±n
			// sahibi OLAY yoludur, Ã§Ã¼nkÃ¼ olay oyunun kendi FSM'ini
			// Ã§alÄ±ÅŸtÄ±rÄ±r; kilit kontrolÃ¼, menteÅŸe sÄ±nÄ±rlarÄ± ve ses
			// oradan gelir. `Required1`/`Open`/`Rot` bool/float'larÄ±
			// gÃ¶nderilmez.
			if (IsTrackedDoor(go)) {
				skippedDoorVars++;
				return;
			}

			PlayMakerFSM[] fsms;
			try { fsms = go.GetComponents<PlayMakerFSM>(); }
			catch { return; }
			if (fsms == null || fsms.Length == 0) return;

			// Bool ve float degiskenlerini topla.
			List<FsmBool> bools = new List<FsmBool>(8);
			List<FsmFloat> floats = new List<FsmFloat>(8);
			for (int f = 0; f < fsms.Length; f++) {
				PlayMakerFSM fsm = fsms[f];
				if (fsm == null || fsm.Fsm == null) continue;
				// FSM degiskenleri fsm.Variables icinde; erisim
				// bozulursa nesne atlanir (sessiz bozulma istemiyoruz).
				if (fsm.FsmVariables != null && fsm.FsmVariables.BoolVariables != null) {
					FsmBool[] bs = fsm.FsmVariables.BoolVariables;
					// PartSync/BoltSync sahipliÄŸi olan deÄŸiÅŸkenler Ã‡IKARILIR.
					// Aksi halde iki yol aynÄ± bool'u yazar â†’ geri besleme.
					if (bs != null) for (int i = 0; i < bs.Length; i++)
						if (bs[i] != null && !ReservedVar(bs[i].Name)) bools.Add(bs[i]);
				}
				if (fsm.FsmVariables != null && fsm.FsmVariables.FloatVariables != null) {
					FsmFloat[] fs = fsm.FsmVariables.FloatVariables;
					// LOCAL_ONLY_VARS elemesi FPS iÃ§in ÅŸart: ham hesap
					// float'larÄ± (Math1..3, RPM, Wear, Progressâ€¦) her kare
					// deÄŸiÅŸtiÄŸi iÃ§in nesneyi sÃ¼rekli "dirty" yapÄ±yordu.
					if (fs != null) for (int i = 0; i < fs.Length; i++)
						if (fs[i] != null && !ReservedVar(fs[i].Name)
							&& !LocalOnlyVar(fs[i].Name)) floats.Add(fs[i]);
				}
			}
			if (bools.Count == 0 && floats.Count == 0) return;

			Tracked t = new Tracked();
			t.Name = go.name;
			t.Path = path;
			t.Fsms = fsms;
			t.BoolVars = bools.ToArray();
			t.FloatVars = floats.ToArray();
			t.BoolLast = new bool[t.BoolVars.Length];
			t.FloatLast = new float[t.FloatVars.Length];
			// GÃ¼nlÃ¼k referansÄ± da "ÅŸu anki" deÄŸerle baÅŸlar: dÃ¼nya yÃ¼klenirken
			// olan hiÃ§bir ÅŸey eylem sayÄ±lmaz.
			t.BoolLogged = new bool[t.BoolVars.Length];
			t.FloatLogged = new float[t.FloatVars.Length];
			for (int i = 0; i < t.BoolLast.Length; i++) t.BoolLast[i] = t.BoolVars[i].Value;
			for (int i = 0; i < t.FloatLast.Length; i++) t.FloatLast[i] = t.FloatVars[i].Value;
			for (int i = 0; i < t.BoolLogged.Length; i++) t.BoolLogged[i] = t.BoolLast[i];
			for (int i = 0; i < t.FloatLogged.Length; i++) t.FloatLogged[i] = t.FloatLast[i];

			tracked.Add(t);
			byPath[path] = t;
		}

		/// <summary>Dunya cikisinda temizle.</summary>
		public static void Clear() {
			tracked.Clear();
			byPath.Clear();
			outbox.Clear();
			doorList.Clear();
			doorByKey.Clear();
			doorByFsm.Clear();
			doorOutbox.Clear();
			controls.Clear();
			controlByKey.Clear();
			controlByFsm.Clear();
			PendingIntentReassert.Clear();
			nextDoorFlush = 0f;
			nextCtrlStatePoll = 0f;
			nextCtrlStateSend = 0f;
			nextPoll = 0f;
			stickyUntil = 0f;
			stickyNext = 0f;
			// YENI ALANLAR (sahne degisimi temizligi).
			//
			// CANLI RISK: `c.LastSwitchOn` eski degerde kalirsa `QueueControlEvent`
			// "durum degismedi" deyip odayi HIC gondermez -> "isiklar senkron
			// degil" belirtisi geri gelir. Yon sayaclari ve UI susturma sayaci
			// da yaniltici olur.
			driveJobs.Clear();
			DirectionMeasured = 0;
			DirectionFromEvent = 0;
			SameDirectionDrops = 0;
			SwitchStateNoop = 0;
			SwitchResolved = 0;
			SwitchCoalesced = 0;
			UiBlockedEvents = 0;
			// Kapi zamanlayicilari DUNYA DEGISMEDEN temizlenmiyordu.
			// `Door` nesneleri `doorList.Clear()` ile atildigi icin eski
			// degerler gorunmez olsa da `PendingIntentReassert` ve
			// `sticky*` ESKI DUNYAYA ait kalip yeni oturumu
			// kirletiyordu (dunya degisimi sonrasi "kendiliginden aciliyor"
			// belirtisinin bir kaynagi).
			hingeHits = nameDoorHits = skippedByJoint = 0;
discovered = false;
		}

		// ------------------------------------------------------------------
		// Kare basina
		// ------------------------------------------------------------------

		/// <summary>Degisiklikleri tarar ve gonderir (SteamMPBehaviour.Update).</summary>
		public static void Tick() {
			try {
				if (!discovered) return;

				// Kapi surus job'lari her kare adimlanir (Poll kapisindan ONCE:
				// zincir, degisken taramasindan bagimsiz yurur).
				try { TickDoorDrives(); } catch { }

				float now = Time.time;

				// KAPI OLAYI FLUSH'U ERKEN ÇIKIŞIN ÜSTÜNE TAŞINDI (canlı hata).
				//
				// `FlushDoors` eskiden `if (now < nextPoll) return;` satırının
				// ALTINDAsı, yani yalnızca 0.25 s'de bir (POLL_INTERVAL) koşuyordu.
				// Kapı olayı kuyruğa alındığında en kötü durumda POLL_INTERVAL +
				// DOOR_FLUSH_INTERVAL (0.05) ≈ 300 ms bekliyor, sonra karşı
				// tarafta açılıyordu. "Kapıyı açtım, diğer oyuncuda hemen
				// görünmüyor" belirtisinin bir kaynaı buydu.
				//
				// Artık her kare denenir; `FlushDoors` zaten kendi
				// DOOR_FLUSH_INTERVAL (0.05 s) sınırına sahip, yani saniyede en
				// fazla 20 kapı paketi çıkar. Maliyet sıfır: kuyruk boşken ilk
				// satırda döner.
				FlushDoors(now);

				if (now < nextPoll) return;
				nextPoll = now + POLL_INTERVAL;


				// DEÄÄ°ÅÄ°KLÄ°K TARAMASI BAÄLANTIDAN BAÄIMSIZ.
				//
				// Eylem gÃ¼nlÃ¼ÄŸÃ¼ tek baÅŸÄ±na test ederken de Ã§alÄ±ÅŸmalÄ±: eski sÃ¼rÃ¼m
				// `if (!HasRemote) return` ile baÅŸlÄ±yordu, yani kapÄ± aÃ§ma hiÃ§
				// yazÄ±lmÄ±yordu. Tarama 46 nesne Ã— birkaÃ§ deÄŸiÅŸken, 4 Hz'de;
				// ihmal edilebilir.
				//
				// REFERANS BURADA GÃœNCELLENÄ°R, Flush() Ä°Ã‡Ä°NDE DEÄÄ°L.
				//
				// CanlÄ± log kanÄ±tÄ±: referanslar Flush'ta gÃ¼ncellendiÄŸi iÃ§in
				// tek baÅŸÄ±na testte Flush hiÃ§ Ã§alÄ±ÅŸmadÄ±, Changed() hep true
				// dÃ¶ndÃ¼ ve 46 nesne 4 Hz'de sÃ¼rekli yazÄ±ldÄ±: log 891 KB'dan
				// 2.47 MB'ye Ã§Ä±ktÄ± (25.271 satÄ±r) saniyeler iÃ§inde. Yani
				// "gÃ¶nder" ile "gÃ¶zle" AYRI olmalÄ±: gÃ¶zle her zaman, gÃ¶nder
				// yalnÄ±zca baÄŸlantÄ± varken.
				outbox.Clear();
				for (int i = 0; i < tracked.Count; i++) {
					Tracked t = tracked[i];
					if (t == null) continue;
					// Karsi tarafÄ±n kendi degisikligini yankilamiyoruz.
					if (t.RemoteOwned && now - t.RemoteSetAt < RESEND_COOLDOWN) continue;

					if (!Changed(t)) continue;
					Observe(t);
					t.LastSentAt = now;
					outbox.Add(t);
					TraceChange(t);
				}
				if (outbox.Count > 0 && SteamTransport.HasRemote) Flush();

			// Kontrol DURUMU aynasi (CD player kapagi, motor).
			// Olay yetmiyor: kapak bir FSM durumu ve Part degiskeni
			// uzakta cozulmedigi icin ASSEMBLE gondermek ise yaramiyor.
			PollCtrlStates(now);
			// Elektrik/ kontak degerlerini yapÄ±ÅŸkan tut (ElectricsOK
			// her kare yeniden hesaplandigi icin tek seferlik yazim yetmez).
			StickyForce(now);
			FlushCtrlStates();
			}
			catch (Exception e) {
				SmpLog.Warning("world object tick failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		static bool Changed(Tracked t) {
			bool any = !t.HasBaseline;
			for (int i = 0; i < t.BoolVars.Length; i++) {
				if (t.BoolVars[i] == null) continue;
				bool v = t.BoolVars[i].Value;
				if (v != t.BoolLast[i]) { any = true; break; }
			}
			if (!any) {
				for (int i = 0; i < t.FloatVars.Length; i++) {
					if (t.FloatVars[i] == null) continue;
					if (Mathf.Abs(t.FloatVars[i].Value - t.FloatLast[i]) > 0.001f) { any = true; break; }
				}
			}
			return any;
		}

		/// <summary>
		/// "GÃ¶rdÃ¼m" â€” referans deÄŸerleri gÃ¼ncellenir.
		///
		/// Flush'tan AYRI tutulmasÄ± ÅŸart (bkz. Tick'teki log kanÄ±tÄ±): baÄŸlantÄ±
		/// yokken de deÄŸiÅŸiklik "tÃ¼ketilmiÅŸ" sayÄ±lmalÄ±, yoksa Changed() hep
		/// true dÃ¶ner ve gÃ¼nlÃ¼k saniyede yÃ¼zlerce satÄ±r yazar.
		/// </summary>
		static void Observe(Tracked t) {
			for (int b = 0; b < t.BoolVars.Length; b++) {
				if (t.BoolVars[b] != null) t.BoolLast[b] = t.BoolVars[b].Value;
			}
			for (int f = 0; f < t.FloatVars.Length; f++) {
				if (t.FloatVars[f] != null) t.FloatLast[f] = t.FloatVars[f].Value;
			}
			t.HasBaseline = true;
		}

		/// <summary>
		/// Bir nesnenin ardisik kac poll'dur float bildirdigi "animasyon"
		/// sayilmaya baslayacagi.
		///
		/// Canli log kaniti (21:38): titreyen bir mum 63 satir uretmis
		/// (345 satirlik logun %18'i). Kapilar/acik-kapanik nesneler
		/// ard arda 3'den fazla bildirmez; mum surekli bildiriyor.
		/// </summary>
		const int CHURN_LIMIT = 3;

		/// <summary>Animasyonlu ilan edilen nesne bu sure kadar sessiz kalir.</summary>
		const float CHURN_COOLDOWN = 5f;

		static void TraceChange(Tracked t) {
			try {
				// SON YAZILAN DEÄERE GÃ–RE KIYAS.
				//
				// CanlÄ± log kanÄ±tÄ± (23:12): araÃ§ kapÄ±sÄ± `Open` bool'u oyun
				// tarafÄ±ndan her tur yeniden yazÄ±lÄ±yor ("Open=True" dÃ¶rt kez
				// Ã¼st Ã¼ste) ve gÃ¼nlÃ¼k saniyede 25 satÄ±rÄ± da aÅŸÄ±yordu. Sebep:
				// gÃ¼nlÃ¼k "son GÃ–ZLENEN" deÄŸerle kÄ±yaslÄ±yordu, oysa oyun hedefi
				// sÃ¼rekli geri yazÄ±yor. ArtÄ±k "son YAZILAN" deÄŸerle kÄ±yaslanÄ±yor:
				// aynÄ± deÄŸeri yazmak hiÃ§bir ÅŸey Ã¼retmez, salÄ±nÄ±m tekrarÄ± da
				// susturulur.
				string detail = t.Path;
				string action = "changed";
				int shown = 0;
				bool isDoor = LooksLikeDoor(t.Name);
				bool labelled = false;
				for (int i = 0; i < t.BoolVars.Length && shown < 3; i++) {
					if (t.BoolVars[i] == null) continue;
					bool v = t.BoolVars[i].Value;
					if (v != t.BoolLogged[i]) {
						detail += "  " + FsmName(t.BoolVars[i]) + "=" + v;
						if (isDoor && !labelled) {
							action = v ? "opened" : "closed";
							labelled = true;
						}
						shown++;
					}
					t.BoolLogged[i] = v;
				}
				// Animasyonlu nesne: float'lari izlemiyoruz ama DEGERI
				// yazmaya devam ediyoruz, yoksa "son yazilan" karsilastirmasi
				// birikmis bir farka donusur ve nesne sustukten sonra tek
				// seferde dev bir deger farkiyla patlar.
				bool animated = Time.time < t.ChurnQuietUntil;
				bool anyFloatChange = false;
				for (int i = 0; i < t.FloatVars.Length; i++) {
					if (t.FloatVars[i] == null) continue;
					float v = t.FloatVars[i].Value;
					if (Mathf.Abs(v - t.FloatLogged[i]) > 0.001f) {
						anyFloatChange = true;
						if (!animated && shown < 3) {
							detail += "  " + FsmName(t.FloatVars[i]) + "=" + v.ToString("F2");
							shown++;
						}
					}
					t.FloatLogged[i] = v;
				}
				if (anyFloatChange) {
					t.ChurnPoll++;
					if (t.ChurnPoll == CHURN_LIMIT) {
						// Tam esik: nesnenin ANIMASYONLU oldugunu bir kez bildir.
					}
					if (t.ChurnPoll >= CHURN_LIMIT) t.ChurnQuietUntil = Time.time + CHURN_COOLDOWN;
				}
				else {
					t.ChurnPoll = 0;
					t.ChurnQuietUntil = 0f;
				}
				if (shown == 0) return;   // yeni bir ÅŸey yok
			}
			catch { }
		}

		static bool LooksLikeDoor(string n) {
			if (string.IsNullOrEmpty(n)) return false;
			string lower = n.ToLowerInvariant();
			if (lower.IndexOf("dashboard", StringComparison.OrdinalIgnoreCase) >= 0) return false;
			if (lower.IndexOf("gauge", StringComparison.OrdinalIgnoreCase) >= 0) return false;
			if (lower.IndexOf("waterpress", StringComparison.OrdinalIgnoreCase) >= 0) return false;
			if (lower.IndexOf("pivotwater", StringComparison.OrdinalIgnoreCase) >= 0) return false;
			return lower.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static void Flush() {
			try {
				MemoryStream s = Protocol.Begin(Protocol.Msg.ObjectState);
				BinaryWriter w = new BinaryWriter(s);
				w.Write((byte)outbox.Count);
				for (int i = 0; i < outbox.Count; i++) {
					Tracked t = outbox[i];
					w.Write(t.Path);
					w.Write((byte)t.BoolVars.Length);
					for (int b = 0; b < t.BoolVars.Length; b++) {
						w.Write(FsmName(t.BoolVars[b]));
						w.Write(t.BoolVars[b].Value);
					}
					w.Write((byte)t.FloatVars.Length);
					for (int f = 0; f < t.FloatVars.Length; f++) {
						w.Write(FsmName(t.FloatVars[f]));
						w.Write(t.FloatVars[f].Value);
					}
					t.HasBaseline = true;
					w.Write(SteamTransport.PingMs);
				}
				w.Flush();
				if (SteamTransport.SendReliable(Protocol.Finish(s))) SentUpdates += outbox.Count;
			}
			catch (Exception e) {
				SmpLog.Warning("world object send failed: " + e.Message);
			}
			finally { outbox.Clear(); }
		}

		/// <summary>FsmBool/FsmFloat degiskeninin adi (yoksa bos).</summary>
		static string FsmName(FsmBool v) {
			try { return v.Name ?? ""; } catch { return ""; }
		}
		static string FsmName(FsmFloat v) {
			try { return v.Name ?? ""; } catch { return ""; }
		}

		// ------------------------------------------------------------------
		// KAPI OLAYI: yakalama
		// ------------------------------------------------------------------

		/// <summary>
		/// SÄ±cak yoldan Ã§aÄŸrÄ±lÄ±r (AssemblyPatch'in PlayMaker olay kancalarÄ±ndan).
		///
		/// MALÄ°YET: oyunun en sÄ±cak noktasÄ± â€” her FSM her olay buradan geÃ§iyor.
		/// Bu yÃ¼zden ilk iÅŸ olay adÄ±nÄ±n UZUNLUÄU. OPEN=4, CLOSE=5, OPENDOOR=7,
		/// DoorOpen=8; yani dÃ¶rt bÃ¼yÃ¼k harfli string karÅŸÄ±laÅŸtÄ±rmasÄ± ve en
		/// kÃ¶tÃ¼de dÃ¶rt eÅŸitlik testi. Nesneye, sÃ¶zlÃ¼ÄŸe ya da yola hiÃ§
		/// dokunulmaz.
		///
		/// CANLI KANIT (bu satÄ±r bir hataydÄ± ve dÃ¼zeltildi): burada
		/// `if (!SteamTransport.HasRemote) return;` vardÄ±, yani baÄŸlantÄ±
		/// yokken OLAY YOLU HÄ°Ã‡ Ã‡ALIÅMIYORDU. "KapÄ±larÄ± aÃ§Ä±p kapattÄ±m,
		/// log yok" belirtisinin sebebi buydu. ARAÃ‡ kapÄ±larÄ± (HAYOSIKO
		/// dahil) deÄŸiÅŸken tablosundan Ã§Ä±karÄ±ldÄ±ÄŸÄ± iÃ§in onlarÄ±n TEK
		/// yolu budur; ev kapÄ±larÄ± ise deÄŸiÅŸken yoklamasÄ±ndan
		/// loglandÄ±ÄŸÄ± iÃ§in farkÄ± fark etmiyordu.
		///
		/// DOÄRU BÃ–LME â€” Tick()'teki yorumla aynÄ± ilke: "gÃ¶zle her zaman,
		/// gÃ¶nder yalnÄ±zca baÄŸlantÄ± varken". GÃ¶nderim ÅŸartÄ± kuyruÄŸa
		/// ekleme ve FlushDoors'ta zaten var.
		/// </summary>
		/// <summary>
		/// GÃ¶nderim sonrasÄ± kaÃ§ saniye boyunca aynÄ± kapÄ±nÄ±n kendi geÃ§iÅŸ
		/// olaylarÄ± bastÄ±rÄ±lÄ±r.
		///
		/// CANLI Ã–LÃ‡ÃœM (host, client baÄŸlÄ±yken): DoorEvent 1 gonder / 16 alin.
		/// Client 16, host 1 gÃ¶nderiyordu: yankÄ±. Ä°z:
		///   12:47:47.588  RX OPEN DoorWC     durum-oncesi=Wait player
		///   12:47:51.478  RX OPEN DoorFront  durum-oncesi=Wait player
		///   12:47:53.047  RX OPEN DoorFront  durum-oncesi=Wait player
		///
		/// SEBEP: `EchoGuard` yalnÄ±z `SendEvent` Ã§aÄŸrÄ±sÄ±nÄ±n Ä°Ã‡Ä°NDE
		/// susturur. Ama bir kapÄ± FSM'i `OPEN` aldÄ±ktan sonra GEÃ‡Ä°ÅÄ°
		/// sonraki karede tamamlar; o geÃ§iÅŸin Ã¼rettiÄŸi olay
		/// `EchoGuard` kapsamÄ± Ã§oktan kapanmÄ±ÅŸken yakalanÄ±r ve geri
		/// gÃ¶nderilir. KapÄ± aÃ§Ä±p kapandÄ±kÃ§a iki taraf birbirini
		/// besler.
		///
		/// NOT: bastÄ±rma YALNIZCA bu kapÄ±nÄ±n kendi geÃ§iÅŸ olaylarÄ±na
		/// uygulanÄ±r. Oyuncunun gerÃ§ek etkileÅŸimi (imleÃ§ kapÄ±da E'ye
		/// basmak) aynÄ± FSM'i tetiklediÄŸi iÃ§in bunun ayrÄ±mÄ± Ã¶lÃ§Ã¼mle
		/// yapÄ±ldÄ±: gÃ¶nderimden sonraki 1.2 saniyede gelen olaylar
		/// sayÄ±lÄ±yor ve bastÄ±rÄ±lÄ±yor.
		/// </summary>
		public const float SEND_DAMP_WINDOW = 1.2f;

		/// <summary>Zaman penceresiyle bastÄ±rÄ±lan kapÄ± olayÄ± sayÄ±sÄ±.</summary>
		public static int SendDampedEvents;

		public static void OnFsmEvent(PlayMakerFSM fsm, string eventName) {
			if (fsm == null || string.IsNullOrEmpty(eventName)) return;
			if (SteamMP.EchoGuard.Suppressed) return;

			// UI SIZINTISI KAPISI: chat/konsol acikken kamera donuk oldugu icin
			// UI tiklamasi dunya tiklamasina donusuyor ve kapilar/salterler
			// karsida yanlis duruma gidiyor. Gonderimi sustur, oyun normal.
			try {
				if (MenuMouse.BlockWorldInteraction) {
					UiBlockedEvents++;
					if (Telemetry.Verbose)
						SmpLog.Debug("world", "door", "UI acik, gonderim susturuldu: "
							+ eventName);
					return;
				}
			} catch { }

			// ZAMANSAL YANKI BASKISI. YakÄ±nda gÃ¶nderdiÄŸimiz bir olayÄ±n
			// ardÄ±ndan bu kapÄ±nÄ±n kendi geÃ§iÅŸi Ã¼retiyorsa, o Ã¼retilen
			// olay yanÄ±ktÄ±r: geri gÃ¶nderilirse iki taraf birbirini
			// besler ve kapÄ± aÃ§Ä±k-kapalÄ± dÃ¶ngÃ¼sÃ¼ baÅŸlar.
			Door damp = null;
			try { damp = doorByFsm != null ? doorByFsm[fsm] : null; } catch { }
			if (damp != null && damp.SendDampedAt > 0f
				&& Time.time - damp.SendDampedAt < SEND_DAMP_WINDOW) {
				SendDampedEvents++;
				if (Telemetry.Verbose)
					SmpLog.Debug("world", "door", "yanki bastirildi: "
						+ eventName + " @ " + damp.Path);
				return;
			}

			// KONTROL OLAYLARI Ã–NCE: "OFF"/"ON"/"ACC"/"USE" gibi kÄ±sa adlar
			// kapÄ± filtresine de takÄ±labiliyor, ama asÄ±l sahipleri kontroller.
			if (IsControlEvent(eventName)) {
				try { QueueControlEvent(fsm, eventName); }
				catch { }
				return;
			}

			// v27: `len < 3 || len > 8` UZUNLUK FILTRESI KALDIRILDI.
			// SMP_EVT_LEN_V27
			// Gerekce: hemen asagida `DOOR_EVENTS` beyaz listesi var; o
			// zaten taninmis isimleri secen ikinci bir filtre oluyordu.
			// Ayrica oyunun `GLOBALEVENT`(11), `NOTINSTALLED`(12),
			// `ELEC_CUTOFF`(11) gibi gecerli olaylari sessizce duserdi
			// ve SAYAC YAZILMAZDI. Tel formatini degistirmez: olay adi
			// zaten string.
			bool candidate = false;
			for (int i = 0; i < DOOR_EVENTS.Length; i++) {
				if (string.Equals(DOOR_EVENTS[i], eventName, StringComparison.Ordinal)) {
					candidate = true;
					break;
				}
			}
			if (!candidate) return;

			try { QueueDoorEvent(fsm, eventName); }
			catch { }
		}

		static bool IsControlEvent(string name) {
			for (int i = 0; i < CONTROL_EVENTS.Length; i++)
				if (string.Equals(CONTROL_EVENTS[i], name, StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		/// <summary>
		/// KapÄ± olayÄ±nÄ± kuyruÄŸa al. FSM bu tablodaysa gÃ¶nderilir; deÄŸilse
		/// sayac artar ve `smp doors` Ã§Ä±ktÄ±sÄ± bunu gÃ¶sterir â€” yani "kapÄ±
		/// aÃ§Ä±lmÄ±yor" belirtisi ile "kapÄ± keÅŸfedilmedi" belirtisi birbirinden
		/// ayrÄ±labilir.
		/// </summary>
		static void QueueDoorEvent(PlayMakerFSM fsm, string eventName) {
			if (!discovered || doorList.Count == 0) return;

			// PERFORMANS: FSM -> Door Ã¶nbelleÄŸi.
			//
			// Ã–nceden her OLAY iÃ§in SceneDump.PathOf() Ã§aÄŸrÄ±lÄ±yordu; o da
			// hiyerarÅŸiyi yÃ¼rÃ¼yÃ¼p Stack<string> + StringBuilder + string
			// tahsis ediyor, ardÄ±ndan DoorKey() bir ikinci string birleÅŸtiriyordu.
			// KapÄ± aÃ§Ä±lÄ±rken FSM bir olay BURST'i Ã¼retir, yani aynÄ± yol
			// saniyede onlarca kez yeniden hesaplanÄ±yordu. Ãœstelik bu yol
			// baÄŸlantÄ±dan baÄŸÄ±msÄ±z olduÄŸu iÃ§in (tek baÅŸÄ±na test de bu maliyeti
			// Ã¶der) "kasma hissi" tek baÅŸÄ±na oynarken de oluÅŸuyordu.
			//
			// Åimdi Ã§Ã¶zÃ¼mleme keÅŸifte bir kez yapÄ±lÄ±r; sÄ±cak yol tek bir
			// sÃ¶zlÃ¼k bakÄ±ÅŸÄ± ve sÄ±fÄ±r tahsis.
			Door d;
			if (!doorByFsm.TryGetValue(fsm, out d) || d == null) {
				d = ResolveDoorSlow(fsm);
				// Ã‡Ã¶zÃ¼lemeyenler de Ã¶nbelleÄŸe girer: aksi hÃ¢lde her olayda
				// yeniden PathOf() denenir ve UnresolvedDoors sayacÄ± ÅŸiÅŸer.
				if (d == null) {
					if (!doorByFsm.ContainsKey(fsm)) doorByFsm[fsm] = null;
					UnresolvedDoors++;
					return;
				}
				doorByFsm[fsm] = d;
			}
			if (d == null) return;

			string path = d.Path;
			string fsmName = d.FsmName;

			// Bu olayÄ± FSM gerÃ§ekten bildiriyor mu? Hook, hedef Ã§Ã¶zÃ¼mlenmeden
			// de Ã§aÄŸrÄ±labildiÄŸi iÃ§in (Ã¶rn. FSM adÄ± boÅŸ dÃ¶ndÃ¼yse) burada ikinci
			// kez doÄŸrulanÄ±r.
			int idx = EventIndex(eventName);
			if (idx < 0 || d.Events == null || idx >= d.Events.Length || !d.Events[idx]) return;
			// YANKI: uzaktan uyguladigimiz olayin kendi FSM yankisi ~1.2 sn icinde gelir.
			if (d.AppliedAt > 0f && Time.time - d.AppliedAt < 1.2f) return;
			// DURUM KONTROL: ayni yonde olay gondermeyi onle (host FSM tekrar tetiklemesin).
			// Host FSM "Check if open" durumunda OPEN uretebilir; kapi zaten aciksa
			// tekrar gondermek yanki/tekrar cakismasi yaratiyor.
			string curState = null;
			try { if (d.Fsm != null && d.Fsm.Fsm != null) curState = d.Fsm.Fsm.ActiveStateName; } catch { }
			// ------------------------------------------------------------------
			// YON OLAY ADINDAN OKUNMAZ (canli hata: "kapattim, clientte gorunmuyor")
			// ------------------------------------------------------------------
			// CANLI KANIT (host logu, Satsuma kaputu 13:22): dort tiklamada dort
			// `OPEN`, hic `CLOSE` yok; `[DOOR-VAR]` kacinin GERCEK durumunu
			// her seferinde degistirdigini gosteriyor:
			//   13:22:18.599 OPEN durum=Mouse over    Open=False Rotation=0.00
			//   13:22:20.580 OPEN durum=Mouse over 2  Open=True  Rotation=289.82
			//   13:22:22.589 OPEN durum=Mouse over    Open=False Rotation=0.00
			//   13:22:25.198 OPEN durum=Mouse over 2  Open=True  Rotation=289.82
			// `Use` FSM'i KAPAMA yonunda da `OPEN` uretiyor; kapi 0->289->0->289
			// donuyor. Olay adi yon TASIYAMAZ.
			//
			// COZUM: yon kacinin kendi durumundan okunur (`Open` bool'u, yoksa
			// `Rot`/`Rotation` menteve acisi) ve telde MUTLAK `OPEN`/`CLOSE`
			// olarak gider. Karsi taraf boyle yonu bilir.
			//
			// SILINEN ESKI KODUN IKI HATASI:
			//  a) `stateOpen` = durum adinda "Open" gecmesi. Satsuma kapi
			//     durumlari: `Mouse off` / `Mouse over 2` / `Check if open` /
			//     `Open hood` / `Close hood`. `"Check if open"` icinde "open"
			//     gectigi icin `stateOpen` HEP true oluyor ve gonderilen `OPEN`
			//     bastiriliyordu -> bazi acmalar hic aga cikmadi.
			//  b) `Close hood`da `stateOpen=false` oldugu icin `CLOSE` de
			//     bastiriliyordu -> kapatma hic aga cikamiyordu.
			// ------------------------------------------------------------------
			// YON OLAY ADINDAN OKUNUR (bu turun terslik duzeltmesi)
			// ------------------------------------------------------------------
			// CANLI KANIT (host logu) -- `[DOOR-VAR]` ve `[DOOR-SRC]` AYNI
			// MILISANIYEDE, yani bool TOGGLE ONCESI degerde:
			//   13:49:57.174 [DOOR-VAR] OPEN durum=Check position DoorOpen=False
			//   13:49:57.174 [DOOR-SRC] OPEN ev=OPEN click=DOWN
			//   13:49:58.681 [DOOR-VAR] CLOSE durum=Test cops       DoorOpen=True
			//   13:49:58.681 [DOOR-SRC] CLOSE ev=CLOSE click=-
			// Ayni desen arac kapisinda:
			//   13:51:02.481 [DOOR-VAR] OPEN durum=Check position Open=False Rot=7.08
			//   13:51:02.481 [DOOR-SRC] OPEN ev=OPEN click=-
			//
			// Bool'dan yon okuyunca "kapıyı açtım" -> telde `CLOSE` gidiyordu
			// (kullanıcı: "açtığımda clientte kapanıyor"). OLAY ADI daima dogru
			// yon; sadece `OPEN`/`CLOSE`a NORMALIZE edilir.
			//
			// Dumper kaniti her iki ailede de yon olayini tasir:
			//   EV  : `Check position` BoolTest(DoorOpen) -> OPEN / Close door
			//   ARAÇ: `Check if open` BoolTest(Open)      -> OPEN / CLOSE
			bool wantOpen = EventIsOpen(eventName);
			string wireEvent = wantOpen ? "OPEN" : "CLOSE";

			// Ayni yonun hizli tekrari. Dumper kaniti: ev kapisi `Check
			// position`dan, arac kapisi `Check if open`dan her karede yeni
			// yon olayi uretebiliyor. Kuyruk kapi basina birlestirir ama FSM
			// her karede yeniden doldurur; kisa bekleme sart.
			const float doorResendGuard = 0.30f;
			if (d.SendDampedAt > 0f && (Time.time - d.SendDampedAt) < doorResendGuard
				&& string.Equals(d.LastSentWire, wireEvent, StringComparison.Ordinal)) {
				SameDirectionDrops++;
				return;
			}
			DirectionFromEvent++;
			d.LastSentEvent = eventName;
			d.LastSentWire = wireEvent;
			d.SendDampedAt = Time.time;
			// KUYRUK BIRLESTIRME: ayni kapinin eski olayi atilir, en yeni kalir.
			for (int i = doorOutbox.Count - 1; i >= 0; i--) {
				DoorEvent q = doorOutbox[i];
				if (string.Equals(q.Path, path, StringComparison.Ordinal)
					&& string.Equals(q.FsmName, fsmName, StringComparison.Ordinal))
					doorOutbox.RemoveAt(i);
			}
			if (doorOutbox.Count >= DOOR_QUEUE_LIMIT) doorOutbox.RemoveAt(0);
			DoorEvent e = new DoorEvent();
			e.Path = path;
			e.FsmName = fsmName;
			e.Event = wireEvent;   // MUTLAK yon; olay adinin kendisi degil
			doorOutbox.Add(e);
			bool isOpen = string.Equals(eventName, "OPEN", StringComparison.Ordinal)
				|| string.Equals(eventName, "OPENDOOR", StringComparison.Ordinal)
				|| string.Equals(eventName, "DoorOpen", StringComparison.Ordinal);
			PlayMakerFSM srcFsm = null;
			try {
				for (int q2 = 0; q2 < doorList.Count; q2++) {
					if (doorList[q2] != null
						&& string.Equals(doorList[q2].Path, path, StringComparison.Ordinal)) {
						srcFsm = doorList[q2].Fsm;
						break;
					}
				}
			}
			catch { }
			string srcState = null;
			try {
				if (srcFsm != null && srcFsm.Fsm != null) srcState = srcFsm.Fsm.ActiveStateName;
			}
			catch { }
			Door door = null;
			try {
				for (int q3 = 0; q3 < doorList.Count; q3++) {
					if (doorList[q3] != null
						&& string.Equals(doorList[q3].Path, path, StringComparison.Ordinal)) {
						door = doorList[q3];
						break;
					}
				}
			}
			catch { }
			if (door != null && isOpen) {
				door.IntentOpen = true;
				door.IntentOpenAt = Time.time;
			}
			try {
				PlayMakerFSM df = door != null ? door.Fsm : null;
				if (df != null && df.Fsm != null) {
					System.Text.StringBuilder vs = new System.Text.StringBuilder(400);
					FsmBool[] bl = null;
					try { bl = df.FsmVariables.BoolVariables; } catch { }
					for (int j = 0; bl != null && j < bl.Length; j++) {
						if (bl[j] == null) continue;
						if (vs.Length > 340) break;
						if (vs.Length > 0) vs.Append(" ");
						vs.Append(bl[j].Name).Append("=").Append(bl[j].Value);
					}
					FsmFloat[] fl = null;
					try { fl = df.FsmVariables.FloatVariables; } catch { }
					for (int k = 0; fl != null && k < fl.Length; k++) {
						if (fl[k] == null) continue;
						if (vs.Length > 340) break;
						if (vs.Length > 0) vs.Append(" ");
						vs.Append(fl[k].Name).Append("=").Append(fl[k].Value.ToString("F2"));
					}
					if (Telemetry.Verbose) SmpLog.Print("[DOOR-VAR] " + (isOpen ? "OPEN " : "CLOSE")
						+ " durum=" + df.Fsm.ActiveStateName + "  " + path);
					if (Telemetry.Verbose) SmpLog.Print("[DOOR-VAR]   bool/float: " + vs.ToString());
				}
				else if (door != null) {
					if (Telemetry.Verbose) SmpLog.Print("[DOOR-VAR] " + (isOpen ? "OPEN " : "CLOSE")
						+ "  " + path + "  (FSM null, degisken okunamadi)");
				}
			}
			catch (Exception de) {
				SmpLog.Debug("world", "door", "durum dokumu basarisiz: " + de.Message);
			}
			if (Telemetry.Verbose) SmpLog.Print("[DOOR-SRC] " + (isOpen ? "OPEN " : "CLOSE")
				+ " yerel-fsm  " + path + "  fsm=" + fsmName + "  ev=" + eventName
				+ "  t=" + Time.time.ToString("F3") + "  kuyruk=" + doorOutbox.Count
				+ (srcState != null ? "  durum=" + srcState : "") + "  " + MenuMouse.ClickCtx());
			if (door != null) {
				try {
					if (SteamMPBehaviour.Instance != null)
						SteamMPBehaviour.Instance.StartCoroutine(ProbeDoorAngle(door, "yerel-" + (isOpen ? "OPEN" : "CLOSE")));
				}
				catch { }
			}
		}

		static int EventIndex(string eventName) {
			for (int i = 0; i < DOOR_EVENTS.Length; i++) {
				if (string.Equals(DOOR_EVENTS[i], eventName, StringComparison.Ordinal)) return i;
			}
			return -1;
		}

		static string PickDoorEvent(Door d, bool open) {
			if (d == null || d.Events == null) return null;
			if (open) {
				int i = EventIndex("USE");
				if (i >= 0 && i < d.Events.Length && d.Events[i]) return "USE";
				i = EventIndex("OPEN");
				if (i >= 0 && i < d.Events.Length && d.Events[i]) return "OPEN";
				i = EventIndex("DoorOpen");
				if (i >= 0 && i < d.Events.Length && d.Events[i]) return "DoorOpen";
				i = EventIndex("OPENDOOR");
				if (i >= 0 && i < d.Events.Length && d.Events[i]) return "OPENDOOR";
				return null;
			}
			int ci = EventIndex("CLOSE");
			if (ci >= 0 && ci < d.Events.Length && d.Events[ci]) return "CLOSE";
			int ui = EventIndex("USE");
			if (ui >= 0 && ui < d.Events.Length && d.Events[ui]) return "USE";
			return null;
		}

		static void FlushDoors(float now) {
			if (doorOutbox.Count == 0) return;
			if (!SteamTransport.HasRemote) { doorOutbox.Clear(); return; }
			if (now < nextDoorFlush) return;
			nextDoorFlush = now + DOOR_FLUSH_INTERVAL;
			System.Collections.Generic.List<DoorEvent> failed = null;
			for (int i = 0; i < doorOutbox.Count; i++) {
				DoorEvent e = doorOutbox[i];
				// Salter isareti COZULUR: `Lights` degeri bu anda okunur, yani
				// FSM'in `ActivateGameObject(Lights)` + `BoolFlip` islemini
				// YAPTIGI deger. Gonderim aninda okusaydik TOGGLE ONCESI
				// degeri alirdik (kapida gordugumuz terslik hatasi).
				try { ResolveSwitchDeferred(ref e); } catch { }
				if (string.IsNullOrEmpty(e.Event)) continue;   // gondermeye gerek yok
				if (NetworkPacketManager.SendDoorEvent(e.Path, e.FsmName, e.Event)) SentDoorEvents++;
				else {
					if (failed == null) failed = new System.Collections.Generic.List<DoorEvent>(4);
					failed.Add(e);
				}
			}
			doorOutbox.Clear();
			if (failed != null) {
				int j = 0;
				while (j < failed.Count && doorOutbox.Count < DOOR_QUEUE_LIMIT) {
					// BAYAT USE DUSER: USE bir TOGGLEDIR; saniyeler sonra
					// tekrar gonderilirse kapiyi ters yone cevirir ("kendi
					// kendine aciliyor"). Tek seferlik gonderilir, duserse duser.
					if (string.Equals(failed[j].Event, "USE", StringComparison.Ordinal)) {
						DroppedStaleUse++;
						j++;
						continue;
					}
					doorOutbox.Add(failed[j]);
					j++;
				}
				while (j < failed.Count) {
					if (string.Equals(failed[j].Event, "USE", StringComparison.Ordinal)) DroppedStaleUse++;
					j++;
				}
				if (failed.Count > 0 && Telemetry.Verbose)
					SmpLog.Debug("world", "door", "door events kept for retry",
						new LogFields().Add("count", failed.Count));
			}
		}

		public static void OnMessage(BinaryReader r, byte count) {
			for (int oi = 0; oi < (int)count; oi++) {
				try {
					Protocol.AssertBytesRemaining(r, 1, "path length");
					string path = r.ReadString();
					Protocol.AssertBytesRemaining(r, 1, "bool count");
					byte nb = r.ReadByte();
					string[] bnames = new string[(int)nb];
					bool[] bvals = new bool[(int)nb];
					for (int j = 0; j < (int)nb; j++) {
						Protocol.AssertBytesRemaining(r, 1, "bool name");
						bnames[j] = r.ReadString();
						Protocol.AssertBytesRemaining(r, 1, "bool value");
						bvals[j] = r.ReadBoolean();
					}
					Protocol.AssertBytesRemaining(r, 1, "float count");
					byte nf = r.ReadByte();
					string[] fnames = new string[(int)nf];
					float[] fvals = new float[(int)nf];
					for (int k = 0; k < (int)nf; k++) {
						Protocol.AssertBytesRemaining(r, 1, "float name");
						fnames[k] = r.ReadString();
						Protocol.AssertBytesRemaining(r, 4, "float value");
						fvals[k] = r.ReadSingle();
					}
					Protocol.AssertBytesRemaining(r, 4, "padding");
					r.ReadSingle();
					Tracked t;
					if (!byPath.TryGetValue(path, out t) || t == null) { Unresolved++; continue; }

					// Yanki: uyguladigimiz degerleri "son bilinen" yap, yoksa
					// hemen geri gonderip sonsuz donguye girer.
					EchoGuard.Scope scope = EchoGuard.Enter();
					try {
for (int i = 0; i < nb; i++) {
							for (int k = 0; k < t.BoolVars.Length; k++) {
								if (t.BoolVars[k] == null) continue;
								if (!string.Equals(t.BoolVars[k].Name, bnames[i],
										StringComparison.Ordinal)) continue;
								t.BoolVars[k].Value = bvals[i];
							// Elektrik/kontak degerleri: uzak TRUE dediyse
							// YAPIÅKAN baÅŸlat. ElectricsOK bir FSM Ã§Ä±ktÄ±sÄ±
							// ve yerelde her kare yeniden hesaplanÄ±p
							// siliniyor (bkz. STICKY_VARS notu).
							if (bvals[i] && IsStickyVar(bnames[i])) {
								stickyUntil = Time.time + STICKY_SECONDS;
								stickyNext = 0f;
							}
								t.BoolLast[k] = bvals[i];
								break;
							}
						}
						for (int i = 0; i < nf; i++) {
							for (int k = 0; k < t.FloatVars.Length; k++) {
								if (t.FloatVars[k] == null) continue;
								if (!string.Equals(t.FloatVars[k].Name, fnames[i],
										StringComparison.Ordinal)) continue;
								t.FloatVars[k].Value = fvals[i];
								t.FloatLast[k] = fvals[i];
								break;
							}
						}
					}
					finally { scope.Dispose(); }

					t.RemoteOwned = true;
					t.RemoteSetAt = Time.time;
					AppliedUpdates++;
				}
				catch (Exception) {
					Telemetry.BadPackets++;
					SmpLog.Warning("world object packet rejected.");
					return;
				}
			}
		}

		/// <summary>
		/// Gelen kapÄ± olayÄ±nÄ± uygular: olayÄ± hedef FSM'e yeniden gÃ¶nder.
		///
		/// NEDEN YALNIZCA OLAY: deÄŸer yazmak durum makinesini ilerletmiyor.
		/// KarÅŸÄ± tarafta kendi `Use` FSM'i Ã§alÄ±ÅŸÄ±r ve kapÄ± kendi yolunu
		/// aÃ§sÄ±n.
		///
		/// Ã‡Ã–ZÃœMLEME YALNIZCA TAM YOLLA: bir kapÄ±nÄ±n yolu baÅŸka bir kapÄ±yla
		/// (FITTAN/doorr ve HAYOSIKO/doorr gibi) Ã§akÄ±ÅŸabilir; yanlÄ±ÅŸ kapÄ±yÄ±
		/// oynatmak sessiz ve kÃ¶tÃ¼dÃ¼r, o yÃ¼zden isim Ã§Ã¶zÃ¼mlemesi KULLANILMAZ.
		///
		/// Yanki korumasÄ±: yerel FSM olayÄ± iÅŸlerken OPEN/CLOSE yeniden
		/// Ã¼retebilir (Ã¶rneÄŸin bir FSM OPEN'Ä± iÅŸleyip CLOSE'a geÃ§iyorsa).
		/// SuppressScope bu yazÄ±mÄ± yakalar.
		/// </summary>
		/// <summary>
		/// Gelen kapÄ± olayÄ±nÄ± uygular: olayÄ± hedef FSM'e yeniden gÃ¶nder.
		///
		/// NEDEN YALNIZCA OLAY: deÄŸer yazmak durum makinesini ilerletmiyor.
		/// KarÅŸÄ± tarafta kendi `Use` FSM'i Ã§alÄ±ÅŸÄ±r ve kapÄ± kendi yolunu
		/// aÃ§sÄ±n.
		///
		/// Ã‡Ã–ZÃœMLEME YALNIZCA TAM YOLLA: bir kapÄ±nÄ±n yolu baÅŸka bir kapÄ±yla
		/// (FITTAN/doorr ve HAYOSIKO/doorr gibi) Ã§akÄ±ÅŸabilir; yanlÄ±ÅŸ kapÄ±yÄ±
		/// oynatmak sessiz ve kÃ¶tÃ¼dÃ¼r, o yÃ¼zden isim Ã§Ã¶zÃ¼mlemesi KULLANILMAZ.
		///
		/// Yanki korumasÄ±: yerel FSM olayÄ± iÅŸlerken OPEN/CLOSE yeniden
		/// Ã¼retebilir (Ã¶rneÄŸin bir FSM OPEN'Ä± iÅŸleyip CLOSE'a geÃ§iyorsa).
		/// SuppressScope bu yazÄ±mÄ± yakalar.
		/// </summary>
		public static void OnDoorMessage(BinaryReader r) {
			try {
				string path = r.ReadString();
				string fsmName = r.ReadString();
				string eventName = r.ReadString();
				if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(eventName)) return;

				Door d;
				if (!doorByKey.TryGetValue(DoorKey(path, fsmName), out d) || d == null) {
					// KONTROL HEDEFÄ° olabilir: aynÄ± kablo (path, fsm, event)
					// ikisini de taÅŸÄ±yor, ayrÄ±m alÄ±cÄ±da yapÄ±lÄ±yor.
					Control c;
					if (controlByKey.TryGetValue(ControlKey(path, fsmName), out c) && c != null) {
						if (c.Fsm == null) return;

						// ------------------------------------------------------------------
						// SALTER: MUTLAK DURUM, IDEMPOTENT (canli hata: "isik yaniyor")
						// ------------------------------------------------------------------
						// Telde `USE:1` (acik) / `USE:0` (kapali) gelir. Yerel durum
						// zaten hedefse oynatilMAZ. Boylece gonderen tarafin kac kez
						// tikladigi onemsizdir; karsi taraf hedef duruma oturur.
						//
						// CANLI KANIT (host logu 13:20): `switch_wc` 10 saniyede 11
						// kez `USE` gonderiyordu; `BoolFlip` sayimi tek sayida
						// SONUK birakiyordu.
						string applyEvent = eventName;
						int wantOn = -1;
						if (eventName.StartsWith("USE:", StringComparison.Ordinal)) {
							wantOn = (eventName.Substring(4) == "1") ? 1 : 0;
							applyEvent = "USE";
							int haveOn = ReadSwitchOn(c.Fsm);
							if (haveOn == wantOn) {
								SwitchStateNoop++;
								SmpTrace.Act("CTRL", eventName, "skip (zaten "
									+ (wantOn != 0 ? "ACIK" : "KAPALI") + ") " + path);
								return;
							}
						}

						bool ok = false;
						for (int i = 0; c.Events != null && i < c.Events.Length; i++) {
							if (string.Equals(c.Events[i], applyEvent, StringComparison.OrdinalIgnoreCase)) {
								ok = true;
								break;
							}
						}
						if (!ok) return;
						EchoGuard.Scope sc2 = EchoGuard.Enter();
						try {
							c.Fsm.SendEvent(applyEvent);
							// Gonderen tarafin dedigi durumu hatirla: yerel
							// `USE` uygulamasindan sonra bu deger dogru olacak,
							// boylece bir sonraki ayni mesajda tekrar oynatmaz.
							if (wantOn >= 0) c.LastSwitchOn = wantOn;
							AppliedControlEvents++;
							SmpTrace.Act("CTRL", eventName, "applied " + path + "  " + fsmName);
						}
						finally { sc2.Dispose(); }
						return;
					}
					UnresolvedDoors++;
					UnresolvedControls++;
					SmpLog.Debug("world", "door", "OnDoorMessage: door/control not found",
					    new LogFields().Add("path", path).Add("fsm", fsmName).Add("event", eventName));
					return;
				}
				// O olay bu FSM gercekten bildiriyor mu?
				//
				// MUTLAK YON (OPEN/CLOSE) ICIN BU ERKEN RET KALDIRILDI:
				// dumper sayimi (kapi sinifi, 92 OPEN/CLOSE bildiren FSM) 2'si
				// yalniz `OPEN`, 1'i yalniz `CLOSE` bildiriyor. `CLOSE` bildir-
				// meyen bir kapida mesaj burada DUSURULUYOR ve kapatma yine
				// "gorunmez" olurdu. Asil secim (duz yon olayi mi, `USE`
				// toggle mi) asagida yapiliyor.
				int idx = EventIndex(eventName);
				bool isDirEvent = string.Equals(eventName, "OPEN", StringComparison.Ordinal)
					|| string.Equals(eventName, "CLOSE", StringComparison.Ordinal);
				if (!isDirEvent
					&& (idx < 0 || d.Events == null || idx >= d.Events.Length || !d.Events[idx])) {
				    SmpLog.Debug("world", "door", "OnDoorMessage: event not in FSM",
				        new LogFields().Add("path", path).Add("fsm", fsmName).Add("event", eventName));
				    return;
				}
				if (d.Fsm == null) return;

				// ------------------------------------------------------------------
				// ==================================================================
				// KAPI ALICISI -- EV KAPISI ve ARAÇ KAPISI AYRI YOL
				// ==================================================================
				// DUMPER KANITI: iki aile de `BoolTest` ile karar veriyor ama
				// GIRIS OLAYI farkli. Birlikte ele alininca birbirine giriyor.
				//
				//  EV KAPISI (DoorFront1/Pivot/Handle, fsm=Use)
				//    Wait player --USE--> Check position --BoolTest(DoorOpen)-->
				//      Open door / Close door --> PlayAnimation(store_door_open/
				//      store_door_close) + SetBoolValue(Pivot, door_open/close)
				//    DINLENME DURUMU YALNIZCA `USE` KABUL EDER.
				//
				//  ARAÇ KAPISI (PartsCar/door left(Clone), fsm=Use)
				//    Mouse off --Door(tiklama)--> Check position --BoolTest(Open)-->
				//      Mouse over 1/2 --> Open door / Open door 2
				//      (SetHingeJointLimits + AddTorque, yani MENTEVE)
				//    `OPEN`/`CLOSE` GLOBAL olaylardir.
				//
				// CANLI KANIT (bu turun logu) -- gonderimde yon TERS cikti:
				//   13:49:57.174 [DOOR-VAR] OPEN durum=Check position DoorOpen=False
				//   13:49:57.174 [DOOR-SRC] OPEN ev=OPEN click=DOWN
				// Bool TOGGLE ONCESI, olay adi DAIMA dogru yon.
				//   13:51:02.481 [DOOR-VAR] OPEN durum=Check position Open=False Rot=7.08
				//   13:51:02.481 [DOOR-SRC] OPEN ev=OPEN click=-
				// Ayni desen arac kapisinda da -> yon olay adindan okunur.
				//
				// CANLI KANIT (bu turun logu) -- alimda ev kapisi bozuldu:
				//   13:50:14.310 [DOOR-STATE] 'DoorFront' olay ilerletmedi
				//                          (Wait player), duruma zorlandi: Close door
				// `Wait player` `CLOSE` kabul etmiyor ve `ForceState`
				// `PlayAnimation`'i oynatmIYOR -> kapi kapanmadi.
				bool openEvt = string.Equals(eventName, "OPEN", StringComparison.Ordinal)
					|| string.Equals(eventName, "OPENDOOR", StringComparison.Ordinal)
					|| string.Equals(eventName, "DoorOpen", StringComparison.Ordinal);
				bool closeEvt = string.Equals(eventName, "CLOSE", StringComparison.Ordinal);
				if (!openEvt && !closeEvt) {
					idx = EventIndex(eventName);
					if (idx < 0 || d.Events == null || idx >= d.Events.Length || !d.Events[idx]) return;
					if (d.Fsm == null) return;
				}
				string curState = null;
				try { curState = (d.Fsm.Fsm != null) ? d.Fsm.Fsm.ActiveStateName : null; } catch { }

				// --- IDEMPOTENS: kapi zaten hedef durumdaysa OYNATMA ----------
				// Olcum bool (`Open`/`DoorOpen`) veya menteve acisi (`Rot`) ile.
				// --- YONU DOGRULA: `Open` BOOL'UNU HEDEFE YAZ ----------
				//
				// CANLI HATA (11.10-12.10 oturumlari, 9 kez logda):
				//   [DOOR-RX-SKIP] '.../door left(Clone)' zaten ACIK (bool),
				//   olay oynatilmadi
				// Eski kod gelen yonu YEREL `Open` bool'uyla karsilastirip
				// "zaten hedefte" derse olayi OYNATMIYORDU. O bool ise YEREL
				// KAYDIN durumunu tasir, KARSI TARAFIN EYLEMINI degil. Iki
				// taraf farkli kayittan geldiginde (canli kanit: tablo hash'i
				// 0x85a3b65c vs 0x4022e654, civata 267 vs 272) host zaten
				// acik olan bir kapiya gelen OPEN'i atar ve kullanicinin
				// sikayeti tam olarak budur: "kapyi actim, karsi tarafta
				// gorunmuyor".
				//
				// COZUM: atlama kararini yerel bool'dan AL. Bunun yerine
				// hedefi bool'a YAZ (WriteOpenBool) -- boylece `Use` FSM'nin
				// `BoolTest` adimi dogru yonu secer. Yinelenen yankiyi ise
				// asagidaki AppliedOpen/AppliedAt bekcisi (ayni yon, 3 sn) eler.
				bool wantOpen = openEvt;
				if (openEvt || closeEvt) {
					WriteOpenBool(d, wantOpen);
				}

				// --- GIRIS OLAYI: AILEYE GORE -------------------------------
				// EV KAPISI  : `USE` (BoolTest yonu, sonra PlayAnimation).
				// ARAÇ KAPISI: duz `OPEN`/`CLOSE` (global olay).
				bool anim = IsAnimDoor(d);
				string entryEvent;
				if (anim) {
					entryEvent = "USE";
				}
				else {
					entryEvent = wantOpen ? "OPEN" : "CLOSE";
				}
				int eIdx = EventIndex(entryEvent);
				bool entryOk = (eIdx >= 0 && d.Events != null && eIdx < d.Events.Length
					&& d.Events[eIdx]);
				if (!entryOk) {
					// Bildirilmiyor: yedek yol. Once `USE`, sonra dogrudan yon.
					int useIdx = EventIndex("USE");
					if (useIdx >= 0 && d.Events != null && useIdx < d.Events.Length
						&& d.Events[useIdx]) {
						entryEvent = "USE"; entryOk = true;
					}
					else if (EventIndex(entryEvent) >= 0) { entryOk = true; }
				}
				if (!entryOk) return;

				// Ayni yonun 3 sn icindeki tekrarini eleyerek `USE` toggle'in
				// kapiyi geri cevirmesini onleriz.
				bool wantCloseRx = closeEvt;
				if (d.AppliedAt > 0f && (Time.time - d.AppliedAt) < 3f
					&& ((openEvt && d.AppliedOpen) || (wantCloseRx && !d.AppliedOpen))) return;

				string translatedEvent = entryEvent;

				EchoGuard.Scope scope = EchoGuard.Enter();
				d.AppliedAt = Time.time;
				d.AppliedOpen = openEvt;
				d.SendDampedAt = Time.time;

				// ----------------------------------------------------------
				// KAPI JOB SURUCU (kare-adimli) -- eszamanli gonderim YOK.
				// ----------------------------------------------------------
				// KANIT: `PlayMakerFSM.SendEvent` ayni karede ilerletmiyor
				// (durum-oncesi/sonrasi hep ayni cikti) ve bazi varyantlarda
				// gecis hic yok. Karede TEK olay gonderilir, sonraki karede
				// durum okunup zincir surulur (TickDoorDrives). Bool'a
				// dokunulmaz, durum zorlanmaz.
				try {
					StartDoorDrive(d, wantOpen, curState);
				}
				catch (Exception e) {
					SmpLog.Warning("door '" + path + "' job baslatilamadi: " + e.Message);
				}
				finally { scope.Dispose(); }
				AppliedDoorEvents++;
				Door rxDoor = null;
				try {
					for (int q = 0; q < doorList.Count; q++) {
						if (doorList[q] != null
							&& string.Equals(doorList[q].Path, path, StringComparison.Ordinal)) {
							rxDoor = doorList[q];
							break;
						}
					}
				}
				catch { }
				if (rxDoor != null) {
					try { if (SteamMPBehaviour.Instance != null)
						SteamMPBehaviour.Instance.StartCoroutine(
							ProbeDoorAngle(rxDoor, "uzaktan-" + translatedEvent));
					} catch { }
				}
				// UZAKTAN GELEN OLAY. "[ACT] DOOR ..." satirlari YEREL
				// bildirimleri gosterir; bu satir "karsi tarafa bize
				// oynatti" der. Ikisi yan yana gorunurse geri besleme
				// dongusu vardir ve kaynak bu logdan bulunur.
				SmpLog.Print("[DOOR-RX] job UZAKTAN-baslatildi  " + path
					+ "  fsm=" + fsmName + "  (wire " + eventName + ")"
					+ "  t=" + Time.time.ToString("F3")
					+ (curState != null ? "  durum-oncesi=" + curState : ""));
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("door event packet rejected: " + e.Message);
			}
		}

// ------------------------------------------------------------------
		// YAPIÅKAN ELEKTRÄ°K YAZIMI  (Electrics / Starter)
		//
		// KULLANICI BELÄ°RTÄ°SÄ°: "arabanÄ±n kontaÄŸÄ±nÄ± taktÄ±m, gÃ¶rÃ¼nmedi ve
		// Ã§alÄ±ÅŸtÄ±rmayÄ± denedim, araba Ã§alÄ±ÅŸmadÄ±."
		//
		// CANLI KANIT (client logu):
		//   12:50:05.307 DOOR changed â€¦/Car/Starter   ACC=True ElectricsOK=True   â† uzaktan geldi
		//   12:50:24.102 DOOR changed â€¦/Car/Electrics ElectricsOK=False          â† 19 sn sonra SÄ°LÄ°NDÄ°
		//   12:50:26.047 DOOR changed â€¦/Car/Starter   ElectricsOK=False
		//
		// NEDEN: `ElectricsOK` bir FSM Ã‡IKTISIDIR, girdi deÄŸil. KarÅŸÄ± tarafta
		// o FSM her karede yeniden hesaplar ve (kayÄ±tta parÃ§a eksik olduÄŸu
		// iÃ§in â€” log: "part tables differ, host 160 / us 215") `False`
		// yazar. Tek seferlik deÄŸiÅŸken yazÄ±mÄ± bir sonraki karede ezilir.
		//
		// Ã‡Ã–ZÃœM: uzak "TRUE" dediÄŸinde deÄŸer KISA SÃœRE YAPIÅKAN olur â€”
		// `stickyUntil` sÃ¼resince her karede yeniden yazÄ±lÄ±r. BÃ¶ylece:
		//   * gÃ¶sterge Ä±ÅŸÄ±klarÄ± yanar (PowerON / ElectricsON),
		//   * anahtar gÃ¶rÃ¼nÃ¼r,
		//   * `Starter` FSM'i ElectricsOK'Ä± doÄŸru gÃ¶rÃ¼r â†’ motor Ã§alÄ±ÅŸÄ±r.
		// SÃ¼re dolunca normal davranÄ±ÅŸa dÃ¶ner; uzaktan "FALSE" gelirse
		// hemen bÄ±rakÄ±r (kontak Ã§ekildi).
		// ------------------------------------------------------------------

		static readonly string[] STICKY_VARS = {
			"ElectricsOK", "ElectricsON", "PowerON", "ACC",
			"Installed1", "Installed2", "Installed3", "Installed4", "Installed5",
		};

		static float stickyUntil;
		static float stickyNext;

		/// <summary>YapÄ±ÅŸkan yazÄ±mÄ±n aktif olma sÃ¼resi (s).</summary>
		const float STICKY_SECONDS = 8f;

		static void StickyForce(float now) {
			if (now >= stickyUntil || now < stickyNext) return;
			stickyNext = now + 0.25f;
			for (int i = 0; i < tracked.Count; i++) {
				Tracked t = tracked[i];
				if (t == null || t.Name == null) continue;
				if (t.Name != "Electrics" && t.Name != "Starter" && t.Name != "PowerON") continue;
				for (int k = 0; k < t.BoolVars.Length; k++) {
					FsmBool b = t.BoolVars[k];
					if (b == null) continue;
					if (!IsStickyVar(b.Name)) continue;
					if (!b.Value) continue;              // uzaktan FALSE ise dokunma
					if (t.BoolLast[k] == b.Value) continue;   // zaten doÄŸru
					b.Value = true;
					t.BoolLast[k] = true;                 // yankÄ±: geri gÃ¶nderme
				}
			}
		}

		static bool IsStickyVar(string n) {
			for (int i = 0; i < STICKY_VARS.Length; i++)
				if (string.Equals(n, STICKY_VARS[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		// ------------------------------------------------------------------
		// KONTROL DURUMU AYNASI  (CtrlState = 58)
		//
		// KULLANICI KANITI: "kontaÄŸÄ± aÃ§tÄ±ÄŸÄ±mda xmas ledler yanÄ±yor fakat
		// cdplayer aÃ§tÄ±ÄŸÄ±mda gÃ¶rÃ¼nmÃ¼yor karÅŸÄ±dan."
		//
		// Neden ledler yanÄ±yor ama CD player kapaÄŸÄ± gÃ¶rÃ¼nmÃ¼yor:
		//   * LED'ler `Electrics`/`ElectricsOK` BOOL deÄŸiÅŸkenlerine baÄŸlÄ±;
		//     yeni DEÄÄ°ÅKEN tablosu onlarÄ± gÃ¶nderiyor â†’ gÃ¶rsel doÄŸru.
		//   * CD player kapaÄŸÄ± bir FSM DURUMU: `cd player(Clone)` FSM
		//     'Assembly':  State 1 -> Check for Part -> Find correct part
		//                  -> Wait for assembly -> Assemble -> "CD in"
		//     KapaÄŸÄ± aÃ§an durumda `PlayAnimation(cd_sled_in)` +
		//     `ActivateGameObject(Part, ChangeTrack)` + `SetParent` Ã§alÄ±ÅŸÄ±r.
		//     Bu bir DEÄÄ°ÅKEN deÄŸil, olay da deÄŸil â€” dÃ¼z bir FSM durumu.
		//
		// OLAY YOLU NEDEN YETMÄ°YOR: `ASSEMBLE`/`CDIN` gÃ¶ndermek iÃ§in karÅŸÄ±
		// tarafta `Part` deÄŸiÅŸkeninin (disk nesnesinin) Ã§Ã¶zÃ¼lmesi gerekir;
		// iki kaydÄ±n farklÄ± olmasÄ± durumunda (log kanÄ±tÄ±: "part tables
		// differ, host 160 / us 215") Ã§Ã¶zÃ¼lemez ve FSM "Check for Part"ta
		// kalÄ±r â€” kapak aÃ§Ä±lmaz.
		//
		// Ã‡Ã–ZÃœM: gÃ¶rsel olarak "saf" durum makinelerinin durum adÄ± aynalanÄ±r
		// ve alanda `Fsm.SetState()` ile doÄŸrudan uygulanÄ±r.
		//
		// BEYAZ LÄ°STE â€” fare/etkileÅŸim durumlarÄ± KASITLI olarak yok:
		//   "Wait player", "Mouse over", "Mouse off", "Wait button" gibi
		//   durumlar taÅŸÄ±nÄ±rsa karÅŸÄ± tarafÄ±n dÃ¼ÄŸmeleri kilitlenir.
		// ------------------------------------------------------------------

		/// <summary>
		/// Durumu aynalanacak FSM adlarÄ±. YalnÄ±zca etkileÅŸimsiz (saf
		/// gÃ¶rsel/mantÄ±k) makineler; `Use`/`Knob`/`UseNew` gibi fareye baÄŸlÄ±
		/// olanlar BÄ°LEREK YOK.
		/// </summary>
		static readonly string[] STATE_MIRROR_FSMS = {
			"Assembly",     // cd player(Clone) + trigger_disc: kapak/disk
			"Starter",      // Car/Starter: motor (ACC / Ã§alÄ±ÅŸÄ±yor / stop)
			"StartHelp",    // Starter'Ä±n yardÄ±m FSM'i
			"Pushstart",    // itmeli Ã§alÄ±ÅŸtÄ±rma
			// KULLANICI KANITI: "kontaÄŸÄ± taktÄ±ÄŸÄ±mda sadece xmas led gÃ¶rÃ¼nÃ¼yor
			// ve dashboarddaki Ä±ÅŸÄ±klar yanmÄ±yor; anahtarÄ± taktÄ±ÄŸÄ±m gÃ¶rÃ¼nmÃ¼yor."
			//
			// Sebep: `Starter` bool'larÄ±nÄ± gÃ¶ndermek yetmiyor, Ã§Ã¼nkÃ¼
			// gÃ¶sterge Ä±ÅŸÄ±klarÄ±nÄ± ve anahtarÄ± `IgnitionSatsuma` FSM'i
			// YAPARAK aÃ§Ä±yor (asset, FSM 'UseNew'):
			//   "ACC on" durumu â†’ ActivateGameObject(CarKey, Electrics, Keys,
			//   Starter) + SetFsmBool(Keys/Electrics/Starter)
			// KarÅŸÄ± tarafta `UseNew` FSM'i kendi baÅŸÄ±na Ã§alÄ±ÅŸmadÄ±ÄŸÄ± iÃ§in
			// (orada kimse fareye basmÄ±yor) bu aksiyonlar HÄ°Ã‡ olmuyor.
			//
			// Ã‡Ã¶zÃ¼m: `UseNew` de durum aynasÄ±na giriyor â†’ "ACC on" durumuna
			// ZORLA alÄ±nÄ±nca OnEnter aksiyonlarÄ± oyunun kendi koduyla Ã§alÄ±ÅŸÄ±r:
			// gÃ¶sterge Ä±ÅŸÄ±klarÄ± yanar, anahtar gÃ¶rÃ¼nÃ¼r.
			//
			// "Wait player"/"Wait button" durumlarÄ± STATE_SKIP_WORDS'te
			// olduÄŸu iÃ§in taÅŸÄ±nmaz; yani uzaktaki dÃ¼ÄŸmeler kilitlenmez.
			"UseNew",
		};

		/// <summary>TaÅŸÄ±nmayacak durum adlarÄ± (fare/etkileÅŸim).</summary>
		static readonly string[] STATE_SKIP_WORDS = {
			"mouse", "wait player", "wait button", "mouse over", "mouse off",
		};

		static readonly List<CtrlEvent> ctrlStateOutbox = new List<CtrlEvent>(8);
		static float nextCtrlStateSend;
		static float nextCtrlStatePoll;

		struct CtrlEvent {
			public string Path;
			public string FsmName;
			public string State;
		}

		public static int SentCtrlStates;
		public static int AppliedCtrlStates;

		/// <summary>
		/// Durum aynalanmadan Ã–NCE iki Ã¶lÃ§Ã¼mde bir aynÄ± olmalÄ± (â‰ˆ0.4 sn).
		///
		/// NEDEN: "geÃ§ici" durumlar (FSM 'UseNew' -> "Test", "Check key",
		/// "Sound") birkaÃ§ karede geÃ§ilir. BunlarÄ± aynalarsak karÅŸÄ± taraf
		/// da o geÃ§ici duruma zorlanÄ±r, oradan da olay Ã¼retilir ve yankÄ±
		/// dÃ¶ngÃ¼sÃ¼ kurulur â€” kapÄ±daki salÄ±nÄ±mÄ±n aynÄ±sÄ±. KararlÄ± durumlar
		/// ("ACC on", "CD in") saniyelerce kalÄ±r, bu yÃ¼zden eÅŸiÄŸi geÃ§er.
		/// </summary>
		static bool Stable(Control c, string st) {
			if (!string.Equals(c.PendingState, st, StringComparison.Ordinal)) {
				c.PendingState = st;
				return false;
			}
			return true;
		}

		static bool StateMirrored(string fsmName, string state) {
			if (string.IsNullOrEmpty(state)) return false;
			bool nameOk = false;
			for (int i = 0; i < STATE_MIRROR_FSMS.Length; i++) {
				if (string.Equals(STATE_MIRROR_FSMS[i], fsmName, StringComparison.OrdinalIgnoreCase)) {
					nameOk = true;
					break;
				}
			}
			if (!nameOk) return false;
			for (int i = 0; i < STATE_SKIP_WORDS.Length; i++) {
				if (state.IndexOf(STATE_SKIP_WORDS[i], StringComparison.OrdinalIgnoreCase) >= 0) return false;
			}
			return true;
		}

		/// <summary>
		/// Kontrol durumlarÄ±nÄ± okur; deÄŸiÅŸenleri kuyruÄŸa alÄ±r.
		/// `Tick` iÃ§inden ~5 Hz Ã§aÄŸrÄ±lÄ±r, baÄŸlantÄ±dan BAÄIMSIZ izler
		/// (ilk paket peer baÄŸlanÄ±nca gÃ¶nderilir).
		/// </summary>
		static void PollCtrlStates(float now) {
			if (controls.Count == 0) return;
			if (now < nextCtrlStatePoll) return;
			nextCtrlStatePoll = now + 0.2f;

			for (int i = 0; i < controls.Count; i++) {
				Control c = controls[i];
				if (c == null || c.Fsm == null || c.Fsm.Fsm == null) continue;
				string st = c.Fsm.Fsm.ActiveStateName;
				if (!StateMirrored(c.FsmName, st)) continue;
				if (string.Equals(c.LastState, st, StringComparison.Ordinal)) continue;
				// KararlÄ±lÄ±k ÅŸartÄ±: iki Ã¶lÃ§Ã¼mde bir aynÄ± olmalÄ±.
				if (!Stable(c, st)) continue;

				c.PendingState = "";
				c.LastState = st;
				if (Telemetry.Verbose) SmpTrace.Act("CTRL", "state", c.Path + "  " + c.FsmName + " -> '" + st + "'");

				if (!SteamTransport.HasRemote) continue;
				if (now < nextCtrlStateSend) continue;
				nextCtrlStateSend = now + 0.1f;

				CtrlEvent e = new CtrlEvent();
				e.Path = c.Path;
				e.FsmName = c.FsmName;
				e.State = st;
				if (ctrlStateOutbox.Count >= 12) ctrlStateOutbox.RemoveAt(0);
				ctrlStateOutbox.Add(e);
			}
		}

		/// <summary>Kuyruktaki durumlarÄ± tek pakette gÃ¶nderir.</summary>
		static void FlushCtrlStates() {
			if (ctrlStateOutbox.Count == 0) return;
			if (!SteamTransport.HasRemote) { ctrlStateOutbox.Clear(); return; }
			try {
				MemoryStream ms = Protocol.Begin(Protocol.Msg.CtrlState);
				BinaryWriter w = new BinaryWriter(ms);
				byte n = (byte)Math.Min(ctrlStateOutbox.Count, 12);
				w.Write(n);
				for (int i = 0; i < n; i++) {
					CtrlEvent e = ctrlStateOutbox[i];
					w.Write(e.Path);
					w.Write(e.FsmName);
					w.Write(e.State);
				}
				w.Flush();
				if (SteamTransport.SendReliable(Protocol.Finish(ms))) {
					SentCtrlStates += n;
					if (Telemetry.Verbose) SmpTrace.Act("CTRL", "state", "sent " + n + " control state(s)");
				}
				ctrlStateOutbox.Clear();
			}
			catch (Exception e) {
				SmpLog.Warning("control state send failed: " + e.GetType().Name);
			}
		}

		/// <summary>Gelen kontrol durumunu uygular (gÃ¶rsel ayna).</summary>
		public static void OnCtrlStateMessage(BinaryReader r) {
			byte n;
			try { n = r.ReadByte(); }
			catch { return; }
			for (int i = 0; i < n; i++) {
				string path, fsmName, state;
				try {
					path = r.ReadString();
					fsmName = r.ReadString();
					state = r.ReadString();
				}
				catch (Exception) {
					Telemetry.BadPackets++;
					return;
				}
				try {
					if (EchoGuard.Suppressed) continue;
					Control c;
					if (!controlByKey.TryGetValue(ControlKey(path, fsmName), out c) || c == null) {
						UnresolvedControls++;
						continue;
					}
					if (c.Fsm == null || c.Fsm.Fsm == null) continue;
					if (!StateMirrored(fsmName, state)) continue;
					string cur = c.Fsm.Fsm.ActiveStateName;
					if (string.Equals(cur, state, StringComparison.Ordinal)) continue;
					// Hedefte gerÃ§ekten var mÄ±? SetState yanlÄ±ÅŸ adÄ± sessizce
					// yutar; kontrol edip gÃ¼ncelliyoruz.
					if (!HasState(c.Fsm, state)) {
						UnresolvedControls++;
						continue;
					}

					EchoGuard.Scope sc = EchoGuard.Enter();
					try {
						if (!ForceState(c.Fsm, state)) {
							UnresolvedControls++;
							continue;
						}
						c.LastState = state;
						AppliedCtrlStates++;
						if (Telemetry.Verbose) SmpTrace.Act("CTRL", "state", "mirror " + path + "  "
							+ fsmName + ": '" + cur + "' -> '" + state + "'");
					}
					finally { sc.Dispose(); }
				}
				catch (Exception e) {
					SmpLog.Warning("control state apply failed: " + e.GetType().Name);
				}
			}
		}

		/// <summary>FSM bu durumu gerÃ§ekten tanÄ±mlÄ±yor mu?</summary>
		static bool HasState(PlayMakerFSM fsm, string state) { return FindState(fsm, state) != null; }

		/// <summary>
		/// Sezgisel kapi hedef durumu: FSM durum adlarindan secilir.
		/// Bosa-durumlar (Wait player/Mouse off) secilmez: giris aksiyonu
		/// olmayan duruma gecis kapiyi oynatmaz, FSM'yi yalan duruma sokar.
		/// </summary>
		static string FindDoorState(Door d, bool open) {
			if (d == null || d.Fsm == null) return null;
			try {
				if (d.Fsm.Fsm == null) return null;
				FsmState[] all = d.Fsm.Fsm.States;
				if (all == null) return null;
				string want1 = open ? "Open door" : "Close door";
				string want2 = open ? "Open" : "Close";
				for (int i = 0; i < all.Length; i++) {
					if (all[i] != null && string.Equals(all[i].Name, want1, StringComparison.OrdinalIgnoreCase))
						return all[i].Name;
				}
				for (int i = 0; i < all.Length; i++) {
					if (all[i] != null && string.Equals(all[i].Name, want2, StringComparison.OrdinalIgnoreCase))
						return all[i].Name;
				}
				for (int i = 0; i < all.Length; i++) {
					if (all[i] == null || string.IsNullOrEmpty(all[i].Name)) continue;
					bool hasOpen = all[i].Name.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0;
					bool hasClose = all[i].Name.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0;
					if (open && hasOpen && !hasClose) return all[i].Name;
					if (!open && hasClose && !hasOpen) return all[i].Name;
				}
			}
			catch { }
			return null;
		}

		/// <summary>Adla durum nesnesi.</summary>
		static FsmState FindState(PlayMakerFSM fsm, string state) {
			try {
				FsmState[] all = fsm.Fsm.States;
				for (int i = 0; all != null && i < all.Length; i++) {
					if (all[i] != null && string.Equals(all[i].Name, state, StringComparison.Ordinal)) return all[i];
				}
			}
			catch { }
			return null;
		}

		/// <summary>
		/// FSM'i verilen duruma ZORLA al.
		///
		/// dnSpy kanÄ±tÄ± (PlayMaker.dll, HutongGames.PlayMaker.Fsm):
		///   public  void  UpdateStateChanges()        0x060001F8
		///   private void  SwitchState(FsmState)      0x060001FA   â† HEDEF
		///   private void  EnterState(FsmState)       0x060001FC
		///   public  string get_ActiveStateName()     0x06000185
		///   private void  set_ActiveState(...)       0x06000184  (private!)
		///
		/// Yani her ikisi de private; dÄ±ÅŸarÄ±dan Ã§aÄŸrÄ±labilir tek yol
		/// yansÄ±ma. `EnterState` de private olsa da `SwitchState` onu
		/// Ã§aÄŸÄ±rÄ±yor, dolayÄ±sÄ±yla giriÅŸ aksiyonlarÄ± (PlayAnimation,
		/// ActivateGameObject, SetParent) oyunun kendi koduyla Ã§alÄ±ÅŸÄ±r â€”
		/// yani CD player kapaÄŸÄ± AYNILAR.
		/// </summary>
		static System.Reflection.MethodInfo switchStateMi;

		internal static bool ForceState(PlayMakerFSM fsm, string state) {
			try {
				if (switchStateMi == null) {
					switchStateMi = typeof(Fsm).GetMethod("SwitchState",
						System.Reflection.BindingFlags.NonPublic
						| System.Reflection.BindingFlags.Instance);
				}
				if (switchStateMi == null) return false;
				FsmState target = FindState(fsm, state);
				if (target == null) return false;
				switchStateMi.Invoke(fsm.Fsm, new object[] { target });
				return true;
			}
			catch (Exception e) {
				if (Telemetry.Verbose)
					SmpLog.Warning("control state force failed: " + e.GetType().Name);
				return false;
			}
		}

		/// <summary>
		/// BaÅŸlangÄ±Ã§: tÃ¼m izlenen nesnelerin durumunu bir kez gÃ¶nderir
		/// + KAPILARIN ve KONTROLLERÄ°N ANLIK DURUMUNU olay olarak yollar.
		///
		/// KAPI ANLIK DURUMU NEDEN YENÄ° â€” canlÄ± log (client, 12:09):
		///   KullanÄ±cÄ±: "arkadaÅŸÄ±m oyuna girdi fakat Satsuma'nÄ±n kapÄ±sÄ±
		///   aÃ§Ä±ktÄ± gÃ¶rÃ¼nmedi."
		///   Log'da kapÄ± olayÄ± YOK, sadece kendi iÅŸlemleri gÃ¶rÃ¼nÃ¼yor:
		///   12:08:56.669 the host is in the world. Joining you automatically.
		///
		/// Sebep yapÄ±saldÄ±r: kapÄ±lar OLAY yoluyla gidiyor ve olay yolu sadece
		/// DEÄÄ°ÅÄ°MÄ° taÅŸÄ±r. KatÄ±lma anÄ±nda kapÄ± zaten aÃ§Ä±ksa hiÃ§bir olay
		/// Ã¼retilmemiÅŸtir â†’ yeni oyuncu kapÄ±nÄ±n kapalÄ± olduÄŸunu sanÄ±r.
		/// DeÄŸiÅŸken tablosu da iÅŸe yaramaz: `Register` kapÄ±larÄ± Ã¶zellikle
		/// dÄ±ÅŸlar ("doors sync by event only"), Ã§Ã¼nkÃ¼ iki yol birbirini
		/// besliyordu (o dÃ¶ngÃ¼yÃ¼ kÄ±rmamak iÃ§in o karar verilmiÅŸti).
		///
		/// Ã‡Ã–ZÃœM: katÄ±lmada her kapÄ±nÄ±n FSM durumu okunur ve OPEN/CLOSE
		/// bir kez oynatÄ±lÄ±r. KapÄ± kendi menteÅŸe/limit/ses mantÄ±ÄŸÄ±nÄ± Ã§alÄ±ÅŸtÄ±rÄ±r.
		/// </summary>
		static void QueueFullState() {
			try {
				for (int i = 0; i < doorList.Count; i++) {
					Door d = doorList[i];
					if (d == null || d.Fsm == null || d.Path == null) continue;
					string st = (d.Fsm.Fsm != null) ? d.Fsm.Fsm.ActiveStateName : null;
					if (string.IsNullOrEmpty(st)) continue;

					// ------------------------------------------------------------------
					// YON TAHMINI KALDIRILDI (canli hata: "kapilar kendiliginden aciliyor")
					//
					// ESKI: st icinde "Open" kelimesi araniyordu:
					//   "Check if open" -> OPEN   (kaput KAPALIYDI, yanlis yon)
					//   "Mouse off"     -> CLOSE  (kapi ACIKTI, yanlis yon)
					// Bu, SendAll() ile her baglanti kurulusunda tum panellere
					// yanlis yon gonderiyordu.
					//
					// YENI: yalniz OGRENILMIS durum kullanilir. ProbeDoorAngle
					// yerel olcumde gercek bitis durumlarini `OpenState` /
					// `ClosedState` alanlarina yazar. Ogrenilmediyse hicbir sey
					// gonderilmez: bilinmeyen durum, yanlis yonden iyidir
					// (bos yere kapi oynatip oyuncuyu yaniltmaktan iye).
					string ev = null;
					if (!string.IsNullOrEmpty(d.OpenState)
						&& string.Equals(st, d.OpenState, StringComparison.Ordinal)) ev = "OPEN";
					else if (!string.IsNullOrEmpty(d.ClosedState)
						&& string.Equals(st, d.ClosedState, StringComparison.Ordinal)) ev = "CLOSE";
					if (ev == null) continue;

					int idx = EventIndex(ev);
					if (idx < 0 || d.Events == null || idx >= d.Events.Length || !d.Events[idx]) continue;

					for (int k = 0; k < doorOutbox.Count; k++) {
						DoorEvent q = doorOutbox[k];
						if (string.Equals(q.Path, d.Path, StringComparison.Ordinal)
							&& string.Equals(q.FsmName, d.FsmName, StringComparison.Ordinal)) { k = int.MaxValue; }
					}
					if (doorOutbox.Count >= DOOR_QUEUE_LIMIT) doorOutbox.RemoveAt(0);

					DoorEvent e = new DoorEvent();
					e.Path = d.Path;
					e.FsmName = d.FsmName;
					e.Event = ev;
					doorOutbox.Add(e);
				}

				// Kontroller: katÄ±lma anÄ±ndaki durum.
				// `Starter` motor aÃ§Ä±kken "START ENGINE" gÃ¶nderiyoruz; kapalÄ±yken
				// hiÃ§bir ÅŸey gÃ¶ndermiyoruz (OFF her zaman yanlÄ±ÅŸ olurdu: karÅŸÄ±
				// tarafta motor Ã§alÄ±ÅŸÄ±rken gÃ¶nderilirse kapatÄ±rdÄ±).
				for (int i = 0; i < controls.Count; i++) {
					Control c = controls[i];
					if (c == null || c.Fsm == null) continue;
					string fn = c.FsmName;
					if (!string.Equals(fn, "Starter", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(fn, "UseNew", StringComparison.OrdinalIgnoreCase)) continue;

					string st = (c.Fsm.Fsm != null) ? c.Fsm.Fsm.ActiveStateName : null;
					if (string.IsNullOrEmpty(st)) continue;
					string ev = null;
					if (st.IndexOf("ACC", StringComparison.OrdinalIgnoreCase) >= 0
						|| st.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0
						|| st.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0) {
						ev = "START ENGINE";
					}
					else if (st.IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0) {
						continue;   // kapalÄ±: gÃ¶ndermeyelim (aÅŸaÄŸÄ±da aÃ§Ä±klandÄ±)
					}
					if (ev == null) continue;

					bool declared = false;
					for (int k = 0; c.Events != null && k < c.Events.Length; k++) {
						if (string.Equals(c.Events[k], ev, StringComparison.OrdinalIgnoreCase)) {
							declared = true;
							break;
						}
					}
					if (!declared) continue;
					if (doorOutbox.Count >= DOOR_QUEUE_LIMIT) doorOutbox.RemoveAt(0);
					DoorEvent e = new DoorEvent();
					e.Path = c.Path;
					e.FsmName = c.FsmName;
					e.Event = ev;
					doorOutbox.Add(e);
					SmpTrace.Act("CTRL", ev, "snapshot " + c.Path + "  " + c.FsmName);
				}
			}
			catch { }
		}

		/// <summary>Baslangic: tum izlenen nesnelerin durumunu bir kez gonderir.</summary>
		public static void SendAll() {
			try {
				if (!discovered || !SteamTransport.HasRemote) return;
				outbox.Clear();
				for (int i = 0; i < tracked.Count && outbox.Count < MAX_PER_PACKET; i++) {
					outbox.Add(tracked[i]);
				}
				if (outbox.Count > 0) Flush();

				// KapÄ±/kontrol anlÄ±k durumu (olay yolu).
				QueueFullState();
				FlushDoors(Time.time + 1f);   // ilk gÃ¶nderimi hemen geÃ§ir
			}
			catch (Exception e) {
				SmpLog.Warning("world object initial sync failed: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Tani
		// ------------------------------------------------------------------

		public static string Describe() {
			if (!discovered) return "world objects: not scanned";
			return "world objects: " + tracked.Count + " tracked, sent " + SentUpdates
				+ ", applied " + AppliedUpdates + ", unresolved " + Unresolved
				+ " | doors: " + doorList.Count + " FSM, sent " + SentDoorEvents
				+ ", applied " + AppliedDoorEvents + ", unresolved " + UnresolvedDoors
				+ ", pending " + doorOutbox.Count
				// Kontrol (kontak / CD player kapaÄŸÄ± / RADIO-CD anahtarÄ±) sayacÄ±.
				// KullanÄ±cÄ± belirtisi: "kontak aÃ§Ä±ktÄ± gÃ¶rmedi, CD player'i aÃ§tÄ±m
				// gÃ¶rmedi" â†’ bu tablo boÅŸsa hiÃ§ gÃ¶nderilmiyor demektir.
				+ " | controls: " + controls.Count + " FSM, sent " + SentControlEvents
				+ ", applied " + AppliedControlEvents + ", unresolved " + UnresolvedControls
				// YENI TESHIS (bu turun duzeltmeleri):
				//  * direction measured/event = kapi yonu olcumden mi geldi.
				//    `measured 0` ise hicbir kapida `Open` bool / `Rot` okunamadi
				//    (FSM tablosu degismis olabilir) -> kapilar yine calismaz.
				//  * same-dir drops = ayni yonun 0.3 sn icinde tekrar uretilip
				//    bastirildigi sayi (FSM "Check if open" dongusu).
				//  * switch noop = salterde durum degismedigi icin gonderilmeyen
				//    sayi (mutlak durum sayesinde tekrar sayisi onemsiz).
				//  * ui-blocked = chat/konsol acikken susturulan gonderim.
				//    Sifirdan buyukse UI tiklamalari dunya tiklamasina donusuyordu.
				+ " | direction measured " + DirectionMeasured + "/event " + DirectionFromEvent
				+ ", same-dir drops " + SameDirectionDrops
				// Salter: `USE?` kuyruga konup FLUSH aninda cozulur; boylece
				// gonderim anindaki TOGGLE ONCESI okuma hatasi ve spam yok.
				// `resolved` = gonderilen mutlak durum, `coalesced` = tek kayda
				// indirgenen arka arkaya tiklama, `noop` = durum degismedi.
				+ ", switch resolved " + SwitchResolved
				+ ", coalesced " + SwitchCoalesced
				+ ", noop " + SwitchStateNoop
				+ ", ui-blocked " + UiBlockedEvents;
		}

		/// <summary>
		/// Baglanan NESNE ve DEGISKEN adlarini yazar. Bu, senkron
		/// tablosunu elle doldurmak yerine okunmasi gereken yerdir:
		/// "smp fsm &lt;ad&gt;" ile tek nesne de dokelebilir.
		/// </summary>
		public static void ReportBindings() {
			ReportBindings(null);
		}

		/// <summary>
		/// BaÄŸlantÄ± raporu. <paramref name="filter"/> verilirse yalnÄ±zca adÄ±
		/// eÅŸleÅŸen nesneler dÃ¶kÃ¼lÃ¼r.
		///
		/// Filtre NEDEN GEREKLÄ°: nesne sayÄ±sÄ± logu dolduruyordu. AraÃ§ kapÄ±larÄ±
		/// listenin sonunda olduÄŸu iÃ§in "araÃ§ kapÄ±sÄ± loglanmÄ±yor" sanÄ±lÄ±yordu.
		/// 'smp doors door' ile tek nesne gÃ¶rÃ¼lebiliyor.
		/// </summary>
		public static void ReportBindings(string filter) {
			if (!discovered) { SmpLog.Print("world objects: not scanned."); return; }

			// KapÄ±lar Ã–NCE: asÄ±l ÅŸikayet konusu onlar.
			if (doorList.Count > 0) {
				SmpLog.Print("openable panels on the OPEN/CLOSE path: " + doorList.Count
					+ TierTally());
				int shown = 0;
				int limit = string.IsNullOrEmpty(filter) ? 40 : 60;
				for (int i = 0; i < doorList.Count && shown < limit; i++) {
					Door d = doorList[i];
					if (!string.IsNullOrEmpty(filter)
						&& d.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
					sb2.Length = 0;
					sb2.Append("  [").Append(d.Tier).Append("] ").Append(d.FsmName).Append("  ");
					for (int e = 0; e < DOOR_EVENTS.Length; e++) {
						if (d.Events[e]) sb2.Append(DOOR_EVENTS[e]).Append(' ');
					}
					sb2.Append("  ->  ").Append(d.Path);
					SmpLog.Print(sb2.ToString());
					shown++;
				}
				if (string.IsNullOrEmpty(filter) && doorList.Count > shown)
					SmpLog.Print("  ... " + (doorList.Count - shown) + " more. "
						+ "Filter with 'smp doors door'.");
			} else {
				SmpLog.Print("openable panels on the OPEN/CLOSE path: NONE. "
					+ "'smp scan' lists the real FSM/event names in this build.");
			}

			if (tracked.Count == 0) {
				SmpLog.Print("world objects: nothing tracked. Open a save first.");
				return;
			}
			SmpLog.Print(Describe());
			int shown2 = 0;
			int limit2 = string.IsNullOrEmpty(filter) ? 12 : 40;
			for (int i = 0; i < tracked.Count && shown2 < limit2; i++) {
				Tracked t = tracked[i];
				if (t == null) continue;
				if (!string.IsNullOrEmpty(filter)
					&& t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
				sb2.Length = 0;
				sb2.Append(t.Name).Append(": ");
				// Filtre VERÄ°LMÄ°ÅSE tÃ¼m deÄŸiÅŸkenler dÃ¶kÃ¼lÃ¼r. CanlÄ± log
				// kanÄ±tÄ±: 'GarageDoors' nesnesinde DoorOpen bool'u YOK, sadece
				// float'lar var ve gerÃ§ek durum deÄŸiÅŸkeni listede gizliydi.
				// Filtresiz rapor kÄ±sa kalÄ±r (okunabilirlik), filtreli rapor
				// tam olur (teÅŸhis).
				int boolLimit = string.IsNullOrEmpty(filter) ? 4 : 99;
				int floatLimit = string.IsNullOrEmpty(filter) ? 2 : 99;
				bool any = false;
				for (int b = 0; b < t.BoolVars.Length && b < boolLimit; b++) {
					if (t.BoolVars[b] == null) continue;
					if (any) sb2.Append(", ");
					sb2.Append(FsmName(t.BoolVars[b])).Append('=').Append(t.BoolVars[b].Value);
					any = true;
				}
				for (int f = 0; f < t.FloatVars.Length && f < floatLimit; f++) {
					if (t.FloatVars[f] == null) continue;
					if (any) sb2.Append(", ");
					sb2.Append(FsmName(t.FloatVars[f])).Append('=')
					  .Append(t.FloatVars[f].Value.ToString("F2"));
					any = true;
				}
				if (!any) sb2.Append("(no syncable variable)");
				SmpLog.Print("  " + sb2.ToString());
				shown2++;
			}
			if (string.IsNullOrEmpty(filter) && tracked.Count > shown2) {
				SmpLog.Print("  ... " + (tracked.Count - shown2) + " more. "
					+ "Filter with 'smp doors door' (or any name part), "
					+ "dump one object with: smp fsm FrontDoor");
			}
		}

		/// <summary>Kademe sayacÄ±: " (3 hinged, 3 fuel-hatch, 33 door-name)"</summary>
		static string TierTally() {
			int hinge = 0, fuel = 0, door = 0;
			for (int i = 0; i < doorList.Count; i++) {
				string t = doorList[i].Tier;
				if (t == TIER_HINGE) hinge++;
				else if (t == TIER_FUEL) fuel++;
				else door++;
			}
			return " (" + hinge + " hinged, " + fuel + " fuel-hatch, " + door + " door-name)";
		}

		static readonly System.Text.StringBuilder sb2 = new System.Text.StringBuilder(160);

        // ======================================================================
        // v15: DoorState(80) / SwitchState(81) / EngineState(82) /
        // EnvironmentState(83) ALICI FONKSIYONLARI KALDIRILDI.
        //
        // Bu kimliklerin HICBIR GONDERICISI YOKTU; hep "alici-only"
        // durumdaydilar. CANLI HATLAR SUNDE:
        //
        //   kapi / salter / kontak : Msg.CtrlState  ("Wait player <FSM> <OLAY>")
        //                            + Msg.ObjectState (FsmBool/FsmFloat aynasi)
        //                            + STATE_MIRROR_FSMS (beyaz liste durum aynasi)
        //   motor durumu           : yine Msg.CtrlState / STATE_MIRROR_FSMS;
        //                            FsmBinder.ApplyEngine icin MP_ olaylari
        //   ortam (gunes/saat/hava) : Msg.EnvClock(79) -> EnvSync
        //
        // Kaldirma gerekcesi: bir alici, gondericisi olmayan bir paketi
        // isleyen kod "bu hat calisiyor" izlenimi veriyordu; oysa
        // `smp netstat` bu kimlikleri gostermiyordu bile. Yanlis izlenim,
        // asil gonderi ekisiz yollara (CtrlState / EnvClock) bakmayi
        // birakirdi.
        //
}
}
