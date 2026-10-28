# Exchange Integration Service - Milestone 1: EWS connectivity and Windows authentication probe

A small .NET 10 application that proves, against an **on-premises Exchange Server Subscription Edition (SE)**, that

1. the Exchange Web Services (EWS) endpoint is reachable over HTTPS,
2. **Windows Integrated Authentication** with the identity of the running process is accepted, and
3. that identity can open the **Inbox of one configured mailbox**.

It does this with a single read-only SOAP `GetFolder` request and reports what it actually established.

> **Status: live Exchange connectivity is NOT YET VERIFIED.**
> The automated tests use a mocked HTTP transport and a loopback socket server. Nothing in this repository has been run
> against a real Exchange server. Treat the first successful `--probe` run against the customer's Exchange environment,
> under the intended identity, as the validation event.

This is the first milestone of a larger Exchange Integration Service (retrieve mail and attachments, store them in Azure
Blob Storage, move/copy messages, send messages and replies). **Only the authentication and mailbox-access proof of
concept is implemented here.** Do not start on ingestion or sending until this probe has been validated.

## What it does and does not do

| Does | Does not |
|---|---|
| One read-only `GetFolder` for the Inbox of the configured mailbox | Modify any mailbox content |
| Windows Integrated Authentication (Negotiate/NTLM) with the process identity | Read message bodies or attachments |
| Report the Windows identity, HTTP result, EWS response code and a failure category | Store, move, copy, send, poll or synchronize anything |
| Return a distinct non-zero exit code per failure category | Use Basic authentication, OAuth, Microsoft Graph, IMAP or SMTP |
| Validate configuration at startup | Change Exchange settings or permissions |
| Run as a console app, structured for a Windows Service later | Install or deploy a Windows Service |

## Prerequisites

- **Windows host.** A domain-joined Windows 10/11 or Windows Server machine supported by .NET 10 (Windows Server 2016 or
  later recommended). Kerberos needs line of sight to a domain controller. The code compiles and the unit tests run on
  other operating systems, but the probe itself is meant for Windows.
- **.NET 10.** The .NET 10 SDK to build and run from source (`global.json` selects the newest installed 10.0.x SDK), or
  the .NET 10 runtime to run published output.
- **An Exchange Server SE EWS endpoint** reachable over HTTPS, normally `https://<exchange-host>/EWS/Exchange.asmx`,
  with a certificate the host trusts and whose names cover `<exchange-host>`.
- **An identity to authenticate as.** The dedicated Active Directory service identity that the service will eventually
  run as, or an approved development identity.
- **EWS enabled for that identity**: not switched off or blocked by an EWS access policy or throttling policy.
- **Mailbox permissions**: the identity must be allowed to see the target mailbox's Inbox (see
  [Exchange administrator checklist](#exchange-administrator-checklist)).

## Configuration

Settings come from `appsettings.json` and can be overridden without code changes. Later sources win:
`appsettings.json` -> `appsettings.<Environment>.json` -> environment variables -> command-line arguments.

```json
{
  "Exchange": {
    "EwsUrl": "https://mail.contoso.com/EWS/Exchange.asmx",
    "Mailbox": "ap-invoices@contoso.com",
    "AccessMode": "Delegated",
    "TimeoutSeconds": 30
  }
}
```

| Setting | Environment variable | Default | Rules |
|---|---|---|---|
| `Exchange:EwsUrl` | `Exchange__EwsUrl` | none, required | Absolute **HTTPS** URL, no embedded user name or password |
| `Exchange:Mailbox` | `Exchange__Mailbox` | none, required | Plain primary SMTP address, no display name |
| `Exchange:AccessMode` | `Exchange__AccessMode` | `Delegated` | Only `Delegated` is supported; `Impersonation` is reserved for a later milestone and is rejected |
| `Exchange:TimeoutSeconds` | `Exchange__TimeoutSeconds` | `30` | 1 to 300; covers the whole request including the authentication handshake |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` | `Information` | Use `Debug` for more detail |

The values shipped in `appsettings.json` are **placeholders** (`contoso.com`). Replace them with environment variables or
a local override file. `appsettings.*.json` files other than `appsettings.json` are git-ignored so real host and mailbox
names are not committed by accident.

All problems are reported at once, before anything is sent to Exchange, for example:

```text
Configuration errors (nothing was sent to Exchange):
  - Exchange:EwsUrl must use HTTPS (found 'http'); Windows authentication must not run over an unencrypted channel.
  - Exchange:Mailbox must be a plain SMTP address such as ap-invoices@contoso.com (no display name or surrounding spaces).
```

## Running locally

```powershell
dotnet build
dotnet test

$env:Exchange__EwsUrl  = "https://<your-exchange-host>/EWS/Exchange.asmx"
$env:Exchange__Mailbox = "<approved-mailbox>@<your-domain>"

dotnet run -- --probe
$LASTEXITCODE            # 0 only when the probe succeeded
```

The probe runs as the Windows identity of the process you start it from and prints it as `Windows Identity`. There is
deliberately no setting for an identity, user name or password. To probe as another account, such as the dedicated
service account, start the process as that account; see
[Run the probe under the intended identity](#run-the-probe-under-the-intended-identity).

A published build runs the same way: `dotnet publish -c Release -o <folder>`, then `<folder>\ExchangeIntegration.exe --probe`.

- `--probe` runs the one-time test and exits. `--help` prints usage. Running without arguments prints usage and exits with 64.
- The report goes to **stdout**; structured logs go to **stderr**. Capture them separately with
  `dotnet run -- --probe 2> probe.log`.

> **Security note.** Windows Integrated Authentication answers any Negotiate or NTLM challenge with the credentials of
> the process. Point the probe only at the customer's own Exchange host, never at an arbitrary URL.

### Example output on success

```text
Exchange Integration - Connectivity Probe

Runtime:
  Windows Identity: CONTOSO\svc-invoiceews
  Access Mode: Delegated

Exchange:
  Endpoint: https://mail.contoso.com/EWS/Exchange.asmx
  Target Mailbox: ap-invoices@contoso.com

Results:
  HTTP Connection: SUCCESS
  Windows Authentication: SUCCESS
  Mailbox Access: SUCCESS
  EWS Operation: GetFolder
  EWS Response: NoError
  HTTP Status: 200
  Exchange Server Version: 15.2.2562.17
  Inbox Folder Id: returned (fingerprint 1A2B3C4D; the identifier is not printed)
  Negotiated Protocol: NOT VERIFIED (Kerberos versus NTLM cannot be told from this response; see README)

Overall: SUCCESS
```

This is an illustration, not an assumed result. A stage is reported as `SUCCESS` only when the evidence proves it:
reaching the endpoint is never taken as proof of authentication. The report also says `NOT CONFIRMED` (reached, but the
evidence does not decide) and `NOT EVALUATED` (never reached).

### Example output on failure

```text
Results:
  HTTP Connection: SUCCESS
  Windows Authentication: FAILED
  Mailbox Access: NOT EVALUATED
  EWS Operation: GetFolder
  EWS Response: (none)
  HTTP Status: 401

Failure:
  Category: Authentication
  Detail: HTTP 401: Exchange did not accept the Windows credentials of this process. The server offered: Negotiate, NTLM.
  Likely Area: Windows identity / Exchange authentication
  Next Step: Exchange did not accept the Windows credentials (HTTP 401). Confirm the identity shown above ...

Overall: FAILURE (exit code 5)
```

### Exit codes

| Code | Category | Meaning | Next step likely belongs to |
|---:|---|---|---|
| 0 | none | Authentication and Inbox access validated (`NoError`) | - |
| 1 | `Unexpected` | An error the probe did not anticipate | This application: rerun with `Logging__LogLevel__Default=Debug` |
| 2 | `Configuration` | Settings invalid; nothing was sent | Configuration |
| 3 | `Connectivity` | DNS, TCP or proxy failure; endpoint not reached | Network |
| 4 | `TlsFailure` | TLS or server certificate validation failed | Network and certificate trust |
| 5 | `Authentication` | HTTP 401: Windows credentials not accepted | Windows identity / Exchange authentication |
| 6 | `HttpAccessDenied` | HTTP 403: refused before mailbox permissions were evaluated | Exchange authentication / Exchange service |
| 7 | `MailboxAuthorization` | EWS answered but denied the mailbox (for example `ErrorAccessDenied`) | Mailbox permissions |
| 8 | `MailboxResolution` | EWS could not resolve the mailbox (for example `ErrorNonExistentMailbox`) | Mailbox / configuration |
| 9 | `SoapProtocol` | Response was not a valid EWS SOAP response | SOAP implementation / endpoint |
| 10 | `Timeout` | No complete response within `Exchange:TimeoutSeconds` | Network / Exchange service |
| 11 | `UnexpectedRedirect` | The endpoint redirected; redirects are never followed | Configuration / Exchange authentication |
| 12 | `UnexpectedHttpStatus` | A status that is not an EWS response (404, 5xx, 407, ...) | Exchange service / network |
| 13 | `EwsError` | EWS returned another error code (kept verbatim in the report) | Exchange service |
| 64 | - | Usage error: no command given | - |
| 130 | `Cancelled` | Cancelled with Ctrl+C | - |

### Run the probe under the intended identity

There is deliberately **no setting for an identity, user name or password**: nothing in `appsettings.json`, an
environment variable or a command-line argument selects one, and the probe never asks for credentials. It authenticates
as the **Windows identity of the process** and prints it as `Windows Identity`. To probe as another account, start the
process as that account.

**A successful run under a developer's personal identity does not establish that the eventual Windows service identity
will work**: mailbox permissions, EWS policies, Kerberos SPNs, network paths and the certificate trust of the host can
all differ. Repeat the probe as the real identity, on the real host, before relying on it.

1. **Development identity.** `dotnet run -- --probe` as the signed-in user. Useful for URL, certificate and mailbox
   configuration; says nothing about the service identity.
2. **A dedicated service account with a known password.** Publish once, then use `runas` to start a command prompt as
   that account and run the published executable from it:

   ```powershell
   # As yourself, once: publish to a folder that the service account can read and execute.
   dotnet publish -c Release -o C:\Apps\ExchangeIntegration

   # Start a command prompt as the service account. Type its password at the prompt.
   runas /user:CONTOSO\svc-invoiceews cmd
   ```

   In the new command prompt, which runs as that account:

   ```bat
   whoami
   cd /d C:\Apps\ExchangeIntegration
   ExchangeIntegration.exe --probe --Exchange:EwsUrl=https://<your-exchange-host>/EWS/Exchange.asmx --Exchange:Mailbox=<approved-mailbox>@<your-domain>
   echo %ERRORLEVEL%
   ```

   - **Confirm the identity.** `whoami` must print the account you intended (for example `contoso\svc-invoiceews`) and
     the probe's `Windows Identity:` line must name the same account (the case can differ). If either shows your own
     account, or they disagree, stop: the result does not describe the service account.
   - **Settings do not carry over.** `runas` starts the account's own environment, not that of the shell you ran it from
     (unless you pass `/env`), so `$env:Exchange__EwsUrl` and `$env:Exchange__Mailbox` set in your shell are **not**
     visible there. In the new prompt, set them again (`set Exchange__EwsUrl=...`), edit `appsettings.json` in the
     published folder, or pass `--Exchange:EwsUrl=...` and `--Exchange:Mailbox=...` as shown above.
   - **Access to the folder.** The account needs read and execute access to the published folder, so do not publish into
     your own profile folder.
   - **Do not use `runas /netonly`.** It changes the credentials used for network authentication but not the process
     identity, so the identity the probe prints would no longer be the identity Exchange sees. Avoid `/savecred` too: it
     stores the account's password where anything running as you can reuse it.
   - **If the account cannot log on to this machine** (for example it belongs to a domain with no trust, or policy denies
     it interactive logon, which is often done deliberately for service accounts), `runas` cannot start the prompt. Run
     the probe on a host that can log on the account, or have an administrator run it as that account by an approved
     method, such as a scheduled task that writes the output to a file. Never put the password in a script or on a
     command line.

## Kerberos versus NTLM

`UseDefaultCredentials = true` asks Windows to authenticate with the process identity **using whichever of Negotiate
(Kerberos) or NTLM it can complete**. It does **not** guarantee Kerberos. A fallback to NTLM happens silently, for example
when the host name in `Exchange:EwsUrl` is an IP address or an alias with no matching SPN, when an SPN is missing or
duplicated, or when the client cannot reach a domain controller.

The probe therefore **never reports Kerberos as confirmed**, and a successful HTTP response is not evidence of it. The
report always prints `Negotiated Protocol: NOT VERIFIED`. To establish the protocol, use diagnostics the customer
approves, for example:

1. **Exchange server Security log (the authoritative evidence).** Event ID 4624 (successful logon, Logon Type 3) records
   the account, the *Logon Process* (`Kerberos` or `NtLmSsp`) and the *Authentication Package* (`Kerberos` or `NTLM`).
   Run the probe, then on the Exchange server (an administrator, with logon auditing enabled):

   ```powershell
   $xpath = "*[System[EventID=4624]] and *[EventData[Data[@Name='TargetUserName']='svc-invoiceews']]"
   Get-WinEvent -LogName Security -FilterXPath $xpath -MaxEvents 20 | ForEach-Object {
       $data = ([xml]$_.ToXml()).Event.EventData.Data
       [pscustomobject]@{
           Time         = $_.TimeCreated
           LogonType    = ($data | Where-Object Name -eq 'LogonType').'#text'
           LogonProcess = ($data | Where-Object Name -eq 'LogonProcessName').'#text'
           AuthPackage  = ($data | Where-Object Name -eq 'AuthenticationPackageName').'#text'
       }
   }
   ```

2. **Domain controller Security log.** Event ID 4769 (Kerberos service ticket requested) for the identity, with a service
   name matching the Exchange SPN, shows Kerberos was used. A missing 4769 is not proof of NTLM because the client may have
   used a cached ticket.
3. **IIS logs on the Exchange server** (`%SystemDrive%\inetpub\logs\LogFiles\W3SVC1`). Filter on the probe's User-Agent
   `ExchangeIntegration-EwsProbe/1.0` and read `cs-username`, `sc-status`, `sc-substatus`. On a new connection Kerberos
   typically shows one 401 challenge followed by 200, NTLM shows an extra 401 round trip. This is only an indication,
   because connection reuse changes the pattern.
4. **Client ticket cache.** `klist` in the identity's own session (for example the `runas` prompt described above) lists
   a `HTTP/<ews-host>` service ticket after a Kerberos-authenticated call (`klist purge` first for a clean test). The
   session of a Windows service cannot be inspected from an interactive session, so use the server-side evidence for it.
5. **SPN check.** `setspn -Q HTTP/<ews-host>` should return exactly one account; `setspn -X` finds duplicates. Kerberos
   works only for host names that have an SPN, so use the same name in `Exchange:EwsUrl`.

Record which protocol was observed, because a fallback to NTLM may be unacceptable for the customer's security policy.

## Exchange administrator checklist

The probe and this repository **never change Exchange settings or permissions**. Everything below is a read-only check
for the Exchange administrator, using the customer's change process for any fix. Replace the placeholder names.

1. **EWS endpoint.** Confirm the URL in the configuration is the real EWS URL and the certificate covers its host name.

   ```powershell
   Get-WebServicesVirtualDirectory -Server <exchange-server> | Format-List Identity, InternalUrl, ExternalUrl
   ```

2. **Windows authentication.** The probe only offers Windows credentials and never falls back to Basic, so Windows
   Integrated Authentication must be enabled on the EWS virtual directory (IIS providers `Negotiate` and `NTLM`).

   ```powershell
   Get-WebServicesVirtualDirectory -Server <exchange-server> | Format-List Identity, WindowsAuthentication, BasicAuthentication, OAuthAuthentication, WSSecurityAuthentication
   ```

3. **Delegated mailbox or folder permissions.** `GetFolder` on the Inbox succeeds only if the identity may see that folder.

   ```powershell
   Get-MailboxPermission -Identity ap-invoices@contoso.com -User 'CONTOSO\svc-invoiceews' | Format-List User, AccessRights, IsInherited, Deny
   Get-MailboxFolderPermission -Identity 'ap-invoices@contoso.com:\Inbox'
   ```

   Folder-level permissions can typically be granted only to mail-enabled principals (a mailbox, mail user or
   mail-enabled security group), whereas Full Access can be granted to any security principal such as a service account;
   agree the model with the customer. Later milestones need more rights than this one (read, move or copy, and send each
   need their own), so decide those before building them.
4. **EWS access policies.** EWS can be switched off, or restricted to allow/block lists matched against the client
   **User-Agent**. Check the organization and both the service identity and the target mailbox. The probe sends
   `User-Agent: ExchangeIntegration-EwsProbe/1.0`. Also review the throttling policy applied to the service identity.

   ```powershell
   Get-OrganizationConfig | Format-List EwsEnabled, EwsApplicationAccessPolicy, EwsAllowList, EwsBlockList
   Get-CASMailbox -Identity ap-invoices@contoso.com | Format-List EwsEnabled, EwsApplicationAccessPolicy, EwsAllowList, EwsBlockList
   ```

5. **Kerberos versus NTLM.** Confirm an SPN exists for the EWS host name (`setspn -Q HTTP/mail.contoso.com`), that no
   duplicates exist, and, for a load-balanced namespace, how Kerberos is configured:

   ```powershell
   Get-ClientAccessService -Identity <exchange-server> -IncludeAlternateServiceAccountCredentialStatus | Format-List Name, AlternateServiceAccountConfiguration
   ```

   Decide whether NTLM is acceptable as a fallback, or whether it must be disabled for this identity.
6. **Unattended service account eligibility.** The identity must be able to authenticate with no person present:
   - the account: enabled, password that does not expire unexpectedly, no smart card required, no logon-hour
     restriction, and not a member of the Protected Users group (it blocks NTLM);
   - the right to log on as a service (the future service) or as a batch job (a scheduled-task test) on the host;
   - clocks within the Kerberos tolerance (5 minutes by default) of the domain, and HTTPS (TCP 443) reachable from the host.

## Troubleshooting

| What you see | Likely cause | What to check |
|---|---|---|
| `Connectivity`: *DNS name resolution failed* | Wrong host name or DNS not reachable from this host | `Resolve-DnsName <host>`; spelling of `Exchange:EwsUrl` |
| `Connectivity`: *connection could not be established* | Firewall, wrong port, proxy | `Test-NetConnection <host> -Port 443` |
| `TlsFailure`: *UntrustedRoot* / *PartialChain* | Host does not trust the CA that issued the Exchange certificate | Install the CA chain in the machine store. **Do not disable validation**; the application has no switch for it |
| `TlsFailure`: *RemoteCertificateNameMismatch* | URL host name is not in the certificate | Use a name the certificate covers |
| `Timeout` | Slow or blocked network path, Exchange/IIS under load, slow authentication | Network path, Exchange health, domain controller reachability; raise `Exchange:TimeoutSeconds` last |
| `Authentication` (401), server offered `Negotiate, NTLM` | Credentials rejected: disabled, locked or expired account, no trust to the Exchange domain, clock skew, Kerberos failing with NTLM blocked, Protected Users | Identity shown by the probe; server Security log (4625 failures); SPNs; time sync |
| `Authentication` (401), server offered only `Basic` | Windows authentication is not enabled on the EWS virtual directory | Administrator step 2. The probe never tries Basic |
| `HttpAccessDenied` (403) | EWS disabled or blocked by access policy or User-Agent list, IP restriction, WAF or proxy | Administrator step 4; IIS `sc-substatus` |
| `UnexpectedRedirect` (3xx) | URL points at OWA, ADFS, another host, or HTTP-to-HTTPS rewriting | Use the final EWS URL. Redirects are never followed so credentials are never forwarded |
| `UnexpectedHttpStatus` 404 | Wrong path | Use `/EWS/Exchange.asmx` |
| `UnexpectedHttpStatus` 407 | A proxy needs authentication | Bypass the proxy for the Exchange host or use an approved proxy configuration |
| `UnexpectedHttpStatus` 5xx | Exchange or IIS fault, or overload | Exchange and IIS logs |
| `SoapProtocol`: *not well-formed XML* / *not a SOAP envelope* | An HTML page (sign-in, proxy, WAF) answered, or the URL is not EWS | Open the URL in a browser; check proxies and load balancers. Rule these out before suspecting the SOAP implementation |
| `MailboxAuthorization`: `ErrorAccessDenied` (HTTP 200) | **Authenticated, but not permitted on this mailbox** | Administrator step 3. Permission changes can take a while to take effect |
| `MailboxResolution`: `ErrorNonExistentMailbox`, `ErrorInvalidSmtpAddress`, `ErrorNonPrimarySmtpAddress` | Not the primary SMTP address of an existing mailbox hosted on this Exchange | Check `Exchange:Mailbox`; ask the administrator whether the identity may see it |
| `EwsError`: `ErrorServerBusy` | Throttling or load (usually transient) | Retry later; throttling policy |
| `EwsError`: `ErrorInvalidServerVersion` | The server rejected the requested schema version | Report it; the request asks for `Exchange2013_SP1` |
| Works as me, fails as the service | Different permissions, policies, SPN or network | Run the probe as the intended identity (see above) |

**Distinguishing the two failures that matter most:** HTTP 401 (`Authentication`, exit 5) means Exchange did not accept
the Windows identity, so look at the identity and Exchange authentication. `ErrorAccessDenied` inside an HTTP 200
(`MailboxAuthorization`, exit 7) means Exchange authenticated the identity and then refused the mailbox, so look at
mailbox permissions. The report shows `Windows Authentication: SUCCESS` for the latter.

## Testing

```powershell
dotnet test
```

The tests need no Exchange server. They use a mocked HTTP transport and cover: a successful `GetFolder`; HTTP 401 and
403; SOAP-level access denial; mailbox resolution errors; other EWS error codes and SOAP faults; invalid SOAP; timeouts
and cancellation; TLS and DNS failures; invalid configuration and environment/argument overrides; and redirect handling.
A loopback socket server exercises the **real** `HttpClientHandler` to prove that redirects are not followed and that a
`Basic` or `Digest` challenge is never answered, so there is no insecure authentication fallback. Other tests assert that
logs and reports never contain bodies, tokens or the full folder identifier.

### Optional live integration tests (customer-approved test mailbox)

Skipped unless explicitly enabled. They perform the same read-only Inbox `GetFolder` as the probe. Run them as the intended
Windows identity, against a mailbox the customer has approved for testing:

```powershell
$env:EXCHANGE_LIVE_TESTS = "1"
$env:Exchange__EwsUrl    = "https://<your-exchange-host>/EWS/Exchange.asmx"
$env:Exchange__Mailbox   = "<customer-approved test mailbox>"
dotnet test --filter "FullyQualifiedName~LiveExchangeTests"
```

### Validating a deliberately unauthorized mailbox (negative test)

This proves the identity is **not** over-privileged. Run it **only against a mailbox that the mailbox owner and the
security team have approved for security testing**; failed access attempts can raise audit alerts. Never guess mailbox names.

1. Obtain written approval and have the Exchange administrator confirm the service identity has **no** permission on that
   mailbox (`Get-MailboxPermission` and `Get-MailboxFolderPermission` show no grant).
2. Run the probe against it:

   ```powershell
   $env:Exchange__Mailbox = "<mailbox-approved-for-security-testing>"
   dotnet run -- --probe
   ```

3. **Expected:** `Windows Authentication: SUCCESS`, `Mailbox Access: FAILED`, `EWS Response: ErrorAccessDenied`,
   `Category: MailboxAuthorization`, exit code 7.
4. If the result is `Overall: SUCCESS`, the identity can reach a mailbox it should not: report it to the Exchange
   administrator. If it is `MailboxResolution`, the address does not resolve; choose an existing mailbox.

The same check exists as a live test: set `EXCHANGE_LIVE_TESTS=1` and
`EXCHANGE_LIVE_TESTS_UNAUTHORIZED_MAILBOX=<mailbox-approved-for-security-testing>`.

## Design notes

- **Authentication:** `ExchangeHttpClientFactory` builds `new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false }`.
  No user names or passwords exist anywhere, nothing prompts for a login, and nothing falls back to another credential mechanism.
- **TLS:** certificate validation is never bypassed (no validation callback), and HTTPS is required twice: by configuration
  validation and again by the EWS client before it sends anything.
- **Redirects:** never followed; they are reported as `UnexpectedRedirect` with scheme, host and path only.
- **Read-only request:** the only operation is `GetFolder` for the `inbox` `DistinguishedFolderId` with a `Mailbox` child,
  asking for `IdOnly` plus `DisplayName`. A success needs HTTP 200, `ResponseClass="Success"` **and** `ResponseCode`
  `NoError`; an HTTP 200 alone is never success.
- **Diagnostics without leakage:** logs are structured and never contain request or response bodies, authentication
  headers, tokens or message content. Server-supplied text is sanitized (control characters removed, token-like runs
  masked, length capped). The Inbox identifier is shown only as a short fingerprint. For a 401 only the *names* of the
  offered authentication schemes are reported.
- **Impersonation later:** `ExchangeAccessMode` reserves `Impersonation` (currently rejected), `EwsRequestBuilder` is the
  single place SOAP headers are produced (where `ExchangeImpersonation` will go), and `IEwsClient` takes the mailbox per
  call so scope can be decided per mailbox.
- **Windows Service later:** `Program.cs` is a Generic Host composition root and `AddExchangeIntegration` registers the
  services. Running as a service will add the `Microsoft.Extensions.Hosting.WindowsServices` package, `AddWindowsService()`
  and `BackgroundService` workers. Configuration is already resolved relative to the application directory, which is what
  a service needs because its working directory is `System32`.

## Project layout

```text
ExchangeIntegration.slnx
global.json
ExchangeIntegration.csproj
Program.cs                      Host composition root and the --probe command
appsettings.json                Placeholder settings
Configuration/                  ExchangeOptions, startup validation, service registration
Authentication/                 ExchangeHttpClientFactory (Windows authentication), process identity
Exchange/                       IEwsClient, EwsClient, EwsRequestBuilder, EwsResponseParser, failure categories, redaction
Diagnostics/                    ExchangeConnectivityProbe, ConnectivityResult, console report
ExchangeIntegration.Tests/      xUnit tests (mocked HTTP transport, loopback server, opt-in live tests)
```

## Explicitly out of scope for this milestone

Email synchronization or polling; reading message bodies or attachments; Azure Blob Storage; moving or copying email;
sending email or replies; Microsoft Graph; IMAP or SMTP; OAuth; Service Bus; database persistence; Windows Service
installation or deployment automation; Exchange administration changes.

Deferred for now: running under a group Managed Service Account (gMSA). It is not documented or tested in this
milestone. The application does not select or restrict the identity (it uses the identity of the process), so this is a
scope decision for the documentation, not a limitation enforced by the code.

## Recommended next step

1. Run the probe under the intended identity on the intended host against the customer's Exchange, record the observed
   protocol (Kerberos or NTLM), and run the negative test with an approved mailbox. Only then mark live connectivity verified.
2. Next milestone: **read-only message retrieval** from approved mailboxes (find new items, read messages and
   attachments) with paging, throttling and back-off handling, and no storage yet.
