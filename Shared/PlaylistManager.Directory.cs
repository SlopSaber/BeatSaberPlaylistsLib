using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace BeatSaberPlaylistsLib
{
    public partial class PlaylistManager
    {
        private sealed class DirectoryLocation
        {
            internal string Path;
            internal DirectoryLocation(string path) => Path = path;
        }

        private sealed class DirectoryMove
        {
            internal readonly Dictionary<PlaylistManager, string> Destinations = new Dictionary<PlaylistManager, string>();
            internal readonly Dictionary<PlaylistManager, DirectoryLocation> Locations = new Dictionary<PlaylistManager, DirectoryLocation>();
            internal readonly TaskCompletionSource<object?> Published = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task<Exception?> Files = Task.FromResult<Exception?>(null);
        }

        private sealed class FileTarget
        {
            internal readonly DirectoryLocation Location;
            private readonly string _directory;
            private readonly string? _destination;
            private readonly Task<Exception?>? _move;

            internal FileTarget(DirectoryLocation location, string directory, string? destination, Task<Exception?>? move)
            {
                Location = location;
                _directory = directory;
                _destination = destination;
                _move = move;
            }

            internal async Task<string> GetDirectoryAsync() => _move != null && await _move.ConfigureAwait(false) == null
                ? _destination! : _directory;
        }

        private DirectoryLocation? _directoryLocation;
        private DirectoryMove? _directoryMove;

        private FileTarget CaptureFileTarget()
        {
            if (_directoryMove != null)
            {
                var location = _directoryMove.Locations[this];
                if (!ReferenceEquals(_directoryLocation, location) || PlaylistPath != location.Path)
                    throw new OperationCanceledException("Directory changed outside the active relocation.");
            }
            if (_directoryLocation == null || _directoryLocation.Path != PlaylistPath)
                _directoryLocation = new DirectoryLocation(PlaylistPath);
            return new FileTarget(_directoryLocation, PlaylistPath,
                _directoryMove == null ? null : _directoryMove.Destinations[this], _directoryMove?.Files);
        }

        private bool MatchesFileTarget(FileTarget target) => CanPublishFiles()
            && ReferenceEquals(_directoryLocation, target.Location) && PlaylistPath == target.Location.Path;

        /// <summary>
        /// Queues an exclusively owned file operation behind this manager's worker files and directory relocation.
        /// Call on owner; the callback runs on a worker and receives its resolved directory. It must not access live or native state.
        /// The returned task never waits for owner publication and is safe to drain during shutdown.
        /// </summary>
        /// <param name="operation">Worker-safe preparation or storage using captured owned input.</param>
        /// <returns>The worker file result.</returns>
        public Task<TResult> QueueFileOperation<TResult>(Func<string, TResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (!CanPublishFiles()) throw new InvalidOperationException("Playlist manager is detached or being deleted.");
            var target = CaptureFileTarget();
            lock (_fileSaveLock)
            {
                Task previous = _pendingFileSaves;
                var files = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); }
                    catch { /* Each file caller observes its own failure. */ }
                    string directory = await target.GetDirectoryAsync().ConfigureAwait(false);
                    return operation(directory);
                });
                _pendingFileSaves = files;
                return files;
            }
        }

        /// <summary>Restores owner context and awaits directory publication before a caller publishes its prepared file result.</summary>
        public async Task WaitForFilePublicationAsync()
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            await WaitForDirectoryMovesAsync();
            if (!CanPublishFiles()) throw new OperationCanceledException("File publication target is detached or being deleted.");
        }

        private void CaptureDirectoryMoves(HashSet<Task> publications, bool includeChildren)
        {
            if (_directoryMove != null) publications.Add(_directoryMove.Published.Task);
            if (includeChildren) foreach (var child in ChildManagers) child.CaptureDirectoryMoves(publications, true);
        }

        private async Task WaitForDirectoryMovesAsync(bool includeChildren = false)
        {
            for (;;)
            {
                var publications = new HashSet<Task>();
                CaptureDirectoryMoves(publications, includeChildren);
                if (publications.Count == 0) return;
                await Task.WhenAll(publications);
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            }
        }

        private void CaptureRelocatedPaths(string source, string destination, Dictionary<PlaylistManager, string> paths)
        {
            string path = Path.GetFullPath(PlaylistPath);
            string prefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!string.Equals(path, source, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Child manager path is outside the relocated directory.");
            paths.Add(this, string.Equals(path, source, StringComparison.OrdinalIgnoreCase) ? destination : Path.Combine(destination, path.Substring(prefix.Length)));
            foreach (var child in ChildManagers) child.CaptureRelocatedPaths(source, destination, paths);
        }

        private static void PublishRelocatedPaths(Dictionary<PlaylistManager, string> paths)
        {
            foreach (var entry in paths)
            {
                entry.Key.CaptureFileTarget();
                entry.Key.PlaylistPath = entry.Value;
                entry.Key._directoryLocation!.Path = entry.Value;
            }
        }

        private static string GetRenameDestination(string source, string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName) || folderName == "." || folderName == ".."
                || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(folderName) != folderName)
                throw new ArgumentException("A single folder name is required.", nameof(folderName));
            string parent = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("Cannot rename a filesystem root.");
            string destination = Path.GetFullPath(Path.Combine(parent, folderName));
            if (!string.Equals(Path.GetDirectoryName(destination), parent, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Destination must remain in the current parent directory.", nameof(folderName));
            return destination;
        }

        /// <summary>
        /// Moves this folder on a worker after pending descendant file jobs and publishes all retained manager paths on owner.
        /// Overlapping async writes follow the successful destination or the original directory after a failed move.
        /// </summary>
        /// <param name="folderName">A single destination folder name under the same parent directory.</param>
        public async Task RenameManagerAsync(string folderName)
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            await WaitForDirectoryMovesAsync(true);
            if (!CanPublishFiles()) throw new InvalidOperationException("Playlist manager is detached or being deleted.");
            string source = Path.GetFullPath(PlaylistPath);
            string destination = GetRenameDestination(source, folderName);
            if (source == destination) return;
            var move = new DirectoryMove();
            CaptureRelocatedPaths(source, destination, move.Destinations);
            var previous = new List<Task>();
            foreach (var manager in move.Destinations.Keys)
            {
                if (!manager.CanPublishFiles()) throw new InvalidOperationException("A descendant is being deleted.");
                move.Locations.Add(manager, manager.CaptureFileTarget().Location);
                lock (manager._fileSaveLock) previous.Add(manager._pendingFileSaves);
            }
            move.Files = Task.Run(async () =>
            {
                try { await Task.WhenAll(previous).ConfigureAwait(false); }
                catch { /* Each file caller observes its own failure. */ }
                try { Directory.Move(source, destination); return (Exception?)null; }
                catch (Exception error) { return error; }
            });
            foreach (var manager in move.Destinations.Keys)
            {
                manager._directoryMove = move;
                lock (manager._fileSaveLock) manager._pendingFileSaves = move.Files;
            }
            try
            {
                Exception? error = await move.Files;
#if BeatSaber
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
                if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
                foreach (var manager in move.Destinations.Keys)
                    if (!manager.CanPublishFiles() || manager.PlaylistPath != move.Locations[manager].Path
                        || !ReferenceEquals(manager._directoryLocation, move.Locations[manager]))
                        throw new OperationCanceledException("Relocation target changed before publication.");
                PublishRelocatedPaths(move.Destinations);
            }
            finally
            {
                foreach (var manager in move.Destinations.Keys)
                    if (ReferenceEquals(manager._directoryMove, move)) manager._directoryMove = null;
                move.Published.TrySetResult(null);
            }
        }
    }
}
