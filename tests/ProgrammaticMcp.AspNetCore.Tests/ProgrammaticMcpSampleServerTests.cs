using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProgrammaticMcp.SampleServer;

namespace ProgrammaticMcp.AspNetCore.Tests;

public sealed class ProgrammaticMcpSampleServerTests
{
    [Fact]
    public async Task SampleRootAndHealthEndpointsAreAvailable()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var root = await client.GetFromJsonAsync<JsonObject>("/");
        var health = await client.GetAsync("/mcp/health");

        Assert.NotNull(root);
        Assert.Equal("sample server", root!["surface"]!.GetValue<string>());
        Assert.Equal("/mcp", root["endpoints"]!["mcp"]!.GetValue<string>());
        Assert.Equal("/mcp/types", root["endpoints"]!["types"]!.GetValue<string>());
        Assert.Equal("/mcp/health", root["endpoints"]!["health"]!.GetValue<string>());
        Assert.Equal(
            new[]
            {
                "sample://workspace/guide",
                "sample://workspace/projects"
            },
            root["resourceUris"]!.AsArray().Select(static item => item!.GetValue<string>()).ToArray());
        Assert.Equal("tasks.summarizeWithSampling", root["sampling"]!["capability"]!.GetValue<string>());
        Assert.Equal("tasks.readForSampling", root["sampling"]!["tool"]!.GetValue<string>());
        Assert.Equal("task-1", root["sampleIds"]!["openTask"]!.GetValue<string>());
        Assert.Equal("task-4", root["sampleIds"]!["samplingTask"]!.GetValue<string>());
        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task SampleServerSupportsDiscoveryExecutionArtifactsAndMutationFlow()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var rawClient = new SampleRawMcpClient(client);

        await rawClient.InitializeAsync();

        var search = await rawClient.CallToolAsync(
            "capabilities.search",
            new JsonObject
            {
                ["detailLevel"] = "Full",
                ["limit"] = 10
            });

        var items = search["items"]!.AsArray()
            .Select(item => item!["apiPath"]!.GetValue<string>())
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "projects.list",
                "tasks.complete",
                "tasks.exportReport",
                "tasks.getById",
                "tasks.list",
                "tasks.summarizeWithSampling"
            },
            items);

        var execute = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "sample-flow",
                ["code"] = """
                           async function main() {
                               return {
                                   projects: await programmatic.projects.list({}),
                                   tasks: await programmatic.tasks.list({ projectId: "project-alpha" }),
                                   task: await programmatic.tasks.getById({ taskId: "task-1" })
                               };
                           }
                           """
            });

        Assert.True(execute["result"] is JsonObject, execute.ToJsonString());
        var executeResult = (JsonObject)execute["result"]!;
        Assert.Equal("task-1", executeResult["task"]?["taskId"]?.GetValue<string>());
        Assert.Equal("project-alpha", executeResult["tasks"]?["tasks"]?[0]?["projectId"]?.GetValue<string>());

        var report = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "sample-report",
                ["maxResultBytes"] = 256,
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.exportReport({ projectId: "project-alpha" });
                           }
                           """
            });

        Assert.Null(report["result"]);
        var artifactId = report["resultArtifactId"]!.GetValue<string>();
        Assert.NotNull(artifactId);

        var artifact = await rawClient.CallToolAsync(
            "artifact.read",
            new JsonObject
            {
                ["conversationId"] = "sample-report",
                ["artifactId"] = artifactId,
                ["limit"] = 1
            });

        Assert.True(artifact["found"]!.GetValue<bool>());
        Assert.Equal("execution.result", artifact["kind"]!.GetValue<string>());
        Assert.True(artifact["items"]!.AsArray().Count >= 1);

        var preview = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "sample-complete",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "task-1" });
                           }
                           """
            });

        var approval = Assert.Single(preview["approvalsRequested"]!.AsArray());
        var approvalId = approval!["approvalId"]!.GetValue<string>();
        var approvalNonce = approval["approvalNonce"]!.GetValue<string>();

        var list = await rawClient.CallToolAsync(
            "mutation.list",
            new JsonObject
            {
                ["conversationId"] = "sample-complete"
            });
        var listed = Assert.Single(list["items"]!.AsArray());
        Assert.Equal(approvalId, listed!["approvalId"]!.GetValue<string>());

        var apply = await rawClient.CallToolAsync(
            "mutation.apply",
            new JsonObject
            {
                ["conversationId"] = "sample-complete",
                ["approvalId"] = approvalId,
                ["approvalNonce"] = approvalNonce
            });

        Assert.Equal("completed", apply["status"]!.GetValue<string>());
        Assert.Equal("completed", apply["result"]!["newStatus"]!.GetValue<string>());

        var updatedRoot = await client.GetFromJsonAsync<JsonObject>("/");
        Assert.NotNull(updatedRoot);
        Assert.NotEqual("task-1", updatedRoot!["sampleIds"]!["openTask"]!.GetValue<string>());
    }

    [Fact]
    public async Task SampleServerShowsRejectedMutationForCompletedTask()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var rawClient = new SampleRawMcpClient(client);

        await rawClient.InitializeAsync();
        var preview = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "sample-failure",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "task-3" });
                           }
                           """
            });

        var approval = Assert.Single(preview["approvalsRequested"]!.AsArray());
        Assert.Contains("already completed", approval!["preview"]!["note"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        var apply = await rawClient.CallToolAsync(
            "mutation.apply",
            new JsonObject
            {
                ["conversationId"] = "sample-failure",
                ["approvalId"] = approval["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        Assert.Equal("failed", apply["status"]!.GetValue<string>());
        Assert.Equal("validation_failed", apply["failureCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task SampleServerSupportsMutationCancel()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var rawClient = new SampleRawMcpClient(client);

        await rawClient.InitializeAsync();
        var preview = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "sample-cancel",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "task-1" });
                           }
                           """
            });

        var approval = Assert.Single(preview["approvalsRequested"]!.AsArray());
        var approvalId = approval!["approvalId"]!.GetValue<string>();
        var approvalNonce = approval["approvalNonce"]!.GetValue<string>();

        var cancel = await rawClient.CallToolAsync(
            "mutation.cancel",
            new JsonObject
            {
                ["conversationId"] = "sample-cancel",
                ["approvalId"] = approvalId,
                ["approvalNonce"] = approvalNonce
            });

        Assert.Equal("cancelled", cancel["status"]!.GetValue<string>());

        var list = await rawClient.CallToolAsync(
            "mutation.list",
            new JsonObject
            {
                ["conversationId"] = "sample-cancel"
            });

        Assert.Empty(list["items"]!.AsArray());
    }

    [Fact]
    public async Task SampleServerAdvertisesAndReadsResources()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var rawClient = new SampleRawMcpClient(client);

        await rawClient.InitializeAsync();

        var list = await rawClient.ListResourcesAsync();
        var resources = list["resources"]!.AsArray()
            .Select(item => item!.AsObject())
            .OrderBy(static item => item["uri"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "sample://workspace/guide",
                "sample://workspace/projects"
            },
            resources.Select(static item => item["uri"]!.GetValue<string>()).ToArray());

        var guide = await rawClient.ReadResourceAsync("sample://workspace/guide");
        var guideContents = Assert.IsType<JsonObject>(Assert.Single(guide["contents"]!.AsArray()));
        Assert.Equal("sample://workspace/guide", guideContents["uri"]!.GetValue<string>());
        Assert.Equal("text/markdown", guideContents["mimeType"]!.GetValue<string>());
        Assert.Contains("Sample Workspace Guide", guideContents["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("programmatic.client.sample(...)", guideContents["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("tasks.summarizeWithSampling", guideContents["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var projects = await rawClient.ReadResourceAsync("sample://workspace/projects");
        var projectContents = Assert.IsType<JsonObject>(Assert.Single(projects["contents"]!.AsArray()));
        Assert.Equal("application/json", projectContents["mimeType"]!.GetValue<string>());
        Assert.Contains("project-alpha", projectContents["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SampleServerStatefulSdkPathAdvertisesTheFullSupportedSurface()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateSessionClientAsync(factory);

        Assert.Contains("/mcp/types", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("resources/list", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("programmatic.client.sample(...)", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("signed header", client.ServerInstructions, StringComparison.OrdinalIgnoreCase);

        var tools = await client.ListToolsAsync();
        Assert.Equal(
            new[]
            {
                "artifact.read",
                "capabilities.search",
                "code.execute",
                "mutation.apply",
                "mutation.cancel",
                "mutation.list"
            },
            tools.Select(tool => tool.ProtocolTool.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray());

        var resources = await client.ListResourcesAsync();
        Assert.Equal(
            new[]
            {
                "sample://workspace/guide",
                "sample://workspace/projects"
            },
            resources.Select(static resource => resource.Uri).OrderBy(static uri => uri, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SampleServerJsSamplingEnforcesExplicitReadOnlyScopeRules()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateSessionClientAsync(
            factory,
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "sample-test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var missingScope = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "sample-js-sampling-no-scope",
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_requires_explicit_read_only_scope", ExtractStringResult(missingScope));

        var visibleMutation = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "sample-js-sampling-mutation-scope",
                ["visibleApiPaths"] = new[] { "tasks.complete" },
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_not_allowed_with_visible_mutations", ExtractStringResult(visibleMutation));
    }

    [Fact]
    public async Task SampleServerSupportsStatefulJsSamplingAndCapabilityHandlerSampling()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateSessionClientAsync(
            factory,
            CreateSamplingClientOptions(
                request =>
                {
                    if (request.Messages.Count == 1)
                    {
                        var userText = Assert.IsType<TextContentBlock>(Assert.Single(request.Messages[0].Content)).Text;
                        var taskId = userText.Contains("task-4", StringComparison.Ordinal) ? "task-4" : "task-1";

                        return ValueTask.FromResult(
                            new CreateMessageResult
                            {
                                Role = Role.Assistant,
                                Model = "sample-test-model",
                                StopReason = "tool_use",
                                Content =
                                [
                                    new ToolUseContentBlock
                                    {
                                        Id = $"tool-{taskId}",
                                        Name = "tasks.readForSampling",
                                        Input = JsonSerializer.SerializeToElement(new { taskId })
                                    }
                                ]
                            });
                    }

                    var toolResult = Assert.IsType<ToolResultContentBlock>(Assert.Single(request.Messages[^1].Content));
                    var resultText = Assert.IsType<TextContentBlock>(Assert.Single(toolResult.Content)).Text;
                    var task = JsonSerializer.Deserialize<TaskDetailsResult>(resultText, JsonSerializerOptions.Web);
                    Assert.NotNull(task);

                    return ValueTask.FromResult(
                        new CreateMessageResult
                        {
                            Role = Role.Assistant,
                            Model = "sample-test-model",
                            StopReason = "endTurn",
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = $"{task!.TaskId} in {task.ProjectName} is {task.Status}: {task.Title}."
                                }
                            ]
                        });
                }));

        Assert.Contains("programmatic.client.sample(...)", client.ServerInstructions, StringComparison.Ordinal);

        var jsSampling = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "sample-js-sampling",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               return await programmatic.client.sample({
                                   messages: [{ role: "user", text: "Summarize task task-1 for the sample flow." }],
                                   enableTools: true,
                                   allowedToolNames: ["tasks.readForSampling"]
                               });
                           }
                           """
            });

        var jsResult = ExtractStringResult(jsSampling);
        Assert.Contains("task-1", jsResult, StringComparison.Ordinal);
        Assert.Contains("open", jsResult, StringComparison.Ordinal);

        var handlerSampling = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "sample-handler-sampling",
                ["visibleApiPaths"] = new[] { "tasks.summarizeWithSampling" },
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.summarizeWithSampling({ taskId: "task-4" });
                           }
                           """
            });

        var handlerPayload = ParseStructuredContent(handlerSampling);
        var handlerResult = ExtractObjectResult(handlerPayload);
        Assert.Equal("task-4", handlerResult["taskId"]!.GetValue<string>());
        Assert.Contains("task-4", handlerResult["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("open", handlerResult["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(
                builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.ConfigureServices(
                        services =>
                            services.PostConfigure<ProgrammaticMcpServerOptions>(
                                options => options.ExecutorOptions = options.ExecutorOptions with { MemoryBytes = 64 * 1024 * 1024 }));
                });
    }

    private static async Task<McpClient> CreateSessionClientAsync(WebApplicationFactory<Program> factory, McpClientOptions? options = null)
    {
        var httpClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, options ?? new McpClientOptions());
    }

    private static McpClientOptions CreateSamplingClientOptions(
        Func<CreateMessageRequestParams, ValueTask<CreateMessageResult>> handler,
        bool supportsToolUse = true)
    {
        return new McpClientOptions
        {
            Capabilities = new ClientCapabilities
            {
                Sampling = new SamplingCapability
                {
                    Tools = supportsToolUse ? new SamplingToolsCapability() : null
                }
            },
            Handlers = new McpClientHandlers
            {
                SamplingHandler = (request, _, _) => handler(request!)
            }
        };
    }

    private static JsonObject ParseStructuredContent(CallToolResult result)
    {
        if (result.StructuredContent is JsonElement element)
        {
            return JsonNode.Parse(element.GetRawText())!.AsObject();
        }

        var mirroredText = result.Content
            .OfType<TextContentBlock>()
            .Select(static block => block.Text)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(mirroredText))
        {
            return JsonNode.Parse(mirroredText)!.AsObject();
        }

        throw new Xunit.Sdk.XunitException("Structured content was not available on the tool result.");
    }

    private static string ExtractStringResult(CallToolResult result)
    {
        var payload = ParseStructuredContent(result);
        if (TryExtractString(payload["result"], out var resultText))
        {
            return resultText;
        }

        if (TryExtractString(payload["error"]?["code"], out var errorCode))
        {
            return errorCode;
        }

        throw new Xunit.Sdk.XunitException($"A string result was not available on the tool result. Payload: {payload.ToJsonString()}");
    }

    private static JsonObject ExtractObjectResult(JsonObject payload)
    {
        if (payload["result"] is JsonObject resultObject)
        {
            return resultObject;
        }

        if (payload["result"] is JsonValue resultValue
            && resultValue.TryGetValue<string>(out var resultText)
            && JsonNode.Parse(resultText) is JsonObject parsedResult)
        {
            return parsedResult;
        }

        if (payload["taskId"] is not null && payload["summary"] is not null)
        {
            return payload;
        }

        throw new Xunit.Sdk.XunitException($"An object result was not available on the tool result. Payload: {payload.ToJsonString()}");
    }

    private static bool TryExtractString(JsonNode? node, out string value)
    {
        switch (node)
        {
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out value!):
                return true;
            case JsonObject jsonObject when jsonObject["text"] is JsonValue textValue && textValue.TryGetValue<string>(out value!):
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private sealed class SampleRawMcpClient(HttpClient client)
    {
        private int _nextId = 1;
        private string _protocolVersion = "2024-11-05";
        private string? _sessionId;
        private readonly string? _defaultOrigin = client.BaseAddress is not null
            ? new Uri(client.BaseAddress, "/").GetLeftPart(UriPartial.Authority)
            : null;

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "sample-test",
                        ["version"] = "1.0.0"
                    }
                },
                cancellationToken);

            Assert.Null(response["error"]);
        }

        public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(
                "tools/call",
                new JsonObject
                {
                    ["name"] = name,
                    ["arguments"] = arguments
                },
                cancellationToken);

            Assert.Null(response["error"]);
            return ParseStructuredContent(response["result"]!.AsObject());
        }

        public async Task<JsonObject> ListResourcesAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync("resources/list", new JsonObject(), cancellationToken);
            Assert.Null(response["error"]);
            return response["result"]!.AsObject();
        }

        public async Task<JsonObject> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(
                "resources/read",
                new JsonObject
                {
                    ["uri"] = uri
                },
                cancellationToken);

            Assert.Null(response["error"]);
            return response["result"]!.AsObject();
        }

        private async Task<JsonObject> SendAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = _nextId++,
                        ["method"] = method,
                        ["params"] = parameters
                    }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json")
            };

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                request.Headers.TryAddWithoutValidation(ProgrammaticMcpServerOptions.McpSessionIdHeaderName, _sessionId);
            }
            if (!string.IsNullOrWhiteSpace(_defaultOrigin))
            {
                request.Headers.TryAddWithoutValidation("Origin", _defaultOrigin);
            }
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = ParseResponseBody(response, body);
            if (method == "initialize" && json["result"]?["protocolVersion"] is JsonValue protocolVersion)
            {
                _protocolVersion = protocolVersion.GetValue<string>();
            }

            if (response.Headers.TryGetValues(ProgrammaticMcpServerOptions.McpSessionIdHeaderName, out var sessionValues))
            {
                _sessionId = sessionValues.LastOrDefault();
            }

            return json;
        }

        private static JsonObject ParseStructuredContent(JsonObject result)
        {
            if (result["structuredContent"] is JsonObject structuredObject)
            {
                return structuredObject;
            }

            if (result["structuredContent"] is JsonValue structuredValue
                && JsonNode.Parse(structuredValue.GetValue<string>()) is JsonObject parsed)
            {
                return parsed;
            }

            throw new Xunit.Sdk.XunitException("Expected structuredContent in MCP tool result.");
        }

        private static JsonObject ParseResponseBody(HttpResponseMessage response, string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return new JsonObject();
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var dataLines = body
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(static line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(static line => line["data:".Length..].Trim())
                    .Where(static line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();

                if (dataLines.Length == 0)
                {
                    throw new InvalidOperationException("No data payload was found in the event-stream response.");
                }

                body = dataLines[^1];
            }

            return JsonNode.Parse(body)!.AsObject();
        }
    }
}
