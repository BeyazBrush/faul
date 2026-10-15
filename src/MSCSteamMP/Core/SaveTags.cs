using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SteamMP {

	/// <summary>
	/// KAYIT DOSYASI TAG'LERİNİ DÖKER.
	///
	/// DNSPY BULGUSU (bu modülün yazılma sebebi)
	/// -------------------------------------------
	/// Tüm derlemeler taranmıştı: "Bolt", "Tight", "Screw", "Hand_Assemble"
	/// dize hiçbir oyun assembly'sinde YOK. C# tarafında cıvata sistemi
	/// yoktur; hepsi PlayMaker'dır. Tek istisna:
	///
	///   HutongGames.PlayMaker.Actions.SaveFloat
	///     ES2.Save<float>(saveValue, saveFile + "?tag=" + uniqueTag)
	///
	/// Yani oyun her kayıt değerini bir ES2 dosyasına "tag" (string anahtar)
	/// ile yazıyor. Cıvata kademelerinin GERÇEK adları bu tag'lerdir.
	///
	/// ES2File.GetTags(ES2Settings) tüm anahtarları döndürür
	/// (token 0x0600019C), yani çalışma anında okunabilir. Yeni assembly
	/// referansı EKLEMEYİZ: ES2 tipi yansımayla bulunur, böylece mod yine
	/// tek dosya ve referanssız kalır.
	///
	/// KULLANIM
	/// --------
	///   smp savetags            → bolt/tight/stage içeren tag'ler
	///   smp savetags engine     → 'engine' içerenler
	///   smp savetags --all      → hepsi (ilk 300)
	/// </summary>
	public static class SaveTags {

		/// <summary>Tag adında aranan kelimeler (varsayılan filtre).</summary>
		static readonly string[] BOLT_WORDS = { "bolt", "tight", "stage", "screw", "loose" };

		/// <summary>Denecek kayıt dosyası adları. Oyun sürümüne göre değişebilir.</summary>
		static readonly string[] CANDIDATE_FILES = {
			"savegame.save", "MSCEssentials.save", "MSCEssentials.dat",
			"save.dat", "savegame.es2", "mysummercar.save",
		};

		/// <summary>'smp savetags [filtre|--all]' komutu.</summary>
		public static void Run(string filter, bool all) {
			Type settingsType = null, fileType = null;
			Assembly es2 = FindEs2();
			if (es2 == null) {
				SmpLog.Error("ES2 assembly not loaded; the game has not saved yet.");
				return;
			}
			settingsType = es2.GetType("ES2Settings");
			fileType = es2.GetType("ES2File");
			if (settingsType == null || fileType == null) {
				SmpLog.Error("ES2Settings/ES2File type not found in " + es2.GetName().Name);
				return;
			}
			MethodInfo getTags = fileType.GetMethod("GetTags",
				new Type[] { settingsType });
			if (getTags == null) {
				SmpLog.Error("ES2File.GetTags(ES2Settings) not found (token 0x0600019C).");
				return;
			}

			string[] files = CandidateFiles();
			int totalTags = 0, shown = 0;
			for (int f = 0; f < files.Length; f++) {
				string[] tags = null;
				try {
					object settings = Activator.CreateInstance(settingsType,
						new object[] { files[f] });
					tags = getTags.Invoke(null, new object[] { settings }) as string[];
				}
				catch {
					// Dosya yok ya da okunamadı: sıradaki adayı dene.
				}
				if (tags == null || tags.Length == 0) continue;

				totalTags += tags.Length;
				List<string> hits = new List<string>();
				for (int t = 0; t < tags.Length; t++) {
					string tag = tags[t];
					if (tag == null) continue;
					if (all || Matches(tag, filter)) hits.Add(tag);
				}
				if (hits.Count == 0) {
					SmpLog.Print(files[f] + ": " + tags.Length + " tag(s), no match.");
					continue;
				}
				SmpLog.Print(files[f] + ": " + tags.Length + " tag(s), "
					+ hits.Count + " match, " + Math.Min(hits.Count, 300) + " shown:");
				for (int i = 0; i < hits.Count && shown < 300; i++) {
					SmpLog.Print("  " + hits[i]);
					shown++;
				}
			}
			if (totalTags == 0) {
				SmpLog.Print("save tags: no readable ES2 file. Tried:");
				for (int i = 0; i < files.Length; i++) SmpLog.Print("  " + files[i]);
				SmpLog.Print("Run this AFTER saving the game once (Continue -> save).");
			}
			else {
				SmpLog.Print("save tags: " + totalTags + " tag(s) total, " + shown + " shown.");
			}
		}

		static bool Matches(string tag, string filter) {
			if (!string.IsNullOrEmpty(filter)) {
				return tag.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
			}
			for (int i = 0; i < BOLT_WORDS.Length; i++) {
				if (tag.IndexOf(BOLT_WORDS[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			return false;
		}

		static Assembly FindEs2() {
			Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
			for (int i = 0; i < all.Length; i++) {
				if (all[i] == null) continue;
				string n = all[i].GetName().Name;
				if (n != null && n.IndexOf("ES2", StringComparison.OrdinalIgnoreCase) >= 0) return all[i];
			}
			return null;
		}

		/// <summary>Oyun klasöründeki kayıt dosyalarını isimle dener.</summary>
		static string[] CandidateFiles() {
			List<string> list = new List<string>(CANDIDATE_FILES);
			// Oyun klasörünü oyunun kendi veri yolundan al; yanlış yol
			// sessizce boş dönmesin diye ES2 default çözümlemesi de denenir.
			try {
				string dir = null;
				string dataPath = UnityEngine.Application.dataPath;   // ".../My Summer Car/My Summer Car_Data"
				if (!string.IsNullOrEmpty(dataPath)) {
					int i = dataPath.LastIndexOf('\\');
					if (i > 0) {
						dir = dataPath.Substring(0, i);
						list.Add(System.IO.Path.Combine(dir, "savegame.save"));
						list.Add(System.IO.Path.Combine(dir, "MSCEssentials.save"));
					}
				}
				if (dir != null && Directory.Exists(dir)) {
					string[] found = Directory.GetFiles(dir, "*.save");
					for (int i = 0; i < found.Length && i < 20; i++) {
						if (!list.Contains(found[i])) list.Add(found[i]);
					}
				}
			}
			catch { }
			return list.ToArray();
		}
	}
}