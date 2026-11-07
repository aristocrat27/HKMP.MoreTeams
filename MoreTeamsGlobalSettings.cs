using System;

namespace HKMP.MoreTeams;

[Serializable]
public sealed class MoreTeamsGlobalSettings {
    public bool AllowPlayerTeamCreation { get; set; }
    public bool CreatorsBecomeTeamLeaders { get; set; }
}
