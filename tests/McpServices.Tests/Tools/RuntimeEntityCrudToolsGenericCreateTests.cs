using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     The generic create tools refuse a CK type whose static method replaces the generic create
///     (GENERIC_CREATE_REPLACED_BY_METHOD), like the GraphQL create of the asset repository, and write nothing.
/// </summary>
public class RuntimeEntityCrudToolsGenericCreateTests : TestBase
{
    private const string BaseTypeId = "TestModule-1/Base-1";
    private const string DerivedTypeId = "TestModule-1/Derived-1";
    private const string PlainTypeId = "TestModule-1/Plain-1";
    private const string CanarySecret = "canary-secret-value-7119";
    private const string CanaryName = "canary-name-7119";

    private static CkMethodDto Method(bool replaces) => new()
    {
        MethodId = "Create-1",
        Kind = CkMethodKindDto.Static,
        ReplacesGenericCreate = replaces
    };

    private static AttributeUpdateItem Item(string path, string json) =>
        new() { AttributePath = path, Value = JsonDocument.Parse(json).RootElement.Clone() };

    private static string ExpectedMethodId(CkMethodDto method) =>
        new CkMethodGraph(new CkId<CkTypeId>(BaseTypeId), method).QualifiedMethodId;

    private void GivenType(CkTypeGraph type) => CkGraphTestBuilder.SetupTryGetRtCkType(MockCkCacheService, type);

    private CkTypeGraph GivenReplacingType(string requestedTypeId)
    {
        var type = CkGraphTestBuilder.BuildTypeWithMethods(requestedTypeId, [(BaseTypeId, Method(true))],
            ("Name", AttributeValueTypesDto.String, null),
            ("Password", AttributeValueTypesDto.Secret, null));
        GivenType(type);
        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns(type);
        return type;
    }

    private void VerifyNothingWritten()
    {
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()), Times.Never);
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    private static void AssertRefusal(CreateEntityResponse result, string requestedTypeId)
    {
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("GENERIC_CREATE_REPLACED_BY_METHOD");
        result.ErrorParameters.Should().NotBeNull();
        result.ErrorParameters!["ckTypeId"].Should().Be(
            new RtCkId<CkTypeId>(new CkId<CkTypeId>(requestedTypeId).ToString()).SemanticVersionedFullName,
            "the refusal names the type by its resolved full id");
        result.ErrorParameters["methodId"].Should().Be(ExpectedMethodId(Method(true)));
        result.RtId.Should().BeNull();
        result.Entity.Should().BeNull();
    }

    [Theory]
    [InlineData(BaseTypeId)]
    [InlineData(DerivedTypeId)]
    public async Task CreateEntity_TypeWithReplacingMethod_IsRefusedWithCodeAndParameters(string typeId)
    {
        GivenReplacingType(typeId);

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, typeId,
            [Item("Name", $"\"{CanaryName}\"")]);

        AssertRefusal(result, typeId);
        VerifyNothingWritten();
    }

    [Theory]
    [InlineData(BaseTypeId)]
    [InlineData(DerivedTypeId)]
    public async Task CreateEntityWithSecrets_TypeWithReplacingMethod_IsRefusedAndWritesNoSecret(string typeId)
    {
        GivenReplacingType(typeId);

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, typeId,
            [Item("Name", $"\"{CanaryName}\"")], [Item("Password", $"\"{CanarySecret}\"")]);

        AssertRefusal(result, typeId);
        VerifyNothingWritten();
    }

    [Fact]
    public async Task Refusal_ContainsNoEntityDataAndNoSecretValue()
    {
        GivenReplacingType(DerivedTypeId);

        var plain = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, DerivedTypeId,
            [Item("Name", $"\"{CanaryName}\"")]);
        var withSecrets = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, DerivedTypeId,
            [Item("Name", $"\"{CanaryName}\"")], [Item("Password", $"\"{CanarySecret}\"")]);

        foreach (var json in new[] { JsonSerializer.Serialize(plain), JsonSerializer.Serialize(withSecrets) })
        {
            json.Should().NotContain(CanarySecret).And.NotContain(CanaryName);
        }

        plain.ErrorMessage.Should().Be(withSecrets.ErrorMessage);
    }

    [Fact]
    public async Task Refusal_TakesPrecedenceOverSecretValidation()
    {
        // A secret value in entityData would be refused by the secret rules; the replacement refusal wins.
        GivenReplacingType(BaseTypeId);

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, BaseTypeId,
            [Item("Password", $"\"{CanarySecret}\"")]);

        result.ErrorCode.Should().Be("GENERIC_CREATE_REPLACED_BY_METHOD");
        VerifyNothingWritten();
    }

    [Fact]
    public async Task CreateEntity_TypeWithoutFlag_IsCreatedAsBefore()
    {
        var type = CkGraphTestBuilder.BuildTypeWithMethods(PlainTypeId, [(PlainTypeId, Method(false))],
            ("Name", AttributeValueTypesDto.String, null));
        GivenType(type);
        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns(type);
        MockTenantRepository
            .Setup(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()))
            .ReturnsAsync(() => new RtEntity
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new RtCkId<CkTypeId>(PlainTypeId)
            });

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, PlainTypeId,
            [Item("Name", "\"n\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.ErrorCode.Should().BeNull();
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Once);
    }

    [Fact]
    public void ToolDescriptions_MentionCreationThroughTheMethod()
    {
        foreach (var name in new[] { nameof(RuntimeEntityCrudTools.CreateEntity), nameof(RuntimeEntityCrudTools.CreateEntityWithSecrets) })
        {
            var description = typeof(RuntimeEntityCrudTools).GetMethod(name)!
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
            description.Should().Contain("GENERIC_CREATE_REPLACED_BY_METHOD").And.Contain("method");
        }
    }
}
