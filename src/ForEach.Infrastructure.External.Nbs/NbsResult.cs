namespace ForEach.Infrastructure.External.Nbs;

/// <summary>
/// The outcome of one NBS call: a value, or a reason there is none.
///
/// Its own type rather than FluentResults, which the host solution happens to use. The library
/// carries no third-party dependency at all, so a solution on a different result type — or on
/// exceptions — adapts it in a dozen lines instead of inheriting a package choice made elsewhere.
///
/// A failure is either an <see cref="NbsError"/> the service itself reported or a transport
/// problem that never reached it, and the two are kept apart: a caller deciding whether to retry
/// needs to know that a timeout is not a licence problem.
/// </summary>
public sealed class NbsResult<T>
{
    private NbsResult(T? value, NbsError? error, string? transportFailure)
    {
        Value = value;
        Error = error;
        TransportFailure = transportFailure;
    }

    /// <summary>What the call returned. Set only on success.</summary>
    public T? Value { get; }

    /// <summary>Set when NBS answered with a fault. Null on success and on transport failure.</summary>
    public NbsError? Error { get; }

    /// <summary>Set when the call never got an answer — timeout, DNS, TLS, a 500 with no fault.</summary>
    public string? TransportFailure { get; }

    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess => Error is null && TransportFailure is null;

    /// <summary>
    /// One sentence naming what went wrong, in the service's own words where there are any.
    ///
    /// The host writes this into a job-run record, so it is worth being the server's message
    /// rather than a paraphrase — an operator reading "Invalid user name or password!!!" knows
    /// what to do, and "the rate import failed" does not.
    /// </summary>
    public string FailureMessage =>
        Error?.ToString() ?? TransportFailure ?? "";

    /// <summary>Retrying is worth it only for a transport problem or an NBS-side one.</summary>
    public bool IsWorthRetrying =>
        TransportFailure is not null || Error?.Kind == NbsFailureKind.Transient;

    /// <summary>A call that succeeded.</summary>
    public static NbsResult<T> Ok(T value) => new(value, null, null);

    /// <summary>A call NBS answered with a fault.</summary>
    public static NbsResult<T> Fail(NbsError error) => new(default, error, null);

    /// <summary>A call that never got an answer.</summary>
    public static NbsResult<T> Transport(string message) => new(default, null, message);
}
