// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI;

/// <summary>Small deterministic state machine for terminal GPU device loss.</summary>
public sealed class GraphicsDeviceLossState
{
    public bool IsLost { get; private set; }
    public int LossCount { get; private set; }
    public string LastOperation { get; private set; } = string.Empty;
    public string LastResult { get; private set; } = string.Empty;

    public bool RecordLoss(string operation, string result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        if (!IsLost)
            LossCount++;
        IsLost = true;
        LastOperation = operation;
        LastResult = result;
        return true;
    }

    public void ThrowIfLost()
    {
        if (IsLost)
        {
            throw new InvalidOperationException(
                $"Graphics device is lost; refusing GPU operation. operation={LastOperation}, result={LastResult}, losses={LossCount}.");
        }
    }
}
