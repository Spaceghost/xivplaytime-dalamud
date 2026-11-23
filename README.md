# XivPlaytime

XivPlaytime records active time in FINAL FANTASY XIV and sends it to [Playing on Spaceghost Scrobbles](https://spacegho.st/scrobble/playing/).

1. In Dalamud Settings → Experimental → Custom Plugin Repositories, add `https://spacegho.st/mods/ffxiv/plugins.json`.
2. Install XivPlaytime. Create a client token in your [Scrobbles account](https://spacegho.st/scrobble/account/).
3. Open `/xivplaytime`, save the token, and enable tracking.

The clock uses FFXIV’s manual and automatic Away from Keyboard status. Keep automatic AFK enabled in the game settings for idle detection. Login screens, loading, logout and long gaps in game updates do not count. The game can still report you as active until its AFK timer expires.

The plugin sends only the game ID and active time ranges. Queued intervals are saved locally and retried when connectivity returns. The client token is stored in the local Dalamud configuration and can be revoked from the account page. Session dates and details stay private. You can share your display name and totals on the Playing page.

## Build

Use .NET 10 and Dalamud API 15 references. On Windows, the SDK can discover local Dalamud references. On Linux, point `DALAMUD_HOME` at the API 15 DLL directory.

```sh
DALAMUD_HOME=/path/to/Dalamud dotnet build XivPlaytime.csproj -c Release
```

The Dalamud SDK creates the manifest and zip under the release output directory. The stable 0.1.0 package has offline timing and API checks. In-game installation and AFK behavior still require live verification.
