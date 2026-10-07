using System.Security.Cryptography;
using AMPay.Domain.Documents;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Domain.Portal;
using AMPay.Domain.Validation;
using AMPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Turns a submitted self-service application into an AM-Pay client - or adds it to the
/// client already on file with the same SA ID number - so onboarding carries on in the
/// normal wizard.
/// <para>
/// Nothing the client typed is trusted as verified. The client lands as captured, its
/// documents land as pending review, and the reviewer and the affordability test do their
/// usual jobs. For an existing client, nothing already on file is overwritten: the
/// application only fills gaps, and a note records what came in.
/// </para>
/// </summary>
public class PortalImporter
{
    private readonly AppDbContext _db;
    private readonly IPortalApi _portal;
    private readonly IDocumentStore _store;
    private readonly ILogger<PortalImporter> _log;

    public PortalImporter(AppDbContext db, IPortalApi portal, IDocumentStore store, ILogger<PortalImporter> log)
    {
        _db = db;
        _portal = portal;
        _store = store;
        _log = log;
    }

    public record Result(Client Client, bool Created, int DocumentsCopied, IReadOnlyList<string> Warnings);

    public async Task<Result> ImportAsync(Guid tenantId, Guid applicationId, string? userId, CancellationToken ct = default)
    {
        var app = await _portal.ApplicationAsync(tenantId, applicationId, ct)
                  ?? throw new PortalUnavailableException("That application was not found on the portal.");

        var s = app.Summary;
        if (s.Status is not (PortalApplicationStatus.Submitted or PortalApplicationStatus.PickedUp))
            throw new PortalUnavailableException("Only a submitted application can be imported.");

        var idNumber = new string((s.IdNumber ?? "").Where(char.IsDigit).ToArray());
        if (!SaIdNumber.IsValid(idNumber))
            throw new PortalUnavailableException("The application has no valid SA ID number, so it cannot be matched or imported.");

        var warnings = new List<string>();

        var client = await _db.Clients
            .Include(c => c.Employment)
            .Include(c => c.Financial)
            .Include(c => c.Addresses)
            .Include(c => c.OtherDetails)
            .Include(c => c.Payback)
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.IdNumber == idNumber, ct);

        var created = client is null;
        if (client is null)
        {
            client = new Client
            {
                TenantId = tenantId,
                ClientNumber = await ClientNumbers.NextAsync(_db, tenantId),
                CreatedByUserId = userId,
                Status = ClientStatus.Captured,
                Title = app.Title,
                FirstName = s.FirstName?.Trim() ?? "",
                MiddleNames = app.MiddleNames?.Trim(),
                Surname = s.Surname?.Trim() ?? "",
                IdNumber = idNumber,
                IsSaIdNumber = true
            };
            if (SaIdNumber.TryParseDateOfBirth(idNumber, out var dob)) client.DateOfBirth = dob;
            client.Gender = SaIdNumber.GetGender(idNumber);
            _db.Clients.Add(client);
        }
        else if (!string.Equals(client.Surname, s.Surname?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"The surname on the application ({s.Surname}) differs from the one on file ({client.Surname}). Check the ID document.");
        }

        // Employment and income - only where the file has nothing yet.
        if (client.Employment is null)
        {
            _db.Add(client.Employment = new ClientEmployment
            {
                ClientId = client.Id,
                EmployerName = app.EmployerName,
                Occupation = app.Occupation,
                EmployedSince = app.EmployedSince,
                PayFrequency = app.PayFrequency,
                SalaryDay = app.SalaryDay
            });
        }

        if (client.Financial is null)
        {
            _db.Add(client.Financial = new ClientFinancial
            {
                ClientId = client.Id,
                GrossMonthlyIncome = app.GrossMonthlyIncome,
                NetMonthlyIncome = app.NetMonthlyIncome,
                OtherIncome = app.OtherMonthlyIncome,
                TotalMonthlyExpenses = app.MonthlyExpenses,
                TotalMonthlyDebtRepayments = app.MonthlyDebtRepayments
            });
        }

        var mobile = PortalApi.DisplayMobile(s.Mobile);
        var physical = client.Addresses.FirstOrDefault(a => a.AddressType == AddressType.Physical);
        if (physical is null)
        {
            _db.Add(new ClientAddress
            {
                ClientId = client.Id,
                AddressType = AddressType.Physical,
                Line1 = app.AddressLine1,
                Line2 = app.AddressLine2,
                Suburb = app.Suburb,
                City = app.City,
                Province = app.Province,
                PostalCode = app.PostalCode,
                MobileNumber = mobile,
                EmailAddress = app.Email
            });
        }
        else
        {
            physical.MobileNumber ??= mobile;
            physical.EmailAddress ??= app.Email;
        }

        // Consent as given on the portal, with the time it was given.
        if (client.OtherDetails is null)
        {
            _db.Add(client.OtherDetails = new ClientOtherDetails
            {
                ClientId = client.Id,
                Source = "Self-service portal",
                DataProcessingConsent = app.DataProcessingConsent,
                DataProcessingConsentUtc = app.DataProcessingConsent ? app.ConsentUtc : null,
                CreditCheckConsent = app.CreditCheckConsent,
                CreditCheckConsentUtc = app.CreditCheckConsent ? app.ConsentUtc : null,
                MarketingConsent = app.MarketingConsent,
                MarketingConsentUtc = app.MarketingConsent ? app.ConsentUtc : null
            });
        }

        if (client.Payback is null && s.RequestedAmount is not null)
        {
            _db.Add(client.Payback = new ClientPayback
            {
                ClientId = client.Id,
                LoanAmount = s.RequestedAmount,
                NumberOfInstalments = s.RequestedTerm
            });
        }

        // Documents: copied in, fingerprint-checked against the portal's, pending review.
        var copied = 0;
        foreach (var d in app.Documents)
        {
            if (client.Documents.Any(x => x.ContentHash == d.Sha256))
                continue; // already imported on an earlier pick-up

            var bytes = await _portal.DocumentAsync(tenantId, s.Id, d.Id, ct);
            if (bytes is null)
            {
                warnings.Add($"{d.FileName} could not be fetched from the portal.");
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != d.Sha256)
            {
                warnings.Add($"{d.FileName} did not match its fingerprint and was not imported.");
                continue;
            }

            StoredDocument stored;
            await using (var ms = new MemoryStream(bytes))
                stored = await _store.SaveAsync(tenantId, client.Id, d.FileName, ms, ct);

            _db.ClientDocuments.Add(new ClientDocument
            {
                ClientId = client.Id,
                DocumentType = d.DocumentType,
                FileName = d.FileName,
                ContentType = d.ContentType,
                SizeBytes = stored.SizeBytes,
                StoragePath = stored.StoragePath,
                ContentHash = stored.ContentHash,
                UploadedByUserId = userId,
                ReviewStatus = DocumentReviewStatus.Pending
            });
            copied++;
        }

        // New documents to review put the client in the reviewer's queue, as an upload would.
        if (copied > 0 && client.Status is ClientStatus.Draft or ClientStatus.Captured)
            client.Status = ClientStatus.PendingVerification;

        _db.ClientNotes.Add(new ClientNote
        {
            ClientId = client.Id,
            Category = "Self-service",
            CreatedByUserId = userId,
            Body = $"Self-service application {s.Reference} imported{(created ? "" : " into the existing client file")}: " +
                   $"asked for {(s.RequestedAmount is { } a ? "R " + a.ToString("N2") : "an unstated amount")}" +
                   $"{(s.RequestedTerm is { } t ? $" over {t} months" : "")}, {copied} document(s) copied for review. " +
                   "Income and expenses are as declared by the client and are not verified."
        });

        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Only once AM-Pay holds everything does the portal hear it was picked up.
        await _portal.MarkPickedUpAsync(tenantId, s.Id, new PortalPickedUp(client.Id, client.ClientNumber), ct);

        _log.LogInformation("Self-service application {Reference} imported as client {ClientNumber} ({Mode}).",
            s.Reference, client.ClientNumber, created ? "new" : "existing");

        return new Result(client, created, copied, warnings);
    }
}
