using BeatSaberPlaylistsLib.Blist;
using BeatSaberPlaylistsLib.Legacy;
using BeatSaberPlaylistsLib.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSaberPlaylistsLib
{
    public partial class PlaylistManager
    {
        private PlaylistManager(PlaylistManager parent, string preparedPath)
        {
            PlaylistPath = preparedPath;
            Parent = parent;
            ChildManagers = new List<PlaylistManager>();
        }

        /// <summary>Owner-published playlist arrays from one asynchronous directory scan.</summary>
        public sealed class ScanResult
        {
            /// <summary>Playlists in directory and child-manager order.</summary>
            public IPlaylist[] Playlists { get; }
            /// <summary>Direct playlists for each scanned manager. Treat returned arrays as read-only.</summary>
            public IReadOnlyDictionary<PlaylistManager, IPlaylist[]> PlaylistsByManager { get; }
            /// <summary>Individual file failures, if any.</summary>
            public AggregateException? Exception { get; }

            internal ScanResult(Dictionary<PlaylistManager, IPlaylist[]> managers, IPlaylist[] playlists, List<Exception> errors)
            {
                PlaylistsByManager = new ReadOnlyDictionary<PlaylistManager, IPlaylist[]>(managers);
                Playlists = playlists;
                Exception = errors.Count == 0 ? null : new AggregateException("Errors scanning playlists.", errors);
            }
        }

        private sealed class ScanHandler
        {
            internal readonly IPlaylistHandler Handler;
            internal readonly int Kind;
            internal ScanHandler(IPlaylistHandler handler)
            {
                Handler = handler;
                Kind = handler.GetType() == typeof(LegacyPlaylistHandler) ? 1
                    : handler.GetType() == typeof(BlistPlaylistHandler) ? 2 : 0;
            }
        }

        private sealed class ScanFile
        {
            internal readonly string Name;
            internal readonly string Extension;
            internal readonly ScanHandler Handler;
            internal IPlaylist? Playlist;
            internal byte[]? Bytes;
            internal ScanFile(string name, string extension, ScanHandler handler)
            {
                Name = name;
                Extension = extension;
                Handler = handler;
            }
        }

        private sealed class ScanNode
        {
            internal readonly PlaylistManager Manager;
            internal readonly string Path;
            internal readonly Dictionary<string, ScanHandler> Handlers;
            internal readonly HashSet<string> CachedNames;
            internal readonly List<ScanNode> Children;
            internal readonly bool Detached;
            internal readonly long Revision;
            internal readonly Dictionary<string, ScanFile> Files = new Dictionary<string, ScanFile>(StringComparer.OrdinalIgnoreCase);
            internal string[] Names = Array.Empty<string>();
            internal bool Exists;
            internal ScanNode(PlaylistManager manager, Dictionary<string, ScanHandler> handlers, HashSet<string> cachedNames, List<ScanNode> children, bool detached = false)
            {
                Manager = manager;
                Path = manager.PlaylistPath;
                Revision = manager._fileListRevision;
                Handlers = handlers;
                CachedNames = cachedNames;
                Children = children;
                Detached = detached;
            }
        }

        /// <summary>
        /// Scans files and prepares detached built-in playlists on workers, then registers results on the owning context.
        /// Cached playlists keep their identities and current unsaved state. Custom handlers deserialize owned bytes on owner.
        /// With children enabled, discovers new directories and removes missing child managers.
        /// </summary>
        /// <param name="includeChildren">Scan child directories recursively.</param>
        /// <param name="cancellationToken">Cancel preparation or owner publication.</param>
        /// <returns>Published arrays and individual file failures.</returns>
        public async Task<ScanResult> GetAllPlaylistsAsync(bool includeChildren = false, CancellationToken cancellationToken = default)
        {
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            await WaitForStoragePublicationAsync(includeChildren);
            var node = CaptureScan(includeChildren);
            var errors = new List<Exception>();
            await Task.Run(() => PrepareScan(node, includeChildren, errors, cancellationToken), cancellationToken);
#if BeatSaber
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
#endif
            await WaitForStoragePublicationAsync(includeChildren);
            cancellationToken.ThrowIfCancellationRequested();
            var managers = new Dictionary<PlaylistManager, IPlaylist[]>();
            PublishScan(node, includeChildren, managers, errors);
            var arrays = managers.Values.ToArray();
            var playlists = await Task.Run(() => arrays.SelectMany(array => array).ToArray(), cancellationToken);
            return new ScanResult(managers, playlists, errors);
        }

        private ScanNode CaptureScan(bool includeChildren)
        {
            var handlers = new Dictionary<string, ScanHandler>(StringComparer.OrdinalIgnoreCase);
            foreach (string extension in GetSupportedExtensions())
            {
                var handler = GetHandlerForExtension(extension);
                if (handler != null) handlers[extension] = new ScanHandler(handler);
            }
            var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CaptureCachedNames(cached);
            return new ScanNode(this, handlers, cached, includeChildren
                ? ChildManagers.Select(child => child.CaptureScan(true)).ToList() : new List<ScanNode>());
        }

        private void CaptureCachedNames(HashSet<string> names)
        {
            foreach (string name in LoadedPlaylists.Keys) names.Add(name);
            foreach (var child in ChildManagers) child.CaptureCachedNames(names);
        }

        private static ScanNode CaptureNewScan(PlaylistManager ownedManager, Dictionary<string, ScanHandler> handlers)
        {
            return new ScanNode(ownedManager, handlers, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new List<ScanNode>(), true);
        }

        private static void PrepareScan(ScanNode node, bool includeChildren, List<Exception> errors, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            node.Exists = Directory.Exists(node.Path);
            if (!node.Exists) return;
            string[] paths = Directory.GetFiles(node.Path, "*.*");
            node.Names = paths.Select(System.IO.Path.GetFileNameWithoutExtension).ToArray();
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                string extension = System.IO.Path.GetExtension(path).TrimStart('.');
                if (node.CachedNames.Contains(name) || node.Files.ContainsKey(name)
                    || !node.Handlers.TryGetValue(extension, out var binding)) continue;
                var file = new ScanFile(name, extension, binding);
                node.Files.Add(name, file);
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    if (binding.Kind == 0) file.Bytes = bytes;
                    else
                    {
                        IPlaylistHandler handler = binding.Kind == 1 ? (IPlaylistHandler)new LegacyPlaylistHandler() : new BlistPlaylistHandler();
                        var draft = (Playlist)handler.CreatePlaylist(name, string.Empty, null, null, extension);
                        draft.IsSnapshot = true;
                        using var stream = new MemoryStream(bytes, false);
                        handler.Populate(stream, draft);
                        file.Playlist = draft;
                    }
                }
                catch (Exception e) { errors.Add(new PlaylistSerializationException($"Loading '{path}' failed.", e)); }
            }
            if (!includeChildren) return;
            foreach (string path in Directory.GetDirectories(node.Path))
            {
                token.ThrowIfCancellationRequested();
                if (node.Children.Any(child => child.Path == path) || Directory.GetFiles(path, "*.plignore").Length != 0) continue;
                var ownedManager = new PlaylistManager(node.Manager, path);
                node.Children.Add(CaptureNewScan(ownedManager, node.Handlers));
            }
            foreach (var child in node.Children)
            {
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(child.Path) && Directory.GetFiles(child.Path, "*.plignore").Length != 0)
                    child.Exists = false;
                else PrepareScan(child, true, errors, token);
            }
        }

        private static void PublishScan(ScanNode node, bool includeChildren, Dictionary<PlaylistManager, IPlaylist[]> managers, List<Exception> errors)
        {
            var owner = node.Manager;
            if (owner.PlaylistPath != node.Path || owner._fileListRevision != node.Revision) return;
            var playlists = new List<IPlaylist>(node.Names.Length);
            foreach (string name in node.Names)
            {
                if (owner.TryPublishPendingCreation(name, out var created))
                {
                    playlists.Add(created!);
                    continue;
                }
                if (owner.TryGetPlaylist(name, true, out var cached) && cached != null)
                {
                    playlists.Add(cached);
                    continue;
                }
                if (!node.Files.TryGetValue(name, out var file)) continue;
                if (!ReferenceEquals(owner.GetHandlerForExtension(file.Extension), file.Handler.Handler)) continue;
                try
                {
                    var playlist = file.Playlist;
                    if (playlist == null && file.Bytes != null)
                    {
                        using var stream = new MemoryStream(file.Bytes, false);
                        playlist = file.Handler.Handler.Deserialize(stream);
                    }
                    if (playlist == null) continue;
                    playlist.Filename = file.Name;
                    playlist.SuggestedExtension = file.Extension;
                    if (playlist is Playlist prepared) prepared.IsSnapshot = false;
                    owner.RegisterPlaylist(playlist, false);
                    playlists.Add(playlist);
                }
                catch (Exception e) { errors.Add(new PlaylistSerializationException($"Publishing '{file.Name}' failed.", e)); }
            }
            managers.Add(owner, playlists.ToArray());
            if (!includeChildren) return;
            foreach (var child in node.Children)
            {
                if (child.Manager.PlaylistPath != child.Path
                    || (!child.Detached && !owner.ChildManagers.Contains(child.Manager))) continue;
                if (!child.Exists) owner.ChildManagers.Remove(child.Manager);
                else
                {
                    var current = owner.ChildManagers.FirstOrDefault(manager => manager.PlaylistPath == child.Path);
                    if (current != null && !ReferenceEquals(current, child.Manager)) continue;
                    if (current == null) owner.ChildManagers.Add(child.Manager);
                    PublishScan(child, true, managers, errors);
                }
            }
        }
    }
}
