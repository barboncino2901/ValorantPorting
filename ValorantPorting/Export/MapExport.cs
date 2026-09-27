using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using Newtonsoft.Json.Linq;
using ValorantPorting.AppUtils;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.Export;

// Exports a whole map (all sub-levels, meshes, materials, textures) as a USD scene using CUE4Parse's
// WorldExporter. Blender imports it with its built-in USD importer.
public static class MapExport
{
    private static readonly ExportOptions Options = new(
        meshFormat: EMeshFormat.USD,
        meshQuality: EMeshQuality.Highest,
        texturePlatform: ETexturePlatform.DesktopMobile,
        textureFormat: ETextureFormat.Png,
        exportMaterials: true,
        exportMorphTargets: false);

    public static async Task<(string Scene, string? Materials)?> ExportUsd(MapItem map)
    {
        UWorld world;
        try
        {
            world = await AppVM.CUE4ParseVM.Provider.LoadPackageObjectAsync<UWorld>(map.ObjectPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not load map {map.Name} ({map.ObjectPath}): {ex.Message}");
            return null;
        }

        var session = new ExportSession(IncludeAllSubLevels)
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2)
        };
        session.Add(world);

        var timer = Stopwatch.StartNew();
        var lastReported = -1;
        var progress = new Progress<ExportProgress>(p =>
        {
            var step = (int) (p.Percentage * 10); // log every 10%
            if (p.Total <= 0 || step <= lastReported) return;
            lastReported = step;
            AppLog.Information($"Exporting {map.Name}: {p.DisplayText}");
        });

        AppLog.Information($"Exporting map {map.Name}. The first export can take a few minutes...");
        var results = await session.RunAsync(App.AssetsFolder.FullName, Options, progress);

        var failed = results.Where(r => !r.Success).ToList();
        AppLog.Information($"Map {map.Name}: {results.Count - failed.Count} files exported, {failed.Count} failed, in {timer.Elapsed.TotalSeconds:0}s.");
        foreach (var failure in failed.Take(5))
            AppLog.Warning($"  failed: {failure.ObjectPath}: {failure.Error?.Message}");

        // the main scene is the .usda named after the map (sub-levels have their own names)
        var worldFile = results
            .Where(r => r.Success)
            .SelectMany(r => r.DiskFilePaths ?? [])
            .FirstOrDefault(p => p.EndsWith(".usda", StringComparison.OrdinalIgnoreCase) &&
                                 Path.GetFileNameWithoutExtension(p).Equals(map.Codename, StringComparison.OrdinalIgnoreCase));
        if (worldFile is null)
        {
            AppLog.Error($"Map {map.Name}: the main scene file was not written.");
            return null;
        }

        return (worldFile, WriteMaterialSummary(results, worldFile));
    }

    // Colors/scalars of every exported material (from CUE4Parse's per-material .json), so the Blender add-on can
    // apply Valorant-specific material fixes the generic USD materials don't cover (e.g. "AO color" on foliage).
    private static string? WriteMaterialSummary(IReadOnlyList<ExportResult> results, string worldFile)
    {
        var summary = new JObject();
        foreach (var file in results.Where(r => r.Success).SelectMany(r => r.DiskFilePaths ?? [])
                     .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var material = JObject.Parse(File.ReadAllText(file));
                summary[Path.GetFileNameWithoutExtension(file)] = new JObject
                {
                    ["Colors"] = material["Colors"],
                    ["Scalars"] = material["Scalars"],
                    ["BlendMode"] = material["BlendMode"]
                };
            }
            catch (Exception)
            {
                // not a material file or unreadable: skip
            }
        }

        var path = Path.ChangeExtension(worldFile, ".materials.json");
        File.WriteAllText(path, summary.ToString(Newtonsoft.Json.Formatting.None));
        return path;
    }

    // Valorant maps are split into streamed sub-levels (art, geometry, lighting, gameplay...). For this first
    // version include all of them, and log their names so we can learn which ones are worth importing.
    private static void IncludeAllSubLevels(StreamingLevelFilterArgs args, CancellationToken ct)
    {
        if (args.StreamingLevels.Count == 0) return;
        AppLog.Information($"{args.WorldName}: {args.StreamingLevels.Count} sub-levels: " +
                           string.Join(", ", args.StreamingLevels.Select(l => l.World.Name + (l.IsPersistent ? "" : "*"))) +
                           "  (* = streamed in the game)");
        foreach (var level in args.StreamingLevels) level.IsPersistent = true;
    }
}
