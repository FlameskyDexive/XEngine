// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime.Audio;

/// <summary>Clamps a decibel value into the mixer's usable range.</summary>
/// <remarks>
/// Managed-first mixer (prowl-sync M6.2, from the upstream preview-4 audio overhaul direction):
/// group hierarchy, volume/mute/solo and named snapshots as a serializable asset. v1 applies group
/// gains through each AudioSource's sound-group volume (backend-agnostic managed math); per-group
/// native DSP nodes and peak metering are deferred until the backends expose a shared node graph.
/// </remarks>
public static class AudioMixerMath
{
    public const float MinVolumeDB = -80.0f;
    public const float MaxVolumeDB = 20.0f;

    public static float ClampDB(float db) => Math.Clamp(db, MinVolumeDB, MaxVolumeDB);

    public static float DecibelsToLinear(float decibels)
        => MathF.Pow(10f, ClampDB(decibels) / 20f);

    public static float LinearToDecibels(float linear)
        => linear <= 0f ? MinVolumeDB : Math.Clamp(20f * MathF.Log10(linear), MinVolumeDB, MaxVolumeDB);
}

/// <summary>A single group in the mixer hierarchy: persistent id, name, parent, volume, mute and solo flags.</summary>
public class AudioMixerGroupData
{
    /// <summary>Persistent identity that survives renames. Empty on legacy assets; minted on load and
    /// persisted on the next editor save.</summary>
    public string Id = "";
    public string Name = "";
    /// <summary>Parent group name; empty when the group hangs directly under the mixer output.</summary>
    public string Parent = "";
    public float VolumeDB;
    public bool Mute;
    public bool Solo;
}

/// <summary>A named set of per-group volume overrides that can be activated as one (a "mix scene").</summary>
public class AudioMixerSnapshotData
{
    public string Name = "";
    /// <summary>Group name → VolumeDB. Groups not listed keep their own VolumeDB.</summary>
    public List<AudioMixerSnapshotVolume> Volumes = new();
}

public class AudioMixerSnapshotVolume
{
    public string Group = "";
    public float VolumeDB;
}

/// <summary>
/// A mixer asset: a hierarchy of groups with volume/mute/solo, plus named snapshots for switching
/// between mix states. AudioSources reference the mixer and a group by name; their effective volume
/// is their own volume times the group chain's linear gain (muted or solo-silenced chains contribute
/// zero).
/// </summary>
[CreateAssetMenu("Audio Mixer", Extension = ".audiomixer", Icon = "\uf028", Order = 30)]
public class AudioMixer : EngineObject, ISerializable
{
    public const string MasterGroupName = "Master";

    public List<AudioMixerGroupData> Groups = new() { new AudioMixerGroupData { Name = MasterGroupName } };
    public List<AudioMixerSnapshotData> Snapshots = new();
    public string ActiveSnapshot = "";

    private bool _anySolo;

    /// <summary>The group every unparented group hangs under, and the mix output.</summary>
    public AudioMixerGroupData Master
    {
        get
        {
            EnsureNotDisposed();
            var master = FindGroup(MasterGroupName);
            if (master != null) return master;
            master = new AudioMixerGroupData { Name = MasterGroupName };
            Groups.Insert(0, master);
            return master;
        }
    }

    /// <summary>True when any group is soloed: non-soloed groups are then silenced.</summary>
    public bool AnySolo
    {
        get
        {
            EnsureNotDisposed();
            foreach (var g in Groups)
                if (g.Solo) return true;
            return false;
        }
    }

    /// <summary>Linear gain contributed by the named group chain, product of ancestors and itself,
    /// honouring mute and solo silencing. Unknown group names contribute the Master chain.</summary>
    public float GetGroupLinearVolume(string groupName)
    {
        EnsureNotDisposed();

        float gain = 1f;
        var chain = new List<AudioMixerGroupData>();
        var current = FindGroup(groupName) ?? FindGroup(MasterGroupName);
        while (current != null && !chain.Contains(current))
        {
            chain.Insert(0, current);
            current = FindGroup(current.Parent);
        }

        bool anySolo = AnySolo;
        foreach (var group in chain)
        {
            if (group.Mute) return 0f;
            if (anySolo && !group.Solo && !IsAncestorOfSoloed(group)) return 0f;
            gain *= AudioMixerMath.DecibelsToLinear(group.VolumeDB);
        }
        return gain;
    }

    /// <summary>Silenced = any group is soloed but this group (or a descendant of it) is not soloed.</summary>
    private bool IsAncestorOfSoloed(AudioMixerGroupData group)
    {
        foreach (var g in Groups)
        {
            if (!g.Solo) continue;
            var walk = g;
            while (!string.IsNullOrEmpty(walk.Parent))
            {
                if (walk.Parent == group.Name) return true;
                var parent = FindGroup(walk.Parent);
                if (parent == null) break;
                walk = parent;
            }
        }
        return false;
    }

    public AudioMixerGroupData? FindGroup(string name)
    {
        EnsureNotDisposed();
        foreach (var g in Groups)
            if (g.Name == name) return g;
        return null;
    }

    public AudioMixerGroupData AddGroup(string name, string parent = "")
    {
        EnsureNotDisposed();
        var group = new AudioMixerGroupData { Id = NewGroupId(), Name = name, Parent = parent };
        Groups.Add(group);
        return group;
    }

    /// <summary>Mints a fresh persistent group id (used on creation and legacy-asset migration).</summary>
    public static string NewGroupId() => Guid.NewGuid().ToString("N");

    /// <summary>Assigns ids to every group missing one (legacy assets). Idempotent.</summary>
    public void EnsureGroupIds()
    {
        EnsureNotDisposed();
        foreach (var g in Groups)
            if (string.IsNullOrEmpty(g.Id))
                g.Id = NewGroupId();
    }

    /// <summary>Activates a snapshot by name: copies its per-group VolumeDB overrides onto the
    /// groups. Returns false when the snapshot does not exist.</summary>
    public bool ApplySnapshot(string snapshotName)
    {
        EnsureNotDisposed();
        foreach (var snapshot in Snapshots)
        {
            if (snapshot.Name != snapshotName) continue;
            foreach (var volume in snapshot.Volumes)
            {
                var group = FindGroup(volume.Group);
                if (group != null) group.VolumeDB = AudioMixerMath.ClampDB(volume.VolumeDB);
            }
            ActiveSnapshot = snapshotName;
            return true;
        }
        return false;
    }

    public void Serialize(ref EchoObject compound, SerializationContext ctx)
    {
        compound.Add("ActiveSnapshot", new EchoObject(ActiveSnapshot));

        var groups = EchoObject.NewList();
        foreach (var g in Groups)
        {
            var tag = EchoObject.NewCompound();
            tag.Add("Id", new EchoObject(g.Id));
            tag.Add("Name", new EchoObject(g.Name));
            tag.Add("Parent", new EchoObject(g.Parent));
            tag.Add("VolumeDB", new EchoObject(g.VolumeDB));
            tag.Add("Mute", new EchoObject(g.Mute));
            tag.Add("Solo", new EchoObject(g.Solo));
            groups.ListAdd(tag);
        }
        compound.Add("Groups", groups);

        var snapshots = EchoObject.NewList();
        foreach (var snapshot in Snapshots)
        {
            var tag = EchoObject.NewCompound();
            tag.Add("Name", new EchoObject(snapshot.Name));
            var volumes = EchoObject.NewList();
            foreach (var v in snapshot.Volumes)
            {
                var vt = EchoObject.NewCompound();
                vt.Add("Group", new EchoObject(v.Group));
                vt.Add("VolumeDB", new EchoObject(v.VolumeDB));
                volumes.ListAdd(vt);
            }
            tag.Add("Volumes", volumes);
            snapshots.ListAdd(tag);
        }
        compound.Add("Snapshots", snapshots);
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        ActiveSnapshot = value.Get("ActiveSnapshot")?.StringValue ?? "";
        Groups.Clear();
        if (value.Get("Groups") is { } groups && groups.TagType == EchoType.List)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                var tag = groups[i];
                Groups.Add(new AudioMixerGroupData
                {
                    Id = tag.Get("Id")?.StringValue ?? "",
                    Name = tag.Get("Name")?.StringValue ?? "",
                    Parent = tag.Get("Parent")?.StringValue ?? "",
                    VolumeDB = tag.Get("VolumeDB")?.FloatValue ?? 0f,
                    Mute = tag.Get("Mute")?.BoolValue ?? false,
                    Solo = tag.Get("Solo")?.BoolValue ?? false,
                });
            }
        }
        if (Groups.Count == 0) Groups.Add(new AudioMixerGroupData { Name = MasterGroupName });
        EnsureGroupIds();

        Snapshots.Clear();
        if (value.Get("Snapshots") is { } snapshots && snapshots.TagType == EchoType.List)
        {
            for (int i = 0; i < snapshots.Count; i++)
            {
                var tag = snapshots[i];
                var snapshot = new AudioMixerSnapshotData { Name = tag.Get("Name")?.StringValue ?? "" };
                if (tag.Get("Volumes") is { } volumes && volumes.TagType == EchoType.List)
                {
                    for (int v = 0; v < volumes.Count; v++)
                    {
                        var vt = volumes[v];
                        snapshot.Volumes.Add(new AudioMixerSnapshotVolume
                        {
                            Group = vt.Get("Group")?.StringValue ?? "",
                            VolumeDB = vt.Get("VolumeDB")?.FloatValue ?? 0f,
                        });
                    }
                }
                Snapshots.Add(snapshot);
            }
        }
    }
}
