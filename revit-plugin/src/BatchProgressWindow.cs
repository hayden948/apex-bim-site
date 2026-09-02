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
/// the API thread — no API call ever leaves it. Between per-item transactions
/// the command pumps a dispatcher frame (see <see cref="Pump"/>) so this
/// window repaints with true per-item status. A pumped Win32 message loop
/// dispatches messages for EVERY window on the thread — including Revit's —
/// so for the duration of the run Revit's main window is DISABLED
/// (<see cref="BeginRunUi"/>/<see cref="EndRunUi"/>, the same owner-disable
/// semantics a modal dialog gets), which is what actually prevents input
/// re-entrancy into a transaction; and this window refuses to close mid-run.
/// During a long single item Windows may ghost the titlebar with
/// "(Not Responding)" — the run is still progressing; the header says so.
/// (A modeless ExternalEvent UI is logged as round-4 debt in the ship
/// ledger; "frozen-but-progressing with honest status" is the shipped
/// behavior.)
///
/// Pure code-built WPF: the build toolchain has no XAML compiler.
/// </summary>
internal sealed class BatchProgressWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly ListBox _list;
    private readonly int _total;
    private readonly IntPtr _owner;
    private bool _running;

    public BatchProgressWindow(int total, string folder, IntPtr ownerHandle)
    {
        _total = total;
        _owner = ownerHandle;
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
            Text = $"Building {total} equipment famil{(total == 1 ? "y" : "ies")} from:\n{folder}\n" +
                "Revit is busy until this finishes. If the titlebar briefly says \"Not Responding\" " +
                "during a large family, the run is still progressing.",
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

    /// <summary>
    /// Disable Revit's main window for the duration of the run (modal
    /// semantics for a pumped loop) and arm the close guard. Call before the
    /// first item; pair with <see cref="EndRunUi"/> in a finally.
    /// </summary>
    public void BeginRunUi()
    {
        _running = true;
        try
        {
            if (_owner != IntPtr.Zero) EnableWindow(_owner, false);
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Could not disable the Revit window for the batch: " + ex.Message);
        }
    }

    /// <summary>Re-enable Revit and allow this window to close. ALWAYS call (finally).</summary>
    public void EndRunUi()
    {
        _running = false;
        try
        {
            if (_owner != IntPtr.Zero) EnableWindow(_owner, true);
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Could not re-enable the Revit window after the batch: " + ex.Message);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The batch cannot be interrupted mid-transaction; refuse to close
        // until the run ends (the command closes this window itself).
        if (_running) e.Cancel = true;
        base.OnClosing(e);
    }

    /// <summary>Called on the API thread just before a drawing is processed.</summary>
    public void Starting(int index1, string file)
    {
        _status.Text = $"Building {index1} of {_total} — {file}";
        Pump();
    }

    /// <summary>Called on the API thread after a drawing finishes (either way).</summary>
    public void Finished(BatchRunReport.Row row, int done)
    {
        _bar.Value = done;
        var item = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        if (row.BuildOk)
        {
            // Same "needs review" definition as the report headline — a family
            // whose geometry checks failed must never read as a plain success.
            bool review = BatchRunReport.NeedsReview(row);
            item.Text = (review ? "⚠ " : "✓ ") + row.File +
                (review
                    ? (BatchRunReport.FlexOk(row)
                        ? " — built; check values (see the build report)"
                        : " — built, but geometry checks FAILED (see the build report)")
                    : " — built");
            if (review) item.Foreground = Brushes.DarkGoldenrod;
        }
        else
        {
            // Customer words, not taxonomy enum names (V2 re-review finding 6).
            item.Text = "✗ " + row.File + " — " + BatchRunReport.CustomerClass(row.Failure) + ": " +
                (string.IsNullOrEmpty(row.Error) ? "see the build report" : row.Error);
            item.Foreground = Brushes.Firebrick;
        }
        _list.Items.Add(item);
        _list.ScrollIntoView(item);
        Pump();
    }

    /// <summary>
    /// Repaint between items: push a dispatcher frame that exits once
    /// Render-priority work (layout + paint) has drained. NOTE: a pushed frame
    /// still runs the thread's Win32 message loop, so it is NOT what prevents
    /// re-entrancy — disabling Revit's window for the run is
    /// (<see cref="BeginRunUi"/>); this window itself has no control that can
    /// reach the Revit API and refuses to close mid-run.
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
