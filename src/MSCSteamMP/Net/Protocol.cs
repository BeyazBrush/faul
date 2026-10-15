using System;
using System.IO;

namespace SteamMP
{
    /// <summary>
    /// Wire format: header, message ids and packet limits.
    ///
    /// Two layers live here:
    ///
    /// 1. Satsuma replication (BoltBatch / PartBatch / PartEvent / SyncRequest /
    ///    SyncDone) with PROTOCOL_ID as the magic number. These ids are pinned by
    ///    tests/WireProbe.cs, so they must not be renumbered.
    /// 2. Transport and vehicle/player messages (Handshake, Heartbeat, Transform,
    ///    PlayerPos, ...). These were added when the transport was ported over to
    ///    the lobby based SteamTransport and are numbered from 10 upwards so they
    ///    can never collide with layer 1.
    ///
    /// Both sides must run the same mod build: PROTOCOL_VERSION is checked in the
    /// handshake and published in the lobby data, so a mismatched client skips the
    /// lobby during discovery instead of connecting and failing later.
    /// </summary>
    public static partial class Protocol
    {
        // WireProbe tarafından beklenen protokol imzası (0xCAFEF00D)
        public const uint PROTOCOL_ID = 3405697037;

        /// <summary>
        /// Revision of the transport/vehicle/player protocol. Bump on any wire change.
        /// Written as a single byte in the handshake, so it has to stay a byte.
        /// v4: FluidState (43) for Satsuma fluids/wear/parked fuel.
        /// v5: each BoltBatch record ends with the bolt's own path. The two sides were
        ///     on different saves (live log: 378 parts / 337 bolts vs 369 / 332), so the
        ///     per-part bolt indices shifted and the wrong bolt got unscrewed. An old
        ///     build would read those bytes as the next record's id, so the version is
        ///     bumped to make a mixed build fail at the handshake instead.
        /// v6: DoorEvent (45). Doors are driven by PlayMaker EVENTS in this build, not
        ///     by a state bool - see WorldObjectSync. The old variable-write path could
        ///     never move a door, so this is a new message rather than a new field, and
        ///     a v5 peer would silently ignore a door.
        /// v7: the Interaction layer finally routes and produces (CarryState 50,
        ///     PartMountState 51, BoltBatchV2 52, AuthorityClaim 53,
        ///     FullStateSnapshot 54, PartRecovery 55, AuthoritySnapshot 56) plus
        ///     LogLine 57 (peer log mirroring). The ids were always allocated; the
        ///     build that shipped before could neither route nor produce them, so a
        ///     v6 peer and this build share no interaction state at all. The version
        ///     bump makes that a handshake failure instead of a silent session where
        ///     nothing appears to sync.
        /// v9: EconomyState (71) and JobState (72) are split off FluidState (43).
        ///     Msg.FluidState was written by THREE senders with THREE different
        ///     record layouts - 7-bit length-prefixed strings for fluids, ushort
        ///     length-prefixed for economy, ushort plus a type byte for jobs - and
        ///     the receiver tried all three readers against every packet by
        ///     rewinding the stream. Two of the three therefore failed on every
        ///     packet by construction (live log, 17x each):
        ///       "economy receive failed: Failed to read past end of stream."
        ///       "jobs receive failed: Failed to read past end of stream."
        ///     Separate ids make the layout unambiguous. Both ids are appended, so
        ///     nothing is renumbered and the pinned Satsuma ids stay untouched.
        /// v10: Msg.TextChat carries the SENDER's timestamp (a string, appended
        ///     last). Previously the receiver stamped the message with its own
        ///     local ClockStamp() on arrival, so the two sides displayed different
        ///     times for the same message and the displayed time was really
        ///     "when it arrived", not "when it was sent". The field is a formatted
        ///     "HH:mm:ss" wall-clock string, so it is directly comparable between
        ///     machines and needs no clock sync. Appended at the END so an older
        ///     reader still parses the prefix; the version bump makes a mixed
        ///     build fail at the handshake instead of showing shifted times.
        /// </summary>
        /// v11: Msg.EnterVehicle / Msg.LeaveVehicle carry a SEAT ROLE byte after
        ///     the vehicle id. Previously the packet only said "vehicle N", so a
        ///     peer sitting in the passenger seat was indistinguishable from the
        ///     driver: the receiver always attached the puppet to the driver seat
        ///     (SyncedVehicle.FindSeat). Two people in the minibus therefore both
        ///     appeared at the wheel. The byte is 0 = driver, 1 = passenger.
        ///     Appended after the existing field, and the version bump makes a
        ///     mixed build fail at the handshake rather than misplace a player.
        /// v16: Msg.MirrorState (84) / Msg.MirrorEvent (85) / Msg.MirrorSim (86).
        ///     MWCoop senkron/aynalama YÖNTEMİNİN genel katmanı (Integration/
        ///     MirrorSync): sürekli fizik = güvenilmez snapshot son-kazanır,
        ///     tekil aksiyon = güvenilir olay-tekrarı, sim = 1 Hz + final.
        ///     Üç id de SONA eklendi (renumber yasak, SLOTS=128 içinde boştu);
        ///     eski build handshake'te elenir, akış kayması olmaz.
        /// v20: Msg.DatabaseBatch (88) -- the `Database/*` part-flag channel.
        ///     Records are (id, installed, bolted, damaged): a Database row is an
        ///     IDENTITY record, not a mount slot, so there is no slot byte.
        ///
        ///     WHY A NEW ID AND NOT PartBatch: the layouts differ by one field
        ///     (PartBatch carries a slot index at position 4). Reusing the id
        ///     would make a v19 peer read a Database `damaged` bool as the NEXT
        ///     record's 7-bit string length -- a silent stream desync, not a
        ///     clean failure. The id is APPENDED (87 is CdPlayerState, 88 was
        ///     the lowest free one) so nothing is renumbered, and the version
        ///     bump turns a mixed build into a handshake refusal
        ///     (SteamTransport checks PROTOCOL_VERSION both in the lobby data
        ///     and in the handshake body).
        /// </summary>
        public const byte PROTOCOL_VERSION = 21;

        /// <summary>Steam P2P channel. Anything not on this channel is not ours.</summary>
        public const int P2P_CHANNEL = 7;

        /// <summary>
        /// Hard cap on an accepted packet. Anything larger is drained and dropped,
        /// otherwise it would stay queued in Steam and the read loop would stall.
        /// 32 KB is far above anything the mod sends (the largest is the initial
        /// bolt batch, a few hundred bytes).
        /// </summary>
        public const uint MAX_PACKET_SIZE = 32 * 1024;

        /// <summary>Header size in bytes: magic (uint) + message id (byte).</summary>
        public const int HEADER_SIZE = 5;

        public enum Msg : byte
        {
            // --- Satsuma replication, ids pinned by tests/WireProbe.cs ---
            BoltBatch = 1,
            PartBatch = 2,
            PartEvent = 3,
            SyncRequest = 4,
            SyncDone = 5,
            /// <summary>A single part's installed bool, no event. The wiring-part path.</summary>
            PartState = 6,
            /// <summary>
            /// Part world transform (position + rotation). Fixes parts appearing in
            /// the garage on the client instead of on the car, and lets a held part
            /// be seen by the other player.
            /// </summary>
            PartTransform = 7,
            /// <summary>
            /// Garage part / item state: position, rotation and who is holding it.
            /// Continuous data, sent unreliable-no-delay.
            /// </summary>
            EntityState = 8,

            // --- Transport ---
            Handshake = 10,
            Heartbeat = 11,
            HeartbeatResp = 12,
            Disconnect = 13,

            // --- Vehicle ---
            Transform = 20,
            Switch = 21,
            State = 22,
            Engine = 23,
        /// <summary>vehicleId (int32) + seatRole (byte: 0 = driver, 1 = passenger). Reliable.</summary>
            EnterVehicle = 24,
        /// <summary>vehicleId (int32) + seatRole (byte). Reliable.</summary>
            LeaveVehicle = 25,
            FullState = 26,
        /// <summary>Driver wheel inputs (torque, steer, brake, rpm). Unreliable, 20 Hz.</summary>
        WheelState = 27,

            // --- Player ---
            /// <summary>Remote player position/rotation. Unreliable, 20 Hz.</summary>
            PlayerPos = 30,
            /// <summary>Player name + which NPC template to clone. Once per session.</summary>
            PlayerInfo = 31,
        /// <summary>Item picked up or dropped: path + event + position. Reliable.</summary>
        ItemEvent = 32,
        /// <summary>Game clock and weather variables. Reliable, 1 Hz.</summary>
        WorldState = 33,



            // --- World objects (doors, taps, shower, switches) ---
            /// <summary>
            /// A world object's FSM state: object path + a list of
            /// (fsm, variable, value). Reliable: a door that fails to open
            /// once stays broken for the rest of the session.
            /// </summary>
            ObjectState = 40,
            /// <summary>
            /// Radio / CD player: track, playing bool, volume, channel.
            /// Reliable, but rate-limited to 1 Hz plus on-change, because
            /// audio that glitches is worse than audio that is 100 ms late.
            /// </summary>
            RadioState = 41,

            /// <summary>
            /// Radyo/CD FSM OLAYI. Degisken senkronu yetmiyor: oyun sesi
            /// AKSIYONLA kapatiyor (RADIO/RadioChannels'in "Off" durumu
            /// AudioSource.mute + EnableFSM), yani "sessiz" durumu hicbir
            /// degiskene yazilmiyor. Kanal degisimi ve CD parca degisimi de
            /// olay olarak gidiyor (NEXT/PREVIOUS/PLAYLIST/FETCH/...).
            /// Guvenilir, olay basina tek paket.
            /// </summary>
            RadioEvent = 46,

            /// <summary>
            /// Radyo/CD ÇALMA DURUMU. Değişken senkronu yetmiyor: karşı
            /// tarafta sesin çalması için oyunun FSM'inin YÜRÜMESİ gerekiyor
            /// (AudioPlay/AudioStop). `CurrentCD`+`Index` yazılsa bile
            /// `CDPlayer/Update` "State 1"de bekliyorsa ses çıkmaz.
            ///
            /// Yüzde 0 = kind (0=CD parçası, 1=radyo kanalı)
            /// sonra: string path, string event, int cdIdx, int track,
            ///       bool playing, bool ch1, bool folk, bool statik, float volume
            ///
            /// Güvenilir, yalnızca değişimde.
            /// </summary>
            RadioPlay = 47,

            /// <summary>
            /// CD SES AKIŞI — BAŞLIK. Gönderen çalan parçayı tanıtır.
            ///   string cdName ("CD1"), string clipName, int rate,
            ///   int totalFrames, int chunkFrames
            ///
            /// NEDEN VAR: karşı tarafta .ogg dosyası olmayabilir. Dosya
            /// göndermek yerine sesi PCM'e çevirip anlık akıtıyoruz; alan
            /// `AudioClip.Create` ile aynı uzunlukta clip üretip dolduruyor.
            /// </summary>
            CdAudioHeader = 48,

            /// <summary>
            /// CD SES AKIŞI — VERİ PARÇASI (güvenilmez, no-delay).
            ///   int frameIndex, byte[2*frames] int16 mono PCM (LE)
            ///
            /// GÜVENİLMEZ + NO-DELAY BİLEREK: gecikme birikiyorsa kuyruk
            /// şişip ses geride kalıyordu. Kayıp paket = 80 ms sessizlik;
            /// birikmiş gecikme = kalıcı kayma. Bu yüzden kayıp yeğlenir.
            /// </summary>
            CdAudioData = 49,

            /// <summary>
            /// ARAÇ KONTROL DURUMU aynası: (string path, string fsm,
            /// string state). Güvenilir, yalnızca değişimde.
            ///
            /// NEDEN GEREKLİ: CD player kapağı bir FSM durumu, bir olay değil.
            /// Kullanıcı: "cdplayer açtığımda görünmüyor". `cd player(Clone)`
            /// FSM 'Assembly' durumları: State 1 -> Check for Part ->
            /// Find correct part -> Wait for assembly -> Assemble -> "CD in".
            /// Olayı (ASSEMBLE) göndermek yetmez: `Part` değişkeni uzakta
            /// çözülmediği için FSM orada "Check for Part"ta kalıyor.
            /// Durumu doğrudan yazınca kapak ANINDA açılır.
            ///
            /// Yalnızca "saf görsel" FSM'ler aynalanır (beyaz liste):
            /// fare durumları ("Wait player", "Mouse over") taşınmaz, yoksa
            /// karşı tarafın düğmeleri kilitlenirdi.
            /// </summary>
            /// <summary>
			/// NOT: 50 KULLANILAMAZ (`CarryState = 50` ile çakışıyor).
			/// Boş kimlikler taramasıyla seçildi: 58/59 boş.
			/// </summary>
            CtrlState = 58,

            /// <summary>
            /// Player flags that change rarely but matter: sitting in a vehicle,
            /// crouching, dead. Reliable and sent ON CHANGE ONLY (plus 1 Hz
            /// keepalive), because a dropped flag leaves the remote player
            /// permanently in the wrong pose.
            /// Byte 0 = flags, byte 1 = crouch amount (0-100),
            /// float = vertical eye offset for the crouch blend.
            /// </summary>
            PlayerState = 42,
            /// <summary>
            /// Satsuma fluids + part wear + parked fuel: (path, fsm, var,
            /// value) records. Reliable, 0.5 Hz poll, on-change only.
            /// Added with PROTOCOL_VERSION 4; ids below must not be renumbered
            /// (same rule as the pinned ones above).
            /// </summary>
            FluidState = 43,
            /// <summary>
            /// Held item: (held, item path, hand path relative to the player
            /// root, local position, local rotation). Reliable, on change only.
            /// Replaces the old "replay the pickup event" path, which parented
            /// the item to the LOCAL player on the peer's machine.
            /// Added with PROTOCOL_VERSION 4; ids below must not be renumbered.
            /// </summary>
            HeldItem = 44,
            /// <summary>
            /// A door was opened or closed: (object path, fsm name, event name).
            /// Reliable, on change only.
            ///
            /// WHY AN EVENT AND NOT A VALUE: read out of the shipped assets, a
            /// door has no "is open" variable that could be handed to the peer.
            /// The interaction FSM is `Use` (or `UseOld`) and it only moves the
            /// mesh out of a state that the OPEN / CLOSE events select:
            ///
            ///   CARPARTS/PartsCar/door left(Clone)        FSM Use
            ///     start: Close door -> Open door -> Open door 2
            ///     events: OPEN, CLOSE, "Mouse off", "Mouse over 1/2", LOOP
            ///     actions: SetRotation + SetHingeJointLimits on Joint
            ///
            ///   YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle   FSM Use
            ///     start: Get pivot
            ///     events: USE, OPEN, CLOSE, OPENDOOR, LOCKED, DoorOpen
            ///
            /// The only bool on the car door is `Open`, and writing it does not
            /// advance the state machine, so the value landed and the door never
            /// moved - which is the reported symptom. Replaying the event runs the
            /// peer's own FSM, which is also the only way to get the lock check,
            /// the hinge limits and the sound.
            /// Added with PROTOCOL_VERSION 6.
            DoorEvent = 45,

            // --- Interaction (Carry/Install/Bolt/Authority) ---
            /// <summary>
            /// Oyuncu bir parçayı eline aldı veya bıraktı: (path, held, carrierPath, localPos, localRot, parentVehiclePath).
            /// Reliable, on-change. parentVehiclePath: taşıyıcı bir araç içindiyse o aracın yolu, yoksa boş.
            /// </summary>
            CarryState = 50,

            /// <summary>
            /// Parça takma/sökme: (partPath, installed, carrierPath, localPos, localRot, parentVehiclePath).
            /// Reliable. Install/Remove FSM olayını replay etmek yerine doğrudan durum yazılır
            /// çünkü FSM olayının hedef FSM'si peer'de farklı bir parent hiyerarşisinde olabilir.
            /// </summary>
            PartMountState = 51,

            /// <summary>
            /// Vida sıkma/gevşetme toplu paketi (batching).
            /// Her kayıt: (boltPath, stage, tightness). Reliable, batched.
            /// </summary>
            BoltBatchV2 = 52,

            /// <summary>
            /// Sahiplik talep/transfer: (objectPath, newOwnerSteamId, requestType).
            /// Reliable. requestType: 0=Request, 1=Grant, 2=Release, 3=ForceRelease.
            /// </summary>
            AuthorityClaim = 53,

            /// <summary>
            /// JIP (Join-In-Progress) tam durum istemi/yanıtı.
            ///
            /// İKİ YÖNLÜ, TEK ID — bu yüzden ilk bayt bir YÖN baytıdır:
            ///   0 = İSTEK  (Client -> Host, gerisinde gövde yok)
            ///   1 = CEVAP  (Host -> Client, gerisinde tam snapshot var)
            ///
            /// NEDEN AYRI BAYT (v9): canlı log, host'un istemcinin İSTEĞİNİ
            /// cevap sanıp tam snapshot okumaya çalıştığını gösteriyordu:
            ///     "recv/FullStateSnapshot/JIP snapshot receive failed:
            ///      Failed to read past end of stream."
            /// İstek gövdesi 1 bayt olduğu için okuyucu daima taşıyor, yani
            /// host'un cevabı üretilse bile client'a hiç ulaşmıyordu. Canlı
            /// logda bu uyarı her denemede tekrarlıyor.
            /// Reliable.
            /// </summary>
            FullStateSnapshot = 54,

            /// <summary>
            /// Parça "harita altından düştü" kurtarma: (partPath, newPosition).
            /// Reliable. Y koordinatı -100'ün altına inerse tetiklenir.
            /// </summary>
            PartRecovery = 55,

            /// <summary>
            /// Sahiplik snapshot (JIP ve doğrulama için).
            /// (objectPath, ownerSteamId, isHeld, isInstalled, holderPath).
            /// </summary>
            AuthoritySnapshot = 56,

            /// <summary>
            /// Karşı tarafın log satırı: (level, channel, text).
            ///
            /// İki oyunculu oturumda tek taraflı teşhis işe yaramıyor:
            /// "gönderdim mi, karşı taraf aldı mı, aldı da işlemedi mi"
            /// ayrımı ancak iki tarafın satırları yan yana durunca yapılabiliyor.
            /// Bu mesaj LogRemote tarafından gönderilir ve alıcıda "peer" kanalıyla
            /// kendi dosyasına yazılır.
            ///
            /// UNRELIABLE: teşhis satırı kaybolabilir, oyun durumu kaybolmamalı.
            /// Varsayılan KAPALI; 'smp log remote on' ile açılır.
            /// </summary>
            LogLine = 57,

            // --- Yeni sistemler (v8+) ---
            /// <summary>
            /// Save dosyası aktarımı (JIP): Host->Client, ES2 dosya verisi chunk'lar halinde.
            /// Reliable, büyük dosyalar için parçalı gönderim.
            ///
            /// ÖKSÜZ: bu kimlikleri kullanan kod KALDIRILDI. `SaveGameTransfer`
            /// sınıfı v22'de silindi — gerekçesi kodun kendi notunda: "HostUpdate()
            /// hiçbir yerden çağrılmıyordu, aktaran taraf veri hiç pompalamıyordu,
            /// alan taraf da veri hiç almıyordu". Yani DOSYA AKTARIMI YOK; iki
            /// tarafın AYNI kaydı yüklemesi gerekir (bkz. AuthorityManager'in
            /// "ortak kayıt varsayımı"). Kimlik numaralandırmayı bozmamak için
            /// duruyor.
            /// </summary>
            SaveGameChunk = 60,
            /// <summary>
            /// Save aktarımı tamamlandı/bitti sinyali.
            ///
            /// ÖKSÜZ: gönderen/okuyan yok (bkz. SaveGameChunk).
            /// </summary>
            SaveGameComplete = 61,

            /// <summary>
            /// Yapay zeka araç durumu: (vehicleId, position, rotation, velocity, angularVelocity, throttle, brake, steer, gear, rpm, lights).
            /// Unreliable, 20 Hz. Host yetkilidir.
            /// </summary>
            AIVehicleState = 62,

            /// <summary>
            /// Oyuncu ihtiyaçları: (bladder, hunger, thirst, fatigue, dead, permadeath).
            /// Reliable, on-change + 1 Hz keepalive.
            /// </summary>
            PlayerNeeds = 63,

            /// <summary>
            /// Dinamik eşya doğurma: (itemPath, position, rotation, networkId, ownerSteamId, sourceType).
            /// sourceType: 0=TeimoShop, 1=Spawner, 2=QuestReward.
            /// Reliable.
            /// </summary>
            ItemSpawn = 64,

            /// <summary>
            /// Metin sohbeti: (senderSteamId, senderName, message, channel).
            /// channel: 0=Global, 1=Team, 2=Whisper.
            /// Reliable.
            /// </summary>
            TextChat = 65,

            /// <summary>
            /// Host migration: yeni host bilgisi ve state snapshot.
            /// Reliable.
            /// </summary>
            HostMigration = 66,

            /// <summary>
            /// Ping ölçümü isteği/yanıtı: (timestamp, sequence).
            /// Unreliable, 1 Hz.
            /// </summary>
            Ping = 67,



            /// <summary>
            /// Oyuncu durum OLAYI: (flags, health, maxHealth, eventType).
            ///
            /// NEDEN AYRI: Msg.PlayerState (42) zaten LocalPlayerState'in
            /// oturma/egilme bayraklarını taşıyor. Aynı id'yi ikinci bir
            /// anlam için kullanmak, gelen paketin hangi işleyiciye
            /// gideceğini belirsizleştirirdi: SteamMPBehaviour bunu
            /// LocalPlayerState'e veriyordu, o da ölüm/canlanma bayraklarını
            /// oturma sanardı. Artık ayrı.
            /// Reliable (nadir: sadece ölüm/bayılma/canlanma anında).
            /// </summary>

            /// <summary>
            /// Host migration nabzı: (netClockMs).
            ///
            /// NEDEN AYRI: Msg.Heartbeat (11) SteamTransport'ın kendi nabzıdır
            /// ve ASLA OnMessage'e geçirilmez. Migration onun yerine bunu
            /// kullanır; aksi hâlde "host düştü" tespiti hiç tetiklenmezdi.
            /// Unreliable, 1 Hz.
            /// </summary>
            MigrationHeartbeat = 70,

            /// <summary>
            /// Ekonomi (para, Teimo stogu, yakıt pompaları): kayıtlar
            /// (path, fsm, var, float). Reliable, 0.5 Hz yoklama, yalnızca değişince.
            ///
            /// NEDEN AYRI (v9): bunlar FluidState (43) ile aynı YAPIYI kullanıyordu
            /// ama aynı mesaj id'si üzerinden gidiyordu ve string kodlaması FARKLIYDI
            /// (ushort uzunluk + UTF8 bayt). FluidWearSync'in kayıtlarını alan bir
            /// ekonomi okuyucusu 7-bit uzunluk önekini ushort sanıyor, yani sayıyı
            /// çarptıyor ve akışın sonuna kadar kayıyor. Canlı log bunu her pakette
            /// "Failed to read past end of stream" olarak bırakıyordu: ekonomi ve iş
            /// trafiği %100 kayıptı.
            /// </summary>
            EconomyState = 71,

            /// <summary>
            /// İşler (kilju, odun, tamirhane): kayıtlar (path, fsm, var, tip, deger).
            /// tip 0=float(4 bayt) 1=int(4 bayt) 2=bool(1 bayt). Reliable.
            ///
            /// NEDEN AYRI (v9): ekonomiyle aynı sebep. Ek olarak FluidWearSync'in
            /// okuyucusunda tip baytı yok, o yüzden iş paketini okurken de akış
            /// kayıyordu.
            /// </summary>
            JobState = 72,

            /// <summary>
            /// Direksiyon simidi: (arac-id, pivot local rot xyzw, simit local rot xyzw).
            /// Unreliable, 10 Hz, yalniz surucu gonderir. WheelState surus girdisini
            /// (CarController.steering) tasir ama kokpit simidi/kolonu baska
            /// transformdur ve HIC aynalanmiyordu (peer tam-kilit adimi goruyordu).
            /// </summary>
            SteerWheel = 73,

            /// <summary>
            /// Gosterge ibreleri: (arac-id, adet, [ad-hash, local rot xyzw]).
            /// Unreliable, 10 Hz, yalniz surucu gonderir. Ibre uniteleri
            /// (takma/sokme) parca sisteminde, lamba/kontak FsmBinder'da;
            /// CANLI ACI (hiz/devir/yakit/hararet) hicbir yerde yoktu.
            /// </summary>
            DashNeedle = 74,

            /// <summary>
            /// Havai fisek atesleme: (yol). Reliable, tek paket.
            /// Canta (fireworks bag) Open+USE ile yanar; fitil ayri nesne
            /// degil (dumper kaniti). Karsi taraf ayni USE'yi oynatir.
            /// </summary>
            FireworkLight = 75,

            /// <summary>
            /// "Yaziyor..." gostergesi: (gonderen-id, ad). Unreliable, 2.5 sn
            /// kisma. Sohbet kutusu odaktayken ve bos degilken gider.
            /// </summary>
            ChatTyping = 76,

            /// <summary>
            /// CD kutusu: (kutuYolu, kapakBiliniyor, kapakAcik, diskBiliniyor,
            /// diskAdi). Bos diskAdi = kutu bos. Bilinmeyen alanlar uygulanmaz.
            /// Reliable, degisimde + JIP yeniden gonderim. Dumper: cd case
            /// Open (cd_caseopen/cd_caseclose) + trigger_cd Assembly (cd_cdin).
            /// </summary>
            CdCase = 78,

            /// <summary>
            /// CD player diski (fiziksel): (slotYolu, diskAdi). Bos diskAdi
            /// = cikarma. Reliable, degisimde + JIP yeniden gonderim.
            /// Dumper: trigger_disc/Assembly "CD in" (cd_sled_in) + Eject
            /// (cd_sled_out + NO CD). CtrlState yalnizca durumu tasir,
            /// disk kimligi (CD1/2/3) bu pakette gider.
            /// </summary>
            CdDisc = 77,

            /// <summary>
            /// CD PLAYER DURUMU: (string path, string fsm, string var,
            /// bool isInt, int/bool value).
            ///
            /// NEDEN GEREKLİ -- kullanıcı: "cd playerı açtığımda kesin
            /// şekilde karşıdaki playerde de görünsün".
            ///
            /// `Database/PartsStatus/Radio` [Status] FSM'inin "Check CD" /
            /// "Check Radio" durumları CDPlayerOK / RadioOK bool'larını
            /// okuyup ActivateGameObject(ButtonsCD)/(ButtonsRadio)
            /// çalıştırıyor -- yani dashboard YÜZEYİ değişimi tam olarak
            /// bu FSM yapıyor. O bool hiçbir yerde senkronize edilmiyordu,
            /// bu yüzden karşı tarafta CD düğmeleri hiç belirmiyordu.
            ///
            /// Aynı paket şu sinyalleri de taşır:
            ///   trigger_cd   [Assembly].Setup   -- bu arabaya takılı mı
            ///   trigger_disc [Assembly].CDin     -- kizakta disk var mı
            ///   Radio/CD     [Playlist].Path     -- CD listeleri yüklendi mi
            ///   Radio/CD/CDn [LoadSongs].MaxTracks -- kaç şarkı var
            /// (MaxTracks: "şarkılar cd'nin içinde" — alıcının kendi .ogg
            ///  dosyaları farklıysa parça numaraları kaymasın diye.)
            ///
            /// Id 87: boştu (kullanılanlar arasında en düşük boş).
            /// </summary>
            CdPlayerState = 87,

            /// <summary>
            /// Gunes / oyun saati / hava durumu (MAP/SUN/Pivot/SUN).
            /// (int day, int minute, bool weather, float sunX).
            /// Host 2 Hz gonderir, UNRELIABLE: saat yavas degisir, kanal
            /// koparsa bir sonraki pakette duzelir.
            ///
            /// Dumper kaniti: `MAP/SUN/Pivot/SUN` fsm=`Clock` (Hours,
            /// Minutes, Time, TimeScale) ve fsm=`Color` (WorldDay, WorldTime,
            /// Weather, ClockTable; durumlar Check time save / Sunrise /
            /// Sunset / Night / RainColorDay / RainColorNight;
            /// SaveInt+LoadInt -> defaultES2File.txt, UniqueTagDay/Time).
            ///
            /// Yalniz uc degisken + `RESET` gonderilir; oyunun kendi
            /// `Color` FSM'i EaseColor / SetAmbientLight / SetFogColor
            /// ile gunes rengini, Ambient'i ve sisi yeniden hesaplar.
            /// Renkleri agdan kopyalamak yanlis olurdu (ozel hesap).
            /// </summary>
            EnvClock = 79,

            /// <summary>
            /// Kapı durumu: (object path, fsm name, event name).
            /// Reliable, on change only.
            ///
            /// WHY AN EVENT AND NOT A VALUE: read out of the shipped assets, a
            /// door has no "is open" variable that could be handed to the peer.
            /// The interaction FSM is `Use` (or `UseOld`) and it only moves the
            /// mesh out of a state that the OPEN / CLOSE events select:
            ///
            ///   CARPARTS/PartsCar/door left(Clone)        FSM Use
            ///     start: Close door -> Open door -> Open door 2
            ///     events: OPEN, CLOSE, "Mouse off", "Mouse over 1/2", LOOP
            ///     actions: SetRotation + SetHingeJointLimits on Joint
            ///
            ///   YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle   FSM Use
            ///     start: Get pivot
            ///     events: USE, OPEN, CLOSE, OPENDOOR, LOCKED, DoorOpen
            ///
            /// The only bool on the car door is `Open`, and writing it does not
            /// advance the state machine, so the value landed and the door never
            /// moved - which is the reported symptom. Replaying the event runs the
            /// peer's own FSM, which is also the only way to get the lock check,
            /// the hinge limits and the sound.
            /// Added with PROTOCOL_VERSION 6.
            /// <summary>
            /// Kapı durumu (eski DoorEvent ile aynı, yeni isim).
            /// (object path, fsm name, event name). Reliable, on change only.
            ///
            /// ÖKSÜZ: bu kimliği GÖNDEREN ya da OKUYAN bir kod yok. Kapı
            /// senkronunun canlı hattı `Msg.ObjectState` (40) +
            /// `WorldObjectSync`'tir; bu kimlik erken bir tasarım
            /// denemesinden kalmış. Karşılığı olmadığı için `default` dalına
            /// düşen bir paket üretemez (hiç gönderilmiyor) ve takma noktası
            /// olarak çakışma riski de yok. Silinmedi: WireProbe diğer
            /// kimlikleri sabitliyor, numaralandırma bozulmasın.
            /// </summary>
            DoorState = 80,

            /// <summary>
            /// Şalter durumu: (switchPath, isOn). Reliable, on change only.
            ///
            /// ÖKSÜZ: gönderen/okuyan yok. Şalter senkronu `ObjectState` (40)
            /// üzerinden gider (WorldObjectSync).
            /// </summary>
            SwitchState = 81,

            /// <summary>
            /// Motor durumu: (entityPath, isRunning). Reliable, on change only.
            ///
            /// ÖKSÜZ: gönderen/okuyan yok. Araç motor durumu `Msg.Engine`
            /// (23) + `Msg.State` (22) ile gider (VehicleRegistry).
            /// </summary>
            EngineState = 82,

            /// <summary>
            /// Ortam durumu: (worldTime, worldDay, weather, sunRotation, temperature).
            /// Host 2 Hz gönderir, UNRELIABLE.
            /// </summary>
            EnvironmentState = 83,

            /// <summary>
            /// MWCoop yöntemi: genel ayna SNAPSHOT'u.
            /// (mode, key, pos, rot, vel, angVel). UNRELIABLE son-kazanır;
            /// bırakma/final anı reliable gönderilir (MirrorSync.ReleaseAuthority).
            /// Host guest'ten geleni diğerine relay eder (yıldız+relay).
            /// v16'da SONA eklendi; renumber yasak.
            /// </summary>
            MirrorState = 84,

            /// <summary>
            /// MWCoop yöntemi: genel ayna OLAY-TEKRARI.
            /// (key, fsm, event). RELIABLE, değişimde tek paket. Alan kendi
            /// FSM'inde aynı olayı oynatır (EchoGuard + Claim ile yankısız).
            /// v16'da SONA eklendi; renumber yasak.
            /// </summary>
            MirrorEvent = 85,

            /// <summary>
            /// MWCoop yöntemi: genel ayna SİM durumu.
            /// (key, final, n, [name, value]). RELIABLE, 1 Hz + bırakma-anı
            /// final. Akü/ısı/yakıt gibi yavaş sim değişkenleri için.
            /// v16'da SONA eklendi; renumber yasak.
            /// </summary>
            MirrorSim = 86,

            /// <summary>
            /// `Database/*` part-flag rows, paged exactly like PartBatch:
            /// one count byte, then that many
            ///     (string id, bool installed, bool bolted, bool damaged)
            /// records. FOUR fields -- a Database row is an identity record
            /// and has no mount slot, so no slot byte is written. Reliable.
            ///
            /// NOT Msg.PartBatch (2): that record carries a slot byte at
            /// offset 3. A v19 peer reading this packet would treat our
            /// `damaged` bool as the next record's 7-bit string length and
            /// desync, so the id is new AND the version is bumped (v20).
            /// Id 88: appended, so nothing above it was renumbered.
            /// </summary>
            DatabaseBatch = 88,
            /// <summary>
            /// TA-01: ic yapi bayraklari (kanca / kemer kilidi /
            /// sinyal). Bunlar SATSUMA parca tablosunda DEGIL
            /// (Assembly/Removal FSM'i olmayan nesneler), dolayisiyla
            /// `WritePartBatch` yolu onlari goremez.
            /// Kayit: byte index, bool deger.
            /// </summary>
            InteriorState = 89,
        }

// ------------------------------------------------------------------
// NetStats  -  paket basina gonderilen / alinan sayaclari
// ------------------------------------------------------------------

        /// <summary>Paket basina sayac. Sadece SAYAR; gonderme islemini YAPMAZ.</summary>
        /// <remarks>
        /// NEDEN: kullanici "hicbiri calismiyor" dedi. Log'da 6 parti
        /// cikarma olayi (+ [ACT] PART removed) ile net.partmount.send n=6
        /// birebir eslesti -> parca yolu CALISIYOR. Ama kap / kontak /
        /// motor / vida / ekonomi / is / esya modullerinin HICBIR ag
        /// sayaci yoktu. [ACT] satirlari YEREL eylem gunlugudur, ag
        /// kaniti degildir. Yani su an su ikisi ayirt edilemiyor:
        ///
        ///   (a) paket gonderiliyor ama karsi tarafta UYGULANMIYOR
        ///   (b) paket hic gonderilmiyor (kesf / kosul hatasi)
        ///
        /// Bu sayaclar bir oturumda ikisini AYIRT EDER:
        ///   gonder=0            -> (b) kesf/kosul hatasi
        ///   gonder>0, alin=0    -> teslim veya uygulama hatasi
        ///   gonder>0, alin>0    -> iki yonlu de calisiyor
        /// </remarks>
        public static class NetStats {

            /// <summary>Msg enum'unun en buyuk degeri + 1</summary>
            public const int SLOTS = 128;

            static readonly long[] sent = new long[SLOTS];
            static readonly long[] recv = new long[SLOTS];
            static readonly long[] sentBytes = new long[SLOTS];
            static readonly long[] recvBytes = new long[SLOTS];
            static readonly long[] refused = new long[SLOTS];
            static long totalSent, totalRecv;

            static int Idx(Msg m) {
                int i = (int)m;
                return (i >= 0 && i < SLOTS) ? i : -1;
            }

            /// <summary>Oyun basindan beri gonderilen bayt.</summary>
            public static long TotalSentBytes { get { return totalSent; } }
            /// <summary>Oyun basindan beri alinan bayt.</summary>
            public static long TotalRecvBytes { get { return totalRecv; } }

            /// <summary>Gonderim SONUCU. SteamTransport.SendRaw icinden cagrilir.</summary>
            /// <param name="ok">Steam pakete kabul etti mi?</param>
            /// <remarks>
            /// NEDEN AYRI SAYAC: onceki konum (Protocol.Finish) paketin
            /// OLUSUGUNU sayiyordu. Ama SendRaw iki yerde sessizce
            /// dusuyordu: oturum yoksa ve Steam guvenilir kanali
            /// reddettiyse. Olcum (iki log): host WorldState 127 gonderdi,
            /// client 74 aldi; paylasimli yon 61/61 TAMDI.
            /// </remarks>
            public static void CountSend(Msg m, int bytes, bool ok) {
                int i = Idx(m);
                if (i < 0) return;
                if (ok) {
                    sent[i]++;
                    sentBytes[i] += bytes;
                    totalSent += bytes;
                }
                else refused[i]++;
            }

            /// <summary>Steam'in REDDETTIGI (ulasmayan) paket sayisi.</summary>
            public static long Refused(Msg m) {
                int i = Idx(m);
                return i < 0 ? 0 : refused[i];
            }

            /// <summary>Bir paket alindi. Dispatch basinda cagrilir.</summary>
            public static void CountRecv(Msg m, int bytes) {
                int i = Idx(m);
                if (i < 0) return;
                recv[i]++;
                recvBytes[i] += bytes;
                totalRecv += bytes;
            }

            /// <summary>Mesajin gonderilme sayisi.</summary>
            public static long Sent(Msg m) {
                int i = Idx(m);
                return i < 0 ? 0 : sent[i];
            }
            /// <summary>Mesajin alinma sayisi.</summary>
            public static long Recv(Msg m) {
                int i = Idx(m);
                return i < 0 ? 0 : recv[i];
            }
            /// <summary>Mesajin gonderilme bayti.</summary>
            public static long SentBytes(Msg m) {
                int i = Idx(m);
                return i < 0 ? 0 : sentBytes[i];
            }
            /// <summary>Mesajin alinme bayti.</summary>
            public static long RecvBytes(Msg m) {
                int i = Idx(m);
                return i < 0 ? 0 : recvBytes[i];
            }

            /// <summary>Sahne degisiminde sayaclari sifirlar.</summary>
            public static void Clear() {
                for (int i = 0; i < SLOTS; i++) {
                    sent[i] = 0; recv[i] = 0;
                    sentBytes[i] = 0; recvBytes[i] = 0;
                    refused[i] = 0;
                }
                totalSent = 0;
                totalRecv = 0;
            }

            /// <summary>'smp netstat' ciktisi (mesaj basina gonderilen/alinan).</summary>
            public static string Describe() {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("NETSTAT  gonderilen ").Append(totalSent)
                  .Append(" B / alinan ").Append(totalRecv).Append(" B");
                bool any = false;
                for (int i = 0; i < SLOTS; i++) {
                    if (sent[i] == 0 && recv[i] == 0) continue;
                    if (!any) {
                        sb.AppendLine();
                        sb.Append("  mesaj                          gonder     alin          gB          aB  reddet");
                        any = true;
                    }
                    // SÜTUNLAR ARASI AYRAÇ ŞART.
                    //
                    // CANLI HATA: PadLeft var ama aralarında boşluk yoktu.
                    // 6 haneli sayılar bitişik basılıyordu ve tablo
                    // okunmuyordu:
                    //     EntityState  196  220  58986160187   3
                    //     RadioState   373  395  249537316395   0
                    // Hangisinin gB hangisinin aB olduğu BELLI DEGILDI,
                    // yani tablonun tek islevi olan sey (teslim oranini
                    // gormek) bozuluyordu. Her sutunun ONUNDE iki bosluk
                    // var; hicbir sayi digerine yapismaz.
                    sb.Append("  ").Append(((Msg)i).ToString().PadRight(28))
                        .Append("  ").Append(sent[i].ToString().PadLeft(7))
                        .Append("  ").Append(recv[i].ToString().PadLeft(7))
                        .Append("  ").Append(sentBytes[i].ToString().PadLeft(9))
                        .Append("  ").Append(recvBytes[i].ToString().PadLeft(9))
                        .Append("  ").Append(refused[i].ToString().PadLeft(6));
                    if (refused[i] > 0) sb.Append("   <== STEAM REDDETTI");
                    else if (sent[i] > 0 && recv[i] == 0) sb.Append("   <== TESLIM YOK");
                    else if (sent[i] == 0 && recv[i] > 0) sb.Append("   <== TEK YONLU");
                    sb.AppendLine();   // HER SATIR AYRI: tablo okunabilir olsun
                }
                if (!any) sb.AppendLine().Append("  (hic paket gonderilmedi)");
                return sb.ToString();
            }

            /// <summary>'smp status' icin tek satirlik ozet.</summary>
            public static string Summary() {
                int active = 0;
                for (int i = 0; i < SLOTS; i++) {
                    if (sent[i] > 0 || recv[i] > 0) active++;
                }
                return "net: " + active + " mesaj turu, " + totalSent
                        + " B gonderildi / " + totalRecv + " B alindi";
            }
        }

        /// <summary>Begin'de giden mesaj tipini hatirlar; Finish NetStats'a bildirir.
        /// Alan sahasi DEGIL ama cift ile kullanilir: ara bir baska paket
        /// yazilirsa sayac yanlis olurdu. Bu yuzden Begin her cagrildiginda
        /// ezilir - cift kirilirsa sonuncu sayilir, o da gonderilen gercek
        /// paket sayisindan fazla olamaz.</summary>

        public static MemoryStream Begin(Msg messageType)
        {
            MemoryStream stream = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(PROTOCOL_ID);
            writer.Write((byte)messageType);
            return stream;
        }

        public static byte[] Finish(MemoryStream stream)
        {
            byte[] data = stream.ToArray();
            // SAYIM: gonderilen HER paket bu noktadan gecer. Buraya koymak,
            // modulun sayac eklemeyi unutma riskini ortadan kaldirir.
            // (smp netstat)
            return data;
        }

        public static bool TryReadHeader(BinaryReader reader, out Msg messageType)
        {
            messageType = (Msg)0;
            if (reader == null) return false;
            if (reader.BaseStream.Position + HEADER_SIZE > reader.BaseStream.Length) return false;

            uint id = reader.ReadUInt32();
            if (id != PROTOCOL_ID) return false;

            messageType = (Msg)reader.ReadByte();
            return true;
        }

        /// <summary>
        /// True when the id belongs to the vehicle/player group rather than Satsuma.
        ///
        /// ETKILESIM MESAJLARI (CarryState, PartMountState, BoltBatchV2,
        /// AuthorityClaim, FullStateSnapshot, PartRecovery, AuthoritySnapshot)
        /// BURAYA GIRMEZ. Onlar InteractionManager'in mesajlari; SteamMPBehaviour
        /// once bunlari yakalayip InteractionManager.RouteMessage'e veriyor. Bu
        /// listede olsalardi onceki ifade onları VehicleRegistry'ye yollardi,
        /// orada da hicbir case yoktu ve paket sessizce atiliyordu - yani taşıma,
        /// parça takma, vida ve sahiplik akışlarinin tamamı ölüydü.
        /// </summary>
        public static bool IsVehicleOrPlayer(Msg id)
        {
            return id == Msg.Transform || id == Msg.Switch || id == Msg.State || id == Msg.Engine
                || id == Msg.EnterVehicle || id == Msg.LeaveVehicle || id == Msg.FullState
                || id == Msg.WheelState || id == Msg.PlayerPos || id == Msg.PlayerInfo
                || id == Msg.SteerWheel || id == Msg.DashNeedle;
            // AIVehicleState(62), PlayerNeeds(63) ve Ping(67) BILINCLI OLARAK
            // CIKARILDI. CANLI LOG KANITI (sessiz kayip): bu ucu buradaydi, ama
            // `VehicleRegistry.OnMessage`in hicbir case'i yok. SteamMPBehaviour
            // sirasiyla su sekilde gidiyordu:
            //
            //     if (IsVehicleOrPlayer(id)) { ... default:
            //         VehicleRegistry.OnMessage(id, sender, r);
            //         return;                      <-- KOSULSUZ
            //     }
            //     if (ModIntegration.TryHandle(id, r)) return;   // HIC ULASILMADI
            //
            // `ModIntegration.IsHandled` bu ucu sahipleniyordu
            // (AIVehicleSync.OnMessage / PlayerNeedsSync.OnMessage / OnPing)
            // ama o satira ASLA gelinmiyordu: switch hicbir case'a dusmedigi
            // icin stream'den HICBIR bayt okunmadan donuluyordu.
            //
            // SONUÇ (iki yonde de %100 sessiz kayip):
            //   - oyuncu ihtiyaclari (susuzluk/acik/stres/yorgunluk/idrar/olum)
            //     hic senkronize olmuyordu (PlayerNeedsSync.cs 31 KB bosa)
            //   - NPC arac durumu hic senkronize olmuyordu (AIVehicleSync.cs 20 KB)
            //   - Ping olculmedigi icin SteamTransport.PingMs hic guncellenmiyordu;
            //     WorldObjectSync.Flush() her ObjectState paketine o bayti yaziyor
            //     (t.PingMs), yani gecikme telafisi yanlis sabitle calisiyordu.
        }

        /// <summary>
        /// True for interaction layer messages (Carry, PartMount, Bolts, Authority, JIP, SaveGame, Items, Chat, HostMigration, Ping).
        /// These are routed to InteractionManager or dedicated handlers.
        /// </summary>
        public static bool IsInteraction(Msg id)
        {
            return id == Msg.CarryState || id == Msg.PartMountState || id == Msg.BoltBatchV2
                || id == Msg.AuthorityClaim || id == Msg.FullStateSnapshot || id == Msg.PartRecovery
                || id == Msg.AuthoritySnapshot || id == Msg.SaveGameChunk || id == Msg.SaveGameComplete
                || id == Msg.ItemSpawn || id == Msg.TextChat || id == Msg.HostMigration
                || id == Msg.Ping;
        }

        /// <summary>
        /// Validates that the stream position is at the expected offset. Useful for
        /// detecting deserialization drift after reading variable-length fields.
        /// </summary>
        public static void AssertStreamPosition(BinaryReader reader, long expected, string context = "")
        {
            long actual = reader.BaseStream.Position;
            if (actual != expected)
            {
                throw new IOException(string.Format(
                    "Stream position mismatch{0}: expected {1}, got {2} (drift = {3} bytes)",
                    string.IsNullOrEmpty(context) ? "" : " (" + context + ")",
                    expected, actual, actual - expected));
            }
        }

        /// <summary>
        /// Validates that enough bytes remain in the stream for a read operation.
        /// </summary>
        public static void AssertBytesRemaining(BinaryReader reader, int bytesNeeded, string context = "")
        {
            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (remaining < bytesNeeded)
            {
                throw new EndOfStreamException(string.Format(
                    "Insufficient bytes{0}: need {1}, have {2}",
                    string.IsNullOrEmpty(context) ? "" : " (" + context + ")",
                    bytesNeeded, remaining));
            }
        }
    }
}
