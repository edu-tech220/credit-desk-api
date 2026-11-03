using CreditDesk.Api.Domain;

namespace CreditDesk.Tests.Unit;

public class AmortizationTests
{
    [Fact]
    public void MonthlyPayment_matches_french_method_reference_value()
    {
        // 100,000 al 12 % anual a 12 meses: valor de referencia 8,884.88.
        Assert.Equal(8_884.88m, Amortization.MonthlyPayment(100_000m, 0.12m, 12));
    }

    [Fact]
    public void MonthlyPayment_with_zero_rate_splits_principal_evenly()
    {
        Assert.Equal(1_000m, Amortization.MonthlyPayment(12_000m, 0m, 12));
    }

    [Theory]
    [InlineData(100_000, 0.12, 12)]
    [InlineData(250_000, 0.32, 36)]
    [InlineData(5_000, 0.22, 6)]
    [InlineData(1_999_999.99, 0.26, 48)]
    public void Schedule_amortizes_exactly_the_principal_and_closes_at_zero(decimal principal, decimal rate, int term)
    {
        var schedule = Amortization.Schedule(principal, rate, term);

        Assert.Equal(term, schedule.Count);
        Assert.Equal(principal, schedule.Sum(r => r.Principal));
        Assert.Equal(0m, schedule[^1].Balance);
        Assert.All(schedule, r => Assert.Equal(r.Payment, r.Interest + r.Principal));
        Assert.All(schedule, r => Assert.Equal(r.Interest, decimal.Round(r.Interest, 2)));
    }

    [Fact]
    public void Schedule_keeps_payment_constant_except_for_the_final_adjustment()
    {
        var schedule = Amortization.Schedule(250_000m, 0.32m, 36);
        var payment = schedule[0].Payment;

        Assert.All(schedule.Take(35), r => Assert.Equal(payment, r.Payment));
        Assert.InRange(Math.Abs(schedule[^1].Payment - payment), 0m, 0.50m);
    }

    [Fact]
    public void Interest_decreases_as_balance_is_paid()
    {
        var schedule = Amortization.Schedule(100_000m, 0.24m, 24);

        for (var i = 1; i < schedule.Count; i++)
            Assert.True(schedule[i].Interest < schedule[i - 1].Interest);
    }

    [Theory]
    [InlineData(0, 0.1, 12)]
    [InlineData(1000, 0.1, 0)]
    [InlineData(1000, -0.1, 12)]
    public void Invalid_inputs_are_rejected(decimal principal, decimal rate, int term)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => Amortization.MonthlyPayment(principal, rate, term));
    }
}
