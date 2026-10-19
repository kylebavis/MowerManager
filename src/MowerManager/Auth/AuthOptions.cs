namespace MowerManager.Auth;

public sealed class AuthOptions
{
    public LocalAuthOptions Local { get; set; } = new();
    public OidcAuthOptions Oidc { get; set; } = new();
}

public sealed class LocalAuthOptions
{
    public bool Enabled { get; set; } = true;
    // Created on startup when no users exist.
    public string? InitialAdminUsername { get; set; }
    public string? InitialAdminPassword { get; set; }
}

public sealed class OidcAuthOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
    public string NameClaim { get; set; } = "preferred_username";
    public string DisplayName { get; set; } = "Single sign-on";
    // Skip the login page and go straight to the provider.
    public bool AutoRedirect { get; set; }
    // Empty = anyone the provider authenticates.
    public string[] AllowedEmails { get; set; } = [];
    public bool SignOutOfProvider { get; set; } = true;
    public bool RequireHttpsMetadata { get; set; } = true;
}
