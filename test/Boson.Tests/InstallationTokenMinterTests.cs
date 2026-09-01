using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Boson.Github;
using Boson.Storage;
using Boson.Tests.Support;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Boson.Tests;

public class InstallationTokenMinterTests
{
    private static (InstallationTokenMinter Minter, FakeGithubClient Github, RSA Key) Build(TempDb db)
    {
        var rsa = RSA.Create(2048);
        var projects = new ProjectsRepository(db.Db);
        projects.Insert(TestProjects.New(pem: rsa.ExportRSAPrivateKeyPem()));

        var github = new FakeGithubClient();
        return (new InstallationTokenMinter(projects, github), github, rsa);
    }

    [Fact]
    public async Task Mints_a_token()
    {
        using var db = new TempDb();
        var (minter, _, key) = Build(db);
        using var _key = key;

        var token = await minter.MintAsync("acme/site");

        Assert.Equal("ghs_test", token.Value);
    }

    /// <summary>
    /// Regression: signing went through CryptoProviderFactory.Default, whose
    /// SignatureProvider cache is keyed on key material and outlives the using-block
    /// that owns the RSA. The second mint of one App's PEM in a single process was
    /// handed the first mint's provider - RSA already disposed - and threw
    /// ObjectDisposedException. In practice `boson add` minted once, then the first
    /// `boson deploy` failed, and only a daemon restart bought one more deploy.
    /// </summary>
    [Fact]
    public async Task Mints_repeatedly_in_one_process()
    {
        using var db = new TempDb();
        var (minter, _, key) = Build(db);
        using var _key = key;

        await minter.MintAsync("acme/site");
        await minter.MintAsync("acme/site");
        var third = await minter.MintAsync("acme/site");

        Assert.Equal("ghs_test", third.Value);
    }

    [Fact]
    public async Task Signs_the_jwt_with_the_projects_key()
    {
        using var db = new TempDb();
        var (minter, github, key) = Build(db);
        using var _key = key;

        // Mint twice: the cache opt-out must not come at the cost of real signing,
        // and the token that matters is the one after the cache would have kicked in.
        await minter.MintAsync("acme/site");
        await minter.MintAsync("acme/site");

        Assert.NotNull(github.LastJwt);
        var validation = new TokenValidationParameters
        {
            IssuerSigningKey = new RsaSecurityKey(key.ExportParameters(false)),
            ValidateIssuerSigningKey = true,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
        };

        new JwtSecurityTokenHandler().ValidateToken(github.LastJwt, validation, out var validated);

        Assert.Equal("1234", ((JwtSecurityToken)validated).Issuer);
    }

    [Fact]
    public async Task Unknown_project_throws()
    {
        using var db = new TempDb();
        var (minter, _, key) = Build(db);
        using var _key = key;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => minter.MintAsync("acme/nope"));
    }
}
