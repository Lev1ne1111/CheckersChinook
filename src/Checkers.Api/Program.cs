using Checkers.Api.Engine;
using Checkers.Api.Logging;
using Checkers.Api.Moves;
using Checkers.Api.Options;

var builder = WebApplication.CreateBuilder(args);

// Sections of appsettings.json -> typed classes, injected as IOptions<T>
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection(EngineOptions.Section));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.Section));
builder.Services.Configure<LimitsOptions>(builder.Configuration.GetSection(LimitsOptions.Section));
builder.Services.Configure<RequestLogOptions>(builder.Configuration.GetSection(RequestLogOptions.Section));

// Engine: real worker pool, or a stand-in that needs no engine installed
bool fakeEngine = string.Equals(builder.Configuration["Engine:Type"], "fake", StringComparison.OrdinalIgnoreCase);
if (fakeEngine)
    builder.Services.AddSingleton<IEnginePool, FakeEnginePool>();
else
    builder.Services.AddSingleton<IEnginePool, EnginePool>();
// Hosted services start before the server accepts requests: the workers are warm when /healthz first answers.
builder.Services.AddHostedService<EngineStartup>();

builder.Services.AddSingleton<MoveService>();
builder.Services.AddSingleton<RequestLogWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RequestLogWriter>());

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();   // /openapi/v1.json
}

app.UseMiddleware<RequestLogMiddleware>();

// Test board: wwwroot/index.html at "/"
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();

// Lets the integration tests start the app with WebApplicationFactory<Program>
public partial class Program;
