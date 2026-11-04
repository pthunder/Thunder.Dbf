//      _______
//     | .DBF  |   ProjectThunder :: Thunder.Dbf CRM sample
//     |_______|   The contact list. Open a dBASE or FoxPro table from Files, work on it here,
//      [=====]    and share it back: it's still the same table, in the same format.

using Thunder.Dbf;

namespace ThunderDbfMaui;

public partial class MainPage : ContentPage
{
    private const string CurrentTableKey = "current-table";
    private CrmTable? _table;
    private bool _dueOnly;

    public MainPage() => InitializeComponent();

    private static string TablesFolder => Path.Combine(FileSystem.AppDataDirectory, "tables");

    // iOS can move the app's data folder on an update or reinstall, so the current table is kept
    // relative to it: an absolute path would quietly fall back to the sample.
    private static string? SavedTable => Preferences.Get(CurrentTableKey, null);

    /// <summary>A message safe to show: Thunder.Dbf's own never hold paths; anything else might.</summary>
    internal static string Safe(Exception ex) => ex switch
    {
        DbfException or ReadOnlyTableException => ex.Message,
        FormatException => $"A value isn't in the right form: {ex.Message}",
        _ => "Something went wrong reading or writing the table files.",
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_table is not null)
        {
            Refresh();
            return;
        }
        var gone = false;
        try
        {
            if (SavedTable is { } saved && Path.Combine(TablesFolder, saved) is var path && File.Exists(path))
                _table = CrmTable.Open(path);
            else
            {
                gone = SavedTable is not null; // opened before, but its files are no longer there
                Preferences.Remove(CurrentTableKey);
                _table = CrmTable.CreateSample(Path.Combine(TablesFolder, "sample"));
            }
            Refresh();
        }
        catch (Exception ex)
        {
            TableInfo.Text = $"Couldn't open the table: {Safe(ex)}";
        }
        if (gone)
            await DisplayAlertAsync("Table not found", "The table you last opened isn't on this phone any more, so this is the sample. Open it again from Files.", "OK");
    }

    private void Refresh()
    {
        if (_table is null)
            return;
        var rows = _table.List(Search.Text, _dueOnly);
        Contacts.ItemsSource = rows;
        TableInfo.Text = $"{_table.Name} · {rows.Count:N0} shown{(_dueOnly ? " (follow-ups due)" : "")}{(_table.ReadOnly ? " · read-only" : "")}";
        AllButton.Opacity = _dueOnly ? 0.6 : 1;
        DueButton.Opacity = _dueOnly ? 1 : 0.6;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => Refresh();

    private void OnAllClicked(object? sender, EventArgs e)
    {
        _dueOnly = false;
        Refresh();
    }

    private void OnDueClicked(object? sender, EventArgs e)
    {
        _dueOnly = true;
        Refresh();
    }

    private async void OnContactSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ContactRow row || _table is null)
            return;
        Contacts.SelectedItem = null;
        await Navigation.PushAsync(new ContactPage(_table, row.Index));
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        if (_table is null)
            return;
        if (_table.ReadOnly)
        {
            await DisplayAlertAsync("Read-only", $"This table's {_table.MissingIndex} index wasn't opened with it. Open it again and pick that file too.", "OK");
            return;
        }
        await Navigation.PushAsync(new ContactPage(_table, index: null));
    }

    // The feature: pick a .dbf (with its .fpt/.dbt memo and .cdx/.mdx index) from Files. The app
    // copies them in and works on that table as it is: its own fields, format and indexes.
    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await TablePicker.PickAsync();
            if (picked.Count == 0)
                return;
            if (picked.FirstOrDefault(f => f.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase)) is not { } dbf)
            {
                await DisplayAlertAsync("No table", "Pick the .dbf file too (and its memo and index files, if it has them).", "OK");
                return;
            }
            var folder = Path.Combine(TablesFolder, Path.GetFileNameWithoutExtension(dbf) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            CrmTable table;
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var file in picked)
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                table = CrmTable.Open(Path.Combine(folder, Path.GetFileName(dbf)));
            }
            catch
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true); // don't leave half a table behind
                throw;
            }
            _table = table;
            Preferences.Set(CurrentTableKey, Path.GetRelativePath(TablesFolder, table.Path));
            _dueOnly = false; // a search or filter left over from the last table would hide this one
            Search.Text = "";
            Refresh();
            await DisplayAlertAsync("Opened", table.Describe(), "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Couldn't open it", Safe(ex), "OK");
        }
    }

    // Shares the table with its memo and index files, unchanged in format: AirDrop it to a PC
    // and it opens in Visual FoxPro, dBASE or Excel.
    private async void OnShareClicked(object? sender, EventArgs e)
    {
        if (_table is null)
            return;
        await Share.Default.RequestAsync(new ShareMultipleFilesRequest
        {
            Title = _table.Name,
            Files = [.. _table.Companions().Select(f => new ShareFile(f))],
        });
    }
}
