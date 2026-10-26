using System.Windows.Input;
using SwitchMonitor.Core.Color;

namespace SwitchMonitor.App.ViewModels;

/// <summary>
/// Advanced tab: color temperature and red/green/blue made by the graphics card (gamma ramp). Works on any display,
/// including the laptop screen, and gives the LG the Medium tint its menu cannot reach over DDC.
/// </summary>
public sealed class SoftwareColorViewModel : ObservableObject
{
    /// <summary>Warmest white point offered (lower ones are refused by Windows without a registry change).</summary>
    public const int Warmest = 3400;
    public const int Coolest = 9300;

    private readonly MonitorViewModel owner;
    private readonly ColorStore store;
    private readonly Action<string, bool> report;
    private double kelvin;
    private double red, green, blue;
    private bool loading;
    private CancellationTokenSource? pending;

    public SoftwareColorViewModel(MonitorViewModel owner, ColorStore store, Action<string, bool> report)
    {
        this.owner = owner;
        this.store = store;
        this.report = report;
        Load(store.Get(owner.Key));
        PresetCommand = new RelayCommand(p => SetPreset(Convert.ToInt32(p)));
        ResetCommand = new RelayCommand(() => SetPreset(SoftwareColor.NeutralKelvin));
    }

    public string GdiName => owner.Device.Identity.GdiName;
    public double MinKelvin => Warmest;
    public double MaxKelvin => Coolest;

    public double Kelvin
    {
        get => kelvin;
        set { if (Set(ref kelvin, Math.Round(value / 100) * 100)) { Raise(nameof(KelvinText)); Changed(); } }
    }

    public string KelvinText => $"{Kelvin:0} K" + (Kelvin < SoftwareColor.NeutralKelvin ? " · warm" : Kelvin > SoftwareColor.NeutralKelvin ? " · cool" : " · neutral");

    public double Red { get => red; set { if (Set(ref red, Math.Round(value))) { Raise(nameof(RedText)); Changed(); } } }
    public double Green { get => green; set { if (Set(ref green, Math.Round(value))) { Raise(nameof(GreenText)); Changed(); } } }
    public double Blue { get => blue; set { if (Set(ref blue, Math.Round(value))) { Raise(nameof(BlueText)); Changed(); } } }
    public string RedText => $"{Red:0}%";
    public string GreenText => $"{Green:0}%";
    public string BlueText => $"{Blue:0}%";

    public ICommand PresetCommand { get; }
    public ICommand ResetCommand { get; }

    public SoftwareColorSetting Current => new((int)Kelvin, Red / 100, Green / 100, Blue / 100);

    private void Load(SoftwareColorSetting setting)
    {
        loading = true;
        try
        {
            Kelvin = setting.Kelvin;
            Red = setting.Red * 100;
            Green = setting.Green * 100;
            Blue = setting.Blue * 100;
        }
        finally { loading = false; }
    }

    private void SetPreset(int presetKelvin)
    {
        loading = true;
        try
        {
            Kelvin = presetKelvin;
            Red = Green = Blue = 100;
        }
        finally { loading = false; }
        _ = ApplyAsync(immediately: true);
    }

    private void Changed()
    {
        if (loading) return;
        _ = ApplyAsync(immediately: false);
    }

    private async Task ApplyAsync(bool immediately)
    {
        pending?.Cancel();
        var cts = pending = new CancellationTokenSource();
        if (!immediately)
        {
            try { await Task.Delay(60, cts.Token); }
            catch (TaskCanceledException) { return; }
        }
        var setting = Current;
        bool ok = await Task.Run(() => Apply(setting));
        if (cts.IsCancellationRequested) return;
        if (ok)
        {
            store.Set(owner.Key, setting);
            owner.SettingChangedByHand();
        }
        else report($"{owner.Name}: Windows refused this color (too far from neutral). Try a milder one.", false);
    }

    private bool Apply(SoftwareColorSetting setting) =>
        !string.IsNullOrEmpty(GdiName) &&
        (setting.IsNeutral ? SoftwareColor.Reset(GdiName) : SoftwareColor.Apply(GdiName, setting.Kelvin, setting.Red, setting.Green, setting.Blue));

    /// <summary>Sets the saved color again (start, resume from sleep, display reconnected). Neutral displays are left alone.</summary>
    public void Reapply()
    {
        var setting = Current;
        if (!setting.IsNeutral) Apply(setting);
    }

    /// <summary>Back to neutral when the app closes, so no tint stays behind without the app.</summary>
    public void Release()
    {
        if (!Current.IsNeutral && !string.IsNullOrEmpty(GdiName)) SoftwareColor.Reset(GdiName);
    }
}
