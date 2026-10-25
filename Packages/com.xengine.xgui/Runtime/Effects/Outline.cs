using XEngine.Echo;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Appends four edge copies of a Graphic's mesh for an inexpensive outline.</summary>
[AddComponentMenu("UI/Effects/Outline")]
public sealed class Outline : UIBehaviour, IMeshModifier
{
    [SerializeField] private Float2 _effectDistance = new(1f, -1f);
    [SerializeField] private Color _effectColor = Color.Black;
    [SerializeField] private bool _useGraphicAlpha = true;

    public Float2 EffectDistance { get => _effectDistance; set => SetField(ref _effectDistance, value, UIDirtyFlags.Vertices); }
    public Color EffectColor { get => _effectColor; set => SetField(ref _effectColor, value, UIDirtyFlags.Vertices); }
    public bool UseGraphicAlpha { get => _useGraphicAlpha; set => SetField(ref _useGraphicAlpha, value, UIDirtyFlags.Vertices); }

    public void ModifyMesh(VertexHelper vh) => vh.AddOutline(_effectDistance, _effectColor, _useGraphicAlpha);
    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
}
