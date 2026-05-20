using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;

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

        // Cached session reference for safe unsubscribe even if Current changes.
        private LobbyHubSession _boundSession;

        private void OnEnable()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", OnLeaveClicked),
            }, true));

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
                return;
            }

            YargLogger.LogInfo(
                $"LobbyView: refresh — id={lobby.LobbyId}, name='{lobby.LobbyName}', "
                + $"host={lobby.HostName}, status={lobby.Status}, "
                + $"members={lobby.Members.Count}/{lobby.MaxPlayers}, "
                + $"queue={lobby.SongQueue.Count}, chat={lobby.ChatHistory.Count}, "
                + $"lobbyLibrary={lobby.LobbySongLibrary.Count}, isHost={lobby.IsLocalHost}");

            RefreshPlayers(lobby);
            RefreshSongs(lobby);
            RefreshChat(lobby);
        }

        private void RefreshPlayers(LobbyRoomState lobby)
        {
            bool   isLocalHost = lobby.IsLocalHost;
            string localUserId = LobbyHubSession.Current?.LocalUserId;

            var seen = new HashSet<string>();
            for (int i = 0; i < lobby.Members.Count; i++)
            {
                string userId = lobby.Members[i];
                seen.Add(userId);

                if (!_playerCards.TryGetValue(userId, out var card))
                {
                    card = Instantiate(_playerPrefab, _playersContent);
                    _playerCards[userId] = card;
                }

                // Cheap to re-init: just text + button visibility + click listeners.
                card.Initialize(
                    userId,
                    lobby.GetDisplayName(userId),
                    isLocalHost,
                    isSelf:     userId == localUserId,
                    onKick:     () => OnKickPlayerClicked(userId),
                    onMakeHost: () => OnMakeHostClicked(userId));
                card.transform.SetSiblingIndex(i);
            }

            RemoveStale(_playerCards, seen);
        }

        private void RefreshSongs(LobbyRoomState lobby)
        {
            bool isLocalHost = lobby.IsLocalHost;

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
                card.transform.SetSiblingIndex(i);
            }

            RemoveStale(_songCards, seen);
        }

        private void RefreshChat(LobbyRoomState lobby)
        {
            // Append-only: chat is monotonic. Walk new messages by Sequence and
            // append a card for each one beyond what we've already rendered.
            bool anyAppended = false;
            for (int i = 0; i < lobby.ChatHistory.Count; i++)
            {
                var msg = lobby.ChatHistory[i];
                if (msg.Sequence <= _lastChatSequenceRendered) continue;
                var card = Instantiate(_chatMessagePrefab, _chatContent);
                card.Initialize(msg);
                _chatCards.Add(card);
                _lastChatSequenceRendered = msg.Sequence;
                anyAppended = true;
            }
            if (anyAppended) ScrollChatToBottom();
        }

        private void ScrollChatToBottom()
        {
            if (_chatScrollRect == null) return;
            // Rows use ContentSizeFitter to grow with wrapped text — force the
            // content RectTransform to re-measure before reading its height,
            // otherwise verticalNormalizedPosition snaps based on stale layout.
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
                if (seen.Contains(key)) continue;
                toRemove ??= new List<TKey>();
                toRemove.Add(key);
            }
            if (toRemove == null) return;

            foreach (var key in toRemove)
            {
                Destroy(cards[key].gameObject);
                cards.Remove(key);
            }
        }

        private void ClearAllCards()
        {
            foreach (var card in _playerCards.Values) Destroy(card.gameObject);
            _playerCards.Clear();

            foreach (var card in _songCards.Values) Destroy(card.gameObject);
            _songCards.Clear();

            foreach (var card in _chatCards) Destroy(card.gameObject);
            _chatCards.Clear();
            _lastChatSequenceRendered = -1;
        }

        private void OnKickedFromLobby(string reason)
        {
            YargLogger.LogInfo($"LobbyView: kicked from lobby — '{reason}'");
            DialogManager.Instance.ShowMessage("Kicked from lobby", reason);
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
        }

        // ---------- UI click handlers (wired in via prefab OnClick later) ----------

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

        public void OnRemoveQueuedSongClicked(long sequence)
        {
            // TODO: ILobbyHub.RemoveQueuedSong(new RemoveQueuedSongArgs(sequence))
            YargLogger.LogWarning($"LobbyView: OnRemoveQueuedSongClicked({sequence}) — not yet wired");
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
                await session.StartGameAsync(CancellationToken.None);
                // No state mutation here — LobbyGameOrchestrator listens for the
                // OnGameStarted callback and drives the loadout flow + UDP connect.
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not start game", ex.Message);
            }
        }
    }
}
