using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoorManThrottle.App.Features.Terminal;

public partial class FunctionKeyConfigPage : ContentPage
{
    public FunctionKeyConfigPage(
        int keyNumber,
        string currentName,
        string? currentCommand,
        Action<string, string?> onSave)
    {
        InitializeComponent();

        BindingContext = new Vm(
            keyNumber,
            currentName,
            currentCommand,
            onSave,
            closeAsync: async () => await Navigation.PopModalAsync(animated: true));
    }

    private sealed partial class Vm : ObservableObject
    {
        private readonly int _keyNumber;
        private readonly Action<string, string?> _onSave;
        private readonly Func<Task> _closeAsync;

        public string TitleText => $"Configure Button #{_keyNumber}";

        [ObservableProperty] private string buttonName;
        [ObservableProperty] private string command;

        public Vm(
            int keyNumber,
            string currentName,
            string? currentCommand,
            Action<string, string?> onSave,
            Func<Task> closeAsync)
        {
            _keyNumber = keyNumber;
            _onSave = onSave;
            _closeAsync = closeAsync;

            buttonName = string.IsNullOrWhiteSpace(currentName) ? $"#{keyNumber}" : currentName;
            command = currentCommand ?? string.Empty;
        }

        [RelayCommand]
        private async Task CancelAsync()
        {
            await _closeAsync();
        }

        [RelayCommand]
        private async Task SaveAsync()
        {
            var name = (ButtonName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"F{_keyNumber}";

            var cmd = (Command ?? string.Empty).Trim();
            if (cmd.Length == 0)
                cmd = string.Empty;

            _onSave(name, cmd.Length == 0 ? null : cmd);

            await _closeAsync();
        }
    }
}