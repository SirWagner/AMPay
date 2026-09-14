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

        b.Entity<Tenant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TradingName).HasMaxLength(200);
            e.Property(x => x.RegistrationNumber).HasMaxLength(50);
            e.Property(x => x.NcrNumber).HasMaxLength(50);
            e.Property(x => x.NetcashAccountNumber).HasMaxLength(11);
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
        b.Entity<ClientBudget>().HasOne(x => x.Client).WithMany(c => c.Budgets)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientCreditEnquiry>().HasOne(x => x.Client).WithMany(c => c.CreditEnquiries)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientNote>().HasOne(x => x.Client).WithMany(c => c.Notes)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientDocument>().HasOne(x => x.Client).WithMany(c => c.Documents)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);

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
        foreach (var p in b.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            p.SetPrecision(18);
            p.SetScale(2);
        }
    }
}
