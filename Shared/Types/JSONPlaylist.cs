using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace BeatSaberPlaylistsLib.Types
{
    /// <summary>
    /// An abstract class for a playlist storing information in a JSON file.
    /// </summary>
    public abstract class JSONPlaylist<T> : Playlist<T> where T : class, IPlaylistSong, new()
    {
        /// <summary>
        /// Additional data not deserialized into the object.
        /// </summary>
        [JsonExtensionData(ReadData = true, WriteData = false)]
        protected Dictionary<string, JToken>? ExtensionData;

        internal void PublishPopulationData(bool hasCustomData, KeyValuePair<string, object>[] changes, Dictionary<string, JToken>? extensions)
        {
            if (hasCustomData)
            {
                CustomDataInternal ??= new Dictionary<string, object>();
                foreach (var entry in changes) CustomDataInternal[entry.Key] = entry.Value;
            }
            if (extensions != null)
            {
                ExtensionData ??= new Dictionary<string, JToken>();
                foreach (var entry in extensions) ExtensionData[entry.Key] = entry.Value;
            }
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            if (ExtensionData != null && ExtensionData.Count > 0)
            {
                OnExtensionData(ExtensionData.Select(p =>
                {
                    object value;
                    if (p.Value.Type == JTokenType.Array)
                        value = p.Value.ToObject(typeof(IList<object>)) ?? p.Value;
                    else
                        value = p.Value.ToObject<object>() ?? p.Value;

                    return new KeyValuePair<string, object>(p.Key, value);
                }));
            }
        }
    }
}
