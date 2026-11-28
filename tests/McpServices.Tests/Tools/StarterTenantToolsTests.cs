using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AiServices;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#9570: <c>create_tenant starter=true</c> leaves a new tenant app-ready in one call (create, administrator,
///     Communication, AI Services, System.Ai.Starter), reports every step, refuses a client-credentials caller before it
///     creates anything, and completes the remaining steps on a second call without repeating finished ones.
/// </summary>
public class StarterTenantToolsTests : ToolTestBase
{
    private const string Child = "starter1";
    private const string Database = "starter1_db";

    private readonly List<TenantDto> _children = [];
    private readonly List<BlueprintInstallationDto> _installations = [];
    private TenantFeaturesStatusDto _features = Status(false, false);
    private readonly Dictionary<string, string?> _readyMissing = [];
    private int _readyProbes;

    // AB#8452: the platform (AB#9578) enables Communication and AI when the starter is installed; the controller then
    // deploys the default site and adapter after _controllerDeploysAfterReads reads of the sites (null = never).
    private bool _platformEnables = true;
    private int? _controllerDeploysAfterReads = 1;
    private TenantFeaturesStatusDto? _featuresAtInstall;
    private List<StarterDefaultsEntity> _sites = [];
    private List<StarterDefaultsEntity> _adapters = [];
    private int _siteReads;

    // The tenant shape the controller's rule looks at (Kassandra round 1, finding 2).
    private bool _siteIsCloud = true;
    private int _otherWorkloads;
    private int _freshSiteCount = 1;
    private int _freshAdapterState = Undeployed;

    // The platform enables Communication (and AI) only after this many feature reads after the install (0 = at once),
    // and AI only after this many further reads.
    private int _platformEnableAfterReads;
    private int _aiEnableAfterReads;
    private bool _platformEnablePending;
    private int _aiPendingReads = -1;

    public StarterTenantToolsTests()
    {
        StarterTenantSequence.RetryDelay = TimeSpan.Zero;
        StarterTenantSequence.WaitDelay = TimeSpan.Zero;
        StarterTenantSequence.EnableWaitAttempts = 3;
        StarterTenantSequence.DeployWaitAttempts = 3;
        StarterTenantSequence.ReadyProbe = (_, tenant) =>
        {
            _readyProbes++;
            return Task.FromResult(_readyMissing.TryGetValue(tenant, out var m) ? m : null);
        };
        GivenAuthenticated();
        // The steps in the child address it by its id (the base resolver maps everything to the home tenant).
        MockTenantResolution.Setup(t => t.ResolveTenantId(Child)).Returns(Child);

        MockAssetClient.Setup(c => c.GetTenantsAsync()).ReturnsAsync(() => _children.ToArray());
        MockAssetClient.Setup(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string id, string db) => _children.Add(new TenantDto { TenantId = id, Database = db }))
            .Returns(Task.CompletedTask);
        MockAssetClient.Setup(c => c.GetTenantFeaturesStatusAsync()).ReturnsAsync(() =>
        {
            if (_platformEnablePending && _platformEnableAfterReads-- <= 0)
            {
                _platformEnablePending = false;
                if (_features.Communication?.TenantEnabled != true)
                {
                    PlatformEnables();
                }
            }

            if (_aiPendingReads >= 0 && _aiPendingReads-- == 0)
            {
                _features = Status(true, true);
            }

            return _features;
        });
        MockCommunicationClient.Setup(c => c.EnableAsync(Child))
            .Callback(() =>
            {
                _features = Status(true, _features.AiServices?.TenantEnabled == true);
                GivenFreshDefaults();
            })
            .Returns(Task.CompletedTask);
        StarterTenantSequence.DefaultsProbe = (_, _, _) => Task.FromResult(ReadDefaults());
        _ = CkMethods; // the deploy calls of deploy_defaults succeed
        MockAiClient.Setup(c => c.EnableAsync(Child))
            .Callback(() => _features = Status(_features.Communication?.TenantEnabled == true, true))
            .Returns(Task.CompletedTask);
        MockAssetClient.Setup(c => c.ListBlueprintInstallationsAsync(Child)).ReturnsAsync(() => _installations.ToList());
        MockAssetClient.Setup(c => c.ListBlueprintsAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new BlueprintCatalogListResponseDto
            {
                Items =
                [
                    new BlueprintCatalogItemDto { Id = "a", Name = "System.Ai.Starter", Version = "1.0.0" },
                    new BlueprintCatalogItemDto { Id = "b", Name = "System.Ai.Starter", Version = "1.2.0" },
                    new BlueprintCatalogItemDto { Id = "c", Name = "System.Ai.Starter.Extra", Version = "9.0.0" },
                    new BlueprintCatalogItemDto { Id = "d", Name = "System.Ai.Builder", Version = "1.1.8" }
                ],
                TotalCount = 4
            });
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(Child, It.IsAny<string>(), false))
            .Callback((string _, string id, bool _) =>
            {
                _featuresAtInstall = _features;
                if (_platformEnables && _features.Communication?.TenantEnabled != true)
                {
                    if (_platformEnableAfterReads > 0)
                    {
                        _platformEnablePending = true;
                    }
                    else
                    {
                        PlatformEnables();
                    }
                }

                _installations.Add(new BlueprintInstallationDto { BlueprintId = id });
                _installations.Add(new BlueprintInstallationDto { BlueprintId = "System.Ai.Builder-1.1.8", IsDependency = true });
            })
            .ReturnsAsync((string tenant, string id, bool _) => new BlueprintApplyResultDto
            {
                Success = true, TenantId = tenant, BlueprintId = id, ApplicationMode = "Install", ArchiveActivations = []
            });
    }

    private bool _platformEnabledCommunication;

    private const string SiteId = "6a0000000000000000000001";
    private const string AdapterRtId = "6a0000000000000000000002";

    private const int Undeployed = 0;
    private const int Pending = 1;
    private const int Deployed = 2;

    private static StarterDefaultsEntity Site(int state, string id = SiteId) => new(id, "default-site", state);

    private static StarterDefaultsEntity Adapter(int state) => new(AdapterRtId, "mesh-adapter", state);

    /// <summary>The platform (AB#9578) enables Communication and AI for the starter; EnableCommunication creates the defaults.</summary>
    private void PlatformEnables()
    {
        _features = Status(true, _aiEnableAfterReads == 0);
        if (_aiEnableAfterReads > 0)
        {
            _aiPendingReads = _aiEnableAfterReads;
        }

        _platformEnabledCommunication = true;
        GivenFreshDefaults();
    }

    /// <summary>What EnableCommunication creates for a fresh tenant: one Cloud site with one adapter, both Undeployed.</summary>
    private void GivenFreshDefaults()
    {
        _sites = Enumerable.Range(0, _freshSiteCount).Select(i => Site(Undeployed, $"6a00000000000000000000a{i}")).ToList();
        _sites[0] = Site(Undeployed);
        _adapters = [Adapter(_freshAdapterState)];
    }

    /// <summary>
    ///     The tenant repository's view (StarterDefaults): the controller deploys after _controllerDeploysAfterReads reads
    ///     when the platform enabled Communication; a deploy call of deploy_defaults marks the site or adapter Deployed.
    /// </summary>
    private StarterDefaultsState ReadDefaults()
    {
        if (++_siteReads > _controllerDeploysAfterReads && _platformEnabledCommunication && IsFresh())
        {
            _sites = [Site(Deployed)];
            _adapters = [Adapter(Deployed)];
        }

        if (_sites.Count == 1 && _sites[0].IsUndeployed && DeployCalls.Contains("SystemCommunicationDeploymentSiteDeploy"))
        {
            _sites = [Site(Deployed)];
        }

        if (_adapters.Count == 1 && _adapters[0].IsUndeployed && DeployCalls.Contains("SystemCommunicationAdapterDeploy"))
        {
            _adapters = [Adapter(Deployed)];
        }

        var site = _sites.Count == 1 ? _sites[0] : null;
        var workloads = site == null ? 0 : _adapters.Count + _otherWorkloads;
        var adapter = workloads == 1 && _adapters.Count == 1 ? _adapters[0] : null;
        return new StarterDefaultsState(_sites.Count, site, site != null && _siteIsCloud, workloads, adapter);
    }

    private bool IsFresh() => _sites.Count == 1 && _siteIsCloud && _otherWorkloads == 0 && _adapters.Count == 1;

    private IEnumerable<string?> DeployCalls =>
        CkMethods.OperationNames.Where(n => n is "SystemCommunicationDeploymentSiteDeploy" or "SystemCommunicationAdapterDeploy");

    private static TenantFeaturesStatusDto Status(bool communication, bool ai) => new()
    {
        AiServices = new TenantFeatureStatusDto { TenantEnabled = ai },
        Communication = new TenantFeatureStatusDto { TenantEnabled = communication },
        Reporting = new TenantFeatureStatusDto { TenantEnabled = false },
        StreamData = new StreamDataFeatureStatusDto { InstanceEnabled = true, TenantEnabled = false }
    };

    private Task<CreateTenantResponse> CreateStarter(string? blueprintId = null, bool scratch = false,
        CancellationToken? cancellationToken = null) =>
        TenantManagementTools.CreateTenant(MockServer.Object, Child, Database, scratch: scratch, starter: true,
            starterBlueprintId: blueprintId, cancellationToken: cancellationToken ?? TestContext.Current.CancellationToken);

    private static IEnumerable<string> Outcomes(CreateTenantResponse r) => r.StarterSteps.Select(s => $"{s.Step}={s.Outcome}");

    /// <summary>
    ///     AB#8452 platform path: the starter goes in first, while Communication is still off, so the platform enables
    ///     Communication and AI for it (AB#9578) and the controller deploys the default site and adapter; the sequence
    ///     enables nothing and deploys nothing itself.
    /// </summary>
    [Fact]
    public async Task Starter_InstallsTheStarterFirst_AndLetsThePlatformEnableAndDeploy()
    {
        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.FailedStep.Should().BeNull();
        Outcomes(result).Should().Equal("create_tenant=done", "provision_admin=skipped", "install_blueprint=done",
            "enable_communication=done", "deploy_defaults=skipped", "enable_ai=done", "default_spend_cap=skipped",
            "verify_ready=done");
        _featuresAtInstall!.Communication!.TenantEnabled.Should().BeFalse("the platform only deploys for a tenant " +
                                                                           "whose Communication it enabled itself");
        result.StarterSteps.Single(s => s.Step == "enable_communication").Detail.Should().Contain("by the platform");
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Detail.Should().Contain("already deployed by the platform");
        MockAssetClient.Verify(c => c.CreateTenantAsync(Child, Database), Times.Once);
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        DeployCalls.Should().BeEmpty();
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(Child, "System.Ai.Starter-1.2.0", false), Times.Once);
        // The parent is no system tenant, so no cap: the message says what is still closed (finding 3).
        result.Message.Should().Contain("NOT app-ready for LLM use").And.Contain("set_ai_spend_cap")
            .And.Contain("adapter 'mesh-adapter' Deployed");
        result.StarterSteps.Single(s => s.Step == "verify_ready").Detail.Should().Contain("builder-agent-client")
            .And.Contain("LLM cap: none");
    }

    /// <summary>
    ///     AB#8452 fallback path (Odin's condition): the platform does not enable within the wait, so the sequence enables
    ///     Communication and AI itself - the controller then deploys nothing, so deploy_defaults deploys the site and the
    ///     adapter.
    /// </summary>
    [Fact]
    public async Task Starter_PlatformDoesNotEnable_FallbackEnablesBoth_AndDeploysSiteAndAdapter()
    {
        _platformEnables = false;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Outcomes(result).Should().Equal("create_tenant=done", "provision_admin=skipped", "install_blueprint=done",
            "enable_communication=done", "deploy_defaults=done", "enable_ai=done", "default_spend_cap=skipped",
            "verify_ready=done");
        result.StarterSteps.Single(s => s.Step == "enable_communication").Detail.Should().Contain("fallback");
        result.StarterSteps.Single(s => s.Step == "enable_ai").Detail.Should().Contain("fallback");
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Detail.Should()
            .Contain("site 'default-site'").And.Contain("adapter 'mesh-adapter'");
        MockCommunicationClient.Verify(c => c.EnableAsync(Child), Times.Once);
        MockAiClient.Verify(c => c.EnableAsync(Child), Times.Once);
        DeployCalls.Should().Equal("SystemCommunicationDeploymentSiteDeploy", "SystemCommunicationAdapterDeploy");
        CkMethods.RtIdOf("SystemCommunicationDeploymentSiteDeploy").Should().Be(SiteId);
        CkMethods.RtIdOf("SystemCommunicationAdapterDeploy").Should().Be(AdapterRtId);
    }

    /// <summary>
    ///     AB#8452: the platform enabled Communication but the controller did not deploy within the wait; deploy_defaults
    ///     deploys what is still Undeployed (idempotent: only Undeployed, nothing undeployed).
    /// </summary>
    [Fact]
    public async Task Starter_PlatformEnablesButDoesNotDeploy_DeployDefaultsDeploysAfterTheWait()
    {
        _controllerDeploysAfterReads = null;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Outcome.Should().Be("done");
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Detail.Should().Contain("had not deployed them");
        DeployCalls.Should().Equal("SystemCommunicationDeploymentSiteDeploy", "SystemCommunicationAdapterDeploy");
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        _siteReads.Should().Be(1 + StarterTenantSequence.DeployWaitAttempts + 1,
            "deploy_defaults reads once and then waits DeployWaitAttempts reads; verify_ready reads once");
    }

    /// <summary>AB#8452: an adapter the controller left Pending is not deployed again; only the Undeployed site is.</summary>
    [Fact]
    public async Task Starter_DeployDefaults_DeploysOnlyWhatIsUndeployed()
    {
        _platformEnables = false;
        _freshAdapterState = Pending;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        DeployCalls.Should().Equal("SystemCommunicationDeploymentSiteDeploy");
    }

    /// <summary>
    ///     Finding 1: an existing tenant that no starter call created (blueprints of its own, Communication on, sites) is
    ///     not resumed: create_tenant fails with "exists" and nothing in the tenant is touched.
    /// </summary>
    [Fact]
    public async Task Starter_ExistingForeignTenant_FailsWithExists_AndChangesNothing()
    {
        _children.Add(new TenantDto { TenantId = Child, Database = "other_db" });
        _installations.Add(new BlueprintInstallationDto { BlueprintId = "Samples.PipelineBasics-2.0.0" });
        _features = Status(true, false);
        GivenFreshDefaults();

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.FailedStep.Should().Be("create_tenant");
        result.ErrorMessage.Should().Contain("exists").And.Contain("not a starter tenant");
        result.Database.Should().Be("other_db", "the skip path reports the existing tenant's database");
        Outcomes(result).Should().Equal("create_tenant=failed", "provision_admin=notRun", "install_blueprint=notRun",
            "enable_communication=notRun", "deploy_defaults=notRun", "enable_ai=notRun", "default_spend_cap=notRun",
            "verify_ready=notRun");
        AssertNothingChanged();
    }

    /// <summary>
    ///     Finding 1, the parked tenant: disable_communication keeps the sites (Undeployed) and turns Communication off, so
    ///     the tenant looks fresh. Without a starter it has installations; with a starter its sites are parked. Both are
    ///     refused, so neither the platform enable nor deploy_defaults deploys its pod again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Starter_ParkedTenant_IsNotResumed(bool starterInstalled)
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });
        _installations.Add(new BlueprintInstallationDto
        {
            BlueprintId = starterInstalled ? "System.Ai.Starter-1.2.0" : "Locations.Austria-1.3.0"
        });
        _features = Status(false, false);
        GivenFreshDefaults();

        var result = await CreateStarter();

        result.FailedStep.Should().Be("create_tenant");
        result.ErrorMessage.Should().Contain("exists");
        if (starterInstalled)
        {
            result.ErrorMessage.Should().Contain("parked");
        }

        AssertNothingChanged();
    }

    /// <summary>Finding 1: the caller does not reach an existing tenant - it is never provisioned into it.</summary>
    [Fact]
    public async Task Starter_ExistingTenantWithoutAccess_FailsWithExists_WithoutProvisioning()
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync(It.IsAny<string>(), Child, It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);

        var result = await CreateStarter();

        result.FailedStep.Should().Be("create_tenant");
        result.ErrorMessage.Should().Contain("no access").And.Contain("provision_current_user_as_admin",
            "a first call that failed at provision_admin leaves a tenant only the caller's provisioning can resume");
        CkMethods.OperationNames.Should().NotContain(n => n != null && n.Contains("ProvisionCurrentUser"));
        AssertNothingChanged();
    }

    /// <summary>
    ///     Finding 1: an existing tenant with no installation and no site is what a first starter call leaves when it
    ///     stopped before the install - it is resumed and completed.
    /// </summary>
    [Fact]
    public async Task Starter_ExistingEmptyTenant_IsResumed()
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Outcomes(result).Should().StartWith(["create_tenant=skipped", "provision_admin=skipped", "install_blueprint=done"]);
        result.StarterSteps[0].Detail.Should().Contain("resumed");
    }

    /// <summary>
    ///     Finding 3, the known limit: a resumed starter tenant whose Communication is on but whose site and adapter are
    ///     still Undeployed (an earlier deploy_defaults failed). deploy_defaults leaves them alone, and verify_ready fails
    ///     and says to deploy them by hand instead of claiming "app-ready".
    /// </summary>
    [Fact]
    public async Task Starter_ResumedWithUndeployedDefaults_FailsAtVerifyReady_InsteadOfClaimingReady()
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });
        _installations.Add(new BlueprintInstallationDto { BlueprintId = "System.Ai.Starter-1.2.0" });
        _features = Status(true, true);
        GivenFreshDefaults();

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        Outcomes(result).Should().Equal("create_tenant=skipped", "provision_admin=skipped", "install_blueprint=skipped",
            "enable_communication=skipped", "deploy_defaults=skipped", "enable_ai=skipped", "default_spend_cap=skipped",
            "verify_ready=failed");
        result.ErrorMessage.Should().Contain("Undeployed").And.Contain("by hand").And.NotContain("app-ready");
        DeployCalls.Should().BeEmpty();
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    ///     Round 2, finding 1: a failed starter install may leave its dependency (System.Ai.Builder) recorded; a tenant with
    ///     dependencies only and no site is still the tenant of an earlier starter call and is resumed.
    /// </summary>
    [Fact]
    public async Task Starter_ExistingTenantWithOnlyADependencyLeft_IsResumed()
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });
        _installations.Add(new BlueprintInstallationDto { BlueprintId = "System.Ai.Builder-1.2.1", IsDependency = true });

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Outcomes(result).Should().StartWith(["create_tenant=skipped", "provision_admin=skipped", "install_blueprint=done"]);
    }

    /// <summary>
    ///     Round 2, finding 5: a retry right after a cancelled call (Starter installed, the platform's enable still in
    ///     flight, Communication still off) waits for the platform instead of racing it with its own enable.
    /// </summary>
    [Fact]
    public async Task Starter_RetryWhileThePlatformEnableIsInFlight_WaitsForThePlatform()
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });
        _installations.Add(new BlueprintInstallationDto { BlueprintId = "System.Ai.Starter-1.2.0" });
        _platformEnablePending = true;
        _platformEnableAfterReads = 2;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Outcomes(result).Should().StartWith(["create_tenant=skipped", "provision_admin=skipped",
            "install_blueprint=skipped", "enable_communication=done"]);
        result.StarterSteps.Single(s => s.Step == "enable_communication").Detail.Should().Contain("by the platform");
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    ///     Round 1 finding 6 / round 2 finding 5: the platform enables only after the wait is over, while the fallback has
    ///     already enabled Communication. Communication is enabled once, and site and adapter are deployed once each.
    /// </summary>
    [Fact]
    public async Task Starter_PlatformEnableLandsAfterTheFallback_NothingIsEnabledOrDeployedTwice()
    {
        _platformEnableAfterReads = StarterTenantSequence.EnableWaitAttempts + 2;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.StarterSteps.Single(s => s.Step == "enable_communication").Detail.Should().Contain("fallback");
        MockCommunicationClient.Verify(c => c.EnableAsync(Child), Times.Once);
        DeployCalls.Should().Equal("SystemCommunicationDeploymentSiteDeploy", "SystemCommunicationAdapterDeploy");
    }

    /// <summary>Round 2, finding 4: only Pending or Deployed site and adapter count as ready.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(null)]
    public async Task Starter_AdapterNotPendingOrDeployed_FailsAtVerifyReady(int? adapterState)
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });
        _installations.Add(new BlueprintInstallationDto { BlueprintId = "System.Ai.Starter-1.2.0" });
        _features = Status(true, true);
        _sites = [Site(Deployed)];
        _adapters = [new StarterDefaultsEntity(AdapterRtId, "mesh-adapter", adapterState)];

        var result = await CreateStarter();

        result.FailedStep.Should().Be("verify_ready");
        result.ErrorMessage.Should().Contain("by hand").And.NotContain("app-ready");
    }

    /// <summary>
    ///     Round 2, finding 2: an HTTP client timeout (a TaskCanceledException without a cancelled request) still ends as a
    ///     failed step with the step list and the tenant id, not as a bare error.
    /// </summary>
    [Fact]
    public async Task Starter_CallTimesOut_FailsTheStep_WithTheStepList()
    {
        MockAssetClient.Setup(c => c.ListBlueprintInstallationsAsync(Child))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.FailedStep.Should().Be("install_blueprint");
        result.ErrorMessage.Should().Contain("timed out");
        result.CreatedTenantId.Should().Be(Child);
        Outcomes(result).Should().ContainInConsecutiveOrder("install_blueprint=failed", "enable_communication=notRun");
    }

    private void AssertNothingChanged()
    {
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.ApplyStarterDefaultSpendCapAsync(It.IsAny<string>()), Times.Never);
        DeployCalls.Should().BeEmpty();
    }

    /// <summary>AB#8452: not the shape of a fresh tenant (two sites): nothing is deployed, the sequence goes on.</summary>
    [Fact]
    public async Task Starter_FallbackWithoutTheFreshShape_DeploysNothing()
    {
        _platformEnables = false;
        _freshSiteCount = 2;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Outcome.Should().Be("skipped");
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Detail.Should().Contain("2 deployment site(s)");
        DeployCalls.Should().BeEmpty();
        result.StarterSteps.Single(s => s.Step == "verify_ready").Detail.Should().Contain("not checked");
    }

    /// <summary>
    ///     Finding 2: deploy_defaults applies the controller's rule (StarterDeploymentService) - an Edge site, or a second
    ///     workload on the site next to the adapter, is not the fresh shape, so nothing is deployed.
    /// </summary>
    [Theory]
    [InlineData(false, 0, "is not a Cloud site")]
    [InlineData(true, 1, "hosts 2 workload(s)")]
    public async Task Starter_FallbackOnAShapeTheControllerRefuses_DeploysNothing(bool cloud, int otherWorkloads,
        string reason)
    {
        _platformEnables = false;
        _siteIsCloud = cloud;
        _otherWorkloads = otherWorkloads;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var step = result.StarterSteps.Single(s => s.Step == "deploy_defaults");
        step.Outcome.Should().Be("skipped");
        step.Detail.Should().Contain(reason);
        DeployCalls.Should().BeEmpty();
    }

    /// <summary>AB#8452: a refused deploy fails deploy_defaults; enable_ai and the later steps do not run.</summary>
    [Fact]
    public async Task Starter_DeployRefused_FailsAtDeployDefaults()
    {
        _platformEnables = false;
        CkMethods.Fails("METHOD_FORBIDDEN", "needs CommunicationManagement");

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.FailedStep.Should().Be("deploy_defaults");
        result.ErrorMessage.Should().Contain("METHOD_FORBIDDEN");
        Outcomes(result).Should().EndWith(["deploy_defaults=failed", "enable_ai=notRun", "default_spend_cap=notRun",
            "verify_ready=notRun"]);
        MockAiClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Starter_SecondCall_SkipsEveryFinishedStep()
    {
        (await CreateStarter()).IsSuccess.Should().BeTrue();

        var again = await CreateStarter();

        again.IsSuccess.Should().BeTrue(again.ErrorMessage);
        Outcomes(again).Should().Equal("create_tenant=skipped", "provision_admin=skipped", "install_blueprint=skipped",
            "enable_communication=skipped", "deploy_defaults=skipped", "enable_ai=skipped", "default_spend_cap=skipped",
            "verify_ready=done");
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        MockCommunicationClient.Verify(c => c.EnableAsync(Child), Times.Never);
        MockAiClient.Verify(c => c.EnableAsync(Child), Times.Never);
        DeployCalls.Should().BeEmpty();
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(Child, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Starter_FailingStep_IsNamed_LaterStepsNotRun_AndARetryCompletesOnlyTheRest()
    {
        // Fallback path: the site and adapter are deployed before enable_ai fails, so the retry needs no deploy.
        _platformEnables = false;
        MockAiClient.SetupSequence(c => c.EnableAsync(Child))
            .ThrowsAsync(new InvalidOperationException("Communication is not ready yet"))
            .Returns(() =>
            {
                _features = Status(true, true);
                return Task.CompletedTask;
            });

        var first = await CreateStarter();

        first.IsSuccess.Should().BeFalse();
        first.FailedStep.Should().Be("enable_ai");
        first.ErrorMessage.Should().Contain("enable_ai").And.Contain("Communication is not ready yet");
        Outcomes(first).Should().Equal("create_tenant=done", "provision_admin=skipped", "install_blueprint=done",
            "enable_communication=done", "deploy_defaults=done", "enable_ai=failed", "default_spend_cap=notRun",
            "verify_ready=notRun");

        var second = await CreateStarter();

        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        Outcomes(second).Should().Equal("create_tenant=skipped", "provision_admin=skipped", "install_blueprint=skipped",
            "enable_communication=skipped", "deploy_defaults=skipped", "enable_ai=done", "default_spend_cap=skipped",
            "verify_ready=done");
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(Child, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
        MockCommunicationClient.Verify(c => c.EnableAsync(Child), Times.Once);
        DeployCalls.Should().HaveCount(2, "the first call deployed site and adapter; the retry deploys nothing");
    }

    /// <summary>Finding 4: the platform enables Communication only after some reads, AI even later; nothing is enabled twice.</summary>
    [Fact]
    public async Task Starter_PlatformEnablesLate_AndAiLater_TheSequenceWaitsAndEnablesNothing()
    {
        StarterTenantSequence.EnableWaitAttempts = 10;
        _platformEnableAfterReads = 2;
        _aiEnableAfterReads = 3;

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.StarterSteps.Single(s => s.Step == "enable_communication").Detail.Should().Contain("by the platform");
        result.StarterSteps.Single(s => s.Step == "enable_ai").Detail.Should().Contain("by the platform");
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    ///     Finding 4: the waits take the request's cancellation token. A cancelled request stops at once, names the step
    ///     it stopped at, and runs no fallback enable or deploy.
    /// </summary>
    [Fact]
    public async Task Starter_CancelledRequest_StopsTheWait_AndRunsNoFallback()
    {
        _platformEnables = false;
        StarterTenantSequence.WaitDelay = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource();
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(Child, It.IsAny<string>(), false))
            .Callback(() => cts.Cancel())
            .ReturnsAsync((string tenant, string id, bool _) => new BlueprintApplyResultDto
            {
                Success = true, TenantId = tenant, BlueprintId = id, ApplicationMode = "Install", ArchiveActivations = []
            });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await CreateStarter(cancellationToken: cts.Token);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the 30 s wait is cancelled");
        result.IsSuccess.Should().BeFalse();
        result.FailedStep.Should().Be("enable_communication");
        result.ErrorMessage.Should().Contain("cancelled");
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        DeployCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Starter_UnauthenticatedCaller_IsRefusedBeforeAnythingIsCreated()
    {
        GivenUnauthenticated();

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.StarterSteps.Should().BeEmpty();
        MockAssetClient.Verify(c => c.GetTenantsAsync(), Times.Never);
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>Minor 5: starterBlueprintId must be a System.Ai.Starter blueprint; another id is refused up front.</summary>
    [Fact]
    public async Task Starter_BlueprintIdThatIsNoStarter_IsRefusedBeforeAnythingIsCreated()
    {
        var result = await CreateStarter("Samples.PipelineBasics-2.0.0");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a System.Ai.Starter blueprint").And.Contain("Nothing was created");
        MockAssetClient.Verify(c => c.GetTenantsAsync(), Times.Never);
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Starter_ClientCredentialsCaller_IsRefusedBeforeAnythingIsCreated()
    {
        GivenServicePrincipalCaller("agent-client", DefaultTenantId, "TenantManagement");

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("user session").And.Contain("Nothing was created");
        result.StarterSteps.Should().BeEmpty();
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        MockAssetClient.Verify(c => c.GetTenantsAsync(), Times.Never);
    }

    [Fact]
    public async Task Starter_WithScratch_IsRefusedBeforeAnythingIsCreated()
    {
        var result = await CreateStarter(scratch: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("scratch").And.Contain("Nothing was created");
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Starter_ProvisionsTheCallerAsAdmin_WhenItHasNoTokenForTheNewTenantYet()
    {
        var exchanges = 0;
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync(It.IsAny<string>(), Child, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string subjectToken, string _, CancellationToken _) =>
                ++exchanges == 1 ? null : new McpSessionTokens { AccessToken = subjectToken, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        CkMethods.Succeeds(new RtAdminProvisioningResultRecordDto
        {
            SourceTenantId = DefaultTenantId, SourceUserName = "alice", Outcome = "created"
        });

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.StarterSteps.Single(s => s.Step == "provision_admin").Outcome.Should().Be("done");
        CkMethods.OperationNames.Should().Contain(n => n != null && n.Contains("ProvisionCurrentUser"));
    }

    [Fact]
    public async Task Starter_NoAccessToTheNewTenant_FailsAtProvisioning_WithoutEnablingAnything()
    {
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync(It.IsAny<string>(), Child, It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);
        CkMethods.Succeeds(new RtAdminProvisioningResultRecordDto { SourceTenantId = DefaultTenantId, Outcome = "created" });

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.FailedStep.Should().Be("provision_admin");
        Outcomes(result).Should().Equal("create_tenant=done", "provision_admin=failed", "install_blueprint=notRun",
            "enable_communication=notRun", "deploy_defaults=notRun", "enable_ai=notRun", "default_spend_cap=notRun",
            "verify_ready=notRun");
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Starter_WithoutAStarterInTheCatalogs_FailsAtTheBlueprintStep()
    {
        MockAssetClient.Setup(c => c.ListBlueprintsAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new BlueprintCatalogListResponseDto { Items = [], TotalCount = 0 });

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.FailedStep.Should().Be("install_blueprint");
        result.ErrorMessage.Should().Contain("refresh_blueprint_catalogs");
    }

    [Fact]
    public async Task Starter_ExplicitBlueprintId_IsInstalledAsGiven()
    {
        var result = await CreateStarter("System.Ai.Starter-1.0.0");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(Child, "System.Ai.Starter-1.0.0", false), Times.Once);
        MockAssetClient.Verify(c => c.ListBlueprintsAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    ///     AB#9573: a starter blueprint without the builder agent (e.g. System.Ai.Starter 1.0.0 with System.Ai.Builder 1.1)
    ///     installs, but the tenant is not ready: verify_ready fails and names what is missing; once the agent is there a
    ///     repeated call checks again (verify_ready is never skipped) and succeeds without reinstalling.
    /// </summary>
    [Fact]
    public async Task Starter_WithoutTheBuilderAgent_FailsAtVerifyReady_AndARetryChecksAgain()
    {
        _readyMissing[Child] = "no System.Ai/Agent 'builder-agent'";

        var first = await CreateStarter();

        first.IsSuccess.Should().BeFalse();
        first.FailedStep.Should().Be("verify_ready");
        first.ErrorMessage.Should().Contain("builder-agent").And.Contain("System.Ai.Starter 1.1");
        Outcomes(first).Should().Equal("create_tenant=done", "provision_admin=skipped", "install_blueprint=done",
            "enable_communication=done", "deploy_defaults=skipped", "enable_ai=done", "default_spend_cap=skipped",
            "verify_ready=failed");

        _readyMissing.Remove(Child);
        var second = await CreateStarter();

        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        Outcomes(second).Should().Contain("install_blueprint=skipped").And.EndWith("verify_ready=done");
        _readyProbes.Should().Be(2);
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(Child, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Starter_ReadyProbeThrows_FailsAtVerifyReady()
    {
        StarterTenantSequence.ReadyProbe = (_, _) => throw new InvalidOperationException("repository unavailable");

        var result = await CreateStarter();

        result.FailedStep.Should().Be("verify_ready");
        result.ErrorMessage.Should().Contain("repository unavailable");
    }

    // AB#9572: default_spend_cap. The parent of the test calls is DefaultTenantId; registering it as the system tenant
    // plays the platform operator's starter path.
    private void GivenParentIsTheSystemTenant() =>
        TestServiceProvider.RegisterService<IOptions<OctoSystemConfiguration>>(
            Microsoft.Extensions.Options.Options.Create(new OctoSystemConfiguration { SystemTenantId = DefaultTenantId }));

    private static StarterSpendCapResultDto CapResult(string outcome) => new()
    {
        TenantId = Child, Outcome = outcome, KeySource = outcome == "created" ? "CentralKey" : "None",
        CapMonthlyEur = outcome == "created" ? 20m : 0m, Message = $"cap {outcome}"
    };

    [Fact]
    public async Task Starter_InTheSystemTenant_AppliesTheDefaultCap_Once()
    {
        GivenParentIsTheSystemTenant();
        MockAiClient.SetupSequence(c => c.ApplyStarterDefaultSpendCapAsync(Child))
            .ReturnsAsync(CapResult(StarterSpendCapResultDto.Created))
            .ReturnsAsync(CapResult(StarterSpendCapResultDto.Exists));

        var first = await CreateStarter();
        var again = await CreateStarter();

        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        first.StarterSteps.Single(s => s.Step == "default_spend_cap").Outcome.Should().Be("done");
        first.StarterSteps.Single(s => s.Step == "default_spend_cap").Detail.Should().Be("cap created");
        first.Message.Should().Contain("is app-ready").And.NotContain("NOT app-ready");
        again.Message.Should().Contain("is app-ready", "an existing cap entry keeps the gateway open");
        again.IsSuccess.Should().BeTrue(again.ErrorMessage);
        again.StarterSteps.Single(s => s.Step == "default_spend_cap").Outcome.Should().Be("skipped");
        MockAiClient.Verify(c => c.ApplyStarterDefaultSpendCapAsync(Child), Times.Exactly(2));
    }

    [Fact]
    public async Task Starter_InAChildTenant_SkipsTheDefaultCap_WithoutAskingTheAiService()
    {
        // The default fixture: the parent is no system tenant (a tenant administrator's starter path, abuse case 1).
        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var step = result.StarterSteps.Single(s => s.Step == "default_spend_cap");
        step.Outcome.Should().Be("skipped");
        step.Detail.Should().Contain("not the system tenant").And.Contain("key source None");
        MockAiClient.Verify(c => c.ApplyStarterDefaultSpendCapAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Starter_SystemTenantWithoutConfiguredDefault_SkipsTheCap_AndCompletes()
    {
        GivenParentIsTheSystemTenant();
        MockAiClient.Setup(c => c.ApplyStarterDefaultSpendCapAsync(Child))
            .ReturnsAsync(CapResult(StarterSpendCapResultDto.NotConfigured));

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.StarterSteps.Single(s => s.Step == "default_spend_cap").Outcome.Should().Be("skipped");
        Outcomes(result).Last().Should().Be("verify_ready=done");
    }

    [Fact]
    public async Task Starter_AiServiceRefusesTheCaller_SkipsTheCap_WithTheReason()
    {
        GivenParentIsTheSystemTenant();
        MockAiClient.Setup(c => c.ApplyStarterDefaultSpendCapAsync(Child))
            .ThrowsAsync(new ServiceClientResultException("forbidden", HttpStatusCode.Forbidden));

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var step = result.StarterSteps.Single(s => s.Step == "default_spend_cap");
        step.Outcome.Should().Be("skipped");
        step.Detail.Should().Contain("403").And.Contain("TenantManagement");
        result.Message.Should().Contain("NOT app-ready for LLM use");
    }

    [Fact]
    public async Task Starter_AiServiceError_FailsAtTheCap_AndSkipsTheReadyCheck()
    {
        GivenParentIsTheSystemTenant();
        MockAiClient.Setup(c => c.ApplyStarterDefaultSpendCapAsync(Child))
            .ThrowsAsync(new ServiceClientResultException("boom", HttpStatusCode.InternalServerError));

        var result = await CreateStarter();

        result.IsSuccess.Should().BeFalse();
        result.FailedStep.Should().Be("default_spend_cap");
        Outcomes(result).Should().EndWith(["default_spend_cap=failed", "verify_ready=notRun"]);
        _readyProbes.Should().Be(0);
    }

    [Fact]
    public async Task WithoutStarter_CreateTenant_IsUnchanged()
    {
        var result = await TenantManagementTools.CreateTenant(MockServer.Object, Child, Database, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.StarterSteps.Should().BeEmpty();
        _readyProbes.Should().Be(0);
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.ApplyStarterDefaultSpendCapAsync(It.IsAny<string>()), Times.Never);
    }
}
