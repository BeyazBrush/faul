// BuildNPCBundle.cs
// Unity 5.0.0f4 Editor script. mpnpc.dat dosyasini okuyup NPC karakter modelini
// (mesh + iskelet + bone weight + materyal + legacy animasyon clip'leri) Unity
// asset'lerine cevirir ve tek bir AssetBundle'a paketler.
//
// Calistirma:
//   Unity.exe -batchmode -quit -nographics \
//     -projectPath "<proje>" -executeMethod BuildNPCBundle.Build \
//     -logFile "<log>"
//
// CIKTI: proje kokunde "mpnpc" dosyasi (AssetBundle).
// Bundle icindeki prefab yolu: assets/mpnpc/mpnpc.prefab
// (AssetBundle container yollari kucuk harfe cevrilir, bu yuzden PlayerAsset.cs
//  icindeki NPCPrefabPath ile BIREBIR ayni olmali.)
//
// BIND POSE YORUMU (olculerek belirlendi, tools/check_rig_space.py):
//   .dat icindeki 16 float SATIR-MAJOR: eleman(r,c) = v[r*4+c].
//   Ceviri 12..14 araliginda, yani Unity Matrix4x4'te son SUTUNDA.
//   Kanit: inv(bindpose) ile mesh uzayindaki kemik konumlari
//   ayak z=0.048, diz z=0.506, kalca z=0.867, kafa z=1.569 cikiyor;
//   mesh AABB'si z[0.007..1.827] ile birebir ortusuyor, ve sol/saag
//   kemikleri y ekseninde simetrik (-0.112 / +0.112).
//   Sutun-major yorumda tum kemikler (0,0,0)'a dusuyor -> bozuk.
//
// LEGACY CLIP YOLLARI:
//   Unity 5 legacy animasyonda Transform rotasyonu 4 AYRI float egri olarak
//   yazilir: "localRotation.x/.y/.z/.w". Tek egriyle ("localRotation")
//   cagrilirsa rotasyon uygulanmaz. Bu yuzden her egri bileşenlere bölünür.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class BuildNPCBundle {

	// ------------------------------------------------------------------
	// AYARLAR
	// ------------------------------------------------------------------
	const string DataPath    = @"D:\MSCSteamMP\extracted_npc\mpnpc.dat";
	const string OutDir      = "Assets/MPNPC";
	const string TexDir      = "Assets/MPNCTextures";
	/// <summary>Doku klasorunun DISKI yolu. Bundle'a alinacak tum png'ler
	/// buradan okunur; skin listesi elle yazilmaz.</summary>
	const string TexDirDisk = @"D:\MSCSteamMP\extracted_npc\textures";
	const string BundleName  = "mpnpc";

	const string PREFAB_PATH   = OutDir + "/mpnpc.prefab";
	const string MESH_PATH     = OutDir + "/mpnpc.mesh.asset";
	// Bundle icindeki container yolu, kucuk harfli:
	const string CONTAINER_PREFAB = "assets/mpnpc/mpnpc.prefab";

	const int KIND_POS = 0, KIND_ROT = 1, KIND_SCALE = 2;

	// ------------------------------------------------------------------
	class BoneRec {
		public string name, path, parentPath;
		public int parent, skinIndex;
		public bool isSkin;
		public Vector3 pos;
		public Quaternion rot;
		public Vector3 scale;
		public Matrix4x4 bind;
	}

	class CurveRec {
		public string path;
		public int kind;
		public float[] values;   // keyCount * comp
		public float[] times;    // keyCount
		public int Comp { get { return kind == KIND_ROT ? 4 : 3; } }
	}

	class ClipRec {
		public string name;
		public float length;
		public bool loop;
		public List<CurveRec> curves = new List<CurveRec>();
		/// <summary>Unity AnimationClip asset'i (Run() bir kez yazar, her
		/// prefab ayni asset'i referanslar).</summary>
		public AnimationClip clip;
	}

	/// <summary>Bir .dat'tan okunan govde: mesh + ortak iskelet + klipler.
	///
	/// NEDEN AYRI SINIF: oyunda "bodymesh" adi altinda birden fazla mesh
	/// var (level2'de 186 SMR, 6 farkli mesh) ve NPC'ler farkli olanlari
	/// kullaniyor. make_bodies.py her biri icin ayri .dat uretip yazar.
	/// Iskelet (20 kemik) ve klipler (26) HEPSINDE ayni; yalnizca mesh
	/// degisiyor. Onceki surum tek mesh varsayiyordu ve 70 skinin 54'une
	/// yanlis govde giydiriyordu ("kiyafetler yanlis giydirilmis").
	/// </summary>
	class Skeleton {
		public string name;
		public Vector3[] verts;
		public Vector3[] norms;
		public Vector4[] tans;
		public Vector2[] uvs;
		public int[] indices;
		public List<int> subCounts = new List<int>();
		public List<BoneRec> bones = new List<BoneRec>();
		public int rootBone;
		public int skinCount;
		public int[] skinOrder;
		public List<ClipRec> clips = new List<ClipRec>();
		public string[] matNames;
		public string[] matTexs;
		public BoneWeight[] weights;
	}

	// ------------------------------------------------------------------
	public static void Build() {
		try {
			Run();
			Debug.Log("[MPNPC] BUILD OK");
			// Unity 5.0.0f4'te -executeMethod sonrasi -quit TETIKLENMIYOR;
			// editor acik kalip batch'i blokluyor. Assetler SaveAssets ile
			// yazildi, bundle dosyasi kopyalandi, sorun yok.
			EditorApplication.Exit(0);
		} catch (Exception e) {
			Debug.LogError("[MPNPC] BUILD FAIL: " + e.GetType().Name + ": " + e.Message
				+ "\n" + e.StackTrace);
			EditorApplication.Exit(1);
		}
	}

	/// <summary>GOVDE MESH LISTESI.
	/// Oyunda "bodymesh" adi altinda birden fazla mesh var ve NPC'ler
	/// farkli olanlari kullaniyor (level2'de 186 SMR, 6 farkli mesh).
	/// make_bodies.py her biri icin ayri .dat uretip yazar; iskelet ve
	/// klipler hepsinde ayni (20 kemik, 26 klip), yalnizca mesh degisiyor.</summary>
	static readonly string[] BodyFiles = {
		"mpnpc_1712.dat",   // varsayilan (39/70 skin)
		"mpnpc_1714.dat",
		"mpnpc_1709.dat",
		"mpnpc_1697.dat",   // kadin / ince
		"mpnpc_1789.dat",   // uzun
		"mpnpc_960.dat",    // cocuk
	};

	const string BodyDir = "Assets/MPNCBodies";

	static void Run() {
		EnsureFolder("Assets");
		EnsureFolder(OutDir);
		EnsureFolder(TexDir);
		EnsureFolder(BodyDir);

		// Govde .dat'larini kaynak klasorunden Assets'e tasi.
		string srcDir = @"D:\MSCSteamMP\extracted_npc\bodies";
		if (Directory.Exists(srcDir)) {
			foreach (string bf in BodyFiles) {
				string s = Path.Combine(srcDir, bf);
				if (File.Exists(s))
					File.Copy(s, Path.Combine(BodyDir, bf), true);
			}
		}

		if (!File.Exists(DataPath)) {
			throw new Exception("veri dosyasi yok: " + DataPath
				+ "\nOnce tools/make_bodies.py calistir.");
		}

		// ---------------- 1) iskelet + klipler (ortak) ----------------
		// Govde dosyalari ayni iskeleti ve ayni 26 klipi tasir; bir kez okunur.
		Skeleton shared = ReadSkeleton(DataPath);
		Log("iskelet (ortak): " + shared.bones.Count + " dugum ("
			+ shared.skinCount + " skin), clip: " + shared.clips.Count);

		// ---------------- 2) materyaller ----------------
		// Mesh, iskelet ve klipler her govde dosyasinda ayni sekilde
		// kurulur; materyaller ortaktir. once tek kez kurulur.
		Shader sh = FindShader();
		FixTextureImporters();
		Material[] mats = new Material[shared.matNames.Length];
		for (int mi = 0; mi < shared.matNames.Length; mi++) {
			string texPath = TexDir + "/" + shared.matTexs[mi];
			Texture2D tex = AssetDatabase.LoadAssetAtPath(
				texPath, typeof(Texture2D)) as Texture2D;
			if (tex == null) tex = LoadPngToAsset(texPath);
			if (tex == null) { tex = Texture2D.whiteTexture; Log("doku YOK: " + texPath); }
			Material m = new Material(sh);
			m.name = shared.matNames[mi];
			if (m.HasProperty("_MainTex")) m.mainTexture = tex;
			string ap = OutDir + "/" + shared.matNames[mi] + ".mat";
			AssetDatabase.DeleteAsset(ap);
			AssetDatabase.CreateAsset(m, ap);
			mats[mi] = m;
		}
		AssetDatabase.SaveAssets();
		Log("materyal: " + mats.Length + " adet, shader="
			+ (sh != null ? sh.name : "NULL"));

		List<ClipRec> sharedClips = shared.clips;

		// Bundle'a girilecek asset yollari. Prefab'lar konteynere
		// girmeden runtime'da LoadAsset<GameObject> null doner, bu yuzden
		// HER prefab buraya eklenir.
		List<string> assetPaths = new List<string>();
		HashSet<string> assetSeen = new HashSet<string>();
		// Bundle'a girmeden runtime'da LoadAsset<GameObject> null doner,
		// bu yuzden HER prefab buraya eklenir.
		for (int pi = 0; pi < assetPaths.Count; pi++) assetSeen.Add(assetPaths[pi]);

		// ---------------- 3) GOVDE PREFAB'LARI ----------------
		// make_bodies.py her govde icin ayri .dat uretir. Hepsi ayni
		// iskeleti (20 kemik) ve ayni 26 klipi tasir; yalnizca mesh farklidir.
		// Klipler ONCE yazilir: her prefab ayni Animation asset'larini
		// referanslar (kopyalanmaz), boylece bundle'da tek kopya durur.
		for (int ci = 0; ci < sharedClips.Count; ci++) {
			ClipRec c = sharedClips[ci];
			HashSet<string> rigPaths = new HashSet<string>();
			foreach (BoneRec b in shared.bones) rigPaths.Add(b.path);

			AnimationClip clip = new AnimationClip();
			clip.name = c.name;
			clip.legacy = true;                 // ONCE legacy, sonra SetCurve
			clip.frameRate = 30f;
			clip.wrapMode = c.loop ? WrapMode.Loop : WrapMode.Default;

			int used = 0, skipped = 0;
			foreach (CurveRec cv in c.curves) {
				if (!rigPaths.Contains(cv.path)) { skipped++; continue; }
				AddCurve(clip, cv);
				used++;
			}
			c.clip = clip;

			string ap = OutDir + "/" + c.name + ".anim";
			AssetDatabase.DeleteAsset(ap);
			AssetDatabase.CreateAsset(clip, ap);
			assetPaths.Add(ap);
			Log("clip: " + c.name + "  " + used + " egri"
				+ (skipped > 0 ? " (" + skipped + " yol bulunamadi)" : "")
				+ "  loop=" + c.loop + "  len=" + c.length.ToString("0.00"));
		}
		AssetDatabase.SaveAssets();

		assetPaths.Clear();
		for (int bi = 0; bi < BodyFiles.Length; bi++) {
			string bf = BodyFiles[bi];
			string bdisk = BodyDir + "/" + bf;
			if (!File.Exists(bdisk)) { Log("govde atlandi: " + bf); continue; }
			GameObject broot = BuildOneBody(bdisk, bf, mats,
				sharedClips, shared.matNames);
			if (broot == null) continue;
			string stem = Path.GetFileNameWithoutExtension(bf);
			string prefabPath = OutDir + "/" + stem + ".prefab";
			AssetDatabase.DeleteAsset(prefabPath);
			PrefabUtility.CreatePrefab(prefabPath, broot);
			AssetDatabase.SaveAssets();
			GameObject.DestroyImmediate(broot);
			assetPaths.Add(prefabPath);
			Log("govde prefab: " + prefabPath);
		}
		if (assetPaths.Count == 0)
			throw new Exception("hicbir govde prefab'i yazilamadi");
		AssetDatabase.SaveAssets();


		// ---------------- 4) bundle ----------------
		string outPath = Path.Combine(
			Directory.GetParent(Application.dataPath).FullName, BundleName);
		if (File.Exists(outPath)) File.Delete(outPath);

		// Unity 5.0 imzasi:
		//   BuildAssetBundle(Object assetBundleName, Object[] assets,
		//                   string variant, BuildAssetBundleOptions)
		// ilk arguman string DEGIL, bir Object olmali (genelde TextAsset) ve
		// cikti dosya adi onun adindan turetilir. Assetleri Object[] olarak
		// vermek zorundayiz, yollar dogrudan verilemez.
		string nameAssetPath = OutDir + "/" + BundleName + ".txt";
		string nameAssetDisk = nameAssetPath.Replace('/', '\\');
		// eski adi temizle: Unity cikti dosya adini bu asset'in adindan turetir
		string oldNameAsset = OutDir + "/" + BundleName + "_bundlename.txt";
		AssetDatabase.DeleteAsset(oldNameAsset);
		File.WriteAllText(nameAssetDisk, BundleName);
		AssetDatabase.ImportAsset(nameAssetPath, ImportAssetOptions.ForceUpdate);
		TextAsset nameAsset = AssetDatabase.LoadAssetAtPath(
			nameAssetPath, typeof(TextAsset)) as TextAsset;
		if (nameAsset == null) throw new Exception("bundle ad asset'i yuklenemedi");
		AssetDatabase.Refresh();

		// ---- skin dokulari: klasordeki TUM png'ler bundle'a girer.
		// Katalog tools\gen_skin_catalog.py ile uretilir ve 47 doku ister;
		// sabit liste yazmak, oyun dokusu yeniden adlandirilirsa sessizce
		// eksik skin birakirdi. Klasoru taramak tek dogru kaynak.
		int skinTex = 0;
		if (Directory.Exists(TexDirDisk)) {
			string[] pngs = Directory.GetFiles(TexDirDisk, "*.png");
			Array.Sort(pngs);
			for (int i = 0; i < pngs.Length; i++) {
				string ap = TexDir + "/" + Path.GetFileName(pngs[i]);
				if (assetSeen.Add(ap)) {
					assetPaths.Add(ap);
					skinTex++;
				}
			}
		}
		Log("skin dokusu: " + skinTex + " adet");

		string[] names = assetPaths.ToArray();
		UnityEngine.Object[] assets = new UnityEngine.Object[names.Length];
		for (int i = 0; i < names.Length; i++) {
			assets[i] = AssetDatabase.LoadAssetAtPath(
				names[i], typeof(UnityEngine.Object));
			if (assets[i] == null) Log("UYARI: asset yuklenemedi: " + names[i]);
		}
		// Unity 5.0'da BuildAssetBundle AssetBundle DEGIL bool donuyor.
		// CollectDependencies sart: prefabin bilesenleri (Animation +
		// SkinnedMeshRenderer) dogrudan listede olmadigi icin Unity
		// "2 component(s) of GameObject 'mpnpc' are missing in the
		// AssetBundle" deyip false donuyor.
		AssetDatabase.Refresh();
		DeleteAnyBundleFiles();
		bool ok = BuildPipeline.BuildAssetBundle(
			nameAsset, assets, null,
			BuildAssetBundleOptions.CollectDependencies);
		Log("BuildAssetBundle(" + nameAsset.name + ", " + assets.Length
			+ " asset, variant=null) -> " + ok);

		if (!ok) {
			// Unity 5.0.0f4 bu ortamda biten bundle'i Temp/unitystream.unity3d
			// altina yaziyor ama son "replace" adiminda basarisiz oluyor ve
			// false donuyor. Dosyanin ICERIGI tam (UnityPy ile dogrulandi:
			// 7 clip + mesh + 3 materyal + 3 doku + prefab), sadece yer
			// degistirilmedi. O yuzden yerlestirmeyi biz yapiyoruz.
			string tempBuilt = Path.Combine(
				Directory.GetParent(Application.dataPath).FullName,
				Path.Combine("Temp", "unitystream.unity3d"));
			if (!File.Exists(tempBuilt)) {
				throw new Exception("BuildPipeline.BuildAssetBundle basarisiz ve "
					+ "Temp/unitystream.unity3d bulunamadi");
			}
			Log("BuildAssetBundle false dondu; Temp/unitystream.unity3d "
				+ "(" + new FileInfo(tempBuilt).Length + " bayt) tasiniyor -> " + outPath);
			File.Copy(tempBuilt, outPath, true);
		}

		Log("bundle: " + outPath + "  " + new FileInfo(outPath).Length + " bayt");
		Log("bundle icindeki prefab: " + CONTAINER_PREFAB);
	}

	/// <summary>Bir .dat dosyasini okur: mesh + iskelet + klipler + materyaller.
	/// Her govde icin cagrilir; iskelet ve klipler ayni cikarsa da
	/// dosyalar ayri ayri okunur (boylece bir govde bozulursa digerleri etkilenmez).</summary>
	static Skeleton ReadSkeleton(string path) {
		byte[] raw = File.ReadAllBytes(path);
		Skeleton s = new Skeleton();
		int o = 0;

		if (EncodingCheck(raw, o, "MPNPC")) o += 5; else throw new Exception("magic hatasi: " + path);
		int version = ReadI32(raw, ref o);
		if (version != 1) throw new Exception("versyon desteklenmiyor: " + version);
		s.name = ReadStr(raw, ref o);

		int vc = ReadI32(raw, ref o);
		int ic = ReadI32(raw, ref o);
		int smc = ReadI32(raw, ref o);

		s.verts = new Vector3[vc];
		for (int i = 0; i < vc; i++)
			s.verts[i] = new Vector3(F32(raw, ref o), F32(raw, ref o), F32(raw, ref o));
		s.norms = new Vector3[vc];
		for (int i = 0; i < vc; i++)
			s.norms[i] = new Vector3(F32(raw, ref o), F32(raw, ref o), F32(raw, ref o));
		s.tans = new Vector4[vc];
		for (int i = 0; i < vc; i++)
			s.tans[i] = new Vector4(F32(raw, ref o), F32(raw, ref o),
			                        F32(raw, ref o), F32(raw, ref o));
		s.uvs = new Vector2[vc];
		for (int i = 0; i < vc; i++)
			s.uvs[i] = new Vector2(F32(raw, ref o), F32(raw, ref o));
		s.indices = new int[ic];
		for (int i = 0; i < ic; i++) s.indices[i] = ReadI32(raw, ref o);
		for (int i = 0; i < smc; i++) s.subCounts.Add(ReadI32(raw, ref o));

		int bc = ReadI32(raw, ref o);
		for (int i = 0; i < bc; i++) {
			BoneRec b = new BoneRec();
			b.name   = ReadStr(raw, ref o);
			b.path   = ReadStr(raw, ref o);
			b.parent = ReadI32(raw, ref o);
			b.isSkin = ReadI32(raw, ref o) != 0;
			b.pos    = new Vector3(F32(raw, ref o), F32(raw, ref o), F32(raw, ref o));
			b.rot    = new Quaternion(F32(raw, ref o), F32(raw, ref o),
			                           F32(raw, ref o), F32(raw, ref o));
			b.scale  = new Vector3(F32(raw, ref o), F32(raw, ref o), F32(raw, ref o));
			// SATIR-MAJOR: eleman(r,c) = v[r*4+c]; ceviri son sutunda
			Matrix4x4 m = new Matrix4x4();
			for (int r = 0; r < 4; r++)
				m.SetRow(r, new Vector4(F32(raw, ref o), F32(raw, ref o),
				                        F32(raw, ref o), F32(raw, ref o)));
			b.bind = m;
			s.bones.Add(b);
		}
		s.rootBone = ReadI32(raw, ref o);

		s.skinCount = ReadI32(raw, ref o);
		s.skinOrder = new int[s.skinCount];
		for (int i = 0; i < s.skinCount; i++) s.skinOrder[i] = ReadI32(raw, ref o);

		int wc = ReadI32(raw, ref o);
		if (wc != vc)
			throw new Exception("weight sayisi (" + wc + ") != vertex (" + vc + ")  " + path);
		s.weights = new BoneWeight[wc];
		for (int i = 0; i < wc; i++) {
			BoneWeight w = new BoneWeight();
			w.boneIndex0 = ReadI32(raw, ref o);
			w.boneIndex1 = ReadI32(raw, ref o);
			w.boneIndex2 = ReadI32(raw, ref o);
			w.boneIndex3 = ReadI32(raw, ref o);
			w.weight0    = F32(raw, ref o);
			w.weight1    = F32(raw, ref o);
			w.weight2    = F32(raw, ref o);
			w.weight3    = F32(raw, ref o);
			s.weights[i] = w;
		}

		int mc = ReadI32(raw, ref o);
		s.matNames = new string[mc];
		s.matTexs  = new string[mc];
		for (int i = 0; i < mc; i++) {
			s.matNames[i] = ReadStr(raw, ref o);
			s.matTexs[i]  = ReadStr(raw, ref o);
		}
		if (mc != smc) {
			Log("UYARI: submesh=" + smc + " materyal=" + mc + " -> esitleniyor");
			while (s.subCounts.Count < mc) s.subCounts.Add(0);
			while (s.subCounts.Count > mc) s.subCounts.RemoveAt(s.subCounts.Count - 1);
		}

		int clc = ReadI32(raw, ref o);
		for (int i = 0; i < clc; i++) {
			ClipRec c = new ClipRec();
			c.name   = ReadStr(raw, ref o);
			c.length = F32(raw, ref o);
			c.loop   = ReadI32(raw, ref o) != 0;
			int nc = ReadI32(raw, ref o);
			for (int k = 0; k < nc; k++) {
				CurveRec cv = new CurveRec();
				cv.path = ReadStr(raw, ref o);
				cv.kind = ReadI32(raw, ref o);
				int kc = ReadI32(raw, ref o);
				int comp = cv.Comp;
				cv.values = new float[kc * comp];
				for (int j = 0; j < cv.values.Length; j++) cv.values[j] = F32(raw, ref o);
				cv.times = new float[kc];
				for (int j = 0; j < kc; j++) cv.times[j] = F32(raw, ref o);
				c.curves.Add(cv);
			}
			s.clips.Add(c);
		}
		Log(Path.GetFileName(path) + ": mesh " + vc + " vertex, iskelet "
			+ s.bones.Count + ", clip " + s.clips.Count);
		return s;
	}

	/// <summary>
	/// Govdede HeadPivot yoksa, head bone'unun araya girer.
	///
	/// KOK NEDEN: klipler referans rig'e (1712) gore yazildi ve
	///   spine_upper/HeadPivot/head
	/// yolunu kullanir. Cocuk govdesinde (960) HeadPivot YOK; head
	/// dogrudan spine_upper'in altindadir. Unity eslesmeyen egriyi
	/// SESSIZCE birakir, sonuc: kafa hic kipirdamaz.
	///
	/// COZUM: araya KIMLIK (identity) bir HeadPivot koy. Kimlik
	/// oldugu icin head'in DUNYA donusumu DEGISMEZ:
	///   once : spine_upper * head.local
	///   sonra: spine_upper * HeadPivot(identite) * head.local = ayni
	/// SkinnedMeshRenderer kemikleri REFERANSLA baglar ve dizi
	/// degismedigi icin bindpose/agirliklar da etkilenmez.
	/// </summary>
	static int EnsureHeadPivot(List<BoneRec> bones, GameObject[] nodes) {
		if (bones == null || nodes == null) return 0;
		for (int i = 0; i < bones.Count; i++) {
			if (bones[i] != null && bones[i].name == "HeadPivot") return 0;
		}
		for (int i = 0; i < bones.Count; i++) {
			BoneRec b = bones[i];
			if (b == null || b.name != "head") continue;
			int p = b.parent;
			if (p < 0 || p >= nodes.Length || nodes[p] == null) continue;

			bool underPivot = false;
			int k = p, guard = 0;
			while (k >= 0 && k < bones.Count && guard++ < 64) {
				if (bones[k] != null && bones[k].name == "HeadPivot") {
					underPivot = true;
					break;
				}
				int q = bones[k].parent;
				if (q == k) break;
				k = q;
			}
			if (underPivot) return 0;

			GameObject pivot = new GameObject("HeadPivot");
			pivot.transform.parent = nodes[p].transform;
			pivot.transform.localPosition = Vector3.zero;
			pivot.transform.localRotation = Quaternion.identity;
			pivot.transform.localScale    = Vector3.one;
			// head'i tasindir; YEREL TRS'i OLDUGU GIBI kalir, bu
			// yuzden dunya donusumu bit bit ayni kalir.
			nodes[i].transform.parent = pivot.transform;
			return 1;
		}
		return 0;
	}

	/// <summary>
	/// Unity, ayni isimli ikinci bir dugumu "HeadPivot 5" diye yeniden
	/// adlandirir. Klip yollari referans rig'e (1712) gore cozuldugu icin
	/// bu ek yollari bozuyor: kafa egirisi tutmaz, omurga tutar, boyun
	/// uzar.
	///
	/// Burada eki atilir. Guvenlik: ayni ebeveyn altinda ek'siz karsilik
	/// ZATEN varsa dokunulmaz (gercekten iki ayri dugum demektir).
	/// </summary>
	static int StripDuplicateSuffix(string name) {
		if (string.IsNullOrEmpty(name)) return 0;
		int i = name.Length;
		while (i > 0 && name[i - 1] >= '0' && name[i - 1] <= '9') i--;
		if (i >= name.Length || i == 0) return 0;              // rakam yok
		if (i < name.Length && name[i - 1] != ' ') return 0; // ' ' yok
		return i - 1;                                          // kesilecek uzunluk
	}

	/// <summary>Govde kurulduktan SONRA cagrilir: yollari referans
	/// rig'e hizalar. Donen: yeniden adlandirilan dugum sayisi.</summary>
	static int AlignBoneNamesToReference(List<BoneRec> bones, GameObject[] nodes) {
		if (bones == null || nodes == null) return 0;
		int renamed = 0;
		for (int i = 0; i < bones.Count && i < nodes.Length; i++) {
			BoneRec b = bones[i];
			if (b == null || nodes[i] == null) continue;
			int cut = StripDuplicateSuffix(b.name);
			if (cut <= 0) continue;
			string bare = b.name.Substring(0, cut);
			if (string.IsNullOrEmpty(bare)) continue;

			// Ayni ebeveyn altinda ek'siz karsilik var mi? Varsa dokunma.
			bool collision = false;
			for (int j = 0; j < bones.Count; j++) {
				if (j == i || bones[j] == null) continue;
				if (bones[j].parent != b.parent) continue;
				if (bones[j].name == bare) { collision = true; break; }
			}
			if (collision) continue;

			nodes[i].name = bare;
			b.name = bare;
			renamed++;
		}
		return renamed;
	}

	/// <summary>Bir govde prefab'i kurar ve kok nesneyi DONER.
	///
	/// ISKELET GOVDEYE OZGUDUR. Onceki surum ORTAK (1712) iskeleti
	/// kullaniyordu; 960'lik cocuk mesh'i 22 kemikli baska bir rig
	/// kullandigi icin agirliklar yanlis bindpose'larla esleniyor ve
	/// kiyafetler bozuk gorunuyordu. Kanit: diag_bodies.py ciktisi.
	/// Animasyon klipleri ONCEDEN .anim asset'i olarak yazilir ve burada
	/// Animation component'ine referanslanir (kopyalanmaz).</summary>
	static GameObject BuildOneBody(string datPath, string fileName,
			Material[] mats, List<ClipRec> clips, string[] matNames) {

		Skeleton s = ReadSkeleton(datPath);

		// ---- mesh
		Mesh mesh = new Mesh();
		mesh.name = Path.GetFileNameWithoutExtension(fileName) + "_mesh";
		mesh.vertices  = s.verts;
		mesh.normals   = s.norms;
		mesh.tangents  = s.tans;
		mesh.uv        = s.uvs;
		List<int> subCounts = s.subCounts;
		mesh.subMeshCount = subCounts.Count;
		int cursor = 0;
		for (int i = 0; i < subCounts.Count; i++) {
			int n = subCounts[i];
			if (cursor + n > s.indices.Length) n = s.indices.Length - cursor;
			if (n <= 0) { mesh.SetTriangles(new int[0], i); continue; }
			int[] tri = new int[n];
			Array.Copy(s.indices, cursor, tri, 0, n);
			mesh.SetTriangles(tri, i);
			cursor += n;
		}
		mesh.boneWeights = s.weights;
		// BINDPOSE GOVDENIN KENDI ISKELETINDEN GELIR.
		// 960'lik cocuk mesh'i 22 kemikli farkli bir rig kullanir
		// (collar_left/collar_right var, HeadPivot yok) ve agirliklar
		// O indekslemeye gore yazildi. 1712'nin bindposeleriyle
		// eslestirilince kemikler kayar, kiyafet 'eriyerek' gorunur.
		int sc = s.skinCount;
		Matrix4x4[] bp = new Matrix4x4[sc];
		for (int i = 0; i < sc; i++) bp[i] = s.bones[s.skinOrder[i]].bind;
		mesh.bindposes = bp;
		mesh.RecalculateBounds();
		string meshPath = OutDir + "/" + mesh.name + ".asset";
		AssetDatabase.DeleteAsset(meshPath);
		AssetDatabase.CreateAsset(mesh, meshPath);

		// ---- kok + iskelet
		// Hiyerarsi GOVDENIN KENDI verisinden kurulur.
		List<BoneRec> bones = s.bones;
		int rootBone = s.rootBone;
		GameObject root = new GameObject(Path.GetFileNameWithoutExtension(fileName));
		GameObject[] nodes = new GameObject[bones.Count];
		for (int i = 0; i < bones.Count; i++) {
			GameObject g = new GameObject(bones[i].name);
			nodes[i] = g;
			if (bones[i].parent >= 0 && bones[i].parent < nodes.Length
					&& bones[i].parent != i) {
				g.transform.parent = nodes[bones[i].parent].transform;
			} else if (i == 0) {
				g.transform.parent = root.transform;
			}
			g.transform.localPosition = bones[i].pos;
			g.transform.localRotation = bones[i].rot;
			g.transform.localScale    = bones[i].scale;
		}
		nodes[0].transform.localPosition = Vector3.zero;
		nodes[0].transform.localRotation = Quaternion.identity;
		nodes[0].transform.localScale    = Vector3.one;

		GameObject bodyGo = new GameObject("bodymesh");
		bodyGo.transform.parent = nodes[0].transform;
		bodyGo.transform.localPosition = Vector3.zero;
		bodyGo.transform.localRotation = Quaternion.identity;
		bodyGo.transform.localScale    = Vector3.one;

		SkinnedMeshRenderer smr = bodyGo.AddComponent<SkinnedMeshRenderer>();
		smr.sharedMesh = mesh;
		Transform[] boneArr = new Transform[sc];
		for (int i = 0; i < sc; i++) boneArr[i] = nodes[s.skinOrder[i]].transform;
		smr.bones = boneArr;
		smr.rootBone = nodes[rootBone].transform;
		smr.sharedMaterials = mats;
		smr.localBounds = mesh.bounds;
		smr.quality = SkinQuality.Bone4;
		smr.updateWhenOffscreen = true;

		// ---- animasyon
		Animation anim = root.AddComponent<Animation>();
		anim.cullingType = AnimationCullingType.AlwaysAnimate;
		anim.playAutomatically = false;
		for (int i = 0; i < clips.Count; i++) {
			if (clips[i].clip != null) anim.AddClip(clips[i].clip, clips[i].name);
		}

		// Klip yollarini bu govdenin gercek hiyerarsisine hizala.
		// SIRA ONEMLI:
		//  1) Once "HeadPivot 5" gibi ekleri at. Aksi halde asagidaki
		//     adim "HeadPivot 5"i HeadPivot saymaz ve GEREKSIZ bir
		//     tane daha ekler (ikinci HeadPivot olur, yol belirsizlesir).
		//  2) Sonra eksik arac dugumu ekle: klipler o yolu kullaniyor
		//     ama cocuk govdede (960) o dugum hic yok.
		int renamed = AlignBoneNamesToReference(bones, nodes);
		if (renamed > 0) Log(fileName + ": " + renamed
			+ " bone name(s) aligned to the reference rig"
			+ " (clip curves would have been dropped).");
		int pivots = EnsureHeadPivot(bones, nodes);
		if (pivots > 0) Log(fileName + ": " + pivots
			+ " identity HeadPivot inserted (head bone curve was dropped).");

		// ---- yon + ayak hizasi (prefaba YAZILMADAN once olcmeli)
		FixWorldOrientation(root, nodes, mesh);
		GroundFeet(root, nodes, bones, rootBone, mesh);
		return root;
	}


	/// <summary>Onceki cikti dosyasini temizler.
	/// SADECE proje kokundeki/Assets icindeki, adi tam olarak BundleName olan
	/// ve UZANTISI OLMAYAN dosyaya dokunur. Temp/unitystream.unity3d Unity'nin
	/// gecici cikti dosyasidir ve silinmemelidir.</summary>
	static void DeleteAnyBundleFiles() {
		string root = Directory.GetParent(Application.dataPath).FullName;
		string[] roots = new string[] {
			root,
			Path.Combine(root, "Assets"),
			Path.Combine(root, Path.Combine("Assets", OutDir.Replace('/', '\\'))),
		};
		for (int r = 0; r < roots.Length; r++) {
			if (!Directory.Exists(roots[r])) continue;
			string[] hits = Directory.GetFiles(roots[r]);
			for (int i = 0; i < hits.Length; i++) {
				string fn = Path.GetFileName(hits[i]);
				if (fn != BundleName) continue;
				Log("eski bundle siliniyor: " + hits[i]);
				File.Delete(hits[i]);
			}
		}
	}

	/// <summary>Prefab kokune, karakterin gercek dunya yonune gore bir duzeltme
	/// dondurumu yazar.
	///
	/// NEDEN: NPC mesh'i Z ekseni boyunca UZATILMIS olarak saklanir
	/// (mesh AABB z[0.007..1.827], ayak z=0.048, kafa z=1.569; y ekseni
	/// kollarin acilma genisligi). Unity'de dikey eksen Y, o yuzden model
	/// oldugu gibi birakilirsa YATIK durur.
	///
	/// NASIL: baglam pozunda mesh rijittir, yani
	///   C = boneWorld(rootBone) * bindpose(rootBone)
	/// sabittir ve tum kemikler icin bones[i].localToWorld * bindposes[i] == C
	/// esitligini saglar. C'yi mesh kutusunun kose noktalarina uygulayip
	/// karakterin gercek DUNYA kutusunu elde ediyoruz; boylece boylemesinin
	/// hangi eksende oldugunu ve nereye baktigini olcuyoruz, tahmin etmiyoruz.
	///
	/// Sonra:
	///   up = normalize(kafa - ayak)   -> (0,1,0)'a cevrilir
	///   fwd = mesh X ekseni           -> dunya +Z'ye cevrilir (yoksa kafa yatay)
	/// Her iki dondurum de prefab kokune TEK bir Quaternion olarak yazilir,
	/// boylece kemik baglantilari ve bindposeler aynen kalir.</summary>
	/// <summary>Prefab kokune, karakterin GERCEK donusunu yazar.
	///
	/// NEDEN: mesh Z ekseni boyunca uzatilmis olarak saklaniyor (ayaklar mesh
	/// z=0.048, kafa z=1.569; olculdu) ama Unity'de dikey eksen Y.
	///
	/// ONCEKI YONTEMLER (3 kez) YANLISTI:
	///  1) mesh AABB'sinin Y eksenini "yukari" kabul ediyordu. Oysa mesh
	///     AABB'si x=0.34 (derinlik), y=1.35 (KOL ACIKLIGI), z=1.82 (govde).
	///  2) Quaternion.LookRotation(fwd, up) kullaniyordu. LookRotation(f, u)
	///     "nesnenin +Z'si f'ye baksin" demektir; bizim istedigimiz "f'yi
	///     (0,0,1)'e CEVRIR". Sonuc: model X ekseninde yatmis durdu.
	///  3) Yonu KEMIKLERDEN olctu: up = head - pelvis. Bu da YANLIS cunku
	///     bindposeler 90 degelik bir donusum tasiyor (bind[0] satirinda
	///     0,0,-1 / 1,0,0 / 0,-1,0 gorunuyor). Yani kemiklerin BAG POZUNDAKI
	///     world konumlari mesh uzayinda degil; up = -Z olculuyor ve model
	///     BAS AYGIDA kaldi (offline render: kafa altta, ayakkabilar ustte).
	///
	/// DOGRU YONTEM: yonu MESH'TEN olc, kemikler hic karistirma.
	///   - govde ekseni = mesh AABB'sinin EN UZUN ekseni (1.82 / 1.35 / 0.34)
	///   - kafa = EN AZ ucgenli submesh (yuz: 894 ucgen; gomlek 3438,
	///     pantolon 3708). Bu submesh'in agirligi govde ekseninin hangi ucunda
	///     oldugunu soyler -> iste bu "yukari" yonunu verir.
	///   - ileri = yuz submesh'inin ortalama NORMALI (burun/kulak disari
	///     bakar), up'a diklestirilir.
	/// Dunya hedefi: right -> +X, up -> +Y, fwd -> +Z. Quaternion, kaynak ve
	/// hedef bAZ[ right | up | fwd ] matrislerinden Shepperd yontemiyle
	/// uretilir. Determinant negatifse (ayna) fwd isaretlenir; boylece daima
	/// GERCEK bir dondurum elde edilir (negatif olcek = ayna = ASLA olmamali).
	/// </summary>
	static void FixWorldOrientation(GameObject root, GameObject[] nodes,
			Mesh mesh) {

		Vector3 up = Vector3.forward, fwd = Vector3.forward, right = Vector3.right;
		string src = "temel kural";

		try {
			Bounds b = VectorMinMax(mesh);
			Vector3 lo = b.min, hi = b.max;
			float[] ext = { hi.x - lo.x, hi.y - lo.y, hi.z - lo.z };
			int bodyAxis = 0;
			for (int i = 1; i < 3; i++) if (ext[i] > ext[bodyAxis]) bodyAxis = i;

			// yuz submesh = en az ucgenli olan
			int faceSub = 0;
			int fewest = int.MaxValue;
			for (int i = 0; i < mesh.subMeshCount; i++) {
				int n = mesh.GetTriangles(i).Length / 3;
				if (n > 0 && n < fewest) { fewest = n; faceSub = i; }
			}

			int[] tri = mesh.GetTriangles(faceSub);
			Vector3[] nrm = mesh.normals;
			Vector3 centroid = Vector3.zero;
			Vector3 nAvg = Vector3.zero;
			for (int i = 0; i < tri.Length; i++) {
				centroid += (Vector3)mesh.vertices[tri[i]];
				if (nrm != null && nrm.Length == mesh.vertices.Length)
					nAvg += nrm[tri[i]];
			}
			centroid /= (float)tri.Length;
			nAvg /= (float)tri.Length;

			Vector3 body = (bodyAxis == 0) ? Vector3.right
			            : (bodyAxis == 1) ? Vector3.up : Vector3.forward;
			// yuz agirligi govde ekseninin hangi ucunda?
			float c = (bodyAxis == 0) ? centroid.x : (bodyAxis == 1) ? centroid.y : centroid.z;
			float mid = (lo[bodyAxis] + hi[bodyAxis]) * 0.5f;
			up = (c >= mid) ? body : -body;

			// ileri = yuz normallerinin ortalamasi, up'a diklestir
			// (Gram-Schmidt). up bu adimdan sonra ZATEN fwd'ye dik oldugu
			// icin uzerine BASKA bir dondurme UYGULANMAZ; Cross(up, fwd)
			// veya Cross(fwd, up) yazmak up'i 90 degce kaydirirdi
			// (boyle bir surum modeli BAS AYGIDA birakmis; duzeltildi).
			fwd = (nAvg - up * Vector3.Dot(nAvg, up));
			if (fwd.sqrMagnitude < 1e-8f) {
				fwd = (bodyAxis == 2) ? Vector3.right : Vector3.forward;
				fwd = (fwd - up * Vector3.Dot(fwd, up));
			}
			fwd = fwd.normalized;
			right = Vector3.Cross(up, fwd).normalized;

			src = "mesh (govde ekseni #" + bodyAxis + ", yuz submesh #" + faceSub
			    + ", " + fewest + " ucgen)";
			Log("yon kaynagi: " + src + "  yuz merkezi " + centroid.ToString("F3")
				+ "  ortalama normal " + nAvg.ToString("F3"));
		}
		catch (Exception e) {
			Log("mesh'ten yon okunamadi: " + e.Message
				+ " -> temel kural (up=+Z, fwd=+X)");
			up = Vector3.forward; fwd = Vector3.right;
			right = Vector3.Cross(up, fwd).normalized;
		}

		float det = right.x * (up.y * fwd.z - up.z * fwd.y)
				  - right.y * (up.x * fwd.z - up.z * fwd.x)
				  + right.z * (up.x * fwd.y - up.y * fwd.x);
		if (det < 0f) {
			fwd = -fwd;
			right = -right;
			det = -det;
		}

		Quaternion fix = QuatFromBasis(right, up, fwd);
		root.transform.localRotation = fix * root.transform.localRotation;

		Vector3 upAfter = fix * up;
		Vector3 fwdAfter = fix * fwd;
		Log("yon duzeltmesi (" + src + "): " + fix.ToString("F4")
			+ "  det=" + det.ToString("F3")
			+ "  up=" + up.ToString("F3") + " -> " + upAfter.ToString("F3")
			+ "  fwd=" + fwd.ToString("F3") + " -> " + fwdAfter.ToString("F3"));
		if (Vector3.Dot(upAfter, Vector3.up) < 0.999f)
			Log("  ! UYARI: up (0,1,0)'a cevrilemedi");
	}


	/// <summary>Kokun yerel konumunu kaydirir: SKINLENMIS mesh'in en alt noktasi
	/// y = 0 olur.
	///
	/// NEDEN: onarim sonrasi olcum (check: skinned box yukseklik(Y)=1.822,
	/// genislik(X)=1.352, derinlik(Z)=0.340) gosterdi ki modelin tumu
	/// Y = -1.826 .. -0.004 arasinda, yani pivot KAFAda. Uzak oyuncu modeli
	/// transform.position.y = 0 ile yere oturtuldugu icin ayaklar 1.83 m
	/// yerin altinda kalirdi; oyun ici karakter havada durur + fizik kayar.
	///
	/// Bag pozunda skinleme rijittir (M_i * bind_i = C sabit, bkz.
	/// diag_skin.py), dolayisiyla skinlenmis kutuyu hesaplamak icin
	/// renderer'a gerek yok:
	///     skinnedBox = C * meshBox,   C = boneWorld(rootBone) * bind(rootBone)
	/// Mesh kutusunun 8 kose noktasini C ile dondurup en dusuk Y'yi aliyoruz.
	/// </summary>
	static void GroundFeet(GameObject root, GameObject[] nodes,
			List<BoneRec> bones, int rootBoneIndex, Mesh mesh) {

		try {
			Matrix4x4 C = nodes[rootBoneIndex].transform.localToWorldMatrix
			            * bones[rootBoneIndex].bind;
			Bounds mb = VectorMinMax(mesh);
			float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
			float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
			float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
			for (int cx = 0; cx < 2; cx++)
				for (int cy = 0; cy < 2; cy++)
					for (int cz = 0; cz < 2; cz++) {
						Vector3 w = C.MultiplyPoint3x4(new Vector3(
							cx != 0 ? mb.max.x : mb.min.x,
							cy != 0 ? mb.max.y : mb.min.y,
							cz != 0 ? mb.max.z : mb.min.z));
						if (w.y < minY) minY = w.y;
						if (w.y > maxY) maxY = w.y;
						if (w.x < minX) minX = w.x;
						if (w.x > maxX) maxX = w.x;
						if (w.z < minZ) minZ = w.z;
						if (w.z > maxZ) maxZ = w.z;
					}

			Vector3 p = root.transform.position;
			p.y -= minY;
			root.transform.position = p;

			Log("ayak hizasi: kok Y " + (-minY).ToString("F4") + " m kaydirildi"
				+ "  kut(u) X " + (maxX - minX).ToString("F3")
				+ " Y " + (maxY - minY).ToString("F3")
				+ " Z " + (maxZ - minZ).ToString("F3"));
		}
		catch (Exception e) {
			Log("ayak hizasi hesaplanamadi: " + e.Message);
		}
	}

	/// <summary>Bir orthonormal bAZdan dondurum quaternion'i uretir.
	///
	/// Unity 5.0'da Matrix4x4.rotation YOK (5.1'de eklendi), bu yuzden
	/// Shepperd yontemiyle elle hesaplanir.
	///
	/// ONEMLI: matrisin SATIRLARI temel vektorlerdir (dogru tarafi hedef
	/// eksene tasiran dondurum R, R * right = +X, R * up = +Y, R * fwd = +Z
	/// olmalidir; bu kosul ancak satir = right/up/fwd iken saglanir).
	/// Sutunlari koymak matrisi transpoze eder ve up yerine right'i yukari
	/// cevirir; boyle bir surumde model X ekseninde yatmis durdu.</summary>
	static Quaternion QuatFromBasis(Vector3 right, Vector3 up, Vector3 fwd) {
		// satir 0 = right, satir 1 = up, satir 2 = fwd
		float m00 = right.x, m01 = right.y, m02 = right.z;
		float m10 = up.x,    m11 = up.y,    m12 = up.z;
		float m20 = fwd.x,   m21 = fwd.y,   m22 = fwd.z;
		float tr = m00 + m11 + m22;
		Quaternion q;
		if (tr > 0f) {
			float s = Mathf.Sqrt(tr + 1f) * 2f;
			q = new Quaternion((m21 - m12) / s, (m02 - m20) / s,
			                   (m10 - m01) / s, 0.25f * s);
		} else if (m00 > m11 && m00 > m22) {
			float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
			q = new Quaternion(0.25f * s, (m01 + m10) / s,
			                   (m02 + m20) / s, (m21 - m12) / s);
		} else if (m11 > m22) {
			float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
			q = new Quaternion((m01 + m10) / s, 0.25f * s,
			                   (m12 + m21) / s, (m02 - m20) / s);
		} else {
			float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
			q = new Quaternion((m02 + m20) / s, (m12 + m21) / s,
			                   0.25f * s, (m10 - m01) / s);
		}
		float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
		if (mag > 1e-6f) {
			q.x /= mag; q.y /= mag; q.z /= mag; q.w /= mag;
		} else {
			q = Quaternion.identity;
		}
		return q;
	}

	static Transform FindNode(GameObject[] nodes, string name) {
		for (int i = 0; i < nodes.Length; i++) {
			if (nodes[i] != null && nodes[i].name == name) return nodes[i].transform;
		}
		return null;
	}


	static Bounds VectorMinMax(Mesh m) {
		if (m == null || m.vertices == null || m.vertices.Length == 0) {
			return new Bounds(Vector3.zero, Vector3.zero);
		}
		Vector3[] v = m.vertices;
		Bounds b = new Bounds(v[0], Vector3.zero);
		for (int i = 1; i < v.Length; i++) b.Encapsulate(v[i]);
		return b;
	}

	/// <summary>Doku ice aktarma ayarlari. Kutupta tekrar/sarma olmasin diye
	/// Clamp + mipmaps kapali. Karakter atlasinda kenardan renk tasmasi gorunur.</summary>
	static void FixTextureImporters() {
		if (!Directory.Exists(TexDir)) return;
		string[] aps = Directory.GetFiles(TexDir, "*.png");
		Array.Sort(aps);
		int n = 0;
		for (int i = 0; i < aps.Length; i++) {
			string rel = "Assets" + aps[i].Substring(TexDir.Length - 1)
				.Replace('\\', '/');
			TextureImporter ti = AssetImporter.GetAtPath(rel) as TextureImporter;
			if (ti == null) continue;
			ti.wrapMode = TextureWrapMode.Clamp;
			ti.mipmapEnabled = false;
			ti.filterMode = FilterMode.Bilinear;
			ti.anisoLevel = 1;
			// 1024x1024 gomlekleri 512'ye indir: 1.7 m'lik bir karakter
			// icin 512 fazli; bundle 47 doku tasiyor ve boyut dogrudan
			// DLL'in icine gomuluyor.
			ti.maxTextureSize = 512;
			// textureType'a dokunma: Unity 5.0'da TextureImporterType enumunda
			// 'Default' adi yok; PNG varsayilani zaten istedigimiz ayar.
			ti.SaveAndReimport();
			n++;
		}
		Log("doku ice aktarma ayarlari: " + n + " adet (Clamp, mipmaps yok, max 512)");
	}

	// ------------------------------------------------------------------
	// legacy transform egri'lerini bilesenlere boler
	// ------------------------------------------------------------------
	static void AddCurve(AnimationClip clip, CurveRec cv) {
		int comp = cv.Comp;
		int kc = cv.times.Length;
		string prop;
		switch (cv.kind) {
			case KIND_POS:   prop = "localPosition";  break;
			case KIND_ROT:   prop = "localRotation"; break;
			default:         prop = "localScale";    break;
		}
		char suffix = (comp == 4) ? 'w' : 'z';
		for (int c = 0; c < comp; c++) {
			Keyframe[] keys = new Keyframe[kc];
			for (int k = 0; k < kc; k++) {
				keys[k] = new Keyframe(cv.times[k], cv.values[k * comp + c]);
			}
			AnimationCurve curve = new AnimationCurve(keys);
			string full = prop + "." + AxisLetter(c);
			clip.SetCurve(cv.path, typeof(Transform), full, curve);
		}
	}

	static string AxisLetter(int i) {
		if (i == 0) return "x";
		if (i == 1) return "y";
		if (i == 2) return "z";
		return "w";
	}

	// ------------------------------------------------------------------
	static Shader FindShader() {
		string[] names = {
			"Legacy Shaders/Diffuse",
			"Mobile/Diffuse",
			"Diffuse",
			"Legacy Shaders/Bumped Diffuse",
			"Mobile/Bumped Diffuse",
		};
		for (int i = 0; i < names.Length; i++) {
			Shader s = Shader.Find(names[i]);
			if (s != null) { Log("shader: " + names[i]); return s; }
		}
		throw new Exception("uygun shader bulunamadi");
	}

	static Texture2D LoadPngToAsset(string assetPath) {
		string disk = assetPath.Replace('/', '\\');
		if (!File.Exists(disk)) return null;
		byte[] png = File.ReadAllBytes(disk);
		Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
		t.LoadImage(png);          // Unity 5.0: Texture2D.LoadImage
		t.name = Path.GetFileNameWithoutExtension(disk);
		AssetDatabase.ImportAsset(assetPath);
		return AssetDatabase.LoadAssetAtPath(
			assetPath, typeof(Texture2D)) as Texture2D;
	}

	static void EnsureFolder(string path) {
		if (AssetDatabase.IsValidFolder(path)) return;
		string parent = Path.GetDirectoryName(path).Replace('\\', '/');
		string leaf   = Path.GetFileName(path);
		AssetDatabase.CreateFolder(parent, leaf);
	}

	// ---------------- ikili okuma ----------------
	static bool EncodingCheck(byte[] b, int o, string s) {
		if (o + s.Length > b.Length) return false;
		for (int i = 0; i < s.Length; i++) {
			if ((char)b[o + i] != s[i]) return false;
		}
		return true;
	}

	static int ReadI32(byte[] b, ref int o) {
		int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
		o += 4;
		return v;
	}

	static float F32(byte[] b, ref int o) {
		// Unity 5.0 / .NET: BitConverter little-endian on Windows
		float v = BitConverter.ToSingle(b, o);
		o += 4;
		return v;
	}

	static string ReadStr(byte[] b, ref int o) {
		int len = 0, shift = 0, x;
		do {
			x = b[o++];
			len |= (x & 0x7F) << shift;
			shift += 7;
		} while ((x & 0x80) != 0);
		string s = System.Text.Encoding.UTF8.GetString(b, o, len);
		o += len;
		return s;
	}

	static void Log(string s) {
		Debug.Log("[MPNPC] " + s);
		Console.WriteLine("[MPNPC] " + s);
	}
}