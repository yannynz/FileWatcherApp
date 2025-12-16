namespace FileWatcherApp.Messaging;

public class FileCommandDto
{
    public string Action { get; set; } = string.Empty;
    public string Nr { get; set; } = string.Empty;
    public string NewPriority { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
}
