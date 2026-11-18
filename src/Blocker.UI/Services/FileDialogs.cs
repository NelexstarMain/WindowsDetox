using Microsoft.Win32;

namespace Blocker.UI.Services;

/// <summary>Native Win32 file pickers.</summary>
internal sealed class FileDialogs : IFileDialogs
{
    /// <inheritdoc />
    public string? PickSavePath(string suggestedFileName, string filter)
    {
        SaveFileDialog dialog = new() { FileName = suggestedFileName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickOpenPath(string filter)
    {
        OpenFileDialog dialog = new() { Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
