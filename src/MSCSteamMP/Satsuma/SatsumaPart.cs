using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// One row of the Satsuma table: a GameObject plus the FSM variables and bolt list
    /// that belong to it.
    ///
    /// The table is built once per world (see SatsumaRegistry.Install) and entries are
    /// never removed: the Assembly FSM re-parents a part on install, so a path-derived id
    /// would stop resolving mid-game if it were recomputed. Everything the network layer
    /// needs is therefore captured here at scan time and kept alive.
    /// </summary>
    public class SatsumaPart
    {
        /// <summary>Path relative to the Satsuma root. This is the wire id.</summary>
        public string Id;
        /// <summary>Leaf object name, used for the human readable dump and name fallback.</summary>
        public string Name;
        /// <summary>False for bolt containers: they have no install FSM of their own.</summary>
        public bool Installable;
        /// <summary>
        /// True when at least one install FSM (Assembly/Removal/Assemble/BoltCheck) is
        /// attached. Separates "a real part that is not an install slot" - a part still
        /// lying in a store box - from a bare <c>Bolts</c> container. Used to pick the
        /// nearest sensible owner for a bolt; see SatsumaRegistry.OwnerOf.
        /// </summary>
        public bool HasPartFsm {
            get {
                return Assembly != null || Removal != null || Assemble != null || BoltCheck != null;
            }
        }
        /// <summary>The live GameObject. Held so the table survives re-parenting.</summary>
        public GameObject Object;

        public List<SatsumaBolt> Bolts = new List<SatsumaBolt>();
        public List<FsmBool> InstalledVars = new List<FsmBool>();

        /// <summary>Install trigger FSM ("Assembly"), normally on a trigger_* child.</summary>
        public PlayMakerFSM Assembly;
        /// <summary>Removal FSM ("Removal"), on the part itself.</summary>
        public PlayMakerFSM Removal;
        /// <summary>Wiring part FSM ("Assemble"). Only ever runs its Init state.</summary>
        public PlayMakerFSM Assemble;
        /// <summary>Part level bolt roll-up FSM ("BoltCheck"). Declares the group variables.</summary>
        public PlayMakerFSM BoltCheck;

        /// <summary>
        /// The `Data` FSM this row was discovered through, or null.
        ///
        /// SET FROM ONE PLACE ONLY: the `fsmName == "Data" &&
        /// IsDatabaseFlagObject(go)` branch of SatsumaRegistry.Install /
        /// LateEntry, which routes through BindDatabaseFlag. So
        /// `Data != null` IS the "this object carries a Database Data
        /// FSM" test used to admit a row, and it stays true even if the
        /// flag bindings themselves are re-bound later.
        ///
        /// WHY NOT THE FOUR INSTALL FSMs: their ABSENCE is what the dump
        /// shows for the Database rows, but their PRESENCE would not
        /// prove a `Data` FSM existed. The test has to be on the field
        /// the scan actually set.
        /// </summary>
        public PlayMakerFSM Data;

        // ------------------------------------------------------------------
        // v15: Database/Data ground truth (see SatsumaRegistry.InstalledOf)
        //
        // Many mount triggers declare NO `Installed` bool of their own.
        // Dumper proof (SATSUMA/.../trigger_shock_fl [Assembly]):
        //   GetFsmBool(db_PartRequired1, "Data", "Installed")
        // The real flag lives on the GameObject held by that `db_*` FsmGameObject
        // variable. 99 of the 102 Satsuma Assembly triggers declare one.
        //
        // Resolved once (InstalledOf runs per part per flush) and cached here.
        // ------------------------------------------------------------------
        /// <summary>Cozum denendi mi? (false ise DB kaynagi yok, sezgisel okunur)</summary>
        public bool DbInstalledChecked;
        /// <summary>Cozulen gercek bayrak; null ise cozulemedi.</summary>
        public FsmBool DbInstalled;
        /// <summary>
        /// v16: cozumlenen `db_*` adinin AILESi. Dokum kaniti: `db_ThisPart`
        /// her iki ana ailede de EN SONDA bildiriliyor (BoltCheck 32/32,
        /// Removal 19/19), yani "ilk cozulen" kurali yanlis parcayi seciyordu.
        /// Artik oncelik sirasiyla secilir ve secilen aile burada gorunur.
        /// </summary>
        public SatsumaRegistry.DbFamily DbResolvedFamily = SatsumaRegistry.DbFamily.None;

        /// <summary>
        /// v18 (a): parcanin `Data/Damaged` bayragi.
        ///
        /// DOKUM KANITI: `Database/*|Data` satirlari
        /// `Usage, Water, Bolted, Damaged, Installed, ...` bildiriyor.
        /// `Damaged` AYRI bir bayrak: kirmilan vida `Bolted=false`
        /// yazar (BoltCheck "Remove other" ->
        /// `SetFsmBool db_ThisPart, Data, Bolted`), ama `Damaged`
        /// degismez. Satsuma icinde `Damaged` YAZAN FSM yok (okuyan 12
        /// nokta var: Cooling, Powertrain, Transmission, FuelLine,
        /// Cylinders, Electrics); yazan tek yer
        /// `REPAIRSHOP/Jobs/EngineFix|Work`. Yani kayit `Database`
        /// satirinda duruyor ve oradan okunmali.
        ///
        /// `InstalledVars`'a EKLENMEZ: `InstalledOf` oradan "kurulu mu"
        /// okuyor, hasar degil.
        /// </summary>
        public FsmBool DbDamaged;

        /// <summary>Yerel hasar degeri (okuma). Telde tasinan deger bu.</summary>
        public bool DamagedValue {
            get { try { return DbDamaged != null && DbDamaged.Value; } catch { return false; } }
        }

        // ------------------------------------------------------------------
        // v18 (b): WHICH Database flags are really bound on this row.
        //
        // DUMP GROUND TRUTH (the 241 objects under Database/):
        //   232 carry a `Data` FSM and nothing else, 9 carry no FSM at all.
        //   Of those 232: 45 declare all three of Installed/Bolted/Damaged,
        //   59 declare two, 9 declare one, and 119 declare NONE of the
        //   three. So "has this row anything to write" is a per-row
        //   answer, and a writer that emitted all three regardless would
        //   be inventing values the receiver has nowhere to put.
        //
        // DERIVED, NOT CACHED. The truth already lives in the three bound
        // references (InstalledVars / GroupBolted / DbDamaged); this is a
        // computed mask over them, so there is no second copy that can
        // fall out of step when a binding changes.
        //
        // WHY THE NAME IS COMPARED EXACTLY: FsmVars.Bool falls back to a
        // case-insensitive SUBSTRING search, and the Database rows declare
        // `UniqueTagBolted` / `UniqueTagDamaged` next to the real ones. A
        // null check alone would call that "has Bolted" and put a tag bit
        // on the wire as if it were part state. StringComparison.Ordinal
        // is what makes the mask mean "the game declared THIS flag".
        // ------------------------------------------------------------------
        /// <summary>`Data/Installed` is bound on this row.</summary>
        public const byte DBFLAG_INSTALLED = 1;
        /// <summary>`Data/Bolted` is bound on this row.</summary>
        public const byte DBFLAG_BOLTED = 2;
        /// <summary>`Data/Damaged` is bound on this row.</summary>
        public const byte DBFLAG_DAMAGED = 4;
        /// <summary>All three of them.</summary>
        public const byte DBFLAG_ALL = DBFLAG_INSTALLED | DBFLAG_BOLTED | DBFLAG_DAMAGED;

        /// <summary>
        /// Bit mask of the Database flags this row can actually carry on
        /// the wire. A sender must emit only the bits that are set.
        /// </summary>
        public byte DbFlagBits {
            get {
                byte bits = 0;
                // InstalledVars[0] and not "any of them":
                // SatsumaPart.Installed -- the value a writer sends for the
                // Installed bit -- reads [0], so the mask must describe
                // the same slot.
                if (InstalledVars.Count > 0 && IsFlagNamed(InstalledVars[0], "Installed")) bits |= DBFLAG_INSTALLED;
                if (IsFlagNamed(GroupBolted, "Bolted")) bits |= DBFLAG_BOLTED;
                if (IsFlagNamed(DbDamaged, "Damaged")) bits |= DBFLAG_DAMAGED;
                return bits;
            }
        }

        /// <summary>At least one of the three is bound: the row has a writable flag.</summary>
        public bool HasDbFlags { get { return DbFlagBits != 0; } }

        /// <summary>All three are bound. Counted for the install log.</summary>
        public bool HasAllDbFlags { get { return DbFlagBits == DBFLAG_ALL; } }

        /// <summary>
        /// The honest read of `Bolted`, for rows that HAVE the flag.
        ///
        /// NOT IsGroupBolted(): that helper answers "are all this part's
        /// bolts tight", not "what does this object's Bolted bool say".
        /// It returns a hard true for a part with no bolts and no
        /// GroupBolted -- so on a row that declares no `Bolted` at all it
        /// invents "all tight" -- and where bolts DO exist it answers from
        /// their stages, which is a different question again. Read this
        /// only where DBFLAG_BOLTED is set.
        /// </summary>
        public bool DbBoltedValue {
            get { try { return GroupBolted != null && GroupBolted.Value; } catch { return false; } }
        }

        /// <summary>Is this bound reference exactly the named Database flag? (Ordinal)</summary>
        static bool IsFlagNamed(FsmBool v, string name) {
            if (v == null || string.IsNullOrEmpty(name)) return false;
            try { return string.Equals(v.Name, name, System.StringComparison.Ordinal); }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // v16 (b): SLOTLU YUVA. Bazi trigger'lar tek yuva degil, birden
        // fazla parcANIN BIRBIRININ YERINE GECTIGI yuva tasir:
        //
        //   MiscParts/trigger_radiator            PART1/PART2  db_Radiator1, db_Radiator2
        //   Dashboard/trigger_gear_stick           PART1/PART2  db_ThisPart1, db_ThisPart2
        //   Body/trigger_hood                     PART1..5    db_ThisPart,  db_ThisPart2
        //   Body/trigger_grille                   PART1..7    db_Part1,     db_Part2
        //   Body/trigger_spoiler                  PART1..7    db_Part1,     db_Part2
        //   .../trigger_steering_wheel            PART1..5    db_WheelGT/Rally/Sport/Stock
        //
        // Dokum kaniti: `PARTn` adlari GLOBAL GECIS olaylari (ORN.
        // trigger_radiator dizisinde 226/227; `DISASSEMBLE` ve `LOOP`
        // yaninda) ve yuvalar `db_*` referanslariyla eslesiyor.
        //
        // ESKI DAVRANIS YETMEZDI: tek `bool` tasiniyordu. Iki yuvadan
        // biri doluyken "true" gondermek karsi tarafta HANGI yuvanin
        // dolulugunu bilmiyor; ek olarak `DatabaseInstalledFlag` yalniz
        // `db_Radiator1`'i cozuyordu (digeri listede ama oncelik sirasi
        // ilkini seciyor).
        //
        // `SlotFlags` her yuvanin `Data/Installed` bool'unu tutar.
        // `SlotIndex` gonderirken hangi yuvanin dolu oldugunu bildirir
        // (0 = yuva yok / tek yuvali parca, 1..n = yuva indeksi).
        // GONDERIM tarafi doldurur; ALICI tarafi o yuvayi zorlar.
        // ------------------------------------------------------------------
        /// <summary>Yuva indeksine gore `Data/Installed` bool'lari. Bos ise
        /// ya tek yuvali ya da cozulemeyen parca (bkz. DbInstalled).</summary>
        public List<FsmBool> SlotFlags = new List<FsmBool>();

        /// <summary>Dolu olan yuvanin indeksi (1 tabanli). 0 = yuva yok.
        /// PART1 = 1, PART2 = 2, ... Gonderimde okunur.</summary>
        public byte SlotIndex;

        /// <summary>Bu parca cok yuvali mi? (SlotFlags.Count &gt; 1)</summary>
        public bool HasSlots { get { return SlotFlags.Count > 1; } }

        /// <summary>Yuva adlari, yalniz `smp verbose` taniligi icin.</summary>
        public List<string> SlotNames = new List<string>();

        /// <summary>
        /// Hangi yuva dolu? 0 = hicbiri. Radyatorde 1 = standart,
        /// 2 = racing. Tek yuvali parcalarda da calisir (SlotIndex = 1).
        /// </summary>
        public byte FilledSlotIndex() {
			int n = SlotFlags.Count;
			for (int i = 0; i < n; i++) {
				try { if (SlotFlags[i] != null && SlotFlags[i].Value) return (byte)(i + 1); }
				catch { }
			}
			return 0;
		}

        /// <summary>Part level bolt roll-up ("BoltCheck"), sent with every bolt batch.</summary>
        public FsmFloat GroupTightness;
        public FsmBool GroupBolted;

        /// <summary>
        /// v28: bu parcada GERCEK bir grup `Bolted` bool'u
        /// BAGLANDI MI?
        ///
        /// DOKUM: 50 Satsuma `BoltCheck` kaydinin HICBIRI ciplak
        /// `Bolted` bildirmiyor; hepsi `BoltedYES`/`BoltedNO` OLAYI +
        /// `Tightness` float'i + `db_ThisPart` nesne referansi tasiyor.
        /// Oyunun gercek yazma hedefi kardes `Data` FSM'idir.
        ///
        /// Bu bayrak `false` iken `IsGroupBolted()` telde bayrak
        /// YAZMAZ; alici taraf kendi vidalarindan turer.
        /// </summary>
        public bool HasGroupBoltFlag;

        /// <summary>
        /// How many bolt slots this part's `BoltCheck` declares, read from the
        /// `Detach1..DetachN` FsmBool set. 0 when the FSM declares none, which
        /// is the NORMAL case: 40 of the 50 Satsuma BoltCheck FSMs declare none.
        ///
        /// WHAT `Detach*` IS (dumper ground truth, do not re-derive): the
        /// `Bolts OFF` state runs `GetChild` x2 with tag `Untagged` and name
        /// `DetachN`, then `SetFsmBool` x6 writing, in order, `Detach3,
        /// Detach4, Detach1, Detach1, Detach2, Detach3, Detach4`, then sends
        /// `Detach` to the paired `Removal` FSM, which declares a single
        /// `Detach` FsmBool as the switch. So `DetachN` is the game's own
        /// PER-BOLT INDEX SET - one bool per bolt slot, resolved from a child's
        /// NAME - not a GameObject array and not a count of Bolt objects.
        ///
        /// Measured distribution over the 50 BoltCheck bodies:
        ///   0 on 40 parts, 3 on 2, 4 on 6, 6 on 2.
        /// Occurrence kinds dump-wide: FsmBool 87, other 42.
        ///
        /// WHY NOT `db_BoltThis*`, WHICH LOOKS LIKE THE OBVIOUS ANSWER: 32 of
        /// the 50 BoltCheck FSMs DECLARE `db_BoltThis1/2/3`, and ZERO of those
        /// 32 bodies reference any `db_BoltThis*` anywhere; `db_BoltThis3` is
        /// referenced by zero FSMs dump-wide. They are three null FsmGameObject
        /// variables, so bolt ownership derived from them would be unfounded.
        /// They stay unread. `DbFamilyOf` still labels the PREFIX as Bolt for
        /// the db_* resolution histogram; that is name classification only and
        /// binds nothing.
        ///
        /// THIS IS A CAPACITY NUMBER, NOT A BOLT COUNT. One `BoltPM` (FSM
        /// `Screw`) is a generic up-to-9-position driver: it finds a child
        /// tagged `Bolt`, derives its slot from the child NAME via GetName ->
        /// GetStringRight -> ConvertStringToInt -> ArrayIndex, and writes ONE
        /// `Stage` (SetFsmInt on PartAssembled) and ONE `Tightness` (AddFsmFloat
        /// on PartAssembled) into the PARENT's BoltCheck. All 60 genuine Satsuma
        /// BoltPM objects carry the byte-identical tail
        /// BoltSize/Scroll/Index/Stage/ArrayIndex/Bolt/PartAssembled/ThisBolt/
        /// Collider/SphereCollider and the identical 0..8 stage-event set.
        ///
        /// SO NO SatsumaBolt IS FABRICATED FROM THIS. One BoltPM writes one
        /// Stage and one Tightness, so manufacturing N records from one BoltPM
        /// would put N records on the wire for N-1 variables that do not exist
        /// - strictly worse than the present under-count. Whether one BoltPM
        /// really does drive N bolts is a MEDIUM-confidence reading from strings
        /// alone; a single-BoltPM reading (9 = tightening sub-steps) cannot be
        /// excluded. The real bolt count is the RUNTIME LENGTH of the `Bolt`
        /// array, and an array length is not a string, so it is not in the dump.
        /// </summary>
        public int DetachSlots;

        /// <summary>
        /// Bolts the table actually bound vs. slots the game declares:
        /// max(Bolts.Count, DetachSlots). DIAGNOSTIC ONLY.
        ///
        /// It gates nothing, is not serialised, and adds no SatsumaBolt record.
        /// Bolt.Path stays the wire identity and Bolt.Index stays the wire
        /// fallback; this property never touches either.
        /// </summary>
        public int BoltCapacity {
            get { return Bolts.Count > DetachSlots ? Bolts.Count : DetachSlots; }
        }

        // ------------------------------------------------------------------
        // Outbound dirty state
        // ------------------------------------------------------------------

        /// <summary>An event is waiting to be replayed on the peer. Wins over the bool.</summary>
        public string PendingEvent;
        /// <summary>Which FSM declared <see cref="PendingEvent"/>, so the peer picks the same one.</summary>
        public string PendingFsm;
        /// <summary>The installed bool changed and still needs to go out.</summary>
        public bool StateDirty;
    /// <summary>
    /// StateDirty kurulurken gonderilecek deger. `Installed` ham bool'dur
    /// (bool'suz parcalarda hep false) ve flush'ta YANLIS yon gonderirdi;
    /// bu alan olayin/bool'un soyledigi GERCEK hedeftir.
    /// </summary>
    public bool StateWant = true;

        /// <summary>Save state: the installed bool. Ground truth, unlike an event alone.</summary>
        public bool Installed {
            get { return InstalledVars.Count > 0 && InstalledVars[0].Value; }
        }

        /// <summary>How many of this part's bolts are fully tightened (stage 8).</summary>
        public int TightBoltCount {
            get {
                int n = 0;
                for (int i = 0; i < Bolts.Count; i++) {
                    if (Bolts[i].StageValue >= 8) n++;
                }
                return n;
            }
        }

        /// <summary>
        /// Whether every bolt on this part is tight. Travels with each bolt record so the
        /// peer's BoltCheck roll-up converges without a separate message.
        /// </summary>
        public bool IsGroupBolted()
        {
            // v15: GERCEK `Bolted` bool'u ONCE.
            //
            // Canlı döküm kanıtı (SATSUMA altında 50 BoltCheck):
            //   altında `Screw` FSM'i OLAN  : 12
            //   altında `Screw` FSM'i OLMAYAN: 38
            //
            // O 38 parçanın HEPSİ `BoltCheck` FSM'inde şunları
            // bildiriyor:
            //   Bolted, BoltedYES, BoltedNO, Bolts, Tightness, Data
            //
            // Yani oyunun kendi "hepsi sıkıldı mı" bayrağı duruyor ve
            // `GroupBolted` zaten bağlıyor (SatsumaRegistry.cs:1073,
            // FsmVars.Bool(fsm, "Bolted")).
            //
            // ESKİ kısa yol (Bolts.Count == 0 -> true) bu 38 parçaya
            // koşulsuz SABİT true gönderiyordu: telde `bolted=true`,
            // oyunda vida gevşekken de aynı şey söylüyordu.
            //
            // Düzeltme: vidası GÖRÜNMEYEN parçada oyunun kendi
            // bayrağı esas alınır (yoksa eski sabit davranış).
            // Vidası görünenlerde mevcut hesap korunur — çünkü telde
            // giden PER-VIDA stage'dir ve alıcı taraf grup toplamını
            // o kademelerden yeniden türetir.
            // SMP_GROUPBOLTED_V28
            if (Bolts.Count == 0) {
                if (GroupBolted != null) { try { return GroupBolted.Value; } catch { } }
                // v28: `GroupBolted` null ise SABIT `true` UYDURMA yapiyordu.
                //
                // DOKUM KANITI: 50 Satsuma `BoltCheck` kaydinin HICBIRI
                // ciplak `Bolted` bildirmiyor. Ornek kuyruk
                // (`Chassis/FR/strut fr rally(xxxxx)|BoltCheck`, nstr=320,
                // token 305-319):
                //   BoltedNO, BoltedYES, Chance, Tightness, Part,
                //   UniqueTagArray, UniqueTagFloat,
                //   Detach1, Detach2, Detach3, Detach4,
                //   db_BoltThis1, db_BoltThis2, db_BoltThis3, db_ThisPart
                // `BoltedYES`/`BoltedNO` OLAYDIR (FsmEvent), bool degil.
                // Oyun `SetFsmBool{fsmName='Data', variableName='Bolted'}`
                // ile KARDES `Data` FSM'ine yaziyor.
                //
                // Sonuc: `FsmVars.Bool(fsm,"Bolted")` 50/50 `null` doner,
                // `GroupBolted` hep null kalir ve bu satir `true`
                // UYDURURDU. Telde `bolted=true` yazildigi icin karsi
                // taraf "hepsi siki" saniyor -- vidalar gevsekken de.
                //
                // v28: bayrak YAZMA; alici taraf kendi vidalarindan
                // turetir. `HasGroupBoltFlag` yalniz GERCEK bir
                // `Bolted` bool'u baglanmissa true doner.
                return HasGroupBoltFlag;
            }
            for (int i = 0; i < Bolts.Count; i++) {
                if (Bolts[i].StageValue < 8) return false; // Varsayılan max sıkılık
            }
            return true;
        }

        /// <summary>
        /// Bu parça vida gerektirmiyor mu? (sadece ASSEMBLE/Assemble FSM'i var, BoltCheck yok)
        /// Bujiler, kablolar, mandal gibi vidasız montaj parçaları için true döner.
        /// </summary>
        public bool IsBoltlessPart()
        {
            // Vida sayısı 0 VE en az bir montaj FSM'i varsa vidasız parçadır
            return Bolts.Count == 0 && (Assembly != null || Assemble != null);
        }
    }

    /// <summary>
    /// One bolt ("BoltPM" with the FSM "Screw"). Stage is the discrete level the FSM
    /// state machine compares against, Tightness is the drawn value.
    /// </summary>
    public class SatsumaBolt
    {
        /// <summary>Owning part. Null means the bolt was dropped from the table.</summary>
        public SatsumaPart Part;
        /// <summary>Index within the owning part's sorted bolt list. Wire fallback.</summary>
        public byte Index;
        /// <summary>
        /// Wire identity: the bolt's own path relative to the Satsuma root.
        ///
        /// WHY NOT THE INDEX (live log evidence): the host reported 378 parts /
        /// 337 bolts while the peer reported 369 / 332 — DIFFERENT SAVES. When a
        /// part's bolt count differs, the indices shift and the sender's bolt #3
        /// lands on the receiver's bolt #2, which unscrews the wrong bolt. That
        /// was the report: "the parts' bolts come loose". A path is independent
        /// of the save, so two sides on the same game build match bolts correctly
        /// even with different saves; the index stays only as a fallback for a
        /// packet written by an older build (empty path).
        /// </summary>
        public string Path;
        public FsmInt Stage;
        public FsmFloat Tightness;
        /// <summary>The bolt's own FSM ("Screw"), kept so a remote change can be driven into it.</summary>
        public PlayMakerFSM Screw;

        /// <summary>Set by the write hooks, cleared by the flush that actually sent it.</summary>
        public bool Dirty;

        /// <summary>
        /// When a remote value landed on this bolt, so the tween the peer FSM is about
        /// to run is not captured and echoed straight back.
        ///
        /// NEDEN GEREKLİ: TIGHTEN/UNTIGHTEN tetiklendiğinde FSM, `Tightness`'i birkaç
        /// kare boyunca AddFsmFloat ile tween eder. Tek bir SuppressScope yalnızca
        /// olayın tetiklendiği anı kapsar, tween'i değil; bu yüzden kısa bir pencere
        /// gerekiyor. WorldObjectSync'in RemoteOwned + RESEND_COOLDOWN deseniyle aynı
        /// fikir.
        /// </summary>
        public float RemoteSetAt = float.NegativeInfinity;

        /// <summary>
        /// Karşı tarafın bildirdiği kademe; -1 = uzlaştırma yok.
        /// Yerel Screw FSM'i bu değere TIGHTEN/UNTIGHTEN ile yürütülür.
        /// </summary>
        public int PendingStage = -1;

        /// <summary>
        /// Cıvatanın dirty olduğu andan beri geçen sürenin başlangıcı.
        ///
        /// SESSİZLİK KAÇIŞ YOLU: bir FSM teorik olarak sonsuza kadar
        /// Tightness yazabilir (beklemede kalmış bir Wait ya da bir tween
        /// döngüsü). O zaman LastLocalWriteAt sürekli tazelenir ve cıvata
        /// hiç gönderilmez — kullanıcı bunu "vida senkronu çalışmıyor"
        /// sanır. Bu zaman damgası, BOLT_MAX_QUIET aşılınca sessizlik
        /// kuralının geçersiz kılınmasını sağlar.
        /// </summary>
        public float DirtySince = float.NegativeInfinity;

        /// <summary>
        /// Son YEREL (insan) yazımın zamanı. Fare tekerleği sürüklenirken
        /// FSM her karede burayı günceller, böylece Collect cıvatanın
        /// "sıkılıyor" değil "durdu" durumunu ayırt edebiliyor.
        ///
        /// SADECE insan yazımları burayı günceller. Karşı tarafın kendi
        /// tween'i SuppressScope bittikten SONRA birkaç kare daha yazmaya
        /// devam eder; bu yüzden güncelleme REMOTE_ECHO_WINDOW dışında
        /// yapılır, aksi halde karşı tarafın cıvatası sürekli "meşgul" görünür
        /// ve hiç gönderilmez.
        /// </summary>
        public float LastLocalWriteAt = float.NegativeInfinity;

        /// <summary>
        /// How many reconciliation steps have been fired for this bolt. Bounded so a
        /// Screw FSM that refuses to advance cannot hold a queue slot forever; see
        /// SatsumaRegistry.RECONCILE_MAX_TRIES.
        /// </summary>
        public int ReconcileTries;

        /// <summary>
        /// Currently sitting in the reconciliation queue. Exists so queueing is O(1):
        /// List.Contains is O(n), and the initial sync queues ~540 bolts at once, which
        /// would turn one linear scan per record into a quadratic one.
        /// </summary>
        public bool InQueue;

        /// <summary>Last values that went out, so the poller can skip unchanged bolts.</summary>
        public int LastSentStage = -1;
        public float LastSentTightness = float.NaN;
        public float LastSentAt = float.NegativeInfinity;

		/// <summary>
		/// Eylem günlüğü için son görülen kademe. -1 = henüz ölçülmedi.
		/// ("vida sıkınca bir şey oluyor mu?" teşhisi)
		/// </summary>
		public int TracedStage = -1;

        public int StageValue { get { return Stage != null ? Stage.Value : 0; } }
        /// <summary>
        /// Cıvatanın çizilen sıkılık değeri.
        ///
        /// v15: BU BUILD'DE `Screw` FSM'ININ KENDI `Tightness` FsmFloat'I
        /// YOK. Dumper kanıtı (docs/fsm_detail/l2__BoltPM__Screw.txt):
        /// bildirilenler BoltSize, Scroll, Index, Stage, ArrayIndex, Bolt,
        /// PartAssembled, ThisBolt, Collider. "Tightness" metni yalnız
        /// AddFsmFloat aksiyonlarının HEDEF variableName'i olarak geçiyor
        /// (satır 358-366, 384-392) ve üstteki `BoltCheck` FSM'ine yazıyor.
        ///
        /// ESKİ DAVRANIŞ: `Tightness` null dönünce sabit 0f dönüyordu, yani
        /// WriteBolt telde her vida için 0 gönderiyordu ve ApplyBolt'un
        /// `if (bolt.Tightness != null)` yazımı kalıcı no-op'tu. Kısacası
        /// sıkılık telde hiç taşınmıyor, sadece kademe (Stage) vardı.
        ///
        /// YENİ DAVRANIŞ: gerçek ayrık düzey ZATEN Stage (FsmInt 0..8)
        /// olduğu için, sıkılık Stage'den türetilir. Böylece tel dolu
        /// gider, ve `IsGroupBolted()` ile Stage zaten aynı 8 eşiğini
        /// kullanıyor -- iki alan da aynı gerçeği gösterir.
        /// </summary>
        public float TightnessValue {
            get {
                if (Tightness != null) return Tightness.Value;
                if (Stage != null) return Stage.Value;
                return 0f;
            }
        }
    }
}