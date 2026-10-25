using System;
using System.IO;

namespace XEngine.Runtime.Test.Video;

internal static class VideoTestEvidence
{
    internal static string Root
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("XENGINE_TEST_VIDEO_EVIDENCE_DIR")
                ?? Path.Combine(RepoRoot.Find(), "docs", "superpowers", "evidence", "video");
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
