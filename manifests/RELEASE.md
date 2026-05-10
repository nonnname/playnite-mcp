# Release Checklist

1. Close Playnite so the plugin DLL is not locked.
2. Build the release plugin:

```powershell
dotnet build .\PlayniteMcpServer.csproj -c Release
```

3. Pack the built extension folder with Playnite Toolbox:

```powershell
Toolbox.exe pack ".\bin\Release" ".\dist"
```

Or run the release helper with an explicit Toolbox path:

```powershell
.\manifests\pack-release.ps1 -ToolboxPath "<Playnite install directory>\Toolbox.exe"
```

Before the GitHub Release asset exists, use `-SkipVerify` to create the `.pext` first:

```powershell
.\manifests\pack-release.ps1 -ToolboxPath "<Playnite install directory>\Toolbox.exe" -SkipVerify
```

4. Upload the generated `.pext` to:

```text
https://github.com/nonnname/playnite-mcp/releases/tag/v0.2.1
```

Expected package name:

```text
PlayniteMcpServer_c19604f7-f5a1-4ee1-9f85-db1df598a1c5_0_2_1.pext
```

5. If the asset name differs, update `PackageUrl` in `installer.yaml`.
6. Verify manifests after pushing the branch and uploading the `.pext`:

```powershell
Toolbox.exe verify installer ".\manifests\installer.yaml"
Toolbox.exe verify addon ".\manifests\addon.yaml"
```

7. Submit `manifests/addon.yaml` as a new manifest in `addons/generic` in the PlayniteAddonDatabase repository.
