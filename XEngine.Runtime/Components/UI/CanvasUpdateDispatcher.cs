// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.UI;

/// <summary>Ordered canvas update stages exposed to optional UI packages.</summary>
public enum CanvasUpdatePhase
{
    PreLayout,
    LayoutCalculation,
    LayoutControl,
    ClipUpdate,
    GraphicRebuild,
    MaterialUpdate,
}

/// <summary>
/// Package-owned canvas update scheduler. The Runtime host invokes this contract but never
/// references a concrete UI package assembly.
/// </summary>
public interface ICanvasUpdateDriver
{
    void Execute(GameCanvas canvas, CanvasUpdatePhase phase);
}

/// <summary>
/// Optional diagnostic extension for a package-owned canvas update driver. Runtime reports only the
/// failed canvas and convergence bound; a UI package can attach its own layout roots and component
/// details without making the Canvas host depend on package types.
/// </summary>
public interface ICanvasLayoutDiagnosticDriver
{
    void ReportNonConvergentLayout(GameCanvas canvas, int passCount);
}

/// <summary>
/// Optional layout-root extension for a package-owned canvas update driver. The package derives
/// highest effective roots from its dirty queue; Runtime retains ownership of the actual layout
/// walk and falls back to a full tree rebuild when no roots are supplied.
/// </summary>
public interface ICanvasLayoutRootProvider
{
    void CollectLayoutRoots(GameCanvas canvas, List<GameObject> destination);
}

/// <summary>
/// Bridges the Runtime Canvas host to one optional package scheduler. Registrations are explicitly
/// disposable so a collectible package AssemblyLoadContext is never retained across hot reload.
/// </summary>
public static class CanvasUpdateDispatcher
{
    private static ICanvasUpdateDriver? s_driver;

    public static IDisposable Register(ICanvasUpdateDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        s_driver = driver;
        return new Registration(driver);
    }

    public static bool IsRegistered => s_driver != null;

    internal static void Execute(GameCanvas canvas, CanvasUpdatePhase phase)
        => s_driver?.Execute(canvas, phase);

    /// <summary>Reports a bounded layout-cycle failure to the optional package diagnostics driver.</summary>
    internal static bool TryReportNonConvergentLayout(GameCanvas canvas, int passCount)
    {
        if (s_driver is not ICanvasLayoutDiagnosticDriver diagnostics) return false;
        diagnostics.ReportNonConvergentLayout(canvas, passCount);
        return true;
    }

    /// <summary>Copies the package-derived highest layout roots into <paramref name="destination"/>.</summary>
    internal static bool TryCollectLayoutRoots(GameCanvas canvas, List<GameObject> destination)
    {
        destination.Clear();
        if (s_driver is not ICanvasLayoutRootProvider provider) return false;
        provider.CollectLayoutRoots(canvas, destination);
        return destination.Count > 0;
    }

    /// <summary>Drops the package driver so a collectible AssemblyLoadContext can unload.</summary>
    [OnAssemblyUnload]
    public static void Reset() => s_driver = null;

    private sealed class Registration : IDisposable
    {
        private ICanvasUpdateDriver? _driver;

        public Registration(ICanvasUpdateDriver driver) => _driver = driver;

        public void Dispose()
        {
            ICanvasUpdateDriver? driver = _driver;
            if (driver == null) return;
            _driver = null;
            if (ReferenceEquals(s_driver, driver)) s_driver = null;
        }
    }
}
