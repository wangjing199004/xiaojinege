using System;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif
namespace ZwcadBatchPlot;
internal static class StandardFrameIdentity
{
    internal const string Key = "XJG_STANDARD_TEMPLATE";
    internal static string[] Read(BlockTableRecord definition, Transaction transaction)
    {
        if (definition.ExtensionDictionary.IsNull) return Array.Empty<string>();
        var dictionary = (DBDictionary)transaction.GetObject(definition.ExtensionDictionary, OpenMode.ForRead);
        if (!dictionary.Contains(Key)) return Array.Empty<string>();
        var marker = transaction.GetObject(dictionary.GetAt(Key), OpenMode.ForRead) as Xrecord;
        using var data = marker?.Data;
        return data?.AsArray().Where(value => value.TypeCode == 1).Select(value => value.Value as string ?? "").ToArray()
            ?? Array.Empty<string>();
    }
    internal static string GetCanonicalName(BlockTableRecord definition, Transaction transaction)
    {
        var marker = Read(definition, transaction);
        var item = marker.Length == 0 ? null : StandardFrameCatalog.Items.FirstOrDefault(template => template.Id == marker[0]);
        return item?.Name ?? definition.Name;
    }
    internal static void Write(BlockTableRecord definition, Transaction transaction, string id, string revision)
    {
        if (definition.ExtensionDictionary.IsNull) definition.CreateExtensionDictionary();
        var dictionary = (DBDictionary)transaction.GetObject(definition.ExtensionDictionary, OpenMode.ForWrite);
        Xrecord marker;
        if (dictionary.Contains(Key)) marker = (Xrecord)transaction.GetObject(dictionary.GetAt(Key), OpenMode.ForWrite);
        else
        {
            marker = new Xrecord();
            dictionary.SetAt(Key, marker);
            transaction.AddNewlyCreatedDBObject(marker, true);
        }
        using var data = new ResultBuffer(new TypedValue(1, id), new TypedValue(1, revision));
        marker.Data = data;
    }
}
