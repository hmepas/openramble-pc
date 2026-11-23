using Avalonia.Headless;
using Avalonia.Threading;

namespace OpenRamble.App.Linux.UiTests;

/// <summary>Letting the headless dispatcher catch up, as a desktop would between frames.</summary>
internal static class Ui
{
    /// <summary>Run what was posted, lay out and render, until nothing more is queued.</summary>
    public static void Settle()
    {
        for (int pass = 0; pass < 4; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Wait for work off the UI thread, settling the UI meanwhile.</summary>
    public static async Task WaitFor(string what, Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Settle();
            if (condition()) { return; }
            await Task.Delay(5);
        }

        Assert.Fail($"didn't wait: {what}");
    }
}
