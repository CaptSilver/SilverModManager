using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Installers;

public class PredicateBasedInstaller : ALibraryArchiveInstaller
{
    public PredicateBasedInstaller(IServiceProvider serviceProvider) : base(serviceProvider, serviceProvider.GetRequiredService<ILogger<PredicateBasedInstaller>>())
    {
        
    }
    public required Func<Node, bool> Root { get; init; }
    public required GamePath Destination { get; init; }
    
    /// <summary>
    /// Puts the files in a folder below <see cref="Destination"/>, named after the matched root folder, or after the
    /// archive when the root is its top level.
    /// </summary>
    public bool InFolderNamedAfterMod { get; init; }

    /// <summary>
    /// Puts the files in a folder below <see cref="Destination"/>, named after the file with this extension that sits
    /// directly in the matched root (its name without the extension). Takes the place of
    /// <see cref="InFolderNamedAfterMod"/> when set.
    /// </summary>
    public Extension? InFolderNamedAfterFileWithExtension { get; init; }

    /// <summary>
    /// Files in this array will not be installed, and will not cause the installer to fail if they exist.
    /// </summary>
    public object[] IgnoreFiles { get; init; } = [];

    public ref struct Node
    {
        private readonly KeyValuePair<RelativePath, KeyedBox<RelativePath, LibraryArchiveTree>> _node;

        public Node(KeyValuePair<RelativePath, KeyedBox<RelativePath, LibraryArchiveTree>> node)
        {
            _node = node; 
        }

        /// <summary>
        /// True if this node is the root (no parent folder)
        /// </summary>
        public bool IsRoot => _node.Key == RelativePath.Empty;

        /// <summary>
        /// Returns true if this node has a direct child folder with the given path.
        /// </summary>
        public bool HasDirectChildFolder(RelativePath path) => _node.Value.Item.Children.TryGetValue(path, out var child) && !child.Item.IsFile;
        
        /// <summary>
        /// Returns true if this node has a direct child folder with any of the given paths.
        /// </summary>
        public bool HasAnyDirectChildFolder(params ReadOnlySpan<string> path)
        {
            foreach (var child in _node.Children())
            {
                if (child.IsFile())
                    continue;
                for (var i = 0; i < path.Length; i++)
                {
                    if (child.Key.ToString().EndsWith(path[i], StringComparison.InvariantCultureIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true if this node's name matches the given glob.
        /// </summary>
        public bool ThisNameLike(Regex regex) => regex.IsMatch(_node.Key.ToString());
        
        /// <summary>
        /// Returns true if this node's name matches the given name.'
        /// </summary>
        public bool ThisNameIs(string name) => !_node.Value.Item.IsFile && _node.Key == RelativePath.FromUnsanitizedInput(name);

        /// <summary>
        /// Returns true if this node has a direct child that ends with the given postfix.
        /// </summary>
        public bool HasDirectChildEndingIn(string postfix) => _node.Value.Item.Children.Any(c => c.Key.ToString().EndsWith(postfix, StringComparison.InvariantCultureIgnoreCase));
        
        /// <summary>
        /// Returns true if this node has a direct child with the given name.
        /// </summary>
        public bool HasDirectChild(string name) => _node.Value.Item.Children.Any(c => c.Key.ToString().Equals(name, StringComparison.InvariantCultureIgnoreCase));
        
        /// <summary>
        /// Returns true if any of the direct children of this node are files with the given extensions.
        /// </summary>
        public bool IsRootWith(params ReadOnlySpan<Extension> extensions)
        {
            if (_node.Key != RelativePath.Empty)
                return false;
            foreach (var child in _node.Value.Item.Children)
            {
                for (int extIdx = 0; extIdx < extensions.Length; extIdx++)
                {
                    if (!child.Value.Item.IsFile)
                        continue;
                    if (child.Key.Extension == extensions[extIdx])
                        return true;
                }
            }

            return false;
        }
        
        /// <summary>
        /// Returns true if any of the direct children of this node are files with the given extensions.
        /// </summary>
        public bool HasDirectChildrenWith(params ReadOnlySpan<Extension> extensions)
        {
            foreach (var child in _node.Value.Item.Children)
            {
                for (var extIdx = 0; extIdx < extensions.Length; extIdx++)
                {
                    if (!child.Value.Item.IsFile)
                        continue;
                    if (child.Key.Extension == extensions[extIdx])
                        return true;
                }
            }

            return false;
        }
    }

    public override ValueTask<InstallerResult> ExecuteAsync(LibraryArchive.ReadOnly libraryArchive, LoadoutItemGroup.New loadoutGroup, ITransaction transaction, Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var tree = LibraryArchiveTreeExtensions.GetTree(libraryArchive);

        LibraryArchiveTree found;
        if (Root(new Node(new KeyValuePair<RelativePath, KeyedBox<RelativePath, LibraryArchiveTree>>("", tree))))
        {
            found = tree;
        }
        else if (!tree
                     .EnumerateChildrenBfs()
                     .Where(x => Root(new Node(x)))
                     .Select(x => x.Value.Item)
                     .TryGetFirst(out found))
        {
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "Found no matching root"));
        }

        var rootDestination = Destination;
        var destinationFolder = Destination.Path;
        if (InFolderNamedAfterFileWithExtension is { } descriptor)
        {
            if (TryGetDescriptorFolder(found, descriptor, out var descriptorFolder) is false)
                return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "The mod's descriptor file does not make a valid folder name"));
            destinationFolder = destinationFolder / descriptorFolder;
        }
        else if (InFolderNamedAfterMod)
        {
            if (TryGetModFolder(libraryArchive, found, out var modFolder) is false)
                return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "The mod's name does not make a valid folder name"));
            destinationFolder = destinationFolder / modFolder;
        }

        int handled = 0;
        int total = 0;
        foreach (var (_, libraryFile) in tree.EnumerateChildrenBfs())
        {
            if (!libraryFile.Item.IsFile) 
                continue;
            
            total++;
            if (!libraryFile.Item.Value.Path.InFolder(found.Path))
            {
                if (IgnoreFiles.Any(i => IsIgnored(i, libraryFile.Item.Value.Path)))
                {
                    handled++;
                }
                continue;
            }

            var destinationPath = destinationFolder / libraryFile.Item.Path.RelativeTo(found.Path);
            _ = new LoadoutFile.New(transaction, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
                {
                    TargetPath = (loadout.Id, rootDestination.LocationId, destinationPath),
                    LoadoutItem = new LoadoutItem.New(transaction, id)
                    {
                        Name = libraryFile.Item.Path.Name,
                        LoadoutId = loadout.Id,
                        ParentId = loadoutGroup.Id,
                    },
                },
                Hash = libraryFile.Item.LibraryFile.Value.Hash,
                Size = libraryFile.Item.LibraryFile.Value.Size,
            };
            handled++;
        }
        
        if (handled != total)
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "Did not handle all files"));

        return ValueTask.FromResult<InstallerResult>(new Success());
    }

    private static bool TryGetDescriptorFolder(LibraryArchiveTree root, Extension descriptor, out RelativePath folder)
    {
        var file = root.Children
            .Where(child => child.Value.Item.IsFile && child.Key.Extension == descriptor)
            .Select(child => child.Key)
            .OrderBy(name => name.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();

        var name = Path.GetFileNameWithoutExtension(file.ToString());
        var valid = name.Length > 0 && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        folder = valid ? RelativePath.FromUnsanitizedInput(name) : RelativePath.Empty;
        return valid;
    }

    private static bool TryGetModFolder(LibraryArchive.ReadOnly libraryArchive, LibraryArchiveTree root, out RelativePath folder)
    {
        if (root.Path != RelativePath.Empty)
        {
            folder = root.Path.Name;
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(libraryArchive.AsLibraryFile().AsLibraryItem().Name);
        var valid = name.Length > 0 && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        folder = valid ? RelativePath.FromUnsanitizedInput(name) : RelativePath.Empty;
        return valid;
    }

    private bool IsIgnored(object pattern, RelativePath path)
    {
        return pattern switch
        {
            string s => s.Equals(path, StringComparison.InvariantCultureIgnoreCase),
            Regex r => r.IsMatch(path),
            Extension e => path.Extension == e,
            _ => throw new Exception($"No handler for pattern type {pattern.GetType()}")
        };
    }
}
