using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using System;
using UnityEngine;

/// <summary>
/// A fishing rod: a bobber on a line hanging from the rod tip, and the bite logic for it.
///
/// The line stays short while the rod is carried, so the bobber dangles clear of the ground.
/// Use casts the bobber forward, paying line out until it lands; leave it in the pond and every
/// 10 to 30 seconds a fish nibbles (the bobber twitches), then strikes (the bobber goes under).
/// Press Use or yank the rod up during the strike to land it, and it hangs off the line until
/// Use releases it. Use on a floating bobber reels it back in.
///
/// Whoever holds the rod last owns it: the owner simulates the bobber and rolls the bites, and
/// streams the bobber position, state and hooked fish to everyone else, who only display it.
/// The rod's own transform is synced by its BasisPickupSyncNetworking.
/// </summary>
[Cilboxable]
public class FishingRod : MonoBehaviour
{
    // States
    private const int StateIdle = 0;     // bobber out of the water
    private const int StateWaiting = 1;  // floating, waiting for a bite
    private const int StateNibble = 2;   // a fish is nibbling: the bobber twitches
    private const int StateStrike = 3;   // the fish bit: the bobber is pulled under, land it now
    private const int StateHooked = 4;   // a fish hangs off the line

    // Messages
    private const byte MsgState = 1;
    private const byte MsgEvent = 2;
    private const byte EvNibble = 1;
    private const byte EvStrike = 2;
    private const byte EvCaught = 3;
    private const byte EvEscaped = 4;
    private const byte EvReleased = 5;
    private const int StatePacketSize = 23;
    private const int EventPacketSize = 7;

    [Header("World")]
    public FishingWorldController Controller;
    public int RodIndex;
    public float WaterLevel = -0.2f;
    public Vector3 PondCenter = Vector3.zero;
    public float PondRadius = 6f;
    public float GroundLevel = 0f;

    [Header("Parts")]
    public Transform Tip;
    public LineRenderer Line;
    public Transform Bobber;
    public Transform FishRoot;
    public GameObject[] FishModels;
    public float[] FishModelLengthCm;

    [Header("Effects")]
    public ParticleSystem Splash;
    public ParticleSystem Sparkle;
    public AudioSource Audio;
    public AudioClip PlopClip;
    public AudioClip SplashClip;
    public AudioClip CatchClip;
    public AudioClip EscapeClip;

    [Header("Tuning")]
    public float MinBiteSeconds = 10f;
    public float MaxBiteSeconds = 30f;
    public float NibbleSeconds = 1.6f;
    public float StrikeSeconds = 1.3f;
    public float LineLength = 1.3f;
    public float CarryLineLength = 1.3f;
    public float MinLineLength = 0.8f;
    public float MaxLineLength = 12f;
    public float CastSpeed = 6.5f;
    public float CastLift = 3f;
    public float YankSpeed = 2.2f;
    public float MaxBobberSpeed = 9f;
    public float HookDrop = 0.28f;
    public float SendInterval = 0.1f;
    // Debug only: speeds up the bite timer.
    public float BiteTimeScale = 1f;

    private BasisNetworkShim net;
    private BasisPickupInteractable pickup;

    private int state;
    private int species = -1;
    private float sizeCm;
    private float stateTime;
    private float waterTime;
    private float nextBiteAt;

    private Vector3 bob;
    private Vector3 bobPrev;
    private Vector3 remoteBob;
    private float remoteSeenAt = -10f;
    private Vector3 lastTip;
    private float tipUpSpeed;
    private bool inWater;
    private bool useDown;
    private bool casting;
    private float castTime;
    private Rigidbody body;
    private float nextSendAt;
    private float nextThrashAt;
    private Vector3 fishSwing;
    private Vector3[] linePoints;

    private void Start()
    {
        linePoints = new Vector3[14];
        Line.positionCount = 14;
        lastTip = Tip.position;
        bob = lastTip + Vector3.down * LineLength;
        bobPrev = bob;
        remoteBob = bob;
        RollNextBite();
        ShowFish(-1, 0f);

        body = this.GetComponent<Rigidbody>();
        pickup = this.GetComponent<BasisPickupInteractable>();
        if (pickup != null)
        {
            pickup.OnInteractStartEvent.AddListener(OnPickup);
            pickup.OnPickupUse.AddListener(OnPickupUse);
        }

        net = SafeUtil.MakeNetworkable(this);
        if (net != null)
        {
            net.NetworkMessageReceived += OnNetworkMessage;
            net.PlayerJoined += OnPlayerJoined;
        }
    }

    private void OnPickup(BasisInput input)
    {
        if (net != null && net.HasNetworkID && !net.IsOwnedLocallyOnServer)
        {
            net.TakeOwnership();
        }
    }

    private void OnPickupUse(BasisPickUpUseMode mode)
    {
        if (mode == BasisPickUpUseMode.OnPickUpUseDown)
        {
            useDown = true;
        }
    }

    /// <summary>Same as pressing Use on the rod. For testing from the editor.</summary>
    public void PressUse()
    {
        useDown = true;
    }

    public int CurrentState()
    {
        return state;
    }

    // The owner, or anyone while there is no network (editor, offline).
    private bool IsAuthority()
    {
        if (net == null || !net.HasNetworkID) return true;
        return net.IsOwnedLocallyOnServer;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        if (dt > 0.05f) dt = 0.05f;

        Vector3 tip = Tip.position;
        float upSpeed = (tip.y - lastTip.y) / dt;
        tipUpSpeed = Mathf.Lerp(tipUpSpeed, upSpeed, 0.5f);
        lastTip = tip;

        bool authority = IsAuthority();
        if (authority)
        {
            Simulate(tip, dt);
            RunBiteLogic(dt);
            if (Time.time >= nextSendAt)
            {
                nextSendAt = Time.time + SendInterval;
                SendState(DeliveryMethod.Unreliable, null);
            }
        }
        else
        {
            if (Time.time - remoteSeenAt < 2f)
            {
                bob = Vector3.Lerp(bob, remoteBob, 1f - Mathf.Exp(-12f * dt));
                bobPrev = bob;
            }
            else
            {
                Simulate(tip, dt);
            }
            stateTime += dt;
        }

        useDown = false;
        UpdateVisuals(tip, dt);
    }

    private bool InPond(Vector3 p)
    {
        float dx = p.x - PondCenter.x;
        float dz = p.z - PondCenter.z;
        return dx * dx + dz * dz < PondRadius * PondRadius;
    }

    // Verlet bobber on a rope of LineLength from the tip, floating on the pond.
    private void Simulate(Vector3 tip, float dt)
    {
        bool pond = InPond(bob);
        bool floating = pond && bob.y <= WaterLevel + 0.03f;

        Vector3 velocity = bob - bobPrev;
        velocity *= floating ? 0.86f : 0.995f;
        // Cap the speed: a constraint correction becomes velocity on the next step, so a sudden
        // jump of the rod (a teleport, a respawn, a hard flick) would otherwise fling the bobber.
        float maxStep = MaxBobberSpeed * dt;
        if (velocity.sqrMagnitude > maxStep * maxStep) velocity = velocity.normalized * maxStep;
        bobPrev = bob;
        bob += velocity + Vector3.down * (9.81f * dt * dt);

        // While a cast is flying the reel spools freely, so the line follows the bobber out.
        Vector3 fromTip = bob - tip;
        float distance = fromTip.magnitude;
        if (casting)
        {
            castTime += dt;
            LineLength = Mathf.Clamp(Mathf.Max(LineLength, distance), MinLineLength, MaxLineLength);
        }
        if (distance > LineLength && distance > 0.0001f)
        {
            bob = tip + fromTip * (LineLength / distance);
        }

        bool landed = false;
        if (InPond(bob))
        {
            float surface = WaterLevel + BobberDepth();
            if (bob.y < surface)
            {
                bob.y = Mathf.Lerp(bob.y, surface, 0.35f);
                bobPrev.y = bob.y;
                landed = true;
            }
        }
        else
        {
            float ground = GroundAt(bob) + 0.04f;
            if (bob.y < ground)
            {
                // Rest on the ground and slide along it: no bounce, a little friction.
                bob.y = ground;
                bobPrev.y = bob.y;
                bobPrev.x = Mathf.Lerp(bobPrev.x, bob.x, 0.12f);
                bobPrev.z = Mathf.Lerp(bobPrev.z, bob.z, 0.12f);
                landed = true;
            }
        }

        if (casting && (landed || castTime > 3f))
        {
            casting = false;
            LineLength = Mathf.Clamp((bob - tip).magnitude + 0.05f, MinLineLength, MaxLineLength);
        }

        inWater = InPond(bob) && bob.y <= WaterLevel + 0.03f;
    }

    // Height of whatever is under the bobber (ground, dock, table), ignoring the rod itself.
    private float GroundAt(Vector3 p)
    {
        Vector3 origin = p + Vector3.up * 1.5f;
        RaycastHit hit;
        for (int i = 0; i < 3; i++)
        {
            if (!Physics.Raycast(origin, Vector3.down, out hit, 5f, -1, QueryTriggerInteraction.Ignore)) break;
            if (body == null || hit.rigidbody != body) return hit.point.y;
            origin = hit.point + Vector3.down * 0.01f;
        }
        return GroundLevel;
    }

    private void Cast()
    {
        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 velocity = forward * CastSpeed + Vector3.up * CastLift;
        float dt = Mathf.Max(Time.deltaTime, 0.001f);
        bob = Tip.position;
        bobPrev = bob - velocity * dt;
        casting = true;
        castTime = 0f;
        PlayClip(PlopClip, 0.3f);
    }

    private void ReelIn()
    {
        casting = false;
        LineLength = CarryLineLength;
        SetState(StateIdle);
        RollNextBite();
    }

    // How far below the surface the bobber sits for the current state.
    private float BobberDepth()
    {
        float t = Time.time;
        if (state == StateNibble) return -Mathf.Abs(Mathf.Sin(t * 22f)) * 0.035f;
        if (state == StateStrike) return -0.14f;
        if (state == StateHooked) return -0.03f + Mathf.Sin(t * 17f) * 0.03f;
        return Mathf.Sin(t * 1.7f + RodIndex) * 0.006f;
    }

    private void RunBiteLogic(float dt)
    {
        stateTime += dt;

        if (useDown)
        {
            if (state == StateStrike)
            {
                Catch();
                return;
            }
            if (state == StateHooked)
            {
                Release();
                return;
            }
        }
        if (useDown)
        {
            if (inWater) ReelIn();
            else Cast();
            return;
        }

        if (state == StateStrike && tipUpSpeed > YankSpeed)
        {
            Catch();
            return;
        }

        if (state == StateHooked) return;

        if (!inWater)
        {
            if (state != StateIdle) SetState(StateIdle);
            return;
        }

        if (state == StateIdle)
        {
            SetState(StateWaiting);
            PlayClip(PlopClip, 0.6f);
            return;
        }

        if (state == StateWaiting)
        {
            waterTime += dt * BiteTimeScale;
            if (waterTime >= nextBiteAt)
            {
                SetState(StateNibble);
                SendEvent(EvNibble);
            }
        }
        else if (state == StateNibble)
        {
            if (stateTime >= NibbleSeconds)
            {
                SetState(StateStrike);
                SendEvent(EvStrike);
                PlayEvent(EvStrike);
            }
        }
        else if (state == StateStrike)
        {
            if (stateTime >= StrikeSeconds)
            {
                SetState(StateWaiting);
                RollNextBite();
                SendEvent(EvEscaped);
                PlayEvent(EvEscaped);
            }
        }
    }

    private void SetState(int next)
    {
        state = next;
        stateTime = 0f;
    }

    private void RollNextBite()
    {
        waterTime = 0f;
        nextBiteAt = UnityEngine.Random.Range(MinBiteSeconds, MaxBiteSeconds);
    }

    private void Catch()
    {
        int caught = 0;
        float cm = 20f;
        if (Controller != null)
        {
            caught = Controller.RollSpecies();
            cm = Controller.RollSizeCm(caught);
        }
        SetState(StateHooked);
        ShowFish(caught, cm);
        // Reel the catch up close to the rod.
        casting = false;
        LineLength = CarryLineLength;
        // Lift the bobber out with the fish.
        bobPrev = bob - Vector3.up * 0.06f;
        SendEvent(EvCaught);
        PlayEvent(EvCaught);
        if (Controller != null) Controller.ReportCatch(caught, cm, 0, true);
    }

    private void Release()
    {
        SetState(StateIdle);
        RollNextBite();
        SendEvent(EvReleased);
        PlayEvent(EvReleased);
        ShowFish(-1, 0f);
    }

    private void ShowFish(int index, float cm)
    {
        species = index;
        sizeCm = cm;
        for (int i = 0; i < FishModels.Length; i++)
        {
            FishModels[i].SetActive(i == index);
        }
        if (index >= 0)
        {
            float scale = cm / FishModelLengthCm[index];
            FishRoot.localScale = new Vector3(scale, scale, scale);
            if (Sparkle != null)
            {
                if (Controller != null && Controller.IsRare(index)) Sparkle.Play();
                else Sparkle.Stop();
            }
        }
        else if (Sparkle != null)
        {
            Sparkle.Stop();
        }
    }

    private void UpdateVisuals(Vector3 tip, float dt)
    {
        Bobber.position = bob;

        Vector3 hook = bob + Vector3.down * HookDrop;
        if (species >= 0)
        {
            // The fish hangs from the hook, mouth up, swinging and flapping.
            float t = Time.time;
            if (t >= nextThrashAt)
            {
                nextThrashAt = t + UnityEngine.Random.Range(0.4f, 1.4f);
                fishSwing = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f));
                if (inWater && Splash != null)
                {
                    Splash.transform.position = new Vector3(bob.x, WaterLevel, bob.z);
                    Splash.Play();
                }
            }
            float flap = Mathf.Sin(t * 14f) * 25f;
            Quaternion hang = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            Quaternion swing = Quaternion.Euler(fishSwing.x * 12f * Mathf.Sin(t * 3f), flap, fishSwing.z * 12f * Mathf.Cos(t * 2.5f));
            FishRoot.SetPositionAndRotation(hook, hang * swing);
        }

        // Line: a sagging curve from the tip to the bobber, then straight down to the hook.
        Vector3 toBob = bob - tip;
        float slack = Mathf.Max(0f, LineLength - toBob.magnitude);
        Vector3 sag = Vector3.down * Mathf.Min(slack * 0.5f + 0.02f, 1.5f);
        int curvePoints = 12;
        for (int i = 0; i < curvePoints; i++)
        {
            float u = i / (float)(curvePoints - 1);
            Vector3 straight = Vector3.Lerp(tip, bob, u);
            linePoints[i] = straight + sag * (4f * u * (1f - u));
        }
        linePoints[12] = bob;
        linePoints[13] = hook;
        Line.SetPositions(linePoints);
    }

    private void PlayClip(AudioClip clip, float volume)
    {
        if (Audio == null || clip == null) return;
        Audio.transform.position = bob;
        Audio.PlayOneShot(clip, volume);
    }

    private void PlaySplash()
    {
        if (Splash == null) return;
        Splash.transform.position = new Vector3(bob.x, WaterLevel, bob.z);
        Splash.Play();
    }

    private void PlayEvent(byte ev)
    {
        if (ev == EvStrike)
        {
            PlayClip(PlopClip, 1f);
            PlaySplash();
        }
        else if (ev == EvCaught)
        {
            PlaySplash();
            PlayClip(SplashClip, 1f);
            PlayClip(CatchClip, 0.8f);
        }
        else if (ev == EvEscaped)
        {
            PlayClip(EscapeClip, 0.7f);
        }
        else if (ev == EvReleased)
        {
            PlaySplash();
            PlayClip(SplashClip, 0.7f);
        }
    }

    // ---- Networking ----

    private void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || net == null || !net.IsOwnedLocallyOnServer) return;
        SendState(DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });
    }

    private void SendState(DeliveryMethod method, ushort[] recipients)
    {
        if (net == null || !net.HasNetworkID) return;
        byte[] buffer = new byte[StatePacketSize];
        buffer[0] = MsgState;
        buffer[1] = (byte)state;
        buffer[2] = (byte)(species + 1);
        WriteFloat(buffer, 3, sizeCm);
        WriteFloat(buffer, 7, bob.x);
        WriteFloat(buffer, 11, bob.y);
        WriteFloat(buffer, 15, bob.z);
        WriteFloat(buffer, 19, LineLength);
        net.SendCustomNetworkEvent(buffer, method, recipients);
    }

    private void SendEvent(byte ev)
    {
        if (net == null || !net.HasNetworkID) return;
        byte[] buffer = new byte[EventPacketSize];
        buffer[0] = MsgEvent;
        buffer[1] = ev;
        buffer[2] = (byte)(species + 1);
        WriteFloat(buffer, 3, sizeCm);
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, null);
    }

    private void OnNetworkMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 1) return;
        if (IsAuthority()) return;

        if (buffer[0] == MsgState && buffer.Length >= StatePacketSize)
        {
            int newState = buffer[1];
            int newSpecies = buffer[2] - 1;
            float newSize = ReadFloat(buffer, 3);
            remoteBob = new Vector3(ReadFloat(buffer, 7), ReadFloat(buffer, 11), ReadFloat(buffer, 15));
            LineLength = ReadFloat(buffer, 19);
            if (Time.time - remoteSeenAt > 2f)
            {
                bob = remoteBob;
                bobPrev = bob;
            }
            remoteSeenAt = Time.time;
            if (newState != state) SetState(newState);
            if (newSpecies != species || Mathf.Abs(newSize - sizeCm) > 0.01f)
            {
                if (newSpecies < FishModels.Length) ShowFish(newSpecies, newSize);
            }
        }
        else if (buffer[0] == MsgEvent && buffer.Length >= EventPacketSize)
        {
            byte ev = buffer[1];
            int evSpecies = buffer[2] - 1;
            float evSize = ReadFloat(buffer, 3);
            if (ev == EvCaught && evSpecies >= 0 && evSpecies < FishModels.Length)
            {
                SetState(StateHooked);
                ShowFish(evSpecies, evSize);
                if (Controller != null) Controller.ReportCatch(evSpecies, evSize, sender, false);
            }
            else if (ev == EvReleased)
            {
                SetState(StateIdle);
                ShowFish(-1, 0f);
            }
            else if (ev == EvNibble)
            {
                SetState(StateNibble);
            }
            else if (ev == EvStrike)
            {
                SetState(StateStrike);
            }
            else if (ev == EvEscaped)
            {
                SetState(StateWaiting);
            }
            PlayEvent(ev);
        }
    }

    private static void WriteFloat(byte[] buffer, int offset, float value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        buffer[offset] = bytes[0];
        buffer[offset + 1] = bytes[1];
        buffer[offset + 2] = bytes[2];
        buffer[offset + 3] = bytes[3];
    }

    private static float ReadFloat(byte[] buffer, int offset)
    {
        return BitConverter.ToSingle(buffer, offset);
    }
}
