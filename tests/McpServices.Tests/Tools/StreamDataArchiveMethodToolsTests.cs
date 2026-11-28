using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.StreamData.v1;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     The archive write tools run the CK methods of <c>System.StreamData</c> through the generated method clients on the
///     tenant client (<see cref="ArchiveMethodCall" />): every tool sends its method, never a REST write of the stream data
///     client; the lifecycle and column tools use the client of the archive's concrete type; a refusal comes back as
///     <c>CODE: message (params)</c>, an asset repository without the methods as a text that names the needed
///     System.StreamData version, an invalid runtime id before any call. backfill_rollup_archive follows the operation of
///     <c>BackfillFromSource-2</c> to its queued job; its answers are the ones recorded from a stream-data tenant.
/// </summary>
public sealed class StreamDataArchiveMethodToolsTests : ToolTestBase, IDisposable
{
    private const string ArchiveId = "6b0a88eafa75fad21127ffa9";
    private const string RollupId = "6b0a88fefa75fad21127ffb3";

    /// <summary>Tool name, the call with valid arguments, whether it reads the archive type, and its operation name.</summary>
    private static readonly Dictionary<string, (Func<StreamDataArchiveMethodToolsTests, Task<(bool Ok, string? Error)>> Call,
        bool ReadsType, string Operation)> Tools = new()
    {
        ["activate_archive"] = (async t => Of(await TimeSeriesTools.ActivateArchive(t.MockServer.Object, ArchiveId)), true,
            "SystemStreamDataTimeRangeArchiveActivate"),
        ["disable_archive"] = (async t => Of(await TimeSeriesTools.DisableArchive(t.MockServer.Object, ArchiveId)), true,
            "SystemStreamDataTimeRangeArchiveDisable"),
        ["enable_archive"] = (async t => Of(await TimeSeriesTools.EnableArchive(t.MockServer.Object, ArchiveId)), true,
            "SystemStreamDataTimeRangeArchiveEnable"),
        ["retry_archive_activation"] = (async t => Of(await TimeSeriesTools.RetryArchiveActivation(t.MockServer.Object,
            ArchiveId)), true, "SystemStreamDataTimeRangeArchiveRetryActivation"),
        ["add_archive_column"] = (async t => Of(await TimeSeriesTools.AddArchiveColumn(t.MockServer.Object, ArchiveId,
            "WindowsProcessed")), true, "SystemStreamDataTimeRangeArchiveAddColumn"),
        ["delete_archive"] = (async t => Of(await TimeSeriesTools.DeleteArchive(t.MockServer.Object, ArchiveId,
            confirm: true)), true, "SystemStreamDataTimeRangeArchiveDeleteArchive"),
        ["freeze_rollup_archive"] = (async t => Of(await TimeSeriesTools.FreezeRollupArchive(t.MockServer.Object, RollupId,
            new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc))), false, "SystemStreamDataRollupArchiveFreeze"),
        ["unfreeze_rollup_archive"] = (async t => Of(await TimeSeriesTools.UnfreezeRollupArchive(t.MockServer.Object,
            RollupId)), false, "SystemStreamDataRollupArchiveUnfreeze"),
        ["rewind_rollup_watermark"] = (async t => Of(await TimeSeriesTools.RewindRollupWatermark(t.MockServer.Object,
            RollupId, new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc), confirm: true)), false,
            "SystemStreamDataRollupArchiveRewindWatermark"),
        ["backfill_rollup_archive"] = (async t => Of(await TimeSeriesTools.BackfillRollupArchive(t.MockServer.Object,
            RollupId, confirm: true)), false, "SystemStreamDataRollupArchiveBackfillFromSource2"),
    };

    public static TheoryData<string> ToolNames => new(Tools.Keys);

    private GraphQlListener? _service;

    public StreamDataArchiveMethodToolsTests()
    {
        GivenAuthenticated();
        CkMethods.GivenQueryReturns(new ArchiveMethodCall.ArchiveTypeDto
        {
            RtId = ArchiveId, CkTypeId = "System.StreamData/TimeRangeArchive-1"
        });
    }

    public void Dispose() => _service?.Dispose();

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_RunsItsMethod_AndCallsNoRestWrite(string name)
    {
        // The backfill answers no operation handle here (the fake has no result for it): a failure after the method was sent.
        CkMethods.Succeeds(name == "delete_archive"
            ? true
            : new RtArchiveTransitionResultRecordDto { ArchiveRtId = ArchiveId, Transition = "x" });

        var (ok, error) = await Tools[name].Call(this);

        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be(Tools[name].Operation);
        CkMethods.RtIdOf(Tools[name].Operation).Should().Be(Tools[name].ReadsType ? ArchiveId : RollupId);
        if (name != "backfill_rollup_archive")
        {
            ok.Should().BeTrue(error);
        }

        MockTenantClient.Verify(c => c.SendQueryAsync<ArchiveMethodCall.ArchiveTypeDto>(It.IsAny<GraphQL.GraphQLRequest>()),
            Tools[name].ReadsType ? Times.Once() : Times.Never());
        MockStreamDataClient.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_RefusedByTheMethodGateway_ReportsTheCode(string name)
    {
        CkMethods.Fails("METHOD_FORBIDDEN", "The CK method call failed: METHOD_FORBIDDEN",
            new Dictionary<string, string> { ["reason"] = "role-missing" });

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeFalse();
        error.Should().Contain("METHOD_FORBIDDEN").And.Contain("reason=role-missing");
        MockStreamDataClient.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_WithoutTheMethods_NamesTheNeededVersion(string name)
    {
        CkMethods.HandlerNotAvailable();

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeFalse();
        error.Should().Contain("System.StreamData 1.30").And.Contain("does not fall back to the REST endpoint");
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_WithASchemaWithoutTheMethod_NamesTheNeededVersion(string name)
    {
        CkMethods.MissingInSchema();

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeFalse();
        error.Should().Contain("System.StreamData 1.30");
    }

    [Fact]
    public async Task DeclaredError_IsReportedWithCodeAndParameters()
    {
        CkMethods.Fails("STREAMDATA_INVALID_TRANSITION", "The CK method call failed: STREAMDATA_INVALID_TRANSITION",
            new Dictionary<string, string> { ["fromStatus"] = "Created", ["attemptedTransition"] = "disable" });

        var result = await TimeSeriesTools.DisableArchive(MockServer.Object, ArchiveId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("STREAMDATA_INVALID_TRANSITION: ")
            .And.Contain("attemptedTransition=disable").And.Contain("fromStatus=Created");
    }

    [Theory]
    [InlineData("archive-1")]
    [InlineData("6b0a88eafa75fad21127ffa")]
    public async Task InvalidRuntimeId_IsRefusedBeforeAnyCall(string id)
    {
        CkMethods.Succeeds();

        var lifecycle = await TimeSeriesTools.ActivateArchive(MockServer.Object, id);
        var rollup = await TimeSeriesTools.FreezeRollupArchive(MockServer.Object, id, DateTime.UtcNow);
        var backfill = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, id, confirm: true);

        lifecycle.ErrorMessage.Should().Be($"archiveRtId '{id}' is not a valid runtime id.");
        rollup.ErrorMessage.Should().Be($"rollupRtId '{id}' is not a valid runtime id.");
        backfill.ErrorMessage.Should().Be($"rollupRtId '{id}' is not a valid runtime id.");
        CkMethods.Requests.Should().BeEmpty();
        MockTenantClient.Verify(c => c.SendQueryAsync<ArchiveMethodCall.ArchiveTypeDto>(It.IsAny<GraphQL.GraphQLRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task UnknownArchive_IsRefusedBeforeAnyWrite()
    {
        CkMethods.GivenQueryReturns<ArchiveMethodCall.ArchiveTypeDto>().Succeeds();

        var result = await TimeSeriesTools.ActivateArchive(MockServer.Object, ArchiveId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be($"Archive '{ArchiveId}' not found.");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("System.StreamData/RawArchive", "SystemStreamDataRawArchiveActivate")]
    [InlineData("System.StreamData/RawArchive-1", "SystemStreamDataRawArchiveActivate")]
    [InlineData("System.StreamData/RollupArchive-2", "SystemStreamDataRollupArchiveActivate")]
    public async Task Activate_UsesTheClientOfTheConcreteType_WhateverTheElementVersion(string ckTypeId, string operation)
    {
        CkMethods.GivenQueryReturns(new ArchiveMethodCall.ArchiveTypeDto { RtId = ArchiveId, CkTypeId = ckTypeId }).Succeeds();

        await TimeSeriesTools.ActivateArchive(MockServer.Object, ArchiveId);

        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be(operation);
    }

    [Theory]
    [InlineData("System.StreamData/RecomputeJob")]
    [InlineData("System.StreamData/Archive")]
    [InlineData("System.Communication/Adapter")]
    public async Task Activate_OfANonArchiveType_IsRefusedBeforeAnyWrite(string ckTypeId)
    {
        CkMethods.GivenQueryReturns(new ArchiveMethodCall.ArchiveTypeDto { RtId = ArchiveId, CkTypeId = ckTypeId }).Succeeds();

        var result = await TimeSeriesTools.ActivateArchive(MockServer.Object, ArchiveId);

        result.ErrorMessage.Should().Contain("which is no RawArchive, TimeRangeArchive or RollupArchive");
        CkMethods.Requests.Should().BeEmpty();
    }

    // backfill_rollup_archive against a loopback asset repository with the recorded answers of a stream-data tenant.

    [Fact]
    public async Task Backfill_ReturnsTheQueuedJob_FromTheOperation()
    {
        GivenLoopback(Recorded("rollup-backfill-from-source2-accepted"), Running("6b0a8911fa75fad21127ffc5"));

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Job!.RtId.Should().Be("6b0a8911fa75fad21127ffc5");
        result.Job.State.Should().Be("Pending");
        result.IsNoOp.Should().BeFalse();
        result.OperationId.Should().Be("ae9db76d1e4e47ea8beb3b81e7811ccb");
        result.Message.Should().Be($"Backfill of rollup '{RollupId}' queued (durable background): job 6b0a8911fa75fad21127ffc5, " +
                                   $"state Pending. Poll list_recompute_jobs for archive '{RollupId}' to watch it run to completion.");
        _service!.Bodies[0].Should().Contain("systemStreamDataRollupArchives { backfillFromSource2(");
        _service.Bodies[1].Should().Contain("query CkOperationById");
        MockStreamDataClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Backfill_WithoutSourceData_IsTheNoOpOfToday()
    {
        GivenLoopback(Recorded("rollup-backfill-from-source2-accepted"), Recorded("operation-backfill-succeeded"));

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Job.Should().BeNull();
        result.IsNoOp.Should().BeTrue();
        result.Message.Should().Contain("no-op");
    }

    [Fact]
    public async Task Backfill_EndedBeforeTheFirstRead_ReturnsTheCompletedJob()
    {
        GivenLoopback(Recorded("rollup-backfill-from-source2-accepted"), Recorded("operation-recompute-succeeded"));

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Job!.RtId.Should().Be("6b0a8911fa75fad21127ffbb");
        result.Job.State.Should().Be("Completed");
        result.Job.WindowsProcessed.Should().Be(24);
    }

    [Fact]
    public async Task Backfill_FailedJob_ReturnsTheFailedJobWithItsReason()
    {
        GivenLoopback(Recorded("rollup-backfill-from-source2-accepted"), Recorded("operation-recompute-failed"));

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Job!.State.Should().Be("Failed");
        result.Job.ErrorReason.Should().StartWith("Archive is not activated");
    }

    [Fact]
    public async Task Backfill_NamingNoJobBeforeTheTimeout_SaysItRunsInTheBackground()
    {
        GivenLoopback([Recorded("rollup-backfill-from-source2-accepted"), .. Enumerable.Repeat(Running(null), 10)]);

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Job.Should().BeNull();
        result.IsNoOp.Should().BeFalse("a null job without the no-op flag means the backfill runs in the background");
        result.OperationId.Should().Be("ae9db76d1e4e47ea8beb3b81e7811ccb");
        result.Message.Should().Contain("accepted as operation ae9db76d1e4e47ea8beb3b81e7811ccb").And.Contain("named no job yet");
    }

    [Fact]
    public async Task Backfill_JobNamedAtALaterStage_IsRunning()
    {
        GivenLoopback(Recorded("rollup-backfill-from-source2-accepted"), Running("6b0a8911fa75fad21127ffc5", "RECOMPUTING"));

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.Job!.State.Should().Be("Running");
    }

    [Fact]
    public async Task Backfill_OperationNotReadable_IsAFailure()
    {
        GivenLoopback(Recorded("rollup-backfill-from-source2-accepted"),
            """{"errors":[{"message":"The operation was not found.","extensions":{"code":"OPERATION_NOT_FOUND"}}],"data":{"runtime":{"operations":null}}}""");

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("OPERATION_NOT_FOUND");
    }

    [Fact]
    public async Task Backfill_OperationFailedWithAnotherCode_IsAFailure()
    {
        var failed = JsonNode.Parse(Recorded("operation-recompute-failed"))!;
        var error = failed["data"]!["runtime"]!["operations"]!["items"]![0]!["error"]!.AsObject();
        error["code"] = "METHOD_HANDLER_FAILED";
        error["parameters"] = new JsonArray();
        GivenLoopback(Recorded("rollup-backfill-from-source2-accepted"), failed.ToJsonString());

        var result = await TimeSeriesTools.BackfillRollupArchive(MockServer.Object, RollupId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("METHOD_HANDLER_FAILED");
    }

    [Fact]
    public async Task TenantWithoutTheArchiveQuery_NamesTheNeededVersion()
    {
        MockTenantClient.Setup(c => c.SendQueryAsync<ArchiveMethodCall.ArchiveTypeDto>(It.IsAny<GraphQL.GraphQLRequest>()))
            .ThrowsAsync(new QlQueryErrorException("GraphQL error",
            [
                new GraphQL.GraphQLError
                {
                    Message = "Cannot query field 'systemStreamDataArchive' on type 'RuntimeModelQuery'.",
                    Extensions = new GraphQL.Map { ["code"] = "FIELDS_ON_CORRECT_TYPE", ["number"] = "5.3.1" }
                }
            ]));
        CkMethods.Succeeds();

        var result = await TimeSeriesTools.ActivateArchive(MockServer.Object, ArchiveId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.StreamData 1.30");
        CkMethods.Requests.Should().BeEmpty();
    }

    private void GivenLoopback(params string[] answers)
    {
        _service = new GraphQlListener();
        _service.RespondInOrder(answers);
        var token = new Mock<ITenantClientAccessToken>();
        var tenantClient = new TenantClient(new TenantClientOptions { EndpointUri = _service.BaseUrl, TenantId = DefaultTenantId },
            token.Object);
        MockClientFactory.Setup(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>())).Returns(tenantClient);
        TestServiceProvider.RegisterService<TimeProvider>(new AdvancingTimeProvider());
    }

    private static (bool, string?) Of(Meshmakers.Octo.Backend.McpServices.Models.ArchiveActionResponse r) => (r.IsSuccess, r.ErrorMessage);

    private static (bool, string?) Of(Meshmakers.Octo.Backend.McpServices.Models.RollupBackfillResponse r) => (r.IsSuccess, r.ErrorMessage);

    private static string Recorded(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "Fixtures", $"assetrepo-streamdata-answer-{name}.json"));

    /// <summary>The recorded succeeded recompute turned into a RUNNING operation; with a job id it is the QUEUED stage.</summary>
    private static string Running(string? jobRtId, string stage = "QUEUED")
    {
        var answer = JsonNode.Parse(Recorded("operation-recompute-succeeded"))!;
        var item = answer["data"]!["runtime"]!["operations"]!["items"]![0]!.AsObject();
        item["state"] = "RUNNING";
        item["result"] = null;
        item["completedAtUtc"] = null;
        item["progress"] = new JsonObject
        {
            ["percent"] = null,
            ["stageCode"] = jobRtId == null ? "RECOMPUTING" : stage,
            ["parameters"] = jobRtId == null
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["name"] = "jobRtId", ["value"] = jobRtId })
        };
        return answer.ToJsonString();
    }

    /// <summary>Lets every delay elapse at once and moves the clock by it (the wait timeout reads the clock).</summary>
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private readonly Lock _sync = new();
        private DateTimeOffset _now = new(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                _now += dueTime;
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A local GraphQL endpoint answering with queued bodies in order (then <c>{}</c>) and recording requests.</summary>
    private sealed class GraphQlListener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Queue<string> _queued = new();
        private readonly List<string> _bodies = [];
        private readonly Lock _sync = new();

        public GraphQlListener()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_sync)
                {
                    return _bodies.ToArray();
                }
            }
        }

        public void RespondInOrder(params string[] bodies)
        {
            lock (_sync)
            {
                foreach (var body in bodies)
                {
                    _queued.Enqueue(body);
                }
            }
        }

        public void Dispose() => _listener.Abort();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                string response;
                lock (_sync)
                {
                    _bodies.Add(body);
                    response = _queued.Count > 0 ? _queued.Dequeue() : "{}";
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }
}
