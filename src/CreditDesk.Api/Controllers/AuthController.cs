using CreditDesk.Api.Contracts;
using CreditDesk.Api.Data;
using CreditDesk.Api.Domain;
using CreditDesk.Api.Infrastructure;
using CreditDesk.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CreditDesk.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(CreditDeskDbContext db, TokenService tokens, TimeProvider clock) : ControllerBase
{
    /// <summary>Registra un solicitante. Los analistas sólo se crean desde configuración.</summary>
    [HttpPost("register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("email_taken", "Ya existe una cuenta con ese correo.");

        var user = new User
        {
            Email = email,
            FullName = request.FullName.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password),
            Role = Roles.Applicant,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, ToResponse(user));
    }

    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);

        // Mismo mensaje para usuario inexistente y contraseña incorrecta: no se filtra qué correos existen.
        if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Credenciales inválidas");

        return Ok(ToResponse(user));
    }

    private AuthResponse ToResponse(User user)
    {
        var token = tokens.Issue(user);
        return new AuthResponse(user.Id, user.Email, user.Role, token.Token, token.ExpiresAt);
    }
}
