using AspireRunner.AspNetCore;
using AspireRunner.Installer;
using ProcessManagerRunner.AspNetCore;
using ProcessManagerRunner.Example.WebApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();

builder.Services.Configure<NodeAppOptions>(builder.Configuration.GetSection(NodeAppOptions.SectionName));

builder.AddProcessManager();

builder.Services.AddProcessManagerService(config =>
{
    builder.Configuration.GetSection("NodeApp").Bind(config);
    config.BurstDelayOutput = 500;
});
builder.Services.AddAspireDashboard(); 
builder.Services.AddAspireDashboardInstaller(); 
// builder.Services.AddHostedService<NodeAppHostedService>();

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    message = "ProcessManagerRunner Example",
    nodeApp = "Node app is managed by Process Manager; stdout/stderr are streamed to application logs.",
}));

app.MapGet("/health", () => Results.Ok("Healthy"));

app.Run();
