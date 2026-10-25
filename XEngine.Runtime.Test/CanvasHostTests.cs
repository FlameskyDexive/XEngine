// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using XEngine.Runtime.Platforms.Android;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CanvasHostTests : RuntimeTestBase
{
    [Fact]
    public void EmptySceneDoesNotProduceCanvasNodes()
    {
        using var scene = CreateScene(enable: true);
        var nodes = new List<CanvasRenderNode>();

        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);

        Assert.Empty(nodes);
    }

    [Fact]
    public void CanvasRegistersSortsAndUnregistersWithSceneLifecycle()
    {
        using var scene = CreateScene(enable: false);
        var lowerGo = CreateGameObject("CanvasLower");
        var higherGo = CreateGameObject("CanvasHigher");
        var lower = lowerGo.AddComponent<GameCanvas>();
        var higher = higherGo.AddComponent<GameCanvas>();
        lower.SortOrder = -10;
        higher.SortOrder = 10;

        scene.Add(lowerGo);
        scene.Add(higherGo);
        scene.Enable();

        IReadOnlyList<GameCanvas> canvases = CanvasRegistry.GetCanvases(scene);
        Assert.Equal(2, canvases.Count);
        Assert.Same(lower, canvases[0]);
        Assert.Same(higher, canvases[1]);

        higherGo.Enabled = false;
        var nodes = new List<CanvasRenderNode>();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
        Assert.Empty(nodes);

        lowerGo.Enabled = false;
        Assert.Empty(CanvasRegistry.GetCanvases(scene));
    }

    [Fact]
    public void CanvasRenderNodeWithoutPayloadIsNotRenderable()
    {
        var node = new CanvasRenderNode();
        node.GetCullingData(out bool renderable, out _);
        Assert.False(renderable);
        Assert.False(node.IsAttached);
        Assert.Equal(UISurface.World, node.Surface);
    }

    [Fact]
    public void ClipStateAndDirtyFlagsRemainBackendNeutral()
    {
        Float4x4 matrix = Float4x4.Identity;
        var clip = CanvasClipState.Rect(matrix, new Float4(-1, -2, 3, 4), 5, 0.25f);
        Assert.True(clip.Enabled);
        Assert.False(clip.UsesStencil);
        Assert.Equal(5, clip.CornerRadius);
        Assert.Equal(CanvasDirtyFlags.All,
            CanvasDirtyFlags.Transform | CanvasDirtyFlags.Vertices | CanvasDirtyFlags.Material |
            CanvasDirtyFlags.Clip | CanvasDirtyFlags.Hierarchy);
    }

    [Fact]
    public void RoundedClipRejectsPointsOutsideCornerArcs()
    {
        Float4x4 matrix = Float4x4.Identity;
        var clip = CanvasClipState.Rect(matrix, new Float4(0, 0, 100, 100), 20f);

        Assert.True(clip.ContainsLocalPoint(new Float2(50, 50)));
        Assert.True(clip.ContainsLocalPoint(new Float2(10, 20)));
        Assert.False(clip.ContainsLocalPoint(new Float2(2, 2)));
    }

    [Fact]
    public void SharpClipKeepsRectangleEdgesAndStencilDoesNotCpuReject()
    {
        var sharp = CanvasClipState.Rect(Float4x4.Identity, new Float4(-2, -3, 4, 5));
        Assert.True(sharp.ContainsLocalPoint(new Float2(-2, -3)));
        Assert.False(sharp.ContainsLocalPoint(new Float2(4.01f, 0)));

        var stencil = CanvasClipState.Stencil(1);
        Assert.True(stencil.ContainsLocalPoint(new Float2(float.MaxValue, float.MaxValue)));
    }

    [Fact]
    public void StencilWriterRetainsParentTestAndOptionalRectClip()
    {
        CanvasClipState rect = CanvasClipState.Rect(Float4x4.Identity, new Float4(0, 0, 100, 100));
        CanvasClipState writer = CanvasClipState.StencilWrite(2, 1, suppressColor: true, rect);

        Assert.True(writer.UsesStencil);
        Assert.True(writer.IsStencilWrite);
        Assert.True(writer.SuppressStencilWriterColor);
        Assert.True(writer.HasRect);
        Assert.Equal(2u, writer.StencilDepth);
        Assert.Equal(1u, writer.StencilReference);
        Assert.True(writer.ContainsLocalPoint(new Float2(50, 50)));
        Assert.False(writer.ContainsLocalPoint(new Float2(101, 50)));
    }

    [Fact]
    public void CanvasBuildsNestedStencilWriterAndTestStates()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();

        GameObject outer = CreateStencilMaskGraphic("OuterMask", canvasObject, showGraphic: false);
        GameObject outerChild = CreateStencilGraphic("OuterChild", outer);
        GameObject inner = CreateStencilMaskGraphic("InnerMask", outer, showGraphic: true);
        GameObject innerChild = CreateStencilGraphic("InnerChild", inner);

        scene.Add(canvasObject);
        scene.Enable();

        var nodes = new List<CanvasRenderNode>();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);

        nodes.Sort(static (left, right) => left.SortKey.CompareTo(right.SortKey));
        Assert.Equal(6, nodes.Count);
        Assert.True(nodes[0].Clip.IsStencilWrite);
        Assert.True(nodes[0].Clip.SuppressStencilWriterColor);
        Assert.Equal(1u, nodes[0].Clip.StencilDepth);
        Assert.Equal(0u, nodes[0].Clip.StencilReference);

        Assert.True(nodes[1].Clip.UsesStencil);
        Assert.False(nodes[1].Clip.IsStencilWrite);
        Assert.Equal(1u, nodes[1].Clip.StencilDepth);

        Assert.True(nodes[2].Clip.IsStencilWrite);
        Assert.False(nodes[2].Clip.SuppressStencilWriterColor);
        Assert.Equal(2u, nodes[2].Clip.StencilDepth);
        Assert.Equal(1u, nodes[2].Clip.StencilReference);

        Assert.True(nodes[3].Clip.UsesStencil);
        Assert.False(nodes[3].Clip.IsStencilWrite);
        Assert.Equal(2u, nodes[3].Clip.StencilDepth);
        Assert.Equal(2u, nodes[3].Clip.StencilReference);
        Assert.True(nodes[4].Clip.IsStencilUnwind);
        Assert.Equal(2u, nodes[4].Clip.StencilReference);
        Assert.True(nodes[5].Clip.IsStencilUnwind);
        Assert.Equal(1u, nodes[5].Clip.StencilReference);

        _ = outerChild;
        _ = innerChild;
    }

    [Fact]
    public void DisablingStencilMaskRestoresUnclippedChildSubmission()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject maskObject = CreateStencilMaskGraphic("Mask", canvasObject, showGraphic: true);
        StencilMaskProbe mask = maskObject.GetComponent<StencilMaskProbe>()!;
        _ = CreateStencilGraphic("Child", maskObject);

        scene.Add(canvasObject);
        scene.Enable();

        var nodes = new List<CanvasRenderNode>();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
        nodes.Sort(static (left, right) => left.SortKey.CompareTo(right.SortKey));
        Assert.Equal(3, nodes.Count);
        Assert.True(nodes[1].Clip.UsesStencil);
        Assert.True(nodes[2].Clip.IsStencilUnwind);

        mask.Enabled = false;
        canvas.MarkDirty(UIDirtyFlags.Hierarchy);
        nodes.Clear();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);

        nodes.Sort(static (left, right) => left.SortKey.CompareTo(right.SortKey));
        Assert.Equal(2, nodes.Count);
        Assert.False(nodes[0].Clip.UsesStencil);
        Assert.False(nodes[1].Clip.UsesStencil);
    }

    [Fact]
    public void RegistryResetRemovesAllCanvasHosts()
    {
        using var scene = CreateScene(enable: false);
        var go = CreateGameObject("Canvas");
        go.AddComponent<GameCanvas>();
        scene.Add(go);
        scene.Enable();
        Assert.Single(CanvasRegistry.GetCanvases(scene));

        CanvasRegistry.Reset();

        Assert.Empty(CanvasRegistry.GetCanvases(scene));
    }

    [Fact]
    public void CanvasUpdateDriverSurvivesSceneRegistryResetAndReceivesOrderedStages()
    {
        var phases = new List<CanvasUpdatePhase>();
        using var registration = CanvasUpdateDispatcher.Register(new RecordingCanvasUpdateDriver(phases));
        Assert.True(CanvasUpdateDispatcher.IsRegistered);

        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        scene.Add(canvasObject);
        scene.Enable();

        canvas.RebuildIfDirty();
        AssertOrderedStages(phases);

        CanvasRegistry.Reset();
        Assert.True(CanvasUpdateDispatcher.IsRegistered);

        phases.Clear();
        canvas.MarkDirty(UIDirtyFlags.Layout);
        canvas.RebuildIfDirty();
        AssertOrderedStages(phases);
    }

    [Fact]
    public void NonConvergedLayoutDefersItsRetryUntilTheNextFrame()
    {
        var phases = new List<CanvasUpdatePhase>();
        using var registration = CanvasUpdateDispatcher.Register(new RecordingCanvasUpdateDriver(phases));
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject child = CreateGameObject("OscillatingChild");
        child.SetParent(canvasObject, worldPositionStays: false);
        child.EnsureRectTransform();
        child.AddComponent<OscillatingLayout>();
        scene.Add(canvasObject);
        scene.Enable();

        canvas.RebuildIfDirty();
        int phaseCountAfterFirstAttempt = phases.Count;
        AssertOrderedStages(phases);

        canvas.RebuildIfDirty();
        Assert.Equal(phaseCountAfterFirstAttempt, phases.Count);

        Time.CurrentTime.FrameCount++;
        canvas.RebuildIfDirty();
        Assert.True(phases.Count > phaseCountAfterFirstAttempt);
    }

    [Fact]
    public void NonConvergedLayoutReportsItsBoundToAnOptionalDiagnosticsDriver()
    {
        var driver = new RecordingLayoutDiagnosticsDriver();
        using var registration = CanvasUpdateDispatcher.Register(driver);
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject child = CreateGameObject("OscillatingChild");
        child.SetParent(canvasObject, worldPositionStays: false);
        child.EnsureRectTransform();
        child.AddComponent<OscillatingLayout>();
        scene.Add(canvasObject);
        scene.Enable();

        canvas.RebuildIfDirty();

        Assert.Same(canvas, driver.Canvas);
        Assert.Equal(2, driver.PassCount);
    }

    [Fact]
    public void DirtyLayoutRebuildsOnlyThePackageReportedHighestRoot()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject root = CreateGameObject("DirtyRoot");
        root.SetParent(canvasObject, worldPositionStays: false);
        root.EnsureRectTransform();
        LayoutProbe rootProbe = root.AddComponent<LayoutProbe>();
        GameObject child = CreateGameObject("DirtyChild");
        child.SetParent(root, worldPositionStays: false);
        child.EnsureRectTransform();
        LayoutProbe childProbe = child.AddComponent<LayoutProbe>();
        GameObject unrelated = CreateGameObject("Unrelated");
        unrelated.SetParent(canvasObject, worldPositionStays: false);
        unrelated.EnsureRectTransform();
        LayoutProbe unrelatedProbe = unrelated.AddComponent<LayoutProbe>();

        scene.Add(canvasObject);
        scene.Enable();

        var driver = new LayoutRootDriver(root);
        using var registration = CanvasUpdateDispatcher.Register(driver);

        // Hierarchy invalidation on the first build deliberately uses the full tree.
        canvas.RebuildIfDirty();
        Assert.True(unrelatedProbe.ApplyCount > 0);

        rootProbe.Reset();
        childProbe.Reset();
        unrelatedProbe.Reset();
        canvas.MarkDirty(UIDirtyFlags.Layout);
        canvas.RebuildIfDirty();

        Assert.Equal(1, rootProbe.ApplyCount);
        Assert.Equal(1, childProbe.ApplyCount);
        Assert.Equal(0, unrelatedProbe.ApplyCount);
        Assert.Equal(1, driver.RequestCount);
    }

    [Fact]
    public void ParentLayoutGroupStillRunsItsChildSelfLayoutControllers()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject parent = CreateGameObject("LayoutParent");
        parent.SetParent(canvasObject, worldPositionStays: false);
        parent.EnsureRectTransform();
        GameObject child = CreateGameObject("FittedChild");
        child.SetParent(parent, worldPositionStays: false);
        child.EnsureRectTransform();

        ParentArranger arranger = parent.AddComponent<ParentArranger>();
        arranger.Child = child;
        LayoutProbe selfController = child.AddComponent<LayoutProbe>();

        scene.Add(canvasObject);
        scene.Enable();

        canvas.RebuildIfDirty();

        Assert.True(selfController.ApplyCount > 0);
        Assert.Equal(new Rect(10f, 20f, 40f, 60f), child.RectTransform!.ComputedRect);
    }

    [Fact]
    public void LegacyGraphicIsRegisteredAsRaycastCandidate()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        UIImage image = imageObject.AddComponent<UIImage>();

        scene.Add(canvasObject);
        scene.Enable();
        canvas.RebuildIfDirty();

        IReadOnlyList<UIBehaviour> candidates = UIRaycastRegistry.GetCandidates(canvas);
        Assert.Contains(image, candidates);
        Assert.True(UIRaycastRegistry.TryGetState(image, out long sortKey, out _, out bool blocks, out _));
        Assert.True(blocks);
        Assert.NotEqual(long.MinValue, sortKey);
    }

    [Fact]
    public void CanvasGroupBlocksRaycastsIsAppliedToRegisteredGraphic()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        UIImage image = imageObject.AddComponent<UIImage>();
        CanvasGroup group = imageObject.AddComponent<CanvasGroup>();
        group.BlocksRaycasts = false;

        scene.Add(canvasObject);
        scene.Enable();
        canvas.RebuildIfDirty();

        Assert.True(UIRaycastRegistry.TryGetState(image, out _, out _, out bool blocks, out _));
        Assert.False(blocks);
    }

    [Fact]
    public void CanvasGroupAlphaUpdatesOnlyDescendantPropertiesWithoutRebuildingMeshes()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();

        GameObject groupObject = CreateGameObject("Group");
        groupObject.SetParent(canvasObject, worldPositionStays: false);
        groupObject.EnsureRectTransform();
        CanvasGroup group = groupObject.AddComponent<CanvasGroup>();

        GameObject hostObject = CreateGameObject("HostGraphic");
        hostObject.SetParent(groupObject, worldPositionStays: false);
        hostObject.EnsureRectTransform();
        AlphaProbeGraphic hostGraphic = hostObject.AddComponent<AlphaProbeGraphic>();
        CanvasRenderer hostRenderer = hostObject.AddComponent<CanvasRenderer>();
        hostRenderer.SetOwner(hostGraphic);

        GameObject legacyObject = CreateGameObject("LegacyGraphic");
        legacyObject.SetParent(groupObject, worldPositionStays: false);
        legacyObject.EnsureRectTransform();
        AlphaProbeGraphic legacyGraphic = legacyObject.AddComponent<AlphaProbeGraphic>();

        GameObject unrelatedObject = CreateGameObject("UnrelatedGraphic");
        unrelatedObject.SetParent(canvasObject, worldPositionStays: false);
        unrelatedObject.EnsureRectTransform();
        AlphaProbeGraphic unrelatedGraphic = unrelatedObject.AddComponent<AlphaProbeGraphic>();

        scene.Add(canvasObject);
        scene.Enable();

        var nodes = new List<CanvasRenderNode>();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
        Mesh hostMesh = Assert.IsType<Mesh>(hostGraphic.CachedMesh);
        Mesh legacyMesh = Assert.IsType<Mesh>(legacyGraphic.CachedMesh);
        Mesh unrelatedMesh = Assert.IsType<Mesh>(unrelatedGraphic.CachedMesh);
        Assert.Equal(1, hostGraphic.GenerateCount);
        Assert.Equal(1, legacyGraphic.GenerateCount);
        Assert.Equal(1, unrelatedGraphic.GenerateCount);

        group.Alpha = 0.35f;
        RenderStats.BeginFrame();
        nodes.Clear();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
        RenderStats.EndFrame();

        Assert.Equal(0, RenderStats.Last.GraphicRebuilds);
        Assert.Equal(0, RenderStats.Last.MeshUploads);
        Assert.Same(hostMesh, hostGraphic.CachedMesh);
        Assert.Same(legacyMesh, legacyGraphic.CachedMesh);
        Assert.Same(unrelatedMesh, unrelatedGraphic.CachedMesh);
        Assert.Equal(1, hostGraphic.GenerateCount);
        Assert.Equal(1, legacyGraphic.GenerateCount);
        Assert.Equal(1, unrelatedGraphic.GenerateCount);

        AssertAlpha(nodes, hostMesh, 0.35f);
        AssertAlpha(nodes, legacyMesh, 0.35f);
        AssertAlpha(nodes, unrelatedMesh, 1f);
    }

    [Fact]
    public void RectMaskPublishesCoarseRaycastClip()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject maskObject = CreateGameObject("Mask");
        maskObject.SetParent(canvasObject, worldPositionStays: false);
        maskObject.EnsureRectTransform();
        RectTransform maskRect = maskObject.RectTransform!;
        maskRect.SizeDelta = new Float2(100f, 100f);
        RectMask mask = maskObject.AddComponent<RectMask>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(maskObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        UIImage image = imageObject.AddComponent<UIImage>();

        scene.Add(canvasObject);
        scene.Enable();
        canvas.RebuildIfDirty();

        Assert.True(UIRaycastRegistry.TryGetState(image, out _, out _, out _, out Rect? coarse));
        Assert.True(coarse.HasValue);
        Assert.Equal(mask.GetClipRectInCanvasPixels(), coarse.Value);
    }

    [Fact]
    public void RaycasterUsesRegisteredRenderOrderAndBlocksRaycasts()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject bottomObject = CreateGameObject("Bottom");
        GameObject topObject = CreateGameObject("Top");
        bottomObject.SetParent(canvasObject, worldPositionStays: false);
        topObject.SetParent(canvasObject, worldPositionStays: false);
        bottomObject.EnsureRectTransform();
        topObject.EnsureRectTransform();
        bottomObject.AddComponent<UIImage>();
        UIImage top = topObject.AddComponent<UIImage>();

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            Assert.True(UIRaycaster.TryPick(scene, new Float2(50f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit hit));
            Assert.Same(topObject, hit.GameObject);

            CanvasGroup group = topObject.AddComponent<CanvasGroup>();
            group.BlocksRaycasts = false;
            Assert.True(UIRaycaster.TryPick(scene, new Float2(50f, 50f), new Float2(100f, 100f), out hit));
            Assert.Same(bottomObject, hit.GameObject);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void LegacySelectableWithoutGraphicRetainsRectangularHitArea()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject buttonObject = CreateGameObject("Button");
        buttonObject.SetParent(canvasObject, worldPositionStays: false);
        buttonObject.EnsureRectTransform();
        buttonObject.AddComponent<UIButton>();

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            Assert.True(UIRaycaster.TryPick(scene, new Float2(50f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit hit));
            Assert.Same(buttonObject, hit.GameObject);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void TouchPointersDispatchIndependentDragCancelAndClickLifecycles()
    {
        using var scene = CreateScene(enable: false);
        GameObject eventObject = CreateGameObject("EventSystem");
        EventSystem system = eventObject.AddComponent<EventSystem>();
        system.Viewport = new EventSystem.HostViewport
        {
            ReferenceSize = new Float2(100f, 100f),
            PointerPosition = Float2.Zero,
            ReceivesInput = true,
        };

        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject leftObject = CreatePointerTarget("Left", canvasObject, new Float2(25f, 50f));
        GameObject rightObject = CreatePointerTarget("Right", canvasObject, new Float2(75f, 50f));
        PointerLifecycleProbe left = leftObject.AddComponent<PointerLifecycleProbe>();
        PointerLifecycleProbe right = rightObject.AddComponent<PointerLifecycleProbe>();

        scene.Add(eventObject);
        scene.Add(canvasObject);

        var input = new AndroidInputHandler();
        Scene.Load(scene);
        global::XEngine.Runtime.Input.PushHandler(input);
        try
        {
            GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
            GameCanvas canvas = canvasObject.GetComponent<GameCanvas>()!;
            canvas.RebuildIfDirty();
            Assert.True(UIRaycaster.TryPick(scene, new Float2(25f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit leftHit));
            Assert.Same(leftObject, leftHit.GameObject);
            Assert.True(UIRaycaster.TryPick(scene, new Float2(75f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit rightHit));
            Assert.Same(rightObject, rightHit.GameObject);

            input.TouchBegan(11, 25f, 50f, 1f);
            input.TouchBegan(22, 75f, 50f, 1f);
            Assert.IsAssignableFrom<ITouchInputHandler>(global::XEngine.Runtime.Input.Current);
            Assert.Equal(2, global::XEngine.Runtime.Input.TouchCount);
            Assert.Equal(11, global::XEngine.Runtime.Input.GetTouch(0).PointerId);
            Assert.Equal(22, global::XEngine.Runtime.Input.GetTouch(1).PointerId);
            Update(scene);

            Assert.Equal([11], left.DownPointerIds);
            Assert.Equal([22], right.DownPointerIds);

            input.EndFrame();
            input.TouchMoved(11, 38f, 50f, 1f);
            Update(scene);
            input.TouchMoved(11, 42f, 50f, 1f);
            Update(scene);

            Assert.Equal(1, left.BeginDragCount);
            Assert.Equal(1, left.DragCount);
            Assert.Equal(0, right.BeginDragCount);
            Assert.Equal(0, right.DragCount);

            input.TouchEnded(11, 42f, 50f, 1f, canceled: true);
            input.TouchEnded(22, 75f, 50f, 1f);
            Update(scene);

            Assert.Equal([11], left.UpPointerIds);
            Assert.Equal(1, left.EndDragCount);
            Assert.Equal(0, left.ClickCount);
            Assert.Equal([22], right.UpPointerIds);
            Assert.Equal(1, right.ClickCount);
            Assert.Equal(0, right.EndDragCount);
        }
        finally
        {
            global::XEngine.Runtime.Input.PopHandler();
            GameCanvas.ScreenSizeOverride = null;
            Scene.Unload();
        }
    }

    [Fact]
    public void RaycasterSkipsTopGraphicWhenRaycastsAreDisabled()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject bottomObject = CreateGameObject("Bottom");
        GameObject topObject = CreateGameObject("Top");
        bottomObject.SetParent(canvasObject, worldPositionStays: false);
        topObject.SetParent(canvasObject, worldPositionStays: false);
        bottomObject.EnsureRectTransform();
        topObject.EnsureRectTransform();
        UIImage bottom = bottomObject.AddComponent<UIImage>();
        UIImage top = topObject.AddComponent<UIImage>();
        top.RaycastTarget = false;

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            Assert.True(UIRaycaster.TryPick(scene, new Float2(50f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit hit));
            Assert.Same(bottomObject, hit.GameObject);

            GameCanvas canvas = canvasObject.GetComponent<GameCanvas>()!;
            IReadOnlyList<UIBehaviour> candidates = UIRaycastRegistry.GetCandidates(canvas);
            Assert.Equal(bottom, candidates[0]);
            Assert.Equal(top, candidates[1]);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void RaycastProviderCanBeInstalledWithoutReplacingLegacyCandidates()
    {
        using var scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject legacyObject = CreateGameObject("Legacy");
        legacyObject.SetParent(canvasObject, worldPositionStays: false);
        legacyObject.EnsureRectTransform();
        UIImage legacy = legacyObject.AddComponent<UIImage>();
        GameObject packageObject = CreateGameObject("Package");
        packageObject.SetParent(canvasObject, worldPositionStays: false);
        packageObject.EnsureRectTransform();
        ProbeRaycastGraphic packageGraphic = packageObject.AddComponent<ProbeRaycastGraphic>();

        scene.Add(canvasObject);
        scene.Enable();
        canvas.RebuildIfDirty();

        var provider = new TestRaycastProvider(canvas, packageGraphic);
        UIRaycastProviderRegistration.Register(provider);
        try
        {
            IReadOnlyList<UIBehaviour> candidates = GetCandidates(canvas);
            Assert.Equal(2, candidates.Count);
            Assert.Same(legacy, candidates[0]);
            Assert.Same(packageGraphic, candidates[1]);

            Assert.True(UIRaycastRegistry.TryGetState(provider.PackageCandidate, out long sortKey,
                out _, out bool blocks, out _));
            Assert.Equal(TestRaycastProvider.PackageSortKey, sortKey);
            Assert.True(blocks);
        }
        finally
        {
            UIRaycastProviderRegistration.Unregister(provider);
        }
    }

    private static IReadOnlyList<UIBehaviour> GetCandidates(GameCanvas canvas)
    {
        // The test lives in the Runtime assembly's friend test project and intentionally checks
        // the same bridge consumed by UIRaycaster.
        return UIRaycastRegistry.GetCandidates(canvas);
    }

    private sealed class TestRaycastProvider(GameCanvas canvas, UIBehaviour packageCandidate) : IUIRaycastProvider
    {
        public const long PackageSortKey = long.MaxValue - 1;
        public UIBehaviour PackageCandidate => packageCandidate;

        public IReadOnlyList<UIBehaviour> GetRaycastCandidates(GameCanvas requestedCanvas)
            => ReferenceEquals(canvas, requestedCanvas) ? new[] { packageCandidate } : Array.Empty<UIBehaviour>();

        public bool TryGetRaycastState(UIBehaviour element, out long sortKey, out CanvasClipState clip,
            out bool blocksRaycasts, out Rect? raycastRect)
        {
            if (ReferenceEquals(element, packageCandidate))
            {
                sortKey = PackageSortKey;
                clip = CanvasClipState.None;
                blocksRaycasts = true;
                raycastRect = null;
                return true;
            }
            sortKey = long.MinValue;
            clip = CanvasClipState.None;
            blocksRaycasts = true;
            raycastRect = null;
            return false;
        }
    }

    private sealed class ProbeRaycastGraphic : UIBehaviour
    {
        public override void GenerateMesh(UIMeshBuilder _, in UIContext __) { }
    }

    private static void AssertOrderedStages(List<CanvasUpdatePhase> phases)
    {
        Assert.True(phases.Count >= 6);
        Assert.Equal(CanvasUpdatePhase.PreLayout, phases[0]);
        Assert.Equal(CanvasUpdatePhase.ClipUpdate, phases[^3]);
        Assert.Equal(CanvasUpdatePhase.GraphicRebuild, phases[^2]);
        Assert.Equal(CanvasUpdatePhase.MaterialUpdate, phases[^1]);

        for (int i = 1; i < phases.Count - 3; i += 2)
        {
            Assert.Equal(CanvasUpdatePhase.LayoutCalculation, phases[i]);
            Assert.Equal(CanvasUpdatePhase.LayoutControl, phases[i + 1]);
        }
    }

    private GameObject CreateStencilMaskGraphic(string name, GameObject parent, bool showGraphic)
    {
        GameObject gameObject = CreateStencilGraphic(name, parent);
        StencilMaskProbe mask = gameObject.AddComponent<StencilMaskProbe>();
        mask.ShowMaskGraphic = showGraphic;
        return gameObject;
    }

    private GameObject CreatePointerTarget(string name, GameObject parent, Float2 position)
    {
        GameObject target = CreateGameObject(name);
        target.SetParent(parent, worldPositionStays: false);
        target.EnsureRectTransform();
        RectTransform rt = target.RectTransform!;
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.Zero;
        rt.Pivot = new Float2(0.5f, 0.5f);
        rt.SizeDelta = new Float2(50f, 100f);
        rt.AnchoredPosition = position;
        target.AddComponent<UIImage>();
        return target;
    }

    private sealed class PointerLifecycleProbe : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerClickHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public readonly List<int> DownPointerIds = new();
        public readonly List<int> UpPointerIds = new();
        public int ClickCount;
        public int BeginDragCount;
        public int DragCount;
        public int EndDragCount;

        public void OnPointerDown(PointerEventData e) => DownPointerIds.Add(e.PointerId);
        public void OnPointerUp(PointerEventData e) => UpPointerIds.Add(e.PointerId);
        public void OnPointerClick(PointerEventData _) => ClickCount++;
        public void OnBeginDrag(PointerEventData _) => BeginDragCount++;
        public void OnDrag(PointerEventData _) => DragCount++;
        public void OnEndDrag(PointerEventData _) => EndDragCount++;
    }

    private GameObject CreateStencilGraphic(string name, GameObject parent)
    {
        GameObject gameObject = CreateGameObject(name);
        gameObject.SetParent(parent, worldPositionStays: false);
        gameObject.EnsureRectTransform();
        gameObject.AddComponent<UIImage>();
        gameObject.AddComponent<CanvasRenderer>();
        return gameObject;
    }

    private sealed class RecordingCanvasUpdateDriver(List<CanvasUpdatePhase> phases) : ICanvasUpdateDriver
    {
        public void Execute(GameCanvas _, CanvasUpdatePhase phase) => phases.Add(phase);
    }

    private sealed class RecordingLayoutDiagnosticsDriver : ICanvasUpdateDriver, ICanvasLayoutDiagnosticDriver
    {
        public GameCanvas? Canvas { get; private set; }
        public int PassCount { get; private set; }

        public void Execute(GameCanvas _, CanvasUpdatePhase __) { }

        public void ReportNonConvergentLayout(GameCanvas canvas, int passCount)
        {
            Canvas = canvas;
            PassCount = passCount;
        }
    }

    private sealed class LayoutRootDriver(GameObject root) : ICanvasUpdateDriver, ICanvasLayoutRootProvider
    {
        public int RequestCount { get; private set; }

        public void Execute(GameCanvas _, CanvasUpdatePhase __) { }

        public void CollectLayoutRoots(GameCanvas _, List<GameObject> destination)
        {
            RequestCount++;
            destination.Add(root);
        }
    }

    private sealed class LayoutProbe : UIBehaviour
    {
        public int ApplyCount { get; private set; }

        public override void GenerateMesh(UIMeshBuilder _, in UIContext __) { }

        public override void ApplyLayout(in Rect _)
        {
            ApplyCount++;
        }

        public void Reset() => ApplyCount = 0;
    }

    private sealed class ParentArranger : UIBehaviour, ILayoutGroupHost
    {
        public GameObject? Child { get; set; }
        public bool IgnoreLayout => false;
        public float MinWidth => 0f;
        public float PreferredWidth => 0f;
        public float FlexibleWidth => 0f;
        public float MinHeight => 0f;
        public float PreferredHeight => 0f;
        public float FlexibleHeight => 0f;

        public override void GenerateMesh(UIMeshBuilder _, in UIContext __) { }

        public void Arrange(Rect _)
        {
            if (Child?.RectTransform is { } rect)
                rect.ComputedRect = new Rect(10f, 20f, 40f, 60f);
        }
    }

    private sealed class OscillatingLayout : UIBehaviour
    {
        private bool _large;

        public override void GenerateMesh(UIMeshBuilder _, in UIContext __) { }

        public override void ApplyLayout(in Rect _)
        {
            _large = !_large;
            GameObject.RectTransform!.SizeDelta = new Float2(_large ? 101f : 100f, 100f);
        }
    }

    private sealed class StencilMaskProbe : UIBehaviour, ICanvasStencilMask
    {
        public bool ShowMaskGraphic { get; set; }

        public override bool IsRaycastCandidate => false;

        public override void GenerateMesh(UIMeshBuilder _, in UIContext __) { }
    }

    private static void AssertAlpha(List<CanvasRenderNode> nodes, Mesh mesh, float expectedAlpha)
    {
        CanvasRenderNode? node = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (ReferenceEquals(nodes[i].Mesh, mesh))
            {
                node = nodes[i];
                break;
            }
        }

        CanvasRenderNode renderNode = Assert.IsType<CanvasRenderNode>(node);
        renderNode.GetRenderingData(default, out PropertyState properties, out _, out _, out _);
        Assert.Equal(expectedAlpha, properties.GetColor("_MainColor").A, 3);
    }

    private sealed class AlphaProbeGraphic : UIBehaviour
    {
        public int GenerateCount { get; private set; }
        public override bool IsGraphic => true;

        public override void GenerateMesh(UIMeshBuilder builder, in UIContext _)
        {
            GenerateCount++;
            builder.AddQuad(new XEngine.Vector.Rect(-8f, -8f, 8f, 8f), Color.White,
                Float2.Zero, Float2.One);
        }

        public override void PopulateProperties(PropertyState properties, in UIContext context)
            => properties.SetColor("_MainColor", new Color(1f, 1f, 1f, context.Alpha));
    }
}
