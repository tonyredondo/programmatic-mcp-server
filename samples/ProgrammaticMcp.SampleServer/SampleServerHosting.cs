using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProgrammaticMcp.AspNetCore;

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
                options.AllowInsecureDevelopmentCookies = builder.Environment.IsDevelopment();
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
            () => Results.Ok(
                new
                {
                    project = "ProgrammaticMcp.SampleServer",
                    phase = "Phase 5 sample server",
                    status = "ready",
                    endpoints = new
                    {
                        mcp = "/mcp",
                        types = "/mcp/types",
                        health = "/mcp/health"
                    },
                    sampleIds = new
                    {
                        openTask = "task-1",
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

    private static void RegisterCatalog(ProgrammaticMcpBuilder catalog)
    {
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
