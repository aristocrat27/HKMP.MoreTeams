using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Api.Client;
using Hkmp.Api.Server;

namespace HKMP.MoreTeams;

internal static class HkmpRuntimeResolver {
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly HashSet<string> LoadedListAssemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ListAssemblyLoadWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static Type FindType(string fullName, string simpleName) {
        if (fullName != null && fullName.StartsWith("HKMP.List.", StringComparison.Ordinal)) {
            TryLoadListAssembly();
        }

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies) {
            try {
                var exact = assembly.GetType(fullName, false);
                if (exact != null) {
                    return exact;
                }
            } catch {
            }
        }

        foreach (var assembly in assemblies) {
            var match = GetLoadableTypes(assembly).FirstOrDefault(type =>
                string.Equals(type.Name, simpleName, StringComparison.Ordinal));
            if (match != null) {
                return match;
            }
        }

        return null;
    }

    private static void TryLoadListAssembly() {
        try {
            var addonLocation = typeof(HkmpRuntimeResolver).Assembly.Location;
            if (string.IsNullOrWhiteSpace(addonLocation)) {
                return;
            }

            var addonDirectory = Path.GetDirectoryName(addonLocation);
            var modsDirectory = string.IsNullOrWhiteSpace(addonDirectory)
                ? null
                : Directory.GetParent(addonDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory)) {
                return;
            }

            var candidates = new List<string> {
                Path.Combine(modsDirectory, "HKMP.List.dll"),
                Path.Combine(modsDirectory, "HKMP.List", "HKMP.List.dll"),
                Path.Combine(modsDirectory, "HKMP", "HKMP.List.dll")
            };

            foreach (var directory in Directory.GetDirectories(modsDirectory)) {
                candidates.Add(Path.Combine(directory, "HKMP.List.dll"));
            }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
                if (!File.Exists(candidate)) {
                    continue;
                }

                var fullPath = Path.GetFullPath(candidate);
                if (LoadedListAssemblyPaths.Contains(fullPath)) {
                    return;
                }

                try {
                    var existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => {
                        try {
                            return string.Equals(assembly.GetName().Name, "HKMP.List", StringComparison.OrdinalIgnoreCase);
                        } catch {
                            return false;
                        }
                    });
                    if (existing == null) {
                        existing = Assembly.LoadFrom(fullPath);
                    }

                    LoadedListAssemblyPaths.Add(fullPath);
                    Modding.Logger.Log("HKMP.MoreTeams discovered HKMP.List assembly: " + existing.FullName + " from " + fullPath);
                    return;
                } catch (Exception exception) {
                    if (ListAssemblyLoadWarnings.Add(fullPath)) {
                        Modding.Logger.LogWarn("HKMP.MoreTeams could not load HKMP.List from " + fullPath + ": " + exception.GetBaseException());
                    }
                }
            }
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not search for HKMP.List.dll: " + exception.GetBaseException());
        }
    }

    public static MethodInfo FindTeamUpdateMethod(Type serverManagerType) {
        if (serverManagerType == null) {
            return null;
        }

        return GetInstanceMethods(serverManagerType)
            .Where(method => string.Equals(method.Name, "TryUpdatePlayerTeam", StringComparison.Ordinal))
            .FirstOrDefault(IsLegacyTeamUpdateSignature);
    }

    public static MethodInfo FindTeamUpdateHandlerMethod(Type serverManagerType) {
        if (serverManagerType == null) {
            return null;
        }

        return GetInstanceMethods(serverManagerType).FirstOrDefault(method => {
            if (!string.Equals(method.Name, "OnPlayerTeamUpdate", StringComparison.Ordinal)) {
                return false;
            }

            var parameters = method.GetParameters();
            return parameters.Length == 2
                && IsIntegralType(parameters[0].ParameterType)
                && parameters[1].ParameterType.Name.IndexOf("ServerPlayerTeamUpdate", StringComparison.Ordinal) >= 0;
        });
    }

    public static MethodInfo FindTeamNotificationHandlerMethod(Type serverManagerType) {
        if (serverManagerType == null) {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal) {
            "OnPlayerTeamUpdate",
            "OnPlayerSettingUpdate"
        };

        var candidates = GetInstanceMethods(serverManagerType).Where(method => {
            if (!names.Contains(method.Name)) {
                return false;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 2 || !IsIntegralType(parameters[0].ParameterType)) {
                return false;
            }

            var packetName = parameters[1].ParameterType.Name;
            return packetName.IndexOf("TeamUpdate", StringComparison.OrdinalIgnoreCase) >= 0
                || packetName.IndexOf("SettingUpdate", StringComparison.OrdinalIgnoreCase) >= 0;
        }).ToList();

        return candidates.FirstOrDefault(method => string.Equals(method.Name, "OnPlayerTeamUpdate", StringComparison.Ordinal))
            ?? candidates.FirstOrDefault(method => string.Equals(method.Name, "OnPlayerSettingUpdate", StringComparison.Ordinal));
    }

    public static MethodInfo FindTeamUpdatePatchTarget(Type serverManagerType) {
        return FindTeamUpdateMethod(serverManagerType) ?? FindTeamUpdateHandlerMethod(serverManagerType);
    }

    public static MethodInfo FindCommandProcessMethod(Type commandManagerType) {
        if (commandManagerType == null) {
            return null;
        }

        return GetInstanceMethods(commandManagerType).FirstOrDefault(method => {
            if (!string.Equals(method.Name, "ProcessCommand", StringComparison.Ordinal)
                || method.ReturnType != typeof(bool)) {
                return false;
            }

            var parameters = method.GetParameters();
            return parameters.Length == 2
                && string.Equals(parameters[0].ParameterType.Name, "ICommandSender", StringComparison.Ordinal)
                && parameters[1].ParameterType == typeof(string);
        });
    }

    public static object GetMemberValue(object instance, params string[] names) {
        if (instance == null || names == null) {
            return null;
        }

        for (var currentType = instance.GetType(); currentType != null; currentType = currentType.BaseType) {
            foreach (var name in names) {
                try {
                    var property = currentType.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (property != null && property.GetIndexParameters().Length == 0) {
                        return property.GetValue(instance, null);
                    }
                } catch {
                }

                try {
                    var field = currentType.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (field != null) {
                        return field.GetValue(instance);
                    }
                } catch {
                }
            }
        }

        return null;
    }

    public static object GetMemberValueByTypeName(object instance, string typeName) {
        if (instance == null || string.IsNullOrWhiteSpace(typeName)) {
            return null;
        }

        for (var currentType = instance.GetType(); currentType != null; currentType = currentType.BaseType) {
            foreach (var field in currentType.GetFields(InstanceFlags | BindingFlags.DeclaredOnly)) {
                if (string.Equals(field.FieldType.Name, typeName, StringComparison.Ordinal)) {
                    return field.GetValue(instance);
                }
            }

            foreach (var property in currentType.GetProperties(InstanceFlags | BindingFlags.DeclaredOnly)) {
                if (property.GetIndexParameters().Length == 0
                    && string.Equals(property.PropertyType.Name, typeName, StringComparison.Ordinal)) {
                    return property.GetValue(instance, null);
                }
            }
        }

        return null;
    }

    public static void SetMemberValue(object instance, string name, object value) {
        TrySetMemberValue(instance, name, value);
    }

    public static bool TrySetMemberValue(object instance, string name, object value) {
        if (instance == null || string.IsNullOrWhiteSpace(name)) {
            return false;
        }

        for (var currentType = instance.GetType(); currentType != null; currentType = currentType.BaseType) {
            var property = currentType.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite) {
                try {
                    property.SetValue(instance, ConvertValue(value, property.PropertyType), null);
                    return true;
                } catch {
                }
            }

            var field = currentType.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsInitOnly) {
                try {
                    field.SetValue(instance, ConvertValue(value, field.FieldType));
                    return true;
                } catch {
                }
            }
        }

        return false;
    }

    public static bool TryGetTeamUpdateValue(object update, out int teamId) {
        teamId = 0;
        if (update == null) {
            return false;
        }

        var namedValue = GetMemberValue(update, "Team", "PlayerTeam", "NewTeam", "TeamId", "TeamID");
        if (TryConvertTeamValue(namedValue, out teamId)) {
            return true;
        }

        var type = update.GetType();
        for (var currentType = type; currentType != null; currentType = currentType.BaseType) {
            foreach (var property in currentType.GetProperties(InstanceFlags | BindingFlags.DeclaredOnly)) {
                if (property.GetIndexParameters().Length != 0) {
                    continue;
                }

                if (!IsTeamValueName(property.Name) && !IsTeamEnumType(property.PropertyType)) {
                    continue;
                }

                try {
                    if (TryConvertTeamValue(property.GetValue(update, null), out teamId)) {
                        return true;
                    }
                } catch {
                }
            }

            foreach (var field in currentType.GetFields(InstanceFlags | BindingFlags.DeclaredOnly)) {
                if (!IsTeamValueName(field.Name.TrimStart('_')) && !IsTeamEnumType(field.FieldType)) {
                    continue;
                }

                try {
                    if (TryConvertTeamValue(field.GetValue(update), out teamId)) {
                        return true;
                    }
                } catch {
                }
            }
        }

        return false;
    }

    public static int GetPlayerTeam(object serverManager, ushort playerId) {
        var player = GetServerPlayer(serverManager, playerId);
        var playerTeam = GetMemberValue(player, "Team");
        if (TryConvertTeamValue(playerTeam, out var teamId)) {
            return teamId;
        }

        var playerData = GetMemberValue(serverManager, "_playerData") as IDictionary;
        if (playerData != null && playerData.Contains(playerId)) {
            var data = playerData[playerId];
            if (TryConvertTeamValue(GetMemberValue(data, "Team"), out teamId)) {
                return teamId;
            }
        }

        var values = GetMemberValue(serverManager, "_playerData") as IEnumerable;
        if (values != null) {
            foreach (var pair in values) {
                var key = GetMemberValue(pair, "Key");
                if (key == null || Convert.ToUInt16(key) != playerId) {
                    continue;
                }

                var data = GetMemberValue(pair, "Value");
                if (TryConvertTeamValue(GetMemberValue(data, "Team"), out teamId)) {
                    return teamId;
                }
            }
        }

        return -1;
    }

    public static int GetLocalPlayerTeamId(object clientManager) {
        var playerManager = GetMemberValue(clientManager, "_playerManager", "PlayerManager");
        var playerTeam = GetMemberValue(playerManager, "LocalPlayerTeam");
        if (TryConvertTeamValue(playerTeam, out var teamId)) {
            return teamId;
        }

        var clientTeam = GetMemberValue(clientManager, "Team");
        return TryConvertTeamValue(clientTeam, out teamId) ? teamId : 0;
    }

    public static bool TryApplyLocalPlayerTeam(object clientManager, int teamId, out string reason) {
        reason = null;
        var playerManager = GetMemberValue(clientManager, "_playerManager", "PlayerManager");
        if (playerManager == null) {
            reason = "The HKMP client player manager is unavailable";
            return false;
        }

        var updateMethod = GetInstanceMethods(playerManager.GetType()).FirstOrDefault(method => {
            if (!string.Equals(method.Name, "OnPlayerTeamUpdate", StringComparison.Ordinal)) {
                return false;
            }

            var parameters = method.GetParameters();
            return parameters.Length >= 2
                && parameters[0].ParameterType == typeof(bool)
                && parameters[1].ParameterType.IsEnum
                && string.Equals(parameters[1].ParameterType.Name, "Team", StringComparison.Ordinal);
        });

        if (updateMethod != null) {
            try {
                var parameters = updateMethod.GetParameters();
                var arguments = new object[parameters.Length];
                arguments[0] = true;
                arguments[1] = Enum.ToObject(parameters[1].ParameterType, teamId);
                for (var index = 2; index < parameters.Length; index++) {
                    arguments[index] = parameters[index].HasDefaultValue
                        ? parameters[index].DefaultValue
                        : parameters[index].ParameterType.IsValueType
                            ? Activator.CreateInstance(parameters[index].ParameterType)
                            : null;
                }

                updateMethod.Invoke(playerManager, arguments);
                if (GetLocalPlayerTeamId(clientManager) == teamId) {
                    return true;
                }
            } catch (Exception exception) {
                reason = exception.GetBaseException().Message;
            }
        }

        var teamProperty = FindInstanceProperty(playerManager.GetType(), "LocalPlayerTeam");
        if (teamProperty != null && teamProperty.CanWrite) {
            try {
                teamProperty.SetValue(playerManager, ConvertValue(teamId, teamProperty.PropertyType), null);
                if (GetLocalPlayerTeamId(clientManager) == teamId) {
                    return true;
                }
            } catch (Exception exception) {
                reason = exception.GetBaseException().Message;
            }
        }

        if (TrySetMemberValue(playerManager, "LocalPlayerTeam", teamId)
            && GetLocalPlayerTeamId(clientManager) == teamId) {
            return true;
        }

        if (string.IsNullOrWhiteSpace(reason)) {
            reason = "HKMP did not accept the authoritative local team assignment";
        }
        return false;
    }

    private static PropertyInfo FindInstanceProperty(Type type, string name) {
        for (var current = type; current != null; current = current.BaseType) {
            var property = current.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0) {
                return property;
            }
        }
        return null;
    }

    public static object GetServerPlayer(object serverManager, ushort playerId) {
        if (serverManager == null) {
            return null;
        }

        var methods = serverManager.GetType().GetMethods(InstanceFlags);
        var getPlayer = methods.FirstOrDefault(method => {
            var parameters = method.GetParameters();
            return string.Equals(method.Name, "GetPlayer", StringComparison.Ordinal)
                && parameters.Length == 1
                && parameters[0].ParameterType == typeof(ushort);
        });
        if (getPlayer != null) {
            try {
                return getPlayer.Invoke(serverManager, new object[] { playerId });
            } catch {
            }
        }

        var players = GetMemberValue(serverManager, "Players") as IEnumerable;
        if (players != null) {
            foreach (var player in players) {
                var id = GetMemberValue(player, "Id");
                if (id != null && Convert.ToUInt16(id) == playerId) {
                    return player;
                }
            }
        }

        var playerData = GetMemberValue(serverManager, "_playerData") as IDictionary;
        if (playerData != null && playerData.Contains(playerId)) {
            return playerData[playerId];
        }

        return null;
    }

    public static bool TryUpdatePlayerTeam(object serverManager, ushort playerId, int teamId, out string reason) {
        reason = null;
        if (serverManager == null) {
            reason = "The HKMP server manager is unavailable";
            return false;
        }

        var legacyMethod = FindTeamUpdateMethod(serverManager.GetType());
        if (legacyMethod != null) {
            var parameters = legacyMethod.GetParameters();
            var arguments = new object[parameters.Length];
            arguments[0] = ConvertValue(playerId, parameters[0].ParameterType);
            arguments[1] = ConvertValue(teamId, parameters[1].ParameterType);
            if (arguments.Length > 2) {
                arguments[2] = null;
            }

            try {
                var result = Convert.ToBoolean(legacyMethod.Invoke(serverManager, arguments));
                if (arguments.Length > 2) {
                    reason = arguments[2] as string;
                }
                return result;
            } catch (Exception exception) {
                reason = exception.GetBaseException().Message;
                return false;
            }
        }

        var handler = FindTeamUpdateHandlerMethod(serverManager.GetType());
        if (handler == null) {
            reason = "The installed HKMP ServerManager exposes no compatible team-update method";
            return false;
        }

        var targetPlayer = GetServerPlayer(serverManager, playerId);
        if (targetPlayer == null) {
            reason = "Could not find player";
            return false;
        }

        var packet = CreateTeamUpdatePacket(handler.GetParameters()[1].ParameterType, teamId);
        if (packet == null) {
            reason = "Could not construct " + handler.GetParameters()[1].ParameterType.FullName + " for a team change";
            return false;
        }

        try {
            handler.Invoke(serverManager, new[] { ConvertValue(playerId, handler.GetParameters()[0].ParameterType), packet });
            var currentTeam = GetPlayerTeam(serverManager, playerId);
            if (currentTeam < 0 || currentTeam != teamId) {
                reason = "HKMP did not apply or confirm the requested team update";
                return false;
            }
            return true;
        } catch (Exception exception) {
            reason = exception.GetBaseException().Message;
            return false;
        }
    }

    private static IEnumerable<MethodInfo> GetInstanceMethods(Type type) {
        for (var current = type; current != null; current = current.BaseType) {
            foreach (var method in current.GetMethods(InstanceFlags | BindingFlags.DeclaredOnly)) {
                yield return method;
            }
        }
    }

    public static string DescribeMethods(Type type, string nameFragment) {
        if (type == null) {
            return "type unavailable";
        }

        var methods = GetInstanceMethods(type)
            .Where(method => method.Name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(DescribeMethod)
            .ToArray();
        return methods.Length == 0 ? "no matching methods in " + type.FullName : string.Join(" | ", methods);
    }

    public static string DescribeMethod(MethodInfo method) {
        if (method == null) {
            return "method unavailable";
        }

        var parameters = string.Join(", ", method.GetParameters().Select(parameter =>
            parameter.ParameterType.FullName + " " + parameter.Name));
        return method.ReturnType.FullName + " " + method.DeclaringType.FullName + "." + method.Name + "(" + parameters + ")";
    }

    private static object CreateTeamUpdatePacket(Type packetType, int teamId) {
        if (packetType == null) {
            return null;
        }

        object packet = null;
        var assigned = false;
        try {
            packet = Activator.CreateInstance(packetType, true);
        } catch {
        }

        if (packet != null) {
            assigned = TryAssignTeamUpdateMembers(packet, packetType, teamId);
        }

        if (packet == null || !assigned) {
            foreach (var constructor in packetType.GetConstructors(InstanceFlags).OrderBy(item => item.GetParameters().Length)) {
                var parameters = constructor.GetParameters();
                var values = new object[parameters.Length];
                var hasTeamParameter = false;
                for (var index = 0; index < parameters.Length; index++) {
                    var parameter = parameters[index];
                    if (IsTeamValueName(parameter.Name) || IsTeamEnumType(parameter.ParameterType)) {
                        values[index] = ConvertValue(teamId, parameter.ParameterType);
                        hasTeamParameter = true;
                    } else if (parameter.ParameterType == typeof(bool)
                        && parameter.Name != null
                        && (parameter.Name.IndexOf("HasTeam", StringComparison.OrdinalIgnoreCase) >= 0
                            || parameter.Name.IndexOf("TeamUpdated", StringComparison.OrdinalIgnoreCase) >= 0)) {
                        values[index] = true;
                    } else if (parameter.Name != null
                        && (parameter.Name.IndexOf("UpdateType", StringComparison.OrdinalIgnoreCase) >= 0
                            || parameter.Name.IndexOf("UpdateFlags", StringComparison.OrdinalIgnoreCase) >= 0)
                        && GetTeamUpdateFlagValue(parameter.ParameterType, out var teamFlag)) {
                        values[index] = teamFlag;
                    } else if (parameter.HasDefaultValue) {
                        values[index] = parameter.DefaultValue;
                    } else if (!parameter.ParameterType.IsValueType || Nullable.GetUnderlyingType(parameter.ParameterType) != null) {
                        values[index] = null;
                    } else {
                        values[index] = Activator.CreateInstance(parameter.ParameterType);
                    }
                }

                if (!hasTeamParameter) {
                    continue;
                }

                try {
                    packet = constructor.Invoke(values);
                    assigned = TryAssignTeamUpdateMembers(packet, packetType, teamId) || hasTeamParameter;
                    break;
                } catch {
                }
            }
        }

        return assigned ? packet : null;
    }

    private static bool TryAssignTeamUpdateMembers(object packet, Type packetType, int teamId) {
        if (packet == null || packetType == null) {
            return false;
        }

        var assigned = false;
        for (var currentType = packetType; currentType != null; currentType = currentType.BaseType) {
            foreach (var property in currentType.GetProperties(InstanceFlags | BindingFlags.DeclaredOnly)) {
                if (property.GetIndexParameters().Length != 0 || !property.CanWrite) {
                    continue;
                }

                var normalizedName = property.Name.Replace("_", string.Empty);
                if (IsTeamValueName(normalizedName) || IsTeamEnumType(property.PropertyType)) {
                    assigned |= TrySetMemberValue(packet, property.Name, teamId);
                } else if (property.PropertyType == typeof(bool)
                    && (property.Name.IndexOf("HasTeam", StringComparison.OrdinalIgnoreCase) >= 0
                        || property.Name.IndexOf("TeamUpdated", StringComparison.OrdinalIgnoreCase) >= 0)) {
                    TrySetMemberValue(packet, property.Name, true);
                } else if (property.Name.IndexOf("UpdateType", StringComparison.OrdinalIgnoreCase) >= 0
                    || property.Name.IndexOf("UpdateFlags", StringComparison.OrdinalIgnoreCase) >= 0) {
                    TrySetTeamUpdateFlag(packet, property.Name, property.PropertyType);
                }
            }

            foreach (var field in currentType.GetFields(InstanceFlags | BindingFlags.DeclaredOnly)) {
                if (field.IsInitOnly) {
                    continue;
                }

                var normalizedName = field.Name.TrimStart('_').Replace("_", string.Empty);
                if (IsTeamValueName(normalizedName) || IsTeamEnumType(field.FieldType)) {
                    assigned |= TrySetMemberValue(packet, field.Name, teamId);
                } else if (field.FieldType == typeof(bool)
                    && (field.Name.IndexOf("HasTeam", StringComparison.OrdinalIgnoreCase) >= 0
                        || field.Name.IndexOf("TeamUpdated", StringComparison.OrdinalIgnoreCase) >= 0)) {
                    TrySetMemberValue(packet, field.Name, true);
                } else if (field.Name.IndexOf("UpdateType", StringComparison.OrdinalIgnoreCase) >= 0
                    || field.Name.IndexOf("UpdateFlags", StringComparison.OrdinalIgnoreCase) >= 0) {
                    TrySetTeamUpdateFlag(packet, field.Name, field.FieldType);
                }
            }
        }

        return assigned;
    }

    private static void TrySetTeamUpdateFlag(object packet, string name, Type flagType) {
        if (GetTeamUpdateFlagValue(flagType, out var flag)) {
            TrySetMemberValue(packet, name, flag);
        }
    }

    private static bool GetTeamUpdateFlagValue(Type flagType, out object value) {
        value = null;
        if (flagType == null || !flagType.IsEnum || !flagType.IsDefined(typeof(FlagsAttribute), false)) {
            return false;
        }

        try {
            var teamName = Enum.GetNames(flagType).FirstOrDefault(name =>
                name.IndexOf("Team", StringComparison.OrdinalIgnoreCase) >= 0);
            if (teamName == null) {
                return false;
            }

            value = Enum.Parse(flagType, teamName);
            return true;
        } catch {
            return false;
        }
    }

    private static bool IsLegacyTeamUpdateSignature(MethodInfo method) {
        if (method == null || method.ReturnType != typeof(bool)) {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length < 2 || parameters.Length > 3
            || !IsIntegralType(parameters[0].ParameterType)
            || !IsTeamValueType(parameters[1].ParameterType)) {
            return false;
        }

        return parameters.Length == 2
            || parameters[2].ParameterType.IsByRef
            && parameters[2].ParameterType.GetElementType() == typeof(string);
    }

    private static bool IsTeamValueName(string name) {
        return string.Equals(name, "Team", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "PlayerTeam", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "NewTeam", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "TeamId", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "TeamID", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTeamEnumType(Type type) {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        return effectiveType != null
            && effectiveType.IsEnum
            && string.Equals(effectiveType.Name, "Team", StringComparison.Ordinal);
    }

    private static bool IsTeamValueType(Type type) {
        return IsTeamEnumType(type) || IsIntegralType(type);
    }

    private static bool IsIntegralType(Type type) {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        return effectiveType == typeof(byte)
            || effectiveType == typeof(sbyte)
            || effectiveType == typeof(short)
            || effectiveType == typeof(ushort)
            || effectiveType == typeof(int)
            || effectiveType == typeof(uint)
            || effectiveType == typeof(long)
            || effectiveType == typeof(ulong);
    }

    private static bool TryConvertTeamValue(object value, out int teamId) {
        teamId = 0;
        if (value == null) {
            return false;
        }

        try {
            teamId = Convert.ToInt32(value);
            return true;
        } catch {
            return false;
        }
    }

    private static object ConvertValue(object value, Type targetType) {
        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveType.IsEnum) {
            return Enum.ToObject(effectiveType, value);
        }

        if (effectiveType.IsInstanceOfType(value)) {
            return value;
        }

        return Convert.ChangeType(value, effectiveType);
    }

    private static IEnumerable<FieldInfo> GetAllInstanceFields(Type type) {
        for (var current = type; current != null; current = current.BaseType) {
            foreach (var field in current.GetFields(InstanceFlags | BindingFlags.DeclaredOnly)) {
                yield return field;
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly) {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException exception) {
            return exception.Types.Where(type => type != null);
        } catch {
            return Array.Empty<Type>();
        }
    }
}
