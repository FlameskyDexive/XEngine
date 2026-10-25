using System;

namespace XEngine.Editor.Core;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class InitializeOnLoadAttribute : Attribute { }
