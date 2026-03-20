using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProgrammaticMcp.AspNetCore;

namespace ProgrammaticMcp.AspNetCore.Tests;

public sealed class ProgrammaticMcpAspNetCoreIntegrationTests
{
    [Fact]
    public async Task InitializeAndToolsListAdvertiseTheProgrammaticSurface()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync();

        Assert.Contains("/mcp/types", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("maxCodeBytes", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("cookie", client.ServerInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("^[A-Za-z0-9._:-]{1,128}$", client.ServerInstructions, StringComparison.Ordinal);

        var tools = await client.ListToolsAsync();
        var names = tools.Select(tool => tool.ProtocolTool.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray();

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
            names);
    }

    [Fact]
    public async Task ResourcesAreAdvertisedSeparatelyAndReadableOnStatefulAndRawHttpPaths()
    {
        static void AddResources(ProgrammaticMcpBuilder catalog)
        {
            catalog.AddResource(
                "test://docs/guide",
                resource => resource
                    .WithName("Guide")
                    .WithDescription("A markdown guide resource.")
                    .WithMimeType("text/markdown")
                    .WithText("# Guide\n\nUse resources for supplemental context."));

            catalog.AddResource(
                "test://docs/projects",
                resource => resource
                    .WithName("Projects")
                    .WithDescription("A JSON project snapshot.")
                    .WithMimeType("application/json")
                    .WithText("""{"items":[{"id":"project-alpha"}]}"""));
        }

        await using var statefulHost = await ProgrammaticMcpTestHost.StartAsync(configureCatalog: AddResources);
        await using var sdkClient = await statefulHost.CreateSessionClientAsync();
        Assert.Contains("resources/list", sdkClient.ServerInstructions, StringComparison.Ordinal);

        var tools = await sdkClient.ListToolsAsync();
        var names = tools.Select(tool => tool.ProtocolTool.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
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
            names);

        var sdkResources = await sdkClient.ListResourcesAsync();
        Assert.Equal(
            new[]
            {
                "test://docs/guide",
                "test://docs/projects"
            },
            sdkResources.Select(static resource => resource.Uri).OrderBy(static value => value, StringComparer.Ordinal).ToArray());

        var sdkRead = await sdkResources.Single(static resource => resource.Uri == "test://docs/guide").ReadAsync();
        var sdkContent = Assert.IsType<TextResourceContents>(Assert.Single(sdkRead.Contents));
        Assert.Equal("test://docs/guide", sdkContent.Uri);
        Assert.Equal("text/markdown", sdkContent.MimeType);
        Assert.Contains("Guide", sdkContent.Text, StringComparison.Ordinal);

        await using var statelessHost = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: AddResources,
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        await using var rawClient = statelessHost.CreateRawClient();
        await rawClient.InitializeAsync();

        var list = await rawClient.ListResourcesAsync();
        Assert.Equal(
            new[]
            {
                "test://docs/guide",
                "test://docs/projects"
            },
            list.Result["resources"]!.AsArray()
                .Select(item => item!["uri"]!.GetValue<string>())
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray());

        var read = await rawClient.ReadResourceAsync("test://docs/guide");
        var content = Assert.IsType<JsonObject>(Assert.Single(read.Result["contents"]!.AsArray()));
        Assert.Equal("test://docs/guide", content["uri"]!.GetValue<string>());
        Assert.Equal("text/markdown", content["mimeType"]!.GetValue<string>());
        Assert.Contains("Guide", content["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingResourcesReturnNotFoundAndEmptyCatalogsListNoResources()
    {
        const string MissingUri = "test://docs/missing";

        await using var statefulHost = await ProgrammaticMcpTestHost.StartAsync();
        await using var sdkClient = await statefulHost.CreateSessionClientAsync();

        Assert.DoesNotContain("resources/list", sdkClient.ServerInstructions, StringComparison.Ordinal);
        Assert.Empty(await sdkClient.ListResourcesAsync());

        var sdkException = await Assert.ThrowsAsync<McpProtocolException>(async () => await sdkClient.ReadResourceAsync(MissingUri));
        Assert.Equal(McpErrorCode.ResourceNotFound, sdkException.ErrorCode);
        Assert.Contains("not found", sdkException.Message, StringComparison.OrdinalIgnoreCase);

        await using var statelessHost = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        await using var rawClient = statelessHost.CreateRawClient();
        await rawClient.InitializeAsync();

        var list = await rawClient.ListResourcesAsync();
        Assert.Empty(list.Result["resources"]!.AsArray());

        var read = await rawClient.ReadResourceAsync(MissingUri);
        Assert.True(read.IsError);
        var error = Assert.IsType<JsonObject>(read.Body["error"]);
        Assert.Equal((int)McpErrorCode.ResourceNotFound, error["code"]!.GetValue<int>());
        Assert.Contains("not found", error["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeInstructionsReflectConfiguredCallerBindingModes()
    {
        await using var cookieOnlyHost = await ProgrammaticMcpTestHost.StartAsync();
        await using var cookieOnlyClient = await cookieOnlyHost.CreateSessionClientAsync();
        Assert.Contains("built-in HTTP fallback via cookie", cookieOnlyClient.ServerInstructions, StringComparison.Ordinal);

        await using var signedHeaderHost = await ProgrammaticMcpTestHost.StartAsync(
            enableSignedHeader: true,
            configureOptions: options => options.EnableCookieCallerBinding = false);
        await using var signedHeaderClient = await signedHeaderHost.CreateSessionClientAsync();
        Assert.Contains("built-in HTTP fallback via signed header", signedHeaderClient.ServerInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie or signed header", signedHeaderClient.ServerInstructions, StringComparison.Ordinal);

        await using var noFallbackHost = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options =>
            {
                options.EnableCookieCallerBinding = false;
                options.EnableSignedHeaderCallerBinding = false;
            });
        await using var noFallbackClient = await noFallbackHost.CreateSessionClientAsync();
        Assert.Contains("no built-in HTTP fallback is enabled", noFallbackClient.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeInstructionsAdvertiseSamplingOnlyOnStatefulTransport()
    {
        await using var statefulHost = await ProgrammaticMcpTestHost.StartAsync();
        await using var statefulClient = await statefulHost.CreateSessionClientAsync();
        Assert.Contains("MCP sampling is optional", statefulClient.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("programmatic.client.sample(...)", statefulClient.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("VisibleApiPaths", statefulClient.ServerInstructions, StringComparison.Ordinal);

        await using var statelessHost = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        await using var statelessClient = await statelessHost.CreateSdkClientAsync(includeDefaultOrigin: true);
        Assert.DoesNotContain("programmatic.client.sample(...)", statelessClient.ServerInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("MCP sampling is optional", statelessClient.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypesEndpointReturnsDeclarationsAndCacheValidators()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        using var client = host.CreatePlainHttpClient();

        var response = await client.GetAsync(host.TypesPath);
        var etag = response.Headers.ETag;
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/typescript", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(etag);
        Assert.Contains("type TasksCompleteResult =", body, StringComparison.Ordinal);

        using var revalidation = new HttpRequestMessage(HttpMethod.Get, host.TypesPath);
        revalidation.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag!.Tag));
        var notModified = await client.SendAsync(revalidation);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    [Fact]
    public async Task TypesEndpointInheritsAuthorizationFromRouteGroup()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            mcpPath: "/secured/mcp",
            configureServices: services =>
            {
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
                services.AddAuthorization();
            },
            configureApp: (app, _) =>
            {
                app.UseAuthentication();
                app.UseAuthorization();

                var group = app.MapGroup("/secured");
                group.RequireAuthorization();
                group.MapProgrammaticMcpServer("/mcp");
            });

        using var unauthorizedClient = host.CreatePlainHttpClient();
        var unauthorized = await unauthorizedClient.GetAsync(host.TypesPath);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var authorizedClient = host.CreatePlainHttpClient();
        authorizedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "allowed");
        var authorized = await authorizedClient.GetAsync(host.TypesPath);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Fact]
    public async Task RouteGroupPrefixIsAdvertisedAndUsedForCallerBindingCookies()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            mcpPath: "/secured/mcp",
            configureApp: (app, _) =>
            {
                var group = app.MapGroup("/secured");
                group.MapProgrammaticMcpServer("/mcp");
            });

        await using var sessionClient = await host.CreateSessionClientAsync();
        Assert.Contains("Types: GET /secured/mcp/types", sessionClient.ServerInstructions, StringComparison.Ordinal);

        await using var rawClient = host.CreateRawClient();
        var initialize = await rawClient.InitializeAsync();
        Assert.True(initialize.HttpResponse.Headers.TryGetValues("Set-Cookie", out var setCookieValues));
        Assert.Contains(
            setCookieValues!,
            static value => value.Contains("Path=/secured/mcp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TypesEndpointHandlesEmptyCatalogAndGenerationFailures()
    {
        await using var emptyHost = await ProgrammaticMcpTestHost.StartAsync(includeDefaultCatalog: false);
        using var emptyClient = emptyHost.CreatePlainHttpClient();
        var emptyResponse = await emptyClient.GetAsync(emptyHost.TypesPath);
        var emptyBody = await emptyResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        Assert.Contains("declare namespace programmatic", emptyBody, StringComparison.Ordinal);

        await using var failingHost = await ProgrammaticMcpTestHost.StartAsync(
            includeDefaultCatalog: false,
            configureServices: services =>
            {
                services.AddSingleton<ICapabilityCatalog>(new ThrowingCatalog());
            });
        using var failingClient = failingHost.CreatePlainHttpClient();
        var failingResponse = await failingClient.GetAsync(failingHost.TypesPath);
        var failingBody = await failingResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, failingResponse.StatusCode);
        Assert.Contains("Type declaration generation failed.", failingBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPagingValidationAndSdkExecutionWork()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync();

        var searchPage1 = await client.CallToolAsync(
            "capabilities.search",
            new Dictionary<string, object?>
            {
                ["query"] = "tasks",
                ["detailLevel"] = "Full",
                ["limit"] = 1
            });

        Assert.False(searchPage1.IsError ?? false);
        var page1 = ParseStructuredContent(searchPage1);
        Assert.Single(page1["items"]!.AsArray());
        Assert.NotNull(page1["nextCursor"]);

        var searchPage2 = await client.CallToolAsync(
            "capabilities.search",
            new Dictionary<string, object?>
            {
                ["query"] = "tasks",
                ["detailLevel"] = "Full",
                ["limit"] = 1,
                ["cursor"] = page1["nextCursor"]!.GetValue<string>()
            });

        var page2 = ParseStructuredContent(searchPage2);
        Assert.Single(page2["items"]!.AsArray());
        Assert.NotEqual(
            page1["items"]!.AsArray()[0]!["apiPath"]!.GetValue<string>(),
            page2["items"]!.AsArray()[0]!["apiPath"]!.GetValue<string>());

        var invalidSearch = await client.CallToolAsync(
            "capabilities.search",
            new Dictionary<string, object?>
            {
                ["query"] = new string('x', 600)
            });

        Assert.True(invalidSearch.IsError ?? false);
        Assert.Equal("invalid_params", ParseStructuredContent(invalidSearch)["error"]!["code"]!.GetValue<string>());

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-search-execute",
                ["code"] = """
                           async function main() {
                               return await programmatic.math.double({ value: 21 });
                           }
                           """
            });

        Assert.False(execute.IsError ?? false);
        var executeNode = ParseStructuredContent(execute);
        Assert.Equal(42, executeNode["result"]!.GetValue<int>());
        Assert.True(executeNode["stats"]!["apiCalls"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task StatefulSamplingSupportsJsToolLoopWithARealSessionBackedClient()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, ReadClockResult>(
                    "clock.read",
                    tool => tool
                        .WithDescription("Reads the current clock.")
                        .WithHandler((_, _) => ValueTask.FromResult(new ReadClockResult("09:30 UTC"))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                request =>
                {
                    if (request.Messages.Count == 1)
                    {
                        return ValueTask.FromResult(
                            new CreateMessageResult
                            {
                                Role = Role.Assistant,
                                Model = "test-model",
                                StopReason = "tool_use",
                                Content =
                                [
                                    new ToolUseContentBlock
                                    {
                                        Id = "tool-1",
                                        Name = "clock.read",
                                        Input = JsonSerializer.SerializeToElement(new { timeZone = "UTC" })
                                    }
                                ]
                            });
                    }

                    var finalMessage = request.Messages[^1];
                    var toolResult = Assert.IsType<ToolResultContentBlock>(Assert.Single(finalMessage.Content));
                    var resultText = Assert.IsType<TextContentBlock>(Assert.Single(toolResult.Content)).Text;
                    Assert.Contains("09:30 UTC", resultText, StringComparison.Ordinal);

                    return ValueTask.FromResult(
                        new CreateMessageResult
                        {
                            Role = Role.Assistant,
                            Model = "test-model",
                            StopReason = "endTurn",
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = "The clock says 09:30 UTC."
                                }
                            ]
                        });
                }));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-js-tools",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               return await programmatic.client.sample({
                                   messages: [{ role: "user", text: "What time is it?" }],
                                   enableTools: true,
                                   allowedToolNames: ["clock.read"]
                               });
                           }
                           """
            });

        Assert.False(execute.IsError ?? false, execute.StructuredContent?.GetRawText());
        Assert.Equal("The clock says 09:30 UTC.", ExtractStringResult(execute));
    }

    [Fact]
    public async Task StatefulSamplingRequiresAnExplicitReadOnlyScope()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSdkClientAsync(
            includeDefaultOrigin: true,
            options: CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-scope",
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

        Assert.Equal("sampling_requires_explicit_read_only_scope", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task StatefulSamplingBlocksVisibleMutations()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-mutations",
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

        Assert.Equal("sampling_not_allowed_with_visible_mutations", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task StatelessSamplingIsUnavailableEvenWhenTheClientAdvertisesSampling()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        var cookieContainer = new CookieContainer();
        await using var client = await host.CreateSdkClientAsync(
            cookieContainer: cookieContainer,
            includeDefaultOrigin: true,
            options: CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-stateless",
                ["visibleApiPaths"] = Array.Empty<string>(),
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

        var executeNode = ParseStructuredContent(execute);
        Assert.Equal("sampling_unavailable", executeNode["result"]?.GetValue<string>() ?? executeNode.ToJsonString());
    }

    [Fact]
    public async Task CapabilityHandlersCanUseSamplingInsideAnExplicitReadOnlyExecutionScope()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddCapability<EmptyInput, SamplingEnvelope>(
                    "diag.askClient",
                    capability => capability
                        .WithDescription("Asks the connected client for a sample.")
                        .UseWhen("You need to validate capability-handler sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                var sample = await context.GetSamplingClient().CreateMessageAsync(
                                    new ProgrammaticSamplingRequest(
                                        null,
                                        [new ProgrammaticSamplingMessage("user", "Hello from handler")]),
                                    context.CancellationToken);
                                return new SamplingEnvelope(sample.Text);
                            }));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                request => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "Handler sample response" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-capability",
                ["visibleApiPaths"] = new[] { "diag.askClient" },
                ["code"] = """
                           async function main() {
                               return await programmatic.diag.askClient({});
                           }
                           """
            });

        Assert.Equal("Handler sample response", ParseStructuredContent(execute)["result"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityHandlersRequireAnExplicitReadOnlyScopeForSampling()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddCapability<EmptyInput, SamplingEnvelope>(
                    "diag.askClient",
                    capability => capability
                        .WithDescription("Asks the connected client for a sample.")
                        .UseWhen("You need to validate capability-handler sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                try
                                {
                                    var sample = await context.GetSamplingClient().CreateMessageAsync(
                                        new ProgrammaticSamplingRequest(
                                            null,
                                            [new ProgrammaticSamplingMessage("user", "Hello from handler")]),
                                        context.CancellationToken);
                                    return new SamplingEnvelope(sample.Text);
                                }
                                catch (ProgrammaticSamplingException exception)
                                {
                                    return new SamplingEnvelope(exception.Code);
                                }
                            }));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-capability-no-scope",
                ["code"] = """
                           async function main() {
                               return await programmatic.diag.askClient({});
                           }
                           """
            });

        Assert.Equal("sampling_requires_explicit_read_only_scope", ParseStructuredContent(execute)["result"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityHandlersBlockSamplingWhenVisibleMutationsArePresent()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddCapability<EmptyInput, SamplingEnvelope>(
                    "diag.askClient",
                    capability => capability
                        .WithDescription("Asks the connected client for a sample.")
                        .UseWhen("You need to validate capability-handler sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                try
                                {
                                    var sample = await context.GetSamplingClient().CreateMessageAsync(
                                        new ProgrammaticSamplingRequest(
                                            null,
                                            [new ProgrammaticSamplingMessage("user", "Hello from handler")]),
                                        context.CancellationToken);
                                    return new SamplingEnvelope(sample.Text);
                                }
                                catch (ProgrammaticSamplingException exception)
                                {
                                    return new SamplingEnvelope(exception.Code);
                                }
                            }));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-capability-mutation-scope",
                ["visibleApiPaths"] = new[] { "diag.askClient", "tasks.complete" },
                ["code"] = """
                           async function main() {
                               return await programmatic.diag.askClient({});
                           }
                           """
            });

        Assert.Equal("sampling_not_allowed_with_visible_mutations", ParseStructuredContent(execute)["result"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResourceReadersReceiveABlockedSamplingClient()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddResource(
                    "test://sampling/resource",
                    resource => resource
                        .WithName("Sampling Block")
                        .WithDescription("Reports the resource-context sampling error.")
                        .WithMimeType("text/plain")
                        .WithReader(
                            async context =>
                            {
                                try
                                {
                                    await ProgrammaticSamplingServiceResolver.ResolvePublic(context.Services).CreateMessageAsync(
                                        new ProgrammaticSamplingRequest(null, [new ProgrammaticSamplingMessage("user", "Hello")]),
                                        context.CancellationToken);
                                    return "unexpected";
                                }
                                catch (ProgrammaticSamplingException exception)
                                {
                                    return exception.Code;
                                }
                            }));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var read = await client.ReadResourceAsync("test://sampling/resource");
        var content = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("sampling_not_allowed_in_resource_context", content.Text);
    }

    [Fact]
    public async Task ResourceReadersBlockPublicAndStructuredSamplingAcrossChildScopes()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddResource(
                    "test://sampling/inspection",
                    resource => resource
                        .WithName("Sampling Inspection")
                        .WithDescription("Reports the sampling clients visible during resource execution.")
                        .WithMimeType("application/json")
                        .WithReader(async context => JsonSerializer.Serialize(await InspectSamplingScopeAsync(context.Services, context.CancellationToken))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var read = await client.ReadResourceAsync("test://sampling/inspection");
        var content = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        var inspection = JsonSerializer.Deserialize<SamplingScopeInspection>(content.Text)!;

        Assert.True(inspection.PublicIsSupported);
        Assert.False(inspection.PublicSupportsToolUse);
        Assert.True(inspection.StructuredIsSupported);
        Assert.False(inspection.StructuredSupportsToolUse);
        Assert.True(inspection.PublicSameInChildScope);
        Assert.True(inspection.StructuredSameInChildScope);
        Assert.Equal("sampling_not_allowed_in_resource_context", inspection.PublicCode);
        Assert.Equal("sampling_not_allowed_in_resource_context", inspection.StructuredCode);
    }

    [Fact]
    public async Task MutationApplyHandlersReceiveABlockedSamplingClient()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddMutation<CompleteTaskArgs, CompleteTaskPreview, SamplingCodeResult>(
                    "tasks.sampleBlocked",
                    mutation => mutation
                        .WithDescription("Reports the mutation-context sampling error.")
                        .UseWhen("You need to validate mutation-context sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithPreviewHandler((input, _) => ValueTask.FromResult(new CompleteTaskPreview(input.TaskId, true)))
                        .WithSummaryFactory((input, _, _) => ValueTask.FromResult("Complete " + input.TaskId))
                        .WithApplyHandler(
                            async (_, context) =>
                            {
                                try
                                {
                                    await ProgrammaticSamplingServiceResolver.ResolvePublic(context.Services).CreateMessageAsync(
                                        new ProgrammaticSamplingRequest(null, [new ProgrammaticSamplingMessage("user", "Hello")]),
                                        context.CancellationToken);
                                    return MutationApplyResult<SamplingCodeResult>.Success(new SamplingCodeResult("unexpected"));
                                }
                                catch (ProgrammaticSamplingException exception)
                                {
                                    return MutationApplyResult<SamplingCodeResult>.Success(new SamplingCodeResult(exception.Code));
                                }
                            }));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var preview = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-mutation-sampling-blocked",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.sampleBlocked({ taskId: "task-1" });
                           }
                           """
            });
        var approval = Assert.Single(ParseStructuredContent(preview)["approvalsRequested"]!.AsArray());

        var apply = await client.CallToolAsync(
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-mutation-sampling-blocked",
                ["approvalId"] = approval!["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        Assert.Equal("sampling_not_allowed_in_mutation_context", ParseStructuredContent(apply)["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task MutationApplyHandlersBlockPublicAndStructuredSamplingAcrossChildScopes()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddMutation<CompleteTaskArgs, CompleteTaskPreview, SamplingScopeInspection>(
                    "tasks.inspectSampling",
                    mutation => mutation
                        .WithDescription("Reports the sampling clients visible during mutation apply.")
                        .UseWhen("You need to validate mutation-context sampling.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithPreviewHandler((input, _) => ValueTask.FromResult(new CompleteTaskPreview(input.TaskId, true)))
                        .WithSummaryFactory((input, _, _) => ValueTask.FromResult("Inspect " + input.TaskId))
                        .WithApplyHandler(
                            async (_, context) => MutationApplyResult<SamplingScopeInspection>.Success(await InspectSamplingScopeAsync(context.Services, context.CancellationToken))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var preview = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-mutation-sampling-inspection",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.inspectSampling({ taskId: "task-1" });
                           }
                           """
            });
        var approval = Assert.Single(ParseStructuredContent(preview)["approvalsRequested"]!.AsArray());

        var apply = await client.CallToolAsync(
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-mutation-sampling-inspection",
                ["approvalId"] = approval!["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        var inspectionNode = ParseStructuredContent(apply)["result"]!;
        Assert.True(inspectionNode["publicIsSupported"]!.GetValue<bool>());
        Assert.False(inspectionNode["publicSupportsToolUse"]!.GetValue<bool>());
        Assert.True(inspectionNode["structuredIsSupported"]!.GetValue<bool>());
        Assert.False(inspectionNode["structuredSupportsToolUse"]!.GetValue<bool>());
        Assert.True(inspectionNode["publicSameInChildScope"]!.GetValue<bool>());
        Assert.True(inspectionNode["structuredSameInChildScope"]!.GetValue<bool>());
        Assert.Equal("sampling_not_allowed_in_mutation_context", inspectionNode["publicCode"]!.GetValue<string>());
        Assert.Equal("sampling_not_allowed_in_mutation_context", inspectionNode["structuredCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingToolHandlersCannotStartNestedSamplingRequests()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, ReadClockResult>(
                    "clock.reentrant",
                    tool => tool
                        .WithDescription("Attempts to start a nested sampling request.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                await ProgrammaticSamplingServiceResolver.ResolvePublic(context.Services).CreateMessageAsync(
                                    new ProgrammaticSamplingRequest(null, [new ProgrammaticSamplingMessage("user", "nested")]),
                                    context.CancellationToken);
                                return new ReadClockResult("unexpected");
                            }));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                request =>
                {
                    if (request.Messages.Count == 1)
                    {
                        return ValueTask.FromResult(
                            new CreateMessageResult
                            {
                                Role = Role.Assistant,
                                Model = "test-model",
                                StopReason = "tool_use",
                                Content =
                                [
                                    new ToolUseContentBlock
                                    {
                                        Id = "tool-1",
                                        Name = "clock.reentrant",
                                        Input = JsonSerializer.SerializeToElement(new { timeZone = "UTC" })
                                    }
                                ]
                            });
                    }

                    return ValueTask.FromResult(
                        new CreateMessageResult
                        {
                            Role = Role.Assistant,
                            Model = "test-model",
                            StopReason = "endTurn",
                            Content = [new TextContentBlock { Text = "unused" }]
                        });
                }));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-reentry",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }],
                                       enableTools: true,
                                       allowedToolNames: ["clock.reentrant"]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_reentry_not_allowed", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingRequestSizeLimitIsEnforced()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.MaxSamplingRequestBytes = 128);
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-request-limit",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "__PAYLOAD__" }]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """.Replace("__PAYLOAD__", new string('x', 256), StringComparison.Ordinal)
            });

        Assert.Equal("sampling_request_too_large", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingToolResultSizeLimitIsEnforced()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.MaxSamplingToolResultBytes = 32,
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, LargeSamplingToolResult>(
                    "clock.large",
                    tool => tool
                        .WithDescription("Returns a large tool result.")
                        .WithHandler((_, _) => ValueTask.FromResult(new LargeSamplingToolResult(new string('x', 128)))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                request =>
                {
                    if (request.Messages.Count == 1)
                    {
                        return ValueTask.FromResult(
                            new CreateMessageResult
                            {
                                Role = Role.Assistant,
                                Model = "test-model",
                                StopReason = "tool_use",
                                Content =
                                [
                                    new ToolUseContentBlock
                                    {
                                        Id = "tool-1",
                                        Name = "clock.large",
                                        Input = JsonSerializer.SerializeToElement(new { timeZone = "UTC" })
                                    }
                                ]
                            });
                    }

                    return ValueTask.FromResult(
                        new CreateMessageResult
                        {
                            Role = Role.Assistant,
                            Model = "test-model",
                            StopReason = "endTurn",
                            Content = [new TextContentBlock { Text = "unused" }]
                        });
                }));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-tool-limit",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }],
                                       enableTools: true,
                                       allowedToolNames: ["clock.large"]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_tool_result_too_large", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingToolUseRequiresClientToolSupport()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, ReadClockResult>(
                    "clock.read",
                    tool => tool
                        .WithDescription("Reads the current clock.")
                        .WithHandler((_, _) => ValueTask.FromResult(new ReadClockResult("09:30 UTC"))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    }),
                supportsToolUse: false));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-tool-support",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }],
                                       enableTools: true,
                                       allowedToolNames: ["clock.read"]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_tool_use_unavailable", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingFailsWhenNoToolsAreAvailable()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-no-tools",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }],
                                       enableTools: true
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_no_tools_available", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingRejectsUnknownToolCalls()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, ReadClockResult>(
                    "clock.read",
                    tool => tool
                        .WithDescription("Reads the current clock.")
                        .WithHandler((_, _) => ValueTask.FromResult(new ReadClockResult("09:30 UTC"))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                request =>
                {
                    if (request.Messages.Count == 1)
                    {
                        return ValueTask.FromResult(
                            new CreateMessageResult
                            {
                                Role = Role.Assistant,
                                Model = "test-model",
                                StopReason = "tool_use",
                                Content =
                                [
                                    new ToolUseContentBlock
                                    {
                                        Id = "tool-1",
                                        Name = "clock.unknown",
                                        Input = JsonSerializer.SerializeToElement(new { timeZone = "UTC" })
                                    }
                                ]
                            });
                    }

                    return ValueTask.FromResult(
                        new CreateMessageResult
                        {
                            Role = Role.Assistant,
                            Model = "test-model",
                            StopReason = "endTurn",
                            Content = [new TextContentBlock { Text = "unused" }]
                        });
                }));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-invalid-tool-call",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }],
                                       enableTools: true,
                                       allowedToolNames: ["clock.read"]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_invalid_tool_call", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingReportsToolExecutionFailures()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, ReadClockResult>(
                    "clock.throw",
                    tool => tool
                        .WithDescription("Throws while executing.")
                        .WithHandler((_, _) => throw new InvalidOperationException("boom")));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                request =>
                {
                    if (request.Messages.Count == 1)
                    {
                        return ValueTask.FromResult(
                            new CreateMessageResult
                            {
                                Role = Role.Assistant,
                                Model = "test-model",
                                StopReason = "tool_use",
                                Content =
                                [
                                    new ToolUseContentBlock
                                    {
                                        Id = "tool-1",
                                        Name = "clock.throw",
                                        Input = JsonSerializer.SerializeToElement(new { timeZone = "UTC" })
                                    }
                                ]
                            });
                    }

                    return ValueTask.FromResult(
                        new CreateMessageResult
                        {
                            Role = Role.Assistant,
                            Model = "test-model",
                            StopReason = "endTurn",
                            Content = [new TextContentBlock { Text = "unused" }]
                        });
                }));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-tool-execution-failed",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }],
                                       enableTools: true,
                                       allowedToolNames: ["clock.throw"]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_tool_execution_failed", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingRoundLimitIsEnforced()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.MaxSamplingRounds = 1,
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, ReadClockResult>(
                    "clock.read",
                    tool => tool
                        .WithDescription("Reads the current clock.")
                        .WithHandler((_, _) => ValueTask.FromResult(new ReadClockResult("09:30 UTC"))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "tool_use",
                        Content =
                        [
                            new ToolUseContentBlock
                            {
                                Id = "tool-1",
                                Name = "clock.read",
                                Input = JsonSerializer.SerializeToElement(new { timeZone = "UTC" })
                            }
                        ]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-round-limit",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hello" }],
                                       enableTools: true,
                                       allowedToolNames: ["clock.read"]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_round_limit_exceeded", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingFailsWhenTheFinalAssistantResponseHasNoText()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content =
                        [
                            new ToolResultContentBlock
                            {
                                ToolUseId = "tool-1",
                                Content = [new TextContentBlock { Text = "tool-only result" }]
                            }
                        ]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-no-final-text",
                ["visibleApiPaths"] = Array.Empty<string>(),
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

        Assert.Equal("sampling_failed", ExtractStringResult(execute));
    }

    [Fact]
    public async Task SamplingJoinsMultipleFinalTextBlocks()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content =
                        [
                            new TextContentBlock { Text = "First paragraph." },
                            new TextContentBlock { Text = "Second paragraph." }
                        ]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-text-join",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               return await programmatic.client.sample({
                                   messages: [{ role: "user", text: "Hello" }]
                               });
                           }
                           """
            });

        Assert.Equal("First paragraph.\n\nSecond paragraph.", ExtractStringResult(execute));
    }

    [Fact]
    public async Task SamplingRequestSizeLimitCountsToolSchemas()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.MaxSamplingRequestBytes = 160,
            configureCatalog: catalog =>
            {
                catalog.AddSamplingTool<ReadClockInput, ReadClockResult>(
                    "clock.read",
                    tool => tool
                        .WithDescription("Reads the current clock.")
                        .WithHandler((_, _) => ValueTask.FromResult(new ReadClockResult("09:30 UTC"))));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-schema-limit",
                ["visibleApiPaths"] = Array.Empty<string>(),
                ["code"] = """
                           async function main() {
                               try {
                                   return await programmatic.client.sample({
                                       messages: [{ role: "user", text: "Hi" }],
                                       enableTools: true,
                                       allowedToolNames: ["clock.read"]
                                   });
                               } catch (error) {
                                   return error.code;
                               }
                           }
                           """
            });

        Assert.Equal("sampling_request_too_large", ParseStructuredContent(execute)["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityExecutionOverlaysShadowAmbientSamplingClientsAndPreserveChildScopes()
    {
        var ambient = new AmbientSamplingClient();

        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureServices: services =>
            {
                services.AddSingleton<IProgrammaticSamplingClient>(ambient);
                services.AddSingleton<IProgrammaticStructuredSamplingClient>(ambient);
            },
            configureCatalog: catalog =>
            {
                catalog.AddCapability<EmptyInput, SamplingIdentityInspection>(
                    "diag.inspectSampling",
                    capability => capability
                        .WithDescription("Inspects the visible sampling clients.")
                        .UseWhen("You need to validate sampling overlays.")
                        .DoNotUseWhen("You are not testing sampling.")
                        .WithHandler(
                            (_, context) =>
                            {
                                var publicClient = ProgrammaticSamplingServiceResolver.ResolvePublic(context.Services);
                                var structuredClient = ProgrammaticSamplingServiceResolver.ResolveStructured(context.Services);
                                using var childScope = context.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
                                var childPublic = ProgrammaticSamplingServiceResolver.ResolvePublic(childScope.ServiceProvider);
                                var childStructured = ProgrammaticSamplingServiceResolver.ResolveStructured(childScope.ServiceProvider);
                                return ValueTask.FromResult(
                                    new SamplingIdentityInspection(
                                        ReferenceEquals(publicClient, ambient),
                                        ReferenceEquals(structuredClient, ambient),
                                        ReferenceEquals(publicClient, childPublic),
                                        ReferenceEquals(structuredClient, childStructured),
                                        publicClient.IsSupported,
                                        publicClient.SupportsToolUse,
                                        structuredClient.IsSupported,
                                        structuredClient.SupportsToolUse));
                            }));
            });
        await using var client = await host.CreateSessionClientAsync(
            CreateSamplingClientOptions(
                _ => ValueTask.FromResult(
                    new CreateMessageResult
                    {
                        Role = Role.Assistant,
                        Model = "test-model",
                        StopReason = "endTurn",
                        Content = [new TextContentBlock { Text = "unused" }]
                    })));

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sampling-overlay-live",
                ["visibleApiPaths"] = new[] { "diag.inspectSampling" },
                ["code"] = """
                           async function main() {
                               return await programmatic.diag.inspectSampling({});
                           }
                           """
            });

        var inspection = ParseStructuredContent(execute)["result"]!;
        Assert.False(inspection["publicIsAmbient"]!.GetValue<bool>());
        Assert.False(inspection["structuredIsAmbient"]!.GetValue<bool>());
        Assert.True(inspection["publicSameInChildScope"]!.GetValue<bool>());
        Assert.True(inspection["structuredSameInChildScope"]!.GetValue<bool>());
        Assert.True(inspection["publicIsSupported"]!.GetValue<bool>());
        Assert.True(inspection["publicSupportsToolUse"]!.GetValue<bool>());
        Assert.True(inspection["structuredIsSupported"]!.GetValue<bool>());
        Assert.True(inspection["structuredSupportsToolUse"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ScopedServicesAreStableWithinARequestAndFreshAcrossRequests()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureServices: services => services.AddScoped<ScopeProbe>(),
            configureCatalog: catalog =>
            {
                catalog.AddCapability<EmptyInput, ScopeProbeResult>(
                    "diag.scope",
                    capability => capability
                        .WithDescription("Returns the current scoped service id.")
                        .UseWhen("You need to test scoping.")
                        .DoNotUseWhen("You are doing normal work.")
                        .WithHandler(
                            (_, context) =>
                            {
                                var probe = context.Services.GetRequiredService<ScopeProbe>();
                                return ValueTask.FromResult(new ScopeProbeResult(probe.ScopeId));
                            }));
            });
        await using var client = await host.CreateSessionClientAsync();

        var first = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-scope-1",
                ["code"] = """
                           async function main() {
                               const first = await programmatic.diag.scope({});
                               const second = await programmatic.diag.scope({});
                               return [first.scopeId, second.scopeId];
                           }
                           """
            });

        var second = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-scope-2",
                ["code"] = """
                           async function main() {
                               return await programmatic.diag.scope({});
                           }
                           """
            });

        Assert.False(first.IsError ?? false, first.StructuredContent?.GetRawText());
        Assert.False(second.IsError ?? false, second.StructuredContent?.GetRawText());
        var firstResult = ParseStructuredContent(first)["result"]!.AsArray();
        var secondResult = ParseStructuredContent(second)["result"]!.AsObject();

        Assert.Equal(firstResult[0]!.GetValue<string>(), firstResult[1]!.GetValue<string>());
        Assert.NotEqual(firstResult[0]!.GetValue<string>(), secondResult["scopeId"]!.GetValue<string>());
    }

    [Fact]
    public async Task RawCookieFallbackSetsRouteScopedCookieAndSupportsReconnectMutationFlow()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false);

        var cookieContainer = new CookieContainer();
        await using var rawClient = host.CreateRawClient(cookieContainer: cookieContainer);
        var initialize = await rawClient.InitializeAsync();
        var storedCookies = cookieContainer.GetCookies(new Uri(host.BaseAddress, host.McpPath)).Cast<Cookie>().ToArray();
        var cookie = Assert.Single(storedCookies);
        var setCookie = initialize.HttpResponse.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.SingleOrDefault()
            : null;

        Assert.Equal(ProgrammaticMcpServerOptions.DefaultCookieName, cookie.Name);
        Assert.Equal("/mcp", cookie.Path);
        Assert.True(cookie.HttpOnly);
        Assert.False(cookie.Secure);
        if (!string.IsNullOrWhiteSpace(setCookie))
        {
            Assert.Contains("SameSite=Lax", setCookie, StringComparison.OrdinalIgnoreCase);
        }

        var preview = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-cookie-fallback",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "cookie-task" });
                           }
                           """
            });

        var approvalItems = preview.StructuredContent["approvalsRequested"]?.AsArray();
        Assert.NotNull(approvalItems);
        var approval = Assert.Single(approvalItems!);
        var approvals = await host.Services.GetRequiredService<IApprovalStore>().ListAllAsync();
        var stored = Assert.Single(approvals);
        Assert.StartsWith("caller-", stored.CallerBindingId, StringComparison.Ordinal);

        await using var reconnectedClient = host.CreateRawClient(cookieContainer: cookieContainer);
        await reconnectedClient.InitializeAsync();
        var listed = await reconnectedClient.CallToolAsync(
            "mutation.list",
            new JsonObject
            {
                ["conversationId"] = "conv-cookie-fallback"
            });

        var item = Assert.Single(listed.StructuredContent["items"]!.AsArray());
        Assert.Equal(approval!["approvalId"]!.GetValue<string>(), item!["approvalId"]!.GetValue<string>());
        Assert.Null(item["approvalNonce"]);

        var apply = await reconnectedClient.CallToolAsync(
            "mutation.apply",
            new JsonObject
            {
                ["conversationId"] = "conv-cookie-fallback",
                ["approvalId"] = approval["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        Assert.Equal("completed", apply.StructuredContent["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task CookieFallbackKeepsSecureFlagForNonLoopbackHostHeaders()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options =>
            {
                options.EnableStatefulHttpTransport = false;
                options.AllowInsecureDevelopmentCookies = true;
            });

        await using var rawClient = host.CreateRawClient(hostHeader: "example.test");
        var initialize = await rawClient.InitializeAsync();
        var setCookie = initialize.HttpResponse.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.SingleOrDefault()
            : null;

        Assert.NotNull(setCookie);
        Assert.Contains("Secure", setCookie!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawCookieFallbackRejectsCrossOriginMutationRequests()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        var cookieContainer = new CookieContainer();
        await using var rawClient = host.CreateRawClient(cookieContainer: cookieContainer);
        await rawClient.InitializeAsync();

        var response = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-cross-origin",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "blocked" });
                           }
                           """
            },
            origin: "https://evil.example");

        Assert.True(response.IsError, response.Body.ToJsonString());
        Assert.Equal("permission_denied", response.StructuredContent["error"]!["code"]!.GetValue<string>());
        Assert.Equal("origin_validation_failed", response.StructuredContent["error"]!["data"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task RawCookieFallbackRejectsRequestsWithoutOriginOrReferer()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        var cookieContainer = new CookieContainer();
        await using var rawClient = host.CreateRawClient(cookieContainer: cookieContainer, includeDefaultOrigin: false);
        await rawClient.InitializeAsync();

        var response = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-missing-origin",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "blocked" });
                           }
                           """
            });

        Assert.True(response.IsError, response.Body.ToJsonString());
        Assert.Equal("permission_denied", response.StructuredContent["error"]!["code"]!.GetValue<string>());
        Assert.Equal("origin_validation_failed", response.StructuredContent["error"]!["data"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task RawSignedHeaderFallbackSupportsReconnectMutationFlow()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            enableSignedHeader: true,
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        var token = host.Services.GetRequiredService<IProgrammaticCallerBindingTokenService>().CreateSignedHeaderToken("header-client-1");

        await using var rawClient = host.CreateRawClient(signedHeaderToken: token);
        await rawClient.InitializeAsync();
        var preview = await rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-header-fallback",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "header-task" });
                           }
                           """
            });

        var approvalItems = preview.StructuredContent["approvalsRequested"]?.AsArray();
        Assert.NotNull(approvalItems);
        var approval = Assert.Single(approvalItems!);
        var stored = Assert.Single(await host.Services.GetRequiredService<IApprovalStore>().ListAllAsync());
        Assert.Equal("header-client-1", stored.CallerBindingId);

        await using var reconnected = host.CreateRawClient(signedHeaderToken: token);
        await reconnected.InitializeAsync();
        var apply = await reconnected.CallToolAsync(
            "mutation.apply",
            new JsonObject
            {
                ["conversationId"] = "conv-header-fallback",
                ["approvalId"] = approval!["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        Assert.Equal("completed", apply.StructuredContent["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task DotNetSdkHarnessCookieFallbackSupportsReconnectMutationFlow()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false);

        var cookieContainer = new CookieContainer();
        await using var initialClient = await host.CreateSdkClientAsync(cookieContainer: cookieContainer, includeDefaultOrigin: true);
        var preview = await initialClient.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sdk-cookie-fallback",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "sdk-cookie-task" });
                           }
                           """
            });

        var previewNode = ParseStructuredContent(preview);
        var approval = Assert.Single(previewNode["approvalsRequested"]!.AsArray());
        var cookies = cookieContainer.GetCookies(new Uri(host.BaseAddress, host.McpPath)).Cast<Cookie>().ToArray();
        var cookie = Assert.Single(cookies);
        Assert.Equal(ProgrammaticMcpServerOptions.DefaultCookieName, cookie.Name);

        await using var reconnectedClient = await host.CreateSdkClientAsync(cookieContainer: cookieContainer, includeDefaultOrigin: true);
        var listed = await reconnectedClient.CallToolAsync(
            "mutation.list",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sdk-cookie-fallback"
            });

        var listedNode = ParseStructuredContent(listed);
        var item = Assert.Single(listedNode["items"]!.AsArray());
        Assert.Equal(approval!["approvalId"]!.GetValue<string>(), item!["approvalId"]!.GetValue<string>());

        var apply = await reconnectedClient.CallToolAsync(
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sdk-cookie-fallback",
                ["approvalId"] = approval["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        Assert.Equal("completed", ParseStructuredContent(apply)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task DotNetSdkHarnessSignedHeaderFallbackSupportsReconnectMutationFlow()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            enableSignedHeader: true,
            configureOptions: options => options.EnableStatefulHttpTransport = false);
        var token = host.Services.GetRequiredService<IProgrammaticCallerBindingTokenService>().CreateSignedHeaderToken("sdk-header-client");

        await using var initialClient = await host.CreateSdkClientAsync(signedHeaderToken: token);
        var preview = await initialClient.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sdk-header-fallback",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "sdk-header-task" });
                           }
                           """
            });

        var previewNode = ParseStructuredContent(preview);
        var approval = Assert.Single(previewNode["approvalsRequested"]!.AsArray());
        var storedApproval = Assert.Single(await host.Services.GetRequiredService<IApprovalStore>().ListAllAsync());
        Assert.Equal("sdk-header-client", storedApproval.CallerBindingId);

        await using var reconnectedClient = await host.CreateSdkClientAsync(signedHeaderToken: token);
        var apply = await reconnectedClient.CallToolAsync(
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-sdk-header-fallback",
                ["approvalId"] = approval!["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        Assert.Equal("completed", ParseStructuredContent(apply)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task MutationAuthorizationPolicyIsEnforced()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(authorizationPolicy: new DenyAllAuthorizationPolicy());
        await using var client = await host.CreateSessionClientAsync();

        var preview = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-auth-denied",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "task-denied" });
                           }
                           """
            });

        Assert.False(preview.IsError ?? false);
        var previewNode = ParseStructuredContent(preview);
        var diagnostics = previewNode["diagnostics"]?.AsArray() ?? new JsonArray();
        var diagnostic = diagnostics
            .OfType<JsonObject>()
            .FirstOrDefault(item => item["code"]?.GetValue<string>() == "mutation_preview_unavailable"
                && item["data"] is JsonObject data
                && data["reason"]?.GetValue<string>() == "authorization_denied");
        Assert.NotNull(diagnostic);
        Assert.Equal("mutation_preview_unavailable", diagnostic!["code"]!.GetValue<string>());
        Assert.Equal("authorization_denied", diagnostic["data"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentMutationApplyTransitionsAreAtomic()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync();

        var preview = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-atomic-apply",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.complete({ taskId: "task-atomic" });
                           }
                           """
            });

        var approval = Assert.Single(ParseStructuredContent(preview)["approvalsRequested"]!.AsArray());
        var request = new Dictionary<string, object?>
        {
            ["conversationId"] = "conv-atomic-apply",
            ["approvalId"] = approval!["approvalId"]!.GetValue<string>(),
            ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
        };

        var apply1 = client.CallToolAsync("mutation.apply", request).AsTask();
        var apply2 = client.CallToolAsync("mutation.apply", request).AsTask();
        await Task.WhenAll(apply1, apply2);

        var statuses = new[]
        {
            ParseStructuredContent(await apply1)["status"]!.GetValue<string>(),
            ParseStructuredContent(await apply2)["status"]!.GetValue<string>()
        }.OrderBy(static value => value, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "completed", "not_found" }, statuses);
    }

    [Fact]
    public async Task ArtifactReadPagesSpilledExecutionResults()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync();

        var execute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-artifacts",
                ["maxResultBytes"] = 256,
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.exportReport({ size: 2048 });
                           }
                           """
            });

        Assert.False(execute.IsError ?? false, execute.StructuredContent?.GetRawText());
        var executeNode = ParseStructuredContent(execute);
        Assert.Null(executeNode["result"]);
        Assert.NotNull(executeNode["resultArtifactId"]);

        var artifactId = executeNode["resultArtifactId"]!.GetValue<string>();
        var page1 = await client.CallToolAsync(
            "artifact.read",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-artifacts",
                ["artifactId"] = artifactId,
                ["limit"] = 1
            });
        var page1Node = ParseStructuredContent(page1);
        Assert.True(page1Node["found"]!.GetValue<bool>());
        Assert.Equal("execution.result", page1Node["kind"]!.GetValue<string>());
        Assert.NotNull(page1Node["nextCursor"]);

        var page2 = await client.CallToolAsync(
            "artifact.read",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-artifacts",
                ["artifactId"] = artifactId,
                ["limit"] = 64,
                ["cursor"] = page1Node["nextCursor"]!.GetValue<string>()
            });
        var page2Node = ParseStructuredContent(page2);
        Assert.True(page2Node["found"]!.GetValue<bool>());
        Assert.True(page2Node["items"]!.AsArray().Count >= 1);
    }

    [Fact]
    public async Task ToolBoundaryValidationFailuresReturnInvalidParams()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync();

        var invalidExecute = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["code"] = "async function main() { return 1; }"
            });

        Assert.True(invalidExecute.IsError ?? false);
        Assert.Equal("invalid_params", ParseStructuredContent(invalidExecute)["error"]!["code"]!.GetValue<string>());

        var invalidArtifactRead = await client.CallToolAsync(
            "artifact.read",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-invalid",
                ["artifactId"] = "artifact",
                ["limit"] = 0
            });

        Assert.True(invalidArtifactRead.IsError ?? false);
        Assert.Equal("invalid_params", ParseStructuredContent(invalidArtifactRead)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidVisibleApiPathsReturnInvalidParams()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync();

        var result = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-visible-invalid",
                ["visibleApiPaths"] = new[] { "missing.capability" },
                ["code"] = "async function main() { return 1; }"
            });

        var node = ParseStructuredContent(result);
        Assert.True(result.IsError ?? false, node.ToJsonString());
        Assert.Equal("invalid_params", node["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Visible API path", node["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidConversationIdCases))]
    public async Task ConversationScopedToolsRejectInvalidConversationIdsAtTheToolBoundary(string toolName, Dictionary<string, object?> arguments)
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        await using var client = await host.CreateSessionClientAsync();

        var result = await client.CallToolAsync(toolName, arguments);
        var node = ParseStructuredContent(result);

        Assert.True(result.IsError ?? false, node.ToJsonString());
        Assert.Equal("invalid_params", node["error"]!["code"]!.GetValue<string>());
        Assert.Equal("conversationId is invalid.", node["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task StartupRecoveryMarksStaleApplyingApprovalsBeforeServing()
    {
        var seededStore = new InMemoryApprovalStore();
        await seededStore.CreateAsync(CreateApplyingApproval("conv-recovery", "caller-recovery", "tasks.complete"));

        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            includeDefaultCatalog: false,
            configureServices: services => services.AddSingleton<IApprovalStore>(seededStore));

        var recovered = Assert.Single(await seededStore.ListAllAsync());
        Assert.Equal(ApprovalState.FailedTerminal, recovered.State);
        Assert.Equal("apply_outcome_unknown", recovered.FailureCode);
    }

    [Fact]
    public async Task MaintenanceSweepRecoversStaleApplyingApprovals()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
        var store = (InMemoryApprovalStore)host.Services.GetRequiredService<IApprovalStore>();
        var approval = CreateApplyingApproval("conv-maintenance", "caller-maintenance", "tasks.complete") with
        {
            ApprovalId = "00000000-0000-0000-0000-00000000aa01",
            ApplyingSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await store.CreateAsync(approval);

        var lifecycle = host.Services
            .GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "ProgrammaticMcpLifecycleService");
        var method = lifecycle.GetType().GetMethod("RunMaintenanceCycleAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(lifecycle, [CancellationToken.None])!;
        await task;

        var recovered = await store.GetAsync(approval.ApprovalId);
        Assert.NotNull(recovered);
        Assert.Equal(ApprovalState.FailedTerminal, recovered!.State);
        Assert.Equal("apply_outcome_unknown", recovered.FailureCode);
    }

    [Fact]
    public async Task StartupWarnsWhenInMemoryArtifactBudgetIsOversized()
    {
        using var loggerProvider = new TestLoggerProvider();

        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureLogging: logging => logging.AddProvider(loggerProvider),
            configureOptions: options =>
            {
                options.ExecutorOptions = options.ExecutorOptions with
                {
                    ArtifactRetention = options.ExecutorOptions.ArtifactRetention with
                    {
                        MaxArtifactBytesGlobal = int.MaxValue
                    }
                };
            });

        Assert.Contains(
            loggerProvider.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("Configured in-memory artifact limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InMemoryApprovalsAndArtifactsClearOnProcessRestart()
    {
        string approvalId;
        string approvalNonce;
        string artifactId;

        await using (var host = await ProgrammaticMcpTestHost.StartAsync())
        {
            await using var client = await host.CreateSessionClientAsync();

            var preview = await client.CallToolAsync(
                "code.execute",
                new Dictionary<string, object?>
                {
                    ["conversationId"] = "conv-restart",
                    ["code"] = """
                               async function main() {
                                   return await programmatic.tasks.complete({ taskId: "task-restart" });
                               }
                               """
                });

            var approval = Assert.Single(ParseStructuredContent(preview)["approvalsRequested"]!.AsArray());
            approvalId = approval!["approvalId"]!.GetValue<string>();
            approvalNonce = approval["approvalNonce"]!.GetValue<string>();

            var execute = await client.CallToolAsync(
                "code.execute",
                new Dictionary<string, object?>
                {
                    ["conversationId"] = "conv-restart",
                    ["maxResultBytes"] = 256,
                    ["code"] = """
                               async function main() {
                                   return await programmatic.tasks.exportReport({ size: 2048 });
                               }
                               """
                });

            artifactId = ParseStructuredContent(execute)["resultArtifactId"]!.GetValue<string>();
        }

        await using var restartedHost = await ProgrammaticMcpTestHost.StartAsync();
        await using var restartedClient = await restartedHost.CreateSessionClientAsync();

        var mutationList = await restartedClient.CallToolAsync(
            "mutation.list",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-restart"
            });
        Assert.Empty(ParseStructuredContent(mutationList)["items"]!.AsArray());

        var apply = await restartedClient.CallToolAsync(
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-restart",
                ["approvalId"] = approvalId,
                ["approvalNonce"] = approvalNonce
            });
        Assert.Equal("not_found", ParseStructuredContent(apply)["status"]!.GetValue<string>());

        var artifactRead = await restartedClient.CallToolAsync(
            "artifact.read",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-restart",
                ["artifactId"] = artifactId
            });
        Assert.False(ParseStructuredContent(artifactRead)["found"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ApplyHandlerExceptionsReturnApplyHandlerError()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            includeDefaultCatalog: false,
            configureCatalog: catalog =>
            {
                catalog.AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
                    "tasks.boom",
                    mutation => mutation
                        .WithDescription("Throws during apply.")
                        .UseWhen("You are testing error mapping.")
                        .DoNotUseWhen("You are doing normal work.")
                        .WithPreviewHandler((args, _) => ValueTask.FromResult(new CompleteTaskPreview(args.TaskId, true)))
                        .WithSummaryFactory((args, _, _) => ValueTask.FromResult("Boom " + args.TaskId))
                        .WithApplyHandler((_, _) => throw new InvalidOperationException("boom")));
            });
        await using var client = await host.CreateSessionClientAsync();

        var preview = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-apply-handler-error",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.boom({ taskId: "task-1" });
                           }
                           """
            });

        var approval = Assert.Single(ParseStructuredContent(preview)["approvalsRequested"]!.AsArray());
        var apply = await client.CallToolAsync(
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-apply-handler-error",
                ["approvalId"] = approval!["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        var applyNode = ParseStructuredContent(apply);
        Assert.Equal("failed", applyNode["status"]!.GetValue<string>());
        Assert.Equal("apply_handler_error", applyNode["failureCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApplyArtifactsUseConfiguredChunkSizeInDescriptors()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            includeDefaultCatalog: false,
            configureOptions: options =>
            {
                options.ExecutorOptions = options.ExecutorOptions with
                {
                    ArtifactRetention = options.ExecutorOptions.ArtifactRetention with
                    {
                        ArtifactChunkBytes = 5
                    }
                };
            },
            configureCatalog: catalog =>
            {
                catalog.AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
                    "tasks.chunked",
                    mutation => mutation
                        .WithDescription("Writes a chunked apply artifact.")
                        .UseWhen("You are testing artifact descriptors.")
                        .DoNotUseWhen("You are doing normal work.")
                        .WithPreviewHandler((args, _) => ValueTask.FromResult(new CompleteTaskPreview(args.TaskId, true)))
                        .WithSummaryFactory((args, _, _) => ValueTask.FromResult("Chunk " + args.TaskId))
                        .WithApplyHandler(
                            async (args, context) =>
                            {
                                await context.Artifacts!.WriteTextArtifactAsync("chunked.txt", "abcdefghijk", "text/plain", context.CancellationToken);
                                return MutationApplyResult<CompleteTaskApplyResult>.Success(new CompleteTaskApplyResult(args.TaskId, "done"));
                            }));
            });
        await using var client = await host.CreateSessionClientAsync();

        var preview = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-chunked-apply",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.chunked({ taskId: "task-1" });
                           }
                           """
            });

        var approval = Assert.Single(ParseStructuredContent(preview)["approvalsRequested"]!.AsArray());
        var apply = await client.CallToolAsync(
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-chunked-apply",
                ["approvalId"] = approval!["approvalId"]!.GetValue<string>(),
                ["approvalNonce"] = approval["approvalNonce"]!.GetValue<string>()
            });

        var applyNode = ParseStructuredContent(apply);
        var artifactNode = Assert.Single(applyNode["artifacts"]!.AsArray());
        Assert.NotNull(artifactNode);
        var artifact = artifactNode!.AsObject();
        Assert.Equal(11, artifact["totalBytes"]!.GetValue<int>());
        Assert.Equal(3, artifact["totalChunks"]!.GetValue<int>());
    }

    [Fact]
    public async Task ApprovalSnapshotsEvictOldCursorsWhenThePerCallerLimitIsExceeded()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options =>
            {
                options.EnableStatefulHttpTransport = false;
                options.MaxApprovalListSnapshotsPerCallerBinding = 2;
            });
        var cookieContainer = new CookieContainer();
        await using var client = host.CreateRawClient(cookieContainer: cookieContainer);
        await client.InitializeAsync();

        foreach (var taskId in new[] { "task-snapshot-1", "task-snapshot-2", "task-snapshot-3" })
        {
            var code = """
                       async function main() {
                           return await programmatic.tasks.complete({ taskId: "__TASK_ID__" });
                       }
                       """.Replace("__TASK_ID__", taskId, StringComparison.Ordinal);

            var preview = await client.CallToolAsync(
                "code.execute",
                new JsonObject
                {
                    ["conversationId"] = "conv-snapshot-evict",
                    ["code"] = code
                });
            Assert.False(preview.IsError, preview.StructuredContent.ToJsonString());
        }

        var first = await client.CallToolAsync(
            "mutation.list",
            new JsonObject
            {
                ["conversationId"] = "conv-snapshot-evict",
                ["limit"] = 1
            });
        var firstCursor = first.StructuredContent["nextCursor"]?.GetValue<string>();

        _ = await client.CallToolAsync("mutation.list", new JsonObject { ["conversationId"] = "conv-snapshot-evict" });
        _ = await client.CallToolAsync("mutation.list", new JsonObject { ["conversationId"] = "conv-snapshot-evict" });

        Assert.NotNull(firstCursor);
        var stale = await client.CallToolAsync(
            "mutation.list",
            new JsonObject
            {
                ["conversationId"] = "conv-snapshot-evict",
                ["cursor"] = firstCursor
            });

        Assert.True(stale.IsError);
        Assert.Equal("invalid_params", stale.StructuredContent["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CompatibilityTextMirroringIsDisabledByDefaultAndCappedWhenEnabled()
    {
        await using (var defaultHost = await ProgrammaticMcpTestHost.StartAsync())
        {
            await using var defaultClient = await defaultHost.CreateSessionClientAsync();
            var result = await defaultClient.CallToolAsync(
                "code.execute",
                new Dictionary<string, object?>
                {
                    ["conversationId"] = "conv-text-default",
                    ["code"] = "async function main() { return { ok: true }; }"
                });

            Assert.Empty(result.Content);
        }

        await using var mirroredHost = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options =>
            {
                options.EnableStatefulHttpTransport = false;
                options.EnableCompatibilityTextMirroring = true;
                options.CompatibilityTextMirrorMaxBytes = 512;
            });
        await using var mirroredClient = mirroredHost.CreateRawClient();
        await mirroredClient.InitializeAsync();

        var small = await mirroredClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-text-small",
                ["code"] = "async function main() { return { ok: true }; }"
            });
        Assert.Single(small.Result["content"]?.AsArray() ?? throw new Xunit.Sdk.XunitException(small.Body.ToJsonString()));

        var large = await mirroredClient.CallToolAsync(
            "code.execute",
                new JsonObject
                {
                    ["conversationId"] = "conv-text-large",
                    ["maxResultBytes"] = 8192,
                    ["code"] = """
                           async function main() {
                               return await programmatic.tasks.exportReport({ size: 4096 });
                           }
                           """
            });
        Assert.Empty(large.Result["content"]!.AsArray());
    }

    [Fact]
    public async Task CodeExecuteRateLimitingReturnsResourceExhausted()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(maxExecutionRequestsPerMinutePerCaller: 1);
        await using var client = await host.CreateSessionClientAsync();

        var first = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-throttle",
                ["code"] = "async function main() { return 1; }"
            });
        Assert.False(first.IsError ?? false);

        var second = await client.CallToolAsync(
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-throttle",
                ["code"] = "async function main() { return 2; }"
            });

        Assert.True(second.IsError ?? false);
        var errorNode = ParseStructuredContent(second);
        Assert.Equal("resource_exhausted", errorNode["error"]!["code"]!.GetValue<string>());
        Assert.True(errorNode["error"]!["data"]!["retryAfterSeconds"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task RequestCancellationPropagatesToCapabilityExecution()
    {
        var probe = new CancellationProbe();
        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false,
            configureServices: services => services.AddSingleton(probe),
            configureCatalog: catalog =>
            {
                catalog.AddCapability<EmptyInput, string>(
                    "diag.cancel",
                    capability => capability
                        .WithDescription("Blocks until the request is cancelled.")
                        .UseWhen("You need to test cancellation.")
                        .DoNotUseWhen("You are doing normal work.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                probe.Started.TrySetResult();
                                try
                                {
                                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                                }
                                catch (OperationCanceledException)
                                {
                                    probe.Cancelled.TrySetResult();
                                    throw;
                                }

                                return "unreachable";
                            }));
            });

        await using var rawClient = host.CreateRawClient();
        await rawClient.InitializeAsync();

        using var cts = new CancellationTokenSource();
        var executeTask = rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-cancel",
                ["code"] = """
                           async function main() {
                               return await programmatic.diag.cancel({});
                           }
                           """
            },
            cancellationToken: cts.Token);

        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await executeTask);
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GracefulShutdownWaitsForInFlightExecutionAndEmitsActivitiesAndHealth()
    {
        var gate = new BlockingProbe();
        using var activityCollector = new TestActivityCollector();
        using var loggerProvider = new TestLoggerProvider();

        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.EnableStatefulHttpTransport = false,
            configureServices: services =>
            {
                services.AddSingleton(gate);
                services.AddHealthChecks().AddCheck("ready", () => HealthCheckResult.Healthy());
            },
            configureLogging: logging => logging.AddProvider(loggerProvider),
            configureCatalog: catalog =>
            {
                catalog.AddCapability<EmptyInput, string>(
                    "diag.block",
                    capability => capability
                        .WithDescription("Blocks until released.")
                        .UseWhen("You need to test graceful shutdown.")
                        .DoNotUseWhen("You are doing normal work.")
                        .WithHandler(
                            async (_, context) =>
                            {
                                gate.Started.TrySetResult();
                                await gate.Release.Task.WaitAsync(context.CancellationToken);
                                return "released";
                            }));
            },
            configureApp: (app, _) =>
            {
                app.MapProgrammaticMcpServer("/mcp");
                app.MapHealthChecks("/healthz");
            });

        using var healthClient = host.CreatePlainHttpClient();
        var health = await healthClient.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        await using var rawClient = host.CreateRawClient();
        await rawClient.InitializeAsync();
        var executionTask = rawClient.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-drain",
                ["code"] = """
                           async function main() {
                               return await programmatic.diag.block({});
                           }
                           """
            });

        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopTask = host.StopAsync();
        var stopProbe = await Task.WhenAny(stopTask, Task.Delay(250));
        Assert.NotSame(stopTask, stopProbe);

        gate.Release.TrySetResult();
        var execution = await executionTask;
        await stopTask;

        Assert.False(execution.IsError, execution.Body.ToJsonString());
        Assert.Equal("released", ExtractStringResult(execution));
        Assert.Contains(
            activityCollector.CompletedActivities,
            activity => activity.OperationName == "programmatic.tool.call"
                && string.Equals(
                    activity.Tags.FirstOrDefault(tag => tag.Key == "mcp.tool.name").Value as string,
                    "code.execute",
                    StringComparison.Ordinal));
        Assert.Contains(loggerProvider.Entries, entry => entry.Message.Contains("Programmatic code execution finished.", StringComparison.Ordinal));
        Assert.Contains(loggerProvider.Entries, entry => entry.Message.Contains("ApiCalls=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GracefulShutdownForceCancelsInFlightMutationApplyAndMarksApprovalTerminal()
    {
        var probe = new CancellationProbe();

        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options =>
            {
                options.EnableStatefulHttpTransport = false;
                options.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100);
            },
            includeDefaultCatalog: false,
            configureServices: services => services.AddSingleton(probe),
            configureCatalog: catalog =>
            {
                catalog.AllowAllBoundCallers();
                catalog.AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
                    "tasks.blockApply",
                    mutation => mutation
                        .WithDescription("Blocks during apply until cancelled.")
                        .UseWhen("You need to test forced shutdown cancellation.")
                        .DoNotUseWhen("You are doing normal work.")
                        .WithPreviewHandler((args, _) => ValueTask.FromResult(new CompleteTaskPreview(args.TaskId, true)))
                        .WithSummaryFactory((args, _, _) => ValueTask.FromResult("Block " + args.TaskId))
                        .WithApplyHandler(
                            async (_, context) =>
                            {
                                var localProbe = context.Services.GetRequiredService<CancellationProbe>();
                                localProbe.Started.TrySetResult();
                                try
                                {
                                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                                }
                                catch (OperationCanceledException)
                                {
                                    localProbe.Cancelled.TrySetResult();
                                    throw;
                                }

                                return MutationApplyResult<CompleteTaskApplyResult>.Success(new CompleteTaskApplyResult("never", "never"));
                            }));
            });
        var store = host.Services.GetRequiredService<IApprovalStore>();
        var cookieContainer = new CookieContainer();
        await using var client = host.CreateRawClient(cookieContainer: cookieContainer);

        await client.InitializeAsync();
        var preview = await client.CallToolAsync(
            "code.execute",
            new JsonObject
            {
                ["conversationId"] = "conv-force-cancel",
                ["code"] = """
                           async function main() {
                               return await programmatic.tasks.blockApply({ taskId: "task-1" });
                           }
                           """
            });

        var approval = Assert.Single(preview.StructuredContent["approvalsRequested"]!.AsArray());
        var approvalId = approval!["approvalId"]!.GetValue<string>();
        var approvalNonce = approval["approvalNonce"]!.GetValue<string>();

        var applyTask = client.CallToolAsync(
            "mutation.apply",
            new JsonObject
            {
                ["conversationId"] = "conv-force-cancel",
                ["approvalId"] = approvalId,
                ["approvalNonce"] = approvalNonce
            });

        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        RawMcpResponse? applyResponse = null;
        try
        {
            applyResponse = await applyTask;
        }
        catch (HttpRequestException)
        {
        }

        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var storedApproval = await store.GetAsync(approvalId);
        Assert.NotNull(storedApproval);
        Assert.Equal(ApprovalState.FailedTerminal, storedApproval!.State);
        Assert.Equal("apply_outcome_unknown", storedApproval.FailureCode);

        if (applyResponse is not null)
        {
            Assert.Equal("failed", applyResponse.StructuredContent["status"]!.GetValue<string>());
            Assert.Equal("apply_outcome_unknown", applyResponse.StructuredContent["failureCode"]!.GetValue<string>());
        }
    }

    private static JsonObject ParseStructuredContent(CallToolResult result)
    {
        if (result.StructuredContent.HasValue)
        {
            return JsonNode.Parse(result.StructuredContent.Value.GetRawText())!.AsObject();
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

    private static string ExtractStringResult(RawMcpResponse response)
    {
        if (TryExtractString(response.StructuredContent["result"], out var resultText))
        {
            return resultText;
        }

        var mirroredText = response.Result["content"]?.AsArray()
            .Select(static item => item?["text"]?.GetValue<string>())
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(mirroredText))
        {
            try
            {
                var parsed = JsonNode.Parse(mirroredText);
                if (TryExtractString(parsed, out var parsedText))
                {
                    return parsedText;
                }
            }
            catch (JsonException)
            {
                return mirroredText;
            }
        }

        if (TryExtractString(response.Body["error"]?["code"], out var errorCode))
        {
            return errorCode;
        }

        throw new Xunit.Sdk.XunitException($"A string result was not available on the raw tool result. Payload: {response.Body.ToJsonString()}");
    }

    private static bool TryExtractString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var directValue))
        {
            value = directValue;
            return true;
        }

        if (node is JsonObject jsonObject
            && jsonObject["text"] is JsonValue textValue
            && textValue.TryGetValue<string>(out var objectText))
        {
            value = objectText;
            return true;
        }

        value = string.Empty;
        return false;
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

    private static async Task<SamplingScopeInspection> InspectSamplingScopeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var publicClient = ProgrammaticSamplingServiceResolver.ResolvePublic(services);
        var structuredClient = ProgrammaticSamplingServiceResolver.ResolveStructured(services);
        using var childScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var childPublic = ProgrammaticSamplingServiceResolver.ResolvePublic(childScope.ServiceProvider);
        var childStructured = ProgrammaticSamplingServiceResolver.ResolveStructured(childScope.ServiceProvider);

        return new SamplingScopeInspection(
            publicClient.IsSupported,
            publicClient.SupportsToolUse,
            structuredClient.IsSupported,
            structuredClient.SupportsToolUse,
            ReferenceEquals(publicClient, childPublic),
            ReferenceEquals(structuredClient, childStructured),
            await CapturePublicSamplingCodeAsync(services, cancellationToken),
            await CaptureStructuredSamplingCodeAsync(services, cancellationToken));
    }

    private static async Task<string> CapturePublicSamplingCodeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var client = ProgrammaticSamplingServiceResolver.ResolvePublic(services);
            _ = await client.CreateMessageAsync(
                new ProgrammaticSamplingRequest(null, [new ProgrammaticSamplingMessage("user", "Hello")]),
                cancellationToken);
            return "ok";
        }
        catch (ProgrammaticSamplingException exception)
        {
            return exception.Code;
        }
    }

    private static async Task<string> CaptureStructuredSamplingCodeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var client = ProgrammaticSamplingServiceResolver.ResolveStructured(services);
            _ = await client.CreateMessageAsync(
                new ProgrammaticStructuredSamplingRequest(
                    null,
                    [
                        new ProgrammaticStructuredSamplingMessage(
                            "user",
                            [new ProgrammaticStructuredSamplingTextBlock("Hello")])
                    ],
                    32),
                cancellationToken);
            return "ok";
        }
        catch (ProgrammaticSamplingException exception)
        {
            return exception.Code;
        }
    }

    public static IEnumerable<object[]> InvalidConversationIdCases()
    {
        yield return
        [
            "code.execute",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "bad/id",
                ["code"] = "async function main() { return 1; }"
            }
        ];
        yield return
        [
            "artifact.read",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "bad/id",
                ["artifactId"] = "artifact-1"
            }
        ];
        yield return
        [
            "mutation.list",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "bad/id"
            }
        ];
        yield return
        [
            "mutation.apply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "bad/id",
                ["approvalId"] = "approval-1",
                ["approvalNonce"] = "nonce"
            }
        ];
        yield return
        [
            "mutation.cancel",
            new Dictionary<string, object?>
            {
                ["conversationId"] = "bad/id",
                ["approvalId"] = "approval-1",
                ["approvalNonce"] = "nonce"
            }
        ];
    }

    private static PendingApproval CreateApplyingApproval(string conversationId, string callerBindingId, string mutationName)
    {
        var approvalId = ApprovalTokenGenerator.GenerateApprovalId();
        var nonce = ApprovalTokenGenerator.GenerateApprovalNonce();
        var args = new JsonObject { ["taskId"] = "seeded-task" };
        var preview = new MutationPreviewEnvelope(
            "mutation.preview",
            approvalId,
            nonce,
            mutationName,
            "Seeded preview",
            args,
            new JsonObject { ["willComplete"] = true },
            "seeded-hash",
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));

        return new PendingApproval(
            approvalId,
            nonce,
            mutationName,
            args,
            "seeded-hash",
            preview,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(5),
            conversationId,
            callerBindingId,
            ApprovalState.Applying,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            null);
    }

    private sealed class ProgrammaticMcpTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ProgrammaticMcpTestHost(WebApplication app, Uri baseAddress, string mcpPath)
        {
            _app = app;
            BaseAddress = baseAddress;
            McpPath = mcpPath;
        }

        public Uri BaseAddress { get; }

        public string McpPath { get; }

        public string TypesPath => McpPath + "/types";

        public IServiceProvider Services => _app.Services;

        public static async Task<ProgrammaticMcpTestHost> StartAsync(
            bool enableSignedHeader = false,
            int maxExecutionRequestsPerMinutePerCaller = 60,
            string mcpPath = "/mcp",
            bool includeDefaultCatalog = true,
            Action<ProgrammaticMcpServerOptions>? configureOptions = null,
            Action<ProgrammaticMcpBuilder>? configureCatalog = null,
            IProgrammaticAuthorizationPolicy? authorizationPolicy = null,
            Action<IServiceCollection>? configureServices = null,
            Action<ILoggingBuilder>? configureLogging = null,
            Action<WebApplication, ProgrammaticMcpServerOptions>? configureApp = null)
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    EnvironmentName = "Development"
                });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            configureLogging?.Invoke(builder.Logging);
            configureServices?.Invoke(builder.Services);

            builder.Services.AddProgrammaticMcpServer(
                options =>
                {
                    options.ServerName = "ProgrammaticMcp.Tests";
                    options.ServerVersion = "1.0.0-test";
                    options.AllowInsecureDevelopmentCookies = true;
                    options.EnableSignedHeaderCallerBinding = enableSignedHeader;
                    options.MaxExecutionRequestsPerMinutePerCaller = maxExecutionRequestsPerMinutePerCaller;
                    options.ExecutorOptions = options.ExecutorOptions with
                    {
                        MaxResultBytes = 256,
                        MemoryBytes = 67_108_864,
                        ArtifactRetention = options.ExecutorOptions.ArtifactRetention with
                        {
                            ArtifactChunkBytes = 128
                        }
                    };

                    if (authorizationPolicy is not null)
                    {
                        options.Builder.UseAuthorizationPolicy(authorizationPolicy);
                    }
                    else
                    {
                        options.Builder.AllowAllBoundCallers();
                    }

                    if (includeDefaultCatalog)
                    {
                        AddDefaultCatalog(options.Builder);
                    }

                    configureCatalog?.Invoke(options.Builder);
                    configureOptions?.Invoke(options);
                });

            var app = builder.Build();
            if (configureApp is null)
            {
                app.MapProgrammaticMcpServer(mcpPath);
            }
            else
            {
                var options = app.Services.GetRequiredService<ProgrammaticMcpServerOptions>();
                configureApp(app, options);
            }

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var baseAddress = new Uri(addresses.Addresses.Single(), UriKind.Absolute);
            return new ProgrammaticMcpTestHost(app, baseAddress, mcpPath);
        }

        public HttpClient CreatePlainHttpClient()
        {
            return new HttpClient { BaseAddress = BaseAddress };
        }

        public async Task<McpClient> CreateSessionClientAsync(McpClientOptions? options = null)
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(BaseAddress, McpPath),
                    TransportMode = HttpTransportMode.StreamableHttp
                });
            return await McpClient.CreateAsync(transport, options ?? new McpClientOptions());
        }

        public async Task<McpClient> CreateSdkClientAsync(
            CookieContainer? cookieContainer = null,
            string? signedHeaderToken = null,
            bool includeDefaultOrigin = false,
            McpClientOptions? options = null)
        {
            var handler = new HttpClientHandler
            {
                UseCookies = cookieContainer is not null,
                CookieContainer = cookieContainer ?? new CookieContainer()
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = BaseAddress
            };

            if (includeDefaultOrigin && client.BaseAddress is not null)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "Origin",
                    new Uri(client.BaseAddress, "/").GetLeftPart(UriPartial.Authority));
            }

            if (!string.IsNullOrWhiteSpace(signedHeaderToken))
            {
                client.DefaultRequestHeaders.Add(ProgrammaticMcpServerOptions.DefaultSignedHeaderName, signedHeaderToken);
            }

            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(BaseAddress, McpPath),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                client,
                NullLoggerFactory.Instance,
                ownsHttpClient: true);

            return await McpClient.CreateAsync(transport, options ?? new McpClientOptions());
        }

        public RawMcpClient CreateRawClient(CookieContainer? cookieContainer = null, string? signedHeaderToken = null, bool includeDefaultOrigin = true, string? hostHeader = null)
        {
            var handler = new HttpClientHandler
            {
                UseCookies = cookieContainer is not null,
                CookieContainer = cookieContainer ?? new CookieContainer()
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = BaseAddress
            };

            if (!string.IsNullOrWhiteSpace(signedHeaderToken))
            {
                client.DefaultRequestHeaders.Add(ProgrammaticMcpServerOptions.DefaultSignedHeaderName, signedHeaderToken);
            }

            var defaultOrigin = includeDefaultOrigin && client.BaseAddress is not null
                ? new Uri(client.BaseAddress, "/").GetLeftPart(UriPartial.Authority)
                : null;
            return new RawMcpClient(client, McpPath, defaultOrigin, hostHeader);
        }

        public Task StopAsync()
        {
            return _app.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static void AddDefaultCatalog(ProgrammaticMcpBuilder catalog)
        {
            catalog.AddCapability<ValueInput, int>(
                "math.double",
                capability => capability
                    .WithDescription("Doubles a value.")
                    .UseWhen("You need a simple calculation.")
                    .DoNotUseWhen("You need to mutate data.")
                    .WithHandler((input, _) => ValueTask.FromResult(input.Value * 2)));

            catalog.AddCapability<ReportRequest, ReportResult>(
                "tasks.exportReport",
                capability => capability
                    .WithDescription("Creates a large report payload.")
                    .UseWhen("You need an artifact spill example.")
                    .DoNotUseWhen("You only need a small inline result.")
                    .WithHandler(
                        (input, _) => ValueTask.FromResult(new ReportResult(new string('x', input.Size)))));

            catalog.AddCapability<EmptyInput, TaskStatusResult>(
                "tasks.status",
                capability => capability
                    .WithDescription("Returns a fake task status.")
                    .UseWhen("You need another task capability for search paging.")
                    .DoNotUseWhen("You need a mutation.")
                    .WithHandler((_, _) => ValueTask.FromResult(new TaskStatusResult("open"))));

            catalog.AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
                "tasks.complete",
                mutation => mutation
                    .WithDescription("Completes a task.")
                    .UseWhen("You want to complete a task.")
                    .DoNotUseWhen("You are still exploring.")
                    .WithPreviewHandler((input, _) => ValueTask.FromResult(new CompleteTaskPreview(input.TaskId, true)))
                    .WithSummaryFactory((input, _, _) => ValueTask.FromResult("Complete " + input.TaskId))
                    .WithApplyHandler(
                        async (input, context) =>
                        {
                            await context.Artifacts!.WriteTextArtifactAsync("apply-result.txt", "completed " + input.TaskId, "text/plain", context.CancellationToken);
                            return MutationApplyResult<CompleteTaskApplyResult>.Success(new CompleteTaskApplyResult(input.TaskId, "done"));
                        }));
        }
    }

    private sealed class RawMcpClient : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly string _mcpPath;
        private readonly string? _defaultOrigin;
        private readonly string? _hostHeader;
        private int _nextId = 1;
        private string _protocolVersion = "2024-11-05";

        public RawMcpClient(HttpClient client, string mcpPath, string? defaultOrigin = null, string? hostHeader = null)
        {
            _client = client;
            _mcpPath = mcpPath;
            _defaultOrigin = defaultOrigin;
            _hostHeader = hostHeader;
        }

        public async Task<RawMcpResponse> InitializeAsync(CancellationToken cancellationToken = default)
        {
            return await SendAsync(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "raw-phase4-test-client",
                        ["version"] = "1.0.0"
                    }
                },
                cancellationToken: cancellationToken);
        }

        public Task<RawMcpResponse> CallToolAsync(
            string name,
            JsonObject arguments,
            string? origin = null,
            CancellationToken cancellationToken = default)
        {
            return SendAsync(
                "tools/call",
                new JsonObject
                {
                    ["name"] = name,
                    ["arguments"] = arguments
                },
                origin,
                cancellationToken);
        }

        public Task<RawMcpResponse> ListResourcesAsync(string? origin = null, CancellationToken cancellationToken = default)
        {
            return SendAsync("resources/list", new JsonObject(), origin, cancellationToken);
        }

        public Task<RawMcpResponse> ReadResourceAsync(string uri, string? origin = null, CancellationToken cancellationToken = default)
        {
            return SendAsync(
                "resources/read",
                new JsonObject
                {
                    ["uri"] = uri
                },
                origin,
                cancellationToken);
        }

        private async Task<RawMcpResponse> SendAsync(
            string method,
            JsonObject? @params,
            string? origin = null,
            CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _mcpPath);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
            origin ??= _defaultOrigin;
            if (!string.IsNullOrWhiteSpace(origin))
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }
            if (!string.IsNullOrWhiteSpace(_hostHeader))
            {
                request.Headers.Host = _hostHeader;
            }

            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Interlocked.Increment(ref _nextId),
                ["method"] = method,
                ["params"] = @params
            };

            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = ParseResponseBody(response, body);
            if (method == "initialize" && json["result"]?["protocolVersion"] is JsonValue protocolVersion)
            {
                _protocolVersion = protocolVersion.GetValue<string>();
            }

            return new RawMcpResponse(response, json);
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

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RawMcpResponse(HttpResponseMessage httpResponse, JsonObject body)
    {
        public HttpResponseMessage HttpResponse { get; } = httpResponse;

        public JsonObject Body { get; } = body;

        public JsonObject Result => Body["result"]?.AsObject() ?? new JsonObject();

        public JsonObject StructuredContent
        {
            get
            {
                if (Result["structuredContent"] is JsonObject structuredContent)
                {
                    return structuredContent;
                }

                if (Body["error"] is JsonObject error)
                {
                    return new JsonObject
                    {
                        ["error"] = error.DeepClone()
                    };
                }

                return new JsonObject();
            }
        }

        public bool IsError => Body["error"] is not null || Result["isError"]?.GetValue<bool>() == true;
    }

    private sealed class ScopeProbe
    {
        public string ScopeId { get; } = Guid.NewGuid().ToString("N");
    }

    private sealed class CancellationProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DenyAllAuthorizationPolicy : IProgrammaticAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ProgrammaticAuthorizationContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class ThrowingCatalog : ICapabilityCatalog
    {
        public IReadOnlyList<CapabilityDefinition> Capabilities => Array.Empty<CapabilityDefinition>();

        public IReadOnlyList<ProgrammaticResourceDefinition> Resources => Array.Empty<ProgrammaticResourceDefinition>();

        public string CapabilityVersion => "throwing-catalog";

        public string GeneratedTypeScript => throw new InvalidOperationException("Synthetic type rendering failure.");

        public IProgrammaticAuthorizationPolicy AuthorizationPolicy { get; } = new AllowAllBoundCallersAuthorizationPolicy();

        public CapabilitySearchResponse Search(CapabilitySearchRequest request)
            => new(1, CapabilityVersion, request.DetailLevel, Array.Empty<CapabilitySearchItem>(), null);

        public ValueTask<ProgrammaticResourceReadResult?> ReadResourceAsync(string uri, ProgrammaticResourceContext context)
            => ValueTask.FromResult<ProgrammaticResourceReadResult?>(null);
    }

    private sealed class TestActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _completedActivities = new();

        public TestActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "ProgrammaticMcp.AspNetCore",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _completedActivities.Add(activity)
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> CompletedActivities => _completedActivities;

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new TestLogger(_entries, categoryName);

        public void Dispose()
        {
        }

        public sealed record LogEntry(string Category, LogLevel Level, string Message);

        private sealed class TestLogger(List<LogEntry> entries, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                entries.Add(new LogEntry(category, logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization == "Test allowed")
            {
                var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "authorized-user") }, Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
            }

            return Task.FromResult(AuthenticateResult.Fail("Missing authorization header."));
        }
    }

    private sealed record EmptyInput();

    private sealed record ValueInput(int Value);

    private sealed record ReportRequest(int Size);

    private sealed record ReportResult(string Content);

    private sealed record TaskStatusResult(string Status);

    private sealed record CompleteTaskArgs(string TaskId);

    private sealed record CompleteTaskPreview(string TaskId, bool WillComplete);

    private sealed record CompleteTaskApplyResult(string TaskId, string Status);

    private sealed record SamplingEnvelope(string Text);

    private sealed record SamplingCodeResult(string Code);

    private sealed record ReadClockInput(string TimeZone);

    private sealed record ReadClockResult(string CurrentTime);

    private sealed record LargeSamplingToolResult(string Payload);

    private sealed record ScopeProbeResult(string ScopeId);

    private sealed record SamplingScopeInspection(
        bool PublicIsSupported,
        bool PublicSupportsToolUse,
        bool StructuredIsSupported,
        bool StructuredSupportsToolUse,
        bool PublicSameInChildScope,
        bool StructuredSameInChildScope,
        string PublicCode,
        string StructuredCode);

    private sealed record SamplingIdentityInspection(
        bool PublicIsAmbient,
        bool StructuredIsAmbient,
        bool PublicSameInChildScope,
        bool StructuredSameInChildScope,
        bool PublicIsSupported,
        bool PublicSupportsToolUse,
        bool StructuredIsSupported,
        bool StructuredSupportsToolUse);

    private sealed class AmbientSamplingClient : IProgrammaticSamplingClient, IProgrammaticStructuredSamplingClient
    {
        public bool IsSupported => false;

        public bool SupportsToolUse => false;

        public ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromException<ProgrammaticSamplingResult>(
                new ProgrammaticSamplingException("ambient_sampling_client_used", "The ambient public sampling client should not be used."));

        public ValueTask<ProgrammaticStructuredSamplingResult> CreateMessageAsync(
            ProgrammaticStructuredSamplingRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<ProgrammaticStructuredSamplingResult>(
                new ProgrammaticSamplingException("ambient_sampling_client_used", "The ambient structured sampling client should not be used."));
    }
}
