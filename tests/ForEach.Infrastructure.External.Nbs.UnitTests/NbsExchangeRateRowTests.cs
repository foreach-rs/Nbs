using ForEach.Infrastructure.External.Nbs.ExchangeRates;

namespace ForEach.Infrastructure.External.Nbs.UnitTests;

/// <summary>
/// Reading a published rate list.
///
/// The shapes here follow `docs/nbs/reference/xsd/GetExchangeRateByDate.xsd.txt` — the official
/// schema, so the element names are the service's rather than a guess at them.
///
/// Every case below is a wrong NUMBER rather than a crash if it is got wrong, which is why they
/// are tested one at a time: a rate that is off by a factor of a hundred, or a rate filed under
/// the wrong day, looks entirely ordinary on screen and is discovered by an accountant.
/// </summary>
public class NbsExchangeRateRowTests
{
    [Fact]
    public void A_middle_list_gives_the_middle_rate_and_leaves_the_others_null()
    {
        // Type 3 publishes MiddleRate only, which is why a day's import is two calls. Null here
        // must mean "this list does not publish that" — a client that read it as zero would file
        // a buying rate of nothing.
        var rows = NbsExchangeRateRow.Parse(Document(Row("EUR", 978, 1, middle: "117.2543")));

        var row = Assert.Single(rows);

        Assert.Equal("EUR", row.CurrencyCode);
        Assert.Equal(117.2543m, row.MiddleRate);
        Assert.Null(row.BuyingRate);
        Assert.Null(row.SellingRate);
    }

    [Fact]
    public void A_foreign_exchange_list_gives_buying_and_selling()
    {
        var rows = NbsExchangeRateRow.Parse(Document(
            Row("EUR", 978, 1, buying: "116.9032", selling: "117.6054", listTypeId: 1)));

        var row = Assert.Single(rows);

        Assert.Equal(116.9032m, row.BuyingRate);
        Assert.Equal(117.6054m, row.SellingRate);
        Assert.Null(row.MiddleRate);
        Assert.Equal(1, row.ListTypeId);
    }

    [Fact]
    public void A_rate_is_read_under_the_invariant_culture()
    {
        // The document writes a POINT whatever the reader's culture is. Parsed under the Serbian
        // culture this application runs in — where the point groups digits — "117.2543" reads as
        // 1.172.543. Not an error: a rate ten thousand times too large, on an invoice.
        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("sr-Latn-RS");

            var row = Assert.Single(NbsExchangeRateRow.Parse(
                Document(Row("EUR", 978, 1, middle: "117.2543"))));

            Assert.Equal(117.2543m, row.MiddleRate);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_rate_of_zero_is_read_as_not_published_rather_than_as_a_number()
    {
        // Not hypothetical, and not what this parser first assumed. The LIVE middle list sends
        // `0.0000` for the buying and selling rates it does not publish, rather than omitting the
        // elements — found by an integration test that asserted they would be absent.
        //
        // A rate of zero is not a rate. Taken at face value it would file "one euro costs zero
        // dinars" for the day the middle list was imported, on the table every invoice converts
        // through, and it would look like data rather than like a gap.
        var row = Assert.Single(NbsExchangeRateRow.Parse(Document(
            "<ExchangeRate>"
            + "<Date>20.08.2026</Date>"
            + "<ExchangeRateListTypeID>3</ExchangeRateListTypeID>"
            + "<CurrencyCodeAlfaChar>EUR</CurrencyCodeAlfaChar>"
            + "<Unit>1</Unit>"
            + "<BuyingRate>0.0000</BuyingRate>"
            + "<MiddleRate>117.2543</MiddleRate>"
            + "<SellingRate>0.0000</SellingRate>"
            + "</ExchangeRate>")));

        Assert.Equal(117.2543m, row.MiddleRate);
        Assert.Null(row.BuyingRate);
        Assert.Null(row.SellingRate);
    }

    [Fact]
    public void A_currency_quoted_per_hundred_keeps_its_unit()
    {
        // JPY and HUF publish per 100. A client that assumed 1 would report a hundredfold error
        // in the customer's favour or the company's, depending on the direction of the trade.
        var rows = NbsExchangeRateRow.Parse(Document(
            Row("HUF", 348, 100, middle: "29.8012"),
            Row("EUR", 978, 1, middle: "117.2543")));

        Assert.Equal(100, rows.Single(r => r.CurrencyCode == "HUF").Unit);
        Assert.Equal(1, rows.Single(r => r.CurrencyCode == "EUR").Unit);
    }

    [Fact]
    public void A_unit_of_zero_is_read_as_one_rather_than_kept()
    {
        // Defensive on purpose: a unit of zero is a divide-by-nothing waiting to happen in
        // whatever converts with it, and one is the value every currency but two actually has.
        var row = Assert.Single(NbsExchangeRateRow.Parse(
            Document(Row("EUR", 978, 0, middle: "117.2543"))));

        Assert.Equal(1, row.Unit);
    }

    [Fact]
    public void The_row_carries_the_lists_own_date_which_need_not_be_the_date_asked_for()
    {
        // The weekend case, and the reason the row date exists on this record at all. A list
        // applies from 08:00 to 08:00 and the previous working day's stays in force over a
        // weekend — so a Saturday query returns FRIDAY's list carrying Friday's date. A caller
        // filing rates under the date it queried re-files Friday's list under Saturday, twice a
        // week, for ever.
        var saturdayQueryReturnsFriday = Document(
            Row("EUR", 978, 1, middle: "117.2543", date: "21.08.2026"));

        var row = Assert.Single(NbsExchangeRateRow.Parse(saturdayQueryReturnsFriday));

        Assert.Equal(new DateOnly(2026, 8, 21), row.Date);
    }

    [Fact]
    public void A_date_is_read_as_dd_MM_yyyy_and_not_as_the_format_dates_go_out_in()
    {
        // Inputs are yyyyMMdd, outputs are dd.MM.yyyy. Reading an output with the input's format
        // fails outright, which is the good case; reading "08.09.2026" as month-first would
        // silently file September's rate in August.
        var row = Assert.Single(NbsExchangeRateRow.Parse(
            Document(Row("EUR", 978, 1, middle: "117.2543", date: "08.09.2026"))));

        Assert.Equal(new DateOnly(2026, 9, 8), row.Date);
    }

    [Fact]
    public void A_full_list_reads_every_row()
    {
        var rows = NbsExchangeRateRow.Parse(Document(
            Row("EUR", 978, 1, middle: "117.2543"),
            Row("USD", 840, 1, middle: "100.4411"),
            Row("CHF", 756, 1, middle: "125.0233"),
            Row("JPY", 392, 100, middle: "68.1290")));

        Assert.Equal(4, rows.Count);
        Assert.Equal(["EUR", "USD", "CHF", "JPY"], rows.Select(r => r.CurrencyCode));
    }

    [Fact]
    public void A_row_with_no_usable_date_is_skipped_rather_than_failing_the_batch()
    {
        // One malformed line should not cost the other thirty currencies their rates for the day.
        // The import counts what it wrote, so a dropped row still shows as a smaller number rather
        // than as success.
        var rows = NbsExchangeRateRow.Parse(Document(
            Row("EUR", 978, 1, middle: "117.2543"),
            Row("USD", 840, 1, middle: "100.4411", date: "not-a-date")));

        Assert.Equal("EUR", Assert.Single(rows).CurrencyCode);
    }

    [Fact]
    public void A_row_with_no_currency_code_is_skipped()
    {
        var rows = NbsExchangeRateRow.Parse(
            "<ExchangeRateDataSet><ExchangeRate>"
            + "<Date>20.08.2026</Date><MiddleRate>117.2543</MiddleRate>"
            + "</ExchangeRate></ExchangeRateDataSet>");

        Assert.Empty(rows);
    }

    [Fact]
    public void An_empty_list_is_no_rows_rather_than_a_failure()
    {
        // What a date before 15.05.2002 looks like, and what a holiday edge can look like. A
        // caller that treats it as an error turns a quiet gap into a daily alarm.
        Assert.Empty(NbsExchangeRateRow.Parse("<ExchangeRateDataSet />"));
        Assert.Empty(NbsExchangeRateRow.Parse(""));
    }

    [Fact]
    public void A_document_that_is_not_xml_yields_nothing_rather_than_throwing()
    {
        // The parser is handed whatever came back inside the result element. It is not the place
        // to decide that the service is broken — it is the place not to take the process down.
        Assert.Empty(NbsExchangeRateRow.Parse("<ExchangeRateDataSet><ExchangeRate>"));
    }

    private static string Document(params string[] rows) =>
        "<ExchangeRateDataSet>" + string.Join("", rows) + "</ExchangeRateDataSet>";

    /// <summary>
    /// One row in the shape the official schema declares, including the fields HRC ignores — so
    /// the fixture is what the service sends rather than what the parser happens to read.
    /// </summary>
    private static string Row(
        string code,
        int numericCode,
        int unit,
        string? middle = null,
        string? buying = null,
        string? selling = null,
        string date = "20.08.2026",
        int listTypeId = 3) =>
        "<ExchangeRate>"
        + "<ExchangeRateListNumber>161</ExchangeRateListNumber>"
        + $"<Date>{date}</Date>"
        + "<CreateDate>19.08.2026</CreateDate>"
        + $"<ExchangeRateListTypeID>{listTypeId}</ExchangeRateListTypeID>"
        + "<CurrencyGroupID>1</CurrencyGroupID>"
        + $"<CurrencyCode>{numericCode}</CurrencyCode>"
        + $"<CurrencyCodeAlfaChar>{code}</CurrencyCodeAlfaChar>"
        + $"<CurrencyCodeNumChar>{numericCode}</CurrencyCodeNumChar>"
        + "<CurrencyNameSerCyrl>евро</CurrencyNameSerCyrl>"
        + "<CurrencyNameSerLat>evro</CurrencyNameSerLat>"
        + "<CurrencyNameEng>euro</CurrencyNameEng>"
        + $"<Unit>{unit}</Unit>"
        + (buying is null ? "" : $"<BuyingRate>{buying}</BuyingRate>")
        + (middle is null ? "" : $"<MiddleRate>{middle}</MiddleRate>")
        + (selling is null ? "" : $"<SellingRate>{selling}</SellingRate>")
        + "</ExchangeRate>";
}
