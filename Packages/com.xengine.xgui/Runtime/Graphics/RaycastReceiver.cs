namespace XEngine.Runtime.UI;

/// <summary>
/// Invisible Graphic that participates in pointer hit testing without drawing.
/// Clears the vertex buffer so there is no fill-rate cost.
/// </summary>
[AddComponentMenu("UI/Raycast Receiver")]
[RequireComponent(typeof(CanvasRenderer))]
public class RaycastReceiver : Graphic
{
    protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();
}
