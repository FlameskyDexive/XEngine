using XEngine.Runtime.UI;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Small host-side stand-in for a package Graphic.</summary>
public sealed class SelectableGraphicBridgeStub : MonoBehaviour, ISelectableSpriteGraphic
{
    public Color Color { get; set; } = Color.White;
    public AssetRef<Sprite> Sprite { get; set; }
}

public sealed class SelectableGraphicBridgeTests : RuntimeTestBase
{
    [Fact]
    public void SelectableDrivesAComponentGraphicThroughTheRuntimeBridge()
    {
        using var scene = CreateScene(enable: false);
        GameObject go = CreateGameObject("PackageButton");
        Selectable selectable = go.AddComponent<Selectable>();
        SelectableGraphicBridgeStub graphic = go.AddComponent<SelectableGraphicBridgeStub>();
        selectable.TargetGraphicComponent = graphic;
        selectable.TransitionMode = Selectable.Transition.None;
        scene.Add(go);
        scene.Enable();

        selectable.Interactable = false;

        Assert.Same(graphic, selectable.GraphicTarget);
        Assert.Equal(selectable.DisabledColor, graphic.Color);
    }

    [Fact]
    public void LegacyUIImageTargetStillDrivesColorAndRemainsReadable()
    {
        using var scene = CreateScene(enable: false);
        GameObject go = CreateGameObject("LegacyButton");
        UIImage image = go.AddComponent<UIImage>();
        Selectable selectable = go.AddComponent<Selectable>();
        selectable.TransitionMode = Selectable.Transition.None;
        scene.Add(go);
        scene.Enable();

        selectable.TargetGraphic = image;
        selectable.Interactable = false;

        Assert.Same(image, selectable.TargetGraphic);
        Assert.Same(image, selectable.GraphicTarget);
        Assert.Equal(selectable.DisabledColor, image.Color);
    }

    [Fact]
    public void TargetGraphicComponentRejectsComponentsWithoutTheColorContract()
    {
        using var scene = CreateScene(enable: false);
        GameObject go = CreateGameObject("Button");
        Selectable selectable = go.AddComponent<Selectable>();
        PlainComponent unrelated = go.AddComponent<PlainComponent>();

        selectable.TargetGraphicComponent = unrelated;

        Assert.Null(selectable.TargetGraphicComponent);
        Assert.Null(selectable.GraphicTarget);
    }

    [Fact]
    public void SpriteSwapUsesStateSpriteAndRestoresTheTargetDefault()
    {
        using var scene = CreateScene(enable: false);
        GameObject go = CreateGameObject("SpriteSwapButton");
        Selectable selectable = go.AddComponent<Selectable>();
        SelectableGraphicBridgeStub graphic = go.AddComponent<SelectableGraphicBridgeStub>();
        var normal = new Sprite();
        var highlighted = new Sprite();
        graphic.Sprite = new AssetRef<Sprite>(normal);
        selectable.TargetGraphicComponent = graphic;
        selectable.SpriteState = new Selectable.SpriteSwapState { HighlightedSprite = new AssetRef<Sprite>(highlighted) };
        selectable.TransitionMode = Selectable.Transition.SpriteSwap;
        scene.Add(go);
        scene.Enable();

        selectable.OnPointerEnter(new PointerEventData());
        Assert.Same(highlighted, graphic.Sprite.Res);

        selectable.OnPointerExit(new PointerEventData());
        Assert.Same(normal, graphic.Sprite.Res);

        selectable.OnPointerEnter(new PointerEventData());
        selectable.TransitionMode = Selectable.Transition.ColorTint;
        Assert.Same(normal, graphic.Sprite.Res);

        selectable.TransitionMode = Selectable.Transition.SpriteSwap;
        selectable.OnPointerEnter(new PointerEventData());
        SelectableGraphicBridgeStub replacement = go.AddComponent<SelectableGraphicBridgeStub>();
        selectable.TargetGraphicComponent = replacement;
        Assert.Same(normal, graphic.Sprite.Res);
    }
}
