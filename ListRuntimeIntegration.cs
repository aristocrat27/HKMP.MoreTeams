using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HKMP.MoreTeams;

internal static class ListRuntimeIntegration {
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static MoreTeamsClientAddon _clientAddon;
    private static MoreTeamsServerAddon _serverAddon;
    private static object _listServerAddon;
    private static object _listTeamStore;
    private static GameObject _retryRunnerObject;

    public static bool IsInstalled => HkmpRuntimeResolver.FindType("HKMP.List.ListMod", "ListMod") != null;

    public static bool IsAvailable => HasListModInstance() &&
        HkmpRuntimeResolver.FindType("HKMP.List.Shared.TeamSettingsData", "TeamSettingsData") != null &&
        HkmpRuntimeResolver.FindType("HKMP.List.Server.ListHostTeamStore", "ListHostTeamStore") != null &&
        HkmpRuntimeResolver.FindType("HKMP.List.Server.ListServerAddon", "ListServerAddon") != null &&
        HkmpRuntimeResolver.FindType("HKMP.List.Shared.ListMoreTeamsBridge", "ListMoreTeamsBridge") != null;

    public static string GetAvailabilityDetails() {
        var missing = new List<string>();
        if (HkmpRuntimeResolver.FindType("HKMP.List.Shared.TeamSettingsData", "TeamSettingsData") == null) missing.Add("TeamSettingsData");
        if (HkmpRuntimeResolver.FindType("HKMP.List.Server.ListHostTeamStore", "ListHostTeamStore") == null) missing.Add("ListHostTeamStore");
        if (HkmpRuntimeResolver.FindType("HKMP.List.Server.ListServerAddon", "ListServerAddon") == null) missing.Add("ListServerAddon");
        if (HkmpRuntimeResolver.FindType("HKMP.List.Shared.ListMoreTeamsBridge", "ListMoreTeamsBridge") == null) missing.Add("ListMoreTeamsBridge (apply the included HKMP.List patch and build version 1.1.0.0)");
        if (missing.Count > 0) return "HKMP.List integration is not enabled: " + string.Join(", ", missing);
        return HasListModInstance() ? "HKMP.List 1.1.0.0 bridge loaded; Harmony-free integration active" : "HKMP.List types loaded but ListMod.Instance is not initialized";
    }

    private static bool HasListModInstance() {
        var type = HkmpRuntimeResolver.FindType("HKMP.List.ListMod", "ListMod");
        if (type == null) return false;
        try {
            var property = type.GetProperty("Instance", StaticFlags);
            if (property != null) return property.GetValue(null, null) != null;
            var field = type.GetField("Instance", StaticFlags);
            return field != null && field.GetValue(null) != null;
        } catch {
            return false;
        }
    }

    public static void InstallClient(MoreTeamsClientAddon addon) {
        _clientAddon = addon;
        EnsureRetryRunner();
        FindRegisteredListAddon();
    }

    public static void InstallServer(MoreTeamsServerAddon addon) {
        _serverAddon = addon;
        EnsureRetryRunner();
        FindRegisteredListAddon();
    }

    private static void EnsureRetryRunner() {
        if (_retryRunnerObject != null) return;
        _retryRunnerObject = new GameObject("HKMP.MoreTeams.ListBridgeRetry");
        UnityEngine.Object.DontDestroyOnLoad(_retryRunnerObject);
        _retryRunnerObject.AddComponent<ListBridgeRetryRunner>();
    }

    internal static void RetryPendingIntegrations() {
        FindRegisteredListAddon();
    }

    public static bool CanManageTeams(object player) {
        if (player == null) return false;
        if (!IsAvailable) {
            if (ReadBoolean(player, "IsAuthorized")) return true;
            var hostUsername = MoreTeamsClientAddon.LocalClientUsername;
            var playerUsername = HkmpRuntimeResolver.GetMemberValue(player, "Username") as string;
            return !string.IsNullOrWhiteSpace(hostUsername)
                && string.Equals(hostUsername, playerUsername, StringComparison.OrdinalIgnoreCase);
        }
        FindRegisteredListAddon();
        if (_listServerAddon == null) return false;
        try {
            var hostMethod = FindMethod(_listServerAddon.GetType(), "IsLocalHostPlayer", 1);
            if (hostMethod != null && Convert.ToBoolean(hostMethod.Invoke(hostMethod.IsStatic ? null : _listServerAddon, new[] { player }))) return true;
            var permissionsMethod = FindMethod(_listServerAddon.GetType(), "GetPermissionsForPlayer", 1);
            if (permissionsMethod == null) return false;
            var permissions = permissionsMethod.Invoke(_listServerAddon, new[] { player });
            return ReadBoolean(permissions, "CanManageTeamLeaders");
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not read HKMP.List team-management permission: " + exception.GetBaseException());
            return false;
        }
    }

    public static string GetAuthorizationFingerprint(object player) {
        if (player == null) return string.Empty;
        var type = HkmpRuntimeResolver.FindType("HKMP.List.Server.ListHostNotesStore", "ListHostNotesStore");
        var method = type == null ? null : type.GetMethod("GetAuthorizationFingerprint", StaticFlags);
        if (method == null) return string.Empty;
        try { return method.Invoke(null, new[] { player }) as string ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static bool AssignLeader(byte teamId, string fingerprint, string username) {
        FindRegisteredListAddon();
        if (_listTeamStore == null || teamId < TeamConfigStore.FirstCustomTeamId || teamId > TeamConfigStore.LastCustomTeamId) return false;
        try {
            var method = FindMethod(_listTeamStore.GetType(), "TrySetLeader", 3);
            if (method == null) return false;
            var parameterType = method.GetParameters()[0].ParameterType;
            var team = parameterType.IsEnum ? Enum.ToObject(parameterType, teamId) : Convert.ChangeType(teamId, parameterType);
            var saved = Convert.ToBoolean(method.Invoke(_listTeamStore, new[] { team, fingerprint, username }));
            if (saved) SynchronizeListServer();
            return saved;
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not assign a team leader in HKMP.List: " + exception.GetBaseException());
            return false;
        }
    }

    public static bool RemoveLeader(byte teamId, string fingerprint) {
        FindRegisteredListAddon();
        if (_listTeamStore == null || teamId < TeamConfigStore.FirstCustomTeamId || teamId > TeamConfigStore.LastCustomTeamId) return false;
        try {
            var method = FindMethod(_listTeamStore.GetType(), "TryRemoveLeader", 2);
            if (method == null) return false;
            var parameterType = method.GetParameters()[0].ParameterType;
            var team = parameterType.IsEnum ? Enum.ToObject(parameterType, teamId) : Convert.ChangeType(teamId, parameterType);
            var removed = Convert.ToBoolean(method.Invoke(_listTeamStore, new[] { team, fingerprint }));
            if (removed) SynchronizeListServer();
            return removed;
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not remove a team leader from HKMP.List: " + exception.GetBaseException());
            return false;
        }
    }

    public static bool AreTeamChangesLocked {
        get {
            if (!IsAvailable) return false;
            FindRegisteredListAddon();
            return ReadBoolean(_listTeamStore, "TeamChangesLocked");
        }
    }

    public static bool PrepareLockedTeamAssignment(object player, byte teamId) {
        if (player == null) return false;
        if (!IsAvailable) return true;
        FindRegisteredListAddon();
        if (_listTeamStore == null) return false;
        if (!ReadBoolean(_listTeamStore, "TeamChangesLocked")) return true;
        if (_listServerAddon == null) return false;

        try {
            var method = FindMethod(_listServerAddon.GetType(), "TrySetMoreTeamsLockedTeam", 2);
            if (method == null) return false;
            var teamParameter = method.GetParameters()[1].ParameterType;
            var teamValue = teamParameter.IsEnum
                ? Enum.ToObject(teamParameter, teamId)
                : Convert.ChangeType(teamId, teamParameter);
            return Convert.ToBoolean(method.Invoke(_listServerAddon, new[] { player, teamValue }));
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not update HKMP.List's locked team assignment: " + exception.GetBaseException());
            return false;
        }
    }

    public static bool ClearTeamSettings(byte teamId) {
        FindRegisteredListAddon();
        if (_listTeamStore == null) return !IsAvailable;
        try {
            var gate = HkmpRuntimeResolver.GetMemberValue(_listTeamStore, "_gate");
            var records = HkmpRuntimeResolver.GetMemberValue(_listTeamStore, "_records") as IDictionary;
            var save = FindMethod(_listTeamStore.GetType(), "SaveLocked", 0);
            if (gate == null || records == null || save == null) return false;
            var keyType = records.GetType().GetGenericArguments()[0];
            var key = keyType.IsEnum ? Enum.ToObject(keyType, teamId) : Convert.ChangeType(teamId, keyType);
            lock (gate) {
                if (!records.Contains(key)) return true;
                var previous = records[key];
                records.Remove(key);
                if (Convert.ToBoolean(save.Invoke(_listTeamStore, null))) {
                    SynchronizeListServer();
                    return true;
                }
                records[key] = previous;
                return false;
            }
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not clear the deleted team's HKMP.List settings: " + exception.GetBaseException());
            return false;
        }
    }

    public static void RehydrateSavedLeaders() {
        if (_serverAddon == null) return;
        FindRegisteredListAddon();
        if (_listTeamStore == null) return;
        foreach (var leader in _serverAddon.GetStoredLeaders()) AssignLeader(leader.TeamId, leader.Fingerprint, leader.Username);
    }

    public static void RefreshListTeamsPanel() {
        if (_clientAddon == null) return;
        try {
            var controllerType = HkmpRuntimeResolver.FindType("HKMP.List.UI.ListUiController", "ListUiController");
            var uiObject = GameObject.Find("HKMP.List.UI");
            var controller = controllerType == null || uiObject == null ? null : uiObject.GetComponent(controllerType);
            var panel = HkmpRuntimeResolver.GetMemberValue(controller, "_teamSettingsPanel");
            var visible = HkmpRuntimeResolver.GetMemberValue(panel, "IsVisible");
            if (panel != null && visible is bool isVisible && isVisible) FindMethod(panel.GetType(), "Refresh", 0)?.Invoke(panel, null);
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not refresh the HKMP.List Teams panel: " + exception.GetBaseException());
        }
    }

    private static void FindRegisteredListAddon() {
        if (_listServerAddon != null) {
            _listTeamStore = HkmpRuntimeResolver.GetMemberValue(_listServerAddon, "_teamStore");
            if (_listTeamStore != null) return;
        }

        var managerType = HkmpRuntimeResolver.FindType("Hkmp.Api.Server.ServerAddonManager", "ServerAddonManager");
        var registeredField = managerType == null ? null : managerType.GetField("RegisteredAddons", StaticFlags);
        var registered = registeredField?.GetValue(null) as IEnumerable;
        if (registered != null) {
            foreach (var addon in registered) {
                if (addon != null && string.Equals(addon.GetType().Name, "ListServerAddon", StringComparison.Ordinal)) {
                    _listServerAddon = addon;
                    _listTeamStore = HkmpRuntimeResolver.GetMemberValue(addon, "_teamStore");
                    if (_listTeamStore != null) return;
                }
            }
        }

        var listType = HkmpRuntimeResolver.FindType("HKMP.List.Server.ListServerAddon", "ListServerAddon");
        if (listType == null) return;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            foreach (var candidateType in GetLoadableTypes(assembly)) {
                foreach (var field in candidateType.GetFields(StaticFlags)) {
                    try {
                        var value = field.GetValue(null);
                        if (value != null && listType.IsInstanceOfType(value)) {
                            _listServerAddon = value;
                            _listTeamStore = HkmpRuntimeResolver.GetMemberValue(value, "_teamStore");
                            if (_listTeamStore != null) return;
                        }
                    } catch { }
                }
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly) {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
        catch { return Array.Empty<Type>(); }
    }

    private static MethodInfo FindMethod(Type type, string name, int parameterCount) {
        for (var current = type; current != null; current = current.BaseType) {
            var method = current.GetMethods(InstanceFlags | StaticFlags | BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == parameterCount);
            if (method != null) return method;
        }
        return null;
    }

    private static bool ReadBoolean(object instance, string name) {
        var value = HkmpRuntimeResolver.GetMemberValue(instance, name);
        return value is bool result && result;
    }

    private static void SynchronizeListServer() {
        FindRegisteredListAddon();
        if (_listServerAddon == null || !IsServerStarted()) return;
        try { FindMethod(_listServerAddon.GetType(), "Synchronize", 0)?.Invoke(_listServerAddon, null); }
        catch (Exception exception) { Modding.Logger.LogWarn("HKMP.MoreTeams could not synchronize HKMP.List: " + exception.GetBaseException()); }
    }

    private static bool IsServerStarted() {
        if (_serverAddon == null) return false;
        var api = HkmpRuntimeResolver.GetMemberValue(_serverAddon, "_api");
        var netServer = HkmpRuntimeResolver.GetMemberValue(api, "NetServer");
        var value = HkmpRuntimeResolver.GetMemberValue(netServer, "IsStarted");
        return value is bool started && started;
    }
}

internal sealed class ListBridgeRetryRunner : MonoBehaviour {
    private float _elapsed;

    private void Update() {
        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed < 0.5f) return;
        _elapsed = 0f;
        ListRuntimeIntegration.RetryPendingIntegrations();
    }
}
