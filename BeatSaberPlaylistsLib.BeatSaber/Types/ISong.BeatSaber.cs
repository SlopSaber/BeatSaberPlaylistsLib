#if BeatSaber
extern alias BeatSaber;
namespace BeatSaberPlaylistsLib.Types
{
    /// <summary>
    /// Interface for the basic data of a song.
    /// </summary>
    public partial interface ISong
    {
        /// <summary>
        /// The <see cref="PlaylistLevel"/> (cast as <see cref="BeatSaber::BeatmapLevel"/>) this playlist song is matched to, if any.
        /// Depends on SongCore being finished loading songs.
        /// </summary>
        BeatSaber::BeatmapLevel? BeatmapLevel { get; }
        /// <summary>
        /// Refreshes the associated <see cref="BeatmapLevel"/> from SongCore.
        /// </summary>
        public void RefreshFromSongCore();
    }
}
#endif
