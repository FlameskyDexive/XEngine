using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XEngine.Editor.Utils;
using Xunit;

namespace XEngine.Editor.Test;

public class LocaleCatalogTests
{
    [Fact]
    public void EverySupportedLocale_HasAllKeysAndValidPlaceholders()
    {
        var english = Read("en");
        foreach (string locale in LocaleHelper.Codes)
        {
            var actual = Read(locale);
            Assert.Equal(english.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
            foreach (var pair in english)
            {
                string translated = actual[pair.Key];
                Assert.False(string.IsNullOrWhiteSpace(translated), $"{locale}/{pair.Key}");
                Assert.DoesNotContain('\uFFFD', translated);
                Assert.Equal(Placeholders(pair.Value), Placeholders(translated));
            }
        }
    }

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{+\w+\}+").Select(m => m.Value).OrderBy(v => v).ToArray();

    private static Dictionary<string, string> Read(string locale)
    {
        using var stream = typeof(LocaleHelper).Assembly.GetManifestResourceStream(
            $"XEngine.Editor.Resources.Locale.{locale}.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())!;
    }
}
