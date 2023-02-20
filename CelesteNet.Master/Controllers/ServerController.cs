using CelesteNet.Master.Shared;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace CelesteNet.Master.Controllers;

public static class ServerList {
    public static Dictionary<Guid, ServerData> Servers = new();

    public static void Cleanup() {
        foreach (KeyValuePair<Guid, ServerData> entry in Servers) {
            if (DateTime.UtcNow > entry.Value.Activity + TimeSpan.FromMilliseconds(60 * 1000 * 1.5)) {
                Servers.Remove(entry.Key);
            }
        }
    }
}

[ApiController]
[Route("api/servers")]
public class ServerController : ControllerBase {
    private readonly ILogger _logger;

    public ServerController(ILogger<ServerController> logger) {
        _logger = logger;
    }

    [HttpGet]
    public ActionResult GetServers() {
        return Ok(ServerList.Servers.Select(entry => new {
            Name = entry.Value.Name,
            Host = entry.Value.Host.ToString(),
            Port = entry.Value.Port,
            PlayerCount = entry.Value.PlayerCount,
        }));
    }

    [HttpPost]
    public ActionResult PostServer([FromBody] ServerPostRequest data) {
        ServerData server;

        if (data.Uuid is Guid uuid && ServerList.Servers.ContainsKey(uuid)) {
            // the server is registered
            server = ServerList.Servers[uuid];
            if (data.Key != server.Key)
                return StatusCode(403);

            server.PlayerCount = data.PlayerCount;
            server.Activity = DateTime.UtcNow;

            _logger.LogInformation("Heartbeat from existing server {Name} ({Uuid})", server.Name, server.Uuid);
            return Ok(new ServerPostResponse(server));
        }

        // server is not registered, set it up and return info
        server = new ServerData(HttpContext.Connection.RemoteIpAddress!, data);
        ServerList.Servers.Add(server.Uuid, server);

        _logger.LogInformation("Registered new server {Name} ({Uuid})", server.Name, server.Uuid);
        return Ok(new ServerPostResponse(server, includeKey: true));
    }
}
