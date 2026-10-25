// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.Browser;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

/// <summary>Serializes with <see cref="ScreenTests"/>: both mutate the global Screen state.</summary>
[Collection("ScreenState")]
public sealed class BrowserScreenOrientationTests
{
    public BrowserScreenOrientationTests()
    {
        Screen.ResetForTests();
    }

    [Theory]
    [InlineData(ScreenOrientation.Portrait, "portrait")]
    [InlineData(ScreenOrientation.PortraitUpsideDown, "portrait")]
    [InlineData(ScreenOrientation.LandscapeLeft, "landscape")]
    [InlineData(ScreenOrientation.LandscapeRight, "landscape")]
    [InlineData(ScreenOrientation.AutoRotation, "any")]
    public void Services_QueueW3COrientationLockTargets(ScreenOrientation requested, string expectedLock)
    {
        bool applied = BrowserInputRuntime.Services.TrySetScreenOrientation(
            requested,
            ScreenAutoRotatePolicy.All);

        Assert.True(applied);
        Assert.Equal(expectedLock, BrowserInputRuntime.TakePendingOrientationLock());
        Assert.Null(BrowserInputRuntime.TakePendingOrientationLock()); // drained once
    }

    [Fact]
    public void Services_RestrictedAutoRotationLocksToTheAllowedAxis()
    {
        var landscapeOnly = new ScreenAutoRotatePolicy(
            AutoRotateToPortrait: false,
            AutoRotateToPortraitUpsideDown: false,
            AutoRotateToLandscapeLeft: true,
            AutoRotateToLandscapeRight: true);

        Assert.True(BrowserInputRuntime.Services.TrySetScreenOrientation(
            ScreenOrientation.AutoRotation, landscapeOnly));
        Assert.Equal("landscape", BrowserInputRuntime.TakePendingOrientationLock());
    }

    [Fact]
    public void Services_UnsupportedHostStopsQueueingRequests()
    {
        BrowserInputRuntime.NoteScreenOrientationUnsupported();

        Assert.False(BrowserInputRuntime.Services.TrySetScreenOrientation(
            ScreenOrientation.Portrait, ScreenAutoRotatePolicy.All));
        Assert.Null(BrowserInputRuntime.TakePendingOrientationLock());
    }

    [Theory]
    [InlineData("portrait-primary", ScreenOrientation.Portrait)]
    [InlineData("portrait-secondary", ScreenOrientation.PortraitUpsideDown)]
    [InlineData("landscape-primary", ScreenOrientation.LandscapeLeft)]
    [InlineData("landscape-secondary", ScreenOrientation.LandscapeRight)]
    [InlineData("sideways", ScreenOrientation.Unknown)]
    public void HostReports_MapScreenOrientationTypeToEngineOrientation(string type, ScreenOrientation expected)
    {
        BrowserInputRuntime.ReportScreenOrientation(type);
        Assert.Equal(expected, Screen.CurrentOrientation);
    }
}
