using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <inheritdoc />
public sealed class GitHubRepoApiClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubRepoApiClient> logger) : IGitHubRepoApiClient
{
    // Pinned to one of GitHub's stable surfaces. The 2022-11-28 version was current at
    // build time; GitHub guarantees backwards compatibility within a major API version.
    private const string GitHubApiVersion = "2022-11-28";

    // GitHub requires a recognisable User-Agent on every API call; "user-agent must
    // contain a username or application name" is the literal error text otherwise.
    private const string UserAgent = "octo-mesh-mcp-service";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public async Task<GitHubRepoCreateResult> CreateAsync(
        string accessToken,
        string name,
        string? description,
        bool isPrivate,
        string? org,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var client = httpClientFactory.CreateClient("github");
        // BaseAddress is a per-request setter so the factory can be reused across multiple
        // GitHub calls in a single MCP session without stale state.
        client.BaseAddress ??= new Uri("https://api.github.com");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.IsNullOrWhiteSpace(org) ? "user/repos" : $"orgs/{org}/repos");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        request.Content = JsonContent.Create(new CreateRepoBody
        {
            Name = name,
            Description = description,
            Private = isPrivate,
            // AutoInit gives us a default main + an initial commit so `git clone` works
            // without a prior push. The agent's next step is usually a clone, so this
            // saves a "branch not found" round trip.
            AutoInit = true,
        }, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex,
                "GitHub repo-create network failure for name {Name} org {Org}.",
                name, org ?? "<user>");
            return new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.UnexpectedError,
                ErrorMessage = $"GitHub API unreachable: {ex.Message}"
            };
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.Unauthorised,
                ErrorMessage = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "GitHub rejected the PAT (401). Rotate the tenant binding."
                    : "GitHub refused the request (403). The PAT likely lacks the `repo` scope."
            };
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // 422 fires for "name already exists" but also for invalid org / pattern. We
            // disambiguate by reading the existing repo via GET /repos/{owner}/{name} —
            // when that succeeds, it's a real conflict; when it 404s, the 422 was a
            // different validation failure (org doesn't exist, name pattern rejected).
            var owner = string.IsNullOrWhiteSpace(org)
                ? await ResolvePatOwnerLoginAsync(client, accessToken, cancellationToken)
                : org;
            if (!string.IsNullOrWhiteSpace(owner))
            {
                var existing = await TryGetExistingRepoAsync(client, accessToken, owner!, name, cancellationToken);
                if (existing is not null)
                {
                    return new GitHubRepoCreateResult
                    {
                        Outcome = GitHubRepoCreateOutcome.Conflict,
                        Repo = existing,
                        ErrorMessage = $"Repo {owner}/{name} already exists. Reuse it or pick a different name."
                    };
                }
            }
            return new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.UnexpectedError,
                ErrorMessage = $"GitHub 422 (validation failed): {await Truncate(response, cancellationToken)}"
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            return new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.UnexpectedError,
                ErrorMessage = $"GitHub {(int)response.StatusCode}: {await Truncate(response, cancellationToken)}"
            };
        }

        var dto = await response.Content.ReadFromJsonAsync<RepoCreateResponseBody>(JsonOptions, cancellationToken);
        if (dto?.CloneUrl is null || dto.FullName is null || dto.Owner?.Login is null || dto.SshUrl is null)
        {
            return new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.UnexpectedError,
                ErrorMessage = "GitHub returned 201 but the body was missing required fields."
            };
        }

        return new GitHubRepoCreateResult
        {
            Outcome = GitHubRepoCreateOutcome.Created,
            Repo = new GitHubRepoInfo
            {
                Owner = dto.Owner.Login,
                FullName = dto.FullName,
                CloneUrl = dto.CloneUrl,
                SshUrl = dto.SshUrl,
                DefaultBranch = dto.DefaultBranch ?? "main",
                RepoId = dto.Id,
            }
        };
    }

    /// <summary>
    ///     Reads the PAT-owner's login via <c>GET /user</c>. Used only on the 422-conflict
    ///     disambiguation path so the unhappy-path cost stays out of the happy-path.
    /// </summary>
    private async Task<string?> ResolvePatOwnerLoginAsync(
        HttpClient client, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("Accept", "application/vnd.github+json");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            var res = await client.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await res.Content.ReadFromJsonAsync<UserBody>(JsonOptions, cancellationToken);
            return body?.Login;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Reads an existing repo's payload via <c>GET /repos/{owner}/{name}</c>. Returns null
    ///     when the repo doesn't exist or any error fires — the caller treats that as "the 422
    ///     wasn't a name collision."
    /// </summary>
    private async Task<GitHubRepoInfo?> TryGetExistingRepoAsync(
        HttpClient client, string accessToken, string owner, string name, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{name}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("Accept", "application/vnd.github+json");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            var res = await client.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }
            var dto = await res.Content.ReadFromJsonAsync<RepoCreateResponseBody>(JsonOptions, cancellationToken);
            if (dto?.CloneUrl is null || dto.FullName is null || dto.Owner?.Login is null || dto.SshUrl is null)
            {
                return null;
            }
            return new GitHubRepoInfo
            {
                Owner = dto.Owner.Login,
                FullName = dto.FullName,
                CloneUrl = dto.CloneUrl,
                SshUrl = dto.SshUrl,
                DefaultBranch = dto.DefaultBranch ?? "main",
                RepoId = dto.Id,
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> Truncate(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length <= 256 ? body : body[..256] + "…";
    }

    private sealed class CreateRepoBody
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("private")] public bool Private { get; init; }
        [JsonPropertyName("auto_init")] public bool AutoInit { get; init; }
    }

    private sealed class RepoCreateResponseBody
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("full_name")] public string? FullName { get; init; }
        [JsonPropertyName("clone_url")] public string? CloneUrl { get; init; }
        [JsonPropertyName("ssh_url")] public string? SshUrl { get; init; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }
        [JsonPropertyName("owner")] public OwnerBody? Owner { get; init; }
    }

    private sealed class OwnerBody
    {
        [JsonPropertyName("login")] public string? Login { get; init; }
    }

    private sealed class UserBody
    {
        [JsonPropertyName("login")] public string? Login { get; init; }
    }
}
