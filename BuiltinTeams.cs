using System.Collections.Generic;
using System.Linq;

namespace HKMP.MoreTeams;

public static class BuiltinTeams {
    private static readonly List<TeamDefinition> Definitions = new List<TeamDefinition> {
        new TeamDefinition { Id = 5, Name = "Moth", TextColor = "#E1B8E0", OutlineColor = "#4A235A", OutlineEnabled = true },
        new TeamDefinition { Id = 6, Name = "Abyss", TextColor = "#000000", OutlineColor = "#FFFFFF", OutlineEnabled = true },
        new TeamDefinition { Id = 7, Name = "Soul", TextColor = "#FFFFFF", OutlineColor = "#E7C547", OutlineEnabled = true },
        new TeamDefinition { Id = 8, Name = "Husk", TextColor = "#455C7B", OutlineColor = "#091322", OutlineEnabled = true },
        new TeamDefinition { Id = 9, Name = "Crystal", TextColor = "#8A428B", OutlineEnabled = false },
        new TeamDefinition { Id = 10, Name = "Mantis", TextColor = "#79533A", OutlineColor = "#332216", OutlineEnabled = true },
        new TeamDefinition { Id = 11, Name = "Fluke", TextColor = "#E98AB9", OutlineEnabled = false },
        new TeamDefinition { Id = 12, Name = "Fool", TextColor = "#70434D",  OutlineEnabled = false }
    };

    public static IReadOnlyList<TeamDefinition> All => Definitions.Select(team => team.Copy()).ToList();

    public static bool TryGet(int id, out TeamDefinition team) {
        var found = Definitions.FirstOrDefault(candidate => candidate.Id == id);
        team = found == null ? null : found.Copy();
        return team != null;
    }
}
