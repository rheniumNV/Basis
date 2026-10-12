using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// A panel listing what has been shared on the server: photos (image pickups), links, text,
/// servers and the world / prop / avatar orbs, with who shared each and when. Everyone sees the
/// same screen: the panel's network owner decides the order of the list (newest first) and when
/// each share was first seen; the page, the type filter, the item shown large and whether the
/// panel can be grabbed are shared too. The buttons on a row act for whoever presses them, so
/// several people can use it at once. The language is each player's own.
///
/// This script uses only what a prop may use without asking. Reading the shares and the actions
/// that need Basis itself (copy, save, moving a photo, joining a server) live in
/// <see cref="SharePanelSource"/>, which needs the player's permission; it pushes the items in and
/// pulls the requests out, so a refused source leaves this script running with a notice.
/// </summary>
[Cilboxable]
public class SharePanel : MonoBehaviour
{
    public const int KindPhoto = 0;
    public const int KindLink = 1;
    public const int KindText = 2;
    public const int KindWorld = 3;
    public const int KindProp = 4;
    public const int KindAvatar = 5;
    public const int KindOther = 6;
    public const int KindServer = 7;
    public const int KindFolder = 8;

    public const int ActionBring = 1;
    public const int ActionCopy = 2;
    public const int ActionSave = 3;
    public const int ActionOpen = 4;
    public const int ActionConnect = 5;
    // Saving an orb (world, prop, avatar, server) through Basis' own save dialog.
    public const int ActionSaveOrb = 6;
    // Spawning a shared prop in front of whoever pressed it.
    public const int ActionSpawn = 7;
    // Removing a share for everyone (where Basis lets this player).
    public const int ActionDelete = 8;

    private const byte MsgOrder = 1;
    private const byte MsgView = 2;
    private const byte MsgBring = 3;
    private const byte MsgSharer = 4;
    private const int Capacity = 64;
    private const int RequestCapacity = 16;
    // Shares already there when the panel first looks have no known time.
    private const float FirstLookSeconds = 3f;
    private const float ConfirmSeconds = 6f;
    // Filter bits by kind; "other" shares (dolly tracks) show only with every filter on.
    private const int AllKinds = (1 << KindPhoto) | (1 << KindLink) | (1 << KindText) | (1 << KindWorld) | (1 << KindProp) | (1 << KindAvatar) | (1 << KindServer) | (1 << KindFolder);

    public SharePanelRow[] Rows;
    public TextMeshPro TitleText;
    public TextMeshPro CountText;
    public TextMeshPro PageText;
    public TextMeshPro StatusText;
    public BasisInteractableButton PrevPageButton;
    public BasisInteractableButton NextPageButton;

    // Filter buttons, in the order of FilterKinds (-1 = turn every kind on). Each kind is its
    // own switch, shown by its lamp.
    public BasisInteractableButton[] FilterButtons;
    public TextMeshPro[] FilterLabels;
    public MeshRenderer[] FilterLamps;
    public int[] FilterKinds;
    public Color FilterOffColor = new Color(0.82f, 0.83f, 0.85f);
    public Color FilterOnText = new Color(0.18f, 0.31f, 0.23f);
    public Color FilterOffText = new Color(0.6f, 0.62f, 0.65f);

    public BasisInteractableButton LanguageJaButton;
    public BasisInteractableButton LanguageEnButton;
    public BasisInteractableButton MoveButton;
    public TextMeshPro MoveLabel;
    // The board's own collider: the pickup grabs by it, so turning it off stops moving.
    public Collider GrabCollider;

    public TextMeshPro PreviewTitle;
    public TextMeshPro PreviewMeta;
    public TextMeshPro PreviewBody;
    public TextMeshPro PreviewPageText;
    public BasisInteractableButton PreviewPrevButton;
    public BasisInteractableButton PreviewNextButton;
    public BasisInteractableButton DeleteButton;
    public TextMeshPro DeleteLabel;
    public MeshRenderer PreviewImage;
    public float PreviewImageWidth = 0.5f;
    public float PreviewImageHeight = 0.36f;
    public float BringDistance = 0.7f;
    // Rough size of the preview text box, for splitting long text into pages: full-width
    // characters per line and lines per page.
    public float PreviewLineUnits = 15.5f;
    public int PreviewLines = 10;

    public Color[] KindColors;

    private BasisNetworkShim net;
    private int japanese;
    private int sourceReady;
    private float noticeUntil;
    private float startedAt;
    private float nextClockRender;

    // Items as this client sees them, rebuilt by the source each refresh.
    private string[] itemKey;
    private int[] itemKind;
    private string[] itemTitle;
    private string[] itemSharer;
    private string[] itemBody;
    private Texture[] itemThumb;
    private float[] itemAspect;
    private int[] itemSaved;
    private int[] itemRemovable;
    private int itemCount;
    // Staging for the refresh in progress.
    private string[] nextKey;
    private int[] nextKind;
    private string[] nextTitle;
    private string[] nextSharer;
    private string[] nextBody;
    private Texture[] nextThumb;
    private float[] nextAspect;
    private int[] nextSaved;
    private int[] nextRemovable;
    private int nextCount;

    // When each share was first seen (UTC ticks, 0 = before the panel was there) and who shared
    // the photos; the owner's values win and travel with the order.
    private Dictionary<string, long> seenAt;
    private Dictionary<string, string> sharerOf;

    // Shared view.
    private List<string> order;
    private int page;
    private int filterMask;
    private string selectedKey;
    private int previewPage;
    private int moveEnabled;
    // Indices into the item arrays, in display order (after the filter).
    private int[] display;
    private int displayCount;
    private string[] previewPages;
    private int previewPageCount;

    // Joining a server and deleting take a second press.
    private string confirmKey;
    private float confirmUntil;
    private string deleteKey;
    private float deleteUntil;

    // Requests for the source: an action, an item key and, for bringing a photo, where to.
    private int[] requestAction;
    private string[] requestKey;
    private Vector3[] requestPosition;
    private Quaternion[] requestRotation;
    // 1 when this client pressed the button, 0 when another client asked (bring only).
    private int[] requestLocal;
    private int requestCount;

    private void Start()
    {
        itemKey = new string[Capacity];
        itemKind = new int[Capacity];
        itemTitle = new string[Capacity];
        itemSharer = new string[Capacity];
        itemBody = new string[Capacity];
        itemThumb = new Texture[Capacity];
        itemAspect = new float[Capacity];
        itemSaved = new int[Capacity];
        itemRemovable = new int[Capacity];
        nextKey = new string[Capacity];
        nextKind = new int[Capacity];
        nextTitle = new string[Capacity];
        nextSharer = new string[Capacity];
        nextBody = new string[Capacity];
        nextThumb = new Texture[Capacity];
        nextAspect = new float[Capacity];
        nextSaved = new int[Capacity];
        nextRemovable = new int[Capacity];
        display = new int[Capacity];
        seenAt = new Dictionary<string, long>();
        sharerOf = new Dictionary<string, string>();
        order = new List<string>();
        selectedKey = "";
        confirmKey = "";
        deleteKey = "";
        filterMask = AllKinds;
        // Fixed in place until someone turns moving on, so a press never drags the board.
        moveEnabled = 0;
        previewPages = new string[1];
        previewPages[0] = "";
        previewPageCount = 1;
        requestAction = new int[RequestCapacity];
        requestKey = new string[RequestCapacity];
        requestPosition = new Vector3[RequestCapacity];
        requestRotation = new Quaternion[RequestCapacity];
        requestLocal = new int[RequestCapacity];
        startedAt = Time.time;

        japanese = Application.systemLanguage == SystemLanguage.Japanese ? 1 : 0;

        PrevPageButton.ButtonDown = OnPrevPage;
        NextPageButton.ButtonDown = OnNextPage;
        PreviewPrevButton.ButtonDown = OnPreviewPrev;
        PreviewNextButton.ButtonDown = OnPreviewNext;
        DeleteButton.ButtonDown = OnDelete;
        LanguageJaButton.ButtonDown = OnLanguageJa;
        LanguageEnButton.ButtonDown = OnLanguageEn;
        MoveButton.ButtonDown = OnMoveToggle;
        FilterButtons[0].ButtonDown = OnFilter0;
        FilterButtons[1].ButtonDown = OnFilter1;
        FilterButtons[2].ButtonDown = OnFilter2;
        FilterButtons[3].ButtonDown = OnFilter3;
        FilterButtons[4].ButtonDown = OnFilter4;
        FilterButtons[5].ButtonDown = OnFilter5;
        FilterButtons[6].ButtonDown = OnFilter6;
        FilterButtons[7].ButtonDown = OnFilter7;
        FilterButtons[8].ButtonDown = OnFilter8;

        net = SafeUtil.MakeNetworkable(this);
        if (net != null)
        {
            net.NetworkMessageReceived += OnNetworkMessage;
            net.PlayerJoined += OnPlayerJoined;
        }

        ApplyMove();
        ApplyLanguage();
    }

    private void Update()
    {
        if (noticeUntil > 0f && Time.time > noticeUntil)
        {
            noticeUntil = 0f;
            StatusText.text = sourceReady == 0 ? PermissionNotice() : "";
        }
        // "3 min ago" moves on by itself.
        if (Time.time > nextClockRender)
        {
            nextClockRender = Time.time + 20f;
            Render();
        }
    }

    /// <summary>Japanese or English text, by this player's choice.</summary>
    public string T(string ja, string en)
    {
        return japanese == 1 ? ja : en;
    }

    private string PermissionNotice()
    {
        return T("共有物を読むには、このプロップのスクリプトを許可してください", "Allow this prop's scripts to list what is shared");
    }

    // ---- Language and moving ----

    private void OnLanguageJa()
    {
        japanese = 1;
        ApplyLanguage();
    }

    private void OnLanguageEn()
    {
        japanese = 0;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        TitleText.text = T("みんなの共有", "Shared here");
        if (noticeUntil == 0f) StatusText.text = sourceReady == 0 ? PermissionNotice() : "";
        ApplyMove();
        Render();
    }

    private void OnMoveToggle()
    {
        moveEnabled = moveEnabled == 1 ? 0 : 1;
        ApplyMove();
        SendView();
    }

    private void ApplyMove()
    {
        GrabCollider.enabled = moveEnabled == 1;
        MoveLabel.text = moveEnabled == 1 ? T("移動：オン", "Move: on") : T("移動：オフ", "Move: off");
    }

    // ---- Filter ----

    private void OnFilter0() { SetFilter(0); }
    private void OnFilter1() { SetFilter(1); }
    private void OnFilter2() { SetFilter(2); }
    private void OnFilter3() { SetFilter(3); }
    private void OnFilter4() { SetFilter(4); }
    private void OnFilter5() { SetFilter(5); }
    private void OnFilter6() { SetFilter(6); }
    private void OnFilter7() { SetFilter(7); }
    private void OnFilter8() { SetFilter(8); }

    private void SetFilter(int button)
    {
        int kind = FilterKinds[button];
        // "All" turns every kind on, or off when they are all on already (then pick a few).
        if (kind < 0) filterMask = filterMask == AllKinds ? 0 : AllKinds;
        else filterMask ^= 1 << kind;
        page = 0;
        SendView();
        Render();
    }

    private bool FilterOn(int button)
    {
        int kind = FilterKinds[button];
        return kind < 0 ? filterMask == AllKinds : (filterMask & (1 << kind)) != 0;
    }

    private bool Shown(int kind)
    {
        if (kind == KindOther) return filterMask == AllKinds;
        return (filterMask & (1 << kind)) != 0;
    }

    private void RenderFilters()
    {
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        for (int i = 0; i < FilterLabels.Length; i++)
        {
            int kind = FilterKinds[i];
            bool on = FilterOn(i);
            FilterLabels[i].text = kind < 0 ? T("すべて", "All") : KindLabel(kind);
            FilterLabels[i].color = on ? FilterOnText : FilterOffText;
            block.SetColor("_BaseColor", on ? (kind < 0 ? FilterOnText : KindColor(kind)) : FilterOffColor);
            FilterLamps[i].SetPropertyBlock(block);
        }
    }

    // ---- Called by the source (permission-gated) ----

    /// <summary>The source is running: the permission notice goes.</summary>
    public void SourceReady()
    {
        if (sourceReady == 1) return;
        sourceReady = 1;
        if (noticeUntil == 0f) StatusText.text = "";
    }

    public void BeginItems()
    {
        nextCount = 0;
    }

    public void AddItem(string key, int kind, string title, string sharer, string body, Texture thumb, float aspect, bool saved, bool removable)
    {
        if (nextCount >= Capacity || string.IsNullOrEmpty(key)) return;
        nextKey[nextCount] = key;
        nextKind[nextCount] = kind;
        nextTitle[nextCount] = Clean(title);
        nextSharer[nextCount] = Clean(sharer);
        nextBody[nextCount] = Clean(body);
        nextThumb[nextCount] = thumb;
        nextAspect[nextCount] = aspect > 0.01f ? aspect : 1f;
        nextSaved[nextCount] = saved ? 1 : 0;
        nextRemovable[nextCount] = removable ? 1 : 0;
        nextCount++;
    }

    public void EndItems()
    {
        bool changed = nextCount != itemCount;
        for (int i = 0; i < nextCount && !changed; i++)
        {
            if (nextKey[i] != itemKey[i] || nextTitle[i] != itemTitle[i] || nextThumb[i] != itemThumb[i] || nextBody[i] != itemBody[i] || nextSharer[i] != itemSharer[i] || nextSaved[i] != itemSaved[i] || nextRemovable[i] != itemRemovable[i]) changed = true;
        }
        string[] k = itemKey; itemKey = nextKey; nextKey = k;
        int[] kd = itemKind; itemKind = nextKind; nextKind = kd;
        string[] t = itemTitle; itemTitle = nextTitle; nextTitle = t;
        string[] s = itemSharer; itemSharer = nextSharer; nextSharer = s;
        string[] b = itemBody; itemBody = nextBody; nextBody = b;
        Texture[] th = itemThumb; itemThumb = nextThumb; nextThumb = th;
        float[] a = itemAspect; itemAspect = nextAspect; nextAspect = a;
        int[] sv = itemSaved; itemSaved = nextSaved; nextSaved = sv;
        int[] rm = itemRemovable; itemRemovable = nextRemovable; nextRemovable = rm;
        itemCount = nextCount;

        // First sight: shares the panel finds on its first look were there before it.
        bool firstLook = Time.time - startedAt < FirstLookSeconds;
        for (int i = 0; i < itemCount; i++)
        {
            if (seenAt.ContainsKey(itemKey[i])) continue;
            seenAt[itemKey[i]] = firstLook ? 0L : System.DateTime.UtcNow.Ticks;
        }

        if (IsAuthority() && UpdateOrder())
        {
            SendOrder(null);
            changed = true;
        }
        if (changed) Render();
    }

    /// <summary>
    /// The source saw a photo for the first time while this client moves it — which, right after a
    /// photo is shared, means this player shared it. Everyone is told the name.
    /// </summary>
    public void ClaimSharer(string key)
    {
        if (sharerOf.ContainsKey(key)) return;
        string name = LocalName();
        if (name.Length == 0) return;
        sharerOf[key] = name;
        SendSharer(key, name);
        Render();
    }

    public int RequestCount()
    {
        return requestCount;
    }

    public int RequestAction(int i)
    {
        return requestAction[i];
    }

    public string RequestKey(int i)
    {
        return requestKey[i];
    }

    public Vector3 RequestPosition(int i)
    {
        return requestPosition[i];
    }

    public Quaternion RequestRotation(int i)
    {
        return requestRotation[i];
    }

    public bool RequestIsLocal(int i)
    {
        return requestLocal[i] != 0;
    }

    public void ClearRequests()
    {
        requestCount = 0;
    }

    /// <summary>A short line on the panel for the person who pressed something ("Copied").</summary>
    public void Notice(string ja, string en)
    {
        StatusText.text = T(ja, en);
        noticeUntil = Time.time + 3f;
    }

    // ---- Rows ----

    /// <summary>A row's button: slot 0 shows the item large for everyone, 1-3 are its actions.</summary>
    public void RowPressed(int row, int slot)
    {
        int at = page * Rows.Length + row;
        if (at >= displayCount) return;
        int item = display[at];
        if (slot == 0)
        {
            selectedKey = itemKey[item];
            previewPage = 0;
            SendView();
            Render();
            return;
        }
        int action = ActionFor(itemKind[item], slot);
        if (action == 0) return;
        if (action == ActionOpen)
        {
            BasisExternalLinkShim.Open(this, itemBody[item]);
            return;
        }
        if (sourceReady == 0) return;
        if (action == ActionSaveOrb && itemSaved[item] != 0)
        {
            Notice("保存済みです", "Already saved");
            return;
        }
        if (action == ActionConnect)
        {
            // Leaving this server is a big step: the first press only asks.
            if (confirmKey != itemKey[item] || Time.time > confirmUntil)
            {
                confirmKey = itemKey[item];
                confirmUntil = Time.time + ConfirmSeconds;
                StatusText.text = T("もう一度「接続」を押すと " + itemTitle[item] + " に移動します", "Press Join again to move to " + itemTitle[item]);
                noticeUntil = confirmUntil;
                return;
            }
            confirmKey = "";
        }
        Vector3 position;
        Quaternion rotation;
        BringPose(out position, out rotation);
        // A photo moves on the client that controls it, so everyone hears where to put it.
        if (action == ActionBring) SendBring(itemKey[item], position, rotation);
        Queue(action, itemKey[item], position, rotation, 1);
        if (action == ActionSaveOrb) Notice("メニューの確認で「保存」を押してください", "Press Save in the menu to keep it");
    }

    // The large view's delete button: removes the share shown there, for everyone. The first
    // press only asks.
    private void OnDelete()
    {
        int item = Find(selectedKey);
        if (item < 0 || itemRemovable[item] == 0 || sourceReady == 0) return;
        if (deleteKey != selectedKey || Time.time > deleteUntil)
        {
            deleteKey = selectedKey;
            deleteUntil = Time.time + ConfirmSeconds;
            DeleteLabel.text = T("本当に削除", "Really delete");
            StatusText.text = T("もう一度「削除」を押すと、全員の前から消えます", "Press Delete again to remove it for everyone");
            noticeUntil = deleteUntil;
            return;
        }
        deleteKey = "";
        DeleteLabel.text = T("削除", "Delete");
        Queue(ActionDelete, selectedKey, Vector3.zero, Quaternion.identity, 1);
    }

    private void Queue(int action, string key, Vector3 position, Quaternion rotation, int local)
    {
        if (requestCount >= RequestCapacity) return;
        requestAction[requestCount] = action;
        requestKey[requestCount] = key;
        requestPosition[requestCount] = position;
        requestRotation[requestCount] = rotation;
        requestLocal[requestCount] = local;
        requestCount++;
    }

    /// <summary>What a row's action slot (1-3) does for an item of this kind; 0 for nothing.</summary>
    public int ActionFor(int kind, int slot)
    {
        if (kind == KindPhoto)
        {
            if (slot == 1) return ActionBring;
            if (slot == 2) return ActionCopy;
            if (slot == 3) return ActionSave;
        }
        else if (kind == KindLink)
        {
            if (slot == 1) return ActionOpen;
            if (slot == 2) return ActionCopy;
        }
        else if (kind == KindText)
        {
            if (slot == 1) return ActionCopy;
        }
        else if (kind == KindServer)
        {
            if (slot == 1) return ActionConnect;
            if (slot == 2) return ActionSaveOrb;
        }
        else if (kind == KindProp)
        {
            if (slot == 1) return ActionSpawn;
            if (slot == 2) return ActionSaveOrb;
        }
        else if (kind == KindWorld || kind == KindAvatar || kind == KindFolder)
        {
            if (slot == 1) return ActionSaveOrb;
        }
        return 0;
    }

    private string ActionLabel(int action, int item)
    {
        if (action == ActionSaveOrb) return itemSaved[item] != 0 ? T("保存済み", "Saved") : T("保存", "Save");
        if (action == ActionBring) return T("手元に", "Bring");
        if (action == ActionCopy) return T("コピー", "Copy");
        if (action == ActionSave) return T("保存", "Save");
        if (action == ActionOpen) return T("開く", "Open");
        if (action == ActionConnect) return T("接続", "Join");
        if (action == ActionSpawn) return T("取り出す", "Spawn");
        return "";
    }

    private string KindLabel(int kind)
    {
        if (kind == KindPhoto) return T("写真", "Photo");
        if (kind == KindLink) return T("リンク", "Link");
        if (kind == KindText) return T("テキスト", "Text");
        if (kind == KindWorld) return T("ワールド", "World");
        if (kind == KindProp) return T("プロップ", "Prop");
        if (kind == KindAvatar) return T("アバター", "Avatar");
        if (kind == KindServer) return T("サーバー", "Server");
        if (kind == KindFolder) return T("フォルダー", "Folder");
        return T("その他", "Other");
    }

    // In front of the local player's eyes, facing them.
    private void BringPose(out Vector3 position, out Quaternion rotation)
    {
        position = transform.position;
        rotation = transform.rotation;
        Camera cam = BasisLocalCameraDriver.CameraInstance;
        if (cam == null) return;
        Transform head = cam.transform;
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();
        position = head.position + forward * BringDistance;
        rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    private string LocalName()
    {
        IBasisPlayer player = BasisPlayersShim.Local;
        if (player == null) return "";
        string name = player.DisplayName;
        return name == null ? "" : name;
    }

    // ---- Paging ----

    private void OnPrevPage()
    {
        if (page <= 0) return;
        page--;
        SendView();
        Render();
    }

    private void OnNextPage()
    {
        if ((page + 1) * Rows.Length >= displayCount) return;
        page++;
        SendView();
        Render();
    }

    private void OnPreviewPrev()
    {
        if (previewPage <= 0) return;
        previewPage--;
        SendView();
        Render();
    }

    private void OnPreviewNext()
    {
        if (previewPage + 1 >= previewPageCount) return;
        previewPage++;
        SendView();
        Render();
    }

    // ---- Drawing ----

    private void Render()
    {
        if (itemKey == null) return;
        BuildDisplay();
        int rows = Rows.Length;
        int pages = displayCount == 0 ? 1 : (displayCount + rows - 1) / rows;
        if (page >= pages) page = pages - 1;
        if (page < 0) page = 0;

        string count = japanese == 1 ? itemCount + " 件" : itemCount + (itemCount == 1 ? " item" : " items");
        if (filterMask != AllKinds) count = displayCount + " / " + count;
        CountText.text = count;
        PageText.text = pages > 1 ? (page + 1) + " / " + pages : "";
        PrevPageButton.gameObject.SetActive(pages > 1);
        NextPageButton.gameObject.SetActive(pages > 1);
        RenderFilters();

        for (int r = 0; r < rows; r++)
        {
            SharePanelRow row = Rows[r];
            int at = page * rows + r;
            if (at >= displayCount)
            {
                // An empty list says so in its first row.
                if (r == 0 && displayCount == 0)
                {
                    row.ShowMessage(itemCount == 0 ? T("まだ何も共有されていません", "Nothing shared yet") : T("選んだ種類の共有はありません", "Nothing of the chosen kinds"));
                    continue;
                }
                row.Hide();
                continue;
            }
            int item = display[at];
            int kind = itemKind[item];
            row.Show(KindLabel(kind), KindColor(kind), RowTitle(item), Meta(item), itemThumb[item], itemAspect[item],
                ActionLabel(ActionFor(kind, 1), item), ActionLabel(ActionFor(kind, 2), item), ActionLabel(ActionFor(kind, 3), item),
                itemKey[item] == selectedKey);
        }
        RenderPreview();
        int shown = Find(selectedKey);
        bool removable = shown >= 0 && itemRemovable[shown] != 0 && sourceReady == 1;
        DeleteButton.gameObject.SetActive(removable);
        if (removable && (deleteKey != selectedKey || Time.time > deleteUntil)) DeleteLabel.text = T("削除", "Delete");
        PreviewPrevButton.gameObject.SetActive(previewPageCount > 1);
        PreviewNextButton.gameObject.SetActive(previewPageCount > 1);
    }

    // "Photo・Alice・3 min ago"
    private string Meta(int item)
    {
        string meta = KindLabel(itemKind[item]);
        string sharer = Sharer(item);
        if (sharer.Length > 0) meta += "・" + sharer;
        string when = When(itemKey[item]);
        if (when.Length > 0) meta += "・" + when;
        return meta;
    }

    private string Sharer(int item)
    {
        if (itemSharer[item].Length > 0) return itemSharer[item];
        string name;
        if (sharerOf.TryGetValue(itemKey[item], out name)) return name;
        return "";
    }

    private string When(string key)
    {
        long ticks;
        if (!seenAt.TryGetValue(key, out ticks) || ticks == 0L) return "";
        System.DateTime at = new System.DateTime(ticks, System.DateTimeKind.Utc);
        double minutes = (System.DateTime.UtcNow - at).TotalMinutes;
        if (minutes < 1.0) return T("たった今", "just now");
        if (minutes < 60.0)
        {
            int m = (int)minutes;
            return japanese == 1 ? m + "分前" : m + (m == 1 ? " min ago" : " mins ago");
        }
        return at.ToLocalTime().ToString("HH:mm");
    }

    private string RowTitle(int item)
    {
        string title = itemTitle[item];
        if (title.Length == 0) title = itemBody[item];
        if (title.Length == 0) title = KindLabel(itemKind[item]);
        title = title.Replace('\n', ' ');
        if (title.Length > 80) title = title.Substring(0, 80) + "…";
        return title;
    }

    private Color KindColor(int kind)
    {
        if (KindColors != null && kind >= 0 && kind < KindColors.Length) return KindColors[kind];
        return Color.gray;
    }

    private void RenderPreview()
    {
        int item = Find(selectedKey);
        if (item < 0)
        {
            PreviewTitle.text = "";
            PreviewMeta.text = "";
            PreviewBody.text = T("一覧の項目を押すと、ここに大きく表示します", "Press an item in the list to show it here");
            PreviewImage.gameObject.SetActive(false);
            SetPreviewPages("");
            PreviewPageText.text = "";
            return;
        }
        int kind = itemKind[item];
        PreviewTitle.text = itemTitle[item].Length > 0 ? itemTitle[item] : KindLabel(kind);
        PreviewMeta.text = Meta(item);

        Texture thumb = itemThumb[item];
        bool showImage = thumb != null && (kind == KindPhoto || kind == KindWorld || kind == KindProp || kind == KindAvatar);
        if (showImage)
        {
            PreviewImage.gameObject.SetActive(true);
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            block.SetTexture("_BaseMap", thumb);
            block.SetTexture("_MainTex", thumb);
            PreviewImage.SetPropertyBlock(block);
            float aspect = itemAspect[item];
            float w = PreviewImageWidth;
            float h = w / aspect;
            if (h > PreviewImageHeight)
            {
                h = PreviewImageHeight;
                w = h * aspect;
            }
            PreviewImage.transform.localScale = new Vector3(w, h, 1f);
        }
        else
        {
            PreviewImage.gameObject.SetActive(false);
        }

        if (kind == KindLink || kind == KindText)
        {
            SetPreviewPages(itemBody[item]);
            if (previewPage >= previewPageCount) previewPage = previewPageCount - 1;
            PreviewBody.text = Verbatim(previewPages[previewPage]);
            PreviewPageText.text = previewPageCount > 1 ? (previewPage + 1) + " / " + previewPageCount : "";
        }
        else
        {
            SetPreviewPages("");
            if (showImage) PreviewBody.text = "";
            else if (kind == KindPhoto) PreviewBody.text = T("読み込み中…", "Loading…");
            else PreviewBody.text = Verbatim(itemBody[item]);
            PreviewPageText.text = "";
        }
    }

    // Splits text into pages that fit the preview box, by an estimate of wrapped lines
    // (full-width characters count as one unit, narrower ones as about half).
    private void SetPreviewPages(string text)
    {
        List<string> pages = new List<string>();
        StringBuilder current = new StringBuilder();
        int lines = 1;
        float units = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                lines++;
                units = 0f;
            }
            else
            {
                units += c < 0x2E80 ? 0.55f : 1f;
                if (units > PreviewLineUnits)
                {
                    lines++;
                    units = c < 0x2E80 ? 0.55f : 1f;
                }
            }
            if (lines > PreviewLines)
            {
                pages.Add(current.ToString());
                current.Length = 0;
                lines = 1;
                if (c == '\n') continue;
            }
            current.Append(c);
        }
        pages.Add(current.ToString());
        previewPages = pages.ToArray();
        previewPageCount = previewPages.Length;
    }

    // The list order: the owner's order first, then anything this client has that the owner's
    // order does not have yet; then the type filter.
    private void BuildDisplay()
    {
        displayCount = 0;
        int[] used = new int[itemCount];
        for (int o = 0; o < order.Count; o++)
        {
            int item = Find(order[o]);
            if (item < 0 || used[item] != 0) continue;
            used[item] = 1;
            if (Shown(itemKind[item])) display[displayCount++] = item;
        }
        for (int i = 0; i < itemCount; i++)
        {
            if (used[i] != 0) continue;
            if (Shown(itemKind[i])) display[displayCount++] = i;
        }
    }

    private int Find(string key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        for (int i = 0; i < itemCount; i++)
        {
            if (itemKey[i] == key) return i;
        }
        return -1;
    }

    // Newest first: keys the order has not seen go on top; keys that are gone drop out.
    private bool UpdateOrder()
    {
        List<string> next = new List<string>();
        for (int i = itemCount - 1; i >= 0; i--)
        {
            if (!order.Contains(itemKey[i])) next.Add(itemKey[i]);
        }
        for (int o = 0; o < order.Count; o++)
        {
            if (Find(order[o]) >= 0) next.Add(order[o]);
        }
        if (next.Count == order.Count)
        {
            bool same = true;
            for (int i = 0; i < next.Count && same; i++) same = next[i] == order[i];
            if (same) return false;
        }
        order = next;
        return true;
    }

    // Remote text is shown exactly as written: no control characters or direction overrides,
    // and with rich text off TMP still decodes \u escapes, so a zero-width space follows each
    // backslash.
    private string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        StringBuilder sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r') continue;
            if (c < 0x20 && c != '\n' && c != '\t') continue;
            if (c == 0x7F) continue;
            if (c >= 0x200B && c <= 0x200F) continue;
            if (c >= 0x202A && c <= 0x202E) continue;
            if (c >= 0x2060 && c <= 0x206F) continue;
            if (c == 0xFEFF) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private string Verbatim(string text)
    {
        return text.Replace("\\", "\\​");
    }

    // ---- Networking ----

    private bool IsAuthority()
    {
        return net == null || !net.HasNetworkID || net.IsOwnedLocallyOnServer;
    }

    // [MsgOrder, count (2 bytes), then per share: key length, key (ASCII), first seen (UTC
    //  ticks, 8 bytes, 0 = unknown), sharer length, sharer (UTF-8, photos only)]
    private void SendOrder(ushort[] to)
    {
        if (net == null || !net.HasNetworkID) return;
        // (No byte[][] here: an array of arrays is a type the prop whitelist does not cover.)
        int count = order.Count;
        int size = 3;
        for (int i = 0; i < count; i++) size += 1 + order[i].Length + 8 + 1 + SharerBytes(order[i]).Length;
        byte[] buffer = new byte[size];
        buffer[0] = MsgOrder;
        buffer[1] = (byte)(count & 0xFF);
        buffer[2] = (byte)((count >> 8) & 0xFF);
        int at = 3;
        for (int i = 0; i < count; i++)
        {
            string key = order[i];
            buffer[at++] = (byte)key.Length;
            for (int c = 0; c < key.Length; c++) buffer[at++] = (byte)key[c];
            long ticks;
            if (!seenAt.TryGetValue(key, out ticks)) ticks = 0L;
            byte[] t = System.BitConverter.GetBytes(ticks);
            for (int c = 0; c < 8; c++) buffer[at++] = t[c];
            byte[] name = SharerBytes(key);
            buffer[at++] = (byte)name.Length;
            for (int c = 0; c < name.Length; c++) buffer[at++] = name[c];
        }
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, to);
    }

    private byte[] SharerBytes(string key)
    {
        string name;
        if (!sharerOf.TryGetValue(key, out name)) return new byte[0];
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        return bytes.Length > 255 ? new byte[0] : bytes;
    }

    private void SendView()
    {
        SendViewTo(null);
    }

    // [MsgView, page (2 bytes), preview page (2 bytes), filter bits (2 bytes), move on, key length, key]
    private void SendViewTo(ushort[] to)
    {
        if (net == null || !net.HasNetworkID) return;
        string key = selectedKey == null ? "" : selectedKey;
        byte[] buffer = new byte[9 + key.Length];
        buffer[0] = MsgView;
        buffer[1] = (byte)(page & 0xFF);
        buffer[2] = (byte)((page >> 8) & 0xFF);
        buffer[3] = (byte)(previewPage & 0xFF);
        buffer[4] = (byte)((previewPage >> 8) & 0xFF);
        buffer[5] = (byte)(filterMask & 0xFF);
        buffer[6] = (byte)((filterMask >> 8) & 0xFF);
        buffer[7] = (byte)moveEnabled;
        buffer[8] = (byte)key.Length;
        for (int c = 0; c < key.Length; c++) buffer[9 + c] = (byte)key[c];
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, to);
    }

    // [MsgBring, position (3 floats), rotation (4 floats), key length, key]
    private void SendBring(string key, Vector3 position, Quaternion rotation)
    {
        if (net == null || !net.HasNetworkID) return;
        byte[] buffer = new byte[30 + key.Length];
        buffer[0] = MsgBring;
        PutFloat(buffer, 1, position.x);
        PutFloat(buffer, 5, position.y);
        PutFloat(buffer, 9, position.z);
        PutFloat(buffer, 13, rotation.x);
        PutFloat(buffer, 17, rotation.y);
        PutFloat(buffer, 21, rotation.z);
        PutFloat(buffer, 25, rotation.w);
        buffer[29] = (byte)key.Length;
        for (int c = 0; c < key.Length; c++) buffer[30 + c] = (byte)key[c];
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, null);
    }

    // [MsgSharer, key length, key, name length, name (UTF-8)]
    private void SendSharer(string key, string name)
    {
        if (net == null || !net.HasNetworkID) return;
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > 255) return;
        byte[] buffer = new byte[3 + key.Length + bytes.Length];
        buffer[0] = MsgSharer;
        buffer[1] = (byte)key.Length;
        for (int c = 0; c < key.Length; c++) buffer[2 + c] = (byte)key[c];
        buffer[2 + key.Length] = (byte)bytes.Length;
        for (int c = 0; c < bytes.Length; c++) buffer[3 + key.Length + c] = bytes[c];
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, null);
    }

    private void PutFloat(byte[] buffer, int at, float value)
    {
        byte[] bytes = System.BitConverter.GetBytes(value);
        buffer[at] = bytes[0];
        buffer[at + 1] = bytes[1];
        buffer[at + 2] = bytes[2];
        buffer[at + 3] = bytes[3];
    }

    private void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || net == null || !net.IsOwnedLocallyOnServer) return;
        ushort[] to = new ushort[] { player.playerId };
        SendOrder(to);
        SendViewTo(to);
    }

    private void OnNetworkMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 1) return;
        if (buffer[0] == MsgOrder && buffer.Length >= 3)
        {
            int count = buffer[1] | (buffer[2] << 8);
            List<string> next = new List<string>();
            int at = 3;
            for (int i = 0; i < count && at < buffer.Length; i++)
            {
                int length = buffer[at++];
                if (at + length + 9 > buffer.Length) break;
                string key = Encoding.ASCII.GetString(buffer, at, length);
                at += length;
                seenAt[key] = System.BitConverter.ToInt64(buffer, at);
                at += 8;
                int nameLength = buffer[at++];
                if (at + nameLength > buffer.Length) break;
                if (nameLength > 0) sharerOf[key] = Encoding.UTF8.GetString(buffer, at, nameLength);
                at += nameLength;
                next.Add(key);
            }
            order = next;
            Render();
        }
        else if (buffer[0] == MsgView && buffer.Length >= 9)
        {
            page = buffer[1] | (buffer[2] << 8);
            previewPage = buffer[3] | (buffer[4] << 8);
            filterMask = buffer[5] | (buffer[6] << 8);
            moveEnabled = buffer[7] != 0 ? 1 : 0;
            int length = buffer[8];
            selectedKey = 9 + length <= buffer.Length ? Encoding.ASCII.GetString(buffer, 9, length) : "";
            ApplyMove();
            Render();
        }
        else if (buffer[0] == MsgBring && buffer.Length >= 30)
        {
            int length = buffer[29];
            if (30 + length > buffer.Length) return;
            Vector3 position = new Vector3(System.BitConverter.ToSingle(buffer, 1), System.BitConverter.ToSingle(buffer, 5), System.BitConverter.ToSingle(buffer, 9));
            Quaternion rotation = new Quaternion(System.BitConverter.ToSingle(buffer, 13), System.BitConverter.ToSingle(buffer, 17), System.BitConverter.ToSingle(buffer, 21), System.BitConverter.ToSingle(buffer, 25));
            Queue(ActionBring, Encoding.ASCII.GetString(buffer, 30, length), position, rotation, 0);
        }
        else if (buffer[0] == MsgSharer && buffer.Length >= 3)
        {
            int length = buffer[1];
            if (2 + length >= buffer.Length) return;
            string key = Encoding.ASCII.GetString(buffer, 2, length);
            int nameLength = buffer[2 + length];
            if (3 + length + nameLength > buffer.Length) return;
            sharerOf[key] = Encoding.UTF8.GetString(buffer, 3 + length, nameLength);
            Render();
        }
    }
}
