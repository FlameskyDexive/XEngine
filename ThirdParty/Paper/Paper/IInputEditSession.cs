using System;

namespace XEngine.PaperUI;

/// <summary>Optional host transaction, created only when input starts mutating a control.</summary>
public interface IInputEditSession
{
    void Apply(Action mutation);
    void Complete(bool cancel);
}
