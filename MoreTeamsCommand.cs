using System;
using Hkmp.Api.Command.Server;

namespace HKMP.MoreTeams;

public sealed class MoreTeamsCommand : IServerCommand {
    private readonly MoreTeamsServerAddon _addon;

    public string Trigger => "/moreteams";
    public string[] Aliases => Array.Empty<string>();
    public bool AuthorizedOnly => false;

    public MoreTeamsCommand(MoreTeamsServerAddon addon) {
        _addon = addon ?? throw new ArgumentNullException(nameof(addon));
    }

    public void Execute(ICommandSender commandSender, string[] arguments) {
        if (!commandSender.IsAuthorized && commandSender.Type != CommandSenderType.Console) {
            commandSender.SendMessage("You are not authorized to manage additional teams");
            return;
        }

        if (arguments.Length == 2 && string.Equals(arguments[1], "reload", StringComparison.OrdinalIgnoreCase)) {
            if (_addon.ReloadConfiguration(out var reason)) {
                commandSender.SendMessage("Additional teams reloaded");
            } else {
                commandSender.SendMessage("Could not reload teams.dat: " + reason);
            }

            return;
        }

        commandSender.SendMessage("Usage: /moreteams reload");
    }
}
