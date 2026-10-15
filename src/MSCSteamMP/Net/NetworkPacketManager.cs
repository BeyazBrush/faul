using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Satsuma serialisation (pure writers, unit tested by tests/WireProbe.cs) plus the
    /// thin send helpers that wrap a writer in a packet and hand it to
    /// <see cref="SteamTransport"/>.
    ///
    /// The split matters: the writers are deterministic and pure, so the byte format can
    /// be verified without a game session, while the send helpers only decide
    /// reliability and telemetry. Everything here is reliable and idempotent: part and
    /// bolt state is repeated as absolute values, so a dropped packet is corrected by the
    /// next flush rather than accumulating error.
    /// </summary>
    public static class NetworkPacketManager
    {
        /// <summary>
        /// F03_PAGE_TOTAL: "bu offset'teki sayfa BOS muydu, yoksa
        /// GONDERILEMEDI mi?" ayrimi.
        ///
        /// Sayfalama dongusu `written == 0` gordugunde bir sonraki
        /// sayfayi dener; ama 0 yazmanin iki nedeni var:
        ///   (a) filtreye gecen satir kalmadi   -> dogal bitis, `break`
        ///   (b) `SendReliable` `false` dondu   -> sayfa KAYBOLMAMALI
        ///
        /// Bu metot (b) durumunu "bu sayfa yazilabiliyordu, sadece
        /// gonderilemedi" diyerek cikar. Boylece `SendAll` birkac kare
        /// bekleyip ayni sayfayi TEKRAR dener.
        ///
        /// DIKKAT: bu YENI bir ag paketi degil; yalnizca yerel bir
        /// sorgudur. Tel (wire) formatinda degisiklik YOKTUR.
        /// </summary>
        public static bool RetryOffset(int startIndex, int limit)
        {
            if (!SteamTransport.HasRemote) return false;
            List<SatsumaPart> table = SatsumaRegistry.PartsForPaging;
            if (table == null) return false;
            try
            {
                using (MemoryStream probe = new MemoryStream())
                {
                    BinaryWriter pw = new BinaryWriter(probe);
                    return WritePartBatch(pw, table, startIndex, limit) > 0;
                }
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Pure writers (wire format)
        // ------------------------------------------------------------------

        public static void WriteBolt(BinaryWriter w, string partId, byte index, float tightness, int stage, bool bolted)
        {
            WriteBolt(w, partId, index, tightness, stage, bolted, null);
        }

        /// <summary>
        /// One bolt record. <paramref name="boltPath"/> is the wire identity; see
        /// SatsumaBolt.Path for why the index alone is not enough (the two sides
        /// were on different saves: 378/337 vs 369/332, so the indices shifted and
        /// the wrong bolt got unscrewed). An empty path means "fall back to the
        /// index", which is what a peer on an older build sends.
        /// </summary>
        public static void WriteBolt(BinaryWriter w, string partId, byte index, float tightness, int stage, bool bolted, string boltPath)
        {
            w.Write(partId ?? "");
            w.Write(index);
            w.Write(tightness);

            // Stage is a signed byte on the wire. The FSM uses 0..8, so 120 is generous;
            // clamping here is what keeps a bad value from wrapping to a negative stage.
            sbyte clampedStage = (sbyte)Mathf.Clamp(stage, -128, 120);
            w.Write(clampedStage);
            w.Write(bolted);
            // Path last: an older build stops reading here, and the reader reads
            // whatever bytes are left, so this stays the final field.
            w.Write(boltPath ?? "");
        }

        public static int WriteBoltBatch(BinaryWriter w, List<SatsumaBolt> bolts)
        {
            int count = 0;
            foreach (var bolt in bolts)
            {
                if (bolt.Part == null) continue;
                bool bolted = bolt.Part.IsGroupBolted();
                WriteBolt(w, bolt.Part.Id, bolt.Index, bolt.TightnessValue, bolt.StageValue, bolted, bolt.Path);
                count++;
            }
            return count;
        }

        /// <summary>
        /// Writes one page of part states.
        ///
        /// <paramref name="startIndex"/> and <paramref name="limit"/> count *installable*
        /// entries, not raw table indices. Bolt containers sit in the table too but are not
        /// part records, and the initial sync pages through InstallableCount, so the two
        /// counters have to speak the same language or the host would resend part 0 forever.
        /// </summary>
        public static int WritePartBatch(BinaryWriter w, List<SatsumaPart> table, int startIndex, int limit)
        {
            int written = 0;
            int seen = 0;
            for (int i = 0; i < table.Count; i++) {
                // Bolt container'lar yoksayılır (Installable = false)
                if (!table[i].Installable) continue;
                // v22: montaj durumu OKUNAMAYAN parca GONDERILMEZ.
                //
                // NEDEN: `InstalledOf` artik FSM durum adindan TAHMIN
                // yapmiyor (dokum kaniti: 60 `Removal` FSM'inin hicbirinde
                // `Installed` bool'u yok, baslangic durumlari `Disable
                // trigger`/`Add mass`; 102 `Assembly`'nin baslangici
                // `Check for Part collision`/`Check Assembly`). Okunamayan
                // durum icin `false` doner -- ama "sokulmus" demek
                // DEGIL, BILINMIYOR demek.
                //
                // Guvenli tek yol: gonderme. Cunku `false` gondermek,
                // karsi tarafa "bu parca yok" demek olurdu ve o parca
                // orada takiliysa KAYBOLURDU.
                if (!SatsumaRegistry.HasKnownInstallState(table[i])) continue;
                if (seen++ < startIndex) continue;
                if (written >= limit) break;

                w.Write(table[i].Id ?? "");
                w.Write(SatsumaRegistry.InstalledOf(table[i]));
                // v15: `bolted` de kayitla gidiyor. Satsuma'daki 50
                // BoltCheck nesnesinin 38'i videosuz, yani vida kaydi
                // uretilmiyor; grup bayragi baska kanaldan tasinmazsa
                // karsi tarafta bayat kalip ikinci montaj turunu
                // tetikliyor (trigger "Check bolts" -> Data/Bolted).
                w.Write(table[i].IsGroupBolted());
                // v16 (b): yuva indeksi. Bakiniz SendPartState.
                w.Write(table[i].HasSlots ? table[i].FilledSlotIndex() : (byte)0);

                // v18 (a): hasar. Bakiniz SendPartState.
                w.Write(table[i].DamagedValue);                written++;
            }
            return written;
        }

        public static int CountInstallable(List<SatsumaPart> table)
        {
            int count = 0;
            foreach (var p in table) if (p.Installable) count++;
            return count;
        }

        /// <summary>
        /// Writes one page of NON-SATSUMA (store / boxed) part states.
        ///
        /// SECOND PATH, DELIBERATELY SEPARATE FROM WritePartBatch.
        /// WritePartBatch keeps `if (!table[i].Installable) continue;`
        /// exactly as it is today; that filter IS the Satsuma mount-slot
        /// contract, and the same flag is read by OwnerOf, StepVerify,
        /// InstalledOfObject and CountInstallable. This writer filters on
        /// SatsumaRegistry.IsSyncableStorePart instead, which is a separate
        /// marker, and that helper re-checks Installable so a row promoted
        /// to a mount slot mid-session is not carried on both paths.
        ///
        /// Paging uses the same `seen` / `written` shape as WritePartBatch,
        /// so startIndex counts store rows rather than raw table indices.
        ///
        /// The packet is Msg.PartBatch on purpose: a count byte, then that
        /// many (id, installed, bolted, slot) records. The receiver's
        /// ApplyPartBatch already loops those records into
        /// SatsumaRegistry.ApplyPartState(id, installed, bolted, slot), so
        /// there is no new handler and no new Msg id, and a peer on an older
        /// build applies the record through the code it already has.
        /// </summary>
        public static int WriteStorePartBatch(BinaryWriter w, List<SatsumaPart> table, int startIndex, int limit)
        {
            int written = 0;
            int seen = 0;
            for (int i = 0; i < table.Count; i++) {
                if (!SatsumaRegistry.IsSyncableStorePart(table[i])) continue;
                if (seen++ < startIndex) continue;
                if (written >= limit) break;

                w.Write(table[i].Id ?? "");
                w.Write(SatsumaRegistry.InstalledOf(table[i]));
                w.Write(table[i].IsGroupBolted());
                // Slot index: the expression is kept VERBATIM from
                // WritePartBatch. A non-Satsuma row is expected not to be
                // slotted -- the slot list is only fed by
                // DatabaseInstalledFlag on a mount trigger, and mount
                // triggers live under the Satsuma -- so 0 ("no slot") is
                // what these rows will send. Should a store row ever turn
                // out to be slotted, this still reports the real index
                // instead of flattening it to 0, which ApplySlot would
                // read as "clear every slot". Not asserted: unverified
                // without a live run.
                w.Write(table[i].HasSlots ? table[i].FilledSlotIndex() : (byte)0);

                // v18 (a): magaza parcalarinin hasari da tasinir.
                w.Write(table[i].DamagedValue);                written++;
            }
            return written;
        }

        /// <summary>
        /// Writes one page of `Database/*` part-flag rows: byte count, then that
        /// many (string id, bool installed, bool bolted, bool damaged) records.
        ///
        /// NEW MESSAGE, DELIBERATELY NOT A FIFTH FIELD ON PartBatch. A Database
        /// row is an IDENTITY record, not a mount slot, so there is no slot byte.
        /// PartBatch's record is (id, installed, bolted, slot, damaged) -- five
        /// fields. A v19 peer handed a Database page would read our `damaged`
        /// bool as the NEXT record's 7-bit string length: not a rejected packet
        /// but a silent stream desync that eats the rest of the page. So this is
        /// Msg.DatabaseBatch and PROTOCOL_VERSION moved 19 -> 20.
        ///
        /// PAGING mirrors WriteStorePartBatch exactly -- the same `seen` /
        /// `written` shape, so `startIndex` counts Database rows and not raw
        /// table indices. `SatsumaRegistry.DatabasePartCount` is the counter the
        /// page loop must agree with, exactly like StorePartCount agrees with
        /// WriteStorePartBatch's filter.
        ///
        /// WHICH BOOLS GO OUT. `installed` and `damaged` are read through the
        /// same accessors the PartBatch writers use (SatsumaRegistry.InstalledOf
        /// and SatsumaPart.DamagedValue); both already fall back to a safe
        /// default when the row has no writable flag, which is what "only the
        /// bools the caller says are bound" means here. `bolted` is
        /// IsGroupBolted(), identical to the Satsuma and store writers. There is
        /// deliberately NO slot byte and NO HasSlots branch.
        /// </summary>
        public static int WriteDatabaseBatch(BinaryWriter w, IList<SatsumaPart> table, int startIndex, int limit)
        {
            int written = 0;
            int seen = 0;
            for (int i = 0; i < table.Count; i++) {
                SatsumaPart row = table[i];
                if (row == null) continue;
                // Membership marker. Kept as an argument rather than inlined so
                // this file does not have to name a SatsumaRegistry member that
                // a concurrent patch owns; see SatsumaRegistry.IsSyncableDatabaseFlagPart.
                if (!SatsumaRegistry.IsSyncableDatabaseFlagPart(row)) continue;
                if (seen++ < startIndex) continue;
                if (written >= limit) break;

                w.Write(row.Id ?? "");
                // v20: `bolted` icin `IsGroupBolted()` DEGIL `DbBoltedValue`
                // kullanilir. `IsGroupBolted()` vida sayisi 0 ve `GroupBolted`
                // null ise SABIT true doner (bkz. SatsumaPart.cs:132-135) --
                // yani `Bolted` bildirmeyen bir Database satiri icin telde
                // UYDURMA "hepsi sikildi" gonderirdi. Dokum: 232 Data FSM'inin
                // 119'u hicbiri bildirmiyor, 9'u tek bayrakli. Bu yuzden
                // yalnizca gercekten bagli olan bayrak yazilir.
                w.Write(SatsumaRegistry.InstalledOf(row));
                w.Write(row.DbBoltedValue);
                w.Write(row.DamagedValue);
                written++;
            }
            return written;
        }

        // ------------------------------------------------------------------
        // Send helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// One part's installed bool with no accompanying event. This is the wiring-part
        /// path: an Assemble FSM only runs Init, so an event would never change anything on
        /// the peer, and the bool is the actual save state.
        /// </summary>
        public static bool SendPartState(string partId, bool installed, bool bolted, byte slot, bool damaged) {
            if (!SteamTransport.HasRemote) return false;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.PartState);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(partId ?? "");
                w.Write(installed);
                // v15: `bolted` artik parca kaydinda da tasiniyor.
                // Canli döküm kanıtı: Satsuma'daki 50 BoltCheck nesnesinin
                // 38'inin altinda HIC `Screw` FSM'i yok, yani vida kaydi
                // hic uretilmiyor ve `bolted` (daha once yalniz vida
                // kaydinda bir alan olarak) telde tasinamiyordu.
                w.Write(bolted);
                // v16 (b): `slot` = dolu olan yuvanin indeksi (1 tabanli).
                // Dokum kaniti: 6 trigger tek yuva degil, birden fazla
                // parcanin birbirinin yerine gectigi yuva tasiyor
                // (trigger_radiator PART1/PART2 -> db_Radiator1/2;
                //  trigger_steering_wheel PART1..5 -> db_WheelGT/Rally/
                // Sport/Stock). Tek bool "true" gondermek hangi yuvanin
                // dolu oldugunu tasimiyordu. 0 = yuva yok / tek yuvali.
                w.Write(slot);
                // v18 (a): parca hasari. Dokum kaniti:
                // `Database/*|Data` satirlari `Damaged` BILDIRIYOR ama
                // Satsuma icinde onu YAZAN bir FSM yok (okuyan 12 nokta:
                // Cooling, Powertrain, Transmission, FuelLine, Cylinders,
                // Electrics). Yazan tek yer `REPAIRSHOP/Jobs/EngineFix|Work`
                // -- yani hasar kaydi `Database` satirinda duruyor ve
                // kirilan vida `Bolted=false` yazarken `Damaged` DEGISMEZ.
                // Bu yuzden ayri bir alan gerekli.
                w.Write(damaged);
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) return false;
                Telemetry.SentPartStates++;
                return true;
            }
            catch (Exception e) {
                ModConsoleError("SendPartState: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Replay a live FSM event on the peer. The event, not the bool, is what makes the
        /// peer's own Assembly/Removal FSM do the real work (play sounds, move the part
        /// into the car); ApplyPartState only converges the value afterwards.
        /// </summary>
        public static bool SendPartEvent(string partId, string fsmName, string eventName) {
            if (!SteamTransport.HasRemote) return false;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.PartEvent);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(partId ?? "");
                w.Write(fsmName ?? "");
                w.Write(eventName ?? "");
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) return false;
                Telemetry.SentPartEvents++;
                return true;
            }
            catch (Exception e) {
                ModConsoleError("SendPartEvent: " + e.Message);
                return false;
            }
        }

        /// <summary>One packet for every bolt that changed since the last flush.</summary>
        public static bool SendBoltBatch(List<SatsumaBolt> bolts) {
            if (!SteamTransport.HasRemote || bolts == null || bolts.Count == 0) return false;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.BoltBatch);
                BinaryWriter w = new BinaryWriter(s);
                // Count goes first so the reader knows how many records to consume.
                int countPos = (int)s.Position;
                w.Write((byte)0);
                int written = WriteBoltBatch(w, bolts);
                w.Flush();
                PatchCountByte(s, w, countPos, written);
                if (written == 0) return false;
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) return false;
                Telemetry.SentBoltSyncs++;
                return true;
            }
            catch (Exception e) {
                ModConsoleError("SendBoltBatch: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// One page of the initial state push. The receiver reads a count byte then that
        /// many records, so the byte is written first and patched once the real count is
        /// known. Returns the number of records written, which the coroutine logs.
        /// </summary>
        public static int SendPartBatch(List<SatsumaPart> table, int startIndex, int limit) {
            if (!SteamTransport.HasRemote || table == null) return 0;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.PartBatch);
                BinaryWriter w = new BinaryWriter(s);
                int countPos = (int)s.Position;
                w.Write((byte)0);
                int written = WritePartBatch(w, table, startIndex, limit);
                w.Flush();
                PatchCountByte(s, w, countPos, written);
                if (written == 0) return 0;
                // F03_PAGE_TOTAL: gonderilemeyen sayfa "yazildi" sayilmaz.
                // Aksi halde `offset` ilerler ve o sayfa KALICI olarak
                // kaybolur -- karsi tarafa hic ulasmaz.
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) {
                    ModConsoleError("SendPartBatch: reliable send failed; page not sent.");
                    return 0;
                }
                return written;
            }
            catch (Exception e) {
                ModConsoleError("SendPartBatch: " + e.Message);
                return 0;
            }
        }

        /// <summary>
        /// One page of the non-Satsuma (store / boxed) part snapshot.
        ///
        /// Same packet, same count-byte patching and the same "always
        /// advance on failure" contract as SendPartBatch. The page size is
        /// SatsumaRegistry.STORE_PARTS_PER_FRAME (8), so the whole store
        /// table leaves over several frames rather than as one unbounded
        /// packet. Returns the number of records written.
        /// </summary>
        public static int SendStorePartBatch(List<SatsumaPart> table, int startIndex, int limit) {
            if (!SteamTransport.HasRemote || table == null) return 0;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.PartBatch);
                BinaryWriter w = new BinaryWriter(s);
                int countPos = (int)s.Position;
                w.Write((byte)0);
                int written = WriteStorePartBatch(w, table, startIndex, limit);
                w.Flush();
                PatchCountByte(s, w, countPos, written);
                if (written == 0) return 0;
                // F03_PAGE_TOTAL: gonderilemeyen sayfa "yazildi" sayilmaz.
                // Aksi halde `offset` ilerler ve o sayfa KALICI olarak
                // kaybolur -- karsi tarafa hic ulasmaz.
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) {
                    ModConsoleError("SendStorePartBatch: reliable send failed; page not sent.");
                    return 0;
                }
                return written;
            }
            catch (Exception e) {
                ModConsoleError("SendStorePartBatch: " + e.Message);
                return 0;
            }
        }

        /// <summary>
        /// One page of the `Database/*` part-flag snapshot.
        ///
        /// Same packet shape, same count-byte patching and the same "always
        /// advance on failure" contract as SendPartBatch /
        /// SendStorePartBatch -- the page loop lives in SatsumaRegistry and
        /// advances on the RETURN VALUE, so returning 0 here is how a failed
        /// page is reported. The page size is
        /// SatsumaRegistry.DATABASE_PARTS_PER_FRAME.
        ///
        /// SIZE: a record is a 7-bit-length-prefixed id plus three bools, so a
        /// typical `Database/wheel` id (~20 chars) is 1 + 20 + 3 = 24 bytes.
        /// At 8 rows per page that is 192 B plus the 6 B header, about 198 B --
        /// roughly 0.6% of Protocol.MAX_PACKET_SIZE (32 KB). The cap is only
        /// enforced on the RECEIVE side (SteamTransport.ProcessMessages drains
        /// and drops anything larger), and nothing here gets near it.
        /// </summary>
        public static int SendDatabaseBatch(IList<SatsumaPart> table, int startIndex, int limit) {
            if (!SteamTransport.HasRemote || table == null) return 0;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.DatabaseBatch);
                BinaryWriter w = new BinaryWriter(s);
                int countPos = (int)s.Position;
                w.Write((byte)0);
                int written = WriteDatabaseBatch(w, table, startIndex, limit);
                w.Flush();
                PatchCountByte(s, w, countPos, written);
                if (written == 0) return 0;
                // F03_PAGE_TOTAL: gonderilemeyen sayfa "yazildi" sayilmaz.
                // Aksi halde `offset` ilerler ve o sayfa KALICI olarak
                // kaybolur -- karsi tarafa hic ulasmaz.
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) {
                    ModConsoleError("SendDatabaseBatch: reliable send failed; page not sent.");
                    return 0;
                }
                return written;
            }
            catch (Exception e) {
                ModConsoleError("SendDatabaseBatch: " + e.Message);
                return 0;
            }
        }

        /// <summary>
        /// Bir kapı açıldı ya da kapandı: (yol, fsm adı, olay adı).
        ///
        /// NEDEN OLAY: bu build'de kapının okunabilir bir "açık" durumu yok.
        /// `CARPARTS/PartsCar/door left(Clone)` üzerindeki `Use` FSM'i OPEN ve
        /// CLOSE olaylarıyla durum değiştiriyor ve mesh'i kendi
        /// SetRotation / SetHingeJointLimits action'larıyla döndürüyor; bool'u
        /// yazmak durum makinesini ilerletmiyor. Karşı tarafta olayı
        /// oynatınca kilit, menteşe sınırları ve ses de oyunun kendi
        /// kodundan geliyor.
        ///
        /// Güvenilir kanal: kayıp bir OPEN kalıcı olarak açık bir kapı bırakır.
        /// </summary>
        public static bool SendDoorEvent(string path, string fsmName, string eventName) {
            if (!SteamTransport.HasRemote) return false;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(eventName)) return false;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.DoorEvent);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(path);
                w.Write(fsmName ?? "");
                w.Write(eventName);
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) return false;
                return true;
            }
            catch (Exception e) {
                ModConsoleError("SendDoorEvent: " + e.Message);
                return false;
            }
        }

        /// <summary>Client asks the host to push the whole state again.</summary>
        public static bool SendSyncRequest() {
            if (!SteamTransport.HasRemote) return false;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.SyncRequest);
                BinaryWriter w = new BinaryWriter(s);
                // The client's own table hash, so the host can log a mismatch immediately.
                w.Write(SatsumaRegistry.TableHash);
                w.Write(SatsumaRegistry.InstallableCount);
                w.Write(SatsumaRegistry.BoltCount);
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) return false;
                Telemetry.SentSyncRequests++;
                return true;
            }
            catch (Exception e) {
                ModConsoleError("SendSyncRequest: " + e.Message);
                return false;
            }
        }

        /// <summary>Host finished the initial push. The hash proves both tables agree.</summary>
        public static bool SendSyncDone(int partCount, int boltCount, uint tableHash) {
            if (!SteamTransport.HasRemote) return false;
            try {
                MemoryStream s = Protocol.Begin(Protocol.Msg.SyncDone);
                BinaryWriter w = new BinaryWriter(s);
                w.Write(partCount);
                w.Write(boltCount);
                w.Write(tableHash);
                if (!SteamTransport.SendReliable(Protocol.Finish(s))) return false;
                Telemetry.SentSyncDone++;
                return true;
            }
            catch (Exception e) {
                ModConsoleError("SendSyncDone: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Rewrites the reserved count byte after the records were appended.
        ///
        /// The stream is seeked back to the placeholder and then restored, because
        /// BinaryWriter over a MemoryStream writes straight through with no buffering.
        /// </summary>
        static void PatchCountByte(MemoryStream s, BinaryWriter w, int countPos, int written) {
            long end = s.Position;
            s.Position = countPos;
            w.Write((byte)Math.Min(written, 255));
            w.Flush();
            s.Position = end;
        }

        static void ModConsoleError(string msg) {
            SmpLog.Error(" "+ msg);
        }
    }
}