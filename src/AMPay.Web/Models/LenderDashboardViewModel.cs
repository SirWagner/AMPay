using AMPay.Domain.Portal;
using AMPay.Web.Services;

namespace AMPay.Web.Models;

/// <summary>
/// Everything the lender dashboard shows, worked out once per tenant by
/// <see cref="Services.LenderDashboardService"/>. The view does no arithmetic beyond formatting.
/// <para>
/// A figure AM-Pay cannot know is null rather than zero - the portal funnel when the portal
/// cannot be reached, capital before the lender has set it, a comparison before there is
/// history - and the view says so instead of showing a made-up R 0.
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

    public int CoolingOffBusinessDays { get; init; } = 5;

    public CollectionsSection Collections { get; init; } = new();
    public KpiSection Kpis { get; init; } = new();
    public IReadOnlyList<ActionCard> Actions { get; init; } = Array.Empty<ActionCard>();
    public int TotalActionItems => Actions.Where(a => a.Count.HasValue).Sum(a => a.Count!.Value);
    public IReadOnlyList<MonthBar> LentByMonth { get; init; } = Array.Empty<MonthBar>();
    public IReadOnlyList<ArrearsBucket> Arrears { get; init; } = Array.Empty<ArrearsBucket>();
    public WriteOffs WrittenOffThisYear { get; init; } = new(0, 0m);
    public IReadOnlyList<CollectionRun> UpcomingRuns { get; init; } = Array.Empty<CollectionRun>();
    public IReadOnlyList<FailureReason> FailureReasons { get; init; } = Array.Empty<FailureReason>();
    public GrowthSection Growth { get; init; } = new();
    public ComplianceSection Compliance { get; init; } = new();
    public IReadOnlyList<PipelineStage> Pipeline { get; init; } = Array.Empty<PipelineStage>();

    public class CollectionsSection
    {
        /// <summary>Instalments due this SAST month on loans being collected.</summary>
        public decimal Expected { get; init; }
        public int ExpectedDebits { get; init; }

        /// <summary>Money received for loans this month, by channel.</summary>
        public decimal CollectedDebiCheck { get; init; }
        public decimal CollectedPayNow { get; init; }
        public decimal CollectedManual { get; init; }
        public int CollectedCount { get; init; }
        public decimal Collected => CollectedDebiCheck + CollectedPayNow + CollectedManual;

        /// <summary>Debits that ran this month, failed, and have not been made good since.</summary>
        public decimal Failed { get; init; }
        public int FailedDebits { get; init; }

        /// <summary>Instalments due from today to month-end.</summary>
        public decimal StillScheduled { get; init; }
        public int StillScheduledDebits { get; init; }

        /// <summary>Every Pay Now checkout completed this month, whether or not it was for a loan.</summary>
        public decimal PayNowCollected { get; init; }
        public int PayNowCount { get; init; }

        public CollectionRun? NextRun { get; init; }
        public CollectionRun? LargestRun { get; init; }

        /// <summary>What the lender set aside for lending. Null until set.</summary>
        public decimal? LendingCapital { get; init; }

        /// <summary>The cash still out on loan (principal not yet repaid).</summary>
        public decimal OutstandingPrincipal { get; init; }

        public decimal? CapitalAvailable => LendingCapital is { } c ? c - OutstandingPrincipal : null;
    }

    public class KpiSection
    {
        public int ActiveLoans { get; init; }

        public decimal BookOutstanding { get; init; }
        public decimal BookInArrears { get; init; }
        public int LoansInArrears { get; init; }
        public decimal? ParRatio => BookOutstanding == 0m ? null : BookInArrears / BookOutstanding;

        /// <summary>The same figures at the start of this month, from the daily snapshot. Null without one.</summary>
        public DateTime? ComparedToDate { get; init; }
        public decimal? BookOutstandingThen { get; init; }
        public decimal? ParRatioThen { get; init; }

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

    /// <param name="Count">Null when the queue's data is not tracked.</param>
    public record ActionCard(
        string Key,
        string Title,
        int? Count,
        string Context,
        string LinkText,
        string? Url,
        bool Highlight = false);

    /// <param name="Recovered">Money received for loans in the month, every channel.</param>
    public record MonthBar(string Label, decimal Lent, int Loans, decimal Recovered, bool IsCurrentMonth);

    public record WriteOffs(int Loans, decimal Amount);

    /// <param name="Unrecovered">Of <paramref name="Count"/>, how many are still missing.</param>
    public record FailureReason(string Label, int Count, decimal Amount, int Unrecovered);

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

        /// <summary>From the portal this month. Null when there is no link or the portal could not be reached.</summary>
        public PortalFunnel? Funnel { get; init; }

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
