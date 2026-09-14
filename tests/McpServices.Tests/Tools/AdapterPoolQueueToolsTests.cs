using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#4924 §10 — the MCP third of the adapter pool queue surface. Four things are pinned because
///     they are what the design decided rather than what the SDK happens to return:
///     <list type="bullet">
///         <item>every entry carries the tenant-local position <b>and</b> the tenants ahead in the
///             rotation, and the response exposes no single global rank (§9.2);</item>
///         <item>a leased entry keeps its member id and is counted apart from the waiting ones;</item>
///         <item>a 409 comes back as <c>Outcome = AlreadyLeased</c> with <c>WasCancelled = false</c>
///             on a successful call — not as a tool failure, because interrupting a running pipeline
///             is a different operation (§5 "Cancellation");</item>
///         <item>an empty queue is an idle pool, not an error.</item>
///     </list>
/// </summary>
public class AdapterPoolQueueToolsTests : ToolTestBase
{
    private const string PoolId = "cc0000000000000000000009";

    public AdapterPoolQueueToolsTests()
    {
        GivenAuthenticated();
    }

    // ── get_adapter_pool_queue ──────────────────────────────────────────────

    [Fact]
    public async Task GetQueue_HappyPath_ReportsPositionInTenantAndTenantsAhead()
    {
        MockCommunicationClient.Setup(c => c.GetAdapterPoolQueueAsync(PoolId))
            .ReturnsAsync(TwoTenantQueue());

        var result = await DataFlowTriggerPoolTools.GetAdapterPoolQueue(MockServer.Object, PoolId);

        result.IsSuccess.Should().BeTrue();
        result.AdapterPoolId.Should().Be(PoolId);
        result.Entries.Should().HaveCount(4);

        var second = result.Entries.Single(e => e.ExecutionId == "e-a2");
        second.PositionInTenant.Should().Be(2);
        second.TenantsAheadInRotation.Should().Be(0);

        var otherTenant = result.Entries.Single(e => e.ExecutionId == "e-b1");
        otherTenant.PositionInTenant.Should().Be(1);
        otherTenant.TenantsAheadInRotation.Should().Be(1);

        // No global rank anywhere on the response — not as a field, and not summarised into the
        // message the AI reads.
        typeof(GetAdapterPoolQueueResponse).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Rank", StringComparison.OrdinalIgnoreCase));
        result.Message.Should().Contain("no global rank");
    }

    [Fact]
    public async Task GetQueue_SeparatesLeasedFromWaitingAndKeepsTheMemberId()
    {
        MockCommunicationClient.Setup(c => c.GetAdapterPoolQueueAsync(PoolId))
            .ReturnsAsync(TwoTenantQueue());

        var result = await DataFlowTriggerPoolTools.GetAdapterPoolQueue(MockServer.Object, PoolId);

        result.LeasedCount.Should().Be(1);
        result.WaitingCount.Should().Be(3);

        var leased = result.Entries.Single(e => e.IsLeased);
        leased.LeasedOnMemberId.Should().Be("member-3");
        leased.PositionInTenant.Should().Be(0);
    }

    [Fact]
    public async Task GetQueue_EmptyPool_IsSuccessAndSaysSo()
    {
        MockCommunicationClient.Setup(c => c.GetAdapterPoolQueueAsync(PoolId))
            .ReturnsAsync([]);

        var result = await DataFlowTriggerPoolTools.GetAdapterPoolQueue(MockServer.Object, PoolId);

        result.IsSuccess.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Entries.Should().BeEmpty();
        result.WaitingCount.Should().Be(0);
        result.Message.Should().Contain("empty queue");
    }

    [Fact]
    public async Task GetQueue_MissingId_ReturnsValidationError()
    {
        var result = await DataFlowTriggerPoolTools.GetAdapterPoolQueue(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(c => c.GetAdapterPoolQueueAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetQueue_Unauthenticated_Refuses()
    {
        GivenUnauthenticated();

        var result = await DataFlowTriggerPoolTools.GetAdapterPoolQueue(MockServer.Object, PoolId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockCommunicationClient.Verify(c => c.GetAdapterPoolQueueAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetQueue_WhenSdkThrows_ReturnsErrorMessage()
    {
        MockCommunicationClient.Setup(c => c.GetAdapterPoolQueueAsync(PoolId))
            .ThrowsAsync(new InvalidOperationException("NotFound: no such adapter pool"));

        var result = await DataFlowTriggerPoolTools.GetAdapterPoolQueue(MockServer.Object, PoolId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("NotFound: no such adapter pool");
    }

    // ── cancel_queued_execution ─────────────────────────────────────────────

    [Fact]
    public async Task Cancel_WithoutConfirm_Refuses()
    {
        var result = await DataFlowTriggerPoolTools.CancelQueuedExecution(MockServer.Object, PoolId, "e-a2");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockCommunicationClient.Verify(
            c => c.CancelQueuedExecutionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_WaitingEntry_IsCancelled()
    {
        MockCommunicationClient.Setup(c => c.CancelQueuedExecutionAsync(PoolId, "e-a2"))
            .ReturnsAsync(new AdapterPoolQueueCancellationResultDto
            {
                Outcome = AdapterPoolQueueCancellationOutcome.Cancelled
            });

        var result = await DataFlowTriggerPoolTools.CancelQueuedExecution(MockServer.Object, PoolId, "e-a2",
            confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.WasCancelled.Should().BeTrue();
        result.Outcome.Should().Be(AdapterPoolQueueCancellationOutcome.Cancelled);
        result.Message.Should().Contain("never run");
        MockCommunicationClient.Verify(c => c.CancelQueuedExecutionAsync(PoolId, "e-a2"), Times.Once);
    }

    [Fact]
    public async Task Cancel_AlreadyLeased_IsItsOwnOutcomeAndNotACancellation()
    {
        MockCommunicationClient.Setup(c => c.CancelQueuedExecutionAsync(PoolId, "e-run"))
            .ReturnsAsync(new AdapterPoolQueueCancellationResultDto
            {
                Outcome = AdapterPoolQueueCancellationOutcome.AlreadyLeased,
                ServerMessage = "Execution 'e-run' already holds a lease and is no longer queued."
            });

        var result = await DataFlowTriggerPoolTools.CancelQueuedExecution(MockServer.Object, PoolId, "e-run",
            confirm: true);

        // The call worked; it just did not cancel anything. Reporting it as a failure would invite a
        // retry, and a retry is not what the caller needs to do.
        result.IsSuccess.Should().BeTrue();
        result.WasCancelled.Should().BeFalse();
        result.Outcome.Should().Be(AdapterPoolQueueCancellationOutcome.AlreadyLeased);
        result.Message.Should().Contain("NOTHING was cancelled");
        result.Message.Should().Contain("different operation");
    }

    [Fact]
    public async Task Cancel_UnknownEntry_IsNotFoundAndNotACancellation()
    {
        MockCommunicationClient.Setup(c => c.CancelQueuedExecutionAsync(PoolId, "e-gone"))
            .ReturnsAsync(new AdapterPoolQueueCancellationResultDto
            {
                Outcome = AdapterPoolQueueCancellationOutcome.NotFound
            });

        var result = await DataFlowTriggerPoolTools.CancelQueuedExecution(MockServer.Object, PoolId, "e-gone",
            confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.WasCancelled.Should().BeFalse();
        result.Outcome.Should().Be(AdapterPoolQueueCancellationOutcome.NotFound);
        result.Message.Should().Contain("No queued execution");
    }

    [Fact]
    public async Task Cancel_MissingExecutionId_ReturnsValidationError()
    {
        var result = await DataFlowTriggerPoolTools.CancelQueuedExecution(MockServer.Object, PoolId, "",
            confirm: true);

        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(
            c => c.CancelQueuedExecutionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_Unauthenticated_Refuses()
    {
        GivenUnauthenticated();

        var result = await DataFlowTriggerPoolTools.CancelQueuedExecution(MockServer.Object, PoolId, "e-a2",
            confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockCommunicationClient.Verify(
            c => c.CancelQueuedExecutionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_WhenSdkThrows_ReturnsErrorMessage()
    {
        MockCommunicationClient.Setup(c => c.CancelQueuedExecutionAsync(PoolId, "e-a2"))
            .ThrowsAsync(new InvalidOperationException("ServiceUnavailable"));

        var result = await DataFlowTriggerPoolTools.CancelQueuedExecution(MockServer.Object, PoolId, "e-a2",
            confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("ServiceUnavailable");
    }

    // ── Risk classification ─────────────────────────────────────────────────

    [Fact]
    public void QueueRead_IsLow_AndCancel_IsHigh()
    {
        var registry = new ToolRiskRegistry(typeof(DataFlowTriggerPoolTools).Assembly);

        registry.GetRiskLevel("get_adapter_pool_queue").Should().Be(McpRiskLevel.Low);
        // Destructive: the work item is discarded and never runs. Every other destructive verb in
        // this tool class pauses the worker, and losing a tenant's queued run unannounced is exactly
        // what the approval gate exists for.
        registry.GetRiskLevel("cancel_queued_execution").Should().Be(McpRiskLevel.High);
    }

    /// <summary>
    ///     One leased entry plus two borrowing tenants waiting — the shape that makes a single global
    ///     rank impossible: borrower-b's first item runs before borrower-a's second one.
    /// </summary>
    private static IReadOnlyList<AdapterPoolQueueEntryDto> TwoTenantQueue() =>
    [
        new()
        {
            ExecutionId = "e-run", BorrowerTenantId = "borrower-a", PipelineName = "Nightly",
            ExecutionClass = 1, QueuedAtUtc = new DateTime(2026, 9, 14, 7, 59, 0, DateTimeKind.Utc),
            PositionInTenant = 0, TenantsAheadInRotation = 0, LeasedOnMemberId = "member-3",
            LeaseExpiresAtUtc = new DateTime(2026, 9, 14, 8, 10, 0, DateTimeKind.Utc)
        },
        new()
        {
            ExecutionId = "e-a1", BorrowerTenantId = "borrower-a", PipelineName = "Nightly",
            ExecutionClass = 1, QueuedAtUtc = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc),
            PositionInTenant = 1, TenantsAheadInRotation = 0
        },
        new()
        {
            ExecutionId = "e-a2", BorrowerTenantId = "borrower-a", PipelineName = "Nightly",
            ExecutionClass = 1, QueuedAtUtc = new DateTime(2026, 9, 14, 8, 0, 5, DateTimeKind.Utc),
            PositionInTenant = 2, TenantsAheadInRotation = 0
        },
        new()
        {
            ExecutionId = "e-b1", BorrowerTenantId = "borrower-b", PipelineName = "Billing",
            ExecutionClass = 0, QueuedAtUtc = new DateTime(2026, 9, 14, 8, 0, 7, DateTimeKind.Utc),
            PositionInTenant = 1, TenantsAheadInRotation = 1
        }
    ];
}
