using Xunit;

namespace Palabras.Tests;

// The build identity is stamped by Palabras.csproj and -p:CommitSha. These tests pin the degradation
// rule: a value that was not supplied (or cannot be a commit) is "unknown", never blank and never a
// plausible-looking sha that would misreport what is deployed.
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
    public void CommitIsReportedAsUnknownUnlessItCanBeACommit(string? supplied, string expected)
    {
        Assert.Equal(expected, BuildIdentity.CommitOrUnknown(supplied));
    }

    [Fact]
    public void VersionIsTheProjectVersionWithoutAShaSuffix()
    {
        var identity = BuildIdentity.FromAssembly(typeof(BuildIdentity).Assembly);

        // <Version>0.7.0</Version> in Palabras.csproj; the informational version must not leak a
        // "+sha" suffix into the health body the deploy poll and the settings screen read.
        Assert.Equal("0.7.0", identity.Version);
        Assert.DoesNotContain("+", identity.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void MilestoneComesFromTheProjectFile()
    {
        var identity = BuildIdentity.FromAssembly(typeof(BuildIdentity).Assembly);

        // <Milestone> in Palabras.csproj is the single definition; M7 is the milestone named in
        // docs/00-handoff.md §7 at the time this was wired.
        Assert.Equal("M7", identity.Milestone);
    }

    [Fact]
    public void ShortCommitIsEitherUnknownOrTheShaPrefix()
    {
        var identity = BuildIdentity.FromAssembly(typeof(BuildIdentity).Assembly);

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
}
