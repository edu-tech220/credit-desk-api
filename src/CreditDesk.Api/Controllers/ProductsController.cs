using CreditDesk.Api.Contracts;
using CreditDesk.Api.Data;
using CreditDesk.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CreditDesk.Api.Controllers;

[ApiController]
[Route("api/products")]
public sealed class ProductsController(CreditDeskDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ProductResponse>> List(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync(ct);
        return products.Select(p => new ProductResponse(p.Code, p.Name, p.MinAmount, p.MaxAmount, p.AnnualInterestRate,
            p.MaxPaymentToIncomeRatio, p.GuarantorThreshold, p.AllowedTerms.Order().ToList(), p.RequiredDocuments)).ToList();
    }

    /// <summary>Simulador público: mensualidad y tabla de amortización sin crear una solicitud.</summary>
    [HttpGet("{code}/quote")]
    [ProducesResponseType<QuoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<QuoteResponse> Quote(string code, [FromQuery] decimal amount, [FromQuery] int termMonths,
        CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Code == normalized && p.IsActive, ct)
            ?? throw new NotFoundException("El producto no existe.");

        var errors = new Dictionary<string, string[]>();
        product.CollectTermsErrors(amount, termMonths, monthlyIncome: 1, errors);
        if (errors.Count > 0)
            throw new BusinessRuleException(errors);

        var schedule = Amortization.Schedule(amount, product.AnnualInterestRate, termMonths);
        var total = schedule.Sum(r => r.Payment);
        return new QuoteResponse(product.Code, amount, termMonths, schedule[0].Payment, total, total - amount, schedule);
    }
}
