using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Palabras.Tests;

// The build identity is stamped by Palabras.csproj and -p:CommitSha. These tests pin the degradation
// rule: a value that was not supplied (or cannot be a commit) is "unknown", never blank and never a
// plausible-looking sha that would misreport what is deployed.
//
// Version and milestone are *not* repeated here: Backend/Palabras/Palabras.csproj is the only place
// that defines them (it says so itself), so the tests read that file and compare it with the assembly
// metadata the build produced and with what /healthz serves. A version or milestone bump is therefore
// one edit in the csproj; a stamp that is missing, malformed or disagrees still goes red.
public sealed class BuildIdentityTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("unknown", "unknown")]
    [InlineData("main", "unknown")]
    [InlineData("not-a-commit-sha", "unknown")]
    [InlineData("123456", "unknown")] // shorter than the shortest displayable sha
    [InlineData("3a6b4c9", "3a6b4c9")]
    [InlineData("3A6B4C9", "3a6b4c9")]
    [InlineData("0303a87f0d3c4e4f0a5b9c8d7e6f5a4b3c2d1e0f", "0303a87f0d3c4e4f0a5b9c8d7e6f5a4b3c2d1e0f")]
    [InlineData("0303a87f0d3c4e4f0a5b9c8d7e6f5a4b3c2d1e0f0", "unknown")] // one char past the full sha
    public void CommitIsReportedAsUnknownUnlessItCanBeACommit(string? supplied, string expected)
    {
        Assert.Equal(expected, BuildIdentity.CommitOrUnknown(supplied));
    }

    [Fact]
    public void VersionIsTheProjectVersionWithoutAShaSuffix()
    {
        var declared = ProjectProperty("Version");
        var identity = IdentityOfPalabras();

        // <Version> is the single release declaration; /healthz and the Ajustes screen must serve it
        // verbatim. The informational version must not leak a "+sha" suffix into the health body the
        // deploy poll and the settings screen read.
        AssertLooksLikeAReleaseVersion(declared);
        Assert.NotEqual(BuildIdentity.Unknown, identity.Version);
        Assert.Equal(declared.Trim(), identity.Version);
        Assert.DoesNotContain("+", identity.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void MilestoneComesFromTheProjectFile()
    {
        var declared = ProjectProperty("Milestone");
        var identity = IdentityOfPalabras();

        // <Milestone> in Palabras.csproj is the single definition; the assembly metadata stamped from
        // it must be the same value and must never degrade to "unknown" in a real build.
        Assert.False(string.IsNullOrWhiteSpace(declared));
        Assert.NotEqual(BuildIdentity.Unknown, declared.Trim());
        Assert.Equal(declared.Trim(), identity.Milestone);
    }

    [Fact]
    public void ShortCommitIsEitherUnknownOrTheShaPrefix()
    {
        var identity = IdentityOfPalabras();

        if (identity.Commit == BuildIdentity.Unknown)
        {
            Assert.Equal(BuildIdentity.Unknown, identity.CommitShort);
        }
        else
        {
            Assert.Equal(identity.Commit[..7], identity.CommitShort);
        }
    }

    [Fact]
    public void MissingAssemblyMetadataDegradesToUnknown()
    {
        // An assembly without the two AssemblyMetadata attributes is what a build that never
        // received them looks like; it must not throw or invent a commit, milestone or sha.
        var identity = BuildIdentity.FromAssembly(typeof(string).Assembly);

        Assert.Equal(BuildIdentity.Unknown, identity.Commit);
        Assert.Equal(BuildIdentity.Unknown, identity.CommitShort);
        Assert.Equal(BuildIdentity.Unknown, identity.Milestone);
        Assert.False(string.IsNullOrWhiteSpace(identity.Version));
        Assert.NotEqual(BuildIdentity.Unknown, identity.Version);
    }

    [Fact]
    public async Task HealthzServesTheStampedIdentity()
    {
        // The runtime answer, not just the object the tests built: a Program change that served a
        // literal (or stale value) would disagree with the assembly metadata and the project file.
        // The full field names/order contract lives in M0ApiTests.Healthz*.
        var identity = IdentityOfPalabras();
        using var factory = new IdentityHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = body.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(identity.Version, root.GetProperty("version").GetString());
        Assert.Equal(identity.Commit, root.GetProperty("commit").GetString());
        Assert.Equal(identity.CommitShort, root.GetProperty("commitShort").GetString());
        Assert.Equal(identity.Milestone, root.GetProperty("milestone").GetString());
        // The served version is also the project-file version, so "metadata and /healthz agree with
        // each other but both drifted from the csproj" cannot pass.
        Assert.Equal(ProjectProperty("Version").Trim(), root.GetProperty("version").GetString());
    }

    private static BuildIdentity IdentityOfPalabras() => BuildIdentity.FromAssembly(typeof(BuildIdentity).Assembly);

    private static string ProjectProperty(string name)
    {
        var values = ProjectFile().Descendants()
            .Where(element => element.Name.LocalName == name && element.Parent?.Name.LocalName == "PropertyGroup")
            .Select(element => element.Value)
            .ToArray();
        // Exactly one definition: a conditional or duplicated version/milestone would mean the
        // effective value depends on how the build was invoked, which is not a single source.
        Assert.True(values.Length == 1, $"Expected exactly one <{name}> in Palabras.csproj, found {values.Length}.");
        return values[0];
    }

    private static XDocument ProjectFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Backend", "Palabras", "Palabras.csproj");
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Cannot locate Backend/Palabras/Palabras.csproj, the single source of the build identity.");
    }

    private static void AssertLooksLikeAReleaseVersion(string declared)
    {
        // A normal release line, optionally pre-release ("0.8.0-rc.1"), but never build metadata:
        // the informational version is served verbatim and a "+suffix" would blur it with the commit.
        Assert.Matches(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$", declared.Trim());
    }

    // /healthz is public and needs no SPA or auth; an isolated SQLite file keeps the check off the
    // developer's database. Deliberately smaller than M0ApiTests' SPA-facing factory.
    private sealed class IdentityHostFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Palabras.Tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_root, "palabras.db")}");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
            builder.UseSetting("https_port", "443");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
