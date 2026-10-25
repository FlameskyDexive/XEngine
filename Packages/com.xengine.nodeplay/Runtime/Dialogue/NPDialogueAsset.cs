using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.NodePlay.Dialogue;

/// <summary>A named role on a dialogue graph: resolved at play time to an
/// <c>IDialogueActor</c> component by key, with "SELF" reserved for the instigator.</summary>
public class NPActorParameter
{
    public string Key = "";
    public string Name = "";
}

/// <summary>Multi-actor dialogue tree. Actors are declared here and referenced by key from nodes.</summary>
[CreateAssetMenu("NodePlay/Dialogue Tree", Extension = NPGraphAsset.FileExtension, Order = 83)]
public partial class NPDialogueAsset : NPGraphAsset
{
    public List<NPActorParameter> ActorParameters = new();

    public const string InstigatorKey = "SELF";

    public NPDialogueAsset() => Module = "dialogue";

    public override void OnValidate()
    {
        base.OnValidate();
        ActorParameters ??= new List<NPActorParameter>();
    }
}
