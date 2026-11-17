using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Input;

namespace NashmiKeys.App.ViewModels;

public sealed class HotkeyCardViewModel : INotifyPropertyChanged
{
    private readonly Action<HotkeyCardViewModel> _enabledChanged;
    private PadFamily _family;

    public HotkeyCardViewModel(Hotkey model, PadFamily family, Action<HotkeyCardViewModel> enabledChanged)
    {
        Model = model;
        _family = family;
        _enabledChanged = enabledChanged;
        RefreshChips();
    }

    public Hotkey Model { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? "Untitled hotkey" : Model.Name.Trim();

    public string ModeText => Model.Mode.ToString().ToUpperInvariant();

    public HotkeyMode Mode => Model.Mode;

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value)
            {
                return;
            }

            Model.Enabled = value;
            OnPropertyChanged();
            _enabledChanged(this);
        }
    }

    public ObservableCollection<string> InputChips { get; } = new();

    public ObservableCollection<string> OutputChips { get; } = new();

    public void RefreshChips(PadFamily family)
    {
        _family = family;
        RefreshChips();
    }

    private void RefreshChips()
    {
        InputChips.Clear();
        foreach (var input in Model.Inputs)
        {
            InputChips.Add(InputCatalog.Label(input, _family));
        }

        OutputChips.Clear();
        foreach (var output in Model.Outputs)
        {
            OutputChips.Add(output);
        }
    }

    public void RefreshEnabled()
    {
        OnPropertyChanged(nameof(Enabled));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
