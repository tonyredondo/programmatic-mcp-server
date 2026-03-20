using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProgrammaticMcp.AspNetCore;
using System.Text.Json;

namespace ProgrammaticMcp.SampleServer;

public static class SampleServerHosting
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(SampleServerOptions.SectionName).Get<SampleServerOptions>() ?? new SampleServerOptions();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(SampleWorkspace.CreateDefault());
        builder.Services.AddHealthChecks();

        if (settings.Cors.EnableBrowserTooling && settings.Cors.AllowedOrigins.Length > 0)
        {
            builder.Services.AddCors(
                cors =>
                    cors.AddPolicy(
                        SampleServerOptions.BrowserToolingCorsPolicyName,
                        policy =>
                            policy.WithOrigins(settings.Cors.AllowedOrigins)
                                .AllowAnyHeader()
                                .AllowCredentials()
                                .WithMethods("GET", "POST", "OPTIONS")));
        }

        builder.Services.AddProgrammaticMcpServer(
            options =>
            {
                options.ServerName = "ProgrammaticMcp.SampleServer";
                options.ServerVersion = "0.1.0";
                options.EnableStatefulHttpTransport = false;
                options.AllowInsecureDevelopmentCookies = ShouldAllowInsecureDevelopmentCookies(builder);
                options.EnableSignedHeaderCallerBinding = true;
                options.Builder.AllowAllBoundCallers();
                options.ConfigureCatalog(RegisterCatalog);
            });
    }

    public static void ConfigureApp(WebApplication app)
    {
        var settings = app.Services.GetRequiredService<SampleServerOptions>();
        if (settings.Cors.EnableBrowserTooling && settings.Cors.AllowedOrigins.Length > 0)
        {
            app.UseCors(SampleServerOptions.BrowserToolingCorsPolicyName);
        }

        app.MapGet(
            "/",
            (SampleWorkspace workspace) => Results.Ok(
                new
                {
                    project = "ProgrammaticMcp.SampleServer",
                    surface = "sample server",
                    status = "ready",
                    endpoints = new
                    {
                        mcp = "/mcp",
                        types = "/mcp/types",
                        health = "/mcp/health"
                    },
                    resourceUris = new[]
                    {
                        "sample://workspace/guide",
                        "sample://workspace/projects"
                    },
                    sampleIds = new
                    {
                        openTask = workspace.GetCurrentOpenTaskId(),
                        completedTask = "task-3",
                        projects = new[] { "project-alpha", "project-beta" }
                    }
                }));

        app.MapProgrammaticMcpServer("/mcp");
        app.MapHealthChecks(
            "/mcp/health",
            new HealthCheckOptions
            {
                AllowCachingResponses = false
            });
    }

    private static bool ShouldAllowInsecureDevelopmentCookies(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            return false;
        }

        var configuredUrls = builder.Configuration["ASPNETCORE_URLS"] ?? builder.Configuration["urls"];
        if (string.IsNullOrWhiteSpace(configuredUrls))
        {
            return true;
        }

        return configuredUrls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(IsLoopbackUrl);
    }

    private static bool IsLoopbackUrl(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static void RegisterCatalog(ProgrammaticMcpBuilder catalog)
    {
        catalog.AddResource(
            "sample://workspace/guide",
            resource => resource
                .WithName("Sample workspace guide")
                .WithDescription("Explains the sample workspace, capability flow, and available resources.")
                .WithMimeType("text/markdown")
                .WithText(
                    """
                    # Sample Workspace Guide

                    This sample server demonstrates the programmatic MCP flow with a small in-memory task workspace.

                    Use the six programmatic tools for progressive discovery and execution:
                    - `capabilities.search`
                    - `code.execute`
                    - `artifact.read`
                    - `mutation.list`
                    - `mutation.apply`
                    - `mutation.cancel`

                    Available MCP resources:
                    - `sample://workspace/guide`
                    - `sample://workspace/projects`
                    """));

        catalog.AddResource(
            "sample://workspace/projects",
            resource => resource
                .WithName("Sample project snapshot")
                .WithDescription("Returns the current sample project list as JSON text.")
                .WithMimeType("application/json")
                .WithReader(
                    context =>
                    {
                        var workspace = context.Services.GetRequiredService<SampleWorkspace>();
                        return ValueTask.FromResult(JsonSerializer.Serialize(workspace.ListProjects(), JsonSerializerOptions.Web));
                    }));

        catalog.AddCapability<ProjectsListInput, ProjectsListResult>(
            "projects.list",
            capability => capability
                .WithDescription("Lists the sample projects and their open task counts.")
                .UseWhen("You need the available projects before filtering task work.")
                .DoNotUseWhen("You already know the exact task you need.")
                .WithHandler((_, context) =>
                {
                    var workspace = context.Services.GetRequiredService<SampleWorkspace>();
                    return ValueTask.FromResult(workspace.ListProjects());
                }));

        catalog.AddCapability<TasksListInput, TasksListResult>(
            "tasks.list",
            capability => capability
                .WithDescription("Lists tasks from the in-memory sample workspace.")
                .UseWhen("You need to browse tasks or filter by project.")
                .DoNotUseWhen("You already know the exact task id.")
                .WithHandler((input, context) =>
                {
                    var workspace = context.Services.GetRequiredService<SampleWorkspace>();
                    return ValueTask.FromResult(workspace.ListTasks(input));
                }));

        catalog.AddCapability<TaskByIdInput, TaskDetailsResult>(
            "tasks.getById",
            capability => capability
                .WithDescription("Returns one task with project and status details.")
                .UseWhen("You already know the task id and want the full record.")
                .DoNotUseWhen("You only need a broad list of tasks.")
                .WithHandler((input, context) =>
                {
                    var workspace = context.Services.GetRequiredService<SampleWorkspace>();
                    return ValueTask.FromResult(workspace.GetTaskById(input));
                }));

        catalog.AddCapability<ExportReportInput, ExportedReportResult>(
            "tasks.exportReport",
            capability => capability
                .WithDescription("Creates a report payload large enough to demonstrate artifact spill.")
                .UseWhen("You need a large task report without keeping all of it inline.")
                .DoNotUseWhen("You only need a short inline summary.")
                .WithHandler((input, context) =>
                {
                    var workspace = context.Services.GetRequiredService<SampleWorkspace>();
                    return ValueTask.FromResult(workspace.ExportReport(input));
                }));

        catalog.AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
            "tasks.complete",
            mutation => mutation
                .WithDescription("Marks one sample task as completed.")
                .UseWhen("You want to complete an open task after previewing the change.")
                .DoNotUseWhen("You are still exploring or the task is already completed.")
                .WithPreviewHandler((input, context) =>
                {
                    var workspace = context.Services.GetRequiredService<SampleWorkspace>();
                    return ValueTask.FromResult(workspace.PreviewCompleteTask(input));
                })
                .WithSummaryFactory((input, preview, _) => ValueTask.FromResult($"Complete {preview.TaskId} in {preview.ProjectName}"))
                .WithApplyHandler((input, context) =>
                {
                    var workspace = context.Services.GetRequiredService<SampleWorkspace>();
                    return ValueTask.FromResult(workspace.ApplyCompleteTask(input));
                }));
    }
}
