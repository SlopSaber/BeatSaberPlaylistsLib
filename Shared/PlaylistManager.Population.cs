using BeatSaberPlaylistsLib.Blist;
using BeatSaberPlaylistsLib.Legacy;
using BeatSaberPlaylistsLib.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSaberPlaylistsLib
{
    public partial class PlaylistManager
    {
        private sealed class PopulationOperation
        {
            internal readonly PlaylistManager Owner;
            internal readonly IPlaylist Playlist;
            internal readonly IPlaylistHandler Handler;
            internal readonly string Directory;
            internal readonly string Filename;
            internal readonly string? Extension;
            internal readonly long Version;
            internal readonly Func<Stream, Action>? Prepare;
            internal Action? Publish;
            internal Exception? Error;

            internal PopulationOperation(PlaylistManager owner, IPlaylist playlist, IPlaylistHandler handler)
            {
                Owner = owner;
                Playlist = playlist;
                Handler = handler;
                Directory = owner.PlaylistPath;
                Filename = playlist.Filename;
                Extension = playlist.SuggestedExtension;
                Version = owner.AdvanceSaveVersion(playlist);
                if (playlist.GetType() == typeof(LegacyPlaylist) && handler.GetType() == typeof(LegacyPlaylistHandler))
                    Prepare = ((LegacyPlaylist)playlist).CapturePopulation();
                else if (playlist.GetType() == typeof(BlistPlaylist) && handler.GetType() == typeof(BlistPlaylistHandler))
                    Prepare = ((BlistPlaylist)playlist).CapturePopulation();
            }
        }

        /// <summary>
        /// Replaces a registered playlist's songs and populates metadata from a caller-transferred readable stream.
        /// Exact built-in handlers decode on workers; custom implementations populate owned bytes on owner.
        /// Retains the playlist identity, omitted metadata and untouched custom objects. Does not raise PlaylistChanged.
        /// </summary>
        /// <param name="playlist">Current registered playlist.</param>
        /// <param name="stream">Exclusively owned stream; do not access or dispose it until this task completes.</param>
        /// <param name="handler">Handler to use, or this manager's default.</param>
        /// <param name="cancellationToken">Cancel preparation or publication.</param>
        /// <returns>False if the playlist changed before publication.</returns>
        public async Task<bool> PopulatePlaylistAsync(IPlaylist playlist, Stream stream, IPlaylistHandler? handler = null, CancellationToken cancellationToken = default)
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            if (playlist == null) throw new ArgumentNullException(nameof(playlist));
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            handler ??= DefaultHandler ?? throw new InvalidOperationException("PlaylistManager has no default handler.");
            var operation = new PopulationOperation(this, playlist, handler);
            await Task.Run(() => PreparePopulation(operation, stream), cancellationToken);
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            cancellationToken.ThrowIfCancellationRequested();
            return PublishPopulation(operation);
        }

        /// <summary>
        /// Scans and reloads playlists with worker file/JSON preparation and owner publication.
        /// Keeps the legacy default-handler extension selection. File failures preserve built-in live data and are returned individually.
        /// </summary>
        /// <param name="refreshChildren">Include child managers.</param>
        /// <param name="cancellationToken">Cancel preparation or publication.</param>
        /// <returns>Scanned playlists and file failures.</returns>
        public async Task<ScanResult> RefreshPlaylistsAsync(bool refreshChildren, CancellationToken cancellationToken = default)
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            var known = new HashSet<IPlaylist>();
            CaptureKnownPlaylists(known, refreshChildren);
            var scan = await GetAllPlaylistsAsync(refreshChildren, cancellationToken);
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            var operations = new List<PopulationOperation>();
            var preparations = new List<Task>();
            CaptureReloads(scan, known, refreshChildren, operations, preparations, cancellationToken);
            await Task.WhenAll(preparations);
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            cancellationToken.ThrowIfCancellationRequested();
            var errors = scan.Exception == null ? new List<Exception>() : new List<Exception>(scan.Exception.InnerExceptions);
            foreach (var operation in operations)
            {
                if (operation.Error != null) errors.Add(operation.Error);
                else if (IsCurrentManager(operation.Owner) && ReferenceEquals(operation.Owner.DefaultHandler, operation.Handler))
                {
                    try { PublishPopulation(operation); }
                    catch (Exception e) { errors.Add(new PlaylistSerializationException($"Publishing '{operation.Filename}' failed.", e)); }
                }
            }
            var managers = new Dictionary<PlaylistManager, IPlaylist[]>();
            foreach (var pair in scan.PlaylistsByManager) managers.Add(pair.Key, pair.Value);
            return new ScanResult(managers, scan.Playlists, errors);
        }

        private void CaptureKnownPlaylists(HashSet<IPlaylist> playlists, bool children)
        {
            foreach (var playlist in LoadedPlaylists.Values) playlists.Add(playlist);
            if (children) foreach (var child in ChildManagers) child.CaptureKnownPlaylists(playlists, true);
        }

        private void CaptureReloads(ScanResult scan, HashSet<IPlaylist> known, bool children, List<PopulationOperation> operations, List<Task> preparations, CancellationToken token)
        {
            if (children) foreach (var child in ChildManagers) child.CaptureReloads(scan, known, true, operations, preparations, token);
            if (!scan.PlaylistsByManager.TryGetValue(this, out var playlists)) return;
            var handler = DefaultHandler ?? throw new InvalidOperationException("PlaylistManager has no default handler.");
            var batch = new List<(PopulationOperation Operation, string Path)>();
            var captured = new HashSet<IPlaylist>();
            foreach (var playlist in playlists)
            {
                if (!captured.Add(playlist) || !TryGetPlaylist(playlist.Filename, out var current) || !ReferenceEquals(current, playlist)) continue;
                string extension = string.IsNullOrWhiteSpace(playlist.SuggestedExtension) ? handler.DefaultExtension : playlist.SuggestedExtension!;
                if (!handler.SupportsExtension(extension)) continue;
                bool builtIn = (playlist.GetType() == typeof(LegacyPlaylist) && handler.GetType() == typeof(LegacyPlaylistHandler))
                    || (playlist.GetType() == typeof(BlistPlaylist) && handler.GetType() == typeof(BlistPlaylistHandler));
                if (builtIn && !known.Contains(playlist)) continue;
                var operation = new PopulationOperation(this, playlist, handler);
                operations.Add(operation);
                batch.Add((operation, Path.Combine(PlaylistPath, playlist.Filename + "." + extension)));
            }
            if (batch.Count == 0) return;
            lock (_fileSaveLock)
            {
                Task previous = _pendingFileSaves;
                var preparation = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); }
                    catch { /* Each file-operation caller observes its own failure. */ }
                    foreach (var item in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (!File.Exists(item.Path)) continue;
                            using var stream = File.OpenRead(item.Path);
                            PreparePopulation(item.Operation, stream);
                        }
                        catch (Exception e) { item.Operation.Error = new PlaylistSerializationException($"Reloading '{item.Path}' failed.", e); }
                    }
                }, token);
                _pendingFileSaves = preparation;
                preparations.Add(preparation);
            }
        }

        private static void PreparePopulation(PopulationOperation operation, Stream stream)
        {
            if (operation.Prepare != null) operation.Publish = operation.Prepare(stream);
            else
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                byte[] bytes = copy.ToArray();
                operation.Publish = () =>
                {
                    operation.Playlist.Clear();
                    using var ownedStream = new MemoryStream(bytes, false);
                    operation.Handler.Populate(ownedStream, operation.Playlist);
                };
            }
        }

        private static bool PublishPopulation(PopulationOperation operation)
        {
            var owner = operation.Owner;
            var playlist = operation.Playlist;
            for (var manager = owner; manager.Parent != null; manager = manager.Parent)
                if (!manager.Parent.ChildManagers.Contains(manager)) return false;
            if (operation.Publish == null || owner.PlaylistPath != operation.Directory || playlist.Filename != operation.Filename
                || playlist.SuggestedExtension != operation.Extension || !owner.TryGetPlaylist(operation.Filename, out var current)
                || !ReferenceEquals(current, playlist)) return false;
            lock (owner._changedLock)
                if (!owner._saveVersions.TryGetValue(playlist, out long version) || version != operation.Version) return false;
            operation.Publish();
            return true;
        }

        private bool IsCurrentManager(PlaylistManager manager)
        {
            while (!ReferenceEquals(manager, this))
            {
                var parent = manager.Parent;
                if (parent == null || !parent.ChildManagers.Contains(manager)) return false;
                manager = parent;
            }
            return true;
        }
    }
}
