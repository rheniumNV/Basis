using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// The fishing world: the button on the rod stand, the pool of rods it hands out, the fish
/// table and the catch board.
///
/// The rods are placed in the scene inactive (a networked object's id comes from its hierarchy
/// path, so a runtime copy could not be synced). Pressing the button brings out the next one for
/// everybody; a player who joins later is told by the owner of this object which are out.
/// </summary>
[Cilboxable]
public class FishingWorldController : MonoBehaviour
{
    private const byte MsgActivate = 1;
    private const byte MsgSnapshot = 2;
    private const int RecentCount = 5;

    [Header("Rods")]
    public BasisInteractableButton SpawnButton;
    public FishingRod[] Rods;
    public Transform RodSpawnPoint;
    public float RodSpacing = 0.2f;
    public ParticleSystem SpawnPuff;
    public AudioSource ButtonAudio;
    public AudioClip ButtonClip;

    [Header("Fish")]
    public string[] SpeciesNames;
    public float[] SpeciesWeights;
    public float[] SpeciesMinCm;
    public float[] SpeciesMaxCm;
    // 1 = rare (sparkles, starred on the board). int, not bool: Cilbox cannot read bool[] elements.
    public int[] SpeciesRare;

    [Header("Board")]
    public TextMeshPro RecentText;
    public TextMeshPro CountsText;
    public TextMeshPro StandText;

    private BasisNetworkShim net;
    private int activeMask;
    private int[] counts;
    private string[] recent;
    private float standMessageUntil;

    private void Start()
    {
        counts = new int[SpeciesNames.Length];
        recent = new string[RecentCount];
        for (int i = 0; i < RecentCount; i++) recent[i] = "";

        SpawnButton.ButtonDown = OnButtonDown;

        net = SafeUtil.MakeNetworkable(this);
        if (net != null)
        {
            net.NetworkMessageReceived += OnNetworkMessage;
            net.PlayerJoined += OnPlayerJoined;
        }
        RefreshBoard();
        SetStandText("ボタンを押すと釣り竿が出るよ");
    }

    private void Update()
    {
        if (standMessageUntil > 0f && Time.time > standMessageUntil)
        {
            standMessageUntil = 0f;
            SetStandText("ボタンを押すと釣り竿が出るよ");
        }
    }

    /// <summary>The button on the stand. Public so it can be pressed from the editor.</summary>
    public void OnButtonDown()
    {
        if (ButtonAudio != null && ButtonClip != null) ButtonAudio.PlayOneShot(ButtonClip, 0.8f);

        int index = -1;
        for (int i = 0; i < Rods.Length; i++)
        {
            if ((activeMask & (1 << i)) == 0)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            SetStandText("釣り竿はもう全部出ているよ");
            standMessageUntil = Time.time + 3f;
            return;
        }

        ActivateRod(index, true);
        if (net != null && net.HasNetworkID)
        {
            net.SendCustomNetworkEvent(new byte[] { MsgActivate, (byte)index }, DeliveryMethod.ReliableOrdered, null);
        }
    }

    private void ActivateRod(int index, bool placeOnStand)
    {
        if (index < 0 || index >= Rods.Length) return;
        if ((activeMask & (1 << index)) != 0) return;
        activeMask |= 1 << index;

        FishingRod rod = Rods[index];
        if (placeOnStand)
        {
            Vector3 position = RodSpawnPoint.position + RodSpawnPoint.right * (RodSpacing * index);
            rod.transform.SetPositionAndRotation(position, RodSpawnPoint.rotation);
            if (SpawnPuff != null)
            {
                SpawnPuff.transform.position = position;
                SpawnPuff.Play();
            }
        }
        rod.gameObject.SetActive(true);
    }

    // ---- Fish table ----

    public int RollSpecies()
    {
        float total = 0f;
        for (int i = 0; i < SpeciesWeights.Length; i++) total += SpeciesWeights[i];
        float roll = Random.Range(0f, total);
        for (int i = 0; i < SpeciesWeights.Length; i++)
        {
            roll -= SpeciesWeights[i];
            if (roll <= 0f) return i;
        }
        return 0;
    }

    public float RollSizeCm(int species)
    {
        // Squared so that big ones are rarer.
        float t = Random.Range(0f, 1f);
        return Mathf.Lerp(SpeciesMinCm[species], SpeciesMaxCm[species], t * t);
    }

    public bool IsRare(int species)
    {
        return species >= 0 && species < SpeciesRare.Length && SpeciesRare[species] != 0;
    }

    /// <summary>Called by a rod on every client when a fish is landed.</summary>
    public void ReportCatch(int species, float cm, ushort sender, bool local)
    {
        if (species < 0 || species >= counts.Length) return;
        counts[species]++;

        string who = local ? LocalName() : RemoteName(sender);
        for (int i = RecentCount - 1; i > 0; i--) recent[i] = recent[i - 1];
        string mark = IsRare(species) ? "<color=#E0A100>★</color> " : "";
        recent[0] = mark + "<b>" + SpeciesNames[species] + "</b> " + Mathf.RoundToInt(cm) + "cm  <color=#7A8A80>" + who + "</color>";
        RefreshBoard();

        SetStandText(SpeciesNames[species] + " " + Mathf.RoundToInt(cm) + "cm!");
        standMessageUntil = Time.time + 4f;
    }

    private string LocalName()
    {
        IBasisPlayer player = BasisPlayersShim.Local;
        if (player == null) return "あなた";
        return player.DisplayName;
    }

    private string RemoteName(ushort id)
    {
        IBasisPlayer player = BasisPlayersShim.GetById(id);
        if (player == null) return "だれか";
        return player.DisplayName;
    }

    private void RefreshBoard()
    {
        if (RecentText != null)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("<b>さいきんの釣果</b>\n");
            bool any = false;
            for (int i = 0; i < RecentCount; i++)
            {
                if (recent[i].Length == 0) continue;
                builder.Append(recent[i]);
                builder.Append("\n");
                any = true;
            }
            if (!any) builder.Append("<color=#7A8A80>まだ誰も釣っていないよ</color>");
            RecentText.text = builder.ToString();
        }
        if (CountsText != null)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("<b>図鑑</b>\n");
            for (int i = 0; i < SpeciesNames.Length; i++)
            {
                builder.Append(counts[i] > 0 ? SpeciesNames[i] : "？？？");
                builder.Append("  ×");
                builder.Append(counts[i]);
                builder.Append("\n");
            }
            CountsText.text = builder.ToString();
        }
    }

    private void SetStandText(string text)
    {
        if (StandText != null) StandText.text = text;
    }

    // ---- Networking ----

    private void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || net == null || !net.IsOwnedLocallyOnServer) return;

        // [MsgSnapshot, mask, count per species (2 bytes each), then the recent lines as UTF-8]
        string joined = string.Join("\n", recent);
        byte[] text = Encoding.UTF8.GetBytes(joined);
        int header = 2 + counts.Length * 2;
        byte[] buffer = new byte[header + text.Length];
        buffer[0] = MsgSnapshot;
        buffer[1] = (byte)activeMask;
        for (int i = 0; i < counts.Length; i++)
        {
            buffer[2 + i * 2] = (byte)(counts[i] & 0xFF);
            buffer[3 + i * 2] = (byte)((counts[i] >> 8) & 0xFF);
        }
        for (int i = 0; i < text.Length; i++) buffer[header + i] = text[i];
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });
    }

    private void OnNetworkMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 2) return;

        if (buffer[0] == MsgActivate)
        {
            ActivateRod(buffer[1], true);
        }
        else if (buffer[0] == MsgSnapshot)
        {
            int mask = buffer[1];
            for (int i = 0; i < Rods.Length; i++)
            {
                // Rods already out keep the pose their pickup sync gives them.
                if ((mask & (1 << i)) != 0) ActivateRod(i, false);
            }
            int header = 2 + counts.Length * 2;
            if (buffer.Length < header) return;
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = buffer[2 + i * 2] | (buffer[3 + i * 2] << 8);
            }
            string joined = Encoding.UTF8.GetString(buffer, header, buffer.Length - header);
            string[] lines = joined.Split('\n');
            for (int i = 0; i < RecentCount; i++) recent[i] = i < lines.Length ? lines[i] : "";
            RefreshBoard();
        }
    }
}
