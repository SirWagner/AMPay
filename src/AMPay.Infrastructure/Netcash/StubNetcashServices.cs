using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Domain.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Netcash;

// =============================================================================================
// STUB IMPLEMENTATIONS - the integration gaps.
//
// Every class here implements the full contract and returns plausible data so the application
// runs end to end without Netcash credentials. Each method carries a GAP note naming the exact
// SOAP operation to call and the fields it needs.
//
// To go live:
//   1. Generate the SOAP clients:
//        dotnet tool install --global dotnet-svcutil
//        dotnet-svcutil https://ws.netcash.co.za/NIWS/NIWS_NIF.svc?wsdl        -n "*,AMPay.Infrastructure.Netcash.Nif"
//        dotnet-svcutil https://ws.netcash.co.za/NIWS/NIWS_Validation.svc?wsdl -n "*,AMPay.Infrastructure.Netcash.Validation"
//        dotnet-svcutil https://ws.netcash.co.za/NIWS/niws_partner.svc?wsdl    -n "*,AMPay.Infrastructure.Netcash.Partner"
//   2. Add a live implementation of each interface beside its stub.
//   3. Flip Netcash:UseStubs to false. Registration in ServiceCollectionExtensions switches over.
//
// The stubs stay. They are how the test suite runs without touching a payment rail.
// =============================================================================================

/// <summary>Stub for the NIWS_Partner ISV surface.</summary>
public class StubNetcashPartnerService : INetcashPartnerService
{
    private readonly ILogger<StubNetcashPartnerService> _log;
    public StubNetcashPartnerService(ILogger<StubNetcashPartnerService> log) => _log = log;

    /// <summary>
    /// GAP - NIWS_Partner.ValidateServiceKey (SOAP 1.2).
    /// Build a ValidateServiceKeyRequest with MerchantAccount, SoftwareVendorKey and a
    /// ServiceInfoList of ServiceID/ServiceKey pairs. Map AccountStatus 001 to authenticated,
    /// then each ServiceInfoResponse.ServiceStatus: 001 validated, 105 no active service,
    /// 106 no active service key.
    /// <para>
    /// Do not retry on failure. Three failed attempts inside ten minutes lock the merchant
    /// account at Netcash, and only a successful call clears the lockout.
    /// </para>
    /// </summary>
    public Task<NetcashResult<IReadOnlyList<ServiceKeyValidation>>> ValidateServiceKeysAsync(
        string merchantAccountNumber,
        IReadOnlyDictionary<NetcashServiceId, string> serviceKeys,
        CancellationToken ct = default)
    {
        _log.LogWarning(
            "STUB ValidateServiceKey for account {Account} across {Count} service(s). No live call made.",
            merchantAccountNumber, serviceKeys.Count);

        var results = serviceKeys.Keys
            .Select(id => new ServiceKeyValidation
            {
                ServiceId = id,
                Status = ServiceKeyStatus.Validated,
                Message = "Stubbed - not validated against Netcash."
            })
            .ToList();

        return Task.FromResult(
            NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Ok(results, NetcashCodes.Authenticated));
    }
}

/// <summary>Stub for the DebiCheck surface of NIWS_NIF.</summary>
public class StubNetcashDebiCheckService : INetcashDebiCheckService
{
    private readonly ILogger<StubNetcashDebiCheckService> _log;
    public StubNetcashDebiCheckService(ILogger<StubNetcashDebiCheckService> log) => _log = log;

    /// <summary>
    /// GAP - NIWS_NIF.DebiCheckAuthenticate (synchronous TT1).
    /// Pass ServiceKey, AccountReference, DebiCheckMandateTemplateId, IsIdNumber,
    /// DebtorIdentification, AccountName, BankAccountName, BranchCode, BankAccountNumber,
    /// BankAccountType, MobileNumber, EmailAddress, CollectionAmount, FirstCollectionDiffers,
    /// FirstCollectionAmount, FirstCollectionDate (CCYYMMDD) and collectionDayCode.
    /// <para>
    /// Success is ErrorCode 000 with BankResponseCode 900000 and BankservResponseCode ACCP.
    /// Error 325 means the template is not configured for real-time and TT1 cannot be used -
    /// fall back to the batch route. Set the channel timeout to at least three minutes.
    /// </para>
    /// </summary>
    public Task<NetcashResult<DebiCheckAuthenticateResponse>> AuthenticateRealTimeAsync(
        string debitOrderServiceKey,
        DebiCheckAuthenticateRequest request,
        CancellationToken ct = default)
    {
        _log.LogWarning(
            "STUB DebiCheckAuthenticate (TT1) for reference {Reference}, amount {Amount}. No live call made.",
            request.AccountReference, request.CollectionAmount);

        var response = new DebiCheckAuthenticateResponse
        {
            ContractReference = $"STUB-{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}",
            BankResponseCode = NetcashCodes.BankResponseSuccess,
            BankservResponseCode = NetcashCodes.BankservAccepted,
            ClientResponseCode = "TRUE",
            Status = "Accepted"
        };

        return Task.FromResult(NetcashResult<DebiCheckAuthenticateResponse>.Ok(response));
    }

    /// <summary>
    /// GAP - NIWS_NIF.BatchFileUpload with instruction DebiCheckAuthentication (TT2).
    /// Build the file with NifFileBuilder, then submit it. Returns a file token; the bank
    /// answer arrives later on the NetConnector postback, so never treat the token as approval.
    /// <para>
    /// The masterfile entry must already exist at Netcash or the instruction is rejected.
    /// </para>
    /// </summary>
    public Task<NetcashResult<string>> AuthenticateBatchAsync(
        string debitOrderServiceKey,
        IReadOnlyList<DebiCheckAuthenticateRequest> requests,
        DateTime actionDate,
        string batchName,
        CancellationToken ct = default)
    {
        _log.LogWarning(
            "STUB DebiCheck batch authentication: {Count} mandate(s), action date {ActionDate:yyyy-MM-dd}. No live call made.",
            requests.Count, actionDate);

        return Task.FromResult(NetcashResult<string>.Ok(StubFileToken()));
    }

    /// <summary>GAP - NIWS_NIF.DebiCheckAuthenticationCurrentStatus.</summary>
    public Task<NetcashResult<string>> GetAuthenticationStatusAsync(
        string debitOrderServiceKey, string contractReference, CancellationToken ct = default)
    {
        _log.LogWarning("STUB DebiCheck status poll for {ContractReference}.", contractReference);
        return Task.FromResult(NetcashResult<string>.Ok("Accepted"));
    }

    /// <summary>GAP - NIWS_NIF.DebiCheckAmendAuthentication. Changes terms without re-authenticating.</summary>
    public Task<NetcashResult<bool>> AmendAuthenticationAsync(
        string debitOrderServiceKey, string contractReference, decimal newAmount,
        CancellationToken ct = default)
    {
        _log.LogWarning("STUB DebiCheck amend {ContractReference} to {Amount}.", contractReference, newAmount);
        return Task.FromResult(NetcashResult<bool>.Ok(true));
    }

    /// <summary>GAP - NIWS_NIF.DebiCheckCancelAuthentication. Requires a Netcash reason code.</summary>
    public Task<NetcashResult<bool>> CancelAuthenticationAsync(
        string debitOrderServiceKey, string contractReference, string reasonCode,
        CancellationToken ct = default)
    {
        _log.LogWarning("STUB DebiCheck cancel {ContractReference}, reason {Reason}.", contractReference, reasonCode);
        return Task.FromResult(NetcashResult<bool>.Ok(true));
    }

    /// <summary>
    /// GAP - NIWS_NIF.DebiCheckRetrieveMandateTemplate.
    /// Templates are configured on the Netcash side. Cache the list; it changes rarely, and a
    /// wrong template id is the most common cause of a rejected TT1.
    /// </summary>
    public Task<NetcashResult<IReadOnlyList<string>>> RetrieveMandateTemplatesAsync(
        string debitOrderServiceKey, CancellationToken ct = default)
    {
        _log.LogWarning("STUB DebiCheck mandate template list.");
        IReadOnlyList<string> templates = new[] { "NCDCT000000001", "NCDCT000000003" };
        return Task.FromResult(NetcashResult<IReadOnlyList<string>>.Ok(templates));
    }

    internal static string StubFileToken() =>
        $"20000000.{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}.0483.2.2";
}

/// <summary>Stub for the debit order and eMandate surface of NIWS_NIF.</summary>
public class StubNetcashDebitOrderService : INetcashDebitOrderService
{
    private readonly ILogger<StubNetcashDebitOrderService> _log;
    public StubNetcashDebitOrderService(ILogger<StubNetcashDebitOrderService> log) => _log = log;

    /// <summary>GAP - NIWS_NIF.BatchFileUpload, instruction Update. Creates the masterfile entry.</summary>
    public Task<NetcashResult<string>> UploadMasterfileAsync(
        string debitOrderServiceKey, string nifFileContent, CancellationToken ct = default)
    {
        _log.LogWarning("STUB masterfile upload, {Bytes} bytes.", nifFileContent.Length);
        return Task.FromResult(NetcashResult<string>.Ok(StubNetcashDebiCheckService.StubFileToken()));
    }

    /// <summary>
    /// GAP - NIWS_NIF.BatchFileUpload, instruction Sameday or TwoDay.
    /// Mind the cut-offs: same-day must be authorised by 10:59 on the action date, two-day by
    /// 23:59 two business days before. Validate with RequestActionDate rather than guessing.
    /// </summary>
    public Task<NetcashResult<string>> UploadCollectionBatchAsync(
        string debitOrderServiceKey, string nifFileContent, CancellationToken ct = default)
    {
        _log.LogWarning("STUB collection batch upload, {Bytes} bytes.", nifFileContent.Length);
        return Task.FromResult(NetcashResult<string>.Ok(StubNetcashDebiCheckService.StubFileToken()));
    }

    /// <summary>
    /// GAP - NIWS_NIF.RequestFileUploadReport.
    /// Parse the response with LoadReportParser. This is the only place per-line validation
    /// errors surface, so a batch is not confirmed until this has been read.
    /// </summary>
    public Task<NetcashResult<string>> GetLoadReportAsync(
        string debitOrderServiceKey, string fileToken, CancellationToken ct = default)
    {
        _log.LogWarning("STUB load report for token {FileToken}.", fileToken);

        var report = string.Join('\t', "###BEGIN", "Stub batch", "SUCCESSFUL", "10:30 AM", "0", "20260914")
                     + "\n"
                     + string.Join('\t', "###END", "10:30 AM")
                     + "\n";

        return Task.FromResult(NetcashResult<string>.Ok(report));
    }

    /// <summary>
    /// GAP - NIWS_NIF.AddMandate.
    /// Returns a hosted Netcash URL where the debtor signs after an OTP. On signature Netcash
    /// form-POSTs back to the postback URL configured once on the NetConnector page, adding
    /// MandateSuccessful and ReasonForDecline.
    /// </summary>
    public Task<NetcashResult<AddMandateResponse>> AddMandateAsync(
        string debitOrderServiceKey, AddMandateRequest request, CancellationToken ct = default)
    {
        _log.LogWarning("STUB AddMandate for {Reference}.", request.AccountReference);

        return Task.FromResult(NetcashResult<AddMandateResponse>.Ok(new AddMandateResponse
        {
            MandateUrl = "https://example.invalid/stub-mandate"
        }));
    }

    /// <summary>
    /// GAP - NIWS_NIF.RequestActionDate.
    /// The stub only skips weekends. The live call also accounts for South African public
    /// holidays and per-service cut-offs, which is why this must not ship as-is.
    /// </summary>
    public Task<NetcashResult<DateTime>> RequestActionDateAsync(
        string debitOrderServiceKey, DateTime desiredDate, CancellationToken ct = default)
    {
        var date = desiredDate.Date;
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            date = date.AddDays(1);

        _log.LogWarning("STUB action date: {Requested:yyyy-MM-dd} resolved to {Resolved:yyyy-MM-dd}. " +
                        "Weekends only - public holidays are NOT applied.", desiredDate, date);

        return Task.FromResult(NetcashResult<DateTime>.Ok(date));
    }
}

/// <summary>Stub for NIWS_Validation.</summary>
public class StubNetcashValidationService : INetcashValidationService
{
    private readonly ILogger<StubNetcashValidationService> _log;
    public StubNetcashValidationService(ILogger<StubNetcashValidationService> log) => _log = log;

    /// <summary>
    /// GAP - NIWS_NIF.AVSRealtimeQuery.
    /// Real AVS answers each question independently and any of them can come back unknown,
    /// which is why the response fields are nullable. Do not collapse that to a single boolean.
    /// </summary>
    public Task<NetcashResult<AvsResponse>> VerifyAccountAsync(
        string serviceKey, AvsRequest request, CancellationToken ct = default)
    {
        _log.LogWarning("STUB AVS for branch {Branch}. No live verification performed.", request.BranchCode);

        return Task.FromResult(NetcashResult<AvsResponse>.Ok(new AvsResponse
        {
            AccountExists = true,
            AccountOpenAtLeastThreeMonths = true,
            IdNumberMatches = true,
            InitialsMatch = true,
            SurnameMatches = true,
            AccountAcceptsDebits = true,
            AccountAcceptsCredits = true,
            Reference = $"STUB-AVS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
        }));
    }

    /// <summary>
    /// GAP - NIWS_Validation.ValidateBankAccount.
    /// Format and CDV only. It does not prove the account exists or belongs to the client -
    /// that needs AVS. The local check below is a length guard, nothing more.
    /// </summary>
    public Task<NetcashResult<bool>> ValidateBankAccountAsync(
        string accountNumber, string branchCode, BankAccountType accountType,
        CancellationToken ct = default)
    {
        var plausible = !string.IsNullOrWhiteSpace(accountNumber)
                        && accountNumber.All(char.IsDigit)
                        && accountNumber.Length is >= 6 and <= 16
                        && branchCode.Length == 6
                        && branchCode.All(char.IsDigit);

        return Task.FromResult(plausible
            ? NetcashResult<bool>.Ok(true)
            : NetcashResult<bool>.Fail(NetcashCodes.ParameterError,
                "Account number or branch code is not in a valid format."));
    }

    /// <summary>
    /// GAP - NIWS_Validation.ValidateId.
    /// The local Luhn check below is a fast pre-filter so the UI can flag a typo without a
    /// round trip. Netcash remains the authority.
    /// </summary>
    public Task<NetcashResult<bool>> ValidateIdNumberAsync(string idNumber, CancellationToken ct = default)
    {
        var valid = SaIdNumber.IsValid(idNumber);
        return Task.FromResult(valid
            ? NetcashResult<bool>.Ok(true)
            : NetcashResult<bool>.Fail(NetcashCodes.ParameterError, "South African ID number failed validation."));
    }

    /// <summary>GAP - NIWS_Validation.GetBankListWithDefaultBranchCode.</summary>
    public Task<NetcashResult<IReadOnlyList<BankInfo>>> GetBankListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<BankInfo> banks = StubBanks;
        return Task.FromResult(NetcashResult<IReadOnlyList<BankInfo>>.Ok(banks));
    }

    /// <summary>
    /// GAP - NIWS_Validation.GetDebiCheckParticipatingBanks.
    /// Not every bank processes DebiCheck. Check this before offering a mandate, or the
    /// authentication fails at the bank with no useful message for the operator.
    /// </summary>
    public Task<NetcashResult<IReadOnlyList<BankInfo>>> GetDebiCheckParticipatingBanksAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<BankInfo> banks = StubBanks.Where(b => b.SupportsDebiCheck).ToList();
        return Task.FromResult(NetcashResult<IReadOnlyList<BankInfo>>.Ok(banks));
    }

    /// <summary>
    /// Placeholder list with real South African universal branch codes, so the capture screens
    /// are usable before the live bank list is wired in.
    /// </summary>
    private static readonly List<BankInfo> StubBanks = new()
    {
        new() { Name = "ABSA Bank",        DefaultBranchCode = "632005", SupportsDebiCheck = true },
        new() { Name = "Capitec Bank",     DefaultBranchCode = "470010", SupportsDebiCheck = true },
        new() { Name = "First National Bank", DefaultBranchCode = "250655", SupportsDebiCheck = true },
        new() { Name = "Nedbank",          DefaultBranchCode = "198765", SupportsDebiCheck = true },
        new() { Name = "Standard Bank",    DefaultBranchCode = "051001", SupportsDebiCheck = true },
        new() { Name = "African Bank",     DefaultBranchCode = "430000", SupportsDebiCheck = true },
        new() { Name = "Bidvest Bank",     DefaultBranchCode = "462005", SupportsDebiCheck = true },
        new() { Name = "Discovery Bank",   DefaultBranchCode = "679000", SupportsDebiCheck = true },
        new() { Name = "Investec Bank",    DefaultBranchCode = "580105", SupportsDebiCheck = true },
        new() { Name = "TymeBank",         DefaultBranchCode = "678910", SupportsDebiCheck = true },
        new() { Name = "Access Bank",      DefaultBranchCode = "410506", SupportsDebiCheck = false },
        new() { Name = "Postbank",         DefaultBranchCode = "460005", SupportsDebiCheck = false }
    };
}

/// <summary>Stub for Pay Now.</summary>
public class StubNetcashPayNowService : INetcashPayNowService
{
    private readonly ILogger<StubNetcashPayNowService> _log;
    private readonly NetcashOptions _options;

    public StubNetcashPayNowService(ILogger<StubNetcashPayNowService> log, IOptions<NetcashOptions> options)
    {
        _log = log;
        _options = options.Value;
    }

    /// <summary>
    /// GAP - Pay Now hosted checkout.
    /// The field names below follow the documented Pay Now form contract. Confirm each one
    /// against the Pay Now ecommerce documentation for your account before going live, and set
    /// the accept, decline, notify and redirect URLs on the Netcash Pay Now service page.
    /// <para>
    /// The customer browser posts these fields; this application never sees card data, which
    /// is what keeps it inside PCI DSS SAQ A.
    /// </para>
    /// </summary>
    public Task<NetcashResult<PayNowCheckoutForm>> BuildCheckoutAsync(
        string payNowServiceKey,
        PayNowCheckoutRequest request,
        CancellationToken ct = default)
    {
        _log.LogWarning(
            "STUB Pay Now checkout for {Reference}, amount {Amount}. Field contract not yet verified against Netcash.",
            request.PaymentReference, request.Amount);

        var fields = new Dictionary<string, string>
        {
            ["m1"] = payNowServiceKey,
            ["m2"] = "24ade73c-98cf-47b3-99be-cc7b867b3080",
            ["p2"] = request.PaymentReference,
            ["p3"] = request.Description ?? "Payment",
            ["p4"] = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            ["m4"] = request.ExtraField1 ?? string.Empty,
            ["m5"] = request.ExtraField2 ?? string.Empty,
            ["m6"] = request.ExtraField3 ?? string.Empty,
            ["m9"] = request.CustomerEmail ?? string.Empty,
            ["m11"] = request.CustomerMobile ?? string.Empty,
            ["m14"] = "1"
        };

        return Task.FromResult(NetcashResult<PayNowCheckoutForm>.Ok(new PayNowCheckoutForm
        {
            PostUrl = _options.PayNowCheckoutUrl,
            Fields = fields
        }));
    }

    /// <summary>
    /// GAP - Pay Now callback verification.
    /// The notify callback is the authoritative one; accept and decline are browser redirects
    /// and can be forged or simply never arrive if the customer closes the tab. Verify the
    /// callback against the service key and reconcile against the Netcash statement before
    /// releasing anything of value.
    /// </summary>
    public Task<NetcashResult<PayNowCallback>> ParseCallbackAsync(
        string payNowServiceKey,
        IDictionary<string, string> form,
        CancellationToken ct = default)
    {
        _log.LogWarning("STUB Pay Now callback parse over {Count} field(s). Signature NOT verified.", form.Count);

        var callback = new PayNowCallback
        {
            PaymentReference = Get(form, "p2", "Reference"),
            TransactionAccepted = Get(form, "TransactionAccepted"),
            Reason = Get(form, "Reason"),
            NetcashTransactionId = Get(form, "RequestTrace", "TransactionId"),
            Method = Get(form, "Method"),
            Raw = new Dictionary<string, string>(form)
        };

        var amountRaw = Get(form, "p4", "Amount");
        if (decimal.TryParse(amountRaw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
            callback.Amount = amount;

        return Task.FromResult(NetcashResult<PayNowCallback>.Ok(callback));
    }

    private static string? Get(IDictionary<string, string> form, params string[] names)
    {
        foreach (var n in names)
        {
            if (form.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v;

            var match = form.FirstOrDefault(kv =>
                string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
        }
        return null;
    }
}
