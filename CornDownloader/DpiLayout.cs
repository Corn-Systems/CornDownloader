using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CornSystems;

namespace CornDownloader
{
    // Per-monitor DPI support for code-built WinForms UI.
    //
    // Controls register every DPI-dependent assignment (Size, Location, Margin, Padding,
    // Font) as a step. Add() runs the step immediately, and Reapply() runs them all again
    // after the window lands on a monitor with a different scale factor. That keeps the
    // "build it once with Dpi.S()" style of the codebase but stops the UI being frozen at
    // whatever DPI it happened to be constructed at.
    internal sealed class DpiLayout
    {
        private readonly List<Action> _steps = new();

        public void Add(Action step)
        {
            if (step == null) return;
            Run(step);
            _steps.Add(step);
        }

        public void Reapply()
        {
            foreach (var step in _steps.ToArray()) Run(step);
        }

        private static void Run(Action step)
        {
            try { step(); }
            catch (Exception ex) { SessionLog.Write("DPI", ex); }
        }

        // Wires a top-level form so `rescale` runs whenever the form's monitor DPI no longer
        // matches the DPI its controls were laid out for:
        //   • DpiChanged — dragged to another monitor, or the user changed display scaling.
        //   • Shown      — opened on a secondary monitor whose DPI differs from the system DPI
        //                  (DeviceDpi before the handle exists is always the system DPI).
        //   • Activated  — Dpi.Current is process-wide; if a dialog was dragged to another
        //                  monitor it moved the global, so re-check when focus comes back.
        public static void Attach(Form form, Action rescale)
        {
            void CheckNow()
            {
                if (form.IsDisposed || !form.IsHandleCreated) return;
                int dpi = form.DeviceDpi;
                if (dpi <= 0 || Math.Abs(dpi - Dpi.Current) < 0.5f) return;
                Dpi.Update(dpi);
                SessionLog.Write($"[DPI] {form.GetType().Name} now at {dpi} dpi ({Dpi.Scale * 100:0}%)");
                form.SuspendLayout();
                try     { rescale(); }
                catch (Exception ex) { SessionLog.Write("DPI", ex); }
                finally { form.ResumeLayout(true); }
                form.Invalidate(true);
            }

            form.DpiChanged += (s, e) =>
            {
                // Windows' suggested rectangle keeps the window under the cursor at the new
                // scale. Applying it to a maximized window would knock it out of maximized.
                if (form.WindowState == FormWindowState.Normal && e.SuggestedRectangle != Rectangle.Empty)
                    form.SetBounds(e.SuggestedRectangle.X, e.SuggestedRectangle.Y,
                                   e.SuggestedRectangle.Width, e.SuggestedRectangle.Height);

                // Defer the rescale: WinForms is still sending WM_DPICHANGED_AFTERPARENT to the
                // existing children (and rescaling their fonts / Min/MaxSize) while this event
                // runs. Running afterwards means our absolute values are the ones that stick.
                try { form.BeginInvoke((Action)CheckNow); }
                catch (Exception ex) { SessionLog.Write("DPI", ex); }
            };
            form.Shown     += (s, e) => CheckNow();
            form.Activated += (s, e) => CheckNow();
        }
    }
}
