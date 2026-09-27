namespace ValorantPorting.Export.Blender;

public class BlenderExportSettings : ExportSettingsBase
{
    public bool ReorientBones;
    public string? AnimationFilterKey; // stored on imported armatures so the app can follow the Blender selection
}