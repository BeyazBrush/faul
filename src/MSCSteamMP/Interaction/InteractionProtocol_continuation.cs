using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Etkilesim mesajlarinin kalan wire yazici-okuyuculari:
    /// BoltBatchV2, AuthorityClaim, FullStateSnapshot, PartRecovery, AuthoritySnapshot.
    ///
    /// InteractionProtocol.cs ile ayni partial class'in parcasi.
    /// </summary>
    public static partial class Protocol
    {
        public static void ReadBoltBatchV2(BinaryReader r, out BoltStateData[] bolts)
        {
            bolts = new BoltStateData[r.ReadUInt16()];
            for (int i = 0; i < bolts.Length; i++)
            {
                bolts[i] = new BoltStateData
                {
                    BoltPath = r.ReadString(),
                    Stage = r.ReadInt32(),
                    Tightness = r.ReadSingle()
                };
            }
        }

        public static void WriteAuthorityClaim(BinaryWriter w, string objectPath, ulong newOwnerSteamId, AuthorityRequestType requestType)
        {
            w.Write(objectPath ?? "");
            w.Write(newOwnerSteamId);
            w.Write((byte)requestType);
        }

        public static void ReadAuthorityClaim(BinaryReader r, out string objectPath, out ulong newOwnerSteamId, out AuthorityRequestType requestType)
        {
            objectPath = r.ReadString();
            newOwnerSteamId = r.ReadUInt64();
            requestType = (AuthorityRequestType)r.ReadByte();
        }

        public static void WriteFullStateSnapshot(BinaryWriter w,
            CarryStateData[] carryStates,
            PartMountStateData[] partMountStates,
            BoltStateData[] boltStates,
            AuthorityStateData[] authorityStates)
        {
            w.Write((ushort)carryStates.Length);
            foreach (var c in carryStates)
            {
                WriteCarryState(w, c.Path, c.Held, c.CarrierPath, c.LocalPos, c.LocalRot, c.ParentVehiclePath);
            }

            w.Write((ushort)partMountStates.Length);
            foreach (var p in partMountStates)
            {
                WritePartMountState(w, p.PartPath, p.Installed, p.CarrierPath, p.LocalPos, p.LocalRot, p.ParentVehiclePath);
            }

            w.Write((ushort)boltStates.Length);
            foreach (var b in boltStates)
            {
                w.Write(b.BoltPath ?? "");
                w.Write(b.Stage);
                w.Write(b.Tightness);
            }

            w.Write((ushort)authorityStates.Length);
            foreach (var a in authorityStates)
            {
                WriteAuthorityState(w, a);
            }
        }

        public static void ReadFullStateSnapshot(BinaryReader r,
            out CarryStateData[] carryStates,
            out PartMountStateData[] partMountStates,
            out BoltStateData[] boltStates,
            out AuthorityStateData[] authorityStates)
        {
            // DİKKAT: DİZİ ELEMANININ ALANLARI out OLARAK GEÇİRİLMİYOR.
            //
            // CANLI KANIT (client logu 14:49:59, stack trace ile):
            //   NullReferenceException
            //     at SteamMP.Protocol.ReadPartMountState (...)
            //     at SteamMP.Protocol.ReadFullStateSnapshot (...)
            //     at SteamMP.JIPHandler.OnFullSnapshot (...)
            //
            // Önceden burada `out partMountStates[i].PartPath` yazılıydı.
            // C# bunu derler (dizi elemanı değişken referansıdır) ama burada
            // çalışan Mono 0.3.0.0 (Unity 5.0) + Roskin'in ürettiği IL
            // dizi-elemanı-alanı byref deseni üzerinde NullReferenceException
            // veriyor. Sonuç: JIP snapshot'ının HİÇBİR KAYDI uygulanmıyor,
            // "alındı" işaretlenmediği için istemci 10 sn'de bir aynı isteği
            // tekrarlıyor (logda 15 kez) — hem bant genişliği hem de
            // "donma" kaynağı.
            //
            // ÇÖZÜM: yerel değişkenlere oku, sonra alana yaz. Her iki
            // okuyucu için de geçerli ve taşınabilir.
            ushort carryCount = r.ReadUInt16();
            carryStates = new CarryStateData[carryCount];
            for (int i = 0; i < carryCount; i++)
            {
                string path, carrierPath, parentVehiclePath;
                bool held;
                Vector3 localPos;
                Quaternion localRot;
                ReadCarryState(r, out path, out held, out carrierPath,
                    out localPos, out localRot, out parentVehiclePath);
                carryStates[i] = new CarryStateData
                {
                    Path = path,
                    Held = held,
                    CarrierPath = carrierPath,
                    LocalPos = localPos,
                    LocalRot = localRot,
                    ParentVehiclePath = parentVehiclePath
                };
            }

            ushort partCount = r.ReadUInt16();
            partMountStates = new PartMountStateData[partCount];
            for (int i = 0; i < partCount; i++)
            {
                string partPath, carrierPath, parentVehiclePath;
                bool installed;
                Vector3 localPos;
                Quaternion localRot;
                ReadPartMountState(r, out partPath, out installed, out carrierPath,
                    out localPos, out localRot, out parentVehiclePath);
                partMountStates[i] = new PartMountStateData
                {
                    PartPath = partPath,
                    Installed = installed,
                    CarrierPath = carrierPath,
                    LocalPos = localPos,
                    LocalRot = localRot,
                    ParentVehiclePath = parentVehiclePath
                };
            }

            ushort boltCount = r.ReadUInt16();
            boltStates = new BoltStateData[boltCount];
            for (int i = 0; i < boltCount; i++)
            {
                boltStates[i] = new BoltStateData
                {
                    BoltPath = r.ReadString(),
                    Stage = r.ReadInt32(),
                    Tightness = r.ReadSingle()
                };
            }

            ushort authCount = r.ReadUInt16();
            authorityStates = new AuthorityStateData[authCount];
            for (int i = 0; i < authCount; i++)
            {
                ReadAuthorityState(r, authorityStates[i]);
            }
        }

        public static void WritePartRecovery(BinaryWriter w, string partPath, Vector3 newPosition)
        {
            w.Write(partPath ?? "");
            w.Write(newPosition.x); w.Write(newPosition.y); w.Write(newPosition.z);
        }

        public static void ReadPartRecovery(BinaryReader r, out string partPath, out Vector3 newPosition)
        {
            partPath = r.ReadString();
            newPosition = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        public static void WriteAuthoritySnapshot(BinaryWriter w, AuthorityStateData[] states)
        {
            w.Write((ushort)states.Length);
            foreach (var s in states) WriteAuthorityState(w, s);
        }

        public static void ReadAuthoritySnapshot(BinaryReader r, out AuthorityStateData[] states)
        {
            states = new AuthorityStateData[r.ReadUInt16()];
            for (int i = 0; i < states.Length; i++) ReadAuthorityState(r, states[i]);
        }

        static void WriteAuthorityState(BinaryWriter w, AuthorityStateData a)
        {
            w.Write(a.ObjectPath ?? "");
            w.Write(a.OwnerSteamId);
            w.Write(a.Held);
            w.Write(a.Installed);
            w.Write(a.HolderPath ?? "");
        }

        static void ReadAuthorityState(BinaryReader r, AuthorityStateData a)
        {
            a.ObjectPath = r.ReadString();
            a.OwnerSteamId = r.ReadUInt64();
            a.Held = r.ReadBoolean();
            a.Installed = r.ReadBoolean();
            a.HolderPath = r.ReadString();
        }
    }
}
