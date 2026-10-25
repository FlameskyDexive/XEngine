using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.VFX;

/// <summary>
/// Echo-serialized visual effect graph (<c>.vfx</c>). Contexts, blocks, operators, and edges are
/// plain fields so AI CLI can inspect and patch the file as text. Compile with
/// <see cref="VfxGraphCompiler"/>.
/// </summary>
[CreateAssetMenu("Effects/Visual Effect Graph", Extension = VfxGraphAsset.FileExtension, Order = 44)]
public sealed class VfxGraphAsset : ScriptableObject
{
    public const string FileExtension = ".vfx";

    public string GraphName = "VFX/Unnamed";
    public VfxTemplateKind Template = VfxTemplateKind.Empty;
    public int Capacity = 4096;
    public List<VfxProperty> Properties = new();
    public List<VfxContext> Contexts = new();
    public List<VfxBlock> Blocks = new();
    public List<VfxOperator> Operators = new();
    public List<VfxEdge> Edges = new();

    public override void OnValidate()
    {
        if (string.IsNullOrWhiteSpace(GraphName))
            GraphName = "VFX/Unnamed";
        if (Capacity < 1)
            Capacity = 1;
        Properties ??= new List<VfxProperty>();
        Contexts ??= new List<VfxContext>();
        Blocks ??= new List<VfxBlock>();
        Operators ??= new List<VfxOperator>();
        Edges ??= new List<VfxEdge>();
        int template = (int)Template;
        if (template < 0 || template > 5)
            Template = VfxTemplateKind.Empty;
    }

    public VfxProperty? FindProperty(string name) => VfxProperties.Find(Properties, name);

    public VfxProperty AddProperty(string name, VfxValueType type, string defaultValue = "")
        => VfxProperties.Add(Properties, name, type, defaultValue);

    public bool RemoveProperty(string name) => VfxProperties.Remove(Properties, name);
}
