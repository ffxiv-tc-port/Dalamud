using System.Runtime.CompilerServices;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Internal.Asserts;

/// <summary>
/// The depths of the ImGui stacks that a plugin's draw has to leave as it found them, read from the current context.
/// </summary>
/// <remarks>
/// Read through the binding's <see cref="ImGuiContext"/> and <see cref="ImGuiWindow"/> fields, whose offsets match
/// the cimgui 1.88 docking build Dalamud ships (derivation in the commit message). Only the current context and its
/// current window are dereferenced, while the frame that owns them runs; their addresses are kept only to be
/// compared with another capture.
/// </remarks>
internal readonly struct ImGuiStackDepths
{
    /// <summary>How many depths are captured.</summary>
    public const int Count = 12;

    /// <summary>Index of <c>CurrentWindowStack.Size</c>.</summary>
    public const int Window = 0;

    /// <summary>Index of <c>BeginPopupStack.Size</c>.</summary>
    public const int Popup = 1;

    /// <summary>Index of <c>GroupStack.Size</c>.</summary>
    public const int Group = 2;

    /// <summary>Index of the current window's <c>IDStack.Size</c>.</summary>
    public const int Id = 3;

    /// <summary>Index of <c>CurrentTabBarStack.Size</c>.</summary>
    public const int TabBar = 4;

    /// <summary>Index of <c>TablesTempDataStacked</c>.</summary>
    public const int Table = 5;

    /// <summary>Index of <c>ClipperTempDataStacked</c>.</summary>
    public const int Clipper = 6;

    /// <summary>Index of <c>DisabledStackSize</c>.</summary>
    public const int Disabled = 7;

    /// <summary>Index of <c>ItemFlagsStack.Size</c>.</summary>
    public const int ItemFlags = 8;

    /// <summary>Index of <c>ColorStack.Size</c>.</summary>
    public const int Color = 9;

    /// <summary>Index of <c>StyleVarStack.Size</c>.</summary>
    public const int StyleVar = 10;

    /// <summary>Index of <c>FontStack.Size</c>.</summary>
    public const int Font = 11;

    private static readonly string[] NameTable =
        ["Window", "Popup", "Group", "Id", "TabBar", "Table", "Clipper", "Disabled", "ItemFlags", "Color", "StyleVar", "Font"];

    private readonly Buffer depths;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImGuiStackDepths"/> struct.
    /// </summary>
    /// <param name="context">Address of the context the depths were read from; 0 when there was none.</param>
    /// <param name="window">Address of that context's current window; 0 when there was none.</param>
    /// <param name="depths">The depths, <see cref="Count"/> of them, by the index constants of this type.</param>
    public ImGuiStackDepths(nint context, nint window, ReadOnlySpan<int> depths)
    {
        this.Context = context;
        this.CurrentWindow = window;
        depths[..Count].CopyTo(this.depths);
    }

    /// <summary>Gets the names of the depths, by index, as they appear in the report.</summary>
    public static ReadOnlySpan<string> Names => NameTable;

    /// <summary>Gets the address of the context the depths were read from, or 0. Never dereferenced.</summary>
    public nint Context { get; }

    /// <summary>Gets the address of the context's current window, or 0. Never dereferenced.</summary>
    public nint CurrentWindow { get; }

    /// <summary>Gets one depth.</summary>
    /// <param name="index">One of the index constants of this type.</param>
    public int this[int index] => this.depths[index];

    /// <summary>
    /// Reads the depths of the current ImGui context. Allocates nothing; call it on the thread that draws.
    /// </summary>
    /// <returns>The depths, or a capture with <see cref="Context"/> 0 when there is no usable context.</returns>
    public static unsafe ImGuiStackDepths Capture()
    {
        var g = ImGui.GetCurrentContext().Handle;
        if (!IsUserAddress(g))
            return default;

        var window = g->CurrentWindow;
        var hasWindow = IsUserAddress(window);
        Span<int> d = stackalloc int[Count];
        d[Window] = g->CurrentWindowStack.Size;
        d[Popup] = g->BeginPopupStack.Size;
        d[Group] = g->GroupStack.Size;
        d[Id] = hasWindow ? window->IDStack.Size : 0;
        d[TabBar] = g->CurrentTabBarStack.Size;
        d[Table] = g->TablesTempDataStacked;
        d[Clipper] = g->ClipperTempDataStacked;
        d[Disabled] = g->DisabledStackSize;
        d[ItemFlags] = g->ItemFlagsStack.Size;
        d[Color] = g->ColorStack.Size;
        d[StyleVar] = g->StyleVarStack.Size;
        d[Font] = g->FontStack.Size;
        return new ImGuiStackDepths((nint)g, hasWindow ? (nint)window : 0, d);
    }

    // Upper bound too: a non-canonical address would fault on read, and nothing could catch that.
    private static unsafe bool IsUserAddress(void* p) => (nuint)p >= 0x10000 && (nuint)p <= 0x00007FFFFFFFFFFF;

    [InlineArray(Count)]
    private struct Buffer
    {
        private int first;
    }
}
