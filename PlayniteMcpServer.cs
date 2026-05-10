using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PlayniteMcpServer
{
    public class PlayniteMcpServer : GenericPlugin
    {
        private const string Prefix = "http://127.0.0.1:8977/";
        private const string McpProtocolVersion = "2025-06-18";
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private HttpListener listener;
        private CancellationTokenSource cancellation;
        private Task serverTask;

        public override Guid Id { get; } = Guid.Parse("c19604f7-f5a1-4ee1-9f85-db1df598a1c5");

        public PlayniteMcpServer(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = false };
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            StartBridge();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            StopBridge();
        }

        public override void Dispose()
        {
            StopBridge();
            base.Dispose();
        }

        private void StartBridge()
        {
            if (listener != null)
            {
                return;
            }

            try
            {
                cancellation = new CancellationTokenSource();
                listener = new HttpListener();
                listener.Prefixes.Add(Prefix);
                listener.Start();
                serverTask = Task.Run(() => ServeAsync(cancellation.Token));
                logger.Info("Playnite MCP server listening on " + Prefix);
            }
            catch (Exception e)
            {
                listener = null;
                cancellation = null;
                logger.Error(e, "Failed to start Playnite MCP server.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "playnite-mcp-server-start-failed",
                    "Playnite MCP Server failed to start. Check Playnite logs.",
                    NotificationType.Error));
            }
        }

        private void StopBridge()
        {
            cancellation?.Cancel();
            if (listener != null)
            {
                listener.Close();
                listener = null;
            }
        }

        private async Task ServeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener != null)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }

                _ = Task.Run(() => HandleRequest(context));
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath.Trim('/').ToLowerInvariant();
                if (path == "health")
                {
                    WriteJson(context, new
                    {
                        ok = true,
                        service = "playnite-mcp-server",
                        mcpEndpoint = Prefix + "mcp",
                        protocolVersion = McpProtocolVersion
                    });
                }
                else if (path == "mcp")
                {
                    HandleMcpRequest(context);
                }
                else if (path == "games")
                {
                    var query = context.Request.QueryString["q"];
                    var games = GetGames(query);
                    WriteJson(context, new { games });
                }
                else
                {
                    WriteJson(context, new { error = "Unknown endpoint." }, 404);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Playnite MCP server request failed.");
                WriteJson(context, new { error = e.Message }, 500);
            }
        }

        private void HandleMcpRequest(HttpListenerContext context)
        {
            AddMcpHeaders(context);

            if (!IsAllowedOrigin(context.Request.Headers["Origin"]))
            {
                WriteJson(context, new { error = "Origin is not allowed." }, 403);
                return;
            }

            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod == "GET")
            {
                context.Response.StatusCode = 405;
                context.Response.Headers["Allow"] = "POST";
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod == "DELETE")
            {
                context.Response.StatusCode = 405;
                context.Response.Headers["Allow"] = "POST";
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                WriteJson(context, new { error = "Method not allowed." }, 405);
                return;
            }

            var accept = context.Request.Headers["Accept"] ?? string.Empty;
            if (accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0 ||
                accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) < 0)
            {
                WriteJson(context, new { error = "MCP requests must accept application/json and text/event-stream." }, 406);
                return;
            }

            Dictionary<string, object> message;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
            {
                var body = reader.ReadToEnd();
                message = json.Deserialize<Dictionary<string, object>>(body);
            }

            if (message == null)
            {
                WriteJson(context, CreateMcpError(null, -32700, "Parse error"), 400);
                return;
            }

            var id = message.ContainsKey("id") ? message["id"] : null;
            var method = GetString(message, "method");
            if (string.IsNullOrWhiteSpace(method))
            {
                WriteJson(context, CreateMcpError(id, -32600, "Invalid Request"), 400);
                return;
            }

            if (id == null && method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }

            object result;
            switch (method)
            {
                case "initialize":
                    result = GetInitializeResult();
                    break;
                case "tools/list":
                    result = new { tools = GetMcpTools() };
                    break;
                case "tools/call":
                    result = HandleMcpToolCall(GetDictionary(message, "params"));
                    break;
                default:
                    WriteJson(context, CreateMcpError(id, -32601, "Method not found: " + method));
                    return;
            }

            WriteJson(context, new
            {
                jsonrpc = "2.0",
                id,
                result
            });
        }

        private object GetInitializeResult()
        {
            return new
            {
                protocolVersion = McpProtocolVersion,
                capabilities = new
                {
                    tools = new
                    {
                        listChanged = false
                    }
                },
                serverInfo = new
                {
                    name = "playnite-mcp-server",
                    title = "Playnite MCP Server",
                    version = "0.1.0"
                },
                instructions = "Use the Playnite tools to list, search, and launch games from the local Playnite library."
            };
        }

        private object[] GetMcpTools()
        {
            return new object[]
            {
                new
                {
                    name = "playnite_list_games",
                    title = "List Playnite Games",
                    description = "List up to 100 games from the local Playnite library.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>(),
                        additionalProperties = false
                    }
                },
                new
                {
                    name = "playnite_search_games",
                    title = "Search Playnite Games",
                    description = "Search local Playnite games by name.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            {
                                "query",
                                new
                                {
                                    type = "string",
                                    description = "Part of the game name."
                                }
                            }
                        },
                        required = new[] { "query" },
                        additionalProperties = false
                    }
                },
                new
                {
                    name = "playnite_launch_game",
                    title = "Launch Playnite Game",
                    description = "Launch a local Playnite game by its database GUID.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            {
                                "id",
                                new
                                {
                                    type = "string",
                                    description = "Playnite game database GUID."
                                }
                            }
                        },
                        required = new[] { "id" },
                        additionalProperties = false
                    }
                }
            };
        }

        private object HandleMcpToolCall(Dictionary<string, object> parameters)
        {
            var name = GetString(parameters, "name");
            var arguments = GetDictionary(parameters, "arguments") ?? new Dictionary<string, object>();

            if (name == "playnite_list_games")
            {
                var payload = new { games = GetGames(null) };
                return CreateToolResult(payload, false);
            }

            if (name == "playnite_search_games")
            {
                var query = GetString(arguments, "query");
                var payload = new { games = GetGames(query) };
                return CreateToolResult(payload, false);
            }

            if (name == "playnite_launch_game")
            {
                var idText = GetString(arguments, "id");
                if (!Guid.TryParse(idText, out var id))
                {
                    return CreateToolResult(new { error = "A valid game id is required." }, true);
                }

                var result = PlayniteApi.MainView.UIDispatcher.Invoke(() => LaunchGameOnUiThread(id));
                return CreateToolResult(result.Payload, result.StatusCode >= 400);
            }

            return CreateToolResult(new { error = "Unknown tool: " + name }, true);
        }

        private object CreateToolResult(object payload, bool isError)
        {
            return new
            {
                content = new object[]
                {
                    new
                    {
                        type = "text",
                        text = json.Serialize(payload)
                    }
                },
                structuredContent = payload,
                isError
            };
        }

        private object[] GetGames(string query)
        {
            return PlayniteApi.MainView.UIDispatcher.Invoke(() => GetGamesOnUiThread(query));
        }

        private object[] GetGamesOnUiThread(string query)
        {
            var games = PlayniteApi.Database.Games.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(query))
            {
                games = games.Where(game => game.Name != null &&
                    game.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return games
                .OrderBy(game => game.Name)
                .Take(100)
                .Select(ToDto)
                .ToArray();
        }

        private object ToDto(Game game)
        {
            return new
            {
                id = game.Id.ToString(),
                name = game.Name,
                gameId = game.GameId,
                isInstalled = game.IsInstalled,
                isRunning = game.IsRunning,
                isLaunching = game.IsLaunching,
                pluginId = game.PluginId.ToString()
            };
        }

        private BridgeResponse LaunchGameOnUiThread(Guid id)
        {
            var game = PlayniteApi.Database.Games.Get(id);
            if (game == null)
            {
                return new BridgeResponse(new { error = "Game not found." }, 404);
            }

            PlayniteApi.StartGame(id);
            return new BridgeResponse(new { ok = true, game = ToDto(game) }, 200);
        }

        private void WriteJson(HttpListenerContext context, object payload, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(json.Serialize(payload));
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        private void AddMcpHeaders(HttpListenerContext context)
        {
            context.Response.Headers["MCP-Protocol-Version"] = McpProtocolVersion;

            var origin = context.Request.Headers["Origin"];
            if (IsAllowedOrigin(origin) && !string.IsNullOrWhiteSpace(origin))
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                context.Response.Headers["Access-Control-Allow-Headers"] = "Accept, Content-Type, MCP-Protocol-Version";
                context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                context.Response.Headers["Vary"] = "Origin";
            }
        }

        private bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return true;
            }

            Uri uri;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out uri))
            {
                return false;
            }

            return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        }

        private object CreateMcpError(object id, int code, string message)
        {
            return new
            {
                jsonrpc = "2.0",
                id,
                error = new
                {
                    code,
                    message
                }
            };
        }

        private string GetString(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null)
            {
                return null;
            }

            return Convert.ToString(source[key]);
        }

        private Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key))
            {
                return null;
            }

            return source[key] as Dictionary<string, object>;
        }

        private class BridgeResponse
        {
            public object Payload { get; }
            public int StatusCode { get; }

            public BridgeResponse(object payload, int statusCode)
            {
                Payload = payload;
                StatusCode = statusCode;
            }
        }
    }
}
