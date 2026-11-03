# ForEach.Infrastructure.External.Nbs

The National Bank of Serbia's web services — daily exchange rates and company account status —
behind a client with no dependencies, so more than one product can use it.

## What it does not do

It persists nothing, retries nothing, and decides no policy. Which rate list a business uses, when
a blockade matters and what to do about it are questions about somebody's ledger, not about NBS.

## Credentials

`NbsCredentials` carries the three values that go in the SOAP header of every request: user name,
password and the licence GUID NBS issues once the signed paper request has been processed. There is
no handshake and no token — that is their design, and HTTPS is the whole of the protection.

`IsConfigured` answers whether a call is worth making at all, so a host can run dark until the
membership exists rather than failing a scheduled job every morning.

## Results and errors

`NbsResult<T>` keeps two kinds of failure apart: a fault NBS reported, and a transport problem that
never reached them. `NbsError.Kind` sorts their seven codes into the three responses a host has —
`Configuration` (credentials, licence, quota: it repeats until somebody acts), `Transient` (retry),
and `CallerMistake` (the parameters were wrong). An unknown code is treated as transient, because a
wasted retry costs one scheduled run and giving up costs the data.

## Testing

`…Nbs.UnitTests` covers the envelope building and fault reading against recorded XML.
`…Nbs.IntegrationTests` makes live calls and skips itself unless the `NBS_*` environment variables
are set, so it never runs in CI by accident.
