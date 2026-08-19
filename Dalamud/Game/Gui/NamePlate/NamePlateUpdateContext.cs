using Dalamud.Game.ClientState.Objects;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Dalamud.Game.Gui.NamePlate;

/// <summary>
/// Contains information related to the pending nameplate data update. This is only valid for a single frame and should
/// not be kept across frames.
/// </summary>
public interface INamePlateUpdateContext
{
    /// <summary>
    /// Gets the number of active nameplates. The actual number visible may be lower than this in cases where some
    /// nameplates are hidden by default (based on in-game "Display Name Settings" and so on).
    /// </summary>
    int ActiveNamePlateCount { get; }

    /// <summary>
    /// Gets a value indicating whether the game is currently performing a full update of all active nameplates.
    /// </summary>
    bool IsFullUpdate { get; }

    /// <summary>
    /// Gets the address of the NamePlate addon.
    /// </summary>
    nint AddonAddress { get; }

    /// <summary>
    /// Gets the address of the NamePlate addon's number array data container.
    /// </summary>
    nint NumberArrayDataAddress { get; }

    /// <summary>
    /// Gets the address of the NamePlate addon's string array data container.
    /// </summary>
    nint StringArrayDataAddress { get; }

    /// <summary>
    /// Gets the address of the first entry in the NamePlate addon's int array.
    /// </summary>
    nint NumberArrayDataEntryAddress { get; }
}

/// <summary>
/// Contains information related to the pending nameplate data update. This is only valid for a single frame and should
/// not be kept across frames.
/// </summary>
internal unsafe class NamePlateUpdateContext : INamePlateUpdateContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NamePlateUpdateContext"/> class.
    /// </summary>
    /// <param name="objectTable">An object table.</param>
    internal NamePlateUpdateContext(ObjectTable objectTable)
    {
        this.ObjectTable = objectTable;

        // Both of these are hand-written wrappers that walk Framework -> UIModule -> ... and return
        // null at each step if the previous one is null, so neither may be dereferenced blindly.
        // Store whatever we get (including null) and let ResetState refuse to produce a usable state
        // if either is missing; dereferencing null here would raise an AccessViolationException,
        // a corrupted-state exception that no try/catch can recover from.
        this.RaptureAtkModule = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule.Instance();

        var uiModule = UIModule.Instance();
        this.Ui3DModule = uiModule == null ? null : uiModule->GetUI3DModule();
    }

    /// <summary>
    /// Gets the number of active nameplates. The actual number visible may be lower than this in cases where some
    /// nameplates are hidden by default (based on in-game "Display Name Settings" and so on).
    /// </summary>
    public int ActiveNamePlateCount { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the game is currently performing a full update of all active nameplates.
    /// </summary>
    public bool IsFullUpdate { get; private set; }

    /// <summary>
    /// Gets the address of the NamePlate addon.
    /// </summary>
    public nint AddonAddress => (nint)this.Addon;

    /// <summary>
    /// Gets the address of the NamePlate addon's number array data container.
    /// </summary>
    public nint NumberArrayDataAddress => (nint)this.NumberData;

    /// <summary>
    /// Gets the address of the NamePlate addon's string array data container.
    /// </summary>
    public nint StringArrayDataAddress => (nint)this.StringData;

    /// <summary>
    /// Gets the address of the first entry in the NamePlate addon's int array.
    /// </summary>
    public nint NumberArrayDataEntryAddress => (nint)this.NumberStruct;

    /// <summary>
    /// Gets the RaptureAtkModule.
    /// </summary>
    internal RaptureAtkModule* RaptureAtkModule { get; }

    /// <summary>
    /// Gets the Ui3DModule.
    /// </summary>
    internal UI3DModule* Ui3DModule { get; }

    /// <summary>
    /// Gets the ObjectTable.
    /// </summary>
    internal ObjectTable ObjectTable { get; }

    /// <summary>
    /// Gets a pointer to the NamePlate addon.
    /// </summary>
    internal AddonNamePlate* Addon { get; private set; }

    /// <summary>
    /// Gets a pointer to the NamePlate addon's number array data container.
    /// </summary>
    internal NumberArrayData* NumberData { get; private set; }

    /// <summary>
    /// Gets a pointer to the NamePlate addon's string array data container.
    /// </summary>
    internal StringArrayData* StringData { get; private set; }

    /// <summary>
    /// Gets a pointer to the NamePlate addon's number array entries as a struct.
    /// </summary>
    internal NamePlateNumberArray* NumberStruct { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether any handler in the current context has instantiated a part builder.
    /// </summary>
    internal bool HasParts { get; set; }

    /// <summary>
    /// Resets the state of the context based on the provided addon lifecycle arguments.
    /// </summary>
    /// <param name="addon">A pointer to the addon.</param>
    /// <param name="numberArrayData">A pointer to the global number array data struct.</param>
    /// <param name="stringArrayData">A pointer to the global string array data struct.</param>
    public void ResetState(AtkUnitBase* addon, NumberArrayData** numberArrayData, StringArrayData** stringArrayData)
    {
        this.Addon = (AddonNamePlate*)addon;
        this.HasParts = false;
        this.NumberData = null;
        this.NumberStruct = null;
        this.StringData = null;
        this.ActiveNamePlateCount = 0;
        this.IsFullUpdate = false;

        // AtkStage.Instance() resolves a [StaticAddress(..., isPointer: true)] slot, so the static
        // address holds a pointer that is null until the stage has been created, and the array
        // getters may return null in their own right. RaptureAtkModule/Ui3DModule come from the
        // constructor and are null if the UI module did not exist when this context was built; the
        // update handlers dereference both, so treat a missing one as "nothing to update" here.
        // Leaving ActiveNamePlateCount at 0 makes NamePlateGui.OnNamePlateUpdate return before it
        // touches any handler, which is the same path it already takes when there are no nameplates.
        var stage = AtkStage.Instance();
        if (stage == null || this.RaptureAtkModule == null || this.Ui3DModule == null || addon == null)
            return;

        // GetNumberArrayData(NumberArrayType)/GetStringArrayData(StringArrayType) are just
        // GetNumberArrayData()[(int)type] and GetStringArrayData()[(int)type], i.e. they dereference
        // the table pointer before indexing it. Both table getters are [MemberFunction]s returning
        // T**, so the table itself can be null and that dereference is unguarded. Splitting the two
        // steps costs no extra call and removes it, matching NamePlateGui.RequestRedraw.
        var numberArrays = stage->GetNumberArrayData();
        var stringArrays = stage->GetStringArrayData();
        if (numberArrays == null || stringArrays == null)
            return;

        var numberData = numberArrays[(int)NumberArrayType.NamePlate];
        var stringData = stringArrays[(int)StringArrayType.NamePlate];
        if (numberData == null || stringData == null || numberData->IntArray == null)
            return;

        this.NumberData = numberData;
        this.NumberStruct = (NamePlateNumberArray*)numberData->IntArray;
        this.StringData = stringData;

        this.ActiveNamePlateCount = this.NumberStruct->ActiveNamePlateCount;
        this.IsFullUpdate = this.Addon->DoFullUpdate != 0;
    }
}
