using AMPay.Domain.Credit;
using AMPay.Domain.Entities;
using AMPay.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace AMPay.Infrastructure.Credit;

/// <summary>
/// Stands in for Home Affairs until a verification provider is chosen. A valid SA ID number is
/// "alive"; anything else is inconclusive. Every answer is marked simulated and logged at Warning,
/// so it is never taken for a real population-register check.
/// </summary>
public class StubLifeStatusVerifier : ILifeStatusVerifier
{
    private readonly ILogger<StubLifeStatusVerifier> _log;

    public StubLifeStatusVerifier(ILogger<StubLifeStatusVerifier> log) => _log = log;

    public Task<LifeStatusAnswer> VerifyAsync(string idNumber, string surname, CancellationToken ct = default)
    {
        _log.LogWarning("STUB Home Affairs life check answered locally. No real check was made.");

        return Task.FromResult(SaIdNumber.IsValid(idNumber)
            ? new LifeStatusAnswer(VerificationResult.Confirmed, "Home Affairs (simulated)", null,
                "Test mode: no provider is connected, so this is not a real Home Affairs answer.", Simulated: true)
            : new LifeStatusAnswer(VerificationResult.Inconclusive, "Home Affairs (simulated)", null,
                "Not a valid SA ID number, so Home Affairs cannot be asked.", Simulated: true));
    }
}

/// <summary>
/// Answers when no AI key is configured. It reads nothing in the statement: it returns a Warning
/// that says so, marked simulated, so the screens and the good-to-go check can be tried end to end.
/// </summary>
public class StubBankStatementAnalyst : IBankStatementAnalyst
{
    private readonly ILogger<StubBankStatementAnalyst> _log;

    public StubBankStatementAnalyst(ILogger<StubBankStatementAnalyst> log) => _log = log;

    public bool IsLive => false;

    public Task<(BankStatementReading? Reading, string? Error)> AnalyseAsync(BankStatementInput input, CancellationToken ct = default)
    {
        _log.LogWarning("STUB bank statement assessment answered locally. The statement was not read.");

        var reading = new BankStatementReading(
            StatementFlag.Warning,
            "Test mode: no AI model is connected, so the statement was not read. Read it yourself before relying on it.",
            new[] { "Simulated result - switch on StatementAnalysis with an API key for a real assessment." },
            AverageMonthlyIncome: null,
            AverageMonthlySpend: null,
            Model: "stub",
            Simulated: true);
        return Task.FromResult<(BankStatementReading?, string?)>((reading, null));
    }
}
