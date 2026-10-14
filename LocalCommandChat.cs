using System;
using System.Globalization;

namespace AmongUsCommands
{
    /// <summary>
    /// Local command responses using Among Us' original HUD popup.
    /// Replay progress refreshes the same native popup as the replay time advances.
    /// No command response is sent as a multiplayer chat RPC.
    /// </summary>
    internal static class LocalCommandChat
    {
        private static bool _replayProgressActive;
        private static string _lastReplayStatusMessage = string.Empty;
        private static float _lastReplayStatusAt = -100f;

        internal static void Show(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                // Use the game's own local popup, as requested. This does not
                // submit a chat message to the lobby.
                if (HudManager.Instance != null)
                {
                    HudManager.Instance.ShowPopUp(
                        "[AmongUsCommands]\n" + message
                    );
                    return;
                }

                Plugin.ModLogger?.LogInfo("[AmongUsCommands] " + message);
            }
            catch (Exception ex)
            {
                // ShowPopUp can throw while the game is transitioning scenes.
                // Catch it here so notification failures don't break commands.
                Plugin.ModLogger?.LogWarning(
                    "LocalCommandChat popup failed: " + ex.Message
                );
                Plugin.ModLogger?.LogInfo("[AmongUsCommands] " + message);
            }
        }

        /// <summary>
        /// Updates the native game popup with replay timeline progress.
        /// ReplaySystem calls this when playback time advances (normally once
        /// per second), so the popup's text is refreshed rather than using a
        /// separate custom Canvas overlay.
        /// </summary>
        internal static void ShowReplayProgress(
            string title,
            float elapsedSeconds,
            float totalSeconds)
        {
            if (float.IsNaN(elapsedSeconds) || float.IsInfinity(elapsedSeconds))
                elapsedSeconds = 0f;
            if (float.IsNaN(totalSeconds) || float.IsInfinity(totalSeconds))
                totalSeconds = 0f;

            elapsedSeconds = Math.Max(0f, elapsedSeconds);
            totalSeconds = Math.Max(0f, totalSeconds);
            if (totalSeconds > 0f)
                elapsedSeconds = Math.Min(elapsedSeconds, totalSeconds);

            int percent = totalSeconds <= 0f
                ? 0
                : (int)Math.Floor(Math.Min(1f, elapsedSeconds / totalSeconds) * 100f);

            _replayProgressActive = true;
            _lastReplayStatusMessage = string.Empty;
            string safeTitle = string.IsNullOrWhiteSpace(title)
                ? "Replay Loading"
                : title.Trim();

            Show(safeTitle + "\n" + percent.ToString(CultureInfo.InvariantCulture) +
                "%   " + FormatTime(elapsedSeconds) +
                " out of " + FormatTime(totalSeconds));
        }

        /// <summary>
        /// Show the current launcher stage while a replay is queued. This is
        /// intentionally separate from timeline percentage: loading stages are
        /// statuses, not fabricated playback progress. Calls are throttled so
        /// ReplaySystem can invoke this every frame without spamming the HUD.
        /// </summary>
        internal static void ShowReplayStatus(string message, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            float now = 0f;
            try { now = UnityEngine.Time.unscaledTime; }
            catch { }

            bool changed = !string.Equals(_lastReplayStatusMessage, message, StringComparison.Ordinal);
            if (!force && !changed && now - _lastReplayStatusAt < 4f)
                return;

            _lastReplayStatusMessage = message;
            _lastReplayStatusAt = now;
            _replayProgressActive = true;
            Show("Replay Launcher\n" + message);
        }

        /// <summary>
        /// Compatibility hook expected by ReplaySystem. The native HUD popup
        /// manages its own lifetime; don't try to destroy its DialogueBox while
        /// the network/session is transitioning.
        /// </summary>
        internal static void HideReplayProgress()
        {
            _replayProgressActive = false;
            _lastReplayStatusMessage = string.Empty;
            _lastReplayStatusAt = -100f;
            // There is no stable, version-independent Hide API for ShowPopUp.
            // The existing native popup will expire according to the game HUD.
        }

        /// <summary>
        /// Compatibility tick called by ReplaySystem. Progress is refreshed by
        /// ShowReplayProgress calls from the replay timeline itself. Kept as a
        /// method to support builds where ReplaySystem invokes this every frame.
        /// </summary>
        internal static void Tick()
        {
            // Intentionally empty: ShowPopUp owns its own animation/lifetime.
            // Avoid repeated ShowPopUp calls each frame, which can trigger
            // DialogueBox/HUD errors during scene transitions.
            if (!_replayProgressActive)
                return;
        }

        private static string FormatTime(float seconds)
        {
            int totalSeconds;
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
                totalSeconds = 0;
            else if (seconds >= int.MaxValue)
                totalSeconds = int.MaxValue;
            else
                totalSeconds = (int)Math.Floor(seconds);

            int minutes = totalSeconds / 60;
            int remainder = totalSeconds % 60;
            return minutes.ToString(CultureInfo.InvariantCulture) + ":" +
                   remainder.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
