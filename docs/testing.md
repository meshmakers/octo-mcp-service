---
description: Test base classes and helpers, test file template, pitfalls, and the CI test step and build-time settings of this service.
applies_to: tests/McpServices.Tests/**, azure-pipelines.yml, run-tests.sh
---

# Tests and CI

## Test Infrastructure

`tests/McpServices.Tests/` uses xUnit + Moq + FluentAssertions.

- `TestBase` — base mocks (`McpServer`, `TestServiceProvider`, `IOctoHttpContextAccessor`, `ITenantResolutionService`, `ICkCacheService`, `ITenantRepository` + its `ISecureSessionFactory` face) plus a `DefaultHttpContext` carrying the authenticated request principal the direct-engine tools derive their caller identity from, and an `IMcpSessionTokenStore` holding the matching home token for the cross-tenant exchange path (see *Caller identity + tenant gate* in `tenant-gate.md`). The parameterless `GetSessionAsync()` throws on purpose.
- `ToolTestBase : TestBase` — adds `IMcpSessionTokenStore` + `IOctoServiceClientFactory` mocks plus per-SDK-client mocks (`MockIdentityClient`, `MockAssetClient`, `MockCommunicationClient`, `MockStreamDataClient`, `MockReportingClient`, `MockBotClient`, `MockAiClient`, `MockTenantClient`) and the real `FileTransferStore`. Helpers: `GivenAuthenticated()`, `GivenUnauthenticated()`, `GivenTokenExpired()`.
  - **`GivenAuthenticated()` returns the token it stored** and defaults to a JWT bound to the request principal `TestBase` installs (`sub=test-subject`, `tenant_id=test-tenant`). An opaque placeholder would be refused by the binding check, so assert against the returned value instead of a literal. Passing an explicit token is still allowed — for a token belonging to another identity (a denial test) or when the caller is a service principal.
  - `MockTokenExchanger` defaults to an **identity-preserving** exchange (returns the `subject_token` back) because the default session token now carries a readable `tenant_id`: a test that points a tool at another tenant takes the cross-tenant exchange path and still sees the token it arranged. Tests about the exchange itself override this setup.
- `InternalsVisibleTo("McpServices.Tests")` is set on `McpServices.csproj` so tests can access `FileTransferStore` directly (the interface is `IFileTransferStore`).

### Adding a tests file

```csharp
public class MyToolsTests : ToolTestBase
{
    public MyToolsTests() { GivenAuthenticated(); }

    [Fact]
    public async Task MyTool_HappyPath_CallsSdk()
    {
        MockIdentityClient.Setup(c => c.DoSomething("x")).ReturnsAsync(new SomeDto());

        var result = await MyTools.MyTool(MockServer.Object, "x");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DoSomething("x"), Times.Once);
    }

    [Fact]
    public async Task MyTool_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await MyTools.MyTool(MockServer.Object, "x");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DoSomething(It.IsAny<string>()), Times.Never);
    }
}
```

### Pitfalls to remember

- **CkTypeId format is `Name-VersionUint`, not SemVer.** `new CkTypeId("MyType-1")` works; `new CkTypeId("MyType-1.0.0")` throws because the SDK reflection-constructs `CkTypeId` from the second path segment and parses the version as `uint`.
- **`OctoObjectId` must be a 24-char hex string.** Use realistic values like `"507f1f77bcf86cd799439011"` in tests.
- **Moq method matchers must use the right type-param.** For methods that take `IEnumerable<T>`, match with `It.IsAny<IEnumerable<T>>()`, not `It.IsAny<List<T>>()`.

### CI: tests in Azure Pipelines

The root `azure-pipelines.yml` runs the full test suite on every push to `main`, `dev/*` and `test/*` branches. The relevant step:

```yaml
- task: DotNetCoreCLI@2
  displayName: 'Test'
  inputs:
    command: 'test'
    arguments: '--no-build --configuration $(buildConfiguration) /p:OctoNugetPrivateServer=$(nugetPrivateServer)'
    projects: |
      **/*Tests.csproj
      !**/*SystemTests.csproj
    testRunTitle: 'CI'
```

Notes:

- **Test config is `Release`** (not `DebugL`). The test step uses `$(buildConfiguration) = Release` and the published NuGet packages from `$(nugetPrivateServer)` — local-only DebugL packages from `../nuget/` are not available on the agent. Mirror this locally with `dotnet test Octo.McpServices.sln -c Release` when you suspect a config-sensitive break.
- **Results land in the Azure DevOps Tests tab** under run title `CI`.
- **Test glob excludes `*SystemTests.csproj`** so a real-service integration suite (`McpServices.SystemTests`) can be added later without breaking the main build — those would need their own pipeline + Testcontainers env, matching the pattern in `octo-identity-services`.

CI build-time settings (root `azure-pipelines.yml`):

- **Docs-only commits do not start CI.** The `trigger:` excludes `**/*.md`, `*.md`, `.claude/` and `docs/`. A commit pushed to `main`, `dev/*` or `test/*` that touches only those paths builds nothing; the change ships with the next code commit. The path filter belongs to `trigger:` only: the pipeline has no `pr:` block, so a pull request starts the PR validation build even when it changes only docs.
- **NuGet restore uses the HTTP cache.** `purge-private-nuget-http-cache.yml` (octo-pipeline-templates) drops only the private feed's cache entries before the restore, so floating `0.1.*` ranges still see freshly published packages; `--force` and `noCache: true` are gone (`checkout: clean: true` already rules out stale `obj/`).
- **Docker layer reuse** comes from the shared pipeline templates (pin in `azure-pipelines.yml`): a registry cache (`octo-mesh-mcp-services:buildcache`) plus the buildx builder `mybuilder`, which now survives between runs on the same agent.

The suite is mock-based unit tests + a handful of in-process integration tests (`McpServerIntegrationTests`). If you add real-service-dependent tests, put them in a separate `*SystemTests` project so they're skipped here.
