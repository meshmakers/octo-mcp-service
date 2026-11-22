using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using GraphQL.Client.Serializer.SystemTextJson;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     <c>invoke_ck_method</c>: one GraphQL method mutation with the caller's own token; a refusal comes back with the
///     same code, parameters and details as the GraphQL error's <c>extensions</c>. One test per abuse case of the
///     security invariant: other tenant, method without rights, undeclared method, oversized and malformed parameters,
///     injection into the document, System methods (case variants, inherited, resolved through another spelling),
///     sensitive results, unauthenticated callers, undeclared codes, and an unresolvable definition (fail closed).
/// </summary>
public class CkMethodToolsTests : ToolTestBase
{
    private const string TypeId = "Sample.Booking-1.0.0/Resource-1";
    private const string RtTypeId = "Sample.Booking/Resource";
    private const string TargetId = "65f2c0ffee00000000000b01";

    private const string ExpectedBookDocument =
        "mutation SampleBookingResourceBook($rtId: OctoObjectId!, $input: SampleBookingResourceBookMethodInput!, " +
        "$idempotencyKey: String) { runtime { sampleBookingResources { book(rtId: $rtId, input: $input, " +
        "idempotencyKey: $idempotencyKey) { invocationId result } } } }";

    private readonly Dictionary<string, CkTypeGraph> _types = new(StringComparer.Ordinal);

    public CkMethodToolsTests()
    {
        GivenAuthenticated();
        _ = CkMethods; // every call succeeds unless a test says otherwise
        GivenTypes(ResourceType());

        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns((string _, RtCkId<CkTypeId> id) =>
                _types.TryGetValue(id.ToString(), out var type) ? type : throw new KeyNotFoundException(id.ToString()));
        MockCkCacheService
            .Setup(c => c.GetCkType(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>()))
            .Returns((string _, CkId<CkTypeId> id) =>
                _types.TryGetValue(id.ToString(), out var type) ? type : throw new KeyNotFoundException(id.ToString()));
        MockCkCacheService
            .Setup(c => c.GetCkTypes(It.IsAny<string>()))
            .Returns(() => _types.Values.Distinct().ToList());
    }

    // ── Acceptance: same code and parameters as GraphQL ──────────────────────────────────────────

    [Fact]
    public async Task DeclaredConcurrentChange_IsTheSameCodeAndParametersAsTheGraphQlExtensions()
    {
        // The GraphQL answer of the refused call as it travels over the wire (the asset repository writes the
        // handler's declared error into extensions.code / extensions.parameters).
        const string wire = """
            {"data":{"runtime":{"sampleBookingResources":{"book":null}}},
             "errors":[{"message":"The CK method call failed: CONCURRENT_CHANGE",
                        "path":["runtime","sampleBookingResources","book"],
                        "extensions":{"code":"CONCURRENT_CHANGE",
                                      "parameters":{"methodId":"Sample.Booking/Resource.Book","slotKey":"r1|2026-12-11T09:00:00Z"}}}]}
            """;
        var serializer = new SystemTextJsonSerializer();
        var graphQlResponse = JsonSerializer.Deserialize<GraphQLResponse<JsonElement>>(wire, serializer.Options)!;
        CkMethods.Throws(new QlQueryErrorException("GraphQL error", graphQlResponse.Errors!));

        var result = await Book();

        using var expected = JsonDocument.Parse(wire);
        var extensions = expected.RootElement.GetProperty("errors")[0].GetProperty("extensions");
        result.IsSuccess.Should().BeFalse();
        result.MethodError.Should().NotBeNull();
        result.MethodError!.Code.Should().Be(extensions.GetProperty("code").GetString());
        result.MethodError.Parameters.Should().BeEquivalentTo(
            extensions.GetProperty("parameters").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()));
        result.MethodError.Declared.Should().BeTrue("the method declares CONCURRENT_CHANGE");
        result.MethodError.Details.Should().BeEmpty();
        result.ErrorMessage.Should().StartWith("CONCURRENT_CHANGE");
        CkMethods.Requests.Should().ContainSingle();
    }

    // ── Happy path and document ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Success_SendsOneMutationWithTheVariables_AndReturnsTheResult()
    {
        CkMethods.Succeeds(JsonSerializer.SerializeToElement(new { booked = true }));

        var result = await Book(idempotencyKey: "k-1");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.InvocationId.Should().Be("inv-1");
        result.Result!.Value.GetProperty("booked").GetBoolean().Should().BeTrue();
        result.CkTypeId.Should().Be(RtTypeId);
        result.MethodId.Should().Be("Sample.Booking/Resource.Book");
        result.TenantId.Should().Be(DefaultTenantId);

        var request = CkMethods.Requests.Should().ContainSingle().Subject;
        request.Query.Should().Be(ExpectedBookDocument);
        request.OperationName.Should().Be("SampleBookingResourceBook");
        var variables = CkMethods.SingleVariables("SampleBookingResourceBook");
        variables.GetProperty("rtId").GetString().Should().Be(TargetId);
        variables.GetProperty("input").GetProperty("slotStartUtc").GetString().Should().Be("2026-12-11T09:00:00Z");
        variables.GetProperty("idempotencyKey").GetString().Should().Be("k-1");
    }

    [Fact]
    public async Task StaticMethodWithoutParameters_HasNeitherRtIdNorInput()
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, RtTypeId, "Rebuild");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        CkMethods.Requests.Should().ContainSingle().Which.Query.Should().Be(
            "mutation SampleBookingResourceRebuild($idempotencyKey: String) { runtime { sampleBookingResources { " +
            "rebuild(idempotencyKey: $idempotencyKey) { invocationId } } } }");
        CkMethods.SingleVariables("SampleBookingResourceRebuild").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task AsynchronousMethod_ReturnsTheOperationHandle()
    {
        GraphQLRequest? sent = null;
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Returns(new InvocationFunc(invocation =>
            {
                sent = (GraphQLRequest)invocation.Arguments[0];
                var responseType = invocation.Method.GetGenericArguments()[0];
                var response = (CkMethodAsyncCallResponse)Activator.CreateInstance(responseType, nonPublic: true)!;
                response.InvocationId = "inv-2";
                response.Operation = new CkOperationHandleResponse { OperationId = "op-1", RetryAfterSeconds = 5 };
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType)
                    .Invoke(null, [response])!;
            }));

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, "Export-1", TargetId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.InvocationId.Should().Be("inv-2");
        result.Operation!.OperationId.Should().Be("op-1");
        result.Operation.RetryAfterSeconds.Should().Be(5);
        sent!.Query.Should()
            .Contain("export(rtId: $rtId, idempotencyKey: $idempotencyKey) { invocationId operation { operationId acceptedAtUtc retryAfterSeconds expiresAtUtc } }");
    }

    [Fact]
    public async Task RecordResult_SelectsTheVisibleAttributes_LikeTheGeneratedClients()
    {
        var recordId = new CkId<CkRecordId>("Sample.Booking-1.0.0/Receipt-1");
        CkGraphTestBuilder.SetupTryGetCkRecord(MockCkCacheService, recordId,
            CkGraphTestBuilder.BuildRecordWithAccess(recordId,
                ("BookingId", AttributeValueTypesDto.String, null, null),
                ("Token", AttributeValueTypesDto.Secret, null, null),
                ("Internal", AttributeValueTypesDto.String, null, CkAttributeAccessDto.Hidden)));
        GivenTypes(Type("Sample.Booking-1.0.0/Desk-1", isAbstract: false,
            ("Sample.Booking-1.0.0/Desk-1", new CkMethodDto
            {
                MethodId = "Reserve", Kind = CkMethodKindDto.Static,
                Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.Record, ValueCkRecordId = recordId }
            })));

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Sample.Booking/Desk-1", "Reserve");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        CkMethods.Requests.Should().ContainSingle().Which.Query.Should()
            .Contain("{ invocationId result { bookingId token { isSet keyMissing setAt } } }");
    }

    [Fact]
    public async Task QualifiedMethodId_IsAccepted()
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, "Sample.Booking/Resource.Book",
            TargetId, "{\"slotStartUtc\":\"x\"}");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        CkMethods.Requests.Should().ContainSingle().Which.Query.Should().Be(ExpectedBookDocument);
    }

    [Fact]
    public void Tool_IsClassifiedMediumRisk()
    {
        typeof(CkMethodTools).GetMethod(nameof(CkMethodTools.InvokeCkMethod))!
            .GetCustomAttribute<McpRiskAttribute>()!.Level.Should().Be(McpRiskLevel.Medium);
    }

    // ── Abuse case 1: other tenant ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OtherTenant_IsRefused_WithoutCacheReadOrCall()
    {
        GivenForeignTenantCall();

        var result = await Book(tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockCkCacheService.Verify(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()), Times.Never);
        MockClientFactory.Verify(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AllowedTenant_UsesTheCallersOwnTokenForExactlyThatTenant()
    {
        var token = GivenAuthenticated();

        await Book();

        MockClientFactory.Verify(f => f.CreateTenantClient(DefaultTenantId, token), Times.Once);
        MockClientFactory.Verify(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // ── Abuse case 2: method without rights ──────────────────────────────────────────────────────

    [Fact]
    public async Task MethodWithoutRights_ReturnsMethodForbiddenUnchanged_AfterOneCall()
    {
        var token = GivenAuthenticated();
        CkMethods.Fails("METHOD_FORBIDDEN", "The caller may not call the method.",
            new Dictionary<string, string> { ["methodId"] = "Sample.Booking/Resource.Book" });

        var result = await Book();

        result.IsSuccess.Should().BeFalse();
        result.MethodError!.Code.Should().Be("METHOD_FORBIDDEN");
        result.MethodError.Declared.Should().BeFalse();
        result.MethodError.Parameters.Should().Equal(new Dictionary<string, string>
            { ["methodId"] = "Sample.Booking/Resource.Book" });
        CkMethods.Requests.Should().ContainSingle("no second attempt with another identity");
        MockClientFactory.Verify(f => f.CreateTenantClient(DefaultTenantId, token), Times.Once);
        MockClientFactory.Verify(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // ── Abuse case 3: undeclared method ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Cancel")]
    [InlineData("book")]
    [InlineData("Book-2")]
    public async Task MethodTheTypeDoesNotHave_IsRefused_WithoutCall(string methodId)
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, methodId, TargetId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("has no method");
        AssertNoCall();
    }

    [Fact]
    public async Task AbstractType_IsRefused_WithoutCall()
    {
        GivenTypes(Type("Sample.Booking-1.0.0/Bookable-1", isAbstract: true,
            ("Sample.Booking-1.0.0/Bookable-1", BookMethod())));

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Sample.Booking/Bookable-1", "Book",
            TargetId, "{\"slotStartUtc\":\"x\"}");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("abstract");
        AssertNoCall();
    }

    [Fact]
    public async Task UnknownType_IsRefused_WithoutCall()
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Sample.Booking/Nothing-1", "Book", TargetId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        AssertNoCall();
    }

    // ── Abuse case 4: oversized parameters ───────────────────────────────────────────────────────

    [Fact]
    public async Task OversizedParameters_AreRefused_BeforeAnyCacheReadOrCall()
    {
        var big = "{\"slotStartUtc\":\"" + new string('x', CkMethodInvocationPlanner.MaxParametersBytes) + "\"}";

        var result = await Book(parameters: big);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("maximum");
        MockCkCacheService.Verify(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()), Times.Never);
        AssertNoCall();
    }

    [Fact]
    public async Task ParametersOfMultiByteCharacters_AreMeasuredInBytes()
    {
        // 100k three-byte characters are 300 KiB of UTF-8, above the limit although below it in chars.
        var big = "{\"slotStartUtc\":\"" + new string('€', 100_000) + "\"}";
        Encoding.UTF8.GetByteCount(big).Should().BeGreaterThan(CkMethodInvocationPlanner.MaxParametersBytes);

        var result = await Book(parameters: big);

        result.ErrorMessage.Should().Contain("maximum");
        AssertNoCall();
    }

    // ── Abuse case 5: malformed parameters and arguments ─────────────────────────────────────────

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task ParametersThatAreNotOneJsonObject_AreRefused(string parameters)
    {
        var result = await Book(parameters: parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("parameters");
        AssertNoCall();
    }

    [Fact]
    public async Task TooDeeplyNestedParameters_AreRefused()
    {
        var deep = "{\"a\":" + string.Concat(Enumerable.Repeat("[", 40)) + string.Concat(Enumerable.Repeat("]", 40)) +
                   "}";

        var result = await Book(parameters: deep);

        result.ErrorMessage.Should().Contain("not valid JSON");
        AssertNoCall();
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("65f2c0ffee00000000000b0")]
    [InlineData("65f2c0ffee00000000000b01\"){x}")]
    public async Task InvalidRtId_IsRefused(string rtId)
    {
        var result = await Book(rtId: rtId);

        result.ErrorMessage.Should().Contain("not a runtime id");
        AssertNoCall();
    }

    [Fact]
    public async Task InstanceMethodWithoutRtId_IsRefused()
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, "Book", null,
            "{\"slotStartUtc\":\"x\"}");

        result.ErrorMessage.Should().Contain("rtId of the target entity is required");
        AssertNoCall();
    }

    [Fact]
    public async Task StaticMethodWithRtId_IsRefused()
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, "Rebuild", TargetId);

        result.ErrorMessage.Should().Contain("static method");
        AssertNoCall();
    }

    [Fact]
    public async Task ParametersForAMethodWithoutParameters_AreRefused()
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, "Rebuild", null, "{\"a\":1}");

        result.ErrorMessage.Should().Contain("takes no parameters");
        AssertNoCall();
    }

    [Theory]
    [InlineData("")]
    [InlineData("129")]
    public async Task IdempotencyKeyOutsideItsLength_IsRefused(string key)
    {
        var result = await Book(idempotencyKey: key == "129" ? new string('k', 129) : key);

        result.ErrorMessage.Should().Contain("idempotencyKey");
        AssertNoCall();
    }

    [Theory]
    [InlineData(null, "Book")]
    [InlineData(TypeId, null)]
    [InlineData(" ", "Book")]
    public async Task MissingTypeOrMethod_IsRefused(string? ckTypeId, string? methodId)
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, ckTypeId!, methodId!, TargetId);

        result.ErrorMessage.Should().Contain("is required");
        AssertNoCall();
    }

    [Fact]
    public async Task WellShapedButInvalidValues_GoToTheServer_WhoseValidationDecides()
    {
        CkMethods.Fails("METHOD_VALIDATION_FAILED", "slotStartUtc is not a date time.",
            new Dictionary<string, string> { ["field"] = "slotStartUtc" });

        var result = await Book(parameters: "{\"slotStartUtc\":12,\"unknown\":true}");

        result.MethodError!.Code.Should().Be("METHOD_VALIDATION_FAILED");
        result.MethodError.Parameters["field"].Should().Be("slotStartUtc");
        CkMethods.InputOf("SampleBookingResourceBook").GetProperty("unknown").GetBoolean().Should().BeTrue();
    }

    // ── Abuse case 6: injection into the built mutation ──────────────────────────────────────────

    [Theory]
    [InlineData("Sample.Booking/Resource-1 } } mutation { x")]
    [InlineData("Sample.Booking/Resource-1\"){ y }")]
    [InlineData("Sample.Booking/Resource-1\nmutation")]
    public async Task TypeIdWithGraphQlSyntax_NeverReachesTheDocument(string ckTypeId)
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, ckTypeId, "Book", TargetId,
            "{\"slotStartUtc\":\"x\"}");

        result.IsSuccess.Should().BeFalse();
        AssertNoCall();
    }

    [Theory]
    [InlineData("Book) { x } mutation { y(")]
    [InlineData("Book\"}")]
    [InlineData("Book\nquery")]
    public async Task MethodIdWithGraphQlSyntax_NeverReachesTheDocument(string methodId)
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, methodId, TargetId,
            "{\"slotStartUtc\":\"x\"}");

        result.IsSuccess.Should().BeFalse();
        AssertNoCall();
    }

    [Fact]
    public async Task ParameterNamesAndValuesWithGraphQlSyntax_StayInTheVariables()
    {
        const string hostile =
            "{\"slotStartUtc\":\"x\\\") } } mutation { evil }\",\"} } mutation { evil2 } #\":\"$rtId\"}";

        var result = await Book(parameters: hostile);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var request = CkMethods.Requests.Should().ContainSingle().Subject;
        request.Query.Should().Be(ExpectedBookDocument, "the document does not depend on parameter values");
        var input = CkMethods.InputOf("SampleBookingResourceBook");
        input.GetProperty("slotStartUtc").GetString().Should().Be("x\") } } mutation { evil }");
        input.GetProperty("} } mutation { evil2 } #").GetString().Should().Be("$rtId");
    }

    [Fact]
    public async Task CkDefinitionWithANameThatIsNoGraphQlName_IsRefused()
    {
        GivenTypes(Type("Sample.Booking-1.0.0/Odd-1", isAbstract: false,
            ("Sample.Booking-1.0.0/Odd-1", new CkMethodDto { MethodId = "Do{x}", Kind = CkMethodKindDto.Static })));

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Sample.Booking/Odd-1", "Do{x}");

        result.ErrorMessage.Should().Contain("no valid GraphQL name");
        AssertNoCall();
    }

    // ── Abuse case 7: System methods (from the resolved definition) ──────────────────────────────

    [Theory]
    [InlineData("System.Identity-3.8.0/User-1")]
    [InlineData("system.identity-3.8.0/User-1")]
    [InlineData("SYSTEM.Communication-4.19.0/Pipeline-1")]
    [InlineData("System-2.7.0/Thing-1")]
    public async Task MethodOfASystemModel_IsRefused_InAnyCase(string systemTypeId)
    {
        GivenTypes(Type(systemTypeId, isAbstract: false, (systemTypeId, StaticMethod("ResetPassword"))));

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, new CkId<CkTypeId>(systemTypeId)
            .ToRtCkId().ToString(), "ResetPassword");

        result.ErrorMessage.Should().Contain("System model");
        AssertNoCall();
    }

    [Fact]
    public async Task SystemMethodInheritedByACustomType_IsRefused()
    {
        GivenTypes(Type("Acme.People-1.0.0/Person-1", isAbstract: false,
            ("System.Identity-3.8.0/User-1", StaticMethod("ResetPassword"))));

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Acme.People/Person-1", "ResetPassword");

        result.ErrorMessage.Should().Contain("System model");
        AssertNoCall();
    }

    [Fact]
    public async Task TypeIdThatResolvesToASystemModelThroughAnotherSpelling_IsRefused()
    {
        // The input names no System model; the cache resolves it to one (an alias or another spelling). The check
        // follows the resolved definition, not the text.
        var systemType = Type("System.Identity-3.8.0/User-1", isAbstract: false,
            ("System.Identity-3.8.0/User-1", StaticMethod("ResetPassword")));
        _types[new RtCkId<CkTypeId>("Users/Account-1").ToString()] = systemType;

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Users/Account-1", "ResetPassword");

        result.ErrorMessage.Should().Contain("System model");
        AssertNoCall();
    }

    [Fact]
    public async Task DefinitionThatCannotBeResolved_IsRefused_FailClosed()
    {
        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Throws(new InvalidOperationException("cache broken"));
        MockCkCacheService
            .Setup(c => c.GetCkType(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>()))
            .Throws(new InvalidOperationException("cache broken"));
        MockCkCacheService
            .Setup(c => c.GetCkTypes(It.IsAny<string>()))
            .Throws(new InvalidOperationException("cache broken"));

        var result = await Book();

        result.IsSuccess.Should().BeFalse();
        AssertNoCall();
    }

    [Theory]
    [InlineData("System", true)]
    [InlineData("system.identity", true)]
    [InlineData("SYSTEM.Communication", true)]
    [InlineData(null, true)]
    [InlineData("Sample.Booking", false)]
    [InlineData("Acme.Systemtechnik", false)]
    public void IsSystemModel_FollowsTheModelName(string? model, bool expected)
    {
        CkMethodInvocationPlanner.IsSystemModel(model).Should().Be(expected);
    }

    // ── Abuse case 8: sensitive result ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MethodWithASensitiveResult_IsRefused_BeforeTheCall()
    {
        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, "IssueKey", TargetId);

        result.ErrorMessage.Should().Contain("sensitive result");
        AssertNoCall();
    }

    [Fact]
    public async Task MethodWithASensitiveParameter_IsRefused_BeforeTheCall()
    {
        GivenTypes(Type("Sample.Booking-1.0.0/Vault-1", isAbstract: false,
            ("Sample.Booking-1.0.0/Vault-1", new CkMethodDto
            {
                MethodId = "Store", Kind = CkMethodKindDto.Static,
                Parameters =
                [
                    new CkMethodParameterDto { Name = "label", ValueType = AttributeValueTypesDto.String },
                    new CkMethodParameterDto { Name = "pin", ValueType = AttributeValueTypesDto.String, Sensitive = true }
                ]
            })));

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Sample.Booking/Vault-1", "Store", null,
            "{\"label\":\"a\",\"pin\":\"1234\"}");

        result.ErrorMessage.Should().Contain("sensitive parameter").And.NotContain("1234");
        AssertNoCall();
    }

    [Fact]
    public async Task CustomMethodOfATypeDerivedFromAKnowledgePage_IsRefused()
    {
        // A custom (non-System) type that derives from the knowledge page type, with a custom method: knowledge pages
        // are written only by the knowledge tools, through the content guard.
        const string derivedId = "Sample.Docs-1.0.0/Article-1";
        var derived = Type(derivedId, isAbstract: false,
            [new CkGraphTypeInheritance(new CkId<CkTypeId>(derivedId), new CkId<CkTypeId>("System.Ai-1.0.0/KnowledgePage-1"), 1)],
            (derivedId, StaticMethod("Publish")));
        GivenTypes(derived);

        var result = await CkMethodTools.InvokeCkMethod(MockServer.Object, "Sample.Docs/Article-1", "Publish");

        result.ErrorMessage.Should().Contain("knowledge page type");
        AssertNoCall();
    }

    // ── Abuse case 9: not authenticated ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UnauthenticatedCaller_IsRefused_WithoutCall()
    {
        GivenUnauthenticatedCaller();
        GivenUnauthenticated();

        var result = await Book();

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        AssertNoCall();
    }

    [Fact]
    public async Task CallerWithoutSessionToken_IsRefused_WithoutCall()
    {
        GivenUnauthenticated();
        GivenNoRuntimeCallerToken();

        var result = await Book();

        result.IsSuccess.Should().BeFalse();
        AssertNoCall();
    }

    // ── Abuse case 10: undeclared codes stay refusals ────────────────────────────────────────────

    [Fact]
    public async Task CodeTheMethodDoesNotDeclare_IsStructured_AndNeverASuccess()
    {
        CkMethods.Fails("METHOD_TARGET_NOT_FOUND", "No entity.",
            new Dictionary<string, string> { ["rtId"] = TargetId });

        var result = await Book();

        result.IsSuccess.Should().BeFalse();
        result.InvocationId.Should().BeNull();
        result.MethodError!.Code.Should().Be("METHOD_TARGET_NOT_FOUND");
        result.MethodError.Declared.Should().BeFalse();
        result.MethodError.Parameters["rtId"].Should().Be(TargetId);
    }

    [Fact]
    public async Task DocumentTheSchemaRejects_ReturnsTheGraphQlCodeUnchanged_NeverASuccess()
    {
        // A tenant whose schema lacks the method (e.g. a stale CK cache): GraphQL validation answers with its own
        // extensions.code; the tool passes it on like every other code.
        CkMethods.MissingInSchema();

        var result = await Book();

        result.IsSuccess.Should().BeFalse();
        result.MethodError!.Code.Should().Be("FIELDS_ON_CORRECT_TYPE");
        result.MethodError.Declared.Should().BeFalse();
        CkMethods.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TransportFailure_IsARefusal_WithARedactedMessage()
    {
        CkMethods.Throws(new InvalidOperationException("boom Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc"));

        var result = await Book();

        result.IsSuccess.Should().BeFalse();
        result.MethodError.Should().BeNull();
        result.ErrorMessage.Should().NotContain("eyJhbGciOiJIUzI1NiJ9.e30.abc");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private Task<InvokeCkMethodResponse> Book(string? rtId = TargetId,
        string? parameters = "{\"slotStartUtc\":\"2026-12-11T09:00:00Z\"}", string? idempotencyKey = null,
        string? tenantId = null) =>
        CkMethodTools.InvokeCkMethod(MockServer.Object, TypeId, "Book", rtId, parameters, idempotencyKey, tenantId);

    private void AssertNoCall()
    {
        CkMethods.Requests.Should().BeEmpty();
        MockClientFactory.Verify(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private void GivenTypes(params CkTypeGraph[] types)
    {
        foreach (var type in types)
        {
            _types[type.CkTypeId.ToString()] = type;
            _types[type.CkTypeId.ToRtCkId().ToString()] = type;
        }
    }

    private static CkTypeGraph ResourceType() =>
        Type(TypeId, isAbstract: false,
            (TypeId, BookMethod()),
            (TypeId, StaticMethod("Rebuild")),
            (TypeId, new CkMethodDto
            {
                MethodId = "Export-1", Kind = CkMethodKindDto.Instance,
                Execution = new CkMethodExecutionDto { Mode = "async" }
            }),
            (TypeId, new CkMethodDto
            {
                MethodId = "IssueKey", Kind = CkMethodKindDto.Instance,
                Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.String, Sensitive = true }
            }));

    private static CkMethodDto BookMethod() => new()
    {
        MethodId = "Book",
        Kind = CkMethodKindDto.Instance,
        Parameters = [new CkMethodParameterDto { Name = "slotStartUtc", ValueType = AttributeValueTypesDto.DateTime }],
        Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.Boolean },
        Errors = [new CkMethodErrorDto { Code = "CONCURRENT_CHANGE" }]
    };

    private static CkMethodDto StaticMethod(string methodId) => new()
    {
        MethodId = methodId,
        Kind = CkMethodKindDto.Static
    };

    private static CkTypeGraph Type(string typeId, bool isAbstract,
        params (string DeclaringTypeId, CkMethodDto Method)[] methods) =>
        Type(typeId, isAbstract, [], methods);

    private static CkTypeGraph Type(string typeId, bool isAbstract, CkGraphTypeInheritance[] baseTypes,
        params (string DeclaringTypeId, CkMethodDto Method)[] methods) =>
        new(
            ckTypeId: new CkId<CkTypeId>(typeId),
            isAbstract: isAbstract,
            isFinal: false,
            isCollectionRoot: true,
            baseTypes: new ReadOnlyCollection<CkGraphTypeInheritance>(baseTypes),
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: new ReadOnlyCollection<CkGraphTypeInheritance>([]),
            definedAttributes: [],
            allAttributes: new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(),
            indexes: new ReadOnlyCollection<CkTypeIndexDto>([]),
            associations: new CkGraphDirectedAssociations(new List<CkTypeAssociationDto>()),
            description: string.Empty,
            enableChangeStreamPreAndPostImages: false,
            allMethods: methods.ToDictionary(m => m.Method.MethodId,
                m => new CkMethodGraph(new CkId<CkTypeId>(m.DeclaringTypeId), m.Method)));
}
