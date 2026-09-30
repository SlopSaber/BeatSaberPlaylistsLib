#if BeatSaber
extern alias BeatSaber;
using BeatSaber::UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;

namespace BeatSaberPlaylistsLib.Types
{
    /// <summary>
    /// Base class for a Playlist.
    /// </summary>
    public abstract partial class Playlist : IPlaylist
    {
        /// <summary>
        /// Maximum width and height of the small cover image
        /// </summary>
        public const int kSmallImageSize = 128;

        /// <summary>
        /// Queue of <see cref="Action"/>s to load playlist sprites.
        /// </summary>
        protected static readonly Queue<Action> SpriteQueue = new Queue<Action>();

        /// <summary>
        /// Instance of the playlist cover sprite.
        /// </summary>
        protected Sprite? _sprite;
        /// <summary>
        /// Instance of the previous cover sprite.
        /// </summary>
        protected Sprite? _previousSprite;

        /// <summary>
        /// Instance of the downscaled playlist cover sprite.
        /// </summary>
        protected Sprite? _smallSprite;
        /// <summary>
        /// Instance of the previous downscaled cover sprite.
        /// </summary>
        protected Sprite? _previousSmallSprite;

        /// <summary>
        /// Returns true if the sprite for the playlist is already queued.
        /// </summary>
        protected bool SpriteLoadQueued;

        /// <summary>
        /// Returns true if the small sprite for the playlist is already queued.
        /// </summary>
        protected bool SmallSpriteLoadQueued;

        private static readonly object _loaderLock = new object();
        private static bool CoroutineRunning = false;
        private static readonly SemaphoreSlim coverPreparationSlots = new SemaphoreSlim(2, 2);
        private int coverRevision;

        /// <summary>
        /// Adds a playlist to the sprite load queue.
        /// </summary>
        /// <param name="playlist"></param>
        /// <param name="downscaleImage"></param>
        protected async static void QueueLoadSprite(Playlist playlist, bool downscaleImage)
        {
            await UnityGame.SwitchToMainThreadAsync();
            int revision = playlist.coverRevision;
            await coverPreparationSlots.WaitAsync();
            try
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (revision != playlist.coverRevision) return;
                var stream = playlist.HasCover ? playlist.GetCoverStream() : await playlist.GetDefaultCoverStream();
                byte[]? bytes = stream == null ? null : await Task.Run(() =>
                {
                    using (stream)
                    {
                        Stream processed = downscaleImage && stream != Stream.Null
                            ? Utilities.DownscaleImage(stream, kSmallImageSize) : stream;
                        try { return processed.ToArray(); }
                        finally { if (!ReferenceEquals(processed, stream)) processed.Dispose(); }
                    }
                });
                await UnityGame.SwitchToMainThreadAsync();
                if (revision != playlist.coverRevision) return;
                var starter = SharedCoroutineStarter.instance;
                if (starter == null) return;
                var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SpriteQueue.Enqueue(() =>
                {
                    try
                    {
                        if (revision != playlist.coverRevision) return;
                        var sprite = bytes == null ? Utilities.DefaultSprite : Utilities.GetSpriteFromBytes(bytes);
                        if (downscaleImage && bytes != null)
                        {
                            playlist._smallSprite = sprite;
                            OnSmallSpriteLoaded(playlist);
                        }
                        else
                        {
                            playlist._sprite = sprite;
                            playlist._smallSprite = sprite;
                            OnSpriteLoaded(playlist);
                        }
                    }
                    finally
                    {
                        if (revision == playlist.coverRevision)
                        {
                            if (downscaleImage) playlist.SmallSpriteLoadQueued = false;
                            else playlist.SpriteLoadQueued = false;
                        }
                        published.TrySetResult(true);
                    }
                });
                if (!CoroutineRunning) starter.StartCoroutine(SpriteLoadCoroutine());
                await published.Task;
            }
            catch (Exception ex)
            {
                await UnityGame.SwitchToMainThreadAsync();
                Utilities.Logger?.Invoke("Preparing playlist cover failed.", ex);
            }
            finally
            {
                coverPreparationSlots.Release();
                await UnityGame.SwitchToMainThreadAsync();
                if (revision == playlist.coverRevision)
                {
                    if (downscaleImage) playlist.SmallSpriteLoadQueued = false;
                    else playlist.SpriteLoadQueued = false;
                }
            }
        }

        private static void OnSpriteLoaded(Playlist playlist)
        {
            int revision = playlist.coverRevision;
            playlist.SmallSpriteWasLoaded = true;
            playlist.SpriteWasLoaded = true;
            try { playlist.SpriteLoaded?.Invoke(playlist, null); }
            finally
            {
                if (revision == playlist.coverRevision)
                {
                    playlist._previousSprite = null;
                    playlist._previousSmallSprite = null;
                    playlist.SpriteLoadQueued = false;
                    playlist.SmallSpriteLoadQueued = false;
                }
            }
        }

        private static void OnSmallSpriteLoaded(Playlist playlist)
        {
            int revision = playlist.coverRevision;
            playlist.SmallSpriteWasLoaded = true;
            try { playlist.SpriteLoaded?.Invoke(playlist, null); }
            finally
            {
                if (revision == playlist.coverRevision)
                {
                    playlist._previousSmallSprite = null;
                    playlist.SmallSpriteLoadQueued = false;
                }
            }
        }

        /// <summary>
        /// Wait <see cref="YieldInstruction"/> between sprite loads.
        /// </summary>
        public static YieldInstruction LoadWait = new WaitForEndOfFrame();

        /// <summary>
        /// Coroutine to load sprites in the queue.
        /// </summary>
        /// <returns></returns>
        protected static IEnumerator<YieldInstruction> SpriteLoadCoroutine()
        {
            lock (_loaderLock)
            {
                if (CoroutineRunning)
                    yield break;
                CoroutineRunning = true;
            }
            while (SpriteQueue.Count > 0)
            {
                yield return LoadWait;
                var loader = SpriteQueue.Dequeue();
                try { loader?.Invoke(); }
                catch (Exception ex) { Utilities.Logger?.Invoke("Publishing playlist cover failed.", ex); }
            }
            CoroutineRunning = false;
            if (SpriteQueue.Count > 0) // Just in case
                SharedCoroutineStarter.instance?.StartCoroutine(SpriteLoadCoroutine());
        }

        /// <inheritdoc/>
        public PlaylistLevelPack PlaylistLevelPack => new PlaylistLevelPack(this);

        #region IStagedSpriteLoad

        /// <inheritdoc/>
        public event EventHandler? SpriteLoaded;

        /// <inheritdoc/>
        public bool SpriteWasLoaded { get; protected set; }

        /// <inheritdoc/>
        public Sprite? Sprite
        {
            get
            {
                if (_sprite != null)
                    return _sprite;
                _sprite = _previousSprite ? _previousSprite : Utilities.DefaultSprite;
                if (!SpriteLoadQueued)
                {
                    SpriteLoadQueued = true;
                    QueueLoadSprite(this, false);
                }
                return _sprite;
            }
        }

        /// <inheritdoc/>
        public bool SmallSpriteWasLoaded { get; protected set; }

        /// <inheritdoc/>
        public Sprite? SmallSprite
        {
            get
            {
                if (_smallSprite != null)
                    return _smallSprite;
                _smallSprite = _previousSmallSprite ? _previousSmallSprite : Utilities.DefaultSprite;
                if (!SmallSpriteLoadQueued)
                {
                    SmallSpriteLoadQueued = true;
                    QueueLoadSprite(this, true);
                }
                return _smallSprite;
            }
        }

        /// <summary>
        /// Resets the sprite for...reasons.
        /// </summary>
        partial void ResetSprite()
        {
            coverRevision++;
            SpriteLoadQueued = false;
            SmallSpriteLoadQueued = false;
            SpriteWasLoaded = false;
            SmallSpriteWasLoaded = false;
            _previousSprite = _sprite;
            _previousSmallSprite = _smallSprite;
            _sprite = null;
            _smallSprite = null;
        }

        #endregion

        /// <summary>
        /// BeatmapLevelPack ID.
        /// </summary>
        public string ID => BeatSaber.CustomLevelLoader.kCustomLevelPackPrefixId + playlistID;

        /// <summary>
        /// Returns a new array of PlaylistSongs (cast as <see cref="BeatSaber::BeatmapLevel"/>) in this playlist.
        /// </summary>
        public BeatSaber::BeatmapLevel[] BeatmapLevels
        {
            get
            {
                foreach (var song in this)
                {
                    song.RefreshFromSongCore();
                }
                return this.Where(s => s.BeatmapLevel != null).Select(s => s.BeatmapLevel!).ToArray();
            }
        }

        /// <inheritdoc/>
        public IPlaylistSong? Add(BeatSaber::BeatmapLevel beatmapLevel, BeatSaber::BeatmapKey? beatmapKey = null)
        {
            if (beatmapLevel == null)
                return null;

            IPlaylistSong? song = Add((ISong)CreateFromByLevelId(beatmapLevel.levelID, beatmapLevel.songName, null, string.Join(", ", beatmapLevel.allMappers.Concat(beatmapLevel.allLighters).Distinct())));

            if (song != null && beatmapKey != null)
            {
                Difficulty difficulty = new Difficulty() {
                    BeatmapDifficulty = beatmapKey.Value.difficulty,
                    Characteristic = beatmapKey.Value.characteristic.SerializedName(),
                };
                song.Difficulties = new List<Difficulty> { difficulty };
            }

            return song;
        }

        #region Default Cover

        private static SemaphoreSlim _defaultCoverSemaphore = new SemaphoreSlim(1, 1);

        /// <inheritdoc cref="IPlaylist.GetDefaultCoverStream" />
        public async Task<Stream?> GetDefaultCoverStream()
        {
            await UnityGame.SwitchToMainThreadAsync();
            if (_defaultCoverData != null) return new MemoryStream(_defaultCoverData);
            if (!Utilities.ImageSharpLoaded()) return null;
            await _defaultCoverSemaphore.WaitAsync();
            int revision = 0;
            try
            {
                await UnityGame.SwitchToMainThreadAsync();
                revision = coverRevision;
                if (_defaultCoverData != null) return new MemoryStream(_defaultCoverData);
                var paths = new List<string?>(4);
                foreach (var song in this)
                {
                    song.RefreshFromSongCore();
                    var level = song.BeatmapLevel;
                    if (level == null) continue;
                    paths.Add(Utilities.GetBeatmapCoverPath(level));
                    if (paths.Count == 4) break;
                }
                if (paths.Count == 0) return null;
                var capturedPaths = paths.ToArray();
                var bytes = await Task.Run(async () =>
                {
                    var streams = new Stream[capturedPaths.Length];
                    try
                    {
                        for (int i = 0; i < streams.Length; i++)
                            streams[i] = Utilities.OpenBeatmapCover(capturedPaths[i]) ?? Stream.Null;
                        if (streams.Length == 1) return streams[0].ToArray();
                        using var collage = streams.Length == 2
                            ? await ImageUtilities.GenerateCollage(streams[0], streams[1]).ConfigureAwait(false)
                            : streams.Length == 3
                                ? await ImageUtilities.GenerateCollage(streams[0], streams[1], streams[2]).ConfigureAwait(false)
                                : await ImageUtilities.GenerateCollage(streams[0], streams[1], streams[2], streams[3]).ConfigureAwait(false);
                        return collage.ToArray();
                    }
                    finally
                    {
                        foreach (var stream in streams) stream?.Dispose();
                    }
                });
                await UnityGame.SwitchToMainThreadAsync();
                if (revision == coverRevision) _defaultCoverData = bytes;
                return new MemoryStream(bytes);
            }
            catch (Exception)
            {
                await UnityGame.SwitchToMainThreadAsync();
                var empty = Array.Empty<byte>();
                if (revision == coverRevision) _defaultCoverData = empty;
                return new MemoryStream(empty);
            }
            finally
            {
                _defaultCoverSemaphore.Release();
            }
        }
        #endregion
    }
}
#endif
