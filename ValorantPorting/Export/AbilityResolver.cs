using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.Export;

// Which ability an ability model really belongs to, and how multi-part models are put together in game.
// Riot's model folders don't follow the abilities (Cypher's Spycam models sit in the Cyber Cage folder), so this looks at
// the game files that use a model: an ability blueprint, projectile or placed object in some ability folder, whose
// nearest "UIData_…" holds the ability's name and key. The placed object (e.g. Chamber's trap) also holds each part's
// offset, rotation and size.
public sealed class AbilityResolver
{
    private readonly IFileProvider provider;
    private readonly Dictionary<ulong, List<string>> importers = new(); // package id -> files that import it

    private static readonly Regex AbilityFile = new(@"^ShooterGame/Content/Characters/[^/]+/S0/Ability_[^/]+/", RegexOptions.IgnoreCase);
    private static readonly Regex NotInGame = new(@"/Console/|/NPE/|Prototype|Gungame|FXC_CharacterSelect|/_Archive/", RegexOptions.IgnoreCase);

    public AbilityResolver(IFileProvider provider)
    {
        this.provider = provider;
        if (provider is not AbstractVfsFileProvider vfs) return;
        foreach (var reader in vfs.MountedVfs)
        {
            if (reader is not IoStoreReader io || io.ContainerHeader is not { StoreEntries.Length: > 0 } header) continue;
            for (var i = 0; i < header.StoreEntries.Length; i++)
            {
                if (!io.PackageIdIndex.TryGetValue(header.PackageIds[i], out var file)) continue;
                foreach (var imported in header.StoreEntries[i].ImportedPackages)
                {
                    if (!importers.TryGetValue(imported.id, out var list)) importers[imported.id] = list = [];
                    list.Add(file.Path);
                }
            }
        }
    }

    // "/Game/X/Y.Y" -> files (ShooterGame/Content/…uasset) that import the package /Game/X/Y
    private List<string> Users(string objectOrPackagePath)
    {
        var package = objectOrPackagePath.Contains('.') ? objectOrPackagePath[..objectOrPackagePath.LastIndexOf('.')] : objectOrPackagePath;
        return importers.GetValueOrDefault(FPackageId.FromName(package).id) ?? [];
    }

    private static string PackageOf(string file) => "/Game/" + file["ShooterGame/Content/".Length..^".uasset".Length];

    // The ability (name, key) that uses this model: from the UIData nearest to the in-game files using it directly; if
    // none, from the files using those (only when they all point to one ability, e.g. not a parent shared by two).
    public (string Name, string Key)? AbilityOf(string modelObjectPath)
    {
        static bool InGameAbilityFile(string file) => file.EndsWith(".uasset") && AbilityFile.IsMatch(file) && !NotInGame.IsMatch(file);
        static string DirectoryOf(string file) => file[..file.LastIndexOf('/')];

        var direct = Users(modelObjectPath).Where(f => f.EndsWith(".uasset") && !NotInGame.IsMatch(f)).ToList();
        var candidates = direct.Where(InGameAbilityFile).Select(DirectoryOf).ToList();
        if (candidates.Count == 0)
        {
            var indirect = direct.SelectMany(f => Users(PackageOf(f))).Where(InGameAbilityFile).Select(DirectoryOf).ToList();
            var abilities = indirect.Select(d => Regex.Match(d, @"^.*?/Ability_[^/]+").Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (abilities.Count == 1) candidates = indirect;
        }

        foreach (var directory in candidates.GroupBy(d => d, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key))
            if (UiDataNear(directory) is { } info) return info;
        return null;
    }

    // walk up from a folder to its Ability_* folder, taking the first UIData found
    private (string Name, string Key)? UiDataNear(string directory)
    {
        var current = directory;
        while (current.Contains("/Ability_"))
        {
            var folder = current["ShooterGame/Content/".Length..];
            if (AbilityItem.ReadAbilityInfo(provider, folder) is { } info) return info;
            if (Regex.IsMatch(current, @"/Ability_[^/]+$")) break;
            current = current[..current.LastIndexOf('/')];
        }

        return null;
    }

    private static readonly Regex PlaceholderMaterial = new(@"^(lambert\d*|.*Clear.*|WorldGridMaterial|DefaultMaterial)$", RegexOptions.IgnoreCase);

    // a model whose materials are only leftovers (lambert1) or invisible (Clear): a holder the game fills otherwise
    public static bool HasOnlyPlaceholderMaterials(IFileProvider provider, string modelObjectPath)
    {
        try
        {
            var names = provider.LoadPackageObject(modelObjectPath) switch
            {
                CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh skeletal => skeletal.Materials.Select(m => m?.Name),
                CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh staticMesh => staticMesh.StaticMaterials.Select(m => m?.MaterialInterface?.Name),
                _ => []
            };
            var list = names.Where(n => n != null).ToList();
            return list.Count > 0 && list.All(n => PlaceholderMaterial.IsMatch(n!));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public record Placement(FVector Location, FRotator Rotation, FVector Scale);

    // Each part's offset/rotation/size from the placed object that has all of them as components (Chamber's trap:
    // body and rotator 3x, the eye 8.7 cm up). Null when no single object holds at least two of the parts.
    public Dictionary<string, Placement>? PlacementOf(IReadOnlyList<string> partObjectPaths)
    {
        var wanted = partObjectPaths.ToDictionary(p => p[(p.LastIndexOf('.') + 1)..], p => p, StringComparer.OrdinalIgnoreCase);
        var candidates = partObjectPaths.SelectMany(Users).Where(f => f.EndsWith(".uasset") && !NotInGame.IsMatch(f))
            .GroupBy(f => f).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(12);

        Dictionary<string, Placement>? best = null;
        foreach (var file in candidates)
        {
            try
            {
                var found = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
                var exports = provider.LoadPackage(file).GetExports().ToList();
                var byName = exports.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First());

                // the blueprint's component tree (SCS nodes): child component template -> parent component template
                var parentOf = new Dictionary<string, string>();
                foreach (var node in exports.Where(e => e.ExportType == "SCS_Node"))
                {
                    var template = node.GetOrDefault<FPackageIndex>("ComponentTemplate")?.Name;
                    if (template is null) continue;
                    foreach (var child in node.GetOrDefault("ChildNodes", Array.Empty<FPackageIndex>()))
                        if (child?.Load() is { } childNode && childNode.GetOrDefault<FPackageIndex>("ComponentTemplate")?.Name is { } childTemplate)
                            parentOf[childTemplate] = template;
                }

                FTransform Relative(UObject component) => new(
                    component.GetOrDefault("RelativeRotation", FRotator.ZeroRotator),
                    component.GetOrDefault("RelativeLocation", FVector.ZeroVector),
                    component.GetOrDefault("RelativeScale3D", FVector.OneVector));

                // a component's transform in the object: its own, then its parents' (the Trap's eye sits on the 3x rotator)
                FTransform InObject(UObject component, int depth = 0)
                {
                    var transform = Relative(component);
                    if (depth < 8 && parentOf.TryGetValue(component.Name, out var parent) && byName.TryGetValue(parent, out var parentComponent))
                        transform = transform * InObject(parentComponent, depth + 1);
                    return transform;
                }

                foreach (var export in exports)
                {
                    var mesh = export.GetOrDefault<FPackageIndex>("SkeletalMesh") ?? export.GetOrDefault<FPackageIndex>("StaticMesh");
                    if (mesh?.Name is not { } meshName || !wanted.TryGetValue(meshName, out var partPath) || found.ContainsKey(partPath)) continue;
                    var transform = InObject(export);
                    found[partPath] = new Placement(transform.Translation, transform.Rotator(), transform.Scale3D);
                }

                if (found.Count >= 2 && (best is null || found.Count > best.Count)) best = found;
                if (best?.Count == wanted.Count) break;
            }
            catch (Exception)
            {
                // unreadable: try the next one
            }
        }

        return best;
    }
}
