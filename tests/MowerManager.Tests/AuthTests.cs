using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MowerManager.Monitoring;
using TUnit.Core.Interfaces;

namespace MowerManager.Tests;

public sealed class AppFactory : WebApplicationFactory<Program>, IAsyncInitializer
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mower-tests").FullName;

    // Start the host once up front; parallel tests would otherwise race to create it.
    public Task InitializeAsync()
    {
        _ = Server;
        return Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(_dir, "test.db")}");
        builder.UseSetting("Husqvarna:AppKey", "key");
        builder.UseSetting("Husqvarna:AppSecret", "secret");
        builder.UseSetting("Husqvarna:StreamEnabled", "false");
        builder.UseSetting("Auth:Local:InitialAdminUsername", "admin");
        builder.UseSetting("Auth:Local:InitialAdminPassword", "admin-password");
        builder.ConfigureServices(s => s.Remove(s.Single(d => d.ImplementationType == typeof(MowerPoller))));
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<HttpClient> LoginAsync(string username = "admin", string password = "admin-password")
    {
        var client = Client();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        return client;
    }
}

[ClassDataSource<AppFactory>(Shared = SharedType.PerClass)]
public class AuthTests(AppFactory app)
{
    [Test]
    [Arguments("/")]
    [Arguments("/index.html")]
    [Arguments("/users.html")]
    public async Task Pages_redirect_to_login(string path)
    {
        var res = await app.Client().GetAsync(path);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(res.Headers.Location!.PathAndQuery).StartsWith("/login");
    }

    [Test]
    [Arguments("/api/mowers")]
    [Arguments("/api/settings")]
    [Arguments("/api/users")]
    public async Task Api_returns_401(string path) =>
        await Assert.That((await app.Client().GetAsync(path)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

    [Test]
    [Arguments("/login")]
    [Arguments("/health")]
    [Arguments("/api/auth/options")]
    public async Task Anonymous_endpoints(string path) =>
        await Assert.That((await app.Client().GetAsync(path)).StatusCode).IsEqualTo(HttpStatusCode.OK);

    [Test]
    public async Task Wrong_password_rejected()
    {
        var res = await app.Client().PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope-nope" });
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Logged_in_user_can_use_app()
    {
        var client = await app.LoginAsync();
        await Assert.That((await client.GetAsync("/")).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.GetAsync("/api/mowers")).StatusCode).IsEqualTo(HttpStatusCode.OK);

        var put = await client.PutAsJsonAsync("/api/settings", new { scope = "SecondaryArea", thresholdPercent = 30, action = "Pause" });
        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var settings = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/settings");
        await Assert.That(settings!["scope"].ToString()).IsEqualTo("SecondaryArea");
    }

    [Test]
    public async Task Notifications_off_without_pushover_config()
    {
        var client = await app.LoginAsync();
        var settings = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/settings");
        await Assert.That(settings!["notificationsEnabled"].ToString()).IsEqualTo("False");
        await Assert.That((await client.PostAsync("/api/notifications/test", null)).StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task User_lifecycle()
    {
        var admin = await app.LoginAsync();
        var created = await admin.PostAsJsonAsync("/api/users", new { username = "bob", password = "bob-password" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var bobId = (await created.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["id"].ToString();

        await Assert.That((await admin.PostAsJsonAsync("/api/users", new { username = "BOB", password = "another-pass" })).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);

        // Password change ends bob's existing session.
        var bob = await app.LoginAsync("bob", "bob-password");
        await Assert.That((await admin.PutAsJsonAsync($"/api/users/{bobId}/password", new { password = "new-bob-password" })).StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await bob.GetAsync("/api/mowers")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        bob = await app.LoginAsync("bob", "new-bob-password");
        await Assert.That((await bob.DeleteAsync($"/api/users/{bobId}")).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        await Assert.That((await admin.DeleteAsync($"/api/users/{bobId}")).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await bob.GetAsync("/api/mowers")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
