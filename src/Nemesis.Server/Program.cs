using Nemesis.AgentCore;
using Nemesis.AgentCore.Models;
using Nemesis.AgentCore.Services;
using Nemesis.Server.Hubs;
using Nemesis.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Load configuration
var config = new NemesisConfiguration();
builder.Configuration.GetSection("Nemesis").Bind(config);

// Add services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.EnableDetailedErrors = true;
});

builder.Services.AddNemesisAgentCore(config);
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<PersistenceService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Initialize services
await app.Services.InitializeNemesisAsync();

// Load persistent data
var persistence = app.Services.GetRequiredService<PersistenceService>();
await persistence.LoadAsync();
Console.WriteLine($"Données persistantes chargées depuis: {persistence.DataPath}");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAntiforgery();

app.MapRazorComponents<Nemesis.Server.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapHub<AgentHub>("/hubs/agent");

Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════╗
║                                                               ║
║   ███╗   ██╗███████╗███╗   ███╗███████╗███████╗██╗███████╗   ║
║   ████╗  ██║██╔════╝████╗ ████║██╔════╝██╔════╝██║██╔════╝   ║
║   ██╔██╗ ██║█████╗  ██╔████╔██║█████╗  ███████╗██║███████╗   ║
║   ██║╚██╗██║██╔══╝  ██║╚██╔╝██║██╔══╝  ╚════██║██║╚════██║   ║
║   ██║ ╚████║███████╗██║ ╚═╝ ██║███████╗███████║██║███████║   ║
║   ╚═╝  ╚═══╝╚══════╝╚═╝     ╚═╝╚══════╝╚══════╝╚═╝╚══════╝   ║
║                                                               ║
║          Unity AI Development Assistant                       ║
║                                                               ║
╚═══════════════════════════════════════════════════════════════╝
");

Console.WriteLine($"Server running at: http://localhost:5000");
Console.WriteLine($"Press Ctrl+C to stop.");

app.Run();
