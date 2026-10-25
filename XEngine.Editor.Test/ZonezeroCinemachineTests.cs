using System.Collections.Generic;

using XEngine.Cinemachine;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// zonezero M6 — com.xengine.cinemachine: blend math, vcam Transposer/Composer pipeline,
/// FreeLook 3-rig orbit (ZZZ reference orbit numbers), and brain vcam selection/blending.
/// </summary>
public sealed class ZonezeroCinemachineTests : EditorTestHarness
{
    [Fact]
    public void FreeLookDriver_WithoutLookInput_LeavesCameraAxesFixedAndWarnsOnce()
    {
        var cameraGo = new GameObject("FreeLook");
        var scene = new XEngine.Runtime.Resources.Scene { Name = "NoLookInput" };
        scene.Add(cameraGo);
        XEngine.Runtime.Resources.Scene.Load(scene);

        var freeLook = cameraGo.AddComponent<CinemachineFreeLook>();
        var driver = cameraGo.AddComponent<XEngine.Zonezero.ZonezeroFreeLookDriver>();
        freeLook.XAxis.Value = 23f;
        freeLook.YAxis.Value = 0.37f;

        int warningCount = 0;
        OnLog handler = (message, _, severity) =>
        {
            if (severity == LogSeverity.Warning && message.Contains("camera remains fixed"))
                warningCount++;
        };

        Debug.OnLog += handler;
        Application.IsPlaying = true;
        try
        {
            driver.Start();
            driver.Update();
            driver.Update();

            Assert.Equal(23f, freeLook.XAxis.Value);
            Assert.Equal(0.37f, freeLook.YAxis.Value);
            Assert.Equal(1, warningCount);
        }
        finally
        {
            Debug.OnLog -= handler;
            Application.IsPlaying = false;
            XEngine.Runtime.Resources.Scene.Unload();
            cameraGo.Dispose();
        }
    }

    [Fact]
    public void Blend_CurvesAndStateLerp()
    {
        Assert.Equal(1f, CinemachineBlend.Evaluate(BlendStyle.Cut, 0.25f), 5);          // cut jumps to end
        Assert.Equal(0.25f, CinemachineBlend.Evaluate(BlendStyle.Linear, 0.25f), 5);
        Assert.Equal(0.0625f, CinemachineBlend.Evaluate(BlendStyle.EaseIn, 0.25f), 5);
        Assert.Equal(0.5f, CinemachineBlend.Evaluate(BlendStyle.EaseInOut, 0.5f), 5);   // smoothstep symmetric

        var a = new CameraState { Position = new Float3(0, 0, 0), FieldOfView = 40f };
        var b = new CameraState { Position = new Float3(10, 0, 0), FieldOfView = 60f };
        CameraState mid = CinemachineBlend.Lerp(a, b, 0.5f);
        Assert.Equal(5f, mid.Position.X, 3);
        Assert.Equal(50f, mid.FieldOfView, 3);
    }

    [Fact]
    public void Transposer_PlacesCameraBehindTargetWithDamping()
    {
        var target = new GameObject("Follow");
        var cameraGo = new GameObject("Vcam");
        try
        {
            target.Transform.Position = new Float3(10f, 0f, 20f);
            var transposer = cameraGo.AddComponent<CinemachineTransposer>();
            transposer.FollowOffset = new Float3(0f, 0.14f, -3.5f);
            transposer.XDamping = 0f;
            transposer.YDamping = 0f;
            transposer.ZDamping = 0f;

            // Identity heading → offset stays object-space: camera sits behind (−Z) and above.
            Float3 position = transposer.CalculatePosition(target, 1f / 60f);
            Assert.Equal(10f, position.X, 3);
            Assert.Equal(0.14f, position.Y, 3);
            Assert.Equal(20f - 3.5f, position.Z, 3);

            // Target faces +90° yaw (forward = +X) → the −Z offset rotates to −X.
            target.Transform.LocalEulerAngles = new Float3(0f, 90f, 0f);
            Float3 rotated = transposer.CalculatePosition(target, 0f);
            Assert.Equal(10f - 3.5f, rotated.X, 2);
            Assert.Equal(20f, rotated.Z, 2);
        }
        finally
        {
            target.Dispose();
            cameraGo.Dispose();
        }
    }

    [Fact]
    public void FreeLook_OrbitPositionsMatchZzzReference()
    {
        var target = new GameObject("Player");
        var freeLookGo = new GameObject("FreeLook");
        try
        {
            target.Transform.Position = Float3.Zero;
            var freeLook = freeLookGo.AddComponent<CinemachineFreeLook>();
            freeLook.Follow = target;
            freeLook.LookAt = target;
            // ZZZ reference orbits: Top(1.47, 2.3) Middle(0.14, 3.5) Bottom(-1.1, 2.5).
            freeLook.Top = new FreeLookOrbit { Height = 1.47f, Radius = 2.3f };
            freeLook.Middle = new FreeLookOrbit { Height = 0.14f, Radius = 3.5f };
            freeLook.Bottom = new FreeLookOrbit { Height = -1.1f, Radius = 2.5f };

            // Middle rig at Y=0.5 (the reference default): height 0.14, radius 3.5, camera
            // behind the target along heading 0 → -Z.
            freeLook.YAxis.Value = 0.5f;
            CameraState middle = freeLook.CalculateState(0f);
            Assert.Equal(0.14f, middle.Position.Y, 3);
            Assert.Equal(0f, middle.Position.X, 3);
            Assert.Equal(-3.5f, middle.Position.Z, 3);

            // Top rig: height 1.47, radius 2.3.
            freeLook.YAxis.Value = 1f;
            CameraState top = freeLook.CalculateState(0f);
            Assert.Equal(1.47f, top.Position.Y, 3);
            Assert.Equal(-2.3f, top.Position.Z, 3);

            // Bottom rig: height -1.1, radius 2.5.
            freeLook.YAxis.Value = 0f;
            CameraState bottom = freeLook.CalculateState(0f);
            Assert.Equal(-1.1f, bottom.Position.Y, 3);
            Assert.Equal(-2.5f, bottom.Position.Z, 3);

            // X axis orbits around the target: +90° puts the camera to −X.
            freeLook.YAxis.Value = 0.5f;
            freeLook.XAxis.Value = 90f;
            CameraState orbited = freeLook.CalculateState(0f);
            Assert.Equal(-3.5f, orbited.Position.X, 2);
            Assert.Equal(0f, orbited.Position.Z, 2);

            // Aim keeps the target centered: forward points from camera to target.
            freeLook.XAxis.Value = 0f;
            CameraState aimed = freeLook.CalculateState(0f);
            Float3 forward = aimed.Rotation * Float3.UnitZ;
            Assert.True(forward.Z > 0.99f, $"aim forward should be +Z toward target, got {forward}");
        }
        finally
        {
            target.Dispose();
            freeLookGo.Dispose();
        }
    }

    [Fact]
    public void VirtualCamera_PipelineComposesTransposerAndComposer()
    {
        var target = new GameObject("Target");
        var vcamGo = new GameObject("Shot");
        try
        {
            target.Transform.Position = new Float3(0f, 1f, 0f);
            var vcam = vcamGo.AddComponent<CinemachineVirtualCamera>();
            vcam.Priority = 10;
            vcam.Follow = target;
            vcam.LookAt = target;
            vcam.FieldOfView = 40f;

            var transposer = vcamGo.AddComponent<CinemachineTransposer>();
            transposer.FollowOffset = new Float3(0f, 0.54f, -2.22f);
            transposer.XDamping = 0f;
            transposer.YDamping = 0f;
            transposer.ZDamping = 0f;
            vcamGo.AddComponent<CinemachineComposer>();

            Assert.True(vcam.IsLive);
            CameraState state = vcam.CalculateState(0f);
            Assert.Equal(0f, state.Position.X, 3);
            Assert.Equal(1.54f, state.Position.Y, 3);      // target Y + 0.54
            Assert.Equal(-2.22f, state.Position.Z, 3);
            Assert.Equal(40f, state.FieldOfView, 3);
            // Composer aims back at the target: +Z and pitched down (camera sits 0.54 above it).
            Float3 forward = state.Rotation * Float3.UnitZ;
            Assert.True(forward.Z > 0.9f && forward.Y < 0f, $"composer should aim +Z/down, got {forward}");
        }
        finally
        {
            target.Dispose();
            vcamGo.Dispose();
        }
    }

    [Fact]
    public void Brain_SelectsHighestPriorityAndBlendsOnSwitch()
    {
        var cameraGo = new GameObject("MainCamera");
        cameraGo.AddComponent<Camera>();
        var brain = cameraGo.AddComponent<CinemachineBrain>();
        brain.DefaultBlend = BlendDefinition.Cut();   // camera cut: snap on first application

        var target = new GameObject("Target");
        var wideGo = new GameObject("Wide");
        var closeGo = new GameObject("Close");

        // The brain scans the current scene for vcams.
        var scene = new XEngine.Runtime.Resources.Scene { Name = "CmBlendTest" };
        scene.Add(cameraGo);
        scene.Add(target);
        scene.Add(wideGo);
        scene.Add(closeGo);
        XEngine.Runtime.Resources.Scene.Load(scene);
        // Simulated frame time (no game loop in tests).
        XEngine.Runtime.Time.TimeStack.Push(new XEngine.Runtime.TimeData { DeltaTime = 1f / 60f });
        try
        {
            var wide = wideGo.AddComponent<CinemachineVirtualCamera>();
            wide.Priority = 5;
            wide.Follow = target;
            var wideTransposer = wideGo.AddComponent<CinemachineTransposer>();
            wideTransposer.FollowOffset = new Float3(0, 0, -10f);
            wideTransposer.XDamping = 0f;
            wideTransposer.YDamping = 0f;
            wideTransposer.ZDamping = 0f;
            wideGo.AddComponent<CinemachineComposer>();

            var close = closeGo.AddComponent<CinemachineVirtualCamera>();
            close.Priority = 10;                      // wins over wide
            close.Follow = target;
            var closeTransposer = closeGo.AddComponent<CinemachineTransposer>();
            closeTransposer.FollowOffset = new Float3(0, 0, -2f);
            closeTransposer.XDamping = 0f;
            closeTransposer.YDamping = 0f;
            closeTransposer.ZDamping = 0f;
            closeGo.AddComponent<CinemachineComposer>();

            // Frame 1 with a Cut blend: close (priority 10) snaps straight to its offset.
            brain.LateUpdate();
            Assert.Same(close, brain.LiveVcam);
            Assert.Equal(-2f, cameraGo.Transform.Position.Z, 3);

            // Cut the priority: wide takes over with a 1 s EaseInOut blend — mid-blend the
            // camera sits between the two offsets.
            brain.DefaultBlend = BlendDefinition.EaseInOut(1f);
            close.Priority = 1;
            for (int i = 0; i < 30; i++)               // ~0.5 s of the 1 s EaseInOut blend
                brain.LateUpdate();
            Float3 midPosition = cameraGo.Transform.Position;
            Assert.True(midPosition.Z < -2f - 0.5f && midPosition.Z > -10f + 0.5f,
                $"mid-blend position should sit between the two offsets, got {midPosition.Z}");

            // Manually push the blend to its end: repeated frames converge on wide.
            for (int i = 0; i < 240; i++)
                brain.LateUpdate();
            Assert.Same(wide, brain.LiveVcam);
            Assert.Equal(-10f, cameraGo.Transform.Position.Z, 2);
        }
        finally
        {
            XEngine.Runtime.Time.TimeStack.Clear();
            cameraGo.Dispose();
            target.Dispose();
            wideGo.Dispose();
            closeGo.Dispose();
        }
    }

    [Fact]
    public void CinemachinePackage_LayoutAndZonezeroDependency()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.cinemachine");
        Assert.True(Directory.Exists(root), "com.xengine.cinemachine package missing");
        foreach (string file in new[]
                 {
                     "Runtime/CinemachineCore.cs",
                     "Runtime/CinemachineBrain.cs",
                     "Runtime/CinemachineVirtualCamera.cs",
                     "Runtime/CinemachineFreeLook.cs",
                     "Runtime/CinemachineCollider.cs",
                 })
            Assert.True(File.Exists(Path.Combine(root, file)), $"missing {file}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent!;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
