using System.Text.Json.Serialization;

namespace CelesteNet.Master.Shared;

public class ServerPostResponse {
    public Guid Uuid { get; set; }
    public string? Key { get; set; }

    [JsonConstructor]
    public ServerPostResponse() { }

    public ServerPostResponse(ServerData data, bool includeKey = false) {
        Uuid = data.Uuid;
        if (includeKey)
            Key = data.Key;
    }
}
