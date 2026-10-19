using System.ComponentModel.DataAnnotations;

namespace MowerManager.Husqvarna;

public sealed class HusqvarnaOptions
{
    [Required] public string AppKey { get; set; } = "";
    [Required] public string AppSecret { get; set; } = "";
    public string AuthUrl { get; set; } = "https://api.authentication.husqvarnagroup.dev/v1/oauth2/token";
    public string ApiUrl { get; set; } = "https://api.amc.husqvarna.dev/v1/";
    public string WebSocketUrl { get; set; } = "wss://ws.openapi.husqvarna.dev/v1";
    public bool StreamEnabled { get; set; } = true;
}
