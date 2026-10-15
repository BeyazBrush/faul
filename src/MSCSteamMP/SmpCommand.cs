using System;
using System.Collections.Generic;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// 'smp host | join | find | leave | status | rescan | sync | autoload | enter
	///     | verbose | copy | clone | player | world | items | wheel | menu | log'
	///
	/// Döküm komutları neden yok
	/// -----------------------
	/// Daha önce sahne ve FSM dökümü konsola basılıyordu: smp scene / ls / go /
	/// fsm / var / scan / npcs / view / parts / bolts. Hepsi aynı işi yapıyordu:
	/// "ne oluyor?" sorusunun cevabını loglardan okumak yerine komutla sorgulamak.
	///
	/// Artık gerek kalmadı. Mod ne yaptığını iki yere birden yazıyor:
	///
	///   * " tuşu ile açılan ekran konsörü (canlı, filtre kutusu ve fare ile
	///     kaydırmalı)
	///   * Mods\mscmp.txt (kalıcı, iki oyuncu arasında paylaşılabilir)
	///
	/// Kullanıcı artık sorgu komutu yazmak zorunda değil; olan biteni okuyor. Bu
	/// yüzden komut yüzeyi küçüldü: sahne müfettişi (~410 satır), ekran
	/// görüntüleyici ve NPC dökümü moddan tamamen çıkarıldı.
	///
	/// Kalan komutlar ya durum bildirir ya da eylem yapar. "smp status" ve modül
	/// komutları (world / items / wheel) tek satır özet verir, satır listesi değil.
	/// </summary>
	public class SmpCommand : ConsoleCommand {

		public override string Name => "smp";

		/// <summary>
		/// Alt komut adları. 'smp help' çıktısının ayrışmasını önlemek için tek
		/// kaynak: hem liste hem ayrıştırıcı buradan okuyor.
		/// </summary>
		static readonly string[] SUBCOMMANDS = {
			"host", "join", "find", "leave", "status",
			"rescan", "sync", "autoload", "enter", "verbose", "copy",
			"world", "items", "item", "wheel", "menu",
			"player", "log", "parts", "fluids", "globals", "god", "discord", "songs",
			"dashboard", "battery",
			"authority", "yetki",
			"getsave", "save",
		};

		public override string Help =>
			"MSCSteamMP. Session: 'smp host' create a lobby, 'smp join' find and join automatically "
			+ "(or 'smp join <lobbyId>'), 'smp find' search only, 'smp leave' disconnect, "
			+ "'smp enter' load the world (same as CONTINUE), "
			+ "'smp autoload on|off' enter the world automatically once the host is in it. "
			+ "Sync: 'smp status', 'smp rescan', 'smp sync', 'smp verbose', 'smp copy'. "
			+ "Modules: 'smp world', 'smp items', 'smp wheel' one-line status. "
			+ "Remote player: 'smp player' model, body, animation and seat state. "
			+ "Menu: 'smp menu [code [new]]' custom menu state and the lobby code. "
			+ "Log: 'smp log' opens the whole logging surface - 'smp log health', 'smp log levels', "
			+ "'smp log stats', 'smp log dump', 'smp log remote on'. "
			+ "Appearance: 'smp skin list', 'smp skin 12', 'smp skin Teimo' - or the GORUNUM "
			+ "row in the main menu (3D preview). "
			+ "Godmode: 'smp god on|off' - deaths NEVER complete (wasp/bee, crashes, fire, "
			+ "drowning): the death is intercepted and the player is teleported to the first "
			+ "safe position of the loaded save. "
			+ "Death sync was removed in v15; godmode is the only death guard. ";

		public override void Run(string[] args) {
			string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";
			if (sub == "help" || sub == "?") { PrintHelp(); return; }

			// ---------------- performans ----------------
			// 'smp perf' : paneli ac/kapa. Arguman yoksa anlik olcum yazar,
			// boylece F8 bilmeden de "kasiliyor" sorusu cevaplanabilir.
			if (sub == "perf" || sub == "fps") {
				if (args.Length < 2) {
					SmpLog.Print(PerfHud.Describe());
					SmpLog.Print("panel is " + (PerfHud.Visible ? "ON" : "off")
						+ " - press F8 or run 'smp perf' to toggle.");
					return;
				}
				string on = args[1].ToLowerInvariant();
				if (on == "on") PerfHud.Visible = true;
				else if (on == "off") PerfHud.Visible = false;
				else PerfHud.Toggle();
				SmpLog.Print("performance panel " + (PerfHud.Visible ? "ON" : "off") + ".");
				SmpLog.Print(PerfHud.Describe());
				return;
			}

			// ---------------- eşitleme ----------------
			if (sub == "rescan") {
				VehicleRegistry.Rescan();
				SatsumaRegistry.Install();
				SatsumaRegistry.OnWorldReady();
				return;
			}
			if (sub == "sync") {
				if (!SteamTransport.IsHost) { SmpLog.Print("only the host can send full state."); return; }
				Telemetry.Reset();
				SatsumaRegistry.BeginInitialSync();
				return;
			}
			if (sub == "verbose") {
				Telemetry.Verbose = !Telemetry.Verbose;
				SmpLog.Print("verbose: " + (Telemetry.Verbose ? "ON" : "OFF"));
				return;
			}
			if (sub == "copy") {
				if (SteamTransport.LobbyId.IsValid()) {
					CopyToClipboard(SteamTransport.LobbyId.m_SteamID.ToString());
					SmpLog.Print("lobby ID copied: " + SteamTransport.LobbyId.m_SteamID);
				}
				else SmpLog.Print("no lobby. 'smp host' first.");
				return;
			}
			if (sub == "autoload") {
				if (args.Length >= 2) {
					string v = args[1].ToLowerInvariant();
					if (v == "on" || v == "1" || v == "true") { AutoLoad.Enabled = true; }
					else if (v == "off" || v == "0" || v == "false") { AutoLoad.Enabled = false; }
					else { SmpLog.Error("usage: smp autoload on|off"); return; }
				}
				SmpLog.Print("autoload: " + (AutoLoad.Enabled ? "ON" : "OFF")
					+ "  (join the world automatically once the host is in it; also warns "
					+ "when only one side has loaded a save)");
				return;
			}

			// ---------------- oturum: host / join / find / leave ----------------
			// Yardim metni bunlari vadediyordu ama calismiyorlardi (Status'a
			// dusuyorlardi). Menu disinda konsoldan baglanmak icin.
			if (sub == "host") {
				LobbyCode.Generate();
				SteamTransport.Host();
				return;
			}
			if (sub == "leave") {
				SteamTransport.Leave();
				return;
			}
			if (sub == "find") {
				SteamTransport.DiscoverLobbies();
				SmpLog.Print("searching... run 'smp find' again to list, or 'smp join <lobbyId>' to join directly.");
				if (SteamTransport.FoundLobbies.Count > 0) {
					for (int i = 0; i < SteamTransport.FoundLobbies.Count; i++) {
						SmpLog.Print("  lobby " + SteamTransport.FoundLobbies[i].m_SteamID);
					}
				}
				return;
			}
			if (sub == "join") {
				if (args.Length < 2) {
					SteamTransport.JoinAuto();
					return;
				}
				ulong id;
				if (ulong.TryParse(args[1], out id) && id > 0UL) {
					SteamTransport.Join(id);
					return;
				}
				SmpLog.Error("usage: smp join <lobbyId> (17-digit number from host log) or plain 'smp join' for auto-discovery.");
				return;
			}

			// ---------------- discord ----------------
			if (sub == "discord" || sub == "dpc") {
				if (args.Length >= 3 && string.Equals(args[1], "id", StringComparison.OrdinalIgnoreCase)) {
					DiscordPresence.ClientId = args[2].Trim();
					DiscordPresence.SaveSettings();
					SmpLog.Print("discord clientId = " + DiscordPresence.ClientId + " (saved).");
					return;
				}
				if (args.Length >= 2) {
					string v = args[1].ToLowerInvariant();
					if (v == "on" || v == "1" || v == "true") DiscordPresence.Enabled = true;
					else if (v == "off" || v == "0" || v == "false") DiscordPresence.Disable();
					else if (v == "reset") DiscordPresence.ForceUpdate();
					else { SmpLog.Error("usage: smp discord [on|off|reset|id <appId>]"); return; }
				}
				if (string.IsNullOrEmpty(DiscordPresence.ClientId)) {
					SmpLog.Print("discord: clientId is not set. Create an app at "
						+ "discord.com/developers and run: smp discord id <your app id>");
					return;
				}
				SmpLog.Print(DiscordPresence.Describe());
				return;
			}

			// ---------------- godmode ----------------
			// ---------------- muzik ----------------
			if (sub == "songs" || sub == "music" || sub == "cd") {
				if (args.Length >= 2) {
					string a = args[1].ToLowerInvariant();
					if (a == "import") CdMusicImport.Send();
					else if (a == "rescan") CdMusicImport.Discover();
					else if (a == "reset") CdMusicImport.ResetAndRetry();
					else { SmpLog.Error("usage: smp songs [import|rescan|reset]"); return; }
				}
				// v15: CD PLAYER durumu da ayni ciktinin sonuna eklenir.
				// `CdPlayerSync` ayri bir komut DEGIL: kullanici "smp cd"
				// yazinca hem import durumunu hem de
				// CDPlayerOK/RadioOK/trigger_cd.Setup/trigger_disc.CDin
				// (yani "karsi tarafta gorunecek mi") bilgisini gormeli.
				SmpLog.Print(CdMusicImport.Describe());
				try { SmpLog.Print(CdPlayerSync.Describe()); } catch { }
				return;
			}

			if (sub == "dashboard" || sub == "dash") {
				if (args.Length >= 2) {
					string a = args[1].ToLowerInvariant();
					if (a == "on") DashboardPartSync.Enabled = true;
					else if (a == "off") DashboardPartSync.Enabled = false;
					else if (a == "rescan") { /* Tick yeniden tarar */ }
					else { SmpLog.Error("usage: smp dashboard [on|off]"); return; }
				}
				SmpLog.Print(DashboardPartSync.Describe());
				SmpLog.Print(BatteryKeepFull.Describe());
				return;
			}

			if (sub == "battery" || sub == "aku") {
				if (args.Length >= 2) {
					string a = args[1].ToLowerInvariant();
					if (a == "on") BatteryKeepFull.Enabled = true;
					else if (a == "off") BatteryKeepFull.Enabled = false;
					else { SmpLog.Error("usage: smp battery [on|off]"); return; }
				}
				SmpLog.Print(BatteryKeepFull.Describe());
				return;
			}

			if (sub == "god" || sub == "godmode") {
				if (args.Length >= 2) {
					string v = args[1].ToLowerInvariant();
					if (v == "on" || v == "1" || v == "true") GodMode.Set(true);
					else if (v == "off" || v == "0" || v == "false") GodMode.Set(false);
					else { SmpLog.Error("usage: smp god [on|off]"); return; }
				} else {
					// Argümansız: aç/kapa çevir
					GodMode.Set(!GodMode.Enabled);
				}
				SmpLog.Print(GodMode.Describe());
				return;
			}

			// ---------------- kayıt aktarımı ----------------
			if (sub == "getsave" || sub == "save") {
				// İSTEMCİ ister, HOST gönderir. Otomatik değil: karşı tarafın
				// kaydını sessizce ezmek istemiyoruz (bkz. SaveTransfer).
				if (args.Length >= 2 && args[1].ToLowerInvariant() == "abort") {
					if (SteamTransport.IsHost) {
						SaveTransfer.AbortSend("komutla iptal");
						SmpLog.Print("kayıt aktarımı iptal edildi.");
					} else {
						SmpLog.Print("yalnızca host gönderimi iptal edebilir.");
					}
					return;
				}
				SaveTransfer.RequestFromHost();
				SmpLog.Print(SaveTransfer.Describe());
				return;
			}

			// ---------------- dünya ----------------
			if (sub == "world") {
				if (args.Length >= 3 && args[1].ToLowerInvariant() == "scan") {
					WorldSync.Scan(args[2]);
					return;
				}
				if (args.Length >= 2 && args[1].ToLowerInvariant() == "scan") {
					WorldSync.Scan("");
					return;
				}
				if (args.Length >= 3 && args[1].ToLowerInvariant() == "bind") {
					if (WorldSync.AddName(args[2])) SmpLog.Print("world: '" + args[2] + "' bound.");
					else SmpLog.Print("world: '" + args[2] + "' already known.");
					SmpLog.Print(WorldSync.Describe());
					return;
				}
				SmpLog.Print(WorldSync.Describe());
				return;
			}
			// ---------------- eşya ----------------
			//   smp item                 -> takip edilen eşyaların durumu
			//   smp item <ad>             -> sahnedeki eşyayı takibe al
			//   smp item find <parça>     -> ada göre SAHNEDE ara (yol ver)
			//   smp item spawn <ad> [n]   -> eşyayı doğur ve karşı tarafa gönder
			//
			// NEDEN 'spawn' EKLENDİ: ItemSpawnSync.RequestSpawn HİÇBİR YERDEN
			// çağrılmıyordu (statik tarama). Yani eşya doğma sistemi hazırdı
			// ama tetiklenmiyordu; 'smp' ile elle doğrulama mümkün.
			if (sub == "items" || sub == "item") {
				if (args.Length < 2) {
					SmpLog.Print(ItemSync.Describe());
					SmpLog.Print("  smp item <ad>          -> sahneden takibe al");
					SmpLog.Print("  smp item find <parca>  -> ada gore sahnede ara");
					SmpLog.Print("  smp item spawn <ad> [adet]");
					SmpLog.Print(ItemSpawnSync.Describe());
					return;
				}

				if (args[1] == "find") {
					if (args.Length < 3) {
						SmpLog.Print("smp item find <parca>");
						return;
					}
					SmpLog.Print("'" + args[2] + "' icin sahne yollari:");
					SmpLog.Print("        " + ItemSpawnSync.FindNamed(args[2]));
					return;
				}

				if (args[1] == "spawn") {
					if (args.Length < 3) {
						SmpLog.Print("smp item spawn <sahne-adı> [adet]");
						SmpLog.Print("once adı dogrula:  smp item find <parca>");
						return;
					}
					int amount = 1;
					if (args.Length >= 4) {
						int.TryParse(args[3],
							System.Globalization.NumberStyles.Integer,
							System.Globalization.CultureInfo.InvariantCulture,
							out amount);
						if (amount <= 0) amount = 1;
					}
					Vector3 at;
					if (!PlayerMotion.TryGetPosition(out at)) {
						Camera cam = Camera.main != null ? Camera.main : Camera.current;
						at = cam != null ? cam.transform.position : Vector3.zero;
					}
					// PlayerMotion'te dönüş erişimi yok (yalnızca konum):
					// kamera yönünü kullanıyoruz, yoksa kimlik.
					Quaternion rot = Quaternion.identity;
					Camera c2 = Camera.main != null ? Camera.main : Camera.current;
					if (c2 != null) rot = c2.transform.rotation;
					// +1 m öne koy: klon kendi içinde bize çarpmasın.
					ItemSpawnSync.RequestSpawn(new ItemSpawnSync.SpawnRequest {
						ItemPath = args[2],
						Position = at + c2.transform.forward * 1f + new Vector3(0f, 0.5f, 0f),
						Rotation = rot,
						Source = ItemSpawnSync.SpawnSource.Admin,
						Amount = amount,
					});
					SmpLog.Print(ItemSpawnSync.Describe());
					return;
				}

				if (ItemSync.TrackByName(args[1])) SmpLog.Print(ItemSync.Describe());
				else SmpLog.Error("item '" + args[1] + "' not found in the scene.");
				return;
			}
			if (sub == "wheel") {
				SmpLog.Print(WheelSync.Describe());
				// Direksiyonun ARAÇA YAZILDIĞININ kanıtı. Gönderilen/gelen
				// paket sayısı tek başına yetmez: belirti "direksiyon çevirdim,
				// arkadaşımda görünmüyor" idi ve paketler geliyordu ama hiçbir
				// araca yazılmıyordu. Sayaç 0 ise uygulama yolu çalışmıyor.
				SmpLog.Print(WheelSync.DescribeApplied());
				SmpLog.Print(PlayerMotion.Describe());
				return;
			}

			// Kapi / musluk / dus ve radyo-CD ayri tanilari: bu moduller
			// oyunun FSM'ine baglandigi icin "kac degisken bulundu"
			// sorusu en onemli tanidir.
			if (sub == "doors" || sub == "door" || sub == "objects") {
				// 'smp doors door' -> yalnizca adinda 'door' gecen nesneler
				// (46 nesne var, varsayilan rapor ilk 12'yi basiyor).
				WorldObjectSync.ReportBindings(args.Length >= 2 ? args[1] : null);
				return;
			}

			// Kayit dosyasi tag'leri: ES2File.GetTags (dnSpy 0x0600019C). Civata
			// kademelerinin GERCEK adlari burada; oyun C#'de degil, ES2
			// anahtarlarinda tutuyor.
			if (sub == "savetags" || sub == "tags" || sub == "save") {
				string filter = null;
				bool all = false;
				if (args.Length >= 2) {
					if (args[1] == "--all") all = true;
					else filter = args[1];
				}
				SaveTags.Run(filter, all);
				return;
			}

			// Eylem günlüğü: vida sıkma, eşya alma, kapı açma, motor. Grep: '[ACT]'.
			if (sub == "trace" || sub == "act" || sub == "eylem") {
				if (args.Length >= 2) {
					string what = args[1].ToLowerInvariant();
					if (what == "off" || what == "kapat") SmpTrace.Set(false, false);
					else if (what == "on" || what == "ac") SmpTrace.Set(true, false);
					else if (what == "reset" || what == "sifirla") SmpTrace.Set(SmpTrace.Enabled, true);
				}
				SmpLog.Print(SmpTrace.Describe());
				return;
			}
			if (sub == "radio" || sub == "cd") {
				// 'smp radio all'    -> Satsuma'nin CD/radyosu da senkronlansin
				// 'smp radio no-car' -> yalnizca ev/garaj radyolari
				// Canli log kaniti: bu build'de sahnedeki TEK CD adayi
				// Satsuma'ninki ("Skipped 2"), yani filtre varsayilan olursa
				// radyo tamamen susuyor. Varsayilan DAHIL.
				if (args.Length >= 2) {
					string v = args[1].ToLowerInvariant();
					if (v == "all" || v == "with-car" || v == "car") RadioSync.SetIncludeSatsuma(true);
					else if (v == "no-car" || v == "without-car") RadioSync.SetIncludeSatsuma(false);
				}
				SmpLog.Print(RadioSync.Describe());
				return;
			}
			if (sub == "fluids" || sub == "fluid" || sub == "wear") {
				FluidWearSync.Report();
				return;
			}
			if (sub == "globals" || sub == "global") {
				FsmInspector.DumpGlobals(args.Length >= 2 ? args[1] : null);
				return;
			}
			if (sub == "warnlist" || sub == "warnings") {
				List<string> pending = FsmVars.PendingWarnings;
				if (pending.Count == 0) {
					SmpLog.Print("no pending FSM variable warnings in this world.");
					return;
				}
				SmpLog.Print(pending.Count + " pending warning(s):");
				for (int i = 0; i < pending.Count; i++) SmpLog.Print("  " + pending[i]);
				return;
			}
			if (sub == "fsm" || sub == "vars" || sub == "inspect") {
				if (args.Length < 2) {
					SmpLog.Error("usage: smp fsm <objectName|'*'> [variableFilter]");
					SmpLog.Print("examples: smp fsm FrontDoor | smp fsm KitchenWaterTap | smp fsm * Track");
					return;
				}
				string name = args[1];
				string filter = args.Length >= 3 ? args[2] : null;
				if (name == "*") { FsmInspector.DumpAllWithName(filter, 12); return; }
				FsmInspector.Dump(name, filter);
				return;
			}

			// ---------------- dünyaya giriş ----------------
			// Yardım metni 'smp enter' vaat ediyordu ama komut yoktu, yani
			// konsoldan dünyaya girmenin yolu yalnızca menüydü. AutoLoad'in
			// kullandığı aynı yolu çağırıyoruz.
			if (sub == "enter" || sub == "load") {
				AutoLoad.EnterWorld();
				return;
			}

						// ---------------- ağ istatistiği ----------------
			// NEDEN: log'da paket basina iz yoktu. Kapı/kontak/motor/vida
			// için 'gönderiliyor mu' ayırt edilemiyordu; [ACT] satırları
			// yerel eylem günlüğü. Bu tablo mesaj başına gönderilen/alınan
			// sayıyı gösterir ve tek oturumda teşhis yeter.
			//
			//   gonder=0           -> o yol hiç tetiklenmedi (keşif hatası)
			//   gonder>0 alin=0    -> gidiyor ama teslim edilmiyor
			//   gonder>0 alin>0    -> iki yönlü de çalışıyor
			if (sub == "netstat" || sub == "net") {
				SmpLog.Print(Protocol.NetStats.Describe());
				// UYGULAMA KATMANI: teslim %100 olsa bile paket
				// UYGULANMAYABILIR. Iki katman ayri ayri yazilir; once
				// tasima, sonra huni okunur.
				//
				// CANLI HATA: once yalnizca tasima olculuyordu. "Parca/vida
				// senkronu calismiyor" deniyordu ama teslim %100 gosteriyordu;
				// kaybin YERINI bilmek icin o katman sayaci gerekiyordu.
				SmpLog.Print(Telemetry.ApplyFunnel());
				SmpLog.Print("Okuma: 'uygulandi 300/301' -> 1 kayit dustu, "
					+ "satirdaki 'dustu:' kovasi HANGISI o 1'i anlatir.");
				SmpLog.Print("Iki istemci de acikken calistir: birinde "
					+ "gonder, digerinde al kuyrugu gorunmelidir.");
				return;
			}

			// ---------------- mesafe kalkıkanı ----------------
			//   smp authority           -> eşikleri göster
			//   smp authority           -> yetki/spam korumasi durumu
			//
			// v15: eski "smp authority <metre>" kaldirildi. O komut mesafe
			// esiklerini ayarliyordu ama AuthorityManager'daki
			// CheckDistance'in HICBIR canli cagrisi yoktu (tek giris noktasi
			// ValidatePartState idi, o da sifir cagriliyordu; dolayisiyla
			// ValidateBoltAction / ValidatePartPickup /
			// ValidatePartAssemble / ValidateWorldObjectInteraction da hic
			// cagrilmiyordu). Ayarlanan bir sey yoktu.

				if (sub == "authority" || sub == "yetki") {
					// v15: MESAFE DOGRULAMASI KALDIRILDI.
					//
					// Eski komut esikleri gosteriyor ve '<metre>' ile ayarliyordu.
					// Ama AuthorityManager'daki CheckDistance'in CANLI CAGIRANI
					// YOKTU: tek giris noktasi ValidatePartState idi, o da sifir
					// cagriliyordu; dolayisiyla ValidateBoltAction /
					// ValidatePartPickup / ValidatePartAssemble /
					// ValidateWorldObjectInteraction da hic cagrilmiyordu.
					// Yani ayarlanan bir sey yoktu.
					//
					// Kalan koruma: ValidateConvergence / ValidateBolt /
					// ValidatePartEventReplay - bunlar mesafe OLCMEZ, ayni deger
					// tekrarini eler (0.15 s cooldown).
					//
					// Artik komut yalnizca gercegi raporluyor; sayi kabul etmez.
					SmpLog.Print(AuthorityManager.DescribeThresholds());
					if (InteractionManager.Authority != null)
						SmpLog.Print(InteractionManager.Authority.Describe());
					if (args.Length >= 2)
						SmpLog.Warning("v15: 'smp authority <metre>' kaldirildi; "
							+ "mesafe dogrulamasi artik uygulanmiyor.");
					return;
				}

// ---------------- gövde testi ----------------
			// Bundle'daki 6 gövdeyi tek tek denemek için:
			//   smp body list     -> mevcut gövdeler (vertex sayısı)
			//   smp body <vertex> -> gövdeyi değiştir (menüdeki görünüm
			//                       sekmesinde de seçilebilir)
			if (sub == "body" || sub == "govde") {
				int[] vs = PlayerAssetNpc.BodyVertices;
				if (args.Length < 2) {
					SmpLog.Print("govdeler: " + vs.Length + " adet");
					for (int i = 0; i < vs.Length; i++)
						SmpLog.Print("  " + vs[i] + " vertex"
							+ (vs[i] == PlayerAssetNpc.LoadedBody ? "   <- yuklu" : ""));
					SmpLog.Print("smp body <vertex>");
					return;
				}
				int bc;
				if (!int.TryParse(args[1], out bc)) {
					if (args[1] == "auto") {
						PlayerSkin.SetBodyOverride(0);
						SmpLog.Print("govde: otomatik");
						return;
					}
					if (args[1] == "list") {
						for (int i = 0; i < vs.Length; i++)
							SmpLog.Print("  " + vs[i] + " vertex"
								+ (vs[i] == PlayerAssetNpc.LoadedBody ? "   <- yuklu" : ""));
						return;
					}
					SmpLog.Error("govde vertex sayisi olmali: " + args[1]);
					return;
				}
				if (PlayerSkin.SetBodyOverride(bc))
					SmpLog.Print("govde: " + bc + " vertex secildi."
						+ " Gormek icin: smp player cagir");
				else
					SmpLog.Error("govde yok: " + bc + " (smp body list)");
				return;
			}

			// ---------------- görünüm (skin) ----------------
			// Menüden seçilir ama konsoldan da denenebilmeli: 3B önizleme
			// yalnızca menüde çalışır, `smp skin <ad>` doğrudan uygular.
			if (sub == "skin" || sub == "gorunum") {
				if (args.Length >= 2) {
					string nm = args[1];
					if (nm == "list") {
						for (int i = 0; i < PlayerSkin.Count; i++) {
							SmpLog.Print("  " + (i + 1).ToString().PadLeft(3) + "  "
								+ NpcSkinCatalog.All[i].ToString()
								+ (i == PlayerSkin.Selected ? "   <- secili" : ""));
						}
						SmpLog.Print(PlayerSkin.Describe());
						return;
					}
					int num;
					if (int.TryParse(nm, out num)) {
						PlayerSkin.Select(num - 1);
					}
					else {
						if (!PlayerSkin.SelectByName(nm)) {
							SmpLog.Error("skin yok: " + nm + " (smp skin list)");
							return;
						}
					}
					SmpLog.Print(PlayerSkin.Describe());
					return;
				}
				SmpLog.Print(PlayerSkin.Describe());
				SmpLog.Print("smp skin list | smp skin <no|ad>   "
					+ "(menu: ana menudeki GORUNUM satiri, 3B onizlemeli)");
				return;
			}

			/// <summary>Haritadaki uzak oyuncu kuklasını bulur (animasyon testi için).</summary>
		static RemotePlayerBehaviour FindRemoteBody() {
			try {
				RemotePlayerBehaviour[] all
					= UnityEngine.Object.FindObjectsOfType(typeof(RemotePlayerBehaviour))
						as RemotePlayerBehaviour[];
				if (all != null && all.Length > 0) return all[0];
			}
			catch { }
			return null;
		}

		// ---------------- animasyon testi ----------------
			// Bundle'daki 26 klibi tek tek denemek için:
			//   smp anim list          -> klip adlari
			//   smp anim <ad>          -> klip oynat (Wave, Drink, CarIn...)
			//   smp anim stop          -> taban durusa don
			// Klipler katman 1'de tek seferlik; sure dolunca kukla
			// kendiliginden Idle/Walk'a doner.
			if (sub == "anim" || sub == "clip") {
				RemotePlayerBehaviour body = FindRemoteBody();
				if (body == null || body.Anim == null) {
					SmpLog.Error("uzak oyuncu yok. Haritaya gir ve test_player'i cagir.");
					return;
				}
				PlayerAnimManager mgr = body.Anim;
				if (args.Length < 2) {
					SmpLog.Print("klip: " + mgr.ListClips());
					if (mgr.CurrentOnce != null)
						SmpLog.Print("oynayan: " + mgr.CurrentOnce);
					SmpLog.Print("smp anim list | smp anim <ad> | smp anim all"
						+ " | smp anim next | smp anim stop");
					return;
				}
				string what = args[1];
				if (what == "list") {
					SmpLog.Print("klip: " + mgr.ListClips());
					return;
				}
				if (what == "stop") {
					mgr.StopTour();
					mgr.StopOnce();
					SmpLog.Print("animasyon durdu; taban durusa donuldu.");
					return;
				}
				// SIRALI TEST: "all" bastan baslar, "next" bir sonrakine
				// gecer. Her klip kendi suresi kadar gorunur, sonra
				// siradaki gelir. Eller serbest kalir.
				if (what == "all") {
					if (mgr.StartTour()) {
						SmpLog.Print("animasyon sirasi basladi "
							+ "(durdurmak icin: smp anim stop)");
					} else {
						SmpLog.Error("animasyon sirasi baslatilamadi.");
					}
					return;
				}
				if (what == "next") {
					if (!mgr.Touring && !mgr.StartTour()) {
						SmpLog.Error("klip yok.");
						return;
					}
					mgr.AdvanceTour();
					return;
				}
				if (mgr.PlayOnce(what))
					SmpLog.Print("anim: " + what + " oynatiliyor.");
				else
					SmpLog.Error("klip oynatilamadi: " + what + " (smp anim list)");
				return;
			}

			// ---------------- oyuncu ----------------
			if (sub == "player" || sub == "peer") {
				if (args.Length >= 2) {
					string what = args[1].ToLowerInvariant();
					if (what == "kill" || what == "clear") {
						RemotePlayerSync.Clear();
						SmpLog.Print("remote player body removed.");
						return;
					}
				}
				// Kullanılan modeli ÖNCE yaz: ikisi de yüklü olabilir, NPC
				// önceliklidir. 'player asset' satırı hangisinin gerçekten
				// spawn edildiğini söylemez, bu yüzden ayrı bir satır şart.
				SmpLog.Print(PlayerAssetNpc.Describe());
				SmpLog.Print(PlayerSkin.Describe());
				SmpLog.Print(PlayerAsset.Describe());
				SmpLog.Print(RemotePlayerSync.Describe());
				SmpLog.Print(PlayerMotion.Describe());
				SmpLog.Print(LocalPlayerState.Describe());
				// Olum / yeniden dogma: FSM bulundu mu, kaç kez oldu, Game Over
				// kaç kez bastirildi.
				SmpLog.Print(GodMode.Describe());
				SmpLog.Print(WaspBlocker.Describe());
				SmpLog.Print(HeldItemSync.Describe());
				// Kayıt aktarımı (JIP): sürüyor / bitti / neden başarısız.
				SmpLog.Print("save: " + SaveTransfer.Describe());
				return;
			}

			// ---------------- menü ----------------
			if (sub == "menu") {
				if (args.Length >= 2) {
					string what = args[1].ToLowerInvariant();
					if (what == "code") {
						if (args.Length < 3) {
							SmpLog.Print("lobby code: "
								+ (LobbyCode.Current.Length > 0 ? LobbyCode.Current : "(none yet)"));
							return;
						}
						LobbyCode.Generate();
						return;
					}
					if (what == "show") { MenuController.Ui.ShowMain(); return; }

					if (what == "scan") {
						// NEW GAME keşfini yeniden çalıştırıp tüm adayları dök.
						// Gerekçe: keşif sahnenin gerçek verisine bağlı; oyun
						// yapısı değiştiyse (ya da bir önceki sürüm yanlış FSM'i
						// seçtiyse) log'a tekrar bakmak yerine buradan tazelemek
						// çok daha hızlı. Oyun içinde, menüdeyken çalışır.
						MenuController.Rescan();
						return;
					}
				}
				SmpLog.Print("menu: " + (MenuController.InMenu ? "in the menu" : "in a world")
					+ ", ui=" + MenuController.Ui.Target
					+ ", " + MenuController.HiddenCount + " object(s) hidden, background "
					+ (MenuController.HasBackground ? "loaded" : "none"));
				return;
			}

			if (sub == "parts" || sub == "part") {
				SatsumaRegistry.DumpPartStates(args.Length >= 2 ? args[1] : null);
				return;
			}
			if (sub == "log") { RunLog(args); return; }

			// ---------------- durum ----------------
			Status();
		}

		/// <summary>Elle başlatılan süre ölçümleri (ad -&gt; başlangıç ms).</summary>
		static readonly Dictionary<string, long> LogSpanState
			= new Dictionary<string, long>(StringComparer.Ordinal);

		/// <summary>
		/// 'smp log ...' — log sisteminin tam komut yüzeyi.
		///
		///   open | close | toggle             ekran konsörü (F3)
		///   path                               dosyaların tam yolu (döndürme dahil)
		///   health                             log sisteminin kendi durumu
		///   levels                             kanal başına eşik ve kayıt sayısı
		///   level &lt;ch|global&gt; &lt;lvl&gt;       kanal eşiği (kaydedilir)
		///   levels set &lt;spec&gt;                "net=debug,ui=off" toplu
		///   filter [text]                      konsör filtre kutusuna odaklan
		///   find &lt;text&gt;                      konsörü filtrele
		///   errors                             yalnız warn/error göster
		///   channel &lt;name&gt;                   kanal süzgeci (boş = hepsi)
		///   stats                              sayaçlar ve süre ölçümleri
		///   dump [dosya]                       tüm tamponu ayrı dosyaya yaz
		///   dedupe [on|off|reset]              tekrar katlama
		///   remote [on|off|debug|clear]        karşı taraf log aynalaması
		///   mirror                             aynı + özet
		///   span &lt;isim&gt; [ms] / span end &lt;isim&gt;  süre ölçümü
		///   fsm &lt;on|off&gt;                      PlayMaker FSM loglaması
		///   clear                              konsör ve dosyayı sıfırla
		/// </summary>
		static void RunLog(string[] args) {
			LogConsole console = SteamMPBehaviour.Instance != null
				? SteamMPBehaviour.Instance.Console : null;

			string what = args.Length >= 2 ? args[1].ToLowerInvariant() : "toggle";
			string arg = args.Length >= 3 ? args[2] : null;

			// Ekran konsolu kaldirildi (LogConsole.ENABLED = false).
			// Acma/kapama komutlari sessizce hicbir sey yapmasin diye acikca
			// bildiriliyor. Bilgilendirici komutlar (health/stats/dump/...)
			// dosyaya yazmaya devam ediyor.
			if (!LogConsole.ENABLED
				&& (what == "open" || what == "close" || what == "toggle"
					|| what == "filter" || what == "find" || what == "errors"
					|| what == "channel" || what == "clear")) {
				SmpLog.Print("ekran konsolu kaldirildi; 'smp log path' "
					+ "dosya konumunu, 'smp log health|stats|dump|levels' "
					+ "raporlari verir.");
				return;
			}

			// path / health / levels / stats konsörü açık olmadan da çalışmalı:
			// konsörü açmadan ne yazıldığını sormak en çok ihtiyaç duyulan şey.
			if (what == "path") {
				SmpLog.Print("log files:");
				SmpLog.Print("  mod (this mod's own messages) : " + SmpLog.Path);
				string[] files = SmpLog.Files();
				for (int i = 0; i < files.Length; i++) {
					try {
						if (File.Exists(files[i])) {
							SmpLog.Print("      rotated: " + files[i] + "  ("
								+ (new FileInfo(files[i]).Length / 1024) + " KB)");
						}
					}
					catch { }
				}
				SmpLog.Print("  game + MSCLoader (Console.WriteLine) : "
					+ Path.Combine(GameLogCapture.Directory, GameLogCapture.CONSOLE_FILE));
				SmpLog.Print("  Unity + PlayMaker FSM (Debug.Log)    : "
					+ Path.Combine(GameLogCapture.Directory, GameLogCapture.UNITY_FILE));
				SmpLog.Print("  console buffer: " + SmpLog.LineCount + " line(s) of "
					+ LogEngine.Total + " this session, F3 opens it.");
				return;
			}

			if (what == "health") {
				SmpLog.Print(SmpLog.Health());
				string[] problems = LogEngine.Problems();
				for (int i = 0; i < problems.Length; i++) {
					SmpLog.ErrorChannel(LogChannels.Mod, problems[i]);
				}
				return;
			}

			if (what == "levels" || what == "level") { RunLogLevels(args, what, arg); return; }

			if (what == "stats") {
				SmpLog.Print(LogMetrics.Dump());
				SmpLog.Print(LogDedup.Describe());
				SmpLog.Print(LogRemote.Describe());
				SmpLog.Print(LogChannels.Describe());
				return;
			}

			if (what == "dedupe") {
				string v = (arg ?? "status").ToLowerInvariant();
				if (v == "on" || v == "off") {
					LogDedup.AlwaysFold = (v == "on");
					SmpLog.Print("dedupe: info/warn folding " + (LogDedup.AlwaysFold ? "ON" : "off")
						+ " (debug/trace always fold, errors never do).");
				}
				else if (v == "reset") {
					LogDedup.Reset();
				}
				SmpLog.Print(LogDedup.Describe());
				return;
			}

			if (what == "remote" || what == "mirror") {
				string v = (arg ?? "status").ToLowerInvariant();
				if (v == "on") LogRemote.Set(true, false);
				else if (v == "debug") LogRemote.Set(true, true);
				else if (v == "off") LogRemote.Set(false, false);
				else if (v == "clear") LogRemote.Clear();

				SmpLog.Print(LogRemote.Describe());
				if (what == "remote" && (v == "status" || arg == null)) {
					SmpLog.Print("  'smp log remote on' mirrors THIS side's log into the peer's file. "
						+ "One side is enough; the peer's lines then show up under the "
						+ LogChannels.Peer + " channel, so one file holds both sides.");
				}
				return;
			}

			if (what == "dump") {
				string written = LogEngine.Dump(arg);
				if (written.StartsWith("FAILED")) SmpLog.ErrorChannel(LogChannels.Mod, written);
				else SmpLog.Print("buffer dumped to " + written
					+ " - send that file instead of a screenshot.");
				return;
			}

			if (what == "span") { RunLogSpan(args, arg); return; }

			if (what == "fsm") {
				string v = arg != null ? arg.ToLowerInvariant() : "on";
				bool on = !(v == "off" || v == "0" || v == "false");
				bool now = GameLogCapture.SetFsmLogging(on);
				if (now) {
					SmpLog.Print("PlayMaker FSM logging ON. Every state change now goes to "
						+ GameLogCapture.UNITY_FILE + " - it grows fast, turn it off again.");
				}
				else {
					SmpLog.Print("PlayMaker FSM logging could not be toggled (API refused).");
				}
				return;
			}

			if (console == null) {
				SmpLog.Error("log console is not ready yet. 'smp log path', 'smp log health', "
					+ "'smp log levels' and 'smp log stats' work without it.");
				return;
			}

			if (what == "open" || what == "show") { console.Open(); return; }
			if (what == "close" || what == "hide") { console.Close(); return; }
			if (what == "toggle") { console.Toggle(); return; }

			if (what == "filter") {
				if (arg != null) console.SetFilterText(arg);
				console.FocusFilter();
				SmpLog.Print("filter field focused: type to search, ENTER applies, ESC clears.");
				return;
			}
			if (what == "find") {
				console.SetFilterText(arg ?? "");
				console.Open();
				SmpLog.Print(string.IsNullOrEmpty(arg) ? "filter cleared." : "console filtered by '" + arg + "'.");
				return;
			}
			if (what == "errors") {
				console.SetFilterErrors(true);
				console.Open();
				SmpLog.Print("console filtered to warnings and errors only.");
				return;
			}
			if (what == "channel") {
				console.SetFilterChannel(arg ?? "");
				console.Open();
				SmpLog.Print("channel filter: " + (string.IsNullOrEmpty(arg) ? "(all)" : arg));
				return;
			}
			if (what == "clear" || what == "reset") {
				SmpLog.Restart();
				SmpLog.Print("log cleared. New file: " + SmpLog.Path);
				return;
			}

			SmpLog.Error("usage: smp log [open|close|toggle|path|health|levels|level <ch> <lvl>|"
				+ "levels set <spec>|filter [text]|find <text>|errors|channel <name>|stats|dump [file]|"
				+ "dedupe [on|off|reset]|remote [on|off|debug|clear]|span <name> [ms]|fsm <on|off>|clear]");
		}

		/// <summary>
		/// 'smp log span &lt;isim&gt; [ms]' ve 'smp log span end &lt;isim&gt;'.
		///
		/// Elle ölçüm: konsoldan bir işe başlayıp bitirip gerçek süresini
		/// görmek. Ölçüm LogMetrics'e de yazılır, yani 'smp log stats' içinde
		/// birikir.
		/// </summary>
		static void RunLogSpan(string[] args, string arg) {
			System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
			if (arg != null && string.Equals(arg, "end", StringComparison.OrdinalIgnoreCase)) {
				if (args.Length < 4) { SmpLog.Error("usage: smp log span end <name>"); return; }
				string where = args[3];
				long started;
				if (!LogSpanState.TryGetValue(where, out started)) {
					SmpLog.ErrorChannel(LogChannels.Mod, "no span named '" + where + "' is running.");
					return;
				}
				double ms = LogEngine.Since() - started;
				LogSpanState.Remove(where);
				LogMetrics.Time("manual." + where, ms);
				SmpLog.WriteChannel(LogSeverity.Info, LogKind.Info, LogChannels.Mod, "span",
					where + " finished", new LogFields().AddMs("took", ms));
				return;
			}

			if (arg == null) {
				SmpLog.Print("usage: smp log span <name> [budgetMs]   then   smp log span end <name>");
				return;
			}
			float budget = 0f;
			if (args.Length >= 4) {
				try { budget = float.Parse(args[3], inv); } catch { budget = 0f; }
			}
			LogSpanState[arg] = LogEngine.Since();
			SmpLog.Print("span '" + arg + "' started, budget " + budget.ToString("F1", inv)
				+ " ms. Report it with 'smp log span end " + arg + "'.");
		}

		/// <summary>'smp log level ...' ve 'smp log levels [set &lt;spec&gt;]'</summary>
		static void RunLogLevels(string[] args, string what, string arg) {
			if (what == "levels") {
				if (arg != null && string.Equals(arg, "set", StringComparison.OrdinalIgnoreCase)) {
					string spec = args.Length >= 4 ? args[3] : "";
					if (!LogChannels.ApplySpec(spec)) {
						SmpLog.Error("usage: smp log levels set net=debug,ui=off,global=info");
						return;
					}
					LogChannels.Save();
					SmpLog.Print("levels applied and saved to " + LogChannels.CONFIG_NAME + ".");
				}
				else if (arg != null && string.Equals(arg, "clear", StringComparison.OrdinalIgnoreCase)) {
					LogChannels.ApplySpec("*=" + LogSeverities.Name(LogChannels.Global));
					LogChannels.Save();
					SmpLog.Print("every channel reset to the global level ("
						+ LogSeverities.Name(LogChannels.Global) + ").");
				}
				SmpLog.Print(LogChannels.Describe());
				return;
			}

			if (arg == null) {
				SmpLog.Print("usage: smp log level <channel|global> <off|fatal|error|warn|info|debug|trace>");
				return;
			}
			if (args.Length < 4) {
				SmpLog.Print("level of '" + arg + "' is " + LogSeverities.Name(LogChannels.LevelOf(arg)) + ".");
				return;
			}
			string value = args[3].ToLowerInvariant();
			if (string.Equals(arg, "global", StringComparison.OrdinalIgnoreCase) || arg == "*") {
				LogChannels.SetGlobal(LogSeverities.Parse(value, LogChannels.Global), true);
				SmpLog.Print("global level = " + LogSeverities.Name(LogChannels.Global)
					+ " (saved; channels without their own level follow it).");
				return;
			}
			LogChannels.SetLevel(arg, LogSeverities.Parse(value, LogChannels.Global), true);
			SmpLog.Print("channel '" + arg + "' level = " + LogSeverities.Name(LogChannels.LevelOf(arg))
				+ " (saved to " + LogChannels.CONFIG_NAME + ").");
		}

		/// <summary>
		/// Durum özeti.
		///
		/// Satır listesini bilerek çıkarmadık: eskiden burada onlarca satır
		/// basılıyordu ve konsolu dolduruyordu. Şimdi hepsi " tuşundaki konsörde,
		/// kaydırılabilir biçimde duruyor. Burada yalnızca "bir sonraki adım
		/// ne?" sorusu cevaplanıyor.
		/// </summary>
		void Status() {
			SmpLog.Print("steam: " + (SteamTransport.SteamReady ? "ready" : "NOT READY")
				+ " | harmony patches: " + ModEntry.PatchCount + "/" + ModEntry.ExpectedPatches
				+ " | echo guard depth: " + EchoGuard.Depth
				+ " | mode: " + SteamTransport.CurrentMode
				+ (SteamTransport.IsOnline ? " lobby " + SteamTransport.LobbyId.m_SteamID
					+ (SteamTransport.HasRemote ? " peer " + SteamTransport.RemoteId.m_SteamID
						+ " ping " + SteamTransport.PingMs + "ms" : " (alone)") : ""));
			SmpLog.Print(Telemetry.Summary());

			// Uzak oyuncu: asset yuklendi mi, govde var mi. Ikinci satir en
			// tanisi: "arkadasim gorunmuyor" dediginde buraya bakilir.
			SmpLog.Print(RemotePlayerSync.Describe());

			if (SatsumaRegistry.PartCount == 0) {
				SmpLog.Print("satsuma: no table yet (load a save, then 'smp rescan').");
			}
			else {
				SmpLog.Print("satsuma: " + SatsumaRegistry.PartCount + " entries, "
					+ SatsumaRegistry.BoltCount + " bolts, "
					+ SatsumaRegistry.InstalledCount() + " installed, hash 0x"
					+ SatsumaRegistry.TableHash.ToString("x8")
					+ " | pending " + SatsumaRegistry.PendingParts + " part(s), "
					+ SatsumaRegistry.DirtyBolts + " bolt(s), "
					+ SatsumaRegistry.PendingReconcile + " bolt(s) walking to the peer's stage");
				// Tıklışkanlık teşhisi: "kanca gerçekten tetikleniyor mu?"
				// Canlı log (proto 5) bunu "probe: bolt-seen 0" ile yanlış
				// ölçüyordu; buradaki sayac doğrudan olaydan gelir.
				SmpLog.Print("bolt input: " + BoltSync.ScrewEvents + " TIGHTEN/UNTIGHTEN event, "
					+ BoltSync.ScrewBursts + " burst (last "
					+ BoltSync.LastBurstEvents + " event(s)); quiet period "
					+ (BoltSync.BOLT_QUIET_PERIOD * 1000f).ToString("F0") + " ms");
				SmpLog.Print(Telemetry.SatsumaSummary() + " | last: " + Telemetry.LastReceived);
				SmpLog.Print(Telemetry.ApplyFunnel());
				SmpLog.Print(WorldObjectSync.Describe());
			}

			SmpLog.Print("sync state: " + SatsumaRegistry.SyncLabel
				+ " (no-save=save not loaded, no-peer=not connected, "
				+ "requested=asked the host, sending=host is pushing, ok=state exchanged, "
				+ "awaiting-host=waiting for the host)");

			// Etkilesim katmani: tasima / parca takma / vida / sahiplik sayaclari.
			// Bunlar sifir kalirsa olay kancasina baglanma ya da mesaj yonlendirmesi
			// calismiyor demektir; send/recv sayaclari hangi yonun bozuk oldugunu gosterir.
			SmpLog.Print(InteractionManager.Describe());

			// Yeni moduller: ihtiyac / sohbet / AI arac / eşya / ping sayaclari.
			// Hepsi sifir kalirsa mesaj yonlendirmesi ya da modul baglantisi
			// eksik demektir; hangi yonun bozuk oldugunu sayac gosterir.
			SmpLog.Print(Telemetry.ExtendedSummary());
			SmpLog.Print(ModIntegration.Describe() + " | " + AIVehicleSync.Describe()
				+ " | " + HostMigration.Describe());

			SmpLog.Print("log: " + LogFile.Describe() + "  (\" opens the console, F4 filters it)");
			string[] logProblems = LogEngine.Problems();
			if (logProblems.Length > 0) {
				for (int i = 0; i < logProblems.Length; i++) {
					SmpLog.ErrorChannel(LogChannels.Mod, "log: " + logProblems[i]);
				}
			}

			PrintConnectionHelp();

			if (VehicleRegistry.Vehicles.Count == 0) {
				SmpLog.Print("no vehicles yet (load your save first).");
				return;
			}
			foreach (KeyValuePair<int, SyncedVehicle> kv in VehicleRegistry.Vehicles) {
				SyncedVehicle sv = kv.Value;
				if (sv == null) SmpLog.Print("  " + kv.Key + ": <destroyed>");
				else SmpLog.Print("  " + kv.Key + " '" + sv.VehicleName + "'"
					+ (sv.LocalIsDriver ? " [YOU DRIVE]" : "")
					+ (sv.RemoteDriven ? " [FRIEND DRIVES]" : ""));
			}
		}

		void PrintHelp() {
			SmpLog.Print(Help);
			SmpLog.Print("sub-commands: " + string.Join(", ", SUBCOMMANDS));
		}

		static void PrintConnectionHelp() {
			SmpLog.Print("next step:");
			if (!SteamTransport.SteamReady) {
				SmpLog.Print("  Steam is NOT ready. Start Steam, then reload the game.");
				return;
			}
			if (SteamTransport.IsHost && !SteamTransport.HasRemote) {
				SmpLog.Print("  You are hosting. Invite a friend with Shift+Tab, or let them run 'smp join' on their side.");
				SmpLog.Print("  lobby id: " + SteamTransport.LobbyId.m_SteamID);
				return;
			}
			if (!SteamTransport.IsOnline) {
				SmpLog.Print("  Run 'smp host' to create a lobby, or 'smp join' to find and join a friend's.");
			}
		}

		static void CopyToClipboard(string text) {
			try {
				TextEditor editor = new TextEditor();
				editor.content = new GUIContent(text);
				editor.SelectAll();
				editor.Copy();
			}
			catch { }
		}
	}
}
