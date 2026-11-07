using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Api.Client;
using Hkmp.Api.Client.Networking;
using Hkmp.Game;
using Hkmp.Networking.Packet;
using UnityEngine;

namespace HKMP.MoreTeams;

public sealed class MoreTeamsClientAddon : ClientAddon {
    private static MoreTeamsClientAddon _instance;

    private IClientApi _api;
    private IClientAddonNetworkSender<AddonPacketId> _sender;
    private IClientAddonNetworkReceiver<AddonPacketId> _receiver;
    private List<TeamDefinition> _teams = BuiltinTeams.All.ToList();
    private bool _canManage;
    private bool _canCreate;
    private bool _hasListIntegration;
    private bool _allowPlayerCreation;
    private bool _creatorsBecomeLeaders;
    private bool _hasReceivedCatalog;

    protected override string Name => "HKMP.MoreTeams";
    protected override string Version => "1.0.0.0";
    public override bool NeedsNetwork => true;

    public override void Initialize(IClientApi clientApi) {
        _instance = this;
        _api = clientApi ?? throw new ArgumentNullException(nameof(clientApi));

        Logger.Info("HKMP.MoreTeams custom team text and outline colors are handled by the refresh runner; no Harmony patch is used");

        try {
            ListRuntimeIntegration.InstallClient(this);
            Logger.Info(ListRuntimeIntegration.IsAvailable
                ? "HKMP.MoreTeams HKMP.List runtime integration installed"
                : "HKMP.MoreTeams HKMP.List client integration is waiting: " + ListRuntimeIntegration.GetAvailabilityDetails());
        } catch (Exception exception) {
            Logger.Warn("HKMP.MoreTeams could not install the HKMP.List client integration: " + exception);
        }

        try {
            InGameTeamMenu.Install(this, _api, _api.ClientManager);
            Logger.Info("HKMP.MoreTeams in-game settings integration initialized");
        } catch (Exception exception) {
            Logger.Warn("HKMP.MoreTeams could not initialize the in-game settings integration: " + exception);
        }

        _sender = _api.NetClient.GetNetworkSender<AddonPacketId>(this);
        _receiver = _api.NetClient.GetNetworkReceiver<AddonPacketId>(this, CreatePacket);
        _receiver.RegisterPacketHandler<TeamCatalogPacket>(AddonPacketId.TeamCatalog, HandleTeamCatalog);

        _api.ClientManager.ConnectEvent += RequestTeamCatalog;
        _api.ClientManager.DisconnectEvent += ResetCatalog;
        _api.ClientManager.PlayerEnterSceneEvent += RefreshPlayerColor;
        TeamColorService.StartRefreshRunner(this);
        var addonAssembly = typeof(MoreTeamsClientAddon).Assembly;
        Logger.Info("HKMP.MoreTeams client addon initialized; version 1.0.0.0; assembly: " + addonAssembly.FullName + "; path: " + addonAssembly.Location + "; client manager type: " + _api.ClientManager.GetType().AssemblyQualifiedName);
    }

    internal static MoreTeamsClientAddon Instance => _instance;

    internal static string LocalClientUsername {
        get {
            try {
                return _instance?._api?.ClientManager?.Username ?? string.Empty;
            } catch {
                return string.Empty;
            }
        }
    }

    internal static bool TryGetTeam(int id, out TeamDefinition team) {
        var found = _instance == null ? null : _instance._teams.FirstOrDefault(candidate => candidate.Id == id);
        team = found == null ? null : found.Copy();
        return team != null;
    }

    internal IReadOnlyList<TeamDefinition> GetCurrentTeams() {
        return _teams.OrderBy(team => team.Id).Select(team => team.Copy()).ToList();
    }

    internal IReadOnlyList<TeamDefinition> GetBuiltinTeams() {
        return BuiltinTeams.All;
    }

    internal IReadOnlyList<TeamDefinition> GetCustomTeams() {
        return _teams.Where(team => team.Id >= TeamConfigStore.FirstCustomTeamId)
            .OrderBy(team => team.Id)
            .Select(team => team.Copy())
            .ToList();
    }

    internal bool CanManageTeams => _canManage;
    internal bool CanCreateTeams => _canCreate;
    internal bool HasListIntegration => _hasListIntegration;
    internal bool AllowPlayerCreation => _allowPlayerCreation;
    internal bool CreatorsBecomeLeaders => _creatorsBecomeLeaders;
    internal bool HasReceivedCatalog => _hasReceivedCatalog;
    internal int CurrentTeamId => _api?.ClientManager == null
        ? 0
        : HkmpRuntimeResolver.GetLocalPlayerTeamId(_api.ClientManager);

    internal string CurrentTeamName {
        get {
            var teamId = CurrentTeamId;
            var additionalTeam = _teams.FirstOrDefault(team => team.Id == teamId);
            if (additionalTeam != null) {
                return additionalTeam.Name;
            }

            switch (teamId) {
                case 0: return "None";
                case 1: return "Moss";
                case 2: return "Hive";
                case 3: return "Grimm";
                case 4: return "Lifeblood";
                default: return "Team " + teamId;
            }
        }
    }

    internal bool TeamSelectionEnabled {
        get {
            var serverSettings = HkmpRuntimeResolver.GetMemberValue(_api?.ClientManager, "_serverSettings", "ServerSettings");
            var value = HkmpRuntimeResolver.GetMemberValue(serverSettings, "TeamsEnabled");
            return value is bool enabled && enabled;
        }
    }

    internal void SelectTeamFromInGameMenu(byte teamId) {
        if (!_teams.Any(team => team.Id == teamId)) {
            Logger.Warn("Ignored selection of unknown additional team ID " + teamId);
            InGameTeamMenu.RefreshCurrent();
            return;
        }

        if (!TeamSelectionEnabled) {
            Logger.Info("Additional team selection is disabled because HKMP server TeamsEnabled is false");
            InGameTeamMenu.RefreshCurrent();
            return;
        }

        if (_api?.NetClient == null || !_api.NetClient.IsConnected) {
            Logger.Warn("Could not select team " + teamId + " because the HKMP client is not connected");
            InGameTeamMenu.RefreshCurrent();
            return;
        }

        try {
            var team = _teams.First(candidate => candidate.Id == teamId);
            if (!SendRequest(new TeamRequestPacket {
                Action = TeamRequestAction.ChangeTeam,
                TeamId = teamId
            })) {
                Logger.Warn("Could not queue team change for " + team.Name + " (ID " + teamId + ")");
                InGameTeamMenu.RefreshCurrent();
                return;
            }
            Logger.Info("Requested validated server team change for " + team.Name + " (ID " + teamId + ")");
        } catch (Exception exception) {
            Logger.Warn("Could not send HKMP.MoreTeams team-change request: " + exception.GetBaseException());
            InGameTeamMenu.RefreshCurrent();
        }
    }

    internal bool CreateTeamFromInGameMenu(string name, string textColor, string outlineColor, bool outlineEnabled) {
        if (!_canCreate) {
            Logger.Warn("Ignored team creation because the server did not allow player team creation");
            return false;
        }

        if (_teams.Count >= TeamConfigStore.MaximumTeams) {
            Logger.Warn("Ignored team creation because the 64-team limit has been reached");
            return false;
        }

        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length == 0 || normalizedName.Length > 24) {
            Logger.Warn("Ignored team creation because the team name must contain between 1 and 24 characters");
            return false;
        }

        if (_teams.Any(team => string.Equals(team.Name, normalizedName, StringComparison.OrdinalIgnoreCase))) {
            Logger.Warn("Ignored team creation because a team with this name already exists");
            return false;
        }

        if (!IsValidColor(textColor) || !IsValidColor(outlineColor)) {
            Logger.Warn("Ignored team creation because the selected colors are invalid");
            return false;
        }

        return SendRequest(new TeamRequestPacket {
            Action = TeamRequestAction.CreateTeam,
            Name = normalizedName,
            TextColor = textColor,
            OutlineColor = outlineColor,
            OutlineEnabled = outlineEnabled
        });
    }

    internal void DeleteTeam(byte teamId) {
        if (teamId < TeamConfigStore.FirstCustomTeamId
            || teamId > TeamConfigStore.LastCustomTeamId) {
            return;
        }

        if (!SendRequest(new TeamRequestPacket {
            Action = TeamRequestAction.DeleteTeam,
            TeamId = teamId
        })) {
            Logger.Warn("Could not queue deletion for custom team ID " + teamId);
            return;
        }

        Logger.Info("Requested deletion of custom team ID " + teamId);
    }

    internal void SetPlayerCreationEnabled(bool enabled) {
        if (!HasListIntegration) {
            return;
        }

        SendRequest(new TeamRequestPacket {
            Action = TeamRequestAction.SetPlayerCreationEnabled,
            Enabled = enabled
        });
    }

    internal void SetCreatorsBecomeLeaders(bool enabled) {
        if (!HasListIntegration) {
            return;
        }

        SendRequest(new TeamRequestPacket {
            Action = TeamRequestAction.SetCreatorsBecomeLeaders,
            Enabled = enabled
        });
    }

    internal void LogInGameMenuInfo(string message) {
        Logger.Info(message);
    }

    internal void LogInGameMenuWarning(string message) {
        Logger.Warn(message);
    }

    private static IPacketData CreatePacket(AddonPacketId packetId) {
        switch (packetId) {
            case AddonPacketId.TeamCatalog:
                return new TeamCatalogPacket();
            case AddonPacketId.TeamRequest:
                return new TeamRequestPacket();
            default:
                return new TeamRequestPacket();
        }
    }

    private void RequestTeamCatalog() {
        try {
            ListRuntimeIntegration.InstallClient(this);
        } catch (Exception exception) {
            Logger.Warn("Could not initialize HKMP.List client integration before requesting the team catalog: " + exception.GetBaseException());
        }
        SendRequest(new TeamRequestPacket { Action = TeamRequestAction.RequestCatalog });
    }

    private void ResetCatalog() {
        _hasReceivedCatalog = false;
        _teams = BuiltinTeams.All.ToList();
        _canManage = false;
        _canCreate = false;
        _hasListIntegration = ListRuntimeIntegration.IsAvailable;
        _allowPlayerCreation = false;
        _creatorsBecomeLeaders = false;
        InGameTeamMenu.RefreshCurrent();
    }

    private bool SendRequest(TeamRequestPacket packet) {
        if (_sender == null || packet == null || _api == null || !_api.NetClient.IsConnected) {
            return false;
        }

        try {
            _sender.SendSingleData(AddonPacketId.TeamRequest, packet);
            return true;
        } catch (Exception exception) {
            Logger.Warn("Could not send an HKMP.MoreTeams request: " + exception.GetBaseException());
            return false;
        }
    }

    private void HandleTeamCatalog(TeamCatalogPacket packet) {
        if (packet == null) {
            return;
        }

        try {
            ListRuntimeIntegration.InstallClient(this);
        } catch (Exception exception) {
            Logger.Warn("Could not initialize HKMP.List client integration after receiving the team catalog: " + exception.GetBaseException());
        }

        var received = (packet.Teams ?? new List<TeamDefinition>())
            .Where(IsValidTeam)
            .GroupBy(team => team.Id)
            .Select(group => group.First().Copy())
            .ToDictionary(team => (int) team.Id, team => team);

        var merged = new List<TeamDefinition>();
        foreach (var builtin in BuiltinTeams.All) {
            merged.Add(received.TryGetValue(builtin.Id, out var serverBuiltin) ? serverBuiltin : builtin.Copy());
        }

        merged.AddRange(received.Values
            .Where(team => team.Id >= TeamConfigStore.FirstCustomTeamId)
            .OrderBy(team => team.Id)
            .Take(TeamConfigStore.MaximumCustomTeams));

        _teams = merged.OrderBy(team => team.Id).ToList();
        _hasReceivedCatalog = true;
        _canManage = packet.CanManage;
        _canCreate = packet.CanCreate;
        _hasListIntegration = packet.HasListIntegration;
        _allowPlayerCreation = packet.AllowPlayerCreation;
        _creatorsBecomeLeaders = packet.CreatorsBecomeLeaders;
        if (IsValidAuthoritativeTeamId(packet.CurrentTeamId)) {
            if (!HkmpRuntimeResolver.TryApplyLocalPlayerTeam(_api?.ClientManager, packet.CurrentTeamId, out var teamApplyReason)) {
                Logger.Warn("Could not apply the server-confirmed local team ID " + packet.CurrentTeamId + " to HKMP ClientManager: " + teamApplyReason);
            } else {
                Logger.Info("Applied server-confirmed local team ID " + packet.CurrentTeamId + " (" + CurrentTeamName + ") from the More Teams catalog");
            }
        } else {
            Logger.Warn("Ignored invalid server-confirmed local team ID " + packet.CurrentTeamId);
        }
        Logger.Info("Received additional team catalog: " + _teams.Count + " total (" + GetCustomTeams().Count + " custom), current team: " + CurrentTeamId + ", can manage: " + _canManage + ", can create: " + _canCreate + ", List: " + _hasListIntegration);
        InGameTeamMenu.RefreshCurrent();
        ListRuntimeIntegration.RefreshListTeamsPanel();
        RefreshPlayerColors();
    }

    private bool IsValidAuthoritativeTeamId(int teamId) {
        return teamId >= 0 && teamId <= 4 || _teams.Any(team => team.Id == teamId);
    }

    private static bool IsValidTeam(TeamDefinition team) {
        return team != null
            && team.Id >= TeamConfigStore.FirstBuiltinTeamId
            && team.Id <= TeamConfigStore.LastCustomTeamId
            && !string.IsNullOrWhiteSpace(team.Name)
            && IsValidColor(team.TextColor)
            && IsValidColor(team.OutlineColor);
    }

    private static bool IsValidColor(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#') {
            return false;
        }

        for (var index = 1; index < value.Length; index++) {
            var character = value[index];
            if (!(character >= '0' && character <= '9')
                && !(character >= 'a' && character <= 'f')
                && !(character >= 'A' && character <= 'F')) {
                return false;
            }
        }

        return true;
    }

    private void RefreshPlayerColor(IClientPlayer player) {
        if (player == null || !player.IsInLocalScene || player.PlayerContainer == null) {
            return;
        }

        try {
            var refreshed = TeamColorService.ApplyCustomColorToContainer(
                player.PlayerContainer,
                Convert.ToInt32(player.Team)
            );
            if (refreshed > 0) {
                Logger.Info("Applied team text and outline color to player " + player.Id + " (" + player.Username + ")");
            }
        } catch (Exception exception) {
            Logger.Warn("Could not apply team colors after player scene entry: " + exception.GetBaseException());
        }
    }

    internal void OnClientTeamChanged(Team team) {
        var teamId = Convert.ToInt32(team);
        if (TryGetTeam(teamId, out var selectedTeam)) {
            var refreshed = RefreshPlayerColors(false);
            Logger.Info("HKMP confirmed the active team as '" + selectedTeam.Name + "' (ID " + teamId + "); refreshed " + refreshed + " player name color component(s)");
        } else {
            RefreshPlayerColors(false);
            Logger.Info("HKMP confirmed the active team as '" + CurrentTeamName + "' (ID " + teamId + ")");
        }
    }

    internal void RefreshPlayerColorsFromRunner() {
        RefreshPlayerColors(false);
    }

    private void RefreshPlayerColors() {
        RefreshPlayerColors(true);
    }

    private int RefreshPlayerColors(bool logResult) {
        var refreshed = 0;
        var players = _api?.ClientManager?.Players;
        if (players != null) {
            foreach (var player in players) {
                if (player == null || !player.IsInLocalScene || player.PlayerContainer == null) {
                    continue;
                }

                try {
                    refreshed += TeamColorService.ApplyCustomColorToContainer(
                        player.PlayerContainer,
                        Convert.ToInt32(player.Team)
                    );
                } catch (Exception exception) {
                    Logger.Warn("Could not refresh the team color for player " + player.Id + ": " + exception.GetBaseException());
                }
            }
        }

        var hero = HeroController.instance;
        if (hero != null && _api?.ClientManager != null) {
            try {
                refreshed += TeamColorService.ApplyCustomColorToContainer(
                    hero.gameObject,
                    CurrentTeamId
                );
            } catch (Exception exception) {
                Logger.Warn("Could not refresh the local player's team color: " + exception.GetBaseException());
            }
        }

        if (logResult && refreshed > 0) {
            Logger.Info("Refreshed team text and outline colors on " + refreshed + " existing player name(s)");
        }

        return refreshed;
    }


}
