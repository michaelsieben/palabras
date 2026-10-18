using System.Reflection;

namespace Palabras;

// The deployed build's identity, read back from assembly metadata stamped at build time by
// Palabras.csproj (Version, Milestone) and -p:CommitSha=... (supplied by CI and the image build).
// Nothing here invents a value: a commit that was not supplied, or that is not a commit sha, is
// reported as "unknown" so a running container can never look like a build it is not.
internal sealed class BuildIdentity
{
    internal const string Unknown = "unknown";
    private const int ShortLength = 7;
    private const int FullLength = 40;

    private BuildIdentity(string version, string commit, string milestone)
    {
        Version = version;
        Commit = commit;
        Milestone = milestone;
    }

    internal string Version { get; }

    internal string Commit { get; }

    internal string CommitShort => Commit == Unknown ? Unknown : Commit[..ShortLength];

    internal string Milestone { get; }

    internal static BuildIdentity FromAssembly(Assembly assembly) => new(
        VersionOf(assembly),
        CommitOrUnknown(MetadataOf(assembly, "CommitSha")),
        MetadataOf(assembly, "Milestone"));

    // A supplied value only counts as a commit when it can be one: 7-40 hex characters. Anything
    // else ("", "unknown", a branch name) degrades to the explicit unknown marker.
    internal static string CommitOrUnknown(string? supplied)
    {
        var value = supplied?.Trim();
        if (value is null || value.Length is < ShortLength or > FullLength || !value.All(Uri.IsHexDigit))
        {
            return Unknown;
        }

        return value.ToLowerInvariant();
    }

    private static string VersionOf(Assembly assembly)
    {
        // The informational version is "0.7.0" and, with IncludeSourceRevisionInInformationalVersion
        // off, never carries a "+sha" suffix; split anyway so a stray suffix cannot reach the UI.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            var version = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (version.Length > 0)
            {
                return version;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null ? Unknown : assemblyVersion.ToString(3);
    }

    private static string MetadataOf(Assembly assembly, string key)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value
            ?.Trim();
        return string.IsNullOrEmpty(value) ? Unknown : value;
    }
}
