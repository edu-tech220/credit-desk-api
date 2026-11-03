namespace CreditDesk.Api.Domain;

public sealed record AmortizationRow(int Number, decimal Payment, decimal Interest, decimal Principal, decimal Balance);

/// <summary>
/// Amortización francesa (pago fijo). Todo se calcula en <see cref="decimal"/> y se redondea
/// a centavos en cada periodo; el último pago absorbe la diferencia para que el saldo cierre en 0.
/// </summary>
public static class Amortization
{
    public static decimal MonthlyPayment(decimal principal, decimal annualRate, int termMonths)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(principal);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(termMonths);
        ArgumentOutOfRangeException.ThrowIfNegative(annualRate);

        if (annualRate == 0)
            return Round(principal / termMonths);

        var monthlyRate = annualRate / 12m;
        var growth = Pow(1m + monthlyRate, termMonths);
        return Round(principal * monthlyRate * growth / (growth - 1m));
    }

    public static IReadOnlyList<AmortizationRow> Schedule(decimal principal, decimal annualRate, int termMonths)
    {
        var payment = MonthlyPayment(principal, annualRate, termMonths);
        var monthlyRate = annualRate / 12m;
        var balance = principal;
        var rows = new List<AmortizationRow>(termMonths);

        for (var n = 1; n <= termMonths; n++)
        {
            var interest = Round(balance * monthlyRate);
            var principalPart = n == termMonths ? balance : payment - interest;
            balance -= principalPart;
            rows.Add(new AmortizationRow(n, principalPart + interest, interest, principalPart, balance));
        }

        return rows;
    }

    private static decimal Pow(decimal value, int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
            result *= value;
        return result;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
