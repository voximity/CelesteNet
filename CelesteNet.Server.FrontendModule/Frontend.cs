﻿#if NETCORE
using Microsoft.AspNetCore.StaticFiles;
#endif
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using WebSocketSharp.Server;
using Celeste.Mod.Helpers;
using Celeste.Mod.CelesteNet.DataTypes;
using Celeste.Mod.CelesteNet.Server.Chat;
using System.Timers;

namespace Celeste.Mod.CelesteNet.Server.Control {
    public class Frontend : CelesteNetServerModule<FrontendSettings> {

        public static readonly string COOKIE_SESSION = "celestenet-session";
        public static readonly string COOKIE_DISCORDAUTH = "celestenet-discordauth";
        public static readonly string COOKIE_KEY = "celestenet-key";

        public readonly List<RCEndpoint> EndPoints = new();
        public readonly HashSet<string> CurrentSessionKeys = new();
        public readonly HashSet<string> CurrentSessionExecKeys = new();

        private HttpServer? HTTPServer;
        private WebSocketServiceHost? WSHost;

        private Timer? StatsTimer;
        private readonly Dictionary<string, long> WSUpdateCooldowns = new();
        public static readonly long WSUpdateCooldownTime = 1000;

#if NETCORE
        private readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
#endif

        public JsonSerializer Serializer = new() {
            Formatting = Formatting.Indented
        };

        public override void Init(CelesteNetServerModuleWrapper wrapper) {
            base.Init(wrapper);

            Logger.Log(LogLevel.VVV, "frontend", "Scanning for endpoints");
            foreach (Type t in CelesteNetUtils.GetTypes()) {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    foreach (RCEndpointAttribute epa in m.GetCustomAttributes(typeof(RCEndpointAttribute), false)) {
                        RCEndpoint ep = epa.Data;
                        Logger.Log(LogLevel.VVV, "frontend", $"Found endpoint: {ep.Path} - {ep.Name} ({m.Name}::{t.FullName})");
                        ep.Handle = (f, c) => m.Invoke(null, new object[] { f, c });
                        EndPoints.Add(ep);
                    }
                }
            }

            Server.OnConnect += OnConnect;
            Server.OnSessionStart += OnSessionStart;
            using (Server.ConLock.R())
                foreach (CelesteNetPlayerSession session in Server.Sessions)
                    session.OnEnd += OnSessionEnd;
            Server.OnDisconnect += OnDisconnect;

            Server.Channels.OnBroadcastList += OnBroadcastChannels;
            Server.Channels.OnCreate += OnCreateChannel;
            Server.Channels.OnRemove += OnRemoveChannel;
            Server.Channels.OnMove += OnChannelMove;

            ChatModule chat = Server.Get<ChatModule>();
            chat.OnReceive += OnChatReceive;
            chat.OnForceSend += OnForceSend;
        }

        public override void Start() {
            base.Start();

            Logger.Log(LogLevel.INF, "frontend", $"Startup on port {Settings.Port}");

            HTTPServer = new(Settings.Port);

            HTTPServer.OnGet += HandleRequestRaw;
            HTTPServer.OnPost += HandleRequestRaw;

            HTTPServer.WebSocketServices.AddService<FrontendWebSocket>($"{Settings.APIPrefix}/ws", ws => ws.Frontend = this);

            HTTPServer.Start();

            HTTPServer.WebSocketServices.TryGetServiceHost($"{Settings.APIPrefix}/ws", out WSHost);

            StatsTimer = new Timer(Settings.NetPlusStatsUpdateRate);
            StatsTimer.AutoReset = true;
            StatsTimer.Elapsed += (_, _) => RCEndpoints.UpdateStats(Server);
            StatsTimer.Enabled = true;
        }

        public override void Dispose() {
            base.Dispose();

            Logger.Log(LogLevel.INF, "frontend", "Shutdown");

            StatsTimer?.Dispose();

            try {
                HTTPServer?.Stop();
            } catch {
            }
            HTTPServer = null;

            Server.OnConnect -= OnConnect;
            Server.OnSessionStart -= OnSessionStart;
            using (Server.ConLock.R())
                foreach (CelesteNetPlayerSession session in Server.Sessions)
                    session.OnEnd -= OnSessionEnd;
            Server.OnDisconnect -= OnDisconnect;

            Server.Channels.OnBroadcastList -= OnBroadcastChannels;
            Server.Channels.OnCreate -= OnCreateChannel;
            Server.Channels.OnRemove -= OnRemoveChannel;
            Server.Channels.OnMove -= OnChannelMove;

            if (Server.TryGet(out ChatModule? chat)) {
                chat.OnReceive -= OnChatReceive;
                chat.OnForceSend -= OnForceSend;
            }
        }

        private void OnConnect(CelesteNetServer server, CelesteNetConnection con) {
            TryBroadcastUpdate(Settings.APIPrefix + "/status");
        }

        private void OnSessionStart(CelesteNetPlayerSession session) {
            BroadcastCMD(false, "sess_join", PlayerSessionToFrontend(session, shorten: true));
            TryBroadcastUpdate(Settings.APIPrefix + "/status");
            //TryBroadcastUpdate(Settings.APIPrefix + "/players");
            session.OnEnd += OnSessionEnd;
        }

        private void OnSessionEnd(CelesteNetPlayerSession session, DataPlayerInfo? lastPlayerInfo) {
            BroadcastCMD(false, "sess_leave", PlayerSessionToFrontend(session, shorten: true));
            TryBroadcastUpdate(Settings.APIPrefix + "/status");
            //TryBroadcastUpdate(Settings.APIPrefix + "/players");
        }

        private void OnDisconnect(CelesteNetServer server, CelesteNetConnection con, CelesteNetPlayerSession? session) {
            if (session == null)
                TryBroadcastUpdate(Settings.APIPrefix + "/status");
        }

        private void OnBroadcastChannels(Channels obj) {
            TryBroadcastUpdate(Settings.APIPrefix + "/channels");
        }

        private void OnChannelMove(CelesteNetPlayerSession session, Channel? from, Channel? to) {
            BroadcastCMD(false, "chan_move", new { session.SessionID, session.UID, fromID = from?.ID, toID = to?.ID });
        }

        private void OnCreateChannel(Channel channel, int total) {
            BroadcastCMD(channel.IsPrivate, "chan_create", new { Channel = new { channel.ID, channel.Name, channel.IsPrivate, Players = channel.Players.Select(p => p.SessionID) }, Count = total });
        }

        private void OnRemoveChannel(string name, uint id, int total) {
            BroadcastCMD(false, "chan_remove", new { Name = name, ID = id, Count = total });
        }

        private void OnChatReceive(ChatModule chat, DataChat msg) {
            BroadcastCMD(msg.Targets != null, "chat", msg.ToDetailedFrontendChat());
        }

        private void OnForceSend(ChatModule chat, DataChat msg) {
            BroadcastCMD(msg.Targets != null, "chat", msg.ToDetailedFrontendChat());
        }

        public object PlayerSessionToFrontend(CelesteNetPlayerSession p, bool auth = false, bool shorten = false) {
            // This sucks c:
            return shorten ? new {
                ID = p.SessionID,
                UID = auth ? p.UID : null,
                p.PlayerInfo?.Name,
                p.PlayerInfo?.FullName,
                p.PlayerInfo?.DisplayName,
                Avatar = Server.UserData.HasFile(p.UID, "avatar.png") ? $"{Settings.APIPrefix}/avatar?uid={p.UID}" : null,

                Connection = auth ? p.Con.ID : null,
                ConnectionUID = auth ? p.Con.UID : null
            }
            : new {
                ID = p.SessionID,
                UID = auth ? p.UID : null,
                p.PlayerInfo?.Name,
                p.PlayerInfo?.FullName,
                p.PlayerInfo?.DisplayName,
                Avatar = Server.UserData.HasFile(p.UID, "avatar.png") ? $"{Settings.APIPrefix}/avatar?uid={p.UID}" : null,

                Connection = auth ? p.Con.ID : null,
                ConnectionUID = auth ? p.Con.UID : null,

                TCPPingMs = auth ? (p.Con as ConPlusTCPUDPConnection)?.TCPPingMs : null,
                UDPPingMs = auth ? (p.Con as ConPlusTCPUDPConnection)?.UDPPingMs : null,

                TCPDownlinkBpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.TCPRecvRate.ByteRate : null,
                TCPDownlinkPpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.TCPRecvRate.PacketRate : null,
                TCPUplinkBpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.TCPSendRate.ByteRate : null,
                TCPUplinkPpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.TCPSendRate.PacketRate : null,
                UDPDownlinkBpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.UDPRecvRate.ByteRate : null,
                UDPDownlinkPpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.UDPRecvRate.PacketRate : null,
                UDPUplinkBpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.UDPSendRate.ByteRate : null,
                UDPUplinkPpS = auth ? (p.Con as ConPlusTCPUDPConnection)?.UDPSendRate.PacketRate : null,
            };
        }

        private string? GetContentType(string path) {
#if NETCORE
            if (ContentTypeProvider.TryGetContentType(path, out string contentType))
                return contentType;
            return null;
#else
            return MimeMapping.GetMimeMapping(path);
#endif
        }

        public Stream? OpenContent(string path, out string pathNew, out DateTime? lastMod, out string? contentType) {
            pathNew = path;

            try {
                string dir = Path.GetFullPath(Settings.ContentRoot);
                string pathFS = Path.GetFullPath(Path.Combine(dir, path));
                if (pathFS.StartsWith(dir) && File.Exists(pathFS)) {
                    lastMod = File.GetLastWriteTimeUtc(pathFS);
                    contentType = GetContentType(pathFS);
                    return File.OpenRead(pathFS);
                }
            } catch {
            }

#if DEBUG
            try {
                string dir = Path.GetFullPath(Path.Combine("..", "..", "..", "Content"));
                string pathFS = Path.GetFullPath(Path.Combine(dir, path));
                if (pathFS.StartsWith(dir) && File.Exists(pathFS)) {
                    lastMod = File.GetLastWriteTimeUtc(pathFS);
                    contentType = GetContentType(pathFS);
                    return File.OpenRead(pathFS);
                }
            } catch {
            }

            try {
                string dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "CelesteNet.Server.FrontendModule", "Content"));
                string pathFS = Path.GetFullPath(Path.Combine(dir, path));
                if (pathFS.StartsWith(dir) && File.Exists(pathFS)) {
                    lastMod = File.GetLastWriteTimeUtc(pathFS);
                    contentType = GetContentType(pathFS);
                    return File.OpenRead(pathFS);
                }
            } catch {
            }
#endif

            if (!path.EndsWith("/index.html")) {
                path = path.EndsWith("/") ? path : (path + "/");
                Stream? index = OpenContent(path + "index.html", out _, out lastMod, out contentType);
                if (index != null) {
                    pathNew = path;
                    return index;
                }
            }

            lastMod = null;
            contentType = GetContentType(path);
            return typeof(CelesteNetServer).Assembly.GetManifestResourceStream("Celeste.Mod.CelesteNet.Server.Content." + path.Replace("/", "."));
        }

        private void HandleRequestRaw(object? sender, HttpRequestEventArgs c) {
            try {
                using (c.Request.InputStream)
                using (c.Response) {
                    HandleRequest(c);
                }

            } catch (Exception e) {
                Logger.Log(LogLevel.ERR, "frontend", $"Frontend failed responding: {e}");
            }
        }

        private void HandleRequest(HttpRequestEventArgs c) {
            Logger.Log(LogLevel.VVV, "frontend", $"{c.Request.RemoteEndPoint} requested: {c.Request.RawUrl}");

            string urlRaw = c.Request.RawUrl;
            string url = urlRaw;
            int indexOfSplit = url.IndexOf('?');
            if (indexOfSplit != -1)
                url = url.Substring(0, indexOfSplit);

            if (!url.ToLowerInvariant().StartsWith(Settings.APIPrefix)) {
                RespondContent(c, "frontend/" + url.Substring(1));
                return;
            }

            string urlApiRaw = urlRaw.Substring(Settings.APIPrefix.Length);
            string urlApi = url.Substring(Settings.APIPrefix.Length);

            RCEndpoint? endpoint =
                EndPoints.FirstOrDefault(ep => ep.Path == urlApiRaw) ??
                EndPoints.FirstOrDefault(ep => ep.Path == urlApi) ??
                EndPoints.FirstOrDefault(ep => ep.Path.ToLowerInvariant() == urlApiRaw.ToLowerInvariant()) ??
                EndPoints.FirstOrDefault(ep => ep.Path.ToLowerInvariant() == urlApi.ToLowerInvariant());

            if (endpoint == null) {
                RespondContent(c, "frontend/" + url.Substring(1));
                return;
            }

            c.Response.Headers.Set("Cache-Control", "no-store, max-age=0, s-maxage=0, no-cache, no-transform");

            if (endpoint.Auth) {
                if (TryGetSessionAuthCookie(c) is string session && IsAuthorized(c)) {
                    // refresh this with a new expiry date
                    SetSessionAuthCookie(c, session);
                } else {
                    c.Response.StatusCode = (int) HttpStatusCode.Unauthorized;
                    RespondJSON(c, new {
                        Error = "Unauthorized."
                    });
                    return;
                }
            }

            endpoint.Handle(this, c);
        }

        public void SetCookie(HttpRequestEventArgs c, string name, string value, string path = "/", DateTime? expires = null) {
            c.Response.SetCookie(new(name, value, path) {
                Expires = expires ?? DateTime.MinValue,
                HttpOnly= true
            });
        }

        public void SetSessionAuthCookie(HttpRequestEventArgs c, string value, DateTime? expires = null) =>
            SetCookie(c, COOKIE_SESSION, value, "/", expires ?? DateTime.Now.AddDays(30));

        public void UnsetSessionAuthCookie(HttpRequestEventArgs c) => SetSessionAuthCookie(c, "");

        public void SetDiscordAuthCookie(HttpRequestEventArgs c, string value, DateTime? expires = null) =>
            SetCookie(c, COOKIE_DISCORDAUTH, value, "/", expires ?? DateTime.Now.AddMonths(6));

        public void UnsetDiscordAuthCookie(HttpRequestEventArgs c) => SetDiscordAuthCookie(c, "");

        public void SetKeyCookie(HttpRequestEventArgs c, string key, DateTime? expires = null) =>
            SetCookie(c, COOKIE_KEY, key, "/", expires ?? DateTime.Now.AddDays(90));

        public void UnsetKeyCookie(HttpRequestEventArgs c) => SetKeyCookie(c, "");

        public void UnsetAllCookies(HttpRequestEventArgs c) {
            UnsetDiscordAuthCookie(c);
            UnsetSessionAuthCookie(c);
            UnsetKeyCookie(c);
        }

        public string? TryGetCookie(HttpRequestEventArgs c, string name) => c.Request.Cookies[name]?.Value;
        public string? TryGetSessionAuthCookie(HttpRequestEventArgs c) => c.Request.Cookies[COOKIE_SESSION]?.Value;
        public string? TryGetDiscordAuthCookie(HttpRequestEventArgs c) => c.Request.Cookies[COOKIE_DISCORDAUTH]?.Value;
        public string? TryGetKeyCookie(HttpRequestEventArgs c) => c.Request.Cookies[COOKIE_KEY]?.Value;

        public bool IsAuthorized(HttpRequestEventArgs c)
            => TryGetSessionAuthCookie(c) is string session && CurrentSessionKeys.Contains(session);

        public bool IsAuthorizedExec(HttpRequestEventArgs c)
            => TryGetSessionAuthCookie(c) is string session && CurrentSessionExecKeys.Contains(session);

        public string GetNewKey(bool execAuth = false) {
            string key;
            do {
                key = Guid.NewGuid().ToString();
            } while (!CurrentSessionKeys.Add(key) || (execAuth && !CurrentSessionExecKeys.Add(key)));
            return key;
        }

        public void BroadcastRawString(bool authOnly, string data) {
            if (WSHost == null)
                return;

            foreach (FrontendWebSocket session in WSHost.Sessions.Sessions.ToArray())
                if (!authOnly || session.IsAuthorized)
                    try {
                        session.SendRawString(data);
                    } catch (Exception e) {
                        Logger.Log(LogLevel.VVV, "frontend", $"Failed broadcast:\n{session.CurrentEndPoint}\n{e}");
                    }
        }

        public void BroadcastRawObject(bool authOnly, object obj) {
            if (WSHost == null)
                return;

            using MemoryStream ms = new();
            using (StreamWriter sw = new(ms, CelesteNetUtils.UTF8NoBOM, 1024, true))
            using (JsonTextWriter jtw = new(sw))
                Serializer.Serialize(jtw, obj);

            ms.Seek(0, SeekOrigin.Begin);

            using StreamReader sr = new(ms, Encoding.UTF8, false, 1024, true);
            BroadcastRawString(authOnly, sr.ReadToEnd());
        }

        public void TryBroadcastUpdate(string path, bool authOnly = false) {
            WSUpdateCooldowns.TryGetValue(path, out long cd);
            if (cd > DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) {
                Logger.Log(LogLevel.VVV, "frontend", $"Not sending 'cmd update {path}' wscmd because of WSUpdateCooldown");
                return;
            }
            WSUpdateCooldowns[path] = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond + WSUpdateCooldownTime;
            
            BroadcastCMD(authOnly, "update", path);
        }

        public void BroadcastCMD(bool authOnly, string id, object obj) {
            BroadcastRawString(authOnly, "cmd");
            BroadcastRawString(authOnly, id);
            BroadcastRawObject(authOnly, obj);
        }

        #region Read / Parse Helpers

        public NameValueCollection ParseQueryString(string url) {
            NameValueCollection nvc = new();

            int indexOfSplit = url.IndexOf('?');
            if (indexOfSplit == -1)
                return nvc;
            url = url.Substring(indexOfSplit + 1);

            string[] args = url.Split('&');
            foreach (string arg in args) {
                indexOfSplit = arg.IndexOf('=');
                if (indexOfSplit == -1)
                    continue;
                nvc[arg.Substring(0, indexOfSplit)] = arg.Substring(indexOfSplit + 1);
            }

            return nvc;
        }

        #endregion

        #region Write Helpers

        public void RespondContent(HttpRequestEventArgs c, string id) {
            using Stream? s = OpenContent(id, out string pathNew, out DateTime? lastMod, out string? contentType);
            if (s == null) {
                c.Response.StatusCode = (int) HttpStatusCode.NotFound;
                RespondJSON(c, new {
                    Error = "Resource not found."
                });
                return;
            }

            if (id != pathNew && pathNew.StartsWith("frontend/")) {
                // c.Response.Redirect($"http://{c.Request.UserHostName}/{pathNew.Substring(9)}");
                c.Response.StatusCode = (int) HttpStatusCode.Moved;
                c.Response.Headers.Set("Location", $"http://{c.Request.UserHostName}/{pathNew.Substring(9)}");
                Respond(c, $"Redirecting to /{pathNew.Substring(9)}");
                return;
            }

            if (lastMod != null)
                c.Response.Headers.Set("Last-Modified", lastMod.Value.ToString("r"));

            if (contentType != null)
                c.Response.ContentType = contentType;

            Respond(c, s.ToBytes());
        }

        public void RespondContent(HttpRequestEventArgs c, Stream s) {
            Respond(c, s.ToBytes());
        }

        public void RespondJSON(HttpRequestEventArgs c, object obj) {
            using MemoryStream ms = new();
            using (StreamWriter sw = new(ms, CelesteNetUtils.UTF8NoBOM, 1024, true))
            using (JsonTextWriter jtw = new(sw))
                Serializer.Serialize(jtw, obj);

            ms.Seek(0, SeekOrigin.Begin);

            c.Response.ContentType = "application/json";
            Respond(c, ms.ToArray());
        }

        public void Respond(HttpRequestEventArgs c, string str) {
            Respond(c, CelesteNetUtils.UTF8NoBOM.GetBytes(str));
        }

        public void Respond(HttpRequestEventArgs c, byte[] buf) {
            c.Response.ContentLength64 = buf.Length;
            c.Response.Close(buf, true);
        }

        #endregion

    }
}
