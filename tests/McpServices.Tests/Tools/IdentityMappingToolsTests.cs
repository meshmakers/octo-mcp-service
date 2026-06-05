using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
        await EmailDomainGroupRuleTools.CreateRule(MockServer.Object,
            "meshmakers.io", GroupId, "internal");

        MockIdentityClient.Verify(c => c.CreateEmailDomainGroupRule(
            It.Is<EmailDomainGroupRuleDto>(d =>
                d.EmailDomainPattern == "meshmakers.io" && d.TargetGroupRtId == GroupId &&
                d.Description == "internal")), Times.Once);
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
        var result = await EmailDomainGroupRuleTools.UpdateRule(MockServer.Object,
            RuleId, "example.com", GroupId);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateEmailDomainGroupRule(
            It.Is<OctoObjectId>(o => o.ToString() == RuleId),
            It.Is<EmailDomainGroupRuleDto>(d => d.EmailDomainPattern == "example.com")), Times.Once);
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
        var result = await EmailDomainGroupRuleTools.DeleteRule(MockServer.Object,
            RuleId, confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteEmailDomainGroupRule(
            It.Is<OctoObjectId>(o => o.ToString() == RuleId)), Times.Once);
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
        await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object,
            sourceTenantId: "src-tenant",
            sourceUserId: "u1",
            sourceUserName: "alice",
            roleIds: ["r1", "r2"]);

        MockIdentityClient.Verify(c => c.CreateExternalTenantUserMapping(
            It.Is<CreateExternalTenantUserMappingDto>(d =>
                d.SourceTenantId == "src-tenant" && d.SourceUserId == "u1" &&
                d.SourceUserName == "alice" && d.RoleIds!.Count == 2)), Times.Once);
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
        await ExternalTenantUserMappingTools.UpdateMapping(MockServer.Object,
            MappingId, ["r1"]);

        MockIdentityClient.Verify(c => c.UpdateExternalTenantUserMapping(
            It.Is<OctoObjectId>(o => o.ToString() == MappingId),
            It.Is<UpdateExternalTenantUserMappingDto>(d => d.RoleIds!.Count == 1)), Times.Once);
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
        var result = await ExternalTenantUserMappingTools.DeleteMapping(MockServer.Object,
            MappingId, confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteExternalTenantUserMapping(
            It.Is<OctoObjectId>(o => o.ToString() == MappingId)), Times.Once);
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
        await AdminProvisioningTools.CreateMapping(MockServer.Object,
            "target", "src", "u1", "alice");

        MockIdentityClient.Verify(c => c.CreateAdminProvisioningMapping("target",
            It.Is<CreateExternalTenantUserMappingDto>(d =>
                d.SourceTenantId == "src" && d.SourceUserId == "u1" && d.SourceUserName == "alice")),
            Times.Once);
    }

    [Fact]
    public async Task ProvisionCurrentUser_HappyPath_CallsSdk()
    {
        var result = await AdminProvisioningTools.ProvisionCurrentUser(MockServer.Object, "target");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.ProvisionCurrentUser("target"), Times.Once);
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
        var result = await AdminProvisioningTools.DeleteMapping(MockServer.Object,
            "target", MappingId, confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteAdminProvisioningMapping("target",
            It.Is<OctoObjectId>(o => o.ToString() == MappingId)), Times.Once);
    }
}
