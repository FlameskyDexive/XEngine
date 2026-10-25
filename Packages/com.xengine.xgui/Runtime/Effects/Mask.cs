using XEngine.Echo;
using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

/// <summary>
/// Stencil mask for package Graphics. The mask Graphic writes its nesting depth into the
/// Canvas stencil buffer; descendants then test that value. A missing Graphic is reported by
/// diagnostics and leaves the hierarchy renderable without inventing a runtime object.
/// </summary>
[AddComponentMenu("UI/Mask")]
public sealed class Mask : UIBehaviour, ICanvasStencilMask
{
    [SerializeField] private bool _showMaskGraphic = true;

    public bool ShowMaskGraphic
    {
        get => _showMaskGraphic;
        set => SetField(ref _showMaskGraphic, value, UIDirtyFlags.Hierarchy | UIDirtyFlags.Material);
    }

    public override bool IsRaycastCandidate => false;

    public override void GenerateMesh(UIMeshBuilder _, in UIContext __) { }
}
