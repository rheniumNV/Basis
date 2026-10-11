using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using Cilbox;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Builds the REST client prop and the scene to try it in.
///
/// <b>Build Prefab</b> writes RestClientProp.prefab from code, so the panel layout lives in one
/// readable place and can be regenerated after editing it here.
///
/// The prefab carries its own CilboxPropBasis host, so its script is sandboxed with the prop
/// whitelist whether it is placed in a scene or built into a bundle.
///
/// <b>Test Scene</b> opens the initialization scene (the one that spawns a player onto a floor)
/// and drops the prop in front of the spawn INACTIVE, with a <see cref="BasisRestClientPropTestSpawner"/>
/// that activates it the way a spawn does. Placed active, the host would start its scripts with the
/// scene, before Cilbox consent can ask, and consent refuses anything it never got to ask about.
/// Nothing is saved.
/// </summary>
public static class BasisRestClientPropBuilder
{
    const string PrefabPath = "Assets/RestClientProp/RestClientProp.prefab";
    const string InitializationScene = "Packages/com.basis.framework/Scenes/initialization.unity";
    const int UILayer = 5;

    static readonly Vector2 PanelSize = new Vector2(640f, 520f);
    static readonly Vector3 SpawnOffset = new Vector3(0f, 1.4f, 1.5f);

    static readonly Color PanelColor = new Color(0.11f, 0.12f, 0.15f, 0.96f);
    static readonly Color ButtonColor = new Color(0.22f, 0.25f, 0.32f, 1f);
    static readonly Color MethodColor = new Color(0.18f, 0.36f, 0.62f, 1f);
    static readonly Color LockColor = new Color(0.35f, 0.08f, 0.1f, 0.97f);

    [MenuItem("Basis/Tools/REST Client Prop/Build Prefab", false, 100)]
    public static void BuildPrefab()
    {
        GameObject root = BuildHierarchy();
        try
        {
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            if (success) Debug.Log($"[RestClientProp] Wrote {PrefabPath}");
            else Debug.LogError($"[RestClientProp] Failed to write {PrefabPath}");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [MenuItem("Basis/Tools/REST Client Prop/Test Scene", false, 101)]
    public static void BuildTestScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null) BuildPrefab();
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

        Scene scene = EditorSceneManager.OpenScene(InitializationScene, OpenSceneMode.Single);

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        instance.transform.position = SpawnOffset;
        instance.SetActive(false);

        GameObject spawnerGo = new GameObject("REST Client Prop Spawner (test only)");
        SceneManager.MoveGameObjectToScene(spawnerGo, scene);
        spawnerGo.AddComponent<BasisRestClientPropTestSpawner>().Prop = instance;

        Selection.activeGameObject = instance;
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[RestClientProp] Test scene ready (unsaved). Enter Play mode; a few seconds in the prop spawns and the script-permission prompt should ask for Internet access.");
    }

    static GameObject BuildHierarchy()
    {
        GameObject root = new GameObject("REST Client Prop");
        BasisProp prop = root.AddComponent<BasisProp>();
        prop.BasisBundleDescription = new BasisBundleDescription
        {
            AssetBundleName = "REST Client",
            AssetBundleDescription = "A small REST client panel (GET/POST/PUT/DELETE) for trying Cilbox script permissions. Needs Internet access.",
        };
        root.AddComponent<CilboxPropBasis>();

        // Canvas: world space, 1 canvas unit = 1 mm.
        GameObject canvasGo = new GameObject("Canvas", typeof(RectTransform));
        canvasGo.layer = UILayer;
        canvasGo.transform.SetParent(root.transform, false);
        RectTransform canvasRect = (RectTransform)canvasGo.transform;
        canvasRect.sizeDelta = PanelSize;
        canvasRect.localScale = Vector3.one * 0.001f;

        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 4f;
        BasisGraphicUIRayCaster raycaster = canvasGo.AddComponent<BasisGraphicUIRayCaster>();
        raycaster.Canvas = canvas;
        BasisUIComponent uiComponent = canvasGo.AddComponent<BasisUIComponent>();
        uiComponent.Canvas = canvas;
        uiComponent.CanvasScaler = scaler;
        uiComponent.GraphicUIRayCaster = raycaster;

        Panel(canvasRect, "Background", Vector2.zero, PanelSize, PanelColor);

        BasisRestClientProp client = root.AddComponent<BasisRestClientProp>();

        Label(canvasRect, "Title", new Vector2(0f, 230f), new Vector2(600f, 44f), "REST Client", 34, TextAlignmentOptions.Center, FontStyles.Bold);

        Label(canvasRect, "Endpoint Caption", new Vector2(0f, 186f), new Vector2(600f, 24f), "Presets", 18, TextAlignmentOptions.Left, FontStyles.Normal);
        client.EndpointAButton = MakeButton(canvasRect, "Endpoint A", new Vector2(-200f, 150f), new Vector2(190f, 44f), "httpbin /anything", ButtonColor, 18);
        client.EndpointBButton = MakeButton(canvasRect, "Endpoint B", new Vector2(0f, 150f), new Vector2(190f, 44f), "jsonplaceholder", ButtonColor, 18);
        client.EndpointCButton = MakeButton(canvasRect, "Endpoint C", new Vector2(200f, 150f), new Vector2(190f, 44f), "httpbin 418", ButtonColor, 18);

        client.UrlInput = MakeInputField(canvasRect, "Url", new Vector2(0f, 108f), new Vector2(600f, 36f), "https://...", 18);

        client.GetButton = MakeButton(canvasRect, "GET", new Vector2(-228f, 62f), new Vector2(136f, 50f), "GET", MethodColor, 24);
        client.PostButton = MakeButton(canvasRect, "POST", new Vector2(-76f, 62f), new Vector2(136f, 50f), "POST", MethodColor, 24);
        client.PutButton = MakeButton(canvasRect, "PUT", new Vector2(76f, 62f), new Vector2(136f, 50f), "PUT", MethodColor, 24);
        client.DeleteButton = MakeButton(canvasRect, "DELETE", new Vector2(228f, 62f), new Vector2(136f, 50f), "DELETE", MethodColor, 24);

        client.StatusText = Label(canvasRect, "Status", new Vector2(0f, 14f), new Vector2(600f, 32f), "", 22, TextAlignmentOptions.Left, FontStyles.Bold);

        Panel(canvasRect, "Body Background", new Vector2(0f, -122f), new Vector2(600f, 236f), new Color(0.06f, 0.07f, 0.09f, 1f));
        client.BodyText = Label(canvasRect, "Body", new Vector2(0f, -122f), new Vector2(584f, 224f), "", 13, TextAlignmentOptions.TopLeft, FontStyles.Normal);
        client.BodyText.overflowMode = TextOverflowModes.Truncate;
        client.BodyText.textWrappingMode = TextWrappingModes.Normal;

        // Lock overlay: on by default, covering (and blocking clicks to) everything above.
        // Only the script takes it down, once it has been allowed to touch UnityWebRequest.
        Image lockImage = Panel(canvasRect, "Locked Overlay", Vector2.zero, PanelSize, LockColor);
        lockImage.raycastTarget = true;
        RectTransform lockRect = (RectTransform)lockImage.transform;
        Label(lockRect, "Lock Title", new Vector2(0f, 60f), new Vector2(600f, 70f), "LOCKED", 56, TextAlignmentOptions.Center, FontStyles.Bold);
        Label(lockRect, "Lock Body", new Vector2(0f, -40f), new Vector2(560f, 120f),
            "This prop needs Internet access.\nAllow it in the script permission prompt,\nor it stays locked.",
            24, TextAlignmentOptions.Center, FontStyles.Normal);
        client.LockedOverlay = lockImage.gameObject;

        return root;
    }

    static Image Panel(RectTransform parent, string name, Vector2 position, Vector2 size, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = UILayer;
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        Image image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static TextMeshProUGUI Label(RectTransform parent, string name, Vector2 position, Vector2 size, string text, float fontSize, TextAlignmentOptions alignment, FontStyles style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = UILayer;
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.fontStyle = style;
        label.color = Color.white;
        label.raycastTarget = false;
        return label;
    }

    static TMP_InputField MakeInputField(RectTransform parent, string name, Vector2 position, Vector2 size, string placeholder, float fontSize)
    {
        Image background = Panel(parent, name, position, size, new Color(0.06f, 0.07f, 0.09f, 1f));
        background.raycastTarget = true;
        RectTransform rect = (RectTransform)background.transform;

        GameObject viewportGo = new GameObject("Text Area", typeof(RectTransform));
        viewportGo.layer = UILayer;
        RectTransform viewport = (RectTransform)viewportGo.transform;
        viewport.SetParent(rect, false);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(10f, 4f);
        viewport.offsetMax = new Vector2(-10f, -4f);
        viewportGo.AddComponent<RectMask2D>();

        TextMeshProUGUI hint = Label(viewport, "Placeholder", Vector2.zero, Vector2.zero, placeholder, fontSize, TextAlignmentOptions.MidlineLeft, FontStyles.Italic);
        Stretch(hint.rectTransform);
        hint.color = new Color(1f, 1f, 1f, 0.35f);

        TextMeshProUGUI text = Label(viewport, "Text", Vector2.zero, Vector2.zero, "", fontSize, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        Stretch(text.rectTransform);
        text.color = new Color(0.7f, 0.8f, 1f);
        text.textWrappingMode = TextWrappingModes.NoWrap;

        TMP_InputField input = background.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = background;
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = hint;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.contentType = TMP_InputField.ContentType.Standard;
        input.characterLimit = 0;
        input.pointSize = fontSize;
        return input;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    static Button MakeButton(RectTransform parent, string name, Vector2 position, Vector2 size, string text, Color color, float fontSize)
    {
        Image image = Panel(parent, name, position, size, color);
        image.raycastTarget = true;
        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
        colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        button.colors = colors;
        Label((RectTransform)image.transform, "Label", Vector2.zero, size, text, fontSize, TextAlignmentOptions.Center, FontStyles.Bold);
        return button;
    }
}
