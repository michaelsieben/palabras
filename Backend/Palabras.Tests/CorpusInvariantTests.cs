using System.Text.Json.Nodes;
using Xunit;

namespace Palabras.Tests;

// Guards the shipped corpus by shape, not by size: these assertions keep holding when a content batch adds
// entries, and fail when a file stops being importable (bad id, wrong filename, a file the importer would
// silently skip). "The DB receives exactly one entry per file" lives in M2ContentImportTests.
public sealed class CorpusInvariantTests
{
    [Fact]
    public void EveryWordFileIsNamedAfterTheIdItDeclaresAndIdsAreUnique()
    {
        var files = M2ContentImportTests.CorpusFiles();
        var declaredIds = new List<string>();
        foreach (var path in files)
        {
            var json = JsonNode.Parse(File.ReadAllText(path));
            Assert.NotNull(json);
            var id = json["id"];
            Assert.NotNull(id);
            declaredIds.Add(id.GetValue<string>());
            Assert.Equal(id.GetValue<string>() + ".json", Path.GetFileName(path));
        }
        Assert.Equal(declaredIds.Count, declaredIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CorpusDirectoryHoldsOnlyJsonWordFilesAndTheReadme()
    {
        var unexpected = Directory.GetFiles(M2ContentImportTests.CorpusDirectory())
            .Select(path => Path.GetFileName(path))
            .Where(name => name != "README.md" && !name.StartsWith('.') && !name.EndsWith(".json", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(unexpected);
    }
}
