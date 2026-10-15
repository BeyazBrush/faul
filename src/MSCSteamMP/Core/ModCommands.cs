using System;
using System.Collections.Generic;
using System.Reflection;
using MSCLoader;

namespace SteamMP {

	/// <summary>
	/// F3 konsöründen komut çalıştırma köprüsü.
	///
	/// Sorun neydi
	/// ------------
	/// Komutlar MSCLoader'ın kendi konsolundan (BackQuote) yazılıyordu. O ekranı
	/// kullanılmaz hâle getirdiğimiz için ortada komut YAZILACAK bir yer kalmadı
	/// ve "/npc çalışmıyor" belirtisi doğdu. Artık konsörün altında kendi giriş
	/// satırı var.
	///
	/// Köprü: iki katman da erişilemez
	/// --------------------------------
	/// dnSpy ile MSCLoader.dll açıldı, bulunanlar:
	///
	///   ConsoleCommand.cc                 internal static ConsoleController
	///   type ConsoleController            Flags: Public = FALSE   (internal sınıf)
	///   ConsoleController.RunCommandString(string)              public metot
	///   ConsoleController.commands        internal Dictionary&lt;string, CommandRegistration&gt;
	///
	/// Alan da, tip de internal. Yani derleme zamanında ConsoleController'a hiç
	/// dokunamayız; çalıştırma ve komut listesi de yansımadan geçiyor. Erişim
	/// bilgileri (alan, metot, sözlük) bir kez bulunup önbelleğe alınıyor, her
	/// komutta yeniden aranmıyor.
	///
	/// Neden kendi ayrıştırıcımız yok
	/// ----------------------------
	/// MSCLoader'ınki tırnaklı argüman ayrıştırmasını ve komut kaydını zaten
	/// doğru yapıyor. Kendi ayrıştırıcı yazmak ikinci bir doğruluk kaynağı
	/// olurdu: /smp ve /npc çalışırken MSCLoader'ın başka komutları çalışmaz ya
	/// da tersi. Tek çalıştırıcı, tek doğruluk.
	///
	/// Alan bulundu ama BOŞ olabilir
	/// ------------------------------
	/// MSCLoader yürütücüyü menü yüklenirken atıyor. Değeri önbelleğe ALMIYORUZ
	/// (sadece alanın kendisi), her çağrıda yeniden okuyoruz; yoksa mod menüden
	/// önce yüklendiğinde yürütücüye bir daha asla ulaşamazdık.
	/// </summary>
	public static class ModCommands {

		/// <summary>Komut yürütücüsü hazır mı? (menü henüz yüklenmediyse false)</summary>
		public static bool Available { get { return Resolve() != null; } }

		/// <summary>Hazır değilse neden? Kullanıcıya göstermek için.</summary>
		public static string Unavailable { get { Resolve(); return unavailable; } }

		static FieldInfo controllerField;   // ConsoleCommand.cc
		static MethodInfo runMethod;         // ConsoleController.RunCommandString
		static FieldInfo commandsField;      // ConsoleController.commands
		static bool probed;
		static string unavailable = "";

		/// <summary>
		/// Komutları MSCLoader'ın listesine kaydeder.
		///
		/// Bu ŞARTT. dnSpy'de MSCLoader'ın mod komutlarını otomatik keşfeden bir
		/// tarama görünmüyor; ConsoleCommand.Add(cmd) yalnızca verilen örneği
		/// kaydediyor. Kaydetmezsek 'smp' hiç var olmuyor.
		/// </summary>
		public static void RegisterAll() {
			try {
				ConsoleCommand.Add(new SmpCommand());
				ConsoleCommand.Add(new NpcCommand());
				SmpLog.Print("console commands registered: smp, npc");
			}
			catch (Exception e) {
				// Kayıt başarısız olursa komutlar çalışmaz; ama modun geri kalanı
				// çalışmaya devam etmeli, o yüzden yalnızca kayda geçiyoruz.
				SmpLog.Warning("console command registration failed: " + e.Message
					+ " (the mod still works; commands will not).");
			}
		}

		/// <summary>
		/// Bir komut satırı çalıştırır. Baştaki eğik çizgi isteğe bağlı, yani
		/// hem "npc Ahmet" hem "/npc Ahmet" çalışır.
		/// </summary>
		public static void Run(string line) {
			if (string.IsNullOrEmpty(line)) return;
			string text = line.Trim();
			if (text.Length == 0) return;
			if (text[0] == '/') text = text.Substring(1).TrimStart();

			object controller = Resolve();
			if (controller == null) {
				SmpLog.Error("commands are not available yet: " + unavailable);
				return;
			}

			try {
				// RunCommandString ayrıştırır, komutu bulur ve çalıştırır, ayrıca
				// geçmişe ekler. Yansıma TargetInvocationException sarıyor, bu
				// yüzden iç istisna okunuyor: aksi hâlde kullanıcı "command threw
				// Exception" görür ve asıl sebebi göremez.
				runMethod.Invoke(controller, new object[] { text });
			}
			catch (Exception e) {
				Exception real = e.InnerException ?? e;
				SmpLog.Error("command '" + text + "' threw: " + real.GetType().Name
					+ ": " + real.Message);
			}
		}

		/// <summary>
		/// Komut adları, sekme tamamlama için. MSCLoader'ın kendi komutları dahil.
		/// </summary>
		public static string[] KnownCommands() {
			object controller = Resolve();
			if (controller == null) return Empty;
			if (commandsField == null) return Empty;

			try {
				object commands = commandsField.GetValue(controller);
				if (commands == null) return Empty;

				// commands bir Dictionary<string, CommandRegistration>. IDictionary
				// üzerinden okumak en az kırılgan yol: iç değer tipini bilmemiz
				// gerekmiyor, sadece anahtarlar.
				System.Collections.IDictionary dict = commands as System.Collections.IDictionary;
				if (dict == null) return Empty;

				List<string> list = new List<string>(dict.Count);
				foreach (object key in dict.Keys) {
					if (key != null) list.Add(key.ToString());
				}
				list.Sort(StringComparer.Ordinal);
				return list.Count == 0 ? Empty : list.ToArray();
			}
			catch (Exception) {
				return Empty;
			}
		}

		static readonly string[] Empty = new string[0];

		/// <summary>
		/// Yürütücüyü çözer: alanı okur, değeri null ise null döner.
		/// </summary>
		static object Resolve() {
			if (!probed) Probe();
			if (controllerField == null || runMethod == null) return null;

			object value = controllerField.GetValue(null);
			if (value == null) unavailable = "the menu has not finished loading yet";
			return value;
		}

		/// <summary>Erişim bilgilerini bir kez bulur ve saklar.</summary>
		static void Probe() {
			probed = true;
			try {
				// Alan adı ileride değişebileceği için İSİMLE değil TÜRE göre
				// arıyoruz: tek bir internal statik alan var ve tipinin adı
				// "ConsoleController" ile bitiyor.
				FieldInfo cc = null;
				foreach (FieldInfo f in typeof(ConsoleCommand).GetFields(
						BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)) {
					if (f.FieldType == null) continue;
					if (!f.FieldType.Name.EndsWith("ConsoleController", StringComparison.Ordinal)) continue;
					cc = f;
					break;
				}

				if (cc == null) {
					unavailable = "MSCLoader's command controller field was not found";
					return;
				}
				controllerField = cc;

				// Tip internal, bu yüzden adını kaynak kodda yazamayız; alanın
				// FieldType'ı üzerinden çalışma zamanında arıyoruz.
				Type controllerType = cc.FieldType;

				runMethod = controllerType.GetMethod("RunCommandString",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
					null, new Type[] { typeof(string) }, null);

				if (runMethod == null) {
					unavailable = "RunCommandString was not found on " + controllerType.Name;
					return;
				}

				commandsField = controllerType.GetField("commands",
					BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
			}
			catch (Exception e) {
				unavailable = e.GetType().Name + ": " + e.Message;
				controllerField = null;
				runMethod = null;
				commandsField = null;
			}
		}
	}
}