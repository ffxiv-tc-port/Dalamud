namespace Dalamud.Game.Gui.Dtr;

/// <summary>
/// Running state of the server info bar row layout for one frame.
/// </summary>
internal struct DtrRowCursor
{
    /// <summary>
    /// Index of the row the last placed entry went into; 0 is the native bar's own row.
    /// </summary>
    public int Row;

    /// <summary>
    /// Width already taken on the current row, spacing included.
    /// </summary>
    public float Used;

    /// <summary>
    /// Width taken on row 0, spacing included.
    /// </summary>
    public float FirstRowUsed;

    /// <summary>
    /// Gets the number of rows in use, or 0 if nothing was placed.
    /// </summary>
    public readonly int RowCount => this.Used > 0 || this.Row > 0 ? this.Row + 1 : 0;
}

/// <summary>
/// Pure layout math for wrapping server info bar entries onto extra rows.
/// Contract: never throws and never allocates; NaN or non-positive limits mean "do not wrap".
/// </summary>
internal static class DtrBarLayout
{
    /// <summary>
    /// Minimum room to the screen edge that is trusted as a limit.
    /// </summary>
    internal const float MinimumRoom = 32;

    /// <summary>
    /// Place the next entry, wrapping to a new row when it would not fit on the current one.
    /// An entry wider than the limit still gets a row of its own instead of being dropped.
    /// </summary>
    /// <param name="cursor">Layout state, advanced in place.</param>
    /// <param name="elementWidth">Entry width including spacing.</param>
    /// <param name="rowLimit">Maximum width of one row.</param>
    /// <returns>Distance from the row's start to the entry's near edge.</returns>
    public static float Place(ref DtrRowCursor cursor, float elementWidth, float rowLimit)
    {
        if (rowLimit > 0 && cursor.Used > 0 && cursor.Used + elementWidth > rowLimit)
        {
            cursor.Row++;
            cursor.Used = 0;
        }

        var offset = cursor.Used;
        cursor.Used += elementWidth;
        if (cursor.Row == 0)
            cursor.FirstRowUsed = cursor.Used;

        return offset;
    }

    /// <summary>
    /// Compute the row limit from the user's maximum width and the room left before the screen edge.
    /// </summary>
    /// <param name="configuredWidth">User maximum row width; 0 or less means "screen edge only".</param>
    /// <param name="roomToScreenEdge">Room between the first entry's start and the screen edge, in bar units.</param>
    /// <returns>The tighter positive limit, or positive infinity when neither applies.</returns>
    public static float RowLimit(float configuredWidth, float roomToScreenEdge)
    {
        var limit = float.PositiveInfinity;
        if (configuredWidth > 0 && float.IsFinite(configuredWidth))
            limit = configuredWidth;

        // Less than one icon of room would put every entry on its own row; treat as unknown instead.
        if (roomToScreenEdge >= MinimumRoom && float.IsFinite(roomToScreenEdge))
            limit = MathF.Min(limit, roomToScreenEdge);

        return limit;
    }

    /// <summary>
    /// Decide whether extra rows go above the bar instead of below it.
    /// </summary>
    /// <param name="barCenterY">Screen Y of the bar's vertical center.</param>
    /// <param name="screenHeight">Screen height; unknown (0 or NaN) means "below".</param>
    /// <returns>True when the bar sits in the lower half of the screen.</returns>
    public static bool WrapUpwards(float barCenterY, float screenHeight)
        => screenHeight > 0 && float.IsFinite(barCenterY) && barCenterY > screenHeight / 2;
}
