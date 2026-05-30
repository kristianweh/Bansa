using System;
using System.Collections.Generic;

namespace Bansa.Models;

/// <summary>
/// Snapshot of a process's network state at a moment in time.
/// </summary>
public class ProcessNetInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string ImagePath { get; init; } = "";

    /// <summary>Bytes per second, downloaded.</summary>
    public long BytesInPerSec { get; set; }

    /// <summary>Bytes per second, uploaded.</summary>
    public long BytesOutPerSec { get; set; }

    /// <summary>Total bytes downloaded since Bansa started monitoring.</summary>
    public long TotalBytesIn { get; set; }

    /// <summary>Total bytes uploaded since Bansa started monitoring.</summary>
    public long TotalBytesOut { get; set; }

    /// <summary>Current active TCP/UDP connections.</summary>
    public List<ConnectionInfo> Connections { get; set; } = new();

    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

public class ConnectionInfo
{
    public string Protocol { get; init; } = "TCP";
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public string State { get; init; } = "";
}
