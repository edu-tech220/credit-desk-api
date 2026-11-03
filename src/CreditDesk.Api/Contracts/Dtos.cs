using System.ComponentModel.DataAnnotations;
using CreditDesk.Api.Domain;

namespace CreditDesk.Api.Contracts;

// ---- Autenticación ----

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(200)] string FullName,
    [Required, MinLength(8), MaxLength(128)] string Password);

public sealed record LoginRequest([Required] string Email, [Required] string Password);

public sealed record AuthResponse(Guid UserId, string Email, string Role, string AccessToken, DateTime ExpiresAt);

// ---- Catálogo ----

public sealed record ProductResponse(
    string Code,
    string Name,
    decimal MinAmount,
    decimal MaxAmount,
    decimal AnnualInterestRate,
    decimal MaxPaymentToIncomeRatio,
    decimal? GuarantorThreshold,
    IReadOnlyList<int> AllowedTerms,
    IReadOnlyList<DocumentType> RequiredDocuments);

public sealed record QuoteResponse(string ProductCode, decimal Amount, int TermMonths, decimal MonthlyPayment,
    decimal TotalPayment, decimal TotalInterest, IReadOnlyList<AmortizationRow> Schedule);

// ---- Solicitudes ----

public sealed record CreateApplicationRequest(
    [Required] string ProductCode,
    [Range(typeof(decimal), "0.01", "100000000")] decimal Amount,
    [Range(1, 120)] int TermMonths,
    [Range(typeof(decimal), "0.01", "100000000")] decimal MonthlyIncome);

public sealed record UpdateTermsRequest(
    [Range(typeof(decimal), "0.01", "100000000")] decimal Amount,
    [Range(1, 120)] int TermMonths,
    [Range(typeof(decimal), "0.01", "100000000")] decimal MonthlyIncome);

public sealed record AddGuarantorRequest(
    [Required, MaxLength(200)] string FullName,
    [Required, MaxLength(13)] string Rfc,
    [Range(typeof(decimal), "0.01", "100000000")] decimal MonthlyIncome);

public sealed record AttachDocumentRequest([Required] DocumentType? Type, [Required, MaxLength(260)] string FileName);

public sealed record ApproveRequest([MaxLength(500)] string? Note);

public sealed record RejectRequest([Required, MaxLength(500)] string Reason);

public sealed record GuarantorResponse(Guid Id, string FullName, string Rfc, decimal MonthlyIncome);

public sealed record DocumentResponse(Guid Id, DocumentType Type, string FileName, DateTime UploadedAt);

public sealed record StatusChangeResponse(ApplicationStatus? From, ApplicationStatus To, Guid ActorId, DateTime At, string? Note);

public sealed record ApplicationSummaryResponse(
    Guid Id,
    string ProductCode,
    decimal Amount,
    int TermMonths,
    ApplicationStatus Status,
    Guid ApplicantId,
    Guid? AssignedAnalystId,
    DateTime CreatedAt,
    DateTime? SubmittedAt);

public sealed record ApplicationResponse(
    Guid Id,
    string ProductCode,
    decimal Amount,
    int TermMonths,
    decimal MonthlyIncome,
    decimal MonthlyPayment,
    ApplicationStatus Status,
    Guid ApplicantId,
    Guid? AssignedAnalystId,
    string? DecisionNote,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SubmittedAt,
    DateTime? DecidedAt,
    string Version,
    IReadOnlyList<GuarantorResponse> Guarantors,
    IReadOnlyList<DocumentResponse> Documents,
    IReadOnlyList<StatusChangeResponse> History)
{
    public static ApplicationResponse From(LoanApplication a) => new(
        a.Id,
        a.Product.Code,
        a.Amount,
        a.TermMonths,
        a.MonthlyIncome,
        a.MonthlyPayment,
        a.Status,
        a.ApplicantId,
        a.AssignedAnalystId,
        a.DecisionNote,
        a.CreatedAt,
        a.UpdatedAt,
        a.SubmittedAt,
        a.DecidedAt,
        a.Version.ToString("N"),
        a.Guarantors.Select(g => new GuarantorResponse(g.Id, g.FullName, g.Rfc, g.MonthlyIncome)).ToList(),
        a.Documents.OrderBy(d => d.Type).Select(d => new DocumentResponse(d.Id, d.Type, d.FileName, d.UploadedAt)).ToList(),
        a.History.OrderBy(h => h.At).ThenBy(h => h.Id)
            .Select(h => new StatusChangeResponse(h.From, h.To, h.ActorId, h.At, h.Note)).ToList());
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed class ApplicationQuery
{
    public ApplicationStatus? Status { get; init; }
    public string? ProductCode { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 50)]
    public int PageSize { get; init; } = 10;
}
