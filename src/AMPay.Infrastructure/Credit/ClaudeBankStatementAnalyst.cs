using System.Text.Json;
using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Credit;

/// <summary>
/// The AI bank-statement assessment. Off until an API key is set; the stub answers meanwhile.
/// <para>
/// POPIA s72: the statement leaves South Africa for the model provider. The screen will not send
/// it without the operator recording the client's consent to exactly that.
/// </para>
/// </summary>
public class StatementAnalysisOptions
{
    public const string SectionName = "StatementAnalysis";

    /// <summary>Anthropic API key. Key Vault or user-secrets only - never appsettings.json.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Sends the statement (PDF or photo) to Claude with a fixed brief and reads back a JSON verdict.
/// One request per statement; nothing is kept by the provider beyond its standard retention.
/// </summary>
public class ClaudeBankStatementAnalyst : IBankStatementAnalyst
{
    private const string Brief = """
        You assess a South African consumer's bank statement for a registered credit provider deciding
        whether to lend. Read every page. Report how the account holder manages money, citing what you see:

        - salary or other regular income: amount, payer, and whether it arrives on a regular day
        - returned or unpaid debit orders, bounced payments, "insufficient funds" fees
        - other lenders: loan instalments, payday or micro-lenders, debt collectors, garnishee orders
        - gambling, betting, lottery spend
        - overdraft use, the balance running to or below zero before payday
        - cash withdrawals taken almost as soon as income lands

        Flag "Good" when income is regular and none of the above is material, "Warning" when there are
        signs a lender should ask about, "Bad" when the statement shows the person cannot keep up with what
        they already owe. Work only from the statement: if something cannot be seen, say so rather than guess.
        Amounts in rand. Keep the summary to three sentences a loan officer can read at a glance.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            flag = new { type = "string", @enum = new[] { "Good", "Warning", "Bad" } },
            summary = new { type = "string" },
            observations = new { type = "array", items = new { type = "string" } },
            average_monthly_income = new { type = new[] { "number", "null" } },
            average_monthly_spend = new { type = new[] { "number", "null" } }
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[]
            { "flag", "summary", "observations", "average_monthly_income", "average_monthly_spend" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
    };

    private readonly StatementAnalysisOptions _options;
    private readonly ILogger<ClaudeBankStatementAnalyst> _log;

    public ClaudeBankStatementAnalyst(IOptions<StatementAnalysisOptions> options, ILogger<ClaudeBankStatementAnalyst> log)
    {
        _options = options.Value;
        _log = log;
    }

    public bool IsLive => true;

    public async Task<(BankStatementReading? Reading, string? Error)> AnalyseAsync(
        BankStatementInput input, CancellationToken ct = default)
    {
        BetaContentBlockParam statement;
        var data = Convert.ToBase64String(input.Content);
        switch (input.ContentType)
        {
            case "application/pdf":
                statement = new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data } };
                break;
            case "image/jpeg" or "image/png" or "image/webp":
                statement = new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource
                    {
                        Data = data,
                        MediaType = input.ContentType switch
                        {
                            "image/png" => MediaType.ImagePng,
                            "image/webp" => MediaType.ImageWebP,
                            _ => MediaType.ImageJpeg
                        }
                    }
                };
                break;
            default:
                return (null, "Only a PDF statement or a photo (JPG, PNG, WebP) can be assessed.");
        }

        var context = input.DeclaredNetIncome is { } net
            ? $"The applicant declared a net monthly income of R {net:N2}. Say whether the statement bears that out."
            : "The applicant has not declared an income yet.";

        var client = new AnthropicClient { ApiKey = _options.ApiKey };

        try
        {
            var response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = 16000,
                // Re-serves a policy decline on Anthropic's recommended fallback model instead of failing.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                System = Brief,
                OutputConfig = new BetaOutputConfig
                {
                    Effort = Effort.Medium,
                    Format = new BetaJsonOutputFormat { Schema = Schema }
                },
                Messages =
                [
                    new BetaUserMessageParam
                    {
                        Content = new List<BetaContentBlockParam> { statement, new BetaTextBlockParam { Text = context } }
                    }
                ]
            }, ct);

            if (response.StopReason == "refusal")
            {
                _log.LogWarning("Statement assessment declined by the model ({Category}).", response.StopDetails?.Category);
                return (null, "The AI model declined to assess this statement. Assess it by hand.");
            }
            if (response.StopReason == "max_tokens")
                return (null, "The assessment ran out of room before it finished. Try a shorter statement.");

            var json = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            return (Parse(json, _options.Model), null);
        }
        catch (AnthropicRateLimitException)
        {
            return (null, "The AI service is busy. Try again in a minute.");
        }
        catch (AnthropicApiException ex)
        {
            _log.LogWarning("Statement assessment failed: {Message}", ex.Message);
            return (null, "The AI service could not assess the statement just now.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning("Statement assessment failed: {Message}", ex.Message);
            return (null, "The AI service could not be reached, or answered in a form that could not be read.");
        }
    }

    /// <summary>Reads the model's JSON verdict. Public for the tests.</summary>
    public static BankStatementReading Parse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        decimal? Money(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDecimal(), 2) : null;

        var flag = Enum.TryParse<StatementFlag>(root.GetProperty("flag").GetString(), ignoreCase: true, out var f)
            ? f : StatementFlag.Warning;
        var observations = root.TryGetProperty("observations", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : new List<string>();

        return new BankStatementReading(
            flag,
            root.GetProperty("summary").GetString() ?? "",
            observations,
            Money("average_monthly_income"),
            Money("average_monthly_spend"),
            model,
            Simulated: false);
    }
}
