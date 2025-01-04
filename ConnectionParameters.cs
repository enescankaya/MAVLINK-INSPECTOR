namespace MavlinkInspector;

public class ConnectionParameters
{
    public string? ConnectionType { get; set; }
    public string? Port { get; set; }
    public int BaudRate { get; set; }
    public string? IpAddress { get; set; }
    public int NetworkPort { get; set; }
}
