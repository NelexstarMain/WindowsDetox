using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>Content of the dialog that shows the one-time break-glass code.</summary>
internal sealed partial class BreakGlassCodeDialogViewModel(string code) : ObservableObject
{
    [ObservableProperty]
    private string _copyLabel = "Copy code";

    /// <summary>Gets the recovery code. Held only while the dialog is open.</summary>
    public string Code { get; } = code;

    [RelayCommand]
    private void Copy()
    {
        try
        {
            Clipboard.SetDataObject(Code, copy: true);
            CopyLabel = "Copied";
        }
        catch (COMException)
        {
            CopyLabel = "Clipboard busy, try again";
        }
    }
}

/// <summary>Content of the dialog that asks for the break-glass code.</summary>
internal sealed partial class BreakGlassPromptDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _code = string.Empty;
}
