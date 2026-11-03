using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreditDesk.Api.Contracts;
using CreditDesk.Api.Domain;

namespace CreditDesk.Tests.Integration;

public class ApplicationsApiTests(CreditDeskFactory factory) : IClassFixture<CreditDeskFactory>
{
    [Fact]
    public async Task Requests_without_token_are_rejected()
    {
        var response = await factory.CreateClient().GetAsync("/api/applications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new LoginRequest(CreditDeskFactory.AnalystEmails[0], "incorrecta"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Registering_same_email_twice_returns_409()
    {
        var email = $"dup-{Guid.NewGuid():N}@test.local";
        await factory.CreateApplicantAsync(email);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email.ToUpperInvariant(), "Otro", "Secreta#2026"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Quote_returns_payment_and_full_schedule()
    {
        var quote = await factory.CreateClient()
            .GetFromJsonAsync<QuoteResponse>("/api/products/personal/quote?amount=100000&termMonths=12", CreditDeskFactory.Json);

        Assert.NotNull(quote);
        Assert.Equal(12, quote.Schedule.Count);
        Assert.Equal(Amortization.MonthlyPayment(100_000m, 0.32m, 12), quote.MonthlyPayment);
        Assert.Equal(quote.TotalPayment - 100_000m, quote.TotalInterest);
    }

    [Fact]
    public async Task Quote_outside_product_limits_returns_422()
    {
        var response = await factory.CreateClient().GetAsync("/api/products/NOMINA/quote?amount=1000000&termMonths=7");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = await ReadErrorsAsync(response);
        Assert.True(errors.TryGetProperty("amount", out _));
        Assert.True(errors.TryGetProperty("termMonths", out _));
    }

    [Fact]
    public async Task Complete_lifecycle_from_draft_to_disbursement()
    {
        var applicant = await factory.CreateApplicantAsync();
        var analyst = await factory.CreateAnalystAsync();

        var submitted = await applicant.CreateSubmittedAsync();
        Assert.Equal(ApplicationStatus.Submitted, submitted.Status);

        var review = await analyst.PostAsync($"/api/applications/{submitted.Id}/review", null);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        var approve = await analyst.PostAsJsonAsync($"/api/applications/{submitted.Id}/approve", new ApproveRequest("Cumple políticas"));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var disburse = await analyst.PostAsync($"/api/applications/{submitted.Id}/disburse", null);
        var final = await disburse.ReadApplicationAsync();

        Assert.Equal(ApplicationStatus.Disbursed, final.Status);
        Assert.Equal(5, final.History.Count);
        Assert.Equal("Cumple políticas", final.DecisionNote);

        var schedule = await applicant.GetFromJsonAsync<List<AmortizationRow>>(
            $"/api/applications/{submitted.Id}/schedule", CreditDeskFactory.Json);
        Assert.Equal(final.TermMonths, schedule!.Count);
        Assert.Equal(final.MonthlyPayment, schedule[0].Payment);
    }

    [Fact]
    public async Task Submit_with_missing_requirements_returns_422_with_every_error()
    {
        var applicant = await factory.CreateApplicantAsync();
        var draft = await applicant.CreateDraftAsync("PERSONAL", amount: 150_000m, term: 24, income: 40_000m);

        var response = await applicant.PostAsync($"/api/applications/{draft.Id}/submit", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = await ReadErrorsAsync(response);
        Assert.True(errors.TryGetProperty("documents", out _));
        Assert.True(errors.TryGetProperty("guarantors", out _));
    }

    [Fact]
    public async Task Only_one_open_application_per_product()
    {
        var applicant = await factory.CreateApplicantAsync();
        await applicant.CreateDraftAsync("NOMINA");

        var second = await applicant.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("NOMINA", 10_000m, 6, 15_000m));
        var otherProduct = await applicant.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("PERSONAL", 10_000m, 6, 15_000m));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("open_application_exists", await ReadCodeAsync(second));
        Assert.Equal(HttpStatusCode.Created, otherProduct.StatusCode);
    }

    [Fact]
    public async Task Cancelled_application_frees_the_product_slot()
    {
        var applicant = await factory.CreateApplicantAsync();
        var draft = await applicant.CreateDraftAsync("NOMINA");
        (await applicant.PostAsync($"/api/applications/{draft.Id}/cancel", null)).EnsureSuccessStatusCode();

        var again = await applicant.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("NOMINA", 10_000m, 6, 15_000m));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Applicant_cannot_see_someone_elses_application()
    {
        var owner = await factory.CreateApplicantAsync();
        var stranger = await factory.CreateApplicantAsync();
        var draft = await owner.CreateDraftAsync();

        var response = await stranger.GetAsync($"/api/applications/{draft.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Applicant_cannot_execute_analyst_actions()
    {
        var applicant = await factory.CreateApplicantAsync();
        var submitted = await applicant.CreateSubmittedAsync();

        var response = await applicant.PostAsync($"/api/applications/{submitted.Id}/review", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_transition_returns_409_with_code()
    {
        var applicant = await factory.CreateApplicantAsync();
        var analyst = await factory.CreateAnalystAsync();
        var draft = await applicant.CreateDraftAsync();

        var response = await analyst.PostAsync($"/api/applications/{draft.Id}/review", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("invalid_transition", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Stale_if_match_returns_412_and_current_etag_succeeds()
    {
        var applicant = await factory.CreateApplicantAsync();
        var created = await applicant.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("NOMINA", 30_000m, 12, 15_000m));
        var draft = await created.ReadApplicationAsync();
        var staleETag = created.Headers.ETag!.Tag;
        Assert.Equal($"\"{draft.Version}\"", staleETag);

        // Otro cambio (p. ej. desde otra pestaña) deja obsoleta la versión leída.
        var update = await applicant.PutAsJsonAsync($"/api/applications/{draft.Id}/terms", new UpdateTermsRequest(40_000m, 12, 15_000m));
        var currentETag = update.Headers.ETag!.Tag;

        var stale = await SendWithIfMatch(applicant, HttpMethod.Put, $"/api/applications/{draft.Id}/terms",
            new UpdateTermsRequest(50_000m, 12, 15_000m), staleETag);
        var fresh = await SendWithIfMatch(applicant, HttpMethod.Put, $"/api/applications/{draft.Id}/terms",
            new UpdateTermsRequest(50_000m, 12, 15_000m), currentETag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(50_000m, (await fresh.ReadApplicationAsync()).Amount);
    }

    [Fact]
    public async Task Guarantors_can_be_added_and_removed_while_draft()
    {
        var applicant = await factory.CreateApplicantAsync();
        var draft = await applicant.CreateDraftAsync("PERSONAL", amount: 150_000m, term: 24, income: 40_000m);

        var added = await (await applicant.PostAsJsonAsync($"/api/applications/{draft.Id}/guarantors",
            new AddGuarantorRequest("Aval Uno", "gode561231gr8", 20_000m))).ReadApplicationAsync();
        var guarantor = Assert.Single(added.Guarantors);
        Assert.Equal("GODE561231GR8", guarantor.Rfc);

        var removed = await (await applicant.DeleteAsync(
            $"/api/applications/{draft.Id}/guarantors/{guarantor.Id}")).ReadApplicationAsync();
        Assert.Empty(removed.Guarantors);
    }

    [Fact]
    public async Task Malformed_input_returns_400()
    {
        var applicant = await factory.CreateApplicantAsync();

        var response = await applicant.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("NOMINA", -5m, 12, 15_000m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_review_claims_have_exactly_one_winner()
    {
        var applicant = await factory.CreateApplicantAsync();
        var submitted = await applicant.CreateSubmittedAsync();
        var analysts = await Task.WhenAll(
            Enumerable.Range(0, CreditDeskFactory.AnalystEmails.Length).Select(i => factory.CreateAnalystAsync(i)));

        // Cada analista intenta varias veces a la vez para forzar la carrera.
        var attempts = analysts.SelectMany(a => Enumerable.Repeat(a, 4))
            .Select(a => a.PostAsync($"/api/applications/{submitted.Id}/review", null));
        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        var final = await (await applicant.GetAsync($"/api/applications/{submitted.Id}")).ReadApplicationAsync();
        Assert.Equal(ApplicationStatus.InReview, final.Status);
        Assert.Single(final.History, h => h.To == ApplicationStatus.InReview);
    }

    [Fact]
    public async Task Analyst_queue_is_paginated_fifo()
    {
        using var isolated = new CreditDeskFactory();
        var analyst = await isolated.CreateAnalystAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var applicant = await isolated.CreateApplicantAsync();
            ids.Add((await applicant.CreateSubmittedAsync()).Id);
        }

        var page1 = await analyst.GetFromJsonAsync<PagedResponse<ApplicationSummaryResponse>>(
            "/api/applications?status=Submitted&page=1&pageSize=2", CreditDeskFactory.Json);
        var page3 = await analyst.GetFromJsonAsync<PagedResponse<ApplicationSummaryResponse>>(
            "/api/applications?status=Submitted&page=3&pageSize=2", CreditDeskFactory.Json);

        Assert.Equal(5, page1!.TotalCount);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal(ids.Take(2), page1.Items.Select(i => i.Id));
        Assert.Equal([ids[4]], page3!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Applicant_list_only_contains_own_applications()
    {
        var applicant = await factory.CreateApplicantAsync();
        await (await factory.CreateApplicantAsync()).CreateDraftAsync();
        var mine = await applicant.CreateDraftAsync();

        var page = await applicant.GetFromJsonAsync<PagedResponse<ApplicationSummaryResponse>>(
            "/api/applications", CreditDeskFactory.Json);

        Assert.Equal(mine.Id, Assert.Single(page!.Items).Id);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=51")]
    public async Task Invalid_pagination_returns_400(string query)
    {
        var applicant = await factory.CreateApplicantAsync();

        var response = await applicant.GetAsync($"/api/applications?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendWithIfMatch<T>(HttpClient client, HttpMethod method, string url, T body,
        string etag)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadErrorsAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors");
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("code").GetString();
    }
}
