// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Linq;

using Xunit;

using XEngine.Animation;
using XEngine.Echo;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Regression for the zonezero combat blocker: a generated .controller asset parses on disk
/// (names/states fine) but deserializes with every state's <see cref="AnimatorController.State.Motion"/>
/// AssetID silently emptied — the FSM then compiled "alive" states that played nothing. Pins the
/// Echo round-trip against real character controllers from the host project when available.
/// </summary>
public class ControllerAssetDeserializeTests : RuntimeTestBase
{
    private static string? FindController(string subpath)
    {
        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "ZonezeroTestProject", "Assets", "ZZZ",
                "Arts", "PlayerModel", subpath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent!;
        }
        return null;
    }

    private static AnimatorController Load(string path)
    {
        EchoObject echo = EchoObject.ReadFromString(File.ReadAllText(path));
        return Serializer.Deserialize<AnimatorController>(echo)
               ?? throw new InvalidOperationException($"deserialized null controller: {path}");
    }

    [Fact]
    public void Generated_controller_keeps_state_motion_guids()
    {
        string? path = FindController(Path.Combine("Corlin", "Corin.controller"));
        if (path == null)
            return; // host project not checked out next to the runtime — nothing to pin here.

        AnimatorController controller = Load(path);

        Assert.True(controller.States.Count >= 20, $"states={controller.States.Count}");
        var idle = controller.States.FirstOrDefault(s => s.Name == "Idle");
        Assert.NotNull(idle);

        int emptyMotions = 0;
        foreach (AnimatorController.State s in controller.States)
        {
            if (s.Motion.AssetID == Guid.Empty)
                emptyMotions++;
        }
        Assert.True(emptyMotions == 0,
            $"{emptyMotions}/{controller.States.Count} states lost their Motion AssetID during " +
            "deserialization — Echo is dropping nested AssetRef struct fields (see Idle).");
    }

    [Fact]
    public void In_memory_roundtrip_preserves_assetref_field()
    {
        // Isolate Echo from disk differences: author → text → object.
        Guid id = Guid.Parse("11112222-3333-4444-5555-666677778888");
        var author = new AnimatorController { Name = "roundtrip" };
        author.States.Add(new AnimatorController.State
        {
            Name = "State_A",
            Motion = new AssetRef<AnimationClip>(id),
        });

        var logged = new System.Collections.Generic.List<string>();
        Echo.Serializer.Logger = new CollectingLogger(logged);
        try
        {
            // Mirror ZonezeroNativeAssets.WriteNativeAsset exactly.
            EchoObject echo = Serializer.Serialize(typeof(object), author);
            string text = echo.WriteToString();

            if (!text.Contains(id.ToString()))
                throw new Xunit.Sdk.XunitException(
                    "serialized text lost the Motion guid. text=" + text +
                    " | echoLog=" + string.Join(" ;; ", logged));

            var back = Serializer.Deserialize<AnimatorController>(echo);
            if (back == null)
                throw new Xunit.Sdk.XunitException(
                    "typed deserialize returned null. echoLog=" + string.Join(" ;; ", logged));

            Assert.Equal(id, back.States[0].Motion.AssetID);
        }
        finally
        {
            Echo.Serializer.Logger = new global::Echo.Logging.NullEchoLogger();
        }
    }

    private sealed class CollectingLogger : global::Echo.Logging.IEchoLogger
    {
        private readonly System.Collections.Generic.List<string> _sink;
        public CollectingLogger(System.Collections.Generic.List<string> sink) => _sink = sink;
        public void Debug(string message) => _sink.Add("[D] " + message);
        public void Info(string message) => _sink.Add("[I] " + message);
        public void Warning(string message) => _sink.Add("[W] " + message);
        public void Error(string message, Exception? exception = null)
            => _sink.Add("[E] " + message + (exception != null ? " :: " + exception.GetType().Name + ":" + exception.Message : ""));
    }
}
