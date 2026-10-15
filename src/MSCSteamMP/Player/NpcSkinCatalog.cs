// ---------------------------------------------------------------------------
// NpcSkinCatalog.cs  -  OTOMATIK URETILIR, ELLE DUZENLEMEYIN.
//
// Uretici  : tools\gen_skin_catalog.py
// Kaynak    : tools\skin_roster.py -> extracted_npc\roster.json
//
// Oyunun kendi NPC kadrosu.
//
// GORUNUM = uc doku + GOVDE TIPI.
// 'TUM NPC'ler ayni mesh'i paylasir' onceki varsayimi YANLISTI.
// Oyunda "bodymesh" adi altinda birden fazla mesh var ve NPC'ler
// farkli olanlari kullaniyor. level2'de olculdu (tools\gen_skin_mesh_map.py):
//
//   bodymesh      1712 vertex   115 SMR
//   bodymesh      1709 vertex    35 SMR
//   bodymesh      1714 vertex    14 SMR
//   bodymesh      1697 vertex     8 SMR   (ince / kadin)
//   bodymesh_kid  1714 vertex     5 SMR
//   bodymesh      1789 vertex     5 SMR   (uzun)
//
// 70 skin'in 54'u 1712-vertex govdeyi kullaniyordu ama biz yalnizca
// 1709-lu varyanti cikarmistik. Sonuc: yasli yuz + kadin yaka gomlek
// + genc erkek bedeni ayni modelde birlesiyor (kullanicinin ekran
// goruntusundeki 'kiyafetler yanlis giydirilmis' tablosu).
//
// Bu dosya tools\gen_skin_catalog.py ile URETILIR. govde vertex sayisi
// tools\gen_skin_mesh_map.py ciktisi olan skin_mesh_map.json'dan gelir.
// ---------------------------------------------------------------------------

namespace SteamMP {

	/// <summary>Bir NPC gorunumu: uc doku + govde tipi.</summary>
	public struct NpcSkin {
		/// <summary>Menu ve logda gorunen ad (oyundaki NPC adi).</summary>
		public string name;
		/// <summary>Gomlek dokusu (atlas adi, .png'siz).</summary>
		public string shirt;
		/// <summary>Pantolon dokusu.</summary>
		public string pants;
		/// <summary>Yuz dokusu.</summary>
		public string face;
		/// <summary>Govde mesh'inin vertex sayisi. Bundle'daki govdeyle
		/// eslesmezse kiyafetler yanlis bedene oturur (yaka/cap uyumsuzlugu,
		/// kollarin/kadin govdesinin erkek omuzuna girmesi).</summary>
		/// <remarks>0 = katalogda cozulemedi, varsayilan govde kullanilir.</remarks>
		public int bodyVertices;

		public NpcSkin(string n, string s, string p, string f, int bv) {
			name = n; shirt = s; pants = p; face = f; bodyVertices = bv;
		}

		public override string ToString() {
			return name + " (" + shirt + ", " + pants + ", " + face
				+ (bodyVertices > 0 ? ", govde " + bodyVertices : "") + ")";
		}
	}

	/// <summary>Oyunun NPC kadrosu ve skin'lerin kullandigi dokular.</summary>
	public static class NpcSkinCatalog {

		/// <summary>Toplam skin sayisi.</summary>
		public const int Count = 70;

		/// <summary>Bundle'daki varsayilan govdenin vertex sayisi.</summary>
		public const int DEFAULT_BODY = 1712;

		public static readonly NpcSkin[] All = {
			new NpcSkin("KiljuBuyer", "char_shirt01", "char_pants01", "char_face01", 1712),
			new NpcSkin("VICTRO", "char_shirt01", "char_pants02", "char_face01", 1712),
			new NpcSkin("Uncle", "char_shirt01", "char_pants02", "char_face09", 1712),
			new NpcSkin("AIboat", "char_shirt01", "char_pants02", "char_face08", 1712),
			new NpcSkin("PRESIDENT", "char_shirt01", "char_pants07", "char_face07", 1714),
			new NpcSkin("Granny", "char_shirt01", "fabric6", "char_face04_woman", 1697),
			new NpcSkin("Dancer", "char_shirt01", "fabric23", "char_face05", 1697),
			new NpcSkin("Teimo", "char_shirt02", "char_pants02", "char_face01", 1712),
			new NpcSkin("AIboat 2", "char_shirt02", "char_pants03", "char_face01", 1712),
			new NpcSkin("NPC", "char_shirt03", "char_pants02", "char_face02", 1712),
			new NpcSkin("Unto", "char_shirt03", "char_pants02", "char_face04_woman", 1712),
			new NpcSkin("Uncle 2", "char_shirt03", "char_pants02", "char_face09", 1712),
			new NpcSkin("Berryman", "char_shirt03", "char_pants03", "char_face03", 1712),
			new NpcSkin("VICTRO 2", "char_shirt04", "char_pants01", "char_face02", 1709),
			new NpcSkin("ShitMan", "char_shirt04", "char_pants03", "char_face01", 1714),
			new NpcSkin("Kristian", "char_shirt04", "char_pants03", "char_face09", 1709),
			new NpcSkin("Sender", "char_shirt04", "char_pants06", "char_face01", 1712),
			new NpcSkin("Drunk", "char_shirt04", "char_pants06", "char_face08", 1712),
			new NpcSkin("Dancer 2", "char_shirt04", "char_pants07", "char_face01", 1712),
			new NpcSkin("Man", "char_shirt04", "char_pants07", "char_face04", 1712),
			new NpcSkin("Move", "char_shirt04", "char_pants07", "char_face08", 1712),
			new NpcSkin("ShitMan 2", "char_shirt05", "char_pants01", "char_face01", 1712),
			new NpcSkin("Drunk 2", "char_shirt05", "char_pants01", "char_face03", 1709),
			new NpcSkin("MarkkuTheatre", "char_shirt05", "char_pants01", "char_face04_woman", 1712),
			new NpcSkin("VICTRO 3", "char_shirt05", "char_pants02", "char_face01", 1712),
			new NpcSkin("Work", "char_shirt05", "char_pants02", "char_face02", 1712),
			new NpcSkin("LAMORE", "char_shirt05", "char_pants06", "char_face08", 1712),
			new NpcSkin("ShitMan 3", "char_shirt06", "char_pants02", "char_face01", 1712),
			new NpcSkin("Dancer 3", "char_shirt06", "char_pants02", "char_face02", 1712),
			new NpcSkin("Julli", "char_shirt06", "char_pants03", "char_face03", 1714),
			new NpcSkin("Neighbour", "char_shirt07", "char_pants01", "char_face02", 1709),
			new NpcSkin("KID", "char_shirt07", "char_pants03", "char_face05", 1714),
			new NpcSkin("KID 2", "char_shirt07", "char_pants03", "char_face07", 1714),
			new NpcSkin("SpectatorA", "char_shirt08", "char_pants02", "char_face02", 1712),
			new NpcSkin("FITTAN", "char_shirt08", "char_pants02", "char_face03", 1714),
			new NpcSkin("KYLAJANI", "char_shirt09", "char_pants03", "char_face04", 1712),
			new NpcSkin("LAMORE 2", "char_shirt09", "char_pants03", "char_face07", 1714),
			new NpcSkin("CrashEvent", "char_shirt10", "char_pants03", "char_face05", 1709),
			new NpcSkin("VenttiPig", "char_shirt11", "char_pants04", "char_face06", 1627),
			new NpcSkin("POLSA", "char_shirt12", "char_pants01", "char_face06", 1709),
			new NpcSkin("Shitman", "char_shirt12", "char_pants02", "char_face06", 1712),
			new NpcSkin("Synthist", "char_shirt12", "char_pants02", "char_face07", 1712),
			new NpcSkin("Dancer 4", "char_shirt12", "char_pants03", "char_face03", 1712),
			new NpcSkin("Bassist", "char_shirt12", "char_pants06", "char_face03", 1714),
			new NpcSkin("Drummer", "char_shirt12", "char_pants06", "char_face06", 1709),
			new NpcSkin("CrashEvent 2", "char_shirt13", "char_pants05", "char_face04", 1714),
			new NpcSkin("KID 3", "char_shirt13", "char_pants05", "char_face07", 1714),
			new NpcSkin("Man 2", "char_shirt14", "char_pants03", "char_face09", 1712),
			new NpcSkin("Guard", "char_shirt14", "char_pants06", "char_face08", 1712),
			new NpcSkin("SVOBODA", "char_shirt15", "char_pants05", "char_face09", 1709),
			new NpcSkin("Alpo", "char_shirt15", "char_pants06", "char_face09", 1714),
			new NpcSkin("SuskiHiker", "char_shirt16", "char_pants06", "char_face11", 1789),
			new NpcSkin("KUSKI", "char_shirt17", "char_pants03", "char_face07", 1709),
			new NpcSkin("LAMORE 3", "char_shirt18", "char_pants01", "char_face06", 1712),
			new NpcSkin("Walker", "char_shirt18", "char_pants02", "char_face08", 1709),
			new NpcSkin("Dancer 5", "char_shirt18", "char_pants06", "char_face01", 1712),
			new NpcSkin("Rauno", "char_shirt18", "char_pants06", "char_face08", 1712),
			new NpcSkin("Reijo", "char_shirt18", "char_pants07", "char_face09", 1712),
			new NpcSkin("Fighter", "char_shirt19", "char_pants06", "char_face03", 1712),
			new NpcSkin("Latanen", "char_shirt20", "char_pants06", "char_face03", 1712),
			new NpcSkin("MARA", "char_shirt22", "char_pants08", "char_face04_woman", 1712),
			new NpcSkin("CopHome", "cop_shirt", "cop_pants01", "char_face02", 1712),
			new NpcSkin("CopHome 2", "cop_shirt", "cop_pants01", "char_face03", 1712),
			new NpcSkin("CopAlc", "cop_shirt", "cop_pants01", "char_face06", 1709),
			new NpcSkin("CopRadar", "cop_shirt", "cop_pants01", "char_face09", 1709),
			new NpcSkin("CopTeimo", "cop_shirt", "cop_pants01", "char_face08", 1712),
			new NpcSkin("CopTeimo 2", "cop_shirt2", "char_pants06", "char_face04_woman", 1709),
			new NpcSkin("CopPassenger", "cop_shirt2", "cop_pants01", "char_face09", 1709),
			new NpcSkin("Dragcar", "rally_shirt", "rally_pants01", "char_face09", 1709),
			new NpcSkin("RALLYCAR", "rally_shirt", "rally_pants01", "char_face08", 1709),
		};

		/// <summary>Bundle'a gomulen doku adlari. 47 adet.</summary>
		/// <remarks>Bundle yolu: assets/mpnctextures/&lt;ad&gt;.png (kucuk harf).</remarks>
		public static readonly string[] Textures = {
			"char_face01",
			"char_face02",
			"char_face03",
			"char_face04",
			"char_face04_woman",
			"char_face05",
			"char_face06",
			"char_face07",
			"char_face08",
			"char_face09",
			"char_face11",
			"char_pants01",
			"char_pants02",
			"char_pants03",
			"char_pants04",
			"char_pants05",
			"char_pants06",
			"char_pants07",
			"char_pants08",
			"char_shirt01",
			"char_shirt02",
			"char_shirt03",
			"char_shirt04",
			"char_shirt05",
			"char_shirt06",
			"char_shirt07",
			"char_shirt08",
			"char_shirt09",
			"char_shirt10",
			"char_shirt11",
			"char_shirt12",
			"char_shirt13",
			"char_shirt14",
			"char_shirt15",
			"char_shirt16",
			"char_shirt17",
			"char_shirt18",
			"char_shirt19",
			"char_shirt20",
			"char_shirt22",
			"cop_pants01",
			"cop_shirt",
			"cop_shirt2",
			"fabric23",
			"fabric6",
			"rally_pants01",
			"rally_shirt",
		};
	}
}
