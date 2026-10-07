using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Contracts;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Credit origination: quoting, submitting and deciding loans.
/// <para>
/// A loan is raised only against an onboarded client - one whose identity document and
/// payslip a reviewer has accepted. It is never part of capture. The operator supplies the
/// amount, the number of instalments and the package; the pricing engine produces every
/// other figure, and the affordability assessment is run and recorded before anyone can
/// approve it.
/// </para>
/// <para>
/// Capturers quote and submit. Only an administrator approves, declines or disburses.
/// </para>
/// </summary>
[Authorize]
public class LoansController : Controller
{
    private const string ReadRoles =
        AppRoles.SuperAdmin + "," + AppRoles.TenantAdmin + "," + AppRoles.Capturer + "," + AppRoles.Viewer;

    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly ILoanPricingService _pricing;
    private readonly IAffordabilityService _affordability;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ContractService _contracts;
    private readonly ILogger<LoansController> _log;

    public LoansController(
        AppDbContext db,
        ICurrentTenant tenant,
        ILoanPricingService pricing,
        IAffordabilityService affordability,
        UserManager<ApplicationUser> users,
        ContractService contracts,
        ILogger<LoansController> log)
    {
        _db = db;
        _tenant = tenant;
        _pricing = pricing;
        _affordability = affordability;
        _users = users;
        _contracts = contracts;
        _log = log;
    }

    // ---------------------------------------------------------------- the loan book

    [Authorize(Roles = ReadRoles)]
    public async Task<IActionResult> Index(LoanStatus? status)
    {
        ViewData["Title"] = "Loans";
        await _tenant.LoadAsync();

        var query = _db.Loans.AsNoTracking().AsQueryable();

        if (_tenant.TenantId is not null)
            query = query.Where(l => l.TenantId == _tenant.TenantId);
        else if (!_tenant.IsPlatformUser)
            return Forbid();

        if (status is not null) query = query.Where(l => l.Status == status);
        ViewBag.Status = status;

        var rows = await query
            .OrderByDescending(l => l.CreatedUtc)
            .Take(500)
            .Select(l => new LoanRow
            {
                Id = l.Id,
                LoanNumber = l.LoanNumber,
                ClientId = l.ClientId,
                ClientName = l.Client!.FirstName + " " + l.Client.Surname,
                ClientNumber = l.Client.ClientNumber,
                PackageName = l.CreditPackage!.Name,
                Principal = l.Principal,
                FirstInstalment = l.FirstInstalment,
                NumberOfInstalments = l.NumberOfInstalments,
                Status = l.Status,
                Affordability = l.Assessments
                    .OrderByDescending(a => a.AssessedUtc)
                    .Select(a => (AffordabilityOutcome?)a.Outcome)
                    .FirstOrDefault(),
                CreatedUtc = l.CreatedUtc
            })
            .ToListAsync();

        return View(rows);
    }

    // ---------------------------------------------------------------- raising a loan

    [HttpGet]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Create(Guid clientId)
    {
        ViewData["Title"] = "Raise a loan";

        var client = await LoadClientAsync(clientId);
        if (client is null) return NotFound();

        var blockers = ReasonsCannotBorrow(client);
        if (blockers.Count > 0)
            return RefuseCannotBorrow(client, blockers);

        var packages = await PackagesForAsync(client.TenantId);
        if (packages.Count == 0)
        {
            TempData["Error"] =
                "No credit package is available for this customer, so no loan can be priced. " +
                "The AM-Pay platform administrator sets pricing under Administration → Credit packages.";
            return RedirectToAction("Details", "Clients", new { id = clientId });
        }

        var model = new LoanCreateModel
        {
            ClientId = client.Id,
            ClientName = client.FullName,
            ClientNumber = client.ClientNumber,
            Packages = packages,
            HasFinancials = HasIncome(client.Financial),
            CreditPackageId = packages[0].Id,
            FirstCollectionDate = LoanOrigination.DefaultFirstCollectionDate(
                client.Employment?.SalaryDay, DateTime.Today)
        };

        return View(model);
    }

    /// <summary>
    /// Prices a loan without saving anything. Called as the operator types, so that the
    /// instalment and total cost are in front of them before they commit to a figure.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Quote(
        Guid clientId, Guid packageId, decimal principal, int instalments, DateTime? firstCollection)
    {
        var client = await LoadClientAsync(clientId);
        if (client is null) return NotFound();

        var package = await _db.CreditPackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == packageId && p.TenantId == client.TenantId && p.IsActive);

        if (package is null)
            return Json(new LoanQuoteView { Ok = false, Error = "Choose a credit package." });

        var first = firstCollection ?? LoanOrigination.DefaultFirstCollectionDate(
            client.Employment?.SalaryDay, DateTime.Today);

        var (quote, error) = TryQuote(package, principal, instalments, first);
        if (quote is null)
            return Json(new LoanQuoteView { Ok = false, Error = error });

        var view = ToView(quote);

        var input = LoanOrigination.AffordabilityInputFor(
            client.Financial, quote.FirstInstalment, await OwnInstalmentsAsync(client.Id, excludingLoanId: null));
        if (input is not null)
        {
            var result = _affordability.Assess(input);
            view.Affordability = new LoanQuoteView.AffordabilityView
            {
                Outcome = result.Outcome.ToString(),
                Reasoning = result.Reasoning,
                DiscretionaryIncome = result.DiscretionaryIncome,
                SurplusAfterInstalment = result.SurplusAfterInstalment,
                UtilisationRatio = result.UtilisationRatio,
                MaximumAffordableInstalment = result.MaximumAffordableInstalment
            };
        }

        return Json(view);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Create(LoanCreateModel model)
    {
        ViewData["Title"] = "Raise a loan";

        var client = await LoadClientAsync(model.ClientId);
        if (client is null) return NotFound();

        // Re-checked on submit: a document could have been rejected while the form was open.
        var blockers = ReasonsCannotBorrow(client);
        if (blockers.Count > 0)
            return RefuseCannotBorrow(client, blockers);

        var package = model.CreditPackageId is null
            ? null
            : await _db.CreditPackages.FirstOrDefaultAsync(p =>
                p.Id == model.CreditPackageId && p.TenantId == client.TenantId && p.IsActive);

        if (package is null)
            ModelState.AddModelError(nameof(model.CreditPackageId), "Choose a credit package.");

        LoanQuote? quote = null;
        if (ModelState.IsValid && package is not null)
        {
            var (q, error) = TryQuote(package, model.Principal!.Value,
                model.NumberOfInstalments!.Value, model.FirstCollectionDate!.Value);

            quote = q;
            if (quote is null) ModelState.AddModelError(string.Empty, error!);
        }

        // Affordability is not optional. s81 of the NCA makes an advance reckless if the
        // assessment was not done, so a loan with no income on file cannot even be submitted.
        var input = quote is null
            ? null
            : LoanOrigination.AffordabilityInputFor(
                client.Financial, quote.FirstInstalment, await OwnInstalmentsAsync(client.Id, excludingLoanId: null));

        var result = input is null ? null : _affordability.Assess(input);

        if (quote is not null && (result is null || result.Outcome == AffordabilityOutcome.Insufficient))
            ModelState.AddModelError(string.Empty,
                "Affordability cannot be assessed: this client has no income on file. " +
                "Capture gross and net monthly income on the Financial step first.");

        if (!ModelState.IsValid)
        {
            await RepopulateAsync(model, client);
            return View(model);
        }

        var userId = _users.GetUserId(User);

        var loan = new Loan
        {
            TenantId = client.TenantId,
            ClientId = client.Id,
            CreditPackageId = package!.Id,
            Status = LoanStatus.PendingApproval,
            Frequency = DebitFrequency.Monthly,
            TrackingDays = model.TrackingDays,
            AgreementDate = DateTime.Today,
            CreatedByUserId = userId
        };

        var schedule = LoanOrigination.ApplyQuote(loan, quote!);
        loan.CollectionDay = LoanOrigination.CollectionDayFor(loan.FirstCollectionDate!.Value);

        _db.Loans.Add(loan);
        _db.LoanScheduleEntries.AddRange(schedule);
        _db.AffordabilityAssessments.Add(LoanOrigination.ToAssessment(loan.Id, input!, result!, userId));

        await SaveWithLoanNumberAsync(loan);

        _log.LogInformation(
            "Loan {LoanNumber} raised for client {ClientNumber}: {Principal} over {Term} months, affordability {Outcome}.",
            loan.LoanNumber, client.ClientNumber, loan.Principal, loan.NumberOfInstalments, result!.Outcome);

        TempData[result.Outcome == AffordabilityOutcome.Pass ? "Success" : "Info"] =
            result.Outcome switch
            {
                AffordabilityOutcome.Pass =>
                    $"Loan {loan.LoanNumber} submitted for approval.",
                AffordabilityOutcome.Marginal =>
                    $"Loan {loan.LoanNumber} submitted. Affordability is marginal, so the approver " +
                    "must record a reason to proceed.",
                _ =>
                    $"Loan {loan.LoanNumber} submitted, but the client failed the affordability " +
                    "assessment. It can only be approved with a recorded override."
            };

        return RedirectToAction(nameof(Details), new { id = loan.Id });
    }

    // ---------------------------------------------------------------- one loan

    [Authorize(Roles = ReadRoles)]
    public async Task<IActionResult> Details(Guid id)
    {
        var loan = await _db.Loans
            .AsNoTracking()
            .Include(l => l.Client)
            .Include(l => l.CreditPackage)
            .Include(l => l.Mandate)
            .Include(l => l.Schedule.OrderBy(s => s.InstalmentNumber))
            .Include(l => l.Assessments)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loan is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(loan.TenantId);

        ViewData["Title"] = loan.LoanNumber;

        var userIds = new[] { loan.CreatedByUserId, loan.DecidedByUserId }
            .Concat(loan.Assessments.Select(a => a.OverriddenByUserId))
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToList();

        ViewBag.UserNames = await _db.Users
            .Where(u => userIds.Contains(u.Id.ToString()))
            .ToDictionaryAsync(u => u.Id.ToString(), u => u.FullName ?? u.Email ?? "Unknown");

        var canDecide = User.IsInRole(AppRoles.SuperAdmin) || User.IsInRole(AppRoles.TenantAdmin);
        ViewBag.CanDecide = canDecide;
        ViewBag.CanCapture = canDecide || User.IsInRole(AppRoles.Capturer);

        ViewBag.Contracts = new LoanContractsPanel
        {
            LoanId = loan.Id,
            LoanStatus = loan.Status,
            Contracts = await _db.LoanContracts.AsNoTracking()
                .Where(c => c.LoanId == loan.Id)
                .OrderByDescending(c => c.Issue)
                .ToListAsync(),
            CanCapture = (bool)ViewBag.CanCapture,
            CanDecide = canDecide
        };

        return View(loan);
    }

    // ---------------------------------------------------------------- the decision

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanApproveCredit)]
    public async Task<IActionResult> Approve(Guid id, string? overrideReason)
    {
        var loan = await LoadForDecisionAsync(id);
        if (loan is null) return NotFound();

        if (loan.Status != LoanStatus.PendingApproval)
            return Refuse(id, "Only a loan awaiting approval can be approved.");

        var userId = _users.GetUserId(User);

        // Re-assessed now, not trusted from submission. The NCA test applies when the
        // agreement is entered into, and a lot can change between submitting and deciding -
        // most obviously another loan to the same client being approved in between, which
        // the submission-time assessment knew nothing about.
        var financial = await _db.ClientFinancials.AsNoTracking()
            .FirstOrDefaultAsync(f => f.ClientId == loan.ClientId);

        var input = LoanOrigination.AffordabilityInputFor(
            financial, loan.FirstInstalment, await OwnInstalmentsAsync(loan.ClientId, excludingLoanId: loan.Id));

        if (input is null)
            return Refuse(id, "This client no longer has income on file, so affordability cannot be assessed.");

        var result = _affordability.Assess(input);
        var previous = loan.Assessments.OrderByDescending(a => a.AssessedUtc).FirstOrDefault();

        var assessment = previous;
        var changed = previous is null ||
                      previous.Outcome != result.Outcome ||
                      previous.DiscretionaryIncome != result.DiscretionaryIncome;

        if (changed)
        {
            // Through the DbSet: a keyed child attached via a tracked parent's navigation
            // would be treated as an existing row and UPDATEd.
            assessment = LoanOrigination.ToAssessment(loan.Id, input, result, userId);
            _db.AffordabilityAssessments.Add(assessment);
        }

        if (result.Outcome == AffordabilityOutcome.Insufficient)
        {
            await _db.SaveChangesAsync();
            return Refuse(id, "Affordability cannot be assessed: this client has no income on file.");
        }

        // Marginal and Fail are not forbidden, but approving one is a decision somebody has to
        // own - in writing, with their name on it.
        if (assessment!.Outcome != AffordabilityOutcome.Pass)
        {
            if (string.IsNullOrWhiteSpace(overrideReason))
            {
                // Keep the fresh assessment even though nothing is approved, so the approver
                // sees on the page what they are being asked to override.
                if (changed) await _db.SaveChangesAsync();

                var verdict = assessment.Outcome == AffordabilityOutcome.Marginal
                    ? "Affordability is marginal"
                    : "The client does not pass the affordability assessment";

                return Refuse(id, changed && previous is not null
                    ? $"Affordability has changed since this loan was submitted. {verdict} " +
                      "now - see the updated assessment below. Record why it should be approved anyway."
                    : $"{verdict}. Record why this loan should be approved anyway.");
            }

            assessment.WasOverridden = true;
            assessment.OverrideReason = overrideReason.Trim();
            assessment.OverriddenByUserId = userId;
        }

        loan.Status = LoanStatus.Approved;
        loan.DecidedUtc = DateTime.UtcNow;
        loan.DecidedByUserId = userId;
        loan.DecisionNotes = string.IsNullOrWhiteSpace(overrideReason) ? null : overrideReason.Trim();

        await _db.SaveChangesAsync();

        _log.LogInformation("Loan {LoanNumber} approved{Override}.",
            loan.LoanNumber, assessment.WasOverridden ? " with an affordability override" : "");

        TempData["Success"] =
            $"Loan {loan.LoanNumber} approved. Originate the DebiCheck mandate, then mark it disbursed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanApproveCredit)]
    public async Task<IActionResult> Decline(Guid id, string? reason)
    {
        var loan = await LoadForDecisionAsync(id);
        if (loan is null) return NotFound();

        if (loan.Status != LoanStatus.PendingApproval)
            return Refuse(id, "Only a loan awaiting approval can be declined.");

        // A declined applicant is entitled to know why (NCA s62), so the reason is required.
        if (string.IsNullOrWhiteSpace(reason))
            return Refuse(id, "Record the reason for declining.");

        loan.Status = LoanStatus.Declined;
        loan.DecidedUtc = DateTime.UtcNow;
        loan.DecidedByUserId = _users.GetUserId(User);
        loan.DecisionNotes = reason.Trim();

        await _db.SaveChangesAsync();

        _log.LogInformation("Loan {LoanNumber} declined.", loan.LoanNumber);

        TempData["Success"] = $"Loan {loan.LoanNumber} declined.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Records that the money has been paid out. This does not move any money - the payout
    /// itself happens outside AM-Pay for now - it records that it happened, which is what
    /// makes the client Active and the loan collectable.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanApproveCredit)]
    public async Task<IActionResult> Disburse(Guid id)
    {
        var loan = await LoadForDecisionAsync(id);
        if (loan is null) return NotFound();

        if (loan.Status != LoanStatus.Approved)
            return Refuse(id, "Only an approved loan can be marked as disbursed.");

        // The client agrees before the money moves - never the other way round.
        var signed = await _db.LoanContracts.AnyAsync(c => c.LoanId == loan.Id && c.Status == ContractStatus.Signed);
        if (!signed)
            return Refuse(id, "The client has not signed the credit agreement yet. Issue the contract, send it, and " +
                              "pay out only once it shows as signed.");

        loan.Status = LoanStatus.Disbursed;
        loan.DisbursedUtc = DateTime.UtcNow;

        var client = await _db.Clients.FirstAsync(c => c.Id == loan.ClientId);
        if (client.Status == ClientStatus.Onboarded)
        {
            client.Status = ClientStatus.Active;
            client.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        _log.LogInformation("Loan {LoanNumber} disbursed; client {ClientNumber} is active.",
            loan.LoanNumber, client.ClientNumber);

        TempData["Success"] = loan.MandateId is null
            ? $"Loan {loan.LoanNumber} marked as disbursed. No DebiCheck mandate is linked yet - " +
              "originate one so the instalments can be collected."
            : $"Loan {loan.LoanNumber} marked as disbursed.";

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.CanCapture)]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var loan = await LoadForDecisionAsync(id);
        if (loan is null) return NotFound();

        if (loan.Status is not (LoanStatus.PendingApproval or LoanStatus.Approved))
            return Refuse(id, "Only a loan that has not been disbursed can be cancelled.");

        loan.Status = LoanStatus.Cancelled;
        await _contracts.VoidOpenForLoanAsync(loan.Id, "The loan was cancelled.", _users.GetUserId(User));
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Loan {loan.LoanNumber} cancelled.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------------------------------------------------------------- helpers

    private (LoanQuote? Quote, string? Error) TryQuote(
        CreditPackage package, decimal principal, int instalments, DateTime firstCollection)
    {
        try
        {
            return (_pricing.Quote(new LoanQuoteRequest(principal, instalments, firstCollection), package), null);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // The framework appends " (Parameter 'request')" - useful in a log, noise on screen.
            var message = ex.Message;
            var cut = message.IndexOf(" (Parameter", StringComparison.Ordinal);
            return (null, cut > 0 ? message[..cut] : message);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }
    }

    private static LoanQuoteView ToView(LoanQuote q) => new()
    {
        Ok = true,
        Principal = q.Principal,
        NumberOfInstalments = q.NumberOfInstalments,
        MonthlyInterestRate = q.MonthlyInterestRate,
        MonthlyServiceFee = q.MonthlyServiceFee,
        CreditLifeRate = q.CreditLifeRate,
        InitiationFee = q.InitiationFee,
        CapitalisedAmount = q.CapitalisedAmount,
        FirstInstalment = q.FirstInstalment,
        FinalInstalment = q.FinalInstalment,
        TotalInterest = q.TotalInterest,
        TotalServiceFees = q.TotalServiceFees,
        TotalCreditLife = q.TotalCreditLife,
        TotalRepayable = q.TotalRepayable,
        TotalCostOfCredit = q.TotalCostOfCredit,
        Notices = q.Notices.ToList(),
        Schedule = q.Schedule.ToList()
    };

    /// <summary>
    /// Assigns the next loan number and saves. Two operators submitting at the same instant
    /// can both pick the same number; the unique index turns that into an exception, and
    /// the loser simply takes the next one.
    /// </summary>
    private async Task SaveWithLoanNumberAsync(Loan loan)
    {
        const int attempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            var highest = await _db.Loans.AsNoTracking()
                .Where(l => l.TenantId == loan.TenantId && l.LoanNumber.StartsWith(LoanOrigination.LoanNumberPrefix))
                .OrderByDescending(l => l.LoanNumber)
                .Select(l => l.LoanNumber)
                .FirstOrDefaultAsync();

            loan.LoanNumber = LoanOrigination.NextLoanNumber(highest);

            try
            {
                await _db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException ex) when (attempt < attempts && IsDuplicateKey(ex))
            {
                _log.LogWarning("Loan number {LoanNumber} was taken concurrently; retrying.", loan.LoanNumber);
            }
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    private async Task<Client?> LoadClientAsync(Guid id)
    {
        var client = await _db.Clients
            .AsNoTracking()
            .Include(c => c.Financial)
            .Include(c => c.Employment)
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (client is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(client.TenantId);

        return client;
    }

    /// <summary>
    /// What the client already repays this lender each month on other live loans. The
    /// first instalment is used because it is the largest - see <see cref="Loan.FirstInstalment"/>.
    /// </summary>
    private async Task<decimal> OwnInstalmentsAsync(Guid clientId, Guid? excludingLoanId) =>
        await _db.Loans.AsNoTracking()
            .Where(l => l.ClientId == clientId && (excludingLoanId == null || l.Id != excludingLoanId))
            .Where(l => l.Status == LoanStatus.Approved || l.Status == LoanStatus.Disbursed)
            .SumAsync(l => (decimal?)l.FirstInstalment) ?? 0m;

    private async Task<Loan?> LoadForDecisionAsync(Guid id)
    {
        var loan = await _db.Loans
            .Include(l => l.Assessments)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loan is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(loan.TenantId);

        return loan;
    }

    private async Task<List<LoanCreateModel.PackageOption>> PackagesForAsync(Guid tenantId) =>
        await _db.CreditPackages.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.IsActive)
            .OrderBy(p => p.Tier).ThenBy(p => p.Name)
            .Select(p => new LoanCreateModel.PackageOption
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                MonthlyInterestRate = p.MonthlyInterestRate,
                MonthlyServiceFee = p.MonthlyServiceFee,
                InitiationFeeRate = p.InitiationFeeRate,
                CreditLifeRate = p.CreditLifeRate,
                MinLoanAmount = p.MinLoanAmount,
                MaxLoanAmount = p.MaxLoanAmount,
                MinTermMonths = p.MinTermMonths,
                MaxTermMonths = p.MaxTermMonths
            })
            .ToListAsync();

    private async Task RepopulateAsync(LoanCreateModel model, Client client)
    {
        model.ClientName = client.FullName;
        model.ClientNumber = client.ClientNumber;
        model.Packages = await PackagesForAsync(client.TenantId);
        model.HasFinancials = HasIncome(client.Financial);
    }

    private static bool HasIncome(ClientFinancial? f) =>
        f?.GrossMonthlyIncome > 0 && f.NetMonthlyIncome > 0;

    /// <summary>
    /// Why this client cannot be lent to, or an empty list when they can.
    /// <para>
    /// Checks the documents directly rather than trusting the status alone. Clients onboarded
    /// before document review existed were moved to Onboarded by a data migration without
    /// any documents on file; the status says they are verified, the documents say they are
    /// not, and the documents win.
    /// </para>
    /// </summary>
    private static List<string> ReasonsCannotBorrow(Client client)
    {
        var reasons = new List<string>();

        if (!ClientStatusInfo.CanBorrow(client.Status))
            reasons.Add($"the client is {ClientStatusInfo.Label(client.Status).ToLowerInvariant()}, not onboarded");

        foreach (var required in DocumentsController.Required)
        {
            var accepted = client.Documents.Any(d =>
                d.DocumentType == required && d.ReviewStatus == DocumentReviewStatus.Approved);

            if (!accepted)
                reasons.Add($"no accepted {DocumentsController.Describe(required).ToLowerInvariant()}");
        }

        return reasons;
    }

    private IActionResult RefuseCannotBorrow(Client client, List<string> reasons)
    {
        TempData["Error"] =
            $"A loan cannot be raised for this client yet: {string.Join("; ", reasons)}. " +
            "The identity document and payslip must both be accepted by a reviewer first.";
        return RedirectToAction("Details", "Clients", new { id = client.Id });
    }

    private IActionResult Refuse(Guid loanId, string message)
    {
        TempData["Error"] = message;
        return RedirectToAction(nameof(Details), new { id = loanId });
    }
}
