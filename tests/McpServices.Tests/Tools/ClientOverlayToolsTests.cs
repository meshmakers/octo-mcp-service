using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ClientOverlayToolsTests : ToolTestBase
{
    public ClientOverlayToolsTests()
    {
        GivenAuthenticated();
    }

    private static ApplyOverlayUrisResultDto MakeApplyResult(string clientId = "c1", string overlayName = "local-dev") =>
        new()
        {
            ClientId = clientId,
            OverlayName = overlayName,
            RedirectUris = new ApplyOverlayUrisListCountDto { Added = 1, SkippedDuplicate = 0 },
            PostLogoutRedirectUris = new ApplyOverlayUrisListCountDto { Added = 0, SkippedDuplicate = 0 },
            AllowedCorsOrigins = new ApplyOverlayUrisListCountDto { Added = 0, SkippedDuplicate = 0 }
        };

    // ---- apply_client_overlay ----

    [Fact]
    public async Task ApplyClientOverlay_HappyPath_CallsSdk()
    {
        MockIdentityClient
            .Setup(c => c.ApplyClientOverlay("octo-data-refinery-studio", It.IsAny<ApplyOverlayUrisDto>()))
            .ReturnsAsync(MakeApplyResult("octo-data-refinery-studio"));

        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object,
            clientId: "octo-data-refinery-studio",
            overlayName: "local-dev",
            redirectUris: ["http://localhost:4200/auth-callback"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.ClientId.Should().Be("octo-data-refinery-studio");
        result.OverlayName.Should().Be("local-dev");
        MockIdentityClient.Verify(c => c.ApplyClientOverlay("octo-data-refinery-studio",
            It.Is<ApplyOverlayUrisDto>(d =>
                d.OverlayName == "local-dev" &&
                d.RedirectUris != null && d.RedirectUris.Count == 1 &&
                d.PostLogoutRedirectUris == null &&
                d.AllowedCorsOrigins == null)), Times.Once);
    }

    [Fact]
    public async Task ApplyClientOverlay_TrimsAndDropsBlankUris()
    {
        MockIdentityClient
            .Setup(c => c.ApplyClientOverlay(It.IsAny<string>(), It.IsAny<ApplyOverlayUrisDto>()))
            .ReturnsAsync(MakeApplyResult());

        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object,
            clientId: "c1",
            overlayName: "local-dev",
            redirectUris: ["  http://localhost:4200/auth-callback  ", "   ", ""]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        MockIdentityClient.Verify(c => c.ApplyClientOverlay("c1",
            It.Is<ApplyOverlayUrisDto>(d =>
                d.RedirectUris != null &&
                d.RedirectUris.Count == 1 &&
                d.RedirectUris[0] == "http://localhost:4200/auth-callback")), Times.Once);
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
        MockIdentityClient
            .Setup(c => c.CleanOverlayEntries(null))
            .ReturnsAsync(new CleanOverlayEntriesResultDto
            {
                OverlayName = null,
                ClientsAffected = 1,
                TotalEntriesRemoved = 3,
                ClientResults =
                [
                    new CleanOverlayEntriesClientResultDto
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
        MockIdentityClient.Verify(c => c.CleanOverlayEntries(null), Times.Once);
    }

    [Fact]
    public async Task CleanClientOverlays_WithOverlayName_PassesFilter()
    {
        MockIdentityClient
            .Setup(c => c.CleanOverlayEntries("local-dev"))
            .ReturnsAsync(new CleanOverlayEntriesResultDto
            {
                OverlayName = "local-dev",
                ClientsAffected = 0,
                TotalEntriesRemoved = 0,
                ClientResults = []
            });

        var result = await ClientOverlayTools.CleanClientOverlays(MockServer.Object,
            confirm: true, overlayName: "local-dev");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        MockIdentityClient.Verify(c => c.CleanOverlayEntries("local-dev"), Times.Once);
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
