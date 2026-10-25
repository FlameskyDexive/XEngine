// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.AssetBundles;

/// <summary>Lifecycle states of an <see cref="AsyncOperationBase"/>.</summary>
public enum EOperationStatus : byte
{
    /// <summary>Not yet started.</summary>
    None = 0,

    /// <summary>Started and being updated each frame by <see cref="AsyncOperationSystem"/>.</summary>
    Processing = 1,

    /// <summary>Completed successfully.</summary>
    Succeeded = 2,

    /// <summary>Completed with an error (see <see cref="AsyncOperationBase.Error"/>).</summary>
    Failed = 3,
}
