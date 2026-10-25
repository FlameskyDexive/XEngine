// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class InputCursorLockTests
{
    [Fact]
    public void ClearLockContexts_UnlocksAndDrainsStackedPushes()
    {
        var context = new CursorLockContext();
        global::XEngine.Runtime.Input.PushLockContext(context);
        global::XEngine.Runtime.Input.PushLockContext(context);
        global::XEngine.Runtime.Input.LockCursor();
        Assert.True(global::XEngine.Runtime.Input.CursorLocked);

        global::XEngine.Runtime.Input.ClearLockContexts();

        Assert.False(global::XEngine.Runtime.Input.CursorLocked);
        global::XEngine.Runtime.Input.LockCursor();
        global::XEngine.Runtime.Input.ClearLockContexts();
        Assert.False(global::XEngine.Runtime.Input.CursorLocked);
    }
}
