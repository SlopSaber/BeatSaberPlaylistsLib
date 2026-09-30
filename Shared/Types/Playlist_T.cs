using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BeatSaberPlaylistsLib.Types
{
    /// <summary>
    /// Base class for a Playlist.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract partial class Playlist<T> : Playlist, IPlaylist<T>
        where T : class, IPlaylistSong
    {
        /// <summary>
        /// Internal collection of songs in the playlist.
        /// </summary>
        protected List<T> Songs { get; set; } = new List<T>();


        /// <inheritdoc/>
        public override IPlaylistSong this[int index]
        {
            get => Songs[index];
            set
            {
                Songs[index] = CreateWith(value);
            }
        }


        /// <inheritdoc/>
        public override int Count => Songs.Count;

        /// <inheritdoc/>
        public override bool IsReadOnly => ReadOnly;

        /// <inheritdoc/>
        protected override IPlaylistSong CreateFrom(ISong song) => this.CreateFrom(song);
        /// <inheritdoc/>
        protected override IPlaylistSong CreateFromByHash(string songHash, string? songName, string? songKey, string? mapper)
            => CreateWithHash(songHash, songName, songKey, mapper);
        /// <inheritdoc/>
        protected override IPlaylistSong CreateFromByLevelId(string levelId, string? songName, string? songKey, string? mapper)
            => CreateWithLevelId(levelId, songName, songKey, mapper);

        /// <summary>
        /// Creates a new <see cref="IPlaylistSong"/> of type <typeparamref name="T"/> from the given <paramref name="song"/>.
        /// </summary>
        /// <param name="song"></param>
        /// <returns></returns>
        protected abstract T CreateWith(ISong song);

        /// <summary>
        /// Creates a new <see cref="IPlaylistSong"/> of type <typeparamref name="T"/> from the given values.
        /// </summary>
        /// <param name="songHash"></param>
        /// <param name="songName"></param>
        /// <param name="songKey"></param>
        /// <param name="mapper"></param>
        /// <returns></returns>
        protected abstract T CreateWithHash(string songHash, string? songName, string? songKey, string? mapper);

        /// <summary>
        /// Creates a new <see cref="IPlaylistSong"/> of type <typeparamref name="T"/> from the given values.
        /// </summary>
        /// <param name="levelId"></param>
        /// <param name="songName"></param>
        /// <param name="songKey"></param>
        /// <param name="mapper"></param>
        /// <returns></returns>
        protected abstract T CreateWithLevelId(string levelId, string? songName, string? songKey, string? mapper);

        /// <inheritdoc/>
        public override IPlaylistSong? Add(ISong song)
        {
            if (AllowDuplicates || !Songs.Any(s => s.Hash == song.Hash || (song.Key != null && s.Key == song.Key)))
            {
                T playlistSong = CreateWith(song);
                Songs.Add(playlistSong);

                if (Count <= 4)
                {
                    RaiseCoverImageChangedForDefaultCover();
                }

                return playlistSong;
            }
            return null;
        }

        /// <inheritdoc/>
        public override IPlaylistSong? Add(string songHash, string? songName, string? songKey, string? mapper) =>
            AddSong(CreateWithHash(songHash, songName, songKey, mapper));
        /// <inheritdoc/>
        public override void Add(IPlaylistSong item)
        {
            AddSong(item);
        }

        /// <summary>
        /// Adds a song to the playlist.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public virtual IPlaylistSong? AddSong(IPlaylistSong item) => Add((ISong)item);

        /// <inheritdoc/>
        public override void Clear()
        {
            Songs.Clear();
            RaiseCoverImageChangedForDefaultCover();
        }

        /// <inheritdoc/>
        public override void Sort()
        {
            Songs = Songs.OrderByDescending(s => s.DateAdded).ToList();
            RaiseCoverImageChangedForDefaultCover();
        }

        /// <inheritdoc/>
        public override bool Contains(IPlaylistSong item)
        {
            return Songs.Any(s => s.Equals(item));
        }

        /// <inheritdoc/>
        public override void CopyTo(IPlaylistSong[] array, int arrayIndex)
        {
            int index = arrayIndex;
            foreach (T song in Songs)
            {
                array[index] = song;
                index++;
            }
        }

        /// <inheritdoc/>
        public override IEnumerator<IPlaylistSong> GetEnumerator()
        {
            return Songs.GetEnumerator();
        }

        /// <inheritdoc/>
        public override int IndexOf(IPlaylistSong item)
        {
            if (item is T song)
                return Songs.IndexOf(song);
            else
                return -1;
        }

        /// <inheritdoc/>
        public override void Insert(int index, IPlaylistSong item)
        {
            Songs.Insert(index, CreateWith(item));
            if (index < 4)
            {
                RaiseCoverImageChangedForDefaultCover();
            }
        }

        /// <inheritdoc/>
        public override bool Remove(IPlaylistSong item)
        {
            int index = -1;
            if (item is T matchedType)
            {
                index = Songs.IndexOf(matchedType);
            }
            else
            {
                index = Songs.FindIndex(s => s.Equals(item));
            }

            if (index != -1)
            {
                Songs.RemoveAt(index);
                if (index < 4)
                {
                    RaiseCoverImageChangedForDefaultCover();
                }
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public override void RemoveAt(int index)
        {
            Songs.RemoveAt(index);
            if (index < 4)
            {
                RaiseCoverImageChangedForDefaultCover();
            }
        }

        /// <inheritdoc/>
        public override int RemoveAll(Func<IPlaylistSong, bool> match)
        {
            int songsRemoved = Songs.RemoveAll(s => match(s));
            if (songsRemoved > 0)
                RaiseCoverImageChangedForDefaultCover();
            return songsRemoved;
        }

        /// <inheritdoc/>
        public virtual int RemoveAll(Func<T, bool> match)
        {
            int removedSongs = 0;
            if (match != null)
                removedSongs = Songs.RemoveAll(s => match(s));
            if (removedSongs > 0)
                RaiseCoverImageChangedForDefaultCover();
            return removedSongs;
        }

        /// <inheritdoc/>
        public override void RemoveDuplicates()
        {
            int previousCount = Songs.Count;
            Songs = Songs.Distinct(IPlaylistSongComparer<T>.Default).ToList();
            if (Songs.Count != previousCount)
            {
                RaisePlaylistChanged();
                RaiseCoverImageChangedForDefaultCover();
            }
        }

        private readonly struct DuplicateKey
        {
            internal readonly bool IsNull;
            internal readonly string? LevelId;
            internal readonly string? Key;

            internal DuplicateKey(T song)
            {
                IsNull = song == null;
                LevelId = song?.LevelId;
                Key = song?.Key;
            }
        }

        private sealed class DuplicateKeyComparer : IEqualityComparer<DuplicateKey>
        {
            internal static readonly DuplicateKeyComparer Instance = new DuplicateKeyComparer();

            public bool Equals(DuplicateKey x, DuplicateKey y)
            {
                if (x.IsNull) return y.IsNull;
                if (y.IsNull || GetHashCode(x) != GetHashCode(y)) return false;
                // Match IPlaylistSongComparer, including identifier-less entries that never collapse.
                if (x.LevelId != null) return x.LevelId == y.LevelId;
                return x.Key != null && x.Key == y.Key;
            }

            public int GetHashCode(DuplicateKey song) =>
                238947239 ^ (song.LevelId?.GetHashCode() ?? song.Key?.GetHashCode() ?? 0);
        }

        /// <summary>
        /// Captures song references and identifier strings on owner for isolated worker duplicate preparation.
        /// Invoke the returned factory once on a worker. Its optional action publishes retained original songs on owner.
        /// The caller must reject changed song sequences or identifiers before invoking the publication action.
        /// </summary>
        /// <returns>A worker factory returning owner publication, or null if no songs were removed.</returns>
        public Func<Action?> CaptureDuplicateRemoval()
        {
            var original = Songs.ToArray();
            var keys = new DuplicateKey[original.Length];
            for (int i = 0; i < keys.Length; ++i) keys[i] = new DuplicateKey(original[i]);
            return () =>
            {
                var seen = new HashSet<DuplicateKey>(DuplicateKeyComparer.Instance);
                var songs = new List<T>(original.Length);
                for (int i = 0; i < keys.Length; ++i)
                    if (seen.Add(keys[i])) songs.Add(original[i]);
                if (songs.Count == original.Length) return null;
                return () =>
                {
                    Songs = songs;
                    RaisePlaylistChanged();
                    RaiseCoverImageChangedForDefaultCover();
                };
            };
        }


        /// <inheritdoc/>
        public override bool TryRemoveByHash(string songHash)
        {
            songHash = songHash.ToUpper();
            return RemoveAll((IPlaylistSong s) => s.Hash == songHash) > 0;
        }

        /// <inheritdoc/>
        public override bool TryRemoveByKey(string songKey)
        {
            songKey = songKey.ToLower();
            return RemoveAll((IPlaylistSong s) => s.Key == songKey) > 0;
        }

        /// <inheritdoc/>
        public override bool TryRemove(IPlaylistSong song)
        {
            if (song == null)
                return false;
            return RemoveAll((IPlaylistSong s) => s == song) > 0;
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return Songs.GetEnumerator();
        }

        /// <inheritdoc/>
        IEnumerator<IPlaylistSong> IEnumerable<IPlaylistSong>.GetEnumerator()
        {
            foreach (var song in Songs)
            {
                yield return song;
            }
        }

        /// <inheritdoc/>
        void ICollection<IPlaylistSong>.Add(IPlaylistSong item)
        {
            AddSong(item);
        }
    }
}
