using System.Net;
using Microsoft.Extensions.Configuration;

namespace FileWatcherApp.Util;

public static class SystemInfo
{
    public static string GetHostName() => Dns.GetHostName();

    public static string GetVersion(IConfiguration cfg)
        => cfg["FileWatcher:Version"] ?? "unknown";
}

