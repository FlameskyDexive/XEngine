using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

/// <summary>Writes each Graphic vertex position into UV1 for custom UI shaders.</summary>
[AddComponentMenu("UI/Effects/Position As UV1")]
public sealed class PositionAsUV1 : UIBehaviour, IMeshModifier
{
    public void ModifyMesh(VertexHelper vh) => vh.SetPositionAsUV1();
    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
}
