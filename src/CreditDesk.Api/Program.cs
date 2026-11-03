using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CreditDesk.Api.Data;
using CreditDesk.Api.Infrastructure;
using CreditDesk.Api.Security;
using CreditDesk.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Datos ----
var provider = config["Database:Provider"] ?? "SqlServer";
var connectionString = config.GetConnectionString("CreditDesk")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:CreditDesk.");

builder.Services.AddDbContext<CreditDeskDbContext>(options =>
{
    // Split query: el agregado carga tres colecciones (avales, documentos, bitácora) sin producto cartesiano.
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(connectionString, sql => sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    else
        options.UseSqlServer(connectionString, sql => sql
            .EnableRetryOnFailure()
            .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LoanApplicationService>();

// ---- Seguridad ----
builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddSingleton<TokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.GetKey(),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimiting:AuthPermitLimit", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
});

// ---- API ----
builder.Services.AddControllers(o => o.Filters.Add<DomainExceptionFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "CreditDesk API", Version = "v1" });
    var scheme = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    options.AddSecurityDefinition("Bearer", scheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "CreditDesk.Api.xml"));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment() || config.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

await InitializeDatabaseAsync(app);
app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<CreditDeskDbContext>();
    var config = services.GetRequiredService<IConfiguration>();

    // SQL Server usa migraciones versionadas; SQLite (pruebas/demos) crea el esquema desde el modelo.
    if (db.Database.IsSqlServer())
    {
        if (config.GetValue("Database:ApplyMigrations", true))
            await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }

    var analysts = config.GetSection("Seed:Analysts").Get<List<SeedAnalyst>>() ?? [];
    await DbSeeder.SeedAsync(db, analysts, services.GetRequiredService<TimeProvider>());
}

public partial class Program;
