using System;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// F3 konsolu icin basit komut ayristirici (Command Parser).
	///
	/// Artık sadece 'smp' komutlarını işler. NPC komutları tamamen kaldırıldı.
	///
	/// Unity 5 / .NET 3.5 uyumlu: interpolasyon yok, LINQ yok, null-conditional yok.
	/// Tum hatalar yakalanir, hicbir komut oyunu dusuremez (crash-free).
	/// </summary>
	public static class SmpCommandParser {

		/// <summary>
		/// Ham konsol satirini calistirir.
		///
		/// DONUS DEGERI: kullaniciya gosterilecek tek satirlik ozet.
		/// </summary>
		public static string Execute(string line) {
			if (string.IsNullOrEmpty(line)) return null;
			string text = line.Trim();
			if (text.Length == 0) return null;
			if (text[0] == '/') text = text.Substring(1).TrimStart();
			if (text.Length == 0) return null;

			string cmd;
			string[] args;
			Split(text, out cmd, out args);

			try {
				if (string.Equals(cmd, "help", StringComparison.OrdinalIgnoreCase)
					|| cmd == "?") {
					SmpLog.Print("commands: smp, help");
					ModCommands.Run("help");
					return "help shown (see the lines above)";
				}
				// Geri kalani MSCLoader'a: smp, help dahil.
				if (!ModCommands.Available) {
					return "commands are not available yet: " + ModCommands.Unavailable;
				}
				ModCommands.Run(text);
				return "'" + cmd + "' sent to MSCLoader.";
			}
			catch (Exception e) {
				string msg = "command '" + text + "' failed: "
					+ e.GetType().Name + ": " + e.Message;
				SmpLog.Error(msg);
				return msg;
			}
		}

		static void Split(string text, out string cmd, out string[] args) {
			// Tirnakli arguman destegi (MSCLoader ParseArguments ile ayni kural):
			// cift tirnak arasindaki bosluk bolunmez.
			System.Collections.Generic.List<string> parts =
				new System.Collections.Generic.List<string>(8);
			System.Text.StringBuilder cur = new System.Text.StringBuilder(64);
			bool inQuote = false;
			for (int i = 0; i < text.Length; i++) {
				char c = text[i];
				if (c == '"') { inQuote = !inQuote; continue; }
				if (!inQuote && (c == ' ' || c == '\t')) {
					if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Length = 0; }
					continue;
				}
				cur.Append(c);
			}
			if (cur.Length > 0) parts.Add(cur.ToString());

			if (parts.Count == 0) { cmd = ""; args = new string[0]; return; }
			cmd = parts[0];
			args = new string[Math.Max(0, parts.Count - 1)];
			for (int i = 0; i < args.Length; i++) args[i] = parts[i + 1];
		}
	}
}