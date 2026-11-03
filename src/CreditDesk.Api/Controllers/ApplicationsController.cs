using CreditDesk.Api.Contracts;
using CreditDesk.Api.Domain;
using CreditDesk.Api.Infrastructure;
using CreditDesk.Api.Security;
using CreditDesk.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreditDesk.Api.Controllers;

/// <summary>
/// Ciclo de vida de una solicitud de crédito. Las respuestas de detalle incluyen <c>ETag</c>;
/// los comandos aceptan <c>If-Match</c> para evitar sobrescribir cambios ajenos (412).
/// </summary>
[ApiController]
[Authorize]
[Route("api/applications")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class ApplicationsController(LoanApplicationService service) : ControllerBase
{
    private Actor CurrentActor => new(User.GetUserId(), User.IsInRole(Roles.Analyst) ? Roles.Analyst : Roles.Applicant);

    // ---- Consultas ----

    /// <summary>Solicitantes ven sólo las suyas; analistas ven todas (bandeja de trabajo).</summary>
    [HttpGet]
    public Task<PagedResponse<ApplicationSummaryResponse>> List([FromQuery] ApplicationQuery query, CancellationToken ct) =>
        service.ListAsync(CurrentActor, query, ct);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ApplicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        WithETag(await service.GetAsync(CurrentActor, id, ct));

    [HttpGet("{id:guid}/schedule")]
    public async Task<IReadOnlyList<AmortizationRow>> Schedule(Guid id, CancellationToken ct)
    {
        var application = await service.GetAsync(CurrentActor, id, ct);
        return Amortization.Schedule(application.Amount, application.Product.AnnualInterestRate, application.TermMonths);
    }

    // ---- Comandos del solicitante ----

    [HttpPost]
    [Authorize(Roles = Roles.Applicant)]
    [ProducesResponseType<ApplicationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(CreateApplicationRequest request, CancellationToken ct)
    {
        var application = await service.CreateAsync(CurrentActor, request, ct);
        Response.Headers.ETag = ETags.Format(application.Version);
        return CreatedAtAction(nameof(Get), new { id = application.Id }, ApplicationResponse.From(application));
    }

    [HttpPut("{id:guid}/terms")]
    [Authorize(Roles = Roles.Applicant)]
    public Task<IActionResult> UpdateTerms(Guid id, UpdateTermsRequest request, CancellationToken ct) =>
        Execute(id, (a, now) => a.UpdateTerms(CurrentActor.Id, request.Amount, request.TermMonths, request.MonthlyIncome, now), ct);

    [HttpPost("{id:guid}/guarantors")]
    [Authorize(Roles = Roles.Applicant)]
    public Task<IActionResult> AddGuarantor(Guid id, AddGuarantorRequest request, CancellationToken ct) =>
        Execute(id, (a, now) => a.AddGuarantor(CurrentActor.Id, request.FullName, request.Rfc, request.MonthlyIncome, now), ct);

    [HttpDelete("{id:guid}/guarantors/{guarantorId:guid}")]
    [Authorize(Roles = Roles.Applicant)]
    public Task<IActionResult> RemoveGuarantor(Guid id, Guid guarantorId, CancellationToken ct) =>
        Execute(id, (a, now) => a.RemoveGuarantor(CurrentActor.Id, guarantorId, now), ct);

    /// <summary>Registra el documento de un tipo en el expediente (reemplaza el anterior del mismo tipo).</summary>
    [HttpPut("{id:guid}/documents")]
    [Authorize(Roles = Roles.Applicant)]
    public Task<IActionResult> AttachDocument(Guid id, AttachDocumentRequest request, CancellationToken ct) =>
        Execute(id, (a, now) => a.AttachDocument(CurrentActor.Id, request.Type!.Value, request.FileName, now), ct);

    [HttpPost("{id:guid}/submit")]
    [Authorize(Roles = Roles.Applicant)]
    public Task<IActionResult> Submit(Guid id, CancellationToken ct) =>
        Execute(id, (a, now) => a.Submit(CurrentActor.Id, now), ct);

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = Roles.Applicant)]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        Execute(id, (a, now) => a.Cancel(CurrentActor.Id, now), ct);

    // ---- Comandos del analista ----

    /// <summary>Toma una solicitud enviada; si dos analistas la toman a la vez, sólo uno gana (409 para el otro).</summary>
    [HttpPost("{id:guid}/review")]
    [Authorize(Roles = Roles.Analyst)]
    public Task<IActionResult> StartReview(Guid id, CancellationToken ct) =>
        Execute(id, (a, now) => a.StartReview(CurrentActor.Id, now), ct);

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = Roles.Analyst)]
    public Task<IActionResult> Approve(Guid id, ApproveRequest request, CancellationToken ct) =>
        Execute(id, (a, now) => a.Approve(CurrentActor.Id, request.Note, now), ct);

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = Roles.Analyst)]
    public Task<IActionResult> Reject(Guid id, RejectRequest request, CancellationToken ct) =>
        Execute(id, (a, now) => a.Reject(CurrentActor.Id, request.Reason, now), ct);

    [HttpPost("{id:guid}/disburse")]
    [Authorize(Roles = Roles.Analyst)]
    public Task<IActionResult> Disburse(Guid id, CancellationToken ct) =>
        Execute(id, (a, now) => a.Disburse(CurrentActor.Id, now), ct);

    private async Task<IActionResult> Execute(Guid id, Action<LoanApplication, DateTime> action, CancellationToken ct)
    {
        var expected = ETags.Parse(Request.Headers.IfMatch.ToString());
        return WithETag(await service.ExecuteAsync(CurrentActor, id, expected, action, ct));
    }

    private OkObjectResult WithETag(LoanApplication application)
    {
        Response.Headers.ETag = ETags.Format(application.Version);
        return Ok(ApplicationResponse.From(application));
    }
}
