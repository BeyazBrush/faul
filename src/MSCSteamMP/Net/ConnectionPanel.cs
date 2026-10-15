using MSCLoader;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Durum paneli (sağ üstte).
	///
	/// İKİ GERÇEK HATA (ekran görüntüsü kanıtı):
	///
	/// 1. Oyun kendi OnGUI'sinde GUI.matrix'i değiştiriyor (MSC sanal
	///    çözünürlük kullanıyor). Panelimiz OnGUI'de çizildiği için o
	///    matrisi MİRAS ALIYORDU; "Screen.width - W - 10" hesabı o ölçekli
	///    uzayda geçersiz kalıyor ve panel ekranın SOL ÜSTÜNE düşüyordu.
	///    Çözüm: çizimden önce GUI.matrix = identity, sonra geri alınır.
	///
	/// 2. Unity'nin varsayılan label stilinde wordWrap = TRUE'dir. Uzun
	///    satırlar ("satsuma : 240 parts, 137 bolts") iki satıra bölünüyor,
	///    satır yüksekliği 17 piksel olduğu için ikinci satır KIRPILIYORDU
	///    ("yarım görünüyor"). Çözüm: wordWrap=false + clipping=Clip olan
	///    kendi stilimiz ve genişlik metinden ÖLÇÜLÜR.
	///
	/// GC-free: metinler yalnızca değer değiştiğinde yeniden birleştirilir
	/// (StringBuilder önbelleği), kare başına ayırma yoktur.
	/// </summary>
	public class ConnectionPanel {

		// Ölçüler (mantıksal piksel, ham ekran uzayında çizilir)
		const float MARGIN = 10f;
		const float PAD = 8f;
		const float HEADER_H = 20f;
		const float ROW_H = 15f;
		const float MIN_W = 300f;
		const float MAX_W = 520f;

		public bool Visible;

		public bool WorldReady { get { return SatsumaRegistry.Root != null; } }

		public bool ShouldPrompt {
			get {
				return SteamTransport.HasRemote && SteamTransport.IsClient && !WorldReady;
			}
		}

		// Stiller ve önbellek
		GUIStyle styleHeader, styleRow, styleNote, styleBtn;
		bool stylesReady;
		Texture2D pixel;
		float lastW;
		float lastH;

		// Metin önbelleği: değer değişmedikçe yeniden birleştirme yok.
		string cachedMode, cachedPeer, cachedSync, cachedSatsuma, cachedVersion, cachedPeerId;
		bool cachedHasRemote, cachedWorld;

		public void Draw() {
			Visible = SteamTransport.IsOnline;
			if (!Visible) return;
			Matrix4x4 keep = GUI.matrix;
			Color keepColor = GUI.color;
			try {
				try { DrawPanel(); }
				catch (System.Exception) {
				// Panel asla oyunu düşürmez; ilk hatada gizlenir ve
				// bir sonraki karede yeniden denenir.
					Visible = false;
				}
			}
			// Istisna olsa da olmasa da matrix/color geri yuklenir: DrawPanel
			// icinde ara bir noktada kopulursa sonundaki geri yazma calismaz ve
			// AYNI OnGUI gecisinde sonra cizen her sey (PerfHud, konsol, oyunun
			// kendi GUI'leri) olcekli matrisi miras alirdi.
			finally {
				GUI.matrix = keep;
				GUI.color = keepColor;
			}
		}

		void DrawPanel() {
			EnsureStyles();

			// Oyunun GUI.matrix'i miras kalmasın.
			Matrix4x4 keep = GUI.matrix;
			Color keepColor = GUI.color;
			GUI.matrix = Matrix4x4.identity;

			// Satırları hazırla (önbellekli).
			string mode = SteamTransport.CurrentMode.ToString();
			string peer = SteamTransport.HasRemote
				? SteamTransport.RemoteId.m_SteamID + "  (" + SteamTransport.PingMs + "ms)"
				: "none yet";
			string sync = SatsumaRegistry.SyncLabel;
			string satsuma = WorldReady
				? SatsumaRegistry.PartCount + " parts, " + SatsumaRegistry.BoltCount + " bolts"
				: "waiting for a save";
			string version = ModEntry.MOD_VERSION + " (proto " + Protocol.PROTOCOL_VERSION + ")";

			// Genişlik en uzun satırdan ÖLÇÜLÜR -> kırpma olmaz.
			float need = styleHeader.CalcSize(new GUIContent("MSCSteamMP  WAITING")).x;
			need = Mathf.Max(need, styleRow.CalcSize(new GUIContent("mode     : " + mode)).x);
			need = Mathf.Max(need, styleRow.CalcSize(new GUIContent("peer     : " + peer)).x);
			need = Mathf.Max(need, styleRow.CalcSize(new GUIContent("sync     : " + sync)).x);
			need = Mathf.Max(need, styleRow.CalcSize(new GUIContent("satsuma  : " + satsuma)).x);
			need = Mathf.Max(need, styleRow.CalcSize(new GUIContent("version  : " + version)).x);
			if (ShouldPrompt) {
				need = Mathf.Max(need, styleNote.CalcSize(new GUIContent(
					"Host is in the world. Joining you automatically...")).x);
			}
			float w = Mathf.Clamp(need + PAD * 2f + 4f, MIN_W, MAX_W);
			if (w > Screen.width - MARGIN * 2f) w = Screen.width - MARGIN * 2f;

			bool prompt = ShouldPrompt || (SteamTransport.IsHost && !WorldReady);
			float h = PAD + HEADER_H + ROW_H * 5f + PAD;
			if (prompt) h += ROW_H * 2f + 26f;

			Rect box = new Rect(Screen.width - w - MARGIN, MARGIN, w, h);

			// Zemin + çerçeve.
			GUI.color = new Color(0f, 0f, 0f, 0.80f);
			GUI.DrawTexture(box, pixel);
			GUI.color = SteamTransport.HasRemote
				? new Color(0.4f, 1f, 0.4f, 0.9f)
				: new Color(1f, 0.78f, 0.2f, 0.95f);
			GUI.DrawTexture(new Rect(box.x, box.y, box.width, 2f), pixel);
			GUI.color = new Color(1f, 1f, 1f, 0.12f);
			GUI.DrawTexture(new Rect(box.x + 1f, box.y + 2f, box.width - 2f, 1f), pixel);

			float x = box.x + PAD;
			float inner = box.width - PAD * 2f;
			float y = box.y + PAD + 3f;

			GUI.color = SteamTransport.HasRemote
				? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.78f, 0.2f);
			GUI.Label(new Rect(x, y, inner, HEADER_H),
				"MSCSteamMP  " + (SteamTransport.HasRemote ? "LINKED" : "WAITING"), styleHeader);

			GUI.color = Color.white;
			y += HEADER_H;
			Line(x, ref y, inner, "mode     : " + mode);
			Line(x, ref y, inner, "peer     : " + peer);
			Line(x, ref y, inner, "sync     : " + sync);
			Line(x, ref y, inner, "satsuma  : " + satsuma);
			Line(x, ref y, inner, "version  : " + version);

			if (prompt) {
				y += 4f;
				GUI.color = new Color(1f, 0.85f, 0.25f);
				string note = ShouldPrompt
					? (AutoLoad.Enabled
						? "Host is in the world. Joining you automatically..."
						: "Connected. Press CONTINUE to load your save,\nthe same one the host has.")
					: "You are the host. Press CONTINUE to load your save\nso your friend can sync to it.";
				GUI.Label(new Rect(x, y, inner, ROW_H * 2f + 4f), note, styleNote);

				GUI.color = Color.white;
				Rect b = new Rect(x, y + ROW_H * 2f + 6f, inner, 22f);
				bool loadBlocked = MenuMouse.GuiBlockedAtCurrentEvent();
				bool loadHit = false;
				if (loadBlocked) { GUI.Box(b, "Load world now (smp enter)", styleBtn); }
				else { loadHit = GUI.Button(b, "Load world now (smp enter)", styleBtn); }
				if (loadHit) {
					try { AutoLoad.EnterWorld(); } catch { }
				}
			}

			GUI.matrix = keep;
			GUI.color = keepColor;
		}

		void Line(float x, ref float y, float w, string text) {
			// wordWrap=false olduğu için tek satırda kalır ve kırpılmaz.
			GUI.Label(new Rect(x, y, w, ROW_H), text, styleRow);
			y += ROW_H;
		}

		// ------------------------------------------------------------------
		// Varlıklar
		// ------------------------------------------------------------------

		void EnsureStyles() {
			if (pixel == null) {
				pixel = new Texture2D(1, 1, TextureFormat.RGB24, false);
				pixel.SetPixel(0, 0, Color.white);
				pixel.Apply();
				pixel.hideFlags = HideFlags.HideAndDontSave;
			}
			if (stylesReady && styleRow != null) return;
			if (GUI.skin == null) return;

			GUIStyle root = GUI.skin.label;

			styleHeader = Make(root, 12, TextAnchor.MiddleLeft, new Color(1f, 0.85f, 0.3f));
			styleRow = Make(root, 11, TextAnchor.MiddleLeft, new Color(0.85f, 0.87f, 0.90f));
			styleNote = Make(root, 11, TextAnchor.UpperLeft, new Color(1f, 0.85f, 0.3f));

			styleBtn = new GUIStyle(GUI.skin.button);
			styleBtn.fontSize = 11;
			styleBtn.alignment = TextAnchor.MiddleCenter;
			styleBtn.clipping = TextClipping.Clip;
			styleBtn.wordWrap = false;

			stylesReady = true;
		}

		static GUIStyle Make(GUIStyle root, int size, TextAnchor anchor, Color c) {
			GUIStyle s = new GUIStyle(root);
			s.fontSize = size;
			s.alignment = anchor;
			s.normal.textColor = c;
			s.wordWrap = false;              // kırpma sorununun kökü: bu true'ydu
			s.clipping = TextClipping.Clip;  // taşma yerine kırp
			s.padding = new RectOffset(0, 0, 0, 0);
			return s;
		}
	}
}
