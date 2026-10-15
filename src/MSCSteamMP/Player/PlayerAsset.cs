using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// KARAKTER MODELİNİ YÜKLER.
	///
	/// Neden bundle
	/// -----------
	/// FBX runtime'da yüklenemez; Unity yalnızca derlenmiş asset bundle okur.
	/// Oyunun kendi Unity'si 5.0.0f4 (mysummercar_Data\Managed\UnityEngine.dll), yani
	/// aynı sürümle derlenmiş bir bundle sorunsuz okunur.
	///
	/// Neden DLL'in İÇİNDE
	/// -------------------
	/// README'nin "no asset bundle" kuralı: kurulum klasöründe dosya yok, tek DLL.
	/// Bundle /resource ile gömülür, çalışma anında belleğten açılır. Deploy.cmd
	/// ve MSCLoader'ın mod klasörü düzeni değişmez.
	///
	/// Unity 5.0 API TUZAKI
	/// --------------------
	/// Unity 5.0'da AssetBundle.LoadFromMemory YOK. Doğrusu iki isim var:
	///
	///     AssetBundle.CreateFromMemory(byte[])            -> AssetBundleCreateRequest
	///     AssetBundle.CreateFromMemoryImmediate(byte[])   -> AssetBundle
	///
	/// İlkine bakma: ASENKRON bir istek döner, yani bir AssetBundle DEĞİL. Senkron
	/// açılımın adı Immediate. (Oyunun kendi UnityEngine.dll'i üzerinden
	/// yansımayla doğrulandı; MSCMP CreateFromFile kullanıyordu, o yüzden aynı
	/// tuzağa düşmemiş.)
	///
	/// mscorlib 2.0
	/// -------------
	/// Mono 2.5 profili çalışıyor, bu yüzden Stream.CopyTo (.NET 4.0) ve
	/// HashSet<T> (System.Core 3.0) YOK. Bayt kopyası elle yazıldı.
	///
	/// Animasyonlar
	/// ------------
	/// Clip'ler ayrıca yüklenmiyor: prefab Instantiate edildiğinde Animation
	/// component'i clip'leriyle birlikte gelir ve isimle indekslenir
	/// (animation["Walk"]). MSCMP'de çalışan yol da bu.
	/// </summary>
	public static class PlayerAsset {

		/// <summary>Gömülü kaynak adı (build.cmd'daki /resource ile eşleşmeli).</summary>
		public const string RESOURCE = "SteamMP.player.bundle";

		/// <summary>
		/// Bundle içindeki model yolu. BÜYÜK/KÜÇÜK HARF BİREBİR OLMALI:
		/// bundle container anahtarı "assets/mpplayermodel/mpplayermodel.fbx"
		/// (UnityPy ile bundle'dan okundu). AssetBundle.LoadAsset eşleşmesi
		/// büyük/küçük harfe duyarlıdır; yanlış yazımda prefab null döner ve
		/// uzak oyuncu gövdesi hiç doğmaz.
		/// </summary>
		public const string MODEL_PATH = "assets/mpplayermodel/mpplayermodel.fbx";

		static bool attempted;
		static AssetBundle bundle;
		static GameObject prefab;
		static string problem = "";
		static int resourceBytes;

		/// <summary>Model hazır mı? İlk çağrıda yüklemeyi dener.</summary>
		public static bool Ready { get { EnsureLoaded(); return prefab != null; } }

		/// <summary>Yüklenemediyse neden. 'smp player' ve 'smp status' gösterir.</summary>
		public static string Problem { get { EnsureLoaded(); return problem; } }

		/// <summary>Model nesnesi. Spawn için kullanılır; çağırmadan önce Ready kontrol edin.</summary>
		public static GameObject Prefab { get { EnsureLoaded(); return prefab; } }

		/// <summary>Gömülü bundle'ın boyutu (bayt). 'smp player' bunu basar.</summary>
		public static int ResourceBytes { get { EnsureLoaded(); return resourceBytes; } }

		/// <summary>Tek satır durum.</summary>
		public static string Describe() {
			EnsureLoaded();
			if (prefab == null) return "player asset: NOT LOADED (" + problem + ")";
			return "player asset: " + MODEL_PATH + " (" + resourceBytes
				+ " byte bundle, " + prefab.transform.childCount + " child) OK";
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Yüklemeyi bir kez dener.
		///
		/// "attempted" bayrağı: başarısızlık da kalıcıdır. Yeniden denemek her karede
		/// 2 MB'lık bir bayt dizisini kopyalamak demek; ayrıca nedeni değişmediği için
		/// yeni bir bilgi üretmez.
		/// </summary>
		static void EnsureLoaded() {
			if (attempted) return;
			attempted = true;

			try {
				Assembly self = typeof(PlayerAsset).Assembly;
				using (Stream s = self.GetManifestResourceStream(RESOURCE)) {
					if (s == null) {
						problem = "embedded resource '" + RESOURCE + "' not found in "
							+ self.GetName().Name + " (was build.cmd's /resource line run?)";
						SmpLog.Error(problem);
						return;
					}

					// mscorlib 2.0: Stream.CopyTo yok, elle kopyala.
					resourceBytes = (int)s.Length;
					byte[] data = new byte[resourceBytes];
					int offset = 0;
					while (offset < data.Length) {
						int read = s.Read(data, offset, data.Length - offset);
						if (read <= 0) break;
						offset += read;
					}
					if (offset != data.Length) {
						problem = "short read: " + offset + " of " + data.Length + " bytes";
						SmpLog.Error(problem);
						return;
					}

					// Unity 5.0: Immediate senkron acilimi doner. CreateFromMemory
					// ASENKRON bir istek doner (AssetBundle degil).
					bundle = AssetBundle.CreateFromMemoryImmediate(data);
				}

				if (bundle == null) {
					problem = "AssetBundle.CreateFromMemory returned null";
					SmpLog.Error(problem);
					return;
				}

				prefab = bundle.LoadAsset<GameObject>(MODEL_PATH);
				if (prefab == null) {
					problem = "no GameObject at '" + MODEL_PATH + "' in the bundle";
					SmpLog.Error(problem);
					return;
				}

				problem = "";
				SmpLog.Print("player model loaded from the embedded bundle: "
					+ MODEL_PATH + " (" + resourceBytes + " byte).");
			}
			catch (Exception e) {
				bundle = null;
				prefab = null;
				problem = e.GetType().Name + ": " + e.Message;
				SmpLog.Error("player asset load failed: " + problem);
			}
		}
	}
}