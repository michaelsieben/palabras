using System.Text.RegularExpressions;

namespace Palabras.Learn;

// Plan §4: the cloze text is derived server-side by replacing the headword match in
// example_es. A conjugated or inflected example has no exact match, and the card
// falls back to typed (risk R7). No dictionary, no lemmatiser.
internal static class ClozeDerivation
{
    private const string Blank = "____";

    internal static string? Derive(string? exampleEs, string? headword)
    {
        if (string.IsNullOrWhiteSpace(exampleEs) || string.IsNullOrWhiteSpace(headword))
        {
            return null;
        }
        // Letter-boundary lookarounds instead of \b: accents are letters here, and a
        // digit-adjacent headword must not match inside a longer token.
        var pattern = $@"(?<!\p{{L}}){Regex.Escape(headword.Trim())}(?!\p{{L}})";
        var match = Regex.Match(exampleEs, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? string.Concat(exampleEs.AsSpan(0, match.Index), Blank, exampleEs.AsSpan(match.Index + match.Length)) : null;
    }
}
