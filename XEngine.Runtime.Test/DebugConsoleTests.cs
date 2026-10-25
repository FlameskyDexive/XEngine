// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class DebugConsoleTests
{
    [Fact]
    public void TrySetConsoleTitle_DoesNotThrowWithoutAConsole()
    {
        Debug.TrySetConsoleTitle("XEngine test");
        Debug.TrySetConsoleTitle("XEngine test 2");
    }
}
