using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Song;

namespace YARG.Menu.Online
{
    public class QueuedSong : MonoBehaviour
    {
        [SerializeField]
        private Image _albumArt;
        [SerializeField]
        private Sprite _placeholder;
        [SerializeField]
        private TextMeshProUGUI _songName;
        [SerializeField]
        private Button _removeButton;

        private CancellationTokenSource _albumCts;
        private Texture2D _ownedTexture;
        private Action _onRemove;

        public void Initialize(HashWrapper hash, bool isLocalHost, Action onRemove)
        {
            CancelAlbumLoad();
            ClearOwnedTexture();

            _onRemove = onRemove;
            //_removeButton.onClick.RemoveAllListeners();
            //_removeButton.onClick.AddListener(InvokeRemove);
            _removeButton.gameObject.SetActive(isLocalHost);

            if (!SongContainer.SongsByHash.TryGetValue(hash, out var songs))
            {
                _songName.text = hash.ToString();
                _albumArt.sprite = _placeholder;
                return;
            }

            var song = songs[0];
            _songName.text = song.Name;
            _albumArt.sprite = _placeholder;

            _albumCts = new CancellationTokenSource();
            LoadAlbumArtAsync(song, _albumCts.Token).Forget();
        }

        private async UniTaskVoid LoadAlbumArtAsync(SongEntry song, CancellationToken cancellationToken)
        {
            Texture2D texture = null;

            // Matches Sidebar.LoadAlbumCover: don't pass the token into RunOnThreadPool, because we
            // need to resume on this method to dispose the YARGImage (backed by a FixedArray).
            // ReSharper disable once MethodSupportsCancellation
            using var image = await UniTask.RunOnThreadPool(song.LoadAlbumData);
            if (image != null)
            {
                texture = image.LoadTexture(false);
            }

            if (cancellationToken.IsCancellationRequested || this == null)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
                return;
            }

            if (texture == null)
            {
                _albumArt.sprite = _placeholder;
                return;
            }

            _ownedTexture = texture;
            _albumArt.sprite = Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f));
        }

        public void SetRemoveButtonVisible(bool visible)
        {
            _removeButton.gameObject.SetActive(visible);
        }

        public void InvokeRemove()
        {
            _onRemove?.Invoke();
        }

        private void OnDisable()
        {
            CancelAlbumLoad();
            ClearOwnedTexture();
        }

        private void OnDestroy()
        {
            CancelAlbumLoad();
            ClearOwnedTexture();
        }

        private void CancelAlbumLoad()
        {
            if (_albumCts == null)
            {
                return;
            }

            _albumCts.Cancel();
            _albumCts.Dispose();
            _albumCts = null;
        }

        private void ClearOwnedTexture()
        {
            if (_ownedTexture == null)
            {
                return;
            }

            _albumArt.sprite = _placeholder;
            Destroy(_ownedTexture);
            _ownedTexture = null;
        }
    }
}
