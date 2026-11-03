using System.Text;

namespace ForEach.Infrastructure.External.Nbs.ExchangeRates;

/// <summary>
/// The NBS exchange-rate register.
///
/// Against `ExchangeRateXmlService` — the string-returning twin — because the DataSet twin returns
/// an inline schema and a diffgram, which is awkward to consume anywhere outside .NET Framework
/// and pointless when the same document is available as a string.
///
/// Rates exist from 15.05.2002 onwards.
/// </summary>
public sealed class NbsExchangeRateClient(HttpClient httpClient, NbsCredentials credentials)
{
    private const string Service = "ExchangeRateXmlService";

    /// <summary>
    /// The middle list — type 3 — which is the one documents must be converted at.
    /// </summary>
    public const int MiddleListType = 3;

    /// <summary>
    /// The foreign-exchange list — type 1 — carrying buying and selling rates.
    ///
    /// Type 2 exists as well (`Efektiva`, cash buying and selling) and is deliberately not used: it
    /// prices banknotes over a counter, not the transfer of money, so quoting it on an invoice
    /// would be the wrong rate on the right currency.
    /// </summary>
    public const int ForeignExchangeListType = 1;

    /// <summary>Whether the credentials are filled in, so a host can run dark until it has an account.</summary>
    public bool IsConfigured => credentials.IsConfigured;

    /// <summary>
    /// Every row on one published list for one date.
    ///
    /// The date is a QUERY, not an assertion: NBS returns the list in force on that date, so a
    /// weekend or holiday query comes back with the previous working day's list carrying its own
    /// date. Each row says which date it belongs to and the caller must believe the row.
    ///
    /// An empty list is a success with no rows, not a failure. It is what a date before the
    /// register begins looks like, and a caller that treats it as an error turns a quiet gap into
    /// a daily alarm.
    /// </summary>
    public async Task<NbsResult<List<NbsExchangeRateRow>>> GetByDateAsync(
        DateOnly date, int listTypeId, CancellationToken ct = default)
    {
        if (!credentials.IsConfigured)
            return NbsResult<List<NbsExchangeRateRow>>.Fail(
                NbsError.NotConfigured("NBS credentials are not configured."));

        const string operation = "GetExchangeRateByDate";

        var response = await PostAsync(operation, ct,
            ("date", NbsSoapEnvelope.Date(date)),
            ("exchangeRateListTypeID", listTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        if (!response.IsSuccess)
            return response.Error is { } error
                ? NbsResult<List<NbsExchangeRateRow>>.Fail(error)
                : NbsResult<List<NbsExchangeRateRow>>.Transport(response.TransportFailure!);

        return NbsResult<List<NbsExchangeRateRow>>.Ok(
            NbsExchangeRateRow.Parse(response.Value ?? ""));
    }

    /// <summary>
    /// One currency's history over a span. NBS caps a single call at **366 days**, which is a
    /// caller's problem rather than this method's: it sends what it is given and NBS answers
    /// error 20 for a longer span, which arrives as a CallerMistake rather than as a retryable
    /// failure. A backfill walks the years in windows.
    /// </summary>
    public async Task<NbsResult<List<NbsExchangeRateRow>>> GetByCurrencyAsync(
        int numericCurrencyCode,
        DateOnly from,
        DateOnly to,
        int listTypeId,
        CancellationToken ct = default)
    {
        if (!credentials.IsConfigured)
            return NbsResult<List<NbsExchangeRateRow>>.Fail(
                NbsError.NotConfigured("NBS credentials are not configured."));

        const string operation = "GetExchangeRateByCurrency";

        var response = await PostAsync(operation, ct,
            ("currencyCode", numericCurrencyCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("dateFrom", NbsSoapEnvelope.Date(from)),
            ("dateTo", NbsSoapEnvelope.Date(to)),
            ("exchangeRateListTypeID", listTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        if (!response.IsSuccess)
            return response.Error is { } error
                ? NbsResult<List<NbsExchangeRateRow>>.Fail(error)
                : NbsResult<List<NbsExchangeRateRow>>.Transport(response.TransportFailure!);

        return NbsResult<List<NbsExchangeRateRow>>.Ok(
            NbsExchangeRateRow.Parse(response.Value ?? ""));
    }

    /// <summary>
    /// One request. The HTTP status is deliberately not consulted before the body is read: NBS
    /// answers a fault with 500, and the fault is the useful part — branching on the status first
    /// would throw away the service's own words in favour of "the server returned 500".
    /// </summary>
    private async Task<NbsResult<string>> PostAsync(
        string operation, CancellationToken ct, params (string Name, string? Value)[] parameters)
    {
        try
        {
            // Built inside the try: a control character in a credential or a filter is not
            // something XML can carry, and it is a result for the host to show, not an exception.
            var envelope = NbsSoapEnvelope.Build(operation, credentials, parameters);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, credentials.EndpointFor(Service))
            {
                Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
            };

            request.Headers.Add("SOAPAction", $"\"{NbsSoapEnvelope.SoapAction(operation)}\"");

            using var response = await httpClient.SendAsync(request, ct);

            return NbsSoapEnvelope.ReadResult(await response.Content.ReadAsStringAsync(ct), operation);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The timeout, told apart from the caller's own cancellation. HttpClient reports both
            // as the same exception type, and "the request timed out" and "the host is shutting
            // down" want different reactions from whoever reads the job log.
            return NbsResult<string>.Transport("The NBS request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return NbsResult<string>.Transport(ex.Message);
        }
        catch (UriFormatException ex)
        {
            return NbsResult<string>.Fail(
                NbsError.NotConfigured($"The NBS address is not usable: {ex.Message}"));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException)
        {
            return NbsResult<string>.Fail(NbsError.InvalidRequest(
                $"The request could not be built — check the credentials and filters for control characters: {ex.Message}"));
        }
    }
}
