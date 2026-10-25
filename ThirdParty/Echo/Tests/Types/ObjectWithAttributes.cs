// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;

namespace Tests.Types;

public class ObjectWithAttributes
{
    [FormerlySerializedAs("oldName")]
    public string NewName = "Test";

    [IgnoreOnNull]
    public string? OptionalField = null;

    [SerializeField]
    private float[] privateField = null;
}
