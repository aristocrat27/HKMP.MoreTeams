using System;
using System.Collections.Generic;
using System.Linq;

namespace HKMP.MoreTeams;

public static class MoreTeamsBridge {
    public static int[] GetPlayableTeamIds() {
        var ids = new SortedSet<int> { 1, 2, 3, 4 };
        var teams = MoreTeamsServerAddon.HasServerStore
            ? MoreTeamsServerAddon.GetCurrentTeams()
            : MoreTeamsClientAddon.Instance?.GetCurrentTeams() ?? Array.Empty<TeamDefinition>();
        foreach (var team in teams) {
            if (team != null && team.Id >= 5) ids.Add(team.Id);
        }
        return ids.ToArray();
    }

    public static bool IsPlayableTeam(int teamId) {
        if (teamId >= 1 && teamId <= 4) return true;
        if (MoreTeamsServerAddon.HasServerStore) return MoreTeamsServerAddon.HasTeam(teamId);
        var client = MoreTeamsClientAddon.Instance;
        if (client != null) {
            if (!client.HasReceivedCatalog) return teamId >= TeamConfigStore.FirstBuiltinTeamId && teamId <= TeamConfigStore.LastCustomTeamId;
            return client.GetCurrentTeams().Any(team => team.Id == teamId);
        }
        return false;
    }

    public static string GetTeamName(int teamId) {
        var team = MoreTeamsServerAddon.HasServerStore
            ? MoreTeamsServerAddon.GetCurrentTeams().FirstOrDefault(candidate => candidate.Id == teamId)
            : MoreTeamsClientAddon.Instance?.GetCurrentTeams().FirstOrDefault(candidate => candidate.Id == teamId);
        if (team != null) return team.Name;
        switch (teamId) {
            case 1: return "MOSS";
            case 2: return "HIVE";
            case 3: return "GRIMM";
            case 4: return "LIFEBLOOD";
            default: return "NO TEAM";
        }
    }

    public static string GetTeamTextColor(int teamId) {
        var team = MoreTeamsServerAddon.HasServerStore
            ? MoreTeamsServerAddon.GetCurrentTeams().FirstOrDefault(candidate => candidate.Id == teamId)
            : MoreTeamsClientAddon.Instance?.GetCurrentTeams().FirstOrDefault(candidate => candidate.Id == teamId);
        return team?.TextColor ?? string.Empty;
    }

    public static bool CanManageTeams => MoreTeamsClientAddon.Instance?.CanManageTeams ?? false;
    public static bool HasListIntegration => MoreTeamsClientAddon.Instance?.HasListIntegration ?? false;
    public static bool AllowPlayerCreation => MoreTeamsClientAddon.Instance?.AllowPlayerCreation ?? false;
    public static bool CreatorsBecomeLeaders => MoreTeamsClientAddon.Instance?.CreatorsBecomeLeaders ?? false;

    public static void DeleteTeam(byte teamId) {
        MoreTeamsClientAddon.Instance?.DeleteTeam(teamId);
    }

    public static void SetPlayerCreationEnabled(bool enabled) {
        MoreTeamsClientAddon.Instance?.SetPlayerCreationEnabled(enabled);
    }

    public static void SetCreatorsBecomeLeaders(bool enabled) {
        MoreTeamsClientAddon.Instance?.SetCreatorsBecomeLeaders(enabled);
    }
}
