# AM-Pay — DebiCheck Onboarding & Pay Now

Phase 1 of the AM-Pay platform: client onboarding, DebiCheck mandate origination and Pay Now
checkout, built as a Netcash ISV.

AM-Pay Fintech is the ISV. Customers (NCR-registered lenders and merchants) trade beneath it,
each with their own Netcash merchant account and service keys. Albatross Money (NCRCP9166) is
customer number one.

## How the app is separated

- **AM-Pay Platform** is for AM-Pay. The super admin sees every customer AM-Pay has onboarded,
  and can open any of them.
- **AM-Pay Loan Flow** is for the lender. A lender's staff sign in to Loan Flow and see only
  their own business - their borrowers, loans and settings - never another lender's.
- **Each customer sees the product they bought.** A customer on Loan Flow lands in Loan Flow;
  a customer on Payroll (to come) will land in Payroll. The super admin sees it all.
- **Borrowers** apply through their lender's self-service link (`src/AMPay.Portal`), also
  branded AM-Pay Loan Flow.

---

## Running it

```bash
dotnet build
dotnet run --project src/AMPay.Web
```

The database is created and seeded on first run (SQL Server LocalDB by default). Seeding
refuses to invent a password for the platform account, so set one first:

```bash
cd src/AMPay.Web
dotnet user-secrets set "Seed:SuperAdmin:Email" "you@albatrossmoney.com"
dotnet user-secrets set "Seed:SuperAdmin:Password" "<a strong password>"
```

Seeding creates: the four roles, the AM-Pay platform tenant, Albatross Money as a customer
with three service-key slots, and the super admin.

---

## Layout

```
src/
  AMPay.Domain/          entities, enums, Netcash contracts — no dependencies
  AMPay.Infrastructure/  EF Core, Identity, Netcash clients, NIF file builder
  AMPay.Web/             ASP.NET Core 8 MVC
```

`AMPay.Domain` defines the `INetcash*` interfaces. `AMPay.Infrastructure` implements them —
today with stubs, tomorrow with SOAP clients. Nothing above the interface changes when that
swap happens.

---

## Roles

| Role | Scope |
|---|---|
| `SuperAdmin` | Platform. No tenant of their own; selects which customer to work in. |
| `TenantAdmin` | One customer: its users, service keys and clients. |
| `Capturer` | Captures clients and originates mandates within one customer. |
| `Viewer` | Read-only within one customer. |

A user's `TenantId` is the boundary. Null means platform staff; anything else pins them to
that customer. `ICurrentTenant.EnsureCanAccess` is called on every action that loads a record
by id — without it, an id in the URL is a way across the boundary.

---

## Netcash integration

Everything is SOAP 1.2. There is no REST API.

| Endpoint | Used for |
|---|---|
| `NIWS_NIF.svc` | DebiCheck, debit orders, batch upload, eMandates |
| `NIWS_Validation.svc` | Bank list, branch/account/ID validation, AVS |
| `niws_partner.svc` | `ValidateServiceKey` — the ISV surface |
| `PayNow.svc` | Pay Now service operations |

### Service keys are never in the database

`TenantServiceKey` stores a **secret name**, not a key. The value is resolved at the moment of
use through `INetcashSecretStore` — user-secrets in development, Azure Key Vault in production.
A database dump therefore cannot leak a live payment credential, and no screen in the
application can display one.

```bash
dotnet user-secrets set "Netcash:ServiceKeys:<tenant-guid>:1" "<debit order service key>"
```

Service ids: `1` debit orders, `2` creditor, `3` risk reports, `5` account, `10` salary,
`14` Pay Now.

### The two DebiCheck routes

They are not a flag on the same flow.

**TT1 (real time)** calls `DebiCheckAuthenticate`. The debtor's bank answers inside the
request, so the mandate lands on `Authenticated` or `Rejected` before the operator leaves the
screen. Needs a mandate template configured for real-time use — a batch template returns
error `325`. Netcash requires a client timeout of at least three minutes.

**TT2 (batch)** requires two uploads in order:

1. `BatchFileUpload` with instruction `Update` — creates the debit order masterfile entry.
   Netcash rejects a DebiCheck authentication for an account it does not already hold.
2. `BatchFileUpload` with instruction `DebiCheckAuthentication`.

That returns a **file token, which is not an approval**. The bank's answer arrives later on
the NetConnector postback, so the mandate sits in `SubmittedToBank` and collections must not
be attempted until `/webhooks/netcash/debicheck` moves it on.

### Batch files

`NifFileBuilder` owns the NIF format: tab-delimited `H`/`K`/`T`/`F` records, field ids in
ascending order, amounts in cents, dates `CCYYMMDD`. The footer total is computed from the
rows actually added rather than passed in, because a footer that disagrees with the body
produces a load report full of line errors rather than a clean failure.

`LoadReportParser` reads the result. Note that `SUCCESSFUL WITH ERRORS` is a real outcome —
only an unqualified `SUCCESSFUL` with no messages means every row was accepted.

---

## Onboarding

Thirteen steps, in the Maxmoney tab order, but as one wizard over a single aggregate rather
than thirteen independent forms — a part-finished capture survives the operator closing the
browser.

Steps 1–7 (General, Employment, Financial, Banking, Payback, Address, Other details) are
built and carry everything Netcash needs to authenticate a collection. Budgets (step 9) is
captured on the Financial step, beside the income it is set against: the Maxmoney budget
lines, an "Add expense" line, and the NET of NET — income less the greater of the budget and
the Regulation 23A minimum, less debt instalments. The rest of steps 8–13 are not built, and
none of them block a mandate:

| Step | What is missing |
|---|---|
| References, Notes | Plain CRUD over entities that already exist. |
| Credit check | A decision: Compuscan/Experian directly, or Netcash risk reports (service id 3). See `ICreditBureauClient`. |
| Documents, Photograph | Azure Blob Storage — private container, downloads through an authorised action, never a public URL. |

---

## Running without Netcash credentials

You do not need any Netcash credentials to run, develop against, or demo this application.

`Netcash:UseStubs` (default `true`) routes every call to the stub clients. A yellow banner
sits across the top of every screen so a stubbed response can never be mistaken for a real
one, and each stub logs at `Warning`.

When you do have credentials, they arrive **per customer, per service** — not all at once.
`INetcashCapabilityService` is the single place that decides whether a given service is usable
for a given customer, and it is what every call site asks:

| State | Usable | Meaning |
|---|---|---|
| `Stubbed` | yes | Stubs are on. Completes locally, nothing reaches Netcash. |
| `Ready` | yes | Key loaded and validated with Netcash inside 24 hours. |
| `ReadyUnvalidated` | yes | Key loaded, but not validated recently. Work continues; the operator is told. |
| `NoSlot` | no | No key slot for this service on this customer. |
| `SecretMissing` | no | Slot exists, no value in the secret store. |
| `Inactive` | no | Slot deactivated. |
| `Rejected` | no | Netcash rejected this key at last validation. |
| `NoMerchantAccount` | no | Customer has no Netcash account number. |

Two rules matter more than the rest:

**It never returns a placeholder key when stubs are off.** An earlier version fell back to
`"STUB-SERVICE-KEY"` when a secret was missing. That string would have been sent to Netcash as
a service key, drawing error 100 — and three of those inside ten minutes locks the merchant
account. A missing key would have surfaced as a *locked Netcash account* rather than as a
missing key. It now refuses, and says which secret name is empty.

**A key Netcash already rejected is not retried**, for the same reason: retrying spends one of
the three attempts before lockout for no possible gain.

Operators see this before they start work, not after they submit a form. `Mandates/Create` and
`Checkout` show a banner naming the missing piece and disable the submit button; the Netcash
service keys screen carries a "What works right now" panel per customer. Partial configuration
works exactly as you would hope — a customer with a debit order key but no Pay Now key can
originate mandates and cannot create payment links.

Enabling live calls is a separate switch: setting `UseStubs` to `false` **throws at startup**
unless live clients are registered, rather than silently falling back to fake payment responses.

## Tests

```bash
dotnet test
```

32 tests, no Netcash connection required. They cover the two things that can be verified
without one: the capability rules above (including every refusal), and the NIF batch format —
key-record ordering, row padding, footer totals, rands-to-cents rounding, and the
`SUCCESSFUL WITH ERRORS` load-report case that reads like success but is a partial failure.

## Deploying to Azure

One command, from the repository root, after `az login`:

```powershell
.\deploy\deploy-azure.ps1
```

It runs the tests, publishes, refuses to ship local data (App_Data, database files, `folder/`,
user-secrets), zips for Linux, deploys, and waits for `/healthz` to report the app and its
database up. Migrations apply themselves at startup. `-SkipTests` skips the tests;
`-AppName` / `-ResourceGroup` (or `AMPAY_AZ_APP` / `AMPAY_AZ_RG`) target another environment.

Staging runs in South Africa North: Linux App Service B1, Azure SQL (free offer, signs in with
the app's managed identity - no SQL passwords), Key Vault for secrets. A monthly budget emails
at 80% and 100% and, at 100%, a runbook stops the app and drops its plan to the free tier.

## The gaps, in one place

Search the source for `GAP` — each one names the operation to call and the fields it needs.

1. **Live Netcash clients.** `Netcash:UseStubs` is `true`. Generate the SOAP clients, implement
   each `INetcash*` interface, register them in `AddAMPayInfrastructure`. Startup throws
   rather than falling back to fake payment responses if stubs are disabled with nothing behind
   them.
   ```bash
   dotnet tool install --global dotnet-svcutil
   dotnet-svcutil https://ws.netcash.co.za/NIWS/NIWS_NIF.svc?wsdl -n "*,AMPay.Infrastructure.Netcash.Nif"
   ```
2. **Azure Key Vault** as a configuration source in `Program.cs`.
3. **The webhook endpoint is unauthenticated.** Anyone who can reach
   `/webhooks/netcash/debicheck` can currently mark a mandate authenticated. Allowlist the
   Netcash source IPs and put a shared secret in the configured postback path before go-live.
4. **Pay Now callback verification.** The `notify` callback is authoritative; `accept` and
   `decline` are browser redirects and can be forged. Verify the payload really came from
   Netcash and reconcile against the Netcash statement before releasing anything of value.
   The amount-mismatch guard is in place; the origin check is not.
5. **Pay Now form field contract** (`m1`, `p2`, `p4`…) follows the documentation but has not
   been confirmed against a live account.
6. **`RequestActionDate`** — the stub skips weekends only. The live call also applies South
   African public holidays and per-service cut-offs.
7. **Credit bureau**, **document storage**, **photo capture** — as above.

---

## Things that will bite you

**EF and `Guid.NewGuid()` keys.** Every entity initialises its own key. When a new child is
attached through a *tracked parent's navigation property*, EF sees a key that is already set,
concludes the row exists, and emits an `UPDATE` — which affects zero rows and throws
`DbUpdateConcurrencyException`. Always add new entities through the `DbSet`
(`_db.ClientBankAccounts.Add(...)`), never through `client.BankAccounts.Add(...)`. See
`ClientsController.GetOrCreate`.

**Razor's email heuristic.** `R@Model.Amount` is parsed as an email address and rendered
literally. Write `R @Model.Amount` or `R@(Model.Amount)`.

**Service key validation locks accounts.** Three failures inside ten minutes locks the Netcash
merchant account, and only a successful call clears it. Validation is a button, never a timer
or a page-load side effect. Netcash still expects revalidation at least every 24 hours.

**Tenant selection is a cookie, not session.** In-memory session dies with the process, so on
Azure every restart — and every request landing on a second instance — would silently drop a
platform operator back to "no customer selected" mid-capture.

**PCI scope.** The customer is redirected to Netcash's hosted page and card data never reaches
this application. That is what keeps AM-Pay inside SAQ A rather than SAQ D. Never proxy the
card form, however convenient it looks.

---

## Verified working

Signed in, selected a customer, captured a client, added a bank account, completed the
mandate-critical steps, activated, and originated both mandate types against the stubs:

- **TT1** → `Authenticated` in-session, error `000`, bank `900000`, Bankserv `ACCP`.
- **TT2** → `SubmittedToBank` with a file token, then `Authenticated` after posting a
  `DEBICHECKRESULT` to the webhook.
- **Pay Now** → payment link created, hosted-checkout hand-off page rendered.

SA ID numbers are Luhn-checked locally before Netcash sees them, and date of birth and gender
are derived from the number rather than retyped.
