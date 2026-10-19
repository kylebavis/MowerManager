using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MowerManager.Data;

namespace MowerManager.Auth;

public static class AuthSetup
{
    public const string Cookie = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string Oidc = OpenIdConnectDefaults.AuthenticationScheme;
    public const string MethodClaim = "auth_method";
    public const string StampClaim = "stamp";
    private const string Challenge = "challenge";

    public static void AddAppAuth(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new();
        if (!options.Local.Enabled && !options.Oidc.Enabled)
            throw new InvalidOperationException("Enable Auth:Local and/or Auth:Oidc.");

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        var auth = services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = Cookie;
                o.DefaultChallengeScheme = Challenge;
            })
            .AddCookie(Cookie, o =>
            {
                o.Cookie.Name = "mower.auth";
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.LoginPath = "/login";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx => RedirectOrStatus(ctx, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = ctx => RedirectOrStatus(ctx, StatusCodes.Status403Forbidden);
                o.Events.OnValidatePrincipal = ValidateLocalUserAsync;
            })
            // Browsers go straight to the provider when auto-redirect is on; API calls always get a 401.
            .AddPolicyScheme(Challenge, null, o => o.ForwardDefaultSelector = ctx =>
                options.Oidc is { Enabled: true, AutoRedirect: true } && !IsApi(ctx.Request) ? Oidc : Cookie);

        if (options.Oidc.Enabled)
            auth.AddOpenIdConnect(Oidc, o => ConfigureOidc(o, options.Oidc));

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    private static void ConfigureOidc(OpenIdConnectOptions o, OidcAuthOptions cfg)
    {
        o.Authority = cfg.Authority;
        o.ClientId = cfg.ClientId;
        o.ClientSecret = cfg.ClientSecret;
        o.ResponseType = "code";
        // Query response mode lets correlation cookies stay SameSite=Lax (works over plain http too).
        o.ResponseMode = "query";
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.RequireHttpsMetadata = cfg.RequireHttpsMetadata;
        o.MapInboundClaims = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        // id_token is needed as a hint for provider sign-out.
        o.SaveTokens = cfg.SignOutOfProvider;
        o.Scope.Clear();
        foreach (var scope in cfg.Scopes)
            o.Scope.Add(scope);
        o.TokenValidationParameters.NameClaimType = cfg.NameClaim;

        o.Events.OnTokenValidated = ctx =>
        {
            ((ClaimsIdentity)ctx.Principal!.Identity!).AddClaim(new Claim(MethodClaim, "oidc"));
            return Task.CompletedTask;
        };
        o.Events.OnUserInformationReceived = ctx =>
        {
            if (cfg.AllowedEmails.Length == 0)
                return Task.CompletedTask;
            var email = ctx.Principal?.FindFirstValue("email")
                ?? (ctx.User.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null);
            if (email is null || !cfg.AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
                ctx.Fail($"{email ?? "This account"} is not allowed.");
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/login?error=" + Uri.EscapeDataString(ctx.Failure?.Message ?? "Sign-in failed"));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    }

    private static bool IsApi(HttpRequest request) => request.Path.StartsWithSegments("/api");

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> ctx, int status)
    {
        if (IsApi(ctx.Request))
            ctx.Response.StatusCode = status;
        else
            ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }

    // Local sessions end when the user is deleted, changes password, or local login is disabled.
    private static async Task ValidateLocalUserAsync(CookieValidatePrincipalContext ctx)
    {
        if (ctx.Principal?.FindFirstValue(MethodClaim) != "local")
            return;

        var services = ctx.HttpContext.RequestServices;
        var valid = services.GetRequiredService<AuthOptions>().Local.Enabled
            && int.TryParse(ctx.Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && await services.GetRequiredService<AppDb>().Users
                .AnyAsync(u => u.Id == id && u.SecurityStamp == ctx.Principal.FindFirstValue(StampClaim));
        if (!valid)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(Cookie);
        }
    }

    public static ClaimsPrincipal LocalPrincipal(User user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(MethodClaim, "local"),
        new Claim(StampClaim, user.SecurityStamp),
    ], Cookie));
}
