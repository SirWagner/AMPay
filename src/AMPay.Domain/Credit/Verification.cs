using AMPay.Domain.Entities;

namespace AMPay.Domain.Credit;

/// <summary>What a life-status provider said about an ID number.</summary>
public record LifeStatusAnswer(VerificationResult Result, string Method, string? Reference, string? Detail, bool Simulated);

/// <summary>
/// Checks an SA ID number against the Home Affairs population register: is this a living person?
/// <para>
/// GAP - provider not chosen. Home Affairs is not queried directly by lenders; the check comes
/// through a bureau or a verification vendor that holds a DHA connection. Until one is picked
/// the stub answers, and every answer it gives is marked simulated.
/// </para>
/// </summary>
public interface ILifeStatusVerifier
{
    Task<LifeStatusAnswer> VerifyAsync(string idNumber, string surname, CancellationToken ct = default);
}

/// <summary>A bank statement to assess, as the file bytes the client gave.</summary>
public record BankStatementInput(byte[] Content, string ContentType, string ClientName, decimal? DeclaredNetIncome);

/// <summary>What the analyst read in the statement.</summary>
public record BankStatementReading(
    StatementFlag Flag,
    string Summary,
    IReadOnlyList<string> Observations,
    decimal? AverageMonthlyIncome,
    decimal? AverageMonthlySpend,
    string Model,
    bool Simulated);

/// <summary>
/// Reads a client's bank statement and says how they handle money: salary regularity, bounced
/// debits, gambling, payday lenders, overdraft use. Advice for the lender, never a decision.
/// </summary>
public interface IBankStatementAnalyst
{
    /// <summary>True when a real model is configured; false when the stub answers.</summary>
    bool IsLive { get; }

    /// <summary>Null with a reason when the statement could not be read.</summary>
    Task<(BankStatementReading? Reading, string? Error)> AnalyseAsync(BankStatementInput input, CancellationToken ct = default);
}
