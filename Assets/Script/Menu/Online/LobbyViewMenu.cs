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
using YARG.Online.Lobbies.Contracts.Enums;
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
        // Header-row "Public Lobby" / "Private Lobby" label. Drag the new TMP into
        // this slot in the Inspector. May be unwired on prefabs that haven't been
        // updated yet; RefreshHeader silently skips in that case.
        [SerializeField]
        private TextMeshProUGUI _lobbyTypeText;
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
        // Single shared popup for host actions (Make Host / Kick). Hosted on
        // the menu prefab as an inactive child; each player card invokes it
        // via Initialize with the appropriate target's name + callbacks.
        // May be unwired on prefabs that haven't been updated — the player
        // card logs a warning and no-ops the edit button click in that case.
        [SerializeField]
        private LobbyPlayerActionsPopup _playerActionsPopup;

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

            ConfigureChatScroll();

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
        // Blue press-and-hold threshold. Crossing this fires OnShowCodeHold instead
        // of OnCopyCodeClicked. 0.5 s is long enough that a quick tap reliably maps
        // to the copy action (typical click is <100 ms) but short enough that the
        // "hold to show" affordance reveals quickly.
        private const float CopyCodeHoldSeconds = 0.5f;

        private void PushNavSchemeForRole(bool isHost)
        {
            var entries = new List<NavigationScheme.Entry>
            {
                new NavigationScheme.Entry(MenuAction.Yellow, "Menu.Online.AddSong", OnQueueSongClicked),
                // Copy Code is available to host AND non-host — anyone in the lobby
                // can pass the code along to a friend joining via JoinByCode. Tap
                // copies the code silently to the clipboard; press-and-hold pops a
                // dialog displaying the code so it can be read aloud / shown on
                // stream without committing to clipboard. Nav-only — no on-screen
                // button on the lobby header; the Blue action is the only entry point.
                new NavigationScheme.Entry(
                    MenuAction.Blue,
                    "Menu.Online.CopyCode",
                    handler:       OnCopyCodeClicked,
                    onHoldHandler: OnShowCodeHold,
                    holdSeconds:   CopyCodeHoldSeconds),
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
                _headerMainText.text = lobby.LobbyName;
            }
            if (_headerSubText)
            {
                _headerSubText.text = Localize.Key("Menu.Online.LobbyHeaderSubText");
            }
            if (_lobbyTypeText)
            {
                _lobbyTypeText.text = Localize.Key(lobby.IsPublic
                    ? "Menu.Online.LobbyType.Public"
                    : "Menu.Online.LobbyType.Private");
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

            // Host pinned to sibling 0; everyone else ranked by their position in
            // lobby.Members, which the session appends in join order (FromEnter seeds
            // the snapshot order, OnPlayerJoined appends new arrivals at the tail).
            int nonHostRank = 0;

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

                // Stage derivation lives on LobbyRoomState so the dialog text and
                // the row colour stay in sync. Missing IsBackInLobby entries are
                // treated as InLobby — see LobbyRoomState.ResolveMemberStage.
                var stage = lobby.ResolveMemberStage(userId);

                // Cheap to re-init: just text + button visibility + click listeners.
                card.Initialize(
                    userId,
                    lobby.GetDisplayName(userId),
                    instrumentSprite,
                    isLocalHost,
                    isSelf:       isSelf,
                    stage:        stage,
                    onKick:       () => OnKickPlayerClicked(userId),
                    onMakeHost:   () => OnMakeHostClicked(userId),
                    actionsPopup: _playerActionsPopup);
                int displayIndex = userId == lobby.HostUserId ? 0 : 1 + nonHostRank++;
                card.transform.SetSiblingIndex(staticChildCount + displayIndex);
            }

            RemoveStale(_playerCards, seen);
        }

        private void RefreshSongs(LobbyRoomState lobby)
        {
            bool   isLocalHost  = lobby.IsLocalHost;
            string localUserId  = LobbyHubSession.Current?.LocalUserId;
            int    count        = lobby.SongQueue.Count;

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

                // A song's remove button shows if the local user is host
                // (host can clear anyone's pick) or if the local user is the
                // requester (you can take back your own pick). RequesterId
                // is server-authoritative — the hub re-checks this on the
                // RemoveSongFromQueue RPC and rejects if neither condition
                // holds, so toggling the button doesn't widen permissions.
                bool canRemove = isLocalHost
                    || (!string.IsNullOrEmpty(localUserId) && dto.RequesterId == localUserId);

                if (_songCards.TryGetValue(dto.Sequence, out var card))
                {
                    // Existing card — host status may have changed but the
                    // hash/sequence pairing is immutable, so skip the album load.
                    card.SetRemoveButtonVisible(canRemove);
                }
                else
                {
                    card = Instantiate(_songPrefab, _songsContent);
                    _songCards[dto.Sequence] = card;
                    long sequence = dto.Sequence;
                    card.Initialize(
                        HashWrapper.FromString(dto.SongHash),
                        canRemove,
                        () => OnRemoveQueuedSongClicked(sequence));
                }
                // Queue order preserved (oldest first), offset past any static children.
                card.transform.SetSiblingIndex(staticChildCount + i);
            }

            RemoveStale(_songCards, seen);
        }

        private void ConfigureChatScroll()
        {
        }

        // ---------- Chat scroll (standard ScrollRect) ----------

        private void RefreshChat(LobbyRoomState lobby)
        {
            // verticalNormalizedPosition: 0 = bottom, 1 = top.
            bool wasAtBottom = _chatScrollRect == null
                || _lastChatSequenceRendered < 0
                || _chatScrollRect.verticalNormalizedPosition <= 0.01f;

            bool anyAppended = false;
            foreach (var msg in lobby.ChatHistory)
            {
                if (msg.Sequence <= _lastChatSequenceRendered) continue;

                var card = Instantiate(_chatMessagePrefab, _chatContent);
                card.Initialize(msg);
                _chatCards.Add(card);
                _lastChatSequenceRendered = msg.Sequence;
                anyAppended = true;
            }

            if (anyAppended && wasAtBottom)
            {
                ScrollChatToBottomDeferred().Forget();
            }
        }

        private async UniTaskVoid ScrollChatToBottomDeferred()
        {
            // Wait one frame so ContentSizeFitter / TMP finalize row heights.
            await UniTask.NextFrame();
            if (!this || _chatScrollRect == null) return;

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
            // The server's `reason` is a stable machine code (e.g. "kicked_by_host")
            // intended for localization, not a user-facing string. Map known codes to
            // their localized body; fall back to the raw reason on unknown codes so a
            // future server-added reason at least shows *something* meaningful instead
            // of an empty dialog.
            var body = reason switch
            {
                "kicked_by_host" => Localize.Key("Menu.Online.KickDialog.Reason.KickedByHost"),
                _                => Localize.KeyFormat("Menu.Online.KickDialog.Reason.Unknown", reason ?? string.Empty),
            };
            DialogManager.Instance.ShowMessage(
                Localize.Key("Menu.Online.KickDialog.Title"),
                body);
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
        }

        public void OnLeaveClicked() => LeaveAsync().Forget();

        /// <summary>
        /// Copies the current lobby's 6-character code onto the system clipboard so the
        /// user can paste it to a friend. Surfaces a confirmation toast on success
        /// and an error toast if the lobby state isn't available yet (early click
        /// before <see cref="Refresh"/> has bound _boundSession.CurrentLobby).
        /// </summary>
        private void OnCopyCodeClicked()
        {
            var lobbyId = _boundSession?.CurrentLobby?.LobbyId;
            if (string.IsNullOrEmpty(lobbyId))
            {
                ToastManager.ToastError(Localize.Key("Menu.Online.Toast.CopyCode.NoLobby"));
                return;
            }

            GUIUtility.systemCopyBuffer = lobbyId;
            // Deliberately do NOT include the lobby code in the toast. The whole point of
            // Copy (vs. Hold-to-Show) is that the code stays off-screen — surfacing it in
            // a toast right after copying would defeat that for stream/screen-share users.
            ToastManager.ToastSuccess(Localize.Key("Menu.Online.Toast.CopyCode.Success"));
        }

        /// <summary>
        /// Press-and-hold Blue surfaces a modal dialog displaying the lobby code at
        /// large size — handy for reading aloud, screen-sharing, or showing on
        /// stream without committing the code to the clipboard. Tap copies (see
        /// <see cref="OnCopyCodeClicked"/>); hold reveals.
        /// </summary>
        private void OnShowCodeHold()
        {
            var lobbyId = _boundSession?.CurrentLobby?.LobbyId;
            if (string.IsNullOrEmpty(lobbyId))
            {
                ToastManager.ToastError(Localize.Key("Menu.Online.Toast.CopyCode.NoLobby"));
                return;
            }

            DialogManager.Instance.ShowMessage(
                Localize.Key("Menu.Online.ShowCode.Title"),
                Localize.KeyFormat("Menu.Online.ShowCode.Body", lobbyId));
        }

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
            // filter is cleared in MusicLibraryMenu's exit funnel, so we own
            // the seed here.
            var lobby = (_boundSession ?? LobbyHubSession.Current)?.CurrentLobby;

            // Client-side cap. Server-side limit may differ (or be absent); this is
            // a friendly gate so the user can't even open the picker when full.
            int currentCount = lobby?.SongQueue.Count ?? 0;
            if (currentCount >= MaxQueueSize)
            {
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.QueueFull.Title"),
                    Localize.KeyFormat("Menu.Online.QueueFull.Body", MaxQueueSize));
                return;
            }

            MusicLibraryMenu.AllowedSongHashes = BuildPlayableSongSet(lobby);

            MusicLibraryMenu.SongPickedCallback = OnSongPicked;
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.MusicLibrary);
        }

        /// <summary>
        /// Intersect the lobby's shared song library (every member's local libraries
        /// AND'd together server-side, mirrored into <see cref="LobbyRoomState.LobbySongLibrary"/>)
        /// with the set of songs that have a track for every member's currently-selected
        /// instrument. The result is what the picker can actually queue without leaving
        /// some member with nothing to play.
        ///
        /// Unknown member instruments (a peer who just joined and hasn't sent theirs yet)
        /// are skipped from the playability check — better to show too many songs for a
        /// brief startup window than to flicker the picker as members report in.
        ///
        /// Snapshot semantics: the returned set is a NEW HashSet, not a live alias of
        /// <c>LobbySongLibrary</c>. It's only valid for this picker open; library
        /// add/remove broadcasts that arrive while the picker is open won't auto-fold
        /// in (MusicLibraryMenu's allowed-hash filter is one-shot at activation). A
        /// rare staleness window vs the silent broken queue we'd otherwise allow.
        /// </summary>
        /// <summary>
        /// Builds the strict picker filter for a given lobby state. Internal so
        /// <see cref="LobbyHubSession.OnLobbySongLibraryUpdatedAsync"/> can re-derive
        /// it when the lobby's shared library changes mid-picker (e.g. after a
        /// mid-lobby song scan triggers <see cref="ILobbyHub.UpdateLibrary"/>).
        /// </summary>
        internal static HashSet<HashWrapper> BuildPlayableSongSet(LobbyRoomState lobby)
        {
            if (lobby == null)
            {
                return null;
            }

            // Collect each known member's instrument; skip members we haven't received
            // an instrument for yet (the safer side of the race).
            var requiredInstruments = new List<Instrument>(lobby.Members.Count);
            foreach (var uid in lobby.Members)
            {
                if (lobby.MemberInstruments.TryGetValue(uid, out var inst))
                {
                    requiredInstruments.Add(inst);
                }
            }

            if (requiredInstruments.Count == 0)
            {
                // No member instruments known — fall back to the raw lobby library so
                // we don't filter everything away. Reference-aliases the lobby's
                // HashSet to preserve the live-update behavior the OLD path had.
                return lobby.LobbySongLibrary;
            }

            // Pre-resolve each member's GameMode so the per-song hot loop can call
            // PossibleInstrumentsForSong without re-deriving it for every song.
            // Fully qualified because YARG.Online.Lobbies.Contracts.Enums.GameMode
            // is also in scope via the lobby contracts using-block.
            var memberGameModes = new YARG.Core.GameMode[requiredInstruments.Count];
            for (int i = 0; i < requiredInstruments.Count; i++)
            {
                memberGameModes[i] = requiredInstruments[i].ToNativeGameMode();
            }

            var playable = new HashSet<HashWrapper>();
            foreach (var hash in lobby.LobbySongLibrary)
            {
                if (!SongContainer.SongsByHash.TryGetValue(hash, out var entries) || entries.Count == 0)
                {
                    continue;
                }

                // SongsByHash groups entries by hash (re-encodes / multiple library locations
                // can produce duplicates). Any one entry covers the chart's parts, so the
                // first is sufficient.
                var entry = entries[0];
                bool playableForAll = true;
                for (int i = 0; i < memberGameModes.Length; i++)
                {
                    // Mirror the MusicLibrary's "is this song playable for this profile" check
                    // (ShowCategories.IsSongPlayable). A member's chosen Instrument maps to a
                    // GameMode, and that GameMode exposes the set of Instruments any profile
                    // in that mode could actually play on this song — for vocals that's
                    // [Vocals, Harmony]; for drums it picks the right four/five/pro split
                    // based on what the chart contains. We accept the song iff at least one
                    // of those instruments is present (and a chart-aware HasInstrument check
                    // catches custom songs whose vocal track is empty / missing entirely).
                    var candidates = memberGameModes[i].PossibleInstrumentsForSong(entry);
                    bool anyAvailable = false;
                    foreach (var candidate in candidates)
                    {
                        if (entry.HasInstrument(candidate))
                        {
                            anyAvailable = true;
                            break;
                        }
                    }
                    if (!anyAvailable)
                    {
                        playableForAll = false;
                        break;
                    }
                }
                if (playableForAll)
                {
                    playable.Add(hash);
                }
            }
            return playable;
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

        public void OnKickPlayerClicked(string userId) => KickPlayerAsync(userId).Forget();
        public void OnMakeHostClicked(string userId)   => TransferHostAsync(userId).Forget();

        private async UniTaskVoid KickPlayerAsync(string userId)
        {
            var session = _boundSession ?? LobbyHubSession.Current;
            if (session == null) return;
            try
            {
                await session.KickPlayerAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.HostActionError.KickTitle"),
                    TranslateHostActionError(ex.Message, isKick: true));
            }
        }

        private async UniTaskVoid TransferHostAsync(string userId)
        {
            var session = _boundSession ?? LobbyHubSession.Current;
            if (session == null) return;
            try
            {
                await session.TransferHostAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.HostActionError.TransferHostTitle"),
                    TranslateHostActionError(ex.Message, isKick: false));
            }
        }

        // Translate the shared set of hub error tags (KickPlayer + TransferHost throw
        // the same outcomes) into a localized sentence. The raw HubException.Message
        // is a stable contract identifier like "not_host" that's useless to surface
        // verbatim. Unknown tags fall through to the raw message so genuinely-novel
        // server errors still get reported instead of being swallowed.
        private static string TranslateHostActionError(string msg, bool isKick)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            string suffix = isKick ? "Kick" : "MakeHost";
            if (msg.Contains("not_host"))
                return Localize.Key($"Menu.Online.HostActionError.NotHost.{suffix}");
            if (msg.Contains("target_is_host"))
                return Localize.Key($"Menu.Online.HostActionError.TargetIsHost.{suffix}");
            if (msg.Contains("target_not_member"))
                return Localize.Key("Menu.Online.HostActionError.TargetNotMember");
            if (msg.Contains("not_in_lobby"))
                return Localize.Key("Menu.Online.HostActionError.NotInLobby");
            if (msg.Contains("validation_failed"))
                return Localize.Key($"Menu.Online.HostActionError.ValidationFailed.{suffix}");
            return msg;
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

                // Client-side preflight on the same gates the server enforces.
                // Catching them here gives the host immediate feedback without
                // burning an RPC. The server still re-validates everything.
                var lobby = session.CurrentLobby;
                if (lobby != null && lobby.Status == LobbyStatus.Starting)
                {
                    // The host hit Start, the server already transitioned us
                    // to Starting, but the LoadingContext from the original
                    // press hasn't ended yet. Silently ignore the second
                    // press rather than show "already_starting" mid-flight.
                    return;
                }
                if (lobby != null && lobby.Status == LobbyStatus.GameStarted)
                {
                    DialogManager.Instance.ShowMessage("Could not start game",
                        "The game has already started.");
                    return;
                }
                if (lobby != null && !lobby.AllMembersBackInLobby)
                {
                    // Bucket the not-back members into the two real-world states
                    // (still playing the song vs. sitting on the results screen)
                    // using LobbyRoomState.IsSongInProgress, which the session
                    // flips on the LobbyStatus + Played-removal events. Names
                    // appear in only one bucket so the host can tell at a glance
                    // who they're waiting on for which reason.
                    var inGame    = new List<string>();
                    var onResults = new List<string>();
                    foreach (var userId in lobby.Members)
                    {
                        switch (lobby.ResolveMemberStage(userId))
                        {
                            case LobbyMemberStage.InGame:    inGame.Add(lobby.GetDisplayName(userId));    break;
                            case LobbyMemberStage.OnResults: onResults.Add(lobby.GetDisplayName(userId)); break;
                        }
                    }
                    string body;
                    if (inGame.Count > 0 && onResults.Count > 0)
                    {
                        body = Localize.KeyFormat("Menu.Online.WaitingForPlayers.Both",
                            string.Join(", ", inGame),
                            string.Join(", ", onResults));
                    }
                    else if (inGame.Count > 0)
                    {
                        body = Localize.KeyFormat("Menu.Online.WaitingForPlayers.InGame",
                            string.Join(", ", inGame));
                    }
                    else if (onResults.Count > 0)
                    {
                        body = Localize.KeyFormat("Menu.Online.WaitingForPlayers.OnResults",
                            string.Join(", ", onResults));
                    }
                    else
                    {
                        // AllMembersBackInLobby was false but our bucketing found
                        // nothing — only happens if the dictionary was mutated
                        // between the check and the loop. Fall back to a generic.
                        body = Localize.Key("Menu.Online.WaitingForPlayers.Generic");
                    }
                    DialogManager.Instance.ShowMessage(
                        Localize.Key("Menu.Online.WaitingForPlayers.Title"), body);
                    return;
                }

                // The server's new StartGame flow blocks the RPC across two
                // phases: BeginStartGame (state -> Starting + broadcast) and,
                // after the game server allocator returns, ConfirmStartGame
                // (mint per-member tokens + state -> GameStarted + broadcast
                // OnGameStarted). Allocation can take seconds, so wrap the
                // await in a LoadingContext to show the host a "Preparing
                // game…" overlay instead of a frozen menu.
                using (var loading = new LoadingContext())
                {
                    loading.SetLoadingText(Localize.Key("Menu.Online.PreparingGame"));
                    await session.StartGameAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                // Translate server-side hub-error tags (stable contract identifiers
                // across the BeginStart / Confirm / Abort flow) into a localized
                // sentence. Unknown tags fall through to the raw message so any
                // genuinely-novel error still surfaces.
                string body = ex.Message;
                if (body != null)
                {
                    if      (body.Contains("players_still_in_results")) body = Localize.Key("Menu.Online.StartGameError.PlayersStillInResults");
                    else if (body.Contains("already_starting"))         body = Localize.Key("Menu.Online.StartGameError.AlreadyStarting");
                    else if (body.Contains("already_started"))          body = Localize.Key("Menu.Online.StartGameError.AlreadyStarted");
                    else if (body.Contains("allocation_failed"))        body = Localize.Key("Menu.Online.StartGameError.AllocationFailed");
                    else if (body.Contains("start_aborted"))            body = Localize.Key("Menu.Online.StartGameError.StartAborted");
                    else if (body.Contains("not_enough_players"))       body = Localize.Key("Menu.Online.StartGameError.NotEnoughPlayers");
                    else if (body.Contains("queue_empty"))              body = Localize.Key("Menu.Online.StartGameError.QueueEmpty");
                }
                DialogManager.Instance.ShowMessage(
                    Localize.Key("Menu.Online.StartGameError.Title"), body);
            }
        }
    }
}
