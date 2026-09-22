#if BeatSaber
extern alias BeatSaber;

namespace BeatSaberPlaylistsLib.Types
{
    /// <summary>
    /// Interface for a playlist.
    /// </summary>
    public partial interface IPlaylist : IStagedSpriteLoad
    {
        /// <summary>
        /// The game level-pack identifier for this playlist.
        /// </summary>
        string ID { get; }

        /// <summary>
        /// A generated <see cref="PlaylistLevelPack"/> for this playlist.
        /// </summary>
        PlaylistLevelPack PlaylistLevelPack { get; }

        /// <summary>
        /// The <see cref="BeatSaber::BeatmapLevel"/>s associated with this playlist.
        /// </summary>
        BeatSaber::BeatmapLevel[] BeatmapLevels { get; }

        /// <summary>
        /// Adds the <see cref="ISong"/> to the playlist.
        /// Does nothing if <see cref="AllowDuplicates"/> is false and the song is already in the playlist.
        /// Converts the <see cref="ISong"/> if needed.
        /// </summary>
        /// <param name="beatmapLevel"></param>
        /// <param name="beatmapKey"></param>
        /// <returns>The added IPlaylistSong, null if nothing was added.</returns>
        IPlaylistSong? Add(BeatSaber::BeatmapLevel beatmapLevel, BeatSaber::BeatmapKey? beatmapKey = null);
    }
}

#endif
