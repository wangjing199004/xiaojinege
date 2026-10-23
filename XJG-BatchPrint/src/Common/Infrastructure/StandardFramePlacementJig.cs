#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>使用CAD原生拖拽预览，图框中心跟随捕捉点，原块基点不变。</summary>
internal sealed class StandardFramePlacementJig : EntityJig
{
    private readonly BlockReference _frame;
    private readonly Transaction _transaction;
    private Point3d _point;
    private Point3d _center;
    private readonly string? _paper;
    internal bool SwitchRequested { get; private set; }
    internal Point3d CursorPoint => _point;

    internal StandardFramePlacementJig(BlockReference frame, Transaction transaction, string? paper = null) : base(frame)
    {
        _frame = frame;
        _transaction = transaction;
        _paper = paper;
        _point = StandardFrameInserter.GetCenter(frame);
        _center = _point;
    }

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        var options = new JigPromptPointOptions(_paper == null
            ? "\n移动鼠标对位并指定图框中心，单击确认，Esc取消: "
            : "\n空白 HP " + _paper + "目录：指定目录中心 [切换(S)]（右键或空格切换A3/A4，单击确认，Esc取消）: ")
        {
            UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.GovernedByOrthoMode
        };
        if (_paper != null)
        {
            options.UserInputControls |= UserInputControls.NullResponseAccepted;
            options.Keywords.Add("Switch", "S", "切换");
            options.Keywords.Default = "Switch";
        }
        var result = prompts.AcquirePoint(options);
        if (_paper != null && (result.Status == PromptStatus.None || result.Status == PromptStatus.Keyword))
        {
            SwitchRequested = true;
            return SamplerStatus.Cancel;
        }
        if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
        if (result.Value.IsEqualTo(_point)) return SamplerStatus.NoChange;
        _point = result.Value;
        return SamplerStatus.OK;
    }

    protected override bool Update()
    {
        StandardFrameInserter.MoveBy(_frame, _transaction, _point - _center);
        _center = _point;
        return true;
    }
}
