using System;

namespace XEngine.Editor;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AssetDoubleClickHandlerAttribute : Attribute
{
    public string[] Extensions { get; }
    public AssetDoubleClickHandlerAttribute(params string[] extensions) => Extensions = extensions;
}
