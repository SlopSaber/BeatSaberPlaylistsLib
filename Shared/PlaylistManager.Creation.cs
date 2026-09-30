using BeatSaberPlaylistsLib.Blist;
using BeatSaberPlaylistsLib.Legacy;
using BeatSaberPlaylistsLib.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace BeatSaberPlaylistsLib
{
    public partial class PlaylistManager
    {
        private sealed class CreationSave
        {
            internal readonly string Directory;
            internal readonly Task<string> Save;
            internal readonly CreationSave? Previous;
            internal CreationSave(string directory, Task<string> save, CreationSave? previous)
            {
                Directory = directory;
                Save = save;
                Previous = previous;
            }
        }

        private sealed class NewPlaylistJob
        {
            internal readonly string Directory;
            internal readonly Task<IPlaylist> Save;
            internal NewPlaylistJob(string directory, Task<IPlaylist> save)
            {
                Directory = directory;
                Save = save;
            }
        }

        private readonly Dictionary<IPlaylist, CreationSave> _pendingCreations = new Dictionary<IPlaylist, CreationSave>();
        private readonly List<NewPlaylistJob> _pendingNewPlaylists = new List<NewPlaylistJob>();

        /// <summary>
        /// Creates and saves a built-in playlist on the worker file queue, then registers its identity on owner.
        /// Custom handler implementations retain owner construction/serialization.
        /// </summary>
        /// <param name="title">Playlist title.</param>
        /// <param name="author">Playlist author.</param>
        /// <param name="getCoverStream">Worker-safe factory for an exclusively owned cover stream, disposed after reading.</param>
        /// <param name="customData">Metadata captured on owner before worker serialization.</param>
        /// <returns>The saved, owner-registered playlist.</returns>
        public async Task<IPlaylist> CreatePlaylistAsync(string title, string? author, Func<Stream?>? getCoverStream = null, IReadOnlyDictionary<string, object>? customData = null)
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            if (!CanPublishFiles()) throw new InvalidOperationException("Playlist manager is detached or being deleted.");
            var data = Utilities.SnapshotCustomData(customData);
            var handler = DefaultHandler ?? System.Linq.Enumerable.FirstOrDefault(PlaylistHandlers.Values)
                ?? throw new InvalidOperationException("PlaylistManager has no registered IPlaylistHandlers.");
            int kind = handler.GetType() == typeof(LegacyPlaylistHandler) ? 1 : handler.GetType() == typeof(BlistPlaylistHandler) ? 2 : 0;
            string extension = handler.DefaultExtension;
            if (kind == 0)
            {
                byte[]? cover = getCoverStream == null ? null : await Task.Run(() =>
                {
                    using var stream = getCoverStream();
                    if (stream == null) return null;
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    return copy.ToArray();
                });
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
                if (!CanPublishFiles()) throw new OperationCanceledException("Creation target changed.");
                var playlist = handler.CreatePlaylist(string.Empty, title, author!, null);
                if (cover != null) playlist.SetCover(cover);
                if (data != null) foreach (var pair in data) playlist.SetCustomData(pair.Key, pair.Value);
                await StorePlaylistAsync(playlist);
                if (string.IsNullOrEmpty(playlist.Filename)) throw new OperationCanceledException("Creation publication was superseded.");
                return playlist;
            }
            string directory = PlaylistPath;
            string[] cachedNames = System.Linq.Enumerable.ToArray(LoadedPlaylists.Keys);
            Task<IPlaylist> save;
            lock (_fileSaveLock)
            {
                Task previous = _pendingFileSaves;
                save = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); }
                    catch { /* Each caller observes its own file failure. */ }
                    IPlaylistHandler ownedHandler = kind == 1 ? (IPlaylistHandler)new LegacyPlaylistHandler() : new BlistPlaylistHandler();
                    var playlist = (Playlist)ownedHandler.CreatePlaylist(string.Empty, title, author!, null, extension);
                    playlist.IsSnapshot = true;
                    if (getCoverStream != null)
                    {
                        using var stream = getCoverStream();
                        if (stream != null) playlist.SetCover(stream);
                    }
                    if (data != null) foreach (var pair in data) playlist.SetCustomData(pair.Key, pair.Value);
                    playlist.Filename = SaveNewSnapshot(ownedHandler, playlist, directory, extension, cachedNames);
                    return (IPlaylist)playlist;
                });
                _pendingFileSaves = save;
            }
            return await PublishNewPlaylistAsync(new NewPlaylistJob(directory, save));
        }

        private async Task<IPlaylist> PublishNewPlaylistAsync(NewPlaylistJob job)
        {
            _pendingNewPlaylists.Add(job);
            IPlaylist result;
            try { result = await job.Save; }
            finally
            {
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
                _pendingNewPlaylists.Remove(job);
            }
            if (PlaylistPath != job.Directory || !CanPublishFiles() || !PublishCreatedPlaylist(result, result.Filename))
                throw new OperationCanceledException("Creation target changed before publication.");
            return result;
        }

        /// <summary>
        /// Clones the current saved file into a writable playlist with a new filename and identity.
        /// Exact built-in handlers read, decode and persist on workers; custom handlers deserialize on owner.
        /// </summary>
        /// <param name="playlist">Playlist whose saved file is copied.</param>
        /// <param name="handler">Handler to use, or the registered extension/type handler.</param>
        /// <returns>The saved and owner-registered writable clone.</returns>
        public async Task<IPlaylist> ClonePlaylistAsync(IPlaylist playlist, IPlaylistHandler? handler = null)
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            if (playlist == null) throw new ArgumentNullException(nameof(playlist));
            if (!CanPublishFiles()) throw new InvalidOperationException("Playlist manager is detached or being deleted.");
            handler ??= playlist.SuggestedExtension == null ? null : GetHandlerForExtension(playlist.SuggestedExtension);
            handler ??= GetHandlerForPlaylistType(playlist.GetType()) ?? throw new InvalidOperationException("No handler supports this playlist.");
            string directory = PlaylistPath;
            string extension = playlist.SuggestedExtension ?? handler.DefaultExtension;
            string sourcePath = Path.Combine(directory, playlist.Filename + "." + extension);
            string[] cachedNames = System.Linq.Enumerable.ToArray(LoadedPlaylists.Keys);
            int kind = handler.GetType() == typeof(LegacyPlaylistHandler) ? 1 : handler.GetType() == typeof(BlistPlaylistHandler) ? 2 : 0;
            if (kind == 0)
            {
                Task<byte[]> read;
                lock (_fileSaveLock)
                {
                    Task previous = _pendingFileSaves;
                    read = Task.Run(async () =>
                    {
                        try { await previous.ConfigureAwait(false); }
                        catch { /* Each caller observes its own file failure. */ }
                        return File.ReadAllBytes(sourcePath);
                    });
                    _pendingFileSaves = read;
                }
                byte[] bytes = await read;
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
                if (PlaylistPath != directory || !CanPublishFiles()) throw new OperationCanceledException("Clone target changed.");
                using var stream = new MemoryStream(bytes, false);
                var clone = handler.Deserialize(stream);
                clone.ReadOnly = false;
                clone.Filename = string.Empty;
                clone.SuggestedExtension = extension;
                await StorePlaylistAsync(clone);
                if (string.IsNullOrEmpty(clone.Filename)) throw new OperationCanceledException("Clone publication was superseded.");
                return clone;
            }

            Task<IPlaylist> save;
            NewPlaylistJob job;
            lock (_fileSaveLock)
            {
                Task previous = _pendingFileSaves;
                save = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); }
                    catch { /* Each caller observes its own file failure. */ }
                    IPlaylistHandler ownedHandler = kind == 1 ? (IPlaylistHandler)new LegacyPlaylistHandler() : new BlistPlaylistHandler();
                    var clone = (Playlist)ownedHandler.CreatePlaylist(string.Empty, string.Empty, null, null, extension);
                    clone.IsSnapshot = true;
                    using (var stream = File.OpenRead(sourcePath)) ownedHandler.Populate(stream, clone);
                    clone.ReadOnly = false;
                    clone.Filename = SaveNewSnapshot(ownedHandler, clone, directory, extension, cachedNames);
                    return (IPlaylist)clone;
                });
                _pendingFileSaves = save;
                job = new NewPlaylistJob(directory, save);
                _pendingNewPlaylists.Add(job);
            }
            IPlaylist result;
            try { result = await save; }
            catch
            {
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
                _pendingNewPlaylists.Remove(job);
                throw;
            }
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            _pendingNewPlaylists.Remove(job);
            if (PlaylistPath != directory || !CanPublishFiles() || !PublishCreatedPlaylist(result, result.Filename))
                throw new OperationCanceledException("Clone target changed before publication.");
            return result;
        }

        private bool CanPublishFiles()
        {
            for (var manager = this; ; manager = manager.Parent!)
            {
                if (manager._deleting) return false;
                if (manager.Parent == null) return true;
                if (!manager.Parent.ChildManagers.Contains(manager)) return false;
            }
        }

        private void RemovePendingCreation(IPlaylist playlist, CreationSave? creation)
        {
            if (creation != null && _pendingCreations.TryGetValue(playlist, out var current) && ReferenceEquals(current, creation))
                _pendingCreations.Remove(playlist);
        }

        private bool PublishCreatedPlaylist(IPlaylist playlist, string filename)
        {
            if (!CanPublishFiles() || (!string.IsNullOrEmpty(playlist.Filename) && playlist.Filename != filename)) return false;
            if (TryGetPlaylist(filename, out var cached) && !ReferenceEquals(cached, playlist))
                throw new InvalidOperationException("A different playlist registered the new filename.");
            playlist.Filename = filename;
            if (playlist is Playlist prepared) prepared.IsSnapshot = false;
            RegisterPlaylist(playlist, false);
            return true;
        }

        private bool TryPublishPendingCreation(string filename, out IPlaylist? playlist)
        {
            playlist = null;
            if (!CanPublishFiles()) return false;
            if (TryGetPlaylist(filename, out var cached))
            {
                playlist = cached;
                return cached != null;
            }
            foreach (var pair in _pendingCreations)
                for (var creation = pair.Value; creation != null; creation = creation.Previous)
                {
                    // These are completed worker file tasks; reading results never joins owner publication.
                    if (creation.Directory != PlaylistPath || creation.Save.Status != TaskStatus.RanToCompletion) continue;
                    string savedName = creation.Save.Result;
                    if (!savedName.Equals(filename, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!PublishCreatedPlaylist(pair.Key, savedName)) continue;
                    playlist = pair.Key;
                    return true;
                }
            foreach (var job in _pendingNewPlaylists)
            {
                if (job.Directory != PlaylistPath || job.Save.Status != TaskStatus.RanToCompletion) continue;
                var result = job.Save.Result;
                if (!result.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PublishCreatedPlaylist(result, result.Filename)) continue;
                playlist = result;
                return true;
            }
            return false;
        }

        private static string SaveNewSnapshot(IPlaylistHandler handler, IPlaylist draft, string directory, string extension, string[] cachedNames)
        {
            string stem = string.Join("_", string.Join(string.Empty, draft.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Split());
            if (string.IsNullOrEmpty(stem)) stem = "playlist";
            var unavailable = new HashSet<string>(cachedNames, StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(directory, "*.*"))
                unavailable.Add(Path.GetFileNameWithoutExtension(path));
            string temporary = Path.Combine(directory, ".playlist-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                handler.SerializeToFile(draft, temporary);
                for (int duplicate = 0; ; ++duplicate)
                {
                    string filename = duplicate == 0 ? stem : stem + "(" + duplicate + ")";
                    string destination = Path.Combine(directory, filename + "." + extension);
                    if (unavailable.Contains(filename) || File.Exists(destination)) continue;
                    try { File.Move(temporary, destination); }
                    catch (IOException) when (File.Exists(destination)) { continue; }
                    draft.Filename = filename;
                    return filename;
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
