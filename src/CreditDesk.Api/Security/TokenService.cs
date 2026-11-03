using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CreditDesk.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CreditDesk.Api.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "CreditDesk";
    public string Audience { get; set; } = "CreditDesk";

    /// <summary>Llave HMAC de al menos 32 bytes. Se inyecta por entorno; nunca va en el repositorio.</summary>
    public string SigningKey { get; set; } = "";

    public int ExpirationMinutes { get; set; } = 60;

    public SymmetricSecurityKey GetKey()
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < 32)
            throw new InvalidOperationException("Jwt:SigningKey debe configurarse y tener al menos 32 bytes.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
    }
}

public sealed record AccessToken(string Token, DateTime ExpiresAt);

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public AccessToken Issue(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.ExpirationMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role),
        };

        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(_options.GetKey(), SecurityAlgorithms.HmacSha256));

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out var id) ? id : throw new InvalidOperationException("El token no contiene el id de usuario.");
    }
}
