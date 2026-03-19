using ProgrammaticMcp.SampleServer;

var builder = WebApplication.CreateBuilder(args);
SampleServerHosting.ConfigureServices(builder);

var app = builder.Build();
SampleServerHosting.ConfigureApp(app);

app.Run();

public partial class Program;
