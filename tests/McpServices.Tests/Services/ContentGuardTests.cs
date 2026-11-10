using System.Security.Cryptography;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Microsoft.Extensions.Logging;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#8475: one test per detector, the false-positive cases the knowledge of the platform is full of (ids, hashes,
///     paths, slugs, URLs, documentation that names a key prefix), and the refusal log that must never carry content.
/// </summary>
public class ContentGuardTests
{
    private static readonly ContentGuardContext Context = new("tenant-a", "gerald", "knowledge_upsert_page");

    private readonly CapturingLogger _logger = new();

    private ContentGuard Guard => new(_logger);

    private Task<ContentGuardResult> CheckAsync(ContentGuardContext context, IReadOnlyCollection<ContentGuardField> fields) =>
        Guard.CheckAsync(context, fields, TestContext.Current.CancellationToken);

    // A fixed key-shaped value (base64url, mixed classes): random values would make the tests flaky, because the
    // detector is a heuristic that misses about 3 % of random keys (see ContentDetectors.MaxWordCoverage).
    private const string FakeKey = "Xk9Qz7Lm2Vb8Rt4Ny6Pw1Hs5Jd3Gf0Ka9Ec2Ub7Zo4Tq";

    private static string RandomBase64(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    [Fact]
    public async Task AnthropicKey_IsRejected()
    {
        var result = await CheckAsync(Context,
            [new ContentGuardField("sections[0]", "Use the key sk-ant-api03-" + "Ab1Cd2Ef3Gh4" + " for the call.")]);

        result.IsRejected.Should().BeTrue();
        result.Findings.Should().ContainSingle(f => f.Detector == ContentGuardDetector.AnthropicKey);
        result.Message.Should().StartWith(ContentGuardResult.ErrorCode);
    }

    [Fact]
    public async Task AnthropicKeyStartWithEllipsis_AsInTheMilestoneTest_IsRejected()
    {
        var result = await CheckAsync(Context, [new ContentGuardField("title", "Fake key sk-ant-api03-…")]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.AnthropicKey);
    }

    [Fact]
    public async Task PemBlock_IsRejected()
    {
        var pem = "-----BEGIN PRIVATE KEY-----\n" + RandomBase64(48) + "\n" + RandomBase64(48) +
                  "\n-----END PRIVATE KEY-----";

        var result = await CheckAsync(Context, [new ContentGuardField("sections[1]", "Key:\n" + pem)]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.PemBlock);
    }

    [Fact]
    public async Task Jwt_IsRejected()
    {
        const string jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwidGVuYW50X2lkIjoiYSJ9." +
                           "c2lnbmF0dXJlLXBhcnQtb2YtdGhlLXRva2Vu";

        var result = await CheckAsync(Context, [new ContentGuardField("summary", $"Bearer {jwt}")]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.Jwt);
    }

    [Fact]
    public async Task HighEntropyTokenInAUrlQuery_IsRejected_ButNotInTheUrlPath()
    {
        const string token = FakeKey;

        (await CheckAsync(Context, [new ContentGuardField("a", $"https://example.invalid/share/{token}")]))
            .IsRejected.Should().BeFalse("ids in the path of a share link are not credentials");
        (await CheckAsync(Context, [new ContentGuardField("b", $"https://example.invalid/api?sig={token}")]))
            .Findings.Should().Contain(f => f.Detector == ContentGuardDetector.HighEntropyToken);
    }

    [Fact]
    public async Task HighEntropyToken_IsRejected()
    {
        const string token = FakeKey;

        var result = await CheckAsync(Context, [new ContentGuardField("sections[0]", $"client secret {token}")]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.HighEntropyToken);
    }

    [Fact]
    public async Task TenantSecretValue_IsRejected_WithoutTheGuardSeeingThePlaintextOfOtherValues()
    {
        const string secret = "mqtt-broker-pass-2026!";
        var context = Context with
        {
            SecretFingerprints = _ => ValueTask.FromResult<IReadOnlySet<string>>(
                new HashSet<string> { SecretFingerprint.Compute(secret) })
        };

        var result = await CheckAsync(context,
            [new ContentGuardField("sections[2]", $"The broker accepts password={secret}.")]);

        result.Findings.Should().ContainSingle(f => f.Detector == ContentGuardDetector.TenantSecret &&
                                                    f.FieldName == "sections[2]");
    }

    [Fact]
    public async Task TenantSecretValue_InQuotesOrAlone_IsRejected()
    {
        const string secret = "S3cret-Value-For-Test";
        var context = Context with
        {
            SecretFingerprints = _ => ValueTask.FromResult<IReadOnlySet<string>>(
                new HashSet<string> { SecretFingerprint.Compute(secret) })
        };

        (await CheckAsync(context, [new ContentGuardField("a", $"\"{secret}\"")])).IsRejected.Should().BeTrue();
        (await CheckAsync(context, [new ContentGuardField("b", secret)])).IsRejected.Should().BeTrue();
        (await CheckAsync(context, [new ContentGuardField("c", "nothing secret here")])).IsRejected
            .Should().BeFalse();
    }

    [Fact]
    public async Task FingerprintSourceFails_WriteIsRefused()
    {
        var context = Context with
        {
            SecretFingerprints = _ => throw new InvalidOperationException("database down")
        };

        var result = await CheckAsync(context, [new ContentGuardField("title", "harmless")]);

        result.Findings.Should().ContainSingle(f => f.Detector == ContentGuardDetector.TenantSecret);
    }

    // AB#8702: one test per credential pattern. Every value is an obvious fake: low-entropy runs and the word "fake"
    // or "example", never a real credential. The values are built so that only the detector under test is needed.

    [Theory]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghu_")]
    [InlineData("ghs_")]
    [InlineData("ghr_")]
    public async Task GitHubClassicToken_IsRejected(string prefix)
    {
        var result = await CheckAsync(Context,
            [new ContentGuardField("sections[0]", $"Clone with {prefix}FAKEfake0000FAKEfake0000FAKEfake0000 and push.")]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.GitHubToken &&
                                              f.FieldName == "sections[0]");
        result.Message.Should().StartWith(ContentGuardResult.ErrorCode).And.NotContain("FAKEfake");
    }

    [Fact]
    public async Task GitHubFineGrainedToken_IsRejected()
    {
        var result = await CheckAsync(Context,
            [new ContentGuardField("summary", "token github_pat_11FAKE0000000000000000_fakefakefakefakefakefake")]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.GitHubToken);
    }

    [Fact]
    public async Task AwsAccessKeyId_IsRejected()
    {
        var result = await CheckAsync(Context,
            [new ContentGuardField("sections[1]", "The bucket is read with AKIAFAKEFAKEFAKE0000 from the build agent.")]);

        result.Findings.Should().ContainSingle(f => f.Detector == ContentGuardDetector.AwsAccessKey);
    }

    [Fact]
    public async Task AwsTemporaryAccessKeyId_IsRejected()
    {
        var result = await CheckAsync(Context, [new ContentGuardField("title", "Key ASIAFAKEFAKEFAKE0000")]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.AwsAccessKey);
    }

    [Theory]
    [InlineData("Connect with password=hunter2fake to the broker.")]
    [InlineData("Server=db;User Id=sa;Password=FakePass123;")]
    [InlineData("export DB_PASSWORD=fakefake")]
    [InlineData("password: Fake-Pass-2026!")]
    [InlineData("{\"username\": \"admin\", \"password\": \"fakefake\"}")]
    [InlineData("passwd = 'fake1234'")]
    // Review of PR #66: a leading symbol is no placeholder.
    [InlineData("password=#Fake2xyz!")]
    [InlineData("password=@fakeP4ss")]
    [InlineData("password=&fake0000")]
    [InlineData("password=.fake0000")]
    [InlineData("password=?fake0000")]
    [InlineData("password=(fake0000)")]
    [InlineData("password=$3cretFake")]
    [InlineData("password=*fake0000")]
    public async Task PasswordAssignment_IsRejected(string text)
    {
        var result = await CheckAsync(Context, [new ContentGuardField("sections[0]", text)]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.CredentialAssignment, text);
    }

    [Theory]
    [InlineData("client_secret=fakeClientSecret0000")]
    [InlineData("\"clientSecret\": \"fake-secret-0000\"")]
    [InlineData("AZURE_CLIENT_SECRET: fake.secret.0000~x")]
    public async Task SecretAssignment_IsRejected(string text)
    {
        var result = await CheckAsync(Context, [new ContentGuardField("sections[0]", text)]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.CredentialAssignment, text);
    }

    [Theory]
    [InlineData("token=fakefake0000")]
    [InlineData("access_token: fake_token_0000")]
    [InlineData("api_key = \"fakekey0000\"")]
    [InlineData("curl https://example.invalid/api?apikey=fake0000")]
    [InlineData("AWS_SECRET_ACCESS_KEY=fake/secret/0000")]
    public async Task TokenAndKeyAssignment_IsRejected(string text)
    {
        var result = await CheckAsync(Context, [new ContentGuardField("sections[0]", text)]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.CredentialAssignment, text);
    }

    [Fact]
    public async Task PrivateKeyBlock_IsRejected_AsInTheStory()
    {
        const string pem = "-----BEGIN RSA PRIVATE KEY-----\nFAKEfakeFAKEfakeFAKEfakeFAKEfake\nFAKEfakeFAKEfake\n" +
                           "-----END RSA PRIVATE KEY-----";

        var result = await CheckAsync(Context, [new ContentGuardField("sections[0]", pem)]);

        result.Findings.Should().Contain(f => f.Detector == ContentGuardDetector.PemBlock);
    }

    [Fact]
    public async Task AnthropicKeyInAnAssignment_IsRejectedByBothDetectors()
    {
        var result = await CheckAsync(Context,
            [new ContentGuardField("sections[0]", "ANTHROPIC_API_KEY=sk-ant-api03-FAKEfake")]);

        result.Findings.Select(f => f.Detector).Should()
            .Contain([ContentGuardDetector.AnthropicKey, ContentGuardDetector.CredentialAssignment]);
    }

    /// <summary>AB#8702 AC 2: prose and documentation that name a credential without a value pass.</summary>
    [Theory]
    [InlineData("Set a strong password for the admin account and rotate it every 90 days.")]
    [InlineData("The password is stored in Vault; ask the platform team for access.")]
    [InlineData("Password: see the team vault.")]
    [InlineData("Client secret: rotate it yearly in the identity provider.")]
    [InlineData("A password reset link expires after one hour.")]
    [InlineData("The token: an opaque string the identity service issues.")]
    [InlineData("password=<your-password>")]
    [InlineData("DB_PASSWORD=$DB_PASSWORD")]
    [InlineData("token: ${{ secrets.GITHUB_TOKEN }}")]
    [InlineData("password: ***")]
    [InlineData("password=[REDACTED]")]
    [InlineData("password=%DB_PASSWORD%")]
    [InlineData("password={password}")]
    [InlineData("password=$env:DB_PASSWORD")]
    [InlineData("password=$(cat /run/secrets/db)")]
    [InlineData("password: {{ .Values.db.password }}")]
    [InlineData("password=...")]
    [InlineData("password: \"\"")]
    [InlineData("\"token\": \"The access token of the caller\"")]
    [InlineData("type Config { host: String! password: String! }")]
    [InlineData("token: string; apiKey: process.env.API_KEY")]
    [InlineData("var token = await GetTokenAsync(cancellationToken: ct);")]
    [InlineData("if (token == null) return; items.Select(token => token.Value)")]
    [InlineData("existingSecret: octo-mongodb-credentials")]
    [InlineData("secretKeyRef: name: octo-tls")]
    [InlineData("GitHub classic tokens start with ghp_ and fine-grained ones with github_pat_.")]
    [InlineData("AWS access key ids start with AKIA.")]
    [InlineData("passwordPolicy: strict, tokenLifetime: 3600")]
    public async Task ProseAndPlaceholdersAboutCredentials_AreAccepted(string text)
    {
        var result = await CheckAsync(Context, [new ContentGuardField("sections[0]", text)]);

        result.IsRejected.Should().BeFalse(result.Message);
    }

    [Theory]
    [InlineData("Known key prefixes such as `sk-ant-`, PEM blocks, JWTs and high-entropy tokens are rejected.")]
    [InlineData("rtId 67a1000000000000000a1c20, commit b110b96b9f0f8b6c3c1d2e7a4f5e6d7c8b9a0f1e")]
    [InlineData("GUID 3bb4b1b4-a69e-4847-abe5-aab3621e69f0 and SHA-256 3f1b0c4e8d2a7f6b5c9e0a1d2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e")]
    [InlineData("Branch gerald/AB#8483_system-ai-knowledge-types in octo-construction-kit-engine-mongodb")]
    [InlineData("See src/McpServices/Services/ContentGuard/TenantSecretFingerprints.cs and docs/concepts/tenant-builder-agent-concept.md")]
    [InlineData("https://dev.azure.com/meshmakers/OctoMesh/_workitems/edit/8483?view=Microsoft.VSTS.Common.AcceptanceCriteria")]
    [InlineData("Use OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT=Enforce and System.Ai-4.7.0/KnowledgePage-1")]
    [InlineData("-----BEGIN PRIVATE KEY----- is what a PEM block starts with; never paste one.")]
    [InlineData("Header eyJhbGciOi... is the start of a JWT.")]
    [InlineData("Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4")]
    [InlineData("https://dev.azure.com/meshmakers/OctoMesh/_wiki/wikis/OctoMesh.wiki/1234/Release2-Trains2-Development-Guidelines")]
    [InlineData("guide: `technologyGuide/constructionKits/methods3/methodHandlerPipelines2/AB7768`")]
    [InlineData("Teams channel 19:Ab3dE5fG7hJ9kL2mN4pQ6rS8tU0vW1xY3zA5bC7dE9fG@thread.tacv2")]
    public async Task KnowledgeTextOfThePlatform_IsAccepted(string text)
    {
        var result = await CheckAsync(Context, [new ContentGuardField("sections[0]", text)]);

        result.IsRejected.Should().BeFalse(result.Message);
    }

    [Fact]
    public async Task Refusal_IsLoggedWithTenantCallerAndDetector_NeverWithTheContent()
    {
        const string content = "token sk-ant-api03-SECRETPAYLOAD123";

        await CheckAsync(Context, [new ContentGuardField("sections[0]", content)]);

        var line = _logger.Lines.Should().ContainSingle().Subject;
        line.Should().Contain("tenant-a").And.Contain("gerald").And.Contain("knowledge_upsert_page")
            .And.Contain(nameof(ContentGuardDetector.AnthropicKey)).And.Contain("sections[0]");
        line.Should().NotContain("SECRETPAYLOAD");
    }

    [Fact]
    public async Task RefusalMessage_NeverContainsTheContent()
    {
        var result = await CheckAsync(Context,
            [new ContentGuardField("title", "sk-ant-admin01-VISIBLEPART")]);

        result.Message.Should().NotContain("VISIBLEPART");
    }

    [Fact]
    public async Task EmptyFields_AreAccepted_AndTheFingerprintsAreNotRead()
    {
        var read = false;
        var context = Context with
        {
            SecretFingerprints = _ =>
            {
                read = true;
                return ValueTask.FromResult<IReadOnlySet<string>>(new HashSet<string>());
            }
        };

        var result = await CheckAsync(context, [new ContentGuardField("title", null), new ContentGuardField("x", "")]);

        result.IsRejected.Should().BeFalse();
        read.Should().BeFalse();
    }

    /// <summary>
    ///     False-positive check against real knowledge (AC 2): set <c>OCTO_CONTENT_GUARD_CORPUS</c> to one or more
    ///     directories (separated by the path separator), e.g. the octo-claude-skills <c>skills</c> folder and a Claude
    ///     Code memory folder. Every markdown file must pass. Skipped when the variable is not set (CI).
    /// </summary>
    [Fact]
    public async Task Corpus_FromEnvironment_PassesTheGuard()
    {
        var corpus = Environment.GetEnvironmentVariable("OCTO_CONTENT_GUARD_CORPUS");
        if (string.IsNullOrWhiteSpace(corpus))
        {
            Assert.Skip("OCTO_CONTENT_GUARD_CORPUS is not set.");
        }

        var files = corpus.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.md", SearchOption.AllDirectories))
            .ToList();
        files.Should().NotBeEmpty();

        var rejected = new List<string>();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            var result = await CheckAsync(Context, [new ContentGuardField(Path.GetFileName(file), text)]);
            if (result.IsRejected)
            {
                rejected.Add($"{file}: {string.Join(",", result.Findings.Select(f => f.Detector))}");
            }
        }

        rejected.Should().BeEmpty($"all {files.Count} files of the corpus must pass");
    }

    private sealed class CapturingLogger : ILogger<ContentGuard>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
