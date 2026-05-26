using System;
using TMPro;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.Settings;
using YARG.Online;
using YARG.Player;
using YARG.Settings;

namespace YARG.Menu.Main
{
    public class MainMenu : MonoBehaviour
    {
        private static bool _antiPiracyDialogShown;

        [SerializeField]
        private TextMeshProUGUI _versionText;

        private void Start()
        {
            _versionText.text = GlobalVariables.Instance.CurrentVersion;

            // Show the anti-piracy dialog if it hasn't been shown already
            // Also only show it once per game launch
            if (!_antiPiracyDialogShown && SettingsManager.Settings.ShowAntiPiracyDialog)
            {
                DialogManager.Instance.ShowOneTimeMessage(
                    "Menu.Dialog.AntiPiracy",
                    () =>
                    {
                        SettingsManager.Settings.ShowAntiPiracyDialog = false;
                        SettingsManager.SaveSettings();
                    });

                _antiPiracyDialogShown = true;
            }

            if (SettingsMenu.ConsumeOpenOnNextMenuLoad())
            {
                SettingsMenu.Instance.gameObject.SetActive(true);
            }
        }

        private void OnEnable()
        {
            // Set navigation scheme
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                new NavigationScheme.Entry(MenuAction.Select, "Menu.Main.GoToCurrentlyPlaying", CurrentlyPlaying)
            }, true));
        }

        private void OnDisable()
        {
            Navigator.Instance?.PopScheme();
        }

        public void CurrentlyPlaying()
        {
            MusicLibraryMenu.RequestGoToCurrentlyPlaying(MusicPlayer.NowPlaying);
            QuickPlay();
        }

        public void QuickPlay()
        {
            var menu = MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary, false);

            MusicLibraryMenu.LibraryMode = MusicLibraryMode.QuickPlay;

            menu.gameObject.SetActive(true);
        }

        public async void Online()
        {
            // Hard gate: the online flow assumes at least one connected player
            // (lobby member instrument, loadout RPC, score tracking, etc. all read
            // from PlayerContainer.Players[0]). Letting the user enter with an
            // empty roster lands them in a lobby they can't actually queue/start
            // from. MusicLibrary already shows a "no player" warning for the
            // analogous solo case; for online we hard-block at the entry point
            // with a dialog rather than let them stumble in.
            if (PlayerContainer.Players.Count == 0)
            {
                YargLogger.LogInfo("MainMenu: Online blocked -- no profiles connected");
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Main.OnlineRequiresProfile.Title"),
                    Localize.Key("Menu.Main.OnlineRequiresProfile.Body"));
                return;
            }

            if (PlayerContainer.HasAnyBotsActive())
            {
                YargLogger.LogInfo("MainMenu: Online blocked. At least one connected profile is a bot");
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Main.OnlineNoBots.Title"),
                    Localize.Key("Menu.Main.OnlineNoBots.Body"));
                return;
            }

            YargLogger.LogInfo("MainMenu: Online button pressed -- authenticating before push");
            using var context = new LoadingContext();
            context.SetLoadingText("Signing in...");

            // Auth + connect are both treated as soft failures: if either
            // step throws, we still navigate into the Online menu so the
            // local LAN host/join flow stays reachable (it doesn't depend
            // on the central matchmaking service). A warning dialog tells
            // the user the cause + scope. The public lobby browser will
            // simply stay empty since LobbyHubSession.Current ends up null.
            bool serverReachable = true;

            var provider = new OnlineAccessTokenProvider(OnlineAccessTokenProvider.ResolveDefaultAuthName());
            try
            {
                await provider.EnsureAuthenticatedAsync();
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                serverReachable = false;
            }

            if (serverReachable)
            {
                context.SetLoadingText("Connecting...");
                try
                {
                    await LobbyHubSession.InitializeAsync(provider);
                }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex);
                    serverReachable = false;
                }
            }

            // Transition to the Online menu FIRST, then surface any error
            // dialog. The dialog needs to render on top of the destination
            // menu's UI -- if we show it before the menu switch, the menu
            // transition tears down the dialog's parent canvas (or steals
            // focus) and the user lands on the Online menu with no
            // explanation for why the lobby browser is empty.
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);

            if (!serverReachable)
            {
                YargLogger.LogWarning("MainMenu: online server unavailable -- proceeded to Online menu in LAN-only mode");
                DialogManager.Instance.ShowMessage(
                    YARG.Localization.Localize.Key("Menu.Online.ServerUnavailable.Title"),
                    YARG.Localization.Localize.Key("Menu.Online.ServerUnavailable.Body"));
            }
            else
            {
                YargLogger.LogInfo("MainMenu: connected -- opened Online menu");
            }
        }

        public void Practice()
        {
            var menu = MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary, false);

            MusicLibraryMenu.LibraryMode = MusicLibraryMode.Practice;

            menu.gameObject.SetActive(true);
        }

        public void Profiles()
        {
            MenuManager.Instance.PushMenu(MenuManager.Menu.ProfileList);
        }

        public void Replays()
        {
            MenuManager.Instance.PushMenu(MenuManager.Menu.History);
        }

        public void Credits()
        {
            MenuManager.Instance.PushMenu(MenuManager.Menu.Credits);
        }

        public void Settings()
        {
            SettingsMenu.Instance.gameObject.SetActive(true);
        }

        public void Exit()
        {
#if UNITY_EDITOR

            UnityEditor.EditorApplication.isPlaying = false;

#else
			Application.Quit();

#endif
        }

        public void OpenDiscord()
        {
            Application.OpenURL("https://discord.gg/sqpu4R552r");
        }

        public void OpenTwitter()
        {
            Application.OpenURL("https://twitter.com/YARGGame");
        }

        public void OpenGithub()
        {
            Application.OpenURL("https://github.com/YARC-Official/YARG");
        }
    }
}
