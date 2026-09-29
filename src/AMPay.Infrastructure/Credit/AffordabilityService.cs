using System.Globalization;
using AMPay.Domain.Credit;
using AMPay.Domain.Enums;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Credit;

/// <summary>
/// NCA s78-81 affordability assessment.
/// <para>
/// Discretionary income is net income less the applied expense floor and existing debt
/// service. The floor is the <em>greater</em> of what the client declared and the
/// Regulation 23A(7) minimum for their income band - a client who under-declares their
/// living costs, whether to qualify or through optimism, must not be lent into hardship on
/// the strength of it.
/// </para>
/// </summary>
public class AffordabilityService : IAffordabilityService
{
    private static readonly CultureInfo Rand = CultureInfo.GetCultureInfo("en-ZA");

    private readonly AffordabilityNorms _norms;

    public AffordabilityService(IOptions<AffordabilityNorms> norms) => _norms = norms.Value;

    public AffordabilityResult Assess(AffordabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var statutoryMinimum = Round(_norms.MinimumExpensesFor(input.GrossMonthlyIncome));

        // Without income there is nothing to assess. This is a data gap, not a decline -
        // the distinction matters, because a decline is reportable and a gap is fixable.
        if (input.NetMonthlyIncome <= 0 || input.GrossMonthlyIncome <= 0)
        {
            return new AffordabilityResult
            {
                Outcome = AffordabilityOutcome.Insufficient,
                StatutoryMinimumExpenses = statutoryMinimum,
                AppliedExpenses = 0m,
                DiscretionaryIncome = 0m,
                SurplusAfterInstalment = 0m,
                UtilisationRatio = 0m,
                MaximumAffordableInstalment = 0m,
                Reasoning =
                    "No income on file. Capture gross and net monthly income on the client " +
                    "Financial tab before assessing affordability."
            };
        }

        var applied = Math.Max(Round(input.DeclaredMonthlyExpenses), statutoryMinimum);
        var discretionary = Round(input.NetMonthlyIncome - applied - input.ExistingDebtRepayments);
        var surplus = Round(discretionary - input.ProposedInstalment);

        var utilisation = discretionary > 0
            ? Math.Round(input.ProposedInstalment / discretionary, 4, MidpointRounding.AwayFromZero)
            : 0m;

        var maxAffordable = discretionary > 0 ? discretionary : 0m;

        var usingStatutory = statutoryMinimum > Round(input.DeclaredMonthlyExpenses);
        var floorNote = usingStatutory
            ? $" The Regulation 23A minimum of {Money(statutoryMinimum)} was applied instead of the " +
              $"declared {Money(input.DeclaredMonthlyExpenses)}, being the higher of the two."
            : $" The declared expenses of {Money(input.DeclaredMonthlyExpenses)} exceed the " +
              $"Regulation 23A minimum of {Money(statutoryMinimum)} and were used.";

        var basis =
            $"Net income {Money(input.NetMonthlyIncome)} less expenses {Money(applied)} " +
            $"less existing debt {Money(input.ExistingDebtRepayments)} leaves discretionary " +
            $"income of {Money(discretionary)}.{floorNote}";

        AffordabilityOutcome outcome;
        string verdict;

        if (discretionary <= 0)
        {
            outcome = AffordabilityOutcome.Fail;
            verdict =
                " There is no discretionary income. No further credit may be advanced - " +
                "doing so would be reckless under s80 of the National Credit Act.";
        }
        else if (input.ProposedInstalment > discretionary)
        {
            outcome = AffordabilityOutcome.Fail;
            verdict =
                $" The proposed instalment of {Money(input.ProposedInstalment)} exceeds that by " +
                $"{Money(input.ProposedInstalment - discretionary)}. The client cannot service " +
                $"this loan. The largest instalment that fits is {Money(maxAffordable)}.";
        }
        else if (utilisation > _norms.MarginalUtilisationThreshold)
        {
            outcome = AffordabilityOutcome.Marginal;
            verdict =
                $" The proposed instalment of {Money(input.ProposedInstalment)} consumes " +
                $"{utilisation:P1} of it, leaving {Money(surplus)}. That is within the client's " +
                $"means but above the {_norms.MarginalUtilisationThreshold:P0} comfort threshold, " +
                "so it needs a human decision.";
        }
        else
        {
            outcome = AffordabilityOutcome.Pass;
            verdict =
                $" The proposed instalment of {Money(input.ProposedInstalment)} consumes " +
                $"{utilisation:P1} of it, leaving {Money(surplus)} a month.";
        }

        return new AffordabilityResult
        {
            Outcome = outcome,
            StatutoryMinimumExpenses = statutoryMinimum,
            AppliedExpenses = applied,
            DiscretionaryIncome = discretionary,
            SurplusAfterInstalment = surplus,
            UtilisationRatio = utilisation,
            MaximumAffordableInstalment = maxAffordable,
            Reasoning = basis + verdict
        };
    }

    private static string Money(decimal value) => value.ToString("C2", Rand);

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
