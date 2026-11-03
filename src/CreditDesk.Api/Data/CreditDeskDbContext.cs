using CreditDesk.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CreditDesk.Api.Data;

public class CreditDeskDbContext(DbContextOptions<CreditDeskDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<LoanProduct> Products => Set<LoanProduct>();
    public DbSet<LoanApplication> Applications => Set<LoanApplication>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Id).ValueGeneratedNever();
            user.Property(u => u.Email).HasMaxLength(256);
            user.Property(u => u.FullName).HasMaxLength(200);
            user.Property(u => u.PasswordHash).HasMaxLength(200);
            user.Property(u => u.Role).HasMaxLength(20);
            user.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<LoanProduct>(product =>
        {
            product.Property(p => p.Code).HasMaxLength(30);
            product.Property(p => p.Name).HasMaxLength(100);
            product.Property(p => p.AnnualInterestRate).HasPrecision(6, 4);
            product.Property(p => p.MaxPaymentToIncomeRatio).HasPrecision(4, 2);
            product.HasIndex(p => p.Code).IsUnique();
        });

        modelBuilder.Entity<LoanApplication>(application =>
        {
            application.Property(a => a.Id).ValueGeneratedNever();
            application.Property(a => a.Version).IsConcurrencyToken();
            application.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            application.Property(a => a.DecisionNote).HasMaxLength(500);
            application.Ignore(a => a.MonthlyPayment);

            application.HasOne(a => a.Product).WithMany().HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Restrict);
            application.HasOne<User>().WithMany().HasForeignKey(a => a.ApplicantId).OnDelete(DeleteBehavior.Restrict);

            // Respaldo en base de datos de la regla "una solicitud abierta por solicitante y producto":
            // aunque dos peticiones pasen la validación al mismo tiempo, sólo una puede insertar.
            application.HasIndex(a => new { a.ApplicantId, a.ProductId })
                .IsUnique()
                .HasFilter("[IsOpen] = 1")
                .HasDatabaseName("UX_Applications_OneOpenPerProduct");

            application.HasIndex(a => new { a.Status, a.SubmittedAt });

            application.OwnsMany(a => a.Guarantors, guarantor =>
            {
                guarantor.ToTable("Guarantors");
                guarantor.WithOwner().HasForeignKey(g => g.LoanApplicationId);
                guarantor.HasKey(g => g.Id);
                guarantor.Property(g => g.Id).ValueGeneratedNever();
                guarantor.Property(g => g.FullName).HasMaxLength(200);
                guarantor.Property(g => g.Rfc).HasMaxLength(13);
            });

            application.OwnsMany(a => a.Documents, document =>
            {
                document.ToTable("ApplicationDocuments");
                document.WithOwner().HasForeignKey(d => d.LoanApplicationId);
                document.HasKey(d => d.Id);
                document.Property(d => d.Id).ValueGeneratedNever();
                document.Property(d => d.Type).HasConversion<string>().HasMaxLength(30);
                document.Property(d => d.FileName).HasMaxLength(260);
            });

            application.OwnsMany(a => a.History, change =>
            {
                change.ToTable("StatusChanges");
                change.WithOwner().HasForeignKey(c => c.LoanApplicationId);
                change.HasKey(c => c.Id);
                change.Property(c => c.From).HasConversion<string>().HasMaxLength(20);
                change.Property(c => c.To).HasConversion<string>().HasMaxLength(20);
                change.Property(c => c.Note).HasMaxLength(500);
            });
        });
    }
}
