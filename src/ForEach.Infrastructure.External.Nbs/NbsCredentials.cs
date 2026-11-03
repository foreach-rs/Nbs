namespace ForEach.Infrastructure.External.Nbs;

/// <summary>
/// What NBS needs on every call, and where to send it.
///
/// There is no handshake and no token: the same three values go in the SOAP header of every
/// request, in plaintext, and HTTPS is the whole of the protection. That is NBS's design, not a
/// simplification here — there is no WS-Security option to prefer.
///
/// UserName and Password are chosen by the applicant during registration; LicenceId is the GUID
/// NBS e-mails back once the signed paper request has been processed. A host binding these from
/// configuration should treat Password as a secret in whatever way it treats its others.
/// </summary>
public sealed class NbsCredentials
{
    /// <summary>
    /// The service root. `CommunicationOfficeService1_0` is the only published version, and each
    /// service is a page under it — so this ends in a slash and the client appends
    /// `{Service}.asmx`.
    /// </summary>
    public string BaseUrl { get; set; } = "https://webservices.nbs.rs/CommunicationOfficeService1_0/";

    /// <summary>The account name issued by NBS.</summary>
    public string UserName { get; set; } = "";

    /// <summary>The account password. A secret, and treated as one by whoever binds it.</summary>
    public string Password { get; set; } = "";

    /// <summary>The licence GUID issued by NBS, in `D` format.</summary>
    public string LicenceId { get; set; } = "";

    /// <summary>
    /// All three present. Nothing here checks whether they are CORRECT — only NBS can say that,
    /// and it says it as error 70. What this answers is whether a call is worth making at all,
    /// so a host can run dark until the membership exists rather than failing a job every day.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(UserName)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(LicenceId);

    /// <summary>The endpoint of one service, e.g. `ExchangeRateXmlService`.</summary>
    public Uri EndpointFor(string serviceName) =>
        new($"{BaseUrl.TrimEnd('/')}/{serviceName}.asmx");
}
