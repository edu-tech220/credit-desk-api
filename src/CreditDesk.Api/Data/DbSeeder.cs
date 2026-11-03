using CreditDesk.Api.Domain;
using CreditDesk.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace CreditDesk.Api.Data;

public sealed class SeedAnalyst
{
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>
/// Carga el catálogo de productos y, si se configuran, las cuentas de analista.
/// Los analistas no se pueden registrar por la API: sólo se crean desde configuración
/// (variables de entorno / user-secrets), nunca con contraseñas escritas en el código.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(CreditDeskDbContext db, IEnumerable<SeedAnalyst> analysts, TimeProvider clock,
        CancellationToken ct = default)
    {
        if (!await db.Products.AnyAsync(ct))
        {
            db.Products.AddRange(
                new LoanProduct
                {
                    Code = "PERSONAL",
                    Name = "Crédito personal",
                    MinAmount = 5_000m,
                    MaxAmount = 300_000m,
                    AnnualInterestRate = 0.32m,
                    MaxPaymentToIncomeRatio = 0.35m,
                    GuarantorThreshold = 100_000m,
                    AllowedTerms = [6, 12, 18, 24, 36],
                    RequiredDocuments = [DocumentType.OfficialId, DocumentType.ProofOfAddress, DocumentType.ProofOfIncome],
                },
                new LoanProduct
                {
                    Code = "NOMINA",
                    Name = "Crédito de nómina",
                    MinAmount = 3_000m,
                    MaxAmount = 150_000m,
                    AnnualInterestRate = 0.22m,
                    MaxPaymentToIncomeRatio = 0.40m,
                    GuarantorThreshold = null,
                    AllowedTerms = [6, 12, 24],
                    RequiredDocuments = [DocumentType.OfficialId, DocumentType.ProofOfIncome],
                },
                new LoanProduct
                {
                    Code = "PYME",
                    Name = "Capital de trabajo PyME",
                    MinAmount = 50_000m,
                    MaxAmount = 2_000_000m,
                    AnnualInterestRate = 0.26m,
                    MaxPaymentToIncomeRatio = 0.30m,
                    GuarantorThreshold = 250_000m,
                    AllowedTerms = [12, 24, 36, 48],
                    RequiredDocuments =
                    [
                        DocumentType.OfficialId, DocumentType.ProofOfAddress,
                        DocumentType.ProofOfIncome, DocumentType.BankStatement
                    ],
                });
        }

        foreach (var analyst in analysts.Where(a => !string.IsNullOrWhiteSpace(a.Email)))
        {
            var email = analyst.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
                continue;

            if (analyst.Password.Length < PasswordHasher.MinLength)
                throw new InvalidOperationException($"La contraseña del analista {email} es demasiado corta.");

            db.Users.Add(new User
            {
                Email = email,
                FullName = analyst.FullName,
                PasswordHash = PasswordHasher.Hash(analyst.Password),
                Role = Roles.Analyst,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
