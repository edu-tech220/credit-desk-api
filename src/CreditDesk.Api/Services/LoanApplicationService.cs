using CreditDesk.Api.Contracts;
using CreditDesk.Api.Data;
using CreditDesk.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CreditDesk.Api.Services;

/// <summary>Usuario que ejecuta la operación, ya resuelto desde el token.</summary>
public sealed record Actor(Guid Id, string Role)
{
    public bool IsAnalyst => Role == Roles.Analyst;
}

/// <summary>
/// Orquesta los casos de uso: carga el agregado respetando la visibilidad del usuario,
/// valida la versión esperada (If-Match), delega la regla al dominio y persiste
/// traduciendo los conflictos de concurrencia a errores de negocio.
/// </summary>
public sealed class LoanApplicationService(CreditDeskDbContext db, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<LoanApplication> CreateAsync(Actor actor, CreateApplicationRequest request, CancellationToken ct)
    {
        var code = request.ProductCode.Trim().ToUpperInvariant();
        var product = await db.Products.SingleOrDefaultAsync(p => p.Code == code, ct)
            ?? throw new BusinessRuleException("productCode", "El producto no existe.");

        if (await HasOpenApplicationAsync(actor.Id, product.Id, ct))
            throw OpenApplicationConflict();

        var application = LoanApplication.Create(actor.Id, product, request.Amount, request.TermMonths,
            request.MonthlyIncome, Now);
        db.Applications.Add(application);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Otra petición simultánea pudo ganar la carrera: el índice único filtrado lo impide en la base.
            db.Entry(application).State = EntityState.Detached;
            if (await HasOpenApplicationAsync(actor.Id, product.Id, CancellationToken.None))
                throw OpenApplicationConflict();
            throw;
        }

        return application;
    }

    public async Task<LoanApplication> GetAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var application = await db.Applications
            .Include(a => a.Product)
            .SingleOrDefaultAsync(a => a.Id == id, ct);

        // Un solicitante nunca sabe si existe una solicitud ajena: recibe 404, no 403.
        if (application is null || (!actor.IsAnalyst && application.ApplicantId != actor.Id))
            throw new NotFoundException("La solicitud no existe.");

        return application;
    }

    public async Task<PagedResponse<ApplicationSummaryResponse>> ListAsync(Actor actor, ApplicationQuery query,
        CancellationToken ct)
    {
        var applications = db.Applications.AsNoTracking();

        if (!actor.IsAnalyst)
            applications = applications.Where(a => a.ApplicantId == actor.Id);
        if (query.Status is { } status)
            applications = applications.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(query.ProductCode))
        {
            var code = query.ProductCode.Trim().ToUpperInvariant();
            applications = applications.Where(a => a.Product.Code == code);
        }

        // La bandeja de "Enviadas" se atiende en orden de llegada (FIFO); el resto, lo más reciente primero.
        applications = query.Status == ApplicationStatus.Submitted
            ? applications.OrderBy(a => a.SubmittedAt).ThenBy(a => a.Id)
            : applications.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id);

        var total = await applications.CountAsync(ct);
        var items = await applications
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(a => new ApplicationSummaryResponse(a.Id, a.Product.Code, a.Amount, a.TermMonths, a.Status,
                a.ApplicantId, a.AssignedAnalystId, a.CreatedAt, a.SubmittedAt))
            .ToListAsync(ct);

        return new PagedResponse<ApplicationSummaryResponse>(items, query.Page, query.PageSize, total);
    }

    /// <summary>
    /// Ejecuta una acción del dominio sobre la solicitud y la persiste.
    /// <paramref name="expectedVersion"/> viene del encabezado If-Match (opcional).
    /// </summary>
    public async Task<LoanApplication> ExecuteAsync(Actor actor, Guid id, string? expectedVersion,
        Action<LoanApplication, DateTime> action, CancellationToken ct)
    {
        var application = await GetAsync(actor, id, ct);

        if (expectedVersion is not null && expectedVersion != application.Version.ToString("N"))
            throw new PreconditionFailedException("La solicitud cambió desde la última lectura; vuelve a consultarla.");

        action(application, Now);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("concurrency_conflict",
                "Otro usuario modificó la solicitud al mismo tiempo; vuelve a consultarla.");
        }

        return application;
    }

    private Task<bool> HasOpenApplicationAsync(Guid applicantId, int productId, CancellationToken ct) =>
        db.Applications.AsNoTracking().AnyAsync(a => a.ApplicantId == applicantId && a.ProductId == productId && a.IsOpen, ct);

    private static ConflictException OpenApplicationConflict() =>
        new("open_application_exists", "Ya tienes una solicitud abierta para este producto.");
}
