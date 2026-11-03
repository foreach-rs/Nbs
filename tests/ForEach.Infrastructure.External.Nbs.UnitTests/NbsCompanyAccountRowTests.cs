using ForEach.Infrastructure.External.Nbs.CompanyAccounts;

namespace ForEach.Infrastructure.External.Nbs.UnitTests;

/// <summary>
/// Reading the jedinstveni registar računa.
///
/// Element names follow `docs/nbs/reference/xsd/GetCompanyAccount.xsd.txt`, so the fixtures are
/// the service's shape rather than a guess at it.
///
/// The register is ACCOUNT-shaped, not company-shaped: a company with four accounts is four rows,
/// each repeating the same name and identifiers. That is the fact most of these tests are about,
/// because a caller that assumed one row per company would enrich a contact from whichever
/// account happened to come back first.
/// </summary>
public class NbsCompanyAccountRowTests
{
    [Fact]
    public void A_row_carries_the_company_and_the_account_together()
    {
        var row = Assert.Single(NbsCompanyAccountRow.Parse(Document(Row())));

        Assert.Equal("160-0000000123456-78", row.Account);
        Assert.Equal("Banca Intesa", row.BankName);
        Assert.Equal("KAFETERIJA CENTAR DOO BEOGRAD", row.CompanyName);
        Assert.Equal("101234567", row.TaxIdentificationNumber);
        Assert.Equal("Knez Mihailova 12", row.Address);
        Assert.Equal("Beograd", row.City);
    }

    [Fact]
    public void The_registers_composite_key_survives_so_a_later_delta_can_be_matched()
    {
        // Bank code, account number and control digit together identify the account in NBS's own
        // terms. Keeping only the formatted string would mean re-parsing it to match a delta, and
        // the formatting is the register's to change.
        var row = Assert.Single(NbsCompanyAccountRow.Parse(Document(Row())));

        Assert.Equal(160, row.BankCode);
        Assert.Equal(123456, row.AccountNumber);
        Assert.Equal(78, row.ControlNumber);
    }

    [Fact]
    public void A_maticni_broj_is_read_as_a_long_rather_than_an_int()
    {
        // The register declares it int and the ORIGIN data declares it wider. A number that
        // overflows does not fail — it silently becomes a different company, which on an
        // enrichment screen is somebody else's name and address filled into your contact.
        var row = Assert.Single(NbsCompanyAccountRow.Parse(
            Document(Row(nationalIdentificationNumber: "12345678901"))));

        Assert.Equal(12345678901L, row.NationalIdentificationNumber);
    }

    [Fact]
    public void One_company_with_several_accounts_is_several_rows()
    {
        // The shape the whole feature has to cope with. A contact form offering the accounts as a
        // list of checkboxes needs all of them; one that took `.First()` would offer whichever the
        // register happened to order first.
        var rows = NbsCompanyAccountRow.Parse(Document(
            Row(account: "160-0000000123456-78", accountNumber: "123456"),
            Row(account: "265-0000000999888-11", accountNumber: "999888", bankCode: "265",
                bankName: "Raiffeisen banka", controlNumber: "11"),
            Row(account: "170-0000000555444-22", accountNumber: "555444", bankCode: "170",
                bankName: "UniCredit Bank", controlNumber: "22")));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("101234567", r.TaxIdentificationNumber));
        Assert.Equal(3, rows.Select(r => r.BankCode).Distinct().Count());
    }

    [Fact]
    public void The_blockade_flag_is_carried_and_not_interpreted()
    {
        // The signal a blocked-account watch reads, and deliberately left as the register's own
        // number: NBS publishes the code book as a separate operation, so a library asserting
        // "1 means blocked" would be claiming something it has not read.
        var rows = NbsCompanyAccountRow.Parse(Document(
            Row(blockadeStatusId: "0"),
            Row(account: "265-0000000999888-11", accountNumber: "999888", blockadeStatusId: "1")));

        Assert.Equal(0, rows[0].BlockadeStatusId);
        Assert.Equal(1, rows[1].BlockadeStatusId);
    }

    [Fact]
    public void The_update_timestamp_is_read_as_a_dateTime_not_as_the_rate_lists_date_format()
    {
        // Two conventions in one API: rate rows come back dd.MM.yyyy as strings, these come back
        // xs:dateTime. Parsing one with the other's rules is how a delta sync starts asking for
        // the wrong window — and a delta sync that asks for the wrong window silently misses the
        // blockade it exists to catch.
        var row = Assert.Single(NbsCompanyAccountRow.Parse(
            Document(Row(updateDate: "2026-08-19T14:32:05"))));

        Assert.Equal(new DateTime(2026, 8, 19, 14, 32, 5, DateTimeKind.Unspecified), row.UpdateDate);
    }

    [Fact]
    public void A_missing_update_timestamp_is_null_rather_than_a_default_date()
    {
        // DateTime.MinValue in a delta window would ask NBS for everything since the year one,
        // which is refused as a caller mistake — and reads in the log as a service problem.
        var row = Assert.Single(NbsCompanyAccountRow.Parse(Document(Row(updateDate: null))));

        Assert.Null(row.UpdateDate);
    }

    [Fact]
    public void The_origin_variant_is_read_by_the_same_parser()
    {
        // GetCompanyAccountOriginByNationalIdNumber returns the same fields under a different row
        // name — the company's own bank's version of its details, sometimes fresher. Reading both
        // names means that call needs no second parser to keep in step with this one.
        var rows = NbsCompanyAccountRow.Parse(
            "<CompanyAccountDataSet>"
            + Row().Replace("CompanyAccount>", "CompanyAccountOrigin>", StringComparison.Ordinal)
            + "</CompanyAccountDataSet>");

        Assert.Equal("KAFETERIJA CENTAR DOO BEOGRAD", Assert.Single(rows).CompanyName);
    }

    [Fact]
    public void A_row_with_no_account_number_is_skipped()
    {
        // The register's key is the bank code, the account number and the control digit together.
        // A row missing one cannot be matched against a later delta, which is the only reason
        // those three are kept at all.
        var rows = NbsCompanyAccountRow.Parse(Document(
            Row(),
            "<CompanyAccount><CompanyName>Bez računa</CompanyName></CompanyAccount>"));

        Assert.Equal("KAFETERIJA CENTAR DOO BEOGRAD", Assert.Single(rows).CompanyName);
    }

    [Fact]
    public void An_empty_register_answer_is_no_rows_rather_than_a_failure()
    {
        // What an unknown PIB looks like. The screen says "nothing found"; it is not an error.
        Assert.Empty(NbsCompanyAccountRow.Parse("<CompanyAccountDataSet />"));
        Assert.Empty(NbsCompanyAccountRow.Parse(""));
        Assert.Empty(NbsCompanyAccountRow.Parse("<CompanyAccountDataSet><CompanyAccount>"));
    }

    private static string Document(params string[] rows) =>
        "<CompanyAccountDataSet>" + string.Join("", rows) + "</CompanyAccountDataSet>";

    private static string Row(
        string account = "160-0000000123456-78",
        string bankCode = "160",
        string accountNumber = "123456",
        string controlNumber = "78",
        string bankName = "Banca Intesa",
        string nationalIdentificationNumber = "20123456",
        string blockadeStatusId = "0",
        string? updateDate = "2026-08-19T14:32:05") =>
        "<CompanyAccount>"
        + $"<Account>{account}</Account>"
        + $"<BankCode>{bankCode}</BankCode>"
        + $"<AccountNumber>{accountNumber}</AccountNumber>"
        + $"<ControlNumber>{controlNumber}</ControlNumber>"
        + "<BankID>7</BankID>"
        + $"<BankName>{bankName}</BankName>"
        + "<CompanyName>KAFETERIJA CENTAR DOO BEOGRAD</CompanyName>"
        + $"<NationalIdentificationNumber>{nationalIdentificationNumber}</NationalIdentificationNumber>"
        + "<TaxIdentificationNumber>101234567</TaxIdentificationNumber>"
        + "<Address>Knez Mihailova 12</Address>"
        + "<City>Beograd</City>"
        + "<MunicipalityCode>70</MunicipalityCode>"
        + "<MunicipalityName>Stari grad</MunicipalityName>"
        + "<ActivityCode>5630</ActivityCode>"
        + "<ActivityName>Delatnost pripremanja i posluživanja pića</ActivityName>"
        + "<CompanyAccountStatusID>1</CompanyAccountStatusID>"
        + $"<CompanyAccountBlockadeStatusID>{blockadeStatusId}</CompanyAccountBlockadeStatusID>"
        + "<CompanyAccountTypeID>1</CompanyAccountTypeID>"
        + "<LegalUserTypeID>1</LegalUserTypeID>"
        + "<InitializationDate>2019-03-04T00:00:00</InitializationDate>"
        + (updateDate is null ? "" : $"<UpdateDate>{updateDate}</UpdateDate>")
        + "</CompanyAccount>";
}
