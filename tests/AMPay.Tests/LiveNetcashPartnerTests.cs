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
