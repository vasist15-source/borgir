using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Text;
using static Ryujinx.Graphics.Shader.CodeGen.Msl.Instructions.InstGenHelper;
using static Ryujinx.Graphics.Shader.StructuredIr.InstructionInfo;

namespace Ryujinx.Graphics.Shader.CodeGen.Msl.Instructions
{
    static class InstGenMemory
    {
        private static readonly bool FetchOffsetEnabled = Environment.GetEnvironmentVariable("RYUJINX_MSL_FETCH_OFFSET") != "0";

        public static string GenerateLoadOrStore(CodeGenContext context, AstOperation operation, bool isStore)
        {
            StorageKind storageKind = operation.StorageKind;

            string varName;
            AggregateType varType;
            int srcIndex = 0;
            bool isStoreOrAtomic = operation.Inst == Instruction.Store || operation.Inst.IsAtomic();
            int inputsCount = isStoreOrAtomic ? operation.SourcesCount - 1 : operation.SourcesCount;
            bool fieldHasPadding = false;

            if (operation.Inst == Instruction.AtomicCompareAndSwap)
            {
                inputsCount--;
            }

            string fieldName = string.Empty;
            switch (storageKind)
            {
                case StorageKind.ConstantBuffer:
                case StorageKind.StorageBuffer:
                    if (operation.GetSource(srcIndex++) is not AstOperand bindingIndex || bindingIndex.Type != OperandType.Constant)
                    {
                        throw new InvalidOperationException($"First input of {operation.Inst} with {storageKind} storage must be a constant operand.");
                    }

                    int binding = bindingIndex.Value;
                    BufferDefinition buffer = storageKind == StorageKind.ConstantBuffer
                        ? context.Properties.ConstantBuffers[binding]
                        : context.Properties.StorageBuffers[binding];

                    if (operation.GetSource(srcIndex++) is not AstOperand fieldIndex || fieldIndex.Type != OperandType.Constant)
                    {
                        throw new InvalidOperationException($"Second input of {operation.Inst} with {storageKind} storage must be a constant operand.");
                    }

                    StructureField field = buffer.Type.Fields[fieldIndex.Value];

                    fieldHasPadding = buffer.Layout == BufferLayout.Std140
                                      && ((field.Type & AggregateType.Vector4) == 0)
                                      && ((field.Type & AggregateType.Array) != 0);

                    varName = storageKind == StorageKind.ConstantBuffer
                        ? "constant_buffers"
                        : "storage_buffers";
                    varName += "." + buffer.Name;
                    varName += "->" + field.Name;
                    varType = field.Type;
                    break;

                case StorageKind.LocalMemory:
                case StorageKind.SharedMemory:
                    if (operation.GetSource(srcIndex++) is not AstOperand { Type: OperandType.Constant } bindingId)
                    {
                        throw new InvalidOperationException($"First input of {operation.Inst} with {storageKind} storage must be a constant operand.");
                    }

                    MemoryDefinition memory = storageKind == StorageKind.LocalMemory
                        ? context.Properties.LocalMemories[bindingId.Value]
                        : context.Properties.SharedMemories[bindingId.Value];

                    varName = memory.Name;
                    varType = memory.Type;
                    break;

                case StorageKind.Input:
                case StorageKind.InputPerPatch:
                case StorageKind.Output:
                case StorageKind.OutputPerPatch:
                    if (operation.GetSource(srcIndex++) is not AstOperand varId || varId.Type != OperandType.Constant)
                    {
                        throw new InvalidOperationException($"First input of {operation.Inst} with {storageKind} storage must be a constant operand.");
                    }

                    IoVariable ioVariable = (IoVariable)varId.Value;
                    bool isOutput = storageKind.IsOutput();
                    bool isPerPatch = storageKind.IsPerPatch();
                    int location = -1;
                    int component = 0;

                    if (context.Definitions.HasPerLocationInputOrOutput(ioVariable, isOutput))
                    {
                        if (operation.GetSource(srcIndex++) is not AstOperand vecIndex || vecIndex.Type != OperandType.Constant)
                        {
                            throw new InvalidOperationException($"Second input of {operation.Inst} with {storageKind} storage must be a constant operand.");
                        }

                        location = vecIndex.Value;

                        if (operation.SourcesCount > srcIndex &&
                            operation.GetSource(srcIndex) is AstOperand elemIndex &&
                            elemIndex.Type == OperandType.Constant &&
                            context.Definitions.HasPerLocationInputOrOutputComponent(ioVariable, vecIndex.Value, elemIndex.Value, isOutput))
                        {
                            component = elemIndex.Value;
                            srcIndex++;
                        }
                    }

                    (varName, varType) = IoMap.GetMslBuiltIn(
                        context.Definitions,
                        ioVariable,
                        location,
                        component,
                        isOutput,
                        isPerPatch);
                    break;

                default:
                    throw new InvalidOperationException($"Invalid storage kind {storageKind}.");
            }

            for (; srcIndex < inputsCount; srcIndex++)
            {
                IAstNode src = operation.GetSource(srcIndex);

                if ((varType & AggregateType.ElementCountMask) != 0 &&
                    srcIndex == inputsCount - 1 &&
                    src is AstOperand elementIndex &&
                    elementIndex.Type == OperandType.Constant)
                {
                    varName += "." + "xyzw"[elementIndex.Value & 3];
                }
                else
                {
                    varName += $"[{GetSourceExpr(context, src, AggregateType.S32)}]";
                }
            }
            varName += fieldName;
            varName += fieldHasPadding ? ".x" : string.Empty;

            if (isStore)
            {
                varType &= AggregateType.ElementTypeMask;
                varName = $"{varName} = {GetSourceExpr(context, operation.GetSource(srcIndex), varType)}";
            }

            return varName;
        }

        public static string ImageLoadOrStore(CodeGenContext context, AstOperation operation)
        {
            AstTextureOperation texOp = (AstTextureOperation)operation;

            bool isArray = (texOp.Type & SamplerType.Array) != 0;

            StringBuilder texCallBuilder = new();

            int srcIndex = 0;

            string Src(AggregateType type)
            {
                return GetSourceExpr(context, texOp.GetSource(srcIndex++), type);
            }

            string imageName = GetImageName(context, texOp, ref srcIndex);
            texCallBuilder.Append(imageName);
            texCallBuilder.Append('.');

            if (texOp.Inst == Instruction.ImageAtomic)
            {
                texCallBuilder.Append((texOp.Flags & TextureFlags.AtomicMask) switch
                {
                    TextureFlags.Add => "atomic_fetch_add",
                    TextureFlags.Minimum => "atomic_min",
                    TextureFlags.Maximum => "atomic_max",
                    TextureFlags.Increment => "atomic_fetch_add",
                    TextureFlags.Decrement => "atomic_fetch_sub",
                    TextureFlags.BitwiseAnd => "atomic_fetch_and",
                    TextureFlags.BitwiseOr => "atomic_fetch_or",
                    TextureFlags.BitwiseXor => "atomic_fetch_xor",
                    TextureFlags.Swap => "atomic_exchange",
                    TextureFlags.CAS => "atomic_compare_exchange_weak",
                    _ => "atomic_fetch_add",
                });
            }
            else
            {
                texCallBuilder.Append(texOp.Inst == Instruction.ImageLoad ? "read" : "write");
            }

            texCallBuilder.Append('(');

            StringBuilder coordsBuilder = new();

            int coordsCount = texOp.Type.Dimensions;

            if (coordsCount > 1)
            {
                string[] elems = new string[coordsCount];

                for (int index = 0; index < coordsCount; index++)
                {
                    elems[index] = Src(AggregateType.S32);
                }

                coordsBuilder.Append($"uint{coordsCount}({string.Join(", ", elems)})");
            }
            else
            {
                coordsBuilder.Append($"uint({Src(AggregateType.S32)})");
            }

            if (isArray)
            {
                coordsBuilder.Append(", ");
                coordsBuilder.Append(Src(AggregateType.S32));
            }

            if (texOp.Inst == Instruction.ImageStore)
            {
                AggregateType type = texOp.Format.GetComponentType();

                string[] cElems = new string[4];

                for (int index = 0; index < 4; index++)
                {
                    if (srcIndex < texOp.SourcesCount)
                    {
                        cElems[index] = Src(type);
                    }
                    else
                    {
                        cElems[index] = type switch
                        {
                            AggregateType.S32 => NumberFormatter.FormatInt(0),
                            AggregateType.U32 => NumberFormatter.FormatUint(0),
                            _ => NumberFormatter.FormatFloat(0),
                        };
                    }
                }

                string prefix = type switch
                {
                    AggregateType.S32 => "int",
                    AggregateType.U32 => "uint",
                    AggregateType.FP32 => "float",
                    _ => string.Empty,
                };

                texCallBuilder.Append($"{prefix}4({string.Join(", ", cElems)})");
                texCallBuilder.Append(", ");
            }

            texCallBuilder.Append(coordsBuilder);

            if (texOp.Inst == Instruction.ImageAtomic)
            {
                texCallBuilder.Append(", ");

                AggregateType type = texOp.Format.GetComponentType();

                if ((texOp.Flags & TextureFlags.AtomicMask) == TextureFlags.CAS)
                {
                    texCallBuilder.Append(Src(type)); // Compare value.
                }

                string value = (texOp.Flags & TextureFlags.AtomicMask) switch
                {
                    TextureFlags.Increment => NumberFormatter.FormatInt(1, type), // TODO: Clamp value
                    TextureFlags.Decrement => NumberFormatter.FormatInt(-1, type), // TODO: Clamp value
                    _ => Src(type),
                };

                texCallBuilder.Append(value);
                // This doesn't match what the MSL spec document says so either
                // it is wrong or the MSL compiler has a bug.
                texCallBuilder.Append(")[0]");
            }
            else
            {
                texCallBuilder.Append(')');

                if (texOp.Inst == Instruction.ImageLoad)
                {
                    texCallBuilder.Append(GetMaskMultiDest(texOp.Index));
                }
            }

            return texCallBuilder.ToString();
        }

        public static string Load(CodeGenContext context, AstOperation operation)
        {
            return GenerateLoadOrStore(context, operation, isStore: false);
        }

        public static string Lod(CodeGenContext context, AstOperation operation)
        {
            AstTextureOperation texOp = (AstTextureOperation)operation;

            int coordsCount = texOp.Type.Dimensions;
            int coordsIndex = 0;

            string textureName = GetTextureName(context, texOp, ref coordsIndex);
            string samplerName = GetSamplerName(context, texOp, ref coordsIndex);

            string coordsExpr;

            if (coordsCount > 1)
            {
                string[] elems = new string[coordsCount];

                for (int index = 0; index < coordsCount; index++)
                {
                    elems[index] = GetSourceExpr(context, texOp.GetSource(coordsIndex + index), AggregateType.FP32);
                }

                coordsExpr = "float" + coordsCount + "(" + string.Join(", ", elems) + ")";
            }
            else
            {
                coordsExpr = GetSourceExpr(context, texOp.GetSource(coordsIndex), AggregateType.FP32);
            }

            string clamped = $"{textureName}.calculate_clamped_lod({samplerName}, {coordsExpr})";
            string unclamped = $"{textureName}.calculate_unclamped_lod({samplerName}, {coordsExpr})";

            return $"float2({clamped}, {unclamped}){GetMask(texOp.Index)}";
        }

        public static string Store(CodeGenContext context, AstOperation operation)
        {
            return GenerateLoadOrStore(context, operation, isStore: true);
        }

        public static string TextureSample(CodeGenContext context, AstOperation operation)
        {
            AstTextureOperation texOp = (AstTextureOperation)operation;

            bool isGather = (texOp.Flags & TextureFlags.Gather) != 0;
            bool hasDerivatives = (texOp.Flags & TextureFlags.Derivatives) != 0;
            bool intCoords = (texOp.Flags & TextureFlags.IntCoords) != 0;
            bool hasLodBias = (texOp.Flags & TextureFlags.LodBias) != 0;
            bool hasLodLevel = (texOp.Flags & TextureFlags.LodLevel) != 0;
            bool hasOffset = (texOp.Flags & TextureFlags.Offset) != 0;
            bool hasOffsets = (texOp.Flags & TextureFlags.Offsets) != 0;

            bool isArray = (texOp.Type & SamplerType.Array) != 0;
            bool isShadow = (texOp.Type & SamplerType.Shadow) != 0;

            StringBuilder texCallBuilder = new();

            bool colorIsVector = isGather || !isShadow;

            int srcIndex = 0;

            string Src(AggregateType type)
            {
                return GetSourceExpr(context, texOp.GetSource(srcIndex++), type);
            }

            string textureName = GetTextureName(context, texOp, ref srcIndex);
            string samplerName = GetSamplerName(context, texOp, ref srcIndex);

            texCallBuilder.Append(textureName);
            texCallBuilder.Append('.');

            if (intCoords)
            {
                texCallBuilder.Append("read(");
            }
            else
            {
                if (isGather)
                {
                    texCallBuilder.Append("gather");
                }
                else
                {
                    texCallBuilder.Append("sample");
                }

                if (isShadow)
                {
                    texCallBuilder.Append("_compare");
                }

                texCallBuilder.Append($"({samplerName}, ");
            }

            int coordsCount = texOp.Type.Dimensions;

            int pCount = coordsCount;

            bool appended = false;
            void Append(string str)
            {
                if (appended)
                {
                    texCallBuilder.Append(", ");
                }
                else
                {
                    appended = true;
                }

                texCallBuilder.Append(str);
            }

            AggregateType coordType = intCoords ? AggregateType.S32 : AggregateType.FP32;

            string AssemblePVector(int count)
            {
                if (count > 1)
                {
                    string[] elems = new string[count];

                    for (int index = 0; index < count; index++)
                    {
                        elems[index] = Src(coordType);
                    }

                    return string.Join(", ", elems);
                }

                return Src(coordType);
            }

            // Built here because the sources have to be consumed in order, but emitted
            // further down: a texel fetch carries its constant offset in a later source
            // and Metal's read() has no offset parameter, so the only place that offset
            // can go is inside the coordinate itself.
            string coords = AssemblePVector(pCount);
            string arrayIndex = isArray ? Src(AggregateType.S32) : null;
            string shadowCompare = isShadow ? Src(AggregateType.FP32) : null;

            string AssembleDerivativesVector(int count)
            {
                if (count > 1)
                {
                    string[] elems = new string[count];

                    for (int index = 0; index < count; index++)
                    {
                        elems[index] = Src(AggregateType.FP32);
                    }

                    return "float" + count + "(" + string.Join(", ", elems) + ")";
                }

                return Src(AggregateType.FP32);
            }

            string samplingOption = null;

            if (hasDerivatives)
            {
                string gradientType = (texOp.Type & SamplerType.Mask) switch
                {
                    SamplerType.Texture1D => "gradient1d",
                    SamplerType.Texture2D => "gradient2d",
                    SamplerType.Texture3D => "gradient3d",
                    SamplerType.TextureCube => "gradientcube",
                    _ => throw new InvalidOperationException($"Sampler type {texOp.Type} does not support gradients."),
                };

                string dPdx = AssembleDerivativesVector(coordsCount);
                string dPdy = AssembleDerivativesVector(coordsCount);

                samplingOption = $"{gradientType}({dPdx}, {dPdy})";
            }

            string lodLevel = null;

            if (hasLodLevel)
            {
                if (intCoords)
                {
                    lodLevel = Src(coordType);
                }
                else
                {
                    samplingOption = $"level({Src(coordType)})";
                }
            }

            string AssembleOffsetVector(int count)
            {
                if (count > 1)
                {
                    string[] elems = new string[count];

                    for (int index = 0; index < count; index++)
                    {
                        elems[index] = Src(AggregateType.S32);
                    }

                    return "int" + count + "(" + string.Join(", ", elems) + ")";
                }
                else
                {
                    return Src(AggregateType.S32);
                }
            }

            string offset = null;

            if (hasOffset)
            {
                offset = AssembleOffsetVector(coordsCount);
            }
            else if (hasOffsets)
            {
                // Consume all four source offsets even though Metal does not
                // expose the equivalent multi-offset gather overload here.
                for (int index = 0; index < 4; index++)
                {
                    AssembleOffsetVector(coordsCount);
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu, "Multiple offsets on gathers are not yet supported!");
            }

            // LodBias follows offsets in the IR source list, but Metal expects
            // the sampling option before the optional offset argument.
            if (hasLodBias)
            {
                samplingOption = $"bias({Src(AggregateType.FP32)})";
            }

            string vecSuffix = pCount > 1 ? pCount.ToString() : string.Empty;

            if (intCoords)
            {
                // read() has no offset parameter, so a constant offset has to be added to
                // the coordinate. Dropping it, which is what this did before, is silent
                // and severe: a neighbourhood filter degenerates into the same texel
                // fetched N times. The addition is signed, and only then converted, so a
                // negative offset near the left or top edge wraps the same way the other
                // backends' texelFetchOffset does rather than becoming a huge unsigned
                // coordinate.
                // RYUJINX_MSL_FETCH_OFFSET=0 restores the pre-7378 behaviour (offset dropped)
                // for A/B of the picture's brightness; pair it with RYUJINX_CODEGEN_SALT so the
                // two translations do not share one host cache.
                coords = offset != null && FetchOffsetEnabled
                    ? $"uint{vecSuffix}(int{vecSuffix}({coords}) + {offset})"
                    : $"uint{vecSuffix}({coords})";

                offset = null;
            }
            else
            {
                coords = $"float{vecSuffix}({coords})";
            }

            Append(coords);

            if (arrayIndex != null)
            {
                Append(arrayIndex);
            }

            if (shadowCompare != null)
            {
                Append(shadowCompare);
            }

            if (lodLevel != null)
            {
                Append(lodLevel);
            }

            if (samplingOption != null)
            {
                Append(samplingOption);
            }

            if (offset != null)
            {
                Append(offset);
            }

            texCallBuilder.Append(')');
            texCallBuilder.Append(colorIsVector ? GetMaskMultiDest(texOp.Index) : string.Empty);

            return texCallBuilder.ToString();
        }

        private static string GetTextureName(CodeGenContext context, AstTextureOperation texOp, ref int srcIndex)
        {
            TextureDefinition textureDefinition = context.Properties.Textures[texOp.GetTextureSetAndBinding()];
            string name = textureDefinition.Name;
            string setName = Declarations.GetNameForSet(textureDefinition.Set, true);

            if (textureDefinition.ArrayLength != 1)
            {
                name = $"{name}[{GetSourceExpr(context, texOp.GetSource(srcIndex++), AggregateType.S32)}]";
            }

            return $"{setName}.tex_{name}";
        }

        private static string GetSamplerName(CodeGenContext context, AstTextureOperation texOp, ref int srcIndex)
        {
            SetBindingPair index = texOp.IsSeparate ? texOp.GetSamplerSetAndBinding() : texOp.GetTextureSetAndBinding();
            int sourceIndex = texOp.IsSeparate ? srcIndex++ : srcIndex + 1;

            TextureDefinition samplerDefinition = context.Properties.Textures[index];
            string name = samplerDefinition.Name;
            string setName = Declarations.GetNameForSet(samplerDefinition.Set, true);

            if (samplerDefinition.ArrayLength != 1)
            {
                name = $"{name}[{GetSourceExpr(context, texOp.GetSource(sourceIndex), AggregateType.S32)}]";
            }

            return $"{setName}.samp_{name}";
        }

        private static string GetImageName(CodeGenContext context, AstTextureOperation texOp, ref int srcIndex)
        {
            TextureDefinition imageDefinition = context.Properties.Images[texOp.GetTextureSetAndBinding()];
            string name = imageDefinition.Name;
            string setName = Declarations.GetNameForSet(imageDefinition.Set, true);

            if (imageDefinition.ArrayLength != 1)
            {
                name = $"{name}[{GetSourceExpr(context, texOp.GetSource(srcIndex++), AggregateType.S32)}]";
            }

            return $"{setName}.{name}";
        }

        private static string GetMaskMultiDest(int mask)
        {
            if (mask == 0x0)
            {
                return string.Empty;
            }

            string swizzle = ".";

            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    swizzle += "xyzw"[i];
                }
            }

            return swizzle;
        }

        public static string TextureQuerySamples(CodeGenContext context, AstOperation operation)
        {
            AstTextureOperation texOp = (AstTextureOperation)operation;

            int srcIndex = 0;

            string textureName = GetTextureName(context, texOp, ref srcIndex);

            return $"{textureName}.get_num_samples()";
        }

        public static string TextureQuerySize(CodeGenContext context, AstOperation operation)
        {
            AstTextureOperation texOp = (AstTextureOperation)operation;

            StringBuilder texCallBuilder = new();

            int srcIndex = 0;

            string textureName = GetTextureName(context, texOp, ref srcIndex);
            texCallBuilder.Append(textureName);
            texCallBuilder.Append('.');

            if (texOp.Index == 3)
            {
                texCallBuilder.Append("get_num_mip_levels()");
            }
            else
            {
                context.Properties.Textures.TryGetValue(texOp.GetTextureSetAndBinding(), out TextureDefinition definition);
                bool hasLod = !definition.Type.HasFlag(SamplerType.Multisample) && (definition.Type & SamplerType.Mask) != SamplerType.TextureBuffer;
                bool isArray = definition.Type.HasFlag(SamplerType.Array);
                texCallBuilder.Append("get_");

                if (texOp.Index == 0)
                {
                    texCallBuilder.Append("width");
                }
                else if (texOp.Index == 1)
                {
                    texCallBuilder.Append("height");
                }
                else
                {
                    if (isArray)
                    {
                        texCallBuilder.Append("array_size");
                    }
                    else
                    {
                        texCallBuilder.Append("depth");
                    }
                }

                texCallBuilder.Append('(');

                if (hasLod && !isArray)
                {
                    IAstNode lod = operation.GetSource(0);
                    string lodExpr = GetSourceExpr(context, lod, GetSrcVarType(operation.Inst, 0));

                    texCallBuilder.Append(lodExpr);
                }

                texCallBuilder.Append(')');
            }

            return texCallBuilder.ToString();
        }

        public static string PackHalf2x16(CodeGenContext context, AstOperation operation)
        {
            IAstNode src0 = operation.GetSource(0);
            IAstNode src1 = operation.GetSource(1);

            string src0Expr = GetSourceExpr(context, src0, GetSrcVarType(operation.Inst, 0));
            string src1Expr = GetSourceExpr(context, src1, GetSrcVarType(operation.Inst, 1));

            return $"as_type<uint>(half2({src0Expr}, {src1Expr}))";
        }

        public static string UnpackHalf2x16(CodeGenContext context, AstOperation operation)
        {
            IAstNode src = operation.GetSource(0);

            string srcExpr = GetSourceExpr(context, src, GetSrcVarType(operation.Inst, 0));

            return $"float2(as_type<half2>({srcExpr})){GetMask(operation.Index)}";
        }

        private static string GetMask(int index)
        {
            return $".{"xy".AsSpan(index, 1)}";
        }
    }
}
