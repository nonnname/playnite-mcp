# Playnite MCP Server

This Playnite extension exposes a local Streamable HTTP MCP endpoint directly from Playnite.

## Endpoints

- `http://127.0.0.1:8977/mcp`: MCP Streamable HTTP endpoint.
- `http://127.0.0.1:8977/health`: manual health check.
- `http://127.0.0.1:8977/games`: debug endpoint for listing games.

Launching games is only exposed through the MCP `tools/call` endpoint. There is no side-effecting GET endpoint.

## MCP Tools

- `playnite_list_games`: list games with filters, sorting, limit, and offset.
- `playnite_search_games`: search games by name with the same filters as listing.
- `playnite_get_game`: get detailed game information by id or name.
- `playnite_launch_game`: launch a game by id or by unambiguous name.

### Filtering

`playnite_list_games` and `playnite_search_games` support:

- `query`: optional text search by game name.
- `installedOnly`: only installed games.
- `runningOnly`: only running games.
- `launchingOnly`: only games currently launching.
- `favoriteOnly`: only favorite games.
- `includeHidden`: include hidden games, defaults to `true`.
- `limit`: result page size, defaults to `100`, maximum `200`.
- `offset`: number of matching games to skip.
- `sortBy`: `name`, `lastActivity`, `playtime`, or `added`.

`playnite_launch_game` supports:

- `id`: Playnite game database GUID.
- `name`: game name to resolve when `id` is not provided.
- `preferInstalled`: prefer installed matches when resolving by name, defaults to `true`.
- `exactMatch`: require exact name match when resolving by name, defaults to `false`.

If a name matches multiple games, the server returns candidates and does not launch anything.

## Example Prompts

```text
Show my installed Playnite games.
```

```text
Find installed games with Diablo in the title.
```

```text
Launch Diablo IV from Playnite.
```

```text
Show my 20 most recently played games.
```

```text
Get details for Baldur's Gate 3.
```

```text
List my favorite installed games sorted by playtime.
```

## MCP Client URL

Use this URL in MCP clients that support Streamable HTTP:

```text
http://127.0.0.1:8977/mcp
```

## Manual Checks

Build the extension and add the build output folder to Playnite:

```powershell
dotnet build .\PlayniteMcpServer.csproj
```

Then add this folder in Playnite `Settings -> For developers -> External extensions`:

```text
<repo>\extensions\PlayniteMcpServer\bin\Debug
```

Restart Playnite after changing or rebuilding the plugin. Playnite keeps plugin DLLs loaded while running, so close Playnite before rebuilding into `bin\Debug`.

For normal installation instead of developer loading, copy the built extension folder containing `extension.yaml` and `PlayniteMcpServer.dll` into Playnite's `Extensions` directory:

- Installed Playnite: `%AppData%\Playnite\Extensions`
- Portable Playnite: `Extensions` inside the Playnite install directory

Plugins are loaded on Playnite startup and cannot be reloaded at runtime, so restart Playnite after changing the DLL.

```powershell
Invoke-RestMethod http://127.0.0.1:8977/health
```

```powershell
$headers = @{
  Accept = "application/json, text/event-stream"
  "Content-Type" = "application/json"
}

$body = @{
  jsonrpc = "2.0"
  id = 1
  method = "initialize"
  params = @{
    protocolVersion = "2025-06-18"
    capabilities = @{}
    clientInfo = @{
      name = "manual-test"
      version = "0.1.0"
    }
  }
} | ConvertTo-Json -Depth 8

Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8977/mcp -Headers $headers -Body $body
```

## Publishing

Use Playnite's bundled `Toolbox.exe` to pack and verify releases:

```powershell
dotnet build .\PlayniteMcpServer.csproj -c Release
Toolbox.exe pack ".\bin\Release" ".\dist"
Toolbox.exe verify installer ".\manifests\installer.yaml"
Toolbox.exe verify addon ".\manifests\addon.yaml"
```

Upload the generated `.pext` to a GitHub Release and update `manifests/installer.yaml` if the release URL changes.
If the release asset is not uploaded yet, run `pack-release.ps1 -SkipVerify` first, then run verification after the release URL is live.
