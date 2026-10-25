using System;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.Audio;
using XEngine.Runtime.AssetStreaming;
using XEngine.Runtime.Rendering;

namespace XEngine.Runtime;

/// <summary>
/// Loads and applies project settings from Echo YAML files in the built player.
/// Reads from Content/Settings/ folder and applies graphics, physics, audio, time, and tags/layers.
/// </summary>
public static class PlayerSettingsLoader
{
    private static string? _settingsDir;

    /// <summary>Apply all project settings and register for scene load events.</summary>
    public static void Apply(string settingsDir)
    {
        _settingsDir = settingsDir;

        if (!Directory.Exists(settingsDir))
        {
            Debug.LogWarning($"[PlayerSettings] Settings directory not found: {settingsDir}");
            return;
        }

        ApplyAssetConfig(settingsDir);
        ApplyAudio(settingsDir);
        ApplyTime(settingsDir);
        ApplyTagsAndLayers(settingsDir);
        ApplyGeneral(settingsDir);
        ApplyQuality(settingsDir);
        ApplyGraphics(settingsDir);
        ApplyPlayer(settingsDir);

        // Physics needs to apply to each new scene's PhysicsWorld
        ApplyPhysics(settingsDir);

        // Re-apply physics whenever a new scene loads
        Resources.Scene.OnSceneLoaded += () =>
        {
            if (_settingsDir != null)
                ApplyPhysics(_settingsDir);
        };
    }

    /// <summary>
    /// Apply the async-asset-loading toggle. Exposed separately so the player can set it
    /// BEFORE the default scene loads (component OnEnable may resolve AssetRefs), not just
    /// during the bulk <see cref="Apply"/> that runs after scene load.
    /// </summary>
    public static void ApplyAssetConfig(string dir)
    {
        var settings = Read(dir, "Assets");
        if (settings == null) return;

        try
        {
            // Default ON if the key is absent.
            bool async = !settings.TryGet("AsyncAssetLoading", out var a) || a!.BoolValue;
            AssetLoadingConfig.AsyncEnabled = async;
            Debug.Log($"[PlayerSettings] Async asset loading: {async}.");
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply asset config: {ex.Message}"); }
    }

    /// <summary>
    /// Applies only the player orientation settings from a packaged asset provider. Mobile
    /// players receive Settings/Player.yaml from an APK/HAP VFS rather than a physical directory,
    /// so this must run before the first surface is attached and before the scene is loaded.
    /// </summary>
    public static void ApplyOrientation(IAssetStreamProvider provider, string settingsDirectory = "Settings")
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);

        string path = settingsDirectory.TrimEnd('/', '\\') + "/Player.yaml";
        try
        {
            using Stream? stream = provider.OpenRead(path);
            if (stream == null)
            {
                Debug.LogWarning($"[PlayerSettings] Packaged player settings not found: {path}");
                return;
            }

            using var reader = new StreamReader(stream);
            EchoObject? settings = EchoObject.ReadFromYaml(reader.ReadToEnd());
            if (settings != null)
                ApplyPlayer(settings);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[PlayerSettings] Failed to read packaged player settings: {ex.Message}");
        }
    }

    private static void ApplyPhysics(string dir)
    {
        var settings = Read(dir, "Physics");
        if (settings == null) return;

        try
        {
            float gx = settings.TryGet("GravityX", out var gxp) ? gxp!.FloatValue : 0;
            float gy = settings.TryGet("GravityY", out var gyp) ? gyp!.FloatValue : -9.81f;
            float gz = settings.TryGet("GravityZ", out var gzp) ? gzp!.FloatValue : 0;
            int solverIter = settings.TryGet("SolverIterations", out var si) ? si!.IntValue : 8;
            int relaxIter = settings.TryGet("RelaxIterations", out var ri) ? ri!.IntValue : 4;
            int subSteps = settings.TryGet("SubSteps", out var ss) ? ss!.IntValue : 2;
            bool sleep = !settings.TryGet("AllowSleep", out var sl) || sl!.BoolValue;
            bool mt = !settings.TryGet("UseMultithreading", out var mtp) || mtp!.BoolValue;
            bool sync = !settings.TryGet("AutoSyncTransforms", out var st) || st!.BoolValue;

            // Advanced settings
            bool determ = settings.TryGet("EnhancedDeterminism", out var dt) && dt!.BoolValue;
            bool persistThreads = settings.TryGet("ThreadModel", out var tmp)
                && tmp!.IntValue == (int)PhysicsThreadModel.Persistent;
            bool auxcp = !settings.TryGet("EnableAuxiliaryContactPoints", out var ax) || ax!.BoolValue;
            bool persistManifold = !settings.TryGet("PersistentContactManifold", out var pm) || pm!.BoolValue;
            float specRelax = settings.TryGet("SpeculativeRelaxationFactor", out var sr) ? sr!.FloatValue : 0.9f;
            float sweepVelocity = settings.TryGet("SweepVelocityThreshold", out var sv) ? sv!.FloatValue : 10f;

            // Materials and queries
            float bounceThreshold = settings.TryGet("BounceThreshold", out var bt) ? bt!.FloatValue : 1f;
            bool queriesHitTriggers = !settings.TryGet("QueriesHitTriggers", out var qht) || qht!.BoolValue;

            // The default material is an AssetRef, so the settings file holds a GUID rather than the
            // material itself. Resolve it through the database like any other asset reference.
            PhysicsMaterial defaultMaterial = null;
            if (settings.TryGet("DefaultMaterial", out var dm) && dm!.TryGet("AssetID", out var dmId)
                && Guid.TryParse(dmId!.StringValue, out Guid dmGuid) && dmGuid != Guid.Empty)
            {
                defaultMaterial = AssetDatabase.Get(dmGuid) as PhysicsMaterial;
                if (defaultMaterial == null)
                    Debug.LogWarning($"[PlayerSettings] Default physics material {dmGuid} is missing.");
            }

            var scene = Resources.Scene.Current;
            if (scene != null)
            {
                scene.Physics.Gravity = new Vector.Float3(gx, gy, gz);
                scene.Physics.SolverIterations = solverIter;
                scene.Physics.RelaxIterations = relaxIter;
                scene.Physics.Substep = subSteps;
                scene.Physics.AllowSleep = sleep;
                scene.Physics.UseMultithreading = mt;
                scene.Physics.AutoSyncTransforms = sync;
                scene.Physics.EnhancedDeterminism = determ;
                scene.Physics.ThreadModel = persistThreads ? PhysicsThreadModel.Persistent : PhysicsThreadModel.Regular;
                scene.Physics.EnableAuxiliaryContactPoints = auxcp;
                scene.Physics.PersistentContactManifold = persistManifold;
                scene.Physics.SpeculativeRelaxationFactor = specRelax;
                scene.Physics.SweepVelocityThreshold = sweepVelocity;
                scene.Physics.BounceThreshold = bounceThreshold;
                scene.Physics.QueriesHitTriggers = queriesHitTriggers;
                scene.Physics.DefaultMaterial = defaultMaterial;
            }

            // Collision matrix (uint[] serializes as a compound holding an "array" list)
            if (settings.TryGet("CollisionMatrixRows", out var cmProp) && cmProp!.TryGet("array", out var rows)
                && rows!.TagType == EchoType.List)
            {
                int i = 0;
                foreach (var row in rows.List)
                {
                    if (i >= 32) break;
                    uint val = row.UIntValue;
                    for (int j = 0; j < 32; j++)
                        CollisionMatrix.SetLayerCollision(i, j, (val & (1u << j)) != 0);
                    i++;
                }
            }

            Debug.Log("[PlayerSettings] Physics applied.");
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply physics: {ex.Message}"); }
    }

    private static void ApplyAudio(string dir)
    {
        var settings = Read(dir, "Audio");
        if (settings == null) return;

        try
        {
            float vol = settings.TryGet("GlobalVolume", out var v) ? v!.FloatValue : 1f;
            AudioContext.MasterVolume = vol;
            Debug.Log("[PlayerSettings] Audio applied.");
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply audio: {ex.Message}"); }
    }

    /// <summary>
    /// Quality-level render pipeline overrides (Unity 6.7 customRenderPipeline per level). Must run
    /// before ApplyGraphics so the assignment chain resolves tier override → graphics default.
    /// </summary>
    private static void ApplyQuality(string dir)
    {
        var settings = Read(dir, "Quality");
        if (settings == null) return;

        try
        {
            ApplyTierPipelineOverride(settings, "PreviewPipeline", QualitySettings.QualityTier.Preview);
            ApplyTierPipelineOverride(settings, "FullPipeline", QualitySettings.QualityTier.Full);
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply quality: {ex.Message}"); }
    }

    private static void ApplyTierPipelineOverride(EchoObject settings, string key, QualitySettings.QualityTier tier)
    {
        if (!settings.TryGet(key, out var tierProp)
            || !tierProp!.TryGet("AssetID", out var tierId)
            || !Guid.TryParse(tierId!.StringValue, out Guid tierGuid)
            || tierGuid == Guid.Empty)
            return;

        var overrideAsset = AssetDatabase.Get(tierGuid) as UniversalRenderPipelineAsset;
        overrideAsset?.Renderer.EnsureLoaded();
        QualitySettings.SetPipelineOverride(tier, overrideAsset);
        if (overrideAsset == null)
            Debug.LogWarning($"[PlayerSettings] Quality pipeline override {tierGuid} for {tier} is missing.");
    }

    private static void ApplyGraphics(string dir)
    {
        var settings = Read(dir, "Graphics");
        if (settings == null) return;

        try
        {
            bool shadows = !settings.TryGet("ShadowsEnabled", out var sh) || sh!.BoolValue;
            float shadowDistance = settings.TryGet("ShadowDistance", out var sd) ? sd!.FloatValue : 100f;
            int shadowAtlasSize = settings.TryGet("ShadowAtlasSize", out var sa) ? sa!.IntValue : 4096;
            bool imageEffects = !settings.TryGet("ImageEffectsEnabled", out var ie) || ie!.BoolValue;
            int antiAliasing = settings.TryGet("AntiAliasing", out var aa) ? aa!.IntValue : 0;

            QualitySettings.ShadowsEnabled = shadows;
            QualitySettings.ShadowDistance = shadowDistance;
            QualitySettings.ShadowAtlasSize = shadowAtlasSize;
            QualitySettings.ImageEffectsEnabled = imageEffects;
            QualitySettings.AntiAliasing = Enum.IsDefined((QualitySettings.AntiAliasingMode)antiAliasing)
                ? (QualitySettings.AntiAliasingMode)antiAliasing
                : QualitySettings.AntiAliasingMode.Disabled;

            if (settings.TryGet("DefaultPipeline", out var pipelineProp)
                && pipelineProp!.TryGet("AssetID", out var pipelineId)
                && Guid.TryParse(pipelineId!.StringValue, out Guid pipelineGuid)
                && pipelineGuid != Guid.Empty)
            {
                var pipeline = AssetDatabase.Get(pipelineGuid) as UniversalRenderPipelineAsset;
                pipeline?.Renderer.EnsureLoaded();
                UniversalPipelineAssignment.GraphicsDefault = pipeline;
                if (pipeline == null)
                    Debug.LogWarning($"[PlayerSettings] Default pipeline {pipelineGuid} is missing.");
            }

            // Unity 6.7 quality-level customRenderPipeline: a tier override set by Quality.yaml
            // (applied by ApplyQuality) wins over the graphics default once both are loaded.
            UniversalPipelineAssignment.Apply();

            if (settings.TryGet("PipelineGlobalSettings", out var globalProp)
                && globalProp!.TryGet("AssetID", out var globalId)
                && Guid.TryParse(globalId!.StringValue, out Guid globalGuid)
                && globalGuid != Guid.Empty)
            {
                UniversalRenderPipelineGlobalSettings.Active =
                    AssetDatabase.Get(globalGuid) as UniversalRenderPipelineGlobalSettings;
                if (UniversalRenderPipelineGlobalSettings.Active == null)
                    Debug.LogWarning($"[PlayerSettings] Pipeline global settings {globalGuid} is missing.");
            }

            Debug.Log("[PlayerSettings] Graphics applied.");
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply graphics: {ex.Message}"); }
    }

    private static void ApplyPlayer(string dir)
    {
        var settings = Read(dir, "Player");
        if (settings == null) return;

        ApplyPlayer(settings);
    }

    private static void ApplyPlayer(EchoObject settings)
    {

        try
        {
            if (!Application.IsEditor)
                Application.TargetFrameRate = settings.TryGet("TargetFrameRate", out var frameRate)
                    ? Math.Max(0, frameRate!.IntValue) : 0;
            var orientation = ScreenOrientation.AutoRotation;
            if (settings.TryGet("DefaultOrientation", out var orientationProperty))
            {
                int raw = orientationProperty!.IntValue;
                if (Enum.IsDefined(typeof(ScreenOrientation), raw))
                    orientation = (ScreenOrientation)raw;
            }

            // Flags first: the first AutoRotation request must already carry the full policy.
            Screen.AutoRotateToPortrait = !settings.TryGet("AutoRotateToPortrait", out var portrait) || portrait!.BoolValue;
            Screen.AutoRotateToPortraitUpsideDown =
                !settings.TryGet("AutoRotateToPortraitUpsideDown", out var portraitDown) || portraitDown!.BoolValue;
            Screen.AutoRotateToLandscapeLeft =
                !settings.TryGet("AutoRotateToLandscapeLeft", out var landscapeLeft) || landscapeLeft!.BoolValue;
            Screen.AutoRotateToLandscapeRight =
                !settings.TryGet("AutoRotateToLandscapeRight", out var landscapeRight) || landscapeRight!.BoolValue;
            Screen.Orientation = orientation;
            PlatformServices.KeepScreenOn = settings.TryGet("KeepScreenOn", out var awake) && awake!.BoolValue;

            Debug.Log($"[PlayerSettings] Screen orientation: {orientation}.");
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply player settings: {ex.Message}"); }
    }

    private static void ApplyTime(string dir)
    {
        var settings = Read(dir, "Time");
        if (settings == null) return;

        try
        {
            float fixedDt = settings.TryGet("FixedTimestep", out var ft) ? ft!.FloatValue : 1f / 60f;
            float timeScale = settings.TryGet("DefaultTimeScale", out var ts) ? ts!.FloatValue : 1f;
            int maxIter = settings.TryGet("MaxFixedIterations", out var mi) ? mi!.IntValue : 3;

            Time.FixedDeltaTime = fixedDt;
            Time.TimeScale = timeScale;
            Time.MaxFixedIterations = maxIter;
            Debug.Log("[PlayerSettings] Time applied.");
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply time: {ex.Message}"); }
    }

    private static void ApplyTagsAndLayers(string dir)
    {
        var settings = Read(dir, "Tags & Layers");
        if (settings == null) return;

        try
        {
            // Tags is a List<string> (serializes as a list directly).
            if (settings.TryGet("Tags", out var tagsProp) && tagsProp!.TagType == EchoType.List)
            {
                TagLayerManager.tags.Clear();
                foreach (var tag in tagsProp.List)
                    TagLayerManager.tags.Add(tag.StringValue);
            }

            // Layers is a string[] (serializes as a compound holding an "array" list).
            if (settings.TryGet("Layers", out var layersProp) && layersProp!.TryGet("array", out var layers)
                && layers!.TagType == EchoType.List)
            {
                int i = 0;
                foreach (var layer in layers.List)
                {
                    if (i >= TagLayerManager.layers.Length) break;
                    TagLayerManager.layers[i++] = layer.StringValue;
                }
            }

            Debug.Log("[PlayerSettings] Tags & Layers applied.");
        }
        catch (Exception ex) { Debug.LogWarning($"[PlayerSettings] Failed to apply tags/layers: {ex.Message}"); }
    }

    private static void ApplyGeneral(string dir)
    {
        // Informational only for now
    }

    private static EchoObject? Read(string dir, string name)
    {
        string path = Path.Combine(dir, $"{name}.yaml");
        if (!File.Exists(path)) return null;

        try
        {
            return EchoObject.ReadFromYaml(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
