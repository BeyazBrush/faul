using UnityEngine;

namespace SteamMP {

	/// <summary>
	/// KAFA ÜSTÜ İSİM ETİKETİ — uzaktan oyuncu gövdesi + test botu.
	///
	/// Neden OnGUI: dünyanın üstüne çizilen Screen-space GUI.Label, mesafeden
	/// bağımsız okunur ve ek kamera/ek Canvas gerektirmez. Gövde başına bir
	/// bileşen, kare başına tahsis yok (stil statik, Rect stack'te).
	///
	/// Yükseklik modelden ölçülür (Renderer.bounds): bundle kökü 3.71
	/// ölçekli olduğu için sabit sayı yazmak yanlış olurdu.
	///
	/// Unity 5 / .NET 3.5 uyumlu.
	/// </summary>
	public class NameTag : MonoBehaviour {

		/// <summary>Gösterilen isim.</summary>
		public string Text = "";

		/// <summary>Ayaklardan yukarı ek pay (m). Sınırlar modele göre bulunur.</summary>
		public float Padding = 0.35f;

		static GUIStyle style;
		static GUIStyle shadow;

		/// <summary>Gövdeye takar (yoksa ekler) ve ismi yazar.</summary>
		public static NameTag Attach(GameObject go, string text) {
			if (go == null) return null;
			NameTag t = go.GetComponent<NameTag>();
			if (t == null) t = go.AddComponent<NameTag>();
			t.Text = text != null ? text : "";
			return t;
		}

		/// <summary>İsmi günceller (PlayerInfo geç geldiğinde).</summary>
		public void SetText(string s) { Text = s != null ? s : ""; }

		void OnGUI() {
			if (string.IsNullOrEmpty(Text)) return;
			Camera cam = Camera.main != null ? Camera.main : Camera.current;
			if (cam == null) return;

			float top = transform.position.y + 2f;
			try {
				Renderer r = GetComponentInChildren<SkinnedMeshRenderer>();
				if (r == null) r = GetComponentInChildren<Renderer>();
				if (r != null) top = r.bounds.max.y + Padding;
			}
			catch { }

			Vector3 sp;
			try { sp = cam.WorldToScreenPoint(new Vector3(
				transform.position.x, top, transform.position.z)); }
			catch { return; }
			if (sp.z < 0.5f) return;
			if (sp.x < -60f || sp.x > Screen.width + 60f) return;
			if (sp.y < -60f || sp.y > Screen.height + 60f) return;

			if (style == null) {
				style = new GUIStyle(GUI.skin.label);
				style.alignment = TextAnchor.MiddleCenter;
				style.fontSize = 14;
				style.fontStyle = FontStyle.Bold;
				style.normal.textColor = Color.white;
				shadow = new GUIStyle(style);
				shadow.normal.textColor = Color.black;
			}

			Rect box = new Rect(sp.x - 100f, Screen.height - sp.y - 12f, 200f, 24f);
			GUI.Label(new Rect(box.x + 1f, box.y + 1f, box.width, box.height), Text, shadow);
			GUI.Label(box, Text, style);
		}
	}
}