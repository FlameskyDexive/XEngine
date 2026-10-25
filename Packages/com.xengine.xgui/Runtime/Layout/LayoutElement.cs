using System.Collections.Generic;
using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Public package layout-size contract used by groups and fitters.</summary>
public interface ILayoutElement : global::XEngine.Runtime.UI.ILayoutElementHost
{
}

/// <summary>Reports desired sizes to a parent layout group or ContentSizeFitter.</summary>
[AddComponentMenu("UI/Layout/Layout Element")]
[ComponentIcon("")]
public class LayoutElement : UIBehaviour, ILayoutElement
{
    [SerializeField] private bool _ignoreLayout;
    [SerializeField] private float _minWidth = -1f;
    [SerializeField] private float _minHeight = -1f;
    [SerializeField] private float _preferredWidth = -1f;
    [SerializeField] private float _preferredHeight = -1f;
    [SerializeField] private float _flexibleWidth = -1f;
    [SerializeField] private float _flexibleHeight = -1f;

    public bool IgnoreLayout { get => _ignoreLayout; set => SetField(ref _ignoreLayout, value, UIDirtyFlags.Layout); }
    public float MinWidth { get => _minWidth; set => SetField(ref _minWidth, value, UIDirtyFlags.Layout); }
    public float MinHeight { get => _minHeight; set => SetField(ref _minHeight, value, UIDirtyFlags.Layout); }
    public float PreferredWidth { get => _preferredWidth; set => SetField(ref _preferredWidth, value, UIDirtyFlags.Layout); }
    public float PreferredHeight { get => _preferredHeight; set => SetField(ref _preferredHeight, value, UIDirtyFlags.Layout); }
    public float FlexibleWidth { get => _flexibleWidth; set => SetField(ref _flexibleWidth, value, UIDirtyFlags.Layout); }
    public float FlexibleHeight { get => _flexibleHeight; set => SetField(ref _flexibleHeight, value, UIDirtyFlags.Layout); }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
}

/// <summary>Public package layout utility facade kept for source compatibility.</summary>
public static class LayoutUtility
{
    public static Float2 GetPreferredSize(GameObject go) => XGuiLayoutUtility.GetPreferredSize(go);
    public static Float2 GetMinSize(GameObject go) => XGuiLayoutUtility.GetMinSize(go);
    public static Float2 GetFlexible(GameObject go) => XGuiLayoutUtility.GetFlexible(go);
}

/// <summary>Resolves package and host layout elements without depending on concrete component types.</summary>
internal static class XGuiLayoutUtility
{
    public static Float2 GetPreferredSize(GameObject go) => GetPreferredSizeCore(go, componentScratch: null);

    internal static Float2 GetPreferredSize(GameObject go, List<MonoBehaviour> componentScratch)
        => GetPreferredSizeCore(go, componentScratch);

    private static Float2 GetPreferredSizeCore(GameObject go, List<MonoBehaviour>? componentScratch)
    {
        float pw = -1f, ph = -1f, mw = -1f, mh = -1f;
        if (componentScratch != null)
        {
            go.GetComponents(componentScratch);
            for (int i = 0; i < componentScratch.Count; i++)
                AccumulatePreferred(componentScratch[i], ref pw, ref ph, ref mw, ref mh);
        }
        else
        {
            foreach (MonoBehaviour component in go.GetComponents<MonoBehaviour>())
                AccumulatePreferred(component, ref pw, ref ph, ref mw, ref mh);
        }

        Float2 intrinsic = Intrinsic(go);
        float w = pw >= 0f ? pw : (mw >= 0f ? mw : intrinsic.X);
        float h = ph >= 0f ? ph : (mh >= 0f ? mh : intrinsic.Y);
        if (mw >= 0f) w = Maths.Max(w, mw);
        if (mh >= 0f) h = Maths.Max(h, mh);
        return new Float2(w, h);
    }

    public static Float2 GetMinSize(GameObject go) => GetMinSizeCore(go, componentScratch: null);

    internal static Float2 GetMinSize(GameObject go, List<MonoBehaviour> componentScratch)
        => GetMinSizeCore(go, componentScratch);

    private static Float2 GetMinSizeCore(GameObject go, List<MonoBehaviour>? componentScratch)
    {
        float mw = -1f, mh = -1f;
        if (componentScratch != null)
        {
            go.GetComponents(componentScratch);
            for (int i = 0; i < componentScratch.Count; i++)
                AccumulateMinimum(componentScratch[i], ref mw, ref mh);
        }
        else
        {
            foreach (MonoBehaviour component in go.GetComponents<MonoBehaviour>())
                AccumulateMinimum(component, ref mw, ref mh);
        }
        Float2 intrinsic = Intrinsic(go);
        return new Float2(mw >= 0f ? mw : intrinsic.X, mh >= 0f ? mh : intrinsic.Y);
    }

    public static Float2 GetFlexible(GameObject go) => GetFlexibleCore(go, componentScratch: null);

    internal static Float2 GetFlexible(GameObject go, List<MonoBehaviour> componentScratch)
        => GetFlexibleCore(go, componentScratch);

    private static Float2 GetFlexibleCore(GameObject go, List<MonoBehaviour>? componentScratch)
    {
        float fw = 0f, fh = 0f;
        if (componentScratch != null)
        {
            go.GetComponents(componentScratch);
            for (int i = 0; i < componentScratch.Count; i++)
                AccumulateFlexible(componentScratch[i], ref fw, ref fh);
        }
        else
        {
            foreach (MonoBehaviour component in go.GetComponents<MonoBehaviour>())
                AccumulateFlexible(component, ref fw, ref fh);
        }
        return new Float2(fw, fh);
    }

    private static void AccumulatePreferred(MonoBehaviour component, ref float preferredWidth, ref float preferredHeight,
        ref float minimumWidth, ref float minimumHeight)
    {
        if (component is not ILayoutElementHost element || !component.EnabledInHierarchy) return;
        preferredWidth = Maths.Max(preferredWidth, element.PreferredWidth);
        preferredHeight = Maths.Max(preferredHeight, element.PreferredHeight);
        minimumWidth = Maths.Max(minimumWidth, element.MinWidth);
        minimumHeight = Maths.Max(minimumHeight, element.MinHeight);
    }

    private static void AccumulateMinimum(MonoBehaviour component, ref float minimumWidth, ref float minimumHeight)
    {
        if (component is not ILayoutElementHost element || !component.EnabledInHierarchy) return;
        minimumWidth = Maths.Max(minimumWidth, element.MinWidth);
        minimumHeight = Maths.Max(minimumHeight, element.MinHeight);
    }

    private static void AccumulateFlexible(MonoBehaviour component, ref float flexibleWidth, ref float flexibleHeight)
    {
        if (component is not ILayoutElementHost element || !component.EnabledInHierarchy) return;
        if (element.FlexibleWidth > flexibleWidth) flexibleWidth = element.FlexibleWidth;
        if (element.FlexibleHeight > flexibleHeight) flexibleHeight = element.FlexibleHeight;
    }

    private static Float2 Intrinsic(GameObject go)
        => go.RectTransform is { } rt ? rt.SizeDelta : new Float2(100f, 100f);
}
