// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// Acceptance-evidence generator: drives AdsStageProjectPatcher against a COPY of the real
// SDK Bundle Stage templates and emits the contributed files so review can diff exactly what
// a HarmonyOS build would ship.
//
//   dotnet run --project Packages/com.xengine.ads/Build~/StageContributionEvidence -- \
//     <stage-template-root> <output-markdown>
//
// <stage-template-root> is the SDK Bundle's templates/stage-player directory.

using System.Text;

using XEngine.Ads.Editor.Build;

string templateRoot = args.Length > 0 ? args[0]
    : throw new ArgumentException("pass the SDK Bundle templates/stage-player root");
string outputMarkdown = args.Length > 1 ? args[1] : "stage-contribution-evidence.md";

string workRoot = Path.Combine(Path.GetTempPath(), "xengine-ads-evidence-" + Guid.NewGuid().ToString("N"));
string packageRoot = Path.GetFullPath(args.Length > 2 ? args[2]
    : Path.Combine(Environment.CurrentDirectory, "Packages", "com.xengine.ads"));
if (!Directory.Exists(Path.Combine(packageRoot, "Native", "glue")))
    throw new ArgumentException($"package root with Native/glue not found at {packageRoot}");

var markdown = new StringBuilder();
markdown.AppendLine("# com.xengine.ads — Stage contribution evidence (2026-09-17)");
markdown.AppendLine();
markdown.AppendLine($"Template source: `{templateRoot}` (real SDK Bundle stage-player templates).");
markdown.AppendLine();

string huaweiStage = Path.Combine(workRoot, "huawei", "stage");
CopyDirectory(templateRoot, huaweiStage);
var huaweiErrors = new List<string>();
AdsStageProjectPatcher.Apply(huaweiStage, packageRoot, new AdsBuildConfig
{
    ProviderId = "huawei-adskit",
    UseTestAds = true,
    VendorAppId = "110xxxxx",
    RewardVideoSlotId = "testb4zuon2bty",
    InterstitialSlotId = "testb4zcw8ocpa",
}, huaweiErrors);
AppendContribution(markdown, "Huawei Ads Kit (huawei-adskit)", huaweiStage, huaweiErrors);

string pangleStage = Path.Combine(workRoot, "pangle", "stage");
CopyDirectory(templateRoot, pangleStage);
string fakeHar = Path.Combine(workRoot, "openadsdk.har");
File.WriteAllBytes(fakeHar, [0x50, 0x4b, 0x03, 0x04]);
var pangleErrors = new List<string>();
AdsStageProjectPatcher.Apply(pangleStage, packageRoot, new AdsBuildConfig
{
    ProviderId = "pangle",
    UseTestAds = true,
    VendorAppId = "pg-app",
    RewardVideoSlotId = "pg-reward",
    InterstitialSlotId = "pg-inter",
    HarSourcePath = fakeHar,
}, pangleErrors);
AppendContribution(markdown, "Pangle (pangle)", pangleStage, pangleErrors);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMarkdown))!);
File.WriteAllText(outputMarkdown, markdown.ToString(), new UTF8Encoding(false));
Console.WriteLine("evidence written: " + Path.GetFullPath(outputMarkdown));

static void AppendContribution(StringBuilder markdown, string title, string stage, List<string> errors)
{
    markdown.AppendLine($"## {title}");
    markdown.AppendLine();
    markdown.AppendLine("Contribution errors: " + (errors.Count == 0 ? "none" : string.Join("; ", errors)));
    markdown.AppendLine();

    markdown.AppendLine("### Files added/changed by the contributor");
    markdown.AppendLine();
    markdown.AppendLine("```");
    foreach (string relative in new[]
             {
                 "entry/src/main/ets/xengineads/XengineAdsHuawei.ets",
                 "entry/src/main/ets/xengineads/XengineAdsPangle.ets",
                 "entry/src/main/ets/entryability/EntryAbility.ets",
                 "entry/src/main/module.json5",
                 "entry/oh-package.json5",
                 "entry/libs/openadsdk.har",
                 "entry/src/main/resources/resfile/Settings/Ads.json",
             })
    {
        string path = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
        markdown.AppendLine((File.Exists(path) ? "[x] " : "[ ] ") + relative);
    }
    markdown.AppendLine("```");
    markdown.AppendLine();

    string entryAbility = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "ets", "entryability", "EntryAbility.ets"));
    markdown.AppendLine("### EntryAbility.ets (contributed)");
    markdown.AppendLine();
    markdown.AppendLine("```ts");
    markdown.AppendLine(entryAbility.TrimEnd());
    markdown.AppendLine("```");
    markdown.AppendLine();

    string moduleJson = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "module.json5"));
    markdown.AppendLine("### module.json5 (permissions injected)");
    markdown.AppendLine();
    markdown.AppendLine("```json5");
    markdown.AppendLine(moduleJson.TrimEnd());
    markdown.AppendLine("```");
    markdown.AppendLine();

    string payloadPath = Path.Combine(stage, "entry", "src", "main", "resources", "resfile", "Settings", "Ads.json");
    if (File.Exists(payloadPath))
    {
        markdown.AppendLine("### Settings/Ads.json (runtime payload)");
        markdown.AppendLine();
        markdown.AppendLine("```json");
        markdown.AppendLine(File.ReadAllText(payloadPath).TrimEnd());
        markdown.AppendLine("```");
        markdown.AppendLine();
    }

    string ohPackagePath = Path.Combine(stage, "entry", "oh-package.json5");
    if (File.ReadAllText(ohPackagePath).Contains("openadsdk", StringComparison.Ordinal))
    {
        markdown.AppendLine("### entry/oh-package.json5 (har dependency injected)");
        markdown.AppendLine();
        markdown.AppendLine("```json5");
        markdown.AppendLine(File.ReadAllText(ohPackagePath).TrimEnd());
        markdown.AppendLine("```");
        markdown.AppendLine();
    }
}

static void CopyDirectory(string source, string target)
{
    foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
    }
    foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
    {
        string destination = file.Replace(source, target, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }
}
