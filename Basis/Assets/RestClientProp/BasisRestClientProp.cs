using System.Net.Sockets;
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
/// UDP mode sends the payload as one datagram to the host:port in the URL field and shows
/// the first datagram that comes back. UdpClient falls under the same Internet permission.
///
/// Requests and replies are polled from Update() rather than awaited or called back, which
/// keeps the interpreted code to plain calls and field reads.
/// </summary>
[Cilboxable]
public class BasisRestClientProp : MonoBehaviour
{
    private const int MaxBodyCharacters = 1200;
    private const int TimeoutSeconds = 15;
    private const int UdpBufferSize = 65536;

    [Header("Lock")]
    public GameObject LockedOverlay;

    [Header("Mode")]
    public Button HttpModeButton;
    public Button UdpModeButton;
    public Color ModeOnColor = new Color(0.18f, 0.36f, 0.62f, 1f);
    public Color ModeOffColor = new Color(0.22f, 0.25f, 0.32f, 1f);

    [Header("Endpoints")]
    public string EndpointA = "https://httpbin.org/anything";
    public string EndpointB = "https://jsonplaceholder.typicode.com/posts/1";
    public string EndpointC = "https://httpbin.org/status/418";
    public Button EndpointAButton;
    public Button EndpointBButton;
    public Button EndpointCButton;

    [Header("Methods")]
    public GameObject HttpMethods;
    public Button GetButton;
    public Button PostButton;
    public Button PutButton;
    public Button DeleteButton;
    public Button UdpSendButton;

    [Header("Input")]
    public TMP_InputField UrlInput;
    public TMP_InputField PayloadInput;

    [Header("Output")]
    public TextMeshProUGUI StatusText;
    public TextMeshProUGUI BodyText;

    private bool udpMode;

    private UnityWebRequest request;
    private string requestMethod;
    private float requestStartedAt;

    private UdpClient udp;
    private string udpTarget;
    private float udpSentAt;

    private void Start()
    {
        // Needs the Internet permission. Denied, this throws and nothing below runs.
        UnityWebRequest probe = new UnityWebRequest();
        probe.Dispose();

        if (LockedOverlay != null) LockedOverlay.SetActive(false);

        HttpModeButton.onClick.AddListener(SetHttpMode);
        UdpModeButton.onClick.AddListener(SetUdpMode);
        EndpointAButton.onClick.AddListener(SelectA);
        EndpointBButton.onClick.AddListener(SelectB);
        EndpointCButton.onClick.AddListener(SelectC);
        GetButton.onClick.AddListener(SendGet);
        PostButton.onClick.AddListener(SendPost);
        PutButton.onClick.AddListener(SendPut);
        DeleteButton.onClick.AddListener(SendDelete);
        UdpSendButton.onClick.AddListener(SendUdp);

        SetHttpMode();
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
        CloseUdp();
    }

    private void SetHttpMode() { SetMode(false); }
    private void SetUdpMode() { SetMode(true); }

    private void SetMode(bool udpOn)
    {
        udpMode = udpOn;
        HttpMethods.SetActive(!udpOn);
        UdpSendButton.gameObject.SetActive(udpOn);
        HttpModeButton.image.color = udpOn ? ModeOffColor : ModeOnColor;
        UdpModeButton.image.color = udpOn ? ModeOnColor : ModeOffColor;
        if (udpOn) SetStatus("UDP: type host:port, then SEND.", Color.white);
        else SetStatus("HTTP: type a URL or pick a preset, then a method.", Color.white);
    }

    private void SelectA() { Select(EndpointA); }
    private void SelectB() { Select(EndpointB); }
    private void SelectC() { Select(EndpointC); }

    private void Select(string url)
    {
        if (udpMode) SetMode(false);
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
            string body = PayloadInput.text;
            if (body.Length == 0)
            {
                body = "{\"from\":\"Basis REST client prop\",\"method\":\"" + method + "\",\"time\":" + Time.time + "}";
            }
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
            request.SetRequestHeader("Content-Type", "application/json");
        }

        requestMethod = method;
        requestStartedAt = Time.realtimeSinceStartup;
        request.SendWebRequest();

        SetStatus(method + " ...", Color.yellow);
        BodyText.text = "";
    }

    private void SendUdp()
    {
        string target = UrlInput.text.Trim();
        if (target.StartsWith("udp://")) target = target.Substring(6);
        int colon = target.LastIndexOf(':');
        if (colon <= 0 || colon == target.Length - 1)
        {
            SetStatus("UDP needs host:port, e.g. example.com:7", Color.yellow);
            return;
        }
        string host = target.Substring(0, colon);
        if (host.StartsWith("[") && host.EndsWith("]")) host = host.Substring(1, host.Length - 2);

        CloseUdp();
        try
        {
            int port = int.Parse(target.Substring(colon + 1));
            string payload = PayloadInput.text;
            if (payload.Length == 0) payload = "Basis REST client prop " + Time.time;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload);

            udp = new UdpClient(host, port);
            udp.Send(bytes, bytes.Length);
            udpTarget = host + ":" + port;
            udpSentAt = Time.realtimeSinceStartup;
            SetStatus("UDP " + udpTarget + "  sent " + bytes.Length + " bytes, waiting...", Color.yellow);
        }
        catch (System.Exception e)
        {
            CloseUdp();
            SetStatus("UDP failed: " + e.Message, new Color(1f, 0.4f, 0.4f));
        }
        BodyText.text = "";
    }

    private void Update()
    {
        PollHttp();
        PollUdp();
    }

    private void PollHttp()
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
        ShowBody(body);

        request.Dispose();
        request = null;
    }

    private void PollUdp()
    {
        if (udp == null) return;

        int elapsedMs = (int)((Time.realtimeSinceStartup - udpSentAt) * 1000f);
        try
        {
            if (udp.Available > 0)
            {
                byte[] buffer = new byte[UdpBufferSize];
                int length = udp.Client.Receive(buffer);
                SetStatus("UDP " + udpTarget + "  reply " + length + " bytes  (" + elapsedMs + " ms)", new Color(0.35f, 0.9f, 0.45f));
                ShowBody(System.Text.Encoding.UTF8.GetString(buffer, 0, length));
                CloseUdp();
                return;
            }
        }
        catch (System.Exception e)
        {
            // An ICMP "port unreachable" for the datagram surfaces here as a reset.
            SetStatus("UDP " + udpTarget + "  " + e.Message, new Color(1f, 0.4f, 0.4f));
            CloseUdp();
            return;
        }

        if (elapsedMs > TimeoutSeconds * 1000)
        {
            SetStatus("UDP " + udpTarget + "  no reply in " + TimeoutSeconds + " s", new Color(1f, 0.4f, 0.4f));
            CloseUdp();
        }
    }

    private void CloseUdp()
    {
        if (udp == null) return;
        udp.Close();
        udp = null;
    }

    private void ShowBody(string body)
    {
        if (body == null) body = "";
        if (body.Length > MaxBodyCharacters) body = body.Substring(0, MaxBodyCharacters) + "\n...";
        BodyText.text = body;
    }

    private void SetStatus(string text, Color color)
    {
        StatusText.text = text;
        StatusText.color = color;
    }
}
