using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace XivPlaytime;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; }
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public List<PlayInterval> Pending { get; set; } = [];
}

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xivplaytime";
    private const string Site = "https://spacegho.st/scrobble/";
    private const int AwayFromKeyboardStatus = 17;
    private const long MaxBackfillMs = 29L * 86_400_000;
    private static readonly Regex TokenPattern = new(@"\Asgs_[A-Za-z0-9_-]{43}\z");

    private readonly IDalamudPluginInterface plugin;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly Configuration config;
    private readonly PlayClock clock = new();
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private readonly CancellationTokenSource stopping = new();
    private Task<HttpStatusCode?>? upload;
    private PlayInterval[] sent = [];
    private int tokenGeneration;
    private int sentGeneration;
    private long nextSample;
    private long nextUpload;
    private bool visible;
    private string draftToken = "";
    private string activity = "Waiting for the game world";
    private string sync = "Waiting for playtime";

    public Plugin(IDalamudPluginInterface plugin, IClientState client, IObjectTable objects,
        ICondition condition, IFramework framework, ICommandManager commands)
    {
        this.plugin = plugin;
        this.client = client;
        this.objects = objects;
        this.condition = condition;
        this.framework = framework;
        this.commands = commands;
        config = plugin.GetPluginConfig() as Configuration ?? new Configuration();

        framework.Update += Update;
        plugin.UiBuilder.Draw += Draw;
        plugin.UiBuilder.OpenConfigUi += Open;
        commands.AddHandler(Command, new CommandInfo((_, _) => Open())
        {
            HelpMessage = "Open the XivPlaytime settings."
        });
    }

    private void Open()
    {
        draftToken = "";
        visible = true;
    }

    private unsafe string ReadActivity()
    {
        if (!config.Enabled)
            return "Tracking paused";
        if (!TokenPattern.IsMatch(config.Token))
            return "Add a Scrobbles client token";
        if (!client.IsLoggedIn || client.TerritoryType == 0 || objects.LocalPlayer == null
            || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.LoggingOut])
            return "Waiting for the game world";
        var info = InfoModule.Instance();
        if (info == null)
            return "Waiting for online status";
        // The bitset includes AFK even when another status takes display priority.
        return (info->OnlineStatusFlags & (1UL << AwayFromKeyboardStatus)) != 0 ? "Paused · AFK" : "Recording active time";
    }

    private void Update(IFramework _)
    {
        var tick = Environment.TickCount64;
        if (tick < nextSample)
            return;
        nextSample = tick + 500;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        activity = ReadActivity();
        SaveInterval(clock.Sample(activity == "Recording active time", now, tick));
        Sync(tick, now);
    }

    private void SaveInterval(PlayInterval? interval)
    {
        if (interval is null)
            return;
        config.Pending.Add(interval);
        plugin.SavePluginConfig(config);
    }

    private void Sync(long tick, long now)
    {
        if (upload is { IsCompleted: false })
            return;
        if (upload != null)
        {
            var result = upload.GetAwaiter().GetResult();
            upload = null;
            if (sentGeneration == tokenGeneration)
            {
                if (result is HttpStatusCode.OK)
                {
                    foreach (var interval in sent)
                        config.Pending.Remove(interval);
                    plugin.SavePluginConfig(config);
                    sync = "Synced to Spaceghost Scrobbles";
                }
                else
                {
                    sync = result switch
                    {
                        HttpStatusCode.Unauthorized => "Client token rejected. Check the token in your Scrobbles account.",
                        HttpStatusCode.Forbidden => "Scrobbling is paused or blocked on your Scrobbles account.",
                        _ => "Sync paused. Pending playtime will retry automatically."
                    };
                    nextUpload = tick + 60_000;
                }
            }
        }
        if (tick < nextUpload || !TokenPattern.IsMatch(config.Token))
            return;
        if (config.Pending.RemoveAll(interval => interval.StartedAt < now - MaxBackfillMs) > 0)
            plugin.SavePluginConfig(config);
        if (config.Pending.Count == 0)
            return;
        sent = config.Pending.Take(32).ToArray();
        sentGeneration = tokenGeneration;
        nextUpload = tick + 5_000;
        upload = Send(config.Token, sent, stopping.Token);
    }

    private async Task<HttpStatusCode?> Send(string token, PlayInterval[] intervals, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://scrobble.spacegho.st/1/game-sessions")
        {
            Content = JsonContent.Create(new { game = "ffxiv", intervals })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            return response.StatusCode;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    private void Draw()
    {
        if (!visible)
            return;
        ImGui.SetNextWindowSize(new(475, 290), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("XivPlaytime", ref visible))
        {
            ImGui.TextUnformatted(activity);
            ImGui.TextWrapped(sync);
            ImGui.TextUnformatted($"Pending intervals: {config.Pending.Count}");
            ImGui.Separator();
            ImGui.InputText("Client token", ref draftToken, 64, ImGuiInputTextFlags.Password);
            var token = draftToken.Trim();
            ImGui.BeginDisabled(!TokenPattern.IsMatch(token));
            if (ImGui.Button("Save token"))
            {
                if (config.Token != token)
                {
                    // Never upload play collected for one credential under another.
                    clock.Close();
                    config.Pending.Clear();
                    config.Token = token;
                    tokenGeneration++;
                    nextUpload = 0;
                    sync = "Waiting for playtime";
                    plugin.SavePluginConfig(config);
                }
                draftToken = "";
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Get a token"))
                OpenUrl(Site + "account/");
            var enabled = config.Enabled;
            ImGui.BeginDisabled(!TokenPattern.IsMatch(config.Token));
            if (ImGui.Checkbox("Track active playtime", ref enabled))
            {
                SaveInterval(clock.Close());
                config.Enabled = enabled;
                plugin.SavePluginConfig(config);
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped("Uses the game's AFK status. Character names, locations and input are never collected.");
            if (ImGui.Button("Open Playing on Scrobbles"))
                OpenUrl(Site + "playing/");
        }
        ImGui.End();
    }

    private static void OpenUrl(string url) =>
        Dalamud.Utility.Util.OpenLink(url);

    public void Dispose()
    {
        framework.Update -= Update;
        plugin.UiBuilder.Draw -= Draw;
        plugin.UiBuilder.OpenConfigUi -= Open;
        commands.RemoveHandler(Command);
        SaveInterval(clock.Close());
        stopping.Cancel();
        http.Dispose();
        stopping.Dispose();
    }
}
