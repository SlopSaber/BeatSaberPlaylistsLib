extern alias BeatSaber;
using System.Text.RegularExpressions;

namespace BeatSaberPlaylistsLib.Types
{
    /// <summary>
    /// Represents a playlist-based level pack.
    /// </summary>
    public class PlaylistLevelPack : BeatSaber::BeatmapLevelPack
    {
        /// <summary>
        /// The playlist associated with this collection of levels.
        /// </summary>
        public IPlaylist playlist { get; }

        internal PlaylistLevelPack(IPlaylist playlist)
            : base(playlist.ID, Regex.Replace(playlist.Title, @"\t|\n|\r", " "), Regex.Replace(playlist.Title, @"\t|\n|\r", " "), playlist.Sprite, playlist.SmallSprite, BeatSaber::PackBuyOption.Default, playlist.BeatmapLevels, BeatSaber::PlayerSensitivityFlag.Unknown)
        {
            this.playlist = playlist;
        }
    }
}
