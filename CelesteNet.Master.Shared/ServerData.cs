using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace CelesteNet.Master.Shared;

public class ServerData {
    public static string CreateSecretKey() {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    /// The server's UUID.
    public Guid Uuid { get; set; }

    /// The secret key used to identify requests to do with the server.
    public string Key { get; set; } = null!;

    /// The IP address of the server.
    public IPAddress Host = null!;

    /// The port of the server.
    public int Port { get; set; }

    /// The name of the server.
    public string Name { get; set; } = null!;

    /// The last heartbeat activity.
    public DateTime Activity { get; set; }

    /// The player count.
    public int PlayerCount { get; set; }

    public ServerData(IPAddress addr, ServerPostRequest data) {
        Uuid = Guid.NewGuid();
        Key = CreateSecretKey();
        Host = addr;
        Port = data.Port;
        Name = data.Name;
        Activity = DateTime.UtcNow;
        PlayerCount = data.PlayerCount;
    }

    [JsonConstructor]
    public ServerData() { }
}
