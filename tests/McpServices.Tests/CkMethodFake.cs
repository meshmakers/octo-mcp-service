using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
using Moq;

namespace McpServices.Tests;

/// <summary>
///     Answers the CK method calls of the generated method clients on the mocked tenant client (identity wave 2) and records
///     the GraphQL requests they send. One answer for every method call of the test; the lookups by GraphQL query
///     (<see cref="GivenQueryReturns{TDto}" />) are answered separately.
/// </summary>
public sealed class CkMethodFake
{
    private readonly Mock<ITenantClient> _tenantClient;

    /// <summary>Creates the fake over the tenant client mock and answers every method call with success.</summary>
    public CkMethodFake(Mock<ITenantClient> tenantClient)
    {
        _tenantClient = tenantClient;
        Succeeds();
    }

    /// <summary>The method requests sent, in order.</summary>
    public List<GraphQLRequest> Requests { get; } = [];

    /// <summary>The operation names of the method requests (e.g. <c>SystemIdentityGroupCreateGroup</c>).</summary>
    public IEnumerable<string?> OperationNames => Requests.Select(r => r.OperationName);

    /// <summary>The variables of the single method request, as JSON (<c>rtId</c>, <c>input</c>, <c>idempotencyKey</c>).</summary>
    public JsonElement SingleVariables(string operationName)
    {
        var request = Requests.Should().ContainSingle(r => r.OperationName == operationName).Subject;
        return JsonSerializer.SerializeToElement(request.Variables);
    }

    /// <summary>The <c>rtId</c> variable (target) of the single request with <paramref name="operationName" />.</summary>
    public string? RtIdOf(string operationName) =>
        SingleVariables(operationName).TryGetProperty("rtId", out var rtId) ? rtId.GetString() : null;

    /// <summary>The <c>input</c> variable (method parameters) of the single request with <paramref name="operationName" />.</summary>
    public JsonElement InputOf(string operationName) => SingleVariables(operationName).GetProperty("input");

    /// <summary>The string items of a JSON array (e.g. <c>roleNames</c> of an input).</summary>
    public static IReadOnlyList<string?> Strings(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(e => e.GetString()).ToList() : [];

    /// <summary>A property of an input as JSON, or <c>null</c> when it is missing or JSON null.</summary>
    public static JsonElement? Prop(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    /// <summary>A string property of an input, or <c>null</c>.</summary>
    public static string? Str(JsonElement obj, string name) => Prop(obj, name)?.GetString();

    /// <summary>A boolean property of an input, or <c>null</c>.</summary>
    public static bool? Bool(JsonElement obj, string name) => Prop(obj, name)?.GetBoolean();

    /// <summary>Every method call succeeds; a method with a result answers <paramref name="result" />.</summary>
    public CkMethodFake Succeeds(object? result = null)
    {
        _tenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Returns(new InvocationFunc(invocation =>
            {
                Requests.Add((GraphQLRequest)invocation.Arguments[0]);
                var responseType = invocation.Method.GetGenericArguments()[0];
                var response = (CkMethodCallResponse)Activator.CreateInstance(responseType, nonPublic: true)!;
                response.InvocationId = "inv-1";
                if (result != null)
                {
                    responseType.GetProperty("Result")?.SetValue(response, result);
                }

                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType)
                    .Invoke(null, [response])!;
            }));
        return this;
    }

    /// <summary>Every method call fails with <paramref name="code" /> (declared errors come back as the result).</summary>
    public CkMethodFake Fails(string code, string message = "refused",
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var extensions = new Map { ["code"] = code };
        if (parameters is { Count: > 0 })
        {
            extensions["parameters"] = parameters.ToDictionary(p => p.Key, p => (object?)p.Value);
        }

        Throws(new QlQueryErrorException(message, [new GraphQLError { Message = message, Extensions = extensions }]));
        return this;
    }

    /// <summary>No live handler: the identity service is too old or the bus exposure is off.</summary>
    public CkMethodFake HandlerNotAvailable() => Fails("METHOD_HANDLER_NOT_AVAILABLE", "No live handler.");

    /// <summary>The asset repository rejects the document in GraphQL validation (a schema without the method).</summary>
    public CkMethodFake MissingInSchema()
    {
        Throws(new QlQueryErrorException("GraphQL error",
        [
            new GraphQLError
            {
                Message = "Cannot query field 'x' on type 'SystemIdentityMutations'.",
                Extensions = new Map { ["code"] = "FIELDS_ON_CORRECT_TYPE", ["number"] = "5.3.1" }
            }
        ]));
        return this;
    }

    /// <summary>Every method call throws <paramref name="exception" />.</summary>
    public CkMethodFake Throws(Exception exception)
    {
        _tenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Callback(new InvocationAction(invocation => Requests.Add((GraphQLRequest)invocation.Arguments[0])))
            .Throws(exception);
        return this;
    }

    /// <summary>A lookup query for <typeparamref name="TDto" /> answers <paramref name="items" />.</summary>
    public CkMethodFake GivenQueryReturns<TDto>(params TDto[] items) where TDto : class
    {
        _tenantClient
            .Setup(c => c.SendQueryAsync<TDto>(It.IsAny<GraphQLRequest>()))
            .ReturnsAsync(new QlItemsContainer<TDto> { Items = items });
        return this;
    }
}
