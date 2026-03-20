using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
                "tasks.list"
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

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
    }

    private sealed class SampleRawMcpClient(HttpClient client)
    {
        private int _nextId = 1;
        private string _protocolVersion = "2024-11-05";
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
