namespace HKMP.MoreTeams;

public enum AddonPacketId {
    TeamCatalog = 0,
    TeamRequest = 1
}

public enum TeamRequestAction : byte {
    RequestCatalog = 0,
    CreateTeam = 1,
    DeleteTeam = 2,
    SetPlayerCreationEnabled = 3,
    SetCreatorsBecomeLeaders = 4,
    ChangeTeam = 5
}
