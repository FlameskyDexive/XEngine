using System;
using XEngine.Runtime;
using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

/// <summary>
/// Serializable event bridge for wiring inspector-authored <see cref="XEngineAction"/> calls to every
/// EventSystem lifecycle event without a custom script.
/// </summary>
[AddComponentMenu("UI/Event Trigger")]
public sealed class EventTrigger : UIBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler,
    IScrollHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler,
    ISelectHandler, IDeselectHandler, IMoveHandler, ISubmitHandler, ICancelHandler
{
    public XEngineAction PointerEnter = new();
    public XEngineAction PointerExit = new();
    public XEngineAction PointerDown = new();
    public XEngineAction PointerUp = new();
    public XEngineAction PointerClick = new();
    public XEngineAction Scroll = new();
    public XEngineAction BeginDrag = new();
    public XEngineAction Drag = new();
    public XEngineAction EndDrag = new();
    public XEngineAction Drop = new();
    public XEngineAction Select = new();
    public XEngineAction Deselect = new();
    public XEngineAction Move = new();
    public XEngineAction Submit = new();
    public XEngineAction Cancel = new();

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
    public void OnPointerEnter(PointerEventData e) => PointerEnter.Invoke();
    public void OnPointerExit(PointerEventData e) => PointerExit.Invoke();
    public void OnPointerDown(PointerEventData e) => PointerDown.Invoke();
    public void OnPointerUp(PointerEventData e) => PointerUp.Invoke();
    public void OnPointerClick(PointerEventData e) => PointerClick.Invoke();
    public void OnScroll(PointerEventData e) => Scroll.Invoke();
    public void OnBeginDrag(PointerEventData e) => BeginDrag.Invoke();
    public void OnDrag(PointerEventData e) => Drag.Invoke();
    public void OnEndDrag(PointerEventData e) => EndDrag.Invoke();
    public void OnDrop(PointerEventData e) => Drop.Invoke();
    public void OnSelect() => Select.Invoke();
    public void OnDeselect() => Deselect.Invoke();
    public void OnMove(MoveDirection direction) => Move.Invoke();
    public void OnSubmit() => Submit.Invoke();
    public void OnCancel() => Cancel.Invoke();
}
