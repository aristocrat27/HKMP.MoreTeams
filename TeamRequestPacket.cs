using Hkmp.Networking.Packet;

namespace HKMP.MoreTeams;

public sealed class TeamRequestPacket : IPacketData {
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => true;
    public TeamRequestAction Action { get; set; }
    public string Name { get; set; }
    public string TextColor { get; set; }
    public string OutlineColor { get; set; }
    public bool OutlineEnabled { get; set; } = true;
    public byte TeamId { get; set; }
    public bool Enabled { get; set; }

    public void WriteData(IPacket packet) {
        packet.Write((byte)Action);
        switch (Action) {
            case TeamRequestAction.CreateTeam:
                packet.Write(Name ?? string.Empty);
                packet.Write(TextColor ?? "#FFFFFF");
                packet.Write(OutlineColor ?? "#000000");
                packet.Write(OutlineEnabled);
                break;
            case TeamRequestAction.DeleteTeam:
            case TeamRequestAction.ChangeTeam:
                packet.Write(TeamId);
                break;
            case TeamRequestAction.SetPlayerCreationEnabled:
            case TeamRequestAction.SetCreatorsBecomeLeaders:
                packet.Write(Enabled);
                break;
        }
    }

    public void ReadData(IPacket packet) {
        Action = (TeamRequestAction)packet.ReadByte();
        Name = string.Empty;
        TextColor = "#FFFFFF";
        OutlineColor = "#000000";
        OutlineEnabled = true;
        TeamId = 0;
        Enabled = false;

        switch (Action) {
            case TeamRequestAction.CreateTeam:
                Name = packet.ReadString(24);
                TextColor = packet.ReadString(7);
                OutlineColor = packet.ReadString(7);
                OutlineEnabled = packet.ReadBool();
                break;
            case TeamRequestAction.DeleteTeam:
            case TeamRequestAction.ChangeTeam:
                TeamId = packet.ReadByte();
                break;
            case TeamRequestAction.SetPlayerCreationEnabled:
            case TeamRequestAction.SetCreatorsBecomeLeaders:
                Enabled = packet.ReadBool();
                break;
        }
    }
}
