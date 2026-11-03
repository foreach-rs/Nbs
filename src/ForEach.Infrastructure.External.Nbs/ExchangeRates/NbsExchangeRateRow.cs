using System.Globalization;
using System.Xml.Linq;

namespace ForEach.Infrastructure.External.Nbs.ExchangeRates;

/// <summary>
/// One currency's line on one published list.
///
/// A row carries whichever rate its list type publishes and nulls for the rest: the middle list
/// (type 3) fills <see cref="MiddleRate"/> only, the foreign-exchange list (type 1) fills buying
/// and selling. That is why a day's import is two calls rather than one, and why a null here means
/// "this list does not publish that" rather than "the rate is zero".
/// </summary>
/// <param name="Date">
/// **The list's own date, which is not always the date that was asked for.** A daily list applies
/// from 08:00 of its date until 08:00 the next day, and on a weekend or holiday the previous
/// working day's list stays in force — so a Saturday query returns Friday's list, carrying
/// Friday's date. A caller that files rates under the date it queried will re-file Friday's list
/// under Saturday, twice a week, for ever.
/// </param>
/// <param name="Unit">
/// How many units of the currency the rate is quoted for. Most are 1; JPY and HUF publish per 100.
/// Dropping it is a hundredfold error that looks entirely plausible on screen.
/// </param>
/// <param name="ListTypeId">Which list this is: 1 foreign exchange, 3 middle.</param>
/// <param name="ListNumber">The list's number within its year.</param>
/// <param name="CurrencyCode">ISO 4217 alphabetic code, upper-case.</param>
/// <param name="NumericCode">ISO 4217 numeric code.</param>
/// <param name="BuyingRate">Buying rate, or null when the list does not publish one.</param>
/// <param name="MiddleRate">Middle rate, or null when the list does not publish one.</param>
/// <param name="SellingRate">Selling rate, or null when the list does not publish one.</param>
public sealed record NbsExchangeRateRow(
    DateOnly Date,
    int ListTypeId,
    int ListNumber,
    string CurrencyCode,
    int NumericCode,
    int Unit,
    decimal? BuyingRate,
    decimal? MiddleRate,
    decimal? SellingRate)
{
    /// <summary>
    /// Reads an `ExchangeRateDataSet` document into rows.
    ///
    /// Static and pure: this is the half of the integration most likely to be wrong and the half
    /// no server is needed to test. The recorded documents in `docs/nbs/reference/xsd` are the
    /// source for its fixtures.
    ///
    /// A row whose date or currency code will not parse is SKIPPED rather than failing the batch.
    /// One malformed line should not cost the other thirty currencies their rates for the day —
    /// and the import counts what it wrote, so a silent drop still shows as a smaller number.
    /// </summary>
    public static List<NbsExchangeRateRow> Parse(string exchangeRateDataSetXml)
    {
        var rows = new List<NbsExchangeRateRow>();

        if (string.IsNullOrWhiteSpace(exchangeRateDataSetXml)) return rows;

        XDocument document;

        try
        {
            document = XDocument.Parse(exchangeRateDataSetXml);
        }
        catch (System.Xml.XmlException)
        {
            return rows;
        }

        foreach (var row in document.Descendants().Where(e => e.Name.LocalName == "ExchangeRate"))
        {
            var code = Text(row, "CurrencyCodeAlfaChar");

            if (string.IsNullOrWhiteSpace(code)) continue;
            if (ReadDate(Text(row, "Date")) is not { } date) continue;

            rows.Add(new NbsExchangeRateRow(
                date,
                Int(row, "ExchangeRateListTypeID") ?? 0,
                Int(row, "ExchangeRateListNumber") ?? 0,
                code.Trim().ToUpperInvariant(),
                Int(row, "CurrencyCode") ?? 0,

                // A unit of zero would divide by nothing later. Absent or unreadable means one,
                // which is true of every currency but the two that publish per hundred.
                Int(row, "Unit") is { } unit && unit > 0 ? unit : 1,

                Rate(row, "BuyingRate"),
                Rate(row, "MiddleRate"),
                Rate(row, "SellingRate")));
        }

        return rows;
    }

    private static string? Text(XElement row, string name) =>
        row.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static int? Int(XElement row, string name) =>
        int.TryParse(Text(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// A rate, invariant — and zero read as ABSENT rather than as a number.
    ///
    /// Invariant because the document writes `117.2543` with a POINT whatever the caller's culture
    /// is, and parsing that under a Serbian culture — where the point is a group separator — reads
    /// it as 1.172.543. The wrong answer is not an error; it is a rate ten thousand times too
    /// large.
    ///
    /// Zero because the live service says so. The middle list does not OMIT the buying and selling
    /// elements it has no value for — it sends `0.0000`, which was discovered by an integration
    /// test asserting they would be absent. A rate of zero is not a rate: nobody buys a currency
    /// for nothing, and a caller that took it at face value would file "one euro costs zero
    /// dinars" against the day the middle list was imported. Every consumer here treats null as
    /// "this list does not publish that", which is exactly what a zero means.
    /// </summary>
    private static decimal? Rate(XElement row, string name) =>
        decimal.TryParse(Text(row, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : null;

    /// <summary>
    /// The row date, which NBS writes as `dd.MM.yyyy` — a string, not an `xs:date`, and not the
    /// `yyyyMMdd` its own inputs take. The two formats in one conversation are its convention.
    /// </summary>
    private static DateOnly? ReadDate(string? text) =>
        DateOnly.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
}
