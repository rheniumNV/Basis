using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Cilbox;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using Unity.Scripting.LifecycleManagement;

/// <summary>
/// Builds the fishing world scene from code: a soft low-poly forest around a pond, a rod stand
/// with a button just ahead of the spawn, a catch board, and a pool of rods for the button to
/// hand out.
///
/// Everything it makes (meshes, materials, sounds, the Japanese font asset and the scene) goes
/// under Assets/FishingWorld and is overwritten on every build, so tweak this file, not the
/// generated assets. The layout is seeded, so a rebuild comes out the same.
/// </summary>
[AutoStaticsCleanup]
public static partial class FishingWorldBuilder
{
    const string Root = "Assets/FishingWorld";
    const string Generated = Root + "/Generated";
    const string ScenePath = Root + "/FishingWorld.unity";
    const string JapaneseFontPath = "Packages/com.basis.sdk/Fonts/NotoSansJP-Regular.ttf";

    // Pond: dug into the ground around the origin. Water shows inside about 6.8 m.
    const float PondDigRadius = 9f;
    const float PondDepth = 1.4f;
    const float WaterLevel = -0.2f;
    const float FishingRadius = 6.6f;

    const int RodCount = 30;
    const int RodSlots = 6;
    // Basis' layer for pickups: collides with the world and other pickups, not with players.
    const int InteractableLayer = 8;

    static readonly Vector3 SpawnPosition = new Vector3(0f, 0f, -19f);
    static readonly Vector3 StandPosition = new Vector3(2.4f, 0f, -15f);
    static readonly Vector3 BoardPosition = new Vector3(5.2f, 0f, -15.6f);

    static readonly Vector3 ZukanPosition = new Vector3(-1.9f, 0f, -15.2f);

    const string FishDataPath = Root + "/Data/fish.json";
    const string BackendDataPath = Root + "/Data/backend.json";

    // The fish table and the server settings, edited by hand in Data/ (see Data/README.md).
    [System.Serializable]
    class FishData
    {
        public float rewardSizeBonus = 1f;
        public float crownGoldPercent = 5f;
        public float crownSilverPercent = 10f;
        public FishEntry[] fish;
    }

    [System.Serializable]
    class FishEntry
    {
        public string name;
        public float weight;
        public float minCm;
        public float maxCm;
        public bool rare;
        public int baseExp;
        public int baseMoney;
        public FishModel model;
    }

    [System.Serializable]
    class FishModel
    {
        public string type = "fish";
        public float lengthCm = 30f;
        public float width = 0.06f;
        public float height = 0.09f;
        public string body = "8E9C5E";
        public string belly = "DCD5A3";
        public string fin = "6F7D45";
        public string patch = "";
        public string stripe = "";
        public bool whiskers;
    }

    [System.Serializable]
    class BackendData
    {
        public string apiBaseUrl = "";
        public int rankingLimit = 10;
        public float rankingRefreshSeconds = 60f;
        public string editorTestUserId = "";
    }

    static FishData fishData;

    static FishData LoadFishData()
    {
        FishData data = JsonUtility.FromJson<FishData>(File.ReadAllText(FishDataPath));
        if (data == null || data.fish == null || data.fish.Length == 0) throw new System.Exception(FishDataPath + ": no fish");
        foreach (FishEntry fish in data.fish)
        {
            if (string.IsNullOrEmpty(fish.name)) throw new System.Exception(FishDataPath + ": a fish has no name");
            if (fish.maxCm < fish.minCm || fish.minCm <= 0f) throw new System.Exception(FishDataPath + ": " + fish.name + " needs 0 < minCm <= maxCm");
            if (fish.weight < 0f) throw new System.Exception(FishDataPath + ": " + fish.name + " has a negative weight");
            if (fish.model == null) fish.model = new FishModel();
        }
        return data;
    }

    static Dictionary<string, Material> materials;
    static Mesh blobA, blobB, blobC, cone, prism6, prism24;

    [MenuItem("Basis/Tools/Fishing World/Build Scene", false, 120)]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        BuildWithoutPrompt();
    }

    /// <summary>Same as Build Scene, without asking to save the open scene first.</summary>
    public static void BuildWithoutPrompt()
    {
        EnsureFolder(Root, "Generated");
        EnsureFolder(Generated, "Meshes");
        EnsureFolder(Generated, "Materials");
        EnsureFolder(Generated, "Audio");

        materials = new Dictionary<string, Material>();
        blobA = SaveMesh(FishingWorldMeshes.Blob("Blob A", 1, 0.12f, 11));
        blobB = SaveMesh(FishingWorldMeshes.Blob("Blob B", 1, 0.16f, 23));
        blobC = SaveMesh(FishingWorldMeshes.Blob("Blob C", 0, 0.08f, 37));
        cone = SaveMesh(FishingWorldMeshes.Cone("Cone", 7));
        prism6 = SaveMesh(FishingWorldMeshes.Prism("Prism 6", 6));
        prism24 = SaveMesh(FishingWorldMeshes.Prism("Prism 24", 24));
        Mesh groundMesh = SaveMesh(FishingWorldMeshes.Ground("Ground", 44f, 1.25f, GroundHeight, 5));

        AudioClip plop = SaveSound("plop", Synth.Plop());
        AudioClip splash = SaveSound("splash", Synth.Splash());
        AudioClip fanfare = SaveSound("catch", Synth.Fanfare());
        AudioClip escape = SaveSound("escape", Synth.Escape());
        AudioClip click = SaveSound("click", Synth.Click());
        AudioClip music = SaveSound("forest-bgm", FishingWorldMusic.Render(), FishingWorldMusic.Rate);
        SetMusicImport(music);

        fishData = LoadFishData();
        BackendData backend = JsonUtility.FromJson<BackendData>(File.ReadAllText(BackendDataPath));
        TMP_FontAsset font = JapaneseFont();
        string contentGroupId = ExistingContentGroupId();

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SetupLighting();

        // World root: the Basis scene descriptor and the Cilbox host for the world scripts.
        GameObject world = new GameObject("FishingWorld");
        BasisScene basisScene = world.AddComponent<BasisScene>();
        basisScene.BasisBundleDescription = new BasisBundleDescription
        {
            AssetBundleName = "Forest Pond Fishing",
            AssetBundleDescription = "A soft low-poly forest pond. Grab a rod from the stand and fish.",
            // Kept across rebuilds so new bundles stay the same world.
            ContentGroupId = contentGroupId,
        };
        basisScene.RespawnHeight = -20f;
        world.AddComponent<CilboxSceneBasis>();

        Transform spawn = new GameObject("SpawnPoint").transform;
        spawn.SetParent(world.transform, false);
        spawn.position = OnGround(SpawnPosition);
        spawn.rotation = Quaternion.identity;
        basisScene.SpawnPoint = spawn;

        Transform environment = Group(world.transform, "Environment");
        GameObject ground = MeshObject(environment, "Ground", groundMesh, Mat("Grass", Hex("9ED68A")), Vector3.zero, Vector3.one, Quaternion.identity);
        ground.AddComponent<MeshCollider>().sharedMesh = groundMesh;
        ground.isStatic = true;

        BuildPond(environment);
        BuildDock(environment);
        BuildForest(environment);
        BuildDecorations(environment);
        BuildClouds(environment);
        BuildMusic(world.transform, music);

        FishingWorldController controller = BuildStand(world.transform, font, click);
        BuildBoard(world.transform, font, controller);
        BuildZukanBoard(world.transform, font, controller);

        FishingBackend server = controller.gameObject.AddComponent<FishingBackend>();
        server.Controller = controller;
        server.ApiBaseUrl = (backend.apiBaseUrl ?? "").Trim().TrimEnd('/');
        server.RankingLimit = backend.rankingLimit;
        server.RankingRefreshSeconds = backend.rankingRefreshSeconds;
        server.EditorTestUserId = backend.editorTestUserId ?? "";
        controller.BackendExpected = server.ApiBaseUrl.Length > 0 ? 1 : 0;

        Transform rods = Group(world.transform, "Rods");
        controller.Rods = new FishingRod[RodCount];
        for (int i = 0; i < RodCount; i++)
        {
            controller.Rods[i] = BuildRod(rods, i, controller, plop, splash, fanfare, escape);
        }

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[FishingWorld] Built {ScenePath}");
    }

    /// <summary>The id the SDK gave the world on its first bundle build, read from the scene being replaced.</summary>
    static string ExistingContentGroupId()
    {
        if (!File.Exists(ScenePath)) return null;
        Scene previous = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        BasisScene.SceneTraversalFindBasisScene(previous, out BasisScene existing);
        return existing != null ? existing.BasisBundleDescription.ContentGroupId : null;
    }

    // ---------------------------------------------------------------- terrain

    /// <summary>Ground height: flat around the pond, gently rolling further out, hills at the edge.</summary>
    public static float GroundHeight(float x, float z)
    {
        float r = Mathf.Sqrt(x * x + z * z);
        float roll = 0.35f * Mathf.Sin(x * 0.21f + 1.3f) * Mathf.Cos(z * 0.17f) + 0.2f * Mathf.Sin(x * 0.5f + z * 0.37f);
        float h = roll * Mathf.Clamp01((r - 11f) / 10f);
        if (r > 28f) h += (r - 28f) * 0.45f;
        if (r < PondDigRadius)
        {
            float t = 1f - r / PondDigRadius;
            h -= PondDepth * t * t * (3f - 2f * t) * 1.6f;
            h = Mathf.Max(h, -PondDepth);
        }
        return h;
    }

    static Vector3 OnGround(Vector3 p)
    {
        return new Vector3(p.x, GroundHeight(p.x, p.z), p.z);
    }

    // ---------------------------------------------------------------- scene parts

    static void SetupLighting()
    {
        GameObject sun = new GameObject("Sun");
        Light light = sun.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Hex("FFF0D8");
        light.intensity = 1.25f;
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.55f;
        sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = Hex("CFE6F5");
        RenderSettings.ambientEquatorColor = Hex("D6EAD0");
        RenderSettings.ambientGroundColor = Hex("A4BC92");
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.014f;
        RenderSettings.fogColor = Hex("DCEFE6");
        RenderSettings.sun = light;

        Material sky = SaveMaterial("Sky", new Material(Shader.Find("Skybox/Procedural")));
        sky.SetColor("_SkyTint", Hex("A8D8F0"));
        sky.SetColor("_GroundColor", Hex("C9E3C0"));
        sky.SetFloat("_AtmosphereThickness", 0.6f);
        sky.SetFloat("_Exposure", 1.25f);
        sky.SetFloat("_SunSize", 0.03f);
        RenderSettings.skybox = sky;
    }

    static void BuildPond(Transform parent)
    {
        Transform pond = Group(parent, "Pond");
        Material water = WaterMaterial();
        MeshObject(pond, "Water", prism24, water, new Vector3(0f, WaterLevel - 0.02f, 0f), new Vector3(PondDigRadius, 0.04f, PondDigRadius), Quaternion.identity);

        System.Random random = new System.Random(3);
        // Lily pads, a few with a pink flower.
        Material pad = Mat("Lily Pad", Hex("7CC48A"));
        Material flower = Mat("Lily Flower", Hex("F7B8CF"));
        for (int i = 0; i < 9; i++)
        {
            float a = (float)random.NextDouble() * Mathf.PI * 2f;
            float r = 2.5f + (float)random.NextDouble() * 3.6f;
            Vector3 p = new Vector3(Mathf.Cos(a) * r, WaterLevel + 0.01f, Mathf.Sin(a) * r);
            float size = 0.3f + (float)random.NextDouble() * 0.25f;
            MeshObject(pond, "Lily Pad", prism6, pad, p, new Vector3(size, 0.02f, size), Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f));
            if (i % 3 == 0)
            {
                MeshObject(pond, "Lily Flower", blobC, flower, p + Vector3.up * 0.06f, new Vector3(0.09f, 0.06f, 0.09f), Quaternion.identity);
            }
        }

        // Round stones along the shore.
        Material[] stones = { Mat("Stone", Hex("C9C3B6")), Mat("Stone Light", Hex("DAD5CA")), Mat("Stone Dark", Hex("AFA99D")) };
        for (int i = 0; i < 34; i++)
        {
            float a = i / 34f * Mathf.PI * 2f + (float)random.NextDouble() * 0.12f;
            // Leave the dock end of the shore clear.
            if (Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, 270f)) < 12f) continue;
            float r = 6.7f + (float)random.NextDouble() * 0.9f;
            Vector3 p = OnGround(new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            float s = 0.25f + (float)random.NextDouble() * 0.35f;
            MeshObject(pond, "Stone", random.Next(2) == 0 ? blobA : blobC, stones[random.Next(stones.Length)], p, new Vector3(s * 1.3f, s * 0.7f, s), Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f));
        }

        // Reeds.
        Material reed = Mat("Reed", Hex("8FBF6A"));
        Material cattail = Mat("Cattail", Hex("A3754E"));
        for (int i = 0; i < 7; i++)
        {
            float a = (float)random.NextDouble() * Mathf.PI * 2f;
            if (Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, 270f)) < 25f) continue;
            float r = 6.2f + (float)random.NextDouble() * 0.6f;
            Vector3 c = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = c + new Vector3((float)random.NextDouble() - 0.5f, 0f, (float)random.NextDouble() - 0.5f) * 0.6f;
                p.y = Mathf.Max(GroundHeight(p.x, p.z), WaterLevel - 0.3f);
                float h = 0.9f + (float)random.NextDouble() * 0.6f;
                Quaternion lean = Quaternion.Euler(((float)random.NextDouble() - 0.5f) * 14f, 0f, ((float)random.NextDouble() - 0.5f) * 14f);
                Box(pond, "Reed", p + lean * Vector3.up * (h * 0.5f), new Vector3(0.03f, h, 0.03f), lean, reed, false);
                Box(pond, "Cattail", p + lean * Vector3.up * (h - 0.05f), new Vector3(0.06f, 0.18f, 0.06f), lean, cattail, false);
            }
        }
    }

    static void BuildDock(Transform parent)
    {
        Transform dock = Group(parent, "Dock");
        Material plank = Mat("Plank", Hex("D9B48A"));
        Material plankDark = Mat("Plank Dark", Hex("C49B73"));
        Material post = Mat("Post", Hex("A57E5A"));
        const float deckY = 0.12f;
        const float startZ = -9.2f, endZ = -4.6f;
        int i = 0;
        for (float z = startZ; z < endZ; z += 0.32f, i++)
        {
            Box(dock, "Plank", new Vector3(0f, deckY, z + 0.15f), new Vector3(1.6f, 0.07f, 0.28f), Quaternion.Euler(0f, (i % 3 - 1) * 0.8f, 0f), i % 2 == 0 ? plank : plankDark, true);
        }
        float[] postZ = { startZ + 0.2f, -6.9f, endZ - 0.1f };
        foreach (float z in postZ)
        {
            foreach (float x in new[] { -0.75f, 0.75f })
            {
                Box(dock, "Post", new Vector3(x, deckY - 0.7f, z), new Vector3(0.14f, 1.7f, 0.14f), Quaternion.identity, post, true);
            }
        }
    }

    static void BuildForest(Transform parent)
    {
        Transform forest = Group(parent, "Forest");
        System.Random random = new System.Random(42);
        List<Vector3> placed = new List<Vector3>();

        // The tree that shades the rod stand.
        Vector3 shade = StandPosition + new Vector3(1.6f, 0f, 1.6f);
        Tree(forest, OnGround(shade), 1.35f, 0, random, 0);
        placed.Add(shade);

        int made = 0;
        for (int attempt = 0; attempt < 2000 && made < 85; attempt++)
        {
            float a = (float)random.NextDouble() * Mathf.PI * 2f;
            float r = 11f + Mathf.Sqrt((float)random.NextDouble()) * 26f;
            Vector3 p = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);

            // Keep the walk from the spawn to the dock, the stand and the board open.
            if (Mathf.Abs(p.x) < 3.2f && p.z < -6f && p.z > -24f) continue;
            if ((p - StandPosition).sqrMagnitude < 2.6f * 2.6f) continue;
            if ((p - BoardPosition).sqrMagnitude < 2.2f * 2.2f) continue;
            if ((p - ZukanPosition).sqrMagnitude < 2.2f * 2.2f) continue;
            if ((p - SpawnPosition).sqrMagnitude < 4f * 4f) continue;

            bool tooClose = false;
            float spacing = r < 20f ? 3.2f : 2.6f;
            foreach (Vector3 q in placed)
            {
                if ((p - q).sqrMagnitude < spacing * spacing) { tooClose = true; break; }
            }
            if (tooClose) continue;

            placed.Add(p);
            float scale = 0.8f + (float)random.NextDouble() * 0.6f + Mathf.Clamp01((r - 20f) / 15f) * 0.4f;
            int kind = random.NextDouble() < 0.3 ? 1 : 0;
            Tree(forest, OnGround(p), scale, kind, random, made + 1);
            made++;
        }
    }

    static readonly string[] LeafColors = { "8FD18A", "A9E4A0", "73C493", "BFE69A", "9AD8B0" };
    static readonly string[] AccentLeafColors = { "F6BFD0", "FFD8A8" };

    static void Tree(Transform parent, Vector3 basePosition, float scale, int kind, System.Random random, int index)
    {
        Transform tree = new GameObject("Tree " + index).transform;
        tree.SetParent(parent, false);
        tree.position = basePosition;
        tree.rotation = Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
        tree.localScale = Vector3.one * scale;

        Material trunk = Mat("Trunk", Hex("B08661"));
        float trunkHeight = kind == 0 ? 2.2f : 1.4f;
        GameObject trunkGo = Box(tree, "Trunk", Vector3.zero, new Vector3(0.38f, trunkHeight, 0.38f), Quaternion.identity, trunk, true);
        trunkGo.transform.localPosition = new Vector3(0f, trunkHeight * 0.5f - 0.1f, 0f);

        if (kind == 0)
        {
            // Round tree: a few lumpy blobs stacked together.
            string color = random.NextDouble() < 0.12 ? AccentLeafColors[random.Next(AccentLeafColors.Length)] : LeafColors[random.Next(LeafColors.Length)];
            Material leaves = Mat("Leaves " + color, Hex(color));
            int blobs = 2 + random.Next(2);
            for (int i = 0; i < blobs; i++)
            {
                float size = 1.4f + (float)random.NextDouble() * 0.6f - i * 0.2f;
                Vector3 offset = new Vector3(((float)random.NextDouble() - 0.5f) * 0.9f, trunkHeight + 0.5f + i * 0.75f, ((float)random.NextDouble() - 0.5f) * 0.9f);
                GameObject blob = MeshObject(tree, "Leaves", random.Next(2) == 0 ? blobA : blobB, leaves, Vector3.zero, Vector3.one * size, Quaternion.Euler(0f, random.Next(360), 0f));
                blob.transform.localPosition = offset;
                blob.transform.localScale = new Vector3(size, size * 0.85f, size);
            }
        }
        else
        {
            // Pine: three stacked cones.
            Material needles = Mat("Pine", Hex(random.Next(2) == 0 ? "6FB98F" : "82C79A"));
            for (int i = 0; i < 3; i++)
            {
                float radius = 1.35f - i * 0.33f;
                GameObject layer = MeshObject(tree, "Pine", cone, needles, Vector3.zero, Vector3.one, Quaternion.identity);
                layer.transform.localPosition = new Vector3(0f, trunkHeight - 0.2f + i * 0.85f, 0f);
                layer.transform.localScale = new Vector3(radius, 1.5f, radius);
                layer.transform.localRotation = Quaternion.Euler(0f, i * 25f, 0f);
            }
        }
    }

    static void BuildDecorations(Transform parent)
    {
        Transform decor = Group(parent, "Decorations");
        System.Random random = new System.Random(7);

        Material[] bushes = { Mat("Bush", Hex("86CF8E")), Mat("Bush Light", Hex("A6DE9B")) };
        Material[] petals = { Mat("Flower Pink", Hex("F9B5C9")), Mat("Flower Yellow", Hex("FBE38E")), Mat("Flower White", Hex("FFFFFF")), Mat("Flower Lilac", Hex("C9B6F2")) };
        Material stem = Mat("Stem", Hex("6DB46A"));
        Material capRed = Mat("Mushroom Cap", Hex("F28B82"));
        Material capTan = Mat("Mushroom Cap Tan", Hex("E8C49A"));
        Material stalk = Mat("Mushroom Stalk", Hex("FFF6E8"));

        for (int i = 0; i < 260; i++)
        {
            float a = (float)random.NextDouble() * Mathf.PI * 2f;
            float r = 7.8f + (float)random.NextDouble() * 24f;
            Vector3 p = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            bool onPath = Mathf.Abs(p.x) < 1.6f && p.z < -6f && p.z > -22f;
            if ((p - StandPosition).sqrMagnitude < 1.5f * 1.5f || (p - BoardPosition).sqrMagnitude < 1.5f * 1.5f || (p - ZukanPosition).sqrMagnitude < 1.6f * 1.6f) continue;
            p = OnGround(p);
            double kind = random.NextDouble();
            if (kind < 0.3)
            {
                if (onPath) continue;
                float s = 0.5f + (float)random.NextDouble() * 0.6f;
                MeshObject(decor, "Bush", random.Next(2) == 0 ? blobA : blobB, bushes[random.Next(2)], p + Vector3.up * s * 0.25f, new Vector3(s * 1.3f, s * 0.8f, s), Quaternion.Euler(0f, random.Next(360), 0f));
            }
            else if (kind < 0.85)
            {
                // A little cluster of flowers.
                Material petal = petals[random.Next(petals.Length)];
                int n = 2 + random.Next(4);
                for (int k = 0; k < n; k++)
                {
                    Vector3 q = p + new Vector3((float)random.NextDouble() - 0.5f, 0f, (float)random.NextDouble() - 0.5f) * 0.5f;
                    q.y = GroundHeight(q.x, q.z);
                    float h = 0.12f + (float)random.NextDouble() * 0.12f;
                    Box(decor, "Stem", q + Vector3.up * h * 0.5f, new Vector3(0.02f, h, 0.02f), Quaternion.identity, stem, false);
                    Box(decor, "Flower", q + Vector3.up * h, new Vector3(0.07f, 0.04f, 0.07f), Quaternion.Euler(0f, random.Next(90), 0f), petal, false);
                }
            }
            else
            {
                if (onPath) continue;
                float s = 0.6f + (float)random.NextDouble() * 0.8f;
                Transform m = new GameObject("Mushroom").transform;
                m.SetParent(decor, false);
                m.position = p;
                m.localScale = Vector3.one * s;
                GameObject st = MeshObject(m, "Stalk", prism6, stalk, Vector3.zero, Vector3.one, Quaternion.identity);
                st.transform.localPosition = new Vector3(0f, 0.08f, 0f);
                st.transform.localScale = new Vector3(0.05f, 0.16f, 0.05f);
                GameObject cap = MeshObject(m, "Cap", blobC, random.Next(3) == 0 ? capTan : capRed, Vector3.zero, Vector3.one, Quaternion.identity);
                cap.transform.localPosition = new Vector3(0f, 0.17f, 0f);
                cap.transform.localScale = new Vector3(0.16f, 0.09f, 0.16f);
            }
        }
    }

    static void BuildClouds(Transform parent)
    {
        Transform clouds = Group(parent, "Clouds");
        Material white = Unlit("Cloud", Hex("FFFFFF"));
        System.Random random = new System.Random(99);
        for (int i = 0; i < 14; i++)
        {
            float a = (float)random.NextDouble() * Mathf.PI * 2f;
            float r = 20f + (float)random.NextDouble() * 45f;
            Vector3 c = new Vector3(Mathf.Cos(a) * r, 20f + (float)random.NextDouble() * 10f, Mathf.Sin(a) * r);
            Transform cloud = new GameObject("Cloud").transform;
            cloud.SetParent(clouds, false);
            cloud.position = c;
            int puffs = 3 + random.Next(3);
            for (int k = 0; k < puffs; k++)
            {
                float s = 2.5f + (float)random.NextDouble() * 2.5f;
                GameObject puff = MeshObject(cloud, "Puff", blobB, white, Vector3.zero, Vector3.one, Quaternion.identity);
                puff.transform.localPosition = new Vector3((k - puffs * 0.5f) * 2.4f, (float)random.NextDouble() * 1.2f, ((float)random.NextDouble() - 0.5f) * 2f);
                puff.transform.localScale = new Vector3(s, s * 0.6f, s * 0.8f);
            }
        }
    }

    // Background music: heard the same everywhere (2D) and deliberately quiet; Basis routes it
    // through the world volume when the scene loads.
    const float MusicVolume = 0.06f;

    static void BuildMusic(Transform parent, AudioClip clip)
    {
        GameObject go = new GameObject("Background Music");
        go.transform.SetParent(parent, false);
        AudioSource source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = true;
        source.playOnAwake = true;
        source.spatialBlend = 0f;
        source.volume = MusicVolume;
        source.dopplerLevel = 0f;
        source.priority = 32;
    }

    static FishingWorldController BuildStand(Transform parent, TMP_FontAsset font, AudioClip click)
    {
        Transform stand = Group(parent, "Rod Stand");
        stand.position = OnGround(StandPosition);
        Material wood = Mat("Stand Wood", Hex("E2BE92"));
        Material woodDark = Mat("Stand Wood Dark", Hex("BF946B"));

        // Table: rods come out lying on it, pointing along the table.
        Local(Box(stand, "Table Top", Vector3.zero, new Vector3(2.4f, 0.08f, 0.9f), Quaternion.identity, wood, true), new Vector3(0f, 0.82f, 0f));
        foreach (float x in new[] { -1.1f, 1.1f })
        {
            foreach (float z in new[] { -0.36f, 0.36f })
            {
                Local(Box(stand, "Leg", Vector3.zero, new Vector3(0.09f, 0.82f, 0.09f), Quaternion.identity, woodDark, true), new Vector3(x, 0.41f, z));
            }
        }

        // Back rack: a display rod hangs on two hooks, the button sits above it.
        Local(Box(stand, "Rack Post", Vector3.zero, new Vector3(0.1f, 1.9f, 0.1f), Quaternion.identity, woodDark, true), new Vector3(-1.15f, 0.95f, 0.5f));
        Local(Box(stand, "Rack Post", Vector3.zero, new Vector3(0.1f, 1.9f, 0.1f), Quaternion.identity, woodDark, true), new Vector3(1.15f, 0.95f, 0.5f));
        Local(Box(stand, "Rack Board", Vector3.zero, new Vector3(2.4f, 0.95f, 0.05f), Quaternion.identity, wood, true), new Vector3(0f, 1.42f, 0.52f));
        Local(Box(stand, "Rack Roof", Vector3.zero, new Vector3(2.6f, 0.08f, 0.45f), Quaternion.Euler(-12f, 0f, 0f), Mat("Roof", Hex("F2A7A0")), true), new Vector3(0f, 1.95f, 0.38f));
        foreach (float x in new[] { -0.7f, 0.7f })
        {
            Local(Box(stand, "Hook", Vector3.zero, new Vector3(0.04f, 0.04f, 0.12f), Quaternion.identity, woodDark, false), new Vector3(x, 1.12f, 0.44f));
        }
        Transform display = RodModel(stand, "Display Rod");
        display.localPosition = new Vector3(-1.05f, 1.16f, 0.42f);
        display.localRotation = Quaternion.Euler(0f, 90f, 0f);

        // Button above the display rod.
        Local(Box(stand, "Button Base", Vector3.zero, new Vector3(0.26f, 0.26f, 0.06f), Quaternion.identity, woodDark, false), new Vector3(0f, 1.52f, 0.48f));
        GameObject button = Box(stand, "Rod Button", Vector3.zero, new Vector3(0.17f, 0.17f, 0.08f), Quaternion.identity, Mat("Button", Hex("FF8A8A")), true);
        Local(button, new Vector3(0f, 1.52f, 0.42f));
        BasisInteractableButton interactable = button.AddComponent<BasisInteractableButton>();
        interactable.ColliderRef = button.GetComponent<Collider>();
        interactable.RendererRef = button.GetComponent<MeshRenderer>();
        interactable.PropertyName = "_BaseColor";
        interactable.Color = Hex("FF8A8A");
        interactable.HoverColor = Hex("FFB0A8");
        interactable.InteractColor = Hex("E66A6A");
        interactable.DisabledColor = Hex("C8C8C8");
        interactable.InteractRange = 2.5f;

        TextMeshPro title = Text(stand, "Stand Title", font, "釣り竿", 2.6f, new Vector2(1.6f, 0.3f), Hex("6B4A2E"));
        Place(title, new Vector3(0f, 1.78f, 0.485f));
        TextMeshPro hint = Text(stand, "Stand Hint", font, "", 1.3f, new Vector2(2.2f, 0.2f), Hex("6B4A2E"));
        Place(hint, new Vector3(0f, 1.27f, 0.485f));
        TextMeshPro note = Text(stand, "Permission Note", font, "", 0.85f, new Vector2(2.2f, 0.12f), Hex("B5651D"));
        Place(note, new Vector3(0f, 1.0f, 0.485f));

        // In front of the rack, over the button: up unless the world's scripts start (they hide it),
        // so it needs no script to say that they are not running.
        Transform warning = new GameObject("Script Warning").transform;
        warning.SetParent(stand, false);
        warning.localPosition = new Vector3(0f, 1.42f, 0.33f);
        Box(warning, "Border", Vector3.zero, new Vector3(2.2f, 0.92f, 0.02f), Quaternion.identity, Mat("Warning Border", Hex("D9534F")), false).transform.localPosition = new Vector3(0f, 0f, 0.012f);
        Box(warning, "Panel", Vector3.zero, new Vector3(2.12f, 0.84f, 0.02f), Quaternion.identity, Mat("Warning Panel", Hex("FDE3E0")), false).transform.localPosition = Vector3.zero;
        TextMeshPro warningText = Text(warning, "Text", font,
            "<size=135%><b>！ スクリプトが動いていません</b></size>\n" +
            "このワールドの釣り（竿・ボタン・ボード）は使えません。\n" +
            "スクリプトの許可が必要です。\n" +
            "拒否した場合は、Basis を再起動してワールドに入り直し、\n" +
            "「許可」を選んでください。",
            0.62f, new Vector2(2.02f, 0.78f), Hex("8A1C1C"));
        warningText.textWrappingMode = TextWrappingModes.NoWrap;
        Place(warningText, new Vector3(0f, 0f, -0.015f));

        // Rods appear on the table, side by side, tips to the right.
        Transform spawnPoint = new GameObject("Rod Spawn Point").transform;
        spawnPoint.SetParent(stand, false);
        spawnPoint.localPosition = new Vector3(-1.1f, 0.9f, 0.3f);
        spawnPoint.localRotation = Quaternion.Euler(0f, 90f, 0f);

        GameObject game = new GameObject("Fishing Game");
        game.transform.SetParent(parent, false);
        FishingWorldController controller = game.AddComponent<FishingWorldController>();
        controller.SpawnButton = interactable;
        controller.RodSpawnPoint = spawnPoint;
        controller.RodSpacing = 0.11f;
        controller.RodSlots = RodSlots;
        controller.RodLayerMask = 1 << InteractableLayer;
        controller.StandText = hint;
        controller.ScriptWarning = warning.gameObject;
        controller.PermissionNote = note;
        FishEntry[] fish = fishData.fish;
        controller.SpeciesNames = System.Array.ConvertAll(fish, f => f.name);
        controller.SpeciesWeights = System.Array.ConvertAll(fish, f => f.weight);
        controller.SpeciesMinCm = System.Array.ConvertAll(fish, f => f.minCm);
        controller.SpeciesMaxCm = System.Array.ConvertAll(fish, f => f.maxCm);
        controller.SpeciesRare = System.Array.ConvertAll(fish, f => f.rare ? 1 : 0);
        controller.SpeciesBaseExp = System.Array.ConvertAll(fish, f => f.baseExp);
        controller.SpeciesBaseMoney = System.Array.ConvertAll(fish, f => f.baseMoney);
        controller.RewardSizeBonus = fishData.rewardSizeBonus;
        controller.CrownGoldPercent = fishData.crownGoldPercent;
        controller.CrownSilverPercent = fishData.crownSilverPercent;
        controller.SpawnPuff = Particles(stand, "Spawn Puff", Hex("FFFFFF"), Hex("FFE3EE"), 30, 0.8f, 0.6f, 0.05f, 0.1f, -0.1f, 0.3f);
        controller.ButtonAudio = Audio(button.transform, 0.9f);
        controller.ButtonClip = click;
        return controller;
    }

    static void BuildBoard(Transform parent, TMP_FontAsset font, FishingWorldController controller)
    {
        Transform board = Group(parent, "Catch Board");
        board.position = OnGround(BoardPosition);
        board.rotation = Quaternion.Euler(0f, 18f, 0f);
        Material wood = Mat("Stand Wood Dark", Hex("BF946B"));
        Local(Box(board, "Post", Vector3.zero, new Vector3(0.1f, 2.5f, 0.1f), Quaternion.identity, wood, true), new Vector3(-1.3f, 1.25f, 0.05f));
        Local(Box(board, "Post", Vector3.zero, new Vector3(0.1f, 2.5f, 0.1f), Quaternion.identity, wood, true), new Vector3(1.3f, 1.25f, 0.05f));
        Local(Box(board, "Panel", Vector3.zero, new Vector3(2.8f, 1.7f, 0.06f), Quaternion.identity, Mat("Board", Hex("FFF8EC")), true), new Vector3(0f, 1.55f, 0f));
        Local(Box(board, "Frame Top", Vector3.zero, new Vector3(2.9f, 0.08f, 0.1f), Quaternion.identity, wood, false), new Vector3(0f, 2.43f, 0f));
        Local(Box(board, "Frame Bottom", Vector3.zero, new Vector3(2.9f, 0.08f, 0.1f), Quaternion.identity, wood, false), new Vector3(0f, 0.67f, 0f));
        Local(Box(board, "Divider", Vector3.zero, new Vector3(0.02f, 1.5f, 0.07f), Quaternion.identity, wood, false), new Vector3(0.15f, 1.55f, 0f));

        TextMeshPro recent = Text(board, "Recent", font, "", 0.9f, new Vector2(1.45f, 1.5f), Hex("4A5A50"));
        recent.alignment = TextAlignmentOptions.TopLeft;
        // One catch per line: long player names are cut off rather than wrapped off the panel.
        recent.textWrappingMode = TextWrappingModes.NoWrap;
        recent.overflowMode = TextOverflowModes.Ellipsis;
        Place(recent, new Vector3(-0.6f, 1.55f, -0.035f));
        TextMeshPro ranking = Text(board, "Ranking", font, "", 0.78f, new Vector2(1.15f, 1.5f), Hex("4A5A50"));
        ranking.alignment = TextAlignmentOptions.TopLeft;
        ranking.textWrappingMode = TextWrappingModes.NoWrap;
        ranking.overflowMode = TextOverflowModes.Ellipsis;
        Place(ranking, new Vector3(0.78f, 1.55f, -0.035f));
        controller.RecentText = recent;
        controller.RankingText = ranking;
    }

    // The local player's encyclopedia: a row per species with count, largest and smallest size,
    // and a gold or silver crown beside a size among the largest/smallest few percent.
    static void BuildZukanBoard(Transform parent, TMP_FontAsset font, FishingWorldController controller)
    {
        FishEntry[] fish = fishData.fish;
        int rows = fish.Length;
        const float rowHeight = 0.15f;
        float panelHeight = 0.5f + rows * rowHeight;
        float panelCenter = 0.75f + panelHeight * 0.5f;

        Transform board = Group(parent, "Zukan Board");
        board.position = OnGround(ZukanPosition);
        board.rotation = Quaternion.Euler(0f, -15f, 0f);
        Material wood = Mat("Stand Wood Dark", Hex("BF946B"));
        float postHeight = panelCenter + panelHeight * 0.5f + 0.1f;
        Local(Box(board, "Post", Vector3.zero, new Vector3(0.1f, postHeight, 0.1f), Quaternion.identity, wood, true), new Vector3(-1.25f, postHeight * 0.5f, 0.05f));
        Local(Box(board, "Post", Vector3.zero, new Vector3(0.1f, postHeight, 0.1f), Quaternion.identity, wood, true), new Vector3(1.25f, postHeight * 0.5f, 0.05f));
        Local(Box(board, "Panel", Vector3.zero, new Vector3(2.4f, panelHeight, 0.06f), Quaternion.identity, Mat("Board Mint", Hex("F1FAF2")), true), new Vector3(0f, panelCenter, 0f));
        Local(Box(board, "Frame Top", Vector3.zero, new Vector3(2.5f, 0.08f, 0.1f), Quaternion.identity, wood, false), new Vector3(0f, panelCenter + panelHeight * 0.5f, 0f));
        Local(Box(board, "Frame Bottom", Vector3.zero, new Vector3(2.5f, 0.08f, 0.1f), Quaternion.identity, wood, false), new Vector3(0f, panelCenter - panelHeight * 0.5f, 0f));

        float top = panelCenter + panelHeight * 0.5f - 0.12f;
        Color ink = Hex("4A5A50");
        Color faint = Hex("7A8A80");
        TextMeshPro title = Text(board, "Title", font, "<b>あなたの図鑑</b>", 1.1f, new Vector2(1.4f, 0.16f), ink);
        title.alignment = TextAlignmentOptions.Left;
        Place(title, new Vector3(-0.42f, top, -0.035f));
        TextMeshPro status = Text(board, "Status", font, "", 0.6f, new Vector2(1.2f, 0.12f), faint);
        status.alignment = TextAlignmentOptions.Right;
        status.textWrappingMode = TextWrappingModes.NoWrap;
        status.overflowMode = TextOverflowModes.Ellipsis;
        Place(status, new Vector3(0.5f, top, -0.035f));
        controller.ZukanStatus = status;

        // Columns: name | count | largest + crown | smallest + crown.
        float nameX = -1.1f, countX = -0.18f, maxX = 0.25f, minX = 0.82f;
        float headerY = top - 0.17f;
        Header(board, font, "なまえ", nameX, headerY, faint, TextAlignmentOptions.Left);
        Header(board, font, "釣った数", countX, headerY, faint, TextAlignmentOptions.Right);
        Header(board, font, "最大", maxX, headerY, faint, TextAlignmentOptions.Right);
        Header(board, font, "最小", minX, headerY, faint, TextAlignmentOptions.Right);

        controller.ZukanName = new TextMeshPro[rows];
        controller.ZukanCount = new TextMeshPro[rows];
        controller.ZukanMax = new TextMeshPro[rows];
        controller.ZukanMin = new TextMeshPro[rows];
        controller.ZukanMaxGold = new GameObject[rows];
        controller.ZukanMaxSilver = new GameObject[rows];
        controller.ZukanMinGold = new GameObject[rows];
        controller.ZukanMinSilver = new GameObject[rows];
        Material gold = Mat("Crown Gold", Hex("F2C230"));
        Material silver = Mat("Crown Silver", Hex("C8D0D8"));
        for (int i = 0; i < rows; i++)
        {
            float y = headerY - 0.17f - i * rowHeight;
            controller.ZukanName[i] = Cell(board, font, "Name " + i, nameX, y, 0.8f, ink, TextAlignmentOptions.Left);
            controller.ZukanCount[i] = Cell(board, font, "Count " + i, countX, y, 0.4f, ink, TextAlignmentOptions.Right);
            controller.ZukanMax[i] = Cell(board, font, "Max " + i, maxX, y, 0.45f, ink, TextAlignmentOptions.Right);
            controller.ZukanMin[i] = Cell(board, font, "Min " + i, minX, y, 0.45f, ink, TextAlignmentOptions.Right);
            controller.ZukanMaxGold[i] = Crown(board, "Max Gold " + i, new Vector3(maxX + 0.13f, y, -0.06f), gold);
            controller.ZukanMaxSilver[i] = Crown(board, "Max Silver " + i, new Vector3(maxX + 0.13f, y, -0.06f), silver);
            controller.ZukanMinGold[i] = Crown(board, "Min Gold " + i, new Vector3(minX + 0.13f, y, -0.06f), gold);
            controller.ZukanMinSilver[i] = Crown(board, "Min Silver " + i, new Vector3(minX + 0.13f, y, -0.06f), silver);
        }
    }

    // A text cell whose anchor x is its left edge (left aligned) or right edge (right aligned).
    static TextMeshPro Cell(Transform board, TMP_FontAsset font, string name, float x, float y, float width, Color color, TextAlignmentOptions alignment)
    {
        TextMeshPro text = Text(board, name, font, "", 0.85f, new Vector2(width, 0.14f), color);
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        bool left = alignment == TextAlignmentOptions.Left;
        Place(text, new Vector3(left ? x + width * 0.5f : x - width * 0.5f, y, -0.035f));
        return text;
    }

    static void Header(Transform board, TMP_FontAsset font, string label, float x, float y, Color color, TextAlignmentOptions alignment)
    {
        TextMeshPro text = Cell(board, font, "Header " + label, x, y, 0.5f, color, alignment);
        text.fontSize = 0.6f;
        text.text = label;
    }

    // A small low-poly crown: a band with three points, hidden until the controller shows it.
    static GameObject Crown(Transform parent, string name, Vector3 localPosition, Material material)
    {
        Transform crown = new GameObject(name).transform;
        crown.SetParent(parent, false);
        crown.localPosition = localPosition;
        crown.localScale = Vector3.one * 1.7f;
        GameObject band = MeshObject(crown, "Band", prism6, material, Vector3.zero, Vector3.one, Quaternion.identity);
        band.transform.localPosition = new Vector3(0f, -0.025f, 0f);
        band.transform.localScale = new Vector3(0.05f, 0.03f, 0.03f);
        for (int k = -1; k <= 1; k++)
        {
            GameObject point = MeshObject(crown, "Point", cone, material, Vector3.zero, Vector3.one, Quaternion.identity);
            point.transform.localPosition = new Vector3(k * 0.032f, -0.01f, 0f);
            point.transform.localScale = new Vector3(0.016f, k == 0 ? 0.05f : 0.038f, 0.016f);
        }
        crown.gameObject.SetActive(false);
        return crown.gameObject;
    }

    // ---------------------------------------------------------------- rods and fish

    /// <summary>The rod's look: grip, reel and a tapering shaft along +Z, butt at the origin.</summary>
    static Transform RodModel(Transform parent, string name)
    {
        Transform rod = new GameObject(name).transform;
        rod.SetParent(parent, false);
        Material cork = Mat("Cork", Hex("D9B07C"));
        Material shaft = Mat("Rod Shaft", Hex("7FB3E0"));
        Material shaftTip = Mat("Rod Tip", Hex("F7F2EA"));
        Material reel = Mat("Reel", Hex("F2D06B"));
        Material dark = Mat("Rod Dark", Hex("5C6B7A"));

        Local(Box(rod, "Grip", Vector3.zero, new Vector3(0.045f, 0.045f, 0.38f), Quaternion.identity, cork, false), new Vector3(0f, 0f, 0.19f));
        Local(Box(rod, "Reel Seat", Vector3.zero, new Vector3(0.05f, 0.05f, 0.08f), Quaternion.identity, dark, false), new Vector3(0f, 0f, 0.42f));
        Local(Box(rod, "Reel Foot", Vector3.zero, new Vector3(0.015f, 0.05f, 0.03f), Quaternion.identity, dark, false), new Vector3(0f, -0.045f, 0.42f));
        Local(Box(rod, "Reel", Vector3.zero, new Vector3(0.05f, 0.08f, 0.08f), Quaternion.identity, reel, false), new Vector3(0f, -0.1f, 0.42f));
        Local(Box(rod, "Reel Handle", Vector3.zero, new Vector3(0.07f, 0.012f, 0.012f), Quaternion.identity, dark, false), new Vector3(0.05f, -0.1f, 0.42f));
        Local(Box(rod, "Shaft 1", Vector3.zero, new Vector3(0.03f, 0.03f, 0.62f), Quaternion.identity, shaft, false), new Vector3(0f, 0f, 0.77f));
        Local(Box(rod, "Shaft 2", Vector3.zero, new Vector3(0.022f, 0.022f, 0.6f), Quaternion.identity, shaft, false), new Vector3(0f, 0f, 1.37f));
        Local(Box(rod, "Shaft 3", Vector3.zero, new Vector3(0.014f, 0.014f, 0.55f), Quaternion.identity, shaftTip, false), new Vector3(0f, 0f, 1.94f));
        foreach (float z in new[] { 0.9f, 1.4f, 1.85f, 2.18f })
        {
            Local(Box(rod, "Guide", Vector3.zero, new Vector3(0.025f, 0.03f, 0.008f), Quaternion.identity, dark, false), new Vector3(0f, -0.02f, z));
        }
        return rod;
    }

    static FishingRod BuildRod(Transform parent, int index, FishingWorldController controller, AudioClip plop, AudioClip splash, AudioClip fanfare, AudioClip escape)
    {
        GameObject go = new GameObject("FishingRod_" + index);
        go.transform.SetParent(parent, false);
        go.transform.position = OnGround(StandPosition) + new Vector3(0f, 0.9f, 0f);
        go.SetActive(false);

        RodModel(go.transform, "Model");
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.center = new Vector3(0f, -0.03f, 1.05f);
        collider.size = new Vector3(0.07f, 0.12f, 2.15f);

        Rigidbody body = go.AddComponent<Rigidbody>();
        body.mass = 0.6f;
        body.linearDamping = 0.2f;
        body.angularDamping = 0.8f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        Transform grip = new GameObject("Grip Point").transform;
        grip.SetParent(go.transform, false);
        grip.localPosition = new Vector3(0f, 0f, 0.2f);

        BasisPickupInteractable pickup = go.AddComponent<BasisPickupInteractable>();
        pickup.RigidRef = body;
        pickup.GripPoint = grip;
        pickup.InteractRange = 2f;
        // Stays in the hand after the grab is let go, on desktop and in VR; grab again to drop.
        pickup.AutoHold = BasisInteractableObject.BasisAutoHold.Everywhere;

        BasisPickupSyncNetworking sync = go.AddComponent<BasisPickupSyncNetworking>();
        sync.BasisPickupInteractable = pickup;
        sync.Target = go.transform;

        Transform tip = new GameObject("Tip").transform;
        tip.SetParent(go.transform, false);
        tip.localPosition = new Vector3(0f, 0f, 2.22f);

        GameObject lineGo = new GameObject("Line");
        lineGo.transform.SetParent(go.transform, false);
        LineRenderer line = lineGo.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 14;
        line.widthMultiplier = 0.006f;
        line.numCapVertices = 0;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = Unlit("Fishing Line", Hex("FFFFFF"));

        Transform bobber = new GameObject("Bobber").transform;
        bobber.SetParent(go.transform, false);
        MeshObject(bobber, "Top", blobC, Mat("Bobber Red", Hex("FF6B6B")), new Vector3(0f, 0.025f, 0f), new Vector3(0.07f, 0.055f, 0.07f), Quaternion.identity).transform.localPosition = new Vector3(0f, 0.025f, 0f);
        MeshObject(bobber, "Bottom", blobC, Mat("Bobber White", Hex("FFFFFF")), Vector3.zero, Vector3.one, Quaternion.identity).transform.localScale = new Vector3(0.066f, 0.055f, 0.066f);
        Local(Box(bobber, "Stick", Vector3.zero, new Vector3(0.01f, 0.08f, 0.01f), Quaternion.identity, Mat("Bobber Red", Hex("FF6B6B")), false), new Vector3(0f, 0.07f, 0f));

        Transform fishRoot = new GameObject("Fish").transform;
        fishRoot.SetParent(go.transform, false);
        GameObject[] models = BuildFishModels(fishRoot);

        FishingRod rod = go.AddComponent<FishingRod>();
        rod.Controller = controller;
        rod.RodIndex = index;
        rod.WaterLevel = WaterLevel;
        rod.PondCenter = Vector3.zero;
        rod.PondRadius = FishingRadius;
        rod.GroundLevel = 0f;
        rod.Tip = tip;
        rod.Line = line;
        rod.Bobber = bobber;
        rod.FishRoot = fishRoot;
        rod.FishModels = models;
        rod.FishModelLengthCm = System.Array.ConvertAll(fishData.fish, f => f.model.lengthCm);
        rod.Splash = Particles(go.transform, "Splash", Hex("FFFFFF"), Hex("BFE6FF"), 26, 0.8f, 2.6f, 0.03f, 0.07f, 1f, 0.12f);
        rod.Sparkle = Particles(fishRoot, "Sparkle", Hex("FFF3A0"), Hex("FFD24A"), 0, 0.7f, 0.5f, 0.02f, 0.05f, -0.05f, 0.15f);
        ParticleSystem.EmissionModule sparkleEmission = rod.Sparkle.emission;
        sparkleEmission.rateOverTime = 18f;
        ParticleSystem.MainModule sparkleMain = rod.Sparkle.main;
        sparkleMain.loop = true;
        rod.Audio = Audio(go.transform, 1f);
        rod.GroundMask = 1;
        go.AddComponent<FishingHaptics>().Rod = rod;
        SetLayer(go.transform, InteractableLayer);
        rod.PlopClip = plop;
        rod.SplashClip = splash;
        rod.CatchClip = fanfare;
        rod.EscapeClip = escape;
        return rod;
    }

    /// <summary>One model per species in Data/fish.json, mouth at the origin, body along -Z, all hidden.</summary>
    static GameObject[] BuildFishModels(Transform parent)
    {
        FishEntry[] fish = fishData.fish;
        GameObject[] models = new GameObject[fish.Length];
        for (int i = 0; i < fish.Length; i++)
        {
            FishModel m = fish[i].model;
            string name = "Fish " + i;
            if (m.type == "boot")
            {
                models[i] = Boot(parent, name, m.body, m.belly);
                continue;
            }
            float length = m.lengthCm * 0.01f;
            models[i] = Fish(parent, name, length, m.width, m.height, m.body, m.belly, m.fin);
            if (!string.IsNullOrEmpty(m.patch))
            {
                Local(Box(models[i].transform, "Patch", Vector3.zero, new Vector3(m.width * 1.02f, m.height * 0.3f, length * 0.25f), Quaternion.identity, ColorMat(m.patch), false), new Vector3(0f, m.height * 0.38f, -length * 0.38f));
            }
            if (!string.IsNullOrEmpty(m.stripe))
            {
                Local(Box(models[i].transform, "Stripe", Vector3.zero, new Vector3(m.width * 1.05f, m.height * 0.22f, length * 0.66f), Quaternion.identity, ColorMat(m.stripe), false), new Vector3(0f, 0f, -length * 0.43f));
            }
            if (m.whiskers)
            {
                Material whisker = Mat("Whisker", Hex("4A474E"));
                foreach (float x in new[] { -1f, 1f })
                {
                    Local(Box(models[i].transform, "Whisker", Vector3.zero, new Vector3(0.006f, 0.006f, length * 0.28f), Quaternion.Euler(0f, x * 35f, 0f), whisker, false), new Vector3(x * m.width * 0.58f, -0.01f, -0.02f));
                    Local(Box(models[i].transform, "Whisker", Vector3.zero, new Vector3(0.005f, 0.005f, length * 0.18f), Quaternion.Euler(20f, x * 50f, 0f), whisker, false), new Vector3(x * m.width * 0.42f, -m.height * 0.4f, -0.03f));
                }
            }
        }
        foreach (GameObject model in models) model.SetActive(false);
        return models;
    }

    // Fish parts share one material per colour.
    static Material ColorMat(string hex)
    {
        return Mat("Color " + hex.TrimStart('#').ToUpperInvariant(), Hex(hex.TrimStart('#')));
    }

    static GameObject Fish(Transform parent, string name, float length, float width, float height, string body, string belly, string fin)
    {
        Transform fish = new GameObject(name).transform;
        fish.SetParent(parent, false);
        Material bodyMat = ColorMat(body);
        Material bellyMat = ColorMat(belly);
        Material finMat = ColorMat(string.IsNullOrEmpty(fin) ? body : fin);
        Material eye = Mat("Fish Eye", Hex("2B2B33"));

        Local(Box(fish, "Head", Vector3.zero, new Vector3(width * 0.85f, height * 0.8f, length * 0.22f), Quaternion.identity, bodyMat, false), new Vector3(0f, 0f, -length * 0.11f));
        Local(Box(fish, "Body", Vector3.zero, new Vector3(width, height, length * 0.5f), Quaternion.identity, bodyMat, false), new Vector3(0f, 0f, -length * 0.45f));
        Local(Box(fish, "Belly", Vector3.zero, new Vector3(width * 0.96f, height * 0.4f, length * 0.55f), Quaternion.identity, bellyMat, false), new Vector3(0f, -height * 0.3f, -length * 0.4f));
        Local(Box(fish, "Tail Root", Vector3.zero, new Vector3(width * 0.6f, height * 0.55f, length * 0.16f), Quaternion.identity, bodyMat, false), new Vector3(0f, 0f, -length * 0.77f));
        Local(Box(fish, "Tail Top", Vector3.zero, new Vector3(width * 0.25f, height * 0.6f, length * 0.16f), Quaternion.Euler(-35f, 0f, 0f), finMat, false), new Vector3(0f, height * 0.22f, -length * 0.9f));
        Local(Box(fish, "Tail Bottom", Vector3.zero, new Vector3(width * 0.25f, height * 0.6f, length * 0.16f), Quaternion.Euler(35f, 0f, 0f), finMat, false), new Vector3(0f, -height * 0.22f, -length * 0.9f));
        Local(Box(fish, "Dorsal", Vector3.zero, new Vector3(width * 0.2f, height * 0.4f, length * 0.25f), Quaternion.Euler(-12f, 0f, 0f), finMat, false), new Vector3(0f, height * 0.6f, -length * 0.42f));
        foreach (float x in new[] { -1f, 1f })
        {
            Local(Box(fish, "Eye", Vector3.zero, Vector3.one * Mathf.Max(0.012f, length * 0.05f), Quaternion.identity, eye, false), new Vector3(x * width * 0.43f, height * 0.12f, -length * 0.08f));
            Local(Box(fish, "Fin", Vector3.zero, new Vector3(width * 0.5f, height * 0.12f, length * 0.12f), Quaternion.Euler(0f, x * 30f, x * 20f), finMat, false), new Vector3(x * width * 0.6f, -height * 0.2f, -length * 0.3f));
        }
        return fish.gameObject;
    }

    static GameObject Boot(Transform parent, string name, string body, string sole)
    {
        Transform boot = new GameObject(name).transform;
        boot.SetParent(parent, false);
        Material rubber = ColorMat(body);
        Material soleMat = ColorMat(string.IsNullOrEmpty(sole) ? "3F4A40" : sole);
        Local(Box(boot, "Leg", Vector3.zero, new Vector3(0.1f, 0.1f, 0.26f), Quaternion.identity, rubber, false), new Vector3(0f, 0f, -0.13f));
        Local(Box(boot, "Foot", Vector3.zero, new Vector3(0.1f, 0.17f, 0.09f), Quaternion.identity, rubber, false), new Vector3(0f, -0.05f, -0.3f));
        Local(Box(boot, "Sole", Vector3.zero, new Vector3(0.105f, 0.19f, 0.02f), Quaternion.identity, soleMat, false), new Vector3(0f, -0.05f, -0.35f));
        Local(Box(boot, "Rim", Vector3.zero, new Vector3(0.11f, 0.11f, 0.03f), Quaternion.identity, soleMat, false), new Vector3(0f, 0f, -0.01f));
        return boot.gameObject;
    }

    // ---------------------------------------------------------------- helpers

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        foreach (Transform child in t) SetLayer(child, layer);
    }

    static Transform Group(Transform parent, string name)
    {
        Transform t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

    static GameObject Local(GameObject go, Vector3 localPosition)
    {
        go.transform.localPosition = localPosition;
        return go;
    }

    static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size, Quaternion rotation, Material material, bool collider)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        // Rotation is relative to the parent, so parts of a turned group (the board) turn with it.
        go.transform.localRotation = rotation;
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    static GameObject MeshObject(Transform parent, string name, Mesh mesh, Material material, Vector3 position, Vector3 scale, Quaternion rotation)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.localRotation = rotation;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    static TextMeshPro Text(Transform parent, string name, TMP_FontAsset font, string text, float size, Vector2 rect, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        TextMeshPro tmp = go.AddComponent<TextMeshPro>();
        tmp.font = font;
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.rectTransform.sizeDelta = rect;
        return tmp;
    }

    // A TextMeshPro has a RectTransform, and the scene saves its anchoredPosition: setting only
    // localPosition looks right until the scene is reloaded, then x and y fall back to zero.
    static void Place(TextMeshPro text, Vector3 localPosition)
    {
        RectTransform rect = text.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition3D = localPosition;
        rect.localPosition = localPosition;
    }

    static ParticleSystem Particles(Transform parent, string name, Color a, Color b, int burst, float lifetime, float speed, float minSize, float maxSize, float gravity, float radius)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.5f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.6f, lifetime);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.5f, speed);
        main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = new ParticleSystem.MinMaxGradient(a, b);
        main.gravityModifier = gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 80;
        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        if (burst > 0) emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burst) });
        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = radius;
        ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
        ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.mesh = blobC;
        renderer.sharedMaterial = ParticleMaterial();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        return ps;
    }

    static AudioSource Audio(Transform parent, float volume)
    {
        GameObject go = new GameObject("Audio");
        go.transform.SetParent(parent, false);
        AudioSource source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.volume = volume;
        source.minDistance = 1.5f;
        source.maxDistance = 25f;
        source.rolloffMode = AudioRolloffMode.Linear;
        return source;
    }

    static Color Hex(string hex)
    {
        ColorUtility.TryParseHtmlString("#" + hex, out Color c);
        return c;
    }

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
    }

    static Mesh SaveMesh(Mesh mesh)
    {
        string path = $"{Generated}/Meshes/{mesh.name}.asset";
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            // Update in place so references from other assets keep their GUID.
            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    static Material SaveMaterial(string name, Material material)
    {
        string path = $"{Generated}/Materials/{name}.mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        material.name = name;
        if (existing != null)
        {
            existing.shader = material.shader;
            existing.CopyPropertiesFromMaterial(material);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    /// <summary>A matte URP Lit material in one flat colour, shared by name.</summary>
    static Material Mat(string name, Color color)
    {
        if (materials.TryGetValue(name, out Material cached)) return cached;
        Material m = FlatMaterial(name, "Universal Render Pipeline/Lit", color);
        m.SetFloat("_Smoothness", 0.05f);
        m.SetFloat("_Metallic", 0f);
        m.enableInstancing = true;
        return m;
    }

    static Material Unlit(string name, Color color)
    {
        if (materials.TryGetValue(name, out Material cached)) return cached;
        return FlatMaterial(name, "Universal Render Pipeline/Unlit", color);
    }

    // Sets the colour on the existing asset rather than replacing its properties, so a rebuild
    // leaves alone the fields URP fills in on import and the asset does not churn in git.
    static Material FlatMaterial(string name, string shaderName, Color color)
    {
        string path = $"{Generated}/Materials/{name}.mat";
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader shader = Shader.Find(shaderName);
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
        EditorUtility.SetDirty(m);
        materials[name] = m;
        return m;
    }

    static Material ParticleMaterial()
    {
        if (materials.TryGetValue("Particle", out Material cached)) return cached;
        Material m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        m.SetColor("_BaseColor", Color.white);
        m = SaveMaterial("Particle", m);
        materials["Particle"] = m;
        return m;
    }

    static Material WaterMaterial()
    {
        Material m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", new Color(0.47f, 0.76f, 0.9f, 0.72f));
        m.SetFloat("_Smoothness", 0.85f);
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        m.SetShaderPassEnabled("DepthOnly", false);
        m.SetShaderPassEnabled("ShadowCaster", false);
        return SaveMaterial("Water", m);
    }

    /// <summary>A dynamic TMP font asset over Noto Sans JP, so the board can show Japanese and any player name.</summary>
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
        // Bake the fixed text in, so it shows even before the dynamic atlas fills.
        font.TryAddCharacters("！スクリプトが動いていません使えません許可必要拒否場合再起動入直選記録サーバー保存され釣り竿ボタンを押すと釣り竿が出るよもう全部出ているさいきんの釣果まだ誰も釣っていないよ図鑑？×があなただれかフナコイニジマスナマズ金のコイ長靴cm!★…0123456789()");
        UseWorldTextShader(font);
        EditorUtility.SetDirty(font);
        AssetDatabase.SaveAssets();
        return font;
    }

    /// <summary>
    /// TMP's shaders take their depth test from a UI-only global that URP leaves off, so 3D text
    /// would draw through everything. The world's copy of the shader depth-tests normally.
    /// </summary>
    static void UseWorldTextShader(TMP_FontAsset font)
    {
        Shader shader = Shader.Find("FishingWorld/TMP Distance Field (World)");
        if (shader == null || font.material.shader == shader) return;
        font.material.shader = shader;
        EditorUtility.SetDirty(font.material);
        AssetDatabase.SaveAssets();
    }

    static AudioClip SaveSound(string name, float[] samples, int rate = Synth.Rate)
    {
        string path = $"{Generated}/Audio/{name}.wav";
        Synth.WriteWav(Path.Combine(Directory.GetCurrentDirectory(), path), samples, rate);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
    }

    // Music is long: compress it and stream it rather than hold a minute of PCM in memory.
    static void SetMusicImport(AudioClip clip)
    {
        AudioImporter importer = (AudioImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip));
        AudioImporterSampleSettings settings = importer.defaultSampleSettings;
        if (settings.loadType == AudioClipLoadType.Streaming && settings.compressionFormat == AudioCompressionFormat.Vorbis) return;
        settings.loadType = AudioClipLoadType.Streaming;
        settings.compressionFormat = AudioCompressionFormat.Vorbis;
        settings.quality = 0.5f;
        importer.defaultSampleSettings = settings;
        importer.forceToMono = true;
        importer.SaveAndReimport();
    }

    /// <summary>
    /// Small sound effects synthesized from code. Composed and written by an AI assistant, so they
    /// are AI-generated audio: per AI.md the fishing world is not for the Basis repository.
    /// </summary>
    static class Synth
    {
        public const int Rate = 44100;

        public static float[] Plop()
        {
            float[] s = new float[(int)(Rate * 0.22f)];
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float f = Mathf.Lerp(900f, 180f, Mathf.Clamp01(t / 0.12f));
                s[i] = Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t * 22f) * 0.8f;
            }
            return s;
        }

        public static float[] Splash()
        {
            System.Random random = new System.Random(1);
            float[] s = new float[(int)(Rate * 0.6f)];
            float low = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float noise = (float)random.NextDouble() * 2f - 1f;
                low += (noise - low) * 0.18f;
                float env = Mathf.Min(1f, t / 0.01f) * Mathf.Exp(-t * 7f);
                s[i] = low * env * 1.6f;
            }
            return s;
        }

        public static float[] Fanfare()
        {
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };
            float noteLength = 0.11f;
            float[] s = new float[(int)(Rate * (noteLength * notes.Length + 0.35f))];
            for (int n = 0; n < notes.Length; n++)
            {
                int start = (int)(n * noteLength * Rate);
                float length = n == notes.Length - 1 ? 0.45f : noteLength * 1.4f;
                for (int i = 0; i < (int)(length * Rate) && start + i < s.Length; i++)
                {
                    float t = i / (float)Rate;
                    float tone = Mathf.Sin(2f * Mathf.PI * notes[n] * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * notes[n] * t);
                    s[start + i] += tone * Mathf.Exp(-t * 6f) * 0.32f;
                }
            }
            return s;
        }

        public static float[] Escape()
        {
            float[] s = new float[(int)(Rate * 0.4f)];
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float f = Mathf.Lerp(520f, 260f, t / 0.4f);
                s[i] = Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t * 6f) * 0.35f;
            }
            return s;
        }

        public static float[] Click()
        {
            float[] s = new float[(int)(Rate * 0.12f)];
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = (Mathf.Sin(2f * Mathf.PI * 1200f * t) * Mathf.Exp(-t * 60f) + Mathf.Sin(2f * Mathf.PI * 1800f * t) * Mathf.Exp(-(t - 0.05f) * 60f) * (t > 0.05f ? 1f : 0f)) * 0.4f;
            }
            return s;
        }

        public static void WriteWav(string path, float[] samples, int rate)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int dataBytes = samples.Length * 2;
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(rate);
                writer.Write(rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);
                foreach (float sample in samples)
                {
                    writer.Write((short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue));
                }
            }
        }
    }
}
