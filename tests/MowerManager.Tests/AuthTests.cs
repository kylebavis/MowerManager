using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MowerManager.Monitoring;

namespace MowerManager.Tests;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mower-tests").FullName;

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
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        return client;
    }
}

public class AuthTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/users.html")]
    public async Task Pages_redirect_to_login(string path)
    {
        var res = await app.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/login", res.Headers.Location!.PathAndQuery);
    }

    [Theory]
    [InlineData("/api/mowers")]
    [InlineData("/api/settings")]
    [InlineData("/api/users")]
    public async Task Api_returns_401(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client().GetAsync(path)).StatusCode);

    [Theory]
    [InlineData("/login")]
    [InlineData("/health")]
    [InlineData("/api/auth/options")]
    public async Task Anonymous_endpoints(string path) =>
        Assert.Equal(HttpStatusCode.OK, (await app.Client().GetAsync(path)).StatusCode);

    [Fact]
    public async Task Wrong_password_rejected()
    {
        var res = await app.Client().PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Logged_in_user_can_use_app()
    {
        var client = await app.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/mowers")).StatusCode);

        var put = await client.PutAsJsonAsync("/api/settings", new { scope = "SecondaryArea", thresholdPercent = 30, action = "Pause" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var settings = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/settings");
        Assert.Equal("SecondaryArea", settings!["scope"].ToString());
    }

    [Fact]
    public async Task Notifications_off_without_pushover_config()
    {
        var client = await app.LoginAsync();
        var settings = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/settings");
        Assert.Equal("False", settings!["notificationsEnabled"].ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/notifications/test", null)).StatusCode);
    }

    [Fact]
    public async Task User_lifecycle()
    {
        var admin = await app.LoginAsync();
        var created = await admin.PostAsJsonAsync("/api/users", new { username = "bob", password = "bob-password" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bobId = (await created.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["id"].ToString();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/users", new { username = "BOB", password = "another-pass" })).StatusCode);

        // Password change ends bob's existing session.
        var bob = await app.LoginAsync("bob", "bob-password");
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync($"/api/users/{bobId}/password", new { password = "new-bob-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/mowers")).StatusCode);

        bob = await app.LoginAsync("bob", "new-bob-password");
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.DeleteAsync($"/api/users/{bobId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{bobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/mowers")).StatusCode);
    }
}
