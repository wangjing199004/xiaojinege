using System;

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    /// <summary>单张打印：打开主面板并切到「单张打印」Tab（框选外框、选纸、打印都在 Tab 内完成）。</summary>
    private static void SinglePlotCore()
    {
        ShowMainWindow(2);
    }
}
