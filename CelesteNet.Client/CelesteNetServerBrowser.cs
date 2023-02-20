using Celeste.Mod.UI;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Net;

namespace Celeste.Mod.CelesteNet.Client {
    class ServerObject {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Name { get; set; }
    }

    class OuiServerBrowser : OuiGenericMenu {
        public override string MenuName => "Server Browser";

        protected override void addOptionsToMenu(TextMenu menu) {
            WebClient client = new WebClient();
            var response = client.DownloadString(CelesteNetClientModule.Settings.MasterServer + "/api/servers");
            var servers = JsonConvert.DeserializeObject<List<ServerObject>>(response);

            if (servers.Count == 0) {
                menu.Add(new TextMenu.Header("No servers found :("));
                return;
            }

            foreach (var server in servers) {
                menu.Add(new TextMenu.Button(server.Name).Pressed(() => {
                    Audio.Play("event:/ui/main/savefile_rename_start");

                    CelesteNetClientModule.Settings.Server = $"{server.Host}:{server.Port}";
                    // this should also properly (disconnect and re)connect the user

                    backToParentMenu(Overworld);
                }));
            }
        }
    }
}
