//
// MenuUi -- ana menu: pencereler, olcek, cizim ve tum etkilesim.
//
// BU DOSYA BIR KURTARMADIR. Orijinal kaynak 2026-10-14 00:16'da
// bosaltilmisti (5 bayt: yalnizca BOM + satir sonu) ve hicbir yedegi
// yoktu. Icerik, 2026-10-14 00:05 tarihli SON BASARILI derlemeden
// (bin\Release\MSCSteamMP.dll) decompile edilerek geri getirildi.
// Kaynak yorumlari IL icinde bulunmadigi icin KAYIP; kod aynidir.
//
// DIKKAT: bu dosya, tipin BIRLESIK halinden MenuUiSkins.cs icinde
// zaten tanimli olan uyeler cikarilarak uretildi (partial class iki
// parcadir). Ikisi birlikte derlenir.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SteamMP
{
	public partial class MenuUi
	{
	public enum View
	{
		Hidden,
		Main,
		Online,
		Loading,
		GameDialog,
		Skins
	}
	public const float REF_W = 1280f;
	public const float REF_H = 720f;
	private static float viewScale;
	private static Vector2 viewOffset;
	private const float PAD = 26f;
	private const float TITLE_H = 46f;
	private const float SUB_H = 20f;
	private const float RULE_H = 2f;
	private const float GAP = 12f;
	private const float PANEL_W = 520f;
	private const float BTN_H = 42f;
	private const float BTN_GAP = 12f;
	private const float CODE_LABEL_H = 18f;
	private const float CODE_H = 44f;
	private const float STATUS_H = 22f;
	private const float BACK_H = 34f;
	private const float PANEL_PAD = 26f;
	private const float BRAND_H = 30f;
	private const float BRAND_SUB_H = 16f;
	private const float LOAD_TITLE_H = 58f;
	private const float LOAD_SUB_H = 22f;
	private const float LOAD_BAR_H = 6f;
	private const float LOAD_BAR_W = 420f;
	private const float ROW_H = 52f;
	private const float ROW_W = 340f;
	private const float ROW_GAP = 14f;
	private const float FOOT_H = 18f;
	private const float CMD_H = 34f;
	private const float CMD_GAP = 14f;
	private const string CmdControlName = "SmpMenuCmd";
	private View target;
	private float fade;
	private int hover = -1;
	private int active = -1;
	private float slide;
	private float press;
	private float clock;
	private string cmdInput = string.Empty;
	private string cmdInputKey = "\0";
	private bool cmdFocused;
	private readonly List<string> cmdHistory = new List<string>();
	private int cmdHistoryIndex = -1;
	private string[] cmdNames;
	private float cmdCaret;
	private static readonly GUIContent CodeContent = new GUIContent(string.Empty);
	private string codeKey = "\0";
	private float ruleT;
	private string hostLobbyId = string.Empty;
	private string typedCode = string.Empty;
	private string typedKey = "\0";
	private static readonly GUIContent TypedContent = new GUIContent(string.Empty);
	private bool fieldFocused;
	public const int MaxCodeChars = 20;
	private readonly StringBuilder stringBuilder = new StringBuilder(20);
	private static readonly GUIContent StatusContent = new GUIContent(string.Empty);
	private string statusKey = "\0";
	private Color statusColor = new Color(1f, 0.78f, 0.35f);
	private Color statusColorNext = new Color(1f, 0.78f, 0.35f);
	private string loadReason = string.Empty;
	private float loadFade;
	private float loadProgress;
	private bool loadingOut;
	private float loadOutFade;
	private static readonly GUIContent GameHintContent = new GUIContent("The game is asking to confirm. Press BEGIN to start a new game, CANCEL to go back.  ESC also goes back.");
	private string loadTitleKey = "\0";
	private GUIContent loadTitle = new GUIContent(string.Empty);
	private GUIContent loadStatus = new GUIContent(string.Empty);
	private static readonly GUIContent LobbyIdPendingContent = new GUIContent("lobby id pending...");
	private string hostLobbyKey = "\0";
	private GUIContent hostLobbyContent = new GUIContent(string.Empty);
	private string typedLobbyKey = "\0";
	private GUIContent typedLobbyContent = new GUIContent(string.Empty);
	private static readonly Color Accent = MenuTheme.Accent;
	private static readonly Color Normal = MenuTheme.Normal;
	private static readonly Color Dim = MenuTheme.Dim;
	private static readonly Color Warn = MenuTheme.Warn;
	private static readonly Vector2[] Outline = new Vector2[4]
	{
		new Vector2(-1f, -1f),
		new Vector2(1f, -1f),
		new Vector2(-1f, 1f),
		new Vector2(1f, 1f)
	};
	private static readonly GUIContent RowPlay = new GUIContent("PLAY ONLINE");
	private static readonly GUIContent ArrowContent = new GUIContent(">");
	private static readonly GUIContent OnlineTitle = new GUIContent("ONLINE MULTIPLAYER");
	private static readonly GUIContent HostContent = new GUIContent("OPEN LOBBY");
	private static readonly GUIContent JoinContent = new GUIContent("JOIN LOBBY");
	private static readonly GUIContent BackContent = new GUIContent("BACK");
	private static readonly GUIContent HostLabelContent = new GUIContent("LOBBY CODE  -  click to copy");
	private static readonly GUIContent JoinLabelContent = new GUIContent("ENTER THE LOBBY CODE");
	private static readonly GUIContent PlaceholderContent = new GUIContent("click here, then type");
	private static readonly GUIContent CopyContent = new GUIContent("copy");
	private string brandKey = "\0";
	private GUIContent brandContent = new GUIContent(string.Empty);
	private static readonly GUIContent BrandSub = new GUIContent("S T E A M   M U L T I P L A Y E R");
	private static GUIContent charContent = new GUIContent(string.Empty);
	private GUIStyle styleBrand;
	private GUIStyle styleBrandSub;
	private GUIStyle styleRow;
	private GUIStyle styleTitle;
	private GUIStyle styleSub;
	private GUIStyle styleButton;
	private GUIStyle styleStatus;
	private GUIStyle styleCode;
	private GUIStyle styleCodeHint;
	private GUIStyle styleFoot;
	private GUIStyle styleHint;
	private GUIStyle styleCmd;
	private GUIStyle styleCmdHint;
	private GUIStyle styleHit;
	private GUIStyle styleLoadTitle;
	private GUIStyle styleLoadSub;
	private bool stylesReady;
	private GUISkin skinBefore;
	private Texture2D pixel;
	private Texture2D gradient;
	private Texture2D beam;
	private Texture2D background;
	private bool backgroundTried;
	private const string EMBEDDED_BG = "SteamMP.menu_bg.jpg";
	private static float ClockValue;
	public View Target => target;
	public float Fade => fade;
	public int Hover => hover;
	public int Active => active;
	public bool CommandFocused => cmdFocused;
	public string HostCode => CodeContent.text;
	public string HostLobbyId => hostLobbyId;
	public bool HasHostCode => CodeContent.text.Length > 0;
	public bool HasBackgroundImage => (UnityEngine.Object)(object)background != (UnityEngine.Object)null;
	public bool FieldFocused => fieldFocused;
	public bool LooksLikeLobbyId
	{
		get
		{
			if (typedCode != null && typedCode.Length >= 10)
			{
				return AllDigits(typedCode);
			}
			return false;
		}
	}
	public bool CodeComplete
	{
		get
		{
			if (!LooksLikeLobbyId)
			{
				return typedCode.Length == 6;
			}
			return true;
		}
	}
	public string TypedCode => typedCode;
	public int TypedLength => typedCode.Length;
	public bool IsVisible
	{
		get
		{
			if (target == View.Hidden)
			{
				return fade > 0.002f;
			}
			return true;
		}
	}
	public bool LoadingVisible
	{
		get
		{
			if (target == View.Loading)
			{
				return loadOutFade < 0.99f;
			}
			return false;
		}
	}
	public event Action OnPlayOnline;
	public event Action OnHost;
	public event Action OnJoin;
	public event Action OnBack;
	private static Rect V(Rect r)
	{
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		return new Rect(r.x * viewScale + viewOffset.x, r.y * viewScale + viewOffset.y, r.width * viewScale, r.height * viewScale);
	}
	private static Rect ScreenRect()
	{
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		return new Rect(0f, 0f, (float)Screen.width, (float)Screen.height);
	}
	public void BlurCommand()
	{
		cmdFocused = false;
		cmdInput = string.Empty;
		try
		{
			GUI.FocusControl(string.Empty);
		}
		catch
		{
		}
	}
	public void SetHostCode(string code)
	{
		string text = code ?? string.Empty;
		if (!(text == codeKey))
		{
			codeKey = text;
			CodeContent.text = text;
		}
	}
	public void SetHostLobbyId(ulong id)
	{
		hostLobbyId = id.ToString();
	}
	private static bool AllDigits(string s)
	{
		for (int i = 0; i < s.Length; i++)
		{
			if (s[i] < '0' || s[i] > '9')
			{
				return false;
			}
		}
		return true;
	}
	public void DebugSetCode(string text, bool focused)
	{
		typedCode = text ?? string.Empty;
		if (typedCode.Length > 20)
		{
			typedCode = typedCode.Substring(0, 20);
		}
		fieldFocused = focused;
		RefreshTyped();
	}
	public void Type(char c)
	{
		if (fieldFocused && c != ' ' && c >= '!' && c <= '~' && typedCode.Length < 20)
		{
			typedCode += c;
			RefreshTyped();
		}
	}
	public void Paste(string text)
	{
		if (!fieldFocused || string.IsNullOrEmpty(text))
		{
			return;
		}
		stringBuilder.Length = 0;
		for (int i = 0; i < text.Length; i++)
		{
			if (stringBuilder.Length >= 20)
			{
				break;
			}
			char c = text[i];
			if (c != ' ' && c != '\r' && c != '\n' && c != '\t' && c >= '!' && c <= '~')
			{
				stringBuilder.Append(c);
			}
		}
		if (stringBuilder.Length != 0)
		{
			typedCode = stringBuilder.ToString();
			RefreshTyped();
		}
	}
	public void Backspace()
	{
		if (fieldFocused && typedCode.Length != 0)
		{
			typedCode = typedCode.Substring(0, typedCode.Length - 1);
			RefreshTyped();
		}
	}
	public void ClearCode()
	{
		typedCode = string.Empty;
		fieldFocused = false;
		RefreshTyped();
	}
	public void UnfocusField()
	{
		fieldFocused = false;
	}
	private void RefreshTyped()
	{
		if (!(typedCode == typedKey))
		{
			typedKey = typedCode;
			TypedContent.text = typedCode;
		}
	}
	public void ShowMain()
	{
		if (target != View.Main || !(fade > 0.99f))
		{
			target = View.Main;
			hover = -1;
			fieldFocused = false;
			slide = 0f;
			ruleT = 0f;
		}
	}
	public void ShowOnline()
	{
		if (target != View.Online || !(fade > 0.99f))
		{
			target = View.Online;
			hover = -1;
			fieldFocused = false;
			slide = 0f;
			ruleT = 0f;
		}
	}
	public void ShowSkins()
	{
		if (target != View.Skins || !(fade > 0.99f))
		{
			target = View.Skins;
			hover = -1;
			fieldFocused = false;
			slide = 0f;
			ruleT = 0f;
			SkinsEnter();
		}
	}
	public void ShowLoading(string reason)
	{
		target = View.Loading;
		hover = -1;
		fieldFocused = false;
		loadReason = reason ?? string.Empty;
		loadFade = 0f;
		loadProgress = 0f;
		slide = 0f;
	}
	public void HideLoading()
	{
		if (target == View.Loading)
		{
			loadingOut = true;
		}
	}
	public void SetLoadReason(string reason)
	{
		loadReason = reason ?? string.Empty;
	}
	public void Hide()
	{
		target = View.Hidden;
	}
	public void ShowGameDialog()
	{
		target = View.GameDialog;
		hover = -1;
		fieldFocused = false;
	}
	public void SetStatus(string key, string message)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		if (key != statusKey)
		{
			statusKey = key;
			statusColorNext = ColorFor(key);
		}
		StatusContent.text = message ?? string.Empty;
	}
	private static Color ColorFor(string key)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrEmpty(key))
		{
			return new Color(1f, 0.78f, 0.35f);
		}
		switch (key)
		{
		case "hosting":
		case "joining":
			return new Color(0.55f, 0.85f, 1f);
		case "nosteam":
		case "short":
		case "newgame":
			return new Color(1f, 0.45f, 0.4f);
		default:
			return new Color(1f, 0.78f, 0.35f);
		}
	}
	public void Tick(float dt)
	{
		if (dt < 0f)
		{
			dt = 0f;
		}
		if (dt > 0.25f)
		{
			dt = 0.25f;
		}
		clock += dt;
		ClockValue = clock;
		float num = ((target == View.Hidden) ? 0f : 1f);
		float num2 = 14f * dt;
		float num3 = 1f - 1f / (1f + num2 + 0.5f * num2 * num2);
		fade += (num - fade) * num3;
		if (num == 0f && fade < 0.01f)
		{
			fade = 0f;
		}
		if (num == 1f && fade > 0.99f)
		{
			fade = 1f;
		}
		slide += (((num > 0f) ? 1f : 0f) - slide) * num3;
		ruleT += (((num > 0f) ? 1f : 0f) - ruleT) * num3;
		SkinsTick(dt);
		cmdCaret += dt;
		if (cmdCaret > 1f)
		{
			cmdCaret--;
		}
		if (target == View.Loading)
		{
			loadFade += (1f - loadFade) * Mathf.Clamp01(dt * 5f);
			loadProgress += (0.94f - loadProgress) * Mathf.Clamp01(dt * 0.6f);
			if (loadingOut)
			{
				loadOutFade += (1f - loadOutFade) * Mathf.Clamp01(dt * 4f);
				if (loadOutFade >= 0.99f)
				{
					Hide();
					loadingOut = false;
					loadOutFade = 0f;
				}
			}
		}
		else
		{
			loadOutFade = 0f;
		}
	}
	public void Draw()
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0132: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_014b: Unknown result type (might be due to invalid IL or missing references)
		if (fade <= 0.002f)
		{
			return;
		}
		Matrix4x4 matrix = GUI.matrix;
		int depth = GUI.depth;
		bool enabled = GUI.enabled;
		Color color = GUI.color;
		Color contentColor = GUI.contentColor;
		Color backgroundColor = GUI.backgroundColor;
		try
		{
			GUI.matrix = Matrix4x4.identity;
		}
		catch
		{
		}
		try
		{
			GUI.depth = 0;
		}
		catch
		{
		}
		try
		{
			GUI.enabled = true;
		}
		catch
		{
		}
		ReleaseStaleHotControl();
		try
		{
			EnsureStyles();
			EnsureTextures();
			EnsureBackground();
			ComputeView();
			MenuMouse.SetView(viewScale, viewOffset);
			if (target != View.GameDialog)
			{
				DrawBackdrop();
			}
			if (target == View.Loading)
			{
				DrawLoading();
			}
			else if (target == View.Online)
			{
				DrawOnline();
			}
			else if (target == View.GameDialog)
			{
				DrawGameHint();
			}
			else if (target == View.Skins)
			{
				DrawSkins();
			}
			else
			{
				DrawMain();
			}
		}
		catch (Exception ex)
		{
			SmpLog.Warning("menu draw failed: " + ex.GetType().Name + ": " + ex.Message);
		}
		try
		{
			GUI.matrix = matrix;
		}
		catch
		{
		}
		try
		{
			GUI.depth = depth;
		}
		catch
		{
		}
		try
		{
			GUI.enabled = enabled;
		}
		catch
		{
		}
		try
		{
			GUI.color = color;
		}
		catch
		{
		}
		try
		{
			GUI.contentColor = contentColor;
		}
		catch
		{
		}
		try
		{
			GUI.backgroundColor = backgroundColor;
		}
		catch
		{
		}
		MenuMouse.EndFrame();
	}
	private static void ReleaseStaleHotControl()
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Invalid comparison between Unknown and I4
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Invalid comparison between Unknown and I4
		try
		{
			Event current = Event.current;
			if ((current == null || ((int)current.type != 0 && (int)current.type != 3 && (int)current.type != 1)) && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && GUIUtility.hotControl != 0)
			{
				GUIUtility.hotControl = 0;
			}
		}
		catch
		{
		}
	}
	private void DrawGameHint()
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		float num = fade;
		Rect r = new Rect(230f, 662f, 820f, 30f);
		Fill(new Color(0.03f, 0.03f, 0.04f, 0.86f * num), r);
		Fill(new Color(Accent.r, Accent.g, Accent.b, 0.55f * num), new Rect(r.x, r.y, 3f, r.height));
		Label(new Rect(r.x + 14f, r.y, r.width - 28f, r.height), GameHintContent, styleHint, Dim, num);
	}
	private void ComputeView()
	{
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		float num = ((Screen.width > 0) ? ((float)Screen.width) : 1280f);
		float num2 = ((Screen.height > 0) ? ((float)Screen.height) : 720f);
		viewScale = Mathf.Min(num / 1280f, num2 / 720f);
		if (viewScale <= 0.001f)
		{
			viewScale = 0.001f;
		}
		viewOffset = new Vector2((num - 1280f * viewScale) * 0.5f, (num2 - 720f * viewScale) * 0.5f);
	}
	private void DrawBackdrop()
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_0240: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		float num = fade;
		Color color = GUI.color;
		GUI.color = new Color(1f, 1f, 1f, num);
		GUI.DrawTexture(ScreenRect(), (Texture)(object)gradient, (ScaleMode)0, true);
		GUI.color = color;
		float num2 = Mathf.Repeat(clock * 0.11f, 1f);
		float num3 = (float)Screen.width * 0.42f;
		float num4 = (num2 - 0.2f) * (float)Screen.width;
		Color color2 = GUI.color;
		GUI.color = new Color(1f, 0.92f, 0.72f, 0.05f * num);
		GUI.DrawTexture(new Rect(num4, 0f, num3, (float)Screen.height), (Texture)(object)beam, (ScaleMode)0, true);
		GUI.color = color2;
		if ((UnityEngine.Object)(object)background != (UnityEngine.Object)null)
		{
			Color color3 = GUI.color;
			GUI.color = new Color(1f, 1f, 1f, num * 0.55f);
			GUI.DrawTextureWithTexCoords(ScreenRect(), (Texture)(object)background, SourceCrop(), true);
			GUI.color = color3;
		}
		FillScreen(new Color(0f, 0f, 0f, 0.55f * num), new Rect(0f, 0f, (float)Screen.width, 4f));
		FillScreen(new Color(0f, 0f, 0f, 0.55f * num), new Rect(0f, (float)Screen.height - 4f, (float)Screen.width, 4f));
		FillScreen(new Color(0f, 0f, 0f, 0.55f * num), new Rect(0f, 0f, 4f, (float)Screen.height));
		FillScreen(new Color(0f, 0f, 0f, 0.55f * num), new Rect((float)Screen.width - 4f, 0f, 4f, (float)Screen.height));
		FillScreen(new Color(Accent.r, Accent.g, Accent.b, 0.85f * num), new Rect(0f, 0f, (float)Screen.width, 2f));
	}
	private Rect SourceCrop()
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		float num = Screen.width;
		float num2 = Screen.height;
		int width = ((Texture)background).width;
		int height = ((Texture)background).height;
		if (num <= 0f || num2 <= 0f || width <= 0 || height <= 0)
		{
			return new Rect(0f, 0f, 1f, 1f);
		}
		float num3 = num / num2;
		float num4 = (float)width / (float)height;
		float num5 = 0f;
		float num6 = 0f;
		float num7 = 1f;
		float num8 = 1f;
		if (num4 > num3)
		{
			num7 = num3 / num4;
			num5 = (1f - num7) * 0.5f;
		}
		else
		{
			num8 = num4 / num3;
			num6 = (1f - num8) * 0.5f;
		}
		return new Rect(num5, num6, num7, num8);
	}
	private void DrawMain()
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_0176: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
		float num = fade;
		Vector2 val = MouseLocal();
		float num2 = 224f;
		float num3 = (720f - num2) * 0.5f;
		float num4 = 640f;
		float num5 = 300f;
		float num6 = num4 - 170f;
		GUIContent content = BrandText();
		Label(new Rect(num4 - num5 * 0.5f, num3, num5, 30f), content, styleBrand, Accent, num);
		num3 += 30f;
		Label(new Rect(num4 - num5 * 0.5f, num3, num5, 16f), BrandSub, styleBrandSub, Dim, num);
		num3 += 16f;
		float num7 = 190f * Ease(ruleT);
		Fill(new Color(Accent.r, Accent.g, Accent.b, 0.9f * num), new Rect(num4 - num7 * 0.5f, num3, num7, 2f));
		if (num7 > 4f)
		{
			Fill(new Color(1f, 1f, 1f, 0.85f * num), new Rect(num4 - num7 * 0.5f - 1.5f, num3 - 1f, 3f, 4f));
		}
		num3 += 14f;
		Rect r = new Rect(num6, num3, 340f, 52f);
		Row(r, RowPlay, 0, num);
		if (r.Contains(val))
		{
			hover = 0;
		}
		num3 += 66f;
		Rect r2 = new Rect(num6, num3, 340f, 52f);
		RowSkin(r2, num);
		if (hover < 0 && r2.Contains(val))
		{
			hover = 1;
		}
		num3 += 64f;
		Rect r3 = new Rect(num6, num3, 340f, 34f);
		DrawCommandBar(r3, num);
	}
	private void DrawCommandBar(Rect r, float a)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_0207: Unknown result type (might be due to invalid IL or missing references)
		//IL_020d: Invalid comparison between Unknown and I4
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_012a: Invalid comparison between Unknown and I4
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_021b: Invalid comparison between Unknown and I4
		//IL_012d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Invalid comparison between Unknown and I4
		//IL_021f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0229: Invalid comparison between Unknown and I4
		//IL_0245: Unknown result type (might be due to invalid IL or missing references)
		//IL_024c: Invalid comparison between Unknown and I4
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_0262: Invalid comparison between Unknown and I4
		//IL_027b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0285: Invalid comparison between Unknown and I4
		//IL_029e: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a5: Invalid comparison between Unknown and I4
		bool flag = r.Contains(MouseLocal());
		Fill(new Color(0.04f, 0.04f, 0.05f, 0.72f * a), r);
		Fill(new Color(1f, 1f, 1f, 0.04f * a), new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 1f));
		Frame(cmdFocused ? new Color(Accent.r, Accent.g, Accent.b, a) : (flag ? new Color(0.55f, 0.57f, 0.62f, a) : new Color(0.34f, 0.36f, 0.4f, a)), r, 1f);
		Rect r2 = new Rect(r.x + 10f, r.y, r.width - 20f, r.height);
		try
		{
			Event current = Event.current;
			if (current != null && (int)current.type == 4 && (int)current.keyCode == 9)
			{
				try
				{
					current.Use();
				}
				catch
				{
				}
			}
			GUI.SetNextControlName("SmpMenuCmd");
			string text = cmdInput;
			try
			{
				text = GUI.TextField(V(r2), cmdInput, styleCmd);
			}
			catch
			{
			}
			if (text != cmdInput)
			{
				cmdInput = text;
				cmdHistoryIndex = -1;
			}
			if (GUI.GetNameOfFocusedControl() == "SmpMenuCmd")
			{
				cmdFocused = true;
			}
			bool flag2 = MenuMouse.Clicked(r);
			if (GUI.Button(V(r), GUIContent.none, styleHit))
			{
				flag2 = true;
			}
			if (!cmdFocused & flag2)
			{
				cmdFocused = true;
				try
				{
					GUI.FocusControl("SmpMenuCmd");
				}
				catch
				{
				}
			}
			Event current2 = Event.current;
			if (current2 == null || !cmdFocused || (int)current2.type != 4)
			{
				return;
			}
			if ((int)current2.keyCode == 13 || (int)current2.keyCode == 271)
			{
				try
				{
					current2.Use();
				}
				catch
				{
				}
				RunMenuCommand();
			}
			else if ((int)current2.keyCode == 9)
			{
				CompleteCmd();
			}
			else if ((int)current2.keyCode == 273)
			{
				try
				{
					current2.Use();
				}
				catch
				{
				}
				WalkCmdHistory(-1);
			}
			else if ((int)current2.keyCode == 274)
			{
				try
				{
					current2.Use();
				}
				catch
				{
				}
				WalkCmdHistory(1);
			}
			else if ((int)current2.keyCode == 27)
			{
				try
				{
					current2.Use();
				}
				catch
				{
				}
				GUI.FocusControl(string.Empty);
				cmdFocused = false;
				cmdInput = string.Empty;
			}
		}
		catch
		{
		}
	}
	private void RunMenuCommand()
	{
		string text = ((cmdInput == null) ? string.Empty : cmdInput.Trim());
		if (text.Length > 0 && (cmdHistory.Count == 0 || cmdHistory[cmdHistory.Count - 1] != text))
		{
			cmdHistory.Add(text);
			if (cmdHistory.Count > 40)
			{
				cmdHistory.RemoveAt(0);
			}
		}
		cmdInput = string.Empty;
		cmdInputKey = "\0";
		cmdHistoryIndex = -1;
		if (text.Length == 0)
		{
			return;
		}
		try
		{
			SmpCommandParser.Execute(text);
		}
		catch (Exception ex)
		{
			SmpLog.Error("menu command failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}
	private void CompleteCmd()
	{
		if (cmdNames == null || cmdNames.Length == 0)
		{
			cmdNames = ModCommands.KnownCommands();
			if (cmdNames == null || cmdNames.Length == 0)
			{
				SmpLog.Warning("no command list yet.");
				return;
			}
		}
		string text = CurrentCmdWord();
		string text2 = null;
		int num = 0;
		for (int i = 0; i < cmdNames.Length; i++)
		{
			if (cmdNames[i].StartsWith(text, StringComparison.OrdinalIgnoreCase))
			{
				num++;
				if (text2 == null)
				{
					text2 = cmdNames[i];
				}
			}
		}
		switch (num)
		{
		case 0:
			break;
		case 1:
			ReplaceCmdWord(text2 + " ");
			break;
		default:
			SmpLog.Print(num + " commands start with '" + text + "'.");
			break;
		}
	}
	private string CurrentCmdWord()
	{
		int length = cmdInput.Length;
		int num = length;
		while (num > 0 && IsCmdWord(cmdInput[num - 1]))
		{
			num--;
		}
		return cmdInput.Substring(num, length - num);
	}
	private void ReplaceCmdWord(string replacement)
	{
		int num = cmdInput.Length;
		while (num > 0 && IsCmdWord(cmdInput[num - 1]))
		{
			num--;
		}
		cmdInput = cmdInput.Substring(0, num) + replacement;
	}
	private static bool IsCmdWord(char c)
	{
		if ((c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9'))
		{
			return c == '_';
		}
		return true;
	}
	private void WalkCmdHistory(int delta)
	{
		if (cmdHistory.Count == 0)
		{
			return;
		}
		if (cmdHistoryIndex == -1)
		{
			if (delta > 0)
			{
				return;
			}
			cmdHistoryIndex = cmdHistory.Count - 1;
		}
		else
		{
			int num = cmdHistoryIndex + delta;
			if (num < 0)
			{
				num = 0;
			}
			if (num >= cmdHistory.Count)
			{
				cmdHistoryIndex = -1;
				cmdInput = string.Empty;
				return;
			}
			cmdHistoryIndex = num;
		}
		cmdInput = cmdHistory[cmdHistoryIndex];
	}
	private void Row(Rect r, GUIContent content, int index, float a)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Invalid comparison between Unknown and I4
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Invalid comparison between Unknown and I4
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0200: Unknown result type (might be due to invalid IL or missing references)
		//IL_0231: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_033f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0312: Unknown result type (might be due to invalid IL or missing references)
		//IL_0344: Unknown result type (might be due to invalid IL or missing references)
		//IL_0354: Unknown result type (might be due to invalid IL or missing references)
		//IL_036f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0368: Unknown result type (might be due to invalid IL or missing references)
		//IL_0374: Unknown result type (might be due to invalid IL or missing references)
		//IL_0377: Unknown result type (might be due to invalid IL or missing references)
		//IL_0380: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d9: Unknown result type (might be due to invalid IL or missing references)
		bool flag = r.Contains(MouseLocal());
		if (flag)
		{
			Event current = Event.current;
			bool flag2 = current != null && (int)current.type == 0 && current.button == 0;
			if (!flag2)
			{
				flag2 = (int)Event.current.type == 8 && MenuMouse.Held;
			}
			if (flag2)
			{
				press = 1f;
			}
			else if (current != null && (int)current.type == 7)
			{
				if (MenuMouse.Held)
				{
					press = Mathf.Max(press, 0.6f);
				}
				else
				{
					press = Mathf.Max(0f, press - 0.08f);
				}
			}
		}
		else
		{
			press = Mathf.Max(0f, press - 0.08f);
		}
		float num = (flag ? 2f : 0f);
		float num2 = (flag ? 1f : 0f);
		float num3 = press;
		Rect val = new Rect(r.x, r.y - num + num3 * 1.5f, r.width, r.height);
		if (num2 > 0.01f)
		{
			float num4 = num2 * (0.35f - num3 * 0.15f);
			Frame(new Color(Accent.r, Accent.g, Accent.b, 0.1f * num4 * a), Grow(val, 3f), 1f);
			Frame(new Color(Accent.r, Accent.g, Accent.b, 0.13f * num4 * a), Grow(val, 6f), 1f);
		}
		Fill(new Color(0.07f, 0.07f, 0.09f, 0.55f * a), val);
		Fill(new Color(1f, 1f, 1f, 0.05f * a), new Rect(val.x + 1f, val.y + 1f, val.width - 2f, 1f));
		float num5 = (flag ? 4f : 2f);
		float num6 = (flag ? (val.height - 8f) : (val.height - 18f)) * Ease(num2 * 0.35f + 0.65f);
		num6 = Mathf.Max(num6, 6f);
		Fill(new Color(Accent.r, Accent.g, Accent.b, (flag ? 1f : 0.7f) * a), new Rect(val.x + 1f, val.y + (val.height - num6) * 0.5f, num5, num6));
		Frame(flag ? new Color(Accent.r, Accent.g, Accent.b, 0.95f * a) : new Color(0.45f, 0.47f, 0.52f, 0.85f * a), val, 1f);
		if (flag)
		{
			DrawSheen(val, a, (float)index * 0.37f);
		}
		Color color = (flag ? Accent : Normal);
		Label(val, content, styleRow, color, a);
		if (flag)
		{
			float num7 = Mathf.Sin(clock * 5f) * 1.5f;
			Label(new Rect(val.xMax - 26f + num7, val.y, 20f, val.height), ArrowContent, styleRow, Accent, a);
		}
		bool flag3 = MenuMouse.Clicked(val);
		if (GUI.Button(V(val), GUIContent.none, styleHit) | flag3)
		{
			OnRow(index);
		}
	}
	private bool OnRow(int index)
	{
		active = index;
		press = 0f;
		if (index == 1)
		{
			ShowSkins();
			return true;
		}
		if (OnPlayOnline != null)
		{
			OnPlayOnline();
		}
		return true;
	}
	private void DrawLoading()
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_013a: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0187: Unknown result type (might be due to invalid IL or missing references)
		_ = loadFade;
		float num = 1f - loadOutFade;
		FillScreen(new Color(0f, 0f, 0f, num * 0.82f), ScreenRect());
		float num2 = 640f;
		float num3 = 110f;
		float num4 = (720f - num3) * 0.5f;
		float reveal = Mathf.Clamp01(loadFade * 1.4f);
		Label(new Rect(num2 - 400f, num4, 800f, 58f), LoadTitleContent(reveal), styleLoadTitle, Accent, num);
		num4 += 68f;
		float num5 = 420f;
		Rect r = new Rect(num2 - num5 * 0.5f, num4, num5, 6f);
		Fill(new Color(1f, 1f, 1f, 0.1f * num), r);
		float num6 = num5 * loadProgress;
		if (num6 > 1f)
		{
			Fill(new Color(Accent.r, Accent.g, Accent.b, 0.9f * num), new Rect(r.x, r.y, num6, 6f));
			Fill(new Color(1f, 1f, 1f, 0.9f * num), new Rect(r.x + num6 - 2f, r.y - 1f, 3f, 8f));
		}
		num4 += 20f;
		Label(new Rect(num2 - 400f, num4, 800f, 22f), LoadStatusContent(), styleLoadSub, Dim, num);
	}
	private GUIContent LoadTitleContent(float reveal)
	{
		string text = "LOADING MULTIPLAYER";
		int length = Mathf.Clamp(Mathf.CeilToInt((float)text.Length * reveal), 0, text.Length);
		string text2 = text.Substring(0, length);
		if (text2 != loadTitleKey)
		{
			loadTitleKey = text2;
			loadTitle.text = text2;
		}
		return loadTitle;
	}
	private GUIContent LoadStatusContent()
	{
		string text = (string.IsNullOrEmpty(loadReason) ? "Connecting to your friend..." : loadReason);
		if (loadStatus.text != text)
		{
			loadStatus.text = text;
		}
		return loadStatus;
	}
	private void DrawOnline()
	{
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_021c: Unknown result type (might be due to invalid IL or missing references)
		//IL_024f: Unknown result type (might be due to invalid IL or missing references)
		//IL_025f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0290: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_031f: Unknown result type (might be due to invalid IL or missing references)
		//IL_033a: Unknown result type (might be due to invalid IL or missing references)
		//IL_035b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0361: Unknown result type (might be due to invalid IL or missing references)
		//IL_0380: Unknown result type (might be due to invalid IL or missing references)
		//IL_0392: Unknown result type (might be due to invalid IL or missing references)
		//IL_0397: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03db: Unknown result type (might be due to invalid IL or missing references)
		float num = fade;
		float num2 = 468f;
		float num3 = 54f;
		float num4 = 62f;
		float num5 = 84f + num3 + 12f + num4 + 12f + 22f + 12f + 34f + 26f;
		float num6 = Ease(slide);
		Rect r = new Rect(380f, (720f - num5) * 0.5f + (1f - num6) * 26f, 520f, num5);
		FillScreen(new Color(0f, 0f, 0f, 0.25f * num), ScreenRect());
		Fill(new Color(0.06f, 0.06f, 0.08f, 0.55f * num), r);
		Frame(new Color(0.42f, 0.44f, 0.5f, 0.9f * num), r, 1f);
		Frame(new Color(Accent.r, Accent.g, Accent.b, 0.1f * num), Grow(r, 4f), 1f);
		Fill(new Color(Accent.r, Accent.g, Accent.b, 0.95f * num), new Rect(r.x, r.y, r.width, 2f));
		float num7 = r.width * 0.22f * Ease(ruleT);
		Fill(new Color(1f, 1f, 1f, 0.8f * num), new Rect(r.x, r.y - 1f, num7, 1f));
		Fill(new Color(1f, 1f, 1f, 0.8f * num), new Rect(r.xMax - num7, r.y - 1f, num7, 1f));
		float num8 = r.x + 26f;
		float num9 = r.y + 26f;
		Label(new Rect(num8, num9, num2, 46f), OnlineTitle, styleTitle, Accent, num);
		num9 += 58f;
		float num10 = (num2 - 12f) * 0.5f;
		Rect r2 = new Rect(num8, num9, num10, 42f);
		Rect r3 = new Rect(num8 + num10 + 12f, num9, num10, 42f);
		if (Button(r2, HostContent, num, 0f) && OnHost != null)
		{
			OnHost();
		}
		if (Button(r3, JoinContent, num, 0.5f) && OnJoin != null)
		{
			OnJoin();
		}
		num9 += num3 + 12f;
		bool hasHostCode = HasHostCode;
		Label(new Rect(num8, num9, num2, 18f), hasHostCode ? HostLabelContent : JoinLabelContent, styleSub, Dim, num);
		num9 += 18f;
		Rect r4 = new Rect(num8, num9, num2, 44f);
		CodeBox(r4, hasHostCode, num);
		num9 += 56f;
		Label(new Rect(num8, num9, num2, 22f), StatusContent, styleStatus, Pulse(statusColor), num);
		num9 += 34f;
		Rect r5 = new Rect(r.x + (r.width - 160f) * 0.5f, num9, 160f, 34f);
		if (Button(r5, BackContent, num, 1f) && OnBack != null)
		{
			OnBack();
		}
	}
	private void CodeBox(Rect r, bool hostView, float a)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_0127: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0227: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_016b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_02bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0267: Unknown result type (might be due to invalid IL or missing references)
		//IL_0274: Unknown result type (might be due to invalid IL or missing references)
		//IL_023a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0259: Unknown result type (might be due to invalid IL or missing references)
		//IL_0252: Unknown result type (might be due to invalid IL or missing references)
		bool flag = r.Contains(MouseLocal());
		Fill(new Color(0.03f, 0.03f, 0.04f, 0.85f * a), r);
		Fill(new Color(1f, 1f, 1f, 0.04f * a), new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 1f));
		float t = 1f;
		Color c;
		if (hostView)
		{
			c = (flag ? new Color(1f, 1f, 1f, 0.7f * a) : new Color(0.35f, 0.37f, 0.42f, a));
		}
		else if (fieldFocused)
		{
			c = new Color(Accent.r, Accent.g, Accent.b, a);
		}
		else
		{
			c = ((!flag) ? new Color(0.32f, 0.34f, 0.38f, a) : new Color(0.55f, 0.57f, 0.62f, a));
		}
		Frame(c, r, t);
		if (fieldFocused && !hostView)
		{
			Frame(new Color(Accent.r, Accent.g, Accent.b, 0.25f * a * Pulse01()), Grow(r, 3f), 1f);
		}
		if (hostView)
		{
			float num = r.height * 0.5f;
			DrawCodeCells(new Rect(r.x + 14f, r.y + 3f, r.width - 28f, num), CodeContent.text, a, filled: true);
			float num2 = r.y + 3f + num + 2f;
			float num3 = r.height - (num2 - r.y) - 3f;
			Rect logical = new Rect(r.x + 14f, num2, r.width - 28f, num3);
			if (!string.IsNullOrEmpty(hostLobbyId))
			{
				Label(logical, HostLobbyIdContent(hostLobbyId, flag), styleCodeHint, flag ? Accent : Dim, a);
			}
			else
			{
				Label(logical, LobbyIdPendingContent, styleCodeHint, Dim, a * 0.8f);
			}
		}
		else
		{
			DrawTypedCode(new Rect(r.x + 14f, r.y, r.width - 28f, r.height), a);
		}
		bool flag2 = MenuMouse.Clicked(r);
		if (GUI.Button(V(r), GUIContent.none, styleHit))
		{
			flag2 = true;
		}
		if (flag2)
		{
			if (hostView)
			{
				bool flag3 = !string.IsNullOrEmpty(hostLobbyId);
				string text = (flag3 ? hostLobbyId : CodeContent.text);
				CopyToClipboard(text);
				SmpLog.Print((flag3 ? "lobby id" : "lobby code") + " copied to the clipboard: " + text);
			}
			else
			{
				fieldFocused = true;
			}
		}
	}
	private void DrawTypedCode(Rect r, float a)
	{
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		if (LooksLikeLobbyId)
		{
			Label(r, TypedLobbyIdContent(typedCode), styleCode, Accent, a);
			return;
		}
		DrawCodeCells(r, typedCode, a, fieldFocused);
		if (typedCode.Length == 0 && !fieldFocused)
		{
			Label(new Rect(r.x + 2f, r.y, r.width, r.height), PlaceholderContent, styleCodeHint, Dim, a);
		}
	}
	private GUIContent HostLobbyIdContent(string id, bool hot)
	{
		string text = id + (hot ? "1" : "0");
		if (text != hostLobbyKey)
		{
			hostLobbyKey = text;
			hostLobbyContent.text = "LOBBY ID  " + id + (hot ? "   (click to copy)" : "");
		}
		return hostLobbyContent;
	}
	private GUIContent TypedLobbyIdContent(string id)
	{
		if (id != typedLobbyKey)
		{
			typedLobbyKey = id;
			typedLobbyContent.text = id + "   (lobby id)";
		}
		return typedLobbyContent;
	}
	private void DrawCodeCells(Rect r, string code, float a, bool filled)
	{
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_010b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Unknown result type (might be due to invalid IL or missing references)
		int num = 6;
		if (num <= 0)
		{
			return;
		}
		float num2 = 6f;
		float num3 = (r.width - num2 * (float)(num - 1)) / (float)num;
		if (num3 < 6f)
		{
			num3 = 6f;
		}
		for (int i = 0; i < num; i++)
		{
			Rect val = new Rect(r.x + (float)i * (num3 + num2), r.y + 6f, num3, r.height - 12f);
			bool flag = code != null && i < code.Length;
			Color val2 = (Color)(flag ? Accent : new Color(0.55f, 0.57f, 0.62f));
			if (flag)
			{
				Fill(new Color(val2.r, val2.g, val2.b, 0.16f * a), val);
			}
			Frame(new Color(val2.r, val2.g, val2.b, (flag ? 0.85f : 0.35f) * a), val, 1f);
			if (flag)
			{
				string ch = code.Substring(i, 1);
				Label(val, CharContent(ch), styleCode, filled ? Accent : val2, a);
			}
			else if (filled && i == (code?.Length ?? 0))
			{
				float num4 = Pulse01();
				Frame(new Color(1f, 1f, 1f, 0.5f * num4 * a), Grow(val, 1f), 1f);
			}
		}
	}
	private bool Button(Rect r, GUIContent content, float a, float phase)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_014c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0196: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_019b: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01df: Unknown result type (might be due to invalid IL or missing references)
		bool flag = r.Contains(MouseLocal());
		float num = (flag ? 1.5f : 0f);
		bool flag2 = false;
		Event current = Event.current;
		if (flag && current != null && (int)current.type == 0 && current.button == 0)
		{
			flag2 = true;
		}
		if (flag && MenuMouse.Held)
		{
			flag2 = true;
		}
		Rect val = new Rect(r.x, r.y - num + (flag2 ? 1f : 0f), r.width, r.height);
		if (flag)
		{
			Frame(new Color(Accent.r, Accent.g, Accent.b, 0.12f * a), Grow(val, 3f), 1f);
		}
		Fill(new Color(flag ? 0.2f : 0.13f, flag ? 0.19f : 0.13f, flag ? 0.16f : 0.15f, 0.95f * a), val);
		Fill(new Color(1f, 1f, 1f, 0.06f * a), new Rect(val.x + 1f, val.y + 1f, val.width - 2f, 1f));
		Frame(flag ? new Color(Accent.r, Accent.g, Accent.b, a) : new Color(0.44f, 0.46f, 0.5f, 0.9f * a), val, 1f);
		if (flag)
		{
			DrawSheen(val, a, phase);
		}
		Label(val, content, styleButton, flag ? Accent : Normal, a);
		bool flag3 = MenuMouse.Clicked(val);
		return GUI.Button(V(val), GUIContent.none, styleHit) | flag3;
	}
	private void DrawSheen(Rect r, float a, float phase)
	{
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		float num = Mathf.Repeat(clock * 0.55f + phase, 1f);
		float num2 = r.width * 0.34f;
		float num3 = r.x + (num - 0.2f) * r.width;
		float num4 = Mathf.Max(num3, r.x);
		float num5 = Mathf.Min(num3 + num2, r.xMax);
		if (!(num5 <= num4))
		{
			Fill(new Color(1f, 1f, 1f, 0.07f * a), new Rect(num4, r.y, num5 - num4, r.height));
		}
	}
	public static void CopyToClipboard(string text)
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Expected Obj, but got Unknown
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (!string.IsNullOrEmpty(text))
			{
				TextEditor val = new TextEditor
				{
					content = new GUIContent(text)
				};
				val.SelectAll();
				val.Copy();
			}
		}
		catch (Exception)
		{
		}
	}
	private Vector2 MouseLocal()
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		if (MenuMouse.Enabled)
		{
			return MenuMouse.Logical;
		}
		Event current = Event.current;
		if (current == null)
		{
			return new Vector2(-9999f, -9999f);
		}
		if (viewScale <= 0.0001f)
		{
			return current.mousePosition;
		}
		return new Vector2((current.mousePosition.x - viewOffset.x) / viewScale, (current.mousePosition.y - viewOffset.y) / viewScale);
	}
	private static Rect Grow(Rect r, float d)
	{
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		return new Rect(r.x - d, r.y - d, r.width + d * 2f, r.height + d * 2f);
	}
	private void Fill(Color c, Rect r)
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		if (!(r.width <= 0f) && !(r.height <= 0f))
		{
			Rect val = V(r);
			if (!(val.width <= 0f) && !(val.height <= 0f))
			{
				Color color = GUI.color;
				GUI.color = c;
				GUI.DrawTexture(val, (Texture)(object)pixel);
				GUI.color = color;
			}
		}
	}
	private void FillScreen(Color c, Rect s)
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		if (!(s.width <= 0f) && !(s.height <= 0f))
		{
			Color color = GUI.color;
			GUI.color = c;
			GUI.DrawTexture(s, (Texture)(object)pixel);
			GUI.color = color;
		}
	}
	private void Frame(Color c, Rect r, float t)
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		if (!(r.width <= 0f) && !(r.height <= 0f) && !(t <= 0f))
		{
			Fill(c, new Rect(r.x, r.y, r.width, t));
			Fill(c, new Rect(r.x, r.yMax - t, r.width, t));
			Fill(c, new Rect(r.x, r.y + t, t, r.height - t * 2f));
			Fill(c, new Rect(r.xMax - t, r.y + t, t, r.height - t * 2f));
		}
	}
	private void Label(Rect logical, GUIContent content, GUIStyle style, Color color, float a)
	{
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		if (content != null)
		{
			Rect val = V(logical);
			Color color2 = GUI.color;
			GUI.color = new Color(0f, 0f, 0f, 0.85f * a);
			for (int i = 0; i < Outline.Length; i++)
			{
				GUI.Label(new Rect(val.x + Outline[i].x * viewScale, val.y + Outline[i].y * viewScale, val.width, val.height), content, style);
			}
			GUI.color = new Color(color.r, color.g, color.b, a);
			GUI.Label(val, content, style);
			GUI.color = color2;
		}
	}
	private GUIContent BrandText()
	{
		if (brandKey == "\0")
		{
			brandContent.text = "MY SUMMER CAR";
			brandKey = brandContent.text;
		}
		return brandContent;
	}
	private static GUIContent CharContent(string ch)
	{
		charContent.text = ch;
		return charContent;
	}
	private void EnsureStyles()
	{
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0127: Expected Obj, but got Unknown
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Unknown result type (might be due to invalid IL or missing references)
		//IL_0219: Unknown result type (might be due to invalid IL or missing references)
		//IL_0223: Expected Obj, but got Unknown
		//IL_022d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0237: Expected Obj, but got Unknown
		if (!stylesReady || !((UnityEngine.Object)(object)skinBefore == (UnityEngine.Object)(object)GUI.skin))
		{
			skinBefore = GUI.skin;
			GUIStyle label = GUI.skin.label;
			styleBrand = Make(label, 34, (TextAnchor)4);
			styleBrandSub = Make(label, 12, (TextAnchor)4);
			styleRow = Make(label, 21, (TextAnchor)4);
			styleTitle = Make(label, 22, (TextAnchor)3);
			styleSub = Make(label, 12, (TextAnchor)3);
			styleButton = Make(label, 15, (TextAnchor)4);
			styleStatus = Make(label, 13, (TextAnchor)4);
			styleCode = Make(label, 20, (TextAnchor)4);
			styleCodeHint = Make(label, 12, (TextAnchor)3);
			styleFoot = Make(label, 11, (TextAnchor)4);
			styleHint = Make(label, 10, (TextAnchor)4);
			styleCmd = Make(label, 14, (TextAnchor)3);
			styleCmdHint = Make(label, 12, (TextAnchor)3);
			styleLoadTitle = Make(label, 44, (TextAnchor)4);
			styleLoadSub = Make(label, 13, (TextAnchor)4);
			styleHit = new GUIStyle(GUI.skin.button);
			styleHit.normal.background = null;
			styleHit.hover.background = null;
			styleHit.active.background = null;
			styleHit.focused.background = null;
			styleHit.normal.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.hover.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.active.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.focused.textColor = new Color(0f, 0f, 0f, 0f);
			styleHit.padding = new RectOffset(0, 0, 0, 0);
			styleHit.margin = new RectOffset(0, 0, 0, 0);
			stylesReady = true;
		}
	}
	private static GUIStyle Make(GUIStyle root, int size, TextAnchor anchor)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Expected Obj, but got Unknown
		GUIStyle val = new GUIStyle(root)
		{
			font = root.font,
			fontSize = size,
			alignment = anchor
		};
		val.normal.textColor = Color.white;
		val.clipping = (TextClipping)1;
		val.wordWrap = false;
		return val;
	}
	private void EnsureTextures()
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Expected Obj, but got Unknown
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Expected Obj, but got Unknown
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_013a: Expected Obj, but got Unknown
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Unknown result type (might be due to invalid IL or missing references)
		if (!((UnityEngine.Object)(object)pixel != (UnityEngine.Object)null))
		{
			pixel = new Texture2D(1, 1, (TextureFormat)3, false);
			pixel.SetPixel(0, 0, Color.white);
			pixel.Apply();
			((UnityEngine.Object)pixel).hideFlags = (HideFlags)61;
			((Texture)pixel).wrapMode = (TextureWrapMode)1;
			gradient = new Texture2D(1, 128, (TextureFormat)3, false);
			for (int i = 0; i < 128; i++)
			{
				float num = (float)i / 127f;
				Color val = new Color(0.055f, 0.06f, 0.085f);
				Color val2 = new Color(0.085f, 0.082f, 0.08f);
				Color val3 = new Color(0.03f, 0.03f, 0.038f);
				Color val4 = ((num < 0.5f) ? Color.Lerp(val, val2, num * 2f) : Color.Lerp(val2, val3, (num - 0.5f) * 2f));
				gradient.SetPixel(0, i, val4);
			}
			gradient.Apply();
			((UnityEngine.Object)gradient).hideFlags = (HideFlags)61;
			((Texture)gradient).wrapMode = (TextureWrapMode)1;
			beam = new Texture2D(1, 64, (TextureFormat)3, false);
			for (int j = 0; j < 64; j++)
			{
				float num2 = (float)j / 63f;
				beam.SetPixel(0, j, new Color(1f, 1f, 1f, Mathf.Sin(num2 * (float)Math.PI)));
			}
			beam.Apply();
			((UnityEngine.Object)beam).hideFlags = (HideFlags)61;
			((Texture)beam).wrapMode = (TextureWrapMode)1;
		}
	}
	private void EnsureBackground()
	{
		if (backgroundTried)
		{
			return;
		}
		backgroundTried = true;
		string[] array = new string[4] { "menu_bg.jpg", "menu_bg.png", "mysummercar.jpg", "mysummercar.png" };
		string[] array2 = new string[2]
		{
			GameModsFolder(),
			DesktopFolder()
		};
		for (int i = 0; i < array2.Length; i++)
		{
			if (!((UnityEngine.Object)(object)background == (UnityEngine.Object)null))
			{
				break;
			}
			for (int j = 0; j < array.Length && (UnityEngine.Object)(object)background == (UnityEngine.Object)null; j++)
			{
				string text;
				try
				{
					text = Path.Combine(array2[i], array[j]);
				}
				catch
				{
					continue;
				}
				if (FileExistsSafe(text) && TryLoad(text))
				{
					SmpLog.Print("menu background: " + text);
				}
			}
		}
		if ((UnityEngine.Object)(object)background == (UnityEngine.Object)null && TryLoadEmbedded())
		{
			SmpLog.Print("menu background: embedded (SteamMP.menu_bg.jpg)");
		}
		if ((UnityEngine.Object)(object)background == (UnityEngine.Object)null)
		{
			SmpLog.Print("no menu background found; the gradient stays visible.");
		}
	}
	private bool TryLoadEmbedded()
	{
		if ((UnityEngine.Object)(object)background != (UnityEngine.Object)null)
		{
			return true;
		}
		try
		{
			using Stream stream = GetType().Assembly.GetManifestResourceStream("SteamMP.menu_bg.jpg");
			if (stream == null)
			{
				return false;
			}
			long length = stream.Length;
			if (length <= 0 || length > 8388608)
			{
				return false;
			}
			byte[] array = new byte[(int)length];
			int i;
			int num;
			for (i = 0; i < array.Length; i += num)
			{
				num = stream.Read(array, i, array.Length - i);
				if (num <= 0)
				{
					break;
				}
			}
			if (i != array.Length)
			{
				return false;
			}
			return TryLoadBytes(array);
		}
		catch
		{
			return false;
		}
	}
	private static string GameModsFolder()
	{
		try
		{
			return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}
	private static string DesktopFolder()
	{
		try
		{
			return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
		}
		catch
		{
			return string.Empty;
		}
	}
	private static bool FileExistsSafe(string path)
	{
		try
		{
			return !string.IsNullOrEmpty(path) && File.Exists(path);
		}
		catch
		{
			return false;
		}
	}
	private bool TryLoad(string path)
	{
		try
		{
			byte[] bytes = File.ReadAllBytes(path);
			return TryLoadBytes(bytes);
		}
		catch (Exception ex)
		{
			SmpLog.Warning("background '" + path + "' unreadable: " + ex.Message);
			background = null;
			return false;
		}
	}
	private bool TryLoadBytes(byte[] bytes)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Expected Obj, but got Unknown
		try
		{
			if (bytes == null || bytes.Length == 0)
			{
				return false;
			}
			Texture2D val = new Texture2D(2, 2, (TextureFormat)3, false);
			if (!val.LoadImage(bytes))
			{
				UnityEngine.Object.Destroy((UnityEngine.Object)(object)val);
				return false;
			}
			((Texture)val).wrapMode = (TextureWrapMode)1;
			((Texture)val).filterMode = (FilterMode)1;
			background = val;
			return true;
		}
		catch
		{
			background = null;
			return false;
		}
	}
	private static float Ease(float t)
	{
		if (t <= 0f)
		{
			return 0f;
		}
		if (t >= 1f)
		{
			return 1f;
		}
		return t * (2f - t);
	}
	private static float Pulse01()
	{
		return 0.5f + 0.5f * Mathf.Sin(ClockValue * 3.2f);
	}
	private Color Pulse(Color c)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		Color result = c;
		result.a = 0.82f + 0.18f * Pulse01();
		return result;
	}
	public MenuUi()
	{
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Expected Obj, but got Unknown
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Expected Obj, but got Unknown
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Expected Obj, but got Unknown
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		//IL_011a: Expected Obj, but got Unknown
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Expected Obj, but got Unknown
	}
	static MenuUi()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Expected Obj, but got Unknown
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Expected Obj, but got Unknown
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Expected Obj, but got Unknown
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Expected Obj, but got Unknown
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Expected Obj, but got Unknown
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Expected Obj, but got Unknown
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Expected Obj, but got Unknown
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0103: Expected Obj, but got Unknown
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Expected Obj, but got Unknown
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Expected Obj, but got Unknown
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Expected Obj, but got Unknown
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_013f: Expected Obj, but got Unknown
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Expected Obj, but got Unknown
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Expected Obj, but got Unknown
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_016c: Expected Obj, but got Unknown
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Expected Obj, but got Unknown
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Expected Obj, but got Unknown
		//IL_018f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Expected Obj, but got Unknown
		//IL_019e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a8: Expected Obj, but got Unknown
	}
	}
}
