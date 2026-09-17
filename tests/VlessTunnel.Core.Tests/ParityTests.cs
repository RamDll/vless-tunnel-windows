using System.Text.Json;
using System.Text.Json.Nodes;
using VlessTunnel.Core;
using VlessTunnel.Core.Models;
using Xunit;

namespace VlessTunnel.Core.Tests;

/// <summary>
/// Golden-тесты паритета с Linux-бэкендом (план, 5.7): для каждой ссылки в
/// tests/parity/links.json C#-сборщик должен дать тот же outbound-блок
/// байт в байт (после нормализации JSON — сортировки ключей), что и
/// эталон (vless-tunnel.sh, py_backend, make). Эталонные JSON лежат рядом,
/// сгенерированы один раз на хосте из ~/src/vless-tunnel и закоммичены.
/// </summary>
public sealed class ParityTests
{
    private static string ParityDir => Path.Combine(AppContext.BaseDirectory, "parity");

    public sealed record LinkCase(string Name, string Link, string? OutboundFile, JsonElement? Options)
    {
        public override string ToString() => Name;
    }

    public sealed record BrokenCase(string Name, string Link, string ErrorContains)
    {
        public override string ToString() => Name;
    }

    private static (List<LinkCase> Valid, List<BrokenCase> Broken) LoadManifest()
    {
        var json = File.ReadAllText(Path.Combine(ParityDir, "links.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var valid = new List<LinkCase>();
        foreach (var e in root.GetProperty("valid").EnumerateArray())
        {
            valid.Add(new LinkCase(
                e.GetProperty("name").GetString()!,
                e.GetProperty("link").GetString()!,
                e.TryGetProperty("outbound_file", out var f) ? f.GetString() : null,
                e.TryGetProperty("options", out var o) ? o.Clone() : null));
        }

        var broken = new List<BrokenCase>();
        foreach (var e in root.GetProperty("broken").EnumerateArray())
        {
            broken.Add(new BrokenCase(
                e.GetProperty("name").GetString()!,
                e.GetProperty("link").GetString()!,
                e.GetProperty("error_contains").GetString()!));
        }

        return (valid, broken);
    }

    private static BuildOptions OptionsFrom(JsonElement? opts)
    {
        var visionUdp443 = true;
        if (opts is { } o && o.TryGetProperty("visionUdp443", out var v))
        {
            visionUdp443 = v.GetBoolean();
        }
        return new BuildOptions { VisionUdp443 = visionUdp443 };
    }

    private static JsonNode? Canon(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(
            obj.Select(kv => kv).OrderBy(kv => kv.Key, StringComparer.Ordinal)
               .Select(kv => KeyValuePair.Create(kv.Key, Canon(kv.Value?.DeepClone())))),
        JsonArray arr => new JsonArray(arr.Select(x => Canon(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private static string CanonString(JsonNode? node) =>
        Canon(node)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    public static IEnumerable<object[]> ValidCases() =>
        LoadManifest().Valid.Select(c => new object[] { c });

    public static IEnumerable<object[]> BrokenCases() =>
        LoadManifest().Broken.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void Outbound_matches_linux_reference(LinkCase testCase)
    {
        var parsed = LinkParser.Parse(testCase.Link);
        var options = OptionsFrom(testCase.Options);
        var outbound = OutboundBuilder.BuildOutbound(parsed, options);

        var expectedJson = File.ReadAllText(Path.Combine(ParityDir, testCase.OutboundFile!));
        var expected = JsonNode.Parse(expectedJson);

        Assert.Equal(CanonString(expected), CanonString(outbound));
    }

    [Theory]
    [MemberData(nameof(BrokenCases))]
    public void Broken_links_produce_clear_errors(BrokenCase testCase)
    {
        var ex = Assert.Throws<LinkParseException>(() => LinkParser.Parse(testCase.Link));
        Assert.Contains(testCase.ErrorContains, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
