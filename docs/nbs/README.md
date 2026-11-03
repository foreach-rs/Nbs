# NBS web service reference

The National Bank of Serbia's web services as they were published on 2026-08-20, kept here so
the parsers can be checked against the authority's own contract without a membership.

| Folder | Contents |
|---|---|
| `reference/*.wsdl` | The six live WSDLs: Core, Exchange Rate and Company Account, each in a typed and an XML flavour |
| `reference/xsd/*.xsd.txt` | The 37 official response schemas, one per operation — the recorded-XML source for parser tests |

The services are free to registered members. Membership is a signed paper request to NBS; once
processed it yields the user name, password and licence GUID that `NbsCredentials` carries in the
SOAP header of every call. Quota is metered per licence, which is why the live tests are opt-in.
