using System.Xml.Linq;

namespace ForEach.Infrastructure.External.Nbs;

/// <summary>
/// What NBS says when a call fails.
///
/// Every failure is HTTP 500 with a SOAP fault carrying this structure in `detail/ErrorInfo` —
/// including an unauthenticated call, which is how the shape was confirmed against the live
/// service rather than only read in the documentation.
/// </summary>
/// <param name="Code">
/// The numeric code. Kept as a number rather than only as an enum because NBS can add codes, and
/// a caller that switches on a parsed enum must still be able to report one it does not know.
/// </param>
/// <param name="Type">NBS's name for the error type.</param>
/// <param name="Message">The service's own message.</param>
public sealed record NbsError(int Code, string Type, string Message)
{
    /// <summary>The element namespace every NBS service uses, for header, body and results alike.</summary>
    public const string Namespace = "http://communicationoffice.nbs.rs";

    /// <summary>
    /// What the caller should DO about it, which is the only distinction worth making in code.
    ///
    /// The seven codes fall into three groups and they want three different responses: a
    /// configuration problem that will repeat until somebody fixes it, a transient backend failure
    /// that the next scheduled run will probably clear, and a caller bug that must fail loudly
    /// rather than be swallowed as "the service was down".
    /// </summary>
    public NbsFailureKind Kind => Code switch
    {
        10 or 50 or 60 or 70 => NbsFailureKind.Configuration,
        30 or 40 => NbsFailureKind.Transient,
        20 => NbsFailureKind.CallerMistake,

        // An unknown code is treated as transient on purpose: retrying something that will never
        // work costs one scheduled run, while giving up on something that would have worked costs
        // the data until somebody notices.
        _ => NbsFailureKind.Transient
    };

    /// <summary>The fault as one line: code, type and message.</summary>
    public override string ToString() => $"NBS {Code} {Type}: {Message}";

    /// <summary>
    /// Reads the fault out of a SOAP response body, or null when the body is not a fault.
    ///
    /// Namespace-agnostic on the ErrorInfo element: SOAP 1.1 puts `detail` outside any namespace
    /// and the service's own detail content has been seen unqualified, but a serialiser change
    /// that qualified it would otherwise turn a readable error into "the response could not be
    /// parsed" — which is the least useful thing a client can say about a failure.
    /// </summary>
    public static NbsError? TryRead(string responseXml)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(responseXml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var info = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");

        if (info is null) return null;

        var code = Child(info, "ErrorCode");

        return new NbsError(
            int.TryParse(code, out var parsed) ? parsed : 0,
            Child(info, "ErrorType") ?? "UnknownError",
            Child(info, "ErrorMessage") ?? "");
    }

    private static string? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
}

/// <summary>How a caller should respond to a failure. See <see cref="NbsError.Kind"/>.</summary>
public enum NbsFailureKind
{
    /// <summary>Credentials, licence or quota. It will repeat until a person changes something.</summary>
    Configuration,

    /// <summary>An NBS-side failure. The next run will probably succeed.</summary>
    Transient,

    /// <summary>The parameters were wrong. A bug here, not there.</summary>
    CallerMistake
}
