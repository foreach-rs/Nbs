using System.Globalization;
using System.Xml.Linq;

namespace ForEach.Infrastructure.External.Nbs.CompanyAccounts;

/// <summary>
/// One bank account in NBS's jedinstveni registar računa, with the company that holds it.
///
/// The register is account-shaped rather than company-shaped: a company with four accounts comes
/// back as four rows, each repeating the same name, address and identifiers. Callers that want a
/// company group the rows themselves — the alternative, guessing which row is authoritative, is
/// the kind of decision a client library should not be making on somebody's behalf.
/// </summary>
/// <param name="NationalIdentificationNumber">
/// Matični broj. Parsed as a **long** although the register declares it as an int: the origin
/// data — the same field as reported by the company's own bank — declares it wider, and a number
/// that overflows silently becomes a different company.
/// </param>
/// <param name="BlockadeStatusId">
/// The blocked-account flag, and the reason this register is worth watching rather than only
/// reading once. Zero is the ordinary state; anything else means money cannot move through the
/// account, which is worth knowing BEFORE shipping to the customer who holds it.
///
/// Deliberately not interpreted here. NBS publishes the code book as its own operation, and a
/// library that hard-coded "1 means blocked" would be asserting something it has not read.
/// </param>
/// <param name="Account">The account as the register formats it: bank code, number and control digits.</param>
/// <param name="BankCode">The bank's three-digit code.</param>
/// <param name="AccountNumber">The account number within the bank.</param>
/// <param name="ControlNumber">The two-digit control number.</param>
/// <param name="BankName">The bank's name.</param>
/// <param name="CompanyName">The holder's registered name.</param>
/// <param name="TaxIdentificationNumber">PIB of the holder.</param>
/// <param name="Address">The holder's street address.</param>
/// <param name="City">The holder's city.</param>
/// <param name="MunicipalityName">The holder's municipality.</param>
/// <param name="ActivityCode">The holder's activity code (šifra delatnosti).</param>
/// <param name="ActivityName">The activity's name.</param>
/// <param name="AccountStatusId">The register's account-status code, uninterpreted.</param>
/// <param name="AccountTypeId">The register's account-type code, uninterpreted.</param>
/// <param name="UpdateDate">When the register last changed this row, or null when it did not say.</param>
public sealed record NbsCompanyAccountRow(
    string Account,
    long BankCode,
    long AccountNumber,
    int ControlNumber,
    string BankName,
    string CompanyName,
    long NationalIdentificationNumber,
    string TaxIdentificationNumber,
    string Address,
    string City,
    string MunicipalityName,
    int ActivityCode,
    string ActivityName,
    int AccountStatusId,
    int BlockadeStatusId,
    int AccountTypeId,
    DateTime? UpdateDate)
{
    /// <summary>
    /// Reads a `CompanyAccountDataSet` document into rows. Static and pure, as the rate parser is.
    ///
    /// A row with no account number is skipped: the register's key is the bank code, the account
    /// number and the control digit together, and a row missing one of them cannot be matched
    /// against anything later — which is the whole point of keeping them.
    /// </summary>
    public static List<NbsCompanyAccountRow> Parse(string companyAccountDataSetXml)
    {
        var rows = new List<NbsCompanyAccountRow>();

        if (string.IsNullOrWhiteSpace(companyAccountDataSetXml)) return rows;

        XDocument document;

        try
        {
            document = XDocument.Parse(companyAccountDataSetXml);
        }
        catch (System.Xml.XmlException)
        {
            return rows;
        }

        // Both `CompanyAccount` and `CompanyAccountOrigin`: the origin operation returns the same
        // shape under a different row name, and reading both means the origin call needs no
        // second parser to keep in step with this one.
        foreach (var row in document.Descendants()
                     .Where(e => e.Name.LocalName is "CompanyAccount" or "CompanyAccountOrigin"))
        {
            var accountNumber = Long(row, "AccountNumber");

            if (accountNumber is null) continue;

            rows.Add(new NbsCompanyAccountRow(
                Text(row, "Account") ?? "",
                Long(row, "BankCode") ?? 0,
                accountNumber.Value,
                Int(row, "ControlNumber") ?? 0,
                Text(row, "BankName") ?? "",
                Text(row, "CompanyName") ?? "",
                Long(row, "NationalIdentificationNumber") ?? 0,
                Text(row, "TaxIdentificationNumber") ?? "",
                Text(row, "Address") ?? "",
                Text(row, "City") ?? "",
                Text(row, "MunicipalityName") ?? "",
                Int(row, "ActivityCode") ?? 0,
                Text(row, "ActivityName") ?? "",
                Int(row, "CompanyAccountStatusID") ?? 0,
                Int(row, "CompanyAccountBlockadeStatusID") ?? 0,
                Int(row, "CompanyAccountTypeID") ?? 0,
                Timestamp(row, "UpdateDate")));
        }

        return rows;
    }

    private static string? Text(XElement row, string name) =>
        row.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static long? Long(XElement row, string name) =>
        long.TryParse(Text(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? Int(XElement row, string name) =>
        int.TryParse(Text(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// A timestamp, invariant and round-trippable. These come back as `xs:dateTime` — unlike the
    /// rate list's dates, which are `dd.MM.yyyy` strings. Two conventions in one API is NBS's, and
    /// parsing one with the other's rules is how a delta sync starts asking for the wrong window.
    /// </summary>
    private static DateTime? Timestamp(XElement row, string name) =>
        DateTime.TryParse(Text(row, name), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
}
