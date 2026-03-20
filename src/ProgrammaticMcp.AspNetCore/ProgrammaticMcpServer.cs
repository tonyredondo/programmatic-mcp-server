using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using global::ProgrammaticMcp.Jint;

namespace ProgrammaticMcp.AspNetCore;

/// <summary>
/// Configures the ASP.NET Core MCP server integration.
/// </summary>
public sealed class ProgrammaticMcpServerOptions
{
    /// <summary>Gets the default cookie name used for caller binding.</summary>
    public const string DefaultCookieName = "__Host-programmatic-mcp-caller";
    /// <summary>Gets the default signed-header name used for caller binding.</summary>
    public const string DefaultSignedHeaderName = "X-Programmatic-Mcp-Caller-Binding";
    /// <summary>Gets the default path segment used by the generated TypeScript endpoint.</summary>
    public const string DefaultTypeEndpointSegment = "types";
    /// <summary>Gets the HTTP header that carries the MCP session identifier.</summary>
    public const string McpSessionIdHeaderName = "Mcp-Session-Id";

    /// <summary>Gets the catalog builder used to register capabilities before the server starts.</summary>
    public ProgrammaticMcpBuilder Builder { get; } = new();

    /// <summary>Gets or sets the runtime options used by the Jint-backed executor.</summary>
    public JintExecutorOptions ExecutorOptions { get; set; } = new();

    /// <summary>Gets or sets the server name advertised to MCP clients.</summary>
    public string ServerName { get; set; } = "ProgrammaticMcp.Server";

    /// <summary>Gets or sets the server version advertised to MCP clients.</summary>
    public string ServerVersion { get; set; } = "0.1.0";

    /// <summary>Gets or sets the default route prefix used when mapping the server.</summary>
    public string RoutePrefix { get; internal set; } = "/mcp";

    /// <summary>Gets or sets whether the HTTP MCP transport should keep server-side sessions.</summary>
    public bool EnableStatefulHttpTransport { get; set; } = true;

    /// <summary>Gets or sets the relative path segment used for the generated TypeScript endpoint.</summary>
    public string TypeEndpointSegment { get; set; } = DefaultTypeEndpointSegment;

    /// <summary>Gets or sets whether cookie-based caller binding is enabled.</summary>
    public bool EnableCookieCallerBinding { get; set; } = true;

    /// <summary>Gets or sets whether signed-header caller binding is enabled.</summary>
    public bool EnableSignedHeaderCallerBinding { get; set; }

    /// <summary>Gets or sets the cookie name used for caller binding.</summary>
    public string CookieName { get; set; } = DefaultCookieName;

    /// <summary>Gets or sets the signed-header name used for caller binding.</summary>
    public string SignedHeaderName { get; set; } = DefaultSignedHeaderName;

    /// <summary>Gets or sets the cookie lifetime used for caller binding.</summary>
    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Gets or sets the signed-header token lifetime used for caller binding.</summary>
    public TimeSpan HeaderLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Gets or sets whether insecure development cookies are allowed.</summary>
    public bool AllowInsecureDevelopmentCookies { get; set; }

    /// <summary>Gets or sets the maximum number of concurrent executions allowed per caller.</summary>
    public int MaxConcurrentExecutionsPerCaller { get; set; } = 2;

    /// <summary>Gets or sets the maximum number of execution requests allowed per caller per minute.</summary>
    public int MaxExecutionRequestsPerMinutePerCaller { get; set; } = 60;

    /// <summary>Gets or sets the maximum length of capability search queries.</summary>
    public int MaxQueryLength { get; set; } = 500;

    /// <summary>Gets or sets the maximum number of approval list snapshots kept per caller binding.</summary>
    public int MaxApprovalListSnapshotsPerCallerBinding { get; set; } = 8;

    /// <summary>Gets or sets the lifetime, in seconds, of approval list snapshots.</summary>
    public int ApprovalListSnapshotTtlSeconds { get; set; } = 60;

    /// <summary>Gets or sets the age threshold, in seconds, for recovering stale applying approvals.</summary>
    public int StaleApplyingTimeoutSeconds { get; set; } = 60;

    /// <summary>Gets or sets whether inline compatibility text mirroring is enabled.</summary>
    public bool EnableCompatibilityTextMirroring { get; set; }

    /// <summary>Gets or sets the maximum number of bytes mirrored into compatibility text.</summary>
    public int CompatibilityTextMirrorMaxBytes { get; set; } = 16_384;

    /// <summary>Gets or sets the graceful shutdown timeout used when draining in-flight work.</summary>
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Invokes the catalog configuration callback.</summary>
    /// <param name="configure">Configures the capabilities that will be exposed by the server.</param>
    public void ConfigureCatalog(Action<ProgrammaticMcpBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Builder);
    }

    /// <summary>Validates the configured server options.</summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerName))
        {
            throw new InvalidOperationException("ServerName is required.");
        }

        if (string.IsNullOrWhiteSpace(ServerVersion))
        {
            throw new InvalidOperationException("ServerVersion is required.");
        }

        if (string.IsNullOrWhiteSpace(TypeEndpointSegment) || TypeEndpointSegment.Contains('/'))
        {
            throw new InvalidOperationException("TypeEndpointSegment must be a single relative segment.");
        }

        if (MaxConcurrentExecutionsPerCaller <= 0)
        {
            throw new InvalidOperationException("MaxConcurrentExecutionsPerCaller must be positive.");
        }

        if (MaxExecutionRequestsPerMinutePerCaller <= 0)
        {
            throw new InvalidOperationException("MaxExecutionRequestsPerMinutePerCaller must be positive.");
        }

        if (MaxQueryLength <= 0)
        {
            throw new InvalidOperationException("MaxQueryLength must be positive.");
        }

        if (MaxApprovalListSnapshotsPerCallerBinding <= 0)
        {
            throw new InvalidOperationException("MaxApprovalListSnapshotsPerCallerBinding must be positive.");
        }

        if (ApprovalListSnapshotTtlSeconds <= 0 || StaleApplyingTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("ApprovalListSnapshotTtlSeconds and StaleApplyingTimeoutSeconds must be positive.");
        }

        if (CompatibilityTextMirrorMaxBytes <= 0)
        {
            throw new InvalidOperationException("CompatibilityTextMirrorMaxBytes must be positive.");
        }

        ExecutorOptions.Validate();
    }
}

/// <summary>
/// Creates caller-binding tokens for the ASP.NET Core transport.
/// </summary>
public interface IProgrammaticCallerBindingTokenService
{
    /// <summary>Creates a signed header token for the specified caller binding identifier.</summary>
    string CreateSignedHeaderToken(string callerBindingId);
}

/// <summary>
/// Registers Programmatic MCP services in the dependency injection container.
/// </summary>
public static class ProgrammaticMcpServiceCollectionExtensions
{
    /// <summary>Adds the Programmatic MCP server services to the container.</summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="configure">Configures the server before registration.</param>
    public static IServiceCollection AddProgrammaticMcpServer(
        this IServiceCollection services,
        Action<ProgrammaticMcpServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProgrammaticMcpServerOptions();
        configure(options);
        options.Validate();

        var catalog = options.Builder.BuildCatalog();

        services.AddDataProtection();
        services.AddHttpContextAccessor();

        services.TryAddSingleton(options);
        services.TryAddSingleton<ICapabilityCatalog>(catalog);
        services.TryAddSingleton(catalog);
        services.TryAddSingleton(options.ExecutorOptions);
        services.TryAddSingleton<IArtifactStore>(sp => new InMemoryArtifactStore(sp.GetRequiredService<JintExecutorOptions>().ArtifactRetention));
        services.TryAddSingleton<IApprovalStore, InMemoryApprovalStore>();
        services.TryAddSingleton<ICodeExecutor>(
            sp => new global::ProgrammaticMcp.Jint.JintCodeExecutor(
                sp.GetRequiredService<ICapabilityCatalog>(),
                sp.GetRequiredService<JintExecutorOptions>(),
                sp.GetRequiredService<IArtifactStore>(),
                sp.GetRequiredService<IApprovalStore>()));
        services.TryAddSingleton<ICodeExecutionService>(sp => new DefaultCodeExecutionService(sp.GetRequiredService<ICodeExecutor>()));
        services.TryAddSingleton<ProgrammaticMcpRouteState>();
        services.TryAddSingleton<ProgrammaticMcpThrottle>();
        services.TryAddSingleton<ApprovalSnapshotStore>();
        services.TryAddSingleton<ProgrammaticMcpActivitySource>();
        services.TryAddSingleton<ProgrammaticMcpShutdownCoordinator>();
        services.TryAddSingleton<ProgrammaticCallerBindingResolver>();
        services.TryAddSingleton<IProgrammaticCallerBindingTokenService>(sp => sp.GetRequiredService<ProgrammaticCallerBindingResolver>());
        services.TryAddSingleton<ProgrammaticToolSchemaRegistry>();
        services.TryAddSingleton<ProgrammaticMcpToolHandlers>();
        services.TryAddSingleton<ProgrammaticMcpLifecycleService>();
        services.AddHostedService(sp => sp.GetRequiredService<ProgrammaticMcpLifecycleService>());

        services
            .AddMcpServer(
                serverOptions =>
                {
                    serverOptions.ServerInfo = new Implementation
                    {
                        Name = options.ServerName,
                        Version = options.ServerVersion
                    };
                })
            .WithHttpTransport(
                transportOptions =>
                {
                    transportOptions.Stateless = !options.EnableStatefulHttpTransport;
                    transportOptions.ConfigureSessionOptions = async (httpContext, serverOptions, cancellationToken) =>
                    {
                        var routeState = httpContext.RequestServices.GetRequiredService<ProgrammaticMcpRouteState>();
                        var runtimeOptions = httpContext.RequestServices.GetRequiredService<ProgrammaticMcpServerOptions>();
                        var callerBindingResolver = httpContext.RequestServices.GetRequiredService<ProgrammaticCallerBindingResolver>();
                        serverOptions.ServerInfo = new Implementation
                        {
                            Name = runtimeOptions.ServerName,
                            Version = runtimeOptions.ServerVersion
                        };
                        serverOptions.ServerInstructions = ProgrammaticInstructionsBuilder.Build(runtimeOptions, routeState);
                        await callerBindingResolver.IssueInitialCookieAsync(httpContext, cancellationToken);
                        await Task.CompletedTask;
                    };
                })
            .WithListToolsHandler(
                static async (context, cancellationToken) =>
                {
                    var handlers = context.Services!.GetRequiredService<ProgrammaticMcpToolHandlers>();
                    return await handlers.ListToolsAsync(context, cancellationToken);
                })
            .WithCallToolHandler(
                static async (context, cancellationToken) =>
                {
                    var handlers = context.Services!.GetRequiredService<ProgrammaticMcpToolHandlers>();
                    return await handlers.CallToolAsync(context, cancellationToken);
                });

        return services;
    }
}

/// <summary>
/// Maps the Programmatic MCP endpoints into an ASP.NET Core endpoint route builder.
/// </summary>
public static class ProgrammaticMcpEndpointRouteBuilderExtensions
{
    /// <summary>Maps the MCP transport endpoint and the generated TypeScript endpoint.</summary>
    /// <param name="endpoints">The route builder used to register endpoints.</param>
    /// <param name="routePrefix">Optional override for the MCP route prefix.</param>
    public static IEndpointRouteBuilder MapProgrammaticMcpServer(this IEndpointRouteBuilder endpoints, string? routePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<ProgrammaticMcpServerOptions>();
        var routeState = endpoints.ServiceProvider.GetRequiredService<ProgrammaticMcpRouteState>();

        var resolvedPrefix = NormalizePrefix(routePrefix ?? options.RoutePrefix);
        options.RoutePrefix = resolvedPrefix;
        routeState.RoutePrefix = resolvedPrefix;
        routeState.TypeEndpointPath = CombineRoute(resolvedPrefix, options.TypeEndpointSegment);

        endpoints.MapMcp(resolvedPrefix);
        endpoints.MapGet(
            routeState.TypeEndpointPath,
            async context =>
            {
                var handlers = context.RequestServices.GetRequiredService<ProgrammaticMcpToolHandlers>();
                await handlers.WriteTypesAsync(context);
            });

        return endpoints;
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "/";
        }

        var normalized = prefix.StartsWith("/", StringComparison.Ordinal) ? prefix : "/" + prefix;
        if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static string CombineRoute(string prefix, string segment)
    {
        if (prefix == "/")
        {
            return "/" + segment;
        }

        return prefix + "/" + segment;
    }
}

internal sealed class DefaultCodeExecutionService(ICodeExecutor executor) : ICodeExecutionService
{
    public ValueTask<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken = default)
        => executor.ExecuteAsync(request, cancellationToken);
}

internal sealed class ProgrammaticMcpRouteState
{
    public string RoutePrefix { get; set; } = "/mcp";

    public string TypeEndpointPath { get; set; } = "/mcp/types";
}

internal sealed class ProgrammaticMcpActivitySource : IDisposable
{
    private readonly ActivitySource _activitySource = new("ProgrammaticMcp.AspNetCore");

    public Activity? Start(string name) => _activitySource.StartActivity(name);

    public void Dispose() => _activitySource.Dispose();
}

internal sealed class ProgrammaticMcpShutdownCoordinator
{
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _forcedCancellation = new();
    private int _activeOperations;
    private volatile bool _stopping;

    public bool IsStopping => _stopping;

    public CancellationToken ForcedCancellationToken => _forcedCancellation.Token;

    public IDisposable Enter()
    {
        if (_stopping)
        {
            throw new InvalidOperationException("The server is shutting down.");
        }

        Interlocked.Increment(ref _activeOperations);
        return new Lease(this);
    }

    public void BeginShutdown()
    {
        _stopping = true;
        if (Volatile.Read(ref _activeOperations) == 0)
        {
            _drained.TrySetResult();
        }
    }

    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(_drained.Task, Task.Delay(timeout, cancellationToken));
        return completed == _drained.Task;
    }

    public void ForceCancelRemainingWork()
    {
        _forcedCancellation.Cancel();
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _activeOperations) == 0 && _stopping)
        {
            _drained.TrySetResult();
        }
    }

    private sealed class Lease(ProgrammaticMcpShutdownCoordinator owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}

internal sealed class ProgrammaticMcpThrottle(ProgrammaticMcpServerOptions options)
{
    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _inFlight = new(StringComparer.Ordinal);

    public ThrottleDecision TryEnter(string callerKey, DateTimeOffset utcNow)
    {
        var window = _windows.GetOrAdd(callerKey, static _ => new WindowState());
        lock (window.Gate)
        {
            if (utcNow >= window.WindowStartUtc.AddMinutes(1))
            {
                window.WindowStartUtc = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, utcNow.Offset);
                window.RequestCount = 0;
            }

            if (window.RequestCount >= options.MaxExecutionRequestsPerMinutePerCaller)
            {
                var retryAfter = (int)Math.Ceiling((window.WindowStartUtc.AddMinutes(1) - utcNow).TotalSeconds);
                return ThrottleDecision.RateLimited(retryAfter);
            }

            var currentInFlight = _inFlight.AddOrUpdate(callerKey, 1, static (_, count) => count + 1);
            if (currentInFlight > options.MaxConcurrentExecutionsPerCaller)
            {
                _inFlight.AddOrUpdate(callerKey, 0, static (_, count) => Math.Max(0, count - 1));
                return ThrottleDecision.ConcurrencyLimited();
            }

            window.RequestCount++;
            return ThrottleDecision.Permit(new Releaser(_inFlight, callerKey));
        }
    }

    internal sealed class WindowState
    {
        public object Gate { get; } = new();

        public DateTimeOffset WindowStartUtc { get; set; } = DateTimeOffset.UtcNow;

        public int RequestCount { get; set; }
    }

    public sealed record ThrottleDecision(bool IsAllowed, string? Reason, int? RetryAfterSeconds, IDisposable? Lease)
    {
        public static ThrottleDecision Permit(IDisposable lease) => new(true, null, null, lease);

        public static ThrottleDecision RateLimited(int retryAfterSeconds) => new(false, "rate_limit", retryAfterSeconds, null);

        public static ThrottleDecision ConcurrencyLimited() => new(false, "concurrency_limit", null, null);
    }

    private sealed class Releaser(ConcurrentDictionary<string, int> entries, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                entries.AddOrUpdate(key, 0, static (_, count) => Math.Max(0, count - 1));
            }
        }
    }
}

internal sealed class ApprovalSnapshotStore(ProgrammaticMcpServerOptions options)
{
    private readonly ConcurrentDictionary<string, LinkedList<ApprovalSnapshot>> _snapshots = new(StringComparer.Ordinal);

    public ApprovalSnapshot Create(string conversationId, string callerBindingId, IReadOnlyList<PendingApproval> approvals)
    {
        var snapshot = new ApprovalSnapshot(
            Guid.NewGuid().ToString("N"),
            conversationId,
            callerBindingId,
            DateTimeOffset.UtcNow.AddSeconds(options.ApprovalListSnapshotTtlSeconds),
            approvals);

        var list = _snapshots.GetOrAdd(callerBindingId, static _ => new LinkedList<ApprovalSnapshot>());
        lock (list)
        {
            CleanupExpired(list);
            list.AddLast(snapshot);
            while (list.Count > options.MaxApprovalListSnapshotsPerCallerBinding)
            {
                list.RemoveFirst();
            }
        }

        return snapshot;
    }

    public ApprovalSnapshot Get(string callerBindingId, string snapshotId, string conversationId)
    {
        if (!_snapshots.TryGetValue(callerBindingId, out var list))
        {
            throw new InvalidOperationException("Cursor is invalid or stale.");
        }

        lock (list)
        {
            CleanupExpired(list);
            var snapshot = list.FirstOrDefault(item =>
                item.SnapshotId == snapshotId
                && item.CallerBindingId == callerBindingId
                && item.ConversationId == conversationId);
            return snapshot ?? throw new InvalidOperationException("Cursor is invalid or stale.");
        }
    }

    private static void CleanupExpired(LinkedList<ApprovalSnapshot> list)
    {
        var now = DateTimeOffset.UtcNow;
        var node = list.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.ExpiresAt <= now)
            {
                list.Remove(node);
            }

            node = next;
        }
    }
}

internal sealed record ApprovalSnapshot(
    string SnapshotId,
    string ConversationId,
    string CallerBindingId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<PendingApproval> Items);

internal sealed class ProgrammaticCallerBindingResolver(
    ProgrammaticMcpServerOptions options,
    IDataProtectionProvider dataProtectionProvider,
    IHttpContextAccessor httpContextAccessor) : IProgrammaticCallerBindingTokenService
{
    private const string CookiePurpose = "ProgrammaticMcp.AspNetCore.CallerBinding.Cookie";
    private const string HeaderPurpose = "ProgrammaticMcp.AspNetCore.CallerBinding.Header";

    private readonly IDataProtector _cookieProtector = dataProtectionProvider.CreateProtector(CookiePurpose);
    private readonly IDataProtector _headerProtector = dataProtectionProvider.CreateProtector(HeaderPurpose);

    public async ValueTask<CallerBindingResolution> ResolveAsync(MessageContext context, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext ?? context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        var principal = context.User;
        var principalIdentity = principal?.Identity?.IsAuthenticated == true
            ? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.Identity?.Name
            : null;
        if (!string.IsNullOrWhiteSpace(principalIdentity))
        {
            return new CallerBindingResolution(principalIdentity, "principal", null);
        }

        var sessionIdentity = GetTrustedSessionIdentity(context, httpContext);
        if (!string.IsNullOrWhiteSpace(sessionIdentity))
        {
            return new CallerBindingResolution(sessionIdentity, "session", null);
        }

        if (httpContext is null)
        {
            return new CallerBindingResolution(null, null, null);
        }

        if (options.EnableSignedHeaderCallerBinding
            && httpContext.Request.Headers.TryGetValue(options.SignedHeaderName, out var headerValues))
        {
            var headerValue = headerValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(headerValue))
            {
                var token = Unprotect(headerValue, _headerProtector, HeaderPurpose);
                if (token is not null && token.ExpiresAtUtc > DateTimeOffset.UtcNow && RouteMatches(token))
                {
                    return new CallerBindingResolution(token.CallerBindingId, "signed_header", token.TokenId);
                }
            }
        }

        if (!options.EnableCookieCallerBinding)
        {
            return new CallerBindingResolution(null, null, null);
        }

        if (httpContext.Request.Cookies.TryGetValue(options.CookieName, out var cookieValue))
        {
            var token = Unprotect(cookieValue, _cookieProtector, CookiePurpose);
            if (token is not null && token.ExpiresAtUtc > DateTimeOffset.UtcNow && RouteMatches(token))
            {
                EnsureSameOrigin(httpContext);

                if (token.IssuedAtUtc.AddTicks((token.ExpiresAtUtc - token.IssuedAtUtc).Ticks / 2) <= DateTimeOffset.UtcNow)
                {
                    IssueCookie(httpContext, CreateToken(token.CallerBindingId, options.CookieLifetime, CookiePurpose));
                }

                return new CallerBindingResolution(token.CallerBindingId, "cookie", token.TokenId);
            }
        }

        await Task.CompletedTask;
        return new CallerBindingResolution(null, null, null);
    }

    public string CreateSignedHeaderToken(string callerBindingId)
    {
        var token = CreateToken(callerBindingId, options.HeaderLifetime, HeaderPurpose);
        return _headerProtector.Protect(JsonSerializer.Serialize(token));
    }

    public ValueTask IssueInitialCookieAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (!options.EnableCookieCallerBinding)
        {
            return ValueTask.CompletedTask;
        }

        var principalIdentity = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? httpContext.User.Identity?.Name
            : null;
        if (!string.IsNullOrWhiteSpace(principalIdentity))
        {
            return ValueTask.CompletedTask;
        }

        if (options.EnableSignedHeaderCallerBinding
            && httpContext.Request.Headers.TryGetValue(options.SignedHeaderName, out var headerValues)
            && !string.IsNullOrWhiteSpace(headerValues.FirstOrDefault()))
        {
            return ValueTask.CompletedTask;
        }

        if (httpContext.Request.Cookies.TryGetValue(options.CookieName, out var existingCookie))
        {
            var existing = Unprotect(existingCookie, _cookieProtector, CookiePurpose);
            if (existing is not null && existing.ExpiresAtUtc > DateTimeOffset.UtcNow && RouteMatches(existing))
            {
                return ValueTask.CompletedTask;
            }
        }

        IssueCookie(httpContext, CreateToken("caller-" + Guid.NewGuid().ToString("N"), options.CookieLifetime, CookiePurpose));
        return ValueTask.CompletedTask;
    }

    private ProtectedCallerBindingToken CreateToken(string callerBindingId, TimeSpan lifetime, string purpose)
    {
        return new ProtectedCallerBindingToken(
            callerBindingId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(lifetime),
            purpose,
            options.RoutePrefix,
            Guid.NewGuid().ToString("N"));
    }

    private void IssueCookie(HttpContext httpContext, ProtectedCallerBindingToken token)
    {
        var protectedValue = _cookieProtector.Protect(JsonSerializer.Serialize(token));
        httpContext.Response.Cookies.Append(
            options.CookieName,
            protectedValue,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = !(options.AllowInsecureDevelopmentCookies && IsLoopbackHost(httpContext.Request.Host.Host)),
                IsEssential = true,
                Path = options.RoutePrefix == "/" ? "/" : options.RoutePrefix,
                Expires = token.ExpiresAtUtc
            });
    }

    private void EnsureSameOrigin(HttpContext httpContext)
    {
        var origin = httpContext.Request.Headers.Origin.FirstOrDefault();
        var referer = httpContext.Request.Headers.Referer.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(origin) && string.IsNullOrWhiteSpace(referer))
        {
            throw new InvalidOperationException("Same-origin validation failed for cookie-bound caller binding.");
        }

        if (!string.IsNullOrWhiteSpace(origin) && IsSameOrigin(httpContext, origin))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(referer) && IsSameOrigin(httpContext, referer))
        {
            return;
        }

        throw new InvalidOperationException("Same-origin validation failed for cookie-bound caller binding.");
    }

    private static bool IsSameOrigin(HttpContext httpContext, string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, httpContext.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, httpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == (httpContext.Request.Host.Port ?? (string.Equals(httpContext.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80));
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private bool RouteMatches(ProtectedCallerBindingToken token)
        => string.Equals(token.RoutePrefix, options.RoutePrefix, StringComparison.Ordinal);

    private static string? GetTrustedSessionIdentity(MessageContext context, HttpContext? httpContext)
    {
        return context.Server?.SessionId;
    }

    private static ProtectedCallerBindingToken? Unprotect(string value, IDataProtector protector, string expectedPurpose)
    {
        try
        {
            var json = protector.Unprotect(value);
            var token = JsonSerializer.Deserialize<ProtectedCallerBindingToken>(json);
            return token is not null && string.Equals(token.Purpose, expectedPurpose, StringComparison.Ordinal) ? token : null;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record CallerBindingResolution(string? CallerBindingId, string? Mode, string? TokenId);

internal sealed record ProtectedCallerBindingToken(
    string CallerBindingId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Purpose,
    string RoutePrefix,
    string TokenId);

internal sealed class ProgrammaticToolSchemaRegistry
{
    public JsonElement SearchSchema => ToElement(
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string" },
                ["detailLevel"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Names", "Signatures", "Full") },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
                ["cursor"] = new JsonObject { ["type"] = "string" }
            }
        });

    public JsonElement ExecuteSchema => ToElement(
        new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("conversationId", "code"),
            ["properties"] = new JsonObject
            {
                ["conversationId"] = new JsonObject { ["type"] = "string" },
                ["code"] = new JsonObject { ["type"] = "string" },
                ["entrypoint"] = new JsonObject { ["type"] = "string" },
                ["args"] = new JsonObject { ["type"] = "object" },
                ["visibleApiPaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["timeoutMs"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["maxApiCalls"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["maxResultBytes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["maxStatements"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["memoryBytes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }
            }
        });

    public JsonElement ArtifactReadSchema => ToElement(
        new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("conversationId", "artifactId"),
            ["properties"] = new JsonObject
            {
                ["conversationId"] = new JsonObject { ["type"] = "string" },
                ["artifactId"] = new JsonObject { ["type"] = "string" },
                ["cursor"] = new JsonObject { ["type"] = "string" },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 64 }
            }
        });

    public JsonElement MutationListSchema => ToElement(
        new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("conversationId"),
            ["properties"] = new JsonObject
            {
                ["conversationId"] = new JsonObject { ["type"] = "string" },
                ["cursor"] = new JsonObject { ["type"] = "string" },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 }
            }
        });

    public JsonElement MutationApplySchema => ToElement(
        new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("conversationId", "approvalId", "approvalNonce"),
            ["properties"] = new JsonObject
            {
                ["conversationId"] = new JsonObject { ["type"] = "string" },
                ["approvalId"] = new JsonObject { ["type"] = "string" },
                ["approvalNonce"] = new JsonObject { ["type"] = "string" }
            }
        });

    public JsonElement MutationCancelSchema => MutationApplySchema;

    public IList<Tool> CreateTools()
    {
        return
        [
            CreateTool("capabilities.search", "Progressive discovery for the programmatic capability catalog.", SearchSchema),
            CreateTool("code.execute", "Sandboxed JavaScript execution against the generated programmatic namespace.", ExecuteSchema),
            CreateTool("artifact.read", "Paged artifact retrieval for large execution outputs.", ArtifactReadSchema),
            CreateTool("mutation.list", "Lists pending approvals for the current conversation and caller binding.", MutationListSchema),
            CreateTool("mutation.apply", "Consumes a previously issued approval and executes the mutation.", MutationApplySchema),
            CreateTool("mutation.cancel", "Cancels a previously issued approval.", MutationCancelSchema)
        ];
    }

    private static Tool CreateTool(string name, string description, JsonElement inputSchema)
        => new()
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema
        };

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node, JsonSerializerOptions.Web);
}

internal static class ProgrammaticInstructionsBuilder
{
    public static string Build(ProgrammaticMcpServerOptions options, ProgrammaticMcpRouteState routeState)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Programmatic MCP flow: initialize -> tools/list -> capabilities.search -> code.execute -> artifact.read when needed -> mutation.list/apply/cancel for writes. Types: GET {routeState.TypeEndpointPath}. Mutations require stable caller binding via {DescribeCallerBinding(options)}. conversationId must match ^[A-Za-z0-9._:-]{{1,128}}$. code.execute is synchronous request/response in v0. Limits: timeoutMs={options.ExecutorOptions.TimeoutMs}, maxApiCalls={options.ExecutorOptions.MaxApiCalls}, maxResultBytes={options.ExecutorOptions.MaxResultBytes}, maxStatements={options.ExecutorOptions.MaxStatements}, memoryBytes={options.ExecutorOptions.MemoryBytes}, maxCodeBytes={options.ExecutorOptions.MaxCodeBytes}, maxArgsBytes={options.ExecutorOptions.MaxArgsBytes}, maxConsoleLines={options.ExecutorOptions.MaxConsoleLines}, maxConsoleBytes={options.ExecutorOptions.MaxConsoleBytes}.");
    }

    private static string DescribeCallerBinding(ProgrammaticMcpServerOptions options)
    {
        return (options.EnableCookieCallerBinding, options.EnableSignedHeaderCallerBinding) switch
        {
            (true, true) => "principal or MCP session identity, with built-in HTTP fallback via cookie or signed header",
            (true, false) => "principal or MCP session identity, with built-in HTTP fallback via cookie",
            (false, true) => "principal or MCP session identity, with built-in HTTP fallback via signed header",
            (false, false) => "principal or MCP session identity; no built-in HTTP fallback is enabled"
        };
    }
}

internal sealed class ProgrammaticMcpLifecycleService(
    ILogger<ProgrammaticMcpLifecycleService> logger,
    ProgrammaticMcpServerOptions options,
    IArtifactStore artifactStore,
    IApprovalStore approvalStore,
    ProgrammaticMcpShutdownCoordinator shutdownCoordinator) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var artifactLimit = options.ExecutorOptions.ArtifactRetention.MaxArtifactBytesGlobal;
        var memoryBudget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var warningThreshold = CalculateArtifactWarningThreshold(memoryBudget);
        if (artifactLimit > warningThreshold)
        {
            logger.LogWarning(
                "Configured in-memory artifact limit {ArtifactLimitBytes} bytes exceeds the startup warning threshold of {WarningThresholdBytes} bytes. Consider lowering MaxArtifactBytesGlobal or using a non-memory artifact store for constrained hosts.",
                artifactLimit,
                warningThreshold);
        }

        if (approvalStore is InMemoryApprovalStore inMemoryApprovalStore)
        {
            var recovered = await inMemoryApprovalStore.RecoverStaleApplyingAsync(
                TimeSpan.FromSeconds(options.StaleApplyingTimeoutSeconds),
                static approval => approval with { State = ApprovalState.FailedTerminal, ApplyingSinceUtc = null, FailureCode = "apply_outcome_unknown" },
                cancellationToken);
            if (recovered > 0)
            {
                logger.LogInformation("Recovered {RecoveredCount} stale applying approvals during startup.", recovered);
            }
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (shutdownCoordinator.IsStopping)
            {
                break;
            }

            await RunMaintenanceCycleAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        shutdownCoordinator.BeginShutdown();
        var drained = await shutdownCoordinator.WaitForDrainAsync(options.GracefulShutdownTimeout, cancellationToken);
        if (!drained)
        {
            logger.LogWarning("Graceful shutdown timeout elapsed; cancelling remaining in-flight work.");
            shutdownCoordinator.ForceCancelRemainingWork();
        }

        await base.StopAsync(cancellationToken);
    }

    private static long CalculateArtifactWarningThreshold(long memoryBudgetBytes)
    {
        const long fallbackThresholdBytes = 1_073_741_824;
        if (memoryBudgetBytes <= 0)
        {
            return fallbackThresholdBytes;
        }

        return Math.Min(memoryBudgetBytes / 4, fallbackThresholdBytes);
    }

    private async Task RunMaintenanceCycleAsync(CancellationToken cancellationToken)
    {
        if (approvalStore is InMemoryApprovalStore inMemoryApprovalStore)
        {
            var recovered = await inMemoryApprovalStore.RecoverStaleApplyingAsync(
                TimeSpan.FromSeconds(options.StaleApplyingTimeoutSeconds),
                static approval => approval with { State = ApprovalState.FailedTerminal, ApplyingSinceUtc = null, FailureCode = "apply_outcome_unknown" },
                cancellationToken);
            if (recovered > 0)
            {
                logger.LogInformation("Recovered {RecoveredCount} stale applying approvals during maintenance.", recovered);
            }
        }

        await artifactStore.SweepExpiredAsync(cancellationToken);
        await approvalStore.SweepExpiredAsync(cancellationToken);
    }
}

internal sealed class ProgrammaticMcpToolHandlers(
    ProgrammaticMcpServerOptions options,
    ICapabilityCatalog catalog,
    ICodeExecutionService executionService,
    IArtifactStore artifactStore,
    IApprovalStore approvalStore,
    ProgrammaticCallerBindingResolver callerBindingResolver,
    ProgrammaticToolSchemaRegistry schemas,
    ProgrammaticMcpThrottle throttle,
    ApprovalSnapshotStore snapshotStore,
    ProgrammaticMcpShutdownCoordinator shutdownCoordinator,
    ProgrammaticMcpActivitySource activitySource,
    ILogger<ProgrammaticMcpToolHandlers> logger)
{
    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        using var activity = activitySource.Start("programmatic.tools.list");
        activity?.SetTag("programmatic.capabilityVersion", catalog.CapabilityVersion);
        return ValueTask.FromResult(new ListToolsResult { Tools = schemas.CreateTools() });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (shutdownCoordinator.IsStopping)
        {
            return CreateToolError("server_shutting_down", "The server is shutting down.");
        }

        using var lease = shutdownCoordinator.Enter();
        var name = context.Params?.Name ?? string.Empty;

        using var activity = activitySource.Start("programmatic.tool.call");
        activity?.SetTag("mcp.tool.name", name);

        return name switch
        {
            "capabilities.search" => await HandleSearchAsync(context, cancellationToken),
            "code.execute" => await HandleExecuteAsync(context, cancellationToken),
            "artifact.read" => await HandleArtifactReadAsync(context, cancellationToken),
            "mutation.list" => await HandleMutationListAsync(context, cancellationToken),
            "mutation.apply" => await HandleMutationApplyAsync(context, cancellationToken),
            "mutation.cancel" => await HandleMutationCancelAsync(context, cancellationToken),
            _ => CreateToolError("unknown_tool", $"Unknown tool '{name}'.")
        };
    }

    public async Task WriteTypesAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        try
        {
            var declarations = string.IsNullOrWhiteSpace(catalog.GeneratedTypeScript)
                ? "// No capabilities are currently registered.\n"
                : catalog.GeneratedTypeScript;

            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/typescript";
            httpContext.Response.Headers.ETag = "\"" + catalog.CapabilityVersion + "\"";
            httpContext.Response.Headers.CacheControl = "no-cache, max-age=0";

            if (httpContext.Request.Headers.IfNoneMatch == "\"" + catalog.CapabilityVersion + "\"")
            {
                httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            await httpContext.Response.WriteAsync(declarations, httpContext.RequestAborted);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to render the TypeScript declaration endpoint.");
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsync("Type declaration generation failed.", httpContext.RequestAborted);
        }
    }

    private async ValueTask<CallToolResult> HandleSearchAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var args = ParseArguments(context.Params?.Arguments);
        var query = args["query"]?.GetValue<string>();
        if (query is { Length: > 0 } && Encoding.UTF8.GetByteCount(query) > options.MaxQueryLength)
        {
            return CreateToolError("invalid_params", "query exceeded maxQueryLength.");
        }

        var detailLevel = args["detailLevel"]?.GetValue<string>();
        var limit = args["limit"]?.GetValue<int?>() ?? 20;
        var cursor = args["cursor"]?.GetValue<string>();

        CapabilityDetailLevel parsedDetailLevel = detailLevel switch
        {
            null => CapabilityDetailLevel.Full,
            "Names" => CapabilityDetailLevel.Names,
            "Signatures" => CapabilityDetailLevel.Signatures,
            "Full" => CapabilityDetailLevel.Full,
            _ => throw new InvalidOperationException("detailLevel must be Names, Signatures, or Full.")
        };

        try
        {
            var response = catalog.Search(new CapabilitySearchRequest(query, parsedDetailLevel, limit, cursor));
            return CreateSuccess(response);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return CreateToolError("invalid_params", exception.Message);
        }
    }

    private async ValueTask<CallToolResult> HandleExecuteAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var args = ParseArguments(context.Params?.Arguments);
        if (!TryReadConversationId(args, out var conversationIdValue, out var error))
        {
            return error!;
        }
        var conversationId = conversationIdValue!;

        if (args["code"]?.GetValue<string>() is not { } code)
        {
            return CreateToolError("invalid_params", "code is required.");
        }

        var callerBinding = await TryResolveCallerBindingAsync(context, cancellationToken);
        if (callerBinding.Error is not null)
        {
            return callerBinding.Error;
        }

        var binding = callerBinding.Binding!;
        var httpContext = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        var abuseKey = binding.CallerBindingId ?? CreateAnonymousCallerKey(httpContext);
        var throttleDecision = throttle.TryEnter(abuseKey, DateTimeOffset.UtcNow);
        if (!throttleDecision.IsAllowed)
        {
            return CreateToolError(
                "resource_exhausted",
                "Execution request rejected by built-in throttling.",
                new JsonObject
                {
                    ["reason"] = throttleDecision.Reason,
                    ["retryAfterSeconds"] = throttleDecision.RetryAfterSeconds
                });
        }

        using var throttleLease = throttleDecision.Lease;
        using var executionScope = context.Services?.CreateScope();
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownCoordinator.ForcedCancellationToken);
        var request = new CodeExecutionRequest(
            conversationId,
            code,
            args["entrypoint"]?.GetValue<string>() ?? "main",
            args["args"] as JsonObject,
            args["visibleApiPaths"] is JsonArray visible ? visible.Select(static node => node!.GetValue<string>()).ToArray() : null,
            args["timeoutMs"]?.GetValue<int?>(),
            args["maxApiCalls"]?.GetValue<int?>(),
            args["maxResultBytes"]?.GetValue<int?>(),
            args["maxStatements"]?.GetValue<int?>(),
            args["memoryBytes"]?.GetValue<int?>(),
            binding.CallerBindingId,
            executionScope?.ServiceProvider ?? context.Services,
            context.User);

        try
        {
            var response = await executionService.ExecuteAsync(request, executionCancellation.Token);
            logger.LogInformation(
                "Programmatic code execution finished. ConversationId={ConversationId} ApiCalls={ApiCalls} Diagnostics={DiagnosticCount}",
                conversationId,
                response.Stats.ApiCalls,
                response.Diagnostics.Count);
            return CreateSuccess(response);
        }
        catch (ArgumentException exception)
        {
            return CreateToolError("invalid_params", exception.Message);
        }
    }

    private async ValueTask<CallToolResult> HandleArtifactReadAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var args = ParseArguments(context.Params?.Arguments);
        if (!TryReadConversationId(args, out var conversationIdValue, out var error))
        {
            return error!;
        }
        var conversationId = conversationIdValue!;

        if (args["artifactId"]?.GetValue<string>() is not { } artifactId)
        {
            return CreateToolError("invalid_params", "artifactId is required.");
        }

        var limit = args["limit"]?.GetValue<int?>() ?? 16;
        if (limit is <= 0 or > 64)
        {
            return CreateToolError("invalid_params", "limit must be between 1 and 64.");
        }

        var callerBinding = await TryResolveCallerBindingAsync(context, cancellationToken);
        if (callerBinding.Error is not null)
        {
            return callerBinding.Error;
        }

        var binding = callerBinding.Binding!;
        if (binding.CallerBindingId is null)
        {
            return CreateSuccess(new ArtifactReadResponse(1, catalog.CapabilityVersion, false, null, null, null, null, Array.Empty<ArtifactReadItem>(), null, null, null, null));
        }

        ArtifactReadResult storeResponse;
        try
        {
            storeResponse = await artifactStore.ReadAsync(
                new ArtifactReadRequest(
                    artifactId,
                    conversationId,
                    binding.CallerBindingId,
                    args["cursor"]?.GetValue<string>(),
                    limit),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return CreateToolError("invalid_params", exception.Message);
        }

        if (!storeResponse.Found)
        {
            return CreateSuccess(new ArtifactReadResponse(1, catalog.CapabilityVersion, false, null, null, null, null, Array.Empty<ArtifactReadItem>(), null, null, null, null));
        }

        return CreateSuccess(
            new ArtifactReadResponse(
                1,
                catalog.CapabilityVersion,
                true,
                storeResponse.ArtifactId,
                storeResponse.Kind,
                storeResponse.Name,
                storeResponse.MimeType,
                storeResponse.Items.Select(static item => new ArtifactReadItem(item.Index, item.Content, item.Bytes)).ToArray(),
                storeResponse.NextCursor,
                storeResponse.TotalChunks,
                storeResponse.TotalBytes,
                storeResponse.ExpiresAt?.ToString("O")));
    }

    private async ValueTask<CallToolResult> HandleMutationListAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var args = ParseArguments(context.Params?.Arguments);
        if (!TryReadConversationId(args, out var conversationIdValue, out var error))
        {
            return error!;
        }
        var conversationId = conversationIdValue!;

        var limit = args["limit"]?.GetValue<int?>() ?? 20;
        if (limit is <= 0 or > 100)
        {
            return CreateToolError("invalid_params", "limit must be between 1 and 100.");
        }

        var callerBinding = await TryResolveCallerBindingAsync(context, cancellationToken);
        if (callerBinding.Error is not null)
        {
            return callerBinding.Error;
        }

        var binding = callerBinding.Binding!;
        if (binding.CallerBindingId is null)
        {
            return CreateToolError("permission_denied", "Mutation listing requires caller binding.", new JsonObject { ["reason"] = "missing_caller_binding" });
        }

        var authorized = await catalog.AuthorizationPolicy.AuthorizeAsync(
            new ProgrammaticAuthorizationContext("list", conversationId, binding.CallerBindingId, null, context.User),
            cancellationToken);
        if (!authorized)
        {
            return CreateToolError("permission_denied", "Mutation listing is not authorized.", new JsonObject { ["reason"] = "authorization_denied" });
        }

        try
        {
            ApprovalSnapshot snapshot;
            var cursor = args["cursor"]?.GetValue<string>();
            var offset = 0;
            if (string.IsNullOrWhiteSpace(cursor))
            {
                var approvals = await approvalStore.ListPendingAsync(conversationId, binding.CallerBindingId, cancellationToken);
                snapshot = snapshotStore.Create(conversationId, binding.CallerBindingId, approvals);
            }
            else
            {
                var parsed = ParseApprovalCursor(cursor, conversationId, binding.CallerBindingId);
                snapshot = snapshotStore.Get(binding.CallerBindingId, parsed.SnapshotId, conversationId);
                offset = parsed.Offset;
            }

            var page = snapshot.Items.Skip(offset).Take(limit)
                .Select(static approval => new MutationListItem(
                    approval.PreviewEnvelope.Kind,
                    approval.PreviewEnvelope.ApprovalId,
                    approval.PreviewEnvelope.MutationName,
                    approval.PreviewEnvelope.Summary,
                    approval.PreviewEnvelope.Args.DeepClone().AsObject(),
                    approval.PreviewEnvelope.Preview?.DeepClone(),
                    approval.PreviewEnvelope.ActionArgsHash,
                    approval.PreviewEnvelope.ExpiresAt))
                .ToArray();

            var nextCursor = offset + page.Length < snapshot.Items.Count
                ? CreateApprovalCursor(snapshot.SnapshotId, conversationId, binding.CallerBindingId, offset + page.Length)
                : null;

            return CreateSuccess(new MutationListResponse(1, catalog.CapabilityVersion, page, nextCursor));
        }
        catch (InvalidOperationException exception)
        {
            return CreateToolError("invalid_params", exception.Message);
        }
    }

    private async ValueTask<CallToolResult> HandleMutationApplyAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var args = ParseArguments(context.Params?.Arguments);
        if (!TryReadConversationId(args, out var conversationIdValue, out var error))
        {
            return error!;
        }
        var conversationId = conversationIdValue!;

        if (args["approvalId"]?.GetValue<string>() is not { } approvalId
            || args["approvalNonce"]?.GetValue<string>() is not { } approvalNonce)
        {
            return CreateToolError("invalid_params", "conversationId, approvalId, and approvalNonce are required.");
        }

        var callerBinding = await TryResolveCallerBindingAsync(context, cancellationToken);
        if (callerBinding.Error is not null)
        {
            return callerBinding.Error;
        }

        var binding = callerBinding.Binding!;
        if (binding.CallerBindingId is null)
        {
            return CreateToolError("permission_denied", "Mutation apply requires caller binding.", new JsonObject { ["reason"] = "missing_caller_binding" });
        }

        var approval = await approvalStore.GetAsync(approvalId, cancellationToken);
        if (!IsApprovalVisible(approval, conversationId, binding.CallerBindingId, approvalNonce))
        {
            return CreateSuccess(new MutationApplyResponse(1, catalog.CapabilityVersion, "not_found", approvalId, null, null, Array.Empty<ExecutionArtifactDescriptor>(), null, null, null, null));
        }

        var authorized = await catalog.AuthorizationPolicy.AuthorizeAsync(
            new ProgrammaticAuthorizationContext("apply", conversationId, binding.CallerBindingId, approval!.MutationName, context.User),
            cancellationToken);
        if (!authorized)
        {
            return CreateSuccess(new MutationApplyResponse(1, catalog.CapabilityVersion, "not_found", approvalId, null, null, Array.Empty<ExecutionArtifactDescriptor>(), null, null, null, null));
        }

        var transition = await approvalStore.TryTransitionAsync(
            approvalId,
            ApprovalState.Pending,
            current => current with { State = ApprovalState.Applying, ApplyingSinceUtc = DateTimeOffset.UtcNow },
            cancellationToken);
        if (transition.Status != ApprovalTransitionStatus.Success || transition.Approval is null)
        {
            return CreateSuccess(new MutationApplyResponse(1, catalog.CapabilityVersion, "not_found", approvalId, null, null, Array.Empty<ExecutionArtifactDescriptor>(), null, null, null, null));
        }

        var capability = catalog.Capabilities.Single(item => item.IsMutation && item.MutationApplyHandler is not null && item.ApiPath == transition.Approval!.MutationName);
        var writer = new TransportArtifactWriter(
            artifactStore,
            conversationId,
            binding.CallerBindingId,
            options.ExecutorOptions.ArtifactRetention.ArtifactTtlSeconds,
            options.ExecutorOptions.ArtifactRetention.ArtifactChunkBytes);
        using var applyScope = context.Services?.CreateScope();
        using var applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownCoordinator.ForcedCancellationToken);
        var mutationContext = new ProgrammaticMutationContext(
            conversationId,
            binding.CallerBindingId,
            approvalId,
            applyScope?.ServiceProvider ?? context.Services!,
            applyCancellation.Token,
            writer);

        try
        {
            var applyHandler = capability.MutationApplyHandler ?? throw new InvalidOperationException("Mutation apply handler is not registered.");
            var applyResult = await applyHandler(transition.Approval.Args.DeepClone().AsObject(), mutationContext);
            if (applyResult.IsSuccess)
            {
                if (applyResult.Value is not null && capability.ApplyResultSchema is not null)
                {
                    JsonSchemaValidator.Validate(applyResult.Value, capability.ApplyResultSchema);
                }

                await approvalStore.TryTransitionAsync(
                    approvalId,
                    ApprovalState.Applying,
                    current => current with { State = ApprovalState.Completed, ApplyingSinceUtc = null },
                    CancellationToken.None);

                var artifacts = writer.Descriptors.ToArray();
                return CreateSuccess(
                    new MutationApplyResponse(
                        1,
                        catalog.CapabilityVersion,
                        "completed",
                        approvalId,
                        transition.Approval.ActionArgsHash,
                        applyResult.Value,
                        artifacts,
                        artifacts.FirstOrDefault(static item => item.Kind == "execution.result")?.ArtifactId,
                        null,
                        null,
                        null));
            }

            var failureCode = applyResult.FailureCode ?? "apply_handler_error";
            if (applyResult.FailureKind == MutationApplyFailureKind.Retryable)
            {
                await approvalStore.TryTransitionAsync(
                    approvalId,
                    ApprovalState.Applying,
                    current => current with { State = ApprovalState.Pending, ApplyingSinceUtc = null, FailureCode = failureCode },
                    CancellationToken.None);
            }
            else
            {
                await approvalStore.TryTransitionAsync(
                    approvalId,
                    ApprovalState.Applying,
                    current => current with { State = ApprovalState.FailedTerminal, ApplyingSinceUtc = null, FailureCode = failureCode },
                    CancellationToken.None);
            }

            return CreateSuccess(
                new MutationApplyResponse(
                    1,
                    catalog.CapabilityVersion,
                    "failed",
                    approvalId,
                    transition.Approval.ActionArgsHash,
                    null,
                    Array.Empty<ExecutionArtifactDescriptor>(),
                    null,
                    failureCode,
                    applyResult.FailureKind == MutationApplyFailureKind.Retryable,
                    applyResult.Message));
        }
        catch (JsonSchemaValidationException exception)
        {
            logger.LogWarning(exception, "Mutation apply result validation failed for approval {ApprovalId}.", approvalId);
            await approvalStore.TryTransitionAsync(
                approvalId,
                ApprovalState.Applying,
                current => current with { State = ApprovalState.FailedTerminal, ApplyingSinceUtc = null, FailureCode = "validation_failed" },
                CancellationToken.None);

            return CreateSuccess(
                new MutationApplyResponse(
                    1,
                    catalog.CapabilityVersion,
                    "failed",
                    approvalId,
                    transition.Approval.ActionArgsHash,
                    null,
                    Array.Empty<ExecutionArtifactDescriptor>(),
                    null,
                    "validation_failed",
                    false,
                    exception.Message));
        }
        catch (OperationCanceledException) when (applyCancellation.IsCancellationRequested)
        {
            logger.LogWarning("Mutation apply was cancelled during shutdown for approval {ApprovalId}.", approvalId);
            await approvalStore.TryTransitionAsync(
                approvalId,
                ApprovalState.Applying,
                current => current with { State = ApprovalState.FailedTerminal, ApplyingSinceUtc = null, FailureCode = "apply_outcome_unknown" },
                CancellationToken.None);

            return CreateSuccess(
                new MutationApplyResponse(
                    1,
                    catalog.CapabilityVersion,
                    "failed",
                    approvalId,
                    transition.Approval.ActionArgsHash,
                    null,
                    Array.Empty<ExecutionArtifactDescriptor>(),
                    null,
                    "apply_outcome_unknown",
                    false,
                    "The mutation apply outcome is unknown."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Mutation apply failed unexpectedly for approval {ApprovalId}.", approvalId);
            await approvalStore.TryTransitionAsync(
                approvalId,
                ApprovalState.Applying,
                current => current with { State = ApprovalState.FailedTerminal, ApplyingSinceUtc = null, FailureCode = "apply_handler_error" },
                CancellationToken.None);

            return CreateSuccess(
                new MutationApplyResponse(
                    1,
                    catalog.CapabilityVersion,
                    "failed",
                    approvalId,
                    transition.Approval.ActionArgsHash,
                    null,
                    Array.Empty<ExecutionArtifactDescriptor>(),
                    null,
                    "apply_handler_error",
                    false,
                    "The mutation apply handler failed."));
        }
    }

    private async ValueTask<CallToolResult> HandleMutationCancelAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var args = ParseArguments(context.Params?.Arguments);
        if (!TryReadConversationId(args, out var conversationIdValue, out var error))
        {
            return error!;
        }
        var conversationId = conversationIdValue!;

        if (args["approvalId"]?.GetValue<string>() is not { } approvalId
            || args["approvalNonce"]?.GetValue<string>() is not { } approvalNonce)
        {
            return CreateToolError("invalid_params", "conversationId, approvalId, and approvalNonce are required.");
        }

        var callerBinding = await TryResolveCallerBindingAsync(context, cancellationToken);
        if (callerBinding.Error is not null)
        {
            return callerBinding.Error;
        }

        var binding = callerBinding.Binding!;
        if (binding.CallerBindingId is null)
        {
            return CreateToolError("permission_denied", "Mutation cancel requires caller binding.", new JsonObject { ["reason"] = "missing_caller_binding" });
        }

        var approval = await approvalStore.GetAsync(approvalId, cancellationToken);
        if (!IsApprovalVisible(approval, conversationId, binding.CallerBindingId, approvalNonce))
        {
            return CreateSuccess(new MutationCancelResponse(1, catalog.CapabilityVersion, "not_found", approvalId, null));
        }

        var authorized = await catalog.AuthorizationPolicy.AuthorizeAsync(
            new ProgrammaticAuthorizationContext("cancel", conversationId, binding.CallerBindingId, approval!.MutationName, context.User),
            cancellationToken);
        if (!authorized)
        {
            return CreateSuccess(new MutationCancelResponse(1, catalog.CapabilityVersion, "not_found", approvalId, null));
        }

        var transition = await approvalStore.TryTransitionAsync(
            approvalId,
            ApprovalState.Pending,
            current => current with { State = ApprovalState.Cancelled, ApplyingSinceUtc = null },
            cancellationToken);

        return transition.Status == ApprovalTransitionStatus.Success
            ? CreateSuccess(new MutationCancelResponse(1, catalog.CapabilityVersion, "cancelled", approvalId, transition.Approval!.ActionArgsHash))
            : CreateSuccess(new MutationCancelResponse(1, catalog.CapabilityVersion, "not_found", approvalId, null));
    }

    private static bool IsApprovalVisible(PendingApproval? approval, string conversationId, string callerBindingId, string approvalNonce)
    {
        return approval is not null
            && approval.State == ApprovalState.Pending
            && approval.ExpiresAt > DateTimeOffset.UtcNow
            && approval.ConversationId == conversationId
            && approval.CallerBindingId == callerBindingId
            && approval.ApprovalNonce == approvalNonce;
    }

    private async ValueTask<(CallerBindingResolution? Binding, CallToolResult? Error)> TryResolveCallerBindingAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await callerBindingResolver.ResolveAsync(context, cancellationToken), null);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Same-origin validation failed", StringComparison.Ordinal))
        {
            return (null, CreateToolError("permission_denied", "Cookie-bound caller binding failed same-origin validation.", new JsonObject
            {
                ["reason"] = "origin_validation_failed"
            }));
        }
    }

    private static string CreateAnonymousCallerKey(HttpContext? httpContext)
    {
        var ip = httpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();
        return "anon:" + ip + ":" + userAgent;
    }

    private static JsonObject ParseArguments(IDictionary<string, JsonElement>? arguments)
    {
        var result = new JsonObject();
        if (arguments is null)
        {
            return result;
        }

        foreach (var pair in arguments)
        {
            result[pair.Key] = JsonNode.Parse(pair.Value.GetRawText());
        }

        return result;
    }

    private CallToolResult CreateInvalidConversationIdError(bool missing)
        => CreateToolError("invalid_params", missing ? "conversationId is required." : "conversationId is invalid.");

    private bool TryReadConversationId(JsonObject args, out string? conversationId, out CallToolResult? error)
    {
        if (args["conversationId"]?.GetValue<string>() is not { } value)
        {
            conversationId = null;
            error = CreateInvalidConversationIdError(missing: true);
            return false;
        }

        if (!ConversationIdValidator.IsValid(value))
        {
            conversationId = null;
            error = CreateInvalidConversationIdError(missing: false);
            return false;
        }

        conversationId = value;
        error = null;
        return true;
    }

    private CallToolResult CreateSuccess<T>(T payload)
    {
        JsonNode node = JsonSerializer.SerializeToNode(payload, JsonSerializerOptions.Web)!;
        return CreateToolResult(node, isError: false);
    }

    private CallToolResult CreateToolError(string code, string message, JsonObject? data = null)
    {
        var payload = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = data
            }
        };

        return CreateToolResult(payload, isError: true);
    }

    private CallToolResult CreateToolResult(JsonNode payload, bool isError)
    {
        var json = CanonicalJson.Serialize(payload);
        IList<ContentBlock> content = [];
        if (options.EnableCompatibilityTextMirroring && Encoding.UTF8.GetByteCount(json) <= options.CompatibilityTextMirrorMaxBytes)
        {
            content = [new TextContentBlock { Text = json }];
        }

        return new CallToolResult
        {
            IsError = isError,
            StructuredContent = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web),
            Content = content
        };
    }

    private static string CreateApprovalCursor(string snapshotId, string conversationId, string callerBindingId, int offset)
    {
        var payload = new JsonObject
        {
            ["snapshotId"] = snapshotId,
            ["conversationId"] = conversationId,
            ["callerBindingId"] = callerBindingId,
            ["offset"] = offset
        };

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    private static ApprovalCursor ParseApprovalCursor(string cursor, string conversationId, string callerBindingId)
    {
        try
        {
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)))!.AsObject();
            if (payload["conversationId"]?.GetValue<string>() != conversationId
                || payload["callerBindingId"]?.GetValue<string>() != callerBindingId)
            {
                throw new InvalidOperationException("Cursor is invalid or stale.");
            }

            return new ApprovalCursor(
                payload["snapshotId"]?.GetValue<string>() ?? throw new InvalidOperationException("Cursor is invalid or stale."),
                payload["offset"]?.GetValue<int>() ?? 0);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("Cursor is invalid or stale.", exception);
        }
    }

    private sealed record ApprovalCursor(string SnapshotId, int Offset);

    private sealed class TransportArtifactWriter(
        IArtifactStore artifactStore,
        string conversationId,
        string callerBindingId,
        int artifactTtlSeconds,
        int artifactChunkBytes) : IArtifactWriter
    {
        private readonly List<ExecutionArtifactDescriptor> _descriptors = [];

        public IReadOnlyList<ExecutionArtifactDescriptor> Descriptors => _descriptors;

        public async ValueTask<ExecutionArtifactDescriptor> WriteJsonArtifactAsync(string name, JsonNode payload, CancellationToken cancellationToken = default)
        {
            var text = CanonicalJson.Serialize(payload);
            return await WriteAsync("execution.result", name, "application/json", text, cancellationToken);
        }

        public async ValueTask<ExecutionArtifactDescriptor> WriteTextArtifactAsync(string name, string content, string mimeType, CancellationToken cancellationToken = default)
        {
            return await WriteAsync("handler.output", name, mimeType, content, cancellationToken);
        }

        private async ValueTask<ExecutionArtifactDescriptor> WriteAsync(string kind, string name, string mimeType, string content, CancellationToken cancellationToken)
        {
            var artifactId = "artifact-" + Guid.NewGuid().ToString("N");
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(artifactTtlSeconds);
            await artifactStore.WriteAsync(
                new ArtifactWriteRequest(
                    artifactId,
                    conversationId,
                    callerBindingId,
                    kind,
                    name,
                    mimeType,
                    content,
                    expiresAt),
                cancellationToken);

            var totalBytes = Encoding.UTF8.GetByteCount(content);
            var totalChunks = Math.Max(1, (int)Math.Ceiling((double)totalBytes / Math.Max(1, artifactChunkBytes)));
            var descriptor = new ExecutionArtifactDescriptor(artifactId, kind, name, mimeType, totalBytes, totalChunks, expiresAt.ToString("O"));
            _descriptors.Add(descriptor);
            return descriptor;
        }
    }
}
