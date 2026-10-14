using System;
using System.Collections.Generic;
using System.Reflection;

namespace AmongUsCommands
{
    internal static partial class CommandHandlers
    {
        // ========================================================
        // PRESET MODELS
        // ========================================================

        private sealed class PresetOption
        {
            internal readonly string Setter;
            internal readonly string Name;
            internal readonly object Value;

            internal PresetOption(
                string setter,
                string name,
                object value)
            {
                Setter = setter;
                Name = name;
                Value = value;
            }
        }

        private sealed class RoleRate
        {
            internal readonly string Role;
            internal readonly int Count;
            internal readonly int Chance;

            internal RoleRate(
                string role,
                int count,
                int chance)
            {
                Role = role;
                Count = count;
                Chance = chance;
            }
        }

        private sealed class PresetDefinition
        {
            internal readonly string DisplayName;
            internal readonly string Description;
            internal readonly string CopyFrom;
            internal readonly PresetOption[] Options;
            internal readonly RoleRate[] Roles;

            internal PresetDefinition(
                string displayName,
                string description,
                string copyFrom,
                PresetOption[] options,
                RoleRate[] roles)
            {
                DisplayName = displayName;
                Description = description;
                CopyFrom = copyFrom;
                Options = options;
                Roles = roles;
            }
        }

        // ========================================================
        // HARD GAME
        // ========================================================

        private static PresetDefinition CreateHardGame()
        {
            return new PresetDefinition(
                "Hard Game",
                "A faster, more difficult lobby configuration.",
                "Roles Galore",
                new PresetOption[]
                {
                    new PresetOption(
                        "SetInt",
                        "NumImpostors",
                        3
                    ),

                    new PresetOption(
                        "SetFloat",
                        "KillCooldown",
                        10f
                    ),

                    new PresetOption(
                        "SetFloat",
                        "ImpostorLightMod",
                        1.5f
                    ),

                    // Vanilla KillDistance values: 0 = Short, 1 = Medium,
                    // 2 = Long. Check your installed game if this changes.
                    new PresetOption(
                        "SetInt",
                        "KillDistance",
                        2
                    ),

                    new PresetOption(
                        "SetFloat",
                        "PlayerSpeedMod",
                        1.75f
                    ),

                    new PresetOption(
                        "SetFloat",
                        "CrewLightMod",
                        1.0f
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumEmergencyMeetings",
                        2
                    ),

                    new PresetOption(
                        "SetInt",
                        "EmergencyCooldown",
                        30
                    ),

                    new PresetOption(
                        "SetInt",
                        "DiscussionTime",
                        30
                    ),

                    new PresetOption(
                        "SetInt",
                        "VotingTime",
                        75
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumCommonTasks",
                        2
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumLongTasks",
                        3
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumShortTasks",
                        5
                    ),

                    new PresetOption(
                        "SetBool",
                        "VisualTasks",
                        true
                    )
                },
                CreateHardGameRoles()
            );
        }

        private static RoleRate[] CreateHardGameRoles()
        {
            return new RoleRate[]
            {
                new RoleRate("Scientist", 1, 100),
                new RoleRate("GuardianAngel", 3, 100),
                new RoleRate("Engineer", 1, 100),
                new RoleRate("Noisemaker", 1, 100),
                new RoleRate("Tracker", 1, 100),
                new RoleRate("Detective", 1, 100),

                // Judge: one role slot, 30% spawn chance.
                new RoleRate("Judge", 1, 30),

                new RoleRate("Influencer", 3, 80),
                new RoleRate("Shapeshifter", 1, 70),
                new RoleRate("Phantom", 1, 70),
                new RoleRate("Viper", 1, 70)
            };
        }

        // ========================================================
        // BEGINNER GAME
        // ========================================================

        private static PresetDefinition CreateBeginnerGame()
        {
            return new PresetDefinition(
                "Beginner Game",
                "A more forgiving lobby configuration.",
                "Roles Galore",
                new PresetOption[]
                {
                    new PresetOption(
                        "SetInt",
                        "NumImpostors",
                        3
                    ),

                    new PresetOption(
                        "SetFloat",
                        "KillCooldown",
                        20f
                    ),

                    new PresetOption(
                        "SetFloat",
                        "ImpostorLightMod",
                        1.5f
                    ),

                    // Vanilla KillDistance values: 0 = Short, 1 = Medium,
                    // 2 = Long. Check your installed game if this changes.
                    new PresetOption(
                        "SetInt",
                        "KillDistance",
                        1
                    ),

                    new PresetOption(
                        "SetFloat",
                        "PlayerSpeedMod",
                        2.75f
                    ),

                    new PresetOption(
                        "SetFloat",
                        "CrewLightMod",
                        1.0f
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumEmergencyMeetings",
                        5
                    ),

                    new PresetOption(
                        "SetInt",
                        "EmergencyCooldown",
                        15
                    ),

                    new PresetOption(
                        "SetInt",
                        "DiscussionTime",
                        60
                    ),

                    new PresetOption(
                        "SetInt",
                        "VotingTime",
                        75
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumCommonTasks",
                        2
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumLongTasks",
                        1
                    ),

                    new PresetOption(
                        "SetInt",
                        "NumShortTasks",
                        2
                    ),

                    new PresetOption(
                        "SetBool",
                        "VisualTasks",
                        true
                    )
                },
                CreateBeginnerGameRoles()
            );
        }

        private static RoleRate[] CreateBeginnerGameRoles()
        {
            return new RoleRate[]
            {
                new RoleRate("Scientist", 1, 100),
                new RoleRate("GuardianAngel", 3, 100),
                new RoleRate("Engineer", 1, 100),
                new RoleRate("Noisemaker", 1, 100),
                new RoleRate("Tracker", 1, 100),
                new RoleRate("Detective", 1, 100),

                // Judge: one role slot, 100% spawn chance.
                new RoleRate("Judge", 1, 100),

                new RoleRate("Influencer", 3, 80),
                new RoleRate("Shapeshifter", 1, 70),
                new RoleRate("Phantom", 1, 70),
                new RoleRate("Viper", 1, 70)
            };
        }

        // ========================================================
        // PRESET LOOKUP
        // ========================================================

        private static PresetDefinition FindPreset(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return null;

            string name = NormalizePresetName(arguments);

            switch (name)
            {
                case "hardgame":
                    return CreateHardGame();

                case "beginnergame":
                    return CreateBeginnerGame();

                default:
                    return null;
            }
        }

        private static string NormalizePresetName(string name)
        {
            return name.Trim()
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        // ========================================================
        // /custompreset <preset>
        // ========================================================

        internal static void CustomPreset(string arguments)
        {
            if (!RequireHostLobby())
                return;

            if (string.IsNullOrWhiteSpace(arguments))
            {
                Plugin.Reply(
                    "Usage: /custompreset HardGame or /custompreset BeginnerGame."
                );

                return;
            }

            PresetDefinition preset = FindPreset(arguments);

            if (preset == null)
            {
                Plugin.Reply(
                    "Unknown preset. Available presets: HardGame, BeginnerGame."
                );

                return;
            }

            object manager =
                ReflectionHelper.GetSingleton("GameOptionsManager");

            if (manager == null)
            {
                Plugin.ModLogger?.LogError(
                    "GameOptionsManager wasn't found while applying preset."
                );

                Plugin.Reply(
                    "Couldn't find GameOptionsManager. Check the game version."
                );

                return;
            }

            object options = ReflectionHelper.GetMemberValue(
                manager,
                "CurrentGameOptions",
                "currentGameOptions"
            );

            if (options == null)
            {
                Plugin.ModLogger?.LogError(
                    "CurrentGameOptions wasn't found while applying preset."
                );

                Plugin.Reply(
                    "Couldn't read the current game settings."
                );

                return;
            }

            int appliedOptions = 0;
            int appliedRoles = 0;

            List<string> failedOptions = new List<string>();
            List<string> failedRoles = new List<string>();

            // Do not sync after every individual option.
            // Apply the entire preset and synchronize once at the end.
            ApplyMainSettings(
                options,
                preset,
                ref appliedOptions,
                failedOptions
            );

            ApplyRoleSettings(
                options,
                preset,
                ref appliedRoles,
                failedRoles
            );

            if (appliedOptions == 0 && appliedRoles == 0)
            {
                Plugin.ModLogger?.LogError(
                    "Preset failed completely: " + preset.DisplayName
                );

                Plugin.Reply(
                    "Preset couldn't be applied. No compatible settings were found."
                );

                return;
            }

            SynchronizeOptions(manager, options);

            string result = BuildResultMessage(
                preset,
                appliedOptions,
                appliedRoles,
                failedOptions,
                failedRoles
            );

            Plugin.ModLogger?.LogInfo(result);

            if (failedOptions.Count > 0 || failedRoles.Count > 0)
            {
                Plugin.ModLogger?.LogWarning(
                    "Preset option failures: " +
                    string.Join(", ", failedOptions.ToArray())
                );

                Plugin.ModLogger?.LogWarning(
                    "Preset role failures: " +
                    string.Join(", ", failedRoles.ToArray())
                );
            }

            // One local message only. Additional details stay in the log.
            Plugin.Reply(result);
        }

        // ========================================================
        // MAIN OPTION APPLICATION
        // ========================================================

        private static void ApplyMainSettings(
            object options,
            PresetDefinition preset,
            ref int appliedOptions,
            List<string> failures)
        {
            if (options == null || preset == null)
                return;

            foreach (PresetOption option in preset.Options)
            {
                bool changed = ReflectionHelper.TryInvokeMethod(
                    options,
                    option.Setter,
                    new object[]
                    {
                        option.Name,
                        option.Value
                    }
                );

                if (changed)
                {
                    appliedOptions++;

                    Plugin.ModLogger?.LogDebug(
                        "Preset option applied: " +
                        option.Name
                    );
                }
                else
                {
                    failures.Add(option.Name);

                    Plugin.ModLogger?.LogWarning(
                        "Preset option wasn't found: " +
                        option.Name
                    );
                }
            }
        }

        // ========================================================
        // ROLE APPLICATION
        // ========================================================

        private static void ApplyRoleSettings(
            object options,
            PresetDefinition preset,
            ref int appliedRoles,
            List<string> failures)
        {
            if (options == null || preset == null)
                return;

            object roleOptions = FindRoleOptions(options);

            if (roleOptions == null)
            {
                Plugin.ModLogger?.LogWarning(
                    "Role options object couldn't be located."
                );
            }

            foreach (RoleRate role in preset.Roles)
            {
                bool changed = TrySetRoleRate(
                    options,
                    roleOptions,
                    role
                );

                if (changed)
                {
                    appliedRoles++;

                    Plugin.ModLogger?.LogDebug(
                        "Preset role applied: " +
                        role.Role
                    );
                }
                else
                {
                    failures.Add(role.Role);

                    Plugin.ModLogger?.LogWarning(
                        "Preset role wasn't applied: " +
                        role.Role
                    );
                }
            }
        }

        // ========================================================
        // ROLE OPTIONS LOOKUP
        // ========================================================

        private static object FindRoleOptions(object options)
        {
            if (options == null)
                return null;

            object roleOptions = ReflectionHelper.GetMemberValue(
                options,
                "RoleOptions",
                "roleOptions"
            );

            if (roleOptions != null)
                return roleOptions;

            Type type = options.GetType();

            while (type != null)
            {
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

                foreach (MethodInfo method in methods)
                {
                    if (method.GetParameters().Length != 0)
                        continue;

                    if (method.Name != "GetRoleOptions" &&
                        method.Name != "get_RoleOptions")
                    {
                        continue;
                    }

                    try
                    {
                        roleOptions = method.Invoke(
                            options,
                            null
                        );

                        if (roleOptions != null)
                            return roleOptions;
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger?.LogDebug(
                            "Role options getter failed: " +
                            ex.Message
                        );
                    }
                }

                type = type.BaseType;
            }

            return null;
        }

        // ========================================================
        // ROLE RATE SETTING
        // ========================================================

        private static bool TrySetRoleRate(
            object options,
            object roleOptions,
            RoleRate role)
        {
            if (role == null)
                return false;

            Type roleType = ReflectionHelper.FindType(
                "AmongUs.GameOptions.RoleTypes"
            );

            if (roleType == null)
            {
                roleType = ReflectionHelper.FindType(
                    "RoleTypes"
                );
            }

            if (roleType == null || !roleType.IsEnum)
            {
                Plugin.ModLogger?.LogWarning(
                    "RoleTypes enum wasn't found."
                );

                return false;
            }

            object roleValue;

            try
            {
                roleValue = Enum.Parse(
                    roleType,
                    role.Role,
                    true
                );
            }
            catch
            {
                // A role can be absent from this particular game build.
                Plugin.ModLogger?.LogWarning(
                    "Role doesn't exist in this game version: " +
                    role.Role
                );

                return false;
            }

            object[] arguments = new object[]
            {
                roleValue,
                role.Count,
                role.Chance
            };

            // Preferred API in many recent Among Us builds.
            if (roleOptions != null &&
                ReflectionHelper.TryInvokeMethod(
                    roleOptions,
                    "SetRoleRate",
                    arguments))
            {
                return true;
            }

            // Some builds expose SetRoleRate from the outer options object.
            if (options != null &&
                ReflectionHelper.TryInvokeMethod(
                    options,
                    "SetRoleRate",
                    arguments))
            {
                return true;
            }

            return false;
        }

        // ========================================================
        // SYNCHRONIZATION
        // ========================================================

        private static void SynchronizeOptions(
            object manager,
            object options)
        {
            if (manager == null || options == null)
                return;

            ReflectionHelper.SetMemberValue(
                manager,
                options,
                "GameHostOptions",
                "gameHostOptions"
            );

            object gameManager =
                ReflectionHelper.GetSingleton("GameManager");

            object logicOptions = ReflectionHelper.GetMemberValue(
                gameManager,
                "LogicOptions",
                "logicOptions"
            );

            if (logicOptions == null)
            {
                Plugin.ModLogger?.LogWarning(
                    "Preset options were modified, but GameManager.LogicOptions wasn't found."
                );

                return;
            }

            ReflectionHelper.TryInvokeMethod(
                logicOptions,
                "SetGameOptions",
                new object[]
                {
                    options
                }
            );

            bool synced = ReflectionHelper.TryInvokeMethod(
                logicOptions,
                "SyncOptions",
                new object[0]
            );

            if (!synced)
            {
                Plugin.ModLogger?.LogWarning(
                    "Preset options were modified, but SyncOptions wasn't found."
                );
            }
        }

        // ========================================================
        // RESULT MESSAGE
        // ========================================================

        private static string BuildResultMessage(
            PresetDefinition preset,
            int appliedOptions,
            int appliedRoles,
            List<string> failedOptions,
            List<string> failedRoles)
        {
            string result =
                preset.DisplayName +
                ": " +
                appliedOptions +
                "/" +
                preset.Options.Length +
                " main settings, " +
                appliedRoles +
                "/" +
                preset.Roles.Length +
                " roles applied.";

            if (failedOptions.Count > 0 || failedRoles.Count > 0)
            {
                result +=
                    " Some settings/roles unsupported; check LogOutput.log.";
            }

            return result;
        }
    }
}