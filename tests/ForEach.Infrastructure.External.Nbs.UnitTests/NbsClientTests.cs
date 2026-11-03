using System.Net;
using System.Text;
using ForEach.Infrastructure.External.Nbs.CompanyAccounts;
using ForEach.Infrastructure.External.Nbs.ExchangeRates;

namespace ForEach.Infrastructure.External.Nbs.UnitTests;

/// <summary>
/// Both clients against a fake handler: what goes on the wire, and how each way a call can end is
/// reported. They share one shape on purpose, so every case runs against both.
///
/// The distinction that matters most is between a result the host should RETRY and one it should
/// REPORT: a timeout is the first, and a soap:Client fault or missing credentials are the second.
/// A job that retries a rejected licence GUID every night is working exactly as written.
/// </summary>
public class NbsClientTests
{
    private const string RatesOperation = "GetExchangeRateByDate";
    private const string AccountsOperation = "GetCompanyAccountByNationalIdentificationNumber";

    /// <summary>The parts of a result every case below looks at, whichever client produced it.</summary>
    private sealed record Outcome(bool IsSuccess, NbsError? Error, string? TransportFailure, bool IsWorthRetrying);

    [Theory]
    [InlineData("rates", RatesOperation)]
    [InlineData("accounts", AccountsOperation)]
    public async Task The_soap_action_header_names_the_operation_in_quotes(string client, string operation)
    {
        string? action = null;

        var handler = new Handler((request, _) =>
        {
            action = request.Headers.GetValues("SOAPAction").Single();

            return Task.FromResult(Reply(HttpStatusCode.OK, Result(operation, "<x/>")));
        });

        await Call(client, handler, Configured());

        Assert.Equal($"\"http://communicationoffice.nbs.rs/{operation}\"", action);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task A_timeout_is_a_retryable_transport_failure(string client)
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK, "");
        });

        var outcome = await Call(client, handler, Configured(), timeout: TimeSpan.FromMilliseconds(200));

        Assert.False(outcome.IsSuccess);
        Assert.NotNull(outcome.TransportFailure);
        Assert.True(outcome.IsWorthRetrying);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task A_connection_error_is_a_retryable_transport_failure(string client)
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("refused"));

        var outcome = await Call(client, handler, Configured());

        Assert.NotNull(outcome.TransportFailure);
        Assert.True(outcome.IsWorthRetrying);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task Missing_credentials_are_a_configuration_failure_and_nothing_is_sent(string client)
    {
        var sent = false;
        var handler = new Handler((_, _) =>
        {
            sent = true;

            return Task.FromResult(Reply(HttpStatusCode.OK, ""));
        });

        var outcome = await Call(client, handler, new NbsCredentials());

        Assert.False(sent);
        Assert.Null(outcome.TransportFailure);
        Assert.Equal(NbsFailureKind.Configuration, outcome.Error!.Kind);

        // Retrying it every night would be the job working exactly as written and never working.
        Assert.False(outcome.IsWorthRetrying);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task A_500_with_an_html_body_is_a_retryable_transport_failure(string client)
    {
        var handler = new Handler((_, _) =>
            Task.FromResult(Reply(HttpStatusCode.InternalServerError, "<html><body>Bad gateway</html>")));

        var outcome = await Call(client, handler, Configured());

        Assert.NotNull(outcome.TransportFailure);
        Assert.True(outcome.IsWorthRetrying);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task A_client_fault_without_error_info_is_a_configuration_failure(string client)
    {
        // What ASP.NET answers when it rejects a request before the service sees it — a licence
        // GUID that is not a GUID, for one. Read as "no Result element" it would be retried for ever.
        const string fault =
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>"
            + "<soap:Fault><faultcode>soap:Client</faultcode>"
            + "<faultstring>Server was unable to read request.</faultstring></soap:Fault>"
            + "</soap:Body></soap:Envelope>";

        var handler = new Handler((_, _) =>
            Task.FromResult(Reply(HttpStatusCode.InternalServerError, fault)));

        var outcome = await Call(client, handler, Configured());

        Assert.Null(outcome.TransportFailure);
        Assert.Equal(NbsFailureKind.Configuration, outcome.Error!.Kind);
        Assert.Equal("soap:Client", outcome.Error.Type);
        Assert.Equal("Server was unable to read request.", outcome.Error.Message);
        Assert.False(outcome.IsWorthRetrying);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task A_server_fault_without_error_info_is_still_worth_retrying(string client)
    {
        const string fault =
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>"
            + "<soap:Fault><faultcode>soap:Server</faultcode><faultstring>Oops</faultstring></soap:Fault>"
            + "</soap:Body></soap:Envelope>";

        var handler = new Handler((_, _) =>
            Task.FromResult(Reply(HttpStatusCode.InternalServerError, fault)));

        var outcome = await Call(client, handler, Configured());

        Assert.Equal(NbsFailureKind.Transient, outcome.Error!.Kind);
        Assert.True(outcome.IsWorthRetrying);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task The_callers_own_cancellation_propagates(string client)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK, "");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Call(client, handler, Configured(), cts.Token));
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task A_control_character_in_a_credential_is_a_result_and_not_an_exception(string client)
    {
        var credentials = Configured();
        credentials.Password = "pass\u0001word";

        var outcome = await Call(client, new Handler((_, _) =>
            Task.FromResult(Reply(HttpStatusCode.OK, ""))), credentials);

        Assert.False(outcome.IsSuccess);
        Assert.NotNull(outcome.Error);
    }

    [Theory]
    [InlineData("rates")]
    [InlineData("accounts")]
    public async Task Credentials_are_trimmed_before_they_are_sent(string client)
    {
        string? body = null;

        var handler = new Handler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();

            return Reply(HttpStatusCode.OK, "");
        });

        var credentials = Configured();
        credentials.UserName = "  korisnik\n";

        await Call(client, handler, credentials);

        Assert.Contains("<UserName>korisnik</UserName>", body, StringComparison.Ordinal);
    }

    private static NbsCredentials Configured() => new()
    {
        BaseUrl = "https://nbs.test/CommunicationOfficeService1_0/",
        UserName = "korisnik",
        Password = "lozinka",
        LicenceId = "11111111-2222-3333-4444-555555555555"
    };

    private static async Task<Outcome> Call(
        string client, Handler handler, NbsCredentials credentials,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { Timeout = timeout ?? System.Threading.Timeout.InfiniteTimeSpan };

        if (client == "rates")
        {
            var rates = await new NbsExchangeRateClient(http, credentials)
                .GetByDateAsync(new DateOnly(2026, 11, 3), NbsExchangeRateClient.MiddleListType, ct);

            return new Outcome(rates.IsSuccess, rates.Error, rates.TransportFailure, rates.IsWorthRetrying);
        }

        var accounts = await new NbsCompanyAccountClient(http, credentials)
            .GetByNationalIdentificationNumberAsync(20123456, ct);

        return new Outcome(accounts.IsSuccess, accounts.Error, accounts.TransportFailure, accounts.IsWorthRetrying);
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private static string Result(string operation, string inner) =>
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>"
        + $"<{operation}Response xmlns=\"http://communicationoffice.nbs.rs\">"
        + $"<{operation}Result>{System.Net.WebUtility.HtmlEncode(inner)}</{operation}Result>"
        + $"</{operation}Response></soap:Body></soap:Envelope>";

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
