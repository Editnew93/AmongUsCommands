using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Globalization;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AmongUsCommands
{
    /// <summary>
    /// Local state-based match recorder and in-world replay player.
    /// AMR2 stores timestamped player snapshots (position, velocity, role, cosmetics,
    /// death/vent/disconnect state and task status) plus chat and game-event markers.
    /// AMR1 files remain readable. Playback actors are local visuals, not network players.
    /// </summary>
    internal static class ReplaySystem
    {
        private const int FileVersion = 2;
        private const int LegacyFileVersion = 1;
        private const byte EntryFrame = 1;
        private const byte EntryChat = 2;
        private const byte EntryEvent = 3;
        private const byte EntryEnd = 255;
        private const float SampleInterval = 0.05f; // 20 snapshots per second, similar to a game-tick replay
        private const float ActorZ = -0.01f;

        private static Harmony _harmony;
        private static BinaryWriter _writer;
        private static string _recordingPath = string.Empty;
        private static string _pendingRecordName = string.Empty;
        private static bool _recordWhenMatchStarts;
        private static bool _isRecording;
        private static float _recordStartTime;
        private static float _nextSampleTime;
        private static float _lastFlushTime;
        private static bool _sawRecordingMap;
        private static int _lastTickFrame = -1;
        private static bool _velocityCaptureWarningLogged;
        private static bool _taskCaptureWarningLogged;
        private static float _lastRecordedChatTime = -1f;
        private static int _lastRecordedChatPlayerId = int.MinValue;
        private static string _lastRecordedChatText = string.Empty;
        private static string _lastReplayLoadingStatus = string.Empty;

        private static ReplayData _loadedReplay;
        private static readonly Dictionary<int, ReplayActor> Actors = new Dictionary<int, ReplayActor>();
        private static bool _playbackLoading;
        private static bool _playbackActive;
        private static bool _playbackPaused;
        // Replay launch state. Playback runs through offline Freeplay; recorded
        // actors are visual-only. Session exits are deferred out of chat callbacks.
        private static bool _freeplayButtonClicked;
        private static bool _mapButtonClicked;
        private static bool _freeplayStartClicked;
        private static bool _sawMainMenu;
        private static bool _localModeClicked;
        private static bool _localHostClicked;
        private static bool _localCreateClicked;
        private static bool _localHostSessionDetected;
        private static bool _localForceStartRequested;
        private static bool _localFallbackStartAttempted;
        private static bool _localLaunchWarningShown;
        private static int _localForceStartAttempts;
        private static float _localForceStartLastAttempt;
        // ExitGame may transition scenes asynchronously. Never load MainMenu
        // in the same frame as ExitGame; defer the fallback to avoid racing the
        // game's network/session teardown and closing the process.
        private static bool _mainMenuFallbackPending; // legacy field; no forced scene-load fallback is used
        private static bool _mainMenuFallbackAttempted; // legacy field; no forced scene-load fallback is used
        private static float _mainMenuFallbackAt; // legacy field; no forced scene-load fallback is used
        // Never disconnect or change scenes inside ChatController.SendFreeChat.
        // ExitGame can collide with Among Us' network spawn serializer if invoked
        // from the same frame as a join/chat action.
        private static bool _deferredExitPending;
        private static bool _awaitingMainMenu;
        private static int _deferredExitEarliestFrame;
        private static float _deferredExitNotBefore;
        private static float _deferredExitDeadline;
        private static float _awaitMainMenuDeadline;
        private static float _mainMenuReadyAt;
        private static bool _exitRequestIssued;
        private static bool _exitTimeoutLogged;
        private static string _exitObservedState = string.Empty;
        private static int _exitObservedPlayerCount = -1;
        private static float _exitStateStableSince;
        // Keep an already-open private local host lobby on port 22023 instead
        // of tearing down its session. Preview actors are visual-only and never
        // become fake network players or NetObjects.
        private static bool _reusePort22023Lobby;
        private static bool _lobbyActorPreviewCreated;
        private static bool _waitForManualExitFromOtherLobby;
        private static float _playbackLaunchStart;
        private static float _lastLaunchProbe;
        private static float _playbackTime;
        private static float _lastPlaybackTick;
        private static int _nextChatIndex;
        private static int _nextEventIndex;
        private static bool _localWasDead;
        private static bool _savedLocalDead;
        private static PlayerControl _spectator;
        private static bool _savedLocalMoveable;
        private static int _hauntedPlayerId = -1;

        private sealed class PlayerSnapshot
        {
            public int PlayerId;
            public string Name;
            public byte ColorId;
            public string Role;
            public bool IsDead;
            public bool IsImpostor;
            public bool InVent;
            public float X;
            public float Y;
            public float Z;
            public string Hat;
            public string Skin;
            public string Visor;
            public string Pet;
            // AMR2 state fields. AMR1 files leave these at their defaults.
            public float VelocityX;
            public float VelocityY;
            public bool IsDisconnected;
            public readonly List<TaskSnapshot> Tasks = new List<TaskSnapshot>();
        }

        private sealed class TaskSnapshot
        {
            public int Id;
            public int TypeId;
            public bool IsComplete;
        }

        private sealed class ReplayFrame
        {
            public float Time;
            public readonly List<PlayerSnapshot> Players = new List<PlayerSnapshot>();
        }

        private sealed class ReplayChat
        {
            public float Time;
            public int PlayerId;
            public string PlayerName;
            public byte ColorId;
            public string Message;
        }

        private sealed class ReplayEvent
        {
            public float Time;
            public string EventName;
            public string ActorName;
            public string TargetName;
            public string Details;
        }

        private sealed class ReplayData
        {
            public int FormatVersion;
            public byte MapId;
            public string FileName;
            public float Duration;
            public readonly List<ReplayFrame> Frames = new List<ReplayFrame>();
            public readonly List<ReplayChat> Chat = new List<ReplayChat>();
            public readonly List<ReplayEvent> Events = new List<ReplayEvent>();
        }

        private sealed class ReplayActor
        {
            public GameObject Root;
            public int PlayerId;
            public string Name;
            public string Role;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Component> TextComponents = new List<Component>();
            public readonly Dictionary<SpriteRenderer, bool> OriginalFlipX = new Dictionary<SpriteRenderer, bool>();
            public bool LastDead;
        }

        internal static bool IsPlaybackActive
        {
            get { return _playbackActive || _playbackLoading; }
        }

        internal static void Initialize()
        {
            try
            {
                _harmony = new Harmony(Plugin.ModGuid + ".replay");
                PatchTick("KeyboardJoystick", "Update");
                PatchTick("HudManager", "Update");
                // Keep the replay launcher ticking even if this build does not
                // update HudManager on the Local/LAN lobby screen.
                PatchTick("GameStartManager", "Update");
                PatchCameraFollow();
                PatchChatCapture();

                PatchEvent("PlayerControl", "MurderPlayer");
                PatchEvent("PlayerControl", "CheckMurder");
                PatchEvent("PlayerControl", "CmdCheckMurder");
                PatchEvent("PlayerControl", "ReportDeadBody");
                PatchEvent("PlayerControl", "CmdReportDeadBody");
                PatchEvent("PlayerControl", "RpcSetRole");
                PatchEvent("PlayerControl", "CompleteTask");
                PatchEvent("PlayerControl", "RpcCompleteTask");
                PatchEvent("PlayerControl", "Shapeshift");
                PatchEvent("PlayerControl", "StartMeeting");
                PatchEvent("PlayerPhysics", "EnterVent");
                PatchEvent("PlayerPhysics", "ExitVent");
                PatchEvent("Vent", "EnterVent");
                PatchEvent("Vent", "ExitVent");
                PatchEvent("MeetingHud", "Start");
                PatchEvent("MeetingHud", "CastVote");
                PatchEvent("MeetingHud", "CheckForEndVoting");
                PatchEvent("ExileController", "Begin");
                PatchEvent("EndGameManager", "Start");

                // Record sabotage/repair/door operations as timeline events too.
                // Missing type or method names are safely skipped on different game builds.
                PatchEvent("ShipStatus", "RpcUpdateSystem");
                PatchEvent("ShipStatus", "RpcCloseDoorsOfType");
                PatchEvent("ShipStatus", "RpcOpenDoorsOfType");
                PatchEvent("ShipStatus", "RepairSystem");
                PatchEvent("SabotageSystem", "RepairDamage");
                PatchEvent("ReactorSystem", "RepairDamage");
                PatchEvent("LifeSuppSystem", "RepairDamage");
                PatchEvent("SwitchSystem", "RepairDamage");
                PatchEvent("HudOverrideSystem", "RepairDamage");
                PatchEvent("DoorsSystem", "RepairDamage");
                PatchEvent("HeliSabotageSystem", "RepairDamage");
                PatchEvent("LaboratorySystem", "RepairDamage");

                Plugin.ModLogger?.LogInfo("Replay system hooks initialized.");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogError("Replay hook initialization failed: " + ex);
            }
        }

        private static void PatchTick(string typeName, string methodName)
        {
            try
            {
                Type type = ReflectionHelper.FindType(typeName);
                if (type == null)
                    return;

                MethodInfo method = type.GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);

                if (method == null)
                    return;

                MethodInfo postfix = typeof(ReplaySystem).GetMethod(
                    nameof(TickPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay tick patch skipped for " + typeName + "." + methodName + ": " + ex.Message);
            }
        }

        private static void PatchCameraFollow()
        {
            try
            {
                Type type = ReflectionHelper.FindType("FollowerCamera");
                if (type == null)
                {
                    Plugin.ModLogger?.LogWarning("FollowerCamera was not found; /haunt may not move the camera on this game build.");
                    return;
                }

                MethodInfo postfix = typeof(ReplaySystem).GetMethod(
                    nameof(CameraFollowPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "Update" || method.IsAbstract || method.ContainsGenericParameters ||
                        method.GetParameters().Length != 0)
                        continue;

                    try
                    {
                        _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                        Plugin.ModLogger?.LogInfo("Replay haunt camera hook installed.");
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug("Replay camera hook skipped: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogWarning("Replay camera hook initialization failed: " + ex.Message);
            }
        }

        // The camera's own Update can reset its target; apply our local replay
        // target after the game's camera update so haunting stays locked on.
        private static void CameraFollowPostfix()
        {
            if (_playbackActive)
                ApplyHauntCameraTarget();
        }

        // ChatLoggerPatch in Plugin.cs already forwards chat to RecordChat on some
        // builds. This replay-owned hook covers builds where AddChat overloads differ.
        // RecordChat deduplicates the double-hook case, so Plugin.cs does not need edits.
        private static void PatchChatCapture()
        {
            try
            {
                Type type = ReflectionHelper.FindType("ChatController");
                if (type == null)
                {
                    Plugin.ModLogger?.LogWarning("ChatController was not found; replay chat capture is unavailable on this build.");
                    return;
                }

                MethodInfo postfix = typeof(ReplaySystem).GetMethod(
                    nameof(ChatCapturePostfix), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                int patched = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "AddChat" || method.IsAbstract || method.ContainsGenericParameters)
                        continue;
                    try
                    {
                        _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug("Replay chat hook skipped for ChatController.AddChat: " + ex.Message);
                    }
                }

                Plugin.ModLogger?.LogInfo("Replay chat capture hook installed on " + patched + " ChatController.AddChat overload(s).");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogWarning("Replay chat hook initialization failed: " + ex.Message);
            }
        }

        private static void ChatCapturePostfix(object[] __args)
        {
            if (!_isRecording || __args == null || __args.Length == 0)
                return;

            try
            {
                PlayerControl source = null;
                string text = null;
                for (int i = 0; i < __args.Length; i++)
                {
                    object arg = __args[i];
                    if (arg == null)
                        continue;

                    PlayerControl possiblePlayer = arg as PlayerControl;
                    if (possiblePlayer != null)
                        source = possiblePlayer;

                    string possibleText = arg as string;
                    if (possibleText != null && !string.IsNullOrWhiteSpace(possibleText) && text == null)
                        text = possibleText;
                }

                if (!string.IsNullOrWhiteSpace(text))
                    RecordChat(source, text);
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay chat capture postfix failed: " + ex.Message);
            }
        }

        private static void PatchEvent(string typeName, string methodName)
        {
            try
            {
                Type type = ReflectionHelper.FindType(typeName);
                if (type == null)
                    return;

                MethodInfo postfix = typeof(ReplaySystem).GetMethod(
                    nameof(EventPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != methodName || method.IsAbstract || method.ContainsGenericParameters)
                        continue;

                    try
                    {
                        _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug("Replay event patch skipped for " + typeName + "." + methodName + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay event target not found for " + typeName + "." + methodName + ": " + ex.Message);
            }
        }

        private static bool TryClassifySystemEvent(string originalName, string details, out string eventName)
        {
            eventName = originalName;
            if (string.IsNullOrEmpty(originalName))
                return false;

            string lower = originalName.ToLowerInvariant();
            if (lower.Contains("rpcupdatesystem"))
            {
                eventName = "Sabotage.SystemUpdate";
                return true;
            }
            if (lower.Contains("rpcclosedoorsoftype") || lower.Contains("closedoorsoftype"))
            {
                eventName = "Sabotage.Doors";
                return true;
            }
            if (lower.EndsWith(".repairdamage", StringComparison.Ordinal))
            {
                int dot = originalName.LastIndexOf('.');
                string systemName = dot > 0 ? originalName.Substring(0, dot) : originalName;
                eventName = "Sabotage.Repair." + systemName;
                return true;
            }
            if (lower.Contains("repairsystem"))
            {
                eventName = "Sabotage.SystemRepair";
                return true;
            }
            if (lower.Contains("rpcopendoorsoftype") || lower.Contains("opendoorsoftype"))
            {
                eventName = "Sabotage.DoorsOpen";
                return true;
            }
            if (lower.Contains("sabotage") || lower.Contains("reactorsystem") ||
                lower.Contains("lifesuppsystem") || lower.Contains("switchsystem") ||
                lower.Contains("hudoverridesystem") || lower.Contains("doorssystem") ||
                lower.Contains("laboratorysystem") || lower.Contains("helisabotage"))
            {
                eventName = "Sabotage.SystemEvent";
                return true;
            }
            return false;
        }

        private static void TickPostfix()
        {
            Tick();
        }

        private static void EventPostfix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (!_isRecording || __originalMethod == null)
                return;

            try
            {
                string actor = GetPlayerName(__instance);
                string target = string.Empty;
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        string possibleName = GetPlayerName(__args[i]);
                        if (!string.IsNullOrWhiteSpace(possibleName) &&
                            !string.Equals(possibleName, actor, StringComparison.Ordinal))
                        {
                            target = possibleName;
                            break;
                        }
                    }
                }

                string eventName = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
                string details = MakeEventDetails(__args);
                string systemEventName;
                if (TryClassifySystemEvent(eventName, details, out systemEventName))
                    eventName = systemEventName;

                WriteEvent(eventName, actor, target, details);
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay event capture failed: " + ex.Message);
            }
        }

        internal static void Tick()
        {
            if (_lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;

            try { LocalCommandChat.Tick(); }
            catch (Exception ex) { Plugin.ModLogger?.LogDebug("LocalCommandChat tick failed: " + ex.Message); }

            try
            {
                if (_recordWhenMatchStarts && IsMatchSceneReady())
                {
                    _recordWhenMatchStarts = false;
                    StartRecording(_pendingRecordName);
                }

                if (_isRecording)
                {
                    if (!IsMatchSceneReady() || ReflectionHelper.GetSingleton("EndGameManager") != null)
                    {
                        StopRecording("Match ended; replay saved.");
                    }
                    else
                    {
                        TickRecorder();
                    }
                }

                if (_playbackLoading)
                {
                    TickDeferredExitRequest();
                    if (_playbackLoading)
                        TickPlaybackLauncher();
                }
                else if (_playbackActive)
                {
                    TickPlayback();
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogError("Replay tick failed: " + ex);
            }
        }

        internal static void RecordChat(PlayerControl sourcePlayer, string chatText)
        {
            if (!_isRecording || _writer == null || string.IsNullOrWhiteSpace(chatText))
                return;

            string trimmed = chatText.Trim();
            // Commands and replay-generated lines are not player conversation.
            if (trimmed.StartsWith("/", StringComparison.Ordinal) ||
                trimmed.StartsWith("[AmongUsCommands]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.IndexOf("[Replay]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf("[Replay event]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf("[Replay sabotage]", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            try
            {
                string name = sourcePlayer != null && sourcePlayer.Data != null
                    ? sourcePlayer.Data.PlayerName
                    : "System";
                int playerId = sourcePlayer != null
                    ? SafeInt(ReflectionHelper.GetMemberValue(sourcePlayer, "PlayerId", "playerId"), -1)
                    : -1;
                byte colorId = sourcePlayer != null ? GetColorId(sourcePlayer) : (byte)0;
                float time = GetRecordingTime();

                // Plugin.ChatLoggerPatch and this replay-owned hook can both see one AddChat call.
                // Deduplicate the same player/message from the same frame while preserving later repeats.
                if (playerId == _lastRecordedChatPlayerId &&
                    string.Equals(trimmed, _lastRecordedChatText, StringComparison.Ordinal) &&
                    time >= _lastRecordedChatTime && time - _lastRecordedChatTime <= 0.075f)
                    return;

                _writer.Write(EntryChat);
                _writer.Write(time);
                _writer.Write(playerId);
                _writer.Write(colorId);
                _writer.Write(name ?? "System");
                _writer.Write(trimmed);

                _lastRecordedChatTime = time;
                _lastRecordedChatPlayerId = playerId;
                _lastRecordedChatText = trimmed;
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogWarning("Replay chat capture failed: " + ex.Message);
            }
        }

        internal static void HandleReplayCommand(string arguments)
        {
            string[] parts = (arguments ?? string.Empty).Split(
                new char[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                Plugin.Reply("Usage: /replay <record|play|stop|stoprecording> <filename>");
                return;
            }

            string action = parts[0].ToLowerInvariant();
            string name = parts.Length > 1 ? string.Join(" ", parts, 1, parts.Length - 1) : string.Empty;

            switch (action)
            {
                case "record":
                    if (_playbackActive || _playbackLoading)
                    {
                        Plugin.Reply("Stop the replay before starting a new recording.");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Plugin.Reply("Usage: /replay record <filename>");
                        return;
                    }

                    if (_isRecording || _recordWhenMatchStarts)
                    {
                        Plugin.Reply("A replay recording is already active or queued.");
                        return;
                    }

                    _pendingRecordName = NormalizeFileName(name);
                    if (IsMatchSceneReady())
                    {
                        StartRecording(_pendingRecordName);
                        Plugin.Reply("Recording started: " + _pendingRecordName + ".amr");
                    }
                    else
                    {
                        _recordWhenMatchStarts = true;
                        Plugin.Reply("When the match starts we will start recording.");
                    }
                    return;

                case "play":
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Plugin.Reply("Usage: /replay play <filename>");
                        return;
                    }
                    StartPlayback(NormalizeFileName(name));
                    return;

                case "stoprecording":
                case "stop-recording":
                    bool wasQueued = _recordWhenMatchStarts;
                    _recordWhenMatchStarts = false;
                    _pendingRecordName = string.Empty;
                    if (_isRecording)
                        StopRecording("Recording stopped and replay saved.");
                    else if (wasQueued)
                        Plugin.Reply("Queued replay recording cancelled.");
                    else
                        Plugin.Reply("No replay recording is active.");
                    return;

                case "stop":
                    if (_playbackActive || _playbackLoading)
                    {
                        StopPlayback(true);
                        Plugin.Reply("Replay playback stopped.");
                    }
                    else if (_isRecording || _recordWhenMatchStarts)
                    {
                        _recordWhenMatchStarts = false;
                        StopRecording("Recording stopped and replay saved.");
                    }
                    else
                    {
                        Plugin.Reply("No replay is currently playing or recording.");
                    }
                    return;

                default:
                    Plugin.Reply("Unknown replay action. Use record, play, stop or stoprecording.");
                    return;
            }
        }

        internal static void HandlePlaybackCommand(string command, string arguments)
        {
            if (!_playbackActive)
            {
                Plugin.Reply("No replay is currently playing.");
                return;
            }

            switch ((command ?? string.Empty).ToLowerInvariant())
            {
                case "/pause":
                    _playbackPaused = true;
                    Plugin.Reply("Replay paused at " + FormatTime(_playbackTime) + ".");
                    return;

                case "/resume":
                    _playbackPaused = false;
                    _lastPlaybackTick = Time.unscaledTime;
                    Plugin.Reply("Replay resumed.");
                    return;

                case "/length":
                    Plugin.Reply("Replay length: " + FormatTime(_loadedReplay.Duration) + ". Current time: " + FormatTime(_playbackTime) + ".");
                    return;

                case "/time":
                    float target;
                    if (!TryParseTime(arguments, out target))
                    {
                        Plugin.Reply("Usage: /time <seconds or mm:ss>");
                        return;
                    }

                    _playbackTime = Mathf.Clamp(target, 0f, _loadedReplay.Duration);
                    _nextChatIndex = FindChatIndex(_playbackTime);
                    _nextEventIndex = FindEventIndex(_playbackTime);
                    ApplyPlaybackFrame(_playbackTime);
                    Plugin.Reply("Replay seeked to " + FormatTime(_playbackTime) + ".");
                    return;

                case "/haunt":
                case "/spectate":
                    HandleHauntCommand(arguments);
                    return;

                case "/roles":
                    ShowReplayRoles();
                    return;

                case "/role":
                    ShowOneReplayRole(arguments);
                    return;
            }
        }

        private static void StartRecording(string rawName)
        {
            try
            {
                if (_isRecording)
                    StopRecording("Previous replay recording was closed.");

                string name = NormalizeFileName(rawName);
                string directory = GetReplayDirectory();
                Directory.CreateDirectory(directory);
                _recordingPath = Path.Combine(directory, name + ".amr");

                FileStream stream = new FileStream(_recordingPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new BinaryWriter(stream);
                _writer.Write(new byte[] { (byte)'A', (byte)'M', (byte)'R', (byte)'2' });
                _writer.Write(FileVersion);
                _writer.Write(GetMapId());
                _writer.Write(name);
                _writer.Write(DateTime.UtcNow.Ticks);
                _writer.Flush();

                _recordStartTime = Time.unscaledTime;
                _lastRecordedChatTime = -1f;
                _lastRecordedChatPlayerId = int.MinValue;
                _lastRecordedChatText = string.Empty;
                _nextSampleTime = 0f;
                _lastFlushTime = 0f;
                _sawRecordingMap = true;
                _isRecording = true;
                _pendingRecordName = name;

                Plugin.ModLogger?.LogInfo("Recording AMR2 state replay to " + _recordingPath +
                ". Capturing timestamped position/velocity, role, outfit, dead/vent/disconnected state, task state, chat and event markers.");
                WriteEvent("RecordingStarted", "System", string.Empty, "map=" + GetMapId());
                TickRecorder();
            }
            catch (Exception ex)
            {
                _isRecording = false;
                SafeCloseWriter();
                Plugin.ModLogger?.LogError("Could not start replay recording: " + ex);
                Plugin.Reply("Couldn't start replay recording. Check LogOutput.log.");
            }
        }

        private static void TickRecorder()
        {
            if (!_isRecording || _writer == null)
                return;

            float now = GetRecordingTime();
            if (now >= _nextSampleTime)
            {
                WriteFrame(CaptureFrame(now));
                _nextSampleTime = now + SampleInterval;
            }

            if (now - _lastFlushTime >= 2f)
            {
                _writer.Flush();
                _lastFlushTime = now;
            }
        }

        private static ReplayFrame CaptureFrame(float time)
        {
            ReplayFrame frame = new ReplayFrame();
            frame.Time = time;

            try
            {
                if (PlayerControl.AllPlayerControls == null)
                    return frame;

                foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                {
                    if (player == null || player.Data == null || player.transform == null)
                        continue;

                    PlayerSnapshot snapshot = new PlayerSnapshot();
                    snapshot.PlayerId = SafeInt(ReflectionHelper.GetMemberValue(player, "PlayerId", "playerId"), -1);
                    snapshot.Name = player.Data.PlayerName ?? ("Player " + snapshot.PlayerId);
                    snapshot.ColorId = GetColorId(player);
                    snapshot.Role = GetRoleName(player);
                    snapshot.IsDead = SafeBool(ReflectionHelper.GetMemberValue(player.Data, "IsDead", "isDead"));
                    snapshot.IsImpostor = IsImpostorRole(snapshot.Role);
                    snapshot.InVent = SafeBool(ReflectionHelper.GetMemberValue(player, "inVent", "InVent"));
                    snapshot.IsDisconnected = SafeBool(ReflectionHelper.GetMemberValue(player.Data, "Disconnected", "disconnected"));
                    CaptureVelocityAndTasks(player, snapshot);

                    Vector3 position = player.transform.position;
                    snapshot.X = position.x;
                    snapshot.Y = position.y;
                    snapshot.Z = position.z;

                    object outfit = ReflectionHelper.GetMemberValue(player.Data, "DefaultOutfit", "defaultOutfit", "CurrentOutfit", "currentOutfit");
                    snapshot.Hat = SafeString(ReflectionHelper.GetMemberValue(outfit, "HatId", "hatId"));
                    snapshot.Skin = SafeString(ReflectionHelper.GetMemberValue(outfit, "SkinId", "skinId"));
                    snapshot.Visor = SafeString(ReflectionHelper.GetMemberValue(outfit, "VisorId", "visorId"));
                    snapshot.Pet = SafeString(ReflectionHelper.GetMemberValue(outfit, "PetId", "petId"));
                    frame.Players.Add(snapshot);
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay frame capture partially failed: " + ex.Message);
            }

            return frame;
        }

        private static void CaptureVelocityAndTasks(PlayerControl player, PlayerSnapshot snapshot)
        {
            if (player == null || snapshot == null)
                return;

            try
            {
                object physics = ReflectionHelper.GetMemberValue(player, "MyPhysics", "myPhysics");
                object body = ReflectionHelper.GetMemberValue(physics, "body", "Body");
                object velocity = ReflectionHelper.GetMemberValue(body, "velocity", "Velocity");
                snapshot.VelocityX = SafeFloat(ReflectionHelper.GetMemberValue(velocity, "x", "X"), 0f);
                snapshot.VelocityY = SafeFloat(ReflectionHelper.GetMemberValue(velocity, "y", "Y"), 0f);
            }
            catch (Exception ex)
            {
                if (!_velocityCaptureWarningLogged)
                {
                    _velocityCaptureWarningLogged = true;
                    Plugin.ModLogger?.LogWarning("Replay velocity capture is unavailable on this build; positions will still be recorded. " + ex.Message);
                }
            }

            try
            {
                object taskCollection = ReflectionHelper.GetMemberValue(player, "myTasks", "MyTasks");
                int count = GetReplayCollectionCount(taskCollection);
                // Guard corrupted/changed collections so a single malformed player cannot abort the frame.
                count = Math.Max(0, Math.Min(count, 255));
                for (int index = 0; index < count; index++)
                {
                    object taskObject = GetReplayCollectionItem(taskCollection, index);
                    if (taskObject == null)
                        continue;

                    TaskSnapshot task = new TaskSnapshot();
                    task.Id = SafeInt(ReflectionHelper.GetMemberValue(taskObject, "Id", "id"), index);
                    task.TypeId = SafeInt(ReflectionHelper.GetMemberValue(taskObject, "TaskType", "taskType", "TypeId", "typeId"), 0);
                    task.IsComplete = SafeBool(ReflectionHelper.GetMemberValue(taskObject, "IsComplete", "isComplete", "Complete", "complete"));
                    snapshot.Tasks.Add(task);
                }
            }
            catch (Exception ex)
            {
                if (!_taskCaptureWarningLogged)
                {
                    _taskCaptureWarningLogged = true;
                    Plugin.ModLogger?.LogWarning("Replay task capture is unavailable on this build; movement/chat/events will still be recorded. " + ex.Message);
                }
            }
        }

        private static int GetReplayCollectionCount(object collection)
        {
            if (collection == null)
                return 0;
            if (collection is Array)
                return ((Array)collection).Length;
            if (collection is System.Collections.ICollection)
                return ((System.Collections.ICollection)collection).Count;
            return SafeInt(ReflectionHelper.GetMemberValue(collection, "Count", "Length", "size"), 0);
        }

        private static object GetReplayCollectionItem(object collection, int index)
        {
            if (collection == null || index < 0)
                return null;
            try
            {
                Array array = collection as Array;
                if (array != null)
                    return index < array.Length ? array.GetValue(index) : null;
                System.Collections.IList list = collection as System.Collections.IList;
                if (list != null)
                    return index < list.Count ? list[index] : null;

                Type type = collection.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                while (type != null)
                {
                    MethodInfo[] methods = type.GetMethods(flags);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        MethodInfo method = methods[i];
                        if (method.Name != "get_Item" || method.GetParameters().Length != 1)
                            continue;
                        ParameterInfo parameter = method.GetParameters()[0];
                        if (parameter.ParameterType != typeof(int))
                            continue;
                        return method.Invoke(collection, new object[] { index });
                    }
                    type = type.BaseType;
                }
            }
            catch
            {
                // Unsupported IL2CPP list wrappers simply produce an empty task snapshot.
            }
            return null;
        }

        private static float SafeFloat(object value, float fallback)
        {
            try
            {
                if (value == null)
                    return fallback;
                float result = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
                return float.IsNaN(result) || float.IsInfinity(result) ? fallback : result;
            }
            catch { return fallback; }
        }

        private static void WriteFrame(ReplayFrame frame)
        {
            if (_writer == null || frame == null)
                return;

            _writer.Write(EntryFrame);
            _writer.Write(frame.Time);
            _writer.Write((ushort)Math.Min(frame.Players.Count, ushort.MaxValue));

            int count = Math.Min(frame.Players.Count, ushort.MaxValue);
            for (int i = 0; i < count; i++)
            {
                PlayerSnapshot p = frame.Players[i];
                _writer.Write(p.PlayerId);
                _writer.Write(p.Name ?? string.Empty);
                _writer.Write(p.ColorId);
                _writer.Write(p.Role ?? "Unknown");
                byte flags = 0;
                if (p.IsDead) flags |= 1;
                if (p.IsImpostor) flags |= 2;
                if (p.InVent) flags |= 4;
                _writer.Write(flags);
                _writer.Write(p.X);
                _writer.Write(p.Y);
                _writer.Write(p.Z);
                _writer.Write(p.Hat ?? string.Empty);
                _writer.Write(p.Skin ?? string.Empty);
                _writer.Write(p.Visor ?? string.Empty);
                _writer.Write(p.Pet ?? string.Empty);

                // AMR2 extends snapshots with velocity, disconnect state and task states.
                _writer.Write(p.IsDisconnected);
                _writer.Write(p.VelocityX);
                _writer.Write(p.VelocityY);
                int taskCount = Math.Min(p.Tasks.Count, 255);
                _writer.Write((byte)taskCount);
                for (int taskIndex = 0; taskIndex < taskCount; taskIndex++)
                {
                    TaskSnapshot task = p.Tasks[taskIndex];
                    _writer.Write(task.Id);
                    _writer.Write(task.TypeId);
                    _writer.Write(task.IsComplete);
                }
            }
        }

        private static void WriteEvent(string eventName, string actorName, string targetName, string details)
        {
            if (!_isRecording || _writer == null)
                return;

            try
            {
                _writer.Write(EntryEvent);
                _writer.Write(GetRecordingTime());
                _writer.Write(eventName ?? "Event");
                _writer.Write(actorName ?? string.Empty);
                _writer.Write(targetName ?? string.Empty);
                _writer.Write(details ?? string.Empty);
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay event write failed: " + ex.Message);
            }
        }

        private static void StopRecording(string message)
        {
            bool hadRecording = _isRecording;
            _isRecording = false;
            _recordWhenMatchStarts = false;

            try
            {
                if (_writer != null)
                {
                    _writer.Write(EntryEnd);
                    _writer.Write(GetRecordingTime());
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogWarning("Replay footer write failed: " + ex.Message);
            }
            finally
            {
                SafeCloseWriter();
            }

            if (hadRecording)
            {
                Plugin.ModLogger?.LogInfo("Replay saved: " + _recordingPath);
                if (!string.IsNullOrEmpty(message))
                    Plugin.Reply(message + " File: " + Path.GetFileName(_recordingPath));
            }
        }

        private static void SafeCloseWriter()
        {
            try { if (_writer != null) _writer.Dispose(); }
            catch { }
            _writer = null;
        }

        private static void StartPlayback(string name)
        {
            if (_playbackActive || _playbackLoading)
            {
                Plugin.Reply("A replay is already loading or playing. Use /replay stop first.");
                return;
            }

            if (_isRecording)
                StopRecording("Recording saved before replay playback.");

            string path = Path.Combine(GetReplayDirectory(), NormalizeFileName(name) + ".amr");
            if (!File.Exists(path))
            {
                Plugin.Reply("Replay not found: " + Path.GetFileName(path) + ". Replays are stored in the game's replays folder.");
                return;
            }

            ReplayData data;
            try
            {
                data = LoadReplay(path);
                if (data.Frames.Count == 0)
                {
                    Plugin.Reply("This replay has no recorded player frames.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogError("Replay load failed: " + ex);
                Plugin.Reply("Couldn't read this replay. Check LogOutput.log.");
                return;
            }

            _loadedReplay = data;
            _playbackLoading = true;
            _playbackActive = false;
            _playbackPaused = false;
            _playbackTime = 0f;
            _nextChatIndex = 0;
            _nextEventIndex = 0;
            _freeplayButtonClicked = false;
            _mapButtonClicked = false;
            _freeplayStartClicked = false;
            _sawMainMenu = false;
            _localModeClicked = false;
            _localHostClicked = false;
            _localCreateClicked = false;
            _localHostSessionDetected = false;
            _localForceStartRequested = false;
            _localFallbackStartAttempted = false;
            _localLaunchWarningShown = false;
            _localForceStartAttempts = 0;
            _localForceStartLastAttempt = -100f;
            _deferredExitPending = false;
            _awaitingMainMenu = false;
            _deferredExitEarliestFrame = 0;
            _deferredExitNotBefore = 0f;
            _deferredExitDeadline = 0f;
            _awaitMainMenuDeadline = 0f;
            _mainMenuReadyAt = 0f;
            _exitRequestIssued = false;
            _exitTimeoutLogged = false;
            _exitObservedState = string.Empty;
            _exitObservedPlayerCount = -1;
            _exitStateStableSince = 0f;
            _reusePort22023Lobby = false;
            _lobbyActorPreviewCreated = false;
            _waitForManualExitFromOtherLobby = false;
            _playbackLaunchStart = Time.unscaledTime;
            _lastLaunchProbe = -100f;
            _lastPlaybackTick = Time.unscaledTime;
            _lastReplayLoadingStatus = string.Empty;
            LocalCommandChat.HideReplayProgress();

            // Never kick/disconnect the player or force a scene transition from
            // /replay play. The replay stays queued until we can safely render it
            // in a matching local Freeplay game or the verified private host on 22023.
            bool alreadyInLobby = IsCurrentLobbyScene();
            string activeScene = string.Empty;
            try { activeScene = SceneManager.GetActiveScene().name; }
            catch { }
            bool atMainMenu = !string.IsNullOrEmpty(activeScene) &&
                activeScene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;

            if (alreadyInLobby && IsLocalHostOnPort22023())
            {
                _reusePort22023Lobby = true;
                _sawMainMenu = false;
                _deferredExitPending = false;
                _awaitingMainMenu = false;
                SetSelectedMap(data.MapId);
                Plugin.ModLogger?.LogInfo("Replay reusing existing local host lobby on port 22023. No disconnect or network-player spawn will be attempted.");
                Plugin.Reply("Replay loaded. Keeping your local port 22023 lobby. The preview actors are local-only and won't consume server slots; start the correct map when ready.");
            }
            else if (alreadyInLobby)
            {
                _waitForManualExitFromOtherLobby = true;
                _sawMainMenu = false;
                _deferredExitPending = false;
                _awaitingMainMenu = false;
                Plugin.ModLogger?.LogInfo("Replay queued in current lobby; local endpoint was not verified. The session will not be exited automatically.");
                Plugin.Reply("Replay loaded and queued. Your current lobby will not be exited or force-started. Leave normally to the Main Menu when you are ready, and playback will continue there.");
            }
            else if (IsMatchSceneReady() && IsFreeplayGame() && GetMapId() == data.MapId)
            {
                // This is already the intended safe Freeplay context. The old
                // code set _localHostSessionDetected but forgot _sawMainMenu,
                // even though BeginPlaybackInLoadedMap requires both flags. That
                // left playback queued forever at 0% when launched inside Freeplay.
                _sawMainMenu = true;
                _localHostSessionDetected = true;
                Plugin.ModLogger?.LogInfo("Starting state-based replay in the already-running matching Freeplay map; no lobby launch or disconnect needed.");
                BeginPlaybackInLoadedMap();
            }
            else if (atMainMenu)
            {
                _sawMainMenu = true;
                _mainMenuReadyAt = Time.unscaledTime + 1.5f;
                Plugin.ModLogger?.LogInfo("Replay queued from MainMenu. Waiting for the normal Freeplay menu flow; no ExitGame call will be made.");
                Plugin.Reply("Replay loaded. Opening the recorded map through Freeplay; the current session will not be kicked or force-closed.");
            }
            else
            {
                _waitForManualExitFromOtherLobby = true;
                _sawMainMenu = false;
                Plugin.ModLogger?.LogInfo("Replay queued without touching the current session. Scene='" + activeScene + "'.");
                Plugin.Reply("Replay loaded and queued. Your current session is untouched. Return to the Main Menu normally when ready; the replay will resume loading there.");
            }

            Plugin.ModLogger?.LogInfo("Loaded " + (data.FormatVersion >= 2 ? "AMR2 state replay " : "legacy AMR1 state replay ") +
                Path.GetFileName(path) + "; map=" + GetMapToken(data.MapId) +
                "; recorded players=" + GetRecordedRoster().Count + "; frames=" + data.Frames.Count + ".");
        }

        private static ReplayData LoadReplay(string path)
        {
            ReplayData data = new ReplayData();
            using (BinaryReader reader = new BinaryReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                byte[] magic = reader.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != 'A' || magic[1] != 'M' || magic[2] != 'R' ||
                    (magic[3] != '1' && magic[3] != '2'))
                    throw new InvalidDataException("Not a supported AMR replay file.");

                int magicVersion = magic[3] - '0';
                int version = reader.ReadInt32();
                if ((magicVersion == 1 && version != LegacyFileVersion) ||
                    (magicVersion == 2 && version != FileVersion))
                    throw new InvalidDataException("Replay header/version mismatch. Header AMR" + magicVersion + ", version " + version + ".");
                data.FormatVersion = version;

                data.MapId = reader.ReadByte();
                data.FileName = reader.ReadString();
                reader.ReadInt64(); // UTC creation ticks

                bool ended = false;
                while (reader.BaseStream.Position < reader.BaseStream.Length && !ended)
                {
                    byte type = reader.ReadByte();
                    if (type == EntryFrame)
                    {
                        ReplayFrame frame = new ReplayFrame();
                        frame.Time = reader.ReadSingle();
                        ushort count = reader.ReadUInt16();
                        for (int i = 0; i < count; i++)
                        {
                            PlayerSnapshot p = new PlayerSnapshot();
                            p.PlayerId = reader.ReadInt32();
                            p.Name = reader.ReadString();
                            p.ColorId = reader.ReadByte();
                            p.Role = reader.ReadString();
                            byte flags = reader.ReadByte();
                            p.IsDead = (flags & 1) != 0;
                            p.IsImpostor = (flags & 2) != 0;
                            p.InVent = (flags & 4) != 0;
                            p.X = reader.ReadSingle();
                            p.Y = reader.ReadSingle();
                            p.Z = reader.ReadSingle();
                            p.Hat = reader.ReadString();
                            p.Skin = reader.ReadString();
                            p.Visor = reader.ReadString();
                            p.Pet = reader.ReadString();
                            if (version >= 2)
                            {
                                p.IsDisconnected = reader.ReadBoolean();
                                p.VelocityX = reader.ReadSingle();
                                p.VelocityY = reader.ReadSingle();
                                int taskCount = reader.ReadByte();
                                for (int taskIndex = 0; taskIndex < taskCount; taskIndex++)
                                {
                                    TaskSnapshot task = new TaskSnapshot();
                                    task.Id = reader.ReadInt32();
                                    task.TypeId = reader.ReadInt32();
                                    task.IsComplete = reader.ReadBoolean();
                                    p.Tasks.Add(task);
                                }
                            }
                            frame.Players.Add(p);
                        }
                        data.Frames.Add(frame);
                    }
                    else if (type == EntryChat)
                    {
                        ReplayChat chat = new ReplayChat();
                        chat.Time = reader.ReadSingle();
                        chat.PlayerId = reader.ReadInt32();
                        chat.ColorId = reader.ReadByte();
                        chat.PlayerName = reader.ReadString();
                        chat.Message = reader.ReadString();
                        data.Chat.Add(chat);
                    }
                    else if (type == EntryEvent)
                    {
                        ReplayEvent ev = new ReplayEvent();
                        ev.Time = reader.ReadSingle();
                        ev.EventName = reader.ReadString();
                        ev.ActorName = reader.ReadString();
                        ev.TargetName = reader.ReadString();
                        ev.Details = reader.ReadString();
                        data.Events.Add(ev);
                    }
                    else if (type == EntryEnd)
                    {
                        data.Duration = reader.ReadSingle();
                        ended = true;
                    }
                    else
                    {
                        throw new InvalidDataException("Unknown replay entry " + type + ".");
                    }
                }
            }

            if (data.Frames.Count > 0)
                data.Duration = Math.Max(data.Duration, data.Frames[data.Frames.Count - 1].Time);
            if (data.Chat.Count > 0)
                data.Chat.Sort(delegate (ReplayChat a, ReplayChat b) { return a.Time.CompareTo(b.Time); });
            if (data.Events.Count > 0)
                data.Events.Sort(delegate (ReplayEvent a, ReplayEvent b) { return a.Time.CompareTo(b.Time); });
            return data;
        }

        private static void LeaveCurrentGame()
        {
            // This method is called by the chat command handler. Do not call
            // AmongUsClient.ExitGame here: the handler can still be inside the
            // game's chat/network callback. Schedule the exit for a later frame
            // and let TickDeferredExitRequest wait for a stable session state.
            _mainMenuFallbackPending = false;
            _mainMenuFallbackAttempted = false;
            _deferredExitPending = false;
            _awaitingMainMenu = false;
            _exitRequestIssued = false;
            _exitTimeoutLogged = false;
            _exitObservedState = string.Empty;
            _exitObservedPlayerCount = -1;
            _exitStateStableSince = 0f;

            string sceneName = string.Empty;
            try { sceneName = SceneManager.GetActiveScene().name; }
            catch { }

            if (!string.IsNullOrEmpty(sceneName) &&
                sceneName.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _sawMainMenu = true;
                _mainMenuReadyAt = Time.unscaledTime + 1.5f;
                Plugin.ModLogger?.LogInfo(
                    "Replay requested from MainMenu; waiting 1.5 seconds for menu/session objects to settle.");
                return;
            }

            _deferredExitPending = true;
            _deferredExitEarliestFrame = Time.frameCount + 3;
            _deferredExitNotBefore = Time.unscaledTime + 1.25f;
            _deferredExitDeadline = Time.unscaledTime + 20f;
            Plugin.ModLogger?.LogInfo(
                "Replay exit deferred outside the chat callback. Waiting for a stable client state before ExitGame.");
        }

        private static void TickDeferredExitRequest()
        {
            if (!_playbackLoading || _loadedReplay == null)
                return;

            string sceneName = string.Empty;
            try { sceneName = SceneManager.GetActiveScene().name; }
            catch { }

            bool onMainMenu = !string.IsNullOrEmpty(sceneName) &&
                sceneName.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;
            if (onMainMenu)
            {
                _deferredExitPending = false;
                _awaitingMainMenu = false;
                _mainMenuFallbackPending = false;
                _sawMainMenu = true;
                if (_mainMenuReadyAt <= 0f)
                    _mainMenuReadyAt = Time.unscaledTime + 1.5f;
                return;
            }

            if (_deferredExitPending)
            {
                if (Time.frameCount < _deferredExitEarliestFrame ||
                    Time.unscaledTime < _deferredExitNotBefore)
                    return;

                if (Time.unscaledTime >= _deferredExitDeadline)
                {
                    // Do not cancel/discard the already-loaded replay just because
                    // this game build never exposes a stable client state. Keep the
                    // replay armed and wait for the user to return to MainMenu.
                    // TickPlaybackLauncher will continue automatically once the
                    // real MainMenu scene appears.
                    _deferredExitPending = false;
                    _awaitingMainMenu = false;
                    Plugin.ModLogger?.LogWarning(
                        "Replay exit timed out before a stable client state. Replay remains queued; waiting for MainMenu instead of cancelling.");
                    Plugin.Reply(
                        "Replay is still loaded. Return to the Main Menu manually; replay launch will continue automatically when it opens.");
                    return;
                }

                object client = ReflectionHelper.GetSingleton("AmongUsClient");
                object gameState = ReflectionHelper.GetMemberValue(client, "GameState", "gameState");
                string stateName = gameState == null ? string.Empty : gameState.ToString();

                // Never disconnect in the middle of connection/join/disconnect
                // transitions. These are precisely when spawn serialization can
                // still be enumerating child network objects.
                if (string.IsNullOrEmpty(stateName) ||
                    string.Equals(stateName, "Joining", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stateName, "Connecting", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stateName, "Disconnecting", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stateName, "Leaving", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stateName, "Starting", StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.Equals(stateName, "NotJoined", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stateName, "Disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    // We have no stable live session to exit. Do not issue another
                    // ExitGame or force-load a scene; allow the normal menu flow.
                    if (!_exitTimeoutLogged)
                    {
                        _exitTimeoutLogged = true;
                        Plugin.ModLogger?.LogWarning(
                            "Replay launcher sees no joined session yet; waiting for the normal MainMenu transition.");
                    }
                    return;
                }

                // A state name by itself is not enough: the lobby can report
                // Joined while its spawn list is still being populated. Require
                // the state and local player count to stay unchanged for two full
                // seconds before asking the client to tear down the session.
                bool stableState = string.Equals(stateName, "Joined", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stateName, "Started", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stateName, "Ended", StringComparison.OrdinalIgnoreCase);
                if (!stableState || _exitRequestIssued)
                    return;

                int currentPlayerCount = -1;
                try
                {
                    object players = PlayerControl.AllPlayerControls;
                    object count = ReflectionHelper.GetMemberValue(
                        players, "Count", "Length", "size");
                    if (count != null)
                        currentPlayerCount = Convert.ToInt32(count);
                }
                catch { }

                if (!string.Equals(_exitObservedState, stateName, StringComparison.OrdinalIgnoreCase) ||
                    _exitObservedPlayerCount != currentPlayerCount)
                {
                    _exitObservedState = stateName;
                    _exitObservedPlayerCount = currentPlayerCount;
                    _exitStateStableSince = Time.unscaledTime;
                    Plugin.ModLogger?.LogDebug(
                        "Replay launcher is observing stable exit state '" + stateName +
                        "' with player count " + currentPlayerCount + ".");
                    return;
                }

                if (Time.unscaledTime - _exitStateStableSince < 2.0f)
                    return;

                _exitRequestIssued = true;
                Type reasonType = ReflectionHelper.FindType("DisconnectReasons");
                object reason = null;
                if (reasonType != null && reasonType.IsEnum)
                {
                    try { reason = Enum.Parse(reasonType, "ExitGame", true); }
                    catch { }
                }

                bool exitInvoked = false;
                try
                {
                    if (client != null && reason != null)
                        exitInvoked = ReflectionHelper.TryInvokeMethod(
                            client, "ExitGame", new object[] { reason });
                    if (!exitInvoked && client != null)
                        exitInvoked = ReflectionHelper.TryInvokeMethod(
                            client, "ExitGame", new object[0]);
                }
                catch (Exception ex)
                {
                    Plugin.ModLogger?.LogWarning("Deferred replay ExitGame invocation failed: " + ex);
                }

                _deferredExitPending = false;
                if (!exitInvoked)
                {
                    // Keep the replay data and launch state alive. Some game
                    // builds expose no reflection-compatible ExitGame overload;
                    // do not lose the loaded replay just because automation failed.
                    _awaitingMainMenu = false;
                    Plugin.ModLogger?.LogWarning(
                        "This game build exposed no compatible ExitGame method. Replay remains queued while waiting for a manual return to MainMenu.");
                    Plugin.Reply(
                        "Replay is still loaded. Leave this session manually to the Main Menu; playback will start automatically when the menu opens.");
                    return;
                }

                _awaitingMainMenu = true;
                _awaitMainMenuDeadline = Time.unscaledTime + 15f;
                Plugin.ModLogger?.LogInfo(
                    "Deferred ExitGame request accepted after state '" + stateName +
                    "' and player count stayed stable for 2 seconds. Waiting for Among Us to return to MainMenu normally.");
                return;
            }

            if (_awaitingMainMenu && Time.unscaledTime >= _awaitMainMenuDeadline)
            {
                // Never force SceneManager.LoadScene while networking may still
                // own or serialize NetObjects. However, do not cancel the replay:
                // keep it queued until the player exits to MainMenu normally.
                _awaitingMainMenu = false;
                Plugin.ModLogger?.LogWarning(
                    "The normal MainMenu transition timed out. Replay remains queued; forced scene loading was skipped.");
                Plugin.Reply(
                    "Replay has NOT been cancelled. Use Leave/Exit to return to the Main Menu; replay loading will continue automatically when it opens.");
            }
        }

        private static void UpdateReplayLoadingStatus()
        {
            if (!_playbackLoading || _loadedReplay == null)
                return;

            string activeScene = string.Empty;
            try { activeScene = SceneManager.GetActiveScene().name; }
            catch { }

            bool onMainMenu = !string.IsNullOrEmpty(activeScene) &&
                activeScene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;
            string status;

            if (_reusePort22023Lobby)
            {
                if (IsMatchSceneReady())
                {
                    status = GetMapId() == _loadedReplay.MapId
                        ? "Map detected; preparing replay actors..."
                        : "Waiting for the recorded map (" + GetMapToken(_loadedReplay.MapId) + ").";
                }
                else
                {
                    status = "Replay loaded, but still in the port 22023 lobby. For solo playback, leave normally to the Main Menu.";
                }
            }
            else if (_waitForManualExitFromOtherLobby && !onMainMenu)
            {
                status = "Waiting for Main Menu. Use Leave/Exit in-game; this mod will not disconnect or force-start your lobby.";
            }
            else if (!_sawMainMenu && !onMainMenu)
            {
                status = "Replay file loaded. Waiting for you to return to the Main Menu.";
            }
            else if (IsMatchSceneReady() && IsFreeplayGame())
            {
                status = GetMapId() == _loadedReplay.MapId
                    ? "Recorded map loaded; creating local replay actors..."
                    : "Wrong Freeplay map loaded. Return to the map selector and choose " + GetMapToken(_loadedReplay.MapId) + ".";
            }
            else if (!_sawMainMenu || Time.unscaledTime < _mainMenuReadyAt)
            {
                status = "Main Menu detected. Preparing to open Freeplay...";
            }
            else if (!_freeplayButtonClicked)
            {
                status = "Opening Freeplay...";
            }
            else if (!_mapButtonClicked)
            {
                status = "Selecting recorded map: " + GetMapToken(_loadedReplay.MapId) + "...";
            }
            else if (!_freeplayStartClicked)
            {
                status = "Waiting for the Freeplay map selection...";
            }
            else
            {
                status = "Waiting for the recorded map to load: " + GetMapToken(_loadedReplay.MapId) + ".";
            }

            if (!string.Equals(status, _lastReplayLoadingStatus, StringComparison.Ordinal))
            {
                _lastReplayLoadingStatus = status;
                Plugin.ModLogger?.LogInfo("Replay launcher: " + status);
            }
        }

        private static void TickPlaybackLauncher()
        {
            if (_loadedReplay == null)
            {
                _playbackLoading = false;
                _mainMenuFallbackPending = false;
                return;
            }

            UpdateReplayLoadingStatus();

            // If already in a verified local host lobby on port 22023, display
            // the replay roster there without disconnecting. Once the host starts
            // the recorded map, discard the lobby preview and play the timeline.
            if (_reusePort22023Lobby)
            {
                if (IsMatchSceneReady())
                {
                    byte currentMapId = GetMapId();
                    if (currentMapId != _loadedReplay.MapId)
                    {
                        if (!_localLaunchWarningShown)
                        {
                            _localLaunchWarningShown = true;
                            Plugin.ModLogger?.LogWarning("The existing lobby started on map " + GetMapToken(currentMapId) +
                                ", but replay requires " + GetMapToken(_loadedReplay.MapId) + ". Playback was not started on the wrong map.");
                            Plugin.Reply("This lobby started on " + GetMapToken(currentMapId) + ", but the replay uses " +
                                GetMapToken(_loadedReplay.MapId) + ". Stop the round and select the replay's map first.");
                        }
                        return;
                    }
                    _reusePort22023Lobby = false;
                    _lobbyActorPreviewCreated = false;
                    // Reaching this branch means the previously verified local
                    // host has actually entered a map. BeginPlaybackInLoadedMap
                    // uses these guards to prevent unsafe scene initialization;
                    // mark this known-safe loaded-map context explicitly.
                    _sawMainMenu = true;
                    _localHostSessionDetected = true;
                    BeginPlaybackInLoadedMap();
                    return;
                }

                if (IsCurrentLobbyScene())
                {
                    if (!_lobbyActorPreviewCreated && Time.unscaledTime - _playbackLaunchStart > 0.75f)
                        SpawnReplayActorsInExistingLobby();
                    return;
                }

                string reuseScene = string.Empty;
                try { reuseScene = SceneManager.GetActiveScene().name; }
                catch { }
                if (!string.IsNullOrEmpty(reuseScene) &&
                    reuseScene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _reusePort22023Lobby = false;
                    _lobbyActorPreviewCreated = false;
                    ClearActors();
                    _sawMainMenu = true;
                    _mainMenuReadyAt = Time.unscaledTime + 1.5f;
                }
                else
                {
                    return;
                }
            }

            if (_waitForManualExitFromOtherLobby)
            {
                string waitingScene = string.Empty;
                try { waitingScene = SceneManager.GetActiveScene().name; }
                catch { }
                if (string.IsNullOrEmpty(waitingScene) ||
                    waitingScene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                _waitForManualExitFromOtherLobby = false;
                _sawMainMenu = true;
                _mainMenuReadyAt = Time.unscaledTime + 1.5f;
                Plugin.ModLogger?.LogInfo("MainMenu detected after manual lobby exit. Continuing the queued replay launch.");
            }

            string activeScene = string.Empty;
            try { activeScene = SceneManager.GetActiveScene().name; }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Could not read active scene during replay launch: " + ex.Message);
            }

            bool onMainMenu = !string.IsNullOrEmpty(activeScene) &&
                activeScene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;

            if (onMainMenu)
            {
                if (!_sawMainMenu)
                {
                    _mainMenuReadyAt = Time.unscaledTime + 1.5f;
                    Plugin.ModLogger?.LogInfo(
                        "MainMenu reached. Waiting 1.5 seconds for network objects and scene transitions to settle before opening Freeplay.");
                }
                _sawMainMenu = true;
                _mainMenuFallbackPending = false;
                _awaitingMainMenu = false;
            }

            // Important crash fix: do NOT create a LAN/online host for playback.
            // The client's spawn serializer expects real registered NetObjects,
            // and replay-only actors are not network players. Launch the real map
            // through local Freeplay, then draw replay actors from SpriteRenderers
            // only. This avoids the "child net obj ... now null during serialize" path.
            // Observe the actual game state instead of requiring our menu-button
            // click hook to have succeeded. This also supports the manual fallback
            // offered below when this game's menu hierarchy differs from the one
            // the reflection-based button finder expects.
            if (_sawMainMenu && IsMatchSceneReady() && IsFreeplayGame() &&
                Time.unscaledTime - _playbackLaunchStart > 0.5f)
            {
                byte activeMapId = GetMapId();
                if (activeMapId == _loadedReplay.MapId)
                {
                    _localHostSessionDetected = true;
                    BeginPlaybackInLoadedMap();
                    return;
                }

                if (!_localLaunchWarningShown)
                {
                    _localLaunchWarningShown = true;
                    Plugin.ModLogger?.LogWarning("Freeplay loaded map " + GetMapToken(activeMapId) +
                        ", but replay requires " + GetMapToken(_loadedReplay.MapId) + ". Waiting for the correct map.");
                    Plugin.Reply("Freeplay is on " + GetMapToken(activeMapId) + ". The replay needs " +
                        GetMapToken(_loadedReplay.MapId) + "; return to the Freeplay map selector and choose that map.");
                }
            }

            if (Time.unscaledTime - _lastLaunchProbe < 0.5f)
                return;
            _lastLaunchProbe = Time.unscaledTime;

            if (!_sawMainMenu)
                return;

            // Do not invoke menu buttons in the same frame that MainMenu appears.
            // Among Us may still be cleaning up InnerNetClient/NetObject instances.
            if (Time.unscaledTime < _mainMenuReadyAt)
                return;

            SetSelectedMap(_loadedReplay.MapId);

            if (!_freeplayButtonClicked)
            {
                bool opened = TryClickMenuTarget("freeplay", string.Empty);
                if (!opened)
                    opened = TryClickMenuTarget("free play", string.Empty);
                if (!opened)
                    opened = TryClickMenuTarget("practice", string.Empty);

                if (opened)
                {
                    _freeplayButtonClicked = true;
                    Plugin.ModLogger?.LogInfo("Replay launcher selected Freeplay; no network host is created.");
                    return;
                }
            }
            else if (!_mapButtonClicked)
            {
                string[] mapTokens;
                switch (_loadedReplay.MapId)
                {
                    case 0: mapTokens = new string[] { "skeld" }; break;
                    case 1: mapTokens = new string[] { "mira", "mirahq" }; break;
                    case 2: mapTokens = new string[] { "polus" }; break;
                    case 3: mapTokens = new string[] { "dleks" }; break;
                    case 4: mapTokens = new string[] { "airship" }; break;
                    case 5: mapTokens = new string[] { "fungle" }; break;
                    default: mapTokens = new string[] { GetMapToken(_loadedReplay.MapId).ToLowerInvariant() }; break;
                }

                bool selectedMap = false;
                for (int i = 0; i < mapTokens.Length && !selectedMap; i++)
                {
                    selectedMap = TryClickMenuTarget(mapTokens[i], string.Empty);
                }
                if (selectedMap)
                {
                    _mapButtonClicked = true;
                    Plugin.ModLogger?.LogInfo("Replay launcher selected map " + GetMapToken(_loadedReplay.MapId) + " in Freeplay.");
                    return;
                }
                // Don't mark the map as selected when no matching button was
                // found. Keep probing at the normal 0.5-second interval so a
                // transient/late-created menu hierarchy can still be detected.
            }
            else if (!_freeplayStartClicked)
            {
                bool started = TryClickMenuTarget("start", "button");
                if (!started)
                    started = TryClickMenuTarget("play", "button");
                if (started)
                {
                    _freeplayStartClicked = true;
                    Plugin.ModLogger?.LogInfo("Replay launcher confirmed Freeplay map selection.");
                    return;
                }
                // Avoid hammering the UI every frame if this build launches the
                // selected map directly and exposes no separate Start button.
                _freeplayStartClicked = true;
            }

            if (Time.unscaledTime - _playbackLaunchStart > 20f && !_localLaunchWarningShown)
            {
                _localLaunchWarningShown = true;
                Plugin.ModLogger?.LogWarning(
                    "Replay Freeplay launcher could not identify the map transition. " +
                    "No online/LAN host was created; use the game's Freeplay menu manually.");
                Plugin.Reply(
                    "Couldn't automatically finish opening Freeplay on this build. Open Freeplay and select " +
                    GetMapToken(_loadedReplay.MapId) + "; replay playback will start when the map loads.");
            }
        }

        private static bool IsCurrentLobbyScene()
        {
            try
            {
                if (PlayerControl.LocalPlayer == null ||
                    ReflectionHelper.GetSingleton("GameStartManager") == null ||
                    ReflectionHelper.GetSingleton("ShipStatus") != null)
                    return false;

                object client = ReflectionHelper.GetSingleton("AmongUsClient");
                object state = ReflectionHelper.GetMemberValue(client, "GameState", "gameState");
                return state == null || string.Equals(state.ToString(), "Joined", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay lobby detection failed: " + ex.Message);
                return false;
            }
        }

        private static bool IsLocalHostOnPort22023()
        {
            try
            {
                object client = ReflectionHelper.GetSingleton("AmongUsClient");
                if (client == null || !SafeBool(ReflectionHelper.GetMemberValue(client, "AmHost", "amHost")))
                    return false;

                List<object> candidates = new List<object>();
                candidates.Add(client);
                object serverManager = ReflectionHelper.GetSingleton("ServerManager");
                if (serverManager != null)
                {
                    object currentServer = ReflectionHelper.GetMemberValue(serverManager,
                        "CurrentServer", "currentServer", "CurrentServerInfo", "currentServerInfo", "TargetServer", "targetServer");
                    if (currentServer != null) candidates.Add(currentServer);

                    object currentRegion = ReflectionHelper.GetMemberValue(serverManager,
                        "CurrentRegion", "currentRegion", "TargetRegion", "targetRegion");
                    if (currentRegion != null) candidates.Add(currentRegion);
                }

                for (int i = 0; i < candidates.Count; i++)
                {
                    string address;
                    int port;
                    if (!TryGetActiveServerEndpoint(candidates[i], out address, out port))
                        continue;

                    bool isLocal = IsLocalAddress(address);
                    Plugin.ModLogger?.LogDebug("Replay active endpoint candidate: " + address + ":" + port + "; isLocal=" + isLocal + ".");
                    if (port == 22023 && isLocal)
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Could not verify local 22023 server: " + ex.Message);
                return false;
            }
        }

        private static bool TryGetActiveServerEndpoint(object source, out string address, out int port)
        {
            address = string.Empty;
            port = -1;
            if (source == null)
                return false;

            object p = ReflectionHelper.GetMemberValue(source,
                "ServerPort", "serverPort", "CurrentPort", "currentPort", "Port", "port", "NetworkPort", "networkPort");
            if (p != null)
            {
                try { port = Convert.ToInt32(p); }
                catch { port = -1; }
            }

            object a = ReflectionHelper.GetMemberValue(source,
                "ServerAddress", "serverAddress", "NetworkAddress", "networkAddress", "Address", "address",
                "Ip", "ip", "Host", "host", "Hostname", "hostname", "RemoteEndPoint", "remoteEndPoint",
                "EndPoint", "endPoint", "CurrentServerAddress", "currentServerAddress");
            if (a != null)
                address = Convert.ToString(a) ?? string.Empty;

            int parsedPort;
            if (port < 0 && TryParsePortFromText(address, out parsedPort))
                port = parsedPort;

            if (port < 0 || string.IsNullOrWhiteSpace(address))
            {
                object endpoint = ReflectionHelper.GetMemberValue(source,
                    "Connection", "connection", "ServerConnection", "serverConnection", "Transport", "transport",
                    "NetTransport", "netTransport", "CurrentServer", "currentServer", "ServerInfo", "serverInfo",
                    "CurrentRegion", "currentRegion", "Server", "server", "Endpoint", "endpoint");
                if (endpoint != null && !ReferenceEquals(endpoint, source))
                {
                    object nestedPort = ReflectionHelper.GetMemberValue(endpoint,
                        "ServerPort", "serverPort", "CurrentPort", "currentPort", "Port", "port", "NetworkPort", "networkPort");
                    if (port < 0 && nestedPort != null)
                    {
                        try { port = Convert.ToInt32(nestedPort); }
                        catch { }
                    }
                    object nestedAddress = ReflectionHelper.GetMemberValue(endpoint,
                        "ServerAddress", "serverAddress", "NetworkAddress", "networkAddress", "Address", "address",
                        "Ip", "ip", "Host", "host", "Hostname", "hostname", "RemoteEndPoint", "remoteEndPoint",
                        "EndPoint", "endPoint");
                    if (string.IsNullOrWhiteSpace(address) && nestedAddress != null)
                        address = Convert.ToString(nestedAddress) ?? string.Empty;
                    if (port < 0 && TryParsePortFromText(address, out parsedPort))
                        port = parsedPort;
                }
            }

            return port >= 0 && !string.IsNullOrWhiteSpace(address);
        }

        private static bool TryParsePortFromText(string text, out int port)
        {
            port = -1;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string value = text.Trim().TrimEnd('/');
            int colon = value.LastIndexOf(':');
            if (colon < 0 || colon + 1 >= value.Length)
                return false;
            string suffix = value.Substring(colon + 1).TrimEnd(']', ' ', '\t');
            int parsed;
            if (!int.TryParse(suffix, out parsed) || parsed <= 0 || parsed > 65535)
                return false;
            port = parsed;
            return true;
        }

        private static bool IsLocalAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return false;
            string value = address.Trim().ToLowerInvariant();
            if (value.Contains("://"))
            {
                int scheme = value.IndexOf("://", StringComparison.Ordinal);
                value = value.Substring(scheme + 3);
            }
            int slash = value.IndexOf('/');
            if (slash >= 0) value = value.Substring(0, slash);
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closeBracket = value.IndexOf(']');
                if (closeBracket > 0)
                    value = value.Substring(1, closeBracket - 1);
            }
            else
            {
                int colon = value.LastIndexOf(':');
                if (colon > 0 && value.IndexOf(':') == colon)
                    value = value.Substring(0, colon);
            }
            value = value.Trim('[', ']', ' ');

            if (value == "localhost" || value == "::1" || value == "0:0:0:0:0:0:0:1" || value == "0.0.0.0")
                return true;
            if (value.StartsWith("127.", StringComparison.Ordinal) ||
                value.StartsWith("10.", StringComparison.Ordinal) ||
                value.StartsWith("192.168.", StringComparison.Ordinal) ||
                value.StartsWith("fc", StringComparison.Ordinal) ||
                value.StartsWith("fd", StringComparison.Ordinal))
                return true;
            if (value.StartsWith("172.", StringComparison.Ordinal))
            {
                string[] parts = value.Split('.');
                int second;
                if (parts.Length > 1 && int.TryParse(parts[1], out second) && second >= 16 && second <= 31)
                    return true;
            }
            return false;
        }

        private static void SpawnReplayActorsInExistingLobby()
        {
            try
            {
                PlayerControl local = PlayerControl.LocalPlayer;
                if (local == null || local.transform == null || _loadedReplay == null)
                    return;

                List<PlayerSnapshot> roster = GetRecordedRoster();
                if (roster.Count == 0)
                {
                    Plugin.Reply("This replay has no recorded players to preview in the lobby.");
                    _lobbyActorPreviewCreated = true;
                    return;
                }

                ClearActors();
                Vector3 center = local.transform.position;
                const int columns = 5;
                const float horizontalSpacing = 0.62f;
                const float verticalSpacing = 0.62f;
                int created = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    PlayerSnapshot snapshot = roster[i];
                    ReplayActor actor = EnsureActor(snapshot);
                    if (actor == null || actor.Root == null)
                        continue;
                    int column = i % columns;
                    int row = i / columns;
                    float x = (column - (columns - 1) / 2f) * horizontalSpacing;
                    float y = 0.9f + row * verticalSpacing;
                    actor.Root.transform.position = new Vector3(center.x + x, center.y + y, center.z + ActorZ);
                    actor.Root.transform.rotation = local.transform.rotation;
                    actor.Root.SetActive(true);
                    created++;
                }

                _lobbyActorPreviewCreated = true;
                Plugin.ModLogger?.LogInfo("Created " + created + " local visual replay actor(s) in the existing port 22023 lobby; no network PlayerControl or NetObject was spawned.");
                Plugin.Reply("Placed " + created + " recorded actor(s) in this lobby for local preview. They do not count as real server slots. Start the match on the replay map to begin playback.");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogError("Could not place replay actors in existing lobby: " + ex);
                _lobbyActorPreviewCreated = true;
                Plugin.Reply("Could not place replay actor previews in the lobby. Check LogOutput.log.");
            }
        }

        private static bool IsLocalHostLobbyReady()
        {
            try
            {
                if (PlayerControl.LocalPlayer == null)
                    return false;

                object client = ReflectionHelper.GetSingleton("AmongUsClient");
                if (client == null || !SafeBool(ReflectionHelper.GetMemberValue(client, "AmHost", "amHost")))
                    return false;

                object manager = ReflectionHelper.GetSingleton("GameStartManager");
                if (manager == null)
                    return false;

                // Don't mistake Freeplay or an already-running match for a lobby.
                if (ReflectionHelper.GetSingleton("ShipStatus") != null)
                    return false;

                object state = ReflectionHelper.GetMemberValue(client, "GameState", "gameState");
                if (state != null && !string.Equals(state.ToString(), "Joined", StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Local replay host-lobby detection failed: " + ex.Message);
                return false;
            }
        }

        private static void RequestLocalForceStart()
        {
            _localForceStartLastAttempt = Time.unscaledTime;
            _localForceStartAttempts++;

            object client = ReflectionHelper.GetSingleton("AmongUsClient");
            object manager = ReflectionHelper.GetSingleton("GameStartManager");
            if (manager == null || client == null ||
                !SafeBool(ReflectionHelper.GetMemberValue(client, "AmHost", "amHost")))
            {
                Plugin.ModLogger?.LogWarning("Replay force-start skipped: local host manager/client wasn't ready.");
                return;
            }

            SetSelectedMap(_loadedReplay.MapId);

            // Relax the lobby's player-count requirement before invoking the
            // game's own BeginGame method. Do not set Countdown before this
            // call: in many builds BeginGame toggles an active countdown off.
            bool minPlayersSet = ReflectionHelper.SetMemberValue(
                manager, 1, "MinPlayers", "minPlayers");

            object optionsManager = ReflectionHelper.GetSingleton("GameOptionsManager");
            object options = ReflectionHelper.GetMemberValue(
                optionsManager, "CurrentGameOptions", "currentGameOptions");
            if (options != null)
            {
                ReflectionHelper.SetMemberValue(options, _loadedReplay.MapId, "MapId", "mapId");
                // Keep the configured lobby capacity aligned with the replay's
                // maximum 15 recorded player visuals where the member is writable.
                ReflectionHelper.SetMemberValue(options, 15, "MaxPlayers", "maxPlayers");
                ReflectionHelper.SetMemberValue(optionsManager, options,
                    "CurrentGameOptions", "currentGameOptions");
            }

            bool startInvoked = ReflectionHelper.TryInvokeMethod(
                manager, "BeginGame", new object[0]);
            if (!startInvoked)
                startInvoked = TryClickMenuTarget("start", "button");
            if (!startInvoked)
                startInvoked = TryClickMenuTarget("start", string.Empty);

            // Force the existing GameStartManager.Update countdown path AFTER
            // BeginGame has been given its chance, so a solo replay lobby starts
            // quickly instead of waiting for extra people or toggling Cancel.
            bool timerSet = ReflectionHelper.SetMemberValue(
                manager, 0.15f, "countDownTimer", "CountDownTimer");
            bool stateSet = ReflectionHelper.SetMemberValue(
                manager, "Countdown", "startState", "StartState");

            if ((!timerSet || !stateSet) && !startInvoked)
            {
                // Last resort for builds where the countdown fields were renamed.
                startInvoked = ReflectionHelper.TryInvokeMethod(
                    manager, "FinallyBegin", new object[0]) || startInvoked;
            }

            ReflectionHelper.SetObjectActive(
                ReflectionHelper.GetMemberValue(manager, "GameStartTextParent", "gameStartTextParent"),
                true);

            Plugin.ModLogger?.LogInfo(
                "Replay local force-start attempt " + _localForceStartAttempts +
                ": MinPlayers=" + minPlayersSet +
                ", countdown=" + timerSet +
                ", startState=" + stateSet +
                ", start-call=" + startInvoked +
                ", map=" + GetMapToken(_loadedReplay.MapId) + ".");

            _localForceStartRequested = startInvoked || minPlayersSet || timerSet || stateSet;
            if (_localForceStartRequested)
            {
                Plugin.Reply("Replay host detected. Starting the one-player match on " +
                    GetMapToken(_loadedReplay.MapId) + ". The recorded players will be replayed locally.");
            }
            else
            {
                Plugin.ModLogger?.LogWarning("Replay force-start could not find a compatible start method or countdown field.");
            }
        }

        private static void ForceLocalCountdownFallback()
        {
            _localFallbackStartAttempted = true;

            // This fallback is only reached while loading a replay and only in
            // a host lobby created for that replay. Never auto-start an unrelated
            // public/friends lobby.
            if (_loadedReplay == null || !IsLocalHostLobbyReady())
                return;

            object manager = ReflectionHelper.GetSingleton("GameStartManager");
            if (manager == null)
                return;

            SetSelectedMap(_loadedReplay.MapId);
            ReflectionHelper.SetMemberValue(manager, 1, "MinPlayers", "minPlayers");
            bool timerSet = ReflectionHelper.SetMemberValue(
                manager, 0.05f, "countDownTimer", "CountDownTimer");
            bool stateSet = ReflectionHelper.SetMemberValue(
                manager, "Countdown", "startState", "StartState");

            bool finalStartInvoked = false;
            if (!timerSet || !stateSet)
            {
                finalStartInvoked = ReflectionHelper.TryInvokeMethod(
                    manager, "FinallyBegin", new object[0]);
            }

            Plugin.ModLogger?.LogWarning(
                "Replay start fallback applied: countdown=" + timerSet +
                ", state=" + stateSet + ", FinallyBegin=" + finalStartInvoked + ".");

            if (!timerSet && !stateSet && !finalStartInvoked)
            {
                Plugin.Reply("The lobby opened, but this game build rejected the automatic start. Check BepInEx/LogOutput.log.");
            }
        }

        private static bool TryClickMenuTarget(string token, string requiredExtra)
        {
            // IMPORTANT: never invoke arbitrary OnClick/Click methods discovered
            // through reflection. The old implementation scanned every component
            // and could execute an unrelated menu action (including Exit/Quit).
            // Only inspect active GameObjects and invoke the onClick event of a
            // button-like component attached to the matching object.
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                    return false;

                UnityEngine.SceneManagement.Scene activeScene = SceneManager.GetActiveScene();
                GameObject[] objects = Resources.FindObjectsOfTypeAll<GameObject>();
                string tokenLower = token.ToLowerInvariant();
                string extraLower = (requiredExtra ?? string.Empty).ToLowerInvariant();

                for (int i = 0; i < objects.Length; i++)
                {
                    GameObject go = objects[i];
                    if (go == null || !go.activeInHierarchy)
                        continue;

                    // Ignore hidden prefab assets and objects from non-active scenes.
                    string sceneName = string.Empty;
                    try { sceneName = go.scene.name; }
                    catch { }
                    if (!string.Equals(sceneName, activeScene.name, StringComparison.Ordinal) &&
                        !string.Equals(sceneName, "DontDestroyOnLoad", StringComparison.Ordinal))
                        continue;

                    string lower = (go.name ?? string.Empty).ToLowerInvariant();
                    if (lower.IndexOf(tokenLower, StringComparison.Ordinal) < 0)
                        continue;
                    if (!string.IsNullOrEmpty(extraLower) &&
                        lower.IndexOf(extraLower, StringComparison.Ordinal) < 0)
                        continue;

                    List<Component> components = GetKnownButtonComponents(go);
                    for (int j = 0; j < components.Count; j++)
                    {
                        Component component = components[j];
                        if (component == null || !IsButtonLike(component))
                            continue;

                        if (TryInvokeClick(component))
                        {
                            Plugin.ModLogger?.LogInfo(
                                "Replay launcher invoked onClick for button '" + go.name +
                                "' (component " + component.GetType().Name + ").");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogWarning("Safe replay menu button search failed: " + ex);
            }
            return false;
        }

        private static List<Component> GetKnownButtonComponents(GameObject target)
        {
            List<Component> result = new List<Component>();
            if (target == null)
                return result;

            string[] names = new string[] { "PassiveButton", "Button", "MultiTouchButton" };
            for (int i = 0; i < names.Length; i++)
            {
                Type type = ReflectionHelper.FindType(names[i]);
                if (type == null)
                    continue;
                try
                {
                    Component component = GetComponentCompatible(target, type);
                    if (component != null && !result.Contains(component))
                        result.Add(component);
                }
                catch { }
            }
            return result;
        }

        private static bool IsButtonLike(Component component)
        {
            if (component == null)
                return false;

            string name = component.GetType().Name;
            return string.Equals(name, "Button", StringComparison.Ordinal) ||
                   string.Equals(name, "PassiveButton", StringComparison.Ordinal) ||
                   string.Equals(name, "MultiTouchButton", StringComparison.Ordinal) ||
                   name.EndsWith("Button", StringComparison.Ordinal);
        }

        private static bool TryInvokeClick(Component component)
        {
            if (component == null || !IsButtonLike(component))
                return false;

            try
            {
                // Invoke only the component's UnityEvent. Do not directly call
                // arbitrary methods named OnClick/Click on game controllers.
                object clickEvent = ReflectionHelper.GetMemberValue(
                    component, "onClick", "OnClick", "clickEvent", "ClickEvent");
                return clickEvent != null &&
                    ReflectionHelper.TryInvokeMethod(clickEvent, "Invoke", new object[0]);
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Button event invocation skipped: " + ex.Message);
                return false;
            }
        }

        private static void SetSelectedMap(byte mapId)
        {
            try
            {
                object manager = ReflectionHelper.GetSingleton("GameOptionsManager");
                object options = ReflectionHelper.GetMemberValue(manager, "CurrentGameOptions", "currentGameOptions");
                if (options != null)
                {
                    ReflectionHelper.SetMemberValue(options, mapId, "MapId", "mapId");
                    ReflectionHelper.SetMemberValue(manager, options, "CurrentGameOptions", "currentGameOptions");
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Could not preselect replay map: " + ex.Message);
            }
        }

        private static void BeginPlaybackInLoadedMap()
        {
            try
            {
                PlayerControl local = PlayerControl.LocalPlayer;
                if (local == null || _loadedReplay == null || !_sawMainMenu || !_localHostSessionDetected)
                {
                    Plugin.ModLogger?.LogWarning(
                        "Replay playback begin skipped because the local replay host/map transition was not confirmed.");
                    return;
                }

                ClearActors();
                _spectator = local;
                _savedLocalDead = SafeBool(ReflectionHelper.GetMemberValue(local.Data, "IsDead", "isDead"));
                _localWasDead = _savedLocalDead;
                _savedLocalMoveable = SafeBool(ReflectionHelper.GetMemberValue(local, "moveable", "Moveable"));

                ReplayFrame firstFrame = _loadedReplay.Frames[0];

                // Build the roster from every frame, not just frame zero. This
                // preserves all 15 recorded players even if one is missing from
                // an early frame due to a recording/version quirk. Actors that
                // are not present at the current timestamp remain inactive until
                // their first recorded appearance. The current local player is a
                // separate 16th ghost spectator and is never counted as a replay
                // actor or sent to a network lobby.
                List<PlayerSnapshot> recordedRoster = GetRecordedRoster();
                for (int i = 0; i < recordedRoster.Count; i++)
                    EnsureActor(recordedRoster[i]);

                // Local-only ghost spectator: do not send RPCs or join a lobby.
                // Start free-spectating at the local ghost, not attached to the first
                // recorded player. /haunt <name> is an explicit camera-follow command.
                _hauntedPlayerId = -1;
                EnforceGhostSpectatorState();

                _playbackActive = true;
                _playbackLoading = false;
                _playbackPaused = false;
                _playbackTime = 0f;
                _lastPlaybackTick = Time.unscaledTime;
                _nextChatIndex = 0;
                _nextEventIndex = 0;
                ApplyPlaybackFrame(0f);
                ApplyHauntCameraTarget();
                EnforceGhostSpectatorState();
                int recordedPlayerCount = recordedRoster.Count;
                ShowReplayChat("Replay loaded on " + GetMapToken(_loadedReplay.MapId) +
                    ". Recorded players: " + recordedPlayerCount +
                    ". Free spectate is enabled: use normal movement controls. Use /haunt <player> to follow someone, /haunt off to return to free movement, /roles, /pause, /resume, /length, /time mm:ss, or /replay stop.");
                Plugin.Reply("Replay started: " + recordedPlayerCount +
                    " recorded player(s) loaded. Move freely with normal controls; use /haunt <player> to follow someone.");
                Plugin.ModLogger?.LogInfo("In-world replay started. Recorded roster: " + recordedPlayerCount +
                    ", actors created: " + Actors.Count + ", frames: " + _loadedReplay.Frames.Count +
                    ". Solo launch succeeded without waiting for a lobby player count.");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogError("Couldn't begin in-world replay: " + ex);
                _playbackActive = false;
                _playbackLoading = false;
                Plugin.Reply("Couldn't create replay actors on this build. Check LogOutput.log.");
            }
        }

        private static void TickPlayback()
        {
            if (_loadedReplay == null || !IsMatchSceneReady())
            {
                StopPlayback(true);
                return;
            }

            float now = Time.unscaledTime;
            if (!_playbackPaused)
                _playbackTime = Mathf.Min(_loadedReplay.Duration, _playbackTime + Mathf.Max(0f, now - _lastPlaybackTick));
            _lastPlaybackTick = now;

            // Do not refresh native HUD popups while playback advances. The game popup
            // has no stable version-independent hide API and periodic ShowPopUp calls
            // leave "Replay Playing" visible/restarting forever on some builds.

            // Keep the local player in local ghost state but leave movement enabled.
            // The replay is entirely client-local and does not send gameplay RPCs.
            EnforceGhostSpectatorState();
            ApplyPlaybackFrame(_playbackTime);
            ReplayChatDue();
            ReplayEventsDue();

            if (_playbackTime >= _loadedReplay.Duration && _loadedReplay.Duration > 0f && !_playbackPaused)
            {
                _playbackPaused = true;
                ShowReplayChat("Replay finished. Use /time 0 to watch again or /replay stop to exit replay mode.");
            }
        }

        private static void ApplyPlaybackFrame(float time)
        {
            if (_loadedReplay == null || _loadedReplay.Frames.Count == 0)
                return;

            int leftIndex = 0;
            while (leftIndex + 1 < _loadedReplay.Frames.Count && _loadedReplay.Frames[leftIndex + 1].Time <= time)
                leftIndex++;
            int rightIndex = Math.Min(leftIndex + 1, _loadedReplay.Frames.Count - 1);
            ReplayFrame left = _loadedReplay.Frames[leftIndex];
            ReplayFrame right = _loadedReplay.Frames[rightIndex];
            float span = right.Time - left.Time;
            float blend = span <= 0.0001f ? 0f : Mathf.Clamp01((time - left.Time) / span);

            // Track roster membership and change active state only on a real visibility
            // transition. Disabling every actor and re-enabling it every frame causes
            // visible flicker and animator/cosmetics reinitialization glitches.
            HashSet<int> playersPresentThisFrame = new HashSet<int>();
            for (int i = 0; i < left.Players.Count; i++)
            {
                PlayerSnapshot current = left.Players[i];
                if (current == null)
                    continue;
                playersPresentThisFrame.Add(current.PlayerId);
                PlayerSnapshot next = FindPlayer(right, current.PlayerId);
                if (next == null)
                    next = current;

                ReplayActor actor = EnsureActor(current);
                if (actor == null || actor.Root == null)
                    continue;

                Vector3 from = new Vector3(current.X, current.Y, current.Z + ActorZ);
                Vector3 to = new Vector3(next.X, next.Y, next.Z + ActorZ);
                actor.Root.transform.position = Vector3.Lerp(from, to, blend);
                bool shouldBeVisible = !current.InVent && !current.IsDisconnected;
                if (actor.Root.activeSelf != shouldBeVisible)
                    actor.Root.SetActive(shouldBeVisible);
                actor.Name = current.Name;
                actor.Role = current.Role;
                ApplyActorAppearance(actor, current);
            }

            foreach (KeyValuePair<int, ReplayActor> pair in Actors)
            {
                ReplayActor actor = pair.Value;
                if (actor == null || actor.Root == null || playersPresentThisFrame.Contains(pair.Key))
                    continue;
                if (actor.Root.activeSelf)
                    actor.Root.SetActive(false);
            }

            ApplyHauntCameraTarget();
        }

        private static List<PlayerSnapshot> GetRecordedRoster()
        {
            List<PlayerSnapshot> roster = new List<PlayerSnapshot>();
            if (_loadedReplay == null)
                return roster;

            HashSet<int> seenPlayerIds = new HashSet<int>();
            for (int frameIndex = 0; frameIndex < _loadedReplay.Frames.Count; frameIndex++)
            {
                ReplayFrame frame = _loadedReplay.Frames[frameIndex];
                if (frame == null)
                    continue;

                for (int playerIndex = 0; playerIndex < frame.Players.Count; playerIndex++)
                {
                    PlayerSnapshot snapshot = frame.Players[playerIndex];
                    if (snapshot == null || snapshot.PlayerId < 0 ||
                        !seenPlayerIds.Add(snapshot.PlayerId))
                    {
                        continue;
                    }

                    // The first snapshot is the correct initial transform for
                    // this recorded player. Their actor remains hidden until a
                    // frame containing them is played.
                    roster.Add(snapshot);
                }
            }

            return roster;
        }

        private static void EnforceGhostSpectatorState()
        {
            PlayerControl spectator = _spectator != null ? _spectator : PlayerControl.LocalPlayer;
            if (spectator == null)
                return;

            try
            {
                if (spectator.Data != null)
                    ReflectionHelper.SetMemberValue(spectator.Data, true, "IsDead", "isDead");
                ReflectionHelper.SetMemberValue(spectator, true, "isDead", "IsDead");
                ReflectionHelper.SetMemberValue(spectator, true, "moveable", "Moveable");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Could not enforce local replay ghost state: " + ex.Message);
            }
        }

        private static void HandleHauntCommand(string arguments)
        {
            if (_loadedReplay == null || !_playbackActive)
            {
                Plugin.Reply("The replay is not ready yet.");
                return;
            }

            string target = (arguments ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                string currentName = GetHauntedActorName();
                Plugin.Reply(string.IsNullOrEmpty(currentName)
                    ? "Usage: /haunt <player name|next|previous|off>"
                    : "Haunting " + currentName + ". Use /haunt next, /haunt previous or /haunt off.");
                return;
            }

            if (string.Equals(target, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, "stop", StringComparison.OrdinalIgnoreCase))
            {
                _hauntedPlayerId = -1;
                ApplyHauntCameraTarget();
                Plugin.Reply("Haunt camera released. Free ghost movement restored; use normal movement controls to spectate.");
                return;
            }

            if (string.Equals(target, "next", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, "previous", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, "prev", StringComparison.OrdinalIgnoreCase))
            {
                List<PlayerSnapshot> players = GetPlayersAtPlaybackTime();
                if (players.Count == 0)
                {
                    Plugin.Reply("No recorded players are available at this timestamp.");
                    return;
                }

                int currentIndex = -1;
                for (int i = 0; i < players.Count; i++)
                    if (players[i].PlayerId == _hauntedPlayerId) { currentIndex = i; break; }

                int step = string.Equals(target, "next", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
                int newIndex = currentIndex < 0
                    ? (step > 0 ? 0 : players.Count - 1)
                    : (currentIndex + step + players.Count) % players.Count;
                _hauntedPlayerId = players[newIndex].PlayerId;
                ApplyHauntCameraTarget();
                Plugin.Reply("Now haunting " + players[newIndex].Name + " (" + players[newIndex].Role + ").");
                return;
            }

            List<PlayerSnapshot> currentPlayers = GetPlayersAtPlaybackTime();
            PlayerSnapshot match = null;
            for (int i = 0; i < currentPlayers.Count; i++)
            {
                if (string.Equals(currentPlayers[i].Name, target, StringComparison.OrdinalIgnoreCase))
                {
                    match = currentPlayers[i];
                    break;
                }
            }

            if (match == null)
            {
                Plugin.Reply("Couldn't find recorded player '" + target + "'. Use /roles to list player names and roles.");
                return;
            }

            _hauntedPlayerId = match.PlayerId;
            ApplyHauntCameraTarget();
            Plugin.Reply("Now haunting " + match.Name + " | Role: " + match.Role + ". Use /haunt off to release the camera.");
        }

        private static int CountCompletedTasks(PlayerSnapshot player)
        {
            if (player == null || player.Tasks == null)
                return 0;
            int count = 0;
            for (int i = 0; i < player.Tasks.Count; i++)
                if (player.Tasks[i] != null && player.Tasks[i].IsComplete)
                    count++;
            return count;
        }

        private static void ShowReplayRoles()
        {
            List<PlayerSnapshot> players = GetPlayersAtPlaybackTime();
            if (players.Count == 0)
            {
                Plugin.Reply("No recorded player roles are available.");
                return;
            }

            List<string> items = new List<string>();
            for (int i = 0; i < players.Count; i++)
            {
                string name = string.IsNullOrWhiteSpace(players[i].Name) ? "Player " + players[i].PlayerId : players[i].Name;
                string role = string.IsNullOrWhiteSpace(players[i].Role) ? "Unknown" : players[i].Role;
                string taskSummary = players[i].Tasks.Count > 0
                    ? " [Tasks " + CountCompletedTasks(players[i]) + "/" + players[i].Tasks.Count + "]"
                    : string.Empty;
                items.Add(name + " = " + role + (players[i].IsDead ? " (Ghost)" : string.Empty) + taskSummary);
            }
            Plugin.Reply("Replay roles @ " + FormatTime(_playbackTime) + ": " + string.Join(" | ", items.ToArray()));
        }

        private static void ShowOneReplayRole(string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerName))
            {
                Plugin.Reply("Usage: /role <recorded player name>");
                return;
            }

            List<PlayerSnapshot> players = GetPlayersAtPlaybackTime();
            for (int i = 0; i < players.Count; i++)
            {
                if (string.Equals(players[i].Name, playerName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Reply(players[i].Name + " | Role: " + players[i].Role +
                        (players[i].IsDead ? " | Ghost/dead at " + FormatTime(_playbackTime) : " | Alive at " + FormatTime(_playbackTime)));
                    return;
                }
            }
            Plugin.Reply("Couldn't find recorded player '" + playerName.Trim() + "'. Use /roles.");
        }

        private static List<PlayerSnapshot> GetPlayersAtPlaybackTime()
        {
            List<PlayerSnapshot> result = new List<PlayerSnapshot>();
            if (_loadedReplay == null || _loadedReplay.Frames.Count == 0)
                return result;

            int index = 0;
            while (index + 1 < _loadedReplay.Frames.Count &&
                   _loadedReplay.Frames[index + 1].Time <= _playbackTime)
                index++;

            ReplayFrame frame = _loadedReplay.Frames[index];
            for (int i = 0; i < frame.Players.Count; i++)
                result.Add(frame.Players[i]);
            return result;
        }

        private static string GetHauntedActorName()
        {
            foreach (KeyValuePair<int, ReplayActor> pair in Actors)
            {
                if (pair.Key == _hauntedPlayerId && pair.Value != null)
                    return pair.Value.Name;
            }
            return string.Empty;
        }

        private static void ApplyHauntCameraTarget()
        {
            if (!_playbackActive)
                return;

            PlayerControl spectator = _spectator != null ? _spectator : PlayerControl.LocalPlayer;
            object camera = null;
            try { camera = ReflectionHelper.GetSingleton("FollowerCamera"); }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Could not access replay follower camera: " + ex.Message);
            }

            // In free-spectate mode the native camera must follow the real local
            // PlayerControl, so its normal keyboard movement and camera stay aligned.
            // Do not point the camera at an actor unless /haunt was explicitly used.
            if (_hauntedPlayerId < 0)
            {
                if (spectator == null)
                    return;

                bool targetAssigned = false;
                if (camera != null)
                {
                    targetAssigned = ReflectionHelper.TryInvokeMethod(
                        camera, "SetTarget", new object[] { spectator });
                    targetAssigned = ReflectionHelper.SetMemberValue(
                        camera, spectator,
                        "target", "Target", "player", "Player", "targetPlayer", "TargetPlayer") || targetAssigned;
                    if (!targetAssigned)
                    {
                        // Some builds expose a Transform rather than PlayerControl target.
                        targetAssigned = ReflectionHelper.SetMemberValue(
                            camera, spectator.transform,
                            "target", "Target", "targetTransform", "TargetTransform", "followTarget", "FollowTarget");
                    }
                }

                // Fallback on builds whose FollowerCamera target signature differs.
                // CameraFollowPostfix runs after the game's camera update, keeping the
                // camera aligned with the moving local ghost without freezing movement.
                if (!targetAssigned && Camera.main != null)
                {
                    Vector3 cameraPosition = Camera.main.transform.position;
                    Camera.main.transform.position = new Vector3(
                        spectator.transform.position.x,
                        spectator.transform.position.y,
                        cameraPosition.z);
                }
                return;
            }

            // Replay actors are visual-only and contain no PlayerControl/NetObject.
            // Follow their transform when supported, otherwise move the camera locally.
            ReplayActor actor;
            if (!Actors.TryGetValue(_hauntedPlayerId, out actor) || actor == null || actor.Root == null)
            {
                _hauntedPlayerId = -1;
                ApplyHauntCameraTarget();
                return;
            }

            Transform actorTransform = actor.Root.transform;
            bool actorTargetAssigned = false;
            if (camera != null)
            {
                actorTargetAssigned = ReflectionHelper.TryInvokeMethod(
                    camera, "SetTarget", new object[] { actorTransform });
                actorTargetAssigned = ReflectionHelper.SetMemberValue(
                    camera, actorTransform,
                    "target", "Target", "targetTransform", "TargetTransform", "followTarget", "FollowTarget") || actorTargetAssigned;
            }

            if (!actorTargetAssigned && Camera.main != null)
            {
                Vector3 cameraPosition = Camera.main.transform.position;
                Camera.main.transform.position = new Vector3(
                    actorTransform.position.x,
                    actorTransform.position.y,
                    cameraPosition.z);
            }
        }

        // Unity's non-generic GameObject.GetComponent(Type) overload is missing in the
        // user's IL2CPP proxy. Prefer direct generic calls for common Unity visuals and use
        // a closed generic method only for optional/version-specific components (TMP/buttons).
        private static MethodInfo _genericGetComponentMethod;
        private static MethodInfo _genericAddComponentMethod;
        private static bool _genericComponentMethodsSearched;

        private static Component GetComponentCompatible(GameObject target, Type componentType)
        {
            if (target == null || componentType == null)
                return null;

            try
            {
                if (componentType == typeof(SpriteRenderer)) return target.GetComponent<SpriteRenderer>();
                if (componentType == typeof(Animator)) return target.GetComponent<Animator>();
                if (componentType == typeof(MeshRenderer)) return target.GetComponent<MeshRenderer>();
                if (componentType == typeof(SkinnedMeshRenderer)) return target.GetComponent<SkinnedMeshRenderer>();
                if (componentType == typeof(ParticleSystemRenderer)) return target.GetComponent<ParticleSystemRenderer>();
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Typed component lookup failed for " + componentType.Name + ": " + ex.Message);
            }

            try
            {
                MethodInfo method = GetGenericComponentMethod(false);
                if (method == null)
                    return null;
                return method.MakeGenericMethod(componentType).Invoke(target, null) as Component;
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Generic component lookup skipped for " + componentType.Name + ": " + ex.Message);
                return null;
            }
        }

        private static Component AddComponentCompatible(GameObject target, Type componentType)
        {
            if (target == null || componentType == null)
                return null;

            try
            {
                if (componentType == typeof(SpriteRenderer)) return target.AddComponent<SpriteRenderer>();
                if (componentType == typeof(Animator)) return target.AddComponent<Animator>();
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Typed AddComponent failed for " + componentType.Name + ": " + ex.Message);
            }

            try
            {
                MethodInfo method = GetGenericComponentMethod(true);
                if (method == null)
                    return null;
                return method.MakeGenericMethod(componentType).Invoke(target, null) as Component;
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Generic AddComponent skipped for " + componentType.Name + ": " + ex.Message);
                return null;
            }
        }

        private static MethodInfo GetGenericComponentMethod(bool addComponent)
        {
            if (!_genericComponentMethodsSearched)
            {
                // GameObject inherits GetComponent<T>() from Component in Unity versions
                // where it is declared there; find the generic zero-argument method through
                // the managed proxy metadata rather than invoking a System.Type overload.
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo[] methods = typeof(GameObject).GetMethods(flags);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1 ||
                        method.GetParameters().Length != 0)
                        continue;

                    if (method.Name == "GetComponent" && _genericGetComponentMethod == null)
                        _genericGetComponentMethod = method;
                    else if (method.Name == "AddComponent" && _genericAddComponentMethod == null)
                        _genericAddComponentMethod = method;
                }
                _genericComponentMethodsSearched = true;
            }
            return addComponent ? _genericAddComponentMethod : _genericGetComponentMethod;
        }

        // IL2CPP-compatible visual traversal. Avoid array-returning component APIs
        // on this Unity build; query known concrete visual types individually.
        private static Component[] FindVisualComponentsInChildren(GameObject root, Type wantedType)
        {
            // Walk the Transform tree and query known visual types without the unavailable GetComponent(Type) overload.
            List<Component> found = new List<Component>();
            if (root == null || wantedType == null)
                return found.ToArray();

            List<Type> concreteTypes = BuildCompatibleComponentTypes(wantedType);
            Stack<Transform> pending = new Stack<Transform>();
            if (root.transform != null)
                pending.Push(root.transform);

            while (pending.Count > 0)
            {
                Transform node = pending.Pop();
                // Inactive hierarchy branches contain alternate character/cosmetic
                // variants that are not part of the current visible outfit. Do not
                // return their renderers or traverse into them for replay cloning.
                if (node == null || node.gameObject == null || !node.gameObject.activeInHierarchy)
                    continue;

                for (int typeIndex = 0; typeIndex < concreteTypes.Count; typeIndex++)
                {
                    Type componentType = concreteTypes[typeIndex];
                    if (componentType == null)
                        continue;
                    try
                    {
                        Component component = GetComponentCompatible(node.gameObject, componentType);
                        if (component != null && wantedType.IsAssignableFrom(component.GetType()) && !found.Contains(component))
                            found.Add(component);
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug("Component lookup skipped for " + componentType.Name + ": " + ex.Message);
                    }
                }

                for (int childIndex = 0; childIndex < node.childCount; childIndex++)
                {
                    try
                    {
                        Transform child = node.GetChild(childIndex);
                        if (child != null)
                            pending.Push(child);
                    }
                    catch { }
                }
            }
            return found.ToArray();
        }

        private static List<Type> BuildCompatibleComponentTypes(Type wantedType)
        {
            List<Type> types = new List<Type>();
            if (wantedType == null)
                return types;

            if (wantedType == typeof(SpriteRenderer) || wantedType == typeof(Renderer) || wantedType == typeof(Component))
                AddCompatibleType(types, typeof(SpriteRenderer));
            if (wantedType == typeof(Animator) || wantedType == typeof(Component))
                AddCompatibleType(types, typeof(Animator));

            if (wantedType == typeof(Renderer) || wantedType == typeof(Component))
            {
                AddCompatibleTypeByName(types, "MeshRenderer");
                AddCompatibleTypeByName(types, "SkinnedMeshRenderer");
                AddCompatibleTypeByName(types, "ParticleSystemRenderer");
            }

            if (wantedType == typeof(Component))
            {
                string[] visualTypes = new string[]
                {
                    "TMP_Text", "TextMeshPro", "TextMeshProUGUI", "TextMeshProBase",
                    "NormalBodySpriteCosmeticsLayer", "CosmeticsLayer", "PlayerCosmeticsLayer",
                    "PassiveButton", "Button", "MultiTouchButton"
                };
                for (int i = 0; i < visualTypes.Length; i++)
                    AddCompatibleTypeByName(types, visualTypes[i]);
            }
            else if (wantedType != typeof(SpriteRenderer) && wantedType != typeof(Renderer) && wantedType != typeof(Animator))
            {
                AddCompatibleType(types, wantedType);
            }
            return types;
        }

        private static void AddCompatibleType(List<Type> types, Type type)
        {
            if (type != null && !types.Contains(type))
                types.Add(type);
        }

        private static void AddCompatibleTypeByName(List<Type> types, string name)
        {
            try { AddCompatibleType(types, ReflectionHelper.FindType(name)); }
            catch { }
        }

        private static ReplayActor EnsureActor(PlayerSnapshot snapshot)
        {
            ReplayActor existing;
            if (Actors.TryGetValue(snapshot.PlayerId, out existing) && existing != null && existing.Root != null)
                return existing;

            PlayerControl local = PlayerControl.LocalPlayer;
            if (local == null || local.gameObject == null || snapshot == null)
                return null;

            GameObject root = null;
            try
            {
                // NEVER Instantiate(local.gameObject): it clones PlayerControl,
                // NetObject and child network behaviours. Those fake network
                // objects cause Unity's "child net obj ... null during serialize"
                // errors when the game tries to spawn/serialize a player.
                // Instead, recreate only the visual transform tree, SpriteRenderers
                // and Animators. Replay actors are graphics, not network entities.
                root = new GameObject("AmongUsCommands_ReplayVisual_" + snapshot.PlayerId);
                root.transform.position = new Vector3(snapshot.X, snapshot.Y, snapshot.Z + ActorZ);
                root.transform.rotation = local.transform.rotation;
                root.transform.localScale = local.transform.localScale;

                CloneReplayVisualNode(local.transform, root.transform, true);
                AddReplayNameLabel(root, local, snapshot);

                ReplayActor actor = new ReplayActor();
                actor.Root = root;
                actor.PlayerId = snapshot.PlayerId;
                actor.Name = snapshot.Name;
                actor.Role = snapshot.Role;

                Component[] renderComponents = FindVisualComponentsInChildren(root, typeof(Renderer));
                Renderer[] renderers = new Renderer[renderComponents.Length];
                for (int i = 0; i < renderComponents.Length; i++)
                    renderers[i] = renderComponents[i] as Renderer;
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null || renderers[i].gameObject == null)
                        continue;
                    if (renderers[i].gameObject.name.StartsWith("ReplayName_", StringComparison.Ordinal))
                        continue;
                    actor.Renderers.Add(renderers[i]);
                    SpriteRenderer sprite = renderers[i] as SpriteRenderer;
                    if (sprite != null && !actor.OriginalFlipX.ContainsKey(sprite))
                        actor.OriginalFlipX.Add(sprite, sprite.flipX);
                }

                Component[] textComponents = FindVisualComponentsInChildren(root, typeof(Component));
                for (int i = 0; i < textComponents.Length; i++)
                {
                    Component c = textComponents[i];
                    if (c == null)
                        continue;
                    string typeName = c.GetType().Name;
                    if (typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        c.gameObject != null && c.gameObject.name.IndexOf("ReplayName", StringComparison.OrdinalIgnoreCase) >= 0)
                        actor.TextComponents.Add(c);
                }

                Actors[snapshot.PlayerId] = actor;
                ApplyActorAppearance(actor, snapshot);
                return actor;
            }
            catch (Exception ex)
            {
                if (root != null)
                    UnityEngine.Object.Destroy(root);
                Plugin.ModLogger?.LogWarning("Could not create visual-only replay actor for " + snapshot.Name + ": " + ex.Message);
                return null;
            }
        }

        private static void CloneReplayVisualNode(Transform source, Transform target, bool copySourceNode)
        {
            if (source == null || target == null || source.gameObject == null ||
                !source.gameObject.activeInHierarchy)
                return;

            if (copySourceNode)
            {
                SpriteRenderer sourceRenderer = source.GetComponent<SpriteRenderer>();
                // Among Us toggles alternate body, visor and cosmetic sprites by
                // disabling renderers. Copy only the one that is actually visible.
                if (sourceRenderer != null && sourceRenderer.enabled && sourceRenderer.sprite != null)
                    CopyReplaySpriteRenderer(sourceRenderer, target.gameObject);

                // Intentionally do not copy Animator components. The source controller is
                // wired to the real PlayerControl hierarchy and may animate each detached
                // replay child independently, producing broken/flickering crewmate parts.
                // The renderer hierarchy is frozen as one coherent visual snapshot instead.
            }

            for (int i = 0; i < source.childCount; i++)
            {
                Transform child = source.GetChild(i);
                if (child == null || child.gameObject == null || !child.gameObject.activeInHierarchy ||
                    !HasVisibleSpriteRendererInChildren(child.gameObject))
                    continue;

                GameObject node = new GameObject(child.name + "_ReplayVisual");
                Transform nodeTransform = node.transform;
                nodeTransform.SetParent(target, false);
                nodeTransform.localPosition = child.localPosition;
                nodeTransform.localRotation = child.localRotation;
                nodeTransform.localScale = child.localScale;
                CloneReplayVisualNode(child, nodeTransform, true);
            }
        }

        private static bool HasVisibleSpriteRendererInChildren(GameObject root)
        {
            if (root == null || !root.activeInHierarchy)
                return false;

            Component[] components = FindVisualComponentsInChildren(root, typeof(SpriteRenderer));
            for (int i = 0; i < components.Length; i++)
            {
                SpriteRenderer sprite = components[i] as SpriteRenderer;
                if (sprite != null && sprite.enabled && sprite.sprite != null &&
                    sprite.gameObject != null && sprite.gameObject.activeInHierarchy)
                    return true;
            }
            return false;
        }

        private static void CopyReplaySpriteRenderer(SpriteRenderer source, GameObject target)
        {
            if (source == null || target == null)
                return;

            SpriteRenderer copy = target.AddComponent<SpriteRenderer>();
            copy.sprite = source.sprite;
            copy.color = source.color;
            copy.flipX = source.flipX;
            copy.flipY = source.flipY;
            copy.sortingLayerID = source.sortingLayerID;
            copy.sortingOrder = source.sortingOrder;
            copy.sharedMaterial = source.sharedMaterial;
            copy.drawMode = source.drawMode;
            copy.size = source.size;
            copy.maskInteraction = source.maskInteraction;
            copy.enabled = source.enabled;
        }

        private static void AddReplayNameLabel(GameObject actorRoot, PlayerControl local, PlayerSnapshot snapshot)
        {
            try
            {
                if (actorRoot == null || local == null || snapshot == null)
                    return;

                Component sourceText = null;
                Component[] components = FindVisualComponentsInChildren(local.gameObject, typeof(Component));
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null)
                        continue;
                    string typeName = component.GetType().Name;
                    string objectName = component.gameObject != null ? component.gameObject.name : string.Empty;
                    if (typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        typeName.IndexOf("UGUI", StringComparison.OrdinalIgnoreCase) < 0 &&
                        objectName.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sourceText = component;
                        break;
                    }
                }

                if (sourceText == null)
                    return;

                GameObject labelObject = new GameObject("ReplayName_" + snapshot.PlayerId);
                labelObject.transform.SetParent(actorRoot.transform, false);
                labelObject.transform.localPosition = new Vector3(0f, 0.72f, -0.15f);
                labelObject.transform.localRotation = Quaternion.identity;
                labelObject.transform.localScale = new Vector3(0.02f, 0.02f, 0.02f);

                // Add only the visual TMP component type. Do not clone the source
                // label GameObject because its ancestors may contain net objects.
                Component label = AddComponentCompatible(labelObject, sourceText.GetType());
                if (label == null)
                    return;

                ReflectionHelper.SetMemberValue(label,
                    ReflectionHelper.GetMemberValue(sourceText, "font", "m_fontAsset"), "font", "m_fontAsset");
                ReflectionHelper.SetMemberValue(label,
                    ReflectionHelper.GetMemberValue(sourceText, "fontSize", "m_fontSize"), "fontSize", "m_fontSize");
                ReflectionHelper.SetMemberValue(label,
                    ReflectionHelper.GetMemberValue(sourceText, "alignment", "m_textAlignment"), "alignment", "m_textAlignment");
                ReflectionHelper.SetMemberValue(label,
                    ReflectionHelper.GetMemberValue(sourceText, "color", "m_fontColor"), "color", "m_fontColor");
                ReflectionHelper.SetMemberValue(label, false, "enableWordWrapping", "m_enableWordWrapping");
                ReflectionHelper.SetMemberValue(label, true, "richText", "m_richText");
                ReflectionHelper.SetMemberValue(label, EscapeRichText(snapshot.Name), "text", "m_text");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay name label creation skipped: " + ex.Message);
            }
        }

        private static bool IsSafeVisualBehaviour(string typeName)
        {
            return typeName.IndexOf("Animator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ApplyActorAppearance(ReplayActor actor, PlayerSnapshot snapshot)
        {
            if (actor == null || actor.Root == null || snapshot == null)
                return;

            string roleSuffix = string.IsNullOrWhiteSpace(snapshot.Role) ? string.Empty : "\n<color=#B8B8B8>" + EscapeRichText(snapshot.Role) + "</color>";
            string label = EscapeRichText(snapshot.Name) + roleSuffix;
            for (int i = 0; i < actor.TextComponents.Count; i++)
                SetText(actor.TextComponents[i], label);

            // This actor deliberately contains renderer/animator visuals only.
            // Do not attach or call PlayerControl/CosmeticsLayer network behaviors
            // during replay; those components can register NetObjects in IL2CPP.
            ApplyPlayerColor(snapshot.ColorId, actor.Renderers);

            float alpha = snapshot.IsDead ? 0.48f : 1f;
            bool hasDirection = Math.Abs(snapshot.VelocityX) > 0.035f;
            bool movingLeft = snapshot.VelocityX < -0.035f;
            for (int i = 0; i < actor.Renderers.Count; i++)
            {
                SpriteRenderer sprite = actor.Renderers[i] as SpriteRenderer;
                if (sprite == null)
                    continue;
                Color color = sprite.color;
                color.a = alpha;
                sprite.color = color;

                bool baseFlip;
                if (hasDirection && actor.OriginalFlipX.TryGetValue(sprite, out baseFlip))
                    sprite.flipX = movingLeft ? !baseFlip : baseFlip;
            }

            actor.LastDead = snapshot.IsDead;
        }

        private static void ApplyRecordedCosmetics(ReplayActor actor, PlayerSnapshot snapshot)
        {
            if (actor == null || actor.Root == null || snapshot == null)
                return;

            try
            {
                Component[] components = FindVisualComponentsInChildren(actor.Root, typeof(Component));
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null || component.GetType().Name.IndexOf("CosmeticsLayer", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // Different Among Us builds expose different local visual setters.
                    // Try the non-RPC cosmetics-layer methods and silently skip unavailable ones.
                    if (!string.IsNullOrEmpty(snapshot.Hat))
                    {
                        ReflectionHelper.TryInvokeMethod(component, "SetHat", new object[] { snapshot.Hat });
                        ReflectionHelper.TryInvokeMethod(component, "SetHat", new object[] { snapshot.Hat, false });
                    }
                    if (!string.IsNullOrEmpty(snapshot.Skin))
                    {
                        ReflectionHelper.TryInvokeMethod(component, "SetSkin", new object[] { snapshot.Skin });
                        ReflectionHelper.TryInvokeMethod(component, "SetSkin", new object[] { snapshot.Skin, false });
                    }
                    if (!string.IsNullOrEmpty(snapshot.Visor))
                    {
                        ReflectionHelper.TryInvokeMethod(component, "SetVisor", new object[] { snapshot.Visor });
                        ReflectionHelper.TryInvokeMethod(component, "SetVisor", new object[] { snapshot.Visor, false });
                    }
                    if (!string.IsNullOrEmpty(snapshot.Pet))
                    {
                        ReflectionHelper.TryInvokeMethod(component, "SetPet", new object[] { snapshot.Pet });
                        ReflectionHelper.TryInvokeMethod(component, "SetPet", new object[] { snapshot.Pet, false });
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay cosmetics application failed: " + ex.Message);
            }
        }

        private static void ApplyPlayerColor(byte colorId, List<Renderer> renderers)
        {
            try
            {
                Type materialType = ReflectionHelper.FindType("PlayerMaterial");
                if (materialType != null)
                {
                    MethodInfo[] methods = materialType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        MethodInfo method = methods[i];
                        if (method.Name != "SetColors")
                            continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length != 2)
                            continue;

                        for (int j = 0; j < renderers.Count; j++)
                        {
                            Renderer renderer = renderers[j];
                            if (renderer == null || !parameters[1].ParameterType.IsInstanceOfType(renderer))
                                continue;

                            object converted;
                            if (!ReflectionHelper.TryConvertValue(colorId, parameters[0].ParameterType, out converted))
                                continue;
                            try { method.Invoke(null, new object[] { converted, renderer }); }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Replay actor color application failed: " + ex.Message);
            }
        }

        private static void SetText(Component component, string text)
        {
            if (component == null)
                return;
            try
            {
                ReflectionHelper.SetMemberValue(component, text, "text", "Text");
            }
            catch { }
        }

        private static void ReplayChatDue()
        {
            if (_loadedReplay == null)
                return;

            while (_nextChatIndex < _loadedReplay.Chat.Count && _loadedReplay.Chat[_nextChatIndex].Time <= _playbackTime)
            {
                ReplayChat chat = _loadedReplay.Chat[_nextChatIndex++];
                string safeName = string.IsNullOrWhiteSpace(chat.PlayerName) ? "System" : chat.PlayerName;
                ShowReplayChat("<color=#B8B8B8>[Replay]</color> <b>" + EscapeRichText(safeName) + "</b>: " + EscapeRichText(chat.Message));
            }
        }

        private static void ReplayEventsDue()
        {
            if (_loadedReplay == null)
                return;

            while (_nextEventIndex < _loadedReplay.Events.Count && _loadedReplay.Events[_nextEventIndex].Time <= _playbackTime)
            {
                ReplayEvent ev = _loadedReplay.Events[_nextEventIndex++];
                if (ev.EventName == "RecordingStarted")
                    continue;

                string actor = string.IsNullOrWhiteSpace(ev.ActorName) ? string.Empty : EscapeRichText(ev.ActorName);
                string target = string.IsNullOrWhiteSpace(ev.TargetName) ? string.Empty : " → " + EscapeRichText(ev.TargetName);
                string details = string.IsNullOrWhiteSpace(ev.Details) ? string.Empty : " (" + EscapeRichText(ev.Details) + ")";
                bool systemEvent = ev.EventName.StartsWith("Sabotage.", StringComparison.OrdinalIgnoreCase);
                if (systemEvent)
                    ApplyRecordedSystemEvent(ev);
                string prefix = systemEvent
                    ? "<color=#FFB347>[Replay sabotage]</color> "
                    : "<color=#C0C0C0>[Replay event]</color> ";
                ShowReplayChat(prefix + EscapeRichText(ev.EventName) + ": " + actor + target + details);
            }
        }

        // Replay system changes locally only in solo Freeplay. This applies the recorded
        // SystemTypes/amount directly to the matching local system object; it deliberately
        // never calls ShipStatus.RpcUpdateSystem or any network RPC during playback.
        private static void ApplyRecordedSystemEvent(ReplayEvent ev)
        {
            if (ev == null || !_playbackActive || !IsFreeplayGame() || !IsMatchSceneReady())
                return;

            try
            {
                if (string.Equals(ev.EventName, "Sabotage.SystemUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    TryApplyRecordedSystemUpdate(ev.Details);
                    return;
                }

                if (string.Equals(ev.EventName, "Sabotage.Doors", StringComparison.OrdinalIgnoreCase))
                {
                    TryApplyRecordedDoorChange(ev.Details, false);
                    return;
                }

                if (string.Equals(ev.EventName, "Sabotage.DoorsOpen", StringComparison.OrdinalIgnoreCase))
                {
                    TryApplyRecordedDoorChange(ev.Details, true);
                    return;
                }

                // RepairDamage markers are retained and shown in the replay event timeline.
                // Applying them twice could double-apply a repair that followed an RPC update.
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogDebug("Local replay system event could not be applied: " + ex.Message);
            }
        }

        private static void TryApplyRecordedSystemUpdate(string details)
        {
            string systemName;
            byte amount;
            if (!TryParseSystemAndAmount(details, out systemName, out amount))
                return;

            Type systemTypes = ReflectionHelper.FindType("SystemTypes");
            if (systemTypes == null || !systemTypes.IsEnum)
                return;

            object systemKey;
            try { systemKey = Enum.Parse(systemTypes, systemName, true); }
            catch { return; }

            object ship = ReflectionHelper.GetSingleton("ShipStatus");
            object systems = ReflectionHelper.GetMemberValue(ship, "Systems", "systems");
            object system = GetReplayDictionaryValue(systems, systemKey);
            PlayerControl local = PlayerControl.LocalPlayer;
            if (system == null || local == null)
                return;

            // RepairDamage is the local system-state method called by the game's system
            // RPC handler. Calling it directly avoids network sends while reproducing the
            // system object's state/visual effects in the replay's offline Freeplay scene.
            ReflectionHelper.TryInvokeMethod(system, "RepairDamage", new object[] { local, amount });
        }

        private static bool TryParseSystemAndAmount(string details, out string systemName, out byte amount)
        {
            systemName = string.Empty;
            amount = 0;
            if (string.IsNullOrWhiteSpace(details))
                return false;

            string[] parts = details.Split(',');
            if (parts.Length < 2)
                return false;

            systemName = parts[0].Trim();
            if (systemName.StartsWith("SystemTypes.", StringComparison.OrdinalIgnoreCase))
                systemName = systemName.Substring("SystemTypes.".Length);

            // Only parse the second argument as a byte. If this build formats the arguments
            // differently, skip the visual system mutation but preserve the event marker.
            return !string.IsNullOrWhiteSpace(systemName) &&
                byte.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount);
        }

        private static object GetReplayDictionaryValue(object dictionary, object key)
        {
            if (dictionary == null || key == null)
                return null;

            try
            {
                System.Collections.IDictionary nonGeneric = dictionary as System.Collections.IDictionary;
                if (nonGeneric != null)
                    return nonGeneric.Contains(key) ? nonGeneric[key] : null;
            }
            catch { }

            Type type = dictionary.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            while (type != null)
            {
                MethodInfo[] methods = type.GetMethods(flags);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "get_Item" || method.GetParameters().Length != 1)
                        continue;

                    object convertedKey;
                    if (!ReflectionHelper.TryConvertValue(key, method.GetParameters()[0].ParameterType, out convertedKey))
                        continue;

                    try { return method.Invoke(dictionary, new object[] { convertedKey }); }
                    catch { }
                }
                type = type.BaseType;
            }
            return null;
        }

        private static void TryApplyRecordedDoorChange(string details, bool open)
        {
            if (string.IsNullOrWhiteSpace(details))
                return;

            string doorTypeName = details.Split(',')[0].Trim();
            Type systemTypes = ReflectionHelper.FindType("SystemTypes");
            if (systemTypes == null || !systemTypes.IsEnum)
                return;
            if (doorTypeName.StartsWith("SystemTypes.", StringComparison.OrdinalIgnoreCase))
                doorTypeName = doorTypeName.Substring("SystemTypes.".Length);

            object doorType;
            try { doorType = Enum.Parse(systemTypes, doorTypeName, true); }
            catch { return; }

            object ship = ReflectionHelper.GetSingleton("ShipStatus");
            if (ship == null)
                return;

            // These are local state/visual methods. Never use RpcCloseDoorsOfType or a
            // network RPC from replay playback.
            ReflectionHelper.TryInvokeMethod(
                ship,
                open ? "OpenDoorsOfType" : "CloseDoorsOfType",
                new object[] { doorType });
        }

        private static void ShowReplayChat(string message)
        {
            string plainMessage = "[Replay] " + StripRichText(message);
            try
            {
                if (HudManager.Instance != null && HudManager.Instance.Chat != null)
                {
                    // On some builds AddChat(null, ...) returns without throwing but does
                    // not actually add a line. Prefer the valid local player as the source.
                    PlayerControl local = PlayerControl.LocalPlayer;
                    if (local != null)
                    {
                        try
                        {
                            HudManager.Instance.Chat.AddChat(local, message, false);
                            return;
                        }
                        catch (Exception ex)
                        {
                            Plugin.ModLogger?.LogDebug("Replay chat AddChat(local, ...) failed: " + ex.Message);
                        }
                    }

                    try
                    {
                        HudManager.Instance.Chat.AddChat(null, message, false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug("Replay chat AddChat(null, ...) failed: " + ex.Message);
                    }
                }

                Plugin.ModLogger?.LogInfo(plainMessage);
            }
            catch (Exception ex)
            {
                Plugin.ModLogger?.LogWarning("Replay chat display failed: " + ex.Message);
                Plugin.ModLogger?.LogInfo(plainMessage);
            }
        }

        private static void StopPlayback(bool destroyActors)
        {
            // Retarget the game's follower camera to the local spectator before
            // destroying replay actors; otherwise the camera can retain a dead Transform.
            if (_playbackActive)
            {
                _hauntedPlayerId = -1;
                ApplyHauntCameraTarget();
            }

            _playbackActive = false;
            _playbackLoading = false;
            _playbackPaused = false;
            LocalCommandChat.HideReplayProgress();
            _lastReplayLoadingStatus = string.Empty;
            _reusePort22023Lobby = false;
            _lobbyActorPreviewCreated = false;
            _waitForManualExitFromOtherLobby = false;
            _deferredExitPending = false;
            _awaitingMainMenu = false;
            _loadedReplay = null;
            if (destroyActors)
                ClearActors();

            try
            {
                if (_spectator != null && _spectator.Data != null)
                {
                    ReflectionHelper.SetMemberValue(_spectator.Data, _savedLocalDead, "IsDead", "isDead");
                    ReflectionHelper.SetMemberValue(_spectator, _savedLocalDead, "isDead", "IsDead");
                    ReflectionHelper.SetMemberValue(_spectator, _savedLocalMoveable, "moveable", "Moveable");
                }
            }
            catch { }

            _hauntedPlayerId = -1;
            _spectator = null;
        }

        private static void ClearActors()
        {
            foreach (KeyValuePair<int, ReplayActor> pair in Actors)
            {
                try
                {
                    if (pair.Value != null && pair.Value.Root != null)
                        UnityEngine.Object.Destroy(pair.Value.Root);
                }
                catch { }
            }
            Actors.Clear();
        }

        private static int FindChatIndex(float time)
        {
            if (_loadedReplay == null)
                return 0;
            int index = 0;
            while (index < _loadedReplay.Chat.Count && _loadedReplay.Chat[index].Time < time)
                index++;
            return index;
        }

        private static int FindEventIndex(float time)
        {
            if (_loadedReplay == null)
                return 0;
            int index = 0;
            while (index < _loadedReplay.Events.Count && _loadedReplay.Events[index].Time < time)
                index++;
            return index;
        }

        private static PlayerSnapshot FindPlayer(ReplayFrame frame, int playerId)
        {
            for (int i = 0; i < frame.Players.Count; i++)
                if (frame.Players[i].PlayerId == playerId)
                    return frame.Players[i];
            return null;
        }

        private static bool IsFreeplayGame()
        {
            try
            {
                object client = ReflectionHelper.GetSingleton("AmongUsClient");
                object mode = ReflectionHelper.GetMemberValue(client, "NetworkMode", "networkMode", "GameMode", "gameMode");
                string text = mode == null ? string.Empty : mode.ToString();
                return text.IndexOf("FreePlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       text.IndexOf("Freeplay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       text.IndexOf("Practice", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMatchSceneReady()
        {
            try
            {
                return ReflectionHelper.GetSingleton("ShipStatus") != null && PlayerControl.LocalPlayer != null;
            }
            catch
            {
                return false;
            }
        }

        private static byte GetMapId()
        {
            try
            {
                object ship = ReflectionHelper.GetSingleton("ShipStatus");
                object value = ReflectionHelper.GetMemberValue(ship, "MapId", "mapId");
                if (value != null)
                    return Convert.ToByte(value);

                object manager = ReflectionHelper.GetSingleton("GameOptionsManager");
                object options = ReflectionHelper.GetMemberValue(manager, "CurrentGameOptions", "currentGameOptions");
                value = ReflectionHelper.GetMemberValue(options, "MapId", "mapId");
                if (value != null)
                    return Convert.ToByte(value);
            }
            catch { }
            return 0;
        }

        private static byte GetColorId(PlayerControl player)
        {
            if (player == null)
                return 0;
            try
            {
                object outfit = ReflectionHelper.GetMemberValue(player.Data, "DefaultOutfit", "defaultOutfit", "CurrentOutfit", "currentOutfit");
                object value = ReflectionHelper.GetMemberValue(outfit, "ColorId", "colorId");
                if (value == null)
                    value = ReflectionHelper.GetMemberValue(player.Data, "ColorId", "colorId");
                if (value == null)
                    value = ReflectionHelper.GetMemberValue(player, "CurrentOutfitColor", "ColorId", "colorId");
                return Convert.ToByte(value ?? 0);
            }
            catch { return 0; }
        }

        private static string GetRoleName(PlayerControl player)
        {
            try
            {
                object role = ReflectionHelper.GetMemberValue(player.Data, "RoleType", "roleType", "Role", "role");
                if (role != null)
                    return role.ToString();
            }
            catch { }
            return "Unknown";
        }

        private static bool IsImpostorRole(string role)
        {
            if (string.IsNullOrEmpty(role))
                return false;
            return role.IndexOf("impostor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   role.IndexOf("shapeshifter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   role.IndexOf("phantom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   role.IndexOf("viper", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetPlayerName(object value)
        {
            if (value == null)
                return string.Empty;
            try
            {
                object data = ReflectionHelper.GetMemberValue(value, "Data", "data");
                object name = ReflectionHelper.GetMemberValue(data, "PlayerName", "playerName");
                if (name != null)
                    return name.ToString();
                name = ReflectionHelper.GetMemberValue(value, "PlayerName", "playerName");
                if (name != null)
                    return name.ToString();
            }
            catch { }
            return string.Empty;
        }

        private static string MakeEventDetails(object[] args)
        {
            if (args == null || args.Length == 0)
                return string.Empty;
            List<string> bits = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(GetPlayerName(args[i])))
                    continue;
                string value = args[i].ToString();
                if (!string.IsNullOrWhiteSpace(value) && value.Length < 120)
                    bits.Add(value);
            }
            return string.Join(", ", bits.ToArray());
        }

        private static int SafeInt(object value, int fallback)
        {
            try { return value == null ? fallback : Convert.ToInt32(value); }
            catch { return fallback; }
        }

        private static bool SafeBool(object value)
        {
            try { return value != null && Convert.ToBoolean(value); }
            catch { return false; }
        }

        private static string SafeString(object value)
        {
            return value == null ? string.Empty : value.ToString();
        }

        private static float GetRecordingTime()
        {
            return Math.Max(0f, Time.unscaledTime - _recordStartTime);
        }

        private static string NormalizeFileName(string input)
        {
            string name = Path.GetFileNameWithoutExtension((input ?? string.Empty).Trim());
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            name = name.Trim().Trim('.');
            if (name.Length > 80)
                name = name.Substring(0, 80);
            return string.IsNullOrWhiteSpace(name) ? "Replay" : name;
        }

        private static string GetReplayDirectory()
        {
            string root;
            try { root = Paths.GameRootPath; }
            catch { root = Directory.GetCurrentDirectory(); }
            return Path.Combine(root, "replays");
        }

        private static string GetMapToken(byte mapId)
        {
            switch (mapId)
            {
                case 0: return "Skeld";
                case 1: return "MiraHQ";
                case 2: return "Polus";
                case 3: return "Dleks";
                case 4: return "Airship";
                case 5: return "Fungle";
                default: return "Map" + mapId;
            }
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 0f)
                seconds = 0f;
            int total = (int)seconds;
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static bool TryParseTime(string text, out float seconds)
        {
            seconds = 0f;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string input = text.Trim();
            if (input.IndexOf(':') >= 0)
            {
                string[] p = input.Split(':');
                if (p.Length != 2)
                    return false;
                int minutes;
                float secs;
                if (!int.TryParse(p[0], out minutes) || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out secs) ||
                    minutes < 0 || secs < 0 || secs >= 60f)
                    return false;
                seconds = minutes * 60f + secs;
                return true;
            }

            return float.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds >= 0f;
        }

        private static string EscapeRichText(string text)
        {
            return (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string StripRichText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex("<[^>]+>");
            return regex.Replace(text, string.Empty);
        }
    }
}
