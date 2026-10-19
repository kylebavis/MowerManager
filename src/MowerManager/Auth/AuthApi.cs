using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MowerManager.Data;

namespace MowerManager.Auth;

public record LoginRequest(string Username, string Password);
public record CreateUserRequest(string Username, string Password);
public record PasswordRequest(string Password);

public static class AuthApi
{
    // Verified against when the user doesn't exist, so timing doesn't reveal valid usernames.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString());

    public static void MapAuthApi(this IEndpointRouteBuilder app, AuthOptions options)
    {
        app.MapGet("/login", (HttpContext ctx, string? returnUrl, string? error, string? signedOut, IWebHostEnvironment env) =>
        {
            if (options.Oidc is { Enabled: true, AutoRedirect: true } && !options.Local.Enabled && error is null && signedOut is null)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [AuthSetup.Oidc]);
            return Results.File(env.WebRootFileProvider.GetFileInfo("login.html").PhysicalPath!, "text/html");
        }).AllowAnonymous();

        app.MapGet("/api/auth/options", () => new
        {
            local = options.Local.Enabled,
            oidc = options.Oidc.Enabled,
            oidcName = options.Oidc.DisplayName,
        }).AllowAnonymous();

        if (options.Oidc.Enabled)
        {
            app.MapGet("/auth/oidc", (string? returnUrl) =>
                Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [AuthSetup.Oidc]))
                .AllowAnonymous();
        }

        app.MapPost("/auth/logout", (ClaimsPrincipal user) =>
        {
            var props = new AuthenticationProperties { RedirectUri = "/login?signedOut=1" };
            return user.FindFirstValue(AuthSetup.MethodClaim) == "oidc" && options.Oidc.SignOutOfProvider
                ? Results.SignOut(props, [AuthSetup.Cookie, AuthSetup.Oidc])
                : Results.SignOut(props, [AuthSetup.Cookie]);
        }).AllowAnonymous();

        app.MapGet("/api/me", (ClaimsPrincipal user) => new
        {
            name = user.Identity?.Name,
            authMethod = user.FindFirstValue(AuthSetup.MethodClaim),
            manageUsers = options.Local.Enabled,
        });

        if (!options.Local.Enabled)
            return;

        app.MapPost("/api/auth/login", async (LoginRequest req, AppDb db, IPasswordHasher<User> hasher, HttpContext ctx) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Username == req.Username);
            var result = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, req.Password ?? "");
            if (user is null || result == PasswordVerificationResult.Failed)
                return Results.Unauthorized();

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, req.Password!);
                await db.SaveChangesAsync();
            }
            await ctx.SignInAsync(AuthSetup.Cookie, AuthSetup.LocalPrincipal(user), new AuthenticationProperties { IsPersistent = true });
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("login");

        var users = app.MapGroup("/api/users");

        users.MapGet("/", (AppDb db) =>
            db.Users.AsNoTracking().OrderBy(u => u.Username).Select(u => new { u.Id, u.Username, u.CreatedAt }).ToListAsync());

        users.MapPost("/", async (CreateUserRequest req, AppDb db, IPasswordHasher<User> hasher, TimeProvider time) =>
        {
            var username = req.Username?.Trim() ?? "";
            if (username.Length is 0 or > 64)
                return Problem("username", "Must be 1-64 characters.");
            if (PasswordError(req.Password) is { } pwError)
                return Problem("password", pwError);
            if (await db.Users.AnyAsync(u => u.Username == username))
                return Problem("username", "Already exists.");

            var user = new User { Username = username, PasswordHash = "", CreatedAt = time.GetUtcNow().UtcDateTime };
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.CreatedAt });
        });

        users.MapPut("/{id:int}/password", async (int id, PasswordRequest req, AppDb db, IPasswordHasher<User> hasher,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (PasswordError(req.Password) is { } pwError)
                return Problem("password", pwError);
            var user = await db.Users.FindAsync(id);
            if (user is null)
                return Results.NotFound();

            user.PasswordHash = hasher.HashPassword(user, req.Password);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
            // Keep the current session alive when changing your own password.
            if (IsSelf(me, id))
                await ctx.SignInAsync(AuthSetup.Cookie, AuthSetup.LocalPrincipal(user), new AuthenticationProperties { IsPersistent = true });
            return Results.NoContent();
        });

        users.MapDelete("/{id:int}", async (int id, AppDb db, ClaimsPrincipal me) =>
        {
            if (IsSelf(me, id))
                return Problem("id", "You can't delete yourself.");
            if (!options.Oidc.Enabled && await db.Users.CountAsync() <= 1)
                return Problem("id", "Can't delete the last user.");
            return await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    public static async Task SeedInitialAdminAsync(AppDb db, AuthOptions options, IPasswordHasher<User> hasher, TimeProvider time, ILogger log)
    {
        if (!options.Local.Enabled || await db.Users.AnyAsync())
            return;

        var (username, password) = (options.Local.InitialAdminUsername, options.Local.InitialAdminPassword);
        if (string.IsNullOrWhiteSpace(username) || PasswordError(password) is not null)
        {
            if (!options.Oidc.Enabled)
                throw new InvalidOperationException(
                    "No users exist. Set Auth__Local__InitialAdminUsername and Auth__Local__InitialAdminPassword (8+ chars).");
            log.LogWarning("Local login enabled but no users exist; sign in with OIDC to add some.");
            return;
        }

        var user = new User { Username = username.Trim(), PasswordHash = "", CreatedAt = time.GetUtcNow().UtcDateTime };
        user.PasswordHash = hasher.HashPassword(user, password!);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        log.LogInformation("Created initial user {Username}", user.Username);
    }

    private static string? PasswordError(string? password) =>
        password is null || password.Length < 8 ? "Must be at least 8 characters." : null;

    private static bool IsSelf(ClaimsPrincipal me, int id) =>
        me.FindFirstValue(AuthSetup.MethodClaim) == "local" && me.FindFirstValue(ClaimTypes.NameIdentifier) == id.ToString();

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static string SafeReturnUrl(string? url) =>
        url is ['/', ..] && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}
