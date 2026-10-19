using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MowerManager;
using MowerManager.Auth;
using MowerManager.Data;
using MowerManager.Husqvarna;
using MowerManager.Monitoring;
using MowerManager.Notifications;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddOptions<HusqvarnaOptions>().BindConfiguration("Husqvarna").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<PollingOptions>().BindConfiguration("Polling");
services.AddOptions<BatteryRuleOptions>().BindConfiguration("BatteryRule");
services.AddOptions<PushoverOptions>().BindConfiguration("Pushover");

services.AddSingleton(TimeProvider.System);
services.AddDbContext<AppDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Sqlite")));

// Persist cookie encryption keys so sessions survive container restarts.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));
builder.AddAppAuth();

services.AddSingleton<HusqvarnaTokenProvider>();
services.AddTransient<HusqvarnaAuthHandler>();
services.AddHttpClient<AutomowerClient>((sp, c) =>
        c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<HusqvarnaOptions>>().Value.ApiUrl))
    .AddHttpMessageHandler<HusqvarnaAuthHandler>();
services.AddHttpClient<INotifier, PushoverNotifier>();

services.AddScoped<MowerMonitor>();
if (builder.Configuration.GetValue("Husqvarna:StreamEnabled", true))
{
    services.AddSingleton<MowerStream>();
    services.AddHostedService(sp => sp.GetRequiredService<MowerStream>());
}
services.AddHostedService<MowerPoller>();

services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddProblemDetails();
services.AddHealthChecks();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider);
    if (!scope.ServiceProvider.GetRequiredService<INotifier>().Enabled)
        app.Logger.LogInformation("Pushover not configured; notifications are off.");
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
// After authorization so the fallback policy protects static files.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAuthApi(app.Services.GetRequiredService<AuthOptions>());
app.MapMowerApi();

app.Run();

public partial class Program;
