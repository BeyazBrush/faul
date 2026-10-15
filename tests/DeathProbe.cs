using System;
using System.Reflection;
using SteamMP;

// GodMode'in karar mantigini Unity olmadan dogrular.
//
// NEDEN BU PROBE DeathSync'i DEGIL GodMode'i TARİYOR:
// olumun ag uzerinden senkronizasyonu (DeathSync) v22'de kaldirildi --
// csproj'tan da silindi. GodMode o aileden bilincli bir sekilde KORUNDU,
// cunku sabit ihtiyaclar/sabit para gibi bilincli bir cheat. Silinen sey
// olum paketleriydi. Bu yuzden probun hedefi GodMode'a kaydi.
//
// NE test edilemez ve neden:
// GodMode.Tick/Discover/FailsafeTick Unity icall'u icerir (Time.time,
// Camera.main, GameObject.Find, SetActive). Masaustu CLR'da bir icall
// iceren metot JIT edilemez; istisna metodun IL'i calismadan once
// atilir, yani try/catch metot ICINDEYSA ona ulasamaz. Bu yuzden burada
// yalniz saf mantik ve sabitler dogrulanir.
public class DeathProbe {
	static int failures;

	static void Check(string what, object actual, object expected) {
		bool ok = Equals(actual, expected);
		if (!ok) failures++;
		Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + " = " + actual
			+ (ok ? "" : "   (expected " + expected + ")"));
	}

	/// <summary>
	/// Log'a yazan bir cagri. SmpLog offline de calisir (Assembly.Location
	/// uzerinden klasor bulur), ama bir ileride eklenece icall'u
	/// probeyi oldurmesin: SecurityException tolere edilir.
	/// </summary>
	static void Call(string what, Action a) {
		try { a(); }
		catch (System.Security.SecurityException) { }
		catch (Exception e) {
			failures++;
			Console.WriteLine("  FAIL " + what + " threw " + e.GetType().Name + ": " + e.Message);
		}
	}

	static void Main() {
		Console.WriteLine("--- godmode / death ---");

		// Sabitler oyunun kendi dokumunden okundu ve dogrulandi:
		//   Systems/Death [Activate Dead Body] -> start = "Permadeath 2"
		// Bunlardan biri degisirse GodMode.Discover FSM'i sessizce
		// BULAMAZ (canli log kaniti 21:03, L727: "olum FSM'ini hic
		// bulamadi") ve godmode olumleri artik engelleyemez.
		Check("DEATH_OBJECT is the dump path", GodMode.DEATH_OBJECT, "Systems/Death");
		Check("DEATH_FSM is the dump FSM name", GodMode.DEATH_FSM, "Activate Dead Body");
		Check("START_STATE is the dump start state", GodMode.START_STATE, "Permadeath 2");

		// Varsayilan ACIK: godmode baslangicta koruma saglar.
		Check("enabled by default", GodMode.Enabled, true);

		// Set() ayni degere ikinci kez yazilmayi gormez (log'dan once doner).
		Call("Set(true) twice", delegate { GodMode.Set(true); GodMode.Set(true); });
		Check("still enabled after redundant Set(true)", GodMode.Enabled, true);

		Call("Set(false)", delegate { GodMode.Set(false); });
		Check("disabled after Set(false)", GodMode.Enabled, false);

		Call("Set(true)", delegate { GodMode.Set(true); });
		Check("re-enabled after Set(true)", GodMode.Enabled, true);

		// Describe() 'smp status' icin tek satir: hem bayragi hem
		// sayaçları tasimak zorunda, yoksa teşhis satiri yanıltır.
		Call("Describe", delegate { Console.WriteLine("  describe: " + GodMode.Describe()); });
		string d = GodMode.Describe();
		Check("describe names godmode", d.StartsWith("godmode:"), true);
		Check("describe carries the flag", d.Contains("ON") || d.Contains("off"), true);

		// ResetSafety sahne degisiminde cagrilir (SteamMPBehaviour
		// OnWorldUnloading): tekrarli ve guvenli olmali.
		Call("ResetSafety once", delegate { GodMode.ResetSafety(); });
		Call("ResetSafety twice", delegate { GodMode.ResetSafety(); GodMode.ResetSafety(); });
		Check("survives repeated ResetSafety", GodMode.Enabled, true);

		// -------- durum gecis kurali (olum yakalama katmani) --------
		//
		// Bu ozellikte en pahali hata YANLIS POZITIFtir: oyuncu yukleme
		// aninda dogus noktasina yankilanirsa mod oynanamaz hale gelir.
		// Kural saf bir fonksiyona cikarildigi icin burada olculebiliyor.
		Check("no state yet is not a death",
			GodMode.ClassifyDeathState(null, true), 0);
		Check("empty state is not a death",
			GodMode.ClassifyDeathState("", true), 0);
		Check("start state is recognised before it was ever seen",
			GodMode.ClassifyDeathState(GodMode.START_STATE, false), 1);
		Check("start state is still recognised after it was seen",
			GodMode.ClassifyDeathState(GodMode.START_STATE, true), 1);
		Check("leaving the start state AFTER seeing it is a death",
			GodMode.ClassifyDeathState("Game Over", true), 2);
		// EN KRITIK SATIR: FSM'in dinlenme durumu baska bir ad olsa bile
		// oyuncu YUKLEMEDE isinlanmamali.
		Check("unknown resting state before the start state is NOT a death",
			GodMode.ClassifyDeathState("Idle", false), 0);
		Check("a different state is still traceable as just 'no verdict'",
			GodMode.ClassifyDeathState("Activate Dead Body", false), 0);

		// -------- olum OLAYI kurali (2026-10-14 duzeltmesi) --------
		//
		// Canli kanit (mscmp.txt 17:53): ilk yakalanan sinyal kan ekraniydi.
		// Oysa oyuncuyu olduren makine olayi bir DAGITIM yolundan aliyor
		// (hedef FSM'e dogrudan ProcessEvent) ve eski yamalar yalniz
		// GONDEREN tarafi goruyordu. Artik engelleme ProcessEvent'te:
		// GodMode.ShouldBlockDeathFsmEvent. Kuralin yanlis olumlu uretmesi
		// bu ozellikte en pahali hatadir (zararsiz bir olayi engellemek
		// oyunu kilitler), o yuzden ad kumesi burada olculur.
		Check("DEATH is a death trigger", GodMode.IsDeathTriggerEvent("DEATH"), true);
		Check("DIE is a death trigger (blindness path)", GodMode.IsDeathTriggerEvent("DIE"), true);
		Check("null event name is not a trigger", GodMode.IsDeathTriggerEvent(null), false);
		Check("empty event name is not a trigger", GodMode.IsDeathTriggerEvent(""), false);
		Check("lower case 'death' is not the game's event",
			GodMode.IsDeathTriggerEvent("death"), false);
		Check("FINISHED is not a trigger", GodMode.IsDeathTriggerEvent("FINISHED"), false);
		Check("a death CAUSE event is not the DEATH trigger",
			GodMode.IsDeathTriggerEvent("WASP"), false);
		Check("BLOOD is handled by the death system FSM, not by name",
			GodMode.IsDeathTriggerEvent("BLOOD"), false);

		// Olum FSM'i bulunamadiysa (deathFsm == null) hicbir sey
		// engellenmemeli: engelleme yalniz GERCEK olum FSM'i icindir.
		//
		// NOT: PlayMakerFSM bir MonoBehaviour'dur, yani Unity'nin
		// Object.op_Equality'si devreye girer; masaustu CLR'da bunun
		// JIT edilememesi mumkun. O durumda test ATLANIR (probe'un
		// FsmNameOf icin kullandigi ayni yol).
		try {
			// PlayMakerFSM GLOBAL ad alaninda (Fsm ise HutongGames.PlayMaker
			// icinde): bu yuzden yalniz Fsm tam nitelenir.
			Check("null event is never blocked",
				GodMode.ShouldBlockDeathEvent((PlayMakerFSM)null, "DIE"), false);
			Check("null FSM is never the death FSM",
				GodMode.IsDeathFsm((HutongGames.PlayMaker.Fsm)null), false);
			// ProcessEvent kapisi: korunan FSM yoksa hicbir olay
			// engellenmemeli (oyunun butun olay trafigi buradan gecer).
			Check("null FSM blocks no event at the seam",
				GodMode.ShouldBlockDeathFsmEvent((HutongGames.PlayMaker.Fsm)null, null), false);
		}
		catch (System.Security.SecurityException) {
			Console.WriteLine("  --   null-gate checks need Unity (ECall); skipped offline");
		}

		// FsmNameOf: null korumasi. PlayMakerFSM.FsmName BAZI bilesenlerde
		// bos donuyor; gercek ad ancak Fsm.Name icinde duruyor. Eski kod
		// yalniz FsmName'e bakti ve FSM'i hic bulamadi (canli 21:03).
		// Burada nesne kurulamadigi icin null kapisi dogrulanir.
		//
		// DIKKAT: FsmNameOf icindeki `f.name` bir ECall'dir, yani metodun
		// tamami masaustu CLR'da JIT edilemez. Reflection Invoke bu
		// SecurityException'i TargetInvocationException icine SARMALAR
		// (kendi try/catch'imiz ile ayni yol degil), o yuzden ana
		// istisna DEGIL ic istisna bakilir.
		MethodInfo fsmNameOf = typeof(GodMode).GetMethod("FsmNameOf",
			BindingFlags.NonPublic | BindingFlags.Static);
		Check("FsmNameOf seam exists", fsmNameOf != null, true);
		if (fsmNameOf != null) {
			try {
				Check("FsmNameOf(null) does not throw",
					fsmNameOf.Invoke(null, new object[] { null }), "");
			}
			catch (TargetInvocationException tie) {
				// Unity icall'i JIT edilemiyorsa bu BEKLENEN bir sonuctur:
				// metodun IL'i hic calismadi, yani probun soyledigi bir
				// sey yok. Bu da GodMode'in oyunda bozuk oldugunu
				// gostermez (Unity'nin JIT'i inline etmez / icall'u cozer).
				bool eCall = tie.InnerException is System.Security.SecurityException;
				Console.WriteLine((eCall ? "  --   " : "  FAIL ")
					+ "FsmNameOf(null) " + (eCall
						? "needs Unity (ECall inside the method); null gate not reacheable offline"
						: "threw " + tie.InnerException.GetType().Name));
				if (!eCall) failures++;
			}
			catch (Exception e) {
				failures++;
				Console.WriteLine("  FAIL FsmNameOf seam call threw " + e.GetType().Name + ": " + e.Message);
			}
		}

		// Kan karari: esik ve aktiflik kurali. IsBloody'nin GÖVDESİ Unity
		// icall'u içerdiği için oyun dışında JIT edilemez; kural bu yüzden
		// BloodIsDeath'te saf tutuluyor. Yoklama 4 Hz'e çıkarken yanlış
		// pozitif (kan yokken ölüm sanmak) oyunu oynanamaz hâle getirirdi:
		// bu dört kontrol tam o kapıyı tutuyor.
		Check("blood under threshold is not death",
			GodMode.BloodIsDeath(true, true, 0.01f), false);
		Check("blood exactly at threshold is not death",
			GodMode.BloodIsDeath(true, true, 0.05f), false);
		Check("blood above threshold is death",
			GodMode.BloodIsDeath(true, true, 0.5f), true);
		Check("blood on inactive object is not death",
			GodMode.BloodIsDeath(false, true, 0.9f), false);
		Check("disabled projector is not death",
			GodMode.BloodIsDeath(true, false, 0.9f), false);

		// IsBloody: önbellek boşken yalnız döngüye girer. Gövdesi yine de
		// icall içerdiği için çağrı Unity isteyebilir; bilgi satırı olarak
		// raporlanır, başarısızlık sayılmaz (FsmNameOf ile aynı desen).
		MethodInfo isBloody = typeof(GodMode).GetMethod("IsBloody",
			BindingFlags.NonPublic | BindingFlags.Static);
		Check("IsBloody seam exists", isBloody != null, true);
		if (isBloody != null) {
			try {
				Check("IsBloody() with empty cache is false",
					isBloody.Invoke(null, null), false);
			}
			catch (TargetInvocationException tie) {
				bool eCall = tie.InnerException is System.Security.SecurityException;
				Console.WriteLine((eCall ? "  --   " : "  FAIL ")
					+ "IsBloody() " + (eCall
						? "needs Unity (ECall inside the method); empty-cache gate not reacheable offline"
						: "threw " + tie.InnerException.GetType().Name));
				if (!eCall) failures++;
			}
			catch (Exception e) {
				failures++;
				Console.WriteLine("  FAIL IsBloody seam call threw " + e.GetType().Name + ": " + e.Message);
			}
		}
		// FindBlood: tarama yapan yarim Unity'ye bagli (Resources), ama
		// seam olarak var olmali -- adi degisirse cagiran taraf sessizce
		// derlenmez, o yuzden burada kilitlenir.
		Check("FindBlood seam exists", typeof(GodMode).GetMethod("FindBlood",
			BindingFlags.NonPublic | BindingFlags.Static) != null, true);

		Console.WriteLine(failures == 0
			? "ALL DEATH CHECKS PASSED"
			: "DEATH CHECKS FAILED (" + failures + ")");
		Environment.Exit(failures == 0 ? 0 : 1);
	}
}
