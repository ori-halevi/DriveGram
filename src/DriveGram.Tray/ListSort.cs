using System.Runtime.InteropServices;

namespace DriveGram.Tray;

/// <summary>
/// Clickable column headers with the sort arrow (like Explorer). Compares the rows' own values (sizes,
/// fractions), never the text shown. A list with a natural order (the upload queue) goes back to it on a
/// third click, with no arrow.
/// </summary>
sealed class ListSort : System.Collections.IComparer
{
    public int Column { get; private set; } = -1;
    public bool Descending { get; private set; }
    readonly Func<ListViewItem, int, object?> _key;
    readonly Func<ListViewItem, long>? _natural;
    readonly ListView _list;

    ListSort(ListView list, Func<ListViewItem, int, object?> key, Func<ListViewItem, long>? natural)
    {
        _list = list;
        _key = key;
        _natural = natural;
    }

    /// <param name="key">The value a column sorts by (string, long, double, int).</param>
    /// <param name="natural">The list's own order (null: none, a third click sorts up again).</param>
    public static ListSort Attach(ListView list, Func<ListViewItem, int, object?> key, Func<ListViewItem, long>? natural = null)
    {
        var s = new ListSort(list, key, natural);
        list.ListViewItemSorter = s;
        list.ColumnClick += (_, e) => s.Click(e.Column);
        list.HandleCreated += (_, _) => s.Arrows();
        return s;
    }

    void Click(int column)
    {
        if (column != Column) (Column, Descending) = (column, false);
        else if (!Descending) Descending = true;
        else if (_natural is not null) (Column, Descending) = (-1, false);
        else Descending = false;
        Apply();
    }

    /// <summary>Sort again (rows were added or changed) and draw the arrow.</summary>
    public void Apply()
    {
        _list.Sort();
        Arrows();
    }

    public int Compare(object? x, object? y)
    {
        if (x is not ListViewItem a || y is not ListViewItem b) return 0;
        int c = 0;
        if (Column >= 0)
        {
            var (ka, kb) = (_key(a, Column), _key(b, Column));
            c = ka is string sa && kb is string sb ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase)
                : System.Collections.Comparer.Default.Compare(ka, kb);
            if (Descending) c = -c;
        }
        if (c == 0 && _natural is not null) c = _natural(a).CompareTo(_natural(b));
        return c;
    }

    // ── the arrow in the header ──────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct HDITEM
    {
        public uint mask; public int cxy; public IntPtr pszText; public IntPtr hbm; public int cchTextMax;
        public int fmt; public IntPtr lParam; public int iImage; public int iOrder; public uint type; public IntPtr pvFilter; public uint state;
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);
    const int LVM_GETHEADER = 0x101F, HDM_GETITEMW = 0x120B, HDM_SETITEMW = 0x120C, HDI_FORMAT = 0x4, HDF_SORTUP = 0x400, HDF_SORTDOWN = 0x200;

    /// <summary>Draws the arrow on the sorted column only (WinForms clears it when a column changes).</summary>
    public void Arrows()
    {
        if (!_list.IsHandleCreated) return;
        var header = SendMessage(_list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero) return;
        for (int i = 0; i < _list.Columns.Count; i++)
        {
            var item = new HDITEM { mask = HDI_FORMAT };
            if (SendMessage(header, HDM_GETITEMW, (IntPtr)i, ref item) == IntPtr.Zero) continue;
            item.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
            if (i == Column) item.fmt |= Descending ? HDF_SORTDOWN : HDF_SORTUP;
            SendMessage(header, HDM_SETITEMW, (IntPtr)i, ref item);
        }
    }
}
