using Cilbox;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Vibrates the controller holding a fishing rod with the pulses the rod asks for.
///
/// Uses Unity's XR haptics (InputDevice.SendHapticImpulse). The XR types are not on the world
/// script whitelist, so the world asks for them in the script permission prompt. Basis'
/// BasisInput.PlayHaptic would be the natural call, but a refused method on an allowed type
/// fails the whole world's scripts (including when the bundle is built), while a refused type
/// only fails the call that uses it.
///
/// A player who denies it gets a failed call here and Cilbox switches this script off. That is
/// why the call lives in its own component, and why it reads from the rod rather than the rod
/// calling in: calling into a switched-off script would throw in the rod.
/// </summary>
[Cilboxable]
public class FishingHaptics : MonoBehaviour
{
    public FishingRod Rod;

    private void Update()
    {
        float strength = Rod.HapticStrength();
        if (strength <= 0f) return;
        float seconds = Rod.HapticSeconds();
        int hand = Rod.HapticHand();
        Rod.ClearHaptic();
        if (hand < 0) return;

        // 0 left, 1 right, 2 both.
        if (hand != 1) Buzz(XRNode.LeftHand, strength, seconds);
        if (hand != 0) Buzz(XRNode.RightHand, strength, seconds);
    }

    private static void Buzz(XRNode node, float strength, float seconds)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid) device.SendHapticImpulse(0u, strength, seconds);
    }
}
