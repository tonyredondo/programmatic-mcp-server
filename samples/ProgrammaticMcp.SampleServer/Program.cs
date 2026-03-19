var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(
    "/",
    () => Results.Ok(
        new
        {
            project = "ProgrammaticMcp.SampleServer",
            phase = "Phase 1 bootstrap",
            status = "ready"
        }));

app.Run();
