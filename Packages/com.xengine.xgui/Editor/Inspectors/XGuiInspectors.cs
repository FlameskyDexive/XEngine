using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime.UI;

namespace XEngine.XGUI.Editor;

[CustomEditor(typeof(Graphic))]
internal sealed class GraphicInspector : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        Graphic graphic = (Graphic)target;
        Undo.Snapshot(graphic);
        PropertyGridUtils.Draw(paper, id, graphic);
        if (EditorTheme.DefaultFont != null)
            Origami.Label(paper, id + "_package", "XGUI package Graphic").Show();
    }
}

[CustomEditor(typeof(Image))]
internal sealed class ImageInspector : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        Image image = (Image)target;
        Undo.Snapshot(image);
        PropertyGridUtils.Draw(paper, id, image);
        XGuiNativeSizeInspector.DrawSetNativeSizeButton(paper, id + "_sns", image);
    }
}

[CustomEditor(typeof(RawImage))]
internal sealed class RawImageInspector : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        RawImage image = (RawImage)target;
        Undo.Snapshot(image);
        PropertyGridUtils.Draw(paper, id, image);
        XGuiNativeSizeInspector.DrawSetNativeSizeButton(paper, id + "_sns", image);
    }
}

[CustomEditor(typeof(Text))]
internal sealed class TextInspector : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        Text text = (Text)target;
        Undo.Snapshot(text);
        PropertyGridUtils.Draw(paper, id, text);
    }
}

[CustomEditor(typeof(RaycastReceiver))]
internal sealed class RaycastReceiverInspector : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        RaycastReceiver receiver = (RaycastReceiver)target;
        Undo.Snapshot(receiver);
        PropertyGridUtils.Draw(paper, id, receiver);
        if (EditorTheme.DefaultFont != null)
            Origami.Label(paper, id + "_hint", "Invisible hit target (no mesh)").Show();
    }
}

[CustomEditor(typeof(RectMask2D))]
internal sealed class RectMask2DInspector : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        RectMask2D mask = (RectMask2D)target;
        Undo.Snapshot(mask);
        PropertyGridUtils.Draw(paper, id, mask);
    }
}

[CustomEditor(typeof(Mask))]
internal sealed class MaskInspector : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        Mask mask = (Mask)target;
        Undo.Snapshot(mask);
        PropertyGridUtils.Draw(paper, id, mask);
    }
}

internal static class XGuiNativeSizeInspector
{
    public static void DrawSetNativeSizeButton(Paper paper, string id, Graphic graphic)
    {
        Origami.Button(paper, id, "Set Native Size", () =>
        {
            var rt = graphic.GameObject.RectTransform;
            if (rt == null) return;
            Undo.Snapshot(rt);
            graphic.SetNativeSize();
        }).Show();
    }
}
