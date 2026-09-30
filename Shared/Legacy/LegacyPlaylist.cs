using BeatSaberPlaylistsLib.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace BeatSaberPlaylistsLib.Legacy
{
    /// <summary>
    /// An <see cref="IPlaylist"/> that can be serialized by a <see cref="LegacyPlaylistHandler"/>.
    /// </summary>
    [JsonObject(MemberSerialization = MemberSerialization.OptIn)]
    public class LegacyPlaylist : JSONPlaylist<LegacyPlaylistSong>
    {

        private Lazy<string>? ImageLoader;
        /// <summary>
        /// Creates an empty <see cref="LegacyPlaylist"/>.
        /// </summary>
        protected LegacyPlaylist()
        { }

        /// <summary>
        /// Captures an isolated playlist on its owning thread for background preparation and serialization.
        /// </summary>
        /// <param name="includeSongs">Whether to retain the current songs.</param>
        /// <exception cref="NotSupportedException">The playlist or a retained song has a custom implementation.</exception>
        public Snapshot CaptureSnapshot(bool includeSongs = true) => new Snapshot(this, includeSongs);

        internal Func<Stream, Action> CapturePopulation()
        {
            var originalData = CustomDataInternal == null ? null : new Dictionary<string, object>(CustomDataInternal);
            var draft = new LegacyPlaylist(Filename, Title, Author)
            {
                Description = Description,
                SuggestedExtension = SuggestedExtension,
                IsSnapshot = true,
                CustomDataInternal = originalData == null ? null : new Dictionary<string, object>(originalData)
            };
            return stream =>
            {
                // Dictionary values retained from the owner stay opaque; incoming JSON creates replacement values.
                new LegacyPlaylistHandler().Populate(stream, draft);
                var changes = Utilities.PrepareCustomDataChanges(originalData, draft.CustomDataInternal);
                bool hasCustomData = draft.CustomDataInternal != null;
                var extensions = draft.ExtensionData;
                var songs = draft.Songs;
                string title = draft.Title;
                string? author = draft.Author;
                string? description = draft.Description;
                bool coverChanged = draft.SnapshotCoverChanged;
                byte[]? cover = draft._coverData;
                return () =>
                {
                    Title = title;
                    Author = author;
                    Description = description;
                    PublishPopulationData(hasCustomData, changes, extensions);
                    Songs = songs;
                    if (coverChanged)
                    {
                        _coverData = cover;
                        RaiseCoverImageChanged();
                    }
                    RaiseCoverImageChangedForDefaultCover();
                };
            };
        }

        /// <summary>
        /// Owns a playlist copy without event subscribers or native cover assets.
        /// </summary>
        public sealed class Snapshot
        {
            private readonly LegacyPlaylist source;
            private readonly Dictionary<Guid, LegacyPlaylistSong> originals = new Dictionary<Guid, LegacyPlaylistSong>();

            /// <summary>The isolated playlist to modify and serialize.</summary>
            public LegacyPlaylist Playlist { get; }

            internal Snapshot(LegacyPlaylist source, bool includeSongs)
            {
                if (source.GetType() != typeof(LegacyPlaylist))
                    throw new NotSupportedException("Snapshots require an unmodified LegacyPlaylist implementation.");
                this.source = source;
                Playlist = new LegacyPlaylist(source.Filename, source.Title, source.Author)
                {
                    Description = source.Description,
                    SuggestedExtension = source.SuggestedExtension,
                    IsSnapshot = true,
                    CustomDataInternal = Utilities.SnapshotCustomData(source.CustomData),
                    _coverData = source._coverData == null ? null : (byte[])source._coverData.Clone()
                };
                if (!includeSongs) return;
                foreach (var song in source.Songs)
                {
                    originals[song.playlistSongID] = song;
                    Playlist.Songs.Add(song.CreateSnapshot());
                }
            }

            /// <summary>
            /// Prepares a bulk song replacement. Invoke the returned action once on the source's owning thread.
            /// Retained songs keep their original identities and custom data; new songs are isolated from the copy.
            /// </summary>
            public Action PrepareSongPublication()
            {
                var songs = new List<LegacyPlaylistSong>(Playlist.Songs.Count);
                foreach (var song in Playlist.Songs)
                    songs.Add(originals.TryGetValue(song.playlistSongID, out var original) ? original : song.CreateSnapshot());
                return () =>
                {
                    source.Songs = songs;
                    source.RaiseCoverImageChangedForDefaultCover();
                    source.RaisePlaylistChanged();
                };
            }
        }

        /// <summary>
        /// Creates a new <see cref="LegacyPlaylist"/> from the given parameters.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="title"></param>
        /// <param name="author"></param>
        public LegacyPlaylist(string fileName, string title, string? author)
        {
            Filename = fileName;
            Title = title;
            Author = author;
        }

        /// <summary>
        /// Creates a new <see cref="LegacyPlaylist"/> from the given parameters.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="title"></param>
        /// <param name="author"></param>
        /// <param name="imageLoader"></param>
        public LegacyPlaylist(string fileName, string title, string? author, Lazy<string> imageLoader)
            : this(fileName, title, author)
        {
            ImageLoader = imageLoader;
        }

        /// <summary>
        /// Creates a new <see cref="LegacyPlaylist"/> from the given parameters.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="title"></param>
        /// <param name="author"></param>
        /// <param name="coverImage"></param>
        public LegacyPlaylist(string fileName, string title, string? author, string? coverImage)
            : this(fileName, title, author)
        {
            SetCover(coverImage);
        }

        ///<inheritdoc/>
        protected override LegacyPlaylistSong CreateWith(ISong song)
        {
            if (song is LegacyPlaylistSong legacySong)
                return legacySong;
            return new LegacyPlaylistSong(song);
        }

        ///<inheritdoc/>
        protected override LegacyPlaylistSong CreateWithHash(string songHash, string? songName, string? songKey, string? mapper)
        {
            return new LegacyPlaylistSong()
            {
                Hash = songHash,
                Name = songName,
                Key = songKey,
                LevelAuthorName = mapper
            };
        }

        ///<inheritdoc/>
        protected override LegacyPlaylistSong CreateWithLevelId(string levelId, string? songName, string? songKey, string? mapper)
        {
            return new LegacyPlaylistSong()
            {
                LevelId = levelId,
                Name = songName,
                Key = songKey,
                LevelAuthorName = mapper
            };
        }

        ///<inheritdoc/>
        [DataMember]
        [JsonProperty("playlistTitle", Order = -10, NullValueHandling = NullValueHandling.Ignore)]
        public override string Title { get; set; } = string.Empty;
        ///<inheritdoc/>
        [DataMember]
        [JsonProperty("playlistAuthor", Order = -5)]
        public override string? Author { get; set; }
        ///<inheritdoc/>
        [DataMember]
        [JsonProperty("playlistDescription", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
        public override string? Description { get; set; }
        ///<inheritdoc/>
        [JsonProperty("customData", Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        private Dictionary<string, object>? _serializedCustomData
        {
            get => CustomDataInternal;
            set => CustomDataInternal = value;
        }
        ///<inheritdoc/>
        [DataMember]
        [JsonProperty("songs", Order = 90)]
        protected List<LegacyPlaylistSong> _serializedSongs
        {
            get => Songs;
            set => Songs = value ?? new List<LegacyPlaylistSong>();
        }
        /// <summary>
        /// A base64 string conversion of the cover image.
        /// </summary>
        [DataMember]
        [JsonProperty("image", Order = 100)]
        protected string? CoverString
        {
            get
            {
                if (CoverData == null)
                    return string.Empty;
                return Utilities.ByteArrayToBase64(CoverData); ;
            }
            set
            {
                if (value == null || value.Length == 0)
                {
                    CoverData = Array.Empty<byte>();
                    return;
                }
                try
                {
                    CoverData = Utilities.Base64ToByteArray(value);
                }
                catch (FormatException)
                {
                    CoverData = Array.Empty<byte>();
                }
            }
        }

        private byte[]? _coverData;

        /// <summary>
        /// Raw data for the cover image.
        /// </summary>
        protected byte[]? CoverData
        {
            get => _coverData;
            set
            {
                _coverData = value;
                RaiseCoverImageChanged();
            }
        }

        ///<inheritdoc/>
        public override bool HasCover => CoverData != null && CoverData.Length > 0;

        ///<inheritdoc/>
        public override Stream GetCoverStream()
        {
            if (HasCover && CoverData != null)
                return new MemoryStream(CoverData);
            else
                return new MemoryStream(Array.Empty<byte>());
        }

        ///<inheritdoc/>
        public override void SetCover(byte[]? coverImage)
        {
            CoverData = coverImage;
        }

        ///<inheritdoc/>
        public override void SetCover(string? coverImageStr)
        {
            CoverString = coverImageStr;
        }

        ///<inheritdoc/>
        public override void SetCover(Stream stream)
        {
            if (stream == null || !stream.CanRead)
                CoverString = "";
            else if (stream is MemoryStream cast)
            {
                CoverData = cast.ToArray();
            }
            else
            {
                CoverData = stream.ToArray();
            }

        }
    }
}
