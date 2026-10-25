using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Package-owned canvas scale policy matching the XGUI/UGUI screen-size semantics.</summary>
[AddComponentMenu("UI/Canvas Scaler")]
[RequireComponent(typeof(GameCanvas))]
public sealed class CanvasScaler : MonoBehaviour, ICanvasScaler
{
    public enum ScaleMode
    {
        ConstantPixelSize,
        ScaleWithScreenSize,
        ConstantPhysicalSize,
    }

    public enum ScreenMatchMode
    {
        MatchWidthOrHeight,
        Expand,
        Shrink,
    }

    public enum Unit
    {
        Centimeters,
        Millimeters,
        Inches,
        Points,
        Picas,
    }

    [SerializeField] private ScaleMode _uiScaleMode = ScaleMode.ConstantPixelSize;
    [SerializeField] private float _scaleFactor = 1f;
    [SerializeField] private Float2 _referenceResolution = new(1280f, 720f);
    [SerializeField] private float _matchWidthOrHeight = 0.5f;
    [SerializeField] private ScreenMatchMode _screenMatchMode = ScreenMatchMode.MatchWidthOrHeight;
    [SerializeField] private float _referencePixelsPerUnit = 100f;
    [SerializeField] private float _fallbackScreenDpi = CanvasPhysicalScale.BaselineDpi;
    [SerializeField] private float _defaultSpriteDpi = CanvasPhysicalScale.BaselineDpi;
    [SerializeField] private Unit _physicalUnit = Unit.Points;

    public ScaleMode UIScaleMode { get => _uiScaleMode; set => SetField(ref _uiScaleMode, value, UIDirtyFlags.Layout); }
    public float ScaleFactor { get => _scaleFactor; set => SetField(ref _scaleFactor, Maths.Max(0.001f, value), UIDirtyFlags.Layout); }
    public Float2 ReferenceResolution { get => _referenceResolution; set => SetField(ref _referenceResolution, value, UIDirtyFlags.Layout); }
    public float MatchWidthOrHeight { get => _matchWidthOrHeight; set => SetField(ref _matchWidthOrHeight, Maths.Clamp(value, 0f, 1f), UIDirtyFlags.Layout); }
    public ScreenMatchMode ScreenMatch { get => _screenMatchMode; set => SetField(ref _screenMatchMode, value, UIDirtyFlags.Layout); }
    public float ReferencePixelsPerUnit { get => _referencePixelsPerUnit; set => SetField(ref _referencePixelsPerUnit, Maths.Max(0.001f, value), UIDirtyFlags.Layout); }
    public float FallbackScreenDpi { get => _fallbackScreenDpi; set => SetField(ref _fallbackScreenDpi, Maths.Max(1f, value), UIDirtyFlags.Layout); }
    public float DefaultSpriteDpi { get => _defaultSpriteDpi; set => SetField(ref _defaultSpriteDpi, Maths.Max(1f, value), UIDirtyFlags.Layout); }
    public Unit PhysicalUnit { get => _physicalUnit; set => SetField(ref _physicalUnit, value, UIDirtyFlags.Layout); }

    public override void OnAddedToScene() => MarkCanvasDirty();
    public override void OnEnable() => MarkCanvasDirty();
    public override void OnDisable() => MarkCanvasDirty();

    public override void OnValidate()
    {
        if (!Application.IsPlaying) MarkCanvasDirty();
    }

    public float ComputeScaleFactor(Float2 screenSize, float fallbackScale)
    {
        if (_uiScaleMode == ScaleMode.ConstantPixelSize)
            return Maths.Max(_scaleFactor, 0.001f);
        if (_uiScaleMode == ScaleMode.ConstantPhysicalSize)
            return ComputePhysicalScale();

        float screenW = screenSize.X;
        float screenH = screenSize.Y;
        if (screenW <= 0f || screenH <= 0f) return Maths.Max(fallbackScale, 0.001f);

        float refW = Maths.Max(_referenceResolution.X, 1f);
        float refH = Maths.Max(_referenceResolution.Y, 1f);
        float logWidth = MathF.Log2(screenW / refW);
        float logHeight = MathF.Log2(screenH / refH);
        float logScale = _screenMatchMode switch
        {
            ScreenMatchMode.MatchWidthOrHeight => Maths.Lerp(logWidth, logHeight, Maths.Clamp(_matchWidthOrHeight, 0f, 1f)),
            ScreenMatchMode.Expand => Maths.Min(logWidth, logHeight),
            ScreenMatchMode.Shrink => Maths.Max(logWidth, logHeight),
            _ => 0f,
        };
        return Maths.Max(Maths.Pow(2f, logScale), 0.001f);
    }

    private float ComputePhysicalScale()
    {
        float contentScale = Window.InternalWindow != null ? Window.ContentScale : 0f;
        float dpi = CanvasPhysicalScale.ResolveDpi(_fallbackScreenDpi, contentScale);
        return CanvasPhysicalScale.ComputeScaleFactor(dpi, _defaultSpriteDpi);
    }

    private bool SetField<T>(ref T field, T value, UIDirtyFlags flags)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        GameObject.GetComponent<GameCanvas>()?.MarkDirty(flags);
        return true;
    }

    private void MarkCanvasDirty()
        => GameObject.GetComponent<GameCanvas>()?.MarkDirty(UIDirtyFlags.Layout);
}
