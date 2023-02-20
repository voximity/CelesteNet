using CelesteNet.Master.Shared;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.Server {
    public class MasterServer : IDisposable {
        public readonly CelesteNetServerSettings Settings;

        private HttpClient client;
        private Guid? uuid;
        private string? key;

        public MasterServer(CelesteNetServerSettings settings) {
            Settings = settings;
            client = new();
        }

        public async Task PostAsync() {
            try {
                var response = await client.PostAsJsonAsync(
                    Settings.MasterServer + "/api/servers",
                    new ServerPostRequest {
                        Uuid = uuid,
                        Key = key,
                        Port = Settings.MainPort,
                        Name = Settings.ServerName,
                        PlayerCount = Program.Server?.Sessions.Count ?? 0
                    }
                );

                var server = await response.Content.ReadFromJsonAsync<ServerData>();
                if (server is null) {
                    Logger.Log(LogLevel.WRN, "master", "Bad response from master server");
                    return;
                }

                if (server.Key is not null)
                    key = server.Key;

                if (uuid != server.Uuid)
                    Logger.Log(LogLevel.INF, "master", $"Master server delegated new UUID {server.Uuid}");
                uuid = server.Uuid;
            } catch (Exception e) {
                Logger.Log(LogLevel.ERR, "master", "Failed to contact master server");
            }
        }

        public void Dispose() {
            client.Dispose();
        }
    }
}
