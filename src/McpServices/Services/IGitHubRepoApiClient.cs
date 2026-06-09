namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Thin wrapper around GitHub's REST <c>POST /user/repos</c> /
///     <c>POST /orgs/{org}/repos</c> endpoints (#4146). The implementation is the only place
///     in the MCP service that talks to GitHub directly; the
///     <c>create_tenant_app_repo</c> MCP tool delegates here so the tool stays small enough
///     to test against a faked client.
/// </summary>
public interface IGitHubRepoApiClient
{
    /// <summary>
    ///     Creates a new private (or public) repo under the PAT-owner's user account, OR under
    ///     the supplied org. Returns the structured outcome — including a Conflict result when
    ///     GitHub returns HTTP 422 because a repo of that name already exists.
    /// </summary>
    /// <param name="accessToken">
    ///     PAT plaintext. The agent reads it from <c>$GH_TOKEN</c> on the worker pod (the
    ///     materialiser wrote it there) and passes it as a parameter on the MCP tool call.
    /// </param>
    /// <param name="name">
    ///     New repo name. GitHub validates this against its own pattern; we pre-validate the
    ///     obvious cases (empty, contains <c>/</c>) at the tool layer.
    /// </param>
    /// <param name="description">Optional short description shown on the GitHub repo page.</param>
    /// <param name="isPrivate">
    ///     <c>true</c> creates a private repo. Default at the tool layer is <c>true</c> —
    ///     tenant code shouldn't accidentally land in a public repo.
    /// </param>
    /// <param name="org">
    ///     When set, creates the repo under <c>POST /orgs/{org}/repos</c>. When null, creates
    ///     under the PAT-owner's user account (<c>POST /user/repos</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<GitHubRepoCreateResult> CreateAsync(
        string accessToken,
        string name,
        string? description,
        bool isPrivate,
        string? org,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Outcome of <see cref="IGitHubRepoApiClient.CreateAsync" />. Discriminates success,
///     name-collision conflict, auth failure, and unexpected errors so the MCP tool can map
///     each into the right <c>CreateTenantAppRepoResponse</c> shape.
/// </summary>
public sealed class GitHubRepoCreateResult
{
    /// <summary>Outcome category.</summary>
    public required GitHubRepoCreateOutcome Outcome { get; init; }

    /// <summary>Free-text error message for non-success outcomes.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Populated on <see cref="GitHubRepoCreateOutcome.Created" /> and <see cref="GitHubRepoCreateOutcome.Conflict" />.</summary>
    public GitHubRepoInfo? Repo { get; init; }
}

/// <summary>Subset of GitHub's repo-create response we surface upstream.</summary>
public sealed class GitHubRepoInfo
{
    /// <summary>Owner login (user or org).</summary>
    public required string Owner { get; init; }

    /// <summary>Full <c>owner/name</c> identifier.</summary>
    public required string FullName { get; init; }

    /// <summary>HTTPS clone URL.</summary>
    public required string CloneUrl { get; init; }

    /// <summary>SSH clone URL.</summary>
    public required string SshUrl { get; init; }

    /// <summary>Default branch name (typically <c>main</c>).</summary>
    public required string DefaultBranch { get; init; }

    /// <summary>GitHub's numeric repository id.</summary>
    public required long RepoId { get; init; }
}

/// <summary>Discriminated outcome of a repo-create attempt.</summary>
public enum GitHubRepoCreateOutcome
{
    /// <summary>Repo created successfully. <see cref="GitHubRepoCreateResult.Repo" /> is set.</summary>
    Created,

    /// <summary>A repo by that name already exists. <see cref="GitHubRepoCreateResult.Repo" /> carries the existing URLs.</summary>
    Conflict,

    /// <summary>The PAT was rejected (401) or has insufficient scopes (403). The operator must rotate the binding.</summary>
    Unauthorised,

    /// <summary>Any other failure mode (network, 5xx, malformed body). The tool surfaces <see cref="GitHubRepoCreateResult.ErrorMessage" />.</summary>
    UnexpectedError,
}
