using ForEach.Infrastructure.External.Nbs.CompanyAccounts;

namespace ForEach.Infrastructure.External.Nbs.IntegrationTests;

/// <summary>
/// The company-account register, against the live service, asked about this company.
///
/// For-Each's own identifiers on purpose. They are public — a PIB and a matični broj are on every
/// invoice the company issues — and asking about ourselves means the test needs nobody's
/// permission and reveals nothing that is not already printed on our paperwork.
///
/// What these prove that recorded XML cannot: that the register answers both lookup shapes, that
/// the fields the parser reads are the ones the service still sends, and — the point of the whole
/// feature — that the PIB and the matični broj agree with each other in NBS's own records.
/// </summary>
public class CompanyAccountLiveTests
{
    /// <summary>For-Each's tax number, as printed on its invoices.</summary>
    private const string Pib = "106476364";

    /// <summary>And its matični broj — the register's own key.</summary>
    private const long MaticniBroj = 61297189;

    [NbsFact]
    public async Task Looking_up_our_own_maticni_broj_finds_us()
    {
        var client = new NbsCompanyAccountClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.GetByNationalIdentificationNumberAsync(MaticniBroj);

        Assert.True(result.IsSuccess, result.FailureMessage);

        var rows = result.Value!;

        Assert.NotEmpty(rows);

        // Every row is this company: the register is account-shaped, so several accounts repeat
        // the same identifiers rather than arriving as one company with a list.
        Assert.All(rows, r => Assert.Equal(MaticniBroj, r.NationalIdentificationNumber));
        Assert.All(rows, r => Assert.Equal(Pib, r.TaxIdentificationNumber));

        // The company fields a contact form would fill from.
        var first = rows[0];

        Assert.False(string.IsNullOrWhiteSpace(first.CompanyName), "the register returned no name");
        Assert.False(string.IsNullOrWhiteSpace(first.City), "the register returned no city");

        // And the account itself, which is what the checkboxes on that form offer.
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Account)));
        Assert.All(rows, r => Assert.True(r.BankCode > 0, "a row came back with no bank code"));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.BankName)));
    }

    [NbsFact]
    public async Task Looking_us_up_by_PIB_finds_the_same_company()
    {
        // The other entry point: a contact form where somebody has the PIB and not the matični
        // broj. It is a filtered SEARCH rather than a key lookup, so this also proves the paging
        // parameters are accepted.
        var client = new NbsCompanyAccountClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.SearchAsync(taxIdentificationNumber: Pib);

        Assert.True(result.IsSuccess, result.FailureMessage);

        var rows = result.Value!;

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(Pib, r.TaxIdentificationNumber));
        Assert.All(rows, r => Assert.Equal(MaticniBroj, r.NationalIdentificationNumber));
    }

    [NbsFact]
    public async Task The_two_lookups_agree_about_us()
    {
        // The cross-check the contact form is built on, verified against the register itself: if
        // the PIB path and the matični broj path ever disagreed about which company they name,
        // the "typo caught at entry" promise would be worth nothing.
        var client = new NbsCompanyAccountClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var byMb = await client.GetByNationalIdentificationNumberAsync(MaticniBroj);
        var byPib = await client.SearchAsync(taxIdentificationNumber: Pib);

        Assert.True(byMb.IsSuccess, byMb.FailureMessage);
        Assert.True(byPib.IsSuccess, byPib.FailureMessage);

        Assert.Equal(
            byMb.Value!.Select(r => r.Account).OrderBy(a => a, StringComparer.Ordinal),
            byPib.Value!.Select(r => r.Account).OrderBy(a => a, StringComparer.Ordinal));
    }

    [NbsFact]
    public async Task Our_own_accounts_are_not_in_blockade()
    {
        // Worth asserting because it is the state the code treats as ordinary, and because a
        // company that cannot pay its own suppliers would rather find out from a test than from a
        // customer. If this ever fails it is either a real blockade or a code-book change — and
        // both are things to know.
        var client = new NbsCompanyAccountClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.GetByNationalIdentificationNumberAsync(MaticniBroj);

        Assert.True(result.IsSuccess, result.FailureMessage);

        Assert.All(result.Value!, r =>
            Assert.True(r.BlockadeStatusId == 0,
                $"account {r.Account} reports blockade status {r.BlockadeStatusId}"));
    }

    [NbsFact]
    public async Task A_matici_broj_nobody_holds_is_an_empty_answer_rather_than_a_failure()
    {
        // What an unknown company looks like, live. The contact form says "nothing found" in
        // words; a failure would render it as an error somebody retries unchanged.
        var client = new NbsCompanyAccountClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.GetByNationalIdentificationNumberAsync(99999999);

        Assert.True(result.IsSuccess, result.FailureMessage);
        Assert.Empty(result.Value!);
    }

    [NbsFact]
    public async Task A_search_with_no_criteria_at_all_is_refused_by_the_register()
    {
        // Every parameter is a filter and null means "ignore", so an all-null search asks for the
        // whole register. NBS refuses it — and the refusal arrives as a CALLER MISTAKE rather than
        // as something to retry, which is what stops a scheduled job hammering it.
        //
        // This is also why the handler refuses an empty form before the call is made: better a
        // Serbian sentence than the register's own error.
        var client = new NbsCompanyAccountClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.SearchAsync();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal(NbsFailureKind.CallerMistake, result.Error!.Kind);
    }
}
