using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     The generic CK method tool: calls one method of a CK type through the tenant's GraphQL endpoint with the caller's
///     own token, so the method gateway of the asset repository (authorization, target, parameter validation) decides
///     exactly as for a GraphQL caller. A refused call returns the same <c>code</c>, <c>parameters</c> and
///     <c>details</c> a GraphQL caller reads from <c>extensions</c>.
/// </summary>
[McpServerToolType]
public static class CkMethodTools
{
    /// <summary>
    ///     Calls a CK method (<c>invoke_ck_method</c>).
    ///     <para>
    ///         Risk Medium (write), like the generic CRUD write tools: a method can change data, and the asset repository's
    ///         gateway authorizes the call with the caller's token. Methods of System models (they have dedicated tools
    ///         with their own risk class and confirm gate), knowledge page types and methods with a sensitive result or
    ///         parameter are refused.
    ///     </para>
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">The concrete CK type</param>
    /// <param name="methodId">The method id</param>
    /// <param name="rtId">The target entity of an instance method</param>
    /// <param name="parameters">The method parameters as one JSON object</param>
    /// <param name="idempotencyKey">Optional idempotency key</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>The result, or the structured refusal</returns>
    [McpServerTool(Name = "invoke_ck_method")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Call a method of a Construction Kit type (an app method such as a booking) with the caller's own identity and " +
        "rights - the same call as the GraphQL method mutation runtime.<types>.<method>. The asset repository checks " +
        "method roles, allowSelf, scopes, the target entity and the parameters exactly as for a GraphQL caller. " +
        "On success: invocationId and result (JSON) or, for an asynchronous method, the operation handle. When the " +
        "server refuses the call, methodError carries the same code, parameters and details a GraphQL caller reads " +
        "from errors[].extensions, e.g. a declared code such as CONCURRENT_CHANGE (declared=true: the method lists it " +
        "in its errors) or a platform code such as METHOD_FORBIDDEN, METHOD_TARGET_NOT_FOUND or " +
        "METHOD_VALIDATION_FAILED (declared=false). Methods of System models (System, System.Identity, " +
        "System.Communication, ...) are refused: use the dedicated tools. Methods with a sensitive result or a sensitive " +
        "parameter and methods of knowledge page types (use knowledge_upsert_page) are refused. An asynchronous method " +
        "returns its operation handle; its result is read through GraphQL (no MCP tool reads operations yet). " +
        "Use search_types or get_available_types to find the type; a call with a method id the type does not have " +
        "is refused with the list of the type's methods.")]
    public static async Task<InvokeCkMethodResponse> InvokeCkMethod(
        McpServer server,
        [Description("Concrete CK type the method is called on, e.g. 'Sample.Booking-1.0.0/Resource-1' or the runtime id 'Sample.Booking/Resource-1'.")]
        string ckTypeId,
        [Description("Method id as the type declares it, e.g. 'Book' or 'Book-1', or the qualified id 'Model/Type.Method'.")]
        string methodId,
        [Description("Runtime id (24 hexadecimal characters) of the target entity. Required for an instance method, not allowed for a static method.")]
        string? rtId = null,
        [Description("The method parameters as one JSON object, one property per parameter name, e.g. {\"slotStartUtc\":\"2026-12-11T09:00:00Z\"}. At most 256 KiB. Omit for a method without parameters.")]
        string? parameters = null,
        [Description("Optional idempotency key (1 to 128 characters): a repeated call by the same caller with the same key and parameters returns the first result instead of running the method again.")]
        string? idempotencyKey = null,
        [Description("Optional tenant ID. If not specified, the tenant is resolved from the URL route.")]
        string? tenantId = null)
    {
        var argumentError = CkMethodInvocationPlanner.ValidateArguments(ckTypeId, methodId, rtId, parameters,
            idempotencyKey);
        if (argumentError != null)
        {
            return Refused(argumentError);
        }

        var (input, parametersError) = CkMethodInvocationPlanner.ParseParameters(parameters);
        if (parametersError != null)
        {
            return Refused(parametersError);
        }

        string? resolvedTenantId = null;
        CkMethodInvocationPlan? plan = null;
        try
        {
            // The same tenant gate as the schema tools: the CK definitions of a tenant are read only for a caller that
            // may access it.
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var access = await RuntimeSecurityContextResolver.ResolveTenantAccessAsync(server, tenantResolution,
                tenantId);
            if (access.Error != null || access.ResolvedTenantId == null)
            {
                return Refused(access.Error ?? Constants.NotAuthenticatedError);
            }

            resolvedTenantId = access.ResolvedTenantId;
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
            var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(resolvedTenantId);
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var (resolvedPlan, planError) = CkMethodInvocationPlanner.Plan(ckCacheService, tenantRepository.TenantId,
                ckTypeId, methodId, rtId, input != null);
            if (resolvedPlan == null)
            {
                return Refused(planError!, resolvedTenantId);
            }

            plan = resolvedPlan;

            // The family-1 helper: the caller's own token for exactly this tenant (a failed exchange is an error, never
            // the home token) and a fresh tenant GraphQL client.
            var ctx = await AssetClientContext.TryBuildAsync(server, resolvedTenantId, withTenantClient: true);
            if (ctx.Error != null || ctx.TenantClient == null)
            {
                return Refused(ctx.Error ?? Constants.NotAuthenticatedError, resolvedTenantId, plan);
            }

            if (!string.Equals(ctx.TenantId, resolvedTenantId, StringComparison.OrdinalIgnoreCase))
            {
                // Fail closed: the client must be bound to the tenant the gate admitted.
                return Refused($"Tenant mismatch: the client was built for '{ctx.TenantId}', not for '{resolvedTenantId}'.",
                    resolvedTenantId, plan);
            }

            var client = new DynamicCkMethodClient(ctx.TenantClient);
            var variables = Variables(plan, rtId, input, idempotencyKey);

            if (plan.IsAsync)
            {
                var started = await client.StartAsync(plan, variables);
                if (!started.IsSuccess)
                {
                    return MethodRefused(started.Error!, true, resolvedTenantId, plan);
                }

                return new InvokeCkMethodResponse
                {
                    IsSuccess = true,
                    TenantId = resolvedTenantId,
                    CkTypeId = plan.CkTypeId,
                    MethodId = plan.QualifiedMethodId,
                    InvocationId = started.InvocationId,
                    Operation = new CkMethodOperationInfo
                    {
                        OperationId = started.Value!.OperationId,
                        AcceptedAtUtc = started.Value.AcceptedAtUtc,
                        RetryAfterSeconds = started.Value.RetryAfterSeconds,
                        ExpiresAtUtc = started.Value.ExpiresAtUtc
                    },
                    Message = "The asynchronous method was accepted. No MCP tool reads CK operation results yet: read the result (or a declared error such as CONCURRENT_CHANGE) through the GraphQL runtime operations query with the operation id."
                };
            }

            var result = await client.InvokeAsync(plan, variables);
            if (!result.IsSuccess)
            {
                return MethodRefused(result.Error!, true, resolvedTenantId, plan);
            }

            return new InvokeCkMethodResponse
            {
                IsSuccess = true,
                TenantId = resolvedTenantId,
                CkTypeId = plan.CkTypeId,
                MethodId = plan.QualifiedMethodId,
                InvocationId = result.InvocationId,
                Result = result.Value is { ValueKind: not JsonValueKind.Undefined } value ? value : null
            };
        }
        catch (CkMethodCallException e)
        {
            // A method error the method does not declare (platform codes such as METHOD_FORBIDDEN): still structured,
            // never a success and never a second attempt.
            return MethodRefused(e.Error, false, resolvedTenantId, plan);
        }
        catch (Exception e)
        {
            return Refused(ErrorTextRedactor.Redact(e.Message), resolvedTenantId, plan);
        }
    }

    private static Dictionary<string, object?> Variables(CkMethodInvocationPlan plan, string? rtId, JsonElement? input,
        string? idempotencyKey)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (plan.IsInstance)
        {
            variables["rtId"] = rtId;
        }

        if (plan.HasInput && input != null)
        {
            variables["input"] = input.Value;
        }

        if (idempotencyKey != null)
        {
            variables["idempotencyKey"] = idempotencyKey;
        }

        return variables;
    }

    private static InvokeCkMethodResponse MethodRefused(CkMethodError error, bool declared, string? tenantId,
        CkMethodInvocationPlan? plan)
    {
        var message = ErrorTextRedactor.Redact(error.Message);
        return new InvokeCkMethodResponse
        {
            IsSuccess = false,
            ErrorMessage = string.IsNullOrWhiteSpace(message) ? error.Code : $"{error.Code}: {message}",
            TenantId = tenantId,
            CkTypeId = plan?.CkTypeId,
            MethodId = plan?.QualifiedMethodId,
            MethodError = new CkMethodErrorInfo
            {
                Code = error.Code,
                Declared = declared,
                Message = message,
                Parameters = new Dictionary<string, string>(error.Parameters, StringComparer.Ordinal),
                Details = error.Details.Select(d => new CkMethodErrorDetailInfo
                {
                    Code = d.Code,
                    Parameters = new Dictionary<string, string>(d.Parameters, StringComparer.Ordinal)
                }).ToList()
            }
        };
    }

    private static InvokeCkMethodResponse Refused(string error, string? tenantId = null,
        CkMethodInvocationPlan? plan = null) =>
        new()
        {
            IsSuccess = false,
            ErrorMessage = error,
            TenantId = tenantId,
            CkTypeId = plan?.CkTypeId,
            MethodId = plan?.QualifiedMethodId
        };
}
