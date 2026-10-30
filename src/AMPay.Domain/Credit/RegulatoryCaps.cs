namespace AMPay.Domain.Credit;

/// <summary>Which statutory ceiling a <see cref="Entities.RegulatoryCap"/> row sets.</summary>
public enum CapType
{
    /// <summary>Initiation fee: flat part (Reg 42). R165.</summary>
    InitiationFeeBase = 1,
    /// <summary>Initiation fee: the principal above which the marginal part applies. R1 000.</summary>
    InitiationFeeThreshold = 2,
    /// <summary>Initiation fee: share of the principal above the threshold. 10%.</summary>
    InitiationFeeMarginalRate = 3,
    /// <summary>Initiation fee: never more than this. R1 050.</summary>
    InitiationFeeMax = 4,
    /// <summary>Monthly service fee ceiling (Reg 44). R60.</summary>
    MonthlyServiceFeeMax = 5,
    /// <summary>Short-term credit: monthly interest on the client's first loan in a calendar year. 5%.</summary>
    ShortTermFirstLoanMonthlyRate = 6,
    /// <summary>Short-term credit: monthly interest on later loans in the same calendar year. 3%.</summary>
    ShortTermRepeatLoanMonthlyRate = 7,
    /// <summary>Short-term credit: largest principal. R8 000.</summary>
    ShortTermMaxPrincipal = 8,
    /// <summary>Short-term credit: longest term in months. 6.</summary>
    ShortTermMaxMonths = 9,
    /// <summary>Unsecured credit: annual interest ceiling (repo rate + 21%). No default - it moves with the repo rate.</summary>
    UnsecuredAnnualRateMax = 10,
    /// <summary>Credit life premium per month as a share of the balance: R4.50 per R1 000.</summary>
    CreditLifeMonthlyRateMax = 11,
    /// <summary>VAT rate. 15%.</summary>
    VatRate = 12
}

/// <summary>
/// The statutory ceilings in force on one date, all EXCLUDING VAT, as the regulations publish
/// them. Built from the effective-dated <see cref="Entities.RegulatoryCap"/> table, which the
/// AM-Pay platform admin keeps current; <see cref="Statutory"/> holds the defaults the table is
/// seeded with.
/// <para>
/// VERIFY BEFORE GO-LIVE. These are gazetted figures, reviewed and amended from time to time.
/// Have a compliance officer confirm them; nothing here is legal advice.
/// </para>
/// </summary>
public record RegulatoryCaps
{
    public decimal InitiationFeeBase { get; init; } = 165m;
    public decimal InitiationFeeThreshold { get; init; } = 1_000m;
    public decimal InitiationFeeMarginalRate { get; init; } = 0.10m;
    public decimal InitiationFeeMax { get; init; } = 1_050m;
    public decimal MonthlyServiceFeeMax { get; init; } = 60m;
    public decimal ShortTermFirstLoanMonthlyRate { get; init; } = 0.05m;
    public decimal ShortTermRepeatLoanMonthlyRate { get; init; } = 0.03m;
    public decimal ShortTermMaxPrincipal { get; init; } = 8_000m;
    public int ShortTermMaxMonths { get; init; } = 6;

    /// <summary>Null until the admin sets it: an unsecured loan cannot be priced without it.</summary>
    public decimal? UnsecuredAnnualRateMax { get; init; }

    public decimal CreditLifeMonthlyRateMax { get; init; } = 0.0045m;
    public decimal VatRate { get; init; } = 0.15m;

    /// <summary>The defaults seeded into the table.</summary>
    public static RegulatoryCaps Statutory { get; } = new();

    /// <summary>Initiation fee ceiling for a principal, excluding VAT.</summary>
    public decimal MaximumInitiationFee(decimal principal)
    {
        if (principal <= 0) return 0m;
        var fee = InitiationFeeBase + Math.Max(0m, principal - InitiationFeeThreshold) * InitiationFeeMarginalRate;
        return Math.Min(fee, InitiationFeeMax);
    }

    /// <summary>Short-term credit under the regulations: small and quick.</summary>
    public bool IsShortTerm(decimal principal, int months) =>
        principal <= ShortTermMaxPrincipal && months <= ShortTermMaxMonths;

    /// <summary>
    /// The monthly interest ceiling for a loan, or null when it cannot be known (an unsecured
    /// loan before the annual cap is set).
    /// </summary>
    public decimal? MonthlyInterestCeiling(decimal principal, int months, int priorShortTermLoansThisYear) =>
        IsShortTerm(principal, months)
            ? priorShortTermLoansThisYear > 0 ? ShortTermRepeatLoanMonthlyRate : ShortTermFirstLoanMonthlyRate
            : UnsecuredAnnualRateMax / 12m;

    /// <summary>The value for one cap type, for the admin screen.</summary>
    public decimal? Get(CapType type) => type switch
    {
        CapType.InitiationFeeBase => InitiationFeeBase,
        CapType.InitiationFeeThreshold => InitiationFeeThreshold,
        CapType.InitiationFeeMarginalRate => InitiationFeeMarginalRate,
        CapType.InitiationFeeMax => InitiationFeeMax,
        CapType.MonthlyServiceFeeMax => MonthlyServiceFeeMax,
        CapType.ShortTermFirstLoanMonthlyRate => ShortTermFirstLoanMonthlyRate,
        CapType.ShortTermRepeatLoanMonthlyRate => ShortTermRepeatLoanMonthlyRate,
        CapType.ShortTermMaxPrincipal => ShortTermMaxPrincipal,
        CapType.ShortTermMaxMonths => ShortTermMaxMonths,
        CapType.UnsecuredAnnualRateMax => UnsecuredAnnualRateMax,
        CapType.CreditLifeMonthlyRateMax => CreditLifeMonthlyRateMax,
        CapType.VatRate => VatRate,
        _ => null
    };

    /// <summary>A copy with one cap replaced.</summary>
    public RegulatoryCaps With(CapType type, decimal value) => type switch
    {
        CapType.InitiationFeeBase => this with { InitiationFeeBase = value },
        CapType.InitiationFeeThreshold => this with { InitiationFeeThreshold = value },
        CapType.InitiationFeeMarginalRate => this with { InitiationFeeMarginalRate = value },
        CapType.InitiationFeeMax => this with { InitiationFeeMax = value },
        CapType.MonthlyServiceFeeMax => this with { MonthlyServiceFeeMax = value },
        CapType.ShortTermFirstLoanMonthlyRate => this with { ShortTermFirstLoanMonthlyRate = value },
        CapType.ShortTermRepeatLoanMonthlyRate => this with { ShortTermRepeatLoanMonthlyRate = value },
        CapType.ShortTermMaxPrincipal => this with { ShortTermMaxPrincipal = value },
        CapType.ShortTermMaxMonths => this with { ShortTermMaxMonths = (int)value },
        CapType.UnsecuredAnnualRateMax => this with { UnsecuredAnnualRateMax = value },
        CapType.CreditLifeMonthlyRateMax => this with { CreditLifeMonthlyRateMax = value },
        CapType.VatRate => this with { VatRate = value },
        _ => this
    };

    public static string Label(CapType type) => type switch
    {
        CapType.InitiationFeeBase => "Initiation fee - flat part (R)",
        CapType.InitiationFeeThreshold => "Initiation fee - threshold (R)",
        CapType.InitiationFeeMarginalRate => "Initiation fee - share above threshold",
        CapType.InitiationFeeMax => "Initiation fee - maximum (R)",
        CapType.MonthlyServiceFeeMax => "Monthly service fee - maximum (R)",
        CapType.ShortTermFirstLoanMonthlyRate => "Short-term interest - first loan in the year, per month",
        CapType.ShortTermRepeatLoanMonthlyRate => "Short-term interest - later loans in the year, per month",
        CapType.ShortTermMaxPrincipal => "Short-term credit - largest principal (R)",
        CapType.ShortTermMaxMonths => "Short-term credit - longest term (months)",
        CapType.UnsecuredAnnualRateMax => "Unsecured credit interest - per year (repo + 21%)",
        CapType.CreditLifeMonthlyRateMax => "Credit life - per month, share of balance",
        CapType.VatRate => "VAT rate",
        _ => type.ToString()
    };

    /// <summary>True for caps entered as a fraction (0.05 = 5%), false for rands and months.</summary>
    public static bool IsRate(CapType type) => type is CapType.InitiationFeeMarginalRate
        or CapType.ShortTermFirstLoanMonthlyRate or CapType.ShortTermRepeatLoanMonthlyRate
        or CapType.UnsecuredAnnualRateMax or CapType.CreditLifeMonthlyRateMax or CapType.VatRate;
}
