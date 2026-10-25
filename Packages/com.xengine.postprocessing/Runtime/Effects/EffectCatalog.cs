// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace XEngine.PostProcessing.Effects;

/// <summary>Discovers <see cref="PostProcessEffectSettings"/> types marked with <see cref="PostProcessAttribute"/>.</summary>
public static class EffectCatalog
{
    private static List<Type>? _types;

    public static IReadOnlyList<Type> AllSettingsTypes
    {
        get
        {
            if (_types != null)
                return _types;

            _types = new List<Type>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types!;
                }

                if (types == null)
                    continue;
                for (int i = 0; i < types.Length; i++)
                {
                    Type? type = types[i];
                    if (type == null || type.IsAbstract || !type.IsSubclassOf(typeof(PostProcessEffectSettings)))
                        continue;
                    if (type.GetCustomAttribute<PostProcessAttribute>() != null)
                        _types.Add(type);
                }
            }

            _types.Sort(static (x, y) => string.CompareOrdinal(MenuPath(x), MenuPath(y)));
            return _types;
        }
    }

    public static string MenuPath(Type settingsType)
        => settingsType.GetCustomAttribute<PostProcessAttribute>()?.MenuItem ?? settingsType.Name;

    public static void ResetCacheForTests() => _types = null;
}
