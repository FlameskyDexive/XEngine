using System;

namespace XEngine.Editor;

[AttributeUsage(AttributeTargets.Method)]
public class OnSceneSavedAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class OnUndoRedoAttribute : Attribute { }
