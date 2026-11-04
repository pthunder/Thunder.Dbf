//      _______
//     | .DBF  |   ProjectThunder :: Thunder.Dbf CRM sample
//     |_______|   The contact list. Open a dBASE or FoxPro table from Files, work on it here,
//      [=====]    and share it back: it's still the same table, in the same format.

namespace ThunderDbfMaui;

public partial class MainPage : ContentPage
{
    private const string CurrentTableKey = "current-table";
    private CrmTable? _table;
    private bool _dueOnly;

    public MainPage() => InitializeComponent();

    private static string TablesFolder => Path.Combine(FileSystem.AppDataDirectory, "tables");

    protected override void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _table ??= Preferences.Get(CurrentTableKey, null) is { } saved && File.Exists(saved)
                ? CrmTable.Open(saved)
                : CrmTable.CreateSample(Path.Combine(TablesFolder, "sample"));
            Refresh();
        }
        catch (Exception ex)
        {
            TableInfo.Text = $"Couldn't open the table: {ex.Message}";
        }
    }

    private void Refresh()
    {
        if (_table is null)
            return;
        var rows = _table.List(Search.Text, _dueOnly);
        Contacts.ItemsSource = rows;
        TableInfo.Text = $"{_table.Name} · {rows.Count:N0} shown{(_dueOnly ? " (follow-ups due)" : "")}";
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
        if (_table is not null)
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
            var folder = Path.Combine(TablesFolder, Path.GetFileNameWithoutExtension(dbf) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            foreach (var file in picked)
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            var table = CrmTable.Open(Path.Combine(folder, Path.GetFileName(dbf)));
            _table = table;
            Preferences.Set(CurrentTableKey, table.Path);
            Refresh();
            await DisplayAlertAsync("Opened", table.Describe(), "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Couldn't open it", ex.Message, "OK");
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
