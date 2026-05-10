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
                    description = "List games from the local Playnite library with optional filters and pagination.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = GetGameQuerySchemaProperties(false),
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
                        properties = GetGameQuerySchemaProperties(true),
                        required = new[] { "query" },
                        additionalProperties = false
                    }
                },
                new
                {
                    name = "playnite_get_game",
                    title = "Get Playnite Game Details",
                    description = "Get detailed information for one Playnite game by GUID or name.",
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
                            },
                            {
                                "name",
                                new
                                {
                                    type = "string",
                                    description = "Game name to resolve when id is not provided."
                                }
                            }
                        },
                        additionalProperties = false
                    }
                },
                new
                {
                    name = "playnite_launch_game",
                    title = "Launch Playnite Game",
                    description = "Launch a local Playnite game by GUID or by unambiguous name.",
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
                            },
                            {
                                "name",
                                new
                                {
                                    type = "string",
                                    description = "Game name to launch when id is not provided."
                                }
                            },
                            {
                                "preferInstalled",
                                new
                                {
                                    type = "boolean",
                                    description = "Prefer installed games when resolving by name. Defaults to true."
                                }
                            },
                            {
                                "exactMatch",
                                new
                                {
                                    type = "boolean",
                                    description = "Require exact name match when resolving by name. Defaults to false."
                                }
                            }
                        },
                        additionalProperties = false
                    }
                }
            };
        }

        private Dictionary<string, object> GetGameQuerySchemaProperties(bool requireQuery)
        {
            return new Dictionary<string, object>
            {
                {
                    "query",
                    new
                    {
                        type = "string",
                        description = requireQuery ? "Part of the game name." : "Optional part of the game name."
                    }
                },
                {
                    "installedOnly",
                    new
                    {
                        type = "boolean",
                        description = "Only include installed games."
                    }
                },
                {
                    "runningOnly",
                    new
                    {
                        type = "boolean",
                        description = "Only include currently running games."
                    }
                },
                {
                    "launchingOnly",
                    new
                    {
                        type = "boolean",
                        description = "Only include games currently launching."
                    }
                },
                {
                    "favoriteOnly",
                    new
                    {
                        type = "boolean",
                        description = "Only include favorite games."
                    }
                },
                {
                    "includeHidden",
                    new
                    {
                        type = "boolean",
                        description = "Include hidden games. Defaults to true."
                    }
                },
                {
                    "limit",
                    new
                    {
                        type = "integer",
                        description = "Maximum number of games to return. Defaults to 100, maximum 200."
                    }
                },
                {
                    "offset",
                    new
                    {
                        type = "integer",
                        description = "Number of matching games to skip. Defaults to 0."
                    }
                },
                {
                    "sortBy",
                    new
                    {
                        type = "string",
                        description = "Sort order: name, lastActivity, playtime, added. Defaults to name."
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
                var payload = GetGameList(arguments, false);
                return CreateToolResult(payload, false);
            }

            if (name == "playnite_search_games")
            {
                var payload = GetGameList(arguments, true);
                return CreateToolResult(payload, false);
            }

            if (name == "playnite_get_game")
            {
                var result = PlayniteApi.MainView.UIDispatcher.Invoke(() => GetGameDetailsOnUiThread(arguments));
                return CreateToolResult(result.Payload, result.StatusCode >= 400);
            }

            if (name == "playnite_launch_game")
            {
                var result = PlayniteApi.MainView.UIDispatcher.Invoke(() => LaunchGameOnUiThread(arguments));
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
            return PlayniteApi.MainView.UIDispatcher.Invoke(() => QueryGamesOnUiThread(new GameQueryOptions
            {
                Query = query,
                IncludeHidden = true,
                Limit = 100,
                Offset = 0,
                SortBy = "name"
            }).Games);
        }

        private object GetGameList(Dictionary<string, object> arguments, bool requireQuery)
        {
            var options = GetGameQueryOptions(arguments);
            if (requireQuery && string.IsNullOrWhiteSpace(options.Query))
            {
                return new
                {
                    games = new object[0],
                    total = 0,
                    limit = options.Limit,
                    offset = options.Offset,
                    error = "query is required."
                };
            }

            return PlayniteApi.MainView.UIDispatcher.Invoke(() => QueryGamesOnUiThread(options));
        }

        private GameListResult QueryGamesOnUiThread(GameQueryOptions options)
        {
            var games = PlayniteApi.Database.Games.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(options.Query))
            {
                games = games.Where(game => game.Name != null &&
                    game.Name.IndexOf(options.Query, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (options.InstalledOnly)
            {
                games = games.Where(game => game.IsInstalled);
            }

            if (options.RunningOnly)
            {
                games = games.Where(game => game.IsRunning);
            }

            if (options.LaunchingOnly)
            {
                games = games.Where(game => game.IsLaunching);
            }

            if (options.FavoriteOnly)
            {
                games = games.Where(game => game.Favorite);
            }

            if (!options.IncludeHidden)
            {
                games = games.Where(game => !game.Hidden);
            }

            var filtered = SortGames(games, options.SortBy).ToList();
            var page = filtered
                .Skip(options.Offset)
                .Take(options.Limit)
                .Select(ToSummaryDto)
                .ToArray();

            return new GameListResult
            {
                Games = page,
                Total = filtered.Count,
                Limit = options.Limit,
                Offset = options.Offset
            };
        }

        private IEnumerable<Game> SortGames(IEnumerable<Game> games, string sortBy)
        {
            switch ((sortBy ?? string.Empty).ToLowerInvariant())
            {
                case "lastactivity":
                    return games.OrderByDescending(game => game.LastActivity ?? DateTime.MinValue).ThenBy(game => game.Name);
                case "playtime":
                    return games.OrderByDescending(game => game.Playtime).ThenBy(game => game.Name);
                case "added":
                    return games.OrderByDescending(game => game.Added ?? DateTime.MinValue).ThenBy(game => game.Name);
                default:
                    return games.OrderBy(game => game.Name);
            }
        }

        private GameQueryOptions GetGameQueryOptions(Dictionary<string, object> arguments)
        {
            return new GameQueryOptions
            {
                Query = GetString(arguments, "query"),
                InstalledOnly = GetBool(arguments, "installedOnly", false),
                RunningOnly = GetBool(arguments, "runningOnly", false),
                LaunchingOnly = GetBool(arguments, "launchingOnly", false),
                FavoriteOnly = GetBool(arguments, "favoriteOnly", false),
                IncludeHidden = GetBool(arguments, "includeHidden", true),
                Limit = Math.Min(Math.Max(GetInt(arguments, "limit", 100), 1), 200),
                Offset = Math.Max(GetInt(arguments, "offset", 0), 0),
                SortBy = GetString(arguments, "sortBy") ?? "name"
            };
        }

        private object ToSummaryDto(Game game)
        {
            return new
            {
                id = game.Id.ToString(),
                name = game.Name,
                gameId = game.GameId,
                isInstalled = game.IsInstalled,
                isRunning = game.IsRunning,
                isLaunching = game.IsLaunching,
                isHidden = game.Hidden,
                isFavorite = game.Favorite,
                playtimeSeconds = game.Playtime,
                lastActivity = game.LastActivity,
                pluginId = game.PluginId.ToString()
            };
        }

        private object ToDetailDto(Game game)
        {
            return new
            {
                id = game.Id.ToString(),
                name = game.Name,
                sortingName = game.SortingName,
                gameId = game.GameId,
                version = game.Version,
                isInstalled = game.IsInstalled,
                isRunning = game.IsRunning,
                isLaunching = game.IsLaunching,
                isHidden = game.Hidden,
                isFavorite = game.Favorite,
                playtimeSeconds = game.Playtime,
                playCount = game.PlayCount,
                added = game.Added,
                modified = game.Modified,
                lastActivity = game.LastActivity,
                installDirectory = game.InstallDirectory,
                pluginId = game.PluginId.ToString(),
                source = game.Source?.Name,
                completionStatus = game.CompletionStatus?.Name,
                platforms = game.Platforms?.Select(platform => platform.Name).ToArray(),
                genres = game.Genres?.Select(genre => genre.Name).ToArray(),
                categories = game.Categories?.Select(category => category.Name).ToArray(),
                tags = game.Tags?.Select(tag => tag.Name).ToArray(),
                developers = game.Developers?.Select(company => company.Name).ToArray(),
                publishers = game.Publishers?.Select(company => company.Name).ToArray(),
                links = game.Links?.Select(link => new { name = link.Name, url = link.Url }).ToArray()
            };
        }

        private BridgeResponse GetGameDetailsOnUiThread(Dictionary<string, object> arguments)
        {
            var resolution = ResolveGameOnUiThread(arguments, false);
            if (resolution.StatusCode >= 400)
            {
                return resolution;
            }

            return new BridgeResponse(new { game = ToDetailDto(resolution.Game) }, 200);
        }

        private BridgeResponse LaunchGameOnUiThread(Dictionary<string, object> arguments)
        {
            var resolution = ResolveGameOnUiThread(arguments, GetBool(arguments, "preferInstalled", true));
            if (resolution.StatusCode >= 400)
            {
                return resolution;
            }

            PlayniteApi.StartGame(resolution.Game.Id);
            return new BridgeResponse(new { ok = true, game = ToSummaryDto(resolution.Game) }, 200);
        }

        private BridgeResponse ResolveGameOnUiThread(Dictionary<string, object> arguments, bool preferInstalled)
        {
            var idText = GetString(arguments, "id");
            if (!string.IsNullOrWhiteSpace(idText))
            {
                Guid id;
                if (!Guid.TryParse(idText, out id))
                {
                    return new BridgeResponse(new { error = "A valid game id is required." }, 400);
                }

                var byId = PlayniteApi.Database.Games.Get(id);
                if (byId == null)
                {
                    return new BridgeResponse(new { error = "Game not found.", id = idText }, 404);
                }

                return BridgeResponse.Success(byId);
            }

            var name = GetString(arguments, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return new BridgeResponse(new { error = "Either id or name is required." }, 400);
            }

            var exactMatch = GetBool(arguments, "exactMatch", false);
            var candidates = PlayniteApi.Database.Games
                .Where(game => game.Name != null && game.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (!candidates.Any())
            {
                return new BridgeResponse(new { error = "No matching game found.", name }, 404);
            }

            var exactCandidates = candidates
                .Where(game => string.Equals(game.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var matches = exactCandidates.Any() ? exactCandidates : (exactMatch ? new List<Game>() : candidates);
            if (!matches.Any())
            {
                return new BridgeResponse(new
                {
                    error = "No exact matching game found.",
                    name,
                    candidates = candidates.Take(10).Select(ToSummaryDto).ToArray()
                }, 404);
            }

            matches = preferInstalled
                ? matches.OrderByDescending(game => game.IsInstalled).ThenBy(game => game.Name).ToList()
                : matches.OrderBy(game => game.Name).ToList();

            var installedMatches = matches.Where(game => game.IsInstalled).ToList();
            if (preferInstalled && installedMatches.Count == 1)
            {
                return BridgeResponse.Success(installedMatches[0]);
            }

            if (matches.Count == 1)
            {
                return BridgeResponse.Success(matches[0]);
            }

            return new BridgeResponse(new
            {
                error = "Multiple matching games found. Use id to choose one.",
                name,
                candidates = matches.Take(10).Select(ToSummaryDto).ToArray()
            }, 409);
        }

        private BridgeResponse LaunchGameOnUiThread(Guid id)
        {
            var game = PlayniteApi.Database.Games.Get(id);
            if (game == null)
            {
                return new BridgeResponse(new { error = "Game not found." }, 404);
            }

            PlayniteApi.StartGame(id);
            return new BridgeResponse(new { ok = true, game = ToSummaryDto(game) }, 200);
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

        private bool GetBool(Dictionary<string, object> source, string key, bool defaultValue)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null)
            {
                return defaultValue;
            }

            if (source[key] is bool)
            {
                return (bool)source[key];
            }

            bool value;
            return bool.TryParse(Convert.ToString(source[key]), out value) ? value : defaultValue;
        }

        private int GetInt(Dictionary<string, object> source, string key, int defaultValue)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(source[key]);
            }
            catch
            {
                return defaultValue;
            }
        }

        private class GameQueryOptions
        {
            public string Query { get; set; }
            public bool InstalledOnly { get; set; }
            public bool RunningOnly { get; set; }
            public bool LaunchingOnly { get; set; }
            public bool FavoriteOnly { get; set; }
            public bool IncludeHidden { get; set; }
            public int Limit { get; set; }
            public int Offset { get; set; }
            public string SortBy { get; set; }
        }

        private class GameListResult
        {
            public object[] Games { get; set; }
            public int Total { get; set; }
            public int Limit { get; set; }
            public int Offset { get; set; }
        }

        private class BridgeResponse
        {
            public object Payload { get; }
            public int StatusCode { get; }
            public Game Game { get; }

            public BridgeResponse(object payload, int statusCode)
            {
                Payload = payload;
                StatusCode = statusCode;
            }

            private BridgeResponse(Game game)
            {
                Game = game;
                StatusCode = 200;
            }

            public static BridgeResponse Success(Game game)
            {
                return new BridgeResponse(game);
            }
        }
    }
}
