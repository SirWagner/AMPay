using System.Security.Cryptography;
using System.Text;
using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Domain.Portal;

// ------------------------------------------------------------------ AM-Pay -> portal: the client's account

/// <summary>
/// Everything a client may see about their own account with one lender: every loan, every
/// cost, every instalment and every payment, failed ones included. Built in AM-Pay and pushed
/// to the portal, which only displays it - the portal never reaches into AM-Pay.
/// </summary>
public record PortalClientStatement(
    Guid TenantId,
    Guid AmpayClientId,
    string ClientNumber,
    string FullName,
    /// <summary>27XXXXXXXXX: the number the client signs in with.</summary>
    string Mobile,
    /// <summary>See <see cref="ClientStatementBuilder.IdNumberHash"/>. The ID number itself never leaves AM-Pay.</summary>
    string IdNumberHash,
    DateTime GeneratedUtc,
    IReadOnlyList<PortalLoanStatement> Loans);

public record PortalLoanStatement(
    string LoanNumber,
    string Status,
    /// <summary>Active, Closed or Pending: how the portal groups and colours the loan.</summary>
    string Stage,
    string? ProductType,
    string? PackageName,
    // What was borrowed and what it costs - the NCA cost elements, all of them
    decimal Principal,
    decimal InitiationFee,
    decimal CapitalisedAmount,
    decimal MonthlyInterestRate,
    decimal MonthlyServiceFee,
    decimal CreditLifeRate,
    decimal VatRate,
    decimal TotalInterest,
    decimal TotalServiceFees,
    decimal TotalCreditLife,
    decimal TotalVat,
    decimal FuneralCover,
    decimal ThirdPartyPayment,
    decimal TotalCostOfCredit,
    decimal TotalRepayable,
    decimal CreditCostMultiple,
    int NumberOfInstalments,
    string Frequency,
    decimal FirstInstalment,
    decimal BaseInstalment,
    DateTime? AgreementDate,
    DateTime? DisbursedUtc,
    DateTime? FirstCollectionDate,
    // Where the account stands today
    decimal PaidToDate,
    decimal RemainingInstalments,
    decimal ArrearsAmount,
    int DaysPastDue,
    int InstalmentsPaid,
    DateTime? NextDueDate,
    decimal? NextDueAmount,
    DateTime? SettledOrClosedUtc,
    decimal? WrittenOffAmount,
    PortalMandateSummary? DebitOrder,
    IReadOnlyList<PortalInstalment> Schedule,
    IReadOnlyList<PortalPayment> Payments);

public record PortalInstalment(
    int Number,
    DateTime DueDate,
    decimal OpeningBalance,
    decimal Interest,
    decimal Capital,
    decimal ServiceFee,
    decimal CreditLife,
    decimal Vat,
    decimal Total,
    decimal Paid,
    DateTime? PaidUtc,
    decimal ClosingBalance,
    /// <summary>Paid, Part paid, Overdue, Due today, Upcoming or Written off.</summary>
    string Status);

public record PortalPayment(
    DateTime Date,
    string Method,
    decimal Amount,
    /// <summary>Received, Unpaid, Pending at the bank or Reversed.</summary>
    string Status,
    /// <summary>Why a debit came back unpaid, in words, with the bank's code.</summary>
    string? Reason,
    string? Reference,
    /// <summary>For an unpaid debit: the instalment has since been paid another way.</summary>
    bool RecoveredLater,
    int? InstalmentNumber);

public record PortalMandateSummary(
    string Status,
    string? StatusHelp,
    decimal Amount,
    decimal? FirstAmount,
    DateTime? FirstDate,
    string Frequency,
    string? CollectionDay,
    string? Bank,
    string? MaskedAccount,
    string? MandateReference,
    DateTime? ApprovedUtc);

/// <summary>Turns AM-Pay's records into the client's statement. Pure: no database, no clock of its own.</summary>
public static class ClientStatementBuilder
{
    /// <summary>
    /// The ID number as the portal may hold it: SHA-256 of tenant and ID digits. The portal
    /// compares the client's typed ID against it to tie the signed-in cell number to the
    /// account - the ID itself is never sent.
    /// </summary>
    public static string IdNumberHash(Guid tenantId, string? idNumber)
    {
        var digits = new string((idNumber ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}:{digits}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>The client's cell number, normalised, from whichever address holds one (physical first).</summary>
    public static string? MobileOf(Client client) =>
        client.Addresses
            .OrderBy(a => a.AddressType == AddressType.Physical ? 0 : 1)
            .Select(a => PortalApi.NormaliseMobile(a.MobileNumber))
            .FirstOrDefault(m => m is not null);

    /// <summary>Loans a client is shown. Drafts are the lender's workings, not yet an offer.</summary>
    public static bool IsShown(Loan loan) => loan.Status != LoanStatus.Draft;

    /// <param name="today">Today's South African date.</param>
    public static PortalClientStatement Build(Client client, IEnumerable<Loan> loans, DateTime today, DateTime nowUtc)
    {
        var mobile = MobileOf(client) ?? "";
        var name = string.Join(" ", new[] { client.FirstName, client.Surname }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new PortalClientStatement(
            client.TenantId, client.Id, client.ClientNumber, name, mobile,
            IdNumberHash(client.TenantId, client.IdNumber), nowUtc,
            loans.Where(IsShown)
                .OrderByDescending(l => l.AgreementDate ?? l.CreatedUtc)
                .Select(l => Loan(l, today))
                .ToList());
    }

    public static PortalLoanStatement Loan(Loan l, DateTime today)
    {
        today = today.Date;
        var schedule = l.Schedule.OrderBy(e => e.InstalmentNumber).ToList();
        var collections = l.Collections.ToList();
        var collecting = l.Status == LoanStatus.Disbursed;

        var overdue = collecting
            ? schedule.Where(e => e.DueDate.Date < today && e.PaidAmount < e.InstalmentTotal).ToList()
            : new List<LoanScheduleEntry>();
        var next = collecting
            ? schedule.FirstOrDefault(e => e.DueDate.Date >= today && e.PaidAmount < e.InstalmentTotal)
            : null;

        var instalmentByEntry = schedule.ToDictionary(e => e.Id, e => e.InstalmentNumber);

        return new PortalLoanStatement(
            l.LoanNumber,
            StatusLabel(l.Status),
            StageOf(l.Status),
            l.ProductType,
            l.CreditPackage?.Name,
            l.Principal, l.InitiationFee, l.CapitalisedAmount,
            l.MonthlyInterestRate, l.MonthlyServiceFee, l.CreditLifeRate, l.VatRate,
            l.TotalInterest, l.TotalServiceFees, l.TotalCreditLife, l.TotalVat,
            l.FuneralCover, l.ThirdPartyPayment,
            l.TotalCostOfCredit, l.TotalRepayable, l.CreditCostMultiple,
            l.NumberOfInstalments, FrequencyLabel(l.Frequency), l.FirstInstalment, l.BaseInstalment,
            l.AgreementDate, l.DisbursedUtc, l.FirstCollectionDate,
            PaidToDate: collections.Where(c => c.Status == CollectionStatus.Successful).Sum(c => c.Amount),
            RemainingInstalments: collecting ? LoanLedger.Outstanding(schedule) : 0m,
            ArrearsAmount: overdue.Sum(e => e.InstalmentTotal - e.PaidAmount),
            DaysPastDue: overdue.Count == 0 ? 0 : (today - overdue.Min(e => e.DueDate.Date)).Days,
            InstalmentsPaid: schedule.Count(e => e.InstalmentTotal > 0 && e.PaidAmount >= e.InstalmentTotal),
            NextDueDate: next?.DueDate.Date,
            NextDueAmount: next is null ? null : next.InstalmentTotal - next.PaidAmount,
            SettledOrClosedUtc: l.Status == LoanStatus.WrittenOff ? l.WrittenOffUtc
                : l.Status == LoanStatus.Settled ? schedule.Max(e => e.PaidUtc) : null,
            WrittenOffAmount: l.WrittenOffAmount,
            DebitOrder: l.Mandate is null ? null : Mandate(l.Mandate),
            Schedule: schedule.Select(e => Instalment(e, l.Status, today)).ToList(),
            Payments: collections
                .OrderByDescending(c => c.ReceivedUtc ?? c.ActionDate)
                .ThenByDescending(c => c.CreatedUtc)
                .Select(c => Payment(c, instalmentByEntry))
                .ToList());
    }

    private static PortalInstalment Instalment(LoanScheduleEntry e, LoanStatus loanStatus, DateTime today)
    {
        var status =
            e.PaidAmount >= e.InstalmentTotal ? "Paid"
            : loanStatus == LoanStatus.WrittenOff ? "Written off"
            : loanStatus is LoanStatus.Cancelled or LoanStatus.Declined ? "Not due"
            : e.PaidAmount > 0m ? (e.DueDate.Date < today ? "Part paid - overdue" : "Part paid")
            : loanStatus != LoanStatus.Disbursed ? "Not yet due"
            : e.DueDate.Date < today ? "Overdue"
            : e.DueDate.Date == today ? "Due today"
            : "Upcoming";

        return new PortalInstalment(e.InstalmentNumber, e.DueDate.Date, e.OpeningBalance, e.InterestPortion, e.CapitalPortion,
            e.ServiceFee, e.CreditLifePremium, e.VatPortion, e.InstalmentTotal, e.PaidAmount, e.PaidUtc, e.ClosingBalance, status);
    }

    private static PortalPayment Payment(LoanCollection c, IReadOnlyDictionary<Guid, int> instalments) => new(
        Date: c.ReceivedUtc is { } r ? Reporting.Sast.DateOf(r) : c.ActionDate.Date,
        Method: c.Channel switch
        {
            CollectionChannel.DebiCheck => "Debit order (DebiCheck)",
            CollectionChannel.PayNow => "Online payment (Pay Now)",
            _ => "Payment received"
        },
        Amount: c.Amount,
        Status: c.Status switch
        {
            CollectionStatus.Successful => "Received",
            CollectionStatus.Failed => "Unpaid",
            CollectionStatus.Reversed => "Reversed",
            _ => "Pending at the bank"
        },
        Reason: c.Status == CollectionStatus.Failed
            ? UnpaidCodes.Label(c.FailureGroup ?? UnpaidCodes.GroupOf(c.FailureCode)) + (c.FailureCode is null ? "" : $" (bank code {c.FailureCode})")
            : c.Status == CollectionStatus.Reversed ? "The payment was taken back after it was recorded" : null,
        Reference: c.ExternalReference,
        RecoveredLater: c.Status == CollectionStatus.Failed && c.RecoveredByCollectionId is not null,
        InstalmentNumber: c.ScheduleEntryId is { } id && instalments.TryGetValue(id, out var n) ? n : null);

    private static PortalMandateSummary Mandate(DebiCheckMandate m)
    {
        var (status, help) = m.Status switch
        {
            MandateStatus.Authenticated => ("Active", "You approved this debit order with your bank. Your instalments are collected from it."),
            MandateStatus.AwaitingDebtorAuthentication => ("Waiting for your approval",
                "Your bank has sent you a request to approve this debit order - check your banking app or the SMS from your bank."),
            MandateStatus.Rejected => ("Not approved", "The debit order was not approved at your bank. Contact the lender to arrange payment."),
            MandateStatus.Cancelled => ("Cancelled", null),
            MandateStatus.Expired => ("Expired", "The approval request expired before it was approved."),
            MandateStatus.Failed => ("Could not be set up", "Contact the lender to arrange payment."),
            MandateStatus.Amended => ("Amended", null),
            _ => ("Being set up", "The lender is registering this debit order with your bank.")
        };

        return new PortalMandateSummary(status, help, m.CollectionAmount,
            m.FirstCollectionDiffers ? m.FirstCollectionAmount : null, m.FirstCollectionDate,
            FrequencyLabel(m.Frequency), m.CollectionDayCode,
            m.BankAccount?.BankName, m.BankAccount?.MaskedAccountNumber, m.MandateReference, m.AuthenticatedUtc);
    }

    public static string StatusLabel(LoanStatus s) => s switch
    {
        LoanStatus.PendingApproval => "Being assessed",
        LoanStatus.Approved => "Approved - not yet paid out",
        LoanStatus.Declined => "Declined",
        LoanStatus.Disbursed => "Active",
        LoanStatus.Settled => "Paid up",
        LoanStatus.Cancelled => "Cancelled",
        LoanStatus.WrittenOff => "Written off",
        _ => "Being prepared"
    };

    public static string StageOf(LoanStatus s) => s switch
    {
        LoanStatus.Disbursed => "Active",
        LoanStatus.PendingApproval or LoanStatus.Approved => "Pending",
        _ => "Closed"
    };

    public static string FrequencyLabel(DebitFrequency f) => f switch
    {
        DebitFrequency.Weekly => "Weekly",
        DebitFrequency.Biweekly => "Every two weeks",
        DebitFrequency.Bimonthly => "Every two months",
        DebitFrequency.ThreeMonthly => "Every three months",
        DebitFrequency.SixMonthly => "Every six months",
        DebitFrequency.Annually => "Yearly",
        _ => "Monthly"
    };
}
