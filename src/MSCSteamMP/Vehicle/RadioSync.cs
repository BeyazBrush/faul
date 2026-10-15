using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// RADYO / CD CALAR SENKRONIZASYONU.
	///
	/// GERECEL DEGISKEN ADLARI (loglardan - smp fsm CDPlayer):
	///
	///   FSM 'Update':  CurrentTrack(int), Index(int), MaxTracks(int),
	///                    CurrentCD(string), Text(string)
	///   FSM 'Fetch':   Installed(bool), _HighPass(bool), _LowPass(bool),
	///                    Volume(float), _DistortionLevel(float)
	///
	/// dnSpy kaniti: Assembly-CSharp'te radyo/CD ile ilgili HICBIRIR tip
	/// veya string YOK. Tamamı PlayMaker FSM. Bu yuzden nesne adlari
	/// sahne taramasiyle bulunur, mantik oyunun kendi FSM'ine yazilir.
	///
	/// NEDEN FSM UZERINDEN
	/// -------------------
	/// CD'yi elle "sarki 3 caliyor" diye yonetmek oyunun ses zincirini
	/// atlar: CDPlayer FSM'i sesi AudioSource'a, ses AudioSource'tan
	/// MasterAudio'ya, MasterAudio da arac hoparlorune goturur. Araya
	/// girersek hoparlor secimi (Kaput / Stereo / Radyo) bozulur.
	/// Biz FSM'in FsmInt/FsmBool/FsmFloat degerlerini kopyaliyoruz; ses
	/// zincirine hic dokunmuyoruz.
	///
	/// GECIKME TOLERANSI
	/// -----------------
	/// Ses, konumdan cok daha hata duyarlidir: 200 ms'lik bir atlama
	/// duyulur, 20 ms'lik atlama duyulmaz. Bu yuzden:
	///   * paket GUVENLI (reliable) - iki oyuncu farkli sarkida
	///     kalmak, birinin 40 ms gecikmesinden kotudur
	///   * 1 Hz kalp atisi + sadece DEGISENDE aninda gonderim
	///   * yumuşatma YOK (Lerp burada yanlis; ses seviyesi kademelenince
	///     "pompaliyor" duyuluyor)
	///
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public static class RadioSync {

		/// <summary>Kalp atisi araligi, saniye.</summary>
		public const float HEARTBEAT = 1f;

		/// <summary>Degisim taramasi araligi, saniye.</summary>
		public const float POLL_INTERVAL = 0.25f;

		// ------------------------------------------------------------------
		// Izlenecek nesneler (asset'ten)
		// ------------------------------------------------------------------

		/// <summary>CD calar adaylari.</summary>
		static readonly string[] CD_OBJECTS = {
			"CDPlayer", "CD_player", "CD", "CD1", "CD2", "CD3", "CDs", "PivotCD",
			// SATSUMA'NIN HOPARLÖR NESNELERI. Log kanıtı: ses düğmesi
			// (`[ACT] RADIO float Volume ... @ RADIO/RadioChannels`) yalnızca
			// global kanaldı; hoparlörlerin kendi Settings FSM'i tabloda yoktu.
			// "Volume" durumu db_Required* bool'una SetBoolValue yazıyor, yani
			// Satsuma'da ses açma/kapamanın GERÇEK durumu burada.
			"SpeakerBass", "SpeakerDash",
		};

		/// <summary>Radyo adaylari.</summary>
		static readonly string[] RADIO_OBJECTS = {
			"Radio", "RADIO", "RadioTuner", "RadioVolume", "RadioChannels",
			"RadioCDSwitch", "RadioPivot",
			"Speaker",
			"Power", "Channel",
		};

		/// <summary>
			/// G18: tasinabilir radyo (ITEMS/radio(itemx)) ve hapishane
			/// radyosu (JAIL/RadioPivot/radio(Clone)). Dumper kaniti:
			///   ITEMS/radio(itemx)/Power[Knob] ve
			///   JAIL/RadioPivot/radio(Clone)/Power[Knob]
			///     -> RadioChannels/OnMuteChannel1, OnMuteFolk, OnStatic
			///     -> Fetch, Channel, Speaker
			/// Yani bunlar da GLOBAL radyo durumunu degistiriyordu ama
			/// Wanted() listesinde olmadiklari icin HIC replike edilmiyordu.
			///
			/// G15-G17: KEKMET kendi dashboard radyosi.
			///   Dashboard/Radio/Power[Use] -> MasterAudioPlaylistStop(CD1)
			///   Dashboard/Radio/RADIO_speaker[Channel2] -> Volume/Clock/Color
			///   Dashboard/Radio/Volume[Knob] -> kendi Volume/Scroll
			///
			/// "Power"/"Channel" gibi jenerik adlar TEK BASINA eklenemez:
			/// butun dunyada yuzlerce nesne var. Bunlar icin asagida
			/// RADIO_PATH_HINTS ile YOL filtresi uygulanir.
		/// </summary>
		static readonly string[] RADIO_PATH_HINTS = {
			"Dashboard/Radio",
		};

		/// <summary>Wanted() sirasinda incelenen nesne (yol filtresi icin).</summary>
		static GameObject wantedObject;

		/// <summary>Jenerik adlar icin yol filtresi gerekir.</summary>
		static bool GenericName(string n) {
			return string.Equals(n, "Power", StringComparison.Ordinal)
				|| string.Equals(n, "Channel", StringComparison.Ordinal)
				|| string.Equals(n, "Volume", StringComparison.Ordinal)
				|| string.Equals(n, "Open", StringComparison.Ordinal)
				|| string.Equals(n, "Speaker", StringComparison.Ordinal)
				|| string.Equals(n, "Play", StringComparison.Ordinal)
				|| string.Equals(n, "RADIO_speaker", StringComparison.Ordinal);
		}

		/// <summary>Jenerik ad bu nesnenin yolunda gecerli mi?</summary>
		static bool GenericAllowed(GameObject go) {
			if (go == null) return false;
			string p = null;
			try { p = SceneDump.PathOf(go.transform); } catch { return false; }
			if (string.IsNullOrEmpty(p)) return false;
			for (int k = 0; k < RADIO_PATH_HINTS.Length; k++) {
				if (p.IndexOf(RADIO_PATH_HINTS[k], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			// Tasinabilir radyo ve hapishane radyosu kendi yollarinda.
			if (p.IndexOf("ITEMS/radio(", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (p.IndexOf("JAIL/RadioPivot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (p.IndexOf("ButtonsCD", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		/// <summary>
		/// Senkronlenecek gerçek değişkenler (asset taramasından, fsms.json):
		///   CDPlayer/Update: CurrentTrack, Index, MaxTracks, CurrentCD, Text
		///   CDPlayer/Fetch:  Installed, _HighPass, _LowPass, Volume, _DistortionLevel
		///   RadioChannels/Fetch (ARAÇ RADYOLARI, ortak durum):
		///                   Volume, Channel1, Folk, Static, StaticAntenna,
		///                   _HighPass, _LowPass, _DistortionLevel
		/// </summary>
        static readonly TargetVar[] TARGET_VARS = {
            // CDPlayer - Update FSM
            new TargetVar("CDPlayer", "Update", "CurrentTrack"),
            new TargetVar("CDPlayer", "Update", "Index"),
            new TargetVar("CDPlayer", "Update", "MaxTracks"),
            new TargetVar("CDPlayer", "Update", "CurrentCD"),
            new TargetVar("CDPlayer", "Update", "Text"),
            // CDPlayer - Fetch FSM
            new TargetVar("CDPlayer", "Fetch", "Installed"),
            new TargetVar("CDPlayer", "Fetch", "_HighPass"),
            new TargetVar("CDPlayer", "Fetch", "_LowPass"),
            new TargetVar("CDPlayer", "Fetch", "Volume"),
            new TargetVar("CDPlayer", "Fetch", "_DistortionLevel"),
            // CDPlayer/Fetch - "db_Required": CDPlayer'in kendi AÇIK/KAPALI
            // bayrağı. Fetch FSM'inin "No sound" durumu bu bool'u okuyup
            // AudioSource.volume sıfırlıyor (log'da Volume 0.00 -> 0.10
            // yazan da o). Bu olmadan ses açma/kapama karşı tarafa gitmez.
            new TargetVar("CDPlayer", "Fetch", "db_Required"),
            // SATSUMA'NIN HOPARLÖRLERI — LOG KANITI, ATLANMIŞTI.
            //
            // Kullanıcı sorusu: "cd playerda ON/OFF/VOLUME tuşu var, sesi 10
            // yaptım görünüyor mu?" Log'da Volume değişimi SADECE
            // `RADIO/RadioChannels` üzerinde görünüyordu:
            //   [ACT] RADIO float Volume 0.90 -> 1.00 @ RADIO/RadioChannels
            // ama `Electricity/SpeakerBass/Settings` ve
            // `Electricity/SpeakerDash/Settings` nesneleri tabloda YOKTU.
            //
            // Asset kanıtı (fsms.json):
            //   SpeakerBass/Settings : Volume, DistortionLevel, HighPass,
            //       LowPass, Installed, AntennaLeft, AntennaRight,
            //       StaticAntenna, StaticAntenna1, db_Required1/2/3
            //   SpeakerDash/Settings : aynı liste (db_Required2/3)
            //
            // "Volume" durumu SetBoolValue ile db_Required*'ı yazıyor; yani
            // ses düğmesi ASIL olarak bu bool'lara yazıyor. Bunlar
            // senkronlanmadan ses karşı tarafa hiç taşınmıyor.
            new TargetVar("SpeakerBass", "Settings", "Volume"),
            new TargetVar("SpeakerBass", "Settings", "db_Required1"),
            new TargetVar("SpeakerBass", "Settings", "db_Required2"),
            new TargetVar("SpeakerBass", "Settings", "db_Required3"),
            new TargetVar("SpeakerBass", "Settings", "Installed"),
            new TargetVar("SpeakerBass", "Settings", "HighPass"),
            new TargetVar("SpeakerBass", "Settings", "LowPass"),
            new TargetVar("SpeakerBass", "Settings", "DistortionLevel"),
            new TargetVar("SpeakerBass", "Settings", "AntennaLeft"),
            new TargetVar("SpeakerBass", "Settings", "AntennaRight"),
            new TargetVar("SpeakerBass", "Settings", "StaticAntenna"),
            new TargetVar("SpeakerBass", "Settings", "StaticAntenna1"),
            new TargetVar("SpeakerDash", "Settings", "Volume"),
            new TargetVar("SpeakerDash", "Settings", "db_Required2"),
            new TargetVar("SpeakerDash", "Settings", "db_Required3"),
            new TargetVar("SpeakerDash", "Settings", "Installed"),
            new TargetVar("SpeakerDash", "Settings", "HighPass"),
            new TargetVar("SpeakerDash", "Settings", "LowPass"),
            new TargetVar("SpeakerDash", "Settings", "DistortionLevel"),
            new TargetVar("SpeakerDash", "Settings", "AntennaLeft"),
            new TargetVar("SpeakerDash", "Settings", "AntennaRight"),
            new TargetVar("SpeakerDash", "Settings", "StaticAntenna"),
            new TargetVar("SpeakerDash", "Settings", "StaticAntenna1"),
            // v15: `TargetVar("Radio", "Settings", "Installed")` SILINDI.
            //
            // Dumper kanıtı: "Radio" adında bir GameObject'in "Settings"
            // adında bir FSM'i YOK. Gerçekte olanlar:
            //   Database/DatabaseMechanics/Radio  [Data]
            //   Database/PartsStatus/Radio        [Status]  (CDPlayerOK)
            //   KEKMET/Dashboard/Radio             [Power] [Volume] ...
            // Yani bu baglama hicbir zaman cozulemiyordu.
            //
            // CD yuvasinin dogru sinyali CdPlayerSync'te:
            //   trigger_cd[Assembly].Setup  (bu araca takili mi)
            //   Database/PartsStatus/Radio[Status].CDPlayerOK
            // ARAÇ RADYOLARI — ASSET KANITI (fsms.json taraması)
            //
            // HAYOSIKO/KEKMET/GIFU/FERNDALE gibi araçların kendi
            // RadioPivot'ları var ve KNOB düğmeleri var, ama düğmeler
            // oyunun TEK global radyo durumunu değiştiriyor:
            //   RADIO/RadioChannels   FSM='Fetch'
            //     değişkenler: Channel1, Folk, Static, StaticAntenna,
            //                 Volume, _HighPass, _LowPass, _DistortionLevel,
            //                 OnMuteChannel1, OnMuteFolk, OnStatic
            //     olaylar:    FETCH, OFF, Adjust, Set settings
            // Minibüsün düğmesi bunu okuyor:
            //   HAYOSIKO(...)/RadioPivot/Radio/ButtonsRadio/RadioVolume (FSM 'Knob')
            //     -> GetFsmFloat/RadioChannels/Volume + getGameObject/fsmName
            // Bu yüzden ARAÇ RADYOSU için CDPlayer'a değil, buraya bakmak
            // gerekiyor. Önceden yalnızca CDPlayer hedefleri vardı, yani
            // "minibüsün radyosu aç/kapa senkron olmuyor" belirtisinin
            // sebebi buydu.
            //
            // Volume en önemlisi: radyo AÇIK/kapalı ayrımı da budur
            // (Volume 0 = kapalı). Kanal seçimi Channel1/Folk/Static.
            new TargetVar("RadioChannels", "Fetch", "Volume"),
            new TargetVar("RadioChannels", "Fetch", "Channel1"),
            new TargetVar("RadioChannels", "Fetch", "Folk"),
            new TargetVar("RadioChannels", "Fetch", "Static"),
            new TargetVar("RadioChannels", "Fetch", "StaticAntenna"),
            new TargetVar("RadioChannels", "Fetch", "_HighPass"),
            new TargetVar("RadioChannels", "Fetch", "_LowPass"),
            new TargetVar("RadioChannels", "Fetch", "_DistortionLevel"),
            // v15: KANAL BASI KAPAMA bool'lari. Dumper kanıtı
            // (`RADIO/RadioChannels`[Fetch] degiskenleri):
            //   OnMuteChannel1, OnMuteFolk, OnStatic,
            //   OnStaticAntenna, OnStaticAntenna1
            //
            // OYUNDAKI HER RADYO DUGMESI (araba radyolari, tasinabilir
            // radyo, hapishane radyosu, CD player'in RadioVolume'u)
            // SetFsmBool ile BU bool'lari yaziyor. Yani "hangi kanal
            // susturuldu / radyo acik mi" bilgisinin GERCEK kaynagi
            // burasi; `Volume == 0` yalnizca bir vekildi ve kanal
            // basina ayrim yapamiyordu.
            new TargetVar("RadioChannels", "Fetch", "OnMuteChannel1"),
            new TargetVar("RadioChannels", "Fetch", "OnMuteFolk"),
            new TargetVar("RadioChannels", "Fetch", "OnStatic"),
            new TargetVar("RadioChannels", "Fetch", "OnStaticAntenna"),
            new TargetVar("RadioChannels", "Fetch", "OnStaticAntenna1"),
            // G15-G18: KEKMET dashboard radyosi + tasinabilir/hapishane
            // radyolari (hepsi dumper ile dogrulandi).
            //
            //   KEKMET/Dashboard/Radio/RADIO_speaker[Channel2]:
            //       Volume, Clock          (saat bazli ses/renk)
            //   KEKMET/Dashboard/Radio/Volume[Knob]:
            //       Volume, Scroll          -- bu dugme
            //       RADIO/RadioChannels'a HIC dokunmuyor; kendi
            //       Volume'unu tutuyor, o yuzden ayri baglaniyor
            //   KEKMET/Dashboard/Radio/Power[Use]:
            //       RadioOn, BatteryCharge  -- kapatinca
            //       MasterAudioPlaylistStop(CD1) ile CD de duruyor
            //   ITEMS/radio(itemx)/Volume[Knob]: Volume, MaxVolume, Scroll
            //   ITEMS/radio(itemx)/Power[Knob] : Channel, Speaker
            //   ITEMS/radio(itemx)/Open[Knob]  : Channel, Speaker
            //   ITEMS/radio(itemx)/Play[Charge]: Volume, VolumeCharge
            new TargetVar("RADIO_speaker", "Channel2", "Volume"),
            new TargetVar("RADIO_speaker", "Channel2", "Clock"),
            new TargetVar("Volume", "Knob", "Volume"),
            new TargetVar("Volume", "Knob", "Scroll"),
            new TargetVar("Power", "Use", "RadioOn"),
            new TargetVar("Power", "Use", "BatteryCharge"),
            new TargetVar("Channel", "Knob", "Channel"),
            new TargetVar("Open", "Knob", "Channel"),
            new TargetVar("Open", "Knob", "Speaker"),
            new TargetVar("Play", "Charge", "VolumeCharge"),
            // G18: ITEMS/radio(itemx)/Volume[Knob] `MaxVolume` float'i
            // dumperda mevcut; pil gucune gore azami ses seviyesi.
            new TargetVar("Volume", "Knob", "MaxVolume"),
            // TUNER (araç radyosunun istasyon düğmesi): Knob FSM'inin
            // sarmal değerleri. Asset incelemesi (fsms.json): her aracın
            // RadioTuner/Knob FSM'i 'Rot' ve 'Tune' float değişkenlerine
            // sahip ve UniqueTag 'CarRadioTuner'/'FerndaleRadioTuner'
            // ile ES2'ye yazılıyor. Ses (Volume) loglandı ama tuner
            // loglanmıyordu; istasyon değişimini izlemek/senkronlamak
            // için bu iki değişken de bağlanır.
            new TargetVar("RadioTuner", "Knob", "Tune"),
            new TargetVar("RadioTuner", "Knob", "Rot"),
        };

        /// <summary>(nesneAdi, fsmAdi, degiskenAdi) ucucu.</summary>
        struct TargetVar {
            public string obj;
            public string fsm;
            public string var;
            public TargetVar(string o, string f, string v) { obj = o; fsm = f; var = v; }
        }
		// ------------------------------------------------------------------

		/// <summary>Senkronlanan bir dugum: FSM degiskeni + adi.</summary>
		class Binding {
			public string Path;        // nesne yolu
			/// <summary>
			/// Değişkenin sahibi FSM. Olay senkronunda hedefi bulmak için
			/// saklanıyor: yol bilmek yetmez, aynı yolda "Fetch" ve "Update"
			/// diye İKİ FSM var ve hangisine olay gideceği önemli.
			/// </summary>
			public PlayMakerFSM Fsm;
			public FsmInt IntVar;
			public FsmBool BoolVar;
			public FsmFloat FloatVar;
			public FsmString StringVar;
			public string VarName;
			public int IntValue;
			public bool BoolValue;
			public float FloatValue;
			public string StringValue;
			public bool HasBaseline;

			/// <summary>
			/// Bu düğüm Satsuma'nın kendi CD/radyo parçası mı?
			/// Kullanıcı bilgisi: "cdplayer ve diğer satsuma radyosu araca takılınca
			/// aktif oluyor, garajda duruyor". İkisi de sahnede duruyor; yalnızca
			/// ARAYA TAKILAN radyo çalışıyor.
			/// </summary>
			public bool Car;

			/// <summary>
			/// Bu kayıt gönderilsin mi? Ev/garaj radyoları her zaman; ara
			/// radyoları yalnızca takılıyken. Taktığın/söktüğün an yeniden
			/// hesaplanır, yani radyo taktığında karşı taraf da duyar.
			/// </summary>
			public bool Active;
		}

		static readonly List<Binding> bindings = new List<Binding>(48);
		static readonly Dictionary<string, Binding> byKey
			= new Dictionary<string, Binding>(StringComparer.Ordinal);

		static float nextPoll;
		static float nextBeat;
		static bool discovered;

		// ------------------------------------------------------------------
		// Telemetri
		// ------------------------------------------------------------------

		public static int BindingCount { get { return bindings.Count; } }
		public static int SentUpdates;
		public static int AppliedUpdates;

		public static int SuppressedIdentity;

		static bool IsCdIdentity(Binding b) {
			try {
				if (b == null || string.IsNullOrEmpty(b.VarName)) return false;
				if (b.Path == null || b.Path.IndexOf("CDPlayer", StringComparison.OrdinalIgnoreCase) < 0) return false;
				return string.Equals(b.VarName, "CurrentCD", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(b.VarName, "Index", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(b.VarName, "CurrentTrack", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(b.VarName, "MaxTracks", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(b.VarName, "Text", StringComparison.OrdinalIgnoreCase);
			}
			catch { return false; }
		}

		static bool LocalCdPresent() {
			try {
				PlayMakerFSM upd = FsmFor("CDPlayer", "Update");
				FsmString c = upd != null ? FsmNames.Str(upd, "CurrentCD") : null;
				return c != null && !string.IsNullOrEmpty(c.Value);
			}
			catch { return false; }
		}

		static bool LocalPlayingCd() {
			try {
				PlayMakerFSM cdUpd = FsmFor("CDPlayer", "Update");
				if (cdUpd == null || cdUpd.Fsm == null) return false;
				string st = cdUpd.Fsm.ActiveStateName;
				if (st != null && (st.IndexOf("Play track", StringComparison.OrdinalIgnoreCase) >= 0
					|| string.Equals(st, "State 6", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(st, "State 7", StringComparison.OrdinalIgnoreCase))) return true;
				AudioSource[] srcs = cdUpd.gameObject.GetComponentsInChildren<AudioSource>(true);
				for (int k = 0; srcs != null && k < srcs.Length; k++) {
					if (srcs[k] == null || srcs[k].clip == null) continue;
					if (srcs[k].isPlaying && srcs[k].mute == false) return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>Gönderilen radyo/CD olayı sayısı (teşhis).</summary>
		public static int SentEvents;

		/// <summary>Karşı tarafta oynatılan olay sayısı (teşhis).</summary>
		public static int AppliedEvents;

		// ------------------------------------------------------------------
		// Kesif
		// ------------------------------------------------------------------

		/// <summary>
		/// CD/radyo nesnelerini bulur ve "track / playing / volume" rolunu
		/// ustlenen degiskenleri baglar.
		///
		/// Degisken SECIMI: gercek adlara gore. Logdan cikarilan isimlerle
		/// eslesenler baglanir. Bu oyunun kendi sozlugunden gelen adlar;
		/// ad tutmuyorsa nesne atlanir ve 'smp radio' bunu bildirir.
		/// </summary>
		public static void Discover() {
			Clear();
			if (SatsumaRegistry.Root == null) return;

			GameObject[] all = null;
			try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("FindObjectsOfTypeAll failed: " + e.Message); }
			if (all == null) return;

			int skipped = 0;
			int carOnly = 0;
			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null) continue;
				string n = go.name;
				// G15-G18: jenerik adlar icin Wanted() nesnenin YOLUNA bakar.
				wantedObject = go;
				if (!Wanted(n)) continue;
				// SATSUMA'NIN İÇİNDEKİ CD: varsayılan olarak DAHİL.
				//
				// Kullanıcı tespiti: "cd player satsumada takılı değildir". Doğru,
				// ama bu build'de sahnedeki tek CD adayı da bu. Kötü tarafı şu:
				// iki tarafta bu nesne farklı durumdaysa kayıt eşleşmez ve paketin
				// tamamı reddedilir (canlı log: "radio packet rejected ...
				// EndOfStreamException"). O hata ayrıca düzeltildi; akış artık
				// bozulmuyor. 'smp radio no-car' ile kapatılabilir.
				if (UnderSatsuma(go)) {
					carOnly++;
					if (!IncludeSatsuma) { skipped++; continue; }
				}
				ScanObject(go);
			}

			discovered = true;
			SmpLog.Log("radio: bound " + bindings.Count
				+ " FSM variable(s) across " + CD_OBJECTS.Length + " CD and "
				+ RADIO_OBJECTS.Length + " radio candidate object(s)."
				+ (skipped > 0 ? " Skipped " + skipped + " Satsuma-internal radio/CD object(s)."
					+ " Run 'smp radio all' to include them."
					: carOnly > 0 ? " " + carOnly + " of them belong to the Satsuma"
						+ " (its CD is not installed, so their values are static)."
						+ " Run 'smp radio no-car' to skip them." : ""));
		}

		/// <summary>
		/// Satsuma'nın kendi CD/radyo nesneleri de senkronlansın mı?
		///
		/// CANLI KANIT (21:44:43): bu build'de sahnedeki TEK CD/radyo adayı
		/// zaten Satsuma'nınki ("radio: bound 0 ... Skipped 2"). Filtre
		/// varsayılan olduğu için radyo tamamen sustu. Bu yüzden varsayılan
		/// DAHİL, 'smp radio no-car' ile kapatılır.
		/// </summary>
		public static bool IncludeSatsuma = true;

		/// <summary>Filtreyi değiştirip keşfi yeniden çalıştırır.</summary>
		public static void SetIncludeSatsuma(bool include) {
			if (IncludeSatsuma == include) return;
			IncludeSatsuma = include;
			Discover();
		}

		/// <summary>Satsuma kökünün altında mı? (Opsiyonel ekipman: takılı değilse gereksiz.)</summary>
		static bool UnderSatsuma(GameObject go) {
			if (go == null || SatsumaRegistry.Root == null) return false;
			Transform t = null;
			try { t = go.transform; } catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("UnderSatsuma go.transform failed: " + e.Message); return false; }
			int guard = 0;
			while (t != null && guard++ < 64) {
				if (t.gameObject == SatsumaRegistry.Root) return true;
				t = t.parent;
			}
			return false;
		}

		static bool Wanted(string n) {
			// G15-G18: jenerik adlar (Power/Channel/Volume/Open/Speaker/Play)
			// butun dunyada var; yol filtresi olmadan HEPSINI baglamak
			// radyoyla iliskisiz yuzlerce nesneyi kapsar.
			if (GenericName(n) && !GenericAllowed(wantedObject)) return false;
			for (int i = 0; i < CD_OBJECTS.Length; i++)
				if (string.Equals(n, CD_OBJECTS[i], StringComparison.OrdinalIgnoreCase)) return true;
			for (int i = 0; i < RADIO_OBJECTS.Length; i++)
				if (string.Equals(n, RADIO_OBJECTS[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		static void ScanObject(GameObject go) {
			string path = SceneDump.PathOf(go.transform);
			if (string.IsNullOrEmpty(path)) return;
			bool car = UnderSatsuma(go);
			
			// SATSUMA'NIN KENDİ RADYOSU/ÇALARI: takılabilir parça DEĞİL.
			// `InstalledOfObject` takılabilir parça için çalışır; yerleşik radyo
			// için her zaman false döner. Başlangıçta `Active = true` yapıp,
			// `RefreshCarActivity` real-time olarak CD takılı/oyuncu araçta mı
			// kontrol edip `Active` günceller.
			bool carActive = car;  // Satsuma'nın kendi radyosu için başlangıçta aktif

			PlayMakerFSM[] fsms;
			try { fsms = go.GetComponentsInChildren<PlayMakerFSM>(true); }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("GetComponentsInChildren<PlayMakerFSM> failed: " + e.Message); return; }
			if (fsms == null) return;

			for (int f = 0; f < fsms.Length; f++) {
				PlayMakerFSM fsm = fsms[f];
				if (fsm == null || fsm.FsmVariables == null) continue;

				FsmInt[] ints = fsm.FsmVariables.IntVariables;
				if (ints != null) {
					for (int i = 0; i < ints.Length; i++) {
						FsmInt v = ints[i];
						if (v == null) continue;
						if (!MatchesTarget(go.name, fsm.Fsm.Name, v.Name)) continue;
						Add(new Binding {
							Path = path, Fsm = fsm, IntVar = v, VarName = v.Name,
							IntValue = v.Value, HasBaseline = false, Car = car, Active = (!car || carActive),
						});
					}
				}

				FsmFloat[] floats = fsm.FsmVariables.FloatVariables;
				if (floats != null) {
					for (int i = 0; i < floats.Length; i++) {
						FsmFloat v = floats[i];
						if (v == null) continue;
						if (!MatchesTarget(go.name, fsm.Fsm.Name, v.Name)) continue;
						Add(new Binding {
							Path = path, Fsm = fsm, FloatVar = v, VarName = v.Name,
							FloatValue = v.Value, Car = car, Active = (!car || carActive),
						});
					}
				}

				FsmBool[] bools = fsm.FsmVariables.BoolVariables;
				if (bools != null) {
					for (int i = 0; i < bools.Length; i++) {
						FsmBool v = bools[i];
						if (v == null) continue;
						if (!MatchesTarget(go.name, fsm.Fsm.Name, v.Name)) continue;
						Add(new Binding {
							Path = path, Fsm = fsm, BoolVar = v, VarName = v.Name,
							BoolValue = v.Value, Car = car, Active = (!car || carActive),
						});
					}
				}

				FsmString[] strings = fsm.FsmVariables.StringVariables;
				if (strings != null) {
					for (int i = 0; i < strings.Length; i++) {
						FsmString v = strings[i];
						if (v == null) continue;
						if (!MatchesTarget(go.name, fsm.Fsm.Name, v.Name)) continue;
						Add(new Binding {
							Path = path, Fsm = fsm, StringVar = v, VarName = v.Name,
							StringValue = v.Value, Car = car, Active = (!car || carActive),
						});
					}
				}
			}
		}

		static bool MatchesTarget(string obj, string fsm, string var) {
			for (int i = 0; i < TARGET_VARS.Length; i++) {
				if (string.Equals(obj, TARGET_VARS[i].obj, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(fsm, TARGET_VARS[i].fsm, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(var, TARGET_VARS[i].var, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		static void Add(Binding b) {
			string key = b.Path + "/" + b.VarName;
			if (byKey.ContainsKey(key)) return;
			bindings.Add(b);
			byKey[key] = b;
		}

		/// <summary>Dunya cikisinda temizle.</summary>
		public static void Clear() {
			bindings.Clear();
			byKey.Clear();
			carActiveByPath.Clear();
			lock (ioEventLock) { eventQueue.Clear(); }
			discovered = false;
		}

		// ------------------------------------------------------------------
		// Kare basina
		// ------------------------------------------------------------------

		/// <summary>Degisiklik varsa aninda, yoksa 1 Hz kalp atisi gonderir.</summary>
		public static void Tick() {
			try {
				if (!discovered) return;
				float now = Time.time;
				if (now < nextPoll) return;
				nextPoll = now + POLL_INTERVAL;

				// ARAC RADYOSU AKTİFLİĞİ: BAĞLANTIDAN BAĞIMSIZ.
				//
				// Kullanıcı bilgisi: CD player ve diğer Satsuma radyosu araca
				// takılınca aktif oluyor, takılmayınca garajda duruyor. Yani
				// "hangi nesne sende çalıyor" sorusunun cevabı oyunun montaj
				// durumunda. Radyo taktığında karşı tarafın da duyması için bu
				// yeniden hesap BAĞLANTI KURULMU OLSA BİLE yapılmalı; ayrıca tek
				// başına test ederken de görünür olsun diye.
				if (RefreshCarActivity()) {
					nextBeat = 0f;   // değişiklik zaten var
				}

			// TESPIT + GÖZLEM bağlantıdan BAĞIMSIZ.
			//
			// CANLI KANIT: eski kod `if (!SteamTransport.HasRemote) return;`
			// satırını kirli-döngüden ÖNCE yazıyordu. Sonuçlar:
			//   1. Tek başına oynarken radyo değişimi ASLA loglanmıyordu
			//      ("radyoyu açtım, log yok" belirtisi).
			//   2. Temel değer yalnızca Flush() içinde güncelleniyordu; bağlantı
			//      olmayınca güncellenmiyordı. Peer bağlandığında TÜM değişkenler
			//      "değişti" sayıldığı için ilk karede FLOOD gidiyordu.
			//
			// DOĞRU BÖLME — kapı Tick() ile aynı ilke: gözle her zaman,
			// gönder yalnızca bağlantı varken.
			bool dirty = false;
			for (int i = 0; i < bindings.Count; i++) {
				Binding b = bindings[i];
				if (!b.Active) continue;
				string desc = null, oldVal = null, newVal = null;
				if (b.IntVar != null && b.IntVar.Value != b.IntValue) { dirty = true; desc = "int"; oldVal = b.IntValue.ToString(); newVal = b.IntVar.Value.ToString(); }
				else if (b.BoolVar != null && b.BoolVar.Value != b.BoolValue) { dirty = true; desc = "bool"; oldVal = b.BoolValue.ToString(); newVal = b.BoolVar.Value.ToString(); }
				else if (b.FloatVar != null && Mathf.Abs(b.FloatVar.Value - b.FloatValue) > 0.005f) { dirty = true; desc = "float"; oldVal = b.FloatValue.ToString("F2"); newVal = b.FloatVar.Value.ToString("F2"); }
				else if (b.StringVar != null && b.StringVar.Value != b.StringValue) { dirty = true; desc = "string"; oldVal = b.StringValue; newVal = b.StringVar.Value; }
				else if (!b.HasBaseline) dirty = true;

				// v15: ses seviyesi kaydirilmasi her adimda bir satir ve
				// bir paket uretiyordu (canli log 21:38: 5 satir). Ustelik
				// `desc` atamasi yalnizca LOG icin; `dirty` zaten ayri
				// tutuluyor, yani paket gonderimi etkilenmiyor.
				if (desc != null && Telemetry.Verbose) {
					SmpTrace.Act("RADIO", desc, b.VarName + " " + oldVal + " -> " + newVal + "  @ " + b.Path);
				}
			}

			// Temel değer her zaman güncellenir: buna bakmadan "değişti mi"
			// kararı yapılamaz ve peer bağlanınca flood önlenir.
			ObserveBaselines();

			// JIP kenari kopukken de izlenmeli (yoksa ilk baglanti gorulmez).
			try { CdDiscSync.Tick(); } catch { }

			if (!SteamTransport.HasRemote) return;

			// OLAY gönderimi: değişken yolundan AYRI. Ses açma/kapatma ve
			// kanal değiştirme değişkene yazılmaz (oyun AudioSource.mute
			// ve EnableFSM kullanır), bu yüzden olay da iletilir.
			// Saniyede en fazla bir paket; olaylar kuyrukta birleşir.
			bool hasEvents;
			lock (ioEventLock) { hasEvents = eventQueue.Count > 0; }
			if (hasEvents && now >= nextEventSend) {
				nextEventSend = now + 0.25f;
				FlushEvents();
			}

			// ÇALMA SENKRONU: "şu an ne çalıyor" durumu. Değişken senkronu
			// yetmiyor — karşı tarafta oyunun FSM'i yürümeli (AudioPlay).
			CaptureAndSendPlay();

			bool beat = now >= nextBeat;
			if (!dirty && !beat && !hasEvents) return;
			if (beat) nextBeat = now + HEARTBEAT;
			Flush();
			}
			catch (Exception e) {
				SmpLog.Warning("radio tick failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// Nesne BAŞINA araç aktivitesi. Binding başına hesaplanırsa "CD takılı
		/// mı" bilgisi sadece `Installed` değişkenine sahip binding'e gider;
		/// CurrentTrack/Index/CurrentCD/Text/Volume gibi diğer 9 değişken `else`
		/// dalına düşüp `now = b.Active` ile kalıcı false alır ve hiç gönderilmez
		/// (logdaki 10 kez "is now INACTIVE" tam olarak bu).
		///
		/// Anahtar nesne yolu; değer o nesnenin son bilinen aktifliği.
		/// </summary>
		static readonly Dictionary<string, bool> carActiveByPath
			= new Dictionary<string, bool>(StringComparer.Ordinal);

		/// <summary>
		/// Ara radyolarının hangisinin takılı olduğunu yeniden hesaplar.
		/// </summary>
		/// <returns>Durum değiştiyse true.</returns>
		static bool RefreshCarActivity() {
			bool changed = false;

			// Tek geçiş: nesne yolu başına bir kez karar ver.
			bool satsumaReady = false;
			if (SatsumaRegistry.Root != null && LocalPlayerState.InVehicle) {
				var localVehicle = VehicleRegistry.FindLocalVehicle();
				satsumaReady = localVehicle != null
					&& localVehicle.VehicleName.StartsWith("SATSUMA", StringComparison.OrdinalIgnoreCase);
			}

			for (int i = 0; i < bindings.Count; i++) {
				Binding b = bindings[i];
				if (!b.Car) continue;

				// Bu yolda "Installed" değişkeni var mı? Varsa tek doğruluk odur.
				bool installedKnown = false;
				bool installed = false;
				for (int k = 0; k < bindings.Count; k++) {
					Binding o = bindings[k];
					if (!o.Car || !string.Equals(o.Path, b.Path, StringComparison.Ordinal)) continue;
					if (o.BoolVar != null && string.Equals(o.VarName, "Installed", StringComparison.OrdinalIgnoreCase)) {
						installedKnown = true;
						installed = o.BoolVar.Value;
						break;
					}
				}

				bool was;
				carActiveByPath.TryGetValue(b.Path, out was);

				// Oyuncu Satsuma'da değilse ara radyoları sessizdir; oyuncu
				// Satsuma'daysa ya CD takılıysa ya da yol gömülü (Installed yok).
				bool now = satsumaReady && (installedKnown ? installed : true);

				bool pathChanged = now != was;
				if (pathChanged) carActiveByPath[b.Path] = now;

				if (now != b.Active) {
					b.Active = now;
					b.HasBaseline = false;
					// Nesne başına TEK log satırı: aynı nesnedeki 10 değişken
					// için 10 satır yazmak gürültüydü.
					if (pathChanged) {
						changed = true;
						// v15: canli log kaniti (21:38, 345 satirin 10'u) --
						// araca her giris/cikis 2 satir yaziyordu. Durum
						// gecisi, hata degil. Sadece Telemetry.Verbose.
						if (Telemetry.Verbose)
						SmpLog.Log("radio: '" + b.Path + "' is now " + (now ? "ACTIVE" : "INACTIVE")
							+ " (" + (installedKnown ? (installed ? "CD installed" : "CD not installed")
								: "built-in") + ", player in Satsuma: " + satsumaReady + "), "
							+ (now ? "sending its state." : "not sending."));
					}
				}
			}
			return changed;
		}

		/// <summary>Yoldan nesne çözer (sadece ara radyoları için, seyrek çağrılır).</summary>
		static GameObject ResolvePathObject(string path) {
			if (string.IsNullOrEmpty(path)) return null;
			GameObject root = SatsumaRegistry.Root;
			if (root == null) return null;
			string prefix = root.name + "/";
			if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
			string rest = path.Substring(prefix.Length);
			Transform t;
			try { t = root.transform.Find(rest); }
			catch (Exception e) { if (Telemetry.Verbose) SmpLog.Warning("root.transform.Find failed: " + e.Message); return null; }
			return t != null ? t.gameObject : null;
		}

		static void ObserveBaselines() {
			// Referans değerleri güncelle: buna bakmadan "değişti mi"
			// kararı yapamayız. Send YAPMAZ — yalnızca gözlemi tüketir.
			for (int i = 0; i < bindings.Count; i++) {
				Binding b = bindings[i];
				if (!b.Active) continue;
				if (b.IntVar != null) b.IntValue = b.IntVar.Value;
				else if (b.BoolVar != null) b.BoolValue = b.BoolVar.Value;
				else if (b.FloatVar != null) b.FloatValue = b.FloatVar.Value;
				else if (b.StringVar != null) b.StringValue = b.StringVar.Value;
				b.HasBaseline = true;
			}
		}

		static void Flush() {
			try {
				// Sayım ÖNCE yazılmalı: kayıt listesi Active olanlardan ibaret.
				int active = 0;
				for (int i = 0; i < bindings.Count; i++) {
					if (bindings[i].Active) active++;
				}
				if (active == 0) return;
				MemoryStream s = Protocol.Begin(Protocol.Msg.RadioState);
				BinaryWriter w = new BinaryWriter(s);
				w.Write((byte)Math.Min(active, 255));
				int written = 0;
				for (int i = 0; i < bindings.Count && written < 255; i++) {
					Binding b = bindings[i];
					if (!b.Active) continue;
					w.Write(b.Path);
					w.Write(b.VarName);
					byte kind = b.IntVar != null ? (byte)0 : (b.BoolVar != null ? (byte)1 : (b.FloatVar != null ? (byte)2 : (byte)3));
					w.Write(kind);
					if (kind == 0) { w.Write(b.IntVar.Value); b.IntValue = b.IntVar.Value; }
					else if (kind == 1) { w.Write(b.BoolVar.Value); b.BoolValue = b.BoolVar.Value; }
					else if (kind == 2) { w.Write(b.FloatVar.Value); b.FloatValue = b.FloatVar.Value; }
					else { w.Write(b.StringVar.Value); b.StringValue = b.StringVar.Value; }
					b.HasBaseline = true;
					written++;
				}
				w.Flush();
				// Guvenilir: iki taraf farkli sarkida kalmak kotudur.
				if (written == 0) return;
				if (SteamTransport.SendReliable(Protocol.Finish(s))) SentUpdates += written;
			}
			catch (Exception e) {
				SmpLog.Warning("radio send failed: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Alma
		// ------------------------------------------------------------------

		/// <summary>Gelen radyo durumunu uygular.</summary>
		public static void OnMessage(BinaryReader r, byte count) {
			for (int i = 0; i < count; i++) {
				string path = "?";
				string varName = "?";
				byte kind = 255;
				try {
					path = r.ReadString();
					varName = r.ReadString();
					kind = r.ReadByte();

					Binding b;
					string key = path + "/" + varName;
					bool known = byKey.TryGetValue(key, out b) && b != null;
					// KRİTİK: kayıt BİTMİŞ OLMALI. Tanimli olmayan bir anahtarda
					// `continue` deger byte'ları akışta bırakıyordu; sonraki
					// kayıtlar çöp okuyor ve paketin sonunda
					// EndOfStreamException patlıyordu (canlı log kanıtı: saniyede
					// bir "radio packet rejected at record 7/10 ... 255").
					// Tarafların tabloları farklıysa (farklı kayıt) bu yol
					// KAÇINILMAZ; tek çözüm değeri okuyup atmak.
					if (!known) {
						SkipValue(r, kind);
						continue;
					}

					EchoGuard.Scope scope = EchoGuard.Enter();
					try {
						// DEĞER HER ZAMAN OKUNUR; yazma kararı ayrı.
						//
						// Karşı tarafın arabasında radyo takılı, bizimki sökük
						// olabilir (farklı kayıtlar). Değer yine de tüketilmeli,
						// yoksa akış yine kayar. Yazma yalnızca YERELDE de
						// takılıysa yapılır.
												// CD KIMLIK KORUMASI: diski olmayan tarafin bos degeri diski olani ezemez.
						// Iki ayri araba ayni yollari paylasir; gercek cikarma CdDisc ile gelir.
bool write = b.Active;
						if (kind == 0) {
							int v = r.ReadInt32();
														if (v == 0 && IsCdIdentity(b) && LocalCdPresent()) { SuppressedIdentity++; continue; }
if (write && b.IntVar != null) { b.IntVar.Value = v; b.IntValue = v; }
						}
						else if (kind == 1) {
							bool v = r.ReadBoolean();
							if (write && b.BoolVar != null) { b.BoolVar.Value = v; b.BoolValue = v; }
						}
						else if (kind == 2) {
							float v = r.ReadSingle();
							if (write && b.FloatVar != null) { b.FloatVar.Value = v; b.FloatValue = v; }
						}
						else {
							string v = r.ReadString();
														if ((v == null || v.Length == 0) && IsCdIdentity(b) && LocalCdPresent()) { SuppressedIdentity++; continue; }
if (write && b.StringVar != null) { b.StringVar.Value = v; b.StringValue = v; }
						}
					}
					finally { scope.Dispose(); }
					AppliedUpdates++;
				}
				catch (Exception e) {
					Telemetry.BadPackets++;
					SmpLog.Warning("radio packet rejected at record " + i + "/" + count
						+ " key '" + path + "/" + varName + "' kind " + kind
						+ ": " + e.GetType().Name + ": " + e.Message);
					return;
				}
			}
		}

		/// <summary>
		/// Tanımsız bir kaydın değerini okuyup atar. Kayıt, isim eşleşmese de
		/// akıştan TAMAMEN tüketilmelidir; aksi halde sonraki kayıtlar kayar
		/// (bkz. OnMessage).
		/// </summary>
		static void SkipValue(BinaryReader r, byte kind) {
			switch (kind) {
				case 0: r.ReadInt32(); break;
				case 1: r.ReadBoolean(); break;
				case 2: r.ReadSingle(); break;
				default: r.ReadString(); break;
			}
		}

		// ------------------------------------------------------------------
		// OLAY SENKRONU (ses acma/kapatma, kanal degistirme, CD parca degistirme)
		// ------------------------------------------------------------------
		//
		// NEDEN AYRI BIR YOL GEREKIYOR
		// ----------------------------
		// Degisken senkronu (RadioState) "sesi kapatma"ni YAPAMAZ, cunku
		// oyun sesi bir DEGISKENE yazmadan kapatiyor:
		//
		//   RADIO/RadioChannels / Fetch / "Off" durumu:
		//     SetProperty(AudioSource.mute = true)   <- degisken degil, AKSIYON
		//     ActivateGameObject / SetParent / EnableFSM
		//     yani "sessiz" durumu HICBIR FsmFloat/FsmBool'a yazilmaz.
		//
		// Ayni sekilde kanal degisimi ve CD parca degisimi degiskene degil
		// OLAYA bagli:
		//   CDPlayer/Update : NEXT, PREVIOUS, PLAYLIST, PLAY CD, STOP CD,
		//                    RADIO_UPDATE, RADIO_FOLK, FOLK
		//   CDPlayer/Fetch  : FETCH, OFF, ON
		//   RadioChannels   : FETCH, OFF, Adjust
		//
		// Bu yuzden olaylar da iletilir: karsi tarafta ayni FSM ayni olayi
		// alir, oyunun KENDI zinciri (mute, EnableFSM, AudioPlay) calisir.
		// Boylece ses acma/kapatma ve kanal degistirme birebir ayni yoldan
		// gider; mod hiçbir AudioSource'a dokunmaz.
		//
		// GUVENLIK
		// -------
		// * Yanki: EchoGuard.Suppressed iken gonderilmez ve alirken de
		//   bastirilir, yoksa olay geri gelip sonsuz dongu yapar.
		// * Yalnizca BINDING'e sahip, AKTIF bir nesne icin gonderilir; boylece
		//   "Satsuma'da CD yokken basilan dugme" bos yere paket uretmez.
		// * Kuyruk: saniyede en fazla bir paket, olaylar birlestirilerek.

		/// <summary>
		/// Iletilecek radyo/CD olaylari. Buyuk/kucuk harf duyarsiz eslesir
		/// (oyun "OFF" ve "off" karisik yaziyor).
		/// </summary>
		static readonly string[] SYNC_EVENTS = {
			// ses acma / kapatma
			"OFF", "ON", "FETCH", "Adjust", "NO SOUND",
			// kanal degistirme
			"NEXT", "PREVIOUS", "PLAYLIST", "RADIO_UPDATE", "RADIO_FOLK", "FOLK",
			// CD calar
			"PLAY CD", "STOP CD", "PLAYLIST", "GLOBALEVENT",
			// ANTENNA degisimi: SpeakerBass/SpeakerDash Settings FSM'i
			// ANTENNA0/1/2 olaylariyla anten secimini degistiriyor
			// (asset kaniti: Settings olay listesi).
			"ANTENNA0", "ANTENNA1", "ANTENNA2",
			// CD/radyo dugmeleri: Knob FSM'i OFF/ON/FETCH/RADIO_UPDATE
			// gonderiyor; bunlar ayni zamanda degisken degil, olay.
			"CHANGE", "TOGGLE", "MODE",
		};

		/// <summary>Kuyruk: gonderilecek (yol, olay) ciftleri.</summary>
		static readonly Queue<string> eventQueue = new Queue<string>(8);
		static float nextEventSend;

		/// <summary>
		/// FSM olay kancasindan gelir (AssemblyPatch.OnSendEvent). Karsi
		/// tarafinin ayni olayi yurutmesi icin kuyruga alir.
		/// </summary>
		public static void OnFsmEvent(PlayMakerFSM fsm, string eventName) {
			if (fsm == null || string.IsNullOrEmpty(eventName)) return;
			if (!discovered) return;
			if (EchoGuard.Suppressed) return;      // yanki: gelen olayi geri gonderme
			if (!SteamTransport.HasRemote) return; // tek basina oynarken bosuna paket uretme

			// Kuyruk sinirli: olaylar saniyede bir araya toplanir, patlama olmaz.
			if (eventQueue.Count >= 16) return;

			if (!IsSyncEvent(eventName)) return;

			// Bu FSM seninkilerden biri mi? Degilse yolla (ornegin izleme
			// ekranindaki bir radio dugmesi).
			Binding owner = OwnerOf(fsm);
			if (owner == null) return;
			if (!owner.Active) return;

			try {
				lock (ioEventLock) {
					eventQueue.Enqueue(owner.Path);
					eventQueue.Enqueue(eventName.ToUpperInvariant());
				}
			}
			catch { }
		}

		static readonly object ioEventLock = new object();

		static bool IsSyncEvent(string name) {
			for (int i = 0; i < SYNC_EVENTS.Length; i++)
				if (string.Equals(SYNC_EVENTS[i], name, StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		/// <summary>
		/// Bu FSM'in ait oldugu binding (yol dondurur), yoksa null.
		///
		/// Once kimlik eslesmesi (Fsm referansi), sonra nesne+FSM adi.
		/// YEDEK ESLESME ONCEDEN CALISMIYORDU: `MatchesTarget` uc arguman
		/// istiyor (objeAdi, fsmAdi, degiskenAdi) ama eski kod sadece
		/// `fsm.gameObject.name` + `fsmName` + `b.VarName` ile bir kez
		/// deniyordu ve `fsm.gameObject.name` BOS olabiliyordu; sonuc
		/// hicbir zaman eslesmiyordu, yani Satsuma'daki dugmeler
		/// paket uretemiyordu.
		/// </summary>
		static Binding OwnerOf(PlayMakerFSM fsm) {
			if (fsm == null) return null;
			try {
				// 1) Kimlik: kesif sirasinda binding'e FSM'i yazdik.
				for (int i = 0; i < bindings.Count; i++) {
					if (bindings[i].Fsm == fsm) return bindings[i];
				}
				// 2) Nesne adi + FSM adi (sahne yeniden yuklendiginde
				//    referans degisir, ama adlar ayni kalir).
				string objName = null;
				try { if (fsm.gameObject != null) objName = fsm.gameObject.name; } catch { }
				if (string.IsNullOrEmpty(objName)) return null;
				string fsmName = null;
				try { if (fsm.Fsm != null) fsmName = fsm.Fsm.Name; } catch { }
				for (int i = 0; i < bindings.Count; i++) {
					Binding b = bindings[i];
					if (MatchesTarget(objName, fsmName, b.VarName)) return b;
				}
			}
			catch { }
			return null;
		}

		/// <summary>Kuyruktaki olaylari tek pakette gonderir (Tick icinden).</summary>
		static void FlushEvents() {
			string[] batch = null;
			int count = 0;
			try {
				lock (ioEventLock) {
					if (eventQueue.Count == 0) return;
					int pairs = eventQueue.Count / 2;
					if (pairs > 8) pairs = 8;
					batch = new string[pairs * 2];
					for (int i = 0; i < pairs * 2; i++) batch[i] = eventQueue.Dequeue();
					count = pairs;
				}
				if (batch == null || count == 0) return;

				MemoryStream s = Protocol.Begin(Protocol.Msg.RadioEvent);
				BinaryWriter w = new BinaryWriter(s);
				w.Write((byte)count);
				for (int i = 0; i < count; i++) {
					w.Write(batch[i * 2]);      // yol
					w.Write(batch[i * 2 + 1]);  // olay
				}
				w.Flush();
				if (SteamTransport.SendReliable(Protocol.Finish(s))) {
					SentEvents += count;
					SmpTrace.Act("RADIO", "event", count + " radio/CD event(s) sent");
				}
			}
			catch (Exception e) {
				SmpLog.Warning("radio event send failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>Gelen radyo/CD olaylarini ayni FSM'de uygular.</summary>
		public static void OnEventMessage(BinaryReader r) {
			byte count;
			try { count = r.ReadByte(); }
			catch { return; }
			for (int i = 0; i < count; i++) {
				string path, eventName;
				try {
					path = r.ReadString();
					eventName = r.ReadString();
				}
				catch (Exception e) {
					Telemetry.BadPackets++;
					SmpLog.Warning("radio event packet truncated at " + i + "/" + count
						+ ": " + e.GetType().Name);
					return;
				}
				ApplyEvent(path, eventName);
			}
		}

		/// <summary>Tek olayi hedef FSM'e iletir.</summary>
		static void ApplyEvent(string path, string eventName) {
			try {
				GameObject go = ResolveAnyPath(path);
				if (go == null) return;

				// Hangi FSM bu yolda? Binding'te saklanan FSM'i kullan;
				// yoksa nesne uzerindeki tum FSM'lerde olayi ara.
				PlayMakerFSM target = null;
				for (int i = 0; i < bindings.Count; i++) {
					Binding b = bindings[i];
					if (!string.Equals(b.Path, path, StringComparison.Ordinal)) continue;
					target = b.Fsm;
					break;
				}
				if (target == null) {
					PlayMakerFSM[] all = go.GetComponentsInChildren<PlayMakerFSM>(true);
					for (int i = 0; all != null && i < all.Length; i++) {
						if (all[i] != null && FsmNames.HasEvent(all[i], eventName)) { target = all[i]; break; }
					}
				}
				if (target == null) return;

				// Yanki: yerelde yeniden uretilen olay geri gonderilmesin.
				EchoGuard.Scope scope = EchoGuard.Enter();
				try {
					target.SendEvent(eventName);
					AppliedEvents++;
					SmpTrace.Act("RADIO", "event", "replayed '" + eventName + "'  @ " + path);
				}
				finally { scope.Dispose(); }
			}
			catch (Exception e) {
				SmpLog.Warning("radio event apply failed (" + path + " / " + eventName
					+ "): " + e.GetType().Name);
			}
		}

		/// <summary>
		/// Yolu nesneye cözer. RADIO/RadioChannels Satsuma'nın altında
		/// DEĞİLDİR, bu yüzden sadece Satsuma köküne bakan ResolvePathObject
		/// yetmez; sahne köklerinde de aranır.
		/// </summary>
		static GameObject ResolveAnyPath(string path) {
			if (string.IsNullOrEmpty(path)) return null;
			GameObject direct = ResolvePathObject(path);
			if (direct != null) return direct;

			GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
			if (all == null) return null;
			for (int i = 0; i < all.Length; i++) {
				GameObject go = all[i];
				if (go == null || go.hideFlags != HideFlags.None) continue;
				if (go.name != path) continue;
				return go;
			}
			return null;
		}

		/// <summary>Baslangic durumu (istemci katilinca).</summary>
		public static void SendAll() {
			try {
				if (!discovered || !SteamTransport.HasRemote) return;
				RefreshCarActivity();
				Flush();
			}
			catch (Exception e) {
				SmpLog.Warning("radio initial sync failed: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		// Tani
		// ------------------------------------------------------------------

		/// <summary>Su anki yerel durumu okur (konsol icin).</summary>
		public static string Describe() {
			if (!discovered) return "radio: not scanned";
			System.Text.StringBuilder sb = new System.Text.StringBuilder(160);
			sb.Append("radio: ").Append(bindings.Count).Append(" variable(s), sent ")
			  .Append(SentUpdates).Append(", applied ").Append(AppliedUpdates);
			if (bindings.Count == 0) {
				sb.Append(" - NO variable bound. The game's FSM variable names differ; run 'smp fsm CDPlayer' to see the real names.");
			}
			else {
				int active = 0, car = 0, carActive = 0;
				for (int i = 0; i < bindings.Count; i++) {
					if (bindings[i].Active) active++;
					if (bindings[i].Car) {
						car++;
						if (bindings[i].Active) carActive++;
					}
				}
				sb.Append(", sending ").Append(active);
				if (car > 0) {
					// Kullanıcı bilgisi: "cdplayer ve diğer satsuma radyosu araca
					// takılınca aktif oluyor, garajda duruyor". Yalnızca ARAYA
					// TAKILAN radyo gönderilir; yedek olan akışta yer almaz.
					sb.Append(", car radio ").Append(carActive).Append("/").Append(car)
					  .Append(" installed ('smp radio all' = include the spare too)");
				}
				sb.Append(" [");
				for (int i = 0; i < bindings.Count && i < 6; i++) {
					if (i > 0) sb.Append(", ");
					sb.Append(bindings[i].VarName);
				}
				sb.Append("]");
			// YOLLAR: "bu radyo hangi nesneye bağlıydı?" sorusunu logda
			// cevaplamak için. Önceden yalnızca DEĞİŞKEN adı yazılıyordu,
			// yani "minibüsün radyosu bağlandı mı" belirtisi logdan
			// anlaşılamıyordu. Yol boşsa nesne silinmiş olabilir, "?"
			// yazılır.
			System.Text.StringBuilder paths = new System.Text.StringBuilder(160);
			int shownPaths = 0;
			for (int i = 0; i < bindings.Count && shownPaths < 8; i++) {
				string bp = null;
				try { bp = bindings[i].Path; } catch { }
				if (string.IsNullOrEmpty(bp)) bp = "?";
				if (paths.Length > 0) paths.Append(", ");
				paths.Append(bp);
				shownPaths++;
			}
			if (bindings.Count > shownPaths) {
				paths.Append(", +").Append(bindings.Count - shownPaths);
			}
			sb.Append(" paths={").Append(paths).Append("}");
			// ÇALMA SENKRONU: "şu an ne çalıyor" paketi sayacı. Değişken
			// senkronu tek başına yetmez; karşı tarafta oyunun FSM'i
			// yürümeli (AudioPlay). Bu satır olmadan "ses neden gitmiyor"
			// sorusunun cevabı logda yoktu.
			sb.Append(" play sent=").Append(SentPlayPackets)
				.Append(" applied=").Append(AppliedPlayPackets)
				.Append(" last=").Append(LastPlayState);
			// CD SES AKIŞI: karşı tarafta .ogg yoksa PCM aktarımı.
			sb.Append("\n         ").Append(CdAudioStream.Describe());
			}
			return sb.ToString();
		}
// ------------------------------------------------------------------
		// ÇALMA SENKRONU  (RadioPlay = 47)
		//
		// NEDEN AYRI BİR PAKET — canlı log kanıtı:
		//   11:28:41.695 cd import: DONE - CD'S IMPORTED   (clip yüklendi)
		//   … [SatsumaFSM] 'Update' -> 'State 1' @ SpeakerBass/CDPlayer
		//
		// `CurrentCD` / `Index` değişkenleri zaten RadioState (41) ile
		// gidiyor — TARGET_VARS'ta kayıtlı. AMA karşı tarafta
		// `CDPlayer/Update` "State 1"de BEKLİYORSA oyun AudioPlay'i
		// çalıştırmaz; değişken yazmak sesi BAŞLATMAZ.
		//
		// Aynı şey radyo için de geçerli: kanal bool'u yazılır ama
		// `RadioChannels/Fetch` "Off" durumunda AudioSource.mute yapıyorsa
		// ses çıkmaz (log: "RADIO float Volume 0.00 -> 0.10").
		//
		// Çözüm: oynayan taraf "şu an CD1 parça 3 çalıyor" / "kanal Folk,
		// ses 0.7" durumunu gönderir; alan taraf değişkenleri yazıp oyunun
		// KENDİ olayını (PLAY CD / RADIO_UPDATE / RADIO_FOLK) EchoGuard
		// içinde yeniden üretir. Böylece karşı taraftaki oyun motoru aynı
		// AudioClip'i kendi AudioSource'unda çalar — ses aktarımı YOK,
		// her iki oyuncu da aynı dosyayı yerel olarak çalar.
		//
		// ASSET (l2/fsms.json):
		//   CDPlayer/Update (SATSUMA/Electricity/SpeakerBass/CDPlayer)
		//     ArrayListGet(SongDatabaseCD, reference=CurrentCD, atIndex=Audio)
		//     ArrayListCount(SongDatabaseCD, CurrentCD)
		//     IntClamp + IntOperator + IntCompare + IntAdd
		//     SetAudioClip + AudioPlay + AudioStop + Wait
		//     olaylar: DOESNOTEXISTS EXISTS FAIL FOLK GLOBALEVENT LOOP NEXT
		//             "PLAY CD" PLAYLIST PREVIOUS RADIO_FOLK RADIO_UPDATE
		//             SAVEGAME "STOP CD"
		//   RadioChannels/Fetch (RADIO/RadioChannels)
		//     SetAudioVolume x4 + SetProperty(mute) + ActivateGameObject
		//     olaylar: FETCH, OFF, Adjust
		//   Folk/Update (RADIO/RadioChannels/Folk)
		//     SetAudioClip(RadioOnFolk) + AudioPlay + Wait
		//     olaylar: DOESNOTEXISTS EXISTS RADIO_FOLK RADIO_UPDATE SAVEGAME
		// ------------------------------------------------------------------

		/// <summary>
		/// Binding tablosundan (obje, fsm) ile FSM referansı bulur.
		///
		/// DIŞARI AÇIK: `CdAudioStream` de aynı referansı kullanıyor —
		/// çalan clip'i `CDPlayer/Update` üzerindeki `Audio` değişkeninden
		/// okuyor, alan tarafta `CDPlayer/Fetch`'in `Volume`/`db_Required`
		/// değerlerini uyguluyor.
		/// </summary>
		public static PlayMakerFSM FindFsm(string obj, string fsm) { return FsmFor(obj, fsm); }

		/// <summary>Binding tablosundan (obje, fsm) ile FSM referansı bulur.</summary>
		static PlayMakerFSM FsmFor(string obj, string fsm) {
			for (int i = 0; i < bindings.Count; i++) {
				Binding b = bindings[i];
				if (b.Fsm == null) continue;
				string on = null, fn = null;
				try { if (b.Fsm.gameObject != null) on = b.Fsm.gameObject.name; } catch { }
				try { if (b.Fsm.Fsm != null) fn = b.Fsm.Fsm.Name; } catch { }
				if (string.Equals(on, obj, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(fn, fsm, StringComparison.OrdinalIgnoreCase)) return b.Fsm;
			}
			return null;
		}

		// --- gönderilmiş son durum (spam olmasın diye)
		static int sentTrack = -1;
		static string sentCdName = "";
		static bool sentPlaying;
		static bool sentCdMode;
		static bool sentCh1, sentFolk, sentStatic;
		static float sentVolume = -1f;
		static float nextPlaySend;

		/// <summary>Gönderilen çalma paketi sayısı (teşhis).</summary>
		public static int SentPlayPackets;

		/// <summary>Uygulanan çalma paketi sayısı (teşhis).</summary>
		public static int AppliedPlayPackets;

		/// <summary>Son gönderilen çalma durumu (teşhis / 'smp radio').</summary>
		public static string LastPlayState = "(none)";

		/// <summary>
		/// Yerelde ne çalıyor? Okur, değiştiyse gönderir.
		/// Tick() içinden ~5 Hz çağrılır.
		/// </summary>
		static void CaptureAndSendPlay() {
			if (!SteamTransport.HasRemote) return;
			float now = Time.time;
			if (now < nextPlaySend) return;
			nextPlaySend = now + 0.2f;

			try {
				// --- CD: CDPlayer/Update
				PlayMakerFSM cdUpd = FsmFor("CDPlayer", "Update");
				string cdName = "";
				int cdIdx = -1;
				bool playing = false;
				bool cdMode = false;
				if (cdUpd != null) {
					FsmString cdStr = FsmNames.Str(cdUpd, "CurrentCD");
					if (cdStr != null && !string.IsNullOrEmpty(cdStr.Value)) cdName = cdStr.Value;
					FsmInt ix = FsmNames.Int(cdUpd, "Index");
					if (ix != null) cdIdx = ix.Value;

					// Asset: "Play track" → AudioPlay; "State 6" NEXT döngüsü;
					// "State 7" IntAdd. Bunlar çalma durumları.
					string st = (cdUpd.Fsm != null) ? cdUpd.Fsm.ActiveStateName : "";
					playing = st.IndexOf("Play track", StringComparison.OrdinalIgnoreCase) >= 0
						|| string.Equals(st, "State 6", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(st, "State 7", StringComparison.OrdinalIgnoreCase);

					// cdMode: CDPlayer altında çalan AudioSource var mı?
					// Anahtar FSM'i (RadioCDSwitch) senkron değişkene
					// yazmıyor; bu ölçülebilir tek gösterge.
					AudioSource[] srcs = cdUpd.gameObject.GetComponentsInChildren<AudioSource>(true);
					for (int k = 0; srcs != null && k < srcs.Length; k++) {
						if (srcs[k] == null || srcs[k].clip == null) continue;
						if (srcs[k].isPlaying && srcs[k].mute == false) { cdMode = true; break; }
					}
					if (!cdMode) cdMode = playing;
				}

				// --- Radyo: RadioChannels/Fetch
				PlayMakerFSM rc = FsmFor("RadioChannels", "Fetch");
				bool ch1 = false, folk = false, sta = false;
				float vol = -1f;
				if (rc != null) {
					FsmBool b1 = FsmNames.Bool(rc, "Channel1");
					FsmBool b2 = FsmNames.Bool(rc, "Folk");
					FsmBool b3 = FsmNames.Bool(rc, "Static");
					FsmFloat fv = FsmNames.Float(rc, "Volume");
					if (b1 != null) ch1 = b1.Value;
					if (b2 != null) folk = b2.Value;
					if (b3 != null) sta = b3.Value;
					if (fv != null) vol = fv.Value;
				}

				bool cdChanged = (cdIdx != sentTrack) || (cdName != sentCdName)
					|| (playing != sentPlaying) || (cdMode != sentCdMode);
				bool radioChanged = (ch1 != sentCh1) || (folk != sentFolk)
					|| (sta != sentStatic)
					|| (sentVolume >= 0f && Mathf.Abs(vol - sentVolume) > 0.005f);
				if (!cdChanged && !radioChanged) return;

				sentTrack = cdIdx; sentCdName = cdName; sentPlaying = playing;
				sentCdMode = cdMode;
				sentCh1 = ch1; sentFolk = folk; sentStatic = sta; sentVolume = vol;

				MemoryStream s = Protocol.Begin(Protocol.Msg.RadioPlay);
				BinaryWriter w = new BinaryWriter(s);
				w.Write((byte)(cdChanged ? 0 : 1));   // kind
				w.Write(cdName);
				w.Write(cdIdx);
				w.Write(playing ? 1 : 0);
				// cdMode: CD player'da GERÇEKTEN çalan bir clip var mı.
				//
				// NEDEN AYRI BAYRAK — asset (l2) `RadioCDSwitch/Use`:
				//   CD KAPALI -> SendEventByName(Update, "STOP CD")
				//                + EnableFSM(ChangeChannel) + EnableFSM(ChangeTrack)
				//   CD AÇIK  -> SetBoolValue(kendi yerel bool'u) + EnableFSM ×2
				//
				// Anahtar HİÇBİR senkron değişkene yazmıyor; `EnableFSM` bir
				// AKSİYONDUR, `AssemblyPatch.OnSendEvent` kancası onu
				// yakalayamaz. Tek ölçülebilir gösterge: CDPlayer'da
				// AudioSource.isPlaying. Bu bayrak taşınınca alan taraf
				// STOP CD / PLAY CD üretir → ses açılıp kapanır.
				w.Write(cdMode ? 1 : 0);
				w.Write(ch1 ? 1 : 0);
				w.Write(folk ? 1 : 0);
				w.Write(sta ? 1 : 0);
				w.Write(vol);
				w.Flush();

				if (SteamTransport.SendReliable(Protocol.Finish(s))) {
					SentPlayPackets++;
					LastPlayState = cdChanged
						? ("CD " + (cdName.Length > 0 ? cdName : "?")
							+ " track=" + cdIdx
							+ " mode=" + (cdMode ? "ON" : "OFF")
							+ " " + (playing ? "PLAYING" : "idle"))
						: ("RADIO ch1=" + ch1 + " folk=" + folk + " static=" + sta
							+ " vol=" + vol.ToString("F2"));
					SmpTrace.Act("RADIO", "play", LastPlayState);
				}
			}
			catch (Exception e) {
				SmpLog.Warning("radio play send failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>Gelen çalma paketini uygular.</summary>
		public static void OnPlayMessage(BinaryReader r) {
			byte kind;
			string cdName;
			int cdIdx, playing, cdMode, ch1, folk, sta;
			float vol;
			try {
				kind = r.ReadByte();
				cdName = r.ReadString();
				cdIdx = r.ReadInt32();
				playing = r.ReadInt32();
				cdMode = r.ReadInt32();
				ch1 = r.ReadInt32();
				folk = r.ReadInt32();
				sta = r.ReadInt32();
				vol = r.ReadSingle();
			}
			catch (Exception e) {
				Telemetry.BadPackets++;
				SmpLog.Warning("radio play packet truncated: " + e.GetType().Name);
				return;
			}

			try {
				if (EchoGuard.Suppressed) return;   // yankı: tekrar gönderme

				bool isCd = (kind == 0);
				// BOS CALMA KORUMASI: hicbirseyi olmayan tarafin "calmiyor" durumu,
				// yerelde calani durduramaz.
				if (isCd && cdName.Length == 0 && playing == 0 && cdMode == 0 && LocalPlayingCd()) return;
				// KARAR: cdMode önceliklidir. CD player'da çalan clip varsa
				// mod AÇIK; yoksa KAPALI (RadioCDSwitch STOP CD gönderdi).
				bool wantPlay = (cdMode != 0) || (playing != 0);

			// PCM AKIS STOP: gonderen CD calmiyorsa (STOP / radyoya donus)
			// alici akisi da durur; yoksa tampon bitene kadar eski ses calardi.
			if (!isCd || !wantPlay) { try { CdAudioStream.StopReceiving(); } catch { } }

				// ---- CDPlayer/Update: değişkenleri yaz, sonra oyunun
				//      kendi olayını tetikle (AudioPlay FSM'in içinde).
				if (isCd && cdName.Length > 0) {
					PlayMakerFSM upd = FsmFor("CDPlayer", "Update");
					if (upd != null) {
						EchoGuard.Scope sc = EchoGuard.Enter();
						try {
							FsmString s = FsmNames.Str(upd, "CurrentCD");
							if (s != null) s.Value = cdName;
							FsmInt ix = FsmNames.Int(upd, "Index");
							if (ix != null) ix.Value = cdIdx;
							FsmInt ct = FsmNames.Int(upd, "CurrentTrack");
							if (ct != null && wantPlay) ct.Value = cdIdx;

							// Oyunun kendi olayı: wantPlay ? "PLAY CD" : "STOP CD"
							string ev = wantPlay ? "PLAY CD" : "STOP CD";
							if (FsmNames.HasEvent(upd, ev)) {
								upd.SendEvent(ev);
								AppliedPlayPackets++;
								SmpTrace.Act("RADIO", "play", "mirror '" + ev
									+ "' " + cdName + " track=" + cdIdx);
							}
							else {
								// Olay bildirilmemişse doğrudan AudioSource'u
								// uyar: en azından var olan parça çalmaya başlar.
								PlayClipDirect(upd, cdName, cdIdx, wantPlay);
								AppliedPlayPackets++;
							}
						}
						finally { sc.Dispose(); }
					}
				}

				// ---- RadioChannels/Fetch: kanal bool'ları + ses, sonra
				//      RADIO_UPDATE (Fetch) / RADIO_FOLK (Folk/Update).
				if (!isCd) {
					PlayMakerFSM rc = FsmFor("RadioChannels", "Fetch");
					if (rc != null) {
						EchoGuard.Scope sc = EchoGuard.Enter();
						try {
							SetBool(rc, "Channel1", ch1 != 0);
							SetBool(rc, "Folk", folk != 0);
							SetBool(rc, "Static", sta != 0);
							FsmFloat fv = FsmNames.Float(rc, "Volume");
							if (fv != null) fv.Value = vol;

							if (FsmNames.HasEvent(rc, "RADIO_UPDATE")) rc.SendEvent("RADIO_UPDATE");
							else if (FsmNames.HasEvent(rc, "FETCH")) rc.SendEvent("FETCH");

							// Folk kanalı ayrı bir FSM'de çalıyor:
							//   RADIO/RadioChannels/Folk  FSM 'Update'
							//   SetAudioClip(RadioOnFolk) + AudioPlay
							PlayMakerFSM fu = FsmFor("Folk", "Update");
							if (fu != null) {
								if (folk != 0 && FsmNames.HasEvent(fu, "RADIO_FOLK")) fu.SendEvent("RADIO_FOLK");
								else if (folk == 0 && FsmNames.HasEvent(fu, "RADIO_UPDATE")) fu.SendEvent("RADIO_UPDATE");
							}
							AppliedPlayPackets++;
							SmpTrace.Act("RADIO", "play", "mirror RADIO_UPDATE ch1=" + (ch1 != 0)
								+ " folk=" + (folk != 0) + " vol=" + vol.ToString("F2"));
						}
						finally { sc.Dispose(); }
					}
				}
			}
			catch (Exception e) {
				SmpLog.Warning("radio play apply failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		static void SetBool(PlayMakerFSM fsm, string name, bool value) {
			FsmBool b = FsmNames.Bool(fsm, name);
			if (b != null) b.Value = value;
		}

		/// <summary>
		/// Yedek yol: olay bildirilmemişse AudioSource'a doğrudan clip koyup
		/// çal. `ArrayListGet(SongDatabaseCD, CurrentCD, index)` listeyi
		/// AudioClip'lerle doldurur; listeler CdMusicImport tarafından
		/// doldurulmuş olur.
		/// </summary>
		static void PlayClipDirect(PlayMakerFSM upd, string cdName, int index, bool play) {
			try {
				AudioSource src = null;
				GameObject cd = CdMusicImport.CdRoot();
				if (cd == null) return;
				PlayMakerArrayListProxy[] px = cd.GetComponents<PlayMakerArrayListProxy>();
				AudioClip clip = null;
				for (int i = 0; px != null && i < px.Length; i++) {
					if (px[i] == null || px[i].arrayList == null) continue;
					if (px[i].referenceName != cdName) continue;
					if (index >= 0 && index < px[i].arrayList.Count) {
						clip = px[i].arrayList[index] as AudioClip;
					}
					break;
				}
				if (clip == null) return;

				AudioSource[] srcs = upd.gameObject.GetComponents<AudioSource>();
				for (int i = 0; srcs != null && i < srcs.Length; i++) {
					if (srcs[i] != null) { src = srcs[i]; break; }
				}
				if (src == null) return;
				src.clip = clip;
				if (play) src.Play(); else src.Stop();
			}
			catch { }
		}
	}
}
