using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace FlameCharter
{
    /// <summary>
    /// This class implements the tool window exposed by this package and hosts a user control.
    /// </summary>
    /// <remarks>
    /// In Visual Studio tool windows are composed of a frame (implemented by the shell) and a pane,
    /// usually implemented by the package implementer.
    /// <para>
    /// This class derives from the ToolWindowPane class provided from the MPF in order to use its
    /// implementation of the IVsUIElementPane interface.
    /// </para>
    /// </remarks>
    [Guid("d89817c5-0f20-4c6d-a3f1-7c27bb8b9f98")]
    public class SamplerWindow : ToolWindowPane
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SamplerWindow"/> class.
        /// </summary>
        public SamplerWindow() : base(null)
        {
            this.Caption = "FlameCharter";

            // This is the user control hosted by the tool window; Note that, even if this class implements IDisposable,
            // we are not calling Dispose on this object. This is because ToolWindowPane calls Dispose on
            // the object returned by the Content property.
            this.Content = new SamplerWindowControl();
        }

        // Closing the window does not stop the sampler on its own: it is a
        // separate process and it keeps writing to the file for as long as the
        // target is alive. Nobody would ever see it, and nobody could stop it
        // either, because the button that signals it just went away.
        protected override void OnClose()
        {
            var control = this.Content as SamplerWindowControl;
            if (control != null) control.StopIfActive();

            base.OnClose();
        }
    }
}