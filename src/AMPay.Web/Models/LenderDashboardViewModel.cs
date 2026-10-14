namespace AMPay.Web.Models;

/// <summary>
/// Everything the lender dashboard shows, worked out once per tenant by
/// <see cref="Services.LenderDashboardService"/>. The view does no arithmetic beyond formatting.
/// <para>
/// Figures AM-Pay does not record yet (debit results, balances, arrears - see
/// docs/lender-dashboard-data-map.md) are absent rather than zero: the view shows
/// "Not tracked yet" for them, never a made-up R 0.
/// </para>
/// </summary>
public class LenderDashboardViewModel
{
    public Guid TenantId { get; init; }
    public string TenantName { get; init; } = "";
    public DateTime TodaySast { get; init; }
    public DateTime GeneratedUtc { get; init; }
    public string MonthLabel => TodaySast.ToString("MMMM yyyy");

    // Setup state, for empty-state guidance.
    public bool HasActivePackages { get; init; }
    public bool HasSelfServiceLink { get; init; }
    public bool HasAnyLoans { get; init; }

    public CollectionsSection Collections { get; init; } = new();
    public KpiSection Kpis { get; init; } = new();
    public IReadOnlyList<ActionCard> Actions { get; init; } = Array.Empty<ActionCard>();
    public int TotalActionItems => Actions.Where(a => a.Count.HasValue).Sum(a => a.Count!.Value);
    public IReadOnlyList<MonthBar> LentByMonth { get; init; } = Array.Empty<MonthBar>();
    public IReadOnlyList<CollectionRun> UpcomingRuns { get; init; } = Array.Empty<CollectionRun>();
    public GrowthSection Growth { get; init; } = new();
    public ComplianceSection Compliance { get; init; } = new();
    public IReadOnlyList<PipelineStage> Pipeline { get; init; } = Array.Empty<PipelineStage>();

    public class CollectionsSection
    {
        /// <summary>Instalments due this SAST month on loans being collected.</summary>
        public decimal Expected { get; init; }
        public int ExpectedDebits { get; init; }

        /// <summary>Instalments due from today to month-end.</summary>
        public decimal StillScheduled { get; init; }
        public int StillScheduledDebits { get; init; }

        /// <summary>Pay Now payments completed this month (all of the tenant's, not per loan).</summary>
        public decimal PayNowCollected { get; init; }
        public int PayNowCount { get; init; }

        public CollectionRun? NextRun { get; init; }
        public CollectionRun? LargestRun { get; init; }
    }

    public class KpiSection
    {
        public int ActiveLoans { get; init; }
        public int DisbursedThisMonth { get; init; }
        public decimal DisbursedPrincipalThisMonth { get; init; }
        public decimal AverageLoanThisMonth => DisbursedThisMonth == 0 ? 0 : DisbursedPrincipalThisMonth / DisbursedThisMonth;
        public int OnPaceFor { get; init; }
        public int DisbursedLastMonth { get; init; }

        public decimal InitiationFees { get; init; }
        public decimal Interest { get; init; }
        public decimal ServiceFees { get; init; }
        public decimal CreditLife { get; init; }
        public decimal Income => InitiationFees + Interest + ServiceFees + CreditLife;
    }

    /// <param name="Count">Null when the queue's data is not tracked yet.</param>
    public record ActionCard(
        string Key,
        string Title,
        int? Count,
        string Context,
        string LinkText,
        string? Url,
        bool Highlight = false);

    public record MonthBar(string Label, decimal Lent, int Loans, bool IsCurrentMonth);

    /// <param name="MandatesReady">Debits whose loan has an authenticated DebiCheck mandate.</param>
    public record CollectionRun(DateTime Date, int Debits, decimal Amount, int MandatesReady)
    {
        public bool AllMandatesReady => MandatesReady >= Debits;
    }

    public class GrowthSection
    {
        public int Approved { get; init; }
        public int Declined { get; init; }
        public decimal? ApprovalRate => Approved + Declined == 0 ? null : (decimal)Approved / (Approved + Declined);

        public int NewClientLoans { get; init; }
        public int RepeatClientLoans { get; init; }

        /// <summary>Self-service applications imported into AM-Pay this month.</summary>
        public int PortalImported { get; init; }
        /// <summary>Loans disbursed this month to clients who came through the self-service link.</summary>
        public int PortalDisbursed { get; init; }
    }

    public class ComplianceSection
    {
        public int ActiveLoans { get; init; }
        public int WithAffordability { get; init; }
        public int WithCreditCheck { get; init; }
        public int MissingDocuments { get; init; }
    }

    public record PipelineStage(string Label, int Count, string Status);
}
