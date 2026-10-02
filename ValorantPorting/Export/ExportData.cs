using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Engine;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.Export;

public class ExportData
{
    public string Name;
    public List<ExportPart> Parts = new();
    public string Type;

    public static async Task<UObject> CreateUiData(UBlueprintGeneratedClass asset)
    {
        return await Task.Run(() => asset.ClassDefaultObject.Load());
    }

    
    // mainAsset: the item's main asset when it isn't the selected one (a scene's agent or gun)
    public static async Task<ExportData> Create(UObject asset, EAssetType assetType, UObject style, ExportChoices? choices = null,
        UObject? mainAsset = null)
    {
        choices ??= new ExportChoices();
        await ExportHelpers.ExportLock.WaitAsync();
        ExportHelpers.MainAssetOverride = mainAsset;
        try
        {
            return await CreateData(asset, assetType, style, choices);
        }
        finally
        {
            ExportHelpers.MainAssetOverride = null;
            ExportHelpers.ExportLock.Release();
        }
    }

    private static async Task<ExportData> CreateData(UObject asset, EAssetType assetType, UObject style, ExportChoices choices)
    {
        var data = new ExportData();
        data.Name = asset.GetOrDefault("DeveloperName", new FText("Unnamed")).Text;
        data.Type = assetType.ToString();
        await Task.Run(() =>
        {
            switch (assetType)
            {
                case EAssetType.Character:
                    ExportHelpers.Character(data.Parts, asset, choices.Models);
                    break;
                case EAssetType.Weapon:
                    ExportHelpers.Weapon(data.Parts, style, choices.WeaponLevel);
                    break;
                case EAssetType.GunBuddy:
                    ExportHelpers.GunBuddy(data.Parts, asset);
                    break;
            }
        });

        await Task.WhenAll(ExportHelpers.Tasks);
        ExportHelpers.Tasks.Clear();
        return data;
    }
}
