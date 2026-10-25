// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Video;

namespace XEngine.Runtime.UI;

/// <summary>
/// Binds a <see cref="VideoPlayer"/>'s presented texture to a <see cref="RawImage"/>:
/// each frame the player's output texture is pushed into the RawImage's runtime-texture
/// slot (never serialized). Missing player/target fall back to siblings on this GameObject.
/// </summary>
[AddComponentMenu("UI/Video Image Binding")]
public sealed class VideoImageBinding : MonoBehaviour
{
    [SerializeField] private VideoPlayer? _player;
    [SerializeField] private RawImage? _target;

    public VideoPlayer? Player { get => _player; set => _player = value; }

    public RawImage? Target { get => _target; set => _target = value; }

    public override void Update()
    {
        VideoPlayer? player = _player ?? GetComponent<VideoPlayer>();
        RawImage? target = _target ?? GetComponent<RawImage>();
        if (player is null || target is null)
        {
            return;
        }

        target.RuntimeTexture = player.Texture;
    }
}
