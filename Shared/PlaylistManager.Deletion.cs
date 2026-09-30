using BeatSaberPlaylistsLib.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace BeatSaberPlaylistsLib
{
    public partial class PlaylistManager
    {
        private sealed class PlaylistDeletion
        {
            internal readonly TaskCompletionSource<object?> Published = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly Dictionary<string, PlaylistDeletion> _playlistDeletes = new Dictionary<string, PlaylistDeletion>(StringComparer.OrdinalIgnoreCase);
        private readonly ConditionalWeakTable<IPlaylist, object> _deletedPlaylists = new ConditionalWeakTable<IPlaylist, object>();
        private long _fileListRevision;

        private void NotifyFileListChanged()
        {
            for (PlaylistManager? manager = this; manager != null; manager = manager.Parent) ++manager._fileListRevision;
        }

        private void CaptureStoragePublications(HashSet<Task> tasks, bool includeChildren)
        {
            if (_directoryMove != null) tasks.Add(_directoryMove.Published.Task);
            foreach (var deletion in _playlistDeletes.Values) tasks.Add(deletion.Published.Task);
            if (includeChildren) foreach (var child in ChildManagers) child.CaptureStoragePublications(tasks, true);
        }

        private async Task WaitForStoragePublicationAsync(bool includeChildren = false)
        {
            for (;;)
            {
                var tasks = new HashSet<Task>();
                CaptureStoragePublications(tasks, includeChildren);
                if (tasks.Count == 0) return;
                await Task.WhenAll(tasks);
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            }
        }

        private bool CanWritePlaylist(IPlaylist playlist)
        {
            string filename = playlist.Filename;
            if (!string.IsNullOrEmpty(filename) && _playlistDeletes.ContainsKey(filename)) return false;
            if (!_deletedPlaylists.TryGetValue(playlist, out _)) return true;
            if (TryGetPlaylist(playlist.Filename, out var registered) && ReferenceEquals(registered, playlist))
            {
                _deletedPlaylists.Remove(playlist);
                return true;
            }
            return false;
        }

        private void RequireWritableIdentity(IPlaylist playlist)
        {
            if (!CanWritePlaylist(playlist)) throw new OperationCanceledException("Playlist identity is being deleted or was deleted.");
        }

        /// <summary>
        /// Queues an isolated playlist file job while rejecting deleted or currently deleting identities.
        /// Call on owner; the worker callback must not access live or native state. The task never waits for owner publication.
        /// </summary>
        /// <param name="playlist">Captured source identity.</param>
        /// <param name="operation">Worker-safe preparation or storage using its resolved directory.</param>
        public Task<TResult> QueuePlaylistFileOperation<TResult>(IPlaylist playlist, Func<string, TResult> operation)
        {
            if (playlist == null) throw new ArgumentNullException(nameof(playlist));
            RequireWritableIdentity(playlist);
            return QueueFileOperation(operation);
        }

        /// <summary>Awaits owner storage publication and rejects a removed playlist before publishing its file result.</summary>
        /// <param name="playlist">Captured source identity.</param>
        public async Task WaitForPlaylistFilePublicationAsync(IPlaylist playlist)
        {
            if (playlist == null) throw new ArgumentNullException(nameof(playlist));
            await WaitForFilePublicationAsync();
            RequireWritableIdentity(playlist);
        }

        /// <summary>
        /// Deletes a registered playlist on the worker file queue, then removes that identity on owner.
        /// File existence, recycling/fallback deletion and verification run on workers. Failure retains cached and dirty state.
        /// </summary>
        /// <param name="playlist">Current registered playlist.</param>
        /// <param name="recycle">Try recycling first, falling back to deletion.</param>
        public async Task DeletePlaylistAsync(IPlaylist playlist, bool recycle = false)
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            if (playlist == null) throw new ArgumentNullException(nameof(playlist));
            if (!CanPublishFiles()) throw new InvalidOperationException("Playlist manager is detached or being deleted.");
            RequireWritableIdentity(playlist);
            string filename = playlist.Filename;
            if (string.IsNullOrEmpty(filename) || !TryGetPlaylist(filename, out var registered) || !ReferenceEquals(registered, playlist))
                throw new FileNotFoundException("Playlist identity is not registered in this manager.");
            string key = filename.ToUpper();
            string extension = string.IsNullOrWhiteSpace(playlist.SuggestedExtension)
                ? (DefaultHandler ?? throw new InvalidOperationException("PlaylistManager has no default handler.")).DefaultExtension
                : playlist.SuggestedExtension!;
            var target = CaptureFileTarget();
            var deletion = new PlaylistDeletion();
            _playlistDeletes.Add(filename, deletion);
            AdvanceSaveVersion(playlist);
            NotifyFileListChanged();
            try
            {
                await QueueFileOperation(directory =>
                {
                    string path = Path.GetFullPath(Path.Combine(directory, filename + '.' + extension));
                    if (!string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Playlist file is outside its manager directory.");
                    if (!File.Exists(path)) throw new FileNotFoundException("Playlist file was not found.", path);
                    if (recycle)
                    {
                        try { if (!NativeUtilities.DeleteFileOrFolder(path)) File.Delete(path); }
                        catch { File.Delete(path); }
                    }
                    else File.Delete(path);
                    if (File.Exists(path)) throw new IOException("Playlist file still exists after deletion.");
                    return true;
                });
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
                await WaitForDirectoryMovesAsync();
                if (!MatchesFileTarget(target)) throw new OperationCanceledException("Deletion target changed before publication.");
                if (LoadedPlaylists.TryGetValue(key, out var current) && ReferenceEquals(current, playlist))
                    LoadedPlaylists.TryRemove(key, out _);
                playlist.PlaylistChanged -= OnPlaylistChanged;
                lock (_changedLock)
                {
                    ChangedPlaylists.Remove(playlist);
                    _saveVersions.Remove(playlist);
                }
                _deletedPlaylists.GetValue(playlist, _ => new object());
            }
            finally
            {
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
                _playlistDeletes.Remove(filename);
                NotifyFileListChanged();
                deletion.Published.TrySetResult(null);
            }
        }
    }
}
