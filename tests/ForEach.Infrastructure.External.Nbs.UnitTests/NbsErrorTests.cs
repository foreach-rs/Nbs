using ForEach.Infrastructure.External.Nbs;

namespace ForEach.Infrastructure.External.Nbs.UnitTests;

/// <summary>
/// The seven documented failure codes, and what a caller should do about each.
///
/// Sorted into three kinds rather than left as bare numbers, because the number is not the
/// decision — the decision is whether to retry, whether to wake somebody up, and whether the
/// mistake is here or there. A scheduled job that retries a wrong password every day for a year
/// is behaving exactly as one that retries a backend hiccup, and only one of those is right.
/// </summary>
public class NbsErrorTests
{
    [Theory]
    [InlineData(10, "AuthenticationHeaderError")]
    [InlineData(50, "LicenceError")]
    [InlineData(60, "LicenceOverflowError")]
    [InlineData(70, "UserAuthenticationError")]
    public void A_configuration_failure_will_repeat_until_a_person_acts(int code, string type)
    {
        // A bad header, an expired licence, an exhausted quota, a wrong password. Nothing the
        // machine does next changes any of them, so retrying is noise and somebody has to be told.
        var error = Read(code, type, "…");

        Assert.Equal(NbsFailureKind.Configuration, error.Kind);
    }

    [Theory]
    [InlineData(30, "DataAccessError")]
    [InlineData(40, "DataPreparationError")]
    public void An_nbs_side_failure_is_worth_trying_again(int code, string type)
    {
        Assert.Equal(NbsFailureKind.Transient, Read(code, type, "…").Kind);
    }

    [Fact]
    public void Invalid_parameters_are_a_bug_here_rather_than_a_service_being_down()
    {
        // Error 20 must fail loudly. Swallowed as "the service was unavailable" it becomes a
        // permanent silent gap — the call never succeeds and nobody is ever told why.
        Assert.Equal(NbsFailureKind.CallerMistake, Read(20, "InputParametersError", "…").Kind);
    }

    [Fact]
    public void An_undocumented_code_is_treated_as_transient()
    {
        // NBS can add codes. Retrying something that will never work costs one scheduled run;
        // giving up on something that would have worked costs the data until somebody notices.
        Assert.Equal(NbsFailureKind.Transient, Read(99, "SomethingNew", "…").Kind);
    }

    [Fact]
    public void The_message_carries_the_code_the_type_and_the_services_own_words()
    {
        // This string is what a host writes into its job log. All three parts are there because
        // the code is what support asks for, the type is what it means, and the message is the
        // only part that ever says which currency or which date.
        var error = Read(70, "UserAuthenticationError", "Invalid user name or password!!!");

        var text = error.ToString();

        Assert.Contains("70", text, StringComparison.Ordinal);
        Assert.Contains("UserAuthenticationError", text, StringComparison.Ordinal);
        Assert.Contains("Invalid user name or password!!!", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_with_no_fault_reads_as_no_error()
    {
        Assert.Null(NbsError.TryRead("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">"
                                     + "<soap:Body><GetExchangeRateByDateResult /></soap:Body></soap:Envelope>"));
    }

    [Fact]
    public void A_fault_whose_detail_is_namespace_qualified_is_still_read()
    {
        // SOAP 1.1 puts `detail` outside any namespace and the live service's content has been
        // seen unqualified — but a serialiser change that qualified it would otherwise turn a
        // perfectly readable error into "the response could not be parsed", which is the least
        // useful thing a client can say about a failure.
        var error = NbsError.TryRead(
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>"
            + "<soap:Fault><detail><ErrorInfo xmlns=\"http://communicationoffice.nbs.rs\">"
            + "<ErrorType>LicenceError</ErrorType><ErrorCode>50</ErrorCode>"
            + "<ErrorMessage>Licence expired</ErrorMessage>"
            + "</ErrorInfo></detail></soap:Fault></soap:Body></soap:Envelope>");

        Assert.Equal(50, error!.Code);
        Assert.Equal("LicenceError", error.Type);
    }

    [Fact]
    public void A_fault_with_an_unreadable_code_still_reports_its_message()
    {
        // Better a code of zero and the server's sentence than an exception thrown while trying
        // to explain a failure.
        var error = NbsError.TryRead(
            "<detail><ErrorInfo><ErrorType>Odd</ErrorType><ErrorCode>?</ErrorCode>"
            + "<ErrorMessage>Nešto je pošlo naopako</ErrorMessage></ErrorInfo></detail>");

        Assert.Equal(0, error!.Code);
        Assert.Equal("Nešto je pošlo naopako", error.Message);
    }

    private static NbsError Read(int code, string type, string message) =>
        NbsError.TryRead(
            "<detail><ErrorInfo>"
            + $"<ErrorType>{type}</ErrorType><ErrorCode>{code}</ErrorCode>"
            + $"<ErrorMessage>{message}</ErrorMessage>"
            + "</ErrorInfo></detail>")!;
}
