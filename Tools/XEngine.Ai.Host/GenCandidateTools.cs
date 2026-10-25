using XEngine.Ai.Gateway;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

internal static class GenCandidateTools
{
    public static GenCandidateListResult List(
        string projectRoot, string? kind, string? query, bool importedOnly, bool includeDiscarded)
    {
        var catalog = new GenCandidateCatalog(projectRoot);
        var infos = new List<GenCandidateInfo>();
        foreach (GenCandidate c in catalog.List(kind, query, importedOnly, includeDiscarded))
            infos.Add(ToInfo(c));
        return new GenCandidateListResult(infos, catalog.ChosenTaskId);
    }

    public static GenCandidateInfo Choose(string projectRoot, string taskId)
        => ToInfo(new GenCandidateCatalog(projectRoot).Choose(taskId));

    public static GenCandidateInfo Discard(string projectRoot, string taskId)
        => ToInfo(new GenCandidateCatalog(projectRoot).Discard(taskId));

    private static GenCandidateInfo ToInfo(GenCandidate c)
        => new(c.TaskId, c.Provider, c.Kind, c.Prompt, c.State, c.InputHash,
            c.AssetPaths, c.CreatedAtUtc, c.Chosen, c.Discarded);
}
