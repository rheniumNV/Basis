using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Interactions;
using Cilbox;
using TMPro;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// Builds the share panel prop (Assets/SharePanel/SharePanel.prefab) from code: the board, the
/// list rows, the large view and every component wired up. Run it again after changing the
/// layout here; the prefab keeps its asset id and its ContentGroupId.
/// </summary>
[AutoStaticsCleanup]
public static partial class SharePanelBuilder
{
    const string Root = "Assets/SharePanel";
    const string Generated = Root + "/Generated";
    const string PrefabPath = Root + "/SharePanel.prefab";
    const string JapaneseFontPath = "Packages/com.basis.sdk/Fonts/NotoSansJP-Regular.ttf";
    const int InteractableLayer = 8;
    const int RowCount = 8;

    // Board, in metres; the face looks along -Z. The list takes the wider left part, the large
    // view the right.
    const float Width = 1.95f;
    const float Height = 1.275f;
    const float Face = -0.012f;
    const float TextZ = -0.016f;
    const float RowHeight = 0.105f;
    const float RowWidth = 1.17f;
    const float FirstRowY = 0.39f;
    const float ActionWidth = 0.105f;
    const float ActionGap = 0.006f;

    static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

    [MenuItem("Basis/Tools/Share Panel/Build Prefab")]
    public static void Build()
    {
        materials.Clear();
        EnsureFolder(Generated);
        EnsureFolder(Generated + "/Materials");
        TMP_FontAsset font = JapaneseFont();
        string contentGroupId = ExistingContentGroupId();

        GameObject root = new GameObject("Share Panel");
        root.layer = InteractableLayer;
        BasisProp prop = root.AddComponent<BasisProp>();
        prop.BasisBundleDescription = new BasisBundleDescription
        {
            AssetBundleName = "Share Panel",
            AssetBundleDescription = "Lists the photos, links, text, servers and orbs shared on the server, with who shared them and when, for everyone to look through together.",
            ContentGroupId = contentGroupId,
        };
        root.AddComponent<CilboxPropBasis>();

        // Grabbed anywhere on the board (the buttons sit in front of it); it stays where it is let
        // go. The panel's move switch turns this collider off.
        BoxCollider grab = root.AddComponent<BoxCollider>();
        grab.center = new Vector3(0f, -0.04f, 0f);
        grab.size = new Vector3(Width + 0.04f, Height + 0.12f, 0.02f);
        Rigidbody body = root.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        BasisPickupInteractable pickup = root.AddComponent<BasisPickupInteractable>();
        pickup.RigidRef = body;
        pickup.InteractRange = 3f;
        pickup.GenerateColliderMesh = false;
        BasisPickupSyncNetworking sync = root.AddComponent<BasisPickupSyncNetworking>();
        sync.BasisPickupInteractable = pickup;
        sync.Target = root.transform;

        Color wood = Hex("8B6B4E");
        Color paper = Hex("FBF8F2");
        Color ink = Hex("3D4148");
        Color faint = Hex("7D838C");
        float top = Height * 0.5f;
        float left = -Width * 0.5f;

        Box(root.transform, "Board", Vector3.zero, new Vector3(Width, Height, 0.02f), Mat("Board", paper));
        Box(root.transform, "Frame Top", new Vector3(0f, top, 0f), new Vector3(Width + 0.04f, 0.03f, 0.035f), Mat("Frame", wood));
        Box(root.transform, "Frame Bottom", new Vector3(0f, -top, 0f), new Vector3(Width + 0.04f, 0.03f, 0.035f), Mat("Frame", wood));
        Box(root.transform, "Frame Left", new Vector3(left, 0f, 0f), new Vector3(0.03f, Height, 0.035f), Mat("Frame", wood));
        Box(root.transform, "Frame Right", new Vector3(-left, 0f, 0f), new Vector3(0.03f, Height, 0.035f), Mat("Frame", wood));
        Box(root.transform, "Handle", new Vector3(0f, -top - 0.07f, 0f), new Vector3(0.6f, 0.05f, 0.05f), Mat("Handle", Hex("5E4632")));
        Box(root.transform, "Handle Post L", new Vector3(-0.25f, -top - 0.03f, 0f), new Vector3(0.025f, 0.06f, 0.025f), Mat("Frame", wood));
        Box(root.transform, "Handle Post R", new Vector3(0.25f, -top - 0.03f, 0f), new Vector3(0.025f, 0.06f, 0.025f), Mat("Frame", wood));

        SharePanel panel = root.AddComponent<SharePanel>();
        SharePanelSource source = root.AddComponent<SharePanelSource>();
        source.Panel = panel;
        panel.GrabCollider = grab;
        panel.KindColors = new[]
        {
            Hex("F2A65A"), // photo
            Hex("5B8DEF"), // link
            Hex("8E9AAF"), // text
            Hex("4CB97F"), // world
            Hex("B07FE0"), // prop
            Hex("E36F8E"), // avatar
            Hex("A0A0A0"), // other
            Hex("CC66FF"), // server
        };

        // Header: title and count on the left; language and the move switch on the right.
        float headerY = top - 0.065f;
        panel.TitleText = Text(root.transform, "Title", font, "", 0.75f, new Vector2(0.5f, 0.1f), ink, TextAlignmentOptions.Left, true);
        panel.TitleText.fontStyle = FontStyles.Bold;
        Place(panel.TitleText, new Vector3(left + 0.28f, headerY, TextZ));
        panel.CountText = Text(root.transform, "Count", font, "", 0.4f, new Vector2(0.3f, 0.07f), faint, TextAlignmentOptions.Left, true);
        Place(panel.CountText, new Vector3(left + 0.68f, headerY - 0.005f, TextZ));
        panel.LanguageJaButton = LabelledButton(root.transform, font, "Language Ja", "日本語", new Vector3(-left - 0.66f, headerY, Face), 0.15f, 0.06f, out _);
        panel.LanguageEnButton = LabelledButton(root.transform, font, "Language En", "English", new Vector3(-left - 0.5f, headerY, Face), 0.15f, 0.06f, out _);
        panel.MoveButton = LabelledButton(root.transform, font, "Move", "", new Vector3(-left - 0.2f, headerY, Face), 0.3f, 0.06f, out TextMeshPro moveLabel);
        panel.MoveLabel = moveLabel;

        panel.StatusText = Text(root.transform, "Status", font, "", 0.36f, new Vector2(Width - 0.08f, 0.06f), Hex("A0522D"), TextAlignmentOptions.Center, true);
        Place(panel.StatusText, new Vector3(0f, -top + 0.045f, TextZ));

        // Type filter above the list.
        float listLeft = left + 0.03f;
        float filterY = top - 0.155f;
        // All, prop, avatar, world, server, link, text, photo.
        panel.FilterKinds = new[] { -1, 4, 5, 3, 7, 1, 2, 0 };
        panel.FilterButtons = new BasisInteractableButton[panel.FilterKinds.Length];
        panel.FilterLabels = new TextMeshPro[panel.FilterKinds.Length];
        panel.FilterLamps = new MeshRenderer[panel.FilterKinds.Length];
        for (int i = 0; i < panel.FilterKinds.Length; i++)
        {
            panel.FilterButtons[i] = LabelledButton(root.transform, font, "Filter " + i, "", new Vector3(listLeft + 0.07f + i * 0.145f, filterY, Face), 0.135f, 0.06f, out TextMeshPro label);
            label.fontSize = 0.27f;
            Place(label, new Vector3(0f, 0.005f, -0.0075f));
            panel.FilterLabels[i] = label;
            // The lamp along the bottom of the button: the kind's colour when on, grey when off.
            GameObject lamp = Box(panel.FilterButtons[i].transform, "Lamp", new Vector3(0f, -0.021f, -0.0072f), new Vector3(0.115f, 0.009f, 0.002f), Mat("Lamp", Color.white));
            panel.FilterLamps[i] = lamp.GetComponent<MeshRenderer>();
        }

        // List.
        panel.Rows = new SharePanelRow[RowCount];
        for (int i = 0; i < RowCount; i++)
        {
            panel.Rows[i] = BuildRow(root.transform, font, panel, i, new Vector3(listLeft, FirstRowY - i * RowHeight, 0f));
        }
        float listCenter = listLeft + RowWidth * 0.5f;
        float pagerY = FirstRowY - RowCount * RowHeight + 0.02f;
        panel.PrevPageButton = LabelledButton(root.transform, font, "Prev Page", "＜", new Vector3(listCenter - 0.15f, pagerY, Face), 0.08f, 0.05f, out _);
        panel.NextPageButton = LabelledButton(root.transform, font, "Next Page", "＞", new Vector3(listCenter + 0.15f, pagerY, Face), 0.08f, 0.05f, out _);
        panel.PageText = Text(root.transform, "Page", font, "", 0.36f, new Vector2(0.18f, 0.07f), faint, TextAlignmentOptions.Center, true);
        Place(panel.PageText, new Vector3(listCenter, pagerY, TextZ));

        // Large view on the right.
        float previewLeft = listLeft + RowWidth + 0.035f;
        float previewWidth = -left - 0.03f - previewLeft;
        float previewX = previewLeft + previewWidth * 0.5f;
        float previewTop = top - 0.12f;
        float previewBottom = -top + 0.09f;
        float previewY = (previewTop + previewBottom) * 0.5f;
        float inner = previewWidth - 0.05f;
        Box(root.transform, "Preview Background", new Vector3(previewX, previewY, -0.011f), new Vector3(previewWidth, previewTop - previewBottom, 0.002f), Mat("Preview", Hex("EEF1F5")));
        panel.PreviewTitle = Text(root.transform, "Preview Title", font, "", 0.46f, new Vector2(inner, 0.08f), ink, TextAlignmentOptions.Left, false);
        panel.PreviewTitle.fontStyle = FontStyles.Bold;
        panel.PreviewTitle.overflowMode = TextOverflowModes.Ellipsis;
        panel.PreviewTitle.textWrappingMode = TextWrappingModes.NoWrap;
        Place(panel.PreviewTitle, new Vector3(previewX, previewTop - 0.045f, TextZ));
        panel.PreviewMeta = Text(root.transform, "Preview Meta", font, "", 0.3f, new Vector2(inner, 0.06f), faint, TextAlignmentOptions.Left, false);
        panel.PreviewMeta.overflowMode = TextOverflowModes.Ellipsis;
        panel.PreviewMeta.textWrappingMode = TextWrappingModes.NoWrap;
        Place(panel.PreviewMeta, new Vector3(previewX, previewTop - 0.09f, TextZ));

        float bodyTop = previewTop - 0.125f;
        float bodyBottom = previewBottom + 0.08f;
        float bodyY = (bodyTop + bodyBottom) * 0.5f;
        panel.PreviewBody = Text(root.transform, "Preview Body", font, "", 0.3f, new Vector2(inner, bodyTop - bodyBottom), ink, TextAlignmentOptions.TopLeft, false);
        panel.PreviewBody.overflowMode = TextOverflowModes.Truncate;
        Place(panel.PreviewBody, new Vector3(previewX, bodyY, TextZ));
        // Measured on the board: about 0.031 m per full-width character and 0.0415 m per line.
        panel.PreviewLineUnits = Mathf.Floor(inner / 0.031f) - 0.5f;
        panel.PreviewLines = Mathf.FloorToInt((bodyTop - bodyBottom) / 0.0415f) - 1;

        GameObject image = Quad(root.transform, "Preview Image", ImageMaterial());
        image.transform.localPosition = new Vector3(previewX, bodyY, TextZ);
        panel.PreviewImage = image.GetComponent<MeshRenderer>();
        panel.PreviewImageWidth = inner;
        panel.PreviewImageHeight = bodyTop - bodyBottom;
        image.SetActive(false);

        float previewPagerY = previewBottom + 0.04f;
        panel.PreviewPrevButton = LabelledButton(root.transform, font, "Preview Prev", "＜", new Vector3(previewX - 0.15f, previewPagerY, Face), 0.08f, 0.05f, out _);
        panel.PreviewNextButton = LabelledButton(root.transform, font, "Preview Next", "＞", new Vector3(previewX + 0.15f, previewPagerY, Face), 0.08f, 0.05f, out _);
        panel.PreviewPageText = Text(root.transform, "Preview Page", font, "", 0.34f, new Vector2(0.16f, 0.07f), faint, TextAlignmentOptions.Center, true);
        Place(panel.PreviewPageText, new Vector3(previewX, previewPagerY, TextZ));

        SetLayer(root.transform, InteractableLayer);
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        Debug.Log("Share Panel: built " + PrefabPath);
    }

    // A row, laid out from its left edge: type chip or thumbnail, title over "type・who・when",
    // then up to three action buttons.
    static SharePanelRow BuildRow(Transform parent, TMP_FontAsset font, SharePanel panel, int index, Vector3 position)
    {
        Transform rowRoot = new GameObject("Row " + index).transform;
        rowRoot.SetParent(parent, false);
        rowRoot.localPosition = position;
        SharePanelRow row = rowRoot.gameObject.AddComponent<SharePanelRow>();
        row.Panel = panel;
        row.Index = index;

        Transform content = new GameObject("Content").transform;
        content.SetParent(rowRoot, false);
        row.Content = content.gameObject;

        float actionsLeft = RowWidth - 3f * ActionWidth - 2f * ActionGap;
        float selectWidth = actionsLeft - 0.01f;
        // The row's face is its select button: press it to show the item large for everyone.
        row.SelectButton = Button(content, "Select", new Vector3(selectWidth * 0.5f, 0f, Face), new Vector3(selectWidth, RowHeight - 0.01f, 0.004f),
            Hex("FFFFFF"), Hex("EAF1FF"), Hex("D6E4FF"));

        GameObject mark = Box(content, "Selected", new Vector3(-0.007f, 0f, Face - 0.003f), new Vector3(0.008f, RowHeight - 0.02f, 0.002f), Mat("Accent", Hex("5B8DEF")));
        row.SelectedMark = mark;
        mark.SetActive(false);

        float chipX = 0.055f;
        GameObject chip = Box(content, "Chip", new Vector3(chipX, 0f, Face - 0.004f), new Vector3(0.09f, 0.075f, 0.002f), Mat("Chip", Color.white));
        row.Chip = chip.GetComponent<MeshRenderer>();
        row.KindText = Text(content, "Kind", font, "", 0.2f, new Vector2(0.088f, 0.05f), Color.white, TextAlignmentOptions.Center, true);
        row.KindText.fontStyle = FontStyles.Bold;
        row.KindText.textWrappingMode = TextWrappingModes.NoWrap;
        Place(row.KindText, new Vector3(chipX, 0f, Face - 0.0055f));

        GameObject thumb = Quad(content, "Thumb", ImageMaterial());
        thumb.transform.localPosition = new Vector3(chipX, 0f, Face - 0.0045f);
        row.Thumb = thumb.GetComponent<MeshRenderer>();
        row.ThumbSize = 0.085f;
        thumb.SetActive(false);

        // Text boxes taller than a line: Ellipsis drops a first line that does not fit the height.
        float textLeft = 0.115f;
        float textWidth = selectWidth - textLeft - 0.01f;
        float textX = textLeft + textWidth * 0.5f;
        row.Title = Text(content, "Title", font, "", 0.32f, new Vector2(textWidth, 0.06f), Hex("3D4148"), TextAlignmentOptions.Left, false);
        row.Title.textWrappingMode = TextWrappingModes.NoWrap;
        row.Title.overflowMode = TextOverflowModes.Ellipsis;
        Place(row.Title, new Vector3(textX, 0.016f, Face - 0.004f));
        row.Meta = Text(content, "Meta", font, "", 0.24f, new Vector2(textWidth, 0.05f), Hex("7D838C"), TextAlignmentOptions.Left, false);
        row.Meta.textWrappingMode = TextWrappingModes.NoWrap;
        row.Meta.overflowMode = TextOverflowModes.Ellipsis;
        Place(row.Meta, new Vector3(textX, -0.022f, Face - 0.004f));

        row.ActionButtons = new BasisInteractableButton[3];
        row.ActionLabels = new TextMeshPro[3];
        for (int i = 0; i < 3; i++)
        {
            float x = actionsLeft + ActionWidth * 0.5f + i * (ActionWidth + ActionGap);
            row.ActionButtons[i] = LabelledButton(content, font, "Action " + (i + 1), "", new Vector3(x, 0f, Face), ActionWidth, 0.07f, out TextMeshPro label);
            label.fontSize = 0.24f;
            row.ActionLabels[i] = label;
        }
        return row;
    }

    // ---------------------------------------------------------------- parts

    static BasisInteractableButton Button(Transform parent, string name, Vector3 position, Vector3 size, Color color, Color hover, Color press)
    {
        Transform root = new GameObject(name).transform;
        root.SetParent(parent, false);
        root.localPosition = position;
        GameObject cap = Box(root, "Cap", new Vector3(0f, 0f, 0f), size, Mat(name + " Button", color));
        cap.AddComponent<BoxCollider>();
        BasisInteractableButton button = root.gameObject.AddComponent<BasisInteractableButton>();
        button.ColliderRef = cap.GetComponent<Collider>();
        button.RendererRef = cap.GetComponent<MeshRenderer>();
        button.PropertyName = "_BaseColor";
        button.Color = color;
        button.HoverColor = hover;
        button.InteractColor = press;
        button.DisabledColor = Hex("D0D0D0");
        button.InteractRange = 2.5f;
        return button;
    }

    static BasisInteractableButton LabelledButton(Transform parent, TMP_FontAsset font, string name, string text, Vector3 position, float width, float height, out TextMeshPro label)
    {
        BasisInteractableButton button = Button(parent, name, position, new Vector3(width, height, 0.012f), Hex("A8D8B9"), Hex("C6EBD3"), Hex("86C29C"));
        label = Text(button.transform, "Label", font, text, 0.34f, new Vector2(width - 0.006f, height), Hex("2F4F3A"), TextAlignmentOptions.Center, true);
        label.textWrappingMode = TextWrappingModes.NoWrap;
        Place(label, new Vector3(0f, 0f, -0.0075f));
        return button;
    }

    static GameObject Box(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = size;
        MeshRenderer renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    static GameObject Quad(Transform parent, string name, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        MeshRenderer renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return go;
    }

    static TextMeshPro Text(Transform parent, string name, TMP_FontAsset font, string text, float size, Vector2 rect, Color color, TextAlignmentOptions alignment, bool richText)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        TextMeshPro tmp = go.AddComponent<TextMeshPro>();
        tmp.font = font;
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        // Text other people shared is shown as written, never as markup.
        tmp.richText = richText;
        tmp.rectTransform.sizeDelta = rect;
        return tmp;
    }

    // A TextMeshPro's RectTransform saves anchoredPosition: set it, or x and y reset on reload.
    static void Place(TextMeshPro text, Vector3 localPosition)
    {
        RectTransform rect = text.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition3D = localPosition;
        rect.localPosition = localPosition;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        foreach (Transform child in t) SetLayer(child, layer);
    }

    static Color Hex(string hex)
    {
        ColorUtility.TryParseHtmlString("#" + hex, out Color c);
        return c;
    }

    // ---------------------------------------------------------------- assets

    static Material Mat(string name, Color color)
    {
        if (materials.TryGetValue(name, out Material cached)) return cached;
        string path = $"{Generated}/Materials/{name}.mat";
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        else if (m.shader != shader)
        {
            m.shader = shader;
        }
        if (m.GetColor("_BaseColor") != color) m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", 0.1f);
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        materials[name] = m;
        return m;
    }

    // Unlit, so photos look as they were taken; the texture comes from a property block.
    static Material ImageMaterial()
    {
        if (materials.TryGetValue("Image", out Material cached)) return cached;
        string path = $"{Generated}/Materials/Image.mat";
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(shader) { name = "Image" };
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", Color.white);
        EditorUtility.SetDirty(m);
        materials["Image"] = m;
        return m;
    }

    /// <summary>A dynamic TMP font asset over Noto Sans JP, so any shared text and player name can show.</summary>
    static TMP_FontAsset JapaneseFont()
    {
        string path = $"{Generated}/NotoSansJP SDF.asset";
        TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (existing != null)
        {
            UseWorldTextShader(existing);
            return existing;
        }
        Font source = AssetDatabase.LoadAssetAtPath<Font>(JapaneseFontPath);
        TMP_FontAsset font = TMP_FontAsset.CreateFontAsset(source, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
        font.name = "NotoSansJP SDF";
        AssetDatabase.CreateAsset(font, path);
        font.atlasTextures[0].name = "NotoSansJP SDF Atlas";
        AssetDatabase.AddObjectToAsset(font.atlasTextures[0], font);
        font.material.name = "NotoSansJP SDF Material";
        AssetDatabase.AddObjectToAsset(font.material, font);
        // The panel's own words, baked so they show before the dynamic atlas fills.
        font.TryAddCharacters("サーバー接続すべて日本語English移動オンオフServerJoinAllMoveonoff種類たった今分前justminsago:パスワード付き名前設定してくださいもう一度押すとに移動しますPressagaintomove共有物この種類の共有はありませんNothingofthiskindみんなの共有Sharedhere件itemsまだ何も共有されていませんNothingyet写真リンクテキストワールドプロップアバターその他PhotoLinkTextWorldPropAvatarOther手元にコピー保存開くBringCopySaveOpen一覧の項目を押すとここに大きく表示しますPressaniteminthelisttoshowit読み込み中Loading共有物を読むにはこのプロップのスクリプトを許可してくださいAllowthisprop'sscriptstolistwhatis＜＞・/…0123456789");
        UseWorldTextShader(font);
        EditorUtility.SetDirty(font);
        AssetDatabase.SaveAssets();
        return font;
    }

    /// <summary>
    /// TMP's shaders take their depth test from a UI-only global that URP leaves off, so 3D text
    /// would draw through everything. The panel's copy of the shader depth-tests normally.
    /// </summary>
    static void UseWorldTextShader(TMP_FontAsset font)
    {
        Shader shader = Shader.Find("SharePanel/TMP Distance Field (World)");
        if (shader == null || font.material.shader == shader) return;
        font.material.shader = shader;
        EditorUtility.SetDirty(font.material);
        AssetDatabase.SaveAssets();
    }

    /// <summary>The id the SDK gave the prop on its first bundle build, read from the prefab being replaced.</summary>
    static string ExistingContentGroupId()
    {
        BasisProp existing = AssetDatabase.LoadAssetAtPath<BasisProp>(PrefabPath);
        return existing != null ? existing.BasisBundleDescription.ContentGroupId : null;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
