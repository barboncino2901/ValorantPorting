using System.Collections.Generic;
using System.Windows.Media.Imaging;
using CUE4Parse.UE4.Assets.Exports;
using ValorantPorting.ViewModels;

namespace ValorantPorting.Views.Controls;

// An agent or gun skin of a saved scene: loaded from the game files by its path when exported (the tiles of that tab
// don't have to be loaded), the same way a tile loads its own.
public class SavedSceneItem : IExportableAsset
{
    private readonly string objectPath;
    private AssetHandlerData.ResolvedAsset? resolved;

    public SavedSceneItem(string objectPath, string packagePath, EAssetType type, string displayName)
    {
        this.objectPath = objectPath;
        PackagePath = packagePath;
        aType = type;
        DisplayName = displayName;
        TooltipName = displayName;
        ID = displayName;
    }

    public string ObjectPath => objectPath;

    private AssetHandlerData.ResolvedAsset? Resolved =>
        resolved ??= AppVM.AssetHandlerVM?.Handlers.GetValueOrDefault(aType)?.ResolveItem(objectPath);

    public string PackagePath { get; }
    public UObject UIAsset { get => Resolved?.UiAsset ?? new UObject(); set { } }
    public UObject MainAsset { get => Resolved?.MainAsset ?? new UObject(); set { } }
    public BitmapImage FullSource { get; set; } = null!;
    public UObject Asset { get => Resolved?.Asset ?? new UObject(); set { } }
    public bool IsRandom { get; set; }
    public string DisplayName { get; set; }
    public EAssetType aType { get; set; }
    public string Description { get; set; } = "";
    public string TooltipName { get; set; }
    public string ID { get; set; }
}
