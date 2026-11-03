namespace CreditDesk.Api.Domain;

/// <summary>Producto de crédito del catálogo: define límites, plazos, tasa y requisitos del expediente.</summary>
public class LoanProduct
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public decimal MinAmount { get; set; }
    public decimal MaxAmount { get; set; }

    /// <summary>Tasa anual fija expresada como fracción (0.24 = 24 %).</summary>
    public decimal AnnualInterestRate { get; set; }

    /// <summary>Proporción máxima del ingreso mensual que puede comprometer la mensualidad.</summary>
    public decimal MaxPaymentToIncomeRatio { get; set; }

    /// <summary>Montos mayores a este valor exigen al menos un aval. Null = nunca se exige.</summary>
    public decimal? GuarantorThreshold { get; set; }

    public List<int> AllowedTerms { get; set; } = [];
    public List<DocumentType> RequiredDocuments { get; set; } = [];
    public bool IsActive { get; set; } = true;

    public bool RequiresGuarantor(decimal amount) => GuarantorThreshold is { } threshold && amount > threshold;

    /// <summary>Reglas que se pueden validar desde el borrador: rango de monto y plazo permitido.</summary>
    public void CollectTermsErrors(decimal amount, int termMonths, decimal monthlyIncome, IDictionary<string, string[]> errors)
    {
        if (amount < MinAmount || amount > MaxAmount)
            errors["amount"] = [$"El monto debe estar entre {MinAmount:N2} y {MaxAmount:N2}."];

        if (!AllowedTerms.Contains(termMonths))
            errors["termMonths"] = [$"Plazos permitidos: {string.Join(", ", AllowedTerms.Order())} meses."];

        if (monthlyIncome <= 0)
            errors["monthlyIncome"] = ["El ingreso mensual debe ser mayor a cero."];
    }
}
