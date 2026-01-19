namespace FikaHeadlessManager.Models;

public record Settings
{
    public string? ProfileId { get; set; }
    public Uri? BackendUrl { get; set; }
    public bool StartMinimized { get; set; }
    public bool ExtraLogging { get; set; }
    public string? Title { get; set; }
}
