using System.Collections;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Editor-test stand-in for spawning a prop, so the script-permission prompt can be tried in
/// Play mode without building and loading a bundle.
///
/// A spawned prop arrives inactive, Content Police runs <see cref="ContentPoliceControl.OnBeforeContentActivated"/>
/// on it (where Cilbox consent scans it and holds its scripts if a prompt is needed), and only
/// then is it activated. This does the same three steps to a prop placed inactive in the scene.
///
/// One step a real spawn never needs: entering Play mode, Cilbox converts the scene and that
/// conversion initializes the prop's box on the spot. Consent then finds a box that already ran
/// without asking and refuses it for the session. A bundle's box arrives fresh, so this resets the
/// box to that state first. The fields are internal to the shim assembly, hence reflection.
///
/// Not part of the prop: it lives in the test scene only, and is not Cilboxable.
/// </summary>
public class BasisRestClientPropTestSpawner : MonoBehaviour
{
    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public GameObject Prop;

    [Tooltip("Seconds to wait after Play starts, so the local player and menus exist before the prompt shows.")]
    public float Delay = 3f;

    private IEnumerator Start()
    {
        if (Prop == null) yield break;
        yield return new WaitForSeconds(Delay);
        Spawn();
    }

    [ContextMenu("Spawn Now")]
    public void Spawn()
    {
        if (Prop == null || Prop.activeSelf) return;
        foreach (Cilbox.Cilbox box in Prop.GetComponentsInChildren<Cilbox.Cilbox>(true))
        {
            ResetToFresh(box);
        }
        ContentPoliceControl.OnBeforeContentActivated?.Invoke(Prop);
        Prop.SetActive(true);
    }

    private static void ResetToFresh(Cilbox.Cilbox box)
    {
        System.Type type = typeof(Cilbox.CilboxBasisCommon);
        if (!(box is Cilbox.CilboxBasisCommon)) return;

        FieldInfo state = type.GetField("consentState", Internal);
        FieldInfo hash = type.GetField("consentHash", Internal);
        FieldInfo granted = type.GetField("consentGranted", Internal);
        if (state == null || hash == null || granted == null)
        {
            Debug.LogWarning("[RestClientProp] Cilbox consent fields moved; the test spawner cannot reset the box, so the prompt may not show.");
            return;
        }

        state.SetValue(box, System.Enum.ToObject(state.FieldType, 0)); // Unchecked
        hash.SetValue(box, null);
        granted.SetValue(box, null);
        box.metadatas = null;
        box.ForceReinit();
    }
}
