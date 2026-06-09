using System.Net;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     HTTP-level specs for <see cref="GitHubRepoApiClient" /> (#4146). Drives the client
///     through a fake <see cref="HttpMessageHandler" /> that returns canned responses for
///     the documented status codes (201 created, 422 with disambiguation, 401 auth fail,
///     network error). The MCP tool integration is covered separately in
///     <c>CustomAppGenerationToolsTests</c>.
/// </summary>
public class GitHubRepoApiClientTests
{
    [Fact]
    public async Task CreateAsync_201Body_ReturnsCreatedWithParsedFields()
    {
        var handler = new SequencedHandler(
            // POST /user/repos
            new Canned(HttpStatusCode.Created, """
                {
                  "id": 9876543,
                  "full_name": "gerald/customer-list",
                  "clone_url": "https://github.com/gerald/customer-list.git",
                  "ssh_url": "git@github.com:gerald/customer-list.git",
                  "default_branch": "main",
                  "owner": { "login": "gerald" }
                }
                """));
        var client = MakeClient(handler);

        var result = await client.CreateAsync(
            "ghp_fake", name: "customer-list",
            description: null, isPrivate: true, org: null);

        result.Outcome.Should().Be(GitHubRepoCreateOutcome.Created);
        result.Repo.Should().NotBeNull();
        result.Repo!.Owner.Should().Be("gerald");
        result.Repo.FullName.Should().Be("gerald/customer-list");
        result.Repo.CloneUrl.Should().Be("https://github.com/gerald/customer-list.git");
        result.Repo.SshUrl.Should().Be("git@github.com:gerald/customer-list.git");
        result.Repo.DefaultBranch.Should().Be("main");
        result.Repo.RepoId.Should().Be(9876543);

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/user/repos");
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("ghp_fake");
        handler.RequestBodies[0].Should().Contain("\"name\":\"customer-list\"");
        handler.RequestBodies[0].Should().Contain("\"private\":true");
        handler.RequestBodies[0].Should().Contain("\"auto_init\":true",
            "auto_init makes the repo cloneable without a prior push");
    }

    [Fact]
    public async Task CreateAsync_WithOrg_PostsToOrgsEndpoint()
    {
        var handler = new SequencedHandler(
            new Canned(HttpStatusCode.Created, """
                {
                  "id": 1, "full_name": "meshmakers/app",
                  "clone_url": "https://github.com/meshmakers/app.git",
                  "ssh_url": "git@github.com:meshmakers/app.git",
                  "default_branch": "main", "owner": { "login": "meshmakers" }
                }
                """));
        var client = MakeClient(handler);

        await client.CreateAsync("ghp_fake", "app", null, true, "meshmakers");

        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/orgs/meshmakers/repos");
    }

    [Fact]
    public async Task CreateAsync_422WithExistingRepo_ResolvesViaUserAndRepoCalls_ToConflict()
    {
        // GitHub returns 422 for both "name exists" AND other validation failures. The
        // client disambiguates by calling GET /user (to find the PAT owner) then
        // GET /repos/{owner}/{name}. When the second 200s the result is Conflict and
        // carries the existing repo's URLs.
        var handler = new SequencedHandler(
            new Canned(HttpStatusCode.UnprocessableEntity, """{"message": "Repository creation failed."}"""),
            new Canned(HttpStatusCode.OK, """{"login": "gerald"}"""),
            new Canned(HttpStatusCode.OK, """
                {
                  "id": 555, "full_name": "gerald/customer-list",
                  "clone_url": "https://github.com/gerald/customer-list.git",
                  "ssh_url": "git@github.com:gerald/customer-list.git",
                  "default_branch": "main", "owner": { "login": "gerald" }
                }
                """));
        var client = MakeClient(handler);

        var result = await client.CreateAsync(
            "ghp_fake", "customer-list", null, true, org: null);

        result.Outcome.Should().Be(GitHubRepoCreateOutcome.Conflict);
        result.Repo.Should().NotBeNull();
        result.Repo!.CloneUrl.Should().Be("https://github.com/gerald/customer-list.git");
        handler.Requests.Should().HaveCount(3, "POST repos → GET user → GET repos/{owner}/{name}");
        handler.Requests[1].RequestUri!.PathAndQuery.Should().Be("/user");
        handler.Requests[2].RequestUri!.PathAndQuery.Should().Be("/repos/gerald/customer-list");
    }

    [Fact]
    public async Task CreateAsync_422WhenRepoLookupFails_RemainsUnexpectedError()
    {
        // If the disambiguation calls 404 (no repo by that name) the 422 was something
        // else — typically an invalid org slug or a name pattern GitHub rejects. The
        // client surfaces UnexpectedError with the body so the agent's response shows
        // the actionable hint.
        var handler = new SequencedHandler(
            new Canned(HttpStatusCode.UnprocessableEntity, """{"message": "Validation failed."}"""),
            new Canned(HttpStatusCode.OK, """{"login": "gerald"}"""),
            new Canned(HttpStatusCode.NotFound, """{"message": "Not Found"}"""));
        var client = MakeClient(handler);

        var result = await client.CreateAsync("ghp_fake", "weird-name", null, true, null);

        result.Outcome.Should().Be(GitHubRepoCreateOutcome.UnexpectedError);
        result.ErrorMessage.Should().Contain("422");
    }

    [Fact]
    public async Task CreateAsync_401_ReturnsUnauthorisedWithRotationHint()
    {
        var handler = new SequencedHandler(
            new Canned(HttpStatusCode.Unauthorized, """{"message": "Bad credentials"}"""));
        var client = MakeClient(handler);

        var result = await client.CreateAsync("ghp_expired", "app", null, true, null);

        result.Outcome.Should().Be(GitHubRepoCreateOutcome.Unauthorised);
        result.ErrorMessage.Should().Contain("Rotate");
    }

    [Fact]
    public async Task CreateAsync_403_ReturnsUnauthorisedWithScopeHint()
    {
        var handler = new SequencedHandler(
            new Canned(HttpStatusCode.Forbidden, """{"message": "Resource not accessible by integration"}"""));
        var client = MakeClient(handler);

        var result = await client.CreateAsync("ghp_fake", "app", null, true, "meshmakers");

        result.Outcome.Should().Be(GitHubRepoCreateOutcome.Unauthorised);
        result.ErrorMessage.Should().Contain("scope");
    }

    [Fact]
    public async Task CreateAsync_NetworkFailure_ReturnsUnexpectedError()
    {
        var handler = new ThrowingHandler();
        var client = MakeClient(handler);

        var result = await client.CreateAsync("ghp_fake", "app", null, true, null);

        result.Outcome.Should().Be(GitHubRepoCreateOutcome.UnexpectedError);
        result.ErrorMessage.Should().Contain("unreachable");
    }

    private static GitHubRepoApiClient MakeClient(HttpMessageHandler handler)
    {
        var factory = new SingleHandlerFactory(handler);
        return new GitHubRepoApiClient(factory, NullLogger<GitHubRepoApiClient>.Instance);
    }

    private sealed record Canned(HttpStatusCode StatusCode, string Body);

    private sealed class SequencedHandler(params Canned[] responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();
        private int _index;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var canned = responses[_index++];
            return new HttpResponseMessage(canned.StatusCode)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated DNS failure");
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
