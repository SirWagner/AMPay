using Microsoft.EntityFrameworkCore;

namespace AMPay.Portal.Data;

/// <summary>The portal's own database. It has no tables, keys or connection in common with AM-Pay's.</summary>
public class PortalDbContext : DbContext
{
    public PortalDbContext(DbContextOptions<PortalDbContext> options) : base(options) { }

    public DbSet<Lender> Lenders => Set<Lender>();
    public DbSet<Applicant> Applicants => Set<Applicant>();
    public DbSet<LoanApplication> Applications => Set<LoanApplication>();
    public DbSet<ApplicationDocument> Documents => Set<ApplicationDocument>();
    public DbSet<CallbackRequest> Callbacks => Set<CallbackRequest>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<LenderLogo> LenderLogos => Set<LenderLogo>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Lender>(e =>
        {
            e.Property(x => x.PublicCode).HasMaxLength(12).IsRequired();
            e.HasIndex(x => x.PublicCode).IsUnique();
            e.HasIndex(x => x.AmpayTenantId).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TradingName).HasMaxLength(200);
            e.Property(x => x.NcrNumber).HasMaxLength(50);
            e.Property(x => x.ContactNumber).HasMaxLength(30);
            e.Property(x => x.ContactEmail).HasMaxLength(254);
            e.Property(x => x.WhatsAppNumber).HasMaxLength(20);
            e.Ignore(x => x.DisplayName);
        });

        b.Entity<Applicant>(e =>
        {
            e.Property(x => x.Mobile).HasMaxLength(15).IsRequired();
            e.HasIndex(x => new { x.LenderId, x.Mobile }).IsUnique();
            e.HasOne(x => x.Lender).WithMany().HasForeignKey(x => x.LenderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LoanApplication>(e =>
        {
            e.Property(x => x.Reference).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => new { x.LenderId, x.Status });
            e.HasOne(x => x.Lender).WithMany().HasForeignKey(x => x.LenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Applicant).WithMany(a => a.Applications).HasForeignKey(x => x.ApplicantId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.IsEditable);

            foreach (var name in new[] { nameof(LoanApplication.Title), nameof(LoanApplication.PayFrequency) })
                e.Property(name).HasMaxLength(30);
            foreach (var name in new[]
                     {
                         nameof(LoanApplication.FirstName), nameof(LoanApplication.MiddleNames), nameof(LoanApplication.Surname),
                         nameof(LoanApplication.AddressLine1), nameof(LoanApplication.AddressLine2), nameof(LoanApplication.Suburb),
                         nameof(LoanApplication.City), nameof(LoanApplication.Province), nameof(LoanApplication.EmployerName),
                         nameof(LoanApplication.Occupation), nameof(LoanApplication.PackageName)
                     })
                e.Property(name).HasMaxLength(150);
            e.Property(x => x.IdNumber).HasMaxLength(20);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.PostalCode).HasMaxLength(10);
            e.Property(x => x.ConsentIp).HasMaxLength(64);
            e.Property(x => x.AmpayClientNumber).HasMaxLength(50);

            foreach (var name in new[]
                     {
                         nameof(LoanApplication.GrossMonthlyIncome), nameof(LoanApplication.NetMonthlyIncome),
                         nameof(LoanApplication.OtherMonthlyIncome), nameof(LoanApplication.MonthlyExpenses),
                         nameof(LoanApplication.MonthlyDebtRepayments), nameof(LoanApplication.RequestedAmount),
                         nameof(LoanApplication.EstimatedInstalment)
                     })
                e.Property(name).HasPrecision(18, 2);
        });

        b.Entity<ApplicationDocument>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.StoragePath).HasMaxLength(400).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.Application).WithMany(a => a.Documents).HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CallbackRequest>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Mobile).HasMaxLength(15).IsRequired();
            e.Property(x => x.PreferredTime).HasMaxLength(60);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.RequestIp).HasMaxLength(64);
            e.HasIndex(x => new { x.LenderId, x.HandledUtc });
            e.HasOne(x => x.Lender).WithMany().HasForeignKey(x => x.LenderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LenderLogo>(e =>
        {
            e.HasKey(x => x.LenderId);
            e.HasOne<Lender>().WithOne().HasForeignKey<LenderLogo>(x => x.LenderId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Data).IsRequired();
        });

        b.Entity<OtpChallenge>(e =>
        {
            e.Property(x => x.Mobile).HasMaxLength(15).IsRequired();
            e.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestIp).HasMaxLength(64);
            e.HasIndex(x => new { x.LenderId, x.Mobile, x.CreatedUtc });
        });
    }
}
