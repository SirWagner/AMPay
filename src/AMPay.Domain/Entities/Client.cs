using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// A captured client. Structure mirrors the Maxmoney NovaMesh onboarding tabs
/// (General, Employment, Financial, Banking, Payback, Address, Other Details,
/// References, Budgets, Credit Check, Notes, Documents, Photo) but is our own schema -
/// there is no integration back to Maxmoney.
/// <para>
/// Unlike Maxmoney, the capture is a single wizard over one aggregate: a tab is a step,
/// not a separate save-or-lose-your-work form.
/// </para>
/// </summary>
public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Per-tenant human reference. Becomes the Netcash "Account reference" (field 101, AN32)
    /// and must be unique within the tenant's Netcash masterfile.
    /// </summary>
    public string ClientNumber { get; set; } = string.Empty;

    // ---- General tab ----
    public string? Title { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleNames { get; set; }
    public string Surname { get; set; } = string.Empty;

    /// <summary>SA ID number (13 digits) when <see cref="IsSaIdNumber"/>, else passport / company registration.</summary>
    public string IdNumber { get; set; } = string.Empty;

    /// <summary>Netcash field 127 / IsIdNumber. True = SA ID (CDV validated by Netcash).</summary>
    public bool IsSaIdNumber { get; set; } = true;

    public DateTime? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? MaritalStatus { get; set; }
    public string? Nationality { get; set; } = "South African";
    public string? PreferredLanguage { get; set; }

    public ClientStatus Status { get; set; } = ClientStatus.Draft;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }
    public string? CreatedByUserId { get; set; }

    public string FullName => string.Join(' ',
        new[] { FirstName, MiddleNames, Surname }.Where(s => !string.IsNullOrWhiteSpace(s)));

    // ---- One-to-one tabs ----
    public ClientEmployment? Employment { get; set; }
    public ClientFinancial? Financial { get; set; }
    public ClientPayback? Payback { get; set; }
    public ClientOtherDetails? OtherDetails { get; set; }
    public ClientPhoto? Photo { get; set; }

    // ---- One-to-many tabs ----
    public ICollection<ClientBankAccount> BankAccounts { get; set; } = new List<ClientBankAccount>();
    public ICollection<ClientWallet> Wallets { get; set; } = new List<ClientWallet>();
    public ICollection<ClientAddress> Addresses { get; set; } = new List<ClientAddress>();
    public ICollection<ClientReference> References { get; set; } = new List<ClientReference>();
    public ICollection<ClientBudget> Budgets { get; set; } = new List<ClientBudget>();
    public ICollection<ClientCreditEnquiry> CreditEnquiries { get; set; } = new List<ClientCreditEnquiry>();
    public ICollection<ClientNote> Notes { get; set; } = new List<ClientNote>();
    public ICollection<ClientDocument> Documents { get; set; } = new List<ClientDocument>();
    public ICollection<DebiCheckMandate> Mandates { get; set; } = new List<DebiCheckMandate>();

    /// <summary>
    /// Credit agreements raised against this client. Loans are originated after onboarding
    /// completes, not during it - see <see cref="Loan"/>.
    /// </summary>
    public ICollection<Loan> Loans { get; set; } = new List<Loan>();
}
