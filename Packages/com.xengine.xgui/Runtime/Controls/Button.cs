using System;
using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Runtime.UI;

/// <summary>Package-owned binary button preserving the legacy serialized click contract.</summary>
[AddComponentMenu("UI/Button")]
public class Button : Selectable, IPointerClickHandler, ISubmitHandler
{
    public event Action? OnClick;

    [SerializeField] private XEngineAction _onClick = new();
    public XEngineAction ClickAction => _onClick;

    public void OnPointerClick(PointerEventData e)
    {
        if (e.Button != MouseButton.Left || !IsInteractable()) return;
        HandleClick();
        e.Use();
    }

    public void OnSubmit()
    {
        if (!IsInteractable()) return;
        HandleClick();
    }

    protected virtual void HandleClick()
    {
        _onClick.Invoke();
        try { OnClick?.Invoke(); }
        catch (Exception ex)
        {
            Debug.LogError($"[Button] OnClick handler on '{Name}' threw: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
