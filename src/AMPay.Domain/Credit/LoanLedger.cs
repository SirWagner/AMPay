using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Domain.Credit;

/// <summary>
/// Turns a loan's collections into paid instalments. Recalculated from scratch on every change,
/// so a reversal or a late unpaid simply re-runs it - nothing is ever patched by hand.
/// </summary>
public static class LoanLedger
{
    /// <param name="Unapplied">Money received beyond everything the schedule asks for.</param>
    /// <param name="FullyPaid">Every instalment is paid in full.</param>
    public record Allocation(decimal Unapplied, bool FullyPaid);

    /// <summary>
    /// Applies every successful payment, in the order received, to the instalments oldest first.
    /// A debit raised for a particular instalment pays that one first. Then marks each failed
    /// debit as recovered when its instalment has since been cleared.
    /// </summary>
    public static Allocation Allocate(IEnumerable<LoanScheduleEntry> schedule, IEnumerable<LoanCollection> collections)
    {
        var entries = schedule.OrderBy(e => e.InstalmentNumber).ToList();
        var all = collections.ToList();

        foreach (var e in entries)
        {
            e.PaidAmount = 0m;
            e.PaidUtc = null;
        }

        var clearedBy = new Dictionary<Guid, LoanCollection>();
        var unapplied = 0m;

        foreach (var payment in all
                     .Where(c => c.Status == CollectionStatus.Successful)
                     .OrderBy(c => c.ReceivedUtc ?? c.CreatedUtc)
                     .ThenBy(c => c.CreatedUtc))
        {
            var left = payment.Amount;

            if (payment.ScheduleEntryId is { } targetId && entries.FirstOrDefault(e => e.Id == targetId) is { } target)
                left = Apply(target, left, payment, clearedBy);

            foreach (var e in entries)
            {
                if (left <= 0m) break;
                left = Apply(e, left, payment, clearedBy);
            }

            unapplied += left;
        }

        foreach (var failed in all.Where(c => c.Status == CollectionStatus.Failed))
        {
            failed.RecoveredByCollectionId =
                failed.ScheduleEntryId is { } id && clearedBy.TryGetValue(id, out var by) ? by.Id : null;
        }

        return new Allocation(unapplied, entries.Count > 0 && entries.All(e => e.PaidAmount >= e.InstalmentTotal));
    }

    /// <summary>What is still owed on the schedule.</summary>
    public static decimal Outstanding(IEnumerable<LoanScheduleEntry> schedule) =>
        schedule.Sum(e => Math.Max(0m, e.InstalmentTotal - e.PaidAmount));

    private static decimal Apply(LoanScheduleEntry e, decimal amount, LoanCollection payment, Dictionary<Guid, LoanCollection> clearedBy)
    {
        var due = e.InstalmentTotal - e.PaidAmount;
        if (due <= 0m || amount <= 0m) return amount;

        var take = Math.Min(due, amount);
        e.PaidAmount += take;

        if (e.PaidAmount >= e.InstalmentTotal)
        {
            e.PaidUtc = payment.ReceivedUtc ?? payment.CreatedUtc;
            clearedBy[e.Id] = payment;
        }

        return amount - take;
    }
}

/// <summary>Arrears buckets by days past the oldest unpaid instalment's due date.</summary>
public static class ArrearsBuckets
{
    public static readonly string[] Labels = { "1-30 days", "31-60 days", "61-90 days", "90+ days" };

    /// <summary>0..3 for a loan that is overdue; -1 when it is not.</summary>
    public static int For(int daysPastDue) => daysPastDue switch
    {
        <= 0 => -1,
        <= 30 => 0,
        <= 60 => 1,
        <= 90 => 2,
        _ => 3
    };
}

/// <summary>
/// Groups the bank unpaid codes Netcash passes back on a returned debit.
/// <para>
/// Only codes whose meaning is settled across the South African banks are mapped here; anything
/// else lands in <see cref="CollectionFailureGroup.Other"/> with the raw code kept on the
/// collection, so nothing is lost if this list grows. Check the list against Netcash's current
/// unpaid-code reference before go-live.
/// </para>
/// </summary>
public static class UnpaidCodes
{
    private static readonly Dictionary<string, CollectionFailureGroup> Groups = new()
    {
        ["02"] = CollectionFailureGroup.InsufficientFunds, // Not provided for
        ["22"] = CollectionFailureGroup.InsufficientFunds, // Account effects not cleared
        ["04"] = CollectionFailureGroup.Disputed,          // Payment stopped by the account holder
        ["30"] = CollectionFailureGroup.Disputed,          // No authority to debit
        ["32"] = CollectionFailureGroup.Disputed,          // Debit in contravention of the payer's authority
        ["36"] = CollectionFailureGroup.Disputed,          // Previously stopped via a stop-payment advice
        ["34"] = CollectionFailureGroup.MandateCancelled   // Authorisation cancelled
    };

    public static CollectionFailureGroup GroupOf(string? code) =>
        code is not null && Groups.TryGetValue(code.Trim().PadLeft(2, '0'), out var g) ? g : CollectionFailureGroup.Other;

    public static string Label(CollectionFailureGroup group) => group switch
    {
        CollectionFailureGroup.InsufficientFunds => "Insufficient funds",
        CollectionFailureGroup.Disputed => "Disputed or stopped",
        CollectionFailureGroup.MandateCancelled => "Mandate cancelled",
        _ => "Other"
    };
}
