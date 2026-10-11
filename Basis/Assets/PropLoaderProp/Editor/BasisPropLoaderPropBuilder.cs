using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using Cilbox;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static BasisRestClientPropBuilder;

/// <summary>
/// Builds the prop loader prop and the scene to try it in, the same way
/// <see cref="BasisRestClientPropBuilder"/> does for the REST client (and with its UI helpers).
///
/// The test scene needs a server connection to actually spawn anything: load requests go
/// through the server like any networked prop.
/// </summary>
public static class BasisPropLoaderPropBuilder
{
    const string PrefabPath = "Assets/PropLoaderProp/PropLoaderProp.prefab";
    const string InitializationScene = "Packages/com.basis.framework/Scenes/initialization.unity";
    const int UILayer = 5;

    static readonly Vector2 PanelSize = new Vector2(640f, 420f);
    static readonly Vector3 SpawnOffset = new Vector3(0f, 1.4f, 1.5f);

    static readonly Color PanelColor = new Color(0.11f, 0.12f, 0.15f, 0.96f);
    static readonly Color ButtonColor = new Color(0.22f, 0.25f, 0.32f, 1f);
    static readonly Color ActionColor = new Color(0.18f, 0.36f, 0.62f, 1f);
    static readonly Color LockColor = new Color(0.35f, 0.08f, 0.1f, 0.97f);

    [MenuItem("Basis/Tools/Prop Loader Prop/Build Prefab", false, 110)]
    public static void BuildPrefab()
    {
        GameObject root = BuildHierarchy();
        try
        {
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            if (success) Debug.Log($"[PropLoaderProp] Wrote {PrefabPath}");
            else Debug.LogError($"[PropLoaderProp] Failed to write {PrefabPath}");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [MenuItem("Basis/Tools/Prop Loader Prop/Test Scene", false, 111)]
    public static void BuildTestScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null) BuildPrefab();
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

        Scene scene = EditorSceneManager.OpenScene(InitializationScene, OpenSceneMode.Single);

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        instance.transform.position = SpawnOffset;
        instance.SetActive(false);

        GameObject spawnerGo = new GameObject("Prop Loader Prop Spawner (test only)");
        SceneManager.MoveGameObjectToScene(spawnerGo, scene);
        spawnerGo.AddComponent<BasisRestClientPropTestSpawner>().Prop = instance;

        Selection.activeGameObject = instance;
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[PropLoaderProp] Test scene ready (unsaved). Enter Play mode while connected to a server; a few seconds in the prop spawns and the script-permission prompt should ask about spawning.");
    }

    static GameObject BuildHierarchy()
    {
        GameObject root = new GameObject("Prop Loader");
        BasisProp prop = root.AddComponent<BasisProp>();
        prop.BasisBundleDescription = new BasisBundleDescription
        {
            AssetBundleName = "Prop Loader",
            AssetBundleDescription = "Loads another prop from a BEE file URL and its password, for everyone in the room. Needs permission to spawn content.",
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

        BasisPropLoaderProp loader = root.AddComponent<BasisPropLoaderProp>();

        Label(canvasRect, "Title", new Vector2(0f, 172f), new Vector2(600f, 44f), "Prop Loader", 34, TextAlignmentOptions.Center, FontStyles.Bold);

        Label(canvasRect, "Url Caption", new Vector2(0f, 126f), new Vector2(600f, 24f), "BEE file URL", 18, TextAlignmentOptions.Left, FontStyles.Normal);
        loader.UrlInput = MakeInputField(canvasRect, "Url", new Vector2(0f, 94f), new Vector2(600f, 36f), "https://.../xxxx.BEE", 18);

        Label(canvasRect, "Password Caption", new Vector2(0f, 52f), new Vector2(600f, 24f), "Password", 18, TextAlignmentOptions.Left, FontStyles.Normal);
        loader.PasswordInput = MakeInputField(canvasRect, "Password", new Vector2(0f, 20f), new Vector2(600f, 36f), "Bundle password", 18);
        loader.PasswordInput.contentType = TMP_InputField.ContentType.Password;

        loader.LoadButton = MakeButton(canvasRect, "Load", new Vector2(-152f, -46f), new Vector2(296f, 56f), "LOAD", ActionColor, 26);
        loader.UnloadButton = MakeButton(canvasRect, "Unload", new Vector2(152f, -46f), new Vector2(296f, 56f), "UNLOAD", ButtonColor, 26);

        loader.StatusText = Label(canvasRect, "Status", new Vector2(0f, -132f), new Vector2(600f, 80f), "", 20, TextAlignmentOptions.TopLeft, FontStyles.Bold);
        loader.StatusText.textWrappingMode = TextWrappingModes.Normal;

        // Lock overlay: on by default, covering (and blocking clicks to) everything above.
        // Only the script takes it down, once it has been allowed to use the spawn API.
        Image lockImage = Panel(canvasRect, "Locked Overlay", Vector2.zero, PanelSize, LockColor);
        lockImage.raycastTarget = true;
        RectTransform lockRect = (RectTransform)lockImage.transform;
        Label(lockRect, "Lock Title", new Vector2(0f, 60f), new Vector2(600f, 70f), "LOCKED", 56, TextAlignmentOptions.Center, FontStyles.Bold);
        Label(lockRect, "Lock Body", new Vector2(0f, -40f), new Vector2(560f, 120f),
            "This prop needs permission to spawn content.\nAllow it in the script permission prompt,\nor it stays locked.",
            24, TextAlignmentOptions.Center, FontStyles.Normal);
        loader.LockedOverlay = lockImage.gameObject;

        return root;
    }
}
