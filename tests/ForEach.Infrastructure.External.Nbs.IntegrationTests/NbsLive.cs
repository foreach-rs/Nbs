using ForEach.Infrastructure.External.Nbs;
using Microsoft.Extensions.Configuration;

namespace ForEach.Infrastructure.External.Nbs.IntegrationTests;

/// <summary>
/// The credentials, and the decision not to run without them.
///
/// **These tests call the real NBS service over the network.** That is the point of them — the
/// unit tests hold the parsing against recorded XML and cannot notice the day NBS changes a field
/// or an endpoint — but it makes them unlike every other test in this repository: they are slow,
/// they need an internet connection, and they consume a licence quota that NBS enforces (error 60
/// exists for exactly that).
///
/// So they SKIP when the credentials are absent rather than fail. A build agent without the
/// membership runs the whole solution green, which is what keeps `dotnet test hrc.sln` honest as
/// the one command CI runs.
/// </summary>
internal static class NbsLive
{
    /// <summary>
    /// User secrets first, then environment. Never a committed file: these are a real membership,
    /// and the password is a secret in the same sense as `SefSettings.ApiKey` and the SMTP one.
    ///
    /// The environment fallback is what a build agent would use, where there is no secret store.
    /// </summary>
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddUserSecrets(typeof(NbsLive).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();

    public static NbsCredentials Credentials { get; } = new()
    {
        UserName = Configuration["Nbs:UserName"] ?? "",
        Password = Configuration["Nbs:Password"] ?? "",
        LicenceId = Configuration["Nbs:LicenceId"] ?? ""
    };

    /// <summary>
    /// Credentials AND an explicit opt-in. Both, deliberately.
    ///
    /// The credentials alone are not enough because this repository's working habit is to run the
    /// whole solution after every change — dozens of times a day. Each full run would spend a
    /// dozen calls against a licence that NBS meters (error 60, `LicenceOverflowError`), on a
    /// service whose answers change once a day. That is a real cost paid for no information.
    ///
    /// So the live suite runs when somebody ASKS for it:
    ///
    ///   NBS_LIVE_TESTS=1 dotnet test tests/ForEach.Infrastructure.External.Nbs.IntegrationTests
    ///
    /// Everything else — every unit test, every architecture test, `dotnet test hrc.sln` in CI —
    /// reports these as skipped, with the reason. A skip that says why is honest; a green that
    /// never called anything is not.
    /// </summary>
    public static bool IsConfigured =>
        Credentials.IsConfigured
        && Configuration["NBS_LIVE_TESTS"] is "1" or "true" or "TRUE";

    /// <summary>
    /// One client per test rather than a shared one. These are a handful of calls, so pooling buys
    /// nothing, and a per-test client makes the timeout below unambiguous.
    ///
    /// Sixty seconds rather than the thirty the application configures: a first call to NBS from a
    /// cold connection has been slower than a warm one, and a test that fails on the clock says
    /// nothing about whether the integration works.
    /// </summary>
    public static HttpClient NewHttpClient() =>
        new() { Timeout = TimeSpan.FromSeconds(60) };
}

/// <summary>
/// A test that needs the real NBS membership, skipped when there is none.
///
/// Attribute-based rather than an early return: a test that quietly passes when it did nothing is
/// how a whole integration ends up believed-in and unexercised. This one reports as SKIPPED, with
/// the reason, so a run that never touched NBS says so.
/// </summary>
public sealed class NbsFactAttribute : FactAttribute
{
    public NbsFactAttribute()
    {
        if (!NbsLive.IsConfigured)
        {
            Skip = NbsLive.Credentials.IsConfigured
                ? "Live NBS tests are opt-in: set NBS_LIVE_TESTS=1 to run them. They call the real "
                  + "service and spend a metered licence quota."
                : "NBS credentials are not configured (Nbs:UserName, Nbs:Password, Nbs:LicenceId "
                  + "in user secrets or the environment).";
        }
    }
}
