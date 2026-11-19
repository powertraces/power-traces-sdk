using System.Diagnostics;

namespace PowerTraces;

/// <summary>Marking a span as failed when nothing threw — typically after reading a reply that
/// came back <c>200 OK</c> but says the operation didn't happen.</summary>
public static class ActivityFailureExtensions
{
    /// <summary>The reason given to <see cref="MarkFailed"/>.</summary>
    public const string ReasonAttribute = "powertraces.failure.reason";

    /// <summary>The optional machine-readable code given to <see cref="MarkFailed"/>.</summary>
    public const string CodeAttribute = "powertraces.failure.code";

    /// <summary>Who decided the span failed — <c>app</c> for <see cref="MarkFailed"/>.</summary>
    public const string SourceAttribute = "powertraces.failure.source";

    /// <summary>Marks the span as failed in the app's own words: its status becomes Error with
    /// <paramref name="reason"/> as the description, and <c>powertraces.failure.reason</c> (plus
    /// <c>powertraces.failure.code</c> when given) records why. The dashboard shows the reason in
    /// the trace's summary, and when the span made a call that answered with a success status just
    /// before, points at that reply as the cause.
    ///
    /// <code>
    /// var reply = await http.PostAsJsonAsync("/debit", request);
    /// var body = await reply.Content.ReadFromJsonAsync&lt;DebitResult&gt;();
    /// if (body is { IsSuccess: false })
    ///     Activity.Current.MarkFailed(body.Message, body.ErrorCode);
    /// </code></summary>
    /// <returns><paramref name="activity"/>, for chaining. Does nothing when it is null.</returns>
    public static Activity? MarkFailed(this Activity? activity, string reason, string? code = null)
    {
        if (activity is null)
        {
            return null;
        }

        var text = string.IsNullOrWhiteSpace(reason) ? "Failed" : reason.Trim();
        activity.SetTag(ReasonAttribute, text);
        activity.SetTag(SourceAttribute, "app");
        if (code is { } c && !string.IsNullOrWhiteSpace(c))
        {
            activity.SetTag(CodeAttribute, c.Trim());
        }

        activity.SetStatus(ActivityStatusCode.Error, text);
        return activity;
    }
}
