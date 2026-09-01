using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Boson.Storage;
using Microsoft.IdentityModel.Tokens;

namespace Boson.Github;

public sealed record InstallationToken(string Value, DateTimeOffset ExpiresAt);

public interface IInstallationTokenMinter
{
    Task<InstallationToken> MintAsync(string repo, CancellationToken ct = default);
}

/// <summary>
/// Signs a short-lived RS256 JWT with the App's PEM (from SQLite) and
/// exchanges it for a ghs_… installation access token (spec §13). No
/// cross-deploy cache: every deploy pass mints fresh (spec §6). The token is
/// never written to disk.
/// </summary>
public sealed class InstallationTokenMinter(
    IProjectsRepository projects,
    IGithubClient github) : IInstallationTokenMinter
{
    public async Task<InstallationToken> MintAsync(string repo, CancellationToken ct = default)
    {
        var project = projects.GetByRepo(repo)
            ?? throw new InvalidOperationException($"unknown project: {repo}");

        string jwt;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(project.GithubAppPem);
            var now = DateTimeOffset.UtcNow;

            // CryptoProviderFactory.Default caches SignatureProviders keyed on the
            // key material, and that cache outlives this using-block. Left alone, the
            // second mint of a given App's PEM in one daemon process gets handed the
            // first mint's provider - whose RSA is long disposed - and throws
            // ObjectDisposedException. Opt out of the cache: the RSA dies here, so
            // nothing may outlive it. The per-call KeyId keeps a lookup from matching
            // an earlier mint even if some other factory is consulted.
            var key = new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("n") };
            var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            };
            var header = new JwtHeader(credentials);
            var payload = new JwtPayload
            {
                // iat backdated 60s for clock skew; exp inside GitHub's 10-minute cap (spec §13).
                { "iat", now.ToUnixTimeSeconds() - 60 },
                { "exp", now.ToUnixTimeSeconds() + 540 },
                { "iss", project.GithubAppId.ToString() },
            };
            jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        }

        return await github.CreateInstallationTokenAsync(jwt, project.GithubInstallationId, ct);
    }
}
