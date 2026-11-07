using Hkmp.Api.Server;
using Hkmp.Api.Server.Networking;
using Hkmp.Game;
using Hkmp.Networking.Packet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HKMP.MoreTeams;

public sealed class MoreTeamsServerAddon : ServerAddon {
    private static MoreTeamsServerAddon _instance;

    private IServerApi _api;
    private IServerAddonNetworkSender<AddonPacketId> _sender;
    private IServerAddonNetworkReceiver<AddonPacketId> _receiver;
    private TeamConfigStore _store;
    private TeamLeaderStore _leaderStore;
    private bool _allowPlayerCreation;
    private bool _creatorsBecomeLeaders;

    protected override string Name => "HKMP.MoreTeams";
    protected override string Version => "1.0.0.0";
    public override bool NeedsNetwork => true;

    private bool MovePlayersOutOfTeam(int teamId, out List<IServerPlayer> movedPlayers) {
        movedPlayers = new List<IServerPlayer>();
        if (teamId < TeamConfigStore.FirstCustomTeamId || teamId > TeamConfigStore.LastCustomTeamId) {
            throw new ArgumentOutOfRangeException(nameof(teamId));
        }

        if (_api?.ServerManager == null) {
            Logger.Warn("Could not move players out of a team because the HKMP server manager is unavailable");
            return false;
        }

        var players = _api.ServerManager.Players == null
            ? new List<IServerPlayer>()
            : _api.ServerManager.Players
                .Where(player => player != null && Convert.ToInt32(player.Team) == teamId)
                .ToList();
        var allPlayersMoved = true;

        foreach (var player in players) {
            if (!TryMovePlayerToTeam(player, (byte) Team.None, true)) {
                allPlayersMoved = false;
                Logger.Warn("Could not move player " + player.Username + " (" + player.Id + ") out of team " + teamId);
                continue;
            }

            movedPlayers.Add(player);
            if (!_api.ServerManager.TryGetPlayer(player.Id, out var updatedPlayer)
                || updatedPlayer == null
                || Convert.ToInt32(updatedPlayer.Team) != (int) Team.None) {
                allPlayersMoved = false;
                Logger.Warn("HKMP did not confirm that player " + player.Username + " (" + player.Id + ") left team " + teamId);
            }
        }

        return allPlayersMoved;
    }

    public bool ReloadConfiguration(out string reason)
    {
        if (!_store.Reload(out reason))
        {
            return false;
        }

        BroadcastCatalog();
        Logger.Info("Reloaded additional team configuration from " + _store.FilePath);
        return true;
    }
    public override void Initialize(IServerApi serverApi) {
        _instance = this;
        _api = serverApi ?? throw new ArgumentNullException(nameof(serverApi));
        var directory = Path.GetDirectoryName(typeof(MoreTeamsServerAddon).Assembly.Location);
        _store = new TeamConfigStore(directory);
        _leaderStore = new TeamLeaderStore(directory);
        foreach (var leader in _leaderStore.GetAll()) {
            if (!_store.HasTeam(leader.TeamId)) {
                _leaderStore.Remove(leader.TeamId);
            }
        }
        _allowPlayerCreation = HKMPMoreTeams.GlobalSettings?.AllowPlayerTeamCreation ?? false;
        _creatorsBecomeLeaders = HKMPMoreTeams.GlobalSettings?.CreatorsBecomeTeamLeaders ?? false;

        Logger.Info("HKMP.MoreTeams team changes use validated addon packets and the HKMP server manager API; no Harmony patches are installed");

        try {
            ListRuntimeIntegration.InstallServer(this);
            Logger.Info(ListRuntimeIntegration.IsAvailable
                ? "HKMP.MoreTeams HKMP.List server integration installed"
                : "HKMP.MoreTeams HKMP.List server integration is waiting: " + ListRuntimeIntegration.GetAvailabilityDetails());
        } catch (Exception exception) {
            Logger.Warn("HKMP.MoreTeams could not install the HKMP.List server integration: " + exception);
        }

        _sender = _api.NetServer.GetNetworkSender<AddonPacketId>(this);
        _receiver = _api.NetServer.GetNetworkReceiver<AddonPacketId>(this, CreatePacket);
        _receiver.RegisterPacketHandler<TeamRequestPacket>(AddonPacketId.TeamRequest, HandleTeamRequest);
        _api.ServerManager.PlayerConnectEvent += HandlePlayerConnect;
        _api.CommandManager.RegisterCommand(new MoreTeamsCommand(this));
        var addonAssembly = typeof(MoreTeamsServerAddon).Assembly;
        Logger.Info("HKMP.MoreTeams server addon initialized; version 1.0.0.0; assembly: " + addonAssembly.FullName + "; path: " + addonAssembly.Location + "; manager type: " + _api.ServerManager.GetType().AssemblyQualifiedName + "; persistent team data: " + _store.FilePath);
    }

    internal static bool HasServerStore => _instance?._store != null;

    internal static IReadOnlyList<TeamDefinition> GetCurrentTeams() {
        return _instance?._store?.GetTeams() ?? BuiltinTeams.All;
    }

    internal static bool HasTeam(int id) {
        return _instance?._store?.HasTeam(id) ?? BuiltinTeams.TryGet(id, out _);
    }

    internal static string GetTeamName(int id) {
        var team = _instance?._store?.GetTeam(id);
        if (team != null) {
            return team.Name;
        }

        switch (id) {
            case 1: return "Moss";
            case 2: return "Hive";
            case 3: return "Grimm";
            case 4: return "Lifeblood";
            case 0: return "None";
            default: return id.ToString();
        }
    }

    internal IReadOnlyList<TeamLeaderRecord> GetStoredLeaders() {
        return _leaderStore?.GetAll() ?? new List<TeamLeaderRecord>();
    }

    internal void RecordTeamLeader(byte teamId, string fingerprint, string username) {
        _leaderStore?.Set(teamId, fingerprint, username);
    }

    internal void RemoveStoredLeader(byte teamId) {
        _leaderStore?.Remove(teamId);
    }

    internal static void ApplyLocalCreationSetting(bool enabled) {
        HKMPMoreTeams.GlobalSettings.AllowPlayerTeamCreation = enabled;
        _instance?.SetPlayerCreationEnabled(enabled, true);
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

    private void HandlePlayerConnect(IServerPlayer player) {
        if (player == null) {
            return;
        }

        TryInstallListIntegration();
        if (ListRuntimeIntegration.IsAvailable) {
            ListRuntimeIntegration.RehydrateSavedLeaders();
        }
        SendCatalog(player);
    }

    private void HandleTeamRequest(ushort playerId, TeamRequestPacket packet) {
        TryInstallListIntegration();
        if (packet == null || !_api.ServerManager.TryGetPlayer(playerId, out var player)) {
            return;
        }

        switch (packet.Action) {
            case TeamRequestAction.RequestCatalog:
                SendCatalog(player);
                return;
            case TeamRequestAction.ChangeTeam:
                HandleTeamChange(player, packet.TeamId);
                return;
            case TeamRequestAction.CreateTeam:
                HandleCreateTeam(player, packet);
                return;
            case TeamRequestAction.DeleteTeam:
                HandleDeleteTeam(player, packet.TeamId);
                return;
            case TeamRequestAction.SetPlayerCreationEnabled:
                if (!ListRuntimeIntegration.IsAvailable || !RequireTeamManagement(player)) {
                    return;
                }
                SetPlayerCreationEnabled(packet.Enabled, true);
                return;
            case TeamRequestAction.SetCreatorsBecomeLeaders:
                if (!RequireTeamManagement(player) || !ListRuntimeIntegration.IsAvailable) {
                    return;
                }
                SetCreatorsBecomeLeaders(packet.Enabled, true);
                return;
        }
    }


    private void HandleTeamChange(IServerPlayer player, byte teamId) {
        if (player == null) {
            return;
        }

        var team = _store.GetTeam(teamId);
        if (team == null || !_store.IsValidTeamId(teamId) || teamId < TeamConfigStore.FirstBuiltinTeamId) {
            _api.ServerManager.SendMessage(player.Id, "The selected additional team does not exist");
            SendCatalog(player);
            return;
        }

        var previousTeamId = Convert.ToInt32(player.Team);
        if (previousTeamId == teamId) {
            SendCatalog(player);
            return;
        }

        if (ListRuntimeIntegration.AreTeamChangesLocked) {
            _api.ServerManager.SendMessage(player.Id, "Team changes are locked by the host in HKMP.List");
            SendCatalog(player);
            return;
        }

        var settings = _api.ServerManager.ServerSettings;
        if (settings == null || !settings.TeamsEnabled) {
            _api.ServerManager.SendMessage(player.Id, "Unable to change team because teams are disabled by the host");
            SendCatalog(player);
            return;
        }

        if (!TryMovePlayerToTeam(player, teamId)) {
            _api.ServerManager.SendMessage(player.Id, "HKMP did not confirm the requested team change");
            SendCatalog(player);
            return;
        }

        if (!_api.ServerManager.TryGetPlayer(player.Id, out var confirmedPlayer)
            || confirmedPlayer == null
            || Convert.ToInt32(confirmedPlayer.Team) != teamId) {
            _api.ServerManager.SendMessage(player.Id, "The server could not confirm the selected team");
            SendCatalog(player);
            return;
        }

        SendTeamChangedMessage(confirmedPlayer, team.Name);
        SendCatalog(confirmedPlayer);
        Logger.Info("Changed the single active team for " + player.Username + " (ID " + player.Id + ") from " + previousTeamId + " to " + team.Name + " (ID " + teamId + ")");
    }

    private void HandleCreateTeam(IServerPlayer player, TeamRequestPacket packet) {
        var canManage = ListRuntimeIntegration.CanManageTeams(player);
        if (!canManage && !_allowPlayerCreation) {
            _api.ServerManager.SendMessage(player.Id, "Players are not allowed to create additional teams");
            SendCatalog(player);
            return;
        }

        if (!_store.TryCreateTeam(packet.Name, packet.TextColor, packet.OutlineColor, packet.OutlineEnabled, out var created, out var reason)) {
            _api.ServerManager.SendMessage(player.Id, reason ?? "Could not create an additional team");
            SendCatalog(player);
            return;
        }

        if (ListRuntimeIntegration.IsAvailable && _creatorsBecomeLeaders) {
            var fingerprint = ListRuntimeIntegration.GetAuthorizationFingerprint(player);
            if (string.IsNullOrWhiteSpace(fingerprint) || !_leaderStore.Set(created.Id, fingerprint, player.Username)) {
                _api.ServerManager.SendMessage(player.Id, "The team was created, but you could not be saved as its leader");
            } else if (!ListRuntimeIntegration.AssignLeader(created.Id, fingerprint, player.Username)) {
                _leaderStore.Remove(created.Id);
                _api.ServerManager.SendMessage(player.Id, "The team was created, but HKMP.List could not appoint you as its leader");
            } else if (!TryMovePlayerToTeam(player, created.Id, true)) {
                ListRuntimeIntegration.RemoveLeader(created.Id, fingerprint);
                _leaderStore.Remove(created.Id);
                _api.ServerManager.SendMessage(player.Id, "The team was created, but you could not be moved into it as its leader");
            } else {
                SendTeamChangedMessage(player, created.Name);
            }
        }

        Logger.Info("Created additional team " + created.Id + " (" + created.Name + ") by " + player.Username);
        _api.ServerManager.SendMessage(player.Id, "Team created: '" + created.Name + "'");
        BroadcastCatalog();
    }

    private void HandleDeleteTeam(IServerPlayer player, byte teamId) {
        if (player == null || !RequireTeamManagement(player)) {
            return;
        }

        var team = _store.GetTeam(teamId);
        if (team == null || teamId < TeamConfigStore.FirstCustomTeamId) {
            _api.ServerManager.SendMessage(player.Id, "Only custom teams can be deleted");
            SendCatalog(player);
            return;
        }

        if (!MovePlayersOutOfTeam(teamId, out var movedPlayers)) {
            RestorePlayersToTeam(movedPlayers, teamId);
            _api.ServerManager.SendMessage(player.Id, "Could not move every player out of the team. The team was not deleted");
            SendCatalog(player);
            return;
        }

        if (!ListRuntimeIntegration.ClearTeamSettings(teamId)) {
            RestorePlayersToTeam(movedPlayers, teamId);
            _api.ServerManager.SendMessage(player.Id, "Could not clear the team's HKMP.List settings. The team was not deleted");
            SendCatalog(player);
            return;
        }

        if (!_store.TryDeleteTeam(teamId, out var deleted, out var reason)) {
            RestorePlayersToTeam(movedPlayers, teamId);
            ListRuntimeIntegration.RehydrateSavedLeaders();
            _api.ServerManager.SendMessage(player.Id, reason ?? "Could not delete the team");
            SendCatalog(player);
            return;
        }

        _leaderStore.Remove(teamId);
        foreach (var movedPlayer in movedPlayers) {
            SendTeamChangedMessage(movedPlayer, "None");
        }
        Logger.Info("Deleted custom team " + deleted.Id + " (" + deleted.Name + ") by " + player.Username);
        _api.ServerManager.BroadcastMessage("Team deleted: '" + deleted.Name + "'");
        BroadcastCatalog();
    }

    private void SendTeamChangedMessage(IServerPlayer player, string teamName) {
        if (player == null || _api?.ServerManager == null) {
            return;
        }

        var message = "Team changed to '" + (teamName ?? "None") + "'";
        try {
            _api.ServerManager.SendMessage(player, message);
            Logger.Info("Sent HKMP system team-change notification to " + player.Username + " (ID " + player.Id + "): " + message);
        } catch (Exception exception) {
            Logger.Warn("Could not send HKMP system team-change notification through the player API: " + exception.GetBaseException());
            try {
                _api.ServerManager.SendMessage(player.Id, message);
                Logger.Info("Sent HKMP system team-change notification through the player-ID API to " + player.Username + " (ID " + player.Id + ")");
            } catch (Exception fallbackException) {
                Logger.Warn("Could not send the fallback HKMP system team-change notification: " + fallbackException.GetBaseException());
            }
        }
    }

    private bool TryMovePlayerToTeam(IServerPlayer player, byte teamId, bool updateListLockState = false) {
        if (player == null) {
            return false;
        }

        var previousTeamId = Convert.ToByte(player.Team);
        if (updateListLockState && !ListRuntimeIntegration.PrepareLockedTeamAssignment(player, teamId)) {
            Logger.Warn("Could not update the HKMP.List team-lock state for " + player.Username + " before moving them to team " + teamId);
            return false;
        }

        var settings = HkmpRuntimeResolver.GetMemberValue(_api.ServerManager, "InternalServerSettings", "ServerSettings");
        var originalTeamsEnabledValue = HkmpRuntimeResolver.GetMemberValue(settings, "TeamsEnabled");
        var originalTeamsEnabled = originalTeamsEnabledValue is bool enabled ? (bool?)enabled : null;
        if (originalTeamsEnabled.HasValue && !originalTeamsEnabled.Value) {
            HkmpRuntimeResolver.SetMemberValue(settings, "TeamsEnabled", true);
        }

        try {
            var updated = HkmpRuntimeResolver.TryUpdatePlayerTeam(_api.ServerManager, player.Id, teamId, out var reason);
            if (!updated) {
                if (updateListLockState) {
                    ListRuntimeIntegration.PrepareLockedTeamAssignment(player, previousTeamId);
                }
                Logger.Warn("Could not move player " + player.Username + " into team " + teamId + ": " + (reason ?? "unknown HKMP update failure"));
                return false;
            }

            if (!_api.ServerManager.TryGetPlayer(player.Id, out var updatedPlayer)
                || updatedPlayer == null
                || Convert.ToInt32(updatedPlayer.Team) != teamId) {
                if (updateListLockState) {
                    ListRuntimeIntegration.PrepareLockedTeamAssignment(player, previousTeamId);
                }
                Logger.Warn("HKMP did not confirm team " + teamId + " for player " + player.Username);
                return false;
            }

            return true;
        } finally {
            if (originalTeamsEnabled.HasValue && !originalTeamsEnabled.Value) {
                HkmpRuntimeResolver.SetMemberValue(settings, "TeamsEnabled", false);
            }
        }
    }
    private void RestorePlayersToTeam(IEnumerable<IServerPlayer> players, byte teamId) {
        if (players == null) {
            return;
        }

        foreach (var player in players) {
            if (player != null && !TryMovePlayerToTeam(player, teamId, true)) {
                Logger.Warn("Could not restore player " + player.Username + " to team " + teamId);
            }
        }
    }

    private bool RequireTeamManagement(IServerPlayer player) {
        if (ListRuntimeIntegration.CanManageTeams(player)) {
            return true;
        }

        _api.ServerManager.SendMessage(player.Id, "Only the host or an administrator with team-management permission can manage additional teams");
        SendCatalog(player);
        return false;
    }

    private void TryInstallListIntegration() {
        try {
            ListRuntimeIntegration.InstallServer(this);
        } catch (Exception exception) {
            Logger.Warn("HKMP.MoreTeams HKMP.List server integration is unavailable: " + exception.GetBaseException());
        }
    }

    private void SetPlayerCreationEnabled(bool enabled, bool broadcast) {
        _allowPlayerCreation = enabled;
        HKMPMoreTeams.GlobalSettings.AllowPlayerTeamCreation = enabled;
        if (broadcast) {
            BroadcastCatalog();
        }
    }

    private void SetCreatorsBecomeLeaders(bool enabled, bool broadcast) {
        _creatorsBecomeLeaders = enabled;
        HKMPMoreTeams.GlobalSettings.CreatorsBecomeTeamLeaders = enabled;
        if (broadcast) {
            BroadcastCatalog();
        }
    }

    private void SendCatalog(IServerPlayer player) {
        TryInstallListIntegration();
        var canManage = ListRuntimeIntegration.CanManageTeams(player);
        _sender.SendSingleData(
            AddonPacketId.TeamCatalog,
            new TeamCatalogPacket {
                CanManage = canManage,
                CanCreate = canManage || _allowPlayerCreation,
                HasListIntegration = ListRuntimeIntegration.IsAvailable,
                AllowPlayerCreation = _allowPlayerCreation,
                CreatorsBecomeLeaders = ListRuntimeIntegration.IsAvailable && _creatorsBecomeLeaders,
                CurrentTeamId = Convert.ToByte(player.Team),
                Teams = new List<TeamDefinition>(_store.GetTeams())
            },
            player.Id
        );
    }

    internal void RefreshCatalogForConnectedPlayers() {
        BroadcastCatalog();
    }

    private void BroadcastCatalog() {
        if (_api == null || _sender == null || _api.ServerManager.Players == null) {
            return;
        }

        foreach (var player in _api.ServerManager.Players) {
            if (player != null) {
                SendCatalog(player);
            }
        }
    }

}
