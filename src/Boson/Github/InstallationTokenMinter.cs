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
            var header = new JwtHeader(new SigningCredentials(
                new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256));
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
