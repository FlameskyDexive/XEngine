using System;

using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Screen-space hotbar: GameCanvas + Text listing slots. Keys 1-8 select.</summary>
[AddComponentMenu("Voxel World/Inventory HUD")]
public sealed class VoxelInventoryHud : MonoBehaviour
{
    public VoxelWorld? World;
    public TextComponent? Label;
    private float _refresh;

    public static string FormatHotbar(VoxelInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var parts = new string[inventory.Types.Length];
        for (int i = 0; i < inventory.Types.Length; i++)
        {
            string mark = i == inventory.Selected ? ">" : " ";
            parts[i] = $"{mark}{i + 1}:{inventory.Types[i]}x{inventory.Counts[i]}";
        }

        return string.Join("  ", parts);
    }

    public override void Start()
    {
        World ??= VoxelWorld.Active;
        EnsureLabel();
        Refresh();
    }

    public override void Update()
    {
        _refresh -= Time.UnscaledDeltaTime;
        if (_refresh > 0f) return;
        _refresh = 0.1f;
        World ??= VoxelWorld.Active;
        Refresh();
    }

    public void Refresh()
    {
        if (World == null)
            return;
        EnsureLabel();
        if (Label != null)
            Label.Text = FormatHotbar(World.Inventory);
    }

    public void EnsureLabel()
    {
        if (Label != null || GameObject == null)
            return;

        var canvasGo = new GameObject("VoxelHotbarCanvas");
        canvasGo.SetParent(GameObject, worldPositionStays: false);
        canvasGo.AddComponent<GameCanvas>();

        var labelGo = new GameObject("Hotbar");
        labelGo.SetParent(canvasGo, worldPositionStays: false);
        Label = labelGo.AddComponent<TextComponent>();
        Label.Size = 18;
        Label.Alignment = TextAlignment.BottomCenter;
        Label.TextColor = Color.White;
        var rect = labelGo.GetComponent<RectTransform>() ?? labelGo.AddComponent<RectTransform>();
        rect.AnchorMin = new Float2(0, 0);
        rect.AnchorMax = new Float2(1, 0);
        rect.Pivot = new Float2(0.5f, 0);
        rect.SizeDelta = new Float2(-32, 48);
        rect.AnchoredPosition = new Float2(0, 16);
    }
}
