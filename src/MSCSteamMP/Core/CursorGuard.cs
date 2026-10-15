using Harmony;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// OYUNUN İMLEÇ İSTEĞİNİ GERÇEKTEN UYGULAR.
	///
	/// BELİRTİ (kullanıcı, tekrarlayan): "esc bastığımda imleç kayboluyor".
	///
	/// dnSpy KANITI — NEDEN OLUYOR
	/// ----------------------------
	/// HutongGames.PlayMaker.Actions.SetMouseCursor
	///   token 0x02000334, OnEnter token 0x06000DC9:
	///
	///     public override void OnEnter() {
	///         PlayMakerGUI.LockCursor = this.lockCursor.Value;
	///         PlayMakerGUI.HideCursor = this.hideCursor.Value;
	///         PlayMakerGUI.MouseCursor = this.cursorTexture.Value;
	///         base.Finish();
	///     }
	///
	/// Dikkat: action Cursor.visible / Cursor.lockState'ı HİÇ YAZMIYOR.
	/// Sadece PlayMakerGUI'nin üç STATİK alanına değer bırakıyor.
	/// dnSpy ile doğrulandı: PlayMakerGUI.HideCursor ve LockCursor
	/// (token 0x06000427 / 0x06000428) düz auto-property, yani yine sadece
	/// alan. Bu build'de onları gerçek Cursor'a taşıyan bir yol yok, dolayısıyla
	/// ESC menüsü açıldığında:
	///
	///   * oyun "imleci göster, kilitleme" diyor (hideCursor=false,
	///     lockCursor=false)
	///   * ama Cursor.visible'a kimse dokunmuyor → oyun içinden gizlenmiş
	///     imleç gizli kalıyor ("görünmez ama serbest" durum)
	///
	/// İKİNCİ KATMAN: SimpleSmoothMouseLook.Update her karede lockCursor
	/// alanını geri yazıyor. Yani sadece Cursor.visible'ı açmak yetmez,
	/// bir sonraki karede kamera scripti imleci tekrar kilitliyor. O yüzden
	/// "imleci aç" dediğimizde kamera bakışını da donduruyoruz.
	///
	/// ÇÖZÜM
	/// ------
	/// 1. Postfix: action'ın istediği durumu gerçek Cursor'a yaz.
	/// 2. "imleci göster" isteği gelince CameraLookGuard.MenuOpen = true,
	///    böylece oyunun kamera scripti bir sonraki karede geri kilitlemez.
	/// 3. Kısa bir süre (REASSERT_SECONDS) boyunca her karede tekrar
	///    uygularız: menü açılışı birkaç kare sürüyor ve arada başka bir
	///    script (GUI vb.) imleci tekrar gizleyebiliyor.
	///
	/// Unity 5 / .NET 3.5 uyumlu. Hiçbir istisna oyun döngüsüne sızmaz.
	/// </summary>
	public static class CursorGuard {

		/// <summary>
		/// "İmleci göster" isteğinden sonra kaç saniye boyunca durum
		/// her karede yeniden uygulanır. Menü açılışı birkaç kare sürer.
		/// </summary>
		const float REASSERT_SECONDS = 1.5f;

		/// <summary>Son "imleci göster" isteğinin zamanı.</summary>
		static float visibleUntil;
		/// <summary>Son "imleci gizle" isteğinin zamanı.</summary>
		static float hiddenUntil;
		/// <summary>Son uygulanan durum (log spam'i önler).</summary>
		static bool lastHide = true;
		static bool lastLock = true;
		static bool loggedOnce;

		/// <summary>Son "imleci göster" isteğinin zamanı.</summary>
		static float lastVisibleAt;
		/// <summary>Menü kapanınca kamerayı bırakma süresi.</summary>
		const float CAMERA_RELEASE_SECONDS = 1.5f;

		/// <summary>
		/// Oyun bir imleç durumu istedi. PlayMakerGUI alanlarına ek olarak
		/// GERÇEK Cursor'a da yazıyoruz.
		/// </summary>
		/// <param name="hide">true = imleç gizlensin.</param>
		/// <param name="lockIt">true = imleç kilitlensin (sürüş kamerası).</param>
		public static void Apply(bool hide, bool lockIt) {
			float now;
			try { now = Time.unscaledTime; } catch { now = 0f; }

			if (hide) hiddenUntil = now + REASSERT_SECONDS;
			else { visibleUntil = now + REASSERT_SECONDS; lastVisibleAt = now; }

			// "İmleci göster ve serbest bırak" = menü durumu. Kamera
			// scriptini dondur ki bir sonraki karede geri kilitlemesin.
			try { CameraLookGuard.SetMenuOpen(!hide && !lockIt); } catch { }

			Write(hide, lockIt);
			KillSoftwareCursor();

			// LOG SPAM KORUMASI: menü FSM'i bu action'ı her karede çağırabilir.
			// Durum değişmedikçe yazmıyoruz; mscmp.txt zaten 500 KB'a ulaştı.
			if (!loggedOnce || hide != lastHide || lockIt != lastLock) {
				loggedOnce = true;
				lastHide = hide;
				lastLock = lockIt;
				SmpLog.Print("game cursor request: " + (hide ? "hidden" : "visible")
					+ ", " + (lockIt ? "locked" : "free")
					+ " (applied to Cursor directly).");
			}
		}

		/// <summary>Kare başına: kısa süreli istekleri tazele.</summary>
		public static void Tick() {
			// Odak bizde degilken (Alt-Tab masaustu) imlece DOKUNMA:
			// penceresizken lockState/visible yazmak OS imlecini
			// sol-uste (0,0) kilitliyor ("masaüstünde olmama rağmen
			// mouse sol üste kayıyor" belirtisi).
			if (!SteamMPBehaviour.AppFocused) return;
			KillSoftwareCursor();
			float now;
			try { now = Time.unscaledTime; } catch { return; }

			// Ozel imlec HER YERDE (anamenude bile): gorunurluk degisince
			// tek seferlik tak/birak. Kanca ateslenmeyen yerlerde (an menu)
			// baska turlu uygulanamiyor.
			try {
				if (Cursor.visible) ApplyCustomCursor();
				else RestoreDefaultCursor();
			}
			catch { }

			// MENÜ KAPANMA KORUMASI.
			// MenuOpen bayrağı "oyun imleci gösterdi" anında set edilir ve
			// yalnızca bir sonraki "imleci gizle" isteğinde sıfırlanır. Oyun
			// menüyü kapatırken SetMouseCursor'ı HİÇ çağırmıyorsa bayrak
			// takılı kalır ve kamera DONUK kalır — oyuncu fareye bakamaz.
			// İstekten bu kadar sonra hâlâ görünür imleç ve kilitli kamera
			// varsa menü kapanmış demektir: kamerayı bırakıyoruz.
			if (now - lastVisibleAt > CAMERA_RELEASE_SECONDS && CameraLookGuard.MenuOpen) {
				CameraLookGuard.SetMenuOpen(false);
				visibleUntil = 0f;
			}

			if (now < visibleUntil && !Cursor.visible) {
				Write(false, Cursor.lockState != CursorLockMode.None);
			}
			else if (now < hiddenUntil && Cursor.visible) {
				Write(true, false);
			}
		}

		static void Write(bool hide, bool lockIt) {
			if (!SteamMPBehaviour.AppFocused) return;
			try {
				if (Cursor.visible == hide) Cursor.visible = !hide;
				CursorLockMode want = lockIt ? CursorLockMode.Locked : CursorLockMode.None;
				if (Cursor.lockState != want) Cursor.lockState = want;
				if (!hide) ApplyCustomCursor(); else RestoreDefaultCursor();
			}
			catch { }
		}

		static Texture2D customCursor;
		static bool customTried;
		static bool customActive;

		/// <summary>DLL icine gomulu imleci yukler (assets/cursor.png).</summary>
		internal static void EnsureCustomCursor() {
			if (customTried) return;
			customTried = true;
			try {
				System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
				System.IO.Stream st = asm.GetManifestResourceStream("SteamMP.cursor.png");
				if (st == null) return;
				byte[] buf = new byte[(int)st.Length];
				int n = 0;
				while (n < buf.Length) {
					int r = st.Read(buf, n, buf.Length - n);
					if (r <= 0) break;
					n += r;
				}
				try { st.Close(); } catch { }
				Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
				if (t != null && t.LoadImage(buf)) customCursor = t;
			}
			catch { customCursor = null; }
		}

		/// <summary>Oyunun yazilimsal imlecini oldurur (dnSpy: PlayMakerGUI.OnGUI token 0x0600042F MouseCursor DrawTexture; null olunca yalniz OS imleci kalir).</summary>
		internal static void KillSoftwareCursor() {
			try { PlayMakerGUI.MouseCursor = null; } catch { }
		}

		/// <summary>SetCursor yamasi icin gomulu imlec (yuklenemezse null).</summary>
		internal static Texture2D CustomTexture() {
			EnsureCustomCursor();
			return customCursor;
		}

		/// <summary>Gorunur imleci ozel imlecle degistirir (yuklenemezse sessiz gecer).</summary>
		public static void ApplyCustomCursor() {
			if (customActive) return;
			EnsureCustomCursor();
			if (customCursor == null) return;
			try { Cursor.SetCursor(customCursor, Vector2.zero, CursorMode.Auto); customActive = true; }
			catch { }
		}

		/// <summary>Varsayilan oyun imlecine doner.</summary>
		public static void RestoreDefaultCursor() {
			if (!customActive) return;
			customActive = false;
			try { Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto); }
			catch { }
		}

		/// <summary>Sahne değişiminde istekleri bırakır.</summary>
		public static void Reset() {
			visibleUntil = 0f;
			hiddenUntil = 0f;
			lastVisibleAt = 0f;
			loggedOnce = false;
			try { CameraLookGuard.SetMenuOpen(false); } catch { }
		}
	}

	/// <summary>
	/// SetMouseCursor.OnEnter (token 0x06000DC9) postfix'i.
	/// Action çalıştıktan SONRA çağrılır; böylece PlayMakerGUI alanları
	/// önce oyunun kendi değerleriyle dolar, sonra biz Cursor'a yazarız.
	/// </summary>
	/// <summary>Cursor.SetCursor one: oyunun kendi imlec cagrisini engeller, doku daima bizimki + sicak nokta (0,0) olur. Odaksizken dokunmaz.</summary>
	[HarmonyPatch(typeof(Cursor), "SetCursor")]
	public static class CursorSetPatch {
		static void Prefix(ref Texture2D texture, ref Vector2 hotspot) {
			try {
				if (!SteamMPBehaviour.AppFocused) return;
				Texture2D ours = CursorGuard.CustomTexture();
				if (ours == null) return;
				texture = ours;
				hotspot = Vector2.zero;
			}
			catch { }
		}
	}

	[HarmonyPatch(typeof(SetMouseCursor), "OnEnter")]
	public static class SetMouseCursorPatch {
		static void Postfix(SetMouseCursor __instance) {
			try {
				bool hide = __instance.hideCursor != null && __instance.hideCursor.Value;
				// "lock" C# anahtar kelimesidir; yerel değişken adı olamaz.
				bool lockIt = __instance.lockCursor != null && __instance.lockCursor.Value;
				CursorGuard.Apply(hide, lockIt);
			}
			catch (System.Exception e) {
				SmpLog.Warning("cursor guard failed: " + e.GetType().Name + ": " + e.Message);
			}
		}
	}
}