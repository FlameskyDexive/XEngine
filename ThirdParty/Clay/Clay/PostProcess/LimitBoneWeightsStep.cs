// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Clay.Importer;
using XEngine.Clay.Internal.Intermediate;

namespace XEngine.Clay.PostProcess;

/// <summary>
/// Truncates each vertex's bone-influence list to the top-N entries by weight, then renormalises
/// the kept weights so they sum to 1.
/// </summary>
/// <remarks>
/// glTF allows up to 8 influences (JOINTS_0 + JOINTS_1); FBX is unbounded. Game engines typically
/// upload 4 influences per vertex for skinning. After this step every vertex has at most
/// <see cref="ModelImporterSettings.BoneWeightLimit"/> influences.
/// </remarks>
internal sealed class LimitBoneWeightsStep : IPostProcess
{
    public PostProcessFlags Flag => PostProcessFlags.LimitBoneWeights;
    public string Name => "LimitBoneWeights";

    public void Execute(IntermediateScene scene, ImportContext context)
    {
        int limit = Math.Max(1, context.Settings.BoneWeightLimit);

        foreach (var mesh in scene.Meshes)
        {
            if (mesh.VertexJoints is null || mesh.VertexWeights is null)
                continue;

            int oldInfluences = mesh.MaxInfluencesPerVertex;
            int vertexCount = mesh.Positions.Count;
            if (oldInfluences <= limit)
            {
                // Sorted even though nothing is dropped, so BoneWeight.Index0 really is the strongest
                // influence its documentation promises. The common four-influence glTF case lands here.
                SortByWeightInPlace(mesh.VertexJoints, mesh.VertexWeights, vertexCount, oldInfluences);
                NormaliseInPlace(mesh.VertexWeights, vertexCount, oldInfluences);
                continue;
            }
            int[] newJoints = new int[vertexCount * limit];
            float[] newWeights = new float[vertexCount * limit];

            for (int v = 0; v < vertexCount; v++)
            {
                int srcBase = v * oldInfluences;
                int dstBase = v * limit;
                // Keep a sorted top-N directly in the destination. Scan the entire source
                // stride: a fixed scratch cap can discard the strongest authored influences.
                // Strict comparisons retain source order for equal weights.
                int last = dstBase + limit - 1;
                for (int k = 0; k < oldInfluences; k++)
                {
                    float weight = mesh.VertexWeights[srcBase + k];
                    if (!(weight > newWeights[last])) continue; // also ignore NaN, as the previous selector did.
                    int slot = last;
                    while (slot > dstBase && newWeights[slot - 1] < weight) slot--;
                    for (int m = last; m > slot; m--)
                    {
                        newWeights[m] = newWeights[m - 1];
                        newJoints[m] = newJoints[m - 1];
                    }
                    newWeights[slot] = weight;
                    newJoints[slot] = mesh.VertexJoints[srcBase + k];
                }

                // Renormalise.
                float sum = 0f;
                for (int s = 0; s < limit; s++)
                    sum += newWeights[dstBase + s];
                if (sum > 1e-6f)
                {
                    for (int s = 0; s < limit; s++)
                        newWeights[dstBase + s] /= sum;
                }
                // A vertex with no influence is left with none. Binding it to bone 0 would snap
                // genuinely unweighted geometry onto whichever joint happens to be listed first,
                // whereas zero weights are what a skinning shader reads as "not skinned" and falls
                // back to mesh local space for. The early-out path above leaves them alone too.
            }

            mesh.VertexJoints = newJoints;
            mesh.VertexWeights = newWeights;
            mesh.MaxInfluencesPerVertex = limit;
        }
    }

    /// <summary>Orders each vertex's influences strongest first, keeping joints and weights paired.</summary>
    private static void SortByWeightInPlace(int[] joints, float[] weights, int vertexCount, int influencesPerVertex)
    {
        // Insertion sort: influencesPerVertex is at most the bone weight limit, so a handful.
        for (int v = 0; v < vertexCount; v++)
        {
            int b = v * influencesPerVertex;
            for (int i = 1; i < influencesPerVertex; i++)
            {
                float w = weights[b + i];
                int j = joints[b + i];
                int k = i - 1;
                while (k >= 0 && weights[b + k] < w)
                {
                    weights[b + k + 1] = weights[b + k];
                    joints[b + k + 1] = joints[b + k];
                    k--;
                }
                weights[b + k + 1] = w;
                joints[b + k + 1] = j;
            }
        }
    }

    private static void NormaliseInPlace(float[] weights, int vertexCount, int influencesPerVertex)
    {
        for (int v = 0; v < vertexCount; v++)
        {
            int b = v * influencesPerVertex;
            float sum = 0f;
            for (int i = 0; i < influencesPerVertex; i++)
                sum += weights[b + i];
            if (sum > 1e-6f)
            {
                for (int i = 0; i < influencesPerVertex; i++)
                    weights[b + i] /= sum;
            }
        }
    }
}
