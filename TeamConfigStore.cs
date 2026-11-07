using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace HKMP.MoreTeams;

public sealed class TeamConfigStore {
    public const int FirstBuiltinTeamId = 5;
    public const int LastBuiltinTeamId = 12;
    public const int FirstCustomTeamId = 13;
    public const int LastCustomTeamId = 68;
    public const int MaximumCustomTeams = 56;
    public const int MaximumTeams = 64;
    public const int MaximumListTeams = MaximumTeams + 4;

    private const int LegacyFileSignature = 0x4D545331;
    private const int FileSignature = 0x4D545332;
    private readonly string _filePath;
    private readonly object _sync = new object();
    private List<TeamDefinition> _customTeams = new List<TeamDefinition>();

    public string FilePath => _filePath;

    public TeamConfigStore(string directoryPath) {
        if (string.IsNullOrWhiteSpace(directoryPath)) {
            throw new ArgumentException("A configuration directory is required", nameof(directoryPath));
        }

        _filePath = Path.Combine(directoryPath, "teams.dat");
        Reload();
    }

    public IReadOnlyList<TeamDefinition> GetTeams() {
        lock (_sync) {
            return BuiltinTeams.All.Concat(_customTeams)
                .OrderBy(team => team.Id)
                .Select(team => team.Copy())
                .ToList();
        }
    }

    public IReadOnlyList<TeamDefinition> GetCustomTeams() {
        lock (_sync) {
            return _customTeams.OrderBy(team => team.Id).Select(team => team.Copy()).ToList();
        }
    }

    public bool IsValidTeamId(int id) {
        if (id >= 0 && id <= 4) {
            return true;
        }

        return HasTeam(id);
    }

    public bool HasTeam(int id) {
        if (BuiltinTeams.TryGet(id, out _)) {
            return true;
        }

        lock (_sync) {
            return _customTeams.Any(team => team.Id == id);
        }
    }

    public TeamDefinition FindByName(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        var normalized = name.Trim();
        var builtin = BuiltinTeams.All.FirstOrDefault(team =>
            string.Equals(team.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (builtin != null) {
            return builtin.Copy();
        }

        lock (_sync) {
            var found = _customTeams.FirstOrDefault(team =>
                string.Equals(team.Name, normalized, StringComparison.OrdinalIgnoreCase));
            return found == null ? null : found.Copy();
        }
    }

    public bool TryCreateTeam(string name, string textColor, string outlineColor, bool outlineEnabled, out TeamDefinition created, out string reason) {
        lock (_sync) {
            created = null;
            var normalizedName = NormalizeName(name);
            var normalizedTextColor = NormalizeColor(textColor);
            var normalizedOutlineColor = NormalizeColor(outlineColor);

            if (normalizedName == null) {
                reason = "Team name must contain between 1 and 24 characters";
                return false;
            }

            if (normalizedTextColor == null || normalizedOutlineColor == null) {
                reason = "Both team colors must be valid HEX colors";
                return false;
            }

            if (BuiltinTeams.All.Any(team => string.Equals(team.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                || _customTeams.Any(team => string.Equals(team.Name, normalizedName, StringComparison.OrdinalIgnoreCase))) {
                reason = "A team with this name already exists";
                return false;
            }

            if (_customTeams.Count >= MaximumCustomTeams) {
                reason = "The limit of 64 team entries, including the eight built-in teams, has been reached";
                return false;
            }

            var usedIds = new HashSet<int>(_customTeams.Select(team => (int) team.Id));
            var id = Enumerable.Range(FirstCustomTeamId, MaximumCustomTeams)
                .FirstOrDefault(candidate => !usedIds.Contains(candidate));
            if (id == 0) {
                reason = "No team ID is available";
                return false;
            }

            created = new TeamDefinition {
                Id = (byte) id,
                Name = normalizedName,
                TextColor = normalizedTextColor,
                OutlineColor = normalizedOutlineColor,
                OutlineEnabled = outlineEnabled
            };

            _customTeams.Add(created);
            if (!SaveUnsafe()) {
                var createdId = created.Id;
                _customTeams.RemoveAll(team => team.Id == createdId);
                created = null;
                reason = "Could not save the teams.dat file";
                return false;
            }

            created = created.Copy();
            reason = null;
            return true;
        }
    }

    public TeamDefinition GetTeam(int id) {
        if (BuiltinTeams.TryGet(id, out var builtin)) {
            return builtin;
        }

        lock (_sync) {
            var found = _customTeams.FirstOrDefault(team => team.Id == id);
            return found == null ? null : found.Copy();
        }
    }

    public bool TryDeleteTeam(int id, out TeamDefinition deleted, out string reason) {
        lock (_sync) {
            deleted = null;
            var found = _customTeams.FirstOrDefault(team => team.Id == id);
            if (found == null) {
                reason = "Only custom teams can be deleted";
                return false;
            }

            var previous = _customTeams.Select(team => team.Copy()).ToList();
            _customTeams.RemoveAll(team => team.Id == id);
            if (!SaveUnsafe()) {
                _customTeams = previous;
                reason = "Could not save the teams.dat file";
                return false;
            }

            deleted = found.Copy();
            reason = null;
            return true;
        }
    }

    public bool Reload(out string reason) {
        lock (_sync) {
            try {
                if (!File.Exists(_filePath)) {
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(_filePath), "teams.json"))) {
                        if (!MigrateLegacyJson(out reason)) {
                            return false;
                        }
                    } else {
                        _customTeams = new List<TeamDefinition>();
                        if (!SaveUnsafe()) {
                            reason = "Could not create teams.dat";
                            return false;
                        }
                    }

                    reason = null;
                    return true;
                }

                using (var stream = File.Open(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream)) {
                    var signature = reader.ReadInt32();
                    if (signature != LegacyFileSignature && signature != FileSignature) {
                        reason = "teams.dat has an unsupported format";
                        return false;
                    }

                    var count = reader.ReadByte();
                    if (count > MaximumCustomTeams) {
                        reason = "teams.dat contains too many custom teams";
                        return false;
                    }

                    var loaded = new List<TeamDefinition>();
                    for (var index = 0; index < count; index++) {
                        loaded.Add(new TeamDefinition {
                            Id = reader.ReadByte(),
                            Name = reader.ReadString(),
                            TextColor = reader.ReadString(),
                            OutlineColor = reader.ReadString(),
                            OutlineEnabled = signature == LegacyFileSignature || reader.ReadBoolean()
                        });
                    }

                    _customTeams = Validate(loaded);
                }

                reason = null;
                return true;
            } catch (Exception exception) {
                reason = exception.Message;
                return false;
            }
        }
    }

    public void Reload() {
        if (!Reload(out var reason) && reason != null) {
            System.Diagnostics.Trace.TraceError("HKMP.MoreTeams could not load teams.dat: " + reason);
        }
    }


    private bool MigrateLegacyJson(out string reason) {
        var legacyPath = Path.Combine(Path.GetDirectoryName(_filePath), "teams.json");
        try {
            var legacy = JsonConvert.DeserializeObject<LegacyTeamFile>(File.ReadAllText(legacyPath));
            var migrated = new List<TeamDefinition>();
            var usedNames = new HashSet<string>(BuiltinTeams.All.Select(team => team.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in (legacy?.Teams ?? new List<LegacyTeam>())
                         .Where(team => team != null)
                         .OrderBy(team => team.Id)) {
                var name = NormalizeName(candidate.Name);
                var textColor = NormalizeColor(candidate.Color);
                if (name == null || textColor == null || !usedNames.Add(name)) {
                    continue;
                }

                if (migrated.Count >= MaximumCustomTeams) {
                    break;
                }

                migrated.Add(new TeamDefinition {
                    Id = (byte) (FirstCustomTeamId + migrated.Count),
                    Name = name,
                    TextColor = textColor,
                    OutlineColor = "#000000",
                    OutlineEnabled = true
                });
            }

            var previousTeams = _customTeams;
            _customTeams = migrated;
            if (!SaveUnsafe()) {
                _customTeams = previousTeams;
                reason = "Could not save migrated teams to teams.dat";
                return false;
            }

            var backupPath = legacyPath + ".migrated";
            var suffix = 1;
            while (File.Exists(backupPath)) {
                backupPath = legacyPath + ".migrated." + suffix++;
            }
            try {
                File.Move(legacyPath, backupPath);
            } catch (Exception moveException) {
                System.Diagnostics.Trace.TraceWarning("HKMP.MoreTeams migrated teams.json but could not rename the legacy file: " + moveException);
            }

            reason = null;
            return true;
        } catch (Exception exception) {
            reason = "Could not migrate legacy teams.json: " + exception.Message;
            return false;
        }
    }

    private sealed class LegacyTeamFile {
        [JsonProperty("teams")]
        public List<LegacyTeam> Teams { get; set; }
    }

    private sealed class LegacyTeam {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("color")]
        public string Color { get; set; }
    }

    private List<TeamDefinition> Validate(IEnumerable<TeamDefinition> source) {
        var result = new List<TeamDefinition>();
        var usedIds = new HashSet<byte>();
        var usedNames = new HashSet<string>(BuiltinTeams.All.Select(team => team.Name), StringComparer.OrdinalIgnoreCase);
        if (source == null) {
            return result;
        }

        foreach (var candidate in source) {
            if (candidate == null || candidate.Id < FirstCustomTeamId || candidate.Id > LastCustomTeamId) {
                continue;
            }

            var name = NormalizeName(candidate.Name);
            var textColor = NormalizeColor(candidate.TextColor);
            var outlineColor = NormalizeColor(candidate.OutlineColor);
            if (usedIds.Contains(candidate.Id) || name == null || textColor == null || outlineColor == null
                || usedNames.Contains(name)) {
                continue;
            }

            usedIds.Add(candidate.Id);
            usedNames.Add(name);
            result.Add(new TeamDefinition {
                Id = candidate.Id,
                Name = name,
                TextColor = textColor,
                OutlineColor = outlineColor,
                OutlineEnabled = candidate.OutlineEnabled
            });

            if (result.Count == MaximumCustomTeams) {
                break;
            }
        }

        return result.OrderBy(team => team.Id).ToList();
    }

    private static string NormalizeName(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return null;
        }

        var name = value.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (name.Length == 0 || name.Length > 24) {
            return null;
        }

        return name;
    }

    private static string NormalizeColor(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return null;
        }

        var color = value.Trim();
        if (color.Length != 7 || color[0] != '#') {
            return null;
        }

        for (var index = 1; index < color.Length; index++) {
            var character = color[index];
            if (!(character >= '0' && character <= '9')
                && !(character >= 'a' && character <= 'f')
                && !(character >= 'A' && character <= 'F')) {
                return null;
            }
        }

        return color.ToUpperInvariant();
    }

    private bool SaveUnsafe() {
        try {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
            using (var stream = File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream)) {
                writer.Write(FileSignature);
                writer.Write((byte) _customTeams.Count);
                foreach (var team in _customTeams.OrderBy(candidate => candidate.Id)) {
                    writer.Write(team.Id);
                    writer.Write(team.Name);
                    writer.Write(team.TextColor);
                    writer.Write(team.OutlineColor);
                    writer.Write(team.OutlineEnabled);
                }
            }

            if (File.Exists(_filePath)) {
                File.Replace(temporaryPath, _filePath, null);
            } else {
                File.Move(temporaryPath, _filePath);
            }

            return true;
        } catch (Exception exception) {
            System.Diagnostics.Trace.TraceError("HKMP.MoreTeams could not save teams.dat: " + exception);
            return false;
        }
    }
}
