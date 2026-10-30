using System.Xml.Linq;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Netcash;

namespace AMPay.Tests;

/// <summary>The live ValidateServiceKey call, without the network: what we send and how we read the answer.</summary>
public class LiveNetcashPartnerTests
{
    private const string Endpoint = "https://ws.netcash.co.za/NIWS/NIWS_Partner.svc";

    [Fact]
    public void TheEnvelope_IsSoap12_WithAddressing_AndEveryServiceKey()
    {
        var xml = LiveNetcashPartnerService.BuildEnvelope(Endpoint, "51234567890", "vendor-key",
            new Dictionary<NetcashServiceId, string> { [NetcashServiceId.DebitOrders] = "key-1", [NetcashServiceId.PayNow] = "key-<&>" });

        var doc = XDocument.Parse(xml); // well-formed, special characters escaped
        XNamespace s = "http://www.w3.org/2003/05/soap-envelope";
        XNamespace a = "http://www.w3.org/2005/08/addressing";
        XNamespace n = "http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS";

        Assert.Equal(LiveNetcashPartnerService.Action, doc.Descendants(a + "Action").Single().Value);
        Assert.Equal(Endpoint, doc.Descendants(a + "To").Single().Value);
        Assert.NotNull(doc.Root!.Element(s + "Body")!.Element(XName.Get("ValidateServiceKey", "http://tempuri.org/")));
        Assert.Equal("51234567890", doc.Descendants(n + "MerchantAccount").Single().Value);
        Assert.Equal(new[] { "1", "14" }, doc.Descendants(n + "ServiceId").Select(e => e.Value));
        Assert.Equal("key-<&>", doc.Descendants(n + "ServiceKey").Last().Value);
    }

    [Fact]
    public void AnAuthenticatedAccount_MapsEachServiceStatus()
    {
        var result = LiveNetcashPartnerService.ParseResponse(Response("001", ("1", "001"), ("5", "106"), ("14", "105")),
            new[] { NetcashServiceId.DebitOrders, NetcashServiceId.Account, NetcashServiceId.PayNow, NetcashServiceId.SalaryPayments });

        Assert.True(result.Success);
        Assert.Equal(
            new[] { ServiceKeyStatus.Validated, ServiceKeyStatus.NoActiveServiceKey, ServiceKeyStatus.NoActiveService, ServiceKeyStatus.ServiceError },
            result.Data!.Select(v => v.Status));
        Assert.Equal("Netcash did not report on this service.", result.Data![3].Message);
    }

    [Fact]
    public void AnAccountThatDoesNotAuthenticate_IsAFailure_WithItsCode()
    {
        var result = LiveNetcashPartnerService.ParseResponse(Response("100"), new[] { NetcashServiceId.DebitOrders });

        Assert.False(result.Success);
        Assert.Equal("100", result.Code);
        Assert.Contains("Authentication failure", result.Message);
    }

    [Fact]
    public void ASoapFault_IsReportedWithItsReason()
    {
        const string fault =
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault>" +
            "<s:Code><s:Value>s:Sender</s:Value></s:Code><s:Reason><s:Text xml:lang=\"en-US\">The message could not be processed.</s:Text></s:Reason>" +
            "</s:Fault></s:Body></s:Envelope>";

        var result = LiveNetcashPartnerService.ParseResponse(fault, new[] { NetcashServiceId.DebitOrders });

        Assert.False(result.Success);
        Assert.Equal(NetcashCodes.GeneralServiceError, result.Code);
        Assert.Contains("could not be processed", result.Message);
    }

    [Fact]
    public void Garbage_IsAFailure_NotAnException() =>
        Assert.False(LiveNetcashPartnerService.ParseResponse("<html>gateway error", new[] { NetcashServiceId.DebitOrders }).Success);

    private static string Response(string accountStatus, params (string Id, string Status)[] services) =>
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
        "<ValidateServiceKeyResponse xmlns=\"http://tempuri.org/\">" +
        "<ValidateServiceKeyResult xmlns:b=\"http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS\">" +
        $"<b:AccountStatus>{accountStatus}</b:AccountStatus><b:MerchantAccount>51234567890</b:MerchantAccount>" +
        "<b:ServiceInfo>" +
        string.Concat(services.Select(x =>
            $"<b:ServiceInfoResponse><b:ServiceId>{x.Id}</b:ServiceId><b:ServiceKey>echoed</b:ServiceKey><b:ServiceStatus>{x.Status}</b:ServiceStatus></b:ServiceInfoResponse>")) +
        "</b:ServiceInfo><b:SoftwareVendorKey>v</b:SoftwareVendorKey>" +
        "</ValidateServiceKeyResult></ValidateServiceKeyResponse></s:Body></s:Envelope>";
}

public class NetcashLockoutGuardTests
{
    [Fact]
    public void TwoFailuresInTenMinutes_BlockAThirdAttempt_UntilTheWindowPasses()
    {
        var account = $"5{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}";
        var t0 = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

        LiveNetcashPartnerService.RecordFailure(account, t0);
        Assert.Null(LiveNetcashPartnerService.BlockedUntil(account, t0.AddMinutes(1)));

        LiveNetcashPartnerService.RecordFailure(account, t0.AddMinutes(2));
        Assert.Equal(t0.AddMinutes(10), LiveNetcashPartnerService.BlockedUntil(account, t0.AddMinutes(3)));

        // Ten minutes after the first failure it drops out, leaving one: allowed again.
        Assert.Null(LiveNetcashPartnerService.BlockedUntil(account, t0.AddMinutes(10).AddSeconds(1)));
    }
}

public class NetcashKeyNamingTests
{
    private sealed class Store : INetcashSecretStore
    {
        private readonly Dictionary<string, string> _values;
        public Store(Dictionary<string, string> values) => _values = values;
        public Task<string?> GetServiceKeyAsync(string secretName, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(secretName, out var v) ? v : null);
        public Task<string> GetSoftwareVendorKeyAsync(CancellationToken ct = default) => Task.FromResult("v");
    }

    [Fact]
    public async Task AKeyStoredByAccountNumber_IsFound_WhenTheSlotNameIsNotSet()
    {
        var store = new Store(new() { ["Netcash:ServiceKeys:51234567890:1"] = "by-account" });

        Assert.Equal("by-account", await store.ResolveServiceKeyAsync("Netcash:ServiceKeys:some-tenant-id:1", "51234567890", NetcashServiceId.DebitOrders));
        Assert.Null(await store.ResolveServiceKeyAsync("Netcash:ServiceKeys:some-tenant-id:14", "51234567890", NetcashServiceId.PayNow));
        Assert.Null(await store.ResolveServiceKeyAsync("Netcash:ServiceKeys:some-tenant-id:1", null, NetcashServiceId.DebitOrders));
    }

    [Fact]
    public async Task TheSlotsOwnName_WinsOverTheAccountNumberName()
    {
        var store = new Store(new() { ["slot"] = "slot-value", ["Netcash:ServiceKeys:51234567890:1"] = "by-account" });
        Assert.Equal("slot-value", await store.ResolveServiceKeyAsync("slot", "51234567890", NetcashServiceId.DebitOrders));
    }
}

/// <summary>The live bank, ID and AVS checks, without the network: how Netcash's answers are read.</summary>
public class LiveNetcashValidationTests
{
    [Theory]
    [InlineData("0", true, "0")]
    [InlineData("1", false, "1")]
    [InlineData("2", false, "2")]
    [InlineData("100", false, "100")]
    [InlineData("200", false, "200")]
    [InlineData("77", false, "200")]
    public void BankAccountCodes_AreMapped(string code, bool ok, string expectedCode)
    {
        var r = LiveNetcashValidationService.BankAccountResult(code);
        Assert.Equal(ok, r.Success);
        Assert.Equal(expectedCode, r.Code);
        if (!ok) Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    [Fact]
    public void IdCodes_AreMapped()
    {
        Assert.True(LiveNetcashValidationService.IdResult("0").Success);
        Assert.False(LiveNetcashValidationService.IdResult("1").Success);
        Assert.Contains("not accepted", LiveNetcashValidationService.IdResult("100").Message);
    }

    // The two shapes Netcash returned on 16 Oct 2026 (bank names as Netcash spells them).

    [Fact]
    public void TheDebiCheckBankList_IsReadAsNames()
    {
        var r = LiveNetcashValidationService.BankList(
            "<BankListResponse><BankList><BankName>ABSA</BankName><BankName>CAPITEC BANK</BankName><BankName>TYMEBANK</BankName></BankList>" +
            "<Errors><ErrorCode>000</ErrorCode></Errors></BankListResponse>", debiCheck: true);

        Assert.True(r.Success);
        Assert.Equal(new[] { "ABSA", "CAPITEC BANK", "TYMEBANK" }, r.Data!.Select(b => b.Name));
        Assert.All(r.Data!, b => Assert.True(b.SupportsDebiCheck));
        Assert.All(r.Data!, b => Assert.Null(b.DefaultBranchCode));
    }

    [Fact]
    public void TheFullBankList_IsReadWithDefaultBranchCodes()
    {
        var r = LiveNetcashValidationService.BankList(
            "<BankListResponse><BankList><Bank><BankName>ABSA</BankName><DefaultCode>632005</DefaultCode></Bank>" +
            "<Bank><BankName>CAPITEC BANK</BankName><DefaultCode>470010</DefaultCode></Bank></BankList>" +
            "<Errors><ErrorCode>000</ErrorCode></Errors></BankListResponse>", debiCheck: false);

        Assert.True(r.Success);
        Assert.Equal("632005", r.Data!.Single(b => b.Name == "ABSA").DefaultBranchCode);
        Assert.Equal("470010", r.Data!.Single(b => b.Name == "CAPITEC BANK").DefaultBranchCode);
    }

    [Fact]
    public void ABankListWithAnErrorCode_IsAFailure() =>
        Assert.Equal("100", LiveNetcashValidationService.BankList(
            "<BankListResponse><BankList/><Errors><ErrorCode>100</ErrorCode></Errors></BankListResponse>", debiCheck: true).Code);

    [Fact]
    public void ABankListThatIsJustACode_IsAnError() =>
        Assert.False(LiveNetcashValidationService.BankList("100", debiCheck: false).Success);

    [Fact]
    public void AvsAnswers_MapValidInvalidAndNotApplicable()
    {
        var r = LiveNetcashValidationService.ParseAvs(Avs(
            "<b:AcceptsDebits>Valid</b:AcceptsDebits><b:AccountActive>Valid</b:AccountActive><b:BankAccountNumberValid>Valid</b:BankAccountNumberValid>" +
            "<b:IdNumberMatch>Invalid</b:IdNumberMatch><b:InitialMatch>NotApplicable</b:InitialMatch><b:FileToken>abc-123</b:FileToken><b:OfflineRequest>false</b:OfflineRequest>"));

        Assert.True(r.Success);
        Assert.True(r.Data!.AccountExists);
        Assert.True(r.Data.AccountAcceptsDebits);
        Assert.False(r.Data.IdNumberMatches);
        Assert.Null(r.Data.InitialsMatch);
        Assert.Null(r.Data.SurnameMatches); // not in the answer at all
        Assert.Equal("abc-123", r.Data.Reference);
        Assert.False(r.Data.AnswerPending);
    }

    [Fact]
    public void AnAvsAnswerWithOnlyMessages_IsARefusal_WithTheMessages()
    {
        var r = LiveNetcashValidationService.ParseAvs(Avs(
            "<b:Messages xmlns:c=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\"><c:string>Invalid service key</c:string></b:Messages><b:OfflineRequest>false</b:OfflineRequest>"));
        Assert.False(r.Success);
        Assert.Contains("Invalid service key", r.Message);
    }

    [Fact]
    public void AnOfflineAvsAnswer_IsPending_NotAFailure()
    {
        var r = LiveNetcashValidationService.ParseAvs(Avs("<b:FileToken>tok</b:FileToken><b:OfflineRequest>true</b:OfflineRequest>"));
        Assert.True(r.Success);
        Assert.True(r.Data!.AnswerPending);
    }

    [Fact]
    public void ASoap11Fault_IsReported() =>
        Assert.Contains("Bad thing", LiveNetcashValidationService.ParseAvs(
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Bad thing</faultstring></s:Fault></s:Body></s:Envelope>").Message);

    private static string Avs(string inner) =>
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><AVSRealtimeQueryResponse xmlns=\"http://tempuri.org/\">" +
        $"<AVSRealtimeQueryResult xmlns:b=\"http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS\">{inner}</AVSRealtimeQueryResult>" +
        "</AVSRealtimeQueryResponse></s:Body></s:Envelope>";
}

/// <summary>The live DebiCheck calls, without the network: what TT1 sends and how answers are read.</summary>
public class LiveNetcashDebiCheckTests
{
    private const string Tempuri = "http://tempuri.org/";

    private static DebiCheckAuthenticateRequest Request() => new()
    {
        AccountReference = "C000014",
        MandateTemplateId = " NCDCT000000001 ",
        IsIdNumber = true,
        DebtorIdentification = "8506155009085",
        AccountName = "Thandi Testperson",
        BankAccountName = "T TESTPERSON",
        BranchCode = "632005",
        BankAccountNumber = "4012345678",
        BankAccountType = BankAccountType.Savings,
        MobileNumber = "0820000000",
        CollectionAmount = 1554.5m,
        FirstCollectionDate = new DateTime(2026, 11, 25)
    };

    [Fact]
    public void TheTt1Request_HasNetcashsFieldsInOrder_AndItsFormats()
    {
        var xml = XDocument.Parse(LiveNetcashDebiCheckService.BuildAuthenticate("key", Request()));
        var names = xml.Root!.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal(new[]
        {
            "ServiceKey", "AccountReference", "DebiCheckMandateTemplateId", "IsIdNumber", "DebtorIdentification", "AccountName",
            "BankAccountName", "BranchCode", "BankAccountNumber", "BankAccountType", "MobileNumber", "EmailAddress",
            "CollectionAmount", "FirstCollectionDiffers", "FirstCollectionDate", "collectionDayCode", "DebtorIdentificationCode"
        }, names);

        string V(string n) => xml.Root!.Element(XName.Get(n, Tempuri))!.Value;
        Assert.Equal("NCDCT000000001", V("DebiCheckMandateTemplateId"));
        Assert.Equal("Savings", V("BankAccountType"));
        Assert.Equal("1554.50", V("CollectionAmount"));
        Assert.Equal("20261125", V("FirstCollectionDate"));
        Assert.Equal("MNTH_25", V("collectionDayCode"));
        Assert.Equal("I", V("DebtorIdentificationCode"));
    }

    [Theory]
    [InlineData("MNTH_05", null, "MNTH_05")]
    [InlineData("mnth_31", null, "MNTH_31")]
    [InlineData("25", null, "MNTH_25")]
    [InlineData(null, "2026-11-07", "MNTH_07")]
    [InlineData("rubbish", null, null)]
    public void DayCodes_AreNormalised(string? code, string? first, string? expected) =>
        Assert.Equal(expected, LiveNetcashDebiCheckService.DayCode(code, first is null ? null : DateTime.Parse(first)));

    [Fact]
    public void AnAcceptedTt1_IsReadWithItsCodes()
    {
        var r = LiveNetcashDebiCheckService.ParseAuthenticate(Answer("DebiCheckAuthenticateResult",
            "<b:BankResponseCode>900000</b:BankResponseCode><b:BankservResponseCode>ACCP</b:BankservResponseCode>" +
            "<b:ClientResponseCode>TRUE</b:ClientResponseCode><b:ContractReference>REF123</b:ContractReference><b:Status>Accepted</b:Status>"));

        Assert.True(r.Success);
        Assert.True(r.Data!.IsAccepted);
        Assert.Equal("REF123", r.Data.ContractReference);
    }

    [Fact]
    public void ARejectedTt1_IsAnAnswer_NotAFailure()
    {
        var r = LiveNetcashDebiCheckService.ParseAuthenticate(Answer("DebiCheckAuthenticateResult",
            "<b:BankResponseCode>900001</b:BankResponseCode><b:BankservResponseCode>RJCT</b:BankservResponseCode><b:ContractReference>REF9</b:ContractReference><b:Status>Rejected</b:Status>"));
        Assert.True(r.Success);
        Assert.False(r.Data!.IsAccepted);
    }

    [Fact]
    public void ATt1RefusedByNetcash_CarriesItsMessagesAndCode()
    {
        var r = LiveNetcashDebiCheckService.ParseAuthenticate(Answer("DebiCheckAuthenticateResult",
            "<b:Messages xmlns:c=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\"><c:string>325 - Template not real-time</c:string></b:Messages>"));
        Assert.False(r.Success);
        Assert.Equal("325", r.Code);
        Assert.Contains("Template not real-time", r.Message);
    }

    [Fact]
    public void TheStatus_IsReadWithTheBankCodesAndCancellation()
    {
        var r = LiveNetcashDebiCheckService.ParseStatus(Answer("DebiCheckAuthenticationCurrentStatusResult",
            "<b:CancellationReason>CUST</b:CancellationReason><b:ContractReference>REF1</b:ContractReference><b:DateCancelled>20261101</b:DateCancelled>" +
            "<b:Status>Cancelled</b:Status><b:BankservResponse>ACCP</b:BankservResponse>"));
        Assert.True(r.Success);
        Assert.Equal("Cancelled", r.Data);
        Assert.Contains("cancelled 20261101 (CUST)", r.Message);
    }

    [Fact]
    public void AnUnknownTemplate_IsNotOffered()
    {
        Assert.Null(LiveNetcashDebiCheckService.TemplateDetail(Answer("DebiCheckRetrieveMandateTemplateDetailResult", "<b:TemplateId></b:TemplateId>")));
        Assert.Equal(("Monthly loans", "REALTIME"), LiveNetcashDebiCheckService.TemplateDetail(Answer("DebiCheckRetrieveMandateTemplateDetailResult",
            "<b:AuthenticationType>REALTIME</b:AuthenticationType><b:TemplateId>NCDCT000000001</b:TemplateId><b:TemplateName>Monthly loans</b:TemplateName>")));
    }

    private static string Answer(string result, string inner) =>
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><Response xmlns=\"http://tempuri.org/\">" +
        $"<{result} xmlns:b=\"http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS\">{inner}</{result}>" +
        "</Response></s:Body></s:Envelope>";
}

/// <summary>Pay Now's notify must prove it belongs to a checkout AM-Pay created.</summary>
public class LivePayNowCallbackTests
{
    private const string Secret = "test-secret-0123456789-abcdefghijklmnop";

    private static Dictionary<string, string> Notify(string reference, string amount, string? check) => new()
    {
        ["TransactionAccepted"] = "true",
        ["Reference"] = reference,
        ["Amount"] = amount,
        ["RequestTrace"] = "TRACE-1",
        ["Method"] = "1",
        ["Extra3"] = check ?? ""
    };

    [Fact]
    public void ANotifyWithTheRightCheckValue_IsAccepted()
    {
        var check = LiveNetcashPayNowService.CheckValue(Secret, "AMP-1", "150.00");
        var r = LiveNetcashPayNowService.Parse(Secret, Notify("AMP-1", "150.00", check));

        Assert.True(r.Success);
        Assert.True(r.Data!.IsAccepted);
        Assert.Equal(150m, r.Data.Amount);
        Assert.Equal("TRACE-1", r.Data.NetcashTransactionId);
    }

    [Fact]
    public void AnAmountWrittenDifferently_StillVerifies() =>
        Assert.True(LiveNetcashPayNowService.Parse(Secret,
            Notify("AMP-1", "150", LiveNetcashPayNowService.CheckValue(Secret, "AMP-1", "150.00"))).Success);

    [Theory]
    [InlineData(null)]
    [InlineData("0000000000000000000000000000000")]
    public void AForgedOrMissingCheckValue_IsRefused(string? check) =>
        Assert.False(LiveNetcashPayNowService.Parse(Secret, Notify("AMP-1", "150.00", check)).Success);

    [Fact]
    public void ACheckValueForAnotherAmount_IsRefused()
    {
        // Someone reuses a genuine check value but claims a bigger payment.
        var check = LiveNetcashPayNowService.CheckValue(Secret, "AMP-1", "15.00");
        Assert.False(LiveNetcashPayNowService.Parse(Secret, Notify("AMP-1", "150.00", check)).Success);
    }
}

/// <summary>Credit checks through Netcash risk reports: the request file and the report answer.</summary>
public class NetcashRiskReportTests
{
    [Fact]
    public void TheRequestFile_FollowsTheRiskReportLayout()
    {
        var file = NetcashRiskReportService.BuildFile("svc-key", "vendor-key",
            new CreditCheckRequest("0001", "8506155009085", "Test-Molefe!", "Lerato", CreditBureau.Experian), new DateTime(2026, 10, 30));
        var lines = file.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();

        Assert.Equal(new[] { "H", "svc-key", "1", "CD12", "CC-0001", "20261030", "vendor-key" }, lines[0]);
        Assert.Equal(new[] { "K", "101", "111", "113", "114", "450" }, lines[1]);
        Assert.Equal(new[] { "T", "0001", "8506155009085", "Test-Molefe", "Lerato", "32" }, lines[2]); // no special characters
        Assert.Equal(new[] { "F", "1", "0", "9999" }, lines[3]);
    }

    [Theory]
    [InlineData(CreditBureau.TransUnion, "CD11")]
    [InlineData(CreditBureau.Experian, "CD12")]
    [InlineData(CreditBureau.Xds, "CD13")]
    public void EachBureau_HasItsInstruction(CreditBureau bureau, string instruction) =>
        Assert.Equal(instruction, NetcashRiskReportService.Instruction(bureau));

    private static string Answer(params string[] parts) =>
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><RequestCreditDataReportResponse xmlns=\"http://tempuri.org/\">" +
        "<RequestCreditDataReportResult xmlns:a=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\">" +
        string.Concat(parts.Select(p => $"<a:string>{p}</a:string>")) +
        "</RequestCreditDataReportResult></RequestCreditDataReportResponse></s:Body></s:Envelope>";

    [Fact]
    public void FileNotReady_IsSuccessWithNoReportYet()
    {
        var r = NetcashRiskReportService.ParseReport(Answer("FILE NOT READY"));
        Assert.True(r.Success);
        Assert.Null(r.Data);
    }

    [Fact]
    public void ABase64Pdf_IsDecoded_EvenWhenSplitAcrossStrings()
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 a credit report"));
        var r = NetcashRiskReportService.ParseReport(Answer(b64[..10], b64[10..]));
        Assert.True(r.Success);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(r.Data!));
    }

    [Fact]
    public void AnErrorCode_OrSomethingThatIsNotAPdf_IsAFailure()
    {
        Assert.Equal("100", NetcashRiskReportService.ParseReport(Answer("100")).Code);
        Assert.False(NetcashRiskReportService.ParseReport(Answer("Batch rejected: invalid reason code")).Success);
    }
}
