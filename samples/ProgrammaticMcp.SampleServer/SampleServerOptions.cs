namespace ProgrammaticMcp.SampleServer;

public sealed class SampleServerOptions
{
    public const string SectionName = "SampleServer";
    public const string BrowserToolingCorsPolicyName = "SampleBrowserTooling";

    public SampleCorsOptions Cors { get; set; } = new();
}

public sealed class SampleCorsOptions
{
    public bool EnableBrowserTooling { get; set; }

    public string[] AllowedOrigins { get; set; } =
    [
        "http://127.0.0.1:3000",
        "http://localhost:3000"
    ];
}
