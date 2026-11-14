using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// The screening half of onboarding: the good-to-go flag and the checks that feed it (Home
/// Affairs, employment, the bank statement), the client's contact history, and the outcome report.
/// </summary>
public partial class ClientsController
{
    /// <summary>Largest statement sent for assessment. The model's own limit is higher; this keeps a request quick.</summary>
    private const int MaxStatementBytes = 15 * 1024 * 1024;

    // ------------------------------------------------------------- good to go

    [HttpGet]
    public async Task<IActionResult> GoodToGo(Guid id, [FromServices] IBankStatementAnalyst analyst)
    {
        var client = await LoadClientAsync(id, c => c.Employment, c => c.Documents, c => c.Financial);
        if (client is null) return NotFound();

        // Opening the page is the recheck: whatever changed on the file since last time shows now.
        var screening = await _screening.EvaluateAsync(id, ScreeningTrigger.DetailsChanged, User.Identity?.Name);
        if (screening is null) return NotFound();

        var verifications = await _db.ClientVerifications.AsNoTracking()
            .Where(v => v.ClientId == id).OrderByDescending(v => v.CheckedUtc).ToListAsync();

        var employer = client.Employment?.EmployerId is { } employerId
            ? await _db.Employers.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employerId)
            : null;

        var assessments = await _db.BankStatementAssessments.AsNoTracking()
            .Where(a => a.ClientId == id).OrderByDescending(a => a.CreatedUtc).ToListAsync();

        ViewData["Title"] = "Good to go";
        await PopulateWizardAsync(id, OnboardingStep.GoodToGo);

        return View(new GoodToGoPageModel
        {
            Client = client,
            Screening = screening,
            Checks = GoodToGoResult.ParseChecks(screening.Checks),
            History = await _db.ClientScreenings.AsNoTracking()
                .Where(s => s.ClientId == id).OrderByDescending(s => s.CheckedUtc).Take(8).ToListAsync(),
            Life = verifications.FirstOrDefault(v => v.Kind == VerificationKind.Life),
            Employment = verifications.FirstOrDefault(v => v.Kind == VerificationKind.Employment),
            EmployerTelephone = employer?.Telephone ?? client.Employment?.WorkTelephone,
            EmployerContact = employer?.ContactPerson ?? client.Employment?.SupervisorName,
            Statements = client.Documents.Where(d => d.DocumentType == DocumentType.BankStatement)
                .OrderByDescending(d => d.UploadedUtc).ToList(),
            Assessments = assessments.GroupBy(a => a.DocumentId).ToDictionary(g => g.Key, g => g.First()),
            StatementReview = await _db.StatementReviews.AsNoTracking()
                .Where(r => r.ClientId == id).OrderByDescending(r => r.CreatedUtc).FirstOrDefaultAsync(),
            DeclaredNetIncome = client.Financial?.NetMonthlyIncome,
            DeclaredDebtInstalments = client.Financial?.TotalMonthlyDebtRepayments ?? 0m,
            AnalystLive = analyst.IsLive,
            AffordabilityCaptured = await _screening.AffordabilityCapturedAsync(id),
            CanOverride = User.IsInRole(AppRoles.TenantAdmin) || User.IsInRole(AppRoles.SuperAdmin),
            RecheckDays = await _db.Tenants.Where(t => t.Id == client.TenantId).Select(t => t.ScreeningRecheckDays).FirstAsync()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Recheck(Guid id)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        var before = (await _screening.LatestAsync(id))?.Outcome;
        var after = await _screening.EvaluateAsync(id, ScreeningTrigger.Manual, User.Identity?.Name);

        TempData["Info"] = before == after?.Outcome
            ? $"Rechecked. Still {Domain.Credit.GoodToGo.Label(after!.Outcome).ToLowerInvariant()}."
            : $"Rechecked. Now {Domain.Credit.GoodToGo.Label(after!.Outcome).ToLowerInvariant()}.";
        return RedirectToAction(nameof(GoodToGo), new { id });
    }

    /// <summary>
    /// Asks Home Affairs whether the ID number belongs to a living person. Needs the client's
    /// POPIA consent on file first: it sends their ID number to a third party.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyLife(Guid id, [FromServices] ILifeStatusVerifier verifier)
    {
        var client = await LoadClientAsync(id, c => c.OtherDetails);
        if (client is null) return NotFound();

        if (client.OtherDetails?.DataProcessingConsent != true)
        {
            TempData["Error"] = "Record the client's POPIA consent on Other details before checking with Home Affairs.";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }

        var answer = await verifier.VerifyAsync(client.IdNumber, client.Surname);
        _db.ClientVerifications.Add(new ClientVerification
        {
            ClientId = id,
            Kind = VerificationKind.Life,
            Result = answer.Result,
            Method = answer.Method,
            Reference = answer.Reference,
            Notes = answer.Detail,
            Simulated = answer.Simulated,
            CheckedBy = User.Identity?.Name
        });
        await _db.SaveChangesAsync();

        TempData[answer.Result == VerificationResult.Confirmed ? "Success" : "Error"] = answer.Result switch
        {
            VerificationResult.Confirmed => answer.Simulated
                ? "Home Affairs check recorded (simulated - no provider is connected yet)."
                : "Home Affairs confirms the client is alive.",
            VerificationResult.Failed => "Home Affairs records this ID number as deceased. Do not go further.",
            _ => answer.Detail ?? "Home Affairs could not be checked just now."
        };
        return RedirectToAction(nameof(GoodToGo), new { id });
    }

    /// <summary>Records that someone confirmed (or could not confirm) the client's employment, and how.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyEmployment(Guid id, VerificationResult result, string? method, string? reference, string? notes)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        if (!GoodToGoPageModel.EmploymentMethods.Contains(method) || !Enum.IsDefined(result))
        {
            TempData["Error"] = "Choose how the employment was checked.";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }
        if (result == VerificationResult.Confirmed && string.IsNullOrWhiteSpace(reference))
        {
            TempData["Error"] = "Say who confirmed it, or which document shows it.";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }

        _db.ClientVerifications.Add(new ClientVerification
        {
            ClientId = id,
            Kind = VerificationKind.Employment,
            Result = result,
            Method = method!,
            Reference = Truncate(reference, 200),
            Notes = Truncate(notes, 1000),
            CheckedBy = User.Identity?.Name
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Employment verification recorded.";
        return RedirectToAction(nameof(GoodToGo), new { id });
    }

    /// <summary>
    /// Lets a "Review" client through to the credit check. An administrator's call, with the reason
    /// kept. A "Not good to go" cannot be overridden: fix what failed, or do not lend.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.TenantAdministration)]
    public async Task<IActionResult> OverrideScreening(Guid id, string? reason)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        var screening = await _screening.EvaluateAsync(id, ScreeningTrigger.DetailsChanged, User.Identity?.Name);
        if (screening?.Outcome != ScreeningOutcome.Review)
        {
            TempData["Error"] = "Only a client flagged Review can be let through.";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            TempData["Error"] = "Give the reason for letting this client through (at least a short sentence).";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }

        screening.OverrideReason = Truncate(reason.Trim(), 500);
        screening.OverriddenBy = User.Identity?.Name;
        screening.OverriddenUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _log.LogInformation("Good-to-go review overridden for client {ClientNumber}.", client.ClientNumber);

        TempData["Success"] = "Recorded. The credit check can now be run.";
        return RedirectToAction(nameof(GoodToGo), new { id });
    }

    /// <summary>
    /// Runs the bank-statement model on the figures a staff member read off the statement: it
    /// validates the declared income and debt against them and flags the statement Good, Warning
    /// or Bad. Rules, no AI, nothing leaves the system.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckStatement(Guid id, StatementFiguresModel model)
    {
        var client = await LoadClientAsync(id, c => c.Financial, c => c.Documents);
        if (client is null) return NotFound();

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Check the statement figures: " +
                string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Distinct());
            return RedirectToAction(nameof(GoodToGo), new { id });
        }
        if (model.SalaryDeposits > 0 && model.AverageSalaryDeposit <= 0)
        {
            TempData["Error"] = "Enter the average salary deposit, or 0 salary deposits if there are none.";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }

        var figures = new StatementFigures
        {
            MonthsCovered = model.MonthsCovered,
            SalaryDeposits = model.SalaryDeposits,
            AverageSalaryDeposit = model.AverageSalaryDeposit,
            ReturnedDebitOrders = model.ReturnedDebitOrders,
            OtherLenders = model.OtherLenders,
            MonthlyLoanRepayments = model.MonthlyLoanRepayments,
            MonthlyGambling = model.MonthlyGambling,
            DaysOverdrawn = model.DaysOverdrawn
        };
        var declaredIncome = client.Financial?.NetMonthlyIncome;
        var declaredDebt = client.Financial?.TotalMonthlyDebtRepayments ?? 0m;
        var verdict = StatementModel.Evaluate(figures, declaredIncome, declaredDebt);

        _db.StatementReviews.Add(new StatementReview
        {
            ClientId = id,
            DocumentId = client.Documents.Any(d => d.Id == model.DocumentId && d.DocumentType == DocumentType.BankStatement)
                ? model.DocumentId : null,
            MonthsCovered = figures.MonthsCovered,
            SalaryDeposits = figures.SalaryDeposits,
            AverageSalaryDeposit = figures.AverageSalaryDeposit,
            ReturnedDebitOrders = figures.ReturnedDebitOrders,
            OtherLenders = figures.OtherLenders,
            MonthlyLoanRepayments = figures.MonthlyLoanRepayments,
            MonthlyGambling = figures.MonthlyGambling,
            DaysOverdrawn = figures.DaysOverdrawn,
            DeclaredNetIncome = declaredIncome,
            DeclaredDebtInstalments = declaredDebt,
            Flag = verdict.Flag,
            Summary = verdict.Summary,
            Findings = Truncate(verdict.SerialisedFindings, 4000) ?? "",
            CreatedBy = User.Identity?.Name
        });
        await _db.SaveChangesAsync();

        TempData[verdict.Flag == StatementFlag.Bad ? "Error" : verdict.Flag == StatementFlag.Warning ? "Info" : "Success"] =
            $"Bank statement: {verdict.Flag}. {verdict.Summary}";
        return RedirectToAction(nameof(GoodToGo), new { id });
    }

    /// <summary>
    /// Sends one of the client's bank statements for the AI assessment. The statement leaves South
    /// Africa for the model provider, so the operator must record the client's consent to that
    /// (POPIA s72) each time.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssessStatement(
        Guid id, Guid documentId, bool consent, [FromServices] IBankStatementAnalyst analyst)
    {
        var client = await LoadClientAsync(id, c => c.Documents, c => c.Financial);
        var document = client?.Documents.FirstOrDefault(d => d.Id == documentId && d.DocumentType == DocumentType.BankStatement);
        if (client is null || document is null) return NotFound();

        if (!consent)
        {
            TempData["Error"] = "Record that the client agreed to their statement being assessed before sending it.";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }
        if (document.SizeBytes > MaxStatementBytes)
        {
            TempData["Error"] = "That statement is over 15 MB. Upload a smaller file (three months is enough).";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }

        await using var stream = await _store.OpenAsync(document.StoragePath);
        if (stream is null) return NotFound();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        var contentType = Path.GetExtension(document.FileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => document.ContentType ?? ""
        };

        var (reading, error) = await analyst.AnalyseAsync(new BankStatementInput(
            buffer.ToArray(), contentType, client.FullName, client.Financial?.NetMonthlyIncome));

        if (reading is null)
        {
            TempData["Error"] = error ?? "The statement could not be assessed.";
            return RedirectToAction(nameof(GoodToGo), new { id });
        }

        _db.BankStatementAssessments.Add(new BankStatementAssessment
        {
            ClientId = id,
            DocumentId = document.Id,
            Flag = reading.Flag,
            Summary = Truncate(reading.Summary, 2000) ?? "",
            Observations = Truncate(string.Join('\n', reading.Observations), 4000),
            AverageMonthlyIncome = reading.AverageMonthlyIncome,
            AverageMonthlySpend = reading.AverageMonthlySpend,
            Model = reading.Model,
            Simulated = reading.Simulated,
            ConsentRecordedUtc = DateTime.UtcNow,
            CreatedBy = User.Identity?.Name
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = reading.Simulated
            ? "Assessment recorded (simulated - no AI model is connected yet)."
            : $"Statement assessed: {reading.Flag}.";
        return RedirectToAction(nameof(GoodToGo), new { id });
    }

    // ------------------------------------------------------------- notes (contact history)

    [HttpGet]
    public async Task<IActionResult> Notes(Guid id)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        ViewData["Title"] = "Notes";
        await PopulateWizardAsync(id, OnboardingStep.Notes);

        return View(new ClientNotesModel
        {
            ClientId = id,
            Notes = await _db.ClientNotes.AsNoTracking()
                .Where(n => n.ClientId == id).OrderByDescending(n => n.CreatedUtc).ToListAsync()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(Guid id, string? channel, string? body, DateTime? followUpOn)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Write what was said or done.";
            return RedirectToAction(nameof(Notes), new { id });
        }

        _db.ClientNotes.Add(new ClientNote
        {
            ClientId = id,
            Category = ContactChannels.All.Contains(channel) ? channel : "Note",
            Body = Truncate(body.Trim(), 4000)!,
            CreatedByUserId = User.Identity?.Name,
            FollowUpOn = followUpOn?.Date
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = followUpOn is null ? "Note saved." : $"Note saved. Follow up on {followUpOn:d MMM yyyy}.";
        return RedirectToAction(nameof(Notes), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteFollowUp(Guid id, Guid noteId)
    {
        var client = await LoadClientAsync(id);
        var note = client is null ? null : await _db.ClientNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.ClientId == id);
        if (note is null) return NotFound();

        note.FollowUpDoneUtc ??= DateTime.UtcNow;
        note.FollowUpDoneBy ??= User.Identity?.Name;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Follow-up marked done.";
        return RedirectToAction(nameof(Notes), new { id });
    }

    // ------------------------------------------------------------- report for the client

    /// <summary>
    /// The outcome, on one printable page, in words for the client: where their application
    /// stands, why, and what is still needed. Staff print it or save it as a PDF and hand it over.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Report(Guid id)
    {
        var client = await LoadClientAsync(id, c => c.Financial, c => c.CreditEnquiries);
        if (client is null) return NotFound();

        var screening = await _screening.EvaluateAsync(id, ScreeningTrigger.DetailsChanged, User.Identity?.Name);
        if (screening is null) return NotFound();

        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == client.TenantId);
        var checks = GoodToGoResult.ParseChecks(screening.Checks);
        var f = client.Financial;
        var enquiry = client.CreditEnquiries
            .Where(e => e.Status == CreditEnquiryStatus.ReportReceived)
            .OrderByDescending(e => e.EnquiryDate).FirstOrDefault();

        ViewData["Title"] = "Application outcome";
        ViewData["Lender"] = tenant.TradingName ?? tenant.Name;

        return View(new ClientReportModel
        {
            Client = client,
            Lender = tenant.TradingName ?? tenant.Name,
            LenderNcr = tenant.NcrNumber,
            LenderContact = tenant.ContactNumber ?? tenant.ContactEmail,
            Screening = screening,
            Reasons = checks.Where(c => c.Status == CheckStatus.Fail)
                .Select(ClientReportModel.Wording).OfType<string>().ToList(),
            StillNeeded = checks.Where(c => c.Status == CheckStatus.Missing)
                .Select(ClientReportModel.Wording).OfType<string>().ToList(),
            NetOfNet = f is null ? null
                : NetOfNetFor(f.GrossMonthlyIncome, f.NetMonthlyIncome, f.OtherIncome,
                    f.TotalMonthlyExpenses ?? 0m, f.TotalMonthlyDebtRepayments ?? 0m),
            Bureau = enquiry?.Bureau,
            BureauCheckedUtc = enquiry?.ReportReceivedUtc
        });
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
}
