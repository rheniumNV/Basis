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
/// table, the catch board (recent catches and the level ranking) and the local player's own
/// encyclopedia board.
///
/// The rods are placed in the scene inactive (a networked object's id comes from its hierarchy
/// path, so a runtime copy could not be synced). Pressing the button brings out the next one for
/// everybody; a player who joins later is told by the owner of this object which are out.
///
/// Persistence goes through FishingBackend, which pulls the catches to send from here and pushes
/// the ranking and the encyclopedia back. This script never calls into it: the backend uses
/// permission-gated APIs, and if a player refuses them it is switched off, which must not take
/// the fishing down with it.
/// </summary>
[Cilboxable]
public class FishingWorldController : MonoBehaviour
{
    private const byte MsgActivate = 1;
    private const byte MsgSnapshot = 2;
    private const int RecentCount = 5;
    private const int PendingCapacity = 32;

    [Header("Rods")]
    public BasisInteractableButton SpawnButton;
    public FishingRod[] Rods;
    public Transform RodSpawnPoint;
    public float RodSpacing = 0.2f;
    // Places on the table a rod comes out at; a place with a rod still lying in it is skipped.
    public int RodSlots = 6;
    public int RodLayerMask = 1 << 8;
    public ParticleSystem SpawnPuff;
    public AudioSource ButtonAudio;
    public AudioClip ButtonClip;

    [Header("Fish (from Data/fish.json)")]
    // Japanese names: also the names the record server keeps the catches under.
    public string[] SpeciesNames;
    public string[] SpeciesNamesEn;
    public float[] SpeciesWeights;
    public float[] SpeciesMinCm;
    public float[] SpeciesMaxCm;
    // 1 = rare (sparkles, starred on the board). int, not bool: Cilbox cannot read bool[] elements.
    public int[] SpeciesRare;
    public int[] SpeciesBaseExp;
    public int[] SpeciesBaseMoney;
    // Reward multiplier at the largest size: 1 + RewardSizeBonus (linear in size between).
    public float RewardSizeBonus = 1f;
    // Crowns for a size among the largest (max) or smallest (min) this share of possible catches.
    public float CrownGoldPercent = 5f;
    public float CrownSilverPercent = 10f;

    [Header("Catch board")]
    public TextMeshPro RecentText;
    public TextMeshPro RankingText;
    public TextMeshPro StandText;

    [Header("Language (texts from Data/strings.json)")]
    // Each player picks their own language on the sign; it starts as Japanese when the OS is
    // Japanese and as English otherwise.
    public string[] TextKeys;
    public string[] TextJa;
    public string[] TextEn;
    // Fixed labels in the world and the key of the text each shows.
    public TextMeshPro[] LabelTexts;
    public string[] LabelKeys;
    public BasisInteractableButton LanguageJaButton;
    public BasisInteractableButton LanguageEnButton;
    public TextMeshPro LanguageCurrent;
    // The permission warning's text (FishingBackend hides the sign itself once allowed).
    public TextMeshPro WarningText;

    [Header("Reload buttons on the boards")]
    public BasisInteractableButton RankingReloadButton;
    public BasisInteractableButton ZukanReloadButton;

    [Header("Encyclopedia board (one row per species)")]
    public TextMeshPro ZukanStatus;
    public TextMeshPro[] ZukanName;
    public TextMeshPro[] ZukanCount;
    public TextMeshPro[] ZukanMax;
    public TextMeshPro[] ZukanMin;
    public GameObject[] ZukanMaxGold;
    public GameObject[] ZukanMaxSilver;
    public GameObject[] ZukanMinGold;
    public GameObject[] ZukanMinSilver;

    private BasisNetworkShim net;
    private int activeMask;
    // 0 = Japanese, 1 = English.
    private int language;
    // Recent catches, newest first, kept as data so each player shows them in their own language.
    private int[] recentSpecies;
    private float[] recentCm;
    private string[] recentWho;
    private int[] recentMine;
    private float standMessageUntil;
    // Reloads asked for on the boards, for FishingBackend to pick up: 1 = ranking, 2 = encyclopedia.
    private int reloadRequests;

    // The local player's encyclopedia: from the server once loaded, plus this visit's catches.
    private int[] myCount;
    private float[] myMin;
    private float[] myMax;

    // Catches by the local player waiting to be sent, oldest first.
    private int[] pendingSpecies;
    private float[] pendingCm;
    private int pendingStart;
    private int pendingLength;

    private void Start()
    {
        recentSpecies = new int[RecentCount];
        recentCm = new float[RecentCount];
        recentWho = new string[RecentCount];
        recentMine = new int[RecentCount];
        for (int i = 0; i < RecentCount; i++)
        {
            recentSpecies[i] = -1;
            recentWho[i] = "";
        }
        int n = SpeciesNames.Length;
        myCount = new int[n];
        myMin = new float[n];
        myMax = new float[n];
        pendingSpecies = new int[PendingCapacity];
        pendingCm = new float[PendingCapacity];

        SpawnButton.ButtonDown = OnButtonDown;
        if (RankingReloadButton != null) RankingReloadButton.ButtonDown = OnRankingReload;
        if (ZukanReloadButton != null) ZukanReloadButton.ButtonDown = OnZukanReload;
        if (LanguageJaButton != null) LanguageJaButton.ButtonDown = OnLanguageJa;
        if (LanguageEnButton != null) LanguageEnButton.ButtonDown = OnLanguageEn;
        language = Application.systemLanguage == SystemLanguage.Japanese ? 0 : 1;

        net = SafeUtil.MakeNetworkable(this);
        if (net != null)
        {
            net.NetworkMessageReceived += OnNetworkMessage;
            net.PlayerJoined += OnPlayerJoined;
        }
        // Status lines stay empty unless the server reports an error.
        SetZukanStatus("");
        ApplyLanguage();
    }

    private void Update()
    {
        if (standMessageUntil > 0f && Time.time > standMessageUntil)
        {
            standMessageUntil = 0f;
            SetStandText(Text("stand.hint"));
        }
    }

    // ---- Language ----

    /// <summary>The text for a key of Data/strings.json in the current language.</summary>
    public string Text(string key)
    {
        for (int i = 0; i < TextKeys.Length; i++)
        {
            if (TextKeys[i] == key) return language == 1 ? TextEn[i] : TextJa[i];
        }
        return key;
    }

    public string SpeciesName(int species)
    {
        if (language == 1 && species < SpeciesNamesEn.Length && SpeciesNamesEn[species].Length > 0) return SpeciesNamesEn[species];
        return SpeciesNames[species];
    }

    public string RankingHeader()
    {
        return "<b>" + Text("ranking.title") + "</b>\n";
    }

    public void OnLanguageJa()
    {
        SetLanguage(0);
    }

    public void OnLanguageEn()
    {
        SetLanguage(1);
    }

    private void SetLanguage(int value)
    {
        if (ButtonAudio != null && ButtonClip != null) ButtonAudio.PlayOneShot(ButtonClip, 0.5f);
        if (language == value) return;
        language = value;
        ApplyLanguage();
    }

    // Redraws everything that shows text in the current language.
    private void ApplyLanguage()
    {
        for (int i = 0; i < LabelTexts.Length; i++)
        {
            if (LabelTexts[i] != null) LabelTexts[i].text = Text(LabelKeys[i]);
        }
        if (WarningText != null) WarningText.text = Text("warning");
        if (LanguageCurrent != null) LanguageCurrent.text = language == 1 ? "English" : "日本語";
        standMessageUntil = 0f;
        SetStandText(Text("stand.hint"));
        RefreshRecent();
        RefreshZukan();
        // The ranking is drawn by FishingBackend from the server's answer: fetch it again.
        SetRankingText(RankingHeader());
        reloadRequests |= 1;
    }

    /// <summary>The ranking board's reload button.</summary>
    public void OnRankingReload()
    {
        if (ButtonAudio != null && ButtonClip != null) ButtonAudio.PlayOneShot(ButtonClip, 0.5f);
        reloadRequests |= 1;
    }

    /// <summary>The encyclopedia board's reload button.</summary>
    public void OnZukanReload()
    {
        if (ButtonAudio != null && ButtonClip != null) ButtonAudio.PlayOneShot(ButtonClip, 0.5f);
        reloadRequests |= 2;
    }

    /// <summary>FishingBackend takes the reloads asked for since it last looked.</summary>
    public int TakeReloadRequests()
    {
        int requests = reloadRequests;
        reloadRequests = 0;
        return requests;
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
            SetStandText(Text("stand.allOut"));
            standMessageUntil = Time.time + 3f;
            return;
        }
        int slot = FreeSlot();
        if (slot < 0)
        {
            SetStandText(Text("stand.tableFull"));
            standMessageUntil = Time.time + 3f;
            return;
        }

        ActivateRod(index, slot);
        if (net != null && net.HasNetworkID)
        {
            net.SendCustomNetworkEvent(new byte[] { MsgActivate, (byte)index, (byte)slot }, DeliveryMethod.ReliableOrdered, null);
        }
    }

    private Vector3 SlotPosition(int slot)
    {
        return RodSpawnPoint.position + RodSpawnPoint.right * (RodSpacing * slot);
    }

    // The first place on the table with no rod lying in it, or -1.
    private int FreeSlot()
    {
        Vector3 halfExtents = new Vector3(0.04f, 0.06f, 1.05f);
        for (int slot = 0; slot < RodSlots; slot++)
        {
            Vector3 center = SlotPosition(slot) + RodSpawnPoint.forward * 1.05f;
            if (!Physics.CheckBox(center, halfExtents, RodSpawnPoint.rotation, RodLayerMask, QueryTriggerInteraction.Ignore)) return slot;
        }
        return -1;
    }

    // slot < 0: leave the rod where it is (its pickup sync places it).
    private void ActivateRod(int index, int slot)
    {
        if (index < 0 || index >= Rods.Length) return;
        if ((activeMask & (1 << index)) != 0) return;
        activeMask |= 1 << index;

        FishingRod rod = Rods[index];
        if (slot >= 0)
        {
            Vector3 position = SlotPosition(slot);
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
        // t squared, so big ones are rarer. The crowns below rely on this distribution.
        float t = Random.Range(0f, 1f);
        return Mathf.Lerp(SpeciesMinCm[species], SpeciesMaxCm[species], t * t);
    }

    public bool IsRare(int species)
    {
        return species >= 0 && species < SpeciesRare.Length && SpeciesRare[species] != 0;
    }

    public int SpeciesIndex(string name)
    {
        for (int i = 0; i < SpeciesNames.Length; i++)
        {
            if (SpeciesNames[i] == name) return i;
        }
        return -1;
    }

    // Where a size sits in the species' range, 0 (smallest) to 1 (largest).
    private float SizeFraction(int species, float cm)
    {
        float span = SpeciesMaxCm[species] - SpeciesMinCm[species];
        if (span <= 0f) return 0f;
        return Mathf.Clamp01((cm - SpeciesMinCm[species]) / span);
    }

    // Sizes are drawn as min + span * t^2 with t uniform, so P(size <= s) = sqrt(fraction):
    // the largest p of catches have fraction >= (1 - p)^2, the smallest p have fraction <= p^2.
    // 2 = gold, 1 = silver, 0 = none.
    public int LargeCrown(int species, float cm)
    {
        float fraction = SizeFraction(species, cm);
        float gold = 1f - CrownGoldPercent * 0.01f;
        float silver = 1f - CrownSilverPercent * 0.01f;
        if (fraction >= gold * gold) return 2;
        if (fraction >= silver * silver) return 1;
        return 0;
    }

    public int SmallCrown(int species, float cm)
    {
        float fraction = SizeFraction(species, cm);
        float gold = CrownGoldPercent * 0.01f;
        float silver = CrownSilverPercent * 0.01f;
        if (fraction <= gold * gold) return 2;
        if (fraction <= silver * silver) return 1;
        return 0;
    }

    public int RewardExp(int species, float cm)
    {
        return Mathf.RoundToInt(SpeciesBaseExp[species] * (1f + RewardSizeBonus * SizeFraction(species, cm)));
    }

    public int RewardMoney(int species, float cm)
    {
        return Mathf.RoundToInt(SpeciesBaseMoney[species] * (1f + RewardSizeBonus * SizeFraction(species, cm)));
    }

    /// <summary>Called by a rod on every client when a fish is landed.</summary>
    public void ReportCatch(int species, float cm, ushort sender, bool local)
    {
        if (species < 0 || species >= SpeciesNames.Length) return;

        AddRecent(species, cm, local ? LocalName() : RemoteName(sender), local ? 1 : 0);
        RefreshRecent();

        SetStandText(SpeciesName(species) + " " + Mathf.RoundToInt(cm) + "cm!");
        standMessageUntil = Time.time + 4f;

        if (local)
        {
            AddToZukan(species, 1, cm, cm);
            RefreshZukan();
            QueueCatch(species, cm);
        }
    }

    // Display names, or "" when unknown (shown as "someone").
    private string LocalName()
    {
        IBasisPlayer player = BasisPlayersShim.Local;
        if (player == null) return "";
        return player.DisplayName;
    }

    private string RemoteName(ushort id)
    {
        IBasisPlayer player = BasisPlayersShim.GetById(id);
        if (player == null) return "";
        return player.DisplayName;
    }

    private void AddRecent(int species, float cm, string who, int mine)
    {
        for (int i = RecentCount - 1; i > 0; i--)
        {
            recentSpecies[i] = recentSpecies[i - 1];
            recentCm[i] = recentCm[i - 1];
            recentWho[i] = recentWho[i - 1];
            recentMine[i] = recentMine[i - 1];
        }
        recentSpecies[0] = species;
        recentCm[0] = cm;
        recentWho[0] = who == null ? "" : who;
        recentMine[0] = mine;
    }

    // ---- Catches waiting for the server (FishingBackend pulls these) ----

    private void QueueCatch(int species, float cm)
    {
        if (pendingLength == PendingCapacity)
        {
            // Full (server unreachable for a long time): drop the oldest.
            pendingStart = (pendingStart + 1) % PendingCapacity;
            pendingLength--;
        }
        int slot = (pendingStart + pendingLength) % PendingCapacity;
        pendingSpecies[slot] = species;
        pendingCm[slot] = cm;
        pendingLength++;
    }

    public int PendingCatchCount()
    {
        return pendingLength;
    }

    public string PendingFishName()
    {
        return SpeciesNames[pendingSpecies[pendingStart]];
    }

    public float PendingSizeCm()
    {
        return pendingCm[pendingStart];
    }

    public int PendingExp()
    {
        return RewardExp(pendingSpecies[pendingStart], pendingCm[pendingStart]);
    }

    public int PendingMoney()
    {
        return RewardMoney(pendingSpecies[pendingStart], pendingCm[pendingStart]);
    }

    public void PopPendingCatch()
    {
        if (pendingLength == 0) return;
        pendingStart = (pendingStart + 1) % PendingCapacity;
        pendingLength--;
    }

    // ---- Encyclopedia (FishingBackend pushes the server's copy here) ----

    private void AddToZukan(int species, int count, float min, float max)
    {
        if (myCount[species] == 0)
        {
            myMin[species] = min;
            myMax[species] = max;
        }
        else
        {
            if (min < myMin[species]) myMin[species] = min;
            if (max > myMax[species]) myMax[species] = max;
        }
        myCount[species] += count;
    }

    /// <summary>Starts replacing the encyclopedia with the server's copy.</summary>
    public void BeginServerZukan()
    {
        for (int i = 0; i < myCount.Length; i++) myCount[i] = 0;
    }

    /// <summary>One species from the server; names not in the fish data are ignored.</summary>
    public void ServerZukanEntry(string fishName, int count, float minCm, float maxCm)
    {
        int species = SpeciesIndex(fishName);
        if (species < 0 || count <= 0) return;
        AddToZukan(species, count, minCm, maxCm);
    }

    public void EndServerZukan()
    {
        RefreshZukan();
    }

    public void SetZukanStatus(string text)
    {
        if (ZukanStatus != null) ZukanStatus.text = text;
    }

    public void SetRankingText(string text)
    {
        if (RankingText != null) RankingText.text = text;
    }

    private void RefreshZukan()
    {
        for (int i = 0; i < SpeciesNames.Length && i < ZukanName.Length; i++)
        {
            bool caught = myCount[i] > 0;
            ZukanName[i].text = caught ? SpeciesName(i) : Text("zukan.unknown");
            ZukanCount[i].text = caught ? "×" + myCount[i] : "-";
            ZukanMax[i].text = caught ? FormatCm(myMax[i]) : "-";
            ZukanMin[i].text = caught ? FormatCm(myMin[i]) : "-";
            int large = caught ? LargeCrown(i, myMax[i]) : 0;
            int small = caught ? SmallCrown(i, myMin[i]) : 0;
            ZukanMaxGold[i].SetActive(large == 2);
            ZukanMaxSilver[i].SetActive(large == 1);
            ZukanMinGold[i].SetActive(small == 2);
            ZukanMinSilver[i].SetActive(small == 1);
        }
    }

    // One decimal, always with a dot (ToString would follow the player's locale).
    public static string FormatCm(float cm)
    {
        int tenths = Mathf.RoundToInt(cm * 10f);
        return (tenths / 10) + "." + (tenths % 10) + "cm";
    }

    private void RefreshRecent()
    {
        if (RecentText == null) return;
        StringBuilder builder = new StringBuilder();
        builder.Append("<b>");
        builder.Append(Text("recent.title"));
        builder.Append("</b>\n");
        bool any = false;
        for (int i = 0; i < RecentCount; i++)
        {
            int species = recentSpecies[i];
            if (species < 0 || species >= SpeciesNames.Length) continue;
            if (IsRare(species)) builder.Append("<color=#E0A100>★</color> ");
            builder.Append("<b>");
            builder.Append(SpeciesName(species));
            builder.Append("</b> ");
            builder.Append(Mathf.RoundToInt(recentCm[i]));
            builder.Append("cm  <color=#7A8A80>");
            if (recentMine[i] != 0) builder.Append(Text("recent.you"));
            else if (recentWho[i].Length == 0) builder.Append(Text("recent.someone"));
            else
            {
                builder.Append("<noparse>");
                builder.Append(recentWho[i]);
                builder.Append("</noparse>");
            }
            builder.Append("</color>\n");
            any = true;
        }
        if (!any)
        {
            builder.Append("<color=#7A8A80>");
            builder.Append(Text("recent.empty"));
            builder.Append("</color>");
        }
        RecentText.text = builder.ToString();
    }

    private void SetStandText(string text)
    {
        if (StandText != null) StandText.text = text;
    }

    // ---- Networking ----

    private void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || net == null || !net.IsOwnedLocallyOnServer) return;

        // [MsgSnapshot, mask (4 bytes), count, then per catch: species, cm (4 bytes),
        //  name length (2 bytes), name as UTF-8]. "Mine" catches go out under the owner's name.
        int count = 0;
        int size = 6;
        byte[][] names = new byte[RecentCount][];
        for (int i = 0; i < RecentCount; i++)
        {
            if (recentSpecies[i] < 0) continue;
            string who = recentWho[i];
            if (recentMine[i] != 0 && who.Length == 0) who = LocalName();
            names[i] = Encoding.UTF8.GetBytes(who == null ? "" : who);
            size += 7 + names[i].Length;
            count++;
        }
        byte[] buffer = new byte[size];
        buffer[0] = MsgSnapshot;
        buffer[1] = (byte)(activeMask & 0xFF);
        buffer[2] = (byte)((activeMask >> 8) & 0xFF);
        buffer[3] = (byte)((activeMask >> 16) & 0xFF);
        buffer[4] = (byte)((activeMask >> 24) & 0xFF);
        buffer[5] = (byte)count;
        int at = 6;
        // Oldest first, so the receiver can add them in order.
        for (int i = RecentCount - 1; i >= 0; i--)
        {
            if (recentSpecies[i] < 0) continue;
            buffer[at] = (byte)recentSpecies[i];
            byte[] cm = System.BitConverter.GetBytes(recentCm[i]);
            buffer[at + 1] = cm[0];
            buffer[at + 2] = cm[1];
            buffer[at + 3] = cm[2];
            buffer[at + 4] = cm[3];
            buffer[at + 5] = (byte)(names[i].Length & 0xFF);
            buffer[at + 6] = (byte)((names[i].Length >> 8) & 0xFF);
            for (int k = 0; k < names[i].Length; k++) buffer[at + 7 + k] = names[i][k];
            at += 7 + names[i].Length;
        }
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });
    }

    private void OnNetworkMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 2) return;

        if (buffer[0] == MsgActivate && buffer.Length >= 3)
        {
            ActivateRod(buffer[1], buffer[2]);
        }
        else if (buffer[0] == MsgSnapshot && buffer.Length >= 6)
        {
            int mask = buffer[1] | (buffer[2] << 8) | (buffer[3] << 16) | (buffer[4] << 24);
            for (int i = 0; i < Rods.Length; i++)
            {
                // Rods already out keep the pose their pickup sync gives them.
                if ((mask & (1 << i)) != 0) ActivateRod(i, -1);
            }
            int count = buffer[5];
            int at = 6;
            for (int i = 0; i < count && at + 7 <= buffer.Length; i++)
            {
                int species = buffer[at];
                float cm = System.BitConverter.ToSingle(buffer, at + 1);
                int nameLength = buffer[at + 5] | (buffer[at + 6] << 8);
                if (at + 7 + nameLength > buffer.Length) break;
                string who = Encoding.UTF8.GetString(buffer, at + 7, nameLength);
                if (species < SpeciesNames.Length) AddRecent(species, cm, who, 0);
                at += 7 + nameLength;
            }
            RefreshRecent();
        }
    }
}
