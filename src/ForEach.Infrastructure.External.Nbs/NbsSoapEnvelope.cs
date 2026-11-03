using System.Globalization;
using System.Xml.Linq;

namespace ForEach.Infrastructure.External.Nbs;

/// <summary>
/// Building the request and unwrapping the answer. Static and pure, so both halves of the
/// conversation can be tested against recorded XML with no server involved.
///
/// **Why by hand rather than a generated proxy.** Every NBS service ships in twin variants: one
/// returns a .NET `DataSet` — inline schema plus diffgram, awkward anywhere outside .NET Framework
/// — and one returns the same document as a plain string. Generated proxies bind to the DataSet
/// twin and produce types nobody wants to own; the string twin is an envelope and an
/// `XDocument.Parse`, which is this file.
///
/// The one wrinkle it exists to hide: the service returns its payload **XML-escaped inside**
/// `{Operation}Result`, so the response is a document containing a document, and the inner one has
/// to be parsed separately.
/// </summary>
public static class NbsSoapEnvelope
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Nbs = NbsError.Namespace;

    /// <summary>The SOAPAction header one operation needs. SOAP 1.1 requires it and NBS enforces it.</summary>
    public static string SoapAction(string operation) => $"{NbsError.Namespace}/{operation}";

    /// <summary>
    /// The full request for one operation.
    ///
    /// Parameters are ordered — the binding is document/literal WRAPPED, so the elements must
    /// appear in the order the WSDL declares, and a dictionary would lose that. They are passed as
    /// name/value pairs rather than a typed object per operation because every NBS input is a
    /// string or a number, and one envelope builder for eighteen operations is the whole point.
    ///
    /// A null value is written as `xsi:nil`, which is NBS's "ignore this criterion" — every
    /// parameter is a filter, and an omitted element is not the same as a nil one.
    /// </summary>
    public static string Build(
        string operation,
        NbsCredentials credentials,
        params (string Name, string? Value)[] parameters)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var body = new XElement(Nbs + operation,
            parameters.Select(p => p.Value is null
                ? new XElement(Nbs + p.Name, new XAttribute(xsi + "nil", "true"))
                : new XElement(Nbs + p.Name, p.Value)));

        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
            new XElement(Soap + "Header",
                new XElement(Nbs + "AuthenticationHeader",
                    new XElement(Nbs + "UserName", credentials.UserName),
                    new XElement(Nbs + "Password", credentials.Password),
                    new XElement(Nbs + "LicenceID", credentials.LicenceId))),
            new XElement(Soap + "Body", body));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), envelope).ToString();
    }

    /// <summary>
    /// The inner document a successful call returned, or the fault it answered with.
    ///
    /// Faults are checked FIRST and independently of the HTTP status. NBS answers a fault with
    /// 500, so a client that branches on the status code and only then looks for a fault reads the
    /// same way — but a client that trusts a 200 to mean success would miss one, and the ordering
    /// here means the parse never depends on the transport agreeing.
    /// </summary>
    public static NbsResult<string> ReadResult(string responseXml, string operation)
    {
        if (NbsError.TryRead(responseXml) is { } error) return NbsResult<string>.Fail(error);

        XDocument document;

        try
        {
            document = XDocument.Parse(responseXml);
        }
        catch (System.Xml.XmlException ex)
        {
            return NbsResult<string>.Transport($"The response was not XML: {ex.Message}");
        }

        var result = document
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == operation + "Result");

        // No result element and no fault is a shape nobody has documented — an empty body, an
        // HTML error page that happened to parse, a service renamed underneath us. Reported as a
        // transport failure so it is retried rather than recorded as data.
        return result is null
            ? NbsResult<string>.Transport($"The response carried no {operation}Result element.")
            : NbsResult<string>.Ok(result.Value);
    }

    /// <summary>
    /// A date as NBS wants it on the way IN: `yyyyMMdd`, always a string, never a `xs:date`.
    ///
    /// Invariant culture explicitly. The application it was extracted from runs under a Serbian
    /// culture, where the default formatting of a date is neither the shape nor the calendar this
    /// needs — and a library is used by hosts whose culture it cannot see.
    /// </summary>
    public static string Date(DateOnly date) =>
        date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
