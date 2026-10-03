using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
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
                fileOf[header.PackageIds[i].id] = file.Path;
                imports[file.Path] = header.StoreEntries[i].ImportedPackages.Select(p => p.id).ToArray();
                foreach (var imported in header.StoreEntries[i].ImportedPackages)
                {
                    if (!importers.TryGetValue(imported.id, out var list)) importers[imported.id] = list = [];
                    list.Add(file.Path);
                }
            }
        }
    }

    private static readonly object SharedLock = new();
    private static AbilityResolver? shared;

    // one index for the whole session (about 0.2 s to build), e.g. for the effects that play an animation
    public static AbilityResolver Shared(IFileProvider provider)
    {
        lock (SharedLock) return shared ??= new AbilityResolver(provider);
    }

    // "/Game/X/Y.Y" -> files (ShooterGame/Content/…uasset) that import the package /Game/X/Y
    public IReadOnlyList<string> FilesUsing(string objectOrPackagePath) => Users(objectOrPackagePath);

    // "ShooterGame/Content/…uasset" -> the files it imports
    public IReadOnlyList<string> FilesUsedBy(string file) =>
        imports.TryGetValue(file, out var ids) ? ids.Select(id => fileOf.GetValueOrDefault(id)).OfType<string>().ToList() : [];

    private readonly Dictionary<ulong, string> fileOf = new();
    private readonly Dictionary<string, ulong[]> imports = new(StringComparer.OrdinalIgnoreCase);

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

    // a model made only of effect materials: no colour/normal texture anywhere (Fade's Haunt orb: the game draws its
    // look with effects, which can't be exported), so it isn't listed
    public static bool LooksEffectOnly(IFileProvider provider, string modelObjectPath)
    {
        try
        {
            var materials = provider.LoadPackageObject(modelObjectPath) switch
            {
                USkeletalMesh skeletal => skeletal.Materials.Select(m => m?.Load() as UMaterialInterface),
                UStaticMesh staticMesh => staticMesh.StaticMaterials.Select(m => m?.MaterialInterface?.Load() as UMaterialInterface),
                _ => []
            };
            var list = materials.Where(m => m != null).ToList();
            return list.Count > 0 && !list.Any(m => Textured(m!));
        }
        catch (Exception)
        {
            return false;
        }

        static bool Textured(UMaterialInterface material)
        {
            UMaterialInterface? current = material;
            for (var depth = 0; depth < 12 && current != null; depth++)
            {
                if (current is UMaterialInstanceConstant instance)
                {
                    if (instance.TextureParameterValues.Any(t => t?.ParameterValue?.Name is { } name && !name.Contains("Default", StringComparison.OrdinalIgnoreCase)))
                        return true;
                    current = instance.Parent?.Load() as UMaterialInterface;
                }
                else if (current is UMaterial baseMaterial)
                    return baseMaterial.ReferencedTextures.Any(t => t?.Name is { } name && ExportHelpers.IsBuiltInTextureName(name));
                else return false;
            }

            return false;
        }
    }

    // a model the game only uses in the character select intro (Clove's butterfly)
    public bool OnlyInCharacterSelect(string modelObjectPath)
    {
        var users = Users(modelObjectPath).Where(f => f.EndsWith(".uasset")).ToList();
        return users.Count > 0 && users.All(f => f.Contains("/CharSelect/", StringComparison.OrdinalIgnoreCase));
    }

    public record BoneAttachment(string MeshPath, string Bone, Placement Offset);

    // Models the game hangs on this model's bones (Jett's Blade Storm: a knife on each of the rig's bones Knife1-5),
    // from the ability object holding both: its component tree says which bone/socket each one sits on.
    public List<BoneAttachment>? BoneAttachmentsOf(string modelObjectPath)
    {
        var meshName = modelObjectPath[(modelObjectPath.LastIndexOf('.') + 1)..];
        List<BoneAttachment>? best = null;
        foreach (var file in Users(modelObjectPath).Where(f => f.EndsWith(".uasset") && !NotInGame.IsMatch(f)).Distinct().Take(12))
        {
            try
            {
                var exports = provider.LoadPackage(file).GetExports().ToList();
                // the components showing this model ("WeaponMesh1P", or "X_GEN_VARIABLE" for a blueprint-added one)
                var holders = exports.Where(e => (e.GetOrDefault<FPackageIndex>("SkeletalMesh") ?? e.GetOrDefault<FPackageIndex>("SkeletalMeshAsset"))?.Name == meshName)
                    .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (holders.Count == 0) continue;
                foreach (var name in holders.ToList().Where(n => n.EndsWith("_GEN_VARIABLE"))) holders.Add(name[..^"_GEN_VARIABLE".Length]);

                var attachedNodes = new List<UObject>();
                foreach (var node in exports.Where(e => e.ExportType == "SCS_Node"))
                {
                    // attached to a native component by name, or a child node of a blueprint component
                    if (holders.Contains(node.GetOrDefault<FName>("ParentComponentOrVariableName").Text)) attachedNodes.Add(node);
                    if (holders.Contains(node.GetOrDefault<FPackageIndex>("ComponentTemplate")?.Name ?? ""))
                        attachedNodes.AddRange(node.GetOrDefault("ChildNodes", Array.Empty<FPackageIndex>()).Select(c => c?.Load()).OfType<UObject>());
                }

                var found = new List<BoneAttachment>();
                foreach (var node in attachedNodes.Distinct())
                {
                    var bone = node.GetOrDefault<FName>("AttachToName").Text;
                    if (string.IsNullOrEmpty(bone) || bone == "None") continue;
                    if (node.GetOrDefault<FPackageIndex>("ComponentTemplate")?.Load() is not { } template) continue;
                    var mesh = template.GetOrDefault<FPackageIndex>("StaticMesh") ?? template.GetOrDefault<FPackageIndex>("SkeletalMesh");
                    if (mesh?.Load()?.GetPathName() is not { } meshPath) continue;
                    var offset = new FTransform(template.GetOrDefault("RelativeRotation", FRotator.ZeroRotator),
                        template.GetOrDefault("RelativeLocation", FVector.ZeroVector), template.GetOrDefault("RelativeScale3D", FVector.OneVector));
                    (bone, offset) = OnBone(modelObjectPath, bone, offset);
                    found.Add(new BoneAttachment(meshPath, bone, new Placement(offset.Translation, offset.Rotator(), offset.Scale3D)));
                }

                if (found.Count > (best?.Count ?? 0)) best = found;
            }
            catch (Exception)
            {
                // unreadable: try the next one
            }
        }

        return best?.OrderBy(a => a.Bone, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // a socket name -> its bone, with the socket's own offset added
    private (string Bone, FTransform Offset) OnBone(string meshObjectPath, string name, FTransform offset)
    {
        try
        {
            if (provider.LoadPackageObject(meshObjectPath) is USkeletalMesh mesh)
                foreach (var socket in mesh.Sockets.Select(s => s?.Load()).OfType<UObject>())
                {
                    if (!socket.GetOrDefault<FName>("SocketName").Text.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    var socketTransform = new FTransform(socket.GetOrDefault("RelativeRotation", FRotator.ZeroRotator),
                        socket.GetOrDefault("RelativeLocation", FVector.ZeroVector), socket.GetOrDefault("RelativeScale", FVector.OneVector));
                    return (socket.GetOrDefault<FName>("BoneName").Text, offset * socketTransform);
                }
        }
        catch (Exception)
        {
            // keep the name as a bone name
        }

        return (name, offset);
    }

    // The Abilities tab's list from the ability model files: names, one entry per model, parts placed, rigs with what
    // hangs on them; models drawn only by effects are left out.
    public static List<AbilityItem> BuildList(IFileProvider provider, IEnumerable<string> modelFiles, Func<string, (string Name, string Key)?> folderInfo)
    {
        var resolver = new AbilityResolver(provider);
        var attachments = new Dictionary<string, List<BoneAttachment>>(StringComparer.OrdinalIgnoreCase);
        bool IsRig(AbilityItem item)
        {
            if (!HasOnlyPlaceholderMaterials(provider, item.ObjectPath) || resolver.BoneAttachmentsOf(item.ObjectPath) is not { Count: > 0 } found) return false;
            attachments[item.ObjectPath] = found;
            return true;
        }

        var items = AbilityItem.Tidy(modelFiles.Select(file => new AbilityItem(file[..^".uasset".Length],
            resolver.AbilityOf(PackageOf(file)) ?? folderInfo(file))), IsRig);
        var result = new List<AbilityItem>();
        foreach (var item in items)
        {
            if (attachments.TryGetValue(item.ObjectPath, out var found)) item.MakeRig(found);
            else if (item.ModelPaths.All(p => LooksEffectOnly(provider, p))) continue;
            if (item.ModelPaths.Count > 1) item.Placements = resolver.PlacementOf(item.ModelPaths);
            if (resolver.OnlyInCharacterSelect(item.ObjectPath)) item.MarkCharacterSelectOnly();
            result.Add(item);
        }

        return result.OrderBy(a => a.SortKey, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // an entry's parts for Blender: each placed as in game, a rig's models on their bones
    public static List<ExportPart> ExportParts(IFileProvider provider, AbilityItem item)
    {
        var parts = new List<ExportPart>();
        int Add(string path) => provider.LoadPackageObject(path) switch
        {
            USkeletalMesh skeletalMesh => ExportHelpers.Mesh(skeletalMesh, parts),
            UStaticMesh staticMesh => ExportHelpers.SMesh(staticMesh, parts),
            _ => -1
        };

        foreach (var modelPath in item.ModelPaths)
        {
            var index = Add(modelPath);
            if (index >= 0 && item.Placements?.GetValueOrDefault(modelPath) is { } placement)
                parts[index].Placement = new PartPlacement(placement.Location, placement.Rotation, placement.Scale);
        }

        if (parts.Count > 0)
            foreach (var attachment in item.BoneAttachments ?? [])
            {
                var index = Add(attachment.MeshPath);
                if (index >= 0)
                    parts[index].AttachToBone = new PartBoneAttachment(attachment.Bone, attachment.Offset.Location, attachment.Offset.Rotation, attachment.Offset.Scale);
            }

        return parts;
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
