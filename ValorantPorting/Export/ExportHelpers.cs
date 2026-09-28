using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using SkiaSharp;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Export;

public static class ExportHelpers
{
    public static readonly List<Task> Tasks = new();

    private static readonly ExportOptions ExportOptions = new(
        meshFormat: EMeshFormat.ActorX,
        meshQuality: EMeshQuality.Highest,
        texturePlatform: ETexturePlatform.DesktopMobile,
        textureFormat: ETextureFormat.Png,
        exportMaterials: false,
        exportMorphTargets: false);

    // Exports a mesh with CUE4Parse's session API and saves it as "<name>_LOD0.psk/pskx",
    // the file name the Blender add-on looks for.
    private static void ExportMeshLod0(ExporterBase exporter, UObject obj)
    {
        var results = new ExportSession { MaxDegreeOfParallelism = 1 }
            .Add(exporter)
            .RunAsync(App.AssetsFolder.FullName, ExportOptions)
            .GetAwaiter().GetResult();

        foreach (var result in results)
        {
            if (!result.Success)
            {
                AppLog.Warning($"Mesh export failed for {result.ObjectPath}: {result.Error?.Message}");
                continue;
            }

            foreach (var file in result.DiskFilePaths ?? [])
            {
                var ext = Path.GetExtension(file).TrimStart('.').ToLower();
                if (ext is not ("psk" or "pskx")) continue;
                if (Path.GetFileNameWithoutExtension(file) != obj.Name) continue; // only the main (LOD0) file

                var target = Path.Combine(Path.GetDirectoryName(file)!, obj.Name + "_LOD0." + ext);
                File.Move(file, target, overwrite: true);
            }
        }
    }

    private static bool Lod0Exists(UObject obj) =>
        File.Exists(GetExportPath(obj, "psk", "_LOD0")) || File.Exists(GetExportPath(obj, "pskx", "_LOD0"));
    
    public static void GunBuddy(List<ExportPart> exportParts, UObject asset)
    {
        if (asset.TryGetValue(out UObject charm, "Charm"))
        {
            if (charm is UStaticMesh staticMesh)
            {
                SMesh(staticMesh, exportParts);
            }
            else if (charm is USkeletalMesh skeletalMesh)
            {
                Mesh(skeletalMesh, exportParts);
            }
        }
    }
    
    public static void Character(List<ExportPart> exportParts, UObject asset, ECharacterModels models = ECharacterModels.All)
    {
        var components = new List<UObject>();
        //1P Mesh
        if (models.HasFlag(ECharacterModels.FirstPerson) && asset.TryGetValue(out UObject meshOverlay1P, "MeshOverlay1P"))
        {
            if (meshOverlay1P.Properties.Count < 2 && asset.TryGetValue(out UObject mesh1P, "Mesh1P"))
            {
                components.Add(mesh1P);
            }
            else
            {
                components.Add(meshOverlay1P);
            }
        }
        //3P Mesh
        if (models.HasFlag(ECharacterModels.ThirdPerson) && asset.TryGetValue(out UObject meshCosmetic3P, "MeshCosmetic3P"))
        {
            components.Add(meshCosmetic3P);
        }
        //CS Mesh
        if (models.HasFlag(ECharacterModels.CharacterSelect) && AppVM.MainVM.CurrentAsset.MainAsset.TryGetValue(out UObject characterSelectFxc, "CharacterSelectFXC"))
        {
            var exports = AppVM.CUE4ParseVM.Provider.LoadPackageObjects(characterSelectFxc.GetPathName().Substring(0, characterSelectFxc.GetPathName().LastIndexOf(".")));
            foreach (var export in exports)
            {
                if (export.ExportType == "SkeletalMeshComponent" && export.Name == "SkeletalMesh_GEN_VARIABLE") components.Add(export);
            }
        }
        
        foreach (var component in components)
        {
            if (component.TryGetValue(out USkeletalMesh skelMesh, "SkeletalMesh"))
            {
                var index = Mesh(skelMesh, exportParts);
                if (index >= 0 && skelMesh.TryGetValue(out UMaterialInstanceConstant[] materialOverrides, "MaterialOverrides"))
                    OverrideMaterials(materialOverrides, exportParts[index]);
            }
        }
    }
    
    
    // level: index into the skin's Levels (its model/materials as of that upgrade); null = fully upgraded
    public static void Weapon(List<ExportPart> exportParts, UObject style, int? level = null)
    {
        var mainAsset = AppVM.MainVM.CurrentAsset.MainAsset;
        var levelTuple = GetHighestLevel(level);
        // chromas only exist on the fully upgraded skin
        if (level is { } chosen && mainAsset.GetOrDefault("Levels", Array.Empty<UBlueprintGeneratedClass>()).Length > chosen + 1)
            style = null;
        var handledStyleGun = style != null ? HandleStyle(style) : null;

        //gun mesh (if not in the skin's levels, the base gun mesh)
        var gunIndex = Mesh(levelTuple.Item1 ?? GetBaseWeapon(), exportParts);
        if (gunIndex < 0) return;
        var gun = exportParts[gunIndex];
        OverrideMaterials(levelTuple.Item2, gun);
        // the chroma's 1P list matches the 1P gun's material slots; older chromas only have the 3P list
        if (handledStyleGun != null)
            OverrideMaterials(FirstMaterialList(handledStyleGun, "MaterialOverrides", "1p MaterialOverrides", "1p Material Overrides", "3p Material Overrides"), gun, style: true);

        //mag mesh, with the level's magazine materials
        var magIndex = SMesh(levelTuple.Item4 ?? GetMagMesh(), exportParts);
        if (magIndex >= 0)
        {
            var mag = exportParts[magIndex];
            OverrideMaterials(levelTuple.Item3.Length > 0 ? levelTuple.Item3 : levelTuple.Item2, mag);
            if (handledStyleGun != null)
            {
                var magOverrides = FirstMaterialList(handledStyleGun, "1pMagazine MaterialOverrides", "3pMagazineMaterial Overrides");
                if (magOverrides.Length == 0)
                    magOverrides = FirstMaterialList(handledStyleGun, "MaterialOverrides", "3p Material Overrides");
                OverrideMaterials(magOverrides, mag, style: true);
            }

            //attach mag to gun body
            gun.Attatchments.Add(new ExportAttatchment { BoneName = "Magazine_Main", AttatchmentName = mag.MeshName });
        }

        //attachment (scope & silencer)
        var usedSockets = new HashSet<string>();
        mainAsset.TryGetValue(out UScriptMap attachmentOverrides, "AttachmentOverrides");
        var forcedAttachments = GetForcedAttachments(attachmentOverrides);
        if (forcedAttachments.Count > 0)
        {
            // Guns with default attachments (e.g. the Warden's ACOG scope): the skin's AttachmentOverrides replace
            // those specific attachments; entries for other attachments (like the generic ACOG) don't apply.
            foreach (var (socket, mesh, materials) in forcedAttachments)
                AddAttachment(socket, mesh, materials);
        }
        else if (attachmentOverrides is not null)
        {
            var attachmentTuple = GetWeaponAttatchments(attachmentOverrides);
            for (var i = 0; i < attachmentTuple.Item2.Length; i++)
            {
                // GetWeaponAttatchments always returns fixed-size-2 arrays even when a weapon only
                // has one real attachment (e.g. Operator-class scopes with no silencer slot) - the
                // unfilled slot has a null mesh and must be skipped entirely, or exportParts.Last()
                // silently stays pointed at the previous real attachment and gets its materials
                // overwritten by this phantom entry's fallback (confirmed via diagnostic log: the
                // sniper scope's correct materials were immediately overwritten by the main body's).
                if (attachmentTuple.Item2[i] == null) continue;

                AddAttachment(attachmentTuple.Item1[i], attachmentTuple.Item2[i], attachmentTuple.Item3[i]);
            }
        }

        void AddAttachment(string socket, USkeletalMesh mesh, UMaterialInstanceConstant[]? materials)
        {
            var index = Mesh(mesh, exportParts);
            if (index < 0) return;
            var part = exportParts[index];
            gun.Attatchments.Add(new ExportAttatchment { BoneName = socket, AttatchmentName = part.MeshName });
            OverrideMaterials(materials, part);
            if (style == null) return;

            // the chroma's version of this attachment (scope or silencer), else materials named like the attachment's own
            // in the chroma's lists (some chromas list the attachment's materials together with the gun's)
            var (chromaMaterials, thirdPerson) = GetStyleAttachmentMaterials(style, socket);
            if (chromaMaterials != null && !thirdPerson)
                OverrideMaterials(chromaMaterials, part, style: true);
            else if (chromaMaterials != null)
                OverrideMaterialsByName(chromaMaterials, part);
            else if (handledStyleGun != null)
                OverrideMaterialsByName(ChromaMaterialLists.SelectMany(name => handledStyleGun.GetOrDefault(name, Array.Empty<UMaterialInstanceConstant>())), part);
        }
    }

    private static readonly string[] ChromaMaterialLists = ["MaterialOverrides", "1p MaterialOverrides", "1p Material Overrides", "3p Material Overrides"];

    // Property names of an attachment's 1P mesh, 1P materials and 3P materials, by the gun socket it goes on
    // (the 3P list belongs to the 3P model; its slots don't always line up with the 1P model's)
    private static readonly Dictionary<string, (string Mesh, string[] Materials, string ThirdPersonMaterials)> AttachmentProperties = new()
    {
        ["Reflex"] = ("1pReflexMesh", ["MaterialOverrides", "1p MaterialOverrides"], "3pMaterialOverrides"),
        ["Barrel"] = ("1p Mesh", ["1p MaterialOverrides", "1p Material Overrides"], "3p MaterialOverrides")
    };

    private static UMaterialInstanceConstant[] FirstMaterialList(UObject? source, params string[] names)
    {
        if (source == null) return [];
        foreach (var name in names)
            if (GetInherited<UMaterialInstanceConstant[]>(source, name) is { Length: > 0 } list) return list;
        return [];
    }

    // The chroma's replacement for the attachment on this socket: its AttachmentOverrides entry whose attachment carries
    // a mesh for that socket (a scope has a reflex mesh, a silencer a barrel mesh).
    private static (UMaterialInstanceConstant[]? Materials, bool ThirdPerson) GetStyleAttachmentMaterials(UObject style, string socket)
    {
        if (style is not UBlueprintGeneratedClass styleClass || !AttachmentProperties.TryGetValue(socket, out var properties)) return (null, false);
        var styleDefaults = styleClass.ClassDefaultObject.Load();
        if (styleDefaults == null) return (null, false);

        var sources = new List<UObject>();
        if (styleDefaults.TryGetValue(out UBlueprintGeneratedClass chroma, "EquippableSkinChroma") && chroma.ClassDefaultObject.Load() is { } chromaDefaults)
            sources.Add(chromaDefaults);
        sources.Add(styleDefaults);

        foreach (var source in sources)
        {
            if (!source.TryGetValue(out UScriptMap overrides, "AttachmentOverrides")) continue;
            foreach (var entry in overrides.Properties)
            {
                if (entry.Value?.GenericValue is not FSoftObjectPath path || !path.TryLoad(out UBlueprintGeneratedClass attachmentClass)) continue;
                var attachment = attachmentClass.ClassDefaultObject.Load();
                if (attachment == null || GetInherited<USkeletalMesh>(attachment, properties.Mesh) is null) continue;
                if (FirstMaterialList(attachment, properties.Materials) is { Length: > 0 } firstPerson) return (firstPerson, false);
                if (FirstMaterialList(attachment, properties.ThirdPersonMaterials) is { Length: > 0 } thirdPerson) return (thirdPerson, true);
            }
        }
        return (null, false);
    }

    // Attachments a gun always carries (its primary asset's "ForcedAttachments", e.g. the Warden's ACOG scope),
    // each replaced by the skin's version when its AttachmentOverrides map that attachment to another one.
    // Attachment primary asset -> "Attachment" blueprint -> mesh + materials (possibly inherited from a parent).
    private static List<(string Socket, USkeletalMesh Mesh, UMaterialInstanceConstant[]? Materials)> GetForcedAttachments(
        UScriptMap? overrides)
    {
        var found = new List<(string, USkeletalMesh, UMaterialInstanceConstant[]?)>();
        try
        {
            var mainAsset = AppVM.MainVM.CurrentAsset.MainAsset;
            if (!mainAsset.TryGetValue(out UBlueprintGeneratedClass gunPrimary, "Equippable")) return found;
            var gunDefaults = gunPrimary.ClassDefaultObject.Load();
            if (gunDefaults is null || !gunDefaults.TryGetValue(out FSoftObjectPath[] forced, "ForcedAttachments")) return found;

            // skin overrides: replaced attachment class -> replacement class
            var replacements = new Dictionary<string, FSoftObjectPath>(StringComparer.OrdinalIgnoreCase);
            if (overrides is not null)
            foreach (var entry in overrides.Properties)
            {
                if (entry.Value?.GenericValue is FSoftObjectPath replacement && SoftPathText(entry.Key.GenericValue) is { } key)
                    replacements[key] = replacement;
            }

            foreach (var forcedPath in forced)
            {
                if (!forcedPath.TryLoad(out UBlueprintGeneratedClass attachmentPrimary)) continue;
                var primaryDefaults = attachmentPrimary.ClassDefaultObject.Load();
                if (primaryDefaults is null || !primaryDefaults.TryGetValue(out FSoftObjectPath attachmentPath, "Attachment")) continue;

                var chosen = replacements.TryGetValue(attachmentPath.AssetPathName.Text, out var skinVersion) ? skinVersion : attachmentPath;
                if (!chosen.TryLoad(out UBlueprintGeneratedClass attachmentClass)) continue;
                var attachment = attachmentClass.ClassDefaultObject.Load();
                if (attachment is null) continue;

                // scopes, then silencers
                foreach (var (socket, properties) in AttachmentProperties)
                {
                    if (GetInherited<USkeletalMesh>(attachment, properties.Mesh) is not { } mesh) continue;
                    found.Add((socket, mesh, FirstMaterialList(attachment, properties.Materials)));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not read the gun's default attachments: {ex.Message}");
        }

        return found;
    }

    private static string? SoftPathText(object? value) => value switch
    {
        FSoftObjectPath path => path.AssetPathName.Text,
        null => null,
        _ => value.ToString()
    };

    // A property from a blueprint default object or, if it isn't set there, from the blueprint it's based on
    // (e.g. a skin's scope that only changes materials keeps its parent scope's mesh).
    private static T? GetInherited<T>(UObject obj, string name)
    {
        for (var current = obj; current is not null; current = current.Template?.Load())
        {
            if (current.TryGetValue(out T value, name) && value is not null) return value;
        }

        return default;
    }
    
    public static UObject? HandleStyle(UObject style)
    {
        var bpGnCast = style as UBlueprintGeneratedClass;
        var styleClassDefaultObject = bpGnCast.ClassDefaultObject.Load();
        if (styleClassDefaultObject.TryGetValue(out UBlueprintGeneratedClass attachmentOverrides, "EquippableSkinChroma")) 
            return attachmentOverrides.ClassDefaultObject.Load();
        return null;
    }

    public static Tuple<USkeletalMesh, UMaterialInstanceConstant[], UMaterialInstanceConstant[], UStaticMesh>
        GetHighestLevel(int? upToLevel = null)
    {
        var mainAsset = AppVM.MainVM.CurrentAsset.MainAsset;
        // 
        USkeletalMesh highestMeshUsed = null;
        UMaterialInstanceConstant[] highestWeapMaterialUsed = { };
        UMaterialInstanceConstant[] highestMagMaterialUsed = { };
        UStaticMesh highestMagMeshUsed = null;
        //
        mainAsset.TryGetValue(out UBlueprintGeneratedClass[] levels, "Levels");
        for (var i = 0; i < levels.Length && (upToLevel is null || i <= upToLevel); i++)
        {
            var activeO = levels[i];
            var cdoLo = activeO.ClassDefaultObject.Load();
            UBlueprintGeneratedClass localUob;
            if (cdoLo.TryGetValue(out localUob, "SkinAttachment"))
            {
                var ready = localUob.ClassDefaultObject.Load();
                ready.TryGetValue(out USkeletalMesh cosmeticMesh, "Weapon 1P Cosmetic");
                ready.TryGetValue(out USkeletalMesh actualWeaponMesh, "Weapon 1P");
                ready.TryGetValue(out USkeletalMesh newMesh, "NewMesh");

                // Default true = safe fallback (matches old baseline) if we can't read bone data at all.
                bool cosmeticLooksLikeAWeapon = true;

                // Real gun-mechanism bone names confirmed present on every tested weapon mesh
                // (Cyberknight, Revolver Lv2 Edge, Daedalus) and absent on the Aquarium2 fish mesh.
                // "Magazine_Main" (the original guess) never actually exists on any of these meshes -
                // that's why both attempt 2a and 2b failed no matter which way the default was flipped.
                string[] weaponIndicatorBones = { "Muzzle", "Mag_Holder", "Hammer", "Gun_Buddy", "Magazine_Extra" };

                if (cosmeticMesh != null)
                {
                    var logDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    var logPath = System.IO.Path.Combine(logDir, "bonecheck_diagnostics.log");
                    try
                    {
                        System.IO.Directory.CreateDirectory(logDir);
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"--- {DateTime.Now:HH:mm:ss} ---");
                        sb.AppendLine($"Mesh Name: {cosmeticMesh.Name}");

                        var refSkeleton = cosmeticMesh.ReferenceSkeleton;
                        if (refSkeleton == null)
                        {
                            sb.AppendLine("ReferenceSkeleton is null.");
                        }
                        else
                        {
                            var rsType = refSkeleton.GetType();
                            sb.AppendLine($"ReferenceSkeleton CLR Type: {rsType.FullName}");
                            sb.AppendLine("All members:");

                            object boneArray = null;
                            string boneArrayMemberName = null;

                            foreach (var prop in rsType.GetProperties())
                            {
                                object val = null;
                                try { val = prop.GetValue(refSkeleton); } catch { }
                                var countStr = "";
                                if (val is System.Collections.IEnumerable en && !(val is string))
                                {
                                    var count = 0;
                                    foreach (var _ in en) count++;
                                    countStr = $" (Count={count})";
                                    if (boneArray == null && count > 0) { boneArray = val; boneArrayMemberName = prop.Name; }
                                }
                                sb.AppendLine($"  [property] {prop.Name} : {prop.PropertyType.Name}{countStr}");
                            }
                            foreach (var field in rsType.GetFields())
                            {
                                object val = null;
                                try { val = field.GetValue(refSkeleton); } catch { }
                                var countStr = "";
                                if (val is System.Collections.IEnumerable en && !(val is string))
                                {
                                    var count = 0;
                                    foreach (var _ in en) count++;
                                    countStr = $" (Count={count})";
                                    if (boneArray == null && count > 0) { boneArray = val; boneArrayMemberName = field.Name; }
                                }
                                sb.AppendLine($"  [field] {field.Name} : {field.FieldType.Name}{countStr}");
                            }

                            if (boneArray is System.Collections.IEnumerable boneList)
                            {
                                sb.AppendLine($"Dumping bone names from '{boneArrayMemberName}':");
                                var boneNames = new System.Collections.Generic.List<string>();
                                foreach (var boneInfo in boneList)
                                {
                                    if (boneInfo == null) continue;
                                    var boneInfoType = boneInfo.GetType();
                                    object nameMember = (object)boneInfoType.GetField("Name") ?? (object)boneInfoType.GetProperty("Name");
                                    object nameValue = nameMember switch
                                    {
                                        System.Reflection.FieldInfo f => f.GetValue(boneInfo),
                                        System.Reflection.PropertyInfo p => p.GetValue(boneInfo),
                                        _ => null
                                    };
                                    string boneNameText = null;
                                    if (nameValue != null)
                                    {
                                        var textProp = nameValue.GetType().GetProperty("Text");
                                        boneNameText = textProp != null ? textProp.GetValue(nameValue)?.ToString() : nameValue.ToString();
                                    }
                                    boneNames.Add(boneNameText ?? "(null)");
                                }
                                sb.AppendLine("  " + string.Join(", ", boneNames));
                                cosmeticLooksLikeAWeapon = boneNames.Any(n => weaponIndicatorBones.Contains(n, StringComparer.OrdinalIgnoreCase));
                                sb.AppendLine($"  Matched a weapon-indicator bone: {cosmeticLooksLikeAWeapon}");
                            }
                            else
                            {
                                sb.AppendLine("Could not find any non-empty enumerable member to use as a bone array.");
                            }
                        }

                        if (WriteDiagnosticLogs) System.IO.File.AppendAllText(logPath, sb.ToString() + "\n");
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            if (!WriteDiagnosticLogs) throw;
                            System.IO.Directory.CreateDirectory(logDir);
                            System.IO.File.AppendAllText(logPath, $"--- {DateTime.Now:HH:mm:ss} --- EXCEPTION: {ex}\n\n");
                        }
                        catch { }
                    }
                }

                USkeletalMesh localMeshUsed = cosmeticLooksLikeAWeapon
                    ? (cosmeticMesh ?? actualWeaponMesh ?? newMesh)
                    : (actualWeaponMesh ?? newMesh ?? cosmeticMesh);
                if (localMeshUsed != null) highestMeshUsed = localMeshUsed;
                ready.TryGetValue(out UMaterialInstanceConstant[] localMatUsed, "1p MaterialOverrides");
                if (localMatUsed != null) highestWeapMaterialUsed = localMatUsed;
                ready.TryGetValue(out UMaterialInstanceConstant[] magOverrides, "1pMagazine MaterialOverrides");
                if (magOverrides != null) highestMagMaterialUsed = magOverrides;
                ready.TryGetValue(out UStaticMesh magMesh, "Magazine 1P", "SpeedLoader");
                if (magMesh != null) highestMagMeshUsed = magMesh;
            }
        }

        return Tuple.Create(highestMeshUsed, highestWeapMaterialUsed, highestMagMaterialUsed, highestMagMeshUsed);
    }
    
    
    
    public static USkeletalMesh GetBaseWeapon()
    {
        var mainAsset = AppVM.MainVM.CurrentAsset.MainAsset;
        if (mainAsset.TryGetValue(out UBlueprintGeneratedClass equippable, "Equippable"))
        {
            var classDefaultObject = equippable.ClassDefaultObject.Load();
            if (classDefaultObject.TryGetValue(out UBlueprintGeneratedClass localEqippable, "Equippable"))
            {
                var loadedEquippable = localEqippable.ClassDefaultObject.Load();
                if (loadedEquippable.TryGetValue(out UObject objectReturn, "Mesh1P") &&
                    objectReturn.TryGetValue(out USkeletalMesh skeletalMesh, "SkeletalMesh"))
                    return skeletalMesh;
            }
        }
        return null;
    }

    // for some reason the mag mash is not in the properties here so gotta load all exports
    public static UStaticMesh GetMagMesh()
    {
        var mainAsset = AppVM.MainVM.CurrentAsset.MainAsset;
        if (mainAsset.TryGetValue(out UBlueprintGeneratedClass equippable, "Equippable"))
        {
            var classDefaultObject = equippable.ClassDefaultObject.Load();
            if (classDefaultObject.TryGetValue(out UObject localEquippable, "Equippable"))
            {
                var mainObjectExports = AppVM.CUE4ParseVM.Provider.LoadPackageObjects(localEquippable.GetPathName().Substring(0, localEquippable.GetPathName().LastIndexOf(".")));
                foreach (var export in mainObjectExports)
                    if (export.Name.Contains("Magazine_1P") && export.TryGetValue(out UStaticMesh staticMesh, "StaticMesh"))
                        return staticMesh;
            }
        }
        return null;
    }
    
    public static Tuple<string[], USkeletalMesh[], UMaterialInstanceConstant[][], string[]> GetWeaponAttatchments(
        UScriptMap scriptMap)
    {
        // initializer for return tuple stuff
        var fullSockets = new string[2];
        var fullOverrideMaterials = new UMaterialInstanceConstant[2][];
        var meshes = new USkeletalMesh[2];
        var paramNames = new string[2];
        //  loop 
        foreach (var scriptMapVariable in scriptMap.Properties)
        {
            var scriptMapValue = (FSoftObjectPath)scriptMapVariable.Value.GenericValue;
            var valueLoaded = (UBlueprintGeneratedClass)scriptMapValue.Load();
            var classDefaultObject = valueLoaded.ClassDefaultObject.Load();

            var i = 0;
            foreach (var (socket, properties) in AttachmentProperties)
            {
                var localMesh = GetInherited<USkeletalMesh>(classDefaultObject, properties.Mesh);
                if (localMesh != null)
                {
                    fullSockets[i] = socket;
                    meshes[i] = localMesh;
                    fullOverrideMaterials[i] = FirstMaterialList(classDefaultObject, properties.Materials);
                    paramNames[i] = properties.Materials[0];
                }
                i++;
            }
        }

        return Tuple.Create(fullSockets, meshes, fullOverrideMaterials, paramNames);
    }

    // Debug dump from the bone-check fix (logs/bonecheck_diagnostics.log). Off, so the file doesn't grow on every
    // gun export; set to true when debugging it.
    private const bool WriteDiagnosticLogs = false;

    public static int Mesh(USkeletalMesh? skeletalMesh, List<ExportPart> exportParts)
    {
        if (skeletalMesh is null) return -1;
        if (!skeletalMesh.TryConvert(out var convertedMesh, EMeshQuality.Highest)) return -1;
        using var convertedMeshScope = convertedMesh;
        if (convertedMesh.LODs.Count <= 0) return -1;

        var exportPart = new ExportPart();
        exportPart.MeshPath = skeletalMesh.GetPathName();
        exportPart.MeshName = skeletalMesh.Name + "_LOD0.ao";
        Save(skeletalMesh);

        AddSectionMaterials(exportPart, convertedMesh.LODs[0].Sections, section => convertedMesh.GetMaterial(section)?.Material);
        if (skeletalMesh.LODModels?.FirstOrDefault()?.Sections is { } sourceSections && sourceSections.Length == exportPart.SectionMaterialSlots.Count)
            for (var i = 0; i < sourceSections.Length; i++)
                if (sourceSections[i].bDisabled) exportPart.DisabledSections.Add(i);
        exportParts.Add(exportPart);
        return exportParts.Count - 1;
    }

    public static int SMesh(UStaticMesh? staticMesh, List<ExportPart> exportParts)
    {
        if (staticMesh is null) return -1;
        if (!staticMesh.TryConvert(out var convertedMesh, EMeshQuality.Highest)) return -1;
        using var convertedMeshScope = convertedMesh;
        if (convertedMesh.LODs.Count <= 0) return -1;
        var exportPart = new ExportPart();
        exportPart.MeshPath = staticMesh.GetPathName();
        exportPart.MeshName = staticMesh.Name + "_LOD0.mo";
        Save(staticMesh);

        AddSectionMaterials(exportPart, convertedMesh.LODs[0].Sections, section => convertedMesh.GetMaterial(section)?.Material);
        exportParts.Add(exportPart);
        return exportParts.Count - 1;
    }

    // The .psk has one material per mesh section, in section order; Unreal's override lists are indexed by material slot.
    private static void AddSectionMaterials(ExportPart exportPart, MeshSectionDto[] sections, Func<MeshSectionDto, FPackageIndex?> materialOf)
    {
        for (var idx = 0; idx < sections.Length; idx++)
        {
            var section = sections[idx];
            exportPart.SectionMaterialSlots.Add(section.MaterialIndex);
            var sectionMaterial = materialOf(section);
            if (sectionMaterial is null || !sectionMaterial.TryLoad(out var material)) continue;

            var exportMaterial = new ExportMaterial { MaterialName = material.Name, SlotIndex = idx };
            if (material is UMaterialInterface materialInterface) DescribeMaterial(materialInterface, exportMaterial);
            exportPart.Materials.Add(exportMaterial);
        }
    }

    // Applies an override list to the mesh sections. Lists are indexed by Unreal material slot, but some chroma lists
    // follow another order (e.g. the 3P model's), so a section whose current material has a namesake in the list
    // ("Crystal_MI" -> "Crystal_v1_MI") gets that one; the rest go by slot index.
    public static void OverrideMaterials(UMaterialInstanceConstant?[]? overrides, ExportPart part, bool style = false)
    {
        if (overrides is null || overrides.Length == 0) return;
        var target = style ? part.StyleMaterials : part.OverrideMaterials;
        var sectionCount = Math.Max(part.SectionMaterialSlots.Count, overrides.Length);
        var assigned = new Dictionary<int, UMaterialInstanceConstant>();

        var byStem = new Dictionary<string, UMaterialInstanceConstant>();
        foreach (var material in overrides)
            if (material != null) byStem.TryAdd(MaterialStem(material.Name), material);
        for (var section = 0; section < sectionCount; section++)
            if (CurrentMaterialName(part, section) is { } current && byStem.TryGetValue(MaterialStem(current), out var namesake))
                assigned[section] = namesake;

        var matchedByName = assigned.Values.ToHashSet();
        for (var slot = 0; slot < overrides.Length; slot++)
        {
            if (overrides[slot] is not { } material || matchedByName.Contains(material)) continue;
            var sections = part.SectionMaterialSlots.Count == 0
                ? [slot]
                : Enumerable.Range(0, part.SectionMaterialSlots.Count).Where(s => part.SectionMaterialSlots[s] == slot);
            foreach (var section in sections) assigned.TryAdd(section, material);
        }

        foreach (var (section, material) in assigned.OrderBy(pair => pair.Key))
        {
            try
            {
                var swapPath = material.GetOrDefault<FSoftObjectPath>("MaterialToSwap").AssetPathName.PlainText;
                var exportMaterial = new ExportMaterial
                {
                    MaterialName = material.Name,
                    SlotIndex = section,
                    MaterialNameToSwap = string.IsNullOrEmpty(swapPath) ? string.Empty : swapPath.SubstringAfterLast(".")
                };
                DescribeMaterial(material, exportMaterial);
                target.Add(exportMaterial);
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Skipped a material override due to an error: {ex.Message}");
            }
        }
    }

    // The material a section ends up with so far (chroma over level over the mesh's own)
    private static string? CurrentMaterialName(ExportPart part, int section) =>
        (part.StyleMaterials.LastOrDefault(m => m.SlotIndex == section)
         ?? part.OverrideMaterials.LastOrDefault(m => m.SlotIndex == section)
         ?? part.Materials.FirstOrDefault(m => m.SlotIndex == section))?.MaterialName;

    // Applies materials to the sections whose own material has the same name apart from a chroma/3P suffix
    // (e.g. "Scope_MI" <- "Scope_v2_MI"), for lists whose slot order doesn't match the mesh.
    public static void OverrideMaterialsByName(IEnumerable<UMaterialInstanceConstant?> candidates, ExportPart part)
    {
        var byName = new Dictionary<string, UMaterialInstanceConstant>();
        foreach (var candidate in candidates)
            if (candidate != null) byName.TryAdd(MaterialStem(candidate.Name), candidate);

        foreach (var material in part.Materials)
        {
            if (!byName.TryGetValue(MaterialStem(material.MaterialName), out var replacement)) continue;
            if (replacement.Name == material.MaterialName) continue;
            var exportMaterial = new ExportMaterial { MaterialName = replacement.Name, SlotIndex = material.SlotIndex };
            DescribeMaterial(replacement, exportMaterial);
            part.StyleMaterials.Add(exportMaterial);
        }
    }

    // A material's name without chroma/3P/1P markers and the "_MI" ending: "Scope_Water_v2_3p_MI" -> "scope_water"
    internal static string MaterialStem(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"_(v\d+|3p|1p|mi|mat|inst)(?=_|$)", "");

    // Parameters plus how the material is drawn (its shader and blend mode), so Blender can skip or fade effect-only
    // materials (translucent liquids, additive glows) instead of drawing them as solid surfaces.
    private static void DescribeMaterial(UMaterialInterface material, ExportMaterial exportMaterial)
    {
        if (material is UMaterialInstanceConstant materialInstance)
        {
            var (textures, scalars, vectors) = MaterialParameters(materialInstance);
            exportMaterial.Textures = textures;
            exportMaterial.Scalars = scalars;
            exportMaterial.Vectors = vectors;
            if (materialInstance.Parent != null) exportMaterial.ParentName = materialInstance.Parent.Name;
        }

        string? blendOverride = null;
        UMaterialInterface current = material;
        for (var depth = 0; depth < 16 && current is UMaterialInstance instance; depth++)
        {
            if (blendOverride == null && instance.TryGetValue(out FStructFallback overrides, "BasePropertyOverrides") &&
                overrides.GetOrDefault<bool>("bOverride_BlendMode"))
                blendOverride = overrides.GetOrDefault<FName>("BlendMode").Text;
            if (instance.Parent == null || !instance.Parent.TryLoad(out var parent) || parent is not UMaterialInterface next) break;
            current = next;
        }

        exportMaterial.BaseMaterial = current.Name;
        var blend = blendOverride ?? (current as UMaterial)?.BlendMode.ToString();
        if (blend != null) exportMaterial.BlendMode = blend.SubstringAfterLast("BLEND_");
    }

    public static (List<TextureParameter>, List<ScalarParameter>, List<VectorParameter>) MaterialParameters(UMaterialInstanceConstant materialInstance)
    {
        var textures = new List<TextureParameter>();
        var scalars = new List<ScalarParameter>();
        var vectors = new List<VectorParameter>();
        
        ParentMaterialInstanceParameters(materialInstance, textures, scalars, vectors);
        return (textures, scalars, vectors);
    }

    public static void ParentMaterialInstanceParameters(UMaterialInstanceConstant materialInstance, List<TextureParameter> textures, List<ScalarParameter> scalars, List<VectorParameter> vectors)
    {
        if (materialInstance == null) return;
        foreach (var parameter in materialInstance.TextureParameterValues)
        {
            if (parameter == null) continue;
            if (!parameter.ParameterValue.TryLoad(out UTexture2D texture)) continue;
            if (textures.Any(x => x.Name.Equals(parameter.Name))) continue;
            textures.Add(new TextureParameter(parameter.ParameterInfo.Name.PlainText, texture.GetPathName()));
            Save(texture);
        }

        foreach (var parameter in materialInstance.ScalarParameterValues)
        {
            if (parameter == null) continue;
            if (scalars.Any(x => x.Name.Equals(parameter.Name))) continue;
            scalars.Add(new ScalarParameter(parameter.ParameterInfo.Name.PlainText, parameter.ParameterValue));
        }

        foreach (var parameter in materialInstance.VectorParameterValues)
        {
            if (parameter == null) continue;
            if (parameter.ParameterValue is null) continue;
            if (vectors.Any(x => x.Name.Equals(parameter.Name))) continue;
            vectors.Add(new VectorParameter(parameter.ParameterInfo.Name.PlainText, parameter.ParameterValue.Value));
        }

        if (materialInstance.Parent != null && materialInstance.Parent.TryLoad(out var parentExport) && parentExport is UMaterialInstanceConstant parent)
            ParentMaterialInstanceParameters(parent, textures, scalars, vectors);
    }

    internal static bool WriteFiles = true; // off for dev checks that only look at the export data

    public static void Save(UObject obj)
    {
        if (!WriteFiles) return;
        Tasks.Add(Task.Run(() =>
        {
            try
            {
                switch (obj)
                {
                    case USkeletalMesh skeletalMesh:
                    {
                        if (Lod0Exists(obj)) return;
                        ExportMeshLod0(new SkinnedAssetExporter(skeletalMesh), obj);
                        break;
                    }

                    case UStaticMesh staticMesh:
                    {
                        if (Lod0Exists(obj)) return;
                        ExportMeshLod0(new StaticMeshExporter(staticMesh), obj);
                        break;
                    }
                    case UTexture2D texture:
                    {
                        var path = GetExportPath(obj, "png");
                        if (File.Exists(path)) return;
                        Directory.CreateDirectory(path.Replace('\\', '/').SubstringBeforeLast('/'));

                        var decoded = texture.Decode(ETexturePlatform.DesktopMobile);
                        if (decoded is null) return;
                        var data = decoded.Encode(ETextureFormat.Png, false, out _);
                        File.WriteAllBytes(path, data);
                        break;
                    }
                }
            }
            catch (IOException)
            {
            }
        }));
    }

    private static string GetExportPath(UObject obj, string ext, string extra = "")
    {
        var path = obj.Owner.Name;
        path = path.SubstringBeforeLast('.');
        if (path.StartsWith("/")) path = path[1..];
        if (path.StartsWith("Game/")) path = "ShooterGame/Content/" + path["Game/".Length..]; // matches CUE4Parse's on-disk layout

        var finalPath = Path.Combine(App.AssetsFolder.FullName, path) + $"{extra}.{ext.ToLower()}";
        return finalPath;
    }
}
