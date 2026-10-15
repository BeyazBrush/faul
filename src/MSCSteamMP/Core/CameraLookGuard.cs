using System;
using Harmony;
using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// KONSOL AÇIKKEN OYUN KAMERASININ DÖNMESİNİ ENGELLER.
	///
	/// dnSpy kanıtı (Assembly-CSharp):
	///
	///   SimpleSmoothMouseLook.Update  token 0x06000087
	///     Screen.lockCursor = this.lockCursor;                       // ZORLAR
	///     Vector2 v = new Vector2(Input.GetAxisRaw("Mouse X"),
	///                              Input.GetAxisRaw("Mouse Y"));     // okur
	///     ... this.transform.localRotation = ...                      // döndürür
	///
	///   Yani imleci serbest bırakmak (CursorLockMode.None) İŞE YARAMAZ:
	///   oyun her karede lockCursor alanını geri yazıyor, ama o sırada
	///   GetAxisRaw("Mouse X") gerçek fare hareketi okuduğu için kamera
	///   dönmeye devam ediyordu. Konsolda log okurken kamera sallanıyordu.
	///
	/// Çözüm (iki katman):
	///   1. Prefix: konsol açıksa tüm Update'i atla → kamera hiç dönmez,
	///      lockCursor da ezilmez (kendi durumunu korur).
	///   2. Suffix değil Prefix: Unity'nin MonoBehaviour.Update'inde
	///      Prefix `bool` dönüşü false ise orijinal metot ÇAĞRILMAZ.
	///
	/// Aynı sebeple SmoothMouseLook da vardı; ikisi de yamalıdır
	/// (biri yoksa diğeri kamerayı döndürmeye devam eder).
	///
	/// Unity 5 / .NET 3.5 uyumlu. Hiçbir istisna oyun döngüsüne sızmaz.
	/// </summary>
	public static class CameraLookGuard {

		/// <summary>
		/// Konsol açıkken fare bakışını dondur.
		/// LogConsole bu değeri her karede günceller.
		/// </summary>
		public static bool ConsoleOpen;

		/// <summary>
		/// Oyunun kendi ESC menüsü açıkken de dondur.
		///
		/// ESC İMLECİ HATASI
		/// ------------------
		/// dnSpy kanıtı: SimpleSmoothMouseLook.Update (token 0x06000087) her
		/// karede `lockCursor` alanını geri yazıyor. Yani menü açılıp
		/// imleç serbest bırakıldıktan bir kare sonra kamera scripti
		/// imleci yeniden KİLİTLİYOR ve imleç kayboluyor — kullanıcı menüyü
		/// görüyor ama tıklayamıyor. İmleci açmak yetmiyor, kamera
		/// scriptinin o karede ezmesi de engellenmeli.
		///
		/// CursorGuard bu bayrağı, oyun "imleci göster" dediğinde set eder.
		/// </summary>
		public static bool MenuOpen;

		/// <summary>
		/// Kamera bakışı şu an dondurulmalı mı? Her iki gerekçe OR'lanır:
		/// konsol açık VEYA oyunun menüsü açık.
		/// </summary>
		public static bool Blocked {
			get { return ConsoleOpen || MenuOpen; }
		}

		static bool logWritten;
		static bool loggedMenu;

		/// <summary>Konsol durumu değiştiğinde bir kez teşhis yazar.</summary>
		public static void SetBlocked(bool blocked) {
			ConsoleOpen = blocked;
			if (!logWritten && blocked) {
				logWritten = true;
				SmpLog.Print("camera look frozen while the log console is open.");
			}
		}

		/// <summary>Oyunun ESC menüsü imleci açtığında bir kez teşhis yazar.</summary>
		public static void SetMenuOpen(bool open) {
			MenuOpen = open;
			if (!loggedMenu && open) {
				loggedMenu = true;
				SmpLog.Print("game menu wants the cursor: camera look frozen so the "
					+ "game script cannot re-hide it.");
			}
		}

		/// <summary>
		/// Prefix: konsol açıksa false döndürerek orijinal Update'i atla.
		/// </summary>
		public static bool Prefix() {
			if (!Blocked) return true;
			// false => orijinal metot çalışmaz, kamera döner.
			return false;
		}
	}

	/// <summary>SimpleSmoothMouseLook.Update — MSC'nin birinci-person kamera kontrolü.</summary>
	[HarmonyPatch(typeof(SimpleSmoothMouseLook), "Update")]
	public static class SimpleSmoothMouseLookPatch {
		static bool Prefix() {
			return CameraLookGuard.Prefix();
		}
	}

	/// <summary>SmoothMouseLook.Update — alternatif kamera kontrolü (araç içi/alternatif).</summary>
	[HarmonyPatch(typeof(SmoothMouseLook), "Update")]
	public static class SmoothMouseLookPatch {
		static bool Prefix() {
			return CameraLookGuard.Prefix();
		}
	}

	/// <summary>
	/// MainCamera.LateUpdate token 0x0600047D: sağ tuş basılıyken
	/// Input.GetAxis("Mouse X"/"Mouse Y") ile kamerayı döndürür ve tekerlekle
	/// zoom yapar. Konsol açıkken dönmeme ve zoom'a dokunmama gerekir:
	/// tekerlek log kaydırmak için kullanılıyor ve konsol penceresi
	/// odaktayken zoom değişmesi kullanıcıyı şaşırtıyor.
	/// </summary>
	[HarmonyPatch(typeof(MainCamera), "LateUpdate")]
	public static class MainCameraLateUpdatePatch {
		static bool Prefix() {
			return CameraLookGuard.Prefix();
		}
	}
}
