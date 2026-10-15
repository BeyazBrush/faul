using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SteamMP
{
    /// <summary>Sahiplik talep tipleri. Wire'da tek byte olarak yazilir.</summary>
    public enum AuthorityRequestType : byte
    {
        Request = 0,
        Grant = 1,
        Release = 2,
        ForceRelease = 3
    }

    /// <summary>Vida sıkma/gevşetme eylemi türü.</summary>
    public enum BoltAction : byte
    {
        Tighten = 0,
        Untighten = 1
    }

    /// <summary>Bir parcayi tutma durumu: (path, held, carrierPath, localPos, localRot, parentVehiclePath).</summary>
    public class CarryStateData
    {
        public string Path;
        public bool Held;
        public string CarrierPath;
        public Vector3 LocalPos;
        public Quaternion LocalRot;
        public string ParentVehiclePath;
    }

    /// <summary>Bir parcanin takma durumu. CarryState ile ayni yuz, farkli "installed" bayragi.</summary>
    public class PartMountStateData
    {
        public string PartPath;
        public bool Installed;
        public string CarrierPath;
        public Vector3 LocalPos;
        public Quaternion LocalRot;
        public string ParentVehiclePath;
    }

    /// <summary>Vida durumu: (boltPath, stage, tightness).</summary>
    public class BoltStateData
    {
        public string BoltPath;
        public int Stage;
        public float Tightness;
    }

    /// <summary>Vida batch verisi: (bolts[], batchId, senderSteamId, timestamp).</summary>
    public class BoltBatchData
    {
        public BoltStateData[] Bolts;
        public uint BatchId;
        public ulong SenderSteamId;
        public uint Timestamp;
    }

    /// <summary>Sahiplik durumu: (objectPath, ownerSteamId, held, installed, holderPath).</summary>
    public class AuthorityStateData
    {
        public string ObjectPath;
        public ulong OwnerSteamId;
        public bool Held;
        public bool Installed;
        public string HolderPath;
    }

    /// <summary>
    /// Etkilesim mesajlarinin (CarryState / PartMountState) wire yazici-okuyuculari.
    ///
    /// Diger etkilesim yazicilari InteractionProtocol_continuation.cs icinde.
    /// </summary>
    public static partial class Protocol
    {
        public static void WriteCarryState(BinaryWriter w, string path, bool held, string carrierPath, Vector3 localPos, Quaternion localRot, string parentVehiclePath)
        {
            w.Write(path ?? "");
            w.Write(held);
            w.Write(carrierPath ?? "");
            w.Write(localPos.x); w.Write(localPos.y); w.Write(localPos.z);
            w.Write(localRot.x); w.Write(localRot.y); w.Write(localRot.z); w.Write(localRot.w);
            w.Write(parentVehiclePath ?? "");
        }

        public static void ReadCarryState(BinaryReader r, out string path, out bool held, out string carrierPath, out Vector3 localPos, out Quaternion localRot, out string parentVehiclePath)
        {
            path = r.ReadString();
            held = r.ReadBoolean();
            carrierPath = r.ReadString();
            localPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            localRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            parentVehiclePath = r.ReadString();
        }

        public static void WritePartMountState(BinaryWriter w, string partPath, bool installed, string carrierPath, Vector3 localPos, Quaternion localRot, string parentVehiclePath)
        {
            w.Write(partPath ?? "");
            w.Write(installed);
            w.Write(carrierPath ?? "");
            w.Write(localPos.x); w.Write(localPos.y); w.Write(localPos.z);
            w.Write(localRot.x); w.Write(localRot.y); w.Write(localRot.z); w.Write(localRot.w);
            w.Write(parentVehiclePath ?? "");
        }

        public static void ReadPartMountState(BinaryReader r, out string partPath, out bool installed, out string carrierPath, out Vector3 localPos, out Quaternion localRot, out string parentVehiclePath)
        {
            partPath = r.ReadString();
            installed = r.ReadBoolean();
            carrierPath = r.ReadString();
            localPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            localRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            parentVehiclePath = r.ReadString();
        }

        // ------------------------------------------------------------------
        // ORTAM DURUMU (Msg.EnvironmentState = 83)
        // ------------------------------------------------------------------

        /// <summary>
        /// Dunya saati/havasinin tel bicimi.
        ///
        ///   float worldTime, int worldDay, byte weather,
        ///   float sunRotation, float temperature, ulong hostId, uint timestamp
        ///
        /// NEDEN TEK KAYNAK: bicim daha once `EnvSync` icinde cift yerde
        /// (gonderenin `w.Write` zinciri ve alicinin `r.Read` zinciri) yaziliydi
        /// ve ikisi arasindaki tek dogruluk kaynagi bir YORUMDU. Yanlis sira
        /// ya da yanlis tip sessiz bozulma uretirdi (deger kayar, istisna
        /// atmadan) -- ortam senkronu hicbir zaman calismadigi icin de kimse
        /// fark etmezdi. Artik taraf bu iki metodu cagirir ve sira/tipler burada
        /// bir kez tanimlanir; tests\WireProbe.cs done-me-gerisini test eder.
        /// </summary>
        public struct EnvironmentStateData
        {
            public float WorldTime;      // oyun saati (dakika)
            public int WorldDay;
            public byte Weather;         // 0=Clear, 1=Rain, 2=Thunder, 3=Fog
            public float SunRotation;    // gunes donusu (derece, x ekseni)
            public float Temperature;     // oyun bu FSM'de tutmuyor: bilincli 0
            public ulong HostId;         // gonderen (teshis)
            public uint Timestamp;       // gonderme ani (teshis / zaman asimi)
        }

        public static void WriteEnvironmentState(BinaryWriter w, EnvironmentStateData d)
        {
            w.Write(d.WorldTime);
            w.Write(d.WorldDay);
            w.Write(d.Weather);
            w.Write(d.SunRotation);
            w.Write(d.Temperature);
            w.Write(d.HostId);
            w.Write(d.Timestamp);
        }

        public static void ReadEnvironmentState(BinaryReader r, out EnvironmentStateData d)
        {
            d.WorldTime = r.ReadSingle();
            d.WorldDay = r.ReadInt32();
            d.Weather = r.ReadByte();
            d.SunRotation = r.ReadSingle();
            d.Temperature = r.ReadSingle();
            d.HostId = r.ReadUInt64();
            d.Timestamp = r.ReadUInt32();
        }

        /// <summary>Bir kaydin tam bayt uzunlugu (tampon boyu tahmini).</summary>
        public const int ENVIRONMENT_STATE_BYTES = 4 + 4 + 1 + 4 + 4 + 8 + 4;

        // ------------------------------------------------------------------
        // KAYIT AKTARIMI (Msg.SaveGameChunk = 60 / Msg.SaveGameComplete = 61)
        // ------------------------------------------------------------------

        /// <summary>
        /// Tek kayıt parçası.
        ///
        /// Bicim TEK KAYNAKTA: gonderici (SaveTransfer.TickSend) ve alici
        /// (SaveTransfer.OnChunk) ikisi de bu iki metodu cagirir. Elle siralanmis
        /// `w.Write` zincirleri bir gun birbirinden ayrilir ve sessiz bozulma
        /// uretirdi -- kayit aktarimi artik oynanicinin kendi dosyasini ezdigi
        /// icin burada hata affedilmez.
        ///
        ///   uint fileSize, uint crc32, ushort index, ushort count,
        ///   ushort length, byte[] data
        ///
        /// fileSize/crc32 HER parçada tekrarlanır: ilk parça kaybolsa bile
        /// alıcı dosyanın kimliğini bilir ve yabancı bir parçayı reddeder.
        /// </summary>
        public struct SaveChunkData
        {
            public uint FileSize;
            public uint Crc32;
            public ushort Index;
            public ushort Count;
            /// <summary>Veri tamponu (tüm parça) — yazımda uzunluk bilinir.</summary>
            public byte[] Data;
            /// <summary>Bu parçanın tampon içindeki başlangıcı.</summary>
            public int Offset;
            /// <summary>Bu parçanın bayt uzunluğu.</summary>
            public int Length;
        }

        /// <summary>Tek parçanın sabit başlık boyu (veri hariç).</summary>
        public const int SAVE_CHUNK_HEADER = 4 + 4 + 2 + 2 + 2;

        public static void WriteSaveChunk(BinaryWriter w, SaveChunkData d)
        {
            w.Write(d.FileSize);
            w.Write(d.Crc32);
            w.Write(d.Index);
            w.Write(d.Count);
            int len = d.Length;
            if (len < 0) len = 0;
            if (len > 65535) len = 65535;
            w.Write((ushort)len);
            if (d.Data != null && len > 0) w.Write(d.Data, d.Offset, len);
        }

        public static void ReadSaveChunk(BinaryReader r, out SaveChunkData d)
        {
            d.FileSize = r.ReadUInt32();
            d.Crc32 = r.ReadUInt32();
            d.Index = r.ReadUInt16();
            d.Count = r.ReadUInt16();
            int len = (int)r.ReadUInt16();
            if (len < 0) len = 0;
            d.Length = len;
            // Veri tamponun geri kalanıdır: akıştan tam olarak o kadar okunur.
            d.Data = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = r.Read(d.Data, read, len - read);
                if (n <= 0) throw new EndOfStreamException("kayıt parçası eksik");
                read += n;
            }
            d.Offset = 0;
        }

        /// <summary>
        /// Kapanış / istek paketi. İki yönde kullanılır:
        ///   client -> host : ok=false, message="request"  (istek)
        ///   host -> client : ok=true,  message="sent"     (tüm parçalar gitti)
        ///   client -> host : ok=true/false, message=...   (yazma sonucu)
        /// </summary>
        public struct SaveCompleteData
        {
            public uint FileSize;
            public uint Crc32;
            public bool Ok;
            public string Message;
        }

        public static void WriteSaveComplete(BinaryWriter w, SaveCompleteData d)
        {
            w.Write(d.FileSize);
            w.Write(d.Crc32);
            w.Write(d.Ok);
            w.Write(d.Message ?? "");
        }

        public static void ReadSaveComplete(BinaryReader r, out SaveCompleteData d)
        {
            d.FileSize = r.ReadUInt32();
            d.Crc32 = r.ReadUInt32();
            d.Ok = r.ReadBoolean();
            d.Message = r.ReadString();
        }
    }
}
