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
    private List<DeploymentSiteSummaryDto> _sites = [];
    private List<AdapterSummaryDto> _adapters = [];
    private int _siteReads;

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
        MockAssetClient.Setup(c => c.GetTenantFeaturesStatusAsync()).ReturnsAsync(() => _features);
        MockCommunicationClient.Setup(c => c.EnableAsync(Child))
            .Callback(() =>
            {
                _features = Status(true, _features.AiServices?.TenantEnabled == true);
                GivenFreshDefaults();
            })
            .Returns(Task.CompletedTask);
        MockCommunicationClient.Setup(c => c.GetDeploymentSitesAsync()).ReturnsAsync(() =>
        {
            if (++_siteReads > _controllerDeploysAfterReads && _platformEnabledCommunication)
            {
                _sites = [Site(EntityDeploymentState.Deployed)];
                _adapters = [Adapter(EntityDeploymentState.Deployed)];
            }

            return _sites.ToArray();
        });
        MockCommunicationClient.Setup(c => c.GetAdaptersAsync()).ReturnsAsync(() => _adapters.ToArray());
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
                    _features = Status(true, true);
                    _platformEnabledCommunication = true;
                    GivenFreshDefaults();
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

    private static DeploymentSiteSummaryDto Site(EntityDeploymentState state) => new()
    {
        RtId = SiteId, Name = "default-site", DeploymentState = state,
        CommunicationState = CommunicationState.Offline, ConfigurationState = ConfigurationState.Configured
    };

    private static AdapterSummaryDto Adapter(EntityDeploymentState state) => new()
    {
        RtId = AdapterRtId, Name = "mesh-adapter", DeploymentState = state,
        CommunicationState = CommunicationState.Offline, ConfigurationState = ConfigurationState.Configured
    };

    /// <summary>What EnableCommunication creates for a fresh tenant: one Cloud site with one adapter, both Undeployed.</summary>
    private void GivenFreshDefaults()
    {
        _sites = [Site(EntityDeploymentState.Undeployed)];
        _adapters = [Adapter(EntityDeploymentState.Undeployed)];
    }

    private IEnumerable<string?> DeployCalls =>
        CkMethods.OperationNames.Where(n => n is "SystemCommunicationDeploymentSiteDeploy" or "SystemCommunicationAdapterDeploy");

    private static TenantFeaturesStatusDto Status(bool communication, bool ai) => new()
    {
        AiServices = new TenantFeatureStatusDto { TenantEnabled = ai },
        Communication = new TenantFeatureStatusDto { TenantEnabled = communication },
        Reporting = new TenantFeatureStatusDto { TenantEnabled = false },
        StreamData = new StreamDataFeatureStatusDto { InstanceEnabled = true, TenantEnabled = false }
    };

    private Task<CreateTenantResponse> CreateStarter(string? blueprintId = null, bool scratch = false) =>
        TenantManagementTools.CreateTenant(MockServer.Object, Child, Database, scratch: scratch, starter: true,
            starterBlueprintId: blueprintId);

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
        result.Message.Should().Contain("app-ready");
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
        MockCommunicationClient.Verify(c => c.GetDeploymentSitesAsync(),
            Times.Exactly(1 + StarterTenantSequence.DeployWaitAttempts));
    }

    /// <summary>AB#8452: an adapter the controller left Pending is not deployed again; only the Undeployed site is.</summary>
    [Fact]
    public async Task Starter_DeployDefaults_DeploysOnlyWhatIsUndeployed()
    {
        _platformEnables = false;
        MockCommunicationClient.Setup(c => c.GetAdaptersAsync())
            .ReturnsAsync(() => [Adapter(EntityDeploymentState.Pending)]);

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        DeployCalls.Should().Equal("SystemCommunicationDeploymentSiteDeploy");
    }

    /// <summary>
    ///     AB#8452: a tenant whose Communication was enabled before the call keeps its sites as they are, even when they
    ///     are Undeployed and the tenant has the fresh shape.
    /// </summary>
    [Fact]
    public async Task Starter_TenantHadCommunicationAlready_SiteAndAdapterAreNotTouched()
    {
        _children.Add(new TenantDto { TenantId = Child, Database = Database });
        _features = Status(true, false);
        GivenFreshDefaults();

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Outcomes(result).Should().Equal("create_tenant=skipped", "provision_admin=skipped", "install_blueprint=done",
            "enable_communication=skipped", "deploy_defaults=skipped", "enable_ai=done", "default_spend_cap=skipped",
            "verify_ready=done");
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Detail.Should().Contain("left as they are");
        DeployCalls.Should().BeEmpty();
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>AB#8452: not the shape of a fresh tenant (two sites): nothing is deployed, the sequence goes on.</summary>
    [Fact]
    public async Task Starter_FallbackWithoutTheFreshShape_DeploysNothing()
    {
        _platformEnables = false;
        MockCommunicationClient.Setup(c => c.GetDeploymentSitesAsync()).ReturnsAsync(() =>
            [
                Site(EntityDeploymentState.Undeployed),
                new DeploymentSiteSummaryDto
                {
                    RtId = "6a0000000000000000000009", Name = "second-site",
                    DeploymentState = EntityDeploymentState.Undeployed,
                    CommunicationState = CommunicationState.Offline, ConfigurationState = ConfigurationState.Configured
                }
            ]);

        var result = await CreateStarter();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Outcome.Should().Be("skipped");
        result.StarterSteps.Single(s => s.Step == "deploy_defaults").Detail.Should().Contain("2 deployment site(s)");
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
        var result = await TenantManagementTools.CreateTenant(MockServer.Object, Child, Database);

        result.IsSuccess.Should().BeTrue();
        result.StarterSteps.Should().BeEmpty();
        _readyProbes.Should().Be(0);
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.ApplyStarterDefaultSpendCapAsync(It.IsAny<string>()), Times.Never);
    }
}
