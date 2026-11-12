using System.Diagnostics;

namespace SolvencyProof.Helpers;

/// <summary>
/// Pads public lookups to a minimum duration so that response time does not reveal
/// whether an identifier exists.
/// </summary>
public static class ResponseTiming
{
    public static readonly TimeSpan PublicLookupMinimum = TimeSpan.FromMilliseconds(400);

    public static async Task EnsureMinimumAsync(Stopwatch stopwatch, TimeSpan minimum, CancellationToken cancellationToken = default)
    {
        TimeSpan remaining = minimum - stopwatch.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }
}
