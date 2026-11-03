using System.Xml.Linq;
using ForEach.Infrastructure.External.Nbs;

namespace ForEach.Infrastructure.External.Nbs.UnitTests;

/// <summary>
/// The envelope NBS is sent, and the answer it sends back.
///
/// Both halves are static and pure so they can be held against recorded XML with no server. That
/// matters more here than usual: the service cannot be called at all until a signed paper request
/// has been posted to Belgrade and processed by hand, so for as long as that takes, these tests
/// are the only thing that says the integration is right.
/// </summary>
public class NbsSoapEnvelopeTests
{
    private static readonly NbsCredentials Credentials = new()
    {
        UserName = "hrc",
        Password = "tajna-lozinka",
        LicenceId = "3fa85f64-5717-4562-b3fc-2c963f66afa6"
    };

    [Fact]
    public void Every_call_carries_the_credentials_in_the_header()
    {
        // There is no handshake and no token: the same three values go on every request, and a
        // header that is merely absent produces error 70, which reads as "wrong password" rather
        // than "no header".
        var envelope = XDocument.Parse(NbsSoapEnvelope.Build(
            "GetExchangeRateByDate", Credentials, ("date", "20260820")));

        var header = envelope.Descendants()
            .Single(e => e.Name.LocalName == "AuthenticationHeader");

        Assert.Equal("hrc", Child(header, "UserName"));
        Assert.Equal("tajna-lozinka", Child(header, "Password"));
        Assert.Equal("3fa85f64-5717-4562-b3fc-2c963f66afa6", Child(header, "LicenceID"));

        // The namespace is the same one every element in the conversation uses, header included.
        Assert.Equal("http://communicationoffice.nbs.rs", header.Name.NamespaceName);
    }

    [Fact]
    public void The_operation_element_is_the_body_and_its_parameters_keep_their_order()
    {
        // Document/literal WRAPPED: the elements must appear in the order the WSDL declares them,
        // so a parameter collection that does not preserve order would produce an envelope the
        // service rejects for reasons that read as a data problem.
        var envelope = XDocument.Parse(NbsSoapEnvelope.Build(
            "GetExchangeRateByCurrency", Credentials,
            ("currencyCode", "978"),
            ("dateFrom", "20260101"),
            ("dateTo", "20260820"),
            ("exchangeRateListTypeID", "3")));

        var body = envelope.Descendants()
            .Single(e => e.Name.LocalName == "GetExchangeRateByCurrency");

        Assert.Equal(
            ["currencyCode", "dateFrom", "dateTo", "exchangeRateListTypeID"],
            body.Elements().Select(e => e.Name.LocalName));

        Assert.Equal("978", body.Elements().First().Value);
    }

    [Fact]
    public void A_null_parameter_is_sent_as_nil_rather_than_left_out()
    {
        // NBS treats every parameter as a FILTER and nil as "ignore this criterion". An omitted
        // element is a different thing to the service than a nil one, so the distinction has to
        // survive into the envelope.
        var envelope = XDocument.Parse(NbsSoapEnvelope.Build(
            "GetExchangeRateListType", Credentials, ("exchangeRateListTypeID", null)));

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var parameter = envelope.Descendants()
            .Single(e => e.Name.LocalName == "exchangeRateListTypeID");

        Assert.Equal("true", parameter.Attribute(xsi + "nil")?.Value);
        Assert.Empty(parameter.Value);
    }

    [Fact]
    public void A_date_goes_out_as_yyyyMMdd()
    {
        // NBS takes dates IN as yyyyMMdd strings and hands them back OUT as dd.MM.yyyy. Two
        // formats in one conversation is its convention, not a mistake here, and this is the half
        // that would fail as "invalid parameters" rather than as a wrong answer.
        Assert.Equal("20260820", NbsSoapEnvelope.Date(new DateOnly(2026, 8, 20)));
        Assert.Equal("20020515", NbsSoapEnvelope.Date(new DateOnly(2002, 5, 15)));
    }

    [Fact]
    public void The_soap_action_names_the_operation_in_the_service_namespace()
    {
        // SOAP 1.1 requires the header and NBS enforces it: without it the request is refused
        // before authentication is even considered.
        Assert.Equal(
            "http://communicationoffice.nbs.rs/GetExchangeRateByDate",
            NbsSoapEnvelope.SoapAction("GetExchangeRateByDate"));
    }

    [Fact]
    public void A_successful_response_gives_back_the_document_inside_the_result_element()
    {
        // The wrinkle the Xml twin exists around: the payload arrives XML-ESCAPED inside
        // {Operation}Result, so what comes back is a document containing a document and the inner
        // one has to be parsed separately. A client that treated the outer parse as the whole job
        // would find no rows and report an empty list.
        var response = Soap(
            "<GetExchangeRateByDateResult>"
            + "&lt;ExchangeRateDataSet&gt;&lt;ExchangeRate&gt;&lt;CurrencyCodeAlfaChar&gt;EUR"
            + "&lt;/CurrencyCodeAlfaChar&gt;&lt;/ExchangeRate&gt;&lt;/ExchangeRateDataSet&gt;"
            + "</GetExchangeRateByDateResult>");

        var result = NbsSoapEnvelope.ReadResult(response, "GetExchangeRateByDate");

        Assert.True(result.IsSuccess);
        Assert.StartsWith("<ExchangeRateDataSet>", result.Value, StringComparison.Ordinal);
        Assert.Contains("EUR", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fault_is_read_as_the_services_own_words()
    {
        // Recorded from a live unauthenticated call, which is how this shape was confirmed rather
        // than only read about. The message goes into the host's job log verbatim: an operator who
        // reads "Invalid user name or password!!!" knows what to do, and one who reads "the rate
        // import failed" does not.
        var result = NbsSoapEnvelope.ReadResult(Fault(70, "UserAuthenticationError",
            "Invalid user name or password!!!"), "GetExchangeRateByDate");

        Assert.False(result.IsSuccess);
        Assert.Equal(70, result.Error!.Code);
        Assert.Equal("UserAuthenticationError", result.Error.Type);
        Assert.Contains("Invalid user name or password", result.FailureMessage, StringComparison.Ordinal);

        // And it is NOT a transport failure. The two are kept apart because a caller deciding
        // whether to retry needs to know that a timeout is not a licence problem.
        Assert.Null(result.TransportFailure);
    }

    [Fact]
    public void A_fault_is_read_even_though_it_arrives_with_a_success_looking_body()
    {
        // The ordering that matters: faults are checked BEFORE the result element is looked for,
        // and independently of the HTTP status. NBS answers a fault with 500 — a client that
        // branched on the status first would throw the fault away in favour of "the server
        // returned 500".
        var result = NbsSoapEnvelope.ReadResult(
            Fault(60, "LicenceOverflowError", "Licence quota exceeded"), "GetExchangeRateByDate");

        Assert.Equal(60, result.Error!.Code);
        Assert.Equal(NbsFailureKind.Configuration, result.Error.Kind);
        Assert.False(result.IsWorthRetrying);
    }

    [Fact]
    public void A_response_that_is_neither_a_result_nor_a_fault_is_a_transport_failure()
    {
        // An HTML error page, an empty body, a service renamed underneath us. Reported as
        // transport so it is retried on the next run rather than recorded as "no rates today".
        var result = NbsSoapEnvelope.ReadResult(Soap("<SomethingElse/>"), "GetExchangeRateByDate");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Contains("GetExchangeRateByDateResult", result.TransportFailure!, StringComparison.Ordinal);
        Assert.True(result.IsWorthRetrying);
    }

    [Fact]
    public void A_body_that_is_not_xml_at_all_is_a_transport_failure_rather_than_a_crash()
    {
        var result = NbsSoapEnvelope.ReadResult("<html>503 Service Unavailable", "GetExchangeRateByDate");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsWorthRetrying);
    }

    private static string Child(XElement parent, string name) =>
        parent.Elements().Single(e => e.Name.LocalName == name).Value;

    private static string Soap(string body) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        + "<soap:Body>" + body + "</soap:Body></soap:Envelope>";

    private static string Fault(int code, string type, string message) =>
        Soap("<soap:Fault><faultcode>soap:Server</faultcode>"
             + "<faultstring>Server was unable to process request.</faultstring>"
             + "<detail><ErrorInfo>"
             + $"<ErrorType>{type}</ErrorType>"
             + $"<ErrorCode>{code}</ErrorCode>"
             + $"<ErrorMessage>{message}</ErrorMessage>"
             + "</ErrorInfo></detail></soap:Fault>");
}
