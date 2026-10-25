// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// UIKit host sources for the generated <c>net10.0-ios</c> player. Microsoft.iOS types are legal
/// here; they must not appear in XEngine.Runtime.
/// </summary>
internal static class IosManagedPlayerSource
{
    internal static string CreatePlayer(IosPlayerProjectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string label = JsonSerializer.Serialize(options.ApplicationLabel);
        string fps = options.PreferredFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        string expectedSceneGuid = options.DefaultSceneGuid.ToString("D");
        string packagingMode = options.PackagingMode.ToString();
        string audioInitialization = options.RuntimeIdentifier == "iossimulator-arm64"
            ? "Console.WriteLine(\"[iOS] Simulator audio initialization skipped; validate Core Audio on a physical device.\");"
            : "_host.InitializeAudio(44100, 2048);";
        return $$"""
            using System;

            using CoreAnimation;
            using Foundation;
            using AVFoundation;
            using GameController;
            using UIKit;

            using System.Runtime.InteropServices;

            using XEngine.Runtime;
            using XEngine.Runtime.Platforms.iOS;
            using XEngine.Runtime.UI;

            namespace XEngine.Generated.iOS;

            public static class Program
            {
                public static void Main(string[] args)
                {
                    RegisterMiniAudioResolver();
                    UIApplication.Main(args, null, typeof(AppDelegate));
                }

                private static void RegisterMiniAudioResolver()
                {
                    NativeLibrary.SetDllImportResolver(typeof(EngineObject).Assembly, (libraryName, _, _) =>
                    {
                        if (libraryName != "miniaudioex")
                            return IntPtr.Zero;
                        string fileName = "libminiaudioex.dylib";
                        string[] candidates =
                        [
                            Path.Combine(NSBundle.MainBundle.BundlePath, fileName),
                            Path.Combine(NSBundle.MainBundle.ResourcePath ?? NSBundle.MainBundle.BundlePath, fileName),
                            Path.Combine(AppContext.BaseDirectory, fileName),
                        ];
                        for (int i = 0; i < candidates.Length; i++)
                        {
                            if (File.Exists(candidates[i]))
                                return NativeLibrary.Load(candidates[i]);
                        }
                        return IntPtr.Zero;
                    });
                }
            }

            [Register("AppDelegate")]
            public sealed class AppDelegate : UIApplicationDelegate
            {
                public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
                {
                    XEnginePlayerRoots.Preserve();
                    Application.ConfigureRuntimePlatform(ApplicationRuntimePlatform.iOS);
                    Application.IsEditor = false;
                    Application.IsPlayerBuild = true;
                    Application.IsPlaying = true;
                    return true;
                }
            }

            [Register("SceneDelegate")]
            public sealed class SceneDelegate : UIWindowSceneDelegate
            {
                public override UIWindow? Window { get; set; }

                public override void WillConnect(
                    UIScene scene,
                    UISceneSession session,
                    UISceneConnectionOptions connectionOptions)
                {
                    if (scene is not UIWindowScene windowScene)
                    {
                        Debug.LogError("[iOS] The application scene is not a UIWindowScene.");
                        return;
                    }

                    Window = new UIWindow(windowScene)
                    {
                        RootViewController = new GameViewController(),
                    };
                    Window.MakeKeyAndVisible();
                }
            }

            public sealed class GameViewController : UIViewController
            {
                private IosPlatformHost? _host;
                private IosPlayerGame? _game;
                private CADisplayLink? _displayLink;
                private UITextField? _imeField;
                private IDisposable? _notifications;
                private UIImpactFeedbackGenerator? _haptics;
                private ScreenOrientation _requestedOrientation = ScreenOrientation.Unknown;
                private ScreenAutoRotatePolicy _autoRotatePolicy = ScreenAutoRotatePolicy.All;

                public override void LoadView()
                {
                    View = new MetalGameView();
                }

                public override void ViewDidLoad()
                {
                    base.ViewDidLoad();
                    var view = (MetalGameView)View!;
                    view.MultipleTouchEnabled = true;
                    view.ConfigureMetalLayer();

                    ConfigureSandbox();
                    IosAssetHost.BindPlayerIfConfigured(
                        AssetPackagingMode.{{packagingMode}},
                        new Guid("{{expectedSceneGuid}}"));
                    ConfigureAudioSession();

                    _haptics = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);
                    _imeField = CreateImeField();
                    view.AddSubview(_imeField);

                    _host = new IosPlatformHost(
                        clipboardGetter: ReadClipboard,
                        clipboardSetter: WriteClipboard,
                        softKeyboard: SetSoftKeyboardVisible,
                        platformServices: CreatePlatformServices());
                    _host.PreferredFramesPerSecond = {{fps}};
                    view.Host = _host;
                    _game = new IosPlayerGame();
                    _game.InitializePlatformHost({{label}}, 1, 1);

                    int width = Math.Max(1, (int)view.DrawableSize.Width);
                    int height = Math.Max(1, (int)view.DrawableSize.Height);
                    _host.AttachLayer(view.MetalLayerHandle, width, height);
                    PublishViewMetrics(view);
                    _notifications = SubscribeLifecycle();
                    SyncConnectedGamepads();
                    {{audioInitialization}}
                    _host.Resume();
                    StartDisplayLink();
                }

                public override void ViewDidLayoutSubviews()
                {
                    base.ViewDidLayoutSubviews();
                    if (_host == null || View is not MetalGameView view)
                        return;
                    view.ConfigureMetalLayer();
                    _host.ResizeLayer(
                        Math.Max(1, (int)view.DrawableSize.Width),
                        Math.Max(1, (int)view.DrawableSize.Height));
                    PublishViewMetrics(view);
                }

                public override void ViewWillAppear(bool animated)
                {
                    base.ViewWillAppear(animated);
                    _host?.Resume();
                    StartDisplayLink();
                }

                public override void ViewDidDisappear(bool animated)
                {
                    StopDisplayLink();
                    _host?.Suspend();
                    base.ViewDidDisappear(animated);
                }

                public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
                {
                    if (!DispatchPresses(presses, pressed: true))
                        base.PressesBegan(presses, evt);
                }

                public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
                {
                    if (!DispatchPresses(presses, pressed: false))
                        base.PressesEnded(presses, evt);
                }

                protected override void Dispose(bool disposing)
                {
                    if (disposing)
                    {
                        StopDisplayLink();
                        _notifications?.Dispose();
                        _host?.Dispose();
                        _host = null;
                    }
                    base.Dispose(disposing);
                }

                private void StartDisplayLink()
                {
                    if (_displayLink != null || _host == null)
                        return;
                    _displayLink = CADisplayLink.Create(OnDisplayLink);
                    _displayLink.PreferredFramesPerSecond = _host.PreferredFramesPerSecond;
                    _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Default);
                }

                private void StopDisplayLink()
                {
                    _displayLink?.Invalidate();
                    _displayLink = null;
                }

                private void OnDisplayLink()
                {
                    if (_host == null || _displayLink == null)
                        return;
                    PollGamepads();
                    _host.Tick((long)(_displayLink.Timestamp * 1_000_000_000d));
                }

                private void PublishViewMetrics(MetalGameView view)
                {
                    if (_host == null)
                        return;
                    int viewWidth = Math.Max(1, (int)Math.Round(view.Bounds.Width));
                    int viewHeight = Math.Max(1, (int)Math.Round(view.Bounds.Height));
                    UIEdgeInsets insets = view.SafeAreaInsets;
                    _host.UpdateSafeAreaInsets(
                        (int)Math.Round(insets.Left),
                        (int)Math.Round(insets.Top),
                        (int)Math.Round(insets.Right),
                        (int)Math.Round(insets.Bottom),
                        viewWidth,
                        viewHeight);
                }

                private bool DispatchPresses(NSSet<UIPress> presses, bool pressed)
                {
                    if (_host == null)
                        return false;
                    bool handled = false;
                    foreach (UIPress press in presses)
                    {
                        UIKey? key = press.Key;
                        if (key == null)
                            continue;
                        handled |= _host.HandleKey((int)key.KeyCode, pressed);
                    }
                    return handled;
                }

                private void SetSoftKeyboardVisible(bool visible)
                {
                    if (_imeField == null)
                        return;
                    ApplyImeOptions();
                    if (visible)
                        _imeField.BecomeFirstResponder();
                    else
                        _imeField.ResignFirstResponder();
                }

                private void ApplyImeOptions()
                {
                    if (_imeField == null || _host == null)
                        return;
                    _imeField.SecureTextEntry = _host.InputHandler.LastSecureTextEntry;
                    _imeField.KeyboardType = _host.InputHandler.LastKeyboardType switch
                    {
                        TouchScreenKeyboardType.NumberPad => UIKeyboardType.NumberPad,
                        TouchScreenKeyboardType.PhonePad => UIKeyboardType.PhonePad,
                        TouchScreenKeyboardType.EmailAddress => UIKeyboardType.EmailAddress,
                        TouchScreenKeyboardType.URL => UIKeyboardType.Url,
                        TouchScreenKeyboardType.DecimalPad => UIKeyboardType.DecimalPad,
                        TouchScreenKeyboardType.Search => UIKeyboardType.WebSearch,
                        TouchScreenKeyboardType.NumbersAndPunctuation => UIKeyboardType.NumbersAndPunctuation,
                        TouchScreenKeyboardType.NamePhonePad => UIKeyboardType.NamePhonePad,
                        TouchScreenKeyboardType.ASCIICapable => UIKeyboardType.ASCIICapable,
                        _ => UIKeyboardType.Default,
                    };
                }

                private UITextField CreateImeField()
                {
                    var field = new UITextField
                    {
                        Hidden = true,
                        AutocorrectionType = UITextAutocorrectionType.No,
                        SpellCheckingType = UITextSpellCheckingType.No,
                    };
                    field.ShouldChangeCharacters = (textField, range, replacement) =>
                    {
                        if (_host == null)
                            return false;
                        if (string.IsNullOrEmpty(replacement))
                            _host.InputHandler.DeleteSurroundingText((int)range.Length, 0);
                        else
                            _host.InputHandler.CommitText(replacement);
                        return false;
                    };
                    field.ShouldEndEditing = _ =>
                    {
                        _host?.InputHandler.SetComposingText(string.Empty, 0);
                        return true;
                    };
                    return field;
                }

                private IosPlatformServices CreatePlatformServices()
                {
                    _haptics ??= new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);
                    UIImpactFeedbackGenerator haptics = _haptics;
                    return new IosPlatformServices(
                        localeName: static () => NSLocale.CurrentLocale.LocaleIdentifier.Replace('_', '-'),
                        timeZoneId: static () => NSTimeZone.LocalTimeZone.Name,
                        networkReachability: static () => PlatformNetworkReachability.Unknown,
                        openUrl: url => UIApplication.SharedApplication.OpenUrl(new NSUrl(url)),
                        setKeepScreenOn: value =>
                        {
                            UIApplication.SharedApplication.IdleTimerDisabled = value;
                            return true;
                        },
                        vibrate: duration =>
                        {
                            haptics.Prepare();
                            haptics.ImpactOccurred();
                            return duration > TimeSpan.Zero;
                        },
                        openApplicationSettings: () =>
                        {
                            NSUrl? settings = new NSUrl(UIApplication.OpenSettingsUrlString);
                            return settings != null && UIApplication.SharedApplication.OpenUrl(settings);
                        },
                        trySetScreenOrientation: SetEngineScreenOrientation);
                }

                // --- Screen orientation (Unity Screen.orientation parity) ---------------------------
                // The supported-orientations mask below is what iOS actually consults; requests
                // only update the mask and poke the system to re-evaluate it.

                // iOS 27 bindings expose GetSupportedInterfaceOrientations() as the virtual
                // (the old SupportedInterfaceOrientations property no longer exists to override).
                public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations()
                {
                        if (_requestedOrientation == ScreenOrientation.AutoRotation)
                        {
                            UIInterfaceOrientationMask mask = 0;
                            if (_autoRotatePolicy.AutoRotateToPortrait)
                                mask |= UIInterfaceOrientationMask.Portrait;
                            if (_autoRotatePolicy.AutoRotateToPortraitUpsideDown)
                                mask |= UIInterfaceOrientationMask.PortraitUpsideDown;
                            // UIKit's LandscapeLeft is "home button on the right", matching Unity.
                            if (_autoRotatePolicy.AutoRotateToLandscapeLeft)
                                mask |= UIInterfaceOrientationMask.LandscapeLeft;
                            if (_autoRotatePolicy.AutoRotateToLandscapeRight)
                                mask |= UIInterfaceOrientationMask.LandscapeRight;
                            return mask == 0 ? base.GetSupportedInterfaceOrientations() : mask;
                        }
                        return _requestedOrientation switch
                        {
                            ScreenOrientation.Portrait => UIInterfaceOrientationMask.Portrait,
                            ScreenOrientation.PortraitUpsideDown => UIInterfaceOrientationMask.PortraitUpsideDown,
                            ScreenOrientation.LandscapeLeft => UIInterfaceOrientationMask.LandscapeLeft,
                            ScreenOrientation.LandscapeRight => UIInterfaceOrientationMask.LandscapeRight,
                            _ => base.GetSupportedInterfaceOrientations(),
                        };
                }

                private bool SetEngineScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy policy)
                {
                    _requestedOrientation = requested;
                    _autoRotatePolicy = policy;
                    InvokeOnMainThread(() =>
                    {
                        if (OperatingSystem.IsIOSVersionAtLeast(16, 1))
                        {
                            SetNeedsUpdateOfSupportedInterfaceOrientations();
                        }
                        else
                        {
                            // Legacy re-evaluation: force a device-orientation write so the system
                            // re-asks for SupportedInterfaceOrientations, then retry autorotation.
                            UIDeviceOrientation device = ToDeviceOrientation(requested);
                            UIDevice.CurrentDevice.SetValueForKey(NSObject.FromObject(device), new NSString("orientation"));
                            UIViewController.AttemptRotationToDeviceOrientation();
                        }
                    });
                    return true;
                }

                private static UIDeviceOrientation ToDeviceOrientation(ScreenOrientation requested) => requested switch
                {
                    ScreenOrientation.Portrait => UIDeviceOrientation.Portrait,
                    ScreenOrientation.PortraitUpsideDown => UIDeviceOrientation.PortraitUpsideDown,
                    ScreenOrientation.LandscapeLeft => UIDeviceOrientation.LandscapeRight,
                    ScreenOrientation.LandscapeRight => UIDeviceOrientation.LandscapeLeft,
                    _ => UIDeviceOrientation.Unknown,
                };

                public override void ViewWillTransitionToSize(CoreGraphics.CGSize toSize, IUIViewControllerTransitionCoordinator coordinator)
                {
                    base.ViewWillTransitionToSize(toSize, coordinator);
                    // Size cannot distinguish landscape left from right (or portrait upside down),
                    // but those only matter for layout; the engine treats them uniformly there.
                    XEngine.Runtime.Screen.ReportDeviceOrientation(
                        toSize.Width > toSize.Height ? ScreenOrientation.LandscapeLeft : ScreenOrientation.Portrait);
                }

                private IDisposable SubscribeLifecycle()
                {
                    var center = NSNotificationCenter.DefaultCenter;
                    NSObject background = center.AddObserver(UIApplication.DidEnterBackgroundNotification, _ =>
                    {
                        StopDisplayLink();
                        _host?.Suspend();
                    });
                    NSObject foreground = center.AddObserver(UIApplication.WillEnterForegroundNotification, _ =>
                    {
                        if (View is MetalGameView view)
                        {
                            view.ConfigureMetalLayer();
                            _host?.ResizeLayer(
                                Math.Max(1, (int)view.DrawableSize.Width),
                                Math.Max(1, (int)view.DrawableSize.Height));
                        }
                        _host?.Resume();
                        StartDisplayLink();
                    });
                    NSObject connected = center.AddObserver(GCController.DidConnectNotification, _ => SyncConnectedGamepads());
                    NSObject disconnected = center.AddObserver(GCController.DidDisconnectNotification, notification =>
                    {
                        if (notification.Object is GCController controller)
                            _host?.GamepadDisconnected((int)controller.Handle);
                    });
                    NSObject interruption = center.AddObserver(AVAudioSession.InterruptionNotification, notification =>
                    {
                        if (_host == null || notification.UserInfo == null)
                            return;
                        var typeNumber = notification.UserInfo["AVAudioSessionInterruptionTypeKey"] as NSNumber;
                        if (typeNumber == null)
                            return;
                        if ((AVAudioSessionInterruptionType)typeNumber.Int32Value == AVAudioSessionInterruptionType.Began)
                        {
                            _host.HandleAudioInterruption(true, false);
                            return;
                        }
                        var optionNumber = notification.UserInfo["AVAudioSessionInterruptionOptionKey"] as NSNumber;
                        bool shouldResume = optionNumber != null &&
                            (AVAudioSessionInterruptionOptions)optionNumber.UInt32Value ==
                            AVAudioSessionInterruptionOptions.ShouldResume;
                        _host.HandleAudioInterruption(false, shouldResume);
                    });
                    return new NotificationScope(center, background, foreground, connected, disconnected, interruption);
                }

                private void SyncConnectedGamepads()
                {
                    if (_host == null)
                        return;
                    GCController[] controllers = GCController.Controllers;
                    for (int i = 0; i < controllers.Length; i++)
                        _host.GamepadConnected((int)controllers[i].Handle);
                }

                private void PollGamepads()
                {
                    if (_host == null)
                        return;
                    GCController[] controllers = GCController.Controllers;
                    for (int i = 0; i < controllers.Length; i++)
                    {
                        GCController controller = controllers[i];
                        GCExtendedGamepad? pad = controller.ExtendedGamepad;
                        if (pad == null)
                            continue;
                        int id = (int)controller.Handle;
                        _host.GamepadConnected(id);
                        _host.HandleGamepadButton(id, GamepadButton.A, pad.ButtonA.IsPressed);
                        _host.HandleGamepadButton(id, GamepadButton.B, pad.ButtonB.IsPressed);
                        _host.HandleGamepadButton(id, GamepadButton.X, pad.ButtonX.IsPressed);
                        _host.HandleGamepadButton(id, GamepadButton.Y, pad.ButtonY.IsPressed);
                        _host.HandleGamepadButton(id, GamepadButton.LeftBumper, pad.LeftShoulder.IsPressed);
                        _host.HandleGamepadButton(id, GamepadButton.RightBumper, pad.RightShoulder.IsPressed);
                        _host.HandleGamepadButton(id, GamepadButton.LeftStick, pad.LeftThumbstickButton?.IsPressed ?? false);
                        _host.HandleGamepadButton(id, GamepadButton.RightStick, pad.RightThumbstickButton?.IsPressed ?? false);
                        _host.HandleGamepadButton(id, GamepadButton.Start, pad.ButtonMenu.IsPressed);
                        _host.HandleGamepadButton(id, GamepadButton.Back, pad.ButtonOptions?.IsPressed ?? false);
                        _host.HandleGamepadMotion(
                            id,
                            pad.LeftThumbstick.XAxis.Value,
                            pad.LeftThumbstick.YAxis.Value,
                            pad.RightThumbstick.XAxis.Value,
                            pad.RightThumbstick.YAxis.Value,
                            pad.LeftTrigger.Value,
                            pad.RightTrigger.Value,
                            pad.DPad.XAxis.Value,
                            pad.DPad.YAxis.Value);
                    }
                }

                private static string ReadClipboard() => UIPasteboard.General.String ?? string.Empty;
                private static void WriteClipboard(string value) => UIPasteboard.General.String = value;

                private static void ConfigureSandbox()
                {
                    string resourcePath = NSBundle.MainBundle.ResourcePath ?? NSBundle.MainBundle.BundlePath;
                    string contentRoot = Path.Combine(resourcePath, "Content");
                    if (!Directory.Exists(contentRoot))
                        contentRoot = Path.Combine(NSBundle.MainBundle.BundlePath, "Content");

                    string documents = FirstSearchPath(NSSearchPathDirectory.DocumentDirectory);
                    string caches = FirstSearchPath(NSSearchPathDirectory.CachesDirectory);
                    string tmp = NSFileManager.DefaultManager.GetTemporaryDirectory().Path;
                    IosAssetHost.Configure(resourcePath, contentRoot, documents, caches, tmp);
                }

                private static void ConfigureAudioSession()
                {
                    // Playback ignores the Ring/Silent switch. v1 does not request UIBackgroundModes
                    // audio, so a call/Siri interrupt pauses via AVAudioSession.InterruptionNotification.
                    // iOS 27 bindings: SharedInstance is a method and the (category, out NSError)
                    // SetCategory overload became an NSError-returning one.
                    AVAudioSession session = AVAudioSession.SharedInstance();
                    NSError? categoryError = session.SetCategory(AVAudioSessionCategory.Playback);
                    if (categoryError != null)
                        Console.WriteLine("AVAudioSession SetCategory failed: " + categoryError.LocalizedDescription);
                    session.SetActive(true, out NSError? activeError);
                    if (activeError != null)
                        Console.WriteLine("AVAudioSession SetActive failed: " + activeError.LocalizedDescription);
                }

                private static string FirstSearchPath(NSSearchPathDirectory directory)
                {
                    string[] paths = NSSearchPath.GetDirectories(directory, NSSearchPathDomain.User, true);
                    if (paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
                        throw new InvalidOperationException("Missing iOS sandbox path for " + directory);
                    return paths[0];
                }

                private sealed class NotificationScope : IDisposable
                {
                    private readonly NSNotificationCenter _center;
                    private readonly NSObject[] _tokens;

                    public NotificationScope(NSNotificationCenter center, params NSObject[] tokens)
                    {
                        _center = center;
                        _tokens = tokens;
                    }

                    public void Dispose()
                    {
                        for (int i = 0; i < _tokens.Length; i++)
                            _center.RemoveObserver(_tokens[i]);
                    }
                }
            }

            internal sealed class IosPlayerGame : Game
            {
                public override void Initialize()
                {
                    Application.IsPlaying = true;
                    Application.IsEditor = false;
                    Application.IsPlayerBuild = true;
                    Application.ConfigureRuntimePlatform(ApplicationRuntimePlatform.iOS);

                    if (AssetDatabase.Current is not PlayerAssetBackendBase assetBackend)
                    {
                        Debug.LogError("[iOS] No player asset backend was initialized; cannot load the build scene.");
                        return;
                    }

                    GameResources.Initialize(assetBackend.ResourcesMap);
                    Guid sceneGuid = assetBackend.DefaultSceneGuid;
                    Guid expectedSceneGuid = new Guid("{{expectedSceneGuid}}");
                    if (expectedSceneGuid != Guid.Empty && sceneGuid != expectedSceneGuid)
                    {
                        Debug.LogError($"[iOS] Asset manifest default scene {sceneGuid} does not match build scene {expectedSceneGuid}.");
                        sceneGuid = expectedSceneGuid;
                    }

                    PlayerSettingsLoader.ApplyAssetConfig(Path.Combine(Application.DataPath, "Content", "Settings"));
                    if (sceneGuid == Guid.Empty)
                    {
                        Debug.LogError("[iOS] No enabled build scene; rendering clear frame.");
                        return;
                    }

                    XEngine.Runtime.Resources.Scene? scene = assetBackend.LoadScene(sceneGuid);
                    if (scene == null)
                        Debug.LogError($"[iOS] Failed to load default scene {sceneGuid}.");
                    else
                    {
                        XEngine.Runtime.Resources.Scene.Load(scene);
                        Debug.Log($"[iOS] Loaded default scene {sceneGuid}.");
                    }

                    PlayerSettingsLoader.Apply(Path.Combine(Application.DataPath, "Content", "Settings"));
                }
            }
            """;
    }

    internal static string CreateMetalView() =>
        """
        using System;

        using CoreAnimation;
        using CoreGraphics;
        using Foundation;
        using Metal;
        using ObjCRuntime;
        using UIKit;

        using XEngine.Runtime.Platforms.iOS;

        namespace XEngine.Generated.iOS;

        /// <summary>
        /// UIView whose layer class is CAMetalLayer. drawableSize is physical pixels;
        /// contentsScale follows the screen nativeScale. Touches are forwarded in view points.
        /// </summary>
        public sealed class MetalGameView : UIView
        {
            [Export("layerClass")]
            public static Class LayerClass() => new Class(typeof(CAMetalLayer));

            public IosPlatformHost? Host { get; set; }

            public nint MetalLayerHandle => Layer.Handle;

            public CGSize DrawableSize
            {
                get
                {
                    ConfigureMetalLayer();
                    return ((CAMetalLayer)Layer).DrawableSize;
                }
            }

            public override void LayoutSubviews()
            {
                base.LayoutSubviews();
                ConfigureMetalLayer();
            }

            public override void TouchesBegan(NSSet touches, UIEvent? evt)
            {
                DispatchTouches(touches, evt, IosTouchAction.Began);
            }

            public override void TouchesMoved(NSSet touches, UIEvent? evt)
            {
                DispatchTouches(touches, evt, IosTouchAction.Moved);
            }

            public override void TouchesEnded(NSSet touches, UIEvent? evt)
            {
                DispatchTouches(touches, evt, IosTouchAction.Ended);
            }

            public override void TouchesCancelled(NSSet touches, UIEvent? evt)
            {
                DispatchTouches(touches, evt, IosTouchAction.Canceled);
            }

            public void ConfigureMetalLayer()
            {
                nfloat scale = Window?.Screen?.NativeScale ?? UIScreen.MainScreen.NativeScale;
                if (scale <= 0)
                    scale = 1;
                ContentScaleFactor = scale;

                var metal = (CAMetalLayer)Layer;
                metal.ContentsScale = scale;
                metal.FramebufferOnly = true;
                metal.PixelFormat = MTLPixelFormat.BGRA8Unorm;
                nfloat width = (nfloat)Math.Max(1d, (double)(Bounds.Width * scale));
                nfloat height = (nfloat)Math.Max(1d, (double)(Bounds.Height * scale));
                metal.DrawableSize = new CGSize(width, height);
            }

            private void DispatchTouches(NSSet touches, UIEvent? evt, IosTouchAction action)
            {
                IosPlatformHost? host = Host;
                if (host == null)
                    return;
                foreach (UITouch touch in touches.Cast<UITouch>())
                {
                    int pointerId = (int)touch.Handle;
                    CGPoint location = touch.LocationInView(this);
                    float pressure = touch.MaximumPossibleForce > 0
                        ? (float)(touch.Force / touch.MaximumPossibleForce)
                        : 1f;
                    if (action == IosTouchAction.Moved && evt != null)
                    {
                        UITouch[]? coalesced = evt.GetCoalescedTouches(touch);
                        if (coalesced != null)
                        {
                            foreach (UITouch sample in coalesced)
                            {
                                CGPoint sampleLocation = sample.LocationInView(this);
                                float samplePressure = sample.MaximumPossibleForce > 0
                                    ? (float)(sample.Force / sample.MaximumPossibleForce)
                                    : pressure;
                                host.TouchHistory(
                                    pointerId,
                                    (float)sampleLocation.X,
                                    (float)sampleLocation.Y,
                                    samplePressure,
                                    (long)(sample.Timestamp * 1000d));
                            }
                        }
                    }
                    host.DispatchTouch(pointerId, action, (float)location.X, (float)location.Y, pressure);
                }
            }
        }
        """;
}
