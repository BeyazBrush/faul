using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using SteamMP;

namespace WireProbe {

	public static class Program {
		/// <summary>
		/// KAYIT AKTARIMI (SaveGameChunk/Complete) donusu.
		///
		/// NEDEN BU TEST KRİTİK: bu hat oyuncunun KENDİ KAYIT DOSYASINI
		/// eziyor. Format sessizce bozulursa (parça basligi kayarsa, veri
		/// tasarsa) ya dosya eksik yazilir ya da CRC32 yanlis hesaplanir.
		/// CRC32 dogrulamasi son direk, ama O da dogru hesaplanmali -- bu yuzden
		/// bilinen bir dizinin CRC32'si burada sabitlenir.
		/// </summary>
		static void RunSaveTransfer() {
			Console.WriteLine("--- save transfer ---");

			// 'a'..'e' (5 bayt) + 20 sifir: 25 bayt, 8 KB'lik parcalarla 1 parça.
			byte[] payload = new byte[SaveTransfer.CHUNK_BYTES + 25];
			for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
			uint crc = Crc32Of(payload);

			// --- Parça donusu (tek parça, tam boy) ---
			MemoryStream s = Protocol.Begin(Protocol.Msg.SaveGameChunk);
			BinaryWriter w = new BinaryWriter(s);
			Protocol.WriteSaveChunk(w, new Protocol.SaveChunkData {
				FileSize = (uint)payload.Length,
				Crc32 = crc,
				Index = 0,
				Count = 1,
				Data = payload,
				Offset = 0,
				Length = payload.Length
			});
			byte[] raw = Protocol.Finish(s);
			MemoryStream m = new MemoryStream(raw);
			BinaryReader r = new BinaryReader(m);
			Protocol.Msg id;
			Check("save chunk header", Protocol.TryReadHeader(r, out id), true);
			Check("SaveGameChunk id is pinned", (int)id, (int)Protocol.Msg.SaveGameChunk);
			Check("save chunk payload size",
				(int)(m.Length - Protocol.HEADER_SIZE),
				Protocol.SAVE_CHUNK_HEADER + payload.Length);

			Protocol.SaveChunkData got;
			Protocol.ReadSaveChunk(r, out got);
			Check("save chunk fileSize", got.FileSize, (uint)payload.Length);
			Check("save chunk crc", got.Crc32, crc);
			Check("save chunk index", (int)got.Index, 0);
			Check("save chunk count", (int)got.Count, 1);
			Check("save chunk length", got.Length, payload.Length);
			// Veri bayt bayt ayni mi? (parça basligi kaydiysa bu FAIL olur.)
			bool same = got.Data != null && got.Data.Length == payload.Length;
			for (int i = 0; same && i < payload.Length; i++)
				if (got.Data[i] != payload[i]) same = false;
			Check("save chunk data identical", same, true);
			Check("save chunk stream fully consumed", m.Position, m.Length);

			// --- Çok parçalı: ilk ve son parça ---
			byte[] big = new byte[SaveTransfer.CHUNK_BYTES * 2 + 100];
			for (int i = 0; i < big.Length; i++) big[i] = (byte)((i * 7) % 253);
			uint crcBig = Crc32Of(big);
			int total = (big.Length + SaveTransfer.CHUNK_BYTES - 1) / SaveTransfer.CHUNK_BYTES;
			Check("save split into 3 chunks", total, 3);

			for (int idx = 0; idx < total; idx++) {
				int off = idx * SaveTransfer.CHUNK_BYTES;
				int len = big.Length - off;
				if (len > SaveTransfer.CHUNK_BYTES) len = SaveTransfer.CHUNK_BYTES;
				MemoryStream cs = Protocol.Begin(Protocol.Msg.SaveGameChunk);
				BinaryWriter cw = new BinaryWriter(cs);
				Protocol.WriteSaveChunk(cw, new Protocol.SaveChunkData {
					FileSize = (uint)big.Length, Crc32 = crcBig,
					Index = (ushort)idx, Count = (ushort)total,
					Data = big, Offset = off, Length = len
				});
				MemoryStream cm = new MemoryStream(Protocol.Finish(cs));
				BinaryReader cr_ = new BinaryReader(cm);
				Protocol.Msg cid;
				Check("chunk " + idx + " header", Protocol.TryReadHeader(cr_, out cid), true);
				Protocol.SaveChunkData cg;
				Protocol.ReadSaveChunk(cr_, out cg);
				bool ok = cg.Index == idx && cg.Count == total && cg.Length == len
					&& cg.FileSize == big.Length && cg.Crc32 == crcBig;
				// DİKKAT: `ReadSaveChunk` veriyi KENDİ tamponuna yazar ve
				// `Offset`'i 0 yapar. Yani karşılaştırma YAZIM anındaki
				// offset ile olmalı (`off`), okunan `cg.Offset` ile DEĞİL.
				for (int i = 0; ok && i < cg.Length; i++)
					if (cg.Data[i] != big[off + i]) ok = false;
				Check("chunk " + idx + " round trip", ok, true);
				Check("chunk " + idx + " consumed", cm.Position, cm.Length);
			}

			// --- Kapanış / istek paketi ---
			MemoryStream ds = Protocol.Begin(Protocol.Msg.SaveGameComplete);
			BinaryWriter dw = new BinaryWriter(ds);
			Protocol.WriteSaveComplete(dw, new Protocol.SaveCompleteData {
				FileSize = (uint)big.Length, Crc32 = crcBig, Ok = true, Message = "sent"
			});
			MemoryStream dm = new MemoryStream(Protocol.Finish(ds));
			BinaryReader dr = new BinaryReader(dm);
			Protocol.Msg did;
			Check("save complete header", Protocol.TryReadHeader(dr, out did), true);
			Check("SaveGameComplete id is pinned", (int)did, (int)Protocol.Msg.SaveGameComplete);
			Protocol.SaveCompleteData dg;
			Protocol.ReadSaveComplete(dr, out dg);
			Check("complete ok", dg.Ok, true);
			Check("complete message", dg.Message, "sent");
			Check("complete fileSize", dg.FileSize, (uint)big.Length);
			Check("complete crc", dg.Crc32, crcBig);
			Check("complete stream fully consumed", dm.Position, dm.Length);

			// İstek yönü: ok=false, message="request" — host bunu görünce gönderir.
			MemoryStream qs = Protocol.Begin(Protocol.Msg.SaveGameComplete);
			BinaryWriter qw = new BinaryWriter(qs);
			Protocol.WriteSaveComplete(qw, new Protocol.SaveCompleteData {
				FileSize = 0, Crc32 = 0, Ok = false, Message = "request"
			});
			MemoryStream qm = new MemoryStream(Protocol.Finish(qs));
			BinaryReader qr = new BinaryReader(qm);
			Protocol.Msg qid;
			Check("request header", Protocol.TryReadHeader(qr, out qid), true);
			Protocol.SaveCompleteData qg;
			Protocol.ReadSaveComplete(qr, out qg);
			Check("request ok=false", qg.Ok, false);
			Check("request message", qg.Message, "request");
			Check("request stream fully consumed",qm.Position, qm.Length);

			// --- CRC32 bilinen deger: sıfırlanmamali ---
			// Implementasyonun STANDART CRC-32 (polinom 0xEDB88320) olduğu
			// kanonik vektörle doğrulandı: "123456789" -> 0xCBF43926.
			// Aşağıdaki sabit de aynı uygulamayla bağımsız olarak üretildi.
			Check("crc32 of 5 known bytes", (uint)Crc32Of(new byte[] { 1, 2, 3, 4, 5 }), 1191942644u);
			Check("crc32 of empty", (uint)Crc32Of(new byte[0]), 0u);
			Check("crc32 canonical vector",
				(uint)Crc32Of(System.Text.Encoding.ASCII.GetBytes("123456789")), 0xCBF43926u);
			Check("crc32 stable across calls", (uint)Crc32Of(payload), crc);
		}

		/// <summary>Testin kendi CRC32'si (SaveTransfer'dakiyle ayni polinom).</summary>
		static uint Crc32Of(byte[] buf) {
			uint[] t = new uint[256];
			for (uint n = 0; n < 256; n++) {
				uint c = n;
				for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
				t[n] = c;
			}
			uint c2 = 0xFFFFFFFFu;
			for (int i = 0; i < buf.Length; i++) c2 = t[(c2 ^ buf[i]) & 0xFF] ^ (c2 >> 8);
			return c2 ^ 0xFFFFFFFFu;
		}

		static int failures;

		/// <summary>
		/// KAPI / DÜNYA NESNESİ / MOTOR tel bicimleri.
		///
		/// Bu üc test EKSIKTI ve eksikleri fark edilmeden onceki turda elle
		/// dogrulandi: `SendDoorEvent`, `WorldObjectSync.Flush` (ObjectState)
		/// ve `VehicleRegistry.SendEngine` (Engine) yazim zincirleri ile
		/// alicilari (`OnDoorMessage`, `OnMessage`, `Msg.Engine` dalı) arasinda
		/// tek dogruluk kaynagi yorumdu.
		///
		/// Bir sonraki turda alan eklenip yalnizca bir tarafa yazilirsa bu
		/// testler kirilir -- "stream fully consumed" kontrolu tasmayi yakalar.
		/// </summary>
		static void RunDoorAndWorld() {
			Console.WriteLine("--- door / world / engine wire ---");

			// ---- DoorEvent: (string path, string fsmName, string event) ----
			// SendDoorEvent (NetworkPacketManager:479) AYNEN taklit edilir.
			MemoryStream d = Protocol.Begin(Protocol.Msg.DoorEvent);
			BinaryWriter dw = new BinaryWriter(d);
			dw.Write("YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle");
			dw.Write("Use");
			dw.Write("OPEN");
			dw.Flush();
			MemoryStream dIn = new MemoryStream(Protocol.Finish(d));
			BinaryReader dr = new BinaryReader(dIn);
			Protocol.Msg did;
			Check("door header", Protocol.TryReadHeader(dr, out did), true);
			Check("DoorEvent id is pinned", (int)did, (int)Protocol.Msg.DoorEvent);
			// OnDoorMessage (WorldObjectSync:3337) AYNEN bu sirayla okur.
			Check("door path", dr.ReadString(),
				"YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle");
			Check("door fsm", dr.ReadString(), "Use");
			Check("door event", dr.ReadString(), "OPEN");
			Check("door stream fully consumed", dr.BaseStream.Position, dIn.Length);

			// Salter: `USE:1` / `USE:0` mutlak durum tasir (OnDoorMessage:3362).
			// Not: salter de KAPI hattiyla gider (ayni Msg.DoorEvent) -- ayri
			// bir `SwitchState` mesaji YOK ve olmamali: canli yol zaten bu.
			MemoryStream sw = Protocol.Begin(Protocol.Msg.DoorEvent);
			BinaryWriter sww = new BinaryWriter(sw);
			sww.Write("HOUSE/LIVINGROOM/Switch/MainSwitch");
			sww.Write("Use");        // fsmName — ikinci alan, UNUTULMAMALI
			sww.Write("USE:1");      // mutlak durum: 1 = acik, 0 = kapali
			sww.Flush();
			MemoryStream swIn = new MemoryStream(Protocol.Finish(sw));
			BinaryReader sr = new BinaryReader(swIn);
			Protocol.Msg sid;
			Check("switch-as-door header", Protocol.TryReadHeader(sr, out sid), true);
			Check("switch path", sr.ReadString(), "HOUSE/LIVINGROOM/Switch/MainSwitch");
			Check("switch fsm", sr.ReadString(), "Use");
			Check("switch state event carries absolute value", sr.ReadString(), "USE:1");
			Check("switch stream fully consumed", sr.BaseStream.Position, swIn.Length);

			// ---- Engine: (int vehicleId, byte engine, byte dash, float startTime) ----
			// SendEngine (VehicleRegistry:133) ile birebir ayni sira/tipler.
			MemoryStream eg = Protocol.Begin(Protocol.Msg.Engine);
			BinaryWriter egw = new BinaryWriter(eg);
			egw.Write(4242);
			egw.Write((byte)3);       // engine state
			egw.Write((byte)7);       // dashboard state
			egw.Write(1.5f);           // startTime
			egw.Flush();
			MemoryStream egIn = new MemoryStream(Protocol.Finish(eg));
			BinaryReader egr = new BinaryReader(egIn);
			Protocol.Msg egid;
			Check("engine header", Protocol.TryReadHeader(egr, out egid), true);
			Check("Engine id is pinned", (int)egid, (int)Protocol.Msg.Engine);
			// Msg.Engine dalı (VehicleRegistry:309) AYNEN bu sirayla okur.
			Check("engine vehicleId", egr.ReadInt32(), 4242);
			Check("engine state (byte)", (int)egr.ReadByte(), 3);
			Check("engine dash (byte)", (int)egr.ReadByte(), 7);
			Check("engine startTime", (float)egr.ReadSingle(), 1.5f);
			Check("engine stream fully consumed", egr.BaseStream.Position, egIn.Length);

			// ---- ObjectState: count, then {path, bool{(name,val)}, float{(name,val)}, pingMs} ----
			// WorldObjectSync.Flush (2802) ile birebir ayni bicim.
			MemoryStream os = Protocol.Begin(Protocol.Msg.ObjectState);
			BinaryWriter osw = new BinaryWriter(os);
			osw.Write((byte)2);                       // iki izlenen nesne
			// nesne 1: iki bool + bir float + ping
			osw.Write("YARD/Building/BATHROOM/DoorBathroom/Door");   // path
			osw.Write((byte)2);                                      // bool adedi
			osw.Write("Open"); osw.Write(true);
			osw.Write("Locked"); osw.Write(false);
			osw.Write((byte)1);                                      // float adedi
			osw.Write("Angle"); osw.Write(61.25f);
			osw.Write(35f);                                          // PingMs
			// nesne 2: bool yok, bir float + ping
			osw.Write("KITCHEN/WaterTap/Tap");
			osw.Write((byte)0);
			osw.Write((byte)1);
			osw.Write("Water"); osw.Write(0.75f);
			osw.Write(12f);
			osw.Flush();
			MemoryStream osIn = new MemoryStream(Protocol.Finish(os));
			BinaryReader osr = new BinaryReader(osIn);
			Protocol.Msg osid;
			Check("object state header", Protocol.TryReadHeader(osr, out osid), true);
			Check("ObjectState id is pinned", (int)osid, (int)Protocol.Msg.ObjectState);
			// OnMessage (WorldObjectSync:3236) AYNEN bu sirayla okur.
			byte nObj = osr.ReadByte();
			Check("object state record count", (int)nObj, 2);
			Check("obj 0 path", osr.ReadString(), "YARD/Building/BATHROOM/DoorBathroom/Door");
			byte nb0 = osr.ReadByte();
			Check("obj 0 bool count", (int)nb0, 2);
			Check("obj 0 bool 0 name", osr.ReadString(), "Open");
			Check("obj 0 bool 0 value", osr.ReadBoolean(), true);
			Check("obj 0 bool 1 name", osr.ReadString(), "Locked");
			Check("obj 0 bool 1 value", osr.ReadBoolean(), false);
			byte nf0 = osr.ReadByte();
			Check("obj 0 float count", (int)nf0, 1);
			Check("obj 0 float 0 name", osr.ReadString(), "Angle");
			Check("obj 0 float 0 value", (float)osr.ReadSingle(), 61.25f);
			Check("obj 0 ping", (float)osr.ReadSingle(), 35f);
			Check("obj 1 path", osr.ReadString(), "KITCHEN/WaterTap/Tap");
			Check("obj 1 has no bools", (int)osr.ReadByte(), 0);
			Check("obj 1 float count", (int)osr.ReadByte(), 1);
			Check("obj 1 float name", osr.ReadString(), "Water");
			Check("obj 1 float value", (float)osr.ReadSingle(), 0.75f);
			Check("obj 1 ping", (float)osr.ReadSingle(), 12f);
			Check("object state stream fully consumed", osr.BaseStream.Position, osIn.Length);

			// ---- Sayac baytin tasma riski (savunma testi) ----
			// Flush() `(byte)outbox.Count` yazar; 256+ nesne tek flush'a
			// sigmaz. Bu test o sinirin var oldugunu kayda gecer: eger bir
			// gun sinir kaldirilirsa bu kontrol kaldirilmalidir.
			Console.WriteLine("  note: ObjectState sayaç bayi 0..255; fazlasi "
				+ "sonraki yoklamaya kalir (kaybolmaz).");
		}

		/// <summary>
		/// Bu test EKSIKTI: mesaj protokolde tanimliydi ve alicisi yazilmisti
		/// ama hicbir yerden cagrilmiyordu; bicimin iki tarafi da test
		/// edilmedigi icin siranin bozulmasi sessizce gecerdi. Artik bicim
		/// Protocol'da tek kaynakta ve burada dogrulaniyor.
		/// </summary>
		static void RunEnvironmentState() {
			Console.WriteLine("--- environment state ---");

			Protocol.EnvironmentStateData sent = new Protocol.EnvironmentStateData();
			sent.WorldTime = 742.5f;                 // 12:22:30
			sent.WorldDay = 7;
			sent.Weather = 2;                        // Thunder
			sent.SunRotation = 183.25f;
			sent.Temperature = 0f;                   // oyun tutmuyor: bilincli 0
			sent.HostId = 76561198012345678UL;       // bilincli olarak 64-bit
			sent.Timestamp = 1234567U;

			MemoryStream s = Protocol.Begin(Protocol.Msg.EnvironmentState);
			BinaryWriter w = new BinaryWriter(s);
			Protocol.WriteEnvironmentState(w, sent);
			Check("env id is pinned", (int)Protocol.Msg.EnvironmentState, 83);
			Check("env payload size", (int)s.Length - Protocol.HEADER_SIZE,
				Protocol.ENVIRONMENT_STATE_BYTES);

			byte[] raw = Protocol.Finish(s);
			MemoryStream m = new MemoryStream(raw);
			BinaryReader r = new BinaryReader(m);
			Protocol.Msg id;
			Check("env header", Protocol.TryReadHeader(r, out id), true);
			Check("env header id", (int)id, (int)Protocol.Msg.EnvironmentState);

			// Alıcı tam olarak gerçek kodun okuduğu metodu çağırır.
			Protocol.EnvironmentStateData got;
			Protocol.ReadEnvironmentState(r, out got);

			Check("env worldTime", got.WorldTime, sent.WorldTime);
			Check("env worldDay", got.WorldDay, sent.WorldDay);
			Check("env weather", got.Weather, sent.Weather);
			Check("env sunRotation", got.SunRotation, sent.SunRotation);
			Check("env temperature", got.Temperature, sent.Temperature);
			// Uzun kimlik: sıra bozulursa bu iki alan birbirine kayar ve
			// 64-bit hostId parçalanır (sessiz bozulma).
			Check("env hostId (64-bit)", got.HostId, sent.HostId);
			Check("env timestamp", got.Timestamp, sent.Timestamp);
			// Akışın tamamen tüketilmesi: son alanın ardından hiç bayt kalmamalı.
			Check("env stream consumed", m.Position, m.Length);
		}

		static void Check(string what, object actual, object expected) {
			bool ok = Equals(actual, expected);
			if (!ok) failures++;
			Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + " = " + actual
				+ (ok ? "" : " (expected " + expected + ")"));
		}

		public static int Main() {
			RunDoorAndWorld();
			RunEnvironmentState();
			RunSaveTransfer();
			// ---------------- protocol header ----------------
			MemoryStream header = Protocol.Begin(Protocol.Msg.BoltBatch);
			byte[] packet = Protocol.Finish(header);
			MemoryStream inStream = new MemoryStream(packet);
			BinaryReader r = new BinaryReader(inStream);
			Protocol.Msg id;
			Check("TryReadHeader", Protocol.TryReadHeader(r, out id), true);
			Check("message id", (int)id, (int)Protocol.Msg.BoltBatch);
			Check("header size", packet.Length, 5);
			Check("protocol id", BitConverter.ToUInt32(packet, 0), Protocol.PROTOCOL_ID);

			// a foreign packet must be rejected
			byte[] foreign = new byte[] { 1, 2, 3, 4, 5 };
			Protocol.Msg bogus;
			Check("rejects foreign id", Protocol.TryReadHeader(new BinaryReader(new MemoryStream(foreign)), out bogus), false);

			// ---------------- bolt batch ----------------
			SatsumaPart part = new SatsumaPart();
			part.Id = "SATSUMA(557kg, 248)/Chassis/FL/strut fl(xxxxx)";
			part.Object = null;
			part.InstalledVars.Add(new FsmBool());
			part.InstalledVars[0].Name = "Installed";
			part.InstalledVars[0].Value = true;

			part.Bolts.Add(MakeBolt(part, 0, 3, 2.5f));
			part.Bolts.Add(MakeBolt(part, 1, 8, 8f));
			part.Bolts.Add(MakeBolt(part, 2, 0, 0f));

			SatsumaPart other = new SatsumaPart();
			other.Id = "SATSUMA(557kg, 248)/Wiring/Triggers/Fueltank/ConnectorTank";
			other.InstalledVars.Add(new FsmBool());
			other.InstalledVars[0].Name = "Installed";
			other.InstalledVars[0].Value = false;

			List<SatsumaBolt> bolts = new List<SatsumaBolt>(part.Bolts);

			MemoryStream body = new MemoryStream(512);
			BinaryWriter w = new BinaryWriter(body);
			int count = NetworkPacketManager.WriteBoltBatch(w, bolts);
			Console.WriteLine("bolt batch: " + count + " record(s), " + body.Length + " bytes for "
				+ part.Id.Length + " char id");
			Check("records written", count, 3);

			MemoryStream full = Protocol.Begin(Protocol.Msg.BoltBatch);
			new BinaryWriter(full).Write((byte)count);
			full.Write(body.GetBuffer(), 0, (int)body.Length);

			// read it back exactly the way SatsumaRegistry.ApplyBoltBatch does
			BinaryReader br = new BinaryReader(new MemoryStream(full.ToArray()));
			Protocol.Msg mid;
			Protocol.TryReadHeader(br, out mid);
			byte n = br.ReadByte();
			Check("batch count byte", (int)n, 3);
			for (int i = 0; i < n; i++) {
				string boltId = br.ReadString();
				byte index = br.ReadByte();
				float tightness = br.ReadSingle();
				int stage = (sbyte)br.ReadByte();
				bool bolted = br.ReadBoolean();
				// The bolt's own path is the wire identity and the LAST field, so a
				// peer on the previous build (which stops writing here) still parses.
				string boltPath = br.ReadString();
				Console.WriteLine("  record " + i + ": index=" + index + " stage=" + stage
					+ " tightness=" + tightness + " bolted=" + bolted + " path=" + boltPath);
				Check("  id", boltId, part.Id);
				Check("  index", index, (byte)i);
				Check("  stage", stage, i == 0 ? 3 : (i == 1 ? 8 : 0));
				Check("  tightness", tightness, i == 0 ? 2.5f : (i == 1 ? 8f : 0f));
				// bolted is the part level roll-up: bolts are 3, 8, 0, so the group is loose.
				Check("  bolted (group roll-up)", bolted, false);
				// The path must be present and unique per bolt: it is what matches
				// bolts across different saves (host 378/337 vs peer 369/332), where
				// the index shifts and the wrong bolt gets unscrewed.
				Check("  bolt path is not empty", boltPath.Length > 0, true);
				Check("  bolt path is under the part", boltPath.StartsWith(part.Id + "/"), true);
			}
			Check("stream fully consumed", br.BaseStream.Position, full.Length);

			// a part with every bolt tight must report bolted = true
			SatsumaPart tightPart = new SatsumaPart();
			tightPart.Id = "tight";
			tightPart.Bolts.Add(MakeBolt(tightPart, 0, 8, 8f));
			tightPart.Bolts.Add(MakeBolt(tightPart, 1, 8, 7.5f));
			MemoryStream tightBody = new MemoryStream();
			int tightCount = NetworkPacketManager.WriteBoltBatch(new BinaryWriter(tightBody), tightPart.Bolts);
			tightBody.Position = 0;
			BinaryReader tr = new BinaryReader(tightBody);
			for (int i = 0; i < tightCount; i++) {
				tr.ReadString();
				tr.ReadByte();
				tr.ReadSingle();
				tr.ReadByte();
				Check("  tight part bolted", tr.ReadBoolean(), true);
			}

			// stage clamping: 200 must not wrap to a negative level
			MemoryStream clampStream = new MemoryStream();
			BinaryWriter cw = new BinaryWriter(clampStream);
			NetworkPacketManager.WriteBolt(cw, "x", 0, 0f, 200, false);
			cw.Flush();
			cw.BaseStream.Position = 0;
			BinaryReader cr = new BinaryReader(cw.BaseStream);
			cr.ReadString();
			cr.ReadByte();
			cr.ReadSingle();
			Check("stage 200 clamps to 120", (sbyte)cr.ReadByte(), (sbyte)120);

			// ---------------- part batch ----------------
			part.Installable = true;
			other.Installable = true;
			// A bolt container has no Installed flag, so it must never appear in a batch.
			SatsumaPart container = new SatsumaPart();
			container.Id = "SATSUMA(557kg, 248)/Chassis/FL/Bolts";
			container.Name = "Bolts";
			container.Installable = false;
			container.Bolts.Add(MakeBolt(container, 0, 4, 4f));

			List<SatsumaPart> table = new List<SatsumaPart>();
			table.Add(container);   // deliberately first: must be skipped
			table.Add(part);
			table.Add(other);

			MemoryStream pbody = new MemoryStream(256);
			BinaryWriter pw = new BinaryWriter(pbody);
			int pcount = NetworkPacketManager.WritePartBatch(pw, table, 0, 8);
			Check("part batch clamps to table", pcount, 2);
			pbody.Position = 0;
			BinaryReader pr = new BinaryReader(pbody);
			// Record layout is (id, installed, bolted, slot, damaged) -- v18 (a)
			// grew the record from two fields to five. Reading only the first
			// two silently desynchronises the stream on the next ReadString, so
			// every field is read and checked here, and the "fully consumed"
			// assert at the end is what catches a future field being added to
			// the writer alone.
			Check("part 0 id", pr.ReadString(), part.Id);
			Check("part 0 installed", pr.ReadBoolean(), true);
			Check("part 0 bolted (3/8/0 not all tight)", pr.ReadBoolean(), false);
			Check("part 0 slot (no SlotFlags)", pr.ReadByte(), (byte)0);
			Check("part 0 damaged (no DbDamaged)", pr.ReadBoolean(), false);

			Check("part 1 id", pr.ReadString(), other.Id);
			Check("part 1 installed", pr.ReadBoolean(), false);
			// No bolts and no bound `Bolted` bool: v28 stopped inventing "all
			// tight" for this case, so the roll-up is false, not true.
			Check("part 1 bolted (no bolts, no flag)", pr.ReadBoolean(), false);
			Check("part 1 slot", pr.ReadByte(), (byte)0);
			Check("part 1 damaged", pr.ReadBoolean(), false);

			Check("part batch fully consumed", pr.BaseStream.Position, pbody.Length);
			Check("bolt container excluded", pr.BaseStream.Position == pbody.Length, true);
			Check("CountInstallable", NetworkPacketManager.CountInstallable(table), 2);

			// startIndex counts installable entries, so paging must skip the container.
			MemoryStream page1 = new MemoryStream();
			Check("page at 0", NetworkPacketManager.WritePartBatch(new BinaryWriter(page1), table, 0, 1), 1);
			page1.Position = 0;
			BinaryReader p1 = new BinaryReader(page1);
			Check("page 0 id", p1.ReadString(), part.Id);

			MemoryStream page2 = new MemoryStream();
			Check("page at 1", NetworkPacketManager.WritePartBatch(new BinaryWriter(page2), table, 1, 1), 1);
			page2.Position = 0;
			BinaryReader p2 = new BinaryReader(page2);
			Check("page 1 id", p2.ReadString(), other.Id);

			MemoryStream page3 = new MemoryStream();
			Check("page past the end", NetworkPacketManager.WritePartBatch(new BinaryWriter(page3), table, 2, 1), 0);
			Check("page past the end is empty", (int)page3.Length, 0);

			// A batch of nothing but bolt containers must produce no packet at all.
			List<SatsumaPart> onlyContainers = new List<SatsumaPart>();
			onlyContainers.Add(container);
			MemoryStream noneStream = new MemoryStream();
			Check("all containers writes none", NetworkPacketManager.WritePartBatch(new BinaryWriter(noneStream), onlyContainers, 0, 8), 0);

			// ---------------- part event ----------------
			MemoryStream ev = Protocol.Begin(Protocol.Msg.PartEvent);
			BinaryWriter pew = new BinaryWriter(ev);
			pew.Write("Chassis/FL/strut fl(xxxxx)");
			pew.Write("Assembly");
			pew.Write("ASSEMBLE");
			MemoryStream evIn = new MemoryStream(ev.ToArray());
			BinaryReader per = new BinaryReader(evIn);
			Protocol.Msg peid;
			Protocol.TryReadHeader(per, out peid);
			Check("event id", (int)peid, (int)Protocol.Msg.PartEvent);
			Check("event part", per.ReadString(), "Chassis/FL/strut fl(xxxxx)");
			Check("event fsm", per.ReadString(), "Assembly");
			Check("event name", per.ReadString(), "ASSEMBLE");

			// ---------------- sync request ----------------
			MemoryStream req = Protocol.Begin(Protocol.Msg.SyncRequest);
			BinaryWriter rw = new BinaryWriter(req);
			rw.Write(0xCAFEF00Du);   // the client's table hash
			rw.Write(218);           // installable parts
			rw.Write(137);           // bolts
			MemoryStream reqIn = new MemoryStream(req.ToArray());
			BinaryReader rr = new BinaryReader(reqIn);
			Protocol.Msg rid;
			Protocol.TryReadHeader(rr, out rid);
			Check("sync request id", (int)rid, (int)Protocol.Msg.SyncRequest);
			Check("sync request hash", rr.ReadUInt32(), 0xCAFEF00Du);
			Check("sync request parts", rr.ReadInt32(), 218);
			Check("sync request bolts", rr.ReadInt32(), 137);
			Check("sync request consumed", rr.BaseStream.Position, reqIn.Length);

			// ---------------- a null id must not throw, it writes as empty ----------------
			MemoryStream nullStream = new MemoryStream();
			BinaryWriter nw = new BinaryWriter(nullStream);
			NetworkPacketManager.WriteBolt(nw, null, 0, 0f, 0, false);
			nw.Flush();
			nw.BaseStream.Position = 0;
			BinaryReader nr = new BinaryReader(nw.BaseStream);
			Check("null id writes as empty", nr.ReadString(), "");

			// ---------------- part transform packet ----------------
			// Yeni paket: parçanın dünya konumu. "Client'ta parçalar araçta
			// değil, garajda duruyor" problemini çözen kanal.
			MemoryStream ts = Protocol.Begin(Protocol.Msg.PartTransform);
			BinaryWriter tw = new BinaryWriter(ts);
			tw.Write((byte)2);                                   // iki kayıt
			tw.Write("SATSUMA(557kg, 248)/Chassis/wheel fl");
			tw.Write(1.5f); tw.Write(2.0f); tw.Write(-3.25f);
			tw.Write(0f); tw.Write(0.707f); tw.Write(0f); tw.Write(0.707f);
			tw.Write("SATSUMA(557kg, 248)/Wiring/ConnectorTank");
			tw.Write(0f); tw.Write(0.5f); tw.Write(10f);
			tw.Write(0f); tw.Write(0f); tw.Write(0f); tw.Write(1f);
			tw.Flush();

			MemoryStream tin = new MemoryStream(ts.ToArray());
			BinaryReader xtr = new BinaryReader(tin);
			Protocol.Msg tid;
			Check("transform header", Protocol.TryReadHeader(xtr, out tid), true);
			Check("transform message id", (int)tid, (int)Protocol.Msg.PartTransform);
			byte tc = xtr.ReadByte();
			Check("transform count", (int)tc, 2);
			string t0 = xtr.ReadString();
			Check("transform record 0 id", t0, "SATSUMA(557kg, 248)/Chassis/wheel fl");
			Check("transform record 0 x", xtr.ReadSingle(), 1.5f);
			xtr.ReadSingle(); xtr.ReadSingle();
			xtr.ReadSingle(); xtr.ReadSingle(); xtr.ReadSingle(); xtr.ReadSingle();
			string t1 = xtr.ReadString();
			Check("transform record 1 id", t1, "SATSUMA(557kg, 248)/Wiring/ConnectorTank");
			xtr.ReadSingle(); xtr.ReadSingle(); xtr.ReadSingle();
			xtr.ReadSingle(); xtr.ReadSingle(); xtr.ReadSingle(); xtr.ReadSingle();
			Check("transform stream consumed", tin.Position, tin.Length);
			Check("PartTransform id is pinned", (int)Protocol.Msg.PartTransform, 7);

			// ---------------- garage entity state packet ----------------
			// Garajdaki boş parça/eşya: konum + "karşı taraf tutuyor mu".
			MemoryStream es = Protocol.Begin(Protocol.Msg.EntityState);
			BinaryWriter gew = new BinaryWriter(es);
			gew.Write((byte)1);
			gew.Write("SATSUMA(557kg, 248)/Chassis/wheel fl");
			gew.Write(2f); gew.Write(0f); gew.Write(1f);
			gew.Write(0f); gew.Write(0f); gew.Write(0f); gew.Write(1f);
			gew.Write(true);                 // peer tutuyor
			gew.Flush();

			MemoryStream ein = new MemoryStream(es.ToArray());
			BinaryReader ger = new BinaryReader(ein);
			Protocol.Msg geid;
			Check("entity header", Protocol.TryReadHeader(ger, out geid), true);
			Check("entity message id", (int)geid, (int)Protocol.Msg.EntityState);
			Check("EntityState id is pinned", (int)Protocol.Msg.EntityState, 8);
			Check("entity count", (int)ger.ReadByte(), 1);
			Check("entity path", ger.ReadString(), "SATSUMA(557kg, 248)/Chassis/wheel fl");
			ger.ReadSingle(); ger.ReadSingle(); ger.ReadSingle();
			ger.ReadSingle(); ger.ReadSingle(); ger.ReadSingle(); ger.ReadSingle();
			Check("entity peer holds it", ger.ReadBoolean(), true);
			Check("entity stream consumed", ein.Position, ein.Length);

			// ---------------- world object state packet ----------------
			// Kapi/musluk/dus: nesne yolu + bool/float degisken listesi.
			MemoryStream wo = Protocol.Begin(Protocol.Msg.ObjectState);
			BinaryWriter wow = new BinaryWriter(wo);
			wow.Write((byte)1);
			wow.Write("house/FrontDoor");
			wow.Write((byte)1);
			wow.Write("Open");
			wow.Write(true);
			wow.Write((byte)1);
			wow.Write("Angle");
			wow.Write(85f);
			wow.Write(12f);                      // ping
			wow.Flush();

			MemoryStream woIn = new MemoryStream(wo.ToArray());
			BinaryReader wor = new BinaryReader(woIn);
			Protocol.Msg woid;
			Check("world object header", Protocol.TryReadHeader(wor, out woid), true);
			Check("ObjectState id is pinned", (int)woid, (int)Protocol.Msg.ObjectState);
			Check("world object count", (int)wor.ReadByte(), 1);
			Check("world object path", wor.ReadString(), "house/FrontDoor");
			Check("world object bool count", (int)wor.ReadByte(), 1);
			Check("world object bool name", wor.ReadString(), "Open");
			Check("world object bool value", wor.ReadBoolean(), true);
			Check("world object float count", (int)wor.ReadByte(), 1);
			Check("world object float name", wor.ReadString(), "Angle");
			Check("world object float value", (float)wor.ReadSingle(), 85f);
			wor.ReadSingle();
			Check("world object stream consumed", woIn.Position, woIn.Length);

			// ---------------- radio / CD packet ----------------
			// kind 0 = FsmInt (track), 1 = FsmBool (playing), 2 = FsmFloat (volume).
			MemoryStream rd = Protocol.Begin(Protocol.Msg.RadioState);
			BinaryWriter rdw = new BinaryWriter(rd);
			rdw.Write((byte)3);
			rdw.Write("CDPlayer/PivotCD"); rdw.Write("Track");  rdw.Write((byte)0); rdw.Write(7);
			rdw.Write("CDPlayer/PivotCD"); rdw.Write("Playing"); rdw.Write((byte)1); rdw.Write(true);
			rdw.Write("Radio/RadioVolume"); rdw.Write("Volume"); rdw.Write((byte)2); rdw.Write(0.65f);
			rdw.Flush();

			MemoryStream rdIn = new MemoryStream(rd.ToArray());
			BinaryReader rdr = new BinaryReader(rdIn);
			Protocol.Msg rdid;
			Check("radio header", Protocol.TryReadHeader(rdr, out rdid), true);
			Check("RadioState id is pinned", (int)rdid, (int)Protocol.Msg.RadioState);
			Check("radio binding count", (int)rdr.ReadByte(), 3);
			Check("radio track path", rdr.ReadString(), "CDPlayer/PivotCD");
			Check("radio track var", rdr.ReadString(), "Track");
			Check("radio track kind", (int)rdr.ReadByte(), 0);
			Check("radio track value", rdr.ReadInt32(), 7);
			// Her binding (path, varName, kind, value) dortlusunu tekrarlar.
			Check("radio playing path", rdr.ReadString(), "CDPlayer/PivotCD");
			Check("radio playing var", rdr.ReadString(), "Playing");
			Check("radio playing kind", (int)rdr.ReadByte(), 1);
			Check("radio playing value", rdr.ReadBoolean(), true);
			Check("radio volume path", rdr.ReadString(), "Radio/RadioVolume");
			Check("radio volume var", rdr.ReadString(), "Volume");
			Check("radio volume kind", (int)rdr.ReadByte(), 2);
			Check("radio volume value", (float)rdr.ReadSingle(), 0.65f);
			Check("radio stream consumed", rdIn.Position, rdIn.Length);

			// ---------------- player flags packet ----------------
			// Oturma/egilme/olum. Guvenilir + yalnizca degisince.
			MemoryStream ps = Protocol.Begin(Protocol.Msg.PlayerState);
			BinaryWriter psw = new BinaryWriter(ps);
			psw.Write((byte)(LocalPlayerState.FLAG_SITTING | LocalPlayerState.FLAG_CROUCH));
			psw.Write((byte)65);
			psw.Write(-0.42f);
			psw.Flush();

			MemoryStream psIn = new MemoryStream(ps.ToArray());
			BinaryReader psr = new BinaryReader(psIn);
			Protocol.Msg psid;
			Check("player state header", Protocol.TryReadHeader(psr, out psid), true);
			Check("PlayerState id is pinned", (int)psid, (int)Protocol.Msg.PlayerState);
			Check("FluidState id is pinned", (int)Protocol.Msg.FluidState, 43);
			Check("HeldItem id is pinned", (int)Protocol.Msg.HeldItem, 44);
			Check("DoorEvent id is pinned", (int)Protocol.Msg.DoorEvent, 45);

			// ---------------- door event packet ----------------
			// Kapi acma/kapama. Bu build'de kapinin okunabilir bir durumu
			// YOK: `CARPARTS/PartsCar/door left(Clone)` uzerindeki `Use` FSM'i
			// OPEN/CLOSE olaylariyla durum degistiriyor ve mesh'i kendi
			// SetRotation action'i ile donduruyor. Bu yuzden kapi bir DEGISKEN
			// degil bir OLAY olarak tasinir. Alanlar: nesne yolu, FSM adi, olay.
			//
			// Yolun TAM OLMASI onemli: birden fazla nesne `.../doorr` adi
			// tasiyor (FITTAN, HAYOSIKO, RCO_RUSCKO12), yalnizca yol ayirt eder.
			MemoryStream de = Protocol.Begin(Protocol.Msg.DoorEvent);
			BinaryWriter dew = new BinaryWriter(de);
			dew.Write("YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle");
			dew.Write("Use");
			dew.Write("OPEN");
			dew.Flush();

			MemoryStream deIn = new MemoryStream(de.ToArray());
			BinaryReader der = new BinaryReader(deIn);
			Protocol.Msg deid;
			Check("door event header", Protocol.TryReadHeader(der, out deid), true);
			Check("DoorEvent id round trip", (int)deid, (int)Protocol.Msg.DoorEvent);
			Check("door event path", der.ReadString(), "YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle");
			Check("door event fsm", der.ReadString(), "Use");
			Check("door event name", der.ReadString(), "OPEN");
			Check("door event stream consumed", deIn.Position, deIn.Length);
			// HeldItem: tutma bayragi + eysa yolu + elin oyuncu kokune gore
			// yolu + yerel konum/donus. Alma olayinin SONUCU kopyalanir,
			// olayin kendisi degil: SetParent hedefi oyuncunun kendi nesnesi
			// oldugu icin replay eysayi yerel oyuncunun eline takardi.
			MemoryStream hi = Protocol.Begin(Protocol.Msg.HeldItem);
			BinaryWriter hiw = new BinaryWriter(hi);
			hiw.Write(true);
			hiw.Write("WORKSHOP/Beer can");
			hiw.Write("Armature/Root/hand_r");
			hiw.Write(0.1f); hiw.Write(-0.2f); hiw.Write(0.3f);
			hiw.Write(0f); hiw.Write(0f); hiw.Write(0f); hiw.Write(1f);
			hiw.Flush();

			MemoryStream hiIn = new MemoryStream(hi.ToArray());
			BinaryReader hir = new BinaryReader(hiIn);
			Protocol.Msg hiid;
			Check("held item header", Protocol.TryReadHeader(hir, out hiid), true);
			Check("HeldItem id round trip", (int)hiid, (int)Protocol.Msg.HeldItem);
			Check("held item flag", hir.ReadBoolean(), true);
			Check("held item path", hir.ReadString(), "WORKSHOP/Beer can");
			Check("held item hand path", hir.ReadString(), "Armature/Root/hand_r");
			hir.ReadSingle(); hir.ReadSingle(); hir.ReadSingle();
			hir.ReadSingle(); hir.ReadSingle(); hir.ReadSingle(); hir.ReadSingle();
			Check("held item stream consumed", hiIn.Position, hiIn.Length);
			byte psflags = psr.ReadByte();
			Check("player state sitting bit", (psflags & LocalPlayerState.FLAG_SITTING) != 0, true);
			Check("player state crouch bit", (psflags & LocalPlayerState.FLAG_CROUCH) != 0, true);
			Check("player state dead bit", (psflags & LocalPlayerState.FLAG_DEAD) != 0, false);
			Check("player state crouch amount", (int)psr.ReadByte(), 65);
			Check("player state eye offset", (float)psr.ReadSingle(), -0.42f);
			Check("player state stream consumed", psIn.Position, psIn.Length);

			// ---------------- player position packet ----------------
			// Uzak oyuncunun 20 Hz konum akisi. Guvenilmez. Iki taraf ayni
			// build'i calistirmak zorunda (PROTOCOL_VERSION el sikismada kontrol
			// edilir), yani bu duzen bir sozlesmedir: 3 konum + 4 yonelme +
			// 1 hiz + kafa pitch/yaw = 10 float = 40 bayt.
			//
			// DUZELTME: burasi eskiden 32 bayt yaziyordu (kafa alanlari YOK).
			// Test kendi yazdigi paketi kendi sabitiyle karsilastirinca
			// "geciyor" diyordu; gercek gonderici (SendPos) ise 40 bayt
			// yaziyordu. Yani prob, belgelenmis duzeni degil kendi kopyasini
			// dogruluyordu ve aradaki kaymayi goremezdi. Artik gondericinin
			// alan kumesinin aynisi yazilip okunuyor.
			MemoryStream pp = Protocol.Begin(Protocol.Msg.PlayerPos);
			BinaryWriter ppw = new BinaryWriter(pp);
			ppw.Write(1.5f); ppw.Write(2.25f); ppw.Write(-3.125f);
			ppw.Write(0f); ppw.Write(0.70710678f); ppw.Write(0f); ppw.Write(0.70710678f);
			ppw.Write(1.85f);
			ppw.Write(12.5f);   // headPitch (normalize edilmis -90..90)
			ppw.Write(95.25f);  // headYaw (mutlak dunya yonu)
			ppw.Flush();

			MemoryStream ppIn = new MemoryStream(pp.ToArray());
			BinaryReader ppr = new BinaryReader(ppIn);
			Protocol.Msg ppid;
			Check("player position header", Protocol.TryReadHeader(ppr, out ppid), true);
			Check("PlayerPos id is pinned", (int)ppid, (int)Protocol.Msg.PlayerPos);
			Check("player position x", (float)ppr.ReadSingle(), 1.5f);
			Check("player position y", (float)ppr.ReadSingle(), 2.25f);
			Check("player position z", (float)ppr.ReadSingle(), -3.125f);
			Check("player rotation x", (float)ppr.ReadSingle(), 0f);
			Check("player rotation y", (float)ppr.ReadSingle(), 0.70710678f);
			Check("player rotation z", (float)ppr.ReadSingle(), 0f);
			Check("player rotation w", (float)ppr.ReadSingle(), 0.70710678f);
			Check("player speed", (float)ppr.ReadSingle(), 1.85f);
			Check("player head pitch", (float)ppr.ReadSingle(), 12.5f);
			Check("player head yaw", (float)ppr.ReadSingle(), 95.25f);
			Check("player position stream consumed", ppIn.Position, ppIn.Length);
			Check("PlayerPos payload size is POS_BYTES",
				(int)(ppIn.Length - Protocol.HEADER_SIZE),
				RemotePlayerSync.POS_BYTES);

			// ---------------- player info packet ----------------
			// Oturum basina bir kez guvenilir: karsi tarafin adi.
			MemoryStream pi = Protocol.Begin(Protocol.Msg.PlayerInfo);
			BinaryWriter piw = new BinaryWriter(pi);
			piw.Write("peer");
			piw.Flush();

			MemoryStream piIn = new MemoryStream(pi.ToArray());
			BinaryReader pir = new BinaryReader(piIn);
			Protocol.Msg piid;
			Check("player info header", Protocol.TryReadHeader(pir, out piid), true);
			Check("PlayerInfo id is pinned", (int)piid, (int)Protocol.Msg.PlayerInfo);
			Check("player info name", pir.ReadString(), "peer");
			Check("player info stream consumed", piIn.Position, piIn.Length);

			// Oyuncu paketleri arac trafigine karismamali: IsVehicleOrPlayer
			// true dondurunce Dispatch onlari VehicleRegistry'ye yolluyordu,
			// bu yuzden SteamMPBehaviour once ele aliyor.
			Check("PlayerPos is a player id", Protocol.IsVehicleOrPlayer(Protocol.Msg.PlayerPos), true);
			Check("PlayerInfo is a player id", Protocol.IsVehicleOrPlayer(Protocol.Msg.PlayerInfo), true);
			Check("PlayerState is not a vehicle id", Protocol.IsVehicleOrPlayer(Protocol.Msg.PlayerState), false);

			Console.WriteLine(failures == 0 ? "ALL WIRE CHECKS PASSED" : failures + " CHECK(S) FAILED");
			return failures == 0 ? 0 : 1;
		}

		static SatsumaBolt MakeBolt(SatsumaPart part, byte index, int stage, float tightness) {
			SatsumaBolt b = new SatsumaBolt();
			b.Part = part;
			b.Index = index;
			// The wire identity, mirroring BuildBolt: the bolt's own path under the
			// Satsuma root. It has to be unique per bolt, because it is what matches
			// bolts when the two sides' saves disagree (host 378/337, peer 369/332)
			// and the index shifts onto the wrong bolt.
			b.Path = part.Id + "/Bolt" + index + "(xxxxx)";
			b.Stage = new FsmInt();
			b.Stage.Name = "Stage";
			b.Stage.Value = stage;
			b.Tightness = new FsmFloat();
			b.Tightness.Name = "Tightness";
			b.Tightness.Value = tightness;
			return b;
		}
	}
}
