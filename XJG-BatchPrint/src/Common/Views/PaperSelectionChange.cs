using System.Windows.Controls;
namespace ZwcadBatchPlot;
internal static class PaperSelectionChange
{
    // A ComboBox can raise SelectionChanged while loading or recycling a row.
    // Only active mouse/dropdown or keyboard interaction may trigger CAD re-reading.
    internal static bool IsUserSelection(ComboBox combo, SelectionChangedEventArgs e)
        => e.AddedItems.Count > 0 && combo.SelectedItem != null
            && (combo.IsDropDownOpen || combo.IsKeyboardFocusWithin);
}
