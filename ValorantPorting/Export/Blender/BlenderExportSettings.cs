namespace ValorantPorting.Export.Blender;

public class BlenderExportSettings : ExportSettingsBase
{
    public bool ReorientBones;
    public string? AnimationFilterKey; // stored on imported armatures so the app can follow the Blender selection
    public bool FirstPersonCamera; // a camera on the 1st person arms' "Camera" bone, like the in-game view
    public string? SceneRole;   // in a scene: "agent" or "gun"
    public string? SceneTarget; // in a scene, for the gun: the agent armature holding it ("agent:TP", "agent:FP", ...)
}