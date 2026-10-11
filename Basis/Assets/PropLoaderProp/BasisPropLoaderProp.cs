using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Cilbox;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SerializableBasis;

/// <summary>
/// A panel that spawns another prop from a BEE file URL and its password, networked, the same
/// way the content menu does (<see cref="BasisNetworkSpawnItem.RequestGameObjectLoad"/>).
///
/// The spawn API is not on the prop whitelist, so spawning this prop raises the
/// script-permission prompt. The panel starts behind a lock overlay and only Start() takes it
/// down, after touching a gated type once: if the player denied the prompt that first touch
/// fails, Start() stops there, and the lock stays up.
///
/// Only method calls on Basis types, never field reads: Cilbox resolves a field of a type
/// outside the whitelist when the bundle is built, and fails the build when that type is
/// refused, while a refused method only logs. So the request's NetID (a field) is read back
/// by serializing the request, whose second entry is that NetID.
///
/// A load request is fire-and-forget, so Update() polls the spawn registry for that NetID to
/// report when the prop has actually appeared.
/// </summary>
[Cilboxable]
public class BasisPropLoaderProp : MonoBehaviour
{
    private const float PollInterval = 0.5f;
    private const float GiveUpSeconds = 60f;

    [Header("Lock")]
    public GameObject LockedOverlay;

    [Header("Input")]
    public TMP_InputField UrlInput;
    public TMP_InputField PasswordInput;

    [Header("Buttons")]
    public Button LoadButton;
    public Button UnloadButton;

    [Header("Output")]
    public TextMeshProUGUI StatusText;

    [Header("Placement")]
    public float SpawnDistance = 1f;

    private string lastNetId;
    private bool waiting;
    private float requestedAt;
    private float nextPollAt;

    private void Start()
    {
        // Needs the spawn permission. Denied, this throws and nothing below runs.
        // (BasisNetworkConnection is whitelisted, so it cannot serve as this probe.)
        new NetDataWriter();
        bool connected = BasisNetworkConnection.LocalPlayerIsConnected;

        if (LockedOverlay != null) LockedOverlay.SetActive(false);

        LoadButton.onClick.AddListener(Load);
        UnloadButton.onClick.AddListener(Unload);

        SetStatus(connected ? "Ready. Enter a BEE URL and its password, then LOAD." : "Not connected to a server yet.", Color.white);
    }

    private void Load()
    {
        string url = UrlInput.text.Trim();
        string password = PasswordInput.text.Trim();
        if (url.Length == 0 || password.Length == 0)
        {
            SetStatus("Enter both the URL and the password.", Color.yellow);
            return;
        }
        if (!BasisNetworkConnection.LocalPlayerIsConnected)
        {
            SetStatus("Not connected to a server.", new Color(1f, 0.4f, 0.4f));
            return;
        }

        Vector3 position = transform.position + transform.forward * SpawnDistance;
        LocalLoadResource resource;
        bool sent = BasisNetworkSpawnItem.RequestGameObjectLoad(password, url, position, transform.rotation, Vector3.one, false, false, false, out resource, 0);
        if (!sent)
        {
            SetStatus("The load request was rejected.", new Color(1f, 0.4f, 0.4f));
            return;
        }

        lastNetId = NetIdOf(resource);
        waiting = true;
        requestedAt = Time.realtimeSinceStartup;
        nextPollAt = requestedAt + PollInterval;
        SetStatus("Requested... (" + lastNetId + ")", Color.yellow);
    }

    // LocalLoadResource.Serialize writes Mode (a byte) and then LoadedNetID (a string).
    private static string NetIdOf(LocalLoadResource resource)
    {
        NetDataWriter writer = new NetDataWriter();
        resource.Serialize(writer);
        NetDataReader reader = new NetDataReader(writer.CopyData());
        reader.GetByte();
        return reader.GetString();
    }

    private void Unload()
    {
        if (string.IsNullOrEmpty(lastNetId))
        {
            SetStatus("Nothing loaded from here yet.", Color.yellow);
            return;
        }
        BasisNetworkSpawnItem.RequestGameObjectUnLoad(lastNetId);
        SetStatus("Unload requested (" + lastNetId + ")", Color.white);
        lastNetId = null;
        waiting = false;
    }

    private void Update()
    {
        if (!waiting || Time.realtimeSinceStartup < nextPollAt) return;
        nextPollAt = Time.realtimeSinceStartup + PollInterval;

        BasisRuntimeSpawnRegistry.SpawnInstance instance;
        if (BasisRuntimeSpawnRegistry.TryGetByLoadedNetId(lastNetId, out instance))
        {
            waiting = false;
            int seconds = (int)(Time.realtimeSinceStartup - requestedAt);
            SetStatus("Loaded (" + seconds + " s)", new Color(0.35f, 0.9f, 0.45f));
            return;
        }

        if (Time.realtimeSinceStartup - requestedAt > GiveUpSeconds)
        {
            waiting = false;
            SetStatus("Still not spawned after " + (int)GiveUpSeconds + " s. Check the URL, password and logs.", new Color(1f, 0.4f, 0.4f));
        }
    }

    private void SetStatus(string text, Color color)
    {
        StatusText.text = text;
        StatusText.color = color;
    }
}
