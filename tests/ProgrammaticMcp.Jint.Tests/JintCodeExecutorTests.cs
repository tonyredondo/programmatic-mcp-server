using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ProgrammaticMcp.Jint;

namespace ProgrammaticMcp.Jint.Tests;

public sealed class JintCodeExecutorTests
{
    [Fact]
    public async Task ExecutorSupportsObjectArrayScalarAndNullResults()
    {
        var fixture = CreateFixture();

        var objectResult = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest("conv-1", "async function main() { return { ok: true, values: [1, 2] }; }"));
        var arrayResult = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest("conv-1", "async function main() { return [1, 2, 3]; }"));
        var scalarResult = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest("conv-1", "async function main() { return 42; }"));
        var nullResult = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest("conv-1", "async function main() { return null; }"));

        Assert.Equal("""{"ok":true,"values":[1,2]}""", CanonicalJson.Serialize(objectResult.Result));
        Assert.Equal("[1,2,3]", CanonicalJson.Serialize(arrayResult.Result));
        Assert.Equal("42", CanonicalJson.Serialize(scalarResult.Result));
        Assert.Equal("null", CanonicalJson.Serialize(nullResult.Result));
    }

    [Fact]
    public async Task AsyncEntrypointsSupportMixedSyncAndAsyncCapabilities()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    const doubled = await programmatic.math.double({ value: 21 });
                    return await programmatic.math.incrementAsync({ value: doubled });
                }
                """,
                VisibleApiPaths: new[] { "math.double", "math.incrementAsync" }));

        Assert.Equal("43", CanonicalJson.Serialize(result.Result));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task PromiseAllStillSerializesHostDispatch()
    {
        var activeCalls = 0;
        var maxObserved = 0;
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddCapability<ValueInput, int>(
                    "math.delayedEcho",
                    capability => capability
                        .WithDescription("Returns a delayed value.")
                        .UseWhen("You need to observe serialized dispatch.")
                        .DoNotUseWhen("You need parallel host execution.")
                        .WithHandler(async (input, context) =>
                        {
                            var current = Interlocked.Increment(ref activeCalls);
                            maxObserved = Math.Max(maxObserved, current);
                            try
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(40), context.CancellationToken);
                                return input.Value;
                            }
                            finally
                            {
                                Interlocked.Decrement(ref activeCalls);
                            }
                        })));

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await Promise.all([
                        programmatic.math.delayedEcho({ value: 1 }),
                        programmatic.math.delayedEcho({ value: 2 })
                    ]);
                }
                """));

        Assert.Equal("[1,2]", CanonicalJson.Serialize(result.Result));
        Assert.Equal(1, maxObserved);
    }

    [Fact]
    public async Task VisibleApiPathsRestrictTheNamespaceAndHideTheRawBridge()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return {
                        incrementType: typeof programmatic.math.incrementAsync,
                        rawBridgeType: typeof globalThis.__pmInvoke,
                        capabilityVersionType: typeof programmatic.__meta.capabilityVersion,
                        processType: typeof process,
                        requireType: typeof require
                    };
                }
                """,
                VisibleApiPaths: new[] { "math.double" }));

        Assert.Equal("""{"capabilityVersionType":"string","incrementType":"undefined","processType":"undefined","rawBridgeType":"undefined","requireType":"undefined"}""", CanonicalJson.Serialize(result.Result));
        Assert.Equal(new[] { "math.double" }, result.EffectiveVisibleApiPaths);
    }

    [Fact]
    public async Task RuntimeAlwaysExposesClientSampleAndRuntimeContractVersion()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return {
                        runtimeContractVersion: programmatic.__meta.runtimeContractVersion,
                        sampleType: typeof programmatic.client.sample
                    };
                }
                """,
                VisibleApiPaths: new[] { "math.double" }));

        Assert.Equal(
            """{"runtimeContractVersion":"programmatic-runtime-v2","sampleType":"function"}""",
            CanonicalJson.Serialize(result.Result));
    }

    [Fact]
    public async Task ClientSampleIsUnavailableWithoutAContextualSamplingClient()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.client.sample({
                            messages: [{ role: "user", text: "Hello" }]
                        });
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                VisibleApiPaths: Array.Empty<string>()));

        Assert.Equal("sampling_unavailable", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task ClientSampleUsesAnInjectedSamplingClient()
    {
        var fixture = CreateFixture();
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.client.sample({
                        systemPrompt: "Be brief",
                        messages: [{ role: "user", text: "Hello" }]
                    });
                }
                """,
                VisibleApiPaths: Array.Empty<string>(),
                Services: services));

        Assert.Equal("sampled:Hello", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task ClientSampleRejectsInvalidBridgeRequestsBeforeSamplingStarts()
    {
        var fixture = CreateFixture();
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var nonObjectRequest = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.client.sample("hello");
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                VisibleApiPaths: Array.Empty<string>(),
                Services: services));

        Assert.Equal("invalid_params", nonObjectRequest.Result?.GetValue<string>());

        var invalidRequest = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.client.sample({ messages: [] });
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                VisibleApiPaths: Array.Empty<string>(),
                Services: services));

        Assert.Equal("invalid_params", invalidRequest.Result?.GetValue<string>());
    }

    [Fact]
    public async Task ClientSampleUsesTheStructuredPathForToolEnabledRequests()
    {
        var fixture = CreateFixture();
        var publicClient = new ThrowingSamplingClient("public_client_used");
        var structuredClient = new ScriptedStructuredSamplingClient(
            new Func<ProgrammaticStructuredSamplingRequest, ProgrammaticStructuredSamplingResult>[]
            {
                _ => new ProgrammaticStructuredSamplingResult(
                    "assistant",
                    [new ProgrammaticStructuredSamplingToolUseBlock("tool-1", "lookup", new JsonObject { ["name"] = "world" })],
                    "fake-model",
                    "toolUse"),
                request =>
                {
                    var toolResult = Assert.IsType<ProgrammaticStructuredSamplingToolResultBlock>(request.Messages.Last().Content.Single());
                    Assert.Equal("tool-1", toolResult.ToolUseId);
                    return new ProgrammaticStructuredSamplingResult(
                        "assistant",
                        [new ProgrammaticStructuredSamplingTextBlock("sampled via structured tool loop")],
                        "fake-model",
                        "endTurn");
                }
            });
        using var services = new SamplingServiceProvider(
            publicClient: publicClient,
            structuredClient: structuredClient,
            samplingTools: new FakeSamplingToolRegistry());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.client.sample({
                        messages: [{ role: "user", text: "Hello" }],
                        enableTools: true,
                        allowedToolNames: ["lookup"]
                    });
                }
                """,
                VisibleApiPaths: Array.Empty<string>(),
                Services: services));

        Assert.Equal("sampled via structured tool loop", result.Result?.GetValue<string>());
        Assert.Equal(0, publicClient.CallCount);
        Assert.Equal(2, structuredClient.CallCount);
    }

    [Fact]
    public async Task ClientSampleUsesTheStructuredPathForToolEnabledRequestsEvenWhenOneServiceImplementsBothInterfaces()
    {
        var fixture = CreateFixture();
        var client = new DualInterfaceSamplingClient(
            new Func<ProgrammaticStructuredSamplingRequest, ProgrammaticStructuredSamplingResult>[]
            {
                _ => new ProgrammaticStructuredSamplingResult(
                    "assistant",
                    [new ProgrammaticStructuredSamplingToolUseBlock("tool-1", "lookup", new JsonObject { ["name"] = "world" })],
                    "fake-model",
                    "toolUse"),
                _ => new ProgrammaticStructuredSamplingResult(
                    "assistant",
                    [new ProgrammaticStructuredSamplingTextBlock("dual-interface structured path")],
                    "fake-model",
                    "endTurn")
            });
        using var services = new SamplingServiceProvider(
            publicClient: client,
            structuredClient: client,
            samplingTools: new FakeSamplingToolRegistry());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.client.sample({
                        messages: [{ role: "user", text: "Hello" }],
                        enableTools: true,
                        allowedToolNames: ["lookup"]
                    });
                }
                """,
                VisibleApiPaths: Array.Empty<string>(),
                Services: services));

        Assert.Equal("dual-interface structured path", result.Result?.GetValue<string>());
        Assert.Equal(0, client.PublicCallCount);
        Assert.Equal(2, client.StructuredCallCount);
    }

    [Fact]
    public async Task ClientSampleRequiresAnExplicitReadOnlyScopeEvenWithAnInjectedSamplingClient()
    {
        var fixture = CreateFixture();
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.client.sample({
                            messages: [{ role: "user", text: "Hello" }]
                        });
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                Services: services));

        Assert.Equal("sampling_requires_explicit_read_only_scope", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task ClientSampleBlocksVisibleMutationsEvenWithAnInjectedSamplingClient()
    {
        var fixture = CreateFixture();
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.client.sample({
                            messages: [{ role: "user", text: "Hello" }]
                        });
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                VisibleApiPaths: new[] { "tasks.complete" },
                Services: services));

        Assert.Equal("sampling_not_allowed_with_visible_mutations", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityHandlersRequireAnExplicitReadOnlyScopeForSampling()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddCapability<EmptyInput, string>(
                    "diag.askClient",
                    capability => capability
                        .WithDescription("Requests a sample from the current client.")
                        .UseWhen("You need to validate capability-context sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                try
                                {
                                    var response = await context.GetSamplingClient().CreateMessageAsync(
                                        new ProgrammaticSamplingRequest(null, [new ProgrammaticSamplingMessage("user", "Hello")]),
                                        context.CancellationToken);
                                    return response.Text;
                                }
                                catch (ProgrammaticSamplingException exception)
                                {
                                    return exception.Code;
                                }
                            })));
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.diag.askClient({});
                }
                """,
                Services: services));

        Assert.Equal("sampling_requires_explicit_read_only_scope", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityHandlersBlockVisibleMutationsForSampling()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddCapability<EmptyInput, string>(
                    "diag.askClient",
                    capability => capability
                        .WithDescription("Requests a sample from the current client.")
                        .UseWhen("You need to validate capability-context sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                try
                                {
                                    var response = await context.GetSamplingClient().CreateMessageAsync(
                                        new ProgrammaticSamplingRequest(null, [new ProgrammaticSamplingMessage("user", "Hello")]),
                                        context.CancellationToken);
                                    return response.Text;
                                }
                                catch (ProgrammaticSamplingException exception)
                                {
                                    return exception.Code;
                                }
                            })));
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.diag.askClient({});
                }
                """,
                VisibleApiPaths: new[] { "diag.askClient", "tasks.complete" },
                Services: services));

        Assert.Equal("sampling_not_allowed_with_visible_mutations", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task MutationPreviewHandlersReceiveBlockedSamplingClients()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddMutation<TaskMutationArgs, string, TaskMutationApplyResult>(
                    "tasks.inspectSampling",
                    mutation => mutation
                        .WithDescription("Reports the mutation-context sampling code.")
                        .UseWhen("You need to validate mutation-context sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithPreviewHandler(
                            async (_, context) =>
                            {
                                try
                                {
                                    var client = ProgrammaticSamplingServiceResolver.ResolvePublic(context.Services);
                                    var response = await client.CreateMessageAsync(
                                        new ProgrammaticSamplingRequest(null, [new ProgrammaticSamplingMessage("user", "Hello")]),
                                        context.CancellationToken);
                                    return response.Text;
                                }
                                catch (ProgrammaticSamplingException exception)
                                {
                                    return exception.Code;
                                }
                            })
                        .WithSummaryFactory((args, _, _) => ValueTask.FromResult($"Inspect {args.TaskId}"))
                        .WithApplyHandler((args, _) => ValueTask.FromResult(MutationApplyResult<TaskMutationApplyResult>.Success(new TaskMutationApplyResult(args.TaskId, "done"))))));
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.tasks.inspectSampling({ taskId: "task-1" });
                }
                """,
                CallerBindingId: "binding-1",
                VisibleApiPaths: new[] { "tasks.inspectSampling" },
                Services: services));

        Assert.Equal("sampling_not_allowed_in_mutation_context", result.ApprovalsRequested.Single().Preview?.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityExecutionScopesKeepPublicAndStructuredSamplingStatesAligned()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddCapability<EmptyInput, SamplingClientState>(
                    "diag.inspectSamplingState",
                    capability => capability
                        .WithDescription("Inspects public and structured sampling state.")
                        .UseWhen("You need to validate paired sampling-state overlays.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithHandler(
                            (_, context) =>
                            {
                                var publicClient = ProgrammaticSamplingServiceResolver.ResolvePublic(context.Services);
                                var structuredClient = ProgrammaticSamplingServiceResolver.ResolveStructured(context.Services);
                                return ValueTask.FromResult(
                                    new SamplingClientState(
                                        publicClient.IsSupported,
                                        publicClient.SupportsToolUse,
                                        structuredClient.IsSupported,
                                        structuredClient.SupportsToolUse));
                            })));
        using var services = new SamplingServiceProvider(publicClient: new FakeSamplingClient());

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.diag.inspectSamplingState({});
                }
                """,
                VisibleApiPaths: new[] { "diag.inspectSamplingState" },
                Services: services));

        Assert.Equal(
            """{"publicIsSupported":true,"publicSupportsToolUse":false,"structuredIsSupported":true,"structuredSupportsToolUse":false}""",
            CanonicalJson.Serialize(result.Result));
    }

    [Fact]
    public async Task ReadOnlyExecutionsDoNotEmitMutationWarningsWhenNoMutationIsInvoked()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.math.double({ value: 21 });
                }
                """));

        Assert.Equal("42", CanonicalJson.Serialize(result.Result));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task UnknownCapabilitySuggestionsStayInsideTheVisibleSubset()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.math.doubl({ value: 1 });
                    } catch (error) {
                        return error.data.suggestions;
                    }
                }
                """,
                VisibleApiPaths: new[] { "math.double" }));

        Assert.Equal("""["math.double"]""", CanonicalJson.Serialize(result.Result));
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "unknown_capability");
        Assert.Equal("""["math.double"]""", CanonicalJson.Serialize(diagnostic.Data?["suggestions"]));
    }

    [Fact]
    public async Task RejectsOversizedCodeAndArgsBeforeExecution()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MaxCodeBytes = 16,
                MaxArgsBytes = 8
            });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await fixture.Executor.ExecuteAsync(
                new CodeExecutionRequest("conv-1", "async function main() { return 1; }")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await fixture.Executor.ExecuteAsync(
                new CodeExecutionRequest(
                    "conv-1",
                    "async function main(input) { return input; }",
                    Args: JsonNode.Parse("""{"value":"123456789"}""")!.AsObject())));
    }

    [Fact]
    public void CodeExecutionRequestArgsAreObjectTyped()
    {
        Assert.Equal(typeof(JsonObject), typeof(CodeExecutionRequest).GetProperty(nameof(CodeExecutionRequest.Args))!.PropertyType);
    }

    [Fact]
    public async Task ConsoleCaptureUsesStableStringificationRules()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    const cycle = {};
                    cycle.self = cycle;
                    console.log({ b: 2, a: 1 });
                    console.log(NaN, Infinity, -Infinity);
                    console.warn(Symbol('x'));
                    console.error(cycle);
                    return null;
                }
                """));

        Assert.Equal("""{"a":1,"b":2}""", result.Console[0].Message);
        Assert.Equal("NaN Infinity -Infinity", result.Console[1].Message);
        Assert.Equal("Symbol(x)", result.Console[2].Message);
        Assert.Equal("[Unserializable]", result.Console[3].Message);
    }

    [Fact]
    public async Task ConsoleOutputIsTruncatedWhenItExceedsTheConfiguredLimits()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MaxConsoleLines = 2,
                MaxConsoleBytes = 64
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    console.log("one");
                    console.log("two");
                    console.log("three");
                    return null;
                }
                """));

        Assert.Equal(2, result.Console.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "console_output_truncated");
    }

    [Fact]
    public async Task HandlerExceptionsRejectTheJavaScriptPromiseWithStructuredData()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        await programmatic.throws.always({});
                        return "unexpected";
                    } catch (error) {
                        return `${error.code}:${error.capabilityPath}`;
                    }
                }
                """));

        Assert.Equal("capability_handler_error:throws.always", result.Result?.GetValue<string>());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "capability_handler_error");
    }

    [Fact]
    public async Task SyntaxErrorsAreMappedWithLocationData()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    const value = ;
                    return value;
                }
                """,
                VisibleApiPaths: new[] { "math.double" }));

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "syntax_error");
        Assert.NotNull(diagnostic.Data?["line"]);
        Assert.NotNull(diagnostic.Data?["column"]);
    }

    [Fact]
    public async Task MutationPreviewRequiresCallerBinding()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.tasks.complete({ taskId: "task-1" });
                    } catch (error) {
                        return error.code;
                    }
                }
                """));

        Assert.Equal("mutation_preview_unavailable", result.Result?.GetValue<string>());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "mutation_preview_unavailable");
        Assert.Empty(result.ApprovalsRequested);
    }

    [Fact]
    public async Task MutationPreviewCreatesApprovalAndMirrorsApprovalsRequested()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.tasks.complete({ taskId: "task-1" });
                }
                """,
                CallerBindingId: "binding-1"));

        Assert.Single(result.ApprovalsRequested);
        Assert.Equal(CanonicalJson.Serialize(result.ApprovalsRequested[0].Preview), CanonicalJson.Serialize(result.Result?["preview"]));
        var stored = await fixture.ApprovalStore.GetAsync(result.ApprovalsRequested[0].ApprovalId);
        Assert.NotNull(stored);
        Assert.Equal("binding-1", stored!.CallerBindingId);
    }

    [Fact]
    public async Task OversizedResultsSpillToArtifactsWhenCallerBindingIsAvailable()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MaxResultBytes = 32
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return { message: "this result is intentionally too large to fit inline" };
                }
                """,
                CallerBindingId: "binding-1"));

        Assert.Null(result.Result);
        Assert.NotNull(result.ResultArtifactId);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "result_spilled_to_artifact");
        var artifact = await fixture.ArtifactStore.ReadAsync(new ArtifactReadRequest(result.ResultArtifactId!, "conv-1", "binding-1"));
        Assert.True(artifact.Found);
        Assert.Equal("execution.result", artifact.Kind);
    }

    [Fact]
    public async Task OversizedResultsFailInlineWithoutArtifactContinuity()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MaxResultBytes = 32
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return { message: "this result is intentionally too large to fit inline" };
                }
                """));

        Assert.Null(result.Result);
        Assert.Null(result.ResultArtifactId);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "result_too_large");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "artifact_continuity_unavailable");
    }

    [Fact]
    public async Task HandlersCanCreateArtifactsExplicitly()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.artifacts.emit({ content: "hello from handler" });
                }
                """,
                CallerBindingId: "binding-1"));

        var descriptor = Assert.Single(result.Artifacts);
        Assert.Equal("handler.output", descriptor.Kind);
        var artifact = await fixture.ArtifactStore.ReadAsync(new ArtifactReadRequest(descriptor.ArtifactId, "conv-1", "binding-1"));
        Assert.True(artifact.Found);
        Assert.Equal("text/plain", artifact.MimeType);
    }

    [Fact]
    public async Task CapabilityCallLimitsAreEnforced()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MaxApiCalls = 1
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        await programmatic.math.double({ value: 1 });
                        await programmatic.math.double({ value: 2 });
                        return "unexpected";
                    } catch (error) {
                        return error.code;
                    }
                }
                """));

        Assert.Equal("capability_call_limit_exceeded", result.Result?.GetValue<string>());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "capability_call_limit_exceeded");
    }

    [Fact]
    public async Task RequestLimitsCanOnlyNarrowConfiguredDefaults()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MaxApiCalls = 5
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        await programmatic.math.double({ value: 1 });
                        await programmatic.math.double({ value: 2 });
                        return "unexpected";
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                MaxApiCalls: 1));

        Assert.Equal("capability_call_limit_exceeded", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task TimeoutsCancelLongRunningHostCalls()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddCapability<DelayInput, int>(
                    "runtime.wait",
                    capability => capability
                        .WithDescription("Waits for a delay.")
                        .UseWhen("You need to test timeouts.")
                        .DoNotUseWhen("You need fast results.")
                        .WithHandler(async (input, context) =>
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(input.DelayMs), context.CancellationToken);
                            return input.DelayMs;
                        })),
            options: new JintExecutorOptions
            {
                TimeoutMs = 50
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.runtime.wait({ delayMs: 5000 });
                }
                """,
                CallerBindingId: "caller-1"));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "timeout");

        var recoveryFixture = CreateFixture();
        var secondResult = await recoveryFixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.math.double({ value: 21 });
                }
                """,
                VisibleApiPaths: new[] { "math.double" }));
        Assert.Equal("42", CanonicalJson.Serialize(secondResult.Result));
    }

    [Fact]
    public async Task ExternalCancellationIsReportedSeparatelyFromTimeouts()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddCapability<DelayInput, int>(
                    "runtime.wait",
                    capability => capability
                        .WithDescription("Waits for a delay.")
                        .UseWhen("You need to test cancellation handling.")
                        .DoNotUseWhen("You need fast results.")
                        .WithHandler(async (input, context) =>
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(input.DelayMs), context.CancellationToken);
                            return input.DelayMs;
                        })),
            options: new JintExecutorOptions
            {
                TimeoutMs = 5_000
            });

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return await programmatic.runtime.wait({ delayMs: 5000 });
                }
                """),
            cancellationTokenSource.Token);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution_cancelled");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "timeout");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "execution_failed");
    }

    [Fact]
    public async Task StatementLimitsAreEnforced()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MaxStatements = 500
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    let value = 0;
                    while (true) {
                        value++;
                    }
                }
                """));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "statement_limit_exceeded");
    }

    [Fact]
    public async Task MemoryLimitsAreEnforced()
    {
        var fixture = CreateFixture(
            options: new JintExecutorOptions
            {
                MemoryBytes = 200_000,
                MaxStatements = 100_000
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    const values = [];
                    while (true) {
                        values.push("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
                    }
                }
                """));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "memory_limit_exceeded");
    }

    [Fact]
    public async Task OversizedApprovalPreviewPayloadsAreRejected()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddMutation<TaskMutationArgs, LargePreview, TaskMutationApplyResult>(
                    "tasks.largePreview",
                    mutation => mutation
                        .WithDescription("Creates a large preview payload.")
                        .UseWhen("You need to test preview payload caps.")
                        .DoNotUseWhen("You need compact previews.")
                        .WithPreviewHandler((args, _) => ValueTask.FromResult(new LargePreview(args.TaskId, new string('x', 256))))
                        .WithSummaryFactory((args, _, _) => ValueTask.FromResult($"Preview {args.TaskId}"))
                        .WithApplyHandler((args, _) => ValueTask.FromResult(MutationApplyResult<TaskMutationApplyResult>.Success(new TaskMutationApplyResult(args.TaskId, "done"))))),
            options: new JintExecutorOptions
            {
                MaxApprovalPayloadBytes = 128
            });

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.tasks.largePreview({ taskId: "task-1" });
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                CallerBindingId: "binding-1"));

        Assert.Equal("invalid_result_payload", result.Result?.GetValue<string>());
        Assert.Empty(result.ApprovalsRequested);
    }

    [Fact]
    public async Task SummaryGenerationFailuresAreReported()
    {
        var fixture = CreateFixture(
            configureBuilder: builder =>
                builder.AddMutation<TaskMutationArgs, TaskMutationPreview, TaskMutationApplyResult>(
                    "tasks.badSummary",
                    mutation => mutation
                        .WithDescription("Fails while building a summary.")
                        .UseWhen("You need to validate summary failure handling.")
                        .DoNotUseWhen("You need a successful preview.")
                        .WithPreviewHandler((args, _) => ValueTask.FromResult(new TaskMutationPreview(args.TaskId, true)))
                        .WithSummaryFactory((_, _, _) => throw new InvalidOperationException("summary failed"))
                        .WithApplyHandler((args, _) => ValueTask.FromResult(MutationApplyResult<TaskMutationApplyResult>.Success(new TaskMutationApplyResult(args.TaskId, "done"))))));

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    try {
                        return await programmatic.tasks.badSummary({ taskId: "task-1" });
                    } catch (error) {
                        return error.code;
                    }
                }
                """,
                CallerBindingId: "binding-1"));

        Assert.Equal("summary_generation_error", result.Result?.GetValue<string>());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "summary_generation_error");
        Assert.Empty(result.ApprovalsRequested);
    }

    [Fact]
    public async Task GeneratedTypeScriptNamesStayAlignedWithTheRuntimeNamespace()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    return {
                        doubleType: typeof programmatic.math.double,
                        completeType: typeof programmatic.tasks.complete
                    };
                }
                """));

        Assert.Contains("function double(input: MathDoubleInput): Promise<MathDoubleResult>;", fixture.Catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("function complete(input: TasksCompleteInput): Promise<TasksCompleteResult>;", fixture.Catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Equal("""{"completeType":"function","doubleType":"function"}""", CanonicalJson.Serialize(result.Result));
    }

    [Fact]
    public async Task ExecutionStatsIncludeStatementsAndConsoleCounters()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                """
                async function main() {
                    console.log("hello");
                    let total = 0;
                    for (let i = 0; i < 50; i++) {
                        total += i;
                    }
                    return total;
                }
                """));

        Assert.True(result.Stats.StatementsExecuted > 0);
        Assert.Equal(1, result.Stats.ConsoleLinesEmitted);
    }

    private static TestFixture CreateFixture(
        Action<ProgrammaticMcpBuilder>? configureBuilder = null,
        JintExecutorOptions? options = null)
    {
        var builder = new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<ValueInput, int>(
                "math.double",
                capability => capability
                    .WithDescription("Doubles a number.")
                    .UseWhen("You need a quick arithmetic result.")
                    .DoNotUseWhen("You need asynchronous work.")
                    .WithHandler((input, _) => ValueTask.FromResult(input.Value * 2)))
            .AddCapability<ValueInput, int>(
                "math.incrementAsync",
                capability => capability
                    .WithDescription("Increments a number asynchronously.")
                    .UseWhen("You need a delayed arithmetic result.")
                    .DoNotUseWhen("You need a synchronous result only.")
                    .WithHandler(async (input, context) =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), context.CancellationToken);
                        return input.Value + 1;
                    }))
            .AddCapability<EmptyInput, bool>(
                "throws.always",
                capability => capability
                    .WithDescription("Always throws.")
                    .UseWhen("You need to validate error handling.")
                    .DoNotUseWhen("You need a successful response.")
                    .WithHandler((_, _) => throw new InvalidOperationException("boom")))
            .AddCapability<ArtifactEmitInput, ArtifactEmitResult>(
                "artifacts.emit",
                capability => capability
                    .WithDescription("Writes an artifact.")
                    .UseWhen("You need named handler artifacts.")
                    .DoNotUseWhen("You only need inline JSON.")
                    .WithHandler(async (input, context) =>
                    {
                        var descriptor = await context.Artifacts!.WriteTextArtifactAsync("note.txt", input.Content, "text/plain", context.CancellationToken);
                        return new ArtifactEmitResult(descriptor.ArtifactId);
                    }))
            .AddMutation<TaskMutationArgs, TaskMutationPreview, TaskMutationApplyResult>(
                "tasks.complete",
                mutation => mutation
                    .WithDescription("Completes a task.")
                    .UseWhen("You need to preview a task mutation.")
                    .DoNotUseWhen("You are only reading task state.")
                    .WithPreviewHandler((args, _) => ValueTask.FromResult(new TaskMutationPreview(args.TaskId, true)))
                    .WithSummaryFactory((args, _, _) => ValueTask.FromResult($"Complete {args.TaskId}"))
                    .WithApplyHandler((args, _) => ValueTask.FromResult(MutationApplyResult<TaskMutationApplyResult>.Success(new TaskMutationApplyResult(args.TaskId, "done")))));

        configureBuilder?.Invoke(builder);

        var catalog = builder.BuildCatalog();
        var resolvedOptions = options ?? new JintExecutorOptions();
        var artifactStore = new InMemoryArtifactStore(resolvedOptions.ArtifactRetention);
        var approvalStore = new InMemoryApprovalStore();
        var executor = new JintCodeExecutor(catalog, resolvedOptions, artifactStore, approvalStore);
        return new TestFixture(catalog, executor, artifactStore, approvalStore);
    }

    private sealed record TestFixture(
        ProgrammaticCatalogSnapshot Catalog,
        JintCodeExecutor Executor,
        InMemoryArtifactStore ArtifactStore,
        InMemoryApprovalStore ApprovalStore);

    private sealed record ValueInput(int Value);

    private sealed record DelayInput(int DelayMs);

    private sealed record EmptyInput;

    private sealed record ArtifactEmitInput(string Content);

    private sealed record ArtifactEmitResult(string ArtifactId);

    private sealed record TaskMutationArgs(string TaskId);

    private sealed record TaskMutationPreview(string TaskId, bool WillComplete);

    private sealed record TaskMutationApplyResult(string TaskId, string Status);

    private sealed record LargePreview(string TaskId, string Payload);

    private sealed record SamplingClientState(
        bool PublicIsSupported,
        bool PublicSupportsToolUse,
        bool StructuredIsSupported,
        bool StructuredSupportsToolUse);

    private sealed class FakeSamplingClient : IProgrammaticSamplingClient
    {
        public bool IsSupported => true;

        public bool SupportsToolUse => false;

        public ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
        {
            var text = request.Messages.Single().Text;
            return ValueTask.FromResult(new ProgrammaticSamplingResult("sampled:" + text, "fake-model", "endTurn"));
        }
    }

    private sealed class ThrowingSamplingClient(string code) : IProgrammaticSamplingClient
    {
        public int CallCount { get; private set; }

        public bool IsSupported => true;

        public bool SupportsToolUse => false;

        public ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromException<ProgrammaticSamplingResult>(new ProgrammaticSamplingException(code, "The public sampling client should not have been used."));
        }
    }

    private sealed class ScriptedStructuredSamplingClient(
        IReadOnlyList<Func<ProgrammaticStructuredSamplingRequest, ProgrammaticStructuredSamplingResult>> responses) : IProgrammaticStructuredSamplingClient
    {
        private readonly Queue<Func<ProgrammaticStructuredSamplingRequest, ProgrammaticStructuredSamplingResult>> _responses = new(responses);

        public int CallCount { get; private set; }

        public bool IsSupported => true;

        public bool SupportsToolUse => true;

        public ValueTask<ProgrammaticStructuredSamplingResult> CreateMessageAsync(
            ProgrammaticStructuredSamplingRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_responses.Count == 0)
            {
                throw new Xunit.Sdk.XunitException("No scripted structured sampling response remained.");
            }

            return ValueTask.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class DualInterfaceSamplingClient(
        IReadOnlyList<Func<ProgrammaticStructuredSamplingRequest, ProgrammaticStructuredSamplingResult>> responses) : IProgrammaticSamplingClient, IProgrammaticStructuredSamplingClient
    {
        private readonly Queue<Func<ProgrammaticStructuredSamplingRequest, ProgrammaticStructuredSamplingResult>> _responses = new(responses);

        public int PublicCallCount { get; private set; }

        public int StructuredCallCount { get; private set; }

        public bool IsSupported => true;

        public bool SupportsToolUse => true;

        public ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
        {
            PublicCallCount++;
            return ValueTask.FromException<ProgrammaticSamplingResult>(
                new ProgrammaticSamplingException("public_client_used", "The public sampling interface should not have been used for tool-enabled JS sampling."));
        }

        public ValueTask<ProgrammaticStructuredSamplingResult> CreateMessageAsync(
            ProgrammaticStructuredSamplingRequest request,
            CancellationToken cancellationToken = default)
        {
            StructuredCallCount++;
            if (_responses.Count == 0)
            {
                throw new Xunit.Sdk.XunitException("No dual-interface structured sampling response remained.");
            }

            return ValueTask.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class FakeSamplingToolRegistry : IProgrammaticSamplingToolRegistry
    {
        private static readonly ProgrammaticStructuredSamplingToolDefinition LookupTool = new(
            "lookup",
            "Looks up a canned response.",
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("name")
            });

        public IReadOnlyList<ProgrammaticStructuredSamplingToolDefinition> Tools { get; } = [LookupTool];

        public bool TryGetDefinition(string name, out ProgrammaticStructuredSamplingToolDefinition definition)
        {
            if (string.Equals(name, LookupTool.Name, StringComparison.Ordinal))
            {
                definition = LookupTool;
                return true;
            }

            definition = null!;
            return false;
        }

        public ValueTask<ProgrammaticSamplingToolExecutionResult> InvokeAsync(
            string name,
            JsonObject arguments,
            ProgrammaticSamplingToolContext context,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("lookup", name);
            Assert.Equal("world", arguments["name"]?.GetValue<string>());
            return ValueTask.FromResult(
                new ProgrammaticSamplingToolExecutionResult(
                    """{"result":"world"}""",
                    new JsonObject { ["result"] = "world" }));
        }
    }

    private sealed class SamplingServiceProvider(
        IProgrammaticSamplingClient? publicClient = null,
        IProgrammaticStructuredSamplingClient? structuredClient = null,
        IProgrammaticSamplingToolRegistry? samplingTools = null) : IServiceProvider, IServiceScopeFactory, IDisposable
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceProvider))
            {
                return this;
            }

            if (serviceType == typeof(IServiceScopeFactory))
            {
                return this;
            }

            if (serviceType == typeof(IProgrammaticSamplingClient))
            {
                return publicClient;
            }

            if (serviceType == typeof(IProgrammaticStructuredSamplingClient))
            {
                return structuredClient;
            }

            if (serviceType == typeof(IProgrammaticSamplingToolRegistry))
            {
                return samplingTools;
            }

            return null;
        }

        public IServiceScope CreateScope() => new SamplingServiceScope(this);

        public void Dispose()
        {
        }

        private sealed class SamplingServiceScope(IServiceProvider services) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = services;

            public void Dispose()
            {
            }
        }
    }
}
