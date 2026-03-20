using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ProgrammaticMcp.AspNetCore;

internal static class ProgrammaticSamplingServiceOverlay
{
    public static IServiceProvider Create(
        IServiceProvider inner,
        IProgrammaticSamplingClient publicClient,
        IProgrammaticStructuredSamplingClient structuredClient)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(publicClient);
        ArgumentNullException.ThrowIfNull(structuredClient);
        return new OverlayServiceProvider(
            inner,
            _ => new Dictionary<Type, object>
            {
                [typeof(IProgrammaticSamplingClient)] = publicClient,
                [typeof(IProgrammaticStructuredSamplingClient)] = structuredClient
            });
    }

    private sealed class OverlayServiceProvider : IServiceProvider, IServiceScopeFactory
    {
        private readonly IServiceProvider _inner;
        private readonly Func<IServiceProvider, IReadOnlyDictionary<Type, object>> _overlayFactory;
        private readonly Lazy<IReadOnlyDictionary<Type, object>> _overrides;

        public OverlayServiceProvider(
            IServiceProvider inner,
            Func<IServiceProvider, IReadOnlyDictionary<Type, object>> overlayFactory)
        {
            _inner = inner;
            _overlayFactory = overlayFactory;
            _overrides = new Lazy<IReadOnlyDictionary<Type, object>>(() => overlayFactory(inner));
        }

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

            if (_overrides.Value.TryGetValue(serviceType, out var value))
            {
                return value;
            }

            return _inner.GetService(serviceType);
        }

        public IServiceScope CreateScope()
        {
            var innerScopeFactory = _inner.GetRequiredService<IServiceScopeFactory>();
            return new OverlayServiceScope(innerScopeFactory.CreateScope(), _overlayFactory);
        }
    }

    private sealed class OverlayServiceScope : IServiceScope
    {
        private readonly IServiceScope _inner;

        public OverlayServiceScope(
            IServiceScope inner,
            Func<IServiceProvider, IReadOnlyDictionary<Type, object>> overlayFactory)
        {
            _inner = inner;
            ServiceProvider = new OverlayServiceProvider(inner.ServiceProvider, overlayFactory);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => _inner.Dispose();
    }
}

internal sealed class FixedProgrammaticSamplingClient(
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

    ValueTask<ProgrammaticStructuredSamplingResult> IProgrammaticStructuredSamplingClient.CreateMessageAsync(
        ProgrammaticStructuredSamplingRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromException<ProgrammaticStructuredSamplingResult>(new ProgrammaticSamplingException(code, message));
}

internal sealed class LiveProgrammaticSamplingClient : IProgrammaticSamplingClient, IProgrammaticStructuredSamplingClient
{
    private readonly McpServer _server;
    private readonly IProgrammaticSamplingToolRegistry _samplingTools;
    private readonly IServiceProvider _executionServices;
    private readonly ProgrammaticMcpServerOptions _options;
    private readonly string _conversationId;
    private readonly string? _callerBindingId;

    public LiveProgrammaticSamplingClient(
        McpServer server,
        IProgrammaticSamplingToolRegistry samplingTools,
        IServiceProvider executionServices,
        ProgrammaticMcpServerOptions options,
        string conversationId,
        string? callerBindingId)
    {
        _server = server;
        _samplingTools = samplingTools;
        _executionServices = executionServices;
        _options = options;
        _conversationId = conversationId;
        _callerBindingId = callerBindingId;
    }

    public bool IsSupported => _server.ClientCapabilities?.Sampling is not null;

    public bool SupportsToolUse => _server.ClientCapabilities?.Sampling?.Tools is not null;

    public async ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            BuilderValidation.ValidateSamplingRequest(request);
        }
        catch (ArgumentException exception)
        {
            throw new ProgrammaticSamplingException("invalid_params", exception.Message, innerException: exception);
        }

        EnsureSupported();

        if (!request.EnableTools)
        {
            var singleRound = await ((IProgrammaticStructuredSamplingClient)this).CreateMessageAsync(
                new ProgrammaticStructuredSamplingRequest(
                    request.SystemPrompt,
                    request.Messages.Select(static message => new ProgrammaticStructuredSamplingMessage(
                        message.Role,
                        new ProgrammaticStructuredSamplingContentBlock[]
                        {
                            new ProgrammaticStructuredSamplingTextBlock(message.Text)
                        })).ToArray(),
                    request.MaxTokens ?? 1000),
                cancellationToken);

            return CreateFinalResult(singleRound);
        }

        if (!SupportsToolUse)
        {
            throw new ProgrammaticSamplingException("sampling_tool_use_unavailable", "The connected client does not support tool-enabled sampling.");
        }

        var tools = ResolveEffectiveTools(request);
        var transcript = request.Messages
            .Select(static message => new ProgrammaticStructuredSamplingMessage(
                message.Role,
                new ProgrammaticStructuredSamplingContentBlock[]
                {
                    new ProgrammaticStructuredSamplingTextBlock(message.Text)
                }))
            .ToList();

        for (var round = 0; round < _options.MaxSamplingRounds; round++)
        {
            var response = await ((IProgrammaticStructuredSamplingClient)this).CreateMessageAsync(
                new ProgrammaticStructuredSamplingRequest(
                    request.SystemPrompt,
                    transcript.ToArray(),
                    request.MaxTokens ?? 1000,
                    tools,
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
                if (!tools.Any(tool => string.Equals(tool.Name, toolUse.Name, StringComparison.Ordinal)))
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
                    var blockedToolClient = new FixedProgrammaticSamplingClient(
                        "sampling_reentry_not_allowed",
                        "Sampling-tool handlers cannot start nested sampling requests.",
                        isSupported: true,
                        supportsToolUse: false);
                    var toolServices = ProgrammaticSamplingServiceOverlay.Create(_executionServices, blockedToolClient, blockedToolClient);
                    executionResult = await _samplingTools.InvokeAsync(
                        toolUse.Name,
                        inputObject,
                        new ProgrammaticSamplingToolContext(_conversationId, _callerBindingId, toolServices, cancellationToken),
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

                if (Encoding.UTF8.GetByteCount(executionResult.Text) > _options.MaxSamplingToolResultBytes)
                {
                    throw new ProgrammaticSamplingException("sampling_tool_result_too_large", "Sampling tool result exceeded the configured size limit.");
                }

                transcript.Add(
                    new ProgrammaticStructuredSamplingMessage(
                        "user",
                        new ProgrammaticStructuredSamplingContentBlock[]
                        {
                            new ProgrammaticStructuredSamplingToolResultBlock(
                                toolUse.Id,
                                executionResult.Text,
                                false,
                                executionResult.StructuredContent)
                        }));
            }
        }

        throw new ProgrammaticSamplingException("sampling_round_limit_exceeded", "Sampling exceeded the configured round limit.");
    }

    async ValueTask<ProgrammaticStructuredSamplingResult> IProgrammaticStructuredSamplingClient.CreateMessageAsync(
        ProgrammaticStructuredSamplingRequest request,
        CancellationToken cancellationToken)
    {
        EnsureSupported();
        if (request.Tools is { Count: > 0 } && !SupportsToolUse)
        {
            throw new ProgrammaticSamplingException("sampling_tool_use_unavailable", "The connected client does not support tool-enabled sampling.");
        }

        var protocolRequest = new CreateMessageRequestParams
        {
            SystemPrompt = request.SystemPrompt,
            MaxTokens = request.MaxTokens,
            Messages = request.Messages.Select(ToProtocolMessage).ToList(),
            Tools = request.Tools?.Select(ToProtocolTool).ToList(),
            ToolChoice = request.ToolChoice is null ? null : new ToolChoice { Mode = request.ToolChoice.Mode }
        };

        if (JsonSerializer.SerializeToUtf8Bytes(protocolRequest).Length > _options.MaxSamplingRequestBytes)
        {
            throw new ProgrammaticSamplingException("sampling_request_too_large", "Sampling request exceeded the configured size limit.");
        }

        CreateMessageResult response;
        try
        {
            response = await _server.SampleAsync(protocolRequest, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new ProgrammaticSamplingException("sampling_unavailable", exception.Message, innerException: exception);
        }

        return new ProgrammaticStructuredSamplingResult(
            ToProgrammaticRole(response.Role),
            response.Content.Select(ToProgrammaticContentBlock).ToArray(),
            response.Model,
            response.StopReason);
    }

    private IReadOnlyList<ProgrammaticStructuredSamplingToolDefinition> ResolveEffectiveTools(ProgrammaticSamplingRequest request)
    {
        var tools = request.AllowedToolNames is null
            ? _samplingTools.Tools
            : _samplingTools.Tools.Where(tool => request.AllowedToolNames.Contains(tool.Name, StringComparer.Ordinal)).ToArray();

        if (tools.Count == 0)
        {
            throw new ProgrammaticSamplingException("sampling_no_tools_available", "No sampling tools are available for this request.");
        }

        return tools;
    }

    private ProgrammaticSamplingResult CreateFinalResult(ProgrammaticStructuredSamplingResult response)
    {
        var textBlocks = response.Content.OfType<ProgrammaticStructuredSamplingTextBlock>().Select(static block => block.Text).ToArray();
        if (textBlocks.Length == 0)
        {
            throw new ProgrammaticSamplingException("sampling_failed", "Sampling completed without any text content.");
        }

        return new ProgrammaticSamplingResult(string.Join("\n\n", textBlocks), response.Model, response.StopReason);
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new ProgrammaticSamplingException("sampling_unavailable", "Sampling is unavailable for the connected client.");
        }
    }

    private static SamplingMessage ToProtocolMessage(ProgrammaticStructuredSamplingMessage message)
        => new()
        {
            Role = ToProtocolRole(message.Role),
            Content = message.Content.Select(ToProtocolContentBlock).ToList()
        };

    private static Tool ToProtocolTool(ProgrammaticStructuredSamplingToolDefinition tool)
        => new()
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = ToJsonElement(tool.InputSchema),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            }
        };

    private static ContentBlock ToProtocolContentBlock(ProgrammaticStructuredSamplingContentBlock block)
    {
        return block switch
        {
            ProgrammaticStructuredSamplingTextBlock textBlock => new TextContentBlock { Text = textBlock.Text },
            ProgrammaticStructuredSamplingToolUseBlock toolUseBlock => new ToolUseContentBlock
            {
                Id = toolUseBlock.Id,
                Name = toolUseBlock.Name,
                Input = ToJsonElement(toolUseBlock.Input)
            },
            ProgrammaticStructuredSamplingToolResultBlock toolResultBlock => new ToolResultContentBlock
            {
                ToolUseId = toolResultBlock.ToolUseId,
                IsError = toolResultBlock.IsError,
                StructuredContent = toolResultBlock.StructuredContent is null ? null : ToJsonElement(toolResultBlock.StructuredContent),
                Content =
                [
                    new TextContentBlock
                    {
                        Text = toolResultBlock.Text
                    }
                ]
            },
            _ => throw new InvalidOperationException($"Unsupported sampling content block '{block.GetType().Name}'.")
        };
    }

    private static ProgrammaticStructuredSamplingContentBlock ToProgrammaticContentBlock(ContentBlock block)
    {
        return block switch
        {
            TextContentBlock textBlock => new ProgrammaticStructuredSamplingTextBlock(textBlock.Text),
            ToolUseContentBlock toolUseBlock => new ProgrammaticStructuredSamplingToolUseBlock(
                toolUseBlock.Id,
                toolUseBlock.Name,
                ParseJsonNode(toolUseBlock.Input)),
            ToolResultContentBlock toolResultBlock => new ProgrammaticStructuredSamplingToolResultBlock(
                toolResultBlock.ToolUseId,
                string.Join(
                    "\n\n",
                    toolResultBlock.Content.OfType<TextContentBlock>().Select(static item => item.Text)),
                toolResultBlock.IsError ?? false,
                ParseJsonNode(toolResultBlock.StructuredContent)),
            _ => throw new ProgrammaticSamplingException("sampling_failed", $"Unsupported sampling content block '{block.Type}'.")
        };
    }

    private static Role ToProtocolRole(string role) => role switch
    {
        "user" => Role.User,
        "assistant" => Role.Assistant,
        _ => throw new ProgrammaticSamplingException("invalid_params", $"Unsupported sampling role '{role}'.")
    };

    private static string ToProgrammaticRole(Role role) => role switch
    {
        Role.User => "user",
        _ => "assistant"
    };

    private static JsonNode? ParseJsonNode(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonNode.Parse(element.GetRawText());
    }

    private static JsonNode? ParseJsonNode(JsonElement? element)
    {
        return element is null ? null : ParseJsonNode(element.Value);
    }

    private static JsonElement ToJsonElement(JsonNode? node)
    {
        if (node is null)
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        return JsonSerializer.SerializeToElement(node);
    }
}
