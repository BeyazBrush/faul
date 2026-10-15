using System;
using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// ANA MENÜDEKİ GÖRÜNÜM (SKIN) SEÇİMİ.
	///
	/// Bu dosya MenuUi'nin partial yarısıdır: MenuUi.cs devasa bir dosya ve
	/// oraya 400 satır eklemek onu okunmaz hale getirirdi. Buradaki kod
	/// MenuUi'nin kendi cizim yardimcilarini (Fill/Frame/Label/V/Row)
	/// kullanir, boylece gorsel dil ayni kalir.
	///
	/// YERLESIM
	/// -------
	///   +-------------------------------------------------+
	///   |  3B ONIZLEME     |  LISTE (kaydirilabilir)       |
	///   |  donen model     |  > Teimo                      |
	///   |                  |    Uncle                      |
	///   |  Teimo           |  ... 70 gorunum              |
	///   |  shirt04/pants01 |                               |
	///   |  [12/70]         |                               |
	///   +-------------------------------------------------+
	///   [ ESC ] GERI                      [ ENTER ] UYGULA-KAPAT
	///
	/// GİRDİ
	/// ----
	/// Fare ile liste satırlarına tıkla. Tekerlek / PageUp-PageDown kaydırır.
	/// ESC geri döner. Ana menüdeki SKIN satırı bu ekranı açar.
	///
	/// PERFORMANS
	/// -----------
	/// Önizleme kamerası yalnızca bu ekran açıkken Render() alır (256x384
	/// RenderTexture, bir SkinnedMeshRenderer). Repaint dışındaki OnGUI
	/// çağrılarında render tekrar edilmez.
	/// </summary>
	public partial class MenuUi {

		// --- düzen (mantıksal piksel) -----------------------------------
		const float SK_PAD = MenuTheme.SK_PAD;
		const float SK_TITLE_H = MenuTheme.SK_TITLE_H;
		const float SK_PANEL_W = MenuTheme.SK_PANEL_W;      // ortalanmış panel genişliği
		const float SK_PANEL_H = MenuTheme.SK_PANEL_H;
		const float SK_PREVIEW_W = MenuTheme.SK_PREVIEW_W;
		const float SK_INFO_H = MenuTheme.SK_INFO_H;       // ad + 4 satır (gömlek/pantolon/yüz/gövde) + sayaç
		const float SK_ROW_H = MenuTheme.SK_ROW_H;
		const float SK_ROW_GAP = MenuTheme.SK_ROW_GAP;
		const float SK_FOOT_H = MenuTheme.SK_FOOT_H;
		// Govde secici seridi: listenin altinda, govde basina kucuk dugme.
		const float SK_BODY_H = MenuTheme.SK_BODY_H;
		const float SK_BODY_GAP = MenuTheme.SK_BODY_GAP;

		// --- durum ------------------------------------------------------
		SkinPreview preview;
		int listTop;             // listenin en ustteki gorunum indeksi
		int skinHover = -1;
		int bodyHover = -1;
		bool skinsOpen;
		bool skinRendered;       // bu kare icin onizleme cizildi mi

		static readonly GUIContent SkinTitle
			= new GUIContent("GÖRÜNÜM");
		// SkinSub (başlık altı bilgi satırı) KALDIRILDI — kullanıcı isteği.
		// Alan da boş bırakılmıyor: başlık doğrudan panele bağlanıyor.

		// ------------------------------------------------------------------

		/// <summary>Menü açılırken: önizlemeyi kur, listeyi seçime kaydır.</summary>
		void SkinsEnter() {
			skinsOpen = true;
			skinRendered = false;

			if (preview == null) preview = new SkinPreview();
			preview.Ensure();
			if (preview.EnsureModel()) preview.RefreshSkin();
			// Teşhis dökümü kaldırıldı: model artık doğru (bundle'daki
			// 1712 gövde + doğru ölçüm + localBounds). Yeniden gerekirse
			// SkinPreview.DumpDiagnostics / DumpAnim çağrılabilir.

			int sel = PlayerSkin.Selected;
			if (sel >= 0) {
				// Secili gorunum gorunur olsun
				int rows = SkinVisibleRows();
				if (rows > 0 && sel < rows) listTop = sel;
				else if (rows > 0) listTop = Mathf.Clamp(sel - rows / 2, 0, Count - rows);
				if (listTop < 0) listTop = 0;
			}
		}

		/// <summary>Her kare: modeli dondur; ekran kapanmissa onizlemeyi birak.
		///
		/// SIZINTI KORUMASI: menüden ESC ile cikilirsa SkinsExit() cagirilir,
		/// ama gorunum bazen KENDISI degisir (dunya yuklenince HideLoading,
		/// Steam hatasiinda ShowOnline). O yollarda SkinsExit() hic
		/// cagrilmaz ve onizleme rig'i sahnede kalirdi: y=-5000'de gorunmez
		/// ama kamera, isik ve model nesneleri birikir. Bu yuzden kapali
		/// durumda da temizlik yapilir.</summary>
		float wheelAcc;

		void SkinsTick(float dt) {
			if (preview == null) return;
			if (target == View.Skins && fade > 0.002f) {
				preview.Tick(dt);
				// Tekerlek BURADA okunur (Update yolu): OnGUI ScrollWheel
				// olayina guvenilmiyor (baska GUI tuketebiliyor, Layout/
				// Repaint cift cagrida iki kez isliyordu). TakeScroll tuketir,
				// o yuzden OnGUI dali artik ikinci kez islemez.
				float wd = MenuMouse.TakeScroll();
				if (Mathf.Abs(wd) > 0.0001f) ScrollBy(wd);
			}
			else if (skinsOpen && target != View.Skins) {
				SkinsExit();
			}
		}

		/// <summary>Toplam gorunum sayisi (katalog bos olabilir).</summary>
		int Count { get { return PlayerSkin.Count; } }

		/// <summary>Liste alanına sığan satır sayısı (panel yüksekliğinden).
		/// Klavye kaydırma bu değeri kullanır; çizimde satır yüksekliği
		/// sabittir ve satır sayısı buradan gelir.</summary>
		int SkinVisibleRows() {
			if (Count <= 0) return 0;
			float h = SK_PANEL_H - 20f;
			int rows = (int)(h / (SK_ROW_H + SK_ROW_GAP));
			return Mathf.Clamp(rows, 1, Count);
		}

		// ------------------------------------------------------------------

		void DrawSkins() {
			if (fade <= 0.002f) return;
			float a = fade;

			// Onizleme yalnizca REPaint'te cizilir: OnGUI kare basina iki kez
			// gelir (Layout + Repaint); kamera cizimi her ikisinde de yapilirsa
			// is yarisi bosta gider.
			Event e = Event.current;
			if (e != null && e.type == EventType.Repaint) skinRendered = false;

			HandleSkinsInput();

			// ---- baslik ---------------------------------------------------
			// Altı bilgi satırı olmadığı için başlık PANELİN ÜSTÜNE değil,
			// panelin içine yerleşiyor: ekranda boş şerit kalmıyor.
			float y = SK_PAD;
			Label(new Rect(SK_PAD, y, REF_W - SK_PAD * 2f, SK_TITLE_H),
				  SkinTitle, styleTitle, Accent, a);
			y += SK_TITLE_H + 4f;

			// ---- panel (ortalanmis) --------------------------------------
			float px = (REF_W - SK_PANEL_W) * 0.5f;
			Rect body = new Rect(px, y, SK_PANEL_W, SK_PANEL_H);
			Fill(new Color(0.05f, 0.05f, 0.06f, MenuTheme.PanelAlpha * a), body);
			Frame(new Color(1f, 1f, 1f, 0.07f * a), body, 1f);

			const float IN = MenuTheme.SK_INNER_PAD;
			// Sol: liste (genis), sag: onizleme + bilgi (dar)
			// MenuTheme.Clip ile kırpılıyor: altyapı ölçüleri değişse bile
			// hiçbir öğe panelin dışına taşmaz (boşluk/taşma garantisi).
			Rect inner = MenuTheme.Inset(body, IN);
			Rect list = MenuTheme.Clip(new Rect(inner.x, inner.y,
				inner.width - SK_PREVIEW_W - IN, inner.height), inner);
			DrawSkinList(list, a);

			// Govde seridi listenin altinda.
			Rect bodyBar = MenuTheme.Clip(new Rect(list.x, list.yMax + SK_BODY_GAP,
				list.width, SK_BODY_H), inner);
			DrawBodyBar(bodyBar, a);

			float rightX = list.xMax + IN;
			Rect pv = MenuTheme.Clip(new Rect(rightX, inner.y, SK_PREVIEW_W,
				inner.height - SK_INFO_H - SK_BODY_GAP), inner);
			Fill(new Color(0f, 0f, 0f, MenuTheme.InnerAlpha * a), pv);
			Frame(new Color(Accent.r, Accent.g, Accent.b, 0.20f * a), pv, 1f);
			DrawSkinPreview(pv, a);

			Rect info = MenuTheme.Clip(new Rect(rightX, pv.yMax + SK_BODY_GAP,
				SK_PREVIEW_W, SK_INFO_H), inner);
			DrawSkinInfo(info, a);

			// ---- alt serit: GERI -------------------------------------------
			// GERİ butonu ekranın ALTINDA, panelle aynı hizada. Daha önce
			// panel.yMax + 8 idi ve dikey alan taştığında ekrandan çıkıyordu.
			Rect foot = new Rect(px, body.yMax + SK_BODY_GAP, SK_PANEL_W, SK_FOOT_H);
			DrawSkinFooter(foot, a);
		}

		// ------------------------------------------------------------------

		void DrawSkinFooter(Rect r, float a) {
			// Yalnızca GERİ butonu. Kullanıcı isteğiyle "tıkla / ↑↓ seç ·
			// kaydır · ESC geri" ipucu satırı KALDIRILDI; yerine boşluk da
			// bırakılmıyor (buton panelle aynı genişlikte hizalanıyor).
			Rect back = new Rect(r.x, r.y, r.width, r.height);
			bool hot = back.Contains(MouseLocal());
			Fill(new Color(0.07f, 0.07f, 0.09f, MenuTheme.PanelAlpha * a), back);
			Frame(new Color(hot ? Accent.r : 1f, hot ? Accent.g : 1f,
					hot ? Accent.b : 1f, (hot ? 0.9f : 0.18f) * a), back, 1f);
			Fill(new Color(Accent.r, Accent.g, Accent.b, (hot ? 1f : 0.6f) * a),
				new Rect(back.x + 1f, back.y + 6f, 3f, back.height - 12f));
			Label(back, new GUIContent("◀  GERİ"), styleRow,
				hot ? Accent : Normal, a);

			Event e = Event.current;
			if (hot && e != null && e.type == EventType.MouseDown && e.button == 0) {
				try { e.Use(); } catch { }
				SkinsExit();
				ShowMain();
			}
		}

		// ------------------------------------------------------------------

		void DrawSkinPreview(Rect r, float a) {
			if (preview != null && preview.Preview != null && !skinRendered) {
				preview.Render();
				skinRendered = true;
			}
			Texture t = (preview != null) ? preview.Preview : null;
			if (t != null) {
				try {
					// CONTAIN: RT'yi r ICINDE sigdir, tasmasina izin verme.
					// Önceden "en-boy oranını koru, genişliği sabit tut"
					// kullanılıyordu: 300x428 alanda 300x450 üretiyor ve
					// 22 px alttaki bilgi kutusunun USTUNE biniyordu
					// (ekran görüntüsündeki taşma).
					float ar = t.width / (float)t.height;
					float dw = r.width, dh = r.width / ar;
					if (dh > r.height) { dh = r.height; dw = r.height * ar; }
					Rect dr = new Rect(r.x + (r.width - dw) * 0.5f,
									  r.y + (r.height - dh) * 0.5f,
									  dw, dh);
					Color c = Color.white;
					c.a = a;
					GUI.DrawTexture(V(dr), t, ScaleMode.StretchToFill, false);
				}
				catch { }
			}
			else {
				Label(new Rect(r.x, r.y + r.height * 0.35f, r.width, 20f),
					new GUIContent("model yüklenemedi"), styleHint, Dim, a);
				Label(new Rect(r.x, r.y + r.height * 0.35f + 20f, r.width, 34f),
					new GUIContent("build_bundle.cmd çalıştır,\nsonra build.cmd"),
					styleHint, Dim, a);
			}
		}

		// ------------------------------------------------------------------

		void DrawSkinList(Rect r, float a) {
			if (Count <= 0) {
				Label(r, new GUIContent("skin kataloğu boş"), styleHint, Dim, a);
				return;
			}

			// Satır YÜKSEKLİĞİ SABİT. Önceden "panel yüksekliği / satır
			// sayısı" ile bölüyorduk; liste alanı bir piksel değişse satır
			// boyu değişiyor ve kaydırma ofsetiyle arası açılıyordu.
			float rowH = SK_ROW_H + SK_ROW_GAP;
			int rows = (int)(r.height / rowH);
			if (rows < 1) rows = 1;
			if (rows > Count) rows = Count;

			int maxTop = Count - rows;
			if (maxTop < 0) maxTop = 0;
			if (listTop > maxTop) listTop = maxTop;
			if (listTop < 0) listTop = 0;

			// DİKKAT: GUI.BeginGroup / BeginScrollView KULLANILMAZ.
			// MenuUi'nin Fill/Frame/Label yardımcıları zaten V() uygular;
			// grup içinde bir daha uygulanırsa kutu ekranın sol üstüne,
			// yazı ortaya çizilir. Bunun yerine yalnızca GÖRÜNÜR aralık
			// çizilir (listTop .. listTop+rows) — bu da aynı işi yapar,
			// üstelik panel dışına taşma ihtimali yoktur.
			Vector2 ml = MouseLocal();
			Event ev = Event.current;
			skinHover = -1;

			float listW = r.width - 8f;
			for (int i = 0; i < rows; i++) {
				int idx = listTop + i;
				if (idx < 0 || idx >= Count) break;
				Rect row = new Rect(r.x, r.y + i * rowH, listW, SK_ROW_H);
				DrawSkinRow(row, idx, a, ml, ev);
			}

			// Kaydırma çubuğu
			if (Count > rows) {
				float trackH = rows * rowH;
				float frac = rows / (float)Count;
				float barH = Mathf.Max(trackH * frac, 26f);
				float barY = r.y + (trackH - barH) * (maxTop > 0 ? listTop / (float)maxTop : 0f);
				Fill(new Color(1f, 1f, 1f, 0.08f * a),
					new Rect(r.xMax - 3f, r.y, 3f, trackH));
				Fill(new Color(Accent.r, Accent.g, Accent.b, 0.55f * a),
					new Rect(r.xMax - 3f, barY, 3f, barH));
			}
		}

		void DrawSkinRow(Rect row, int idx, float a, Vector2 ml, Event ev) {
			NpcSkin s = NpcSkinCatalog.All[idx];
			bool sel = (idx == PlayerSkin.Selected);
			bool hot = row.Contains(ml);
			if (hot) skinHover = idx;

			// Satır zemini — hepsi MenuUi'nin kendi yardımcılarıyla, V() bir kez.
			if (sel) {
				Fill(new Color(Accent.r, Accent.g, Accent.b, 0.18f * a), row);
				Fill(new Color(Accent.r, Accent.g, Accent.b, 0.85f * a),
					new Rect(row.x, row.y, 3f, row.height));
			}
			else if (hot) {
				Fill(new Color(1f, 1f, 1f, 0.07f * a), row);
			}
			Frame(new Color(1f, 1f, 1f, 0.05f * a), row, 1f);

			Color col = sel ? Accent : (hot ? Normal : Dim);

			Label(new Rect(row.x + 9f, row.y, 32f, row.height),
				new GUIContent((idx + 1).ToString()), styleHint, Dim, a);
			Label(new Rect(row.x + 44f, row.y, 176f, row.height),
				new GUIContent(s.name), styleRow, col, a);
			Label(new Rect(row.x + 226f, row.y, row.width - 236f, row.height),
				new GUIContent(s.shirt + "  ·  " + s.pants),
				styleHint, Dim, a);

			// MenuMouse yolu: oyunun OnGUI'si olayi tuketse bile tiklama
			// bize gelir. Clicked tiklamayi tuketir, asagidaki Event
			// yolu bu karede ikinci kez calismaz.
			if (hot && MenuMouse.Clicked(row)) { PlayerSkin.Select(idx); return; }
			if (hot && ev != null && ev.type == EventType.MouseDown && ev.button == 0) {
				try { ev.Use(); } catch { }
				PlayerSkin.Select(idx);
			}
		}

		// ------------------------------------------------------------------

		/// <summary>
		/// Govde secici seridi.
		///
		/// Oyunun "bodymesh" adi altinda birden fazla mesh var (level2'de
		/// 186 SkinnedMeshRenderer, 6 farkli mesh) ve NPC'ler farkli
		/// olanlari kullaniyor. Bundle'da altisi de var:
		/// 960 cocuk / 1697 kadin / 1709, 1712, 1714 erkek / 1789 uzun.
		///
		/// IKI SECIM KAYNAGI:
		///   * skin'in kendi govdesi (skin_mesh_map.json) - varsayilan,
		///   * kullanici bu seritten bilerek baska bir govde secebilir.
		///
		/// Secim PlayerSkin.bodyOverride'a yazilir (PlayerPrefs'te kalir) ve
		/// hem 3B onizlemeyi hem de dunyadaki kuklalari etkiler.
		/// </summary>
		void DrawBodyBar(Rect r, float a) {
			int[] vs = PlayerAssetNpc.BodyVertices;
			if (vs == null || vs.Length <= 1) return;   // tek govde: gosterme

			Vector2 ml = MouseLocal();
			Event ev = Event.current;
			bodyHover = -1;
			int cur = PlayerSkin.EffectiveBody;

			Label(new Rect(r.x, r.y, 62f, r.height),
				  new GUIContent("govde"), styleHint, Dim, a);

			float x = r.x + 62f;
			float gap = SK_BODY_GAP;
			float bw = (r.width - 62f - gap * (vs.Length - 1)) / vs.Length;
			if (bw < 28f) bw = 28f;

			for (int i = 0; i < vs.Length; i++) {
				Rect box = new Rect(x + i * (bw + gap), r.y, bw, r.height);
				bool sel = (vs[i] == cur);
				bool hot = box.Contains(ml);
				if (hot) bodyHover = i;

				if (sel) Fill(new Color(Accent.r, Accent.g, Accent.b, 0.22f * a), box);
				else if (hot) Fill(new Color(1f, 1f, 1f, 0.07f * a), box);
				Frame(sel
					  ? new Color(Accent.r, Accent.g, Accent.b, 0.9f * a)
					  : new Color(1f, 1f, 1f, 0.10f * a), box, 1f);

				Label(box, new GUIContent(vs[i].ToString()), styleHint,
					  sel ? Accent : (hot ? Normal : Dim), a);

				bool click = hot && MenuMouse.Clicked(box);
				if (!click && hot && ev != null
					&& ev.type == EventType.MouseDown && ev.button == 0) {
					try { ev.Use(); } catch { }
					click = true;
				}
				if (click) { PlayerSkin.SetBodyOverride(vs[i]); return; }
			}

			// Skin'in kendi govdesi listede yoksa belirt.
			NpcSkin? ns = PlayerSkin.Current;
			int want = (ns.HasValue && ns.Value.bodyVertices > 0)
				? ns.Value.bodyVertices : NpcSkinCatalog.DEFAULT_BODY;
			if (PlayerSkin.bodyOverride <= 0 && !PlayerAssetNpc.HasBody(want)) {
				Label(new Rect(r.x, r.yMax + 3f, r.width, 14f),
					  new GUIContent("bu gorunumun govdesi " + want
						+ " vtx, pakette yok"),
					  styleHint, Warn, a);
			}
		}

		void DrawSkinInfo(Rect r, float a) {
			NpcSkin? cur = PlayerSkin.Current;
			if (!cur.HasValue) return;
			Fill(new Color(0f, 0f, 0f, 0.35f * a), r);
			Frame(new Color(1f, 1f, 1f, 0.06f * a), r, 1f);

			float x = r.x + 9f;
			float w = r.width - 18f;
			float y = r.y + 4f;

			Label(new Rect(x, y, w, SK_ROW_H),
				new GUIContent(cur.Value.name), styleRow, Accent, a);
			y += SK_ROW_H + 1f;

			Label(new Rect(x, y, w, 15f),
				new GUIContent("gömlek   " + cur.Value.shirt), styleHint, Dim, a);
			y += 15f;
			Label(new Rect(x, y, w, 15f),
				new GUIContent("pantolon  " + cur.Value.pants), styleHint, Dim, a);
			y += 15f;
			Label(new Rect(x, y, w, 15f),
				new GUIContent("yüz       " + cur.Value.face), styleHint, Dim, a);
			y += 15f;

			// Gövde tipi. Oyunda "bodymesh" adı altında birden fazla mesh
			// var (level2'de 186 SMR, 6 farklı mesh) ve kıyafet dokusu
			// mesh'e göre tasarlanmış. Skin'in gövdesi bundle'dakinden
			// farklıysa uyarı gösterilir — kıyafetler o zaman yanlış
			// bedene oturur (yaka/cap uyumsuzluğu).
			int bv = cur.Value.bodyVertices;
			string bodyText = "gövde    " + (bv > 0 ? bv.ToString() : "?")
				+ " vtx";
			// Artik govde degistirilebiliyor, "uyuşmayan govde" uyarisi
			// yerine yalnizca "bu govde bundle'da yok" uyarisi kalir.
			bool fits = PlayerAssetNpc.HasBody(bv);
			Label(new Rect(x, y, w, 15f),
				new GUIContent(fits ? bodyText : bodyText + "  ✗ gövde uyuşmuyor"),
				styleHint, fits ? Dim : Warn, a);

			int sel = PlayerSkin.Selected;
			Label(new Rect(x, r.yMax - 18f, w, 16f),
				new GUIContent((sel + 1) + " / " + Count), styleHint, Dim, a);
		}

		// ------------------------------------------------------------------

		void HandleSkinsInput() {
			Event e = Event.current;
			if (e == null) return;

			if (e.type == EventType.KeyDown) {
				int rows = SkinVisibleRows();
				if (rows <= 0) rows = 1;
				if (e.keyCode == KeyCode.Escape) {
					try { e.Use(); } catch { }
					// Z-SIRASI: konsol/sohbet ustteyken gorunum ESC dinlemez.
					if (!UiFocus.ConsoleVisible() && !UiFocus.ChatVisible()) {
						SkinsExit();
						ShowMain();
					}
				}
				else if (e.keyCode == KeyCode.UpArrow) {
					try { e.Use(); } catch { }
					PlayerSkin.Select(PlayerSkin.Selected - 1);
					KeepVisible(PlayerSkin.Selected, rows);
				}
				else if (e.keyCode == KeyCode.DownArrow) {
					try { e.Use(); } catch { }
					PlayerSkin.Select(PlayerSkin.Selected + 1);
					KeepVisible(PlayerSkin.Selected, rows);
				}
				else if (e.keyCode == KeyCode.PageUp) {
					try { e.Use(); } catch { }
					listTop = Mathf.Clamp(listTop - rows, 0, Count - rows);
				}
				else if (e.keyCode == KeyCode.PageDown) {
					try { e.Use(); } catch { }
					listTop = Mathf.Clamp(listTop + rows, 0, Count - rows);
				}
				return;
			}

			// Tekerlek SkinsTick'te islenir (yukarida); burada yalnizca
			// klavye kalir. Not: tekerlek artik SECIMI degistirmez, sadece
			// listeyi kaydirir (secim: tik / yukari-asagi).
		}

		/// <summary>
		/// Tekerlek kaydirma: kesirli birikimle satir satir.
		///
		/// NEDEN BIRIKIM: (int)(delta * 3f) kucuk delta'da (dokunmatik
		/// yuzeyler) sifira kesiyordu -> "kaydiriyorum olmuyor". Birikim
		/// hicbir hareketi yutmaz; tam satir olusunca liste oynar.
		/// delta > 0 = yukari = liste basi (onceki davranisla ayni yon).
		/// </summary>
		void ScrollBy(float delta) {
			int rows = SkinVisibleRows();
			if (rows <= 0 || Count <= rows) { wheelAcc = 0f; return; }
			wheelAcc += delta * 3f;
			if (wheelAcc > rows) wheelAcc = rows;
			if (wheelAcc < -rows) wheelAcc = -rows;
			int step = (int)wheelAcc;
			if (step != 0) {
				wheelAcc -= step;
				int maxTop = Count - rows;
				if (maxTop < 0) maxTop = 0;
				listTop = Mathf.Clamp(listTop - step, 0, maxTop);
			}
		}

		/// <summary>Seçili satır görünür kalsın.</summary>
		void KeepVisible(int index, int rows) {
			if (rows <= 0 || index < 0) return;
			if (index < listTop) listTop = index;
			else if (index >= listTop + rows) listTop = index - rows + 1;
			int maxTop = Count - rows;
			if (listTop > maxTop) listTop = maxTop;
			if (listTop < 0) listTop = 0;
		}

		/// <summary>Menü kapanınca önizlemeyi serbest bırak.</summary>
		void SkinsExit() {
			skinsOpen = false;
			if (preview != null) {
				preview.Release();
				preview = null;
			}
		}

		/// <summary>Ana menüdeki SKIN satırı: sol etiket + sağda seçili ad.</summary>
		static readonly GUIContent RowSkinContent = new GUIContent("GÖRÜNÜM");

		void RowSkin(Rect r, float a) {
			// Satır 340 px geniş ve yazı ORTALI (styleRow MiddleCenter).
			// Ayrı bir sağ etiket çizmek "GÖRÜNÜM" ile çakışırdı; adı aynı
			// GUIContent'in içine koyuyoruz: tek, ortalanmış, taşması yok.
			string name = PlayerSkin.CurrentName;
			RowSkinContent.text = (name == "—")
				? "GÖRÜNÜM"
				: "GÖRÜNÜM   ·   " + name;
			RowSkinContent.tooltip = "3B önizlemeli görünüm seçimi ("
				+ PlayerSkin.Count + " görünüm)";

			// Row() kendi görünmez GUI.Button'unu kuruyor ve OnRow(index)
			// çağırıyor; TIKLAMA ORADA YAPILIR. Burada ayrıca MouseDown
			// dinlemek aynı tıklamayı iki kez işlerdi.
			Row(r, RowSkinContent, 2, a);
		}
	}
}