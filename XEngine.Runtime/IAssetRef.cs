// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

public interface IAssetRef
{
    Guid AssetID { get; set; }
    string Name { get; }
    bool IsRuntimeResource { get; }
    bool IsExplicitNull { get; }
    Type InstanceType { get; }

    object? GetInstance();
    void SetInstance(object? obj);
}
