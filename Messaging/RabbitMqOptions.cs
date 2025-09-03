namespace FileWatcherApp.Messaging;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "192.168.10.13";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string Queue { get; set; } = "filewatcher.rpc.ping";
    public ushort Prefetch { get; set; } = 1;
    public bool AutomaticRecoveryEnabled { get; set; } = true;
    public bool TopologyRecoveryEnabled { get; set; } = true;
    public int RequestedHeartbeatSeconds { get; set; } = 30;
}

