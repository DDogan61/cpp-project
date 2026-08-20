using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Process = System.Diagnostics.Process;   // clashes with EnvDTE.Process

namespace FlameCharter
{
    public partial class SamplerWindowControl : UserControl
    {
        // The usual names of the entry function, used when the file paths do not
        // help us find where the CRT startup code ends.
        private static readonly string[] EntryNames =
            { "main", "wmain", "WinMain", "wWinMain" };

        private readonly FlameChartModel _model = new FlameChartModel();

        private string _solutionFolder;
        private string _samplesPath;

        // Starts out as whatever the user typed, and is overwritten by the
        // interval the sampler reports in its meta line.
        private int _samplerInterval = 10;

        // The one button flips between these two states.
        private bool _active;

        // Only ever watched, never killed: it is the user's own program running
        // under the debugger. We attached to it, we did not start it.
        private Process _targetProcess;

        private Process _samplerProcess;

        // How we ask the sampler to stop. See Deactivate for why it is not just
        // killed.
        private EventWaitHandle _stopEvent;

        private JsonlTailReader _reader;
        private DispatcherTimer _timer;
        private Stopwatch _stopwatch;

        private int _sampleCount;

        // Coverage is measured with the sampler's own clock. The Stopwatch was
        // also counting the target's loader/CRT init and the delay of the UI
        // timer, and neither has anything to do with samples the sampler missed.
        private double _firstSampleMs = -1;
        private double _lastSampleMs;

        public SamplerWindowControl()
        {
            InitializeComponent();
            chart.Model = _model;
            chart.SelectionChanged += Chart_SelectionChanged;
            chart.NavigateRequested += Chart_NavigateRequested;
            scroller.ScrollChanged += Scroller_ScrollChanged;
        }

        // ====================================================================
        // Attaching to and detaching from the target
        // ====================================================================

        // One button for both, so there is never a "which one am I in" question:
        // whatever it says is what pressing it does.
        private void btnToggle_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_active) Deactivate("stopped");
            else Activate();
        }

        // We do not build anything and we do not start anything. The user runs
        // the solution with F5 as usual, and we attach to the process that is
        // already up.
        //
        // Building from in here was what broke on the test machine: the build
        // ran with our environment rather than the one the user's VS was set up
        // with, and BuildProject blocks until it finishes, so VS sat frozen for
        // as long as the build took only to come back with an error.
        private void Activate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int interval;
            if (!int.TryParse(tbInterval.Text, out interval) || interval <= 0)
            {
                txtStatus.Text = "Interval must be a number greater than 0";
                return;
            }

            string samplerExe = ResolveSamplerExe();
            if (!File.Exists(samplerExe))
            {
                txtStatus.Text = "Sampler.exe not found: " + samplerExe;
                return;
            }

            string targetName;
            int pid;

            try
            {
                pid = FindDebuggedProcess(out targetName);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Cannot ask the debugger: " + ex.Message;
                return;
            }

            if (pid == 0)
            {
                txtStatus.Text = "Nothing is running under the debugger. Start it with F5.";
                return;
            }

            // A handle we only use to notice when it exits. Getting it now also
            // tells us the pid is still valid.
            try
            {
                _targetProcess = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                txtStatus.Text = "The target has already exited.";
                return;
            }

            _samplerInterval = interval;
            txtInterval.Text = "Interval (ms): " + interval;

            // Used to tell our own frames from the CRT startup ones. It is only
            // a hint, FindFirstAppFrame falls back to the entry function names
            // when there is no solution folder to compare against.
            _solutionFolder = GetSolutionFolder();

            txtTarget.Text = targetName + " (pid " + pid + ")";

            // Output goes to temp, we always have write permission there.
            _samplesPath = Path.Combine(Path.GetTempPath(), "flamecharter", "samples.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(_samplesPath));

            try
            {
                // Otherwise we would count the lines of the previous run.
                if (File.Exists(_samplesPath)) File.Delete(_samplesPath);
            }
            catch (IOException)
            {
                // A sampler from an earlier run may still be holding it. It gets
                // truncated on open anyway, and the reader starts at zero.
            }

            // Created HERE and not by the sampler: it only opens the event, so
            // it has to exist before the process starts or the very first
            // Deactivate would have nothing to signal.
            string stopName = "Local\\FlameCharter_Stop_" + pid;
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);

            // The constructor IGNORES the initial state when an event of that
            // name is already there, and hands back the existing one as it is.
            // Activate -> Deactivate -> Activate on the same pid can hit exactly
            // that: the previous sampler may not have closed its handle yet, so
            // the object is still alive and still signalled, and the sampler we
            // are about to start would read "stop" on its first wait and exit
            // without a single sample. Resetting by hand costs nothing.
            _stopEvent.Reset();

            try
            {
                _samplerProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = samplerExe,
                    Arguments = string.Format(CultureInfo.InvariantCulture,
                                              "{0} \"{1}\" {2} \"{3}\"",
                                              pid, _samplesPath, interval, stopName),
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Cannot start the sampler: " + ex.Message;
                Deactivate("idle");
                return;
            }

            _stopwatch = Stopwatch.StartNew();
            _reader = new JsonlTailReader(_samplesPath);
            _model.Reset(_samplerInterval);
            chart.Refresh();
            _sampleCount = 0;
            _firstSampleMs = -1;
            _lastSampleMs = 0;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _active = true;
            btnToggle.Content = "Deactivate";
            txtStatus.Text = "attached";
        }

        private void Deactivate(string status)
        {
            // Timer first. Otherwise a tick fires during the waits below and
            // tries to use the reader of a run that is already gone.
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _timer = null;
            }

            // The sampler is ASKED to stop, not killed. At any moment it may be
            // holding a suspended thread of the target, and a process that gets
            // killed never resumes it: the program the user is debugging would
            // stay frozen with no way back. It leaves on its own instead, once
            // the thread is running again.
            if (_stopEvent != null)
            {
                try { _stopEvent.Set(); }
                catch (Exception) { }
            }

            if (_samplerProcess != null)
            {
                try { _samplerProcess.WaitForExit(3000); }
                catch (Exception) { }
            }

            // Whatever it wrote in its last few milliseconds. After this the
            // reader can go.
            ReadNewSamples();
            _reader = null;

            // Only if it ignored the event, which should not happen. Skipping
            // this instead would leak a sampler that keeps writing forever.
            KillIfRunning(ref _samplerProcess);

            if (_stopEvent != null)
            {
                _stopEvent.Dispose();
                _stopEvent = null;
            }

            // Disposed, never killed. It is the user's program and the debugger
            // owns its lifetime, not us.
            if (_targetProcess != null)
            {
                _targetProcess.Dispose();
                _targetProcess = null;
            }

            if (_stopwatch != null) _stopwatch.Stop();

            _active = false;
            btnToggle.Content = "Activate";
            txtStatus.Text = status;
        }

        // Called by the tool window when it closes, so a sampler is never left
        // running behind a window nobody can see any more.
        public void StopIfActive()
        {
            if (_active) Deactivate("idle");
        }

        private static void KillIfRunning(ref Process process)
        {
            if (process == null) return;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);   // waiting forever would freeze the UI
                }
            }
            catch (Exception)
            {
                // It may have exited between the check and the Kill, no problem.
            }
            finally
            {
                process.Dispose();
                process = null;
            }
        }

        // ====================================================================
        // Finding out what to run
        // ====================================================================

        // Sampler.exe ships inside the VSIX, so we look for it next to our own
        // assembly and not through DTE.
        //
        // For this to work Sampler.exe has to be added to the VSIX project with
        // these Properties:
        //   Build Action        = Content
        //   Include in VSIX     = True
        //   Copy to Output Dir  = Copy if newer
        private static string ResolveSamplerExe()
        {
            string dir = Path.GetDirectoryName(
                typeof(SamplerWindowControl).Assembly.Location);
            return Path.Combine(dir, "Sampler.exe");
        }

        // The pid of whatever is running under the debugger, 0 if there is
        // nothing. VS has known it since the moment the user pressed F5, so
        // there is nothing for us to resolve, build or look up by name.
        //
        // Ctrl+F5 (start without debugging) is the one case this cannot see:
        // that process is attached to nothing and DTE does not know about it.
        // The caller turns the 0 into a message telling the user to use F5.
        private static int FindDebuggedProcess(out string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            name = null;

            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
            if (dte == null || dte.Debugger == null) return 0;

            // Design mode means no debugging session at all. Break mode is fine
            // and deliberately allowed: sitting at a breakpoint just means every
            // sample shows the same stack until it runs on.
            if (dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode) return 0;

            // EnvDTE.Process, not System.Diagnostics.Process. The alias at the
            // top of the file points the short name at the other one.
            EnvDTE.Process current = dte.Debugger.CurrentProcess;

            if (current != null)
            {
                name = SafeProcessName(current);
                return current.ProcessID;
            }

            // Solutions that debug several processes at once: nothing is picked
            // in the Debug > Processes dropdown, so we take the first one.
            try
            {
                foreach (EnvDTE.Process process in dte.Debugger.DebuggedProcesses)
                {
                    name = SafeProcessName(process);
                    return process.ProcessID;
                }
            }
            catch (Exception)
            {
                // The session can end between the mode check and this loop.
            }

            return 0;
        }

        // Name is the full path of the exe, and it is COM on the other side of
        // it, so it can throw while the session is winding down.
        private static string SafeProcessName(EnvDTE.Process process)
        {
            try { return Path.GetFileName(process.Name); }
            catch (Exception) { return "target"; }
        }

        // Only used to spot the frames that are ours, with forward slashes to
        // match what the sampler writes. Null is fine, FindFirstAppFrame then
        // goes by the entry function names instead.
        private static string GetSolutionFolder()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
            if (dte == null || dte.Solution == null || !dte.Solution.IsOpen) return null;

            string solutionFile = dte.Solution.FullName;
            if (string.IsNullOrEmpty(solutionFile)) return null;

            return Path.GetDirectoryName(solutionFile).Replace('\\', '/');
        }

        // ====================================================================
        // Reading the samples
        // ====================================================================

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                // The sampler creates the file only once it has attached.
                if (!File.Exists(_samplesPath)) { txtStatus.Text = "waiting for sampler..."; return; }

                // Both checks happen BEFORE the read. The other way around there
                // is a race: if the sampler writes its last lines and exits
                // between the read and the check, we stop without those lines
                // ever being read.
                bool samplerDone = _samplerProcess == null || _samplerProcess.HasExited;
                bool targetGone = _targetProcess == null || _targetProcess.HasExited;

                ReadNewSamples();

                // Time is just the wall clock, to tell the user how long it took.
                txtTime.Text = $"Time: {_stopwatch.Elapsed.TotalSeconds:F2}s";

                // Coverage: how many samples the sampler should have taken versus
                // how many it did. The window comes from the sampler's own stamps,
                // and one interval is added because the last sample stands for a
                // period as well.
                if (_firstSampleMs >= 0 && _samplerInterval > 0)
                {
                    double windowMs = _lastSampleMs - _firstSampleMs + _samplerInterval;
                    double expected = windowMs / _samplerInterval;

                    txtCoverage.Text = $"Coverage: {_sampleCount / expected * 100.0:F2}%";
                }

                // The user stopped debugging, or the program simply ended. The
                // sampler sees it too and closes a few ms later, so we keep
                // reading until it is gone or we lose the last lines.
                if (targetGone)
                {
                    if (samplerDone) Deactivate($"Target exited - {_sampleCount} samples");
                    else txtStatus.Text = "target exited, flushing...";

                    return;
                }

                // Target still alive but the sampler is not: it failed to attach
                // or died on us. Staying "active" with nothing writing would just
                // show a chart frozen for no visible reason.
                if (samplerDone)
                {
                    Deactivate($"Sampler stopped - {_sampleCount} samples");
                    return;
                }

                txtStatus.Text = _sampleCount + " samples";
            }
            catch (Exception ex)
            {
                // One failed read should not take the tool window down with it.
                txtStatus.Text = "ERR: " + ex.Message;
            }
        }

        // Everything that showed up in the file since the last call, into the
        // model and onto the chart. Deactivate calls it one last time so the
        // lines written just before the sampler exited are not lost.
        private void ReadNewSamples()
        {
            if (_reader == null) return;

            var lines = _reader.ReadNewLines();
            if (lines.Count == 0) return;

            // The meta line is picked out to get the interval the sampler
            // actually used.
            foreach (var line in lines)
            {
                if (line.Contains("\"type\":\"meta\""))
                {
                    var mi = Regex.Match(line, "\"interval_ms\":(\\d+)");
                    if (mi.Success)
                    {
                        _samplerInterval = int.Parse(mi.Groups[1].Value);
                        _model.SetInterval(_samplerInterval);
                    }
                }
                else
                {
                    _sampleCount++;

                    double tMs;
                    List<FrameInfo> frames;
                    if (SampleParser.TryParse(line, out tMs, out frames))
                    {
                        // The sampling window: the sampler's own timestamp on
                        // the first and the last sample. Coverage uses this.
                        if (_firstSampleMs < 0) _firstSampleMs = tMs;
                        _lastSampleMs = tMs;

                        // The CRT startup code is compiled into the target exe
                        // so it does not count as a system frame. Everything up
                        // to the first real frame is thrown away.
                        int first = FindFirstAppFrame(frames);
                        if (first > 0) frames.RemoveRange(0, first);

                        if (frames.Count > 0) _model.AddSample(tMs, frames);
                    }
                }
            }

            chart.Refresh();

            // AFTER Refresh and not before: scrolling puts the mouse over a
            // different box and the panel should show that one.
            if (chkFollow.IsChecked == true) scroller.ScrollToRightEnd();

            // The selected run may have grown, refresh its duration.
            chart.RefreshSelection();

            UpdatePeakPanel();
        }

        // Finds where the CRT startup code at the head of the chain ends.
        //
        // Two stages, because neither one is enough on its own:
        //   * Path: the reliable one. The first frame under the solution folder
        //     is our code. But it needs line info.
        //   * Name: with code inlined in Release, or projects whose sources sit
        //     outside the solution folder, the path never matches. Then we look
        //     at the name of the entry function instead.
        //
        // If neither hits it returns -1 and the chain is left alone: showing the
        // CRT frames is better than showing something with pieces missing.
        private int FindFirstAppFrame(List<FrameInfo> frames)
        {
            if (_solutionFolder != null)
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    string file = frames[i].File;

                    if (file != null && file.StartsWith(_solutionFolder,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            for (int i = 0; i < frames.Count; i++)
            {
                for (int e = 0; e < EntryNames.Length; e++)
                {
                    if (string.Equals(frames[i].Fn, EntryNames[e], StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        // ====================================================================
        // Panels and other UI handlers
        // ====================================================================

        // The chart sits inside the ScrollViewer at full size and does not hear
        // about scrolling itself, so we hand it the visible range.
        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            chart.SetViewport(e.HorizontalOffset, e.ViewportWidth);
        }

        private void Chart_SelectionChanged(FlameSelectionInfo info)
        {
            if (info == null)
            {
                txtSelectionFn.Text = "-";
                txtSelectionLine.Text = "";
                txtSelectionRange.Text = "";
                txtSelectionDuration.Text = "";
                return;
            }

            txtSelectionFn.Text = info.Name;

            txtSelectionLine.Text = info.Line > 0
                ? $"Line {info.Line} | {info.LineStartMs:F0} ms - {info.LineEndMs:F0} ms ({info.LineDurationMs:F0} ms)"
                : "";

            txtSelectionRange.Text = $"{info.StartMs:F0} - {info.EndMs:F0} ms";
            txtSelectionDuration.Text = $"Duration: {info.DurationMs:F0} ms";
        }

        private void Chart_NavigateRequested(FlameSelectionInfo info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (info == null || info.Line <= 0 || string.IsNullOrEmpty(info.File))
            {
                txtStatus.Text = "No source info for this box";
                return;
            }

            // The sampler writes forward slashes, since a backslash would have to
            // be escaped in JSON.
            string path = info.File.Replace('/', '\\');

            if (!File.Exists(path))
            {
                txtStatus.Text = "Source not found: " + path;
                return;
            }

            try
            {
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;

                // var is needed here: Window exists both in System.Windows and in
                // EnvDTE.
                var window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindCode);
                window.Activate();

                var selection = (TextSelection)dte.ActiveDocument.Selection;
                selection.GotoLine(info.Line, true);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Cannot open: " + ex.Message;
            }
        }

        // Summary of the interval where the stack stayed deepest, updated while
        // the run is still going.
        private void UpdatePeakPanel()
        {
            if (_model.PeakDepth == 0)
            {
                txtPeakDepth.Text = "Depth: -";
                txtPeakRange.Text = "";
                txtPeakLeaf.Text = "";
                return;
            }

            txtPeakDepth.Text = $"Depth: {_model.PeakDepth}";
            txtPeakRange.Text = $"{_model.PeakStartMs:F0} - {_model.PeakEndMs:F0} ms " +
                                $"({_model.PeakDurationMs:F0} ms)";
            txtPeakLeaf.Text = _model.PeakLeaf;
        }

        private void btnGoPeak_Click(object sender, RoutedEventArgs e)
        {
            if (_model.PeakDepth == 0)
            {
                txtStatus.Text = "No peak yet";
                return;
            }

            // With Follow on, every tick would scroll back to the right end and
            // we would not stay where we jumped to.
            chkFollow.IsChecked = false;

            double startX = _model.PeakStartMs * chart.PixelsPerMs;
            double widthPx = _model.PeakDurationMs * chart.PixelsPerMs;

            // The interval goes to the middle of the screen instead of the left
            // edge, so what comes before and after is visible too and it is
            // easier to tell which call it belongs to.
            double offset = startX + widthPx / 2 - scroller.ViewportWidth / 2;

            scroller.ScrollToHorizontalOffset(Math.Max(0, offset));
        }

        private void tbInterval_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }
    }
}