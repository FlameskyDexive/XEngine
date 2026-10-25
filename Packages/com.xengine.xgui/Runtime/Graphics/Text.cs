using XEngine.Runtime;

namespace XEngine.Runtime.UI;

/// <summary>Package Text compatibility facade over the engine's Scribe-backed TextComponent.</summary>
[AddComponentMenu("UI/Text")]
public class Text : TextComponent
{
    public string TextValue { get => base.Text; set => base.Text = value; }
}
