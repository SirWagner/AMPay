using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Validation;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AMPay.Infrastructure.Credit;

/// <summary>
/// Runs the good-to-go check for a client from what is on file and keeps the history.
/// <para>
/// The check is cheap - a handful of reads, no outside call - so it is simply run again wherever
/// the flag is shown. A new history row is written only when the answer changes; an unchanged
/// answer just moves the "last checked" time on. That is the automatic recheck: nobody has to
/// remember to press a button after editing a client, and the daily job catches the facts that
/// move by themselves (a contract ends, an instalment goes unpaid).
/// </para>
/// </summary>
public class ScreeningService
{
    private readonly AppDbContext _db;
    private readonly IAffordabilityService _affordability;
    private readonly ILogger<ScreeningService> _log;

    public ScreeningService(AppDbContext db, IAffordabilityService affordability, ILogger<ScreeningService> log)
    {
        _db = db;
        _affordability = affordability;
        _log = log;
    }

    public Task<ClientScreening?> LatestAsync(Guid clientId, CancellationToken ct = default) =>
        _db.ClientScreenings.Where(s => s.ClientId == clientId)
            .OrderByDescending(s => s.CheckedUtc).FirstOrDefaultAsync(ct);

    /// <summary>
    /// True when income and a budget are captured: the affordability assessment has something to
    /// work on. Supporting documents are asked for only after this.
    /// </summary>
    public async Task<bool> AffordabilityCapturedAsync(Guid clientId, CancellationToken ct = default) =>
        await _db.ClientFinancials.AnyAsync(f => f.ClientId == clientId && f.GrossMonthlyIncome > 0 && f.NetMonthlyIncome > 0, ct)
        && await _db.ClientBudgets.AnyAsync(b => b.ClientId == clientId && b.Amount > 0 && b.Kind != BudgetLineKind.PayslipDeduction, ct);

    /// <summary>Everything the check reads, gathered from the client's file.</summary>
    public async Task<GoodToGoInput?> BuildInputAsync(Guid clientId, DateTime todayUtc, CancellationToken ct = default)
    {
        var c = await _db.Clients.AsNoTracking()
            .Include(x => x.Employment)
            .Include(x => x.Financial)
            .Include(x => x.OtherDetails)
            .Include(x => x.BankAccounts)
            .Include(x => x.Addresses)
            .FirstOrDefaultAsync(x => x.Id == clientId, ct);
        if (c is null) return null;

        var today = todayUtc.Date;

        var verifications = await _db.ClientVerifications.AsNoTracking()
            .Where(v => v.ClientId == clientId)
            .OrderByDescending(v => v.CheckedUtc)
            .Select(v => new { v.Kind, v.Result, v.Simulated })
            .ToListAsync(ct);

        // The rules model on the figures staff read off the statement comes first; an AI
        // assessment counts only where there is none. A simulated assessment read nothing, so it
        // must not move the flag either way.
        var statementFlag = await _db.StatementReviews.AsNoTracking()
            .Where(r => r.ClientId == clientId)
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r => (StatementFlag?)r.Flag)
            .FirstOrDefaultAsync(ct)
            ?? await _db.BankStatementAssessments.AsNoTracking()
                .Where(a => a.ClientId == clientId && !a.Simulated)
                .OrderByDescending(a => a.CreatedUtc)
                .Select(a => (StatementFlag?)a.Flag)
                .FirstOrDefaultAsync(ct);

        var loans = await _db.Loans.AsNoTracking()
            .Where(l => l.ClientId == clientId)
            .Select(l => new { l.Id, l.Status })
            .ToListAsync(ct);

        // Only loans that were actually paid out are history; a draft or a declined application is not.
        var paidOut = loans.Where(l => l.Status is LoanStatus.Disbursed or LoanStatus.Settled or LoanStatus.WrittenOff).ToList();
        var running = paidOut.Where(l => l.Status == LoanStatus.Disbursed).Select(l => l.Id).ToList();

        var oldestUnpaid = running.Count == 0
            ? null
            : await _db.LoanScheduleEntries.AsNoTracking()
                .Where(e => running.Contains(e.LoanId) && e.DueDate < today && e.PaidAmount < e.InstalmentTotal)
                .OrderBy(e => e.DueDate)
                .Select(e => (DateTime?)e.DueDate)
                .FirstOrDefaultAsync(ct);

        var f = c.Financial;
        var budgetCaptured = await _db.ClientBudgets.AnyAsync(b => b.ClientId == clientId && b.Amount > 0 && b.Kind != BudgetLineKind.PayslipDeduction, ct);

        decimal? netOfNet = null;
        if (f is { GrossMonthlyIncome: > 0, NetMonthlyIncome: > 0 })
        {
            netOfNet = _affordability.Assess(new AffordabilityInput(
                GrossMonthlyIncome: f.GrossMonthlyIncome.Value,
                NetMonthlyIncome: f.NetMonthlyIncome.Value,
                DeclaredMonthlyExpenses: f.TotalMonthlyExpenses ?? 0m,
                ExistingDebtRepayments: f.TotalMonthlyDebtRepayments ?? 0m,
                ProposedInstalment: 0m,
                OtherMonthlyIncome: f.OtherIncome ?? 0m)).DiscretionaryIncome;
        }

        var dob = c.DateOfBirth;
        if (dob is null && c.IsSaIdNumber && SaIdNumber.TryParseDateOfBirth(c.IdNumber, out var parsed)) dob = parsed;

        return new GoodToGoInput
        {
            IsSaIdNumber = c.IsSaIdNumber,
            IdNumberValid = c.IsSaIdNumber && SaIdNumber.IsValid(c.IdNumber),
            AgeYears = dob is null ? null : GoodToGo.AgeOn(dob.Value, today),
            PopiaConsent = c.OtherDetails?.DataProcessingConsent == true,
            HasMobileNumber = c.Addresses.Any(a => !string.IsNullOrWhiteSpace(a.MobileNumber)),
            EmploymentType = c.Employment?.EmploymentType,
            EmployedSince = c.Employment?.EmployedSince,
            ContractExpiryDate = c.Employment?.ContractExpiryDate,
            NetMonthlyIncome = f?.NetMonthlyIncome,
            BudgetCaptured = budgetCaptured,
            NetOfNet = netOfNet,
            HasBankAccount = c.BankAccounts.Count > 0,
            BankAccountVerified = c.BankAccounts.Any(a => a.AvsVerified),
            LifeVerification = verifications.FirstOrDefault(v => v.Kind == VerificationKind.Life)?.Result,
            LifeVerificationSimulated = verifications.FirstOrDefault(v => v.Kind == VerificationKind.Life)?.Simulated == true,
            EmploymentVerification = verifications.FirstOrDefault(v => v.Kind == VerificationKind.Employment)?.Result,
            StatementFlag = statementFlag,
            PreviousLoans = paidOut.Count,
            ActiveLoans = running.Count,
            WrittenOffLoans = paidOut.Count(l => l.Status == LoanStatus.WrittenOff),
            DaysInArrears = oldestUnpaid is null ? 0 : (today - oldestUnpaid.Value.Date).Days,
            Today = today
        };
    }

    /// <summary>
    /// Runs the check and returns the current result. Writes a new history row when the answer
    /// differs from the last one (an override does not carry over: it was given for the old facts);
    /// otherwise only the last-checked time moves.
    /// </summary>
    public async Task<ClientScreening?> EvaluateAsync(
        Guid clientId, ScreeningTrigger trigger, string? checkedBy, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var input = await BuildInputAsync(clientId, now, ct);
        if (input is null) return null;

        var result = GoodToGo.Evaluate(input);
        var checks = result.SerialisedChecks;
        var latest = await LatestAsync(clientId, ct);

        if (latest is not null && latest.Outcome == result.Outcome && latest.Score == result.Score && latest.Checks == checks)
        {
            latest.CheckedUtc = now;
            await _db.SaveChangesAsync(ct);
            return latest;
        }

        var screening = new ClientScreening
        {
            ClientId = clientId,
            Outcome = result.Outcome,
            Score = result.Score,
            IsFirstTimeApplicant = result.IsFirstTimeApplicant,
            Checks = checks,
            Trigger = trigger,
            CheckedUtc = now,
            CheckedBy = checkedBy
        };
        _db.ClientScreenings.Add(screening);

        // A flag that gets worse by itself is exactly what someone needs to hear about: put it on
        // the follow-up list rather than leave it to be noticed.
        if (trigger == ScreeningTrigger.Scheduled && latest is not null && result.Outcome > latest.Outcome)
        {
            _db.ClientNotes.Add(new ClientNote
            {
                ClientId = clientId,
                Category = "Note",
                Body = $"Automatic recheck: good-to-go changed from {GoodToGo.Label(latest.Outcome)} to {GoodToGo.Label(result.Outcome)}. " +
                       string.Join(" ", result.Checks.Where(x => x.Status == CheckStatus.Fail).Select(x => x.Detail)),
                CreatedUtc = now,
                CreatedByUserId = "Automatic recheck",
                FollowUpOn = now.Date
            });
        }

        await _db.SaveChangesAsync(ct);
        return screening;
    }

    /// <summary>
    /// Rechecks every client being onboarded or lent to whose last result is older than their
    /// lender's recheck period. One lender, or all of them. Returns how many were rechecked.
    /// </summary>
    public async Task<int> RecheckStaleAsync(Guid? tenantId, DateTime nowUtc, int max = 500, CancellationToken ct = default)
    {
        var tenants = await _db.Tenants.AsNoTracking()
            .Where(t => tenantId == null || t.Id == tenantId)
            .Select(t => new { t.Id, t.ScreeningRecheckDays })
            .ToListAsync(ct);

        var rechecked = 0;
        foreach (var t in tenants)
        {
            if (t.ScreeningRecheckDays <= 0) continue; // 0 switches the automatic recheck off
            if (rechecked >= max) break;

            var cutoff = nowUtc.AddDays(-t.ScreeningRecheckDays);

            // Only clients who have been screened before: a first screening belongs to the capture.
            var due = await _db.Clients.AsNoTracking()
                .Where(c => c.TenantId == t.Id
                            && c.Status != ClientStatus.Closed && c.Status != ClientStatus.Declined
                            && c.Screenings.Any()
                            && !c.Screenings.Any(s => s.CheckedUtc > cutoff))
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .Take(max - rechecked)
                .ToListAsync(ct);

            foreach (var id in due)
            {
                await EvaluateAsync(id, ScreeningTrigger.Scheduled, "Automatic recheck", nowUtc, ct);
                rechecked++;
            }
        }

        if (rechecked > 0) _log.LogInformation("Good-to-go rechecked for {Count} client(s).", rechecked);
        return rechecked;
    }
}

/// <summary>
/// The lender's employer list: finds the employer a capture names, or adds it, so the next
/// client at the same company picks it instead of typing it.
/// </summary>
public static class EmployerDirectory
{
    /// <summary>
    /// The employer picked from the list; else one with the same name (and pay point) already on
    /// the list; else a new entry. Null when no employer was named at all.
    /// </summary>
    public static async Task<Employer?> ResolveAsync(
        AppDbContext db, Guid tenantId, Guid? employerId, string? name, string? payPoint, CancellationToken ct = default)
    {
        if (employerId is { } id)
        {
            var picked = await db.Employers.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
            if (picked is not null) return picked;
        }

        name = Clean(name);
        if (name is null) return null;
        payPoint = Clean(payPoint);

        // SQL Server compares case-insensitively by default; ToLower keeps the in-memory tests honest.
        var lower = name.ToLower();
        var sameName = await db.Employers
            .Where(e => e.TenantId == tenantId && e.Name.ToLower() == lower)
            .ToListAsync(ct);
        var existing = sameName.FirstOrDefault(e => string.Equals(Clean(e.PayPoint), payPoint, StringComparison.OrdinalIgnoreCase))
                       ?? (payPoint is null ? sameName.FirstOrDefault() : null);
        if (existing is not null) return existing;

        var created = new Employer { TenantId = tenantId, Name = name, PayPoint = payPoint };
        db.Employers.Add(created);
        return created;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
