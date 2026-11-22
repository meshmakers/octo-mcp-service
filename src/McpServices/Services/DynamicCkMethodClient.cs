using System.Text.Json;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Sends the document of a <see cref="CkMethodInvocationPlan" /> through the SDK's method client base, so the answer
///     is read exactly like the generated method clients read it: a success, one of the declared errors as a failed
///     result, every other method error as <see cref="CkMethodCallException" />.
/// </summary>
internal sealed class DynamicCkMethodClient : CkMethodClientBase
{
    public DynamicCkMethodClient(ITenantClient tenantClient) : base(tenantClient)
    {
    }

    /// <summary>
    ///     Calls a synchronous method; the result is returned as raw JSON.
    /// </summary>
    public Task<CkMethodResult<JsonElement?>> InvokeAsync(CkMethodInvocationPlan plan, object variables) =>
        InvokeCkMethodAsync<DynamicResponse, JsonElement?>(plan.QualifiedMethodId, plan.Document, plan.OperationName,
            variables, plan.DeclaredErrorCodes, r => r.Result);

    /// <summary>
    ///     Calls an asynchronous method; the operation handle is returned.
    /// </summary>
    public Task<CkMethodResult<CkOperationHandle>> StartAsync(CkMethodInvocationPlan plan, object variables) =>
        StartCkOperationAsync<DynamicAsyncResponse>(plan.QualifiedMethodId, plan.Document, plan.OperationName,
            variables, plan.DeclaredErrorCodes);

    /// <summary>The answer of a synchronous method field: <c>{ invocationId result }</c>.</summary>
    internal sealed class DynamicResponse : CkMethodCallResponse
    {
        public JsonElement? Result { get; set; }
    }

    /// <summary>The answer of an asynchronous method field: <c>{ invocationId operation { … } }</c>.</summary>
    internal sealed class DynamicAsyncResponse : CkMethodAsyncCallResponse
    {
    }
}
