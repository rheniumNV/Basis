using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// A panel listing what has been shared on the server: photos (image pickups), links, text and
/// the world / prop / avatar orbs. Everyone sees the same screen: the panel's network owner
/// decides the order of the list (newest first) and the page and the item shown large are shared.
/// The buttons on a row act for whoever presses them, so several people can use it at once.
///
/// This script uses only what a prop may use without asking. Reading the shares and the actions
/// that need Basis itself (copy, save, bringing a photo over) live in <see cref="SharePanelSource"/>,
/// which needs the player's permission; it pushes the items in and pulls the requests out, so a
/// refused source leaves this script running with a notice on the panel.
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

    public const int ActionBring = 1;
    public const int ActionCopy = 2;
    public const int ActionSave = 3;
    public const int ActionOpen = 4;

    private const byte MsgOrder = 1;
    private const byte MsgView = 2;
    private const byte MsgBring = 3;
    private const int Capacity = 64;
    private const int RequestCapacity = 16;

    public SharePanelRow[] Rows;
    public TextMeshPro TitleText;
    public TextMeshPro CountText;
    public TextMeshPro PageText;
    public TextMeshPro StatusText;
    public BasisInteractableButton PrevPageButton;
    public BasisInteractableButton NextPageButton;

    public TextMeshPro PreviewTitle;
    public TextMeshPro PreviewMeta;
    public TextMeshPro PreviewBody;
    public TextMeshPro PreviewPageText;
    public BasisInteractableButton PreviewPrevButton;
    public BasisInteractableButton PreviewNextButton;
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

    // Items as this client sees them, rebuilt by the source each refresh.
    private string[] itemKey;
    private int[] itemKind;
    private string[] itemTitle;
    private string[] itemSharer;
    private string[] itemBody;
    private Texture[] itemThumb;
    private float[] itemAspect;
    private int itemCount;
    // Staging for the refresh in progress.
    private string[] nextKey;
    private int[] nextKind;
    private string[] nextTitle;
    private string[] nextSharer;
    private string[] nextBody;
    private Texture[] nextThumb;
    private float[] nextAspect;
    private int nextCount;

    // Shared view: the order of the list (from the owner), the page and the item shown large.
    private List<string> order;
    private int page;
    private string selectedKey;
    private int previewPage;
    // Indices into the item arrays, in display order.
    private int[] display;
    private int displayCount;
    private string[] previewPages;
    private int previewPageCount;

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
        nextKey = new string[Capacity];
        nextKind = new int[Capacity];
        nextTitle = new string[Capacity];
        nextSharer = new string[Capacity];
        nextBody = new string[Capacity];
        nextThumb = new Texture[Capacity];
        nextAspect = new float[Capacity];
        display = new int[Capacity];
        order = new List<string>();
        selectedKey = "";
        previewPages = new string[1];
        previewPages[0] = "";
        previewPageCount = 1;
        requestAction = new int[RequestCapacity];
        requestKey = new string[RequestCapacity];
        requestPosition = new Vector3[RequestCapacity];
        requestRotation = new Quaternion[RequestCapacity];
        requestLocal = new int[RequestCapacity];

        japanese = Application.systemLanguage == SystemLanguage.Japanese ? 1 : 0;

        PrevPageButton.ButtonDown = OnPrevPage;
        NextPageButton.ButtonDown = OnNextPage;
        PreviewPrevButton.ButtonDown = OnPreviewPrev;
        PreviewNextButton.ButtonDown = OnPreviewNext;

        net = SafeUtil.MakeNetworkable(this);
        if (net != null)
        {
            net.NetworkMessageReceived += OnNetworkMessage;
            net.PlayerJoined += OnPlayerJoined;
        }

        if (TitleText != null) TitleText.text = T("みんなの共有", "Shared here");
        // Until the source reports in, the panel can only say it needs permission.
        if (sourceReady == 0) StatusText.text = T("共有物を読むには、このプロップのスクリプトを許可してください", "Allow this prop's scripts to list what is shared");
        Render();
    }

    private void Update()
    {
        if (noticeUntil > 0f && Time.time > noticeUntil)
        {
            noticeUntil = 0f;
            StatusText.text = "";
        }
    }

    private string T(string ja, string en)
    {
        return japanese == 1 ? ja : en;
    }

    // ---- Called by the source (permission-gated) ----

    /// <summary>The source is running: the permission notice goes.</summary>
    public void SourceReady()
    {
        sourceReady = 1;
        if (StatusText != null && noticeUntil == 0f) StatusText.text = "";
    }

    public void BeginItems()
    {
        nextCount = 0;
    }

    public void AddItem(string key, int kind, string title, string sharer, string body, Texture thumb, float aspect)
    {
        if (nextCount >= Capacity || string.IsNullOrEmpty(key)) return;
        nextKey[nextCount] = key;
        nextKind[nextCount] = kind;
        nextTitle[nextCount] = Clean(title);
        nextSharer[nextCount] = Clean(sharer);
        nextBody[nextCount] = Clean(body);
        nextThumb[nextCount] = thumb;
        nextAspect[nextCount] = aspect > 0.01f ? aspect : 1f;
        nextCount++;
    }

    public void EndItems()
    {
        bool changed = nextCount != itemCount;
        for (int i = 0; i < nextCount && !changed; i++)
        {
            if (nextKey[i] != itemKey[i] || nextTitle[i] != itemTitle[i] || nextThumb[i] != itemThumb[i] || nextBody[i] != itemBody[i]) changed = true;
        }
        string[] k = itemKey; itemKey = nextKey; nextKey = k;
        int[] kd = itemKind; itemKind = nextKind; nextKind = kd;
        string[] t = itemTitle; itemTitle = nextTitle; nextTitle = t;
        string[] s = itemSharer; itemSharer = nextSharer; nextSharer = s;
        string[] b = itemBody; itemBody = nextBody; nextBody = b;
        Texture[] th = itemThumb; itemThumb = nextThumb; nextThumb = th;
        float[] a = itemAspect; itemAspect = nextAspect; nextAspect = a;
        itemCount = nextCount;

        if (IsAuthority() && UpdateOrder()) SendOrder(null);
        if (changed) Render();
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
        noticeUntil = Time.time + 2.5f;
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
        Vector3 position;
        Quaternion rotation;
        BringPose(out position, out rotation);
        // A photo moves on the client that controls it, so everyone hears where to put it.
        if (action == ActionBring) SendBring(itemKey[item], position, rotation);
        Queue(action, itemKey[item], position, rotation, 1);
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
        return 0;
    }

    private string ActionLabel(int action)
    {
        if (action == ActionBring) return T("手元に", "Bring");
        if (action == ActionCopy) return T("コピー", "Copy");
        if (action == ActionSave) return T("保存", "Save");
        if (action == ActionOpen) return T("開く", "Open");
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
        BuildDisplay();
        int rows = Rows.Length;
        int pages = displayCount == 0 ? 1 : (displayCount + rows - 1) / rows;
        if (page >= pages) page = pages - 1;
        if (page < 0) page = 0;

        CountText.text = displayCount == 0 ? T("まだ何も共有されていません", "Nothing shared yet") : (japanese == 1 ? displayCount + " 件" : displayCount + (displayCount == 1 ? " item" : " items"));
        PageText.text = pages > 1 ? (page + 1) + " / " + pages : "";
        PrevPageButton.gameObject.SetActive(pages > 1);
        NextPageButton.gameObject.SetActive(pages > 1);

        for (int r = 0; r < rows; r++)
        {
            SharePanelRow row = Rows[r];
            int at = page * rows + r;
            if (at >= displayCount)
            {
                row.Hide();
                continue;
            }
            int item = display[at];
            int kind = itemKind[item];
            string meta = itemSharer[item].Length > 0 ? KindLabel(kind) + "・" + itemSharer[item] : KindLabel(kind);
            row.Show(KindLabel(kind), KindColor(kind), RowTitle(item), meta, itemThumb[item], itemAspect[item],
                ActionLabel(ActionFor(kind, 1)), ActionLabel(ActionFor(kind, 2)), ActionLabel(ActionFor(kind, 3)),
                itemKey[item] == selectedKey);
        }
        RenderPreview();
        PreviewPrevButton.gameObject.SetActive(previewPageCount > 1);
        PreviewNextButton.gameObject.SetActive(previewPageCount > 1);
    }

    private string RowTitle(int item)
    {
        string title = itemTitle[item];
        if (title.Length == 0) title = itemBody[item];
        if (title.Length == 0) title = KindLabel(itemKind[item]);
        title = title.Replace('\n', ' ');
        if (title.Length > 60) title = title.Substring(0, 60) + "…";
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
        PreviewMeta.text = itemSharer[item].Length > 0 ? KindLabel(kind) + "・" + itemSharer[item] : KindLabel(kind);

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
            PreviewBody.text = showImage ? "" : (kind == KindPhoto ? T("読み込み中…", "Loading…") : Verbatim(itemBody[item]));
            PreviewPageText.text = "";
        }
    }

    // Splits text into pages that fit the preview box, by an estimate of wrapped lines
    // (full-width characters count as one unit, narrower ones as half).
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
    // order does not have yet.
    private void BuildDisplay()
    {
        displayCount = 0;
        int[] used = new int[itemCount];
        for (int o = 0; o < order.Count; o++)
        {
            int item = Find(order[o]);
            if (item < 0 || used[item] != 0) continue;
            used[item] = 1;
            display[displayCount++] = item;
        }
        for (int i = 0; i < itemCount; i++)
        {
            if (used[i] != 0) continue;
            display[displayCount++] = i;
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

    private void SendOrder(ushort[] to)
    {
        if (net == null || !net.HasNetworkID) return;
        int count = order.Count;
        int size = 3;
        for (int i = 0; i < count; i++) size += 1 + order[i].Length;
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
        }
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, to);
    }

    private void SendView()
    {
        SendViewTo(null);
    }

    private void SendViewTo(ushort[] to)
    {
        if (net == null || !net.HasNetworkID) return;
        string key = selectedKey == null ? "" : selectedKey;
        byte[] buffer = new byte[6 + key.Length];
        buffer[0] = MsgView;
        buffer[1] = (byte)(page & 0xFF);
        buffer[2] = (byte)((page >> 8) & 0xFF);
        buffer[3] = (byte)(previewPage & 0xFF);
        buffer[4] = (byte)((previewPage >> 8) & 0xFF);
        buffer[5] = (byte)key.Length;
        for (int c = 0; c < key.Length; c++) buffer[6 + c] = (byte)key[c];
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
                if (at + length > buffer.Length) break;
                next.Add(Encoding.ASCII.GetString(buffer, at, length));
                at += length;
            }
            order = next;
            Render();
        }
        else if (buffer[0] == MsgView && buffer.Length >= 6)
        {
            page = buffer[1] | (buffer[2] << 8);
            previewPage = buffer[3] | (buffer[4] << 8);
            int length = buffer[5];
            selectedKey = 6 + length <= buffer.Length ? Encoding.ASCII.GetString(buffer, 6, length) : "";
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
    }
}
