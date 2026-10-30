using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.App;

/// <summary>
/// Order of the settings on every list: picture mode first (it decides what the others can do), brightness next, then
/// the rest from the most to the least used. Unknown controls keep their order after these.
/// </summary>
public static class SettingOrder
{
    public static int Rank(FeatureDefinition feature)
    {
        string name = feature.Name.ToLowerInvariant();
        if (feature.Code == VcpCode.DisplayMode || name.Contains("picture mode")) return 0;
        return feature.Code switch
        {
            VcpCode.Brightness => 1,
            VcpCode.Contrast => 2,
            VcpCode.ColorPreset => 3,
            0x87 => 4,                                       // sharpness
            0xF7 when name.Contains("response") => 5,
            0xF8 when name.Contains("freesync") => 6,
            VcpCode.AudioVolume => 7,
            VcpCode.AudioMute => 8,
            _ => 20
        };
    }
}
