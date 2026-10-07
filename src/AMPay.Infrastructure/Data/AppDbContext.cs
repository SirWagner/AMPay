using AMPay.Domain.Entities;
using AMPay.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantServiceKey> TenantServiceKeys => Set<TenantServiceKey>();

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientEmployment> ClientEmployments => Set<ClientEmployment>();
    public DbSet<ClientFinancial> ClientFinancials => Set<ClientFinancial>();
    public DbSet<ClientPayback> ClientPaybacks => Set<ClientPayback>();
    public DbSet<ClientOtherDetails> ClientOtherDetails => Set<ClientOtherDetails>();
    public DbSet<ClientBankAccount> ClientBankAccounts => Set<ClientBankAccount>();
    public DbSet<ClientWallet> ClientWallets => Set<ClientWallet>();
    public DbSet<ClientAddress> ClientAddresses => Set<ClientAddress>();
    public DbSet<ClientReference> ClientReferences => Set<ClientReference>();
    public DbSet<ClientBudget> ClientBudgets => Set<ClientBudget>();
    public DbSet<ClientCreditEnquiry> ClientCreditEnquiries => Set<ClientCreditEnquiry>();
    public DbSet<ClientNote> ClientNotes => Set<ClientNote>();
    public DbSet<ClientDocument> ClientDocuments => Set<ClientDocument>();
    public DbSet<ClientPhoto> ClientPhotos => Set<ClientPhoto>();

    public DbSet<CreditPackage> CreditPackages => Set<CreditPackage>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanScheduleEntry> LoanScheduleEntries => Set<LoanScheduleEntry>();
    public DbSet<AffordabilityAssessment> AffordabilityAssessments => Set<AffordabilityAssessment>();

    public DbSet<ContractTemplate> ContractTemplates => Set<ContractTemplate>();
    public DbSet<LoanContract> LoanContracts => Set<LoanContract>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();

    public DbSet<DebiCheckMandate> Mandates => Set<DebiCheckMandate>();
    public DbSet<MandateEvent> MandateEvents => Set<MandateEvent>();
    public DbSet<PayNowTransaction> PayNowTransactions => Set<PayNowTransaction>();
    public DbSet<NetcashBatch> NetcashBatches => Set<NetcashBatch>();
    public DbSet<NetcashBatchError> NetcashBatchErrors => Set<NetcashBatchError>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Computed properties live on the domain for convenience; they are not columns.
        b.Entity<Client>().Ignore(x => x.FullName);
        b.Entity<ClientFinancial>().Ignore(x => x.DisposableIncome);
        b.Entity<ClientBankAccount>().Ignore(x => x.MaskedAccountNumber);
        b.Entity<TenantServiceKey>().Ignore(x => x.NeedsRevalidation);
        b.Entity<DebiCheckMandate>().Ignore(x => x.IsCollectable);
        b.Entity<LoanContract>().Ignore(x => x.IsOpen);

        b.Entity<ContractTemplate>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Body).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Kind }).IsUnique();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LoanContract>(e =>
        {
            e.Property(x => x.Reference).HasMaxLength(40).IsRequired();
            e.Property(x => x.SnapshotJson).IsRequired();
            e.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SentChannels).HasMaxLength(20);
            e.Property(x => x.AccessTokenHash).HasMaxLength(64);
            e.Property(x => x.OtpHash).HasMaxLength(64);
            e.Property(x => x.SignedName).HasMaxLength(200);
            e.Property(x => x.SignedIdNumber).HasMaxLength(20);
            e.Property(x => x.SignedMobile).HasMaxLength(20);
            e.Property(x => x.SignedIp).HasMaxLength(64);
            e.Property(x => x.SignedUserAgent).HasMaxLength(400);
            e.Property(x => x.VoidReason).HasMaxLength(500);

            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => new { x.LoanId, x.Issue }).IsUnique();

            // The token arrives in a public URL; finding the contract by it must be an index
            // seek, and two contracts can never share one.
            e.HasIndex(x => x.AccessTokenHash).IsUnique().HasFilter("[AccessTokenHash] IS NOT NULL");

            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Loan).WithMany().HasForeignKey(x => x.LoanId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SignedCopyDocument).WithMany().HasForeignKey(x => x.SignedCopyDocumentId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<OutboundMessage>(e =>
        {
            e.Property(x => x.To).HasMaxLength(254).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.Body).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.Context).HasMaxLength(100);
            e.HasIndex(x => x.CreatedUtc);
            e.HasIndex(x => x.Context);
        });
        b.Entity<Loan>().Ignore(x => x.LatestAssessment);
        b.Entity<Loan>().Ignore(x => x.IsEditable);

        b.Entity<Tenant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TradingName).HasMaxLength(200);
            e.Property(x => x.RegistrationNumber).HasMaxLength(50);
            e.Property(x => x.NcrNumber).HasMaxLength(50);
            e.Property(x => x.NetcashAccountNumber).HasMaxLength(11);
            e.Property(x => x.VatNumber).HasMaxLength(20);
            e.Property(x => x.PhysicalAddress).HasMaxLength(300);
            e.Property(x => x.PostalAddress).HasMaxLength(300);
            e.Property(x => x.CreditLifeUnderwriter).HasMaxLength(200);
            e.Property(x => x.CreditLifeAdministrator).HasMaxLength(200);
            e.Property(x => x.SelfServiceCode).HasMaxLength(12);
            e.HasIndex(x => x.SelfServiceCode).IsUnique().HasFilter("[SelfServiceCode] IS NOT NULL");
            e.Property(x => x.AdvisorWhatsApp).HasMaxLength(20);
            e.HasIndex(x => x.NetcashAccountNumber).IsUnique()
                .HasFilter("[NetcashAccountNumber] IS NOT NULL");

            // Only one tenant may be the platform owner.
            e.HasIndex(x => x.IsPlatformOwner).IsUnique().HasFilter("[IsPlatformOwner] = 1");
        });

        b.Entity<TenantServiceKey>(e =>
        {
            e.Property(x => x.SecretName).HasMaxLength(200).IsRequired();
            e.Property(x => x.KeyHint).HasMaxLength(8);
            e.HasIndex(x => new { x.TenantId, x.ServiceId }).IsUnique();
            e.HasOne(x => x.Tenant).WithMany(t => t.ServiceKeys)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Client>(e =>
        {
            e.Property(x => x.ClientNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Surname).HasMaxLength(100).IsRequired();
            e.Property(x => x.IdNumber).HasMaxLength(20).IsRequired();

            // Field 101 must be unique in the tenant Netcash masterfile, so enforce it here too.
            e.HasIndex(x => new { x.TenantId, x.ClientNumber }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.IdNumber });

            e.HasOne(x => x.Tenant).WithMany(t => t.Clients)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Employment).WithOne(x => x.Client!)
                .HasForeignKey<ClientEmployment>(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Financial).WithOne(x => x.Client!)
                .HasForeignKey<ClientFinancial>(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Payback).WithOne(x => x.Client!)
                .HasForeignKey<ClientPayback>(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.OtherDetails).WithOne(x => x.Client!)
                .HasForeignKey<ClientOtherDetails>(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Photo).WithOne(x => x.Client!)
                .HasForeignKey<ClientPhoto>(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ClientBankAccount>(e =>
        {
            e.Property(x => x.AccountHolderName).HasMaxLength(30).IsRequired();
            e.Property(x => x.BankName).HasMaxLength(100);
            e.Property(x => x.BranchCode).HasMaxLength(6).IsRequired();
            e.Property(x => x.AccountNumber).HasMaxLength(16).IsRequired();
            e.HasOne(x => x.Client).WithMany(c => c.BankAccounts)
                .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ClientWallet>().HasOne(x => x.Client).WithMany(c => c.Wallets)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientAddress>().HasOne(x => x.Client).WithMany(c => c.Addresses)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientReference>().HasOne(x => x.Client).WithMany(c => c.References)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientBudget>(e =>
        {
            e.Property(x => x.Category).HasMaxLength(40);
            e.HasOne(x => x.Client).WithMany(c => c.Budgets)
                .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ClientCreditEnquiry>().HasOne(x => x.Client).WithMany(c => c.CreditEnquiries)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientNote>().HasOne(x => x.Client).WithMany(c => c.Notes)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientDocument>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.StoragePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.ReviewNotes).HasMaxLength(1000);
            e.Property(x => x.ContentHash).HasMaxLength(64);

            e.HasIndex(x => new { x.ClientId, x.ReviewStatus });

            e.HasOne(x => x.Client).WithMany(c => c.Documents)
                .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CreditPackage>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);

            // Rates need more than 2 decimal places - 0.0045 is a real credit life rate.
            e.Property(x => x.MonthlyInterestRate).HasPrecision(9, 6);
            e.Property(x => x.InitiationFeeRate).HasPrecision(9, 6);
            e.Property(x => x.CreditLifeRate).HasPrecision(9, 6);

            // A customer may run several packages at the same tier - two Gold price lists
            // for two kinds of borrower - so the tier is only a grouping for reporting.
            // What must be unique is the name: that is what the operator picks from when
            // quoting, and two identical names is an ambiguity nobody wants at quote time.
            // (Case-insensitive, courtesy of the database collation.)
            e.HasIndex(x => new { x.TenantId, x.Tier });
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();

            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Loan>(e =>
        {
            e.Property(x => x.LoanNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.CollectionDay).HasMaxLength(4);
            e.Property(x => x.CollectionDayCode).HasMaxLength(7);
            e.Property(x => x.DecisionNotes).HasMaxLength(2000);

            e.Property(x => x.MonthlyInterestRate).HasPrecision(9, 6);
            e.Property(x => x.CreditLifeRate).HasPrecision(9, 6);

            e.HasIndex(x => new { x.TenantId, x.LoanNumber }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.ClientId);

            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            // A client with a loan cannot be deleted out from under it.
            e.HasOne(x => x.Client).WithMany(c => c.Loans).HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.CreditPackage).WithMany(p => p.Loans)
                .HasForeignKey(x => x.CreditPackageId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Mandate).WithMany().HasForeignKey(x => x.MandateId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<LoanScheduleEntry>(e =>
        {
            e.HasIndex(x => new { x.LoanId, x.InstalmentNumber }).IsUnique();
            e.HasOne(x => x.Loan).WithMany(l => l.Schedule)
                .HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AffordabilityAssessment>(e =>
        {
            e.Property(x => x.Reasoning).HasMaxLength(2000);
            e.Property(x => x.OverrideReason).HasMaxLength(1000);
            e.Property(x => x.UtilisationRatio).HasPrecision(9, 4);

            e.HasIndex(x => new { x.LoanId, x.AssessedUtc });
            e.HasOne(x => x.Loan).WithMany(l => l.Assessments)
                .HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DebiCheckMandate>(e =>
        {
            e.Property(x => x.AccountReference).HasMaxLength(32).IsRequired();
            e.Property(x => x.MandateTemplateId).HasMaxLength(14);
            e.Property(x => x.ContractReference).HasMaxLength(100);
            e.Property(x => x.MandateReference).HasMaxLength(50);
            e.Property(x => x.CollectionDayCode).HasMaxLength(7);

            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.ContractReference);
            e.HasIndex(x => new { x.TenantId, x.AccountReference });

            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Client).WithMany(c => c.Mandates).HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BankAccount).WithMany().HasForeignKey(x => x.BankAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MandateEvent>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(60).IsRequired();
            e.HasIndex(x => new { x.MandateId, x.OccurredUtc });
            e.HasOne(x => x.Mandate).WithMany(m => m.Events)
                .HasForeignKey(x => x.MandateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PayNowTransaction>(e =>
        {
            e.Property(x => x.PaymentReference).HasMaxLength(100).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.PaymentReference).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<NetcashBatch>(e =>
        {
            e.Property(x => x.BatchName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Instruction).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.FileToken);
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<NetcashBatchError>().HasOne(x => x.Batch).WithMany(x => x.Errors)
            .HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Ignore(x => x.IsPlatformUser);
            e.HasIndex(x => x.TenantId);
        });

        // Money is money. Never let SQL Server pick a default precision for it.
        //
        // Properties that already declared a precision above are left alone: rates are not
        // money. A credit life rate of 0.0045 forced to scale 2 becomes 0.00, and the
        // premium silently disappears from every schedule.
        foreach (var p in b.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?))
                     .Where(p => p.GetPrecision() is null))
        {
            p.SetPrecision(18);
            p.SetScale(2);
        }
    }
}
