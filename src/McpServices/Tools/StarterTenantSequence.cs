using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     <c>create_tenant starter=true</c> (AB#9570): leaves a new child tenant app-ready in one call - create the tenant,
///     provision the calling user as its administrator, enable Communication, enable AI Services and install the
///     starter blueprint <c>System.Ai.Starter</c> (which brings System.Ai.Builder: the knowledge pages and, since
///     Starter 1.1 / Builder 1.2, the builder agent), then check the ready state (<see cref="StarterReadyCheck" />,
///     AB#9573).
///     <para>
///     Every step first checks whether it is already done and is then <c>skipped</c>, so calling the tool again for the
///     same tenant after a failure completes the remaining steps without repeating finished ones. The first failing step
///     stops the sequence; the steps after it are <c>notRun</c>.
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
    internal const string BlueprintStep = "install_blueprint";
    internal const string ReadyStep = "verify_ready";

    private static readonly string[] Steps =
        [CreateStep, ProvisionStep, CommunicationStep, AiStep, BlueprintStep, ReadyStep];

    /// <summary>
    ///     Reads the ready state of the child with the caller's rights: null when ready, else what is missing. Tests
    ///     replace it; the default is <see cref="StarterReadyCheck.MissingAsync(McpServer, string)" />.
    /// </summary>
    internal static Func<McpServer, string, Task<string?>> ReadyProbe { get; set; } = StarterReadyCheck.MissingAsync;

    /// <summary>
    ///     Identity sets a new tenant up asynchronously after the create: the admin provisioning and the token for the
    ///     child are retried this many times, <see cref="RetryDelay" /> apart.
    /// </summary>
    internal const int AccessAttempts = 15;

    /// <summary>Delay between two attempts to reach the new child; tests set it to zero.</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     The reason the starter cannot run for this caller, checked before anything is created; null when it can.
    /// </summary>
    internal static string? RefusalFor(McpServer server, bool scratch)
    {
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
        string childTenantId, string database, string? starterBlueprintId, CreateTenantResponse response)
    {
        var steps = new List<StarterStepDto>();

        var outcome = await RunStepsAsync(server, parentAssetClient, parentTenantId, childTenantId, database,
            starterBlueprintId, steps);

        // Name every step, also those that never ran, so the caller sees the whole sequence.
        foreach (var step in Steps.Where(s => steps.All(d => d.Step != s)))
        {
            steps.Add(new StarterStepDto { Step = step, Outcome = "notRun", Detail = "an earlier step failed" });
        }

        response.StarterSteps = steps;
        response.ParentTenantId = parentTenantId;
        response.CreatedTenantId = childTenantId;
        response.Database = database;

        var failed = steps.FirstOrDefault(s => s.Outcome == "failed");
        if (failed == null)
        {
            response.IsSuccess = true;
            response.Message = $"Tenant '{childTenantId}' is app-ready: Communication and AI Services enabled, " +
                               $"{outcome ?? StarterBlueprintName} installed, builder agent and knowledge base ready. Steps: " +
                               string.Join(", ", steps.Select(s => $"{s.Step}={s.Outcome}")) + ".";
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
        List<StarterStepDto> steps)
    {
        // 1. Create the tenant, unless an earlier call did.
        try
        {
            var children = await parentAssetClient.GetTenantsAsync();
            if (children?.Any(t => string.Equals(t.TenantId, childTenantId, StringComparison.OrdinalIgnoreCase)) == true)
            {
                steps.Add(Skipped(CreateStep, $"tenant '{childTenantId}' already exists under '{parentTenantId}'"));
            }
            else
            {
                await parentAssetClient.CreateTenantAsync(childTenantId, database);
                steps.Add(Done(CreateStep, $"tenant '{childTenantId}' (database '{database}') created under '{parentTenantId}'"));
            }
        }
        catch (Exception ex)
        {
            steps.Add(Failed(CreateStep, ex.Message));
            return null;
        }

        // 2. The caller must reach the child: provision it as administrator unless its token exchange already works.
        if (!await EnsureChildAccessAsync(server, parentTenantId, childTenantId, steps))
        {
            return null;
        }

        // 3. + 4. Communication and AI Services, each only when not yet enabled.
        var features = await Services.AssetClientContext.TryBuildAsync(server, childTenantId);
        if (features.Error != null)
        {
            steps.Add(Failed(CommunicationStep, features.Error));
            return null;
        }

        try
        {
            var status = await features.Client!.GetTenantFeaturesStatusAsync();
            if (status.Communication?.TenantEnabled == true)
            {
                steps.Add(Skipped(CommunicationStep, "Communication is already enabled"));
            }
            else
            {
                var communication = await CommunicationLifecycleTools.EnableCommunication(server, childTenantId);
                if (!communication.IsSuccess)
                {
                    steps.Add(Failed(CommunicationStep, communication.ErrorMessage));
                    return null;
                }

                steps.Add(Done(CommunicationStep, communication.Message));
            }

            if (status.AiServices?.TenantEnabled == true)
            {
                steps.Add(Skipped(AiStep, "AI Services are already enabled"));
            }
            else
            {
                var ai = await AiLifecycleTools.EnableAi(server, childTenantId);
                if (!ai.IsSuccess)
                {
                    steps.Add(Failed(AiStep, ai.ErrorMessage));
                    return null;
                }

                steps.Add(Done(AiStep, ai.Message));
            }
        }
        catch (Exception ex)
        {
            steps.Add(Failed(steps.Any(s => s.Step == CommunicationStep) ? AiStep : CommunicationStep, ex.Message));
            return null;
        }

        // 5. The starter blueprint, unless it is installed already.
        var installed = await InstallStarterAsync(server, features.Client!, childTenantId, starterBlueprintId, steps);
        if (installed == null)
        {
            return null;
        }

        // 6. Ready state: builder agent, mandate, client and the app-index page, read with the caller's rights. Always
        //    checked (also on a repeated call), never skipped: it changes nothing.
        try
        {
            var missing = await ReadyProbe(server, childTenantId);
            steps.Add(missing == null
                ? Done(ReadyStep, "builder agent enabled (mandate write mode Propose, client agent-builder) and " +
                                  "knowledge page 'app-index' present")
                : Failed(ReadyStep, $"'{installed}' is installed but the tenant is not ready: {missing}. Install a " +
                                    "System.Ai.Starter 1.1 or later (it brings System.Ai.Builder 1.2 with the builder agent)."));
        }
        catch (Exception ex)
        {
            steps.Add(Failed(ReadyStep, ex.Message));
        }

        return installed;
    }

    private static async Task<bool> EnsureChildAccessAsync(McpServer server, string parentTenantId,
        string childTenantId, List<StarterStepDto> steps)
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
                await Task.Delay(RetryDelay);
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
        catch (Exception ex)
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
