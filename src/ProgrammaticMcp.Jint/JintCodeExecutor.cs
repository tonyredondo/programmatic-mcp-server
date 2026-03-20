using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jint;
using Jint.Constraints;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace ProgrammaticMcp.Jint;

/// <summary>
/// Executes generated programmatic code inside a constrained Jint runtime.
/// </summary>
public sealed class JintCodeExecutor : ICodeExecutor
{
    private static readonly Regex EntrypointSegmentExpression =
        new("^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ICapabilityCatalog _catalog;
    private readonly JintExecutorOptions _options;
    private readonly IArtifactStore _artifactStore;
    private readonly IApprovalStore _approvalStore;

    /// <summary>
    /// Creates a new executor instance.
    /// </summary>
    /// <param name="catalog">The capability catalog used to build the generated namespace.</param>
    /// <param name="options">Optional runtime limits. When omitted, the default limits are used.</param>
    /// <param name="artifactStore">Optional artifact store used for spill and handler-created artifacts.</param>
    /// <param name="approvalStore">Optional approval store used for mutation previews and state transitions.</param>
    public JintCodeExecutor(
        ICapabilityCatalog catalog,
        JintExecutorOptions? options = null,
        IArtifactStore? artifactStore = null,
        IApprovalStore? approvalStore = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? new JintExecutorOptions();
        _options.Validate();
        _artifactStore = artifactStore ?? new InMemoryArtifactStore(_options.ArtifactRetention);
        _approvalStore = approvalStore ?? new InMemoryApprovalStore();
    }

    /// <summary>
    /// Executes the supplied code against the generated programmatic namespace.
    /// </summary>
    /// <param name="request">The execution request to run.</param>
    /// <param name="cancellationToken">The cancellation token that stops execution.</param>
    /// <returns>A structured execution result containing the returned value, diagnostics, and artifacts.</returns>
    public async ValueTask<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConversationIdValidator.EnsureValid(request.ConversationId);

        var stopwatch = Stopwatch.StartNew();
        var limits = _options.Resolve(request);
        limits.ValidatePayloadBounds(request);

        if (!TryValidateEntrypoint(request.Entrypoint, out var invalidEntrypointMessage))
        {
            stopwatch.Stop();
            return CreateResult(
                request,
                result: null,
                console: Array.Empty<ExecutionConsoleEntry>(),
                diagnostics: new[] { new ExecutionDiagnostic("invalid_entrypoint", invalidEntrypointMessage!) },
                artifacts: Array.Empty<ExecutionArtifactDescriptor>(),
                approvalsRequested: Array.Empty<MutationPreviewEnvelope>(),
                resultArtifactId: null,
                effectiveVisibleApiPaths: null,
                stopwatch.Elapsed,
                apiCalls: 0,
                consoleLinesEmitted: 0,
                runtimeMetrics: new RuntimeMetrics(0, 0));
        }

        var visibleCapabilities = ResolveVisibleCapabilities(request.VisibleApiPaths);
        var callerBindingId = string.IsNullOrWhiteSpace(request.CallerBindingId) ? null : request.CallerBindingId;
        var diagnostics = new List<ExecutionDiagnostic>();
        var services = request.Services ?? EmptyServiceProvider.Instance;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(limits.TimeoutMs);
        using var bridge = new CatalogBridge(
            _catalog,
            visibleCapabilities,
            request,
            services,
            limits,
            callerBindingId,
            _artifactStore,
            _approvalStore,
            diagnostics,
            cancellationToken);
        bridge.ExecutionCancellationToken = timeoutCts.Token;
        using var engine = CreateEngine(bridge, limits, timeoutCts.Token);
        var metricsCollector = RuntimeMetricsCollector.Create(engine);

        try
        {
            engine.SetValue("__pmInvoke", new Func<string, object?, Task<HostCallResponse>>(bridge.InvokeAsync));
            engine.SetValue("__pmConsole", new Action<string, string>(bridge.CaptureConsole));
            engine.SetValue("__pmSample", new Func<object?, Task<HostCallResponse>>(bridge.SampleAsync));
            await engine.ExecuteAsync(CreateBootstrapScript(_catalog.Capabilities, visibleCapabilities, _catalog.CapabilityVersion), cancellationToken: timeoutCts.Token);
            await engine.ExecuteAsync(request.Code, cancellationToken: timeoutCts.Token);

            engine.SetValue("__pmEntrypointArgs", JintJsonSerializer.NodeToClrObject(request.Args ?? new JsonObject()));
            var pending = await engine.EvaluateAsync(CreateEntrypointWrapper(request.Entrypoint), cancellationToken: timeoutCts.Token);
            var settled = await pending.UnwrapIfPromiseAsync(timeoutCts.Token);
            var responseNode = JintJsonSerializer.SerializeToNode(settled.ToObject());
            metricsCollector.Sample();
            return await FinalizeExecutionAsync(request, bridge, diagnostics, responseNode, stopwatch, metricsCollector.Snapshot());
        }
        catch (Exception exception)
        {
            metricsCollector.Sample();
            AddMappedFailureDiagnostic(diagnostics, exception, request, timeoutCts, cancellationToken);
            return await FinalizeExecutionAsync(request, bridge, diagnostics, responseNode: null, stopwatch, metricsCollector.Snapshot());
        }
    }

    private async ValueTask<CodeExecutionResult> FinalizeExecutionAsync(
        CodeExecutionRequest request,
        CatalogBridge bridge,
        List<ExecutionDiagnostic> diagnostics,
        JsonNode? responseNode,
        Stopwatch stopwatch,
        RuntimeMetrics runtimeMetrics)
    {
        JsonNode? result = null;
        string? resultArtifactId = null;
        if (responseNode is JsonObject responseObject)
        {
            var success = responseObject["success"]?.GetValue<bool>() ?? false;
            if (success)
            {
                var hasValue = responseObject["hasValue"]?.GetValue<bool>() ?? false;
                if (!hasValue)
                {
                    diagnostics.Add(new ExecutionDiagnostic("invalid_result_payload", "Entrypoint returned undefined."));
                }
                else
                {
                    result = responseObject["value"]?.DeepClone();
                }
            }
            else if (responseObject["error"] is JsonObject errorObject)
            {
                var code = errorObject["code"]?.GetValue<string>() ?? "execution_failed";
                var message = errorObject["message"]?.GetValue<string>() ?? "Execution failed.";
                var data = errorObject["data"] as JsonObject;

                if (!diagnostics.Any(diagnostic =>
                        diagnostic.Code == code
                        && diagnostic.Message == message))
                {
                    diagnostics.Add(new ExecutionDiagnostic(code, message, data?.DeepClone().AsObject()));
                }

                if (code is not "entrypoint_not_found"
                    and not "invalid_entrypoint"
                    and not "timeout"
                    and not "execution_cancelled")
                {
                    diagnostics.Add(
                        new ExecutionDiagnostic(
                            "execution_failed",
                            "Execution failed.",
                            new JsonObject
                            {
                                ["causeCode"] = code
                            }));
                }
            }
        }

        if (result is not null)
        {
            var canonical = CanonicalJson.Serialize(result);
            var canonicalBytes = Encoding.UTF8.GetByteCount(canonical);
            if (canonicalBytes > bridge.Limits.MaxResultBytes)
            {
                if (bridge.CallerBindingId is null)
                {
                    result = null;
                    diagnostics.Add(
                        new ExecutionDiagnostic(
                            "result_too_large",
                            "The execution result exceeded the inline result limit.",
                            new JsonObject
                            {
                                ["inlineLimitBytes"] = bridge.Limits.MaxResultBytes,
                                ["resultBytes"] = canonicalBytes
                            }));
                    diagnostics.Add(
                        new ExecutionDiagnostic(
                            "artifact_continuity_unavailable",
                            "Result spill requires a stable caller binding.",
                            new JsonObject
                            {
                                ["reason"] = "missing_caller_binding",
                                ["blockedOperation"] = "result_spill"
                            }));
                }
                else
                {
                    var descriptor = await bridge.ArtifactWriter.WriteCanonicalJsonArtifactAsync(
                        "result.json",
                        "execution.result",
                        result,
                        bridge.ExecutionCancellationToken);
                    resultArtifactId = descriptor.ArtifactId;
                    result = null;
                    diagnostics.Add(
                        new ExecutionDiagnostic(
                            "result_spilled_to_artifact",
                            "The execution result was written to an artifact because it exceeded the inline limit.",
                            new JsonObject
                            {
                                ["artifactId"] = descriptor.ArtifactId,
                                ["inlineLimitBytes"] = bridge.Limits.MaxResultBytes
                            }));
                }
            }
        }

        stopwatch.Stop();
        return CreateResult(
            request,
            result,
            bridge.ConsoleEntries,
            diagnostics,
            bridge.Artifacts,
            bridge.ApprovalsRequested,
            resultArtifactId,
            request.VisibleApiPaths is null ? null : bridge.VisibleCapabilities.Select(static capability => capability.ApiPath).ToArray(),
            stopwatch.Elapsed,
            bridge.ApiCallCount,
            bridge.ConsoleLinesEmitted,
            runtimeMetrics);
    }

    private CodeExecutionResult CreateResult(
        CodeExecutionRequest request,
        JsonNode? result,
        IReadOnlyList<ExecutionConsoleEntry> console,
        IReadOnlyList<ExecutionDiagnostic> diagnostics,
        IReadOnlyList<ExecutionArtifactDescriptor> artifacts,
        IReadOnlyList<MutationPreviewEnvelope> approvalsRequested,
        string? resultArtifactId,
        IReadOnlyList<string>? effectiveVisibleApiPaths,
        TimeSpan elapsed,
        int apiCalls,
        int consoleLinesEmitted,
        RuntimeMetrics runtimeMetrics)
    {
        return new CodeExecutionResult(
            ProgrammaticContractConstants.SchemaVersion,
            _catalog.CapabilityVersion,
            result,
            console.ToArray(),
            diagnostics.ToArray(),
            artifacts.ToArray(),
            approvalsRequested.ToArray(),
            resultArtifactId,
            effectiveVisibleApiPaths,
            new ExecutionStats(
                ApiCalls: apiCalls,
                ElapsedMs: (int)Math.Ceiling(elapsed.TotalMilliseconds),
                StatementsExecuted: runtimeMetrics.StatementsExecuted,
                PeakMemoryBytes: runtimeMetrics.PeakMemoryBytes,
                ConsoleLinesEmitted: consoleLinesEmitted));
    }

    private Engine CreateEngine(CatalogBridge bridge, EffectiveExecutionLimits limits, CancellationToken cancellationToken)
    {
        return new Engine(
            options =>
            {
                options.Debugger.Enabled = true;
                options.Debugger.InitialStepMode = global::Jint.Runtime.Debugger.StepMode.Into;
                options.Debugger.StatementHandling = global::Jint.Runtime.Debugger.DebuggerStatementHandling.Script;
                options.TimeoutInterval(TimeSpan.FromMilliseconds(limits.TimeoutMs));
                options.MaxStatements(limits.MaxStatements);
                options.LimitMemory(limits.MemoryBytes);
                options.CancellationToken(cancellationToken);
            });
    }

    private IReadOnlyList<CapabilityDefinition> ResolveVisibleCapabilities(IReadOnlyList<string>? visibleApiPaths)
    {
        if (visibleApiPaths is null)
        {
            return _catalog.Capabilities;
        }

        var byPath = _catalog.Capabilities.ToDictionary(static capability => capability.ApiPath, StringComparer.Ordinal);
        var results = new List<CapabilityDefinition>(visibleApiPaths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var apiPath in visibleApiPaths)
        {
            if (!seen.Add(apiPath))
            {
                continue;
            }

            if (!byPath.TryGetValue(apiPath, out var capability))
            {
                throw new ArgumentException($"Visible API path '{apiPath}' is not registered.", nameof(visibleApiPaths));
            }

            results.Add(capability);
        }

        return results;
    }

    private static bool TryValidateEntrypoint(string entrypoint, out string? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(entrypoint))
        {
            message = "Entrypoint must be present.";
            return false;
        }

        foreach (var segment in entrypoint.Split('.'))
        {
            if (!EntrypointSegmentExpression.IsMatch(segment))
            {
                message = $"Entrypoint '{entrypoint}' contains an invalid identifier segment.";
                return false;
            }
        }

        return true;
    }

    private static string CreateBootstrapScript(
        IReadOnlyList<CapabilityDefinition> allCapabilities,
        IReadOnlyList<CapabilityDefinition> visibleCapabilities,
        string capabilityVersion)
    {
        var hiddenChildrenByNamespace = BuildHiddenChildrenMap(allCapabilities, visibleCapabilities);
        var builder = new StringBuilder();
        builder.AppendLine("(() => {");
        builder.AppendLine("  const hostInvoke = globalThis.__pmInvoke;");
        builder.AppendLine("  const hostConsole = globalThis.__pmConsole;");
        builder.AppendLine("  const hostSample = globalThis.__pmSample;");
        builder.AppendLine("  delete globalThis.__pmInvoke;");
        builder.AppendLine("  delete globalThis.__pmConsole;");
        builder.AppendLine("  delete globalThis.__pmSample;");
        builder.AppendLine("  const root = globalThis.programmatic = globalThis.programmatic || {};");
        builder.AppendLine($"  root.__meta = {{ capabilityVersion: {JsonSerializer.Serialize(capabilityVersion)}, runtimeContractVersion: {JsonSerializer.Serialize(ProgrammaticContractConstants.GeneratedRuntimeContractVersion)} }};");
        builder.AppendLine("  root.client = root.client || {};");
        builder.AppendLine($"  const hiddenChildren = {JsonSerializer.Serialize(hiddenChildrenByNamespace)};");
        builder.AppendLine("  const stableStringify = (value, seen = new Set()) => {");
        builder.AppendLine("    if (value === null) return 'null';");
        builder.AppendLine("    const kind = typeof value;");
        builder.AppendLine("    if (kind === 'string') return JSON.stringify(value);");
        builder.AppendLine("    if (kind === 'number') return Number.isFinite(value) ? JSON.stringify(value) : String(value);");
        builder.AppendLine("    if (kind === 'boolean') return JSON.stringify(value);");
        builder.AppendLine("    if (kind === 'undefined' || kind === 'bigint' || kind === 'symbol' || kind === 'function') {");
        builder.AppendLine("      try { return String(value); } catch { return '[Unserializable]'; }");
        builder.AppendLine("    }");
        builder.AppendLine("    try { JSON.stringify(value); } catch { return '[Unserializable]'; }");
        builder.AppendLine("    if (seen.has(value)) return '[Unserializable]';");
        builder.AppendLine("    seen.add(value);");
        builder.AppendLine("    if (Array.isArray(value)) {");
        builder.AppendLine("      return '[' + value.map(item => stableStringify(item, seen)).join(',') + ']';");
        builder.AppendLine("    }");
        builder.AppendLine("    const keys = Object.keys(value).sort();");
        builder.AppendLine("    return '{' + keys.map(key => JSON.stringify(key) + ':' + stableStringify(value[key], seen)).join(',') + '}';");
        builder.AppendLine("  };");
        builder.AppendLine("  globalThis.console = {");
        builder.AppendLine("    log: (...args) => hostConsole('info', args.map(value => stableStringify(value)).join(' ')),");
        builder.AppendLine("    warn: (...args) => hostConsole('warn', args.map(value => stableStringify(value)).join(' ')),");
        builder.AppendLine("    error: (...args) => hostConsole('error', args.map(value => stableStringify(value)).join(' '))");
        builder.AppendLine("  };");
        builder.AppendLine("  const makeMissingCallable = (path) => new Proxy(async function(input = {}) {");
        builder.AppendLine("    const response = await hostInvoke(path, input);");
        builder.AppendLine("    if (!response.success) { throw response.error; }");
        builder.AppendLine("    return response.value;");
        builder.AppendLine("  }, {");
        builder.AppendLine("    get(_target, prop) {");
        builder.AppendLine("      if (typeof prop !== 'string' || prop === 'then' || prop === 'catch' || prop === 'finally') { return undefined; }");
        builder.AppendLine("      return makeMissingCallable(`${path}.${prop}`);");
        builder.AppendLine("    }");
        builder.AppendLine("  });");
        builder.AppendLine("  const wrapNamespace = (path, target) => new Proxy(target, {");
        builder.AppendLine("    get(obj, prop) {");
        builder.AppendLine("      if (typeof prop !== 'string') { return Reflect.get(obj, prop); }");
        builder.AppendLine("      if (Object.prototype.hasOwnProperty.call(obj, prop)) { return obj[prop]; }");
        builder.AppendLine("      if (prop === '__meta' || prop === '__internal') { return undefined; }");
        builder.AppendLine("      const hidden = hiddenChildren[path] || [];");
        builder.AppendLine("      if (hidden.includes(prop)) { return undefined; }");
        builder.AppendLine("      const nextPath = path ? `${path}.${prop}` : prop;");
        builder.AppendLine("      return makeMissingCallable(nextPath);");
        builder.AppendLine("    }");
        builder.AppendLine("  });");

        foreach (var capability in visibleCapabilities)
        {
            var segments = ApiPathUtilities.SplitAndValidate(capability.ApiPath);
            var currentPath = "root";
            for (var index = 0; index < segments.Count - 1; index++)
            {
                currentPath += $".{segments[index]}";
                builder.AppendLine($"  {currentPath} = {currentPath} || {{}};");
            }

            var parent = segments.Count == 1 ? "root" : $"root.{string.Join('.', segments.Take(segments.Count - 1))}";
            builder.AppendLine($"  {parent}.{segments[^1]} = async function(input = {{}}) {{");
            builder.AppendLine($"    const response = await hostInvoke({JsonSerializer.Serialize(capability.ApiPath)}, input);");
            builder.AppendLine("    if (!response.success) { throw response.error; }");
            builder.AppendLine("    return response.value;");
            builder.AppendLine("  };");
        }

        builder.AppendLine("  root.client.sample = async function(request = {}) {");
        builder.AppendLine("    const response = await hostSample(request);");
        builder.AppendLine("    if (!response.success) { throw response.error; }");
        builder.AppendLine("    return response.value;");
        builder.AppendLine("  };");
        builder.AppendLine("  const applyNamespaceProxies = (path, target) => {");
        builder.AppendLine("    for (const key of Object.keys(target)) {");
        builder.AppendLine("      const value = target[key];");
        builder.AppendLine("      if (key === '__meta') { continue; }");
        builder.AppendLine("      if (value && typeof value === 'object' && !Array.isArray(value)) {");
        builder.AppendLine("        const childPath = path ? `${path}.${key}` : key;");
        builder.AppendLine("        target[key] = applyNamespaceProxies(childPath, value);");
        builder.AppendLine("      }");
        builder.AppendLine("    }");
        builder.AppendLine("    return wrapNamespace(path, target);");
        builder.AppendLine("  };");
        builder.AppendLine("  globalThis.programmatic = applyNamespaceProxies('', root);");
        builder.AppendLine("})();");
        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildHiddenChildrenMap(
        IReadOnlyList<CapabilityDefinition> allCapabilities,
        IReadOnlyList<CapabilityDefinition> visibleCapabilities)
    {
        static Dictionary<string, HashSet<string>> BuildChildren(IEnumerable<CapabilityDefinition> capabilities)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                [string.Empty] = new(StringComparer.Ordinal)
            };

            foreach (var capability in capabilities)
            {
                var segments = ApiPathUtilities.SplitAndValidate(capability.ApiPath);
                for (var index = 0; index < segments.Count; index++)
                {
                    var parent = index == 0 ? string.Empty : string.Join('.', segments.Take(index));
                    if (!result.TryGetValue(parent, out var children))
                    {
                        children = new HashSet<string>(StringComparer.Ordinal);
                        result[parent] = children;
                    }

                    children.Add(segments[index]);
                }
            }

            return result;
        }

        var fullChildren = BuildChildren(allCapabilities);
        var visibleChildren = BuildChildren(visibleCapabilities);
        var hiddenChildren = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var parent in fullChildren.Keys)
        {
            visibleChildren.TryGetValue(parent, out var visibleSet);
            var hidden = fullChildren[parent]
                .Where(child => visibleSet is null || !visibleSet.Contains(child))
                .OrderBy(static child => child, StringComparer.Ordinal)
                .ToArray();
            hiddenChildren[parent] = hidden;
        }

        return hiddenChildren;
    }

    private static string CreateEntrypointWrapper(string entrypoint)
    {
        return
            $$"""
            (async () => {
              const normalizeError = (error) => {
                if (error && typeof error === 'object') {
                  return {
                    code: typeof error.code === 'string' ? error.code : 'execution_failed',
                    message: typeof error.message === 'string' ? error.message : String(error),
                    capabilityPath: typeof error.capabilityPath === 'string' ? error.capabilityPath : null,
                    data: error.data ?? null
                  };
                }

                return {
                  code: 'execution_failed',
                  message: String(error),
                  capabilityPath: null,
                  data: null
                };
              };

              let current = globalThis;
              for (const segment of {{JsonSerializer.Serialize(entrypoint.Split('.'))}}) {
                if (typeof current === 'undefined' || current === null || !(segment in current)) {
                  return {
                    success: false,
                    error: {
                      code: 'entrypoint_not_found',
                      message: 'Entrypoint {{entrypoint}} was not found.',
                      data: { entrypoint: '{{entrypoint}}' }
                    }
                  };
                }

                current = current[segment];
              }

              if (typeof current !== 'function') {
                return {
                  success: false,
                  error: {
                    code: 'invalid_entrypoint',
                    message: 'Entrypoint {{entrypoint}} is not a function.',
                    data: { entrypoint: '{{entrypoint}}' }
                  }
                };
              }

              try {
                const value = await current(globalThis.__pmEntrypointArgs);
                return {
                  success: true,
                  hasValue: typeof value !== 'undefined',
                  value: typeof value === 'undefined' ? null : value
                };
              } catch (error) {
                return { success: false, error: normalizeError(error) };
              } finally {
                delete globalThis.__pmEntrypointArgs;
              }
            })()
            """;
    }

    private static void AddMappedFailureDiagnostic(
        ICollection<ExecutionDiagnostic> diagnostics,
        Exception exception,
        CodeExecutionRequest request,
        CancellationTokenSource timeoutCts,
        CancellationToken requestCancellationToken)
    {
        if (TryMapSyntaxError(exception, out var syntaxDiagnostic))
        {
            diagnostics.Add(syntaxDiagnostic!);
            return;
        }

        if (exception is StatementsCountOverflowException)
        {
            diagnostics.Add(new ExecutionDiagnostic("statement_limit_exceeded", "Execution exceeded the maximum statement count."));
            return;
        }

        if (exception is MemoryLimitExceededException)
        {
            diagnostics.Add(new ExecutionDiagnostic("memory_limit_exceeded", "Execution exceeded the memory limit."));
            return;
        }

        if (exception is OperationCanceledException or ExecutionCanceledException)
        {
            if (!requestCancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                diagnostics.Add(new ExecutionDiagnostic("timeout", "Execution exceeded the configured timeout."));
            }
            else
            {
                diagnostics.Add(
                    new ExecutionDiagnostic(
                        "execution_cancelled",
                        "Execution was cancelled.",
                        new JsonObject { ["reason"] = "cancelled" }));
            }

            return;
        }

        diagnostics.Add(
            new ExecutionDiagnostic(
                "execution_failed",
                exception.Message,
                new JsonObject
                {
                    ["exceptionType"] = exception.GetType().FullName
                }));
    }

    private static bool TryMapSyntaxError(Exception exception, out ExecutionDiagnostic? diagnostic)
    {
        if (exception.GetType().FullName == "Esprima.ParserException")
        {
            diagnostic = new ExecutionDiagnostic(
                "syntax_error",
                exception.Message,
                new JsonObject
                {
                    ["line"] = ReadNullableInt(exception, "LineNumber"),
                    ["column"] = ReadNullableInt(exception, "Column")
                });
            return true;
        }

        if (exception is JavaScriptException javaScriptException)
        {
            var match = Regex.Match(javaScriptException.Message, @"^(?<description>.+?) \((?:<anonymous>|anonymous):(?<line>\d+):(?<column>\d+)\)");
            if (match.Success)
            {
                diagnostic = new ExecutionDiagnostic(
                    "syntax_error",
                    match.Groups["description"].Value,
                    new JsonObject
                    {
                        ["line"] = int.Parse(match.Groups["line"].Value),
                        ["column"] = int.Parse(match.Groups["column"].Value)
                    });
                return true;
            }
        }

        if (exception.InnerException is not null)
        {
            return TryMapSyntaxError(exception.InnerException, out diagnostic);
        }

        diagnostic = null;
        return false;
    }

    private static int? ReadNullableInt(Exception exception, string propertyName)
    {
        var value = exception.GetType().GetProperty(propertyName)?.GetValue(exception);
        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            _ => null
        };
    }

    private sealed class CatalogBridge : IDisposable
    {
        private readonly Dictionary<string, CapabilityDefinition> _capabilitiesByPath;
        private readonly HashSet<string> _visiblePathSet;
        private readonly List<ExecutionDiagnostic> _diagnostics;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<ExecutionConsoleEntry> _console = new();
        private readonly List<ExecutionArtifactDescriptor> _artifacts = new();
        private readonly List<MutationPreviewEnvelope> _approvalsRequested = new();
        private readonly ICapabilityCatalog _catalog;
        private readonly CodeExecutionRequest _request;
        private readonly IServiceProvider _services;
        private readonly IServiceProvider _capabilityServices;
        private readonly IServiceProvider _mutationServices;
        private readonly IProgrammaticSamplingClient _executionSamplingClient;
        private readonly IProgrammaticStructuredSamplingClient _executionStructuredSamplingClient;
        private readonly CancellationToken _requestCancellationToken;
        private int _apiCallCount;
        private int _consoleBytesEmitted;
        private bool _consoleTruncated;

        public CatalogBridge(
            ICapabilityCatalog catalog,
            IReadOnlyList<CapabilityDefinition> visibleCapabilities,
            CodeExecutionRequest request,
            IServiceProvider services,
            EffectiveExecutionLimits limits,
            string? callerBindingId,
            IArtifactStore artifactStore,
            IApprovalStore approvalStore,
            List<ExecutionDiagnostic> diagnostics,
            CancellationToken requestCancellationToken)
        {
            _catalog = catalog;
            _request = request;
            _services = services;
            _requestCancellationToken = requestCancellationToken;
            Limits = limits;
            CallerBindingId = callerBindingId;
            ApprovalStore = approvalStore;
            _diagnostics = diagnostics;
            VisibleCapabilities = visibleCapabilities;
            _capabilitiesByPath = visibleCapabilities.ToDictionary(static capability => capability.ApiPath, StringComparer.Ordinal);
            _visiblePathSet = _capabilitiesByPath.Keys.ToHashSet(StringComparer.Ordinal);
            (_executionSamplingClient, _executionStructuredSamplingClient) = CreateExecutionSamplingClients(
                services,
                request.VisibleApiPaths,
                visibleCapabilities,
                request.ConversationId,
                callerBindingId);
            _capabilityServices = SamplingServiceOverlay.Create(services, _executionSamplingClient, _executionStructuredSamplingClient);
            var blockedMutationClient = FixedProgrammaticSamplingClient.Blocked(
                "sampling_not_allowed_in_mutation_context",
                "Sampling is not allowed while previewing or applying mutations.");
            _mutationServices = SamplingServiceOverlay.Create(services, blockedMutationClient, blockedMutationClient);
            ArtifactWriter = new ExecutionArtifactWriter(
                artifactStore,
                request.ConversationId,
                callerBindingId,
                limits.ArtifactRetention,
                _artifacts);
            ExecutionCancellationToken = CancellationToken.None;
        }

        public IReadOnlyList<CapabilityDefinition> VisibleCapabilities { get; }

        public EffectiveExecutionLimits Limits { get; }

        public string? CallerBindingId { get; }

        public IApprovalStore ApprovalStore { get; }

        public ExecutionArtifactWriter ArtifactWriter { get; }

        public CancellationToken ExecutionCancellationToken { get; set; }

        public IReadOnlyList<ExecutionConsoleEntry> ConsoleEntries => _console;

        public IReadOnlyList<ExecutionArtifactDescriptor> Artifacts => _artifacts;

        public IReadOnlyList<MutationPreviewEnvelope> ApprovalsRequested => _approvalsRequested;

        public int ApiCallCount => _apiCallCount;

        public int ConsoleLinesEmitted { get; private set; }

        public async Task<HostCallResponse> InvokeAsync(string apiPath, object? argument)
        {
            await _gate.WaitAsync(ExecutionCancellationToken);
            try
            {
                var nextCallCount = ++_apiCallCount;
                if (nextCallCount > Limits.MaxApiCalls)
                {
                    return Fail(
                        new StructuredBridgeException(
                            "capability_call_limit_exceeded",
                            "Execution exceeded the maximum capability call count.",
                            new JsonObject { ["maxApiCalls"] = Limits.MaxApiCalls }));
                }

                if (!_capabilitiesByPath.TryGetValue(apiPath, out var capability))
                {
                    return Fail(
                        new UnknownCapabilityException(
                            apiPath,
                            Suggest(apiPath)));
                }

                var inputNode = ConvertBridgeArgument(argument);
                if (inputNode is not JsonObject inputObject)
                {
                    return Fail(
                        new StructuredBridgeException(
                            capability.IsMutation ? "invalid_mutation_args" : "invalid_capability_input",
                            capability.IsMutation
                                ? $"Mutation '{apiPath}' requires a JSON object argument."
                                : $"Capability '{apiPath}' requires a JSON object argument.",
                            new JsonObject { ["capabilityPath"] = apiPath }));
                }

                return capability.IsMutation
                    ? await InvokeMutationAsync(capability, inputObject)
                    : await InvokeCapabilityAsync(capability, inputObject);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<HostCallResponse> SampleAsync(object? argument)
        {
            JsonNode? requestNode;
            try
            {
                requestNode = ConvertBridgeArgument(argument);
            }
            catch (StructuredBridgeException exception)
            {
                _diagnostics.Add(new ExecutionDiagnostic(exception.Code, exception.Message, exception.Data));
                return HostCallResponse.FromFailure(exception.ToJsError());
            }

            if (requestNode is not JsonObject requestObject)
            {
                var exception = new StructuredBridgeException("invalid_params", "Sampling request must be a JSON object.");
                _diagnostics.Add(new ExecutionDiagnostic(exception.Code, exception.Message, exception.Data));
                return HostCallResponse.FromFailure(exception.ToJsError());
            }

            ProgrammaticSamplingRequest request;
            try
            {
                request = JsonSerializerContract.DeserializeFromNode<ProgrammaticSamplingRequest>(requestObject);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or NotSupportedException)
            {
                var structured = new StructuredBridgeException("invalid_params", "Sampling request could not be deserialized.");
                _diagnostics.Add(new ExecutionDiagnostic(structured.Code, structured.Message, structured.Data));
                return HostCallResponse.FromFailure(structured.ToJsError());
            }

            try
            {
                var result = request.EnableTools
                    ? await CreateToolEnabledSamplingResultAsync(request)
                    : await _executionSamplingClient.CreateMessageAsync(request, ExecutionCancellationToken);
                return HostCallResponse.FromSuccess(result.Text);
            }
            catch (ProgrammaticSamplingException exception)
            {
                _diagnostics.Add(new ExecutionDiagnostic(exception.Code, exception.Message, exception.Data));
                return HostCallResponse.FromFailure(
                    new JsonObject
                    {
                        ["code"] = exception.Code,
                        ["message"] = exception.Message,
                        ["capabilityPath"] = null,
                        ["data"] = exception.Data?.DeepClone()
                    });
            }
            catch (OperationCanceledException)
            {
                var cancelledByCaller = _requestCancellationToken.IsCancellationRequested;
                var exception = new StructuredBridgeException(
                    cancelledByCaller ? "execution_cancelled" : "timeout",
                    cancelledByCaller ? "Execution was cancelled." : "Execution exceeded the configured timeout.",
                    new JsonObject
                    {
                        ["reason"] = cancelledByCaller ? "cancelled" : "timeout"
                    });
                _diagnostics.Add(new ExecutionDiagnostic(exception.Code, exception.Message, exception.Data));
                return HostCallResponse.FromFailure(exception.ToJsError());
            }
        }

        public void CaptureConsole(string level, string message)
        {
            ConsoleLinesEmitted++;
            var byteCount = Encoding.UTF8.GetByteCount(message);
            if (_console.Count >= Limits.MaxConsoleLines || _consoleBytesEmitted >= Limits.MaxConsoleBytes)
            {
                EmitConsoleTruncationDiagnostic();
                return;
            }

            if (_consoleBytesEmitted + byteCount > Limits.MaxConsoleBytes)
            {
                var remainingBytes = Limits.MaxConsoleBytes - _consoleBytesEmitted;
                message = TruncateToUtf8Bytes(message, remainingBytes);
                byteCount = Encoding.UTF8.GetByteCount(message);
                EmitConsoleTruncationDiagnostic();
            }

            _console.Add(new ExecutionConsoleEntry(level, message));
            _consoleBytesEmitted += byteCount;
        }

        public void Dispose()
        {
            // The bridge can still be unwinding an in-flight host call when the outer executor
            // reaches disposal after cancellation or shutdown. Disposing the semaphore here
            // creates a race with the pending Release() in InvokeAsync().
        }

        private static (IProgrammaticSamplingClient PublicClient, IProgrammaticStructuredSamplingClient StructuredClient) CreateExecutionSamplingClients(
            IServiceProvider services,
            IReadOnlyList<string>? visibleApiPaths,
            IReadOnlyList<CapabilityDefinition> visibleCapabilities,
            string conversationId,
            string? callerBindingId)
        {
            if (visibleApiPaths is null)
            {
                var blocked = FixedProgrammaticSamplingClient.Blocked(
                    "sampling_requires_explicit_read_only_scope",
                    "Sampling requires an explicit VisibleApiPaths scope.");
                return (blocked, blocked);
            }

            if (visibleCapabilities.Any(static capability => capability.IsMutation))
            {
                var blocked = FixedProgrammaticSamplingClient.Blocked(
                    "sampling_not_allowed_with_visible_mutations",
                    "Sampling is not allowed when visible capabilities include mutations.");
                return (blocked, blocked);
            }

            var structuredClient = ProgrammaticSamplingServiceResolver.ResolveStructured(services);
            if (structuredClient.IsSupported)
            {
                if (structuredClient is IProgrammaticSamplingClient dualInterfaceClient)
                {
                    return (dualInterfaceClient, structuredClient);
                }

                return (
                    new StructuredBackedProgrammaticSamplingClient(structuredClient, services, conversationId, callerBindingId),
                    structuredClient);
            }

            var publicClient = ProgrammaticSamplingServiceResolver.ResolvePublic(services);
            if (publicClient.IsSupported)
            {
                var adaptedStructuredClient = new PublicBackedStructuredSamplingClient(publicClient);
                return (
                    new StructuredBackedProgrammaticSamplingClient(adaptedStructuredClient, services, conversationId, callerBindingId),
                    adaptedStructuredClient);
            }

            var unsupported = FixedProgrammaticSamplingClient.Unsupported("Sampling is unavailable in this execution context.");
            return (unsupported, unsupported);
        }

        private ValueTask<ProgrammaticSamplingResult> CreateToolEnabledSamplingResultAsync(ProgrammaticSamplingRequest request)
            => StructuredSamplingOrchestrator.CreateMessageAsync(
                request,
                _executionStructuredSamplingClient,
                _services,
                _request.ConversationId,
                CallerBindingId,
                ExecutionCancellationToken);

        private async Task<HostCallResponse> InvokeCapabilityAsync(CapabilityDefinition capability, JsonObject input)
        {
            try
            {
                JsonSchemaValidator.Validate(input, capability.Input.Schema);
            }
            catch (JsonSchemaValidationException exception)
            {
                return Fail(
                    new StructuredBridgeException(
                        "invalid_capability_input",
                        exception.Message,
                        new JsonObject { ["capabilityPath"] = capability.ApiPath }));
            }

            try
            {
                var context = new ProgrammaticCapabilityContext(
                    _request.ConversationId,
                    CallerBindingId,
                    _capabilityServices,
                    ExecutionCancellationToken,
                    ArtifactWriter);
                var result = await capability.CapabilityHandler!(input, context);
                ValidateResultPayload(capability.ApiPath, capability.Result.Schema, result);
                return HostCallResponse.FromSuccess(JintJsonSerializer.NodeToClrObject(result));
            }
            catch (OperationCanceledException)
            {
                var cancelledByCaller = _requestCancellationToken.IsCancellationRequested;
                return Fail(
                    new StructuredBridgeException(
                        cancelledByCaller ? "execution_cancelled" : "timeout",
                        cancelledByCaller ? "Execution was cancelled." : "Execution exceeded the configured timeout.",
                        new JsonObject
                        {
                            ["capabilityPath"] = capability.ApiPath,
                            ["reason"] = cancelledByCaller ? "cancelled" : "timeout"
                        },
                        capability.ApiPath));
            }
            catch (ArtifactContinuityUnavailableException exception)
            {
                return Fail(
                    new StructuredBridgeException(
                        "artifact_continuity_unavailable",
                        exception.Message,
                        new JsonObject
                        {
                            ["reason"] = exception.Reason,
                            ["blockedOperation"] = exception.BlockedOperation
                        }));
            }
            catch (Exception exception)
            {
                return FailFromHandler(capability.ApiPath, exception);
            }
        }

        private async Task<HostCallResponse> InvokeMutationAsync(CapabilityDefinition capability, JsonObject input)
        {
            try
            {
                JsonSchemaValidator.Validate(input, capability.Input.Schema);
            }
            catch (JsonSchemaValidationException exception)
            {
                return Fail(
                    new StructuredBridgeException(
                        "invalid_mutation_args",
                        exception.Message,
                        new JsonObject { ["capabilityPath"] = capability.ApiPath }));
            }

            if (CallerBindingId is null)
            {
                return Fail(
                    new StructuredBridgeException(
                        "mutation_preview_unavailable",
                        "Mutation previews require a caller binding.",
                        new JsonObject
                        {
                            ["reason"] = "missing_caller_binding",
                            ["mutationName"] = capability.ApiPath
                        }));
            }

            var authorized = await _catalog.AuthorizationPolicy.AuthorizeAsync(
                new ProgrammaticAuthorizationContext(
                    "mutation.preview",
                    _request.ConversationId,
                    CallerBindingId,
                    capability.ApiPath,
                    _request.Principal),
                ExecutionCancellationToken);
            if (!authorized)
            {
                return Fail(
                    new StructuredBridgeException(
                        "mutation_preview_unavailable",
                        "Mutation preview was not authorized.",
                        new JsonObject
                        {
                            ["reason"] = "authorization_denied",
                            ["mutationName"] = capability.ApiPath
                        }));
            }

            if (_approvalsRequested.Count >= Limits.MaxApprovalsPerExecution)
            {
                return Fail(
                    new StructuredBridgeException(
                        "invalid_result_payload",
                        "Execution exceeded the maximum number of approval previews.",
                        new JsonObject { ["maxApprovalsPerExecution"] = Limits.MaxApprovalsPerExecution }));
            }

            try
            {
                var context = new ProgrammaticMutationContext(
                    _request.ConversationId,
                    CallerBindingId,
                    ApprovalId: null,
                    _mutationServices,
                    ExecutionCancellationToken,
                    ArtifactWriter);
                var preview = await capability.MutationPreviewHandler!(input, context);
                ValidateResultPayload(capability.ApiPath, capability.PreviewPayloadSchema!, preview);

                string summary;
                try
                {
                    summary = await capability.MutationSummaryFactory!(input, preview, context);
                }
                catch (Exception exception)
                {
                    return Fail(
                        new StructuredBridgeException(
                            "summary_generation_error",
                            exception.Message,
                            new JsonObject { ["mutationName"] = capability.ApiPath }));
                }

                var approvalId = ApprovalTokenGenerator.GenerateApprovalId();
                var approvalNonce = ApprovalTokenGenerator.GenerateApprovalNonce();
                var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Limits.ApprovalTtlSeconds);
                var envelope = new MutationPreviewEnvelope(
                    "mutation_preview",
                    approvalId,
                    approvalNonce,
                    capability.ApiPath,
                    summary,
                    input.DeepClone().AsObject(),
                    preview?.DeepClone(),
                    CapabilityVersionCalculator.CalculateArgsHash(input),
                    expiresAt.ToString("O"));
                var envelopeNode = JintJsonSerializer.SerializeToNode(envelope)!;
                var envelopeBytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(envelopeNode));
                if (envelopeBytes > Limits.MaxApprovalPayloadBytes)
                {
                    return Fail(
                        new StructuredBridgeException(
                            "invalid_result_payload",
                            "Approval preview payload exceeded the maximum allowed size.",
                            new JsonObject { ["maxApprovalPayloadBytes"] = Limits.MaxApprovalPayloadBytes }));
                }

                var pendingApproval = new PendingApproval(
                    approvalId,
                    approvalNonce,
                    capability.ApiPath,
                    input.DeepClone().AsObject(),
                    envelope.ActionArgsHash,
                    envelope,
                    DateTimeOffset.UtcNow,
                    expiresAt,
                    _request.ConversationId,
                    CallerBindingId,
                    ApprovalState.Pending,
                    ApplyingSinceUtc: null,
                    FailureCode: null);
                await ApprovalStore.CreateAsync(pendingApproval, ExecutionCancellationToken);
                _approvalsRequested.Add(envelope);
                return HostCallResponse.FromSuccess(JintJsonSerializer.NodeToClrObject(envelopeNode));
            }
            catch (OperationCanceledException)
            {
                var cancelledByCaller = _requestCancellationToken.IsCancellationRequested;
                return Fail(
                    new StructuredBridgeException(
                        cancelledByCaller ? "execution_cancelled" : "timeout",
                        cancelledByCaller ? "Execution was cancelled." : "Execution exceeded the configured timeout.",
                        new JsonObject
                        {
                            ["capabilityPath"] = capability.ApiPath,
                            ["reason"] = cancelledByCaller ? "cancelled" : "timeout"
                        },
                        capability.ApiPath));
            }
            catch (ArtifactContinuityUnavailableException exception)
            {
                return Fail(
                    new StructuredBridgeException(
                        "artifact_continuity_unavailable",
                        exception.Message,
                        new JsonObject
                        {
                            ["reason"] = exception.Reason,
                            ["blockedOperation"] = exception.BlockedOperation
                        }));
            }
            catch (Exception exception)
            {
                return FailFromHandler(capability.ApiPath, exception);
            }
        }

        private HostCallResponse FailFromHandler(string capabilityPath, Exception exception)
        {
            return Fail(
                new StructuredBridgeException(
                    "capability_handler_error",
                    exception.Message,
                    new JsonObject
                    {
                        ["capabilityPath"] = capabilityPath,
                        ["exceptionType"] = exception.GetType().FullName
                    },
                    capabilityPath));
        }

        private HostCallResponse Fail(Exception exception)
        {
            switch (exception)
            {
                case UnknownCapabilityException unknownCapabilityException:
                    var unknownData = new JsonObject
                    {
                        ["capabilityPath"] = unknownCapabilityException.ApiPath
                    };
                    if (unknownCapabilityException.Suggestions.Count > 0)
                    {
                        unknownData["suggestions"] = new JsonArray(unknownCapabilityException.Suggestions.Select(static suggestion => (JsonNode?)suggestion).ToArray());
                    }

                    _diagnostics.Add(new ExecutionDiagnostic("unknown_capability", unknownCapabilityException.Message, unknownData));
                    return HostCallResponse.FromFailure(
                        new JsonObject
                        {
                            ["code"] = "unknown_capability",
                            ["message"] = unknownCapabilityException.Message,
                            ["capabilityPath"] = unknownCapabilityException.ApiPath,
                            ["data"] = unknownData
                        });

                case StructuredBridgeException structuredBridgeException:
                    _diagnostics.Add(new ExecutionDiagnostic(structuredBridgeException.Code, structuredBridgeException.Message, structuredBridgeException.Data));
                    return HostCallResponse.FromFailure(structuredBridgeException.ToJsError());

                default:
                    _diagnostics.Add(
                        new ExecutionDiagnostic(
                            "execution_failed",
                            exception.Message,
                            new JsonObject
                            {
                                ["exceptionType"] = exception.GetType().FullName
                            }));
                    return HostCallResponse.FromFailure(
                        new JsonObject
                        {
                            ["code"] = "execution_failed",
                            ["message"] = exception.Message,
                            ["data"] = new JsonObject
                            {
                                ["exceptionType"] = exception.GetType().FullName
                            }
                        });
            }
        }

        private IReadOnlyList<string> Suggest(string apiPath)
        {
            return _visiblePathSet
                .Select(path => new { Path = path, Distance = Levenshtein(path, apiPath) })
                .OrderBy(static item => item.Distance)
                .ThenBy(static item => item.Path, StringComparer.Ordinal)
                .Take(3)
                .Select(static item => item.Path)
                .ToArray();
        }

        private static JsonNode? ConvertBridgeArgument(object? argument)
        {
            try
            {
                if (argument is null)
                {
                    return null;
                }

                return JintJsonSerializer.SerializeToNode(argument);
            }
            catch (Exception exception) when (exception is NotSupportedException or JsonException)
            {
                throw new StructuredBridgeException(
                    "invalid_json_argument",
                    "Capability input could not be converted into JSON.",
                    new JsonObject());
            }
        }

        private static void ValidateResultPayload(string capabilityPath, JsonNode schema, JsonNode? payload)
        {
            try
            {
                JsonSchemaValidator.Validate(payload, schema);
            }
            catch (JsonSchemaValidationException exception)
            {
                throw new StructuredBridgeException(
                    "invalid_result_payload",
                    exception.Message,
                    new JsonObject { ["capabilityPath"] = capabilityPath },
                    capabilityPath);
            }
        }

        private void EmitConsoleTruncationDiagnostic()
        {
            if (_consoleTruncated)
            {
                return;
            }

            _consoleTruncated = true;
            _diagnostics.Add(
                new ExecutionDiagnostic(
                    "console_output_truncated",
                    "Console output exceeded the configured capture limits.",
                    new JsonObject
                    {
                        ["maxConsoleLines"] = Limits.MaxConsoleLines,
                        ["maxConsoleBytes"] = Limits.MaxConsoleBytes
                    }));
        }

        private static string TruncateToUtf8Bytes(string value, int maxBytes)
        {
            if (maxBytes <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var used = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                var runeText = rune.ToString();
                var runeBytes = Encoding.UTF8.GetByteCount(runeText);
                if (used + runeBytes > maxBytes)
                {
                    break;
                }

                builder.Append(runeText);
                used += runeBytes;
            }

            return builder.ToString();
        }

        private static int Levenshtein(string left, string right)
        {
            var costs = new int[right.Length + 1];
            for (var j = 0; j < costs.Length; j++)
            {
                costs[j] = j;
            }

            for (var i = 1; i <= left.Length; i++)
            {
                var previous = costs[0];
                costs[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var current = costs[j];
                    var replacementCost = left[i - 1] == right[j - 1] ? previous : previous + 1;
                    costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), replacementCost);
                    previous = current;
                }
            }

            return costs[^1];
        }
    }

    private sealed class ExecutionArtifactWriter : IArtifactWriter
    {
        private readonly IArtifactStore _store;
        private readonly string _conversationId;
        private readonly string? _callerBindingId;
        private readonly ArtifactRetentionOptions _retention;
        private readonly List<ExecutionArtifactDescriptor> _artifacts;

        public ExecutionArtifactWriter(
            IArtifactStore store,
            string conversationId,
            string? callerBindingId,
            ArtifactRetentionOptions retention,
            List<ExecutionArtifactDescriptor> artifacts)
        {
            _store = store;
            _conversationId = conversationId;
            _callerBindingId = callerBindingId;
            _retention = retention;
            _artifacts = artifacts;
        }

        public ValueTask<ExecutionArtifactDescriptor> WriteJsonArtifactAsync(
            string name,
            JsonNode payload,
            CancellationToken cancellationToken = default)
        {
            return WriteCanonicalJsonArtifactAsync(name, "handler.output", payload, cancellationToken);
        }

        public ValueTask<ExecutionArtifactDescriptor> WriteTextArtifactAsync(
            string name,
            string content,
            string mimeType,
            CancellationToken cancellationToken = default)
        {
            return WriteArtifactCoreAsync(name, "handler.output", mimeType, content, cancellationToken);
        }

        public ValueTask<ExecutionArtifactDescriptor> WriteCanonicalJsonArtifactAsync(
            string name,
            string kind,
            JsonNode payload,
            CancellationToken cancellationToken = default)
        {
            return WriteArtifactCoreAsync(name, kind, "application/json", CanonicalJson.Serialize(payload), cancellationToken);
        }

        private async ValueTask<ExecutionArtifactDescriptor> WriteArtifactCoreAsync(
            string name,
            string kind,
            string mimeType,
            string content,
            CancellationToken cancellationToken)
        {
            if (_callerBindingId is null)
            {
                throw new ArtifactContinuityUnavailableException("missing_caller_binding", "artifact_write");
            }

            var artifactId = Guid.NewGuid().ToString("n");
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(_retention.ArtifactTtlSeconds);
            await _store.WriteAsync(
                new ArtifactWriteRequest(
                    artifactId,
                    _conversationId,
                    _callerBindingId,
                    kind,
                    name,
                    mimeType,
                    content,
                    expiresAt),
                cancellationToken);

            var totalBytes = Encoding.UTF8.GetByteCount(content);
            var totalChunks = CountChunks(content, _retention.ArtifactChunkBytes);
            var descriptor = new ExecutionArtifactDescriptor(
                artifactId,
                kind,
                name,
                mimeType,
                totalBytes,
                totalChunks,
                expiresAt.ToString("O"));
            _artifacts.Add(descriptor);
            return descriptor;
        }

        private static int CountChunks(string content, int chunkBytes)
        {
            if (content.Length == 0)
            {
                return 0;
            }

            var count = 0;
            var currentBytes = 0;
            foreach (var rune in content.EnumerateRunes())
            {
                var runeBytes = Encoding.UTF8.GetByteCount(rune.ToString());
                if (currentBytes > 0 && currentBytes + runeBytes > chunkBytes)
                {
                    count++;
                    currentBytes = 0;
                }

                currentBytes += runeBytes;
            }

            if (currentBytes > 0)
            {
                count++;
            }

            return count;
        }
    }

    private sealed record HostCallResponse(bool Success, object? Value, object? Error)
    {
        public static HostCallResponse FromSuccess(object? value) => new(true, value, null);

        public static HostCallResponse FromFailure(object error) => new(false, null, error);
    }

    private sealed record RuntimeMetrics(int StatementsExecuted, int PeakMemoryBytes);

    private sealed class RuntimeMetricsCollector
    {
        private static readonly System.Reflection.FieldInfo? InitialMemoryUsageField =
            typeof(MemoryLimitConstraint).GetField("_initialMemoryUsage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private readonly MemoryLimitConstraint? _memoryConstraint;
        private readonly long _initialMemoryUsage;
        private int _statementsExecuted;
        private int _peakMemoryBytes;

        private RuntimeMetricsCollector(Engine engine, MemoryLimitConstraint? memoryConstraint, long initialMemoryUsage)
        {
            _memoryConstraint = memoryConstraint;
            _initialMemoryUsage = initialMemoryUsage;
            engine.Debugger.Step += OnStep;
            Sample();
        }

        public static RuntimeMetricsCollector Create(Engine engine)
        {
            var memoryConstraint = engine.Constraints.Find<MemoryLimitConstraint>();
            var initialMemoryUsage = memoryConstraint is null || InitialMemoryUsageField is null
                ? GC.GetTotalMemory(forceFullCollection: false)
                : (long)(InitialMemoryUsageField.GetValue(memoryConstraint) ?? 0L);

            return new RuntimeMetricsCollector(engine, memoryConstraint, initialMemoryUsage);
        }

        public void Sample()
        {
            var currentMemoryUsage = GC.GetTotalMemory(forceFullCollection: false);
            var delta = Math.Max(0L, currentMemoryUsage - _initialMemoryUsage);
            if (delta > _peakMemoryBytes)
            {
                _peakMemoryBytes = delta > int.MaxValue ? int.MaxValue : (int)delta;
            }
        }

        public RuntimeMetrics Snapshot()
        {
            return new RuntimeMetrics(_statementsExecuted, _peakMemoryBytes);
        }

        private global::Jint.Runtime.Debugger.StepMode OnStep(object? _, global::Jint.Runtime.Debugger.DebugInformation info)
        {
            _statementsExecuted++;
            if (info.CurrentMemoryUsage > 0)
            {
                var delta = Math.Max(0L, info.CurrentMemoryUsage - _initialMemoryUsage);
                if (delta > _peakMemoryBytes)
                {
                    _peakMemoryBytes = delta > int.MaxValue ? int.MaxValue : (int)delta;
                }
            }

            return global::Jint.Runtime.Debugger.StepMode.Into;
        }
    }

    private sealed class StructuredBridgeException : Exception
    {
        public StructuredBridgeException(string code, string message, JsonObject? data = null, string? capabilityPath = null)
            : base(message)
        {
            Code = code;
            Data = data;
            CapabilityPath = capabilityPath;
        }

        public string Code { get; }

        public new JsonObject? Data { get; }

        public string? CapabilityPath { get; }

        public JsonObject ToJsError()
        {
            return new JsonObject
            {
                ["code"] = Code,
                ["message"] = Message,
                ["capabilityPath"] = CapabilityPath,
                ["data"] = Data?.DeepClone()
            };
        }
    }

    private sealed class ArtifactContinuityUnavailableException : Exception
    {
        public ArtifactContinuityUnavailableException(string reason, string blockedOperation)
            : base("Artifact continuity is not available for this execution.")
        {
            Reason = reason;
            BlockedOperation = blockedOperation;
        }

        public string Reason { get; }

        public string BlockedOperation { get; }
    }

    private sealed class FixedProgrammaticSamplingClient(
        string code,
        string message,
        bool isSupported,
        bool supportsToolUse) : IProgrammaticSamplingClient, IProgrammaticStructuredSamplingClient
    {
        public static FixedProgrammaticSamplingClient Blocked(string code, string message)
            => new(code, message, isSupported: true, supportsToolUse: false);

        public static FixedProgrammaticSamplingClient Unsupported(string message)
            => new("sampling_unavailable", message, isSupported: false, supportsToolUse: false);

        public bool IsSupported => isSupported;

        public bool SupportsToolUse => supportsToolUse;

        public ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromException<ProgrammaticSamplingResult>(new ProgrammaticSamplingException(code, message));

        public ValueTask<ProgrammaticStructuredSamplingResult> CreateMessageAsync(
            ProgrammaticStructuredSamplingRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<ProgrammaticStructuredSamplingResult>(new ProgrammaticSamplingException(code, message));
    }

    private static class StructuredSamplingOrchestrator
    {
        public static async ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(
            ProgrammaticSamplingRequest request,
            IProgrammaticStructuredSamplingClient structuredClient,
            IServiceProvider services,
            string conversationId,
            string? callerBindingId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                BuilderValidation.ValidateSamplingRequest(request);
            }
            catch (ArgumentException exception)
            {
                throw new ProgrammaticSamplingException("invalid_params", exception.Message, innerException: exception);
            }

            EnsureSupported(structuredClient);

            if (!request.EnableTools)
            {
                var response = await structuredClient.CreateMessageAsync(ToStructuredRequest(request), cancellationToken);
                return CreateFinalResult(response);
            }

            if (!structuredClient.SupportsToolUse)
            {
                throw new ProgrammaticSamplingException("sampling_tool_use_unavailable", "The current execution context does not support tool-enabled sampling.");
            }

            var limits = ProgrammaticSamplingLoopLimitResolver.Resolve(services);
            var samplingTools = ResolveEffectiveTools(request, services);
            var transcript = request.Messages
                .Select(static message => new ProgrammaticStructuredSamplingMessage(
                    message.Role,
                    [new ProgrammaticStructuredSamplingTextBlock(message.Text)]))
                .ToList();

            for (var round = 0; round < limits.MaxSamplingRounds; round++)
            {
                var response = await structuredClient.CreateMessageAsync(
                    new ProgrammaticStructuredSamplingRequest(
                        request.SystemPrompt,
                        transcript.ToArray(),
                        request.MaxTokens ?? 1000,
                        samplingTools,
                        new ProgrammaticStructuredSamplingToolChoice("auto")),
                    cancellationToken);

                var toolUses = response.Content.OfType<ProgrammaticStructuredSamplingToolUseBlock>().ToArray();
                if (toolUses.Length == 0)
                {
                    return CreateFinalResult(response);
                }

                transcript.Add(new ProgrammaticStructuredSamplingMessage(response.Role, response.Content));

                foreach (var toolUse in toolUses)
                {
                    if (!samplingTools.Any(tool => string.Equals(tool.Name, toolUse.Name, StringComparison.Ordinal)))
                    {
                        throw new ProgrammaticSamplingException(
                            "sampling_invalid_tool_call",
                            $"The model requested unknown sampling tool '{toolUse.Name}'.");
                    }

                    if (toolUse.Input is not JsonObject inputObject)
                    {
                        throw new ProgrammaticSamplingException(
                            "sampling_invalid_tool_call",
                            $"Sampling tool '{toolUse.Name}' must be invoked with a JSON object.");
                    }

                    ProgrammaticSamplingToolExecutionResult executionResult;
                    try
                    {
                        var blockedClient = FixedProgrammaticSamplingClient.Blocked(
                            "sampling_reentry_not_allowed",
                            "Sampling-tool handlers cannot start nested sampling requests.");
                        var toolServices = SamplingServiceOverlay.Create(services, blockedClient, blockedClient);
                        var samplingToolsRegistry = services.GetService(typeof(IProgrammaticSamplingToolRegistry)) as IProgrammaticSamplingToolRegistry
                            ?? throw new ProgrammaticSamplingException("sampling_no_tools_available", "No sampling tools are available for this request.");
                        executionResult = await samplingToolsRegistry.InvokeAsync(
                            toolUse.Name,
                            inputObject,
                            new ProgrammaticSamplingToolContext(conversationId, callerBindingId, toolServices, cancellationToken),
                            cancellationToken);
                    }
                    catch (ProgrammaticSamplingException)
                    {
                        throw;
                    }
                    catch (JsonSchemaValidationException exception)
                    {
                        throw new ProgrammaticSamplingException("sampling_invalid_tool_call", exception.Message, innerException: exception);
                    }
                    catch (Exception exception)
                    {
                        throw new ProgrammaticSamplingException("sampling_tool_execution_failed", exception.Message, innerException: exception);
                    }

                    if (Encoding.UTF8.GetByteCount(executionResult.Text) > limits.MaxSamplingToolResultBytes)
                    {
                        throw new ProgrammaticSamplingException("sampling_tool_result_too_large", "Sampling tool result exceeded the configured size limit.");
                    }

                    transcript.Add(
                        new ProgrammaticStructuredSamplingMessage(
                            "user",
                            [
                                new ProgrammaticStructuredSamplingToolResultBlock(
                                    toolUse.Id,
                                    executionResult.Text,
                                    false,
                                    executionResult.StructuredContent)
                            ]));
                }
            }

            throw new ProgrammaticSamplingException("sampling_round_limit_exceeded", "Sampling exceeded the configured round limit.");
        }

        private static ProgrammaticStructuredSamplingRequest ToStructuredRequest(ProgrammaticSamplingRequest request)
            => new(
                request.SystemPrompt,
                request.Messages.Select(static message => new ProgrammaticStructuredSamplingMessage(
                    message.Role,
                    [new ProgrammaticStructuredSamplingTextBlock(message.Text)])).ToArray(),
                request.MaxTokens ?? 1000);

        private static ProgrammaticSamplingResult CreateFinalResult(ProgrammaticStructuredSamplingResult response)
        {
            var textBlocks = response.Content.OfType<ProgrammaticStructuredSamplingTextBlock>().Select(static block => block.Text).ToArray();
            if (textBlocks.Length == 0)
            {
                throw new ProgrammaticSamplingException("sampling_failed", "Sampling completed without any text content.");
            }

            return new ProgrammaticSamplingResult(string.Join("\n\n", textBlocks), response.Model, response.StopReason);
        }

        private static IReadOnlyList<ProgrammaticStructuredSamplingToolDefinition> ResolveEffectiveTools(
            ProgrammaticSamplingRequest request,
            IServiceProvider services)
        {
            var registry = services.GetService(typeof(IProgrammaticSamplingToolRegistry)) as IProgrammaticSamplingToolRegistry;
            if (registry is null)
            {
                throw new ProgrammaticSamplingException("sampling_no_tools_available", "No sampling tools are available for this request.");
            }

            var tools = request.AllowedToolNames is null
                ? registry.Tools
                : registry.Tools.Where(tool => request.AllowedToolNames.Contains(tool.Name, StringComparer.Ordinal)).ToArray();

            if (tools.Count == 0)
            {
                throw new ProgrammaticSamplingException("sampling_no_tools_available", "No sampling tools are available for this request.");
            }

            return tools;
        }

        private static void EnsureSupported(IProgrammaticStructuredSamplingClient structuredClient)
        {
            if (!structuredClient.IsSupported)
            {
                throw new ProgrammaticSamplingException("sampling_unavailable", "Sampling is unavailable in this execution context.");
            }
        }
    }

    private sealed class StructuredBackedProgrammaticSamplingClient(
        IProgrammaticStructuredSamplingClient structuredClient,
        IServiceProvider services,
        string conversationId,
        string? callerBindingId) : IProgrammaticSamplingClient
    {
        public bool IsSupported => structuredClient.IsSupported;

        public bool SupportsToolUse => structuredClient.SupportsToolUse;

        public ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
            => StructuredSamplingOrchestrator.CreateMessageAsync(request, structuredClient, services, conversationId, callerBindingId, cancellationToken);
    }

    private sealed class PublicBackedStructuredSamplingClient(IProgrammaticSamplingClient publicClient) : IProgrammaticStructuredSamplingClient
    {
        public bool IsSupported => publicClient.IsSupported;

        public bool SupportsToolUse => false;

        public async ValueTask<ProgrammaticStructuredSamplingResult> CreateMessageAsync(
            ProgrammaticStructuredSamplingRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Tools is { Count: > 0 } || request.ToolChoice is not null)
            {
                throw new ProgrammaticSamplingException("sampling_tool_use_unavailable", "The current execution context does not support tool-enabled sampling.");
            }

            var messages = request.Messages.Select(ToPublicMessage).ToArray();
            var response = await publicClient.CreateMessageAsync(
                new ProgrammaticSamplingRequest(
                    request.SystemPrompt,
                    messages,
                    EnableTools: false,
                    AllowedToolNames: null,
                    request.MaxTokens),
                cancellationToken);

            return new ProgrammaticStructuredSamplingResult(
                "assistant",
                [new ProgrammaticStructuredSamplingTextBlock(response.Text)],
                response.Model,
                response.StopReason);
        }

        private static ProgrammaticSamplingMessage ToPublicMessage(ProgrammaticStructuredSamplingMessage message)
        {
            var nonTextBlock = message.Content.FirstOrDefault(static block => block is not ProgrammaticStructuredSamplingTextBlock);
            if (nonTextBlock is not null)
            {
                throw new ProgrammaticSamplingException(
                    "sampling_tool_use_unavailable",
                    "The current execution context cannot translate structured tool content through a text-only sampling client.");
            }

            var text = string.Join(
                "\n\n",
                message.Content
                    .OfType<ProgrammaticStructuredSamplingTextBlock>()
                    .Select(static block => block.Text));

            return new ProgrammaticSamplingMessage(message.Role, text);
        }
    }

    private static class SamplingServiceOverlay
    {
        public static IServiceProvider Create(
            IServiceProvider inner,
            IProgrammaticSamplingClient publicClient,
            IProgrammaticStructuredSamplingClient structuredClient)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(publicClient);
            ArgumentNullException.ThrowIfNull(structuredClient);

            return new OverlayServiceProvider(inner, publicClient, structuredClient);
        }

        private sealed class OverlayServiceProvider(
            IServiceProvider inner,
            IProgrammaticSamplingClient publicClient,
            IProgrammaticStructuredSamplingClient structuredClient) : IServiceProvider
        {
            private readonly Lazy<IReadOnlyDictionary<Type, object>> _overrides = new(
                () => new Dictionary<Type, object>
                {
                    [typeof(IProgrammaticSamplingClient)] = publicClient,
                    [typeof(IProgrammaticStructuredSamplingClient)] = structuredClient,
                    [typeof(IServiceScopeFactory)] = new OverlayServiceScopeFactory(inner, publicClient, structuredClient)
                });

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IServiceProvider))
                {
                    return this;
                }

                if (_overrides.Value.TryGetValue(serviceType, out var overrideValue))
                {
                    return overrideValue;
                }

                return inner.GetService(serviceType);
            }
        }

        private sealed class OverlayServiceScopeFactory(
            IServiceProvider inner,
            IProgrammaticSamplingClient publicClient,
            IProgrammaticStructuredSamplingClient structuredClient) : IServiceScopeFactory
        {
            public IServiceScope CreateScope()
            {
                var innerFactory = inner.GetRequiredService<IServiceScopeFactory>();
                return new OverlayServiceScope(innerFactory.CreateScope(), publicClient, structuredClient);
            }
        }

        private sealed class OverlayServiceScope(
            IServiceScope inner,
            IProgrammaticSamplingClient publicClient,
            IProgrammaticStructuredSamplingClient structuredClient) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = Create(inner.ServiceProvider, publicClient, structuredClient);

            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }

    private static class JintJsonSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public static JsonNode? SerializeToNode(object? value)
        {
            return JsonSerializer.SerializeToNode(value, Options);
        }

        public static object? NodeToClrObject(JsonNode? node)
        {
            return node switch
            {
                null => null,
                JsonObject jsonObject => jsonObject.ToDictionary(
                    static pair => pair.Key,
                    static pair => NodeToClrObject(pair.Value),
                    StringComparer.Ordinal),
                JsonArray jsonArray => jsonArray.Select(NodeToClrObject).ToArray(),
                JsonValue jsonValue => jsonValue.Deserialize<object>(Options),
                _ => node.Deserialize<object>(Options)
            };
        }
    }
}

/// <summary>
/// Represents a lookup failure for a capability path exposed to generated code.
/// </summary>
public sealed class UnknownCapabilityException : Exception
{
    /// <summary>
    /// Creates a new unknown-capability exception.
    /// </summary>
    /// <param name="apiPath">The unresolved capability path.</param>
    /// <param name="suggestions">Optional visible-path suggestions for the caller.</param>
    public UnknownCapabilityException(string apiPath, IReadOnlyList<string>? suggestions = null)
        : base($"Unknown capability '{apiPath}'.")
    {
        ApiPath = apiPath;
        Suggestions = suggestions ?? Array.Empty<string>();
    }

    /// <summary>Gets the unresolved capability path.</summary>
    public string ApiPath { get; }

    /// <summary>Gets suggested visible capability paths, if any.</summary>
    public IReadOnlyList<string> Suggestions { get; }
}
