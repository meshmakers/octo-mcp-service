using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Communication.v4;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using System.Net;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AiServices;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     <c>create_tenant starter=true</c> (AB#9570, Feature AB#8452): leaves a new child tenant app-ready in one call -
///     create the tenant, provision the calling user as its administrator, install the starter blueprint
///     <c>System.Ai.Starter</c> (which brings System.Ai.Builder: the knowledge pages and the builder agent), wait until the
///     platform enabled Communication and AI Services for it (AB#9578), make sure the default site and Mesh Adapter are
///     deployed, apply the default LLM cap (<c>default_spend_cap</c>, AB#9572: only when the parent is the system tenant,
///     else skipped and the tenant stays at key source None) and check the ready state (<see cref="StarterReadyCheck" />,
///     AB#9573).
///     <para>
///     The blueprint goes in FIRST so the platform itself enables Communication for it: only then does the communication
///     controller deploy the default site and adapter (AB#8452, <c>StarterDeploymentService</c>). When the platform has not
///     enabled a capability within <see cref="EnableWaitAttempts" /> reads, <see cref="WaitDelay" /> apart, the sequence
///     enables it itself (fallback); the controller then sees an enabled tenant and deploys nothing, so the step
///     <c>deploy_defaults</c> deploys what is still Undeployed. On the platform path <c>deploy_defaults</c> first waits for
///     the controller and deploys only what it left Undeployed. A tenant whose Communication was already enabled before
///     this call is never touched by <c>deploy_defaults</c>.
///     </para>
///     <para>
///     Every step first checks whether it is already done and is then <c>skipped</c>, so calling the tool again for the
///     same tenant after a failure completes the remaining steps without repeating finished ones. An existing tenant is
///     only resumed when an earlier starter call can have left it so (the caller reaches it, and the starter is installed
///     with no parked site, or it has no installation and no site at all); any other existing tenant fails at
///     <c>create_tenant</c> with "exists". The first failing step stops the sequence; the steps after it are
///     <c>notRun</c>. The waits take the request's cancellation token.
///     </para>
///     <para>
///     The steps in the child run with the caller's token for the child (RFC 8693 exchange), which identity issues only
///     for a user: the tool refuses a client-credentials caller before it creates anything (<see cref="RefusalFor" />).
///     </para>
/// </summary>
internal static class StarterTenantSequence
{
    /// <summary>Name of the starter blueprint the sequence installs.</summary>
    internal const string StarterBlueprintName = "System.Ai.Starter";

    internal const string CreateStep = "create_tenant";
    internal const string ProvisionStep = "provision_admin";
    internal const string CommunicationStep = "enable_communication";
    internal const string AiStep = "enable_ai";
    internal const string DeployStep = "deploy_defaults";
    internal const string SpendCapStep = "default_spend_cap";
    internal const string BlueprintStep = "install_blueprint";
    internal const string ReadyStep = "verify_ready";

    private static readonly string[] Steps =
        [CreateStep, ProvisionStep, BlueprintStep, CommunicationStep, DeployStep, AiStep, SpendCapStep, ReadyStep];

    /// <summary>
    ///     Reads the ready state of the child with the caller's rights: null when ready, else what is missing. Tests
    ///     replace it; the default is <see cref="StarterReadyCheck.MissingAsync(McpServer, string)" />.
    /// </summary>
    internal static Func<McpServer, string, Task<string?>> ReadyProbe { get; set; } = StarterReadyCheck.MissingAsync;

    /// <summary>
    ///     Reads the deployment sites and workloads of the child with the caller's rights (the controller's view, AB#8452).
    ///     Tests replace it; the default is <see cref="StarterDefaults.ReadAsync(McpServer, string, CancellationToken)" />.
    /// </summary>
    internal static Func<McpServer, string, CancellationToken, Task<StarterDefaultsState>> DefaultsProbe { get; set; } =
        StarterDefaults.ReadAsync;

    /// <summary>
    ///     Identity sets a new tenant up asynchronously after the create: the admin provisioning and the token for the
    ///     child are retried this many times, <see cref="RetryDelay" /> apart.
    /// </summary>
    internal const int AccessAttempts = 15;

    /// <summary>Delay between two attempts to reach the new child; tests set it to zero.</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     How often the sequence reads the tenant's features after the blueprint install before it enables a capability
    ///     the platform has not enabled yet (the controller's capability lookup alone may take up to 60 s).
    /// </summary>
    internal static int EnableWaitAttempts { get; set; } = 30;

    /// <summary>
    ///     How often <c>deploy_defaults</c> reads the site and adapter on the platform path before it deploys what the
    ///     controller left Undeployed.
    /// </summary>
    internal static int DeployWaitAttempts { get; set; } = 20;

    /// <summary>Delay between two reads of a wait; tests set it to zero.</summary>
    internal static TimeSpan WaitDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     The reason the starter cannot run for this caller, checked before anything is created; null when it can.
    /// </summary>
    internal static string? RefusalFor(McpServer server, bool scratch, string? starterBlueprintId = null)
    {
        if (!string.IsNullOrWhiteSpace(starterBlueprintId) && !IsStarter(starterBlueprintId))
        {
            return $"starterBlueprintId '{starterBlueprintId}' is not a {StarterBlueprintName} blueprint (expected " +
                   $"'{StarterBlueprintName}-<version>'). Nothing was created.";
        }

        if (scratch)
        {
            return "starter=true cannot be combined with scratch=true: a scratch tenant belongs to a client-credentials " +
                   "client, and the starter steps in the new tenant need a user session. Nothing was created.";
        }

        var caller = McpCallerPrincipal.FromServer(server);
        if (caller is not { IsUserPrincipal: true })
        {
            return "starter=true needs a user session: the steps in the new tenant (enable_communication, enable_ai, " +
                   "install_blueprint) run with the caller's token for that tenant, and identity exchanges only a user's " +
                   "token into a child tenant, not a client-credentials token. Nothing was created. Use a user MCP " +
                   "session, or create the tenant with octo-cli (Create, then EnableCommunication, EnableAi and " +
                   $"InstallBlueprint {StarterBlueprintName}-<version>) with a client of the new tenant.";
        }

        return null;
    }

    /// <summary>Runs the sequence and fills <paramref name="response" />.</summary>
    internal static async Task RunAsync(McpServer server, IAssetServicesClient parentAssetClient, string parentTenantId,
        string childTenantId, string database, string? starterBlueprintId, CreateTenantResponse response,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<StarterStepDto>();
        var run = new RunState();

        string? outcome = null;
        try
        {
            outcome = await RunStepsAsync(server, parentAssetClient, parentTenantId, childTenantId, database,
                starterBlueprintId, steps, run, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The client gave up, or a call timed out (an HTTP client timeout is a TaskCanceledException without a
            // cancelled request): stop here and say where, so a later call resumes from that step.
            var next = Steps.First(s => steps.All(d => d.Step != s));
            steps.Add(Failed(next, cancellationToken.IsCancellationRequested
                ? "the request was cancelled before this step finished; call create_tenant with starter=true again " +
                  "for the same tenant to resume."
                : "a call of this step timed out; call create_tenant with starter=true again for the same tenant to " +
                  "resume."));
        }

        // Name every step, also those that never ran, so the caller sees the whole sequence.
        foreach (var step in Steps.Where(s => steps.All(d => d.Step != s)))
        {
            steps.Add(new StarterStepDto { Step = step, Outcome = "notRun", Detail = "an earlier step failed" });
        }

        response.StarterSteps = steps;
        response.ParentTenantId = parentTenantId;
        response.CreatedTenantId = childTenantId;
        response.Database = run.Database ?? database;

        var failed = steps.FirstOrDefault(s => s.Outcome == "failed");
        if (failed == null)
        {
            response.IsSuccess = true;
            var stepList = "Steps: " + string.Join(", ", steps.Select(s => $"{s.Step}={s.Outcome}")) + ".";
            // "app-ready" only with an open LLM gateway; otherwise the message says what is still closed.
            response.Message = run.CapOpen
                ? $"Tenant '{childTenantId}' is app-ready: {outcome ?? StarterBlueprintName} installed, Communication " +
                  $"and AI Services enabled, builder agent and knowledge base ready, {run.AdapterText}. " + stepList
                : $"Tenant '{childTenantId}' is set up but NOT app-ready for LLM use: {outcome ?? StarterBlueprintName} " +
                  "installed, Communication and AI Services enabled, builder agent and knowledge base ready, " +
                  $"{run.AdapterText}; but it has no central-key LLM cap above 0, so the LLM gateway stays closed until an " +
                  "AiBudgetAdmin sets one (set_ai_spend_cap). " + stepList;
            return;
        }

        response.IsSuccess = false;
        response.FailedStep = failed.Step;
        response.ErrorMessage = $"Step '{failed.Step}' failed for tenant '{childTenantId}': {failed.Detail} " +
                                "Call create_tenant with starter=true again for the same tenant to complete the " +
                                "remaining steps; finished steps are skipped.";
    }

    /// <summary>Runs the steps until one fails; returns the installed starter blueprint id when it got that far.</summary>
    private static async Task<string?> RunStepsAsync(McpServer server, IAssetServicesClient parentAssetClient,
        string parentTenantId, string childTenantId, string database, string? starterBlueprintId,
        List<StarterStepDto> steps, RunState run, CancellationToken cancellationToken)
    {
        // 1. Create the tenant, unless an earlier starter call did (then only a resumable tenant goes on).
        try
        {
            var children = await parentAssetClient.GetTenantsAsync();
            var existing = children?.FirstOrDefault(t =>
                string.Equals(t.TenantId, childTenantId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                run.Database = existing.Database;
                var notResumable = await NotResumableAsync(server, childTenantId, cancellationToken);
                if (notResumable != null)
                {
                    steps.Add(Failed(CreateStep,
                        $"tenant '{childTenantId}' exists under '{parentTenantId}' and is not a starter tenant this " +
                        $"tool can resume: {notResumable}. Nothing was changed. Choose another childTenantId; to make " +
                        "an existing tenant app-ready, install the starter with install_blueprint and enable its " +
                        "capabilities by hand."));
                    return null;
                }

                steps.Add(Skipped(CreateStep,
                    $"tenant '{childTenantId}' (database '{existing.Database}') already exists under " +
                    $"'{parentTenantId}'; resumed as the tenant of an earlier starter call"));
            }
            else
            {
                await parentAssetClient.CreateTenantAsync(childTenantId, database);
                steps.Add(Done(CreateStep, $"tenant '{childTenantId}' (database '{database}') created under '{parentTenantId}'"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(CreateStep, ex.Message));
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. The caller must reach the child: provision it as administrator unless its token exchange already works.
        if (!await EnsureChildAccessAsync(server, parentTenantId, childTenantId, steps, cancellationToken))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var features = await Services.AssetClientContext.TryBuildAsync(server, childTenantId);
        if (features.Error != null)
        {
            steps.Add(Failed(BlueprintStep, features.Error));
            return null;
        }

        // What was enabled BEFORE the blueprint: a tenant that had Communication already keeps its sites as they are.
        TenantFeaturesStatusDto before;
        try
        {
            before = await features.Client!.GetTenantFeaturesStatusAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(BlueprintStep, $"the features of '{childTenantId}' could not be read: {ex.Message}"));
            return null;
        }

        var communicationWasOn = before.Communication?.TenantEnabled == true;

        // 3. The starter blueprint first, so the platform enables Communication and AI for it (AB#9578) and the
        //    controller deploys the default site and adapter (AB#8452).
        var installed = await InstallStarterAsync(server, features.Client!, childTenantId, starterBlueprintId, steps);
        if (installed == null)
        {
            return null;
        }

        // 4. Wait for the platform to enable Communication and AI Services; enable Communication itself when the platform
        //    did not in time (fallback). Also on a resumed call: the platform's enable of an earlier call's install may
        //    still be in flight (e.g. after a cancelled call), and a fallback enable then would race it. The wait ends at
        //    once when both are on.
        var status = await WaitForPlatformEnableAsync(features.Client!, childTenantId, before, steps,
            cancellationToken);
        if (status == null)
        {
            return null;
        }

        var communicationByPlatform = await EnsureCommunicationAsync(server, childTenantId, before, status, steps);
        if (communicationByPlatform == null)
        {
            return null;
        }

        // 5. Default site and adapter, right after Communication (also on the fallback path, before anything else can
        //    fail): idempotent, never on a tenant that had Communication before this call.
        if (!await EnsureDefaultsDeployedAsync(server, childTenantId, communicationWasOn, communicationByPlatform.Value,
                steps, cancellationToken))
        {
            return null;
        }

        // 6. AI Services: enabled by the platform, or by this call when the platform did not in time (fallback).
        if (!await EnsureAiAsync(server, features.Client!, childTenantId, before, status, steps))
        {
            return null;
        }

        // 7. The cluster's default LLM cap (AB#9572): only on the platform operator's path (parent = system tenant).
        cancellationToken.ThrowIfCancellationRequested();
        if (!await ApplyDefaultSpendCapAsync(server, parentTenantId, childTenantId, steps, run))
        {
            return null;
        }

        // 8. Ready state, read with the caller's rights and never skipped (it changes nothing): builder agent, mandate,
        //    client and the app-index page, the default site and adapter, and the LLM cap of step 7.
        cancellationToken.ThrowIfCancellationRequested();
        await VerifyReadyAsync(server, childTenantId, installed, steps, run, cancellationToken);
        return installed;
    }

    /// <summary>
    ///     Invariant: an existing tenant is only resumed when an earlier <c>create_tenant starter=true</c> can have left it
    ///     so - the caller already reaches it (the caller is never provisioned into a tenant it did not just create), and
    ///     either the starter is installed and no site is parked (Communication on, or no site yet), or the tenant has no
    ///     blueprint installation and no deployment site at all. A tenant parked with disable_communication (sites kept,
    ///     Communication off) and every other existing tenant are refused. Returns null when the tenant is resumable, else
    ///     the reason; any read error refuses (fail closed).
    /// </summary>
    private static async Task<string?> NotResumableAsync(McpServer server, string childTenantId,
        CancellationToken cancellationToken)
    {
        var access = await McpSessionContext.ResolveAccessTokenAsync(server, childTenantId);
        if (access.AccessToken == null)
        {
            return "the caller has no access to it. If an earlier create_tenant starter=true of yours created it and " +
                   "failed at provision_admin, run provision_current_user_as_admin for this tenant and call again";
        }

        try
        {
            var asset = await Services.AssetClientContext.TryBuildAsync(server, childTenantId);
            if (asset.Error != null)
            {
                return $"it cannot be read ({asset.Error})";
            }

            var installations = (await asset.Client!.ListBlueprintInstallationsAsync(childTenantId)).ToList();
            var features = await asset.Client!.GetTenantFeaturesStatusAsync();
            var defaults = await DefaultsProbe(server, childTenantId, cancellationToken);
            var starterInstalled = installations.Any(i => IsStarter(i.BlueprintId));
            var communicationOn = features.Communication?.TenantEnabled == true;

            if (starterInstalled && (communicationOn || defaults.SiteCount == 0))
            {
                return null;
            }

            // Dependencies only (e.g. System.Ai.Builder recorded by a failed starter install) count as no installation.
            if (installations.All(i => i.IsDependency) && defaults.SiteCount == 0)
            {
                return null;
            }

            return starterInstalled
                ? $"{StarterBlueprintName} is installed, but Communication is off while {defaults.SiteCount} " +
                  "deployment site(s) exist (parked with disable_communication)"
                : $"it has {installations.Count(i => !i.IsDependency)} blueprint installation(s) without {StarterBlueprintName} and " +
                  $"{defaults.SiteCount} deployment site(s)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"its state could not be read ({ex.Message})";
        }
    }

    /// <summary>
    ///     Step <c>verify_ready</c> (AB#9573): fails when the builder seed is missing, or when the default site or adapter
    ///     is Undeployed or in Error (then no pipeline runs); its detail names the adapter state and the LLM cap state of
    ///     <c>default_spend_cap</c>. A tenant without the fresh shape keeps its sites as they are; the step names that and
    ///     does not judge them.
    /// </summary>
    private static async Task VerifyReadyAsync(McpServer server, string childTenantId, string installed,
        List<StarterStepDto> steps, RunState run, CancellationToken cancellationToken)
    {
        try
        {
            var missing = await ReadyProbe(server, childTenantId);
            if (missing != null)
            {
                steps.Add(Failed(ReadyStep, $"'{installed}' is installed but the tenant is not ready: {missing}. Install a " +
                                            "System.Ai.Starter 1.1 or later (it brings System.Ai.Builder 1.2 with the builder agent)."));
                return;
            }

            var defaults = await DefaultsProbe(server, childTenantId, cancellationToken);
            if (defaults.IsFreshShape)
            {
                var site = defaults.Site!;
                var adapter = defaults.Adapter!;
                if (!site.IsPendingOrDeployed || !adapter.IsPendingOrDeployed)
                {
                    steps.Add(Failed(ReadyStep,
                        $"site '{site.Name}' is {site.StateName} and adapter '{adapter.Name}' is {adapter.StateName}, so " +
                        "no pipeline runs. Deploy them by hand (the site's CK method Deploy, then deploy_workload for " +
                        $"the adapter) and call again. LLM cap: {run.CapText}."));
                    return;
                }

                run.AdapterText =
                    $"adapter '{adapter.Name}' {adapter.StateName} (Online once the operator has rolled it out)";
            }
            else
            {
                run.AdapterText = $"site and adapter not checked: the tenant has {defaults.ShapeText}";
            }

            steps.Add(Done(ReadyStep,
                $"builder agent enabled (mandate write mode Propose, client {StarterReadyCheck.ClientName}) and " +
                $"knowledge page 'app-index' present; {run.AdapterText}; LLM cap: {run.CapText}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(ReadyStep, ex.Message));
        }
    }

    /// <summary>
    ///     True when the existing cap lets LLM calls through: the central key with a monthly cap above 0. Any other key
    ///     source (None, or a source the gateway refuses) or a cap of 0 keeps the gateway closed.
    /// </summary>
    internal static bool IsGatewayOpen(StarterSpendCapResultDto result) =>
        string.Equals(result.KeySource, "CentralKey", StringComparison.OrdinalIgnoreCase) && result.CapMonthlyEur > 0;

    /// <summary>What a run learned on the way, for the database, the ready step and the final message.</summary>
    private sealed class RunState
    {
        /// <summary>Database of the existing tenant on a resumed call; null when this call created it.</summary>
        public string? Database { get; set; }

        /// <summary>
        ///     Whether the LLM gateway is open after <c>default_spend_cap</c>: a central-key cap above 0, created now or kept.
        /// </summary>
        public bool CapOpen { get; set; }

        /// <summary>The cap state in words, for the ready step.</summary>
        public string CapText { get; set; } = "not checked";

        /// <summary>The adapter state in words, for the final message.</summary>
        public string AdapterText { get; set; } = "adapter not checked";
    }

    private static string WaitedText => $"{EnableWaitAttempts * WaitDelay.TotalSeconds:0} s";

    /// <summary>
    ///     After the install: reads the features until the platform enabled Communication and AI Services (AB#9578) or
    ///     <see cref="EnableWaitAttempts" /> reads are used up; returns the last read, null when a read failed.
    /// </summary>
    private static async Task<TenantFeaturesStatusDto?> WaitForPlatformEnableAsync(
        IAssetServicesClient childAssetClient, string childTenantId, TenantFeaturesStatusDto before,
        List<StarterStepDto> steps, CancellationToken cancellationToken)
    {
        var status = before;
        try
        {
            for (var attempt = 1;
                 attempt <= EnableWaitAttempts &&
                 (status.Communication?.TenantEnabled != true || status.AiServices?.TenantEnabled != true);
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (WaitDelay > TimeSpan.Zero)
                {
                    await Task.Delay(WaitDelay, cancellationToken);
                }

                status = await childAssetClient.GetTenantFeaturesStatusAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(CommunicationStep, $"the features of '{childTenantId}' could not be read: {ex.Message}"));
            return null;
        }

        return status;
    }

    /// <summary>
    ///     Step <c>enable_communication</c>: <c>skipped</c> when it was enabled before the install, <c>done</c> "by the
    ///     platform" when the platform enabled it during the wait, else enabled by this call (fallback). Returns null when
    ///     the step failed, else whether the platform enabled it (only then does the controller deploy the defaults).
    /// </summary>
    private static async Task<bool?> EnsureCommunicationAsync(McpServer server, string childTenantId,
        TenantFeaturesStatusDto before, TenantFeaturesStatusDto status, List<StarterStepDto> steps)
    {
        if (before.Communication?.TenantEnabled == true)
        {
            steps.Add(Skipped(CommunicationStep, "Communication was already enabled before the blueprint install"));
            return false;
        }

        if (status.Communication?.TenantEnabled == true)
        {
            steps.Add(Done(CommunicationStep,
                "enabled by the platform for the starter blueprint (AB#9578); the controller deploys the default " +
                "site and adapter"));
            return true;
        }

        var communication = await CommunicationLifecycleTools.EnableCommunication(server, childTenantId);
        if (!communication.IsSuccess)
        {
            steps.Add(Failed(CommunicationStep, communication.ErrorMessage));
            return null;
        }

        steps.Add(Done(CommunicationStep,
            $"the platform had not enabled Communication within {WaitedText}; enabled by this call (fallback). " +
            communication.Message));
        return false;
    }

    /// <summary>
    ///     Step <c>enable_ai</c>: <c>skipped</c> when it was enabled before the install, <c>done</c> "by the platform"
    ///     when the platform enabled it (during the wait or since), else enabled by this call (fallback).
    /// </summary>
    private static async Task<bool> EnsureAiAsync(McpServer server, IAssetServicesClient childAssetClient,
        string childTenantId, TenantFeaturesStatusDto before, TenantFeaturesStatusDto status,
        List<StarterStepDto> steps)
    {
        if (before.AiServices?.TenantEnabled == true)
        {
            steps.Add(Skipped(AiStep, "AI Services were already enabled before the blueprint install"));
            return true;
        }

        try
        {
            // The platform may enable AI late (it needs Communication first): enable only what is still off.
            if (status.AiServices?.TenantEnabled == true ||
                (await childAssetClient.GetTenantFeaturesStatusAsync()).AiServices?.TenantEnabled == true)
            {
                steps.Add(Done(AiStep, "enabled by the platform for the starter blueprint (AB#9578)"));
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(AiStep, $"the features of '{childTenantId}' could not be read: {ex.Message}"));
            return false;
        }

        var ai = await AiLifecycleTools.EnableAi(server, childTenantId);
        if (!ai.IsSuccess)
        {
            steps.Add(Failed(AiStep, ai.ErrorMessage));
            return false;
        }

        steps.Add(Done(AiStep,
            $"the platform had not enabled AI Services within {WaitedText}; enabled by this call (fallback). " +
            ai.Message));
        return true;
    }

    /// <summary>
    ///     Step <c>deploy_defaults</c> (AB#8452). Invariant: it deploys only the shape of a fresh tenant, by the
    ///     controller's rule (exactly one Cloud site hosting exactly one workload, an adapter), only what is <c>Undeployed</c>, never undeploys, and never touches a
    ///     tenant whose Communication was enabled before this call. On the platform path it first waits for the
    ///     controller's own deploy and deploys only what is still Undeployed after the wait; on the fallback path the
    ///     controller deploys nothing, so it deploys at once. A repeated call finds both deployed and is <c>skipped</c>.
    /// </summary>
    private static async Task<bool> EnsureDefaultsDeployedAsync(McpServer server, string childTenantId,
        bool communicationWasOn, bool communicationByPlatform, List<StarterStepDto> steps,
        CancellationToken cancellationToken)
    {
        if (communicationWasOn)
        {
            steps.Add(Skipped(DeployStep,
                "Communication was already enabled before this call; its deployment sites and adapters are left as " +
                "they are"));
            return true;
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, childTenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            steps.Add(Failed(DeployStep, ctx.Error));
            return false;
        }

        try
        {
            var defaults = await DefaultsProbe(server, childTenantId, cancellationToken);
            for (var attempt = 1;
                 communicationByPlatform && attempt <= DeployWaitAttempts && defaults.IsFreshShape &&
                 (defaults.Site!.IsUndeployed || defaults.Adapter!.IsUndeployed);
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (WaitDelay > TimeSpan.Zero)
                {
                    await Task.Delay(WaitDelay, cancellationToken);
                }

                defaults = await DefaultsProbe(server, childTenantId, cancellationToken);
            }

            // The controller's rule (StarterDeploymentService): one Cloud site, exactly one workload on it, an adapter.
            if (!defaults.IsFreshShape)
            {
                steps.Add(Skipped(DeployStep,
                    $"'{childTenantId}' has {defaults.ShapeText}, not the shape of a fresh tenant; nothing was deployed"));
                return true;
            }

            var site = defaults.Site!;
            var adapter = defaults.Adapter!;
            if (!site.IsUndeployed && !adapter.IsUndeployed)
            {
                steps.Add(Skipped(DeployStep,
                    $"site '{site.Name}' ({site.StateName}) and adapter '{adapter.Name}' ({adapter.StateName}) are " +
                    "already deployed" + (communicationByPlatform ? " by the platform" : "")));
                return true;
            }

            var deployed = new List<string>();
            if (site.IsUndeployed)
            {
                var outcome = await CommunicationMethodCall.RunAsync(() =>
                    new RtDeploymentSiteMethodClient(ctx.TenantClient!).DeployAsync(site.RtId));
                if (!outcome.IsSuccess)
                {
                    steps.Add(Failed(DeployStep, $"deploy of site '{site.Name}' failed: {outcome.Error}"));
                    return false;
                }

                deployed.Add($"site '{site.Name}'");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (adapter.IsUndeployed)
            {
                var outcome = await CommunicationMethodCall.RunAsync(() =>
                    new RtAdapterMethodClient(ctx.TenantClient!).DeployAsync(adapter.RtId));
                if (!outcome.IsSuccess)
                {
                    steps.Add(Failed(DeployStep, $"deploy of adapter '{adapter.Name}' failed: {outcome.Error}"));
                    return false;
                }

                deployed.Add($"adapter '{adapter.Name}'");
            }

            steps.Add(Done(DeployStep,
                (communicationByPlatform
                    ? $"the platform had not deployed them within {DeployWaitAttempts * WaitDelay.TotalSeconds:0} s; "
                    : "Communication was enabled by this call, so the platform deploys nothing; ") +
                $"deployed {string.Join(" and ", deployed)} (one adapter pod for this tenant)"));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(DeployStep, ex.Message));
            return false;
        }
    }

    /// <summary>
    ///     Step <c>default_spend_cap</c> (AB#9572). Invariant: central-key LLM spend opens for a tenant only through the
    ///     platform (an <c>AiBudgetAdmin</c>, or once the starter default the AI service takes from cluster
    ///     configuration). So the step calls the AI service only when the parent is the system tenant, with the caller's
    ///     token for the system tenant and without an amount; the service checks the role <c>TenantManagement</c> again
    ///     and never changes an existing entry. Any other parent, an unknown system tenant id or a refusal of the service
    ///     (401/403) skip the step with the reason: the tenant stays at key source None and the gateway stays closed for
    ///     it (fail-safe). Other errors fail the step; a repeated call asks again (the service answers <c>exists</c>).
    /// </summary>
    private static async Task<bool> ApplyDefaultSpendCapAsync(McpServer server, string parentTenantId,
        string childTenantId, List<StarterStepDto> steps, RunState run)
    {
        run.CapText = "none, the LLM gateway stays closed (key source None)";
        var systemTenantId = server.Services?.GetService<IOptions<OctoSystemConfiguration>>()?.Value.SystemTenantId;
        if (string.IsNullOrWhiteSpace(systemTenantId) ||
            !string.Equals(parentTenantId, systemTenantId, StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(Skipped(SpendCapStep,
                "only the platform operator creating the tenant in the system tenant gets the default LLM cap; parent " +
                $"'{parentTenantId}' is not the system tenant, so '{childTenantId}' stays at key source None (LLM " +
                "gateway closed) until an AiBudgetAdmin sets a cap."));
            return true;
        }

        var ai = await Services.AiClientContext.TryBuildAsync(server, systemTenantId);
        if (ai.Error != null)
        {
            steps.Add(Failed(SpendCapStep, ai.Error));
            return false;
        }

        try
        {
            var result = await ai.Client!.ApplyStarterDefaultSpendCapAsync(childTenantId);
            switch (result.Outcome)
            {
                case StarterSpendCapResultDto.Created:
                    run.CapOpen = true;
                    run.CapText = $"default cap created ({result.Message})";
                    steps.Add(Done(SpendCapStep, result.Message));
                    return true;
                case StarterSpendCapResultDto.Exists:
                    // The service keeps any existing entry, also an explicit None or a cap of 0: only a central-key cap
                    // above 0 opens the LLM gateway (docs/llm-gateway.md of octo-ai-services, gateway checks 2 and 4).
                    run.CapOpen = IsGatewayOpen(result);
                    run.CapText = run.CapOpen
                        ? $"a cap entry exists and was kept ({result.KeySource}, EUR {result.CapMonthlyEur} per month)"
                        : $"a cap entry exists and was kept, but it keeps the LLM gateway closed (key source " +
                          $"{result.KeySource}, EUR {result.CapMonthlyEur} per month); an AiBudgetAdmin sets a cap";
                    steps.Add(Skipped(SpendCapStep, result.Message));
                    return true;
                case StarterSpendCapResultDto.NotConfigured:
                    run.CapText = "none: the cluster has no default cap configured, the LLM gateway stays closed";
                    steps.Add(Skipped(SpendCapStep, result.Message));
                    return true;
                default:
                    steps.Add(Failed(SpendCapStep,
                        $"unexpected answer '{result.Outcome}' of the AI service: {result.Message}"));
                    return false;
            }
        }
        catch (ServiceClientResultException ex) when (ex.HttpStatusCode is HttpStatusCode.Forbidden
                                                          or HttpStatusCode.Unauthorized)
        {
            steps.Add(Skipped(SpendCapStep,
                $"the AI service refused the default cap for this caller ({(int)ex.HttpStatusCode}: it needs the role " +
                $"TenantManagement in the system tenant); '{childTenantId}' stays at key source None (LLM gateway " +
                "closed) until an AiBudgetAdmin sets a cap."));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(SpendCapStep, ex.Message));
            return false;
        }
    }

    private static async Task<bool> EnsureChildAccessAsync(McpServer server, string parentTenantId,
        string childTenantId, List<StarterStepDto> steps, CancellationToken cancellationToken)
    {
        var access = await McpSessionContext.ResolveAccessTokenAsync(server, childTenantId);
        if (access.AccessToken != null)
        {
            steps.Add(Skipped(ProvisionStep, $"the caller already has a token for '{childTenantId}'"));
            return true;
        }

        string? lastError = null;
        for (var attempt = 1; attempt <= AccessAttempts; attempt++)
        {
            var provisioned = await AdminProvisioningTools.ProvisionCurrentUser(server, childTenantId, parentTenantId);
            if (provisioned.IsSuccess)
            {
                access = await McpSessionContext.ResolveAccessTokenAsync(server, childTenantId);
                if (access.AccessToken != null)
                {
                    steps.Add(Done(ProvisionStep, provisioned.Message));
                    return true;
                }

                lastError = access.Error;
            }
            else
            {
                lastError = provisioned.ErrorMessage;
            }

            if (attempt < AccessAttempts && RetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        steps.Add(Failed(ProvisionStep,
            $"the caller could not be made administrator of '{childTenantId}' or got no token for it " +
            $"({AccessAttempts} attempts): {lastError}"));
        return false;
    }

    private static async Task<string?> InstallStarterAsync(McpServer server, IAssetServicesClient childAssetClient,
        string childTenantId, string? starterBlueprintId, List<StarterStepDto> steps)
    {
        try
        {
            var installed = (await childAssetClient.ListBlueprintInstallationsAsync(childTenantId))
                .FirstOrDefault(i => IsStarter(i.BlueprintId));
            if (installed != null)
            {
                steps.Add(Skipped(BlueprintStep, $"'{installed.BlueprintId}' is already installed"));
                return installed.BlueprintId;
            }

            var blueprintId = string.IsNullOrWhiteSpace(starterBlueprintId)
                ? await NewestStarterAsync(childAssetClient)
                : starterBlueprintId;
            if (blueprintId == null)
            {
                steps.Add(Failed(BlueprintStep,
                    $"no blueprint '{StarterBlueprintName}' in the blueprint catalogs of the tenant; refresh them with " +
                    "refresh_blueprint_catalogs or name one with starterBlueprintId."));
                return null;
            }

            var install = await BlueprintTools.InstallBlueprint(server, blueprintId, tenantId: childTenantId);
            if (!install.IsSuccess || install.Result?.Success != true)
            {
                steps.Add(Failed(BlueprintStep, install.ErrorMessage ?? install.Message));
                return null;
            }

            steps.Add(Done(BlueprintStep, install.Message));
            return blueprintId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(Failed(BlueprintStep, ex.Message));
            return null;
        }
    }

    /// <summary>The newest <c>System.Ai.Starter</c> of all catalogs, as <c>System.Ai.Starter-&lt;version&gt;</c>.</summary>
    private static async Task<string?> NewestStarterAsync(IAssetServicesClient assetClient)
    {
        const int pageSize = 100;
        Version? newest = null;
        string? newestId = null;
        for (var skip = 0;; skip += pageSize)
        {
            var page = await assetClient.ListBlueprintsAsync(skip, pageSize);
            foreach (var item in page.Items.Where(i => string.Equals(i.Name, StarterBlueprintName, StringComparison.Ordinal)))
            {
                if (Version.TryParse(item.Version, out var version) && (newest == null || version > newest))
                {
                    newest = version;
                    newestId = $"{StarterBlueprintName}-{item.Version}";
                }
            }

            if (page.Items.Count < pageSize || skip + page.Items.Count >= page.TotalCount)
            {
                return newestId;
            }
        }
    }

    private static bool IsStarter(string? blueprintId) =>
        blueprintId != null &&
        (string.Equals(blueprintId, StarterBlueprintName, StringComparison.Ordinal) ||
         blueprintId.StartsWith(StarterBlueprintName + "-", StringComparison.Ordinal));

    private static StarterStepDto Done(string step, string? detail) =>
        new() { Step = step, Outcome = "done", Detail = detail };

    private static StarterStepDto Skipped(string step, string detail) =>
        new() { Step = step, Outcome = "skipped", Detail = detail };

    private static StarterStepDto Failed(string step, string? detail) =>
        new() { Step = step, Outcome = "failed", Detail = detail ?? "no reason given" };
}
