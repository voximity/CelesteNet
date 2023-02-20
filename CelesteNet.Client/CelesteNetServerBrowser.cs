using Celeste.Mod.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;

namespace Celeste.Mod.CelesteNet.Client {
    public class ServerObject {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Name { get; set; }
        public int PlayerCount { get; set; }
    }

    class OuiServerBrowser : OuiGenericMenu {
        public override string MenuName => "Server Browser";

        protected override void addOptionsToMenu(TextMenu menu) {
            void addLabel(string message) {
                var label = new TextMenu.Button(message);
                label.Disabled = true;
                menu.Add(label);
            }

            void load() {
                menu.Clear();
                menu.Add(new TextMenu.Header("Server Browser"));

                try {
                    using WebClient client = new WebClient();
                    var response = client.DownloadString(CelesteNetClientModule.Settings.MasterServer + "/api/servers");
                    var servers = JsonConvert.DeserializeObject<List<ServerObject>>(response);

                    if (servers.Count == 0) {
                        addLabel("No servers found :(");
                        return;
                    }

                    foreach (var server in servers) {
                        var entry = new TextMenu.Button($"{server.Name} ({server.PlayerCount} online)");
                        entry.Pressed(() => {
                            Audio.Play("event:/ui/main/savefile_rename_start");

                            CelesteNetClientModule.Settings.Connected = false;
                            CelesteNetClientModule.Settings.ServerObject = server;
                            CelesteNetClientModule.Settings.Connected = true;

                            backToParentMenu(Overworld);
                        });

                        menu.Add(entry);
                    }
                } catch (Exception e) {
                    addLabel("Failed to reach the master server :(");
                    Logger.Log(LogLevel.ERR, "master", $"Could not reach master server: {e}");
                }

                menu.Add(new TextMenu.Button("Refresh").Pressed(load));
            }

            load();
        }
    }
}
