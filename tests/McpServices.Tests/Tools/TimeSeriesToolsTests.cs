using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class TimeSeriesToolsTests : ToolTestBase
{
    private const string ArchiveRtId = "69fda707d47638c68edc7fea";
    private const string RollupRtId = "69fda707d47638c68edc7feb";

    public TimeSeriesToolsTests()
    {
        GivenAuthenticated();
    }

    // ── Stream Data lifecycle ───────────────────────────────────────────────

    [Fact]
    public async Task EnableStreamData_HappyPath_CallsSdk()
    {
        var result = await TimeSeriesTools.EnableStreamData(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.EnableAsync(DefaultTenantId), Times.Once);
    }

    [Fact]
    public async Task EnableStreamData_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await TimeSeriesTools.EnableStreamData(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockStreamDataClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisableStreamData_WithoutConfirm_Refuses()
    {
        var result = await TimeSeriesTools.DisableStreamData(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        MockStreamDataClient.Verify(c => c.DisableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisableStreamData_WithConfirm_CallsSdk()
    {
        var result = await TimeSeriesTools.DisableStreamData(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.DisableAsync(DefaultTenantId), Times.Once);
    }

    // ── Archive lifecycle ───────────────────────────────────────────────────

    [Fact]
    public async Task ActivateArchive_HappyPath_CallsSdk()
    {
        var result = await TimeSeriesTools.ActivateArchive(MockServer.Object, ArchiveRtId);

        result.IsSuccess.Should().BeTrue();
        result.RtId.Should().Be(ArchiveRtId);
        MockStreamDataClient.Verify(c => c.ActivateArchiveAsync(DefaultTenantId, ArchiveRtId), Times.Once);
    }

    [Fact]
    public async Task ActivateArchive_MissingId_ReturnsValidationError()
    {
        var result = await TimeSeriesTools.ActivateArchive(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        MockStreamDataClient.Verify(c => c.ActivateArchiveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisableArchive_HappyPath_CallsSdk()
    {
        var result = await TimeSeriesTools.DisableArchive(MockServer.Object, ArchiveRtId);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.DisableArchiveAsync(DefaultTenantId, ArchiveRtId), Times.Once);
    }

    [Fact]
    public async Task EnableArchive_HappyPath_CallsSdk()
    {
        var result = await TimeSeriesTools.EnableArchive(MockServer.Object, ArchiveRtId);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.EnableArchiveAsync(DefaultTenantId, ArchiveRtId), Times.Once);
    }

    [Fact]
    public async Task RetryArchiveActivation_HappyPath_CallsSdk()
    {
        var result = await TimeSeriesTools.RetryArchiveActivation(MockServer.Object, ArchiveRtId);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.RetryArchiveActivationAsync(DefaultTenantId, ArchiveRtId), Times.Once);
    }

    [Fact]
    public async Task DeleteArchive_WithoutConfirm_Refuses()
    {
        var result = await TimeSeriesTools.DeleteArchive(MockServer.Object, ArchiveRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockStreamDataClient.Verify(c => c.DeleteArchiveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteArchive_WithConfirm_CallsSdk()
    {
        var result = await TimeSeriesTools.DeleteArchive(MockServer.Object, ArchiveRtId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.DeleteArchiveAsync(DefaultTenantId, ArchiveRtId), Times.Once);
    }

    // ── Rollups ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRollupsForArchive_HappyPath_ReturnsList()
    {
        MockStreamDataClient.Setup(c => c.ListRollupsForArchiveAsync(DefaultTenantId, ArchiveRtId))
            .ReturnsAsync(new[]
            {
                new RollupArchiveInfoDto(RollupRtId, "daily", "Activated",
                    ArchiveRtId, 86400000, 60000, null, null, 3,
                    false, null, null, null, null, 0, 0)
            });

        var result = await TimeSeriesTools.ListRollupsForArchive(MockServer.Object, ArchiveRtId);

        result.IsSuccess.Should().BeTrue();
        result.Rollups.Should().HaveCount(1);
        result.SourceArchiveRtId.Should().Be(ArchiveRtId);
    }

    [Fact]
    public async Task ListRollupsForArchive_MissingArg_ReturnsValidationError()
    {
        var result = await TimeSeriesTools.ListRollupsForArchive(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task FreezeRollupArchive_HappyPath_PassesTimestamp()
    {
        var until = new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);

        var result = await TimeSeriesTools.FreezeRollupArchive(MockServer.Object, RollupRtId, until);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.FreezeRollupArchiveAsync(DefaultTenantId, RollupRtId, until),
            Times.Once);
    }

    [Fact]
    public async Task UnfreezeRollupArchive_Default_DoesNotAcceptGaps()
    {
        var result = await TimeSeriesTools.UnfreezeRollupArchive(MockServer.Object, RollupRtId);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.UnfreezeRollupArchiveAsync(DefaultTenantId, RollupRtId, false),
            Times.Once);
    }

    [Fact]
    public async Task UnfreezeRollupArchive_AcceptGapsTrue_PassesFlag()
    {
        await TimeSeriesTools.UnfreezeRollupArchive(MockServer.Object, RollupRtId, acceptGaps: true);

        MockStreamDataClient.Verify(c => c.UnfreezeRollupArchiveAsync(DefaultTenantId, RollupRtId, true),
            Times.Once);
    }

    [Fact]
    public async Task RewindRollupWatermark_WithoutConfirm_Refuses()
    {
        var ts = DateTime.UtcNow;
        var result = await TimeSeriesTools.RewindRollupWatermark(MockServer.Object, RollupRtId, ts);

        result.IsSuccess.Should().BeFalse();
        MockStreamDataClient.Verify(c => c.RewindRollupWatermarkAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task RewindRollupWatermark_WithConfirm_CallsSdk()
    {
        var ts = DateTime.UtcNow;
        var result = await TimeSeriesTools.RewindRollupWatermark(MockServer.Object, RollupRtId, ts, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockStreamDataClient.Verify(c => c.RewindRollupWatermarkAsync(DefaultTenantId, RollupRtId, ts),
            Times.Once);
    }

    // ── backfill_rollup_archive (AB#4269) ───────────────────────────────────

    [Fact]
    public async Task BackfillRollupArchive_WithoutConfirm_Refuses()
    {
        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupRtId);

        result.IsSuccess.Should().BeFalse();
        MockStreamDataClient.Verify(c => c.BackfillRollupFromSourceAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackfillRollupArchive_MissingRollupRtId_ReturnsError()
    {
        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, string.Empty, confirm: true);

        result.IsSuccess.Should().BeFalse();
        MockStreamDataClient.Verify(c => c.BackfillRollupFromSourceAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackfillRollupArchive_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupRtId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockStreamDataClient.Verify(c => c.BackfillRollupFromSourceAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackfillRollupArchive_WithConfirm_CallsSdkAndReturnsJob()
    {
        var job = new RollupRecomputeJobInfoDto(
            "69fda707d47638c68edc7fec", "Completed", 42, 3, DateTime.UtcNow, DateTime.UtcNow, 12, null);
        MockStreamDataClient.Setup(c => c.BackfillRollupFromSourceAsync(DefaultTenantId, RollupRtId))
            .ReturnsAsync(job);

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupRtId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.Job.Should().BeSameAs(job);
        result.RtId.Should().Be(RollupRtId);
        MockStreamDataClient.Verify(c => c.BackfillRollupFromSourceAsync(DefaultTenantId, RollupRtId), Times.Once);
    }

    [Fact]
    public async Task BackfillRollupArchive_EmptySource_SucceedsWithNullJob()
    {
        MockStreamDataClient.Setup(c => c.BackfillRollupFromSourceAsync(DefaultTenantId, RollupRtId))
            .ReturnsAsync((RollupRecomputeJobInfoDto?)null);

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupRtId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.Job.Should().BeNull();
        result.Message.Should().Contain("no-op");
        MockStreamDataClient.Verify(c => c.BackfillRollupFromSourceAsync(DefaultTenantId, RollupRtId), Times.Once);
    }
}
