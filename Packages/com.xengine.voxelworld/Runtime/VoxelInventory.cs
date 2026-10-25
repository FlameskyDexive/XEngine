using System;

namespace XEngine.VoxelWorld;

public sealed class VoxelInventory
{
    public readonly ushort[] Types;
    public readonly int[] Counts;
    public int Selected;

    public VoxelInventory(int slots = 8)
    {
        if (slots <= 0)
            throw new ArgumentOutOfRangeException(nameof(slots));
        Types = new ushort[slots];
        Counts = new int[slots];
    }

    public ushort SelectedType => Selected >= 0 && Selected < Types.Length ? Types[Selected] : (ushort)0;

    public int Add(ushort type, int count = 1)
    {
        if (type == 0 || count <= 0)
            return 0;
        for (int i = 0; i < Types.Length; i++)
        {
            if (Types[i] == type && Counts[i] > 0)
            {
                Counts[i] += count;
                return i;
            }
        }

        for (int i = 0; i < Types.Length; i++)
        {
            if (Counts[i] == 0)
            {
                Types[i] = type;
                Counts[i] = count;
                return i;
            }
        }

        return -1;
    }

    public bool TryConsumeSelected()
    {
        if (Selected < 0 || Selected >= Counts.Length || Counts[Selected] <= 0 || Types[Selected] == 0)
            return false;
        Counts[Selected]--;
        if (Counts[Selected] == 0)
            Types[Selected] = 0;
        return true;
    }
}
