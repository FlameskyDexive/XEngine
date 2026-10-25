using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

public interface IMeshModifier { void ModifyMesh(VertexHelper vh); }
public interface IMaterialModifier { Material? GetModifiedMaterial(Material baseMaterial); }
public interface ILayoutController { }
public interface ILayoutGroup : ILayoutController, global::XEngine.Runtime.UI.ILayoutGroupHost { }
public interface ILayoutSelfController : ILayoutController { }
