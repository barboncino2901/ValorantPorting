using System;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.i18N;
using SkiaSharp;

namespace ValorantPorting.Views.Controls;

public partial class AssetSelectorItem : IExportableAsset
{
    private const int MARGIN = 2;
    private const int THUMBNAIL_SIZE = 128; // tiles are 64px, the details panel 88px; 128 stays sharp on high-DPI screens

    public AssetSelectorItem(UObject asset, UObject UIasset, UObject MainDataAsset, UTexture2D previewTexture,
        bool isRandomSelector = false)
    {
        InitializeComponent();
        DataContext = this;
        UIAsset = UIasset;
        Asset = asset;
        MainAsset = MainDataAsset;
        DisplayName = UIAsset.GetOrDefault("DisplayName", new FText("")).Text;
        Description = UIAsset.GetOrDefault("Description", new FText("")).Text;
        ID = UIAsset.Name;

        TooltipName = $"{DisplayName} ({ID})";
        IsRandom = isRandomSelector;

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

    public UObject UIAsset { get; set; }
    public UObject MainAsset { get; set; }
    public BitmapImage FullSource { get; set; }
    public UObject Asset { get; set; }
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