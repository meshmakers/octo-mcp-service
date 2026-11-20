using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ClientOverlayToolsTests : ToolTestBase
{
    private const string ClientRtId = "65f1c0ffee0000000000c2c2";
    private const string Apply = "SystemIdentityClientApplyOverlayUris";
    private const string Clean = "SystemIdentityClientCleanOverlayEntries";

    public ClientOverlayToolsTests()
    {
        GivenAuthenticated();
        MockIdentityClient.Setup(c => c.GetClient(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClientDto { ClientId = id, RtId = ClientRtId });
    }

    private static RtClientOverlayUrisResultRecordDto MakeApplyRecord(string clientId = "c1",
        string overlayName = "local-dev") => new()
    {
        ClientId = clientId, OverlayName = overlayName, RedirectUrisAdded = 1, RedirectUrisSkipped = 0
    };

    // ---- apply_client_overlay ----

    [Fact]
    public async Task ApplyClientOverlay_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds(MakeApplyRecord("octo-data-refinery-studio"));

        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object,
            clientId: "octo-data-refinery-studio",
            overlayName: "local-dev",
            redirectUris: ["http://localhost:4200/auth-callback"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.ClientId.Should().Be("octo-data-refinery-studio");
        result.OverlayName.Should().Be("local-dev");
        result.Result!.RedirectUris.Added.Should().Be(1);
        CkMethods.RtIdOf(Apply).Should().Be(ClientRtId);
        var input = CkMethods.InputOf(Apply);
        CkMethodFake.Str(input, "overlayName").Should().Be("local-dev");
        CkMethodFake.Strings(input.GetProperty("redirectUris")).Should().HaveCount(1);
        CkMethodFake.Prop(input, "postLogoutRedirectUris").Should().BeNull();
        CkMethodFake.Prop(input, "allowedCorsOrigins").Should().BeNull();
        MockIdentityClient.Verify(c => c.ApplyClientOverlay(It.IsAny<string>(), It.IsAny<ApplyOverlayUrisDto>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyClientOverlay_TrimsAndDropsBlankUris()
    {
        CkMethods.Succeeds(MakeApplyRecord());

        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object,
            clientId: "c1",
            overlayName: "local-dev",
            redirectUris: ["  http://localhost:4200/auth-callback  ", "   ", ""]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        CkMethodFake.Strings(CkMethods.InputOf(Apply).GetProperty("redirectUris"))
            .Should().Equal("http://localhost:4200/auth-callback");
    }

    [Fact]
    public async Task ApplyClientOverlay_AllListsEmpty_ReturnsValidationError()
    {
        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object,
            clientId: "c1",
            overlayName: "local-dev",
            redirectUris: ["   "],
            postLogoutRedirectUris: [],
            allowedCorsOrigins: null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("At least one");
        MockIdentityClient.Verify(
            c => c.ApplyClientOverlay(It.IsAny<string>(), It.IsAny<ApplyOverlayUrisDto>()), Times.Never);
    }

    [Fact]
    public async Task ApplyClientOverlay_MissingClientId_ReturnsValidationError()
    {
        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object,
            clientId: "",
            overlayName: "local-dev",
            redirectUris: ["http://localhost:4200/auth-callback"]);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(
            c => c.ApplyClientOverlay(It.IsAny<string>(), It.IsAny<ApplyOverlayUrisDto>()), Times.Never);
    }

    [Fact]
    public async Task ApplyClientOverlay_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object,
            clientId: "c1",
            overlayName: "local-dev",
            redirectUris: ["http://localhost:4200/auth-callback"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockIdentityClient.Verify(
            c => c.ApplyClientOverlay(It.IsAny<string>(), It.IsAny<ApplyOverlayUrisDto>()), Times.Never);
    }

    // ---- clean_client_overlays ----

    [Fact]
    public async Task CleanClientOverlays_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds(new RtClientOverlayCleanResultRecordDto
        {
            OverlayName = null,
            ClientsAffected = 1,
            TotalEntriesRemoved = 3,
            ClientResults =
            [
                new RtClientOverlayCleanEntryRecordDto
                {
                    ClientId = "c1",
                    RedirectUrisRemoved = 2,
                    PostLogoutRedirectUrisRemoved = 1,
                    AllowedCorsOriginsRemoved = 0
                }
            ]
        });

        var result = await ClientOverlayTools.CleanClientOverlays(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Result!.TotalEntriesRemoved.Should().Be(3);
        result.Result.ClientResults.Single().RedirectUrisRemoved.Should().Be(2);
        CkMethodFake.Str(CkMethods.InputOf(Clean), "overlayName").Should().BeNull();
        MockIdentityClient.Verify(c => c.CleanOverlayEntries(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task CleanClientOverlays_WithOverlayName_PassesFilter()
    {
        CkMethods.Succeeds(new RtClientOverlayCleanResultRecordDto
        {
            OverlayName = "local-dev", ClientsAffected = 0, TotalEntriesRemoved = 0, ClientResults = []
        });

        var result = await ClientOverlayTools.CleanClientOverlays(MockServer.Object,
            confirm: true, overlayName: "local-dev");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        CkMethodFake.Str(CkMethods.InputOf(Clean), "overlayName").Should().Be("local-dev");
    }

    [Fact]
    public async Task CleanClientOverlays_WithoutConfirm_Refuses()
    {
        var result = await ClientOverlayTools.CleanClientOverlays(MockServer.Object, confirm: false);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockIdentityClient.Verify(c => c.CleanOverlayEntries(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task CleanClientOverlays_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await ClientOverlayTools.CleanClientOverlays(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockIdentityClient.Verify(c => c.CleanOverlayEntries(It.IsAny<string?>()), Times.Never);
    }
}
