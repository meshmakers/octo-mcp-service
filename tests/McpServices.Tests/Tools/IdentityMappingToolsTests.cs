using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class EmailDomainGroupRuleToolsTests : ToolTestBase
{
    private const string RuleId = "507f1f77bcf86cd799439111";
    private const string GroupId = "507f1f77bcf86cd799439222";

    public EmailDomainGroupRuleToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetRules_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetEmailDomainGroupRules())
            .ReturnsAsync([new EmailDomainGroupRuleDto { EmailDomainPattern = "meshmakers.io" }]);

        var result = await EmailDomainGroupRuleTools.GetRules(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Rules.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetRule_HappyPath_PassesObjectId()
    {
        MockIdentityClient.Setup(c => c.GetEmailDomainGroupRule(
                It.Is<OctoObjectId>(o => o.ToString() == RuleId)))
            .ReturnsAsync(new EmailDomainGroupRuleDto { EmailDomainPattern = "meshmakers.io" });

        var result = await EmailDomainGroupRuleTools.GetRule(MockServer.Object, RuleId);

        result.IsSuccess.Should().BeTrue();
        result.Rule!.EmailDomainPattern.Should().Be("meshmakers.io");
    }

    [Fact]
    public async Task CreateRule_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        await EmailDomainGroupRuleTools.CreateRule(MockServer.Object,
            "meshmakers.io", GroupId, "internal");

        var input = CkMethods.InputOf("SystemIdentityEmailDomainGroupRuleCreateEmailDomainGroupRule");
        CkMethodFake.Str(input, "emailDomainPattern").Should().Be("meshmakers.io");
        CkMethodFake.Str(input, "targetGroupRtId").Should().Be(GroupId);
        CkMethodFake.Str(input, "description").Should().Be("internal");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateEmailDomainGroupRule));
    }

    [Fact]
    public async Task CreateRule_MissingArgs_ReturnsValidationError()
    {
        var result = await EmailDomainGroupRuleTools.CreateRule(MockServer.Object, "", "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateRule_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await EmailDomainGroupRuleTools.UpdateRule(MockServer.Object,
            RuleId, "example.com", GroupId);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityEmailDomainGroupRuleUpdateEmailDomainGroupRule").Should().Be(RuleId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityEmailDomainGroupRuleUpdateEmailDomainGroupRule"),
            "emailDomainPattern").Should().Be("example.com");
    }

    [Fact]
    public async Task DeleteRule_WithoutConfirm_Refuses()
    {
        var result = await EmailDomainGroupRuleTools.DeleteRule(MockServer.Object, RuleId);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRule_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await EmailDomainGroupRuleTools.DeleteRule(MockServer.Object,
            RuleId, confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityEmailDomainGroupRuleDeleteEmailDomainGroupRule").Should().Be(RuleId);
    }
}

public class ExternalTenantUserMappingToolsTests : ToolTestBase
{
    private const string MappingId = "507f1f77bcf86cd799439333";

    public ExternalTenantUserMappingToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetMappings_HappyPath_PassesFilters()
    {
        MockIdentityClient.Setup(c => c.GetExternalTenantUserMappings(0, 50, "src-tenant"))
            .ReturnsAsync([new ExternalTenantUserMappingDto
            {
                SourceTenantId = "src-tenant", SourceUserId = "u1", SourceUserName = "alice"
            }]);

        var result = await ExternalTenantUserMappingTools.GetMappings(MockServer.Object,
            sourceTenantId: "src-tenant", skip: 0, take: 50);

        result.IsSuccess.Should().BeTrue();
        result.Mappings.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateMapping_HappyPath_PassesAllFields()
    {
        CkMethods.Succeeds(new RtAdminProvisioningResultRecordDto { MappingRtId = MappingId });
        MockIdentityClient.Setup(c => c.GetExternalTenantUserMapping(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new ExternalTenantUserMappingDto { SourceTenantId = "src-tenant", SourceUserName = "alice" });

        await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object,
            sourceTenantId: "src-tenant",
            sourceUserId: "u1",
            sourceUserName: "alice",
            roleIds: ["r1", "r2"]);

        var input = CkMethods.InputOf("SystemIdentityExternalTenantUserMappingCreateMapping");
        CkMethodFake.Str(input, "sourceTenantId").Should().Be("src-tenant");
        CkMethodFake.Str(input, "sourceUserId").Should().Be("u1");
        CkMethodFake.Str(input, "sourceUserName").Should().Be("alice");
        CkMethodFake.Strings(input.GetProperty("roleIds")).Should().Equal("r1", "r2");
        CkMethodFake.Prop(input, "groupIds").Should().BeNull();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.GrantExternalTenantUser));
    }

    [Fact]
    public async Task CreateMapping_MissingArgs_ReturnsValidationError()
    {
        var result = await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object, "", "", "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateMapping_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        await ExternalTenantUserMappingTools.UpdateMapping(MockServer.Object,
            MappingId, ["r1"]);

        CkMethods.RtIdOf("SystemIdentityExternalTenantUserMappingUpdateMapping").Should().Be(MappingId);
        CkMethodFake.Strings(CkMethods.InputOf("SystemIdentityExternalTenantUserMappingUpdateMapping")
            .GetProperty("roleIds")).Should().Equal("r1");
    }

    [Fact]
    public async Task DeleteMapping_WithoutConfirm_Refuses()
    {
        var result = await ExternalTenantUserMappingTools.DeleteMapping(MockServer.Object, MappingId);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteMapping_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ExternalTenantUserMappingTools.DeleteMapping(MockServer.Object,
            MappingId, confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityExternalTenantUserMappingDeleteMapping").Should().Be(MappingId);
    }
}

public class AdminProvisioningToolsTests : ToolTestBase
{
    private const string MappingId = "507f1f77bcf86cd799439444";

    public AdminProvisioningToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetMappings_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetAdminProvisioningMappings("target-tenant"))
            .ReturnsAsync([new ExternalTenantUserMappingDto
            {
                SourceTenantId = "src", SourceUserId = "u1", SourceUserName = "admin1"
            }]);

        var result = await AdminProvisioningTools.GetMappings(MockServer.Object, "target-tenant");

        result.IsSuccess.Should().BeTrue();
        result.TargetTenantId.Should().Be("target-tenant");
        result.Mappings.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetMappings_MissingTargetTenant_ReturnsValidationError()
    {
        var result = await AdminProvisioningTools.GetMappings(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateMapping_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        await AdminProvisioningTools.CreateMapping(MockServer.Object,
            "target", "src", "u1", "alice");

        var input = CkMethods.InputOf("SystemIdentityExternalTenantUserMappingProvisionAdmin");
        CkMethodFake.Str(input, "targetTenantId").Should().Be("target");
        CkMethodFake.Str(input, "sourceTenantId").Should().Be("src");
        CkMethodFake.Str(input, "sourceUserId").Should().Be("u1");
        CkMethodFake.Str(input, "sourceUserName").Should().Be("alice");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateAdminProvisioningMapping));
    }

    [Fact]
    public async Task ProvisionCurrentUser_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await AdminProvisioningTools.ProvisionCurrentUser(MockServer.Object, "target");

        result.IsSuccess.Should().BeTrue();
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityExternalTenantUserMappingProvisionCurrentUser"),
            "targetTenantId").Should().Be("target");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.ProvisionCurrentUser));
    }

    /// <summary>AB#8017: the result names the source identity the mapping was written for (the home identity).</summary>
    [Fact]
    public async Task ProvisionCurrentUser_ReturnsTheSourceIdentityTheMappingWasWrittenFor()
    {
        CkMethods.Succeeds(new RtAdminProvisioningResultRecordDto
        {
            SourceTenantId = "octosystem",
            SourceUserId = "686a6215fd3e5972ff5693fd",
            SourceUserName = "alice@example.com",
            Outcome = "created"
        });

        var result = await AdminProvisioningTools.ProvisionCurrentUser(MockServer.Object, "grandchild");

        result.IsSuccess.Should().BeTrue();
        result.SourceTenantId.Should().Be("octosystem");
        result.SourceUserId.Should().Be("686a6215fd3e5972ff5693fd");
        result.SourceUserName.Should().Be("alice@example.com");
        result.Message.Should().Contain("'alice@example.com'").And.Contain("tenant 'octosystem'");
    }

    [Fact]
    public async Task ProvisionCurrentUser_WithoutMappingInTheAnswer_StillReportsSuccess()
    {
        // The method answered without a result record.
        CkMethods.Succeeds();

        var result = await AdminProvisioningTools.ProvisionCurrentUser(MockServer.Object, "target");

        result.IsSuccess.Should().BeTrue();
        result.SourceTenantId.Should().BeNull();
        result.Message.Should().Be("Current user provisioned as admin in tenant 'target'.");
    }

    [Fact]
    public async Task DeleteMapping_WithoutConfirm_Refuses()
    {
        var result = await AdminProvisioningTools.DeleteMapping(MockServer.Object, "target", MappingId);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteMapping_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await AdminProvisioningTools.DeleteMapping(MockServer.Object,
            "target", MappingId, confirm: true);
        result.IsSuccess.Should().BeTrue();
        var input = CkMethods.InputOf("SystemIdentityExternalTenantUserMappingRemoveAdminProvisioning");
        CkMethodFake.Str(input, "targetTenantId").Should().Be("target");
        CkMethodFake.Str(input, "mappingRtId").Should().Be(MappingId);
    }
}
