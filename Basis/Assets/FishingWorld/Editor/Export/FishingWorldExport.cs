using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Export;
using GLTFast.Logging;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Writes the world's look to a GLB for other platforms: only the static scenery and the
// boards' frames, no text, effects, rods or scripts. Every material becomes plain colour PBR,
// and the colours can be washed towards white, since other engines light the scene darker
// than Basis does with its fog and ambient light.
public class FishingWorldExport : EditorWindow
{
    const string ScenePath = "Assets/FishingWorld/FishingWorld.unity";
    const string WorldName = "FishingWorld";
    const string LightnessKey = "FishingWorldExport.Lightness";
    const string PathKey = "FishingWorldExport.Path";
    // Parts of the world that are not scenery.
    static readonly string[] SkippedChildren = { "SpawnPoint", "Background Music", "Fishing Game", "Rods" };

    float lightness;
    string path;
    bool exporting;
    string status = "";

    [MenuItem("Basis/Tools/Fishing World/Export GLB")]
    static void Open()
    {
        GetWindow<FishingWorldExport>("Fishing World Export");
    }

    void OnEnable()
    {
        lightness = EditorPrefs.GetFloat(LightnessKey, 0f);
        path = EditorPrefs.GetString(PathKey, Path.GetFullPath(Path.Combine(Application.dataPath, "../../Exports/FishingWorld.glb")));
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("薄さ（色を白に寄せる割合）", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        lightness = EditorGUILayout.Slider(lightness, 0f, 1f);
        if (EditorGUI.EndChangeCheck()) EditorPrefs.SetFloat(LightnessKey, lightness);
        DrawSwatches();

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        path = EditorGUILayout.TextField("書き出し先", path);
        if (GUILayout.Button("...", GUILayout.Width(28)))
        {
            string chosen = EditorUtility.SaveFilePanel("Export GLB", Path.GetDirectoryName(path), Path.GetFileName(path), "glb");
            if (!string.IsNullOrEmpty(chosen)) path = chosen;
        }
        if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PathKey, path);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(exporting))
        {
            if (GUILayout.Button(exporting ? "書き出し中…" : "GLB を書き出す", GUILayout.Height(28))) _ = Run();
        }
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
    }

    // A few of the world's colours before and after, to judge the slider without exporting.
    void DrawSwatches()
    {
        Color[] samples = { Hex("8FD18A"), Hex("5AA9E6"), Hex("73C493"), Hex("C38A4F"), Hex("F6BFD0") };
        Rect row = GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true));
        float w = row.width / samples.Length;
        for (int i = 0; i < samples.Length; i++)
        {
            Rect cell = new Rect(row.x + i * w + 2f, row.y, w - 4f, row.height / 2f);
            EditorGUI.DrawRect(cell, samples[i]);
            cell.y += row.height / 2f;
            EditorGUI.DrawRect(cell, Lighten(samples[i], lightness));
        }
        EditorGUILayout.LabelField("上：元の色　下：書き出す色", EditorStyles.miniLabel);
    }

    static Color Hex(string hex)
    {
        ColorUtility.TryParseHtmlString("#" + hex, out Color c);
        return c;
    }

    // Blends towards white in sRGB, which is how the eye judges "lighter"; alpha is kept.
    public static Color Lighten(Color color, float amount)
    {
        Color c = Color.Lerp(color, Color.white, amount);
        c.a = color.a;
        return c;
    }

    async Task Run()
    {
        exporting = true;
        status = "";
        try
        {
            bool ok = await Export(path, lightness);
            status = ok ? "書き出しました: " + path : "書き出しに失敗しました（Console を確認してください）";
        }
        finally
        {
            exporting = false;
            Repaint();
        }
    }

    // Exports a cleaned copy of the world; the scene itself is left untouched.
    public static async Task<bool> Export(string path, float lightness)
    {
        if (EditorSceneManager.GetActiveScene().path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            EditorSceneManager.OpenScene(ScenePath);
        }
        GameObject world = GameObject.Find(WorldName);
        if (world == null)
        {
            Debug.LogError("Fishing World Export: no '" + WorldName + "' object in " + ScenePath);
            return false;
        }

        GameObject copy = Instantiate(world);
        copy.name = WorldName;
        var temporary = new List<Material>();
        try
        {
            Strip(copy);
            ReplaceMaterials(copy, lightness, temporary);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var export = new GameObjectExport(
                new ExportSettings
                {
                    Format = GltfFormat.Binary,
                    FileConflictResolution = FileConflictResolution.Overwrite,
                    ComponentMask = ComponentType.Mesh,
                },
                new GameObjectExportSettings { OnlyActiveInHierarchy = true },
                // glTFast throws without a logger when it has a warning to give.
                logger: new ConsoleLogger());
            export.AddScene(new[] { copy }, WorldName);
            return await export.SaveToFileAndDispose(path);
        }
        finally
        {
            DestroyImmediate(copy);
            foreach (Material m in temporary) DestroyImmediate(m);
        }
    }

    static void Strip(GameObject copy)
    {
        foreach (string name in SkippedChildren)
        {
            Transform t = copy.transform.Find(name);
            if (t != null) DestroyImmediate(t.gameObject);
        }
        // Text can't go to glTF as text, and effects are scripted.
        foreach (var text in copy.GetComponentsInChildren<TMPro.TMP_Text>(true)) if (text) DestroyImmediate(text.gameObject);
        foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true)) if (particles) DestroyImmediate(particles.gameObject);
        foreach (var line in copy.GetComponentsInChildren<LineRenderer>(true)) if (line) DestroyImmediate(line.gameObject);
        // The "scripts are not allowed" sign means nothing elsewhere.
        foreach (var t in copy.GetComponentsInChildren<Transform>(true)) if (t && t.name.Contains("Warning")) DestroyImmediate(t.gameObject);
    }

    // Swaps every material for a plain URP Lit copy with the (lightened) base colour, so the
    // file has colour and roughness only: no unlit extension, nothing engine-specific.
    static void ReplaceMaterials(GameObject copy, float lightness, List<Material> temporary)
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        var replaced = new Dictionary<Material, Material>();
        foreach (var renderer in copy.GetComponentsInChildren<MeshRenderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material source = materials[i];
                if (source == null) continue;
                if (!replaced.TryGetValue(source, out Material plain))
                {
                    plain = source.shader == lit ? new Material(source) : new Material(lit);
                    plain.name = source.name;
                    Color color = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : Color.white;
                    plain.SetColor("_BaseColor", Lighten(color, lightness));
                    if (source.shader != lit) plain.SetFloat("_Smoothness", 0.05f);
                    replaced.Add(source, plain);
                    temporary.Add(plain);
                }
                materials[i] = plain;
            }
            renderer.sharedMaterials = materials;
        }
    }
}
