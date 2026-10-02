using System;
using System.Windows;
using ValorantPorting.AppUtils;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.i18N;
using SkiaSharp;
using ValorantPorting.ViewModels;

namespace ValorantPorting.Views.Controls;

public partial class AssetSelectorItem : IExportableAsset, ILibraryItem
{
    private const int MARGIN = 2;
    private const int THUMBNAIL_SIZE = 128; // tiles are 64px, the details panel 88px; 128 stays sharp on high-DPI screens

    private readonly Func<AssetHandlerData.ResolvedAsset?> resolver;
    private AssetHandlerData.ResolvedAsset? resolved;

    // uiAsset is only read here (name/description); the tile keeps its path and resolves the game objects when needed.
    public AssetSelectorItem(string packagePath, UObject uiAsset, UTexture2D previewTexture, bool isRandomSelector,
        Func<AssetHandlerData.ResolvedAsset?> resolver)
    {
        InitializeComponent();
        DataContext = this;
        this.resolver = resolver;
        PackagePath = packagePath;
        DisplayName = uiAsset.GetOrDefault("DisplayName", new FText("")).Text;
        Description = uiAsset.GetOrDefault("Description", new FText("")).Text;
        ID = uiAsset.Name;

        TooltipName = $"{DisplayName} ({ID})";
        IsRandom = isRandomSelector;
        IsFavorite = UserLibrary.IsFavorite(LibraryId);

        using var iconBitmap = previewTexture.Decode()?.ToSkBitmap();
        if (iconBitmap is null) return;

        FullSource = CreateThumbnail(iconBitmap);
        DisplayImage.Source = FullSource;
        //BeginAnimation(OpacityProperty, AppearAnimation);
    }

    // Downscales the icon and returns a frozen WPF image that owns no Skia memory.
    private static BitmapImage CreateThumbnail(SKBitmap source)
    {
        var scale = Math.Min(1.0, (double) THUMBNAIL_SIZE / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int) Math.Round(source.Width * scale));
        var height = Math.Max(1, (int) Math.Round(source.Height * scale));

        using var resized = scale < 1.0
            ? source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), SKFilterQuality.High)
            : source.Copy();
        using var png = resized.Encode(SKEncodedImageFormat.Png, 100);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = png.AsStream();
        image.EndInit();
        image.Freeze();
        return image;
    }

    private AssetHandlerData.ResolvedAsset? Resolved => resolved ??= resolver();

    // Frees the game objects again (called when another tile gets selected).
    public void ReleaseGameData() => resolved = null;

    public string PackagePath { get; }
    public string ObjectPath { get; init; } = ""; // the asset it's loaded from (kept in saved scenes)

    public static readonly DependencyProperty IsFavoriteProperty =
        DependencyProperty.Register(nameof(IsFavorite), typeof(bool), typeof(AssetSelectorItem));

    public bool IsFavorite
    {
        get => (bool) GetValue(IsFavoriteProperty);
        set => SetValue(IsFavoriteProperty, value);
    }

    public string LibraryId => "asset:" + PackagePath;
    public int RecentRank => UserLibrary.RecentRank(LibraryId);
    public UObject UIAsset { get => Resolved?.UiAsset ?? new UObject(); set { } }
    public UObject MainAsset { get => Resolved?.MainAsset ?? new UObject(); set { } }
    public BitmapImage FullSource { get; set; }
    public UObject Asset { get => Resolved?.Asset ?? new UObject(); set { } }
    public bool IsRandom { get; set; }
    public string DisplayName { get; set; }
    public EAssetType aType { get; set; }
    public string Description { get; set; }
    public string TooltipName { get; set; }
    public string ID { get; set; }

    public bool Match(string filter, bool useRegex = false)
    {
        if (useRegex) return Regex.IsMatch(DisplayName, filter) || Regex.IsMatch(ID, filter);

        return DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               ID.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}