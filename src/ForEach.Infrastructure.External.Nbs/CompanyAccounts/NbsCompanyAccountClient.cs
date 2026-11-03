using System.Globalization;
using System.Text;

namespace ForEach.Infrastructure.External.Nbs.CompanyAccounts;

/// <summary>
/// NBS's jedinstveni registar računa — who a company is, and which bank accounts it holds.
///
/// Against `CompanyAccountXmlService`, the string-returning twin, for the same reason the rate
/// client uses its own: the DataSet twin returns an inline schema and a diffgram.
///
/// Every parameter of every operation is a FILTER, and null means "ignore this criterion". That
/// is why the lookups below pass nulls explicitly rather than omitting elements — to the service
/// those are different things.
/// </summary>
public sealed class NbsCompanyAccountClient(HttpClient httpClient, NbsCredentials credentials)
{
    private const string Service = "CompanyAccountXmlService";

    /// <summary>
    /// The register caps any result at a hundred rows per call. Stated as a constant because a
    /// caller paging with the `Count` operation needs the same number, and two places writing 100
    /// is how a page-size mismatch starts skipping rows.
    /// </summary>
    public const int PageSize = 100;

    /// <summary>Whether the credentials are filled in, so a host can run dark until it has an account.</summary>
    public bool IsConfigured => credentials.IsConfigured;

    /// <summary>
    /// Every account held under one matični broj — the primary enrichment call.
    ///
    /// By MB rather than by PIB where possible: this is a direct lookup on the register's own
    /// identifier, where the PIB path is a filtered search that happens to return one company.
    /// </summary>
    public Task<NbsResult<List<NbsCompanyAccountRow>>> GetByNationalIdentificationNumberAsync(
        long nationalIdentificationNumber, CancellationToken ct = default) =>
        QueryAsync("GetCompanyAccountByNationalIdentificationNumber", ct,
            ("nationalIdentificationNumber",
                nationalIdentificationNumber.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The filtered search: by PIB, by matični broj, by name and city, or by account.
    ///
    /// **`city` is only meaningful together with a name** and NBS enforces minimum lengths — three
    /// characters for a name, two for a city — so a caller passing shorter ones gets error 20
    /// rather than a wider search. Those are the register's rules; this method sends what it is
    /// given and lets the service be the authority on its own limits.
    /// </summary>
    public Task<NbsResult<List<NbsCompanyAccountRow>>> SearchAsync(
        long? nationalIdentificationNumber = null,
        string? taxIdentificationNumber = null,
        string? companyName = null,
        string? city = null,
        int firstItem = 1,
        int lastItem = PageSize,
        CancellationToken ct = default) =>
        QueryAsync("GetCompanyAccount", ct,
            ("nationalIdentificationNumber",
                nationalIdentificationNumber?.ToString(CultureInfo.InvariantCulture)),
            ("taxIdentificationNumber", taxIdentificationNumber),
            ("bankCode", null),
            ("accountNumber", null),
            ("controlNumber", null),
            ("companyName", companyName),
            ("city", city),
            ("startItemNumber", firstItem.ToString(CultureInfo.InvariantCulture)),
            ("endItemNumber", lastItem.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Everything the register changed in a window — the delta a blocked-account watch reads.
    ///
    /// **NBS caps the window at seven days.** A longer one is refused as error 20, which arrives
    /// as a caller mistake rather than a retryable failure, so a watch that has not run for a
    /// fortnight walks the gap in weekly windows instead of asking for it all at once.
    /// </summary>
    public Task<NbsResult<List<NbsCompanyAccountRow>>> GetUpdatedAsync(
        DateOnly from,
        DateOnly to,
        int firstItem = 1,
        int lastItem = PageSize,
        CancellationToken ct = default) =>
        QueryAsync("GetCompanyAccountUpdated", ct,
            ("dateFrom", NbsSoapEnvelope.Date(from)),
            ("dateTo", NbsSoapEnvelope.Date(to)),
            ("startItemNumber", firstItem.ToString(CultureInfo.InvariantCulture)),
            ("endItemNumber", lastItem.ToString(CultureInfo.InvariantCulture)));

    private async Task<NbsResult<List<NbsCompanyAccountRow>>> QueryAsync(
        string operation, CancellationToken ct, params (string Name, string? Value)[] parameters)
    {
        if (!credentials.IsConfigured)
            return NbsResult<List<NbsCompanyAccountRow>>.Fail(
                NbsError.NotConfigured("NBS credentials are not configured."));

        var response = await PostAsync(operation, ct, parameters);

        if (!response.IsSuccess)
            return response.Error is { } error
                ? NbsResult<List<NbsCompanyAccountRow>>.Fail(error)
                : NbsResult<List<NbsCompanyAccountRow>>.Transport(response.TransportFailure!);

        return NbsResult<List<NbsCompanyAccountRow>>.Ok(
            NbsCompanyAccountRow.Parse(response.Value ?? ""));
    }

    /// <inheritdoc cref="ExchangeRates.NbsExchangeRateClient"/>
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
