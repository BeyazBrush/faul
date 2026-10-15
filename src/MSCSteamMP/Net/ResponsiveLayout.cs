using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// Cozunurlukten bagimsiz (responsive) menu yerlesimi.
	///
	/// Kural: hicbir Rect sabit piksel degildir. Her sey Screen.width /
	/// Screen.height oraniyla, ekranin merkezine gore dinamik hesaplanir.
	/// Kucuk pencerede tasma, buyuk ekranda kucuk kalma olmaz.
	///
	/// Kullanim: MenuUi'nin sanal 1280x720 + GUI.matrix olcegi korunur
	/// (oyunun kendi yontemi), ancak buton/panel Rect'leri burada uretilir
	/// ve her cozunurlukte ortalanmis kalir. Matrix kullanilmayan yerlerde
	/// (ham ekran uzayi) dogrudan Screen oraniyla cagrilir.
	///
	/// Unity 5 / .NET 3.5 uyumlu. Hata ayiklama: SmpLog ile olcu basilir.
	/// </summary>
	public static class ResponsiveLayout {

		/// <summary>Ekranin merkezine ortalanmis panel.</summary>
		public static Rect CenteredPanel(float widthFrac, float heightFrac,
			float maxW, float maxH, float minMargin) {
			float sw = Screen.width <= 0 ? 1280f : Screen.width;
			float sh = Screen.height <= 0 ? 720f : Screen.height;
			float w = sw * widthFrac;
			float h = sh * heightFrac;
			if (w > maxW) w = maxW;
			if (h > maxH) h = maxH;
			if (w > sw - minMargin * 2f) w = sw - minMargin * 2f;
			if (h > sh - minMargin * 2f) h = sh - minMargin * 2f;
			if (w < 1f) w = 1f;
			if (h < 1f) h = 1f;
			return new Rect((sw - w) * 0.5f, (sh - h) * 0.5f, w, h);
		}

		/// <summary>
		/// Panel icinde ortalanmis buton sirasi: n buton yan yana, esit genislik.
		/// Ornek: Play / Open Lobby / Join Lobby.
		/// </summary>
		public static Rect[] RowButtons(Rect panel, int count, float buttonH,
			float pad, float gap) {
			Rect[] rects = new Rect[Mathf.Max(1, count)];
			if (count <= 0) return rects;
			float inner = panel.width - pad * 2f;
			float bw = (inner - gap * (count - 1)) / count;
			float y = panel.y + panel.height * 0.5f - buttonH * 0.5f;
			// Varsayilan: panelin dikey ortasi degil, cagiran y'yi ezebilir.
			for (int i = 0; i < count; i++) {
				rects[i] = new Rect(panel.x + pad + i * (bw + gap), y, bw, buttonH);
			}
			return rects;
		}

		/// <summary>
		/// Ana menu liste satirlari: ekranin merkezine oranli, her cozunurlukte ayni.
		/// Donus: [0]=Play, [1]=Open Lobby, [2]=Join Lobby (istenen 3 buton).
		/// </summary>
		public static Rect[] MainMenuButtons(float rowH, float rowGap, float rowWidthFrac, float maxRowW) {
			float sw = Screen.width <= 0 ? 1280f : Screen.width;
			float sh = Screen.height <= 0 ? 720f : Screen.height;
			float w = sw * rowWidthFrac;
			if (w > maxRowW) w = maxRowW;
			if (w > sw * 0.9f) w = sw * 0.9f;
			float x = (sw - w) * 0.5f;
			float totalH = rowH * 3f + rowGap * 2f;
			float y0 = (sh - totalH) * 0.5f;
			Rect[] r = new Rect[3];
			for (int i = 0; i < 3; i++) {
				r[i] = new Rect(x, y0 + i * (rowH + rowGap), w, rowH);
			}
			return r;
		}

		/// <summary>
		/// Verilen Rect'i ekran icine kelepceler (tasma korumasi). Log basar.
		/// </summary>
		public static Rect ClampToScreen(Rect r, string what) {
			float sw = Screen.width;
			float sh = Screen.height;
			if (sw <= 0f || sh <= 0f) return r;
			float x = r.x, y = r.y, w = r.width, h = r.height;
			if (w > sw) { w = sw; x = 0f; }
			if (h > sh) { h = sh; y = 0f; }
			if (x < 0f) x = 0f;
			if (y < 0f) y = 0f;
			if (x + w > sw) x = sw - w;
			if (y + h > sh) y = sh - h;
			if (x != r.x || y != r.y || w != r.width || h != r.height) {
				SmpLog.Warning("menu clamp: '" + what + "' " + r.ToString()
					+ " -> (" + x.ToString("F0") + "," + y.ToString("F0")
					+ "," + w.ToString("F0") + "x" + h.ToString("F0")
					+ ") on " + sw.ToString("F0") + "x" + sh.ToString("F0"));
			}
			return new Rect(x, y, w, h);
		}

		/// <summary>
		/// Olcekten bagimsiz yazi boyutu: 720p'de 1.0, 1080p'de ~1.5.
		/// GUI.matrix kullanan MenuUi ile ayni oran.
		/// </summary>
		public static float Scale() {
			float sw = Screen.width <= 0 ? 1280f : Screen.width;
			float sh = Screen.height <= 0 ? 720f : Screen.height;
			float s = Mathf.Min(sw / 1280f, sh / 720f);
			if (s < 0.5f) s = 0.5f;
			if (s > 3f) s = 3f;
			return s;
		}
	}
}
