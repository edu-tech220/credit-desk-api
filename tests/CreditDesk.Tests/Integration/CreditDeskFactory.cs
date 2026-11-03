using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreditDesk.Api.Contracts;
using CreditDesk.Api.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace CreditDesk.Tests.Integration;

/// <summary>
/// Levanta la API real contra un archivo SQLite temporal (uno por fábrica). Se usa archivo y no
/// ":memory:" para que varias conexiones simultáneas compartan la base, lo que permite probar
/// carreras reales entre peticiones concurrentes. No requiere servicios externos.
/// </summary>
public sealed class CreditDeskFactory : WebApplicationFactory<Program>
{
    public const string AnalystPassword = "Analista#Test2026";
    public static readonly string[] AnalystEmails = ["ana@test.local", "beto@test.local", "carla@test.local"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"creditdesk-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:CreditDesk", $"Data Source={_databasePath};Pooling=False;Default Timeout=30");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Jwt:SigningKey", "clave-exclusiva-de-pruebas-de-integracion-0123456789");
        builder.UseSetting("RateLimiting:AuthPermitLimit", "1000");

        for (var i = 0; i < AnalystEmails.Length; i++)
        {
            builder.UseSetting($"Seed:Analysts:{i}:Email", AnalystEmails[i]);
            builder.UseSetting($"Seed:Analysts:{i}:FullName", $"Analista {i}");
            builder.UseSetting($"Seed:Analysts:{i}:Password", AnalystPassword);
        }
    }

    public async Task<HttpClient> CreateApplicantAsync(string? email = null)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email ?? $"user-{Guid.NewGuid():N}@test.local", "Solicitante Prueba", "Secreta#2026"));
        response.EnsureSuccessStatusCode();
        return Authorize(client, await response.Content.ReadFromJsonAsync<AuthResponse>(Json));
    }

    public async Task<HttpClient> CreateAnalystAsync(int index = 0)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(AnalystEmails[index], AnalystPassword));
        response.EnsureSuccessStatusCode();
        return Authorize(client, await response.Content.ReadFromJsonAsync<AuthResponse>(Json));
    }

    private static HttpClient Authorize(HttpClient client, AuthResponse? auth)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Archivo temporal; si sigue bloqueado lo limpia el sistema.
        }
    }
}

/// <summary>Atajos para recorrer el flujo de una solicitud desde las pruebas.</summary>
public static class ApiFlows
{
    public static async Task<ApplicationResponse> ReadApplicationAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApplicationResponse>(CreditDeskFactory.Json))!;

    public static async Task<ApplicationResponse> CreateDraftAsync(this HttpClient applicant, string product = "NOMINA",
        decimal amount = 30_000m, int term = 12, decimal income = 15_000m)
    {
        var response = await applicant.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest(product, amount, term, income));
        response.EnsureSuccessStatusCode();
        return await response.ReadApplicationAsync();
    }

    public static async Task<ApplicationResponse> CreateSubmittedAsync(this HttpClient applicant, string product = "NOMINA")
    {
        var draft = await applicant.CreateDraftAsync(product);
        foreach (var type in new[] { DocumentType.OfficialId, DocumentType.ProofOfIncome })
        {
            var attach = await applicant.PutAsJsonAsync($"/api/applications/{draft.Id}/documents",
                new AttachDocumentRequest(type, $"{type}.pdf"), CreditDeskFactory.Json);
            attach.EnsureSuccessStatusCode();
        }

        var submit = await applicant.PostAsync($"/api/applications/{draft.Id}/submit", null);
        submit.EnsureSuccessStatusCode();
        return await submit.ReadApplicationAsync();
    }
}
