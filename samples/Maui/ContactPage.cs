//      _______
//     |  ID   |   ProjectThunder :: Thunder.Dbf CRM sample
//     | NAME  |   One contact, with an editor for every field the table has, whatever
//     |_______|   table it is: the app's sample or a FoxPro table from the 1990s.

using Thunder.Dbf;

namespace ThunderDbfMaui;

public sealed class ContactPage : ContentPage
{
    private readonly CrmTable _table;
    private readonly int? _index;
    private readonly Dictionary<string, (DbfField Field, InputView Input)> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _error = new() { TextColor = Color.FromArgb("#C0392B"), IsVisible = false };

    public ContactPage(CrmTable table, int? index)
    {
        _table = table;
        _index = index;
        Title = index is null ? "New contact" : "Contact";

        var fields = index is { } existing ? table.Read(existing) : [.. table.Fields().Select(f => (f, ""))];
        var form = new VerticalStackLayout { Padding = new Thickness(16, 12), Spacing = 6 };
        foreach (var (field, text) in fields)
        {
            form.Add(new Label { Text = Caption(field), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
            InputView input = field.Type == 'M'
                ? new Editor { Text = text, AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80 }
                : new Entry
                {
                    Text = text,
                    Placeholder = Hint(field),
                    // Names and codes aren't words: autocorrect turned "Blauer See" into "Blazer See".
                    IsSpellCheckEnabled = false,
                    IsTextPredictionEnabled = false,
                    Keyboard = field.Type is 'N' or 'F' or 'I' or 'Y' or 'B' ? Keyboard.Numeric : field.Name.Contains("MAIL", StringComparison.OrdinalIgnoreCase) ? Keyboard.Email : field.Name.Contains("PHONE", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("TEL", StringComparison.OrdinalIgnoreCase) ? Keyboard.Telephone : Keyboard.Default,
                };
            input.AutomationId = $"Field_{field.Name}";
            if (field.Type is 'C' && input is Entry entry)
                entry.MaxLength = field.Length;
            _inputs[field.Name] = (field, input);
            form.Add(input);
        }
        form.Add(_error);

        var save = new Button { Text = "Save", AutomationId = "SaveContact", Margin = new Thickness(0, 12, 0, 0) };
        save.Clicked += OnSave;
        form.Add(save);
        if (index is not null)
        {
            var delete = new Button { Text = "Delete", AutomationId = "DeleteContact", BackgroundColor = Color.FromArgb("#C0392B") };
            delete.Clicked += OnDelete;
            form.Add(delete);
        }
        Content = new ScrollView { Content = form };
    }

    private static string Caption(DbfField field) => field.Type switch
    {
        'M' => $"{field.Name} (memo)",
        'D' => $"{field.Name} (date)",
        'L' => $"{field.Name} (Y/N)",
        _ => field.Name,
    };

    private static string Hint(DbfField field) => field.Type switch
    {
        'D' => "yyyy-mm-dd",
        'T' => "yyyy-mm-dd hh:mm",
        'L' => "Y or N",
        _ => "",
    };

    private async void OnSave(object? sender, EventArgs e)
    {
        try
        {
            var texts = _inputs.ToDictionary(p => p.Key, p => p.Value.Input.Text ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var (name, (field, _)) in _inputs)
                DbfFieldMap.FromText(field, texts[name]); // check every value before writing any
            _table.Save(_index, texts);
            await Navigation.PopAsync();
        }
        catch (FormatException ex)
        {
            ShowError($"A value isn't in the right form: {ex.Message}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (_index is not { } index || !await DisplayAlertAsync("Delete contact?", "It's marked deleted, as dBASE does; PACK removes it for good.", "Delete", "Cancel"))
            return;
        _table.Delete(index);
        await Navigation.PopAsync();
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }
}
