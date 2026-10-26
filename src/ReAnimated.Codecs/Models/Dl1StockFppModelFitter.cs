using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Codecs.Models;

/// <summary>Optional authoring measurements; ordinary fitting derives them from the source anatomy.</summary>
public sealed record Dl1StockFppFitOptions
{
    public double? SourceUniformScaleOverride { get; init; }
    public Vector3D? SourceTranslationOverride { get; init; }
    public double? LeftPalmScaleOverride { get; init; }
    public double? RightPalmScaleOverride { get; init; }
    public bool HideHead { get; init; } = true;
}

/// <summary>Fits visible geometry around an unchanged native first-person player skeleton.</summary>
public static class Dl1StockFppModelFitter
{
    private static readonly string[] RequiredReferenceNames = ["bip01", "l_hand", "r_hand", "eyecamera", "refcamera"];
    public static Dl1RigConformanceApplyResult ApplyDetailed(FbxModelAuthoringImportResult model,
        RigConformanceResult fit, Dl1RigTemplate reference, CustomModelRigConformance? settings = null,
        Dl1StockFppFitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(fit); ArgumentNullException.ThrowIfNull(reference);
        options ??= new();
        RequireReference(reference);
        RigDefinition sourceRig = model.Rig ?? throw new InvalidOperationException("Import a skinned model before fitting a first-person rig.");
        CustomModelDocument sourceDocument = model.Package.Document;
        settings = (settings ?? sourceDocument.RigConformance ?? new()) with
        {
            FitStrategy = CustomModelRigFitStrategy.StockFppRegional,
            TemplateId = reference.TemplateId,
            TemplateProfileName = reference.ProfileName,
            TemplateSourceResourceName = reference.SourceResourceName,
            TemplateFingerprint = reference.SourceFingerprint,
            SourceFbxSha256 = sourceDocument.Source.ContentSha256,
        };
        var sourceBones = sourceDocument.CreateEffectiveBones();
        if (sourceBones.Length != sourceRig.BoneCount) throw new InvalidDataException("The source rig and editable hierarchy differ.");
        TransformMatrix[] sourceGlobals = Globals(sourceBones);
        int[] nativeToSource = ResolveMapping(sourceDocument, sourceBones, sourceGlobals, fit, reference, settings);
        var nativeByName = reference.Entities.ToDictionary(b => b.Name, b => b.Index, StringComparer.OrdinalIgnoreCase);
        var direct = Enumerable.Repeat(-1, sourceBones.Length).ToArray();
        for (int native = 0; native < nativeToSource.Length; native++)
            if (nativeToSource[native] >= 0) direct[nativeToSource[native]] = native;
        var foldedFrames = new Dictionary<int, int>();
        for (int source = 0; source < sourceDocument.Bones.Length; source++)
        {
            int parent = sourceBones[source].ParentIndex;
            if (direct[source] < 0 && parent >= 0 && direct[parent] >= 0 && sourceBones[source].Kind is BoneKind.Deform or BoneKind.Helper &&
                (sourceGlobals[source].Translation - sourceGlobals[parent].Translation).Length < 1e-8)
            { foldedFrames[source] = direct[parent]; direct[source] = direct[parent]; }
        }

        CustomModelPerspectiveSelection? visibility = options.HideHead ? FirstPersonSelection(model, direct, reference) : null;
        FbxModelAuthoringImportResult visible = model;
        var notes = ImmutableArray.CreateBuilder<string>();
        (double size, Vector3D translation) = SourceSizing(model, sourceBones, sourceGlobals, nativeToSource, reference, options);
        TransformMatrix normalization = TransformMatrix.CreateTranslation(translation) * TransformMatrix.CreateScale(new(size, size, size));
        var referenceRig = new RigDefinition("stock-fpp-reference", reference.SourceResourceName,
            reference.Entities.Select(b => new BoneDefinition(b.Index, b.Name, b.ParentIndex, b.LocalRestMatrix.Decompose(1e-4), b.Kind, true, null, b.SemanticRole)));
        var targets = reference.Entities.Select(b => nativeToSource[b.Index] >= 0
            ? (Vector3D?)normalization.TransformPoint(sourceGlobals[nativeToSource[b.Index]].Translation) : null).ToImmutableArray();
        if (reference.TryFindByName("bip01") is { } root && targets[root] is null && reference.TryFindByName("pelvis") is { } pelvis && targets[pelvis] is { } pelvisPoint)
            targets = targets.SetItem(root, pelvisPoint);
        RigRestPoseTransferResult sourceFrames = RigRestPoseTransfer.Solve(referenceRig,
            reference.Entities.Select(b => b.GlobalRestMatrix).ToImmutableArray(), targets, cancellationToken);
        var sourceCanonical = reference.Entities.ToDictionary(b => b.Name,
            b => sourceFrames.PosedGlobals[b.Index], StringComparer.OrdinalIgnoreCase);
        // Directly imported native frames retain their exact calibration; fitting a reference to itself
        // otherwise introduces unnecessary numerical rotations at single-child joints.
        foreach (Dl1RigTemplateEntity node in reference.Entities)
            if (nativeToSource[node.Index] is var source && source >= 0 &&
                sourceBones[source].Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase))
                sourceCanonical[node.Name] = Dl1StockFppRegionalTransforms.Rigid(normalization * sourceGlobals[source]);
        var available = reference.Entities.Where(b => nativeToSource[b.Index] >= 0).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nativeGlobals = reference.Entities.ToDictionary(b => b.Name, b => b.GlobalRestMatrix, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TransformMatrix> regional = Dl1StockFppRegionalTransforms.Solve(sourceCanonical, nativeGlobals, available, options, notes);

        var perSource = new TransformMatrix[sourceBones.Length];
        int Anchor(int source)
        {
            for (int current = source; current >= 0; current = sourceBones[current].ParentIndex)
                if (direct[current] >= 0) return direct[current];
            return reference.TryFindByName("pelvis") ?? 0;
        }
        for (int source = 0; source < sourceBones.Length; source++)
            perSource[source] = regional[reference[Anchor(source)].Name] * normalization;

        HashSet<int> weighted = WeightedBones(visible.Surfaces);
        var retainedSources = fit.Bones.Where(b => b.SourceBoneIndex >= 0 &&
                (b.TemplateIndex >= 0 || !settings.DropExtraBones))
            .Select(b => b.SourceBoneIndex).ToHashSet();
        var extra = new HashSet<int>();
        foreach (int source in weighted)
        {
            if (direct[source] >= 0) continue;
            int parent = sourceBones[source].ParentIndex;
            if (parent >= 0 && direct[parent] >= 0 &&
                (sourceGlobals[source].Translation - sourceGlobals[parent].Translation).Length < 1e-8)
            {
                foldedFrames[source] = direct[parent];
                continue;
            }
            for (int current = source; current >= 0 && direct[current] < 0; current = sourceBones[current].ParentIndex)
                if (retainedSources.Contains(current)) extra.Add(current);
        }
        // Retain explicit non-native attachments even if they have no skin weights.
        for (int source = sourceDocument.Bones.Length; source < sourceBones.Length; source++)
            if (direct[source] < 0)
                for (int current = source; current >= 0 && direct[current] < 0; current = sourceBones[current].ParentIndex)
                    if (current >= sourceDocument.Bones.Length || retainedSources.Contains(current)) extra.Add(current);

        var bones = ImmutableArray.CreateBuilder<CustomModelBone>();
        var outputGlobals = new List<TransformMatrix>();
        foreach (Dl1RigTemplateEntity node in reference.Entities)
        {
            int source = nativeToSource[node.Index];
            bones.Add(new()
            {
                Index = node.Index,
                Name = node.Name,
                ParentIndex = node.ParentIndex,
                Kind = node.Kind,
                IsWeighted = node.IsDeform,
                FbxObjectId = source >= 0 ? sourceBones[source].FbxObjectId : 0,
                ExactLocalBindMatrix = node.LocalRestMatrix,
                LocalBindTransform = node.LocalRestMatrix.Decompose(1e-4)
            });
            outputGlobals.Add(node.GlobalRestMatrix);
        }
        foreach (int source in extra.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int parentSource = sourceBones[source].ParentIndex;
            int parent = parentSource >= 0 && direct[parentSource] >= 0 ? direct[parentSource] : Anchor(source);
            string name = sourceBones[source].Name;
            if (nativeByName.ContainsKey(name)) throw new InvalidOperationException($"Retained source node '{name}' conflicts with the native reference. Review its bone match first.");
            TransformMatrix posed = perSource[source] * sourceGlobals[source];
            posed = Dl1StockFppRegionalTransforms.Rigid(posed);
            TransformMatrix local = outputGlobals[parent].InvertedAffine() * posed;
            direct[source] = bones.Count;
            bones.Add(sourceBones[source] with
            {
                Index = bones.Count,
                ParentIndex = parent,
                ExactLocalBindMatrix = local,
                LocalBindTransform = local.Decompose(1e-4),
                IsWeighted = weighted.Contains(source)
            });
            outputGlobals.Add(posed);
        }
        var identityMap = direct.Select((target, source) => foldedFrames.ContainsKey(source) ? -1 : target).ToImmutableArray();
        foreach ((int source, int target) in foldedFrames) direct[source] = target;
        var skinMap = direct.ToImmutableArray();
        var boneArray = bones.ToImmutable();
        var transforms = perSource.ToImmutableArray();
        var posedSources = sourceGlobals.Select((g, i) => Dl1StockFppRegionalTransforms.Rigid(perSource[i] * g)).ToImmutableArray();
        var transfer = new RigRestPoseTransferResult { PosedGlobals = posedSources, SkinningTransforms = transforms, MaximumJointResidual = 0 };
        Dl1RestPoseBakeResult baked = Dl1RestPoseBaker.Bake(visible.Surfaces, transfer, cancellationToken);
        Dl1SkinConformanceResult skin = Dl1SkinWeightConformer.ConformToHierarchy(baked.Surfaces, sourceRig, skinMap, boneArray.Length, cancellationToken);
        var bound = skin.Surfaces.Select(surface => surface with
        {
            InverseBindMatrices = surface.PaletteBoneIndices.Select(i => outputGlobals[i].InvertedAffine()).ToImmutableArray(),
        }).ToImmutableArray();
        var fitMap = fit.Bones.Select(b => b.TemplateIndex >= 0 ? b.TemplateIndex : b.SourceBoneIndex >= 0 ? direct[b.SourceBoneIndex] : -1).ToImmutableArray();
        var hierarchy = new Dl1ConformanceHierarchy(boneArray, [], fitMap, identityMap, outputGlobals.ToImmutableArray(), notes.ToImmutable());
        var doc = visible.Package.Document with
        {
            Bones = boneArray,
            AuthoredHelpers = [],
            RigConformance = settings ?? sourceDocument.RigConformance,
            LastBuildReceipt = null,
            FirstPersonVisibility = null,
            Camera = sourceDocument.Camera with { ActivePreviewNodeName = reference.Entities.First(b => b.Name.Equals("eyecamera", StringComparison.OrdinalIgnoreCase)).Name },
            RigSignature = CustomModelContractSignatures.ComputeRig(boneArray),
            BuildSettings = sourceDocument.BuildSettings with { ReferenceExistingAnimationLibrary = true, AnimationScriptAlias = "anims_player" },
        };
        var companion = Dl1CompanionReferenceTransfer.Apply(sourceDocument.SecondaryMotion, sourceBones, boneArray,
            skinMap, transforms, cancellationToken);
        doc = doc with { SecondaryMotion = companion.Definition };
        doc = Dl1ConformanceSessionTransfer.Apply(sourceDocument, doc, hierarchy, reference.TemplateId);
        if (doc.RiggingSession is null) doc = doc with { RiggingSession = RiggingSessions.Create(doc, RigStudioEntryPath.AdaptExistingRig) };
        doc = StandardChannels(doc, reference);
        doc = doc with { MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(doc.MorphChannels, bound) };
        var clips = FbxAnimationTrackReindexer.ReindexAvailable(visible.AnimationClips, identityMap, cancellationToken);
        doc = doc with
        {
            Diagnostics = doc.Diagnostics.Where(d => d.Code != "stock_fpp_fit").Append(new CustomModelImportDiagnostic
            {
                Code = "stock_fpp_fit",
                Severity = CustomModelImportSeverity.Information,
                Message = $"Stock first-person rig fitted at source size {size:G4}. " + string.Join(" ", notes)
            }).ToImmutableArray()
        };
        var candidate = visible with { Package = visible.Package with { Document = doc }, Rig = doc.CreateRigDefinition(), Surfaces = bound, AnimationClips = clips };
        doc = doc with
        {
            RigConformance = doc.RigConformance is { } record ? record with
            {
                AppliedOutputRigSignature = RigSignature.Compute(candidate.Rig!),
                StockFppNativeBoneCount = reference.EntityCount,
                StockFppNativeFrameSignature = Dl1StockFppRigValidation.FrameSignature(boneArray.Take(reference.EntityCount)),
            } : null
        };
        candidate = candidate with { Package = candidate.Package with { Document = doc } };
        if (visibility is not null)
        {
            doc = doc with { FirstPersonVisibility = visibility with { SourceGeometryFingerprint = FbxModelPerspectiveAuthoring.ComputeGeometryFingerprint(candidate) } };
            candidate = candidate with { Package = candidate.Package with { Document = doc } };
        }
        candidate = FbxAuthoredModelLayer.Capture(candidate, cancellationToken);
        doc = candidate.Package.Document;
        doc.Validate();
        FbxProfileEditGuard.RequireAllowed(model, candidate, cancellationToken);
        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(candidate, cancellationToken);
        Dl1StockFppRigValidation.RequirePrepared(doc, prepared.Contract);
        return new(candidate, fitMap, skinMap);
    }

    private static void RequireReference(Dl1RigTemplate reference)
    {
        if (!reference.ProfileName.Contains("fpp", StringComparison.OrdinalIgnoreCase) && !reference.SourceResourceName.EndsWith("_fpp", StringComparison.OrdinalIgnoreCase) ||
            RequiredReferenceNames.Any(n => reference.TryFindByName(n) is null))
            throw new InvalidOperationException("Select a first-person player reference for stock FPP fitting.");
    }

    /// <summary>Returns unambiguous authored and recognized source roles for the fitting review.</summary>
    public static ImmutableDictionary<string, string> ProposeSourceRoleOverrides(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (document.Bones.IsEmpty) return result.ToImmutable();
        RigDefinition rig = document.CreateRigDefinition();
        foreach (var group in rig.Bones.Where(b => b.SemanticRole is not null).GroupBy(b => b.SemanticRole!, StringComparer.Ordinal))
            if (group.Count() == 1) result[group.Key] = group.Single().Name;
        foreach (var group in rig.Bones.Select(b => (Bone: b, Role: SourceRole(b.Name))).Where(b => b.Role is not null).GroupBy(b => b.Role!, StringComparer.Ordinal))
            if (group.Count() == 1) result.TryAdd(group.Key, group.Single().Bone.Name);
        return result.ToImmutable();
    }

    private static int[] ResolveMapping(CustomModelDocument document, ImmutableArray<CustomModelBone> source, TransformMatrix[] globals,
        RigConformanceResult fit, Dl1RigTemplate reference, CustomModelRigConformance? settings)
    {
        var result = Enumerable.Repeat(-1, reference.EntityCount).ToArray();
        var used = new HashSet<int>();
        var explicitTargets = new HashSet<int>();
        var byName = source.ToDictionary(b => b.Name, b => b.Index, StringComparer.OrdinalIgnoreCase);
        var proposals = ProposeSourceRoleOverrides(document);
        string? NativeRole(Dl1RigTemplateEntity node) => node.SemanticRole ?? HumanoidBoneSemanticClassifier.Classify(node.Name)?.Role;
        void Assign(int target, int sourceIndex)
        {
            if (result[target] >= 0 || !used.Add(sourceIndex)) return;
            result[target] = sourceIndex;
        }
        // Reviewed choices win, then exact native names and recognized anatomy. Geometry guesses
        // only fill the remaining slots, so clothing chains cannot claim an identified arm or hand.
        foreach (CustomModelConformanceRoleOverride choice in (settings ?? document.RigConformance)?.RoleOverrides ?? [])
        {
            var targets = reference.Entities.Where(n => NativeRole(n) == choice.Role).ToArray();
            if (targets.Length == 1 && byName.TryGetValue(choice.SourceBoneName, out int sourceIndex))
            {
                Assign(targets[0].Index, sourceIndex);
                explicitTargets.Add(targets[0].Index);
            }
        }
        foreach (Dl1RigTemplateEntity node in reference.Entities)
            if (byName.TryGetValue(node.Name, out int sourceIndex)) Assign(node.Index, sourceIndex);
        foreach (Dl1RigTemplateEntity node in reference.Entities)
            if (NativeRole(node) is { } role && proposals.TryGetValue(role, out string? name) && byName.TryGetValue(name, out int sourceIndex))
                Assign(node.Index, sourceIndex);
        foreach (RigConformedBone node in fit.Bones)
            if (node.TemplateIndex >= 0 && node.TemplateIndex < result.Length && node.SourceBoneIndex >= 0 && node.SourceBoneIndex < source.Length)
                Assign(node.TemplateIndex, node.SourceBoneIndex);
        foreach (string side in new[] { "l", "r" })
        {
            if (reference.TryFindByName(side + "_hand") is not { } hand || result[hand] < 0) continue;
            int handSource = result[hand];
            foreach ((string alias, string target) in new[] { ("Wrist", "foretwist"), ("Ulna", "foretwist1") })
            {
                if (reference.TryFindByName(side + "_" + target) is not { } output || explicitTargets.Contains(output)) continue;
                int candidate = Enumerable.Range(0, source.Length).FirstOrDefault(i => !used.Contains(i) &&
                    source[i].Name.EndsWith("_" + (side == "l" ? "L" : "R") + "_" + alias, StringComparison.OrdinalIgnoreCase), -1);
                if (candidate < 0) continue;
                if (alias == "Wrist" && (globals[candidate].Translation - globals[handSource].Translation).Length > 1e-4) continue;
                if (result[output] >= 0) used.Remove(result[output]); result[output] = candidate; used.Add(candidate);
            }
        }
        return result;
    }
    private static string? SourceRole(string name)
    {
        if (HumanoidBoneSemanticClassifier.Classify(name)?.Role is { } classified) return classified;
        const string valvePrefix = "ValveBiped.Bip01_";
        int start = name.LastIndexOf(valvePrefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        string leaf = name[(start + valvePrefix.Length)..].ToLowerInvariant();
        string? core = leaf switch
        {
            "pelvis" => "body.pelvis",
            "spine" => "body.spine.0",
            "spine1" => "body.spine.1",
            "spine2" => "body.spine.2",
            "spine4" => "body.spine.3",
            "neck1" => "body.neck.0",
            "head1" => "body.head",
            _ => null
        };
        if (core is not null) return core;
        if (leaf.Length < 3 || leaf[1] != '_' || leaf[0] is not ('l' or 'r')) return null;
        string side = leaf[0] == 'l' ? "left" : "right"; string tail = leaf[2..];
        string? role = tail switch
        {
            "clavicle" => $"arm.{side}.clavicle",
            "upperarm" => $"arm.{side}.upper",
            "forearm" => $"arm.{side}.lower",
            "hand" => $"hand.{side}",
            "thigh" => $"leg.{side}.upper",
            "calf" => $"leg.{side}.lower",
            "foot" => $"foot.{side}",
            "toe0" => $"toe.{side}",
            _ => null
        };
        if (role is not null) return role;
        if (!tail.StartsWith("finger", StringComparison.Ordinal) || tail.Length is < 7 or > 8) return null;
        int digit = tail[6] - '0'; int joint = tail.Length == 7 ? 1 : tail[7] - '0' + 1;
        string? finger = digit switch { 0 => "thumb", 1 => "index", 2 => "middle", 3 => "ring", 4 => "little", _ => null };
        return finger is not null && joint is >= 1 and <= 3 ? $"finger.{side}.{finger}.{joint}" : null;
    }

    private static (double Scale, Vector3D Translation) SourceSizing(FbxModelAuthoringImportResult model,
        ImmutableArray<CustomModelBone> bones, TransformMatrix[] globals, int[] mapping, Dl1RigTemplate reference, Dl1StockFppFitOptions options)
    {
        bool fullBody = reference.TryFindByName("l_thigh") is { } leg && mapping[leg] >= 0;
        bool nativeFrames = reference.Entities.All(n => mapping[n.Index] >= 0 &&
            bones[mapping[n.Index]].ExactLocalBindMatrix.NearlyEquals(n.LocalRestMatrix, 5e-5) &&
            (bones[mapping[n.Index]].ParentIndex < 0 ? null : bones[bones[mapping[n.Index]].ParentIndex].Name) ==
            (n.ParentIndex < 0 ? null : reference[n.ParentIndex].Name));
        if (nativeFrames && options.SourceUniformScaleOverride is null && options.SourceTranslationOverride is null)
            return (1, Vector3D.Zero);
        double ground = model.Surfaces.SelectMany(s => s.Vertices).Select(v => v.Position.Y).DefaultIfEmpty(0).Min();
        Vector3D[] eyes = Enumerable.Range(0, bones.Length).Where(i => bones[i].Name.Contains("eye", StringComparison.OrdinalIgnoreCase) &&
            !bones[i].Name.Contains("brow", StringComparison.OrdinalIgnoreCase) && !bones[i].Name.Contains("lid", StringComparison.OrdinalIgnoreCase) &&
            bones[i].Kind != BoneKind.Camera).Select(i => globals[i].Translation).ToArray();
        double sourceTop = eyes.Length > 0 ? eyes.Average(p => p.Y) : reference.TryFindByName("head") is { } head && mapping[head] >= 0 ? globals[mapping[head]].Translation.Y : 0;
        double targetTop = reference.Entities.First(b => b.Name.Equals("eyecamera", StringComparison.OrdinalIgnoreCase)).GlobalRestMatrix.Translation.Y;
        double scale = options.SourceUniformScaleOverride ?? (fullBody && sourceTop - ground > 1e-6 ? targetTop / (sourceTop - ground) : 1);
        if (!double.IsFinite(scale) || scale <= 0) throw new InvalidOperationException("Source size must be a positive finite scale.");
        Vector3D translation = options.SourceTranslationOverride ?? new Vector3D(0, fullBody ? -ground * scale : 0, 0);
        if (!translation.IsFinite) throw new InvalidOperationException("Source placement must be finite.");
        return (scale, translation);
    }

    private static CustomModelPerspectiveSelection FirstPersonSelection(FbxModelAuthoringImportResult model, int[] direct, Dl1RigTemplate reference)
    {
        CustomModelPerspectiveSelection selection = model.Package.Document.FirstPersonVisibility ?? FbxModelPerspectiveAuthoring.ProposeFirstPerson(model).Selection;
        if (reference.TryFindByName("head") is { } head)
        {
            var bones = model.Package.Document.CreateEffectiveBones();
            var branch = new HashSet<int>();
            foreach (CustomModelBone bone in bones)
                if (direct[bone.Index] == head || bone.ParentIndex >= 0 && branch.Contains(bone.ParentIndex)) branch.Add(bone.Index);
            var hidden = selection.HiddenTriangles.ToBuilder(); var whole = selection.HiddenSurfaces.ToBuilder();
            bool IsHead(FbxModelSurface surface, int vertex)
            {
                var v = surface.Vertices[vertex]; double total = 0, mass = 0;
                for (int k = 0; k < v.BoneIndices.Length; k++) if (v.BoneWeights[k] > 0)
                { total += v.BoneWeights[k]; if (branch.Contains(surface.PaletteBoneIndices[v.BoneIndices[k]])) mass += v.BoneWeights[k]; }
                return total > 0 && mass / total >= .95;
            }
            foreach (FbxModelSurface surface in model.Surfaces)
            {
                string component = surface.SourceGeometry?.Id ?? surface.Id;
                if (surface.Vertices.Length > 0 && Enumerable.Range(0, surface.Vertices.Length).All(i => IsHead(surface, i)))
                    whole.Add(new(component, surface.Id));
                else if (!surface.SourceTriangles.IsDefaultOrEmpty)
                    for (int triangle = 0; triangle < surface.SourceTriangles.Length; triangle++)
                        if (Enumerable.Range(0, 3).All(k => IsHead(surface, (int)surface.Indices[triangle * 3 + k])))
                            hidden.Add(new(component, surface.SourceTriangles[triangle]));
            }
            selection = selection with { HiddenTriangles = hidden.Distinct().ToImmutableArray(), HiddenSurfaces = whole.Distinct().ToImmutableArray() };
        }
        return selection;
    }

    private static HashSet<int> WeightedBones(ImmutableArray<FbxModelSurface> surfaces)
    {
        var result = new HashSet<int>();
        foreach (FbxModelSurface surface in surfaces) foreach (FbxModelVertex vertex in surface.Vertices)
            for (int k = 0; k < vertex.BoneIndices.Length; k++) if (vertex.BoneWeights[k] > 0) result.Add(surface.PaletteBoneIndices[vertex.BoneIndices[k]]);
        return result;
    }

    private static TransformMatrix[] Globals(ImmutableArray<CustomModelBone> bones)
    {
        var result = new TransformMatrix[bones.Length]; foreach (CustomModelBone bone in bones) result[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : result[bone.ParentIndex] * bone.ExactLocalBindMatrix; return result;
    }

    private static CustomModelDocument StandardChannels(CustomModelDocument doc, Dl1RigTemplate reference)
    {
        RiggingSession session = doc.RiggingSession!;
        var observed = RiggingSessions.ObserveSourceHierarchy(doc);
        var byName = doc.Bones.ToDictionary(b => b.Name, b => observed[b.Index].EntityId, StringComparer.OrdinalIgnoreCase);
        var canonical = reference.Entities.Select(n => byName[n.Name]).ToHashSet();
        foreach (HelperRecipe helper in session.Recipe.Helpers.Where(h => canonical.Contains(h.EntityId)))
            if (helper.LockedFields != RigHelperEditFields.None)
                throw new InvalidOperationException("A native first-person helper is locked. Unlock its transform before selecting the stock FPP rig.");
        var evidence = new RigEvidenceReference
        {
            Id = "stock-fpp-native-channels",
            Kind = RigEvidenceKind.ProfileRule,
            ArtifactSha256 = reference.SourceFingerprint,
            Description = "Stock first-person rig uses reference frames and standard native animation channels."
        };
        var clip = new RigChannelOwnership { Owners = [RigComponentOwner.Clip], Evidence = [evidence] };
        var bind = new RigChannelOwnership { Owners = [RigComponentOwner.BindInherited], Evidence = [evidence] };
        var policies = doc.Bones.Select(b =>
        {
            bool stock = reference.TryFindByName(b.Name) is not null; bool root = stock && b.ParentIndex < 0;
            return new AnimationComponentPolicy
            {
                EntityId = byName[b.Name],
                Position = stock ? clip : bind,
                Rotation = stock ? clip : bind,
                Scale = root ? clip : bind,
                EmittedMask = !stock ? RigAnimationComponents.None : root ? RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale : RigAnimationComponents.Position | RigAnimationComponents.Rotation,
                AnimationLod = RigAnimationLod.Off,
                LodRuleId = "stock-fpp-native-channels",
                LodEvidence = [evidence]
            };
        }).ToImmutableArray();
        var ids = observed.Select(o => o.EntityId).ToHashSet();
        var replacement = session with
        {
            Recipe = session.Recipe with
            {
                Helpers = session.Recipe.Helpers.Where(h => !canonical.Contains(h.EntityId)).ToImmutableArray(),
                FramePolicies = session.Recipe.FramePolicies.Where(p => !canonical.Contains(p.EntityId)).ToImmutableArray(),
                ComponentPolicies = session.Recipe.ComponentPolicies.Where(p => !ids.Contains(p.EntityId)).Concat(policies).ToImmutableArray(),
                Assignments = session.Recipe.Assignments.Where(a => !canonical.Contains(a.EntityId)).Concat(reference.Entities.Where(e => e.SemanticRole is not null).Select(e => new RigRoleAssignment(e.SemanticRole!, byName[e.Name]))).ToImmutableArray(),
            }
        };
        replacement = RiggingSessions.Change(session, replacement, RiggingEditKind.Anatomy);
        return doc with { RiggingSession = replacement };
    }
}
