namespace CelesteNet.Master.Shared;

public class ServerPostRequest {
    public Guid? Uuid { get; set; }
    public string? Key { get; set; }
    public int Port { get; set; }
    public string Name { get; set; } = null!;
}
