using Basis.BasisUI;
using Basis.ImagePickup;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using Cilbox;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Reads what is shared on the server for the <see cref="SharePanel"/> and carries out the
/// actions that need Basis itself: copying to the clipboard, saving a photo, moving a photo and
/// joining a shared server.
///
/// Every Basis type used here is outside the prop whitelist, so this component is what the
/// permission prompt is about. If the player refuses, only this component stops and the panel
/// keeps showing its notice. Three rules keep it that way:
/// - Only methods and properties of these types, never their fields: Cilbox resolves a field of
///   a type it may not use by dereferencing a null type, which breaks the whole prop (and its
///   bundle build, where nothing has been allowed yet). So a photo is known by its object name,
///   and an orb's title comes from the TextMeshPro under it.
/// - No method of a whitelisted type that the whitelist leaves out: that also breaks the prop.
/// - No typeof(...) (System.Type has no allowed methods); Resources is not whitelisted, so its
///   generic lookup is fine. Likewise no List&lt;T&gt; of these types: lists of them are walked as
///   a plain IEnumerable, and their entries read through JsonUtility rather than their fields.
/// </summary>
[Cilboxable]
public class SharePanelSource : MonoBehaviour
{
    public SharePanel Panel;
    public float RefreshSeconds = 1f;
    // Thumbnails are decoded from the photo's PNG once and kept while the photo exists.
    public int MaxThumbnails = 48;

    private float nextRefresh;
    private Dictionary<string, Texture2D> thumbs;
    private Dictionary<string, float> thumbAspect;
    private List<string> seen;
    // Photos this client has looked at, to tell the panel who shared a new one.
    private List<string> photosKnown;
    private MaterialPropertyBlock block;
    // What this player has saved, as JSON text to search in: the library's items and the saved
    // servers. Read every few seconds.
    public float SavedRefreshSeconds = 5f;
    private float nextSavedRefresh;
    private string libraryJson = "";
    private string serversJson = "";
    // A prop waiting for its bundle's metadata before the library's placement can start. Held as
    // object: a field typed with a type Cilbox may refuse is not worth the risk.
    private object pendingSpawnItem;
    private object pendingSpawnTask;

    private void Start()
    {
        thumbs = new Dictionary<string, Texture2D>();
        thumbAspect = new Dictionary<string, float>();
        seen = new List<string>();
        photosKnown = new List<string>();
        block = new MaterialPropertyBlock();
    }

    private void Update()
    {
        int requests = Panel.RequestCount();
        if (requests > 0)
        {
            for (int i = 0; i < requests; i++)
            {
                Perform(Panel.RequestAction(i), Panel.RequestKey(i), Panel.RequestPosition(i), Panel.RequestRotation(i), Panel.RequestIsLocal(i));
            }
            Panel.ClearRequests();
        }

        if (pendingSpawnTask != null && ((System.Threading.Tasks.Task)pendingSpawnTask).IsCompleted)
        {
            object item = pendingSpawnItem;
            pendingSpawnTask = null;
            pendingSpawnItem = null;
            Place(item);
        }

        if (Time.time < nextRefresh) return;
        nextRefresh = Time.time + RefreshSeconds;
        Refresh();
    }

    private void Refresh()
    {
        if (Time.time >= nextSavedRefresh)
        {
            nextSavedRefresh = Time.time + SavedRefreshSeconds;
            ReadSaved();
        }
        seen.Clear();
        Panel.BeginItems();

        BasisImagePickupObject[] images = Resources.FindObjectsOfTypeAll<BasisImagePickupObject>();
        for (int i = 0; i < images.Length; i++)
        {
            BasisImagePickupObject image = images[i];
            if (image == null) continue;
            // "BasisImagePickup_" + the first 8 hex digits of the image's id: the same on every client.
            string key = "img:" + image.gameObject.name;
            seen.Add(key);
            if (!photosKnown.Contains(key))
            {
                photosKnown.Add(key);
                // Whoever moves a photo when it first turns up is, for a new photo, whoever shared it.
                if (image.IsController) Panel.ClaimSharer(key);
            }
            Texture2D thumb = Thumbnail(key, image);
            float aspect = 1f;
            if (thumb != null) thumbAspect.TryGetValue(key, out aspect);
            string size = thumb != null ? thumb.width + " × " + thumb.height : "";
            Panel.AddItem(key, SharePanel.KindPhoto, size, "", "", thumb, aspect, false);
        }

        BasisContentSphere[] spheres = Resources.FindObjectsOfTypeAll<BasisContentSphere>();
        for (int i = 0; i < spheres.Length; i++)
        {
            BasisContentSphere sphere = spheres[i];
            if (sphere == null || string.IsNullOrEmpty(sphere.SphereNetID)) continue;
            int type = (int)sphere.ContentType;
            int kind = SharePanel.KindOther;
            if (type == 0) kind = SharePanel.KindAvatar;
            else if (type == 1) kind = SharePanel.KindProp;
            else if (type == 2) kind = SharePanel.KindWorld;
            else if (type == 3) kind = SharePanel.KindServer;
            else if (type == 5) kind = SharePanel.KindLink;
            else if (type == 6) kind = SharePanel.KindText;

            string key = "orb:" + sphere.SphereNetID;
            string title = "";
            string body = "";
            Texture thumb = null;
            float aspect = 1f;
            bool saved = false;
            if (kind == SharePanel.KindLink || kind == SharePanel.KindText)
            {
                body = sphere.ContentURL;
            }
            else if (kind == SharePanel.KindServer)
            {
                // "address[:port][#password]": the password stays here, only the address is shown.
                string address;
                ushort port;
                bool portGiven;
                string password;
                if (!SavedServerStore.TryParseConnectionString(sphere.ContentURL, out address, out port, out portGiven, out password)) continue;
                title = address + ":" + port;
                saved = ServerSaved(address, port);
                if (!string.IsNullOrEmpty(password)) body = Panel.T("パスワード付きのサーバーです（接続時に自動で使われます）", "This server has a password (used automatically when joining)");
            }
            else
            {
                title = OrbTitle(sphere);
                thumb = OrbImage(sphere);
                saved = LibrarySaved(sphere.ContentURL);
                if (thumb != null) aspect = (float)thumb.width / thumb.height;
            }
            Panel.AddItem(key, kind, title, sphere.CreatorDisplayName, body, thumb, aspect, saved);
        }

        Panel.EndItems();
        ForgetGone();
        // Only now: a refused type fails the first call that uses it and stops this component,
        // so reaching here means the permission was given and the panel's notice can go.
        Panel.SourceReady();
    }

    // The library (worlds, props, avatars) and the saved servers, each entry turned into JSON.
    private void ReadSaved()
    {
        BasisDataStoreItemKeys.EnsureLoaded();
        libraryJson = JoinJson(BasisDataStoreItemKeys.DisplayKeys());
        serversJson = JoinJson(SavedServerStore.Load());
    }

    private string JoinJson(IEnumerable entries)
    {
        if (entries == null) return "";
        System.Text.StringBuilder text = new System.Text.StringBuilder();
        foreach (object entry in entries)
        {
            if (entry == null) continue;
            text.Append(JsonUtility.ToJson(entry));
            text.Append('\n');
        }
        return text.ToString();
    }

    private bool LibrarySaved(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return libraryJson.Contains("\"Url\":\"" + url + "\"");
    }

    private bool ServerSaved(string address, ushort port)
    {
        string lower = serversJson.ToLowerInvariant();
        return lower.Contains("\"address\":\"" + address.ToLowerInvariant() + "\",\"port\":" + port + ",");
    }

    // The orb's label reads "<type>\n<name>" once its bundle's name is known.
    private string OrbTitle(BasisContentSphere sphere)
    {
        TextMeshPro label = sphere.GetComponentInChildren<TextMeshPro>(true);
        if (label == null) return "";
        string text = label.text;
        if (string.IsNullOrEmpty(text)) return "";
        int line = text.IndexOf('\n');
        return line >= 0 ? text.Substring(line + 1) : "";
    }

    // The orb's picture: the bundle's image blended with the type colour, set on its renderer.
    // A 1x1 texture is just the type colour.
    private Texture OrbImage(BasisContentSphere sphere)
    {
        Renderer renderer = sphere.GetComponentInChildren<Renderer>(true);
        if (renderer == null) return null;
        int count = renderer.sharedMaterials.Length;
        for (int m = 0; m < count; m++)
        {
            renderer.GetPropertyBlock(block, m);
            Texture texture = block.GetTexture("_MainTex");
            if (texture != null && texture.width > 1) return texture;
        }
        return null;
    }

    private Texture2D Thumbnail(string key, BasisImagePickupObject image)
    {
        Texture2D thumb;
        if (thumbs.TryGetValue(key, out thumb)) return thumb;
        if (thumbs.Count >= MaxThumbnails) return null;
        byte[] png = image.CleanPng;
        if (png == null || png.Length == 0) return null;
        thumb = new Texture2D(2, 2);
        if (!ImageConversion.LoadImage(thumb, png, true))
        {
            Destroy(thumb);
            return null;
        }
        thumbs[key] = thumb;
        thumbAspect[key] = thumb.height > 0 ? (float)thumb.width / thumb.height : 1f;
        return thumb;
    }

    private void ForgetGone()
    {
        List<string> gone = null;
        foreach (string key in thumbs.Keys)
        {
            if (seen.Contains(key)) continue;
            if (gone == null) gone = new List<string>();
            gone.Add(key);
        }
        if (gone == null) return;
        for (int i = 0; i < gone.Count; i++)
        {
            Destroy(thumbs[gone[i]]);
            thumbs.Remove(gone[i]);
            thumbAspect.Remove(gone[i]);
        }
    }

    private void Perform(int action, string key, Vector3 position, Quaternion rotation, bool local)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (key.StartsWith("img:"))
        {
            BasisImagePickupObject image = FindImage(key.Substring(4));
            if (image == null) return;
            if (action == SharePanel.ActionBring)
            {
                // Every client gets this request; only the one moving the photo for everyone
                // (its controller) moves it, and Basis sends the new place to the rest.
                if (image.IsController) image.transform.SetPositionAndRotation(position, rotation);
                if (local) Panel.Notice("写真を手元に出しました", "Brought the photo over");
            }
            else if (action == SharePanel.ActionCopy)
            {
                image.OnCopyPressed();
                Panel.Notice("写真をコピーしました", "Copied the photo");
            }
            else if (action == SharePanel.ActionSave)
            {
                image.OnSavePressed();
                Panel.Notice("写真をピクチャフォルダに保存しました", "Saved the photo to your Pictures folder");
            }
            return;
        }
        if (!key.StartsWith("orb:")) return;
        BasisContentSphere found = FindSphere(key.Substring(4));
        if (found == null) return;
        bool server = (int)found.ContentType == 3;
        if (action == SharePanel.ActionCopy)
        {
            BasisClipboard.Copy(found.ContentURL);
            Panel.Notice("コピーしました", "Copied");
        }
        else if (action == SharePanel.ActionSpawn && (int)found.ContentType == 1)
        {
            Spawn(found.ContentURL, found.UnlockPassword);
        }
        else if (action == SharePanel.ActionSaveOrb)
        {
            // Basis' own dialog for the orb: "Save" puts it in the library or the server list.
            found.WasPressed();
            nextSavedRefresh = 0f;
        }
        else if (action == SharePanel.ActionConnect && server)
        {
            Join(found.ContentURL);
        }
    }

    // The same path as a basisdemo:// invite link: the link builds the server entry, then the
    // connection service leaves this server and joins that one.
    private void Join(string connection)
    {
        string address;
        ushort port;
        bool portGiven;
        string password;
        if (!SavedServerStore.TryParseConnectionString(connection, out address, out port, out portGiven, out password))
        {
            Panel.Notice("サーバーの情報が読めませんでした", "Could not read the server details");
            return;
        }
        ServerDirectoryEntry entry;
        if (!BasisDeepLinkProvider.TryParseBasisUrl(BasisDeepLinkProvider.FormatDeepLink(address, port, password), out entry))
        {
            Panel.Notice("サーバーの情報が読めませんでした", "Could not read the server details");
            return;
        }
        string userName = BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, "");
        if (string.IsNullOrEmpty(userName) || userName.Trim().Length == 0)
        {
            Panel.Notice("先にメニューのサーバー画面で名前を設定してください", "Set your name in the Servers menu first");
            return;
        }
        Panel.Notice("接続しています…", "Joining…");
        BasisConnectionService.ConnectAsync(entry, userName, false);
    }

    // Spawns a shared prop the way the library does: the prop's own placement settings, and for
    // most props the pointer placement where the player picks the spot. The library's item is
    // made from JSON, since its fields cannot be set from here.
    private void Spawn(string url, string password)
    {
        if (string.IsNullOrEmpty(url)) return;
        string json = "{\"Mode\":2,\"PlacementType\":0,\"Url\":\"" + JsonText(url) + "\",\"Pass\":\"" + JsonText(password) + "\"}";
        BasisDataStoreItemKeys.ItemKey item = JsonUtility.FromJson<BasisDataStoreItemKeys.ItemKey>(json);
        if (item == null) return;
        CachedMetaData.CachedContent meta;
        if (CachedMetaData.TryGetMeta(url, out meta))
        {
            Place(item);
            return;
        }
        // The library knows a prop's size and placement from its metadata; fetch it first.
        Panel.Notice("プロップの情報を読み込んでいます…", "Reading the prop's details…");
        pendingSpawnItem = item;
        pendingSpawnTask = CachedMetaData.PreloadMetaDataForItem(item);
    }

    private void Place(object item)
    {
        Panel.Notice("置く場所を選んでください", "Choose where to put it");
        ContentLoader.LoadProp((BasisDataStoreItemKeys.ItemKey)item, BundledContentHolder.NetworkType.Networked, false, false, false);
    }

    private string JsonText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private BasisImagePickupObject FindImage(string name)
    {
        BasisImagePickupObject[] images = Resources.FindObjectsOfTypeAll<BasisImagePickupObject>();
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i] != null && images[i].gameObject.name == name) return images[i];
        }
        return null;
    }

    private BasisContentSphere FindSphere(string id)
    {
        BasisContentSphere[] spheres = Resources.FindObjectsOfTypeAll<BasisContentSphere>();
        for (int i = 0; i < spheres.Length; i++)
        {
            if (spheres[i] != null && spheres[i].SphereNetID == id) return spheres[i];
        }
        return null;
    }
}
