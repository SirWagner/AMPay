using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Domain.Validation;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Models;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Controllers;

/// <summary>
/// Client onboarding.
/// <para>
/// The capture follows the Maxmoney tab order, but as one wizard over a single aggregate
/// rather than thirteen independent forms. The client record is created at the end of the
/// General step and every later step edits that record, so a half-finished capture survives
/// the operator closing the browser instead of being lost.
/// </para>
/// </summary>
[Authorize(Policy = AppPolicies.CanCapture)]
public class ClientsController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly INetcashValidationService _validation;
    private readonly IAffordabilityService _affordability;
    private readonly ILogger<ClientsController> _log;

    public ClientsController(
        AppDbContext db,
        ICurrentTenant tenant,
        INetcashValidationService validation,
        IAffordabilityService affordability,
        ILogger<ClientsController> log)
    {
        _db = db;
        _tenant = tenant;
        _validation = validation;
        _affordability = affordability;
        _log = log;
    }

    // ---------------------------------------------------------------- list

    public async Task<IActionResult> Index(string? q, ClientStatus? status)
    {
        ViewData["Title"] = "Clients";
        await _tenant.LoadAsync();

        if (_tenant.TenantId is null && !_tenant.IsPlatformUser)
            return TenantNotSelected();

        var query = _db.Clients.AsNoTracking().AsQueryable();

        if (_tenant.TenantId is not null)
            query = query.Where(c => c.TenantId == _tenant.TenantId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c =>
                c.FirstName.Contains(term) ||
                c.Surname.Contains(term) ||
                c.ClientNumber.Contains(term) ||
                c.IdNumber.Contains(term));
        }

        if (status is not null) query = query.Where(c => c.Status == status);

        ViewBag.Query = q;
        ViewBag.Status = status;

        var clients = await query
            .OrderByDescending(c => c.CreatedUtc)
            .Take(200)
            .Include(c => c.Mandates)
            .ToListAsync();

        return View(clients);
    }

    // ------------------------------------------------------------- step 1

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New client";
        await _tenant.LoadAsync();

        if (_tenant.TenantId is null) return TenantNotSelected();

        return View("General", new GeneralStepModel
        {
            ClientNumber = await NextClientNumberAsync(_tenant.TenantId.Value)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> General(GeneralStepModel model)
    {
        ViewData["Title"] = model.ClientId is null ? "New client" : "General details";
        await _tenant.LoadAsync();

        if (_tenant.TenantId is null) return TenantNotSelected();
        var tenantId = _tenant.TenantId.Value;

        await ValidateIdentityAsync(model);

        if (!ModelState.IsValid)
        {
            if (model.ClientId is not null) await PopulateWizardAsync(model.ClientId.Value);
            return View("General", model);
        }

        Client client;

        if (model.ClientId is null)
        {
            // Field 101 must be unique in the tenant Netcash masterfile.
            var number = string.IsNullOrWhiteSpace(model.ClientNumber)
                ? await NextClientNumberAsync(tenantId)
                : model.ClientNumber.Trim();

            if (await _db.Clients.AnyAsync(c => c.TenantId == tenantId && c.ClientNumber == number))
            {
                ModelState.AddModelError(nameof(model.ClientNumber),
                    "That client number is already in use for this customer account.");
                return View("General", model);
            }

            client = new Client
            {
                TenantId = tenantId,
                ClientNumber = number,
                CreatedByUserId = User.Identity?.Name,
                Status = ClientStatus.Draft
            };
            _db.Clients.Add(client);
        }
        else
        {
            client = await LoadClientAsync(model.ClientId.Value)
                     ?? throw new InvalidOperationException("Client not found.");
        }

        client.Title = model.Title;
        client.FirstName = model.FirstName.Trim();
        client.MiddleNames = model.MiddleNames?.Trim();
        client.Surname = model.Surname.Trim();
        client.IdNumber = model.IdNumber.Trim();
        client.IsSaIdNumber = model.IsSaIdNumber;
        client.DateOfBirth = model.DateOfBirth;
        client.Gender = model.Gender;
        client.MaritalStatus = model.MaritalStatus;
        client.Nationality = model.Nationality;
        client.PreferredLanguage = model.PreferredLanguage;
        client.UpdatedUtc = DateTime.UtcNow;

        // An SA ID number already carries date of birth and gender; fill them rather than
        // asking the operator to retype what the number states.
        if (client.IsSaIdNumber && SaIdNumber.IsValid(client.IdNumber))
        {
            if (client.DateOfBirth is null &&
                SaIdNumber.TryParseDateOfBirth(client.IdNumber, out var dob))
                client.DateOfBirth = dob;

            client.Gender ??= SaIdNumber.GetGender(client.IdNumber);
        }

        await _db.SaveChangesAsync();

        TempData["Success"] = "General details saved.";
        return RedirectToAction(nameof(Employment), new { id = client.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        ViewData["Title"] = "General details";
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        await PopulateWizardAsync(id);

        return View("General", new GeneralStepModel
        {
            ClientId = client.Id,
            ClientNumber = client.ClientNumber,
            Title = client.Title,
            FirstName = client.FirstName,
            MiddleNames = client.MiddleNames,
            Surname = client.Surname,
            IdNumber = client.IdNumber,
            IsSaIdNumber = client.IsSaIdNumber,
            DateOfBirth = client.DateOfBirth,
            Gender = client.Gender,
            MaritalStatus = client.MaritalStatus,
            Nationality = client.Nationality,
            PreferredLanguage = client.PreferredLanguage
        });
    }

    // ------------------------------------------------------------- step 2

    [HttpGet]
    public async Task<IActionResult> Employment(Guid id)
    {
        ViewData["Title"] = "Employment";
        var client = await LoadClientAsync(id, c => c.Employment);
        if (client is null) return NotFound();

        await PopulateWizardAsync(id);

        var e = client.Employment;
        return View(new EmploymentStepModel
        {
            ClientId = id,
            EmployerName = e?.EmployerName,
            Occupation = e?.Occupation,
            Department = e?.Department,
            EmployeeNumber = e?.EmployeeNumber,
            EmploymentType = e?.EmploymentType,
            EmployedSince = e?.EmployedSince,
            WorkTelephone = e?.WorkTelephone,
            SupervisorName = e?.SupervisorName,
            PayFrequency = e?.PayFrequency,
            SalaryDay = e?.SalaryDay
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Employment(EmploymentStepModel model)
    {
        ViewData["Title"] = "Employment";
        var client = await LoadClientAsync(model.ClientId, c => c.Employment);
        if (client is null) return NotFound();

        if (!ModelState.IsValid)
        {
            await PopulateWizardAsync(model.ClientId);
            return View(model);
        }

        var e = GetOrCreate(client.Employment, _db.ClientEmployments,
            () => new ClientEmployment { ClientId = client.Id });
        client.Employment = e;

        e.EmployerName = model.EmployerName;
        e.Occupation = model.Occupation;
        e.Department = model.Department;
        e.EmployeeNumber = model.EmployeeNumber;
        e.EmploymentType = model.EmploymentType;
        e.EmployedSince = model.EmployedSince;
        e.WorkTelephone = model.WorkTelephone;
        e.SupervisorName = model.SupervisorName;
        e.PayFrequency = model.PayFrequency;
        e.SalaryDay = model.SalaryDay;

        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Employment details saved.";
        return RedirectToAction(nameof(Financial), new { id = client.Id });
    }

    // ------------------------------------------------------------- step 3

    [HttpGet]
    public async Task<IActionResult> Financial(Guid id)
    {
        ViewData["Title"] = "Financial";
        var client = await LoadClientAsync(id, c => c.Financial, c => c.Budgets);
        if (client is null) return NotFound();

        await PopulateWizardAsync(id);

        var f = client.Financial;
        var model = new FinancialStepModel
        {
            ClientId = id,
            GrossMonthlyIncome = f?.GrossMonthlyIncome,
            NetMonthlyIncome = f?.NetMonthlyIncome,
            OtherIncome = f?.OtherIncome,
            OtherIncomeSource = f?.OtherIncomeSource,
            Budget = BudgetLinesFor(client.Budgets, f),
            BankName = f?.BankName,
            YearsAtBank = f?.YearsAtBank
        };
        model.NetOfNet = NetOfNetFor(model);

        return View(model);
    }

    /// <summary>
    /// Saves the step, or - for <paramref name="command"/> "add" (the line chosen in
    /// <paramref name="addLine"/>), "remove:{index}" or "recalculate" - redraws the form
    /// with the change and a fresh NET of NET, saving nothing. Round-tripping keeps the Regulation 23A calculation in one place, on the
    /// server, rather than duplicated in script.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Financial(
        FinancialStepModel model, string? command, string? addLine)
    {
        ViewData["Title"] = "Financial";
        var client = await LoadClientAsync(model.ClientId, c => c.Financial, c => c.Budgets);
        if (client is null) return NotFound();

        NormaliseBudget(model);

        if (!string.IsNullOrEmpty(command) && command != "save")
        {
            if (command == "add" && addLine == BudgetCategories.Custom)
            {
                model.Budget.Add(new BudgetLineModel
                {
                    Category = BudgetCategories.Custom,
                    Kind = BudgetLineKind.Expense
                });
            }
            else if (command == "add" && BudgetCategories.Find(addLine) is not null)
            {
                model.Budget.First(l => l.Category == addLine).Shown = true;
            }
            else if (command.StartsWith("remove:", StringComparison.Ordinal) &&
                     int.TryParse(command["remove:".Length..], out var index) &&
                     index >= 0 && index < model.Budget.Count)
            {
                var line = model.Budget[index];

                if (line.IsCustom)
                {
                    model.Budget.RemoveAt(index);
                }
                else if (!line.IsMain)
                {
                    // Standard lines always exist; removing one clears it back into the list.
                    line.Amount = null;
                    line.Note = null;
                    line.Shown = false;
                }
            }

            // Posted values would otherwise win over the model, and after a removal every
            // row below it would show its neighbour's figures. Validation waits for Save.
            ModelState.Clear();
            model.NetOfNet = NetOfNetFor(model);
            await PopulateWizardAsync(model.ClientId);
            return View(model);
        }

        if (model.NetMonthlyIncome > model.GrossMonthlyIncome)
            ModelState.AddModelError(nameof(model.NetMonthlyIncome),
                "Net income cannot exceed gross income.");

        for (var i = 0; i < model.Budget.Count; i++)
        {
            var line = model.Budget[i];
            if (line.IsCustom && line.Amount > 0 && string.IsNullOrWhiteSpace(line.Description))
                ModelState.AddModelError($"{nameof(model.Budget)}[{i}].{nameof(line.Description)}",
                    "Say what this expense is.");
        }

        if (!ModelState.IsValid)
        {
            model.NetOfNet = NetOfNetFor(model);
            await PopulateWizardAsync(model.ClientId);
            return View(model);
        }

        var f = GetOrCreate(client.Financial, _db.ClientFinancials,
            () => new ClientFinancial { ClientId = client.Id });
        client.Financial = f;

        f.GrossMonthlyIncome = model.GrossMonthlyIncome;
        f.NetMonthlyIncome = model.NetMonthlyIncome;
        f.OtherIncome = model.OtherIncome;
        f.OtherIncomeSource = model.OtherIncomeSource;
        f.BankName = model.BankName;
        f.YearsAtBank = model.YearsAtBank;

        SaveBudget(client, model.Budget);

        // The totals the loan affordability assessment reads. Derived, never typed.
        f.TotalMonthlyExpenses = model.TotalExpenses;
        f.TotalMonthlyDebtRepayments = model.TotalDebtInstalments;

        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Financial details and budget saved.";
        return RedirectToAction(nameof(Banking), new { id = client.Id });
    }

    /// <summary>
    /// The budget as the form shows it: one line per standard category, then custom lines.
    /// <para>
    /// A client captured before the budget existed has only the two totals. They are
    /// carried into Other and Loans, so the first save does not quietly zero them.
    /// </para>
    /// </summary>
    private static List<BudgetLineModel> BudgetLinesFor(
        IEnumerable<ClientBudget> saved, ClientFinancial? financial)
    {
        var rows = saved.ToList();
        var carryOver = rows.Count == 0;

        var lines = BudgetCategories.Standard.Select(c =>
        {
            var row = rows.FirstOrDefault(r => r.Category == c.Key);
            var line = new BudgetLineModel
            {
                Id = row?.Id,
                Category = c.Key,
                Kind = c.Kind,
                Description = c.Label,
                Amount = row?.Amount,
                Note = row?.Notes,
                Hint = c.Hint
            };

            if (carryOver && financial is not null)
            {
                var earlier = c.Key switch
                {
                    "Other" => financial.TotalMonthlyExpenses,
                    "Loans" => financial.TotalMonthlyDebtRepayments,
                    _ => null
                };

                if (earlier > 0)
                {
                    line.Amount = earlier;
                    line.Note = "Carried over from the total captured before the budget.";
                }
            }

            return line;
        }).ToList();

        lines.AddRange(rows
            .Where(r => BudgetCategories.Find(r.Category) is null)
            .OrderBy(r => r.DisplayOrder)
            .Select(r => new BudgetLineModel
            {
                Id = r.Id,
                Category = BudgetCategories.Custom,
                Kind = BudgetLineKind.Expense,
                Description = r.Description,
                Amount = r.Amount,
                Note = r.Notes
            }));

        return lines;
    }

    /// <summary>
    /// Rebuilds the posted budget from the catalogue. The form carries each line's category
    /// and figures; the kind and label come from here, so a tampered post cannot move an
    /// expense into debt or rename a standard line.
    /// </summary>
    private static void NormaliseBudget(FinancialStepModel model)
    {
        var posted = model.Budget ?? new List<BudgetLineModel>();

        var standard = BudgetCategories.Standard.Select(c =>
        {
            var line = posted.FirstOrDefault(l => l.Category == c.Key) ?? new BudgetLineModel();
            line.Category = c.Key;
            line.Kind = c.Kind;
            line.Description = c.Label;
            line.Hint = c.Hint;
            return line;
        });

        var custom = posted
            .Where(l => BudgetCategories.Find(l.Category) is null)
            .Select(l =>
            {
                l.Category = BudgetCategories.Custom;
                l.Kind = BudgetLineKind.Expense;
                l.Description = l.Description?.Trim();
                return l;
            });

        model.Budget = standard.Concat(custom).ToList();
    }

    /// <summary>
    /// Writes the budget lines. Existing rows are matched only among this client's own
    /// budget - an id posted from another client's form matches nothing and is ignored.
    /// </summary>
    private void SaveBudget(Client client, List<BudgetLineModel> lines)
    {
        var existing = client.Budgets.ToList();
        var kept = new HashSet<Guid>();
        var today = DateTime.UtcNow.Date;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // An added line left empty is a line nobody wanted.
            if (line.IsCustom && string.IsNullOrWhiteSpace(line.Description) && !(line.Amount > 0))
                continue;

            var row = existing.FirstOrDefault(r => line.Id is not null && r.Id == line.Id)
                      ?? (line.IsCustom ? null : existing.FirstOrDefault(r => r.Category == line.Category));

            if (row is null)
            {
                // Through the DbSet, not client.Budgets - see GetOrCreate.
                row = new ClientBudget { ClientId = client.Id };
                _db.ClientBudgets.Add(row);
            }

            row.Kind = line.Kind;
            row.Category = line.Category;
            row.Description = line.Description ?? "";
            row.Amount = line.Amount ?? 0m;
            row.Notes = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim();
            row.DisplayOrder = i;
            row.BudgetDate = today;

            kept.Add(row.Id);
        }

        _db.ClientBudgets.RemoveRange(existing.Where(r => !kept.Contains(r.Id)));
    }

    /// <summary>
    /// NET of NET for the figures on the form: the affordability assessment with a nil
    /// instalment. Null until gross and net pay are captured.
    /// </summary>
    private NetOfNetView? NetOfNetFor(FinancialStepModel model) =>
        NetOfNetFor(model.GrossMonthlyIncome, model.NetMonthlyIncome, model.OtherIncome,
            model.TotalExpenses, model.TotalDebtInstalments);

    private NetOfNetView? NetOfNetFor(
        decimal? gross, decimal? net, decimal? other, decimal expenses, decimal debt)
    {
        if (!(gross > 0) || !(net > 0)) return null;

        var input = new AffordabilityInput(
            GrossMonthlyIncome: gross ?? 0m,
            NetMonthlyIncome: net ?? 0m,
            DeclaredMonthlyExpenses: expenses,
            ExistingDebtRepayments: debt,
            ProposedInstalment: 0m,
            OtherMonthlyIncome: other ?? 0m);

        var result = _affordability.Assess(input);

        return new NetOfNetView
        {
            NetIncome = input.NetMonthlyIncome,
            OtherIncome = input.OtherMonthlyIncome,
            DeclaredExpenses = expenses,
            StatutoryMinimumExpenses = result.StatutoryMinimumExpenses,
            AppliedExpenses = result.AppliedExpenses,
            DebtInstalments = debt,
            NetOfNet = result.DiscretionaryIncome
        };
    }

    // ------------------------------------------------------------- step 4

    [HttpGet]
    public async Task<IActionResult> Banking(Guid id)
    {
        ViewData["Title"] = "Banking";
        var client = await LoadClientAsync(id, c => c.BankAccounts, c => c.Wallets);
        if (client is null) return NotFound();

        await PopulateWizardAsync(id);
        await PopulateBanksAsync();

        ViewBag.Client = client;
        return View(new BankAccountModel
        {
            ClientId = id,
            AccountHolderName = $"{client.FirstName} {client.Surname}".Trim(),
            IsPrimary = client.BankAccounts.Count == 0
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBankAccount(BankAccountModel model)
    {
        ViewData["Title"] = "Banking";
        var client = await LoadClientAsync(model.ClientId, c => c.BankAccounts, c => c.Wallets);
        if (client is null) return NotFound();

        // Cheap pre-flight. A malformed account number rejected here costs nothing; the same
        // number rejected inside a batch costs a failed collection and a load report to unpick.
        var format = await _validation.ValidateBankAccountAsync(
            model.AccountNumber, model.BranchCode, model.AccountType);

        if (!format.Success)
            ModelState.AddModelError(nameof(model.AccountNumber),
                format.Message ?? "The bank account details failed validation.");

        if (!ModelState.IsValid)
        {
            await PopulateWizardAsync(model.ClientId);
            await PopulateBanksAsync();
            ViewBag.Client = client;
            return View("Banking", model);
        }

        if (model.IsPrimary)
            foreach (var existing in client.BankAccounts) existing.IsPrimary = false;

        // Added through the DbSet, not through client.BankAccounts - see GetOrCreate below.
        _db.ClientBankAccounts.Add(new ClientBankAccount
        {
            ClientId = client.Id,
            AccountHolderName = model.AccountHolderName.Trim(),
            BankName = model.BankName,
            BranchCode = model.BranchCode.Trim(),
            AccountNumber = model.AccountNumber.Trim(),
            AccountType = model.AccountType,
            IsPrimary = model.IsPrimary || client.BankAccounts.Count == 0
        });

        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Bank account added.";
        return RedirectToAction(nameof(Banking), new { id = client.Id });
    }

    /// <summary>
    /// Runs account verification against the bank.
    /// <para>
    /// Separate from adding the account on purpose: AVS is a paid, rate-limited lookup, so it
    /// is an explicit operator action rather than something that fires on every keystroke.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyAccount(Guid id, Guid accountId)
    {
        var client = await LoadClientAsync(id, c => c.BankAccounts);
        if (client is null) return NotFound();

        var account = client.BankAccounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return NotFound();

        var result = await _validation.VerifyAccountAsync(
            serviceKey: string.Empty, // resolved inside the live client from the tenant key
            new AvsRequest
            {
                AccountNumber = account.AccountNumber,
                BranchCode = account.BranchCode,
                AccountType = account.AccountType,
                IdNumber = client.IsSaIdNumber ? client.IdNumber : null,
                Initials = client.FirstName.Length > 0 ? client.FirstName[..1] : null,
                Surname = client.Surname
            });

        if (result.Success && result.Data is not null)
        {
            account.AvsVerified = result.Data.AccountExists == true;
            account.AvsVerifiedUtc = DateTime.UtcNow;
            account.AvsResultJson = System.Text.Json.JsonSerializer.Serialize(result.Data);

            await _db.SaveChangesAsync();

            TempData[account.AvsVerified ? "Success" : "Error"] = account.AvsVerified
                ? "Account verified against the bank."
                : "The bank did not confirm this account.";
        }
        else
        {
            TempData["Error"] = result.Message ?? "Account verification failed.";
        }

        return RedirectToAction(nameof(Banking), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveBankAccount(Guid id, Guid accountId)
    {
        var client = await LoadClientAsync(id, c => c.BankAccounts);
        if (client is null) return NotFound();

        var account = client.BankAccounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return NotFound();

        // An account backing a live mandate must not disappear underneath it.
        var inUse = await _db.Mandates.AnyAsync(m =>
            m.BankAccountId == accountId && m.Status != MandateStatus.Cancelled);

        if (inUse)
        {
            TempData["Error"] =
                "This account backs a DebiCheck mandate that is not cancelled. Cancel the mandate first.";
            return RedirectToAction(nameof(Banking), new { id });
        }

        _db.ClientBankAccounts.Remove(account);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Bank account removed.";
        return RedirectToAction(nameof(Banking), new { id });
    }

    // ------------------------------------------------------------- repayment terms

    /// <summary>
    /// Repayment terms used to be captured here, as step 5. They now belong to a loan,
    /// raised once the client is onboarded - see LoansController. Kept as a redirect so an
    /// old link or bookmark lands somewhere useful instead of a 404.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Payback(Guid id)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        if (ClientStatusInfo.CanBorrow(client.Status))
            return RedirectToAction("Create", "Loans", new { clientId = id });

        TempData["Info"] =
            "Repayment terms are now set when a loan is raised, after onboarding is complete " +
            "and the client's identity document and payslip have been accepted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------- step 6

    [HttpGet]
    public async Task<IActionResult> Address(Guid id)
    {
        ViewData["Title"] = "Address";
        var client = await LoadClientAsync(id, c => c.Addresses);
        if (client is null) return NotFound();

        await PopulateWizardAsync(id);

        var a = client.Addresses.FirstOrDefault(x => x.AddressType == AddressType.Physical);
        return View(new AddressStepModel
        {
            ClientId = id,
            Id = a?.Id,
            AddressType = AddressType.Physical,
            Line1 = a?.Line1,
            Line2 = a?.Line2,
            Suburb = a?.Suburb,
            City = a?.City,
            Province = a?.Province,
            PostalCode = a?.PostalCode,
            HomeTelephone = a?.HomeTelephone,
            MobileNumber = a?.MobileNumber,
            EmailAddress = a?.EmailAddress
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Address(AddressStepModel model)
    {
        ViewData["Title"] = "Address";
        var client = await LoadClientAsync(model.ClientId, c => c.Addresses);
        if (client is null) return NotFound();

        // DebiCheck contacts the debtor on this number, so it is not optional here even
        // though the column allows null.
        if (string.IsNullOrWhiteSpace(model.MobileNumber))
            ModelState.AddModelError(nameof(model.MobileNumber),
                "A mobile number is required - DebiCheck uses it to reach the debtor.");

        if (!ModelState.IsValid)
        {
            await PopulateWizardAsync(model.ClientId);
            return View(model);
        }

        var a = client.Addresses.FirstOrDefault(x => x.AddressType == model.AddressType);
        if (a is null)
        {
            a = new ClientAddress { ClientId = client.Id, AddressType = model.AddressType };
            _db.ClientAddresses.Add(a);
        }

        a.Line1 = model.Line1;
        a.Line2 = model.Line2;
        a.Suburb = model.Suburb;
        a.City = model.City;
        a.Province = model.Province;
        a.PostalCode = model.PostalCode;
        a.HomeTelephone = model.HomeTelephone;
        a.MobileNumber = model.MobileNumber;
        a.EmailAddress = model.EmailAddress;

        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Address saved.";
        return RedirectToAction(nameof(OtherDetails), new { id = client.Id });
    }

    // ------------------------------------------------------------- step 7

    [HttpGet]
    public async Task<IActionResult> OtherDetails(Guid id)
    {
        ViewData["Title"] = "Other details";
        var client = await LoadClientAsync(id, c => c.OtherDetails);
        if (client is null) return NotFound();

        await PopulateWizardAsync(id);

        var o = client.OtherDetails;
        return View(new OtherDetailsStepModel
        {
            ClientId = id,
            RiskCategory = o?.RiskCategory,
            ClientCategory = o?.ClientCategory,
            Source = o?.Source,
            DataProcessingConsent = o?.DataProcessingConsent ?? false,
            CreditCheckConsent = o?.CreditCheckConsent ?? false,
            MarketingConsent = o?.MarketingConsent ?? false,
            Comments = o?.Comments
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OtherDetails(OtherDetailsStepModel model)
    {
        ViewData["Title"] = "Other details";
        var client = await LoadClientAsync(model.ClientId, c => c.OtherDetails);
        if (client is null) return NotFound();

        if (!model.DataProcessingConsent)
            ModelState.AddModelError(nameof(model.DataProcessingConsent),
                "POPIA consent is required before this client can be activated.");

        if (!ModelState.IsValid)
        {
            await PopulateWizardAsync(model.ClientId);
            return View(model);
        }

        var o = GetOrCreate(client.OtherDetails, _db.ClientOtherDetails,
            () => new ClientOtherDetails { ClientId = client.Id });
        client.OtherDetails = o;

        o.RiskCategory = model.RiskCategory;
        o.ClientCategory = model.ClientCategory;
        o.Source = model.Source;
        o.Comments = model.Comments;

        // Consent timestamps are the evidence, so only stamp them on the transition to true.
        if (model.DataProcessingConsent && !o.DataProcessingConsent)
            o.DataProcessingConsentUtc = DateTime.UtcNow;
        if (model.CreditCheckConsent && !o.CreditCheckConsent)
            o.CreditCheckConsentUtc = DateTime.UtcNow;
        if (model.MarketingConsent && !o.MarketingConsent)
            o.MarketingConsentUtc = DateTime.UtcNow;

        o.DataProcessingConsent = model.DataProcessingConsent;
        o.CreditCheckConsent = model.CreditCheckConsent;
        o.MarketingConsent = model.MarketingConsent;

        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Other details saved.";
        return RedirectToAction(nameof(Details), new { id = client.Id });
    }

    // ------------------------------------------------------- remaining tabs

    /// <summary>
    /// Steps 8 to 13: References, Budgets, Credit check, Notes, Documents, Photograph.
    /// <para>
    /// Budgets is captured on the Financial step, beside the income it is set against, and
    /// this sends it there.
    /// </para>
    /// <para>
    /// GAP - not yet built. References and Notes are plain CRUD over entities that
    /// already exist. Credit check needs a bureau decision (see ICreditBureauClient), and
    /// Documents and Photograph need Azure Blob Storage wired up. None of them block a
    /// DebiCheck mandate, which is why they are last.
    /// </para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Step(Guid id, OnboardingStep step)
    {
        var client = await LoadClientAsync(id);
        if (client is null) return NotFound();

        if (step == OnboardingStep.Budgets)
            return RedirectToAction(nameof(Financial), "Clients", new { id }, "budget");

        await PopulateWizardAsync(id, step);

        ViewData["Title"] = OnboardingSteps.All.First(s => s.Step == step).Label;
        ViewBag.Step = step;
        ViewBag.ClientId = id;
        return View("PendingStep");
    }

    // ------------------------------------------------------------- summary

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        ViewData["Title"] = "Client";

        var client = await _db.Clients
            .Include(c => c.Employment)
            .Include(c => c.Financial)
            .Include(c => c.Documents)
            .Include(c => c.Loans).ThenInclude(l => l.CreditPackage)
            .Include(c => c.OtherDetails)
            .Include(c => c.BankAccounts)
            .Include(c => c.Addresses)
            .Include(c => c.Mandates)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (client is null) return NotFound();

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(client.TenantId);

        var f = client.Financial;
        ViewBag.NetOfNet = f is null ? null
            : NetOfNetFor(f.GrossMonthlyIncome, f.NetMonthlyIncome, f.OtherIncome,
                f.TotalMonthlyExpenses ?? 0m, f.TotalMonthlyDebtRepayments ?? 0m);

        await PopulateWizardAsync(id);
        return View(client);
    }

    /// <summary>Moves a draft client to active once the mandate-critical steps are present.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(Guid id)
    {
        var client = await LoadClientAsync(id,
            c => c.BankAccounts, c => c.Addresses, c => c.OtherDetails, c => c.Documents);

        if (client is null) return NotFound();

        var missing = new List<string>();

        if (client.BankAccounts.Count == 0) missing.Add("a bank account");
        if (!client.Addresses.Any(a => !string.IsNullOrWhiteSpace(a.MobileNumber)))
            missing.Add("a mobile number");
        if (client.OtherDetails?.DataProcessingConsent != true) missing.Add("POPIA consent");

        // Verified documents, not merely uploaded ones. This is the control that lets a
        // client captured through a public self-service link be trusted at all.
        foreach (var required in DocumentsController.Required)
        {
            var accepted = client.Documents.Any(d =>
                d.DocumentType == required && d.ReviewStatus == DocumentReviewStatus.Approved);

            if (!accepted)
                missing.Add($"an accepted {DocumentsController.Describe(required).ToLowerInvariant()}");
        }

        if (missing.Count > 0)
        {
            TempData["Error"] =
                $"This client cannot be onboarded without {string.Join(", ", missing)}.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Onboarded, not Active. Active means the client is holding a disbursed loan, and
        // that is set by the loan, not here. Conflating the two makes it impossible to say
        // how much of the book is actually lending.
        client.Status = ClientStatus.Onboarded;
        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _log.LogInformation("Client {ClientNumber} onboarded.", client.ClientNumber);

        TempData["Success"] =
            "Client onboarded. A DebiCheck mandate can now be originated and credit advanced.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Returns the existing child, or creates one and registers it on the DbSet.
    /// <para>
    /// The DbSet.Add matters. Every entity here initialises its own key with
    /// <c>Guid.NewGuid()</c>, so when a new child is attached through a tracked parent's
    /// navigation property, EF sees a key that is already set, concludes the row must
    /// already exist, and marks the entity Modified. SaveChanges then issues an UPDATE
    /// against a row that was never inserted and throws DbUpdateConcurrencyException
    /// ("expected to affect 1 row(s), but actually affected 0").
    /// </para>
    /// <para>
    /// Adding through the DbSet states the intent explicitly and always produces an INSERT.
    /// </para>
    /// </summary>
    private static T GetOrCreate<T>(T? existing, DbSet<T> set, Func<T> create) where T : class
    {
        if (existing is not null) return existing;

        var created = create();
        set.Add(created);
        return created;
    }

    private async Task<Client?> LoadClientAsync(
        Guid id, params System.Linq.Expressions.Expression<Func<Client, object?>>[] includes)
    {
        IQueryable<Client> query = _db.Clients;
        foreach (var include in includes) query = query.Include(include);

        var client = await query.FirstOrDefaultAsync(c => c.Id == id);
        if (client is null) return null;

        await _tenant.LoadAsync();
        _tenant.EnsureCanAccess(client.TenantId);

        return client;
    }

    private async Task ValidateIdentityAsync(GeneralStepModel model)
    {
        if (!model.IsSaIdNumber) return;

        var result = await _validation.ValidateIdNumberAsync(model.IdNumber);
        if (!result.Success)
            ModelState.AddModelError(nameof(model.IdNumber),
                "That is not a valid South African ID number. Clear the checkbox if this is a passport.");
    }

    private async Task PopulateWizardAsync(Guid clientId, OnboardingStep? step = null)
    {
        var client = await _db.Clients
            .AsNoTracking()
            .Include(c => c.Employment)
            .Include(c => c.Financial)
            .Include(c => c.OtherDetails)
            .Include(c => c.BankAccounts)
            .Include(c => c.Addresses)
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == clientId);

        if (client is null) return;

        var done = new HashSet<OnboardingStep> { OnboardingStep.General };
        if (client.Employment is not null) done.Add(OnboardingStep.Employment);
        if (client.Financial is not null) done.Add(OnboardingStep.Financial);
        if (client.BankAccounts.Count > 0) done.Add(OnboardingStep.Banking);
        if (client.Addresses.Count > 0) done.Add(OnboardingStep.Address);
        if (client.OtherDetails is not null) done.Add(OnboardingStep.OtherDetails);
        if (await _db.ClientBudgets.AnyAsync(b => b.ClientId == clientId && b.Amount > 0))
            done.Add(OnboardingStep.Budgets);

        // Documents count as done only once the required ones are actually accepted -
        // uploading a file is not the same as having it pass.
        if (DocumentsController.Required.All(t => client.Documents.Any(d =>
                d.DocumentType == t && d.ReviewStatus == DocumentReviewStatus.Approved)))
            done.Add(OnboardingStep.Documents);

        ViewBag.Wizard = new WizardContext
        {
            ClientId = client.Id,
            ClientNumber = client.ClientNumber,
            ClientName = $"{client.FirstName} {client.Surname}".Trim(),
            CurrentStep = step ?? OnboardingStep.General,
            CompletedSteps = done
        };
    }

    private async Task PopulateBanksAsync()
    {
        var banks = await _validation.GetDebiCheckParticipatingBanksAsync();
        ViewBag.Banks = banks.Data ?? new List<BankInfo>();
    }

    /// <summary>
    /// Next sequential client number for the tenant.
    /// <para>
    /// Racy under concurrent capture - two operators starting at the same moment can land on
    /// the same number. The unique index on (TenantId, ClientNumber) catches it, and the
    /// operator can change the number. Replace with a per-tenant sequence if that becomes a
    /// routine collision rather than a rare one.
    /// </para>
    /// </summary>
    private async Task<string> NextClientNumberAsync(Guid tenantId)
    {
        var numbers = await _db.Clients
            .Where(c => c.TenantId == tenantId)
            .Select(c => c.ClientNumber)
            .ToListAsync();

        var highest = numbers
            .Select(n => int.TryParse(n, out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();

        return (highest + 1).ToString("D4");
    }

    private IActionResult TenantNotSelected()
    {
        TempData["Info"] = "Select a customer account to work in.";
        return RedirectToAction("Index", "Tenants");
    }
}
