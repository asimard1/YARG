using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Player;
using YARG.Song;

namespace YARG.Menu.Online
{
    /// <summary>
    /// In-lobby (room) view. The local player gets here after creating or
    /// joining a lobby; the source of truth for the lobby's state is
    /// <see cref="LobbyHubSession.CurrentLobby"/>, refreshed on every
    /// <see cref="LobbyHubSession.CurrentLobbyChanged"/>.
    /// </summary>
    public class LobbyViewMenu : MonoBehaviour
    {
        // Client-side cap on the queue length. Server may or may not enforce its own limit;
        // this is the friendly UI gate. Bump or remove once the server contract settles.
        private const int MaxQueueSize = 6;


        // Header (standard YARG Header prefab dragged in via Editor).
        // Drag the Header's main TMP into _headerMainText, sub TMP into _headerSubText,
        // and its Button into _backButton — OnEnable wires the click to OnLeaveClicked,
        // so no UnityEvent setup is needed in the inspector.
        [SerializeField]
        private TextMeshProUGUI _headerMainText;
        [SerializeField]
        private TextMeshProUGUI _headerSubText;
        [SerializeField]
        private Button _backButton;

        // Section headers — text is set every Refresh to reflect counts.
        [SerializeField]
        private TextMeshProUGUI _playersHeaderText;
        [SerializeField]
        private TextMeshProUGUI _songsHeaderText;

        [SerializeField]
        private Transform _playersContent;
        [SerializeField]
        private LobbyPlayer _playerPrefab;

        [SerializeField]
        private Transform _songsContent;
        [SerializeField]
        private QueuedSong _songPrefab;

        [SerializeField]
        private Transform _chatContent;
        [SerializeField]
        private ChatMessageCard _chatMessagePrefab;
        [SerializeField]
        private TMP_InputField _chatInputField;
        [SerializeField]
        private ScrollRect _chatScrollRect;

        // Diff-based: keyed by userId for players and Sequence for songs so a
        // refresh only destroys/creates cards that actually changed. Song cards
        // in particular do an async album-art load on first init — we don't
        // want to re-trigger that on every chat message or host-toggle.
        private readonly Dictionary<string, LobbyPlayer> _playerCards = new();
        private readonly Dictionary<long, QueuedSong>    _songCards   = new();

        // Chat is monotonic — append-only with a high-water mark on Sequence.
        private readonly List<ChatMessageCard> _chatCards = new();
        private long _lastChatSequenceRendered = -1;

        // Top-of-queue song preview, driven through the persistent MusicPlayer (the
        // pausable audio player on the menu's bottom bar). _previewSongHash tracks the
        // hash we last asked the player to lock onto, so re-renders for unrelated
        // refreshes (chat/players) don't restart the audio mid-play. Released in
        // OnDisable — which also fires when LobbyGameOrchestrator hides all menus on
        // game start, so the lock won't bleed past the lobby's lifecycle.
        private string _previewSongHash;

        // Tracks which nav scheme is currently pushed: the host variant includes a Start
        // entry, the non-host one does not. Repushed only on actual host transitions.
        private bool _schemePushedAsHost;

        // Cached session reference for safe unsubscribe even if Current changes.
        private LobbyHubSession _boundSession;

        private void OnEnable()
        {
            // Back is handled by the on-screen header button (_backButton → OnLeaveClicked, wired below).
            // Intentionally no Red entry here — the header button is the sole exit.
            // Initial scheme is the non-host variant (no Start). Refresh() promotes it to the
            // host scheme once the lobby's IsLocalHost is known. Doing it this way means a
            // joiner never sees the Start entry, and a host-transfer mid-session swaps the
            // scheme as soon as the CurrentLobbyChanged broadcast arrives.
            PushNavSchemeForRole(isHost: false);
            _schemePushedAsHost = false;

            // Child cards' OnDisable releases their album-art textures, so a
            // fresh enter must rebuild from scratch. CurrentLobbyChanged events
            // arriving while we're visible still take the cheap diff path.
            ClearAllCards();

            if (_chatInputField != null)
            {
                // Matches server validator: trimmed length must be <= 256.
                _chatInputField.characterLimit = 256;
                _chatInputField.onSubmit.RemoveListener(OnChatSubmit);
                _chatInputField.onSubmit.AddListener(OnChatSubmit);
            }

            if (_backButton != null)
            {
                _backButton.onClick.RemoveListener(OnLeaveClicked);
                _backButton.onClick.AddListener(OnLeaveClicked);
            }

            _boundSession = LobbyHubSession.Current;
            if (_boundSession == null)
            {
                YargLogger.LogError("LobbyView: opened without an active session");
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
                return;
            }

            _boundSession.CurrentLobbyChanged += Refresh;
            _boundSession.KickedFromLobby     += OnKickedFromLobby;
            Refresh();
        }

        private void OnDisable()
        {
            if (_boundSession != null)
            {
                _boundSession.CurrentLobbyChanged -= Refresh;
                _boundSession.KickedFromLobby     -= OnKickedFromLobby;
                _boundSession = null;
            }
            if (_chatInputField != null) _chatInputField.onSubmit.RemoveListener(OnChatSubmit);
            if (_backButton != null) _backButton.onClick.RemoveListener(OnLeaveClicked);
            // Releases the MusicPlayer lock so it returns to random rotation. Fires whether
            // the user navigates back to the lobby browser or LobbyGameOrchestrator hides all
            // menus on game start. (In the game-start case the orchestrator also hard-hides
            // the MusicPlayer GameObject, but unlocking first keeps the next re-enable clean.)
            ReleasePreviewLock();
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        /// <summary>
        /// Reads the latest <see cref="LobbyHubSession.CurrentLobby"/> and
        /// diffs its members + song queue into the scroll containers.
        /// </summary>
        private void Refresh()
        {
            var lobby = _boundSession?.CurrentLobby;
            if (lobby == null)
            {
                YargLogger.LogInfo("LobbyView: refresh — no current lobby");
                ClearAllCards();
                ReleasePreviewLock();
                return;
            }

            YargLogger.LogInfo(
                $"LobbyView: refresh — id={lobby.LobbyId}, name='{lobby.LobbyName}', "
                + $"host={lobby.HostName}, status={lobby.Status}, "
                + $"members={lobby.Members.Count}/{lobby.MaxPlayers}, "
                + $"queue={lobby.SongQueue.Count}, chat={lobby.ChatHistory.Count}, "
                + $"lobbyLibrary={lobby.LobbySongLibrary.Count}, isHost={lobby.IsLocalHost}");

            RefreshNavSchemeForRole(lobby.IsLocalHost);
            RefreshHeader(lobby);
            RefreshPlayers(lobby);
            RefreshSongs(lobby);
            RefreshChat(lobby);
            RefreshTopSongPreview(lobby);
        }

        /// <summary>
        /// Locks the persistent MusicPlayer onto the song at the head of the queue
        /// (looped via SongEnd → NextSong → same locked song). When the queue is empty
        /// or the local library can't resolve the hash, releases the lock so the player
        /// falls back to its normal random rotation.
        /// </summary>
        private void RefreshTopSongPreview(LobbyRoomState lobby)
        {
            string topHash = lobby.SongQueue.Count > 0 ? lobby.SongQueue[0].SongHash : null;
            if (topHash == _previewSongHash) return;
            _previewSongHash = topHash;

            SongEntry songToLock = null;
            if (!string.IsNullOrEmpty(topHash)
                && SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(topHash), out var entries)
                && entries.Count > 0)
            {
                songToLock = entries[0];
            }

            MusicPlayer.SetLockedSong(songToLock);
        }

        private void ReleasePreviewLock()
        {
            _previewSongHash = null;
            MusicPlayer.SetLockedSong(null);
        }

        /// <summary>
        /// Pops the current nav scheme (if one was pushed) and pushes the appropriate variant
        /// for the caller's role. Host gets AddSong + StartGame; non-host gets just AddSong.
        /// </summary>
        private void PushNavSchemeForRole(bool isHost)
        {
            var entries = new List<NavigationScheme.Entry>
            {
                new NavigationScheme.Entry(MenuAction.Yellow, "Menu.Online.AddSong", OnQueueSongClicked),
            };
            if (isHost)
            {
                entries.Add(new NavigationScheme.Entry(MenuAction.Start, "Menu.Online.StartGame", OnStartGameClicked));
            }
            Navigator.Instance.PushScheme(new NavigationScheme(entries, true));
        }

        /// <summary>
        /// Swaps the pushed nav scheme on a host transition. No-op when the role is
        /// unchanged, so repeated Refresh calls for chat/players don't churn the stack.
        /// </summary>
        private void RefreshNavSchemeForRole(bool isHost)
        {
            if (isHost == _schemePushedAsHost) return;
            if (Navigator.Instance) Navigator.Instance.PopScheme();
            PushNavSchemeForRole(isHost);
            _schemePushedAsHost = isHost;
        }

        private void RefreshHeader(LobbyRoomState lobby)
        {
            if (_headerMainText)
            {
                _headerMainText.text = $"{lobby.LobbyName} #{lobby.LobbyId}";
            }
            if (_headerSubText)
            {
                _headerSubText.text = Localize.Key("Menu.Online.LobbyHeaderSubText");
            }
        }

        private void RefreshPlayers(LobbyRoomState lobby)
        {
            bool   isLocalHost = lobby.IsLocalHost;
            string localUserId = LobbyHubSession.Current?.LocalUserId;

            if (_playersHeaderText)
            {
                _playersHeaderText.text = Localize.KeyFormat(
                    "Menu.Online.PlayersHeader", lobby.Members.Count, lobby.MaxPlayers);
            }

            // Local profile is read live from PlayerContainer (most up-to-date).
            // Remote rows read from LobbyRoomState.MemberInstruments, which is populated by
            // FromEnter and OnPlayerJoined as the server reports each member's instrument.
            Instrument? localInstrument = PlayerContainer.Players.Count > 0
                ? PlayerContainer.Players[0].Profile.CurrentInstrument
                : null;

            // Static (non-card) children of the content — e.g., a header label authored
            // into the prefab. Snapshot before instantiating so card siblings sit AFTER
            // those static children regardless of how many you've added.
            int staticChildCount = _playersContent.childCount - _playerCards.Count;
            int memberCount      = lobby.Members.Count;

            var seen = new HashSet<string>();
            for (int i = 0; i < memberCount; i++)
            {
                string userId = lobby.Members[i];
                seen.Add(userId);

                if (!_playerCards.TryGetValue(userId, out var card))
                {
                    card = Instantiate(_playerPrefab, _playersContent);
                    _playerCards[userId] = card;
                }

                bool isSelf = userId == localUserId;
                Instrument? memberInstrument;
                if (isSelf)
                {
                    memberInstrument = localInstrument;
                }
                else if (lobby.MemberInstruments.TryGetValue(userId, out var ri))
                {
                    memberInstrument = ri;
                }
                else
                {
                    memberInstrument = null;
                }
                string instrumentSprite = memberInstrument?.ToResourceName();

                // Missing entry → assume ready (true). The server's snapshot
                // populates the dict on EnterLobby and the OnPlayerLobbyReadyChanged
                // event keeps it fresh; an unseen userId is the brief window
                // between a join broadcast and the first state event.
                bool isBackInLobby = !lobby.MemberIsBackInLobby.TryGetValue(userId, out var ready) || ready;

                // Cheap to re-init: just text + button visibility + click listeners.
                card.Initialize(
                    userId,
                    lobby.GetDisplayName(userId),
                    instrumentSprite,
                    isLocalHost,
                    isSelf:        isSelf,
                    isBackInLobby: isBackInLobby,
                    onKick:        () => OnKickPlayerClicked(userId),
                    onMakeHost:    () => OnMakeHostClicked(userId));
                // Newest member (last in lobby.Members) renders immediately after the static
                // children. Member at list-index i → sibling staticChildCount + (count-1-i).
                card.transform.SetSiblingIndex(staticChildCount + memberCount - 1 - i);
            }

            RemoveStale(_playerCards, seen);
        }

        private void RefreshSongs(LobbyRoomState lobby)
        {
            bool isLocalHost = lobby.IsLocalHost;
            int  count       = lobby.SongQueue.Count;

            if (_songsHeaderText)
            {
                // Use Singular for exactly 1 (the placeholder is implicit "1"), Plural otherwise.
                _songsHeaderText.text = count == 1
                    ? Localize.Key("Menu.Online.SongsQueued.Singular")
                    : Localize.KeyFormat("Menu.Online.SongsQueued.Plural", count);
            }

            // Static (non-card) children of the song content — e.g., a header label
            // authored into the prefab. Snapshot before instantiating so card siblings
            // sit AFTER those static children regardless of how many you've added.
            int staticChildCount = _songsContent.childCount - _songCards.Count;

            var seen = new HashSet<long>();
            for (int i = 0; i < lobby.SongQueue.Count; i++)
            {
                var dto = lobby.SongQueue[i];
                seen.Add(dto.Sequence);

                if (_songCards.TryGetValue(dto.Sequence, out var card))
                {
                    // Existing card — host status may have changed but the
                    // hash/sequence pairing is immutable, so skip the album load.
                    card.SetRemoveButtonVisible(isLocalHost);
                }
                else
                {
                    card = Instantiate(_songPrefab, _songsContent);
                    _songCards[dto.Sequence] = card;
                    long sequence = dto.Sequence;
                    card.Initialize(
                        HashWrapper.FromString(dto.SongHash),
                        isLocalHost,
                        () => OnRemoveQueuedSongClicked(sequence));
                }
                // Queue order preserved (oldest first), offset past any static children.
                card.transform.SetSiblingIndex(staticChildCount + i);
            }

            RemoveStale(_songCards, seen);
        }

        private void RefreshChat(LobbyRoomState lobby)
        {
            // Append-only: chat is monotonic. Walk new messages by Sequence and
            // append a card for each one beyond what we've already rendered.
            bool anyAppended = false;
            foreach (var msg in lobby.ChatHistory)
            {
                if (msg.Sequence <= _lastChatSequenceRendered)
                {
                    continue;
                }

                var card = Instantiate(_chatMessagePrefab, _chatContent);
                card.Initialize(msg);
                _chatCards.Add(card);
                _lastChatSequenceRendered = msg.Sequence;
                anyAppended = true;
            }
            if (anyAppended)
            {
                ScrollChatToBottom();
            }
        }

        private void ScrollChatToBottom()
        {
            if (!_chatScrollRect)
            {
                return;
            }

            ScrollChatToBottomDeferredAsync().Forget();
        }

        private async UniTaskVoid ScrollChatToBottomDeferredAsync()
        {
            // TMP cells with wrapping don't finalize their height in the same frame they're
            // instantiated — only the next layout pass. If we scroll immediately, the
            // ScrollRect computes verticalNormalizedPosition against a Content height that's
            // still missing the newest row, and the row ends up clipped below the viewport.
            // Wait one frame so ContentSizeFitter sees the real per-row preferred sizes.
            await UniTask.NextFrame();
            if (!this || !_chatScrollRect)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_chatScrollRect.content);
            Canvas.ForceUpdateCanvases();
            _chatScrollRect.verticalNormalizedPosition = 0f;
        }

        private static void RemoveStale<TKey, TCard>(Dictionary<TKey, TCard> cards, HashSet<TKey> seen)
            where TCard : Component
        {
            // Two-pass to avoid mutating during enumeration.
            List<TKey> toRemove = null;
            foreach (var key in cards.Keys)
            {
                if (seen.Contains(key))
                {
                    continue;
                }

                toRemove ??= new List<TKey>();
                toRemove.Add(key);
            }
            if (toRemove == null)
            {
                return;
            }

            foreach (var key in toRemove)
            {
                Destroy(cards[key].gameObject);
                cards.Remove(key);
            }
        }

        private void ClearAllCards()
        {
            foreach (var card in _playerCards.Values)
            {
                Destroy(card.gameObject);
            }

            _playerCards.Clear();

            foreach (var card in _songCards.Values)
            {
                Destroy(card.gameObject);
            }

            _songCards.Clear();

            foreach (var card in _chatCards)
            {
                Destroy(card.gameObject);
            }

            _chatCards.Clear();
            _lastChatSequenceRendered = -1;
        }

        private void OnKickedFromLobby(string reason)
        {
            YargLogger.LogInfo($"LobbyView: kicked from lobby — '{reason}'");
            DialogManager.Instance.ShowMessage("Kicked from lobby", reason);
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
        }

        public void OnLeaveClicked() => LeaveAsync().Forget();

        private async UniTaskVoid LeaveAsync()
        {
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session != null)
                {
                    await session.LeaveLobbyAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }
            finally
            {
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
            }
        }

        public void OnReadyToggleClicked()
        {
            // TODO: ready-state isn't part of the lobby contract yet.
            YargLogger.LogWarning("LobbyView: OnReadyToggleClicked — not yet wired");
        }

        public void OnSendChatClicked() => SendChatAsync().Forget();

        // TMP's onSubmit fires with the submitted text on Enter; route both
        // entry points through the same coroutine.
        private void OnChatSubmit(string _) => OnSendChatClicked();

        private async UniTaskVoid SendChatAsync()
        {
            if (_chatInputField == null) return;
            string text = _chatInputField.text;
            if (string.IsNullOrWhiteSpace(text)) return;

            _chatInputField.text = string.Empty;
            _chatInputField.ActivateInputField(); // keep focus for the next message

            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not send chat",
                        "The lobby session is no longer active.");
                    _chatInputField.text = text;
                    return;
                }
                await session.SendChatMessageAsync(text, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not send chat", ex.Message);
                // Restore the text so the user doesn't lose what they typed.
                _chatInputField.text = text;
            }
        }

        public void OnQueueSongClicked()
        {
            // Re-seed the lobby allow-list every time the picker opens. The
            // filter is cleared in MusicLibraryMenu.OnDisable, so we own the
            // seed here. Reference assignment to the lobby's HashSet means
            // live updates from OnLobbySongLibraryUpdatedAsync still flow.
            var lobby = (_boundSession ?? LobbyHubSession.Current)?.CurrentLobby;

            // Client-side cap. Server-side limit may differ (or be absent); this is
            // a friendly gate so the user can't even open the picker when full.
            int currentCount = lobby?.SongQueue.Count ?? 0;
            if (currentCount >= MaxQueueSize)
            {
                DialogManager.Instance.ShowMessage(
                    "Queue full",
                    $"The song queue holds at most {MaxQueueSize} songs. Remove one before adding another.");
                return;
            }

            MusicLibraryMenu.AllowedSongHashes = lobby?.LobbySongLibrary;

            MusicLibraryMenu.SongPickedCallback = OnSongPicked;
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.MusicLibrary);
        }

        private void OnSongPicked(HashWrapper hash)
        {
            YargLogger.LogInfo($"LobbyView: song picked for queue — hash={hash}");
            QueueSongAsync(hash).Forget();
        }

        private async UniTaskVoid QueueSongAsync(HashWrapper hash)
        {
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not queue song",
                        "The lobby session is no longer active.");
                    return;
                }
                await session.QueueSongAsync(hash, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not queue song", ex.Message);
            }
        }

        public void OnRemoveQueuedSongClicked(long sequence) => RemoveQueuedSongAsync(sequence).Forget();

        private async UniTaskVoid RemoveQueuedSongAsync(long sequence)
        {
            YargLogger.LogInfo($"LobbyView: remove queued song — sequence={sequence}");
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not remove song",
                        "The lobby session is no longer active.");
                    return;
                }
                await session.RemoveQueuedSongAsync(sequence, CancellationToken.None);
                // Server broadcasts OnSongDequeued → CurrentLobbyChanged → Refresh handles the card removal.
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not remove song", ex.Message);
            }
        }

        public void OnKickPlayerClicked(string userId)
        {
            // TODO: ILobbyHub.KickPlayer(new KickPlayerArgs(userId))
            YargLogger.LogWarning($"LobbyView: OnKickPlayerClicked({userId}) — not yet wired");
        }

        public void OnMakeHostClicked(string userId)
        {
            // TODO: ILobbyHub.TransferHost(new TransferHostArgs(userId))
            YargLogger.LogWarning($"LobbyView: OnMakeHostClicked({userId}) — not yet wired");
        }

        public void OnStartGameClicked() => StartGameAsync().Forget();

        private async UniTaskVoid StartGameAsync()
        {
            try
            {
                var session = _boundSession ?? LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not start game",
                        "The lobby session is no longer active.");
                    return;
                }

                // Client-side preflight on the same gate the server enforces.
                // Catching it here gives the host immediate feedback with a
                // useful message, listing which players haven't reported back
                // from the results screen yet. The server still re-validates.
                var lobby = session.CurrentLobby;
                if (lobby != null && !lobby.AllMembersBackInLobby)
                {
                    var stillOut = new List<string>();
                    foreach (var userId in lobby.Members)
                    {
                        if (lobby.MemberIsBackInLobby.TryGetValue(userId, out var ready) && !ready)
                        {
                            stillOut.Add(lobby.GetDisplayName(userId));
                        }
                    }
                    DialogManager.Instance.ShowMessage(
                        "Waiting for players",
                        stillOut.Count > 0
                            ? "Still on the results screen: " + string.Join(", ", stillOut)
                            : "Some players haven't returned to the lobby yet.");
                    return;
                }

                await session.StartGameAsync(CancellationToken.None);
                // No state mutation here — LobbyGameOrchestrator listens for the
                // OnGameStarted callback and drives the loadout flow + UDP connect.
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                // The server's hub-error string for the gate failure is
                // "players_still_in_results". Surface a friendlier message in
                // case the preflight above raced and the server caught it.
                var msg = ex.Message;
                if (msg != null && msg.Contains("players_still_in_results"))
                {
                    msg = "One or more players are still on the results screen. Wait for everyone to return to the lobby.";
                }
                DialogManager.Instance.ShowMessage("Could not start game", msg);
            }
        }
    }
}
