using System;
using System.Collections.Generic;

namespace XEngine.Editor;

public enum AssetReimportStatus
{
    Imported,
    SourceOnly,
    NotFound,
    MissingSource,
    Failed,
}

public sealed record AssetReimportFailure(Guid Guid, string Path, string Error);

/// <summary>
/// Result of an explicit synchronous reimport. Status/Error describe the selected target;
/// direct dependent failures are separate. SourceOnly is a successfully tracked input
/// (for example a shader include), not a new runtime object.
/// </summary>
public sealed record AssetReimportResult(
    Guid Guid, string? Path, AssetReimportStatus Status, string? Error,
    IReadOnlyList<AssetReimportFailure> DependentFailures)
{
    public bool TargetSucceeded => Status is AssetReimportStatus.Imported or AssetReimportStatus.SourceOnly;
    public bool Succeeded => TargetSucceeded && DependentFailures.Count == 0;
}
