using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Domain.Contracts;

/// <summary>
/// Starting wording for each section of a contract pack.
/// <para>
/// THIS IS NOT LEGAL ADVICE AND NOT APPROVED WORDING. It is a structured placeholder that
/// names the matters a short-term credit agreement under the National Credit Act needs to
/// deal with, in plain language, so the pack can be built and tested end to end. Each
/// lender's attorney must replace or approve it. Until an administrator marks a template
/// approved, every document produced from it carries a DRAFT watermark.
/// </para>
/// </summary>
public static class ContractTemplateDefaults
{
    public static IReadOnlyList<ContractTemplate> For(Guid tenantId) => new[]
    {
        Make(tenantId, ContractTemplateKind.CreditAgreementTerms, "Terms and conditions", """
            DRAFT WORDING - to be reviewed and replaced by the credit provider's attorney before use.

            1. This is a short-term credit transaction subject to the National Credit Act 34 of 2005 and its regulations. If anything in this agreement conflicts with the Act, the Act applies.

            2. The borrower will repay the total amount repayable in the instalments, on the dates and by the payment method set out in the payment schedule.

            3. The borrower may settle this agreement at any time without penalty. The settlement amount is the unpaid principal, plus interest, fees and charges due up to the settlement date.

            4. Interest is charged on the outstanding balance at the rate in the quote. Interest on arrears will not exceed that rate.

            5. If the borrower falls into arrears, the credit provider will give written notice before taking any legal step, as the Act requires, and the borrower may refer the agreement to a debt counsellor, alternative dispute resolution agent, consumer court or ombud.

            6. The credit provider will send the borrower a statement of account, free of charge, as the Act requires.

            7. The borrower must tell the credit provider promptly about any change to their employment, address or banking details.

            8. The borrower has the right to receive this agreement in an official language they read and understand, and confirms that its terms and costs have been explained to them.

            9. The credit provider may report the borrower's payment behaviour to registered credit bureaux, as permitted by the Act.

            10. This agreement, with its quote, payment schedule and debit order authorisation, is the whole agreement. A change has effect only if recorded in writing and accepted by both parties.
            """),

        Make(tenantId, ContractTemplateKind.DebitOrderAuthorisation, "Debit order authorisation", """
            DRAFT WORDING - to be reviewed and replaced by the credit provider's attorney before use.

            1. The borrower authorises the credit provider to collect each instalment shown above from the bank account shown above, by DebiCheck debit order.

            2. No collection will exceed the instalment amount or the frequency agreed in the credit agreement.

            3. The borrower will be asked by their own bank to authenticate this mandate. Collections will only be made against an authenticated mandate.

            4. The borrower agrees that a collection may be moved to align with the day their salary or wages are paid, including around weekends, public holidays and December, and that the bank may track the account for the agreed number of days if funds are not available on the collection date.

            5. This authorisation remains in force until every amount due under the credit agreement has been paid, unless cancelled in writing. Cancelling the debit order does not cancel the debt.

            6. The borrower's bank may charge fees for processing debit orders.
            """),

        Make(tenantId, ContractTemplateKind.BudgetAcknowledgement, "Budget acknowledgement", """
            DRAFT WORDING - to be reviewed and replaced by the credit provider's attorney before use.

            1. The borrower confirms that the income, expenses and existing credit instalments shown above are complete and correct to the best of their knowledge.

            2. The borrower has considered whether they can afford the instalment on this agreement in the light of this budget, and confirms that they can.

            3. The borrower understands that the credit provider has relied on this information in assessing affordability, as the National Credit Act requires.
            """),

        Make(tenantId, ContractTemplateKind.CreditLifeDisclosure, "Credit life insurance", """
            DRAFT WORDING - to be reviewed and replaced by the credit provider's attorney before use.

            1. Credit life insurance covers the outstanding balance of this agreement on death, permanent or temporary disability or retrenchment, subject to the policy wording, which the borrower will receive.

            2. The borrower may substitute a policy of their own choice that provides at least the same cover, as section 106 of the National Credit Act allows.

            3. The premium shown is included in the cost of credit and will not exceed the maximum prescribed by the regulations.

            4. Cover begins when the agreement begins and ends when it is settled or terminated.
            """)
    };

    private static ContractTemplate Make(Guid tenantId, ContractTemplateKind kind, string title, string body) => new()
    {
        TenantId = tenantId,
        Kind = kind,
        Title = title,
        Body = body.Trim(),
        IsApproved = false
    };

    /// <summary>The templates a tenant is missing, by kind. Never replaces existing wording.</summary>
    public static IReadOnlyList<ContractTemplate> MissingFor(Guid tenantId, IEnumerable<ContractTemplateKind> existing)
    {
        var have = existing.ToHashSet();
        return For(tenantId).Where(t => !have.Contains(t.Kind)).ToList();
    }
}
