using Cilbox;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// A small REST client on a panel, for trying Cilbox's per-prop script permissions.
///
/// UnityWebRequest is not on the prop whitelist, so spawning this prop raises the
/// script-permission prompt ("Internet"). The panel starts behind a lock overlay and only
/// Start() takes it down, after touching UnityWebRequest once: if the player denied the
/// prompt that first touch fails, Start() stops there, and the lock stays up.
///
/// Requests are polled from Update() rather than awaited or called back, which keeps the
/// interpreted code to plain calls and field reads.
/// </summary>
[Cilboxable]
public class BasisRestClientProp : MonoBehaviour
{
    private const int MaxBodyCharacters = 1200;
    private const int TimeoutSeconds = 15;

    [Header("Lock")]
    public GameObject LockedOverlay;

    [Header("Endpoints")]
    public string EndpointA = "https://httpbin.org/anything";
    public string EndpointB = "https://jsonplaceholder.typicode.com/posts/1";
    public string EndpointC = "https://httpbin.org/status/418";
    public Button EndpointAButton;
    public Button EndpointBButton;
    public Button EndpointCButton;

    [Header("Methods")]
    public Button GetButton;
    public Button PostButton;
    public Button PutButton;
    public Button DeleteButton;

    [Header("Url")]
    public TMP_InputField UrlInput;

    [Header("Output")]
    public TextMeshProUGUI StatusText;
    public TextMeshProUGUI BodyText;

    private UnityWebRequest request;
    private string requestMethod;
    private float requestStartedAt;

    private void Start()
    {
        // Needs the Internet permission. Denied, this throws and nothing below runs.
        UnityWebRequest probe = new UnityWebRequest();
        probe.Dispose();

        if (LockedOverlay != null) LockedOverlay.SetActive(false);

        EndpointAButton.onClick.AddListener(SelectA);
        EndpointBButton.onClick.AddListener(SelectB);
        EndpointCButton.onClick.AddListener(SelectC);
        GetButton.onClick.AddListener(SendGet);
        PostButton.onClick.AddListener(SendPost);
        PutButton.onClick.AddListener(SendPut);
        DeleteButton.onClick.AddListener(SendDelete);

        SelectA();
        SetStatus("Ready. Type a URL or pick a preset, then a method.", Color.white);
        BodyText.text = "";
    }

    private void OnDestroy()
    {
        if (request != null)
        {
            request.Abort();
            request.Dispose();
            request = null;
        }
    }

    private void SelectA() { Select(EndpointA); }
    private void SelectB() { Select(EndpointB); }
    private void SelectC() { Select(EndpointC); }

    private void Select(string url)
    {
        UrlInput.text = url;
    }

    private void SendGet() { Send("GET"); }
    private void SendPost() { Send("POST"); }
    private void SendPut() { Send("PUT"); }
    private void SendDelete() { Send("DELETE"); }

    private void Send(string method)
    {
        if (request != null)
        {
            SetStatus("Still waiting on the previous " + requestMethod + "...", Color.yellow);
            return;
        }
        string url = UrlInput.text.Trim();
        if (url.Length == 0)
        {
            SetStatus("Enter a URL first.", Color.yellow);
            return;
        }

        request = new UnityWebRequest(url, method);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.timeout = TimeoutSeconds;
        if (method == "POST" || method == "PUT")
        {
            string json = "{\"from\":\"Basis REST client prop\",\"method\":\"" + method + "\",\"time\":" + Time.time + "}";
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            request.SetRequestHeader("Content-Type", "application/json");
        }

        requestMethod = method;
        requestStartedAt = Time.realtimeSinceStartup;
        request.SendWebRequest();

        SetStatus(method + " ...", Color.yellow);
        BodyText.text = "";
    }

    private void Update()
    {
        if (request == null || !request.isDone) return;

        int elapsedMs = (int)((Time.realtimeSinceStartup - requestStartedAt) * 1000f);
        long code = request.responseCode;
        string summary = requestMethod + "  " + code + "  (" + elapsedMs + " ms)";

        if (request.result == UnityWebRequest.Result.Success)
        {
            SetStatus(summary, new Color(0.35f, 0.9f, 0.45f));
        }
        else
        {
            SetStatus(summary + "  " + request.error, new Color(1f, 0.4f, 0.4f));
        }

        string body = request.downloadHandler != null ? request.downloadHandler.text : "";
        if (body == null) body = "";
        if (body.Length > MaxBodyCharacters) body = body.Substring(0, MaxBodyCharacters) + "\n...";
        BodyText.text = body;

        request.Dispose();
        request = null;
    }

    private void SetStatus(string text, Color color)
    {
        StatusText.text = text;
        StatusText.color = color;
    }
}
