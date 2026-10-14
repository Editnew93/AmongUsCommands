using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace AmongUsCommands
{
    [BepInPlugin("editnew93.amonguscommands", "AmongUsCommands", "1.1.0")]
    [BepInProcess("Among Us.exe")]
    public sealed class Plugin : BasePlugin
    {
        internal const string ModGuid = "editnew93.amonguscommands";
        internal static ManualLogSource ModLogger;
        internal static string ChatLogPath = string.Empty;

        private Harmony _harmony;

        public override void Load()
        {
            ModLogger = Log;

            try
            {
                Directory.CreateDirectory(Paths.ConfigPath);
                ChatLogPath = Path.Combine(
                    Paths.ConfigPath,
                    "AmongUsCommands-chat.log"
                );
            }
            catch (Exception ex)
            {
                Log.LogError("Couldn't initialize chat log: " + ex);
            }

            _harmony = new Harmony(ModGuid);
            _harmony.PatchAll();

            // Replay patches are registered separately so the original command,
            // settings, preset, and chat logging patches remain intact.
            ReplaySystem.Initialize();

            Log.LogInfo("AmongUsCommands loaded!");
            Log.LogInfo("Chat log: " + ChatLogPath);
        }

        // All command responses go through LocalCommandChat. This module displays
        // feedback locally and never adds a message to the real player chat.
        internal static void Reply(string message)
        {
            try
            {
                LocalCommandChat.Show(message);
            }
            catch (Exception ex)
            {
                ModLogger?.LogWarning(
                    "Couldn't display local command response: " + ex.Message
                );
                ModLogger?.LogInfo("[AmongUsCommands] " + message);
            }
        }
    }

    // Logs messages displayed through the normal game chat UI.
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
    internal static class ChatLoggerPatch
    {
        private static void Postfix(PlayerControl sourcePlayer, string chatText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(chatText))
                    return;

                // Don't add local command feedback to the player chat log.
                if (chatText.StartsWith(
                    "[AmongUsCommands] ",
                    StringComparison.Ordinal))
                {
                    return;
                }

                // Record real, non-command chat in the active replay before writing
                // the legacy text log. ReplaySystem ignores calls while not recording.
                ReplaySystem.RecordChat(sourcePlayer, chatText);

                if (string.IsNullOrEmpty(Plugin.ChatLogPath))
                    return;

                string playerName = "System";
                if (sourcePlayer != null && sourcePlayer.Data != null)
                    playerName = sourcePlayer.Data.PlayerName;

                string safeMessage = chatText
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");

                string line =
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                    playerName + ": " + safeMessage + Environment.NewLine;

                File.AppendAllText(Plugin.ChatLogPath, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogWarning(
                    "Chat logging failed: " + ex.Message
                );
            }
        }
    }

    // Intercepts commands in free chat. Command text is consumed locally and
    // never sent as an ordinary player chat message.
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.SendFreeChat))]
    internal static class ChatCommandPatch
    {
        private static void ClearChatInput(ChatController instance)
        {
            try
            {
                if (instance == null || instance.freeChatField == null)
                    return;

                object field = instance.freeChatField;
                if (!ReflectionHelper.TryInvokeMethod(field, "Clear", new object[0]))
                    ReflectionHelper.SetMemberValue(field, string.Empty, "Text", "text");
            }
            catch
            {
                // Do not break normal chat if this game's input API differs.
            }
        }

        private static bool Prefix(ChatController __instance)
        {
            bool recognizedCommand = false;

            try
            {
                if (__instance == null || __instance.freeChatField == null)
                    return true;

                string text = __instance.freeChatField.Text;
                if (string.IsNullOrWhiteSpace(text))
                    return true;

                text = text.Trim();
                int separator = text.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });

                string command;
                string arguments;

                if (separator < 0)
                {
                    command = text.ToLowerInvariant();
                    arguments = string.Empty;
                }
                else
                {
                    command = text.Substring(0, separator).ToLowerInvariant();
                    arguments = text.Substring(separator).Trim();
                }

                bool replayControl = command == "/replay" ||
                    command == "/pause" || command == "/resume" ||
                    command == "/length" || command == "/time" ||
                    command == "/haunt" || command == "/roles" ||
                    command == "/role" || command == "/spectate";

                bool existingCommand = command == "/kick" ||
                    command == "/forcestart" || command == "/forceskip" ||
                    command == "/setting" || command == "/map" ||
                    command == "/custompreset";

                // During replay, never let typed free-chat text escape to a
                // multiplayer session. Only local replay controls are accepted.
                if (ReplaySystem.IsPlaybackActive && !replayControl)
                {
                    ClearChatInput(__instance);
                    Plugin.Reply("Replay chat is read-only. Use /pause, /resume, /length, /time, /haunt, /roles or /replay stop.");
                    return false;
                }

                if (!existingCommand && !replayControl)
                    return true;

                recognizedCommand = true;

                switch (command)
                {
                    case "/kick":
                        CommandHandlers.Kick(arguments);
                        break;
                    case "/forcestart":
                        CommandHandlers.ForceStart();
                        break;
                    case "/forceskip":
                        CommandHandlers.ForceSkip();
                        break;
                    case "/setting":
                        CommandHandlers.SetSetting(arguments);
                        break;
                    case "/map":
                        CommandHandlers.SetMap(arguments);
                        break;
                    case "/custompreset":
                        CommandHandlers.CustomPreset(arguments);
                        break;
                    case "/replay":
                        ReplaySystem.HandleReplayCommand(arguments);
                        break;
                    case "/pause":
                    case "/resume":
                    case "/length":
                    case "/time":
                    case "/haunt":
                    case "/roles":
                    case "/role":
                    case "/spectate":
                        ReplaySystem.HandlePlaybackCommand(command, arguments);
                        break;
                }

                ClearChatInput(__instance);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogError("Chat command error: " + ex);

                if (recognizedCommand)
                {
                    Plugin.Reply("Command failed. Check BepInEx/LogOutput.log.");
                    return false;
                }

                return true;
            }
        }
    }

    internal static partial class CommandHandlers
    {
        private const byte SkipVoteId = 253;

        private static bool IsHost()
        {
            return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        }

        // The real lobby state is InnerNetClient.GameStates.Joined. Do not
        // require GameStartManager to be discoverable through reflection.
        private static bool IsLobby()
        {
            if (AmongUsClient.Instance == null)
                return false;

            object gameState = ReflectionHelper.GetMemberValue(
                AmongUsClient.Instance,
                "GameState",
                "gameState"
            );

            if (gameState == null || !string.Equals(
                gameState.ToString(), "Joined", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            object networkMode = ReflectionHelper.GetMemberValue(
                AmongUsClient.Instance,
                "NetworkMode",
                "networkMode"
            );

            if (networkMode != null && string.Equals(
                networkMode.ToString(), "FreePlay", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool RequireHost()
        {
            if (!IsHost())
            {
                Plugin.Reply("Only the lobby host can use this command.");
                return false;
            }

            return true;
        }

        private static bool RequireHostLobby()
        {
            if (!RequireHost())
                return false;

            if (!IsLobby())
            {
                Plugin.Reply("This command can only be used in the lobby.");
                return false;
            }

            return true;
        }

        // /kick <exact player name>
        internal static void Kick(string targetName)
        {
            if (!RequireHost())
                return;

            if (string.IsNullOrWhiteSpace(targetName))
            {
                Plugin.Reply("Usage: /kick <exact player name>");
                return;
            }

            PlayerControl target = null;
            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.Data == null)
                    continue;

                if (string.Equals(
                    player.Data.PlayerName,
                    targetName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    target = player;
                    break;
                }
            }

            if (target == null)
            {
                Plugin.Reply("Couldn't find player: " + targetName);
                return;
            }

            if (target == PlayerControl.LocalPlayer)
            {
                Plugin.Reply("You can't kick yourself.");
                return;
            }

            string kickedName = target.Data.PlayerName;
            // false = kick, not ban.
            AmongUsClient.Instance.KickPlayer(target.OwnerId, false);

            Plugin.ModLogger?.LogInfo("Host requested kick for " + kickedName + ".");
            Plugin.Reply("Kick request sent for " + kickedName + ".");
        }

        // /forcestart
        internal static void ForceStart()
        {
            if (!RequireHostLobby())
                return;

            object manager = ReflectionHelper.GetSingleton("GameStartManager");
            if (manager == null)
            {
                Plugin.Reply("Couldn't find the lobby start manager.");
                return;
            }

            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null)
                    continue;

                object serialized = ReflectionHelper.GetMemberValue(
                    player,
                    "hasBeenSerialized",
                    "HasBeenSerialized"
                );

                if (serialized is bool && !(bool)serialized)
                {
                    Plugin.Reply(
                        "Players are still syncing. Try /forcestart again shortly."
                    );
                    return;
                }
            }

            bool minimumSet = ReflectionHelper.SetMemberValue(
                manager, 1, "MinPlayers", "minPlayers");
            bool timerSet = ReflectionHelper.SetMemberValue(
                manager, 1.5f, "countDownTimer", "CountDownTimer");
            bool stateSet = ReflectionHelper.SetMemberValue(
                manager, "Countdown", "startState", "StartState");

            if (!minimumSet || !timerSet || !stateSet)
            {
                Plugin.ModLogger?.LogError(
                    "ForceStart failed: one or more GameStartManager members were not found."
                );
                Plugin.Reply(
                    "Force start isn't compatible with this game's start API. Check LogOutput.log."
                );
                return;
            }

            ReflectionHelper.SetObjectActive(
                ReflectionHelper.GetMemberValue(manager, "StartButton", "startButton"),
                false
            );
            ReflectionHelper.SetObjectActive(
                ReflectionHelper.GetMemberValue(
                    manager, "GameStartTextParent", "gameStartTextParent"),
                true
            );

            Plugin.ModLogger?.LogInfo("Host started a forced countdown.");
            Plugin.Reply("Forced start countdown started.");
        }

        // /forceskip
        internal static void ForceSkip()
        {
            if (!RequireHost())
                return;

            object meeting = ReflectionHelper.GetSingleton("MeetingHud");
            if (meeting == null)
            {
                Plugin.Reply("There is no active meeting.");
                return;
            }

            object states = ReflectionHelper.GetMemberValue(
                meeting, "playerStates", "PlayerStates");

            if (states == null)
            {
                Plugin.Reply("Couldn't read the current meeting's votes.");
                return;
            }

            int count = ReflectionHelper.GetCollectionCount(states);
            int votesSent = 0;
            HashSet<byte> processedPlayers = new HashSet<byte>();

            for (int i = 0; i < count; i++)
            {
                object area = ReflectionHelper.GetCollectionItem(states, i);
                if (area == null)
                    continue;

                object idValue = ReflectionHelper.GetMemberValue(
                    area,
                    "TargetPlayerId", "targetPlayerId", "PlayerId", "playerId");

                if (idValue == null)
                    continue;

                byte playerId;
                try
                {
                    playerId = Convert.ToByte(idValue, CultureInfo.InvariantCulture);
                }
                catch
                {
                    continue;
                }

                if (!processedPlayers.Add(playerId))
                    continue;

                bool sent = ReflectionHelper.TryInvokeMethod(
                    meeting,
                    "CmdCastVote",
                    new object[] { playerId, SkipVoteId }
                );

                if (!sent)
                {
                    sent = ReflectionHelper.TryInvokeMethod(
                        meeting,
                        "CastVote",
                        new object[] { playerId, SkipVoteId }
                    );
                }

                if (sent)
                    votesSent++;
            }

            if (votesSent == 0)
            {
                Plugin.Reply(
                    "Couldn't submit skip votes with this game's meeting API. Check LogOutput.log."
                );
                return;
            }

            ReflectionHelper.TryInvokeMethod(
                meeting, "CheckForEndVoting", new object[0]);
            ReflectionHelper.TryInvokeMethod(
                meeting, "CheckForEndVoting", new object[] { false });

            Plugin.ModLogger?.LogInfo(
                "ForceSkip attempted votes for " +
                votesSent.ToString(CultureInfo.InvariantCulture) + " player areas."
            );
            Plugin.Reply(
                "Force-skip requested for " +
                votesSent.ToString(CultureInfo.InvariantCulture) + " player areas."
            );
        }

        private sealed class SettingSpec
        {
            internal readonly string Setter;
            internal readonly string OptionName;
            internal readonly bool IsFloat;
            internal readonly float Minimum;
            internal readonly float Maximum;

            internal SettingSpec(
                string setter,
                string optionName,
                bool isFloat,
                float minimum,
                float maximum)
            {
                Setter = setter;
                OptionName = optionName;
                IsFloat = isFloat;
                Minimum = minimum;
                Maximum = maximum;
            }
        }

        private static readonly Dictionary<string, SettingSpec> SettingSpecs =
            new Dictionary<string, SettingSpec>(StringComparer.OrdinalIgnoreCase)
            {
                { "PlayerSpeed", new SettingSpec("SetFloat", "PlayerSpeedMod", true, 0.25f, 3.0f) },
                { "Impostors", new SettingSpec("SetInt", "NumImpostors", false, 1f, 3f) },
                { "KillCooldown", new SettingSpec("SetFloat", "KillCooldown", true, 10f, 60f) },
                { "ImpVision", new SettingSpec("SetFloat", "ImpostorLightMod", true, 0.25f, 5f) },
                { "CrewVision", new SettingSpec("SetFloat", "CrewLightMod", true, 0.25f, 5f) },
                { "EmMeetings", new SettingSpec("SetInt", "NumEmergencyMeetings", false, 0f, 9f) },
                { "EmCooldown", new SettingSpec("SetInt", "EmergencyCooldown", false, 0f, 60f) },
                { "DiscussionTime", new SettingSpec("SetInt", "DiscussionTime", false, 0f, 120f) },
                { "VotingTime", new SettingSpec("SetInt", "VotingTime", false, 0f, 300f) },
                { "CommonTasks", new SettingSpec("SetInt", "NumCommonTasks", false, 0f, 4f) },
                { "LongTasks", new SettingSpec("SetInt", "NumLongTasks", false, 0f, 15f) },
                { "ShortTasks", new SettingSpec("SetInt", "NumShortTasks", false, 0f, 23f) }
            };

        // /setting <setting> <number> -- exactly one local chat reply per call.
        internal static void SetSetting(string arguments)
        {
            if (!RequireHostLobby())
                return;

            string[] parts = arguments.Split(
                new char[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries
            );

            if (parts.Length != 2)
            {
                Plugin.Reply(
                    "Usage: /setting <setting> <number>. Example: /setting PlayerSpeed 1.75"
                );
                return;
            }

            SettingSpec spec;
            if (!SettingSpecs.TryGetValue(parts[0], out spec))
            {
                Plugin.Reply("Unknown setting: " + parts[0]);
                return;
            }

            object value;
            if (spec.IsFloat)
            {
                float number;
                if (!float.TryParse(
                    parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
                    float.IsNaN(number) || float.IsInfinity(number))
                {
                    Plugin.Reply("Enter a valid number.");
                    return;
                }

                if (number < spec.Minimum || number > spec.Maximum)
                {
                    Plugin.Reply(
                        parts[0] + " must be between " +
                        spec.Minimum.ToString(CultureInfo.InvariantCulture) + " and " +
                        spec.Maximum.ToString(CultureInfo.InvariantCulture) + "."
                    );
                    return;
                }

                value = number;
            }
            else
            {
                int number;
                if (!int.TryParse(
                    parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    Plugin.Reply("This setting requires a whole number.");
                    return;
                }

                if (number < spec.Minimum || number > spec.Maximum)
                {
                    Plugin.Reply(
                        parts[0] + " must be between " +
                        spec.Minimum.ToString(CultureInfo.InvariantCulture) + " and " +
                        spec.Maximum.ToString(CultureInfo.InvariantCulture) + "."
                    );
                    return;
                }

                value = number;
            }

            bool changed = GameOptionHelper.SetOption(
                spec.Setter, spec.OptionName, value);

            if (!changed)
            {
                Plugin.Reply("Couldn't change this setting with the current game API.");
                return;
            }

            Plugin.ModLogger?.LogInfo(
                "Setting updated: " + parts[0] + " = " +
                Convert.ToString(value, CultureInfo.InvariantCulture)
            );
            Plugin.Reply(
                "Set " + parts[0] + " to " +
                Convert.ToString(value, CultureInfo.InvariantCulture) + "."
            );
        }

        // /map <Skeld/Mira/Polus/Airship/Fungle>
        internal static void SetMap(string arguments)
        {
            if (!RequireHostLobby())
                return;

            string mapName = arguments.Trim();
            if (string.IsNullOrWhiteSpace(mapName))
            {
                Plugin.Reply("Usage: /map <Skeld/Mira/Polus/Airship/Fungle>");
                return;
            }

            byte mapId;
            switch (mapName.ToLowerInvariant())
            {
                case "skeld":
                    mapId = 0;
                    mapName = "Skeld";
                    break;
                case "mira":
                case "mirahq":
                    mapId = 1;
                    mapName = "Mira";
                    break;
                case "polus":
                    mapId = 2;
                    mapName = "Polus";
                    break;
                case "airship":
                    mapId = 4;
                    mapName = "Airship";
                    break;
                case "fungle":
                    mapId = 5;
                    mapName = "Fungle";
                    break;
                default:
                    Plugin.Reply(
                        "Unknown map. Use Skeld, Mira, Polus, Airship or Fungle."
                    );
                    return;
            }

            bool changed = GameOptionHelper.SetOption("SetByte", "MapId", mapId);
            if (!changed)
            {
                Plugin.Reply("Couldn't change the map with the current game API.");
                return;
            }

            Plugin.ModLogger?.LogInfo("Map changed to " + mapName + ".");
            Plugin.Reply("Map set to " + mapName + ".");
        }
    }

    internal static class GameOptionHelper
    {
        internal static bool SetOption(string methodName, string optionName, object value)
        {
            object manager = ReflectionHelper.GetSingleton("GameOptionsManager");
            if (manager == null)
            {
                Plugin.ModLogger?.LogError("GameOptionsManager wasn't found.");
                return false;
            }

            object options = ReflectionHelper.GetMemberValue(
                manager, "CurrentGameOptions", "currentGameOptions");
            if (options == null)
            {
                Plugin.ModLogger?.LogError("CurrentGameOptions wasn't found.");
                return false;
            }

            bool changed = ReflectionHelper.TryInvokeMethod(
                options, methodName, new object[] { optionName, value });
            if (!changed)
            {
                Plugin.ModLogger?.LogError(
                    "Couldn't call " + methodName + " for option " + optionName + ".");
                return false;
            }

            ReflectionHelper.SetMemberValue(
                manager, options, "GameHostOptions", "gameHostOptions");

            object gameManager = ReflectionHelper.GetSingleton("GameManager");
            object logicOptions = ReflectionHelper.GetMemberValue(
                gameManager, "LogicOptions", "logicOptions");

            if (logicOptions != null)
            {
                ReflectionHelper.TryInvokeMethod(
                    logicOptions, "SetGameOptions", new object[] { options });

                bool synced = ReflectionHelper.TryInvokeMethod(
                    logicOptions, "SyncOptions", new object[0]);

                if (!synced)
                {
                    Plugin.ModLogger?.LogWarning(
                        "Settings changed, but SyncOptions wasn't found.");
                }
            }
            else
            {
                Plugin.ModLogger?.LogWarning(
                    "Settings changed, but LogicOptions wasn't found.");
            }

            return true;
        }
    }

    internal static class ReflectionHelper
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, Type> TypeLookupCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        internal static Type FindType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            // Do not call Harmony's AccessTools.TypeByName here. HarmonyX's fallback
            // enumerates every type in Unity/Il2CPP assemblies and prints repetitive
            // ReflectionTypeLoadException warnings for proxy-only interface/job types.
            // We perform a quiet, partial-type-safe search and cache the result instead.
            lock (TypeLookupCache)
            {
                Type cached;
                if (TypeLookupCache.TryGetValue(typeName, out cached))
                    return cached;
            }

            Type found = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // First pass: fully-qualified names can be resolved without enumerating types.
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    found = assemblies[i].GetType(typeName, false);
                    if (found != null)
                        break;
                }
                catch
                {
                    // Some IL2CPP proxy assemblies have metadata entries that cannot be resolved.
                }
            }

            // Second pass: the mod intentionally asks for short game type names such as
            // PlayerControl. Preserve that behavior while tolerating partially loadable assemblies.
            if (found == null)
            {
                for (int i = 0; i < assemblies.Length && found == null; i++)
                {
                    Type[] candidates;
                    try
                    {
                        candidates = assemblies[i].GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        candidates = ex.Types;
                    }
                    catch
                    {
                        continue;
                    }

                    if (candidates == null)
                        continue;

                    for (int j = 0; j < candidates.Length; j++)
                    {
                        Type candidate = candidates[j];
                        if (candidate != null && string.Equals(candidate.Name, typeName, StringComparison.Ordinal))
                        {
                            found = candidate;
                            break;
                        }
                    }
                }
            }

            lock (TypeLookupCache)
                TypeLookupCache[typeName] = found;
            return found;
        }

        internal static object GetSingleton(string typeName)
        {
            Type type = FindType(typeName);
            if (type == null)
                return null;

            foreach (string name in new string[] { "Instance", "instance" })
            {
                try
                {
                    PropertyInfo property = type.GetProperty(name, StaticFlags);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        object value = property.GetValue(null, null);
                        if (value != null)
                            return value;
                    }

                    FieldInfo field = type.GetField(name, StaticFlags);
                    if (field != null)
                    {
                        object value = field.GetValue(null);
                        if (value != null)
                            return value;
                    }
                }
                catch
                {
                    // Continue with alternative singleton lookup.
                }
            }

            try
            {
                Type unityObject = FindType("UnityEngine.Object");
                if (unityObject == null)
                    return null;

                foreach (MethodInfo method in unityObject.GetMethods(StaticFlags))
                {
                    if (method.Name != "FindObjectOfType")
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 ||
                        parameters[0].ParameterType != typeof(Type))
                    {
                        continue;
                    }

                    object value = method.Invoke(null, new object[] { type });
                    if (value != null)
                        return value;
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug(
                    "Singleton lookup failed for " + typeName + ": " + ex.Message);
            }

            return null;
        }

        internal static object GetMemberValue(object target, params string[] names)
        {
            if (target == null)
                return null;

            Type type = target.GetType();
            while (type != null)
            {
                foreach (string name in names)
                {
                    try
                    {
                        PropertyInfo property = type.GetProperty(name, InstanceFlags);
                        if (property != null && property.GetIndexParameters().Length == 0)
                            return property.GetValue(target, null);

                        FieldInfo field = type.GetField(name, InstanceFlags);
                        if (field != null)
                            return field.GetValue(target);
                    }
                    catch
                    {
                        // Try the remaining member names.
                    }
                }

                type = type.BaseType;
            }

            return null;
        }

        internal static bool SetMemberValue(object target, object value, params string[] names)
        {
            if (target == null)
                return false;

            Type type = target.GetType();
            while (type != null)
            {
                foreach (string name in names)
                {
                    try
                    {
                        PropertyInfo property = type.GetProperty(name, InstanceFlags);
                        if (property != null && property.GetIndexParameters().Length == 0)
                        {
                            MethodInfo setter = property.GetSetMethod(true);
                            object converted;
                            if (setter != null && TryConvertValue(
                                value, property.PropertyType, out converted))
                            {
                                property.SetValue(target, converted, null);
                                return true;
                            }
                        }

                        FieldInfo field = type.GetField(name, InstanceFlags);
                        if (field != null && !field.IsInitOnly)
                        {
                            object converted;
                            if (TryConvertValue(value, field.FieldType, out converted))
                            {
                                field.SetValue(target, converted);
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug(
                            "Could not set member " + name + ": " + ex.Message);
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        internal static bool TryConvertValue(object value, Type targetType, out object converted)
        {
            converted = null;
            try
            {
                if (value == null)
                {
                    converted = null;
                    return !targetType.IsValueType;
                }

                if (targetType.IsInstanceOfType(value))
                {
                    converted = value;
                    return true;
                }

                if (targetType.IsEnum)
                {
                    converted = value is string
                        ? Enum.Parse(targetType, (string)value, true)
                        : Enum.ToObject(targetType, value);
                    return true;
                }

                converted = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryInvokeMethod(object target, string methodName, object[] arguments)
        {
            if (target == null)
                return false;

            Type type = target.GetType();
            while (type != null)
            {
                foreach (MethodInfo method in type.GetMethods(InstanceFlags))
                {
                    if (method.Name != methodName)
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != arguments.Length)
                        continue;

                    object[] convertedArguments = new object[arguments.Length];
                    bool valid = true;

                    for (int i = 0; i < arguments.Length; i++)
                    {
                        if (!TryConvertValue(
                            arguments[i], parameters[i].ParameterType, out convertedArguments[i]))
                        {
                            valid = false;
                            break;
                        }
                    }

                    if (!valid)
                        continue;

                    try
                    {
                        method.Invoke(target, convertedArguments);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug(
                            "Invocation of " + methodName + " failed: " + ex.Message);
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        internal static int GetCollectionCount(object collection)
        {
            object count = GetMemberValue(collection, "Count", "Length", "size");
            if (count == null)
                return 0;

            try
            {
                return Convert.ToInt32(count, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        internal static object GetCollectionItem(object collection, int index)
        {
            if (collection == null)
                return null;

            Type type = collection.GetType();
            while (type != null)
            {
                try
                {
                    foreach (PropertyInfo property in type.GetProperties(InstanceFlags))
                    {
                        if (property.Name != "Item")
                            continue;

                        ParameterInfo[] parameters = property.GetIndexParameters();
                        if (parameters.Length != 1)
                            continue;

                        object convertedIndex;
                        if (!TryConvertValue(index, parameters[0].ParameterType, out convertedIndex))
                            continue;

                        return property.GetValue(collection, new object[] { convertedIndex });
                    }
                }
                catch
                {
                    // Try get_Item below.
                }

                try
                {
                    foreach (MethodInfo method in type.GetMethods(InstanceFlags))
                    {
                        if (method.Name != "get_Item" || method.GetParameters().Length != 1)
                            continue;

                        object convertedIndex;
                        if (!TryConvertValue(
                            index, method.GetParameters()[0].ParameterType, out convertedIndex))
                        {
                            continue;
                        }

                        return method.Invoke(collection, new object[] { convertedIndex });
                    }
                }
                catch
                {
                    // Try base type.
                }

                type = type.BaseType;
            }

            return null;
        }

        internal static void SetObjectActive(object target, bool active)
        {
            if (target == null)
                return;

            object gameObject = GetMemberValue(target, "gameObject", "GameObject");
            if (gameObject == null)
                gameObject = target;

            TryInvokeMethod(gameObject, "SetActive", new object[] { active });
        }
    }
}
