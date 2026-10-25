using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

/// <summary>Graphic base that participates in the parent RectMask clip chain.</summary>
public abstract class MaskableGraphic : Graphic
{
    public bool Maskable { get; set; } = true;

    public RectMask? GetRectMask()
        => Maskable ? GameObject.GetComponentInParent<RectMask>(includeSelf: false) : null;
}
