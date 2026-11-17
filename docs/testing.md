## Test Infrastructure

`tests/McpServices.Tests/` uses xUnit + Moq + FluentAssertions.

- `TestBase` — base mocks (`McpServer`, `TestServiceProvider`, `IOctoHttpContextAccessor`, `ITenantResolutionService`, `ICkCacheService`, `ITenantRepository` + its `ISecureSessionFactory` face) plus a `DefaultHttpContext` carrying the authenticated request principal the direct-engine tools derive their caller identity from, and an `IMcpSessionTokenStore` holding the matching home token for the cross-tenant exchange path (AB#5030 — see *Caller identity + tenant gate* above). The parameterless `GetSessionAsync()` throws on purpose.
- `ToolTestBase : TestBase` — adds `IMcpSessionTokenStore` + `IOctoServiceClientFactory` mocks plus 6 per-SDK-client mocks (`MockIdentityClient`, `MockAssetClient`, `MockCommunicationClient`, `MockStreamDataClient`, `MockReportingClient`, `MockBotClient`) and the real `FileTransferStore`. Helpers: `GivenAuthenticated()`, `GivenUnauthenticated()`, `GivenTokenExpired()`.
  - **`GivenAuthenticated()` returns the token it stored** and defaults to a JWT bound to the request principal `TestBase` installs (`sub=test-subject`, `tenant_id=test-tenant`). Since AB#5036 an opaque placeholder would be refused by the binding check, so assert against the returned value instead of a literal. Passing an explicit token is still allowed — for a token belonging to another identity (a denial test) or when the caller is a service principal.
  - `MockTokenExchanger` defaults to an **identity-preserving** exchange (returns the `subject_token` back) because the default session token now carries a readable `tenant_id`: a test that points a tool at another tenant takes the AB#4338 exchange path and still sees the token it arranged. Tests about the exchange itself override this setup.
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

- **`ckTypeId` format of the type tools (AB#6998).** `search_types` / `get_available_types` return the canonical full id
  in `ckTypeId` (`Industry.Basic-2.6.0/Machine-1`, the same as `get_type_schema` returns) and the runtime id in
  `rtCkTypeId` (`Industry.Basic/Machine`; model without version, element version `1` is not spelled). `TypeId` /
  `TypeName` stay the display names. `get_type_schema`, `get_available_archive_paths`, `get_association_tree` and the aggregation tools
  (`query_entities_aggregation`, `query_entities_grouping`) resolve their `ckTypeId` through `CkTypeIdResolver`: the full
  id, the runtime id and unambiguous short spellings (`Industry.Basic-2/Machine`, `Industry.Basic-2.6.0/Machine`) all
  find the installed type; an unknown or ambiguous id returns an error that lists the full candidate ids ("Did you
  mean ..."). A major-only spelling is ambiguous when two minors of the model are installed - then use the full id.
  AB#7553: the generic CRUD and query tools (`query_entities`, `query_entities_simple`, `get_entity_by_id`,
  `create_entity`, `create_entity_with_secrets`, `update_entity`, `set_entity_secrets`, `delete_entity`) resolve their
  `ckTypeId` through the same resolver (`RuntimeEntityCrudTools.ResolveCkTypeAsync`): they take the runtime id for
  the RtCkId/RtEntityId engine calls and the full id for `CreateTransientRtEntityAsync`. `SecretAttributePaths` resolves
  through the resolver too and throws for an id that does not resolve (no swallowed catch, so an unknown type is never
  reported as "not a Secret attribute"). Not yet routed: `navigate_associations` and `export_..._by_deep_graph`
  (`CkModelFileTools`); they still parse the strict runtime id. Tests: `CkTypeIdRoundTripTests`,
  `RuntimeEntityCrudToolsCkTypeIdTests` (cache and repository behave like the real ones: the id spelling matters).
- **CkTypeId format is `Name-VersionUint`, not SemVer.** `new CkTypeId("MyType-1")` works; `new CkTypeId("MyType-1.0.0")` throws because the SDK reflection-constructs `CkTypeId` from the second path segment and parses the version as `uint`.
- **`OctoObjectId` must be a 24-char hex string.** Use realistic values like `"507f1f77bcf86cd799439011"` in tests.
- **Moq method matchers must use the right type-param.** For methods that take `IEnumerable<T>`, match with `It.IsAny<IEnumerable<T>>()`, not `It.IsAny<List<T>>()`.

### CI: tests in Azure Pipelines

`devops-build/azure-pipelines.yml` runs the full test suite on every push to `main`, `dev/*` and `test/*` branches. The relevant step:

```yaml
- task: DotNetCoreCLI@2
  displayName: 'Test (unit + integration)'
  inputs:
    command: 'test'
    arguments: '--configuration $(buildConfiguration) /p:OctoNugetPrivateServer=$(nugetPrivateServer) --logger "console;verbosity=detailed" --collect:"XPlat Code Coverage"'
    projects: |
      **/*Tests.csproj
      !**/*SystemTests.csproj
    testRunTitle: 'McpServices CI - $(Build.BuildNumber)'
    publishTestResults: true
- task: PublishCodeCoverageResults@2
  displayName: 'Publish code coverage'
  condition: succeededOrFailed()
  inputs:
    summaryFileLocation: '$(Agent.TempDirectory)/**/coverage.cobertura.xml'
```

Notes:

- **Test config is `Release`** (not `DebugL`). The test step uses `$(buildConfiguration) = Release` and the published NuGet packages from `$(nugetPrivateServer)` — local-only DebugL packages from `../nuget/` are not available on the agent. Mirror this locally with `dotnet test Octo.McpServices.sln -c Release` when you suspect a config-sensitive break.
- **Results land in the Azure DevOps Tests tab** under run title `McpServices CI - <buildNumber>`. Failed tests show stack traces and console output thanks to the `console;verbosity=detailed` logger.
- **Code coverage** is collected via `coverlet.collector` (already referenced in `McpServices.Tests.csproj`) and surfaced in the Code Coverage tab of the build. Cobertura XML lands in `$(Agent.TempDirectory)`.
- **Test glob excludes `*SystemTests.csproj`** so a future `McpServices.SystemTests` project (real-service integration suite) can be added later without breaking the main build — those would need their own pipeline + Testcontainers env, matching the pattern in `octo-identity-services`.

CI build-time settings (root `azure-pipelines.yml`, AB#5711 phase 0):

- **Docs-only commits do not start CI.** The `trigger:` excludes `**/*.md`, `*.md`, `.claude/` and `docs/`. A commit touching only those paths builds nothing; the change ships with the next code commit.
- **NuGet restore uses the HTTP cache.** `purge-private-nuget-http-cache.yml` (octo-pipeline-templates) drops only the private feed's cache entries before the restore, so floating `0.1.*` ranges still see freshly published packages; `--force` and `noCache: true` are gone (`checkout: clean: true` already rules out stale `obj/`).
- **Docker layer reuse** comes from the template pin (since `tpl-v0.6.5`, now `tpl-v1.7.0`): a registry cache (`octo-mesh-mcp-services:buildcache`) plus the buildx builder `mybuilder`, which now survives between runs on the same agent.

The current suite is ~877 mock-based unit tests + a handful of in-process integration tests (`McpServerIntegrationTests`). If you add real-service-dependent tests, put them in a separate `*SystemTests` project so they're skipped here.
