using UnityEngine;

namespace SteamMP {

    /// <summary>
    /// MENÜ ALTYAPISI — tek doğruluk kaynağı.
    ///
    /// NEDEN AYRI DOSYA: ölçüler ve renkler MenuUi ile MenuUiSkins
    /// arasında bölünmüştü (PAD/PANEL_W burada, SK_* orada) ve yeni bir
    /// görünüm eklerken hangisinin kullanılacağı belli değildi; sonuç
    /// "yarım kalmış, boşluk bırakmış" ekranlar oldu. Artık HER ölçü ve
    /// HER renk burada; görünümler yalnızca buradan okur.
    ///
    /// KULLANIM YAZIMI (yeni menü eklerken):
    ///     void DrawX() {
    ///         Rect p = MenuTheme.Panel(a);        // ortalı ana panel
    ///         MenuTheme.PanelBg(p, a);            // zemin + çerçeve
    ///         Rect rows = MenuTheme.Inset(p, MenuTheme.Pad, a);
    ///         ...
    ///     }
    ///
    /// Boşluk bırakmama kuralı: bir görünüm çizilirken arka arkaya
    /// <c>y += H</c> ile ilerlenir ve son <c>Rect</c> daima panelin
    /// içine sığacak şekilde kırpılır (MenuTheme.Clip). Böylece altyapı
    /// değişse bile hiçbir öğe taşmaz.
    /// </summary>
    public static class MenuTheme {

        // ------------------------------------------------------------------
        // EKRAN
        // ------------------------------------------------------------------
        /// <summary>Tasarım referans çözünürlüğü. Tüm ölçüler buna göre.</summary>
        public const float REF_W = 1280f;
        public const float REF_H = 720f;

        // ------------------------------------------------------------------
        // ORTAK ÖLÇÜLER
        // ------------------------------------------------------------------
        public const float PAD = 26f;        // ekran kenarı boşluğu
        public const float TITLE_H = 46f;    // ekran başlığı
        public const float SUB_H = 20f;      // başlık altı satır
        public const float RULE_H = 2f;      // ince ayırıcı çizgi
        public const float GAP = 12f;        // genel boşluk

        public const float PANEL_W = 520f;   // ana menü paneli genişliği
        public const float PANEL_PAD = 26f;  // panel iç boşluğu
        public const float BTN_H = 42f;      // düğme yüksekliği
        public const float BTN_GAP = 12f;    // düğmeler arası

        public const float CODE_LABEL_H = 18f;
        public const float CODE_H = 44f;
        public const float STATUS_H = 22f;
        public const float BACK_H = 34f;
        public const int MaxCodeChars = 20;

        public const float BRAND_H = 30f;
        public const float BRAND_SUB_H = 16f;

        public const float LOAD_TITLE_H = 58f;
        public const float LOAD_SUB_H = 22f;
        public const float LOAD_BAR_H = 6f;
        public const float LOAD_BAR_W = 420f;

        public const float ROW_H = 52f;      // ana menü satırı
        public const float ROW_W = 340f;
        public const float ROW_GAP = 14f;
        public const float FOOT_H = 18f;

        public const float CMD_H = 34f;      // komut satırı
        public const float CMD_GAP = 14f;

        /// <summary>Ana menü satır sayısı.
        ///
        /// v23: 3 -> 2. NEW GAME satırı kaldırıldı (kullanıcı isteği); modun
        /// menüsü yalnızca PLAY ONLINE ve GÖRÜNÜM satırlarını taşıyor. Oyunun
        /// kendi New Game butonu ise 3B sahnede tamamen gizleniyor.
        /// </summary>
        public const int ROW_COUNT = 2;

        // ------------------------------------------------------------------
        // GÖRÜNÜM (SKIN) SEKMESİ
        // ------------------------------------------------------------------
        public const float SK_PAD = 18f;
        public const float SK_TITLE_H = 30f;
        /// <summary>
        /// Başlık altı satır yüksekliği.
        ///
        /// KULLANILMIYOR: kullanıcı isteğiyle başlık altı bilgi satırları
        /// (SkinSub) kaldırıldı; alan da boş bırakılmaması için burada
        /// tanımlı kalıyor ki ileride başlık altı satırı geri eklenirse
        /// "kaç piksel?" sorusu cevapsız kalmasın.
        /// </summary>
        public const float SubH = 18f;
        public const float SK_PANEL_W = 900f;
        public const float SK_PANEL_H = 546f;
        public const float SK_PREVIEW_W = 300f;
        public const float SK_INFO_H = 107f;
        public const float SK_ROW_H = 26f;
        public const float SK_ROW_GAP = 2f;
        public const float SK_FOOT_H = 30f;
        public const float SK_BODY_H = 28f;
        public const float SK_BODY_GAP = 6f;
        public const float SK_INNER_PAD = 10f;   // panel içindeki kenar boşluğu

        // ------------------------------------------------------------------
        // RENKLER
        // ------------------------------------------------------------------
        public static readonly Color Accent = new Color(1f, 0.80f, 0.15f);
        public static readonly Color Normal = new Color(0.92f, 0.92f, 0.94f);
        public static readonly Color Dim = new Color(0.60f, 0.62f, 0.68f);
        public static readonly Color Warn = new Color(1f, 0.42f, 0.30f);

        /// <summary>
        /// Panel zemini opaklığı.
        ///
        /// Kullanıcı isteği: şeffaf tasarım. Eskiden 0.80 idi ve arkasındaki
        /// oyunu tamamen kesiyordu; 0.55 arkası hâlâ okunuyor.
        /// </summary>
        public const float PanelAlpha = 0.55f;

        /// <summary>İç içe kutular (satır arka planı).</summary>
        public const float InnerAlpha = 0.32f;

        /// <summary>Seçili/üzerine gelinmiş öğe.</summary>
        public const float HotAlpha = 0.90f;

        // ------------------------------------------------------------------
        // YARDIMCI: DÜZEN
        // ------------------------------------------------------------------

        /// <summary>
        /// Ekranda ortalanmış ana panel.
        /// </summary>
        public static Rect Panel(float a, float w = PANEL_W, float h = 0f) {
            float hh = h > 0f ? h : REF_H - PAD * 2f;
            return new Rect((REF_W - w) * 0.5f, (REF_H - hh) * 0.5f, w, hh);
        }

        /// <summary>
        /// Panelin İÇİNDE kalan alan (kenar boşluğu düşülmüş).
        /// </summary>
        public static Rect Inset(Rect r, float pad) {
            return new Rect(r.x + pad, r.y + pad,
                Mathf.Max(1f, r.width - pad * 2f),
                Mathf.Max(1f, r.height - pad * 2f));
        }

        /// <summary>
        /// Bir Rect'i başka Rect'in içine kırpır.
        ///
        /// BOŞLUK KORUMASI: altyapı ölçüleri değişse bile hiçbir öğe
        /// panelin dışına taşmaz; kısmen sığmayan öğe kırpılır.
        /// </summary>
        public static Rect Clip(Rect child, Rect bounds) {
            float x0 = Mathf.Max(child.x, bounds.x);
            float y0 = Mathf.Max(child.y, bounds.y);
            float x1 = Mathf.Min(child.xMax, bounds.xMax);
            float y1 = Mathf.Min(child.yMax, bounds.yMax);
            return new Rect(x0, y0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        }

        /// <summary>
        /// Alt alta öğeler dizen yardımcı. Boşluk bırakmamak için
        /// öğeler tek noktadan yürütülür.
        /// </summary>
        public class Stack {
            public float Y;
            public readonly Rect Bounds;

            public Stack(Rect area, float startY) {
                Bounds = area;
                Y = startY;
            }

            /// <summary>Bir satır yerleştirir ve Y'yi ilerletir.</summary>
            public Rect Row(float x, float w, float h) {
                Rect r = new Rect(x, Y, w, h);
                Y += h;
                return Clip(r, Bounds);
            }

            /// <summary>Satırlar arasına boşluk koyar.</summary>
            public Stack Space(float gap) {
                Y += gap;
                return this;
            }
        }

        // ------------------------------------------------------------------
        // YARDIMCI: OPAKLIK
        // ------------------------------------------------------------------

        /// <summary>Temel rengi verilen alfa ile çarpar (şeffaflık için tek yol).</summary>
        public static Color WithAlpha(Color c, float a) {
            c.a *= a;
            return c;
        }

        /// <summary>Ölçüyü mevcut alfa ile çarpar (çizim çağrılarında tutarlılık).</summary>
        public static float A(float a, float extra = 1f) {
            return Mathf.Clamp01(a * extra);
        }
    }
}
