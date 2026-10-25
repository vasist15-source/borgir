using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Decoders;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ryujinx.Graphics.Shader.Translation
{
    /// <summary>
    /// RYUJINX_IR_DUMP=1: print every block's operations to stderr at the named points of the
    /// translation pipeline, so a chain of definitions that vanishes between the decoder and
    /// the code generator can be placed on the pass that dropped it.
    /// </summary>
    static class IrDump
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_IR_DUMP") == "1";

        public static void Dump(string label, BasicBlock[] blocks)
        {
            if (!Enabled) { return; }
            Dictionary<Operand, int> locals = new();
            StringBuilder sb = new();
            sb.Append("=== IR ").Append(label).Append(" (").Append(blocks.Length).Append(" blocks) ===\n");
            foreach (BasicBlock block in blocks)
            {
                sb.Append("block ").Append(block.Index).Append(":\n");
                foreach (INode node in block.Operations)
                {
                    sb.Append("  ");
                    if (node is Operation op)
                    {
                        sb.Append(op.Inst.ToString());
                        if (op.StorageKind != StorageKind.None) { sb.Append('[').Append(op.StorageKind).Append(']'); }
                        if (op.Index != 0) { sb.Append(".i").Append(op.Index); }
                    }
                    else if (node is PhiNode) { sb.Append("Phi"); }
                    else { sb.Append(node.GetType().Name); }
                    sb.Append(' ');
                    for (int d = 0; d < node.DestsCount; d++) { Operand dd = node.GetDest(d); sb.Append(Fmt(dd, locals)); if (dd != null && dd.Type == OperandType.LocalVariable) { sb.Append("(u").Append(dd.UseOps.Count).Append(')'); } sb.Append(' '); }
                    sb.Append("<- ");
                    for (int s = 0; s < node.SourcesCount; s++) { sb.Append(Fmt(node.GetSource(s), locals)).Append(' '); }
                    sb.Append('\n');
                }
            }
            Console.Error.Write(sb.ToString());
        }

        private static string Fmt(Operand o, Dictionary<Operand, int> locals)
        {
            if (o == null) { return "null"; }
            switch (o.Type)
            {
                case OperandType.Constant: return "#" + o.Value;
                case OperandType.LocalVariable:
                    if (!locals.TryGetValue(o, out int id)) { id = locals.Count; locals[o] = id; }
                    return "%" + id;
                case OperandType.Register:
                    Register r = o.GetRegister();
                    return (r.Type == RegisterType.Predicate ? "P" : r.Type == RegisterType.Gpr ? "R" : r.Type.ToString()[..1]) + r.Index;
                case OperandType.Label: return "L" + o.Value;
                default: return o.Type.ToString() + ":" + o.Value;
            }
        }
    }
}
