using System.Collections.Concurrent;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Ui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;

namespace Catsino.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/catsino";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("Catsino");
    private readonly CatsinoRuntime runtime;
    private readonly CatsinoWindow window;
    private readonly SessionPanelRenderer sessionPanel;
    private readonly Dictionary<Guid, SessionWindow> sessionWindows = [];
    private readonly ConcurrentQueue<Guid> pendingSessionOpens = new();
    private readonly ConcurrentQueue<Guid> pendingSessionCloses = new();
    private bool disposed;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPlayerState playerState,
        IFramework framework,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        IGameGui gameGui,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        ICommandManager commandManager,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        // Initialize ECommons before anything that uses its trade automation (Callback, NeoTaskManager,
        // ClickAddonButton, AddonMaster, throttlers, Svc) is constructed.
        ECommonsMain.Init(pluginInterface, this);
        runtime = new CatsinoRuntime(pluginInterface, playerState, framework, objectTable, targetManager, condition, gameGui, dataManager, pluginLog);
        var blackjackPanel = new BlackjackPanelRenderer(runtime, new CardTextures(textureProvider));
        sessionPanel = new SessionPanelRenderer(runtime, sessionId => pendingSessionOpens.Enqueue(sessionId), blackjackPanel);
        window = new CatsinoWindow(runtime, sessionPanel);
        windowSystem.AddWindow(window);
        runtime.SessionRemoved += OnSessionRemoved;

        commandManager.AddHandler(Command, new CommandInfo((_, _) => window.Toggle())
        {
            HelpMessage = "Open the Catsino dealer client.",
            ShowInHelp = true,
        });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        runtime.SessionRemoved -= OnSessionRemoved;
        commandManager.RemoveHandler(Command);
        windowSystem.RemoveAllWindows();
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ECommonsMain.Dispose();
    }

    private void OpenMainUi() => window.IsOpen = true;

    private void Draw()
    {
        while (pendingSessionOpens.TryDequeue(out var sessionId))
        {
            if (!sessionWindows.TryGetValue(sessionId, out var sessionWindow))
            {
                sessionWindow = new SessionWindow(sessionId, sessionPanel, id => pendingSessionCloses.Enqueue(id));
                sessionWindows.Add(sessionId, sessionWindow);
                windowSystem.AddWindow(sessionWindow);
            }

            runtime.TrackSession(sessionId);
            sessionWindow.IsOpen = true;
        }

        while (pendingSessionCloses.TryDequeue(out var sessionId))
        {
            if (sessionWindows.Remove(sessionId, out var sessionWindow))
            {
                sessionWindow.IsOpen = false;
                windowSystem.RemoveWindow(sessionWindow);
            }
        }

        windowSystem.Draw();
    }

    private void OnSessionRemoved(Guid sessionId) => pendingSessionCloses.Enqueue(sessionId);
}
