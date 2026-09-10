using System.Windows.Forms;

namespace MediaFlux.Tests;

/// <summary>
/// Provides the required shutdown boundary for an STA test that owns WinForms.
/// A queued continuation must run while the owner still has a valid handle; disposing
/// immediately after Close can otherwise race Control.CreateHandle on that continuation.
/// </summary>
internal static class WinFormsTestLifecycle
{
    public static void CloseAndDispose(Form? owner)
    {
        foreach (Form dialog in Application.OpenForms.Cast<Form>()
                     .Where(form => form != owner)
                     .ToArray())
        {
            dialog.Close();
        }

        DrainPendingCallbacks();

        if (owner is { IsDisposed: false })
            owner.Close();

        // Close is synchronous, but continuations queued before it are not.  Flush
        // them before Dispose so they cannot overlap native-handle teardown.
        DrainPendingCallbacks();

        if (owner is { IsDisposed: false })
            owner.Dispose();

        WindowsFormsSynchronizationContext.Uninstall();
    }

    private static void DrainPendingCallbacks()
    {
        SynchronizationContext? context = SynchronizationContext.Current;
        if (context == null)
            return;

        bool drained = false;
        context.Post(_ => drained = true, null);
        while (!drained)
            Application.DoEvents();
    }
}
