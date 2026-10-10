using Dalamud.Plugin.Services;
using Poser.Services;
using System;

namespace Poser;

/// <summary>
/// Narrow text adapter for opening Poser. Product features are exposed
/// through the UI, not a parallel debug console.
/// </summary>
public sealed class CommandRouter
{
    private readonly IUIManager _ui;
    private readonly IChatGui _chat;

    public CommandRouter(
        IUIManager ui,
        IChatGui chat)
    {
        _ui = ui;
        _chat = chat;
    }

    public void Handle(string args)
    {
        var parts = args.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            _ui.ToggleMainWindow();
            return;
        }

        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "help":
                    PrintHelp();
                    break;
                default:
                    Print($"Unknown command '{parts[0]}'.");
                    PrintHelp();
                    break;
            }
        }
        catch (Exception error)
        {
            Print($"Command failed: {error.Message}");
        }
    }

    private void PrintHelp()
        => Print("Commands: /poser · /poser help");

    private void Print(string message)
        => _chat.Print($"[Poser] {message}");
}
