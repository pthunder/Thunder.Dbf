//      _______
//     | .DBF  |   ProjectThunder :: Thunder.Dbf CRM sample
//     | .FPT  |   Picks a table and its memo and index files from Files, and hands back
//     |_.CDX__|   local copies the app can open together.

namespace ThunderDbfMaui;

public static class TablePicker
{
#if IOS
    private static UIKit.UIDocumentPickerViewController? _open;

    // MAUI's PickMultipleAsync comes back empty on iOS 27 for files with no registered type,
    // which is every .dbf, so this asks iOS's own picker for copies of the chosen files.
    public static Task<IReadOnlyList<string>> PickAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<string>>();
        var picker = new UIKit.UIDocumentPickerViewController([UniformTypeIdentifiers.UTTypes.Item], asCopy: true)
        {
            AllowsMultipleSelection = true,
        };
        picker.DidPickDocumentAtUrls += (_, e) => done.TrySetResult([.. e.Urls.Select(u => u.Path!)]);
        picker.WasCancelled += (_, _) => done.TrySetResult([]);
        _open = picker;
        Platform.GetCurrentUIViewController()!.PresentViewController(picker, true, null);
        return done.Task.ContinueWith(t =>
        {
            _open = null;
            return t.Result;
        }, TaskScheduler.Default);
    }
#else
    public static async Task<IReadOnlyList<string>> PickAsync()
    {
        var picked = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Pick a .dbf and its .fpt, .dbt, .cdx or .mdx" });
        return [.. (picked ?? []).Where(f => f is not null).Select(f => f!.FullPath)];
    }
#endif
}
