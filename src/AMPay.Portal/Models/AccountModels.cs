using System.ComponentModel.DataAnnotations;
using AMPay.Domain.Portal;

namespace AMPay.Portal.Models;

public class ConfirmIdModel
{
    [Required(ErrorMessage = "Enter your ID number.")]
    [Display(Name = "Your ID number")]
    [StringLength(20)]
    public string IdNumber { get; set; } = "";

    public int AttemptsLeft { get; set; }
}

public class AccountOverviewModel
{
    public required PortalClientStatement Statement { get; init; }
    public DateTime UpdatedUtc { get; init; }

    public IEnumerable<PortalLoanStatement> Active => Statement.Loans.Where(l => l.Stage == "Active");
    public IEnumerable<PortalLoanStatement> Pending => Statement.Loans.Where(l => l.Stage == "Pending");
    public IEnumerable<PortalLoanStatement> Closed => Statement.Loans.Where(l => l.Stage == "Closed");

    public decimal TotalArrears => Statement.Loans.Sum(l => l.ArrearsAmount);
    public decimal TotalRemaining => Statement.Loans.Sum(l => l.RemainingInstalments);

    public (DateTime Date, decimal Amount, string LoanNumber)? NextPayment =>
        Statement.Loans
            .Where(l => l.NextDueDate is not null && l.NextDueAmount is not null)
            .OrderBy(l => l.NextDueDate)
            .Select(l => ((DateTime, decimal, string)?)(l.NextDueDate!.Value, l.NextDueAmount!.Value, l.LoanNumber))
            .FirstOrDefault();
}

public class LoanDetailModel
{
    public required PortalClientStatement Statement { get; init; }
    public required PortalLoanStatement Loan { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

public class StatementModel
{
    public required PortalClientStatement Statement { get; init; }
    public required PortalLoanStatement Loan { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public DateTime UpdatedUtc { get; init; }

    public IEnumerable<PortalPayment> Payments =>
        Loan.Payments.Where(p => p.Date >= From && p.Date <= To).OrderBy(p => p.Date);

    public IEnumerable<PortalInstalment> Due =>
        Loan.Schedule.Where(e => e.DueDate >= From && e.DueDate <= To);

    /// <summary>Received before the period: what the opening balance has already had taken off.</summary>
    public decimal ReceivedBefore => Loan.Payments.Where(p => p.Status == "Received" && p.Date < From).Sum(p => p.Amount);
    public decimal ReceivedInPeriod => Payments.Where(p => p.Status == "Received").Sum(p => p.Amount);

    public decimal OpeningBalance => Loan.TotalRepayable - ReceivedBefore;
    public decimal ClosingBalance => Loan.TotalRepayable - ReceivedBefore - ReceivedInPeriod;
}

/// <summary>Formatting shared by the account pages, so every amount and date reads the same.</summary>
public static class Fmt
{
    public static string R(decimal v) => "R " + v.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-ZA"));
    public static string R(decimal? v) => v is null ? "-" : R(v.Value);
    public static string D(DateTime? v) => v is null ? "-" : v.Value.ToString("d MMM yyyy");

    /// <summary>A UTC moment as South African time.</summary>
    public static string Sast(DateTime? utc) => utc is null ? "-" : utc.Value.AddHours(2).ToString("d MMM yyyy HH:mm");

    public static string Pct(decimal rate) => (rate * 100m).ToString("0.##") + "%";

    public static string StatusClass(string status) => status switch
    {
        "Paid" or "Received" or "Active" or "Paid up" => "pill-done",
        "Overdue" or "Part paid - overdue" or "Unpaid" or "Not approved" or "Written off" or "Declined" => "pill-bad",
        "Due today" or "Part paid" or "Waiting for your approval" or "Pending at the bank" or "Being assessed"
            or "Approved - not yet paid out" or "Being set up" => "pill-warn",
        _ => "pill-todo"
    };
}
