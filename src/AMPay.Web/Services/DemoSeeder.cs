using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Controllers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Development only: fills a fictional lender, "Demo Lending", with about 400 loans over six
/// months so the lender dashboard can be shown with realistic numbers.
/// <para>
/// Run with <c>dotnet run --project src/AMPay.Web -- --seed-demo</c> (Development environment).
/// Every name, ID number and phone number is generated. It never touches another tenant, and
/// it does nothing if Demo Lending already exists. Arrears and failed debits cannot be seeded
/// until repayment records exist (docs/lender-dashboard-data-map.md, M1).
/// </para>
/// </summary>
public static class DemoSeeder
{
    public const string DemoNcr = "NCRCP-DEMO";

    private static readonly string[] FirstNames =
    {
        "Thabo", "Lerato", "Sipho", "Nomvula", "Pieter", "Anele", "Zanele", "Kagiso", "Lindiwe", "Johan",
        "Ayanda", "Refilwe", "Mandla", "Busisiwe", "Themba", "Naledi", "Karabo", "Palesa", "Sibusiso", "Nandi"
    };

    private static readonly string[] Surnames =
    {
        "Mokoena", "Dlamini", "Nkosi", "Botha", "Khumalo", "Naidoo", "Ndlovu", "van der Merwe", "Mahlangu", "Zulu",
        "Mthembu", "Pillay", "Molefe", "Sithole", "Jacobs", "Radebe", "Maseko", "Daniels", "Ngcobo", "Petersen"
    };

    public static async Task RunAsync(IServiceProvider services, ILogger log)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var env = sp.GetRequiredService<IWebHostEnvironment>();
        if (!env.IsDevelopment())
            throw new InvalidOperationException("--seed-demo runs in Development only.");

        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var pricing = sp.GetRequiredService<ILoanPricingService>();

        if (await db.Tenants.AnyAsync(t => t.NcrNumber == DemoNcr))
        {
            log.LogWarning("Demo Lending already exists - nothing seeded.");
            return;
        }

        var rnd = new Random(20261014);
        var utcNow = DateTime.UtcNow;
        var today = utcNow.AddHours(2).Date;

        var tenant = new Tenant
        {
            Name = "Demo Lending (Pty) Ltd",
            TradingName = "Demo Lending",
            NcrNumber = DemoNcr,
            Status = TenantStatus.Active,
            ContactNumber = "010 000 0000",
            ContactEmail = "hello@demo-lending.test"
        };
        db.Tenants.Add(tenant);
        var packages = CreditPackageDefaults.For(tenant.Id).ToList();
        db.CreditPackages.AddRange(packages);
        await db.SaveChangesAsync();
        var regular = packages.OrderBy(p => p.Tier).First();

        // A lender administrator for the demo, password in App_Data (git-ignored), not the console.
        var password = UsersController.TemporaryPassword();
        var admin = new ApplicationUser
        {
            UserName = "demo-admin@demo-lending.test",
            Email = "demo-admin@demo-lending.test",
            EmailConfirmed = true,
            FullName = "Demo Lending Administrator",
            TenantId = tenant.Id
        };
        var created = await users.CreateAsync(admin, password);
        if (created.Succeeded)
        {
            await users.AddToRoleAsync(admin, AppRoles.TenantAdmin);
            var file = Path.Combine(env.ContentRootPath, "App_Data", "demo-credentials.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, $"Demo Lending administrator\n{admin.Email}\n{password}\n");
            log.LogInformation("Demo administrator {Email} created; password written to App_Data/demo-credentials.txt.", admin.Email);
        }

        var loanNo = 0;
        var clientNo = 0;
        const int clientCount = 340;

        for (var i = 0; i < clientCount; i++)
        {
            var client = NewClient(tenant.Id, ++clientNo, rnd, utcNow);
            db.Clients.Add(client);

            // Most clients take one loan, some come back for a second.
            var loansForClient = rnd.NextDouble() < 0.18 ? 2 : 1;
            var start = utcNow.AddDays(-rnd.Next(5, 182));

            for (var j = 0; j < loansForClient; j++)
            {
                var disbursed = start.AddDays(j * rnd.Next(35, 70));
                if (disbursed > utcNow) break;

                var principal = Math.Round(rnd.Next(5, 80) * 100m, 0);
                var term = rnd.Next(1, 7);
                var firstDue = LoanOrigination.DefaultFirstCollectionDate(25, disbursed.AddHours(2).Date);
                var quote = pricing.Quote(new LoanQuoteRequest(principal, term, firstDue), regular);

                var loan = new Loan
                {
                    TenantId = tenant.Id,
                    ClientId = client.Id,
                    CreditPackageId = regular.Id,
                    LoanNumber = $"DL-{++loanNo:D6}",
                    CreatedUtc = disbursed.AddDays(-2),
                    DecidedUtc = disbursed.AddDays(-1),
                    DisbursedUtc = disbursed,
                    AgreementDate = disbursed.Date
                };
                var schedule = LoanOrigination.ApplyQuote(loan, quote);

                var lastDue = schedule.Max(s => s.DueDate);
                loan.Status = lastDue < today
                    ? (rnd.NextDouble() < 0.04 ? LoanStatus.WrittenOff : LoanStatus.Settled)
                    : LoanStatus.Disbursed;

                if (loan.Status == LoanStatus.Disbursed)
                {
                    var mandate = new DebiCheckMandate
                    {
                        TenantId = tenant.Id,
                        ClientId = client.Id,
                        AccountReference = loan.LoanNumber,
                        CollectionAmount = quote.FirstInstalment,
                        CreatedUtc = disbursed.AddDays(-1),
                        SubmittedUtc = disbursed.AddDays(-1),
                        Status = rnd.NextDouble() < 0.9 ? MandateStatus.Authenticated : MandateStatus.AwaitingDebtorAuthentication
                    };
                    if (mandate.Status == MandateStatus.Authenticated) mandate.AuthenticatedUtc = disbursed;
                    db.Mandates.Add(mandate);
                    loan.MandateId = mandate.Id;
                }

                db.Loans.Add(loan);
                db.LoanScheduleEntries.AddRange(schedule);
                SeedCollections(db, loan, schedule, today, rnd);

                if (rnd.NextDouble() < 0.92) db.AffordabilityAssessments.Add(Assessment(loan, quote.FirstInstalment));
            }

            if (rnd.NextDouble() < 0.6)
                db.ClientCreditEnquiries.Add(new ClientCreditEnquiry { ClientId = client.Id, Bureau = "Demo bureau", Outcome = "Clear", EnquiryDate = client.CreatedUtc });

            if (i % 50 == 49) await db.SaveChangesAsync();
        }

        // Work in the queues: decisions pending, recently declined, documents to review.
        for (var i = 0; i < 24; i++)
        {
            var client = NewClient(tenant.Id, ++clientNo, rnd, utcNow, onboarded: i % 3 != 0);
            db.Clients.Add(client);
            var principal = Math.Round(rnd.Next(5, 60) * 100m, 0);
            var quote = pricing.Quote(new LoanQuoteRequest(principal, rnd.Next(1, 7), LoanOrigination.DefaultFirstCollectionDate(25, today)), regular);
            var loan = new Loan
            {
                TenantId = tenant.Id,
                ClientId = client.Id,
                CreditPackageId = regular.Id,
                LoanNumber = $"DL-{++loanNo:D6}",
                CreatedUtc = utcNow.AddDays(-rnd.Next(0, 9)),
                Status = i < 12 ? LoanStatus.PendingApproval : LoanStatus.Declined
            };
            if (loan.Status == LoanStatus.Declined) { loan.DecidedUtc = utcNow.AddDays(-rnd.Next(0, 9)); loan.DecisionNotes = "Demo: affordability"; }
            db.Loans.Add(loan);
            db.LoanScheduleEntries.AddRange(LoanOrigination.ApplyQuote(loan, quote));
        }

        // A few this month too, so the current month is not empty in a demo.
        for (var i = 0; i < 6; i++)
        {
            var when = utcNow.AddDays(-rnd.Next(0, Math.Max(1, today.Day - 1))).AddHours(-1);
            db.PayNowTransactions.Add(new PayNowTransaction
            {
                TenantId = tenant.Id, PaymentReference = $"DEMO-PN-M{i:D2}", Amount = Math.Round(rnd.Next(2, 30) * 100m, 0),
                Status = PaymentStatus.Complete, CreatedUtc = when.AddMinutes(-5), CompletedUtc = when
            });
        }

        // Pay Now payments over the six months.
        for (var i = 0; i < 60; i++)
        {
            var when = utcNow.AddDays(-rnd.Next(0, 180));
            db.PayNowTransactions.Add(new PayNowTransaction
            {
                TenantId = tenant.Id,
                PaymentReference = $"DEMO-PN-{i:D4}",
                Amount = Math.Round(rnd.Next(2, 30) * 100m, 0),
                Status = rnd.NextDouble() < 0.85 ? PaymentStatus.Complete : PaymentStatus.Declined,
                CreatedUtc = when.AddMinutes(-5),
                CompletedUtc = when
            });
        }

        tenant.LendingCapital = 1_500_000m;
        await db.SaveChangesAsync();

        // A start-of-month snapshot so the month-on-month figures have something to compare with.
        // Demo only: it is today's book measured as at the 1st, not a true record of that day.
        var monthStart = new DateTime(today.Year, today.Month, 1);
        if (monthStart < today)
        {
            var then = await PortfolioMetrics.PositionAsync(db, tenant.Id, monthStart);
            db.PortfolioSnapshots.Add(PortfolioMetrics.ToSnapshot(tenant.Id, monthStart, then));
            await db.SaveChangesAsync();
        }

        log.LogInformation("Demo Lending seeded: {Clients} clients, {Loans} loans.", clientNo, loanNo);
    }

    private static readonly string[] DemoUnpaidCodes = { "02", "02", "02", "02", "22", "04", "30", "34", "99" };

    /// <summary>
    /// Debit results for every instalment already due: most collected, some returned unpaid,
    /// some of those made good later by Pay Now or EFT. Then the instalments are marked paid
    /// from that ledger exactly as the live app does.
    /// </summary>
    private static void SeedCollections(AppDbContext db, Loan loan, IReadOnlyList<LoanScheduleEntry> schedule, DateTime today, Random rnd)
    {
        var collections = new List<LoanCollection>();
        var writtenOff = loan.Status == LoanStatus.WrittenOff;
        var settled = loan.Status == LoanStatus.Settled;
        // A written-off loan stopped paying part-way; a struggling active loan misses more often.
        var stopAfter = writtenOff ? rnd.Next(0, schedule.Count) : int.MaxValue;
        var failRate = rnd.NextDouble() < 0.15 ? 0.6 : 0.08;

        foreach (var e in schedule.OrderBy(s => s.InstalmentNumber).Where(s => s.DueDate < today))
        {
            var fails = !settled && (e.InstalmentNumber > stopAfter || rnd.NextDouble() < failRate);
            var debit = new LoanCollection
            {
                TenantId = loan.TenantId,
                LoanId = loan.Id,
                ScheduleEntryId = e.Id,
                Channel = CollectionChannel.DebiCheck,
                ActionDate = e.DueDate,
                Amount = e.InstalmentTotal,
                Status = fails ? CollectionStatus.Failed : CollectionStatus.Successful,
                ReceivedUtc = fails ? null : Sast.StartUtc(e.DueDate).AddHours(8),
                CreatedUtc = Sast.StartUtc(e.DueDate)
            };
            if (fails)
            {
                debit.FailureCode = DemoUnpaidCodes[rnd.Next(DemoUnpaidCodes.Length)];
                debit.FailureGroup = UnpaidCodes.GroupOf(debit.FailureCode);
            }
            collections.Add(debit);

            // Some failures are recovered a few days later, by Pay Now or an EFT.
            var recoverOn = e.DueDate.AddDays(rnd.Next(2, 12));
            if (fails && !writtenOff && rnd.NextDouble() < 0.45 && recoverOn < today)
            {
                var byPayNow = rnd.NextDouble() < 0.5;
                var received = Sast.StartUtc(recoverOn).AddHours(11);
                var payment = new LoanCollection
                {
                    TenantId = loan.TenantId,
                    LoanId = loan.Id,
                    Channel = byPayNow ? CollectionChannel.PayNow : CollectionChannel.Manual,
                    ActionDate = recoverOn,
                    Amount = e.InstalmentTotal,
                    Status = CollectionStatus.Successful,
                    ReceivedUtc = received,
                    CreatedUtc = received,
                    ExternalReference = byPayNow ? $"DEMO-PNR-{loan.LoanNumber}-{e.InstalmentNumber}" : "EFT"
                };
                if (byPayNow)
                {
                    var tx = new PayNowTransaction
                    {
                        TenantId = loan.TenantId, ClientId = loan.ClientId, PaymentReference = payment.ExternalReference!,
                        Amount = payment.Amount, Status = PaymentStatus.Complete, CreatedUtc = received.AddMinutes(-5), CompletedUtc = received
                    };
                    db.PayNowTransactions.Add(tx);
                    payment.PayNowTransactionId = tx.Id;
                }
                collections.Add(payment);
            }
        }

        LoanLedger.Allocate(schedule, collections);
        db.LoanCollections.AddRange(collections);

        if (writtenOff)
        {
            loan.WrittenOffUtc = Sast.StartUtc(schedule.Max(s => s.DueDate).AddDays(rnd.Next(20, 60)));
            if (loan.WrittenOffUtc > DateTime.UtcNow) loan.WrittenOffUtc = DateTime.UtcNow.AddDays(-1);
            loan.WrittenOffAmount = LoanLedger.Outstanding(schedule);
        }
    }

    private static Client NewClient(Guid tenantId, int number, Random rnd, DateTime utcNow, bool onboarded = true)
    {
        var dob = new DateTime(1960, 1, 1).AddDays(rnd.Next(0, 365 * 40));
        var id = FictionalSaId(dob, rnd);
        var client = new Client
        {
            TenantId = tenantId,
            ClientNumber = number.ToString("D4"),
            FirstName = FirstNames[rnd.Next(FirstNames.Length)],
            Surname = Surnames[rnd.Next(Surnames.Length)],
            IdNumber = id,
            IsSaIdNumber = true,
            DateOfBirth = dob,
            Status = onboarded ? ClientStatus.Active : ClientStatus.PendingVerification,
            CreatedUtc = utcNow.AddDays(-rnd.Next(5, 200)),
            Financial = new ClientFinancial
            {
                GrossMonthlyIncome = rnd.Next(9, 40) * 1_000m,
                NetMonthlyIncome = rnd.Next(7, 30) * 1_000m,
                TotalMonthlyExpenses = rnd.Next(3, 9) * 1_000m,
                TotalMonthlyDebtRepayments = rnd.Next(0, 3) * 500m
            },
            Employment = new ClientEmployment { EmployerName = "Demo Employer", SalaryDay = 25 }
        };
        client.Addresses.Add(new ClientAddress { AddressType = AddressType.Physical, Line1 = $"{rnd.Next(1, 200)} Demo Street", City = "Johannesburg", MobileNumber = $"082 000 {rnd.Next(0, 10000):D4}" });

        var review = onboarded ? DocumentReviewStatus.Approved : DocumentReviewStatus.Pending;
        foreach (var type in new[] { DocumentType.IdDocument, DocumentType.Payslip })
            client.Documents.Add(new ClientDocument { DocumentType = type, FileName = $"demo-{type}.pdf", StoragePath = "demo/not-stored", SizeBytes = 1, ReviewStatus = review });

        return client;
    }

    /// <summary>A valid-looking but fictional SA ID number (date of birth, sequence, check digit).</summary>
    private static string FictionalSaId(DateTime dob, Random rnd)
    {
        var body = $"{dob:yyMMdd}{rnd.Next(0, 10000):D4}08";
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var d = body[body.Length - 1 - i] - '0';
            if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return body + ((10 - sum % 10) % 10);
    }

    private static AffordabilityAssessment Assessment(Loan loan, decimal instalment) => new()
    {
        LoanId = loan.Id,
        AssessedUtc = loan.CreatedUtc,
        GrossMonthlyIncome = 18_000m,
        NetMonthlyIncome = 14_000m,
        DeclaredMonthlyExpenses = 6_000m,
        StatutoryMinimumExpenses = 2_225m,
        ProposedInstalment = instalment,
        DiscretionaryIncome = 8_000m,
        SurplusAfterInstalment = 8_000m - instalment,
        UtilisationRatio = instalment / 8_000m,
        Outcome = AffordabilityOutcome.Pass,
        Reasoning = "Demo data"
    };
}
