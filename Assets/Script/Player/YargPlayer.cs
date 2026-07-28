using System;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Replays;
using YARG.Input;
using YARG.Online.Game.Contracts.Packets;
using YARG.Settings.Customization;
using YARG.Themes;

namespace YARG.Player
{
    public class YargPlayer : IDisposable
    {
        public event MenuInputEvent MenuInput;

        public YargProfile Profile { get; private set; }

        /// <summary>
        /// Whether or not the player is sitting out. This is not needed in <see cref="Profile"/> as
        /// players that are sitting out are not included in replays.
        /// </summary>
        public bool SittingOut;

        public bool InputsEnabled { get; private set; }
        public ProfileBindings Bindings { get; private set; }

        public EnginePreset    EnginePreset    { get; private set; }
        public ThemePreset     ThemePreset     { get; private set; }
        public ColorProfile    ColorProfile    { get; private set; }
        public CameraPreset    CameraPreset    { get; private set; }
        public HighwayPreset   HighwayPreset   { get; private set; }
        public RockMeterPreset RockMeterPreset { get; private set; }

        /// <summary>
        /// Whether or not the score is valid.
        /// </summary>
        /// <remarks>Could be invalidated due abusing pauses or no fail mode.</remarks>
        public bool IsScoreValid { get; set; } = true;

        public bool IsReplay { get; private set; }
        public int ReplayIndex = -1;

        /// <summary>
        /// True for players whose inputs arrive over the network (online multiplayer remote
        /// peers). Their <see cref="Bindings"/> is null and the gameplay scene drives their
        /// engine from a per-peer network buffer instead of from local input subscriptions.
        /// </summary>
        public bool IsRemote { get; private set; }

        /// <summary>
        /// Identifies the remote peer this player corresponds to. Only meaningful when
        /// <see cref="IsRemote"/> is true.
        /// </summary>
        public int RemotePeerId { get; private set; }

        /// <summary>
        /// Overrides the engine parameters in the gameplay player.
        /// This is only used when loading replays.
        /// </summary>
        public BaseEngineParameters EngineParameterOverride { get; set; }

        public bool IsMissingMicrophone => !IsReplay && !IsRemote && Profile.GameMode == GameMode.Vocals && Bindings.Microphone == null && !Profile.IsBot;
        public bool IsMissingInputDevice => !IsReplay && !IsRemote && Profile.GameMode != GameMode.Vocals && !Bindings.HasDeviceAssigned && !Profile.IsBot;

        public YargPlayer(YargProfile profile, ProfileBindings bindings)
        {
            Profile = profile;
            Bindings = bindings;
            IsReplay = false;
        }

        /// <summary>
        /// Constructs a placeholder player whose engine will be driven by network inputs from
        /// the named remote peer. Visual presets default to the local user's defaults -- only
        /// the fields that affect engine scoring (instrument, difficulty, EnginePreset) come
        /// from the wire, so two clients computing the same remote engine agree on the score.
        /// </summary>
        public YargPlayer(int remotePeerId, PeerLoadout loadout)
        {
            var instrument = (Instrument)(byte)loadout.Instrument;
            Profile = new YargProfile
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrEmpty(loadout.DisplayName) ? loadout.UserId : loadout.DisplayName,
                IsBot = false,
                GameMode = instrument.ToNativeGameMode(),
                CurrentInstrument = instrument,
                PreferredInstrument = instrument,
                CurrentDifficulty = (Difficulty)(byte)loadout.Difficulty,
                DifficultyFallback = (Difficulty)(byte)loadout.Difficulty,
                EnginePreset = loadout.EnginePreset,
                NoteSpeed = loadout.NoteSpeed,
            };

            // CurrentModifiers has a private setter; apply the bitmask via the public OR-in
            // helper. Starting from Modifier.None, the OR equals the input.
            if (loadout.Modifiers != 0)
            {
                Profile.AddSingleModifier((Modifier)loadout.Modifiers);
            }
            Bindings = null;
            IsReplay = false;
            IsRemote = true;
            RemotePeerId = remotePeerId;

            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            ThemePreset = ThemePreset.Default;
            ColorProfile = ColorProfile.Default;
            CameraPreset = CameraPreset.Default;
            HighwayPreset = HighwayPreset.Default;
            RockMeterPreset = YARG.Core.Game.RockMeterPreset.Normal;
        }

        public YargPlayer(ReplayFrame frame, ReplayData replay)
        {
            Profile = frame.Profile;
            Bindings = null;
            EngineParameterOverride = frame.EngineParameters;
            IsReplay = true;

            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            ThemePreset = CustomContentManager.ThemePresets.GetPresetById(Profile.ThemePreset)
                ?? ThemePreset.Default;
            ColorProfile = replay.GetColorProfile(Profile.ColorProfile)
                ?? CustomContentManager.ColorProfiles.GetPresetById(Profile.ColorProfile)
                ?? ColorProfile.Default;
            CameraPreset = replay.GetCameraPreset(Profile.CameraPreset)
                ?? CustomContentManager.CameraSettings.GetPresetById(Profile.CameraPreset)
                ?? CameraPreset.Default;

            HighwayPreset = CustomContentManager.HighwayPresets.GetPresetById(Profile.HighwayPreset)
                ?? HighwayPreset.Default;

            RockMeterPreset = replay.GetRockMeterPreset(Profile.RockMeterPreset)
                ?? CustomContentManager.RockMeterPresets.GetPresetById(Profile.RockMeterPreset)
                ?? RockMeterPreset.Normal;
        }

        public void SwapToProfile(YargProfile profile, ProfileBindings bindings, bool resolveDevices)
        {
            // Force-disable inputs
            bool enabled = InputsEnabled;
            DisableInputs();

            // Swap to the new profile
            Bindings?.Dispose();
            Profile = profile;
            Bindings = bindings;

            // Resolve bindings
            if (resolveDevices)
            {
                Bindings?.ResolveDevices();
            }

            // Re-enable inputs
            if (enabled)
            {
                EnableInputs();
            }
        }

        public void RefreshPresets()
        {
            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            Profile.EnginePreset = EnginePreset.Id;
            ThemePreset = CustomContentManager.ThemePresets.GetPresetById(Profile.ThemePreset)
                ?? ThemePreset.Default;
            Profile.ThemePreset = ThemePreset.Id;
            ColorProfile = CustomContentManager.ColorProfiles.GetPresetById(Profile.ColorProfile)
                ?? ColorProfile.Default;
            Profile.ColorProfile = ColorProfile.Id;
            CameraPreset = CustomContentManager.CameraSettings.GetPresetById(Profile.CameraPreset)
                ?? CameraPreset.Default;
            Profile.CameraPreset = CameraPreset.Id;
            HighwayPreset = CustomContentManager.HighwayPresets.GetPresetById(Profile.HighwayPreset)
                ?? HighwayPreset.Default;
            Profile.HighwayPreset = HighwayPreset.Id;
            RockMeterPreset = CustomContentManager.RockMeterPresets.GetPresetById(Profile.RockMeterPreset) ??
                RockMeterPreset.Normal;
            Profile.RockMeterPreset = RockMeterPreset.Id;

        }

        public void EnableInputs()
        {
            if (InputsEnabled || Bindings == null)
            {
                return;
            }

            Bindings.EnableInputs();
            Bindings.MenuInputProcessed += OnMenuInput;
            InputManager.RegisterPlayer(this);

            InputsEnabled = true;
        }

        public void DisableInputs()
        {
            if (!InputsEnabled || Bindings == null)
            {
                return;
            }

            Bindings.DisableInputs();
            Bindings.MenuInputProcessed -= OnMenuInput;
            InputManager.UnregisterPlayer(this);

            InputsEnabled = false;
        }

        private void OnMenuInput(ref GameInput input)
        {
            MenuInput?.Invoke(this, ref input);
        }

        public void Dispose()
        {
            DisableInputs();
            Bindings?.Dispose();
        }
    }
}