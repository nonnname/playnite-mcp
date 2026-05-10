# Playnite MCP Server

This Playnite extension exposes a local Streamable HTTP MCP endpoint directly from Playnite.

## Endpoints

- `http://127.0.0.1:8977/mcp`: MCP Streamable HTTP endpoint.
- `http://127.0.0.1:8977/health`: manual health check.
- `http://127.0.0.1:8977/games`: debug endpoint for listing games.

Launching games is only exposed through the MCP `tools/call` endpoint. There is no side-effecting GET endpoint.

## MCP Tools

- `playnite_list_games`: list up to 100 games.
- `playnite_search_games`: search games by name.
- `playnite_launch_game`: launch a game by Playnite database GUID.

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
