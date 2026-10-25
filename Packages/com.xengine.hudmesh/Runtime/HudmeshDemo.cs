using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Self-contained demo: spawns a row of anchor targets, attaches a full nameplate (blood bar,
/// name, title, guild name, guild icon) to each, loops floating damage numbers on the first
/// target and pops a chat bubble on the second. Drop this component into any scene with a
/// camera and press play — the <c>HudmeshDemo</c> sample scene is a ready-made host. The
/// targets are invisible on purpose: the HUD quads are the visuals.
/// </summary>
[AddComponentMenu("Hudmesh/Hudmesh Demo")]
public class HudmeshDemo : MonoBehaviour
{
    [SerializeField] private int _targetCount = 5;
    [SerializeField] private Float3 _spawnOrigin = new(-4f, 1f, 0f);
    [SerializeField] private Float3 _spawnStep = new(2f, 0f, 0f);
    [SerializeField] private Float3 _headOffset = new(0f, 1.2f, 0f);

    private readonly List<GameObject> _targets = new();
    private Hud? _numberHud;
    private Hud? _talkHud;
    private float _nextNumberAt;
    private int _numberStyle;
    private bool _talkShown;

    public override void Start()
    {
        HudSpriteTable sprites = HudSpriteTable.CreateBundled();
        HudManager.Init(sprites);

        string[] names = { "Aelric", "Brissa", "Corvin", "Dunstan", "Elva", "Fenwick", "Gilda" };
        for (int i = 0; i < _targetCount; i++)
        {
            GameObject target = new($"Target {i + 1}");
            target.Transform.Position = _spawnOrigin + _spawnStep * i;

            Hud hud = HudManager.Instance.CreateHud(i + 1);
            hud.Init(target.Transform, _headOffset);
            hud.SetBloodVisible(true, (HudRelation)int.Clamp(i, 0, 3));
            hud.SetBloodPercent(0.4f + 0.15f * i);
            hud.SetText(HudSlot.Name, names[i % names.Length], HudRelation.Self);
            if (i % 2 == 0)
                hud.SetText(HudSlot.Title, "Knight", HudRelation.Self);
            if (i % 3 == 0)
                hud.SetText(HudSlot.GuildName, "XEngine", HudRelation.Self);
            hud.SetIcon(HudSlot.GuildIcon, "UI_headtop_sign_001_6", HudRelation.Self);

            _targets.Add(target);
            if (_numberHud is null) _numberHud = hud;
            if (_talkHud is null && i == 1) _talkHud = hud;
        }

        _nextNumberAt = Time.TimeSinceStartup + 0.5f;
    }

    public override void Update()
    {
        if (HudManager.Instance is null)
            return;

        float now = Time.TimeSinceStartup;
        if (_numberHud is not null && now >= _nextNumberAt)
        {
            int damage = Random.Shared.Next(80, 999);
            _numberHud.PopNumber(_numberStyle, damage, new Float3(0f, 0.4f, 0f));
            _numberStyle = (_numberStyle + 1) % 2;
            _nextNumberAt = now + 0.6f;
        }

        if (_talkHud is not null && now >= _nextNumberAt + 0.2f && !_talkShown)
        {
            _talkHud.Talk(0, "Hello from Hudmesh!");
            _talkShown = true;
        }
    }

    public override void OnDispose()
    {
        for (int i = 0; i < _targets.Count; i++)
            HudManager.Instance?.DeleteHud(i + 1);
        _targets.Clear();
        _numberHud = null;
        _talkHud = null;
        _talkShown = false;
    }
}
