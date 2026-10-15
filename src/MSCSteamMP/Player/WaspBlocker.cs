using System;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// WaspBlocker — arıların (Wasp/LoneWasp/WildWasp/DeathBee) FSM'lerini
	/// ayrı bir yerden engeller; tanrı modu FSM'inin içinden değil.
	///
	/// Wasp/Yapışkan karıncalarının davranışı PlayMaker FSM'lerinde:
	///   * YARD/WaspHome/LoneWasp
	///   * DRAGRACE/LOD/WaspLandfill
	///   * JOBS/StrawberryField/LOD/WaspStrawberry
	///   * MAP/Buildings/DINGONBIISI/WaspHive/...
	///   * RCO_RUSCKO12(270)/WaspHive/...
	///   * PLAYER/.../DeathBee   (ölümcül arı vuruşu)
	/// Her biri tetiklenince "WASP"/"DRIVEBEE" eventini Death FSM'ine gönderir.
	/// GodMode o FSM'i zaten kapatıyor, ama biz arı FSM'lerini de öldürüyoruz ki
	/// hiçbir ölüm event'i ÜRETİLEMESİN. Nesneler de pasifleşir (ekrandan silinir).
	///
	/// GC-free: tarama 2 saniyede bir yapılır.
	/// </summary>
	public static class WaspBlocker {

		// Sadece bu isimleri içeren nesneler hedeflenir (büyük harf/küçük harf ve
		// "Bee" için ayrı kontrol). "beer case" gibi şeylere dokunmamak için
		// "Bee" yerine "DeathBee" ve "/Wasp" gerek.
		static bool IsWasp(string n) {
			if (string.IsNullOrEmpty(n)) return false;
			if (n.IndexOf("Wasp", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (n.IndexOf("DeathBee", StringComparison.Ordinal) >= 0) return true;
			return false;
		}

		/// <summary>Etkinleştirildi mi? Her kare değil, 2 sn'de bir tara.</summary>
		static float nextScan;

		/// <summary>Etkisiz bırakılan arı FSM sayısı (teşhis).</summary>
		public static int Blocked;

		/// <summary>'smp player' / status için tek satır.</summary>
		public static string Describe() {
			return "wasp-blocker: " + Blocked + " wasp FSM/objects disabled";
		}

		/// <summary>Her kare çağrılır; tarama 2 sn'de bir yapılır.</summary>
		public static void Tick() {
			float now = Time.time;
			if (now < nextScan) return;
			nextScan = now + 2.0f;

			try {
				GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
				if (all == null) return;
				for (int i = 0; i < all.Length; i++) {
					GameObject go = all[i];
					if (go == null || go.hideFlags != HideFlags.None) continue;
					if (!IsWasp(go.name)) continue;

					// 1) Arının kendi FSM'lerini kapat: event üretmesin.
					PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
					if (fsms != null) {
						for (int j = 0; j < fsms.Length; j++) {
							if (fsms[j] != null && fsms[j].enabled) {
								fsms[j].enabled = false;
								Blocked++;
							}
						}
					}

					// 2) Nesneyi pasifleşir: arı ekrandan silinir.
					if (go.activeSelf) {
						go.SetActive(false);
						Blocked++;
					}
				}
			} catch (Exception e) {
				SmpLog.Warning("wasp scan failed: " + e.GetType().Name + ": " + e.Message);
			}
		}
	}
}
