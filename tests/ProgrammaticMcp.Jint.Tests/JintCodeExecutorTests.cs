using System.Text.Json.Nodes;
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
                    Args: JsonNode.Parse("""{"value":"123456789"}"""))));
    }

    [Fact]
    public async Task InvalidEntrypointArgsProduceStructuredDiagnostics()
    {
        var fixture = CreateFixture();

        var result = await fixture.Executor.ExecuteAsync(
            new CodeExecutionRequest(
                "conv-1",
                "async function main(input) { return input; }",
                Args: JsonNode.Parse("""["not","an","object"]""")));

        Assert.Null(result.Result);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "invalid_entrypoint_args");
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
                    console.warn(Symbol('x'));
                    console.error(cycle);
                    return null;
                }
                """));

        Assert.Equal("""{"a":1,"b":2}""", result.Console[0].Message);
        Assert.Equal("Symbol(x)", result.Console[1].Message);
        Assert.Equal("[Unserializable]", result.Console[2].Message);
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
                """));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "timeout");

        var secondResult = await fixture.Executor.ExecuteAsync(
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
}
