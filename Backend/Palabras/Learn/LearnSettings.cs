using System.Globalization;
using System.Text.Json;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// user_settings is the only source of scheduler knobs; a missing row means the
// column defaults from the SrsCards migration, never a second hard-coded set.
internal static class LearnSettings
{
    internal static SchedulerOptions ToSchedulerOptions(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SchedulerOptions(
            settings.DesiredRetention,
            settings.MaxIntervalDays,
            settings.EnableFuzz,
            ParseSteps(settings.LearningSteps, [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)]),
            ParseSteps(settings.RelearningSteps, [TimeSpan.FromMinutes(10)]),
            ParseWeights(settings.WeightsJson),
            settings.WeightsVersion);
    }

    private static TimeSpan[] ParseSteps(string? json, TimeSpan[] fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json);
            if (values is not { Length: > 0 })
            {
                return fallback;
            }
            var steps = new TimeSpan[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                steps[index] = TimeSpan.Parse(values[index], CultureInfo.InvariantCulture);
            }
            return steps;
        }
        catch (JsonException)
        {
            return fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
        catch (OverflowException)
        {
            return fallback;
        }
    }

    private static double[]? ParseWeights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var weights = JsonSerializer.Deserialize<double[]>(json);
            return weights is { Length: > 0 } ? weights : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
