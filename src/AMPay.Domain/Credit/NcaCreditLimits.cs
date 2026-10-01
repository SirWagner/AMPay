namespace AMPay.Domain.Credit;

/// <summary>
/// The statutory ceilings from the National Credit Act regulations.
/// <para>
/// VERIFY BEFORE GO-LIVE. These are gazetted figures that are reviewed and amended. They
/// are expressed as configuration, not constants, precisely so that a rate change is a
/// settings edit rather than a deployment - but the defaults below still need checking
/// against the current Regulations by someone who can read the gazette. Nothing in this
/// file should be treated as legal advice.
/// </para>
/// <para>
/// All money here is VAT inclusive, matching how AM-Pay quotes. The gazetted caps are
/// published exclusive of VAT, so the defaults carry VAT already added.
/// </para>
/// </summary>
public class NcaCreditLimits
{
    public const string SectionName = "Nca";

    /// <summary>VAT rate used to gross up the gazetted exclusive caps.</summary>
    public decimal VatRate { get; set; } = 0.15m;

    // ---- Initiation fee (Reg 42) ----
    // Gazetted: R165 plus 10% of the amount above R1 000, never more than R1 050 - all
    // exclusive of VAT. Grossed up at 15% below.

    /// <summary>Flat component of the initiation fee cap, VAT inclusive. R165 x 1.15.</summary>
    public decimal InitiationFeeBase { get; set; } = 189.75m;

    /// <summary>The principal below which only the flat component applies.</summary>
    public decimal InitiationFeeThreshold { get; set; } = 1_000m;

    /// <summary>Marginal rate applied to principal above the threshold.</summary>
    public decimal InitiationFeeMarginalRate { get; set; } = 0.10m;

    /// <summary>Absolute ceiling on the initiation fee, VAT inclusive. R1 050 x 1.15.</summary>
    public decimal InitiationFeeCeiling { get; set; } = 1_207.50m;

    // ---- Service fee (Reg 44) ----

    /// <summary>Monthly service fee ceiling, VAT inclusive. Gazetted at R60 excl.</summary>
    public decimal MonthlyServiceFeeCeiling { get; set; } = 69m;

    // ---- Credit life insurance (Reg 3(1)(a), 2017 limitation regulations) ----

    /// <summary>
    /// Maximum credit life premium per month as a fraction of the deferred amount:
    /// R4.50 per R1 000 outstanding.
    /// </summary>
    public decimal CreditLifeCeilingRate { get; set; } = 0.0045m;

    // ---- Interest (Reg 42, short term credit) ----

    /// <summary>Maximum interest per month on short-term credit: 5%.</summary>
    public decimal MonthlyInterestCeiling { get; set; } = 0.05m;

    /// <summary>
    /// The statutory initiation fee maximum for a given principal.
    /// </summary>
    public decimal MaximumInitiationFee(decimal principal)
    {
        if (principal <= 0) return 0m;

        var fee = InitiationFeeBase;

        if (principal > InitiationFeeThreshold)
            fee += (principal - InitiationFeeThreshold) * InitiationFeeMarginalRate;

        return Math.Min(fee, InitiationFeeCeiling);
    }
}
