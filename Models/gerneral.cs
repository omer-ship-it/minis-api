namespace Minis.Models
{
    public record PushRequest(
        string? Token,
        string? Title,
        string? Body,
        Dictionary<string, string>? Data
    );
}
