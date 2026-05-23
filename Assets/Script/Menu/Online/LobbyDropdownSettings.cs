using System;
using YARG.Core.Extensions;
using YARG.Localization;
using YARG.Settings.Types;

namespace YARG.Menu.Online
{
    // Three DropdownSetting<T> subclasses that back the lobby browser's filter
    // dropdowns through DropdownSettingVisual — same pattern as MusicLibrary's
    // FilterSortDropdownSetting. Each is constructed fresh per BuildLayout
    // with the owner's current value, and writes back via the onChange action.
    //
    // localizable: false because we override ValueToString to use our own
    // localization key roots under Menu.Online.FiltersMenu.*, rather than the
    // DropdownSettingVisual's default Settings.PresetSetting.* path.

    public sealed class LobbySortDropdownSetting : DropdownSetting<LobbySortAttribute>
    {
        public LobbySortDropdownSetting(LobbySortAttribute initial, Action<LobbySortAttribute> onChange)
            : base(initial, onChange, localizable: false)
        {
        }

        public override void UpdateValues()
        {
            _possibleValues.Clear();
            foreach (var attr in EnumExtensions<LobbySortAttribute>.Values)
            {
                _possibleValues.Add(attr);
            }
        }

        public override string ValueToString(LobbySortAttribute value) => value switch
        {
            LobbySortAttribute.LobbyName   => Localize.Key("Menu.Online.FiltersMenu.SortBy.LobbyName"),
            LobbySortAttribute.HostName    => Localize.Key("Menu.Online.FiltersMenu.SortBy.HostName"),
            LobbySortAttribute.SongName    => Localize.Key("Menu.Online.FiltersMenu.SortBy.SongName"),
            LobbySortAttribute.PlayerCount => Localize.Key("Menu.Online.FiltersMenu.SortBy.PlayerCount"),
            _                              => value.ToString(),
        };
    }

    public sealed class LobbyGameModeDropdownSetting : DropdownSetting<LobbyGameModeFilter>
    {
        public LobbyGameModeDropdownSetting(LobbyGameModeFilter initial, Action<LobbyGameModeFilter> onChange)
            : base(initial, onChange, localizable: false)
        {
        }

        public override void UpdateValues()
        {
            _possibleValues.Clear();
            foreach (var v in EnumExtensions<LobbyGameModeFilter>.Values)
            {
                _possibleValues.Add(v);
            }
        }

        public override string ValueToString(LobbyGameModeFilter value) => value switch
        {
            LobbyGameModeFilter.Any    => Localize.Key("Menu.Online.FiltersMenu.GameMode.Any"),
            LobbyGameModeFilter.Band   => Localize.Key("Menu.Online.FiltersMenu.GameMode.Band"),
            LobbyGameModeFilter.Versus => Localize.Key("Menu.Online.FiltersMenu.GameMode.Versus"),
            _                          => value.ToString(),
        };
    }

    public sealed class LobbyStatusDropdownSetting : DropdownSetting<LobbyStatusFilter>
    {
        public LobbyStatusDropdownSetting(LobbyStatusFilter initial, Action<LobbyStatusFilter> onChange)
            : base(initial, onChange, localizable: false)
        {
        }

        public override void UpdateValues()
        {
            _possibleValues.Clear();
            foreach (var v in EnumExtensions<LobbyStatusFilter>.Values)
            {
                _possibleValues.Add(v);
            }
        }

        public override string ValueToString(LobbyStatusFilter value) => value switch
        {
            LobbyStatusFilter.Any        => Localize.Key("Menu.Online.FiltersMenu.Status.Any"),
            LobbyStatusFilter.SongSelect => Localize.Key("Menu.Online.FiltersMenu.Status.SongSelect"),
            LobbyStatusFilter.InGame     => Localize.Key("Menu.Online.FiltersMenu.Status.InGame"),
            _                            => value.ToString(),
        };
    }
}
