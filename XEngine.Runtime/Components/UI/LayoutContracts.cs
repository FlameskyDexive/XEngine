using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Runtime-neutral size contract implemented by package layout components.</summary>
public interface ILayoutElementHost
{
    bool IgnoreLayout { get; }
    float MinWidth { get; }
    float PreferredWidth { get; }
    float FlexibleWidth { get; }
    float MinHeight { get; }
    float PreferredHeight { get; }
    float FlexibleHeight { get; }
}

/// <summary>Runtime-neutral parent layout contract; the host never references a package type.</summary>
public interface ILayoutGroupHost : ILayoutElementHost
{
    void Arrange(Rect rect);
}
