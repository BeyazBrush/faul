using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// Dinamik EÅŸya DoÄŸurma Senkronizasyonu (Item Spawn Sync)
    ///
    /// KullanÄ±m SenaryolarÄ±:
    /// - Teimo maÄŸazasÄ±ndan satÄ±n alÄ±nan Ã¼rÃ¼nler (bira kasasÄ±, sosis, makarna, vb.)
    /// - Spawner nesnelerinden doÄŸan eÅŸyalar (sandÄ±k, konteyner, quest Ã¶dÃ¼lÃ¼)
    /// - Oyuncu tarafÄ±ndan yerleÅŸtirilen/oluÅŸturulan eÅŸyalar
    ///
    /// Mimari:
    /// - Her eÅŸya iÃ§in benzersiz Network ID (uint) atanÄ±r
    /// - Host yetkilidir: Spawn event'i Host'tan Client'e gider
    /// - Client spawn edilen eÅŸyayÄ± kendi sahnesinde instantiate eder
    /// - EÅŸya ID'si (path + instance index) ile eÅŸleÅŸme saÄŸlanÄ±r
    /// </summary>
    public static class ItemSpawnSync
    {
        // --- Ayarlar ---
        public const float SPAWN_COOLDOWN = 0.1f;

        // --- Spawn KaynaÄŸÄ± Tipleri ---
        public enum SpawnSource : byte
        {
            TeimoShop = 0,
            Spawner = 1,
            QuestReward = 2,
            PlayerCraft = 3,
            Admin = 4
        }

        // --- Veri YapÄ±larÄ± ---

        public struct SpawnRequest
        {
            public string ItemPath;
            public Vector3 Position;
            public Quaternion Rotation;
            public SpawnSource Source;
            public ulong RequesterSteamId;
            public int Amount;
            public string ContainerPath;
        }

        public struct SpawnedItem
        {
            public uint NetworkId;
            public string ItemPath;
            public Vector3 Position;
            public Quaternion Rotation;
            public SpawnSource Source;
            public ulong OwnerSteamId;
            public int Amount;
            public string ContainerPath;
            /// <summary>Yerelde doÄŸan nesne. SAHNEYE AÄ°TTÄ°R ve Clear()
            /// sÄ±rasÄ±nda yok edilir; bu yÃ¼zden pakete YAZILMAZ (aÄŸdan
            /// gelen kayÄ±tta null gelir, o tarafta yeniden doÄŸar).</summary>
            public GameObject Instance;
        }

        // --- Durum ---
        static uint nextNetworkId = 1;
        static readonly Dictionary<uint, SpawnedItem> spawnedItems = new Dictionary<uint, SpawnedItem>();
        static readonly Dictionary<string, float> lastSpawnTime = new Dictionary<string, float>();
        static readonly object gate = new object();

        // --- Host API ---

        public static void RequestSpawn(SpawnRequest request)
        {
            if (!SteamTransport.HasRemote) return;

            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.ItemSpawn);
                BinaryWriter w = new BinaryWriter(s);

                w.Write(request.ItemPath ?? "");
                w.Write(request.Position.x); w.Write(request.Position.y); w.Write(request.Position.z);
                w.Write(request.Rotation.x); w.Write(request.Rotation.y); w.Write(request.Rotation.z); w.Write(request.Rotation.w);
                w.Write((byte)request.Source);
                w.Write(request.RequesterSteamId);
                w.Write(request.Amount);
                w.Write(request.ContainerPath ?? "");

                if (SteamTransport.SendReliable(Protocol.Finish(s)))
                {
                    Telemetry.SentItemSpawnRequests++;
                    SmpLog.Log("[ItemSpawnSync] Spawn isteÄŸi gÃ¶nderildi: " + request.ItemPath);
                }
            }
            catch (Exception e)
            {
                SmpLog.Warning("[ItemSpawnSync] Spawn isteÄŸi hatasÄ±: " + e.Message);
            }
        }

        public static void HostProcessSpawnRequest(SpawnRequest request, CSteamID requester)
        {
            string spamKey = request.ContainerPath + "_" + request.ItemPath;
            float now = Time.realtimeSinceStartup;
            float lastTime;
            if (lastSpawnTime.TryGetValue(spamKey, out lastTime))
            {
                if (now - lastTime < SPAWN_COOLDOWN)
                {
                    SmpLog.Warning("[ItemSpawnSync] Spawn spam reddedildi: " + spamKey);
                    return;
                }
            }
            lastSpawnTime[spamKey] = now;

            uint networkId;
            lock (gate)
            {
                networkId = nextNetworkId++;
            }

            GameObject spawnedObj = SpawnItemInWorld(request.ItemPath, request.Position, request.Rotation, request.Amount);
            if (spawnedObj == null)
            {
                SmpLog.Warning("[ItemSpawnSync] EÅŸya dÃ¼nyada oluÅŸturulamadÄ±: " + request.ItemPath);
                return;
            }

            SpawnedItem item = new SpawnedItem
            {
                NetworkId = networkId,
                ItemPath = request.ItemPath,
                Position = request.Position,
                Rotation = request.Rotation,
                Source = request.Source,
                OwnerSteamId = requester.m_SteamID,
                Amount = request.Amount,
                ContainerPath = request.ContainerPath ?? ""
            };

            lock (gate)
            {
                spawnedItems[networkId] = item;
            }

            item.Instance = spawnedObj;
            BroadcastSpawn(item);

            SmpLog.Log("[ItemSpawnSync] Host eÅŸya doÄŸurdu: " + request.ItemPath + " (NetID: " + networkId + ")");
        }

        static void BroadcastSpawn(SpawnedItem item)
        {
            try
            {
                MemoryStream s = Protocol.Begin(Protocol.Msg.ItemSpawn);
                BinaryWriter w = new BinaryWriter(s);

                w.Write(item.NetworkId);
                w.Write(item.ItemPath ?? "");
                w.Write(item.Position.x); w.Write(item.Position.y); w.Write(item.Position.z);
                w.Write(item.Rotation.x); w.Write(item.Rotation.y); w.Write(item.Rotation.z); w.Write(item.Rotation.w);
                w.Write((byte)item.Source);
                w.Write(item.OwnerSteamId);
                w.Write(item.Amount);
                w.Write(item.ContainerPath ?? "");

                if (SteamTransport.SendReliable(Protocol.Finish(s)))
                {
                    Telemetry.SentItemSpawns++;
                }
            }
            catch (Exception e)
            {
                SmpLog.Error("[ItemSpawnSync] Broadcast hatasÄ±: " + e.Message);
            }
        }

        /// <summary>
        /// EÅŸyayÄ± dÃ¼nyada oluÅŸturur ve Ä°KÄ° YOLDAN prefab arar.
        ///
        /// CANLI HATA (statik tarama): yalnÄ±zca `Resources.Load` deneniyordu.
        /// MSC'nin varlÄ±klarÄ± `sharedassets*.assets` iÃ§indedir, `Resources`
        /// klasÃ¶rÃ¼nde DEÄÄ°LDÄ°R â€” yani bu yol gerÃ§ek bir eÅŸya iÃ§in DAÄ°MA
        /// null dÃ¶ner ve sistem hiÃ§ Ã§alÄ±ÅŸmaz. `RequestSpawn` zaten hiÃ§
        /// Ã§aÄŸrÄ±lmadÄ±ÄŸÄ± iÃ§in bu gÃ¶rÃ¼nmÃ¼yordu.
        ///
        /// MSC'de Ã§alÄ±ÅŸan yol: sahnedeki canlÄ± bir Ã¶rneÄŸi bulup klonlamak.
        /// Klonlarken KAYNAÄI KAPATIRIZ; aksi halde ikinci bir kopya kalÄ±r
        /// (client'ta Ã§ift eÅŸya). Eski kodun "klonlama yanlÄ±ÅŸ" yorumu
        /// doÄŸruydu ama Ã§Ã¶zÃ¼mÃ¼ yanlÄ±ÅŸtÄ±: sorun klonlama deÄŸil, kaynaÄŸÄ±n
        /// aÃ§Ä±k kalmasÄ±ydÄ±.
        /// </summary>
        static GameObject SpawnItemInWorld(string itemPath, Vector3 pos, Quaternion rot, int amount)
        {
            try
            {
                if (string.IsNullOrEmpty(itemPath)) return null;

                GameObject obj = null;
                string how = "Resources";
                try { obj = Resources.Load<GameObject>(itemPath) as GameObject; } catch { }
                bool fromScene = false;
                if (obj == null)
                {
                    obj = FindTemplateInScene(itemPath);
                    fromScene = obj != null;
                    how = "sahne";
                }
                if (obj == null)
                {
                    // KÄ°MLÄ°KSÄ°Z hata yerine NE TARANDIÄINI yaz.
                    SmpLog.Warning("[ItemSpawnSync] prefab bulunamadi: '" + itemPath
                        + "'  Resources'te yok, sahnede de yok. MSC varliklari "
                        + "sharedassets icinde oldugu icin Resources.Load calismaz; "
                        + "sahne yolu kullanilir. 'smp item find <ad>' ile adini ara.");
                    return null;
                }

                if (fromScene)
                {
                    // Kaynak KAPALI kalsÄ±n ki klonla birlikte ikinci bir
                    // Ã¶rnek oluÅŸmasÄ±n. Oturum bitince geri aÃ§Ä±lÄ±r.
                    try
                    {
                        if (obj.activeSelf)
                        {
                            obj.SetActive(false);
                            disabledTemplates.Add(obj);
                        }
                    }
                    catch { }
                }

                GameObject spawned = (GameObject)UnityEngine.Object.Instantiate(obj, pos, rot);
                if (spawned == null) return null;
                spawned.name = obj.name + "_net";

                if (amount > 1)
                {
                    // DoÄŸru nesne: SPAWN EDÄ°LEN kopya, prefab deÄŸil.
                    var itemComp = spawned.GetComponent<PlayMakerFSM>();
                    if (itemComp == null)
                        itemComp = spawned.GetComponentInChildren<PlayMakerFSM>();
                    if (itemComp != null)
                    {
                        FsmInt amountVar = FsmVars.Int(itemComp, "Amount", "amount", "Count", "Quantity");
                        if (amountVar != null) amountVar.Value = amount;
                    }
                }

                SmpLog.Log("[ItemSpawnSync] doguruldu: " + spawned.name
                    + "  kaynak=" + how + (fromScene ? " (sahne, kapali)" : ""));
                return spawned;
            }
            catch (Exception e)
            {
                SmpLog.Error("[ItemSpawnSync] Instantiate hatasÄ±: " + e.Message);
                return null;
            }
        }

        /// <summary>Klonlama iÃ§in kapatÄ±lmÄ±ÅŸ ÅŸablonlar (oturum sonunda aÃ§Ä±lÄ±r).</summary>
        static readonly List<GameObject> disabledTemplates = new List<GameObject>();

        /// <summary>
        /// Sahnedeki canlÄ± bir eÅŸyayÄ± ÅŸablon olarak bulur.
        /// `itemPath` "Kok/alt/yol" biÃ§imindeyse yol olarak Ã§Ã¶zÃ¼lÃ¼r,
        /// deÄŸilse ada gÃ¶re aranÄ±r.
        /// </summary>
        static GameObject FindTemplateInScene(string itemPath)
        {
            try
            {
                GameObject[] roots = SceneDump.AllSceneRoots();
                if (roots == null) return null;

                int slash = SceneDump.RootNameOf(itemPath).Length;
                if (slash > 0 && slash < itemPath.Length - 1)
                {
                    string rootName = itemPath.Substring(0, slash);
                    string rest = itemPath.Substring(slash + 1);
                    for (int i = 0; i < roots.Length; i++)
                    {
                        GameObject r = roots[i];
                        if (r == null) continue;
                        if (!string.Equals(r.name, rootName, StringComparison.Ordinal)) continue;
                        Transform t = r.transform.Find(rest);
                        if (t != null) return t.gameObject;
                    }
                    return null;
                }

                // Ada gÃ¶re: EN KISA yollu (kÃ¶ke en yakÄ±n) olanÄ± seÃ§; maÄŸaza
                // raflarÄ±ndaki kopyalar alt klasÃ¶rlerde de bulunuyor.
                GameObject best = null;
                int bestDepth = int.MaxValue;
                for (int i = 0; roots != null && i < roots.Length; i++)
                {
                    GameObject r = roots[i];
                    if (r == null) continue;
                    Transform[] all = r.GetComponentsInChildren<Transform>(true);
                    for (int k = 0; all != null && k < all.Length; k++)
                    {
                        Transform t = all[k];
                        if (t == null || t.name != itemPath) continue;
                        int d = 0;
                        Transform p = t.parent;
                        while (p != null) { d++; p = p.parent; }
                        if (d < bestDepth) { bestDepth = d; best = t.gameObject; }
                    }
                    if (best != null && bestDepth == 1) break;
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// AdÄ± geÃ§en sahne nesnelerini listeler â€” spawn edilemeyen eÅŸyanÄ±n
        /// GERÃ‡EK adÄ±nÄ± bulmak iÃ§in ('smp item find <ad>').
        /// </summary>
        public static string FindNamed(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return "(ad gerekli)";
                GameObject[] roots = SceneDump.AllSceneRoots();
                int n = 0;
                System.Text.StringBuilder sb = new System.Text.StringBuilder(700);
                for (int i = 0; roots != null && i < roots.Length && n < 25; i++)
                {
                    GameObject r = roots[i];
                    if (r == null) continue;
                    Transform[] all = r.GetComponentsInChildren<Transform>(true);
                    for (int k = 0; all != null && k < all.Length && n < 25; k++)
                    {
                        Transform t = all[k];
                        if (t == null) continue;
                        string p = "?";
                        try { p = SceneDump.PathOf(t); } catch { }
                        if (p.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (sb.Length > 0) sb.Append("\n        ");
                        sb.Append(p);
                        n++;
                    }
                }
                return n > 0 ? sb.ToString() : "(eslesme yok)";
            }
            catch (Exception e) { return "tarama hatasi: " + e.Message; }
        }

        /// <summary>Klonlama iÃ§in kapatÄ±lan ÅŸablonlarÄ± geri aÃ§ar.</summary>
        static void ReenableTemplates()
        {
            for (int i = 0; i < disabledTemplates.Count; i++)
            {
                try { if (disabledTemplates[i] != null) disabledTemplates[i].SetActive(true); }
                catch { }
            }
            disabledTemplates.Clear();
        }

        // --- Client API ---

        /// <summary>
        /// Msg.ItemSpawn GELENÄ°. Hangi taraf olduÄŸumuza gÃ¶re Ä°KÄ° FARKLI
        /// kayÄ±t dÃ¼zeni vardÄ±r ve bu eskiden karÄ±ÅŸtÄ±rÄ±lÄ±yordu.
        ///
        /// CANLI HATA (statik tarama): `Msg.ItemSpawn` her zaman
        /// `OnMessage`'a (client/broadcast okuyucu) gidiyordu. Yani
        /// client `RequestSpawn` ile istek gÃ¶nderdiÄŸinde HOST onu
        /// broadcast sanÄ±p `networkId` alanÄ±nÄ± `itemPath`'in uzunluk
        /// baytlarÄ±ndan okuyordu â†’ akÄ±ÅŸ kaymasÄ±. `RequestSpawn` hiÃ§
        /// Ã§aÄŸrÄ±lmadÄ±ÄŸÄ± iÃ§in bu hata UYUYODDU; ilk Ã§aÄŸrÄ±da patlardÄ±.
        ///
        /// DÃœZELTME: rol belirleyici. Host istek dÃ¼zenini okur ve iÅŸler,
        /// client yayÄ±n dÃ¼zenini okur. P2P 1'e 1 olduÄŸu iÃ§in iki taraf
        /// da hangi rolde olduklarÄ±nÄ± BÄ°LÄ°R, dÃ¼zenler Ã§akÄ±ÅŸmaz.
        /// </summary>
        public static void OnMessage(BinaryReader r)
        {
            if (SteamTransport.IsHost) { HostOnSpawnRequest(r); return; }
            OnSpawnBroadcast(r);
        }

        /// <summary>Host: client'Ä±n spawn Ä°STEÄÄ° (broadcast dÃ¼zeni DEÄÄ°L).</summary>
        static void HostOnSpawnRequest(BinaryReader r)
        {
            try
            {
                SpawnRequest req = new SpawnRequest
                {
                    ItemPath         = r.ReadString(),
                    Position         = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Rotation         = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Source           = (SpawnSource)r.ReadByte(),
                    RequesterSteamId = r.ReadUInt64(),
                    Amount           = r.ReadInt32(),
                    ContainerPath    = r.ReadString(),
                };
                if (string.IsNullOrEmpty(req.ItemPath)) return;
                // SAHÄ°PLÄ°K GÃœVENLÄ°ÄÄ°: paket iÃ§indeki kimliÄŸe deÄŸil,
                // GERÃ‡EK gÃ¶ndericiye bakÄ±yoruz. Aksi halde bir istemci
                // baÅŸkasÄ±nÄ±n adÄ±na eÅŸya doÄŸurabilirdi.
                req.RequesterSteamId = SteamTransport.RemoteId.m_SteamID;
                Telemetry.ReceivedItemSpawnRequests++;
                HostProcessSpawnRequest(req, SteamTransport.RemoteId);
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[ItemSpawnSync] Spawn isteÄŸi hatasÄ±: " + e.Message);
            }
        }

        /// <summary>Client: host'un doÄŸurduÄŸu eÅŸyanÄ±n yayÄ±nÄ±.</summary>
        static void OnSpawnBroadcast(BinaryReader r)
        {
            try
            {
                uint networkId = r.ReadUInt32();
                string itemPath = r.ReadString();
                Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Quaternion rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                SpawnSource source = (SpawnSource)r.ReadByte();
                ulong ownerSteamId = r.ReadUInt64();
                int amount = r.ReadInt32();
                string containerPath = r.ReadString();

                SpawnedItem item = new SpawnedItem
                {
                    NetworkId = networkId,
                    ItemPath = itemPath,
                    Position = pos,
                    Rotation = rot,
                    Source = source,
                    OwnerSteamId = ownerSteamId,
                    Amount = amount,
                    ContainerPath = containerPath
                };

                Telemetry.ReceivedItemSpawns++;

                ClientSpawnItem(item);
            }
            catch (Exception e)
            {
                Telemetry.BadPackets++;
                SmpLog.Warning("[ItemSpawnSync] Paket iÅŸleme hatasÄ±: " + e.Message);
            }
        }

        static void ClientSpawnItem(SpawnedItem item)
        {
            try
            {
                GameObject obj = SpawnItemInWorld(item.ItemPath, item.Position, item.Rotation, item.Amount);
                if (obj == null) return;

                var netIdComp = obj.AddComponent<NetworkIdentity>();
                netIdComp.NetworkId = item.NetworkId;
                netIdComp.ItemPath = item.ItemPath;
                netIdComp.OwnerSteamId = item.OwnerSteamId;

                item.Instance = obj;
                lock (gate)
                {
                    spawnedItems[item.NetworkId] = item;
                }

                SmpLog.Log("[ItemSpawnSync] Client eÅŸya aldÄ±: " + item.ItemPath + " (NetID: " + item.NetworkId + ")");
            }
            catch (Exception e)
            {
                SmpLog.Error("[ItemSpawnSync] Client spawn hatasÄ±: " + e.Message);
            }
        }

        public class NetworkIdentity : MonoBehaviour
        {
            public uint NetworkId;
            public string ItemPath;
            public ulong OwnerSteamId;
        }

        public static void Clear()
        {
            lock (gate)
            {
                // EÅŸyalar SAHNEYE aittir, oturuma deÄŸil: takip tablosunu
                // sÄ±fÄ±rlamak yetmez, takip edilen nesneler de yok edilmelidir.
                // Sahne deÄŸiÅŸiminde bunlar zaten Unity tarafÄ±ndan yok edilmÄ±ÅŸ
                // olur; Destroy null'a karÅŸÄ± gÃ¼venlidir.
                foreach (var kv in spawnedItems)
                {
                    if (kv.Value.Instance != null)
                        UnityEngine.Object.Destroy(kv.Value.Instance);
                }

                spawnedItems.Clear();
                lastSpawnTime.Clear();
                // Klonlama iÃ§in kapatÄ±lan SAHNE ÅABLONLARINI geri aÃ§.
                // Aksi halde maÄŸazadaki eÅŸya kalÄ±cÄ± olarak kaybolur.
                ReenableTemplates();
                nextNetworkId = 1;
            }
        }

        public static void OnItemDestroyed(uint networkId)
        {
            lock (gate)
            {
                spawnedItems.Remove(networkId);
            }
        }

        public static SpawnedItem GetItem(uint networkId)
        {
            lock (gate)
            {
                SpawnedItem item;
                spawnedItems.TryGetValue(networkId, out item);
                return item;
            }
        }

        public static string Describe()
        {
            return "ItemSpawnSync: tracked=" + spawnedItems.Count + ", nextNetId=" + nextNetworkId;
        }
    }
}
