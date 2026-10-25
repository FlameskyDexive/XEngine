using System;

using XEngine.Editor;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine.VoxelWorld;

namespace XEngine.VoxelWorld.Editor;

public static class VoxelWorldMenus
{
    [MenuItem("GameObject/Voxel World/Voxel World", priority: 45, Icon = EditorIcons.Cube)]
    public static void MenuCreateHost() => CreateHostObject();

    [MenuItem("GameObject/Voxel World/Flat Playable", priority: 46, Icon = EditorIcons.Mountain)]
    public static void MenuCreateFlat() => CreateFlatPlayableObject();

    [MenuItem("GameObject/Voxel World/Earth Playable", priority: 47, Icon = EditorIcons.Globe)]
    public static void MenuCreateEarth() => CreateEarthPlayableObject();

    [MenuItem("GameObject/Voxel World/First Person", priority: 48, Icon = EditorIcons.PersonWalking)]
    public static void MenuCreateFirstPerson() => CreateFirstPersonObject();

    public static GameObject CreateHostObject(string name = "Voxel World")
    {
        GameObject go = Create("Voxel World", name);
        go.AddComponent<VoxelWorld>();
        return go;
    }

    public static GameObject CreateFlatPlayableObject(string name = "Flat World")
    {
        GameObject go = Create("Flat World", name);
        var world = go.AddComponent<VoxelWorld>();
        world.EnableStreaming = true;
        world.UseEarthGenerator = false;
        world.ViewDistance = 2;
        AttachPlayer(go);
        return go;
    }

    public static GameObject CreateEarthPlayableObject(string name = "Earth World")
    {
        GameObject go = Create("Earth World", name);
        var world = go.AddComponent<VoxelWorld>();
        world.EnableStreaming = true;
        world.UseEarthGenerator = true;
        world.ViewDistance = 3;
        world.Focus = new Float3(8f, 50f, 8f);
        go.AddComponent<VoxelDayCycle>();
        AttachPlayer(go);
        return go;
    }

    public static GameObject CreateFirstPersonObject(string name = "Player")
    {
        GameObject go = Create("Player", name);
        go.AddComponent<CharacterController>();
        go.AddComponent<VoxelCharacterView>();
        go.AddComponent<VoxelFirstPerson>();
        return go;
    }

    private static void AttachPlayer(GameObject world)
    {
        GameObject player = new("Player");
        player.SetParent(world, worldPositionStays: false);
        player.Transform.LocalPosition = new Float3(8f, 52f, 8f);
        player.AddComponent<CharacterController>();
        player.AddComponent<VoxelCharacterView>();
        var fp = player.AddComponent<VoxelFirstPerson>();
        fp.World = world.GetComponent<VoxelWorld>();

        GameObject mob = new("DummyMob");
        mob.SetParent(world, worldPositionStays: false);
        mob.Transform.LocalPosition = new Float3(14f, 52f, 10f);
        var dummy = mob.AddComponent<VoxelDummyMob>();
        dummy.State.Position = mob.Transform.LocalPosition;
        fp.Mobs.Add(dummy.State);
    }

    private static GameObject Create(string hierarchyName, string name)
    {
        try
        {
            GameObject go = HierarchyPanel.CreateGameObject(hierarchyName, MenuContext.ActiveGameObject);
            go.Name = name;
            return go;
        }
        catch (Exception)
        {
            return new GameObject(name);
        }
    }
}
