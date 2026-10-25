using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class SelectableNavigationTests : RuntimeTestBase
{
    [Fact]
    public void AutomaticHorizontalNavigationSelectsNearestCandidate()
    {
        using var scene = CreateScene(enable: false);
        GameObject systemObject = CreateGameObject("EventSystem");
        systemObject.AddComponent<EventSystem>();

        GameObject leftObject = CreateGameObject("Left");
        GameObject rightObject = CreateGameObject("Right");
        leftObject.EnsureRectTransform();
        rightObject.EnsureRectTransform();
        Selectable left = leftObject.AddComponent<Selectable>();
        Selectable right = rightObject.AddComponent<Selectable>();
        leftObject.RectTransform!.ComputedRect = new Rect(0f, 0f, 40f, 40f);
        rightObject.RectTransform!.ComputedRect = new Rect(60f, 0f, 100f, 40f);
        scene.Add(systemObject);
        scene.Add(leftObject);
        scene.Add(rightObject);
        scene.Enable();

        var system = systemObject.GetComponent<EventSystem>()!;
        system.SetSelected(leftObject);
        left.OnMove(MoveDirection.Right);

        Assert.Same(rightObject, system.Selected);
    }

    [Fact]
    public void ExplicitNoneNavigationDoesNotChangeSelection()
    {
        using var scene = CreateScene(enable: false);
        GameObject systemObject = CreateGameObject("EventSystem");
        systemObject.AddComponent<EventSystem>();
        GameObject firstObject = CreateGameObject("First");
        GameObject secondObject = CreateGameObject("Second");
        firstObject.EnsureRectTransform();
        secondObject.EnsureRectTransform();
        Selectable first = firstObject.AddComponent<Selectable>();
        secondObject.AddComponent<Selectable>();
        first.NavigationSettings = new Selectable.Navigation { NavigationMode = Selectable.Navigation.Mode.None };
        scene.Add(systemObject);
        scene.Add(firstObject);
        scene.Add(secondObject);
        scene.Enable();

        var system = systemObject.GetComponent<EventSystem>()!;
        system.SetSelected(firstObject);
        first.OnMove(MoveDirection.Right);

        Assert.Same(firstObject, system.Selected);
    }
}
