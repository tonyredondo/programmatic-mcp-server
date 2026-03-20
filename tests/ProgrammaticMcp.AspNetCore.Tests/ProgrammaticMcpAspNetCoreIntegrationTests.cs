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
using Microsoft.Extensions.Options;
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
        await using var host = await ProgrammaticMcpTestHost.StartAsync();

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
    public async Task RawCookieFallbackRejectsCrossOriginMutationRequests()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync();
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
    public async Task RawSignedHeaderFallbackSupportsReconnectMutationFlow()
    {
        await using var host = await ProgrammaticMcpTestHost.StartAsync(enableSignedHeader: true);
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
            configureOptions: options => options.MaxApprovalListSnapshotsPerCallerBinding = 2);
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
        await Task.Delay(250);
        Assert.False(stopTask.IsCompleted);

        gate.Release.TrySetResult();
        var execution = await executionTask;
        await stopTask;

        Assert.Equal("released", execution.StructuredContent["result"]!.GetValue<string>());
        var toolActivity = Assert.Single(activityCollector.CompletedActivities, activity => activity.OperationName == "programmatic.tool.call");
        Assert.Equal("code.execute", toolActivity.Tags.Single(tag => tag.Key == "mcp.tool.name").Value);
        Assert.Contains(loggerProvider.Entries, entry => entry.Message.Contains("Programmatic code execution finished.", StringComparison.Ordinal));
        Assert.Contains(loggerProvider.Entries, entry => entry.Message.Contains("ApiCalls=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GracefulShutdownForceCancelsInFlightMutationApplyAndMarksApprovalTerminal()
    {
        var probe = new CancellationProbe();

        await using var host = await ProgrammaticMcpTestHost.StartAsync(
            configureOptions: options => options.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100),
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
        Assert.True(result.StructuredContent.HasValue);
        return JsonNode.Parse(result.StructuredContent.Value.GetRawText())!.AsObject();
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

        public async Task<McpClient> CreateSessionClientAsync()
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(BaseAddress, McpPath),
                    TransportMode = HttpTransportMode.StreamableHttp
                });
            return await McpClient.CreateAsync(transport);
        }

        public RawMcpClient CreateRawClient(CookieContainer? cookieContainer = null, string? signedHeaderToken = null)
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

            return new RawMcpClient(client, McpPath);
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
        private int _nextId = 1;
        private string _protocolVersion = "2024-11-05";

        public RawMcpClient(HttpClient client, string mcpPath)
        {
            _client = client;
            _mcpPath = mcpPath;
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
            if (!string.IsNullOrWhiteSpace(origin))
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
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

        public string CapabilityVersion => "throwing-catalog";

        public string GeneratedTypeScript => throw new InvalidOperationException("Synthetic type rendering failure.");

        public IProgrammaticAuthorizationPolicy AuthorizationPolicy { get; } = new AllowAllBoundCallersAuthorizationPolicy();

        public CapabilitySearchResponse Search(CapabilitySearchRequest request)
            => new(1, CapabilityVersion, request.DetailLevel, Array.Empty<CapabilitySearchItem>(), null);
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

    private sealed record ScopeProbeResult(string ScopeId);
}
