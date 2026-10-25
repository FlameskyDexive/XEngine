using System;

using XEngine.Scribe;
using XEngine.PaperUI.LayoutEngine;
using XEngine.PaperUI.Utilities;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using XEngine.Vector.Spatial;

namespace XEngine.PaperUI
{
    /// <summary>
    /// Typed, allocation-free storage for an element's resolved current style values. Replaces the
    /// per-property boxed dictionary that layout and rendering read every frame: values live in typed
    /// fields, "is this explicitly set" is a single <see cref="ulong"/> bitmask (GuiProp has fewer
    /// than 64 members), and unset fields already hold their default so a read is just a field access.
    /// </summary>
    internal struct StyleValues
    {
        private ulong _set;

        // One field per GuiProp. An ElementStyle starts each field at its default (see CreateDefaults),
        // so a read is just a field access - no separate default lookup.
        public Color BackgroundColor;
        public Gradient BackgroundGradient;
        public Color BorderColor;
        public float BorderWidth;
        public Float4 Rounded;
        public BoxShadow BoxShadow;
        public float BackdropBlur;
        public float AspectRatio;
        public UnitValue Width, Height, MinWidth, MaxWidth, MinHeight, MaxHeight;
        public UnitValue Left, Right, Top, Bottom;
        public UnitValue MinLeft, MaxLeft, MinRight, MaxRight, MinTop, MaxTop, MinBottom, MaxBottom;
        public UnitValue ChildLeft, ChildRight, ChildTop, ChildBottom, RowBetween, ColBetween;
        public UnitValue PaddingLeft, PaddingRight, PaddingTop, PaddingBottom;
        public float TranslateX, TranslateY, ScaleX, ScaleY, Rotate, OriginX, OriginY, SkewX, SkewY;
        public Transform2D Transform;
        public object BackgroundImage;
        public Color TextColor;
        public float WordSpacing, LetterSpacing, LineHeight;
        public int TabSize;
        public float FontSize;
        public FontQuality TextQuality;

        public readonly bool Has(GuiProp p) => (_set & (1UL << (int)p)) != 0;

        /// <summary>Sets a property from a boxed value (unboxing into the typed field) and marks it set.</summary>
        public void Set(GuiProp p, object v)
        {
            _set |= 1UL << (int)p;
            switch (p)
            {
                case GuiProp.BackgroundColor: BackgroundColor = (Color)v; break;
                case GuiProp.BackgroundGradient: BackgroundGradient = (Gradient)v; break;
                case GuiProp.BorderColor: BorderColor = (Color)v; break;
                case GuiProp.BorderWidth: BorderWidth = (float)v; break;
                case GuiProp.Rounded: Rounded = (Float4)v; break;
                case GuiProp.BoxShadow: BoxShadow = (BoxShadow)v; break;
                case GuiProp.BackdropBlur: BackdropBlur = (float)v; break;
                case GuiProp.AspectRatio: AspectRatio = (float)v; break;
                case GuiProp.Width: Width = (UnitValue)v; break;
                case GuiProp.Height: Height = (UnitValue)v; break;
                case GuiProp.MinWidth: MinWidth = (UnitValue)v; break;
                case GuiProp.MaxWidth: MaxWidth = (UnitValue)v; break;
                case GuiProp.MinHeight: MinHeight = (UnitValue)v; break;
                case GuiProp.MaxHeight: MaxHeight = (UnitValue)v; break;
                case GuiProp.Left: Left = (UnitValue)v; break;
                case GuiProp.Right: Right = (UnitValue)v; break;
                case GuiProp.Top: Top = (UnitValue)v; break;
                case GuiProp.Bottom: Bottom = (UnitValue)v; break;
                case GuiProp.MinLeft: MinLeft = (UnitValue)v; break;
                case GuiProp.MaxLeft: MaxLeft = (UnitValue)v; break;
                case GuiProp.MinRight: MinRight = (UnitValue)v; break;
                case GuiProp.MaxRight: MaxRight = (UnitValue)v; break;
                case GuiProp.MinTop: MinTop = (UnitValue)v; break;
                case GuiProp.MaxTop: MaxTop = (UnitValue)v; break;
                case GuiProp.MinBottom: MinBottom = (UnitValue)v; break;
                case GuiProp.MaxBottom: MaxBottom = (UnitValue)v; break;
                case GuiProp.ChildLeft: ChildLeft = (UnitValue)v; break;
                case GuiProp.ChildRight: ChildRight = (UnitValue)v; break;
                case GuiProp.ChildTop: ChildTop = (UnitValue)v; break;
                case GuiProp.ChildBottom: ChildBottom = (UnitValue)v; break;
                case GuiProp.RowBetween: RowBetween = (UnitValue)v; break;
                case GuiProp.ColBetween: ColBetween = (UnitValue)v; break;
                case GuiProp.PaddingLeft: PaddingLeft = (UnitValue)v; break;
                case GuiProp.PaddingRight: PaddingRight = (UnitValue)v; break;
                case GuiProp.PaddingTop: PaddingTop = (UnitValue)v; break;
                case GuiProp.PaddingBottom: PaddingBottom = (UnitValue)v; break;
                case GuiProp.TranslateX: TranslateX = (float)v; break;
                case GuiProp.TranslateY: TranslateY = (float)v; break;
                case GuiProp.ScaleX: ScaleX = (float)v; break;
                case GuiProp.ScaleY: ScaleY = (float)v; break;
                case GuiProp.Rotate: Rotate = (float)v; break;
                case GuiProp.OriginX: OriginX = (float)v; break;
                case GuiProp.OriginY: OriginY = (float)v; break;
                case GuiProp.SkewX: SkewX = (float)v; break;
                case GuiProp.SkewY: SkewY = (float)v; break;
                case GuiProp.Transform: Transform = (Transform2D)v; break;
                case GuiProp.BackgroundImage: BackgroundImage = v; break;
                case GuiProp.TextColor: TextColor = (Color)v; break;
                case GuiProp.WordSpacing: WordSpacing = (float)v; break;
                case GuiProp.LetterSpacing: LetterSpacing = (float)v; break;
                case GuiProp.LineHeight: LineHeight = (float)v; break;
                case GuiProp.TabSize: TabSize = (int)v; break;
                case GuiProp.FontSize: FontSize = (float)v; break;
                case GuiProp.TextQuality: TextQuality = (FontQuality)v; break;
            }
        }

        /// <summary>Typed write of a UnitValue property — the fluent layout setters hit this per
        /// element per frame, and the boxed <see cref="Set"/> path turned each declaration into a
        /// dead heap object (2.5M collected on one overnight editor dump).</summary>
        public void SetUnit(GuiProp p, in UnitValue v)
        {
            _set |= 1UL << (int)p;
            switch (p)
            {
                case GuiProp.Width: Width = v; break;
                case GuiProp.Height: Height = v; break;
                case GuiProp.MinWidth: MinWidth = v; break;
                case GuiProp.MaxWidth: MaxWidth = v; break;
                case GuiProp.MinHeight: MinHeight = v; break;
                case GuiProp.MaxHeight: MaxHeight = v; break;
                case GuiProp.Left: Left = v; break;
                case GuiProp.Right: Right = v; break;
                case GuiProp.Top: Top = v; break;
                case GuiProp.Bottom: Bottom = v; break;
                case GuiProp.MinLeft: MinLeft = v; break;
                case GuiProp.MaxLeft: MaxLeft = v; break;
                case GuiProp.MinRight: MinRight = v; break;
                case GuiProp.MaxRight: MaxRight = v; break;
                case GuiProp.MinTop: MinTop = v; break;
                case GuiProp.MaxTop: MaxTop = v; break;
                case GuiProp.MinBottom: MinBottom = v; break;
                case GuiProp.MaxBottom: MaxBottom = v; break;
                case GuiProp.ChildLeft: ChildLeft = v; break;
                case GuiProp.ChildRight: ChildRight = v; break;
                case GuiProp.ChildTop: ChildTop = v; break;
                case GuiProp.ChildBottom: ChildBottom = v; break;
                case GuiProp.RowBetween: RowBetween = v; break;
                case GuiProp.ColBetween: ColBetween = v; break;
                case GuiProp.PaddingLeft: PaddingLeft = v; break;
                case GuiProp.PaddingRight: PaddingRight = v; break;
                case GuiProp.PaddingTop: PaddingTop = v; break;
                case GuiProp.PaddingBottom: PaddingBottom = v; break;
            }
        }

        /// <summary>Typed write of a float property (no boxing).</summary>
        public void SetFloat(GuiProp p, float v)
        {
            _set |= 1UL << (int)p;
            switch (p)
            {
                case GuiProp.BorderWidth: BorderWidth = v; break;
                case GuiProp.BackdropBlur: BackdropBlur = v; break;
                case GuiProp.AspectRatio: AspectRatio = v; break;
                case GuiProp.TranslateX: TranslateX = v; break;
                case GuiProp.TranslateY: TranslateY = v; break;
                case GuiProp.ScaleX: ScaleX = v; break;
                case GuiProp.ScaleY: ScaleY = v; break;
                case GuiProp.Rotate: Rotate = v; break;
                case GuiProp.OriginX: OriginX = v; break;
                case GuiProp.OriginY: OriginY = v; break;
                case GuiProp.SkewX: SkewX = v; break;
                case GuiProp.SkewY: SkewY = v; break;
                case GuiProp.WordSpacing: WordSpacing = v; break;
                case GuiProp.LetterSpacing: LetterSpacing = v; break;
                case GuiProp.LineHeight: LineHeight = v; break;
                case GuiProp.FontSize: FontSize = v; break;
            }
        }

        /// <summary>Typed write of an int property (no boxing).</summary>
        public void SetInt(GuiProp p, int v)
        {
            _set |= 1UL << (int)p;
            switch (p)
            {
                case GuiProp.TabSize: TabSize = v; break;
            }
        }

        /// <summary>Boxing read of a property (for the compatibility shim / cold callers).</summary>
        public readonly object GetBoxed(GuiProp p) => p switch
        {
            GuiProp.BackgroundColor => BackgroundColor,
            GuiProp.BackgroundGradient => BackgroundGradient,
            GuiProp.BorderColor => BorderColor,
            GuiProp.BorderWidth => BorderWidth,
            GuiProp.Rounded => Rounded,
            GuiProp.BoxShadow => BoxShadow,
            GuiProp.BackdropBlur => BackdropBlur,
            GuiProp.AspectRatio => AspectRatio,
            GuiProp.Width => Width,
            GuiProp.Height => Height,
            GuiProp.MinWidth => MinWidth,
            GuiProp.MaxWidth => MaxWidth,
            GuiProp.MinHeight => MinHeight,
            GuiProp.MaxHeight => MaxHeight,
            GuiProp.Left => Left,
            GuiProp.Right => Right,
            GuiProp.Top => Top,
            GuiProp.Bottom => Bottom,
            GuiProp.MinLeft => MinLeft,
            GuiProp.MaxLeft => MaxLeft,
            GuiProp.MinRight => MinRight,
            GuiProp.MaxRight => MaxRight,
            GuiProp.MinTop => MinTop,
            GuiProp.MaxTop => MaxTop,
            GuiProp.MinBottom => MinBottom,
            GuiProp.MaxBottom => MaxBottom,
            GuiProp.ChildLeft => ChildLeft,
            GuiProp.ChildRight => ChildRight,
            GuiProp.ChildTop => ChildTop,
            GuiProp.ChildBottom => ChildBottom,
            GuiProp.RowBetween => RowBetween,
            GuiProp.ColBetween => ColBetween,
            GuiProp.PaddingLeft => PaddingLeft,
            GuiProp.PaddingRight => PaddingRight,
            GuiProp.PaddingTop => PaddingTop,
            GuiProp.PaddingBottom => PaddingBottom,
            GuiProp.TranslateX => TranslateX,
            GuiProp.TranslateY => TranslateY,
            GuiProp.ScaleX => ScaleX,
            GuiProp.ScaleY => ScaleY,
            GuiProp.Rotate => Rotate,
            GuiProp.OriginX => OriginX,
            GuiProp.OriginY => OriginY,
            GuiProp.SkewX => SkewX,
            GuiProp.SkewY => SkewY,
            GuiProp.Transform => Transform,
            GuiProp.BackgroundImage => BackgroundImage,
            GuiProp.TextColor => TextColor,
            GuiProp.WordSpacing => WordSpacing,
            GuiProp.LetterSpacing => LetterSpacing,
            GuiProp.LineHeight => LineHeight,
            GuiProp.TabSize => TabSize,
            GuiProp.FontSize => FontSize,
            GuiProp.TextQuality => TextQuality,
            _ => null
        };

        /// <summary>Typed read of a UnitValue property (the hot layout path), no boxing.</summary>
        public readonly UnitValue GetUnit(GuiProp p) => p switch
        {
            GuiProp.Width => Width,
            GuiProp.Height => Height,
            GuiProp.MinWidth => MinWidth,
            GuiProp.MaxWidth => MaxWidth,
            GuiProp.MinHeight => MinHeight,
            GuiProp.MaxHeight => MaxHeight,
            GuiProp.Left => Left,
            GuiProp.Right => Right,
            GuiProp.Top => Top,
            GuiProp.Bottom => Bottom,
            GuiProp.MinLeft => MinLeft,
            GuiProp.MaxLeft => MaxLeft,
            GuiProp.MinRight => MinRight,
            GuiProp.MaxRight => MaxRight,
            GuiProp.MinTop => MinTop,
            GuiProp.MaxTop => MaxTop,
            GuiProp.MinBottom => MinBottom,
            GuiProp.MaxBottom => MaxBottom,
            GuiProp.ChildLeft => ChildLeft,
            GuiProp.ChildRight => ChildRight,
            GuiProp.ChildTop => ChildTop,
            GuiProp.ChildBottom => ChildBottom,
            GuiProp.RowBetween => RowBetween,
            GuiProp.ColBetween => ColBetween,
            GuiProp.PaddingLeft => PaddingLeft,
            GuiProp.PaddingRight => PaddingRight,
            GuiProp.PaddingTop => PaddingTop,
            GuiProp.PaddingBottom => PaddingBottom,
            _ => default
        };
    }
}
