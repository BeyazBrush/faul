using System;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Cokmeyen (crash-free) ag hiz sinirlayici (Rate Limiter) + biriktirme (batching).
	///
	/// Sorun: yuzlerce civata/parca ayni anda degisirse her degisiklik ayri
	/// paket olursa ag tikanir, Steam P2P kuyrugu siser, oyun takilir/coker.
	///
	/// Cozum:
	///   1. Hook'lar hicbir paket GONDERMEZ, yalnizca kirli (dirty) isaretler.
	///   2. Gonderim saniyede 10-20 defa (100ms limitli) toplu yapilir.
	///   3. Her flush'ta en fazla MAX_PARTS_PER_FLUSH parca + MAX_BOLTS_PER_FLUSH
	///      civata gider; fazlasi sonraki flush'a kalir (kaybolmaz, kirli kalir).
	///   4. Ayni deger tekrar gonderilmez (son-gonderilen karsilastirmasi).
	///
	/// dnSpy dogrulamasi:
	///   FsmBool.set_Value  token 0x060000AE  (hook: InstalledBoolHook)
	///   FsmFloat.set_Value token 0x060000BC  (hook: BoltFloatHook)
	///   FsmInt.set_Value   token 0x060000CA  (hook: BoltIntHook)
	///   PlayMakerFSM.SendEvent(string) token 0x060003ED -> Fsm.Event(string)
	///   Fsm.Event(FsmEventTarget,string) token 0x060001EC (oyunun ana yolu)
	///   Fsm.ProcessEvent token 0x060001E8 ilk satir: if (!Active) return;
	///   Fsm.OnDisable token 0x060001E4 -> Stop() (token 0x060001E5)
	/// Yani gizli buton SetActive(false) ile oldurulmemeli; Satsuma apply ise
	/// SuppressScope (EchoGuard) icinde yapilmali.
	///
	/// Unity 5 / .NET 3.5 uyumlu. Tum girisler null-guarded, tum gonderimler
	/// try/catch icinde, hicbir istisna oyun dongusune sizmaz.
	/// </summary>
	public static class SatsumaRateLimiter {

		/// <summary>Flush araligi: 0.10s = saniyede 10 defa (10-20Hz bandinin alti).</summary>
		public const float FLUSH_INTERVAL = 0.10f;

		/// <summary>Flush basina en fazla parca olayi. Fazlasi kirli kalir.</summary>
		public const int MAX_PARTS_PER_FLUSH = 8;

		/// <summary>Flush basina en fazla civata. Fazlasi kirli kalir.</summary>
		public const int MAX_BOLTS_PER_FLUSH = 32;

		/// <summary>Civata basi minimum gonderim araligi (float tween gurultusu icin).</summary>
		public const float MIN_BOLT_INTERVAL = 0.10f;

		/// <summary>Float karsilastirma esigi: altindaki degisim gurultudur.</summary>
		public const float EPSILON = 0.001f;

		static float nextFlushAt;

		/// <summary>Saat: testler icin degistirilebilir, oyunda realtime.</summary>
		public static Func<float> Clock = DefaultClock;
		static float DefaultClock() {
			try { return Time.realtimeSinceStartup; }
			catch { return 0f; }
		}

		/// <summary>
		/// Bu karede gonderim yapilabilir mi? SatsumaRegistry.Update icinden cagrilir.
		/// Disaridan cagrilip "erken" tuketilmemeli: tek tuketim noktasi Flush().
		/// </summary>
		public static bool ShouldFlush() {
			float now;
			try { now = Clock(); }
			catch { return false; }
			return now >= nextFlushAt;
		}

		/// <summary>
		/// Gonderim pencerisini ilerletir. Basarili/basarisiz her flush sonrasi
		/// CAGRILMALIDIR, yoksa ayni karede sonsuz gonderim olur.
		/// </summary>
		public static void MarkFlushed() {
			float now;
			try { now = Clock(); }
			catch { now = 0f; }
			nextFlushAt = now + FLUSH_INTERVAL;
		}

		/// <summary>Sahne degisiminde sayaci sifirlar (ilk degisiklik beklemez).</summary>
		public static void Reset() {
			try { nextFlushAt = Clock(); }
			catch { nextFlushAt = 0f; }
		}
	}
}
