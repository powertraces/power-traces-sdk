using System.Diagnostics;
using PowerTraces.Diagnostics;

namespace PowerTraces;

/// <summary>Recording exceptions on spans so the dashboard can show where they happened.</summary>
public static class ActivityExceptionExtensions
{
    /// <summary>Records <paramref name="exception"/> on the span the way OpenTelemetry does — an
    /// <c>exception</c> event with <c>exception.type</c>, <c>exception.message</c> and
    /// <c>exception.stacktrace</c>, and the span's status set to Error — plus
    /// <c>exception.details</c>: the exception's chain (inner exceptions) with each stack frame,
    /// and for up to <paramref name="maxFrames"/> frames of the app's own code in each exception of
    /// the chain, a snapshot of the source around the line that ran (<paramref name="snapshotLines"/>
    /// lines before and after).
    ///
    /// <para>Source comes from the sources embedded in the app's PDB — set
    /// <c>&lt;EmbedAllSources&gt;true&lt;/EmbedAllSources&gt;</c> (and keep PDBs, portable or
    /// embedded, which is the default) so this works where the .cs files aren't deployed — or from
    /// the file on disk if it exists. Without either, frames are recorded without code. Note that
    /// the snapshot puts source code into the telemetry database.</para>
    ///
    /// <para>All the work happens here, on the failure path only; nothing runs for spans that
    /// don't fail.</para></summary>
    /// <returns><paramref name="activity"/>, for chaining. Does nothing when it is null.</returns>
    public static Activity? RecordExceptionWithSource(this Activity? activity, Exception exception, int snapshotLines = 5, int maxFrames = 3)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (activity is null)
        {
            return null;
        }

        var tags = new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name },
            { "exception.message", exception.Message },
            { "exception.stacktrace", exception.ToString() },
        };

        try
        {
            tags.Add(ExceptionDetails.AttributeKey, ExceptionDetails.FromException(exception, Math.Clamp(snapshotLines, 0, 50), Math.Clamp(maxFrames, 0, 20)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Telemetry must never turn one failure into two: the standard attributes are enough.
        }

        activity.AddEvent(new ActivityEvent("exception", DateTimeOffset.UtcNow, tags));
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        return activity;
    }
}
