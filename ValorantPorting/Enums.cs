using System.ComponentModel;

namespace ValorantPorting;

public enum EInstallType
{
    [Description("Local")] Local,

    [Description("Valorant [Live]")] Live
}

public enum ERichPresenceAccess
{
    [Description("Always")] Always,

    [Description("Never")] Never
}

public enum EAssetType
{
    [Description("Characters")] Character,
    [Description("Weapons")] Weapon,
    [Description("Gunbuddies")] GunBuddy,
    [Description("Animations")] Animation, // must stay in tab order (tab index == enum value)
    [Description("Mesh")] Mesh
}

public enum ETreeItemType
{
    Folder,
    Asset
}