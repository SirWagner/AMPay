# Lender dashboard: metric → data source

Status key: ✅ available now · ⚠️ derivable with a new query · ❌ needs new data

> **Update 15 Oct 2026:** M1-M6 and the indexes were approved and are built. Every ❌ below is
> now ✅ except "Open complaints / disputes" and the NCR quarterly return, which are still
> omitted. The tables are kept as the record of where each figure comes from; see
> [What was built](#what-was-built) at the end.

**The one gap that matters most:** AM-Pay records each loan's *repayment schedule*
(`LoanScheduleEntries`), Pay Now payments (`PayNowTransactions`, not linked to a loan) and Netcash
batch files (`NetcashBatches`, totals only). It records **nothing about each debit's outcome** -
no "instalment 3 of LN-000014 collected R 2,093.75 on 25 Oct" and no "debit failed: insufficient
funds". Everything that depends on money actually coming back (collected, failed, outstanding
balance, arrears, recovered) needs proposal **M1** below.

## 1. Hero: "{Month} collections"

| Metric | Source | Status |
|---|---|---|
| Expected this month | `LoanScheduleEntries.InstalmentTotal` where `DueDate` in the SAST month, loan `Status = Disbursed` (Settled/WrittenOff excluded) | ⚠️ |
| Collected (DebiCheck, manual/EFT) | no record of debit results or manual payments | ❌ M1 |
| Collected (Pay Now) | `PayNowTransactions` `Status = Complete`, `CompletedUtc` in month - but **not linked to a loan** | ✅ (tenant level only) |
| Failed (sum, count, not since recovered) | no debit results | ❌ M1 |
| Still scheduled | schedule entries due after today this month, on disbursed loans (the schedule *is* the plan; there is no separate instruction table) | ⚠️ |
| Next collection run (date, count, amount) | schedule grouped by `DueDate` ≥ today | ⚠️ |
| Largest upcoming run | same grouping, max amount | ⚠️ |
| Pay Now & manual this month | Pay Now ✅; manual ❌ M1 | ✅ / ❌ |
| Capital available to lend | no funding pool; also needs outstanding principal | ❌ M2 + M1 |

## 2. KPI row

| Metric | Source | Status |
|---|---|---|
| Loan book outstanding | total repayable − amounts received; amounts received are not recorded | ❌ M1 |
| Active loan count | `Loans.Status = Disbursed` | ✅ |
| Book in arrears (PAR) | needs which instalments are unpaid | ❌ M1 |
| Disbursed this month (count, principal, average) | `Loans.DisbursedUtc` (UTC → SAST month), `Principal` | ✅ |
| On pace for N vs last month | same, with SAST day counts | ✅ |
| Income this month - initiation fees | `Loans.InitiationFee` for loans disbursed in the month | ✅ |
| Income this month - interest, service fees, credit life | `LoanScheduleEntries.InterestPortion/ServiceFee/CreditLifePremium` due in the month (charged per the schedule, not confirmed collected) | ⚠️ |
| Month-on-month comparisons | no history | ❌ M4 (and only meaningful after M1) |

## 3. Needs action today

| Card | Source | Status |
|---|---|---|
| Failed collections | debit results | ❌ M1 |
| Mandates awaiting client authentication (+ oldest wait) | `Mandates.Status` in (`SubmittedToBank`, `AwaitingDebtorAuthentication`), `SubmittedUtc` | ✅ |
| Documents to review | `ClientDocuments.ReviewStatus = Pending` | ✅ |
| Loans awaiting approval (+ R requested) | `Loans.Status = PendingApproval`, `Principal` | ✅ |
| Agreements sent but not signed | `LoanContracts.Status` in (`Sent`, `Viewed`) | ✅ (new list page, no schema change) |
| Loans in cooling-off period | `Loans.DisbursedUtc` within N business days; N fixed at 5 until M3 | ⚠️ (+ ❌ M3 for the setting) |

## 4. Charts

| Chart | Source | Status |
|---|---|---|
| Money lent per month (6 months) | `Loans.DisbursedUtc`, `Principal` | ✅ |
| Money recovered per month | debit results + manual; Pay Now alone would understate it | ❌ M1 |
| Arrears ageing (1-30 / 31-60 / 61-90 / 90+) | oldest unpaid instalment | ❌ M1 |
| Written off this year | `LoanStatus.WrittenOff` exists, but no date or amount | ❌ M5 |

## 5. Collections detail

| Table | Source | Status |
|---|---|---|
| Upcoming runs (date, debits, amount) | schedule entries grouped by `DueDate`, rest of the month | ⚠️ |
| Mandates ready | `Loans.MandateId` → mandate `Status = Authenticated`, per run | ✅ |
| Why debits failed | debit results with Netcash unpaid codes | ❌ M1 |

## 6. Growth & compliance

| Metric | Source | Status |
|---|---|---|
| Approval rate | `Loans.DecidedUtc` in month; `Declined` vs approved (`Approved`/`Disbursed`/`Settled`/`WrittenOff`, or `Cancelled` after a decision) | ✅ |
| New vs repeat clients | loans disbursed this month; client's first disbursed loan | ✅ |
| Funnel: started → submitted | portal API (`/api/v1/applications`) returns submitted/picked-up only | ⚠️ (portal API change, no schema change) |
| Funnel: loan disbursed | client `OtherDetails.Source = "Self-service portal"` with a disbursed loan | ⚠️ |
| Funnel: link opened | not tracked | ❌ M6 |
| Affordability on file | `AffordabilityAssessments` per active loan | ✅ |
| Credit bureau check on file | `ClientCreditEnquiries` per client of an active loan (the Credit check tab is not built yet, so expect 0) | ✅ |
| Active loans missing required documents | ID + payslip not `Approved` for the client | ✅ |
| Open complaints / disputes | not held anywhere | omitted |
| NCR quarterly return | no export exists | omitted - listed as a gap |

## 7. Onboarding pipeline

Existing client status counts. ✅

## Roles

| Brief | AM-Pay role | Sees |
|---|---|---|
| Owner / Customer administrator | `TenantAdmin` (and `SuperAdmin` while working inside a customer) | everything |
| Loan officer | `Capturer` | action queues, upcoming runs, failed debits, pipeline - no income, capital, lent vs recovered |
| - | `Viewer` | same as loan officer (read-only) |
| - | `Reviewer` | documents queue and pipeline only (unchanged: their world is the review queue) |

A super admin with **no** customer selected keeps the platform overview (all customers).

## Schema changes - approved and built (migration `LoanCollectionsAndPortfolio`, portal `LinkEvents`)

**M1 - `LoanCollections`: one row per collection attempt or payment** (unlocks collected, failed,
outstanding, arrears, recovered, failure reasons, Pay Now/manual per loan)

```
LoanCollections
  Id, TenantId, LoanId, ScheduleEntryId (nullable)
  Channel        DebiCheck | PayNow | Manual
  ActionDate     date the money was due / attempted
  Amount         decimal(18,2)
  Status         Scheduled | Successful | Failed | Reversed
  FailureCode    Netcash unpaid/return code (nullable)
  FailureGroup   InsufficientFunds | Disputed | MandateCancelled | Other (nullable)
  RecoveredByCollectionId (nullable) - a later payment that cleared this failure
  ReceivedUtc, CreatedUtc, ExternalReference, NetcashBatchId (nullable), PayNowTransactionId (nullable)
  indexes (TenantId, ActionDate), (TenantId, Status, ActionDate), (LoanId, ActionDate)
```

Plus `LoanScheduleEntries.PaidAmount` (decimal, default 0) and `PaidUtc`, so arrears ("oldest
unpaid instalment") is one indexed query instead of a reconciliation every page load.
Writers: the Netcash collection-result import (stub today), Pay Now notify (when linked to a loan),
and a "Record manual payment" action on the loan page.

**M2 - `Tenants.LendingCapital`** (decimal, nullable) - "Capital available to lend" = capital −
outstanding principal. Needs M1 for outstanding principal.

**M3 - `Tenants.CoolingOffBusinessDays`** (int, default 5). Until then the card uses a fixed 5.

**M4 - `PortfolioSnapshots`** (TenantId, SnapshotDate, BookOutstanding, ActiveLoans, Arrears1to30,
31to60, 61to90, 90Plus, counts per bucket; unique (TenantId, SnapshotDate)) filled nightly by a new
`IHostedService` (the app has no job framework; a hosted service needs no new package or Azure
resource). Only meaningful once M1 exists.

**M5 - `Loans.WrittenOffUtc` and `WrittenOffAmount`** - for "Written off this year".

**M6 - portal `LinkEvents`** (portal database: LenderId, OccurredUtc, Kind = Opened) - the top of
the self-service funnel; aggregated counts exposed on the portal API.

**Indexes for the new queries** (in the same migration):
`Loans (TenantId, DisbursedUtc)`, `Loans (TenantId, DecidedUtc)`, `LoanScheduleEntries (DueDate)`
include `LoanId, InstalmentTotal`, `PayNowTransactions (TenantId, Status, CompletedUtc)`,
`LoanContracts (TenantId, Status)`. At today's data sizes the existing `(TenantId, Status)`
indexes keep every query fast; these matter at thousands of loans.

## What was built

**Writers of money in** (all through `LoanLedgerService`, which re-marks the loan's instalments
from its whole ledger after every change - oldest instalment first; a debit raised for one
instalment pays that one first; a fully paid loan is settled):

- *Netcash debit results* - `ImportDebitResultsAsync(tenantId, results)`, matched on the mandate's
  account reference and the action date. A later result for the same debit replaces the first.
  Lines that match no loan are returned, not guessed. Nothing calls it yet: it is the entry point
  for the live Netcash result fetch, which is still a stub (`Netcash:UseStubs` is unchanged).
- *Pay Now* - the notify callback applies a completed payment to the client's loan when they have
  exactly one being collected; otherwise it waits on the loan page under "Pay Now payments from
  this client".
- *Record a payment* (loan page, owners only) - cash or EFT, with the South African date received.
- *Write off* (loan page, owners only) - records the outstanding amount and a reason (as a client note).

**Unpaid codes** - `UnpaidCodes` maps only the bank codes whose meaning is settled (02, 22 →
insufficient funds; 04, 30, 32, 36 → disputed; 34 → mandate cancelled). Anything else is
"Other", with the raw code kept. **Check against Netcash's unpaid-code list before go-live.**

**Arrears** - an instalment is overdue when its due date has passed and it is not fully paid,
unless its debit is still with the bank (`Status = Scheduled`). Loans are bucketed by their
oldest overdue instalment; the bucket amount is the loan's whole balance. PAR = book in arrears ÷
book outstanding.

**Capital available** = `Tenants.LendingCapital` − principal still out (each unpaid instalment's
capital, scaled from the capitalised amount back to the cash paid out). Set on *Lending settings*.

**Snapshots** - written the first time the dashboard is opened each SAST day, when the database
is awake anyway. The nightly `PortfolioSnapshotService` exists but is off unless
`PortfolioSnapshots:Nightly=true`: on the free serverless Azure SQL database a nightly wake costs
roughly an hour of compute a day. Month-on-month compares with the first snapshot of the month.

**Portal funnel** - the portal records a `LinkEvent` per landing-page view (no IP, agent string or
cookie; link previews and crawlers are skipped) and serves `GET api/v1/funnel`. The dashboard
waits 3 seconds for it and otherwise shows the figures it has.

**Known limits** - loans paid out before this change have no payment history, so their past
instalments show as unpaid until payments are recorded or imported. Early settlement (paying
less than the full schedule because future interest falls away) is not modelled. A settled loan's
DebiCheck mandate is not cancelled automatically.
