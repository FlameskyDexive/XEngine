using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>
/// Insets a stretched RectTransform to the current window safe area so HUD stays clear of
/// notches, status bars, and home indicators. Listens for <see cref="Window.SafeAreaChanged"/>.
/// </summary>
[AddComponentMenu("UI/Layout/Safe Area Fitter")]
public sealed class SafeAreaFitter : UIBehaviour, ILayoutSelfController
{
    [SerializeField] private bool _conformX = true;
    [SerializeField] private bool _conformY = true;

    public bool ConformX { get => _conformX; set => SetField(ref _conformX, value, UIDirtyFlags.Layout); }
    public bool ConformY { get => _conformY; set => SetField(ref _conformY, value, UIDirtyFlags.Layout); }

    public override void OnEnable()
    {
        base.OnEnable();
        Window.SafeAreaChanged += OnSafeAreaChanged;
    }

    public override void OnDisable()
    {
        Window.SafeAreaChanged -= OnSafeAreaChanged;
        base.OnDisable();
    }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void ApplyLayout(in Rect parentRect)
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null) return;

        GameCanvas? canvas = GetCanvas();
        float scale = canvas != null ? canvas.ScaleFactor : 1f;
        SafeAreaLayout.Apply(rt, SafeAreaLayout.ResolveInsets(), scale, ConformX, ConformY);
    }

    private void OnSafeAreaChanged(WindowSafeArea _)
        => MarkDirty(UIDirtyFlags.Layout);
}
