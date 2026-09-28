using System;
using System.Collections.Generic;
using System.IO;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace ValorantPorting.Export;

// Texture parameters of map materials that the material export resolves to Riot's layered-material placeholders
// ("Default_Base", "Default_NM", ...) although a parent material instance sets a real texture, e.g. Summit's
// "Ground_M0_LargeStoneBrick_NoBlend_MI_V5", which only inherits its stone textures from its parent.
public static class MaterialTextures
{
    public static bool IsPlaceholder(string texturePath) =>
        texturePath.Contains("/LayeredEnv/_Textures_Base/", StringComparison.OrdinalIgnoreCase);

    // Texture parameters set along a material instance's parent chain; the nearest instance's value wins.
    public static Dictionary<string, UTexture2D> Inherited(UMaterialInterface material)
    {
        var found = new Dictionary<string, UTexture2D>(StringComparer.OrdinalIgnoreCase);
        UMaterialInterface? current = material;
        for (var depth = 0; depth < 16 && current is UMaterialInstance instance; depth++)
        {
            if (instance is UMaterialInstanceConstant constant)
                foreach (var parameter in constant.TextureParameterValues)
                {
                    var name = parameter.ParameterInfo.Name.Text;
                    if (found.ContainsKey(name) || !parameter.ParameterValue.TryLoad(out UTexture2D texture)) continue;
                    if (!IsPlaceholder(texture.GetPathName())) found[name] = texture;
                }

            current = instance.Parent != null && instance.Parent.TryLoad(out var parent) ? parent as UMaterialInterface : null;
        }

        return found;
    }

    // The texture as a .png under the assets folder (written if the map export didn't write it); returns its path
    // relative to the assets folder, or null if it can't be decoded.
    public static string? EnsurePng(UTexture2D texture, string assetsRoot)
    {
        var package = texture.GetPathName();
        package = package.Contains('.') ? package[..package.LastIndexOf('.')] : package;
        if (package.StartsWith("/Game/")) package = "ShooterGame/Content/" + package["/Game/".Length..];
        var relative = package.TrimStart('/') + ".png";
        var file = Path.Combine(assetsRoot, relative);
        if (File.Exists(file)) return relative;

        try
        {
            var decoded = texture.Decode(ETexturePlatform.DesktopMobile);
            if (decoded is null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, decoded.Encode(ETextureFormat.Png, false, out _));
            return relative;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
