#if BeatSaber
extern alias BeatSaber;
using System;
using System.Runtime.Serialization;

namespace BeatSaberPlaylistsLib.Types
{
    public abstract partial class PlaylistSong : IPlaylistSong
    {
        [NonSerialized]
        private BeatSaber::BeatmapLevel? _beatmapLevel;

        ///<inheritdoc/>
        [IgnoreDataMember]
        public BeatSaber::BeatmapLevel? BeatmapLevel
        {
            get
            {
                if (_beatmapLevel != null)
                    return _beatmapLevel;

                if (LevelId == null || LevelId.Length == 0)
                    return null;

                var beatmapLevel = SongCore.Loader.GetLevelById(LevelId);
                _beatmapLevel = beatmapLevel != null ? new PlaylistLevel(this, beatmapLevel) : null;
                return _beatmapLevel;
            }
            internal set => _beatmapLevel = value != null ? new PlaylistLevel(this, value) : null;
        }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public void RefreshFromSongCore()
        {
            if (LevelId != null && LevelId.Length > 0)
            {
                BeatmapLevel = SongCore.Loader.GetLevelById(LevelId);
            }
        }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }
}

#endif
