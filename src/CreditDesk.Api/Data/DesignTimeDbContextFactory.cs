using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CreditDesk.Api.Data;

/// <summary>
/// Usado sólo por <c>dotnet ef</c>: las migraciones se generan siempre para SQL Server,
/// sin importar el proveedor configurado en el entorno local. No abre conexión.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CreditDeskDbContext>
{
    public CreditDeskDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CreditDeskDbContext>()
            .UseSqlServer("Server=localhost;Database=CreditDesk;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new CreditDeskDbContext(options);
    }
}
