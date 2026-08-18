using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Apex.BimStudio;

/// <summary>
/// Round 4: per-item batch progress, honest about the threading reality.
///
/// The Revit API is single-threaded and the batch runs INSIDE the command on
/// the API thread — no API call ever leaves it. This window is therefore not
/// an interactive dialog: between per-item transactions the command pumps the
/// dispatcher at Render priority only (see <see cref="Pump"/>), which repaints
/// the window WITHOUT processing input. The modeler sees true per-item status
/// while Revit is busy; nothing can be clicked mid-run, and the pre-run
/// confirmation says so. (A modeless ExternalEvent UI is logged as round-4
/// debt in the ship ledger; "frozen-but-progressing with honest status" is the
/// shipped behavior.)
///
/// Pure code-built WPF: the build toolchain has no XAML compiler.
/// </summary>
internal sealed class BatchProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly ListBox _list;
    private readonly int _total;

    public BatchProgressWindow(int total, string folder, IntPtr ownerHandle)
    {
        _total = total;
        Title = "Apex — building families";
        Width = 560;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new TextBlock
        {
            Text = $"Building {total} equipment famil{(total == 1 ? "y" : "ies")} from:\n{folder}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _status = new TextBlock
        {
            Text = "Starting…",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);

        _bar = new ProgressBar { Minimum = 0, Maximum = total, Height = 18, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(_bar, 2);
        root.Children.Add(_bar);

        _list = new ListBox();
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        Grid.SetRow(_list, 3);
        root.Children.Add(_list);

        Content = root;

        try
        {
            if (ownerHandle != IntPtr.Zero)
                new System.Windows.Interop.WindowInteropHelper(this) { Owner = ownerHandle };
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Progress window could not attach to the Revit window: " + ex.Message);
        }
    }

    /// <summary>Called on the API thread just before a drawing is processed.</summary>
    public void Starting(int index1, string file)
    {
        _status.Text = $"Building {index1} of {_total} — {file}  (Revit is busy; this window updates as each family finishes)";
        Pump();
    }

    /// <summary>Called on the API thread after a drawing finishes (either way).</summary>
    public void Finished(BatchRunReport.Row row, int done)
    {
        _bar.Value = done;
        var item = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        if (row.BuildOk)
        {
            bool review = row.ValidateWarnings > 0 || row.LowConfidenceFields.Length > 0;
            item.Text = (review ? "⚠ " : "✓ ") + row.File +
                (review ? " — built; check values (see the build report)" : " — built");
            if (review) item.Foreground = Brushes.DarkGoldenrod;
        }
        else
        {
            item.Text = "✗ " + row.File + " — " + row.Failure + ": " +
                (string.IsNullOrEmpty(row.Error) ? "see the build report" : row.Error);
            item.Foreground = Brushes.Firebrick;
        }
        _list.Items.Add(item);
        _list.ScrollIntoView(item);
        Pump();
    }

    /// <summary>
    /// Repaint without re-entrancy: push a dispatcher frame that drains only
    /// Render-and-above priority work. Input stays queued, so neither this
    /// window nor Revit's UI can re-enter the running command; layout and
    /// rendering still run, so the status the modeler sees is current.
    /// </summary>
    public static void Pump()
    {
        try
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        catch (Exception ex)
        {
            // A paint that fails must never fail the batch.
            ApexLog.Warn("Progress repaint failed (continuing): " + ex.Message);
        }
    }
}
