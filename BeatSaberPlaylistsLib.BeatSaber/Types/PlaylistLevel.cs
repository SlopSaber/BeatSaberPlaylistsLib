extern alias BeatSaber;

namespace BeatSaberPlaylistsLib.Types
{
    /// <summary>
    /// Represents a song in a <see cref="PlaylistLevelPack"/>.
    /// </summary>
    public class PlaylistLevel : BeatSaber::BeatmapLevel
    {
        /// <summary>
        /// The <see cref="IPlaylistSong"/> associated with this level.
        /// </summary>
        public IPlaylistSong playlistSong { get; }

        internal PlaylistLevel(IPlaylistSong playlistSong, BeatSaber::BeatmapLevel beatmapLevel)
            : base(beatmapLevel.version, beatmapLevel.hasPrecalculatedData, beatmapLevel.levelID, beatmapLevel.songName, beatmapLevel.songSubName, beatmapLevel.songAuthorName, beatmapLevel.allMappers, beatmapLevel.allLighters, beatmapLevel.beatsPerMinute, beatmapLevel.integratedLufs, beatmapLevel.songTimeOffset, beatmapLevel.previewStartTime, beatmapLevel.previewDuration, beatmapLevel.songDuration, beatmapLevel.contentRating, beatmapLevel.previewMediaData, System.Linq.Enumerable.ToDictionary(beatmapLevel.beatmapBasicData, pair => pair.Key, pair => pair.Value))
        {
            this.playlistSong = playlistSong;
        }
    }
}
