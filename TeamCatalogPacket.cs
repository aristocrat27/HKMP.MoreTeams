using System.Collections.Generic;
using Hkmp.Networking.Packet;

namespace HKMP.MoreTeams;

public sealed class TeamCatalogPacket : IPacketData {
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => true;
    public bool CanManage { get; set; }
    public bool CanCreate { get; set; }
    public bool HasListIntegration { get; set; }
    public bool AllowPlayerCreation { get; set; }
    public bool CreatorsBecomeLeaders { get; set; }
    public byte CurrentTeamId { get; set; }
    public List<TeamDefinition> Teams { get; set; } = new List<TeamDefinition>();

    public void WriteData(IPacket packet) {
        packet.Write(CanManage);
        packet.Write(CanCreate);
        packet.Write(HasListIntegration);
        packet.Write(AllowPlayerCreation);
        packet.Write(CreatorsBecomeLeaders);
        packet.Write(CurrentTeamId);
        var teams = Teams ?? new List<TeamDefinition>();
        var count = teams.Count > TeamConfigStore.MaximumTeams ? TeamConfigStore.MaximumTeams : teams.Count;
        packet.Write((byte)count);

        for (var index = 0; index < count; index++) {
            var team = teams[index];
            packet.Write(team.Id);
            packet.Write(team.Name ?? string.Empty);
            packet.Write(team.TextColor ?? "#FFFFFF");
            packet.Write(team.OutlineColor ?? "#000000");
            packet.Write(team.OutlineEnabled);
        }
    }

    public void ReadData(IPacket packet) {
        CanManage = packet.ReadBool();
        CanCreate = packet.ReadBool();
        HasListIntegration = packet.ReadBool();
        AllowPlayerCreation = packet.ReadBool();
        CreatorsBecomeLeaders = packet.ReadBool();
        CurrentTeamId = packet.ReadByte();
        var count = packet.ReadByte();
        Teams = new List<TeamDefinition>();

        for (var index = 0; index < count; index++) {
            var team = new TeamDefinition {
                Id = packet.ReadByte(),
                Name = packet.ReadString(24),
                TextColor = packet.ReadString(7),
                OutlineColor = packet.ReadString(7),
                OutlineEnabled = packet.ReadBool()
            };

            if (index < TeamConfigStore.MaximumTeams) {
                Teams.Add(team);
            }
        }
    }
}
