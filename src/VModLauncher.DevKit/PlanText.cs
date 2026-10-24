using System.Text;
using VModLauncher.Core.Install;

namespace VModLauncher.DevKit;

/// <summary>An install plan as text: the choices, every file and where it goes (and why), requirements and warnings.</summary>
public static class PlanText
{
    public static string Format(InstallPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== {plan.ModName}: {plan.Summary}");
        foreach (var v in plan.Variants)
            sb.AppendLine($"  choice {v.Id} \"{v.Title}\": {string.Join(", ", v.Options.Select(o => (o.Id == v.Selected ? "*" : "") + o.Label))}");
        foreach (var op in plan.Ops.OrderBy(o => o.Kind == OpKind.Skip).ThenBy(o => o.SourceRel))
        {
            var active = op.Kind != OpKind.Skip && op.Enabled && plan.IsVariantSelected(op.Variant);
            var mark = op.Kind == OpKind.Skip ? " -" : active ? " +" : " .";
            sb.AppendLine($"{mark} {op.Kind,-8} {op.SourceRel}{(op.Kind == OpKind.Skip ? "" : "  ->  " + op.Target)}   ({op.Reason}){(op.Variant != null ? " [" + op.Variant + "]" : "")}");
        }
        if (plan.Requires.Count > 0) sb.AppendLine("  requires: " + string.Join(", ", plan.Requires));
        foreach (var w in plan.Warnings) sb.AppendLine($"  {w.Level}: {w.Message}");
        if (plan.Readmes.Count > 0) sb.AppendLine("  readmes: " + string.Join(", ", plan.Readmes.Take(5)));
        return sb.ToString();
    }
}
