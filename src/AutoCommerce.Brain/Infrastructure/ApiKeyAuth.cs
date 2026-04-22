using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AutoCommerce.Brain.Infrastructure;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

public class ApiKeyOptions : AuthenticationSchemeOptions
{
    public string? MasterKey { get; set; }
}

public class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyOptions>
{
    private readonly BrainDbContext _db;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        BrainDbContext db) : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values))
            return AuthenticateResult.NoResult();

        var provided = values.ToString();
        if (string.IsNullOrWhiteSpace(provided))
            return AuthenticateResult.Fail("Missing API key");

        var hash = HashKey(provided);

        if (!string.IsNullOrEmpty(Options.MasterKey) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Options.MasterKey),
                Encoding.UTF8.GetBytes(provided)))
        {
            return Success("master", "*");
        }

        var record = await _db.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == hash && !k.Revoked);

        if (record is null) return AuthenticateResult.Fail("Invalid API key");

        return Success(record.Name, record.Scopes);
    }

    private AuthenticateResult Success(string name, string scopes)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim("scopes", scopes)
        };
        var identity = new ClaimsIdentity(claims, ApiKeyDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyDefaults.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    public static string HashKey(string plainKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainKey));
        return Convert.ToHexString(bytes);
    }
}
