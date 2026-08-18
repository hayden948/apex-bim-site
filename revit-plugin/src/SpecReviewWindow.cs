using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Apex.BimStudio.Commands;

namespace Apex.BimStudio;

/// <summary>
/// Round 4: the review-and-override dialog. Shows every extracted value with
/// the extraction's confidence, flags the ones worth a second look, lets the
/// modeler correct a field, and refuses to save anything the FamilySpec
/// validator rejects — with the validator's named-field message shown as-is.
///
/// Modal (ShowDialog) and Revit-free: no API call happens while it is open.
/// The owning command builds AFTER the dialog closes, on the API thread.
/// Pure code-built WPF (no XAML compiler in the toolchain).
/// </summary>
internal sealed class SpecReviewWindow : Window
{
    private readonly SpecReviewModel _model;
    private readonly List<Editor> _editors = new();
    private readonly TextBox _status;

    private sealed class Editor
    {
        public SpecReviewModel.Field Field = null!;
        public TextBox Box = null!;
        public TextBox? UnitsBox;
        public string UnitsOriginal = "";
        // "geometry.width" edits its unit at ".unit"; "parameters[i].value" at ".units".
        public string UnitsKey => Field.Key.EndsWith(".value", StringComparison.Ordinal)
            ? Field.Key.Substring(0, Field.Key.Length - ".value".Length) + ".units"
            : Field.Key + ".unit";
    }

    /// <summary>True once the corrected spec has been written to disk.</summary>
    public bool Saved { get; private set; }
    /// <summary>True when the modeler chose "Save and Build".</summary>
    public bool BuildRequested { get; private set; }

    public SpecReviewWindow(SpecReviewModel model, IntPtr ownerHandle)
    {
        _model = model;
        Title = "Apex — review extracted values";
        Width = 640;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        List<SpecReviewModel.Field> fields = model.BuildFields();
        int flagged = fields.Count(f => f.LowConfidence);

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = $"{System.IO.Path.GetFileName(model.SourcePath)} — {fields.Count} extracted values." +
                (flagged > 0
                    ? $" {flagged} marked CHECK: the extraction was less than " +
                      $"{(int)(SpecReviewModel.LowConfidenceThreshold * 100)} percent sure of them — " +
                      "compare each against the submittal before building."
                    : " No value fell below the confidence threshold, but a spot check never hurts."),
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        int rowIx = 0;
        AddHeaderRow(grid, ref rowIx);
        foreach (SpecReviewModel.Field f in fields)
            AddFieldRow(grid, f, ref rowIx);

        var scroll = new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var warnBlock = new Expander
        {
            Header = $"Notes from the extraction ({model.ExtractionWarnings.Count})",
            IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBox
            {
                Text = model.ExtractionWarnings.Count == 0
                    ? "The extraction reported no notes for this drawing."
                    : string.Join(Environment.NewLine + Environment.NewLine, model.ExtractionWarnings),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 140,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
        Grid.SetRow(warnBlock, 2);
        root.Children.Add(warnBlock);

        _status = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = "Correct any value, then Check values — or Save and Build when it looks right.",
        };
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(MakeButton("Check values", OnCheck));
        buttons.Children.Add(MakeButton("Save", OnSave));
        buttons.Children.Add(MakeButton("Save and Build", OnSaveAndBuild, isDefault: true));
        buttons.Children.Add(MakeButton("Cancel", (_, _) => { DialogResult = false; Close(); }));
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;

        try
        {
            if (ownerHandle != IntPtr.Zero)
                new System.Windows.Interop.WindowInteropHelper(this) { Owner = ownerHandle };
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Review window could not attach to the Revit window: " + ex.Message);
        }
    }

    private static void AddHeaderRow(Grid grid, ref int rowIx)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] titles = { "Field", "Value", "Units", "Confidence" };
        for (int c = 0; c < titles.Length; c++)
        {
            var t = new TextBlock
            {
                Text = titles[c],
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 6, 4),
            };
            Grid.SetRow(t, rowIx);
            Grid.SetColumn(t, c);
            grid.Children.Add(t);
        }
        rowIx++;
    }

    private void AddFieldRow(Grid grid, SpecReviewModel.Field f, ref int rowIx)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = f.Label,
            Margin = new Thickness(2, 3, 6, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = f.Label,
        };
        Grid.SetRow(label, rowIx);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        if (f.Editable)
        {
            var box = new TextBox { Text = f.Value, Margin = new Thickness(0, 1, 6, 1) };
            if (f.LowConfidence) box.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xC4));
            Grid.SetRow(box, rowIx);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            // Units are a real parser-miss class (mm read as in) — editable
            // alongside the value; the geometry unit list is validated on save.
            var unitsBox = new TextBox { Text = f.Units ?? "", Margin = new Thickness(0, 1, 6, 1) };
            Grid.SetRow(unitsBox, rowIx);
            Grid.SetColumn(unitsBox, 2);
            grid.Children.Add(unitsBox);

            _editors.Add(new Editor
            {
                Field = f, Box = box, UnitsBox = unitsBox, UnitsOriginal = f.Units ?? "",
            });
        }
        else
        {
            var val = new TextBlock
            {
                Text = f.Value,
                Margin = new Thickness(2, 3, 6, 3),
                Foreground = Brushes.DimGray,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = f.Value,
            };
            Grid.SetRow(val, rowIx);
            Grid.SetColumn(val, 1);
            grid.Children.Add(val);

            var units = new TextBlock { Text = f.Units ?? "", Margin = new Thickness(2, 3, 6, 3) };
            Grid.SetRow(units, rowIx);
            Grid.SetColumn(units, 2);
            grid.Children.Add(units);
        }

        var conf = new TextBlock { Margin = new Thickness(2, 3, 2, 3) };
        if (f.Confidence.HasValue)
        {
            int pct = (int)Math.Round(f.Confidence.Value * 100);
            if (f.LowConfidence)
            {
                conf.Text = pct + "% — CHECK";
                conf.Foreground = Brushes.Firebrick;
                conf.FontWeight = FontWeights.Bold;
            }
            else
            {
                conf.Text = pct + "%";
            }
        }
        else
        {
            conf.Text = "—";
            conf.Foreground = Brushes.DimGray;
        }
        Grid.SetRow(conf, rowIx);
        Grid.SetColumn(conf, 3);
        grid.Children.Add(conf);

        rowIx++;
    }

    private static Button MakeButton(string text, RoutedEventHandler onClick, bool isDefault = false)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 4, 14, 4),
            IsDefault = isDefault,
        };
        b.Click += onClick;
        return b;
    }

    /// <summary>Push every changed textbox into the model; returns per-field messages.</summary>
    private List<string> ApplyEdits()
    {
        var problems = new List<string>();
        foreach (Editor e in _editors)
        {
            string text = e.Box.Text.Trim();
            if (text != e.Field.Value)
            {
                string? err = _model.TrySet(e.Field.Key, text);
                if (err != null) problems.Add(err);
                else e.Field.Value = text;
            }
            if (e.UnitsBox != null)
            {
                string units = e.UnitsBox.Text.Trim();
                if (units != e.UnitsOriginal)
                {
                    string? err = _model.TrySet(e.UnitsKey, units);
                    if (err != null) problems.Add(err);
                    else e.UnitsOriginal = units;
                }
            }
        }
        return problems;
    }

    private void OnCheck(object sender, RoutedEventArgs e)
    {
        List<string> problems = ApplyEdits();
        PredValidator.Result check = _model.Validate();
        problems.AddRange(check.Errors);
        List<string> consistency = _model.ConsistencyWarnings();
        _status.Text = problems.Count == 0
            ? ("No problems found. The spec is valid and ready to build."
              + (check.Warnings.Count > 0
                  ? $" ({check.Warnings.Count} note(s) — see the run log.)" : "")
              + (consistency.Count > 0
                  ? Environment.NewLine + "Worth a look:" + Environment.NewLine + "• " +
                    string.Join(Environment.NewLine + "• ", consistency)
                  : ""))
            : "Fix these before building:" + Environment.NewLine + "• " +
              string.Join(Environment.NewLine + "• ", problems.Take(10));
    }

    private bool SaveNow()
    {
        List<string> problems = ApplyEdits();
        if (problems.Count > 0)
        {
            _status.Text = "Not saved:" + Environment.NewLine + "• " +
                string.Join(Environment.NewLine + "• ", problems.Take(10));
            return false;
        }
        string? err = _model.TrySave();
        if (err != null)
        {
            _status.Text = err;
            return false;
        }
        Saved = true;
        List<string> consistency = _model.ConsistencyWarnings();
        _status.Text = $"Saved. The original extraction is kept as " +
            $"{System.IO.Path.GetFileName(_model.SourcePath)}.bak next to it." +
            (consistency.Count > 0
                ? Environment.NewLine + "Worth a look before building:" + Environment.NewLine + "• " +
                  string.Join(Environment.NewLine + "• ", consistency)
                : "");
        ApexLog.Info($"Review: corrected spec saved to {_model.SourcePath} (original in .bak).");
        foreach (string w in consistency) ApexLog.Warn("Review consistency: " + w);
        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveNow();

    private void OnSaveAndBuild(object sender, RoutedEventArgs e)
    {
        if (!SaveNow()) return;
        // The default button must not fly past a half-fix: if the drawing's
        // overall size and a same-named parameter disagree, make the modeler
        // choose with the warning ON SCREEN, not in a closing window
        // (V2 re-review finding 3).
        List<string> consistency = _model.ConsistencyWarnings();
        if (consistency.Count > 0)
        {
            MessageBoxResult go = MessageBox.Show(this,
                "Saved — but worth a look before building:\n\n• " +
                string.Join("\n• ", consistency) +
                "\n\nBuild anyway?",
                "Apex — values disagree", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (go != MessageBoxResult.Yes) return; // stay open; status already shows the warnings
        }
        BuildRequested = true;
        DialogResult = true;
        Close();
    }
}
