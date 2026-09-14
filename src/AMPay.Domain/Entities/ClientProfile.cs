using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>Employment tab.</summary>
public class ClientEmployment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string? EmployerName { get; set; }
    public string? Occupation { get; set; }
    public string? Department { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? EmploymentType { get; set; }
    public DateTime? EmployedSince { get; set; }

    public string? WorkTelephone { get; set; }
    public string? SupervisorName { get; set; }
    public string? PayFrequency { get; set; }

    /// <summary>Day of month salary is received. Drives sensible DebiCheck collection-day defaults.</summary>
    public int? SalaryDay { get; set; }
}

/// <summary>Financial tab. Income and affordability, feeding the NCA affordability assessment.</summary>
public class ClientFinancial
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public decimal? GrossMonthlyIncome { get; set; }
    public decimal? NetMonthlyIncome { get; set; }
    public decimal? OtherIncome { get; set; }
    public string? OtherIncomeSource { get; set; }

    public decimal? TotalMonthlyExpenses { get; set; }
    public decimal? TotalMonthlyDebtRepayments { get; set; }

    public decimal? DisposableIncome =>
        NetMonthlyIncome is null ? null
        : NetMonthlyIncome - (TotalMonthlyExpenses ?? 0m) - (TotalMonthlyDebtRepayments ?? 0m);

    public string? BankName { get; set; }
    public int? YearsAtBank { get; set; }
}

/// <summary>
/// Payback tab. These values are what the DebiCheck mandate is built from, so the
/// field names deliberately track the Netcash mandate vocabulary.
/// </summary>
public class ClientPayback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public decimal? LoanAmount { get; set; }
    public decimal? InstalmentAmount { get; set; }
    public int? NumberOfInstalments { get; set; }

    public DebitFrequency Frequency { get; set; } = DebitFrequency.Monthly;

    /// <summary>Collection day: "01".."31" or "LDOM" (last day of month), per Netcash AddMandate.</summary>
    public string? CollectionDay { get; set; }

    /// <summary>Netcash collection frequency day code (field 250, AN7).</summary>
    public string? CollectionDayCode { get; set; }

    public DateTime? FirstCollectionDate { get; set; }

    /// <summary>Netcash field 246 / FirstCollectionDiffers.</summary>
    public bool FirstCollectionDiffers { get; set; }

    /// <summary>Netcash field 247. Only meaningful when <see cref="FirstCollectionDiffers"/>.</summary>
    public decimal? FirstCollectionAmount { get; set; }

    /// <summary>DebiCheck tracking days (field 232). Netcash accepts 1-10.</summary>
    public int TrackingDays { get; set; } = 5;

    public DateTime? AgreementDate { get; set; }
}

/// <summary>Other Details tab: risk categorisation, consent and record metadata.</summary>
public class ClientOtherDetails
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string? RiskCategory { get; set; }
    public string? ClientCategory { get; set; }
    public string? Source { get; set; }

    /// <summary>POPIA: direct-marketing consent must be explicit and separately recorded.</summary>
    public bool MarketingConsent { get; set; }
    public DateTime? MarketingConsentUtc { get; set; }

    /// <summary>POPIA s11 consent to process personal information. Required before capture completes.</summary>
    public bool DataProcessingConsent { get; set; }
    public DateTime? DataProcessingConsentUtc { get; set; }

    public bool CreditCheckConsent { get; set; }
    public DateTime? CreditCheckConsentUtc { get; set; }

    public string? Comments { get; set; }
}
