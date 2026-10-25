using XEngine.Echo;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Appends a translated colored copy of a Graphic's mesh.</summary>
[AddComponentMenu("UI/Effects/Shadow")]
public sealed class Shadow : UIBehaviour, IMeshModifier
{
    [SerializeField] private Float2 _effectDistance = new(1f, -1f);
    [SerializeField] private Color _effectColor = new(0f, 0f, 0f, 0.5f);
    [SerializeField] private bool _useGraphicAlpha = true;

    public Float2 EffectDistance { get => _effectDistance; set => SetField(ref _effectDistance, value, UIDirtyFlags.Vertices); }
    public Color EffectColor { get => _effectColor; set => SetField(ref _effectColor, value, UIDirtyFlags.Vertices); }
    public bool UseGraphicAlpha { get => _useGraphicAlpha; set => SetField(ref _useGraphicAlpha, value, UIDirtyFlags.Vertices); }

    public void ModifyMesh(VertexHelper vh) => vh.AddShadow(_effectDistance, _effectColor, _useGraphicAlpha);
    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
}
