using Basis.Scripts.BasisSdk.Interactions;
using Cilbox;
using TMPro;
using UnityEngine;

/// <summary>
/// One row of the share panel: a type chip or thumbnail, a title, who shared it, and up to
/// three action buttons. Its buttons only tell the panel which row and slot were pressed.
/// </summary>
[Cilboxable]
public class SharePanelRow : MonoBehaviour
{
    public SharePanel Panel;
    public int Index;
    public TextMeshPro Title;
    public TextMeshPro Meta;
    public TextMeshPro KindText;
    public MeshRenderer Chip;
    public MeshRenderer Thumb;
    public float ThumbSize = 0.075f;
    // Shown on the row whose item is on the large view. (The row's own face belongs to its
    // select button, which colours it on hover.)
    public GameObject SelectedMark;
    public BasisInteractableButton SelectButton;
    public BasisInteractableButton[] ActionButtons;
    public TextMeshPro[] ActionLabels;
    public GameObject Content;

    private void Start()
    {
        SelectButton.ButtonDown = OnSelect;
        ActionButtons[0].ButtonDown = OnAction1;
        ActionButtons[1].ButtonDown = OnAction2;
        ActionButtons[2].ButtonDown = OnAction3;
    }

    private void OnSelect()
    {
        Panel.RowPressed(Index, 0);
    }

    private void OnAction1()
    {
        Panel.RowPressed(Index, 1);
    }

    private void OnAction2()
    {
        Panel.RowPressed(Index, 2);
    }

    private void OnAction3()
    {
        Panel.RowPressed(Index, 3);
    }

    public void Hide()
    {
        Content.SetActive(false);
    }

    /// <summary>A line of text in place of an item ("Nothing shared yet").</summary>
    public void ShowMessage(string message)
    {
        Content.SetActive(true);
        Title.text = message;
        Meta.text = "";
        KindText.text = "";
        SelectedMark.SetActive(false);
        Thumb.gameObject.SetActive(false);
        Chip.gameObject.SetActive(false);
        SetAction(0, "");
        SetAction(1, "");
        SetAction(2, "");
    }

    public void Show(string kind, Color kindColor, string title, string meta, Texture thumb, float aspect,
        string action1, string action2, string action3, bool selected)
    {
        Content.SetActive(true);
        Title.text = title;
        Meta.text = meta;

        SelectedMark.SetActive(selected);

        if (thumb != null)
        {
            Thumb.gameObject.SetActive(true);
            Chip.gameObject.SetActive(false);
            KindText.text = "";
            MaterialPropertyBlock image = new MaterialPropertyBlock();
            image.SetTexture("_BaseMap", thumb);
            image.SetTexture("_MainTex", thumb);
            Thumb.SetPropertyBlock(image);
            float w = ThumbSize;
            float h = ThumbSize;
            if (aspect >= 1f) h = ThumbSize / aspect;
            else w = ThumbSize * aspect;
            Thumb.transform.localScale = new Vector3(w, h, 1f);
        }
        else
        {
            Thumb.gameObject.SetActive(false);
            Chip.gameObject.SetActive(true);
            MaterialPropertyBlock chip = new MaterialPropertyBlock();
            chip.SetColor("_BaseColor", kindColor);
            Chip.SetPropertyBlock(chip);
            KindText.text = kind;
        }

        SetAction(0, action1);
        SetAction(1, action2);
        SetAction(2, action3);
    }

    private void SetAction(int slot, string label)
    {
        bool on = label.Length > 0;
        ActionButtons[slot].gameObject.SetActive(on);
        if (on) ActionLabels[slot].text = label;
    }
}
