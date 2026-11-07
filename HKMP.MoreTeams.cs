using System.Collections.Generic;
using Hkmp.Api.Client;
using Hkmp.Api.Server;
using Modding;
using UnityEngine;

namespace HKMP.MoreTeams;

public sealed class HKMPMoreTeams : Mod, IMenuMod, IGlobalSettings<MoreTeamsGlobalSettings> {
    private static bool _clientAddonRegistered;
    private static bool _serverAddonRegistered;
	public HKMPMoreTeams() : base("HKMP.MoreTeams")
	{
	}
	public static MoreTeamsGlobalSettings GlobalSettings { get; private set; } = new MoreTeamsGlobalSettings();

    public override string GetVersion() {
        return "1.0.0.0";
    }

    public bool ToggleButtonInsideMenu => false;

    public void OnLoadGlobal(MoreTeamsGlobalSettings settings) {
        GlobalSettings = settings ?? new MoreTeamsGlobalSettings();
    }

    public MoreTeamsGlobalSettings OnSaveGlobal() {
        return GlobalSettings ?? new MoreTeamsGlobalSettings();
    }

    public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggleButtonEntry) {
        if (ListRuntimeIntegration.IsInstalled) {
            return new List<IMenuMod.MenuEntry>();
        }

        return new List<IMenuMod.MenuEntry> {
            new IMenuMod.MenuEntry(
                "Allow players to create teams",
                new[] { "Off", "On" },
                "Allows non-administrator players to create additional teams on this host.",
                value => {
                    GlobalSettings.AllowPlayerTeamCreation = value == 1;
                    MoreTeamsServerAddon.ApplyLocalCreationSetting(GlobalSettings.AllowPlayerTeamCreation);
                },
                () => GlobalSettings.AllowPlayerTeamCreation ? 1 : 0)
        };
    }

    public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects) {
        if (GlobalSettings == null) {
            GlobalSettings = new MoreTeamsGlobalSettings();
        }

        if (!_clientAddonRegistered) {
            try {
                ClientAddon.RegisterAddon(new MoreTeamsClientAddon());
                _clientAddonRegistered = true;
                Modding.Logger.Log("HKMP.MoreTeams client addon registered from its own mod folder");
            } catch (System.Exception exception) {
                Modding.Logger.LogWarn("HKMP.MoreTeams could not register its client addon: " + exception);
            }
        }

        if (!_serverAddonRegistered) {
            try {
                ServerAddon.RegisterAddon(new MoreTeamsServerAddon());
                _serverAddonRegistered = true;
                Modding.Logger.Log("HKMP.MoreTeams server addon registered from its own mod folder");
            } catch (System.Exception exception) {
                Modding.Logger.LogWarn("HKMP.MoreTeams could not register its server addon: " + exception);
            }
        }

        Modding.Logger.Log("HKMP.MoreTeams initialized without Harmony; team colors are refreshed by the client addon");
    }
}
