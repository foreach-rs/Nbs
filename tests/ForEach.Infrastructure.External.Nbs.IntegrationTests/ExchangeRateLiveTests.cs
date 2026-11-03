using ForEach.Infrastructure.External.Nbs.ExchangeRates;

namespace ForEach.Infrastructure.External.Nbs.IntegrationTests;

/// <summary>
/// The exchange-rate register, against the live service.
///
/// What these can prove that the recorded-XML tests cannot: that the envelope NBS accepts is the
/// one being sent, that the credentials work, and that the fields the parser reads are the fields
/// the service still returns. What they deliberately do NOT assert is any particular RATE — the
/// numbers change daily and a test pinned to one would fail every morning for the right reason
/// and the wrong purpose.
/// </summary>
public class ExchangeRateLiveTests
{
    [NbsFact]
    public async Task The_middle_list_comes_back_with_the_euro_on_it()
    {
        var client = new NbsExchangeRateClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.GetByDateAsync(Today, NbsExchangeRateClient.MiddleListType);

        Assert.True(result.IsSuccess, result.FailureMessage);

        var rows = result.Value!;

        Assert.NotEmpty(rows);

        var euro = rows.SingleOrDefault(r => r.CurrencyCode == "EUR");

        Assert.NotNull(euro);

        // The middle list publishes the middle rate and nothing else — the fact that makes a day's
        // import two calls rather than one.
        Assert.NotNull(euro.MiddleRate);
        Assert.Null(euro.BuyingRate);
        Assert.Null(euro.SellingRate);

        // A sanity band rather than a value. The dinar has traded between 100 and 130 to the euro
        // for years; anything outside that is a decimal point in the wrong place, which is the
        // failure this whole integration is most likely to produce and the hardest to notice.
        Assert.InRange(euro.MiddleRate!.Value, 100m, 130m);

        Assert.Equal(1, euro.Unit);
        Assert.Equal(978, euro.NumericCode);
        Assert.Equal(NbsExchangeRateClient.MiddleListType, euro.ListTypeId);
    }

    [NbsFact]
    public async Task The_foreign_exchange_list_carries_buying_and_selling_around_the_middle()
    {
        var client = new NbsExchangeRateClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var middle = await client.GetByDateAsync(Today, NbsExchangeRateClient.MiddleListType);
        var foreignExchange = await client.GetByDateAsync(
            Today, NbsExchangeRateClient.ForeignExchangeListType);

        Assert.True(foreignExchange.IsSuccess, foreignExchange.FailureMessage);

        var euro = foreignExchange.Value!.Single(r => r.CurrencyCode == "EUR");

        Assert.NotNull(euro.BuyingRate);
        Assert.NotNull(euro.SellingRate);

        // The relationship rather than the numbers: a bank buys below and sells above the middle
        // rate, always. If these three ever came back in another order the mapping onto Kupovni,
        // Srednji and Prodajni would be wrong — and every converted amount with it.
        var middleRate = middle.Value!.Single(r => r.CurrencyCode == "EUR").MiddleRate!.Value;

        Assert.True(euro.BuyingRate < middleRate,
            $"buying {euro.BuyingRate} should be below the middle rate {middleRate}");

        Assert.True(euro.SellingRate > middleRate,
            $"selling {euro.SellingRate} should be above the middle rate {middleRate}");
    }

    [NbsFact]
    public async Task Every_row_on_the_list_agrees_on_one_date_and_it_is_a_working_day()
    {
        // The weekend rule, observed rather than assumed. NBS answers with the list IN FORCE on
        // the queried date, so on a Saturday every row carries Friday's date — which is why the
        // importer files under the row's date and not under the day it ran.
        var client = new NbsExchangeRateClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.GetByDateAsync(Today, NbsExchangeRateClient.MiddleListType);

        Assert.True(result.IsSuccess, result.FailureMessage);

        var dates = result.Value!.Select(r => r.Date).Distinct().ToList();

        var listDate = Assert.Single(dates);

        Assert.NotEqual(DayOfWeek.Saturday, listDate.DayOfWeek);
        Assert.NotEqual(DayOfWeek.Sunday, listDate.DayOfWeek);

        // Never in the future, and never far behind: the list in force today was published today
        // or on the last working day before it.
        Assert.True(listDate <= Today, $"list date {listDate} is after today {Today}");
        Assert.True(listDate >= Today.AddDays(-10), $"list date {listDate} is more than ten days old");
    }

    [NbsFact]
    public async Task A_currency_quoted_per_hundred_comes_back_saying_so()
    {
        // The hundredfold error, checked against the live register rather than a fixture. HUF and
        // JPY publish per 100; if either ever came back as unit 1 with the same figure, every
        // amount converted through it would be a hundred times too large.
        var client = new NbsExchangeRateClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.GetByDateAsync(Today, NbsExchangeRateClient.MiddleListType);

        Assert.True(result.IsSuccess, result.FailureMessage);

        var perHundred = result.Value!
            .Where(r => r.CurrencyCode is "HUF" or "JPY")
            .ToList();

        Assert.NotEmpty(perHundred);
        Assert.All(perHundred, r => Assert.Equal(100, r.Unit));
    }

    [NbsFact]
    public async Task A_year_of_one_currency_can_be_read_for_a_backfill()
    {
        // The backfill path, and the 366-day cap. Thirty days is well inside it; what this proves
        // is that the by-currency operation takes the NUMERIC ISO code and a date span, which is a
        // different envelope shape from the by-date call.
        var client = new NbsExchangeRateClient(NbsLive.NewHttpClient(), NbsLive.Credentials);

        var result = await client.GetByCurrencyAsync(
            978, Today.AddDays(-30), Today, NbsExchangeRateClient.MiddleListType);

        Assert.True(result.IsSuccess, result.FailureMessage);

        var rows = result.Value!;

        // Roughly one row per working day. Asserted as a band because holidays vary.
        Assert.InRange(rows.Count, 15, 31);
        Assert.All(rows, r => Assert.Equal("EUR", r.CurrencyCode));
        Assert.All(rows, r => Assert.NotNull(r.MiddleRate));
    }

    [NbsFact]
    public async Task A_wrong_password_is_refused_in_the_services_own_words()
    {
        // The fault path, live. Everything else here proves the happy case; this proves the client
        // can still READ a refusal — and it is the one failure an operator will actually meet,
        // because a password is the thing that gets mistyped or rotated.
        var wrong = new NbsCredentials
        {
            UserName = NbsLive.Credentials.UserName,
            Password = "definitivno-pogresna-lozinka",
            LicenceId = NbsLive.Credentials.LicenceId
        };

        var client = new NbsExchangeRateClient(NbsLive.NewHttpClient(), wrong);

        var result = await client.GetByDateAsync(Today, NbsExchangeRateClient.MiddleListType);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);

        // A configuration problem rather than a transient one: retrying a wrong password every
        // morning for a year is not a strategy, and the kind is what tells a caller so.
        Assert.Equal(NbsFailureKind.Configuration, result.Error!.Kind);
        Assert.False(result.IsWorthRetrying);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}
