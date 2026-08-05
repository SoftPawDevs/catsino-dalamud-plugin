using Catsino.Plugin.Runtime;
using Catsino.Plugin.Ui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Catsino.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/catsino";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("Catsino");
    private readonly CatsinoRuntime runtime;
    private readonly CatsinoWindow window;
    private bool disposed;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPlayerState playerState,
        IFramework framework,
        ICommandManager commandManager,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        runtime = new CatsinoRuntime(pluginInterface, playerState, framework, pluginLog);
        window = new CatsinoWindow(runtime);
        windowSystem.AddWindow(window);

        commandManager.AddHandler(Command, new CommandInfo((_, _) => window.Toggle())
        {
            HelpMessage = "Open the Catsino dealer client.",
            ShowInHelp = true,
        });
        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        commandManager.RemoveHandler(Command);
        windowSystem.RemoveAllWindows();
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OpenMainUi() => window.IsOpen = true;
}
