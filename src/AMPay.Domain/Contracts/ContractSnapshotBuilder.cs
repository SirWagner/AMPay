using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Domain.Contracts;

/// <summary>
/// Builds the frozen contents of a contract pack from a loan and everything around it.
/// Pure: no database, no clock of its own, so the same inputs always give the same pack.
/// </summary>
public static class ContractSnapshotBuilder
{
    public record Inputs(
        Loan Loan,
        Client Client,
        Tenant Tenant,
        IReadOnlyDictionary<ContractTemplateKind, ContractTemplate> Templates,
        (AffordabilityInput Input, AffordabilityResult Result)? Budget,
        string Reference,
        DateTime IssuedUtc);

    public static ContractSnapshot Build(Inputs i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var loan = i.Loan;
        var client = i.Client;
        var tenant = i.Tenant;

        if (loan.Schedule.Count == 0)
            throw new InvalidOperationException("The loan has no repayment schedule to put in a contract.");

        var schedule = loan.Schedule.OrderBy(s => s.InstalmentNumber).ToList();
        var account = client.BankAccounts.FirstOrDefault(a => a.IsPrimary) ?? client.BankAccounts.FirstOrDefault();

        var templates = new[]
            {
                ContractTemplateKind.CreditAgreementTerms,
                ContractTemplateKind.DebitOrderAuthorisation,
                ContractTemplateKind.BudgetAcknowledgement,
                ContractTemplateKind.CreditLifeDisclosure
            }
            .Where(k => k != ContractTemplateKind.CreditLifeDisclosure || loan.TotalCreditLife > 0)
            .Select(k => i.Templates.TryGetValue(k, out var t)
                ? t
                : throw new InvalidOperationException($"The {k} template is missing for {tenant.Name}."))
            .ToList();

        return new ContractSnapshot
        {
            Reference = i.Reference,
            IssuedUtc = i.IssuedUtc,
            TemplatesApproved = templates.All(t => t.IsApproved),

            Provider = new ProviderDetails(
                RegisteredName: tenant.Name,
                TradingName: tenant.TradingName,
                RegistrationNumber: tenant.RegistrationNumber,
                VatNumber: tenant.VatNumber,
                NcrNumber: tenant.NcrNumber,
                PhysicalAddress: tenant.PhysicalAddress,
                PostalAddress: tenant.PostalAddress,
                ContactNumber: tenant.ContactNumber,
                ContactEmail: tenant.ContactEmail),

            Borrower = new BorrowerDetails(
                ClientNumber: client.ClientNumber,
                FullName: string.Join(' ', new[] { client.FirstName, client.MiddleNames }
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
                Surname: client.Surname,
                IdNumber: client.IdNumber,
                Employer: client.Employment?.EmployerName,
                WorkAddress: FormatAddress(client.Addresses.FirstOrDefault(a => a.AddressType == AddressType.Work)),
                ResidentialAddress: FormatAddress(client.Addresses.FirstOrDefault(a => a.AddressType == AddressType.Physical)),
                PostalAddress: FormatAddress(client.Addresses.FirstOrDefault(a => a.AddressType == AddressType.Postal)),
                Mobile: client.Addresses.Select(a => a.MobileNumber).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)),
                Email: client.Addresses.Select(a => a.EmailAddress).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))),

            Quote = new QuoteDetails
            {
                PackageName = loan.CreditPackage?.Name ?? "",
                QuoteDate = (loan.AgreementDate ?? loan.CreatedUtc).Date,
                LoanAmount = loan.Principal,
                CreditLife = loan.TotalCreditLife,
                InitiationFee = loan.InitiationFee,
                ServiceFees = loan.TotalServiceFees,
                Interest = loan.TotalInterest,
                // Fees are VAT-inclusive today; Part 3 adds a separate VAT line.
                Vat = 0m,
                MonthlyInterestRate = loan.MonthlyInterestRate,
                PenaltyInterestRate = loan.MonthlyInterestRate,
                FirstPaymentDate = schedule[0].DueDate,
                FinalPaymentDate = schedule[^1].DueDate,
                NumberOfInstalments = loan.NumberOfInstalments,
                PaymentMethod = "DebiCheck debit order",
                Frequency = FrequencyText(loan.Frequency),
                InstalmentAmount = loan.FirstInstalment,
                FinalInstalmentAmount = loan.FinalInstalment,
                Customised = false
            },

            Schedule = schedule
                .Select(s => new ScheduleLine(
                    s.InstalmentNumber, s.DueDate, s.InstalmentTotal, s.InterestPortion,
                    s.CapitalPortion, s.ServiceFee, s.CreditLifePremium, s.ClosingBalance))
                .ToList(),

            Mandate = new MandateDetails(
                BankName: account?.BankName,
                BranchCode: account?.BranchCode,
                AccountType: account is not null && Enum.IsDefined(account.AccountType) ? account.AccountType.ToString() : null,
                MaskedAccountNumber: account?.MaskedAccountNumber,
                AccountHolder: account?.AccountHolderName,
                InstalmentAmount: loan.FirstInstalment,
                TotalAmount: loan.TotalRepayable,
                NumberOfInstalments: loan.NumberOfInstalments,
                Frequency: FrequencyText(loan.Frequency),
                CollectionDay: loan.CollectionDay,
                FirstCollectionDate: loan.FirstCollectionDate,
                AgreementReference: i.Reference,
                StatementReference: StatementReference(tenant, loan),
                TrackingDays: loan.TrackingDays),

            Budget = i.Budget is { } b ? BudgetFrom(client, b.Input, b.Result) : null,

            CreditLife = loan.TotalCreditLife <= 0 ? null : new CreditLifeDetails(
                Underwriter: tenant.CreditLifeUnderwriter,
                Administrator: tenant.CreditLifeAdministrator,
                MonthlyRate: loan.CreditLifeRate,
                TotalPremium: loan.TotalCreditLife,
                SumAssured: loan.TotalRepayable,
                CommencementDate: (loan.AgreementDate ?? loan.CreatedUtc).Date,
                TerminationDate: schedule[^1].DueDate),

            Terms = templates.Select(t => new TermsSection(t.Kind.ToString(), t.Title, t.Body)).ToList()
        };
    }

    private static BudgetDetails BudgetFrom(Client client, AffordabilityInput input, AffordabilityResult r)
    {
        var lines = client.Budgets
            .Where(b => b.Amount > 0)
            .OrderBy(b => b.Kind).ThenBy(b => b.DisplayOrder)
            .Select(b => new BudgetDetailsLine(
                b.Description,
                b.Kind == BudgetLineKind.DebtInstalment ? "Instalment" : "Expense",
                b.Amount))
            .ToList();

        return new BudgetDetails(
            NetSalary: input.NetMonthlyIncome,
            OtherIncome: input.OtherMonthlyIncome,
            Lines: lines,
            DeclaredLivingExpenses: input.DeclaredMonthlyExpenses,
            StatutoryMinimum: r.StatutoryMinimumExpenses,
            // Includes the client's other loans with this lender, as the approval test does.
            DebtInstalments: input.ExistingDebtRepayments,
            NetOfNet: r.DiscretionaryIncome);
    }

    /// <summary>
    /// What appears on the client's bank statement: a short provider tag and the loan number,
    /// the same shape as the narrative on the lender's existing mandates.
    /// </summary>
    public static string StatementReference(Tenant tenant, Loan loan)
    {
        var name = new string((tenant.TradingName ?? tenant.Name)
            .ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

        var tag = name.Length > 10 ? name[..10] : name;
        return $"{tag} {loan.LoanNumber}".Trim();
    }

    private static string FrequencyText(DebitFrequency f) => f switch
    {
        DebitFrequency.Bimonthly => "Every two months",
        DebitFrequency.ThreeMonthly => "Quarterly",
        DebitFrequency.SixMonthly => "Every six months",
        DebitFrequency.Annually => "Annually",
        DebitFrequency.Weekly => "Weekly",
        DebitFrequency.Biweekly => "Every two weeks",
        _ => "Monthly"
    };

    private static string? FormatAddress(ClientAddress? a)
    {
        if (a is null) return null;

        var parts = new[] { a.Line1, a.Line2, a.Suburb, a.City, a.PostalCode }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());

        var text = string.Join(", ", parts);
        return text.Length == 0 ? null : text;
    }
}
